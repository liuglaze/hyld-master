using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 世界统计。全部为累计值，用于门禁断言与线上诊断。
    /// </summary>
    public struct PMNetWorldStats
    {
        /// <summary>本端登记过的对象总数。</summary>
        public long ObjectsSpawned;

        /// <summary>本端销毁的对象总数。</summary>
        public long ObjectsDestroyed;

        /// <summary>发出的创建记录数（按连接计，同一对象发给 N 条连接算 N 次）。</summary>
        public long CreatesSent;

        /// <summary>发出的销毁记录数（按连接计）。</summary>
        public long DestroysSent;

        /// <summary>接收侧应用成功的创建数。</summary>
        public long CreatesApplied;

        /// <summary>接收侧应用成功的销毁数。</summary>
        public long DestroysApplied;

        /// <summary>未知类型 ID 导致的丢弃（D-R0-11：未解析引用 = 丢弃 + 告警）。</summary>
        public long DroppedUnknownClass;

        /// <summary>重复 NetId 导致的丢弃（NetId 不复用 ⇒ 重复即协议违规）。</summary>
        public long DroppedDuplicateId;

        /// <summary>解析到不存在对象导致的丢弃。</summary>
        public long DroppedUnknownObject;

        /// <summary>超过对象数上限被拒绝的创建（D-R0-18：必须有界）。</summary>
        public long RejectedOverCapacity;

        /// <summary>初始状态反序列化失败导致的回滚。</summary>
        public long RolledBackCreate;

        /// <summary>创建尚未发出对象就已销毁，因而被吞掉的记录数。</summary>
        public long SwallowedUnsentCreate;

        /// <summary>批量消息解析失败次数（协议不兼容 / 数据损坏）。</summary>
        public long ProtocolErrors;

        /// <summary>单行摘要。</summary>
        public override string ToString()
        {
            return "spawned=" + ObjectsSpawned + " destroyed=" + ObjectsDestroyed
                   + " cs=" + CreatesSent + " ds=" + DestroysSent
                   + " ca=" + CreatesApplied + " da=" + DestroysApplied
                   + " dropClass=" + DroppedUnknownClass + " dropDup=" + DroppedDuplicateId
                   + " dropUnknown=" + DroppedUnknownObject + " overCap=" + RejectedOverCapacity
                   + " rollback=" + RolledBackCreate + " swallow=" + SwallowedUnsentCreate
                   + " protoErr=" + ProtocolErrors;
        }
    }

    /// <summary>
    /// 网络世界：对象登记、身份分配、生命周期事件的产生与消费（M04）。
    ///
    /// 对应 UE 里 `UNetDriver` 承担的那部分职责 —— 但**只取对象登记这一块**：
    /// 通道调度、优先级、带宽预算是 M06 的事，本类不碰。
    ///
    /// ## 为什么必须先把对象登记起来
    ///
    /// hyld 当前的状态散在若干 Dictionary 里（`playerHp` / `playerMana` / `playerPositions`），
    /// 没有对象宿主。没有宿主就无从表达「该属性相对哪份基线、发给哪条连接、增量是什么」，
    /// 也就无从谈复制。所以 M04 是复制层（M06）的前置。
    ///
    /// ## 三条关键契约
    ///
    /// 1. **NetId 单会话内单调、永不复用**（D-R0-02）。因此接收侧遇到重复 NetId 是
    ///    **协议违规**而不是"更新"，必须丢弃并告警；同时免去了整套跨世代串扰判定。
    /// 2. **创建与初始状态原子到达**（D-R0-16）。初始状态嵌在同一条创建记录里，
    ///    接收侧不存在"已创建但值未到"的可见态。
    /// 3. **销毁走可靠流且不等 ack**（D-R0-04）。本端判定销毁即刻摘出索引，
    ///    销毁记录随后排空；因为 NetId 不复用，迟到包自然失效。
    ///
    /// ## 线程模型
    ///
    /// 与 M03 传输层一致：本类**全部方法都只在主线程调用**。入站字节由 `OnLifecycleMessage`
    /// 在主线程的 tick 里消费，没有锁，也不打算有锁。
    /// </summary>
    public sealed class PMNetWorld
    {
        /// <summary>每连接待发生的生命周期事件。</summary>
        private struct PendingEvent
        {
            /// <summary>事件类型。</summary>
            public PMObjectEventKind Kind;

            /// <summary>目标对象。销毁事件里对象已置 Destroyed，这里只为取 NetId/诊断。</summary>
            public PMNetObject Object;

            /// <summary>销毁原因。</summary>
            public PMObjectDestroyReason Reason;
        }

        /// <summary>一条连接在本世界里的状态。</summary>
        private sealed class ConnectionState
        {
            public PMNetConnection Connection;

            /// <summary>按产生顺序排列的待发事件（保序是契约的一部分，见 PMLifecycleCodec）。</summary>
            public readonly List<PendingEvent> Pending = new List<PendingEvent>(64);
        }

        private readonly PMSession _session;
        private readonly int _maxObjects;
        private readonly PMNetIdAllocator _allocator;

        /// <summary>NetId.Value → 对象。只放 Active 对象：销毁即刻摘除（D-R0-04）。</summary>
        private readonly Dictionary<uint, PMNetObject> _byId;

        /// <summary>ClassId → 构造工厂。接收侧靠它把线格式的类型 ID 还原成对象。</summary>
        private readonly Dictionary<uint, Func<PMNetObject>> _classFactories;

        /// <summary>
        /// 本世界**存活**对象的登记顺序表（RV3）。
        ///
        /// 历史实现是"只增不减"（销毁不摘、创建回滚也不摘），后果：
        ///   - 每个已销毁对象被**永久强引用**（违反 D-R0-18 的"内存必须有界"）；
        ///   - `TotalObjectCount` / `GetObjectAt` 语义失真（含幽灵引用）；
        ///   - 每新增一条连接都要线性扫过全部历史对象。
        ///
        /// 现在的约定：
        ///   - 只放存活对象；销毁/创建回滚时把对应槽位置为 **null**（解除强引用）；
        ///   - 置 null 后用 <see cref="CompactDeadSlots"/> 延迟压缩，保持登记顺序；
        ///   - 因此 `Count` 最多是 `2 * _maxObjects`（死槽位达一半就压缩），有界。
        /// </summary>
        private readonly List<PMNetObject> _allObjects;

        /// <summary>`_allObjects` 里存活对象的个数（不含 null 槽位）。</summary>
        private int _liveObjects;

        /// <summary>`_allObjects` 里 null 槽位的个数（延迟压缩的触发依据）。</summary>
        private int _deadSlots;

        /// <summary>连接 ID → 连接状态。</summary>
        private readonly Dictionary<int, ConnectionState> _connections;

        /// <summary>批量写出的暂存记录表（复用，避免每次发包分配）。</summary>
        private readonly List<PMNetLifecycleRecord> _scratchRecords = new List<PMNetLifecycleRecord>(64);

        /// <summary>本轮因不相关而未能发出、需要继续留在队列里的事件。</summary>
        private readonly List<PendingEvent> _stillPending = new List<PendingEvent>(64);

        /// <summary>初始状态序列化的专用写入器（创建是低频事件，一次一个够用）。</summary>
        private readonly PMNetWriter _initialStateWriter = new PMNetWriter(256);

        /// <summary>声明式初值的暂存写入器与单值暂存写入器（RV5）。</summary>
        private readonly PMNetWriter _repStateWriter = new PMNetWriter(256);
        private readonly PMNetWriter _repValueWriter = new PMNetWriter(64);

        private readonly PMNetWriter _scratchWriter = new PMNetWriter(512);

        private PMNetWorldStats _stats;

        /// <summary>
        /// 构造一个世界。
        /// </summary>
        /// <param name="session">本会话（Epoch 用于拒收旧世代包）。</param>
        /// <param name="maxObjects">
        /// 对象数上限（D-R0-18：复制队列与内存必须有界）。超出后服务端拒绝登记、
        /// 客户端拒绝创建 —— 宁可丢对象也不让内存无界增长。
        /// </param>
        public PMNetWorld(PMSession session, int maxObjects = 65535)
        {
            if (maxObjects <= 0)
            {
                throw new ArgumentOutOfRangeException("maxObjects");
            }

            _session = session;
            _maxObjects = maxObjects;
            _allocator = new PMNetIdAllocator();
            _byId = new Dictionary<uint, PMNetObject>(Math.Min(maxObjects, 4096));
            _classFactories = new Dictionary<uint, Func<PMNetObject>>(64);
            _allObjects = new List<PMNetObject>(Math.Min(maxObjects, 4096));
            _connections = new Dictionary<int, ConnectionState>(8);
        }

        /// <summary>本会话。</summary>
        public PMSession Session { get { return _session; } }

        /// <summary>本端是否服务端侧（权威）。</summary>
        public bool IsServer { get { return _session != null && _session.IsServerSide; } }

        /// <summary>对象数上限。</summary>
        public int MaxObjects { get { return _maxObjects; } }

        /// <summary>当前存活对象数（不含已销毁）。</summary>
        public int ObjectCount { get { return _byId.Count; } }

        /// <summary>
        /// 登记表里的**存活对象数**（与 <see cref="ObjectCount"/> 同值）。
        ///
        /// 语义变更（RV3）：本属性曾经是「历史累计创建数（含已销毁）」。
        /// 那个口径把"诊断用的历史计数"与"强引用表"混在一起，掩盖了内存无界的问题。
        /// 现在它是存活计数；**累计**创建数在 <see cref="PMNetWorldStats.ObjectsSpawned"/>。
        /// </summary>
        public int TotalObjectCount { get { return _liveObjects; } }

        /// <summary>登记表当前占用的槽位数（含待压缩的 null 槽位）。门禁用它断言"无界增长已消除"。</summary>
        public int AllObjectSlotCount { get { return _allObjects.Count; } }

        /// <summary>
        /// 世界在造 Create 记录时用来判定"某属性是否应该发给该连接"的兼容性入口（RV5）。
        ///
        /// 签名：(对象, 目标连接, 槽位) → 是否包含。
        ///
        /// 由 `PMReplicationChannel` 接入同一条条件求值链（角色解析 + Custom/Dynamic 覆盖），
        /// 从而让 Create 的声明式初值与后续增量更新用**同一套条件语义**。
        ///
        /// `null`（未接线）时的**明确安全行为**：只包含 `PMCond.None` 的属性。
        /// 其余 7 项都取决于接收方角色或运行期开关，无从判定 —— 漏发可由复制层的
        /// "基线缺失 ⇒ 全量"补齐，多发则是**泄露**。
        ///
        /// 注：同一世界只应有一个发送方接入。多个发送方接入时后写入者生效，
        /// 而两者对同一对象/连接的条件求值结果应当是等价的（否则本就存在语义分歧）。
        /// </summary>
        public Func<PMNetObject, PMNetConnection, int, bool> ReplicatedPropertyFilter;

        /// <summary>累计统计。</summary>
        public PMNetWorldStats Stats { get { return _stats; } }

        /// <summary>Id 分配器（仅服务端有意义）。</summary>
        public PMNetIdAllocator Allocator { get { return _allocator; } }

        /// <summary>日志回调。默认丢弃；接入项目日志（PM_LOG / Logging.Debug.Log）由宿主设置。</summary>
        public Action<string> Warn;

        // ---------------------------------------------------------------- 类型注册

        /// <summary>
        /// 注册「类型 ID → 构造工厂」。接收侧必须为每条可能收到的创建记录预先注册好工厂。
        ///
        /// 重复注册同一个 ClassId 会抛异常：这说明生成器产出了冲突的稳定 ID（D-R0-49），
        /// 属于声明错误，静默覆盖会让类型悄悄映射错位。
        /// </summary>
        public void RegisterClass(uint classId, Func<PMNetObject> factory)
        {
            if (classId == 0u)
            {
                throw new ArgumentOutOfRangeException("classId", "ClassId 0 保留为无效值");
            }

            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }

            if (_classFactories.ContainsKey(classId))
            {
                throw new InvalidOperationException("ClassId " + classId + " 被重复注册");
            }

            _classFactories.Add(classId, factory);
        }

        /// <summary>是否已注册该类型。</summary>
        public bool IsClassRegistered(uint classId)
        {
            return _classFactories.ContainsKey(classId);
        }

        // ---------------------------------------------------------------- 连接

        /// <summary>
        /// 登记一条连接。**服务端侧**：把当前所有存活对象补成该连接的待创建事件
        /// （D-R0-12：首次进入范围 ⇒ 全量初始状态），否则新连接永远看不到既有对象。
        /// </summary>
        public void AddConnection(PMNetConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException("connection");
            }

            if (_connections.ContainsKey(connection.ConnectionId))
            {
                return;
            }

            ConnectionState state = new ConnectionState();
            state.Connection = connection;
            _connections.Add(connection.ConnectionId, state);

            if (!IsServer)
            {
                return;
            }

            // 对已有对象补发创建。顺序取登记顺序，保证引用方在被引用方之后。
            for (int i = 0; i < _allObjects.Count; i++)
            {
                PMNetObject obj = _allObjects[i];
                if (obj == null || obj.State != PMNetObjectState.Active)
                {
                    continue;
                }

                PendingEvent evt = new PendingEvent();
                evt.Kind = PMObjectEventKind.Create;
                evt.Object = obj;
                state.Pending.Add(evt);
            }
        }
        /// <summary>
        /// 注销一条连接并丢弃它的待发事件。
        ///
        /// 注意：**不为该连接生成销毁事件** —— 连接已经断了，发出去也没有接收者。
        /// 客户端侧断开由传输层负责，不是生命周期事件。
        /// </summary>
        public void RemoveConnection(PMNetConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            _connections.Remove(connection.ConnectionId);
        }

        /// <summary>当前登记的连接数。</summary>
        public int ConnectionCount { get { return _connections.Count; } }

        /// <summary>某连接还有多少条待发事件（门禁断言用）。</summary>
        public int PendingEventCount(PMNetConnection connection)
        {
            ConnectionState state;
            if (connection == null || !_connections.TryGetValue(connection.ConnectionId, out state))
            {
                return 0;
            }

            return state.Pending.Count;
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>按身份查找存活对象。已销毁对象查不到（销毁即摘除）。</summary>
        public bool TryFind(PMNetId id, out PMNetObject result)
        {
            return TryFind(id.Value, out result);
        }

        /// <summary>按原始值查找存活对象。</summary>
        public bool TryFind(uint rawId, out PMNetObject result)
        {
            result = null;
            if (rawId == 0u)
            {
                return false;
            }

            return _byId.TryGetValue(rawId, out result);
        }

        /// <summary>按登记顺序取第 i 个**存活**对象（跳过已销毁的槽位）。遍历诊断用。</summary>
        public PMNetObject GetObjectAt(int index)
        {
            if (index < 0)
            {
                return null;
            }

            int seen = 0;
            for (int i = 0; i < _allObjects.Count; i++)
            {
                PMNetObject obj = _allObjects[i];
                if (obj == null)
                {
                    continue;
                }

                if (seen == index)
                {
                    return obj;
                }

                seen++;
            }

            return null;
        }

        // ---------------------------------------------------------------- 生成与销毁（服务端）

        /// <summary>
        /// 生成对象（服务端权威侧）。分配 NetId、登记、并为每条连接排入创建事件。
        ///
        /// 返回 false 表示被拒绝（超出上限、对象未处于 Unregistered、或本端不是服务端）。
        /// 拒绝时**不分配 NetId**，避免白白消耗编号空间（编号永不复用，浪费即不可回收）。
        /// </summary>
        public bool Spawn(PMNetObject obj, uint classId, uint archetypeId = 0u, bool isStatic = false)
        {
            if (obj == null)
            {
                WarnInternal("Spawn 传入 null 对象");
                return false;
            }

            if (!IsServer)
            {
                WarnInternal("客户端侧不得调用 Spawn（对象只能由服务端创建）");
                return false;
            }

            if (obj.State != PMNetObjectState.Unregistered)
            {
                WarnInternal("Spawn 的对象 " + obj.GetType().Name + " 状态为 " + obj.State + "，必须是 Unregistered");
                return false;
            }

            if (_byId.Count >= _maxObjects)
            {
                _stats.RejectedOverCapacity++;
                WarnInternal("对象数已达上限 " + _maxObjects + "，拒绝创建 " + obj.GetType().Name);
                return false;
            }

            PMNetId id = _allocator.Allocate(isStatic);
            obj.NetId = id;
            obj.ClassId = classId;
            obj.ArchetypeId = archetypeId;
            obj.World = this;
            obj.Role = PMNetRole.Authority;
            obj.NetMode = _session != null && _session.IsServerSide ? PMNetMode.DedicatedServer : PMNetMode.Standalone;
            obj.State = PMNetObjectState.Active;

            _byId.Add(id.Value, obj);
            obj.WorldSlot = _allObjects.Count;
            _allObjects.Add(obj);
            _liveObjects++;
            _stats.ObjectsSpawned++;

            foreach (KeyValuePair<int, ConnectionState> kv in _connections)
            {
                PendingEvent evt = new PendingEvent();
                evt.Kind = PMObjectEventKind.Create;
                evt.Object = obj;
                kv.Value.Pending.Add(evt);
            }

            return true;
        }

        /// <summary>
        /// 销毁对象（服务端权威侧）。
        ///
        /// 与 UE 的差异（D-R0-04）：UE legacy 要等关闭包被 ack 才清理通道，
        /// 我们**立刻**摘出索引，销毁记录随后排空即可 —— 因为 NetId 不复用，
        /// 即使销毁记录丢了对端重进范围也不会看到陈旧对象。
        /// </summary>
        public bool DestroyObject(PMNetObject obj, PMObjectDestroyReason reason = PMObjectDestroyReason.Destroyed)
        {
            if (obj == null)
            {
                return false;
            }

            if (!IsServer)
            {
                WarnInternal("客户端侧不得调用 DestroyObject");
                return false;
            }

            if (obj.State != PMNetObjectState.Active)
            {
                return false;
            }

            PMNetId id = obj.NetId;
            obj.State = PMNetObjectState.Destroying;
            _byId.Remove(id.Value);

            // RV3：立即解除强引用（不再当"历史墓碑"永久持有）。
            DetachFromAllObjects(obj);

            // 逐连接决定：创建还没发出去 ⇒ 对端从没见过它 ⇒ 连创建带销毁一起吞掉；
            // 已经发出去过 ⇒ 排一条销毁记录。
            foreach (KeyValuePair<int, ConnectionState> kv in _connections)
            {
                ConnectionState state = kv.Value;
                bool createUnsent = false;

                for (int i = state.Pending.Count - 1; i >= 0; i--)
                {
                    PendingEvent pending = state.Pending[i];
                    if (pending.Kind == PMObjectEventKind.Create
                        && pending.Object != null
                        && pending.Object.NetId.Value == id.Value)
                    {
                        state.Pending.RemoveAt(i);
                        createUnsent = true;
                        break;
                    }
                }

                if (createUnsent)
                {
                    _stats.SwallowedUnsentCreate++;
                    continue;
                }

                PendingEvent evt = new PendingEvent();
                evt.Kind = PMObjectEventKind.Destroy;
                evt.Object = obj;
                evt.Reason = reason;
                state.Pending.Add(evt);
            }

            obj.State = PMNetObjectState.Destroyed;
            _stats.ObjectsDestroyed++;
            return true;
        }

        // ---------------------------------------------------------------- 出站排队

        /// <summary>
        /// 为某条连接构造一批生命周期消息。返回 null 表示该连接当前无待发事件。
        ///
        /// 调用方负责把返回的字节发到**可靠域**（D-R0-03：Create/Destroy 与 Server RPC 同域，
        /// 从而"对象创建先于引用它的 RPC"由顺序域直接保证）。
        /// </summary>
        /// <param name="connection">目标连接。</param>
        /// <param name="viewer">观察者对象，用于相关性判定；null 表示不做距离裁剪。</param>
        public byte[] BuildLifecycleBatch(PMNetConnection connection, PMNetObject viewer = null)
        {
            ConnectionState state;
            if (connection == null || !_connections.TryGetValue(connection.ConnectionId, out state))
            {
                return null;
            }

            if (state.Pending.Count == 0)
            {
                return null;
            }

            _scratchRecords.Clear();
            _stillPending.Clear();

            for (int i = 0; i < state.Pending.Count; i++)
            {
                PendingEvent evt = state.Pending[i];
                PMNetObject obj = evt.Object;

                if (obj == null)
                {
                    continue;
                }

                if (evt.Kind == PMObjectEventKind.Create && !obj.IsNetRelevantFor(connection, viewer))
                {
                    // 不相关：**保持待发**，将来进入范围时再补发（D-R0-12 首次进入范围 ⇒ 全量）。
                    // 这里曾写成直接 `continue` 然后在循环后无条件清空待发表 —— 那些记录会被静默丢掉，
                    // 对象就永远不会出现在对端。凡是「筛选 + 清空」的组合，都必须显式重建剩余集合。
                    _stillPending.Add(evt);
                    continue;
                }

                PMNetLifecycleRecord rec = new PMNetLifecycleRecord();
                rec.Kind = evt.Kind;
                rec.NetId = obj.NetId;

                if (evt.Kind == PMObjectEventKind.Create)
                {
                    rec.ClassId = obj.ClassId;
                    rec.ArchetypeId = obj.ArchetypeId;
                    rec.IsOwner = ReferenceEquals(obj.GetNetConnection(), connection);

                    // 初始状态在这里现取（而不是生成时快照）：对端要的是「发那一刻的值」。
                    _initialStateWriter.Reset();
                    obj.OnSerializeInitialState(_initialStateWriter);
                    rec.InitialState = _initialStateWriter.Length > 0 ? _initialStateWriter.ToArray() : null;

                    // RV5：声明式初值。手写钩子写了内容就以它为准（不重复/不覆盖 —— 两者
                    // 表达的是同一批属性，同时写会让同一份初值双写，而且格式无法共存）。
                    // 生成器不发射钩子，因此声明式复制类走的就是下面这条分支。
                    rec.RepInitialState = rec.InitialState != null
                        ? null
                        : BuildReplicatedInitialState(obj, connection);
                }
                else
                {
                    rec.Reason = evt.Reason;
                }

                _scratchRecords.Add(rec);
            }

            state.Pending.Clear();
            state.Pending.AddRange(_stillPending);

            if (_scratchRecords.Count == 0)
            {
                return null;
            }

            _scratchWriter.Reset();
            PMLifecycleCodec.Write(_scratchWriter, _scratchRecords);

            for (int i = 0; i < _scratchRecords.Count; i++)
            {
                if (_scratchRecords[i].Kind == PMObjectEventKind.Create) { _stats.CreatesSent++; }
                else { _stats.DestroysSent++; }
            }

            byte[] result = _scratchWriter.ToArray();
            _scratchRecords.Clear();
            return result;
        }

        // ---------------------------------------------------------------- 入站（客户端）

        /// <summary>
        /// 消费一条生命周期消息（客户端侧）。
        ///
        /// 协议错误（版本不符 / 数据损坏）⇒ **整条丢弃**并计告警，不做部分采纳。
        /// 单条记录的业务性失败（未知类型 / 重复 NetId / 超上限）⇒ 跳过该条，继续其余记录：
        /// 这是有意的取舍，因为 NetId 不复用 ⇒ 跳过一条创建不会污染其他对象；
        /// 若改成整条丢弃，一个未知类型就会拖垮同一批里的所有对象。
        /// </summary>
        public void OnLifecycleMessage(byte[] payload, int offset, int count)
        {
            List<PMNetLifecycleRecord> records = _inboundScratch;
            string error;
            if (!PMLifecycleCodec.TryRead(payload, offset, count, records, out error))
            {
                _stats.ProtocolErrors++;
                WarnInternal("生命周期消息被丢弃：" + error);
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Kind == PMObjectEventKind.Create)
                {
                    ApplyCreate(records[i]);
                }
                else
                {
                    ApplyDestroy(records[i]);
                }
            }

            records.Clear();
        }

        private readonly List<PMNetLifecycleRecord> _inboundScratch = new List<PMNetLifecycleRecord>(64);

        private void ApplyCreate(PMNetLifecycleRecord rec)
        {
            // NetId 永不复用（D-R0-02）⇒ 重复 ID 是协议违规，不是"更新"。
            if (_byId.ContainsKey(rec.NetId.Value))
            {
                _stats.DroppedDuplicateId++;
                WarnInternal("创建记录的 NetId " + rec.NetId + " 已存在：NetId 不复用，判定为协议违规并丢弃");
                return;
            }

            Func<PMNetObject> factory;
            if (!_classFactories.TryGetValue(rec.ClassId, out factory) || factory == null)
            {
                // D-R0-11：未解析引用 = 丢弃 + 告警（不做延迟队列，由顺序域替代）。
                _stats.DroppedUnknownClass++;
                WarnInternal("未注册的类型 ClassId=" + rec.ClassId + "（NetId " + rec.NetId + "），丢弃该创建");
                return;
            }

            if (_byId.Count >= _maxObjects)
            {
                _stats.RejectedOverCapacity++;
                WarnInternal("对象数已达上限 " + _maxObjects + "，拒绝创建 NetId " + rec.NetId);
                return;
            }

            PMNetObject obj = factory();
            if (obj == null)
            {
                _stats.ProtocolErrors++;
                WarnInternal("类型 ClassId=" + rec.ClassId + " 的工厂返回 null");
                return;
            }

            if (obj.State != PMNetObjectState.Unregistered)
            {
                _stats.ProtocolErrors++;
                WarnInternal("工厂返回的对象已处于 " + obj.State + " 状态（工厂必须产出新对象）");
                return;
            }

            obj.NetId = rec.NetId;
            obj.ClassId = rec.ClassId;
            obj.ArchetypeId = rec.ArchetypeId;
            obj.World = this;
            obj.Role = rec.IsOwner ? PMNetRole.AutonomousProxy : PMNetRole.SimulatedProxy;
            obj.NetMode = _session != null && _session.IsServerSide ? PMNetMode.DedicatedServer : PMNetMode.Client;
            obj.State = PMNetObjectState.Active;

            // 先登记再反序列化：初始状态里可能引用别的对象，也可能自引用。
            // 单线程内不存在"中途被他人观察到"的窗口（没有 yield、没有回调外流），
            // 失败路径会立刻回滚（索引 + 强引用表一起摘），因此不会留下半初始化对象。
            _byId.Add(rec.NetId.Value, obj);
            obj.WorldSlot = _allObjects.Count;
            _allObjects.Add(obj);
            _liveObjects++;

            if (rec.InitialState != null && rec.InitialState.Length > 0)
            {
                try
                {
                    PMNetReader reader = new PMNetReader(rec.InitialState);
                    obj.OnDeserializeInitialState(reader);
                }
                catch (Exception ex)
                {
                    RollBackCreate(obj, rec.NetId,
                        "初始状态反序列化失败：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }

            // RV5：声明式初值必须在 OnReplicatedCreate 之前全部到位（D-R0-16），
            // 否则业务在创建回调里会看到"已创建但字段全默认值"的中间态。
            // 失败 ⇒ 不发布（整条创建回滚），而不是留下一个半初始化的对象。
            if (rec.RepInitialState != null && rec.RepInitialState.Length > 0)
            {
                string error;
                if (!TryApplyReplicatedInitialState(obj, rec.RepInitialState, out error))
                {
                    RollBackCreate(obj, rec.NetId, "声明式初值应用失败：" + error);
                    return;
                }
            }

            _stats.CreatesApplied++;
            obj.OnReplicatedCreate();
        }

        /// <summary>回滚一次未发布的创建：索引与强引用表一起摘掉，不留幽灵引用。</summary>
        private void RollBackCreate(PMNetObject obj, PMNetId netId, string reason)
        {
            _byId.Remove(netId.Value);
            DetachFromAllObjects(obj);
            obj.State = PMNetObjectState.Destroyed;
            _stats.RolledBackCreate++;
            WarnInternal("NetId " + netId + " 的创建已回滚（" + reason + "）");
        }

        private void ApplyDestroy(PMNetLifecycleRecord rec)
        {
            PMNetObject obj;
            if (!_byId.TryGetValue(rec.NetId.Value, out obj) || obj == null)
            {
                // 常见于"创建因未知类型被丢弃 ⇒ 销毁也解析不到"。计次但只告警一次语义说明。
                _stats.DroppedUnknownObject++;
                WarnInternal("销毁记录的 NetId " + rec.NetId + " 不存在（对应的创建可能已被丢弃）");
                return;
            }

            _byId.Remove(rec.NetId.Value);
            DetachFromAllObjects(obj);
            obj.State = PMNetObjectState.Destroyed;
            _stats.DestroysApplied++;
            obj.OnReplicatedDestroy(rec.Reason);
        }

        // ---------------------------------------------------------------- 清理

        /// <summary>
        /// 清空整个世界。用于对局结束与测试复位。
        /// 注意：**不复位 Id 分配器** —— 分配器属于「会话」，跨会话由 SessionEpoch 隔离，
        /// 而同一个世界内复位分配器会让旧包有机会命中新对象（违反 D-R0-02）。
        /// </summary>
        public void Reset()
        {
            _byId.Clear();
            _allObjects.Clear();
            _liveObjects = 0;
            _deadSlots = 0;
            _connections.Clear();
            _scratchRecords.Clear();
        }

        // ---------------------------------------------------------------- 登记表的登记与摘除（RV3）

        /// <summary>
        /// 把对象从存活登记表里摘掉：对应槽位置为 null（**解除强引用**），
        /// 并在必要时压缩。
        ///
        /// 必须对两个调用点都做：销毁（`DestroyObject`）与创建回滚。
        /// 历史上只有 `Reset()` 会清理这张表，于是反复 spawn/destroy 的对象
        /// 会在里面累积永久强引用。
        /// </summary>
        private void DetachFromAllObjects(PMNetObject obj)
        {
            if (obj == null)
            {
                return;
            }

            int slot = obj.WorldSlot;
            obj.WorldSlot = -1;

            if (slot < 0 || slot >= _allObjects.Count || !ReferenceEquals(_allObjects[slot], obj))
            {
                return;
            }

            _allObjects[slot] = null;
            if (_liveObjects > 0)
            {
                _liveObjects--;
            }

            _deadSlots++;
            CompactDeadSlots();
        }

        /// <summary>
        /// 压缩登记表里的 null 槽位，重新建立"登记顺序 + 连续下标"。
        ///
        /// 延迟压缩的理由：销毁是高频的（子弹/特效），每次 O(n) 搬会变成 O(n^2)。
        /// 触发条件是"死槽位达到存活数"（即至少一半是空），因此 `Count` 有界于
        /// `2 * MaxObjects`（D-R0-18 的"内存必须有界"）。
        /// </summary>
        private void CompactDeadSlots()
        {
            if (_deadSlots <= 0)
            {
                return;
            }

            // 全部已销毁：直接清空，让"登记表只持有存活对象"在观察上成立
            // （否则会留下一堆 null 槽位，直到下一次压缩阈值）。
            // 注意：**不复位 Id 分配器** —— 编号永不复用（D-R0-02）。
            if (_liveObjects == 0)
            {
                _allObjects.Clear();
                _deadSlots = 0;
                return;
            }

            if (_deadSlots < 32 || _deadSlots < _liveObjects)
            {
                return;
            }

            int write = 0;
            for (int read = 0; read < _allObjects.Count; read++)
            {
                PMNetObject obj = _allObjects[read];
                if (obj == null)
                {
                    continue;
                }

                obj.WorldSlot = write;
                _allObjects[write] = obj;
                write++;
            }

            if (write < _allObjects.Count)
            {
                _allObjects.RemoveRange(write, _allObjects.Count - write);
            }

            _deadSlots = 0;
        }

        // ---------------------------------------------------------------- 声明式初值（RV5）

        /// <summary>
        /// 取某类型的复制描述符（来自 `PMNetRegistry` 的**静态**表，不扫反射）。
        /// 未注册 / 没有属性 ⇒ 返回 null（该类型不走声明式初值）。
        /// </summary>
        private static PMReplicationDescriptor ResolveReplicationDescriptor(uint classId)
        {
            PMNetClassEntry cls;
            if (!PMNetRegistry.TryGetClass(classId, out cls) || cls == null || cls.Rep == null)
            {
                return null;
            }

            PMReplicationDescriptor desc = cls.Rep;
            if (desc.Properties == null || desc.Properties.Length == 0)
            {
                return null;
            }

            return desc;
        }

        /// <summary>
        /// 组装声明式初值（RV5）。
        ///
        /// 线格式：`( varint(slot) | varint(propertyId) | varint(valueLen) | value )*`，
        /// 无条数前缀（自定型，读到末尾为止）。
        ///
        /// **条件过滤是必需的，不是优化**：初值包含哪些属性取决于**接收连接**
        /// （OwnerOnly / SkipOwner / Never / Custom / Dynamic…），全量写进去等于把
        /// 不拥有该对象的连接看不到的值也发出去 —— 那是权限泄露，不是多传了几个字节。
        /// 过滤源 = <see cref="ReplicatedPropertyFilter"/>（由 `PMReplicationChannel`
        /// 接到与增量更新**同一套**条件求值链）。未接线时只发 `PMCond.None`。
        /// </summary>
        private byte[] BuildReplicatedInitialState(PMNetObject obj, PMNetConnection connection)
        {
            PMReplicationDescriptor desc = ResolveReplicationDescriptor(obj.ClassId);
            if (desc == null)
            {
                return null;
            }

            Func<PMNetObject, PMNetConnection, int, bool> filter = ReplicatedPropertyFilter;
            _repStateWriter.Reset();

            for (int slot = 0; slot < desc.Properties.Length; slot++)
            {
                PMPropertyDescriptor prop = desc.Properties[slot];

                if (prop.Condition == PMCond.Never)
                {
                    // `Never` 在 legacy 里恒为 false，无条件不发。
                    // 这里**先于**过滤器判定：即使接线方给了一个"全部放行"的过滤器，
                    // 也绝不能把声明为 Never 的属性发出去（纵深防御）。
                    continue;
                }

                bool include;
                if (filter != null)
                {
                    try
                    {
                        include = filter(obj, connection, slot);
                    }
                    catch (Exception ex)
                    {
                        // 条件求值出问题时的安全方向是**不发**（不是默认全发）：
                        // 漏发可由复制层的"基线缺失 ⇒ 全量"补齐，多发就是泄露。
                        WarnInternal("复制条件的求值委托抛出（ClassId=" + desc.ClassId + " 槽位 " + slot
                                     + "）：" + ex.GetType().Name + " " + ex.Message
                                     + "；该槽位不进入 Create 初值");
                        include = false;
                    }
                }
                else
                {
                    // 未接线时的明确安全行为：只发无条件属性。
                    include = prop.Condition == PMCond.None;
                }

                if (!include)
                {
                    continue;
                }

                if (prop.Writer == null)
                {
                    _stats.ProtocolErrors++;
                    WarnInternal("槽位 " + slot + "（" + prop.MemberName + "）缺少 Writer，跳过 Create 初值");
                    continue;
                }

                _repValueWriter.Reset();
                prop.Writer(obj, _repValueWriter);
                int valueLength = _repValueWriter.Length;

                _repStateWriter.WriteVarint((ulong)slot);
                _repStateWriter.WriteVarint((ulong)prop.PropertyId);
                _repStateWriter.WriteVarint((ulong)valueLength);
                if (valueLength > 0)
                {
                    _repStateWriter.WriteRawBytes(_repValueWriter.GetBuffer(), 0, valueLength);
                }
            }

            if (_repStateWriter.Length == 0)
            {
                return null;
            }

            return _repStateWriter.ToArray();
        }

        /// <summary>
        /// 应用声明式初值（接收侧）。写入的是**刚创建、尚未发布**的对象，
        /// 因此原子性由"失败就回滚整个创建"保证，无需暂存对象。
        ///
        /// 逐条校验：条数不超过描述符属性数、槽位不越界、属性 ID 一致、
        /// 值能**恰好读完**（多一字节少一字节都算失败）。失败 ⇒ 整个创建不发布。
        /// </summary>
        private bool TryApplyReplicatedInitialState(PMNetObject obj, byte[] repState, out string error)
        {
            error = null;

            PMReplicationDescriptor desc = ResolveReplicationDescriptor(obj.ClassId);
            if (desc == null)
            {
                error = "收到声明式初值，但本端没有 ClassId=" + obj.ClassId + " 的复制描述符";
                return false;
            }

            PMNetReader reader = new PMNetReader(repState);
            int entries = 0;

            while (!reader.IsAtEnd)
            {
                if (entries >= desc.Properties.Length)
                {
                    error = "初值条数超过描述符属性数（" + desc.Properties.Length + "）";
                    return false;
                }

                int slot;
                int propertyId;
                int valueLength;
                byte[] raw;

                try
                {
                    slot = checked((int)reader.ReadVarint());
                    propertyId = checked((int)reader.ReadVarint());
                    valueLength = checked((int)reader.ReadVarint());
                    raw = reader.ReadRawBytesCopy(valueLength);
                }
                catch (Exception ex)
                {
                    error = "初值编码非法：" + ex.GetType().Name + " " + ex.Message;
                    return false;
                }

                if (slot < 0 || slot >= desc.Properties.Length)
                {
                    error = "初值引用了越界槽位 " + slot;
                    return false;
                }

                PMPropertyDescriptor prop = desc.Properties[slot];
                if (prop.PropertyId != propertyId)
                {
                    // D-R0-46 的运行期兜底：两端描述符不同 ⇒ 当场判定。
                    error = "初值的属性 ID 不一致：槽位 " + slot + " 本端=" + prop.PropertyId
                            + " 远端=" + propertyId;
                    return false;
                }

                if (prop.Reader == null)
                {
                    error = "槽位 " + slot + "（" + prop.MemberName + "）没有 Reader";
                    return false;
                }

                PMNetReader valueReader = new PMNetReader(raw);

                try
                {
                    prop.Reader(obj, valueReader);
                }
                catch (Exception ex)
                {
                    error = "槽位 " + slot + " 初值解码失败：" + ex.GetType().Name + " " + ex.Message;
                    return false;
                }

                if (!valueReader.IsAtEnd)
                {
                    error = "槽位 " + slot + " 初值长度与内容不符（余 "
                            + (raw.Length - valueReader.Consumed) + " 字节未消费）";
                    return false;
                }

                entries++;
            }

            return true;
        }

        private void WarnInternal(string message)
        {
            Action<string> handler = Warn;
            if (handler != null)
            {
                handler(message);
            }
        }
    }
}
