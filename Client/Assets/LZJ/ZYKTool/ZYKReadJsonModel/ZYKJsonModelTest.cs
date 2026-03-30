/****************************************************
    ScriptName:        ZYKJsonModelToolTest.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/21 13:41:46
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ZYKTool;
using ZYKTool.Model;


namespace ZYKTest
{
	public class ZYKJsonModelTest : MonoBehaviour
	{
        public void GetPlayerSpeed()
        {
           //Logging.HYLDDebug.LogError(ZYKJsonModelToolBase.Single.ZYKModenGetPlayerSpeed());
        }

        public void GetJsonVideoData()
        {
            List<ModelJsonVideoData> jsonVideoData = new List<ModelJsonVideoData>();
            jsonVideoData =ZYKJsonModelToolBase.Single.ZYKSingleModenGetJsonVideoData();
            foreach(ModelJsonVideoData jsonVideoData1 in jsonVideoData)
            {
                Logging.HYLDDebug.LogError(jsonVideoData1.id);
                Logging.HYLDDebug.LogError(jsonVideoData1.name);
                Logging.HYLDDebug.LogError(jsonVideoData1.type);
            }
        }

    }
}