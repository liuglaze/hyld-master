// R3-B：PMR3 网络对象的运行时接线（契约 Docs/plans/net-r3-control-contract.md §7.4）。
//
// 这个类只做四件框架级的事，不承载任何业务状态机：
//   1) Register()          —— 调生成产物的显式注册表（零反射），并把协议摘要封板；
//   2) ProtocolHash        —— 暴露封板摘要，供 Lobby/票据/端点摘要校验使用（**唯一来源**）；
//   3) Attach(world,bridge)—— 把「世界工厂 / OnRep 分发表 / 生成 RemoteSender」接起来；
//   4) SpawnPlayer(...)    —— 服务端唯一创建入口：Authority 角色 + 唯一 owner + 登记复制。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0，可与 Unity 汇编共享，也可被 net8.0 门禁直编）。
// 本文件**不得**引用 UnityEngine —— 门禁 Tools/PMR3RuntimeTest 直接编真实源码来验证核心路径。
//
// 契约依据：
//   - §7.4「Runtime.Attach 注册 World 工厂 / OnRep / 生成 RemoteSender」；
//   - §7.4「SpawnPlayer 唯一 owner，避免双身份」；
//   - §3.9.4「不得用 GameObject 实例地址、数组下标或单个 AttackId 混代身份」——
//     因此这里只按生成的稳定 ClassId 工作，不做任何类型名/反射匹配。

using System;
using System.Collections.Generic;
using System.Globalization;
using PMNet.Session;

namespace PMNet.R3
{
    /// <summary>
    /// PMR3 声明对象的运行时门面（契约 §7.4 的冻结 API 面）。
    ///
    /// 线程纪律：只允许在**主线程**（Unity Update / 门禁主循环）调用。
    /// 内部用静态字典记住「哪个世界接的是哪条桥」，因为 RemoteSender 是生成物里的
    /// **单个静态接缝**（签名 `Action&lt;PMNetObject, ushort, PMRpcWriter&gt;`，不带桥参数）。
    /// 靠 `target.World` 反查桥，既避免「第二个世界接进来后发错桥」，
    /// 也让门禁能同进程并跑多个世界（E2E/会话门禁都是这么做的）。
    /// </summary>
    public static class PMR3Runtime
    {
        /// <summary>
        /// 固定测试碰撞场景的摘要（契约 §7.4 冻结值）。
        ///
        /// 它标识的是**运行时程序化构建的那套固定布局**（地板 + 一面墙的 BoxCollider 尺寸/中心），
        /// 不是任何旧地图的摘要，也不声称能覆盖旧地图（§7.4 原文：「不声称旧地图摘要」）。
        /// </summary>
        public const uint CollisionDigest = 0x52334201u;

        /// <summary>是否已经执行过 <see cref="Register"/>（生成表的注册是**一次性**的）。</summary>
        private static bool _registered;

        /// <summary>世界 → 桥。仅主线程访问。</summary>
        private static readonly Dictionary<PMNetWorld, PMNetSessionBridge> Bridges =
            new Dictionary<PMNetWorld, PMNetSessionBridge>();

        /// <summary>已经注册过类工厂/OnRep 分发表的世界（`PMNetWorld.RegisterClass` 重复注册会抛）。</summary>
        private static readonly Dictionary<PMNetWorld, bool> AttachedWorlds =
            new Dictionary<PMNetWorld, bool>();

        /// <summary>
        /// 本端副本被创建时触发（**只在客户端侧发生**：DS 的权威副本由 `PMNetWorld.Spawn` 创建，
        /// 不经过 `OnReplicatedCreate`）。
        ///
        /// 宿主（客户端会话宿主）据此决定「这个副本是不是本地的、要不要发一次上行探针」。
        /// 用事件而不是让宿主反射找类型（契约 §7.4 原文），也避免了在复制回调里直接发送
        /// （那时正处在入站 drain 中，发送要等本帧 flush；宿主改在自己帧步里发更清晰）。
        /// </summary>
        public static event Action<PMR3Player> PlayerReplicated;

        /// <summary>服务端权威副本被创建时触发（<see cref="SpawnPlayer"/> 成功之后）。</summary>
        public static event Action<PMR3Player> PlayerSpawned;

        /// <summary>当前是否还有 PlayerReplicated 订阅者（宿主用于断言 static 事件确实被清掉）。</summary>
        public static bool HasPlayerReplicatedHandlers { get { return PlayerReplicated != null; } }

        /// <summary>当前是否还有 PlayerSpawned 订阅者。</summary>
        public static bool HasPlayerSpawnedHandlers { get { return PlayerSpawned != null; } }

        /// <summary>诊断出口。默认丢弃；宿主接到项目日志。</summary>
        public static Action<string> Warn;

