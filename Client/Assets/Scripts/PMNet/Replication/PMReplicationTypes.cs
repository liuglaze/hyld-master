using System;
using System.Collections.Generic;
using System.Text;

namespace PMNet
{
    // =====================================================================================
    //  M06（属性复制）最小闭环的公共类型。
    //
    //  事实来源：
    //    - Docs/plans/net-r0-contract.md §2.4（D-R0-12..18）、§5（复制与生命周期契约）、§3.5
    //    - Docs/plans/net-r2-codegen-contract.md §1（冻结接口）、§3（协议摘要）、§7（B 块边界）
    //    - D:/hyld-refactor-survey/R0_3_replication.md C-1..C-20 / C-29..C-38
    //
    //  本文件只放"数据与纯函数"，不放调度：调度在 PMReplicationChannel.cs。
    //  零 Unity 依赖、零 Google.Protobuf 依赖（由 Tools/PMNetLangCheck 与 PMClientCheck 兜住）。
    // =====================================================================================

    /// <summary>
    /// 「接收副本」在本次复制里的角色（条件求值的输入之一）。
    ///
    /// 为什么需要它：D-R0-14 冻结的 8 项复制条件里，`SimulatedOnly` / `AutonomousOnly`
    /// 的判据是**接收端副本的角色**，而不是"这条连接是不是 Owner"这一件事。
    /// 在客户端上两者恰好等价（owner ⇒ 自治，非 owner ⇒ 模拟），但在权威视角下
    /// 语义不同（legacy 里 `COND_AutonomousOnly = !bIsSimulated`，权威视角下为 true）。
    /// 把角色显式化，才能对「8 项 × 角色组合」做表驱动测试（见 `Tools/PMReplicationTest`）。
    /// </summary>
    public enum PMRepViewRole : byte
    {
        /// <summary>本端即权威（无远端连接，或服务端自视）。</summary>
        Authority = 0,

        /// <summary>接收方拥有该对象（自治副本，可本地预测）。</summary>
        Autonomous = 1,

        /// <summary>接收方不拥有该对象（模拟副本，只消费复制结果）。</summary>
        Simulated = 2,
    }

    /// <summary>复制层线格式的消息类型。消息头里显式携带，便于 Ack/Update 共用一条通道。</summary>
    public enum PMRepMessageKind : byte
    {
        /// <summary>未初始化（解码时必须拒绝）。</summary>
        None = 0,

        /// <summary>属性增量更新。</summary>
        Update = 1,

        /// <summary>送达确认（按对象 × 版本）。</summary>
        Ack = 2,
    }

    /// <summary>
    /// 复制层线格式的协议常量。
    ///
    /// **为什么要有魔数与版本号**：D-R0-46 要求两端协议不一致时**明确不兼容**，
    /// 而不是"忽略这条消息"。复制层的载荷是裸字节（没有 protobuf 的字段号自描述），
    /// 一旦两端描述符不同，逐字节解析会读出一串"看起来合法"的错值——静默错位是最贵的一类 bug。
    /// 因此消息头带魔数 + 协议版本，不匹配即整条丢弃并计告警。
    /// </summary>
    public static class PMRepProtocol
    {
        /// <summary>消息头魔数（0xA7 = 常见的"非文本"哨兵值）。</summary>
        public const byte Magic = 0xA7;

        /// <summary>线格式版本。任何字段序/语义变更都必须递增它。</summary>
        public const ulong Version = 1UL;

        /// <summary>单条消息里对象更新记录数上限（D-R0-18：解析必须**有界**，不可被输入撑爆）。</summary>
        public const int MaxUpdatesPerMessage = 4096;

        /// <summary>单条消息里 Ack 条数上限（同上）。</summary>
        public const int MaxAcksPerMessage = 4096;
    }

    /// <summary>
    /// 复制层的边界参数（D-R0-18：对象数 / 在途 / 单包属性数**全部必须有界**）。
    ///
    /// 默认值来源：`net-r0-contract.md` §9 与 `net-r2-codegen-contract.md` §8
    /// （每帧每连接调度上限沿用项目 150；每对象在途沿用 UE 65535 的"量级"但取实用小值）。
    /// </summary>
    public sealed class PMRepOptions
    {
        /// <summary>单条更新消息里最多携带的属性数（超过则把同一对象拆成多条记录）。</summary>
        public int MaxPropertiesPerUpdate = 64;

