/****************************************************
    Author:            龙之介
    CreatTime:    2022/5/7 16:56:1
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using Google.Protobuf.Collections;
using SocketProto;

namespace Manger
{
	public class HYLDBaoShiZhengBaManger :BattleManger
	{
        private CreatGemLogic creatGemLogic;
        
        public override void Init()
        {
            base.Init();
            Transform transforms = GameObject.Find("HYLDGameTatal").transform.Find("GemCreater").transform;//
            creatGemLogic=transforms.GetComponentInChildren<CreatGemLogic>();
            creatGemLogic.InitData();
        }

        protected override void OnBattleLogicTick(int frameid)
        {
            if (creatGemLogic != null)
            {
                creatGemLogic.OnLogicUpdate();
            }
        }
        //Vector3 pos;
    }
}