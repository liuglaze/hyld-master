using System;
using System.Collections.Generic;
using System.Threading;
using PMNet;

namespace PMCallspaceCheck
{
    /// <summary>
    /// R1 门禁：RPC callspace 真值表 + 归属校验 + 身份/帧契约。
    ///
    /// 事实来源：D:/hyld-refactor-survey/R0_2_ue_rpc.md §A.3/§A.4（UE 源码 16 分支表）
    /// 与 Docs/plans/net-r0-contract.md 的 D-R0-01/02/20/41/42。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R1 门禁：callspace / 归属校验 / 身份与帧契约 ===");
            Console.WriteLine();

            Section("A. callspace 16 分支（逐分支单点覆盖）", TestBranches);
            Section("B. 常用场景真值表（R0_2 §A.4）", TestScenarios);
            Section("C. 接收侧归属校验（D-R0-42 回归）", TestOwnership);
            Section("D. 身份契约（D-R0-01/02）", TestIdentity);
            Section("E. 帧契约（D-R0-20）", TestFrames);
            Section("F. 时间步契约（D-R0-19）", TestTimeStep);
            Section("G. 全局判定（UEngine::GetGlobalFunctionCallspace）", TestGlobalCallspace);

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _passed + " 项检查，0 项失败");
                return 0;
            }

            Console.WriteLine("  " + _passed + " 项通过，" + _failures.Count + " 项失败：");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("    - " + _failures[i]);
            }
            return 1;
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            body();
            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            if (ok) { _passed++; }
            else { _failures.Add(label); }
            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        // ────────────────────────────────────────────────────────────────
        // 上下文构造器
        // ────────────────────────────────────────────────────────────────

        /// <summary>构造一个"正常的客户端 AP"上下文。</summary>
        private static PMCallspaceContext ClientAp(PMRpcFunctionFlags flags)
        {
            PMCallspaceContext c = new PMCallspaceContext();
            c.NetMode = PMNetMode.Client;
            c.LocalRole = PMNetRole.AutonomousProxy;
            c.RemoteRole = PMNetRole.Authority;
            c.Flags = flags;
            c.HasWorld = true;
            c.HasNetConnection = true;
            c.HasNetOwningPlayer = true;
            c.NetOwningPlayerIsLocal = true;
            c.DriverWorldValid = true;
            return c;
        }

        /// <summary>构造一个"正常的 DS 权威"上下文（有 owning connection）。</summary>
        private static PMCallspaceContext ServerAuth(PMRpcFunctionFlags flags)
        {
            PMCallspaceContext c = new PMCallspaceContext();
            c.NetMode = PMNetMode.DedicatedServer;
            c.LocalRole = PMNetRole.Authority;
            c.RemoteRole = PMNetRole.AutonomousProxy;
            c.Flags = flags;
            c.HasWorld = true;
            c.HasNetConnection = true;
            c.HasNetOwningPlayer = true;
            c.NetOwningPlayerIsLocal = false;
            c.DriverWorldValid = true;
            return c;
        }

        private static bool Is(in PMCallspaceResult r, PMFunctionCallspace expected, int branch)
        {
            return r.Callspace == expected && r.Branch == branch;
        }

        // ────────────────────────────────────────────────────────────────
        // A. 逐分支
        // ────────────────────────────────────────────────────────────────

        private static void TestBranches()
        {
            // #1 编辑器内脚本执行
            PMCallspaceContext c1 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer);
            c1.AllowScriptExecutionInEditor = true;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c1), PMFunctionCallspace.Local, 1),
                  "#1 编辑器内脚本执行 → Local");

            // #2 静态函数 / 无 World → 全局判定。
            //
            // 注意：全局判定**不能靠注入**。早期版本给上下文开了个 `GlobalCallspace` 字段
            // 由用例手工赋值，结果是「测了注入、没测判定」——把 ComputeGlobalCallspace
            // 改成恒返回 Absorbed 都能全绿。现在它由 NetMode + Flags 现场推导，
            // 因此下面这些用例必须用**UE 语义下真实可能**的输入组合。
            //
            // UE `UEngine::GetGlobalFunctionCallspace`：两个函数位都没有 → 恒 Local。
            PMCallspaceContext c2 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer);
            c2.HasWorld = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c2), PMFunctionCallspace.Local, 2),
                  "#2 无 World + 无 AuthorityOnly/Cosmetic → 全局判定 Local");

            // 无 World + Cosmetic + DS → 全局判定 Absorbed
            PMCallspaceContext c2c = ServerAuth(PMRpcFunctionFlags.BlueprintCosmetic);
            c2c.HasWorld = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c2c), PMFunctionCallspace.Absorbed, 2),
                  "#2 无 World + Cosmetic + DS → 全局判定 Absorbed");

            // 无 World + AuthorityOnly + Client → 全局判定 Absorbed
            PMCallspaceContext c2d = ClientAp(PMRpcFunctionFlags.BlueprintAuthorityOnly);
            c2d.HasWorld = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c2d), PMFunctionCallspace.Absorbed, 2),
                  "#2 无 World + AuthorityOnly + Client → 全局判定 Absorbed");

            // #2b 静态函数 → 全局判定
            PMCallspaceContext c2b = ClientAp(PMRpcFunctionFlags.Static);
            Check(Is(PMRpcDispatch.EvaluateCallspace(c2b), PMFunctionCallspace.Local, 2),
                  "#2b FUNC_Static + 无特殊位 → 全局判定 Local");

            // #4 pending kill：永不走 Remote
            PMCallspaceContext c4 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer);
            c4.IsPendingKill = true;
            PMCallspaceResult r4 = PMRpcDispatch.EvaluateCallspace(c4);
            Check(Is(r4, PMFunctionCallspace.Local, 4),
                  "#4 pending kill 的 Server RPC → Local（永不 Remote）");

            // #5 NetRequest
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetRequest)),
                     PMFunctionCallspace.Remote, 5),
                  "#5 FUNC_NetRequest → Remote");

            // #6 NetResponse：有 RpcId / 无 RpcId
            PMCallspaceContext c6a = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetResponse);
            c6a.RpcId = 7;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c6a), PMFunctionCallspace.Local, 6),
                  "#6 FUNC_NetResponse(RpcId>0) → Local");
            PMCallspaceContext c6b = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetResponse);
            c6b.RpcId = 0;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c6b), PMFunctionCallspace.Absorbed, 6),
                  "#6 FUNC_NetResponse(RpcId==0) → Absorbed");

            // #7 Standalone：客户端调 Server RPC 被吞
            PMCallspaceContext c7 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer);
            c7.NetMode = PMNetMode.Standalone;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c7), PMFunctionCallspace.Absorbed, 7),
                  "#7 Standalone + 非权威调 Server → Absorbed");
            PMCallspaceContext c7b = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c7b.NetMode = PMNetMode.Standalone;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c7b), PMFunctionCallspace.Local, 7),
                  "#7 Standalone + 调 Client → Local");

            // #8 DS 调 BlueprintCosmetic
            Check(Is(PMRpcDispatch.EvaluateCallspace(ServerAuth(PMRpcFunctionFlags.BlueprintCosmetic)),
                     PMFunctionCallspace.Absorbed, 8),
                  "#8 DS + BlueprintCosmetic → Absorbed");

            // #9 非网络函数：守恒于基线
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.None)),
                     PMFunctionCallspace.Local, 9),
                  "#9 无 FUNC_Net → 基线 Local");
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.BlueprintAuthorityOnly)),
                     PMFunctionCallspace.Absorbed, 9),
                  "#9 无 FUNC_Net + 非权威 + AuthorityOnly → 基线 Absorbed");

            // #11 Multicast：服务端有/无 RemoteRole
            Check(Is(PMRpcDispatch.EvaluateCallspace(ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetMulticast)),
                     PMFunctionCallspace.Local | PMFunctionCallspace.Remote, 11),
                  "#11 服务端多播 + RemoteRole != None → Local|Remote");
            PMCallspaceContext c11b = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetMulticast);
            c11b.RemoteRole = PMNetRole.None;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c11b), PMFunctionCallspace.Local, 11),
                  "#11 服务端多播 + RemoteRole == None → Local");
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetMulticast)),
                     PMFunctionCallspace.Local, 11),
                  "#11 客户端多播 → Local（不外发）");

            // #12 单向过滤
            Check(Is(PMRpcDispatch.EvaluateCallspace(ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer)),
                     PMFunctionCallspace.Local, 12),
                  "#12 服务端调 Server → Local（不回环）");
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient)),
                     PMFunctionCallspace.Local, 12),
                  "#12 客户端调 Client → Local（不外发）");

            // #13 说明：UE 源码里「接收远端函数」是挂在方向位判定上的 **else-if**，
            // 因此当一个函数**同时带** NetServer 与 NetClient（双向 Remote）时，
            // 它不会被 #13 短路，而是继续走后面的权威可达性判定。
            PMCallspaceContext c13 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer
                                              | PMRpcFunctionFlags.NetClient);
            c13.IsReceivingRemoteFunction = true;
            PMCallspaceResult r13 = PMRpcDispatch.EvaluateCallspace(c13);
            Check(Is(r13, PMFunctionCallspace.Remote, 16),
                  "#13 带方向位 + 客户端 + 非 None 远端角色 → 继续走到 #16 Remote（不被 #13 短路）");

            // #13b #13 的唯一可达形态：带 FUNC_Net 但**没有**任何方向位（双向 Remote 说明符）。
            //
            // 校正一个曾经的误判：本类早些时候以为 #13 完全不可达，事实不是——
            // 无方向位时外层 if 整段跳过，else-if 才会被求值。
            PMCallspaceContext c13b = ServerAuth(PMRpcFunctionFlags.Net);
            c13b.IsReceivingRemoteFunction = true;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c13b), PMFunctionCallspace.Local, 13),
                  "#13b 无方向位 + 接收中 → 命中 #13（13 的唯一可达形态）");

            // #13c 一旦有方向位，#13 就不再参与（外层 if 吃掉），改为走 #12。
            PMCallspaceContext c13c = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer);
            c13c.IsReceivingRemoteFunction = true;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c13c), PMFunctionCallspace.Local, 12),
                  "#13c 有方向位 + 接收中 → 命中 #12 而非 #13（else-if 语义）");

            // #13d 无方向位且不在接收中 → 不停在 #13，继续往后判
            PMCallspaceContext c13d = ServerAuth(PMRpcFunctionFlags.Net);
            c13d.IsReceivingRemoteFunction = false;
            // DS + 有连接 + Driver/World 有效 → #14 两子分支均不命中 → #15 RemoteRole≠None → #16
            Check(Is(PMRpcDispatch.EvaluateCallspace(c13d), PMFunctionCallspace.Remote, 16),
                  "#13d 无方向位 + 非接收中 → 不停在 #13，走到 #16 Remote");

            // #14 权威但无收件人
            PMCallspaceContext c14a = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14a.HasNetConnection = false;
            c14a.HasNetOwningPlayer = false;
            c14a.HasNetOwner = true;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14a), PMFunctionCallspace.Absorbed, 14),
                  "#14-① 权威 + 无连接 + HasNetOwner → Absorbed");

            PMCallspaceContext c14b = ServerAuth(PMRpcFunctionFlags.Net);   // 双向 Remote：无方向位
            c14b.HasNetConnection = false;
            c14b.HasNetOwningPlayer = false;
            c14b.HasNetOwner = false;
            PMCallspaceResult r14b = PMRpcDispatch.EvaluateCallspace(c14b);
            Check(Is(r14b, PMFunctionCallspace.Absorbed, 14) && r14b.LogError,
                  "#14-① 权威 + 双向 Remote + 无 owning player → Error + Absorbed");

            PMCallspaceContext c14c = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14c.HasNetConnection = false;
            c14c.HasNetOwningPlayer = true;      // 有 owning player，只是不在本机
            c14c.NetOwningPlayerIsLocal = false;
            // 注意：owning player 非空时 **不**进入 #14-① 的“本地执行”分支（那要求两者都为空），
            // 而是继续往下走 → #16 Remote。
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14c), PMFunctionCallspace.Remote, 16),
                  "#14-① owning player 非空时不短路 → #16 Remote");

            // 真正的“AI 拥有对象调 Client RPC”路径：连接与 owning player 都为空且无方向位约束
            PMCallspaceContext c14f = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14f.HasNetConnection = false;
            c14f.HasNetOwningPlayer = false;
            c14f.HasNetOwner = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14f), PMFunctionCallspace.Local, 14),
                  "#14-① 权威 + 两者皆无 + 有方向位 → 基线 Local（AI 拥有对象调 Client RPC）");

            PMCallspaceContext c14d = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14d.NetOwningPlayerIsLocal = true;   // 但 HasNetConnection 仍为 true
            // ★ UE 的 ② 嵌在 `if (NetConnection == nullptr)` 内，有连接时 **不可达**：
            //   ① 不成立（有连接）、② 不可达、③ 要求 Driver/World 无效（此处有效）
            //   → 掉出 authority 块 → #15 RemoteRole≠None → #16 Remote。
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14d), PMFunctionCallspace.Remote, 16),
                  "#14-② 有连接时该分支不可达 → #16 Remote（曾被写成无守卫独立 if）");

            // #14-② 的 UE 合法形态：无连接 + owning player 是本机 LocalPlayer
            // （典型：ListenServer 的本地玩家）
            PMCallspaceContext c14g = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14g.HasNetConnection = false;
            c14g.HasNetOwningPlayer = true;
            c14g.NetOwningPlayerIsLocal = true;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14g), PMFunctionCallspace.Local, 14),
                  "#14-② 无连接 + owning player 为本机 LocalPlayer → 基线 Local");

            PMCallspaceContext c14e = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c14e.DriverWorldValid = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c14e), PMFunctionCallspace.Absorbed, 14),
                  "#14-③ 有连接但 Driver/World 无效 → Absorbed");

            // #15 RemoteRole == None
            PMCallspaceContext c15 = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer
                                              | PMRpcFunctionFlags.NetClient);
            c15.RemoteRole = PMNetRole.None;
            PMCallspaceResult r15 = PMRpcDispatch.EvaluateCallspace(c15);
            Check(Is(r15, PMFunctionCallspace.Absorbed, 15) && r15.LogWarning,
                  "#15 RemoteRole == None → Absorbed（客户端侧另有 Warning）");

            // #16 真正外发
            Check(Is(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer)),
                     PMFunctionCallspace.Remote, 16),
                  "#16 客户端 AP 调 Server → Remote");
        }

        // ────────────────────────────────────────────────────────────────
        // B. 场景表
        // ────────────────────────────────────────────────────────────────

        private static void TestScenarios()
        {
            // 客户端（AP）调 Server RPC → Remote
            PMCallspaceResult a = PMRpcDispatch.EvaluateCallspace(ClientAp(
                PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer | PMRpcFunctionFlags.NetReliable));
            Check(a.Callspace == PMFunctionCallspace.Remote && PMRpcDispatch.ShouldSendRemote(a.Callspace)
                  && !PMRpcDispatch.ShouldExecuteLocal(a.Callspace),
                  "场景：AP 调可靠 Server RPC → 只外发、本端不执行");

            // 服务端调 Server RPC → Local，不外发
            PMCallspaceResult b = PMRpcDispatch.EvaluateCallspace(ServerAuth(
                PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer));
            Check(b.Callspace == PMFunctionCallspace.Local && !PMRpcDispatch.ShouldSendRemote(b.Callspace),
                  "场景：服务端调 Server RPC → 本端执行、不外发");

            // 服务端调 Client RPC → Remote（只发给 owning connection）
            PMCallspaceResult c = PMRpcDispatch.EvaluateCallspace(ServerAuth(
                PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient));
            Check(c.Callspace == PMFunctionCallspace.Remote,
                  "场景：服务端调 Client RPC → Remote");

            // 客户端调 Client / Multicast → 都不外发
            Check(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient))
                  .Callspace == PMFunctionCallspace.Local, "场景：客户端调 Client RPC → 只本端");
            Check(PMRpcDispatch.EvaluateCallspace(ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetMulticast))
                  .Callspace == PMFunctionCallspace.Local, "场景：客户端调 Multicast → 只本端");

            // 服务端多播：本端执行 + 外发
            PMCallspaceResult d = PMRpcDispatch.EvaluateCallspace(ServerAuth(
                PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetMulticast));
            Check(PMRpcDispatch.ShouldExecuteLocal(d.Callspace) && PMRpcDispatch.ShouldSendRemote(d.Callspace),
                  "场景：服务端调 Multicast → 本端执行 + 外发");

            // 便捷重载必须与完整判定一致（防止两条路径漂移）
            Check(PMRpcDispatch.EvaluateCallspace(PMNetMode.Client, PMNetRole.AutonomousProxy,
                                                  PMRpcKind.Server, true, false) == PMFunctionCallspace.Remote,
                  "便捷重载：客户端 owner 调 Server → Remote");
            Check(PMRpcDispatch.EvaluateCallspace(PMNetMode.DedicatedServer, PMNetRole.Authority,
                                                  PMRpcKind.Client, true, false) == PMFunctionCallspace.Remote,
                  "便捷重载：DS 调 Client（有 owning connection）→ Remote");
            Check(PMRpcDispatch.EvaluateCallspace(PMNetMode.Client, PMNetRole.AutonomousProxy,
                                                  PMRpcKind.Client, true, false) == PMFunctionCallspace.Local,
                  "便捷重载：客户端调 Client → Local");

            // ★ 回归：`isOwner=false`（无主 / AI 拥有的对象）
            // 曾经把 HasNetConnection 写成 `serverSide || isOwner`，在 DS 上恒为 true，
            // 使这条 UE 合法路径变成 Remote（发给不存在的人、DS 上根本不执行）。
            Check(PMRpcDispatch.EvaluateCallspace(PMNetMode.DedicatedServer, PMNetRole.Authority,
                                                  PMRpcKind.Client, false, false) == PMFunctionCallspace.Local,
                  "★ 回归：DS + 无 owning connection 调 Client → Local（AI 拥有对象，本端执行）");
            Check(PMRpcDispatch.EvaluateCallspace(PMNetMode.DedicatedServer, PMNetRole.Authority,
                                                  PMRpcKind.Multicast, false, false)
                  == (PMFunctionCallspace.Local | PMFunctionCallspace.Remote),
                  "便捷重载：DS 多播（与 owning 无关）→ Local|Remote");

            // #15 服务端侧：Absorbed 但**不**告警（UE 只在 !bIsServer 时打 Warning）。
            // 注意：服务端要能走到 #15，flags 必须**含 NetClient**——否则会在 #12 支 1
            // （`isServer && !NetClient`）就先返回 Local，根本到不了这里。
            PMCallspaceContext c15s = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetClient);
            c15s.RemoteRole = PMNetRole.None;
            PMCallspaceResult r15s = PMRpcDispatch.EvaluateCallspace(c15s);
            Check(Is(r15s, PMFunctionCallspace.Absorbed, 15) && !r15s.LogWarning,
                  "#15 服务端侧 → Absorbed 且不告警");

            // #12 服务端 + 同时带 NetServer|NetClient → 两支都不命中 → 下落至 #16
            PMCallspaceContext c12d = ServerAuth(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.NetServer
                                                 | PMRpcFunctionFlags.NetClient);
            Check(Is(PMRpcDispatch.EvaluateCallspace(c12d), PMFunctionCallspace.Remote, 16),
                  "#12 服务端 + 双方向位 → 两内层 if 均不命中 → #16 Remote");

            // #7 先于 #9 的可观测后果：Standalone + AuthorityOnly 且非网络函数 → Local（**不**回落基线）
            PMCallspaceContext c7c = ClientAp(PMRpcFunctionFlags.BlueprintAuthorityOnly);
            c7c.NetMode = PMNetMode.Standalone;
            Check(Is(PMRpcDispatch.EvaluateCallspace(c7c), PMFunctionCallspace.Local, 7),
                  "#7 Standalone 优先于 #9：非网络 AuthorityOnly → Local（不回落基线 Absorbed）");
        }

        // ────────────────────────────────────────────────────────────────
        // C. 归属校验
        // ────────────────────────────────────────────────────────────────

        private static void TestOwnership()
        {
            // ★ D-R0-42 的核心回归：服务端**只**接受来自拥有者连接的调用
            Check(PMRpcDispatch.ShouldCallRemoteFunction(true, true, false),
                  "服务端 + 拥有者连接 → 允许执行");
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(true, false, false),
                  "★ 回归：服务端 + 非拥有者连接 → 拒绝（旧实现此处错误放行）");
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(true, true, true),
                  "服务端 + ignoreRpcs → 拒绝");
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(true, false, true),
                  "服务端 + 非拥有者 + ignoreRpcs → 拒绝");

            // 客户端侧不额外判归属（能收到即服务端所发）
            Check(PMRpcDispatch.ShouldCallRemoteFunction(false, false, false),
                  "客户端 + 非拥有者 → 允许（UE: !IsServer() 即真）");
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(false, true, true),
                  "客户端 + ignoreRpcs → 拒绝");
        }

        // ────────────────────────────────────────────────────────────────
        // D. 身份
        // ────────────────────────────────────────────────────────────────

        private static void TestIdentity()
        {
            Check(!PMNetId.Invalid.IsValid, "PMNetId.Invalid 不是有效身份");
            Check(new PMNetId(1u, false).IsValid, "PMNetId(1) 有效");

            PMNetIdAllocator alloc = new PMNetIdAllocator();
            HashSet<uint> seen = new HashSet<uint>();
            bool monotonic = true;
            bool noReuse = true;
            uint prev = 0u;
            for (int i = 0; i < 10000; i++)
            {
                PMNetId id = alloc.Allocate((i & 1) == 0);
                if (id.Value <= prev) { monotonic = false; }
                if (!seen.Add(id.Value)) { noReuse = false; }
                prev = id.Value;
                if (!id.IsValid) { noReuse = false; }
            }

            Check(monotonic, "分配器单调递增（10000 次）");
            Check(noReuse, "★ D-R0-02：Id 永不复用（10000 次无重复、无 0 值）");
            Check(alloc.LastAllocated == 10000u, "LastAllocated == 分配次数");

            PMNetId st = new PMNetId(5u, true);
            PMNetId dy = new PMNetId(5u, false);
            Check(st != dy, "静态与动态身份不同（同号不算相等）");
            Check(new PMNetId(5u, true) == st, "相同身份相等");

            PMSession s1 = new PMSession(1u, true);
            PMSession s2 = new PMSession(2u, true);
            Check(s1.Epoch != s2.Epoch, "会话 Epoch 不同");

            // 静态与动态共用同一编号空间（否则“同号不同类”会让两个对象撞号）
            PMNetIdAllocator shared = new PMNetIdAllocator();
            PMNetId st1 = shared.Allocate(true);
            PMNetId dy1 = shared.Allocate(false);
            Check(st1.Value != dy1.Value, "静态与动态对象共用编号空间（不撞号）");

            // 跨线程分配必须显式失败，而不是静默产生重复 Id
            PMNetIdAllocator guarded = new PMNetIdAllocator();
            guarded.Allocate(false);
            bool crossThreadThrew = false;
            Thread t = new Thread(delegate ()
            {
                try { guarded.Allocate(false); }
                catch (InvalidOperationException) { crossThreadThrew = true; }
            });
            t.Start();
            t.Join();
            Check(crossThreadThrew, "★ 跨线程分配必须抛异常（否则 _next++ 会重复发号）");
        }

        // ────────────────────────────────────────────────────────────────
        // E. 帧
        // ────────────────────────────────────────────────────────────────

        private static void TestFrames()
        {
            PMFrameId a = new PMFrameId(PMFrameDomain.Input, 10L);
            PMFrameId b = new PMFrameId(PMFrameDomain.Input, 4L);
            Check((a - b) == 6L, "同命名空间差值正确（10 - 4 = 6）");

            PMFrameId srv = new PMFrameId(PMFrameDomain.AuthorityServer, 10L);
            bool crossThrew = false;
            try { long _ = a - srv; }
            catch (InvalidOperationException) { crossThrew = true; }
            Check(crossThrew, "★ D-R0-20：跨命名空间相减必须抛异常（Input - AuthorityServer）");

            // 顺序比较：同命名空间可用，且比较结果正确
            PMFrameId newer = new PMFrameId(PMFrameDomain.Input, 11L);
            Check(a > b && b < a && a >= b && b <= a, "帧顺序比较：同命名空间可用且结果正确");
            Check(a < newer && newer > a, "帧顺序比较：新增帧更大");
            Check(a <= a && a >= a, "帧顺序比较：自身同时满足 <= 与 >=");

            bool cmpCrossThrew = false;
            try { bool _ = a < srv; }
            catch (InvalidOperationException) { cmpCrossThrew = true; }
            Check(cmpCrossThrew, "★ 跨命名空间顺序比较必须抛异常");

            bool cmpNoneThrew = false;
            try { bool _ = a < PMFrameId.None; }
            catch (InvalidOperationException) { cmpNoneThrew = true; }
            Check(cmpNoneThrew, "★ 与未建立帧做顺序比较必须抛异常");

            bool noneThrew = false;
            try { long _ = a - PMFrameId.None; }
            catch (InvalidOperationException) { noneThrew = true; }
            Check(noneThrew, "★ 与未建立帧（None）相减必须抛异常");

            bool noneLeftThrew = false;
            try { long _ = PMFrameId.None - a; }
            catch (InvalidOperationException) { noneLeftThrew = true; }
            Check(noneLeftThrew, "★ 未建立帧作为左操作数相减必须抛异常");

            Check(!PMFrameId.None.IsValid, "PMFrameId.None 不是有效帧");
            Check(a.IsValid, "构造的帧有效");
            Check(new PMFrameId(PMFrameDomain.SimTime, 123L) == new PMFrameId(PMFrameDomain.SimTime, 123L),
                  "相同帧相等");
            Check(new PMFrameId(PMFrameDomain.SimTime, 123L) != new PMFrameId(PMFrameDomain.Session, 123L),
                  "同值不同命名空间的帧不相等");
        }

        // ────────────────────────────────────────────────────────────────
        // G. 全局判定
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// UE `UEngine::GetGlobalFunctionCallspace` 三条语义：
        /// ① 只看两个函数位，都没有→恒 Local；
        /// ② 判据是 WorldNetMode，不是 LocalRole；
        /// ③ 默认 Local（找不到网络模式也 Local）。
        /// 这段以前完全没有断言，是本门禁最大的空洞。
        /// </summary>
        private static void TestGlobalCallspace()
        {
            PMRpcFunctionFlags plain = PMRpcFunctionFlags.Net | PMRpcFunctionFlags.Static;
            PMRpcFunctionFlags auth = plain | PMRpcFunctionFlags.BlueprintAuthorityOnly;
            PMRpcFunctionFlags cos = plain | PMRpcFunctionFlags.BlueprintCosmetic;

            // ① 两个位都没有 → 恒 Local（与 NetMode 无关）
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.DedicatedServer, plain) == PMFunctionCallspace.Local,
                  "全局：无特殊位 + DS → Local");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.Client, plain) == PMFunctionCallspace.Local,
                  "全局：无特殊位 + Client → Local");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.Standalone, plain) == PMFunctionCallspace.Local,
                  "全局：无特殊位 + Standalone → Local");

            // ② DS + Cosmetic → Absorbed；Client + AuthorityOnly → Absorbed
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.DedicatedServer, cos) == PMFunctionCallspace.Absorbed,
                  "全局：DS + Cosmetic → Absorbed");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.Client, auth) == PMFunctionCallspace.Absorbed,
                  "全局：Client + AuthorityOnly → Absorbed");

            // 交叉不成立：DS + AuthorityOnly → Local；Client + Cosmetic → Local
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.DedicatedServer, auth) == PMFunctionCallspace.Local,
                  "全局：DS + AuthorityOnly → Local（不吞）");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.Client, cos) == PMFunctionCallspace.Local,
                  "全局：Client + Cosmetic → Local（不吞）");

            // ★ ③ Standalone / ListenServer 上 Cosmetic → Local（早前实现写成 `netMode != Client` 会误吞）
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.Standalone, cos) == PMFunctionCallspace.Local,
                  "★ 全局：Standalone + Cosmetic → Local（曾被 `!= Client` 误吞）");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.ListenServer, cos) == PMFunctionCallspace.Local,
                  "★ 全局：ListenServer + Cosmetic → Local（曾被 `!= Client` 误吞）");
            Check(PMRpcDispatch.ComputeGlobalCallspace(PMNetMode.ListenServer, auth) == PMFunctionCallspace.Local,
                  "全局：ListenServer + AuthorityOnly → Local");

            // ★ 判据用 WorldNetMode 而非 LocalRole：同一 NetMode 下不同角色结果必须相同
            // （早前实现用 `LocalRole != Authority` 会在这里分叉）
            PMCallspaceContext hostAp = ClientAp(PMRpcFunctionFlags.BlueprintAuthorityOnly | PMRpcFunctionFlags.Static);
            hostAp.LocalRole = PMNetRole.Authority;      // 客户端上角色为 Authority 的本地对象
            hostAp.HasWorld = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(hostAp), PMFunctionCallspace.Absorbed, 2),
                  "★ 全局判据是 NetMode 而非 LocalRole：Client 上的 Authority 对象仍 Absorbed");

            // 无 World 时恒不因“忘记赋值”变成 Absorbed（旧实现用注入字段，默认 0 = Absorbed）
            PMCallspaceContext noInject = ClientAp(PMRpcFunctionFlags.Net | PMRpcFunctionFlags.Static);
            noInject.HasWorld = false;
            Check(Is(PMRpcDispatch.EvaluateCallspace(noInject), PMFunctionCallspace.Local, 2),
                  "★ 无 World 且无特殊位 → Local（不由调用方注入，无法漏赋值为 Absorbed）");
        }

        // ────────────────────────────────────────────────────────────────
        // F. 时间步
        // ────────────────────────────────────────────────────────────────

        private static void TestTimeStep()
        {
            PMTimeStep ok = new PMTimeStep();
            ok.BaseSimTimeMs = 1000L;
            ok.StepMs = 16f;
            ok.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 10L);
            ok.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, 8L);
            Check(ok.IsWellFormed(), "正常时间步 IsWellFormed");

            PMTimeStep nan = ok;
            nan.StepMs = float.NaN;
            Check(!nan.IsWellFormed(), "NaN 步长不合法");

            PMTimeStep inf = ok;
            inf.StepMs = float.PositiveInfinity;
            Check(!inf.IsWellFormed(), "Inf 步长不合法");

            PMTimeStep neg = ok;
            neg.StepMs = -1f;
            Check(!neg.IsWellFormed(), "负步长不合法");

            PMTimeStep negTime = ok;
            negTime.BaseSimTimeMs = -1L;
            Check(!negTime.IsWellFormed(), "负基准时间不合法");

            PMTimeStep zero = ok;
            zero.StepMs = 0f;
            Check(zero.IsWellFormed(), "0 步长形式合法（由调用方按占位帧语义跳过，见 D-R0-19/R0_1 U1）");
        }
    }
}
