// PMNetWeaverTest 的 P3 **加固夹具**：把三类此前没有覆盖的真实形态放进夹具
//   (1) 运行时与 RPC **同程序集**（Unity / Server 的真实形态：PMNet 核心随业务一起编进同一个 DLL，
//       该 DLL 里天然有 20 处「同名不同类型/不同签名的合法重载」）；
//   (2) **同名多类 RPC**（HardeningAlpha 与 HardeningBeta 都有 ServerAttack / ClientNotify，
//       且都是被编织的 RPC —— 用来证明按 RID 核对后同名不再互相污染）；
//   (3) **非 RPC 的合法重载**（OverloadedMarker 在同一个类型里有多份不同签名）。
//
// 为什么这三类必须单独有夹具：P2 报出的 BLOCKER（`VerifyPdbIntegrity` 用「声明类型名.方法名」当键，
// 把合法重载误判成「PDB 损坏」）在原来的夹具形态下**测不出来** —— 原夹具把 PMNet 运行时编成
// 独立程序集，被编织的 DLL 里没有那些重载。本文件就是把那个形态显式造出来。
//
// 双身份说明：
//   · 本文件**不参与 PMNetWeaverTest 自身（net8.0）的编译**（csproj 里 `<Compile Remove>`），
//     它只会被 %TEMP% 里的临时夹具工程（netstandard2.0 + C# 7.3）连同 Generated/*.g.cs 一起编译；
//   · 因此这里只能用 C# 7.3 / netstandard2.0 的写法，且只引用 PMNet 类型。
//   · 测试侧的链条（造变体、调 CLI、注入负例、核对结果）在 Program.cs 的 `RunHardeningChains`。

using System;
using System.Collections.Generic;
using PMNet;
using HardeningBeta = PMWeave.Other.HardeningAlpha;

namespace PMWeave.Hardening
{
    /// <summary>加固夹具 A：含 RPC 与非 RPC 合法重载。</summary>
    [PMNetworkObject]
    public partial class HardeningAlpha : PMNetObject
    {
        /// <summary>业务体执行计数（把结果交给驱动断言）。</summary>
        public int Hits;

        /// <summary>与 HardeningBeta 同名的上行 RPC。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerAttack(int skillId)
        {
            Hits += skillId + 1;
        }

        private PMRpcValidation ServerAttack_ForceValidate(int skillId)
        {
            return PMRpcValidation.Accept;
        }

        /// <summary>与 HardeningBeta 同名的下行 RPC。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientNotify(int code)
        {
            Hits += code;
        }

        /// <summary>非 RPC 合法重载 #1（同名、不同签名；编译后 sequence point 条数不同）。</summary>
        public int OverloadedMarker(int x)
        {
            int acc = x;
            for (int i = 0; i < 3; i++)
            {
                acc += i;
            }

            return acc;
        }

        /// <summary>非 RPC 合法重载 #2。</summary>
        public string OverloadedMarker(string text)
        {
            return text + "!";
        }

        /// <summary>非 RPC 合法重载 #3。</summary>
        public int OverloadedMarker(int x, int y)
        {
            return x + y;
        }
    }

}
namespace PMWeave.Other
{
    /// <summary>加固夹具 B：与 A 的同名 RPC 刻意重名（跨类型同名）。</summary>
    [PMNetworkObject]
    public partial class HardeningAlpha : PMNetObject
    {
        /// <summary>业务体执行计数。</summary>
        public int Hits;

        /// <summary>与 HardeningAlpha.ServerAttack 同名。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerAttack(int skillId)
        {
            Hits += (skillId * 2) + 3;
        }

        private PMRpcValidation ServerAttack_ForceValidate(int skillId)
        {
            return PMRpcValidation.Accept;
        }

        /// <summary>与 HardeningAlpha.ClientNotify 同名。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientNotify(int code)
        {
            Hits += code * 2;
        }

        /// <summary>非 RPC 合法重载（与 A 同名、签名不同）。</summary>
        public int OverloadedMarker(int x)
        {
            return x - 1;
        }

