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
using System.Net.Sockets;
using SocketProto;
using System.Net;

namespace LongZhiJie
{
    public class ClientManger : BaseManger
    {
        private Socket _socket;
        private Message _message;
        private const string ip = "127.0.0.1";
        public ClientManger(GameFace face) : base(face) { }
        public override void OnInit()
        {
            base.OnInit();
            _message = new Message();
            InitSocket();
        }
        public override void OnDestroy()
        {
            base.OnDestroy();
            _message = null;
            CloseSocket();
        }


        /// <summary>
        /// 初始化Socket
        /// </summary>
        private void InitSocket()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                _socket.Connect(ip, 6666);
                //_socket.Connect(new EndPoint(ip,6666))
                Debug.Log("连接成功");
                StartReceive();
            
                //连接成功
            }
            catch (Exception e)
            {
                //连接失败
                //Debug.LogError(e);
                Debug.LogError("连接失败");
                _face.ShowMessage("连接失败");
            }
        }
        /// <summary>
        /// 关闭Socket
        /// </summary>
        private void CloseSocket()
        {
            if (_socket != null && _socket.Connected)
            {
                _socket.Close();
            }
        }
        private void StartReceive()
        { 
            Debug.Log("开始接收  client:" + _socket.LocalEndPoint + "  ---  sever:" + _socket.RemoteEndPoint);
            _socket.BeginReceive(_message.Buffer, _message.StartIndex, _message.Remsize, SocketFlags.None, ReceiveCallBack,null);
        }
        private void ReceiveCallBack(IAsyncResult iar)
        {
            try
            {
                if (_socket == null || _socket.Connected == false) return;
                int len = _socket.EndReceive(iar);
                Debug.Log("接收成功");
                if (len == 0)
                {
                    Debug.Log("数据为0");
                    CloseSocket();
                    return;
                }
                _message.ReadBuffer(len,HandleRequest);
                StartReceive();
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
            }
        }
        private void HandleRequest(MainPack pack)
        {
            _face.HandleRequest(pack);
        }
        public void Send(MainPack pack)
        {
            _socket.Send(Message.PackData(pack));
        }
    }
}