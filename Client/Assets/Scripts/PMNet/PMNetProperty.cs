using System;
using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// 单条复制属性的注册项（对应 UE 的 FLifetimeProperty）。
    /// </summary>
    public struct PMLifetimeProperty
    {
        /// <summary>属性序号（同一对象内唯一，由生成器或手动注册分配）。</summary>
        public int PropertyIndex;

        /// <summary>复制条件。</summary>
        public PMCond Condition;

        /// <summary>是否参与 Push Model 式标脏（true 表示业务侧负责标记变化）。</summary>
        public bool PushBased;

        public PMLifetimeProperty(int propertyIndex, PMCond condition, bool pushBased)
        {
            PropertyIndex = propertyIndex;
            Condition = condition;
            PushBased = pushBased;
        }
    }

    /// <summary>
    /// 复制属性注册表（对应 UE 的 GetLifetimeReplicatedProps 输出参数 TArray&lt;FLifetimeProperty&gt;）。
    ///
    /// 用法与 UE 对齐：对象重写 <see cref="PMNetObject.GetLifetimeReplicatedProps"/>，
    /// 在其中逐条 Add 自己的复制属性与条件。
    /// </summary>
    public sealed class PMRepList
    {
        private readonly List<PMLifetimeProperty> _items = new List<PMLifetimeProperty>(16);

        public int Count
        {
            get { return _items.Count; }
        }

        public PMLifetimeProperty this[int index]
        {
            get { return _items[index]; }
        }

        public void Clear()
        {
            _items.Clear();
        }

        public void Add(int propertyIndex, PMCond condition, bool pushBased)
        {
            _items.Add(new PMLifetimeProperty(propertyIndex, condition, pushBased));
        }

        /// <summary>按属性序号查注册项；未注册返回 false。</summary>
        public bool TryGet(int propertyIndex, out PMLifetimeProperty result)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].PropertyIndex == propertyIndex)
                {
                    result = _items[i];
                    return true;
                }
            }

            result = default(PMLifetimeProperty);
            return false;
        }
    }

    /// <summary>
    /// 脏位追踪器（对应 UE Push Model 的 changemask 部分语义）。
    ///
    /// 职责边界：只记录「某属性可能已变化」，不负责序列化与对比。
    /// 真正的增量由复制层拿脏位去和该连接的 baseline 比对后决定发什么（见计划 §4.3）。
    ///
    /// **位宽 = 256，与 `PMRepMask.MaxBits` 一致**（RV4）。两个位宽必须一致：
    /// "标脏的槽位"与"复制掩码能表达的槽位"一旦不重合，就会出现
    /// "某些槽位永远清不掉"或"某些槽位永远看不到变化"这类静默缺陷。
    ///
    /// 历史坑（已修）：旧实现只有 64 位，且 `Mark(>=64)` / `MarkAll()` 会静默把整对象
    /// 置满 64 位。而脏位清理只遍历 `[0, min(SlotCount, 64))`，于是位 `[SlotCount, 64)`
    /// 是**永远清不掉的伪位**：任何类只要走过一次 `MarkAllPropertiesDirty()` /
    /// 休眠唤醒（`FlushNetDormancy`），就终身停在"待比较"集合，每 Tick 空扫一遍。
    ///
    /// 超出位宽的取值**明确抛异常**，不做"置满退化"：后者会让"属性数超过上限"这类
    /// 声明错误在运行期退化成性能问题，反而更难归因（声明阶段已强制上限）。
    /// </summary>
    public struct PMDirtyTracker
    {
        /// <summary>位宽（与 <see cref="PMRepMask.MaxBits"/> 同源）。</summary>
        public const int MaxBits = 256;

        private ulong _w0;
        private ulong _w1;
        private ulong _w2;
        private ulong _w3;

        /// <summary>是否有任何脏位。</summary>
        public bool HasAny
        {
            get { return (_w0 | _w1 | _w2 | _w3) != 0UL; }
        }

        /// <summary>
        /// 低 64 位的原始位图（调试与旧断言用）。
        ///
        /// 下标 ≥ 64 的位不在这里：新代码请用 <see cref="HasAny"/> /
        /// <see cref="HasAnyBelow"/> / <see cref="IsDirty"/>。
        /// </summary>
        public ulong Bits
        {
            get { return _w0; }
        }

        /// <summary>标记某个属性为脏。越界（&lt;0 或 &gt;=<see cref="MaxBits"/>）抛异常。</summary>
        public void Mark(int propertyIndex)
        {
            CheckIndex(propertyIndex);

            switch (propertyIndex >> 6)
            {
                case 0: _w0 |= 1UL << (propertyIndex & 63); return;
                case 1: _w1 |= 1UL << (propertyIndex & 63); return;
                case 2: _w2 |= 1UL << (propertyIndex & 63); return;
                default: _w3 |= 1UL << (propertyIndex & 63); return;
            }
        }

        /// <summary>整对象标脏（属性新增/删除、结构变化时使用）。</summary>
        public void MarkAll()
        {
            _w0 = ulong.MaxValue;
            _w1 = ulong.MaxValue;
            _w2 = ulong.MaxValue;
            _w3 = ulong.MaxValue;
        }

        /// <summary>查询某位是否脏。越界抛异常。</summary>
        public bool IsDirty(int propertyIndex)
        {
            CheckIndex(propertyIndex);

            switch (propertyIndex >> 6)
            {
                case 0: return (_w0 & (1UL << (propertyIndex & 63))) != 0UL;
                case 1: return (_w1 & (1UL << (propertyIndex & 63))) != 0UL;
                case 2: return (_w2 & (1UL << (propertyIndex & 63))) != 0UL;
                default: return (_w3 & (1UL << (propertyIndex & 63))) != 0UL;
            }
        }

        /// <summary>
        /// 是否存在下标小于 `limit` 的置位。
        ///
        /// 用途：判断" [0, limit) 这个区间内还有没有真实属性脏位"，
        /// 从而决定能不能把 `MarkAll()` 留下的 `[limit, MaxBits)` 伪位一并清掉。
        /// </summary>
        public bool HasAnyBelow(int limit)
        {
            if (limit <= 0)
            {
                return false;
            }

            if (limit >= MaxBits)
            {
                return HasAny;
            }

            int word = limit >> 6;
            int bits = limit & 63;

            switch (word)
            {
                case 0:
                    if ((_w0 & (bits == 0 ? 0UL : ((1UL << bits) - 1UL))) != 0UL)
                    {
                        return true;
                    }

                    return false;

                case 1:
                    if (_w0 != 0UL)
                    {
                        return true;
                    }

                    return bits == 0 ? false : (_w1 & ((1UL << bits) - 1UL)) != 0UL;

                case 2:
                    if (_w0 != 0UL || _w1 != 0UL)
                    {
                        return true;
                    }

                    return bits == 0 ? false : (_w2 & ((1UL << bits) - 1UL)) != 0UL;

                default:
                    if (_w0 != 0UL || _w1 != 0UL || _w2 != 0UL)
                    {
                        return true;
                    }

                    return bits == 0 ? false : (_w3 & ((1UL << bits) - 1UL)) != 0UL;
            }
        }

        /// <summary>清除单个位。越界抛异常。</summary>
        public void ClearBit(int propertyIndex)
        {
            CheckIndex(propertyIndex);

            switch (propertyIndex >> 6)
            {
                case 0: _w0 &= ~(1UL << (propertyIndex & 63)); return;
                case 1: _w1 &= ~(1UL << (propertyIndex & 63)); return;
                case 2: _w2 &= ~(1UL << (propertyIndex & 63)); return;
                default: _w3 &= ~(1UL << (propertyIndex & 63)); return;
            }
        }

        /// <summary>
        /// 清除下标 ≥ fromBit 的全部位。
        ///
        /// 用途：`MarkAll()` / `Mark()` 会把 `[SlotCount, MaxBits)` 也置上（那里根本没有属性），
        /// 这些"非属性伪位"必须在真实属性全部收敛后被清掉，否则 `HasAny` 永远为真
        /// —— 对象终身每 Tick 空扫（并且 `SuppressedUnchanged` 无界增长）。
        /// </summary>
        public void ClearBitsAbove(int fromBit)
        {
            if (fromBit <= 0)
            {
                Clear();
                return;
            }

            if (fromBit >= MaxBits)
            {
                return;
            }

            int word = fromBit >> 6;
            int bits = fromBit & 63;

            switch (word)
            {
                case 0:
                    if (bits == 0)
                    {
                        Clear();
                        return;
                    }

                    _w0 &= (1UL << bits) - 1UL;
                    _w1 = 0UL;
                    _w2 = 0UL;
                    _w3 = 0UL;
                    return;

                case 1:
                    if (bits == 0)
                    {
                        _w1 = 0UL;
                    }
                    else
                    {
                        _w1 &= (1UL << bits) - 1UL;
                    }

                    _w2 = 0UL;
                    _w3 = 0UL;
                    return;

                case 2:
                    if (bits == 0)
                    {
                        _w2 = 0UL;
                    }
                    else
                    {
                        _w2 &= (1UL << bits) - 1UL;
                    }

                    _w3 = 0UL;
                    return;

                default:
                    if (bits != 0)
                    {
                        _w3 &= (1UL << bits) - 1UL;
                    }

                    return;
            }
        }

        /// <summary>
        /// 清除已确认送达的位（对应「该连接 ack 后清脏」）。
        /// 未 ack 的位保留，从而天然支持丢包重发，替代现有的「同帧重复 3~4 次」。
        ///
        /// **兼容语义**：参数是 64 位掩码，因此只能寻址低 64 位。
        /// 下标 ≥ 64 的属性请用 <see cref="ClearBit"/>（复制层已改成后者）。
        /// </summary>
        public void ClearBits(ulong acknowledgedBits)
        {
            _w0 &= ~acknowledgedBits;
        }

        /// <summary>全部清除。</summary>
        public void Clear()
        {
            _w0 = 0UL;
            _w1 = 0UL;
            _w2 = 0UL;
            _w3 = 0UL;
        }

        private static void CheckIndex(int propertyIndex)
        {
            if (propertyIndex < 0 || propertyIndex >= MaxBits)
            {
                throw new ArgumentOutOfRangeException("propertyIndex",
                    "脏位下标 " + propertyIndex + " 越界（支持范围 0.." + (MaxBits - 1) + "）");
            }
        }
    }

    /// <summary>
    /// 属性描述符（对应 UE/Iris 的 NetSerializer / 属性元信息注册）。
    ///
    /// P0 阶段只承载元信息；序列化委托在属性复制层落地时补充（见计划 §4.3）。
    /// </summary>
    public sealed class PMNetPropertyDesc
    {
        /// <summary>属性序号，与 <see cref="PMLifetimeProperty.PropertyIndex"/> 对应。</summary>
        public int Index;

        /// <summary>属性名（调试与工具使用）。</summary>
        public string Name;

        /// <summary>线格式类型说明（对应 proto 字段类型名，例如 int32 / float）。</summary>
        public string WireTypeName;

        /// <summary>默认复制条件；可被对象级注册覆盖。</summary>
        public PMCond DefaultCondition;

        public PMNetPropertyDesc(int index, string name, string wireTypeName, PMCond defaultCondition)
        {
            Index = index;
            Name = name;
            WireTypeName = wireTypeName;
            DefaultCondition = defaultCondition;
        }
    }
}
