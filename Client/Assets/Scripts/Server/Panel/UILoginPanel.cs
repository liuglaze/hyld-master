/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/24 13:5:55
    Description:     UI登陆界面
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using Server;
using SocketProto;

namespace MVC
{
    public class UILoginPanel : UIbasePanel
    {
        public InputField user, pass;
        public Button loginBtn, swichBtn;
        public override void Init()
        {
            base.Init();
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.User, SocketProto.ActionCode.Login));
        }
        public override void OnRecovery()
        {
            base.OnRecovery();
            user.text = PlayerPrefs.GetString("UserName", "");
            pass.text = PlayerPrefs.GetString("PassWord", "");
        }
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            loginBtn.onClick.AddListener(OnLoginClick);
            swichBtn.onClick.AddListener(OnSwitchLogonClick);
        }
        private void OnLoginClick()
        {
            //0.1.点击登陆按钮发送登陆请求
            if (user.text == "" || pass.text == "")
            {
                Logging.HYLDDebug.LogError("密码用户名不得空");
                return;
            }
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0].requestCode;
            pack.Actioncode = Requests[0].actionCode;
            LoginPack loginPack = new LoginPack();
            loginPack.Username = user.text;
            loginPack.Password = pass.text;
            pack.Loginpack = loginPack;
            Requests[0].SendRequest(pack);
        }
        private void OnSwitchLogonClick()
        {
            Logging.HYLDDebug.Log("Logon");
            HYLDManger.Instance.UIBaseManger.Open(nameof(UILogonPanel));
            // UIManger.PushPanel(UIPanelType.Logon);
        }
        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    // 0.5.将玩家信息同步过去
                    // 打开UIStartPanel
                    //
                    // T-LOOP1：这一段是**普通成功登录**的本地身份赋值，它必须**先**执行、
                    // 而且无论登录响应是否附带「上局已终局」只读结果都执行。
                    // 修前的版本把 PMDS-END1 分支放在这段之前并直接 return，
                    // 结果是「Login 成功但 UseName/PassWord/PlayerPrefs 从未初始化」的隐性半登录。
                    HYLDManger.Instance.UIBaseManger.Open(nameof(UIStartPanel));
                    PlayerPrefs.SetString("UserName", user.text);
                    PlayerPrefs.SetString("PassWord", pass.text);
                    HYLDStaticValue.UseName = user.text;
                    HYLDStaticValue.PassWord = pass.text;
                    Logging.HYLDDebug.Log("登陆成功");
                    // 注意：日志不得记录密码（凭据不进日志），只记账号名。
                    Logging.HYLDDebug.Trace($"Username: {user.text} 登陆成功");
                    Logging.HYLDDebug.Log($"TraceSavePath={Logging.HYLDDebug.TraceSavePath}");

                    // T-LOOP1：上局已终局的**只读**结果（无秘密）：只显示胜负并留在大厅，
                    // 不创建 DS 会话、不恢复输入、也不冒充本机的「DS 实机终局」快照。
                    TryShowEndedMatchResultReadOnly(pack.Str);
                    break;
                case ReturnCode.Fail:
                    //HYLDManger.Instance.UIBaseManger.Open(nameof(UILoginPanel));
                    HYLDManger.Instance.ShowMessage("密码或用户名错误或此账号被人登陆");
                    Logging.HYLDDebug.Log("登陆失败");
                    // 只记账号名：失败分支同样不得写密码字段。
                    Logging.HYLDDebug.Trace($"{user.text} 登陆失败");
                    break;
                default:
                    Logging.HYLDDebug.Log("Def");
                    break;
            }
            //Logging.HYLDDebug.LogError(pack.Returncode.ToString());
        }

        /// <summary>
        /// T-LOOP1：把登录响应里的「上局已终局」**只读**通知显示出来，并留在大厅。
        ///
        /// 严格性全部在两端共享的 <c>PMNet.Session.PMDsEndedNoticeCodec</c> 里：只有
        /// 「完整 PMDS-END1: 前缀 + 恰好三段 + 规范 Base64 + 合法 UTF-8 + 非空 matchId + 非负 team」
        /// 的文本才会被接受。它刻意**不**做源码级的字符串判断，因为那只能匹配前缀，
        /// 无法拒非规范 Base64/非法 UTF-8/负 team/尾部字段，也无法提供可执行反例。
        ///
        /// 边界：
        ///   · 非该通知（普通登录）什么都不做；
        ///   · 像但**畸形**的通知一律不展示胜负（错误通知不冒充可信结果），也不创建任何 DS 会话；
        ///   · 结论来自**大厅**已核验结果（进程内缓存，客户端重启后依然存在），
        ///     因此不读、也不得冒充本机 <c>PMClientSessionHost</c> 的 DS 实机结果快照；
        ///   · 只显示而不写任何本地战斗状态，更不会恢复输入/进战斗（入口就是大厅面板）。
        /// </summary>
        private void TryShowEndedMatchResultReadOnly(string text)
        {
            if (!PMNet.Session.PMDsEndedNoticeCodec.HasPrefix(text))
            {
                return;
            }

            PMNet.Session.PMDsEndedNotice notice;
            string error;
            if (!PMNet.Session.PMDsEndedNoticeCodec.TryDecode(text, out notice, out error))
            {
                // 畸形通知：明确拒绝，不显示任何胜负，也不把它当成「已结束」或可信结果。
                Logging.HYLDDebug.LogError("[TLOOP1] 登录响应携带的 PMDS-END1 已结束对局通知非法，已拒绝展示（"
                    + error + "）");
                return;
            }

            Logging.HYLDDebug.Log("[TLOOP1] 收到 PMDS-END1 已结束对局只读结果：只显示胜负并留在大厅，"
                + "不创建 DS 会话、不恢复输入（" + notice + "）");
            HYLDManger.Instance.ShowMessage(PMNet.Session.PMDsEndedNoticeCodec.BuildReadOnlyMessage(notice));
        }
    }
}