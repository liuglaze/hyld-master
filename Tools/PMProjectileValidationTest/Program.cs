// ============================================================================
//  R5-A2 验收（T5A4 / T5A5）：目标历史与命中验证纯核心
// ============================================================================
//
//  被测实现（真实源码，非替身）：
//    · Client/Assets/Scripts/PMProjectile/PMProjectileHistory.cs
//    · Client/Assets/Scripts/PMProjectile/PMProjectileValidator.cs
//
//  覆盖：
//    T5A4 —— variable dt 帧锚新鲜度、同宿主帧多输出、旧流/epoch/未来锚/Teleport/缺历史、
//            不外推、now-clamped rewind、保留窗口、环形容量、目标容量、Record 校验。
//    T5A5 —— L2 三分支与 skip 只跳飞行、NaN/Inf/20m 边界、rewind Report 后坏目标仍 Reject、
//            目标尺寸逐目标独立、白名单/去重、Filter 收到钳制值、几何平行/交叉/点/碰边，
//            Pending 无权威副作用与输入未改、配额、activation0 仅 ServerDirect、墓碑截止。
//
//  预期值全部由独立手算给出（不使用被测实现的常量作为期望，除契约冻结的数值本身）。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

namespace PMProjectileValidationTest
{
    internal static class Program
    {
        private const uint Epoch = 7u;
        private const uint Owner = 3u;
        private const uint ProjId = 5u;
        private const uint TargetA = 42u;
        private const uint TargetB = 43u;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R5-A2 T5A4/T5A5：PMProjectileHistory + PMProjectileValidator 纯核心 ===");
            Console.WriteLine("预期值由独立手算给出；不引用实现自身常量作为期望。");
            Console.WriteLine();

            SectionT5A4_1_FrameAnchorFreshness();
            SectionT5A4_2_SameFrameMultipleOutputs();
            SectionT5A4_3_StreamEpochAndMissing();
            SectionT5A4_4_RecordValidationAndBounds();
            SectionT5A4_5_TeleportAndNoExtrapolation();
            SectionT5A5_1_L2BudgetBranches();
            SectionT5A5_2_EntryHardGates();
            SectionT5A5_3_PerTargetSize();
            SectionT5A5_4_DedupAndWhitelist();
            SectionT5A5_5_FilterGetsClampedPoint();
            SectionT5A5_6_Geometry();
            SectionT5A5_7_PendingQuotaActivation();
            SectionT5A5_8_NoPartialOutputAndNoInputMutation();
            SectionT5A2R_AdversarialRegression();

            Console.WriteLine();
            Console.WriteLine("================ 结果 ================");
            Console.WriteLine("通过: " + _passed);
            Console.WriteLine("失败: " + _failures.Count);
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("  [FAIL] " + _failures[i]);
            }

