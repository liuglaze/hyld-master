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

        /// <summary>
        /// 已关闭标志。R3-B 新增：切服/退局必须能把旧局 socket 真正收掉。
        ///
        /// 为什么需要它：原实现只有 `InitSocket`、没有关闭入口，而接收线程是 `while(true)`，
        /// 于是「关掉 socket」会让 `Receive` 抛异常后被 catch 吞掉，线程继续以空转方式活着
        /// （句柄已释放但线程不退出），而且下一个局又会 `new` 一个 socket 起来，
        /// 两个 socket 同时收包。
        /// </summary>
        private volatile bool _closed;

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
            // 幂等重入：先把旧 socket 收掉（切服/重连可能重复调用），
            // 否则会出现两个 socket 同时收同一端口族的包。
            Close();

            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _closed = false;

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
            // _localEnd 在失败路径上为 null（原实现直接 ToString 会抛 NRE，把「连接失败」掩盖成异常）。
            return _localEnd != null ? _localEnd.ToString() : "<none>";
        }

        /// <summary>是否仍持有可用 socket。</summary>
        public bool IsOpen
        {
            get { return client != null && !_closed; }
        }

        /// <summary>
        /// 幂等关闭：置关闭标志 → 关闭 socket → 清掉未消费队列。
        ///
        /// 清队列是必需的：队列里的旧局包如果在切服后才被 `DrainAndDispatch` 分发，
        /// 会被新局（或已停机的旧 BattleData）当成当前帧数据处理。
        /// **不禁用单例**：`Instance` 仍可再次 `InitSocket`（客户端可能回到大厅再进一局）。
        /// </summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            Socket socket = client;
            client = null;
            _localEnd = null;

            if (socket != null)
            {
                try
                {
                    socket.Close();
                }
                catch (Exception ex)
                {
                    Logging.HYLDDebug.Trace("关闭 UDP socket 异常：" + ex.Message);
                }
            }

            ReceivedUdpPacket ignored;
            while (_recvQueue.TryDequeue(out ignored))
            {
            }

            Handle = null;
        }

        /// <summary>
        /// 只在本单例**已经存在**时关闭（R3-B 切服调用点）。
        /// 刻意不做 `Instance` 懒创建：切服时把单例造出来再关掉毫无意义，
        /// 也会让「到底有没有开过旧 socket」变得不可判定。
        /// </summary>
        public static void CloseExisting()
        {
            UDPSocketManger current = instance;
            if (current != null)
            {
                current.Close();
            }
        }

        /// <summary>
        /// 子线程：只负责收包 → 反序列化 → 入队。绝不碰游戏状态。
        /// </summary>
        private void ReceiveLoop()
        {
            while (!_closed)
            {
                Socket socket = client;
                if (socket == null)
                {
                    break;
                }

                try
                {
                    SocketError socketError;
                    int length = socket.Receive(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, out socketError);

                    // 关闭后再收到半包不要入队（否则旧局数据会越过切服边界）。
                    if (_closed)
                    {
                        break;
                    }

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
                catch (ObjectDisposedException)
                {
                    // 正常关闭路径：socket 已 Close，线程就此退出。
                    break;
                }
                catch (Exception ex)
                {
                    if (_closed)
                    {
                        break;
                    }

                    Logging.HYLDDebug.Trace($"udpClient {_localEnd} 接收数据异常:" + ex.Message, true);
                    Debug.LogError("udpClient接收数据异常:" + ex.Message + "   " + _localEnd);

                    // 有界退避：原实现无延时地 continue，关服后会变成忙等（CPU 空转）。
                    Thread.Sleep(20);
                }
            }
        }

        /// <summary>
        /// 主线程每帧调用：取出所有待处理的包，逐个分发给 Handle。
        /// 保证 Handle（和解/逻辑推进等）全部在主线程执行，与 FixedUpdate 无竞争。
        /// </summary>
        public void DrainAndDispatch()
        {
            // 已关闭：旧局包不得再进入业务分发。
            if (_closed)
            {
                return;
            }

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

        public void SendOperation(int operationFrameId, int repeatCount = 1, bool isCriticalInput = false, string criticalReason = null, IList<ClientMove> clientMoves = null, IList<ClientAttack> clientAttacks = null)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = RequestCode.Battle;
            pack.Actioncode = ActionCode.BattlePushDowmPlayerOpeartions;
            pack.BattleInfo = new BattleInfo();
            pack.BattleInfo.ClientInput = new BattleClientInput
            {
                BattlePlayerId = Manger.BattleData.Instance.battleID,
                ClientTick = operationFrameId,
                AckedServerFrame = Manger.BattleData.Instance.sync_frameID,
                RttMs = Manger.BattleData.Instance.IsRttInitialized
                    ? Mathf.RoundToInt(Manger.BattleData.Instance.smoothedRTT)
                    : 0,
            };
            if (clientMoves != null)
            {
                for (int i = 0; i < clientMoves.Count; i++)
                {
                    pack.BattleInfo.ClientInput.Moves.Add(clientMoves[i]);
                }
            }
            if (clientAttacks != null)
            {
                for (int i = 0; i < clientAttacks.Count; i++)
                {
                    pack.BattleInfo.ClientInput.Attacks.Add(clientAttacks[i]);
                }
            }

            int normalizedRepeatCount = Mathf.Max(1, repeatCount);
            for (int sendIndex = 0; sendIndex < normalizedRepeatCount; sendIndex++)
            {
                if (isCriticalInput)
                {
                    Logging.HYLDDebug.FrameTrace($"[CriticalInput][Send] tick={operationFrameId} repeat={sendIndex + 1}/{normalizedRepeatCount} reason={criticalReason ?? "unknown"} ack={pack.BattleInfo.ClientInput.AckedServerFrame} clientMoves={pack.BattleInfo.ClientInput.Moves.Count} attackCount={pack.BattleInfo.ClientInput.Attacks.Count}");
                }
                Send(pack);
            }
        }

        public void Send(MainPack pack)
        {
            if (_closed || client == null)
            {
                // 切服/退局后旧局上行必须静默丢弃，而不是拿去喂一个已关闭的 socket。
                Logging.HYLDDebug.Trace("udp发送被忽略：socket 已关闭");
                return;
            }

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
