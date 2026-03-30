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
using System.Net;
using System.Net.Sockets;

namespace LongZhiJie
{
	public class StartPanel :BasePanel
	{
        public Button startBtn;
        private void Start()
        {
            startBtn.onClick.AddListener(OnStartButtonClick);
        }
        private void OnStartButtonClick()
        {
            UIManger.PushPanel(UIPanelType.Login);
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
    }
}