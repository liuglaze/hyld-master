// 战斗数值的单一事实源（Shared）：服务端与 Unity 客户端编译同一份源码。
//
// 来源与本轮迁移的依据见 Docs/plans/net-architecture-migration.md 的 P3'-2 一节。
// 本文件由一次性脚本从「客户端英雄表 + 服务端 HeroConfig 的实际值」生成后，
// 转为**手工维护**：后续策划改数值直接改这里，两端自动一致。

using System.Collections.Generic;

namespace PMNet.Shared
{
    /// <summary>
    /// 英雄编号常量。
    ///
    /// <para>
    /// 值与 <c>SocketProto.Hero</c>（权威 proto 枚举）以及客户端手写枚举 <c>HeroName</c> 完全一致，
    /// 由 <c>Tools/PMSharedConfigCheck</c> 逐值校验 —— 三者任何一处漂移都会让门禁失败。
    /// </para>
    ///
    /// <para>
    /// **为什么用 int 而不是枚举键**：这样本文件零依赖（连 Google.Protobuf 都不需要），
    /// 因而可以被最严格的语言门禁编译；而 proto 的编号本来就是它在网络上的身份，
    /// 不会因为将来 socket 层换一套生成产物而失效。
    /// </para>
    /// </summary>
    public static class PMHeroId
    {
        public const int XueLi       =  0;   // 雪莉
        public const int KeErTe      =  1;   // 柯尔特
        public const int PeiPei      =  2;   // 佩佩
        public const int PanNi       =  3;   // 潘妮
        public const int BaLi        =  4;   // 巴利
        public const int GongNiu     =  5;   // 公牛
        public const int DaLiEr      =  6;   // 达里尔
        public const int GeEr        =  7;   // 格尔
        public const int BuLuoKe     =  8;   // 布洛克
        public const int BaoPoMaiKe  =  9;   // 爆破麦克
        public const int ABo         = 10;   // 阿渤
        public const int DiKe        = 11;   // 迪克
        public const int BeiYa       = 12;   // 贝亚
        public const int TaLa        = 13;   // 塔拉
        public const int MaiKeSi     = 14;   // 麦克斯
        public const int SiPaiKe     = 15;   // 斯派克
        public const int HeiYa       = 16;   // 黑鸦
        public const int LiAng       = 17;   // 里昂
        public const int PaMu        = 18;   // 帕姆
        public const int RuiKe       = 19;   // 瑞科

        public const int Count = 20;
    }

    /// <summary>
    /// 单个英雄的战斗数值。
    ///
    /// <para>**只读配置**。这里绝不放运行期状态（当前血量/蓝量/能量、道具加成后的移速等）——
    /// 那些属于玩家状态，放进来就等于让「配置」变成可变全局变量。</para>
    /// </summary>
    public struct HeroNumeric
    {
        /// <summary>本局实际使用的最大生命值（= 旧服务端 HeroConfig 的实际值，切换后行为不变）。</summary>
        public int MaxHp;

        /// <summary>
        /// 设计生命值（设计稿上的真实血量）。**当前不生效**，仅作记录。
        ///
        /// <para>旧服务端为让测试期子弹累积量可控而临时降血，但降幅在各英雄间并不统一
        /// （实测比值 1.96~6.00），无法用单一系数还原，因此这里原样保留设计值，
        /// 待对象池/复制优化后决定是否恢复。</para>
        /// </summary>
        public int DesignHp;

        /// <summary>
        /// 移动速度（单位/秒）。**逐英雄**。
        ///
        /// <para>旧服务端用单一常量 3.9，而客户端是逐英雄的（黑鸦 4.2 / 麦克斯 4.08 / 柯尔特 4.05 /
        /// 里昂 3.96 / 帕姆 3.78）。两侧不一致会让权威位置与预测位置持续偏差、反复触发位置校正。
        /// 本表以客户端设计值为准，两端统一。</para>
        /// </summary>
        public float MoveSpeed;

        /// <summary>普通攻击射程（服务端用作子弹最大飞行距离）。</summary>
        public float ShootDistance;

        /// <summary>
        /// 弹体表现宽度 / 爆炸半径。**仅表现层使用**。
        ///
        /// <para>⚠ 它不是服务端命中判定半径 —— 那是 <see cref="ServerHitRadius"/>。
        /// 两者语义不同（本字段值域 0~4，判定半径全英雄 0.8），早期服务端注释把二者混为一谈，已纠正。</para>
        /// </summary>
        public float ShootWidth;

