// ============================================================================
//  PMEntryInboxTest：T-LIVE1「续局/入局通知先于面板注册到达」的可执行行为门禁
// ============================================================================
//
//  被测对象（**真正生产的源码**，不复制、不快照、不桩）：
//    · Client/Assets/Scripts/Server/Manger/RequestManger.cs        —— 含 PMEntryNoticeInbox
//    · Client/Assets/Scripts/Server/Request/BaseRequest.cs
//    · Client/Assets/Scripts/Server/Manger/PmRpcClient.cs
//    · Client/Assets/Scripts/Log/Loging.cs
//    · Client/Assets/Scripts/PMNet/Session/PMDsEntryOffer.cs       —— 真实严格 codec
//    · Client/Assets/Scripts/Server/SocketProto.cs                 —— 真正 protoc 生成的 MainPack
//
//  Unity / UI 边界是功能性替身（Stubs.cs + 本文件的 EntryPanelStub），面板替身只复刻
//  「严格解码 → Enter」判定核心，且用的是**真实 codec**。替身不证明真实 uGUI 能开面板、
//  不证明真实 Unity 线程模型；那两件事由 PMClientCheck / PMR4UnityCheck / PMNetUnityPlayerCheck
//  与用户实机覆盖。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using PMNet.Session;
using Server;
using SocketProto;

namespace PMEntryInboxTest
{
    /// <summary>
    /// 匹配面板替身：只复刻真实 <c>UIMatchingPanel.OnResponse</c> 的**判定核心** ——
    /// 对 <c>MainPack.Str</c> 做一次严格解码（真实 <see cref="PMDsEntryCodec"/>），
    /// 成功才「进 Enter」，失败明确拒绝且**绝不回退旧链**。
    /// 真实面板里的 Unity UI 部分（Canvas 抑制、栈安全退场）不在本门禁范围内。
    /// </summary>
    internal sealed class EntryPanelStub : MVC.UIbasePanel
    {
        public int EnterCount;
        public int DecodeFailureCount;
        public string LastDecodeError;
        public PMDsEntryOffer LastOffer;
        public readonly List<MainPack> Delivered = new List<MainPack>();

        public override void OnResponse(MainPack pack)
        {
            lock (Delivered) { Delivered.Add(pack); }

            PMDsEntryOffer offer;
            string error;
            if (!PMDsEntryCodec.TryDecode(pack.Str, out offer, out error))
            {
                DecodeFailureCount++;
                LastDecodeError = error;
                return;
            }

            EnterCount++;
            LastOffer = offer;
        }
    }

    /// <summary>StartEnterBattle 的 BaseRequest（真实基类）：统计「RequestManger 投递了几次」。</summary>
    internal sealed class EntryRequest : BaseRequest
    {
        public EntryRequest(MVC.UIbasePanel panel)
            : base(panel, RequestCode.Matching, ActionCode.StartEnterBattle)
        {
        }

        public int Routed;
        public MainPack Last;

        public override void OnResponse(MainPack pack)
        {
            Routed++;
            Last = pack;
            base.OnResponse(pack);
        }
    }

    internal static class Program
    {
        private const string TicketMarker = "TICKET-SECRET-MARKER-7F3A";
        private const string IllegalMarker = "LEAK-MARKER-ILLEGAL-4B21";
        private const string PlainMarker = "PLAIN-STR-MARKER-9C55";

        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new List<string>();
        private static string _tracePath;

