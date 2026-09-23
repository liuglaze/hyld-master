// ============================================================================
//  PMCombatWeaponPlanner —— R6-A1：正式直线攻击「计划」的唯一生成点（DS 与 AP 共用）
// ============================================================================
//
//  契约来源
//  --------
//  · Docs/plans/net-r6-combat-contract.md「D-R6-01/02」与「A1 纯核心」；
//  · Docs/plans/_r6_combat_semantics_survey.md §1.1–1.7（共享数值 / PMBattleSim / 服务端语义）；
//  · Client/Assets/Scripts/PMCombat/PMCombatContracts.cs（主 Agent 冻结，只读）。
//
//  为什么需要它
//  ------------
//  「玩家按了一次攻击键，这一次攻击到底打出几颗弹、朝哪飞、飞多快、能飞多久、伤害多少」
//  在旧实现里是**两端各写一遍**的：服务端 `BattleController.Bullets.cs` 走「每次发射数」，
//  客户端 `HYLDBulletManger.BuildVisualAttackSpec` 走「一轮总数」并按 EachShotInterval 分轮，
//  于是同一次点击两边弹数相差 2~6 倍（调查报告 §1.4）。R6 把「一次攻击的权威口径」
//  收敛到本文件：**普通攻击 = BulletCountPerShot，大招 = BulletCountTotal**，
//  由共享 <see cref="BattleNumericConfig.ResolveAttack"/> 解释，两端不可能再各理解一套。
//
//  三条硬边界（本文件不得越过）
//  --------------------------
//  1) **零引擎依赖**：只引用 PMNet.Shared（数值/公式）与 PMProjectile（spec 载体）。
//     不引 UnityEngine / R3 / 任何宿主类型 —— `Tools/PMCombatCoreCheck` 用
//     netstandard2.0 + C# 7.3 零 Unity 编译本文件，把这条纪律变成可自动检查的门。
//  2) **只读共享配置**：绝不回写 <see cref="BattleNumericConfig"/>（数值源由两端共用，
//     客户端改一次就会污染服务端）。
//  3) **fail closed，不偷偷降级**：抛物线 / 没有大招配置 / 数值非法一律拒绝并给出原因，
//     绝不用「兜底数值」继续生成计划 —— 那等于把「配置缺失」变成「悄悄换了一把枪」。
//
//  数值口径（逐条对应契约，改动前先读契约）
//  ----------------------------------------
//  · 弹数 N = ResolvedAttack.SpawnBulletCount；N ≤ 64（PMCombatLimits.MaxBulletsPerAttack）。
//  · 方向 = PMBattleSim.SpreadDirection（**复用**权威圆弧公式）：±LaunchAngle/2 首尾包含；
//    N ≤ 1 或 LaunchAngle == 0 时**原样返回基准方向**。
//    注意：这正是服务端「柯尔特/格尔多弹同向重叠」的既有语义（调查 §1.4）——
//    旧客户端那种「垂直于弹道平移排布」的表现编排**不在这里复活**。
//  · Spec.SpeedMps = ResolvedAttack.BulletSpeed。
//  · Spec.LifetimeMs = ceil(ShootDistance / BulletSpeed * 1000)，且必须 ≤ 2500ms：
//    攻击记录 TTL 是 5000ms，需要覆盖「授权→真实命中回流」的 pending/墓碑窗口，
//    lifetime 一旦超过 TTL 就会出现「弹还在飞、记录已被回收」。当前冻结表的实际上界见报告。
//  · Spec.RadiusM = 0.1m（PMCombatLimits.ProjectileRadiusM）。**不复用 ShootWidth**：
//    ShootWidth 是表现宽度/爆炸半径，与 R5 的胶囊-半径判定无关（调查 §1.1 字段语义纠偏）。
//  · Spec.StopOnHit = true：直线弹命中即停（R6 第一批不做穿透）。
//  · Plan.Damage = ResolvedAttack.BulletDamage，**damage 0 原样保留**（斯派克），
//    不擅自填值：这是配置事实，不是「配置缺失」。
//  · Plan.FireIntervalMs = max(100, ceil(EachShotInterval * 1000))：这是**下一次攻击**的
//    最小间隔（安全预算），不是「一轮弹幕播放时长」；把 EachShotInterval 当弹幕总时长
//    是旧客户端的表现语义，R6 不采用。
//  · Plan.ManaCost = 普通攻击 NormalAttackManaCost；大招为 0（大招只消耗能量）。
//
//  诚实口径
//  --------
//  本文件只产出「计划」，**不扣资源、不记账、不认识玩家**（那是 PMCombatSession 的事），
//  也不判断枪口位置/命中（那是 R5 与宿主的事）。
// ============================================================================

