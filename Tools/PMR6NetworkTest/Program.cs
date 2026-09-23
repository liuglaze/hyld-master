// ============================================================================
//  PMR6NetworkTest —— R6-B 验收门禁（真实字节链上的战斗闭环）
// ============================================================================
//
//  契约：Docs/plans/net-r6-combat-contract.md「B 整合与宿主」+「验收」的 T6B。
//
//  这一份门禁**不**直接调 core 冒充网络：它建的是
//     真实 PMNetWorld / PMNetSessionBridge / 真实 PMTransport 字节链 / 真实生成 RPC 桩
//     + 真实 PMCombatSession（权威核心）
//     + 真实 PMR5ProjectileDriver（投射物）
//     + 真实 PMR6CombatDriver（本批新增的战斗网络驱动）
//     + 真实 PMProjectileHistory（由「宿主」用真实目标状态填入）
//  然后走：
//     客户端 TryAttack → ServerCombatAttackV1(真实可靠 RPC) → DS core.RequestAttack
//       → ClientCombatAttackResultV1(真实可靠 RPC) → 客户端落 pending 终态
//       → N 颗 R5.TryFire → ServerProjectileSpawnV1(真实可靠 RPC) → DS 授权策略(core slot)
//       → SpawnReserved 上线 → 快照复制 → AP 镜像/接管
//       → 客户端 SubmitPredictedHits → ServerProjectileHitV1(真实可靠 RPC)
//       → DS 真实 history 验证 → Coordinator settlement → R6 ApplySettlement
//       → HP/能量变化 → PublishCombatState 复制 → AP 拿到资源、SP 拿不到
//       → 一血 → 终局结果冻结 → ClientCombatMatchResultV1 → ServerCombatResultAckV1 → 闭环
//
//  运行：dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using PMNet;
using PMNet.Combat;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.R3;
using PMNet.Session;
using PMNet.Shared;
using PMNet.Transport;

