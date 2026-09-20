namespace PMNet
{
    /// <summary>
    /// 一条网络连接（对应 UE 的 UNetConnection）。
    ///
    /// 职责边界：只负责「这条连接是谁」「能不能发」「发什么可靠性」，
    /// 不负责对象筛选、优先级与带宽预算——那是复制层的事（见计划 §4.4）。
    ///
    /// hyld 目标形态下同时存在两条连接（局外 Lobby TCP / 局内 DS UDP），
    /// 因此这里按「连接」抽象，而不是按「socket」抽象。
    /// </summary>
    public abstract class PMNetConnection
    {
        /// <summary>连接序号，在本进程内唯一。</summary>
        public readonly int ConnectionId;

        /// <summary>该连接是否处于服务端侧。</summary>
        public readonly bool IsServerSide;

        /// <summary>
        /// 是否处于「忽略 RPC」状态（对应 UE 的 RepFlags.bIgnoreRPCs）。
        /// 该状态下收到的 RPC 一律不执行，用于通道关闭/对象销毁等窗口。
        /// </summary>
        public bool IgnoreRpcs;

        protected PMNetConnection(int connectionId, bool isServerSide)
        {
            ConnectionId = connectionId;
            IsServerSide = isServerSide;
        }

        /// <summary>连接是否已就绪到可以收发。</summary>
        public abstract bool IsReady { get; }

        /// <summary>发送一段已序列化的字节。可靠性由调用方按 RPC 标记决定。</summary>
        public abstract void Send(byte[] payload, PMRpcReliability reliability);

        /// <summary>
        /// 取出该连接观察者的位置，用于按距离做相关性判定（对应 UE 的 Viewer 位置）。
        /// 不做复制的连接可以返回 false，表示「无观察位置」——此时距离裁剪对它是无意义的。
        /// </summary>
        public virtual bool TryGetViewerLocation(out float x, out float y, out float z)
        {
            x = 0f;
            y = 0f;
            z = 0f;
            return false;
        }
    }
}