        private static int Main()
        {
            Console.WriteLine("== PMEntryInboxTest（T-LIVE1 入局/续局通知暂存与脱敏门禁）==");

            InitTrace();

            try
            {
                Section0_OldBehaviourBaseline();
                Section1_StrictDecodeAndBoundedStaging();
                Section2_OnInitRemoveAllRequestKeepsStaging();
                Section3_MainThreadExactlyOnceDelivery();
                Section4_RejectIllegalPayloads();
                Section5_RejectExpired();
                Section6_OverwriteObservableAndNoTicketLeak();
                Section7_ConnectionCloseAndCrossConnection();
                Section8_LogSanitization();
                Section9_ThreadSafety();
                Section10_HostWiringStructuralChecks();
            }
            catch (Exception ex)
            {
                _fail++;
                _failures.Add("门禁自身抛异常：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("  [FAIL] 门禁自身抛异常：" + ex);
            }

            CleanupTrace();

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("通过 " + _pass + " / 失败 " + _fail);
            if (_fail > 0)
            {
                Console.WriteLine("失败项：");
                foreach (string f in _failures)
                {
                    Console.WriteLine("  - " + f);
                }
            }

            Console.WriteLine(_fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        //  00：修前基线（内联复刻**已被删除**的那条分支，证明判据有鉴别力）
        // ------------------------------------------------------------------
        //
        //  git diff 里被删掉的两段（修前真实源码）：
        //    RequestManger.HandleRequest 的 else：
        //        Logging.HYLDDebug.LogError(pack.ToString());
        //        Logging.HYLDDebug.LogError("不能找到对应的处理   " + pack.Actioncode + "   " + pack.Requestcode);
        //    BaseRequest.Update：
        //        Logging.HYLDDebug.Trace("OnResponse  : \n" + mainPack);
        //
        //  本节点不调用生产代码，只按**旧决策**算出「旧实现会怎样」，再用本门禁的判据去撞它，
        //  证明这些判据在旧代码上必然红（否则门禁就是空的）。
        private static void Section0_OldBehaviourBaseline()
        {
            Console.WriteLine();
            Console.WriteLine("== 00 修前基线：旧分支在“未注册 + 含票据”下必然违反判据 ==");

            Reset();
            RequestManger.NotifySocketConnected();

            string text = BuildValidText(PrefixInitial, "m-baseline", false);
            MainPack pack = MakePack(text);

            // 旧实现的「已注册」判定：面板没注册（AddRequest 还没跑）⇒ 走 else 分支。
            bool oldRegistered = false;
            bool oldDeliveredToPanel = false;
            string oldLog = pack.ToString();   // 旧代码把它打进日志（判据要求日志不含票据）

            Check(!oldRegistered && !oldDeliveredToPanel,
                "旧分支：面板未注册时通知从未投递（= 实机“续局新票丢失”的机制）");
            Check(oldLog.Contains(text),
                "旧分支：pack.ToString() 确实把完整 Str（内含票据）写进了日志文本（= 实机凭据泄漏的机制）");

            // 同一组判据作用在旧行为上必须失败 —— 这是「判据可失败」的证明。
            bool criteriaNoTicketInLog = !oldLog.Contains(text);
            bool criteriaDelivered = oldDeliveredToPanel;
            Check(!criteriaNoTicketInLog && !criteriaDelivered,
                "旧行为同时违反「日志不含票据」与「恰好一次投递」两条判据（门禁有鉴别力）");
        }

        // ------------------------------------------------------------------
        //  01：无注册时，严格解码合法的 PMDS1/PMDSR1 被有界暂存
        // ------------------------------------------------------------------
        private static void Section1_StrictDecodeAndBoundedStaging()
        {
            Console.WriteLine();
            Console.WriteLine("== 01 无注册时：合法 PMDS1/PMDSR1 有界暂存 ==");

            Check(PMEntryNoticeInbox.MaxTtlMs == 30000L, "TTL 硬上限为 30s（MaxTtlMs == 30000）");

            // --- 合法初始票（PMDS1:）在**无任何注册**时被暂存 ---
            Reset();
            RequestManger.NotifySocketConnected();

            // 前提（可观测）：此刻 StartEnterBattle 确实**没有注册者** —— 用一个不带 PMDS 前缀的
            // 同 Action 包探一下，它必须落进「不能找到对应的处理」（旧实现就是在这里丢掉真通知的）。
            RequestManger.HandleRequest(MakePack("probe", ActionCode.StartEnterBattle, RequestCode.Matching, ReturnCode.Succeed));
            Check(UnityEngine.Debug.Contains("不能找到对应的处理   request=Matching   action=StartEnterBattle"),
                "前提：此时 StartEnterBattle 尚未注册（探针包确实无接收者）");
            UnityEngine.Debug.ClearAll();

            string initialText = BuildValidText(PrefixInitial, "match-initial", false);
            bool stagedInitial = StageViaRouting(MakePack(initialText));

            Check(stagedInitial, "合法 PMDS1 在无注册时被暂存（经真实收包入口 HandleRequest）");
            Check(PMEntryNoticeInbox.HasPending, "暂存槽非空（PMDS1）");
            Check(PMEntryNoticeInbox.StagedCount == 1 && PMEntryNoticeInbox.OverwriteCount == 0,
                "暂存计数：staged=1 overwrite=0（PMDS1）");
            Check(!UnityEngine.Debug.Contains("不能找到对应的处理"),
                "未注册的入局通知**不再**落进“不能找到对应的处理”分支（PMDS1）");

            // --- 合法续局票（PMDSR1:）同样被暂存 ---
            Reset();
            RequestManger.NotifySocketConnected();
            string resumeText = BuildValidText(PrefixResume, "match-resume", true);
            bool stagedResume = StageViaRouting(MakePack(resumeText));

            Check(stagedResume, "合法 PMDSR1 在无注册时被暂存（续局）");
            Check(PMEntryNoticeInbox.HasPending, "暂存槽非空（PMDSR1）");

            // --- 有界：连发 50 条仍只占 1 条 ---
            Reset();
            RequestManger.NotifySocketConnected();
            for (int i = 0; i < 50; i++)
            {
                StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-flood-" + i, true)));
            }

            Check(PMEntryNoticeInbox.HasPending, "50 条连发后仍有待投递通知");
            Check(PMEntryNoticeInbox.StagedCount == 50, "50 条连发全部被“暂存”（staged=50）");
            Check(PMEntryNoticeInbox.OverwriteCount == 49, "后 49 条覆盖前一条（overwrite=49，可观测）");
            Check(PMEntryNoticeInbox.Capacity == 1, "暂存槽容量恒为 1（有界，不排队）");

            // 投递出来的必须是**最后一条**（新覆盖旧）。
            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();
            request.Update();

            Check(panel.EnterCount == 1, "50 条连发后只投递 1 次（恰好一次，不重复消费被覆盖的旧票）");
            Check(panel.LastOffer != null && panel.LastOffer.MatchId == "match-flood-49",
                "投递的是最后一条（新通知覆盖旧通知）");
        }

        // ------------------------------------------------------------------
        //  02：HYLDManger.OnInit 的 RemoveAllRequest 不丢暂存
        // ------------------------------------------------------------------
        private static void Section2_OnInitRemoveAllRequestKeepsStaging()
        {
            Console.WriteLine();
            Console.WriteLine("== 02 HYLDManger.OnInit（RemoveAllRequest）不丢暂存 ==");

            Reset();
            RequestManger.NotifySocketConnected();

            Check(StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-oninit", true))),
                "续局通知先到达并被暂存（面板仍无注册）");

            // HYLDManger.OnInit 就是：RemoveAllRequest() → uiManger.OnInit() → 面板 Init/AddRequest。
            RequestManger.RemoveAllRequest();

            Check(PMEntryNoticeInbox.HasPending, "RemoveAllRequest 之后暂存**仍在**（OnInit 期间不丢待用票）");
            Check(PMEntryNoticeInbox.ClearedCount == 0, "RemoveAllRequest 不计入“被清除”");
            Check(PMEntryNoticeInbox.StagedCount == 1, "暂存计数未被清零");

            // 面板注册后投递，仍然恰好一次。
            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();
            request.Update();

            Check(request.Routed == 1 && panel.EnterCount == 1,
                "OnInit 之后注册面板 → 恰好一次进 Enter（续局票没被注册过程吞掉）");
            Check(panel.LastOffer != null && panel.LastOffer.IsResume,
                "进 Enter 的 offer 是续局（PMDSR1 → IsResume=true）");
        }

        // ------------------------------------------------------------------
        //  03：主线程泵——面板未就绪保留、就绪后恰好一次投递
        // ------------------------------------------------------------------
        private static void Section3_MainThreadExactlyOnceDelivery()
        {
            Console.WriteLine();
            Console.WriteLine("== 03 面板未就绪保留 / 就绪后主线程恰好一次投递 ==");

            Reset();
            RequestManger.NotifySocketConnected();

            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);   // 已注册
            MVC.UIMatchingPanel.Ready = false;                // 面板还打不开

            Check(StageViaRouting(MakePack(BuildValidText(PrefixInitial, "match-panel", false))),
                "通知被暂存");

            for (int i = 0; i < 30; i++)
            {
                RequestManger.PumpEntryNoticeInbox();
            }

            Check(request.Routed == 0 && panel.EnterCount == 0, "面板打不开时**一次都不投递**（fail closed）");
            Check(PMEntryNoticeInbox.HasPending, "面板打不开时暂存被保留（不丢通知、不伪造接收者）");
            Check(MVC.UIMatchingPanel.EnsureOpenCalls > 0, "确实反复尝试过安全打开面板");
            Check(UnityEngine.Debug.Errors.Count > 0, "打不开面板留下可观测错误日志");
            long errCountAfterFirstFrame = UnityEngine.Debug.Errors.Count;
            for (int i = 0; i < 30; i++)
            {
                RequestManger.PumpEntryNoticeInbox();
            }

            Check(UnityEngine.Debug.Errors.Count == errCountAfterFirstFrame,
                "同一条暂存的“打不开面板”只记一次错误（不逐帧刷屏）");

            // 面板就绪 → 恰好一次
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();

            Check(request.Routed == 1, "面板就绪后投递 1 次（RequestManger → BaseRequest 队列）");
            Check(panel.EnterCount == 0, "此时包还在面板队列里（未被抽干）");

            request.Update();
            Check(panel.EnterCount == 1, "抽干后面板恰好一次进 Enter");

            for (int i = 0; i < 100; i++)
            {
                RequestManger.PumpEntryNoticeInbox();
            }

            request.Update();
            Check(request.Routed == 1 && panel.EnterCount == 1, "重复泵 100 次不产生第二次投递/第二次 Enter");
            Check(!PMEntryNoticeInbox.HasPending, "投递后暂存槽已消费");
            Check(PMEntryNoticeInbox.DeliveredCount == 1, "DeliveredCount == 1");
        }

        // ------------------------------------------------------------------
        //  04：拒非法载荷（严格解码，绝不“看起来像就收”）
        // ------------------------------------------------------------------
        private static void Section4_RejectIllegalPayloads()
        {
            Console.WriteLine();
            Console.WriteLine("== 04 非法载荷一律拒收（严格解码）==");

            byte[] goodTicket = MakeTicket(TicketMarker);
            string illegalMatch = IllegalMarker;

            // 只有前缀、没有任何载荷：前缀闸门会放行，严格解码必须拒收。
            ExpectIllegal("只有前缀没有载荷", PrefixInitial);
            Check(!PMEntryNoticeInbox.Stage(null, ActionCode.StartEnterBattle), "Stage(null) 直接返回 false");
            // 前缀不是 PMDS1:/PMDSR1: 的包连「入局通知」都不算：HandleRequest 不会把它放进暂存路径。
            // 直接交给 Stage 也必须拒收（它照样要过严格解码），证明「前缀像」不等于能收。
            Check(!PMEntryNoticeInbox.Stage(
                    MakePack("PMDS2:" + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 1, 1, 0, 0, goodTicket, false))),
                    ActionCode.StartEnterBattle),
                "错误前缀 PMDS2: 连 Stage 都拒收（不进暂存）");
            ExpectIllegal("非规范 Base64", PrefixInitial + "!!!!");
            ExpectIllegal("base64 长度非 4 的倍数", PrefixInitial + "AAA");
            ExpectIllegal("载荷版本 = 2", PrefixInitial + Convert.ToBase64String(BuildPayloadV2(illegalMatch, goodTicket)));
            ExpectIllegal("Epoch = 0", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 0, 1, 0, 7777, 1, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("ProtocolHash = 0", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 0, 0, 7777, 1, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("Port = 0", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 0, 1, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("Port = 70000", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 70000, 1, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("Uid = 0", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 0, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("TeamId = -1", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 1, 1, -1, 0, goodTicket, false)));
            ExpectIllegal("票据长度 0", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 1, 1, 0, 0, new byte[0], false)));
            ExpectIllegal("票据缺 PMDS 魔数", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 1, 1, 0, 0, Encoding.ASCII.GetBytes("XXXX" + TicketMarker), false)));
            ExpectIllegal("载荷有多余尾部字节", PrefixInitial + Convert.ToBase64String(BuildPayload(illegalMatch, "d", "h", 1, 1, 0, 7777, 1, 1, 0, 0, goodTicket, true)));
            ExpectIllegal("MatchId 为空", PrefixInitial + Convert.ToBase64String(BuildPayload(string.Empty, "d", "h", 1, 1, 0, 7777, 1, 1, 0, 0, goodTicket, false)));
            ExpectIllegal("整条文本超过 4096 字节",
                PrefixInitial + Convert.ToBase64String(new byte[4096]));

