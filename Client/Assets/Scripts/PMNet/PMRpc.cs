using System;

namespace PMNet
{
    /// <summary>RPC 方向（对应 UE 的 FUNC_NetServer / FUNC_NetClient / FUNC_NetMulticast）。</summary>
    public enum PMRpcKind : byte
    {
        /// <summary>客户端调用 → 服务端执行（UFUNCTION(Server)）。</summary>
        Server = 0,

        /// <summary>服务端调用 → 拥有者客户端执行（UFUNCTION(Client)）。</summary>
        Client = 1,

        /// <summary>服务端调用 → 所有相关连接执行（UFUNCTION(NetMulticast)）。</summary>
        Multicast = 2,
    }

    /// <summary>RPC 可靠性（对应 UE 的 Reliable / Unreliable 标记）。</summary>
    public enum PMRpcReliability : byte
    {
        /// <summary>不可靠：丢包即丢，适合高频状态性调用。</summary>
        Unreliable = 0,

        /// <summary>可靠：同一顺序域内保序且要求送达。</summary>
        Reliable = 1,
    }

    /// <summary>
    /// RPC 参数校验档位（对应 UE 的 `WithValidation` 与项目的 `ForceValidate` 约定）。
    ///
    /// **两者互斥**（D-R0-45）：UE 原生 `_Validate` 失败即断连，而项目的 `_ForceValidate`
    /// 是三态（`Accept` / `Report` / `Reject`）且 `Reject` 只跳过实现、不断连。
    /// 同一个 RPC 上同时声明两者会让「校验失败后到底断不断连」变得不确定，
    /// 因此生成器在声明阶段直接报错，不做静默择一。
    /// </summary>
    public enum PMRpcValidator : byte
    {
        /// <summary>不校验。</summary>
        None = 0,

        /// <summary>
        /// UE 原生校验：对应 `_Validate` 方法，返回 false 则**断开连接**。
        /// </summary>
        Validate = 1,

        /// <summary>
        /// 项目反外挂式三态校验：对应 `_ForceValidate` 方法，产出 `Accept` / `Report` / `Reject`。
        /// `Reject` 只跳过实现，**不断连**（对齐项目 `NetReport` / `NetReject` 口径）。
        /// </summary>
        ForceValidate = 2,
    }

    /// <summary>
    /// 函数的网络相关标志位（对应 UE 的 `FUNC_Net*` / `FUNC_Static` / `FUNC_Blueprint*`）。
    ///
    /// 之所以用位掩码而不是"方向枚举"：UE 的判定是逐位测的，
    /// 而且同一函数可能同时具有 `Net` + `NetServer`（客户端调用的上行 RPC）。
    /// 决定 callspace 的是**这一组位的组合**，不是单一枚举值。
    /// </summary>
    [Flags]
    public enum PMRpcFunctionFlags : uint
    {
        None = 0u,

        /// <summary>`FUNC_Net`：参与网络的函数（RPC 或网络服务）。</summary>
        Net = 1u << 0,

        /// <summary>`FUNC_NetServer`：客户端 → 服务端。</summary>
        NetServer = 1u << 1,

        /// <summary>`FUNC_NetClient`：服务端 → 拥有者客户端。</summary>
        NetClient = 1u << 2,

        /// <summary>`FUNC_NetMulticast`：服务端 → 所有相关连接。</summary>
        NetMulticast = 1u << 3,

        /// <summary>`FUNC_NetRequest`：网络服务请求（走 ServiceRequest 通道）。</summary>
        NetRequest = 1u << 4,

        /// <summary>`FUNC_NetResponse`：网络服务响应。</summary>
        NetResponse = 1u << 5,

        /// <summary>`FUNC_NetReliable`：可靠传输。</summary>
        NetReliable = 1u << 6,

        /// <summary>`FUNC_Static`：静态函数（没有实例，走全局判定）。</summary>
        Static = 1u << 7,

        /// <summary>`FUNC_BlueprintAuthorityOnly`：仅权威可调用，无权限时静默吞掉。</summary>
        BlueprintAuthorityOnly = 1u << 8,

        /// <summary>`FUNC_BlueprintCosmetic`：表现函数，DS 上不执行。</summary>
        BlueprintCosmetic = 1u << 9,
    }

    /// <summary>
    /// callspace 判定所需的上下文（`AActor::GetFunctionCallspace` 的输入）。
    ///
    /// 依据：`Source/Runtime/Engine/Private/Actor.cpp` 的 `GetFunctionCallspace`，
    /// 以及本仓库 `D:/hyld-refactor-survey/R0_2_ue_rpc.md` §A.3 的 16 分支表。
    /// 字段名与那里的分支一一对应。
    ///
    /// 注意：**全局判定结果不是字段**。#2 分支所需的值由
    /// <see cref="PMRpcDispatch.ComputeGlobalCallspace"/> 从 `NetMode` + `Flags` 现场推导。
    /// 若做成调用方注入的字段，结构体默认构造会得到 0（= Absorbed），
    /// 即"忘记赋值就静默什么都不执行"——而 UE 的默认是 Local，方向正好相反。
    /// </summary>
    public struct PMCallspaceContext
    {
        /// <summary>当前进程网络模式。</summary>
        public PMNetMode NetMode;

        /// <summary>本端对该对象的角色（`GetLocalRole()`）。</summary>
        public PMNetRole LocalRole;

        /// <summary>对端对该对象的角色（`GetRemoteRole()`）。</summary>
        public PMNetRole RemoteRole;

        /// <summary>函数的网络标志位。</summary>
        public PMRpcFunctionFlags Flags;

