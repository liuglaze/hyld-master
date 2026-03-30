/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/8 21:8:50
    Description:     活跃好友列表
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
	public class InvateFriendItem :MonoBehaviour
	{
        [HideInInspector]
        public int Id
        {
            get;private set;
        }

        public Button btnInvateFriendToRoom;
        public Text friendname;

        public void Init(string name,Action<int> invate,int id)
        {
            Id = id;
            btnInvateFriendToRoom.onClick.AddListener(()=> { invate.Invoke(id); });
            friendname.text = name;
        }
        public void Init(string name, Action<string> invate, int id)
        {
            btnInvateFriendToRoom.onClick.AddListener(() => { invate.Invoke(friendname.text); });
            friendname.text = name;
        }
    }
}