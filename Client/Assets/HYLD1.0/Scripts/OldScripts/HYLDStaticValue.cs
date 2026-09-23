/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/18 20:19:03 
 * Description:  全局静态类 玩家信息类
 * * * * * * * * * * * * * * * * 
*/

/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/7/3 23：11 
 * Description:  增加float MovingSpeed；引入Hero类，hero自带子弹类型，血量，名字等参数
 *  UpDateTime: 2020/7/24 15:17
 * Description: 将地图变为针对不同模式会生成不同类型地图,新增选英雄选模式的string类型，给予一些特殊英雄一些特殊参数
 * * * * * * * * * * * * * * * * 
*/

/*
 * * * * * * * * * * * * * * * * 
 * Author:        邓龙浩
 * CreatTime:  2020/7/24 17:52:26 
 * Description: 增加抛物线爆炸物逻辑
 * * * * * * * * * * * * * * * * 
*/
using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

using Google.Protobuf.Collections;
using SocketProto;
using Manger;

public class HYLDStaticValue :MonoBehaviour
{
    public bool ISNet;
    public ModelName testMOdel;
    public GameObject _StartGameAni;
    public GameObject BG;

    #region 变量
    // fixedDeltaTime 必须等于逻辑帧间隔（与 NetConfigValue.frameTime 一致）
    // 不要硬编码！改帧率时必须同步改这里
    public static float fixedDeltaTime = Server.NetConfigValue.frameTime; // 原来写死 0.02f 导致帧率越高移速越快
    public static bool isNet = true;
    public static string myheroName="雪莉";
    public static HeroName _myheroName=HeroName.XueLi;
    public static string ModenName = "HYLDBaoShiZhengBa";
    public static List<PlayerInformation> Players = new List<PlayerInformation>(10);

    public static int RoomEnemyTeamGemTotalValue=0;
    public static int RoomSelfTeamGemTotalValue=0;
    //public static float MovingSpeed=4;
    public static bool isloading = false;
    public static int MatchingPlayerTotal = 0;

    public static int playerSelfIDInServer = -1;
    public static string PlayerName;
    public static bool ConfirmWinOrNot = true;
    public static bool 玩家输了吗;

    public static bool 是否为连接状态 = false;
    public static string UseName;
    public static string PassWord;
    public static int PlayerUID;
    public static FriendRoomPack Myroom;
    public static RepeatedField<PlayerPack> FriendLists = new RepeatedField<PlayerPack>();
    public static Dictionary<int, PlayerPack> ActiveFriend = new Dictionary<int, PlayerPack>();
    //public static int UserID = 0;
    //zyk增加部分
    [Header("子弹")]
    public GameObject[] shells;

    public GameObject[] BetterShells;
    public static Dictionary<HeroName, Hero> Heros=new Dictionary<HeroName,Hero>();

    /// <summary>
    /// 向 <see cref="Heros"/> 登记一个英雄。
    ///
    /// <para>
    /// 存在的理由：<c>heroName</c> 既要做字典键、又要作为 <see cref="Hero"/> 构造的第一个参数
    /// （Hero 要用它去共享数值表查值）。若在表里写两遍，就有写不一致的风险，
    /// 而这种不一致不会报错、只会让某个英雄的数值静默错位。这里只接收一次。
    /// </para>
    /// </summary>
    private static void AddHero(HeroName heroName, string displayName, string positioning,
                                GameObject shell, GameObject superEntity = null, GameObject boom = null,
                                bool isSuperMovingType = false)
    {
        Heros.Add(heroName, new Hero(heroName, displayName, positioning, shell, superEntity, boom, isSuperMovingType));
    }
    [Header("大招")]
    public GameObject[] 大招实体;
    [Header("金库模式")]
    //金库生命值（金库模式专用）
    /// <summary>
    /// 「金库攻防」模式的金库上限。
    ///
    /// <para>
    /// P3'-2 清理：此前 RedBP/BlueBP 的初值是 <b>50000</b>，而 Toolbox 的 ReStart() 与
    /// 血条滑条的分母用的是 <b>30000</b> —— 于是**首局与重开局的胜负阈值不一样**，
    /// 且首局滑条会算出 50000/30000 = 1.67 而溢出。以 30000 为准（滑条分母与模式初始化都用它），
    /// 并收敛到这个常量，避免再出现两处不一致。
    /// </para>
    /// </summary>
    public const int VaultBPMax = 30000;

