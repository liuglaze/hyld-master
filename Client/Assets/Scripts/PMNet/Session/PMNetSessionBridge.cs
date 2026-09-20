using System;
using System.Collections.Generic;
using PMNet.Transport;

namespace PMNet.Session
{
    /// <summary>
    /// 每世界一个的会话协调者（R3-A2，契约 §4）。
    ///
    /// ## 职责
    ///   - 把「已认证连接」成对接进 **世界（M04）** 与 **复制（M06）**；
    ///   - 提供 <see cref="SendRpc"/>，作为生成产物
    ///     `PMNet.Generated.PMNetGeneratedRegistry.RemoteSender`
    ///     （签名 `Action&lt;PMNetObject, ushort, PMRpcWriter&gt;`）的绑定目标
    ///     —— 核心**不得**直接依赖尚不存在的生产生成类，所以这里只按描述符工作；
    ///   - 按契约次序驱动一帧：drain 传输 → 生命周期排队 → 复制 Tick 与版本 ACK → flush 传输；
    ///   - 持有生命周期与可靠 RPC 的「同流先 Create 后 RPC」次序。
    ///
    /// ## 交付类别
    ///   - `Server` RPC：只发给**本客户端已认证的那条服务器连接**；
    ///   - `Client` RPC：只发给权威 Owner 链上的那条连接；
    ///   - `Multicast`：发给**已拥有该副本且相关**的连接（逐连接判相关性，不是空间广播）。
    ///
    /// ## 刻意不做
    ///   - 不做第二套票据/密钥/防重放（那是 A1 与 R3-B 的连接接受器）；
    ///   - 不从业务包内自报的 uid 采纳所有权（所有权只来自已认证身份）；
    ///   - 不使用每世界 `static` 状态：本类是实例，可同时存在多个世界（测试并跑两个世界）。
    ///     静态的 `PMNetRegistry` 只作为**只读描述符来源**使用。
    /// </summary>
    public sealed class PMNetSessionBridge
    {
        /// <summary>
        /// 每帧每连接允许发出的数据报数（与既有 <see cref="PMTransportConfig.MaxDatagramsPerUpdate"/>
        /// 的默认值同源：32）。见 <see cref="Update"/> 的「次序与预算」说明。
        /// </summary>
        public const int DefaultFrameDatagramBudget = 32;

        private readonly PMNetWorld _world;
        private readonly PMReplicationChannel _replication;
        private readonly List<PMTransportConnection> _connections = new List<PMTransportConnection>(4);
        private readonly List<PMTransportConnection> _frameScratch = new List<PMTransportConnection>(4);
        private readonly List<PMTransportConnection> _rpcTargets = new List<PMTransportConnection>(4);
        private readonly PMNetWriter _envelopeScratch = new PMNetWriter(2048);

        private PMTransportConnection _serverConnection;
        private bool _disposed;

        /// <summary>日志出口。默认丢弃；宿主接到项目日志。</summary>
        public Action<string> Warn;

        /// <summary>每帧每连接的数据报预算（见 <see cref="Update"/>）。</summary>
        public int FrameDatagramBudget = DefaultFrameDatagramBudget;

        // ── 计数 ────────────────────────────────────────────────────────
        /// <summary>成功发送（至少一条连接接受）的 RPC 次数。</summary>
        public long RpcSent;

        /// <summary>因序列化（PMRpcWriter）抛异常而失败的次数。</summary>
        public long RpcEncodeFailed;

        /// <summary>因编码后超过应用消息上限而失败的次数。</summary>
        public long RpcOversize;

        /// <summary>目标对象已不是 Active（销毁后）而被拒绝的 RPC 次数。</summary>
        public long RpcRejectedInactive;

        /// <summary>按 (ClassId, RpcId) 查不到描述符而被拒绝的次数。</summary>
        public long RpcRejectedNoDescriptor;

        /// <summary>没有合法目标连接（方向非法 / 无拥有者连接 / 无相关连接）而被拒绝的次数。</summary>
        public long RpcRejectedNoConnection;