        /// <summary>子弹速度（单位/秒）。</summary>
        public float BulletSpeed;

        /// <summary>单颗子弹伤害。</summary>
        public int BulletDamage;

        /// <summary>普通攻击「每次发射」的弹数（对应旧服务端的 BulletCount）。</summary>
        public int BulletCountPerShot;

        /// <summary>普通攻击一轮的总弹数（客户端 bulletCount；影响表现层弹数与射击时序）。</summary>
        public int BulletCountTotal;

        /// <summary>扇形总角度（度）。0 = 单发直线。</summary>
        public float LaunchAngle;

        /// <summary>同一轮内相邻两次发射的间隔（秒）。</summary>
        public float EachShotInterval;

        /// <summary>装弹 / 回蓝一拍所需秒数（对应旧服务端 _reloadSeconds）。</summary>
        public float ReloadSeconds;

        /// <summary>是否抛物线弹道（跳过直线碰撞，走落点结算）。</summary>
        public bool IsParabola;

        /// <summary>抛射高度（仅抛物线弹道有意义）。</summary>
        public float High;

        /// <summary>
        /// **服务端命中判定半径**：以玩家位置为球心的「球-点距离」判定
        /// （<c>distance(bullet, player) &lt;= 本值</c>）。
        ///
        /// <para>全英雄同值 0.8，是有意的：它决定「子弹多容易擦到人」，与英雄体型/弹体宽度无关。
        /// 保留为独立字段而不是复用 <see cref="ShootWidth"/>，是为了不让「统一数值源」这件事
        /// 顺带改掉命中手感。</para>
        /// </summary>
        public float ServerHitRadius;

        /// <summary>普通攻击的蓝量消耗。</summary>
        public int NormalAttackManaCost;

        /// <summary>普通攻击后回复的蓝量（麦克斯 = 3）。</summary>
        public int NormalAttackManaRecover;
    }

    /// <summary>大招的数值覆写。字段为 <c>-1</c> 时表示「沿用该英雄的普通攻击值」。</summary>
    public struct SuperNumeric
    {
        public float ShootDistance;

        public float ShootWidth;

        /// <summary>大招一轮的总弹数（注意：大招用「总数」，而普通攻击用「每次发射数」，语义不同）。</summary>
        public int BulletCountTotal;

        /// <summary>-1 = 沿用普通攻击伤害。</summary>
        public int BulletDamage;

        /// <summary>-1 = 沿用普通攻击扇形角。</summary>
        public float LaunchAngle;

        /// <summary>-1 = 沿用普通攻击弹速。</summary>
        public float BulletSpeed;

        /// <summary>-1 = 沿用普通攻击的每次发射数。</summary>
        public int BulletCountPerShot;

        /// <summary>-1 = 沿用普通攻击的发射间隔。</summary>
        public float EachShotInterval;

        public bool IsParabola;

        /// <summary>-1 = 沿用普通攻击高度。</summary>
        public float High;
    }

    /// <summary>
    /// **已解析**的一次攻击规格：把普通攻击与大招的差异在进入仿真前就消掉。
    ///
    /// <para>
    /// 为什么需要它：普通攻击与大招在「本次生成多少颗弹」上用的是**不同字段** ——
    /// 普通攻击用「每次发射数」（雪莉 5），而大招用「一轮总数」（雪莉大招 40）。
    /// 旧服务端把两者塞进同一个字段名 <c>BulletCount</c>，靠「调用哪张表」隐含区分，
    /// 是很容易改错的一处。现在由 <see cref="BattleNumericConfig.ResolveAttack"/> 统一解释，
    /// 两端不可能各写一套理解。
    /// </para>
    /// </summary>
    public struct ResolvedAttack
    {
        /// <summary>本次攻击的射程（服务端用作子弹最大飞行距离）。</summary>
        public float ShootDistance;

        /// <summary>弹体表现宽度 / 爆炸半径（客户端表现用；服务端判定用 ServerHitRadius）。</summary>
        public float ShootWidth;

        /// <summary>子弹速度。</summary>
        public float BulletSpeed;