        /// <summary>非 RPC 合法重载（与 A 同名、签名不同）。</summary>
        public string OverloadedMarker(string text)
        {
            return text;
        }
    }

}
namespace PMWeave.Hardening
{
    /// <summary>
    /// 驱动：在**被加载进来的加固夹具程序集内部**跑断言，只把结论（字符串）交回门禁。
    /// 与 Fixture.cs 的 FixtureDriver 同一手法（门禁跨 ALC 只能传字符串）。
    /// </summary>
    public static class HardeningDriver
    {
        private const uint SessionSeed = 0x73u;

        /// <summary>未编织：两个类的版本都是 0，new 与注册都必须被 guard 拒绝。</summary>
        public static string[] RunPreWeave()
        {
            List<string> lines = new List<string>();

            Add(lines, VersionOf(typeof(HardeningAlpha)) == 0, "hardening-preweave-alpha-version-zero",
                "HardeningAlpha 版本 = " + VersionOf(typeof(HardeningAlpha)));
            Add(lines, VersionOf(typeof(HardeningBeta)) == 0, "hardening-preweave-beta-version-zero",
                "HardeningBeta 版本 = " + VersionOf(typeof(HardeningBeta)));

            Add(lines, NewBlocked(typeof(HardeningAlpha)), "hardening-preweave-alpha-new-blocked",
                "new HardeningAlpha() 必须被实例 gate 拒绝");
            Add(lines, NewBlocked(typeof(HardeningBeta)), "hardening-preweave-beta-new-blocked",
                "new HardeningBeta() 必须被实例 gate 拒绝");

            bool registerBlocked = false;
            string detail;
            PMNetRegistry.Reset();
            try
            {
                PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
                detail = "RegisterAll 竟然成功（未编织程序集必须先被拒绝）";
            }
            catch (InvalidOperationException ex)
            {
                registerBlocked = true;
                detail = "RegisterAll 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                detail = "RegisterAll 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, registerBlocked, "hardening-preweave-register-blocked", detail);
            Add(lines, PMNetRegistry.ClassCount == 0, "hardening-preweave-registry-empty",
                "ClassCount = " + PMNetRegistry.ClassCount);

            AddIds(lines);
            return lines.ToArray();
        }

        /// <summary>已编织：结构 + 注册 + 同名 RPC 各自落到自己的业务体 + 非 RPC 重载未受影响。</summary>
        public static string[] RunWoven()
        {
            List<string> lines = new List<string>();
            string fatal = null;

            try
            {
                RunWovenCore(lines);
            }
            catch (Exception ex)
            {
                fatal = ex.GetType().Name + ": " + ex.Message;
            }

            if (fatal != null)
            {
                lines.Add("FAIL fatal :: " + fatal);
            }

            return lines.ToArray();
        }

