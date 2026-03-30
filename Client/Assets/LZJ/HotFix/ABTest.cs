/****************************************************
    Author:            龙之介
    CreatTime:    2021/4/15 18:5:23
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using XLua;


namespace HOTFIX
{

    [Hotfix]
    public class ABTest : MonoBehaviour
    {
        public HotFixScript hotFixScript;

        private void OnEnable()
        {
         
        }

        private void Start()
        {
            Invoke("Init", 2);
        }
        private void Init()
        {
            
            //HotFixScript.LoadResource("Player", "gameobject/player.ab");
            //Logging.HYLDDebug.Log(HotFixScript.GetGameObject("Player"));
            Logging.HYLDDebug.Log("ABTest 热更前");
        }
        
    }
}