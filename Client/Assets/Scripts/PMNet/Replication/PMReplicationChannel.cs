using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 属性复制层（M06 最小闭环）。
    ///
    /// ## 职责边界（对应 net-architecture-migration.md §3.9.3 的 M06 行）
    ///
    /// 本层只负责**权威状态的传递**：每连接基线、变更掩码比较、8 项复制条件、
    /// OnRep 分发、初始状态全量、有界队列。**不做**：距离裁剪/相关性调度、
    /// 频率/休眠、FastArray、预测回滚（分别属于 M06 后半 / M07/M08）。
    ///
    /// ## 五条被本实现钉死的契约
    ///
    /// 1. **每连接基线 + ACK 只能前进**（R0 §5）：`AckedVersion` 单调，
    ///    旧 ACK 既不回退版本，也不清除比它更新的脏位。
    /// 2. **标脏后仍要比较**（D-R0-13）：对象的脏位只表示"可能变了"，
    ///    真正决定发不发的是"当前值 vs 该连接已确认基线"的字节比较；值未变 ⇒ 掩码为空 ⇒ 不发。
    /// 3. **8 项复制条件**（D-R0-14）：按 legacy 口径求值（见 `PMRepConditions`）。
    /// 4. **条件跃迁全掩码**（D-R0-15）：条件由"不满足 → 满足"时强制把该属性补发一次，
    ///    否则新获得可见性的连接永远拿不到当前值。
    /// 5. **初始状态全量**（D-R0-12/16）：基线缺失（首次进入范围 / 新连接）⇒ 该属性全量发送。
    ///
    /// ## 决策点为什么是 virtual
    ///
    /// `IsAckStale` / `ShouldAdvanceWithoutInflight` / `TryClearSettledDirty` /
    /// `ShouldForceIncludeOnTransition` / `HasValueChangedSinceBaseline` 五处判定是本层的正确性
    /// 关键点，也是最容易写错的地方。把它们做成 `protected virtual`，使门禁可以用
    /// "缺陷子类"注入真实缺陷，证明断言确实抓得住（**负向验证**）
    /// —— 否则一组全绿的断言可能只是没牙。
    /// 生产代码不会覆写它们，默认实现即契约。
    ///
    /// RV2 返工时按同一思路补了两个：`DispatchOnRepPerProperty`（回调时机）与
    /// `IsolateOnRepExceptions`（回调异常隔离）—— 这两条的正确性同样只在运行期可见，
    /// 而且"边写边回调"与"回调异常穿透"都曾经是真实存在过的形态。
    ///
    /// ## 线程模型
    ///
    /// 与 M03/M04 一致：**所有方法只在主线程调用**，不加锁。
    ///
    /// ## 日志
    ///
    /// 本程序集零 Unity 依赖、也不引用项目日志模块（那会破坏 netstandard2.0 自包含），
    /// 因此统一走 <see cref="Warn"/> 回调，由宿主接到 `Logging.Debug.Log`。
    /// </summary>
    public class PMReplicationChannel
    {
        /// <summary>一条连接在本层里的记账（**发送侧**才需要）。</summary>
        private sealed class ConnectionBucket
        {
            public PMNetConnection Connection;

            /// <summary>NetId → 每(连接 × 对象)状态。</summary>
            public readonly Dictionary<uint, PMRepConnectionState> Objects = new Dictionary<uint, PMRepConnectionState>(16);
        }

        private readonly PMRepOptions _options;
        private readonly List<PMNetConnection> _connectionOrder = new List<PMNetConnection>(4);
        private readonly Dictionary<PMNetConnection, ConnectionBucket> _buckets = new Dictionary<PMNetConnection, ConnectionBucket>(4);

        /// <summary>
        /// 接收侧待回传的确认：**来源连接 → 待确认列表**。
        ///
        /// 与发送侧的 <see cref="ConnectionBucket"/> 分开保存："我要往它发"与"我从它收"是两件事，
        /// 合并会让一个纯接收端在 <see cref="Tick"/> 里尝试往上游发数据。
        /// </summary>
        private readonly Dictionary<PMNetConnection, List<PMRepAck>> _ackQueues = new Dictionary<PMNetConnection, List<PMRepAck>>(4);
        private readonly List<PMNetObject> _objects = new List<PMNetObject>(64);
        private readonly Dictionary<PMNetObject, PMRepObjectEntry> _entries = new Dictionary<PMNetObject, PMRepObjectEntry>(64);
        private readonly Dictionary<uint, bool> _warnedNoDescriptor = new Dictionary<uint, bool>(8);
        private readonly Dictionary<uint, Action<PMNetObject, ushort>> _onRepDispatchers = new Dictionary<uint, Action<PMNetObject, ushort>>(8);

        private readonly PMNetWriter _writer = new PMNetWriter(1024);
        private readonly PMNetWriter _valueWriter = new PMNetWriter(64);
        private readonly List<PMRepUpdateRecord> _recordScratch = new List<PMRepUpdateRecord>(64);
        private readonly List<PMRepUpdateRecord> _payloadRecords = new List<PMRepUpdateRecord>(16);
        private readonly List<byte[]> _payloadScratch = new List<byte[]>(4);
        private readonly PMRepMessage _inbound = new PMRepMessage();

        private int[] _slotScratch = new int[16];
        private byte[][] _valueScratch = new byte[16][];
        private bool[] _condScratch = new bool[16];

        private PMRepStats _stats;

        /// <summary>构造。</summary>
        /// <param name="options">边界参数；null 用默认值。</param>
        public PMReplicationChannel(PMRepOptions options = null)
        {
            _options = options ?? PMRepOptions.Default();
        }

        /// <summary>本世界（接收侧解析对象用；发送侧可以不设）。</summary>
        public PMNetWorld World;

        /// <summary>告警回调。默认丢弃；宿主接到项目日志（`Logging.Debug.Log`）。</summary>
        public Action<string> Warn;

        /// <summary>
        /// 角色解析覆盖（表驱动测试用）。
        /// 默认实现见 <see cref="ResolveViewRole"/>：null 连接 ⇒ 权威；拥有者 ⇒ 自治；其余 ⇒ 模拟。
        /// </summary>
        public Func<PMNetObject, PMNetConnection, PMRepViewRole> ViewRoleResolver;

        /// <summary>边界参数。</summary>
        public PMRepOptions Options { get { return _options; } }

        /// <summary>累计统计。</summary>
        public PMRepStats Stats { get { return _stats; } }

        // ---------------------------------------------------------------------------------
        //  契约/接线错误指标（RV2 返工新增）
        //
        //  刻意**不放进 PMRepStats**：那一组是"线协议统计"（发了多少、丢了多少、Ack 如何），
        //  这三个说的是"本端接到了违规的描述符/工厂/回调" —— 它们不是对端发了坏包，
        //  而是**本端接线**出了问题（Reader 不纯、工厂不产出新对象、表现函数抛异常）。
        //  混在一起会让"网络不干净"与"代码接错了"无法区分。
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// OnRep 回调抛异常的累计次数。
        ///
        /// 与协议失败**分离**：抛异常的那条记录已经完整提交，因此照常回 ACK、不重放；
        /// 本计数只表示"表现层炸了"。为 0 且 <see cref="PMRepStats.OnRepUnhandled"/> 为 0
        /// 才说明表现层全绿。
        /// </summary>
        public long OnRepExceptions;

        /// <summary>
        /// 提交阶段（写入活对象）失败的累计次数 —— 即 Reader 在暂存对象上成功、
        /// 在活对象上却抛异常（违反"Reader 必须是纯字段写入"的描述符契约）。
        ///
        /// 这种情形下**已经有一部分属性落在活对象上**：本层只能如实暴露（计数 + 告警 + 不回 ACK），
        /// 不能宣称原子性。详见 <see cref="TryApplyRecord"/> 的"诚实口径"注释。
        /// </summary>
        public long CommitFailures;

        /// <summary>
        /// 构造工厂返回"活对象本身 / 已登记对象"而被拒绝的累计次数。
        ///
        /// 暂存解码（原子性的前提）要求工厂产出**全新、未登记**的对象；
        /// 若它返回的正是活对象，暂存验证就等于在活对象上写 —— 验证失败时活对象已被污染，
        /// 原子性完全失效。这类工厂是接线错误，本层当场拒绝整条记录。
        /// </summary>
        public long StagingRejected;

        /// <summary>当前登记的连接数。</summary>
        public int ConnectionCount { get { return _connectionOrder.Count; } }

        /// <summary>当前登记的对象数。</summary>
        public int ObjectCount { get { return _objects.Count; } }

        // =================================================================================
        //  登记
        // =================================================================================

        /// <summary>
        /// 登记一条连接。登记后该连接才会被复制调度，并获得自己独立的基线。
        ///
        /// 新连接 ⇒ 每对象 `UnknownBaselineCount == SlotCount` ⇒ 首次投递即全量（D-R0-12）。
        /// </summary>
        public void AddConnection(PMNetConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            if (_buckets.ContainsKey(connection))
            {
                return;
            }

            ConnectionBucket bucket = new ConnectionBucket();
            bucket.Connection = connection;
            _buckets.Add(connection, bucket);
            _connectionOrder.Add(connection);

            // 世界在造 Create 记录时也需要按"接收连接"求属性条件，而条件求值（角色解析 +
            // Custom/Dynamic 覆盖）的唯一事实源在本层。因此在这里把入口接上：
            // Create 可能早于本层首次 Tick（甚至世界只造包不调度），所以绝不能等到 Tick 才接。
            TryInstallWorldConditionSource();
        }

        /// <summary>注销连接（连同它的全部基线：重连必须重新全量，不能沿用旧基线）。</summary>
        public void RemoveConnection(PMNetConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            if (!_buckets.Remove(connection))
            {
                return;
            }

            _connectionOrder.Remove(connection);
        }

        /// <summary>登记一个参与复制的对象。对象必须在世界中处于 Active。</summary>
        public void RegisterObject(PMNetObject obj)
        {
            if (obj == null)
            {
                return;
            }

            for (int i = 0; i < _objects.Count; i++)
            {
                if (ReferenceEquals(_objects[i], obj))
                {
                    return;
                }
            }

            _objects.Add(obj);

            TryInstallWorldConditionSource();
        }

        /// <summary>注销对象（销毁时调用）：同时丢掉所有连接对它的基线。</summary>
        public void UnregisterObject(PMNetObject obj)
        {
            if (obj == null)
            {
                return;
            }

            _objects.Remove(obj);
            _entries.Remove(obj);

            if (obj.NetId.IsValid)
            {
                uint netId = obj.NetId.Value;
                for (int i = 0; i < _connectionOrder.Count; i++)
                {
                    ConnectionBucket bucket;
                    if (_buckets.TryGetValue(_connectionOrder[i], out bucket))
                    {
                        bucket.Objects.Remove(netId);
                    }
                }
            }
        }

        /// <summary>注册某类的 OnRep 分发器（生成代码的 `PMNet_OnRepDispatch` 挂到这里）。</summary>
        public void RegisterOnRepDispatcher(uint classId, Action<PMNetObject, ushort> dispatcher)
        {
            if (classId == 0u || dispatcher == null)
            {
                return;
            }

            _onRepDispatchers[classId] = dispatcher;
        }

        // =================================================================================
        //  条件与基线失效的外部入口
        // =================================================================================

        /// <summary>
        /// 打开/关闭 `COND_Custom` 的运行期开关（C-15：**每对象共享**，不是逐连接）。
        /// 打开时对所有已建基线的连接强制补发一次当前值。
        /// </summary>
        public bool SetCustomConditionActive(PMNetObject obj, int slot, bool active)
        {
            PMRepObjectEntry entry = ResolveEntry(obj);
            if (entry == null || slot < 0 || slot >= entry.SlotCount)
            {
                return false;
            }

            entry.CustomActive[slot] = active;

            if (active)
            {
                RequireSendOnAllConnections(entry, slot);
            }

            return true;
        }

        /// <summary>
        /// 改写 `COND_Dynamic` 属性的运行期条件（C-16）。
        /// 按契约必须**失效基线**：这里对已有连接强制补发一次当前值。
        /// </summary>
        public bool SetDynamicCondition(PMNetObject obj, int slot, PMCond condition)
        {
            if (!PMRepConditions.IsSupported(condition))
            {
                WarnInternal("Dynamic 条件被改写成未实现的条件 " + condition + "（D-R0-14 只支持 8 项），已拒绝");
                return false;
            }

            PMRepObjectEntry entry = ResolveEntry(obj);
            if (entry == null || slot < 0 || slot >= entry.SlotCount)
            {
                return false;
            }

            entry.Dynamic[slot] = condition;
            RequireSendOnAllConnections(entry, slot);
            return true;
        }

        private void RequireSendOnAllConnections(PMRepObjectEntry entry, int slot)
        {
            if (entry.Object == null || !entry.Object.NetId.IsValid)
            {
                return;
            }

            uint netId = entry.Object.NetId.Value;
            for (int i = 0; i < _connectionOrder.Count; i++)
            {
                ConnectionBucket bucket;
                if (!_buckets.TryGetValue(_connectionOrder[i], out bucket))
                {
                    continue;
                }

                PMRepConnectionState state;
                if (bucket.Objects.TryGetValue(netId, out state))
                {
                    state.RequireSend(slot);
                }
            }
        }

        // =================================================================================
        //  发送侧
        // =================================================================================

        /// <summary>
        /// 为一条连接组装本轮的更新载荷（不发送）。
        ///
        /// 每个载荷携带的属性总数不超过 <see cref="PMRepOptions.MaxPropertiesPerUpdate"/>；
        /// 超过则拆成多个载荷（而不是产生一个超大包）。
        /// </summary>
        /// <param name="connection">目标连接。</param>
        /// <param name="outPayloads">输出（会先被清空）。</param>
        /// <returns>本次组装出的对象更新记录数。</returns>
        public int BuildUpdate(PMNetConnection connection, List<byte[]> outPayloads)
        {
            if (outPayloads == null)
            {
                throw new ArgumentNullException("outPayloads");
            }

            outPayloads.Clear();

            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return 0;
            }

            _recordScratch.Clear();

            int scheduled = 0;
            for (int i = 0; i < _objects.Count; i++)
            {
                PMNetObject obj = _objects[i];
                if (obj == null)
                {
                    continue;
                }

                if (obj.State != PMNetObjectState.Active || !obj.NetId.IsValid)
                {
                    _stats.SkippedInactiveObject++;
                    continue;
                }

                PMRepObjectEntry entry = ResolveEntry(obj);
                if (entry == null)
                {
                    continue;
                }

                PMRepConnectionState state = GetOrCreateState(bucket, entry);

                bool anyTransition;
                if (!NeedsWork(entry, state, obj, out anyTransition))
                {
                    continue;
                }

                // 调度预算（C-37）：超限**顺延**而不是丢弃。
                // 顺延期间不改 ConditionActive，因此条件跃迁会在下一轮被重新检出，不会漏。
                if (scheduled >= _options.MaxObjectsPerConnectionPerTick)
                {
                    _stats.DeferredByBudget++;
                    continue;
                }

                scheduled++;
                ScanAndAppend(entry, state, obj, _recordScratch);
            }

            if (_recordScratch.Count == 0)
            {
                return 0;
            }

            int recordCount = _recordScratch.Count;
            int accumulated = 0;
            _payloadRecords.Clear();

            for (int i = 0; i < _recordScratch.Count; i++)
            {
                int props = _recordScratch[i].Slots == null ? 0 : _recordScratch[i].Slots.Length;
                if (_payloadRecords.Count > 0 && accumulated + props > _options.MaxPropertiesPerUpdate)
                {
                    FlushPayload(outPayloads);
                    accumulated = 0;
                }

                _payloadRecords.Add(_recordScratch[i]);
                accumulated += props;
            }

            FlushPayload(outPayloads);
            return recordCount;
        }

        /// <summary>
        /// 对所有已登记连接各组装一次并发送（不可靠域：属性状态不需要重传保证，
        /// 下一次比较自然会带上未确认的值）。
        /// </summary>
        /// <returns>实际发出的载荷数。</returns>
        public int Tick()
        {
            int sent = 0;

            TryInstallWorldConditionSource();

            for (int i = 0; i < _connectionOrder.Count; i++)
            {
                PMNetConnection connection = _connectionOrder[i];

                // 未就绪的连接**跳过组装**：否则版本号与在途记录会被提前消耗，
                // 值却永远发不出去（连接就绪后那些版本已经"用掉"了）。
                if (connection == null || !connection.IsReady)
                {
                    continue;
                }

                BuildUpdate(connection, _payloadScratch);

                for (int k = 0; k < _payloadScratch.Count; k++)
                {
                    connection.Send(_payloadScratch[k], PMRpcReliability.Unreliable);
                    _stats.UpdateMessagesSent++;
                    sent++;
                }

                _payloadScratch.Clear();
            }

            return sent;
        }

        /// <summary>
        /// 丢失提醒（由传输层在收到丢包通知时调用；对应 Iris 的 `HandleDroppedRecord`）。
        ///
        /// 作用：把那一条在途记录从账本里去掉，腾出在途容量，让该值在下一轮重新参与发送。
        /// 没有这层通知也不影响最终收敛（本层对未确认的值会持续重发），
        /// 它的价值在持续丢包时更快回收容量，避免对象被在途上限卡住。
        /// </summary>
        /// <returns>true = 确实存在该版本的在途记录并被移除。</returns>
        public bool OnLoss(PMNetConnection connection, uint netId, long version)
        {
            _stats.LossReports++;

            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                _stats.UnknownLossReports++;
                return false;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                _stats.UnknownLossReports++;
                return false;
            }

            for (int i = 0; i < state.Inflight.Count; i++)
            {
                if (state.Inflight[i].Version == version)
                {
                    state.Inflight.RemoveAt(i);
                    return true;
                }
            }

            _stats.UnknownLossReports++;
            return false;
        }

        /// <summary>取某（连接 × 对象）当前在途记录的版本（升序），用于诊断与丢包上报。</summary>
        public long[] GetInflightVersions(PMNetConnection connection, uint netId)
        {
            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return new long[0];
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                return new long[0];
            }

            long[] result = new long[state.Inflight.Count];
            for (int i = 0; i < state.Inflight.Count; i++)
            {
                result[i] = state.Inflight[i].Version;
            }

            return result;
        }

        private void FlushPayload(List<byte[]> outPayloads)
        {
            if (_payloadRecords.Count == 0)
            {
                return;
            }

            _writer.Reset();
            PMReplicationWriter.WriteUpdate(_writer, _payloadRecords);
            outPayloads.Add(_writer.ToArray());
            _payloadRecords.Clear();
        }

        private bool NeedsWork(PMRepObjectEntry entry, PMRepConnectionState state, PMNetObject obj, out bool anyTransition)
        {
            anyTransition = false;

            if (entry.HasConditional)
            {
                EnsureScratch(entry.SlotCount);

                bool isOwner;
                bool isSimulated;
                ResolveFlags(obj, state.Connection, out isOwner, out isSimulated);

                for (int slot = 0; slot < entry.SlotCount; slot++)
                {
                    PMCond raw = entry.Descriptor.Properties[slot].Condition;
                    bool met = entry.EvaluateSlot(slot, isOwner, isSimulated);
                    _condScratch[slot] = met;

                    if (met && !state.ConditionActive[slot] && PMRepConditions.RequiresEvaluation(raw))
                    {
                        anyTransition = true;
                    }
                }
            }

            if (state.UnknownBaselineCount > 0)
            {
                return true;
            }

            if (state.ForceIncludeCount > 0)
            {
                return true;
            }

            if (obj.Dirty.HasAny)
            {
                return true;
            }

            return anyTransition;
        }

        private void ScanAndAppend(PMRepObjectEntry entry, PMRepConnectionState state, PMNetObject obj, List<PMRepUpdateRecord> records)
        {
            PMReplicationDescriptor desc = entry.Descriptor;
            int slotCount = entry.SlotCount;
            EnsureScratch(slotCount);

            bool initialFull = state.UnknownBaselineCount > 0;
            int count = 0;

            for (int slot = 0; slot < slotCount; slot++)
            {
                PMPropertyDescriptor prop = desc.Properties[slot];
                bool met = entry.HasConditional ? _condScratch[slot] : true;

                if (entry.HasConditional)
                {
                    // D-R0-15：条件由"不满足 → 满足"时把该属性全部掩码位置脏。
                    if (met && !state.ConditionActive[slot]
                        && PMRepConditions.RequiresEvaluation(prop.Condition)
                        && ShouldForceIncludeOnTransition(slot))
                    {
                        state.RequireSend(slot);
                    }

                    state.ConditionActive[slot] = met;
                }

                if (!met)
                {
                    // 条件不满足：只过滤**脏位**，值仍留在对象里（C-18）。
                    // 将来条件变为满足时由上面的跃迁补偿补发，因此这里不能丢值。
                    _stats.ConditionFiltered++;
                    continue;
                }

                if (prop.Writer == null || prop.Reader == null)
                {
                    _stats.ProtocolErrors++;
                    WarnInternal("属性槽位 " + slot + "（" + prop.MemberName + "）的 Writer/Reader 缺失，已跳过");
                    continue;
                }

                byte[] current = SerializeProperty(prop, obj);
                if (current == null)
                {
                    _stats.ProtocolErrors++;
                    WarnInternal("属性槽位 " + slot + "（" + prop.MemberName + "）序列化出 0 字节，声明有误，已跳过");
                    continue;
                }

                byte[] baseline = state.Baseline[slot];
                bool force = state.ForceInclude[slot];

                bool changed;
                if (baseline == null)
                {
                    // 初始状态 / 基线缺失 ⇒ 全量（D-R0-12 / D-R0-16）。
                    changed = true;
                }
                else if (force)
                {
                    // 条件跃迁 / 动态条件改写的补偿性重发：不比较，强制带上当前值。
                    changed = true;
                }
                else
                {
                    changed = HasValueChangedSinceBaseline(obj, desc, slot, current, baseline);
                }

                if (!changed)
                {
                    // D-R0-13：标脏只表示"可能变了"；值未变 ⇒ 掩码为空 ⇒ 不发数据。
                    _stats.SuppressedUnchanged++;
                    continue;
                }

                // 已决定要发这个槽位。注意：这里**不**清它的脏位 —— 脏位的生命周期由
                // "是否已被确认"决定（见 TryClearSettledDirty）：若在此清掉，
                // 另一条还没拿到该值的连接就会永远看不到这次修改。
                _slotScratch[count] = slot;
                _valueScratch[count] = current;
                count++;
            }

            if (count == 0)
            {
                // 本轮没有任何属性需要发（典型：标脏了但值没变）。
                // 这正好是"已确认状态追平"的时机，顺手把已经追平的脏位清掉，
                // 否则对象会永远停在"待比较"集合里，每轮白扫一遍。
                TryClearSettledDirty(obj, state);
                return;
            }

            int offset = 0;
            while (offset < count)
            {
                if (state.Inflight.Count >= _options.MaxInflightPerObject)
                {
                    // 有界（D-R0-18）：在途满则顺延。未发出的槽位保留其"强制补发"标记，
                    // 脏位也仍在对象上，因此下一轮会被重新比较 —— 不丢。
                    _stats.DeferredByInflight++;
                    break;
                }

                int take = count - offset;
                if (take > _options.MaxPropertiesPerUpdate)
                {
                    take = _options.MaxPropertiesPerUpdate;
                }

                long version = state.NextSendVersion++;
                int[] slots = new int[take];
                ushort[] propertyIds = new ushort[take];
                byte[][] values = new byte[take][];

                for (int k = 0; k < take; k++)
                {
                    int slot = _slotScratch[offset + k];
                    slots[k] = slot;
                    propertyIds[k] = desc.Properties[slot].PropertyId;
                    values[k] = _valueScratch[offset + k];

                    if (state.ForceInclude[slot])
                    {
                        state.ClearRequireSend(slot);
                        _stats.TransitionForceSends++;
                    }
                }

                state.Inflight.Add(new PMRepInflight(version, slots, values));
                records.Add(new PMRepUpdateRecord(obj.NetId.Value, version, slots, propertyIds, values));

                _stats.UpdateRecordsSent++;
                _stats.PropertiesSent += take;

                offset += take;
            }

            if (initialFull)
            {
                _stats.InitialFullSends++;
            }
        }

        private byte[] SerializeProperty(PMPropertyDescriptor prop, PMNetObject obj)
        {
            _valueWriter.Reset();
            prop.Writer(obj, _valueWriter);

            if (_valueWriter.Length == 0)
            {
                return null;
            }

            return _valueWriter.ToArray();
        }

        /// <summary>
        /// 决策点：值相对该连接已确认基线是否发生了变化。
        ///
        /// 默认实现是**逐字节比较**（D-R0-13 的"标脏后仍要比较"）：对象的脏位只说明
        /// "可能变了"，真正决定发不发的是这里的比较结果。值未变 ⇒ 掩码为空 ⇒ 不发数据。
        /// </summary>
        protected virtual bool HasValueChangedSinceBaseline(PMNetObject obj, PMReplicationDescriptor descriptor, int slot, byte[] current, byte[] baseline)
        {
            return !PMRepConnectionState.BytesEqual(current, baseline);
        }

        /// <summary>
        /// 决策点：条件由"不满足 → 满足"时，是否把该属性的掩码位强制置脏（D-R0-15）。
        ///
        /// 默认 true。漏掉这一条的表现是"新获得可见性的连接永远拿不到当前值"，
        /// 而且只在条件真的发生过跃迁时才复现，属于最难查的一类。
        /// </summary>
        protected virtual bool ShouldForceIncludeOnTransition(int slot)
        {
            return true;
        }

        // =================================================================================
        //  确认（ACK）—— "ACK 只能前进，旧 ACK 不得清除新脏位"
        // =================================================================================

        /// <summary>
        /// 处理一条送达确认。
        ///
        /// 三条规则：
        ///   1. 版本**只能前进**（`IsAckStale`）—— 过期 Ack 整条忽略，连基线都不碰；
        ///   2. 基线推进到**该版本在途记录里的值**（而不是"当前值"）；
        ///   3. 脏位只在"所有连接都已追平该属性的当前值"时才清除（`TryClearSettledDirty`）。
        /// 第 3 条是 R0 §5「旧 ACK 不得清除新脏位」的落地方式：Ack 只覆盖它送出的那一版，
        /// 之后发生的修改不可能被它确认，因此不会被清掉。
        /// </summary>
        public void OnAck(PMNetConnection connection, uint netId, long version)
        {
            _stats.AcksReceived++;

            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                _stats.AckUnknownConnection++;
                return;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                _stats.AckUnknownObject++;
                return;
            }

            if (IsAckStale(state.AckedVersion, version))
            {
                _stats.StaleAckIgnored++;
                return;
            }

            PMRepInflight batch = state.FindInflight(version);
            if (batch == null && !ShouldAdvanceWithoutInflight(version))
            {
                // 水位比已确认版本更新，却找不到对应在途记录：只能来自伪造/重放/错配。
                // **绝不盲目推进水位**：推进会让后续**真实** ACK 被判过期，
                // 基线就永远追不上（表现为"值一直在发却永远不确认"），且容量账本被提前释放。
                _stats.AckWithoutInflight++;
                WarnInternal("收到版本 " + version + " 的 ACK，但（连接×对象 " + netId
                             + "）没有对应在途记录：拒绝推进确认水位");
                return;
            }

            if (batch != null)
            {
                for (int k = 0; k < batch.Slots.Length; k++)
                {
                    state.SetBaselineValue(batch.Slots[k], batch.Values[k]);
                }

                state.HasBaseline = state.UnknownBaselineCount <= 0;
            }

            state.DropInflightUpTo(version);
            state.AckedVersion = version;

            TryClearSettledDirty(state.Entry.Object, state);
        }

        /// <summary>
        /// 决策点：当 ACK 指向的版本比已确认水位新、却**找不到对应在途记录**时，
        /// 是否仍然推进确认水位。默认：**不推进**。
        ///
        /// 为何默认不推进：在途记录只在"收到它的 ACK"或"传输层丢包通知"时移除，
        /// 因此"水位更新却无记录"在正常路径下不可能出现（伪造 / 重放 / 两端错配）。
        /// 盲目推进会把水位推到一个本端从未发送过的版本，使后续**真实** ACK 被判过期
        /// （基线永远追不上），并提前释放容量账本。
        ///
        /// 做成 `protected virtual` 是为了让门禁能注入"旧实现盲目推进"这一真实缺陷
        /// （负向验证）—— 否则这条新防护的存在性与有效性都无法被证明。
        /// </summary>
        protected virtual bool ShouldAdvanceWithoutInflight(long version)
        {
            return false;
        }

        /// <summary>
        /// 判定 Ack 是否过期。默认：**只能前进**（`version &lt;= AckedVersion` 即过期）。
        /// 「旧 ACK 不得清除新脏位」的第一道闸门就是这里。
        /// </summary>
        protected virtual bool IsAckStale(long ackedVersion, long incomingVersion)
        {
            return incomingVersion <= ackedVersion;
        }

        /// <summary>
        /// 脏位清理（Ack 之后、以及"本轮无可发属性"时调用）。
        /// 默认：只有当**所有连接**都已追平该属性的**当前值**时才清。
        ///
        /// 与"条件"的相互作用：条件不满足的连接不要求拥有该值（它看不到），
        /// 因此不计入"追平"判定；将来条件由不满足变满足时，由 D-R0-15 的跃迁补偿补发。
        /// </summary>
        protected virtual void TryClearSettledDirty(PMNetObject obj, PMRepConnectionState state)
        {
            if (obj == null)
            {
                return;
            }

            PMRepObjectEntry entry = state.Entry;

            // 位宽契约：PMDirtyTracker 与 PMRepMask 同为 256 位，因此可清范围就是
            // [0, SlotCount)（ResolveEntry 已拒绝超过 MaxBits 的描述符）。
            // 历史实现把上限定成 min(SlotCount, 64)，于是位 [64, SlotCount) 与
            // MarkAll 置上的伪位永远清不掉 ⇒ 对象终身每 Tick 空扫。
            int limit = entry.SlotCount;
            if (limit > PMDirtyTracker.MaxBits)
            {
                limit = PMDirtyTracker.MaxBits;
            }

            bool anyRealDirtyRemaining = false;
            for (int slot = 0; slot < limit; slot++)
            {
                if (!obj.Dirty.IsDirty(slot))
                {
                    continue;
                }

                if (!IsSlotFullyConfirmed(entry, slot))
                {
                    anyRealDirtyRemaining = true;
                    continue;
                }

                obj.Dirty.ClearBit(slot);
            }

            // `MarkAllPropertiesDirty()` / 休眠唤醒（`FlushNetDormancy`）会把
            // [SlotCount, 256) 也置上 —— 那里根本没有属性。真实属性全部收敛后，
            // 必须把这段"非属性伪位"清掉：否则 `HasAny` 永远为真，
            // 对象就永久留在待比较集合里（每 Tick 全槽位序列化 + 比字节）。
            if (!anyRealDirtyRemaining && limit < PMDirtyTracker.MaxBits)
            {
                obj.Dirty.ClearBitsAbove(limit);
            }
        }

        private bool IsSlotFullyConfirmed(PMRepObjectEntry entry, int slot)
        {
            PMNetObject obj = entry.Object;
            if (obj == null || !obj.NetId.IsValid)
            {
                return false;
            }

            PMPropertyDescriptor prop = entry.Descriptor.Properties[slot];
            if (prop.Writer == null)
            {
                return false;
            }

            byte[] current = SerializeProperty(prop, obj);
            if (current == null)
            {
                return false;
            }

            uint netId = obj.NetId.Value;

            for (int i = 0; i < _connectionOrder.Count; i++)
            {
                ConnectionBucket bucket;
                if (!_buckets.TryGetValue(_connectionOrder[i], out bucket))
                {
                    continue;
                }

                PMRepConnectionState other;
                if (!bucket.Objects.TryGetValue(netId, out other))
                {
                    return false;
                }

                bool isOwner;
                bool isSimulated;
                ResolveFlags(obj, other.Connection, out isOwner, out isSimulated);

                if (!entry.EvaluateSlot(slot, isOwner, isSimulated))
                {
                    continue;
                }

                byte[] baseline = other.Baseline[slot];
                if (baseline == null)
                {
                    return false;
                }

                if (!PMRepConnectionState.BytesEqual(baseline, current))
                {
                    return false;
                }
            }

            return true;
        }

        // =================================================================================
        //  接收侧
        // =================================================================================

        /// <summary>
        /// 消费一条复制层消息（Update 或 Ack）。
        ///
        /// 解析失败 ⇒ **整条丢弃**并计告警，不做部分采纳（半采纳会让接收端停在
        /// "发送端从未存在过的状态"，正是 C-7 警告的那一类）。
        /// </summary>
        /// <returns>本次应用成功的对象更新记录数（Ack 消息返回 0）。</returns>
        public int OnMessage(PMNetConnection connection, byte[] payload, int offset, int count)
        {
            if (payload == null || count <= 0)
            {
                return 0;
            }

            string error;
            if (!PMReplicationReader.TryRead(new PMNetReader(payload, offset, count), _inbound, out error))
            {
                _stats.ProtocolErrors++;
                _inbound.Clear();
                WarnInternal("复制消息被丢弃：" + error);
                return 0;
            }

            if (_inbound.Kind == PMRepMessageKind.Ack)
            {
                for (int i = 0; i < _inbound.Acks.Count; i++)
                {
                    OnAck(connection, _inbound.Acks[i].NetId, _inbound.Acks[i].Version);
                }

                _inbound.Clear();
                return 0;
            }

            if (_inbound.Kind != PMRepMessageKind.Update)
            {
                _stats.ProtocolErrors++;
                _inbound.Clear();
                return 0;
            }

            int applied = 0;
            for (int i = 0; i < _inbound.Updates.Count; i++)
            {
                PMRepUpdateRecord rec = _inbound.Updates[i];

                PMNetObject obj;
                if (World == null || !World.TryFind(rec.NetId, out obj) || obj == null)
                {
                    _stats.DroppedUnknownObject++;
                    continue;
                }

                PMRepObjectEntry entry = ResolveEntry(obj);
                if (entry == null)
                {
                    continue;
                }

                string failure;
                if (!TryApplyRecord(entry, obj, rec, out failure))
                {
                    // 校验失败 ⇒ **整条记录不生效**（结构校验与暂存解码阶段：活对象一个字节都没写）、
                    // 或**提交阶段中途失败**（Reader 非纯字段写入：活对象可能已部分写入，
                    // 见 CommitFailures 与 TryApplyRecord 的诚实口径）。两种情形都：
                    // 不派发 OnRep、**不回 ACK**。
                    //
                    // "不回 ACK" 是关键：发送侧只有收到 ACK 才会把该版本当成"对方已有"
                    // 并推进基线、清除脏位。对一条未生效的记录回 ACK，会让发送侧以为已同步
                    // ⇒ 那些属性就永远不再重发（历史上这正是掩码错配被放大成"静默永久不一致"的路径）。
                    _stats.ProtocolErrors++;
                    _stats.RecordsRejected++;
                    WarnInternal("对象更新记录被拒绝（NetId=" + rec.NetId + "）：" + failure);
                    continue;
                }

                QueueAck(connection, rec.NetId, rec.Version);
                applied++;
            }

            // 只有真的应用了至少一条记录才算"这条消息应用成功"。
            // 否则一条全部被丢弃的消息会推高 applied 计数，让统计口径变得乐观。
            if (applied > 0)
            {
                _stats.UpdateMessagesApplied++;
            }

            _stats.UpdateRecordsApplied += applied;
            _inbound.Clear();
            return applied;
        }

        /// <summary>
        /// 应用一条对象更新记录（RV2）。分三个阶段，顺序不可换：
        ///
        /// <list type="number">
        ///   <item>**结构校验**（不碰活对象）：槽位范围 / 属性 ID / Reader 存在 / 值字节非 null；</item>
        ///   <item>**暂存对象全量解码**：用类工厂造一个暂时对象，把每个值解码一遍，
        ///   且要求"字节恰好读完"（多一字节也算失败）；</item>
        ///   <item>**提交并派发 OnRep**：`3a` 先把整条记录全部写入活对象，`3b` 再统一派发 OnRep。</item>
        /// </list>
        ///
        /// 为何必须分阶段：旧实现是"边解边写"，第 k 个属性非法时前 k-1 个已经落在活对象上，
        /// 于是对象停在"发送端从未存在过的状态"，而且仍然会回 ACK 把这一版当成已确认。
        /// 暂存解码把"全部值都合法"变成提交的前置条件；没有工厂时**明确拒绝整条记录**
        /// （不静默半应用）——宁可重发，也不留下一半新一半旧的状态。
        ///
        /// 为何 `3a` 与 `3b` 必须拆开（RV2 返工）：旧实现在提交循环里"写一个属性就立刻派发它的
        /// OnRep"，于是第 k 个属性的回调执行时，同一条记录里 k+1..n 的属性还停在**旧值**上
        /// —— 业务在回调里读"同记录另一个字段"做派生计算（典型：用新 HP 配旧上限算血条）
        /// 会拿到新旧混合的状态。更要紧的是：那种顺序下回调抛异常会让整条记录被判为"未应用"
        /// （不回 ACK）⇒ 发送侧下轮重发同一批值 ⇒ 已执行过的回调**再执行一次**（重复副作用）。
        /// 现在：先全部提交，再统一派发；回调异常被单独隔离，不影响"记录已提交"这一事实。
        ///
        /// **诚实口径（不宣称完全解决原子性）**：本层的原子性建立在两个前提上 ——
        ///   (1) 描述符的 Reader 是**纯字段写入**（生成代码 `PMNet_Read_&lt;成员&gt;` 就是如此：
        ///       只从 reader 取值写给自己的成员，没有别的副作用）；
        ///   (2) 构造工厂产出**全新、未登记的对象**（本方法已拒绝返回活对象/已登记对象的工厂）。
        /// 前提 (1) 不成立时（Reader 有副作用，或只在活对象上抛），暂存解码成功并不能推出
        /// 提交也成功：`3a` 可能中途失败，此时**已写入的属性无法可靠回退**。
        /// 这里**不用 `Writer` 做回滚**：Writer 同样要读活对象，自身也可能抛；
        /// 一旦回滚失败，本层没有"回滚失败自身可见"的机制（<see cref="CommitFailures"/> 只记第一次失败），
        /// 就会留下比不回滚更坏、且**看不见**的状态。与其造一个不能保证的保证，
        /// 不如把失败当场暴露（计数 + 告警 + 不回 ACK），让发送侧重发整条记录。
        /// 对纯字段写入，重发是幂等的，因此最终收敛；对带副作用的 Reader，本层给不出保证。
        /// </summary>
        /// <returns>true = 整条记录已完整应用（调用方可回 ACK）；false = 未生效或只部分生效。</returns>
        private bool TryApplyRecord(PMRepObjectEntry entry, PMNetObject obj, PMRepUpdateRecord rec, out string failure)
        {
            failure = null;

            PMReplicationDescriptor desc = entry.Descriptor;
            int count = rec.Slots == null ? 0 : rec.Slots.Length;

            if (count == 0 || rec.Values == null || rec.PropertyIds == null
                || rec.Values.Length != count || rec.PropertyIds.Length != count)
            {
                failure = "记录形状非法：槽位/属性ID/值三者必须非空且长度一致（"
                          + count + "/" + (rec.PropertyIds == null ? -1 : rec.PropertyIds.Length)
                          + "/" + (rec.Values == null ? -1 : rec.Values.Length) + "）";
                return false;
            }

            // ---- 阶段 1：结构校验（不触碰活对象）----
            for (int k = 0; k < count; k++)
            {
                int slot = rec.Slots[k];
                if (slot < 0 || slot >= entry.SlotCount)
                {
                    failure = "引用了越界槽位 " + slot + "（本类槽位数 " + entry.SlotCount + "）";
                    return false;
                }

                PMPropertyDescriptor prop = desc.Properties[slot];
                if (prop.PropertyId != rec.PropertyIds[k])
                {
                    // D-R0-46 的运行期兜底：两端描述符不同 ⇒ 当场判定，而不是一路错位。
                    failure = "属性 ID 不一致：槽位 " + slot + " 本端=" + prop.PropertyId
                              + " 远端=" + rec.PropertyIds[k];
                    return false;
                }

                if (prop.Reader == null)
                {
                    failure = "槽位 " + slot + "（" + prop.MemberName + "）没有 Reader";
                    return false;
                }

                if (rec.Values[k] == null)
                {
                    failure = "槽位 " + slot + " 的值字节为 null";
                    return false;
                }
            }

            // ---- 阶段 2：暂存对象全量解码（验证每个值都能恰好读完）----
            Func<PMNetObject> factory = entry.Factory;
            if (factory == null)
            {
                failure = "类 " + desc.TypeName + "（ClassId=" + desc.ClassId
                          + "）未注册构造工厂，无法做暂存对象解码：为保证原子性，整条记录被拒绝";
                return false;
            }

            PMNetObject staging;
            try
            {
                staging = factory();
            }
            catch (Exception ex)
            {
                failure = "构造工厂抛出：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (staging == null)
            {
                failure = "构造工厂返回 null";
                return false;
            }

            // 暂存对象必须是**全新、未登记**的对象。
            // 反例：工厂直接返回活对象（或任何已登记的对象）⇒ 阶段 2 的"验证性解码"就等于
            // 直接在活对象上写：验证失败时活对象已被污染，而本层还以为自己什么都没碰
            // —— 原子性完全失效。这与世界创建路径的同类检查（`PMNetWorld.ApplyCreate`
            // 要求工厂产出 Unregistered 对象）是同一条纪律，只是这里更严格：
            // 连"已登记但已 Destroyed"的对象也不行（重用一个登记过的实例不是"造新对象"）。
            if (ReferenceEquals(staging, obj))
            {
                StagingRejected++;
                failure = "构造工厂返回了活对象本身（暂存验证会直接写活对象，原子性失效）：整条记录被拒绝";
                return false;
            }

            if (staging.NetId.IsValid || staging.WorldSlot >= 0 || staging.World != null
                || staging.State != PMNetObjectState.Unregistered)
            {
                StagingRejected++;
                failure = "构造工厂返回了已登记/已存活的对象（NetId=" + staging.NetId
                          + "，State=" + staging.State + "，WorldSlot=" + staging.WorldSlot
                          + "，World=" + (staging.World != null ? "非空" : "null")
                          + "）：暂存验证会写到活对象上，整条记录被拒绝";
                return false;
            }

            for (int k = 0; k < count; k++)
            {
                int slot = rec.Slots[k];
                PMPropertyDescriptor prop = desc.Properties[slot];
                PMNetReader reader = new PMNetReader(rec.Values[k]);

                try
                {
                    prop.Reader(staging, reader);
                }
                catch (Exception ex)
                {
                    failure = "槽位 " + slot + " 的值解码失败：" + ex.GetType().Name + " " + ex.Message;
                    return false;
                }

                if (!reader.IsAtEnd)
                {
                    failure = "槽位 " + slot + " 的值长度与内容不符（余 "
                              + (rec.Values[k].Length - reader.Consumed) + " 字节未消费）";
                    return false;
                }
            }

            // ---- 阶段 3a：提交到活对象（此刻才允许写入）----
            // 阶段 2 已在同类型对象上用同一批字节成功解码过，因此这里的 Reader 若仍抛异常，
            // 说明该 Reader 不是纯字段写入（违反描述符契约）。此时**前 k 个属性已经落在活对象上**，
            // 无法回退（理由见方法头注释的诚实口径）：明确报出来 + 计数 + **不回 ACK**，
            // 而不是假装成功。还要注意：这里**不派发任何 OnRep** —— 部分写入的中间态
            // 不该被业务观察到（旧实现会在失败前先回调前几个属性）。
            bool perPropertyDispatch = DispatchOnRepPerProperty();

            for (int k = 0; k < count; k++)
            {
                int slot = rec.Slots[k];
                PMPropertyDescriptor prop = desc.Properties[slot];

                try
                {
                    prop.Reader(obj, new PMNetReader(rec.Values[k]));
                }
                catch (Exception ex)
                {
                    CommitFailures++;
                    failure = "提交阶段解码失败（Reader 非纯字段写入，已写入 " + k + "/" + count
                              + " 个属性，活对象可能停在混合状态）：" + ex.GetType().Name + " " + ex.Message;
                    return false;
                }

                _stats.PropertiesApplied++;

                if (perPropertyDispatch)
                {
                    // 缺陷注入路径（旧行为）：写一个属性就回调一次。
                    DispatchOnRep(entry, obj, slot);
                }
            }

            // ---- 阶段 3b：整条记录已完整提交，统一派发 OnRep ----
            // 回调里读到的是**整条记录的新值**（同记录的其他属性也已写入）。
            // 回调抛异常在 DispatchOnRep 内部隔离：它既不改"记录已提交"的结论，
            // 也不阻断后续属性的回调（每个属性都有一次机会看到完整的新状态）。
            if (!perPropertyDispatch)
            {
                for (int k = 0; k < count; k++)
                {
                    DispatchOnRep(entry, obj, rec.Slots[k]);
                }
            }

            return true;
        }

        /// <summary>
        /// 决策点：OnRep 何时派发。默认 false = **整条记录提交完成后统一派发**（契约）。
        ///
        /// true 是**旧行为**（每写入一个属性就立刻派发它的 OnRep），只供门禁注入缺陷：
        /// 那种顺序下第 k 个属性的回调执行时，同一条记录里 k+1..n 的属性还停在旧值上，
        /// 业务用"同记录另一个字段"做的派生计算会拿到新旧混合的状态。
        /// 生产代码不覆写，默认实现即契约。
        /// </summary>
        protected virtual bool DispatchOnRepPerProperty()
        {
            return false;
        }

        /// <summary>
        /// 决策点：OnRep 回调抛异常时是否隔离。默认 true（契约）。
        ///
        /// false 只供门禁注入"回调异常直接穿透"的旧行为：穿透会让**已经完整提交**的记录
        /// 丢掉 ACK（发送侧下轮重发 ⇒ 已执行过的回调重复执行），并让异常逃进网络层。
        /// 生产代码不覆写，默认实现即契约。
        /// </summary>
        protected virtual bool IsolateOnRepExceptions()
        {
            return true;
        }

        private void DispatchOnRep(PMRepObjectEntry entry, PMNetObject obj, int slot)
        {
            ushort methodId = entry.Descriptor.Properties[slot].OnRepMethodId;
            if (methodId == 0)
            {
                return;
            }

            Action<PMNetObject, ushort> dispatcher;
            if (_onRepDispatchers.TryGetValue(entry.Descriptor.ClassId, out dispatcher) && dispatcher != null)
            {
                // 先计数再调用：语义是"触发了一次 OnRep"（业务回调确实被调用了）。
                // 抛异常的调用另计 OnRepExceptions —— 两者合起来才能区分
                // "表现层没接线（OnRepUnhandled）"、"接线了但抛（OnRepExceptions）"、"正常"。
                _stats.OnRepDispatched++;

                try
                {
                    dispatcher(obj, methodId);
                }
                catch (Exception ex)
                {
                    if (!IsolateOnRepExceptions())
                    {
                        // 缺陷注入路径：让异常穿透（旧行为）。
                        throw;
                    }

                    // 表现回调异常**与协议解析失败分离**：记录已完整提交，因此
                    //   1) 不回退、不重新应用；
                    //   2) 照常回 ACK（否则发送侧下轮重发同一批值 ⇒ 已执行过的回调再执行一次，
                    //      形成重复副作用）；
                    //   3) 继续派发同记录后续属性的回调（一个表现函数失败不该吞掉其他属性的通知）。
                    OnRepExceptions++;
                    WarnInternal("OnRep 回调抛出（类 " + entry.Descriptor.TypeName + "，属性 "
                                 + entry.Descriptor.Properties[slot].MemberName + "，方法 ID=" + methodId
                                 + "）：" + ex.GetType().Name + " " + ex.Message
                                 + "；记录已完整提交，仍回 ACK、不重放");
                }

                return;
            }

            _stats.OnRepUnhandled++;
            WarnInternal("类 " + entry.Descriptor.TypeName + " 的属性 " + entry.Descriptor.Properties[slot].MemberName
                         + " 声明了 OnRepMethodId=" + methodId + "，但没有注册分发器（生成代码未挂上）");
        }

        private void QueueAck(PMNetConnection connection, uint netId, long version)
        {
            if (connection == null)
            {
                return;
            }

            List<PMRepAck> queue;
            if (!_ackQueues.TryGetValue(connection, out queue))
            {
                queue = new List<PMRepAck>(16);
                _ackQueues.Add(connection, queue);
            }

            if (queue.Count >= _options.MaxPendingAcks)
            {
                queue.RemoveAt(0);
                _stats.PendingAcksDropped++;
            }

            queue.Add(new PMRepAck(netId, version));
        }

        /// <summary>
        /// 取出并清空某连接待回传的确认。返回 null 表示当前没有待发确认。
        /// </summary>
        public byte[] BuildAckMessage(PMNetConnection connection)
        {
            List<PMRepAck> queue;
            if (connection == null || !_ackQueues.TryGetValue(connection, out queue) || queue.Count == 0)
            {
                return null;
            }

            _writer.Reset();
            PMReplicationWriter.WriteAck(_writer, queue);

            _stats.AcksSent += queue.Count;

            byte[] result = _writer.ToArray();
            queue.Clear();
            return result;
        }

        /// <summary>某连接当前待回传的确认条数。</summary>
        public int PendingAckCount(PMNetConnection connection)
        {
            List<PMRepAck> queue;
            if (connection == null || !_ackQueues.TryGetValue(connection, out queue))
            {
                return 0;
            }

            return queue.Count;
        }

        // =================================================================================
        //  角色与描述符
        // =================================================================================

        /// <summary>
        /// 解析"这条连接看到的副本角色"。默认：
        ///   - 无连接（本端权威自视）⇒ <see cref="PMRepViewRole.Authority"/>；
        ///   - 该连接拥有这个对象 ⇒ <see cref="PMRepViewRole.Autonomous"/>；
        ///   - 其余 ⇒ <see cref="PMRepViewRole.Simulated"/>。
        /// </summary>
        protected virtual PMRepViewRole ResolveViewRole(PMNetObject obj, PMNetConnection connection)
        {
            if (connection == null)
            {
                return PMRepViewRole.Authority;
            }

            if (obj != null)
            {
                if (obj.OwnerConnection != null && ReferenceEquals(obj.OwnerConnection, connection))
                {
                    return PMRepViewRole.Autonomous;
                }

                if (ReferenceEquals(obj.GetNetConnection(), connection))
                {
                    return PMRepViewRole.Autonomous;
                }
            }

            return PMRepViewRole.Simulated;
        }

        private void ResolveFlags(PMNetObject obj, PMNetConnection connection, out bool isOwner, out bool isSimulated)
        {
            Func<PMNetObject, PMNetConnection, PMRepViewRole> resolver = ViewRoleResolver;
            PMRepViewRole role = resolver != null ? resolver(obj, connection) : ResolveViewRole(obj, connection);

            isOwner = role == PMRepViewRole.Autonomous;
            isSimulated = role == PMRepViewRole.Simulated;
        }

        private PMRepObjectEntry ResolveEntry(PMNetObject obj)
        {
            if (obj == null)
            {
                return null;
            }

            PMRepObjectEntry entry;
            if (_entries.TryGetValue(obj, out entry))
            {
                return entry;
            }

            PMNetClassEntry cls;
            if (!PMNetRegistry.TryGetClass(obj.ClassId, out cls) || cls == null || cls.Rep == null)
            {
                _stats.DroppedNoDescriptor++;

                if (!_warnedNoDescriptor.ContainsKey(obj.ClassId))
                {
                    _warnedNoDescriptor.Add(obj.ClassId, true);
                    WarnInternal("ClassId=" + obj.ClassId + " 没有注册复制描述符（PMNetRegistry），该对象不参与复制");
                }

                return null;
            }

            PMReplicationDescriptor desc = cls.Rep;
            if (desc.Properties == null || desc.Properties.Length == 0)
            {
                _stats.DroppedNoDescriptor++;

                if (!_warnedNoDescriptor.ContainsKey(obj.ClassId))
                {
                    _warnedNoDescriptor.Add(obj.ClassId, true);
                    WarnInternal("ClassId=" + obj.ClassId + " 的复制描述符没有属性，该对象不参与复制");
                }

                return null;
            }

            if (desc.Properties.Length > PMRepMask.MaxBits)
            {
                // 有界（D-R0-18）：掩码位宽上限，超出即拒绝而不是静默截断。
                _stats.RejectedTooManyProperties++;

                if (!_warnedNoDescriptor.ContainsKey(obj.ClassId))
                {
                    _warnedNoDescriptor.Add(obj.ClassId, true);
                    WarnInternal("ClassId=" + obj.ClassId + " 有 " + desc.Properties.Length
                                 + " 个复制属性，超过掩码位宽上限 " + PMRepMask.MaxBits + "，拒绝复制");
                }

                return null;
            }

            bool hasConditional = false;
            for (int i = 0; i < desc.Properties.Length; i++)
            {
                if (desc.Properties[i].Condition != PMCond.None)
                {
                    hasConditional = true;
                    break;
                }
            }

            if (desc.HasConditionalMask != hasConditional)
            {
                // 生成器产物的标志与实际属性条件不一致：是生成器的缺陷信号，必须喊出来。
                _stats.DescriptorFlagMismatch++;
                WarnInternal("ClassId=" + obj.ClassId + " 的 HasConditionalMask=" + desc.HasConditionalMask
                             + " 与实际属性条件不符（实际 " + hasConditional + "）");
            }

            entry = new PMRepObjectEntry();
            entry.Object = obj;
            entry.Descriptor = desc;
            entry.SlotCount = desc.Properties.Length;
            entry.HasConditional = hasConditional;
            entry.Factory = cls.Factory;
            entry.CustomActive = new bool[entry.SlotCount];
            entry.Dynamic = new PMCond[entry.SlotCount];

            for (int i = 0; i < entry.SlotCount; i++)
            {
                entry.CustomActive[i] = true;                 // 未覆盖 ≈ 总是复制
                entry.Dynamic[i] = PMCond.Dynamic;            // 未覆盖 = 保持 Dynamic 语义
            }

            _entries.Add(obj, entry);
            return entry;
        }

        // =================================================================================
        //  与世界（M04）的条件接线
        // =================================================================================

        /// <summary>
        /// 把本层的条件求值接到世界上（RV5）。
        ///
        /// 世界在造 Create 记录的"声明式初始状态"时必须按**接收连接**过滤属性
        /// （OwnerOnly / SkipOwner / Never / Custom / Dynamic…），否则 Create 会变成
        /// "全属性泄露"到不拥有该对象的连接上。而条件求值的唯一事实源在本层
        /// （角色解析 + Custom/Dynamic 覆盖），所以由本层把入口接上。
        ///
        /// 幂等，可在 AddConnection / RegisterObject / Tick 重复调用：
        /// Create 可能早于首次 Tick，因此不能在 Tick 里才接。
        /// </summary>
        private void TryInstallWorldConditionSource()
        {
            PMNetWorld world = World;
            if (world == null)
            {
                return;
            }

            world.ReplicatedPropertyFilter = EvaluateSlotForWorld;
        }

        /// <summary>世界回调：本对象的本槽位是否应发给该连接（与发送侧同源）。</summary>
        private bool EvaluateSlotForWorld(PMNetObject obj, PMNetConnection connection, int slot)
        {
            PMRepObjectEntry entry = ResolveEntry(obj);
            if (entry == null || slot < 0 || slot >= entry.SlotCount)
            {
                return false;
            }

            bool isOwner;
            bool isSimulated;
            ResolveFlags(obj, connection, out isOwner, out isSimulated);
            return entry.EvaluateSlot(slot, isOwner, isSimulated);
        }

        // =================================================================================
        //  内部工具
        // =================================================================================

        private ConnectionBucket FindBucket(PMNetConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            ConnectionBucket bucket;
            return _buckets.TryGetValue(connection, out bucket) ? bucket : null;
        }

        private PMRepConnectionState GetOrCreateState(ConnectionBucket bucket, PMRepObjectEntry entry)
        {
            uint netId = entry.Object.NetId.Value;

            PMRepConnectionState state;
            if (bucket.Objects.TryGetValue(netId, out state))
            {
                return state;
            }

            state = new PMRepConnectionState(bucket.Connection, entry);
            bucket.Objects.Add(netId, state);
            return state;
        }

        private void EnsureScratch(int slotCount)
        {
            if (_condScratch.Length >= slotCount && _slotScratch.Length >= slotCount)
            {
                return;
            }

            int size = _condScratch.Length;
            while (size < slotCount)
            {
                size *= 2;
            }

            if (_condScratch.Length < size)
            {
                _condScratch = new bool[size];
            }

            if (_slotScratch.Length < size)
            {
                _slotScratch = new int[size];
                _valueScratch = new byte[size][];
            }
        }

        private void WarnInternal(string message)
        {
            Action<string> handler = Warn;
            if (handler != null)
            {
                handler(message);
            }
        }

        // =================================================================================
        //  门禁查询接口
        // =================================================================================

        /// <summary>某（连接 × 对象）已确认的最大版本；未建状态返回 false。</summary>
        public bool TryGetAckedVersion(PMNetConnection connection, uint netId, out long version)
        {
            version = 0L;
            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return false;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                return false;
            }

            version = state.AckedVersion;
            return true;
        }

        /// <summary>某（连接 × 对象）当前的在途记录数。</summary>
        public int GetInflightCount(PMNetConnection connection, uint netId)
        {
            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return 0;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                return 0;
            }

            return state.Inflight.Count;
        }

        /// <summary>某（连接 × 对象）某属性的已确认基线；未建立返回 false。</summary>
        public bool TryGetBaselineValue(PMNetConnection connection, uint netId, int slot, out byte[] value)
        {
            value = null;
            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return false;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                return false;
            }

            if (slot < 0 || slot >= state.Entry.SlotCount)
            {
                return false;
            }

            value = state.Baseline[slot];
            return value != null;
        }

        /// <summary>某（连接 × 对象）某属性的条件当前是否满足（上一次扫描的结果）。</summary>
        public bool TryGetConditionActive(PMNetConnection connection, uint netId, int slot, out bool active)
        {
            active = false;
            ConnectionBucket bucket = FindBucket(connection);
            if (bucket == null)
            {
                return false;
            }

            PMRepConnectionState state;
            if (!bucket.Objects.TryGetValue(netId, out state))
            {
                return false;
            }

            if (slot < 0 || slot >= state.Entry.SlotCount)
            {
                return false;
            }

            active = state.ConditionActive[slot];
            return true;
        }

        /// <summary>把某对象某属性的当前值编码成字节（门禁断言用）。</summary>
        public byte[] SerializeSlot(PMNetObject obj, int slot)
        {
            PMRepObjectEntry entry = ResolveEntry(obj);
            if (entry == null || slot < 0 || slot >= entry.SlotCount)
            {
                return null;
            }

            PMPropertyDescriptor prop = entry.Descriptor.Properties[slot];
            if (prop.Writer == null)
            {
                return null;
            }

            return SerializeProperty(prop, obj);
        }
    }
}
