/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/4 20:25:48
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
	public class UIMessagePanel :UIbasePanel
	{
        public Text _text;
        public Image image;
        string msg = null;
        public override void Init()
        {
            base.Init();
            image.CrossFadeAlpha(0, 0.1f, false);
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
        public void ShowMessage(string str, bool issync = false)
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
            image.CrossFadeAlpha(1, 0.1f, false);
            _text.text = str;
            _text.CrossFadeAlpha(1, 0.1f, false);
            Invoke("HideText", 1);
        }
        private void HideText()
        {
            image.CrossFadeAlpha(0, 1f, false);
            _text.CrossFadeAlpha(0, 1f, false);
        }
    }
}