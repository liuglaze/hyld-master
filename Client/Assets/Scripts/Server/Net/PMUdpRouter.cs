using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;
using SocketProto;

namespace PMNet.Server
{
    /// <summary>
    /// hyld 战斗 UDP 包的路由器（P3'-1 从 <c>Server/Server/ClientUdp.cs</c> 迁移而来）。
    ///
    /// <para>
    /// **它不是可靠传输层。** 沿用既有契约：UDP 载荷就是裸 protobuf <see cref="MainPack"/>，
    /// 没有长度头、没有序号、没有 ack、没有重传、没有去重、没有乱序处理。
    /// 可靠性全部在战斗层（同帧重复发送、ack 水位、攻击去重）。
    /// 本类只负责：解析 → 按 <c>ActionCode</c> 与来源端点路由 → 交给已注册的 handler。
    /// </para>
    ///
    /// <para>
    /// **线程纪律（本类存在的核心原因）**：既有实现让在后台线程上直接执行战斗 handler 并直接打日志，
    /// 这会在 Unity 中触碰 UnityEngine API 而崩溃。本类把职责切成两半：
    /// <list type="bullet">
    /// <item><see cref="OnDatagramReceived"/> 运行在接收线程：只做「解析 + 入队」，**不调用 handler、不打日志**（只通过注入的日志回调入队）。</item>
    /// <item><see cref="DrainAndDispatch"/> 必须由主线程调用：出队 → 分发 handler → 输出日志。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 本类刻意**不引用 UnityEngine**（日志通过构造参数注入），因此可以被纯 C# 门禁编译校验，
    /// 也可以将来被服务端工程链接复用。
    /// </para>
    ///
    /// <para>
    /// 相对旧实现（<c>LZJUDP</c>）的确定性改进，均已记录在
    /// <c>Docs/plans/net-architecture-migration.md</c> §9.8：
    /// 发送失败不再抛出（旧实现的发送异常会冒泡到战斗循环外层 catch，静默废掉整局）；
    /// 入站队列有界（旧实现无背压）；handler 异常被隔离；全部日志走主线程。
    /// 旧实现的 NetSim 丢包/延迟注入脚手架**有意不迁移**（那是演示用测试设施）。
    /// </para>
    /// </summary>
    public sealed class PMUdpRouter
    {
        /// <summary>单个数据包触发的 handler 委托。</summary>
        public delegate void PacketHandler(MainPack pack);

        /// <summary>
        /// 入站队列上限。超限时丢弃新包并计数（不是丢旧包：旧包通常已构成完整的一帧序列）。
        /// 该值远大于正常帧流量，只在洪泛/异常时起作用。
        /// </summary>
        private const int MaxInboundQueue = 4096;

        /// <summary>端点映射表上限，避免被扫描流量撑爆内存。</summary>
        private const int MaxEndpointRoutes = 256;

        /// <summary>
        /// 战斗绑定的查询面。DS 单局形态下由 <see cref="PMSingleBattleRegistry"/> 提供；
        /// 旧服务端由 <c>Server/Server/BattleManage.cs</c> 提供。
        /// 抽象成接口是为了让本类不依赖服务端的 <c>BattleManage</c>（客户端没有该类）。
        /// </summary>
        public interface IBattleRegistry
        {
            /// <summary>由玩家 uid 反查其所在战斗。旧实现对应 <c>BattleManage.TryGetBattleIDByUID</c>。</summary>
            bool TryGetBattleIdByUid(int uid, out int battleId);

            /// <summary>
            /// 由玩家 uid 反查其战斗内编号。旧实现对应 <c>BattleManage.TryGetBattlePlayerId</c>。
            /// 返回 <c>0</c> 表示「尚未分配」——此时路由会跳过一致性校验（见 <see cref="Dispatch"/>）。
            /// </summary>
            bool TryGetBattlePlayerId(int uid, out int battlePlayerId);
        }

        private sealed class EndpointRouteInfo
        {
            public int Uid;
            public int BattlePlayerId;
            public int BattleId;
        }

