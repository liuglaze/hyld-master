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
using System.Net;
using System.Net.Sockets;

namespace LongZhiJie
{
	public class BasePanel :MonoBehaviour
	{
        protected UIManger UIManger;
        public void SetUIManger(UIManger uIManger)
        {
            UIManger = uIManger;
        }
        /// <summary>
        /// 进入UI
        /// </summary>
        public virtual void OnEnter()
        {
            
        }
        /// <summary>
        /// 暂停UI
        /// </summary>
        public virtual void OnPause()
        {
            
        }
        /// <summary>
        /// 恢复UI
        /// </summary>
        public virtual void OnRecovery()
        {
            
        }
        /// <summary>
        /// 退出UI
        /// </summary>
        public virtual void OnExit()
        {
            
        }
        /// <summary>
        /// 处理请求回调
        /// </summary>
        /// <param name="pack"></param>
        public virtual void OnResponse(MainPack pack)
        {
            
        }
    }
}