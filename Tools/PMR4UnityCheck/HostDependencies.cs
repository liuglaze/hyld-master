// ============================================================================
//  PMR4UnityCheck / HostDependencies.cs —— 宿主在**门禁工程**里的最小旧链边界替身
// ============================================================================
//
//  为什么需要这个文件：
//    R4-B / B3 要求「新宿主必须在 Tools/PMR4UnityCheck 引用真实 Unity2019 DLL 编译验证」。
//    两个宿主里只有**客户端**宿主（PMClientSessionHost）会碰到旧链，而且只碰**一个**入口：
//
//        global::Server.UDPSocketManger.CloseExisting();     // 关掉旧战斗 UDP socket（硬编码 7777）
//
//    真实实现（Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs）会把整套旧战斗链
//    拖进来（Protobuf MainPack、旧 BattleData、HYLDManger…），而门禁要证明的是
//    「新宿主的运动接线能在真实 Unity API 上编译」，不是「旧链能不能编译」。
//
//  边界纪律（本文件必须守住的三条）：
//    1) **只替旧链边界，不替本项目新类型**：Driver / Physics / Session / PMPrediction /
//       PMMover / PMNet / PMUnity 一律编**真实源码**（csproj 逐个 include）。
//       如果在这里桩掉其中任何一个，门禁就退化成"自己证明自己"。
//    2) **签名与真实实现一致**：真实是 `class UDPSocketManger`（internal）+ `public static void CloseExisting()`，
//       同程序集内可调。这里保持"同程序集可调"的可见性关系，方法名/参数/返回值逐字一致，
//       否则门禁会给出假的绿灯（真 Unity 里编译不过）。
//    3) **空实现是刻意的**：门禁不运行游戏逻辑，这个入口的行为（关 socket）与本次验证无关；
//       它只用来让"新宿主引用了旧链边界"这件事在类型层面成真。
// ============================================================================

namespace Server
{
    /// <summary>
    /// 旧客户端战斗 UDP 管理器的**门禁替身**（只保留新宿主用到的那一个静态入口）。
    ///
    /// 真实类型：<c>Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs</c>（internal，含 InitSocket /
    /// ReceiveLoop / DrainAndDispatch …）。门禁不编它，因为它会把整套旧战斗协议链拖进来。
    /// </summary>
    internal static class UDPSocketManger
    {
        /// <summary>
        /// 关闭已存在的旧战斗 UDP socket。幂等；未初始化时无副作用（真实实现同语义）。
        /// 新宿主在进入 PMDS1 局时调它，避免旧 socket 继续收硬编码 7777 的旧包。
        /// </summary>
        public static void CloseExisting()
        {
        }
    }
}
