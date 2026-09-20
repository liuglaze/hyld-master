using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 复制层的线格式解码器（接收侧）。
    ///
    /// 与 <see cref="PMReplicationWriter"/> 严格对称。三条硬要求：
    ///   1. **不信任输入**：任何越界/长度不符都转成返回 false + 错误串，而不是抛异常穿透网络层；
    ///   2. **有界**：单条消息的记录数、单条记录的属性数、掩码字节数都有上限（D-R0-18），
    ///      否则一个伪造的长度头就能让接收端按它分配内存；
    ///   3. **整条丢弃**：解析失败时不采纳任何部分（半采纳会让接收端停在"发送端从未存在过的状态"）。
    ///      失败路径会清空输出容器，不会给调用方留下半裁结果。
    ///
    /// **形状规则（RV1）**：掩码是"最短形态"的 —— 字节数不得超过
    /// <see cref="PMRepMask.MaxBytes"/>（32），且尾字节不得为 0（写侧只写
    /// `ByteCountFor(highest + 1)`）；整条消息尾部不得有多余字节。
    /// 前者保证"线上掩码字节数"与"最高属性槽位"一一对应，后者拒绝截断/拼接。
    /// 掩码的位序按 `slot &gt;&gt; 3` / `slot &amp; 7` 与写侧同源（历史缺陷：读侧曾把字节下标
    /// 当成 ulong 下标，导致槽位 ≥ 8 的属性静默丢失）。
    ///
    /// 调用方可以复用同一个 <see cref="PMRepMessage"/> 实例，从而在稳态下零分配。
    /// </summary>
    public static class PMReplicationReader
    {
        /// <summary>
        /// 解析一条复制层消息。
        /// </summary>
        /// <param name="reader">读取器（从消息头开始）。</param>
        /// <param name="into">输出容器（会先被 Clear）。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        /// <returns>true = 已完整解析；false = 该消息必须被整条丢弃。</returns>
        public static bool TryRead(PMNetReader reader, PMRepMessage into, out string error)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();

            if (reader == null)
            {
                error = "reader 为 null";
                return false;
            }

            try
            {
                ulong magic = reader.ReadVarint();
                if (magic != PMRepProtocol.Magic)
                {
                    error = "复制消息魔数不符：" + magic + "（期望 " + PMRepProtocol.Magic + "）";
                    return false;
                }

                ulong version = reader.ReadVarint();
                if (version != PMRepProtocol.Version)
                {
                    error = "复制协议版本不符：收到 " + version + "，本端 " + PMRepProtocol.Version + "（D-R0-46：不一致即不兼容）";
                    return false;
                }

                ulong kind = reader.ReadVarint();

                bool ok;
                if (kind == (ulong)PMRepMessageKind.Update)
                {
                    ok = ReadUpdate(reader, into, out error);
                }
                else if (kind == (ulong)PMRepMessageKind.Ack)
                {
                    ok = ReadAck(reader, into, out error);
                }
                else
                {
                    error = "未知的复制消息类型：" + kind;
                    ok = false;
                }

                if (ok && !reader.IsAtEnd)
                {
                    // 反例：一份合法消息后面拼了一段尾巴（或截断/拼接被当成两条消息）。
                    // 接受它会让"多出来的字节"在下一条消息里被当成消息头，
                    // 因此尾部必须严格为空。
                    error = "复制消息尾部有多余数据（拒绝截断/拼接）";
                    ok = false;
                }

                if (!ok)
                {
                    // 解析失败 ⇒ 不留下任何半裁结果（调用方可能直接复用该容器）。
                    into.Clear();
                }

                return ok;
            }
            catch (Exception ex)
            {
                // 网络输入不可信：把"格式非法"统一降级为"整条丢弃 + 告警"，
                // 不让一个畸形包把接收线程打崩。
                error = ex.GetType().Name + "：" + ex.Message;
                into.Clear();
                return false;
            }
        }

        private static bool ReadUpdate(PMNetReader reader, PMRepMessage into, out string error)
        {
            into.Kind = PMRepMessageKind.Update;

            ulong recordCount = reader.ReadVarint();
            if (recordCount > (ulong)PMRepProtocol.MaxUpdatesPerMessage)
            {
                error = "更新消息记录数超上限：" + recordCount + " > " + PMRepProtocol.MaxUpdatesPerMessage;
                return false;
            }

            for (ulong i = 0; i < recordCount; i++)
            {
                uint netId = unchecked((uint)reader.ReadVarint());
                long version = unchecked((long)reader.ReadVarint());

                ulong maskBytes = reader.ReadVarint();
                if (maskBytes == 0UL || maskBytes > (ulong)PMRepMask.MaxBytes)
                {
                    error = "掩码字节数非法：" + maskBytes + "（上限 " + PMRepMask.MaxBytes + "）";
                    return false;
                }

                byte[] maskRaw = reader.ReadRawBytesCopy((int)maskBytes);

                // 写侧只写 `ByteCountFor(highest + 1)` 个字节，因此最后一个字节必非 0
                // （尾字节为 0 说明线上掩码字节数被夸大）。
                // 拒绝非最短形态：它本身不会错位，但会让"掩码长度"与"最高属性槽位"不再
                // 一一对应，使字节级断言失去意义（且为后续想用长度判定槽位量的代码埋坑）。
                if (maskRaw[maskRaw.Length - 1] == 0)
                {
                    error = "掩码不是最短形态（尾字节为 0，NetId=" + netId + "）";
                    return false;
                }

                PMRepMask mask = default(PMRepMask);
                if (!mask.TryLoadBytes(maskRaw, 0, maskRaw.Length))
                {
                    error = "掩码字节数越界：" + maskRaw.Length;
                    return false;
                }

                int propertyCount = mask.CountBits();

                if (propertyCount <= 0)
                {
                    error = "对象更新记录的掩码为空（NetId=" + netId + "）：空更新不应出现在线上（D-R0-13）";
                    return false;
                }

                int[] slots = new int[propertyCount];
                ushort[] propertyIds = new ushort[propertyCount];
                byte[][] values = new byte[propertyCount][];

                int index = 0;
                for (int slot = 0; slot < PMRepMask.MaxBits; slot++)
                {
                    if (!mask.IsSet(slot))
                    {
                        continue;
                    }

                    ushort propertyId = unchecked((ushort)reader.ReadVarint());
                    ulong valueLength = reader.ReadVarint();
                    if (valueLength > int.MaxValue)
                    {
                        error = "属性值长度非法：" + valueLength;
                        return false;
                    }

                    slots[index] = slot;
                    propertyIds[index] = propertyId;
                    values[index] = reader.ReadRawBytesCopy((int)valueLength);
                    index++;
                }

                into.Updates.Add(new PMRepUpdateRecord(netId, version, slots, propertyIds, values));
            }

            error = null;
            return true;
        }

        private static bool ReadAck(PMNetReader reader, PMRepMessage into, out string error)
        {
            into.Kind = PMRepMessageKind.Ack;

            ulong ackCount = reader.ReadVarint();
            if (ackCount > (ulong)PMRepProtocol.MaxAcksPerMessage)
            {
                error = "Ack 条数超上限：" + ackCount + " > " + PMRepProtocol.MaxAcksPerMessage;
                return false;
            }

            for (ulong i = 0; i < ackCount; i++)
            {
                uint netId = unchecked((uint)reader.ReadVarint());
                long version = unchecked((long)reader.ReadVarint());
                into.Acks.Add(new PMRepAck(netId, version));
            }

            error = null;
            return true;
        }
    }
}
