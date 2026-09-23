// ============================================================================
//  PMProjectileIntegrationTest —— R5-A3：整合器 + 两组真实核心的端到端闭环
// ============================================================================
//  事实来源：
//    Docs/plans/net-r5-projectile-contract.md「A3/B1集成冻结」+「语义与有限资源」+「命中验证」+「A验收」
//    Docs/plans/_r5_lifecycle_review.md（A1 公开 API 与口径）
//    Docs/plans/_r5_validation_report.md（A2 公开 API 与口径）
//
//  做法：
//    · 直接编**真实源码**：A1（PMProjectileLifecycle / Pending）、A2（PMProjectileHistory /
//      PMProjectileValidator）、A3（PMProjectileCoordinator）与冻结共享契约 —— 不用替身生命周期，
//      也不用替身 validator；
//    · 目标历史用**真实** PMProjectileHistory（不是自定义 provider），几何由真实验证器算；
//    · 期望值独立手算（常量算术 + 显式推导），不拿实现自身当预期。
//
//  覆盖（对应委派列的必测项）：
//    A. 完整权威闭环：Confirm Spawn → 运动 → 历史 target → Verify → 恰好一次 settlement
//    B. Pending Spawn：Verify 先到 → 生成 → 按序（FIFO）回放
//    C. activation Pending 下的命中结论：确认时结算候选，**不重跑历史 / 不重跑 Validate**
//    D. Reject / TTL 不结算；Reject 实际撤销预测实例
//    E. 同 activation 20 散弹：ID 预留 + 多弹一次解挂
//    F. 墓碑窗口内外：窗口内可完成校验；窗口外明确过期且不结算
//    G. 跨 owner / epoch / id / state 注入；trustedSpec 防客户端改 speed/radius
//    H. 最终确认时旧 stream / 死亡目标必须被拒（不结算）
//    I. Drain 重复无结果（先提交再排队）
//    J. 出口队列溢出 = **永久 Faulted + InvalidOperationException**（不丢已接受出口）；
//       升 epoch 重建 / 断连清理；旧 ID 不复活
//    K. NaN / 时钟倒退 / 步长越界 / 追赶预算钳制
//    L. 注册先于追赶 + 运动写回不覆盖保护字段 + StopOnHit 终态 + 宿主 hook
//    M. 负例与边界：B 队列容量/TTL、真实 Verify 配额、结算队列 Faulted
//    N. 【返工】预留假弹 → 权威本体**原地升级**：RequestSpawn 预留 → Confirm → 权威 ID 真实非 0 →
//       CatchUpMotion **真实推进**（不是忽略 NotAuthority）；ID/Origin 不变、只有 Confirmed 可升级、幂等
//    P. 【返工】宿主运动/停止 hook 失败（false / NaN / 抛异常）必须 **fail closed**：
//       本步不推进 + 就地停止 + 直线贯穿被阻止；snapshot 是深 clone（hook 改不回账本）
//    Q. 【返工】C 队列**暂存期去重预留**（重复报告不哏涨组、不挤掉其它目标）；
//       组被丢弃（TTL/容量）时预留必须释放，同 key 可重建结算且不重复
//    R. 【返工】StopOnHit 在 **Pending 期间就停**（不是等 Confirm 才停）
//    S. 【返工】命中上报必须绑定**认证 owner**（跨 owner 一律拒）；未登记 key 不得自称 ServerDirect；
//       B 队列洪泛有界
//
//  不覆盖（诚实口径）：B1 codec（并行中，禁止依赖）、真 RPC 承载、复制、Unity 宿主、
//    场景墙碰撞（本组只有直线核心运动与宿主 hook 接口，不声称不穿墙）、实机 T45（PENDING_USER）。
//
//  运行：dotnet Tools/PMProjectileIntegrationTest/bin/Release/net8.0/PMProjectileIntegrationTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

