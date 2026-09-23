// ============================================================================
//  PMCombatCoreTest —— R6-A1：planner + session 的真实核心运行测试（net8.0）
// ============================================================================
//  事实来源：
//    Docs/plans/net-r6-combat-contract.md（「A1 纯核心」与 D-R6-01/02/03）
//    Docs/plans/_r6_combat_semantics_survey.md §1.1–1.7（共享数值 / PMBattleSim / 服务端语义）
//    Client/Assets/Scripts/PMCombat/PMCombatContracts.cs（主 Agent 冻结，只读）
//    Client/Assets/Scripts/Shared/BattleNumericConfig.cs（数值单一事实源）
//
//  做法：
//    · 直接编**真实源码**（PMCombat 两份实现 + 冻结 contracts + Shared + PMProjectile 核心），
//      不 mock 记账逻辑、不复制实现；
//    · 期望值来自**独立手算表**：本文件里的 20 英雄数值表是照调查报告 §1.2/§1.3 逐值写下的
//      常量（与 Shared 表同源但**另行誊写**），寿命/回蓝/能量等再用显式算术独立推导，
//      因此「拿实现自身当预期」这类假绿不会发生；
//    · 负向用例是真的边界值（容量 512/513、TTL 5000/5001、方向容差 1 度内外、
//      同批重复目标、倒退时钟、终局后写入），不是「错误字符串包含」这类字符串门。
//
//  覆盖（与验收表 T6A1/T6A2/T6A3 对应）：
//    A. Planner（T6A1）：20 英雄 normal/super 支持矩阵、弹数/伤害/速度/射程/寿命/扇形、
//       单发不旋转、零角同向不平移、抛物线/无大招配置拒绝、非法 hero/aim、
//       间隔下限、damage 0 保留、深 clone
//    B. 名册与开战门（T6A3）：伪 uid/team 拒、hero 越界拒、重复、6 人 2 队上限、
//       team 用真实名册值、StartMatch 未齐/重复
//    C. 普通攻击资源（T6A2）：扣蓝、cooldown、逐拍回蓝、攻击打断回蓝、上限、死/断/终局不回
//    D. 大招（T6A2）：能量不足不扣蓝、满 200 清 0、伤害回能 damage/2 且封顶
//    E. 幂等/水位/时钟/容量/clone（T6A2）：重复不双扣、冲突不推翻、TTL 不刷新、
//       旧 ID 不复活、乱序、满表、TTL 回收后可再用、倒退时钟无副作用、深 clone
//    F. 投射物授权（T6A2）：方向槽逐个占用、容差内外、重复 key 不再占槽、身份/epoch/origin、
//       N 上限、多 owner、未知/被拒 activation、spec 冻结
//    G. 结算（T6A3）：去重、友军/自伤/未知/已死无写、整批结构先验、多目标、clamp、
//       damage 0 不首杀、首杀单次锁、终局后无变化、错误 key/owner/epoch/activation 无写
//    H. 终局与断线（T6A3）：未 start 不早终局、剩余唯一队伍 Forfeit、无唯一队伍 winner 0、
//       winner 不是固定 1、断线保留水位、终局后攻击无写
//    I. 对抗审查（review）：非法请求副作用、回收连带、回显边界、方向容差、巨大时钟
//    J. 契约收口（terminal fix）：重复 key 回显闸门 / Capacity 水位 / StartMatch 连接态 /
//       结算身份（含 AuthorityNetId），每个反例都带前后资源快照
//
//  不覆盖（诚实口径）：
//    · 真网络承载（R6-B）、Unity 宿主（T6C）、完整对局实机（T46/T47 PENDING_USER）；
//    · 本测试不产生任何真实投射物，也不代表「已能联机开枪」。
//
//  运行：dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Combat;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Shared;

namespace PMCombatCoreTest
{
    internal static class Program
    {
        private const uint Epoch = 7u;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static double _now = 1000.0;

        // ================================================================ 手算 oracle 表
        // 20 英雄普通攻击（照 Docs/plans/_r6_combat_semantics_survey.md §1.2 逐值誊写）。
        private static readonly int[] NormalPerShot = { 5, 2, 1, 1, 1, 10, 15, 6, 1, 1, 1, 4, 1, 3, 1, 1, 3, 1, 2, 1 };
        private static readonly int[] NormalDamage = { 80, 340, 650, 400, 816, 45, 90, 448, 1155, 840, 520, 680, 800, 460, 320, 0, 320, 680, 260, 400 };
        private static readonly float[] NormalSpeed = { 11f, 12f, 12f, 11f, 5f, 8f, 16f, 11f, 10f, 5f, 10f, 5f, 10f, 10f, 14f, 10f, 12f, 11f, 14f, 13f };
        private static readonly float[] NormalDistance = { 6f, 8f, 11f, 6f, 5f, 4f, 6f, 7f, 10f, 5f, 6f, 5f, 10f, 7f, 7f, 10f, 7f, 7f, 9f, 10f };
        private static readonly float[] NormalAngle = { 30f, 0f, 0f, 0f, 0f, 40f, 45f, 0f, 0f, 20f, 15f, 20f, 0f, 45f, 10f, 0f, 30f, 10f, 50f, 0f };
        private static readonly float[] NormalShotInterval = { 0.005f, 0.1f, 0f, 0f, 0.01f, 0.01f, 0.1f, 0f, 0f, 0.01f, 0.1f, 0.01f, 0f, 0.05f, 0.05f, 0f, 0.08f, 0.09f, 0.15f, 0.1f };
        private static readonly int[] NormalManaCost = { 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 90, 30, 30, 30, 30, 30, 30, 30 };
        private static readonly bool[] NormalParabola = { false, false, false, false, true, false, false, false, false, true, false, true, false, false, false, false, false, false, false, false };

        // 大招（§1.3）：只有 6 个英雄有子弹型大招配置，其中帕姆大招是抛物线（本批不支持）。
        private static readonly bool[] HasSuper = { true, true, false, false, false, false, false, true, false, false, false, false, true, false, false, false, false, false, true, true };
        private static readonly int[] SuperCount = { 40, 12, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 6, 0, 0, 0, 0, 0, 1, 12 };
        private static readonly int[] SuperDamage = { 80, 340, 0, 0, 0, 0, 0, 448, 0, 0, 0, 0, 60, 0, 0, 0, 0, 0, 300, 400 };
        private static readonly float[] SuperSpeed = { 14f, 18f, 0f, 0f, 0f, 0f, 0f, 14f, 0f, 0f, 0f, 0f, 10f, 0f, 0f, 0f, 0f, 0f, 5f, 19f };
        private static readonly float[] SuperDistance = { 6f, 12f, 0f, 0f, 0f, 0f, 0f, 7f, 0f, 0f, 0f, 0f, 10f, 0f, 0f, 0f, 0f, 0f, 2f, 14f };
        private static readonly float[] SuperAngle = { 40f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };

        private static double Now()
        {
            return _now;
        }

        private static double Advance(double ms)
        {
            _now += ms;
            return _now;
        }

        private static int Main()
        {
            Console.WriteLine("=== R6-A1：PMCombat planner / session 纯核心（独立手算 oracle）===");
            Console.WriteLine();

            Section("A. Planner：20 英雄 normal/super 支持矩阵与数值（T6A1）", TestPlanner);
            Section("B. 名册与开战门（T6A3）", TestRosterAndStart);
            Section("C. 普通攻击资源：扣蓝 / cooldown / 回蓝 / 打断 / 上限（T6A2）", TestNormalResources);
            Section("D. 大招能量与伤害回能（T6A2）", TestSuperResources);
            Section("E. 幂等 / 水位 / 时钟 / 容量 / 深 clone（T6A2）", TestLedger);
            Section("F. 投射物授权：方向槽 / 身份 / 上限（T6A2）", TestAuthorization);
            Section("G. 结算：去重 / 敌我筛选 / 结构先验 / 首杀（T6A3）", TestSettlement);
            Section("H. 终局与断线：Forfeit / winner 0 / 未 start（T6A3）", TestEndgame);
            Section("I. 对抗审查：副作用 / 回收 / 回显 / 容差 / 极端时钟（review）", TestAdversarialReview);
            Section("J. 契约收口：回显闸门 / 容量水位 / 开战门 / 结算身份（terminal fix）", TestTerminalFix);

            Console.WriteLine();
            Console.WriteLine("=== 汇总：通过 " + _passed + " / 失败 " + _failures.Count + " ===");
            if (_failures.Count > 0)
            {
                Console.WriteLine("失败明细：");
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("  [" + (i + 1) + "] " + _failures[i]);
                }

                return 1;
            }

            return 0;
        }

        // ================================================================ A. Planner

