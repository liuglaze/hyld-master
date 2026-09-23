// R5-B2a：投射物的**声明层**网络对象（契约 Docs/plans/net-r5-network-contract.md「声明层」）。
//
// 本文件是 `Tools/PMNetGen --decl-gen Client/Assets/Scripts/PMR3` 的输入之一，它只声明：
//   - 唯一复制属性 `_projectileSnapshotV1`（byte[]，纯字节载荷）；
//   - 它的 RepNotify（只通知/入队，不解码）；
//   - 创建/更新/销毁的全局事件出口 PMR5ProjectileEvents。
//
// 三条纪律（都是契约原文要求）：
//   1) **不引用 PMProjectile 纯核心，也不引用 Coordinator**：载荷语义在 driver/codec，
//      声明层只负责「把这片字节可靠地搬到对端」。这样只编「声明层 + PMNet 核心」的
//      旧消费者工程（Tools/PMR3RuntimeTest、Tools/PMR3IntegrationTest 等）无需引入核心。
//   2) 事件必须**按 obj.World 筛选**：一个宿主只应看到自己那个世界的事件；
//      同进程多世界并跑（各门禁的 Rig）时不得串台。
//   3) **各宿主 Dispose 取消自己的订阅**；一个 world 退出的清理动作不得清掉别的 world 的事件。
//      `ClearAll` 只在进程整体 Shutdown（PMR3Runtime.Shutdown）时调用。

using System;
using System.Collections.Generic;
using PMNet;

namespace PMNet.R3
{
    /// <summary>
    /// 投射物副本的声明对象（`[PMNetworkObject]`，纯字段 + 单一复制属性）。
    ///
    /// 生命周期：由 DS 侧会话驱动在**预留 NetId 确认后**经世界创建（B2b），
    /// 且**初始 snapshot 已经写好才上线**（契约：Pending 期间无客户端幽灵对象）。
    /// 本类刻意**不**提供 Spawn 辅助函数——驱动自己写初值，再上线并登记复制。
    ///
    /// 客户端侧经生命周期消息收到副本触发 <see cref="OnReplicatedCreate"/>，
    /// 此时初始状态已经全部到位（D-R0-16），因此创建事件里 `ProjectileSnapshotPayload` 就是可用的。
    /// </summary>
    [PMNetworkObject]
    public partial class PMR5Projectile : PMNetObject
    {
        /// <summary>
        /// 投射物权威快照载荷（R5-B1 codec 的字节，本层不解释结构）。
        ///
        /// 唯一复制属性：`byte[]` 让生成物只负责「可靠搬运这片字节」，
        /// 而不在生成层重新发明投射物字段（与 `PMR3Player._movementSnapshotV1` 同一条理由）。
        /// DS 侧只在写出新快照时经 <see cref="PublishProjectileSnapshot"/> 标脏；
        /// 客户端侧只读，OnRep 只做「通知/入队」。
        /// </summary>
        [PMReplicated]
        private byte[] _projectileSnapshotV1 { get; set; }

        /// <summary>本副本上 <see cref="OnReplicatedCreate"/> 被调用的次数（本地记账，非复制）。</summary>
        public int ReplicatedCreateCount;

        /// <summary>本副本上 <see cref="OnReplicatedDestroy"/> 被调用的次数（本地记账，非复制）。</summary>
        public int ReplicatedDestroyCount;

        /// <summary>
        /// 最近一次复制到的投射物快照载荷（**只读语义**：调用方不得就地改写）。
        /// 每次复制更新都会换成新数组，因此借出期间不会被就地改写。
        /// </summary>
        public byte[] ProjectileSnapshotPayload { get { return _projectileSnapshotV1; } }

        /// <summary>
        /// DS 侧写投射物快照的唯一出口（「赋值即标脏」，Push 模型）。
        /// 自动属性赋值由编织器自动标脏，本方法提供一个
        /// 面向业务的名字，避免调用方直接依赖生成物的命名细节。
        /// </summary>
        public void PublishProjectileSnapshot(byte[] payload)
        {
            _projectileSnapshotV1 = payload;
        }