        private struct InboundPacket
        {
            public MainPack Pack;
            public string EndpointKey;
        }

        private readonly Socket _socket;
        private readonly IBattleRegistry _registry;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private readonly Action<string> _logError;

        private readonly Dictionary<int, PacketHandler> _handlers = new Dictionary<int, PacketHandler>();
        private readonly Dictionary<string, EndpointRouteInfo> _endpointRoutes = new Dictionary<string, EndpointRouteInfo>();

        /// <summary>同时保护 <see cref="_handlers"/> 与 <see cref="_endpointRoutes"/>。</summary>
        private readonly object _routeLock = new object();

        private readonly Queue<InboundPacket> _inbound = new Queue<InboundPacket>();

        /// <summary>同时保护 <see cref="_inbound"/>。</summary>
        private readonly object _inboundLock = new object();

        // ---- 统计（接收线程写，主线程读；全部用 Interlocked）----
        private int _receivedDatagrams;
        private int _enqueuedDatagrams;
        private int _dispatchedPackets;
        private int _parseErrors;
        private int _droppedQueueFull;
        private int _unroutablePackets;
        private int _noHandlerPackets;
        private int _unroutedPings;
        private int _handlerErrors;
        private int _sentDatagrams;
        private int _sendErrors;
        private int _endpointResets;

        /// <summary>接收线程上「已丢弃但还没汇报」的计数，用于把刷屏压成一行。</summary>
        private int _suppressedParseErrors;
        private int _suppressedQueueFull;

        /// <summary><c>true</c> 表示注册表说玩家编号「尚未分配」并已提示过一次。</summary>
        private int _unassignedPlayerIdWarned;

        public PMUdpRouter(
            Socket socket,
            IBattleRegistry registry,
            Action<string> logInfo,
            Action<string> logWarning,
            Action<string> logError)
        {
            if (socket == null)
            {
                throw new ArgumentNullException("socket");
            }

            _socket = socket;
            _registry = registry;
            _logInfo = logInfo;
            _logWarning = logWarning;
            _logError = logError;
        }

        // ==================== 路由注册 ====================

        /// <summary>
        /// 按 battleId 注册战斗 handler。重复注册同一 battleId 会**拒绝**并返回 false
        /// （旧实现是直接覆盖、静默顶掉前者，见 S6-D16）。
        /// </summary>
        public bool RegisterBattle(int battleId, PacketHandler handler)
        {
            if (battleId <= 0 || handler == null)
            {
                return false;
            }

            lock (_routeLock)
            {
                if (_handlers.ContainsKey(battleId))
                {
                    return false;
                }

                _handlers[battleId] = handler;
            }

            LogInfo("[PMUdpRouter] RegisterBattle: battleId=" + battleId);
            return true;
        }

        /// <summary>按 battleId 注销战斗 handler，并清掉该战斗的全部端点路由。幂等。</summary>
        public bool UnregisterBattle(int battleId)
        {
            bool removed;
            lock (_routeLock)
            {
                removed = _handlers.Remove(battleId);

                if (_endpointRoutes.Count > 0)
                {
                    List<string> stale = new List<string>();
                    foreach (KeyValuePair<string, EndpointRouteInfo> item in _endpointRoutes)
                    {
                        if (item.Value.BattleId == battleId)
                        {
                            stale.Add(item.Key);
                        }
                    }

                    for (int i = 0; i < stale.Count; i++)
                    {
                        _endpointRoutes.Remove(stale[i]);
                        Interlocked.Increment(ref _endpointResets);
                    }
                }
            }

            if (removed)
            {
                LogInfo("[PMUdpRouter] UnregisterBattle: battleId=" + battleId);
            }

            return removed;
        }

        /// <summary>已注册的战斗数量（主线程读）。</summary>
        public int RegisteredBattleCount
        {
            get
            {
                lock (_routeLock)
                {
                    return _handlers.Count;
                }
            }
        }

        // ==================== 接收路径 ====================

