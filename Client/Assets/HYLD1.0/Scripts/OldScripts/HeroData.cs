using UnityEngine;
using PMNet.Shared;

// ============================================================================
//  英雄数据模型（P3'-2 从 HYLDStaticValue.cs 抽出，并完成「数值/表现分离」）
//
//  为什么单独一个文件：
//   1) 原文件是一个 525 行的杂物静态类（MonoBehaviour + 全局状态 + 枚举 + 数据模型），
//      其中 Hero 数据模型是**纯数据**，与那些全局状态没有关系；
//   2) 抽出来之后它只依赖 UnityEngine.GameObject 与共享数值表，
//      因此可以用最小的桩件建立**真实的编译门禁**（Tools/PMHeroDataCheck），
//      而不是只能等 Unity 编辑器编译时才发现问题。
//
//  数值/表现分离（本次改造的核心）：
//   - **数值**（血量/移速/弹道/伤害…）全部改成**只读属性**，唯一来源是新表
//     PMNet.Shared.BattleNumericConfig（两端编译同一份源码）。
//   - **表现引用**（子弹预制体 / 大招实体 / 爆炸特效）留在客户端，因为服务端没有这些资源。
//   - 因此 `hero.BloodValue = x` 这类写法会**编译报错** —— 这是刻意的：
//     过去服务端把权威血量写进 hero 配置对象，再由 PlayerLogic 跟随，
//     等价于拿「共享配置」当「每玩家状态」用；现在必须写到每玩家状态里。
// ============================================================================

/// <summary>
/// 英雄枚举。**取值必须与 proto 的 <c>SocketProto.Hero</c> 完全一致**，
/// 由 <c>Tools/check_hero_id_alignment.py</c> 逐值校验（proto / 本枚举 / PMHeroId 三方对齐）。
///
/// <para>行尾注释是原表里带过来的策划/验收备注，保留以便查阅。</para>
/// </summary>
public enum HeroName
{   //         子弹自身技能      大招         妙具         星辉
    XueLi = 0,      //ok            ok
    KeErTe,     //ok            ok                          ok
    PeiPei,//   ok
    PanNi,     //不太行
    BaLi,       //ok
    GongNiu,   //ok
    DaLiEr,    //ok
    GeEr,      //ok              no
    BuLuoKe,    //ok
    BaoPoMaiKe,  //ok
    ABo,        //ok
    DiKe,      //ok
    BeiYa,     //ok
    TaLa,      //ok
    MaiKeSi,   //ok
    SiPaiKe,  //ok
    HeiYa,     //ok
    LiAng,     //ok
    PaMu,   //ok
    RuiKe,    //ok              ok
}

/// <summary>
/// 英雄数据模型：**身份 + 表现引用**，数值一律从 <see cref="BattleNumericConfig"/> 读。
///
/// <para>
/// 本类不含任何可变数值。构造后即不可变（所有字段 readonly，数值是计算属性），
/// 因此不可能再出现「某处偷偷把配置改了，导致全局所有玩家跟着变」的情况。
/// </para>
/// </summary>
public class Hero
{
    // ---------------- 身份 ----------------

    /// <summary>英雄枚举（也是查共享数值表的键）。</summary>
    public readonly HeroName heroName;

    /// <summary>显示名（中文）。</summary>
    public readonly string Name;

    /// <summary>定位描述（坦克/射手/辅助…）。仅供展示。</summary>
    public readonly string HeroPositioning;

    // ---------------- 表现引用（客户端专有；服务端没有这些资源）----------------

    /// <summary>普通攻击的子弹预制体。</summary>
    public readonly GameObject shell;

    /// <summary>大招实体（非子弹型大招用它；子弹型大招也会把它当表现载体）。</summary>
    public readonly GameObject 大招实体;

    /// <summary>爆炸/落点特效。</summary>
    public readonly GameObject Boom;

    /// <summary>大招是否为「移动型实体」而非子弹（如麦克斯）。</summary>
    public readonly bool isSuperMovingType;

    // ---------------- 数值：只读视图，唯一来源是共享表 ----------------

    /// <summary>本局最大生命值（= 共享表 MaxHp，即当前实际生效值）。</summary>
    public int BloodValue { get { return N.MaxHp; } }

    /// <summary>设计生命值（当前不生效，仅作查阅）。</summary>
    public int DesignBloodValue { get { return N.DesignHp; } }

    /// <summary>移动速度（逐英雄）。</summary>
    public float 移动速度 { get { return N.MoveSpeed; } }

    /// <summary>普通攻击射程。</summary>
    public float shootDistance { get { return N.ShootDistance; } }

    /// <summary>弹体表现宽度 / 爆炸半径（**不是**服务端命中判定半径）。</summary>
    public float shootWidth { get { return N.ShootWidth; } }

    /// <summary>普通攻击一轮的总弹数。</summary>
    public int bulletCount { get { return N.BulletCountTotal; } }

    /// <summary>单颗子弹伤害。</summary>
    public int bulletDamage { get { return N.BulletDamage; } }

    /// <summary>扇形总角度（度）。</summary>
    public float LaunchAngle { get { return N.LaunchAngle; } }

    /// <summary>子弹速度。</summary>
    public float speed { get { return N.BulletSpeed; } }

    /// <summary>每次发射的弹数。</summary>
    public int bulletCountByEachTime { get { return N.BulletCountPerShot; } }

    /// <summary>同一轮内相邻两次发射的间隔（秒）。</summary>
    public float EachTimebulletsShootSpace { get { return N.EachShotInterval; } }

    /// <summary>是否抛物线弹道。</summary>
    public bool IsParadola { get { return N.IsParabola; } }

    /// <summary>抛射高度。</summary>
    public float high { get { return N.High; } }

    /// <summary>普通攻击蓝耗（贝亚 = 90）。</summary>
    public int normalAttackManaCost { get { return N.NormalAttackManaCost; } }

    /// <summary>
    /// 该英雄是否有「子弹型大招」的数值覆写。
    ///
    /// <para>取代原先的 <c>superBullet == null</c> 判断 —— 后者把「有没有大招数值」
    /// 编码在一个对象是否为 null 上，而数值本身现在归共享表管。</para>
    /// </summary>
    public bool HasSuperBullet
    {
        get
        {
            SuperNumeric ignored;
            return BattleNumericConfig.TryGetSuper((int)heroName, out ignored);
        }
    }

    /// <summary>共享表里本英雄的那一条（每次读取都是值拷贝，不存在共享可变状态）。</summary>
    private HeroNumeric N { get { return BattleNumericConfig.Get((int)heroName); } }

    /// <summary>
    /// 构造：只接收**身份**与**表现引用**，数值不再进构造函数
    /// （数值写在这里就会变成第二份副本，正是本次要消除的东西）。
    /// </summary>
    public Hero(HeroName heroName, string displayName, string positioning,
                GameObject shell, GameObject superEntity = null, GameObject boom = null,
                bool isSuperMovingType = false)
    {
        this.heroName = heroName;
        this.Name = displayName;
        this.HeroPositioning = positioning;
        this.shell = shell;
        this.大招实体 = superEntity;
        this.Boom = boom;
        this.isSuperMovingType = isSuperMovingType;
    }
}
