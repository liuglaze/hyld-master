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
	public class BaseRequest :MonoBehaviour
	{
        protected RequestCode requestCode;
        protected ActionCode actionCode;
        protected GameFace face;

        public ActionCode GetActionCode
        {
            get { return actionCode; }
        }
        public virtual void Awake()
        {
            face = GameFace.Face;

        }
        public virtual void Start()
        {
            //Debug.LogError("添加" + actionCode.ToString());
            face.AddRequest(this);

        }
        public virtual void OnDestroy()
        {
            face.RemoveRequest(actionCode);
        }

        public virtual void OnResponse(MainPack pack)
        {
            Debug.LogError("   Response  ");
        }
        public virtual void SendRequest(MainPack pack)
        {
            face.Send(pack);
        }
    }
}