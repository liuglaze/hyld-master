/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/18 15:50:55
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using Server;
using Google.Protobuf.Collections;


namespace MVC
{
	public class UIMatchingPanel : UIbasePanel
    {
        public Transform Star;
        public GameObject ExitMathcing;
        public override void Init()
        {
            base.Init();
           // Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.AddMatchingPlayer));
            Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.StartEnterBattle));
        }
        private void FixedUpdate()
        {
            Star.Rotate(new Vector3(0, 0, 1), 1f);
        }
      
        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
            if (pack.Returncode == ReturnCode.Succeed)            
            {
                //1.8更新FightData
                ExitMathcing.SetActive(false);
                //初始化比赛数据，异步加载战斗场景
                Logging.HYLDDebug.LogError("初始化比赛数据，异步加载战斗场景    " + pack);
                Manger.BattleData.Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUserInfo);
                Manger.ClearSenceManger.LoadScene(SceneConfig.battleScene);
            }
        }
        //private void 
    }
}