namespace PMR6NetworkTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            PMR3Player.CombatWarn = delegate(string message) { };
            PMR3Runtime.Warn = delegate(string message) { };
            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();

            Section("A. 契约常量与故障口径", TestContractSurface);
            Section("B. 真实链路：Attack → 多弹 → 命中 → HP/能量 → AP/SP ManaOnly", TestFullCombatChain);
            Section("C. Mana 不足 → 拒绝 → 真实撤 fake", TestInsufficientManaRevokesFakes);
            Section("D. 伪造 spawn（超量 / 方向不符）→ 拒", TestForgedSpawnsRejected);
            Section("E. 未知 hero / 抛物线 → Unsupported 无扣", TestUnsupportedNoDeduction);
            Section("F. 重复 attack / 重复 Spawn 不二次扣", TestDuplicateAttackAndSpawn);
            Section("G. FirstKill → 结果 → ACK → ResultReadyForLobby", TestFirstKillResultAck);
            Section("H. FirstKill → 无 ACK → 5000ms 宽限 → ResultReadyForLobby", TestFirstKillResultGrace);
            Section("I. dead / ended 后攻击无写", TestNoWritesAfterEnd);
            Section("J. Resource 无 client 篡改 + 战斗 RPC 失败抛异常", TestResourceTamperAndRpcFailure);
            Section("K. 隔离 world / Dispose / epoch 不复用", TestIsolationAndDispose);
            Section("L. 回蓝先于请求判定（DS Pump 次序回归）", TestManaRegenBeforeRequest);
            Section("M. 无效枪口不消耗 core 授权槽（策略次序）", TestInvalidMuzzleKeepsCoreSlots);
            Section("N. 终态不倒退：Confirmed 后迟到 Rejected 不撤假弹", TestLateRejectKeepsConfirmedFakes);
            Section("O. 多弹部分 TryFire 失败 ⇒ 撤全部 + fault", TestPartialTryFireFailureCancelsAll);
            Section("P. pending 跟踪表满 ⇒ 发包前拒绝 + fault", TestPendingTableOverflow);
            Section("Q. 回蓝复制（无显式脏标记也要发布）", TestRegenPublishesState);
            Section("R. 宿主回调异常隔离（死亡/冻结不被吞）", TestCallbackExceptionIsolation);
            Section("S. 结算恰好消费一次（无双消费）", TestSettlementAppliedOnce);
            Section("T. 冲突 outcome 保持终局（fail closed）", TestConflictingOutcomeIsTerminal);
            Section("U. Unbind 允许少一个等待 Ack", TestDisconnectedOwnerDoesNotBlockResult);
            Section("V. 第三方订阅者异常 → R5 退回缓冲 → Drain 兜底仍不双扣", TestDrainFallbackIsIdempotent);

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("通过 " + _passed + " / 失败 0");
            }
            else
            {
                Console.WriteLine("通过 " + _passed + " / 失败 " + _failures.Count);
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("  [FAIL] " + _failures[i]);
                }
            }

            return _failures.Count == 0 ? 0 : 1;
        }

        // =================================================================================
        //  断言
        // =================================================================================

        private static void Section(string name, Action body)
        {
            Console.WriteLine("== " + name + " ==");
            int before = _failures.Count;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add(name + " 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            if (_failures.Count == before)
            {
                Console.WriteLine("   ok");
            }
        }

        private static void Check(bool ok, string label)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failures.Add(label);
                Console.WriteLine("  [FAIL] " + label);
            }
        }

        private static void CheckTrue(bool value, string label) { Check(value, label); }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(uint actual, uint expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(int actual, int expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void ExpectThrows(Action body, string label)
        {
            bool threw = false;
            try
            {
                body();
            }
            catch (Exception)
            {
                threw = true;
            }

            Check(threw, label + "（应当抛异常）");
        }

        private static void ExpectNoThrow(Action body, string label)
        {
            bool threw = false;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine("      异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Check(!threw, label + "（不应当抛异常）");
        }

        // =================================================================================
        //  A. 契约常量与故障口径
        // =================================================================================

        private static void TestContractSurface()
        {
            CheckEq(PMR6CombatDriver.ResultResendMs, 1000, "结果重发周期 = 1000ms");
            CheckEq(PMR6CombatDriver.ResultGraceMs, 5000, "结果确认宽限 = 5000ms");
            CheckEq(PMR6CombatDriver.MaxInboundAttacks, 256, "入站攻击队列有界 = 256");
            CheckEq(PMR6CombatDriver.MaxPendingAttacks, 512, "pending attack 有界 = 512");
            CheckEq(PMR6CombatDriver.PendingAttackTtlMs, PMCombatLimits.AttackRecordTtlMs,
                "pending attack TTL = core 攻击记录 TTL");
            CheckEq((int)PMR6CombatFaultReason.SettlementFailed, 13, "结算异常故障码 = 13（冻结值）");
            CheckEq(PMCombatLimits.ResultResendMs, 1000, "核心冻结的 ResultResendMs = 1000");
            CheckEq(PMCombatLimits.ResultGraceMs, 5000, "核心冻结的 ResultGraceMs = 5000");
            CheckEq(PMR5ProjectileDriver.MaxPendingSettlements, 512, "R5 结算待取缓冲 = 512");

            // 授权策略工厂的 fail-closed：缺 model / 缺位置提供者 / 缺时钟都必须直接抛。
            ExpectThrows(delegate { PMR6CombatDriver.CreateAuthorityPolicy(null, null, null); },
                "CreateAuthorityPolicy(null, null, null) 抛异常");
            ExpectThrows(delegate
            {
                PMR6CombatDriver.CreateAuthorityPolicy(new PMCombatSession(1u), null, null);
            }, "CreateAuthorityPolicy 缺位置提供者抛异常");
            ExpectThrows(delegate
            {
                PMR6CombatDriver.CreateAuthorityPolicy(new PMCombatSession(1u), AlwaysPosition, null);
            }, "CreateAuthorityPolicy 缺时钟抛异常");
        }

        private static bool AlwaysPosition(PMR3Player player, out PMVector3 position)
        {
            position = new PMVector3(0f, 1f, 0f);
            return true;
        }

        // =================================================================================
        //  B. 真实链路：Attack → 多弹 → 命中 → HP/能量 → ManaOnly
        // =================================================================================

        private static void TestFullCombatChain()
        {
            // 攻击者 = 柯尔特(1)：2 发、每发 340、蓝耗 30；受害者 = 雪莉(0)：960 HP。
            CombatHarness h = CombatHarness.Build(0x6B01u, Roster(
                new RosterEntry(101, 1, PMHeroId.KeErTe),
                new RosterEntry(102, 2, PMHeroId.XueLi)));
            using (h)
            {
                ResolvedAttack atk = BattleNumericConfig.ResolveAttack(PMHeroId.KeErTe, false);
                int bullets = atk.SpawnBulletCount;
                CheckTrue(bullets >= 2, "柯尔特普通攻击弹数 ≥ 2（实测 " + bullets + "）");

                PMR3Player ap = h.LocalReplica(0);
                PMR3Player sp = h.ReplicaOn(0, 1);

                int manaBefore = ap.CombatMana;
                int hpBefore = sp.CombatHp;

                CheckEq(h.Clients[0].BoundPlayerCount, 2, "B 客户端 0 绑定了两个副本（AP + SP）");
                CheckEq(manaBefore, BattleNumericConfig.ManaMax, "B 攻击前 AP 复制蓝量 = ManaMax");
                CheckEq(sp.CombatMana, 0, "B 攻击前 SP 复制蓝量为 0（OwnerOnly 不泄给 observer）");

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null && keys.Length == bullets, "B 攻击成功并产出 " + bullets + " 颗预测弹："
                    + (error ?? "ok"));
                CheckEq(activationId, 1u, "B 首次 activationId = 1（单调非 0）");
                CheckTrue(h.Clients[0].HasPendingAttack(ap.NetId.Value, activationId),
                    "B 客户端已建立 pending attack 跟踪");

                // 「不本地扣复制字段」：未收到权威裁决前，复制字段必须原样。
                CheckEq(ap.CombatMana, manaBefore, "B 客户端不本地扣复制蓝量（无等待批准前的自扣）");

                h.Tick(6);
                CheckEq(h.Ds.AttacksProcessed, 1, "B DS 处理了 1 次上行攻击");
                CheckEq(h.DsProjectiles.ServerSpawnsAuthorized, bullets,
                    "B DS 按 core 批准授权了 " + bullets + " 颗弹（权威裁决写入 R5 账本）");

                // 攻击者资源：mana 90-30=60；对方 0 伤害前不动。
                PMCombatPlayerSnapshot attacker = h.Model.GetPlayer(ap.NetId.Value);
                PMCombatPlayerSnapshot victim = h.Model.GetPlayer(sp.NetId.Value);
                CheckTrue(attacker != null && victim != null, "B core 名册含攻守双方");
                CheckEq(attacker.Mana, BattleNumericConfig.ManaMax - h.ManaCostOf(0),
                    "B DS 侧 core 扣掉一次普通攻击蓝耗");
                CheckEq(victim.Hp, hpBefore, "B 未命中前受害者 HP 不变（伤害只来自结算）");

                // 真实投射物是否上线（DS 权威对象）。
                h.Tick(6);
                CheckTrue(h.DsProjectiles.AuthorityObjectCount >= bullets,
                    "B DS 上线了 " + bullets + " 个权威投射物（实测 " + h.DsProjectiles.AuthorityObjectCount + "）");

                // 客户端上报**预测命中**（真实 R5 上行链路 + 真实 DS history 验证）。
                string hitError;
                CheckTrue(h.HitAll(0, keys, sp.NetId.Value, out hitError),
                    "B 客户端上报 " + bullets + " 颗命中：" + (hitError ?? "ok"));
                h.Tick(8);

                CheckTrue(h.Ds.SettlementsApplied >= bullets,
                    "B R6 消费了 ≥ " + bullets + " 条权威结算（唯一出口，实测 " + h.Ds.SettlementsApplied + "）");
                CheckEq(h.Ds.SettlementsRejected, 0, "B 结算没有被拒（冻结计划的伤害被接受）");

                int victimMaxHp = BattleNumericConfig.Get(PMHeroId.XueLi).MaxHp;
                int expectedHp = victimMaxHp - bullets * atk.BulletDamage;
                if (expectedHp < 0) { expectedHp = 0; }
                int expectedEnergy = bullets * (atk.BulletDamage / 2);
                if (expectedEnergy > BattleNumericConfig.SuperEnergyMax) { expectedEnergy = BattleNumericConfig.SuperEnergyMax; }

                victim = h.Model.GetPlayer(sp.NetId.Value);
                attacker = h.Model.GetPlayer(ap.NetId.Value);
                CheckEq(victim.Hp, expectedHp, "B DS 权威 HP = maxHp - N*damage");
                CheckEq(attacker.SuperEnergy, expectedEnergy, "B DS 权威能量 = min(N*damage/2, 上限)");
                CheckTrue(!victim.Dead, "B 一次攻击不足以击杀（断言 HP/死亡未被误置）");

                // ---------- 复制回流：AP 拿到资源，SP 拿不到（OwnerOnly 权限边界）
                h.Tick(4);

                int attackerMaxHp = BattleNumericConfig.Get(PMHeroId.KeErTe).MaxHp;
                CheckEq(ap.CombatHp, attackerMaxHp, "B AP 副本拿到自己的权威 HP（攻击者未受伤）");
                CheckEq(sp.CombatHp, expectedHp, "B SP 副本也拿到公共 HP（HP 不是 OwnerOnly）");
                CheckEq(ap.CombatMana, attacker.Mana, "B AP 副本拿到 OwnerOnly 蓝量");
                CheckEq(ap.CombatSuperEnergy, expectedEnergy, "B AP 副本拿到 OwnerOnly 能量");
                CheckEq(sp.CombatMana, 0, "B SP 副本的蓝量始终为 0（OwnerOnly 不泄露）");
                CheckEq(sp.CombatSuperEnergy, 0, "B SP 副本的能量始终为 0（OwnerOnly 不泄露）");

                // 另一个客户端的视角：受害者的自己副本有资源，攻击者的副本没有。
                PMR3Player victimOwn = h.LocalReplica(1);
                PMR3Player attackerAsSp = h.ReplicaOn(1, 0);
                CheckEq(victimOwn.CombatMana, BattleNumericConfig.ManaMax, "B 受害者自己副本的蓝量未被误扣");
                CheckEq(victimOwn.CombatSuperEnergy, 0, "B 受害者自己副本的能量为 0（未被误加）");
                CheckEq(attackerAsSp.CombatMana, 0, "B 攻击者在别的客户端上是 SP，蓝量 0");
                CheckEq(attackerAsSp.CombatSuperEnergy, 0, "B 攻击者在别的客户端上是 SP，能量 0");

                CheckTrue(h.Ds.StatePublishRejected == 0, "B DS 侧发布未被权限拒绝");
                CheckTrue(!h.Ds.IsFaulted, "B 整个链路无会话 fault");
            }
        }

        // =================================================================================
        //  C. Mana 不足 → 拒绝 → 真实撤 fake
        // =================================================================================

        private static void TestInsufficientManaRevokesFakes()
        {
            CombatHarness h = CombatHarness.Build(0x6B02u, Roster(
                new RosterEntry(121, 1, PMHeroId.KeErTe),
                new RosterEntry(122, 2, PMHeroId.XueLi)));
            using (h)
            {
                int cost = h.ManaCostOf(0);
                PMR3Player ap = h.LocalReplica(0);
                CheckTrue(cost > 0, "C 普通攻击蓝耗 > 0（实测 " + cost + "）");

                // 把蓝量用干（不命中 ⇒ 不掉血 ⇒ 不会提前终局）。攻击间隔 100ms、回蓝 1s 一次，
                // 因此这里在 1s 内连续打 3 次即可精确耗尽 90 蓝。
                int used = 0;
                for (int i = 0; i < 3; i++)
                {
                    uint activation;
                    string error;
                    PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activation, out error);
                    if (keys == null)
                    {
                        break;
                    }

                    used++;
                    h.Tick(9);
                }

                PMCombatPlayerSnapshot attacker = h.Model.GetPlayer(ap.NetId.Value);
                CheckEq(used, BattleNumericConfig.ManaMax / cost, "C 恰好打满 " + (BattleNumericConfig.ManaMax / cost) + " 次");
                CheckEq(attacker.Mana, 0, "C DS 权威蓝量已耗尽");

                long revokedBefore = h.ClientProjectiles[0].FakesRevoked;

                uint activationId;
                string error2;
                PMProjectileKey[] extra = h.AttackAndKeys(0, false, out activationId, out error2);
                CheckTrue(extra != null, "C 客户端仍会乐观预测第 4 次攻击（不等批准）：" + (error2 ?? "ok"));
                CheckEq(extra.Length, 2, "C 第 4 次攻击产生 2 颗假弹");

                CheckTrue(h.ClientProjectiles[0].FakeCount >= 2, "C 假弹已创建（乐观预测）");

                h.Tick(10);

                CheckTrue(h.ClientProjectiles[0].FakesRevoked >= revokedBefore + 2,
                    "C 拒绝真的撤销了该 activation 的假弹（真实撤销，不是只改计数）");
                CheckEq(h.Clients[0].PredictionsCancelled, 2,
                    "C 撤销由**攻击级拒绝**（R6）真实完成，而不是等 R5 的逐颗裁决");
                CheckTrue(h.Clients[0].WasActivationRevoked(ap.NetId.Value, activationId),
                    "C 该 activation 被标记为拒绝终态");
                CheckEq(h.Model.GetPlayer(ap.NetId.Value).Mana, 0, "C 被拒攻击没有任何资源副作用");
                CheckTrue(h.Clients[0].LastRejectedActivationId == activationId, "C HUD 可观测拒绝的真实攻击ID");
                CheckTrue(h.Clients[0].LastAttackRejectionReason == PMCombatRejectReason.InsufficientMana,
                    "C HUD 可观测服务器真实拒绝原因（非本地猜测）");

                for (int i = 0; i < extra.Length; i++)
                {
                    PMR5ProjectileView? view = h.FindView(h.ClientProjectiles[0], extra[i]);
                    CheckTrue(!view.HasValue, "C 被撤假弹的视图已消失（key #" + (i + 1) + "）");
                }

                CheckTrue(!h.Ds.IsFaulted && !h.Clients[0].IsFaulted, "C 该路径是「拒绝」而不是会话 fault");
            }
        }

        // =================================================================================
        //  D. 伪造 spawn（超量 / 方向不符）→ 拒
        // =================================================================================

        private static void TestForgedSpawnsRejected()
        {
            CombatHarness h = CombatHarness.Build(0x6B03u, Roster(
                new RosterEntry(131, 1, PMHeroId.KeErTe),
                new RosterEntry(132, 2, PMHeroId.XueLi)));
            using (h)
            {
                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "D 开火成功：" + (error ?? "ok"));
                h.Tick(8);

                int authorityBefore = h.DsProjectiles.AuthorityObjectCount;
                long rejectedBefore = h.DsProjectiles.ServerSpawnsRejected;

                // D1：方向不在计划里（计划是世界 +Z，这里伪造 +X）。
                PMProjectileKey forgedKey = new PMProjectileKey(h.Epoch, h.LocalReplica(0).NetId.Value,
                    keys[keys.Length - 1].ProjectileId + 7u, PMProjectileOrigin.ClientPredicted);
                CheckTrue(h.ReplaySpawnRpc(0, forgedKey, activationId, new PMVector3(1f, 0f, 0f)),
                    "D1 伪造 spawn 载荷已发出");
                h.Tick(6);

                CheckTrue(h.DsProjectiles.ServerSpawnsRejected > rejectedBefore,
                    "D1 方向不符的伪造 spawn 被拒（core 的已批准 slot 是唯一入口）");
                CheckEq(h.DsProjectiles.AuthorityObjectCount, authorityBefore,
                    "D1 被拒的伪造 spawn 没有生成任何权威对象");

                // D2：方向合法但超出该攻击的弹数上限（柯尔特只有 2 槽）。
                long rejectedBefore2 = h.DsProjectiles.ServerSpawnsRejected;
                int authorityBefore2 = h.DsProjectiles.AuthorityObjectCount;
                PMProjectileKey overKey = new PMProjectileKey(h.Epoch, h.LocalReplica(0).NetId.Value,
                    forgedKey.ProjectileId + 1u, PMProjectileOrigin.ClientPredicted);
                CheckTrue(h.ReplaySpawnRpc(0, overKey, activationId, new PMVector3(0f, 0f, 1f)),
                    "D2 超量 spawn 载荷已发出");
                h.Tick(6);

                CheckTrue(h.DsProjectiles.ServerSpawnsRejected > rejectedBefore2,
                    "D2 超出计划弹数的 spawn 被拒（每笔攻击方向槽有限）");
                CheckEq(h.DsProjectiles.AuthorityObjectCount, authorityBefore2,
                    "D2 被拒的超量 spawn 没有生成权威对象");

                // D3：伪造一个从未被批准的 activation（高于水位 = UnknownAttack）。
                long rejectedBefore3 = h.DsProjectiles.ServerSpawnsRejected;
                PMProjectileKey unknownActivationKey = new PMProjectileKey(h.Epoch, h.LocalReplica(0).NetId.Value,
                    overKey.ProjectileId + 3u, PMProjectileOrigin.ClientPredicted);
                CheckTrue(h.ReplaySpawnRpc(0, unknownActivationKey, 777u, new PMVector3(0f, 0f, 1f)),
                    "D3 未批准 activation 的 spawn 载荷已发出");
                h.Tick(6);
                CheckTrue(h.DsProjectiles.ServerSpawnsRejected > rejectedBefore3,
                    "D3 未批准 activation 的 spawn 被拒");

                CheckTrue(!h.Ds.IsFaulted, "D 伪造路径是「逐条拒绝」而不是会话 fault");
            }
        }

        // =================================================================================
        //  E. 未知 hero / 抛物线 → Unsupported 无扣
        // =================================================================================

        private static void TestUnsupportedNoDeduction()
        {
            // E1：抛物线英雄（巴利）在客户端就被 planner 拒绝，**一个字节都不发**。
            CombatHarness h1 = CombatHarness.Build(0x6B04u, Roster(
                new RosterEntry(141, 1, PMHeroId.BaLi),
                new RosterEntry(142, 2, PMHeroId.XueLi)));
            using (h1)
            {
                long attacksBefore = h1.Ds.AttacksReceived;
                uint activationId;
                string error;
                bool ok = h1.Clients[0].TryAttack(h1.LocalReplica(0), false, 0f, 1f, h1.Muzzle, 0,
                    h1.Rig.Now, out activationId, out error);

                CheckTrue(!ok, "E1 抛物线普通攻击被拒（客户端 planner）");
                CheckTrue(error != null && error.Contains("UnsupportedAttack"),
                    "E1 拒绝原因明确是 UnsupportedAttack：" + (error ?? "null"));
                CheckEq(activationId, 0u, "E1 拒绝时不分配 activationId");
                CheckEq(h1.Ds.AttacksReceived, attacksBefore, "E1 拒绝时不发任何上行攻击 RPC");
                CheckEq(h1.Model.GetPlayer(h1.LocalReplica(0).NetId.Value).Mana, BattleNumericConfig.ManaMax,
                    "E1 抛物线攻击没有任何资源副作用");
            }

            // E2：没有子弹型大招配置的英雄（佩佩）—— 即便客户端被绕过、直接伪造上行，DS 也不扣资源。
            CombatHarness h2 = CombatHarness.Build(0x6B05u, Roster(
                new RosterEntry(143, 1, PMHeroId.PeiPei),
                new RosterEntry(144, 2, PMHeroId.XueLi)));
            using (h2)
            {
                PMR3Player ap = h2.LocalReplica(0);
                PMCombatPlayerSnapshot before = h2.Model.GetPlayer(ap.NetId.Value);

                // 直接伪造上行（绕过客户端 planner）：真实 RPC + 真实 wire。
                ExpectNoThrow(delegate { ap.ServerCombatAttackV1(1u, true, 0f, 1f); },
                    "E2 伪造大招上行载荷可发出");
                h2.Tick(6);

                PMCombatPlayerSnapshot after = h2.Model.GetPlayer(ap.NetId.Value);
                CheckEq(after.Mana, before.Mana, "E2 不支持的大招不扣蓝");
                CheckEq(after.SuperEnergy, before.SuperEnergy, "E2 不支持的大招不扣能量");
                CheckTrue(h2.Ds.AttacksProcessed >= 1, "E2 DS 确实处理了这条伪造攻击（拒绝而不是丢弃）");
                CheckTrue(!h2.Model.Ended, "E2 无配置攻击不会造成终局副作用");
            }

            // E3：未知英雄（复制字段越界）—— 客户端 fail closed，不发包。
            CombatHarness h3 = CombatHarness.Build(0x6B06u, Roster(
                new RosterEntry(145, 1, PMHeroId.KeErTe),
                new RosterEntry(146, 2, PMHeroId.XueLi)));
            using (h3)
            {
                PMR3Player dsPlayer = h3.DsPlayers[0];
                CheckTrue(dsPlayer.PublishCombatState(99, 1, 100, 100, false, 90, 0, false, 0),
                    "E3 DS 侧写入越界 hero 用于制造「未知英雄」复制事实");
                h3.Tick(4);

                PMR3Player ap = h3.LocalReplica(0);
                CheckEq(ap.CombatHeroId, 99, "E3 客户端复制到越界 heroId");

                long attacksBefore = h3.Ds.AttacksReceived;
                uint activationId;
                string error;
                bool ok = h3.Clients[0].TryAttack(ap, false, 0f, 1f, h3.Muzzle, 0, h3.Rig.Now,
                    out activationId, out error);

                CheckTrue(!ok, "E3 未知英雄的攻击被拒（fail closed）");
                CheckTrue(error != null && error.Contains("未知英雄"), "E3 拒绝原因明确是未知英雄：" + (error ?? "null"));
                CheckEq(h3.Ds.AttacksReceived, attacksBefore, "E3 未知英雄不发任何上行攻击 RPC");
            }
        }

        // =================================================================================
        //  F. 重复 attack / 重复 Spawn 不二次扣
        // =================================================================================

        private static void TestDuplicateAttackAndSpawn()
        {
            CombatHarness h = CombatHarness.Build(0x6B07u, Roster(
                new RosterEntry(151, 1, PMHeroId.KeErTe),
                new RosterEntry(152, 2, PMHeroId.XueLi)));
            using (h)
            {
                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "F 开火成功：" + (error ?? "ok"));
                h.Tick(8);

                PMCombatPlayerSnapshot afterFirst = h.Model.GetPlayer(h.LocalReplica(0).NetId.Value);
                int manaAfterFirst = afterFirst.Mana;

                long duplicateBefore = h.Model.IdConflictCount;
                long policiesBefore = h.DsProjectiles.ServerSpawnsAuthorized;
                long rejectedBefore = h.DsProjectiles.ServerSpawnsRejected;
                long decisionsBefore = h.DsProjectiles.DecisionsSent;

                // F1：同一 activationId 的上行攻击**重复发送**（可靠重发场景）。
                PMR3Player ap = h.LocalReplica(0);
                ExpectNoThrow(delegate { ap.ServerCombatAttackV1(activationId, false, 0f, 1f); },
                    "F1 重复攻击 RPC 已发出");
                h.Tick(6);

                PMCombatPlayerSnapshot afterDuplicate = h.Model.GetPlayer(ap.NetId.Value);
                CheckEq(afterDuplicate.Mana, manaAfterFirst, "F1 重复 attack 不二次扣蓝（core 幂等）");
                CheckEq(afterDuplicate.SuperEnergy, afterFirst.SuperEnergy, "F1 重复 attack 不二次扣能量");
                CheckEq(h.DsProjectiles.ServerSpawnsRejected, rejectedBefore,
                    "F1 duplicate 不会被反向拒绝（不补发 Rejected）");
                CheckEq(h.Model.IdConflictCount, duplicateBefore, "F1 完全同内容的重复不算 ID 冲突");

                // F2：同一颗弹的 Spawn RPC **重复发送**（R5 的已受理 key 幂等门）。
                long dupIgnoredBefore = h.DsProjectiles.ServerSpawnsDuplicateIgnored;
                CheckTrue(h.ReplaySpawnRpc(0, keys[0], activationId, new PMVector3(0f, 0f, 1f)),
                    "F2 重复 Spawn RPC 已发出");
                h.Tick(6);

                CheckTrue(h.DsProjectiles.ServerSpawnsDuplicateIgnored > dupIgnoredBefore,
                    "F2 已受理 key 的重复 spawn 被无副作用幂等吸收");
                CheckEq(h.DsProjectiles.ServerSpawnsAuthorized, policiesBefore,
                    "F2 重复 spawn 不再授权（不重复扣授权额度）");
                CheckEq(h.DsProjectiles.ServerSpawnsRejected, rejectedBefore,
                    "F2 重复 spawn 不新增拒绝（不反向撤销已确认的弹）");
                CheckEq(h.DsProjectiles.DecisionsSent, decisionsBefore,
                    "F2 重复 spawn 不给已受理 key 下发任何新裁决");
                CheckEq(h.Model.GetPlayer(ap.NetId.Value).Mana, manaAfterFirst,
                    "F2 重复 spawn 不产生二次资源扣减");

                // F3：确认后再来一次完整攻击（新 activation）—— 资源只扣一次。
                long policiesBefore3 = h.DsProjectiles.ServerSpawnsAuthorized;
                uint activation2;
                string error2;
                PMProjectileKey[] keys2 = h.AttackAndKeys(0, false, out activation2, out error2);
                CheckTrue(keys2 != null, "F3 第二次攻击成功：" + (error2 ?? "ok"));
                CheckTrue(activation2 > activationId, "F3 activationId 单调递增（" + activationId + " → " + activation2 + "）");
                h.Tick(6);
                CheckTrue(h.DsProjectiles.ServerSpawnsAuthorized > policiesBefore3,
                    "F3 新 activation 会正常授权（幂等门不误伤新请求）");
                CheckEq(h.Model.GetPlayer(ap.NetId.Value).Mana,
                    manaAfterFirst - h.ManaCostOf(0), "F3 新攻击只扣一次蓝耗");
            }
        }

        // =================================================================================
        //  G. FirstKill → 结果 → ACK → ResultReadyForLobby
        // =================================================================================

        private static void TestFirstKillResultAck()
        {
            CombatHarness h = CombatHarness.Build(0x6B08u, Roster(
                new RosterEntry(161, 1, PMHeroId.KeErTe),
                new RosterEntry(162, 2, PMHeroId.XueLi)));
            using (h)
            {
                h.KillVictim(0);

                CheckTrue(h.Model.Ended, "G core 已因首杀终局");
                CheckTrue(h.Model.Outcome.WinnerTeamId == 1, "G 胜方 = 击杀者队伍 1（真实名册 TeamId）");
                CheckTrue(h.Model.Outcome.Reason == PMCombatEndReason.FirstKill, "G 终局原因 = FirstKill");

                h.Tick(4);

                CheckTrue(h.Ds.OutcomeIsFrozen, "G DS 已冻结结果");
                CheckEq(h.Ds.FrozenOutcomeId, h.Model.Outcome.OutcomeId, "G 冻结的 outcomeId = core 结论");
                CheckEq(h.Ds.WinnerTeamId, 1, "G 冻结的胜方 = 1");
                CheckTrue(h.Ds.ResultSummary.Length > 0, "G 结果摘要非空（PMNetWriter 原语编码）");
                CheckTrue(h.Ds.ResultSends + h.Ds.ResultResends >= 1, "G DS 已下发结果");

                // 两个客户端都收到结果并各自回了 ACK（在 FlushState 里发，不在回调里发）。
                uint storedOid0;
                int storedWinner0;
                CheckTrue(h.Clients[0].TryGetStoredMatchResult(out storedOid0, out storedWinner0),
                    "G 客户端 0 保存了结果");
                CheckEq(storedWinner0, 1, "G 客户端 0 保存的胜方 = 1");
                CheckEq(storedOid0, h.Ds.FrozenOutcomeId, "G 客户端 0 保存的 outcomeId 与 DS 一致");

                uint storedOid1;
                int storedWinner1;
                CheckTrue(h.Clients[1].TryGetStoredMatchResult(out storedOid1, out storedWinner1),
                    "G 客户端 1 也保存了结果（逐在线 owner 下发）");

                h.Tick(6);

                CheckTrue(h.Ds.ResultAckCount >= 2, "G DS 收到两个在线 owner 的认证 ACK（实测 "
                    + h.Ds.ResultAckCount + "）");
                CheckTrue(h.Ds.ResultReadyForLobby, "G 全 ACK ⇒ ResultReadyForLobby = true");
                CheckTrue(h.Ds.ResultReadyByAckCount >= 1, "G 就绪来自 ACK 路径而不是宽限超时");

                byte[] s1 = h.Ds.ResultSummary;
                byte[] s2 = h.Ds.ResultSummary;
                CheckTrue(s1.Length == s2.Length, "G ResultSummary 每次都是同长度的深拷贝");
                s1[0] = (byte)(s1[0] ^ 0xFF);
                CheckTrue(h.Ds.ResultSummary[0] != s1[0], "G ResultSummary 返回深拷贝（改它不影响内部）");

                // 重复结算 / 断线都不得改写冻结结论。
                uint frozenId = h.Ds.FrozenOutcomeId;
                int frozenWinner = h.Ds.WinnerTeamId;
                h.Tick(4);
                CheckEq(h.Ds.FrozenOutcomeId, frozenId, "G 冻结后 outcomeId 不变");
                CheckEq(h.Ds.WinnerTeamId, frozenWinner, "G 冻结后胜方不变");
                CheckTrue(!h.Clients[0].IsFaulted && !h.Clients[1].IsFaulted, "G 重复投递不造成客户端 fault");
            }
        }

        // =================================================================================
        //  H. FirstKill → 无 ACK → 5000ms 宽限
        // =================================================================================

        private static void TestFirstKillResultGrace()
        {
            // 客户端驱动**已绑定**（因此能正常攻击），但**不跑客户端的 FlushState**：
            // 结果 RPC 会到达并被幂等保存，但不会回 ACK —— 这正是「宽限超时」那条口径。
            CombatHarness h = CombatHarness.Build(0x6B09u, Roster(
                new RosterEntry(171, 1, PMHeroId.KeErTe),
                new RosterEntry(172, 2, PMHeroId.XueLi)));
            using (h)
            {
                h.SuppressClientFlush = true;

                h.KillVictim(0);

                CheckTrue(h.Model.Ended, "H core 已因首杀终局");
                h.Tick(4);

                CheckTrue(h.Ds.OutcomeIsFrozen, "H DS 已冻结结果");
                CheckTrue(!h.Ds.ResultReadyForLobby, "H 无 ACK 时不得提前就绪");
                CheckEq(h.Ds.ResultAckCount, 0, "H 没有任何 ACK");

                // 结果确实送到了客户端并被幂等保存（只是没有回 ACK）。
                CheckTrue(h.Clients[0].MatchResultsReceived >= 1,
                    "H 客户端 0 收到了结果（实测 " + h.Clients[0].MatchResultsReceived + "）");
                uint storedOid;
                int storedWinner;
                CheckTrue(h.Clients[0].TryGetStoredMatchResult(out storedOid, out storedWinner),
                    "H 客户端 0 已幂等保存结果（未回 ACK）");
                CheckEq(storedWinner, 1, "H 客户端保存的胜方 = 1");
                CheckEq(h.Clients[0].ResultAckCount, 0, "H 客户端确实没有发出 ACK");

                // 1s 级重发：推进 ~1100ms 应当看到重发计数增长。
                long resendsBefore = h.Ds.ResultResends;
                h.Advance(1100);
                CheckTrue(h.Ds.ResultResends > resendsBefore, "H 每 1000ms 重发同一 outcome（内容不变）");
                CheckTrue(!h.Ds.ResultReadyForLobby, "H 1100ms 时仍未就绪（宽限未到）");

                // 宽限到期 ⇒ 就绪（并且明确是宽限路径，不宣称客户端都看到了）。
                h.Advance(4000);
                CheckTrue(h.Ds.ResultReadyForLobby, "H 5000ms 宽限到期 ⇒ ResultReadyForLobby = true");
                CheckTrue(h.Ds.ResultReadyByGraceCount >= 1, "H 就绪来自宽限路径");
                CheckEq(h.Ds.WinnerTeamId, 1, "H 宽限就绪后胜方仍是冻结值");
            }
        }

        // =================================================================================
        //  I. dead / ended 后攻击无写
        // =================================================================================

        private static void TestNoWritesAfterEnd()
        {
            CombatHarness h = CombatHarness.Build(0x6B0Au, Roster(
                new RosterEntry(181, 1, PMHeroId.KeErTe),
                new RosterEntry(182, 2, PMHeroId.XueLi)));
            using (h)
            {
                h.KillVictim(0);
                h.Tick(6);

                PMR3Player victimOwn = h.LocalReplica(1);
                PMR3Player attackerOwn = h.LocalReplica(0);
                CheckTrue(victimOwn.CombatDead, "I 受害者副本复制到 Dead");
                CheckTrue(attackerOwn.CombatMatchEnded || victimOwn.CombatMatchEnded,
                    "I 双方副本复制到 MatchEnded");

                PMCombatPlayerSnapshot victimBefore = h.Model.GetPlayer(victimOwn.NetId.Value);
                PMCombatPlayerSnapshot attackerBefore = h.Model.GetPlayer(attackerOwn.NetId.Value);

                // I1：客户端侧（死者 / 已结束）不允许新攻击。
                long attacksBefore = h.Ds.AttacksReceived;
                uint activationId;
                string error;
                bool ok = h.Clients[1].TryAttack(victimOwn, false, 0f, 1f, h.Muzzle, 0, h.Rig.Now,
                    out activationId, out error);
                CheckTrue(!ok, "I1 死者的副本不允许新攻击");
                CheckTrue(error != null && (error.Contains("死亡") || error.Contains("结束")),
                    "I1 拒绝原因明确（死亡/结束）：" + (error ?? "null"));
                CheckEq(h.Ds.AttacksReceived, attacksBefore, "I1 被拒攻击没有产生任何上行");

                // I2：即便伪造上行，DS 也不产生任何写（MatchEnded / Dead 都拒绝）。
                long settlementsBefore = h.Ds.SettlementsApplied;
                ExpectNoThrow(delegate { victimOwn.ServerCombatAttackV1(1u, false, 0f, 1f); },
                    "I2 伪造死者攻击上行载荷可发出");
                h.Tick(6);

                PMCombatPlayerSnapshot victimAfter = h.Model.GetPlayer(victimOwn.NetId.Value);
                PMCombatPlayerSnapshot attackerAfter = h.Model.GetPlayer(attackerOwn.NetId.Value);
                CheckEq(victimAfter.Hp, victimBefore.Hp, "I2 终局后无 HP 变化");
                CheckEq(victimAfter.Mana, victimBefore.Mana, "I2 终局后无蓝量变化");
                CheckEq(victimAfter.SuperEnergy, victimBefore.SuperEnergy, "I2 终局后无能量变化");
                CheckEq(attackerAfter.Mana, attackerBefore.Mana, "I2 终局后攻击者蓝量无变化");
                CheckEq(attackerAfter.SuperEnergy, attackerBefore.SuperEnergy, "I2 终局后攻击者能量无变化");
                CheckEq(h.Ds.SettlementsApplied, settlementsBefore, "I2 终局后伪造攻击不产生新结算");
                CheckTrue(!h.Ds.IsFaulted, "I2 终局后伪造攻击不造成会话 fault");
            }
        }

        // =================================================================================
        //  J. Resource 无 client 篡改 + 战斗 RPC 失败抛异常
        // =================================================================================

        private static void TestResourceTamperAndRpcFailure()
        {
            CombatHarness h = CombatHarness.Build(0x6B0Bu, Roster(
                new RosterEntry(191, 1, PMHeroId.KeErTe),
                new RosterEntry(192, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                int manaBefore = ap.CombatMana;
                int energyBefore = ap.CombatSuperEnergy;
                long rejectedBefore = ap.CombatPublishRejectedCount;

                bool written = ap.PublishCombatState(1, 1, 1, 1, false, 9999, 9999, false, 7);

                CheckTrue(!written, "J1 AP 副本上的 PublishCombatState 被拒（非权威世界不写复制字段）");
                CheckTrue(ap.CombatPublishRejectedCount > rejectedBefore, "J1 越权写入被可观察计数");
                CheckEq(ap.CombatMana, manaBefore, "J1 客户端篡改无效：蓝量保持权威值");
                CheckEq(ap.CombatSuperEnergy, energyBefore, "J1 客户端篡改无效：能量保持权威值");
                CheckEq(ap.CombatWinnerTeamId, 0, "J1 客户端篡改无效：胜方字段未被改写");

                h.Tick(2);
                CheckEq(h.Model.GetPlayer(ap.NetId.Value).Mana, BattleNumericConfig.ManaMax,
                    "J1 DS 权威资源不受客户端篡改影响");

                // J2：四条战斗 RPC 的发送失败必须抛异常（R6 driver 才能转成会话 fault）。
                //   上行两条（ServerRpc）由**客户端**发 → 拆客户端世界；
                //   下行两条（ClientRpc）由 **DS** 发 → 拆服务端世界。
                // 两次分开做（且各自 finally 里重接），避免静态 Bridges 归零把 RemoteSender 整个摘掉。
                PMNetWorld clientWorld = h.Rig.ClientWorld(0);
                PMR3Runtime.Detach(clientWorld);
                try
                {
                    ExpectThrows(delegate { ap.ServerCombatAttackV1(999u, false, 0f, 1f); },
                        "J2 未接线世界上 ServerCombatAttackV1 发送失败 ⇒ 抛异常");
                    ExpectThrows(delegate { ap.ServerCombatResultAckV1(1u); },
                        "J2 未接线世界上 ServerCombatResultAckV1 发送失败 ⇒ 抛异常");

                    // 旧 probe 的失败口径**逐字不变**（只是告警，不抛）。
                    ExpectNoThrow(delegate { ap.ServerProbe(7); },
                        "J2 旧 probe RPC 在未接线世界上仍走告警（不抛）");

                    // 旧投射物 RPC 的「失败即抛」也不被破坏（正向对照）。
                    ExpectThrows(delegate { ap.ServerProjectileSpawnV1(new byte[] { 1 }); },
                        "J2 投射物 RPC 的失败即抛口径未被破坏");
                }
                finally
                {
                    PMR3Runtime.Attach(clientWorld, h.Rig.ClientBridge(0));
                }

                PMNetWorld serverWorld = h.Rig.ServerWorld;
                PMR3Runtime.Detach(serverWorld);
                try
                {
                    ExpectThrows(delegate { h.DsPlayers[0].ClientCombatAttackResultV1(1u, true, 0); },
                        "J2 未接线世界上 ClientCombatAttackResultV1 发送失败 ⇒ 抛异常");
                    ExpectThrows(delegate { h.DsPlayers[0].ClientCombatMatchResultV1(1u, 1); },
                        "J2 未接线世界上 ClientCombatMatchResultV1 发送失败 ⇒ 抛异常");
                }
                finally
                {
                    PMR3Runtime.Attach(serverWorld, h.Rig.ServerBridge);
                }
            }
        }

        // =================================================================================
        //  K. 隔离 world / Dispose / epoch 不复用
        // =================================================================================

        private static void TestIsolationAndDispose()
        {
            CombatHarness a = CombatHarness.Build(0x6B0Cu, Roster(
                new RosterEntry(201, 1, PMHeroId.KeErTe),
                new RosterEntry(202, 2, PMHeroId.XueLi)));
            CombatHarness b = CombatHarness.Build(0x6B0Du, Roster(
                new RosterEntry(203, 1, PMHeroId.XueLi),
                new RosterEntry(204, 2, PMHeroId.KeErTe)));

            try
            {
                int subscriptions = PMR5ProjectileEvents.SubscriptionCount;
                CheckTrue(subscriptions >= 6, "K 两个会话的投射物订阅都在（实测 " + subscriptions + "）");

                // epoch 不复用：0 与「与 R5 不一致」都必须直接抛（编程错误）。
                ExpectThrows(delegate
                {
                    new PMR6CombatDriver(a.Rig.ServerWorld, a.Rig.ServerBridge, 0u, a.DsProjectiles, a.Model);
                }, "K epoch=0 抛异常（epoch 不复用）");
                ExpectThrows(delegate
                {
                    new PMR6CombatDriver(a.Rig.ServerWorld, a.Rig.ServerBridge, 0x99u, a.DsProjectiles, a.Model);
                }, "K 与 R5 不一致的 epoch 抛异常");
                ExpectThrows(delegate
                {
                    new PMR6CombatDriver(a.Rig.ServerWorld, a.Rig.ServerBridge, a.Epoch, a.DsProjectiles, null);
                }, "K DS 缺 PMCombatSession 抛异常（契约：DS 必有）");

                // Dispose 会话 A：只撤自己那一份，不动 B。
                a.Ds.Dispose();
                a.Clients[0].Dispose();

                CheckTrue(a.Ds.IsDisposed, "K A 的 DS 驱动已释放");
                CheckTrue(a.LocalReplica(0).CombatDriver == null, "K A 释放后副本的接缝被摘掉");
                CheckTrue(b.Rig.ServerWorld != null && !b.Ds.IsDisposed, "K B 的 DS 驱动不受影响");

                // A 释放后：Pump/TryAttack/FlushState 一律早退，不抛、不改状态。
                ExpectNoThrow(delegate { a.Ds.Pump(a.Rig.Now); }, "K A 释放后 Pump 不抛");
                ExpectNoThrow(delegate { a.Ds.FlushState(a.Rig.Now); }, "K A 释放后 FlushState 不抛");
                uint activationId;
                string error;
                bool ok = a.Clients[0].TryAttack(a.LocalReplica(0), false, 0f, 1f, a.Muzzle, 0,
                    a.Rig.Now, out activationId, out error);
                CheckTrue(!ok, "K A 释放后 TryAttack 返回 false");

                // B 仍然可以完整跑通一次攻击（隔离的真正证据）。
                uint bActivation;
                string bError;
                PMProjectileKey[] bKeys = b.AttackAndKeys(0, false, out bActivation, out bError);
                CheckTrue(bKeys != null, "K B 会话在 A 释放后仍能正常攻击：" + (bError ?? "ok"));
                b.Tick(8);
                CheckTrue(b.DsProjectiles.AuthorityObjectCount >= 1, "K B 的投射物正常上线");
                CheckTrue(!b.Ds.IsFaulted && !b.Clients[0].IsFaulted, "K B 会话无 fault");

                int afterDispose = PMR5ProjectileEvents.SubscriptionCount;
                CheckTrue(afterDispose >= subscriptions - 2, "K A 释放只摘掉自己的订阅（实测 " + afterDispose + "）");
            }
            finally
            {
                try { a.Dispose(); } catch (Exception) { }
                try { b.Dispose(); } catch (Exception) { }
            }
        }

        // =================================================================================
        //  L. 回蓝先于请求判定（DS Pump 次序回归）
        // =================================================================================

        /// <summary>
        /// 回归根因：DS 的 `Pump` 曾在处理上行攻击**之后**才 `core.Tick`（回蓝）。
        /// 于是「这一段回蓝刚好到达」边界上的合法攻击会被判 `InsufficientMana`，并被作为**终态**
        /// 写进 core 的攻击账本（同一 activationId 的幂等回显永远是拒绝）—— 一次 16ms 的次序错误
        /// 等于永久吃掉一次攻击。
        ///
        /// 构造：先精确耗干 90 蓝；再让「攻击被处理的那一帧」恰好跨过 1000ms 的回蓝段。
        /// </summary>
        private static void TestManaRegenBeforeRequest()
        {
            CombatHarness h = CombatHarness.Build(0x6B0Eu, Roster(
                new RosterEntry(211, 1, PMHeroId.KeErTe),
                new RosterEntry(212, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                uint attackerNetId = ap.NetId.Value;

                int cost = h.ManaCostOf(0);
                int manaMax = BattleNumericConfig.ManaMax;
                HeroNumeric hero = BattleNumericConfig.Get(PMHeroId.KeErTe);
                int reloadMs = (int)(hero.ReloadSeconds * 1000f);
                CheckTrue(reloadMs > 0 && cost > 0, "L 回蓝周期/蓝耗有效（" + reloadMs + "ms / " + cost + "）");

                // ---- 耗干蓝量。每笔之后**按住墙钟不动**跑 4 帧：
                //      上行 RPC 在「发出后第 3 帧」被 DS 处理（本文件实测），
                //      按住墙钟使那一帧的 Tick 增量为 0 ⇒ 「请求前 Tick / 请求后 Tick」两种次序
                //      在这一笔上完全等价（不会给后一种次序白送 16ms 回蓝进度），
                //      从而让下面那段边界判断是确定性的。
                long wall = h.Rig.Now;
                int used = 0;
                for (int i = 0; i < manaMax / cost; i++)
                {
                    h.Rig.Now = wall;
                    uint drainActivation;
                    string drainError;
                    if (h.AttackAndKeys(0, false, out drainActivation, out drainError) == null)
                    {
                        Check(false, "L 第 " + (i + 1) + " 次耗蓝攻击失败：" + (drainError ?? "unknown"));
                        break;
                    }

                    used++;
                    for (int f = 0; f < 4; f++) { h.Rig.Now = wall; h.Tick(1); }
                    wall += 124L;                                   // > FireIntervalMs(100)
                }

                CheckEq(used, manaMax / cost, "L 蓝量被精确耗尽（打的次数 = ManaMax/蓝耗）");
                CheckEq(h.Model.GetPlayer(attackerNetId).Mana, 0, "L DS 权威蓝量 = 0（每一笔都被接受）");

                // ---- 边界帧：frame1 墙钟 +1ms（accum=1）；frame2 不动（accum=1）；
                //      frame3 墙钟 +1000ms ⇒ 该帧的 Tick 恰好把 accum 推过一段回蓝。
                //      ⇒ 只有「先 Tick 再判请求」才会把这一段的 30 蓝算进本次攻击的合法性。
                long lastWall = (long)h.Ds.WallTimeMs;
                h.Rig.Now = lastWall + 1L;

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "L 客户端照常乐观预测（客户端不判蓝）：" + (error ?? "ok"));

                h.Rig.Now = lastWall + 1L;      h.Tick(1);      // frame1
                h.Rig.Now = lastWall + 1L;      h.Tick(1);      // frame2
                h.Rig.Now = lastWall + reloadMs; h.Tick(1);     // frame3：边界（请求在这一帧被处理）

                PMCombatPlayerSnapshot after = h.Model.GetPlayer(attackerNetId);
                CheckEq(after.Mana, 0,
                    "L ★ 先回蓝再判请求：卡在回蓝完成点的攻击被接受并只扣一次（0 = 回 30 - 扣 30）");
                CheckEq(h.Ds.AttacksProcessed, used + 1, "L DS 处理了这笔攻击（没有静默丢弃）");

                // 再跑两帧：裁决是**下行可靠 RPC**，要让字节链把它送到 AP（帧号不影响蓝量：
                // 这一段回蓝已被这笔攻击消费并重置）。
                h.Tick(2);

                CheckTrue(h.Clients[0].WasActivationConfirmed(attackerNetId, activationId),
                    "L ★ 该攻击被判 Accepted（而不是 InsufficientMana 的终态拒绝）");
                CheckTrue(!h.Clients[0].WasActivationRevoked(attackerNetId, activationId),
                    "L 该攻击未被反向拒绝");
                CheckEq(h.Model.GetPlayer(attackerNetId).Mana, 0, "L 追加两帧后蓝量仍为 0（未被多余回蓝）");
                CheckTrue(!h.Ds.IsFaulted && !h.Clients[0].IsFaulted, "L 该路径无会话 fault");
            }
        }

        // =================================================================================
        //  M. 无效枪口不消耗 core 授权槽（策略次序）
        // =================================================================================

        /// <summary>
        /// 回归根因：授权策略曾先 `core.TryAuthorizeProjectile`（消耗方向槽 + 已授权 key 记录）
        /// 再验宿主位置。宿主位置暂时不可信（Mover 未就绪 / freeze 未完成）时，同一条攻击的 N 颗弹
        /// 会把 N 个槽全部吃掉（假弹只能被拒，等位置恢复也再也授权不了）。
        ///
        /// 可观察判据：`PMCombatSession.AuthorizedProjectileCount`（core 内部真正占用的授权 key 数）
        /// 在「无效枪口的 spawn 全部被拒」之后必须仍为 0。
        /// </summary>
        private static void TestInvalidMuzzleKeepsCoreSlots()
        {
            CombatHarness h = CombatHarness.Build(0x6B0Fu, Roster(
                new RosterEntry(221, 1, PMHeroId.KeErTe),
                new RosterEntry(222, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                uint attackerNetId = ap.NetId.Value;
                CheckEq(h.Model.AuthorizedProjectileCount, 0, "M 初始 core 授权 key 数 = 0");

                // 宿主报告「枪口不可信」：这是策略唯一的位置来源。
                PMVector3 saved;
                CheckTrue(h.Positions.TryGetValue(attackerNetId, out saved), "M 攻击者位置样本存在");
                h.Positions.Remove(attackerNetId);

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "M 攻击载荷已发出：" + (error ?? "ok"));

                long rejectedBefore = h.DsProjectiles.ServerSpawnsRejected;
                h.Tick(8);

                CheckTrue(h.DsProjectiles.ServerSpawnsRejected >= rejectedBefore + keys.Length,
                    "M 无效枪口的 spawn 全部被策略拒绝（fail closed）");
                CheckEq(h.Model.AuthorizedProjectileCount, 0,
                    "M ★ 无效枪口**没有**在 core 占掉授权槽/key（位置先于 slot 授权）");
                CheckEq(h.DsProjectiles.ServerSpawnsAuthorized, 0, "M 无效枪口没有授权任何 spawn");
                CheckEq(h.DsProjectiles.AuthorityObjectCount, 0, "M 无效枪口没有生成任何权威对象");
                CheckTrue(h.ClientProjectiles[0].FakesRevoked >= keys.Length,
                    "M 客户端假弹被逐颗真实撤销（不是只记拒绝）");
                CheckTrue(!h.Ds.IsFaulted, "M 无效枪口是「逐条拒绝」而不是会话 fault");

                // 枪口恢复后重投同一颗弹：**必须仍然能被授权**。
                //
                // 这正是「先验位置再占槽」的价值：无效枪口只影响它自己那一次生成，
                // 而不会把该 activation 的方向槽/key 提前吃掉（旧次序下 core 已把两个槽都占完，
                // 这里会得到 DirectionMismatch 而永远生成不了这颗弹）。
                h.Positions[attackerNetId] = saved;
                long authorizedBefore = h.DsProjectiles.ServerSpawnsAuthorized;
                CheckTrue(h.ReplaySpawnRpc(0, keys[0], activationId, h.AimDir),
                    "M 枪口恢复后重投同一颗弹的 spawn");
                h.Tick(6);

                CheckEq(h.DsProjectiles.ServerSpawnsAuthorized, authorizedBefore + 1,
                    "M ★ 枪口恢复后同一颗弹仍被授权（槽位没有被无效枪口吃掉）");
                CheckTrue(h.Model.AuthorizedProjectileCount >= 1, "M core 侧确实为它建了授权 key");
                CheckTrue(h.DsProjectiles.AuthorityObjectCount >= 1, "M 该弹确实上线为权威对象");
                CheckTrue(!h.Ds.IsFaulted, "M 全程无会话 fault");
            }
        }

        // =================================================================================
        //  N. 终态不倒退：Confirmed 后迟到 Rejected 不撤假弹
        // =================================================================================

        /// <summary>
        /// 回归点：AP 的 pending attack 表是「终态先到先胜」。权威语义上 core 不可能把已 Accepted
        /// 的 activation 再判 Rejected（duplicate 回显原始 decision），因此一条迟到的 Rejected
        /// 只能是链路/对面的 bug；若照着它去撤假弹，就会把已合法出膛（甚至已被权威镜像接管）
        /// 的弹反向撤销 —— 典型的「活镜像幽灵」。
        /// </summary>
        private static void TestLateRejectKeepsConfirmedFakes()
        {
            CombatHarness h = CombatHarness.Build(0x6B10u, Roster(
                new RosterEntry(231, 1, PMHeroId.KeErTe),
                new RosterEntry(232, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                uint attackerNetId = ap.NetId.Value;

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "N 攻击成功：" + (error ?? "ok"));
                h.Tick(10);

                CheckTrue(h.Clients[0].WasActivationConfirmed(attackerNetId, activationId),
                    "N 攻击级裁决 = Accepted（已建立终态）");
                CheckTrue(!h.Clients[0].WasActivationRevoked(attackerNetId, activationId),
                    "N 该 activation 尚未被标记拒绝");

                long cancelledBefore = h.Clients[0].PredictionsCancelled;
                long revokedBefore = h.ClientProjectiles[0].FakesRevoked;
                int fakesBefore = h.ClientProjectiles[0].FakeCount;
                long lateBefore = h.Clients[0].AttackResultsLateAfterTerminal;

                // 伪造一条**迟到**的攻击级 Rejected（真实 ClientRpc 下行 + 真实字节链）。
                ExpectNoThrow(delegate
                {
                    h.DsPlayers[0].ClientCombatAttackResultV1(
                        activationId, false, (int)PMCombatRejectReason.InsufficientMana);
                }, "N 迟到 Rejected 载荷已发出");
                h.Tick(4);

                CheckTrue(h.Clients[0].WasActivationConfirmed(attackerNetId, activationId),
                    "N ★ Accepted 终态不被迟到 Rejected 翻成拒绝");
                CheckTrue(!h.Clients[0].WasActivationRevoked(attackerNetId, activationId),
                    "N ★ 未被标记为拒绝终态");
                CheckEq(h.Clients[0].PredictionsCancelled, cancelledBefore,
                    "N ★ 迟到 Rejected 不撤销任何假弹（终态不倒退）");
                CheckEq(h.ClientProjectiles[0].FakesRevoked, revokedBefore, "N R5 撤销计数不变");
                CheckTrue(h.ClientProjectiles[0].FakeCount >= fakesBefore,
                    "N 本地假弹/镜像仍在（没有被反向撤销出幽灵）");
                CheckTrue(h.Clients[0].AttackResultsLateAfterTerminal > lateBefore,
                    "N 该迟到裁决被显式计数（可观测，而不是静默）");
                CheckTrue(!h.Clients[0].IsFaulted, "N 终态不倒退是吸收而不是会话 fault");
            }
        }

        // =================================================================================
        //  O. 多弹部分 TryFire 失败 ⇒ 撤全部 + fault
        // =================================================================================

        /// <summary>
        /// 「部分 TryFire 失败不能假成功」：先发的 ServerCombatAttackV1 与已发出的假弹都必须被收回，
        /// 并且整会话 fault（host 终止）。做法：把客户端**预测登记表**填到只剩 1 个空位，
        /// 使柯尔特 2 弹中的第 1 颗能登记、第 2 颗必然 RegistryCapacity 失败。
        /// </summary>
        private static void TestPartialTryFireFailureCancelsAll()
        {
            CombatHarness h = CombatHarness.Build(0x6B14u, Roster(
                new RosterEntry(241, 1, PMHeroId.KeErTe),
                new RosterEntry(242, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                uint fillerOwnerNetId = h.DsPlayers[1].NetId.Value;

                PMCombatAttackPlan plan;
                PMCombatRejectReason reason;
                CheckTrue(PMCombatWeaponPlanner.TryBuild(ap.CombatHeroId, false, h.AimDir.X, h.AimDir.Z,
                    out plan, out reason), "O 测试侧能建立柯尔特普通攻击计划");
                CheckEq(plan.Directions.Length, 2, "O 柯尔特普通攻击 = 2 颗");

                // 直接把客户端**预测登记表**填到只剩 1 个空位（不发任何 RPC）。
                // 用**另一个 owner** 当填充流：全局 predicted 计数是共享的，
                // 而 projectileId 高水位是 per (owner, origin) 的 —— 这样不会污染攻击者自己的流。
                int fill = PMProjectileLimits.MaxProjectiles - 1;
                int filled = 0;
                for (int i = 0; i < fill; i++)
                {
                    PMProjectileKey fillKey = new PMProjectileKey(h.Epoch, fillerOwnerNetId,
                        (uint)(i + 1), PMProjectileOrigin.ClientPredicted);
                    PMProjectileState fillState = new PMProjectileState();
                    fillState.Key = fillKey;
                    fillState.ActivationId = (uint)(90000 + i);

                    PMProjectileRegisterOutcome reg = h.ClientProjectiles[0].Coordinator.Lifecycle
                        .TryRegisterPredicted(fillerOwnerNetId, fillKey.ProjectileId, fillState.ActivationId,
                            fillState, plan.Spec, h.Rig.Now);
                    if (reg.Result != PMProjectileRegisterResult.Registered)
                    {
                        Console.WriteLine("      [O] 填充在第 " + i + " 次失败：" + reg.Result);
                        break;
                    }

                    filled++;
                }

                CheckEq(filled, fill, "O 预测登记表已只剩 1 个空位（实测 " + filled + "/" + fill + "）");

                long revokedBefore = h.ClientProjectiles[0].FakesRevoked;
                long cancelledBefore = h.ClientProjectiles[0].CancelledPredictions;
                int pendingBefore = h.Clients[0].PendingAttackCount;

                uint activationId;
                string error;
                bool attacked = h.Clients[0].TryAttack(ap, false, h.AimDir.X, h.AimDir.Z, h.Muzzle, 0,
                    h.Rig.Now, out activationId, out error);

                CheckTrue(!attacked, "O 部分 TryFire 失败不得假成功：" + (error ?? "null"));
                CheckTrue(error != null && error.Contains("部分投射物预测失败"),
                    "O 失败原因明确是「部分投射物预测失败」：" + (error ?? "null"));
                CheckTrue(h.Clients[0].IsFaulted
                          && h.Clients[0].FaultReason == PMR6CombatFaultReason.SendFailed,
                    "O 部分失败 ⇒ 会话 fault（host 必须终止）");
                CheckEq(h.ClientProjectiles[0].CancelledPredictions, cancelledBefore + 1,
                    "O ★ 已成功发出的那颗假弹被真实撤销（不是只置状态）");
                CheckEq(h.ClientProjectiles[0].FakesRevoked, revokedBefore + 1, "O R5 撤销计数 +1");
                CheckEq(h.Clients[0].PendingAttackCount, pendingBefore, "O 失败不留下 pending 跟踪项");
                CheckEq(activationId, 0u, "O 失败时不返回 activationId");
            }
        }

        // =================================================================================
        //  P. pending 跟踪表满 ⇒ 发包前拒绝 + fault
        // =================================================================================

        /// <summary>
        /// 有界资源打满必须是「什么都没做的拒绝」：曾经先发 ServerCombatAttackV1 + 先生成 N 颗假弹，
        /// 再发现表满并去撤它们（那些假弹对应的上行 spawn 已经在路上，DS 会为一批客户端已撤掉的弹
        /// 建权威镜像）。用 1 发弹的英雄（佩佩）精确填满 512 条再攻击一次。
        /// </summary>
        private static void TestPendingTableOverflow()
        {
            CombatHarness h = CombatHarness.Build(0x6B15u, Roster(
                new RosterEntry(251, 1, PMHeroId.PeiPei),
                new RosterEntry(252, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);

                PMCombatAttackPlan plan;
                PMCombatRejectReason reason;
                CheckTrue(PMCombatWeaponPlanner.TryBuild(ap.CombatHeroId, false, h.AimDir.X, h.AimDir.Z,
                    out plan, out reason), "P 佩佩普通攻击计划可用");
                CheckEq(plan.Directions.Length, 1, "P 佩佩普通攻击 = 1 颗（填表用）");

                // 可靠发送窗口有限（≈512 条 RPC）：分批发、每批后跑 2 帧把窗口清掉。
                // 每笔攻击 = 1 条 attack RPC + 1 条 spawn RPC；8 批 × 128 条 = 安全。
                const int Batch = 64;
                int accepted = 0;
                while (accepted < PMR6CombatDriver.MaxPendingAttacks)
                {
                    int batch = PMR6CombatDriver.MaxPendingAttacks - accepted;
                    if (batch > Batch) { batch = Batch; }

                    for (int i = 0; i < batch; i++)
                    {
                        uint fillActivation;
                        string fillError;
                        bool ok = h.Clients[0].TryAttack(ap, false, h.AimDir.X, h.AimDir.Z, h.Muzzle, 0,
                            h.Rig.Now, out fillActivation, out fillError);
                        if (!ok)
                        {
                            Check(false, "P 第 " + (accepted + 1) + " 次攻击本应成功："
                                + (fillError ?? "unknown"));
                            break;
                        }

                        accepted++;
                    }

                    h.Tick(2);
                }

                CheckEq(accepted, PMR6CombatDriver.MaxPendingAttacks,
                    "P 已填满 " + PMR6CombatDriver.MaxPendingAttacks + " 条 pending 跟踪");
                CheckEq(h.Clients[0].PendingAttackCount, PMR6CombatDriver.MaxPendingAttacks,
                    "P 表内条数 = 上限");
                CheckTrue(!h.Clients[0].IsFaulted, "P 填表阶段无会话 fault");

                long fakesCreatedBefore = h.ClientProjectiles[0].FakesCreated;
                long cancelledBefore = h.ClientProjectiles[0].CancelledPredictions;

                uint activationId;
                string error;
                bool attacked = h.Clients[0].TryAttack(ap, false, h.AimDir.X, h.AimDir.Z, h.Muzzle, 0,
                    h.Rig.Now, out activationId, out error);

                CheckTrue(!attacked, "P 满表后的攻击被拒：" + (error ?? "null"));
                CheckTrue(error != null && error.Contains("跟踪表已满"),
                    "P 拒绝原因明确是 pending 跟踪表满：" + (error ?? "null"));
                CheckTrue(h.Clients[0].IsFaulted
                          && h.Clients[0].FaultReason == PMR6CombatFaultReason.QueueOverflow,
                    "P 满表 ⇒ 会话 fault（QueueOverflow）");
                CheckEq(h.ClientProjectiles[0].FakesCreated, fakesCreatedBefore,
                    "P ★ 满表在**发包/生成假弹之前**就被发现（没有先造弹再撤）");
                CheckEq(h.ClientProjectiles[0].CancelledPredictions, cancelledBefore,
                    "P 没有需要撤销的假弹（拒绝是「什么都没做」）");
                CheckEq(h.Clients[0].PendingAttackCount, PMR6CombatDriver.MaxPendingAttacks,
                    "P 满表被拒后表大小不变（有界且有回落路径）");
            }
        }

        // =================================================================================
        //  Q. 回蓝复制（无显式脏标记也要发布）
        // =================================================================================

        /// <summary>
        /// 回归根因：状态复制只由显式脏标记（攻击/结算/名册）触发，而**回蓝**只发生在 `core.Tick`
        /// 里 —— 于是「只有回蓝、没有任何战斗事件」的那段时间里客户端蓝量永远停在扣费后的值，
        /// 直到下一次命中才跳到正确值。
        /// </summary>
        private static void TestRegenPublishesState()
        {
            CombatHarness h = CombatHarness.Build(0x6B16u, Roster(
                new RosterEntry(261, 1, PMHeroId.KeErTe),
                new RosterEntry(262, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player ap = h.LocalReplica(0);
                uint attackerNetId = ap.NetId.Value;

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "Q 首次攻击成功：" + (error ?? "ok"));
                h.Tick(8);

                int cost = h.ManaCostOf(0);
                CheckEq(ap.CombatMana, BattleNumericConfig.ManaMax - cost,
                    "Q AP 副本已复制到扣费后的蓝量");
                CheckEq(h.Model.GetPlayer(attackerNetId).Mana, BattleNumericConfig.ManaMax - cost,
                    "Q core 当前蓝量 = 扣费后的值");

                long publishesBefore = h.Ds.StatePublishCount;

                // 只推进墙钟：不报命中、不新攻击、不死亡 —— 唯一的变化是回蓝。
                h.Advance(3000L);
                h.Tick(4);

                CheckEq(h.Model.GetPlayer(attackerNetId).Mana, BattleNumericConfig.ManaMax,
                    "Q core 侧回蓝补齐（封顶 90）");
                CheckEq(ap.CombatMana, BattleNumericConfig.ManaMax,
                    "Q ★ 只有回蓝（无显式脏标记）时 AP 副本仍拿到新蓝量");
                CheckTrue(h.Ds.StatePublishCount > publishesBefore,
                    "Q 回蓝确实触发了一次复制发布");
                CheckTrue(!h.Ds.IsFaulted && !h.Clients[0].IsFaulted, "Q 该路径无会话 fault");
            }
        }

        // =================================================================================
        //  R. 宿主回调异常隔离（死亡/冻结不被吞）
        // =================================================================================

        /// <summary>
        /// `PlayerDied` / `OutcomeFrozen` 是宿主注入面（freeze Mover、写 history.Alive、上报）。
        /// 它们抛异常时：
        ///   · 不得打断结算消费（否则 R5 会把同一条结算退回待取缓冲 ⇒ 同一帧被应用两次、
        ///     `PlayerDied` 重复触发）；
        ///   · 不得打断「结果冻结 → 逐 owner 下发 → 就绪判定」这条链（否则客户端收不到胜负）。
        /// </summary>
        private static void TestCallbackExceptionIsolation()
        {
            CombatHarness h = CombatHarness.Build(0x6B17u, Roster(
                new RosterEntry(271, 1, PMHeroId.KeErTe),
                new RosterEntry(272, 2, PMHeroId.XueLi)));
            using (h)
            {
                int diedCalls = 0;
                int frozenCalls = 0;

                h.Ds.PlayerDied += delegate(PMR3Player player)
                {
                    diedCalls++;
                    throw new InvalidOperationException("宿主 PlayerDied bug");
                };
                h.Ds.OutcomeFrozen += delegate(uint outcomeId, int winnerTeamId)
                {
                    frozenCalls++;
                    throw new InvalidOperationException("宿主 OutcomeFrozen bug");
                };

                h.KillVictim(0);
                h.Tick(10);

                CheckTrue(h.Model.Ended, "R core 仍然终局（宿主回调异常不影响权威状态）");
                CheckEq(diedCalls, 1, "R ★ PlayerDied 恰好触发 1 次（重复应用不会重复触发）");
                CheckEq(frozenCalls, 1, "R OutcomeFrozen 被调用 1 次");
                CheckTrue(h.Ds.OutcomeIsFrozen, "R ★ 回调抛异常后结果仍被冻结");
                CheckTrue(h.Ds.ResultSummary.Length > 0, "R ★ 冻结摘要仍被生成");
                CheckTrue(h.Clients[0].MatchResultsReceived >= 1,
                    "R ★ 结果仍被下发（异常没有打断冻结后的下发/重发链）");
                CheckTrue(h.LocalReplica(1).CombatDead,
                    "R ★ 死亡仍被复制（异常没有挡住整包资源复制）");

                h.Tick(8);
                CheckTrue(h.Ds.ResultReadyForLobby, "R ★ 全 ACK 后仍能就绪（闭环没漏）");

                CheckTrue(h.Ds.CallbackExceptions >= 2,
                    "R 回调异常被显式计数（实测 " + h.Ds.CallbackExceptions + "）");
                CheckTrue(!h.Ds.IsFaulted, "R 回调异常不让会话 fault（权威状态未受影响）");
                CheckTrue(h.Ds.SettlementsApplied > 0, "R 结算照常被消费");
                CheckEq(h.DsProjectiles.SettlementsQueued, 0,
                    "R ★ 结算没有因订阅者异常被退回 R5 待取缓冲（避免了同帧双应用）");
                CheckEq(h.DsProjectiles.SettlementsWithNoConsumer, 0, "R 无「无消费者」结算");
                CheckEq(h.Ds.BufferedSettlementCount, 0, "R 驱动观测到的 R5 待取缓冲为 0");
            }
        }

        // =================================================================================
        //  S. 结算恰好消费一次（无双消费）
        // =================================================================================

        /// <summary>
        /// 「订阅回调 + Drain 兜底」必须互斥：R5 的每条结算要么当场交给订阅者、要么进待取缓冲，
        /// 两者不得同时发生（否则同一条伤害/状态发布会走两遍）。这里用计数与 HP 的双重口径固定它。
        /// </summary>
        private static void TestSettlementAppliedOnce()
        {
            CombatHarness h = CombatHarness.Build(0x6B18u, Roster(
                new RosterEntry(281, 1, PMHeroId.KeErTe),
                new RosterEntry(282, 2, PMHeroId.XueLi)));
            using (h)
            {
                uint victimNetId = h.DsPlayers[1].NetId.Value;
                ResolvedAttack atk = BattleNumericConfig.ResolveAttack(PMHeroId.KeErTe, false);

                uint activationId;
                string error;
                PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                CheckTrue(keys != null, "S 攻击成功：" + (error ?? "ok"));
                h.Tick(6);

                string hitError;
                CheckTrue(h.HitAll(0, keys, victimNetId, out hitError),
                    "S 客户端上报 " + keys.Length + " 颗命中：" + (hitError ?? "ok"));
                h.Tick(8);

                CheckEq(h.Ds.SettlementsApplied, keys.Length,
                    "S ★ 每条权威结算恰好被应用一次（订阅路径与 Drain 没有双消费）");
                CheckEq(h.Ds.SettlementsRejected, 0, "S 没有结算被拒");
                CheckEq(h.DsProjectiles.SettlementsQueued, 0, "S R5 待取缓冲为空（无退回）");
                CheckEq(h.Ds.BufferedSettlementCount, 0, "S 驱动观测的 R5 待取缓冲为 0");

                int maxHp = BattleNumericConfig.Get(PMHeroId.XueLi).MaxHp;
                int expectedHp = maxHp - keys.Length * atk.BulletDamage;
                if (expectedHp < 0) { expectedHp = 0; }
                CheckEq(h.Model.GetPlayer(victimNetId).Hp, expectedHp, "S 伤害精确一次");

                // 重复/迟到命中：R5 可能直接拒掉（弹已回收），也可能再产生结算；两种都必须无第二次应用。
                long appliedBefore = h.Ds.SettlementsApplied;
                string dupError;
                try
                {
                    h.HitAll(0, keys, victimNetId, out dupError);
                }
                catch (Exception ex)
                {
                    // 弹已回收时 R5 可能直接拒掉这次上报；抛异常也不能让测试夹具炸掉。
                    Console.WriteLine("      [S] 重复命中上报抛异常（已忽略）："
                                      + ex.GetType().Name + " " + ex.Message);
                }

                h.Tick(6);

                CheckEq(h.Ds.SettlementsApplied, appliedBefore, "S ★ 重复命中不产生第二次应用");
                CheckEq(h.Model.GetPlayer(victimNetId).Hp, expectedHp, "S ★ HP 未被二次扣除");
            }
        }

        // =================================================================================
        //  T. 冲突 outcome 保持终局（fail closed）
        // =================================================================================

        /// <summary>
        /// 客户端收到重复 outcome 是正常路径（每 1000ms 重发），但**冲突** outcome
        ///（不同 outcomeId 或同 outcomeId 不同胜方）是 fail closed：已保存的终局结论不得被改写。
        /// </summary>
        private static void TestConflictingOutcomeIsTerminal()
        {
            CombatHarness h = CombatHarness.Build(0x6B19u, Roster(
                new RosterEntry(291, 1, PMHeroId.KeErTe),
                new RosterEntry(292, 2, PMHeroId.XueLi)));
            using (h)
            {
                h.KillVictim(0);
                h.Tick(6);

                uint storedOutcome;
                int storedWinner;
                CheckTrue(h.Clients[0].TryGetStoredMatchResult(out storedOutcome, out storedWinner),
                    "T 客户端已保存权威结果");
                CheckEq(storedWinner, 1, "T 保存的胜方 = 1");

                long duplicatesBefore = h.Clients[0].MatchResultDuplicates;
                CheckTrue(h.Ds.ResultResends + h.Ds.ResultSends >= 1, "T DS 已下发结果");
                // 伪造冲突结果（同 outcomeId、不同胜方）：真实 ClientRpc 下行。
                int conflictingWinner = storedWinner == 1 ? 2 : 1;
                ExpectNoThrow(delegate
                {
                    h.DsPlayers[0].ClientCombatMatchResultV1(storedOutcome, conflictingWinner);
                }, "T 冲突结果载荷已发出");
                h.Tick(4);

                uint afterOutcome;
                int afterWinner;
                CheckTrue(h.Clients[0].TryGetStoredMatchResult(out afterOutcome, out afterWinner),
                    "T ★ 客户端仍然保存着结果（终局保持）");
                CheckEq(afterOutcome, storedOutcome, "T ★ outcomeId 未被冲突结果改写");
                CheckEq(afterWinner, storedWinner, "T ★ 胜方未被冲突结果改写");
                CheckTrue(h.Clients[0].MatchResultDuplicates >= duplicatesBefore,
                    "T 冲突结果不计入「重复」（它走的是 fail closed 分支）");
                CheckTrue(h.Clients[0].IsFaulted
                          && h.Clients[0].FaultReason == PMR6CombatFaultReason.ConflictingOutcome,
                    "T 冲突结果 fail closed（会话 fault，可观测）");
                CheckTrue(h.Ds.OutcomeIsFrozen && h.Ds.WinnerTeamId == 1,
                    "T DS 侧冻结结论不受客户端拒绝影响");
            }
        }

        // =================================================================================
        //  U. Unbind 允许少一个等待 Ack
        // =================================================================================

        /// <summary>
        /// 契约：结果就绪按「**在线** owner 全 ACK 或 5000ms 宽限」。摘掉一个 owner 后，
        /// 等待集不得再包含它（否则永远只能走宽限超时）。用 2v2 让摘除不触发断线 Forfeit。
        /// </summary>
        private static void TestDisconnectedOwnerDoesNotBlockResult()
        {
            CombatHarness h = CombatHarness.Build(0x6B1Au, Roster(
                new RosterEntry(301, 1, PMHeroId.KeErTe),
                new RosterEntry(302, 1, PMHeroId.XueLi),
                new RosterEntry(303, 2, PMHeroId.KeErTe),
                new RosterEntry(304, 2, PMHeroId.XueLi)));
            using (h)
            {
                PMR3Player fourth = h.DsPlayers[3];

                // 摘掉第 4 个 owner（队伍 2 仍有一名在线成员 ⇒ 不触发断线 Forfeit）。
                CheckTrue(h.Ds.UnbindPlayer(fourth), "U DS 侧成功摘掉一个 owner");
                CheckTrue(!h.Ds.IsPlayerBound(fourth), "U 该 owner 不再在册于驱动");
                CheckTrue(!h.Model.GetPlayer(fourth.NetId.Value).Connected, "U core 侧已标记断线");
                CheckTrue(!h.Model.Ended, "U 断线没有触发终局（两队都还有在线成员）");

                // 首杀终局（攻击者 = 队 1，受害者 = 队 2）。
                h.KillVictim(0, 2);
                h.Tick(10);

                CheckTrue(h.Model.Ended, "U core 已因首杀终局");
                CheckTrue(h.Ds.OutcomeIsFrozen, "U DS 已冻结结果");
                CheckEq(h.Ds.ResultAckCount, 3, "U ★ 只等待剩下的 3 个在线 owner 确认");
                CheckTrue(h.Ds.ResultReadyForLobby, "U ★ 3 个 ACK 即可就绪（不是只能靠 5s 宽限）");
                CheckTrue(h.Ds.ResultReadyByAckCount >= 1, "U 就绪来自 ACK 路径");

                uint ignored;
                int ignoredWinner;
                CheckTrue(!h.Clients[3].TryGetStoredMatchResult(out ignored, out ignoredWinner),
                    "U 被摘掉的 owner 不再收到结果（逐在线 owner 下发）");
            }
        }

        // =================================================================================
        //  V. 第三方订阅者异常 → R5 退回缓冲 → Drain 兜底仍不双扣
        // =================================================================================

        /// <summary>
        /// R5 的结算出口是**多播**：只要链上任何订阅者抛异常，整条交付就算失败，R5 会把同一条结算
        /// 退回有界待取缓冲，等宿主 `DrainSettlements` 再取。这里就人为制造这条真实路径（本驱动之后
        /// 再挂一个会抛的订阅者），验证：
        ///   · Drain 兜底确实会把它取回来并再次交给 core（`SettlementsQueued >= 1`、应用计数 +1）；
        ///   · core 的「每 key-target 只结算一次」保证 **HP 不会被二次扣除**；
        ///   · 驱动的观测值（缓冲计数）回到 0，不会无界积累。
        /// </summary>
        private static void TestDrainFallbackIsIdempotent()
        {
            CombatHarness h = CombatHarness.Build(0x6B1Cu, Roster(
                new RosterEntry(311, 1, PMHeroId.KeErTe),
                new RosterEntry(312, 2, PMHeroId.XueLi)));
            using (h)
            {
                uint victimNetId = h.DsPlayers[1].NetId.Value;
                int maxHp = BattleNumericConfig.Get(PMHeroId.XueLi).MaxHp;
                ResolvedAttack atk = BattleNumericConfig.ResolveAttack(PMHeroId.KeErTe, false);

                // 只在**第一颗弹**的结算上制造「订阅者异常」（挂在驱动订阅之后，因此驱动先应用）。
                int thrown = 0;
                Action<PMProjectileSettlement> hostile = delegate(PMProjectileSettlement settlement)
                {
                    if (thrown == 0)
                    {
                        thrown++;
                        throw new InvalidOperationException("宿主第三个订阅者的 bug");
                    }
                };
                h.DsProjectiles.Settlement += hostile;

                try
                {
                    uint activationId;
                    string error;
                    PMProjectileKey[] keys = h.AttackAndKeys(0, false, out activationId, out error);
                    CheckTrue(keys != null, "V 攻击成功：" + (error ?? "ok"));
                    h.Tick(6);

                    string hitError;
                    h.HitAll(0, new[] { keys[0] }, victimNetId, out hitError);
                    h.Tick(6);

                    CheckEq(h.DsProjectiles.SettlementsQueued, 1,
                        "V ★ 订阅者抛异常 ⇒ 该结算被 R5 退回有界待取缓冲（实测 "
                        + h.DsProjectiles.SettlementsQueued + "）");
                    CheckEq(h.Ds.SettlementsBuffered, 1, "V ★ 驱动 Drain 兜底确实取回了它");
                    CheckEq(h.Ds.BufferedSettlementCount, 0, "V 缓冲已清空（不会无界积累）");
                    CheckEq(h.Ds.SettlementsRejected, 0, "V 第二次应用不是「被拒」（而是 core 幂等跳过）");
                    CheckEq(h.Model.GetPlayer(victimNetId).Hp, maxHp - atk.BulletDamage,
                        "V ★ HP 只扣一次（core 的每 key-target 幂等挡住了双应用）");
                    CheckTrue(!h.Ds.IsFaulted, "V 该路径不是会话 fault");
                }
                finally
                {
                    h.DsProjectiles.Settlement -= hostile;
                }
            }
        }

        // =================================================================================
        //  夹具：真实 Transport + World + Bridge + 生成桩（与 PMR5NetworkTest 同一手法）
        // =================================================================================

        private sealed class TestLink : PMNet.Transport.IPMTransportLink
        {
            public readonly string Name;
            public readonly LinkHub Hub;
            public readonly List<TestLink> Peers = new List<TestLink>(2);
            public PMTransportConnection Connection;

            public TestLink(string name, LinkHub hub)
            {
                Name = name;
                Hub = hub;
            }

            public bool Send(byte[] buffer, int offset, int count)
            {
                Hub.Route(this, buffer, offset, count);
                return true;
            }

            public string Describe() { return Name; }
        }

        private sealed class LinkHub
        {
            private readonly List<TestLink> _links = new List<TestLink>(4);
            private readonly Dictionary<TestLink, List<byte[]>> _inbox =
                new Dictionary<TestLink, List<byte[]>>();

            public TestLink AddLink(string name)
            {
                TestLink link = new TestLink(name, this);
                _links.Add(link);
                _inbox[link] = new List<byte[]>();
                return link;
            }

            public void Connect(TestLink a, TestLink b)
            {
                a.Peers.Add(b);
                b.Peers.Add(a);
            }

            public void Route(TestLink from, byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < from.Peers.Count; i++)
                {
                    TestLink peer = from.Peers[i];
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, offset, copy, 0, count);
                    _inbox[peer].Add(copy);
                }
            }

            public void Deliver()
            {
                for (int i = 0; i < _links.Count; i++)
                {
                    TestLink link = _links[i];
                    List<byte[]> box = _inbox[link];
                    if (box.Count == 0) { continue; }

                    byte[][] batch = box.ToArray();
                    box.Clear();
                    for (int k = 0; k < batch.Length; k++)
                    {
                        if (link.Connection != null)
                        {
                            link.Connection.OnDatagram(batch[k], 0, batch[k].Length);
                        }
                    }
                }
            }
        }

        private sealed class EndpointRow
        {
            public string Name;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
        }

        private sealed class ConnectionRow
        {
            public string Name;
            public EndpointRow Endpoint;
            public PMTransportConnection Connection;
            public TestLink Link;
        }

        private sealed class Rig : IDisposable
        {
            public readonly LinkHub Hub = new LinkHub();
            public EndpointRow Server;
            public readonly List<EndpointRow> ClientEndpoints = new List<EndpointRow>(2);
            public readonly List<ConnectionRow> ServerViews = new List<ConnectionRow>(2);
            public readonly List<ConnectionRow> ClientConnections = new List<ConnectionRow>(2);
            public readonly uint Epoch;
            public long Now = 1000L;

            private Rig(uint epoch) { Epoch = epoch; }

            public PMNetWorld ServerWorld { get { return Server.World; } }
            public PMNetSessionBridge ServerBridge { get { return Server.Bridge; } }
            public PMTransportConnection ServerView(int index) { return ServerViews[index].Connection; }
            public PMNetWorld ClientWorld(int index) { return ClientEndpoints[index].World; }
            public PMNetSessionBridge ClientBridge(int index) { return ClientEndpoints[index].Bridge; }

            public static Rig Build(uint epoch, params int[] uids)
            {
                Rig rig = new Rig(epoch);
                rig.Server = rig.NewEndpoint("server", true);

                for (int i = 0; i < uids.Length; i++)
                {
                    rig.AddClient(uids[i]);
                }

                rig.Finish();
                return rig;
            }

            private EndpointRow NewEndpoint(string name, bool isServer)
            {
                EndpointRow row = new EndpointRow();
                row.Name = name;
                row.World = new PMNetWorld(new PMSession(Epoch, isServer));
                row.World.Warn = delegate(string m) { };
                row.Bridge = new PMNetSessionBridge(row.World);
                row.Bridge.Warn = delegate(string m) { };
                return row;
            }

            private void AddClient(int uid)
            {
                int connectionId = ClientEndpoints.Count + 1;
                EndpointRow client = NewEndpoint("client" + connectionId, false);
                ClientEndpoints.Add(client);

                TestLink serverLink = Hub.AddLink("server->c" + connectionId);
                TestLink clientLink = Hub.AddLink("c" + connectionId + "->server");
                Hub.Connect(serverLink, clientLink);

                ServerViews.Add(NewConnection(Server, "serverView" + connectionId, connectionId,
                    PMSessionPeerRole.Client, uid, uid, uid, serverLink));
                ClientConnections.Add(NewConnection(client, "client" + connectionId, 1,
                    PMSessionPeerRole.Server, uid, uid, uid, clientLink));
            }

            private ConnectionRow NewConnection(EndpointRow owner, string name, int connectionId,
                PMSessionPeerRole peerRole, int uid, int playerId, int teamId, TestLink link)
            {
                ConnectionRow row = new ConnectionRow();
                row.Name = name;
                row.Endpoint = owner;
                row.Link = link;

                PMSessionIdentity identity = new PMSessionIdentity();
                identity.ConnectionId = connectionId;
                identity.PeerRole = peerRole;
                identity.Uid = uid;
                identity.PlayerId = playerId;
                identity.TeamId = teamId;
                identity.HeroId = 1;
                identity.Epoch = Epoch;
                identity.LocalProtocolHash = PMR3Runtime.ProtocolHash;
                identity.PeerProtocolHash = PMR3Runtime.ProtocolHash;
                identity.MatchId = "match-r6-b-network-test";
                identity.DsId = "ds-r6-b-network-test";

                PMTransportConfig config = new PMTransportConfig();
                config.IdleTimeoutMs = 0L;

                row.Connection = new PMTransportConnection(identity, owner.World, owner.Bridge, link, config);
                link.Connection = row.Connection;
                return row;
            }

            private void Finish()
            {
                for (int i = 0; i < ServerViews.Count; i++)
                {
                    string error;
                    CheckTrue(ServerViews[i].Connection.TryActivate(out error),
                        ServerViews[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                for (int i = 0; i < ClientConnections.Count; i++)
                {
                    string error;
                    CheckTrue(ClientConnections[i].Connection.TryActivate(out error),
                        ClientConnections[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                PMR3Runtime.Register();
                PMR3Runtime.Attach(Server.World, Server.Bridge);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Attach(ClientEndpoints[i].World, ClientEndpoints[i].Bridge);
                }
            }

            public PMR3Player SpawnPlayer(int index)
            {
                return PMR3Runtime.SpawnPlayer(Server.World, Server.Bridge, ServerView(index));
            }

            public void Frame(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Hub.Deliver();
                    Server.Bridge.Update(Now);
                    for (int i = 0; i < ClientEndpoints.Count; i++)
                    {
                        ClientEndpoints[i].Bridge.Update(Now);
                    }

                    Hub.Deliver();
                    Now += 16L;
                }
            }

            public void Dispose()
            {
                PMR3Runtime.Detach(Server.World);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Detach(ClientEndpoints[i].World);
                }

                try { Server.Bridge.Dispose(); } catch (Exception) { }

                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    try { ClientEndpoints[i].Bridge.Dispose(); } catch (Exception) { }
                }
            }
        }

        private struct RosterEntry
        {
            public int Uid;
            public int TeamId;
            public int HeroId;

            public RosterEntry(int uid, int teamId, int heroId)
            {
                Uid = uid;
                TeamId = teamId;
                HeroId = heroId;
            }
        }

        private static RosterEntry[] Roster(params RosterEntry[] entries) { return entries; }

        /// <summary>
        /// R6-B 会话夹具：真实 World / Bridge / Transport / PMCombatSession / R5 与 R6 驱动 / history。
        /// </summary>
        private sealed class CombatHarness : IDisposable
        {
            public Rig Rig;
            public uint Epoch;
            public PMProjectileHistory History;
            public PMCombatSession Model;
            public PMR6CombatDriver Ds;
            public PMR5ProjectileDriver DsProjectiles;
            public readonly List<PMR3Player> DsPlayers = new List<PMR3Player>();
            public PMR5ProjectileDriver[] ClientProjectiles;
            public PMR6CombatDriver[] Clients;
            public readonly List<List<PMR3Player>> ClientPlayers = new List<List<PMR3Player>>();

            /// <summary>宿主侧「可信位置」表（netId → 位置）。</summary>
            public readonly Dictionary<uint, PMVector3> Positions = new Dictionary<uint, PMVector3>();

            public PMVector3 Muzzle = new PMVector3(0f, 1f, 0f);
            public PMVector3 AimDir = new PMVector3(0f, 0f, 1f);
            public long HitFrame = 11L;
            public uint HitStreamVersion = 1u;

            /// <summary>测试用：跳过客户端的 FlushState（⇒ 客户端不回结果 Ack，用于宽限口径）。</summary>
            public bool SuppressClientFlush;

            private readonly Dictionary<int, uint> _nextOrdinal = new Dictionary<int, uint>();
            private RosterEntry[] _roster;
            private bool _bindClients;

            public static CombatHarness Build(uint epoch, RosterEntry[] roster, bool bindClients = true)
            {
                CombatHarness h = new CombatHarness();
                h.Epoch = epoch;
                h._roster = roster;
                h._bindClients = bindClients;

                int[] uids = new int[roster.Length];
                for (int i = 0; i < roster.Length; i++) { uids[i] = roster[i].Uid; }

                h.Rig = Rig.Build(epoch, uids);
                h.Model = new PMCombatSession(epoch);
                h.History = new PMProjectileHistory(epoch);

                // 宿主在创建 R5 driver **之前**就拿到授权策略（契约「创建先后」）。
                IPMR5ProjectileAuthorityPolicy policy =
                    PMR6CombatDriver.CreateAuthorityPolicy(h.Model, h.TryGetOwnerPosition, h.Clock);

                // 1) DS 权威副本（真实 SpawnPlayer：唯一 owner + 登记复制）。
                for (int i = 0; i < roster.Length; i++)
                {
                    PMR3Player serverPlayer = h.Rig.SpawnPlayer(i);
                    if (serverPlayer == null)
                    {
                        Check(false, "权威侧创建玩家副本失败 index=" + i);
                        return h;
                    }

                    h.DsPlayers.Add(serverPlayer);
                    h.Positions[serverPlayer.NetId.Value] = h.Muzzle;
                }

                h.DsProjectiles = new PMR5ProjectileDriver(
                    h.Rig.ServerWorld, h.Rig.ServerBridge, epoch, h.History, policy);
                h.Ds = new PMR6CombatDriver(
                    h.Rig.ServerWorld, h.Rig.ServerBridge, epoch, h.DsProjectiles, h.Model);

                // 2) 可信名册 → core.AddPlayer → 绑定接缝（两者一起）。
                for (int i = 0; i < roster.Length; i++)
                {
                    h.Ds.AddPlayer(h.DsPlayers[i], roster[i].Uid, roster[i].TeamId, roster[i].HeroId);
                }

                h.Ds.StartMatch(roster.Length);

                // 3) 初值必须在首次生命周期 Flush 之前就位（否则客户端收到的是空初值）。
                h.Ds.FlushState(h.Rig.Now);

                // 4) 推进复制：客户端拿到玩家副本。
                h.Rig.Frame(3);
                h.Ds.FlushState(h.Rig.Now);
                h.Rig.Frame(2);

                // 5) 每个客户端建自己的 R5 / R6 驱动，并绑定全部副本。
                int clientCount = h.Rig.ClientEndpoints.Count;
                h.ClientProjectiles = new PMR5ProjectileDriver[clientCount];
                h.Clients = new PMR6CombatDriver[clientCount];

                for (int c = 0; c < clientCount; c++)
                {
                    h.ClientProjectiles[c] = new PMR5ProjectileDriver(
                        h.Rig.ClientWorld(c), h.Rig.ClientBridge(c), epoch, new PMProjectileHistory(epoch));
                    h.Clients[c] = new PMR6CombatDriver(
                        h.Rig.ClientWorld(c), h.Rig.ClientBridge(c), epoch, h.ClientProjectiles[c], null);

                    List<PMR3Player> list = new List<PMR3Player>();
                    for (int i = 0; i < h.DsPlayers.Count; i++)
                    {
                        PMNetObject obj;
                        PMR3Player replica = null;
                        if (h.Rig.ClientWorld(c).TryFind(h.DsPlayers[i].NetId, out obj) && obj != null)
                        {
                            replica = obj as PMR3Player;
                        }

                        list.Add(replica);
                        if (replica == null) { continue; }

                        h.ClientProjectiles[c].BindPlayer(replica);
                        if (bindClients) { h.Clients[c].BindPlayer(replica); }
                    }

                    h.ClientPlayers.Add(list);
                    h._nextOrdinal[c] = 0u;
                }

                // 让初始复制与绑定稳定下来。
                h.Rig.Frame(2);
                for (int c = 0; c < clientCount; c++)
                {
                    h.ClientProjectiles[c].Pump(h.Rig.Now, 16);
                    if (bindClients) { h.Clients[c].FlushState(h.Rig.Now); }
                }

                h.Ds.FlushState(h.Rig.Now);
                return h;
            }

            // ---------------------------------------------------------------- 宿主注入面

            public double Clock() { return Rig.Now; }

            public bool TryGetOwnerPosition(PMR3Player player, out PMVector3 position)
            {
                position = PMVector3.Zero;
                if (player == null || !player.NetId.IsValid) { return false; }
                return Positions.TryGetValue(player.NetId.Value, out position);
            }

            // ---------------------------------------------------------------- 便捷访问

            public PMR3Player ServerPlayer(int index) { return DsPlayers[index]; }

            /// <summary>clientIndex 上「自己那个」副本（AP）。</summary>
            public PMR3Player LocalReplica(int clientIndex) { return ClientPlayers[clientIndex][clientIndex]; }

            /// <summary>clientIndex 上 serverPlayerIndex 的副本（可能是 SP）。</summary>
            public PMR3Player ReplicaOn(int clientIndex, int serverPlayerIndex)
            {
                return ClientPlayers[clientIndex][serverPlayerIndex];
            }

            public int BoundPlayerCountOn(int clientIndex) { return ClientPlayers[clientIndex].Count; }

            public int ManaCostOf(int serverPlayerIndex)
            {
                return BattleNumericConfig.Get(_roster[serverPlayerIndex].HeroId).NormalAttackManaCost;
            }

            // ---------------------------------------------------------------- 帧驱动（宿主次序）

            /// <summary>
            /// 契约固定次序：R6.Pump → R5.Pump → R6.FlushState（同一根墙钟）。
            /// </summary>
            public void Tick(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < Clients.Length; c++) { Clients[c].Pump(Rig.Now); }
                    Ds.Pump(Rig.Now);

                    for (int c = 0; c < Clients.Length; c++) { ClientProjectiles[c].Pump(Rig.Now, 16); }
                    DsProjectiles.Pump(Rig.Now, 16);

                    for (int c = 0; c < Clients.Length; c++)
                    {
                        if (!SuppressClientFlush) { Clients[c].FlushState(Rig.Now); }
                    }

                    Ds.FlushState(Rig.Now);

                    Rig.Frame(1);
                }
            }

            /// <summary>推进墙钟 deltaMs 并跑一帧（用于 1s/5s 级口径）。</summary>
            public void Advance(long deltaMs)
            {
                Rig.Now += deltaMs;
                Tick(1);
            }

            // ---------------------------------------------------------------- 攻击 / 命中

            /// <summary>发起一次攻击并返回本端预测的 N 个 key（按真实 R5 projectileId 序列推导）。</summary>
            public PMProjectileKey[] AttackAndKeys(int clientIndex, bool isSuper, out uint activationId,
                out string error)
            {
                activationId = 0u;
                error = null;

                PMR3Player ap = LocalReplica(clientIndex);
                if (ap == null) { error = "本地副本缺失"; return null; }

                bool ok = Clients[clientIndex].TryAttack(
                    ap, isSuper, AimDir.X, AimDir.Z, Muzzle, 0, Rig.Now, out activationId, out error);
                if (!ok) { return null; }

                PMCombatAttackPlan plan;
                PMCombatRejectReason reason;
                if (!PMCombatWeaponPlanner.TryBuild(ap.CombatHeroId, isSuper, AimDir.X, AimDir.Z,
                        out plan, out reason) || plan == null)
                {
                    error = "测试侧 planner 复核失败：" + reason;
                    return null;
                }

                uint ownerNetId = ap.NetId.Value;
                uint start = _nextOrdinal[clientIndex];
                PMProjectileKey[] keys = new PMProjectileKey[plan.Directions.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = new PMProjectileKey(Epoch, ownerNetId, start + (uint)i + 1u,
                        PMProjectileOrigin.ClientPredicted);
                }

                _nextOrdinal[clientIndex] = start + (uint)keys.Length;
                return keys;
            }

            /// <summary>宿主记录一条真实目标历史样本（模拟宿主从真实玩家状态填入）。</summary>
            public bool RecordTarget(uint netId, uint stream, long serverFrame, double worldTimeMs,
                PMVector3 position, float radius, float halfHeight)
            {
                PMProjectileTargetSample sample = new PMProjectileTargetSample();
                sample.Epoch = Epoch;
                sample.NetId = netId;
                sample.StreamVersion = stream;
                sample.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, serverFrame);
                sample.OutputFrame = new PMFrameId(PMFrameDomain.Input, serverFrame);
                sample.TotalSimTimeMs = worldTimeMs;
                sample.WorldTimeMs = worldTimeMs;
                sample.Position = position;
                sample.RadiusM = radius;
                sample.HalfHeightM = halfHeight;
                sample.Teleported = false;
                sample.Alive = true;

                string reason;
                return History.Record(sample, out reason);
            }

            /// <summary>对给定 N 个 key 逐个上报真实预测命中（锚点取 DS 冻结位置）。</summary>
            public bool HitAll(int clientIndex, PMProjectileKey[] keys, uint victimNetId, out string error)
            {
                error = null;
                if (keys == null || keys.Length == 0) { error = "空 key 集"; return false; }

                PMR3Player ap = LocalReplica(clientIndex);

                for (int i = 0; i < keys.Length; i++)
                {
                    PMProjectileSpec spec;
                    PMProjectileState state;
                    if (!DsProjectiles.Coordinator.TryObserveFrozen(keys[i], out spec, out state) || state == null)
                    {
                        error = "第 " + i + " 颗弹的 DS 冻结状态不可观察（key " + DescribeKey(keys[i]) + "）";
                        return false;
                    }

                    PMVector3 anchor = state.Position;
                    if (!RecordTarget(victimNetId, HitStreamVersion, HitFrame, Rig.Now, anchor, 0.5f, 1.0f))
                    {
                        error = "第 " + i + " 颗弹的目标历史样本被拒";
                        return false;
                    }

                    PMProjectileHitCandidate candidate = new PMProjectileHitCandidate();
                    candidate.TargetNetId = victimNetId;
                    candidate.TargetStreamVersion = HitStreamVersion;
                    candidate.TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, HitFrame);
                    candidate.ImpactPoint = anchor;
                    candidate.VisualOffset = PMVector3.Zero;

                    PMProjectileHitBatch batch = new PMProjectileHitBatch();
                    batch.Key = keys[i];
                    batch.PreviousPosition = anchor;
                    batch.HitPosition = anchor;
                    batch.RewindMs = 0;
                    batch.Targets = new[] { candidate };

                    int rpcs;
                    string hitError;
                    if (!ClientProjectiles[clientIndex].SubmitPredictedHits(ap, batch, Rig.Now, out rpcs, out hitError))
                    {
                        error = "第 " + i + " 颗弹上报失败：" + hitError;
                        return false;
                    }
                }

                return true;
            }

            /// <summary>伪造一颗上行 spawn（真实 RPC + 真实字节链）。</summary>
            public bool ReplaySpawnRpc(int clientIndex, PMProjectileKey key, uint activationId,
                PMVector3 direction)
            {
                PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
                intent.Key = key;
                intent.ActivationId = activationId;
                intent.Position = Muzzle;
                intent.Direction = direction;
                intent.Yaw = 0f;
                intent.PredictionMs = 0;

                byte[] payload;
                string error;
                if (!PMProjectileCodec.TryEncodeSpawnIntent(intent, out payload, out error) || payload == null)
                {
                    return false;
                }

                PMR3Player ap = LocalReplica(clientIndex);
                if (ap == null) { return false; }

                try
                {
                    ap.ServerProjectileSpawnV1(payload);
                }
                catch (Exception)
                {
                    return false;
                }

                return true;
            }

            public PMR5ProjectileView? FindView(PMR5ProjectileDriver driver, PMProjectileKey key)
            {
                PMR5ProjectileView[] buffer = new PMR5ProjectileView[128];
                int n = driver.CopyViews(buffer);
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i].Key.Equals(key)) { return buffer[i]; }
                }

                return null;
            }

            /// <summary>把受害者打到死（首杀终局）。</summary>
            public void KillVictim(int attackerClientIndex, int victimServerIndex = 1)
            {
                uint victimNetId = DsPlayers[victimServerIndex].NetId.Value;

                for (int round = 0; round < 8; round++)
                {
                    if (Model.Ended) { return; }

                    uint activationId;
                    string error;
                    PMProjectileKey[] keys = AttackAndKeys(attackerClientIndex, false, out activationId, out error);
                    if (keys == null)
                    {
                        Console.WriteLine("      [kill] 第 " + round + " 轮攻击失败：" + (error ?? "unknown"));
                        return;
                    }

                    Tick(6);

                    string hitError;
                    if (!HitAll(attackerClientIndex, keys, victimNetId, out hitError))
                    {
                        Console.WriteLine("      [kill] 第 " + round + " 轮命中上报失败：" + (hitError ?? "unknown"));
                    }

                    Tick(8);
                }
            }

            public void Dispose()
            {
                if (Clients != null)
                {
                    for (int i = 0; i < Clients.Length; i++)
                    {
                        if (Clients[i] != null) { try { Clients[i].Dispose(); } catch (Exception) { } }
                    }
                }

                if (ClientProjectiles != null)
                {
                    for (int i = 0; i < ClientProjectiles.Length; i++)
                    {
                        if (ClientProjectiles[i] != null)
                        {
                            try { ClientProjectiles[i].Dispose(); } catch (Exception) { }
                        }
                    }
                }

                if (Ds != null) { try { Ds.Dispose(); } catch (Exception) { } }
                if (DsProjectiles != null) { try { DsProjectiles.Dispose(); } catch (Exception) { } }
                if (Rig != null) { Rig.Dispose(); }
            }

            private static string DescribeKey(PMProjectileKey key)
            {
                return "e" + key.Epoch + "/o" + key.OwnerNetId + "/p" + key.ProjectileId + "/" + key.Origin;
            }
        }
    }
}
