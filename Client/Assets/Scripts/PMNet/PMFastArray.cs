using System.Collections.Generic;

namespace PMNet
{
    /// <summary>
    /// FastArray 元素基类（对应 UE 的 FFastArraySerializerItem）。
    ///
    /// 三个 int 字段的含义（对应 FastArraySerializer.h:330-336）：
    ///   ReplicationId                    元素身份。首次标脏时分配，此后不变（不要用下标当身份）。
    ///   ReplicationKey                   元素版本号。每次 MarkItemDirty 自增。
    ///   MostRecentArrayReplicationKey    客户端用：记录「这条数据是数组第几个版本收到的」，
    ///                                    用于补算「该删而没收到」的隐式删除。
    /// </summary>
    public abstract class PMFastArrayItem
    {
        /// <summary>元素身份，稳定不变。</summary>
        public int ReplicationId;

        /// <summary>元素版本号，每次标脏自增。</summary>
        public int ReplicationKey;

        /// <summary>客户端用于隐式删除补算的数组版本号。</summary>
        public int MostRecentArrayReplicationKey;

        /// <summary>所属数组，由数组在 Add 时注入。</summary>
        internal PMFastArrayBase ArrayOwner;

        /// <summary>
        /// 标记本元素已新增或修改。
        /// 必须调用，否则该元素静默不同步——FastArray 不比对内存内容（见计划 §3.4）。
        /// </summary>
        public void MarkItemDirty()
        {
            if (ArrayOwner != null)
            {
                ArrayOwner.MarkItemDirtyInternal(this);
            }
        }
    }

    /// <summary>FastArray 的非泛型基类，仅用于让元素能回调到所属数组。</summary>
    public abstract class PMFastArrayBase
    {
        internal abstract void MarkItemDirtyInternal(PMFastArrayItem item);
    }

    /// <summary>
    /// 按身份（而非下标）做增量的动态列表（对应 UE 的 FFastArraySerializer / FIrisFastArraySerializer）。
    ///
    /// 与「TArray + Replicated」的关键差别：增量键是 ReplicationId 而不是下标，
    /// 因此中间插入/删除不会把后续元素全部拖下水（下标方案会产生「移位放大」）。
    ///
    /// 但删除仍有代价差别，必须遵守：
    ///   RemoveAtSwap  —— 只标脏被交换过来的那一个元素；
    ///   RemoveAt      —— 必须把移位段之后的元素全部标脏（等价于重发一整段）。
    /// 所以能用 Swap 删除就不要用顺序删除。
    ///
    /// 线上格式（对应 FFastArraySerializerHeader）：
    ///   ArrayReplicationKey / BaseReplicationKey / DeletedIds[] / (Id + payload)[]
    /// P0 只实现服务端记账部分；序列化与客户端三分（Added/Modified/Removed）在复制层落地时补齐。
    /// </summary>
    public sealed class PMFastArray<T> : PMFastArrayBase where T : PMFastArrayItem
    {
        private readonly List<T> _items = new List<T>(16);
        private readonly HashSet<int> _dirtyIds = new HashSet<int>();
        private readonly List<int> _deletedIds = new List<int>();
        private int _nextReplicationId;

        /// <summary>整个数组的版本号（对应 ArrayReplicationKey）。</summary>
        public int ArrayReplicationKey;

        /// <summary>上一次发送时对端已确认的数组版本号（对应 BaseReplicationKey）。</summary>
        public int BaseReplicationKey;

        /// <summary>元素数量。</summary>
        public int Count
        {
            get { return _items.Count; }
        }

        /// <summary>按下标取元素。</summary>
        public T this[int index]
        {
            get { return _items[index]; }
        }

        /// <summary>当前待发送的变化元素身份集合。</summary>
        public ICollection<int> DirtyIds
        {
            get { return _dirtyIds; }
        }

        /// <summary>当前待发送的删除元素身份集合。</summary>
        public IReadOnlyList<int> DeletedIds
        {
            get { return _deletedIds; }
        }

        /// <summary>是否有待发送的变化。</summary>
        public bool HasChanges
        {
            get { return _dirtyIds.Count > 0 || _deletedIds.Count > 0; }
        }

        /// <summary>按身份查找下标；找不到返回 -1。</summary>
        public int IndexOfId(int replicationId)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].ReplicationId == replicationId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>新增元素。首次标脏时分配稳定的 ReplicationId。</summary>
        public void Add(T item)
        {
            if (item.ReplicationId == 0)
            {
                item.ReplicationId = ++_nextReplicationId;
            }

            item.ArrayOwner = this;
            _items.Add(item);
            MarkItemDirtyInternal(item);
        }

        /// <summary>
        /// 用末尾元素替换并删除指定下标。
        /// 代价最低：只标脏被交换过来的那一个元素。优先使用本方法。
        /// </summary>
        public void RemoveAtSwap(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                return;
            }

            T removed = _items[index];
            int lastIndex = _items.Count - 1;

            if (index != lastIndex)
            {
                T moved = _items[lastIndex];
                _items[index] = moved;

                // 被交换过来的元素位置变了，需要让它重新发送。
                MarkItemDirtyInternal(moved);
            }

            _items.RemoveAt(lastIndex);
            removed.ArrayOwner = null;

            MarkArrayDirty();
            _dirtyIds.Remove(removed.ReplicationId);
            _deletedIds.Add(removed.ReplicationId);
        }

        /// <summary>
        /// 顺序删除指定下标。
        /// 注意：下标平移后，其后所有元素都需要重新发送（移位放大），
        /// 因此本方法的代价随 index 之后剩余元素数量增长。能用 RemoveAtSwap 就不要用本方法。
        /// </summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                return;
            }

            T removed = _items[index];
            _items.RemoveAt(index);
            removed.ArrayOwner = null;

            MarkArrayDirty();
            _dirtyIds.Remove(removed.ReplicationId);
            _deletedIds.Add(removed.ReplicationId);

            // 移位段整体标脏：这是本方法相对 RemoveAtSwap 多出来的代价，显式写出来避免被误当成等价实现。
            for (int i = index; i < _items.Count; i++)
            {
                MarkItemDirtyInternal(_items[i]);
            }
        }

        /// <summary>清空整个数组。</summary>
        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _deletedIds.Add(_items[i].ReplicationId);
                _items[i].ArrayOwner = null;
            }

            _items.Clear();
            _dirtyIds.Clear();
            ArrayReplicationKey++;
        }

        /// <summary>
        /// 标记整个数组结构发生变化（删除、重排、整体重建）。
        /// 对应 MarkArrayDirty。加元素或改元素内容请用元素的 MarkItemDirty。
        /// </summary>
        public void MarkArrayDirty()
        {
            ArrayReplicationKey++;
        }

        /// <summary>发送并收到确认后清除待发状态（对应发送成功后推进 BaseReplicationKey）。</summary>
        public void AcknowledgeSent(int arrayReplicationKey)
        {
            BaseReplicationKey = arrayReplicationKey;
            _dirtyIds.Clear();
            _deletedIds.Clear();
        }

        internal override void MarkItemDirtyInternal(PMFastArrayItem item)
        {
            if (item == null)
            {
                return;
            }

            if (item.ReplicationId == 0)
            {
                item.ReplicationId = ++_nextReplicationId;
            }

            item.ReplicationKey++;
            item.MostRecentArrayReplicationKey = ArrayReplicationKey;
            _dirtyIds.Add(item.ReplicationId);
        }
    }
}