using System;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Shared;

namespace PMNet.Combat
{
    /// <summary>
    /// 一次攻击的权威计划生成器。两端共用同一份解释。
    /// </summary>
    public static class PMCombatWeaponPlanner
    {
        /// <summary>
        /// 计划允许的最大弹体寿命（毫秒）。超过即 <see cref="PMCombatRejectReason.InvalidConfig"/>。
        ///
        /// <para>上界由「攻击记录 TTL 必须覆盖 pending + 墓碑」推出：R5 的 pending TTL 是 2000ms，
        /// 加上墓碑窗口与回流延迟仍应远小于记录 TTL（<see cref="PMCombatLimits.AttackRecordTtlMs"/>）。</para>
        /// </summary>
        public const int MaxPlanLifetimeMs = 2500;

        /// <summary>方向容差的余弦阈值（<see cref="PMCombatLimits.DirectionToleranceDegrees"/>）。</summary>
        private static readonly float CosTolerance =
            (float)Math.Cos((double)PMCombatLimits.DirectionToleranceDegrees * Math.PI / 180.0);

        /// <summary>
        /// 生成一次攻击的计划。
        /// </summary>
        /// <param name="heroId">英雄编号，**必须**在 <c>0..PMHeroId.Count-1</c>（不允许走共享 Get 的兜底值）。</param>
        /// <param name="isSuper">是否大招。</param>
        /// <param name="aimX">瞄准方向的世界 X 分量（世界 XZ 平面；未归一化，允许任意非零长度）。</param>
        /// <param name="aimZ">瞄准方向的世界 Z 分量。</param>
        /// <param name="plan">成功时为**新建**的深拷贝计划；失败时为 null。</param>
        /// <param name="reason">失败原因；成功时为 <see cref="PMCombatRejectReason.None"/>。</param>
        /// <returns>生成成功返回 true。</returns>
        public static bool TryBuild(int heroId, bool isSuper, float aimX, float aimZ,
            out PMCombatAttackPlan plan, out PMCombatRejectReason reason)
        {
            plan = null;
            reason = PMCombatRejectReason.None;

            // 英雄编号只允许共享表内的合法值。刻意不用 BattleNumericConfig.Get 的兜底：
            // 「未知英雄」绝不能变成「用兜底数值打一枪」（契约 A1：hero 必须 0..19）。
            if (heroId < 0 || heroId >= PMHeroId.Count)
            {
                reason = PMCombatRejectReason.InvalidHero;
                return false;
            }

            // 瞄准方向：必须 finite 且非零（0 长度无法归一化成方向）。
            if (!IsFinite(aimX) || !IsFinite(aimZ))
            {
                reason = PMCombatRejectReason.InvalidAim;
                return false;
            }

            float aimLength = (float)Math.Sqrt((double)aimX * (double)aimX + (double)aimZ * (double)aimZ);
            if (!IsFinite(aimLength) || aimLength <= PMBattleSim.ZeroEpsilon)
            {
                reason = PMCombatRejectReason.InvalidAim;
                return false;
            }

            float baseDirX = (float)((double)aimX / (double)aimLength);
            float baseDirZ = (float)((double)aimZ / (double)aimLength);

            // 大招：没有子弹型大招配置（TryGetSuper == false）时明确不支持，**不降级成普通攻击**。
            if (isSuper)
            {
                SuperNumeric superProbe;
                if (!BattleNumericConfig.TryGetSuper(heroId, out superProbe))
                {
                    reason = PMCombatRejectReason.UnsupportedAttack;
                    return false;
                }
            }

            ResolvedAttack resolved = BattleNumericConfig.ResolveAttack(heroId, isSuper);

            // 抛物线弹道（巴利/爆破麦克/迪克普通攻击、帕姆大招）不在 R6 第一批的直线弹范围内。
            // fail closed：拒绝，且调用方**不得**在这里扣任何资源。
            if (resolved.IsParabola)
            {
                reason = PMCombatRejectReason.UnsupportedAttack;
                return false;
            }

            int bulletCount = resolved.SpawnBulletCount;
            if (bulletCount <= 0 || bulletCount > PMCombatLimits.MaxBulletsPerAttack)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            float speed = resolved.BulletSpeed;
            float distance = resolved.ShootDistance;
            if (!IsFinite(speed) || speed <= 0f || !IsFinite(distance) || distance <= 0f)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            float launchAngle = resolved.LaunchAngle;
            if (!IsFinite(launchAngle) || launchAngle < 0f)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            float eachShotInterval = resolved.EachShotInterval;
            if (!IsFinite(eachShotInterval) || eachShotInterval < 0f)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            // 伤害为 0 是配置事实（斯派克），保留；负数才是坏配置。
            if (resolved.BulletDamage < 0)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            // 寿命：ceil(dist / speed * 1000)。用 double 计算再收敛，避免 float 溢出错值。
            //
            // 关键：**必须在 double 域内先比较、再转 int**。C# 把超出 int 范围的 double
            // 转 int 的结果是「未指定」（x64 上实际得到 int.MinValue），于是 huge lifetime
            // 会被下面的 `lifetimeMs < 1` 钳成 1ms —— 坏配置反而变成一张「合法」的 1ms 计划，
            // 本文件的 fail-closed 承诺失效。（本批冻结表的最大寿命 1000ms 走不到这条路径，
            // 但门禁一旦只在当前数值表上成立，就不再是门禁。）
            double lifetimeMsRaw = (double)distance / (double)speed * 1000.0;
            if (!IsFiniteDouble(lifetimeMsRaw) || lifetimeMsRaw > (double)MaxPlanLifetimeMs)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            int lifetimeMs = (int)Math.Ceiling(lifetimeMsRaw);
            if (lifetimeMs < 1)
            {
                lifetimeMs = 1;
            }

            // 间隔同理：先在 double 域内钳到 int 范围再取整，避免 huge EachShotInterval
            // 溢出成 int.MinValue 后被下面的「下限 100ms」改写成 100ms
            // （等于把「几乎不能再攻击」静默变成「随时能攻击」，与「配置异常 fail-closed」相反）。
            double intervalMsRaw = ExpectedIntervalMs(eachShotInterval);
            if (!IsFiniteDouble(intervalMsRaw) || intervalMsRaw > (double)int.MaxValue)
            {
                intervalMsRaw = (double)int.MaxValue;
            }

            int fireIntervalMs = (int)Math.Ceiling(intervalMsRaw);
            if (fireIntervalMs < PMCombatLimits.MinFireIntervalMs)
            {
                fireIntervalMs = PMCombatLimits.MinFireIntervalMs;
            }

            PMVector3[] directions = new PMVector3[bulletCount];
            for (int i = 0; i < bulletCount; i++)
            {
                float dirX;
                float dirZ;
                PMBattleSim.SpreadDirection(baseDirX, baseDirZ, launchAngle, bulletCount, i,
                    out dirX, out dirZ);

                if (!IsFinite(dirX) || !IsFinite(dirZ)
                    || (dirX == 0f && dirZ == 0f))
                {
                    // 退化方向（理论上仅在坏配置下发生）：整次攻击 fail closed，不生成半张计划。
                    reason = PMCombatRejectReason.InvalidConfig;
                    return false;
                }

                directions[i] = new PMVector3(dirX, 0f, dirZ);
            }

            HeroNumeric hero = BattleNumericConfig.Get(heroId);
            int manaCost = isSuper ? 0 : hero.NormalAttackManaCost;
            if (manaCost < 0)
            {
                reason = PMCombatRejectReason.InvalidConfig;
                return false;
            }

            PMProjectileSpec spec = new PMProjectileSpec();
            spec.SpeedMps = speed;
            spec.RadiusM = PMCombatLimits.ProjectileRadiusM;
            spec.LifetimeMs = lifetimeMs;
            spec.DelayDestroyMs = 0;
            spec.StopOnHit = true;
            spec.HideOnStop = true;
            spec.SkipFlyingTrajectoryValidation = false;

            PMCombatAttackPlan built = new PMCombatAttackPlan();
            built.HeroId = heroId;
            built.IsSuper = isSuper;
            built.Damage = resolved.BulletDamage;
            built.ManaCost = manaCost;
            built.FireIntervalMs = fireIntervalMs;
            built.Spec = spec;
            built.Directions = directions;

            plan = built;
            return true;
        }