        /// <summary>编辑器内是否允许脚本执行（`GAllowActorScriptExecutionInEditor`）。</summary>
        public bool AllowScriptExecutionInEditor;

        /// <summary>`GetWorld() != nullptr`。</summary>
        public bool HasWorld;

        /// <summary>`!IsValidChecked(this)`——对象处于 pending kill。</summary>
        public bool IsPendingKill;

        /// <summary>网络服务响应用：`RPCId`。</summary>
        public int RpcId;

        /// <summary>当前是否处于"接收远端函数"过程中（`ERemoteFunctionMode::Receiving`）。</summary>
        public bool IsReceivingRemoteFunction;

        /// <summary>`GetNetConnection() != nullptr`。</summary>
        public bool HasNetConnection;

        /// <summary>`GetNetOwningPlayer() != nullptr`。</summary>
        public bool HasNetOwningPlayer;

        /// <summary>`GetNetOwningPlayer()` 是否为 `ULocalPlayer`。</summary>
        public bool NetOwningPlayerIsLocal;

        /// <summary>`HasNetOwner()`。</summary>
        public bool HasNetOwner;

        /// <summary>`Driver != nullptr && Driver->World != nullptr`。</summary>
        public bool DriverWorldValid;

        /// <summary>是否处于服务端语义（UE：ListenServer 或 DedicatedServer）。</summary>
        public bool IsServer
        {
            get
            {
                return NetMode == PMNetMode.ListenServer || NetMode == PMNetMode.DedicatedServer;
            }
        }
    }

    /// <summary>
    /// callspace 判定结果。除掩码外还带「命中了哪个分支」，
    /// 因为分支**顺序本身是契约**（R0_2 U2），出错时必须能定位到具体分支。
    /// </summary>
    public struct PMCallspaceResult
    {
        /// <summary>判定结果掩码。</summary>
        public PMFunctionCallspace Callspace;

        /// <summary>命中的分支编号（1..16），用于测试与日志。</summary>
        public int Branch;

        /// <summary>是否应输出 Error 级日志（UE 在第 14-① 分支会打 Error）。</summary>
        public bool LogError;

        /// <summary>是否应输出 Warning 级日志（UE 在第 15 分支的客户端侧会打 Warning）。</summary>
        public bool LogWarning;

        public PMCallspaceResult(PMFunctionCallspace callspace, int branch)
        {
            Callspace = callspace;
            Branch = branch;
            LogError = false;
            LogWarning = false;
        }
    }

    /// <summary>标记一个方法为 Server RPC：客户端调用会自动发往 DS，DS 调用则本地执行。</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PMServerRpcAttribute : Attribute
    {
        /// <summary>可靠性，默认不可靠（对应 UE 默认 Unreliable）。</summary>
        public PMRpcReliability Reliability = PMRpcReliability.Unreliable;

        /// <summary>是否开启参数合法性校验（对应 UE 的 WithValidation 与项目 NetForceValidate 层）。</summary>
        public bool WithValidation;

        /// <summary>
        /// 校验档位，默认 <see cref="PMRpcValidator.None"/>。
        ///
        /// 与 <see cref="WithValidation"/> 的关系：`WithValidation = true` 等价于
        /// `Validator = Validate`（UE 原生语义、失败断连）。要表达项目式的三态校验，
        /// 必须显式写 `Validator = ForceValidate`。
        /// 若两者同时被显式设置且互相矛盾（`WithValidation=true` 且 `Validator=ForceValidate`），
        /// 生成器报错，不做静默择一（D-R0-45）。
        /// </summary>
        public PMRpcValidator Validator = PMRpcValidator.None;
    }

    /// <summary>标记一个方法为 Client RPC：DS 调用会发给拥有者客户端。</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PMClientRpcAttribute : Attribute
    {
        public PMRpcReliability Reliability = PMRpcReliability.Unreliable;

        /// <summary>校验档位；Client RPC 的校验在客户端侧执行。</summary>
        public PMRpcValidator Validator = PMRpcValidator.None;
    }

    /// <summary>
    /// 标记一个方法为 NetMulticast RPC。
    /// 注意语义：这不是空间广播，而是「所有复制了该对象且与该连接相关的连接」
    /// （对应 UE `NetDriver.cpp` 的 `ProcessRemoteFunction` 多播分支）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PMNetMulticastAttribute : Attribute
    {
        public PMRpcReliability Reliability = PMRpcReliability.Unreliable;

        /// <summary>校验档位；Multicast 的校验在服务端侧执行（每个相关连接各判一次）。</summary>
        public PMRpcValidator Validator = PMRpcValidator.None;
    }

