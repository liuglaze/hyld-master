/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/5 20:39:51
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketProto;
using Manger;

namespace LongZhiJie
{
	public class Test :MonoBehaviour
	{
        //private bool isBattleStart;
       // public InputField InputField;
        //int Loaclport;
        private void Start()
        {
            //NetGlobal.Instance.Init();
        }

        /*
        UdpClient UdpClient;
        IPEndPoint endpoint;
        TcpClient TcpClient;
        private NetworkStream _stream;
        private void Start()
        {
            endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4000);
            TcpClient = new TcpClient();
           
           // TcpClient.BeginConnect(endpoint.Address, endpoint.Port,,);
            TcpClient.Connect(endpoint.Address, endpoint.Port);
            _stream = TcpClient.GetStream();
            _ = ReceiveLoopAsync();
        }
        async Task ReceiveLoopAsync()
        {
            try
            {
                while (true)
                {
                    Logging.HYLDDebug.LogError("StartReceive");
                    byte[] res=new byte[1024];
                    int len = await _stream.ReadAsync(res ,0, 1024);
                    Logging.HYLDDebug.LogError("Receive Ac "+len);
                    if (len == 0)
                    {
                        Logging.HYLDDebug.Log("数据为0");
                        return;
                    }
                    string str = System.Text.Encoding.ASCII.GetString(res);
                    Logging.HYLDDebug.LogError(str);
                    str = "lzjclient Ping";
                    res = System.Text.Encoding.ASCII.GetBytes(str.ToCharArray());
                    TcpClient.Client.Send(res);
                    //_message.ReadBuffer(len, HandleRequest);
                }
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError(ex);
            }
            finally
            {
                //CloseSocket();
            }
        }
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                string str = "lzj";
                byte[] res = System.Text.Encoding.ASCII.GetBytes(str.ToCharArray());
                TcpClient.Client.Send(res);
            }
        }
        private void UDPTest()
        {
            UdpClient = new UdpClient();
            endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4000);
            //UdpClient.Connect(endpoint);
            string str = "lzjclient join!";
            byte[] res = System.Text.Encoding.Default.GetBytes(str.ToCharArray());
            UdpClient.Send(res, res.Length, endpoint);

            //IPEndPoint _localEnd = (IPEndPoint)_udpClient.Client.LocalEndPoint;
            //localPort = _localEnd.Port;
            Logging.HYLDDebug.Log($"客户端发送  lzjclent join! {res.Length}   {str.Length}");
            Logging.HYLDDebug.Log(str.ToCharArray()[0] + "  " + str.ToCharArray()[1]);
            Thread thread = new Thread(Receive);
            thread.Start();
            InvokeRepeating("Ping", 1, 1);

        }
        private void Ping()
        {
            string str = "lzjclient Ping";
            byte[] res = System.Text.Encoding.Default.GetBytes(str.ToCharArray());
            UdpClient.Send(res, res.Length, endpoint);
        }
        private void Receive()
        {
            while (true)
            {
                byte[] bytes = UdpClient.Receive(ref endpoint);

                string str = System.Text.Encoding.Default.GetString(bytes);
                Logging.HYLDDebug.LogError(str);
            }

        }
        
        */
    }
}