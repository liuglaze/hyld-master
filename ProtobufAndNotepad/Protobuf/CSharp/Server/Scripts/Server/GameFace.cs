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
	public class GameFace :MonoBehaviour
	{
        private ClientManger _clientManger;
        private RequestManger _requestManger;
        private UIManger _uIManger;
        private static GameFace _face;
        public static GameFace Face 
        {
            get
            {
                if (_face == null)
                {
                    _face = GameObject.Find("GameFace").GetComponent<GameFace>();
                }
                return _face;
            }

        }

        private void Awake()
        {
            _uIManger = new UIManger(this);
            _clientManger = new ClientManger(this);

            _requestManger = new RequestManger(this);

            _uIManger.OnInit();
            _clientManger.OnInit();
            _requestManger.OnInit();

        }

        
        private void OnDestroy()
        {
            _clientManger.OnDestroy();
            _requestManger.OnDestroy();
        }

        public void Send(MainPack pack)
        {
            _clientManger.Send(pack);
        }

        public void HandleRequest(MainPack pack)
        {
            //处理
            _requestManger.HandleResquest(pack);
        }

        public void AddRequest(BaseRequest request)
        {
            _requestManger.AddRequest(request);
        }
        public void RemoveRequest(ActionCode actionCode)
        {
            _requestManger.RemoveRequest(actionCode);
        }

        public void ShowMessage(string str,bool sync=false)
        {
            _uIManger.ShowMessage(str,sync);
        }
    }
}