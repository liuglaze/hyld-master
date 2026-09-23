// ============================================================================
//  PMProjectileLifecycleTest —— R5-A1：生命周期 / 假弹镜像接管 / 三类 Pending
// ============================================================================
//  事实来源：
//    Docs/plans/net-r5-projectile-contract.md（冻结契约「语义与有限资源」「A验收」）
//    Docs/plans/_r5_semantics_survey.md §1.1–1.4（UE 侧语义）
//    Docs/plans/net-architecture-migration.md §3.9.4 + D-R5-01
//
//  做法：
//    · 直接编**真实源码**（PMProjectile 两份实现 + 冻结 contracts），不 mock 记账逻辑；
//    · 期望值全部**独立手算**（常量算术 + 显式推导），不拿实现自身当预期；
//    · 负向用例是真的边界值（容量 128/129、TTL 1999/2000、墓碑窗口内/外、时钟倒退），
//      不是「错误字符串包含」这类字符串门。
//
//  覆盖：
//    A. 身份与登记（T5A1）：双 origin / 跨 owner 同号合法 / 同 owner 拒重号 / 水位拒复用 /
//       跨 epoch / owner 与 registry 容量 / 深度 clone / 升 epoch 清理 / 断连清理
//    B. 接管与镜像（T5A2）：位置闸门 / 一次消费 / 重复接管 / 假弹早结束的迟到镜像不复活 /
//       拒绝即撤销 / 同一 activation 多弹同时解挂 / 终态不倒退 / 注册先于追赶
//    C. 停止与墓碑（T5A2）：停止保留记录 / 重复停止不二次事件 / 墓碑窗口内外的 Verify 准入 /
//       墓碑公式 / 白名单与命中集合的清理时机（停止不清、回收才清）
//    D. 三类 Pending（T5A3）：A 128/owner + TTL + 解挂；B 单 key 5 / 总 128 / FIFO /
//       先清标记再回放 / 注册先于回放；C 128 组 / 合并不刷新时刻 / 满丢最旧 / Confirm 仅一次 /
//       Reject 与 TTL 无结算 / Pending 零结算
//    G. 契约偏离修正：散弹键按完整 ProjectileKey（同源多弹必须通过）/ 挂起 Spawn 总预算 = 契约 1024 /
//       终态标记环淘汰后不得复活（高水位拦截）/ 每 (owner,origin) 高水位自身有界
//    H. 释放队列与撤销边界：out releases 与待取队列不可双消费 / 撤销只针对真实存在的本地假弹 /
//       已接管的弹不得被当成假弹删 / 权威实例不产生撤销项 / 登记被拒也不产生撤销项
//    I. 镜像先到后到 / 停止态保护 / finite / 身份与域：追赶前镜像 Applied 且不消耗追赶资格 /
//       停止后镜像 StoppedNotMoved 不得写位置 / 镜像停止建立墓碑窗口 /
//       登记与镜像的 finite + key 身份一致性 / 预测域 AuthorityNetId 归 0 且入参终态被归一
//    J. 原始上报 vs 可信结论：B 存未消毒原始上行（交给 L0-L4），C 只收已消毒
//       PMProjectileValidatedHitSet（非 finite / TargetNetId=0 / 身份不符一律拒）
//    K. 运动推进 API：TryAdvanceMotion 推进 Position/PreviousPosition/Velocity/Yaw/MoveTimeMs，
//       不覆盖 Key/ActivationId/Activation/AuthorityNetId/HitTargets/AllowedTargets/Stopped/
//       SpawnPosition/Spec/登记时刻/墓碑；停止或结束后 NotMovable
//    L. 【R5-A3 返工支撑】预留假弹原地升级为权威（TryPromoteReservedToAuthority：不重分配 ID / 不改 Origin /
//       权威 NetId 真实非 0 / 只有 Confirmed 可升级 / 幂等 / 升级后才能 TryBeginCatchUp）；
//       终态激活判决被容量淘汰后，迟到/重放的 Confirmed/Rejected（含同向重复、降回 Pending）
//       与同 activation 的新登记一律不得复活
//
//  不覆盖（诚实口径）：
//    · PMProjectileValidator（A2 组）的几何/历史/L0–L4 —— 本组不依赖、不冒充；
//    · 真网络/Unity 宿主（B/C 阶段，T45 PENDING_USER）。
//
//  运行：dotnet Tools/PMProjectileLifecycleTest/bin/Release/net8.0/PMProjectileLifecycleTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

namespace PMProjectileLifecycleTest
{
    internal static class Program
    {
        private const uint EP = 7u;
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static double _now = 1000.0;

        private static double Now() { return _now; }

        private static double At(double ms)
        {
            _now += ms;
            return _now;
        }

        private static int Main()
        {
            Console.WriteLine("=== R5-A1：PMProjectile 生命周期 / 接管 / 三类 Pending（手算 oracle）===");
            Console.WriteLine();

            Section("A. 身份与登记（双 origin / 拒重号 / 水位 / epoch / 容量 / 深 clone / 清理）", TestIdentity);
            Section("B. 假弹镜像接管（位置闸门 / 一次消费 / 迟到镜像 / 拒绝撤销 / 多弹解挂）", TestTakeover);
            Section("C. 停止与墓碑（重复停止 / 窗口内外 Verify / 墓碑公式 / 清理时机）", TestStopTombstone);
            Section("D. Pending A：挂起 Spawn（128/owner、TTL、解挂、深 clone）", TestPendingSpawns);
            Section("E. Pending B：Verify 先于 Spawn（单 key 5 / 总 128 / FIFO / 先清标记）", TestPendingVerifies);
            Section("F. Pending C：命中结论（合并 / 满丢最旧 / Confirm 仅一次 / 无结算）", TestPendingHits);
            Section("G. 契约偏离修正（散弹键 / 总预算 1024 / 终态记忆与高水位）", TestContractDeviations);
            Section("H. 释放队列与撤销边界（不双消费 / 只撤真实假弹 / 登记被拒不撤）", TestReleaseAndRevoke);
            Section("I. 镜像先到后到 / 停止态保护 / finite / 身份与域", TestMirrorStopFiniteDomain);
            Section("J. 原始上报 vs 可信结论（两侧口径刻意不同）", TestRawVersusValidated);
            Section("K. 运动推进 API（A3 宿主推进已登记弹；受保护字段不被覆盖）", TestMotionAdvance);
            Section("L. 预留假弹原地升级为权威 + 终态判决淘汰后的复活防护", TestPromotionAndTerminalMemory);

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

        // =============================================================== A

        private static void TestIdentity()
        {
            // --- 共享契约的墓碑公式（与实现无关的手算预期）---
            CheckEq(PMProjectileTombstone.ComputeTombstoneMs(0, 0), 150, "墓碑: max(150,0,clamp(100))=150");
            CheckEq(PMProjectileTombstone.ComputeTombstoneMs(0, 250), 600, "墓碑: 2*250+100=600");
            CheckEq(PMProjectileTombstone.ComputeTombstoneMs(0, 1000), 1000, "墓碑: 2*1000+100=2100 -> clamp 1000");
            CheckEq(PMProjectileTombstone.ComputeTombstoneMs(800, 0), 800, "墓碑: delayDestroy=800 胜出");
            CheckEq(PMProjectileTombstone.ComputeTombstoneMs(0, -50), 150, "墓碑: 负 prediction 钳到 0 -> max(150,0,100)=150");

            PMProjectileLifecycle life = new PMProjectileLifecycle(EP);

            // --- 双 origin：同一数字在 ServerDirect 与 ClientPredicted 上是两个身份 ---
            PMProjectileKey auth1 = new PMProjectileKey(EP, 100u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileKey pred1 = new PMProjectileKey(EP, 100u, 1u, PMProjectileOrigin.ClientPredicted);
            Check(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 100u, 1u, 900u, 0u,
                null, null, Now()).Result == PMProjectileRegisterResult.Registered, "A: 权威登记");
            Check(life.TryRegisterPredicted(100u, 1u, 55u, null, null, Now()).Result == PMProjectileRegisterResult.Registered,
                "A: 预测登记（同 owner 同号但不同 origin 合法）");
            Check(life.IsRegistered(auth1) && life.IsRegistered(pred1), "A: 双 origin 共存");
            CheckEq(life.AuthorityCount, 1, "A: 权威计数 1");
            CheckEq(life.PredictedCount, 1, "A: 预测计数 1");

            // --- 可信入口：客户端路径不得产生权威弹 ---
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.ClientRequest, 100u, 2u, 901u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.UntrustedAuthority,
                "A: 非可信入口登记权威弹被拒");
            Check(!life.IsRegistered(new PMProjectileKey(EP, 100u, 2u, PMProjectileOrigin.ServerDirect)),
                "A: 被拒的权威弹不存在");

            // --- 同 owner 拒重号 ---
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 100u, 1u, 900u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.DuplicateId, "A: 同 key 重复登记 -> DuplicateId");

            // --- 跨 owner 同 ID 合法（身份含 owner）---
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 1u, 902u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered, "A: 跨 owner 同 ID 合法");

