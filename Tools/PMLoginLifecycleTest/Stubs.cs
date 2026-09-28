using SocketProto;

namespace Server
{
    /// <summary>
    /// 测试替身：<see cref="Client"/> / <see cref="Server"/> 的真实实现依赖 Socket、
    /// 控制器分发表与 DS 宿主（T-LOOP2 不涉及），但被链接的生产源 `Server/DAO/UserData.cs`
    /// 的方法签名里出现了这两个类型，因此这里只保留「编译所需的最小面」。
    ///
    /// 为什么不改用假实现去测：本门禁要验证的是**真实** `UserData.Login` 的身份写入与
    /// **真实** `PMActiveClientIndex` 的登记/删除判定，替身只补不参与判定的外设。
    /// 替身的身份属性刻意与真实 `Client` 逐字一致（一律委托给 `UserData`），
    /// 这样「Login 写身份 → 连接 UID/UserName」这条链在本门禁里与生产完全同形。
    /// </summary>
    internal class Client : IPMActiveClientEntry
    {
        private readonly DAO.UserData _userdata = new DAO.UserData();

        /// <summary>与真实 Client.GetUserData 同形：本连接的用户数据门面。</summary>
        public DAO.UserData GetUserData
        {
            get { return _userdata; }
        }

        /// <summary>与真实 Client.UserName 同形：委托给 UserData。</summary>
        public string UserName
        {
            get { return _userdata.UserName; }
        }

        /// <summary>与真实 Client.UID 同形：委托给 UserData（未写入真实身份时为 0）。</summary>
        public int UID
        {
            get { return _userdata.UID; }
        }

        /// <summary>与真实 Client.PlayerName 同形：委托给 UserData。</summary>
        public string PlayerName
        {
            get { return _userdata.PlayerName; }
        }

        /// <summary>与真实 Client.PlayerState 同形（断线收尾会写它）。</summary>
        public PlayerState PlayerState { get; set; }

        /// <summary>真实实现会走 TCP 写队列；本门禁不做网络，只记录调用次数。</summary>
        public void Send(MainPack pack)
        {
            SendCount++;
        }

        /// <summary>替身观测点：被要求发送的次数（用于确认没有意外路径）。</summary>
        public int SendCount { get; private set; }
    }

    /// <summary>
    /// 测试替身：只满足 `UserData` 里对 Server 的查询签名。
    /// 被 T-LOOP2 覆盖的路径（Login / FindPlayerInfo）完全不使用这些查询，
    /// 因此统一返回「不在线」，不会给负例制造假的通过理由。
    /// </summary>
    internal class Server
    {
        public PlayerState GetPlayerState(int id)
        {
            return PlayerState.PlayerOutline;
        }

        public Client GetClientByUserName(string username)
        {
            return null;
        }

        public Client GetClientByPlayerName(string playerName)
        {
            return null;
        }
    }
}