        /// <summary>每（连接 × 对象）最多保留的在途记录数；达到上限则**顺延**而不是丢弃。</summary>
        public int MaxInflightPerObject = 8;

        /// <summary>每连接每轮最多"进入比较"的对象数（对应 C-37 的 `MaxScheduledObjectsPerFrame = 150`）。</summary>
        public int MaxObjectsPerConnectionPerTick = 150;

        /// <summary>接收侧待发 Ack 的缓冲上限（超出时丢最旧的并计告警，避免无界增长）。</summary>
        public int MaxPendingAcks = 1024;

        /// <summary>构造一份默认边界。</summary>
        public static PMRepOptions Default()
        {
            return new PMRepOptions();
        }
    }

    /// <summary>
    /// 复制层的累计统计（门禁断言与线上诊断共用）。
    ///
    /// 刻意做成"只增不减的计数器"而不是"瞬时状态"：复制层的正确性缺陷大多表现为
    /// "某一类计数为 0 / 非 0"，用计数器做断言比用瞬时状态更稳。
    /// </summary>
    public struct PMRepStats
    {
        /// <summary>发出的更新消息数（一个 payload 算一条）。</summary>
        public long UpdateMessagesSent;

        /// <summary>发出的对象更新记录数（同一对象拆成多条记录时按条计）。</summary>
        public long UpdateRecordsSent;

        /// <summary>发出的属性数。</summary>
        public long PropertiesSent;

        /// <summary>应用成功的更新消息数。</summary>
        public long UpdateMessagesApplied;

        /// <summary>应用成功的对象更新记录数。</summary>
        public long UpdateRecordsApplied;

        /// <summary>应用成功的属性数。</summary>
        public long PropertiesApplied;

        /// <summary>触发的 OnRep 次数。</summary>
        public long OnRepDispatched;

        /// <summary>声明了 OnRepMethodId 但没有注册分发器的次数（应视为接线缺失）。</summary>
        public long OnRepUnhandled;

        /// <summary>发出的 Ack 条数。</summary>
        public long AcksSent;

        /// <summary>收到的 Ack 条数。</summary>
        public long AcksReceived;

        /// <summary>被"ACK 只能前进"规则丢弃的过期 Ack 条数。</summary>
        public long StaleAckIgnored;

        /// <summary>Ack 指向未知连接 / 未知对象的次数。</summary>
        public long AckUnknownConnection;

        /// <summary>Ack 指向未知对象的次数。</summary>
        public long AckUnknownObject;

        /// <summary>因每帧对象预算被顺延的对象数（顺延而非丢弃）。</summary>
        public long DeferredByBudget;

        /// <summary>因每对象在途上限被顺延的对象数（顺延而非丢弃）。</summary>
        public long DeferredByInflight;

        /// <summary>因"基线缺失/首次进入范围"而全量发送的次数。</summary>
        public long InitialFullSends;

        /// <summary>因"条件不满足 → 满足"而强制补发的次数（D-R0-15）。</summary>
        public long TransitionForceSends;

        /// <summary>条件不满足被过滤掉的槽位次数（值不丢，靠跃迁补发）。</summary>
        public long ConditionFiltered;

        /// <summary>标脏但值未变、因而未发数据的槽位次数（D-R0-13）。</summary>
        public long SuppressedUnchanged;

        /// <summary>
        /// `PushBased=false` 的槽位被真正采样的次数（取当前值与基线比较，无论最终是否发出）。
        ///
        /// 用途：把"没有载荷"拆成两种完全不同的原因 ——
        ///   - 采样了但值没变（本计数与 <see cref="SuppressedUnchanged"/> 同时增长）；
        ///   - 压根没被调度（两个计数都不动）。
        /// 缺了它，"无变化所以不发"与"该发却没发"在统计上长得一模一样。
        /// </summary>
        public long PollSampledSlots;

        /// <summary>收到的丢包通知条数（传输层丢失提醒，驱动提前重发）。</summary>
        public long LossReports;