        /// <summary>
        /// 接收线程入口：把一片数据报解析成 <see cref="MainPack"/> 并入队。
        ///
        /// <para>
        /// **纪律**：本方法运行在接收线程上，禁止调用 UnityEngine API、禁止执行战斗 handler。
        /// 只允许：解析、加锁入队、累加 Interlocked 计数、通过注入的日志回调（其实现必须只是入队）输出。
        /// </para>
        /// </summary>
        /// <param name="buffer">接收缓冲。本方法会立即消费内容，不保留引用。</param>
        /// <param name="length">有效字节数。</param>
        /// <param name="from">数据报来源端点。</param>
        public void OnDatagramReceived(byte[] buffer, int length, EndPoint from)
        {
            if (buffer == null || length <= 0)
            {
                return;
            }

            Interlocked.Increment(ref _receivedDatagrams);

            MainPack pack;
            try
            {
                // 与旧实现一致：走描述符 + 默认解析器，不额外指定解析选项。
                pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(buffer, 0, length);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _parseErrors);

                // 解析失败可能来自扫描流量，会刷屏；只在第一次与每 256 次时输出一行。
                int suppressed = Interlocked.Increment(ref _suppressedParseErrors);
                if (suppressed == 1 || (suppressed % 256) == 0)
                {
                    LogWarning("[PMUdpRouter] 数据报解析失败（第 " + suppressed + " 次）：" + e.Message);
                }

                return;
            }

            string endpointKey = GetEndpointKey(from);

            lock (_inboundLock)
            {
                if (_inbound.Count >= MaxInboundQueue)
                {
                    Interlocked.Increment(ref _droppedQueueFull);

                    int suppressed = Interlocked.Increment(ref _suppressedQueueFull);
                    if (suppressed == 1 || (suppressed % 256) == 0)
                    {
                        LogWarning("[PMUdpRouter] 入站队列已满（上限 " + MaxInboundQueue
                            + "），丢弃新包（第 " + suppressed + " 次）。主线程消费过慢或被洪泛。");
                    }

                    return;
                }

                InboundPacket item;
                item.Pack = pack;
                item.EndpointKey = endpointKey;
                _inbound.Enqueue(item);
            }

            Interlocked.Increment(ref _enqueuedDatagrams);
        }

        /// <summary>
        /// 主线程出口：最多取出 <paramref name="maxPackets"/> 个包并分发。
        /// 返回本次实际分发的包数（0 表示队列已空，调用方可据此提前退出）。
        /// </summary>
        public int DrainAndDispatch(int maxPackets)
        {
            if (maxPackets <= 0)
            {
                return 0;
            }

            int dispatched = 0;
            for (int i = 0; i < maxPackets; i++)
            {
                InboundPacket item;
                lock (_inboundLock)
                {
                    if (_inbound.Count == 0)
                    {
                        break;
                    }

                    item = _inbound.Dequeue();
                }

                Dispatch(item);
                dispatched++;
            }

            if (dispatched > 0)
            {
                Interlocked.Add(ref _dispatchedPackets, dispatched);
            }

            return dispatched;
        }

        /// <summary>当前待处理包数（诊断用）。</summary>
        public int PendingInboundCount
        {
            get
            {
                lock (_inboundLock)
                {
                    return _inbound.Count;
                }
            }
        }

        /// <summary>
        /// 主线程分发：Ping 就地应答；其余按端点路由到战斗 handler。
        /// 与旧实现一致，但补了三处：无法路由/无 handler 分别计数，handler 异常被隔离。
        /// </summary>
        private void Dispatch(InboundPacket item)
        {
            MainPack pack = item.Pack;
            string endpointKey = item.EndpointKey;

            if (pack.Actioncode == ActionCode.Ping)
            {
                HandlePing(pack, endpointKey);
                return;
            }

            int battleId;
            if (!TryResolveBattleId(pack, endpointKey, out battleId))
            {
                Interlocked.Increment(ref _unroutablePackets);
                LogWarning("[PMUdpRouter] 无法路由 UDP 包: ActionCode=" + pack.Actioncode
                    + " Endpoint=" + (endpointKey ?? "<null>") + "，已丢弃");
                return;
            }

            PacketHandler handler;
            lock (_routeLock)
            {
                if (!_handlers.TryGetValue(battleId, out handler))
                {
                    handler = null;
                }
            }

            if (handler == null)
            {
                Interlocked.Increment(ref _noHandlerPackets);
                LogWarning("[PMUdpRouter] battleId=" + battleId + " 无已注册的 handler，丢弃包");
                return;
            }

            try
            {
                handler(pack);
            }
            catch (Exception e)
            {
                // 旧实现里 handler 异常会冒泡进接收线程的 catch-all；新架构里它在主线程上，
                // 必须就地隔离，否则一次战斗逻辑异常会打断整个 DS 的帧循环。
                Interlocked.Increment(ref _handlerErrors);
                LogError("[PMUdpRouter] battleId=" + battleId + " 的 handler 抛出异常：" + e);
            }
        }

