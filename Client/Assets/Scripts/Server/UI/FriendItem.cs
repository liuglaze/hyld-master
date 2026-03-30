/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/2 11:47:32
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
	public class FriendItem :MonoBehaviour
	{
        /*
        public int id;
 
        public string username;
        public Sprite head;
        */
        public Text scoretext;
        public Text idtext;
        public Text userText;
        public Image head;

        public void Init(int id, string name)
        {
            idtext.text = id.ToString();
            userText.text = name;
        }
	}
}