/****************************************************
    ScriptName:        Test.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/15 14:3:53
    Description:     
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using ZYKTool;
using DG.Tweening;
using System.Linq;
using ZYKTool.Timer;


namespace ZYKTest
{


    public class ZYKTimerTest : MonoBehaviour
    {

        int tid;

        private void Start()
        {
            //ZYKTimerSystem timerSystem = GetComponent<ZYKTimerSystem>();
        }

        public void ClickAddBtn()
        {
            Logging.HYLDDebug.Log("Add Time Task");
            tid = ZYKTimerSystemTool.Instance.ZYKTimerAddTimerTask(FuncA, 2, 1,PETimeUnit.Second);
            //tid=ZYKTimerSystem.Single.AddTimerTask(FuncA, 500, 0);
        }

        public void ClickDelectBtn()
        {
            bool res = ZYKTimerSystemTool.Instance.ZYKTimerDelectTimeTask(tid);
            //bool res = ZYKTimerSystem.Single.DelectTimeTask(tid);
            Logging.HYLDDebug.Log("Delect Time Task:" + res);
        }

        void FuncA()
        {
            Logging.HYLDDebug.Log("Delay Log");
        }

        public void ClickReplaceBtn()
        {
            //bool res = ZYKTimerSystem.Single.ReplaceTimeTask(tid, FuncB, 2000);
            bool res = ZYKTimerSystemTool.Instance.ZYKTimerReplaceTimeTask(tid, FuncB, 2000,1,PETimeUnit.Second);
            Logging.HYLDDebug.Log("Replace Time Task:" + res);
        }

        void FuncB()
        {
            Logging.HYLDDebug.Log("Replace Log");
        }
    }
}