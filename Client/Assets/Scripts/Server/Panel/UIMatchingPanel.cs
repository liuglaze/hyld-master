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

            // R3-B（契约 §7.1 / §7.4）：入局通知的识别必须**最先**做，并且
            // 一旦识别到 PMDS1 前缀就不允许再走旧链：
            //   解码失败 ⇒ 明确报错返回（绝不回退 BattleData / ClearSence，否则会生成第二套入局状态）；
            //   解码成功 ⇒ 交新链客户端宿主，直接 return。
            // 注意只打 offer 的 ToString()（只含公开字段与票据长度），**不打印完整 Str**。
            if (pack.Returncode == ReturnCode.Succeed && PMNet.Session.PMDsEntryCodec.HasPrefix(pack.Str))
            {
                PMNet.Session.PMDsEntryOffer offer;
                string decodeError;
                if (!PMNet.Session.PMDsEntryCodec.TryDecode(pack.Str, out offer, out decodeError))
                {
                    Logging.HYLDDebug.LogError("[R3B] 新链入局通知解码失败，拒绝回退旧链：" + decodeError);
                    return;
                }

                Logging.HYLDDebug.Log("[R3B] 收到新链入局通知：" + offer);
                PMNet.Unity.PMClientSessionHost.Enter(offer);
                return;
            }

            if (pack.Returncode == ReturnCode.Succeed)            
            {
                //1.8更新FightData
                ExitMathcing.SetActive(false);
                //初始化比赛数据，异步加载战斗场景
                Logging.HYLDDebug.LogError("初始化比赛数据，异步加载战斗场景    " + pack);
                Manger.BattleData.Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUsers);
                Manger.ClearSenceManger.LoadScene(SceneConfig.battleScene);
            }
        }
        //private void 
    }
}
