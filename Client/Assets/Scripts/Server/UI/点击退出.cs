/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/12 0:4:41
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
	public class 点击退出 :MonoBehaviour
	{
        public void Start()
        {
            gameObject.GetComponent<Button>().onClick.AddListener(() => { transform.parent.gameObject.SetActive(false); });
        }

    }
}