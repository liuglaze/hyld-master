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
	public class MessagePanel :BasePanel
	{
        public Text _text;
        string msg = null;
        public override void OnEnter()
        {
            base.OnEnter();
            UIManger.SetMeessagePanel(this);
            _text.CrossFadeAlpha(0, 0.1f, false);
        }
        private void Update()
        {
            if (msg != null)
            {
                ShowText(msg);
                msg = null; 
            }
        }
        public void ShowMessage(string str,bool issync=false)
        {
            if (issync)
            {
                //异步
                msg = str;
            }
            else
            {
                ShowText(str);
            }
        }
        private void ShowText(string str)
        {
            _text.text = str;
            _text.CrossFadeAlpha(1, 0.1f, false);
            Invoke("HideText", 1);
        }
        private void HideText()
        {
            _text.CrossFadeAlpha(0, 1f, false);
        }
    }
}