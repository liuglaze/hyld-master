/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/24 15:55:38
    Description:     登陆面板
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using Server;

namespace MVC
{
	public class UILogonPanel :UIbasePanel
	{
        public InputField user, pass;
        public Button loginBtn;
        public override void Init()
        {
            base.Init();
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.User, SocketProto.ActionCode.Logon));
        }

        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            loginBtn.onClick.AddListener(OnLogonClick);
        }
        private void OnLogonClick()
        {
            //1.1点击注册按钮处理注册请求
            if (user.text == "" || pass.text == "")
            {
                Logging.HYLDDebug.LogError("密码用户名不得空");
                return;
            }
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0] .requestCode;
            pack.Actioncode = Requests[0].actionCode;
            LoginPack loginPack = new LoginPack();
            loginPack.Username = user.text;
            loginPack.Password = pass.text;
            pack.Loginpack = loginPack;
            Requests[0].SendRequest(pack);
        }

        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    //1.5记录用户信息
                    //关闭注册页面
                    PlayerPrefs.SetString("UserName", user.text);
                    PlayerPrefs.SetString("PassWord", pass.text);
                    user.text = "";
                    pass.text = "";
                    //UIManger.ShowMessage("注册成功");
                    //UIManger.PopPanel();
                    print("注册成功");
                    HYLDManger.Instance.UIBaseManger.Close();
                    //Logging.HYLDDebug.Log("注册成功");
                    break;
                case ReturnCode.Fail:
                    //UIManger.ShowMessage("注册失败");
                    HYLDManger.Instance.ShowMessage("账号已存在或者账号密码格式不对");
                    print("注册失败");
                    break;
                default:
                    print("Def");
                    break;
            }
        }


    }
}