        /// <summary>丢包通知指向未知在途版本、因而无效的次数。</summary>
        public long UnknownLossReports;

        /// <summary>找不到复制描述符而跳过的对象次数。</summary>
        public long DroppedNoDescriptor;

        /// <summary>描述符属性数超过掩码位宽上限而被拒绝登记的次数。</summary>
        public long RejectedTooManyProperties;

        /// <summary>接收侧解析不到对象的更新次数。</summary>
        public long DroppedUnknownObject;

        /// <summary>协议错误次数（魔数/版本/长度不符 —— 整条丢弃）。</summary>
        public long ProtocolErrors;

        /// <summary>对象不再 Active（已销毁）被跳过的次数。</summary>
        public long SkippedInactiveObject;

        /// <summary>待发 Ack 溢出被丢掉的条数。</summary>
        public long PendingAcksDropped;

        /// <summary>描述符的 HasConditionalMask 标志与实际属性条件不一致的次数（生成器缺陷信号）。</summary>
        public long DescriptorFlagMismatch;

        /// <summary>
        /// 整条更新记录因校验失败被**拒绝**的次数（不部分应用、也不回 ACK）。
        ///
        /// 与 <see cref="ProtocolErrors"/> 的区别：后者是"协议层看到过错误"，本计数专表
        /// "有一条记录因此**完全未生效**"。两者一起看才能区分"脏包被丢"与"状态静默停在半路"。
        /// </summary>
        public long RecordsRejected;

        /// <summary>
        /// ACK 指向一个"比已确认水位更新、但本端没有对应在途记录"的版本，因此**未推进水位**。
        ///
        /// 正常路径下这种 ACK 不该出现（在途记录只在 ACK 或丢包通知时移除）。
        /// 出现即意味着伪造/重放/错配，盲目推进水位会让后续**真实** ACK 被判过期，
        /// 基线永远追不上（表现为"值一直在发却永远不确认"）。
        /// </summary>
        public long AckWithoutInflight;

        /// <summary>单行摘要。</summary>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("sentMsg=").Append(UpdateMessagesSent);
            sb.Append(" sentRec=").Append(UpdateRecordsSent);
            sb.Append(" sentProp=").Append(PropertiesSent);
            sb.Append(" appliedMsg=").Append(UpdateMessagesApplied);
            sb.Append(" appliedRec=").Append(UpdateRecordsApplied);
            sb.Append(" appliedProp=").Append(PropertiesApplied);
            sb.Append(" onRep=").Append(OnRepDispatched);
            sb.Append(" onRepUnhandled=").Append(OnRepUnhandled);
            sb.Append(" ackSent=").Append(AcksSent);
            sb.Append(" ackRecv=").Append(AcksReceived);
            sb.Append(" ackStale=").Append(StaleAckIgnored);
            sb.Append(" ackUnknown=").Append(AckUnknownObject);
            sb.Append(" initial=").Append(InitialFullSends);
            sb.Append(" transition=").Append(TransitionForceSends);
            sb.Append(" condFiltered=").Append(ConditionFiltered);
            sb.Append(" suppressedUnchanged=").Append(SuppressedUnchanged);
            sb.Append(" pollSampled=").Append(PollSampledSlots);
            sb.Append(" loss=").Append(LossReports);
            sb.Append(" deferredBudget=").Append(DeferredByBudget);
            sb.Append(" deferredInflight=").Append(DeferredByInflight);
            sb.Append(" noDescriptor=").Append(DroppedNoDescriptor);
            sb.Append(" tooManyProps=").Append(RejectedTooManyProperties);
            sb.Append(" dropUnknownObj=").Append(DroppedUnknownObject);
            sb.Append(" protoErr=").Append(ProtocolErrors);
            sb.Append(" skippedInactive=").Append(SkippedInactiveObject);
            sb.Append(" acksDropped=").Append(PendingAcksDropped);
            sb.Append(" recRejected=").Append(RecordsRejected);
            sb.Append(" ackNoInflight=").Append(AckWithoutInflight);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 成员级变更掩码。
    ///
    /// 位序契约：**第 i 位 = 描述符属性表里下标为 i 的属性**（即
    /// `PMPropertyDescriptor.MaskOffset`）。线格式按小端字节序搬运，
    /// 因此"位 i"落在第 (i/8) 字节的第 (i%8) 位。
    ///
    /// 位宽上限 <see cref="MaxBits"/>（256）。这不是随意取的数：
    /// 它与 `PMDirtyTracker` 的位宽（RV4 之后同为 256）保持一致，从而
    /// "标脏的槽位"与"能表达变化的槽位"一一对应；超过上限的描述符在登记阶段被拒绝。
    /// 256 位 = 32 字节，仍可无分配地按值传递。
    ///
    /// **线上字节与位序必须同源**：写侧按 `slot &gt;&gt; 3` / `slot &amp; 7` 铺位，
    /// 读侧只能用同一套映射（<see cref="GetByte"/> / <see cref="SetByte"/>）。
    /// 两侧不同源时记录里"掩码说有几个属性"与"值流有几个值"会错位，
    /// 表现为静默的永久不一致。
    /// </summary>
    public struct PMRepMask
    {
        /// <summary>掩码位宽上限。超过它的描述符在登记阶段被拒绝（不做静默截断）。</summary>
        public const int MaxBits = 256;

