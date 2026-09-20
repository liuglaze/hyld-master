using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 生命周期事件类型。
    ///
    /// 只有两种：对象诞生与对象消亡。UE 里对应 `UActorChannel` 的 open/close，
    /// 对应关系见 `Docs/plans/net-r0-contract.md` D-R0-03。
    /// </summary>
    public enum PMObjectEventKind : byte
    {
        /// <summary>无效事件（未初始化的默认值），解码时必须拒绝。</summary>
        None = 0,

        /// <summary>对象创建，携带初始状态（D-R0-16：与创建同包）。</summary>
        Create = 1,

        /// <summary>对象销毁。</summary>
        Destroy = 2,
    }

    /// <summary>
    /// 对象的生命周期状态。
    ///
    /// 状态机很窄，但必须显式：`Unregistered → Active → Destroying → Destroyed`。
    /// 「Destroying」这一档存在的理由是 D-R0-04：销毁不依赖 ack，所以本端可以先
    /// 把它从索引里摘掉（不再参与复制调度与引用解析），再异步把销毁事件排空。
    /// 没有这一档就会出现「已判定销毁但事件还没发出去」的窗口期，
    /// 期间新来的引用会解析到一个正被销毁的对象。
    /// </summary>
    public enum PMNetObjectState : byte
    {
        /// <summary>尚未登记到世界（未分配 NetId）。</summary>
        Unregistered = 0,

        /// <summary>正常存活，参与复制与引用解析。</summary>
        Active = 1,

        /// <summary>已开始销毁：已摘出索引，销毁事件待发送。</summary>
        Destroying = 2,

        /// <summary>销毁完成。</summary>
        Destroyed = 3,
    }

    /// <summary>
    /// 销毁原因。仅用于诊断与日志，不参与协议语义。
    /// </summary>
    public enum PMObjectDestroyReason : byte
    {
        /// <summary>未说明。</summary>
        Unspecified = 0,

        /// <summary>业务主动销毁（角色死亡、子弹到期等）。</summary>
        Destroyed = 1,

        /// <summary>离开相关性范围被裁掉（对应 UE 的 channel close due to relevancy）。</summary>
        OutOfRange = 2,

        /// <summary>连接断开导致的清理（对应 UE 的 connection close）。</summary>
        ConnectionClosed = 3,

        /// <summary>关卡/对局结束的整体清理。</summary>
        LevelUnload = 4,
    }

    /// <summary>
    /// 一条生命周期事件的**线格式记录**。
    ///
    /// 刻意与 <see cref="PMNetObject"/> 解耦：编解码器只认这个纯数据载体，
    /// 于是「字节级契约」可以脱离对象系统单独测试（这是 M03 得到的教训——
    /// 把契约写成可执行断言本身就在产出正确性）。
    /// </summary>
    public struct PMNetLifecycleRecord
    {
        /// <summary>事件类型。</summary>
        public PMObjectEventKind Kind;

        /// <summary>目标对象身份。Value==0 表示无效（必须拒绝）。</summary>
        public PMNetId NetId;

        /// <summary>类型 ID（由生成器分配，稳定，禁止按声明顺序编号；见 D-R0-49）。</summary>
        public uint ClassId;

        /// <summary>原型 ID；0 表示默认原型。用于区分同一类型的不同配置实例。</summary>
        public uint ArchetypeId;

        /// <summary>
        /// 本连接是否为该对象的拥有者（对应 UE 的 `RepFlags.bNetOwner`）。
        /// 接收侧据此决定副本是 AutonomousProxy 还是 SimulatedProxy。
        /// </summary>
        public bool IsOwner;

        /// <summary>
        /// 初始状态的字节表示（**业务手写钩子** `OnSerializeInitialState` 的产物）。
        /// Create 专用，Destroy 时为 null。
        ///
        /// D-R0-16 要求初始状态与创建原子到达，所以它被嵌在同一条记录里，
        /// 而不是另开一条「补初始状态」的消息——后者会产生「已创建但值未到」的可见态。
        ///
        /// 与 <see cref="RepInitialState"/> 的分工：本字段是**手写**的（业务自定义格式，
        /// 如对象引用），后者是**声明式**的（来自复制描述符的默认属性值）。
        /// 两者**不同时使用**：手写钩子写了内容就不再补声明式初值，避免同一份初值双写。
        /// </summary>
        public byte[] InitialState;

        /// <summary>
        /// **声明式**初始状态（RV5）：从 `PMNetRegistry` 的静态复制描述符取“默认初始属性”，
        /// 按接收连接的复制条件过滤后编码。Create 专用，其他情况为 null。
        ///
        /// 为何需要它：生成器不发射 `OnSerializeInitialState`（它只发射属性读写器/访问器），
        /// 因此声明式复制类的 Create 记录常态上没有初值，只能靠复制层后续另发一条
        /// Unreliable 全量更新来补 —— 那正好破了 D-R0-16 的「创建与初始状态同包」
        /// （对象已 Active 但字段全默认值的可见窗口，且跨顺序域到达）。
        ///
        /// 格式：`( varint(slot) | varint(propertyId) | varint(valueLen) | value )*`，
        /// 自定型（无条数前缀）：接收侧读到末尾为止，条数上限 = 描述符属性数。
        /// （带 propertyId 是为了两端描述符不一致时**当场可判定**，与更新记录同口径）。
        /// </summary>
        public byte[] RepInitialState;

        /// <summary>销毁原因。Destroy 专用。</summary>
        public PMObjectDestroyReason Reason;

        /// <summary>诊断用摘要。</summary>
        public override string ToString()
        {
            if (Kind == PMObjectEventKind.Create)
            {
                return "Create(" + NetId + " class=" + ClassId + " arch=" + ArchetypeId
                       + " owner=" + (IsOwner ? "1" : "0")
                       + " state=" + (InitialState == null ? 0 : InitialState.Length) + "B"
                       + " repState=" + (RepInitialState == null ? 0 : RepInitialState.Length) + "B)";
            }

            if (Kind == PMObjectEventKind.Destroy)
            {
                return "Destroy(" + NetId + " reason=" + Reason + ")";
            }

            return "None";
        }
    }

    /// <summary>
    /// 生命周期批量消息的编解码（M04）。
    ///
    /// 线格式（刻意手工而非 protobuf：这里字段全是整型，protobuf 只带来 tag 开销而没有
    /// 演进收益；D-R0-47 也只要求 protobuf 充当**底层字节编码**）：
    ///
    /// <code>
    /// varint formatVersion      // = 2，不匹配即协议不兼容（D-R0-46）
    /// varint recordCount
    ///   foreach:
    ///     varint kind           // 1 = Create, 2 = Destroy，其他值 = 协议错误
    ///     varint netId
    ///     varint isStatic
    ///     if Create:  varint classId, varint archetypeId, varint isOwner,
    ///                 varint stateLen, state[stateLen],
    ///                 varint repStateLen, repState[repStateLen]    // RV5：声明式初值
    ///     if Destroy: varint reason
    /// </code>
    ///
    /// **关于 formatVersion 1 → 2**：Create 记录尾部多了一段 `repState`（声明式初值）。
    /// 旧读端会把它看成"尾部多余字节"而**拒绝整条**（安全的失败方向），
    /// 新读端读旧消息则会因缺字段而解析失败。两端不同版本时应当明确"不兼容"而不是
    /// "部分接受"，因此这里递增格式版本而不是做兼容分支。
    ///
    /// **为什么逐条带 kind 标签，而不是「先写全部 Create 再写全部 Destroy」**：
    /// 后者会丢失创建与销毁之间的**全局相对顺序**。同一批内可能出现
    /// 「A 被销毁」与「B 被创建且 B 的初始状态引用 A」——若把 B 的创建提到前面，
    /// 客户端就会把 B 的引用解析到一个服务端已经销毁的对象上。
    /// 代价是每条记录多一个 varint，换来顺序语义与发送端完全一致。
    ///
    /// 批量而非逐条一发，是因为 D-R0-05：可靠流只承载低频事件。逐条一发会把每个对象的
    /// 诞生都变成一个包，把可靠流变成高频通道，从而放大队头阻塞。
    ///
    /// 全部 0/1 字段用 varint 写是刻意的：<see cref="PMNetWriter"/> 的整套 API 已被
    /// `Tools/PMNetVerify` 的 27 项字节级测试覆盖，复用比新开一套定长写入更安全。
    /// </summary>
    public static class PMLifecycleCodec
    {
        /// <summary>线格式版本。不匹配 ⇒ 协议不兼容（D-R0-46），不是"忽略该消息"。</summary>
        public const int FormatVersion = 2;

        /// <summary>
        /// 单批允许的记录数上限。
        ///
        /// 与 D-R0-18「复制队列必须有界」同源：解码端必须在读到损坏/恶意的长度前缀时
        /// 立刻拒绝，而不是先按该长度分配内存。
        /// </summary>
        public const int MaxRecordsPerBatch = 4096;

        /// <summary>初始状态字节上限。超出即视为声明/实现错误，不做无界切分。</summary>
        public const int MaxInitialStateBytes = 65536;

        /// <summary>把一批记录按给定顺序写入。</summary>
        public static void Write(PMNetWriter writer, List<PMNetLifecycleRecord> records)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }
            if (records == null) { throw new ArgumentNullException("records"); }
            if (records.Count > MaxRecordsPerBatch)
            {
                throw new ArgumentOutOfRangeException("records",
                    "单批生命周期记录数 " + records.Count + " 超过上限 " + MaxRecordsPerBatch);
            }

            writer.WriteVarint((ulong)FormatVersion);
            writer.WriteVarint((ulong)records.Count);

            for (int i = 0; i < records.Count; i++)
            {
                PMNetLifecycleRecord r = records[i];
                writer.WriteVarint((ulong)(byte)r.Kind);
                writer.WriteVarint((ulong)r.NetId.Value);
                writer.WriteVarint(r.NetId.IsStatic ? 1UL : 0UL);

                if (r.Kind == PMObjectEventKind.Create)
                {
                    writer.WriteVarint((ulong)r.ClassId);
                    writer.WriteVarint((ulong)r.ArchetypeId);
                    writer.WriteVarint(r.IsOwner ? 1UL : 0UL);

                    int len = r.InitialState == null ? 0 : r.InitialState.Length;
                    if (len > MaxInitialStateBytes)
                    {
                        throw new ArgumentOutOfRangeException("records",
                            "初始状态 " + len + " 字节超过上限 " + MaxInitialStateBytes + "（读侧也会拒收，两侧必须对称）");
                    }

                    writer.WriteVarint((ulong)len);
                    if (len > 0)
                    {
                        writer.WriteRawBytes(r.InitialState, 0, len);
                    }

                    int repLen = r.RepInitialState == null ? 0 : r.RepInitialState.Length;
                    if (repLen > MaxInitialStateBytes)
                    {
                        throw new ArgumentOutOfRangeException("records",
                            "声明式初始状态 " + repLen + " 字节超过上限 " + MaxInitialStateBytes + "（读侧也会拒收，两侧必须对称）");
                    }

                    writer.WriteVarint((ulong)repLen);
                    if (repLen > 0)
                    {
                        writer.WriteRawBytes(r.RepInitialState, 0, repLen);
                    }
                }
                else if (r.Kind == PMObjectEventKind.Destroy)
                {
                    writer.WriteVarint((ulong)(byte)r.Reason);
                }
                else
                {
                    throw new ArgumentException("记录 " + i + " 的 Kind 无效（" + r.Kind + "）");
                }
            }
        }

        /// <summary>
        /// 解析一批记录。返回 false 表示**协议不兼容或数据损坏**，调用方必须丢弃整条消息
        /// 并计入告警——不得部分采纳（部分采纳会产生"半创建"的幽灵对象）。
        /// </summary>
        public static bool TryRead(byte[] payload, int offset, int count,
                                   List<PMNetLifecycleRecord> outRecords, out string error)
        {
            outRecords.Clear();
            error = null;

            if (payload == null)
            {
                error = "payload 为 null";
                return false;
            }

            try
            {
                PMNetReader reader = new PMNetReader(payload, offset, count);

                int version = checked((int)reader.ReadVarint());
                if (version != FormatVersion)
                {
                    error = "线格式版本不兼容：收到 " + version + "，本端支持 " + FormatVersion;
                    return false;
                }

                int recordCount = checked((int)reader.ReadVarint());
                if (recordCount <= 0 || recordCount > MaxRecordsPerBatch)
                {
                    error = "记录数 " + recordCount + " 越界";
                    return false;
                }

                for (int i = 0; i < recordCount; i++)
                {
                    PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                    int kind = checked((int)reader.ReadVarint());
                    r.NetId = new PMNetId(checked((uint)reader.ReadVarint()), reader.ReadVarint() != 0UL);

                    if (!r.NetId.IsValid)
                    {
                        error = "记录 " + i + " 的 NetId 为 0（无效身份）";
                        return false;
                    }

                    if (kind == (int)PMObjectEventKind.Create)
                    {
                        r.Kind = PMObjectEventKind.Create;
                        r.ClassId = checked((uint)reader.ReadVarint());
                        r.ArchetypeId = checked((uint)reader.ReadVarint());
                        r.IsOwner = reader.ReadVarint() != 0UL;

                        int stateLen = checked((int)reader.ReadVarint());
                        if (stateLen < 0 || stateLen > MaxInitialStateBytes)
                        {
                            error = "记录 " + i + " 的初始状态长度 " + stateLen + " 越界";
                            return false;
                        }

                        r.InitialState = stateLen > 0 ? reader.ReadRawBytesCopy(stateLen) : null;

                        int repStateLen = checked((int)reader.ReadVarint());
                        if (repStateLen < 0 || repStateLen > MaxInitialStateBytes)
                        {
                            error = "记录 " + i + " 的声明式初始状态长度 " + repStateLen + " 越界";
                            return false;
                        }

                        r.RepInitialState = repStateLen > 0 ? reader.ReadRawBytesCopy(repStateLen) : null;
                    }
                    else if (kind == (int)PMObjectEventKind.Destroy)
                    {
                        r.Kind = PMObjectEventKind.Destroy;
                        r.Reason = (PMObjectDestroyReason)(byte)reader.ReadVarint();
                    }
                    else
                    {
                        error = "记录 " + i + " 的事件类型 " + kind + " 无效";
                        return false;
                    }

                    outRecords.Add(r);
                }

                if (!reader.IsAtEnd)
                {
                    error = "批量消息尾部有 " + (count - reader.Consumed) + " 字节多余数据";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // 越界、varint 溢出等一律视为协议错误：丢弃整条消息。
                error = "解析生命周期消息失败：" + ex.GetType().Name + " " + ex.Message;
                outRecords.Clear();
                return false;
            }
        }
    }
}