        /// <summary>
        /// 复制属性 `_projectileSnapshotV1` 变化的 RepNotify。
        /// **只通知/入队**（契约原文）：不在这里解码、不在这里改任何驱动状态。
        /// </summary>
        [PMRepNotify(nameof(_projectileSnapshotV1))]
        private void OnRep_ProjectileSnapshot()
        {
            PMR5ProjectileEvents.NotifyUpdated(this);
        }

        /// <summary>
        /// 本端副本创建完成（初始状态已应用、对象已登记进世界）。
        /// 转发给全局 <see cref="PMR5ProjectileEvents.Created"/>，由宿主按 world 筛选消费。
        ///
        /// 注意：DS 的权威副本由 `PMNetWorld.Spawn*` 创建，**不经过**本回调
        /// （与 `PMR3Player.OnReplicatedCreate` 同一语义），因此服务端侧的创建事件
        /// 由 B2b 的驱动自行发布，本层不越权代发。
        /// </summary>
        protected internal override void OnReplicatedCreate()
        {
            ReplicatedCreateCount++;
            PMR5ProjectileEvents.NotifyCreated(this);
        }

        /// <summary>
        /// 本端副本被销毁（此时对象已从世界索引摘除）。
        /// 转发给全局 <see cref="PMR5ProjectileEvents.Destroyed"/>。
        /// </summary>
        protected internal override void OnReplicatedDestroy(PMObjectDestroyReason reason)
        {
            ReplicatedDestroyCount++;
            PMR5ProjectileEvents.NotifyDestroyed(this, reason);
        }

        /// <summary>
        /// 把生成物的私有 OnRep 分发表转发成 public（与 `PMR3Player.PMR3DispatchOnRep` 同一模式）。
        ///
        /// `PMNet_OnRepDispatch` 是生成物里的 `private static`，只有本类的 partial 能访问；
        /// 而 <see cref="PMR3Runtime.Attach"/> 需要把它注册进复制通道的 OnRep 分发表。
        /// 转发本身不改变任何行为。
        /// </summary>
        public static void PMR5DispatchOnRep(PMNetObject target, ushort onRepMethodId)
        {
            PMNet_OnRepDispatch(target, onRepMethodId);
        }
    }

    /// <summary>
    /// 投射物副本事件的**唯一进程级出口**（B2b 的 driver 消费它，本批次不消费）。
    ///
    /// 设计要点（契约「声明层」原文）：
    ///   - 事件**按 `obj.World` 筛选**：订阅时绑定一个 world，分发时只投给 world 相同的订阅者；
    ///   - 每个订阅是一张**独立凭证**（<see cref="Subscription"/>），宿主 Dispose 只摘掉自己那一张，
    ///     **不会**影响别的 world 的订阅；
    ///   - <see cref="ClearAll"/> 只在进程整体 Shutdown 时调用（一次性清掉全部残留订阅）。
    ///
    /// 分发是**同步**的（同一栈上直接调用订阅者），与复制/生命周期回调的线程纪律一致：
    /// 订阅者只应做「入队/记账」，不得在回调里阻塞或再入发网。
    /// </summary>
    public static class PMR5ProjectileEvents
    {
        /// <summary>
        /// 一张订阅凭证。`Dispose` 幂等：重复调用只摘一次，不会误摘别人的订阅。
        /// </summary>
        public sealed class Subscription : IDisposable
        {
            internal PMNetWorld HostWorld;
            internal Action<PMR5Projectile> OnCreated;
            internal Action<PMR5Projectile> OnUpdated;
            internal Action<PMR5Projectile, PMObjectDestroyReason> OnDestroyed;

            /// <summary>本次订阅绑定的世界（取消后为 null）。</summary>
            public PMNetWorld World { get { return HostWorld; } }

            /// <summary>是否已经取消（诊断/断言用）。</summary>
            public bool IsDisposed { get { return HostWorld == null; } }

            /// <summary>取消本订阅（只影响这一张凭证）。</summary>
            public void Dispose()
            {
                if (HostWorld == null)
                {
                    return;
                }

                HostWorld = null;
                OnCreated = null;
                OnUpdated = null;
                OnDestroyed = null;
                Remove(this);
            }
        }