        /// <summary>掩码的字节数（<see cref="MaxBits"/> / 8 = 32）。线上掩码的字节数不得超过它。</summary>
        public const int MaxBytes = MaxBits / 8;

        private ulong _w0;
        private ulong _w1;
        private ulong _w2;
        private ulong _w3;

        /// <summary>置位。越界位**不置位**（登记阶段已挡掉，这里是防御性处理）。</summary>
        public void Set(int bit)
        {
            if (bit < 0 || bit >= MaxBits)
            {
                return;
            }

            switch (bit >> 6)
            {
                case 0: _w0 |= 1UL << (bit & 63); return;
                case 1: _w1 |= 1UL << (bit & 63); return;
                case 2: _w2 |= 1UL << (bit & 63); return;
                default: _w3 |= 1UL << (bit & 63); return;
            }
        }

        /// <summary>清位。</summary>
        public void Clear(int bit)
        {
            if (bit < 0 || bit >= MaxBits)
            {
                return;
            }

            switch (bit >> 6)
            {
                case 0: _w0 &= ~(1UL << (bit & 63)); return;
                case 1: _w1 &= ~(1UL << (bit & 63)); return;
                case 2: _w2 &= ~(1UL << (bit & 63)); return;
                default: _w3 &= ~(1UL << (bit & 63)); return;
            }
        }

        /// <summary>查询某位。</summary>
        public bool IsSet(int bit)
        {
            if (bit < 0 || bit >= MaxBits)
            {
                return false;
            }

            switch (bit >> 6)
            {
                case 0: return (_w0 & (1UL << (bit & 63))) != 0UL;
                case 1: return (_w1 & (1UL << (bit & 63))) != 0UL;
                case 2: return (_w2 & (1UL << (bit & 63))) != 0UL;
                default: return (_w3 & (1UL << (bit & 63))) != 0UL;
            }
        }

        /// <summary>是否为空（空掩码 = 不发数据，D-R0-13）。</summary>
        public bool IsEmpty
        {
            get { return (_w0 | _w1 | _w2 | _w3) == 0UL; }
        }

        /// <summary>清空。</summary>
        public void ClearAll()
        {
            _w0 = 0UL;
            _w1 = 0UL;
            _w2 = 0UL;
            _w3 = 0UL;
        }

        /// <summary>置满（调试与"整对象标脏"用）。</summary>
        public void SetAll()
        {
            _w0 = ulong.MaxValue;
            _w1 = ulong.MaxValue;
            _w2 = ulong.MaxValue;
            _w3 = ulong.MaxValue;
        }

        /// <summary>置位数（= 本次要发的属性数）。</summary>
        public int CountBits()
        {
            return PopCount(_w0) + PopCount(_w1) + PopCount(_w2) + PopCount(_w3);
        }

