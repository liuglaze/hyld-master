/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/11 20:19:54
    Description:     UI基于MVC框架
*****************************************************/
using LongZhiJie;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
/// <summary>
///  UI基于MVC框架
/// </summary>
namespace MVC
{
    /// <summary>
    /// 窗体类型
    /// </summary>
    public enum WindowType
    {
        LoginWindow,
        WeaponShop,
        TipsWindow,
        AchievementWindow,
    }
    /// <summary>
    /// 场景类型，目的：根据提供场景类型进行预加载
    /// </summary>
    public enum ScenesType
    {
        None,
        Login,
        Battle,
        StartMenu,
    }
    public class Singleton<T> where T : new()//T 约束 只能是class类型的
    {
        static T instance;

        public static T Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new T();
                }
                return instance;
            }
        }
    }
    namespace View
    {
        /// <summary>
        /// 窗体基类
        /// </summary>
        public class BaseWindow
        {
            //窗体
            protected Transform transform;
            //资源名称
            protected string resName;
            //窗体类型
            protected WindowType selfType;
            //场景类型
            protected ScenesType scenesType;
            //UI控件
            protected Button[] buttonList;
            protected Text[] textList;
            public virtual void Reset()
            {

            }
            public bool IsOpen()
            {
                return transform.gameObject.activeSelf;
            }
            //需要给子类提供的接口
            public void Init(Transform transform)
            {
                this.transform = transform;
                Awake();
            }
            //初始化
            protected virtual void Awake()
            {
                //参数为true表示包括隐藏的物体
                buttonList = transform.GetComponentsInChildren<Button>(true);
                textList = transform.GetComponentsInChildren<Text>(true);


                //注册UI事件（细节由子类实现）
                RegisterUIEvent();
            }
            //UI事件的注册
            protected virtual void RegisterUIEvent()
            {
                foreach (Button btn in buttonList)
                {
                    btn.name = btn.name.Split(' ')[0];
                    if (LZJExternTool.IsSubClassOf(Type.GetType("MVC.View." + btn.name.Substring(3)), typeof(MVC.View.BaseWindow)))
                    {
                        btn.onClick.AddListener(() =>
                        {
                            LongZhiJie.LoginUIManger manger = (LongZhiJie.LoginUIManger)UIRoot.UIManger;
                            manger.recycleDic[btn.name.Substring(3)].Open();
                        });
                        continue;
                    }

                    switch (btn.name)
                    {
                        case "btnClose":
                            btn.onClick.AddListener(() =>
                            {
                                Close();
                            }); break;


                        case "btnExit":
                            btn.onClick.AddListener(() =>
                            {
                                Application.Quit();

                            }); break;
                    }
                }
            }
            //添加监听游戏事件
            protected virtual void OnAddListener() { }
            //移除游戏事件
            protected virtual void OnRemoveListener() { }
            //每次打开
            protected virtual void OnEnable() { }
            //每次关闭
            protected virtual void OnDisable() { }
            //每帧更新
            public virtual void Update(float deltaTime) { }

            //-----------针对WindowManager的方法 (被WindowManager调用)
            public void Open()
            {
                if (transform == null)
                {
                    if (Create())
                    {
                        Awake();  //初始化
                    }
                }
                if (!transform.gameObject.activeSelf)
                {
                    UIRoot.SetParent(transform, true, selfType == WindowType.TipsWindow);
                    transform.gameObject.SetActive(true);
                    OnEnable(); //调用激活时的事件
                    OnAddListener();  //添加事件
                }
            }
            public void Close(bool isForceClose = false)
            {
                if (transform.gameObject.activeSelf)
                {
                    OnRemoveListener();  //移除事件的监控
                    OnDisable();  //隐藏的事件
                    if (!isForceClose)  //非强制
                    {
                        transform.gameObject.SetActive(false);
                        //将窗口从work区域放到recycle区域
                        UIRoot.SetParent(transform, false, false);
                    }
                    else
                    {
                        GameObject.Destroy(transform.gameObject);
                        transform = null;
                    }
                }
            }
            public void PreLoad()
            {
                if (transform == null)
                {
                    if (Create())
                    {
                    }
                }
            }

            //获取场景类型
            public ScenesType GetScenesType()
            {
                return scenesType;
            }
            //获取窗口类型
            public WindowType GetWindowType()
            {
                return selfType;
            }
            //获取根节点
            public Transform GetRoot()
            {
                return transform;
            }


            //--------内部---------
            public bool Create()
            {
                //资源名称为空，则无法创建
                if (string.IsNullOrEmpty(resName))
                {
                    return false;
                }
                //窗体引用为空，则创建实例
                if (transform == null)
                {
                    //根据资源名称加载物体
                    GameObject obj = Resources.Load<GameObject>(resName);
                    if (obj == null)
                    {
                        Logging.HYLDDebug.LogError($"未找到UI预制件{selfType}");
                        return false;
                    }
                    transform = GameObject.Instantiate(obj).transform;

                    transform.gameObject.SetActive(false);

                    UIRoot.SetParent(transform, false, selfType == WindowType.TipsWindow);

                    return true;
                }
                return true;
            }
        }
        /// <summary>
        /// 成就窗口
        /// </summary>
        public class AchievementWindow : BaseWindow
        {
            public override void Reset()
            {
                base.Reset();
                PlayerPrefs.SetString(PlayerPrefabsEnum.SkillBossTime.ToString(), "----");
                PlayerPrefs.SetInt(PlayerPrefabsEnum.SkillBossCount.ToString(), 0);
                PlayerPrefs.SetInt(PlayerPrefabsEnum.SkillBossMaxCount.ToString(), 0);
            }
            protected override void Awake()
            {
                base.Awake();
                resName = "UI/Window/AchievementWindow";
                selfType = WindowType.AchievementWindow;
                scenesType = ScenesType.StartMenu;

                transform.Find(PlayerPrefabsEnum.SkillBossTime.ToString()).gameObject.transform.GetChild(0).GetComponent<Text>().text =
                PlayerPrefs.GetString(PlayerPrefabsEnum.SkillBossTime.ToString(), "----");

                transform.Find(PlayerPrefabsEnum.SkillBossMaxCount.ToString()).gameObject.transform.GetChild(0).GetComponent<Text>().text =
                PlayerPrefs.GetInt(PlayerPrefabsEnum.SkillBossMaxCount.ToString(), 0).ToString();

                transform.Find(PlayerPrefabsEnum.SkillBossCount.ToString()).gameObject.transform.GetChild(0).GetComponent<Text>().text =
                PlayerPrefs.GetInt(PlayerPrefabsEnum.SkillBossCount.ToString(), 0).ToString();
            }
            protected override void OnAddListener()
            {
                base.OnAddListener();
            }
            protected override void OnDisable()
            {
                base.OnDisable();
            }
            protected override void OnEnable()
            {
                base.OnEnable();
            }
            protected override void OnRemoveListener()
            {
                base.OnRemoveListener();
            }
            protected override void RegisterUIEvent()
            {
                base.RegisterUIEvent();
                foreach (Button btn in buttonList)
                {
                    switch (btn.name)
                    {
                        case "":
                            btn.onClick.AddListener(() =>
                            {
                                Close();
                            }); break;
                    }
                }

            }
            public override void Update(float deltaTime)
            {
                base.Update(deltaTime);

                //每帧监听，按下C关闭此窗口

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                }

            }
        }
        /// <summary>
        /// 武器商店窗口
        /// </summary>
        public class ShopWindow : BaseWindow
        {
            protected override void Awake()
            {
                base.Awake();
                resName = "UI/Window/WeaponShopWindow";
                selfType = WindowType.WeaponShop;
                scenesType = ScenesType.StartMenu;
            }
            protected override void OnAddListener()
            {
                base.OnAddListener();
            }
            protected override void OnDisable()
            {
                base.OnDisable();
            }
            protected override void OnEnable()
            {
                base.OnEnable();
            }
            protected override void OnRemoveListener()
            {
                base.OnRemoveListener();
            }
            protected override void RegisterUIEvent()
            {
                base.RegisterUIEvent();
                foreach (Button btn in buttonList)
                {
                    switch (btn.name)
                    {
                        case "btnBuy":
                            btn.onClick.AddListener(() => {
                                OnBuyButton(btn);
                            });
                            break;
                    }
                }

            }
            public override void Update(float deltaTime)
            {
                base.Update(deltaTime);

                //每帧监听，按下ECS关闭此窗口
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                }
            }
            private void OnBuyButton(Button btn)
            {
                Logging.HYLDDebug.Log("点击了BuyButton");
                //通过Control修改Model
                if (Ctrl.WeaponShopCtrl.Instance.BuyWeapon(1))
                {
                    ///TODO: 更改武器/操作系统
                    /*
                    int count = Ctrl.WeaponShopCtrl.Instance.GetWeapon(1).CurCount;
                    btn.transform.parent.Find("Tips").
                        GetComponent<Text>().text = "已购买倚天剑，倚天剑剩余" + count;
                    */
                }
                else
                    btn.transform.parent.Find("Tips").
                        GetComponent<Text>().text = "购买失败";
            }
        }

    }
    namespace Model
    {
        /// <summary>
        /// 成就模型系统
        /// </summary>
        public class AchievementModel : Singleton<AchievementModel>
        {
            private Dictionary<int, Achievement> propDic = new Dictionary<int, Achievement>();
            public AchievementModel()
            {
                //TODO：后期换成json表添加成就系统
                Add(new Achievement(1, "击杀boss", 10, 500, 10));
            }
            //成就+1
            public void AccomplishAnAchievement(int propId)
            {
                if (!propDic.ContainsKey(propId))
                {
                    Logging.HYLDDebug.LogError("成就列表未存在此成就");
                }
                //完成一次任务
                propDic[propId].AccomplishAnAchievement();
            }

            public void Add(Achievement prop)
            {
                if (!propDic.ContainsKey(prop.Id))
                {
                    propDic[prop.Id] = prop;
                }
            }
            public Achievement GetAchievement(int id)
            {
                if (!propDic.ContainsKey(id))
                {
                    Logging.HYLDDebug.LogError("成就列表未存在此成就");
                }
                return propDic[id];
            }
            public void Reset()
            {
                foreach (var achieve in propDic.Values)
                {
                    achieve.Reset(10);//TODO:JOSO 优化
                }
            }
        }
        /// <summary>
        /// 成就模型系统
        /// </summary>
        public class WeaponShopModel : Singleton<WeaponShopModel>
        {
            private Dictionary<int, Weapon> propDic = new Dictionary<int, Weapon>();
            public WeaponShopModel()
            {
                //TODO：后期换成json表添加成就系统
                Add(new Weapon(1, "方块枪", 2500));
            }
            //成就+1
            public bool BuyWeapon(int propId)
            {
                if (!propDic.ContainsKey(propId))
                {
                    Logging.HYLDDebug.LogError("枪未存在");
                }
                //完成一次任务
                return propDic[propId].BuyWeapon(10000);//TODO : 此处需要增加玩家的金币接口
            }

            public void Add(Weapon weapon)
            {
                if (!propDic.ContainsKey(weapon.Id))
                {
                    propDic[weapon.Id] = weapon;
                }
            }
            public Weapon GetWeapon(int id)
            {
                if (!propDic.ContainsKey(id))
                {
                    Logging.HYLDDebug.LogError("成就列表未存在此成就");
                }
                return propDic[id];
            }
            public void Reset()
            {
                foreach (var achieve in propDic.Values)
                {
                    achieve.Reset();//TODO:JOSO 优化
                }
            }
        }
    }
    namespace Ctrl
    {
        public class AchievementCtrl : Singleton<AchievementCtrl>
        {
            //给Store View分配的接口，用来给Store Model添加道具prop
            public void SaveProp(Achievement prop)
            {
                Model.AchievementModel.Instance.Add(prop);
            }
            public void AccomplishAnAchievement(int id)
            {
                Model.AchievementModel.Instance.AccomplishAnAchievement(id);
            }
            public Achievement GetAchievement(int id)
            {
                return Model.AchievementModel.Instance.GetAchievement(id);
            }
        }
        public class WeaponShopCtrl : Singleton<WeaponShopCtrl>
        {
            //给WeaponShopView分配的接口，用来给WeaponShop Model添加道具prop
            public void AddWeapon(Weapon weapon)
            {
                Model.WeaponShopModel.Instance.Add(weapon);
            }
            public bool BuyWeapon(int id)
            {
                return Model.WeaponShopModel.Instance.BuyWeapon(id);
            }
            public Weapon GetWeapon(int id)
            {
                return Model.WeaponShopModel.Instance.GetWeapon(id);
            }
        }
    }

    /// <summary>
    /// 成就类
    /// id
    /// 目标说明
    /// 已完成数/目标数
    /// 可获得成就点
    /// </summary>
    public class Achievement
    {
        private int curcount;
        public int Id
        {
            get;
            private set;
        }
        public string Target
        {
            get;
            private set;
        }
        public int CurCount
        {
            get { return curcount; }
            set { curcount = value; }
        }
        public int TargetCount
        {
            get;
            private set;
        }
        private int EeveyTimeIncreaseTargetCount;
        public int Reward
        {
            get;
            private set;
        }
        public Achievement(int id, string name, int TargetCount, int reward, int EeveyTimeIncreaseTargetCount)
        {
            this.Id = id;
            this.Target = name;
            this.CurCount = PlayerPrefs.GetInt(Target, 0);
            this.TargetCount = PlayerPrefs.GetInt(Target + "Target", TargetCount);
            Reward = reward;
            this.EeveyTimeIncreaseTargetCount = EeveyTimeIncreaseTargetCount;
        }
        public void Reset(int TargetCount)
        {
            PlayerPrefs.SetInt(Target + "Target", TargetCount);
            PlayerPrefs.SetInt(Target, 0);
        }
        public void AccomplishAnAchievement()
        {
            CurCount++;
            if (CurCount >= TargetCount)
            {
                PlayerPrefs.SetInt(Target + "Target", TargetCount + EeveyTimeIncreaseTargetCount);
                TargetCount = PlayerPrefs.GetInt(Target + "Target");
                CurCount = 0;
                PlayerPrefs.SetInt(Target, 0);
            }
            else
            {
                PlayerPrefs.SetInt(Target, CurCount);
            }
        }
    }

    /// <summary>
    /// 武器类
    /// id
    /// 武器类型
    /// 售价
    /// 是否已买
    /// </summary>
    public class Weapon
    {
        public int Id
        {
            get;
            private set;
        }
        public string name
        {
            get;
            private set;
        }
        public int Price
        {
            get;
            private set;
        }
        public bool IsBuy
        {
            get;
            private set;
        }
        public Weapon(int id, string name, int Price)
        {
            this.Id = id;
            this.name = name;
            this.Price = Price;
            IsBuy = (PlayerPrefs.GetInt(name, 0) == 1);
        }
        /// <summary>
        /// 买枪
        /// </summary>
        /// <param name="price"></param>
        /// <returns></returns>
        public bool BuyWeapon(int price)
        {
            if (Price <= price)
            {
                IsBuy = true;
                PlayerPrefs.SetInt(name, 1);
                return true;
            }
            return false;
        }

        public void Reset()
        {
            PlayerPrefs.SetInt(name, 0);
        }
    }
}