        /// <summary>
        /// 两个世界方向是否落在方向容差内（两端共用的唯一比较口径）。
        ///
        /// <para>用点积与余弦阈值比较，而不是 <c>acos</c>：小角度下 <c>acos</c> 的浮点误差
        /// 会明显放大，而「同一条方向被重复上报」必须稳定判定为匹配。</para>
        /// </summary>
        public static bool IsSameDirection(float ax, float az, float bx, float bz)
        {
            if (!IsFinite(ax) || !IsFinite(az) || !IsFinite(bx) || !IsFinite(bz))
            {
                return false;
            }

            double lenA = Math.Sqrt((double)ax * (double)ax + (double)az * (double)az);
            double lenB = Math.Sqrt((double)bx * (double)bx + (double)bz * (double)bz);
            if (!(lenA > (double)PMBattleSim.ZeroEpsilon) || !(lenB > (double)PMBattleSim.ZeroEpsilon))
            {
                return false;
            }

            float dot = (float)((double)ax / lenA) * (float)((double)bx / lenB)
                + (float)((double)az / lenA) * (float)((double)bz / lenB);
            if (dot > 1f)
            {
                dot = 1f;
            }
            else if (dot < -1f)
            {
                dot = -1f;
            }

            return dot >= CosTolerance;
        }

