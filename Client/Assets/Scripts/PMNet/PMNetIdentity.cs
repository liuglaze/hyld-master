using System;

namespace PMNet
{
    /// <summary>
    /// 网络对象身份（对应 UE 的 FNetRefHandle / NetGUID，但**刻意简化**）。
    ///
    /// 决策依据：Docs/plans/net-r0-contract.md 的 D-R0-01 / D-R0-02。
    ///
    /// 与 UE 的差异（有意）：
    ///   UE 用 60 位 Id + 4 位 ReplicationSystemId 以支持 PIE / 多世界并存；
    ///   hyld 单进程单世界，因此去掉系统位，只保留「会话内单调 Id + 静态标记」。
    ///
    /// 最重要的不变量：**Id 在单会话内永不复用**（D-R0-02）。
    /// 这一条消除了 UE 侧最麻烦的一类问题——池化对象复用同一网络身份后，
    /// 迟到的包/回调落到「已经不是原来那个对象」的实例上（跨世代串扰）。
    /// 代价是 uint32 会耗尽，但单局绝无可能（42 亿）。
    ///
    /// 前提（由 <see cref="PMNetIdAllocator"/> 保证，见其注释）：
    /// **静态与动态对象共用同一个编号空间**——否则“同号不同类”会被判为不同身份，
    /// 两个对象会拿到相同的 `Value`。
    /// </summary>
    public readonly struct PMNetId : IEquatable<PMNetId>
    {
        /// <summary>无效身份。任何接收此值的 API 都应拒绝而不是静默处理。</summary>
        public static readonly PMNetId Invalid = new PMNetId(0u, false);

        /// <summary>会话内单调编号；0 表示无效。</summary>
        public readonly uint Value;

        /// <summary>是否为关卡静态对象（对应 UE 的静态 NetGUID）。</summary>
        public readonly bool IsStatic;

        public PMNetId(uint value, bool isStatic)
        {
            Value = value;
            IsStatic = isStatic;
        }

        public bool IsValid { get { return Value != 0u; } }

        public bool Equals(PMNetId other)
        {
            return Value == other.Value && IsStatic == other.IsStatic;
        }

        public override bool Equals(object obj)
        {
            return obj is PMNetId && Equals((PMNetId)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Value * 397) ^ (IsStatic ? 1 : 0);
        }

        public override string ToString()
        {
            return IsStatic ? ("Static#" + Value) : ("Net#" + Value);
        }

        public static bool operator ==(PMNetId a, PMNetId b) { return a.Equals(b); }
        public static bool operator !=(PMNetId a, PMNetId b) { return !a.Equals(b); }
    }

    /// <summary>
    /// 身份分配器：会话内单调递增、**永不复用**（D-R0-02）。
    ///
    /// **前提（必须在宿主层保证）**：
    /// 1. **全进程只能有一个分配器实例**。本类目前在 Unity 侧尚无封装约束，
    ///    若各处各 `new` 一个，不同分配器会发出**重复的 `Value`**，
    ///    而“永不复用”只在单个分配器内成立。宿主接入时必须改为单一入口持有。
    /// 2. **只在主线程调用**（D13「局内权威在主线程」）。
    ///    这里刻意**不加锁**：加锁能保证唯一，但会使“分配顺序”变得不确定，
    ///    而顺序本身是网络可见的（同一个 `NetId` 在不同端的创建顺序必须一致）。
    ///    跨线程调用会让 `_next++` 产生重复值，因此下面用线程检查显式封住。
    /// 3. **静态与动态共用同一编号空间**（每次 `Allocate` 都递增），
    ///    因此不需要额外的号段划分就能保证全局唯一。
    ///
    /// 之前的注释把“不加锁”的理由写成了“加锁会导致复用”，那是错的：
    /// `Interlocked` 只保证唯一性。真正的理由是上面的第 2 条。
    /// </summary>
    public sealed class PMNetIdAllocator
    {
        private uint _next = 1u;

        /// <summary>分配所在线程（首次分配时记录），用于挡住跨线程误用。</summary>
        private int _ownerThreadId;

        /// <summary>已分配的最大 Id（诊断用）。首次分配前为 0，与 <see cref="PMNetId.Invalid"/> 同值。</summary>
        public uint LastAllocated { get { return _next - 1u; } }