        /// <summary>
        /// 覆盖 `bitCount` 个位所需的字节数（至少 1，便于"空掩码也要写长度"）。
        ///
        /// 超过 <see cref="MaxBits"/> 直接抛：掩盖掉它会让调用方"默默少写几位"，
        /// 而少写的那几位在接收侧表现为"某些属性永远不同步"。
        /// </summary>
        public static int ByteCountFor(int bitCount)
        {
            if (bitCount <= 0)
            {
                return 1;
            }

            if (bitCount > MaxBits)
            {
                throw new ArgumentOutOfRangeException("bitCount",
                    "掩码位数 " + bitCount + " 超过上限 " + MaxBits);
            }

            return (bitCount + 7) / 8;
        }

        /// <summary>
        /// 取第 index 个掩码**字节**（小端：字节内低位是低位属性）。
        ///
        /// 位序契约：线上第 b 字节承载槽位 `8b .. 8b+7`，第 (8b+j) 位落在该字节的第 j 位。
        /// 因此 `index` 是**字节下标**（0 .. &lt;<see cref="MaxBytes"/>），而不是"第几个 ulong"。
        ///
        /// 历史坑：本方法曾经把 index 当成 ulong 下标且只接受 0..3，而掩码有 32 字节 ——
        /// 于是接收侧把第 b 字节当成槽位 `64b..64b+7`，槽位 ≥ 8 的属性全部落到越界位，
        /// 被当成协议错误丢弃；更糟的是发送侧仍会收到 ACK 并推进基线，形成**静默永久不一致**。
        /// 写侧按 `slot &gt;&gt; 3` / `slot &amp; 7` 铺位，读侧必须同源。
        /// </summary>
        public byte GetByte(int index)
        {
            if (index < 0 || index >= MaxBytes)
            {
                return 0;
            }

            int shift = (index & 7) * 8;
            switch (index >> 3)
            {
                case 0: return (byte)((_w0 >> shift) & 0xFFUL);
                case 1: return (byte)((_w1 >> shift) & 0xFFUL);
                case 2: return (byte)((_w2 >> shift) & 0xFFUL);
                default: return (byte)((_w3 >> shift) & 0xFFUL);
            }
        }

        /// <summary>
        /// 用线上字节**覆盖**第 index 个掩码字节（与 <see cref="GetByte"/> 同源）。
        ///
        /// 是"覆盖"而不是"或上"：同一字节被重复/乱序写入时，结果必须与写入顺序无关。
        /// 否则重复的 SetByte 会把先后两次的掩码叠在一起，静默多出一批属性位，
        /// 而这些位在接收侧会被当成"记录带了更多值"，从而把值流错位。
        /// </summary>
        public void SetByte(int index, byte value)
        {
            if (index < 0 || index >= MaxBytes)
            {
                return;
            }

            int shift = (index & 7) * 8;
            ulong byteMask = 0xFFUL << shift;
            ulong bits = (ulong)value << shift;

            switch (index >> 3)
            {
                case 0: _w0 = (_w0 & ~byteMask) | bits; return;
                case 1: _w1 = (_w1 & ~byteMask) | bits; return;
                case 2: _w2 = (_w2 & ~byteMask) | bits; return;
                default: _w3 = (_w3 & ~byteMask) | bits; return;
            }
        }

        /// <summary>
        /// 与线上字节流对照：把 `count` 个字节原样装进掩码（下标从 0 起）。
        /// 返回 false 表示 `count` 越界，调用方必须把整条记录判为非法（不静默截断）。
        /// </summary>
        public bool TryLoadBytes(byte[] source, int offset, int count)
        {
            if (source == null || offset < 0 || count < 0 || offset + count > source.Length)
            {
                return false;
            }

            if (count > MaxBytes)
            {
                return false;
            }

            ClearAll();
            for (int i = 0; i < count; i++)
            {
                SetByte(i, source[offset + i]);
            }

            return true;
        }

        private static int PopCount(ulong v)
        {
            // 不依赖 System.Numerics 的 BitOperations（netstandard2.0 的 API 面更窄，
            // 且这里要保持 Unity 2019.4 可编）。SWAR popcount，常数时间、无表。
            unchecked
            {
                v = v - ((v >> 1) & 0x5555555555555555UL);
                v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
                v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                return (int)((v * 0x0101010101010101UL) >> 56);
            }
        }
    }