    /// <summary>
    /// RPC 分流与归属校验。
    ///
    /// 本类把 UE 的两段判定固化成可测逻辑：
    ///   1. 发送侧：`GetFunctionCallspace` —— 决定本地执行 / 发往远端 / 静默吞掉；
    ///   2. 接收侧：`ShouldCallRemoteFunction` —— 决定收到的 Server RPC 该不该执行（归属校验）。
    ///
    /// 事实来源：
    ///   - `Source/Runtime/CoreUObject/Private/UObject/ScriptCore.cpp`（`UObject::CallFunction` 三态分派）
    ///   - `Source/Runtime/CoreUObject/Public/UObject/Object.h`（`GetFunctionCallspace` / `CallRemoteFunction` 默认实现）
    ///   - `Source/Runtime/Engine/Private/Actor.cpp`（`AActor::GetFunctionCallspace`，16 个分支）
    ///   - `Source/Runtime/Engine/Private/UnrealEngine.cpp`（`UEngine::GetGlobalFunctionCallspace`）
    ///   - `Source/Runtime/Engine/Private/NetDriver.cpp`（`ShouldCallRemoteFunction`）
    ///   - 整理见 `D:/hyld-refactor-survey/R0_2_ue_rpc.md` 与 `Docs/plans/net-r0-contract.md`
    /// </summary>
    public static class PMRpcDispatch
    {
        /// <summary>
        /// 全局判定（对应 `UEngine::GetGlobalFunctionCallspace`）。
        ///
        /// 原文语义（`UnrealEngine.cpp`）：
        /// <code>
        /// if (!bIsAuthoritativeFunc &amp;&amp; !bIsCosmeticFunc) return Local;   // 两个位都没有 → 恒 Local
        /// if (WorldNetMode == NM_DedicatedServer &amp;&amp; bIsCosmeticFunc)      return Absorbed;
        /// if (WorldNetMode == NM_Client          &amp;&amp; bIsAuthoritativeFunc) return Absorbed;
        /// return Local;                                                 // 找不到网络模式也一律 Local
        /// </code>
        ///
        /// 三条必须记住的语义：
        /// 1. **只看两个函数位**；两个位都没有时恒 Local，与角色/网络模式无关。
        /// 2. 判据是 **WorldNetMode**，不是 `LocalRole`。
        /// 3. 默认值是 **Local**（"找不到网络模式就本地执行"），不是 Absorbed。
        /// </summary>
        public static PMFunctionCallspace ComputeGlobalCallspace(
            PMNetMode netMode,
            PMRpcFunctionFlags flags)
        {
            bool isAuthoritativeFunc = (flags & PMRpcFunctionFlags.BlueprintAuthorityOnly) != 0;
            bool isCosmeticFunc = (flags & PMRpcFunctionFlags.BlueprintCosmetic) != 0;

            if (!isAuthoritativeFunc && !isCosmeticFunc)
            {
                return PMFunctionCallspace.Local;
            }

            if (netMode == PMNetMode.DedicatedServer && isCosmeticFunc)
            {
                return PMFunctionCallspace.Absorbed;
            }

            if (netMode == PMNetMode.Client && isAuthoritativeFunc)
            {
                return PMFunctionCallspace.Absorbed;
            }

            // 含 Standalone / ListenServer：UE 在此一律 Local
            return PMFunctionCallspace.Local;
        }

