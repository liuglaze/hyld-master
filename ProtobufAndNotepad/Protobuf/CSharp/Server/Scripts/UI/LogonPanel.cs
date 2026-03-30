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
	public class LogonPanel :BasePanel
	{
        public  LogonRequest logonRequest;
        public InputField user, pass;
        public Button logonBtn,closeBtn;

        private void Start()
        {
            logonBtn.onClick.AddListener(OnLogonClick);
            closeBtn.onClick.AddListener(OnCloseClick);
        }
        private void OnLogonClick()
        {
            if (user.text == "" || pass.text == "")
            {
                Debug.LogError("密码用户名不得空");
                return;
            }
            logonRequest.SendRequest(user.text,pass.text);
        }
        private void OnCloseClick()
        {
            UIManger.PopPanel();
        }
        public override void OnEnter()
        {
            base.OnEnter();
            gameObject.SetActive(true);
        }
        public override void OnExit()
        {
            base.OnExit();
            gameObject.SetActive(false);
        }
        public override void OnRecovery()
        {
            base.OnRecovery();
            gameObject.SetActive(true);
        }
        public override void OnPause()
        {
            base.OnPause();
            gameObject.SetActive(false);
        }
        public override void OnResponse(MainPack pack)
        {
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    PlayerPrefs.SetString("UserName", user.text);
                    PlayerPrefs.SetString("PassWord", pass.text);
                    UIManger.ShowMessage("注册成功");
                    UIManger.PopPanel();
           

                    //UIManger.PushPanel(UIPanelType.RoomList);
                    //Debug.Log("注册成功");
                    break;
                case ReturnCode.Fail:
                    UIManger.ShowMessage("注册失败");
                    //Debug.Log("注册失败");
                    break;
                default:
                    Debug.Log("Def");
                    break;
            }
            Debug.LogError(pack.Returncode.ToString());
        }
    }
}