        public PMNetId Allocate(bool isStatic)
        {
            int current = System.Environment.CurrentManagedThreadId;
            if (_ownerThreadId == 0)
            {
                _ownerThreadId = current;
            }
            else if (_ownerThreadId != current)
            {
                // 不静默返回可能重复的 Id：这是网络可见的身份，必须显式失败。
                throw new InvalidOperationException(
                    "[PMNetIdAllocator] 跨线程分配：分配器只允许在主线程使用（否则 _next++ 会产生重复 Id）。");
            }

            if (_next == 0u)
            {
                // 走到了 2^32 次分配——按契约不得复用，只能显式失败。
                throw new InvalidOperationException("[PMNetIdAllocator] Id 空间耗尽：按 D-R0-02 不得复用身份。");
            }

            PMNetId id = new PMNetId(_next, isStatic);
            _next++;
            return id;
        }
    }

    /// <summary>
    /// 一条连接的会话（D-R0-01）。
    ///
    /// Epoch 在连接建立时分配并随包携带；收到的包 Epoch 与当前不符即丢弃。
    /// 这是「旧连接 / 重连前的迟到包不得作用到新会话对象」的判据。
    /// </summary>
    public sealed class PMSession
    {
        public readonly uint Epoch;
        public readonly bool IsServerSide;

        public PMSession(uint epoch, bool isServerSide)
        {
            Epoch = epoch;
            IsServerSide = isServerSide;
        }

        public override string ToString()
        {
            return "Session#" + Epoch + (IsServerSide ? "(server)" : "(client)");
        }
    }

    /// <summary>
    /// 帧号的命名空间（D-R0-20 / R0_1 U5）。
    ///
    /// UE 侧有四个互不相通的编号：客户端输入序号、DS 权威仿真序号、
    /// 单个 Actor 的累计仿真时间、以及 World 级共享仿真时间。
    /// 它们的数值**没有任何对应关系**，混算会得到看似合理但完全错误的结果。
    /// </summary>
    public enum PMFrameDomain : byte
    {
        /// <summary>未建立 / 不适用。禁止参与差值。</summary>
        None = 0,

        /// <summary>客户端输入流水号（UE 的 ClientInputFrame）。</summary>
        Input = 1,

        /// <summary>权威对某实例真正执行一次仿真的输出号（UE 的 ServerFrame）。</summary>
        AuthorityServer = 2,

        /// <summary>单个实例的累计仿真时间（毫秒）。</summary>
        SimTime = 3,

        /// <summary>进程/世界级共享会话时间（毫秒）。</summary>
        Session = 4,
    }

