using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 一个被复制对象在本复制层里的共享状态（**每对象一份**，跨所有连接共享）。
    ///
    /// 与 per-connection 的部分（<see cref="PMRepConnectionState"/>）分开是刻意的：
    ///   - `Custom` 条件的开关在 legacy 里是**每对象共享**的（C-15：`ActiveState` 不在连接上）；
    ///   - `Dynamic` 条件的改写同样是每对象的（C-16）；
    ///   - 描述符本身更是每对象的。
    /// 把它们放进连接状态会导致"两条连接看到两个不同的条件值"，那正是 C-14 警告的混用。
    /// </summary>
    public sealed class PMRepObjectEntry
    {
        /// <summary>被复制的对象。</summary>
        public PMNetObject Object;

        /// <summary>该类的复制描述符（生成器产物，运行期只读）。</summary>
        public PMReplicationDescriptor Descriptor;

        /// <summary>属性槽位数（= 描述符属性表长度）。</summary>
        public int SlotCount;

        /// <summary>是否存在"可能在运行时改变真假"的条件属性。没有则不需要逐帧扫描条件。</summary>
        public bool HasConditional;

        /// <summary>
        /// 是否存在 `PushBased=false`（轮询式）属性。
        ///
        /// 与 <see cref="HasConditional"/> 一样是"该对象是否需要每轮被扫描"的判据之一，而且
        /// **必须由 `NeedsWork` 消费**：`PushBased=false` 的语义就是"业务直接写字段、不标脏，
        /// 由复制层每轮取当前值与基线比较决定发不发"。若调度判据里没有这一条，
        /// 这类属性的无脏位修改将永不参与比较 ⇒ 静默不同步，且只在业务没走
        /// `PMNet_Set` / `MarkPropertyDirty` 时才复现。
        /// </summary>
        public bool HasPoll;

        /// <summary>`Custom` 条件的运行期开关，下标 = 槽位。初值恒 true（未覆盖 ≈ 总是复制）。</summary>
        public bool[] CustomActive;

        /// <summary>`Dynamic` 条件运行期改写成的条件，下标 = 槽位。初值 `Dynamic` 表示"未覆盖"。</summary>
        public PMCond[] Dynamic;

        /// <summary>
        /// 本类的构造工厂（来自 `PMNetRegistry` 的注册项；接收侧做"暂存对象解码"时必须用它）。
        ///
        /// 为何需要它（RV2）：接收侧不应把多个属性**逐个写进活对象**再在中途发现某个值非法
        /// —— 那样会留下"一半新一半旧"的状态（发送端从未存在过的状态）。
        /// 正确做法是先用工厂造一个**暂存对象**把所有值解码一遍（连同"字节是否恰好读完"），
        /// 全部通过后再提交到活对象。`null` 表示该类型未注册工厂，
        /// 此时复制层**明确拒绝**该记录（不静默半应用）。
        ///
        /// **前置条件（RV2 返工补齐）**：工厂必须返回**全新、未登记**的对象
        /// （`State == Unregistered`、无 NetId、未绑定世界）。若它返回的是活对象本身，
        /// "暂存解码"写的其实就是活对象 —— 验证失败时活对象已被污染，原子性完全失效。
        /// 因此 `PMReplicationChannel` 会当场拒绝整条记录并计 `StagingRejected`。
        /// 生成产物 `PMNet_CreateInstance()` 满足该条件（就是 `new` 一个新实例）。
        ///
        /// **注意**：该委托在 `PMRepObjectEntry` 上是**按对象缓存**的
        /// （`ResolveEntry` 首次解析该对象时从注册表 `PMNetClassEntry.Factory` 拷贝），
        /// 因此注册表必须在处理任何消息之前完成注册。
        /// </summary>
        public Func<PMNetObject> Factory;

        /// <summary>取其"生效条件"：`Dynamic` 属性在解析后用运行期改写值。</summary>
        public PMCond EffectiveCondition(int slot)
        {
            PMCond c = Descriptor.Properties[slot].Condition;
            if (c == PMCond.Dynamic && Dynamic != null)
            {
                return Dynamic[slot];
            }

            return c;
        }

        /// <summary>求值某槽位对某角色的可见性。</summary>
        public bool EvaluateSlot(int slot, bool isOwner, bool isSimulated)
        {
            PMPropertyDescriptor prop = Descriptor.Properties[slot];

            bool customActive = CustomActive == null || slot >= CustomActive.Length || CustomActive[slot];

            PMCond dynamicResolved = PMCond.Dynamic;
            if (prop.Condition == PMCond.Dynamic && Dynamic != null && slot < Dynamic.Length)
            {
                dynamicResolved = Dynamic[slot];
            }

            return PMRepConditions.Evaluate(prop.Condition, isOwner, isSimulated, customActive, dynamicResolved);
        }
    }

    /// <summary>
    /// 每（连接 × 对象）的复制状态 —— 即 R0 契约 §5 的「每连接基线」。
    ///
    /// ## 基线到底是什么
    ///
    /// `Baseline[slot]` 保存的是**该连接已确认（Ack）过的那个属性值**的字节。
    /// `null` 表示"该连接还不知道这个属性的值"（首次进入范围 / 基线缺失），
    /// 此时按 D-R0-12/D-R0-16 语义发全量。
    ///
    /// ## 为什么基线必须在 Ack 时推进，而不是发送时
    ///
    /// 发送时推进 = 乐观确认：一旦包丢了，对端永远拿不到那一版值，而本端以为已经同步。
    /// 因此这里只在收到该版本的 Ack 时推进，并在 `AckedVersion` 上做**单调**约束：
    /// 旧 Ack 既不能回退版本号，也不能覆盖更新的基线（R0 §5「旧 ACK 不得清除新脏位」）。
    /// </summary>
    public sealed class PMRepConnectionState
    {
        /// <summary>所属连接。</summary>
        public PMNetConnection Connection;

        /// <summary>所属对象条目（共享）。</summary>
        public PMRepObjectEntry Entry;

        /// <summary>是否已建立基线（false ⇒ 首次进入范围 / 基线缺失 ⇒ 全量，D-R0-12）。</summary>
        public bool HasBaseline;

        /// <summary>还不知道值的槽位数（> 0 ⇒ 必须全量补齐这些槽位）。</summary>
        public int UnknownBaselineCount;

        /// <summary>已确认的最大发送版本（单调，**只能前进**）。</summary>
        public long AckedVersion;

        /// <summary>下一个发送版本（每（连接 × 对象）单调递增，从 1 开始；0 保留为"未知"）。</summary>
        public long NextSendVersion = 1L;

        /// <summary>每槽位已确认的值字节；null = 未知。</summary>
        public byte[][] Baseline;

        /// <summary>
        /// 上一次求值得到的条件真假（每槽位）。
        /// D-R0-15 的"不满足 → 满足"就是靠它与本次求值比较得到的 —— 这是最容易漏的一项。
        /// </summary>
        public bool[] ConditionActive;

        /// <summary>待强制补发的槽位（条件跃迁 / 基线失效）。</summary>
        public bool[] ForceInclude;

        /// <summary>`ForceInclude` 里 true 的个数（避免每次遍历整个数组）。</summary>
        public int ForceIncludeCount;

        /// <summary>在途发送记录（有界，见 <see cref="PMRepOptions.MaxInflightPerObject"/>）。</summary>
        public readonly List<PMRepInflight> Inflight = new List<PMRepInflight>(4);

        /// <summary>构造。</summary>
        public PMRepConnectionState(PMNetConnection connection, PMRepObjectEntry entry)
        {
            Connection = connection;
            Entry = entry;

            int n = entry.SlotCount;
            Baseline = new byte[n][];
            ConditionActive = new bool[n];
            ForceInclude = new bool[n];
            UnknownBaselineCount = n;
        }

        /// <summary>记录一个已确认的值（推进基线）。</summary>
        public void SetBaselineValue(int slot, byte[] value)
        {
            if (Baseline[slot] == null && value != null && UnknownBaselineCount > 0)
            {
                UnknownBaselineCount--;
            }

            Baseline[slot] = value;
        }

        /// <summary>请求强制补发某槽位一次（条件跃迁 D-R0-15 / 动态条件改写失效 C-16）。</summary>
        public void RequireSend(int slot)
        {
            if (ForceInclude[slot])
            {
                return;
            }

            ForceInclude[slot] = true;
            ForceIncludeCount++;
        }

        /// <summary>清掉强制补发标记（已真的发出后调用）。</summary>
        public void ClearRequireSend(int slot)
        {
            if (!ForceInclude[slot])
            {
                return;
            }

            ForceInclude[slot] = false;
            ForceIncludeCount--;
        }

        /// <summary>丢弃某个版本及其之前的在途记录（Ack 到达后调用）。</summary>
        public int DropInflightUpTo(long version)
        {
            int dropped = 0;
            for (int i = Inflight.Count - 1; i >= 0; i--)
            {
                if (Inflight[i].Version <= version)
                {
                    Inflight.RemoveAt(i);
                    dropped++;
                }
            }

            return dropped;
        }

        /// <summary>取出指定版本的在途记录；没有则返回 null。</summary>
        public PMRepInflight FindInflight(long version)
        {
            for (int i = 0; i < Inflight.Count; i++)
            {
                if (Inflight[i].Version == version)
                {
                    return Inflight[i];
                }
            }

            return null;
        }

        /// <summary>逐字节比较（复制层的基线与当前值都是"已编码字节"，直接比字节最诚实）。</summary>
        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null)
            {
                return false;
            }

            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
