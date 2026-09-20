namespace PMNet
{
    /// <summary>
    /// 网络角色（对应 UE 的 ENetRole）。
    ///
    /// 语义要点：Role 描述的是「某个副本在特定连接上的身份」，不是某个对象全局唯一的身份。
    /// 同一个对象的服务端权威副本是 Authority，拥有者客户端上的副本是 AutonomousProxy，
    /// 其他相关客户端上的副本是 SimulatedProxy。
    ///
    /// 这是「同一份函数两端复用、靠网络角色区分执行端」的基础（见计划 §3.2）。
    /// </summary>
    public enum PMNetRole : byte
    {
        /// <summary>未参与复制。</summary>
        None = 0,

        /// <summary>服务端权威副本。</summary>
        Authority = 1,

        /// <summary>拥有者客户端副本，可本地预测并上报输入。</summary>
        AutonomousProxy = 2,

        /// <summary>其他客户端上的模拟副本，只消费复制结果并插值。</summary>
        SimulatedProxy = 3,
    }

    /// <summary>
    /// 角色判定辅助。
    ///
    /// **为什么必须用这里的方法而不是数值比较**：UE 的 ENetRole 顺序是
    /// `None=0, SimulatedProxy=1, AutonomousProxy=2, Authority=3`，所以 UE 源码里的
    /// 「`LocalRole &lt; ROLE_Authority`」等价于「不是权威」。
    /// 而本项目的 PMNetRole 顺序不同（Authority=1），直接用 `&lt;` 比较会静默错判——
    /// 只有 None 被认为「小于权威」，AutonomousProxy / SimulatedProxy 会被误当成权威。
    /// 迁移 UE 的 callspace 真值表时这是最容易踩的坑，因此这里提供显式谓词。
    /// </summary>
    public static class PMNetRoles
    {
        /// <summary>是否具备权威（对应 UE 的 `LocalRole == ROLE_Authority`）。</summary>
        public static bool IsAuthority(PMNetRole role) { return role == PMNetRole.Authority; }

        /// <summary>
        /// 是否「低于权威」（对应 UE 的 `LocalRole &lt; ROLE_Authority`）。
        /// 注意这是**不等于权威**，不是数值小于。
        /// </summary>
        public static bool IsLessThanAuthority(PMNetRole role) { return role != PMNetRole.Authority; }

        /// <summary>是否为「无角色」（对应 UE 的 `ROLE_None`）。</summary>
        public static bool IsNone(PMNetRole role) { return role == PMNetRole.None; }
    }

    /// <summary>
    /// 进程网络模式（对应 UE 的 ENetMode）。
    /// hyld 的目标形态是 DedicatedServer（局内 DS）与 Client 两种，Standalone 保留给单机与工具。
    ///
    /// 说明：`ListenServer` 在当前形态里**不会被产生**，保留它是为了让 callspace 真值表
    /// 与 UE 源码逐分支对齐（UE 的 `bIsServer` = ListenServer 或 DedicatedServer）。
    /// 若将来真要做主机模式，判定逻辑已经就位，不需要改真值表。
    /// </summary>
    public enum PMNetMode : byte
    {
        /// <summary>单机：既是服务端也是客户端，不做真正收发。</summary>
        Standalone = 0,

        /// <summary>专用服务器（局内战斗 DS）。</summary>
        DedicatedServer = 1,

        /// <summary>客户端。</summary>
        Client = 2,

        /// <summary>主机（既是服务端也是客户端，但对远程连接而言是服务端）。当前形态不产生此值。</summary>
        ListenServer = 3,
    }

    /// <summary>
    /// 函数可执行位置（对应 UE 的 FunctionCallspace）。
    ///
    /// 必须是位掩码而非单值枚举：UE 里服务端调用 NetMulticast 的结果是 Local|Remote（既本地执行又发出去），
    /// 而 Absorbed 是「没有任何位」——两端都不执行、静默吞掉。
    ///
    /// **位值必须与 UE 一致**（`Source/Runtime/CoreUObject/Public/UObject/Script.h`）：
    /// `Absorbed = 0x0 / Remote = 0x1 / Local = 0x2`。
    /// 本项目自己的契约 `Docs/plans/net-r0-contract.md` §3.4 也按此冻结。
    /// 之前本枚举曾把 Local 与 Remote 写成 1/2 互换，虽不影响位运算语义，
    /// 但会让「与 UE 逐位对齐」的追溯失效，已纠正。
    /// </summary>
    [System.Flags]
    public enum PMFunctionCallspace : byte
    {
        /// <summary>两端都不执行，静默吞掉。对应 UE 的 Absorbed（值为 0）。</summary>
        Absorbed = 0,

        /// <summary>发往远端。对应 UE 的 Remote = 0x1。</summary>
        Remote = 1,

        /// <summary>本地直接执行。对应 UE 的 Local = 0x2。</summary>
        Local = 2,
    }
}
