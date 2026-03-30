using SocketProto;
using System.Collections.Generic;

namespace Server
{
    /// <summary>
    /// 服务端英雄子弹参数配置。
    /// 参数值从客户端 HYLDStaticValue.Players（Hero 类）提取，需人工保持同步。
    /// V1: 硬编码配置字典；后续可迁移到配置文件。
    /// </summary>
    public static class HeroConfig
    {
        public struct BulletParams
        {
            public float BulletSpeed;       // 子弹速度（单位/秒）
            public float BulletMaxDist;     // 子弹最大飞行距离
            public float HitRadius;         // 命中半径
            public int   Damage;            // 每颗子弹伤害
            public int   BulletCount;       // 每次攻击发射子弹数（散弹数）
            public float SpreadAngle;       // 扇形总角度（度），单发=0
            public bool  IsParabola;        // 是否抛物线（抛物线英雄跳过服务端碰撞，V1 暂用相同逻辑）
        }

        // hitRadius 统一使用 0.8f（与客户端 ConstValue.hitRadius 一致）
        private const float DefaultHitRadius = 0.8f;

        private static readonly Dictionary<Hero, BulletParams> _config = new Dictionary<Hero, BulletParams>
        {
            // Hero 枚举顺序与 proto 一致：XueLi=0 ... RuiKe=19
            // 参数来源：客户端 HYLDStaticValue.Awake() Hero 构造调用
            // 构造函数参数顺序：名字, 定位, 血量, 移速, 进攻距离, 攻击距离, 每次发射间隔, 装弹速度,
            //                   shell, shootDistance, shootWidth, bulletCount, bulletDamage, LaunchAngle, speed, bulletCountByEachTime, ...
            // 服务端用：shootDistance=BulletMaxDist, speed=BulletSpeed, bulletCountByEachTime=BulletCount(每次发射), LaunchAngle=SpreadAngle, bulletDamage=Damage

            [Hero.XueLi]      = new BulletParams { BulletSpeed=11, BulletMaxDist=6,  HitRadius=DefaultHitRadius, Damage=80,   BulletCount=5,  SpreadAngle=30f,  IsParabola=false },
            [Hero.KeErTe]     = new BulletParams { BulletSpeed=12, BulletMaxDist=8,  HitRadius=DefaultHitRadius, Damage=340,  BulletCount=2,  SpreadAngle=0f,   IsParabola=false },
            [Hero.PeiPei]     = new BulletParams { BulletSpeed=12, BulletMaxDist=11, HitRadius=DefaultHitRadius, Damage=650,  BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
            [Hero.PanNi]      = new BulletParams { BulletSpeed=11, BulletMaxDist=6,  HitRadius=DefaultHitRadius, Damage=400,  BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
            [Hero.BaLi]       = new BulletParams { BulletSpeed=5,  BulletMaxDist=5,  HitRadius=DefaultHitRadius, Damage=816,  BulletCount=1,  SpreadAngle=0f,   IsParabola=true  },
            [Hero.GongNiu]    = new BulletParams { BulletSpeed=8,  BulletMaxDist=4,  HitRadius=DefaultHitRadius, Damage=45,   BulletCount=10, SpreadAngle=40f,  IsParabola=false },
            [Hero.DaLiEr]     = new BulletParams { BulletSpeed=16, BulletMaxDist=6,  HitRadius=DefaultHitRadius, Damage=90,   BulletCount=15, SpreadAngle=45f,  IsParabola=false },
            [Hero.GeEr]       = new BulletParams { BulletSpeed=11, BulletMaxDist=7,  HitRadius=DefaultHitRadius, Damage=448,  BulletCount=6,  SpreadAngle=0f,   IsParabola=false },
            [Hero.BuLuoKe]    = new BulletParams { BulletSpeed=10, BulletMaxDist=10, HitRadius=DefaultHitRadius, Damage=1155, BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
            [Hero.BaoPoMaiKe] = new BulletParams { BulletSpeed=5,  BulletMaxDist=5,  HitRadius=DefaultHitRadius, Damage=840,  BulletCount=1,  SpreadAngle=20f,  IsParabola=true  },
            [Hero.Abo]        = new BulletParams { BulletSpeed=10, BulletMaxDist=6,  HitRadius=DefaultHitRadius, Damage=520,  BulletCount=1,  SpreadAngle=15f,  IsParabola=false },
            [Hero.DiKe]       = new BulletParams { BulletSpeed=5,  BulletMaxDist=5,  HitRadius=DefaultHitRadius, Damage=680,  BulletCount=4,  SpreadAngle=20f,  IsParabola=true  },
            [Hero.BeiYa]      = new BulletParams { BulletSpeed=10, BulletMaxDist=10, HitRadius=DefaultHitRadius, Damage=800,  BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
            [Hero.TaLa]       = new BulletParams { BulletSpeed=10, BulletMaxDist=7,  HitRadius=DefaultHitRadius, Damage=460,  BulletCount=3,  SpreadAngle=45f,  IsParabola=false },
            [Hero.MaiKeSi]    = new BulletParams { BulletSpeed=14, BulletMaxDist=7,  HitRadius=DefaultHitRadius, Damage=320,  BulletCount=1,  SpreadAngle=10f,  IsParabola=false },
            [Hero.SiPaiKe]    = new BulletParams { BulletSpeed=10, BulletMaxDist=10, HitRadius=DefaultHitRadius, Damage=0,    BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
            [Hero.HeiYa]      = new BulletParams { BulletSpeed=12, BulletMaxDist=7,  HitRadius=DefaultHitRadius, Damage=320,  BulletCount=3,  SpreadAngle=30f,  IsParabola=false },
            [Hero.LiAng]      = new BulletParams { BulletSpeed=11, BulletMaxDist=7,  HitRadius=DefaultHitRadius, Damage=680,  BulletCount=1,  SpreadAngle=10f,  IsParabola=false },
            [Hero.PaMu]       = new BulletParams { BulletSpeed=14, BulletMaxDist=9,  HitRadius=DefaultHitRadius, Damage=260,  BulletCount=2,  SpreadAngle=50f,  IsParabola=false },
            [Hero.RuiKe]      = new BulletParams { BulletSpeed=13, BulletMaxDist=10, HitRadius=DefaultHitRadius, Damage=400,  BulletCount=1,  SpreadAngle=0f,   IsParabola=false },
        };

        /// <summary>默认参数（Hero 不在配置字典时使用）</summary>
        private static readonly BulletParams _default = new BulletParams
        {
            BulletSpeed = 10, BulletMaxDist = 7, HitRadius = DefaultHitRadius,
            Damage = 200, BulletCount = 1, SpreadAngle = 0, IsParabola = false
        };

        public static BulletParams Get(Hero hero)
        {
            if (_config.TryGetValue(hero, out BulletParams p))
                return p;
            Logging.Debug.Log($"[HeroConfig] 未知英雄类型 {hero}，使用默认参数");
            return _default;
        }

        // ---- HP 配置 ----
        // 临时降低血量（约原值 1/5），减少测试期子弹累积量避免 Editor 崩溃。
        // 后续对象池优化完成后恢复原始血量。
        // 原始值参考客户端 HYLDStaticValue.Awake() 中 Hero 构造参数第 3 项（血量）。
        private static readonly Dictionary<Hero, int> _hpConfig = new Dictionary<Hero, int>
        {
            [Hero.XueLi]      = 960,   // 原 4680
            [Hero.KeErTe]     = 1180,  // 原 5880
            [Hero.PeiPei]     = 840,   // 原 4200
            [Hero.PanNi]      = 1010,  // 原 5040
            [Hero.BaLi]       = 840,   // 原 4200
            [Hero.GongNiu]    = 1400,  // 原 7000
            [Hero.DaLiEr]     = 960,   // 原 4680
            [Hero.GeEr]       = 900,   // 原 4480
            [Hero.BuLuoKe]    = 1120,  // 原 5600
            [Hero.BaoPoMaiKe] = 1060,  // 原 5320
            [Hero.Abo]        = 1060,  // 原 5320
            [Hero.DiKe]       = 1120,  // 原 5600
            [Hero.BeiYa]      = 900,   // 原 4480
            [Hero.TaLa]       = 1120,  // 原 5600
            [Hero.MaiKeSi]    = 1060,  // 原 5320
            [Hero.SiPaiKe]    = 840,   // 原 4200
            [Hero.HeiYa]      = 900,   // 原 4480
            [Hero.LiAng]      = 840,   // 原 4200
            [Hero.PaMu]       = 1340,  // 原 6720
            [Hero.RuiKe]      = 840,   // 原 4200
        };

        private const int DefaultHp = 960;  // 原 4680

        /// <summary>获取英雄最大 HP（与客户端 playerBloodMax 一致）</summary>
        public static int GetHp(Hero hero)
        {
            return _hpConfig.TryGetValue(hero, out int hp) ? hp : DefaultHp;
        }
    }
}
