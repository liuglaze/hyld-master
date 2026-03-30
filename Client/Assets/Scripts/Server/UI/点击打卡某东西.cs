/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/12 0:5:3
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace LongZhiJie
{
	public class 点击打卡某东西 :MonoBehaviour
	{
        public GameObject game;
        private void Start()
        {
            gameObject.GetComponent<Button>().onClick.AddListener(() => { game.SetActive(true); });
        }

    }
}