            // --- 水位：同 owner/origin 的 ID 必须单调递增 ---
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 1u, 903u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.DuplicateId, "A: owner200 id1 重复");
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 5u, 904u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered, "A: owner200 id5 登记");
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 3u, 905u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.NonMonotonicId, "A: id3 < 水位5 -> 拒");
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 6u, 906u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered, "A: id6 > 水位5 -> 收");

            // --- 激活 ID 契约：客户端预测的 activationId 必须非 0 ---
            CheckEq(life.TryRegisterPredicted(300u, 1u, 0u, null, null, Now()).Result,
                PMProjectileRegisterResult.InvalidActivationId, "A: 预测 activationId=0 -> 拒（仅 ServerDirect 可用 0）");
            CheckEq(life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 300u, 1u, 907u, 0u,
                null, null, Now()).Activation, PMActivationResult.Confirmed,
                "A: ServerDirect activationId=0 -> 隐式 Confirmed");

            // --- 深度 clone：入参状态/规格被冻结，外部改写不影响登记项 ---
            PMProjectileLifecycle cloneLife = new PMProjectileLifecycle(EP);
            PMProjectileState inState = new PMProjectileState();
            inState.Position = new PMVector3(1f, 2f, 3f);
            inState.HitTargets = new uint[] { 7u, 8u };
            PMProjectileSpec inSpec = new PMProjectileSpec();
            inSpec.SpeedMps = 12f;
            inSpec.RadiusM = 0.25f;
            PMProjectileKey ck = new PMProjectileKey(EP, 400u, 1u, PMProjectileOrigin.ServerDirect);
            Check(cloneLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 400u, 1u, 910u, 0u,
                inState, inSpec, Now()).IsRegistered, "A: clone 用例登记");
            inState.Position = new PMVector3(9f, 9f, 9f);
            inState.HitTargets[0] = 99u;
            inSpec.SpeedMps = 99f;
            inSpec.RadiusM = 9f;

            PMProjectileRegistration snap;
            Check(cloneLife.TryGetRegistration(ck, out snap), "A: 读取冻结副本");
            CheckEq(snap.State.Position.X, 1f, "A: 深 clone - 入参 Position 改写不影响冻结值");
            CheckEq(snap.State.HitTargets[0], 7u, "A: 深 clone - 入参数组改写不影响冻结值");
            CheckEq(snap.Spec.SpeedMps, 12f, "A: 深 clone - 入参 Spec 改写不影响冻结值");
            CheckEq(snap.Spec.RadiusM, 0.25f, "A: 深 clone - 入参 Radius 改写不影响冻结值");

            snap.Spec.SpeedMps = 5f;
            snap.State.Position = new PMVector3(4f, 4f, 4f);
            snap.State.HitTargets[0] = 555u;
            PMProjectileRegistration snap2;
            cloneLife.TryGetRegistration(ck, out snap2);
            CheckEq(snap2.Spec.SpeedMps, 12f, "A: 深 clone - 返回副本改写不影响内部");
            CheckEq(snap2.State.Position.X, 1f, "A: 深 clone - 返回副本 Position 改写不影响内部");
            CheckEq(snap2.State.HitTargets[0], 7u, "A: 深 clone - 返回副本数组改写不影响内部");
            PMProjectileSpec frozenSpec;
            PMProjectileState frozenState;
            cloneLife.TryGetFrozen(ck, out frozenSpec, out frozenState);
            frozenSpec.SpeedMps = 1f;
            PMProjectileSpec frozenSpec2;
            PMProjectileState frozenState2;
            cloneLife.TryGetFrozen(ck, out frozenSpec2, out frozenState2);
            CheckEq(frozenSpec2.SpeedMps, 12f, "A: 深 clone - TryGetFrozen 副本改写不影响内部");

            // --- owner 容量：MaxOwners=64 ---
            PMProjectileLifecycle ownerLife = new PMProjectileLifecycle(EP);
            for (uint i = 1u; i <= PMProjectileLimits.MaxOwners; i++)
            {
                PMProjectileRegisterOutcome ro = ownerLife.TryRegisterAuthority(
                    PMProjectileTrust.AuthorityServerDirect, 1000u + i, 1u, 5000u + i, 0u, null, null, Now());
                if (ro.Result != PMProjectileRegisterResult.Registered)
                {
                    Check(false, "A: owner 容量 - 第 " + i + " 个 owner 应登记成功，实际 " + ro.Result);
                    return;
                }
            }

            CheckEq(ownerLife.OwnerCount, PMProjectileLimits.MaxOwners, "A: owner 数 = 64");
            CheckEq(ownerLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 9999u, 1u, 9000u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.OwnerCapacity, "A: 第 65 个 owner -> OwnerCapacity");

            // --- registry 容量：单类别 MaxProjectiles=1024 ---
            PMProjectileLifecycle capLife = new PMProjectileLifecycle(EP);
            bool allOk = true;
            for (uint i = 1u; i <= PMProjectileLimits.MaxProjectiles; i++)
            {
                if (capLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 42u, i, 7000u, 0u,
                    null, null, Now()).Result != PMProjectileRegisterResult.Registered)
                {
                    allOk = false;
                    break;
                }
            }

            Check(allOk, "A: 权威类别 1024 颗全部登记成功");
            CheckEq(capLife.AuthorityCount, PMProjectileLimits.MaxProjectiles, "A: 权威计数 1024");
            CheckEq(capLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 42u,
                (uint)(PMProjectileLimits.MaxProjectiles + 1), 7001u, 0u, null, null, Now()).Result,
                PMProjectileRegisterResult.RegistryCapacity, "A: 第 1025 颗 -> RegistryCapacity");

            // --- 断连清理：水位保留 ⇒ 同 owner 同 ID 不会复活 ---
            PMProjectileLifecycle clearLife = new PMProjectileLifecycle(EP);
            clearLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 500u, 3u, 800u, 0u, null, null, Now());
            clearLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 501u, 3u, 801u, 0u, null, null, Now());
            CheckEq(clearLife.ClearOwner(500u, At(1.0)), 1, "A: 断连清理只移除该 owner");
            CheckEq(clearLife.RegistrationCount, 1, "A: 另一个 owner 的记录保留");
            CheckEq(clearLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 500u, 3u, 802u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.NonMonotonicId,
                "A: 断连后同 owner 复用旧 ID -> 拒（水位不复位）");
            CheckEq(clearLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 500u, 4u, 803u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered, "A: 断连后更大 ID 可登记");

            // --- 升 epoch：整体清理 + 旧 epoch 不复活 ---
            PMProjectileLifecycle epochLife = new PMProjectileLifecycle(EP);
            PMProjectileKey oldKey = new PMProjectileKey(EP, 600u, 1u, PMProjectileOrigin.ServerDirect);
            epochLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 600u, 1u, 810u, 0u, null, null, Now());
            PMProjectileActivationRelease[] rels;
            CheckEq(epochLife.TrySetActivationResult(600u, 88u, PMActivationResult.Confirmed, Now(), out rels),
                PMActivationLedgerResult.Applied, "A: epoch 前写账本");
            CheckEq(epochLife.ActivationLedgerCount, 1, "A: 账本计数 1");
            Check(epochLife.AdvanceEpoch(EP + 1u), "A: 升 epoch 成功");
            CheckEq(epochLife.RegistrationCount, 0, "A: 升 epoch 清空登记");
            CheckEq(epochLife.OwnerCount, 0, "A: 升 epoch 清空 owner");
            CheckEq(epochLife.ActivationLedgerCount, 0, "A: 升 epoch 清空账本");
            Check(!epochLife.IsRegistered(oldKey), "A: 旧 epoch key 不再登记");
            CheckEq(epochLife.AdmitVerify(oldKey, Now()).Result, PMProjectileVerifyAdmission.StaleEpoch,
                "A: 旧 epoch 的 Verify -> StaleEpoch（不复活）");
            CheckEq(epochLife.TryTakeoverPredicted(oldKey).Result, PMProjectileTakeoverResult.StaleEpoch,
                "A: 旧 epoch 的接管 -> StaleEpoch");
            CheckEq(epochLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 600u, 1u, 810u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered,
                "A: 新 epoch 下同 owner 同 ID 可重新开始（水位随 epoch 清理）");
            Check(!epochLife.AdvanceEpoch(EP), "A: epoch 不递增 -> 拒绝");
            Check(!epochLife.AdvanceEpoch(EP + 1u), "A: epoch 相等 -> 拒绝");
            CheckEq(epochLife.Epoch, EP + 1u, "A: epoch 保持 " + (EP + 1u));

            // --- 墙钟：非 finite / 倒退显式拒绝 ---
            PMProjectileLifecycle clockLife = new PMProjectileLifecycle(EP);
            CheckEq(clockLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 1u, 820u, 0u,
                null, null, double.NaN).Result, PMProjectileRegisterResult.InvalidClock, "A: NaN 墙钟 -> InvalidClock");
            CheckEq(clockLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 1u, 820u, 0u,
                null, null, double.PositiveInfinity).Result, PMProjectileRegisterResult.InvalidClock,
                "A: +Inf 墙钟 -> InvalidClock");
            Check(clockLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 1u, 820u, 0u,
                null, null, 10.0).IsRegistered, "A: 首次合法墙钟");
            CheckEq(clockLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 2u, 821u, 0u,
                null, null, 9.0).Result, PMProjectileRegisterResult.InvalidClock, "A: 墙钟倒退 -> InvalidClock");
            CheckEq(clockLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 2u, 821u, 0u,
                null, null, 10.0).Result, PMProjectileRegisterResult.Registered, "A: 墙钟相等（同毫秒）合法");
        }

        // =============================================================== B

        private static void TestTakeover()
        {
            PMProjectileLifecycle life = new PMProjectileLifecycle(EP);
            PMProjectileKey key = new PMProjectileKey(EP, 100u, 1u, PMProjectileOrigin.ClientPredicted);

            // --- 预测登记 -> 挂起激活（不生成幽灵弹：登记表里只有账本项）---
            PMProjectileRegisterOutcome reg = life.TryRegisterPredicted(100u, 1u, 77u, null, null, Now());
            CheckEq(reg.Result, PMProjectileRegisterResult.Registered, "B: 预测登记");
            CheckEq(reg.Activation, PMActivationResult.Pending, "B: 无账本裁决 -> Pending");
            Check(life.IsPredictedAlive(key), "B: 假弹存活（位置闸门开）");

            // --- 镜像后到（假弹仍活）→ 位置闸门拒绝写位置 ---
            PMProjectileState mirror = new PMProjectileState();
            mirror.Position = new PMVector3(50f, 1f, 0f);
            mirror.Velocity = new PMVector3(3f, 0f, 0f);
            PMProjectileMirrorOutcome mo = life.TryApplyMirror(key, mirror, Now());
            CheckEq(mo.Result, PMProjectileMirrorResult.GateFakeAlive, "B: 假弹存活时镜像 -> GateFakeAlive");
            Check(!mo.PositionApplied, "B: 位置闸门 - 镜像位置未写入");
            PMProjectileRegistration s1;
            life.TryGetRegistration(key, out s1);
            CheckEq(s1.State.Position.X, 0f, "B: 假弹位置未被镜像覆盖");

            // --- 一次消费接管 ---
            PMProjectileTakeoverOutcome to = life.TryTakeoverPredicted(key);
            CheckEq(to.Result, PMProjectileTakeoverResult.TakenOver, "B: 首次接管成功");
            CheckEq(life.TryTakeoverPredicted(key).Result, PMProjectileTakeoverResult.AlreadyTakenOver,
                "B: 二次接管 -> AlreadyTakenOver（不可重复消费）");
            Check(!life.IsPredictedAlive(key), "B: 接管后位置闸门关闭");

            // --- 接管后镜像生效 ---
            PMProjectileMirrorOutcome mo2 = life.TryApplyMirror(key, mirror, Now());
            CheckEq(mo2.Result, PMProjectileMirrorResult.Applied, "B: 接管后镜像 -> Applied");
            Check(mo2.PositionApplied, "B: 接管后镜像位置写入");
            PMProjectileRegistration s2;
            life.TryGetRegistration(key, out s2);
            CheckEq(s2.State.Position.X, 50f, "B: 镜像位置生效");
            Check(s2.TakenOver, "B: 接管标志记录");

            // --- 未知 key / 已回收 key ---
            CheckEq(life.TryApplyMirror(new PMProjectileKey(EP, 999u, 9u, PMProjectileOrigin.ServerDirect), mirror, Now()).Result,
                PMProjectileMirrorResult.UnknownKey, "B: 未知 key 镜像 -> UnknownKey");
            CheckEq(life.TryTakeoverPredicted(new PMProjectileKey(EP, 999u, 9u, PMProjectileOrigin.ClientPredicted)).Result,
                PMProjectileTakeoverResult.UnknownKey, "B: 未知 key 接管 -> UnknownKey");
            key = new PMProjectileKey(EP, 100u, 1u, PMProjectileOrigin.ClientPredicted);

            // --- 假弹早结束：墓碑建立、幂等、迟到镜像只隐藏不复活 ---
            PMProjectileLifecycle lateLife = new PMProjectileLifecycle(EP);
            PMProjectileKey lateKey = new PMProjectileKey(EP, 300u, 1u, PMProjectileOrigin.ClientPredicted);
            lateLife.TryRegisterPredicted(300u, 1u, 11u, null, null, Now());
            CheckEq(lateLife.AdmitVerify(lateKey, Now()).Result, PMProjectileVerifyAdmission.Allowed,
                "B: 假弹未结束时 Verify 正常放行");
            PMProjectilePredictedEndOutcome end = lateLife.NotifyPredictedEnded(lateKey, Now(), 0, 0);
            CheckEq(end.Result, PMProjectilePredictedEndResult.Recorded, "B: 假弹早结束 -> Recorded");
            CheckEq(end.TombstoneMs, 150, "B: 假弹早结束墓碑 = max(150,0,100) = 150");
            CheckEq(lateLife.NotifyPredictedEnded(lateKey, Now(), 0, 0).Result,
                PMProjectilePredictedEndResult.AlreadyEnded, "B: 重复结束 -> AlreadyEnded（不续期）");
            CheckEq(lateLife.TryTakeoverPredicted(lateKey).Result, PMProjectileTakeoverResult.PredictedNotAlive,
                "B: 假弹已结束 -> 接管不成立");
            PMProjectileMirrorOutcome lateMirror = lateLife.TryApplyMirror(lateKey, mirror, Now());
            CheckEq(lateMirror.Result, PMProjectileMirrorResult.HiddenNoRevive, "B: 迟到镜像 -> HiddenNoRevive");
            Check(lateMirror.HideWithoutStopEvent, "B: 迟到镜像必须隐藏且不二次停止通知");
            CheckEq(lateLife.TryApplyMirror(lateKey, mirror, Now()).Result, PMProjectileMirrorResult.HiddenNoRevive,
                "B: 重复迟到镜像仍不复活");
            PMProjectileRegistration lateSnap;
            lateLife.TryGetRegistration(lateKey, out lateSnap);
            Check(lateSnap.Hidden && lateSnap.Stopped, "B: 迟到镜像 -> 已隐藏且已停止（不复活）");

            // --- 拒绝即撤销 + 终态不倒退 ---
            PMProjectileLifecycle rejLife = new PMProjectileLifecycle(EP);
            PMProjectileKey rk1 = new PMProjectileKey(EP, 400u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileKey rk2 = new PMProjectileKey(EP, 400u, 2u, PMProjectileOrigin.ClientPredicted);
            rejLife.TryRegisterPredicted(400u, 1u, 123u, null, null, Now());
            rejLife.TryRegisterPredicted(400u, 2u, 123u, null, null, Now());
            CheckEq(rejLife.PredictedCount, 2, "B: 同一 activation 两颗挂起弹");
            PMProjectileActivationRelease[] releases;
            CheckEq(rejLife.TrySetActivationResult(400u, 123u, PMActivationResult.Rejected, Now(), out releases),
                PMActivationLedgerResult.Applied, "B: 写入 Rejected 裁决");
            CheckEq(releases.Length, 2, "B: Rejected 一次解挂两颗（同一 activation）");
            Check(releases[0].RemovedInstance && releases[1].RemovedInstance, "B: Rejected 释放项要求实际撤销实例");
            CheckEq(releases[0].Outcome, PMActivationResult.Rejected, "B: 释放项 outcome=Rejected");
            CheckEq(releases[0].Reason, PMProjectileReleaseReason.ActivationRejected, "B: 释放原因=ActivationRejected");
            Check(!rejLife.IsRegistered(rk1) && !rejLife.IsRegistered(rk2), "B: Rejected 后预测实例已从登记表移除");
            Check(rejLife.IsRetired(rk1) && rejLife.IsRetired(rk2), "B: Rejected 后进入终态标记环");
            CheckEq(rejLife.PredictedCount, 0, "B: 预测计数归零");
            PMProjectileActivationRelease[] rels2;
            CheckEq(rejLife.TrySetActivationResult(400u, 123u, PMActivationResult.Confirmed, Now(), out rels2),
                PMActivationLedgerResult.AlreadyTerminal, "B: 终态不倒退 - Rejected 后写 Confirmed 被拒");
            CheckEq(rels2.Length, 0, "B: 终态重复写入不产生重复释放");
            CheckEq(rejLife.TryRegisterPredicted(400u, 3u, 123u, null, null, Now()).Result,
                PMProjectileRegisterResult.RevokedRejected, "B: 已 Rejected 的 activation 上再登记 -> RevokedRejected");
            CheckEq(rejLife.PredictedCount, 0, "B: 被拒登记不产生实例");

            // --- 确认：同一 activation 多弹同时解挂 + 之后的同 activation 立即 Confirmed ---
            PMProjectileLifecycle conLife = new PMProjectileLifecycle(EP);
            conLife.TryRegisterPredicted(500u, 1u, 9u, null, null, Now());
            At(100.0);
            conLife.TryRegisterPredicted(500u, 2u, 9u, null, null, Now());
            At(50.0);
            conLife.TryRegisterPredicted(500u, 3u, 9u, null, null, Now());
            PMProjectileActivationRelease[] cr;
            CheckEq(conLife.TrySetActivationResult(500u, 9u, PMActivationResult.Confirmed, Now(), out cr),
                PMActivationLedgerResult.Applied, "B: 写入 Confirmed");
            CheckEq(cr.Length, 3, "B: Confirmed 一次解挂三颗");
            CheckEq(cr[0].PendingElapsedMs, 150.0, "B: 挂起时长 = 解挂时刻 - 登记时刻（150ms）");
            CheckEq(cr[1].PendingElapsedMs, 50.0, "B: 第二条挂起时长 50ms（各自计算）");
            CheckEq(cr[2].PendingElapsedMs, 0.0, "B: 第三条在解挂同一毫秒登记，挂起时长 0ms");
            Check(!cr[0].RemovedInstance && !cr[1].RemovedInstance && !cr[2].RemovedInstance,
                "B: Confirmed 不撤销实例");
            PMActivationResult ledger;
            Check(conLife.TryGetActivationResult(500u, 9u, out ledger), "B: 账本可读");
            CheckEq(ledger, PMActivationResult.Confirmed, "B: 账本 = Confirmed");
            PMProjectileActivationRelease[] cr2;
            CheckEq(conLife.TrySetActivationResult(500u, 9u, PMActivationResult.Confirmed, Now(), out cr2),
                PMActivationLedgerResult.AlreadyTerminal, "B: 重复 Confirmed -> AlreadyTerminal");
            CheckEq(cr2.Length, 0, "B: 重复 Confirmed 不再释放");
            PMProjectileRegisterOutcome later = conLife.TryRegisterPredicted(500u, 4u, 9u, null, null, Now());
            CheckEq(later.Result, PMProjectileRegisterResult.Registered, "B: 已 Confirmed 的 activation 上可继续登记");
            CheckEq(later.Activation, PMActivationResult.Confirmed, "B: 新弹立即得到 Confirmed（不必等待）");
            PMProjectileActivationRelease[] cr3;
            CheckEq(conLife.TrySetActivationResult(500u, 9u, PMActivationResult.Rejected, Now(), out cr3),
                PMActivationLedgerResult.AlreadyTerminal, "B: Confirmed 后写 Rejected 被拒（终态不倒退）");

            // --- 账本非法参数 + 有界淘汰 ---
            PMProjectileLifecycle ledgerLife = new PMProjectileLifecycle(EP);
            PMProjectileActivationRelease[] dummy;
            CheckEq(ledgerLife.TrySetActivationResult(1u, 0u, PMActivationResult.Confirmed, Now(), out dummy),
                PMActivationLedgerResult.InvalidActivationId, "B: activationId=0 写账本 -> 拒");
            CheckEq(ledgerLife.TrySetActivationResult(0u, 5u, PMActivationResult.Confirmed, Now(), out dummy),
                PMActivationLedgerResult.InvalidActivationId, "B: owner=0 写账本 -> 拒");
            CheckEq(ledgerLife.TrySetActivationResult(1u, 5u, PMActivationResult.Pending, Now(), out dummy),
                PMActivationLedgerResult.RecordedPending, "B: 写 Pending");
            CheckEq(ledgerLife.TrySetActivationResult(1u, 5u, PMActivationResult.Rejected, Now(), out dummy),
                PMActivationLedgerResult.Applied, "B: Pending -> Rejected 允许");
            CheckEq(ledgerLife.ActivationLedgerCount, 1, "B: 账本计数 1（Pending 升级为终态不新增）");
            for (int i = 0; i < PMProjectileLifecycleLimits.MaxActivationLedger; i++)
            {
                ledgerLife.TrySetActivationResult(1u, (uint)(1000 + i), PMActivationResult.Confirmed, Now(), out dummy);
            }

            CheckEq(ledgerLife.ActivationLedgerCount, PMProjectileLifecycleLimits.MaxActivationLedger,
                "B: 账本上限 = 512");
            CheckEq(ledgerLife.TrySetActivationResult(1u, 999999u, PMActivationResult.Confirmed, Now(), out dummy),
                PMActivationLedgerResult.Applied, "B: 账本满时淘汰最旧终态后仍可写入");
            PMActivationResult evicted;
            Check(!ledgerLife.TryGetActivationResult(1u, 5u, out evicted), "B: 最旧终态条目已被淘汰");

            // --- 注册必须先于追赶 ---
            PMProjectileLifecycle capLife = new PMProjectileLifecycle(EP);
            PMProjectileKey authKey = new PMProjectileKey(EP, 600u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileKey predKey = new PMProjectileKey(EP, 601u, 1u, PMProjectileOrigin.ClientPredicted);
            CheckEq(capLife.TryBeginCatchUp(authKey, Now()).Result, PMProjectileCatchUpResult.NotRegistered,
                "B: 未注册就追赶 -> NotRegistered");
            capLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 600u, 1u, 950u, 0u, null, null, Now());
            PMProjectileCatchUpOutcome cu = capLife.TryBeginCatchUp(authKey, Now());
            CheckEq(cu.Result, PMProjectileCatchUpResult.Started, "B: 注册后追赶 -> Started");
            CheckEq(cu.AuthorityNetId, 950u, "B: 追赶拿到权威 NetId");
            CheckEq(capLife.TryBeginCatchUp(authKey, Now()).Result, PMProjectileCatchUpResult.AlreadyStarted,
                "B: 重复追赶 -> AlreadyStarted（外部不可重入）");
            capLife.TryRegisterPredicted(601u, 1u, 3u, null, null, Now());
            CheckEq(capLife.TryBeginCatchUp(predKey, Now()).Result, PMProjectileCatchUpResult.NotAuthority,
                "B: 预测弹不能开始权威追赶");
            CheckEq(capLife.TryBeginCatchUp(authKey, double.NaN).Result, PMProjectileCatchUpResult.InvalidClock,
                "B: 追赶墙钟非法 -> InvalidClock");
            PMProjectileLifecycle retiredLife = new PMProjectileLifecycle(EP);
            PMProjectileKey rr = new PMProjectileKey(EP, 700u, 1u, PMProjectileOrigin.ServerDirect);
            retiredLife.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 700u, 1u, 960u, 0u, null, null, Now());
            retiredLife.Retire(rr, Now());
            CheckEq(retiredLife.TryBeginCatchUp(rr, Now()).Result, PMProjectileCatchUpResult.Retired,
                "B: 已回收 key 追赶 -> Retired");
            Check(retiredLife.IsRetired(rr), "B: 已回收 key 有终态标记");
            CheckEq(retiredLife.TryApplyMirror(rr, mirror, Now()).Result, PMProjectileMirrorResult.Retired,
                "B: 已回收 key 的迟到镜像 -> Retired（不复活）");
            CheckEq(retiredLife.AdmitVerify(rr, Now()).Result, PMProjectileVerifyAdmission.Retired,
                "B: 已回收 key 的迟到 Verify -> Retired");
            Check(!retiredLife.TryRecordStop(rr, PMVector3.Zero, Now(), 0, 0).StopEventEmitted,
                "B: 已回收 key 不会再产生停止事件");
        }

        // =============================================================== C

        private static void TestStopTombstone()
        {
            // --- 停止保留权威记录；重复停止不产生二次事件、不延长墓碑 ---
            PMProjectileLifecycle life = new PMProjectileLifecycle(EP);
            PMProjectileKey key = new PMProjectileKey(EP, 100u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileState st = new PMProjectileState();
            st.Position = new PMVector3(5f, 1f, 5f);
            PMProjectileSpec spec = new PMProjectileSpec();
            spec.HideOnStop = false;
            life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 100u, 1u, 800u, 0u, st, spec, Now());
            life.TryAddHitTarget(key, 11u);
            life.TryAddHitTarget(key, 12u);
            life.TryAddHitTarget(key, 11u);
            CheckEq(life.HitTargetCount(key), 2, "C: 命中集合去重（11 重复只记一次）");
            uint[] allowed = new uint[] { 21u, 22u, 21u };
            life.TrySetAllowedTargets(key, allowed);
            CheckEq(life.AllowedTargetCount(key), 2, "C: 白名单去重");
            Check(life.IsTargetAllowed(key, 21u) && !life.IsTargetAllowed(key, 23u), "C: 白名单判定");

            double stopAt = Now();
            PMProjectileStopOutcome stop1 = life.TryRecordStop(key, new PMVector3(9f, 1f, 9f), stopAt, 0, 0);
            CheckEq(stop1.Result, PMProjectileStopResult.Recorded, "C: 首次停止 -> Recorded");
            Check(stop1.StopEventEmitted, "C: 首次停止产生事件");
            CheckEq(stop1.TombstoneUntilMs, stopAt + 150.0, "C: 墓碑 = stopAt+150（prediction=0,delay=0）");
            Check(life.IsRegistered(key), "C: 停止不解除注册（墓碑期保留权威记录）");
            CheckEq(life.HitTargetCount(key), 2, "C: 停止不清命中集合（墓碑期仍要去重）");
            CheckEq(life.AllowedTargetCount(key), 2, "C: 停止不清白名单（回收才清）");
            PMProjectileStopOutcome stop2 = life.TryRecordStop(key, new PMVector3(9f, 1f, 9f), At(10.0), 999, 999);
            CheckEq(stop2.Result, PMProjectileStopResult.AlreadyStopped, "C: 重复停止 -> AlreadyStopped");
            Check(!stop2.StopEventEmitted, "C: 重复停止不产生二次事件");
            CheckEq(stop2.TombstoneUntilMs, stopAt + 150.0, "C: 重复停止不延长墓碑");

            // --- 墓碑窗口内的迟到 Verify 允许 ---
            CheckEq(life.AdmitVerify(key, stopAt + 149.0).Result, PMProjectileVerifyAdmission.AllowedInTombstone,
                "C: 墓碑内（+149ms）Verify 允许");
            CheckEq(life.AdmitVerify(key, stopAt + 149.0).TombstoneRemainingMs, 1.0,
                "C: 墓碑剩余 1ms（手算 stopAt+150-（stopAt+149））");
            CheckEq(life.AdmitVerify(key, stopAt + 150.0).Result, PMProjectileVerifyAdmission.AllowedInTombstone,
                "C: 墓碑边界（+150ms）仍允许（> 才算过期）");
            CheckEq(life.AdmitVerify(key, stopAt + 151.0).Result, PMProjectileVerifyAdmission.TombstoneExpired,
                "C: 墓碑超窗（+151ms）-> TombstoneExpired");
            Check(!life.IsRegistered(key), "C: 超窗后条目已清理");
            Check(life.IsRetired(key), "C: 超窗后进入终态标记环");
            CheckEq(life.HitTargetCount(key), 0, "C: 回收后命中集合清空");
            CheckEq(life.AllowedTargetCount(key), 0, "C: 回收后白名单清空");
            CheckEq(life.AdmitVerify(key, stopAt + 200.0).Result, PMProjectileVerifyAdmission.Retired,
                "C: 已回收 key 的 Verify -> Retired（终态不倒退）");

            // --- 墓碑公式在停止路径上的体现 ---
            PMProjectileLifecycle g = new PMProjectileLifecycle(EP);
            PMProjectileKey gk = new PMProjectileKey(EP, 200u, 1u, PMProjectileOrigin.ServerDirect);
            g.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 200u, 1u, 810u, 0u, null, null, Now());
            double t = Now();
            PMProjectileStopOutcome gs = g.TryRecordStop(gk, PMVector3.Zero, t, 300, 0);
            CheckEq(gs.TombstoneUntilMs, t + 700.0, "C: 墓碑 = max(150,0,2*300+100=700) = 700");
            PMProjectileLifecycle g2 = new PMProjectileLifecycle(EP);
            PMProjectileKey gk2 = new PMProjectileKey(EP, 201u, 1u, PMProjectileOrigin.ServerDirect);
            g2.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 201u, 1u, 811u, 0u, null, null, Now());
            double t2 = Now();
            PMProjectileStopOutcome gs2 = g2.TryRecordStop(gk2, PMVector3.Zero, t2, 0, 400);
            CheckEq(gs2.TombstoneUntilMs, t2 + 400.0, "C: 墓碑 = max(150,400,100) = 400");

            // --- 墓碑到期清理（墙钟驱动）---
            PMProjectileLifecycle p = new PMProjectileLifecycle(EP);
            PMProjectileKey pk = new PMProjectileKey(EP, 300u, 1u, PMProjectileOrigin.ServerDirect);
            p.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 300u, 1u, 820u, 0u, null, null, Now());
            double ps = Now();
            p.TryRecordStop(pk, PMVector3.Zero, ps, 0, 0);
            CheckEq(p.PurgeExpired(ps + 149.0), 0, "C: 墓碑未到期不清理");
            CheckEq(p.PurgeExpired(ps + 151.0), 1, "C: 墓碑到期清理 1 条");
            CheckEq(p.RegistrationCount, 0, "C: 清理后登记表为空");
            CheckEq(p.PurgeExpired(double.NaN), 0, "C: 清理墙钟非法 -> 不动作");

            // --- 停止事件与镜像的关系（两种到达顺序都只产生一次事件）---
            PMProjectileLifecycle m = new PMProjectileLifecycle(EP);
            PMProjectileKey mk = new PMProjectileKey(EP, 400u, 1u, PMProjectileOrigin.ServerDirect);
            m.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 400u, 1u, 830u, 0u, null, null, Now());
            PMProjectileState stoppedMirror = new PMProjectileState();
            stoppedMirror.Stopped = true;
            stoppedMirror.Position = new PMVector3(1f, 1f, 1f);

            // 顺序一：本地先产生停止事件 —— 随后镜像带 Stopped 不得再发一次事件（也不得再挪动它）。
            CheckEq(m.TryRecordStop(mk, PMVector3.Zero, Now(), 0, 0).Result, PMProjectileStopResult.Recorded,
                "C: 显式停止 -> Recorded（一次事件）");
            PMProjectileMirrorOutcome afterStop = m.TryApplyMirror(mk, stoppedMirror, Now());
            CheckEq(afterStop.Result, PMProjectileMirrorResult.StoppedNotMoved,
                "C: 已停止后镜像 -> StoppedNotMoved（停止是运动终态，不写位置）");
            Check(!afterStop.PositionApplied, "C: 已停止后镜像不写位置");
            CheckEq(m.TryRecordStop(mk, PMVector3.Zero, Now(), 0, 0).Result, PMProjectileStopResult.AlreadyStopped,
                "C: 镜像带来的 Stopped 不算第二次停止事件");

            // 顺序二：镜像先带 Stopped —— 之后的本地重复停止仍不得发事件。
            PMProjectileLifecycle m2 = new PMProjectileLifecycle(EP);
            PMProjectileKey mk2 = new PMProjectileKey(EP, 401u, 1u, PMProjectileOrigin.ServerDirect);
            m2.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 401u, 1u, 831u, 0u, null, null, Now());
            CheckEq(m2.TryApplyMirror(mk2, stoppedMirror, Now()).Result, PMProjectileMirrorResult.Applied,
                "C: 镜像先带 Stopped -> Applied");
            PMProjectileStopOutcome mirrorFirst = m2.TryRecordStop(mk2, PMVector3.Zero, Now(), 0, 0);
            CheckEq(mirrorFirst.Result, PMProjectileStopResult.AlreadyStopped,
                "C: 镜像已停止后重复停止 -> AlreadyStopped");
            Check(!mirrorFirst.StopEventEmitted, "C: 重复停止不发事件");

            CheckEq(m.TryRecordStop(new PMProjectileKey(EP, 402u, 1u, PMProjectileOrigin.ServerDirect), PMVector3.Zero, Now(), 0, 0).Result,
                PMProjectileStopResult.UnknownKey, "C: 未知 key 停止 -> UnknownKey");
            CheckEq(m.TryRecordStop(mk, PMVector3.Zero, double.NaN, 0, 0).Result, PMProjectileStopResult.InvalidClock,
                "C: 停止墙钟非法 -> InvalidClock");

            // --- 断连清理产生待取释放项（有界队列可读）---
            PMProjectileLifecycle ol = new PMProjectileLifecycle(EP);
            PMProjectileKey ok1 = new PMProjectileKey(EP, 500u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileKey ok2 = new PMProjectileKey(EP, 500u, 2u, PMProjectileOrigin.ClientPredicted);
            ol.TryRegisterPredicted(500u, 1u, 1u, null, null, Now());
            ol.TryRegisterPredicted(500u, 2u, 2u, null, null, Now());
            CheckEq(ol.ClearOwner(500u, Now()), 2, "C: 断连清理 2 条");
            PMProjectileActivationRelease[] drained;
            CheckEq(ol.DrainPendingReleases(10, out drained), 2, "C: 待取释放队列 2 条");
            Check(drained[0].RemovedInstance && drained[1].RemovedInstance, "C: 断连释放要求撤销预测实例");
            CheckEq(drained[0].Reason, PMProjectileReleaseReason.OwnerCleared, "C: 释放原因=OwnerCleared");
            Check(!ol.IsRegistered(ok1) && !ol.IsRegistered(ok2), "C: 断连后记录已移除");
        }

        // =============================================================== D

        private static PMProjectileKey Pk(uint ownerNetId, uint projectileId)
        {
            return new PMProjectileKey(EP, ownerNetId, projectileId, PMProjectileOrigin.ClientPredicted);
        }

        private static PMProjectileSpawnRequest SpawnReq(PMProjectileKey key)
        {
            PMProjectileSpawnRequest r = new PMProjectileSpawnRequest();
            r.State = new PMProjectileState();
            r.State.Key = key;
            r.State.Position = new PMVector3(key.ProjectileId, 1f, 0f);
            r.Spec = new PMProjectileSpec();
            return r;
        }

        private static void TestPendingSpawns()
        {
            double t0 = Now();
            PMProjectilePendingSpawns store = new PMProjectilePendingSpawns(EP);

            // --- 128/owner 容量：满则拒新并计数 ---
            for (uint i = 1u; i <= PMProjectileLimits.MaxPendingSpawnsPerOwner; i++)
            {
                PMProjectileKey k = Pk(900u, i);
                PMProjectilePendingSpawnOutcome o = store.TryEnqueue(k, i, 7u, 0u, 0, SpawnReq(k), Now());
                if (o.Result != PMProjectilePendingSpawnResult.Enqueued)
                {
                    Check(false, "D: 第 " + i + " 条挂起 Spawn 应入队，实际 " + o.Result);
                    return;
                }
            }

            CheckEq(store.CountForOwner(900u), 128, "D: 每 owner 上限 = 128");
            CheckEq(store.Count, 128, "D: 总计数 128");
            PMProjectileKey overKey = Pk(900u, 129u);
            PMProjectilePendingSpawnOutcome over = store.TryEnqueue(overKey, 129u, 7u, 0u, 0, SpawnReq(overKey), Now());
            CheckEq(over.Result, PMProjectilePendingSpawnResult.OwnerCapacity, "D: 第 129 条 -> OwnerCapacity（拒新）");
            CheckEq(store.RejectedByCapacityCount, 1, "D: 容量拒绝计数 = 1");

            // --- 同一完整 key 重复入队拒重且保留原始挂起时刻 ---
            PMProjectileKey dupKey = Pk(900u, 1u);
            CheckEq(store.TryEnqueue(dupKey, 1u, 7u, 0u, 0, SpawnReq(dupKey), Now()).Result,
                PMProjectilePendingSpawnResult.DuplicateKey, "D: 同一完整 key 重复入队 -> DuplicateKey");

            // --- 深 clone：返回条目与入参解耦 ---
            PMProjectilePendingSpawn cloneGet;
            Check(store.TryGet(dupKey, out cloneGet), "D: 读取挂起项");
            CheckEq(cloneGet.Request.State.Position.X, 1f, "D: 挂起项冻结值");
            cloneGet.Request.State.Position = new PMVector3(777f, 0f, 0f);
            cloneGet.Request.Spec.SpeedMps = 777f;
            PMProjectilePendingSpawn cloneGet2;
            store.TryGet(dupKey, out cloneGet2);
            CheckEq(cloneGet2.Request.State.Position.X, 1f, "D: 深 clone - 返回副本改写不影响内部");
            CheckEq(cloneGet2.Request.Spec.SpeedMps, 10f, "D: 深 clone - Spec 默认 10 未被改写");

            // --- TTL：1999 不清理 / 2000 清理，且清理不生成任何东西 ---
            CheckEq(store.PurgeExpired(t0 + 1999.0, out _droppedSpawnsPlaceholder), 0, "D: TTL 未到（1999ms）不清理");
            PMProjectilePendingSpawnRelease[] dropped;
            CheckEq(store.PurgeExpired(t0 + 2000.0, out dropped), 128, "D: TTL 到（2000ms）清理全部 128 条");
            CheckEq(dropped[0].Reason, PMProjectileReleaseReason.TombstoneExpired, "D: TTL 清理原因=TombstoneExpired");
            Check(dropped[0].Spawn == null, "D: TTL 清理不携带可生成载荷（不生成幽灵弹）");
            CheckEq(store.Count, 0, "D: TTL 清理后为空");

            // --- 解挂：Confirmed 携带挂起时长，Rejected 丢弃 ---
            PMProjectilePendingSpawns store2 = new PMProjectilePendingSpawns(EP);
            double e0 = Now();
            store2.TryEnqueue(Pk(901u, 1u), 1u, 51u, 111u, 40, SpawnReq(Pk(901u, 1u)), e0);
            At(120.0);
            store2.TryEnqueue(Pk(901u, 2u), 2u, 51u, 112u, 40, SpawnReq(Pk(901u, 2u)), Now());
            At(30.0);
            store2.TryEnqueue(Pk(901u, 3u), 3u, 52u, 113u, 40, SpawnReq(Pk(901u, 3u)), Now());
            CheckEq(store2.Count, 3, "D: 3 条挂起项");
            PMProjectilePendingSpawnRelease[] rel;
            CheckEq(store2.ResolveByActivation(901u, 51u, PMActivationResult.Confirmed, Now(), out rel), 2,
                "D: 同一 activation 解挂 2 条");
            Check(rel[0].Spawn != null && rel[1].Spawn != null, "D: Confirmed 携带挂起载荷");
            CheckEq(rel[0].ExtraDeferMs, 150, "D: ExtraDeferMs = 150（挂起时长须叠加进追赶窗口）");
            CheckEq(rel[1].ExtraDeferMs, 30, "D: 第二条 ExtraDeferMs = 30");
            CheckEq(rel[0].Reason, PMProjectileReleaseReason.ActivationConfirmed, "D: 解挂原因=ActivationConfirmed");
            CheckEq(store2.Count, 1, "D: 另一 activation 的挂起项保留");
            PMProjectilePendingSpawnRelease[] rel2;
            CheckEq(store2.ResolveByActivation(901u, 52u, PMActivationResult.Rejected, Now(), out rel2), 1,
                "D: Rejected 丢弃 1 条");
            Check(rel2[0].Spawn == null, "D: Rejected 不携带载荷（丢弃而非生成）");
            CheckEq(store2.Count, 0, "D: 解挂后为空");
            PMProjectilePendingSpawnRelease[] rel3;
            CheckEq(store2.ResolveByActivation(901u, 52u, PMActivationResult.Pending, Now(), out rel3), 0,
                "D: Pending 不解挂");
            CheckEq(rel3.Length, 0, "D: Pending 不产生释放项");

            // --- 断连清理 + 时钟负向 ---
            PMProjectilePendingSpawns store3 = new PMProjectilePendingSpawns(EP);
            double s0 = Now();
            store3.TryEnqueue(Pk(902u, 1u), 1u, 60u, 0u, 0, SpawnReq(Pk(902u, 1u)), s0);
            store3.TryEnqueue(Pk(903u, 1u), 1u, 60u, 0u, 0, SpawnReq(Pk(903u, 1u)), s0);
            PMProjectilePendingSpawnRelease[] d1;
            CheckEq(store3.DiscardOwner(902u, Now(), out d1), 1, "D: 断连清理只清该 owner");
            CheckEq(store3.Count, 1, "D: 其它 owner 保留");
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 60u, 0u, 0, SpawnReq(Pk(902u, 2u)), double.NaN).Result,
                PMProjectilePendingSpawnResult.InvalidClock, "D: NaN 墙钟 -> InvalidClock");
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 60u, 0u, 0, SpawnReq(Pk(902u, 2u)), s0 - 5.0).Result,
                PMProjectilePendingSpawnResult.InvalidClock, "D: 墙钟倒退 -> InvalidClock");
            CheckEq(store3.TryEnqueue(new PMProjectileKey(EP, 0u, 2u, PMProjectileOrigin.ClientPredicted),
                2u, 60u, 0u, 0, SpawnReq(Pk(902u, 2u)), Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: owner=0 -> Invalid");
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 0u, 60u, 0u, 0, SpawnReq(Pk(902u, 2u)), Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: source=0 -> Invalid");
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 0u, 0u, 0, SpawnReq(Pk(902u, 2u)), Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: activationId=0 -> Invalid（等待激活的弹必须有非 0 激活号）");
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 60u, 0u, -1, SpawnReq(Pk(902u, 2u)), Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: predictionMs<0 -> Invalid");
            CheckEq(store3.TryEnqueue(new PMProjectileKey(EP + 1u, 902u, 2u, PMProjectileOrigin.ClientPredicted),
                2u, 60u, 0u, 0, SpawnReq(Pk(902u, 2u)), Now()).Result,
                PMProjectilePendingSpawnResult.StaleEpoch, "D: 跨 epoch -> StaleEpoch");
            PMProjectileSpawnRequest nanReq = SpawnReq(Pk(902u, 2u));
            nanReq.State.Position = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 60u, 0u, 0, nanReq, Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: 非 finite 载荷 -> Invalid");
            PMProjectileSpawnRequest wrongReq = SpawnReq(Pk(902u, 99u));
            CheckEq(store3.TryEnqueue(Pk(902u, 2u), 2u, 60u, 0u, 0, wrongReq, Now()).Result,
                PMProjectilePendingSpawnResult.Invalid, "D: 载荷身份与 key 不一致 -> Invalid");
            Check(!store3.AdvanceEpoch(EP), "D: epoch 不递增 -> 拒绝");
            Check(store3.AdvanceEpoch(EP + 1u), "D: 升 epoch");
            CheckEq(store3.Count, 0, "D: 升 epoch 清空挂起项");
        }

        private static PMProjectilePendingSpawnRelease[] _droppedSpawnsPlaceholder;

        // =============================================================== E

        private static PMProjectileVerifyRequest VerifyReq(uint projectileId, int rewindMs, float px)
        {
            PMProjectileVerifyRequest r = new PMProjectileVerifyRequest();
            r.Key = new PMProjectileKey(EP, 700u, projectileId, PMProjectileOrigin.ClientPredicted);
            r.PreviousPosition = new PMVector3(px - 1f, 1f, 0f);
            r.HitPosition = new PMVector3(px, 1f, 0f);
            r.RewindMs = rewindMs;
            r.TotalSimTimeMs = 1000.0 + px;
            r.Targets = new PMProjectileHitCandidate[1];
            r.Targets[0].TargetNetId = projectileId * 10u;
            return r;
        }

        private static void TestPendingVerifies()
        {
            PMProjectileLifecycle life = new PMProjectileLifecycle(EP);
            PMProjectilePendingVerifies store = new PMProjectilePendingVerifies(EP);
            PMProjectileKey key = new PMProjectileKey(EP, 700u, 1u, PMProjectileOrigin.ClientPredicted);

            // --- 单 key 上限 5 ---
            for (int i = 0; i < PMProjectileLimits.MaxPendingVerifiesPerKey; i++)
            {
                CheckEq(store.TryStash(VerifyReq(1u, i, (float)i), Now()).Result,
                    PMProjectileVerifyStashResult.Stashed, "E: 第 " + (i + 1) + " 条暂存成功");
            }

            CheckEq(store.CountForKey(key), 5, "E: 单 key 上限 = 5");
            CheckEq(store.TryStash(VerifyReq(1u, 5, 5f), Now()).Result, PMProjectileVerifyStashResult.KeyCapacity,
                "E: 第 6 条 -> KeyCapacity（丢新到）");
            CheckEq(store.RejectedByCapacityCount, 1, "E: 容量拒绝计数 1");
            Check(store.IsPendingMarkerSet(key), "E: pending 标记已置位");

            // --- 总量上限 128 ---
            PMProjectilePendingVerifies big = new PMProjectilePendingVerifies(EP);
            int ok = 0;
            for (int i = 0; i < PMProjectileLimits.MaxPendingVerifies; i++)
            {
                PMProjectileVerifyRequest req = VerifyReq((uint)(1000 + i), 0, 0f);
                if (big.TryStash(req, Now()).Result == PMProjectileVerifyStashResult.Stashed) { ok++; }
            }

            CheckEq(ok, PMProjectileLimits.MaxPendingVerifies, "E: 总量 128 条全部入队");
            CheckEq(big.Count, 128, "E: 总量 = 128");
            CheckEq(big.TryStash(VerifyReq(9999u, 0, 0f), Now()).Result, PMProjectileVerifyStashResult.TotalCapacity,
                "E: 第 129 条 -> TotalCapacity");

            // --- 注册必须先于回放 ---
            PMProjectileVerifyReplayOutcome noReg = store.TryBeginReplay(life, key, 0, Now());
            Check(noReg.RegistrationMissing, "E: 未注册就回放 -> RegistrationMissing");
            Check(!noReg.MarkerCleared, "E: 未注册的回放不消耗 pending 标记");
            CheckEq(noReg.Items.Length, 0, "E: 未注册的回放不给条目");
            Check(store.IsPendingMarkerSet(key), "E: 标记仍在（等待注册完成后回放）");

            // --- 注册后回放：先清标记，再按 FIFO 返回 ---
            PMProjectileRegisterOutcome reg = life.TryRegisterPredicted(700u, 1u, 1u, null, null, Now());
            CheckEq(reg.Result, PMProjectileRegisterResult.Registered, "E: 补登记成功");
            PMProjectileVerifyReplayOutcome replay = store.TryBeginReplay(life, key, 33, Now());
            Check(!replay.RegistrationMissing, "E: 注册后回放可用");
            Check(replay.MarkerCleared, "E: 回放解除 pending 标记");
            CheckEq(replay.ExtraDeferMs, 33, "E: 回放携带 ExtraDeferMs");
            CheckEq(replay.DrainedCount, 5, "E: 回放 5 条");
            CheckEq(replay.Items.Length, 5, "E: 独占条目数组长度 5");
            bool fifo = true;
            for (int i = 0; i < 5; i++)
            {
                if (replay.Items[i].RewindMs != i) { fifo = false; }
            }

            Check(fifo, "E: 回放严格 FIFO（RewindMs 0..4）");
            Check(!store.IsPendingMarkerSet(key), "E: 回放后标记已清除");
            CheckEq(store.CountForKey(key), 0, "E: 回放后条目已出队");
            PMProjectileVerifyReplayOutcome again = store.TryBeginReplay(life, key, 0, Now());
            Check(!again.MarkerCleared, "E: 二次回放 -> 标记已清（不可重入消费）");
            CheckEq(again.DrainedCount, 0, "E: 二次回放 0 条");

            // --- 深 clone：返回条目改写不影响后续（重新暂存验证）---
            store.TryStash(VerifyReq(1u, 7, 8f), Now());
            PMProjectileVerifyReplayOutcome c1 = store.TryBeginReplay(life, key, 0, Now());
            CheckEq(c1.Items[0].Targets[0].TargetNetId, 10u, "E: 冻结目标值 10");
            c1.Items[0].Targets[0].TargetNetId = 555u;
            c1.Items[0].RewindMs = 999;
            store.TryStash(VerifyReq(1u, 7, 8f), Now());
            PMProjectileVerifyReplayOutcome c2 = store.TryBeginReplay(life, key, 0, Now());
            CheckEq(c2.Items[0].Targets[0].TargetNetId, 10u, "E: 深 clone - 返回副本改写不影响新暂存");
            CheckEq(c2.Items[0].RewindMs, 7, "E: 深 clone - RewindMs 未被改写");

            // --- TTL（随 Spawn 2000ms）+ 丢弃 + 时钟负向 ---
            PMProjectilePendingVerifies ttl = new PMProjectilePendingVerifies(EP);
            double s0 = Now();
            ttl.TryStash(VerifyReq(5u, 1, 1f), s0);
            CheckEq(ttl.PurgeExpired(s0 + 1999.0), 0, "E: TTL 未到不清理");
            CheckEq(ttl.PurgeExpired(s0 + 2000.0), 1, "E: TTL 到清理 1 条");
            CheckEq(ttl.Count, 0, "E: TTL 清理后为空");
            Check(!ttl.IsPendingMarkerSet(new PMProjectileKey(EP, 700u, 5u, PMProjectileOrigin.ClientPredicted)),
                "E: TTL 清理同时清标记");
            At(2001.0);
            CheckEq(ttl.TryStash(VerifyReq(6u, 1, 1f), Now()).Result, PMProjectileVerifyStashResult.Stashed,
                "E: 清理后可继续暂存");
            CheckEq(ttl.Discard(new PMProjectileKey(EP, 700u, 6u, PMProjectileOrigin.ClientPredicted)), 1,
                "E: Discard 丢弃 1 条");
            CheckEq(ttl.TryStash(VerifyReq(7u, 1, 1f), s0 - 10.0).Result,
                PMProjectileVerifyStashResult.InvalidClock, "E: 墙钟倒退 -> InvalidClock");
            CheckEq(ttl.TryStash(VerifyReq(7u, -1, 1f), Now()).Result,
                PMProjectileVerifyStashResult.Invalid, "E: 负数 RewindMs -> Invalid");
            CheckEq(ttl.TryStash(VerifyReq(7u, 1, 1f) , double.PositiveInfinity).Result,
                PMProjectileVerifyStashResult.InvalidClock, "E: +Inf 墙钟 -> InvalidClock");
            PMProjectileVerifyRequest foreign = VerifyReq(8u, 1, 1f);
            foreign.Key = new PMProjectileKey(EP + 3u, 700u, 8u, PMProjectileOrigin.ClientPredicted);
            CheckEq(ttl.TryStash(foreign, Now()).Result, PMProjectileVerifyStashResult.StaleEpoch,
                "E: 跨 epoch 暂存 -> StaleEpoch");
            CheckEq(ttl.TryStash(null, Now()).Result, PMProjectileVerifyStashResult.Invalid, "E: null 请求 -> Invalid");
        }

        // =============================================================== F

        private static PMProjectileValidatedHitSet HitSet(PMProjectileKey key, uint targetNetId, float x)
        {
            PMProjectileValidatedHitSet s = new PMProjectileValidatedHitSet();
            s.Key = key;
            s.Hits = new PMProjectileValidatedHit[1];
            s.Hits[0].TargetNetId = targetNetId;
            s.Hits[0].TargetStreamVersion = 1u;
            s.Hits[0].ImpactPoint = new PMVector3(x, 1f, 0f);
            s.Hits[0].Resolution = PMProjectileHistoryResolution.FrameAnchor;
            return s;
        }

        private static void TestPendingHits()
        {
            PMProjectilePendingHits store = new PMProjectilePendingHits(EP);
            PMProjectileKey key = new PMProjectileKey(EP, 800u, 1u, PMProjectileOrigin.ClientPredicted);

            // --- Pending 绝不结算 ---
            PMProjectileHitResolveOutcome none = store.TryResolve(key, PMActivationResult.Pending, Now());
            CheckEq(none.Result, PMProjectileHitResolveResult.NotFound, "F: 无组时 Pending -> NotFound");

            double t0 = Now();
            PMProjectileHitStashOutcome s1 = store.TryStash(key, HitSet(key, 10u, 1f), t0);
            CheckEq(s1.Result, PMProjectileHitStashResult.Created, "F: 建组");
            CheckEq(s1.OriginalWallTimeMs, t0, "F: 原始时刻 = 入队时刻");
            CheckEq(store.GroupCount, 1, "F: 组数 1");

            // --- 同 key 合并不刷新原始时刻 ---
            At(500.0);
            PMProjectileHitStashOutcome s2 = store.TryStash(key, HitSet(key, 11u, 2f), Now());
            CheckEq(s2.Result, PMProjectileHitStashResult.Appended, "F: 同 key 追加");
            CheckEq(s2.OriginalWallTimeMs, t0, "F: 合并不刷新原始时刻（仍为 t0）");
            PMProjectilePendingHitGroup grp;
            Check(store.TryGetGroup(key, out grp), "F: 读组");
            CheckEq(grp.SetCount, 2, "F: 组内 2 条已消毒结论");
            CheckEq(grp.HitCount, 2, "F: 共 2 个命中");
            CheckEq(grp.EnqueuedWallTimeMs, t0, "F: 组原始时刻 t0");

            // --- Pending 状态保留且不结算 ---
            PMProjectileHitResolveOutcome pend = store.TryResolve(key, PMActivationResult.Pending, At(100.0));
            CheckEq(pend.Result, PMProjectileHitResolveResult.PendingNoSettlement, "F: Pending -> 不结算");
            Check(!pend.Settled, "F: Pending 零结算");
            CheckEq(pend.Sets.Length, 0, "F: Pending 不给结算数据");
            Check(store.Contains(key), "F: Pending 保留组");

            // --- Confirm 只兑现一次 ---
            PMProjectileHitResolveOutcome conf = store.TryResolve(key, PMActivationResult.Confirmed, At(50.0));
            CheckEq(conf.Result, PMProjectileHitResolveResult.ConfirmedOnce, "F: Confirm -> 兑现一次");
            Check(conf.Settled, "F: Confirm 结算标志");
            CheckEq(conf.SettledSetCount, 2, "F: 结算 2 条结论");
            CheckEq(conf.SettledHitCount, 2, "F: 结算 2 个命中");
            CheckEq(conf.Sets.Length, 2, "F: 独占结算数据 2 条");
            CheckEq(conf.OriginalWallTimeMs, t0, "F: 结算带回原始时刻");
            CheckEq(conf.Sets[1].Hits[0].TargetNetId, 11u, "F: 合并后的第二条内容正确");
            CheckEq(conf.Sets[1].Hits[0].Resolution, PMProjectileHistoryResolution.FrameAnchor,
                "F: 结论携带 Resolution（退化信息不得丢）");
            Check(!store.Contains(key), "F: 兑现后组已移除");
            PMProjectileHitResolveOutcome confAgain = store.TryResolve(key, PMActivationResult.Confirmed, At(10.0));
            CheckEq(confAgain.Result, PMProjectileHitResolveResult.AlreadyResolved, "F: 重复确认 -> AlreadyResolved");
            Check(!confAgain.Settled, "F: 重复确认不结算");
            CheckEq(confAgain.Sets.Length, 0, "F: 重复确认不给数据");
            Check(store.IsResolvedMarkerSet(key), "F: 已兑现标记置位");

            // --- 终态之后不允许再攒（否则迟到的一批会重开一份可结算数据）---
            CheckEq(store.TryStash(key, HitSet(key, 12u, 3f), At(1.0)).Result,
                PMProjectileHitStashResult.AlreadyResolved, "F: Confirm 后再暂存 -> AlreadyResolved");
            Check(!store.Contains(key), "F: 迟到暂存不得重新建组");
            CheckEq(store.TryResolve(key, PMActivationResult.Pending, Now()).Result,
                PMProjectileHitResolveResult.AlreadyResolved, "F: 终态后 Pending 也不再是 NotFound");

            // --- Reject 无结算，且同样是终态 ---
            PMProjectileKey rk = new PMProjectileKey(EP, 800u, 2u, PMProjectileOrigin.ClientPredicted);
            store.TryStash(rk, HitSet(rk, 20u, 3f), Now());
            PMProjectileHitResolveOutcome rej = store.TryResolve(rk, PMActivationResult.Rejected, At(10.0));
            CheckEq(rej.Result, PMProjectileHitResolveResult.RejectedNoSettlement, "F: Reject -> 无结算");
            Check(!rej.Settled, "F: Reject 不结算");
            CheckEq(rej.Sets.Length, 0, "F: Reject 不给数据");
            Check(!store.Contains(rk), "F: Reject 移除组");
            Check(store.IsResolvedMarkerSet(rk), "F: Reject 也打终态标记（不得被迟到 Confirmed 反转）");
            CheckEq(store.TryStash(rk, HitSet(rk, 21u, 4f), At(1.0)).Result,
                PMProjectileHitStashResult.AlreadyResolved, "F: Reject 后再暂存 -> AlreadyResolved");
            PMProjectileHitResolveOutcome afterReject = store.TryResolve(rk, PMActivationResult.Confirmed, At(1.0));
            CheckEq(afterReject.Result, PMProjectileHitResolveResult.AlreadyResolved,
                "F: Reject 后迟到 Confirm -> AlreadyResolved（不得结算）");
            Check(!afterReject.Settled, "F: Reject 后迟到 Confirm 零结算");

            // --- TTL 无结算 ---
            PMProjectilePendingHits ttl = new PMProjectilePendingHits(EP);
            PMProjectileKey tk = new PMProjectileKey(EP, 800u, 3u, PMProjectileOrigin.ClientPredicted);
            double tt = Now();
            ttl.TryStash(tk, HitSet(tk, 30u, 4f), tt);
            CheckEq(ttl.PurgeExpired(tt + 1999.0, out _droppedHitsPlaceholder), 0, "F: TTL 未到不清理");
            PMProjectilePendingHitExpiry[] expired;
            CheckEq(ttl.PurgeExpired(tt + 2000.0, out expired), 1, "F: TTL 到清理 1 组");
            CheckEq(expired[0].SetCount, 1, "F: 过期信息含 1 条结论");
            PMProjectileHitResolveOutcome afterTtl = ttl.TryResolve(tk, PMActivationResult.Confirmed, tt + 2001.0);
            CheckEq(afterTtl.Result, PMProjectileHitResolveResult.NotFound, "F: TTL 过期后 Confirm -> NotFound（不结算）");
            Check(!afterTtl.Settled, "F: TTL 路径零结算");

            // --- 128 组上限：满丢最旧并统计 ---
            PMProjectilePendingHits cap = new PMProjectilePendingHits(EP);
            for (uint i = 1u; i <= PMProjectileLimits.MaxPendingHitGroups; i++)
            {
                PMProjectileKey k = new PMProjectileKey(EP, 801u, i, PMProjectileOrigin.ClientPredicted);
                PMProjectileHitStashOutcome co = cap.TryStash(k, HitSet(k, i * 3u, (float)i), Now());
                if (co.Result != PMProjectileHitStashResult.Created)
                {
                    Check(false, "F: 第 " + i + " 组应 Created，实际 " + co.Result);
                    return;
                }
            }

            CheckEq(cap.GroupCount, 128, "F: 组上限 = 128");
            PMProjectileKey oldest = new PMProjectileKey(EP, 801u, 1u, PMProjectileOrigin.ClientPredicted);
            Check(cap.Contains(oldest), "F: 第 1 组在容量内");
            PMProjectileKey newest = new PMProjectileKey(EP, 801u, 129u, PMProjectileOrigin.ClientPredicted);
            PMProjectileHitStashOutcome ev = cap.TryStash(newest, HitSet(newest, 999u, 9f), Now());
            CheckEq(ev.Result, PMProjectileHitStashResult.EvictedOldest, "F: 第 129 组 -> 丢最旧");
            CheckEq(ev.DroppedGroups, 1, "F: 丢组计数 1");
            CheckEq(cap.GroupCount, 128, "F: 组数仍为 128");
            Check(!cap.Contains(oldest), "F: 最旧组已被丢弃");
            Check(cap.Contains(newest), "F: 最新组在表内");

            // --- 深 clone：入参结论改写不影响内部 ---
            PMProjectilePendingHits cl = new PMProjectilePendingHits(EP);
            PMProjectileKey ck = new PMProjectileKey(EP, 802u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileValidatedHitSet mutable = HitSet(ck, 42u, 6f);
            cl.TryStash(ck, mutable, Now());
            mutable.Hits[0].TargetNetId = 4242u;
            mutable.Hits[0].ImpactPoint = new PMVector3(999f, 0f, 0f);
            PMProjectileHitResolveOutcome cres = cl.TryResolve(ck, PMActivationResult.Confirmed, Now());
            CheckEq(cres.Sets[0].Hits[0].TargetNetId, 42u, "F: 深 clone - 入参结论改写不影响冻结值");
            CheckEq(cres.Sets[0].Hits[0].ImpactPoint.X, 6f, "F: 深 clone - 入参位置改写不影响冻结值");
            cres.Sets[0].Hits[0].TargetNetId = 777u;
            PMProjectileKey cl2 = new PMProjectileKey(EP, 802u, 2u, PMProjectileOrigin.ClientPredicted);
            Check(cl.TryStash(cl2, HitSet(cl2, 43u, 7f), Now()).Result == PMProjectileHitStashResult.Created,
                "F: 返回改写后仍可继续暂存");

            // --- 负向：非可信结论 / 非法 key / epoch / 时钟 ---
            PMProjectilePendingHits bad = new PMProjectilePendingHits(EP);
            PMProjectileKey bk = new PMProjectileKey(EP, 803u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileValidatedHitSet wrongId = HitSet(bk, 42u, 1f);
            wrongId.Key = new PMProjectileKey(EP, 803u, 2u, PMProjectileOrigin.ClientPredicted);
            CheckEq(bad.TryStash(bk, wrongId, Now()).Result, PMProjectileHitStashResult.Invalid,
                "F: 结论身份与 key 不一致 -> Invalid");
            PMProjectileValidatedHitSet zeroTarget = HitSet(bk, 0u, 1f);
            CheckEq(bad.TryStash(bk, zeroTarget, Now()).Result, PMProjectileHitStashResult.Invalid,
                "F: TargetNetId=0 -> Invalid（不是可信结论）");
            PMProjectileValidatedHitSet nanHit = HitSet(bk, 42u, 1f);
            nanHit.Hits[0].ImpactPoint = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(bad.TryStash(bk, nanHit, Now()).Result, PMProjectileHitStashResult.Invalid,
                "F: 非 finite ImpactPoint -> Invalid");
            PMProjectileValidatedHitSet tooMany = HitSet(bk, 42u, 1f);
            tooMany.Hits = new PMProjectileValidatedHit[PMProjectileLimits.MaxTargets + 1];
            for (int i = 0; i < tooMany.Hits.Length; i++)
            {
                tooMany.Hits[i].TargetNetId = (uint)(i + 1);
                tooMany.Hits[i].ImpactPoint = new PMVector3(1f, 1f, 1f);
            }

            CheckEq(bad.TryStash(bk, tooMany, Now()).Result, PMProjectileHitStashResult.Invalid,
                "F: 超过每批 100 目标 -> Invalid");
            PMProjectileHitStashOutcome badEpoch = bad.TryStash(
                new PMProjectileKey(EP + 1u, 803u, 9u, PMProjectileOrigin.ClientPredicted),
                HitSet(new PMProjectileKey(EP + 1u, 803u, 9u, PMProjectileOrigin.ClientPredicted), 1u, 1f), Now());
            CheckEq(badEpoch.Result, PMProjectileHitStashResult.StaleEpoch, "F: 跨 epoch 暂存 -> StaleEpoch");
            CheckEq(bad.TryStash(bk, null, Now()).Result, PMProjectileHitStashResult.Invalid, "F: null 结论 -> Invalid");
            CheckEq(bad.TryStash(bk, HitSet(bk, 1u, 1f), double.NaN).Result,
                PMProjectileHitStashResult.InvalidClock, "F: NaN 墙钟 -> InvalidClock");
            CheckEq(bad.TryResolve(bk, PMActivationResult.Confirmed, double.NaN).Result,
                PMProjectileHitResolveResult.InvalidClock, "F: 结算墙钟非法 -> InvalidClock");
            Check(!bad.AdvanceEpoch(EP), "F: epoch 不递增 -> 拒绝");
            PMProjectileHitStashOutcome beforeEpoch = bad.TryStash(bk, HitSet(bk, 1u, 1f), Now());
            CheckEq(beforeEpoch.Result, PMProjectileHitStashResult.Created, "F: 升 epoch 前建组");
            Check(bad.AdvanceEpoch(EP + 1u), "F: 升 epoch");
            CheckEq(bad.GroupCount, 0, "F: 升 epoch 清空组");
            CheckEq(bad.DroppedGroupCount, 0, "F: 升 epoch 重置计数");
        }

        private static PMProjectilePendingHitExpiry[] _droppedHitsPlaceholder;

        // =============================================================== G

        private static void TestContractDeviations()
        {
            // --- G1：同一 activation / 同一生成源可产出多颗散弹（键必须是完整 ProjectileKey）---
            PMProjectilePendingSpawns sg = new PMProjectilePendingSpawns(EP);
            uint src = 42u;
            uint act = 7u;
            bool all = true;
            for (uint i = 1u; i <= 20u; i++)
            {
                PMProjectileKey k = Pk(910u, i);
                if (sg.TryEnqueue(k, src, act, 0u, 12, SpawnReq(k), Now()).Result
                    != PMProjectilePendingSpawnResult.Enqueued)
                {
                    all = false;
                }
            }

            Check(all, "G: 同一 source/activation 的 20 颗散弹全部入队（旧键会被 DuplicateSource 挡掉 19 颗）");
            CheckEq(sg.Count, 20, "G: 20 条挂起项");
            CheckEq(sg.CountForOwner(910u), 20, "G: 每 owner 计数 20");
            CheckEq(sg.CountForSource(910u, src), 20, "G: 同一源计数 20（源不参与唯一性）");
            PMProjectileKey sgDup = Pk(910u, 1u);
            CheckEq(sg.TryEnqueue(sgDup, src, act, 0u, 12, SpawnReq(sgDup), Now()).Result,
                PMProjectilePendingSpawnResult.DuplicateKey, "G: 只有同一完整 key 重复才算重复");
            PMProjectilePendingSpawnRelease[] sgRel;
            CheckEq(sg.ResolveByActivation(910u, act, PMActivationResult.Confirmed, Now(), out sgRel), 20,
                "G: 一次激活解挂 20 颗散弹");
            Check(sgRel[0].Spawn != null && sgRel[19].Spawn != null, "G: 每颗弹都带回自己的挂起载荷");
            Check(sgRel[0].Spawn.Key.Equals(Pk(910u, 1u)) && sgRel[19].Spawn.Key.Equals(Pk(910u, 20u)),
                "G: 解挂顺序与载荷身份一一对应（无错位）");
            CheckEq(sg.Count, 0, "G: 解挂后清空");

            PMProjectilePendingSpawns sg2 = new PMProjectilePendingSpawns(EP);
            PMProjectileKey a1k = Pk(911u, 1u);
            PMProjectileKey a2k = Pk(911u, 2u);
            CheckEq(sg2.TryEnqueue(a1k, 5u, 100u, 0u, 0, SpawnReq(a1k), Now()).Result,
                PMProjectilePendingSpawnResult.Enqueued, "G: 同 source 同 activation 第一颗");
            CheckEq(sg2.TryEnqueue(a2k, 5u, 200u, 0u, 0, SpawnReq(a2k), Now()).Result,
                PMProjectilePendingSpawnResult.Enqueued, "G: 同 source 不同 activation 仍可共存");

            // --- G2：总预算 = 契约 1024（不是 owner×per-owner 相乘的 8192）---
            CheckEq(PMProjectilePendingLimits.MaxPendingSpawnsTotal, PMProjectileLimits.MaxProjectiles,
                "G: 总预算口径 = 契约 1024");
            PMProjectilePendingSpawns tb = new PMProjectilePendingSpawns(EP);
            int made = 0;
            bool ok = true;
            for (uint owner = 1u; owner <= 8u && ok; owner++)
            {
                for (uint i = 1u; i <= PMProjectileLimits.MaxPendingSpawnsPerOwner; i++)
                {
                    PMProjectileKey k = Pk(3000u + owner, i);
                    if (tb.TryEnqueue(k, i, 1u, 0u, 0, SpawnReq(k), Now()).Result
                        != PMProjectilePendingSpawnResult.Enqueued)
                    {
                        ok = false;
                        break;
                    }

                    made++;
                }
            }

            CheckEq(made, PMProjectilePendingLimits.MaxPendingSpawnsTotal,
                "G: 8 owner × 128 = 1024 条恰好装满");
            CheckEq(tb.Count, PMProjectilePendingLimits.MaxPendingSpawnsTotal, "G: 总量 = 1024");
            PMProjectileKey tbOver = Pk(3009u, 1u);
            CheckEq(tb.TryEnqueue(tbOver, 1u, 1u, 0u, 0, SpawnReq(tbOver), Now()).Result,
                PMProjectilePendingSpawnResult.TotalCapacity, "G: 第 1025 条 -> TotalCapacity（拒新）");
            CheckEq(tb.RejectedByCapacityCount, 1, "G: 容量拒绝计数 1");

            // --- G3：终态记忆淘汰后不得被迟到镜像/激活重新复活 ---
            PMProjectileLifecycle rl = new PMProjectileLifecycle(EP);
            PMProjectileKey rk = Pk(930u, 1u);
            rl.TryRegisterPredicted(930u, 1u, 5u, null, null, Now());
            Check(rl.Retire(rk, Now()), "G: 终态回收");
            PMProjectileState anyMirror = new PMProjectileState();
            anyMirror.Position = new PMVector3(1f, 1f, 1f);
            CheckEq(rl.TryApplyMirror(rk, anyMirror, Now()).Result, PMProjectileMirrorResult.Retired,
                "G: 已终态 key 迟到镜像 -> Retired");
            CheckEq(rl.AdmitVerify(rk, Now()).Result, PMProjectileVerifyAdmission.Retired,
                "G: 已终态 key 迟到 Verify -> Retired");
            CheckEq(rl.TryRecordStop(rk, PMVector3.Zero, Now(), 0, 0).Result, PMProjectileStopResult.Retired,
                "G: 已终态 key 停止 -> Retired");
            PMProjectileActivationRelease[] lateRel;
            CheckEq(rl.TrySetActivationResult(930u, 5u, PMActivationResult.Confirmed, Now(), out lateRel),
                PMActivationLedgerResult.Applied, "G: 迟到激活仍可写账本（账本与 key 终态是两回事）");
            CheckEq(lateRel.Length, 0, "G: 迟到 Confirmed 不得复活已终态的 key（0 条释放项）");
            Check(!rl.IsRegistered(rk), "G: 终态 key 未被复活");
            CheckEq(rl.PredictedCount, 0, "G: 预测计数仍为 0");

            for (uint i = 0u; i < (uint)(PMProjectileLifecycleLimits.MaxRetiredMarkers + 10u); i++)
            {
                PMProjectileKey k = new PMProjectileKey(EP, 931u, 10000u + i, PMProjectileOrigin.ServerDirect);
                rl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 931u, 10000u + i, 1u, 0u,
                    null, null, Now());
                rl.Retire(k, Now());
            }

            Check(!rl.IsRetired(rk), "G: 终态标记环已达 2048 上限（旧标记被淘汰）");
            CheckEq(rl.TryApplyMirror(rk, anyMirror, Now()).Result, PMProjectileMirrorResult.UnknownKey,
                "G: 标记淘汰后迟到镜像 -> UnknownKey（仍然不复活）");
            CheckEq(rl.TryRegisterPredicted(930u, 1u, 5u, null, null, Now()).Result,
                PMProjectileRegisterResult.NonMonotonicId,
                "G: 标记淘汰后同 key 复用 -> NonMonotonicId（高水位挡住复活）");

            // --- G4：每 (owner,origin) 高水位自身有界（≤ MaxOwners × 2），且永不复位 ---
            PMProjectileLifecycle wl = new PMProjectileLifecycle(EP);
            bool streamsOk = true;
            for (uint i = 1u; i <= PMProjectileLimits.MaxOwners; i++)
            {
                uint owner = 2000u + i;
                if (wl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, owner, 1u, 1u, 0u,
                    null, null, Now()).Result != PMProjectileRegisterResult.Registered)
                {
                    streamsOk = false;
                }

                if (wl.TryRegisterPredicted(owner, 1u, 1u, null, null, Now()).Result
                    != PMProjectileRegisterResult.Registered)
                {
                    streamsOk = false;
                }

                if (wl.ClearOwner(owner, Now()) != 2) { streamsOk = false; }
            }

            Check(streamsOk, "G: 64 owner × 2 origin = 128 条流全部登记并逐个断开");
            CheckEq(wl.OwnerCount, 0, "G: 全部 owner 已断开");
            CheckEq(wl.RegistrationCount, 0, "G: 登记表为空");

            // owner 占用已释放（_ownerCounts=0 < 64），所以这一条只能是「高水位流数上限」挡的。
            CheckEq(wl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 9000u, 1u, 1u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.OwnerCapacity,
                "G: 128 条流满后新 owner 的流被拒（拒绝复用记忆必须有界）");
            CheckEq(wl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 2001u, 2u, 1u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.Registered,
                "G: 已记录流的更高 ID 仍可登记（断开不清水位）");
            CheckEq(wl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 2001u, 2u, 1u, 0u,
                null, null, Now()).Result, PMProjectileRegisterResult.DuplicateId,
                "G: 已记录流的同 ID 重放 -> DuplicateId");
        }

        // =============================================================== H

        private static void TestReleaseAndRevoke()
        {
            // --- H1：out releases 与待取队列互斥（不可双消费）---
            PMProjectileLifecycle dl = new PMProjectileLifecycle(EP);
            dl.TryRegisterPredicted(940u, 1u, 8u, null, null, Now());
            dl.TryRegisterPredicted(940u, 2u, 8u, null, null, Now());
            PMProjectileActivationRelease[] dr;
            CheckEq(dl.TrySetActivationResult(940u, 8u, PMActivationResult.Rejected, Now(), out dr),
                PMActivationLedgerResult.Applied, "H: 写 Rejected");
            CheckEq(dr.Length, 2, "H: out releases 两条");
            Check(dr[0].RemovedInstance && dr[1].RemovedInstance, "H: 两条都要求实际撤销");
            PMProjectileActivationRelease[] drain1;
            CheckEq(dl.DrainPendingReleases(10, out drain1), 0,
                "H: Rejected 的 out releases 不再进待取队列（不会双消费）");
            CheckEq(dl.PendingReleaseCount, 0, "H: 待取队列计数 0");

            PMProjectileLifecycle ol = new PMProjectileLifecycle(EP);
            ol.TryRegisterPredicted(941u, 1u, 9u, null, null, Now());
            CheckEq(ol.ClearOwner(941u, Now()), 1, "H: 断连清理 1 条");
            CheckEq(ol.PendingReleaseCount, 1, "H: 断连释放进入待取队列");
            PMProjectileActivationRelease[] drain2;
            CheckEq(ol.DrainPendingReleases(10, out drain2), 1, "H: 第一次 Drain 取到 1 条");
            PMProjectileActivationRelease[] drain3;
            CheckEq(ol.DrainPendingReleases(10, out drain3), 0, "H: 第二次 Drain 取到 0（不重复可见）");
            CheckEq(ol.PendingReleaseCount, 0, "H: 队列已空");
            CheckEq(ol.DroppedReleaseCount, 0, "H: 未发生溢出丢弃");

            // --- H2：撤销只针对真实存在的本地假弹 ---
            PMProjectileLifecycle al = new PMProjectileLifecycle(EP);
            PMProjectileKey ak1 = Pk(951u, 1u);
            PMProjectileKey ak2 = Pk(951u, 2u);
            al.TryRegisterPredicted(951u, 1u, 11u, null, null, Now());
            al.TryRegisterPredicted(951u, 2u, 11u, null, null, Now());
            CheckEq(al.TryTakeoverPredicted(ak2).Result, PMProjectileTakeoverResult.TakenOver, "H: 第二颗已接管");
            PMProjectileActivationRelease[] ar;
            CheckEq(al.TrySetActivationResult(951u, 11u, PMActivationResult.Rejected, Now(), out ar),
                PMActivationLedgerResult.Applied, "H: 写 Rejected");
            CheckEq(ar.Length, 2, "H: 两颗弹一次解挂");
            Check(ar[0].RemovedInstance, "H: 活跃假弹必须被撤销");
            Check(!ar[1].RemovedInstance, "H: 已接管的弹本地实例已被权威镜像取代，不得被当成假弹删除");
            CheckEq(al.PredictedCount, 0, "H: 预测计数归零");
            Check(al.IsRetired(ak1) && al.IsRetired(ak2), "H: 两颗都进终态标记");

            PMProjectileLifecycle bl = new PMProjectileLifecycle(EP);
            PMProjectileKey bk = new PMProjectileKey(EP, 952u, 1u, PMProjectileOrigin.ServerDirect);
            bl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 952u, 1u, 1u, 0u, null, null, Now());
            CheckEq(bl.ClearOwner(952u, Now()), 1, "H: 权威弹断连清理");
            PMProjectileActivationRelease[] br;
            CheckEq(bl.DrainPendingReleases(10, out br), 0, "H: 权威实例不是假弹 -> 不产生撤销项");
            Check(bl.IsRetired(bk), "H: 权威弹仍进终态标记");

            // --- H3：登记被拒时不产生「要撤销的实例」---
            PMProjectileLifecycle vl = new PMProjectileLifecycle(EP);
            PMProjectileActivationRelease[] vr;
            CheckEq(vl.TrySetActivationResult(960u, 13u, PMActivationResult.Rejected, Now(), out vr),
                PMActivationLedgerResult.Applied, "H: 先写 Rejected");
            PMProjectileRegisterOutcome revoked = vl.TryRegisterPredicted(960u, 1u, 13u, null, null, Now());
            CheckEq(revoked.Result, PMProjectileRegisterResult.RevokedRejected, "H: 已 Rejected 的 activation 上登记 -> RevokedRejected");
            CheckEq(revoked.Releases.Length, 1, "H: 拒绝带一条说明性释放项");
            Check(!revoked.Releases[0].RemovedInstance, "H: 登记没落地 -> 没有实例可撤销");
            CheckEq(revoked.Releases[0].Reason, PMProjectileReleaseReason.ActivationRejected, "H: 释放原因=ActivationRejected");
            CheckEq(revoked.Releases[0].ActivationId, 13u, "H: 释放项带回激活号");
            CheckEq(vl.DrainPendingReleases(10, out br), 0, "H: 登记拒绝的释放项也不进待取队列");

            PMProjectileRegisterOutcome echoed = vl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect,
                961u, 1u, 77u, 5u, null, null, Now());
            CheckEq(echoed.Result, PMProjectileRegisterResult.Registered, "H: 权威登记");
            CheckEq(echoed.ActivationId, 5u, "H: 登记结果回显 activationId（调用方不必自猜）");
            CheckEq(echoed.AuthorityNetId, 77u, "H: 登记结果回显 authorityNetId");
            CheckEq(echoed.Key.OwnerNetId, 961u, "H: 登记结果回显 key");
        }

        // =============================================================== I

        private static PMProjectileState MirrorAt(float x, float y, float z)
        {
            PMProjectileState s = new PMProjectileState();
            s.Position = new PMVector3(x, y, z);
            s.PreviousPosition = new PMVector3(x - 0.5f, y, z);
            s.Velocity = new PMVector3(3f, 0f, 0f);
            s.Yaw = 45f;
            s.MoveTimeMs = 100.0;
            return s;
        }

        private static void TestMirrorStopFiniteDomain()
        {
            // --- I1：镜像先到（未登记 -> UnknownKey；追赶前 -> Applied，且不消耗追赶资格）---
            PMProjectileLifecycle hl = new PMProjectileLifecycle(EP);
            PMProjectileKey hk = new PMProjectileKey(EP, 970u, 1u, PMProjectileOrigin.ServerDirect);
            CheckEq(hl.TryApplyMirror(hk, MirrorAt(1f, 1f, 1f), Now()).Result, PMProjectileMirrorResult.UnknownKey,
                "I: 未登记镜像 -> UnknownKey（宿主必须先登记）");
            hl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 970u, 1u, 3u, 0u, null, null, Now());
            PMProjectileMirrorOutcome pre = hl.TryApplyMirror(hk, MirrorAt(5f, 1f, 0f), Now());
            CheckEq(pre.Result, PMProjectileMirrorResult.Applied, "I: 追赶前镜像 -> Applied");
            Check(pre.PositionApplied, "I: 追赶前镜像写位置");
            CheckEq(pre.Snapshot.State.Position.X, 5f, "I: 镜像位置生效");
            CheckEq(hl.TryBeginCatchUp(hk, Now()).Result, PMProjectileCatchUpResult.Started,
                "I: 镜像不消耗追赶资格（注册 -> 追赶 -> 镜像 仍是合法顺序）");

            // --- I2：镜像的身份与 finite 校验（不得半写）---
            PMProjectileState foreign = MirrorAt(9f, 1f, 1f);
            foreign.Key = new PMProjectileKey(EP, 970u, 2u, PMProjectileOrigin.ServerDirect);
            CheckEq(hl.TryApplyMirror(hk, foreign, Now()).Result, PMProjectileMirrorResult.Invalid,
                "I: 镜像自带 key 与目标不一致 -> Invalid");
            PMProjectileRegistration unchanged;
            hl.TryGetRegistration(hk, out unchanged);
            CheckEq(unchanged.State.Position.X, 5f, "I: Invalid 镜像不产生半写");
            PMProjectileState nanMirror = MirrorAt(1f, 1f, 1f);
            nanMirror.Position = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(hl.TryApplyMirror(hk, nanMirror, Now()).Result, PMProjectileMirrorResult.Invalid,
                "I: 非 finite 镜像 -> Invalid");
            CheckEq(hl.TryApplyMirror(hk, null, Now()).Result, PMProjectileMirrorResult.Invalid,
                "I: null 镜像 -> Invalid");

            // --- I3：停止态保护（停止是运动终态；镜像/推进都不得再挪动它）---
            PMProjectileLifecycle sl = new PMProjectileLifecycle(EP);
            PMProjectileKey sk = new PMProjectileKey(EP, 971u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileSpec sspec = new PMProjectileSpec();
            sspec.HideOnStop = true;
            sspec.DelayDestroyMs = 200;
            PMProjectileState sstate = new PMProjectileState();
            sstate.Position = new PMVector3(0f, 1f, 0f);
            sl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 971u, 1u, 1u, 0u, sstate, sspec, Now());
            sl.TryRecordStop(sk, new PMVector3(1f, 1f, 1f), Now(), 0, 0);

            PMProjectileState lateMove = MirrorAt(99f, 0f, 0f);
            lateMove.Velocity = new PMVector3(50f, 0f, 0f);
            lateMove.Yaw = 90f;
            lateMove.MoveTimeMs = 5000.0;
            lateMove.Stopped = false;
            lateMove.Hidden = false;
            PMProjectileMirrorOutcome afterStop = sl.TryApplyMirror(sk, lateMove, At(10.0));
            CheckEq(afterStop.Result, PMProjectileMirrorResult.StoppedNotMoved,
                "I: 已停止后镜像 -> StoppedNotMoved");
            Check(!afterStop.PositionApplied, "I: 已停止后镜像不写位置");
            PMProjectileRegistration stoppedSnap;
            sl.TryGetRegistration(sk, out stoppedSnap);
            CheckEq(stoppedSnap.State.Position.X, 1f, "I: 停止位置不被迟到镜像覆盖");
            CheckEq(stoppedSnap.State.Velocity.Length, 0f, "I: 停止后速度保持 0（不被镜像重新点燃）");
            CheckEq(stoppedSnap.State.MoveTimeMs, 0.0, "I: 停止后运动时间不被镜像推高");
            Check(stoppedSnap.Stopped && stoppedSnap.Hidden, "I: Stopped/Hidden 不被镜像翻回");
            Check(stoppedSnap.State.Stopped && stoppedSnap.State.Hidden, "I: 冻结状态与账本标志一致");
            PMProjectileMotionOutcome notMove = sl.TryAdvanceMotion(sk, new PMVector3(50f, 0f, 0f),
                new PMVector3(1f, 1f, 1f), new PMVector3(1f, 0f, 0f), 0f, 600.0);
            CheckEq(notMove.Result, PMProjectileMotionResult.NotMovable, "I: 停止后推进 -> NotMovable");
            CheckEq(notMove.Position.X, 1f, "I: NotMovable 回显当前位置（便于表现层拉回）");
            PMProjectileRegistration stopAfterAdvance;
            sl.TryGetRegistration(sk, out stopAfterAdvance);
            CheckEq(stopAfterAdvance.State.Position.X, 1f, "I: 停止后位置未被推进改写");

            // --- I4：镜像带来的停止必须建立墓碑窗口 ---
            PMProjectileLifecycle tl = new PMProjectileLifecycle(EP);
            PMProjectileKey tk = new PMProjectileKey(EP, 972u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileSpec tspec = new PMProjectileSpec();
            tspec.DelayDestroyMs = 300;
            tl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 972u, 1u, 1u, 0u, null, tspec, Now());
            PMProjectileState stoppedMirror = MirrorAt(2f, 1f, 2f);
            stoppedMirror.Stopped = true;
            double mNow = Now();
            PMProjectileMirrorOutcome first = tl.TryApplyMirror(tk, stoppedMirror, mNow);
            CheckEq(first.Result, PMProjectileMirrorResult.Applied, "I: 首次观察到的镜像停止 -> Applied（只写状态）");
            CheckEq(first.Snapshot.TombstoneUntilMs, mNow + 300.0,
                "I: 镜像停止建立墓碑 = now + max(150,300,100)");
            CheckEq(tl.AdmitVerify(tk, mNow + 100.0).Result, PMProjectileVerifyAdmission.AllowedInTombstone,
                "I: 镜像停止后墓碑内的合法迟到 Verify 允许（修复前会被误判过期并清理）");
            PMProjectileStopOutcome second = tl.TryRecordStop(tk, PMVector3.Zero, mNow + 101.0, 0, 0);
            CheckEq(second.Result, PMProjectileStopResult.AlreadyStopped, "I: 镜像已停止 -> 本地停止 AlreadyStopped");
            Check(!second.StopEventEmitted, "I: 镜像停止不产生第二停止事件");

            // --- I5：登记侧的 finite / 身份 / 域规范化 ---
            PMProjectileLifecycle fl = new PMProjectileLifecycle(EP);
            PMProjectileState nanPos = new PMProjectileState();
            nanPos.Position = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                nanPos, null, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: NaN 位置登记 -> InvalidPayload");
            PMProjectileState infVel = new PMProjectileState();
            infVel.Velocity = new PMVector3(0f, float.PositiveInfinity, 0f);
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                infVel, null, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: +Inf 速度登记 -> InvalidPayload");
            PMProjectileState nanYaw = new PMProjectileState();
            nanYaw.Yaw = float.NaN;
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                nanYaw, null, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: NaN Yaw 登记 -> InvalidPayload");
            PMProjectileState nanMove = new PMProjectileState();
            nanMove.MoveTimeMs = double.NaN;
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                nanMove, null, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: NaN MoveTimeMs 登记 -> InvalidPayload");
            PMProjectileSpec infSpeed = new PMProjectileSpec();
            infSpeed.SpeedMps = float.PositiveInfinity;
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                null, infSpeed, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: +Inf Speed 登记 -> InvalidPayload");
            PMProjectileSpec nanRadius = new PMProjectileSpec();
            nanRadius.RadiusM = float.NaN;
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                null, nanRadius, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: NaN Radius 登记 -> InvalidPayload");
            PMProjectileState wrongKey = new PMProjectileState();
            wrongKey.Key = new PMProjectileKey(EP, 980u, 77u, PMProjectileOrigin.ServerDirect);
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 980u, 1u, 1u, 0u,
                wrongKey, null, Now()).Result, PMProjectileRegisterResult.InvalidPayload,
                "I: state.Key 指向另一颗弹 -> InvalidPayload");
            CheckEq(fl.RegistrationCount, 0, "I: 被拒登记不落地");
            CheckEq(fl.OwnerCount, 0, "I: 被拒登记不占 owner");

            PMProjectileState spoof = new PMProjectileState();
            spoof.Position = new PMVector3(1f, 1f, 1f);
            spoof.AuthorityNetId = 999u;
            spoof.ActivationId = 12345u;
            spoof.Stopped = true;
            spoof.Hidden = true;
            spoof.TakenOver = true;
            CheckEq(fl.TryRegisterPredicted(981u, 1u, 21u, spoof, null, Now()).Result,
                PMProjectileRegisterResult.Registered, "I: 预测登记");
            PMProjectileKey spk = Pk(981u, 1u);
            PMProjectileRegistration sp;
            fl.TryGetRegistration(spk, out sp);
            CheckEq(sp.AuthorityNetId, 0u, "I: 预测域 AuthorityNetId 归 0（不接受入参自称）");
            CheckEq(sp.State.AuthorityNetId, 0u, "I: 冻结状态同步归 0");
            CheckEq(sp.ActivationId, 21u, "I: 激活号按登记参数盖章");
            CheckEq(sp.State.ActivationId, 21u, "I: 冻结状态同步盖章");
            Check(!sp.Stopped && !sp.State.Stopped, "I: 入参 Stopped=true 被归一（登记=新实例）");
            Check(!sp.Hidden && !sp.State.Hidden, "I: 入参 Hidden=true 被归一");
            Check(!sp.State.TakenOver, "I: 入参 TakenOver=true 被归一");
            Check(fl.IsPredictedAlive(spk), "I: 归一后位置闸门仍为开（假弹存活）");

            PMProjectileState spoof2 = new PMProjectileState();
            spoof2.AuthorityNetId = 1u;
            spoof2.ActivationId = 2u;
            CheckEq(fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 982u, 1u, 4242u, 0u,
                spoof2, null, Now()).Result, PMProjectileRegisterResult.Registered, "I: 权威登记");
            PMProjectileRegistration sp2;
            fl.TryGetRegistration(new PMProjectileKey(EP, 982u, 1u, PMProjectileOrigin.ServerDirect), out sp2);
            CheckEq(sp2.AuthorityNetId, 4242u, "I: 权威域 AuthorityNetId 按登记参数盖章");
            CheckEq(sp2.State.AuthorityNetId, 4242u, "I: 冻结状态同步盖章");

            // 目标集合的唯一真值：List（快照里的数组由它派生，不会各写一份）
            PMProjectileKey tgk = new PMProjectileKey(EP, 983u, 1u, PMProjectileOrigin.ServerDirect);
            fl.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 983u, 1u, 5u, 0u, null, null, Now());
            fl.TryAddHitTarget(tgk, 61u);
            fl.TrySetAllowedTargets(tgk, new uint[] { 62u });
            PMProjectileRegistration tg;
            fl.TryGetRegistration(tgk, out tg);
            CheckEq(tg.State.HitTargets.Length, 1, "I: 快照 HitTargets 由 List 派生");
            CheckEq(tg.State.HitTargets[0], 61u, "I: 命中目标可见");
            CheckEq(tg.State.AllowedTargets.Length, 1, "I: 快照 AllowedTargets 由 List 派生");
            CheckEq(tg.State.AllowedTargets[0], 62u, "I: 白名单可见");
            CheckEq(tg.HitTargets.Length, 1, "I: 注册快照自带 HitTargets 副本");
        }

        // =============================================================== J

        private static void TestRawVersusValidated()
        {
            // B 侧（Verify 暂存）刻意保留原始上报：非 finite 点不在 A1 静默丢弃，交给 L0 判 Rejected。
            PMProjectilePendingVerifies rawStore = new PMProjectilePendingVerifies(EP);
            PMProjectileKey rk = new PMProjectileKey(EP, 700u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileVerifyRequest raw = VerifyReq(1u, 0, 1f);
            raw.HitPosition = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(rawStore.TryStash(raw, Now()).Result, PMProjectileVerifyStashResult.Stashed,
                "J: 原始上报的非 finite 点原样暂存（等 L0-L4 判 Rejected）");
            PMProjectileLifecycle jl = new PMProjectileLifecycle(EP);
            jl.TryRegisterPredicted(700u, 1u, 1u, null, null, Now());
            PMProjectileVerifyReplayOutcome jr = rawStore.TryBeginReplay(jl, rk, 0, Now());
            CheckEq(jr.DrainedCount, 1, "J: 回放 1 条原始上报");
            Check(float.IsNaN(jr.Items[0].HitPosition.X), "J: 回放原样带出未消毒坐标");
            CheckEq(jr.Items[0].Targets.Length, 1, "J: 原始候选目标原样保留");

            // C 侧（命中结论）只收已消毒结论：非 finite / TargetNetId=0 / 身份不符一律拒。
            PMProjectilePendingHits trustStore = new PMProjectilePendingHits(EP);
            PMProjectileKey ck = new PMProjectileKey(EP, 701u, 1u, PMProjectileOrigin.ClientPredicted);
            PMProjectileValidatedHitSet nanSet = new PMProjectileValidatedHitSet();
            nanSet.Key = ck;
            nanSet.Hits = new PMProjectileValidatedHit[1];
            nanSet.Hits[0].TargetNetId = 5u;
            nanSet.Hits[0].ImpactPoint = new PMVector3(float.NaN, 0f, 0f);
            CheckEq(trustStore.TryStash(ck, nanSet, Now()).Result, PMProjectileHitStashResult.Invalid,
                "J: 结论里的非 finite 点 -> Invalid（两侧口径刻意不同）");
            nanSet.Hits[0].ImpactPoint = new PMVector3(0f, 0f, 0f);
            CheckEq(trustStore.TryStash(ck, nanSet, Now()).Result, PMProjectileHitStashResult.Created,
                "J: 消毒后同一结论可暂存");
        }

        // =============================================================== K

        private static void TestMotionAdvance()
        {
            PMProjectileLifecycle ml = new PMProjectileLifecycle(EP);
            PMProjectileKey mk = new PMProjectileKey(EP, 990u, 1u, PMProjectileOrigin.ServerDirect);
            PMProjectileSpec mspec = new PMProjectileSpec();
            mspec.DelayDestroyMs = 500;
            mspec.SpeedMps = 11f;
            PMProjectileState mstate = new PMProjectileState();
            mstate.SpawnPosition = new PMVector3(0f, 1f, 0f);
            mstate.Position = new PMVector3(0f, 1f, 0f);
            ml.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 990u, 1u, 55u, 0u, mstate, mspec, Now());
            ml.TryAddHitTarget(mk, 7u);
            ml.TrySetAllowedTargets(mk, new uint[] { 8u, 9u });
            PMProjectileRegistration before;
            ml.TryGetRegistration(mk, out before);

            PMProjectileMotionOutcome mo = ml.TryAdvanceMotion(mk, new PMVector3(3f, 1f, 4f),
                new PMVector3(2f, 1f, 4f), new PMVector3(0.6f, 0f, 0.8f), 53f, 250.0);
            CheckEq(mo.Result, PMProjectileMotionResult.Advanced, "K: 运动推进成功");
            Check(mo.IsAdvanced, "K: IsAdvanced 与 Result 一致");

            PMProjectileRegistration after;
            ml.TryGetRegistration(mk, out after);
            CheckEq(after.State.Position.X, 3f, "K: Position 已推进");
            CheckEq(after.State.Position.Z, 4f, "K: Position.Z 已推进");
            CheckEq(after.State.PreviousPosition.X, 2f, "K: PreviousPosition 已推进");
            CheckEq(after.State.Velocity.Z, 0.8f, "K: Velocity 已推进");
            CheckEq(after.State.Yaw, 53f, "K: Yaw 已推进");
            CheckEq(after.State.MoveTimeMs, 250.0, "K: MoveTimeMs 已推进");

            Check(after.Key.Equals(before.Key), "K: Key 不被覆盖");
            CheckEq(after.AuthorityNetId, 55u, "K: AuthorityNetId 不被覆盖");
            CheckEq(after.ActivationId, before.ActivationId, "K: ActivationId 不被覆盖");
            CheckEq(after.Activation, before.Activation, "K: 激活裁决不被覆盖");
            CheckEq(after.State.SpawnPosition.X, 0f, "K: SpawnPosition（寿命账本锚点）不被覆盖");
            CheckEq(after.Spec.SpeedMps, 11f, "K: Spec 不被覆盖");
            CheckEq(after.Spec.DelayDestroyMs, 500, "K: Spec.DelayDestroy 不被覆盖");
            CheckEq(after.RegisteredWallTimeMs, before.RegisteredWallTimeMs, "K: 登记时刻不被覆盖");
            CheckEq(after.State.HitTargets.Length, 1, "K: HitTargets 保留");
            CheckEq(after.State.HitTargets[0], 7u, "K: HitTargets 内容保留");
            CheckEq(after.State.AllowedTargets.Length, 2, "K: AllowedTargets 保留");
            CheckEq(after.State.AllowedTargets[1], 9u, "K: AllowedTargets 内容保留");
            Check(!after.Stopped, "K: Stopped 不被运动推翻");
            CheckEq(after.TombstoneUntilMs, 0.0, "K: 墓碑（寿命账本）不被运动覆盖");

            // 未知 / 跨 epoch / 已回收
            PMProjectileMotionOutcome unknown = ml.TryAdvanceMotion(
                new PMProjectileKey(EP, 999u, 1u, PMProjectileOrigin.ServerDirect),
                PMVector3.Zero, PMVector3.Zero, PMVector3.Zero, 0f, 1.0);
            CheckEq(unknown.Result, PMProjectileMotionResult.UnknownKey, "K: 未知 key -> UnknownKey");
            PMProjectileMotionOutcome stale = ml.TryAdvanceMotion(
                new PMProjectileKey(EP + 1u, 990u, 1u, PMProjectileOrigin.ServerDirect),
                PMVector3.Zero, PMVector3.Zero, PMVector3.Zero, 0f, 1.0);
            CheckEq(stale.Result, PMProjectileMotionResult.StaleEpoch, "K: 跨 epoch -> StaleEpoch");
            PMProjectileMotionOutcome nanAdvance = ml.TryAdvanceMotion(mk, new PMVector3(float.NaN, 0f, 0f),
                PMVector3.Zero, PMVector3.Zero, 0f, 1.0);
            CheckEq(nanAdvance.Result, PMProjectileMotionResult.Invalid, "K: 非 finite 推进 -> Invalid");
            PMProjectileRegistration stillAfter;
            ml.TryGetRegistration(mk, out stillAfter);
            CheckEq(stillAfter.State.Position.X, 3f, "K: 非法推进不产生半写");
            Check(ml.Retire(mk, Now()), "K: 回收");
            PMProjectileMotionOutcome retired = ml.TryAdvanceMotion(mk, PMVector3.Zero, PMVector3.Zero,
                PMVector3.Zero, 0f, 1.0);
            CheckEq(retired.Result, PMProjectileMotionResult.Retired, "K: 已回收 -> Retired");

            // 预测假弹可由宿主推进；结束后不可推进
            PMProjectileLifecycle pl = new PMProjectileLifecycle(EP);
            PMProjectileKey pk = Pk(991u, 1u);
            pl.TryRegisterPredicted(991u, 1u, 21u, null, null, Now());
            CheckEq(pl.TryAdvanceMotion(pk, new PMVector3(1f, 1f, 1f), PMVector3.Zero,
                new PMVector3(1f, 0f, 0f), 0f, 16.0).Result, PMProjectileMotionResult.Advanced,
                "K: 预测假弹也可由宿主推进（共享轨迹模型）");
            Check(pl.IsPredictedAlive(pk), "K: 推进不影响位置闸门判定");
            pl.NotifyPredictedEnded(pk, Now(), 0, 0);
            CheckEq(pl.TryAdvanceMotion(pk, new PMVector3(2f, 1f, 1f), PMVector3.Zero,
                PMVector3.Zero, 0f, 32.0).Result, PMProjectileMotionResult.NotMovable,
                "K: 假弹结束后不可推进");

            // 推进不改激活账本：挂起 -> 推进 -> 解挂仍然成立
            PMProjectileLifecycle al = new PMProjectileLifecycle(EP);
            PMProjectileKey ak = Pk(992u, 1u);
            al.TryRegisterPredicted(992u, 1u, 31u, null, null, Now());
            CheckEq(al.TryAdvanceMotion(ak, new PMVector3(4f, 1f, 4f), PMVector3.Zero,
                PMVector3.Zero, 0f, 64.0).Result, PMProjectileMotionResult.Advanced,
                "K: 挂起等待期也可推进本地假弹");
            PMProjectileActivationRelease[] ar;
            CheckEq(al.TrySetActivationResult(992u, 31u, PMActivationResult.Confirmed, Now(), out ar),
                PMActivationLedgerResult.Applied, "K: 推进不破坏激活账本");
            CheckEq(ar.Length, 1, "K: 解挂仍拿到该弹");
            CheckEq(ar[0].PendingElapsedMs, 0.0, "K: 挂起时长不受推进影响");
        }

        // =============================================================== L

        /// <summary>
        /// R5-A3 返工的 A1 侧支撑：
        ///   1) TryPromoteReservedToAuthority：预留假弹**原地**升级为权威本体（不重新分配 ID、不改 Origin、
        ///      权威 NetId 真实非 0、只有 Confirmed 可升级、幂等），升级后 TryBeginCatchUp 才能 Started；
        ///   2) 终态激活判决被容量淘汰后，迟到/重放的裁决不得在同一 activation 上「复活」。
        /// </summary>
        private static void TestPromotionAndTerminalMemory()
        {
            // --- 1) 升级：要求与幂等 ---
            PMProjectileLifecycle life = new PMProjectileLifecycle(EP);
            PMProjectileKey k = new PMProjectileKey(EP, 800u, 1u, PMProjectileOrigin.ClientPredicted);

            // 未登记
            CheckEq(life.TryPromoteReservedToAuthority(k, 950u, Now()).Result,
                PMProjectilePromoteResult.UnknownKey, "L: 未登记 -> UnknownKey");
            // 跨 epoch / 权威 ID=0 / 墙钟非法
            CheckEq(life.TryPromoteReservedToAuthority(
                new PMProjectileKey(EP + 1u, 800u, 1u, PMProjectileOrigin.ClientPredicted), 950u, Now()).Result,
                PMProjectilePromoteResult.StaleEpoch, "L: 跨 epoch -> StaleEpoch");

            life.TryRegisterPredicted(800u, 1u, 12u, null, null, Now());
            CheckEq(life.TryPromoteReservedToAuthority(k, 0u, Now()).Result,
                PMProjectilePromoteResult.InvalidAuthority, "L: 权威 ID=0 -> InvalidAuthority");
            CheckEq(life.TryPromoteReservedToAuthority(k, 950u, double.NaN).Result,
                PMProjectilePromoteResult.InvalidClock, "L: 墙钟 NaN -> InvalidClock");
            // activation 仍 Pending：只有 Confirmed 可升级
            CheckEq(life.TryPromoteReservedToAuthority(k, 950u, Now()).Result,
                PMProjectilePromoteResult.NotConfirmed, "L: activation Pending -> NotConfirmed");
            // 登记项未被半写
            PMProjectileRegistration before;
            life.TryGetRegistration(k, out before);
            Check(before.Predicted && !before.Promoted, "L: 被拒的升级不得半写");
            CheckEq(before.AuthorityNetId, 0u, "L: 被拒的升级不得盖章权威 ID");

            // Pending → Confirmed 后升级成功
            PMProjectileActivationRelease[] rel;
            CheckEq(life.TrySetActivationResult(800u, 12u, PMActivationResult.Confirmed, Now(), out rel),
                PMActivationLedgerResult.Applied, "L: 写 Confirmed");
            CheckEq(rel.Length, 1, "L: 解挂 1 条");

            PMProjectilePromoteOutcome p = life.TryPromoteReservedToAuthority(k, 950u, Now());
            CheckEq(p.Result, PMProjectilePromoteResult.Promoted, "L: Confirmed 后可升级");
            CheckEq(p.AuthorityNetId, 950u, "L: 升级返回真实权威 ID");

            PMProjectileRegistration after;
            life.TryGetRegistration(k, out after);
            Check(after.Key.Equals(k), "L: 升级不重新分配 ID / 不改 Key");
            CheckEq(after.Key.ProjectileId, 1u, "L: ProjectileId 不变");
            CheckEq(after.Key.Origin, PMProjectileOrigin.ClientPredicted, "L: Origin 不变");
            Check(!after.Predicted && after.Promoted, "L: 已升级为权威本体");
            CheckEq(after.AuthorityNetId, 950u, "L: 账本登记项的权威 ID 真实非 0");
            PMProjectileSpec fs;
            PMProjectileState frozen;
            life.TryGetFrozen(k, out fs, out frozen);
            CheckEq(frozen.AuthorityNetId, 950u, "L: 冻结状态的权威 ID 同步为真实值");
            CheckEq(life.PredictedCount, 0, "L: 预测计数减 1");
            CheckEq(life.AuthorityCount, 1, "L: 权威计数加 1");

            // 升级后：可以开始权威追赶（这就是 A3 玩家弹能追赶的前提）
            CheckEq(life.TryBeginCatchUp(k, Now()).Result, PMProjectileCatchUpResult.Started,
                "L: 升级后可以开始权威追赶");
            CheckEq(life.TryBeginCatchUp(k, Now()).Result, PMProjectileCatchUpResult.AlreadyStarted,
                "L: 追赶仍只开始一次");

            // 幂等 + 权威原生不算升级
            CheckEq(life.TryPromoteReservedToAuthority(k, 950u, Now()).Result,
                PMProjectilePromoteResult.AlreadyPromoted, "L: 重复升级幂等");
            CheckEq(life.AuthorityCount, 1, "L: 幂等不重复计数");
            PMProjectileKey nat = new PMProjectileKey(EP, 801u, 1u, PMProjectileOrigin.ServerDirect);
            life.TryRegisterAuthority(PMProjectileTrust.AuthorityServerDirect, 801u, 1u, 960u, 0u, null, null, Now());
            CheckEq(life.TryPromoteReservedToAuthority(nat, 960u, Now()).Result,
                PMProjectilePromoteResult.NotPredicted, "L: 权威原生登记 -> NotPredicted");

            // 已停止的预留项仍可升级（停止是运动终态，但身份必须切成权威）
            PMProjectileLifecycle life2 = new PMProjectileLifecycle(EP);
            PMProjectileKey k2 = new PMProjectileKey(EP, 802u, 1u, PMProjectileOrigin.ClientPredicted);
            life2.TryRegisterPredicted(802u, 1u, 13u, null, null, Now());
            PMProjectileActivationRelease[] rel2;
            life2.TrySetActivationResult(802u, 13u, PMActivationResult.Confirmed, Now(), out rel2);
            PMProjectileStopOutcome st = life2.TryRecordStop(k2, new PMVector3(1f, 1f, 1f), Now(), 0, 0);
            Check(st.StopEventEmitted, "L: 记录停止");
            CheckEq(life2.TryPromoteReservedToAuthority(k2, 961u, Now()).Result,
                PMProjectilePromoteResult.Promoted, "L: 已停止的预留项仍可升级（身份必须切权威）");
            CheckEq(life2.TryAdvanceMotion(k2, PMVector3.Zero, PMVector3.Zero, PMVector3.Zero, 0f, 1.0).Result,
                PMProjectileMotionResult.NotMovable, "L: 但运动仍是终态");

            // Rejected 的预留项已进终态标记：不得升级
            PMProjectileLifecycle life3 = new PMProjectileLifecycle(EP);
            PMProjectileKey k3 = new PMProjectileKey(EP, 803u, 1u, PMProjectileOrigin.ClientPredicted);
            life3.TryRegisterPredicted(803u, 1u, 14u, null, null, Now());
            PMProjectileActivationRelease[] rel3;
            life3.TrySetActivationResult(803u, 14u, PMActivationResult.Rejected, Now(), out rel3);
            CheckEq(life3.TryPromoteReservedToAuthority(k3, 962u, Now()).Result,
                PMProjectilePromoteResult.Retired, "L: Rejected 后 -> Retired");

            // --- 2) 终态判决被容量淘汰后：迟到/重放裁决不得复活 ---
            PMProjectileLifecycle ev = new PMProjectileLifecycle(EP);
            PMProjectileActivationRelease[] evRel;
            CheckEq(ev.TrySetActivationResult(900u, 1u, PMActivationResult.Rejected, Now(), out evRel),
                PMActivationLedgerResult.Applied, "L: 写入 Rejected");
            for (int i = 0; i < PMProjectileLifecycleLimits.MaxActivationLedger; i++)
            {
                ev.TrySetActivationResult(901u, (uint)(2000 + i), PMActivationResult.Confirmed, Now(), out evRel);
            }

            PMActivationResult gone;
            Check(!ev.TryGetActivationResult(900u, 1u, out gone),
                "L: 该终态判决已被账本容量淘汰（TryGetActivationResult 如实返回 false）");
            Check(ev.TerminalVerdictMemoryCount > 0, "L: 被淘汰的终态判决进入有界记忆（可观测）");
            Check(ev.EvictedTerminalLedgerCount > 0, "L: 淘汰计数可观测");

            // 迟到 Confirmed 不得复活已 Rejected 的 activation（否则之后的登记会被当成 Confirmed 直接生成）
            CheckEq(ev.TrySetActivationResult(900u, 1u, PMActivationResult.Confirmed, Now(), out evRel),
                PMActivationLedgerResult.AlreadyTerminal, "L: 淘汰后迟到 Confirmed 被拒（不复活）");
            CheckEq(evRel.Length, 0, "L: 不产生释放项");
            Check(!ev.TryGetActivationResult(900u, 1u, out gone),
                "L: 不因这次写入而新增账本项（记忆只在写路径/登记裁决生效，不改变账本可查性）");

            // 登记路径也看同一份记忆：已 Rejected 的 activation 上再登记必须 RevokedRejected
            PMProjectileLifecycle ev2 = new PMProjectileLifecycle(EP);
            PMProjectileActivationRelease[] evRel2;
            ev2.TrySetActivationResult(902u, 5u, PMActivationResult.Rejected, Now(), out evRel2);
            for (int i = 0; i < PMProjectileLifecycleLimits.MaxActivationLedger; i++)
            {
                ev2.TrySetActivationResult(903u, (uint)(3000 + i), PMActivationResult.Confirmed, Now(), out evRel2);
            }

            PMActivationResult gone2;
            Check(!ev2.TryGetActivationResult(902u, 5u, out gone2), "L: 判决已淘汰");
            CheckEq(ev2.TryRegisterPredicted(902u, 1u, 5u, null, null, Now()).Result,
                PMProjectileRegisterResult.RevokedRejected, "L: 淘汰后登记仍看到 Rejected（不复活）");
            CheckEq(ev2.PredictedCount, 0, "L: 被拒登记不产生实例");

            // 同向重复写入也保持幂等
            PMProjectileLifecycle ev3 = new PMProjectileLifecycle(EP);
            PMProjectileActivationRelease[] evRel3;
            ev3.TrySetActivationResult(904u, 7u, PMActivationResult.Confirmed, Now(), out evRel3);
            for (int i = 0; i < PMProjectileLifecycleLimits.MaxActivationLedger; i++)
            {
                ev3.TrySetActivationResult(905u, (uint)(4000 + i), PMActivationResult.Confirmed, Now(), out evRel3);
            }

            CheckEq(ev3.TrySetActivationResult(904u, 7u, PMActivationResult.Confirmed, Now(), out evRel3),
                PMActivationLedgerResult.AlreadyTerminal, "L: 淘汰后同向重复 Confirmed 也幂等拒绝");
            CheckEq(ev3.TrySetActivationResult(904u, 7u, PMActivationResult.Pending, Now(), out evRel3),
                PMActivationLedgerResult.AlreadyTerminal, "L: 终态不得被降回 Pending");
        }

        // ============================================================ 断言工具

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

        private static void CheckEq(float actual, float expected, string what)
        {
            Check(Math.Abs(actual - expected) <= 1e-5f, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(double actual, double expected, string what)
        {
            Check(Math.Abs(actual - expected) <= 1e-6, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq<T>(T actual, T expected, string what) where T : struct
        {
            Check(actual.Equals(expected), what + "（期望 " + expected + "，实际 " + actual + "）");
        }
    }
}