    // ---- 客户端玩法数值（单机/表现侧）----
    //
    // P3'-2 清理：这些值原先以字面量散落在 PlayerLogic / TextLogic / Toolbox / 本文件里，
    // 同一个含义在多处重复（毒伤 85 在 2 个文件各写一遍），改一处漏一处就会出现不一致。
    // 收敛为命名常量，只整理来源、**不改变任何取值**。
    //
    // 注意：它们不在共享数值表（Client/Assets/Scripts/Shared/BattleNumericConfig.cs）里，
    // 因为服务端不需要它们 —— 服务端的战斗数值已经在那边统一了。

    /// <summary>中毒每跳伤害。</summary>
    public const int PoisonDamagePerTick = 85;

    /// <summary>中毒持续跳数（每次 Update 一跳）。</summary>
    public const int PoisonTickCount = 5;

    // P3'-3c 删除：CureDamageFreeSeconds / CureIntervalSeconds / CureHpRatio / ShieldSeconds。
    // 它们只被 PlayerLogic 的「回血 / 护盾」机制使用，而那两个机制已随
    // 「客户端不再改写权威字段」一并删除（见 PlayerLogic 类注释）。
    //
    // 上面的 PoisonDamagePerTick / PoisonTickCount **保留**：试玩模式的 TextLogic
    // 仍在使用（它是自包含的单机机器人，不读写权威字段）。

    /// <summary>「宝石争霸」模式的胜利宝石数。</summary>
    public const int GemWinCount = 10;

    public static int RedBP = VaultBPMax;
    public static int BlueBP = VaultBPMax;
    //爆炸预制体
    [Header("爆炸预制体")]
    public GameObject[] Booms;
    //自制工具盒
    [Header("工具盒")]
    public GameObject ToolBox;
    [Header("地图生成器")]
    public ScenseBuildLogic ScenseBuildLogic;
    #endregion
    #region 添加英雄

    #region net帧同步交互部分
    public static float PlayerMoveX;
    public static float PlayerMoveY;

    #endregion
    