        private static void RunWovenCore(List<string> lines)
        {
            Add(lines, VersionOf(typeof(HardeningAlpha)) == 1, "hardening-alpha-version-one",
                "HardeningAlpha 版本 = " + VersionOf(typeof(HardeningAlpha)));
            Add(lines, VersionOf(typeof(HardeningBeta)) == 1, "hardening-beta-version-one",
                "HardeningBeta 版本 = " + VersionOf(typeof(HardeningBeta)));

            Add(lines, HasPrivateBody(typeof(HardeningAlpha)), "hardening-alpha-body",
                "HardeningAlpha.PMNet_RpcBody_ServerAttack " + BodyState(typeof(HardeningAlpha)));
            Add(lines, HasPrivateBody(typeof(HardeningBeta)), "hardening-beta-body",
                "HardeningBeta.PMNet_RpcBody_ServerAttack " + BodyState(typeof(HardeningBeta)));

            PMNetRegistry.Reset();
            PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
            Add(lines, PMNetRegistry.ClassCount == 2 && PMNetRegistry.RpcCount == 4,
                "hardening-register-two-classes",
                "ClassCount=" + PMNetRegistry.ClassCount + " RpcCount=" + PMNetRegistry.RpcCount + "（期望 2 / 4）");

            PMNetRpcEntry entry;
            bool alphaRegistered = PMNetRegistry.TryGetRpc(
                HardeningAlpha.PMGeneratedClassId, HardeningAlpha.PMGeneratedRpcId_ServerAttack, out entry);
            bool betaRegistered = PMNetRegistry.TryGetRpc(
                HardeningBeta.PMGeneratedClassId, HardeningBeta.PMGeneratedRpcId_ServerAttack, out entry);

            Add(lines, alphaRegistered, "hardening-alpha-same-name-rpc-registered",
                "HardeningAlpha.ServerAttack 注册 = " + alphaRegistered);
            Add(lines, betaRegistered, "hardening-beta-same-name-rpc-registered",
                "HardeningBeta.ServerAttack 注册 = " + betaRegistered);

            // 同名 RPC 必须各走各的业务体（同程序集里两个类的 PMNet_RpcBody_ServerAttack 是不同方法）。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            HardeningAlpha alpha = NewServerObject(typeof(HardeningAlpha)) as HardeningAlpha;
            HardeningBeta beta = NewServerObject(typeof(HardeningBeta)) as HardeningBeta;
            if (alpha == null || beta == null)
            {
                throw new InvalidOperationException("加固夹具对象创建失败");
            }

            alpha.ServerAttack(5);
            beta.ServerAttack(5);

            Add(lines, alpha.Hits == 6 && beta.Hits == 13, "hardening-same-name-rpc-own-body",
                "alpha.Hits = " + alpha.Hits + "（期望 6），beta.Hits = " + beta.Hits + "（期望 13）");

            // 非 RPC 合法重载：编织不得影响它们的 IL/行为。
            Add(lines, alpha.OverloadedMarker(1) == 4 && beta.OverloadedMarker(1) == 0,
                "hardening-overload-int",
                "alpha.OverloadedMarker(1) = " + alpha.OverloadedMarker(1) + "（期望 4），beta = "
                + beta.OverloadedMarker(1) + "（期望 0）");
            Add(lines, alpha.OverloadedMarker("x") == "x!" && beta.OverloadedMarker("x") == "x",
                "hardening-overload-string",
                "alpha.OverloadedMarker(\"x\") = " + alpha.OverloadedMarker("x") + "（期望 x!），beta = "
                + beta.OverloadedMarker("x") + "（期望 x）");
            Add(lines, alpha.OverloadedMarker(2, 3) == 5, "hardening-overload-two-args",
                "alpha.OverloadedMarker(2,3) = " + alpha.OverloadedMarker(2, 3) + "（期望 5）");

            // 已编织：guard 正常返回 1（否则 new 也会被自己的 guard 拒绝）。
            Add(lines, RequireOf(typeof(HardeningAlpha)) == 1 && RequireOf(typeof(HardeningBeta)) == 1,
                "hardening-woven-require-returns-one",
                "RequireRpcWeave = " + RequireOf(typeof(HardeningAlpha)) + " / " + RequireOf(typeof(HardeningBeta)));

            lines.Add("HASH PMNetRegistry.ProtocolHash 0x" + PMNetRegistry.ProtocolHash.ToString("X8"));
            AddIds(lines);
        }

        /// <summary>
        /// 实例 gate 被改坏后的运行期行为（hard-gate-disabled 变体专用）：
        ///   · HardeningAlpha 的字段初始化被改成常量 ⇒ **new 不再被拒**（这就是旧实现放过的反例）；
        ///   · HardeningBeta 的 gate 未被改动 ⇒ new 仍被拒（证明反例是局部注入、不是夹具坏了）；
        ///   · 注册入口的 guard 仍在 ⇒ 注册仍被拒（“半防御”：只靠 BuildEntry 卡不住 new）。
        /// </summary>
        public static string[] RunPreWeaveGateDisabled()
        {
            List<string> lines = new List<string>();

            Add(lines, !NewBlocked(typeof(HardeningAlpha)), "hardening-nogate-alpha-new-succeeds",
                "new HardeningAlpha() 未被拒（字段初始化被改成常量 ⇒ 实例 gate 失效）");
            Add(lines, NewBlocked(typeof(HardeningBeta)), "hardening-nogate-beta-new-blocked",
                "new HardeningBeta() 仍被拒（其 gate 未被改动，证明反例是局部注入）");

            bool registerBlocked = false;
            string detail;
            PMNetRegistry.Reset();
            try
            {
                PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
                detail = "RegisterAll 竟然成功";
            }
            catch (InvalidOperationException ex)
            {
                registerBlocked = true;
                detail = "RegisterAll 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                detail = "RegisterAll 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, registerBlocked, "hardening-nogate-register-still-blocked", detail);
            return lines.ToArray();
        }

