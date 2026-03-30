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
                    //0.5.将玩家信息同步过去
                    //打开UIStartPanel
                    HYLDManger.Instance.UIBaseManger.Open(nameof(UIStartPanel));
                    PlayerPrefs.SetString("UserName", user.text);
                    PlayerPrefs.SetString("PassWord", pass.text);
                    HYLDStaticValue.UseName = user.text;
                    HYLDStaticValue.PassWord = pass.text;
                    Logging.HYLDDebug.Log("登陆成功");
                    Logging.HYLDDebug.Trace($"Username: {user.text}  PassWord: {pass.text} 登陆成功");
                    Logging.HYLDDebug.Log($"TraceSavePath={Logging.HYLDDebug.TraceSavePath}");
                    break;
                case ReturnCode.Fail:
                    //HYLDManger.Instance.UIBaseManger.Open(nameof(UILoginPanel));
                    HYLDManger.Instance.ShowMessage("密码或用户名错误或此账号被人登陆");
                    Logging.HYLDDebug.Log("登陆失败");
                    Logging.HYLDDebug.Trace($"{user} {pass} 登陆失败");
                    break;
                default:
                    Logging.HYLDDebug.Log("Def");
                    break;
            }
            //Logging.HYLDDebug.LogError(pack.Returncode.ToString());
        }
    }
}