        /// <summary>
        /// 发送侧 callspace 判定（对应 `AActor::GetFunctionCallspace`）。
        ///
        /// **分支顺序本身是契约**：例如 pending-kill 必须在 NetRequest 之前判、
        /// Standalone 必须在 Net 位判定之前判。改动顺序会改变结果。
        /// 返回值同时给出命中的分支编号，便于对照 R0_2 §A.3 逐行核对。
        /// </summary>
        public static PMCallspaceResult EvaluateCallspace(in PMCallspaceContext ctx)
        {
            bool isServer = ctx.IsServer;

            // ── #3 基线（后续分支可能覆盖它）────────────────────────────────
            // LocalRole < Authority 且"仅权威"标记 → 吞掉；否则本地执行。
            PMFunctionCallspace baseline =
                (PMNetRoles.IsLessThanAuthority(ctx.LocalRole)
                 && (ctx.Flags & PMRpcFunctionFlags.BlueprintAuthorityOnly) != 0)
                    ? PMFunctionCallspace.Absorbed
                    : PMFunctionCallspace.Local;

            // ── #1 编辑器内脚本执行 ─────────────────────────────────────────
            if (ctx.AllowScriptExecutionInEditor)
            {
                return new PMCallspaceResult(PMFunctionCallspace.Local, 1);
            }

            // ── #2 静态函数 / 无 World → 全局判定 ───────────────────────────
            if ((ctx.Flags & PMRpcFunctionFlags.Static) != 0 || !ctx.HasWorld)
            {
                return new PMCallspaceResult(
                    ComputeGlobalCallspace(ctx.NetMode, ctx.Flags), 2);
            }

            // ── #4 pending kill：永不走 Remote ──────────────────────────────
            if (ctx.IsPendingKill)
            {
                return new PMCallspaceResult(baseline, 4);
            }

            // ── #5 网络服务请求 ─────────────────────────────────────────────
            if ((ctx.Flags & PMRpcFunctionFlags.NetRequest) != 0)
            {
                return new PMCallspaceResult(PMFunctionCallspace.Remote, 5);
            }

            // ── #6 网络服务响应 ─────────────────────────────────────────────
            if ((ctx.Flags & PMRpcFunctionFlags.NetResponse) != 0)
            {
                return new PMCallspaceResult(
                    ctx.RpcId > 0 ? PMFunctionCallspace.Local : PMFunctionCallspace.Absorbed, 6);
            }

            // ── #7 单机：不允许"发上去" ─────────────────────────────────────
            // 注意此处 UE 直接返回 Local/Absorbed，**不回落基线**。
            if (ctx.NetMode == PMNetMode.Standalone)
            {
                bool absorb = PMNetRoles.IsLessThanAuthority(ctx.LocalRole)
                              && (ctx.Flags & PMRpcFunctionFlags.NetServer) != 0;
                return new PMCallspaceResult(
                    absorb ? PMFunctionCallspace.Absorbed : PMFunctionCallspace.Local, 7);
            }

            // ── #8 DS 不跑表现函数 ──────────────────────────────────────────
            if (ctx.NetMode == PMNetMode.DedicatedServer
                && (ctx.Flags & PMRpcFunctionFlags.BlueprintCosmetic) != 0)
            {
                return new PMCallspaceResult(PMFunctionCallspace.Absorbed, 8);
            }

            // ── #9 非网络函数：守恒于基线 ───────────────────────────────────
            if ((ctx.Flags & PMRpcFunctionFlags.Net) == 0)
            {
                return new PMCallspaceResult(baseline, 9);
            }

            // ── #10 上溯到最顶层 SuperFunction ─────────────────────────────
            // 引擎在此把判定对象换成父函数。hyld 的生成器直接产出最终标志位，
            // 因此这里不需要运行期上溯；保留编号以对齐真值表。
            // （若将来支持 C# 方法继承链上的 RPC 声明，需在此处补解析。）
            bool hasDirectionBit =
                (ctx.Flags & (PMRpcFunctionFlags.NetServer
                              | PMRpcFunctionFlags.NetClient
                              | PMRpcFunctionFlags.NetMulticast)) != 0;

            // ── #11 NetMulticast ────────────────────────────────────────────
            if ((ctx.Flags & PMRpcFunctionFlags.NetMulticast) != 0)
            {
                if (isServer)
                {
                    // 服务端：本端也执行，并按相关性逐连接外发。
                    return new PMCallspaceResult(
                        PMNetRoles.IsNone(ctx.RemoteRole)
                            ? PMFunctionCallspace.Local                                   // NoRemoteRole
                            : (PMFunctionCallspace.Local | PMFunctionCallspace.Remote),
                        11);
                }

                // 客户端调多播：只本端执行（或基线为 Absorbed 时吞掉），不外发。
                return new PMCallspaceResult(baseline, 11);
            }

            // ── #12 / #13 单向过滤与「接收远端函数」────────────────────────
            // 注意结构：UE 源码里「接收远端函数」是**挂在方向位判定上的 else-if**，
            // 即只有函数**没有**任何方向位（双向 Remote 说明符）时才看它。
            // 写成并列的 if 会在一函数同时带 NetServer 与 NetClient 时提前返回，
            // 从而跳过后面的权威可达性判定。
            if (hasDirectionBit)
            {
                // #12 支 1：服务端调 Server 函数 → 本端执行，不回环。
                if (isServer && (ctx.Flags & PMRpcFunctionFlags.NetClient) == 0)
                {
                    return new PMCallspaceResult(baseline, 12);
                }

                // #12 支 2：客户端调 Client 函数 → 本端执行，不外发。
                if (!isServer && (ctx.Flags & PMRpcFunctionFlags.NetServer) == 0)
                {
                    return new PMCallspaceResult(baseline, 12);
                }
            }
            else if (ctx.IsReceivingRemoteFunction)
            {
                // #13：双向 Remote（带 FUNC_Net 但无任何方向位）的接收钩子。
                // 这是 #13 的**唯一可达形态**：只要有任一方向位，外层 if 就接管。
                return new PMCallspaceResult(baseline, 13);
            }

            // ── #14 权威对象："有没有可寻址的收件人" ────────────────────────
            // UE 结构（原文）：
            //   if (NetConnection == nullptr) {
            //       if (ClientPlayer == nullptr) { ...①... }
            //       else if (Cast<ULocalPlayer>(ClientPlayer)) { return Callspace; }  // ②
            //       // owning player 非空且非 LocalPlayer → 什么都不做，掉出本块
            //   }
            //   else if (!Driver || !Driver->World) { return Absorbed; }              // ③
            // 即 **①② 都在「没有连接」之内，③ 是「有连接」的 else-if**。
            if (PMNetRoles.IsAuthority(ctx.LocalRole))
            {
                if (!ctx.HasNetConnection)
                {
                    if (!ctx.HasNetOwningPlayer)
                    {
                        // ① 既无连接也无 owning player
                        if (ctx.HasNetOwner)
                        {
                            return new PMCallspaceResult(PMFunctionCallspace.Absorbed, 14);
                        }

                        if (!hasDirectionBit)
                        {
                            // 双向 Remote 却无 owning player：引擎打 Error 后吞掉。
                            PMCallspaceResult err =
                                new PMCallspaceResult(PMFunctionCallspace.Absorbed, 14);
                            err.LogError = true;
                            return err;
                        }

                        // AI 拥有的对象调 Client RPC 的典型路径：本地执行。
                        return new PMCallspaceResult(baseline, 14);
                    }

                    // ② 有 owning player 且是本机 LocalPlayer → 本地执行
                    if (ctx.NetOwningPlayerIsLocal)
                    {
                        return new PMCallspaceResult(baseline, 14);
                    }

                    // owning player 非空且不是 LocalPlayer：掉出本块（不返回）。
                }
                else if (!ctx.DriverWorldValid)
                {
                    // ③ 有连接但 Driver/World 无效（多为关服中）→ 吞掉
                    return new PMCallspaceResult(PMFunctionCallspace.Absorbed, 14);
                }
            }

            // ── #15 对象根本没在复制 ────────────────────────────────────────
            if (PMNetRoles.IsNone(ctx.RemoteRole))
            {
                PMCallspaceResult r = new PMCallspaceResult(PMFunctionCallspace.Absorbed, 15);
                r.LogWarning = !isServer;
                return r;
            }

            // ── #16 真正发出去 ──────────────────────────────────────────────
            return new PMCallspaceResult(PMFunctionCallspace.Remote, 16);
        }