        /// <summary>找到了目标连接但对方未就绪而被拒绝的次数。</summary>
        public long RpcRejectedNotReady;

        /// <summary>发出但传输层拒绝接收的次数。</summary>
        public long RpcSendRejected;

        /// <summary>成功发出的生命周期消息（Create/Destroy 批次）次数。</summary>
        public long LifecycleMessagesSent;

        /// <summary>生命周期消息被传输层拒绝的次数。</summary>
        public long LifecycleSendFailed;

        /// <summary>生命周期消息超过应用消息上限的次数。</summary>
        public long LifecycleOversize;

        /// <summary>
        /// 因生命周期批次无法发出而**显式断开连接**的次数。
        ///
        /// 这不是丢包统计，而是「可靠消息发不出去」的显式失败凭证：
        /// 世界在返回批次前就清空了待发表，因此那一批 Create/Destroy 没有第二次机会。
        /// 宁可把这个连接拆了（重连后由世界的 AddConnection 重新排队全量 Create），
        /// 也不能让对端停在「世界里有对象、它却永远拿不到 Create」的状态上。
        /// </summary>
        public long LifecycleDisconnects;

        /// <summary>成功发出的复制属性确认消息次数。</summary>
        public long ReplicationAckMessagesSent;

        /// <summary>驱动过的帧数。</summary>
        public long Frames;

        /// <summary>因帧内数据报预算用尽而顺延 flush 的次数（消息仍留在传输层队列里，不丢）。</summary>
        public long DatagramsDeferredByBudget;

        /// <summary>被 <see cref="RegisterConnection"/> 拒绝的连接次数（客户端第二条服务器连接 / 服务端重复 uid 等）。</summary>
        public long RejectedConnections;

        /// <summary>断开后被成对摘除的连接次数。</summary>
        public long ClosedConnections;

        public PMNetSessionBridge(PMNetWorld world, PMReplicationChannel replication = null,
                                  PMRepOptions replicationOptions = null)
        {
            if (world == null) { throw new ArgumentNullException("world"); }

            _world = world;
            _replication = replication ?? new PMReplicationChannel(replicationOptions);
            _replication.World = world;
            _replication.Warn = WarnInternal;
        }

        /// <summary>本桥服务的世界。</summary>
        public PMNetWorld World { get { return _world; } }

        /// <summary>本桥的复制通道（每世界一个实例，非 static）。</summary>
        public PMReplicationChannel Replication { get { return _replication; } }

        /// <summary>本端是否为世界权威（**判据永远是世界，不是连接上的 IsServerSide**）。</summary>
        public bool IsServer { get { return _world.IsServer; } }

        /// <summary>登记在册的连接数。</summary>
        public int ConnectionCount { get { return _connections.Count; } }

        /// <summary>客户端侧已认证的服务器连接（服务端侧为 null）。</summary>
        public PMTransportConnection ServerConnection { get { return _serverConnection; } }

        /// <summary>按登记序取连接（诊断/门禁用）。</summary>
        public PMTransportConnection GetConnectionAt(int index)
        {
            if (index < 0 || index >= _connections.Count)
            {
                return null;
            }

            return _connections[index];
        }

        /// <summary>按 uid 找连接（服务端侧有用；找不到返回 null）。</summary>
        public PMTransportConnection FindConnectionByUid(int uid)
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                if (_connections[i].Identity.Uid == uid)
                {
                    return _connections[i];
                }
            }

