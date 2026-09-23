// PMNetWeaverTest 的声明夹具（也充当“被编织的程序集”的驱动）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md
//   - 业务只写「带标记的普通方法 + 直接写业务体」，调用普通名；
//   - 生成物提供 private `PMNet_<M>`（发送 helper）与 `PMNet_RpcInvoke_<M>`（收包 helper）；
//   - 编织器把业务体搬进 private `PMNet_RpcBody_<M>`，把两个 helper 的唯一调用改指过去，
//     并把原方法 M 变成 `this+参数 → call PMNet_<M> → ret`。
//
// 本文件同时是 PMNetGen 的声明输入（--decl-gen）与临时夹具工程的源文件之一。
// 它被本仓库的**生产源码树之外**的临时工程编译（见 PMNetWeaverTest/Program.cs），
// 因此不会污染 Client/Assets 的 Generated，也不需要 Unity。
//
// 刻意把下列形态都放进**业务体**里，用来证明“克隆业务体”不是只搬了几条指令：
//   数组参数 / for 循环 / switch 跳转表 / try-catch-finally / 委托（闭包）+ 局部变量 / string 参数。
//
// 语言面：C# 7.3（Unity 2019.4 上限）；不引用 Unity，不引用任何第三方包。

using System;
using System.Collections.Generic;
using PMNet;

namespace PMWeave.Fixture
{
    /// <summary>被编织的网络对象（业务侧只写普通方法与普通调用）。</summary>
    [PMNetworkObject]
    public partial class WeaveFixture : PMNetObject
    {
        // ---------------------------------------------------------------- 业务体可观察状态

        /// <summary>`ServerAttack` 的业务体被执行了几次。</summary>
        public int AttackCount;

        /// <summary>`ServerAttack` 最近一次收到的 skillId。</summary>
        public int AttackSkillId = int.MinValue;

        /// <summary>`ServerProbe` 的业务体被执行了几次。</summary>
        public int ProbeCount;

        /// <summary>`ServerProbe` 最近一次收到的 token。</summary>
        public int ProbeToken = int.MinValue;

        /// <summary>`ServerWarp` 的业务体被执行了几次（非法校验值必须失败关闭）。</summary>
        public int WarpCount;

        /// <summary>`ClientNotify` 的业务体被执行了几次。</summary>
        public int NotifyCount;

        /// <summary>`ClientNotify` 最近一次收到的 code。</summary>
        public int NotifyCode = int.MinValue;

        /// <summary>`MulticastPush` 的业务体被执行了几次。</summary>
        public int PushCount;

        /// <summary>`MulticastPush` 业务体看到的首元素（null 记 -1）。</summary>
        public float PushFirstSeen = float.NaN;

        /// <summary>`MulticastPush` 业务体看到的数组长度（null 记 -1）。</summary>
        public int PushLengthSeen = int.MinValue;

        /// <summary>`ServerBranch` 的业务体被执行了几次。</summary>
        public int BranchCount;

        /// <summary>`ServerBranch` 业务体算出的结果。</summary>
        public int BranchResult = int.MinValue;

        /// <summary>`ServerBranch` 的 finally 执行了几次（证明异常处理区间被正确搬移）。</summary>
        public int BranchFinallyRuns;

        /// <summary>`ServerBranch` 里委托（闭包）算出的结果。</summary>
        public int BranchLambdaResult = int.MinValue;
        public bool BranchLockHeld;

        /// <summary>`ServerShout` 的业务体被执行了几次。</summary>
        public int ShoutCount;

        /// <summary>`ServerShout` 最近一次收到的字符串。</summary>
        public string ShoutMessage;

        /// <summary>业务体捕获到的异常消息（证明 catch 区域被正确搬移）。</summary>
        public string BranchCaught;

        // ---------------------------------------------------------------- RPC 声明

        /// <summary>
        /// 上行 RPC：三态校验（Reject / Report / Accept）。
        /// 业务体刻意包含 for 循环与 switch 跳转表。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerAttack(int skillId)
        {
            int acc = 0;
            for (int i = 0; i < 3; i++)
            {
                acc += i;
            }

            switch (skillId)
            {
                case 0:
                    acc += 100;
                    break;
                case 7:
                    acc += 200;
                    break;
                default:
                    acc += 1;
                    break;
            }

            AttackCount++;
            AttackSkillId = skillId;
            BranchResult = acc;
        }