        /// <summary>把任意非零有限世界方向归一化（失败返回 false 且输出为 0）。</summary>
        public static bool TryNormalizeDirection(float x, float z, out float dirX, out float dirZ)
        {
            dirX = 0f;
            dirZ = 0f;
            if (!IsFinite(x) || !IsFinite(z))
            {
                return false;
            }

            double length = Math.Sqrt((double)x * (double)x + (double)z * (double)z);
            if (!(length > (double)PMBattleSim.ZeroEpsilon))
            {
                return false;
            }

            dirX = (float)((double)x / length);
            dirZ = (float)((double)z / length);
            return true;
        }

        /// <summary>netstandard2.0 没有 <c>float.IsFinite</c>，这里收敛成唯一写法。</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// double 版本的有限判定。用于「先验后转」的中间量：超出 int 范围的 double 转 int
        /// 是未指定结果，绝不能让它在钳位之前进入整数域。
        /// </summary>
        private static bool IsFiniteDouble(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// 把「每次发射间隔（秒）」换算成毫秒。
        ///
        /// <para>先用 1e-4 ms 归整再取 ceil：float32 的二进制表示会让十进制写法多出 1ms
        /// （<c>0.15f = 0.150000006</c> → 严格 ceil 得 151，<c>0.1f = 0.100000001</c> → 101），
        /// 而配置语义明显是 150ms / 100ms。归整只消除表示误差，不会掩盖真实的更细间隔。</para>
        /// </summary>
        private static double ExpectedIntervalMs(float eachShotIntervalSeconds)
        {
            double rawMs = (double)eachShotIntervalSeconds * 1000.0;
            return Math.Round(rawMs, 4);
        }
    }
}