            return null;
        }

        // =================================================================================
        //  登记（世界 + 复制成对加入/退出）
        // =================================================================================

        /// <summary>
        /// 把一条已激活的连接登记进世界与复制层。
        ///
        /// 接受规则（契约 §4）：
        ///   - **客户端**：只接受 <see cref="PMSessionPeerRole.Server"/> 角色的连接，且只允许一条；
        ///   - **服务端**：只接受 <see cref="PMSessionPeerRole.Client"/> 角色的连接，
        ///     ConnectionId 与 Uid 都必须唯一（「服务端只有激活玩家」）。
        /// </summary>
        public bool RegisterConnection(PMTransportConnection connection, out string error)
        {
            error = null;

            if (connection == null) { error = "连接为 null"; return false; }
            if (connection.World != _world) { error = "连接不属于本桥的世界"; return false; }
            if (!connection.IsActivated) { error = "连接尚未激活"; return false; }

            // 硬前置：登记进世界的连接必须是**活的**。否则断开回调不会再触发（传输层的 Disconnect 幂等），
            // 这条连接就永远留在世界/复制里（见 PMTransportConnection.TryActivate 的同类前置）。
            if (connection.Transport == null || !connection.Transport.IsConnected)
            {
                error = "传输层已断开，拒绝登记进世界/复制（否则无法自动摘除）";
                RejectedConnections++;
                return false;
            }

            for (int i = 0; i < _connections.Count; i++)
            {
                if (ReferenceEquals(_connections[i], connection))
                {
                    return true;
                }
            }

            if (IsServer)
            {
                if (connection.Identity.PeerRole != PMSessionPeerRole.Client)
                {
                    error = "服务端只接受客户端角色的连接（对端声明 " + connection.Identity.PeerRole + "）";
                    RejectedConnections++;
                    return false;
                }

                for (int i = 0; i < _connections.Count; i++)
                {
                    PMTransportConnection existing = _connections[i];

                    if (existing.ConnectionId == connection.ConnectionId)
                    {
                        error = "ConnectionId " + connection.ConnectionId + " 已被占用";
                        RejectedConnections++;
                        return false;
                    }

                    if (existing.Identity.Uid == connection.Identity.Uid)
                    {
                        error = "Uid " + connection.Identity.Uid + " 已有活跃连接（同一账号不得同时占两条连接）";
                        RejectedConnections++;
                        return false;
                    }
                }
            }
            else
            {
                if (connection.Identity.PeerRole != PMSessionPeerRole.Server)
                {
                    error = "客户端只接受服务器角色的连接（对端声明 " + connection.Identity.PeerRole + "）";
                    RejectedConnections++;
                    return false;
                }

                if (_serverConnection != null && !ReferenceEquals(_serverConnection, connection))
                {
                    error = "客户端已绑定受信服务器连接 " + _serverConnection.Name + "，拒绝第二条";
                    RejectedConnections++;
                    return false;
                }
            }

            _connections.Add(connection);
            if (!IsServer)
            {
                _serverConnection = connection;
            }

            // 成对进入：世界负责生命周期（Create/Destroy 队列），复制层负责基线。
            _world.AddConnection(connection);
            _replication.AddConnection(connection);
            return true;
        }

        /// <summary>把连接从世界与复制层成对摘除（幂等）。</summary>
        public void UnregisterConnection(PMTransportConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            if (!_connections.Remove(connection))
            {
                return;
            }

            if (ReferenceEquals(_serverConnection, connection))
            {
                _serverConnection = null;
            }

            _world.RemoveConnection(connection);
            _replication.RemoveConnection(connection);

            // 帧内快照可能仍持有它：判据由各阶段自己重新检查（IsReady / IsOpenForPump）。
            _frameScratch.Remove(connection);
        }

        /// <summary>连接断开时由连接回调（成对离开世界与复制）。</summary>
        internal void OnConnectionClosed(PMTransportConnection connection)
        {
            bool had = _connections.Contains(connection);
            UnregisterConnection(connection);
            if (had)
            {
                ClosedConnections++;
            }
        }

        // =================================================================================
        //  对象登记（世界与复制成对）
        // =================================================================================

        /// <summary>
        /// 把对象登记进复制层（只有服务端权威侧会产生更新）。
        ///
        /// 为什么放在桥里：对象登记必须与世界侧的 Spawn/Destroy 成对，
        /// 否则会出现「世界里有对象、复制层不知道」或反过来的悬空登记。
        /// 桥是唯一同时持有世界与复制层的对象，因此由它提供这个钩子。
        /// </summary>
        public void RegisterReplicatedObject(PMNetObject obj)
        {
            if (obj != null)
            {
                _replication.RegisterObject(obj);
            }
        }

        /// <summary>注销对象的复制登记（与 <see cref="DestroyObject"/> 成对；也可单独用于离开相关性集合）。</summary>
        public void UnregisterReplicatedObject(PMNetObject obj)
        {
            if (obj != null)
            {
                _replication.UnregisterObject(obj);
            }
        }

        /// <summary>
        /// 销毁一个权威对象：**先**走世界（排队 Destroy 生命周期记录），**再**注销复制登记。
        ///
        /// 次序不可换：世界的 `BuildLifecycleBatch` 需要用对象的 NetId 造销毁记录，
        /// 而复制层的注销会丢掉基线。反过来做会让「注销后世界还想造记录」这一窗口存在。
        /// </summary>
        public bool DestroyObject(PMNetObject obj, PMObjectDestroyReason reason = PMObjectDestroyReason.Destroyed)
        {
            if (obj == null || !IsServer)
            {
                return false;
            }

            if (!_world.DestroyObject(obj, reason))
            {
                return false;
            }

            _replication.UnregisterObject(obj);
            return true;
        }

        // =================================================================================
        //  每帧驱动
        // =================================================================================

        /// <summary>
        /// 按契约次序驱动一帧（契约 §4）：
        ///
        /// <code>
        ///   ① drain transport（处理入站：生命周期 / RPC / 复制 / 复制确认）
        ///   ② 生命周期排队（服务端为每条连接构造 Create/Destroy 批次，可靠域）
        ///   ③ 复制 Tick 与版本 ACK 发送（Replication 域）
        ///   ④ transport flush（把 ②③ 入队的东西真正发出去）
        /// </code>
        ///
        /// **为什么 ① 与 ④ 要调用两次 `PMTransport.Update`，以及为什么必须传预算**：
        /// 契约要求的帧内次序是「drain → … → flush」，而 M03 的 Update 内部固定「先 DrainInbound
        /// 再 FlushOutbound」。因此帧首调用一次（drain + 顺带 flush 上一帧遗留），帧尾再调一次
        /// （flush 本帧 ②③ 的产物）。
        ///
        /// 代价：`MaxDatagramsPerUpdate` 只约束**单次**调用。两次各自按自己的上限发，帧内总量就是
        /// `s + 32`（s 为帧首发量）；过去帧尾只在「本帧已发 >= 预算」时跳过，因此只要帧首还剩
        /// 一点余地（例如上一帧顺延、或丢包后的可靠重传），上界就是 31 + 32 = **63**，
        /// 而不是契约与报告里写的 32。
        ///
        /// 现在：帧首把「本帧总预算」写进连接，两次驱动都把 `RemainingFrameDatagramBudget`
        /// （总预算 − 本帧已发）传给传输层，从而帧内总量**严格**满足 `<= FrameDatagramBudget`。
        /// 超预算就把 flush 顺延到下一帧：消息留在传输层的有界队列里（可靠消息不丢），
        /// 计入 <see cref="DatagramsDeferredByBudget"/>。
        /// 真正的「drain-only / flush-only 拆分」现由传输层的
        /// `PMTransport.Update(now, sendBudget)` **兼容重载**覆盖（原 `Update(now)` 语义不变）。
        ///
        /// **迭代安全**：所有阶段都遍历帧内快照，并在每个连接上重新检查就绪状态；
        /// 入站处理可能引发断开（回调会从 `_connections` 摘除该连接），
        /// 因此绝不在 `_connections` 上直接迭代。
        /// </summary>
        public void Update(long nowMs)
        {
            if (_disposed)
            {
                return;
            }

            Frames++;

            // ── ① drain transport ────────────────────────────────────────
            SnapshotConnections();
            for (int i = 0; i < _frameScratch.Count; i++)
            {
                PMTransportConnection connection = _frameScratch[i];
                if (!connection.IsOpenForPump)
                {
                    continue;
                }

                // 帧首开始计账：本帧两次驱动共享 FrameDatagramBudget 这一个余量。
                connection.BeginFrameDatagramBudget(FrameDatagramBudget);
                connection.Update(nowMs, connection.RemainingFrameDatagramBudget);
            }

            // ── ② 生命周期排队 ───────────────────────────────────────────
            SnapshotConnections();
            for (int i = 0; i < _frameScratch.Count; i++)
            {
                FlushLifecycle(_frameScratch[i]);
            }

            // ── ③ 复制 Tick 与版本 ACK 发送 ──────────────────────────────
            _replication.Tick();

            SnapshotConnections();
            for (int i = 0; i < _frameScratch.Count; i++)
            {
                PMTransportConnection connection = _frameScratch[i];
                if (connection == null || !connection.IsReady)
                {
                    continue;
                }

                byte[] ack = _replication.BuildAckMessage(connection);
                if (ack == null)
                {
                    continue;
                }

                byte[] envelope = PMApplicationEnvelope.Wrap(_envelopeScratch,
                    PMSessionMessageKind.ReplicationAck, ack, 0, ack.Length);
                if (envelope == null)
                {
                    WarnInternal("复制确认消息超限（" + ack.Length + " 字节），已丢弃本次确认");
                    continue;
                }

                if (connection.SendReplicationAckEnvelope(envelope))
                {
                    ReplicationAckMessagesSent++;
                }
            }

            // ── ④ transport flush ───────────────────────────────────────
            SnapshotConnections();
            for (int i = 0; i < _frameScratch.Count; i++)
            {
                PMTransportConnection connection = _frameScratch[i];
                if (!connection.IsOpenForPump)
                {
                    continue;
                }

                int remaining = connection.RemainingFrameDatagramBudget;
                if (remaining <= 0)
                {
                    DatagramsDeferredByBudget++;
                    continue;
                }

                connection.Update(nowMs, remaining);
            }
        }

        private void SnapshotConnections()
        {
            _frameScratch.Clear();
            for (int i = 0; i < _connections.Count; i++)
            {
                _frameScratch.Add(_connections[i]);
            }
        }

        // =================================================================================
        //  生命周期
        // =================================================================================

        /// <summary>
        /// 为一条连接抽干世界里的生命周期待发事件，并发到**可靠域**。
        ///
        /// 与可靠 RPC 同域 ⇒ 「对象创建先于引用它的 RPC」由顺序域直接保证（D-R0-03），
        /// 不依赖任何跨域推断。
        ///
        /// **返回值语义**：true = 「可以继续」（本连接没有待发事件，或本批已成功交给传输层）；
        /// false = 本次失败且**已显式处理**（连接被断开），调用方不得再往这条连接发东西。
        /// 客户端侧（非权威侧）没有生命周期批次，视为「无需处理」⇒ 返回 true。
        ///
        /// **为什么失败必须显式断连，而不是记一条 Warn 了事**：
        /// `PMNetWorld.BuildLifecycleBatch` 在返回批次之前就已 `state.Pending.Clear()`，
        /// 所以这个批次是那些 Create/Destroy 的**最后一次机会**。发不出去时如果只计一个
        /// 计数 + 告警，对端就永远拿不到 Create —— 之后所有复制 Update 在那边被当作未知对象丢弃
        /// （`DroppedUnknownObject`），指向这些对象的 RPC 被 `UnknownTarget` 拒，
        /// 而 NetId 不复用、待发表已清空、无重发源 ⇒ **不可自愈**。
        /// 断连把「永久不一致」换成「显式失败」：重连后世界的 AddConnection 会为存活对象
        /// 重新排队全量 Create。
        ///
        /// **真正的「按字节预算分批」本轮做不了**：分批需要把剩余事件留在待发表里，
        /// 而那需要在世界的 `BuildLifecycleBatch` 上开一个字节预算出口（M04 边界，不在本轮写边界内）。
        /// 已作为待办登记在报告里。
        /// </summary>
        public bool FlushLifecycle(PMTransportConnection connection)
        {
            // 生命周期只由服务端权威侧产生；客户端侧没有待发批次，不是失败。
            if (!IsServer)
            {
                return true;
            }

            if (connection == null || !connection.IsReady)
            {
                return false;
            }

            byte[] batch = _world.BuildLifecycleBatch(connection);
            if (batch == null)
            {
                // 没有待发事件（世界侧已空）：不是失败。
                return true;
            }

            byte[] envelope = PMApplicationEnvelope.Wrap(_envelopeScratch, PMSessionMessageKind.Lifecycle,
                                                         batch, 0, batch.Length);
            if (envelope == null)
            {
                LifecycleOversize++;
                WarnInternal("生命周期批次 " + batch.Length + " 字节超过应用消息上限 "
                             + PMApplicationEnvelope.MaxMessageBytes + " 字节 ⇒ 显式断开连接 "
                             + connection.Name + "（不得静默丢弃已从待发表摘除的可靠数据）");
                DisconnectForLifecycleFailure(connection);
                return false;
            }

            if (connection.SendLifecycleEnvelope(envelope))
            {
                LifecycleMessagesSent++;
                return true;
            }

            LifecycleSendFailed++;
            if (connection.Transport != null && connection.Transport.IsConnected)
            {
                WarnInternal("生命周期批次发送失败 ⇒ 显式断开连接 " + connection.Name
                             + "（transport 判定 " + connection.Transport.LastSendRejectReason + "）");
                DisconnectForLifecycleFailure(connection);
            }

            return false;
        }

        /// <summary>
        /// 生命周期批次无法发出时的统一处置：断开这条连接并记账。
        /// 原因用专用值 <see cref="PMDisconnectReason.LifecycleBatchOversize"/>，
        /// 以免与传输层的「分片超限 / 超上限 / 可靠窗溢出」混为一谈。
        /// </summary>
        private void DisconnectForLifecycleFailure(PMTransportConnection connection)
        {
            LifecycleDisconnects++;
            connection.Disconnect(PMDisconnectReason.LifecycleBatchOversize);
        }

        // =================================================================================
        //  RPC 发送
        // =================================================================================

        /// <summary>
        /// 发送一条 RPC。签名与生成的
        /// `PMNet.Generated.PMNetGeneratedRegistry.RemoteSender`
        /// （`Action&lt;PMNetObject, ushort, PMRpcWriter&gt;`）逐参数对应，可直接绑定。
        ///
        /// 关键行为（契约 §4）：
        ///   - **立即序列化**：`write` 在本调用内同步执行完，超限当场失败，不做延迟编码；
        ///   - 描述符按 `(target.ClassId, rpcId)` 查，方向/可靠性/ParamLayoutId 全部来自它；
        ///   - 可靠 RPC 入 `Reliable` 域，且**先把该连接的 Create/Destroy 批次排进同一域**；
        ///   - 目标不是 Active（已 Destroy）时拒绝，不发送。
        /// </summary>
        public bool SendRpc(PMNetObject target, ushort rpcId, PMRpcWriter write)
        {
            if (target == null || write == null)
            {
                RpcEncodeFailed++;
                WarnInternal("SendRpc 收到 null 目标或 null 编码委托，已拒绝");
                return false;
            }

            if (!target.NetId.IsValid)
            {
                RpcRejectedInactive++;
                WarnInternal("SendRpc 的目标没有有效 NetId，已拒绝");
                return false;
            }

            if (target.State != PMNetObjectState.Active)
            {
                // 「Destroy 后 RPC 拒绝」：销毁即摘除索引，业务不该再往上发。
                RpcRejectedInactive++;
                WarnInternal("SendRpc 的目标（NetId " + target.NetId + "）状态为 " + target.State + "，已拒绝");
                return false;
            }

            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(target.ClassId, rpcId, out entry) || entry == null)
            {
                RpcRejectedNoDescriptor++;
                WarnInternal("SendRpc 找不到描述符：ClassId=" + target.ClassId + " RpcId=" + rpcId
                             + "（生成产物未注册或 ID 漂移）");
                return false;
            }

            _rpcTargets.Clear();
            string reason;
            ResolveRpcTargets(target, entry.Descriptor.Direction, _rpcTargets, out reason);
            if (_rpcTargets.Count == 0)
            {
                RpcRejectedNoConnection++;
                WarnInternal("SendRpc 没有合法目标连接（" + entry.Descriptor.MethodName + "）：" + reason);
                return false;
            }

            byte[] envelope = BuildRpcEnvelope(target, rpcId, entry.Descriptor.ParamLayoutId, write);
            if (envelope == null)
            {
                RpcOversize++;
                WarnInternal("SendRpc 编码后长度超过应用消息上限 " + PMApplicationEnvelope.MaxMessageBytes
                             + " 字节（" + entry.Descriptor.MethodName + "），已拒绝");
                return false;
            }

            bool reliable = entry.Descriptor.IsReliable;
            bool any = false;
            bool anyReady = false;

            for (int i = 0; i < _rpcTargets.Count; i++)
            {
                PMTransportConnection connection = _rpcTargets[i];
                if (connection == null || !connection.IsReady)
                {
                    continue;
                }

                anyReady = true;

                // 「Create 必须先入同一可靠流，再入 RPC」：可靠域内 Create 与 RPC 同流，
                // 因此这里先把该连接的生命周期批次排空，再发 RPC。
                // 若这一步失败（批次已从待发表摘除却发不出去），该连接已被显式断开：
                // 不再往它上面发一条指向「对端永远不知道的对象」的 RPC。
                if (!FlushLifecycle(connection))
                {
                    continue;
                }

                if (connection.SendRpcEnvelope(envelope, reliable))
                {
                    any = true;
                }
            }

            if (!any)
            {
                if (!anyReady)
                {
                    RpcRejectedNotReady++;
                }
                else
                {
                    RpcSendRejected++;
                }

                return false;
            }

            RpcSent++;
            return true;
        }

        private void ResolveRpcTargets(PMNetObject target, PMRpcKind direction,
                                       List<PMTransportConnection> outConnections, out string reason)
        {
            reason = null;

            switch (direction)
            {
                case PMRpcKind.Server:
                    // 服务端调用 Server RPC 时 callspace 会判为本地执行，根本不会走到这里。
                    if (IsServer)
                    {
                        reason = "服务端不得发出 Server RPC（callspace 应判为本地执行）";
                        return;
                    }

                    if (_serverConnection == null)
                    {
                        reason = "客户端尚未绑定受信服务器连接";
                        return;
                    }

                    if (!_serverConnection.IsReady)
                    {
                        reason = "受信服务器连接未就绪";
                        return;
                    }

                    outConnections.Add(_serverConnection);
                    return;

                case PMRpcKind.Client:
                    if (!IsServer)
                    {
                        reason = "客户端不得发出 Client RPC";
                        return;
                    }

                    {
                        string ownerReason;
                        PMTransportConnection owner = ResolveOwnerConnection(target, out ownerReason);
                        if (owner == null)
                        {
                            reason = ownerReason;
                            return;
                        }

                        outConnections.Add(owner);
                    }
                    return;

                case PMRpcKind.Multicast:
                    if (!IsServer)
                    {
                        reason = "客户端不得发出 Multicast RPC";
                        return;
                    }

                    for (int i = 0; i < _connections.Count; i++)
                    {
                        PMTransportConnection connection = _connections[i];
                        if (connection == null || !connection.IsReady)
                        {
                            continue;
                        }

                        // 「相关连接集合」：逐连接判相关性，不是空间广播（D-R0-44）。
                        if (!target.IsNetRelevantFor(connection, null))
                        {
                            continue;
                        }

                        outConnections.Add(connection);
                    }

                    if (outConnections.Count == 0)
                    {
                        reason = "没有已拥有该副本且相关的连接";
                    }
                    return;

                default:
                    reason = "未知的 RPC 方向 " + direction;
                    return;
            }
        }

        /// <summary>
        /// 求某对象的**唯一**权威拥有者连接。
        ///
        /// `OwnerConnection`（显式绑定）与 `GetNetConnection()`（UE Owner 链推导）都算合法来源，
        /// 但两者指向**不同连接**时本层拒绝发送：宿主只能指定一个权威来源
        /// （契约 §4：「不把 OwnerConnection 与 Owner 链两个冲突连接都视为拥有者」）。
        /// </summary>
        private PMTransportConnection ResolveOwnerConnection(PMNetObject target, out string reason)
        {
            reason = null;

            PMTransportConnection explicitOwner = AsRegisteredConnection(target.OwnerConnection);
            PMTransportConnection chainOwner = AsRegisteredConnection(target.GetNetConnection());

            if (explicitOwner != null && chainOwner != null && !ReferenceEquals(explicitOwner, chainOwner))
            {
                reason = "OwnerConnection(" + explicitOwner.Name + ") 与 Owner 链(" + chainOwner.Name
                         + ") 指向两条不同连接，宿主只能指定一个权威来源";
                return null;
            }

            PMTransportConnection owner = explicitOwner != null ? explicitOwner : chainOwner;
            if (owner == null)
            {
                reason = "对象没有本桥登记的权威拥有者连接";
                return null;
            }

            if (!owner.IsReady)
            {
                reason = "权威拥有者连接 " + owner.Name + " 未就绪";
                return null;
            }

            return owner;
        }

        private PMTransportConnection AsRegisteredConnection(PMNetConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            PMTransportConnection session = connection as PMTransportConnection;
            if (session == null)
            {
                // 宿主接了别的连接实现：本桥只能通过自己的适配器发信封，因此视为不可用。
                return null;
            }

            for (int i = 0; i < _connections.Count; i++)
            {
                if (ReferenceEquals(_connections[i], session))
                {
                    return session;
                }
            }

            return null;
        }

        private byte[] BuildRpcEnvelope(PMNetObject target, ushort rpcId, ushort paramLayoutId, PMRpcWriter write)
        {
            _envelopeScratch.Reset();
            PMApplicationEnvelope.WriteHeader(_envelopeScratch, PMSessionMessageKind.Rpc);

            // 正文：NetId + bStatic、ClassId、RpcId、ParamLayoutId、参数（契约 §4）。
            _envelopeScratch.WriteUInt64(target.NetId.Value);
            _envelopeScratch.WriteBool(target.NetId.IsStatic);
            _envelopeScratch.WriteUInt64(target.ClassId);
            _envelopeScratch.WriteUInt64(rpcId);
            _envelopeScratch.WriteUInt64(paramLayoutId);

            try
            {
                write(target, _envelopeScratch);
            }
            catch (Exception ex)
            {
                // 序列化必须在**调用点同步完成**：编码失败当场抛出（不静默、不延迟到发送时才炸）。
                RpcEncodeFailed++;
                throw new InvalidOperationException(
                    "PMNet RPC 参数序列化失败（ClassId=" + target.ClassId + " RpcId=" + rpcId
                    + "）：" + ex.GetType().Name + " " + ex.Message, ex);
            }

            if (_envelopeScratch.Length > PMApplicationEnvelope.EnforceableMaxBytes)
            {
                // 用「契约上限与传输上限的最小值」：否则长度落进 (65535, 65536] 的 RPC
                // 会在这里通过、在传输层被静默截断（见 MaxTransportableBytes 的说明）。
                return null;
            }

            return _envelopeScratch.ToArray();
        }

        // =================================================================================
        //  清理
        // =================================================================================

        /// <summary>释放桥：断开并释放全部连接（幂等，可重复调用）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SnapshotConnections();
            for (int i = 0; i < _frameScratch.Count; i++)
            {
                PMTransportConnection connection = _frameScratch[i];
                if (connection != null)
                {
                    connection.Dispose();
                }
            }

            _connections.Clear();
            _serverConnection = null;
        }

        private void WarnInternal(string message)
        {
            Action<string> handler = Warn;
            if (handler != null)
            {
                handler(message);
            }
        }

        public override string ToString()
        {
            return "PMNetSessionBridge(server=" + IsServer + " conns=" + _connections.Count
                   + " objects=" + _world.ObjectCount + " repObjects=" + _replication.ObjectCount + ")";
        }
    }

}
