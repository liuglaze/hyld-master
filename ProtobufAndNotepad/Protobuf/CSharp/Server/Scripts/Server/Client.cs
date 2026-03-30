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
using System.Net;
using System.Net.Sockets;
using System.Text;



namespace LongZhiJie
{
	public class Client :MonoBehaviour
	{
        private Socket socket;
        private byte[] buffer = new byte[1024];
        private void Start()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect("127.0.0.1", 6666);//连接完成
            StartReceive();
            Send();
        }

        void StartReceive()
        {
            socket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, ReceiveCallBack, null);
        }
        void ReceiveCallBack(IAsyncResult iasyncResult)
        {
            int lenth = socket.EndReceive(iasyncResult);
            if (lenth == 0)
            {
                return;
            }
            string str = Encoding.UTF8.GetString(buffer,0,lenth);
            Debug.LogError(str);
            StartReceive();
        }

        void Send()
        {
            socket.Send(Encoding.UTF8.GetBytes("起飞"));
        }
    }
}