    public void Awake()
    {
        RoomEnemyTeamGemTotalValue = 0;
         RoomSelfTeamGemTotalValue = 0;
        //public static float MovingSpeed=4;
       isloading = false;

        playerSelfIDInServer = -1;

        ConfirmWinOrNot = true;
         玩家输了吗=false;

         RedBP = VaultBPMax;
         BlueBP = VaultBPMax;

    // Debug.LogError("11");
    //zyk增加部分      名字              名字     定位  血量 移速 进攻距离 子弹预制体 距离 宽度 子弹数量 伤害 角度 速度 每次发射数量 [间隔] 
    //以上是劣质特效，特效被删了的子弹。
    //以下是实体子弹
        Heros.Clear();

        // 表里只放「身份 + 表现引用」。数值（血量/移速/弹道/伤害/大招参数）全部来自
        // PMNet.Shared.BattleNumericConfig（两端编译同一份源码），所以这里不应出现任何数字。
        // 某个数值不对就改那个文件，不要改这里。
        //
        // 用 AddHero 而不是 Heros.Add + new Hero(...)：heroName 既要做字典键，又要作为
        // Hero 构造的第一个参数（它要用它去查共享表）。写两遍就有一致性风险，AddHero 只收一次。
        //
        // 行序与 PMHeroId 编号（= proto Hero 枚举值）一致，便于与共享表逐行交叉核对；
        // Heros 是 Dictionary，行序不影响运行。形状与表现引用由
        // Tools/check_hero_table_shape.py 校验（含与改造前基准的逐字段比对）。
        //
        //  英雄              显示名      定位          子弹预制体          大招实体        爆炸特效      移动型大招
        //  ----              ------      ----          ----------          --------        --------      ----------
        AddHero(HeroName.XueLi,      "雪莉", "战士", BetterShells[13], 大招实体[10], null, false);
        AddHero(HeroName.KeErTe,     "柯尔特", "射手", BetterShells[12], 大招实体[9], null, false);
        AddHero(HeroName.PeiPei,     "佩佩", "射手", BetterShells[8], 大招实体[6], null, false);
        AddHero(HeroName.PanNi,      "潘妮", "战士", BetterShells[11], 大招实体[8], null, false);
        AddHero(HeroName.BaLi,       "巴利", "投掷手", BetterShells[18], 大招实体[15], Booms[0], false);
        AddHero(HeroName.GongNiu,    "公牛", "坦克", BetterShells[1], null, null, false);
        AddHero(HeroName.DaLiEr,     "达里尔", "坦克", BetterShells[0], null, null, false);
        AddHero(HeroName.GeEr,       "格尔", "辅助", BetterShells[10], 大招实体[7], null, false);
        AddHero(HeroName.BuLuoKe,    "布洛克", "射手", BetterShells[14], 大招实体[11], null, false);
        AddHero(HeroName.BaoPoMaiKe, "爆破麦克", "投掷手", BetterShells[17], 大招实体[14], Booms[2], false);
        AddHero(HeroName.ABo,        "阿渤", "战士", BetterShells[3], 大招实体[1], null, false);
        AddHero(HeroName.DiKe,       "迪克", "投掷手", BetterShells[19], 大招实体[16], Booms[1], false);
        AddHero(HeroName.BeiYa,      "贝亚", "射手", BetterShells[15], 大招实体[12], null, false);
        AddHero(HeroName.TaLa,       "塔拉", "战士", BetterShells[5], 大招实体[3], null, false);
        AddHero(HeroName.MaiKeSi,    "麦克斯", "辅助", BetterShells[4], 大招实体[2], null, true);
        AddHero(HeroName.SiPaiKe,    "斯派克", "射手", BetterShells[16], 大招实体[13], null, false);
        AddHero(HeroName.HeiYa,      "黑鸦", "致伤突袭者", BetterShells[7], 大招实体[5], null, false);
        AddHero(HeroName.LiAng,      "里昂", "潜行突袭者", BetterShells[9], null, null, false);
        AddHero(HeroName.PaMu,       "帕姆", "辅助", BetterShells[6], 大招实体[4], Booms[3], false);
        AddHero(HeroName.RuiKe,      "瑞科", "射手", BetterShells[2], 大招实体[0], null, false);
        if (!ISNet)
        {
            ModenName = testMOdel.ToString();
        }
        // 旧链退役（契约 §B）：原在这里按模式 AddComponent<HYLDBaoShiZhengBaManger>() /
        // AddComponent<BattleManger>() 自动拉起旧战斗宿主。旧战斗链（含单机试玩自动启动）已整体退役，
        // 这两条启动分支删除：旧场景/预制体上的本类只作**素材与静态数据**输入，
        // 不再运行第二套战斗权威，也不为旧入口留替身。
        Logging.HYLDDebug.Log("[HYLDStaticValue] 旧战斗宿主自动启动已退役（mode=" + ModenName + "），不再拉起 BattleManger/HYLDBaoShiZhengBaManger");
        
    }
    //对于爆破手而言、宽度为爆炸半径、速度与抛物高度成反比、距离为爆炸点离玩家距离
    #endregion
    public void Update()
    {
        /*
        if (ConfirmWinOrNot)
        {
            if (ToolBox == null)
                ToolBox = GameObject.Find("ToolBox");
            ConfirmWinOrNot = false;
            //控制宝石争霸输赢结束
            if (ModenName == "HYLDBaoShiZhengBa")
            {
                int totalRedTemp = 0, totalBlueTemp = 0;
                foreach (var pos in Players)
                {
                    if (pos.playerType == PlayerType.Self || pos.playerType == PlayerType.Teammate)
                    {

                        totalBlueTemp += pos.gemTotal;

                    }
                    else
                    {
                        totalRedTemp += pos.gemTotal;
                    }
                }

                RoomEnemyTeamGemTotalValue = totalRedTemp;
                RoomSelfTeamGemTotalValue = totalBlueTemp;

                if (RoomEnemyTeamGemTotalValue >= GemWinCount || RoomSelfTeamGemTotalValue >= GemWinCount)
                {

                    ToolBox.GetComponent<Toolbox>().BlueGem = totalBlueTemp;
                    ToolBox.GetComponent<Toolbox>().RedGem = totalRedTemp;
                    ToolBox.GetComponent<Toolbox>().StartCountDown();
                }
                else
                {
                    ToolBox.GetComponent<Toolbox>().CancelCountDown();
                }
            }
            //控制金库攻防输赢结束
            else if (ModenName == "HYLDJinKuGongFang")
            {
                //ToolBox.GetComponent<Toolbox>().ChangeCenternText(BlueBP.ToString());
                ToolBox.GetComponent<Toolbox>().ChangeLeftUpText(BlueBP.ToString());
                ToolBox.GetComponent<Toolbox>().ChangeRightUpText(RedBP.ToString());
                if (RedBP <= 0 || BlueBP <= 0)
                    
                {
                    if (RedBP <= 0) 玩家输了吗 = false;
                    else 玩家输了吗 = true;
                    ToolBox.GetComponent<Toolbox>().游戏结束方法();
                }
            }
        }   
        */
    }