        /// <summary>单颗子弹伤害。</summary>
        public int BulletDamage;

        /// <summary>
        /// **本次调用要生成的弹数**。
        ///
        /// <para>普通攻击 = <see cref="BulletCountPerShot"/>；大招 = <see cref="BulletCountTotal"/>。
        /// 这是服务端一次攻击包对应的生成量，与客户端的表现节奏无关。</para>
        /// </summary>
        public int SpawnBulletCount;

        /// <summary>一轮的总弹数（客户端表现层按「每次发射数 × 轮数」播放，需要这个值）。</summary>
        public int BulletCountTotal;

        /// <summary>每次发射的弹数（已按大招的 -1 继承语义解析过）。</summary>
        public int BulletCountPerShot;

        /// <summary>扇形总角度（度）。</summary>
        public float LaunchAngle;

        /// <summary>同一轮内相邻两次发射的间隔（秒）。</summary>
        public float EachShotInterval;

        /// <summary>是否抛物线弹道。</summary>
        public bool IsParabola;

        /// <summary>抛射高度。</summary>
        public float High;
    }

    /// <summary>
    /// 战斗数值的**单一事实源**。
    ///
    /// <para>
    /// 本类被服务端与 Unity 客户端**同时编译**（服务端用 <c>&lt;Compile Include&gt;</c> 链接同一份源码，
    /// 与 PMNet 核的做法一致），因此两端不可能再出现数值漂移。
    /// </para>
    ///
    /// <para>
    /// **约束**：本文件不得引用 <c>UnityEngine</c>（否则服务端编不过），也不得引用任何生成代码。
    /// 它由 <c>Tools/PMSharedConfigCheck</c> 以 netstandard2.0 + C# 7.3（Unity 2019.4 的天花板）编译校验。
    /// </para>
    ///
    /// <para>
    /// **设计原则**：这里只有「配置」，没有「运行期状态」。每名玩家的当前血量/蓝量/能量、
    /// 道具加成后的移速等都属于玩家状态，绝不能写回本表。
    /// </para>
    /// </summary>
    public static class BattleNumericConfig
    {
        /// <summary>蓝量上限（旧实现有 4 处重复字面量 90，此处收敛为唯一定义）。</summary>
        public const int ManaMax = 90;

        /// <summary>每段回蓝量。</summary>
        public const int ManaPerSegment = 30;

        /// <summary>普通攻击默认蓝耗（贝亚为 90，见英雄表）。</summary>
        public const int DefaultAttackManaCost = 30;

        /// <summary>大招能量上限。</summary>
        public const int SuperEnergyMax = 200;

        /// <summary>
        /// 单个逻辑帧的时长（秒）= 62.5Hz。
        ///
        /// <para>旧实现有三个独立声明点（服务端 <c>Battle.cs</c>、<c>ServerConfig.frameTime</c>、
        /// 客户端 <c>ConstValue.frameTime</c>），此处收敛为唯一定义。</para>
        /// </summary>
        public const float FrameTimeSec = 0.016f;

        /// <summary>单帧毫秒数（由 <see cref="FrameTimeSec"/> 派生，不要另写常量）。</summary>
        public const int FrameTimeMs = 16;

        /// <summary>服务端位置校正阈值：权威位置与客户端上报位置之差超过它即回 ack_good_move=false。</summary>
        public const float MovementMaxPositionError = 0.6f;

        /// <summary>服务端可接受的上行攻击延迟（帧）。</summary>
        public const int MaxAcceptableAttackDelay = 8;

