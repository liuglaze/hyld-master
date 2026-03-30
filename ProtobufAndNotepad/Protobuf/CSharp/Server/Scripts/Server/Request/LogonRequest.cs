/****************************************************
    Author:            龙之介
    CreatTime:    #CreateTime#
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;

namespace LongZhiJie
{
	public class LogonRequest :BaseRequest
	{
        public LogonPanel LogonPanel;
        private MainPack mainPack;
        public override void Awake()
        {
            requestCode = SocketProto.RequestCode.User;
            actionCode = SocketProto.ActionCode.Logon;
            base.Awake();
        }
        private void Update()
        {
            if (mainPack != null)
            {
                LogonPanel.OnResponse(mainPack);
                mainPack = null;
            }
        }
        public override void OnResponse(MainPack pack)
        {
            mainPack=pack;
        }
        public void SendRequest(string user,string pass)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = requestCode;
            pack.Actioncode = actionCode;
            LoginPack loginPack = new LoginPack();
            loginPack.Username = user;
            loginPack.Password = pass;
            pack.Loginpack = loginPack;
            base.SendRequest(pack);
        }
    }
}