        // ------------------------------------------------------------------ 工具

        private static void AddIds(List<string> lines)
        {
            lines.Add("ID HardeningAlpha.PMGeneratedClassId " + HardeningAlpha.PMGeneratedClassId);
            lines.Add("ID HardeningAlpha.PMGeneratedRpcId_ServerAttack " + HardeningAlpha.PMGeneratedRpcId_ServerAttack);
            lines.Add("ID HardeningAlpha.PMGeneratedRpcId_ClientNotify " + HardeningAlpha.PMGeneratedRpcId_ClientNotify);
            lines.Add("ID HardeningBeta.PMGeneratedClassId " + HardeningBeta.PMGeneratedClassId);
            lines.Add("ID HardeningBeta.PMGeneratedRpcId_ServerAttack " + HardeningBeta.PMGeneratedRpcId_ServerAttack);
            lines.Add("ID HardeningBeta.PMGeneratedRpcId_ClientNotify " + HardeningBeta.PMGeneratedRpcId_ClientNotify);
        }

        private static int VersionOf(Type type)
        {
            object value = InvokePrivateStatic(type, "PMNet_GetRpcWeaveVersion");
            return value == null ? int.MinValue : (int)value;
        }

        private static int RequireOf(Type type)
        {
            object value = InvokePrivateStatic(type, "PMNet_RequireRpcWeave");
            return value == null ? int.MinValue : (int)value;
        }

        private static object InvokePrivateStatic(Type type, string name)
        {
            System.Reflection.MethodInfo method = type.GetMethod(
                name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                return null;
            }

            return method.Invoke(null, null);
        }

        private static bool NewBlocked(Type type)
        {
            try
            {
                Activator.CreateInstance(type);
                return false;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                return ex.InnerException is InvalidOperationException;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static bool HasPrivateBody(Type type)
        {
            System.Reflection.MethodInfo body = type.GetMethod(
                "PMNet_RpcBody_ServerAttack",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return body != null && !body.IsPublic;
        }

        private static string BodyState(Type type)
        {
            System.Reflection.MethodInfo body = type.GetMethod(
                "PMNet_RpcBody_ServerAttack",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return body == null ? "<缺失>" : (body.IsPublic ? "是 public（不符）" : "是 private");
        }

        private static PMNetObject NewServerObject(Type type)
        {
            uint classId = type == typeof(HardeningAlpha)
                ? HardeningAlpha.PMGeneratedClassId
                : HardeningBeta.PMGeneratedClassId;

            PMNetWorld world = new PMNetWorld(new PMSession(SessionSeed, true));
            world.RegisterClass(classId, delegate () { return (PMNetObject)Activator.CreateInstance(type); });

            PMNetObject obj = (PMNetObject)Activator.CreateInstance(type);
            if (!world.Spawn(obj, classId))
            {
                throw new InvalidOperationException("加固夹具对象 Spawn 失败（" + type.Name + "）");
            }

            TestConn connection = new TestConn(910, true);
            world.AddConnection(connection);
            obj.OwnerConnection = connection;
            return obj;
        }

        private static void Add(List<string> lines, bool ok, string name, string detail)
        {
            lines.Add((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }

        /// <summary>简易连接替身（与 Fixture.cs 的 TestConn 同一手法；这里只需要 IsReady/Send）。</summary>
        private sealed class TestConn : PMNetConnection
        {
            public TestConn(int connectionId, bool serverSide)
                : base(connectionId, serverSide)
            {
            }

            public override bool IsReady
            {
                get { return true; }
            }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
            }
        }
    }
}
