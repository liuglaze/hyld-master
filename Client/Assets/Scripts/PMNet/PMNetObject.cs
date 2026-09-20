namespace PMNet
{
    /// <summary>
    /// 网络对象基类（对应 UE 的 AActor / 复制子对象 UObject 的地位）。
    ///
    /// 这是「属性复制」得以存在的前提：hyld 当前的状态散在若干 Dictionary 里
    /// （playerHp / playerMana / playerSuperEnergy / playerPositions），没有对象化宿主，
    /// 因此无从谈「某个属性变脏了、该发给哪条连接、相对哪份基线发增量」。
    ///
    /// 目标形态下，可复制状态的宿主必须继承本类（见计划 §3.3 / §4.1）。
    /// </summary>
    public abstract class PMNetObject
    {
        /// <summary>
        /// 网络唯一标识（对应 UE 的 NetGUID / Iris 的 NetRefHandle）。
        /// `Value == 0` 表示尚未分配；单会话内单调分配且**永不复用**（D-R0-02）。
        /// </summary>
        public PMNetId NetId;

        /// <summary>类型 ID（生成器分配的稳定 ID，见 D-R0-49）。接收侧靠它选择构造工厂。</summary>
        public uint ClassId;

        /// <summary>原型 ID；0 表示默认原型。</summary>
        public uint ArchetypeId;

        /// <summary>所属世界。未登记时为 null。</summary>
        public PMNetWorld World;

        /// <summary>生命周期状态。只有 Active 的对象参与复制与引用解析。</summary>
        public PMNetObjectState State = PMNetObjectState.Unregistered;

        /// <summary>
        /// 拥有者对象（对应 UE 的 `AActor::Owner`）。
        ///
        /// 它是**引用语义**而非包含语义：仅用于推导"这个对象属于哪条连接"，
        /// 不表达生命周期（拥有者销毁不会自动销毁被拥有者——UE 同样如此）。
        /// </summary>
        public PMNetObject Owner;

        /// <summary>
        /// 直接绑定的连接（对应 `APlayerController::NetConnection`）。
        ///
        /// 只有"控制器类"对象才应该设置它；它是 Owner 链的**终点**，
        /// 也是 RPC 归属校验（D-R0-42）的最终依据。
        /// </summary>
        public PMNetConnection NetConnection;

        /// <summary>当前副本的网络角色。</summary>
        public PMNetRole Role = PMNetRole.None;

        /// <summary>当前进程的网络模式。</summary>
        public PMNetMode NetMode = PMNetMode.Standalone;

        /// <summary>拥有者连接（对应 UE 的 Owning Connection）。RPC 归属校验与 OwnerOnly 条件都依赖它。</summary>
        public PMNetConnection OwnerConnection;

        /// <summary>
        /// 在本世界"存活对象列表"里的下标（由 `PMNetWorld` 维护；-1 = 未登记）。
        ///
        /// 用途：销毁/回滚时能 O(1) 把该对象从世界列表里摘掉（置空槽位 + 延迟压缩），
        /// 而不必整表扫描。它是纯粹的记账细节，业务不得读写。
        /// </summary>
        internal int WorldSlot = -1;

        // ---------------- 调度参数（对应 UE 的 relevancy / frequency / dormancy）----------------

        /// <summary>是否对所有连接始终相关（对应 bAlwaysRelevant）。</summary>
        public bool AlwaysRelevant;

        /// <summary>
        /// 距离裁剪半径的平方（对应 NetCullDistanceSquared）。负值表示不做距离裁剪。
        /// 用平方比较以避免每帧开方。
        /// </summary>
        public float CullDistanceSquared = -1f;

        /// <summary>网络更新频率（对应 NetUpdateFrequency，单位 Hz）。参考 ProjectMecury 的角色默认 30Hz。</summary>
        public float NetUpdateFrequency = 30f;

        /// <summary>网络更新频率下限（对应 MinNetUpdateFrequency）。</summary>
        public float MinNetUpdateFrequency = 2f;

        /// <summary>休眠策略（对应 ENetDormancy）。</summary>
        public PMNetDormancy Dormancy = PMNetDormancy.Never;

        /// <summary>是否已休眠。休眠期间常规复制不参与调度，属性变化时由 FlushNetDormancy 唤醒。</summary>
        public bool IsDormant;

        /// <summary>本对象的脏位（Push Model 语义）。</summary>
        public PMDirtyTracker Dirty;

        private PMRepList _repList;

        /// <summary>是否为权威副本（对应 UE 的 HasAuthority）。</summary>
        public bool HasAuthority
        {
            get { return Role == PMNetRole.Authority; }
        }

        // ---------------- Owner 链（对应 UE 的 GetNetConnection / GetNetOwningPlayer）----------------

        /// <summary>
        /// 推导本对象属于哪条连接。
        ///
        /// 逐字对齐 UE 的 `AActor::GetNetConnection()`（`Actor.cpp:1968`）：
        /// <code>
        /// return Owner ? Owner-&gt;GetNetConnection() : nullptr;
        /// </code>
        ///
        /// 这是 RPC 归属校验的判据：UE 的 `UActorChannel::ReplicateActor` 用它算出
        /// `RepFlags.bNetOwner`（`DataChannel.cpp:3984`），而 `UNetDriver::ShouldCallRemoteFunction`
        /// 再用 `bNetOwner` 决定服务端要不要执行（`NetDriver.cpp:8041`）。
        ///
        /// **为什么要有子类覆写**：UE 的 `APawn` / `APlayerController` 都覆写了这个函数，
        /// 且覆写规则**不对称**（见 <see cref="PMNetPawnObject"/> / <see cref="PMNetControllerObject"/>）。
        /// 直接把链路简化成"沿 Owner 一路往上"会让控制器类对象推导出错误归属。
        /// </summary>
        public virtual PMNetConnection GetNetConnection()
        {
            return Owner != null ? Owner.GetNetConnection() : null;
        }

        /// <summary>
        /// 推导"拥有本对象的玩家"。在服务端形态下，一个 `UPlayer` 就等价于一条 `UNetConnection`，
        /// 因此这里返回连接。
        ///
        /// 逐字对齐 UE 的 `AActor::GetNetOwningPlayer()`（`Actor.cpp:1979`）：
        /// <code>
        /// if (GetLocalRole() == ROLE_Authority) { if (Owner) return Owner-&gt;GetNetOwningPlayer(); }
        /// return nullptr;
        /// </code>
        /// 注意**权威角色是前置条件**：非权威副本永远推不出拥有者玩家。
        /// 与 <see cref="GetNetConnection"/> 不同，这个不用于 RPC 归属，而用于
        /// 相关性判定（M06）。
        /// </summary>
        public virtual PMNetConnection GetNetOwningPlayer()
        {
            if (Role == PMNetRole.Authority && Owner != null)
            {
                return Owner.GetNetOwningPlayer();
            }

            return null;
        }

        // ---------------- 生命周期钩子（M04）----------------

        /// <summary>
        /// 序列化初始状态（服务端发创建记录时调用）。
        ///
        /// D-R0-16 要求初始状态与创建原子到达，因此它被写进创建记录内部，
        /// 而不是另开一条消息。M06 落地后，默认实现会改为"遍历复制属性全量写出"。
        /// </summary>
        protected internal virtual void OnSerializeInitialState(PMNetWriter writer)
        {
        }

        /// <summary>
        /// 反序列化初始状态（客户端收到创建记录时调用）。
        ///
        /// 抛出异常会被世界接住并**回滚整个创建**（对象不可信）。因此这里不要用
        /// 异常做业务分支——它表达的是协议/实现错误。
        /// </summary>
        protected internal virtual void OnDeserializeInitialState(PMNetReader reader)
        {
        }

        /// <summary>本端副本被创建后回调（初始状态已应用）。此时对象已登记进世界。</summary>
        protected internal virtual void OnReplicatedCreate()
        {
        }

        /// <summary>本端副本被销毁时回调。此时对象已从世界索引摘除。</summary>
        protected internal virtual void OnReplicatedDestroy(PMObjectDestroyReason reason)
        {
        }

        /// <summary>
        /// 取本对象的复制属性注册表（对应 UE 的 GetLifetimeReplicatedProps）。
        /// 首次调用时收集并缓存；属性集合是静态的，运行时不需要反复重建。
        /// </summary>
        public PMRepList GetLifetimeReplicatedProps()
        {
            if (_repList == null)
            {
                _repList = new PMRepList();
                CollectLifetimeReplicatedProps(_repList);
            }

            return _repList;
        }

        /// <summary>
        /// 子类重写此方法注册自己的复制属性与条件（对应 UE 里 DOREPLIFETIME_WITH_PARAMS_FAST 的位置）。
        /// </summary>
        protected virtual void CollectLifetimeReplicatedProps(PMRepList outProps)
        {
        }

        // ---------------- 相关性（对应 UE 的 IsNetRelevantFor）----------------

        /// <summary>
        /// 判断本对象对某条连接是否相关。
        /// 复制是 Connection × Object 的二维决策，不是广播（见计划 §3.3）。
        /// </summary>
        public virtual bool IsNetRelevantFor(PMNetConnection connection, PMNetObject viewer)
        {
            if (connection == null)
            {
                return false;
            }

            if (AlwaysRelevant)
            {
                return true;
            }

            if (CullDistanceSquared < 0f)
            {
                return true;
            }

            // viewer 为 null 或该连接没有观察位置时，不做距离裁剪（保持可见，避免误裁）。
            if (viewer == null)
            {
                return true;
            }

            float vx;
            float vy;
            float vz;
            if (!connection.TryGetViewerLocation(out vx, out vy, out vz))
            {
                return true;
            }

            float ox;
            float oy;
            float oz;
            if (!TryGetReplicationLocation(out ox, out oy, out oz))
            {
                return true;
            }

            float dx = ox - vx;
            float dy = oy - vy;
            float dz = oz - vz;
            return (dx * dx + dy * dy + dz * dz) <= CullDistanceSquared;
        }

        /// <summary>取出用于距离裁剪的位置；返回 false 表示本对象不参与距离裁剪。</summary>
        protected virtual bool TryGetReplicationLocation(out float x, out float y, out float z)
        {
            x = 0f;
            y = 0f;
            z = 0f;
            return false;
        }

        // ---------------- 属性条件（对应 UE 的 COND_*）----------------

        /// <summary>
        /// 判断某属性是否允许发给该连接（对应 UE 的 Replication Condition 过滤）。
        /// 条件只过滤属性，不代表对象整体相关性。
        /// </summary>
        public bool ShouldReplicateProperty(int propertyIndex, PMNetConnection connection)
        {
            PMLifetimeProperty registered;
            if (!GetLifetimeReplicatedProps().TryGet(propertyIndex, out registered))
            {
                return false;
            }

            bool isOwnerConnection = connection != null && ReferenceEquals(connection, OwnerConnection);

            switch (registered.Condition)
            {
                case PMCond.None:
                    return true;

                case PMCond.OwnerOnly:
                    return isOwnerConnection;

                case PMCond.SkipOwner:
                    return !isOwnerConnection;

                case PMCond.SimulatedOnly:
                    return !isOwnerConnection;

                case PMCond.AutonomousOnly:
                    return isOwnerConnection;

                case PMCond.Never:
                    return false;

                case PMCond.Custom:
                    // 运行期覆盖开关由复制层按连接查询（对应 SetCustomIsActiveOverride）。
                    // 未设置覆盖时，UE 的语义是「默认按总是复制处理」。
                    return true;

                case PMCond.Dynamic:
                    // 运行期可把条件改成另一条；未覆盖时同样默认放行。
                    return true;

                default:
                    // 未实现的条件（InitialOnly / *Replay* / NetGroup 等）在生成器声明阶段
                    // 就已被拒绝，运行期走到这里说明声明校验被绕过了 —— 明确拒绝而非静默放行。
                    return false;
            }
        }

        // ---------------- 标脏与休眠 ----------------

        /// <summary>
        /// 标记属性变脏。必须在真实赋值之后、紧邻赋值处调用：
        /// 漏标会导致该属性静默不同步，未修改却标会削弱 Push Model 的收益（见计划 §3.3）。
        /// </summary>
        public void MarkPropertyDirty(int propertyIndex)
        {
            Dirty.Mark(propertyIndex);

            // 属性变化即唤醒：对应 UE 的 FlushNetDormancy 语义。
            if (IsDormant && Dormancy == PMNetDormancy.Partial)
            {
                FlushNetDormancy();
            }
        }

        /// <summary>整对象标脏（属性集合变化、初始同步等）。</summary>
        public void MarkAllPropertiesDirty()
        {
            Dirty.MarkAll();

            if (IsDormant && Dormancy == PMNetDormancy.Partial)
            {
                FlushNetDormancy();
            }
        }

        /// <summary>设置休眠策略（对应 SetNetDormancy）。</summary>
        public void SetNetDormancy(PMNetDormancy dormancy)
        {
            Dormancy = dormancy;
            IsDormant = dormancy == PMNetDormancy.Initial || dormancy == PMNetDormancy.Partial;
        }

        /// <summary>唤醒并标脏整对象，使其重新参与复制调度（对应 FlushNetDormancy）。</summary>
        public void FlushNetDormancy()
        {
            IsDormant = false;
            Dirty.MarkAll();
        }
    }
}