    // 已删除 bulletHurts：全项目零消费点的死数组（P3'-2 清理）。
}
public enum PlayerType
{
    none,
    Self,
    Teammate,
    Enemy,

}
public class PlayerInformation
{
    public int teamID { get; private set; }
    // P3'-3c 删除：是否有防护罩 / isCanCure / isCanCure1 / isPoisoning。
    //
    // 它们都是「只在客户端生效、服务端不知道」的机制状态：
    //   · 是否有防护罩 —— 服务端不实现护盾；原实现是客户端本地 3 秒计时后自行关闭，
    //     属客户端自决玩法状态。默认值还是 true，删掉前它在联机下会一直为 true，
    //     但读取方（shell.cs / Boom.cs 的碰撞回调）在联机下都不可达，所以无实际影响。
    //   · isCanCure / isCanCure1 —— 回血门槛。isCanCure 全项目**没有任何地方置 true**，
    //     即回血条件恒不成立，本身就是死配置。
    //   · isPoisoning —— 毒 tick 的状态位，唯一写入点在不可达的 shell.cs 碰撞回调。
    //
    // 对应机制一并从 PlayerLogic 删除，理由见其类注释。
    public string playerName;
    public bool isNotDie = false;
    public GameObject body;
    public Vector3 playerPositon;
    public float playerMoveX ;
    public float playerMoveY ;
    public Vector3 playerMoveDir;
    public float playerMoveMagnitude;

    private Animator animator;
    public Animator bodyAnimator
    {
        get
        {
            if (animator == null)
            {
                animator = body.GetComponent<PlayerLogic>().bodyAnimator;
            }
            return animator;
        }
    }
    public int playerBloodValue ;

    /// <summary>
    /// 本玩家的**有效**最大生命值。
    ///
    /// <para>
    /// 这是「每玩家状态」，不是配置：初始值取自共享数值表，联机下会被服务端下发的权威值覆盖
    /// （见 <c>BattleData.HitEvent</c>），道具等运行期修正也加在这里。
    /// 历史实现把这三个来源都塞进 <c>hero.BloodValue</c>（共享配置对象），
    /// 导致「改一个玩家的血量会改到所有同英雄玩家」——P3'-2 已拆开。
    /// </para>
    /// </summary>
    public int playerBloodMax ;

    /// <summary>
    /// 本玩家的**有效**子弹伤害（配置值 + 道具等运行期修正）。
    ///
    /// <para>只影响客户端表现（弹体上的伤害数字/特效强度）——真正的伤害判定在服务端。</para>
    /// </summary>
    public int bulletDamage ;

    public int playerManaValue=90;
    public int gemTotal = 0;
    
    public FireState fireState;
    public Vector3 fireTowards ;

    public bool isAutoFire;
    public PlayerType playerType;
    public Hero hero;
    public bool beeReady = false;
    public bool isPoisoning = false;
    public bool 被控制 = false;
    public bool 可以按大招 = false;
    public float 最大能量 = 200;
    public float 当前能量 = 0;
    public float 移动速度 = -1;
    public int 炮台数量 = 0;
    public PlayerInformation()
    {}

    public PlayerInformation(Vector3 playerPositon, string playerName, Hero hero, int playerTeam, PlayerType playerType)
    {
        this.playerName = playerName;
        this.playerPositon = playerPositon;
        this.hero = hero;
        // 有效值的初值来自共享配置；之后由权威覆盖 / 道具修正，都不再回写配置。
        this.playerBloodValue = hero.BloodValue;
        this.playerBloodMax = hero.BloodValue;
        this.bulletDamage = hero.bulletDamage;
        移动速度 = hero.移动速度;
        teamID = playerTeam;
        this.playerType = playerType;
    }
    public PlayerInformation(Vector3 playerPositon)
    {
        this.playerPositon = playerPositon;
    }

 
}
public enum FireState
{
    none,
    //PstolNormalAuto,
    PstolNormal,
    PstolSuper,
    ShotgunNormal,
    ShotgunSuper,
}
public enum WeaponType
{
    Gun = 0,
    Rifle = 1,
    Rocket = 2,
    MAX
}

/*
 *   LuoSha,
    BoKe,
    KaEr,
    YaQi,
    AiErPuLiMo,
    BaBite,
    AiMei,
    BoMu,
    NiTa,
    JieXi,
    BiBi
    FoLanKen,
    NaNi,
    MoTiSi,
    JiEn,
    PXianSheng,
    YaYa,
    ShaDi,
*/
