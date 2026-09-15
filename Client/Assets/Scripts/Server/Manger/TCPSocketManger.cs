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
        private readonly object _sendLock = new object();
        private bool _isSending;
        private bool _sendFailed;
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
                lock (_sendLock)
                {
                    writeQueue.Clear();
                    _isSending = false;
                    _sendFailed = false;
                }
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
            CloseSocket("CloseSocket");
        }

        private void CloseSocket(string reason)
        {
            HYLDStaticValue.是否为连接状态 = false;
            lock (_sendLock)
            {
                _isSending = false;
                _sendFailed = true;
                writeQueue.Clear();
            }

            Socket socket = null;
            try
            {
                socket = _client?.Client;
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError($"[TCP][CloseSocketError] reason={reason} stage=get-socket ex={ex}");
            }

            Logging.HYLDDebug.Trace($"[TCP][CloseSocket] reason={reason} hasSocket={socket != null} socketConnected={(socket != null && socket.Connected)}");
            try
            {
                socket?.Close();
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError($"[TCP][CloseSocketError] reason={reason} stage=close ex={ex}");
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
                    _message.EnsureWritableSpace();
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
                Logging.HYLDDebug.LogError($"[TCP][ReceiveLoopException] {ex}");
            } finally {
                CloseSocket("ReceiveLoop finally");
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
                bool hasClient = _client != null;
                bool hasSocket = hasClient && _client.Client != null;
                bool connected = hasSocket && _client.Client.Connected;
                int pendingBefore;
                lock (_sendLock)
                {
                    pendingBefore = writeQueue.Count;
                }
                Logging.HYLDDebug.Trace($"[TCP][SendAttempt] connectedFlag={HYLDStaticValue.是否为连接状态} hasClient={hasClient} hasSocket={hasSocket} socketConnected={connected} sendFailed={_sendFailed} isSending={_isSending} queue={pendingBefore} request={pack.Requestcode} action={pack.Actioncode}");
                if (!hasClient || !hasSocket || !connected || _sendFailed)
                {
                    Logging.HYLDDebug.LogError($"[TCP][SendBlocked] connectedFlag={HYLDStaticValue.是否为连接状态} hasClient={hasClient} hasSocket={hasSocket} socketConnected={connected} sendFailed={_sendFailed} request={pack.Requestcode} action={pack.Actioncode}");
                    CloseSocket($"SendBlocked request={pack.Requestcode} action={pack.Actioncode}");
                    return;
                }

                byte[] sendbyte = TCPSocketMessage.PackData(pack);
                ByteArray ba = new ByteArray(sendbyte);
                bool shouldStartSend = false;
                int queueAfter;
                lock (_sendLock)
                {
                    writeQueue.Enqueue(ba);
                    queueAfter = writeQueue.Count;
                    if (!_isSending)
                    {
                        _isSending = true;
                        shouldStartSend = true;
                    }
                }

                Logging.HYLDDebug.Trace($"[TCP][SendQueued] bytes={sendbyte.Length} queue={queueAfter} start={shouldStartSend} request={pack.Requestcode} action={pack.Actioncode}");
                if (shouldStartSend)
                {
                    StartQueueHeadSend();
                }
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError($"[TCP][SendError] connectedFlag={HYLDStaticValue.是否为连接状态} request={pack.Requestcode} action={pack.Actioncode} ex={ex}");
                CloseSocket($"SendError request={pack.Requestcode} action={pack.Actioncode}");
            }
        }

        private void StartQueueHeadSend()
        {
            ByteArray head = null;
            int queueCount;
            lock (_sendLock)
            {
                queueCount = writeQueue.Count;
                if (_sendFailed || queueCount == 0)
                {
                    _isSending = false;
                    return;
                }
                head = writeQueue.Peek();
            }

            try
            {
                Socket socket = _client?.Client;
                if (socket == null || !socket.Connected)
                {
                    Logging.HYLDDebug.LogError($"[TCP][SendStartBlocked] hasSocket={socket != null} socketConnected={(socket != null && socket.Connected)} queue={queueCount}");
                    CloseSocket("SendStartBlocked");
                    return;
                }

                Logging.HYLDDebug.Trace($"[TCP][SendStart] bytes={head.Lenth} readIdx={head.readIdx} queue={queueCount} local={socket.LocalEndPoint} remote={socket.RemoteEndPoint}");
                socket.BeginSend(head.bytes, head.readIdx, head.Lenth, 0, SendBackCall, socket);
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError($"[TCP][SendStartError] queue={queueCount} ex={ex}");
                CloseSocket("SendStartError");
            }
        }

        private void SendBackCall(IAsyncResult ar)
        {
            try
            {
                Socket socket = (Socket)ar.AsyncState;
                int count = socket.EndSend(ar);
                ByteArray next = null;
                int queueAfter;
                lock (_sendLock)
                {
                    if (writeQueue.Count == 0)
                    {
                        throw new InvalidOperationException("Send callback completed with empty queue.");
                    }

                    ByteArray current = writeQueue.Peek();
                    current.readIdx += count;
                    if (current.Lenth == 0)
                    {
                        writeQueue.Dequeue();
                    }

                    queueAfter = writeQueue.Count;
                    if (queueAfter == 0)
                    {
                        _isSending = false;
                    }
                    else
                    {
                        next = writeQueue.Peek();
                    }
                }

                Logging.HYLDDebug.Trace($"[TCP][SendCallback] sent={count} queue={queueAfter} hasNext={next != null} socketConnected={socket.Connected}");
                if (next != null)
                {
                    StartQueueHeadSend();
                }
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError($"[TCP][SendCallbackError] ex={ex}");
                CloseSocket("SendCallbackError");
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
