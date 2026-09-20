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
            // 分配请求 ID 并登记待确认（计划 P1 / B3）。
            // 0 保留表示「非请求」，因此这里必须拿到非 0 值。
            pack.RequestId = PmRpcClient.NextRequestId();
            PmRpcClient.Track(this, pack);

            HYLDManger.Instance.Send(pack);
        }

        /// <summary>
        /// 请求超时回调（超时且不再重发时触发）。
        /// 默认只记日志；需要给出 UI 反馈的面板可以重写。
        /// </summary>
        public virtual void OnRequestTimeout(ActionCode code, int requestId)
        {
            Logging.HYLDDebug.LogError($"[RPC][超时] panel={GetType().Name} action={code} requestId={requestId}");
        }

    }
}