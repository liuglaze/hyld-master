/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/3 17:51:16
    Description:     添加好友请求item
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
	public class AplyAddFriendItem :MonoBehaviour
	{
        public Text txtUsername;
        public Button btnaccept;
        public Button btnReject;
        public Text txtScore;

        public void Init(string name, Action<string> accpet,Action<string> reject)
        {
            txtUsername.text = name;
            btnaccept.onClick.AddListener(()=> { accpet.Invoke(txtUsername.text);Destroy(gameObject); });
            btnReject.onClick.AddListener(() =>{ reject.Invoke(txtUsername.text); Destroy(gameObject); });
        }
	}
}