    /// <summary>
    /// 带命名空间的帧标识（D-R0-20）。
    ///
    /// 关键设计：**跨命名空间相减会抛出**，而不是静默返回一个数字。
    /// 依据：R0_1 U5 要求「四个命名空间严格隔离」；旧实现把客户端预测帧号
    /// 当权威帧号去查历史位置（计划 §2.7 B7），就是这类错误的实例。
    /// </summary>
    public readonly struct PMFrameId : IEquatable<PMFrameId>
    {
        /// <summary>未建立的帧标识（对应 UE 的 INDEX_NONE 语义）。</summary>
        public static readonly PMFrameId None = new PMFrameId(PMFrameDomain.None, 0L);

        public readonly PMFrameDomain Domain;
        public readonly long Value;

        public PMFrameId(PMFrameDomain domain, long value)
        {
            Domain = domain;
            Value = value;
        }

        /// <summary>是否已建立。为 false 时禁止参与任何差值或比较运算。</summary>
        public bool IsValid { get { return Domain != PMFrameDomain.None; } }

        public bool Equals(PMFrameId other)
        {
            return Domain == other.Domain && Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is PMFrameId && Equals((PMFrameId)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Domain * 397) ^ Value.GetHashCode();
        }

        public override string ToString()
        {
            return IsValid ? (Domain + ":" + Value) : "None";
        }

        /// <summary>
        /// 同命名空间差值。**跨命名空间或任一侧未建立时抛出**（fail-fast）。
        ///
        /// 之所以选择抛出而不是返回 0 或断言：这类错误在编译期完全看不出来，
        /// 在运行期表现为「玩起来就是有点不对」，是最难定位的一类。宁可早失败。
        /// </summary>
        public static long operator -(PMFrameId a, PMFrameId b)
        {
            if (!a.IsValid || !b.IsValid)
            {
                throw new InvalidOperationException(
                    "[PMFrameId] 对未建立的帧标识做差值（对应 UE 的 INDEX_NONE 误用）。");
            }

            if (a.Domain != b.Domain)
            {
                throw new InvalidOperationException(
                    "[PMFrameId] 跨命名空间差值：" + a.Domain + " - " + b.Domain +
                    "（D-R0-20 禁止混算，见 net-r0-contract.md）。");
            }

            return a.Value - b.Value;
        }

        public static bool operator ==(PMFrameId a, PMFrameId b) { return a.Equals(b); }
        public static bool operator !=(PMFrameId a, PMFrameId b) { return !a.Equals(b); }

        /// <summary>
        /// 顺序比较。**仅限同命名空间且两侧均已建立**；否则抛出（与 <see cref="operator -"/> 同口径）。
        ///
        /// 为什么需要它：“这一帧是否比那一帧新”是预测/复制里最常用的判断之一。
        /// 缺了它，调用方就只能先相减再比 0，容易绕开命名空间检查。
        /// </summary>
        public static bool operator <(PMFrameId a, PMFrameId b)
        {
            EnsureComparable(a, b);
            return a.Value < b.Value;
        }

        public static bool operator >(PMFrameId a, PMFrameId b)
        {
            EnsureComparable(a, b);
            return a.Value > b.Value;
        }

        public static bool operator <=(PMFrameId a, PMFrameId b)
        {
            EnsureComparable(a, b);
            return a.Value <= b.Value;
        }

        public static bool operator >=(PMFrameId a, PMFrameId b)
        {
            EnsureComparable(a, b);
            return a.Value >= b.Value;
        }

        private static void EnsureComparable(PMFrameId a, PMFrameId b)
        {
            if (!a.IsValid || !b.IsValid)
            {
                throw new InvalidOperationException(
                    "[PMFrameId] 对未建立的帧标识做顺序比较（对应 UE 的 INDEX_NONE 误用）。");
            }

            if (a.Domain != b.Domain)
            {
                throw new InvalidOperationException(
                    "[PMFrameId] 跨命名空间比较：" + a.Domain + " vs " + b.Domain +
                    "（D-R0-20 禁止混算，见 net-r0-contract.md）。");
            }
        }
    }

    /// <summary>
    /// 一次仿真推进的时间步（对应 UE 的 FMoverTimeStep 概念）。
    ///
    /// 关键决策 D-R0-19：推进单位是「一条输入 + 它自带的 DeltaTimeMS」，
    /// **不是**固定的 16ms，也不是墙钟。
    /// 这撤回了旧 P3'-3b 提案里的「沿用服务端 16ms 累加器」——
    /// 那条路会把输入与时间解耦，导致重模拟无法用原始 dt 复现。
    /// </summary>
    public struct PMTimeStep
    {
        /// <summary>该步开始时的实例累计仿真时间（毫秒，SimTime 命名空间）。</summary>
        public long BaseSimTimeMs;

        /// <summary>本步时长（毫秒），来自输入自身，**不得**被服务器合并或放大。</summary>
        public float StepMs;

        /// <summary>当前权威仿真帧（AuthorityServer 命名空间）。</summary>
        public PMFrameId ServerFrame;

        /// <summary>当前客户端输入帧（Input 命名空间）。未建立表示本端即权威。</summary>
        public PMFrameId ClientInputFrame;

        /// <summary>本步是否处于重模拟（回滚重放）中。</summary>
        public bool IsResimulating;

        /// <summary>
        /// 契约校验：重模拟之外的步长必须为正；重模拟可以用原始 dt。
        /// 返回 false 表示调用方构造了非法时间步，应拒绝推进而不是继续。
        /// </summary>
        public bool IsWellFormed()
        {
            if (float.IsNaN(StepMs) || float.IsInfinity(StepMs)) { return false; }
            if (StepMs < 0f) { return false; }
            if (BaseSimTimeMs < 0L) { return false; }
            return true;
        }
    }
}