        /// <summary>
        /// Ping/Pong：只有**已建立端点路由**的来源才回，避免被当成开放反射器。
        /// 与旧实现语义一致。
        /// </summary>
        private void HandlePing(MainPack pack, string endpointKey)
        {
            if (string.IsNullOrEmpty(endpointKey))
            {
                return;
            }

            bool hasRoute;
            lock (_routeLock)
            {
                hasRoute = _endpointRoutes.ContainsKey(endpointKey);
            }

            if (!hasRoute)
            {
                // 未建链端点的 Ping 不应答（否则本层会变成开放反射器），但**必须计数**：
                // 实测中这类包原本被静默丢掉，心跳里的 unroutable 仍是 0，
                // 于是「有人在扫端口」或「客户端丢了路由」这两种情况都无法从日志看出来。
                int seen = Interlocked.Increment(ref _unroutedPings);
                if (seen == 1 || (seen % 256) == 0)
                {
                    LogWarning("[PMUdpRouter] 未建链端点的 Ping，已忽略（第 " + seen + " 次）："
                        + endpointKey);
                }

                return;
            }

            MainPack pong = new MainPack();
            pong.Actioncode = ActionCode.Pong;
            pong.Timestamp = pack.Timestamp;
            Send(pong, endpointKey);
        }

