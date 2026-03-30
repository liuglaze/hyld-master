/****************************************************
    Author:            龙之介
    CreatTime:    #CreateTime#
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;


namespace LongZhiJie
{
	public class RequestManger :BaseManger
	{
        public RequestManger(GameFace face):base(face) { }

        private Dictionary<ActionCode, BaseRequest> _requestDic = new Dictionary<ActionCode, BaseRequest>();
        public void AddRequest(BaseRequest request)
        {
            _requestDic.Add(request.GetActionCode, request);
            //Debug.LogError(request);
        }
        public void RemoveRequest(ActionCode action)
        {
            _requestDic.Remove(action);
        }

        public void HandleResquest(MainPack pack)
        {
            if (_requestDic.TryGetValue(pack.Actioncode, out BaseRequest request))
            {
                request.OnResponse(pack);
                Debug.LogError("response  "+pack);
            }
            else
            {
                Debug.LogError("不能找到对应的处理");
            }
        }
	}
}