        /// <summary>
        /// 本程序集的协议摘要（由生成表的 `Seal(...)` 现算得到）。
        ///
        /// **必须在任何票据/端点摘要比对之前调用 <see cref="Register"/>**，否则这里返回 0，
        /// 而 0 在契约里是「未声明」的哨兵值，比对必然失败（这是有意的失败关闭）。
        /// </summary>
        public static uint ProtocolHash { get { return PMNetRegistry.ProtocolHash; } }

        /// <summary>生成表是否已封板（诊断用；未封板时 <see cref="ProtocolHash"/> 为 0）。</summary>
        public static bool IsRegistered { get { return PMNetRegistry.IsSealed; } }

        // =================================================================================
        //  注册
        // =================================================================================

        /// <summary>
        /// 注册生成产物（`PMNet.Generated.PMNetGeneratedRegistry.RegisterAll()`）。
        ///
        /// 幂等：重复调用直接返回。**必须早于任何连接激活**（契约 §3.9.4 的「先注册再接线」），
        /// 否则激活时的摘要一致性校验会拿到 0 并拒绝。
        /// </summary>
        public static void Register()
        {
            if (_registered && PMNetRegistry.IsSealed)
            {
                return;
            }

            global::PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
            _registered = true;
        }

        // =================================================================================
        //  接线
        // =================================================================================

