using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using System.Net.Sockets;
using SocketProto;
using System.Threading.Tasks;
using System.Net;
using System.Threading;
using Google.Protobuf;
using Logging;
using System.Collections.Concurrent;
namespace Server
{
    class UDPSocketManger
    {
        private Socket client;
        private static UDPSocketManger instance;
        public Action<MainPack, long> Handle;
        private IPEndPoint _localEnd;
        private const int UdpReceiveBufferLength = 64 * 1024;
        private byte[] receiveBuffer = new byte[UdpReceiveBufferLength];

        private sealed class ReceivedUdpPacket
        {
            public MainPack Pack;
            public long ReceivedAtMs;
        }

        // 无锁队列：子线程只写，主线程只读
        private readonly ConcurrentQueue<ReceivedUdpPacket> _recvQueue = new ConcurrentQueue<ReceivedUdpPacket>();
        // 主线程复用的临时列表，避免每帧分配
        private readonly List<ReceivedUdpPacket> _drainBuffer = new List<ReceivedUdpPacket>(4);

        public static UDPSocketManger Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new UDPSocketManger();
                }
                return instance;
            }
        }

        public string InitSocket()
        {
            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                client.ReceiveBufferSize = UdpReceiveBufferLength;
                client.SendBufferSize = UdpReceiveBufferLength;
                client.Connect(NetConfigValue.ServiceIP, NetConfigValue.ServiceUDPPort);
                _localEnd = (IPEndPoint)client.LocalEndPoint;
                Logging.HYLDDebug.Trace($"UDP连接成功  本地:{client.LocalEndPoint}   服务器:{client.RemoteEndPoint}");

                Thread.Sleep(100);
                (new Thread(ReceiveLoop) { IsBackground = true }).Start();
            }
            catch (Exception e)
            {
                Logging.HYLDDebug.Log("连接失败" + e);
                HYLDManger.Instance.ShowMessage("连接失败");
                HYLDStaticValue.是否为连接状态 = false;
            }
            return _localEnd.ToString();
        }

        /// <summary>
        /// 子线程：只负责收包 → 反序列化 → 入队。绝不碰游戏状态。
        /// </summary>
        private void ReceiveLoop()
        {
            while (true)
            {
                try
                {
                    SocketError socketError;
                    int length = client.Receive(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, out socketError);
                    if (socketError == SocketError.MessageSize || length >= receiveBuffer.Length)
                    {
                        continue;
                    }
                    if (socketError != SocketError.Success || length <= 0)
                    {
                        continue;
                    }
                    MainPack pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(receiveBuffer, 0, length);
                    long receivedAtMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _recvQueue.Enqueue(new ReceivedUdpPacket
                    {
                        Pack = pack,
                        ReceivedAtMs = receivedAtMs,
                    });
                }
                catch (Exception ex)
                {
                    Logging.HYLDDebug.Trace($"udpClient {_localEnd} 接收数据异常:" + ex.Message, true);
                    Debug.LogError("udpClient接收数据异常:" + ex.Message + "   " + _localEnd);
                }
            }
        }

        /// <summary>
        /// 主线程每帧调用：取出所有待处理的包，逐个分发给 Handle。
        /// 保证 Handle（和解/逻辑推进等）全部在主线程执行，与 FixedUpdate 无竞争。
        /// </summary>
        public void DrainAndDispatch()
        {
            _drainBuffer.Clear();
            while (_recvQueue.TryDequeue(out ReceivedUdpPacket packet))
            {
                _drainBuffer.Add(packet);
            }
            for (int i = 0; i < _drainBuffer.Count; i++)
            {
                Handle?.Invoke(_drainBuffer[i].Pack, _drainBuffer[i].ReceivedAtMs);
            }
        }

        public void SendOperation(int operationFrameId)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = RequestCode.Battle;
            pack.Actioncode = ActionCode.BattlePushDowmPlayerOpeartions;
            pack.BattleInfo = new BattleInfo();
            pack.BattleInfo.SelfOperation = Manger.BattleData.Instance.selfOperation;
            pack.BattleInfo.OperationID = operationFrameId;
            pack.BattleInfo.ClientAckedFrame = Manger.BattleData.Instance.sync_frameID;
            Send(pack);
        }

        public void Send(MainPack pack)
        {
            byte[] sendbuff = pack.ToByteArray();
            try
            {
                EndPoint point = new IPEndPoint(IPAddress.Parse(NetConfigValue.ServiceIP), NetConfigValue.ServiceUDPPort);
                client.SendTo(sendbuff, point);
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.Log("udp发送失败:" + ex.Message);
                Logging.HYLDDebug.Trace("udp发送失败:" + ex.Message);
            }
        }
    }
}