    /// <summary>
    /// 4 项"简单条件"的求值（D-R0-14）。
    ///
    /// **口径 = legacy（`RepLayout.cpp` 的 `BuildConditionMapFromRepFlags`）**，
    /// 不是 Iris：D-R0-14 明确要求二选一，且 R0_3 C-14 记录了 Iris 下
    /// `ReplayOrOwner ≡ OwnerOnly`、`SimulatedOnlyNoReplay ≡ SimulatedOnly` 等差异。
    /// 本项目不实现 replay 连接，因此 legacy 的 `bIsReplay` 相关项全部落在"未实现"一侧
    /// （生成器声明阶段直接报错，见 net-r2-codegen-contract §5 规则 11）。
    ///
    /// 映射表（`isOwner` = 接收方是否拥有该对象；`isSimulated` = 接收副本是否为模拟副本）：
    /// <code>
    /// None            = true
    /// OwnerOnly       = isOwner
    /// SkipOwner       = !isOwner
    /// SimulatedOnly   = isSimulated
    /// AutonomousOnly  = !isSimulated          // legacy 原文，注意不是 isOwner
    /// Custom          = customActive          // 每对象共享的第二层门（C-15）
    /// Dynamic         = 解析后的条件再求值      // (C-16)；未覆盖时等价 None
    /// Never           = false
    /// </code>
    /// </summary>
    public static class PMRepConditions
    {
        /// <summary>求值。</summary>
        /// <param name="condition">属性声明的条件。</param>
        /// <param name="isOwner">接收方是否拥有该对象。</param>
        /// <param name="isSimulated">接收副本是否为模拟副本。</param>
        /// <param name="customActive">`Custom` 的运行期开关（未覆盖时为 true）。</param>
        /// <param name="dynamicResolved">`Dynamic` 运行期被改写成的条件；`Dynamic` 自身表示"未覆盖"。</param>
        public static bool Evaluate(PMCond condition, bool isOwner, bool isSimulated, bool customActive, PMCond dynamicResolved)
        {
            switch (condition)
            {
                case PMCond.None:
                    return true;

                case PMCond.OwnerOnly:
                    return isOwner;

                case PMCond.SkipOwner:
                    return !isOwner;

                case PMCond.SimulatedOnly:
                    return isSimulated;

                case PMCond.AutonomousOnly:
                    return !isSimulated;

                case PMCond.Custom:
                    return customActive;

                case PMCond.Dynamic:
                    if (dynamicResolved == PMCond.Dynamic)
                    {
                        // 未覆盖：legacy 的 ConditionMap 恒 true。
                        return true;
                    }

                    // 递归求值，并把 dynamicResolved 传成 Dynamic 作为递归终点哨兵。
                    return Evaluate(dynamicResolved, isOwner, isSimulated, customActive, PMCond.Dynamic);

                case PMCond.Never:
                    return false;

                default:
                    // 未实现的 9 项（InitialOnly / *Replay* / SimulatedOrPhysics* / NetGroup 等）：
                    // 生成器声明阶段就该报错。运行期走到这里说明声明校验被绕过 ——
                    // 明确拒绝而不是静默放行（"静默降级"是本项目明令禁止的）。
                    return false;
            }
        }