            return _failures.Count == 0 ? 0 : 1;
        }

        // ==================================================================
        //  T5A4-1 帧锚新鲜度
        // ==================================================================

        private static void SectionT5A4_1_FrameAnchorFreshness()
        {
            Section("T5A4-1 帧锚新鲜度：用 TotalSimTimeMs age[0,600+defer]，禁止帧差×16");

            // 1a) 帧差 90、仿真时间差仅 100ms → 必须命中 FrameAnchor（帧差×16 会算成 1440ms 误判过期）。
            PMProjectileHistory h1 = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f),
                Sample(TargetA, 1u, 100, 110, 1100.0, 5150.0, 0f, 0f, 1f, 0.5f, 1f));
            PMProjectileTargetSample s;
            PMProjectileHistoryResolution r;
            bool ok = h1.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 1f), 5150.0, 0, 0, out s, out r);
            Check("1a 命中", ok);
            CheckEqEnum("1a 帧差90/时间差100ms → FrameAnchor", PMProjectileHistoryResolution.FrameAnchor, r);

            // 1b) 帧差 3、仿真时间差 750ms → 必须退化 Rewind（帧差×16 会算成 48ms 误判新鲜）。
            PMProjectileHistory h2 = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f),
                Sample(TargetA, 1u, 13, 23, 1750.0, 5400.0, 0f, 0f, 4f, 0.5f, 1f));
            ok = h2.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5400.0, 100, 0, out s, out r);
            Check("1b 命中", ok);
            CheckEqEnum("1b 帧差3/时间差750ms → 退 Rewind", PMProjectileHistoryResolution.Rewind, r);
            // targetWorld = 5400-100 = 5300；s0(world5000,z0) → s1(world5400,z4)；alpha=0.75 → z=3
            CheckEqF("1b 时间回溯插值 z=3", 3.0, (double)s.Position.Z, 1e-4);

            // 1c) extraDefer=200 → 窗口 800 → 750 通过 → FrameAnchor（锚样本 z=0）
            ok = h2.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5400.0, 100, 200, out s, out r);
            Check("1c 命中", ok);
            CheckEqEnum("1c defer=200 扩窗 → FrameAnchor", PMProjectileHistoryResolution.FrameAnchor, r);
            CheckEqF("1c 取锚样本位置 z=0", 0.0, (double)s.Position.Z, 1e-6);
        }

        // ==================================================================
        //  T5A4-2 同宿主帧多输出
        // ==================================================================

        private static void SectionT5A4_2_SameFrameMultipleOutputs()
        {
            Section("T5A4-2 同 AuthorityServer 帧多 Input 边界：取最后输出，迟到旧重复被忽略");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));
            Check("同帧更新（输出边界 21）接受", h.Record(Sample(TargetA, 1u, 10, 21, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f)));

            PMProjectileTargetSample s;
            PMProjectileHistoryResolution r;
            h.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5000.0, 0, 0, out s, out r);
            CheckEqF("取最后输出 z=1", 1.0, (double)s.Position.Z, 1e-6);
            CheckEqEnum("同帧仍是帧锚命中", PMProjectileHistoryResolution.FrameAnchor, r);

            Check("迟到更旧输出（边界 19）被忽略但接受", h.Record(Sample(TargetA, 1u, 10, 19, 1000.0, 5000.0, 0f, 0f, 2f, 0.5f, 1f)));
            CheckEq("样本数仍为 1（同帧就地更新）", 1, h.SampleCount(TargetA));
            CheckEq("迟到旧重复忽略计数 1", 1, h.IgnoredStaleDuplicates);
            h.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5000.0, 0, 0, out s, out r);
            CheckEqF("迟到旧输出未覆盖 z=1", 1.0, (double)s.Position.Z, 1e-6);
        }

        // ==================================================================
        //  T5A4-3 旧流 / epoch / 缺失
        // ==================================================================

        private static void SectionT5A4_3_StreamEpochAndMissing()
        {
            Section("T5A4-3 升代清旧 / 旧流拒收 / 跨 epoch 不退化 / 缺失 / Reset 拒旧");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));

            string reason;
            Check("升代接受", h.Record(Sample(TargetA, 2u, 20, 30, 1500.0, 5200.0, 0f, 0f, 9f, 0.5f, 1f), out reason));
            CheckEq("升代清旧：样本数 1", 1, h.SampleCount(TargetA));
            CheckEq("stream 版本升到 2", 2, (long)h.StreamVersionOf(TargetA));

            PMProjectileTargetSample s;
            PMProjectileHistoryResolution r;
            Check("旧流请求拒收（不退化到另一世代）", !h.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5200.0, 0, 0, out s, out r));
            Check("新流请求可用", h.TryResolve(Epoch, Cand(TargetA, 2u, 20, 0f, 0f, 9f), 5200.0, 0, 0, out s, out r));
            Check("旧流 Record 拒收", !h.Record(Sample(TargetA, 1u, 21, 31, 1600.0, 5300.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            CheckEqS("旧流拒收原因", "stale-stream", reason);
            Check("未知 stream 请求拒收", !h.TryResolve(Epoch, Cand(TargetA, 7u, 20, 0f, 0f, 0f), 5200.0, 0, 0, out s, out r));

            PMProjectileHistory he = new PMProjectileHistory(Epoch);
            Check("异 epoch Record 拒收", !he.Record(WithEpoch(Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f), 8u), out reason));
            CheckEqS("异 epoch 拒收原因", "epoch-mismatch", reason);
            Check("异 epoch 请求不退化", !he.TryResolve(8u, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5000.0, 0, 0, out s, out r));

            Check("基线记录", he.Record(Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            he.Reset(9u);
            CheckEq("Reset 后样本清空", 0, he.SampleCount(TargetA));
            Check("Reset 后旧 epoch 请求拒收", !he.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5000.0, 0, 0, out s, out r));
            Check("Reset 后旧 epoch 记录拒收", !he.Record(Sample(TargetA, 1u, 11, 21, 1100.0, 5100.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            Check("Reset 后新 epoch 可用", he.Record(WithEpoch(Sample(TargetA, 1u, 11, 21, 1100.0, 5100.0, 0f, 0f, 0f, 0.5f, 1f), 9u), out reason));

            Check("未跟踪目标请求拒收", !h.TryResolve(Epoch, Cand(TargetB, 2u, 20, 0f, 0f, 0f), 5200.0, 0, 0, out s, out r));
        }

        // ==================================================================
        //  T5A4-4 Record 校验与有界
        // ==================================================================

        private static void SectionT5A4_4_RecordValidationAndBounds()
        {
            Section("T5A4-4 Record 校验：finite / 帧域 / 尺寸 / 单调；保留窗口 / 环形 512 / 目标 64");

            PMProjectileHistory h = new PMProjectileHistory(Epoch);
            string reason;
            PMProjectileTargetSample good = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f);
            Check("基线接受", h.Record(good, out reason));

            PMProjectileTargetSample t = good;
            t.Position = new PMVector3(float.NaN, 0f, 0f);
            Check("NaN 位置拒绝", !h.Record(t, out reason));
            CheckEqS("原因 position-not-finite", "position-not-finite", reason);

            t = good;
            t.Position = new PMVector3(0f, float.PositiveInfinity, 0f);
            Check("Inf 位置拒绝", !h.Record(t, out reason));

            t = good;
            t.RadiusM = 0f;
            Check("radius=0 拒绝", !h.Record(t, out reason));
            CheckEqS("原因 radius-not-positive", "radius-not-positive", reason);

            t = good;
            t.HalfHeightM = 0.3f;
            Check("halfHeight<radius 拒绝", !h.Record(t, out reason));
            CheckEqS("原因 halfheight-below-radius", "halfheight-below-radius", reason);

            t = good;
            t.HalfHeightM = float.NaN;
            Check("halfHeight NaN 拒绝", !h.Record(t, out reason));

            t = good;
            t.ServerFrame = Inp(10);
            Check("ServerFrame 域非法拒绝", !h.Record(t, out reason));
            CheckEqS("原因 serverframe-domain", "serverframe-domain", reason);

            t = good;
            t.ServerFrame = PMFrameId.None;
            Check("ServerFrame 未建立拒绝", !h.Record(t, out reason));

            t = good;
            t.OutputFrame = Srv(20);
            Check("OutputFrame 域非法拒绝", !h.Record(t, out reason));
            CheckEqS("原因 outputframe-domain", "outputframe-domain", reason);

            t = good;
            t.StreamVersion = 0u;
            Check("stream=0 拒绝", !h.Record(t, out reason));
            CheckEqS("原因 stream-invalid", "stream-invalid", reason);

            Check("时间回归拒绝", !h.Record(Sample(TargetA, 1u, 11, 21, 900.0, 5100.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            CheckEqS("原因 totaltime-regression", "totaltime-regression", reason);

            Check("WorldTime 回归拒绝", !h.Record(Sample(TargetA, 1u, 12, 22, 1300.0, 4900.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            CheckEqS("原因 worldtime-regression", "worldtime-regression", reason);

            Check("帧号回归拒绝", !h.Record(Sample(TargetA, 1u, 9, 19, 1300.0, 5100.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            CheckEqS("原因 serverframe-regression", "serverframe-regression", reason);

            // 保留窗口（1000ms，按 WorldTimeMs）
            PMProjectileHistory hr = HistoryWith(Sample(TargetA, 1u, 10, 20, 1000.0, 1000.0, 0f, 0f, 1f, 0.5f, 1f));
            PMProjectileTargetSample s;
            PMProjectileHistoryResolution r;
            Check("超保留窗口后查不到（1000ms）", !hr.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 1f), 3000.0, 0, 0, out s, out r));
            CheckEq("裁剪计数 1", 1, hr.PrunedByAge);

            // 环形 512
            PMProjectileHistory hring = new PMProjectileHistory(Epoch);
            for (int i = 0; i < 600; i++)
            {
                hring.Record(Sample(TargetA, 1u, 10 + i, 20 + i, 1000.0 + i, 5000.0 + i, 0f, 0f, 1f, 0.5f, 1f), out reason);
            }

            CheckEq("环形上界 = 512", 512, hring.SampleCount(TargetA));
            CheckEq("覆盖最旧计数 = 88", 88, hring.OverwrittenSamples);

            // 目标容量 64 拒新
            PMProjectileHistory hcap = new PMProjectileHistory(Epoch);
            int accepted = 0;
            for (uint i = 1u; i <= 64u; i++)
            {
                if (hcap.Record(Sample(i, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f), out reason))
                {
                    accepted++;
                }
            }

            CheckEq("64 个目标全部接受", 64, accepted);
            Check("第 65 个目标拒新", !hcap.Record(Sample(65u, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f), out reason));
            CheckEqS("容量拒因 target-capacity", "target-capacity", reason);
            CheckEq("容量拒新计数 1", 1, hcap.RejectedNewTargets);
        }

        // ==================================================================
        //  T5A4-5 Teleport / 不外推 / now-clamped
        // ==================================================================

        private static void SectionT5A4_5_TeleportAndNoExtrapolation()
        {
            Section("T5A4-5 不跨 Teleport 插值 / 不外推 / now-clamped rewind / 未来锚走回溯");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f),
                Sample(TargetA, 1u, 20, 30, 1100.0, 5100.0, 0f, 0f, 10f, 0.5f, 1f, true, true));

            PMProjectileTargetSample s;
            PMProjectileHistoryResolution r;
            h.TryResolve(Epoch, Cand(TargetA, 1u, 5, 0f, 0f, 0f), 5100.0, 50, 0, out s, out r);
            CheckEqEnum("跨 Teleport 不插值 → Rewind", PMProjectileHistoryResolution.Rewind, r);
            CheckEqF("取近侧（teleport 前）z=0", 0.0, (double)s.Position.Z, 1e-6);
            CheckEq("teleport 拒插值计数 1", 1, h.TeleportRefusals);

            h.TryResolve(Epoch, Cand(TargetA, 1u, 5, 0f, 0f, 0f), 6000.0, 0, 0, out s, out r);
            CheckEqEnum("请求晚于最新 → Current（不外推）", PMProjectileHistoryResolution.Current, r);
            CheckEqF("Current 取最新位置 z=10", 10.0, (double)s.Position.Z, 1e-6);

            h.TryResolve(Epoch, Cand(TargetA, 1u, 5, 0f, 0f, 0f), 5100.0, 0, 0, out s, out r);
            CheckEqEnum("rewind=0 → Current", PMProjectileHistoryResolution.Current, r);

            h.TryResolve(Epoch, Cand(TargetA, 1u, 99, 0f, 0f, 0f), 5100.0, 50, 0, out s, out r);
            CheckEqEnum("未来锚（帧号>最新）→ 时间回溯", PMProjectileHistoryResolution.Rewind, r);

            // 锚帧不存在 → 候选帧缺失也退回溯
            h.TryResolve(Epoch, Cand(TargetA, 1u, 15, 0f, 0f, 0f), 5100.0, 50, 0, out s, out r);
            CheckEqEnum("锚帧缺失 → 时间回溯", PMProjectileHistoryResolution.Rewind, r);

            PMProjectileHistory h2 = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 1900.0, 0f, 0f, 1f, 0.5f, 1f),
                Sample(TargetA, 1u, 11, 21, 1100.0, 2000.0, 0f, 0f, 2f, 0.5f, 1f));
            h2.TryResolve(Epoch, Cand(TargetA, 1u, 5, 0f, 0f, 1f), 2400.0, 500, 0, out s, out r);
            CheckEqEnum("早于保留窗口 → Rewind（钳到最旧）", PMProjectileHistoryResolution.Rewind, r);
            CheckEqF("钳到最旧样本 z=1", 1.0, (double)s.Position.Z, 1e-6);
            CheckEq("钳位计数 1", 1, h2.ClampedRewinds);

            // 缺历史（未跟踪目标）时，验证器按 HistoryUnavailable skip，而不是整包拒。
            PMProjectileHistory hEmpty = new PMProjectileHistory(Epoch);
            PMProjectileState st = FlyingState();
            PMProjectileHitBatch b = Batch(st.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult res = Run(st, Spec(), b, hEmpty, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("缺历史：整包仍 Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEqEnum("缺历史：目标 skip 原因 HistoryUnavailable",
                PMProjectileSkipReason.HistoryUnavailable, SkipReasonOf(res, TargetA));
        }

        // ==================================================================
        //  T5A5-1 L2 三分支
        // ==================================================================

        private static void SectionT5A5_1_L2BudgetBranches()
        {
            Section("T5A5-1 L2 三分支：飞行 / 停止+回放 / 停止+常规；skip 只跳飞行");

            PMProjectileHistory hA = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter filter = new RecordingFilter();
            PMProjectileState st = FlyingState();
            PMProjectileSpec spec = Spec();

            // ① 飞行：budget = 10*(2*100 + 100 + 0)/1000 = 3.0m
            PMProjectileHitBatch bEdge = Batch(st.Key, 0.6f, 3.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult res = Run(st, spec, bEdge, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("① 飞行 budget=3.0m 边界 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("① 边界命中 1", 1, res.Hits.Length);

            PMProjectileHitBatch bOver = Batch(st.Key, 0.6f, 3.01f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, spec, bOver, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("① 飞行 3.01m 超预算 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("① 原因 TrajectoryBudgetExceeded", PMProjectileRejectReason.TrajectoryBudgetExceeded, res.Reason);

            // ②a 停止+回放：锚＝出生点 (0,0,0)，budget = 10*(2*100+100+200)/1000 = 5.0m
            //     停止位置故意放到 (0,0,50)：若锚错用停止位置则距离 47 > 5 → 会 Rejected。
            PMProjectileState stReplay = FlyingState();
            stReplay.Stopped = true;
            stReplay.Position = new PMVector3(0f, 0f, 50f);
            stReplay.SpawnPosition = new PMVector3(0f, 0f, 0f);
            PMProjectileHitBatch bReplay = Batch(stReplay.Key, 0.6f, 3.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(stReplay, spec, bReplay, hA, filter, PMActivationResult.Confirmed, 5000.0, 200, 0);
            CheckEqEnum("②a 停止+回放锚出生点 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);

            // ②b 停止+常规：锚＝停止位置 (0,0,0)，budget = 0.1+5.0+0.3 = 5.4m
            //     出生点故意放到 (0,0,100)：若锚错用出生点则距离 100 > 5.4 → 会 Rejected。
            PMProjectileState stStop = FlyingState();
            stStop.Stopped = true;
            stStop.Position = new PMVector3(0f, 0f, 0f);
            stStop.SpawnPosition = new PMVector3(0f, 0f, 100f);
            PMProjectileHitBatch bStopOk = Batch(stStop.Key, 0.6f, 5.3f, 0, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(stStop, spec, bStopOk, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("②b 停止+常规锚停止位置 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);

            PMProjectileHitBatch bStopBad = Batch(stStop.Key, 0.6f, 5.45f, 0, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(stStop, spec, bStopBad, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("②b 5.45m 超 5.4m → Rejected（锚＝停止位置）", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("②b 原因 TrajectoryBudgetExceeded", PMProjectileRejectReason.TrajectoryBudgetExceeded, res.Reason);

            // ③ skipTrajectoryValidation 只跳飞行分支
            PMProjectileSpec specZero = Spec();
            specZero.SpeedMps = 0f;
            PMProjectileHitBatch bZero = Batch(st.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, specZero, bZero, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("速度 0 且未 skip → 超预算 Rejected", PMProjectileValidateStatus.Rejected, res.Status);

            specZero.SkipFlyingTrajectoryValidation = true;
            res = Run(st, specZero, bZero, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("skip 飞行分支 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("skip 飞行后仍正常命中", 1, res.Hits.Length);

            PMProjectileState stStopSkip = FlyingState();
            stStopSkip.Stopped = true;
            stStopSkip.Position = new PMVector3(0f, 0f, 0f);
            stStopSkip.SpawnPosition = new PMVector3(0f, 0f, 0f);
            PMProjectileHitBatch bFarStop = Batch(stStopSkip.Key, 0.6f, 15.0f, 0, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(stStopSkip, specZero, bFarStop, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("skip 不跳过 ②b → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("skip 不跳过 ②b 原因", PMProjectileRejectReason.TrajectoryBudgetExceeded, res.Reason);
        }

        // ==================================================================
        //  T5A5-2 入口硬门
        // ==================================================================

        private static void SectionT5A5_2_EntryHardGates()
        {
            Section("T5A5-2 入口硬门：NaN/Inf、20m 边界、rewind>1000 只 Report 但坏目标仍 Reject");

            PMProjectileHistory hA = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter filter = new RecordingFilter();
            PMProjectileState st = FlyingState();
            PMProjectileSpec spec = Spec();
            PMProjectileSpec specSkip = Spec();
            specSkip.SkipFlyingTrajectoryValidation = true;

            // NaN 命中点
            PMProjectileHitBatch bNaN = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(float.NaN, 0f, 1f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult res = Run(st, spec, bNaN, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("HitPosition NaN → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 HitPositionNotFinite", PMProjectileRejectReason.HitPositionNotFinite, res.Reason);

            // Inf 前一点
            PMProjectileHitBatch bInfPrev = BatchSegment(st.Key, new PMVector3(float.PositiveInfinity, 0f, 0f), new PMVector3(0f, 0f, 1f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, specSkip, bInfPrev, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("PreviousPosition Inf → Rejected", PMProjectileValidateStatus.Rejected, res.Status);

            // 20m 线段边界
            PMProjectileHitBatch bSeg20 = BatchSegment(st.Key, new PMVector3(0f, 0f, 0f), new PMVector3(0f, 0f, 20f), 0,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, specSkip, bSeg20, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("线段 20.0m 边界通过", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("20m 线段命中 1", 1, res.Hits.Length);

            PMProjectileHitBatch bSegOver = BatchSegment(st.Key, new PMVector3(0f, 0f, 0f), new PMVector3(0f, 0f, 20.01f), 0,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, specSkip, bSegOver, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("线段 20.01m → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 SegmentTooLong", PMProjectileRejectReason.SegmentTooLong, res.Reason);

            // VisualOffset 20m 边界（只影响该目标几何，不整包拒）
            PMProjectileHitBatch bVo20 = Batch(st.Key, 0.6f, 1.0f, 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f, 0f, 20f, 0f));
            res = Run(st, spec, bVo20, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("VisualOffset 20.0m 边界不整包拒", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("VisualOffset 20m → 目标几何 miss", 0, res.Hits.Length);
            CheckEqEnum("target skip 原因 GeometryMiss", PMProjectileSkipReason.GeometryMiss, SkipReasonOf(res, TargetA));

            PMProjectileHitBatch bVoOver = Batch(st.Key, 0.6f, 1.0f, 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f, 0f, 20.01f, 0f));
            res = Run(st, spec, bVoOver, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("VisualOffset 20.01m → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 VisualOffsetTooLarge", PMProjectileRejectReason.VisualOffsetTooLarge, res.Reason);

            // rewind
            PMProjectileHitBatch bNeg = Batch(st.Key, 0.6f, 1.0f, -1, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, spec, bNeg, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("rewind<0 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 RewindNegative", PMProjectileRejectReason.RewindNegative, res.Reason);

            // rewind>1000：先 Report，再继续检查候选 → 坏目标仍必须 Reject
            PMProjectileHitBatch bReportBad = Batch(st.Key, 0.6f, 1.0f, 1200,
                Cand(TargetA, 1u, 10, float.NaN, 0f, 1f));
            res = Run(st, spec, bReportBad, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("rewind>1000 Report 后坏目标仍 Reject", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 ImpactPointNotFinite", PMProjectileRejectReason.ImpactPointNotFinite, res.Reason);
            Check("rewind>1000 已 Report", HasReport(res, "rewind-exceeds-1000ms"));

            // rewind>1000 且全部合法 → 只 Report，不 Reject
            PMProjectileHitBatch bReportOk = Batch(st.Key, 0.6f, 1.0f, 1200, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, spec, bReportOk, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("rewind>1000 合法包 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            Check("合法包也带 rewind>1000 Report", HasReport(res, "rewind-exceeds-1000ms"));

            // 目标数超限
            PMProjectileHitCandidate[] many = new PMProjectileHitCandidate[101];
            for (int i = 0; i < many.Length; i++)
            {
                many[i] = Cand((uint)(1000 + i), 1u, 10, 0f, 0f, 1f);
            }

            PMProjectileHitBatch bMany = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100, many);
            res = Run(st, spec, bMany, hA, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("目标数 101 > 100 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 TargetCountExceeded", PMProjectileRejectReason.TargetCountExceeded, res.Reason);
        }

        // ==================================================================
        //  T5A5-3 逐目标尺寸独立
        // ==================================================================

        private static void SectionT5A5_3_PerTargetSize()
        {
            Section("T5A5-3 逐目标尺寸独立：大目标命中、小目标 miss；非法尺寸明确 skip");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f),
                Sample(TargetB, 1u, 10, 20, 1000.0, 5000.0, 0f, 3f, 0f, 0.05f, 0.1f));
            PMProjectileState st = FlyingState();
            RecordingFilter filter = new RecordingFilter();
            PMProjectileHitBatch b = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f), Cand(TargetB, 1u, 10, 0f, 3f, 0f));
            PMProjectileValidateResult res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("B miss 不连坐 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("只命中大目标 A", 1, res.Hits.Length);
            CheckEq("命中目标为 A", (long)TargetA, (long)res.Hits[0].TargetNetId);
            CheckEqEnum("B 因几何 miss 被 skip", PMProjectileSkipReason.GeometryMiss, SkipReasonOf(res, TargetB));

            // 非法尺寸样本（历史 Record 会拒，故用替身 provider 直接给出）
            StubHistory stub = new StubHistory();
            PMProjectileHitBatch bA = Batch(st.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            stub.SampleValue = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0f, 1f);
            res = Run(st, Spec(), bA, stub, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("radius=0 → 目标明确 skip", PMProjectileSkipReason.TargetSizeInvalid, SkipReasonOf(res, TargetA));
            CheckEqEnum("radius=0 时整包仍 Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);

            stub.SampleValue = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 0.3f);
            res = Run(st, Spec(), bA, stub, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("halfHeight<radius → 目标明确 skip", PMProjectileSkipReason.TargetSizeInvalid, SkipReasonOf(res, TargetA));

            stub.SampleValue = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, float.NaN, 1f);
            res = Run(st, Spec(), bA, stub, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("radius NaN → 目标明确 skip", PMProjectileSkipReason.TargetSizeInvalid, SkipReasonOf(res, TargetA));

            PMProjectileTargetSample dead = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f);
            dead.Alive = false;
            stub.SampleValue = dead;
            res = Run(st, Spec(), bA, stub, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("alive=false → 不命中", PMProjectileSkipReason.TargetNotAlive, SkipReasonOf(res, TargetA));
            CheckEq("alive=false → 0 命中", 0, res.Hits.Length);
        }

        // ==================================================================
        //  T5A5-4 去重 / 白名单
        // ==================================================================

        private static void SectionT5A5_4_DedupAndWhitelist()
        {
            Section("T5A5-4 已命中/批内重复去重、白名单");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter filter = new RecordingFilter();
            PMProjectileState st = FlyingState();
            PMProjectileHitBatch b = Batch(st.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            st.HitTargets = new uint[] { TargetA };
            PMProjectileValidateResult res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("已命中目标不再命中", 0, res.Hits.Length);
            CheckEqEnum("原因 DuplicateAlreadyHit", PMProjectileSkipReason.DuplicateAlreadyHit, SkipReasonOf(res, TargetA));

            st.HitTargets = new uint[0];
            PMProjectileHitBatch bDup = Batch(st.Key, 0.6f, 1.0f, 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f), Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, Spec(), bDup, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("批内重复只结算一次", 1, res.Hits.Length);
            CheckEq("重复目标 skip 计数 1", 1, CountSkip(res, TargetA, PMProjectileSkipReason.DuplicateInBatch));

            st.AllowedTargets = new uint[] { TargetB };
            res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("白名单外目标不命中", 0, res.Hits.Length);
            CheckEqEnum("原因 NotInWhitelist", PMProjectileSkipReason.NotInWhitelist, SkipReasonOf(res, TargetA));

            st.AllowedTargets = new uint[] { TargetA };
            res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("白名单内目标命中", 1, res.Hits.Length);
        }

        // ==================================================================
        //  T5A5-5 Filter 收到钳制值
        // ==================================================================

        private static void SectionT5A5_5_FilterGetsClampedPoint()
        {
            Section("T5A5-5 ImpactPoint 只钳不拒：Filter 收到钳制后的点");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));
            PMProjectileState st = FlyingState();
            RecordingFilter filter = new RecordingFilter();

            // 目标中心线 y∈[-0.5,0.5]，threshold = 0.1+0.5+0.3 = 0.9；原始 ImpactPoint y=5 → 钳到 y=1.4
            PMProjectileHitBatch b = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100,
                Cand(TargetA, 1u, 10, 0f, 5f, 0f));
            PMProjectileValidateResult res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("钳制后几何仍命中 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);
            CheckEq("Filter 被调用 1 次", 1, filter.Points.Count);
            CheckEqF("Filter 收到钳制点 y=0.5+0.9=1.4", 1.4, (double)filter.Points[0].Y, 1e-4);
            CheckEqF("结算命中点即消毒值", 1.4, (double)res.Hits[0].ImpactPoint.Y, 1e-4);
            Check("原始 ImpactPoint y=5 未被直接采用", Math.Abs(filter.Points[0].Y - 5f) > 1f);

            // 过滤拒绝 → 目标 skip
            RecordingFilter reject = new RecordingFilter();
            reject.RejectList.Add(TargetA);
            res = Run(st, Spec(), b, h, reject, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("Filter 拒绝 → 目标 skip", PMProjectileSkipReason.FilterRejected, SkipReasonOf(res, TargetA));

            // 无权威 filter → 目标 skip（fail-closed，不默认放行）
            res = Run(st, Spec(), b, h, null, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("null filter → FilterUnavailable", PMProjectileSkipReason.FilterUnavailable, SkipReasonOf(res, TargetA));
        }

        // ==================================================================
        //  T5A5-6 几何
        // ==================================================================

        private static void SectionT5A5_6_Geometry()
        {
            Section("T5A5-6 几何：平行、交叉、退化点、碰边（==threshold）");

            PMProjectileSpec specSkip = Spec();
            specSkip.SkipFlyingTrajectoryValidation = true;
            PMProjectileState st = FlyingState();
            RecordingFilter filter = new RecordingFilter();
            PMVector3 prev = new PMVector3(0f, 0f, 0f);
            PMVector3 hit = new PMVector3(0f, 0f, 10f);

            // 平行：目标中心线沿 Y 位于 x=0.9，距离恰好 = threshold(0.9) → 命中（== 视为命中）
            PMProjectileHistory hEdge = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0.9f, 0f, 5f, 0.5f, 1f));
            PMProjectileHitBatch bEdge = BatchSegment(st.Key, prev, hit, 100, Cand(TargetA, 1u, 10, 0.9f, 0f, 5f));
            PMProjectileValidateResult res = Run(st, specSkip, bEdge, hEdge, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("平行且距离=threshold → 命中", 1, res.Hits.Length);

            PMProjectileHistory hMiss = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0.91f, 0f, 5f, 0.5f, 1f));
            PMProjectileHitBatch bMiss = BatchSegment(st.Key, prev, hit, 100, Cand(TargetA, 1u, 10, 0.91f, 0f, 5f));
            res = Run(st, specSkip, bMiss, hMiss, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("平行 0.91m > threshold → GeometryMiss", PMProjectileSkipReason.GeometryMiss, SkipReasonOf(res, TargetA));

            // 交叉：飞行线段沿 X 穿过目标中心线（z=5）
            PMProjectileHistory hCross = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 5f, 0.5f, 1f));
            PMProjectileHitBatch bCross = BatchSegment(st.Key, new PMVector3(-3f, 0f, 5f), new PMVector3(3f, 0f, 5f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 5f));
            res = Run(st, specSkip, bCross, hCross, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("交叉线段命中", 1, res.Hits.Length);

            // 退化中心线（halfHeight == radius → 中心线退化为点）
            PMProjectileHistory hPoint = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 5f, 0.5f, 0.5f));
            PMProjectileHitBatch bThrough = BatchSegment(st.Key, new PMVector3(0f, 0f, 4f), new PMVector3(0f, 0f, 6f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 5f));
            res = Run(st, specSkip, bThrough, hPoint, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("退化中心线（点）命中", 1, res.Hits.Length);

            // 退化子弹线段（prev == hit）
            PMProjectileHitBatch bDot = BatchSegment(st.Key, new PMVector3(0f, 0f, 5f), new PMVector3(0f, 0f, 5f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 5f));
            res = Run(st, specSkip, bDot, hPoint, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("退化子弹线段（点）命中", 1, res.Hits.Length);

            // 共线（完全平行且重叠）→ 距离 0
            PMProjectileHistory hCollinear = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            PMProjectileHitBatch bCollinear = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, specSkip, bCollinear, hCollinear, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("共线/重叠线段命中", 1, res.Hits.Length);
        }

        // ==================================================================
        //  T5A5-7 Pending / 配额 / activation / 墓碑
        // ==================================================================

        private static void SectionT5A5_7_PendingQuotaActivation()
        {
            Section("T5A5-7 Pending 不结算 / 配额 / activation0 仅 ServerDirect / 墓碑 / key 不匹配");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter filter = new RecordingFilter();
            PMProjectileState st = FlyingState();
            PMProjectileHitBatch b = Batch(st.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            PMProjectileValidateResult res = Run(st, Spec(), b, h, filter, PMActivationResult.Pending, 5000.0, 0, 0);
            CheckEqEnum("activation Pending → Pending", PMProjectileValidateStatus.Pending, res.Status);
            Check("Pending 不可结算", !res.IsSettleable);
            CheckEq("Pending 候选保留（供暂存）", 1, res.Hits.Length);
            Check("Pending 消耗配额", res.VerifyConsumed);
            CheckEqEnum("Pending 候选带 resolution", PMProjectileHistoryResolution.FrameAnchor, res.Hits[0].Resolution);

            res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            Check("Confirmed 可结算", res.IsSettleable);

            res = Run(st, Spec(), b, h, filter, PMActivationResult.Rejected, 5000.0, 0, 0);
            CheckEqEnum("activation Rejected → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 ActivationRejected", PMProjectileRejectReason.ActivationRejected, res.Reason);
            CheckEq("被拒整包无 hits", 0, res.Hits.Length);
            Check("被拒不可结算", !res.IsSettleable);

            res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 4);
            Check("used=4 → 消耗配额", res.VerifyConsumed);
            CheckEqEnum("used=4 → Confirmed", PMProjectileValidateStatus.Confirmed, res.Status);

            res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 5);
            Check("used=5 → 不消耗配额", !res.VerifyConsumed);
            CheckEqEnum("used=5 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 VerifyQuotaExhausted", PMProjectileRejectReason.VerifyQuotaExhausted, res.Reason);

            PMProjectileState cp = FlyingState();
            cp.ActivationId = 0u;
            res = Run(cp, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("ClientPredicted + activation0 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 ActivationIdZeroNotServerDirect",
                PMProjectileRejectReason.ActivationIdZeroNotServerDirect, res.Reason);

            PMProjectileState sd = FlyingState();
            sd.Key = new PMProjectileKey(Epoch, Owner, ProjId, PMProjectileOrigin.ServerDirect);
            sd.ActivationId = 0u;
            PMProjectileHitBatch bSd = Batch(sd.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(sd, Spec(), bSd, h, filter, PMActivationResult.Rejected, 5000.0, 0, 0);
            CheckEqEnum("ServerDirect + activation0 → Confirmed（可信，不看账本）",
                PMProjectileValidateStatus.Confirmed, res.Status);

            PMProjectileState tomb = FlyingState();
            tomb.Stopped = true;
            tomb.TombstoneUntilMs = 1000.0;
            PMProjectileHitBatch bTomb = Batch(tomb.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(tomb, Spec(), bTomb, h, filter, PMActivationResult.Confirmed, 1001.0, 0, 0);
            CheckEqEnum("超墓碑 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 TombstoneExpired", PMProjectileRejectReason.TombstoneExpired, res.Reason);

            res = Run(tomb, Spec(), bTomb, h, filter, PMActivationResult.Confirmed, 1000.0, 0, 0);
            Check("墓碑边界（worldNow==until）仍受理", res.Status != PMProjectileValidateStatus.Rejected);

            PMProjectileHitBatch bKey = Batch(
                new PMProjectileKey(Epoch, Owner, 99u, PMProjectileOrigin.ClientPredicted), 0.6f, 1.0f, 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            res = Run(st, Spec(), bKey, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("batch key 不匹配 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 BatchKeyMismatch", PMProjectileRejectReason.BatchKeyMismatch, res.Reason);

            PMProjectileHitBatch bEmpty = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100);
            res = Run(st, Spec(), bEmpty, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("空目标批 → Rejected", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEqEnum("原因 NoTargets", PMProjectileRejectReason.NoTargets, res.Reason);
        }

        // ==================================================================
        //  T5A5-8 无部分输出 / 不修改输入
        // ==================================================================

        private static void SectionT5A5_8_NoPartialOutputAndNoInputMutation()
        {
            Section("T5A5-8 结果深拷贝、无别名、不修改输入、无部分可结算输出");

            PMProjectileHistory h = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f),
                Sample(TargetB, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter filter = new RecordingFilter();
            PMProjectileState st = FlyingState();
            st.HitTargets = new uint[] { 0u };
            PMProjectileHitCandidate[] inputTargets = new PMProjectileHitCandidate[]
            {
                Cand(TargetA, 1u, 10, 0f, 0f, 1f),
                Cand(TargetB, 1u, 10, 0f, 0f, 1f),
            };
            PMProjectileHitBatch b = BatchSegment(st.Key, new PMVector3(0f, 0f, 0.6f), new PMVector3(0f, 0f, 1f), 100, inputTargets);

            PMProjectileValidateResult res = Run(st, Spec(), b, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("两个目标各自命中", 2, res.Hits.Length);
            Check("hits 与输入数组无别名", !ReferenceEquals(res.Hits, b.Targets));
            Check("hits 元素数组独立", !ReferenceEquals(res.Hits, inputTargets));

            float beforeZ = res.Hits[0].ImpactPoint.Z;
            inputTargets[0].ImpactPoint = new PMVector3(999f, 999f, 999f);
            inputTargets[0].TargetNetId = 12345u;
            CheckEqF("事后改输入不影响已产出结果", (double)beforeZ, (double)res.Hits[0].ImpactPoint.Z, 1e-6);

            CheckEq("state.HitTargets 长度未被改写", 1, st.HitTargets.Length);
            CheckEq("state.HitTargets 内容未被改写", 0L, (long)st.HitTargets[0]);
            CheckEq("batch.Targets 数组本身未被替换", (long)inputTargets.Length, (long)b.Targets.Length);

            // 整包被拒时不得给出任何可结算 hits
            PMProjectileHitBatch bReject = BatchSegment(st.Key, new PMVector3(0f, 0f, 0f), new PMVector3(0f, 0f, 999f), 100,
                Cand(TargetA, 1u, 10, 0f, 0f, 1f), Cand(TargetB, 1u, 10, 0f, 0f, 1f));
            res = Run(st, Spec(), bReject, h, filter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("整包拒（超长线段）", PMProjectileValidateStatus.Rejected, res.Status);
            CheckEq("被拒无 hits（不部分输出）", 0, res.Hits.Length);
            Check("被拒不可结算", !res.IsSettleable);
            CheckEq("被拒无 skip 输出", 0, res.SkippedTargets.Length);
        }

        // ==================================================================
        //  T5A2-R 对抗复核回归（第二轮独立复核新增）
        // ==================================================================

        private static void SectionT5A2R_AdversarialRegression()
        {
            Section("T5A2-R 对抗复核回归：epoch 单调 / 未定义枚举 / 时钟 / 计数口径 / 溢出 / 消毒隔离");

            // ---------------- R1 Reset 不得倒退 epoch（本轮修复的真 bug）----------------
            PMProjectileHistory hEpoch = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));
            Check("R1.1 Reset 到更小 epoch → 抛 ArgumentOutOfRangeException（拒倒退）",
                ThrowsArgumentOutOfRange(delegate { hEpoch.Reset(Epoch - 1u); }));
            CheckEq("R1.2 抛异常后 epoch 未被改写", (long)Epoch, (long)hEpoch.Epoch);
            CheckEq("R1.3 抛异常后样本仍在（未清空）", 1, hEpoch.SampleCount(TargetA));

            string r1Reason;
            Check("R1.4 倒退未生效：原 epoch 记录仍可用",
                hEpoch.Record(Sample(TargetA, 1u, 11, 21, 1100.0, 5100.0, 0f, 0f, 1f, 0.5f, 1f), out r1Reason));

            hEpoch.Reset(Epoch);
            CheckEq("R1.5 Reset 到相同 epoch 允许（等价 Clear）", (long)Epoch, (long)hEpoch.Epoch);
            CheckEq("R1.6 同 epoch Reset 清空样本", 0, hEpoch.SampleCount(TargetA));

            hEpoch.Reset(Epoch + 5u);
            CheckEq("R1.7 升 epoch 生效", (long)(Epoch + 5u), (long)hEpoch.Epoch);
            Check("R1.8 升 epoch 后旧 epoch 记录仍被拒",
                !hEpoch.Record(Sample(TargetA, 1u, 12, 22, 1200.0, 5200.0, 0f, 0f, 0f, 0.5f, 1f), out r1Reason));

            PMProjectileTargetSample r1Sample;
            PMProjectileHistoryResolution r1Res;
            Check("R1.9 升 epoch 后旧 epoch 请求仍被拒",
                !hEpoch.TryResolve(Epoch, Cand(TargetA, 1u, 10, 0f, 0f, 0f), 5200.0, 0, 0, out r1Sample, out r1Res));

            // ---------------- R2 Clear() 丢失 stream 高水位（登记的限制，不修）----------------
            PMProjectileHistory hClear = HistoryWith(
                Sample(TargetA, 3u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));
            hClear.Clear();
            CheckEq("R2.1 Clear 后样本清空", 0, hClear.SampleCount(TargetA));
            CheckEq("R2.2 Clear 保留 epoch", (long)Epoch, (long)hClear.Epoch);

            string r2Reason;
            Check("R2.3 【已知限制】Clear 后低版本 stream 记录被当新流接受（高水位已丢）",
                hClear.Record(Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f), out r2Reason));
            CheckEq("R2.4 该记录确实把 stream 版本降为 1", 1, (long)hClear.StreamVersionOf(TargetA));

            PMProjectileHistory hNoClear = HistoryWith(
                Sample(TargetA, 3u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f));
            Check("R2.5 对照：未 Clear 时同记录被 stale-stream 拒",
                !hNoClear.Record(Sample(TargetA, 1u, 11, 21, 1100.0, 5100.0, 0f, 0f, 0f, 0.5f, 1f), out r2Reason));
            CheckEqS("R2.6 拒因 stale-stream", "stale-stream", r2Reason);

            // ---------------- R3 未定义 enum 一律 fail-closed ----------------
            PMProjectileHistory hU = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            RecordingFilter uFilter = new RecordingFilter();
            PMProjectileState uState = FlyingState();
            PMProjectileHitBatch uBatch = Batch(uState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            PMProjectileValidateResult uRes = Run(uState, Spec(), uBatch, hU, uFilter,
                (PMActivationResult)99, 5000.0, 0, 0);
            CheckEqEnum("R3.1 未定义 PMActivationResult → Pending（不是 Confirmed）",
                PMProjectileValidateStatus.Pending, uRes.Status);
            Check("R3.2 未定义 activation → 不可结算", !uRes.IsSettleable);

            PMProjectileState uOrigin = FlyingState();
            uOrigin.Key = new PMProjectileKey(Epoch, Owner, ProjId, (PMProjectileOrigin)9);
            PMProjectileHitBatch uOriginBatch = Batch(uOrigin.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            uRes = Run(uOrigin, Spec(), uOriginBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R3.3 未定义 PMProjectileOrigin → Rejected", PMProjectileValidateStatus.Rejected, uRes.Status);
            CheckEqEnum("R3.4 拒因 StateKeyInvalid", PMProjectileRejectReason.StateKeyInvalid, uRes.Reason);

            string uReason;
            PMProjectileHistory hDomain = new PMProjectileHistory(Epoch);
            PMProjectileTargetSample uBad = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f);
            uBad.ServerFrame = new PMFrameId((PMFrameDomain)9, 10);
            Check("R3.5 未定义帧域作 ServerFrame → 拒", !hDomain.Record(uBad, out uReason));
            CheckEqS("R3.6 拒因 serverframe-domain", "serverframe-domain", uReason);
            uBad = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 0f, 0.5f, 1f);
            uBad.OutputFrame = new PMFrameId((PMFrameDomain)9, 20);
            Check("R3.7 未定义帧域作 OutputFrame → 拒", !hDomain.Record(uBad, out uReason));
            CheckEqS("R3.8 拒因 outputframe-domain", "outputframe-domain", uReason);

            // 错域锚：不抛异常、不退到别的世代，按契约退时间回溯
            PMProjectileHitCandidate wrongDomain = Cand(TargetA, 1u, 10, 0f, 0f, 1f);
            wrongDomain.TargetServerFrame = new PMFrameId((PMFrameDomain)9, 10);
            PMProjectileTargetSample wdSample;
            PMProjectileHistoryResolution wdRes;
            Check("R3.9 错域锚仍可查询（不抛异常）",
                hU.TryResolve(Epoch, wrongDomain, 5200.0, 300, 0, out wdSample, out wdRes));
            CheckEqEnum("R3.10 错域锚 → 退时间回溯（Rewind，而非帧锚）",
                PMProjectileHistoryResolution.Rewind, wdRes);

            // ---------------- R4 L0：账本三态 + activation0 信任边界 ----------------
            PMProjectileState l0State = FlyingState();
            PMProjectileHitBatch l0Batch = Batch(l0State.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            PMProjectileValidateResult l0Res = Run(l0State, Spec(), l0Batch, hU, uFilter,
                PMActivationResult.Rejected, 5000.0, 0, 0);
            CheckEqEnum("R4.1 非 0 激活 + 账本 Rejected → Rejected", PMProjectileValidateStatus.Rejected, l0Res.Status);
            Check("R4.2 账本 Rejected 在配额之前拒绝 → 不消耗 Verify", !l0Res.VerifyConsumed);

            l0Res = Run(l0State, Spec(), l0Batch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R4.3 非 0 激活 + 账本 Confirmed → Confirmed", PMProjectileValidateStatus.Confirmed, l0Res.Status);
            Check("R4.4 Confirmed 且有命中 → 可结算", l0Res.IsSettleable);

            l0Res = Run(l0State, Spec(), l0Batch, hU, uFilter, PMActivationResult.Pending, 5000.0, 0, 0);
            CheckEqEnum("R4.5 非 0 激活 + 账本 Pending → Pending", PMProjectileValidateStatus.Pending, l0Res.Status);

            PMProjectileState sd0 = FlyingState();
            sd0.Key = new PMProjectileKey(Epoch, Owner, ProjId, PMProjectileOrigin.ServerDirect);
            sd0.ActivationId = 0u;
            PMProjectileHitBatch sd0Batch = Batch(sd0.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            l0Res = Run(sd0, Spec(), sd0Batch, hU, uFilter, PMActivationResult.Rejected, 5000.0, 0, 0);
            CheckEqEnum("R4.6 【信任决策锁定】ServerDirect+激活0 即使账本 Rejected 仍 Confirmed",
                PMProjectileValidateStatus.Confirmed, l0Res.Status);
            CheckEq("R4.7 该口径下仍产出命中（调用方不得对 0 号传 Rejected）", 1, l0Res.Hits.Length);

            PMProjectileState sdN = FlyingState();
            sdN.Key = new PMProjectileKey(Epoch, Owner, ProjId, PMProjectileOrigin.ServerDirect);
            sdN.ActivationId = 77u;
            PMProjectileHitBatch sdNBatch = Batch(sdN.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            l0Res = Run(sdN, Spec(), sdNBatch, hU, uFilter, PMActivationResult.Pending, 5000.0, 0, 0);
            CheckEqEnum("R4.8 ServerDirect 但激活 ID 非 0 → 仍由账本裁决（Pending）",
                PMProjectileValidateStatus.Pending, l0Res.Status);

            // ---------------- R5 provider 返回错 epoch/netId/stream 必须核对 ----------------
            StubHistory stub = new StubHistory();
            PMProjectileState vState = FlyingState();
            PMProjectileHitBatch vBatch = Batch(vState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            RecordingFilter vFilter = new RecordingFilter();
            stub.SampleValue = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f);
            PMProjectileValidateResult vRes = Run(vState, Spec(), vBatch, stub, vFilter,
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("R5.1 对照：完全匹配 → 命中 1", 1, vRes.Hits.Length);
            CheckEq("R5.2 对照：filter 被调用 1 次", 1, vFilter.Points.Count);

            vFilter = new RecordingFilter();
            stub.SampleValue = WithEpoch(Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f), Epoch + 1u);
            vRes = Run(vState, Spec(), vBatch, stub, vFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R5.3 provider 返回错 epoch → StreamMismatch skip",
                PMProjectileSkipReason.StreamMismatch, SkipReasonOf(vRes, TargetA));
            CheckEq("R5.4 错 epoch → 0 命中 + filter 未被调用", 0, vRes.Hits.Length + vFilter.Points.Count);

            vFilter = new RecordingFilter();
            stub.SampleValue = Sample(TargetB, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f);
            vRes = Run(vState, Spec(), vBatch, stub, vFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R5.5 provider 返回错 netId → StreamMismatch skip",
                PMProjectileSkipReason.StreamMismatch, SkipReasonOf(vRes, TargetA));
            CheckEq("R5.6 错 netId → filter 未被调用", 0, vFilter.Points.Count);

            vFilter = new RecordingFilter();
            stub.SampleValue = Sample(TargetA, 9u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f);
            vRes = Run(vState, Spec(), vBatch, stub, vFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R5.7 provider 返回错 stream → StreamMismatch skip",
                PMProjectileSkipReason.StreamMismatch, SkipReasonOf(vRes, TargetA));
            CheckEq("R5.8 错 stream → filter 未被调用", 0, vFilter.Points.Count);

            PMProjectileHitBatch wrongStreamBatch = Batch(vState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 9u, 10, 0f, 0f, 1f));
            vRes = Run(vState, Spec(), wrongStreamBatch, hU, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R5.9 真实历史对错 stream 返回 false → 验证器只能按 HistoryUnavailable skip（诊断粒度限制）",
                PMProjectileSkipReason.HistoryUnavailable, SkipReasonOf(vRes, TargetA));

            // ---------------- R6 Verify 计数口径 ----------------
            PMProjectileState qState = FlyingState();
            PMProjectileHitBatch qBatch = Batch(qState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            PMProjectileValidateResult qRes = Run(qState, Spec(), qBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, -3);
            Check("R6.1 used=-3 视为 0 且消耗", qRes.VerifyConsumed);

            qRes = Run(qState, Spec(), qBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 4);
            Check("R6.2 used=4 消耗（第 5 次）", qRes.VerifyConsumed);

            qRes = Run(qState, Spec(), qBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 5);
            Check("R6.3 used=5 → 拒绝且不消耗（绝不出现第 6 次）", !qRes.VerifyConsumed);
            CheckEqEnum("R6.4 拒因 VerifyQuotaExhausted", PMProjectileRejectReason.VerifyQuotaExhausted, qRes.Reason);

            PMProjectileHitCandidate[] tooMany = new PMProjectileHitCandidate[101];
            for (int i = 0; i < tooMany.Length; i++)
            {
                tooMany[i] = Cand((uint)(2000 + i), 1u, 10, 0f, 0f, 1f);
            }

            PMProjectileHitBatch manyBatch = BatchSegment(qState.Key, new PMVector3(0f, 0f, 0.6f),
                new PMVector3(0f, 0f, 1f), 100, tooMany);
            qRes = Run(qState, Spec(), manyBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 3);
            Check("R6.5 目标数 >100 属结构非法 → 拒绝且不消耗配额", !qRes.VerifyConsumed);

            PMProjectileHitBatch overBudget = Batch(qState.Key, 0.6f, 5.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            qRes = Run(qState, Spec(), overBudget, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 3);
            CheckEqEnum("R6.6 L2 超预算 → Rejected", PMProjectileValidateStatus.Rejected, qRes.Status);
            Check("R6.7 L2 在配额检查之后 → 消耗（口径锁定）", qRes.VerifyConsumed);

            // ---------------- R7 时钟必须有限（本轮修复）----------------
            PMProjectileState tState = FlyingState();
            tState.Stopped = true;
            tState.TombstoneUntilMs = 1000.0;
            PMProjectileHitBatch tBatch = Batch(tState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            PMProjectileValidateResult tRes = Run(tState, Spec(), tBatch, hU, uFilter, PMActivationResult.Confirmed, 999.0, 0, 0);
            Check("R7.1 对照：墓碑窗口内（worldNow<until）受理", tRes.Status != PMProjectileValidateStatus.Rejected);

            tRes = Run(tState, Spec(), tBatch, hU, uFilter, PMActivationResult.Confirmed, double.NaN, 0, 0);
            CheckEqEnum("R7.2 WorldNowMs=NaN → Rejected（否则墓碑比较恒 false、窗口静默失效）",
                PMProjectileValidateStatus.Rejected, tRes.Status);
            CheckEqEnum("R7.3 拒因 InvalidArgument", PMProjectileRejectReason.InvalidArgument, tRes.Reason);
            Check("R7.4 非有限时钟不消耗配额", !tRes.VerifyConsumed);
            CheckEq("R7.5 非有限时钟零 hits", 0, tRes.Hits.Length);

            tRes = Run(tState, Spec(), tBatch, hU, uFilter, PMActivationResult.Confirmed, double.PositiveInfinity, 0, 0);
            CheckEqEnum("R7.6 WorldNowMs=+Inf → Rejected", PMProjectileValidateStatus.Rejected, tRes.Status);

            tRes = Run(tState, Spec(), tBatch, hU, uFilter, PMActivationResult.Confirmed, double.MaxValue, 0, 0);
            CheckEqEnum("R7.7 有限极大时钟 → 墓碑过期 Rejected（fail-closed，不静默放行）",
                PMProjectileRejectReason.TombstoneExpired, tRes.Reason);

            // ---------------- R8 墓碑语义边界：未设置(0) / 非有限 ----------------
            PMProjectileHistory hLate = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 1000000.0, 0f, 0f, 1f, 0.5f, 1f));
            PMProjectileState tZero = FlyingState();
            tZero.Stopped = true;
            tZero.TombstoneUntilMs = 0.0;
            PMProjectileHitBatch tZeroBatch = Batch(tZero.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            tRes = Run(tZero, Spec(), tZeroBatch, hLate, new RecordingFilter(), PMActivationResult.Confirmed, 1000000.0, 0, 0);
            CheckEqEnum("R8.1 TombstoneUntilMs=0 视为「未设置」→ 极晚时刻仍受理（A1 必须写真实墓碑值）",
                PMProjectileValidateStatus.Confirmed, tRes.Status);
            CheckEq("R8.2 且确实产出命中（证明窗口未施加）", 1, tRes.Hits.Length);

            PMProjectileState tNaN = FlyingState();
            tNaN.Stopped = true;
            tNaN.TombstoneUntilMs = double.NaN;
            PMProjectileHitBatch tNaNBatch = Batch(tNaN.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            // 专用于本用例的历史：避免本用例的极晚 worldNow 把共享历史裁剪掉（否则后续用例会
            // 因「历史为空」而失败，掩盖真正的断言）。
            PMProjectileHistory hNaN = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f));
            tRes = Run(tNaN, Spec(), tNaNBatch, hNaN, new RecordingFilter(), PMActivationResult.Confirmed, 1e9, 0, 0);
            Check("R8.3 非有限墓碑：只 Report 不 Reject（窗口失效但可观测）",
                HasReport(tRes, "tombstone-not-finite"));

            // ---------------- R9 float/double 溢出必须 fail-closed ----------------
            PMProjectileHistory hOver = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, -float.MaxValue, 1f, 1f, float.MaxValue));
            PMProjectileState oState = FlyingState();
            PMProjectileHitBatch oBatch = Batch(oState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, -float.MaxValue, 1f));
            PMProjectileValidateResult oRes = Run(oState, Spec(), oBatch, hOver, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("R9.1 极端半高使中心线端点溢出 → 0 命中（不外推溢出结果）", 0, oRes.Hits.Length);
            CheckEqEnum("R9.2 目标被明确 skip（NumericOverflow），不是静默通过",
                PMProjectileSkipReason.NumericOverflow, SkipReasonOf(oRes, TargetA));

            PMProjectileSpec hugeSpec = Spec();
            hugeSpec.RadiusM = float.MaxValue;
            PMProjectileState hState = FlyingState();
            PMProjectileHitBatch hBatch = Batch(hState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult hSpecRes = Run(hState, hugeSpec, hBatch, hU, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            Check("R9.3 【已知边界】spec 无上限：极大 Radius 使 threshold 极大 → 仍判命中（Spec 必须由权威提供）",
                hSpecRes.Status == PMProjectileValidateStatus.Confirmed && hSpecRes.Hits.Length == 1);

            // ---------------- R10 本层不做障碍/LOS 校验（如实锁定，不夸大）----------------
            PMProjectileHistory hWall = HistoryWith(
                Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 5f, 0.5f, 1f));
            PMProjectileSpec skipSpec = Spec();
            skipSpec.SkipFlyingTrajectoryValidation = true;
            PMProjectileState wallState = FlyingState();
            PMProjectileHitBatch wallBatch = BatchSegment(wallState.Key, new PMVector3(0f, 0f, 0f),
                new PMVector3(0f, 0f, 10f), 100, Cand(TargetA, 1u, 10, 0f, 0f, 5f));
            PMProjectileValidateResult wallRes = Run(wallState, skipSpec, wallBatch, hWall, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("R10.1 10m 线段与中心线相交 → 命中（环境无墙体/遮挡输入，故不检测隔墙）", 1, wallRes.Hits.Length);

            PMProjectileHitBatch longSeg = BatchSegment(wallState.Key, new PMVector3(0f, 0f, 0f),
                new PMVector3(0f, 0f, 20.01f), 100, Cand(TargetA, 1u, 10, 0f, 0f, 5f));
            PMProjectileValidateResult lsRes = Run(wallState, skipSpec, longSeg, hWall, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R10.2 单帧位移 >20m 会被整包拒（防伪造扫描线段的代价，非穿墙防护）",
                PMProjectileRejectReason.SegmentTooLong, lsRes.Reason);

            // ---------------- R11 白名单 / 去重顺序 ----------------
            PMProjectileState wState = FlyingState();
            PMProjectileHitBatch wBatch = Batch(wState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));

            wState.AllowedTargets = new uint[0];
            PMProjectileValidateResult wRes = Run(wState, Spec(), wBatch, hU, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEq("R11.1 空白名单 = 未设置白名单（放行）", 1, wRes.Hits.Length);

            wState.AllowedTargets = new uint[] { TargetB };
            wRes = Run(wState, Spec(), wBatch, hU, new RecordingFilter(), PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R11.2 非空白名单不含目标 → NotInWhitelist",
                PMProjectileSkipReason.NotInWhitelist, SkipReasonOf(wRes, TargetA));

            wState.AllowedTargets = new uint[0];
            wState.HitTargets = new uint[] { TargetA };
            RecordingFilter wFilter = new RecordingFilter();
            wRes = Run(wState, Spec(), wBatch, hU, wFilter, PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R11.3 HitTargets 已命中 → DuplicateAlreadyHit（先于白名单与几何）",
                PMProjectileSkipReason.DuplicateAlreadyHit, SkipReasonOf(wRes, TargetA));
            CheckEq("R11.4 已命中目标不再走 filter/几何", 0, wFilter.Points.Count);

            // ---------------- R12 纯验证无状态 + Pending 候选结算约定 ----------------
            PMProjectileState pState = FlyingState();
            PMProjectileHitBatch pBatch = Batch(pState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult p1 = Run(pState, Spec(), pBatch, hU, new RecordingFilter(),
                PMActivationResult.Pending, 5000.0, 0, 0);
            PMProjectileValidateResult p2 = Run(pState, Spec(), pBatch, hU, new RecordingFilter(),
                PMActivationResult.Pending, 5000.0, 0, 0);
            Check("R12.1 同输入两次调用结论一致（无跨调用状态）",
                p1.Status == p2.Status && p1.Hits.Length == p2.Hits.Length);
            Check("R12.2 两次调用各自消耗配额 → 确认后结算不得重跑 Validate",
                p1.VerifyConsumed && p2.VerifyConsumed);
            Check("R12.3 Pending 的 Hits 是候选且带 resolution（可直接用于确认后结算）",
                p1.Hits.Length == 1 && p1.Hits[0].Resolution == PMProjectileHistoryResolution.FrameAnchor);
            Check("R12.4 两次结果不共享实例与数组",
                !ReferenceEquals(p1, p2) && !ReferenceEquals(p1.Hits, p2.Hits));

            // ---------------- R13 批量边界：100 通过、101 拒 ----------------
            PMProjectileHitCandidate[] exactly100 = new PMProjectileHitCandidate[100];
            for (int i = 0; i < exactly100.Length; i++)
            {
                exactly100[i] = Cand((uint)(3000 + i), 1u, 10, 0f, 0f, 1f);
            }

            PMProjectileHitBatch b100 = BatchSegment(qState.Key, new PMVector3(0f, 0f, 0.6f),
                new PMVector3(0f, 0f, 1f), 100, exactly100);
            PMProjectileValidateResult r13 = Run(qState, Spec(), b100, hU, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            Check("R13.1 100 目标不触发 TargetCountExceeded", r13.Reason != PMProjectileRejectReason.TargetCountExceeded);
            CheckEq("R13.2 100 个未跟踪目标 → 100 条 HistoryUnavailable skip", 100, r13.SkippedTargets.Length);
            CheckEq("R13.3 且 0 命中", 0, r13.Hits.Length);

            // ---------------- R14 batch 身份：origin/epoch/projectileId 任一不同都要拒 ----------------
            PMProjectileState idState = FlyingState();
            PMProjectileHitBatch idBatch1 = Batch(new PMProjectileKey(Epoch, Owner, ProjId, PMProjectileOrigin.ServerDirect),
                0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            CheckEqEnum("R14.1 batch/state origin 不同 → BatchKeyMismatch", PMProjectileRejectReason.BatchKeyMismatch,
                Run(idState, Spec(), idBatch1, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 0).Reason);

            PMProjectileHitBatch idBatch2 = Batch(new PMProjectileKey(Epoch + 1u, Owner, ProjId, PMProjectileOrigin.ClientPredicted),
                0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            CheckEqEnum("R14.2 batch/state epoch 不同 → BatchKeyMismatch", PMProjectileRejectReason.BatchKeyMismatch,
                Run(idState, Spec(), idBatch2, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 0).Reason);

            PMProjectileHitBatch idBatch3 = Batch(new PMProjectileKey(Epoch, Owner, ProjId + 1u, PMProjectileOrigin.ClientPredicted),
                0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            CheckEqEnum("R14.3 batch/state projectileId 不同 → BatchKeyMismatch", PMProjectileRejectReason.BatchKeyMismatch,
                Run(idState, Spec(), idBatch3, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 0, 0).Reason);

            // ---------------- R15 Rejected 全零输出 + 数组隔离 ----------------
            PMProjectileHitBatch rejBatch = BatchSegment(idState.Key, new PMVector3(0f, 0f, 0f),
                new PMVector3(float.NaN, 0f, 1f), 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult rej1 = Run(idState, Spec(), rejBatch, hU, uFilter,
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            PMProjectileValidateResult rej2 = Run(idState, Spec(), rejBatch, hU, uFilter,
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            CheckEqEnum("R15.1 NaN 命中点 → Rejected", PMProjectileValidateStatus.Rejected, rej1.Status);
            CheckEq("R15.2 Rejected 零 hits", 0, rej1.Hits.Length);
            CheckEq("R15.3 Rejected 零 skip", 0, rej1.SkippedTargets.Length);
            Check("R15.4 Rejected 不可结算", !rej1.IsSettleable);
            Check("R15.5 两次 Rejected 不共享数组实例（无静态缓存）",
                !ReferenceEquals(rej1.Hits, rej2.Hits) && !ReferenceEquals(rej1.SkippedTargets, rej2.SkippedTargets));

            // ---------------- R16 provider 返回未定义 Resolution 不崩、不误报退化 ----------------
            stub.ResolutionValue = (PMProjectileHistoryResolution)99;
            stub.SampleValue = Sample(TargetA, 1u, 10, 20, 1000.0, 5000.0, 0f, 0f, 1f, 0.5f, 1f);
            PMProjectileValidateResult r16 = Run(vState, Spec(), vBatch, stub, new RecordingFilter(),
                PMActivationResult.Confirmed, 5000.0, 0, 0);
            Check("R16.1 provider 返回未定义 Resolution → 不崩、按帧锚语义命中", r16.Hits.Length == 1);
            Check("R16.2 未定义 Resolution 不误报 history-degraded-to-current",
                !HasReport(r16, "history-degraded-to-current"));

            // ---------------- R17 extraDefer 超 TTL 只 Report（本轮新增可观测信号）----------------
            PMProjectileHitBatch deferBatch = Batch(qState.Key, 0.6f, 1.0f, 100, Cand(TargetA, 1u, 10, 0f, 0f, 1f));
            PMProjectileValidateResult dRes = Run(qState, Spec(), deferBatch, hU, uFilter,
                PMActivationResult.Confirmed, 5000.0, 2000, 0);
            Check("R17.1 extraDefer=2000（等于 TTL）不触发异常 Report",
                !HasReport(dRes, "extra-defer-exceeds-pending-ttl"));
            dRes = Run(qState, Spec(), deferBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, 2001, 0);
            CheckEqEnum("R17.2 extraDefer=2001 仍受理（只 Report 不 Reject）", PMProjectileValidateStatus.Confirmed, dRes.Status);
            Check("R17.3 且带 extra-defer-exceeds-pending-ttl Report",
                HasReport(dRes, "extra-defer-exceeds-pending-ttl"));
            dRes = Run(qState, Spec(), deferBatch, hU, uFilter, PMActivationResult.Confirmed, 5000.0, -5, 0);
            Check("R17.4 负 extraDefer 仍按 0 处理并 Report", HasReport(dRes, "extra-defer-negative-clamped"));
        }

        private static bool ThrowsArgumentOutOfRange(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        // ==================================================================
        //  夹具与工具
        // ==================================================================

        private sealed class RecordingFilter : IPMProjectileHitFilter
        {
            public readonly List<PMVector3> Points = new List<PMVector3>();
            public readonly List<uint> Seen = new List<uint>();
            public readonly List<uint> RejectList = new List<uint>();

            public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target, PMVector3 sanitizedImpact)
            {
                Points.Add(sanitizedImpact);
                Seen.Add(target.NetId);
                return !RejectList.Contains(target.NetId);
            }
        }

        private sealed class StubHistory : IPMProjectileTargetHistory
        {
            public PMProjectileTargetSample SampleValue;
            public PMProjectileHistoryResolution ResolutionValue = PMProjectileHistoryResolution.FrameAnchor;
            public bool ReturnValue = true;

            public bool TryResolve(uint epoch, PMProjectileHitCandidate candidate, double worldNowMs,
                int rewindMs, int extraDeferMs, out PMProjectileTargetSample sample,
                out PMProjectileHistoryResolution resolution)
            {
                sample = SampleValue;
                resolution = ResolutionValue;
                return ReturnValue;
            }
        }

        private static PMFrameId Srv(long value)
        {
            return new PMFrameId(PMFrameDomain.AuthorityServer, value);
        }

        private static PMFrameId Inp(long value)
        {
            return new PMFrameId(PMFrameDomain.Input, value);
        }

        private static PMProjectileTargetSample Sample(uint netId, uint stream, long srvFrame, long outFrame,
            double totalSimMs, double worldMs, float x, float y, float z, float radius, float halfHeight)
        {
            return Sample(netId, stream, srvFrame, outFrame, totalSimMs, worldMs, x, y, z, radius, halfHeight, false, true);
        }

        private static PMProjectileTargetSample Sample(uint netId, uint stream, long srvFrame, long outFrame,
            double totalSimMs, double worldMs, float x, float y, float z, float radius, float halfHeight,
            bool teleported, bool alive)
        {
            PMProjectileTargetSample s = default(PMProjectileTargetSample);
            s.Epoch = Epoch;
            s.NetId = netId;
            s.StreamVersion = stream;
            s.ServerFrame = Srv(srvFrame);
            s.OutputFrame = Inp(outFrame);
            s.TotalSimTimeMs = totalSimMs;
            s.WorldTimeMs = worldMs;
            s.Position = new PMVector3(x, y, z);
            s.RadiusM = radius;
            s.HalfHeightM = halfHeight;
            s.Teleported = teleported;
            s.Alive = alive;
            return s;
        }

        private static PMProjectileTargetSample WithEpoch(PMProjectileTargetSample sample, uint epoch)
        {
            PMProjectileTargetSample copy = sample;
            copy.Epoch = epoch;
            return copy;
        }

        private static PMProjectileHitCandidate Cand(uint netId, uint stream, long srvFrame,
            float ix, float iy, float iz)
        {
            return Cand(netId, stream, srvFrame, ix, iy, iz, 0f, 0f, 0f);
        }

        private static PMProjectileHitCandidate Cand(uint netId, uint stream, long srvFrame,
            float ix, float iy, float iz, float vx, float vy, float vz)
        {
            PMProjectileHitCandidate c = default(PMProjectileHitCandidate);
            c.TargetNetId = netId;
            c.TargetStreamVersion = stream;
            c.TargetServerFrame = Srv(srvFrame);
            c.ImpactPoint = new PMVector3(ix, iy, iz);
            c.VisualOffset = new PMVector3(vx, vy, vz);
            return c;
        }

        private static PMProjectileState FlyingState()
        {
            PMProjectileState s = new PMProjectileState();
            s.Key = new PMProjectileKey(Epoch, Owner, ProjId, PMProjectileOrigin.ClientPredicted);
            s.AuthorityNetId = 900u;
            s.ActivationId = 123u;
            s.SpawnPosition = new PMVector3(0f, 0f, 0f);
            s.PreviousPosition = new PMVector3(0f, 0f, 0f);
            s.Position = new PMVector3(0f, 0f, 0f);
            s.Velocity = new PMVector3(0f, 0f, 10f);
            s.MoveTimeMs = 100.0;
            s.Stopped = false;
            s.HitTargets = new uint[0];
            s.AllowedTargets = new uint[0];
            return s;
        }

        private static PMProjectileSpec Spec()
        {
            PMProjectileSpec spec = new PMProjectileSpec();
            spec.SpeedMps = 10f;
            spec.RadiusM = 0.1f;
            spec.LifetimeMs = 3000;
            return spec;
        }

        private static PMProjectileHitBatch Batch(PMProjectileKey key, float prevZ, float hitZ, int rewindMs,
            params PMProjectileHitCandidate[] targets)
        {
            return BatchSegment(key, new PMVector3(0f, 0f, prevZ), new PMVector3(0f, 0f, hitZ), rewindMs, targets);
        }

        private static PMProjectileHitBatch BatchSegment(PMProjectileKey key, PMVector3 prev, PMVector3 hit,
            int rewindMs, params PMProjectileHitCandidate[] targets)
        {
            PMProjectileHitBatch b = new PMProjectileHitBatch();
            b.Key = key;
            b.PreviousPosition = prev;
            b.HitPosition = hit;
            b.RewindMs = rewindMs;
            b.Targets = targets;
            return b;
        }

        private static PMProjectileHistory HistoryWith(params PMProjectileTargetSample[] samples)
        {
            PMProjectileHistory h = new PMProjectileHistory(Epoch);
            for (int i = 0; i < samples.Length; i++)
            {
                string reason;
                if (!h.Record(samples[i], out reason))
                {
                    _failures.Add("夹具：Record 意外失败（" + reason + "）");
                }
            }

            return h;
        }

        private static PMProjectileValidateResult Run(PMProjectileState state, PMProjectileSpec spec,
            PMProjectileHitBatch batch, IPMProjectileTargetHistory history, IPMProjectileHitFilter filter,
            PMActivationResult activation, double worldNowMs, int deferMs, int usedVerifies)
        {
            return PMProjectileValidator.Validate(state, spec, activation, batch, history, filter,
                worldNowMs, deferMs, usedVerifies);
        }

        private static bool HasReport(PMProjectileValidateResult result, string prefix)
        {
            for (int i = 0; i < result.Reports.Length; i++)
            {
                if (result.Reports[i] != null
                    && result.Reports[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static PMProjectileSkipReason SkipReasonOf(PMProjectileValidateResult result, uint netId)
        {
            for (int i = 0; i < result.SkippedTargets.Length; i++)
            {
                if (result.SkippedTargets[i].TargetNetId == netId)
                {
                    return result.SkippedTargets[i].Reason;
                }
            }

            return PMProjectileSkipReason.None;
        }

        private static int CountSkip(PMProjectileValidateResult result, uint netId, PMProjectileSkipReason reason)
        {
            int count = 0;
            for (int i = 0; i < result.SkippedTargets.Length; i++)
            {
                if (result.SkippedTargets[i].TargetNetId == netId && result.SkippedTargets[i].Reason == reason)
                {
                    count++;
                }
            }

            return count;
        }

        // ------------------------------------------------------------------ 框架

        private static void Section(string title)
        {
            Console.WriteLine("---- " + title);
        }

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failures.Add(name);
            }
        }

        private static void CheckEq(string name, long expected, long actual)
        {
            Check(name + "（期望 " + expected + "，实际 " + actual + "）", expected == actual);
        }

        private static void CheckEqS(string name, string expected, string actual)
        {
            Check(name + "（期望 " + expected + "，实际 " + actual + "）", expected == actual);
        }

        private static void CheckEqF(string name, double expected, double actual, double tolerance)
        {
            Check(name + "（期望 " + expected.ToString("0.######") + "，实际 " + actual.ToString("0.######") + "）",
                Math.Abs(expected - actual) <= tolerance);
        }

        private static void CheckEqEnum<T>(string name, T expected, T actual) where T : struct
        {
            Check(name + "（期望 " + expected + "，实际 " + actual + "）", expected.Equals(actual));
        }
    }
}
