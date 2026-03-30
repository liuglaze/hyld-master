/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/12 20:56:32
    Description:     Nothing
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
    public class PingPongManger 
    {
        public static bool isUserPing = true;
        public static int pingInterval = 180;
        float lastPingInterval = 0;
        float lastPongInterval = 0;
        public void Init()
        {
            lastPingInterval = Time.time;
            lastPongInterval = Time.time;
        }
        public void OnResponse(float time)
        {
            Logging.HYLDDebug.LogError("Pong");
            lastPongInterval = time;
        }
        public void Excute()
        {
            if (!isUserPing)
            {
                return;
            }
            //发送Ping
            if (Time.time - lastPingInterval > pingInterval)
            {
                MainPack pack = new MainPack();
                pack.Actioncode = ActionCode.Ping;
                pack.Requestcode = RequestCode.PingPong;
                pack.Str = "P";
                HYLDManger.Instance.Send(pack);
                lastPingInterval = Time.time;
            }
            //检测Pong
            if (Time.time - lastPongInterval > pingInterval * 4)
            {
                HYLDManger.Instance.CloseClient();
            }
        }
    }


}