        /// <summary>是否是 D-R0-14 冻结的 8 项之一。</summary>
        public static bool IsSupported(PMCond condition)
        {
            switch (condition)
            {
                case PMCond.None:
                case PMCond.OwnerOnly:
                case PMCond.SkipOwner:
                case PMCond.SimulatedOnly:
                case PMCond.AutonomousOnly:
                case PMCond.Custom:
                case PMCond.Dynamic:
                case PMCond.Never:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 该条件是否需要"逐帧扫描"（因为它可能在运行时改变真假）。
        ///
        /// `Never` 恒 false、`None` 恒 true，都不需要扫描；其余 6 项都取决于接收方角色
        /// 或运行期覆盖，必须在每次投递前重新求值，否则会漏掉 D-R0-15 的条件跃迁。
        /// </summary>
        public static bool RequiresEvaluation(PMCond condition)
        {
            switch (condition)
            {
                case PMCond.None:
                case PMCond.Never:
                    return false;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// 一条"对象更新"记录（发送侧组装、接收侧解析共用同一载体）。
    ///
    /// 只放"已编码好的值字节"而不放字段引用：这样编解码与对象系统解耦，
    /// 字节级契约可以脱离世界单独测试（M04 得到的教训）。
    /// </summary>
    public struct PMRepUpdateRecord
    {
        /// <summary>目标对象 NetId 的原始值。</summary>
        public uint NetId;

        /// <summary>
        /// 该记录所属的发送版本（每（连接 × 对象）单调递增）。
        /// Ack 回带的就是它；"ACK 只能前进"这条规则靠它判定。
        /// </summary>
        public long Version;

        /// <summary>本次携带的属性槽位（升序）。</summary>
        public int[] Slots;

        /// <summary>每个槽位的属性 ID（与 `Slots` 一一对应，接收侧用于校验两端描述符一致）。</summary>
        public ushort[] PropertyIds;

        /// <summary>每个槽位的值字节（与 `Slots` 一一对应，由描述符的 Writer 产出）。</summary>
        public byte[][] Values;

        /// <summary>构造。</summary>
        public PMRepUpdateRecord(uint netId, long version, int[] slots, ushort[] propertyIds, byte[][] values)
        {
            NetId = netId;
            Version = version;
            Slots = slots;
            PropertyIds = propertyIds;
            Values = values;
        }
    }

    /// <summary>一条送达确认：某连接上的某对象、某个发送版本已被确认。</summary>
    public struct PMRepAck
    {
        /// <summary>对象 NetId 的原始值。</summary>
        public uint NetId;

        /// <summary>被确认的发送版本。</summary>
        public long Version;

        /// <summary>构造。</summary>
        public PMRepAck(uint netId, long version)
        {
            NetId = netId;
            Version = version;
        }
    }

    /// <summary>
    /// 一条解码后的复制层消息（Update 或 Ack）。调用方可复用同一实例来避免每帧分配。
    /// </summary>
    public sealed class PMRepMessage
    {
        /// <summary>消息类型。</summary>
        public PMRepMessageKind Kind;

        /// <summary>更新记录。</summary>
        public readonly List<PMRepUpdateRecord> Updates = new List<PMRepUpdateRecord>(16);

        /// <summary>确认记录。</summary>
        public readonly List<PMRepAck> Acks = new List<PMRepAck>(16);

        /// <summary>清空内容，保留容量。</summary>
        public void Clear()
        {
            Kind = PMRepMessageKind.None;
            Updates.Clear();
            Acks.Clear();
        }
    }

    /// <summary>
    /// 一条在途发送记录（每（连接 × 对象）一条链，必须有界 —— D-R0-18）。
    ///
    /// 它承担两件事：
    ///   1. Ack 到达时，用它把该连接的基线**推进到已确认的那一版值**（而不是当前值）；
    ///   2. 作为"未确认队列"的容量账本（必须有界）。
    ///
    /// **为什么没有"同一值已在途就不重发"这层抑制**：属性更新走不可靠域，丢包是预期内的。
    /// 若因为"这个值已经发过"就不再发，而对方那一包恰好丢了，该值会一直等不到确认 ——
    /// 现实里靠的是传输层的丢包通知（Iris 的 `HandleDroppedRecord` / legacy 的 NAK）来重新标脏，
    /// 而 M03 的该通知尚未接线。因此在拿到通知之前，本层选择"未确认就继续重发"：
    /// 冗余被在途上限与已有基线比较双重约束，换到的是不依赖通知也能自愈。
    /// </summary>
    public sealed class PMRepInflight
    {
        /// <summary>发送版本。</summary>
        public readonly long Version;

        /// <summary>本批携带的槽位（升序）。</summary>
        public readonly int[] Slots;

        /// <summary>本批携带的值字节。</summary>
        public readonly byte[][] Values;

        /// <summary>构造。</summary>
        public PMRepInflight(long version, int[] slots, byte[][] values)
        {
            Version = version;
            Slots = slots;
            Values = values;
        }
    }
}
