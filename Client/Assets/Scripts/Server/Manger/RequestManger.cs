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
            // 待确认表也一并清空：连接/面板重建后旧请求的回包不会再到达。
            PmRpcClient.ClearAll();
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
            // 先做 request_id 配对，必须落在下面所有早退分支之前。
            // 服务端对「已处理但不回包」的请求（例如 Chat）会补一个 ActionNone 的空 ack，
            // 而 ActionNone 会被本方法直接忽略；若把清账放在那之后，这类请求会一直重试。
            PmRpcClient.OnResponseReceived(pack);

            //8.根据RequestCode处加入到消息队列

            if (pack.Requestcode == RequestCode.PingPong)
            {
                HYLDManger.Instance.Pong(pack);
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
