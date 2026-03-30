/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/22 18:43:34
    Description:     TCP套接字客户端管理
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using System.Net.Sockets;
using SocketProto;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

namespace Server
{
	public class TCPServerManger
	{
        private TcpClient _client;
        private Socket _socket => _client.Client;
        private NetworkStream _stream;
        private TCPSocketMessage _message;
        //public TCPSocketManger(GameFace face) : base(face) { }
        public void OnInit()
        {
            _message = new TCPSocketMessage();
            InitSocket();
        }
        public void OnDestroy()
        {
            _message = null;
            CloseSocket();
        }


        /// <summary>
        /// 初始化Socket
        /// </summary>
        private void InitSocket()
        {
            _client = new TcpClient();
            try
            {
                //2.创建TCP连接
                //IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(IPManager.GetIP(ADDRESSFAM.IPv4)), ServerConfig.UDPservePort);
              
                _client.Connect(NetConfigValue.ServiceIP, Server.NetConfigValue.ServiceTCPPort);
                _stream = _client.GetStream();
                Logging.HYLDDebug.Trace($"TCP连接成功  本机：{_client.Client.LocalEndPoint}  服务器:{_client.Client.RemoteEndPoint}");
                Logging.HYLDDebug.Log("连接成功");
                HYLDManger.Instance.ShowMessage("连接成功");
                HYLDStaticValue.是否为连接状态 = true;

                //4.开始异步接受消息
                _ = ReceiveLoopAsync();
            }
            catch (Exception e)
            {
                //连接失败
                Logging.HYLDDebug.Log("连接失败"+e);
                HYLDManger.Instance.ShowMessage("连接失败");
                HYLDStaticValue.是否为连接状态 = false;
            }
        }

        /// <summary>
        /// 关闭Socket
        /// </summary>
        public void CloseSocket()
        {
            if (_socket != null && _socket.Connected)
            {
                HYLDStaticValue.是否为连接状态 = false; 
                _socket.Close();
            }
        }
        /// <summary>
        /// 异步接受消息
        /// </summary>
        /// <returns></returns>
        async Task ReceiveLoopAsync()
        {

            try
            {
                while (true)
                {
                    int len = await _stream.ReadAsync(_message.Data, _message.StartIndex, _message.RemainSize);
                    if (len == 0)
                    {
                        Logging.HYLDDebug.Trace($"[TCP][ReceiveLoopEnd] reason=stream-returned-0 local={_client?.Client?.LocalEndPoint} remote={_client?.Client?.RemoteEndPoint}");
                        if (HYLDManger.Instance != null)
                        {
                            HYLDManger.Instance.CloseClient();
                        }
                        return;
                    }
                    _message.ReadBuffer(len, HandleRequest);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
                //Logging.HYLDDebug.LogError(ex);
            } finally {
                CloseSocket();
            }
        }
        private void HandleRequest(MainPack pack)
        {
            RequestManger.HandleRequest(pack);
        }
        private Queue<ByteArray> writeQueue = new Queue<ByteArray>();
        public void Send(MainPack pack)
        {
            //5.发送消息
            try
            {
                byte[] sendbyte = TCPSocketMessage.PackData(pack);
                ByteArray ba = new ByteArray(sendbyte);
                int count = 0;
                lock (writeQueue)
                {
                    writeQueue.Enqueue(ba);
                    count = writeQueue.Count;
                }
                if (count == 1)
                {
                    //Console.WriteLine(writeQueue.Count);
                    _socket.BeginSend(sendbyte, 0, sendbyte.Length, 0, SendBackCall, _socket);
                }
                //Console.WriteLine("[Send]" + BitConverter.ToString(sendbyte));
            }
            catch (Exception ex)
            {
                Console.WriteLine("!!!!!!!!![Send] Error!!!!!!!!!");
                Console.WriteLine(ex);
            }
        }
        private void SendBackCall(IAsyncResult ar)
        {

            Socket socket = (Socket)ar.AsyncState;
            int count = socket.EndSend(ar);
            ByteArray ba;
            lock (writeQueue)
            {
                ba = writeQueue.Peek();
            }
            ba.readIdx += count;
            ///完整发送了消息
            if (ba.Lenth == 0)
            {
                lock (writeQueue)
                {
                    ba = null;
                    writeQueue.Dequeue();
                    if (writeQueue.Count != 0)
                        ba = writeQueue.Peek();
                }
            }

            if (ba != null)
            {
                socket.BeginSend(ba.bytes, ba.readIdx, ba.Lenth, 0, SendBackCall, socket);
            }

        }

    }
}



public class IPManager
{
    public static string GetIP(ADDRESSFAM Addfam)
    {
        //Return null if ADDRESSFAM is Ipv6 but Os does not support it
        if (Addfam == ADDRESSFAM.IPv6 && !Socket.OSSupportsIPv6)
        {
            return null;
        }

        string output = "";

        foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
        {

            //#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            NetworkInterfaceType _type1 = NetworkInterfaceType.Wireless80211;
            NetworkInterfaceType _type2 = NetworkInterfaceType.Ethernet;

            if ((item.NetworkInterfaceType == _type1 || item.NetworkInterfaceType == _type2) && item.OperationalStatus == OperationalStatus.Up)
            //#endif
            {
                foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                {
                    //IPv4
                    if (Addfam == ADDRESSFAM.IPv4)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            output = ip.Address.ToString();
                            //Debug.Log("啊" + output);
                        }
                    }

                    //IPv6
                    else if (Addfam == ADDRESSFAM.IPv6)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            output = ip.Address.ToString();
                        }
                    }
                }
            }
        }
        return output;
    }
}

public enum ADDRESSFAM
{
    IPv4, IPv6
}
