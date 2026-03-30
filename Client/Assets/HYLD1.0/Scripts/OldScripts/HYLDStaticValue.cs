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
    [Header("大招")]
    public GameObject[] 大招实体;
    [Header("金库模式")]
    //金库生命值（金库模式专用）
    public static int RedBP = 50000;
    public static int BlueBP = 50000;
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
    public static LZJ.Fixed PlayerMoveX;
    public static LZJ.Fixed PlayerMoveY;

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

         RedBP = 50000;
         BlueBP = 50000;

    // Debug.LogError("11");
    //Logging.HYLDDebug.Log($"float {15 * 6.154646}  fixed:{(new LZJ.Fixed(15) * 6.154646f).ToFloat()}");
    //zyk增加部分      名字              名字     定位  血量 移速 进攻距离 子弹预制体 距离 宽度 子弹数量 伤害 角度 速度 每次发射数量 [间隔] 
    //Heros.Add(HeroName.BoKe,   new Hero("波克",  "战士",4680, 8,     3,5,       shells[0], 6,    1,     4,    260,  30,  10,     4));//ok
    //Heros.Add(HeroName.LuoSha, new Hero("罗莎",  "坦克",6750, 8,     1,3,       shells[3], 3,   0.5f,   3,    575,  60,  11,     1));//ok
    //Heros.Add(HeroName.BaBite, new Hero("8比特", "射手", 2730,6,     4,6,       shells[7], 6, 0.4f, 6, 600, 0, 20, 1,0.05f));//ok
    //Heros.Add(HeroName.LIAng, new Hero("布洛克", "射手", 2730, shells[8], 2, 0.3f, 1, 800, 0, 10, 1));//shell[8]有问题
    //Heros.Add(HeroName.ABo, new Hero("布洛克", "射手", 2730, shells[9], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[9]有问题
    //Heros.Add(HeroName.AiErPuLiMo, new Hero("布洛克", "射手", 2730, shells[10], 10, 0.5f, 10, 1555, 0, 10, 10));//shell[10]有问题
    //Heros.Add(HeroName.BaLi, new Hero("布洛克", "射手", 2730, shells[11], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[11]有bug
    //Heros.Add(HeroName.BeiYa, new Hero("布洛克", "射手", 2730, shells[12], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[12]有bug
    //Heros.Add(HeroName.BiBi, new Hero("布洛克", "射手", 2730, shells[13], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[13]有bug
    //Heros.Add(HeroName.BiBi, new Hero("布洛克", "射手", 2730, shells[14], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[14]有bug
    //Heros.Add(HeroName.BiBi, new Hero("布洛克", "射手", 2730, shells[15], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[15]有bug
    // Heros.Add(HeroName.BiBi, new Hero("布洛克", "射手", 2730, shells[16], 10, 0.5f, 1, 1555, 0, 10, 1));//shell[12]有bug
    //Heros.Add(HeroName.BiBi, new Hero("布洛克", "射手", 2730, shells[17], 10, 0.5f, 1, 1555, 0, 1, 1));//shell[12]有bug
    //以上是劣质特效，特效被删了的子弹。
    //以下是实体子弹
        Heros.Clear();
        //   名字                    名字    定位        血量  移速  攻击距离   每次发射间隔    装弹速度   子弹预制体     距离    宽度  子弹数量 伤害 角度 速度 每次发射数量[间隔]  大招实体                   爆破手
        Heros.Add(HeroName.DaLiEr,      new Hero("达里尔", "坦克",      5760, 3.9f,     1, 3,        0.5f,       2,         BetterShells[0],  6,     0,    30,    90,  45,    16,   15, 0.1f));//ok
        Heros.Add(HeroName.GongNiu,     new Hero("公牛",   "坦克",      5880, 3.9f,     1, 3,        0.5f,       1,         BetterShells[1],  4,     0,    50,    45,  40,     8,   10, 0.01f));//ok
        Heros.Add(HeroName.RuiKe,       new Hero("瑞科",   "射手",      3250, 3.9f,     4, 5,        1f,         1,         BetterShells[2],  10, 0.04f,     5,   400,   0,    13,    1, 0.1f,        大招实体[0]));//ok
        Heros.Add(HeroName.ABo,         new Hero("阿渤",   "战士",      3600, 3.9f,     4, 5,        0.6f,       1,         BetterShells[3],  6,     0,     3,   520,  15,    10,    1, 0.1f,         大招实体[1]));//ok
        Heros.Add(HeroName.MaiKeSi,     new Hero("麦克斯", "辅助",      3200, 4.08f,    3, 5,        0.3f,       2,         BetterShells[4],  7,     0,     4,   320,  10,    14,    1, 0.05f,        大招实体[2]));//ok
        Heros.Add(HeroName.TaLa,        new Hero("塔拉",   "战士",      3400, 3.9f,     2, 5,        0.4f,       1,         BetterShells[5],  7,     0,     3,   460,  45,    10,    3, 0.05f,        大招实体[3]));//ok
        Heros.Add(HeroName.PaMu,        new Hero("帕姆",   "辅助",      4800, 3.78f,    1, 6,        1f,         1,         BetterShells[6],  9,     0,     9,   260,  50,    14,    2, 0.15f,        大招实体[4]           ,Booms[3],false,30));//ok
        Heros.Add(HeroName.HeiYa,       new Hero("黑鸦",   "致伤突袭者",2400, 4.2f,     4, 5,        0.3f,       2,         BetterShells[7],  7,     0,     3,   320,  30,    12,    3, 0.08f,        大招实体[5]));//ok        
        Heros.Add(HeroName.PeiPei,      new Hero("佩佩",   "射手",      3240, 3.9f,     5, 6,        1f,         1,         BetterShells[8], 11,  0.5f,     1,   650,   0,     12,   1, 0,            大招实体[6]));//ok
        Heros.Add(HeroName.LiAng ,      new Hero("里昂",   "潜行突袭者",4800, 3.96f,    1, 5,        0.6f,       1,         BetterShells[9],  7,     0,     4,   680,  10,    11,    1, 0.09f));//ok
        Heros.Add(HeroName.GeEr,        new Hero("格尔",   "辅助",      4420, 3.9f,     3, 5,        0.6f,       1,         BetterShells[10], 7,    3f,     6,   448,   0,    11,    6, 0,            大招实体[7]));//ok
        Heros.Add(HeroName.PanNi,       new Hero("潘妮",   "战士",      4160, 3.9f,     5, 4,        0.6f,       1,         BetterShells[11], 6, 0.01f,     1,   400,   0,    11,    1, 0,            大招实体[8]));//ok
        Heros.Add(HeroName.KeErTe,      new Hero("柯尔特", "射手",      3640, 4.05f,    5, 5,        1f,         1,         BetterShells[12], 8,  0.4f,     6,    340,  0,    12,    2, 0.1f,            大招实体[9]));//ok
        Heros.Add(HeroName.XueLi,       new Hero("雪莉",   "战士",      4680, 3.9f,     1, 3,        0.5f,       1,         BetterShells[13], 6,  0.5f,    20,     80, 30,    11,    5, 0.005f,       大招实体[10]));//ok
        Heros.Add(HeroName.BuLuoKe,     new Hero("布洛克", "射手",      2730, 3.9f,     6, 6,        0.8f,       1,         BetterShells[14],10,  0.5f,     1,   1155,  0,    10,    1, 0,            大招实体[11]));//ok
        Heros.Add(HeroName.BeiYa,       new Hero("贝亚",   "射手",      2400, 3.9f,     6, 6,        1f,         9,         BetterShells[15],10,  0.5f,     1,    800,  0,    10,    1, 0,            大招实体[12]));//ok
        Heros.Add(HeroName.SiPaiKe,     new Hero("斯派克", "射手",      2400, 3.9f,     2, 5,        0.4f,       1,         BetterShells[16],10,  0.5f,     1,      0,  0,    10,    1, 0,            大招实体[13]));//ok
        Heros.Add(HeroName.BaoPoMaiKe,  new Hero("爆破麦克", "投掷手",  2940, 3.9f,     3, 5,        0.5f,       1,         BetterShells[17], 5,    0.2f,   2,    840,  20,    5,    1, 0.01f,        大招实体[14],              Booms[2],true, 10));//ok
        Heros.Add(HeroName.BaLi,        new Hero("巴利", "投掷手",      2880, 3.9f,     3, 5,        0.5f,       1,         BetterShells[18], 5,    2f,     1,    816,  0,    5,    1, 0.01f,         大招实体[15],              Booms[0],true,10));//ok
        Heros.Add(HeroName.DiKe,        new Hero("迪克", "投掷手",      2200, 3.9f,     3, 5,        0.5f,       1,         BetterShells[19], 5,    0.4f,   4,    680,  20,    5,    4, 0.01f,        大招实体[16],              Booms[1],true,10));//ok

        // ── 大招子弹参数配置（数据驱动，消除 Attack() 中的 if-else 硬编码） ──
        // 麦克斯：移动型大招，不走子弹系统
        Heros[HeroName.MaiKeSi].isSuperMovingType = true;
        Heros[HeroName.MaiKeSi].normalAttackManaRecover = 3;
        // 瑞科：大招 shootWidth=0.2, shootDistance=14, speed=19, bulletCount=12
        Heros[HeroName.RuiKe].superBullet = new SuperBulletParams(
            shootDistance: 14f, shootWidth: 0.2f, bulletCount: 12, speed: 19f);
        // 柯尔特：大招 shootWidth=0.2, shootDistance=12, speed=18, bulletCount=12
        Heros[HeroName.KeErTe].superBullet = new SuperBulletParams(
            shootDistance: 12f, shootWidth: 0.2f, bulletCount: 12, speed: 18f);
        // 雪莉：大招 LaunchAngle=40, bulletCount=40, speed=14
        Heros[HeroName.XueLi].superBullet = new SuperBulletParams(
            shootDistance: 6f, shootWidth: 0.5f, bulletCount: 40, LaunchAngle: 40f, speed: 14f);
        // 格尔：大招 shootWidth=4, bulletCountByEachTime=4, bulletCount=4, speed=14, EachTimebulletsShootSpace=0
        Heros[HeroName.GeEr].superBullet = new SuperBulletParams(
            shootDistance: 7f, shootWidth: 4f, bulletCount: 4, speed: 14f,
            bulletCountByEachTime: 4, EachTimebulletsShootSpace: 0f);
        // 贝亚：大招 shootWidth=0.8, bulletCountByEachTime=6, bulletCount=6, bulletDamage=60
        Heros[HeroName.BeiYa].superBullet = new SuperBulletParams(
            shootDistance: 10f, shootWidth: 0.8f, bulletCount: 6, bulletDamage: 60,
            speed: 10f, bulletCountByEachTime: 6);
        Heros[HeroName.BeiYa].normalAttackManaCost = 90;
        // 帕姆：大招全覆写
        Heros[HeroName.PaMu].superBullet = new SuperBulletParams(
            shootDistance: 2f, shootWidth: 1f, bulletCount: 1, bulletDamage: 300,
            LaunchAngle: 0f, speed: 5f, bulletCountByEachTime: 1,
            EachTimebulletsShootSpace: 0f, IsParadola: true);

        // ── 统一赋值 heroName 枚举（反向映射） ──
        foreach (var kvp in Heros)
        {
            kvp.Value.heroName = kvp.Key;
        }

        if (!ISNet)
        {
            ModenName = testMOdel.ToString();
        }
        //Debug.LogError($"START {ModenName}");
        if (ModenName == ModelName.HYLDBaoShiZhengBa.ToString())
        {
            BattleManger battleManger = gameObject.AddComponent<HYLDBaoShiZhengBaManger>();
            battleManger.ISNet = ISNet;
            battleManger._StartGameAni = _StartGameAni;
            battleManger.BG = BG;
            battleManger.ScenseBuildLogic = ScenseBuildLogic;
            battleManger.toolbox = ToolBox.GetComponent<Toolbox>();
            battleManger.Init();
        }
        else if (ModenName == ModelName.HYLDJinKuGongFang.ToString())
        {
            BattleManger battleManger = gameObject.AddComponent<BattleManger>();
            battleManger.ISNet = ISNet;
            battleManger._StartGameAni = _StartGameAni;
            battleManger.BG = BG;
            battleManger.ScenseBuildLogic = ScenseBuildLogic;
            battleManger.toolbox = ToolBox.GetComponent<Toolbox>();
            battleManger.Init();
        }
        else
        {
            Debug.LogError($"cant find {ModenName}");
        }
        
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

                if (RoomEnemyTeamGemTotalValue >= 10 || RoomSelfTeamGemTotalValue >= 10)
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

    public static readonly int[] bulletHurts =new int[5]{100,235,300,400,500};
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
    public bool 是否有防护罩 = true;
    public string playerName;
    public bool isCanCure = false;
    public bool isCanCure1 = false;
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
    public float 最大能量 = 3000;
    public float 当前能量 = 0;
    public float 移动速度 = -1;
    public int 炮台数量 = 0;
    public PlayerInformation()
    {}

    public PlayerInformation(Vector3 playerPositon, string playerName, Hero hero, int playerTeam, PlayerType playerType)
    {
        this.playerName = playerName;
        this.playerPositon = playerPositon;
        this.playerBloodValue = hero.BloodValue;
        this.hero = hero;
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
public enum HeroName
{   //         子弹自身技能      大招         妙具         星辉
    XueLi=0,      //ok            ok
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
    TaLa,     //ok
    MaiKeSi,   //ok
    SiPaiKe,  //ok
    HeiYa,     //ok
    LiAng,     //ok
    PaMu  ,   //ok
    RuiKe,    //ok              ok
  
    
    
  

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
/// <summary>
/// 大招子弹参数覆写（绝对值）。
/// 为 null 时表示该英雄大招不走子弹系统（如移动型大招）或无大招。
/// </summary>
public class SuperBulletParams
{
    public float shootDistance;
    public float shootWidth;
    public int   bulletCount;
    public int   bulletDamage;   // -1 表示沿用普通攻击伤害
    public float LaunchAngle;    // -1 表示沿用普通攻击角度
    public float speed;
    public int   bulletCountByEachTime; // -1 表示沿用普通攻击值
    public float EachTimebulletsShootSpace; // -1 表示沿用普通攻击值
    public bool  IsParadola;
    public float high;           // -1 表示沿用普通攻击值
    public GameObject Boom;      // null 表示沿用普通攻击值

    public SuperBulletParams(float shootDistance, float shootWidth, int bulletCount,
        int bulletDamage = -1, float LaunchAngle = -1, float speed = -1,
        int bulletCountByEachTime = -1, float EachTimebulletsShootSpace = -1,
        bool IsParadola = false, float high = -1, GameObject Boom = null)
    {
        this.shootDistance = shootDistance;
        this.shootWidth = shootWidth;
        this.bulletCount = bulletCount;
        this.bulletDamage = bulletDamage;
        this.LaunchAngle = LaunchAngle;
        this.speed = speed;
        this.bulletCountByEachTime = bulletCountByEachTime;
        this.EachTimebulletsShootSpace = EachTimebulletsShootSpace;
        this.IsParadola = IsParadola;
        this.high = high;
        this.Boom = Boom;
    }
}

public class Hero
{
    //英雄基本属性
    public string Name;
    public HeroName heroName;
    public string HeroPositioning;
    public int BloodValue;
    public float 移动速度;
    public float 最小离敌人距离;
    public float 攻击距离;
    //英雄枪属性（普通攻击）
    public GameObject shell;
    public float shootDistance;
    public float shootWidth;
    public int bulletCount;
    public int bulletDamage;
    public float LaunchAngle;
    public float speed;
    public int bulletCountByEachTime;
    public float EachTimebulletsShootSpace;
    public bool IsParadola;//是否抛物线
    public GameObject Boom;
    public float high;
    public float 每次发射可以发射间隔手感问题;
    public int 装弹速度;

    //英雄大招属性
    public GameObject 大招实体;
    /// <summary>true = 大招使用移动型大招实体（如麦克斯），不走子弹系统</summary>
    public bool isSuperMovingType;
    /// <summary>大招子弹参数覆写。null 表示无大招或大招不走子弹</summary>
    public SuperBulletParams superBullet;

    //英雄普通攻击蓝耗参数
    /// <summary>普通攻击蓝耗（默认30，贝亚=90）</summary>
    public int normalAttackManaCost = 30;
    /// <summary>普通攻击后蓝量回复（默认0，麦克斯=3）</summary>
    public int normalAttackManaRecover = 0;

    public Hero(string _Name,string _HeroPositioning,int _BloodValue, float 移速,float 进攻距离,float 攻击距离喽, float 每次发射间隔, int _装弹速度,GameObject _shell,float _shootDistance, float _shootWidth, int _bulletCount, int _bulletDamage, float _LaunchAngle, float _speed, int _bulletCountByEachTime, float _EachTimebulletsShootSpace = 0.1f, GameObject _大招实体 = null, GameObject _Boom=null, bool _IsParadola=false,float _high=0)
    {
        Name = _Name;
        HeroPositioning = _HeroPositioning;
        BloodValue = _BloodValue;
        shell = _shell;


        shootDistance = _shootDistance;
        shootWidth = _shootWidth;
        bulletCount = _bulletCount;
        bulletDamage = _bulletDamage;
        LaunchAngle = _LaunchAngle;
        speed = _speed;
        bulletCountByEachTime = _bulletCountByEachTime;
        EachTimebulletsShootSpace = _EachTimebulletsShootSpace;

        high=_high;
        Boom = _Boom;
        IsParadola = _IsParadola;
        移动速度=移速;
        最小离敌人距离 = 进攻距离;
        攻击距离 = 攻击距离喽;
        每次发射可以发射间隔手感问题 = 每次发射间隔;
        装弹速度 = _装弹速度;
        大招实体 = _大招实体;
    }
}