        private static void TestPlanner()
        {
            CheckEq(PMCombatLimits.MaxAttacks, 512, "契约常量 MaxAttacks=512");
            CheckEq(PMCombatLimits.MaxBulletsPerAttack, 64, "契约常量 MaxBulletsPerAttack=64");
            CheckEq(PMCombatLimits.MaxAuthorizedProjectiles, 2048, "契约常量 MaxAuthorizedProjectiles=2048");
            CheckEq(PMCombatLimits.AttackRecordTtlMs, 5000, "契约常量 AttackRecordTtlMs=5000");
            CheckEq(PMCombatLimits.MinFireIntervalMs, 100, "契约常量 MinFireIntervalMs=100");
            CheckEq(PMCombatLimits.MaxPlayers, 6, "契约常量 MaxPlayers=6");
            CheckEq(PMCombatLimits.MaxTeams, 2, "契约常量 MaxTeams=2");
            CheckEq(BattleNumericConfig.ManaMax, 90, "共享数值 ManaMax=90");
            CheckEq(BattleNumericConfig.ManaPerSegment, 30, "共享数值 ManaPerSegment=30");
            CheckEq(BattleNumericConfig.SuperEnergyMax, 200, "共享数值 SuperEnergyMax=200");

            int maxLifetime = 0;
            int superSupported = 0;

            for (int hero = 0; hero < PMHeroId.Count; hero++)
            {
                // ---- 普通攻击 ----
                PMCombatAttackPlan plan;
                PMCombatRejectReason reason;
                bool ok = PMCombatWeaponPlanner.TryBuild(hero, false, 1f, 0f, out plan, out reason);
                if (NormalParabola[hero])
                {
                    Check(!ok && reason == PMCombatRejectReason.UnsupportedAttack,
                        "hero " + hero + " 普通攻击是抛物线 → UnsupportedAttack（实际 ok=" + ok + " reason=" + reason + "）");
                }
                else
                {
                    Check(ok, "hero " + hero + " 普通攻击计划应成功（reason=" + reason + "）");
                    if (ok)
                    {
                        CheckEq(plan.Directions.Length, NormalPerShot[hero], "hero " + hero + " 普通弹数 = PerShot");
                        CheckEq(plan.Damage, NormalDamage[hero], "hero " + hero + " 普通伤害");
                        CheckEq(plan.Spec.SpeedMps, NormalSpeed[hero], "hero " + hero + " 普通弹速");
                        CheckEq(plan.Spec.LifetimeMs, ExpectedLifetime(NormalDistance[hero], NormalSpeed[hero]),
                            "hero " + hero + " 普通寿命 = ceil(dist/speed*1000)");
                        CheckEq(plan.ManaCost, NormalManaCost[hero], "hero " + hero + " 普通蓝耗");
                        CheckEq(plan.FireIntervalMs, ExpectedInterval(NormalShotInterval[hero]),
                            "hero " + hero + " 普通最小间隔 = max(100, ceil(EachShotInterval*1000))");
                        CheckEq(plan.HeroId, hero, "hero " + hero + " 计划 heroId");
                        Check(!plan.IsSuper, "hero " + hero + " 普通计划 IsSuper=false");
                        CheckCommonSpec(plan, "hero " + hero + " 普通");
                        CheckDirections(plan, NormalAngle[hero], "hero " + hero + " 普通");
                        if (plan.Spec.LifetimeMs > maxLifetime)
                        {
                            maxLifetime = plan.Spec.LifetimeMs;
                        }
                    }
                }

                // ---- 大招 ----
                bool superOk = PMCombatWeaponPlanner.TryBuild(hero, true, 1f, 0f, out plan, out reason);
                bool superSupportedHero = HasSuper[hero] && SuperCount[hero] > 0
                    && ExpectedLifetime(SuperDistance[hero], SuperSpeed[hero]) <= PMCombatWeaponPlanner.MaxPlanLifetimeMs
                    && hero != 18; // 帕姆大招是抛物线（IsParabola=true）→ 本批不支持
                if (!superSupportedHero)
                {
                    Check(!superOk && reason == PMCombatRejectReason.UnsupportedAttack,
                        "hero " + hero + " 大招不支持 → UnsupportedAttack（实际 ok=" + superOk + " reason=" + reason + "）");
                }
                else
                {
                    superSupported++;
                    Check(superOk, "hero " + hero + " 大招计划应成功（reason=" + reason + "）");
                    if (superOk)
                    {
                        CheckEq(plan.Directions.Length, SuperCount[hero], "hero " + hero + " 大招弹数 = Total");
                        CheckEq(plan.Damage, SuperDamage[hero], "hero " + hero + " 大招伤害");
                        CheckEq(plan.Spec.SpeedMps, SuperSpeed[hero], "hero " + hero + " 大招弹速");
                        CheckEq(plan.Spec.LifetimeMs, ExpectedLifetime(SuperDistance[hero], SuperSpeed[hero]),
                            "hero " + hero + " 大招寿命");
                        CheckEq(plan.ManaCost, 0, "hero " + hero + " 大招不扣蓝（ManaCost=0）");
                        Check(plan.IsSuper, "hero " + hero + " 大招计划 IsSuper=true");
                        CheckCommonSpec(plan, "hero " + hero + " 大招");
                        CheckDirections(plan, SuperAngle[hero], "hero " + hero + " 大招");
                        if (plan.Spec.LifetimeMs > maxLifetime)
                        {
                            maxLifetime = plan.Spec.LifetimeMs;
                        }
                    }
                }
            }

            CheckEq(superSupported, 5, "本批支持的大招英雄数（雪莉/柯尔特/格尔/贝亚/瑞科）");
            Check(maxLifetime <= PMCombatWeaponPlanner.MaxPlanLifetimeMs,
                "全部计划寿命上界 <= 2500ms（实际最大 " + maxLifetime + "ms）");
            Check(maxLifetime >= 1 && maxLifetime <= 1000,
                "冻结表实际最大寿命 <= 1000ms（实际 " + maxLifetime + "ms，R5 pending 2000ms + 墓碑仍在记录 TTL 5000ms 内）");
            Console.WriteLine("    计划寿命实际上界 = " + maxLifetime + "ms（预算 2500ms / 记录 TTL 5000ms）");

            // ---- 合法性：hero / aim ----
            PMCombatAttackPlan p;
            PMCombatRejectReason r;
            Check(!PMCombatWeaponPlanner.TryBuild(-1, false, 1f, 0f, out p, out r) && r == PMCombatRejectReason.InvalidHero, "hero=-1 → InvalidHero");
            Check(!PMCombatWeaponPlanner.TryBuild(PMHeroId.Count, false, 1f, 0f, out p, out r) && r == PMCombatRejectReason.InvalidHero, "hero=20 → InvalidHero");
            Check(!PMCombatWeaponPlanner.TryBuild(999, false, 1f, 0f, out p, out r) && r == PMCombatRejectReason.InvalidHero, "hero=999 → InvalidHero");
            Check(!PMCombatWeaponPlanner.TryBuild(0, false, 0f, 0f, out p, out r) && r == PMCombatRejectReason.InvalidAim, "aim 零向量 → InvalidAim");
            Check(!PMCombatWeaponPlanner.TryBuild(0, false, float.NaN, 0f, out p, out r) && r == PMCombatRejectReason.InvalidAim, "aim NaN → InvalidAim");
            Check(!PMCombatWeaponPlanner.TryBuild(0, false, 0f, float.PositiveInfinity, out p, out r) && r == PMCombatRejectReason.InvalidAim, "aim +Inf → InvalidAim");
            Check(!PMCombatWeaponPlanner.TryBuild(0, false, 1e-9f, -1e-9f, out p, out r) && r == PMCombatRejectReason.InvalidAim, "aim 退化小量 → InvalidAim");

            // ---- 无大招配置 vs 有配置 ----
            Check(!PMCombatWeaponPlanner.TryBuild(PMHeroId.PeiPei, true, 1f, 0f, out p, out r) && r == PMCombatRejectReason.UnsupportedAttack,
                "佩佩（TryGetSuper=false）大招 → UnsupportedAttack，不降级成普通");
            Check(!PMCombatWeaponPlanner.TryBuild(PMHeroId.PaMu, true, 1f, 0f, out p, out r) && r == PMCombatRejectReason.UnsupportedAttack,
                "帕姆大招（抛物线配置）→ UnsupportedAttack");

            // ---- 单发不旋转 / 扇形首尾 / 零角同向 ----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.MaiKeSi, false, 1f, 2f, out p, out r), "麦克斯普通（angle=10，弹数 1）应成功");
            CheckSameDirection(p.Directions[0], 1f, 2f, "麦克斯单发不被 -angle/2 白白转掉");
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.ABo, false, -2f, 1f, out p, out r), "阿渤普通（angle=15，弹数 1）应成功");
            CheckSameDirection(p.Directions[0], -2f, 1f, "阿渤单发方向 = 基准方向");

            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.XueLi, false, 1f, 0f, out p, out r), "雪莉普通应成功");
            CheckEq(p.Directions.Length, 5, "雪莉普通 5 弹");
            CheckEq(AngleDeg(p.Directions[0], 1f, 0f), -15.0, "雪莉扇形首 = -15 度");
            CheckEq(AngleDeg(p.Directions[4], 1f, 0f), 15.0, "雪莉扇形尾 = +15 度");
            CheckEq(AngleDeg(p.Directions[2], 1f, 0f), 0.0, "雪莉扇形中 = 0 度");
            CheckEq(AngleDeg(p.Directions[1], 1f, 0f), -7.5, "雪莉扇形第 2 发 = -7.5 度");
            CheckEq(AngleDeg(p.Directions[3], 1f, 0f), 7.5, "雪莉扇形第 4 发 = +7.5 度");

            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.KeErTe, false, 1f, 0f, out p, out r), "柯尔特普通（angle=0，弹数 2）应成功");
            Check(IsSameAsBase(p.Directions[0], 1f, 0f) && IsSameAsBase(p.Directions[1], 1f, 0f),
                "柯尔特零角多弹 = 全部与基准方向相同（服务端既有语义：重叠同向，不做客户端平移排布）");

            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.GeEr, false, 0f, 1f, out p, out r), "格尔普通（angle=0，弹数 6）应成功");
            bool allSame = true;
            for (int i = 0; i < p.Directions.Length; i++)
            {
                if (!IsSameAsBase(p.Directions[i], 0f, 1f))
                {
                    allSame = false;
                }
            }

            Check(allSame, "格尔 6 弹全部与基准同向（不平移）");

            // ---- 归一化：aim(3,4) → (0.6,0.8) ----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.PeiPei, false, 3f, 4f, out p, out r), "佩佩 aim(3,4) 应成功");
            CheckEq(p.Directions[0].X, 0.6f, "aim(3,4) → 方向 X=0.6");
            CheckEq(p.Directions[0].Z, 0.8f, "aim(3,4) → 方向 Z=0.8");

            // ---- damage 0 保留（斯派克）----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.SiPaiKe, false, 1f, 0f, out p, out r), "斯派克普通应成功");
            CheckEq(p.Damage, 0, "斯派克 damage 0 保留（不擅自填值）");

            // ---- 间隔下限实证 ----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.XueLi, false, 1f, 0f, out p, out r), "雪莉普通");
            CheckEq(p.FireIntervalMs, 100, "雪莉 EachShotInterval=0.005s → 下限 100ms");
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.PaMu, false, 1f, 0f, out p, out r), "帕姆普通");
            CheckEq(p.FireIntervalMs, 150, "帕姆 EachShotInterval=0.15s → 150ms（不因 float32 变成 151）");
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.KeErTe, false, 1f, 0f, out p, out r), "柯尔特普通");
            CheckEq(p.FireIntervalMs, 100, "柯尔特 EachShotInterval=0.1s → 100ms（不因 float32 变成 101）");

            // ---- 深 clone：改返回的计划不影响后续 ----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.XueLi, false, 1f, 0f, out p, out r), "雪莉普通");
            PMCombatAttackPlan clone = p.Clone();
            clone.Directions[0] = new PMVector3(0f, 0f, 1f);
            clone.Spec.SpeedMps = 999f;
            clone.Damage = 12345;
            CheckEq(p.Directions[0].X, 0.9659f, "Clone 后改方向不影响原计划（容差内比较）");
            CheckEq(p.Spec.SpeedMps, 11f, "Clone 后改 spec 不影响原计划");
            CheckEq(p.Damage, 80, "Clone 后改伤害不影响原计划");

            // ---- 显式寿命字面值（与共享表独立核对）----
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.BuLuoKe, false, 1f, 0f, out p, out r), "布洛克普通");
            CheckEq(p.Spec.LifetimeMs, 1000, "布洛克 ceil(10/10*1000) = 1000");
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.PeiPei, false, 1f, 0f, out p, out r), "佩佩普通");
            CheckEq(p.Spec.LifetimeMs, 917, "佩佩 ceil(11/12*1000) = 917");
            Check(PMCombatWeaponPlanner.TryBuild(PMHeroId.RuiKe, true, 1f, 0f, out p, out r), "瑞科大招");
            CheckEq(p.Spec.LifetimeMs, 737, "瑞科大招 ceil(14/19*1000) = 737");
        }

        private static void CheckCommonSpec(PMCombatAttackPlan plan, string what)
        {
            Check(plan.Spec != null, what + "：Spec 非空");
            if (plan.Spec == null)
            {
                return;
            }

            CheckEq(plan.Spec.RadiusM, PMCombatLimits.ProjectileRadiusM, what + "：半径 = 0.1m（不复用 ShootWidth）");
            Check(plan.Spec.StopOnHit, what + "：StopOnHit=true");
            Check(plan.Spec.LifetimeMs >= 1 && plan.Spec.LifetimeMs <= PMCombatWeaponPlanner.MaxPlanLifetimeMs,
                what + "：寿命在 [1,2500]（实际 " + plan.Spec.LifetimeMs + "）");
            Check(plan.Spec.DelayDestroyMs == 0, what + "：无额外销毁延迟");
            Check(!plan.Spec.SkipFlyingTrajectoryValidation, what + "：不跳过飞行轨迹校验");
        }

        private static void CheckDirections(PMCombatAttackPlan plan, float angleDeg, string what)
        {
            int count = plan.Directions.Length;
            for (int i = 0; i < count; i++)
            {
                float length = (float)Math.Sqrt((double)plan.Directions[i].X * plan.Directions[i].X
                    + (double)plan.Directions[i].Z * plan.Directions[i].Z);
                Check(Math.Abs(length - 1f) <= 1e-4f, what + "：方向 " + i + " 已单位化（len=" + length + "）");
                Check(plan.Directions[i].Y == 0f, what + "：方向 " + i + " 为世界 XZ 平面（Y=0）");

                if (count <= 1 || angleDeg == 0f)
                {
                    Check(IsSameAsBase(plan.Directions[i], 1f, 0f, 1e-6f),
                        what + "：数量<=1 或零角时方向 " + i + " 原样等于基准方向");
                }
                else
                {
                    float expected = -angleDeg / 2f + (angleDeg / (count - 1)) * i;
                    CheckEq(AngleDeg(plan.Directions[i], 1f, 0f), expected,
                        what + "：方向 " + i + " 期望扇形角 " + expected + " 度（容差 0.05）");
                }
            }
        }

        private static int ExpectedLifetime(float distance, float speed)
        {
            return (int)Math.Ceiling((double)distance / (double)speed * 1000.0);
        }

        private static int ExpectedInterval(float seconds)
        {
            // 与 planner 同口径：先抹平 float32 表示误差再取 ceil（0.15f = 0.150000006 → 150ms）
            int ms = (int)Math.Ceiling(Math.Round((double)seconds * 1000.0, 4));
            return ms < PMCombatLimits.MinFireIntervalMs ? PMCombatLimits.MinFireIntervalMs : ms;
        }

        // ================================================================ B. 名册 / 开战门

        private static void TestRosterAndStart()
        {
            PMCombatSession s = new PMCombatSession(Epoch);
            Check(!s.AddPlayer(0u, 1, 1, 0), "netId=0 拒（不得用伪身份）");
            Check(!s.AddPlayer(101u, 0, 1, 0), "uid=0 拒");
            Check(!s.AddPlayer(101u, -3, 1, 0), "uid<0 拒");
            Check(!s.AddPlayer(101u, 1, 0, 0), "team=0 拒");
            Check(!s.AddPlayer(101u, 1, -1, 0), "team<0 拒");
            Check(!s.AddPlayer(101u, 1, 1, -1), "hero=-1 拒");
            Check(!s.AddPlayer(101u, 1, 1, PMHeroId.Count), "hero=20 拒");
            CheckEq(s.PlayerCount, 0, "以上非法登记全部无副作用");

            // team 不限制必须 1/2：只要合法正数
            Check(s.AddPlayer(101u, 1, 5, 0), "team=5 合法（真实名册 TeamId）");
            Check(s.AddPlayer(102u, 2, 9, 1), "team=9 合法");
            Check(!s.AddPlayer(103u, 3, 4, 2), "第 3 支队伍 → 拒（MaxTeams=2）");
            Check(!s.AddPlayer(101u, 4, 5, 2), "重复 netId → 拒");
            Check(!s.AddPlayer(103u, 1, 5, 2), "重复 uid → 拒");
            CheckEq(s.PlayerCount, 2, "以上非法登记全部无副作用");

            PMCombatSession full = new PMCombatSession(Epoch);
            for (int i = 0; i < PMCombatLimits.MaxPlayers; i++)
            {
                Check(full.AddPlayer((uint)(200 + i), 100 + i, i % 2 == 0 ? 1 : 2, i), "名册第 " + (i + 1) + " 人登记成功");
            }

            Check(!full.AddPlayer(300u, 300, 1, 0), "第 7 人 → 拒（MaxPlayers=6）");

            // StartMatch 门
            Check(!full.StartMatch(5), "StartMatch(5) 与 6 人名册不符 → 拒");
            Check(!full.StartMatch(7), "StartMatch(7) 超上限 → 拒");
            Check(!full.StartMatch(0), "StartMatch(0) → 拒");
            Check(!full.Started, "未齐时仍处于未开战");
            Check(full.StartMatch(6), "StartMatch(6) 全名册接入 → 开战");
            Check(full.Started, "开战后 Started=true");
            Check(!full.StartMatch(6), "重复 StartMatch → 拒（幂等不重开）");
            Check(!full.AddPlayer(301u, 301, 1, 0), "开战后不得再登记");

            // 未开战时的攻击：NotStarted 且无副作用
            PMCombatSession notStarted = new PMCombatSession(Epoch);
            Check(notStarted.AddPlayer(101u, 1, 1, 0), "登记 101");
            Check(notStarted.AddPlayer(104u, 4, 2, 0), "登记 104");
            PMCombatAttackDecision d = notStarted.RequestAttack(101u, 1u, false, 1f, 0f, Advance(1.0));
            Check(!d.Accepted && d.Reason == PMCombatRejectReason.NotStarted, "未 StartMatch → NotStarted");
            CheckEq(notStarted.GetPlayer(101u).Mana, 90, "未开战不扣蓝");
            CheckEq(notStarted.AttackRecordCount, 0, "未开战不记账（不给开战前上行塞满账本的机会）");
            Check(!notStarted.StartMatch(3), "名册 2 人时 StartMatch(3) → 拒");
        }

        // ================================================================ C. 普通攻击资源

        private static void TestNormalResources()
        {
            PMCombatSession s = NewStartedStandardSession();
            double t = Advance(1.0);

            CheckEq(s.GetPlayer(101u).Mana, 90, "初始蓝 = 90（共享 ManaMax）");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 0, "初始能量 = 0");
            CheckEq(s.GetPlayer(101u).Hp, 960, "雪莉初始 HP = 960（共享 MaxHp）");
            CheckEq(s.GetPlayer(104u).Hp, 900, "贝亚初始 HP = 900");

            PMCombatAttackDecision d1 = s.RequestAttack(101u, 1u, false, 1f, 0f, t);
            Check(d1.Accepted, "普通攻击应被批准（reason=" + d1.Reason + "）");
            CheckEq(d1.Reason, PMCombatRejectReason.None, "批准时 reason=None");
            Check(!d1.Duplicate, "首次请求 Duplicate=false");
            Check(d1.Plan != null && d1.Plan.Directions.Length == 5, "批准返回计划（雪莉 5 弹）");
            CheckEq(s.GetPlayer(101u).Mana, 60, "扣 30 蓝 → 60");

            PMCombatAttackDecision d2 = s.RequestAttack(101u, 2u, false, 1f, 0f, t);
            Check(!d2.Accepted && d2.Reason == PMCombatRejectReason.Cooldown, "同一时刻再次攻击 → Cooldown（间隔下限 100ms）");
            CheckEq(s.GetPlayer(101u).Mana, 60, "被拒后蓝不变");

            t = Advance(100.0);
            PMCombatAttackDecision d3 = s.RequestAttack(101u, 3u, false, 1f, 0f, t);
            Check(d3.Accepted, "间隔 100ms 后可再次攻击");
            CheckEq(s.GetPlayer(101u).Mana, 30, "再扣 30 → 30");

            t = Advance(100.0);
            PMCombatAttackDecision d4 = s.RequestAttack(101u, 4u, false, 1f, 0f, t);
            Check(d4.Accepted, "第三次攻击批准");
            CheckEq(s.GetPlayer(101u).Mana, 0, "三次攻击后蓝 = 0");

            t = Advance(100.0);
            PMCombatAttackDecision d5 = s.RequestAttack(101u, 5u, false, 1f, 0f, t);
            Check(!d5.Accepted && d5.Reason == PMCombatRejectReason.InsufficientMana, "蓝不足 → InsufficientMana");
            CheckEq(s.GetPlayer(101u).Mana, 0, "蓝不足不扣蓝");

            // 逐拍回蓝：雪莉 ReloadSeconds=1 → 每 1000ms 回 30
            t = Advance(999.0);
            Check(s.Tick(t), "Tick 接受墙钟");
            CheckEq(s.GetPlayer(101u).Mana, 0, "999ms 未到一拍 → 蓝仍为 0");
            t = Advance(1.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 30, "1000ms 到一拍 → 回 30");
            t = Advance(1000.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 60, "2000ms → 回 60");
            t = Advance(1000.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 90, "3000ms → 回满 90");
            t = Advance(5000.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 90, "满蓝后再 Tick 不溢出");

            // 攻打断回蓝：先打掉蓝，再推进到半拍，再攻击 → 计时重置
            t = Advance(100.0);
            s.RequestAttack(101u, 6u, false, 1f, 0f, t);
            CheckEq(s.GetPlayer(101u).Mana, 60, "第 4 次攻击 → 60");
            t = Advance(100.0);
            s.RequestAttack(101u, 7u, false, 1f, 0f, t);
            CheckEq(s.GetPlayer(101u).Mana, 30, "第 5 次攻击 → 30");
            t = Advance(500.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 30, "半拍之内不回蓝");
            t = Advance(100.0);
            s.RequestAttack(101u, 8u, false, 1f, 0f, t);
            CheckEq(s.GetPlayer(101u).Mana, 0, "第 6 次攻击 → 0（并打断回蓝进度）");
            t = Advance(999.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 0, "攻击已打断回蓝：999ms 仍为 0（若未重置则应为 30）");
            t = Advance(1.0);
            s.Tick(t);
            CheckEq(s.GetPlayer(101u).Mana, 30, "重置后满一拍 → 30");

            // 贝亚：ReloadSeconds=9、ManaCost=90
            PMCombatSession slow = NewStartedStandardSession();
            double ts = Advance(1.0);
            PMCombatAttackDecision b1 = slow.RequestAttack(104u, 1u, false, 1f, 0f, ts);
            Check(b1.Accepted, "贝亚普通攻击批准（蓝耗 90）");
            CheckEq(b1.Plan.ManaCost, 90, "贝亚蓝耗 = 90（共享表）");
            CheckEq(slow.GetPlayer(104u).Mana, 0, "贝亚一次攻击吃满整条蓝");
            ts = Advance(8999.0);
            slow.Tick(ts);
            CheckEq(slow.GetPlayer(104u).Mana, 0, "贝亚 8999ms 未到 9s → 0");
            ts = Advance(1.0);
            slow.Tick(ts);
            CheckEq(slow.GetPlayer(104u).Mana, 30, "贝亚 9000ms → 30");
            ts = Advance(9000.0);
            slow.Tick(ts);
            CheckEq(slow.GetPlayer(104u).Mana, 60, "贝亚 18000ms → 60");
        }

        // ================================================================ D. 大招

        private static void TestSuperResources()
        {
            PMCombatSession s = NewStartedStandardSession();
            double t = Advance(1.0);

            PMCombatAttackDecision super1 = s.RequestAttack(104u, 1u, true, 1f, 0f, t);
            Check(!super1.Accepted && super1.Reason == PMCombatRejectReason.InsufficientEnergy, "能量 0 → InsufficientEnergy");
            CheckEq(s.GetPlayer(104u).Mana, 90, "大招能量不足不扣蓝");
            CheckEq(s.GetPlayer(104u).SuperEnergy, 0, "能量不足不变");

            PMCombatAttackDecision normal1 = s.RequestAttack(104u, 2u, false, 1f, 0f, t);
            Check(normal1.Accepted, "贝亚普通攻击批准");
            CheckEq(s.GetPlayer(104u).Mana, 0, "贝亚扣蓝 90 → 0");

            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            Check(TryAuthorize(s, 104u, 2u, 1u, normal1.Plan.Directions[0].X, normal1.Plan.Directions[0].Z, out spec, out reason),
                "贝亚普通弹授权成功（reason=" + reason + "）");
            PMProjectileKey key = MakeKey(104u, 2u, 1u);

            Check(s.ApplySettlement(MakeSettlement(key, 2u, 101u, 102u), t), "结算命中两名敌队目标");
            CheckEq(s.GetPlayer(101u).Hp, 160, "101：960-800 = 160");
            CheckEq(s.GetPlayer(102u).Hp, 380, "102：1180-800 = 380");
            CheckEq(s.GetPlayer(104u).SuperEnergy, 200, "回能 damage/2=400 且封顶 200（两次命中仍为 200）");

            PMCombatAttackDecision super2 = s.RequestAttack(104u, 3u, true, 1f, 0f, Advance(100.0));
            Check(super2.Accepted, "能量满 → 大招批准（reason=" + super2.Reason + "）");
            CheckEq(s.GetPlayer(104u).SuperEnergy, 0, "大招把能量清 0");
            CheckEq(s.GetPlayer(104u).Mana, 0, "大招不改蓝量");
            CheckEq(super2.Plan.Directions.Length, 6, "贝亚大招弹数 = Total 6");
            CheckEq(super2.Plan.Damage, 60, "贝亚大招伤害 = 60（覆盖普通 800）");
            CheckEq(super2.Plan.ManaCost, 0, "大招不扣蓝");

            PMCombatAttackDecision super3 = s.RequestAttack(104u, 4u, true, 1f, 0f, Advance(100.0));
            Check(!super3.Accepted && super3.Reason == PMCombatRejectReason.InsufficientEnergy, "清 0 后再次大招 → InsufficientEnergy");

            // 能量只能靠有效伤害获取：damage/2 向下取整
            PMCombatSession half = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(half.StartMatch(2), "1v1 开战");
            double t2 = Advance(1.0);
            PMCombatAttackDecision d = half.RequestAttack(104u, 1u, false, 1f, 0f, t2);
            Check(d.Accepted, "布洛克普通批准（伤害 1155）");
            Check(half.TryAuthorizeProjectile(104u, MakeIntent(104u, 1u, 1u, d.Plan.Directions[0].X, d.Plan.Directions[0].Z), t2, out spec, out reason),
                "布洛克弹授权成功");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t2), "布洛克命中雪莉");
            CheckEq(half.GetPlayer(101u).Hp, 0, "960-1155 → clamp 0");
            CheckEq(half.GetPlayer(104u).SuperEnergy, 200, "1155/2 = 577 → 封顶 200");
            CheckEq(half.Outcome.WinnerTeamId, 2, "首杀胜方 = 击杀者队伍（team 2，不是固定 1）");
        }

        // ================================================================ E. 幂等 / 水位 / 时钟 / 容量

        private static void TestLedger()
        {
            // ---- 幂等：重复不双扣、不推翻已批准 ----
            PMCombatSession s = NewStartedStandardSession();
            double t = Advance(1.0);
            PMCombatAttackDecision first = s.RequestAttack(101u, 10u, false, 1f, 0f, t);
            Check(first.Accepted, "首次攻击批准");
            CheckEq(s.GetPlayer(101u).Mana, 60, "扣蓝一次 → 60");

            PMCombatAttackDecision again = s.RequestAttack(101u, 10u, false, 1f, 0f, t);
            Check(again.Duplicate, "同 ID 重复 → Duplicate=true");
            Check(again.Accepted, "重复仍返回原结论 Accepted=true");
            CheckEq(again.Reason, PMCombatRejectReason.None, "重复 reason 与原结论一致");
            Check(again.Plan != null && again.Plan.Directions.Length == 5, "重复返回原计划");
            CheckEq(s.GetPlayer(101u).Mana, 60, "重复不双扣蓝");
            CheckEq(s.IdConflictCount, 0, "同内容重复不算冲突");

            PMCombatAttackDecision conflict = s.RequestAttack(101u, 10u, true, -1f, 0f, t);
            Check(conflict.Duplicate, "同 ID 内容冲突仍返回原结论（Duplicate=true）");
            Check(conflict.Accepted && !conflict.Plan.IsSuper, "冲突不得把已批准的普通攻击翻成大招");
            CheckEq(s.IdConflictCount, 1, "冲突计数 +1");
            CheckEq(s.GetPlayer(101u).Mana, 60, "冲突不双扣蓝");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 0, "冲突不改能量");

            // ---- 水位：旧 ID 不复活 ----
            PMCombatSession stale = NewStartedStandardSession();
            double ts = Advance(1.0);
            Check(stale.RequestAttack(101u, 20u, false, 1f, 0f, ts).Accepted, "ID 20 批准");
            CheckEq(stale.GetPlayer(101u).Mana, 60, "扣蓝 → 60");
            PMCombatAttackDecision old1 = stale.RequestAttack(101u, 5u, false, 1f, 0f, ts);
            Check(!old1.Accepted && old1.Reason == PMCombatRejectReason.StaleId, "乱序旧 ID（5 < 水位 20）→ StaleId");
            CheckEq(stale.GetPlayer(101u).Mana, 60, "StaleId 不扣蓝");
            PMCombatAttackDecision next = stale.RequestAttack(101u, 21u, false, 1f, 0f, Advance(100.0));
            Check(next.Accepted, "高于水位的 ID 仍可批准");
            CheckEq(stale.GetPlayer(101u).Mana, 30, "ID 21 扣蓝 → 30");

            // ---- TTL：重复不刷新、过期后授权被拒 ----
            PMCombatSession ttl = NewStartedStandardSession();
            double t0 = Now();
            PMCombatAttackDecision att = ttl.RequestAttack(101u, 30u, false, 1f, 0f, t0);
            Check(att.Accepted, "TTL 用例：攻击批准");
            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            double tDup = Advance(4900.0);
            PMCombatAttackDecision dup = ttl.RequestAttack(101u, 30u, false, 1f, 0f, tDup);
            Check(dup.Duplicate && dup.Accepted, "TTL 内重复返回原结论（不刷新记录时刻）");
            double tExpire = Advance(101.0);
            bool authorized = ttl.TryAuthorizeProjectile(101u,
                MakeIntent(101u, 30u, 1u, att.Plan.Directions[0].X, att.Plan.Directions[0].Z), tExpire, out spec, out reason);
            Check(!authorized && reason == PMCombatRejectReason.Expired,
                "记录 TTL(5000ms) 后授权 → Expired（若重复刷新了 TTL 这里会成功）");

            // ---- 时钟：NaN / 负 / 倒退一律无副作用 ----
            PMCombatSession clk = NewStartedStandardSession();
            double tc = Advance(1000.0);
            Check(clk.RequestAttack(101u, 1u, false, 1f, 0f, tc).Accepted, "时钟用例：ID 1 批准");
            CheckEq(clk.GetPlayer(101u).Mana, 60, "扣蓝 → 60");

            PMCombatAttackDecision nan = clk.RequestAttack(101u, 2u, false, 1f, 0f, double.NaN);
            Check(!nan.Accepted && nan.Reason == PMCombatRejectReason.InvalidClock, "NaN 墙钟 → InvalidClock");
            PMCombatAttackDecision neg = clk.RequestAttack(101u, 2u, false, 1f, 0f, -1.0);
            Check(!neg.Accepted && neg.Reason == PMCombatRejectReason.InvalidClock, "负墙钟 → InvalidClock");
            PMCombatAttackDecision inf = clk.RequestAttack(101u, 2u, false, 1f, 0f, double.PositiveInfinity);
            Check(!inf.Accepted && inf.Reason == PMCombatRejectReason.InvalidClock, "+Inf 墙钟 → InvalidClock");
            PMCombatAttackDecision back = clk.RequestAttack(101u, 2u, false, 1f, 0f, tc - 1.0);
            Check(!back.Accepted && back.Reason == PMCombatRejectReason.InvalidClock, "倒退墙钟 → InvalidClock");
            CheckEq(clk.GetPlayer(101u).Mana, 60, "时钟非法不扣蓝");
            CheckEq(clk.AttackRecordCount, 1, "时钟非法不记账");
            Check(!clk.Tick(double.NaN), "Tick 拒绝 NaN");
            Check(!clk.Tick(tc - 5.0), "Tick 拒绝倒退");
            Check(clk.Tick(tc), "Tick 接受等值墙钟（非递减）");

            // ---- 容量 512：满表拒且不扣资源；TTL 回收后可继续；旧 ID 仍被水位拦住 ----
            PMCombatSession cap = BuildSession(Epoch,
                R(104u, 4, 2, PMHeroId.BeiYa), R(101u, 1, 1, PMHeroId.XueLi));
            Check(cap.StartMatch(2), "容量用例开战");
            double tcap = Advance(1.0);
            Check(cap.RequestAttack(104u, 1u, false, 1f, 0f, tcap).Accepted, "容量用例：ID 1 批准（贝亚扣 90 蓝）");
            CheckEq(cap.GetPlayer(104u).Mana, 0, "贝亚蓝 = 0");

            for (uint id = 2u; id <= 512u; id++)
            {
                PMCombatAttackDecision rejected = cap.RequestAttack(104u, id, false, 1f, 0f, tcap);
                if (id == 2u)
                {
                    Check(!rejected.Accepted && rejected.Reason == PMCombatRejectReason.Cooldown,
                        "满表压力用例：ID 2 因 cooldown 被拒（仍会占记录位）");
                }
            }

            CheckEq(cap.AttackRecordCount, 512, "账本达到 512 条（含被拒绝结论）");
            PMCombatAttackDecision full = cap.RequestAttack(104u, 513u, false, 1f, 0f, tcap);
            Check(!full.Accepted && full.Reason == PMCombatRejectReason.Capacity, "第 513 个新 ID → Capacity");
            CheckEq(cap.GetPlayer(104u).Mana, 0, "满表拒绝不扣蓝");
            CheckEq(cap.AttackRecordCount, 512, "满表拒绝不写账本");

            double tLater = Advance(5001.0);
            PMCombatAttackDecision afterPurge = cap.RequestAttack(104u, 514u, false, 1f, 0f, tLater);
            Check(!afterPurge.Accepted && afterPurge.Reason == PMCombatRejectReason.InsufficientMana,
                "TTL 过后容量被回收 → 走到资源检查（InsufficientMana 而不是 Capacity）");
            CheckEq(cap.AttackRecordCount, 1, "过期记录已被回收（只余新记录）");
            PMCombatAttackDecision retired = cap.RequestAttack(104u, 2u, false, 1f, 0f, tLater);
            Check(!retired.Accepted && retired.Reason == PMCombatRejectReason.StaleId,
                "被回收的旧 ID（2）仍被水位拦住，不会当成新请求复活");

            // ---- 深 clone：快照与返回计划都不得借出内部可变引用 ----
            PMCombatSession cl = NewStartedStandardSession();
            double tcl = Advance(1.0);
            PMCombatAttackDecision d = cl.RequestAttack(101u, 1u, false, 1f, 0f, tcl);
            Check(d.Accepted, "clone 用例：批准");
            float originalDirX = d.Plan.Directions[0].X;
            float originalDirZ = d.Plan.Directions[0].Z;
            d.Plan.Directions[0] = new PMVector3(0f, 0f, 0f);
            d.Plan.Spec.SpeedMps = 999f;
            PMProjectileSpec spec2;
            PMCombatRejectReason reason2;
            bool okAfterMutate = cl.TryAuthorizeProjectile(101u,
                MakeIntent(101u, 1u, 1u, originalDirX, originalDirZ), tcl, out spec2, out reason2);
            Check(okAfterMutate, "篡改返回的计划不影响内部账本（原方向仍可授权，reason=" + reason2 + "）");
            CheckEq(spec2.SpeedMps, 11f, "授权得到的 spec 来自冻结计划（speed=11）");

            PMCombatPlayerSnapshot snap = cl.GetPlayer(101u);
            snap.Hp = -12345;
            snap.Mana = -999;
            snap.Dead = true;
            snap.TeamId = 77;
            CheckEq(cl.GetPlayer(101u).Hp, 960, "改快照不影响会话 HP");
            CheckEq(cl.GetPlayer(101u).Mana, 60, "改快照不影响会话蓝量");
            Check(!cl.GetPlayer(101u).Dead, "改快照不影响会话死亡标志");
            CheckEq(cl.GetPlayer(101u).TeamId, 1, "改快照不影响会话队伍");

            PMCombatPlayerSnapshot[] capture = cl.CapturePlayers();
            CheckEq(capture.Length, 6, "CapturePlayers 返回 6 人");
            capture[0].Mana = -1;
            capture[0] = null;
            CheckEq(cl.CapturePlayers()[0].Mana, 60, "改 CapturePlayers 结果不影响会话");
            Check(cl.GetPlayer(999u) == null, "未知 netId → null");
        }

        // ================================================================ F. 授权

        private static void TestAuthorization()
        {
            // ---- 方向槽逐个占用（雪莉 5 槽，扇形）----
            PMCombatSession s = NewStartedStandardSession();
            double t = Advance(1.0);
            PMCombatAttackDecision d = s.RequestAttack(101u, 1u, false, 1f, 0f, t);
            Check(d.Accepted, "雪莉攻击批准");
            PMVector3[] dirs = d.Plan.Directions;

            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            for (int i = 0; i < 5; i++)
            {
                Check(TryAuthorize(s, 101u, 1u, (uint)(i + 1), dirs[i].X, dirs[i].Z, out spec, out reason),
                    "第 " + (i + 1) + " 方向槽授权成功（reason=" + reason + "）");
                if (i == 0)
                {
                    CheckEq(spec.SpeedMps, 11f, "授权 spec 速度来自计划（11）");
                    CheckEq(spec.LifetimeMs, 546, "雪莉 ceil(6/11*1000) = 546");
                    CheckEq(spec.RadiusM, PMCombatLimits.ProjectileRadiusM, "授权 spec 半径 = 0.1m");
                    Check(spec.StopOnHit, "授权 spec StopOnHit=true");
                }
            }

            Check(!TryAuthorize(s, 101u, 1u, 6u, dirs[0].X, dirs[0].Z, out spec, out reason)
                && reason == PMCombatRejectReason.ProjectileLimit, "第 6 颗 → ProjectileLimit（N=5）");
            // 额度优先于方向匹配：5 个槽已占满时，即使方向对不上也先报额度
            float offX;
            float offZ;
            Rotate(dirs[2].X, dirs[2].Z, 11.25f, out offX, out offZ);
            Check(!TryAuthorize(s, 101u, 1u, 7u, offX, offZ, out spec, out reason)
                && reason == PMCombatRejectReason.ProjectileLimit, "槽位用尽时偏离方向 → 先报 ProjectileLimit");

            // 重复 key：原样返回且不占槽（槽位已被 5 个 key 占满，重复仍成功）
            Check(TryAuthorize(s, 101u, 1u, 1u, dirs[0].X, dirs[0].Z, out spec, out reason),
                "重复 key 授权成功（reason=" + reason + "）");
            CheckEq(s.AuthorizedProjectileCount, 5, "重复 key 不新增授权元数据");

            // 容差内：旋转 0.5 度应匹配（但 5 个槽已占满 → ProjectileLimit，换新会话验证）
            PMCombatSession tol = NewStartedStandardSession();
            double tt = Advance(1.0);
            PMCombatAttackDecision td = tol.RequestAttack(101u, 1u, false, 1f, 0f, tt);
            Check(td.Accepted, "容差用例：攻击批准");
            float nearX;
            float nearZ;
            Rotate(td.Plan.Directions[0].X, td.Plan.Directions[0].Z, 0.5f, out nearX, out nearZ);
            Check(TryAuthorize(tol, 101u, 1u, 1u, nearX, nearZ, out spec, out reason),
                "容差内（0.5 度）→ 授权成功（reason=" + reason + "）");
            float farX;
            float farZ;
            Rotate(td.Plan.Directions[0].X, td.Plan.Directions[0].Z, -1.5f, out farX, out farZ);
            Check(!TryAuthorize(tol, 101u, 1u, 2u, farX, farZ, out spec, out reason)
                && reason == PMCombatRejectReason.DirectionMismatch, "容差外（-1.5 度 vs 首槽 -15 度）→ DirectionMismatch");
            float midX;
            float midZ;
            Rotate(td.Plan.Directions[2].X, td.Plan.Directions[2].Z, 11.25f, out midX, out midZ);
            Check(!TryAuthorize(tol, 101u, 1u, 3u, midX, midZ, out spec, out reason)
                && reason == PMCombatRejectReason.DirectionMismatch, "偏离最近槽 3.75 度（> 1 度容差）→ DirectionMismatch");

            // ---- 同方向多槽逐个取（柯尔特 angle=0，两条完全相同的方向）----
            PMCombatSession same = NewStartedStandardSession();
            double ts = Advance(1.0);
            PMCombatAttackDecision sd = same.RequestAttack(102u, 1u, false, 1f, 0f, ts);
            Check(sd.Accepted, "柯尔特攻击批准");
            CheckEq(sd.Plan.Directions.Length, 2, "柯尔特普通 2 弹");
            Check(TryAuthorize(same, 102u, 1u, 1u, 1f, 0f, out spec, out reason), "同向第 1 槽授权");
            Check(TryAuthorize(same, 102u, 1u, 2u, 1f, 0f, out spec, out reason), "同向第 2 槽逐个占用");
            Check(TryAuthorize(same, 102u, 1u, 1u, 1f, 0f, out spec, out reason), "重复 key 仍成功");
            Check(!TryAuthorize(same, 102u, 1u, 3u, 1f, 0f, out spec, out reason)
                && reason == PMCombatRejectReason.ProjectileLimit, "第 3 颗同向 → ProjectileLimit（2 槽用尽）");

            // ---- 身份：owner / epoch / origin / id ----
            PMCombatSession idn = NewStartedStandardSession();
            double ti = Advance(1.0);
            PMCombatAttackDecision idd = idn.RequestAttack(101u, 1u, false, 1f, 0f, ti);
            Check(idd.Accepted, "身份用例：攻击批准");
            PMProjectileSpawnIntent intent = MakeIntent(101u, 1u, 1u, idd.Plan.Directions[0].X, idd.Plan.Directions[0].Z);
            intent.Key = new PMProjectileKey(Epoch, 102u, 1u, PMProjectileOrigin.ClientPredicted);
            Check(!idn.TryAuthorizeProjectile(101u, intent, ti, out spec, out reason)
                && reason == PMCombatRejectReason.IdentityMismatch, "key.owner 与请求 owner 不符 → IdentityMismatch");

            intent = MakeIntent(101u, 1u, 1u, idd.Plan.Directions[0].X, idd.Plan.Directions[0].Z);
            intent.Key = new PMProjectileKey(Epoch + 1u, 101u, 1u, PMProjectileOrigin.ClientPredicted);
            Check(!idn.TryAuthorizeProjectile(101u, intent, ti, out spec, out reason)
                && reason == PMCombatRejectReason.IdentityMismatch, "epoch 不符 → IdentityMismatch");

            intent = MakeIntent(101u, 1u, 1u, idd.Plan.Directions[0].X, idd.Plan.Directions[0].Z);
            intent.Key = new PMProjectileKey(Epoch, 101u, 1u, PMProjectileOrigin.ServerDirect);
            Check(!idn.TryAuthorizeProjectile(101u, intent, ti, out spec, out reason)
                && reason == PMCombatRejectReason.IdentityMismatch, "ServerDirect 上行 → IdentityMismatch");

            Check(!idn.TryAuthorizeProjectile(101u, MakeIntent(101u, 1u, 0u, 1f, 0f), ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidId, "projectileId=0 → InvalidId");
            Check(!idn.TryAuthorizeProjectile(101u, MakeIntent(101u, 0u, 1u, 1f, 0f), ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidId, "activationId=0 → InvalidId");
            Check(!idn.TryAuthorizeProjectile(101u, null, ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidId, "intent=null → InvalidId");
            Check(!idn.TryAuthorizeProjectile(0u, MakeIntent(101u, 1u, 1u, 1f, 0f), ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidId, "owner=0 → InvalidId");
            Check(!idn.TryAuthorizeProjectile(101u, MakeIntent(101u, 1u, 1u, 0f, 0f), ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidAim, "方向零向量 → InvalidAim");
            Check(!idn.TryAuthorizeProjectile(101u, MakeIntent(101u, 1u, 1u, float.NaN, 0f), ti, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidAim, "方向 NaN → InvalidAim");

            // ---- 未知 / 被拒 activation ----
            Check(!TryAuthorize(idn, 101u, 999u, 1u, idd.Plan.Directions[0].X, idd.Plan.Directions[0].Z, out spec, out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "高于水位的未知 activation → UnknownAttack");

            PMCombatSession rejected = NewStartedStandardSession();
            double tr = Advance(1.0);
            PMCombatAttackDecision rd = rejected.RequestAttack(104u, 1u, true, 1f, 0f, tr);
            Check(!rd.Accepted && rd.Reason == PMCombatRejectReason.InsufficientEnergy, "能量不足的攻击被拒");
            Check(!rejected.TryAuthorizeProjectile(104u, MakeIntent(104u, 1u, 1u, 1f, 0f), tr, out spec, out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "被拒 activation 不能授权投射物");

            // ---- 多 owner：ID 空间按 owner 独立，槽位与资源也各算各的 ----
            PMCombatSession multi = NewStartedStandardSession();
            double tm = Advance(1.0);
            PMCombatAttackDecision m1 = multi.RequestAttack(101u, 1u, false, 1f, 0f, tm);
            Check(m1.Accepted, "owner 101 的 ID 1 批准");
            PMCombatAttackDecision m2 = multi.RequestAttack(102u, 1u, false, 1f, 0f, tm);
            Check(m2.Accepted, "owner 102 的 ID 1 批准（ID 空间按 owner 独立，不是冲突）");
            CheckEq(multi.IdConflictCount, 0, "两个 owner 各自的 ID 1 不算冲突");
            CheckEq(multi.GetPlayer(101u).Mana, 60, "101 各自扣蓝");
            CheckEq(multi.GetPlayer(102u).Mana, 60, "102 各自扣蓝");
            Check(TryAuthorize(multi, 101u, 1u, 1u, m1.Plan.Directions[0].X, m1.Plan.Directions[0].Z, out spec, out reason),
                "101 的第 1 槽授权（reason=" + reason + "）");
            Check(TryAuthorize(multi, 102u, 1u, 1u, m2.Plan.Directions[0].X, m2.Plan.Directions[0].Z, out spec, out reason),
                "102 的槽位独立授权（reason=" + reason + "）");
            Check(TryAuthorize(multi, 101u, 1u, 2u, m1.Plan.Directions[1].X, m1.Plan.Directions[1].Z, out spec, out reason),
                "101 的第 2 槽授权（自己的额度，reason=" + reason + "）");
            Check(!TryAuthorize(multi, 101u, 2u, 9u, 1f, 0f, out spec, out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "101 使用不属于它的 activation → UnknownAttack");

            // ---- 未开战 / 终局后 ----
            PMCombatSession late = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            double tl = Advance(1.0);
            Check(!late.TryAuthorizeProjectile(101u, MakeIntent(101u, 1u, 1u, 1f, 0f), tl, out spec, out reason)
                && reason == PMCombatRejectReason.NotStarted, "未开战授权 → NotStarted");
            Check(late.StartMatch(2), "开战");
            PMCombatAttackDecision kill = late.RequestAttack(104u, 1u, false, 1f, 0f, tl);
            Check(kill.Accepted, "佩佩攻击批准（650 伤害）");
            Check(TryAuthorize(late, 104u, 1u, 1u, kill.Plan.Directions[0].X, kill.Plan.Directions[0].Z, out spec, out reason), "授权成功");
            Check(late.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), tl), "结算");
            CheckEq(late.GetPlayer(101u).Hp, 310, "960-650 = 310（未致死）");
            Check(!late.Ended, "未致死 → 未终局");
            PMCombatAttackDecision kill2 = late.RequestAttack(104u, 2u, false, 1f, 0f, Advance(100.0));
            Check(kill2.Accepted, "第二次攻击批准");
            Check(TryAuthorize(late, 104u, 2u, 2u, kill2.Plan.Directions[0].X, kill2.Plan.Directions[0].Z, out spec, out reason), "第二次授权");
            Check(late.ApplySettlement(MakeSettlement(MakeKey(104u, 2u, 2u), 2u, 101u), Now()), "第二次结算（致死）");
            Check(late.Ended, "终局");
            Check(!late.TryAuthorizeProjectile(104u, MakeIntent(104u, 3u, 3u, 1f, 0f), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "终局后新 key 授权 → MatchEnded");

            // ---- 时钟倒退 ----
            PMCombatSession clkt = NewStartedStandardSession();
            double tct = Advance(1000.0);
            Check(clkt.RequestAttack(101u, 1u, false, 1f, 0f, tct).Accepted, "时钟用例攻击批准");
            Check(!clkt.TryAuthorizeProjectile(101u, MakeIntent(101u, 1u, 1u, 1f, 0f), tct - 1.0, out spec, out reason)
                && reason == PMCombatRejectReason.InvalidClock, "授权入口倒退墙钟 → InvalidClock");
        }

        // ================================================================ G. 结算

        private static void TestSettlement()
        {
            // ---- 基础：命中敌队、重复去重、友军/自伤/未知无写 ----
            PMCombatSession s = NewStartedStandardSession();
            double t = Advance(1.0);
            PMCombatAttackDecision d = s.RequestAttack(101u, 1u, false, 1f, 0f, t);
            Check(d.Accepted, "雪莉攻击批准");
            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            Check(TryAuthorize(s, 101u, 1u, 1u, d.Plan.Directions[0].X, d.Plan.Directions[0].Z, out spec, out reason), "授权成功");
            PMProjectileKey key = MakeKey(101u, 1u, 1u);

            Check(s.ApplySettlement(MakeSettlement(key, 1u, 104u), t), "命中敌队 104");
            CheckEq(s.GetPlayer(104u).Hp, 820, "900-80 = 820");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 40, "回能 80/2 = 40");

            Check(s.ApplySettlement(MakeSettlement(key, 1u, 104u), t), "重复结算被受理（幂等消费）");
            CheckEq(s.GetPlayer(104u).Hp, 820, "重复结算不产生第二次伤害");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 40, "重复结算不重复回能");

            Check(s.ApplySettlement(MakeSettlement(key, 1u, 102u), t), "友军命中被受理但跳过");
            CheckEq(s.GetPlayer(102u).Hp, 1180, "友军不受伤");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 40, "友军不回能");

            Check(s.ApplySettlement(MakeSettlement(key, 1u, 101u), t), "自伤被受理但跳过");
            CheckEq(s.GetPlayer(101u).Hp, 960, "自伤无写");

            Check(s.ApplySettlement(MakeSettlement(key, 1u, 999u), t), "未知目标被受理但跳过");
            Check(s.GetPlayer(999u) == null, "未知目标未进入名册");

            // 另一个 key 命中同一目标 → 再次生效（每 key-target 一次，而不是每 target 一次）
            Check(TryAuthorize(s, 101u, 1u, 2u, d.Plan.Directions[1].X, d.Plan.Directions[1].Z, out spec, out reason), "第二颗弹授权");
            Check(s.ApplySettlement(MakeSettlement(MakeKey(101u, 1u, 2u), 1u, 104u), t), "第二颗弹命中同一目标");
            CheckEq(s.GetPlayer(104u).Hp, 740, "不同 key → 再次扣血 820-80 = 740");
            CheckEq(s.GetPlayer(101u).SuperEnergy, 80, "再回能 40 → 80");

            // ---- 整批结构先验 ----
            PMProjectileSettlement bad = MakeSettlement(key, 1u, 104u);
            bad.Hits = null;
            Check(!s.ApplySettlement(bad, t, out reason) && reason == PMCombatRejectReason.InvalidId, "Hits=null → InvalidId");

            bad = MakeSettlement(key, 1u);
            Check(!s.ApplySettlement(bad, t, out reason) && reason == PMCombatRejectReason.InvalidId, "空批 → InvalidId");

            bad = MakeSettlement(key, 1u, 104u);
            bad.HitCount = 5;
            Check(!s.ApplySettlement(bad, t, out reason) && reason == PMCombatRejectReason.InvalidId, "HitCount 与 Hits 长度不符 → InvalidId");

            bad = MakeSettlement(key, 1u, 104u);
            bad.Hits[0].TargetNetId = 0u;
            Check(!s.ApplySettlement(bad, t, out reason) && reason == PMCombatRejectReason.InvalidId, "目标 0 → InvalidId");

            bad = MakeSettlement(key, 1u, 104u, 104u);
            Check(!s.ApplySettlement(bad, t, out reason) && reason == PMCombatRejectReason.InvalidId, "同批重复目标 → InvalidId");

            // 整批先验：一条合法 + 一条目标 0 → 整批拒绝，合法目标也不得先扣血
            PMCombatSession bulk = NewStartedStandardSession();
            double tb = Advance(1.0);
            PMCombatAttackDecision bd = bulk.RequestAttack(101u, 1u, false, 1f, 0f, tb);
            Check(TryAuthorize(bulk, 101u, 1u, 1u, bd.Plan.Directions[0].X, bd.Plan.Directions[0].Z, out spec, out reason), "整批用例授权");
            PMProjectileSettlement mixed = MakeSettlement(MakeKey(101u, 1u, 1u), 1u, 105u, 106u);
            mixed.Hits[1].TargetNetId = 0u;
            Check(!bulk.ApplySettlement(mixed, tb, out reason) && reason == PMCombatRejectReason.InvalidId, "混合非法批 → 整批拒");
            CheckEq(bulk.GetPlayer(105u).Hp, 840, "整批被拒后合法目标也没有扣血（结构先验）");

            PMProjectileSettlement two = MakeSettlement(MakeKey(101u, 1u, 1u), 1u, 105u, 106u);
            Check(bulk.ApplySettlement(two, tb), "合法双目标批受理");
            CheckEq(bulk.GetPlayer(105u).Hp, 760, "105：840-80");
            CheckEq(bulk.GetPlayer(106u).Hp, 760, "106：840-80");
            CheckEq(bulk.GetPlayer(101u).SuperEnergy, 80, "两次命中各回 40 → 80");

            // ---- 错误 key / owner / epoch / activation ----
            Check(!s.ApplySettlement(MakeSettlement(MakeKey(101u, 1u, 9u), 1u, 104u), t, out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "未授权的 key → UnknownAttack");

            PMProjectileSettlement wrongAct = MakeSettlement(key, 9u, 104u);
            Check(!s.ApplySettlement(wrongAct, t, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "activation 不符 → IdentityMismatch");

            PMProjectileSettlement wrongOwner = MakeSettlement(key, 1u, 104u);
            wrongOwner.OwnerNetId = 102u;
            Check(!s.ApplySettlement(wrongOwner, t, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "owner 不符 → IdentityMismatch");

            PMProjectileSettlement wrongEpoch = MakeSettlement(key, 1u, 104u);
            wrongEpoch.Key = new PMProjectileKey(Epoch + 1u, 101u, 1u, PMProjectileOrigin.ClientPredicted);
            Check(!s.ApplySettlement(wrongEpoch, t, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "epoch 不符 → IdentityMismatch");
            CheckEq(s.GetPlayer(104u).Hp, 740, "以上错误结算都无写");

            // ---- damage 0：不扣血、不回能、不首杀 ----
            PMCombatSession zero = BuildSession(Epoch,
                R(111u, 11, 1, PMHeroId.SiPaiKe), R(121u, 21, 2, PMHeroId.XueLi));
            Check(zero.StartMatch(2), "斯派克 1v1 开战");
            double tz = Advance(1.0);
            PMCombatAttackDecision zd = zero.RequestAttack(111u, 1u, false, 1f, 0f, tz);
            Check(zd.Accepted && zd.Plan.Damage == 0, "斯派克攻击批准且伤害 0");
            Check(TryAuthorize(zero, 111u, 1u, 1u, zd.Plan.Directions[0].X, zd.Plan.Directions[0].Z, out spec, out reason), "斯派克弹授权");
            Check(zero.ApplySettlement(MakeSettlement(MakeKey(111u, 1u, 1u), 1u, 121u), tz), "伤害 0 结算被受理");
            CheckEq(zero.GetPlayer(121u).Hp, 960, "伤害 0 不扣血");
            CheckEq(zero.GetPlayer(111u).SuperEnergy, 0, "伤害 0 不回能");
            Check(!zero.GetPlayer(121u).Dead, "伤害 0 不致死");
            Check(!zero.Ended, "伤害 0 不首杀（不终局）");

            // ---- 首杀：单次锁 + clamp + 终局后无变化 ----
            PMCombatSession kill = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe), R(103u, 3, 1, PMHeroId.BuLuoKe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei), R(106u, 6, 2, PMHeroId.RuiKe));
            Check(kill.StartMatch(6), "首杀用例开战");
            double tk = Advance(1.0);
            PMCombatAttackDecision kd = kill.RequestAttack(103u, 1u, false, 1f, 0f, tk);
            Check(kd.Accepted && kd.Plan.Damage == 1155, "布洛克攻击批准（1155）");
            Check(TryAuthorize(kill, 103u, 1u, 1u, kd.Plan.Directions[0].X, kd.Plan.Directions[0].Z, out spec, out reason), "布洛克弹授权");
            PMProjectileKey kkey = MakeKey(103u, 1u, 1u);

            Check(kill.ApplySettlement(MakeSettlement(kkey, 1u, 105u), tk), "首杀结算受理");
            CheckEq(kill.GetPlayer(105u).Hp, 0, "840-1155 → clamp 0");
            Check(kill.GetPlayer(105u).Dead, "105 死亡");
            Check(kill.Ended, "首杀即终局");
            CheckEq(kill.Outcome.WinnerTeamId, 1, "胜方 = 击杀者队伍 1");
            CheckEq(kill.Outcome.OutcomeId, 1u, "OutcomeId 从 1 开始且非 0");
            CheckEq(kill.Outcome.Reason, PMCombatEndReason.FirstKill, "结束原因 = FirstKill");
            CheckEq(kill.GetPlayer(103u).SuperEnergy, 200, "击杀回能 1155/2 → 封顶 200");

            // 终局后：同 key 换目标、复投同批、再次攻击 全部无变化
            Check(!kill.ApplySettlement(MakeSettlement(kkey, 1u, 106u), tk, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "终局后结算 → MatchEnded");
            CheckEq(kill.GetPlayer(106u).Hp, 840, "终局后目标无写");
            Check(!kill.ApplySettlement(MakeSettlement(kkey, 1u, 105u), tk, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "终局后重复结算 → MatchEnded");
            CheckEq(kill.Outcome.OutcomeId, 1u, "OutcomeId 只锁一次");
            CheckEq(kill.Outcome.WinnerTeamId, 1, "WinnerTeamId 只锁一次");
            PMCombatAttackDecision afterEnd = kill.RequestAttack(103u, 2u, false, 1f, 0f, tk);
            Check(!afterEnd.Accepted && afterEnd.Reason == PMCombatRejectReason.MatchEnded, "终局后攻击 → MatchEnded");
            CheckEq(kill.GetPlayer(103u).Mana, 60, "终局后攻击不扣蓝");

            // ---- 首杀胜方不是固定 1：team 2 击杀 ----
            PMCombatSession win2 = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(win2.StartMatch(2), "team2 击杀用例开战");
            double t2 = Advance(1.0);
            PMCombatAttackDecision w2 = win2.RequestAttack(104u, 1u, false, 1f, 0f, t2);
            Check(w2.Accepted, "104 攻击批准");
            Check(TryAuthorize(win2, 104u, 1u, 1u, w2.Plan.Directions[0].X, w2.Plan.Directions[0].Z, out spec, out reason), "104 弹授权");
            Check(win2.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t2), "104 击杀 101");
            CheckEq(win2.Outcome.WinnerTeamId, 2, "胜方 = 2（证明不是硬编码 1）");

            // ---- 已死目标不再结算 ----
            PMCombatSession dead = BuildSession(Epoch,
                R(103u, 3, 1, PMHeroId.BuLuoKe), R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei),
                R(106u, 6, 2, PMHeroId.RuiKe));
            Check(dead.StartMatch(4), "已死目标用例开战");
            double td = Advance(1.0);
            PMCombatAttackDecision dd = dead.RequestAttack(103u, 1u, false, 1f, 0f, td);
            Check(TryAuthorize(dead, 103u, 1u, 1u, dd.Plan.Directions[0].X, dd.Plan.Directions[0].Z, out spec, out reason), "授权 1");
            Check(dead.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 1u), 1u, 105u), td), "击杀 105");
            Check(dead.GetPlayer(105u).Dead, "105 死亡");
            // 终局后一切结算被拒（MatchEnded），这里验证「死亡后不再被计入伤害」由终局本身保证
            Check(!dead.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 1u), 1u, 106u), td, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "首杀终局后不再结算其他目标");

            // ---- 结算入口时钟 ----
            Check(!s.ApplySettlement(MakeSettlement(key, 1u, 104u), double.NaN, out reason)
                && reason == PMCombatRejectReason.InvalidClock, "结算 NaN 墙钟 → InvalidClock");
            Check(!s.ApplySettlement(MakeSettlement(key, 1u, 104u), t - 100.0, out reason)
                && reason == PMCombatRejectReason.InvalidClock, "结算倒退墙钟 → InvalidClock");
            CheckEq(s.GetPlayer(104u).Hp, 740, "时钟非法结算无写");

            // ---- 未开战结算 ----
            PMCombatSession ns = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(!ns.ApplySettlement(MakeSettlement(MakeKey(101u, 1u, 1u), 1u, 104u), Advance(1.0), out reason)
                && reason == PMCombatRejectReason.NotStarted, "未开战结算 → NotStarted");
        }

        // ================================================================ H. 终局 / 断线

        private static void TestEndgame()
        {
            // ---- 未 start 不早终局 ----
            PMCombatSession pre = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(pre.Disconnect(101u), "未开战时断线被记录");
            Check(!pre.Ended, "未 StartMatch 不提前终局");
            Check(!pre.GetPlayer(101u).Connected, "断线状态可见");

            // ---- 1v1：断线方队伍被没收，胜方 = 剩余唯一队伍（不是固定 1）----
            PMCombatSession a = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(a.StartMatch(2), "1v1 开战");
            double ta = Advance(1.0);
            Check(a.RequestAttack(101u, 5u, false, 1f, 0f, ta).Accepted, "101 攻击批准（留下水位与记录）");
            CheckEq(a.GetPlayer(101u).Mana, 60, "扣蓝 → 60");
            Check(a.Disconnect(101u), "101 断线");
            Check(a.Ended, "剩余唯一队伍 → 终局");
            CheckEq(a.Outcome.WinnerTeamId, 2, "胜方 = 剩余队伍 2（不是硬编码 1）");
            CheckEq(a.Outcome.Reason, PMCombatEndReason.Forfeit, "结束原因 = Forfeit");

            PMCombatAttackDecision dup = a.RequestAttack(101u, 5u, false, 1f, 0f, ta);
            Check(dup.Duplicate && dup.Accepted, "断线后重复 ID 仍返回原结论（水位与记录保留）");
            CheckEq(a.GetPlayer(101u).Mana, 60, "断线后重复不双扣");
            PMCombatAttackDecision newId = a.RequestAttack(101u, 6u, false, 1f, 0f, ta);
            Check(!newId.Accepted && newId.Reason == PMCombatRejectReason.MatchEnded, "终局后新 ID → MatchEnded");

            PMCombatSession b = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(b.StartMatch(2), "1v1 开战（反向）");
            Check(b.Disconnect(104u), "104 断线");
            CheckEq(b.Outcome.WinnerTeamId, 1, "胜方 = 剩余队伍 1");

            // ---- 2v2：断一人不终局，断到只剩一队才判 ----
            PMCombatSession ff = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(ff.StartMatch(4), "2v2 开战");
            Check(ff.Disconnect(101u), "断 101");
            Check(!ff.Ended, "两队都在线 → 不终局");
            Check(!ff.Disconnect(101u), "重复断线 → 无变化");
            Check(ff.Disconnect(102u), "断 102（team1 全掉线）");
            Check(ff.Ended, "只剩 team2 → 终局");
            CheckEq(ff.Outcome.WinnerTeamId, 2, "胜方 = 2");

            // ---- 无唯一赢家 → winner 0 ----
            PMCombatSession solo = BuildSession(Epoch, R(101u, 1, 3, PMHeroId.XueLi));
            Check(solo.StartMatch(1), "单人局开战（StartMatch(1) 用于表达「无唯一队伍」这条契约）");
            Check(solo.Disconnect(101u), "断线");
            Check(solo.Ended, "无人剩余 → 终局");
            CheckEq(solo.Outcome.WinnerTeamId, 0, "无唯一队伍 → winner 0（不硬编码 1）");
            CheckEq(solo.Outcome.Reason, PMCombatEndReason.Draw, "无剩余队伍 → Draw");

            // ---- 首杀后断线不改胜负（结论只锁一次）----
            PMCombatSession lockFirst = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(lockFirst.StartMatch(2), "首杀+断线用例开战");
            double tl = Advance(1.0);
            PMCombatAttackDecision ld = lockFirst.RequestAttack(104u, 1u, false, 1f, 0f, tl);
            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            Check(TryAuthorize(lockFirst, 104u, 1u, 1u, ld.Plan.Directions[0].X, ld.Plan.Directions[0].Z, out spec, out reason), "授权");
            Check(lockFirst.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), tl), "首杀（team2）");
            CheckEq(lockFirst.Outcome.WinnerTeamId, 2, "首杀胜方 = 2");
            Check(lockFirst.Disconnect(101u), "断线被记录");
            CheckEq(lockFirst.Outcome.WinnerTeamId, 2, "首杀结论不被后续断线改写");
            CheckEq(lockFirst.Outcome.Reason, PMCombatEndReason.FirstKill, "结束原因仍为 FirstKill");

            // ---- 终局后 Tick 不回蓝 ----
            double tl2 = Advance(10000.0);
            lockFirst.Tick(tl2);
            CheckEq(lockFirst.GetPlayer(104u).Mana, 60, "终局后不回蓝（布洛克扣了 30）");
        }

        // ================================================================ I. 对抗审查（R6-A1 review）
        //
        //  本节是独立 review 追加的对抗用例，不动 A–H 的任何既有断言。
        //  期望值全部手工推导（damage/2 取整、HP 算术、门限数值），走的是生产入口
        //  （RequestAttack / TryAuthorizeProjectile / ApplySettlement / Tick / Disconnect / StartMatch）。
        //
        //  标签约定：
        //    · 无标注 = 契约要求的行为（实现必须做到）；
        //    · 回顾（`_r6_core_review.md`）里被标为「by design」的边界中，属于**契约缺口**的四项已在
        //      J 段收口（重复 key 回显绕过 Ended/Dead/Expired、Capacity 不占水位、
        //      StartMatch 不看连接态、结算不校验 attacker 连接态与 AuthorityNetId），
        //      本节对应旧断言已改为契约正确期望（不改 A–H 的正例）。

        private static void TestAdversarialReview()
        {
            // ---- I.1 非法请求不写资源；只有「被记账的拒绝」才推进水位 ----
            PMCombatSession noSide = NewStartedStandardSession();
            double t1 = Advance(1.0);
            Check(noSide.RequestAttack(101u, 1u, false, 1f, 0f, t1).Accepted, "I.1 基准：ID 1 批准");
            CheckEq(noSide.GetPlayer(101u).Mana, 60, "I.1 基准扣蓝 → 60");
            CheckEq(noSide.RequestAttack(101u, 2u, false, 1f, 0f, t1).Reason,
                PMCombatRejectReason.Cooldown, "I.1 同刻 ID 2 → Cooldown");
            CheckEq(noSide.RequestAttack(101u, 3u, false, 0f, 0f, t1).Reason,
                PMCombatRejectReason.InvalidAim, "I.1 ID 3 零方向 → InvalidAim");
            CheckEq(noSide.RequestAttack(999u, 1u, false, 1f, 0f, t1).Reason,
                PMCombatRejectReason.UnknownPlayer, "I.1 未知玩家 → UnknownPlayer");
            CheckEq(noSide.AttackRecordCount, 3, "I.1 账本只含 Accepted + Cooldown + InvalidAim（未知玩家不记账）");
            CheckEq(noSide.GetPlayer(101u).Mana, 60, "I.1 全部拒绝不扣蓝");
            CheckEq(noSide.GetPlayer(101u).SuperEnergy, 0, "I.1 全部拒绝不改能量");
            CheckEq(noSide.GetPlayer(104u).Hp, 900, "I.1 全部拒绝不改 HP");

            Check(noSide.RequestAttack(101u, 10u, false, 1f, 0f, Advance(200.0)).Accepted, "I.1 跳到 ID 10 批准");
            CheckEq(noSide.GetPlayer(101u).Mana, 30, "I.1 ID 10 扣蓝 → 30");
            CheckEq(noSide.RequestAttack(101u, 5u, false, 1f, 0f, Advance(200.0)).Reason,
                PMCombatRejectReason.StaleId, "I.1 水位之下且从未记账的 ID 5 → StaleId");
            CheckEq(noSide.AttackRecordCount, 4, "I.1 StaleId 不记账");
            CheckEq(noSide.GetPlayer(101u).Mana, 30, "I.1 StaleId 不扣蓝");

            // ---- I.2 未支持攻击（抛物线 / 无大招配置）一律不扣资源 ----
            PMCombatSession para = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BaLi));
            Check(para.StartMatch(2), "I.2 巴利局开战");
            double tp = Advance(1.0);
            PMCombatAttackDecision bali = para.RequestAttack(104u, 1u, false, 1f, 0f, tp);
            CheckEq(bali.Reason, PMCombatRejectReason.UnsupportedAttack, "I.2 巴利普通（抛物线）→ UnsupportedAttack");
            CheckEq(para.GetPlayer(104u).Mana, 90, "I.2 抛物线拒绝不扣蓝");
            CheckEq(para.AttackRecordCount, 1, "I.2 抛物线拒绝进账本（幂等需要它）");

            PMCombatSession noSuper = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(noSuper.StartMatch(2), "I.2 佩佩局开战");
            double tn = Advance(1.0);
            PMCombatAttackDecision pn = noSuper.RequestAttack(105u, 1u, false, 1f, 0f, tn);
            Check(pn.Accepted, "I.2 佩佩普通批准");
            PMProjectileSpec spec;
            PMCombatRejectReason reason;
            Check(TryAuthorize(noSuper, 105u, 1u, 1u, pn.Plan.Directions[0].X, pn.Plan.Directions[0].Z, out spec, out reason),
                "I.2 佩佩弹授权（reason=" + reason + "）");
            Check(noSuper.ApplySettlement(MakeSettlement(MakeKey(105u, 1u, 1u), 1u, 101u), tn), "I.2 佩佩命中 101");
            CheckEq(noSuper.GetPlayer(101u).Hp, 310, "I.2 960-650 = 310");
            CheckEq(noSuper.GetPlayer(105u).SuperEnergy, 200, "I.2 650/2 = 325 → 封顶 200");
            CheckEq(noSuper.GetPlayer(105u).Mana, 60, "I.2 佩佩扣蓝 30 → 60");
            PMCombatAttackDecision badSuper = noSuper.RequestAttack(105u, 2u, true, 1f, 0f, Advance(200.0));
            CheckEq(badSuper.Reason, PMCombatRejectReason.UnsupportedAttack, "I.2 佩佩无大招配置 → UnsupportedAttack");
            CheckEq(noSuper.GetPlayer(105u).SuperEnergy, 200, "I.2 未支持大招不清能量（资源零副作用）");
            CheckEq(noSuper.GetPlayer(105u).Mana, 60, "I.2 未支持大招不扣蓝");

            // ---- I.3 伤害回能 = damage/2 向下取整（手工 oracle：45 → 22 → 44 → 66）----
            PMCombatSession half = BuildSession(Epoch,
                R(103u, 3, 1, PMHeroId.GongNiu), R(105u, 5, 1, PMHeroId.PeiPei),
                R(104u, 4, 2, PMHeroId.PeiPei), R(106u, 6, 2, PMHeroId.RuiKe));
            Check(half.StartMatch(4), "I.3 2v2 开战");
            double th = Advance(1.0);
            PMCombatAttackDecision gong = half.RequestAttack(103u, 1u, false, 1f, 0f, th);
            Check(gong.Accepted && gong.Plan.Damage == 45, "I.3 公牛单发伤害 45");
            Check(TryAuthorize(half, 103u, 1u, 1u, gong.Plan.Directions[0].X, gong.Plan.Directions[0].Z, out spec, out reason),
                "I.3 公牛第 1 槽授权");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 1u), 1u, 104u), th), "I.3 命中敌队 104");
            CheckEq(half.GetPlayer(104u).Hp, 795, "I.3 840-45 = 795");
            CheckEq(half.GetPlayer(103u).SuperEnergy, 22, "I.3 45/2 = 22（整数除法向下取整）");
            Check(TryAuthorize(half, 103u, 1u, 2u, gong.Plan.Directions[1].X, gong.Plan.Directions[1].Z, out spec, out reason),
                "I.3 公牛第 2 槽授权");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 2u), 1u, 104u), th), "I.3 第 2 槽命中同一目标");
            CheckEq(half.GetPlayer(104u).Hp, 750, "I.3 795-45 = 750");
            CheckEq(half.GetPlayer(103u).SuperEnergy, 44, "I.3 再回 22 → 44");
            Check(TryAuthorize(half, 103u, 1u, 3u, gong.Plan.Directions[2].X, gong.Plan.Directions[2].Z, out spec, out reason),
                "I.3 公牛第 3 槽授权");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 3u), 1u, 105u), th), "I.3 友军命中被受理但跳过");
            CheckEq(half.GetPlayer(105u).Hp, 840, "I.3 友军不掉血");
            CheckEq(half.GetPlayer(103u).SuperEnergy, 44, "I.3 友军不回能");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 3u), 1u, 103u), th), "I.3 自伤被受理但跳过");
            CheckEq(half.GetPlayer(103u).Hp, 1400, "I.3 自伤无写");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 3u), 1u, 999u), th), "I.3 未知目标被受理但跳过");
            CheckEq(half.GetPlayer(103u).SuperEnergy, 44, "I.3 未知目标不回能");
            Check(half.ApplySettlement(MakeSettlement(MakeKey(103u, 1u, 3u), 1u, 104u), th), "I.3 第 3 槽最终命中敌队");
            CheckEq(half.GetPlayer(104u).Hp, 705, "I.3 750-45 = 705（前几次跳过没有消耗该 key 的额度）");
            CheckEq(half.GetPlayer(103u).SuperEnergy, 66, "I.3 再回 22 → 66");

            // ---- I.4 麦克斯 NormalAttackManaRecover 本批不消费（契约明确留到后续技能批次）----
            PMCombatSession maxwell = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(113u, 13, 2, PMHeroId.MaiKeSi));
            Check(maxwell.StartMatch(2), "I.4 麦克斯局开战");
            double tm2 = Advance(1.0);
            Check(maxwell.RequestAttack(113u, 1u, false, 1f, 0f, tm2).Accepted, "I.4 麦克斯普通批准");
            CheckEq(maxwell.GetPlayer(113u).Mana, 60, "I.4 只扣 30、不回 3（本批不消费 NormalAttackManaRecover）");

            // ---- I.5 整批结构先验：任何不合格 → 整批拒绝且零写入 ----
            PMCombatSession bulk = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei), R(105u, 5, 2, PMHeroId.RuiKe));
            Check(bulk.StartMatch(3), "I.5 开战");
            double tb = Advance(1.0);
            PMCombatAttackDecision bd = bulk.RequestAttack(101u, 1u, false, 1f, 0f, tb);
            Check(TryAuthorize(bulk, 101u, 1u, 1u, bd.Plan.Directions[0].X, bd.Plan.Directions[0].Z, out spec, out reason),
                "I.5 授权");
            PMProjectileKey bkey = MakeKey(101u, 1u, 1u);

            PMProjectileSettlement s101 = MakeSettlement(bkey, 1u, 104u, 105u);
            s101.Hits = new PMProjectileValidatedHit[101];
            for (int i = 0; i < 101; i++)
            {
                s101.Hits[i].TargetNetId = (uint)(1000 + i);
                s101.Hits[i].TargetStreamVersion = 1u;
            }

            s101.HitCount = 101;
            Check(!bulk.ApplySettlement(s101, tb, out reason) && reason == PMCombatRejectReason.InvalidId,
                "I.5 Hits=101（> MaxTargets=100）→ InvalidId");
            PMProjectileSettlement sNeg = MakeSettlement(bkey, 1u, 104u);
            sNeg.HitCount = -1;
            Check(!bulk.ApplySettlement(sNeg, tb, out reason) && reason == PMCombatRejectReason.InvalidId,
                "I.5 HitCount=-1 → InvalidId");
            PMProjectileSettlement sZeroPid = MakeSettlement(bkey, 1u, 104u);
            sZeroPid.Key = new PMProjectileKey(Epoch, 101u, 0u, PMProjectileOrigin.ClientPredicted);
            Check(!bulk.ApplySettlement(sZeroPid, tb, out reason) && reason == PMCombatRejectReason.InvalidId,
                "I.5 key.projectileId=0 → InvalidId");
            PMProjectileSettlement sZeroAct = MakeSettlement(bkey, 0u, 104u);
            Check(!bulk.ApplySettlement(sZeroAct, tb, out reason) && reason == PMCombatRejectReason.InvalidId,
                "I.5 activationId=0 → InvalidId");
            CheckEq(bulk.GetPlayer(104u).Hp, 840, "I.5 以上结构拒绝零写入（目标 1）");
            CheckEq(bulk.GetPlayer(105u).Hp, 840, "I.5 以上结构拒绝零写入（目标 2）");
            CheckEq(bulk.GetPlayer(101u).SuperEnergy, 0, "I.5 以上结构拒绝不回能");
            CheckEq(bulk.AttackRecordCount, 1, "I.5 结构拒绝不写账本");

            // ---- I.6 批次自身打出首杀：同批后续目标不得再写入（终局后无变化）----
            PMCombatSession stop = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe), R(103u, 3, 1, PMHeroId.BuLuoKe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei), R(106u, 6, 2, PMHeroId.RuiKe));
            Check(stop.StartMatch(6), "I.6 开战");
            double ts = Advance(1.0);
            PMCombatAttackDecision bru = stop.RequestAttack(103u, 1u, false, 1f, 0f, ts);
            Check(bru.Accepted && bru.Plan.Damage == 1155, "I.6 布洛克伤害 1155");
            Check(TryAuthorize(stop, 103u, 1u, 1u, bru.Plan.Directions[0].X, bru.Plan.Directions[0].Z, out spec, out reason),
                "I.6 授权");
            PMProjectileSettlement lethal = MakeSettlement(MakeKey(103u, 1u, 1u), 1u, 105u, 106u);
            Check(stop.ApplySettlement(lethal, ts), "I.6 整批受理");
            CheckEq(stop.GetPlayer(105u).Hp, 0, "I.6 第 1 目标 840 被 1155 打死并 clamp 0");
            Check(stop.GetPlayer(105u).Dead, "I.6 第 1 目标 Dead");
            Check(stop.Ended, "I.6 首杀即终局");
            CheckEq(stop.Outcome.WinnerTeamId, 1, "I.6 胜方 = 击杀者队伍 1");
            CheckEq(stop.Outcome.OutcomeId, 1u, "I.6 OutcomeId 只锁一次");
            CheckEq(stop.GetPlayer(106u).Hp, 840, "I.6 同批第 2 目标不再写入（终局后无变化）");
            Check(!stop.GetPlayer(106u).Dead, "I.6 同批第 2 目标不因同批后续命中被置 Dead");
            CheckEq(stop.GetPlayer(103u).SuperEnergy, 200, "I.6 击杀回能封顶 200");

            // ---- I.7 容量拒绝不写满表记录，但**必须占该 owner 的单调水位**：同一 ID 不得复活 ----
            PMCombatSession cap2 = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(cap2.StartMatch(2), "I.7 开战");
            double tc = Advance(1.0);
            Check(cap2.RequestAttack(101u, 1u, false, 1f, 0f, tc).Accepted, "I.7 ID 1 批准（雪莉扣 30）");
            CheckEq(cap2.GetPlayer(101u).Mana, 60, "I.7 蓝 = 60");
            for (uint id = 2u; id <= 512u; id++)
            {
                cap2.RequestAttack(101u, id, false, 1f, 0f, tc);
            }

            CheckEq(cap2.AttackRecordCount, 512, "I.7 账本满 512");
            ResSnapshot capCapacityBefore = Snap(cap2, 101u);
            PMCombatAttackDecision over = cap2.RequestAttack(101u, 513u, false, 1f, 0f, tc);
            Check(!over.Accepted && over.Reason == PMCombatRejectReason.Capacity, "I.7 第 513 个新 ID → Capacity");
            CheckEq(cap2.AttackRecordCount, 512, "I.7 Capacity 不写满表记录（也不插一条）");
            CheckResUnchanged("I.7 Capacity 拒绝零副作用", capCapacityBefore, Snap(cap2, 101u));

            // 反例：TTL 回收 + 资源恢复之后，同一个 ID 513 不得被当成全新攻击接受。
            // （修复前这条会 Accepted 并再扣一次蓝；R5 已经因为这次 Rejected 撤了该 activation 的假弹，
            //   重接受 = 扣了资源却生成不出东西。）
            ResSnapshot capReuseBefore = Snap(cap2, 101u);
            PMCombatAttackDecision reuse = cap2.RequestAttack(101u, 513u, false, 1f, 0f, Advance(5001.0));
            Check(!reuse.Accepted && reuse.Reason == PMCombatRejectReason.StaleId,
                "I.7 回收后同一 ID 513 → StaleId（Capacity 也占水位，不复活）");
            CheckResUnchanged("I.7 同一 ID 复活被拦零副作用", capReuseBefore, Snap(cap2, 101u));
            CheckEq(cap2.GetPlayer(101u).Mana, 60, "I.7 蓝仍为 60（没有被第二次扣费）");
            CheckEq(cap2.AttackRecordCount, 512, "I.7 StaleId 不触发回收也不记账（账本仍为 512）");

            // ---- I.8 回收攻击记录必须连带回收 projectile 元数据与去重账 ----
            PMCombatSession purge = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            Check(purge.StartMatch(2), "I.8 开战");
            double t8 = Advance(1.0);
            PMCombatAttackDecision p8 = purge.RequestAttack(101u, 1u, false, 1f, 0f, t8);
            Check(TryAuthorize(purge, 101u, 1u, 1u, p8.Plan.Directions[0].X, p8.Plan.Directions[0].Z, out spec, out reason),
                "I.8 授权 key(pid=1)");
            PMProjectileKey k8 = MakeKey(101u, 1u, 1u);
            CheckEq(purge.AuthorizedProjectileCount, 1, "I.8 授权 key 数 = 1");
            Check(purge.ApplySettlement(MakeSettlement(k8, 1u, 104u), t8), "I.8 首次结算");
            CheckEq(purge.GetPlayer(104u).Hp, 760, "I.8 840-80 = 760");
            Check(purge.RequestAttack(101u, 2u, false, 1f, 0f, Advance(5001.0)).Accepted, "I.8 新攻击触发容量回收");
            CheckEq(purge.AttackRecordCount, 1, "I.8 旧记录（含被拒结论）已回收，只余新记录");
            CheckEq(purge.AuthorizedProjectileCount, 0, "I.8 旧记录的已授权 key 一并回收");
            PMCombatRejectReason reason8;
            Check(!purge.ApplySettlement(MakeSettlement(k8, 1u, 104u), Now(), out reason8)
                && reason8 == PMCombatRejectReason.UnknownAttack,
                "I.8 记录回收后旧 key 的迟到结算 → UnknownAttack（不会复活、不会再扣血）");
            CheckEq(purge.GetPlayer(104u).Hp, 760, "I.8 迟到结算零写入");
            Check(TryAuthorize(purge, 101u, 2u, 1u, p8.Plan.Directions[0].X, p8.Plan.Directions[0].Z, out spec, out reason),
                "I.8 同一 key 在新 activation 下可重新授权（旧元数据已消失）");
            CheckEq(purge.GetPlayer(101u).Mana, 30, "I.8 新攻击扣蓝一次 → 30");

            // ---- I.9 重复 key 回显的闸门（终局 / TTL 过期都不得回显 true）----
            PMCombatSession dup = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(dup.StartMatch(2), "I.9 终局回显局开战");
            double t9 = Advance(1.0);
            PMCombatAttackDecision d9 = dup.RequestAttack(104u, 1u, false, 1f, 0f, t9);
            Check(TryAuthorize(dup, 104u, 1u, 1u, d9.Plan.Directions[0].X, d9.Plan.Directions[0].Z, out spec, out reason),
                "I.9 授权 key");
            Check(dup.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t9), "I.9 首杀终局（team2）");
            Check(dup.Ended, "I.9 Ended");
            ResSnapshot dupEndedBefore = Snap(dup, 104u);
            Check(!dup.TryAuthorizeProjectile(104u,
                    MakeIntent(104u, 1u, 1u, d9.Plan.Directions[0].X, d9.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.MatchEnded,
                "I.9 终局后重复 key → MatchEnded（不再从历史授权反推当前可生成）");
            Check(spec == null, "I.9 终局回显拒绝不返回 spec");
            CheckEq(dup.AuthorizedProjectileCount, 1, "I.9 回显拒绝**不撤销**既有授权元数据");
            CheckResUnchanged("I.9 终局回显拒绝", dupEndedBefore, Snap(dup, 104u));
            Check(!TryAuthorize(dup, 104u, 1u, 2u, d9.Plan.Directions[0].X, d9.Plan.Directions[0].Z, out spec, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "I.9 终局后**新** key → MatchEnded");

            PMCombatSession dupTtl = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            Check(dupTtl.StartMatch(2), "I.9 TTL 回显局开战");
            double t9b = Advance(1.0);
            PMCombatAttackDecision d9b = dupTtl.RequestAttack(101u, 1u, false, 1f, 0f, t9b);
            Check(TryAuthorize(dupTtl, 101u, 1u, 1u, d9b.Plan.Directions[0].X, d9b.Plan.Directions[0].Z, out spec, out reason),
                "I.9 TTL 局授权");
            Advance(5001.0);
            ResSnapshot dupTtlBefore = Snap(dupTtl, 101u);
            Check(!dupTtl.TryAuthorizeProjectile(101u,
                    MakeIntent(101u, 1u, 1u, d9b.Plan.Directions[0].X, d9b.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.Expired,
                "I.9 原 activation 过 TTL 后重复 key → Expired（不再回显 true）");
            Check(spec == null, "I.9 TTL 回显拒绝不返回 spec");
            Check(!TryAuthorize(dupTtl, 101u, 1u, 2u, d9b.Plan.Directions[1].X, d9b.Plan.Directions[1].Z, out spec, out reason)
                && reason == PMCombatRejectReason.Expired, "I.9 TTL 过期后**新** key → Expired");
            CheckEq(dupTtl.AuthorizedProjectileCount, 1, "I.9 TTL 回显拒绝不新增、也不撤销授权");
            CheckResUnchanged("I.9 TTL 回显拒绝", dupTtlBefore, Snap(dupTtl, 101u));

            // ---- I.10 方向容差 1 度内外（生产路径）+ Epsilon 边界 ----
            PMCombatSession tol = NewStartedStandardSession();
            double t10 = Advance(1.0);
            PMCombatAttackDecision td = tol.RequestAttack(101u, 1u, false, 1f, 0f, t10);
            Check(td.Accepted, "I.10 雪莉攻击批准");
            float inX;
            float inZ;
            float outX;
            float outZ;
            Rotate(td.Plan.Directions[0].X, td.Plan.Directions[0].Z, 0.9f, out inX, out inZ);
            Check(TryAuthorize(tol, 101u, 1u, 1u, inX, inZ, out spec, out reason),
                "I.10 偏 0.9 度（< 1 度）→ 授权成功（reason=" + reason + "）");
            Rotate(td.Plan.Directions[1].X, td.Plan.Directions[1].Z, 1.1f, out outX, out outZ);
            Check(!TryAuthorize(tol, 101u, 1u, 2u, outX, outZ, out spec, out reason)
                && reason == PMCombatRejectReason.DirectionMismatch, "I.10 偏 1.1 度（> 1 度）→ DirectionMismatch");
            float nx;
            float nz;
            Check(PMCombatWeaponPlanner.TryNormalizeDirection(2e-6f, 0f, out nx, out nz),
                "I.10 长度 2e-6 > ZeroEpsilon(1e-6) → 可归一化");
            Check(!PMCombatWeaponPlanner.TryNormalizeDirection(1e-6f, 0f, out nx, out nz),
                "I.10 长度 1e-6 == ZeroEpsilon → 拒绝（边界含）");

            // ---- I.11 巨大 double 墙钟增量：补蓝有界、绝不溢出/挂死 ----
            PMCombatSession huge = NewStartedStandardSession();
            double t11 = Advance(1.0);
            Check(huge.RequestAttack(104u, 1u, false, 1f, 0f, t11).Accepted, "I.11 贝亚攻击（蓝 90 → 0）");
            CheckEq(huge.GetPlayer(104u).Mana, 0, "I.11 蓝 = 0");
            Check(huge.Tick(1e18), "I.11 Tick(1e18) 接受");
            CheckEq(huge.GetPlayer(104u).Mana, 90, "I.11 一次 Tick 最多 3 段 → 90（有界，不退化成巨大 while）");
            Check(!huge.Tick(1e18 - 1e6), "I.11 相对 1e18 倒退 → 拒绝");
            Check(huge.Tick(1e18), "I.11 等值 Tick 接受（非递减）");
            CheckEq(huge.GetPlayer(104u).Mana, 90, "I.11 满蓝不再增长");
            Check(huge.Tick(double.MaxValue), "I.11 Tick(double.MaxValue) 接受");
            CheckEq(huge.GetPlayer(104u).Mana, 90, "I.11 极端增量仍封顶 90");
            Check(!huge.Tick(double.PositiveInfinity), "I.11 Tick(+Inf) → 拒绝");
            Check(!huge.Tick(double.NaN), "I.11 Tick(NaN) → 拒绝");
            CheckEq(huge.GetPlayer(104u).Mana, 90, "I.11 非法 Tick 不改资源");

            // ---- I.12 断线者：新请求 / 重复 key 回显 / 已授权弹的结算全部拒绝（未终局也拦）----
            PMCombatSession inflight = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(inflight.StartMatch(4), "I.12 2v2 开战");
            double t12 = Advance(1.0);
            PMCombatAttackDecision b12 = inflight.RequestAttack(104u, 1u, false, 1f, 0f, t12);
            Check(TryAuthorize(inflight, 104u, 1u, 1u, b12.Plan.Directions[0].X, b12.Plan.Directions[0].Z, out spec, out reason),
                "I.12 104 授权");
            Check(inflight.Disconnect(104u), "I.12 104 断线");
            Check(!inflight.Ended, "I.12 两队仍各有在线玩家 → 不终局");
            CheckEq(inflight.RequestAttack(104u, 2u, false, 1f, 0f, t12).Reason,
                PMCombatRejectReason.Disconnected, "I.12 断线者新攻击 → Disconnected");
            ResSnapshot inflightOwner = Snap(inflight, 104u);
            Check(!inflight.TryAuthorizeProjectile(104u,
                    MakeIntent(104u, 1u, 1u, b12.Plan.Directions[0].X, b12.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.Disconnected,
                "I.12 断线者已授权 key 重复回显 → Disconnected（不再回显 true）");
            Check(spec == null, "I.12 断线回显拒绝不返回 spec");
            Check(!TryAuthorize(inflight, 104u, 1u, 2u, b12.Plan.Directions[0].X, b12.Plan.Directions[0].Z, out spec, out reason)
                && reason == PMCombatRejectReason.Disconnected, "I.12 断线者新 key → Disconnected");
            CheckResUnchanged("I.12 断线回显拒绝", inflightOwner, Snap(inflight, 104u));

            ResSnapshot inflightTarget = Snap(inflight, 101u);
            Check(!inflight.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t12, out reason)
                && reason == PMCombatRejectReason.Disconnected,
                "I.12 断线 attacker 的结算 → Disconnected（权威侧已离场，迟到结算不得改血量/回能）");
            CheckEq(inflight.GetPlayer(101u).Hp, 960, "I.12 目标 HP 无写");
            CheckEq(inflight.GetPlayer(104u).SuperEnergy, 0, "I.12 断线 attacker 不回能");
            CheckResUnchanged("I.12 断线结算零写入：目标", inflightTarget, Snap(inflight, 101u));
            CheckResUnchanged("I.12 断线结算零写入：attacker", inflightOwner, Snap(inflight, 104u));
            Check(!inflight.Ended, "I.12 未致死 → 未终局");

            // ---- I.13 终局结论只锁一次：重复断线不改 winner/OutcomeId ----
            PMCombatSession lockW = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(lockW.StartMatch(2), "I.13 开战");
            double t13 = Advance(1.0);
            PMCombatAttackDecision lw = lockW.RequestAttack(104u, 1u, false, 1f, 0f, t13);
            Check(TryAuthorize(lockW, 104u, 1u, 1u, lw.Plan.Directions[0].X, lw.Plan.Directions[0].Z, out spec, out reason),
                "I.13 授权");
            Check(lockW.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t13), "I.13 首杀");
            CheckEq(lockW.Outcome.OutcomeId, 1u, "I.13 OutcomeId = 1");
            CheckEq(lockW.Outcome.WinnerTeamId, 2, "I.13 winner = 2");
            CheckEq(lockW.Outcome.Reason, PMCombatEndReason.FirstKill, "I.13 reason = FirstKill");
            Check(lockW.Disconnect(101u), "I.13 终局后断线仍被记录（物理事实）");
            Check(lockW.Disconnect(104u), "I.13 第二个断线被记录");
            CheckEq(lockW.Outcome.OutcomeId, 1u, "I.13 结论不被重复断线改写（OutcomeId）");
            CheckEq(lockW.Outcome.WinnerTeamId, 2, "I.13 结论不被重复断线改写（winner）");
            CheckEq(lockW.Outcome.Reason, PMCombatEndReason.FirstKill, "I.13 结论不被重复断线改写（reason）");

            // ---- I.14 Dead 分支的 reachability：HP 归 0 与终局同一次发生 → 恒被 Ended 遮蔽 ----
            PMCombatSession dead = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(dead.StartMatch(2), "I.14 开战");
            double t14 = Advance(1.0);
            PMCombatAttackDecision d14 = dead.RequestAttack(104u, 1u, false, 1f, 0f, t14);
            Check(TryAuthorize(dead, 104u, 1u, 1u, d14.Plan.Directions[0].X, d14.Plan.Directions[0].Z, out spec, out reason),
                "I.14 授权");
            Check(dead.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), t14), "I.14 击杀");
            Check(dead.GetPlayer(101u).Dead, "I.14 101 Dead");
            Check(dead.Ended, "I.14 同时 Ended（Dead ⇒ Ended）");
            CheckEq(dead.RequestAttack(101u, 1u, false, 1f, 0f, Advance(200.0)).Reason,
                PMCombatRejectReason.MatchEnded, "I.14 死者自己的新攻击 → MatchEnded（Dead 分支被 Ended 遮蔽）");

            // ---- I.15 共享时钟水位：任何合法时钟调用（含被拒请求）都会推进它（宿主契约）----
            PMCombatSession clk = BuildSession(Epoch, R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            Check(clk.StartMatch(2), "I.15 开战");
            double t15 = Advance(1.0);
            PMCombatAttackDecision c15 = clk.RequestAttack(101u, 1u, false, 1f, 0f, t15);
            Check(TryAuthorize(clk, 101u, 1u, 1u, c15.Plan.Directions[0].X, c15.Plan.Directions[0].Z, out spec, out reason),
                "I.15 授权");
            PMProjectileKey k15 = MakeKey(101u, 1u, 1u);
            CheckEq(clk.RequestAttack(999u, 7u, false, 1f, 0f, t15 + 2000.0).Reason,
                PMCombatRejectReason.UnknownPlayer, "I.15 未知玩家的合法时钟请求 → UnknownPlayer（仍推进时钟水位）");
            Check(!clk.ApplySettlement(MakeSettlement(k15, 1u, 104u), t15 + 100.0, out reason)
                && reason == PMCombatRejectReason.InvalidClock,
                "I.15 更旧的合法结算被时钟水位拒绝（共享水位后果：宿主必须只喂同一根单调时钟）");
            CheckEq(clk.GetPlayer(104u).Hp, 840, "I.15 被拒结算零写入");
            Check(clk.ApplySettlement(MakeSettlement(k15, 1u, 104u), t15 + 2000.0, out reason),
                "I.15 时钟追平（同值）后同一结算被受理");
            CheckEq(clk.GetPlayer(104u).Hp, 760, "I.15 840-80 = 760");

            // ---- I.16 StartMatch 门槛：人数一致 **且** 所有名册成员 Connected/未 Dead ----
            PMCombatSession preStart = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(preStart.Disconnect(104u), "I.16 开战前 104 断线");
            Check(preStart.Disconnect(105u), "I.16 开战前 105 断线（team2 全断）");
            Check(!preStart.Ended, "I.16 未 StartMatch 不早判终局");
            ResSnapshot preStartBefore = Snap(preStart, 101u);
            Check(!preStart.StartMatch(4), "I.16 名册 4 人但有成员断线 → StartMatch(4) 拒（不得用名义人数补齐开局）");
            Check(!preStart.Started, "I.16 被拒后仍未开战");
            CheckResUnchanged("I.16 StartMatch 被拒零副作用", preStartBefore, Snap(preStart, 101u));
            CheckEq(preStart.RequestAttack(101u, 1u, false, 1f, 0f, Advance(1.0)).Reason,
                PMCombatRejectReason.NotStarted, "I.16 未开战：攻击 → NotStarted");
            Check(!preStart.StartMatch(2), "I.16 降成在线人数（2）也拒：expected 必须与实际名册人数一致");
            CheckEq(preStart.AttackRecordCount, 0, "I.16 未开战攻击不记账");
            Check(!preStart.GetPlayer(104u).Connected, "I.16 断线态保留");

            // 对照：同样的 4 人名册、全员在线 → 可以开战；StartMatch 本身仍不判 forfeit。
            PMCombatSession allUp = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(allUp.StartMatch(4), "I.16 全员在线 → StartMatch(4) 成功");
            Check(allUp.Started && !allUp.Ended, "I.16 StartMatch 本身不判 forfeit");
            Check(allUp.Disconnect(104u), "I.16 开战后断 104");
            Check(!allUp.Ended, "I.16 两队仍各有在线玩家 → 不终局");
            Check(allUp.Disconnect(105u), "I.16 再断 105（team2 全断）");
            Check(allUp.Ended, "I.16 此时才判终局（只有 Disconnect 会判）");
            CheckEq(allUp.Outcome.WinnerTeamId, 1, "I.16 winner = 唯一在线队伍 1");

            // ---- I.17 被拒 ID 也占水位；同 ID 冲突只计数、不翻结论 ----
            PMCombatSession wm = NewStartedStandardSession();
            double t17 = Advance(1.0);
            CheckEq(wm.RequestAttack(101u, 4u, false, 0f, 0f, t17).Reason,
                PMCombatRejectReason.InvalidAim, "I.17 ID 4 因非法方向被拒（记账）");
            CheckEq(wm.RequestAttack(101u, 4u, false, 1f, 0f, t17).Reason,
                PMCombatRejectReason.InvalidAim, "I.17 同 ID 换合法方向 → 仍返回原结论（不翻成批准）");
            CheckEq(wm.IdConflictCount, 1, "I.17 同 ID 内容冲突 +1");
            CheckEq(wm.GetPlayer(101u).Mana, 90, "I.17 未扣蓝");
            CheckEq(wm.RequestAttack(101u, 3u, false, 1f, 0f, t17).Reason,
                PMCombatRejectReason.StaleId, "I.17 低于水位的 ID 3 → StaleId（被拒 ID 也占水位）");
        }

        // ================================================================ J. 契约收口（terminal fix）
        //
        //  本段收口 `_r6_core_review.md` 里被标为「by design」、但实际是**契约缺口**的四项：
        //    · 重复 key 回显绕过 Ended / Dead / Expired（历史授权被当成当前可生成）；
        //    · Capacity 拒绝不占水位（回收后同一 ID 复活 → 扣资源却生成不出弹）；
        //    · StartMatch 只看名册人数（断线成员补齐名义人数即开局）；
        //    · ApplySettlement 不校验 attacker 连接态 / AuthorityNetId / Origin 双向一致。
        //  每个反例都带**前后资源快照**（HP / Mana / SuperEnergy / Dead / Connected），
        //  因此「拒绝 = 零副作用」是可执行的断言，而不是靠读代码相信。

        private static void TestTerminalFix()
        {
            PMProjectileSpec spec;
            PMCombatRejectReason reason;

            // ---- J.1 重复 key 回显闸门：终局后 / 原 activation 过期后 / owner 断线后一律拒 ----
            PMCombatSession ended = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BuLuoKe));
            Check(ended.StartMatch(2), "J.1 终局局开战");
            double tEnd = Advance(1.0);
            PMCombatAttackDecision e1 = ended.RequestAttack(104u, 1u, false, 1f, 0f, tEnd);
            Check(TryAuthorize(ended, 104u, 1u, 1u, e1.Plan.Directions[0].X, e1.Plan.Directions[0].Z, out spec, out reason),
                "J.1 授权 key");
            Check(ended.ApplySettlement(MakeSettlement(MakeKey(104u, 1u, 1u), 1u, 101u), tEnd), "J.1 首杀终局");
            Check(ended.Ended, "J.1 Ended");
            ResSnapshot endedBefore = Snap(ended, 104u);
            Check(!ended.TryAuthorizeProjectile(104u,
                    MakeIntent(104u, 1u, 1u, e1.Plan.Directions[0].X, e1.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.MatchEnded, "J.1 终局后重复 key → MatchEnded");
            Check(spec == null, "J.1 终局回显拒绝不返回 spec");
            CheckEq(ended.AuthorizedProjectileCount, 1, "J.1 拒绝不撤销既有授权元数据");
            CheckResUnchanged("J.1 终局回显拒绝", endedBefore, Snap(ended, 104u));

            PMCombatSession expired = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            Check(expired.StartMatch(2), "J.1 过期局开战");
            double tExp = Advance(1.0);
            PMCombatAttackDecision e2 = expired.RequestAttack(101u, 1u, false, 1f, 0f, tExp);
            Check(TryAuthorize(expired, 101u, 1u, 1u, e2.Plan.Directions[0].X, e2.Plan.Directions[0].Z, out spec, out reason),
                "J.1 过期局授权");
            Advance(5001.0);
            ResSnapshot expiredBefore = Snap(expired, 101u);
            Check(!expired.TryAuthorizeProjectile(101u,
                    MakeIntent(101u, 1u, 1u, e2.Plan.Directions[0].X, e2.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.Expired, "J.1 原 activation 过 TTL 后重复 key → Expired");
            Check(spec == null, "J.1 过期回显拒绝不返回 spec");
            CheckEq(expired.AuthorizedProjectileCount, 1, "J.1 过期回显不删除既有授权（只是不放行）");
            CheckResUnchanged("J.1 过期回显拒绝", expiredBefore, Snap(expired, 101u));

            PMCombatSession offline = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(offline.StartMatch(4), "J.1 断线局开战");
            double tOff = Advance(1.0);
            PMCombatAttackDecision e3 = offline.RequestAttack(104u, 1u, false, 1f, 0f, tOff);
            Check(TryAuthorize(offline, 104u, 1u, 1u, e3.Plan.Directions[0].X, e3.Plan.Directions[0].Z, out spec, out reason),
                "J.1 断线局授权");
            Check(offline.Disconnect(104u), "J.1 104 断线");
            Check(!offline.Ended, "J.1 两队仍在线 → 未终局");
            ResSnapshot offlineBefore = Snap(offline, 104u);
            Check(!offline.TryAuthorizeProjectile(104u,
                    MakeIntent(104u, 1u, 1u, e3.Plan.Directions[0].X, e3.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.Disconnected, "J.1 断线者重复 key → Disconnected");
            Check(spec == null, "J.1 断线回显拒绝不返回 spec");
            CheckResUnchanged("J.1 断线回显拒绝", offlineBefore, Snap(offline, 104u));

            // ---- J.2 未知 / 过期 key 与未知 owner 的结算零副作用（不占授权、不写账本、不改资源）----
            PMCombatSession unknown = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.PeiPei));
            Check(unknown.StartMatch(2), "J.2 开战");
            double tUnk = Advance(1.0);
            PMCombatAttackDecision u1 = unknown.RequestAttack(101u, 1u, false, 1f, 0f, tUnk);
            Check(TryAuthorize(unknown, 101u, 1u, 1u, u1.Plan.Directions[0].X, u1.Plan.Directions[0].Z, out spec, out reason),
                "J.2 授权 1 个 key");
            ResSnapshot unknownBefore = Snap(unknown, 104u);
            int unknownRecords = unknown.AttackRecordCount;
            int unknownAuthorized = unknown.AuthorizedProjectileCount;
            Check(!unknown.TryAuthorizeProjectile(101u,
                    MakeIntent(101u, 999u, 7u, u1.Plan.Directions[0].X, u1.Plan.Directions[0].Z), Now(), out spec, out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "J.2 未知 activation → UnknownAttack");
            CheckEq(unknown.AttackRecordCount, unknownRecords, "J.2 未知 key 不写账本");
            CheckEq(unknown.AuthorizedProjectileCount, unknownAuthorized, "J.2 未知 key 不占授权");
            CheckResUnchanged("J.2 未知 key 零副作用", unknownBefore, Snap(unknown, 104u));
            Check(!unknown.ApplySettlement(MakeSettlement(MakeKey(101u, 999u, 7u), 999u, 104u), Now(), out reason)
                && reason == PMCombatRejectReason.UnknownAttack, "J.2 未知 key 的结算 → UnknownAttack");
            CheckResUnchanged("J.2 未知 key 结算零写入", unknownBefore, Snap(unknown, 104u));
            Check(!unknown.ApplySettlement(MakeSettlement(MakeKey(999u, 1u, 1u), 1u, 104u), Now(), out reason),
                "J.2 未知 owner 的结算被拒");
            CheckResUnchanged("J.2 未知 owner 结算零写入", unknownBefore, Snap(unknown, 104u));
            CheckEq(unknown.AttackRecordCount, unknownRecords, "J.2 三条拒绝都不写账本");

            // ---- J.3 Capacity 占水位，但不影响已有 decision 的幂等查询 ----
            PMCombatSession capWm = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(104u, 4, 2, PMHeroId.BeiYa));
            Check(capWm.StartMatch(2), "J.3 开战");
            double tWm = Advance(1.0);
            Check(capWm.RequestAttack(101u, 1u, false, 1f, 0f, tWm).Accepted, "J.3 ID 1 批准");
            for (uint id = 2u; id <= 512u; id++)
            {
                capWm.RequestAttack(101u, id, false, 1f, 0f, tWm);
            }

            CheckEq(capWm.AttackRecordCount, 512, "J.3 账本满 512");
            PMCombatAttackDecision dupQuery = capWm.RequestAttack(101u, 7u, false, 1f, 0f, tWm);
            Check(dupQuery.Duplicate && !dupQuery.Accepted && dupQuery.Reason == PMCombatRejectReason.Cooldown,
                "J.3 已有记录的重复查询仍返回**原结论**（_attacks 查询在水位之前）");
            CheckEq(capWm.IdConflictCount, 0, "J.3 同内容重复不算冲突");
            CheckEq(capWm.AttackRecordCount, 512, "J.3 幂等查询不新增记录");
            CheckEq(capWm.RequestAttack(101u, 513u, false, 1f, 0f, tWm).Reason,
                PMCombatRejectReason.Capacity, "J.3 第 513 个新 ID → Capacity");
            ResSnapshot capWmBefore = Snap(capWm, 101u);
            PMCombatAttackDecision wmReuse = capWm.RequestAttack(101u, 513u, false, 1f, 0f, Advance(5001.0));
            Check(!wmReuse.Accepted && wmReuse.Reason == PMCombatRejectReason.StaleId,
                "J.3 回收后同一 ID 513 → StaleId（Capacity 占水位）");
            CheckResUnchanged("J.3 同一 ID 复活被拦", capWmBefore, Snap(capWm, 101u));
            CheckEq(capWm.GetPlayer(101u).Mana, 60, "J.3 只扣过 ID 1 的 30 蓝（没有第二次扣费）");

            // ---- J.4 StartMatch 连接态门槛；单队 / 单人仍完整可用 ----
            PMCombatSession partial = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa));
            Check(partial.Disconnect(104u), "J.4 开战前断 104");
            ResSnapshot partialBefore = Snap(partial, 101u);
            Check(!partial.StartMatch(3), "J.4 名册 3 人但有人断线 → 拒");
            Check(!partial.StartMatch(2), "J.4 降成在线人数（2）→ 拒：expected 必须与实际名册人数一致");
            Check(!partial.StartMatch(4), "J.4 expected 超名册 → 拒");
            Check(!partial.Started, "J.4 三次拒绝都没有开战");
            CheckResUnchanged("J.4 StartMatch 全部被拒零副作用", partialBefore, Snap(partial, 101u));

            PMCombatSession singleTeam = BuildSession(Epoch,
                R(101u, 1, 7, PMHeroId.XueLi), R(102u, 2, 7, PMHeroId.KeErTe));
            Check(singleTeam.StartMatch(2), "J.4 单队 2 人（无对手）仍可开战（不扩成玩法选择）");
            Check(singleTeam.Started, "J.4 单队开战成功");
            PMCombatSession singlePlayer = BuildSession(Epoch, R(101u, 1, 5, PMHeroId.XueLi));
            Check(singlePlayer.StartMatch(1), "J.4 单人局（全员在线）仍可开战");
            Check(singlePlayer.Started, "J.4 单人开局成功");

            // ---- J.5 结算身份：AuthorityNetId / Origin 双向一致；attacker 必须在线 ----
            PMCombatSession id5 = BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei));
            Check(id5.StartMatch(4), "J.5 2v2 开战");
            double tId5 = Advance(1.0);
            PMCombatAttackDecision j5 = id5.RequestAttack(104u, 1u, false, 1f, 0f, tId5);
            Check(TryAuthorize(id5, 104u, 1u, 1u, j5.Plan.Directions[0].X, j5.Plan.Directions[0].Z, out spec, out reason),
                "J.5 104 授权");
            PMProjectileKey key5 = MakeKey(104u, 1u, 1u);

            ResSnapshot id5Target = Snap(id5, 101u);
            ResSnapshot id5Attacker = Snap(id5, 104u);

            PMProjectileSettlement noAuthority = MakeSettlement(key5, 1u, 101u);
            noAuthority.AuthorityNetId = 0u;
            Check(!id5.ApplySettlement(noAuthority, tId5, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "J.5 AuthorityNetId=0（无权威身份的伪结算）→ IdentityMismatch");
            CheckResUnchanged("J.5 AuthorityNetId=0 零写入：目标", id5Target, Snap(id5, 101u));
            CheckResUnchanged("J.5 AuthorityNetId=0 零写入：attacker", id5Attacker, Snap(id5, 104u));

            PMProjectileSettlement badOrigin = MakeSettlement(key5, 1u, 101u);
            badOrigin.Origin = PMProjectileOrigin.ServerDirect;
            Check(!id5.ApplySettlement(badOrigin, tId5, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "J.5 settlement.Origin 与 Key.Origin 不一致 → IdentityMismatch");
            CheckResUnchanged("J.5 Origin 不一致零写入：目标", id5Target, Snap(id5, 101u));

            PMProjectileSettlement serverDirectKey = MakeSettlement(key5, 1u, 101u);
            serverDirectKey.Key = new PMProjectileKey(Epoch, 104u, 1u, PMProjectileOrigin.ServerDirect);
            Check(!id5.ApplySettlement(serverDirectKey, tId5, out reason) && reason == PMCombatRejectReason.IdentityMismatch,
                "J.5 ServerDirect key 的结算 → IdentityMismatch（本批只收 ClientPredicted）");
            CheckResUnchanged("J.5 ServerDirect key 零写入：目标", id5Target, Snap(id5, 101u));
            Check(!id5.Ended, "J.5 以上身份拒绝都未改变战局");

            Check(id5.Disconnect(104u), "J.5 104 断线");
            Check(!id5.Ended, "J.5 两队仍在线 → 未终局");
            ResSnapshot id5Offline = Snap(id5, 101u);
            Check(!id5.ApplySettlement(MakeSettlement(key5, 1u, 101u), Now(), out reason)
                && reason == PMCombatRejectReason.Disconnected, "J.5 attacker 断线 → 结算 Disconnected");
            CheckEq(id5.GetPlayer(101u).Hp, 960, "J.5 目标 HP 无写");
            CheckEq(id5.GetPlayer(104u).SuperEnergy, 0, "J.5 断线 attacker 不回能");
            CheckResUnchanged("J.5 断线结算零写入：目标", id5Offline, Snap(id5, 101u));

            // 只拦断线 owner：同队（team2）在线成员 105 的弹照常结算。
            PMCombatAttackDecision j5b = id5.RequestAttack(105u, 1u, false, 1f, 0f, Advance(100.0));
            Check(j5b.Accepted, "J.5 同队在线成员 105 仍可攻击");
            Check(TryAuthorize(id5, 105u, 1u, 1u, j5b.Plan.Directions[0].X, j5b.Plan.Directions[0].Z, out spec, out reason),
                "J.5 105 授权");
            Check(id5.ApplySettlement(MakeSettlement(MakeKey(105u, 1u, 1u), 1u, 101u), Now(), out reason),
                "J.5 在线成员的弹正常结算（只拦断线 owner）");
            CheckEq(id5.GetPlayer(101u).Hp, 310, "J.5 960-650 = 310");
        }

        // ================================================================ 辅助

        // ---------------------------------------------------------------- 资源快照（拒绝 = 零副作用的可执行证据）

        /// <summary>玩家级资源/存活快照（深拷贝值语义）。账本计数单独用 AttackRecordCount 断言，
        /// 因为回收（Purge）本来就会合法地改变记录数，不能和「拒绝无副作用」混在一起判。</summary>
        private struct ResSnapshot
        {
            public int Hp;
            public int Mana;
            public int SuperEnergy;
            public bool Dead;
            public bool Connected;
        }

        private static ResSnapshot Snap(PMCombatSession session, uint netId)
        {
            ResSnapshot snapshot = new ResSnapshot();
            PMCombatPlayerSnapshot player = session.GetPlayer(netId);
            if (player != null)
            {
                snapshot.Hp = player.Hp;
                snapshot.Mana = player.Mana;
                snapshot.SuperEnergy = player.SuperEnergy;
                snapshot.Dead = player.Dead;
                snapshot.Connected = player.Connected;
            }

            return snapshot;
        }

        private static void CheckResUnchanged(string what, ResSnapshot before, ResSnapshot after)
        {
            CheckEq(after.Hp, before.Hp, what + "：HP 不变");
            CheckEq(after.Mana, before.Mana, what + "：Mana 不变");
            CheckEq(after.SuperEnergy, before.SuperEnergy, what + "：SuperEnergy 不变");
            Check(after.Dead == before.Dead,
                what + "：Dead 不变（期望 " + before.Dead + "，实际 " + after.Dead + "）");
            Check(after.Connected == before.Connected,
                what + "：Connected 不变（期望 " + before.Connected + "，实际 " + after.Connected + "）");
        }

        private sealed class RosterEntry
        {
            public uint NetId;
            public int Uid;
            public int Team;
            public int Hero;
        }

        private static RosterEntry R(uint netId, int uid, int team, int hero)
        {
            RosterEntry entry = new RosterEntry();
            entry.NetId = netId;
            entry.Uid = uid;
            entry.Team = team;
            entry.Hero = hero;
            return entry;
        }

        private static PMCombatSession BuildSession(uint epoch, params RosterEntry[] entries)
        {
            PMCombatSession session = new PMCombatSession(epoch);
            for (int i = 0; i < entries.Length; i++)
            {
                Check(session.AddPlayer(entries[i].NetId, entries[i].Uid, entries[i].Team, entries[i].Hero),
                    "名册登记 " + entries[i].NetId);
            }

            return session;
        }

        private static PMCombatSession NewStandardSession()
        {
            return BuildSession(Epoch,
                R(101u, 1, 1, PMHeroId.XueLi), R(102u, 2, 1, PMHeroId.KeErTe), R(103u, 3, 1, PMHeroId.BuLuoKe),
                R(104u, 4, 2, PMHeroId.BeiYa), R(105u, 5, 2, PMHeroId.PeiPei), R(106u, 6, 2, PMHeroId.RuiKe));
        }

        private static PMCombatSession NewStartedStandardSession()
        {
            PMCombatSession session = NewStandardSession();
            Check(session.StartMatch(6), "标准 6 人名册 StartMatch(6)");
            return session;
        }

        private static PMProjectileKey MakeKey(uint owner, uint activationId, uint projectileId)
        {
            return new PMProjectileKey(Epoch, owner, projectileId, PMProjectileOrigin.ClientPredicted);
        }

        private static PMProjectileSpawnIntent MakeIntent(uint owner, uint activationId, uint projectileId,
            float dirX, float dirZ)
        {
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = MakeKey(owner, activationId, projectileId);
            intent.ActivationId = activationId;
            intent.Position = new PMVector3(3f, 1f, -2f);
            intent.Direction = new PMVector3(dirX, 0f, dirZ);
            intent.Yaw = 0f;
            intent.PredictionMs = 0;
            return intent;
        }

        private static PMProjectileSettlement MakeSettlement(PMProjectileKey key, uint activationId,
            params uint[] targets)
        {
            PMProjectileSettlement settlement = new PMProjectileSettlement();
            settlement.Key = key;
            settlement.OwnerNetId = key.OwnerNetId;
            settlement.ActivationId = activationId;
            settlement.AuthorityNetId = 4242u;
            settlement.Origin = key.Origin;
            settlement.WallTimeMs = Now();
            settlement.StopOnHit = true;
            settlement.HitCount = targets.Length;
            settlement.Hits = new PMProjectileValidatedHit[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                settlement.Hits[i].TargetNetId = targets[i];
                settlement.Hits[i].TargetStreamVersion = 3u;
                settlement.Hits[i].ImpactPoint = new PMVector3(1f, 1f, 1f);
            }

            return settlement;
        }

        private static bool TryAuthorize(PMCombatSession session, uint owner, uint activationId,
            uint projectileId, float dirX, float dirZ, out PMProjectileSpec spec,
            out PMCombatRejectReason reason)
        {
            return session.TryAuthorizeProjectile(owner,
                MakeIntent(owner, activationId, projectileId, dirX, dirZ), Now(), out spec, out reason);
        }

        private static bool IsSameAsBase(PMVector3 direction, float baseX, float baseZ)
        {
            return IsSameAsBase(direction, baseX, baseZ, 1e-3f);
        }

        private static bool IsSameAsBase(PMVector3 direction, float baseX, float baseZ, float tolerance)
        {
            float length = (float)Math.Sqrt((double)baseX * baseX + (double)baseZ * baseZ);
            float nx = baseX / length;
            float nz = baseZ / length;
            return Math.Abs(direction.X - nx) <= tolerance && Math.Abs(direction.Z - nz) <= tolerance;
        }

        private static void CheckSameDirection(PMVector3 direction, float baseX, float baseZ, string what)
        {
            Check(IsSameAsBase(direction, baseX, baseZ), what + "（实际 " + direction.X + "," + direction.Z + "）");
        }

        private static double AngleDeg(PMVector3 direction, float baseX, float baseZ)
        {
            double baseAngle = Math.Atan2(baseZ, baseX);
            double dirAngle = Math.Atan2(direction.Z, direction.X);
            double delta = (dirAngle - baseAngle) * 180.0 / Math.PI;
            while (delta > 180.0)
            {
                delta -= 360.0;
            }

            while (delta < -180.0)
            {
                delta += 360.0;
            }

            return delta;
        }

        private static void Rotate(float dirX, float dirZ, float degrees, out float outX, out float outZ)
        {
            double rad = degrees * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double x = (double)dirX * cos - (double)dirZ * sin;
            double z = (double)dirX * sin + (double)dirZ * cos;
            double length = Math.Sqrt(x * x + z * z);
            outX = (float)(x / length);
            outZ = (float)(z / length);
        }

        private static void Section(string name, Action test)
        {
            Console.WriteLine("[" + name + "]");
            int beforePass = _passed;
            int beforeFail = _failures.Count;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                _failures.Add(name + " 抛出异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine("    通过 " + (_passed - beforePass) + " / 失败 " + (_failures.Count - beforeFail));
        }

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
            }
            else
            {
                _failures.Add(what);
            }
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
            Check(Math.Abs(actual - expected) <= 1e-3f, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(double actual, double expected, string what)
        {
            Check(Math.Abs(actual - expected) <= 0.05, what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq<T>(T actual, T expected, string what) where T : struct
        {
            Check(actual.Equals(expected), what + "（期望 " + expected + "，实际 " + actual + "）");
        }
    }
}