        /// <summary>
        /// 接收侧归属校验（对应 `UNetDriver::ShouldCallRemoteFunction`）。
        ///
        /// 原文（`NetDriver.cpp`）：
        /// <code>
        /// return ((!IsServer() || RepFlags.bNetOwner) &amp;&amp; !RepFlags.bIgnoreRPCs);
        /// </code>
        /// 即：**服务端侧只接受来自「该对象的拥有者连接」的调用**；
        /// 客户端侧不额外判归属（能收到就说明服务端发的）。
        ///
        /// ⚠ 历史缺陷：本方法此前的实现是「服务端直接放行」，
        /// 与 UE 相反，等于取消了服务端全部的归属校验。见 net-r0-contract.md 的 D-R0-42。
        /// </summary>
        /// <param name="receiverIsServer">接收方是否为服务端。</param>
        /// <param name="receiverIsObjectOwner">接收方连接是否为该对象的拥有者连接（对应 RepFlags.bNetOwner）。</param>
        /// <param name="ignoreRpcs">是否处于忽略 RPC 的状态（对应 RepFlags.bIgnoreRPCs）。</param>
        public static bool ShouldCallRemoteFunction(
            bool receiverIsServer,
            bool receiverIsObjectOwner,
            bool ignoreRpcs)
        {
            return (!receiverIsServer || receiverIsObjectOwner) && !ignoreRpcs;
        }

        /// <summary>callspace 是否要求本地执行。</summary>
        public static bool ShouldExecuteLocal(PMFunctionCallspace callspace)
        {
            return (callspace & PMFunctionCallspace.Local) != 0;
        }

        /// <summary>callspace 是否要求发往远端。</summary>
        public static bool ShouldSendRemote(PMFunctionCallspace callspace)
        {
            return (callspace & PMFunctionCallspace.Remote) != 0;
        }

        /// <summary>
        /// 便捷重载：按方向 + 角色直接给出 callspace（供业务侧少数手写调用点使用）。
        /// 内部仍走完整的 16 分支判定，不做简化。
        ///
        /// 注意 `HasNetConnection` 取 `isOwner` 而**不是** `isServer || isOwner`：
        /// 后者在 DS 上恒为 true，会让 #14-① 里「权威 + 无连接 + 无 owning player +
        /// 有方向位 → 本地执行（AI 拥有的对象调 Client RPC）」这条 UE 合法路径永远不可达，
        /// 无主对象上的 Client RPC 会被判成 Remote（发给不存在的人、DS 上根本不执行）。
        /// </summary>
        public static PMFunctionCallspace EvaluateCallspace(
            PMNetMode netMode,
            PMNetRole role,
            PMRpcKind kind,
            bool isOwner,
            bool authorityOnly)
        {
            bool serverSide = netMode == PMNetMode.ListenServer || netMode == PMNetMode.DedicatedServer;

            PMCallspaceContext ctx = new PMCallspaceContext();
            ctx.NetMode = netMode;
            ctx.LocalRole = role;
            // RemoteRole 取自"对端看到的角色"：服务端侧对端是拥有者客户端，反之则是权威。
            ctx.RemoteRole = role == PMNetRole.None
                ? PMNetRole.None
                : (serverSide ? PMNetRole.AutonomousProxy : PMNetRole.Authority);
            ctx.Flags = PMRpcFunctionFlags.Net
                        | (kind == PMRpcKind.Server ? PMRpcFunctionFlags.NetServer : PMRpcFunctionFlags.None)
                        | (kind == PMRpcKind.Client ? PMRpcFunctionFlags.NetClient : PMRpcFunctionFlags.None)
                        | (kind == PMRpcKind.Multicast ? PMRpcFunctionFlags.NetMulticast : PMRpcFunctionFlags.None)
                        | (authorityOnly ? PMRpcFunctionFlags.BlueprintAuthorityOnly : PMRpcFunctionFlags.None);
            ctx.HasWorld = true;
            ctx.HasNetConnection = isOwner;
            ctx.HasNetOwningPlayer = isOwner;
            ctx.NetOwningPlayerIsLocal = !serverSide && isOwner;
            ctx.DriverWorldValid = true;

            return EvaluateCallspace(ctx).Callspace;
        }
    }

    // =====================================================================================
    //  RPC 接收入口（带世界查找 / 类归属 / 归属校验 / 校验路由的真实派发入口）
    //
    //  生成物的接收分发只做「读参数 → 过校验 → 调实现」：它拿不到连接、也拿不到世界，
    //  因此 D-R0-42 的归属校验、D-R0-43 的方向判定在接收点**没有任何落点**。
    //  后果是「声明了校验」只覆盖"业务参数合法性"，
    //  覆盖不到"这条连接有没有资格调这条 RPC"。
    //
    //  本入口把判定固化成一条**顺序明确**的路径（顺序本身是契约）：
    //    ① 上下文可用性 → ② 世界查找目标对象 → ③ 目标存活
    //    → ④ 按 **(目标的 ClassId, RpcId)** 取该类的执行器（不属于目标类即拒）
    //    → ⑤ 方向合法 → ⑥ 归属 / IgnoreRpcs → ⑦ 解码 + 校验 + 执行
    //  前六步都发生在**解码之前**：未授权 / 方向错 / 类不对的包连参数都不读，
    //  更不会执行任何业务实现；`PMNet_RpcInvoke_*` 里的"尾随字节"检查
    //  又发生在**业务实现之前**（见 DeclEmitter 的发射点）。
    //
    //  为什么②必须早于④（顺序本身是契约）：RpcId 只在**类内**唯一（契约 §2.2），
    //  因此"是哪条 RPC"只有在拿到目标对象的 ClassId 之后才能确定。反过来先按裸 RpcId
    //  全局查表，就只能靠"对象类型转换失败"去兜住"投错类"，而那等于把拒绝
    //  退化成解码期异常 —— 只在载荷恰好能被解码时才会暴露。
    // =====================================================================================

