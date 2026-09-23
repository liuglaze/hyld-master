// ============================================================================
//  R5-C 验收：PMProjectileCandidateCollector（客户端候选收集器，纯 C#）
// ============================================================================
//
//  被测实现（真实源码，非替身）：
//    · Client/Assets/Scripts/PMProjectile/PMProjectileCandidateCollector.cs
//
//  覆盖：
//    A 手算几何 —— 交叉 / 擦边 / 错过 / 退化点 / 平行（含 impact 与阈值边界）
//    B 目标尺寸逐目标独立 —— 半径、半高、退化为单点的中心线
//    C 数值非法 —— NaN / ±Inf / 负半径 / 零半径 / halfHeight<radius / 超长线段 / 非法 key
//    D 身份自证 —— owner 必须是本地、origin 必须是 ClientPredicted、不能打自己、
//                   epoch / stream / 帧锚（必须 AuthorityServer，禁止拿本地 input 帧冒充）/ Alive
//    E 去重与提交语义 —— 在途不重复、Commit 后才占位、Rollback 后可以重收集
//    F 停止段 —— 只允许最后一段、提交后关闭该 key
//    G 有界性 —— 每弹 100 目标、key 数 1024、Forget/Clear 退休
//    H 批量入口与字段透传（宿主真实调用面）
//    I 跨 epoch 隔离
//    J 复审补强 —— 被拒/失败一律不占账（keys 不建、在途可退休）、退休真的释放 100 目标容量
//
//  预期值全部由独立手算给出；阈值 0.3m 是契约冻结值（PMProjectileLimits.HitToleranceM），
//  在本文件里直接写 0.3f 参与手算，而**不**引用实现自身的常量作为期望。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

namespace PMProjectileCandidateTest
{
    internal static class Program
    {
        private const uint Epoch = 7u;
        private const uint Owner = 3u;
        private const uint OtherOwner = 4u;
        private const uint TargetA = 42u;
        private const uint TargetB = 43u;
        private const float ProjRadius = 0.1f;   // 共享诊断 spec 的弹半径
        private const float Tol = 0.3f;          // 契约冻结的命中容差（米）

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R5-C：PMProjectileCandidateCollector 候选收集（纯 C#） ===");
            Console.WriteLine("预期值由独立手算给出（阈值 = 弹半径 + 目标半径 + 0.30m）。");
            Console.WriteLine();

            SectionA_Geometry();
            SectionB_PerTargetSize();
            SectionC_InvalidNumbers();
            SectionD_IdentitySelfProof();
            SectionE_DedupCommitRollback();
            SectionF_StopSegment();
            SectionG_BoundsAndRetire();
            SectionH_BulkAndPayload();
            SectionI_CrossEpoch();
            SectionJ_ReviewRegressions();

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
        //  A 手算几何
        // ==================================================================

        private static void SectionA_Geometry()
        {
            Section("A 手算几何：交叉 / 擦边 / 错过 / 退化点 / 平行");

            // 目标胶囊：中心 (0,1,0)、半径 0.4、半高 1.0 → 中心线 y ∈ [0.4, 1.6]（半长 = 1.0-0.4 = 0.6）。
            // 阈值 = 0.1（弹）+ 0.4（目标）+ 0.3（容差）= 0.80m。
            PMProjectileTargetSample t = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);