        /// <summary>三态校验同伴（规则 13）：负数 ⇒ Reject、7 ⇒ Report、其余 ⇒ Accept。</summary>
        private PMRpcValidation ServerAttack_ForceValidate(int skillId)
        {
            if (skillId < 0)
            {
                return PMRpcValidation.Reject;
            }

            if (skillId == 7)
            {
                return PMRpcValidation.Report;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>上行 RPC：原生 `Validate` 档位（false ⇒ 请求断连）。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void ServerProbe(int token)
        {
            ProbeCount++;
            ProbeToken = token;
        }

        /// <summary>原生校验同伴（规则 13）。</summary>
        private bool ServerProbe_Validate(int token)
        {
            return token >= 0;
        }

        /// <summary>
        /// 上行 RPC：校验结论直接由实参决定，用来验证「非法枚举值必须失败关闭」。
        /// `(PMRpcValidation)99` 是合法 C#（枚举底层是 byte，不校验范围）。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerWarp(int verdict)
        {
            WarpCount++;
        }

        /// <summary>三态校验同伴：原样回传实参。</summary>
        private PMRpcValidation ServerWarp_ForceValidate(int verdict)
        {
            return (PMRpcValidation)verdict;
        }

        /// <summary>下行 RPC（服务端 ⇒ 拥有者客户端）。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientNotify(int code)
        {
            NotifyCount++;
            NotifyCode = code;
        }

        /// <summary>
        /// 多播 RPC + 数组参数：业务体**就地改写**实参数组，
        /// 因此远端必须看到「调用时」的值（调用点快照）。
        /// </summary>
        [PMNetMulticast(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void MulticastPush(float[] values)
        {
            PushCount++;
            PushLengthSeen = values == null ? -1 : values.Length;
            PushFirstSeen = (values == null || values.Length == 0) ? float.NaN : values[0];

            if (values != null && values.Length > 0)
            {
                values[0] = 999f;
            }
        }

        /// <summary>多播三态校验同伴：null ⇒ Reject；首元素为负 ⇒ Report；否则 Accept。</summary>
        private PMRpcValidation MulticastPush_ForceValidate(float[] values)
        {
            if (values == null)
            {
                return PMRpcValidation.Reject;
            }

            if (values.Length > 0 && values[0] < 0f)
            {
                return PMRpcValidation.Report;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>
        /// 上行 RPC：业务体包含 switch / 循环 / try-catch-finally / 委托（闭包）。
        /// 这些形态各自依赖不同的 IL 结构（跳转表、异常处理区间、闭包类与局部变量），
        /// 是「业务体克隆是否真的完整」的核心证据。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized | System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void ServerBranch(int mode, int count)
        {
            BranchLockHeld = System.Threading.Monitor.IsEntered(this);
            int acc = 0;

            for (int i = 0; i < count; i++)
            {
                acc += i;
            }

            switch (mode)
            {
                case 0:
                    acc += 10;
                    break;
                case 1:
                    acc += 20;
                    break;
                case 2:
                    acc += 30;
                    break;
                default:
                    acc -= 1;
                    break;
            }

            try
            {
                if (count < 0)
                {
                    throw new ArgumentException("count 为负");
                }

                acc += Math.Abs(count);
            }
            catch (ArgumentException ex)
            {
                BranchCaught = ex.Message;
                acc -= 1000;
            }
            finally
            {
                BranchFinallyRuns++;
            }

            // 委托（闭包）：捕获局部变量 acc 与参数 mode。
            Func<int, int> scale = delegate (int value)
            {
                return value * 2 + acc;
            };

            int lambdaResult = scale(mode);

            BranchCount++;
            BranchResult = acc;
            BranchLambdaResult = lambdaResult;
        }

        /// <summary>三态校验同伴：恒定 Accept。</summary>
        private PMRpcValidation ServerBranch_ForceValidate(int mode, int count)
        {
            return PMRpcValidation.Accept;
        }

        /// <summary>下行 RPC + string 参数。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ServerShout(string message)
        {
            ShoutCount++;
            ShoutMessage = message;
        }
    }

    /// <summary>
    /// 驱动：在**被加载进来的夹具程序集内部**跑完所有断言，只把结论（字符串）交回门禁。
    ///
    /// 为什么驱动要放进夹具程序集：PMNet 运行时被编成独立程序集（PMNet.Runtime.Temp），
    /// 门禁进程与夹具各自加载一份实例会导致类型身份不同（无法跨 ALC 传 PMNet 对象）。
    /// 因此门禁只通过 AssemblyLoadContext 调两个公开入口：RunPreWeave / RunWoven。
    /// </summary>
    public static class FixtureDriver
    {
        private const uint SessionSeed = 0x51u;

        /// <summary>收集到的“当前这次调用发出的 RPC 载荷”（RemoteSender 接线后的同步编码结果）。</summary>
        private static ushort _capturedRpcId;

        private static byte[] _capturedPayload;

        /// <summary>三态校验上报记录（"方法名:结论"）。</summary>
        private static readonly List<string> _reported = new List<string>();

        /// <summary>原生校验失败的断连请求记录。</summary>
        private static readonly List<string> _validateFailed = new List<string>();

        /// <summary>
        /// 未编织形态的行为：`new` 与注册都必须被生成物 guard 拒绝。
        /// （这是契约里「正常 new 实例和注册均拒绝未编织程序集」的负例。）
        /// </summary>
        public static string[] RunPreWeave()
        {
            List<string> lines = new List<string>();

            // 版本方法本身必须可调用，且必须报 0。
            object version = InvokePrivateStatic("PMNet_GetRpcWeaveVersion");
            Add(lines, version != null && (int)version == 0, "preweave-version-zero",
                "PMNet_GetRpcWeaveVersion() = " + (version == null ? "<缺失>" : version.ToString()));

            bool newBlocked = false;
            string newDetail = null;
            try
            {
                WeaveFixture unused = new WeaveFixture();
                newDetail = "new 竟然成功（未编织程序集必须先被拒绝）";
            }
            catch (InvalidOperationException ex)
            {
                newBlocked = true;
                newDetail = "new 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                newDetail = "new 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, newBlocked, "preweave-new-blocked", newDetail);

            PMNetRegistry.Reset();
            bool registerBlocked = false;
            string registerDetail = null;
            try
            {
                PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
                registerDetail = "RegisterAll 竟然成功（未编织程序集必须先被拒绝）";
            }
            catch (InvalidOperationException ex)
            {
                registerBlocked = true;
                registerDetail = "RegisterAll 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                registerDetail = "RegisterAll 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, registerBlocked, "preweave-register-blocked", registerDetail);
            Add(lines, PMNetRegistry.ClassCount == 0, "preweave-registry-empty",
                "ClassCount = " + PMNetRegistry.ClassCount);

            // 未编织 ⇒ 还没有私有业务体；发送 helper 已按冻结格式改成 private。
            System.Reflection.MethodInfo body = typeof(WeaveFixture).GetMethod(
                "PMNet_RpcBody_ServerAttack",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Add(lines, body == null, "preweave-no-body-method", "业务体方法 = " + (body == null ? "<无>" : body.Name));

            System.Reflection.MethodInfo send = typeof(WeaveFixture).GetMethod(
                "PMNet_ServerAttack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Add(lines, send != null && !send.IsPublic, "preweave-send-helper-not-public",
                "PMNet_ServerAttack public = " + (send == null ? "<缺>" : send.IsPublic.ToString()));

            return lines.ToArray();
        }

        /// <summary>已编织形态的行为断言（唯一一次真实跑通全链的地方）。</summary>
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
            // ── 0. 结构面（编织结果必须是冻结格式）──────────────────────────
            object version = InvokePrivateStatic("PMNet_GetRpcWeaveVersion");
            Add(lines, version != null && (int)version == 1, "stamp-is-one",
                "PMNet_GetRpcWeaveVersion() = " + (version == null ? "<缺失>" : version.ToString()));

            object require = InvokePrivateStatic("PMNet_RequireRpcWeave");
            Add(lines, require != null && (int)require == 1, "require-returns-one",
                "PMNet_RequireRpcWeave() = " + (require == null ? "<缺失>" : require.ToString()));

            System.Reflection.MethodInfo bodyMethod = typeof(WeaveFixture).GetMethod(
                "PMNet_RpcBody_ServerAttack",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Add(lines, bodyMethod != null && !bodyMethod.IsPublic, "body-method-private",
                "PMNet_RpcBody_ServerAttack = " + (bodyMethod == null ? "<缺失>" : "private"));

            System.Reflection.MethodInfo sendMethod = typeof(WeaveFixture).GetMethod(
                "PMNet_ServerAttack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Add(lines, sendMethod != null && !sendMethod.IsPublic, "send-helper-private",
                "PMNet_ServerAttack public = " + (sendMethod == null ? "<缺失>" : sendMethod.IsPublic.ToString()));

            Add(lines, bodyMethod != null && bodyMethod.GetCustomAttributes(typeof(PMServerRpcAttribute), false).Length == 0,
                "body-attribute-free", "业务体不带 RPC Attribute");

            // ── 1. 注册表（走 PMNet_BuildEntry → guard）───────────────────────
            PMNetRegistry.Reset();
            PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
            Add(lines, PMNetRegistry.ClassCount == 1 && PMNetRegistry.RpcCount == 7, "register-all",
                "ClassCount=" + PMNetRegistry.ClassCount + " RpcCount=" + PMNetRegistry.RpcCount);

            PMNetRpcEntry entry;
            bool hasAttack = PMNetRegistry.TryGetRpc(WeaveFixture.PMGeneratedClassId, WeaveFixture.PMGeneratedRpcId_ServerAttack, out entry);
            Add(lines, hasAttack && entry.Descriptor.Validator == PMRpcValidator.ForceValidate, "register-validator-forcevalidate",
                "ServerAttack 注册校验档位 = " + (hasAttack ? entry.Descriptor.Validator.ToString() : "<缺>"));

            // 稳定 ID：把常量报给门禁，与生成物源文本逐项比对（编织不得触碰 ID）。
            lines.Add("ID PMGeneratedClassId " + WeaveFixture.PMGeneratedClassId);
            lines.Add("ID PMGeneratedRpcId_ServerAttack " + WeaveFixture.PMGeneratedRpcId_ServerAttack);
            lines.Add("ID PMGeneratedRpcId_ServerProbe " + WeaveFixture.PMGeneratedRpcId_ServerProbe);
            lines.Add("ID PMGeneratedRpcId_ServerWarp " + WeaveFixture.PMGeneratedRpcId_ServerWarp);
            lines.Add("ID PMGeneratedRpcId_ClientNotify " + WeaveFixture.PMGeneratedRpcId_ClientNotify);
            lines.Add("ID PMGeneratedRpcId_MulticastPush " + WeaveFixture.PMGeneratedRpcId_MulticastPush);
            lines.Add("ID PMGeneratedRpcId_ServerBranch " + WeaveFixture.PMGeneratedRpcId_ServerBranch);
            lines.Add("ID PMGeneratedRpcId_ServerShout " + WeaveFixture.PMGeneratedRpcId_ServerShout);
            lines.Add("HASH PMNetRegistry.ProtocolHash 0x" + PMNetRegistry.ProtocolHash.ToString("X8"));
            Add(lines, true, "id-constants", "已上报 8 个稳定 ID 与全局协议摘要");

            InstallValidationSink();

            try
            {
                // ── 2. 本地：普通名调用必须**本端恰好执行一次**且不发包 ──────────
                TestLocalOnce(lines);

                // ── 3. 远端：普通名调用**不得本地执行**，且参数 bytes 正确 ────────
                TestRemoteSendAndReceive(lines);

                // ── 4. 校验路由（ForceValidate 三态 + 非法值 + 原生 Validate）────
                TestValidationRouting(lines);

                // ── 5. 数组参数（调用点快照 / 本地改写 / 远端收到调用时的值）──────
                TestArrayArguments(lines);

                // ── 6. 业务体形态（switch/循环/try-finally/委托/string）──────────
                TestBodyShapes(lines);

                // ── 7. 方法组引用仍指向网络入口 ─────────────────────────────────
                TestDelegateEntry(lines);
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
                PMRpcValidationSink.OnValidateFailed = null;
                PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;
            }
        }

        // ------------------------------------------------------------------ 用例

        private static void TestLocalOnce(List<string> lines)
        {
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            WeaveFixture server = NewServerObject("ServerAttack");

            // ★ 普通名调用：编织后它必须走「M → PMNet_ServerAttack →（callspace=Local）→ PMNet_RpcBody_ServerAttack」。
            server.ServerAttack(5);

            Add(lines, server.AttackCount == 1, "local-once",
                "本地调用后业务体执行次数 = " + server.AttackCount + "（期望 1）");
            Add(lines, server.AttackSkillId == 5, "local-args",
                "业务体收到的 skillId = " + server.AttackSkillId + "（期望 5）");
            Add(lines, PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0, "local-no-remote-send",
                "PendingRpcCount = " + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "（期望 0）");

            // 再来一次，确认不是“只执行一次”的巧合，而是每次调用恰好一次。
            server.ServerAttack(6);
            Add(lines, server.AttackCount == 2, "local-once-repeat",
                "第二次本地调用后业务体执行次数 = " + server.AttackCount + "（期望 2）");
        }

        private static void TestRemoteSendAndReceive(List<string> lines)
        {
            // (a) 队列路径：RemoteSender 未接线 ⇒ 调用进待发队列，本地不执行。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            WeaveFixture client = NewClientObject();
            client.ServerAttack(5);

            Add(lines, client.AttackCount == 0, "remote-not-local",
                "客户端普通调用后本端业务体执行次数 = " + client.AttackCount + "（期望 0）");
            Add(lines, PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1, "remote-enqueued",
                "PendingRpcCount = " + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "（期望 1）");

            PMNet.Generated.PMNetPendingRpc pending;
            byte[] payload = null;
            if (PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out pending))
            {
                PMNetWriter writer = new PMNetWriter(64);
                pending.Write(pending.Target, writer);
                payload = writer.ToArray();
                _capturedRpcId = pending.RpcId;
            }

            Add(lines, payload != null && payload.Length > 0, "remote-payload-encoded",
                payload == null ? "没有取到待发调用" : ("载荷 " + payload.Length + " 字节，RpcId=" + _capturedRpcId));

            if (payload != null)
            {
                PMNetReader reader = new PMNetReader(payload);
                int decoded = reader.ReadInt32();
                Add(lines, decoded == 5, "remote-bytes-match",
                    "载荷解出的第一个参数 = " + decoded + "（期望 5）");
            }
            else
            {
                Add(lines, false, "remote-bytes-match", "载荷缺失，无法解码");
            }

            // (b) 同步编码路径：RemoteSender 接线后必须**同步**编码出同样的参数。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            _capturedPayload = null;
            _capturedRpcId = 0;
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = CaptureRemote;

            WeaveFixture client2 = NewClientObject();
            client2.ServerAttack(9);

            Add(lines, client2.AttackCount == 0 && PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0,
                "remote-sync-encode-not-local",
                "同步编码路径：本端执行 = " + client2.AttackCount + "，队列 = "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount);

            int syncDecoded = int.MinValue;
            if (_capturedPayload != null)
            {
                syncDecoded = new PMNetReader(_capturedPayload).ReadInt32();
            }

            Add(lines, syncDecoded == 9, "remote-sync-bytes-match",
                "同步编码载荷解出的第一个参数 = " + syncDecoded + "（期望 9）");

            // (c) 真实接收链：参数 bytes → PMNetRpcReceive.Deliver → 校验 → 业务体。
            PMNetRpcReceive.ResetStats();

            WeaveFixture server = NewServerObject("ServerAttack");
            TestConn owner = (TestConn)server.OwnerConnection;

            PMNetWriter receiveWriter = new PMNetWriter(64);
            receiveWriter.WriteInt32(11);
            byte[] receivePayload = receiveWriter.ToArray();

            PMNetRpcReceiveResult result = PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, receivePayload, 0, receivePayload.Length);

            Add(lines, result.Applied, "receive-status-applied", "投递结论 = " + result.Status);
            Add(lines, server.AttackCount == 1, "receive-validator-business",
                "收包后业务体执行次数 = " + server.AttackCount + "（期望 1）");
            Add(lines, server.AttackSkillId == 11, "receive-args",
                "收包后业务体收到的 skillId = " + server.AttackSkillId + "（期望 11）");
            Add(lines, PMNetRpcReceive.Delivered == 1, "receive-delivered-counter",
                "PMNetRpcReceive.Delivered = " + PMNetRpcReceive.Delivered);

            // 递归会表现为栈溢出/无限增长；这里再投一次，仍必须是「每次调用一次」。
            PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, receivePayload, 0, receivePayload.Length);
            Add(lines, server.AttackCount == 2 && PMNetRpcReceive.Delivered == 2, "no-recursion",
                "第二次投递后业务体执行次数 = " + server.AttackCount + "，Delivered = " + PMNetRpcReceive.Delivered);

            // 归属：非 Owner 连接必须被拒且不执行。
            TestConn stranger = new TestConn(999, true);
            server.World.AddConnection(stranger);
            PMNetRpcReceiveResult strangerResult = PMNetRpcReceive.Deliver(
                server.World, stranger, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, receivePayload, 0, receivePayload.Length);
            Add(lines, strangerResult.Status == PMRpcReceiveStatus.NotOwner && server.AttackCount == 2,
                "receive-not-owner-rejected", "非 Owner 结论 = " + strangerResult.Status);
        }

        private static void TestValidationRouting(List<string> lines)
        {
            WeaveFixture server = NewServerObject("ServerAttack");
            TestConn owner = (TestConn)server.OwnerConnection;

            // ForceValidate Reject：跳过业务体、**不断连**。
            byte[] negative = EncodeInt32(-3);
            PMNetRpcReceiveResult rejected = PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, negative, 0, negative.Length);

            Add(lines, server.AttackCount == 0 && rejected.Applied, "force-reject-skip",
                "Reject：业务体执行次数 = " + server.AttackCount + "，投递结论 = " + rejected.Status);
            Add(lines, !rejected.DisconnectRequested && owner.DisconnectRequests.Count == 0, "force-reject-no-disconnect",
                "Reject：DisconnectRequested = " + rejected.DisconnectRequested);
            Add(lines, _reported.Count == 1 && _reported[0] == "ServerAttack:Reject", "force-reject-reported",
                "上报记录 = " + Join(_reported));

            // ForceValidate Report：上报后**仍然执行**。
            _reported.Clear();
            byte[] report = EncodeInt32(7);
            PMNetRpcReceiveResult reportedResult = PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, report, 0, report.Length);

            Add(lines, reportedResult.Applied && server.AttackCount == 1, "force-report-executes",
                "Report：业务体执行次数 = " + server.AttackCount + "，投递结论 = " + reportedResult.Status);
            Add(lines, _reported.Count == 1 && _reported[0] == "ServerAttack:Report", "force-report-reported",
                "上报记录 = " + Join(_reported));

            // ForceValidate Accept：静默放行。
            _reported.Clear();
            byte[] accept = EncodeInt32(3);
            PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerAttack, server.NetId, accept, 0, accept.Length);
            Add(lines, server.AttackCount == 2 && _reported.Count == 0, "force-accept-silent",
                "Accept：业务体执行次数 = " + server.AttackCount + "，上报数 = " + _reported.Count);

            // 非法枚举值必须失败关闭（既不上报 Report 也不执行）。
            _reported.Clear();
            byte[] bogus = EncodeInt32(99);
            PMNetRpcReceive.Deliver(
                server.World, owner, WeaveFixture.PMGeneratedRpcId_ServerWarp, server.NetId, bogus, 0, bogus.Length);
            Add(lines, server.WarpCount == 0, "force-invalid-fail-closed",
                "非法校验值：ServerWarp 业务体执行次数 = " + server.WarpCount + "（期望 0）");

            // 原生 Validate：false ⇒ 请求断连且不执行；true ⇒ 执行。
            WeaveFixture probeServer = NewServerObject("ServerProbe");
            TestConn probeOwner = (TestConn)probeServer.OwnerConnection;

            byte[] badToken = EncodeInt32(-1);
            _validateFailed.Clear();
            PMNetRpcReceiveResult validateFailed = PMNetRpcReceive.Deliver(
                probeServer.World, probeOwner, WeaveFixture.PMGeneratedRpcId_ServerProbe, probeServer.NetId, badToken, 0, badToken.Length);

            Add(lines, probeServer.ProbeCount == 0 && validateFailed.Status == PMRpcReceiveStatus.ValidationFailed,
                "native-validate-false-skips",
                "原生校验 false：业务体执行 = " + probeServer.ProbeCount + "，结论 = " + validateFailed.Status);
            Add(lines, validateFailed.DisconnectRequested && probeOwner.DisconnectRequests.Count == 1
                       && probeOwner.DisconnectRequests[0] == "ServerProbe",
                "native-validate-false-disconnect",
                "断连请求 = " + validateFailed.DisconnectRequested + "，" + Join(probeOwner.DisconnectRequests));

            byte[] goodToken = EncodeInt32(4);
            PMNetRpcReceiveResult validateOk = PMNetRpcReceive.Deliver(
                probeServer.World, probeOwner, WeaveFixture.PMGeneratedRpcId_ServerProbe, probeServer.NetId, goodToken, 0, goodToken.Length);
            Add(lines, probeServer.ProbeCount == 1 && probeServer.ProbeToken == 4 && validateOk.Applied,
                "native-validate-true-executes",
                "原生校验 true：业务体执行 = " + probeServer.ProbeCount + "，token = " + probeServer.ProbeToken);
        }

        private static void TestArrayArguments(List<string> lines)
        {
            // 多播在服务端 = Local | Remote：先本地执行、再外发；业务体就地改写实参。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            _capturedPayload = null;
            _capturedRpcId = 0;
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = CaptureRemote;

            WeaveFixture sender = NewServerObject("MulticastPush");
            float[] buffer = new float[] { 1.5f, 2.5f };

            // ★ 普通名调用（多播）
            sender.MulticastPush(buffer);

            Add(lines, sender.PushCount == 1 && Math.Abs(sender.PushFirstSeen - 1.5f) < 1e-6f, "array-body-sees-calltime-value",
                "本地业务体看到的首元素 = " + sender.PushFirstSeen + "（期望 1.5）");
            Add(lines, Math.Abs(buffer[0] - 999f) < 1e-6f, "array-caller-mutated-locally",
                "调用方数组首元素 = " + buffer[0] + "（期望 999：业务体就地改写）");

            int arrayLength = -1;
            float arrayFirst = float.NaN;
            if (_capturedPayload != null)
            {
                PMNetReader reader = new PMNetReader(_capturedPayload);
                arrayLength = reader.ReadSInt32();
                if (arrayLength > 0)
                {
                    arrayFirst = reader.ReadFloat();
                }
            }

            Add(lines, arrayLength == 2 && Math.Abs(arrayFirst - 1.5f) < 1e-6f, "array-remote-snapshot",
                "外发载荷里的数组 = 长度 " + arrayLength + "，首元素 " + arrayFirst + "（期望 2 / 1.5）");

            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            // 把同一份载荷投递给另一台“客户端副本”：它必须收到调用时的值。
            // 注意：Multicast 方向的 RPC 在**服务端世界**会被 PMNetRpcReceive.Deliver 按方向拒绝，
            // 所以对象必须是由生命周期创建出来的非服务端副本。
            byte[] payload = _capturedPayload;
            if (payload != null)
            {
                PMNetWorld clientWorld;
                PMNetConnection clientConn;
                WeaveFixture peer = NewClientReplica("MulticastPush", out clientWorld, out clientConn);

                PMNetRpcReceiveResult delivered = PMNetRpcReceive.Deliver(
                    clientWorld, clientConn, WeaveFixture.PMGeneratedRpcId_MulticastPush, peer.NetId, payload, 0, payload.Length);

                Add(lines, delivered.Applied && peer.PushCount == 1 && Math.Abs(peer.PushFirstSeen - 1.5f) < 1e-6f,
                    "array-remote-peer-receives",
                    "远端副本：结论 = " + delivered.Status + "，执行 = " + peer.PushCount + "，首元素 = " + peer.PushFirstSeen);
            }
            else
            {
                Add(lines, false, "array-remote-peer-receives", "没有捕获到多播载荷");
            }

            // null 数组在收包侧必须被 Reject（跳过实现）。
            _reported.Clear();
            PMNetWriter nullWriter = new PMNetWriter(16);
            nullWriter.WriteSInt32(-1);
            byte[] nullPayload = nullWriter.ToArray();

            PMNetWorld nullWorld;
            PMNetConnection nullConn;
            WeaveFixture nullPeer = NewClientReplica("MulticastPush", out nullWorld, out nullConn);
            PMNetRpcReceiveResult nullResult = PMNetRpcReceive.Deliver(
                nullWorld, nullConn, WeaveFixture.PMGeneratedRpcId_MulticastPush, nullPeer.NetId, nullPayload, 0, nullPayload.Length);

            Add(lines, nullPeer.PushCount == 0 && nullResult.Applied, "array-null-reject-receive",
                "null 数组：业务体执行 = " + nullPeer.PushCount + "，结论 = " + nullResult.Status);
        }

        private static void TestBodyShapes(List<string> lines)
        {
            // 本地路径：switch + 循环 + try-finally + 委托
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            WeaveFixture server = NewServerObject("ServerBranch");
            server.ServerBranch(1, 4);

            // 期望：acc = 0+1+2+3 = 6；case 1 ⇒ +20 = 26；try ⇒ +4 = 30；lambda ⇒ 1*2+30 = 32
            Add(lines, server.BranchResult == 30 && server.BranchLambdaResult == 32 && server.BranchFinallyRuns == 1 && server.BranchLockHeld,
                "body-loop-switch-tryfinally",
                "本地：acc = " + server.BranchResult + "（期望 30），finally 次数 = " + server.BranchFinallyRuns);
            Add(lines, server.BranchLambdaResult == 32, "body-lambda",
                "本地：委托结果 = " + server.BranchLambdaResult + "（期望 32）");
            Add(lines, server.BranchCaught == null, "body-catch-not-taken",
                "本地：未进入 catch（BranchCaught = " + (server.BranchCaught == null ? "<null>" : server.BranchCaught) + "）");

            // 负数计数 ⇒ 走 catch 分支（证明异常处理区间被搬移后仍然正确）
            WeaveFixture catchServer = NewServerObject("ServerBranch");
            catchServer.ServerBranch(2, -5);
            Add(lines, catchServer.BranchCaught != null && catchServer.BranchFinallyRuns == 1,
                "body-catch-taken",
                "负数计数：进入 catch = " + (catchServer.BranchCaught == null ? "<否>" : catchServer.BranchCaught)
                + "，finally 次数 = " + catchServer.BranchFinallyRuns);

            // 远端路径：同样的参数必须得到同样的结果
            byte[] payload = EncodeInt32Pair(1, 4);
            WeaveFixture remotePeer = NewServerObject("ServerBranch");
            TestConn remoteOwner = (TestConn)remotePeer.OwnerConnection;
            PMNetRpcReceiveResult result = PMNetRpcReceive.Deliver(
                remotePeer.World, remoteOwner, WeaveFixture.PMGeneratedRpcId_ServerBranch, remotePeer.NetId, payload, 0, payload.Length);

            Add(lines, result.Applied && remotePeer.BranchResult == 30 && remotePeer.BranchLambdaResult == 32 && remotePeer.BranchLockHeld,
                "body-shapes-remote-match",
                "远端：结论 = " + result.Status + "，acc = " + remotePeer.BranchResult + "，lambda = " + remotePeer.BranchLambdaResult);

            // string 参数的远端路径（Client 方向 ⇒ 必须投给非服务端副本）
            PMNetWriter stringWriter = new PMNetWriter(64);
            stringWriter.WriteStringValue("编织后的字符串");
            byte[] stringPayload = stringWriter.ToArray();

            PMNetWorld clientWorld;
            PMNetConnection clientConn;
            WeaveFixture clientTarget = NewClientReplica("ServerShout", out clientWorld, out clientConn);
            PMNetRpcReceiveResult shoutResult = PMNetRpcReceive.Deliver(
                clientWorld, clientConn, WeaveFixture.PMGeneratedRpcId_ServerShout,
                clientTarget.NetId, stringPayload, 0, stringPayload.Length);

            Add(lines, shoutResult.Applied && clientTarget.ShoutCount == 1 && clientTarget.ShoutMessage == "编织后的字符串",
                "string-param-receive",
                "string 参数：结论 = " + shoutResult.Status + "，收到 = " + (clientTarget.ShoutMessage == null ? "<null>" : clientTarget.ShoutMessage));

            // 下行（Client）RPC：服务端发出 → 客户端副本接收
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = CaptureRemote;
            _capturedPayload = null;
            _capturedRpcId = 0;
            WeaveFixture serverSender = NewServerObject("ClientNotify");
            ConnCarrier carrier = new ConnCarrier();
            carrier.Conn = serverSender.OwnerConnection;
            serverSender.Owner = carrier;
            serverSender.ClientNotify(42);
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            byte[] notifyPayload = _capturedPayload;
            PMFunctionCallspace notifyCallspace = PMRpcDispatch.EvaluateCallspace(
                serverSender.NetMode, serverSender.Role, PMRpcKind.Client, serverSender.GetNetConnection() != null, false);

            if (notifyPayload != null)
            {
                PMNetRpcReceiveResult notifyResult = PMNetRpcReceive.Deliver(
                    clientWorld, clientConn, WeaveFixture.PMGeneratedRpcId_ClientNotify,
                    clientTarget.NetId, notifyPayload, 0, notifyPayload.Length);

                Add(lines, notifyResult.Applied && clientTarget.NotifyCount == 1 && clientTarget.NotifyCode == 42,
                    "client-rpc-receive",
                    "Client 方向：callspace = " + notifyCallspace + "，结论 = " + notifyResult.Status
                    + "，执行 = " + clientTarget.NotifyCount + "，code = " + clientTarget.NotifyCode);
            }
            else
            {
                Add(lines, false, "client-rpc-receive",
                    "没有捕获到 Client RPC 载荷（callspace = " + notifyCallspace
                    + "，NetMode = " + serverSender.NetMode + "，Role = " + serverSender.Role
                    + "，服务端对象本地执行次数 = " + serverSender.NotifyCount
                    + "，RpcId = " + _capturedRpcId
                    + "，队列 = " + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "）");
            }
        }

        private static void TestDelegateEntry(List<string> lines)
        {
            // 方法组引用（委托）仍必须指向"正常网络入口"：客户端调用应当发包、不本地执行。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            PMNet.Generated.PMNetGeneratedRegistry.RemoteSender = null;

            WeaveFixture client = NewClientObject();
            Action<int> call = client.ServerAttack;
            call(13);

            Add(lines, client.AttackCount == 0 && PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1,
                "delegate-entry-still-networked",
                "委托调用：本端执行 = " + client.AttackCount + "，入队 = "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount);

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
        }

        // ------------------------------------------------------------------ 夹具与工具

        private static void InstallValidationSink()
        {
            _reported.Clear();
            _validateFailed.Clear();
            PMRpcValidationSink.OnReported = delegate (PMNetObject target, ushort rpcId, PMRpcValidation verdict, string methodName)
            {
                _reported.Add(methodName + ":" + verdict);
            };
            PMRpcValidationSink.OnValidateFailed = delegate (PMNetObject target, ushort rpcId, string methodName)
            {
                _validateFailed.Add(methodName);
            };
        }

        private static void CaptureRemote(PMNetObject target, ushort rpcId, PMRpcWriter write)
        {
            PMNetWriter writer = new PMNetWriter(256);
            write(target, writer);
            _capturedRpcId = rpcId;
            _capturedPayload = writer.ToArray();
        }

        private static WeaveFixture NewServerObject(string label)
        {
            PMNetWorld world = new PMNetWorld(new PMSession(SessionSeed, true));
            world.RegisterClass(WeaveFixture.PMGeneratedClassId, delegate () { return new WeaveFixture(); });

            WeaveFixture obj = new WeaveFixture();
            if (!world.Spawn(obj, WeaveFixture.PMGeneratedClassId))
            {
                throw new InvalidOperationException("夹具对象 Spawn 失败（" + label + "）");
            }

            TestConn connection = new TestConn(900, true);
            world.AddConnection(connection);
            obj.OwnerConnection = connection;
            return obj;
        }

        /// <summary>
        /// 带连接的载体：UE 的 `GetNetConnection()` 是沿 `Owner` 链推导的，
        /// 而 `OwnerConnection` 字段只用于**收包侧**归属校验。
        ///
        /// 为什么 Client 方向的 RPC 需要它：`PMRpcDispatch.EvaluateCallspace` 的 #14 ① 分支
        /// （AI 拥有的对象调 Client RPC）在“无连接且无 owning player”时**返回基线（Local）**，
        /// 只有对象真的能推导出连接（`GetNetConnection() != null`）时才会走到 #16 变成 Remote。
        /// 这正是 UE 的语义（`AActor::GetFunctionCallspace`），不是本项目自造。
        /// </summary>
        private sealed class ConnCarrier : PMNetObject
        {
            public PMNetConnection Conn;

            public override PMNetConnection GetNetConnection()
            {
                return Conn;
            }
        }

        /// <summary>
        /// 造一个“服务端权威对象 → 客户端副本”的完整链路：
        /// 副本必须由**生命周期消息**创建（客户端世界不允许 Spawn，对象只能由服务端创建）。
        /// Client / Multicast 方向的 RPC 只能投给非服务端副本（否则 PMNetRpcReceive 按方向拒绝）。
        /// </summary>
        private static WeaveFixture NewClientReplica(string label, out PMNetWorld clientWorld, out PMNetConnection clientConn)
        {
            PMNetWorld serverWorld = new PMNetWorld(new PMSession(SessionSeed, true));
            serverWorld.RegisterClass(WeaveFixture.PMGeneratedClassId, delegate () { return new WeaveFixture(); });

            WeaveFixture serverObj = new WeaveFixture();
            if (!serverWorld.Spawn(serverObj, WeaveFixture.PMGeneratedClassId))
            {
                throw new InvalidOperationException("夹具副本的源对象 Spawn 失败（" + label + "）");
            }

            TestConn serverSideConn = new TestConn(903, true);
            serverWorld.AddConnection(serverSideConn);

            clientWorld = new PMNetWorld(new PMSession(SessionSeed, false));
            clientWorld.RegisterClass(WeaveFixture.PMGeneratedClassId, delegate () { return new WeaveFixture(); });
            clientConn = new TestConn(904, false);

            byte[] lifecycle = serverWorld.BuildLifecycleBatch(serverSideConn);
            if (lifecycle != null)
            {
                clientWorld.OnLifecycleMessage(lifecycle, 0, lifecycle.Length);
            }

            PMNetObject replica;
            if (!clientWorld.TryFind(serverObj.NetId, out replica) || replica == null)
            {
                throw new InvalidOperationException("客户端世界没有创建出副本对象（" + label + "）");
            }

            return (WeaveFixture)replica;
        }

        private static WeaveFixture NewClientObject()
        {
            WeaveFixture obj = new WeaveFixture();
            obj.NetMode = PMNetMode.Client;
            obj.Role = PMNetRole.AutonomousProxy;
            return obj;
        }

        private static byte[] EncodeInt32(int value)
        {
            PMNetWriter writer = new PMNetWriter(16);
            writer.WriteInt32(value);
            return writer.ToArray();
        }

        private static byte[] EncodeInt32Pair(int first, int second)
        {
            PMNetWriter writer = new PMNetWriter(32);
            writer.WriteInt32(first);
            writer.WriteInt32(second);
            return writer.ToArray();
        }

        private static object InvokePrivateStatic(string name)
        {
            System.Reflection.MethodInfo method = typeof(WeaveFixture).GetMethod(
                name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                return null;
            }

            return method.Invoke(null, null);
        }

        private static void Add(List<string> lines, bool ok, string name, string detail)
        {
            lines.Add((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }

        private static string Join(List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "<空>";
            }

            string text = string.Empty;
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    text += ",";
                }

                text += values[i];
            }

            return text;
        }

        /// <summary>
        /// 简易连接替身：捕获断连请求（原生校验失败必须真的落到连接上）。
        /// </summary>
        private sealed class TestConn : PMNetConnection, IPMNetRpcDisconnectTarget
        {
            public readonly List<string> DisconnectRequests = new List<string>();

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

            public void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName)
            {
                DisconnectRequests.Add(methodName);
            }
        }
    }
}