        /// <summary>
        /// 路由判定（迁移自 <c>ClientUdp.TryResolveBattleID</c>）。
        /// <c>BattleReady</c> 建立端点→战斗映射；其余战斗包按端点查映射。
        /// </summary>
        private bool TryResolveBattleId(MainPack pack, string endpointKey, out int battleId)
        {
            battleId = -1;

            if (string.IsNullOrEmpty(endpointKey))
            {
                return false;
            }

            // 旧实现把整个判定包在 try/catch 里（畸形包会让字段解引用抛异常）。保留该保护：
            // 路由失败只应表现为「丢这个包」，不应打断主线程帧循环。
            try
            {
                int battlePlayerId;
                if (!TryParseBattlePlayerId(pack, out battlePlayerId))
                {
                    return false;
                }

                switch (pack.Actioncode)
                {
                    case ActionCode.BattleReady:
                        return TryResolveRouteSetup(pack, endpointKey, out battleId);

                    case ActionCode.BattlePushDowmPlayerOpeartions:
                    case ActionCode.ClientSendGameOver:
                    case ActionCode.BattleSetNetSimConfig:
                        {
                            EndpointRouteInfo routeInfo;
                            lock (_routeLock)
                            {
                                if (!_endpointRoutes.TryGetValue(endpointKey, out routeInfo))
                                {
                                    return false;
                                }
                            }

                            // routeInfo.BattlePlayerId == 0 表示注册表尚未分配编号（DS 等大厅下发，
                            // 见 PMSingleBattleRegistry 的注释），此时跳过一致性校验。
                            if (routeInfo.BattlePlayerId != 0 && routeInfo.BattlePlayerId != battlePlayerId)
                            {
                                LogWarning("[PMUdpRouter] battlePlayerId 不匹配: endpoint=" + endpointKey
                                    + " expected=" + routeInfo.BattlePlayerId + " actual=" + battlePlayerId);
                                return false;
                            }

                            battleId = routeInfo.BattleId;
                            return battleId > 0;
                        }

                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _unroutablePackets);
                LogWarning("[PMUdpRouter] 路由判定异常（包已丢弃）：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// <c>BattleReady</c> 的建链分支：校验 uid 属于某场战斗，然后把**服务端观测到的真实远端地址**
        /// 写回 <c>pack.Str</c>，供战斗层记录（不要依赖客户端自报地址）。
        /// </summary>
        private bool TryResolveRouteSetup(MainPack pack, string endpointKey, out int battleId)
        {
            battleId = -1;

            if (pack.Battleplayerpack == null || pack.Battleplayerpack.Count == 0)
            {
                return false;
            }

            int uid = pack.Battleplayerpack[0].Id;
            if (uid <= 0 || _registry == null || !_registry.TryGetBattleIdByUid(uid, out battleId))
            {
                return false;
            }

            int expectedBattlePlayerId;
            if (!_registry.TryGetBattlePlayerId(uid, out expectedBattlePlayerId))
            {
                LogWarning("[PMUdpRouter] BattleReady 未找到 battlePlayerId, uid=" + uid + ", battleID=" + battleId);
                return false;
            }

            int actualBattlePlayerId;
            if (!TryParseBattlePlayerId(pack, out actualBattlePlayerId))
            {
                return false;
            }

            // 注意两个字段的语义不同，不要混用：
            //   Battleplayerpack[0].Id       = 玩家 uid（账号级）
            //   Battleplayerpack[0].Battleid = 战斗内玩家编号（本局内）
            if (expectedBattlePlayerId != 0 && expectedBattlePlayerId != actualBattlePlayerId)
            {
                LogWarning("[PMUdpRouter] BattleReady battlePlayerId 不匹配: uid=" + uid
                    + " battleID=" + battleId + " expected=" + expectedBattlePlayerId + " actual=" + actualBattlePlayerId);
                return false;
            }

            if (expectedBattlePlayerId == 0 && Interlocked.CompareExchange(ref _unassignedPlayerIdWarned, 1, 0) == 0)
            {
                LogWarning("[PMUdpRouter] 注册表尚未分配 battlePlayerId（DS 单局模式）。"
                    + "已暂时跳过玩家编号一致性校验；P4' 接入大厅下发的玩家分配后此校验才真正生效。");
            }

            int addedRoutes;
            lock (_routeLock)
            {
                EndpointRouteInfo existing;
                if (_endpointRoutes.TryGetValue(endpointKey, out existing))
                {
                    // 同一端点重复 BattleReady：更新内容，不占新增名额。
                    existing.Uid = uid;
                    existing.BattlePlayerId = expectedBattlePlayerId;
                    existing.BattleId = battleId;
                    addedRoutes = 0;
                }
                else
                {
                    if (_endpointRoutes.Count >= MaxEndpointRoutes)
                    {
                        LogWarning("[PMUdpRouter] 端点路由表已满（上限 " + MaxEndpointRoutes
                            + "），拒绝新的 BattleReady: endpoint=" + endpointKey);
                        return false;
                    }

                    EndpointRouteInfo info = new EndpointRouteInfo();
                    info.Uid = uid;
                    info.BattlePlayerId = expectedBattlePlayerId;
                    info.BattleId = battleId;
                    _endpointRoutes[endpointKey] = info;
                    addedRoutes = 1;
                }
            }

            if (addedRoutes > 0)
            {
                LogInfo("[PMUdpRouter] BattleReady 建链: uid=" + uid + " battleId=" + battleId
                    + " endpoint=" + endpointKey);
            }

            // 使用服务端观测到的真实远端地址，避免依赖客户端自报地址。
            pack.Str = endpointKey;
            return true;
        }

        /// <summary>
        /// 按 <c>ActionCode</c> 从包内提取「战斗内玩家编号」（battlePlayerId）。
        /// 直接迁移自 <c>ClientUdp.TryParseBattlePlayerId</c>：**不同动作读不同字段**，不要简化成单一读法。
        /// </summary>
        private static bool TryParseBattlePlayerId(MainPack pack, out int battlePlayerId)
        {
            battlePlayerId = -1;

            switch (pack.Actioncode)
            {
                case ActionCode.BattleReady:
                    if (pack.Battleplayerpack == null || pack.Battleplayerpack.Count == 0)
                    {
                        return false;
                    }

                    battlePlayerId = pack.Battleplayerpack[0].Battleid;
                    return battlePlayerId > 0;

                case ActionCode.BattlePushDowmPlayerOpeartions:
                    if (pack.BattleInfo == null || pack.BattleInfo.ClientInput == null)
                    {
                        return false;
                    }

                    battlePlayerId = pack.BattleInfo.ClientInput.BattlePlayerId;
                    return battlePlayerId > 0;

                case ActionCode.ClientSendGameOver:
                    return int.TryParse(pack.Str, out battlePlayerId) && battlePlayerId > 0;

                case ActionCode.BattleSetNetSimConfig:
                    if (pack.BattleNetSimConfig == null)
                    {
                        return false;
                    }

                    battlePlayerId = pack.BattleNetSimConfig.BattlePlayerId;
                    return battlePlayerId > 0;

                default:
                    return false;
            }
        }

        // ==================== 发送路径 ====================

        /// <summary>
        /// 发送一个战斗包到指定端点。
        ///
        /// <para>
        /// **本方法永不抛出**。旧实现的发送异常会冒泡到战斗循环的外层 catch 并把循环置为停止，
        /// 导致整局静默失效（不发 GameOver、不注销路由）。这里改为返回 false + 计数。
        /// </para>
        /// </summary>
        /// <param name="endpointKey"><c>"ip:port"</c> 形式的端点键。</param>
        public bool Send(MainPack pack, string endpointKey)
        {
            if (pack == null || string.IsNullOrEmpty(endpointKey))
            {
                return false;
            }

            try
            {
                byte[] bytes = pack.ToByteArray();
                EndPoint target = ParseEndpoint(endpointKey);
                if (target == null)
                {
                    Interlocked.Increment(ref _sendErrors);
                    LogWarning("[PMUdpRouter] 端点格式非法，发送取消: " + endpointKey);
                    return false;
                }

                _socket.SendTo(bytes, target);
                Interlocked.Increment(ref _sentDatagrams);
                return true;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _sendErrors);
                LogWarning("[PMUdpRouter] 发送失败（endpoint=" + endpointKey + "）：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 解析 <c>"ip:port"</c>。用最后一个冒号切分，因此 IPv4 与带端口的 IPv6 字面量都能处理
        /// （旧实现用 <c>Split(":")</c> 只支持 IPv4）。
        /// </summary>
        private static EndPoint ParseEndpoint(string endpointKey)
        {
            int split = endpointKey.LastIndexOf(':');
            if (split <= 0 || split == endpointKey.Length - 1)
            {
                return null;
            }

            IPAddress address;
            if (!IPAddress.TryParse(endpointKey.Substring(0, split), out address))
            {
                return null;
            }

            int port;
            if (!int.TryParse(endpointKey.Substring(split + 1), out port) || port <= 0 || port > 65535)
            {
                return null;
            }

            return new IPEndPoint(address, port);
        }

        /// <summary>把端点格式化成路由键。与旧实现 <c>GetEndpointKey</c> 语义一致。</summary>
        public static string GetEndpointKey(EndPoint point)
        {
            if (point == null)
            {
                return null;
            }

            IPEndPoint ipEndPoint = point as IPEndPoint;
            if (ipEndPoint != null)
            {
                return ipEndPoint.Address + ":" + ipEndPoint.Port;
            }

            return point.ToString();
        }

        // ==================== 诊断 ====================

        /// <summary>一行汇总，供心跳日志使用。</summary>
        public string DescribeCounters()
        {
            return "recv=" + Interlocked.CompareExchange(ref _receivedDatagrams, 0, 0)
                + " queued=" + Interlocked.CompareExchange(ref _enqueuedDatagrams, 0, 0)
                + " dispatched=" + Interlocked.CompareExchange(ref _dispatchedPackets, 0, 0)
                + " sent=" + Interlocked.CompareExchange(ref _sentDatagrams, 0, 0)
                + " pending=" + PendingInboundCount
                + " parseErr=" + Interlocked.CompareExchange(ref _parseErrors, 0, 0)
                + " qFull=" + Interlocked.CompareExchange(ref _droppedQueueFull, 0, 0)
                + " unroutable=" + Interlocked.CompareExchange(ref _unroutablePackets, 0, 0)
                + " unroutedPing=" + Interlocked.CompareExchange(ref _unroutedPings, 0, 0)
                + " noHandler=" + Interlocked.CompareExchange(ref _noHandlerPackets, 0, 0)
                + " handlerErr=" + Interlocked.CompareExchange(ref _handlerErrors, 0, 0)
                + " sendErr=" + Interlocked.CompareExchange(ref _sendErrors, 0, 0);
        }

        // ---- 逐项计数器（供测试与诊断读取；全部无副作用）----

        /// <summary>收到的数据报数。</summary>
        public int ReceivedDatagrams()
        {
            return Interlocked.CompareExchange(ref _receivedDatagrams, 0, 0);
        }

        /// <summary>成功入队的数据报数。</summary>
        public int EnqueuedDatagrams()
        {
            return Interlocked.CompareExchange(ref _enqueuedDatagrams, 0, 0);
        }

        /// <summary>已分发给 handler 的包数。</summary>
        public int DispatchedPackets()
        {
            return Interlocked.CompareExchange(ref _dispatchedPackets, 0, 0);
        }

        /// <summary>protobuf 解析失败的次数。</summary>
        public int ParseErrors()
        {
            return Interlocked.CompareExchange(ref _parseErrors, 0, 0);
        }

        /// <summary>因队列满而丢弃的包数。</summary>
        public int QueueFullDrops()
        {
            return Interlocked.CompareExchange(ref _droppedQueueFull, 0, 0);
        }

        /// <summary>无法路由（建链失败或编号不匹配）的包数。</summary>
        public int UnroutablePackets()
        {
            return Interlocked.CompareExchange(ref _unroutablePackets, 0, 0);
        }

        /// <summary>路由到 battleId 但无 handler 的包数。</summary>
        public int NoHandlerPackets()
        {
            return Interlocked.CompareExchange(ref _noHandlerPackets, 0, 0);
        }

        /// <summary>来自未建链端点的 Ping 数（不应答，但需要可观测）。</summary>
        public int UnroutedPings()
        {
            return Interlocked.CompareExchange(ref _unroutedPings, 0, 0);
        }

        /// <summary>handler 抛出异常的次数。</summary>
        public int HandlerErrors()
        {
            return Interlocked.CompareExchange(ref _handlerErrors, 0, 0);
        }

        /// <summary>成功发送的数据报数。</summary>
        public int SentDatagrams()
        {
            return Interlocked.CompareExchange(ref _sentDatagrams, 0, 0);
        }

        /// <summary>发送失败次数（含端点非法）。</summary>
        public int SendErrors()
        {
            return Interlocked.CompareExchange(ref _sendErrors, 0, 0);
        }

        /// <summary>当前已建立的端点→战斗路由数。</summary>
        public int EndpointRouteCount()
        {
            lock (_routeLock)
            {
                return _endpointRoutes.Count;
            }
        }

        /// <summary>清空入站队列（仅限主线程在关停时调用）。返回被丢弃的包数。</summary>
        public int ClearInbound()
        {
            lock (_inboundLock)
            {
                int count = _inbound.Count;
                _inbound.Clear();
                return count;
            }
        }

        // ---- 日志包装：调用方注入的实现必须自身线程安全（本类可能在接收线程上调用）----

        private void LogInfo(string message)
        {
            if (_logInfo != null)
            {
                _logInfo(message);
            }
        }

        private void LogWarning(string message)
        {
            if (_logWarning != null)
            {
                _logWarning(message);
            }
        }

        private void LogError(string message)
        {
            if (_logError != null)
            {
                _logError(message);
            }
        }
    }
}
