using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 复制层的线格式编码器（发送侧）。
    ///
    /// ## 为什么属性值要带"外长度"
    ///
    /// 描述符里的 `Writer/Reader` 是**成对的**类型化读写器：`Writer` 只写值本身
    /// （例如 `WriteInt32`），`Reader` 也只读值本身。让接收侧"读到哪算哪"是可行的，
    /// 但那要求两端描述符逐字节一致才不出错，而一旦不一致就会读出一串看似合法的错值。
    ///
    /// 因此这里给每个值加一层 varint 外长度 + 属性 ID：
    ///   - 接收侧可以先切出该值的字节区间，再交给描述符的 Reader（分层、可单测）；
    ///   - 属性 ID 让"两端描述符不一致"变成**当场可判定**的协议错误（D-R0-46 的运行期兜底），
    ///     而不是一路错位到最后。
    /// 代价是每个属性 1~3 字节开销，与"错位不可定位"相比完全值得。
    ///
    /// ## 字节布局（小端，varint 为 protobuf base-128）
    ///
    /// <code>
    /// Update:  magic(0xA7) | protocolVersion | kind=1 | recordCount
    ///          record*: netId | version | maskByteCount | mask[..]
    ///                   ( propertyId | valueByteCount | value[..] )*
    /// Ack:     magic(0xA7) | protocolVersion | kind=2 | ackCount
    ///          ack*:    netId | version
    /// </code>
    ///
    /// 掩码的位序：**第 i 位 = 属性槽位 i**；第 (i/8) 字节的第 (i%8) 位。
    ///
    /// ## 形状校验（RV1）
    ///
    /// 写侧**不静默截断、不静默跳过**：记录里槽位必须严格升序且不重复、
    /// 三个平行数组长度必须一致。这三条不满足时必须抛，而不是"尽力写出去"——
    /// 接收侧是**按掩码位升序**取值的，掩码里的位与值流的顺序一旦不同源，
    /// 值就会写到错误的槽位上（表现为"属性 A 的值出现在属性 B 上"），
    /// 而属性 ID 兜底只能把它变成"逐槽位静默丢弃"，仍然是个难查的缺陷。
    /// </summary>
    public static class PMReplicationWriter
    {
        /// <summary>掩码字节缓冲的复用尺寸（<see cref="PMRepMask.MaxBits"/> / 8）。</summary>
        private const int MaskScratchBytes = PMRepMask.MaxBits / 8;

        /// <summary>写一条更新消息。</summary>
        /// <param name="writer">目标写入器（会被追加，不 Reset）。</param>
        /// <param name="records">对象更新记录；空列表会产出一条"空更新"（调用方应避免）。</param>
        public static void WriteUpdate(PMNetWriter writer, List<PMRepUpdateRecord> records)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (records == null)
            {
                throw new ArgumentNullException("records");
            }

            writer.WriteVarint(PMRepProtocol.Magic);
            writer.WriteVarint(PMRepProtocol.Version);
            writer.WriteVarint((ulong)PMRepMessageKind.Update);
            writer.WriteVarint((ulong)records.Count);

            byte[] maskScratch = new byte[MaskScratchBytes];

            for (int i = 0; i < records.Count; i++)
            {
                PMRepUpdateRecord rec = records[i];
                int slotCount = ValidateRecordShape(rec, i);

                writer.WriteVarint(rec.NetId);
                writer.WriteVarint(unchecked((ulong)rec.Version));

                // 槽位已校验为严格升序 ⇒ 最后一个就是最高位。
                int highest = slotCount == 0 ? -1 : rec.Slots[slotCount - 1];
                int maskBytes = PMRepMask.ByteCountFor(highest + 1);
                if (maskBytes > MaskScratchBytes)
                {
                    // 不可达（槽位已在 ValidateRecordShape 里限死），但仍不静默截断：
                    // 截断会让高槽位在接收侧变成"未置位"，那些属性就永远不同步了。
                    throw new InvalidOperationException(
                        "记录 " + i + " 需要 " + maskBytes + " 个掩码字节，超过上限 " + MaskScratchBytes);
                }

                for (int b = 0; b < maskBytes; b++)
                {
                    maskScratch[b] = 0;
                }

                for (int k = 0; k < slotCount; k++)
                {
                    int slot = rec.Slots[k];
                    maskScratch[slot >> 3] |= (byte)(1 << (slot & 7));
                }

                writer.WriteVarint((ulong)maskBytes);
                writer.WriteRawBytes(maskScratch, 0, maskBytes);

                for (int k = 0; k < slotCount; k++)
                {
                    writer.WriteVarint(rec.PropertyIds[k]);

                    byte[] value = rec.Values[k];
                    int valueLength = value == null ? 0 : value.Length;
                    writer.WriteVarint((ulong)valueLength);
                    if (valueLength > 0)
                    {
                        writer.WriteRawBytes(value, 0, valueLength);
                    }
                }
            }
        }

        /// <summary>
        /// 校验一条待写记录的形状，返回槽位数。
        ///
        /// 三条硬规则：槽位在 `[0, MaxBits)` 内；严格升序（隐含不重复）；
        /// `Slots` / `PropertyIds` / `Values` 长度一致。
        /// 违反任一条即抛：这些错误在本地就是声明/组装错误，必须当场暴露，
        /// 而不得变成线上一条"看起来合法但值位置错位"的记录。
        /// </summary>
        private static int ValidateRecordShape(PMRepUpdateRecord rec, int index)
        {
            int slotCount = rec.Slots == null ? 0 : rec.Slots.Length;
            if (slotCount == 0)
            {
                // 空记录仍允许写出（由接收侧判定"空掩码不该上线"并整条拒绝）。
                // 这样"读侧拒绝非法形状"这条断言才有一个可构造的输入。
                return 0;
            }

            if (rec.PropertyIds == null || rec.Values == null
                || rec.PropertyIds.Length != slotCount || rec.Values.Length != slotCount)
            {
                throw new ArgumentException(
                    "更新记录 " + index + " 的 Slots/PropertyIds/Values 长度不一致（"
                    + slotCount + "/" + (rec.PropertyIds == null ? -1 : rec.PropertyIds.Length)
                    + "/" + (rec.Values == null ? -1 : rec.Values.Length)
                    + "）：写出去必然把值写到错误的槽位");
            }

            int previous = -1;
            for (int k = 0; k < slotCount; k++)
            {
                int slot = rec.Slots[k];
                if (slot < 0 || slot >= PMRepMask.MaxBits)
                {
                    throw new ArgumentOutOfRangeException("records",
                        "更新记录 " + index + " 的槽位 " + slot + " 越界（支持 0.." + (PMRepMask.MaxBits - 1) + "）");
                }

                if (slot <= previous)
                {
                    throw new ArgumentException(
                        "更新记录 " + index + " 的槽位必须严格升序且不重复（第 " + k + " 项 " + slot
                        + " <= 前一项 " + previous + "）：乱序/重复会让掩码位序与值流不同源");
                }

                previous = slot;
            }

            return slotCount;
        }

        /// <summary>写一条 Ack 消息。</summary>
        public static void WriteAck(PMNetWriter writer, List<PMRepAck> acks)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (acks == null)
            {
                throw new ArgumentNullException("acks");
            }

            writer.WriteVarint(PMRepProtocol.Magic);
            writer.WriteVarint(PMRepProtocol.Version);
            writer.WriteVarint((ulong)PMRepMessageKind.Ack);
            writer.WriteVarint((ulong)acks.Count);

            for (int i = 0; i < acks.Count; i++)
            {
                writer.WriteVarint(acks[i].NetId);
                writer.WriteVarint(unchecked((ulong)acks[i].Version));
            }
        }
    }
}