    /// <summary>
    /// 原生 `_Validate` 返回 false 的**信号**。
    ///
    /// 语义（D-R0-45）：载荷本身解码成功，但业务校验判定这条调用不可信 ⇒ **请求断开来源连接**。
    /// 它与三态 `ForceValidate` 的 `Reject` 刻意不同：后者只跳过实现、**不断连**。
    ///
    /// 为什么用异常而不是"让 Invoke 返回状态"：`PMRpcInvoker` 的签名（`void (PMNetObject, PMNetReader)`）
    /// 是生成物与接收侧之间已冻结的那一面（生成器按它发射执行器），不能改；
    /// 而校验发生在生成物**内部**，它没有连接，只能把结论"抛"给
    /// 持有连接的那一层（本文件的接收入口）。异常携带 RpcId/方法名，接收入口据此请求断连。
    /// </summary>
    public sealed class PMNetRpcValidationFailedException : Exception
    {
        /// <summary>出问题的 RPC ID。</summary>
        public readonly ushort RpcId;

        /// <summary>出问题的 RPC 方法名（诊断用）。</summary>
        public readonly string MethodName;

        public PMNetRpcValidationFailedException(ushort rpcId, string methodName)
            : base("RPC " + methodName + " 的原生校验（_Validate）返回 false：按 D-R0-45 请求断开来源连接")
        {
            RpcId = rpcId;
            MethodName = methodName;
        }
    }