            // A1 交叉：线段穿过中心线，最近距离 0 → 命中，impact = 交点 (0,1,0)。
            PMProjectileCandidateCollector c1 = NewCollector();
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;
            bool ok = c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), t, out hit, out why);
            Check("A1 交叉：命中", ok && why == PMProjectileCandidateRejectReason.None);
            CheckEqF("A1 交叉：impact.x = 0", 0.0, hit.ImpactPoint.X, 1e-4);
            CheckEqF("A1 交叉：impact.y = 1", 1.0, hit.ImpactPoint.Y, 1e-4);
            CheckEqF("A1 交叉：impact.z = 0", 0.0, hit.ImpactPoint.Z, 1e-4);
            CheckEqF("A1 交叉：VisualOffset 恒为 0", 0.0, hit.VisualOffset.Length, 1e-9);
            CheckEq("A1 交叉：Carried NetId", (long)TargetA, (long)hit.TargetNetId);
            CheckEq("A1 交叉：Carried StreamVersion", 3L, (long)hit.TargetStreamVersion);
            CheckEq("A1 交叉：Carried ServerFrame.Value", 100L, hit.TargetServerFrame.Value);

            // A2 擦边：整段沿 z = 0.7 穿过，最近距离 0.7 < 0.80 → 命中；impact = (0,1,0.7)。
            PMProjectileCandidateCollector c2 = NewCollector();
            ok = c2.TryCollect(Segment(-2f, 1f, 0.7f, 2f, 1f, 0.7f, 11u), t, out hit, out why);
            Check("A2 擦边 0.70 < 0.80：命中", ok && why == PMProjectileCandidateRejectReason.None);
            CheckEqF("A2 擦边：impact.x = 0", 0.0, hit.ImpactPoint.X, 1e-4);
            CheckEqF("A2 擦边：impact.y = 1", 1.0, hit.ImpactPoint.Y, 1e-4);
            CheckEqF("A2 擦边：impact.z = 0.7", 0.7, hit.ImpactPoint.Z, 1e-4);

            // A3 擦边外 0.9 > 0.80 → 未命中（GeometryMiss，不是错误）。
            PMProjectileCandidateCollector c3 = NewCollector();
            ok = c3.TryCollect(Segment(-2f, 1f, 0.9f, 2f, 1f, 0.9f, 11u), t, out hit, out why);
            Check("A3 0.90 > 0.80：GeometryMiss", !ok && why == PMProjectileCandidateRejectReason.GeometryMiss);

            // A4 错过：整段远离目标 → GeometryMiss。
            PMProjectileCandidateCollector c4 = NewCollector();
            ok = c4.TryCollect(Segment(-2f, 10f, 5f, 2f, 10f, 5f, 11u), t, out hit, out why);
            Check("A4 远处线段：GeometryMiss", !ok && why == PMProjectileCandidateRejectReason.GeometryMiss);

            // A5 退化点（弹静止）：线段退化为点 (0,1,0.7)，距离 0.7 ≤ 0.80 → 命中；impact = 该点。
            PMProjectileCandidateCollector c5 = NewCollector();
            ok = c5.TryCollect(Segment(0f, 1f, 0.7f, 0f, 1f, 0.7f, 11u), t, out hit, out why);
            Check("A5 退化点 0.70：命中", ok && why == PMProjectileCandidateRejectReason.None);
            CheckEqF("A5 退化点：impact = (0,1,0.7)", 0.7, hit.ImpactPoint.Z, 1e-4);
            CheckEqF("A5 退化点：impact.y = 1", 1.0, hit.ImpactPoint.Y, 1e-4);

            // A6 退化点在阈值外 0.81 > 0.80 → GeometryMiss。
            PMProjectileCandidateCollector c6 = NewCollector();
            ok = c6.TryCollect(Segment(0f, 1f, 0.81f, 0f, 1f, 0.81f, 11u), t, out hit, out why);
            Check("A6 退化点 0.81：GeometryMiss", !ok && why == PMProjectileCandidateRejectReason.GeometryMiss);

            // A7 平行：弹线段 x = 0.7 竖直平行于中心线，最近距离 0.7 ≤ 0.80 → 命中。
            //     手算（Ericson，denom = a*e - b² = 0 → s 固定 0 → t 钳到 0 → s = 0.2）：
            //     impact = (0.7, 0.4, 0)（即中心线最下端点正侧方的最近点）。
            PMProjectileCandidateCollector c7 = NewCollector();
            ok = c7.TryCollect(Segment(0.7f, 0f, 0f, 0.7f, 2f, 0f, 11u), t, out hit, out why);
            Check("A7 平行 0.70：命中", ok && why == PMProjectileCandidateRejectReason.None);
            CheckEqF("A7 平行：impact.x = 0.7", 0.7, hit.ImpactPoint.X, 1e-4);
            CheckEqF("A7 平行：impact.y = 0.4", 0.4, hit.ImpactPoint.Y, 1e-4);
            CheckEqF("A7 平行：impact.z = 0", 0.0, hit.ImpactPoint.Z, 1e-4);

            // A8 平行但在阈值外（x = 0.9 > 0.80）→ GeometryMiss。
            PMProjectileCandidateCollector c8 = NewCollector();
            ok = c8.TryCollect(Segment(0.9f, 0f, 0f, 0.9f, 2f, 0f, 11u), t, out hit, out why);
            Check("A8 平行 0.90：GeometryMiss", !ok && why == PMProjectileCandidateRejectReason.GeometryMiss);

            // A9 阈值随弹半径变化：弹半径 0.3 → 阈值 0.3+0.4+0.3 = 1.00 → 0.90 的平行段变成命中。
            PMProjectileCandidateCollector c9 = NewCollector();
            ok = c9.TryCollect(Segment(0.9f, 0f, 0f, 0.9f, 2f, 0f, 11u, projectileRadius: 0.3f), t, out hit, out why);
            Check("A9 弹半径 0.3（阈值 1.00）→ 0.90 命中", ok && why == PMProjectileCandidateRejectReason.None);
        }

        // ==================================================================
        //  B 目标尺寸逐目标独立
        // ==================================================================

        private static void SectionB_PerTargetSize()
        {
            Section("B 目标尺寸逐目标独立：半径 / 半高 / 退化为单点的中心线");

            // B1 同一线段 z = 0.6 对同一位置的两个目标：
            //    A（半径 0.4）阈值 = 0.1+0.4+0.3 = 0.80 → 命中；
            //    B（半径 0.05）阈值 = 0.1+0.05+0.3 = 0.45 → 未命中。
            PMProjectileTargetSample big = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileTargetSample small = Target(TargetB, 0f, 1f, 0f, 0.05f, 0.5f);

            PMProjectileCandidateCollector c1 = NewCollector();
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;
            bool okBig = c1.TryCollect(Segment(-2f, 1f, 0.6f, 2f, 1f, 0.6f, 11u), big, out hit, out why);
            Check("B1 大目标（阈值 0.80）在 0.60：命中", okBig);

            PMProjectileCandidateCollector c2 = NewCollector();
            bool okSmall = c2.TryCollect(Segment(-2f, 1f, 0.6f, 2f, 1f, 0.6f, 11u), small, out hit, out why);
            Check("B1 小目标（阈值 0.45）在 0.60：GeometryMiss",
                !okSmall && why == PMProjectileCandidateRejectReason.GeometryMiss);

            // B2 半高独立：同一水平线段 y = 3：
            //    A（半高 1.0 → 中心线到 y=1.6）距离 1.4 > 0.80 → 未命中；
            //    D（半高 2.5 → 中心线到 y=3.1）距离 0 → 命中。
            PMProjectileTargetSample tall = Target(TargetB, 0f, 1f, 0f, 0.4f, 2.5f);

            PMProjectileCandidateCollector c3 = NewCollector();
            bool okShort = c3.TryCollect(Segment(-2f, 3f, 0f, 2f, 3f, 0f, 11u), big, out hit, out why);
            Check("B2 半高 1.0 的胶囊在 y=3：GeometryMiss",
                !okShort && why == PMProjectileCandidateRejectReason.GeometryMiss);

            PMProjectileCandidateCollector c4 = NewCollector();
            bool okTall = c4.TryCollect(Segment(-2f, 3f, 0f, 2f, 3f, 0f, 11u), tall, out hit, out why);
            Check("B2 半高 2.5 的胶囊在 y=3：命中", okTall);

            // B3 中心线退化为单点（halfHeight == radius → 半长 0）：
            //    距离 0.3 ≤ 0.80 → 命中；impact = (0,1,0.3)。
            PMProjectileTargetSample dot = Target(TargetB, 0f, 1f, 0f, 0.4f, 0.4f);
            PMProjectileCandidateCollector c5 = NewCollector();
            bool okDot = c5.TryCollect(Segment(0f, 1f, 0.3f, 0f, 1f, 0.3f, 11u), dot, out hit, out why);
            Check("B3 单点中心线：命中", okDot);
            CheckEqF("B3 单点中心线：impact.z = 0.3", 0.3, hit.ImpactPoint.Z, 1e-4);
        }

        // ==================================================================
        //  C 数值非法
        // ==================================================================

        private static void SectionC_InvalidNumbers()
        {
            Section("C 数值非法：NaN / ±Inf / 尺寸非法 / 超长线段 / 非法 key");

            PMProjectileTargetSample t = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);

            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;

            // C1 弹线段端点 NaN / Inf。
            PMProjectileCandidateCollector c1 = NewCollector();
            bool ok = c1.TryCollect(Segment(float.NaN, 1f, 0f, 2f, 1f, 0f, 11u), t, out hit, out why);
            Check("C1 线段起点 NaN：SegmentNotFinite",
                !ok && why == PMProjectileCandidateRejectReason.SegmentNotFinite);

            PMProjectileCandidateCollector c2 = NewCollector();
            ok = c2.TryCollect(Segment(-2f, 1f, 0f, float.PositiveInfinity, 1f, 0f, 11u), t, out hit, out why);
            Check("C1 线段终点 +Inf：SegmentNotFinite",
                !ok && why == PMProjectileCandidateRejectReason.SegmentNotFinite);

            // C2 弹半径非法。
            PMProjectileCandidateCollector c3 = NewCollector();
            ok = c3.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u, projectileRadius: float.NaN), t, out hit, out why);
            Check("C2 弹半径 NaN：SegmentRadiusInvalid",
                !ok && why == PMProjectileCandidateRejectReason.SegmentRadiusInvalid);

            PMProjectileCandidateCollector c4 = NewCollector();
            ok = c4.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u, projectileRadius: -0.5f), t, out hit, out why);
            Check("C2 弹半径 -0.5：SegmentRadiusInvalid",
                !ok && why == PMProjectileCandidateRejectReason.SegmentRadiusInvalid);

            // C3 目标位置非有限。
            PMProjectileTargetSample badPos = Target(TargetA, float.NaN, 1f, 0f, 0.4f, 1f);
            PMProjectileCandidateCollector c5 = NewCollector();
            ok = c5.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badPos, out hit, out why);
            Check("C3 目标位置 NaN：TargetPositionNotFinite",
                !ok && why == PMProjectileCandidateRejectReason.TargetPositionNotFinite);

            // C4 目标尺寸非法：radius NaN / radius 0 / radius 负 / halfHeight < radius。
            PMProjectileTargetSample badSize = Target(TargetA, 0f, 1f, 0f, float.NaN, 1f);
            PMProjectileCandidateCollector c6 = NewCollector();
            ok = c6.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badSize, out hit, out why);
            Check("C4 目标半径 NaN：TargetSizeInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetSizeInvalid);

            badSize = Target(TargetA, 0f, 1f, 0f, 0f, 1f);
            PMProjectileCandidateCollector c7 = NewCollector();
            ok = c7.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badSize, out hit, out why);
            Check("C4 目标半径 0：TargetSizeInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetSizeInvalid);

            badSize = Target(TargetA, 0f, 1f, 0f, 0.4f, 0.2f);
            PMProjectileCandidateCollector c8 = NewCollector();
            ok = c8.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badSize, out hit, out why);
            Check("C4 halfHeight(0.2) < radius(0.4)：TargetSizeInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetSizeInvalid);

            // C5 超长线段（> MaxSegmentM = 20m）：本地先拒，避免让 DS 整包 Reject。
            PMProjectileCandidateCollector c9 = NewCollector();
            ok = c9.TryCollect(Segment(0f, 1f, 0f, 25f, 1f, 0f, 11u), t, out hit, out why);
            Check("C5 25m 线段：SegmentTooLong", !ok && why == PMProjectileCandidateRejectReason.SegmentTooLong);

            // C6 非法 key（default → epoch/owner/projectileId 全 0）。
            PMProjectileCandidateSegment badKey = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            badKey.Key = default(PMProjectileKey);
            PMProjectileCandidateCollector c10 = NewCollector();
            ok = c10.TryCollect(badKey, t, out hit, out why);
            Check("C6 非法 key：SegmentKeyInvalid",
                !ok && why == PMProjectileCandidateRejectReason.SegmentKeyInvalid);

            // C7 被拒调用不留下任何账。
            CheckEq("C7 被拒不建账：KeyCount = 0", 0L, c10.KeyCount);
        }

        // ==================================================================
        //  D 身份自证
        // ==================================================================

        private static void SectionD_IdentitySelfProof()
        {
            Section("D 身份自证：owner/origin/self/epoch/stream/帧锚/Alive");

            PMProjectileTargetSample t = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;

            // D1 别人的弹（SP 视角）不上报。
            PMProjectileCandidateSegment other = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            other.Key = new PMProjectileKey(Epoch, OtherOwner, 11u, PMProjectileOrigin.ClientPredicted);
            PMProjectileCandidateCollector c1 = NewCollector();
            bool ok = c1.TryCollect(other, t, out hit, out why);
            Check("D1 非本地 owner：SegmentOwnerNotLocal",
                !ok && why == PMProjectileCandidateRejectReason.SegmentOwnerNotLocal);

            // D2 权威直创的弹由 DS 自己判定，客户端不上报。
            PMProjectileCandidateSegment direct = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            direct.Key = new PMProjectileKey(Epoch, Owner, 11u, PMProjectileOrigin.ServerDirect);
            PMProjectileCandidateCollector c2 = NewCollector();
            ok = c2.TryCollect(direct, t, out hit, out why);
            Check("D2 ServerDirect：SegmentOriginNotPredicted",
                !ok && why == PMProjectileCandidateRejectReason.SegmentOriginNotPredicted);

            // D3 不能打自己。
            PMProjectileTargetSample self = Target(Owner, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileCandidateCollector c3 = NewCollector();
            ok = c3.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), self, out hit, out why);
            Check("D3 目标是本地 owner：TargetIsOwner",
                !ok && why == PMProjectileCandidateRejectReason.TargetIsOwner);

            // D4 弹 epoch 与会话不一致。
            PMProjectileCandidateSegment otherEpoch = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            otherEpoch.Key = new PMProjectileKey(Epoch + 1u, Owner, 11u, PMProjectileOrigin.ClientPredicted);
            PMProjectileCandidateCollector c4 = NewCollector();
            ok = c4.TryCollect(otherEpoch, t, out hit, out why);
            Check("D4 弹 epoch 不一致：SegmentEpochMismatch",
                !ok && why == PMProjectileCandidateRejectReason.SegmentEpochMismatch);

            // D5 目标 NetId 为 0。
            PMProjectileTargetSample zero = Target(0u, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileCandidateCollector c5 = NewCollector();
            ok = c5.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), zero, out hit, out why);
            Check("D5 目标 NetId 0：TargetIdInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetIdInvalid);

            // D6 目标 epoch 不一致。
            PMProjectileTargetSample badEpoch = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            badEpoch.Epoch = Epoch + 1u;
            PMProjectileCandidateCollector c6 = NewCollector();
            ok = c6.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badEpoch, out hit, out why);
            Check("D6 目标 epoch 不一致：TargetEpochMismatch",
                !ok && why == PMProjectileCandidateRejectReason.TargetEpochMismatch);

            // D7 目标 stream 为 0。
            PMProjectileTargetSample badStream = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f, 0u);
            PMProjectileCandidateCollector c7 = NewCollector();
            ok = c7.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), badStream, out hit, out why);
            Check("D7 目标 stream 0：TargetStreamInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetStreamInvalid);

            // D8 帧锚不是 AuthorityServer（**本地 input 帧冒充权威帧**的判据）。
            PMProjectileTargetSample localFrame = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            localFrame.ServerFrame = new PMFrameId(PMFrameDomain.Input, 100L);
            PMProjectileCandidateCollector c8 = NewCollector();
            ok = c8.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), localFrame, out hit, out why);
            Check("D8 帧锚是 Input 域：TargetServerFrameInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetServerFrameInvalid);

            PMProjectileTargetSample noFrame = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            noFrame.ServerFrame = PMFrameId.None;
            PMProjectileCandidateCollector c8b = NewCollector();
            ok = c8b.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), noFrame, out hit, out why);
            Check("D8 无帧锚：TargetServerFrameInvalid",
                !ok && why == PMProjectileCandidateRejectReason.TargetServerFrameInvalid);

            // D9 目标已不可命中。
            PMProjectileTargetSample dead = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            dead.Alive = false;
            PMProjectileCandidateCollector c9 = NewCollector();
            ok = c9.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), dead, out hit, out why);
            Check("D9 目标 Alive=false：TargetNotAlive",
                !ok && why == PMProjectileCandidateRejectReason.TargetNotAlive);

            // D10 被拒调用一律不建账。
            CheckEq("D10 全部被拒后 KeyCount = 0", 0L, c9.KeyCount);
        }

        // ==================================================================
        //  E 去重与提交语义
        // ==================================================================

        private static void SectionE_DedupCommitRollback()
        {
            Section("E 去重与提交语义：在途不重复、Commit 后才占位、Rollback 后可重收集");

            PMProjectileTargetSample a = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileTargetSample b = Target(TargetB, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileCandidateSegment seg = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);

            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;

            // E1 同一 (弹, 目标) 在途期间重复收集 → AlreadyPending（未提交也**不重复**上报）。
            PMProjectileCandidateCollector c1 = NewCollector();
            bool ok1 = c1.TryCollect(seg, a, out hit, out why);
            bool ok2 = c1.TryCollect(seg, a, out hit, out why);
            Check("E1 首次收集：成功", ok1);
            Check("E1 同目标重复收集：AlreadyPending",
                !ok2 && why == PMProjectileCandidateRejectReason.AlreadyPending);
            CheckEq("E1 在途计数 = 1", 1L, c1.PendingTargetCount(seg.Key));
            Check("E1 HasPending = true", c1.HasPending(seg.Key));
            CheckEq("E1 未提交：CommittedTargetCount = 0", 0L, c1.CommittedTargetCount(seg.Key));

            // E2 不同目标不受影响（逐目标独立去重）。
            PMProjectileCandidateCollector c2 = NewCollector();
            c2.TryCollect(seg, a, out hit, out why);
            bool okB = c2.TryCollect(seg, b, out hit, out why);
            Check("E2 同弹不同目标：可收集", okB);
            CheckEq("E2 在途计数 = 2", 2L, c2.PendingTargetCount(seg.Key));

            // E3 Commit 之后该 (弹, 目标) 才被真正占位。
            PMProjectileCandidateCollector c3 = NewCollector();
            c3.TryCollect(seg, a, out hit, out why);
            bool committed = c3.Commit(seg.Key);
            Check("E3 Commit 返回 true（确有在途）", committed);
            CheckEq("E3 Commit 后 CommittedTargetCount = 1", 1L, c3.CommittedTargetCount(seg.Key));
            CheckEq("E3 Commit 后在途清零", 0L, c3.PendingTargetCount(seg.Key));

            PMProjectileCandidateCollector c3b = NewCollector();
            c3b.TryCollect(seg, a, out hit, out why);
            c3b.Commit(seg.Key);
            bool again = c3b.TryCollect(seg, a, out hit, out why);
            Check("E3 已提交后重复收集：AlreadyCommitted",
                !again && why == PMProjectileCandidateRejectReason.AlreadyCommitted);

            // E4 「失败发送不能永久预占去重」：Rollback 之后同一 (弹, 目标) 可以重新收集。
            PMProjectileCandidateCollector c4 = NewCollector();
            c4.TryCollect(seg, a, out hit, out why);
            bool rolled = c4.Rollback(seg.Key);
            Check("E4 Rollback 返回 true（确有在途）", rolled);
            CheckEq("E4 Rollback 后在途清零", 0L, c4.PendingTargetCount(seg.Key));
            bool retry = c4.TryCollect(seg, a, out hit, out why);
            Check("E4 Rollback 后可重新收集（未被永久占位）", retry);
            CheckEq("E4 Rollback 后 CommittedTargetCount 仍为 0", 0L, c4.CommittedTargetCount(seg.Key));

            // E5 Rollback / Commit 在无在途时返回 false。
            PMProjectileCandidateCollector c5 = NewCollector();
            Check("E5 从未收集：Commit = false", !c5.Commit(seg.Key));
            Check("E5 从未收集：Rollback = false", !c5.Rollback(seg.Key));

            // E6 「镜像接管后 LocalFake=false 也要继续报」的语义基础：收集器**不接收**
            //     任何「接管状态」输入，只有 owner + origin + 去重账决定是否上报。
            //     因此同一弹在已提交目标 A 之后，仍能收集新目标 B。
            PMProjectileCandidateCollector c6 = NewCollector();
            c6.TryCollect(seg, a, out hit, out why);
            c6.Commit(seg.Key);
            bool laterB = c6.TryCollect(seg, b, out hit, out why);
            Check("E6 已提交 A 后仍可对同一弹收集新目标 B（不因接管停报）", laterB);

            // E7 CopyPending 按收集顺序拷出，且不改动在途集合。
            PMProjectileCandidateCollector c7 = NewCollector();
            c7.TryCollect(seg, a, out hit, out why);
            c7.TryCollect(seg, b, out hit, out why);
            PMProjectileHitCandidate[] buffer = new PMProjectileHitCandidate[4];
            int copied = c7.CopyPending(seg.Key, buffer);
            CheckEq("E7 CopyPending 条数 = 2", 2L, copied);
            CheckEq("E7 CopyPending[0] = 目标 A", (long)TargetA, (long)buffer[0].TargetNetId);
            CheckEq("E7 CopyPending[1] = 目标 B", (long)TargetB, (long)buffer[1].TargetNetId);
            CheckEq("E7 CopyPending 不改动在途", 2L, c7.PendingTargetCount(seg.Key));
        }

        // ==================================================================
        //  F 停止段
        // ==================================================================

        private static void SectionF_StopSegment()
        {
            Section("F 停止段：只允许最后一段、提交后关闭该 key、回滚可重来");

            PMProjectileTargetSample a = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileTargetSample b = Target(TargetB, 0f, 1f, 0f, 0.4f, 1f);

            PMProjectileCandidateSegment stopped = Segment(-2f, 1f, 0f, 0f, 1f, 0f, 11u, stopped: true);
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;

            // F1 停止段本身可以收集（**不**因为停止就完全不上报）。
            PMProjectileCandidateCollector c1 = NewCollector();
            bool ok = c1.TryCollect(stopped, a, out hit, out why);
            Check("F1 停止段：可收集（不是直接丢弃）", ok);
            CheckEqF("F1 停止段 impact.x = 0", 0.0, hit.ImpactPoint.X, 1e-4);

            // F2 提交之后该 key 完全关闭：连**新目标**也不再接受（无无界重复）。
            c1.Commit(stopped.Key);
            bool afterA = c1.TryCollect(stopped, a, out hit, out why);
            Check("F2 停止段提交后同目标：StopSegmentAlreadyReported",
                !afterA && why == PMProjectileCandidateRejectReason.StopSegmentAlreadyReported);
            bool afterB = c1.TryCollect(stopped, b, out hit, out why);
            Check("F2 停止段提交后新目标：StopSegmentAlreadyReported",
                !afterB && why == PMProjectileCandidateRejectReason.StopSegmentAlreadyReported);

            // F3 提交前，同一次停止段仍可带多个目标（同一子步的命中批）。
            PMProjectileCandidateCollector c3 = NewCollector();
            c3.TryCollect(stopped, a, out hit, out why);
            bool okB = c3.TryCollect(stopped, b, out hit, out why);
            Check("F3 提交前可同段带多个目标", okB);

            // F4 停止段被回滚后可以重来（失败发送不落永久标记）。
            PMProjectileCandidateCollector c4 = NewCollector();
            c4.TryCollect(stopped, a, out hit, out why);
            c4.Rollback(stopped.Key);
            bool retry = c4.TryCollect(stopped, a, out hit, out why);
            Check("F4 停止段回滚后可重收集", retry);

            // F5 非停止段不受「收官」限制：提交后不同目标仍可收集（与 F2 对照）。
            PMProjectileCandidateCollector c5 = NewCollector();
            PMProjectileCandidateSegment flying = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 12u);
            c5.TryCollect(flying, a, out hit, out why);
            c5.Commit(flying.Key);
            bool laterB = c5.TryCollect(flying, b, out hit, out why);
            Check("F5 非停止段提交后仍可收集新目标", laterB);
        }

        // ==================================================================
        //  G 有界性与退休
        // ==================================================================

        private static void SectionG_BoundsAndRetire()
        {
            Section("G 有界性：每弹 100 目标、key 数 1024、Forget/Clear 退休");

            PMProjectileCandidateSegment seg = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;

            // G1 每弹最多记住 100 个目标（已提交 + 在途）。
            PMProjectileCandidateCollector c1 = NewCollector();
            int accepted = 0;
            PMProjectileCandidateRejectReason lastReason = PMProjectileCandidateRejectReason.None;
            for (int i = 0; i < 101; i++)
            {
                if (c1.TryCollect(seg, Target(2000u + (uint)i, 0f, 1f, 0f, 0.4f, 1f), out hit, out why))
                {
                    accepted++;
                }
                else
                {
                    lastReason = why;
                }
            }

            CheckEq("G1 每弹接受 100 个目标", 100L, accepted);
            Check("G1 第 101 个目标：TargetCapacityExceeded",
                lastReason == PMProjectileCandidateRejectReason.TargetCapacityExceeded);
            CheckEq("G1 在途计数封顶 100", 100L, c1.PendingTargetCount(seg.Key));

            // G2 回滚后容量释放（去重账不因失败永久占位）。
            c1.Rollback(seg.Key);
            bool okAfterRollback = c1.TryCollect(seg, Target(3001u, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            Check("G2 回滚后容量释放：可再次收集", okAfterRollback);

            // G3 key 数上界 1024：第 1025 个 key 拒新（每 key 都真的产生过一条在途候选）。
            PMProjectileCandidateCollector c3 = NewCollector();
            PMProjectileTargetSample anyTarget = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            int keysAccepted = 0;
            PMProjectileCandidateRejectReason overflowReason = PMProjectileCandidateRejectReason.None;
            for (int i = 0; i < 1025; i++)
            {
                PMProjectileCandidateSegment s = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 5000u + (uint)i);
                if (c3.TryCollect(s, anyTarget, out hit, out overflowReason)) { keysAccepted++; }
            }

            CheckEq("G3 接受 1024 个 key", 1024L, keysAccepted);
            CheckEq("G3 KeyCount = 1024", 1024L, c3.KeyCount);
            Check("G3 第 1025 个 key：KeyCapacityExceeded",
                overflowReason == PMProjectileCandidateRejectReason.KeyCapacityExceeded);

            // G4 已存在的 key 不受容量限制（仍可写新目标）。
            PMProjectileCandidateSegment existing = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 5000u);
            bool existingOk = c3.TryCollect(existing, Target(TargetB, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            Check("G4 已存在 key 在容量满时仍可写新目标", existingOk);

            // G5 Forget 退休单个 key（视图移除路径）。
            PMProjectileCandidateCollector c5 = NewCollector();
            PMProjectileCandidateSegment k = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 21u);
            c5.TryCollect(k, Target(TargetA, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            c5.Commit(k.Key);
            CheckEq("G5 Forget 前已提交 1 个", 1L, c5.CommittedTargetCount(k.Key));
            bool forgotten = c5.Forget(k.Key);
            Check("G5 Forget 返回 true", forgotten);
            CheckEq("G5 Forget 后 KeyCount = 0", 0L, c5.KeyCount);
            CheckEq("G5 Forget 后已提交计数归零", 0L, c5.CommittedTargetCount(k.Key));

            // G6 Forget 之后重新收集是允许的（去重从零开始；重复由 DS 的 HitTargets 兜底）。
            bool recollect = c5.TryCollect(k, Target(TargetA, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            Check("G6 Forget 后可重新收集同一 (弹, 目标)", recollect);

            // G7 Forget 不存在的 key 返回 false。
            Check("G7 Forget 未知 key = false", !c5.Forget(new PMProjectileKey(Epoch, Owner, 999u, PMProjectileOrigin.ClientPredicted)));

            // G8 Clear 清空全部账（会话退出）。
            c5.Clear();
            CheckEq("G8 Clear 后 KeyCount = 0", 0L, c5.KeyCount);
            CheckEq("G8 Clear 后 PendingKeyCount = 0", 0L, c5.PendingKeyCount);

            // G9 可观测计数只增不减，且 Describe 可读。
            //     注意：Rejected 要用一个**确实产生过拒绝**的收集器来断言（c5 全是成功收集）。
            Check("G9 Collected > 0", c5.Collected > 0);
            Check("G9 Rejected > 0（c3 拒绝过第 1025 个 key）", c3.Rejected > 0);
            Check("G9 Describe 非空且含 keys=", c5.Describe() != null && c5.Describe().Contains("keys="));
        }

        // ==================================================================
        //  H 批量入口与字段透传
        // ==================================================================

        private static void SectionH_BulkAndPayload()
        {
            Section("H 批量入口（宿主真实调用面）与字段透传");

            PMProjectileCandidateSegment seg = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            PMProjectileTargetSample near = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileTargetSample far = Target(TargetB, 0f, 1f, 20f, 0.4f, 1f);
            PMProjectileTargetSample self = Target(Owner, 0f, 1f, 0f, 0.4f, 1f);

            PMProjectileCandidateCollector c1 = NewCollector();

            List<PMProjectileTargetSample> targets = new List<PMProjectileTargetSample>();
            targets.Add(near);
            targets.Add(far);
            targets.Add(self);

            List<PMProjectileHitCandidate> results = new List<PMProjectileHitCandidate>();
            int collected = c1.Collect(seg, targets, results);

            CheckEq("H1 批量：只收 1 条（近命中；远错过；自己排除）", 1L, collected);
            CheckEq("H1 批量：结果条数一致", 1L, results.Count);
            CheckEq("H1 批量：命中目标是 A", (long)TargetA, (long)results[0].TargetNetId);

            // H2 批量入口对空输入安全。
            CheckEq("H2 空目标数组：0 条", 0L, c1.Collect(seg, new PMProjectileTargetSample[0], new List<PMProjectileHitCandidate>()));
            CheckEq("H2 null 目标：0 条", 0L, c1.Collect(seg, (IList<PMProjectileTargetSample>)null, new List<PMProjectileHitCandidate>()));
            CheckEq("H2 null 结果：0 条", 0L, c1.Collect(seg, targets, null));

            // H3 字段透传：StreamVersion / ServerFrame 原样进入候选（DS 依此解析同流 + 帧锚）。
            PMProjectileCandidateCollector c3 = NewCollector();
            PMProjectileTargetSample sample = Target(TargetB, 0f, 1f, 0f, 0.4f, 1f, 9u, 4242L);
            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;
            bool ok = c3.TryCollect(seg, sample, out hit, out why);
            Check("H3 透传：收集成功", ok);
            CheckEq("H3 透传 StreamVersion = 9", 9L, (long)hit.TargetStreamVersion);
            CheckEq("H3 透传 ServerFrame = 4242", 4242L, hit.TargetServerFrame.Value);
            CheckEqEnum("H3 透传 ServerFrame 域", PMFrameDomain.AuthorityServer, hit.TargetServerFrame.Domain);
            CheckEq("H3 透传 TargetNetId", (long)TargetB, (long)hit.TargetNetId);
            CheckEqF("H3 透传 VisualOffset = 0", 0.0, hit.VisualOffset.Length, 1e-9);

            // H4 弹半径变大不会漏掉原来命中的目标（阈值单调）。
            PMProjectileCandidateCollector c4 = NewCollector();
            ok = c4.TryCollect(Segment(-2f, 1f, 0.9f, 2f, 1f, 0.9f, 11u, projectileRadius: 0.5f), near, out hit, out why);
            Check("H4 弹半径 0.5（阈值 1.20）→ 0.90 命中", ok);
        }

        // ==================================================================
        //  I 跨 epoch 隔离
        // ==================================================================

        private static void SectionI_CrossEpoch()
        {
            Section("I 跨 epoch 隔离：每个收集器只认自己的 epoch");

            PMProjectileTargetSample t7 = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            PMProjectileTargetSample t8 = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            t8.Epoch = Epoch + 1u;

            PMProjectileCandidateSegment seg7 = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            PMProjectileCandidateSegment seg8 = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            seg8.Key = new PMProjectileKey(Epoch + 1u, Owner, 11u, PMProjectileOrigin.ClientPredicted);

            PMProjectileCandidateCollector c7 = NewCollector();
            PMProjectileCandidateCollector c8 = new PMProjectileCandidateCollector(Epoch + 1u, Owner);

            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;
            bool ok7 = c7.TryCollect(seg7, t7, out hit, out why);
            bool ok8 = c8.TryCollect(seg8, t8, out hit, out why);
            Check("I1 epoch 7 收集器接受 epoch 7", ok7);
            Check("I1 epoch 8 收集器接受 epoch 8", ok8);

            bool crossA = c7.TryCollect(seg8, t8, out hit, out why);
            Check("I2 epoch 7 收集器拒绝 epoch 8 的弹", !crossA && why == PMProjectileCandidateRejectReason.SegmentEpochMismatch);

            bool crossB = c7.TryCollect(seg7, t8, out hit, out why);
            Check("I3 epoch 7 收集器拒绝 epoch 8 的目标", !crossB && why == PMProjectileCandidateRejectReason.TargetEpochMismatch);

            CheckEq("I4 两个收集器各自独立计数", 1L, c8.KeyCount);
            CheckEq("I4 两个收集器各自独立计数（7）", 1L, c7.KeyCount);

            // I5 构造参数校验（epoch / owner 非 0）。
            bool threwEpoch = false;
            try { new PMProjectileCandidateCollector(0u, Owner); }
            catch (ArgumentOutOfRangeException) { threwEpoch = true; }
            Check("I5 epoch 0 构造抛 ArgumentOutOfRangeException", threwEpoch);

            bool threwOwner = false;
            try { new PMProjectileCandidateCollector(Epoch, 0u); }
            catch (ArgumentOutOfRangeException) { threwOwner = true; }
            Check("I5 owner 0 构造抛 ArgumentOutOfRangeException", threwOwner);

            CheckEq("I6 Epoch/LocalOwnerNetId 只读回读", (long)Epoch, (long)c7.Epoch);
            CheckEq("I6 Epoch/LocalOwnerNetId 只读回读（owner）", (long)Owner, (long)c7.LocalOwnerNetId);
        }

        // ==================================================================
        //  J 复审补强（宿主接线相关性质在收集器侧的落地）
        // ==================================================================

        /// <summary>
        /// 复审（Docs/plans/_r5_client_host_review.md）针对宿主接线的性质，在收集器侧钉成断言：
        ///   1) 任何被拒调用都不留下账（keys / committed / pending 全不变）——「不占位」的唯一含义；
        ///   2) **在途未提交**就退休（视图消失路径）→ 账整体消失，不会永久占住 100 目标上限；
        ///   3) Forget 之后「每弹 100 目标」的容量**真的**被释放（同一 key 可再收满 100）；
        ///   4) 发送失败回滚不占位、也不影响已提交目标的去重。
        /// </summary>
        private static void SectionJ_ReviewRegressions()
        {
            Section("J 复审补强：被拒不占账 / 在途可退休 / 退休释放容量 / 失败回滚不占位");

            PMProjectileHitCandidate hit;
            PMProjectileCandidateRejectReason why;
            PMProjectileTargetSample okTarget = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);

            // J1 同一收集器上连续用各种非法输入调用：全部被拒，且**一条账都不建**。
            PMProjectileCandidateCollector c1 = NewCollector();

            c1.TryCollect(Segment(float.NaN, 1f, 0f, 2f, 1f, 0f, 11u), okTarget, out hit, out why);
            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u, projectileRadius: float.NaN), okTarget, out hit, out why);
            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), Target(0u, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), Target(TargetA, float.NaN, 1f, 0f, 0.4f, 1f), out hit, out why);

            PMProjectileCandidateSegment otherOwner = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            otherOwner.Key = new PMProjectileKey(Epoch, OtherOwner, 11u, PMProjectileOrigin.ClientPredicted);
            c1.TryCollect(otherOwner, okTarget, out hit, out why);

            PMProjectileCandidateSegment direct = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            direct.Key = new PMProjectileKey(Epoch, Owner, 11u, PMProjectileOrigin.ServerDirect);
            c1.TryCollect(direct, okTarget, out hit, out why);

            PMProjectileCandidateSegment otherEpoch = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u);
            otherEpoch.Key = new PMProjectileKey(Epoch + 1u, Owner, 11u, PMProjectileOrigin.ClientPredicted);
            c1.TryCollect(otherEpoch, okTarget, out hit, out why);

            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), Target(TargetA, 0f, 1f, 0f, 0.4f, 1f, 0u), out hit, out why);

            PMProjectileTargetSample localFrame = Target(TargetA, 0f, 1f, 0f, 0.4f, 1f);
            localFrame.ServerFrame = new PMFrameId(PMFrameDomain.Input, 100L);
            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), localFrame, out hit, out why);

            // 纯几何未命中（目标在 z = 20m 处）。
            c1.TryCollect(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u), Target(TargetA, 0f, 1f, 20f, 0.4f, 1f), out hit, out why);

            CheckEq("J1 10 次被拒调用后 KeyCount 仍为 0（不建账）", 0L, c1.KeyCount);
            CheckEq("J1 10 次被拒全部计入 Rejected（可观测）", 10L, c1.Rejected);
            CheckEq("J1 被拒不产生在途", 0L, c1.PendingKeyCount);
            Check("J1 被拒 key 无在途", !c1.HasPending(Segment(-2f, 1f, 0f, 2f, 1f, 0f, 11u).Key));

            // J2 在途未提交就退休（宿主「视图消失 → Forget」路径，且该 key 可能没建过表现）：
            //    账整体消失，key 容量被释放，不会永久占住。
            PMProjectileCandidateCollector c2 = NewCollector();
            PMProjectileCandidateSegment live = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 31u);
            c2.TryCollect(live, okTarget, out hit, out why);
            CheckEq("J2 退休前有 1 条在途", 1L, c2.PendingTargetCount(live.Key));
            Check("J2 在途未提交也能退休（Forget = true）", c2.Forget(live.Key));
            CheckEq("J2 退休后 KeyCount = 0", 0L, c2.KeyCount);
            CheckEq("J2 退休后在途清零", 0L, c2.PendingTargetCount(live.Key));
            Check("J2 Forget 计数可观测", c2.ForgottenKeys > 0);

            // J3 Forget 真的释放「每弹 100 目标」容量：同一 key 换一批目标可以再收满 100。
            PMProjectileCandidateCollector c3 = NewCollector();
            PMProjectileCandidateSegment key100 = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 41u);
            for (int i = 0; i < 100; i++)
            {
                c3.TryCollect(key100, Target(4000u + (uint)i, 0f, 1f, 0f, 0.4f, 1f), out hit, out why);
            }

            CheckEq("J3 退休前已收满 100 个目标", 100L, c3.PendingTargetCount(key100.Key));
            c3.Forget(key100.Key);

            int refilled = 0;
            for (int i = 0; i < 100; i++)
            {
                if (c3.TryCollect(key100, Target(5000u + (uint)i, 0f, 1f, 0f, 0.4f, 1f), out hit, out why)) { refilled++; }
            }

            CheckEq("J3 Forget 后 100 目标容量释放（可再收满 100）", 100L, refilled);

            // J4 发送失败回滚不占位，也不影响已提交目标的去重。
            PMProjectileCandidateCollector c4 = NewCollector();
            PMProjectileCandidateSegment k4 = Segment(-2f, 1f, 0f, 2f, 1f, 0f, 51u);
            c4.TryCollect(k4, okTarget, out hit, out why);
            c4.Commit(k4.Key);                                          // 目标 A 发送成功 → 占位

            PMProjectileTargetSample b = Target(TargetB, 0f, 1f, 0f, 0.4f, 1f);
            c4.TryCollect(k4, b, out hit, out why);                      // 目标 B 在途
            c4.Rollback(k4.Key);                                        // 发送失败 → 回滚

            CheckEq("J4 回滚不动已提交目标数", 1L, c4.CommittedTargetCount(k4.Key));
            Check("J4 回滚后目标 B 可重收", c4.TryCollect(k4, b, out hit, out why));

            bool recommitA = c4.TryCollect(k4, okTarget, out hit, out why);
            Check("J4 已提交目标 A 仍被去重（AlreadyCommitted）",
                !recommitA && why == PMProjectileCandidateRejectReason.AlreadyCommitted);
        }

        // ==================================================================
        //  夹具
        // ==================================================================

        private static PMProjectileCandidateCollector NewCollector()
        {
            return new PMProjectileCandidateCollector(Epoch, Owner);
        }

        private static PMProjectileTargetSample Target(uint netId, float x, float y, float z,
            float radius, float halfHeight, uint streamVersion = 3u, long serverFrame = 100L)
        {
            PMProjectileTargetSample t = default(PMProjectileTargetSample);
            t.Epoch = Epoch;
            t.NetId = netId;
            t.StreamVersion = streamVersion;
            t.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, serverFrame);
            t.OutputFrame = default(PMFrameId);
            t.TotalSimTimeMs = 1000.0;
            t.WorldTimeMs = 5000.0;
            t.Position = new PMVector3(x, y, z);
            t.RadiusM = radius;
            t.HalfHeightM = halfHeight;
            t.Teleported = false;
            t.Alive = true;
            return t;
        }

        private static PMProjectileCandidateSegment Segment(float px, float py, float pz,
            float qx, float qy, float qz, uint projectileId,
            float projectileRadius = ProjRadius, bool stopped = false)
        {
            PMProjectileCandidateSegment s = default(PMProjectileCandidateSegment);
            s.Key = new PMProjectileKey(Epoch, Owner, projectileId, PMProjectileOrigin.ClientPredicted);
            s.PreviousPosition = new PMVector3(px, py, pz);
            s.Position = new PMVector3(qx, qy, qz);
            s.RadiusM = projectileRadius;
            s.Stopped = stopped;
            return s;
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