/// <summary>
/// 管理UIRoot预制体
/// </summary>
public static class UIRoot
{
    //UIRoot本尊
    static Transform transform;
    //回收的窗体：回收池
    static Transform recyclePool;
    //前台显示/工作的窗体
    static Transform workstation;
    //提示类型的窗体
    static Transform noticestation;
    public static UIBaseManger UIManger;
    static bool isInint = false;
    public static void Init(Transform _transform, UIBaseManger uIManger)
    {
        transform = _transform;
        UIManger = uIManger;

        recyclePool = transform.Find("recyclePool");

        workstation = transform.Find("workstationPool");

        noticestation = transform.Find("noticePool");

        isInint = true;
    }
    //对窗体的父panel设置
    public static void SetParent(Transform window, bool isOpen, bool isTipsWindow = false)
    {

        if (!isInint)
        {   //没有初始化
            Init(GameObject.FindWithTag("UIManger").transform, GameObject.FindWithTag("UIManger").transform.GetComponent<UIBaseManger>());
        }

        if (isOpen)     //是一个开启的面板
        {
            if (isTipsWindow)  //是一个提示面板
            {
                //窗体父Panel是noticestation
                //第二个参数意思是“是否启用世界坐标”
                window.SetParent(noticestation, false);
            }
            else
            {
                //窗体父Panel是workstation
                window.SetParent(workstation, false);
            }
        }
        else
        {
            //窗体父Panel是recyclePool
            window.SetParent(recyclePool, false);
        }
    }
}