    /// <summary>
    /// 接收入口向"来源连接"请求断连的最小能力。
    ///
    /// **为什么需要这个接口**：`PMNetConnection` 只声明了
    /// `Send` / `IsReady` / `IgnoreRpcs` 三项能力，没有断连 API；而"断开一条连接"
    /// 是**宿主级**动作（原因码、对端通知、本地清场顺序都由宿主的传输层决定），
    /// 复制 / RPC 层不应自己另造一套。因此在**本文件**声明这个最小能力，由宿主连接实现；
    /// 宿主实现应当直接委托给既有的传输层 API
    /// `PMNet.Transport.PMTransport.Disconnect(PMDisconnectReason)`
    /// （`PMTransport.cs` 里已有该实现，包括"尽力通知对端一次再本地清场"）。
    ///
    /// 未实现本接口的连接上发生失败校验时，入口**不会静默当没事**：
    /// 计入 <see cref="PMNetRpcReceive.UnroutableDisconnectRequests"/> 并走 <see cref="PMNetRpcReceive.Warn"/>。
    /// </summary>
    public interface IPMNetRpcDisconnectTarget
    {
        /// <summary>请求断开本连接（由宿主决定具体原因码与清理动作）。</summary>
        void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName);
    }

    /// <summary>接收入口的判定结果。区分失败原因是**可验收**的前提："被拒"不等于"不知道为什么被拒"。</summary>
    public enum PMRpcReceiveStatus : byte
    {
        /// <summary>通过全部闸门，参数的解码/校验/执行已交给生成物的内部执行器。</summary>
        Applied = 0,

        /// <summary>世界 / 连接 / reader 之一为空 —— 调用方接线错误。</summary>
        InvalidContext = 1,

        /// <summary>RpcId 不在注册表里（未注册 / 两端版本不一致）。</summary>
        UnknownRpc = 2,

        /// <summary>世界查不到目标对象（未创建，或已销毁 —— 销毁即摘出索引）。</summary>
        UnknownTarget = 3,

        /// <summary>目标对象存在但不在 Active 状态（销毁中的窗口）。</summary>
        TargetNotActive = 4,

        /// <summary>方向非法（D-R0-43）：服务端收到 Client/Multicast，或客户端收到 Server。</summary>
        WrongDirection = 5,

        /// <summary>非拥有者连接发来的 Server RPC（D-R0-42）。</summary>
        NotOwner = 6,

        /// <summary>连接处于 IgnoreRpcs 状态（通道关闭 / 对象销毁窗口）。</summary>
        IgnoreRpcs = 7,

        /// <summary>载荷畸形：截断、越界、数组/字符串超限、尾随字节，或对象类型与 RPC 不匹配。</summary>
        Malformed = 8,

        /// <summary>原生校验失败：已向来源连接请求断连（D-R0-45）。</summary>
        ValidationFailed = 9,
    }

    /// <summary>接收入口的一次判定结果。</summary>
    public struct PMNetRpcReceiveResult
    {
        /// <summary>判定结果。</summary>
        public PMRpcReceiveStatus Status;

        /// <summary>本次投递的 RPC ID。</summary>
        public ushort RpcId;

        /// <summary>是否已就本次调用向来源连接发出了断连请求。</summary>
        public bool DisconnectRequested;

        /// <summary>诊断细节（失败原因）。</summary>
        public string Detail;

        /// <summary>是否已交给执行器（不代表业务实现一定执行了 —— ForceValidate 的 Reject 会跳过实现）。</summary>
        public bool Applied { get { return Status == PMRpcReceiveStatus.Applied; } }
    }

    /// <summary>
    /// **唯一的 RPC 接收入口**。
    ///
    /// 设计要点（每一条都有 E2E 断言）：
    /// 1. **先定位目标、再定位 RPC**：RpcId 只在类内唯一（契约 §2.2），因此执行器按
    ///    `(目标对象的 ClassId, RpcId)` 取；不属于目标类的包直接拒绝，
    ///    不靠对象类型转换兜底；
    /// 2. **先权限再解码**：类归属 / 非 Owner / 方向错 / IgnoreRpcs / 已销毁 的包不会进入解码，
    ///    更不会执行任何业务实现；
    /// 3. **归属校验用既有谓词**：`PMRpcDispatch.ShouldCallRemoteFunction`（D-R0-42），
    ///    三个入参都来自现场（世界角色 / 对象拥有者 / 连接标志），不接受调用方注入；
    /// 4. **畸形与尾随字节在业务实现之前**被拒：解码异常在生成物内抛出（数组/字符串上限、
    ///    reader 越界），尾随字节由生成物在解码后、调实现前显式抛出；
    /// 5. **失败可观测**：每种拒绝都有独立状态 + 计数器，宿主据此决定日志/断连策略；
    /// 6. **不代替宿主做断连决策**：只有原生校验失败会经
    ///    <see cref="IPMNetRpcDisconnectTarget"/> 发出断连请求。
    /// </summary>
    public static class PMNetRpcReceive
    {
        /// <summary>成功交给执行器的次数。</summary>
        public static long Delivered;

        /// <summary>被拒次数（不含畸形）。</summary>
        public static long Rejected;

        /// <summary>载荷畸形次数（协议错误，宿主可据此决定是否断连）。</summary>
        public static long MalformedCount;

        /// <summary>"请求断连"实际发出到连接上的次数。</summary>
        public static long DisconnectRequests;

        /// <summary>需要请求断连、但连接没有实现 <see cref="IPMNetRpcDisconnectTarget"/> 的次数（必须为 0）。</summary>
        public static long UnroutableDisconnectRequests;

        /// <summary>诊断告警出口（宿主/门禁接线；为 null 时静默）。</summary>
        public static Action<string> Warn;

        /// <summary>结果观察口（门禁用来做"喂包级"证据，而不是只看谓词）。</summary>
        public static Action<PMNetRpcReceiveResult> Observer;

        /// <summary>清零计数器（测试与热重载用）。</summary>
        public static void ResetStats()
        {
            Delivered = 0;
            Rejected = 0;
            MalformedCount = 0;
            DisconnectRequests = 0;
            UnroutableDisconnectRequests = 0;
        }

        /// <summary>
        /// 投递一条 RPC 载荷。
        /// </summary>
        /// <param name="world">目标对象所在世界（决定"接收方是不是服务端"）。</param>
        /// <param name="source">来源连接（归属校验与断连请求都作用于它）。</param>
        /// <param name="rpcId">RPC ID。</param>
        /// <param name="targetId">目标对象身份。</param>
        /// <param name="reader">参数载荷读取器（从参数起点开始）。</param>
        public static PMNetRpcReceiveResult Deliver(
            PMNetWorld world,
            PMNetConnection source,
            ushort rpcId,
            PMNetId targetId,
            PMNetReader reader)
        {
            if (world == null || source == null || reader == null)
            {
                return Finish(PMRpcReceiveStatus.InvalidContext, rpcId, false,
                    "世界 / 来源连接 / reader 为空");
            }

            // ── 世界查找（先于解码，也先于 RPC 查表）───────────────────────
            // 必须先拿到目标对象：RpcId 只在类内唯一（契约 §2.2），
            // "是哪条 RPC"由 (目标的 ClassId, RpcId) 共同决定。
            PMNetObject target;
            if (!world.TryFind(targetId, out target) || target == null)
            {
                return Finish(PMRpcReceiveStatus.UnknownTarget, rpcId, false, "世界查不到目标对象");
            }

            if (target.State != PMNetObjectState.Active)
            {
                return Finish(PMRpcReceiveStatus.TargetNotActive, rpcId, false,
                    "目标对象状态为 " + target.State);
            }

            // ── 按 (目标 ClassId, RpcId) 取该类自己的执行器 ──────────────────
            // 不属于目标类的 RPC 在这里就被拒：不靠对象类型转换失败兜底，
            // 也不存在"投给 A 类目标却执行了 B 类实现"这条串线路径。
            // （网络继承尚未完整支持：这里只做精确 ClassId 匹配，不沿基类链上查；
            // 若将来支持继承声明，需同时在这里和生成物归属里补解析。）
            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(target.ClassId, rpcId, out entry) || entry == null || entry.Invoke == null)
            {
                string detail = "目标类 " + target.ClassId + " 上没有注册 RpcId " + rpcId;
                PMNetRpcEntry elsewhere;
                if (PMNetRegistry.TryGetRpc(rpcId, out elsewhere) && elsewhere != null)
                {
                    detail += "（该 RpcId 属于类 " + elsewhere.OwningClassId + " 的 "
                              + elsewhere.Descriptor.MethodName + "，不接受投到别的类）";
                }

                return Finish(PMRpcReceiveStatus.UnknownRpc, rpcId, false, detail);
            }

            // ── 方向合法（D-R0-43）─────────────────────────────────────────
            bool receiverIsServer = world.IsServer;
            PMRpcKind direction = entry.Descriptor.Direction;
            if (receiverIsServer)
            {
                // 服务端只接受上行（Server）RPC；收到 Client/Multicast 说明对端在伪造方向。
                if (direction != PMRpcKind.Server)
                {
                    return Finish(PMRpcReceiveStatus.WrongDirection, rpcId, false,
                        "服务端收到了 " + direction + " 方向的 RPC");
                }
            }
            else if (direction == PMRpcKind.Server)
            {
                // 客户端只接受下行（Client / Multicast）；Server RPC 只允许上行。
                return Finish(PMRpcReceiveStatus.WrongDirection, rpcId, false,
                    "客户端收到了 Server 方向的 RPC");
            }

            // ── 归属 / IgnoreRpcs（D-R0-42）────────────────────────────────
            bool isOwnerConnection = IsOwnerConnection(target, source);
            if (!PMRpcDispatch.ShouldCallRemoteFunction(receiverIsServer, isOwnerConnection, source.IgnoreRpcs))
            {
                if (source.IgnoreRpcs)
                {
                    return Finish(PMRpcReceiveStatus.IgnoreRpcs, rpcId, false, "连接处于 IgnoreRpcs 状态");
                }

                return Finish(PMRpcReceiveStatus.NotOwner, rpcId, false,
                    "来源连接不是该对象的拥有者连接");
            }

            // ── 解码 + 校验 + 执行（生成物的内部执行器）────────────────────
            try
            {
                entry.Invoke(target, reader);
            }
            catch (PMNetRpcValidationFailedException ex)
            {
                // 原生校验失败 ⇒ 请求断连（D-R0-45）；三态 Reject 不会走到这里。
                bool routed = RequestDisconnect(source, target, ex.RpcId, ex.MethodName);
                return Finish(PMRpcReceiveStatus.ValidationFailed, rpcId, routed,
                    "原生校验失败 ⇒ 请求断连：" + ex.MethodName);
            }
            catch (FormatException ex)
            {
                // 解码期/尾随字节检查抛出的都是 FormatException（与 PMNetReader 一致）。
                // 关键：这些异常都发生在**调用业务实现之前**，因此实现没有被执行。
                return Finish(PMRpcReceiveStatus.Malformed, rpcId, false, ex.Message);
            }
            catch (InvalidCastException ex)
            {
                // 防御网（**不是**类归属判定 —— 归属已在查表时用 ClassId 定完）：
                // 若 ClassId 与实际工厂产出的运行时类型不一致（注册配置错），
                // 生成执行器里的强制转换会在这里落地，而不是把异常漏给业务实现。
                return Finish(PMRpcReceiveStatus.Malformed, rpcId, false, "对象类型与 RPC 不匹配：" + ex.Message);
            }

            Delivered++;
            return Finish(PMRpcReceiveStatus.Applied, rpcId, false, null);
        }

        /// <summary>按字节投递（供宿主在收到一整个载荷时使用）。</summary>
        public static PMNetRpcReceiveResult Deliver(
            PMNetWorld world,
            PMNetConnection source,
            ushort rpcId,
            uint targetId,
            byte[] payload,
            int offset,
            int count)
        {
            return Deliver(world, source, rpcId, new PMNetId(targetId, false),
                new PMNetReader(payload, offset, count));
        }

        /// <summary>按字节投递（目标身份已是 `PMNetId` 时用这个重载）。</summary>
        public static PMNetRpcReceiveResult Deliver(
            PMNetWorld world,
            PMNetConnection source,
            ushort rpcId,
            PMNetId targetId,
            byte[] payload,
            int offset,
            int count)
        {
            return Deliver(world, source, rpcId, targetId, new PMNetReader(payload, offset, count));
        }

        /// <summary>
        /// 对象拥有者连接判定。
        ///
        /// 两个来源都算"拥有"：
        /// - <see cref="PMNetObject.OwnerConnection"/>：显式绑定（复制层的 OwnerOnly 条件用的也是它）；
        /// - <see cref="PMNetObject.GetNetConnection"/>：UE 的 Owner 链推导
        ///   （`AActor::GetNetConnection`，数据通道用它算 `RepFlags.bNetOwner`）。
        /// 只认其中一个会让"控制器对象的 Owner 链"与"显式绑定"两种合法形态各自失效。
        /// </summary>
        private static bool IsOwnerConnection(PMNetObject target, PMNetConnection source)
        {
            if (target == null || source == null)
            {
                return false;
            }

            if (ReferenceEquals(target.OwnerConnection, source))
            {
                return true;
            }

            return ReferenceEquals(target.GetNetConnection(), source);
        }

        /// <summary>向来源连接请求断连；连接未实现接缝时计入不可路由并告警（不静默）。</summary>
        private static bool RequestDisconnect(PMNetConnection source, PMNetObject target, ushort rpcId, string methodName)
        {
            IPMNetRpcDisconnectTarget sink = source as IPMNetRpcDisconnectTarget;
            if (sink == null)
            {
                UnroutableDisconnectRequests++;
                WarnInternal("需要断连但连接 " + source.GetType().Name
                    + " 未实现 IPMNetRpcDisconnectTarget（RPC " + methodName + "），断连请求被丢弃");
                return false;
            }

            sink.RequestRpcDisconnect(target, rpcId, methodName);
            DisconnectRequests++;
            return true;
        }

        private static PMNetRpcReceiveResult Finish(PMRpcReceiveStatus status, ushort rpcId, bool disconnected, string detail)
        {
            if (status != PMRpcReceiveStatus.Applied)
            {
                if (status == PMRpcReceiveStatus.Malformed)
                {
                    MalformedCount++;
                }
                else if (status != PMRpcReceiveStatus.ValidationFailed)
                {
                    Rejected++;
                }
            }

            PMNetRpcReceiveResult result = new PMNetRpcReceiveResult();
            result.Status = status;
            result.RpcId = rpcId;
            result.DisconnectRequested = disconnected;
            result.Detail = detail;

            Action<PMNetRpcReceiveResult> observer = Observer;
            if (observer != null)
            {
                observer(result);
            }

            return result;
        }

        private static void WarnInternal(string message)
        {
            Action<string> warn = Warn;
            if (warn != null)
            {
                warn(message);
            }
        }
    }
}
