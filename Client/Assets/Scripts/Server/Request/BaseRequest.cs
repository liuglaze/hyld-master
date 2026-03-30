/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/23 22:17:37
    Description:     请求基类
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using MVC;

namespace Server
{
    public class BaseRequest
    {
        public RequestCode requestCode
        {
            get;
            private set;
        }
        public ActionCode actionCode
        {
            get;
            private set;
        }
        protected List<MainPack> mainPackList=new List<MainPack>();
        private int packlen = 0;
        private const int MAX_PACK = 10;
        private UIbasePanel panel;

        public BaseRequest(UIbasePanel panel, RequestCode requestCode = RequestCode.RequestNone, ActionCode actionCode = ActionCode.ActionNone)
        {
            this.panel = panel;
            this.requestCode = requestCode;
            this.actionCode = actionCode;
            RequestManger.AddRequest(this);
        }
        public ActionCode GetActionCode
        {
            get { return actionCode; }
        }
         public void Update()
        {
            //9.处理消息队列

            //初步处理提升效率
            if (packlen == 0) return;

            //重复处理
            for (int i = 0; i < MAX_PACK; i++)
            {
                //获取第一条消息
                MainPack mainPack = null;
                lock (mainPackList)
                {
                    if (mainPackList.Count > 0)
                    {
                        mainPack = mainPackList[0];
                        mainPackList.RemoveAt(0);
                        packlen--;
                    }
                }

                //分发消息
                if (mainPack != null)
                {
                    //Logging.HYLDDebug.LogError(mainPack);
                    Logging.HYLDDebug.Trace("OnResponse  : \n" + mainPack);
                    panel.OnResponse(mainPack);
                }
                else
                {
                    break;//无消息了
                }
            }

        }


        public virtual void OnDestroy()
        {
            RequestManger.RemoveRequest(actionCode);
        }

        public virtual void OnResponse(MainPack pack)
        {
            // 8.根据RequestCode处加入到消息队列（线程安全）
            lock (mainPackList)
            {
                mainPackList.Add(pack);
                packlen++;
            }
        }
        public virtual void SendRequest(MainPack pack)
        {
            //5.发送消息
            HYLDManger.Instance.Send(pack);
        }

    }
}