/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/29 22:15:7
    Description:     对话小窗口
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
	public class TalkItem :MonoBehaviour
	{
        public Text Name;
        public Text Talking;
        public Image head;
        /// <summary>
        /// 说话对象初始化
        /// </summary>
        /// <param name="name"></param>
        /// <param name="talking"></param>
        public void Init(string name, string talking)
        {
            Name.text = name;
            Talking.text = talking;
        }
        public void Init(string message)
        {
            Talking.text = message;
        }
        /// <summary>
        /// 未来接入换头像功能
        /// </summary>
        /// <param name="name"></param>
        /// <param name="talking"></param>
        /// <param name="image"></param>
        public void Init(string name,string talking,Image image)
        {
            Name.text = name;
            Talking.text = talking;
            head = image;
        }
	}
}