namespace PMProjectileIntegrationTest
{
    internal sealed class AcceptAllFilter : IPMProjectileHitFilter
    {
        public int Calls;
        public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target, PMVector3 sanitizedImpact)
        {
            Calls++;
            return true;
        }
    }

    internal sealed class RejectAllFilter : IPMProjectileHitFilter
    {
        public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target, PMVector3 sanitizedImpact)
        {
            return false;
        }
    }

    /// <summary>宿主运动 hook：把直线推进改成「只沿 +Z、速度固定 20m/s」，用于证明 hook 生效且不越权。</summary>
    internal sealed class ZOnlyMotionHook : IPMProjectileHostMotion
    {
        public int Steps;
        public int StopAtStep = -1;
        public PMVector3 LastSeenSnapshotPosition;
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            Steps++;
            LastSeenSnapshotPosition = snapshot.Position;
            velocity = new PMVector3(0f, 0f, 20f);
            position = snapshot.Position + velocity * (deltaMs / 1000f);
            yaw = straightLineYaw;
            return true;
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot.Position;
            return StopAtStep >= 0 && Steps >= StopAtStep;
        }
    }

    /// <summary>TryStep 返回 false：必须 fail closed（不能回退直线穿墙）。</summary>
    internal sealed class FalseStepHook : IPMProjectileHostMotion
    {
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            position = straightLinePosition;
            velocity = straightLineVelocity;
            yaw = straightLineYaw;
            return false;
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot.Position;
            return false;
        }
    }

    /// <summary>TryStep 返回 true 但位置是 NaN：必须 fail closed（不得把 NaN 写进账本）。</summary>
    internal sealed class NanStepHook : IPMProjectileHostMotion
    {
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            position = new PMVector3(float.NaN, 1f, 1f);
            velocity = straightLineVelocity;
            yaw = straightLineYaw;
            return true;
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot.Position;
            return false;
        }
    }

    /// <summary>TryStep 抛异常：必须 fail closed。</summary>
    internal sealed class ThrowingStepHook : IPMProjectileHostMotion
    {
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            throw new InvalidOperationException("宿主碰撞查询炸了");
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot.Position;
            return false;
        }
    }

    /// <summary>TryStop 抛异常 / 输出 NaN 停止点：必须 fail closed（当次运动已生效，但立即就地停止）。</summary>
    internal sealed class BadStopHook : IPMProjectileHostMotion
    {
        public bool ThrowOnStop;
        public bool NanStopPoint;
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            position = straightLinePosition;
            velocity = straightLineVelocity;
            yaw = straightLineYaw;
            return true;
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            if (ThrowOnStop) { throw new InvalidOperationException("停止查询炸了"); }
            stopPosition = NanStopPoint ? new PMVector3(float.NaN, 0f, 0f) : snapshot.Position;
            return true;
        }
    }

    /// <summary>
    /// 用直线结果回填（合法），但把传入的 snapshot 改脏：
    /// 用于证明 snapshot 是深 clone、且传入的总是弹的**真实当前位置**（hook 改不回账本）。
    /// </summary>
    internal sealed class MutatingHook : IPMProjectileHostMotion
    {
        public PMVector3 SeenSnapshotPosition;
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            SeenSnapshotPosition = snapshot.Position;
            snapshot.Position = new PMVector3(9999f, 9999f, 9999f);
            snapshot.Velocity = new PMVector3(9999f, 0f, 0f);
            position = straightLinePosition;
            velocity = straightLineVelocity;
            yaw = straightLineYaw;
            return true;
        }

        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot.Position;
            return false;
        }
    }

    internal static class Program
    {
        private const uint EP = 7u;
        private const uint OWNER = 100u;
        private const uint AUTH_NET_ID = 900u;
        private const uint SOURCE = 5000u;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static double _now = 1000.0;
        private static long _serverFrame = 10L;

        private static double Now() { return _now; }

        private static double At(double ms)
        {
            _now += ms;
            return _now;
        }

        private static int Main()
        {
            Console.WriteLine("=== R5-A3：PMProjectileCoordinator 真实核心整合（手算 oracle）===");
            Console.WriteLine();

            Section("A. 完整权威闭环（Confirm Spawn → 运动 → 历史 target → Verify → 一次 settlement）",
                TestFullLoop);
            Section("B. Pending Spawn：Verify 先到 → 生成 → FIFO 按序回放", TestVerifyBeforeSpawn);
            Section("C. activation Pending 命中结论：确认时结算且不重跑历史", TestPendingHitConfirm);
            Section("D. Reject / TTL 不结算；Reject 实际撤销预测实例", TestRejectAndTtl);
            Section("E. 同 activation 20 散弹（ID 预留 + 多弹一次解挂）", TestShotgun);
            Section("F. 墓碑窗口内外", TestTombstone);
            Section("G. 跨 owner/epoch/id/state 注入 + trustedSpec 防改 speed/radius", TestAdmissionGuards);
            Section("H. 最终确认时旧 stream / 死亡目标必须被拒", TestStaleTargetAtConfirm);
            Section("I. Drain 重复无结果（先提交再排队）", TestDrainOnce);
            Section("J. 队列压力 / 升 epoch / 断线清理 / 旧 ID 不复活", TestCapacityEpochCleanup);
            Section("K. NaN / 时钟倒退 / 步长越界 / 追赶预算钳制", TestNumericAndClock);
            Section("L. 注册先于追赶 / 运动保护字段 / StopOnHit 终态 / 宿主 hook", TestMotionAndStop);
            Section("M. 负例与边界：B 队列容量/TTL、真实 Verify 配额、结算队列压力", TestQueueAndQuotaEdges);
            Section("N. 预留假弹 → 权威本体原地升级（Confirm 后可追赶并真实推进）", TestPromotion);
            Section("P. 宿主运动/停止 hook 失败必须 fail closed（不回退直线穿墙）", TestHostMotionFailClosed);
            Section("Q. C 队列暂存期去重预留 / TTL 丢弃后释放并可重建结算", TestPendingHitDedupReservation);
            Section("R. StopOnHit 在 Pending 期间就停（不是等 Confirm）", TestStopOnHitWhilePending);
            Section("S. 命中上报的认证 owner 绑定与未登记 ServerDirect 拒绝", TestHitReportTrustBoundary);
            Section("T. 【返工】权威注册后的首次有界追赶（CatchUpRegisteredMotion）", TestCatchUpRegisteredMotion);

            Console.WriteLine();
            Console.WriteLine(_failures.Count == 0
                ? ("全部通过：" + _passed + " 项断言。")
                : ("失败 " + _failures.Count + " 项 / 通过 " + _passed + " 项。"));
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("  FAIL " + _failures[i]);
            }

            return _failures.Count == 0 ? 0 : 1;
        }

        // =========================================================== 工具

        private static PMProjectileSpec Spec(int lifetimeMs, bool stopOnHit)
        {
            PMProjectileSpec s = new PMProjectileSpec();
            s.SpeedMps = 10f;
            s.RadiusM = 0.1f;
            s.LifetimeMs = lifetimeMs;
            s.DelayDestroyMs = 0;
            s.StopOnHit = stopOnHit;
            s.HideOnStop = true;
            s.SkipFlyingTrajectoryValidation = false;
            return s;
        }

        private static PMProjectileSpawnRequest MakeRequest(uint owner, uint id, uint activationId,
            PMVector3 muzzle, PMVector3 direction)
        {
            PMProjectileSpawnRequest req = new PMProjectileSpawnRequest();
            PMProjectileState st = new PMProjectileState();
            st.Key = new PMProjectileKey(EP, owner, id, PMProjectileOrigin.ClientPredicted);
            st.ActivationId = activationId;
            st.SpawnPosition = muzzle;
            st.Position = muzzle;
            st.PreviousPosition = muzzle;
            st.Velocity = direction;
            st.Yaw = 0f;
            req.State = st;
            req.Spec = new PMProjectileSpec();
            req.PredictionMs = 0;
            return req;
        }

        private static PMProjectileState MakeAuthorityState(uint owner, uint id, uint activationId,
            PMVector3 muzzle, PMVector3 direction)
        {
            PMProjectileState st = new PMProjectileState();
            st.Key = new PMProjectileKey(EP, owner, id, PMProjectileOrigin.ServerDirect);
            st.ActivationId = activationId;
            st.SpawnPosition = muzzle;
            st.Position = muzzle;
            st.PreviousPosition = muzzle;
            st.Velocity = direction;
            st.Yaw = 0f;
            return st;
        }

        private static PMProjectileKey PredKey(uint owner, uint id)
        {
            return new PMProjectileKey(EP, owner, id, PMProjectileOrigin.ClientPredicted);
        }

        private static PMProjectileKey AuthKey(uint owner, uint id)
        {
            return new PMProjectileKey(EP, owner, id, PMProjectileOrigin.ServerDirect);
        }

        private static PMProjectileHitBatch MakeBatch(PMProjectileKey key, PMVector3 previous, PMVector3 hit,
            int rewindMs, uint targetNetId, uint targetStream)
        {
            PMProjectileHitBatch batch = new PMProjectileHitBatch();
            batch.Key = key;
            batch.PreviousPosition = previous;
            batch.HitPosition = hit;
            batch.RewindMs = rewindMs;
            PMProjectileHitCandidate c = default(PMProjectileHitCandidate);
            c.TargetNetId = targetNetId;
            c.TargetStreamVersion = targetStream;
            c.TargetServerFrame = PMFrameId.None;
            c.ImpactPoint = hit;
            c.VisualOffset = PMVector3.Zero;
            batch.Targets = new PMProjectileHitCandidate[1];
            batch.Targets[0] = c;
            return batch;
        }

        /// <summary>
        /// 命中上报（正常路径）：认证会话绑定的 owner = batch 自称的 owner。
        /// 契约「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报」在 API 上就是 ReportHits 的
        /// authenticatedOwnerNetId 入参；跨 owner 伪造由 S 段显式验证。
        /// </summary>
        private static PMProjectileHitReportOutcome Report(PMProjectileCoordinator coord,
            PMProjectileHitBatch batch, double wallNowMs)
        {
            return coord.ReportHits(batch, batch.Key.OwnerNetId, wallNowMs);
        }

        private static bool RecordTarget(PMProjectileHistory history, uint netId, uint stream, double worldMs,
            PMVector3 position, bool alive, float radius, float halfHeight)
        {
            PMProjectileTargetSample s = default(PMProjectileTargetSample);
            s.Epoch = EP;
            s.NetId = netId;
            s.StreamVersion = stream;
            s.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, _serverFrame);
            _serverFrame++;
            s.OutputFrame = PMFrameId.None;
            s.TotalSimTimeMs = worldMs;
            s.WorldTimeMs = worldMs;
            s.Position = position;
            s.RadiusM = radius;
            s.HalfHeightM = halfHeight;
            s.Teleported = false;
            s.Alive = alive;
            string reason;
            return history.Record(s, out reason);
        }

        private static PMProjectileHistory NewHistory()
        {
            return new PMProjectileHistory(EP);
        }

        /// <summary>标准受害目标：中心 (0,1,3)、半径 0.5、半高 1.0（中心线半长 0.5）。</summary>
        private static PMProjectileHistory HistoryWithTarget(uint netId, uint stream, double worldMs, bool alive)
        {
            PMProjectileHistory h = NewHistory();
            RecordTarget(h, netId, stream, worldMs, new PMVector3(0f, 1f, 3f), alive, 0.5f, 1.0f);
            return h;
        }

        /// <summary>
        /// 近原点受害目标：中心 (0,1,0.5)、半径 0.5、半高 1.0。
        /// 用于「弹尚未推进」的场景 —— L2 飞行预算 = speed*(100+defer)/1000 = 1.0m，
        /// 因此命中点必须落在弹当前位置（(0,1,0)）1m 内，否则整包被 L2 预算拒。
        /// </summary>
        private static PMProjectileHistory HistoryNear(uint netId, uint stream, double worldMs, bool alive)
        {
            PMProjectileHistory h = NewHistory();
            RecordTarget(h, netId, stream, worldMs, new PMVector3(0f, 1f, 0.5f), alive, 0.5f, 1.0f);
            return h;
        }

        /// <summary>近原点命中包：弹在 (0,1,0)、命中点 (0,1,0.4)（距弹 0.4m，预算内）。</summary>
        private static PMProjectileHitBatch NearBatch(PMProjectileKey key, uint targetNetId, uint targetStream)
        {
            return MakeBatch(key, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 1f, 0.4f), 0, targetNetId, targetStream);
        }

        // =========================================================== A

        private static void TestFullLoop()
        {
            _now = 1000.0;
            PMProjectileHistory history = HistoryWithTarget(55u, 1u, _now, true);
            AcceptAllFilter filter = new AcceptAllFilter();
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, filter, null);

            // activation 先被权威裁决为 Confirmed ⇒ admission 时立即生成。
            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 1u,
                PMActivationResult.Confirmed, Now());
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "A: 激活裁决写入");

            PMProjectileSpawnRequest req = MakeRequest(OWNER, 1u, 1u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f));
            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(req, OWNER, AUTH_NET_ID, SOURCE,
                new PMVector3(0f, 1f, 0f), Spec(3000, true), null, Now());
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "A: admission 通过");
            Check(adm.Spawned && !adm.SpawnPending, "A: activation 已确认 ⇒ 立即生成");
            CheckEq(coord.SpawnEventCount, 1, "A: 产生 1 条 SpawnReady");

            // 权威面消毒：AuthorityNetId 取可信入参；速度 = 归一化方向 * trustedSpec.SpeedMps。
            PMProjectileSpec frozenSpec;
            PMProjectileState frozen;
            Check(coord.TryObserveFrozen(PredKey(OWNER, 1u), out frozenSpec, out frozen), "A: 冻结观察");
            // R5-A3 返工：activation 已 Confirmed ⇒ admission 当场就把预留弹**原地升级**为权威本体，
            // 冻结状态上的 AuthorityNetId 自此是真实可信值（不再是 0），且 CatchUpMotion 可用。
            // 客户端自称的 9999 仍然不会被写入。
            CheckEq(frozen.AuthorityNetId, AUTH_NET_ID, "A: Confirmed ⇒ 原地升级，权威 ID 真实非 0");
            PMProjectileRegistration regA;
            Check(coord.TryObserveRegistration(PredKey(OWNER, 1u), out regA), "A: 登记观察");
            Check(!regA.Predicted && regA.Promoted, "A: 已从预留假弹升级为权威本体");
            CheckEq(regA.Key.ProjectileId, 1u, "A: 升级不重新分配 ID");
            CheckEq(regA.Key.Origin, PMProjectileOrigin.ClientPredicted, "A: 升级不改 Origin");
            CheckEq(coord.PromotedProjectileCount, 1, "A: 升级计数可观测");
            CheckEq((double)frozen.Velocity.Length, 10.0, "A: 速度强度取 trustedSpec");
            CheckEq(frozen.HitTargets.Length, 0, "A: 命中集合不来自客户端");
            CheckEq(frozen.AllowedTargets.Length, 0, "A: 白名单不来自客户端");

            // 运动：5 步 × 50ms = 250ms ⇒ 位置 (0,1,2.5)。
            for (int i = 0; i < 5; i++)
            {
                PMProjectileAdvanceOutcome adv = coord.AdvanceMotion(PredKey(OWNER, 1u), 50, At(1.0));
                CheckEq(adv.Result, PMProjectileAdvanceResult.Advanced, "A: 运动推进 #" + i);
            }

            Check(coord.TryObserveFrozen(PredKey(OWNER, 1u), out frozenSpec, out frozen), "A: 再次冻结观察");
            CheckEq((double)frozen.Position.Z, 2.5, "A: 位置沿 +Z 推进 2.5m");
            CheckEq(frozen.MoveTimeMs, 250.0, "A: MoveTimeMs 累计 250ms");

            PMProjectileHitBatch batch = MakeBatch(PredKey(OWNER, 1u), new PMVector3(0f, 1f, 2.5f),
                new PMVector3(0f, 1f, 2.6f), 0, 55u, 1u);
            PMProjectileHitReportOutcome rep = Report(coord, batch, At(1.0));
            CheckEq(rep.Result, PMProjectileHitReportResult.Settled, "A: Verify 结算");
            CheckEq(rep.Status, PMProjectileValidateStatus.Confirmed, "A: L0 结论 Confirmed");
            Check(rep.VerifyConsumed, "A: 消耗一次 Verify 配额");
            CheckEq(rep.VerifyUsed, 1, "A: 真实配额 = 1");
            CheckEq(rep.SettledHitCount, 1, "A: 结算 1 个命中");

            PMProjectileSettlement[] settlements;
            int n = coord.DrainSettlements(8, out settlements);
            CheckEq(n, 1, "A: 恰好一次 settlement");
            CheckEq(settlements[0].Key, PredKey(OWNER, 1u), "A: settlement 带 key");
            CheckEq(settlements[0].ActivationId, 1u, "A: settlement 带 activation");
            CheckEq(settlements[0].AuthorityNetId, AUTH_NET_ID, "A: settlement 带可信 authorityNetId");
            CheckEq(settlements[0].HitCount, 1, "A: settlement 命中数");
            CheckEq(settlements[0].Hits[0].TargetNetId, 55u, "A: 命中目标正确");
            CheckEq(settlements[0].Hits[0].TargetStreamVersion, 1u, "A: 命中目标 stream 正确");
            Check(settlements[0].Hits[0].ImpactPoint.IsFinite, "A: 命中点是已消毒有限值");
            Check(settlements[0].StopOnHit, "A: StopOnHit 透传");

            CheckEq(coord.DrainSettlements(8, out settlements), 0, "A: 重复 Drain 无结果");

            // StopOnHit ⇒ 停止 + 墓碑 + Stopped 出口；之后运动进入终态。
            PMProjectileStopEvent[] stops;
            CheckEq(coord.DrainStopped(8, out stops), 1, "A: StopOnHit 产生 1 条停止出口");
            CheckEq(stops[0].Key, PredKey(OWNER, 1u), "A: 停止出口带 key");
            Check(stops[0].TombstoneUntilMs > stops[0].WallTimeMs, "A: 停止建立了墓碑窗口");
            CheckEq(coord.AdvanceMotion(PredKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.NotMovable, "A: 停止后运动是终态");

            PMProjectileSpawnEvent[] spawns;
            CheckEq(coord.DrainSpawnReady(4, out spawns), 1, "A: 生成出口 1 条");
            CheckEq(spawns[0].AuthorityNetId, AUTH_NET_ID, "A: 生成出口带可信 authorityNetId");
        }

        // =========================================================== B

        private static void TestVerifyBeforeSpawn()
        {
            _now = 2000.0;
            PMProjectileHistory history = NewHistory();
            RecordTarget(history, 55u, 1u, _now, new PMVector3(0f, 1f, 0.5f), true, 0.5f, 1.0f);
            RecordTarget(history, 56u, 1u, _now, new PMVector3(0f, 1f, -0.5f), true, 0.5f, 1.0f);

            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 5u);

            // 1) 命中上报先到（该 key 连登记都没有）⇒ 进 B 队列（原始上行）。
            PMProjectileHitReportOutcome r1 = Report(coord, 
                MakeBatch(key, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 1f, 0.4f), 0, 55u, 1u), At(1.0));
            PMProjectileHitReportOutcome r2 = Report(coord, 
                MakeBatch(key, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 1f, -0.4f), 0, 56u, 1u), At(1.0));
            CheckEq(r1.Result, PMProjectileHitReportResult.StashedForSpawn, "B: 先到上报进 B 队列 #1");
            CheckEq(r2.Result, PMProjectileHitReportResult.StashedForSpawn, "B: 先到上报进 B 队列 #2");
            CheckEq(coord.PendingVerifies.CountForKey(key), 2, "B: 同 key 暂存 2 条");
            Check(coord.PendingVerifies.IsPendingMarkerSet(key), "B: pending 标记已置");
            CheckEq(coord.PendingVerifies.Count, 2, "B: 总量 2");
            CheckEq(coord.StashedVerifyCount, 2, "B: 暂存计数可观测");
            CheckEq(coord.SettlementCount, 0, "B: 暂存期绝不结算");

            // 2) 生成请求到达：activation 未定 ⇒ 挂起（不生成幽灵弹）。
            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(
                MakeRequest(OWNER, 5u, 7u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "B: admission 通过");
            Check(adm.SpawnPending && !adm.Spawned, "B: activation 未定 ⇒ 挂起、未生成");
            CheckEq(coord.SpawnEventCount, 0, "B: 挂起期不发 SpawnReady（不生成幽灵弹）");
            CheckEq(coord.PendingSpawns.Count, 1, "B: A 队列 1 条");
            CheckEq(coord.SettlementCount, 0, "B: 挂起期仍不结算");

            // 3) 激活确认 ⇒ 生成 + 按 FIFO 回放两条暂存。
            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 7u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "B: 激活确认");
            CheckEq(act.ReplayedVerifies, 2, "B: 回放 2 条");
            CheckEq(act.SpawnedKeys, 1, "B: 生成 1 颗");
            CheckEq(act.SettledHitCount, 2, "B: 回放结算 2 个命中");
            CheckEq(coord.PendingVerifies.CountForKey(key), 0, "B: 回放后 B 队列清空");
            Check(!coord.PendingVerifies.IsPendingMarkerSet(key), "B: pending 标记已清除");

            PMProjectileSettlement[] settlements;
            int n = coord.DrainSettlements(8, out settlements);
            CheckEq(n, 2, "B: 两次结算");
            CheckEq(settlements[0].Hits[0].TargetNetId, 55u, "B: 第一条是 FIFO 先入的 55");
            CheckEq(settlements[1].Hits[0].TargetNetId, 56u, "B: 第二条是 FIFO 后入的 56");

            PMProjectileSpawnEvent[] spawns;
            CheckEq(coord.DrainSpawnReady(8, out spawns), 1, "B: 生成出口 1 条");
            CheckEq(spawns[0].Key, key, "B: 生成出口带 key");
        }

        // =========================================================== C

        private static void TestPendingHitConfirm()
        {
            _now = 3000.0;
            PMProjectileHistory history = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 9u);

            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(
                MakeRequest(OWNER, 9u, 5u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "C: admission 通过");
            Check(adm.SpawnPending, "C: activation 未定 ⇒ 挂起");

            // 命中成立但 activation 仍 Pending ⇒ L0 Pending ⇒ 进 C 队列（已消毒结论）。
            PMProjectileHitReportOutcome rep = Report(coord, NearBatch(key, 55u, 1u), At(1.0));
            CheckEq(rep.Result, PMProjectileHitReportResult.Pending, "C: 命中结论 Pending");
            CheckEq(rep.Status, PMProjectileValidateStatus.Pending, "C: L0 结论 Pending");
            CheckEq(rep.StashedHitCount, 1, "C: 暂存 1 个已消毒命中");
            CheckEq(coord.PendingHits.GroupCount, 1, "C: C 队列 1 组");
            CheckEq(coord.SettlementCount, 0, "C: Pending 期间不结算");
            CheckEq(rep.VerifyUsed, 1, "C: 已消耗 1 次 Verify");

            // 确认 ⇒ 直接结算候选（不重跑 Validate / 不重跑历史几何）。
            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 5u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "C: 确认生效");
            CheckEq(act.SettledHitCount, 1, "C: 确认时结算 1 个候选命中");
            CheckEq(coord.VerifyUsedCount(key), 1, "C: 配额仍是 1（未重跑 Validate）");
            CheckEq(coord.PendingHits.GroupCount, 0, "C: C 队列已兑现并移除");

            PMProjectileSettlement[] settlements;
            CheckEq(coord.DrainSettlements(8, out settlements), 1, "C: 一次结算");
            CheckEq(settlements[0].Hits[0].TargetNetId, 55u, "C: 结算目标与暂存一致");

            // 重复确认：终态不倒退 ⇒ 不再兑现。
            PMProjectileActivationApplyOutcome again = coord.ResolveActivation(OWNER, 5u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(again.Result, PMProjectileActivationApplyResult.AlreadyResolved, "C: 重复确认幂等");
            CheckEq(coord.DrainSettlements(8, out settlements), 0, "C: 重复确认不产生第二次结算");
        }

        // =========================================================== D

        private static void TestRejectAndTtl()
        {
            _now = 4000.0;
            PMProjectileHistory history = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 11u);

            coord.RequestSpawn(MakeRequest(OWNER, 11u, 8u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            Report(coord, NearBatch(key, 55u, 1u), At(1.0));
            CheckEq(coord.PendingHits.GroupCount, 1, "D: 命中结论已暂存");

            PMProjectileActivationApplyOutcome rej = coord.ResolveActivation(OWNER, 8u,
                PMActivationResult.Rejected, At(1.0));
            CheckEq(rej.Result, PMProjectileActivationApplyResult.Applied, "D: 拒绝生效");

            PMProjectileSettlement[] settlements;
            CheckEq(coord.DrainSettlements(8, out settlements), 0, "D: 拒绝不结算");
            CheckEq(coord.PendingHits.GroupCount, 0, "D: 拒绝丢弃暂存命中");
            Check(!coord.Lifecycle.IsRegistered(key), "D: 拒绝实际移除预测登记");

            PMProjectileActivationRelease[] decisions;
            int dn = coord.DrainDecisions(16, out decisions);
            Check(dn >= 1, "D: 拒绝产生决策出口");
            bool removed = false;
            for (int i = 0; i < dn; i++)
            {
                if (decisions[i].Key == key && decisions[i].RemovedInstance) { removed = true; }
            }

            Check(removed, "D: 决策携带 RemovedInstance=true（调用方须真撤销）");

            // 终态不倒退：拒绝之后同 activation 的新弹不得被当成 Pending 复活。
            PMProjectileAdmissionOutcome after = coord.RequestSpawn(
                MakeRequest(OWNER, 12u, 8u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));
            CheckEq(after.Result, PMProjectileAdmissionResult.RevokedActivation, "D: 已拒 activation 不复活");

            // ---- TTL 不结算 ----
            _now = 5000.0;
            PMProjectileHistory history2 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord2 = new PMProjectileCoordinator(EP, history2, new AcceptAllFilter(), null);
            PMProjectileKey key2 = PredKey(OWNER, 13u);
            coord2.RequestSpawn(MakeRequest(OWNER, 13u, 3u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            CheckEq(coord2.PendingSpawns.Count, 1, "D: A 队列 1 条");

            PMProjectilePurgeOutcome purge = coord2.PurgeExpired(At(2001.0));
            CheckEq(purge.PendingSpawnsExpired, 1, "D: 挂起 Spawn 超 TTL 被清理");
            CheckEq(coord2.PendingSpawns.Count, 0, "D: A 队列清空");
            Check(!coord2.Lifecycle.IsRegistered(key2), "D: 超时挂起把 ID 预留一并退回（不留永久假弹）");
            CheckEq(coord2.DrainSettlements(8, out settlements), 0, "D: TTL 不结算");
        }

        // =========================================================== E

        private static void TestShotgun()
        {
            _now = 6000.0;
            PMProjectileHistory history = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);

            // 20 颗同 activation、同生成源、不同 projectileId ⇒ 全部入队（不是只剩一颗）。
            for (uint i = 1u; i <= 20u; i++)
            {
                PMProjectileAdmissionOutcome adm = coord.RequestSpawn(
                    MakeRequest(OWNER, i, 3u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                    OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));
                CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "E: 散弹 #" + i + " 入队");
                Check(adm.SpawnPending, "E: 散弹 #" + i + " 挂起等待");
            }

            CheckEq(coord.PendingSpawns.Count, 20, "E: A 队列 20 条");
            CheckEq(coord.PendingSpawns.CountForSource(OWNER, SOURCE), 20, "E: 同源 20 条（键按完整 key）");

            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 3u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.ReleasedKeys, 20, "E: 一次解挂 20 条");
            CheckEq(act.SpawnedKeys, 20, "E: 一次生成 20 颗");
            CheckEq(coord.PendingSpawns.Count, 0, "E: A 队列清空");

            PMProjectileSpawnEvent[] spawns;
            CheckEq(coord.DrainSpawnReady(64, out spawns), 20, "E: 20 条生成出口");
            bool[] seen = new bool[21];
            for (int i = 0; i < spawns.Length; i++)
            {
                uint id = spawns[i].Key.ProjectileId;
                if (id >= 1u && id <= 20u) { seen[id] = true; }
            }

            int distinct = 0;
            for (int i = 1; i <= 20; i++) { if (seen[i]) { distinct++; } }
            CheckEq(distinct, 20, "E: 20 个不同 projectileId 全部生成（无错位）");
        }

        // =========================================================== F

        private static void TestTombstone()
        {
            _now = 7000.0;
            PMProjectileHistory history = NewHistory();
            RecordTarget(history, 55u, 1u, _now, new PMVector3(0f, 1f, 2.5f), true, 0.5f, 1.0f);
            RecordTarget(history, 56u, 1u, _now, new PMVector3(0f, 1f, 2.5f), true, 0.5f, 1.0f);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);

            // 两颗 ServerDirect 弹（speed 10、Lifetime 200ms ⇒ 停在 2.0m）。
            PMProjectileAdmissionOutcome a1 = coord.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(200, false), null, 0, Now());
            PMProjectileAdmissionOutcome a2 = coord.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 2u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(200, false), null, 0, Now());
            CheckEq(a1.Result, PMProjectileAdmissionResult.Admitted, "F: ServerDirect #1");
            CheckEq(a2.Result, PMProjectileAdmissionResult.Admitted, "F: ServerDirect #2");
            Check(a1.Spawned && a1.Activation == PMActivationResult.Confirmed, "F: ServerDirect 按构造 Confirmed");
            CheckEq(a1.ActivationId, 0u, "F: ServerDirect 允许 activationId=0");

            for (int i = 0; i < 4; i++)
            {
                CheckEq(coord.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                    i < 3 ? PMProjectileAdvanceResult.Advanced : PMProjectileAdvanceResult.Stopped,
                    "F: Lifetime 停弹步 #" + i);
                CheckEq(coord.AdvanceMotion(AuthKey(OWNER, 2u), 50, At(1.0)).Result,
                    i < 3 ? PMProjectileAdvanceResult.Advanced : PMProjectileAdvanceResult.Stopped,
                    "F: Lifetime 停弹步 #" + i + "（弹 2）");
            }

            PMProjectileStopEvent[] stops;
            CheckEq(coord.DrainStopped(8, out stops), 2, "F: 两颗弹各一条停止出口");
            double until = stops[0].TombstoneUntilMs;
            // 墓碑公式：max(150, delayDestroyMs=0, clamp(2*predictionMs(0)+100, 0, 1000)) = 150。
            CheckEq(until - stops[0].WallTimeMs, 150.0, "F: 墓碑窗口 = 150ms（手算）");

            // 窗口内：允许完成校验（②b 停止常规分支）。
            PMProjectileHitReportOutcome inside = Report(coord, 
                MakeBatch(AuthKey(OWNER, 1u), new PMVector3(0f, 1f, 1.9f), new PMVector3(0f, 1f, 2.4f), 0, 55u, 1u),
                At(100.0));
            Check(inside.Result != PMProjectileHitReportResult.TombstoneExpired, "F: 窗口内不被判过期");
            CheckEq(inside.Result, PMProjectileHitReportResult.Settled, "F: 窗口内可完成校验并结算");

            // 窗口外（>150ms）：明确过期且不结算。
            PMProjectileHitReportOutcome outside = Report(coord, 
                MakeBatch(AuthKey(OWNER, 2u), new PMVector3(0f, 1f, 1.9f), new PMVector3(0f, 1f, 2.4f), 0, 56u, 1u),
                At(100.0));
            CheckEq(outside.Result, PMProjectileHitReportResult.TombstoneExpired, "F: 窗口外明确过期");

            PMProjectileSettlement[] settlements;
            CheckEq(coord.DrainSettlements(8, out settlements), 1, "F: 只有窗口内那次结算");
        }

        // =========================================================== G

        private static void TestAdmissionGuards()
        {
            _now = 8000.0;
            PMProjectileHistory history = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMProjectileSpec trusted = Spec(3000, false);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);

            // --- 跨 owner ---
            PMProjectileSpawnRequest crossOwner = MakeRequest(200u, 1u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(crossOwner, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.UntrustedOwner, "G: 跨 owner 注入被拒");

            // --- owner=0 ---
            PMProjectileSpawnRequest zeroOwner = MakeRequest(0u, 2u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(zeroOwner, 0u, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.UntrustedOwner, "G: owner=0 被拒");

            // --- 跨 epoch ---
            PMProjectileSpawnRequest crossEpoch = MakeRequest(OWNER, 3u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            crossEpoch.State.Key = new PMProjectileKey(EP + 1u, OWNER, 3u, PMProjectileOrigin.ClientPredicted);
            CheckEq(coord.RequestSpawn(crossEpoch, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.StaleEpoch, "G: 跨 epoch 被拒");

            // --- 冒充 ServerDirect ---
            PMProjectileSpawnRequest fakeDirect = MakeRequest(OWNER, 4u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            fakeDirect.State.Key = new PMProjectileKey(EP, OWNER, 4u, PMProjectileOrigin.ServerDirect);
            CheckEq(coord.RequestSpawn(fakeDirect, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.InvalidRequest, "G: 客户端冒充 ServerDirect 被拒");

            // --- activationId=0（预测侧）---
            PMProjectileSpawnRequest zeroAct = MakeRequest(OWNER, 4u, 0u, ownerPos, new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(zeroAct, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.InvalidActivationId, "G: 预测侧 activationId=0 被拒");

            // --- 10m 枪口：10m 通过、10.01m 拒 ---
            PMProjectileSpawnRequest far = MakeRequest(OWNER, 5u, 1u, new PMVector3(10f, 1f, 0f), new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(far, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.Admitted, "G: 恰好 10m 通过（边界内）");
            PMProjectileSpawnRequest farther = MakeRequest(OWNER, 6u, 1u, new PMVector3(10.01f, 1f, 0f), new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(farther, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.MuzzleTooFar, "G: 10.01m 被拒");

            // --- 方向为 0 / 非 finite ---
            PMProjectileSpawnRequest noDir = MakeRequest(OWNER, 7u, 1u, ownerPos, PMVector3.Zero);
            CheckEq(coord.RequestSpawn(noDir, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.InvalidDirection, "G: 零方向被拒");
            PMProjectileSpawnRequest nanDir = MakeRequest(OWNER, 8u, 1u, ownerPos, new PMVector3(float.NaN, 0f, 1f));
            CheckEq(coord.RequestSpawn(nanDir, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.InvalidDirection, "G: NaN 方向被拒");

            // --- 客户端权威字段注入 + trustedSpec 防改 ---
            PMProjectileSpawnRequest inject = MakeRequest(OWNER, 20u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            inject.Spec = new PMProjectileSpec();
            inject.Spec.SpeedMps = 9999f;
            inject.Spec.RadiusM = 999f;
            inject.Spec.LifetimeMs = 999999;
            inject.Spec.StopOnHit = true;
            inject.State.AuthorityNetId = 9999u;
            inject.State.Stopped = true;
            inject.State.Hidden = true;
            inject.State.TakenOver = true;
            inject.State.MoveTimeMs = 4242.0;
            inject.State.HitTargets = new uint[] { 7u };
            inject.State.AllowedTargets = new uint[] { 7u };
            PMProjectileAdmissionOutcome inj = coord.RequestSpawn(inject, OWNER, AUTH_NET_ID, SOURCE, ownerPos,
                trusted, null, At(1.0));
            CheckEq(inj.Result, PMProjectileAdmissionResult.Admitted, "G: 合法请求仍通过（注入被消毒）");

            PMProjectileSpec fs;
            PMProjectileState fstate;
            Check(coord.TryObserveFrozen(PredKey(OWNER, 20u), out fs, out fstate), "G: 冻结观察");
            CheckEq((double)fs.SpeedMps, 10.0, "G: trustedSpec 防客户端改 speed");
            CheckEq((double)fs.RadiusM, 0.1, "G: trustedSpec 防客户端改 radius");
            CheckEq(fs.LifetimeMs, 3000, "G: trustedSpec 防客户端改 lifetime");
            // activationId=1u 在本段从未被裁决 ⇒ 仍是 Pending 预留，因此权威 ID 仍为 0（尚未升级）。
            CheckEq(fstate.AuthorityNetId, 0u, "G: Pending 预留期权威 ID 为 0（客户端自称 9999 未被写入）");
            Check(!fstate.Stopped && !fstate.Hidden && !fstate.TakenOver, "G: 停止/隐藏/接管被归一为假");
            CheckEq(fstate.MoveTimeMs, 0.0, "G: MoveTimeMs 不来自客户端");
            CheckEq(fstate.HitTargets.Length, 0, "G: 命中集合不来自客户端");
            CheckEq(fstate.AllowedTargets.Length, 0, "G: 白名单不来自客户端");

            // --- 白名单只能来自可信入参 ---
            PMProjectileAdmissionOutcome wl = coord.RequestSpawn(
                MakeRequest(OWNER, 21u, 1u, ownerPos, new PMVector3(0f, 0f, 1f)), OWNER, AUTH_NET_ID, SOURCE,
                ownerPos, trusted, new uint[] { 55u }, At(1.0));
            CheckEq(wl.Result, PMProjectileAdmissionResult.Admitted, "G: 带可信白名单的 admission");
            Check(coord.TryObserveFrozen(PredKey(OWNER, 21u), out fs, out fstate), "G: 白名单冻结观察");
            CheckEq(fstate.AllowedTargets.Length, 1, "G: 白名单来自可信入参");
            CheckEq(fstate.AllowedTargets[0], 55u, "G: 白名单值正确");

            // --- 重复 key ---
            PMProjectileSpawnRequest dup = MakeRequest(OWNER, 22u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            CheckEq(coord.RequestSpawn(dup, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.Admitted, "G: 首次登记成功");
            CheckEq(coord.RequestSpawn(dup, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.AlreadyAdmitted, "G: 同一 key 重复登记被拒");

            // --- ID 单调（同 stream 回退 = 复用）---
            PMProjectileSpawnRequest mono = MakeRequest(OWNER, 1u, 2u, ownerPos, new PMVector3(0f, 0f, 1f));
            coord.RequestSpawn(MakeRequest(OWNER, 30u, 2u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0));
            PMProjectileAdmissionOutcome back = coord.RequestSpawn(
                MakeRequest(OWNER, 25u, 2u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0));
            CheckEq(back.Result, PMProjectileAdmissionResult.IdNotReserved, "G: 回退 ID 被水位拒绝（不复用）");
            CheckEq(mono.State.Key.ProjectileId, 1u, "G: （占位断言，保持 ID 语义可读）");

            CheckEq(coord.RejectedAdmissionCount > 0, true, "G: 拒绝计数可观测");
        }

        // =========================================================== H

        private static void TestStaleTargetAtConfirm()
        {
            _now = 9000.0;
            PMProjectileHistory history = NewHistory();
            RecordTarget(history, 55u, 1u, _now, new PMVector3(0f, 1f, 0.5f), true, 0.5f, 1.0f);
            RecordTarget(history, 56u, 1u, _now, new PMVector3(0f, 1f, 0.5f), true, 0.5f, 1.0f);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);

            PMProjectileKey k1 = PredKey(OWNER, 1u);
            PMProjectileKey k2 = PredKey(OWNER, 2u);
            coord.RequestSpawn(MakeRequest(OWNER, 1u, 4u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));
            coord.RequestSpawn(MakeRequest(OWNER, 2u, 4u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));

            CheckEq(Report(coord, NearBatch(k1, 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Pending, "H: 命中 55 暂存（Pending）");
            CheckEq(Report(coord, NearBatch(k2, 56u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Pending, "H: 命中 56 暂存（Pending）");

            // 目标 55 升 stream（旧流不再可回溯）；目标 56 死亡。
            RecordTarget(history, 55u, 2u, At(1.0), new PMVector3(0f, 1f, 0.5f), true, 0.5f, 1.0f);
            RecordTarget(history, 56u, 1u, At(1.0), new PMVector3(0f, 1f, 0.5f), false, 0.5f, 1.0f);

            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 4u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "H: 确认仍生效");
            CheckEq(act.SettledHitCount, 0, "H: 旧 stream / 死亡目标确认时都不结算");
            CheckEq(coord.RejectedStaleTargetCount, 2, "H: 两个陈旧目标被计数拒绝");

            PMProjectileSettlement[] settlements;
            CheckEq(coord.DrainSettlements(8, out settlements), 0, "H: 无结算出口");
        }

        // =========================================================== I

        private static void TestDrainOnce()
        {
            _now = 10000.0;
            PMProjectileHistory history = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            coord.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(3000, false), null, 0, Now());

            Report(coord, NearBatch(AuthKey(OWNER, 1u), 55u, 1u), At(1.0));
            CheckEq(coord.SettlementCount, 1, "I: 队列里有 1 条");
            PMProjectileSettlement[] settlements;
            CheckEq(coord.DrainSettlements(4, out settlements), 1, "I: 第一次 Drain 取到 1");
            CheckEq(coord.SettlementCount, 0, "I: 出队已提交");
            CheckEq(coord.DrainSettlements(4, out settlements), 0, "I: 第二次 Drain 无结果");

            // 同一弹对同一目标只结算一次（去重先提交再排队）。
            PMProjectileHitReportOutcome dupe = Report(coord, NearBatch(AuthKey(OWNER, 1u), 55u, 1u), At(1.0));
            CheckEq(dupe.Result, PMProjectileHitReportResult.NoSettleableHit, "I: 同弹同目标第二次不结算");
            CheckEq(coord.DrainSettlements(4, out settlements), 0, "I: 去重后无出口");

            // 不可结算的 Pending 结论绝不会从 DrainSettlements 出来。
            PMProjectileActivationApplyOutcome pending = coord.ResolveActivation(OWNER, 77u,
                PMActivationResult.Pending, At(1.0));
            CheckEq(pending.Result, PMProjectileActivationApplyResult.RecordedPending, "I: Pending 只记账");
            CheckEq(coord.DrainSettlements(4, out settlements), 0, "I: Pending 无结算");
        }

        // =========================================================== J

        private static void TestCapacityEpochCleanup()
        {
            // --- 输出队列压力：溢出是**显式失败（Faulted）**，不是「丢最旧 + 计数」---
            _now = 11000.0;
            PMProjectileHistory history = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);

            int admitted = 0;
            bool threw = false;
            for (uint i = 1u; i <= 600u; i++)
            {
                try
                {
                    PMProjectileAdmissionOutcome adm = coord.ServerDirectSpawn(
                        MakeAuthorityState(OWNER, i, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                        AUTH_NET_ID, Spec(3000, false), null, 0, At(1.0));
                    if (adm.Result == PMProjectileAdmissionResult.Admitted) { admitted++; }
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                    break;
                }
            }

            Check(threw, "J: 出口队列溢出必须抛 InvalidOperationException（不是静默丢最旧）");
            CheckEq(admitted, PMProjectileCoordinatorLimits.MaxSpawnEvents, "J: 前 512 条正常受理");
            Check(coord.IsFaulted, "J: 溢出后进入永久 Faulted");
            CheckEq(coord.FaultReason, PMProjectileFaultReason.SpawnEventQueueOverflow, "J: 故障原因明确");
            Check(coord.FaultMessage.Length > 0, "J: 故障说明可读");
            CheckEq(coord.SpawnEventCount, PMProjectileCoordinatorLimits.MaxSpawnEvents,
                "J: 既有排队项目不被覆盖/不被丢弃");

            PMProjectileSpawnEvent[] spawns;
            CheckEq(coord.DrainSpawnReady(4096, out spawns), PMProjectileCoordinatorLimits.MaxSpawnEvents,
                "J: Drain* 在 Faulted 下仍可取出现有项目（便于宿主处置）");
            bool firstWindow = true;
            for (int i = 0; i < spawns.Length; i++)
            {
                if (spawns[i].Key.ProjectileId != (uint)(i + 1)) { firstWindow = false; }
            }

            Check(firstWindow, "J: 取出的正是最早那 512 条（未被后来的覆盖）");
            CheckEq(coord.SpawnEventCount, 0, "J: Drain 后为空");
            Check(coord.IsFaulted, "J: Drain 不解除 Faulted（数据已不完整）");

            // 所有变更型调用都必须可见 Faulted。
            Check(ThrowsInvalidOp(delegate { coord.PurgeExpired(At(1.0)); }),
                "J: PurgeExpired 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate { coord.ResolveActivation(OWNER, 9u, PMActivationResult.Confirmed, At(1.0)); }),
                "J: ResolveActivation 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate { coord.ClearOwner(OWNER, At(1.0)); }),
                "J: ClearOwner 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate { coord.ResetState(At(1.0)); }),
                "J: ResetState 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate
                {
                    Report(coord, NearBatch(PredKey(OWNER, 1u), 55u, 1u), At(1.0));
                }), "J: ReportHits 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate
                {
                    coord.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0));
                }), "J: AdvanceMotion 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate
                {
                    coord.CatchUpMotion(AuthKey(OWNER, 1u), 50, At(1.0));
                }), "J: CatchUpMotion 在 Faulted 下抛异常");
            Check(ThrowsInvalidOp(delegate
                {
                    coord.ServerDirectSpawn(MakeAuthorityState(OWNER, 900u, 0u, new PMVector3(0f, 1f, 0f),
                        new PMVector3(0f, 0f, 1f)), AUTH_NET_ID, Spec(3000, false), null, 0, At(1.0));
                }), "J: ServerDirectSpawn 在 Faulted 下抛异常");

            // 升 epoch 是唯一恢复路径（对应「宿主要求断连重建」）。
            Check(coord.AdvanceEpoch(EP + 1u), "J: Faulted 后升 epoch 重建");
            Check(!coord.IsFaulted, "J: 升 epoch 清除故障标记");
            CheckEq(coord.FaultReason, PMProjectileFaultReason.None, "J: 故障原因归零");
            Check(coord.AdvanceEpoch(EP + 1u) == false, "J: epoch 仍只能前进");

            // --- 断连清理 ---
            _now = 12000.0;
            PMProjectileHistory history2 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord2 = new PMProjectileCoordinator(EP, history2, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 40u);
            coord2.RequestSpawn(MakeRequest(OWNER, 40u, 6u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            Report(coord2, NearBatch(key, 55u, 1u), At(1.0));
            PMProjectileKey other = PredKey(OWNER, 41u);
            Report(coord2, NearBatch(other, 55u, 1u), At(1.0));
            CheckEq(coord2.PendingSpawns.Count, 1, "J: 断连前 A 队列 1");
            CheckEq(coord2.PendingVerifies.Count, 1, "J: 断连前 B 队列 1");
            CheckEq(coord2.PendingHits.GroupCount, 1, "J: 断连前 C 队列 1");

            PMProjectileOwnerClearOutcome clear = coord2.ClearOwner(OWNER, At(1.0));
            CheckEq(clear.RetiredProjects, 1, "J: 断连回收 1 个项目");
            CheckEq(coord2.PendingSpawns.Count, 0, "J: 断连清空 A");
            CheckEq(coord2.PendingVerifies.Count, 0, "J: 断连清空 B");
            CheckEq(coord2.PendingHits.GroupCount, 0, "J: 断连清空 C");
            CheckEq(coord2.VerifyUsedCount(other), 0, "J: 断连清掉该 owner 的记账");
            CheckEq(coord2.SettlementCount, 0, "J: 断连不结算");

            // 旧 ID 不复活：同 (owner,origin) 复用 ProjectileId 会被水位拒。
            PMProjectileAdmissionOutcome reuse = coord2.RequestSpawn(
                MakeRequest(OWNER, 40u, 6u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, At(1.0));
            CheckEq(reuse.Result, PMProjectileAdmissionResult.IdNotReserved, "J: 断连后旧 ID 不复活");

            // --- 升 epoch ---
            PMProjectileHistory history3 = HistoryWithTarget(55u, 1u, 12000.0, true);
            PMProjectileCoordinator coord3 = new PMProjectileCoordinator(EP, history3, new AcceptAllFilter(), null);
            coord3.RequestSpawn(MakeRequest(OWNER, 60u, 6u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            Check(coord3.AdvanceEpoch(EP + 1u), "J: 升 epoch");
            Check(!coord3.AdvanceEpoch(EP), "J: epoch 只能前进");
            CheckEq(coord3.PendingSpawns.Count, 0, "J: 升代清空 A");
            CheckEq(coord3.Lifecycle.RegistrationCount, 0, "J: 升代清空登记");
            history3.Reset(EP + 1u);
            PMProjectileSpawnRequest oldEpoch = MakeRequest(OWNER, 61u, 6u, new PMVector3(0f, 1f, 0f),
                new PMVector3(0f, 0f, 1f));
            CheckEq(coord3.RequestSpawn(oldEpoch, OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f),
                Spec(3000, false), null, At(1.0)).Result, PMProjectileAdmissionResult.StaleEpoch,
                "J: 升代后旧 epoch 请求被拒");

            // --- ResetState：清暂存与登记，但保留水位（旧 ID 不复活）---
            PMProjectileHistory history4 = HistoryWithTarget(55u, 1u, 12000.0, true);
            PMProjectileCoordinator coord4 = new PMProjectileCoordinator(EP, history4, new AcceptAllFilter(), null);
            coord4.RequestSpawn(MakeRequest(OWNER, 70u, 6u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(3000, false), null, Now());
            CheckEq(coord4.ResetState(At(1.0)), 1, "J: ResetState 退回 1 个项目");
            CheckEq(coord4.Lifecycle.RegistrationCount, 0, "J: ResetState 清空登记");
            CheckEq(coord4.RequestSpawn(MakeRequest(OWNER, 70u, 6u, new PMVector3(0f, 1f, 0f),
                new PMVector3(0f, 0f, 1f)), OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f),
                Spec(3000, false), null, At(1.0)).Result, PMProjectileAdmissionResult.IdNotReserved,
                "J: ResetState 后旧 ID 不复活");
        }

        // =========================================================== K

        private static void TestNumericAndClock()
        {
            _now = 13000.0;
            PMProjectileHistory history = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);
            PMProjectileSpec trusted = Spec(3000, false);

            // --- NaN 墙钟 ---
            CheckEq(coord.RequestSpawn(MakeRequest(OWNER, 1u, 1u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, double.NaN).Result,
                PMProjectileAdmissionResult.InvalidClock, "K: NaN 墙钟被拒");
            CheckEq(coord.RequestSpawn(MakeRequest(OWNER, 1u, 1u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, double.PositiveInfinity).Result,
                PMProjectileAdmissionResult.InvalidClock, "K: Inf 墙钟被拒");

            // --- NaN 位置 ---
            PMProjectileSpawnRequest nanPos = MakeRequest(OWNER, 2u, 1u, ownerPos, new PMVector3(0f, 0f, 1f));
            nanPos.State.SpawnPosition = new PMVector3(float.NaN, 1f, 0f);
            CheckEq(coord.RequestSpawn(nanPos, OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(1.0)).Result,
                PMProjectileAdmissionResult.InvalidPosition, "K: NaN 枪口位置被拒");

            // --- 正常登记后：时钟倒退 ---
            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(
                MakeRequest(OWNER, 3u, 1u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, trusted, null, At(100.0));
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "K: 正常登记");
            CheckEq(coord.ResolveActivation(OWNER, 1u, PMActivationResult.Confirmed, At(1.0)).Result,
                PMProjectileActivationApplyResult.Applied, "K: 该 activation 已被确认（后续验算走 Confirmed 分支）");
            double t = Now();
            CheckEq(coord.AdvanceMotion(PredKey(OWNER, 3u), 50, t - 50.0).Result,
                PMProjectileAdvanceResult.InvalidClock, "K: 时钟倒退被拒");
            CheckEq(Report(coord, MakeBatch(PredKey(OWNER, 3u), ownerPos, new PMVector3(0f, 1f, 2.5f), 0, 55u, 1u),
                t - 50.0).Result, PMProjectileHitReportResult.InvalidClock, "K: 命中上报时钟倒退被拒");

            // --- 步长越界 ---
            CheckEq(coord.AdvanceMotion(PredKey(OWNER, 3u), 51, At(1.0)).Result,
                PMProjectileAdvanceResult.Invalid, "K: 单步 51ms 被拒");
            CheckEq(coord.AdvanceMotion(PredKey(OWNER, 3u), -1, At(1.0)).Result,
                PMProjectileAdvanceResult.Invalid, "K: 单步 -1ms 被拒");
            CheckEq(coord.AdvanceMotion(PredKey(OWNER, 3u), 0, At(1.0)).Result,
                PMProjectileAdvanceResult.Advanced, "K: 零步可查询且不改状态");

            // --- NaN 命中点由真实验证器拒 ---
            PMProjectileHitBatch nanHit = MakeBatch(PredKey(OWNER, 3u), ownerPos, new PMVector3(float.NaN, 1f, 2.5f), 0, 55u, 1u);
            PMProjectileHitReportOutcome nanRep = Report(coord, nanHit, At(1.0));
            CheckEq(nanRep.Result, PMProjectileHitReportResult.Rejected, "K: NaN 命中点整包被拒");
            CheckEq(nanRep.Reason, PMProjectileRejectReason.HitPositionNotFinite, "K: 拒因明确");

            // --- 超越点距离（L2 预算）---
            PMProjectileHitBatch within = NearBatch(PredKey(OWNER, 3u), 55u, 1u);
            CheckEq(Report(coord, within, At(1.0)).Result, PMProjectileHitReportResult.Settled, "K: 预算内通过");

            // --- 追赶预算钳制 ---
            _now = 14000.0;
            PMProjectileHistory history2 = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator coord2 = new PMProjectileCoordinator(EP, history2, new AcceptAllFilter(), null);
            coord2.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileAdvanceOutcome cu = coord2.CatchUpMotion(AuthKey(OWNER, 1u), 100000, At(1.0));
            Check(cu.Clamped, "K: 巨大追赶被钳制（不形成无界 loop）");
            Check(cu.AppliedMs <= PMProjectileCoordinatorLimits.MaxCatchUpBudgetMs, "K: 应用步数不超总预算");
            CheckEq(coord2.ClampedCatchUpCount, 1, "K: 钳制计数可观测");
        }

        // =========================================================== L

        private static void TestMotionAndStop()
        {
            _now = 15000.0;
            PMProjectileHistory history = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);

            // --- 注册先于追赶 ---
            CheckEq(coord.CatchUpMotion(PredKey(OWNER, 99u), 100, At(1.0)).Result,
                PMProjectileAdvanceResult.NotRegistered, "L: 未登记 key 不能追赶");

            PMProjectileKey predKey = PredKey(OWNER, 1u);
            coord.RequestSpawn(MakeRequest(OWNER, 1u, 1u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, Spec(3000, false), new uint[] { 55u }, Now());
            CheckEq(coord.CatchUpMotion(predKey, 100, At(1.0)).Result,
                PMProjectileAdvanceResult.NotAuthority, "L: 预测弹不能走权威追赶");

            coord.ServerDirectSpawn(MakeAuthorityState(OWNER, 2u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), new uint[] { 55u }, 0, Now());
            PMProjectileAdvanceOutcome cu = coord.CatchUpMotion(AuthKey(OWNER, 2u), 100, At(1.0));
            CheckEq(cu.Result, PMProjectileAdvanceResult.Advanced, "L: 登记后可追赶");
            CheckEq(cu.CatchUp, PMProjectileCatchUpResult.Started, "L: 追赶只开始一次");
            CheckEq(coord.CatchUpMotion(AuthKey(OWNER, 2u), 50, At(1.0)).CatchUp,
                PMProjectileCatchUpResult.AlreadyStarted, "L: 重复追赶被识破");

            // --- 运动不覆盖保护字段 ---
            PMProjectileSpec fs;
            PMProjectileState fstate;
            coord.TryObserveFrozen(predKey, out fs, out fstate);
            uint authBefore = fstate.AuthorityNetId;
            uint actBefore = fstate.ActivationId;
            int allowedBefore = fstate.AllowedTargets.Length;
            coord.AdvanceMotion(predKey, 50, At(1.0));
            coord.TryObserveFrozen(predKey, out fs, out fstate);
            CheckEq(fstate.AuthorityNetId, authBefore, "L: 推进不改 AuthorityNetId");
            CheckEq(fstate.ActivationId, actBefore, "L: 推进不改 ActivationId");
            CheckEq(fstate.AllowedTargets.Length, allowedBefore, "L: 推进不改白名单");
            Check(fstate.AllowedTargets.Length == 1 && fstate.AllowedTargets[0] == 55u, "L: 白名单值仍可信");

            // --- 宿主 hook：运动被替换，但不能决定伤害 ---
            _now = 16000.0;
            PMProjectileHistory history2 = HistoryWithTarget(55u, 1u, _now, true);
            ZOnlyMotionHook hook = new ZOnlyMotionHook();
            PMProjectileCoordinator coord2 = new PMProjectileCoordinator(EP, history2, new AcceptAllFilter(), hook);
            coord2.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(1f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(coord2.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.Advanced, "L: hook 路径仍推进");
            coord2.TryObserveFrozen(AuthKey(OWNER, 1u), out fs, out fstate);
            CheckEq((double)fstate.Position.X, 1.0, "L: hook 生效（X 不被内核直线推进）");
            CheckEq((double)fstate.Position.Z, 1.0, "L: hook 生效（Z = 20m/s * 50ms）");
            CheckEq(hook.Steps, 1, "L: hook 收到 1 步");
            CheckEq(coord2.DrainSettlements(4, out _unusedSettlements), 0, "L: hook 不产生伤害结算");

            // --- StopOnHit 终态 ---
            _now = 17000.0;
            PMProjectileHistory history3 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord3 = new PMProjectileCoordinator(EP, history3, new AcceptAllFilter(), null);
            coord3.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, true), null, 0, Now());
            CheckEq(Report(coord3, NearBatch(AuthKey(OWNER, 1u), 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Settled, "L: 命中即结算");
            PMProjectileStopEvent[] stops;
            CheckEq(coord3.DrainStopped(4, out stops), 1, "L: StopOnHit 产生停止出口");
            CheckEq(coord3.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.NotMovable, "L: StopOnHit 之后运动终态");

            // --- hook 触发 stop ---
            _now = 18000.0;
            PMProjectileHistory history4 = HistoryWithTarget(55u, 1u, _now, true);
            ZOnlyMotionHook hook2 = new ZOnlyMotionHook();
            hook2.StopAtStep = 2;
            PMProjectileCoordinator coord4 = new PMProjectileCoordinator(EP, history4, new AcceptAllFilter(), hook2);
            coord4.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(coord4.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.Advanced, "L: hook stop 之前仍推进");
            CheckEq(coord4.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.Stopped, "L: hook 请求停止");
            CheckEq(coord4.DrainStopped(4, out stops), 1, "L: hook 停止产生停止出口");

            // --- 拒绝性 filter 不结算 ---
            _now = 19000.0;
            PMProjectileHistory history5 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord5 = new PMProjectileCoordinator(EP, history5, new RejectAllFilter(), null);
            coord5.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileHitReportOutcome filt = Report(coord5, NearBatch(AuthKey(OWNER, 1u), 55u, 1u), At(1.0));
            CheckEq(filt.Result, PMProjectileHitReportResult.NoSettleableHit, "L: 权威 filter 拒绝则不结算");
            CheckEq(coord5.DrainSettlements(4, out _unusedSettlements), 0, "L: filter 拒绝无出口");

            // --- 无 filter（fail-closed）---
            _now = 20000.0;
            PMProjectileHistory history6 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator coord6 = new PMProjectileCoordinator(EP, history6, null, null);
            coord6.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(Report(coord6, NearBatch(AuthKey(OWNER, 1u), 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.NoSettleableHit, "L: 无权威 filter 一律不结算");
        }

        private static PMProjectileSettlement[] _unusedSettlements;

        // =========================================================== M

        private static void TestQueueAndQuotaEdges()
        {
            // --- M1/M2/M3：B 队列单 key 5 / 总 128 / TTL 联动清理 ---
            _now = 21000.0;
            PMProjectileHistory h1 = NewHistory();
            PMProjectileCoordinator c1 = new PMProjectileCoordinator(EP, h1, new AcceptAllFilter(), null);
            PMProjectileKey k1 = PredKey(OWNER, 1u);
            for (int i = 0; i < 5; i++)
            {
                CheckEq(Report(c1, NearBatch(k1, 55u, 1u), At(1.0)).Result,
                    PMProjectileHitReportResult.StashedForSpawn, "M: 单 key 前 5 条暂存 #" + i);
            }

            CheckEq(Report(c1, NearBatch(k1, 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Capacity, "M: 单 key 第 6 条超限被拒（不是默默丢）");
            CheckEq(c1.PendingVerifies.CountForKey(k1), 5, "M: 单 key 上限 5");
            CheckEq(c1.StashCapacityRejectionCount, 1, "M: 整合器侧容量拒绝计数");
            CheckEq(c1.PendingVerifies.RejectedByCapacityCount, 1, "M: A1 侧计数一致（无静默丢）");

            for (uint i = 2u; i <= 40u; i++)
            {
                for (int j = 0; j < 5; j++)
                {
                    Report(c1, NearBatch(PredKey(OWNER, i), 55u, 1u), At(1.0));
                }
            }

            CheckEq(c1.PendingVerifies.Count, PMProjectileLimits.MaxPendingVerifies, "M: B 队列总量有界 128");
            Check(c1.PendingVerifies.RejectedByCapacityCount > 1, "M: 总量溢出被计数");

            PMProjectilePurgeOutcome purge = c1.PurgeExpired(At(2001.0));
            CheckEq(c1.PendingVerifies.Count, 0, "M: TTL 联动清空 B 队列");
            Check(purge.PendingVerifiesExpired > 0, "M: 清理条数可观测");
            CheckEq(c1.SettlementCount, 0, "M: B 队列 TTL 不结算");

            // --- M4：真实 Verify 配额（由验证器 VerifyConsumed 驱动，与墓碑准入不双计数）---
            _now = 22000.0;
            PMProjectileHistory h2 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c2 = new PMProjectileCoordinator(EP, h2, new AcceptAllFilter(), null);
            c2.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileKey qk = AuthKey(OWNER, 1u);

            for (int i = 0; i < 5; i++)
            {
                PMProjectileHitReportOutcome r = Report(c2, NearBatch(qk, 55u, 1u), At(1.0));
                Check(r.VerifyConsumed, "M: 第 " + (i + 1) + " 次 Verify 消耗配额");
                CheckEq(r.VerifyUsed, i + 1, "M: 配额逐次 +1（无双计数）");
            }

            PMProjectileHitReportOutcome over = Report(c2, NearBatch(qk, 55u, 1u), At(1.0));
            CheckEq(over.Result, PMProjectileHitReportResult.QuotaExhausted, "M: 第 6 次 Verify 超配额");
            Check(!over.VerifyConsumed, "M: 超配额不再消耗");
            CheckEq(c2.VerifyUsedCount(qk), 5, "M: 配额上限 = 5");
            CheckEq(c2.QuotaRejectionCount, 1, "M: 配额拒绝可观测");
            CheckEq(c2.Lifecycle.AdmitVerify(qk, At(1.0)).Result, PMProjectileVerifyAdmission.Allowed,
                "M: 墓碑准入（AdmitVerify）与 Verify 配额是两个口径，不双计数");

            // --- M5：批内 stream 版本不符 / 未知目标：skip 而不是结算 ---
            _now = 23000.0;
            PMProjectileHistory h3 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c3 = new PMProjectileCoordinator(EP, h3, new AcceptAllFilter(), null);
            c3.ServerDirectSpawn(
                MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileHitReportOutcome mm = Report(c3, 
                MakeBatch(AuthKey(OWNER, 1u), new PMVector3(0f, 1f, 0f), new PMVector3(0f, 1f, 0.4f), 0, 55u, 99u),
                At(1.0));
            CheckEq(mm.Result, PMProjectileHitReportResult.NoSettleableHit, "M: stream 版本不符 → skip 不结算");
            CheckEq(c3.DrainSettlements(4, out _unusedSettlements), 0, "M: stream 不符无结算出口");
            CheckEq(Report(c3, NearBatch(AuthKey(OWNER, 1u), 777u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.NoSettleableHit, "M: 未知目标 → skip 不结算");

            // --- M6：结算队列压力（**绝不丢已接受出口**：溢出即 Faulted + 抛异常）---
            _now = 24000.0;
            PMProjectileHistory h4 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c4 = new PMProjectileCoordinator(EP, h4, new AcceptAllFilter(), null);
            int settledBefore = 0;
            bool settlementThrew = false;
            for (uint i = 1u; i <= 600u; i++)
            {
                PMProjectileAdmissionOutcome adm;
                try
                {
                    adm = c4.ServerDirectSpawn(
                        MakeAuthorityState(OWNER, i, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                        AUTH_NET_ID, Spec(60000, false), null, 0, Now());
                }
                catch (InvalidOperationException)
                {
                    // 生成出口自己也会溢出：本段只验证**结算出口**，因此每轮把生成出口抽干。
                    settlementThrew = true;
                    break;
                }

                if (adm.Result != PMProjectileAdmissionResult.Admitted) { break; }

                PMProjectileSpawnEvent[] drained;
                c4.DrainSpawnReady(64, out drained);

                try
                {
                    PMProjectileHitReportOutcome rep = Report(c4, NearBatch(AuthKey(OWNER, i), 55u, 1u), Now());
                    if (rep.Result == PMProjectileHitReportResult.Settled) { settledBefore++; }
                }
                catch (InvalidOperationException)
                {
                    settlementThrew = true;
                    break;
                }
            }

            Check(settlementThrew, "M: 结算出口溢出必须显式失败（不丢真实伤害）");
            Check(c4.IsFaulted, "M: 结算溢出后 Faulted");
            CheckEq(c4.FaultReason, PMProjectileFaultReason.SettlementQueueOverflow, "M: 故障原因 = 结算出口");
            CheckEq(settledBefore, PMProjectileCoordinatorLimits.MaxSettlements,
                "M: 已受理的 512 条结算一条不少");
            CheckEq(c4.SettlementCount, PMProjectileCoordinatorLimits.MaxSettlements,
                "M: 既有结算项目不被覆盖/不被丢最旧");
            PMProjectileSettlement[] ss;
            CheckEq(c4.DrainSettlements(4096, out ss), PMProjectileCoordinatorLimits.MaxSettlements,
                "M: Faulted 下仍可 Drain 出现有结算");
            for (int i = 0; i < ss.Length; i++)
            {
                CheckEq(ss[i].Hits[0].TargetNetId, 55u, "M: 结算内容完整 #" + i);
            }

            CheckEq(c4.SettlementCount, 0, "M: Drain 后为空");
            Check(ThrowsInvalidOp(delegate
            {
                Report(c4, NearBatch(AuthKey(OWNER, 600u), 55u, 1u), At(1.0));
            }), "M: Faulted 后任何上报都被拒");
        }

        // =========================================================== N

        private static void TestPromotion()
        {
            _now = 25000.0;
            PMProjectileHistory history = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 12u);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);

            // 1) 收请求时**预留 ID**（activation 未定）⇒ 挂起，不生成。
            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(
                MakeRequest(OWNER, 12u, 42u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, Spec(60000, false), null, Now());
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "N: admission 通过");
            Check(adm.SpawnPending && !adm.Spawned, "N: 预留等待裁决");

            PMProjectileRegistration reg;
            Check(coord.TryObserveRegistration(key, out reg), "N: 登记观察");
            Check(reg.Predicted && !reg.Promoted, "N: 预留期仍是预测身份");
            CheckEq(reg.AuthorityNetId, 0u, "N: 预留期权威 ID 为 0（尚未升级）");

            // 预留期不能追赶：这不是“忽略 NotAuthority”，而是预留身份确实还不属于权威模拟。
            CheckEq(coord.CatchUpMotion(key, 100, At(1.0)).Result, PMProjectileAdvanceResult.NotAuthority,
                "N: Pending 预留期不能追赶（NotAuthority）");

            // 2) 确认 ⇒ 原地升级 ⇒ CatchUpMotion **真的推弹**。
            PMProjectileActivationApplyOutcome act = coord.ResolveActivation(OWNER, 42u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "N: 确认生效");
            CheckEq(act.SpawnedKeys, 1, "N: 生成 1 颗");
            CheckEq(coord.PromotedProjectileCount, 1, "N: 恰好一次原地升级");

            Check(coord.TryObserveRegistration(key, out reg), "N: 升级后登记观察");
            Check(!reg.Predicted && reg.Promoted, "N: 已升级为权威本体");
            CheckEq(reg.AuthorityNetId, AUTH_NET_ID, "N: 权威 ID 真实非 0");
            Check(reg.Key.Equals(key), "N: Key/ID 未被重新分配");
            CheckEq(reg.Key.Origin, PMProjectileOrigin.ClientPredicted, "N: Origin 未改");

            PMProjectileAdvanceOutcome cu = coord.CatchUpMotion(key, 100, At(1.0));
            CheckEq(cu.Result, PMProjectileAdvanceResult.Advanced, "N: 确认后可追赶并真实推进");
            CheckEq(cu.CatchUp, PMProjectileCatchUpResult.Started, "N: 追赶开始");
            CheckEq(cu.AppliedMs, 100, "N: 追赶应用 100ms");
            CheckEq(coord.CatchUpMotion(key, 50, At(1.0)).CatchUp, PMProjectileCatchUpResult.AlreadyStarted,
                "N: 追赶只开始一次");

            // speed 10m/s：首次 CatchUp 100ms → 1.0m；重复 CatchUp（AlreadyStarted）仍会再推 50ms，
            // 因为“追赶总量”由调用方给的 totalMs 决定（start 只允许一次，推进不锁死）。
            PMProjectileSpec fs;
            PMProjectileState fstate;
            Check(coord.TryObserveFrozen(key, out fs, out fstate), "N: 冻结观察");
            CheckEq((double)fstate.Position.Z, 1.5, "N: 位置真的沿 +Z 推了 1.5m（100ms + 50ms）");
            CheckEq(fstate.AuthorityNetId, AUTH_NET_ID, "N: 冻结状态的权威 ID 同步为真实值");

            // 生成出口里的冻结状态必须已经带真实权威 ID（下行 Snapshot 要求「权威ID 非 0」）。
            PMProjectileSpawnEvent[] spawns;
            CheckEq(coord.DrainSpawnReady(4, out spawns), 1, "N: 生成出口 1 条");
            CheckEq(spawns[0].State.AuthorityNetId, AUTH_NET_ID, "N: 生成出口的冻结状态权威 ID 非 0");

            // 3) 升级的不变式：幂等 / 只限 Confirmed / 拒 0 权威 ID / 权威原生不算升级。
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(key, AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.AlreadyPromoted, "N: 重复升级幂等");
            CheckEq(coord.PromotedProjectileCount, 1, "N: 幂等不重复计数");

            PMProjectileKey pend = PredKey(OWNER, 13u);
            coord.RequestSpawn(MakeRequest(OWNER, 13u, 43u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, Spec(60000, false), null, At(1.0));
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(pend, AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.NotConfirmed, "N: Pending 预留不得升级");
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(pend, 0u, At(1.0)).Result,
                PMProjectilePromoteResult.InvalidAuthority, "N: 权威 ID=0 不得升级");
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(AuthKey(OWNER, 20u), AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.UnknownKey, "N: 未登记 key -> UnknownKey");
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(
                new PMProjectileKey(EP + 1u, OWNER, 12u, PMProjectileOrigin.ClientPredicted), AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.StaleEpoch, "N: 跨 epoch -> StaleEpoch");

            coord.ServerDirectSpawn(MakeAuthorityState(OWNER, 21u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, At(1.0));
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(AuthKey(OWNER, 21u), AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.NotPredicted, "N: 权威原生登记 -> NotPredicted");
            CheckEq(coord.PromotedProjectileCount, 1, "N: 权威原生不计数");

            // 拒绝（Rejected）的 activation 不得升级。
            PMProjectileKey rej = PredKey(OWNER, 14u);
            coord.RequestSpawn(MakeRequest(OWNER, 14u, 44u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, Spec(60000, false), null, At(1.0));
            coord.ResolveActivation(OWNER, 44u, PMActivationResult.Rejected, At(1.0));
            CheckEq(coord.Lifecycle.TryPromoteReservedToAuthority(rej, AUTH_NET_ID, At(1.0)).Result,
                PMProjectilePromoteResult.Retired, "N: 已拒绝的预留已进终态标记，不能升级（Retired 是更强的终态信号）");
        }

        // =========================================================== P

        private static void TestHostMotionFailClosed()
        {
            // 1) TryStep 返回 false ⇒ 本步不推进 + 就地停止。
            _now = 26000.0;
            PMProjectileHistory h1 = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator c1 = new PMProjectileCoordinator(EP, h1, new AcceptAllFilter(), new FalseStepHook());
            c1.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileAdvanceOutcome a1 = c1.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0));
            CheckEq(a1.Result, PMProjectileAdvanceResult.HostMotionFaulted, "P: hook false -> HostMotionFaulted");
            Check(a1.Stopped, "P: fail closed 必须停止");
            CheckEq((double)a1.Position.Z, 0.0, "P: 本步不推进（停在真实位置）");
            CheckEq(c1.HostMotionFailureCount, 1, "P: 失败可观测");

            PMProjectileSpec fs;
            PMProjectileState fst;
            c1.TryObserveFrozen(AuthKey(OWNER, 1u), out fs, out fst);
            CheckEq((double)fst.Position.Z, 0.0, "P: 账本位置仍是真实位置（没有沿直线穿过去）");
            Check(fst.Stopped, "P: 账本状态已停止");
            Check(PMProjectileFiniteCheck(fst.Position), "P: 账本里没有 NaN");
            PMProjectileStopEvent[] stops;
            CheckEq(c1.DrainStopped(4, out stops), 1, "P: 产生停止出口");
            CheckEq(c1.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result, PMProjectileAdvanceResult.NotMovable,
                "P: fail closed 之后运动是终态（不偷偷继续飞）");

            // 2) TryStep 输出 NaN ⇒ 不写账本 + 就地停止。
            _now = 26500.0;
            PMProjectileHistory h2 = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator c2 = new PMProjectileCoordinator(EP, h2, new AcceptAllFilter(), new NanStepHook());
            c2.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(c2.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.HostMotionFaulted, "P: hook NaN -> HostMotionFaulted");
            c2.TryObserveFrozen(AuthKey(OWNER, 1u), out fs, out fst);
            Check(PMProjectileFiniteCheck(fst.Position), "P: NaN 没有进入冻结状态");
            CheckEq((double)fst.Position.Z, 0.0, "P: 位置仍是真实位置");

            // 3) TryStep 抛异常 ⇒ fail closed。
            _now = 27000.0;
            PMProjectileHistory h3 = HistoryWithTarget(55u, 1u, _now, true);
            PMProjectileCoordinator c3 = new PMProjectileCoordinator(EP, h3, new AcceptAllFilter(), new ThrowingStepHook());
            c3.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileAdvanceOutcome a3 = c3.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0));
            CheckEq(a3.Result, PMProjectileAdvanceResult.HostMotionFaulted, "P: hook 抛异常 -> HostMotionFaulted");
            CheckEq(c3.HostMotionFailureCount, 1, "P: 异常路径也计数");

            // 4) TryStop 抛异常 / NaN 停止点 ⇒ 当步运动已生效，但立即就地停止。
            _now = 27500.0;
            PMProjectileHistory h4 = HistoryWithTarget(55u, 1u, _now, true);
            BadStopHook badStop = new BadStopHook();
            badStop.ThrowOnStop = true;
            PMProjectileCoordinator c4 = new PMProjectileCoordinator(EP, h4, new AcceptAllFilter(), badStop);
            c4.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            PMProjectileAdvanceOutcome a4 = c4.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0));
            CheckEq(a4.Result, PMProjectileAdvanceResult.HostMotionFaulted, "P: TryStop 抛异常 -> HostMotionFaulted");
            CheckEq((double)a4.Position.Z, 0.5, "P: 已生效的那一步位置保留（0.5m）");
            CheckEq(c4.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result, PMProjectileAdvanceResult.NotMovable,
                "P: 失败停止后运动终态");

            _now = 28000.0;
            PMProjectileHistory h5 = HistoryWithTarget(55u, 1u, _now, true);
            BadStopHook nanStop = new BadStopHook();
            nanStop.NanStopPoint = true;
            PMProjectileCoordinator c5 = new PMProjectileCoordinator(EP, h5, new AcceptAllFilter(), nanStop);
            c5.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(c5.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.HostMotionFaulted, "P: NaN 停止点 -> HostMotionFaulted");
            c5.TryObserveFrozen(AuthKey(OWNER, 1u), out fs, out fst);
            Check(PMProjectileFiniteCheck(fst.Position), "P: NaN 停止点没有写进账本");

            // 5) 合法行为：用直线回填（显式 true）+ 把 snapshot 改脏 ⇒ 账本不受影响（深 clone）。
            _now = 28500.0;
            PMProjectileHistory h6 = HistoryWithTarget(55u, 1u, _now, true);
            MutatingHook mutate = new MutatingHook();
            PMProjectileCoordinator c6 = new PMProjectileCoordinator(EP, h6, new AcceptAllFilter(), mutate);
            c6.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            CheckEq(c6.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0)).Result,
                PMProjectileAdvanceResult.Advanced, "P: 合法回填仍推进");
            CheckEq((double)mutate.SeenSnapshotPosition.Z, 0.0, "P: 传给 hook 的 snapshot 是**真实当前位置**");
            c6.TryObserveFrozen(AuthKey(OWNER, 1u), out fs, out fst);
            CheckEq((double)fst.Position.Z, 0.5, "P: snapshot 是深 clone（hook 改脏写不进账本）");
            CheckEq(c6.HostMotionFailureCount, 0, "P: 合法调用不计失败");

            // 6) ZOnly hook 仍必须观察到真实位置（原有用例的加强断言）。
            _now = 29000.0;
            PMProjectileHistory h7 = HistoryWithTarget(55u, 1u, _now, true);
            ZOnlyMotionHook z = new ZOnlyMotionHook();
            PMProjectileCoordinator c7 = new PMProjectileCoordinator(EP, h7, new AcceptAllFilter(), z);
            c7.ServerDirectSpawn(MakeAuthorityState(OWNER, 1u, 0u, new PMVector3(1f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, Now());
            c7.AdvanceMotion(AuthKey(OWNER, 1u), 50, At(1.0));
            CheckEq((double)z.LastSeenSnapshotPosition.X, 1.0, "P: hook 看到真实 X");
            CheckEq((double)z.LastSeenSnapshotPosition.Z, 0.0, "P: hook 看到真实 Z");
        }

        private static bool PMProjectileFiniteCheck(PMVector3 v)
        {
            return !float.IsNaN(v.X) && !float.IsInfinity(v.X)
                && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y)
                && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
        }

        // =========================================================== Q

        private static void TestPendingHitDedupReservation()
        {
            _now = 30000.0;
            PMProjectileHistory h = HistoryNear(55u, 1u, _now, true);
            RecordTarget(h, 56u, 1u, _now, new PMVector3(0f, 1f, 0.5f), true, 0.5f, 1.0f);
            PMProjectileCoordinator c = new PMProjectileCoordinator(EP, h, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 30u);
            c.RequestSpawn(MakeRequest(OWNER, 30u, 55u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(60000, false), null, Now());

            // 同一目标反复上报（UDP 重传/客户端重报，共 4 次）：只有第一次入组。
            // 注意 Verify 配额上限是 5/key，所以这里只用 5 次上报（配额由 M4 覆盖）。
            for (int i = 0; i < 4; i++)
            {
                CheckEq(Report(c, NearBatch(key, 55u, 1u), At(1.0)).Result,
                    PMProjectileHitReportResult.Pending, "Q: Pending 结论 #" + i);
            }

            CheckEq(c.DedupPendingHitSkipCount, 3, "Q: 3 次重复被暂存期去重预留挡下");
            CheckEq(c.PendingHits.GroupCount, 1, "Q: 只有 1 组");
            PMProjectilePendingHitGroup g;
            Check(c.PendingHits.TryGetGroup(key, out g), "Q: 组可观测");
            CheckEq(g.SetCount, 1, "Q: 组内只有 1 套结论（未反复入同目标）");
            CheckEq(c.PendingHits.DroppedAppendCount, 0, "Q: 追加上限没有被重传耗尽");

            // 另一个目标仍能正常入组（这就是“不预留就会丢真实伤害”的反面）。
            CheckEq(Report(c, NearBatch(key, 56u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Pending, "Q: 新目标仍可入组");
            Check(c.PendingHits.TryGetGroup(key, out g), "Q: 组可观测 #2");
            CheckEq(g.SetCount, 2, "Q: 组内 2 套结论（不同目标）");

            PMProjectileActivationApplyOutcome act = c.ResolveActivation(OWNER, 55u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(act.SettledHitCount, 2, "Q: 确认时两个目标各结算一次");
            PMProjectileSettlement[] ss;
            CheckEq(c.DrainSettlements(4, out ss), 1, "Q: 一条结算");
            CheckEq(ss[0].HitCount, 2, "Q: 含 2 个命中");
            CheckEq(c.DrainSettlements(4, out ss), 0, "Q: 无重复结算");

            // --- 挂起 Spawn 的 TTL 让 C 组连带作废：不结算、预留释放、key 进终态（同 key 不可重建）---
            _now = 31000.0;
            PMProjectileHistory h2 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c2 = new PMProjectileCoordinator(EP, h2, new AcceptAllFilter(), null);
            PMProjectileKey k2 = PredKey(OWNER, 31u);
            c2.RequestSpawn(MakeRequest(OWNER, 31u, 56u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(60000, false), null, Now());
            CheckEq(Report(c2, NearBatch(k2, 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Pending, "Q: TTL 前暂存");
            CheckEq(c2.ReleasedPendingHitReservationCount, 0, "Q: 暂存中不释放预留");

            PMProjectilePurgeOutcome purge = c2.PurgeExpired(At(2001.0));
            CheckEq(purge.PendingSpawnsExpired, 1, "Q: 挂起 Spawn 超 TTL 被清理");
            CheckEq(purge.PendingHitGroupsExpired, 0,
                "Q: C 组不是自己 TTL 到期——是随 A 队列清理被连带丢弃（联动顺序：A 先到期）");
            CheckEq(c2.ReleasedPendingHitReservationCount, 1, "Q: 去重预留随组释放（不留孤儿预留）");
            CheckEq(c2.PendingHits.GroupCount, 0, "Q: C 队列已清空");
            CheckEq(c2.SettlementCount, 0, "Q: TTL 不结算");
            Check(!c2.Lifecycle.IsRegistered(k2), "Q: ID 预留一并退回");

            // 同一 key 重报：已进终态标记 ⇒ 不得重建结算（终态不倒退）。
            CheckEq(Report(c2, NearBatch(k2, 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Retired, "Q: 同 key 超时后不能重建结算（Retired）");
            CheckEq(c2.SettlementCount, 0, "Q: 仍不结算");

            // --- 可达的「C 组被容量淘汰」路径：预留必须释放，同 key 重报后仍只结算一次 ---
            _now = 33000.0;
            PMProjectileHistory h3 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c3 = new PMProjectileCoordinator(EP, h3, new AcceptAllFilter(), null);
            PMVector3 oPos = new PMVector3(0f, 1f, 0f);

            // owner 500：65 颗；owner 600：64 颗 ⇒ 共 129 组（超过 C 队列 128 组上限）。
            for (uint i = 1u; i <= 65u; i++)
            {
                c3.RequestSpawn(MakeRequest(500u, i, 1u, oPos, new PMVector3(0f, 0f, 1f)),
                    500u, AUTH_NET_ID, SOURCE, oPos, Spec(60000, false), null, At(1.0));
                Report(c3, NearBatch(PredKey(500u, i), 55u, 1u), Now());
            }

            for (uint i = 1u; i <= 64u; i++)
            {
                c3.RequestSpawn(MakeRequest(600u, i, 2u, oPos, new PMVector3(0f, 0f, 1f)),
                    600u, AUTH_NET_ID, SOURCE, oPos, Spec(60000, false), null, At(1.0));
                Report(c3, NearBatch(PredKey(600u, i), 55u, 1u), Now());
            }

            CheckEq(c3.PendingHits.GroupCount, PMProjectileLimits.MaxPendingHitGroups, "Q: C 组数被有界在 128");
            CheckEq(c3.PendingHits.DroppedGroupCount, 1, "Q: 第 129 组触发「丢最旧未结算组」（可观测）");
            Check(c3.ReleasedPendingHitReservationCount >= 1, "Q: 被淘汰组的去重预留已释放");

            // 被淘汰的是最早创建的组：owner 500 / id 1。重报必须能重新入组（预留没被永久挡住）。
            // 注意：重入又会把当时最旧的组（500/2）挤出 —— 128 上限下的有界丢最旧是契约允许的，
            // 但它必须**可观测**，而不能是“预留永久挡住某目标”那种静默丢失。
            CheckEq(Report(c3, NearBatch(PredKey(500u, 1u), 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Pending, "Q: 被淘汰 key 重报可重新入组（预留已释放）");
            CheckEq(c3.PendingHits.DroppedGroupCount, 2, "Q: 重入又挤出最旧组（仍可观测）");
            Check(c3.ReleasedPendingHitReservationCount >= 2, "Q: 每次丢弃都释放对应预留");

            PMProjectileActivationApplyOutcome a500 = c3.ResolveActivation(500u, 1u,
                PMActivationResult.Confirmed, At(1.0));
            PMProjectileActivationApplyOutcome a600 = c3.ResolveActivation(600u, 2u,
                PMActivationResult.Confirmed, At(1.0));
            CheckEq(a500.SettledHitCount, 64,
                "Q: owner500 里 64 颗结算（65 颗中恰有 1 颗因 128 上限被丢最旧，不静默、不重复）");
            CheckEq(a600.SettledHitCount, 64, "Q: owner600 的 64 颗各结算一次");

            PMProjectileSettlement[] many;
            CheckEq(c3.DrainSettlements(512, out many), 128, "Q: 共 128 条结算（其余 1 组是已观测的丢最旧）");
            HashSet<uint> settledKeys = new HashSet<uint>();
            bool allSingleHit = true;
            bool evictedKeyResettled = false;
            for (int i = 0; i < many.Length; i++)
            {
                if (many[i].HitCount != 1) { allSingleHit = false; }
                if (many[i].Key.OwnerNetId == 500u && many[i].Key.ProjectileId == 1u) { evictedKeyResettled = true; }
                settledKeys.Add(many[i].Key.OwnerNetId * 1000u + many[i].Key.ProjectileId);
            }

            CheckEq(settledKeys.Count, 128, "Q: 128 个不同 key 各恰好一条（无重复结算）");
            Check(allSingleHit, "Q: 每条结算只含 1 个命中（同弹同目标一次）");
            Check(evictedKeyResettled, "Q: 被淘汰后重报的那颗确实结算了（预留真的被释放，不是永久挡住）");
        }

        // =========================================================== R

        private static void TestStopOnHitWhilePending()
        {
            _now = 32000.0;
            PMProjectileHistory h = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c = new PMProjectileCoordinator(EP, h, new AcceptAllFilter(), null);
            PMProjectileKey key = PredKey(OWNER, 33u);
            c.RequestSpawn(MakeRequest(OWNER, 33u, 60u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(60000, true), null, Now());

            PMProjectileHitReportOutcome rep = Report(c, NearBatch(key, 55u, 1u), At(1.0));
            CheckEq(rep.Result, PMProjectileHitReportResult.Pending, "R: L0 结论 Pending");
            Check(rep.StopRecorded, "R: StopOnHit 在 Pending 期间就已停（不是等 Confirm）");
            PMProjectileStopEvent[] stopEvents;
            CheckEq(c.DrainStopped(4, out stopEvents), 1, "R: 产生 1 条停止出口");
            CheckEq(stopEvents[0].Key, key, "R: 停止出口带 key");
            CheckEq(c.AdvanceMotion(key, 50, At(1.0)).Result, PMProjectileAdvanceResult.NotMovable,
                "R: Pending 停后运动是终态（不继续飞）");

            PMProjectileActivationApplyOutcome act = c.ResolveActivation(OWNER, 60u,
                PMActivationResult.Confirmed, At(100.0));
            CheckEq(act.SettledHitCount, 1, "R: 确认时结算候选（墓碑窗口内）");
            CheckEq(c.DrainStopped(4, out stopEvents), 0, "R: 不产生第二次停止通知");
            PMProjectileSettlement[] ss;
            CheckEq(c.DrainSettlements(4, out ss), 1, "R: 一次结算");
            Check(ss[0].StopOnHit, "R: StopOnHit 透传");

            // StopOnHit=false 时 Pending 不得停（对照）。
            _now = 32500.0;
            PMProjectileHistory h2 = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c2 = new PMProjectileCoordinator(EP, h2, new AcceptAllFilter(), null);
            PMProjectileKey k2 = PredKey(OWNER, 34u);
            c2.RequestSpawn(MakeRequest(OWNER, 34u, 61u, new PMVector3(0f, 1f, 0f), new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, new PMVector3(0f, 1f, 0f), Spec(60000, false), null, Now());
            PMProjectileHitReportOutcome rep2 = Report(c2, NearBatch(k2, 55u, 1u), At(1.0));
            CheckEq(rep2.Result, PMProjectileHitReportResult.Pending, "R: 对照组也是 Pending");
            Check(!rep2.StopRecorded, "R: StopOnHit=false 时 Pending 不停（对照）");
            CheckEq(c2.AdvanceMotion(k2, 50, At(1.0)).Result, PMProjectileAdvanceResult.Advanced,
                "R: 对照组继续飞");
        }

        // =========================================================== S

        private static void TestHitReportTrustBoundary()
        {
            _now = 33000.0;
            PMProjectileHistory h = HistoryNear(55u, 1u, _now, true);
            PMProjectileCoordinator c = new PMProjectileCoordinator(EP, h, new AcceptAllFilter(), null);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);
            PMProjectileKey key = PredKey(OWNER, 40u);
            c.RequestSpawn(MakeRequest(OWNER, 40u, 70u, ownerPos, new PMVector3(0f, 0f, 1f)),
                OWNER, AUTH_NET_ID, SOURCE, ownerPos, Spec(60000, false), null, Now());

            // 跨 owner / owner=0：不接受「自称的 owner」。
            CheckEq(c.ReportHits(NearBatch(key, 55u, 1u), 999u, At(1.0)).Result,
                PMProjectileHitReportResult.UntrustedOwner, "S: 跨 owner 上报被拒");
            CheckEq(c.ReportHits(NearBatch(key, 55u, 1u), 0u, At(1.0)).Result,
                PMProjectileHitReportResult.UntrustedOwner, "S: owner=0 上报被拒");
            CheckEq(c.PendingHits.GroupCount, 0, "S: 被拒的上报没有进入 C 队列");
            CheckEq(c.PendingVerifies.Count, 0, "S: 被拒的上报没有进入 B 队列");
            CheckEq(c.VerifyUsedCount(key), 0, "S: 被拒的上报不消耗 Verify 配额");

            // 未登记 key 不能自称 ServerDirect（否则能「占位」未来的权威弹、等它出现后回放伪造命中）。
            CheckEq(c.ReportHits(MakeBatch(AuthKey(OWNER, 77u), ownerPos, new PMVector3(0f, 1f, 0.4f), 0, 55u, 1u),
                OWNER, At(1.0)).Result, PMProjectileHitReportResult.Invalid,
                "S: 未登记 ServerDirect key 的上报被拒");
            CheckEq(c.PendingVerifies.Count, 0, "S: 该伪造没有进 B 队列");

            // 未登记 ClientPredicted（合法上行）仍走 B 队列。
            CheckEq(Report(c, NearBatch(PredKey(OWNER, 41u), 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.StashedForSpawn, "S: 未登记 ClientPredicted 仍进 B 队列");
            CheckEq(c.PendingVerifies.Count, 1, "S: B 队列 1 条");

            // 已登记的权威弹：校验/结算流程不变。
            c.ServerDirectSpawn(MakeAuthorityState(OWNER, 78u, 0u, ownerPos, new PMVector3(0f, 0f, 1f)),
                AUTH_NET_ID, Spec(60000, false), null, 0, At(1.0));
            CheckEq(Report(c, NearBatch(AuthKey(OWNER, 78u), 55u, 1u), At(1.0)).Result,
                PMProjectileHitReportResult.Settled, "S: 已登记权威弹仍可校验结算");

            // B 队列洪泛有界（单 key 5 / 总 128）：跨 owner 的灌入被完全挡住。
            int foreignAttempts = 0;
            for (uint i = 0u; i < 200u; i++)
            {
                PMProjectileHitReportOutcome r = c.ReportHits(NearBatch(PredKey(999u, 100u + i), 55u, 1u), OWNER, At(1.0));
                if (r.Result != PMProjectileHitReportResult.UntrustedOwner) { foreignAttempts++; }
            }

            CheckEq(foreignAttempts, 0, "S: 200 次跨 owner 洪泛全部被拒");
            Check(c.PendingVerifies.Count <= PMProjectileLimits.MaxPendingVerifies, "S: B 队列仍是有界的");
        }

        // =========================================================== 新增断言工具

        private static bool ThrowsInvalidOp(Action body)
        {
            try
            {
                body();
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            return false;
        }

        // =========================================================== 断言工具

        private static void Section(string name, Action body)
        {
            int beforePass = _passed;
            int beforeFail = _failures.Count;
            Console.WriteLine("-- " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add(name + " 抛出异常: " + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine("   通过 " + (_passed - beforePass) + " / 失败 " + (_failures.Count - beforeFail));
        }

        // =========================================================== T

        /// <summary>
        /// 【R5-B2c 返工】权威注册后的首次有界追赶（`CatchUpRegisteredMotion`）：
        ///
        ///   · 未登记 key / 尚未升级的预留身份**不得**被推成直线运动（NotRegistered / NotAuthority）；
        ///   · 预算 = `predictionMs / PredictionCatchUpDivisor` + **挂起时长**，两项均由整合器自记账；
        ///   · 恶意巨大的挂起时长被钳到 `MaxCatchUpBudgetMs` 并计数；
        ///   · 重复调用不重新开始追赶（AlreadyStarted），且不会静默变成别的语义。
        /// </summary>
        private static void TestCatchUpRegisteredMotion()
        {
            _now = 40000.0;
            PMProjectileHistory history = HistoryWithTarget(66u, 1u, _now, true);
            PMProjectileCoordinator coord = new PMProjectileCoordinator(EP, history, new AcceptAllFilter(), null);
            PMVector3 ownerPos = new PMVector3(0f, 1f, 0f);
            PMProjectileSpec spec = Spec(60000, false);

            // 1) 未登记 key：不得偷偷推直线（那是「接口失败回退」）。
            PMProjectileKey ghost = PredKey(OWNER, 700u);
            CheckEq(coord.CatchUpRegisteredMotion(ghost, _now).Result, PMProjectileAdvanceResult.NotRegistered,
                "T1: 未登记 key 的首次追赶 = NotRegistered（不伪造运动）");

            // 2) 预留（尚未升级）身份：NotAuthority（注册先于追赶）。
            PMProjectileSpawnRequest req = MakeRequest(OWNER, 12u, 42u, ownerPos, new PMVector3(0f, 0f, 1f));
            req.PredictionMs = 100;
            double registeredWall = _now;
            PMProjectileAdmissionOutcome adm = coord.RequestSpawn(req, OWNER, AUTH_NET_ID, SOURCE, ownerPos,
                spec, null, registeredWall);
            CheckEq(adm.Result, PMProjectileAdmissionResult.Admitted, "T2: 预留受理");
            Check(adm.SpawnPending, "T2: Pending 预留（尚未升级）");

            PMProjectileKey key = PredKey(OWNER, 12u);
            CheckEq(coord.CatchUpRegisteredMotion(key, registeredWall).Result, PMProjectileAdvanceResult.NotAuthority,
                "T2: 预留身份不得追赶（NotAuthority）");

            // 3) 挂起 300ms 后确认 ⇒ 预算 = 100/2 + 300 = 350ms。
            double resolveWall = registeredWall + 300.0;
            PMProjectileActivationApplyOutcome act =
                coord.ResolveActivation(OWNER, 42u, PMActivationResult.Confirmed, resolveWall);
            CheckEq(act.Result, PMProjectileActivationApplyResult.Applied, "T3: 确认生效");
            Check(act.SpawnedKeys == 1, "T3: 生成 1 颗");
            Check(coord.PromotedProjectileCount == 1, "T3: 恰好一次原地升级（追赶前提）");

            PMProjectileAdvanceOutcome cu = coord.CatchUpRegisteredMotion(key, resolveWall);
            CheckEq(cu.Result, PMProjectileAdvanceResult.Advanced, "T3: 升级后可以追赶并真实推进");
            CheckEq(cu.CatchUp, PMProjectileCatchUpResult.Started, "T3: 追赶开始");
            CheckEq(cu.AppliedMs, 350, "T3: 追赶量 = prediction/2(50) + 挂起(300)");
            Check(coord.ClampedCatchUpCount == 0, "T3: 预算未触顶（不虚报钳制）");

            PMProjectileSpec frozenSpec;
            PMProjectileState frozenState;
            Check(coord.TryObserveFrozen(key, out frozenSpec, out frozenState), "T3: 冻结状态可观察");
            CheckEq(frozenState.MoveTimeMs, 350.0, "T3: 冻结运动时基 = 350ms");
            Check(Math.Abs(frozenState.Position.Z - 3.5f) <= 0.001f,
                "T3: 位置 = speed 10m/s × 0.35s = 3.5m（实测 " + frozenState.Position.Z + "）");
            CheckEq(frozenState.AuthorityNetId, AUTH_NET_ID, "T3: 冻结状态的权威 ID 是真实值");

            // 4) 跑完预算后再调一次：不重新开始追赶（AlreadyStarted），且不会变成别的语义。
            PMProjectileAdvanceOutcome again = coord.CatchUpRegisteredMotion(key, resolveWall);
            CheckEq(again.CatchUp, PMProjectileCatchUpResult.AlreadyStarted, "T4: 重复调用不重新开始追赶");
            Check(again.AppliedMs == 350, "T4: 重复调用仍按给定预算推进（调用方给的 totalMs 决定）");

            // 5) 恶意巨大挂起时长：预算必须被钳到 MaxCatchUpBudgetMs 并计数。
            _now = 60000.0;
            PMProjectileSpawnRequest req2 = MakeRequest(OWNER, 13u, 43u, ownerPos, new PMVector3(0f, 0f, 1f));
            req2.PredictionMs = 100;
            double registeredWall2 = _now;
            PMProjectileAdmissionOutcome adm2 = coord.RequestSpawn(req2, OWNER, AUTH_NET_ID, SOURCE, ownerPos,
                spec, null, registeredWall2);
            CheckEq(adm2.Result, PMProjectileAdmissionResult.Admitted, "T5: 第二次预留受理");

            double resolveWall2 = registeredWall2 + 1500.0;
            PMProjectileActivationApplyOutcome act2 =
                coord.ResolveActivation(OWNER, 43u, PMActivationResult.Confirmed, resolveWall2);
            CheckEq(act2.Result, PMProjectileActivationApplyResult.Applied, "T5: 第二次确认生效");

            PMProjectileKey key2 = PredKey(OWNER, 13u);
            int clampedBefore = coord.ClampedCatchUpCount;
            PMProjectileAdvanceOutcome cu2 = coord.CatchUpRegisteredMotion(key2, resolveWall2);
            Check(cu2.AppliedMs <= PMProjectileCoordinatorLimits.MaxCatchUpBudgetMs,
                "T5: 巨量挂起被钳到总预算（实际 " + cu2.AppliedMs + "）");
            Check(coord.ClampedCatchUpCount > clampedBefore, "T5: 钳制被计数（可观测，不静默）");

            PMProjectileState frozen2;
            PMProjectileSpec spec2;
            Check(coord.TryObserveFrozen(key2, out spec2, out frozen2), "T5: 第二次冻结状态可观察");
            Check(Math.Abs(frozen2.Position.Z - 10f) <= 0.001f,
                "T5: 位置 = speed 10 × 1.0s = 10m（钳到 1000ms，实测 " + frozen2.Position.Z + "）");

            // 6) predictionMs = 0 时不得凭空多推（预算里只有挂起项）。
            _now = 70000.0;
            PMProjectileSpawnRequest req3 = MakeRequest(OWNER, 14u, 44u, ownerPos, new PMVector3(0f, 0f, 1f));
            double registeredWall3 = _now;
            PMProjectileAdmissionOutcome adm3 = coord.RequestSpawn(req3, OWNER, AUTH_NET_ID, SOURCE, ownerPos,
                spec, null, registeredWall3);
            CheckEq(adm3.Result, PMProjectileAdmissionResult.Admitted, "T6: 第三次预留受理");
            double resolveWall3 = registeredWall3 + 200.0;
            coord.ResolveActivation(OWNER, 44u, PMActivationResult.Confirmed, resolveWall3);

            PMProjectileAdvanceOutcome cu3 = coord.CatchUpRegisteredMotion(PredKey(OWNER, 14u), resolveWall3);
            CheckEq(cu3.AppliedMs, 200, "T6: prediction=0 时追赶量 = 挂起时长（200ms）");
        }

        private static void Check(bool condition, string what)
        {
            if (condition) { _passed++; }
            else { _failures.Add(what); }
        }

        private static void CheckEq(int actual, int expected, string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(uint actual, uint expected, string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(double actual, double expected, string what)
        {
            Check(Math.Abs(actual - expected) <= 1e-6, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq<T>(T actual, T expected, string what) where T : struct
        {
            Check(actual.Equals(expected), what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileAdmissionResult actual, PMProjectileAdmissionResult expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileHitReportResult actual, PMProjectileHitReportResult expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileAdvanceResult actual, PMProjectileAdvanceResult expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileActivationApplyResult actual,
            PMProjectileActivationApplyResult expected, string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileValidateStatus actual, PMProjectileValidateStatus expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileRejectReason actual, PMProjectileRejectReason expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(PMProjectileCatchUpResult actual, PMProjectileCatchUpResult expected,
            string what)
        {
            Check(actual == expected, what + "（期望 " + expected + "，实际 " + actual + "）");
        }
    }
}
