/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/23 21:3:13
    Description:   和服务器进行对接的请求管理
               使用Dic<ActionCode, BaseRequest>处理对应事件
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;


namespace Server
{
	public class RequestManger
	{
        private static Dictionary<ActionCode, BaseRequest> _requestDic = new Dictionary<ActionCode, BaseRequest>();
        
        public static void AddRequest(BaseRequest request)
        {
            _requestDic.Add(request.GetActionCode, request);
        }
        public static void RemoveAllRequest()
        {
            _requestDic.Clear();
        }
        public static void RemoveRequest(ActionCode action)
        {
            _requestDic.Remove(action);
        }
        /// <summary>
        /// 根据Action获取Request
        /// </summary>
        /// <param name="pack"></param>
        public static void HandleRequest(MainPack pack)
        {
            //8.根据RequestCode处加入到消息队列

            if (pack.Requestcode == RequestCode.PingPong)
            {
                HYLDManger.Instance.Pong(pack);
                return;
            }
            else if (pack.Actioncode == ActionCode.BattleReview)
            {
                HYLDManger.Instance.AddBattleReview(pack);
                return;
            }
            if (pack.Actioncode == ActionCode.ActionNone) return;
            if (_requestDic.TryGetValue(pack.Actioncode, out BaseRequest request))
            {
                request.OnResponse(pack);
            }
            else
            {
                Logging.HYLDDebug.LogError(pack.ToString());
                Logging.HYLDDebug.LogError("不能找到对应的处理   "+pack.Actioncode + "   " + pack.Requestcode);
            }
        }

    }
}