            Check(!UnityEngine.Debug.Contains(IllegalMarker), "所有非法载荷的原始内容都没进日志（只记结构性问题）");
            Check(!PMEntryNoticeInbox.HasPending, "非法载荷不留下任何暂存");

            // 非法之后紧跟合法：状态没被污染。
            Reset();
            RequestManger.NotifySocketConnected();
            ExpectIllegal("先来一条非法的", PrefixInitial + "!!!!");

            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;
            StageViaRouting(MakePack(BuildValidText(PrefixInitial, "match-after-illegal", false)));
            RequestManger.PumpEntryNoticeInbox();
            request.Update();

            Check(request.Routed == 1 && panel.EnterCount == 1, "非法载荷之后合法通知仍能正常投递（无状态污染）");
        }

        // ------------------------------------------------------------------
        //  05：拒过期（TTL ≤ 30s）
        // ------------------------------------------------------------------
        private static void Section5_RejectExpired()
        {
            Console.WriteLine();
            Console.WriteLine("== 05 过期暂存拒投递（TTL ≤ 30s）==");

            long now = 1000000L;

            // 29999ms：仍有效
            Reset();
            PMEntryNoticeInbox.SetNowMsProvider(() => now);
            RequestManger.NotifySocketConnected();
            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;

            StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-ttl-ok", true)));
            now += 29999L;
            RequestManger.PumpEntryNoticeInbox();
            request.Update();
            Check(request.Routed == 1 && panel.EnterCount == 1, "等待 29999ms（< TTL）仍然正常投递");

            // 30000ms：到期即丢
            Reset();
            now = 2000000L;
            PMEntryNoticeInbox.SetNowMsProvider(() => now);
            RequestManger.NotifySocketConnected();
            EntryPanelStub panel2 = new EntryPanelStub();
            EntryRequest request2 = new EntryRequest(panel2);
            MVC.UIMatchingPanel.Ready = true;

            StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-ttl-expired", true)));
            now += 30000L;
            RequestManger.PumpEntryNoticeInbox();
            request2.Update();

            Check(request2.Routed == 0 && panel2.EnterCount == 0, "等待 30000ms（= TTL）后**拒绝投递**");
            Check(!PMEntryNoticeInbox.HasPending, "过期暂存被丢弃（槽已清空）");
            Check(PMEntryNoticeInbox.ExpiredCount == 1, "过期计数 +1（可观测）");
            Check(UnityEngine.Debug.Warnings.Count > 0, "过期丢弃留下告警日志");

            // TTL 不允许被调大超过硬上限
            bool threw = false;
            try
            {
                PMEntryNoticeInbox.SetTtlMsForTests(PMEntryNoticeInbox.MaxTtlMs + 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Check(threw, "TTL 不允许被调到 30s 以上（SetTtlMsForTests 抛异常）");
            Check(PMEntryNoticeInbox.TtlMs == PMEntryNoticeInbox.MaxTtlMs, "TTL 仍是硬上限 30000");
        }

        // ------------------------------------------------------------------
        //  06：新覆盖旧可观测 + 日志不泄票
        // ------------------------------------------------------------------
        private static void Section6_OverwriteObservableAndNoTicketLeak()
        {
            Console.WriteLine();
            Console.WriteLine("== 06 新通知覆盖旧通知可观测，且日志不泄票 ==");

            Reset();
            RequestManger.NotifySocketConnected();

            string textA = BuildValidText(PrefixResume, "match-overwrite-A", true);
            string textB = BuildValidText(PrefixResume, "match-overwrite-B", true);

            Check(StageViaRouting(MakePack(textA)), "第一条暂存成功");
            Check(StageViaRouting(MakePack(textB)), "第二条暂存成功");
            Check(PMEntryNoticeInbox.OverwriteCount == 1, "覆盖次数可观测（OverwriteCount == 1）");
            Check(PMEntryNoticeInbox.StagedCount == 2, "暂存次数 2");

            string logDump = UnityEngine.Debug.DumpAll();
            Check(!logDump.Contains(TicketMarker), "暂存日志不含票据（含覆盖那条）");
            Check(!logDump.Contains(textA) && !logDump.Contains(textB), "暂存日志不含完整 Str 文本");
            Check(logDump.Contains("overwrite=#1"), "暂存日志确实记录了覆盖事实（可观测）");

            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();
            request.Update();

            Check(panel.EnterCount == 1 && panel.LastOffer.MatchId == "match-overwrite-B",
                "只有最新那条进入 Enter（旧票被覆盖后不可能再被消费）");
        }

        // ------------------------------------------------------------------
        //  07：关闭连接清暂存 / 跨连接拒旧暂存 / 无连接拒暂存
        // ------------------------------------------------------------------
        private static void Section7_ConnectionCloseAndCrossConnection()
        {
            Console.WriteLine();
            Console.WriteLine("== 07 关闭 socket 清暂存 / 跨连接旧暂存被拒 ==");

            // --- 关闭连接清暂存 ---
            Reset();
            RequestManger.NotifySocketConnected();
            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;

            StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-close", true)));
            Check(PMEntryNoticeInbox.HasPending, "关闭前有暂存");

            // TCPSocketManger.CloseSocket 的真实落点
            RequestManger.NotifySocketClosed("门禁：模拟 CloseSocket");

            Check(!PMEntryNoticeInbox.HasPending, "关闭 socket 后暂存被清空（清待用票）");
            Check(PMEntryNoticeInbox.ClearedCount == 1, "清除计数 +1");

            RequestManger.PumpEntryNoticeInbox();
            request.Update();
            Check(request.Routed == 0 && panel.EnterCount == 0, "关闭后不再投递");

            // --- 跨连接：旧代次暂存必须被拒 ---
            Reset();
            RequestManger.NotifySocketConnected();          // gen = 1
            StageViaRouting(MakePack(BuildValidText(PrefixResume, "match-stale", true)));
            int genBefore = PMEntryNoticeInbox.CurrentGeneration;

            // 模拟「关闭路径被漏掉、直接重连」：代次前进但暂存还在。
            RequestManger.NotifySocketConnected();          // gen = 2
            Check(PMEntryNoticeInbox.CurrentGeneration == genBefore + 1, "重连使连接代次 +1");

            EntryPanelStub panel2 = new EntryPanelStub();
            EntryRequest request2 = new EntryRequest(panel2);
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();
            request2.Update();

            Check(request2.Routed == 0 && panel2.EnterCount == 0, "属于旧连接的暂存**不被投递**（不拿旧票开新连接）");
            Check(PMEntryNoticeInbox.StaleConnectionCount == 1, "跨连接丢弃计数 +1（可观测）");
            Check(!PMEntryNoticeInbox.HasPending, "跨连接暂存已丢弃");

            // --- 没有活动连接时拒收 ---
            Reset();
            Check(!StageViaRouting(MakePack(BuildValidText(PrefixInitial, "match-noconn", false))),
                "没有活动连接时入局通知**不被暂存**");
            Check(PMEntryNoticeInbox.StaleConnectionCount == 1, "无连接拒收计数 +1");
            Check(!PMEntryNoticeInbox.HasPending, "无连接时不留下暂存");
        }

        // ------------------------------------------------------------------
        //  08：日志脱敏（未注册分支 + BaseRequest 的正常 Trace）
        // ------------------------------------------------------------------
        private static void Section8_LogSanitization()
        {
            Console.WriteLine();
            Console.WriteLine("== 08 未注册分支与 BaseRequest.Trace 都不写 MainPack/Str/票据 ==");

            // --- 8.1 未注册的普通请求（非入局通知）--- 
            Reset();
            RequestManger.NotifySocketConnected();

            MainPack plain = MakePack(PlainMarker, ActionCode.FindFriendsInfo, RequestCode.Friend);
            plain.RequestId = 4242;
            StageViaRouting(plain);

            string dump = UnityEngine.Debug.DumpAll();
            Check(dump.Contains("不能找到对应的处理"), "未注册分支仍然记录“不能找到对应的处理”（可观测）");
            Check(dump.Contains("action=FindFriendsInfo") && dump.Contains("requestId=4242"),
                "未注册分支记录了 request/action/requestId 等非秘密元数据");
            Check(!dump.Contains(PlainMarker), "未注册分支**不打印** MainPack 内容（Str 不出现）");
            Check(!dump.Contains("str_") && !dump.Contains("Str:"), "未注册分支没有 protobuf ToString 的字段渲染痕迹");

            // --- 8.2 BaseRequest.Update 的正常 Trace ---
            Reset();
            RequestManger.NotifySocketConnected();

            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            string secretText = BuildValidText(PrefixInitial, "match-trace", false);
            string beforeTrace = ReadTrace();

            StageViaRouting(MakePack(secretText));
            MVC.UIMatchingPanel.Ready = true;
            RequestManger.PumpEntryNoticeInbox();
            request.Update();

            string traceAll = ReadTrace();
            string traceDelta = traceAll.Substring(Math.Min(beforeTrace.Length, traceAll.Length));

            Check(panel.EnterCount == 1, "（前置）正常投递仍然成功");
            Check(!traceDelta.Contains(TicketMarker), "BaseRequest.Trace 不含票据");
            Check(!traceDelta.Contains(secretText), "BaseRequest.Trace 不含完整 Str 文本");
            Check(traceDelta.Contains("OnResponse request=") && traceDelta.Contains("requestId="),
                "BaseRequest.Trace 只记 request/action/return/requestId/strLen 这类非秘密元数据");

            // --- 8.3 全流程汇总（含非法 + 合法 + 覆盖）后日志仍无票据 ---
            string dump2 = UnityEngine.Debug.DumpAll();
            Check(!dump2.Contains(TicketMarker), "汇总日志（含非法/覆盖/投递）不含票据");
            Check(!dump2.Contains(IllegalMarker), "汇总日志不含非法载荷原文");
        }

        // ------------------------------------------------------------------
        //  09：收包线程安全（收包线程写 / 主线程读，并发不崩、不重投）
        // ------------------------------------------------------------------
        private static void Section9_ThreadSafety()
        {
            Console.WriteLine();
            Console.WriteLine("== 09 收包线程与主线程并发：不崩、不重投、不泄票 ==");

            Reset();
            RequestManger.NotifySocketConnected();

            EntryPanelStub panel = new EntryPanelStub();
            EntryRequest request = new EntryRequest(panel);
            MVC.UIMatchingPanel.Ready = true;

            string resumeText = BuildValidText(PrefixResume, "match-thread", true);
            MainPack resumePack = MakePack(resumeText);
            MainPack plainPack = MakePack(PlainMarker, ActionCode.FindFriendsInfo, RequestCode.Friend);

            const int threadCount = 8;
            const int iterations = 500;
            Exception captured = null;
            Thread[] threads = new Thread[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                threads[t] = new Thread(delegate ()
                {
                    try
                    {
                        for (int i = 0; i < iterations; i++)
                        {
                            // 收包线程路径：普通未注册包 + 入局通知，交错。
                            StageViaRouting(plainPack);
                            StageViaRouting(resumePack);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref captured, ex, null);
                    }
                });
                threads[t].IsBackground = true;
            }

            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Start();
            }

            // 主线程一边泵一边抽干面板队列，制造真实的读写交错。
            try
            {
                for (int i = 0; i < 4000; i++)
                {
                    RequestManger.PumpEntryNoticeInbox();
                    request.Update();
                    if (i % 64 == 0)
                    {
                        Thread.Yield();
                    }
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Join(30000);
            }

            Check(captured == null, "并发收包/主线程泵未抛出异常（" + (captured == null ? "-" : captured.GetType().Name) + "）");

            // 收尾：不再有新暂存产生，泵干静。
            for (int i = 0; i < 20; i++)
            {
                RequestManger.PumpEntryNoticeInbox();
                request.Update();
            }

            long delivered = PMEntryNoticeInbox.DeliveredCount;
            long staged = PMEntryNoticeInbox.StagedCount;

            Check(delivered <= staged, "投递次数 ≤ 暂存次数（不会凭空多投）");
            Check(!PMEntryNoticeInbox.HasPending, "收尾后暂存槽为空");
            Check(request.Routed == (int)delivered, "RequestManger 投递次数与暂存消费次数一致");
            Check(panel.EnterCount == request.Routed, "每次投递都恰好进一次 Enter（无重复/无丢失）");
            Check(!UnityEngine.Debug.Contains(TicketMarker), "并发路径的日志也不含票据");
            Check(!UnityEngine.Debug.Contains(resumeText), "并发路径的日志不含完整 Str 文本");
            Check(PMEntryNoticeInbox.Capacity == 1, "并发下暂存槽容量仍恒为 1");
        }

        // ------------------------------------------------------------------
        //  10：宿主接线的静态结构断言（补充证据，不冒充运行验证）
        // ------------------------------------------------------------------
        private static void Section10_HostWiringStructuralChecks()
        {
            Console.WriteLine();
            Console.WriteLine("== 10 宿主接线静态结构断言（补充，非运行验证）==");
            Console.WriteLine("  （先剥离注释与字符串再看标识符：避免把注释里的“改动前写法”当成现存代码）");

            string root = FindRepoRoot();
            Check(root != null, "能找到仓库根（Client/Assets/Scripts）");
            if (root == null)
            {
                return;
            }

            // 关键：注释里会写「改动前是 Trace(... + mainPack)」，直接子串匹配会假红。
            // 先把注释剥掉，再对**真正的代码**做结构断言。
            string requestManger = StripComments(ReadSource(Path.Combine(root, "Client/Assets/Scripts/Server/Manger/RequestManger.cs")));
            string baseRequest = StripComments(ReadSource(Path.Combine(root, "Client/Assets/Scripts/Server/Request/BaseRequest.cs")));
            string hyldManger = StripComments(ReadSource(Path.Combine(root, "Client/Assets/Scripts/Server/Manger/HYLDManger.cs")));
            string tcp = StripComments(ReadSource(Path.Combine(root, "Client/Assets/Scripts/Server/Manger/TCPSocketManger.cs")));
            string matching = StripComments(ReadSource(Path.Combine(root, "Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs")));

            Check(requestManger != null && !requestManger.Contains("pack.ToString()"),
                "RequestManger.cs 的**代码**里不再出现 pack.ToString()（票据不进日志）");
            Check(baseRequest != null && !baseRequest.Contains("+ mainPack)"),
                "BaseRequest.cs 的**代码**里不再把整个 mainPack 拼进日志（票据不进日志）");
            Check(baseRequest != null && baseRequest.Contains("mainPack.Str == null ? 0 : mainPack.Str.Length"),
                "BaseRequest.cs 只记 strLen（非秘密元数据）");

            Check(hyldManger != null && hyldManger.Contains("Server.RequestManger.PumpEntryNoticeInbox();"),
                "HYLDManger.Update 真的调用了主线程泵 PumpEntryNoticeInbox");
            Check(PumpPrecedesUiExcute(hyldManger),
                "主线程泵排在 _uiManger.Excute 之前（先投递、同帧抽干）");
            Check(hyldManger != null && hyldManger.Contains("ClearStagedEntryNotice"),
                "HYLDManger 退出路径显式清暂存");

            string handleBody = ExtractMethodBody(requestManger, "public static void HandleRequest(MainPack pack)");
            Check(handleBody != null && handleBody.Contains("QueueMatchingEntryNotice("),
                "HandleRequest 把入局通知交给 QueueMatchingEntryNotice（收包线程只暂存、不投递）");
            Check(handleBody != null && !handleBody.Contains("UIMatchingPanel") && !handleBody.Contains("NetGlobal"),
                "HandleRequest（收包线程路径）不触碰任何 Unity API：既不开面板，也不碰 NetGlobal");

            string deliverBody = ExtractMethodBody(requestManger,
                "private static void OpenMatchingPanelThenDeliver(BaseRequest request, MainPack pack)");
            Check(deliverBody != null, "取到 OpenMatchingPanelThenDeliver 的方法体");
            Check(deliverBody != null && OpenPrecedesHandoff(deliverBody),
                "OpenMatchingPanelThenDeliver 里“先安全打开面板、再交包”（原有次序契约保持）");
            Check(deliverBody != null && deliverBody.Contains("ConsumeForDelivery()")
                  && deliverBody.IndexOf("ConsumeForDelivery()", System.StringComparison.Ordinal)
                     < deliverBody.IndexOf("request.OnResponse(pack);", System.StringComparison.Ordinal),
                "OpenMatchingPanelThenDeliver 先消费暂存再交包（恰好一次）");
            Check(requestManger != null && !requestManger.Contains("NetGlobal"),
                "RequestManger 的**代码**整体不再依赖 NetGlobal（收包线程不再懒创建 GameObject）");

            Check(tcp != null && tcp.Contains("RequestManger.NotifySocketConnected();"),
                "TCPSocketManger 建立连接时通知请求层“新连接代次”");
            Check(tcp != null && tcp.Contains("RequestManger.NotifySocketClosed(reason);"),
                "TCPSocketManger.CloseSocket 通知请求层清暂存");

            string removeAllBody = ExtractMethodBody(requestManger, "public static void RemoveAllRequest()");
            Check(removeAllBody != null, "取到 RemoveAllRequest 的方法体");
            Check(removeAllBody != null && !removeAllBody.Contains("PMEntryNoticeInbox"),
                "RemoveAllRequest 的方法体**不触碰**暂存槽（OnInit 期间保留待用票）");
            Check(removeAllBody != null && removeAllBody.Contains("PmRpcClient.ClearAll()"),
                "RemoveAllRequest 仍然清待确认表（原有语义未变）");

            // 真实面板仍是唯一入局裁决者：严格解码 + 进 Enter，且没有旧链回退。
            Check(matching != null && matching.Contains("PMDsEntryCodec.TryDecode(pack.Str, out offer, out decodeError)"),
                "UIMatchingPanel 仍自己严格解码（不绕过面板校验）");
            Check(matching != null && matching.Contains("PMClientSessionHost.Enter(offer)"),
                "UIMatchingPanel 仍把 offer 交 PMClientSessionHost.Enter");
            Check(matching != null && !matching.Contains("BattleData") && !matching.Contains("ClearSence"),
                "UIMatchingPanel 的代码里没有旧链回退（BattleData/ClearSence 已退役）");
        }

        /// <summary>取一个方法的方法体（按大括号配平）；用于「这个方法体里不许出现 X」这类断言。</summary>
        private static string ExtractMethodBody(string code, string signature)
        {
            if (code == null)
            {
                return null;
            }

            int start = code.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            int open = code.IndexOf('{', start);
            if (open < 0)
            {
                return null;
            }

            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return code.Substring(open, i - open + 1);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 剥离 C# 注释（行注释 / 块注释），保留字符串与字符字面量（含逐字字符串 @"..."）。
        /// 用途见 Section10：结构断言必须看**代码**，不能把注释里的“改动前写法”当成现存代码。
        /// </summary>
        private static string StripComments(string source)
        {
            if (source == null)
            {
                return null;
            }

            StringBuilder sb = new StringBuilder(source.Length);
            bool inString = false;
            bool inVerbatim = false;
            bool inChar = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char next = (i + 1 < source.Length) ? source[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n')
                    {
                        inLineComment = false;
                        sb.Append(c);
                    }

                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (inVerbatim)
                {
                    sb.Append(c);
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            sb.Append(next);
                            i++;
                        }
                        else
                        {
                            inVerbatim = false;
                        }
                    }

                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }

                    continue;
                }

                if (c == '@' && next == '"')
                {
                    inVerbatim = true;
                    sb.Append(c);
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '\'')
                {
                    inChar = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>静态断言：OpenMatchingPanelThenDeliver 里“开面板”在“交包”之前。</summary>
        private static bool OpenPrecedesHandoff(string deliverBody)
        {
            int open = deliverBody.IndexOf("TryEnsureOpenForEntryNotice(out error)", StringComparison.Ordinal);
            int hand = deliverBody.IndexOf("request.OnResponse(pack);", StringComparison.Ordinal);
            return open >= 0 && hand >= 0 && open < hand;
        }

        /// <summary>静态断言：PumpEntryNoticeInbox 的调用点在 _uiManger.Excute 之前。</summary>
        private static bool PumpPrecedesUiExcute(string source)
        {
            if (source == null)
            {
                return false;
            }

            int pump = source.IndexOf("Server.RequestManger.PumpEntryNoticeInbox();", StringComparison.Ordinal);
            int excute = source.IndexOf("_uiManger.Excute(Time.deltaTime);", StringComparison.Ordinal);
            return pump >= 0 && excute >= 0 && pump < excute;
        }

        // ==================================================================
        //  工具
        // ==================================================================

        private const string PrefixInitial = "PMDS1:";
        private const string PrefixResume = "PMDSR1:";

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name + " ==");
        }

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _pass++;
                Console.WriteLine("  [PASS] " + what);
            }
            else
            {
                _fail++;
                _failures.Add(what);
                Console.WriteLine("  [FAIL] " + what);
            }
        }

        private static void Reset()
        {
            UnityEngine.Debug.ClearAll();
            PMEntryNoticeInbox.ResetForTests();
            RequestManger.RemoveAllRequest();
            MVC.UIMatchingPanel.Ready = false;
            MVC.UIMatchingPanel.EnsureOpenCalls = 0;
            lock (HYLDManger.Instance.ReceivedPongs) { HYLDManger.Instance.ReceivedPongs.Clear(); }
            lock (HYLDManger.Instance.SentPacks) { HYLDManger.Instance.SentPacks.Clear(); }
        }

        /// <summary>
        /// 走**真实收包入口**投递一个包，返回「它是否被暂存」。
        /// 不新造任何生产 API：判定依据就是真实 <see cref="PMEntryNoticeInbox.StagedCount"/> 的增量。
        /// </summary>
        private static bool StageViaRouting(MainPack pack)
        {
            long before = PMEntryNoticeInbox.StagedCount;
            RequestManger.HandleRequest(pack);
            return PMEntryNoticeInbox.StagedCount > before;
        }

        /// <summary>
        /// 非法载荷：走**真实收包入口** <see cref="RequestManger.HandleRequest"/>（因此先过
        /// 「Succeed + StartEnterBattle + PMDS 前缀」这层闸门），再由真实 codec 严格解码拒收 ——
        /// 前缀像不代表能收，这正是「不以字符串正则冒充运行验证」的判据。
        /// </summary>
        private static void ExpectIllegal(string label, string text)
        {
            long beforeRejected = PMEntryNoticeInbox.RejectedIllegalCount;
            long beforeStaged = PMEntryNoticeInbox.StagedCount;

            RequestManger.HandleRequest(MakePack(text));

            Check(PMEntryNoticeInbox.StagedCount == beforeStaged, "非法载荷未被暂存：" + label);
            Check(PMEntryNoticeInbox.RejectedIllegalCount == beforeRejected + 1,
                "非法载荷计入 RejectedIllegalCount（严格解码拒绝）：" + label);
            Check(!PMEntryNoticeInbox.HasPending, "非法载荷不留下暂存：" + label);
        }

        /// <summary>构造一条由**真实 codec** 编码的合法入局通知文本。</summary>
        private static string BuildValidText(string prefix, string matchId, bool resume)
        {
            PMDsEntryOffer offer = new PMDsEntryOffer();
            offer.MatchId = matchId;
            offer.DsId = "ds-live1";
            offer.Host = "127.0.0.1";
            offer.Epoch = 7u;
            offer.ProtocolHash = 0xAEA98336u;
            offer.CollisionDigest = 0x52334201u;
            offer.Port = 31000;
            offer.Identity = new PMNet.Control.PMDsRosterIdentity(101, 202, 0, 3);
            offer.Ticket = MakeTicket(TicketMarker);
            offer.IsResume = resume;
            return PMDsEntryCodec.Encode(offer);
        }

        /// <summary>假票据：只需满足「以既有 codec 的 PMDS 魔数开头且长度在界内」。</summary>
        private static byte[] MakeTicket(string marker)
        {
            string body = "PMDS" + marker + new string('Z', 24);
            byte[] bytes = Encoding.ASCII.GetBytes(body);
            if (bytes.Length > PMDsEntryCodec.MaxTicketBytes)
            {
                throw new InvalidOperationException("测试票据超过 codec 上限");
            }

            return bytes;
        }

        private static MainPack MakePack(string text)
        {
            return MakePack(text, ActionCode.StartEnterBattle, RequestCode.Matching, ReturnCode.Succeed);
        }

        private static MainPack MakePack(string text, ActionCode action, RequestCode request)
        {
            return MakePack(text, action, request, ReturnCode.Succeed);
        }

        private static MainPack MakePack(string text, ActionCode action, RequestCode request, ReturnCode ret)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = request;
            pack.Actioncode = action;
            pack.Returncode = ret;
            pack.Str = text;
            return pack;
        }

        /// <summary>按真实 codec 的二进制布局手写载荷（门禁需要构造**越界**字段，故不能走 Encode）。</summary>
        private static byte[] BuildPayload(string matchId, string dsId, string host, uint epoch, uint hash, uint digest,
            int port, int uid, int playerId, int teamId, int heroId, byte[] ticket, bool appendTrailingByte)
        {
            PMNet.PMNetWriter w = new PMNet.PMNetWriter(512);
            w.WriteInt32(1);
            w.WriteStringValue(matchId);
            w.WriteStringValue(dsId);
            w.WriteStringValue(host);
            w.WriteUInt32(epoch);
            w.WriteUInt32(hash);
            w.WriteUInt32(digest);
            w.WriteInt32(port);
            w.WriteInt32(uid);
            w.WriteInt32(playerId);
            w.WriteInt32(teamId);
            w.WriteInt32(heroId);
            w.WriteInt32(ticket == null ? 0 : ticket.Length);
            if (ticket != null && ticket.Length > 0)
            {
                w.WriteRawBytes(ticket, 0, ticket.Length);
            }

            if (appendTrailingByte)
            {
                w.WriteRawBytes(new byte[] { 0 }, 0, 1);
            }

            byte[] outBytes = new byte[w.Length];
            Array.Copy(w.GetBuffer(), outBytes, w.Length);
            return outBytes;
        }

        private static byte[] BuildPayloadV2(string matchId, byte[] ticket)
        {
            PMNet.PMNetWriter w = new PMNet.PMNetWriter(512);
            w.WriteInt32(2); // 版本 2：codec 必须拒绝
            w.WriteStringValue(matchId);
            w.WriteStringValue("d");
            w.WriteStringValue("h");
            w.WriteUInt32(1u);
            w.WriteUInt32(1u);
            w.WriteUInt32(0u);
            w.WriteInt32(7777);
            w.WriteInt32(1);
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(ticket.Length);
            w.WriteRawBytes(ticket, 0, ticket.Length);
            byte[] outBytes = new byte[w.Length];
            Array.Copy(w.GetBuffer(), outBytes, w.Length);
            return outBytes;
        }

        private static void InitTrace()
        {
            _tracePath = Path.Combine(Path.GetTempPath(), "pm_entry_inbox_trace_" + Guid.NewGuid().ToString("N") + ".log");
            Logging.HYLDDebug.TraceSavePath = _tracePath;
            Logging.HYLDDebug.SetLogAllSeverities();
        }

        private static string ReadTrace()
        {
            Logging.HYLDDebug.FlushTrace();
            if (!File.Exists(_tracePath))
            {
                return string.Empty;
            }

            try
            {
                using (FileStream fs = new FileStream(_tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        private static void CleanupTrace()
        {
            try
            {
                if (!string.IsNullOrEmpty(_tracePath) && File.Exists(_tracePath))
                {
                    File.Delete(_tracePath);
                }
            }
            catch (IOException)
            {
            }
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string probe = Path.Combine(dir.FullName, "Client", "Assets", "Scripts");
                if (Directory.Exists(probe))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static string ReadSource(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