        /// <summary>
        /// 把一个世界与它的会话桥接起来（契约 §7.4）：
        ///   - 注册本类的世界工厂（接收侧靠 `ClassId` 选构造工厂；零反射）；
        ///   - 注册 OnRep 分发表（生成物的 `PMNet_OnRepDispatch`）；
        ///   - 安装生成 `RemoteSender`（经世界反查到这条桥的 `SendRpc`）。
        ///
        /// 幂等到「同一 (世界, 桥) 重复调用无副作用」。两侧都该调：DS 侧为权威副本，
        /// 客户端侧为接收副本 + 上行 RPC。
        /// </summary>
        public static void Attach(PMNetWorld world, PMNetSessionBridge bridge)
        {
            if (world == null) { throw new ArgumentNullException("world"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }

            if (!ReferenceEquals(world, bridge.World))
            {
                throw new ArgumentException("世界与桥不是同一个世界实例", "bridge");
            }

            // 1) 世界工厂：`RegisterClass` 对同一 ClassId 重复注册会抛，因此先查再注册。
            if (!world.IsClassRegistered(PMR3Player.PMGeneratedClassId))
            {
                world.RegisterClass(PMR3Player.PMGeneratedClassId, CreatePlayerInstance);
            }

            AttachedWorlds[world] = true;

            // 2) OnRep 分发表（字典赋值，天然幂等）。
            if (bridge.Replication != null)
            {
                bridge.Replication.RegisterOnRepDispatcher(
                    PMR3Player.PMGeneratedClassId, PMR3Player.PMR3DispatchOnRep);
            }

            // 3) 桥登记（同一世界重复 Attach 时用最后一次的桥；进程内正常只有一条）。
            Bridges[world] = bridge;

            // 4) 生成物的 RemoteSender 是**单个静态接缝**，因此这里只装一次分发器：
            //    它按 `target.World` 反查桥，避免多世界并跑时发错桥。
            global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = SendRemoteRpc;
        }

        /// <summary>
        /// 解除一条世界的接线并清掉静态状态（客户端退出 / 门禁复位用）。
        ///
        /// **static 事件必须在这里清掉**：事件订阅会强引用宿主对象，
        /// 不清会让「退出后再 Enter」拿到上一次的宿主持有者。
        /// </summary>
        public static void Detach(PMNetWorld world)
        {
            if (world != null)
            {
                Bridges.Remove(world);
                AttachedWorlds.Remove(world);
            }

            if (Bridges.Count == 0)
            {
                global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;
            }
        }

        /// <summary>清空全部静态接线与事件订阅（门禁复位 / 进程收尾）。</summary>
        public static void Shutdown()
        {
            Bridges.Clear();
            AttachedWorlds.Clear();
            global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;
            PlayerReplicated = null;
            PlayerSpawned = null;
        }

        /// <summary>某个世界是否已经接好线（宿主用于拒绝「未接线就 Spawn」）。</summary>
        public static bool IsAttached(PMNetWorld world)
        {
            return world != null && AttachedWorlds.ContainsKey(world);
        }

        /// <summary>取某个世界接的桥（找不到返回 null）。</summary>
        public static PMNetSessionBridge GetBridge(PMNetWorld world)
        {
            PMNetSessionBridge bridge;
            if (world == null || !Bridges.TryGetValue(world, out bridge))
            {
                return null;
            }

            return bridge;
        }

        private static PMNetObject CreatePlayerInstance()
        {
            return new PMR3Player();
        }

        /// <summary>
        /// 生成 `RemoteSender` 的实现：按目标对象所属世界反查桥，再走桥的 `SendRpc`。
        ///
        /// 为什么不能直接把 `bridge.SendRpc` 绑上去：它的返回值是 `bool`，而接缝是 `Action&lt;...&gt;`；
        /// 更重要的是接缝不带桥参数 —— 直接绑会把「多世界」变成「最后一个世界赢」。
        /// </summary>
        private static void SendRemoteRpc(PMNetObject target, ushort rpcId, PMRpcWriter write)
        {
            if (target == null)
            {
                return;
            }

            PMNetSessionBridge bridge;
            if (target.World == null || !Bridges.TryGetValue(target.World, out bridge) || bridge == null)
            {
                WarnInternal("RPC 目标所属世界没有接线（ClassId=" + target.ClassId
                             + " RpcId=" + rpcId + "），本次调用被丢弃");
                return;
            }

            if (!bridge.SendRpc(target, rpcId, write))
            {
                WarnInternal("RPC 发送被桥拒绝（ClassId=" + target.ClassId + " RpcId=" + rpcId + "）");
            }
        }

        // =================================================================================
        //  服务端唯一创建入口
        // =================================================================================

        /// <summary>
        /// 创建一个玩家副本（**服务端唯一入口**，契约 §7.4）：
        ///   - 只在权威侧（`world.IsServer`）允许；
        ///   - 角色固定 Authority（由 `PMNetWorld.Spawn` 设置）；
        ///   - owner **只有一处**：`OwnerConnection`（`PMR3Player.GetNetConnection()` 直返它）；
        ///   - 先绑 owner 与初值、再 `Spawn`，最后登记复制 ——
        ///     初始状态在派发生命周期批次时现取，因此初值必须在上线之前就位。
        ///
        /// 失败一律返回 null 并告警，绝不「创建了但没登记」地留下半成品。
        /// </summary>
        public static PMR3Player SpawnPlayer(PMNetWorld world, PMNetSessionBridge bridge,
                                             PMTransportConnection owner)
        {
            if (world == null) { throw new ArgumentNullException("world"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (owner == null) { throw new ArgumentNullException("owner"); }

            if (!world.IsServer)
            {
                WarnInternal("SpawnPlayer 只能在权威侧调用（客户端副本由生命周期消息创建）");
                return null;
            }

            if (!ReferenceEquals(bridge.World, world))
            {
                WarnInternal("SpawnPlayer 的桥不属于该世界");
                return null;
            }

            if (!IsAttached(world))
            {
                WarnInternal("SpawnPlayer 前必须先 PMR3Runtime.Attach(world, bridge)");
                return null;
            }

            if (!owner.IsReady)
            {
                WarnInternal("SpawnPlayer 的 owner 连接未就绪：" + owner.Name);
                return null;
            }

            PMR3Player player = new PMR3Player();

            // 唯一 owner 来源。刻意**不**设 Owner / NetConnection，避免出现第二条冲突的拥有者链。
            player.OwnerConnection = owner;

            // 权威初值：uid 只来自已认证连接的身份（不从业务包自报 uid 采纳）。
            player.PMNet_Set_uid(owner.Identity.Uid);

            if (!world.Spawn(player, PMR3Player.PMGeneratedClassId))
            {
                WarnInternal("world.Spawn 拒绝了玩家副本（uid=" + owner.Identity.Uid + "）");
                return null;
            }

            bridge.RegisterReplicatedObject(player);

            Action<PMR3Player> spawned = PlayerSpawned;
            if (spawned != null)
            {
                spawned(player);
            }

            return player;
        }

        // =================================================================================
        //  内部事件转发（由 PMR3Player.OnReplicatedCreate 调用）
        // =================================================================================

        /// <summary>把「本端副本创建完成」转发给宿主事件（见 <see cref="PlayerReplicated"/>）。</summary>
        internal static void NotifyPlayerReplicated(PMR3Player player)
        {
            if (player == null)
            {
                return;
            }

            Action<PMR3Player> handler = PlayerReplicated;
            if (handler == null)
            {
                return;
            }

            // 订阅方（宿主）只做入队/记账，不做网络发送，因此这里不隔离异常的必要性不高；
            // 但仍逐订阅者隔离：一个宿主的 bug 不该让整条复制链炸掉。
            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<PMR3Player>)list[i])(player);
                }
                catch (Exception ex)
                {
                    WarnInternal("PlayerReplicated 订阅者异常：" + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        private static void WarnInternal(string message)
        {
            Action<string> handler = Warn;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>单行诊断摘要（宿主启动日志用）。</summary>
        public static string Describe()
        {
            return "PMR3 registered=" + _registered
                   + " sealed=" + PMNetRegistry.IsSealed
                   + " hash=0x" + ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                   + " digest=0x" + CollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                   + " worlds=" + Bridges.Count;
        }
    }
}