        private static readonly List<Subscription> Subscriptions = new List<Subscription>(4);

        /// <summary>当前订阅数（门禁断言用）。</summary>
        public static int SubscriptionCount { get { return Subscriptions.Count; } }

        /// <summary>诊断出口（未接线时丢弃）。回调异常不会打断其它订阅者。</summary>
        public static Action<string> Warn;

        /// <summary>
        /// 订阅某个世界的投射物事件（三个回调都可以为 null，表示不关心那一类）。
        /// 返回的凭证必须由宿主在 Dispose 时取消（契约：各宿主 Dispose 取消自己的订阅）。
        /// </summary>
        public static Subscription Subscribe(PMNetWorld world,
                                            Action<PMR5Projectile> onCreated,
                                            Action<PMR5Projectile> onUpdated,
                                            Action<PMR5Projectile, PMObjectDestroyReason> onDestroyed)
        {
            if (world == null) { throw new ArgumentNullException("world"); }

            Subscription sub = new Subscription();
            sub.HostWorld = world;
            sub.OnCreated = onCreated;
            sub.OnUpdated = onUpdated;
            sub.OnDestroyed = onDestroyed;
            Subscriptions.Add(sub);
            return sub;
        }

        /// <summary>进程整体 Shutdown 时的唯一清理入口（清掉全部残留订阅）。</summary>
        public static void ClearAll()
        {
            for (int i = 0; i < Subscriptions.Count; i++)
            {
                Subscription sub = Subscriptions[i];
                sub.HostWorld = null;
                sub.OnCreated = null;
                sub.OnUpdated = null;
                sub.OnDestroyed = null;
            }

            Subscriptions.Clear();
        }

        private static void Remove(Subscription sub)
        {
            for (int i = 0; i < Subscriptions.Count; i++)
            {
                if (ReferenceEquals(Subscriptions[i], sub))
                {
                    Subscriptions.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>发布「副本创建」（由 <see cref="PMR5Projectile.OnReplicatedCreate"/> 调用）。</summary>
        internal static void NotifyCreated(PMR5Projectile obj)
        {
            if (obj == null || obj.World == null)
            {
                return;
            }

            // 先快照订阅集合：订阅者在回调里取消自己（或新增订阅）时，
            // 本次分发仍严格按「进入分发时的那一组」走，不会漏投也不会重复投。
            Subscription[] snapshot = Subscriptions.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Subscription sub = snapshot[i];
                if (!ReferenceEquals(sub.HostWorld, obj.World) || sub.OnCreated == null)
                {
                    continue;
                }

                try
                {
                    sub.OnCreated(obj);
                }
                catch (Exception ex)
                {
                    WarnInternal("Created 订阅者异常：" + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>发布「快照更新」（由 RepNotify 调用）。</summary>
        internal static void NotifyUpdated(PMR5Projectile obj)
        {
            if (obj == null || obj.World == null)
            {
                return;
            }

            Subscription[] snapshot = Subscriptions.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Subscription sub = snapshot[i];
                if (!ReferenceEquals(sub.HostWorld, obj.World) || sub.OnUpdated == null)
                {
                    continue;
                }

                try
                {
                    sub.OnUpdated(obj);
                }
                catch (Exception ex)
                {
                    WarnInternal("Updated 订阅者异常：" + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>发布「副本销毁」（由 <see cref="PMR5Projectile.OnReplicatedDestroy"/> 调用）。</summary>
        internal static void NotifyDestroyed(PMR5Projectile obj, PMObjectDestroyReason reason)
        {
            if (obj == null || obj.World == null)
            {
                return;
            }

            Subscription[] snapshot = Subscriptions.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Subscription sub = snapshot[i];
                if (!ReferenceEquals(sub.HostWorld, obj.World) || sub.OnDestroyed == null)
                {
                    continue;
                }

                try
                {
                    sub.OnDestroyed(obj, reason);
                }
                catch (Exception ex)
                {
                    WarnInternal("Destroyed 订阅者异常：" + ex.GetType().Name + " " + ex.Message);
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
    }
}