        private static readonly HeroNumeric[] _heroes = new HeroNumeric[PMHeroId.Count]
        {
            //  0  雪莉
            new HeroNumeric
            {
                MaxHp = 960, DesignHp = 4680, MoveSpeed = 3.9f,
                ShootDistance = 6f, ShootWidth = 0.5f, BulletSpeed = 11f,
                BulletDamage = 80, BulletCountPerShot = 5, BulletCountTotal = 20,
                LaunchAngle = 30f, EachShotInterval = 0.005f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  1  柯尔特
            new HeroNumeric
            {
                MaxHp = 1180, DesignHp = 3640, MoveSpeed = 4.05f,
                ShootDistance = 8f, ShootWidth = 0.4f, BulletSpeed = 12f,
                BulletDamage = 340, BulletCountPerShot = 2, BulletCountTotal = 6,
                LaunchAngle = 0f, EachShotInterval = 0.1f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  2  佩佩
            new HeroNumeric
            {
                MaxHp = 840, DesignHp = 3240, MoveSpeed = 3.9f,
                ShootDistance = 11f, ShootWidth = 0.5f, BulletSpeed = 12f,
                BulletDamage = 650, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  3  潘妮
            new HeroNumeric
            {
                MaxHp = 1010, DesignHp = 4160, MoveSpeed = 3.9f,
                ShootDistance = 6f, ShootWidth = 0.01f, BulletSpeed = 11f,
                BulletDamage = 400, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  4  巴利
            new HeroNumeric
            {
                MaxHp = 840, DesignHp = 2880, MoveSpeed = 3.9f,
                ShootDistance = 5f, ShootWidth = 2f, BulletSpeed = 5f,
                BulletDamage = 816, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0.01f, ReloadSeconds = 1f,
                IsParabola = true, High = 10f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  5  公牛
            new HeroNumeric
            {
                MaxHp = 1400, DesignHp = 5880, MoveSpeed = 3.9f,
                ShootDistance = 4f, ShootWidth = 0f, BulletSpeed = 8f,
                BulletDamage = 45, BulletCountPerShot = 10, BulletCountTotal = 50,
                LaunchAngle = 40f, EachShotInterval = 0.01f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  6  达里尔
            new HeroNumeric
            {
                MaxHp = 960, DesignHp = 5760, MoveSpeed = 3.9f,
                ShootDistance = 6f, ShootWidth = 0f, BulletSpeed = 16f,
                BulletDamage = 90, BulletCountPerShot = 15, BulletCountTotal = 30,
                LaunchAngle = 45f, EachShotInterval = 0.1f, ReloadSeconds = 2f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  7  格尔
            new HeroNumeric
            {
                MaxHp = 900, DesignHp = 4420, MoveSpeed = 3.9f,
                ShootDistance = 7f, ShootWidth = 3f, BulletSpeed = 11f,
                BulletDamage = 448, BulletCountPerShot = 6, BulletCountTotal = 6,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  8  布洛克
            new HeroNumeric
            {
                MaxHp = 1120, DesignHp = 2730, MoveSpeed = 3.9f,
                ShootDistance = 10f, ShootWidth = 0.5f, BulletSpeed = 10f,
                BulletDamage = 1155, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            //  9  爆破麦克
            new HeroNumeric
            {
                MaxHp = 1060, DesignHp = 2940, MoveSpeed = 3.9f,
                ShootDistance = 5f, ShootWidth = 0.2f, BulletSpeed = 5f,
                BulletDamage = 840, BulletCountPerShot = 1, BulletCountTotal = 2,
                LaunchAngle = 20f, EachShotInterval = 0.01f, ReloadSeconds = 1f,
                IsParabola = true, High = 10f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 10  阿渤
            new HeroNumeric
            {
                MaxHp = 1060, DesignHp = 3600, MoveSpeed = 3.9f,
                ShootDistance = 6f, ShootWidth = 0f, BulletSpeed = 10f,
                BulletDamage = 520, BulletCountPerShot = 1, BulletCountTotal = 3,
                LaunchAngle = 15f, EachShotInterval = 0.1f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 11  迪克
            new HeroNumeric
            {
                MaxHp = 1120, DesignHp = 2200, MoveSpeed = 3.9f,
                ShootDistance = 5f, ShootWidth = 0.4f, BulletSpeed = 5f,
                BulletDamage = 680, BulletCountPerShot = 4, BulletCountTotal = 4,
                LaunchAngle = 20f, EachShotInterval = 0.01f, ReloadSeconds = 1f,
                IsParabola = true, High = 10f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 12  贝亚
            new HeroNumeric
            {
                MaxHp = 900, DesignHp = 2400, MoveSpeed = 3.9f,
                ShootDistance = 10f, ShootWidth = 0.5f, BulletSpeed = 10f,
                BulletDamage = 800, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 9f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 90, NormalAttackManaRecover = 0,
            },
            // 13  塔拉
            new HeroNumeric
            {
                MaxHp = 1120, DesignHp = 3400, MoveSpeed = 3.9f,
                ShootDistance = 7f, ShootWidth = 0f, BulletSpeed = 10f,
                BulletDamage = 460, BulletCountPerShot = 3, BulletCountTotal = 3,
                LaunchAngle = 45f, EachShotInterval = 0.05f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 14  麦克斯
            new HeroNumeric
            {
                MaxHp = 1060, DesignHp = 3200, MoveSpeed = 4.08f,
                ShootDistance = 7f, ShootWidth = 0f, BulletSpeed = 14f,
                BulletDamage = 320, BulletCountPerShot = 1, BulletCountTotal = 4,
                LaunchAngle = 10f, EachShotInterval = 0.05f, ReloadSeconds = 2f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 3,
            },
            // 15  斯派克
            new HeroNumeric
            {
                MaxHp = 840, DesignHp = 2400, MoveSpeed = 3.9f,
                ShootDistance = 10f, ShootWidth = 0.5f, BulletSpeed = 10f,
                BulletDamage = 0, BulletCountPerShot = 1, BulletCountTotal = 1,
                LaunchAngle = 0f, EachShotInterval = 0f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 16  黑鸦
            new HeroNumeric
            {
                MaxHp = 900, DesignHp = 2400, MoveSpeed = 4.2f,
                ShootDistance = 7f, ShootWidth = 0f, BulletSpeed = 12f,
                BulletDamage = 320, BulletCountPerShot = 3, BulletCountTotal = 3,
                LaunchAngle = 30f, EachShotInterval = 0.08f, ReloadSeconds = 2f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 17  里昂
            new HeroNumeric
            {
                MaxHp = 840, DesignHp = 4800, MoveSpeed = 3.96f,
                ShootDistance = 7f, ShootWidth = 0f, BulletSpeed = 11f,
                BulletDamage = 680, BulletCountPerShot = 1, BulletCountTotal = 4,
                LaunchAngle = 10f, EachShotInterval = 0.09f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 18  帕姆
            new HeroNumeric
            {
                MaxHp = 1340, DesignHp = 4800, MoveSpeed = 3.78f,
                ShootDistance = 9f, ShootWidth = 0f, BulletSpeed = 14f,
                BulletDamage = 260, BulletCountPerShot = 2, BulletCountTotal = 9,
                LaunchAngle = 50f, EachShotInterval = 0.15f, ReloadSeconds = 1f,
                IsParabola = false, High = 30f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
            // 19  瑞科
            new HeroNumeric
            {
                MaxHp = 840, DesignHp = 3250, MoveSpeed = 3.9f,
                ShootDistance = 10f, ShootWidth = 0.04f, BulletSpeed = 13f,
                BulletDamage = 400, BulletCountPerShot = 1, BulletCountTotal = 5,
                LaunchAngle = 0f, EachShotInterval = 0.1f, ReloadSeconds = 1f,
                IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
                NormalAttackManaCost = 30, NormalAttackManaRecover = 0,
            },
        };

        private static readonly bool[] _hasSuper = new bool[PMHeroId.Count]
        {
            true , //  0 雪莉
            true , //  1 柯尔特
            false, //  2 佩佩
            false, //  3 潘妮
            false, //  4 巴利
            false, //  5 公牛
            false, //  6 达里尔
            true , //  7 格尔
            false, //  8 布洛克
            false, //  9 爆破麦克
            false, // 10 阿渤
            false, // 11 迪克
            true , // 12 贝亚
            false, // 13 塔拉
            false, // 14 麦克斯
            false, // 15 斯派克
            false, // 16 黑鸦
            false, // 17 里昂
            true , // 18 帕姆
            true , // 19 瑞科
        };

        private static readonly SuperNumeric[] _supers = new SuperNumeric[PMHeroId.Count]
        {
            //  0 雪莉
            new SuperNumeric
            {
                ShootDistance = 6f, ShootWidth = 0.5f, BulletCountTotal = 40,
                BulletDamage = -1, LaunchAngle = 40f, BulletSpeed = 14f,
                BulletCountPerShot = -1, EachShotInterval = -1f, IsParabola = false, High = -1f,
            },
            //  1 柯尔特
            new SuperNumeric
            {
                ShootDistance = 12f, ShootWidth = 0.2f, BulletCountTotal = 12,
                BulletDamage = -1, LaunchAngle = -1f, BulletSpeed = 18f,
                BulletCountPerShot = -1, EachShotInterval = -1f, IsParabola = false, High = -1f,
            },
            default(SuperNumeric),   //  2 佩佩（无大招弹道覆写）
            default(SuperNumeric),   //  3 潘妮（无大招弹道覆写）
            default(SuperNumeric),   //  4 巴利（无大招弹道覆写）
            default(SuperNumeric),   //  5 公牛（无大招弹道覆写）
            default(SuperNumeric),   //  6 达里尔（无大招弹道覆写）
            //  7 格尔
            new SuperNumeric
            {
                ShootDistance = 7f, ShootWidth = 4f, BulletCountTotal = 4,
                BulletDamage = -1, LaunchAngle = -1f, BulletSpeed = 14f,
                BulletCountPerShot = 4, EachShotInterval = 0f, IsParabola = false, High = -1f,
            },
            default(SuperNumeric),   //  8 布洛克（无大招弹道覆写）
            default(SuperNumeric),   //  9 爆破麦克（无大招弹道覆写）
            default(SuperNumeric),   // 10 阿渤（无大招弹道覆写）
            default(SuperNumeric),   // 11 迪克（无大招弹道覆写）
            // 12 贝亚
            new SuperNumeric
            {
                ShootDistance = 10f, ShootWidth = 0.8f, BulletCountTotal = 6,
                BulletDamage = 60, LaunchAngle = -1f, BulletSpeed = 10f,
                BulletCountPerShot = 6, EachShotInterval = -1f, IsParabola = false, High = -1f,
            },
            default(SuperNumeric),   // 13 塔拉（无大招弹道覆写）
            default(SuperNumeric),   // 14 麦克斯（无大招弹道覆写）
            default(SuperNumeric),   // 15 斯派克（无大招弹道覆写）
            default(SuperNumeric),   // 16 黑鸦（无大招弹道覆写）
            default(SuperNumeric),   // 17 里昂（无大招弹道覆写）
            // 18 帕姆
            new SuperNumeric
            {
                ShootDistance = 2f, ShootWidth = 1f, BulletCountTotal = 1,
                BulletDamage = 300, LaunchAngle = 0f, BulletSpeed = 5f,
                BulletCountPerShot = 1, EachShotInterval = 0f, IsParabola = true, High = -1f,
            },
            // 19 瑞科
            new SuperNumeric
            {
                ShootDistance = 14f, ShootWidth = 0.2f, BulletCountTotal = 12,
                BulletDamage = -1, LaunchAngle = -1f, BulletSpeed = 19f,
                BulletCountPerShot = -1, EachShotInterval = -1f, IsParabola = false, High = -1f,
            },
        };

        /// <summary>未知英雄编号的兜底数值。</summary>
        private static readonly HeroNumeric _fallback = new HeroNumeric
        {
            MaxHp = 960, DesignHp = 4680, MoveSpeed = 3.9f,
            ShootDistance = 7f, ShootWidth = 0f, BulletSpeed = 10f,
            BulletDamage = 200, BulletCountPerShot = 1, BulletCountTotal = 1,
            LaunchAngle = 0f, EachShotInterval = 0.1f, ReloadSeconds = 1f,
            IsParabola = false, High = 0f, ServerHitRadius = 0.8f,
            NormalAttackManaCost = DefaultAttackManaCost, NormalAttackManaRecover = 0,
        };

        private static readonly HashSet<int> _unknownHeroes = new HashSet<int>();

        /// <summary>
        /// 按英雄编号取数值。越界或未知编号返回兜底值并记录，**不抛异常**。
        ///
        /// <para>旧实现里 <c>HeroConfig.Get</c> 与 <c>GetHp</c> 有默认值，但 <c>GetReloadSeconds</c>
        /// 用直接索引，未知英雄会抛 <c>KeyNotFoundException</c>（一处静默不一致）。
        /// 战斗循环里抛异常会直接废掉整局，而未知编号只应表现为「这个英雄数值不对」，
        /// 因此这里统一为「兜底 + 记录」。</para>
        /// </summary>
        public static HeroNumeric Get(int heroId)
        {
            if (heroId < 0 || heroId >= _heroes.Length)
            {
                _unknownHeroes.Add(heroId);
                return _fallback;
            }

            return _heroes[heroId];
        }

        /// <summary>取大招覆写。<c>false</c> = 该英雄没有大招弹道覆写（非子弹型大招，或沿用普通攻击）。</summary>
        public static bool TryGetSuper(int heroId, out SuperNumeric super)
        {
            if (heroId >= 0 && heroId < _hasSuper.Length && _hasSuper[heroId])
            {
                super = _supers[heroId];
                return true;
            }

            super = default(SuperNumeric);
            return false;
        }

        /// <summary>
        /// 把「英雄 + 是否大招」解析成一次攻击的完整规格。
        ///
        /// <para>
        /// 这是普通攻击与大招在数值上的**唯一合并点**。旧实现里这段逻辑散落在调用侧：
        /// 先决定查哪张表，再依赖两张表恰好用同名字段表达不同含义。
        /// 把它收到这里后，两端只会有一份解释。
        /// </para>
        ///
        /// <para>
        /// 大招的 <c>-1</c> 字段会按预置的「沿用普通攻击」语义回填，因此返回值总是自洽的
        /// （不会把 -1 当真实数值用出去）。若英雄没有大招覆写，<paramref name="isSuper"/> 为 true 时
        /// 会回退到普通攻击值 —— 调用方如需区分「该英雄没有大招弹道」，应先调 <see cref="TryGetSuper"/>。
        /// </para>
        /// </summary>
        public static ResolvedAttack ResolveAttack(int heroId, bool isSuper)
        {
            HeroNumeric hero = Get(heroId);

            ResolvedAttack result;
            result.ShootDistance = hero.ShootDistance;
            result.ShootWidth = hero.ShootWidth;
            result.BulletSpeed = hero.BulletSpeed;
            result.BulletDamage = hero.BulletDamage;
            result.BulletCountTotal = hero.BulletCountTotal;
            result.BulletCountPerShot = hero.BulletCountPerShot;
            // 注意：普通攻击一次生成的量是「每次发射数」，不是「一轮总数」。
            result.SpawnBulletCount = hero.BulletCountPerShot;
            result.LaunchAngle = hero.LaunchAngle;
            result.EachShotInterval = hero.EachShotInterval;
            result.IsParabola = hero.IsParabola;
            result.High = hero.High;

            SuperNumeric super;
            if (!isSuper || !TryGetSuper(heroId, out super))
            {
                return result;
            }

            // 大招覆写：负数表示「沿用普通攻击」。
            result.ShootDistance = super.ShootDistance;
            result.ShootWidth = super.ShootWidth;
            result.BulletCountTotal = super.BulletCountTotal;
            // 大招一次把「一轮总数」全生成出来 —— 与普通攻击的选择不同，这是有意的（与旧服务端行为一致）。
            result.SpawnBulletCount = super.BulletCountTotal;
            result.IsParabola = super.IsParabola;

            if (super.BulletSpeed >= 0f) result.BulletSpeed = super.BulletSpeed;
            if (super.BulletDamage >= 0) result.BulletDamage = super.BulletDamage;
            if (super.BulletCountPerShot >= 0) result.BulletCountPerShot = super.BulletCountPerShot;
            if (super.LaunchAngle >= 0f) result.LaunchAngle = super.LaunchAngle;
            if (super.EachShotInterval >= 0f) result.EachShotInterval = super.EachShotInterval;
            if (super.High >= 0f) result.High = super.High;

            return result;
        }

        /// <summary>
        /// 未知英雄编号的兜底数值。
        ///
        /// <para>公开出来是给「拿不到英雄编号」的调用方用的（例如玩家尚未进入战斗就被问移速）。
        /// 这种情形**不是「出现了未知英雄」**，不该污染 <see cref="UnknownHeroes"/>，
        /// 所以不能靠 <c>Get(-1)</c> 凑。</para>
        /// </summary>
        public static HeroNumeric Fallback
        {
            get { return _fallback; }
        }

        /// <summary>
        /// 出现过的未知英雄编号。
        ///
        /// <para>用集合而不是计数：未知编号天然极少，而且要能在日志里看出「到底是谁」。
        /// **日志输出由调用方决定** —— 本类不引用任何日志设施，以保持零依赖、可被最严格门禁编译。</para>
        /// </summary>
        public static ICollection<int> UnknownHeroes
        {
            get { return _unknownHeroes; }
        }
    }
}
