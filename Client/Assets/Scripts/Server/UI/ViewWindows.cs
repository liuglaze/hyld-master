/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/14 20:46:19
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC.View
{
    public class GameExplainWindow : BaseWindow
    {
        
    }
    public class PleaseExpectingWindow : BaseWindow
    {
        protected override void Awake()
        {
            base.Awake();
            resName = "UI/Window/AchievementWindow";
            selfType = WindowType.TipsWindow;
            scenesType = ScenesType.StartMenu;

        }
        private float Timer = 0;
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            Timer += deltaTime;
            if (Timer >= 1.5f)
            {
                Timer = 0;
                Close();
            }
        }
    }
    public class MainWindow :BaseWindow
	{
        protected override void Awake()
        {

           // Logging.HYLDDebug.LogError(transform);
            foreach (Image image in transform.GetComponentsInChildren<Image>(true))
            {

                if (image.transform.name.Length > 2 && image.transform.name.Substring(0, 3) == "btn")
                     image.color-=new Color(0,0,0,image.color.a*0.98f) ;
            }
            base.Awake();
            if(PlayerPrefs.GetInt(PlayerPrefabsEnum.BigSkillTeach.ToString(),0)==0&&PlayerPrefs.GetInt(PlayerPrefabsEnum.IsEasyFinish.ToString(), 0) == 1)
            {
                PlayerPrefs.SetInt(PlayerPrefabsEnum.BigSkillTeach.ToString(), 1);
                LongZhiJie.LoginUIManger manger = (LongZhiJie.LoginUIManger)UIRoot.UIManger;
                manger.recycleDic[nameof(RewardWindow)].Open();
            }

        }
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            foreach (Button btn in buttonList)
            {
                switch (btn.name)
                {

                    case "btnPreGame":
                        btn.onClick.AddListener(() =>
                        {
                            LongZhiJie.LoginUIManger manger = (LongZhiJie.LoginUIManger)UIRoot.UIManger;
                            manger.recycleDic[nameof(PreGameWindow)].Open();
                        }); break;
                   case "btnRecord":
                        btn.onClick.AddListener(() =>
                        {
                            LongZhiJie.LoginUIManger manger = (LongZhiJie.LoginUIManger)UIRoot.UIManger;
                            manger.recycleDic[nameof(RecordWindow)].Open();
                        }); break;
            }
            }
        }
    }
    public class MembersListWindow : BaseWindow
    {

    }
    public class RecordWindow : BaseWindow
    {

    }
    public class RewardWindow : BaseWindow
    {
        protected override void Awake()
        {
            base.Awake();
            resName = "UI/Window/AchievementWindow";
            selfType = WindowType.TipsWindow;
            scenesType = ScenesType.StartMenu;
        }
        private float Timer = 0;
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            Timer += deltaTime;
            if (Timer >= 3f)
            {
                Timer = 0;
                Close();
            }
        }
    }

    public class PreGameWindow : BaseWindow
    {
        private List<GameObject> games = new List<GameObject>();
        private Dictionary<string,GameObject> Guns=new Dictionary<string, GameObject>();

        public override void Reset()
        {
            PlayerPrefs.SetInt(PlayerPrefabsEnum.isFirst.ToString(), 0);
            base.Reset();
           
            PlayerPrefs.SetInt(PlayerPrefabsEnum.IsEasyFinish.ToString(), 0);
            PlayerPrefs.SetInt(PlayerPrefabsEnum.BigSkillTeach.ToString(), 0);
        }
        protected override void Awake()
        {
            base.Awake();
            Transform guns = transform.Find("Guns").transform;
            foreach (Transform gun in guns)
            {
                Guns.Add(gun.name,gun.gameObject);
                gun.gameObject.SetActive(false);
                /*
                if (PlayerPrefs.GetString(PlayerPrefabsEnum.CurGun.ToString(), "Gun1") == gun.name)
                {
                    gun.gameObject.SetActive(true);
                    TapTapStaticValue.CurGun = (LongZhiJie.EnumGun)System.Enum.Parse(typeof(LongZhiJie.EnumGun),gun.name);
                }
                else
                {
                    gun.gameObject.SetActive(false);
                }
                */
            }
            gunname = PlayerPrefs.GetString(PlayerPrefabsEnum.CurGun.ToString(), "Gun1");
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            startAni = false;
            foreach (GameObject game in games)
            {
             //   Logging.HYLDDebug.LogError(game);
                game.SetActive(false);
            }
            Guns[gunname].SetActive(false);
            foreach (Button btn in buttonList)
            {
                if (btn.name.Length>=6&&btn.name.Substring(3, 3) == "Gun")
                {
                    Color color = btn.transform.GetChild(0).GetComponent<Text>().color;
                    btn.transform.GetChild(0).GetComponent<Text>().color = new Color(color.r, color.g, color.b, 0.27f);
                    color = btn.transform.GetChild(1).GetComponent<Image>().color;
                    btn.transform.GetChild(1).GetComponent <Image>().color = new Color(color.r, color.g, color.b, 0f);
                    if(btn.transform.childCount==3)
                    btn.transform.GetChild(2).GetComponent<Image>().color = new Color(color.r, color.g, color.b, 0f);
                }
            }

        }
        private void ChangeGun(bool ISGame=false)
        {
            if (ISGame)
            {
                foreach (GameObject game in games)
                {
                    game.SetActive(true);
                    Color color = game.GetComponent<Image>().color;
                    float res = 2f * (timer - 1.3f);
                    if(res>=1) startAni = false;
                    game.GetComponent<Image>().color = new Color(color.r, color.g, color.b, Mathf.Clamp(res, 0, 1));
                    color = game.transform.GetChild(0).transform.GetComponent<Text>().color;
                    game.transform.GetChild(0).transform.GetComponent<Text>().color= new Color(color.r, color.g, color.b, Mathf.Clamp(res, 0, 1));

                    if (game.name.Substring(0, 5) == "Block")
                    {
                        color = game.GetComponent<Image>().color;
                        res = 1.6f * (timer - 1.3f);
                        game.GetComponent<Image>().color = new Color(color.r, color.g, color.b, Mathf.Clamp(res, 0, 0.80f));
                    }
                }
            }
            else
            {
                Guns[gunname].SetActive(true);
                Color color = Guns[gunname].transform.GetChild(0).GetComponent<Image>().color;
                float res = 2f * (timer - 0.8f);
                Guns[gunname].transform.GetChild(0).GetComponent<Image>().color = new Color(color.r, color.g, color.b, Mathf.Clamp(res, 0, 1));
               
                PlayerPrefs.SetString(PlayerPrefabsEnum.CurGun.ToString(), gunname);
            }

        }
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            if (!startAni) return;
            timer += deltaTime;
            if (timer > 1.3)
            {
                ChangeGun(true);
            }
            else if (timer > 0.8)
            {
                ChangeGun();
            }
            
        }
        string gunname;
        float timer = 0;
        bool startAni = false;
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            foreach (Button btn in buttonList)
            {
                if (btn.name.Substring(0,5)== "Block")
                {
                    btn.gameObject.SetActive(false);
                    if (PlayerPrefs.GetInt(PlayerPrefabsEnum.IsEasyFinish.ToString(), 0) == 0)
                    {
                        games.Add(btn.gameObject);

                    }
                }
                if (btn.name.Length>=6&& btn.name.Substring(3,3)=="Gun")
                {

                   // Logging.HYLDDebug.LogError(gunname);

                    btn.onClick.AddListener(() =>
                    {
                        startAni = true;
                        timer = 0;
                        gunname = btn.name.Substring(3);
                        foreach (GameObject game in games)
                        {
                            game.SetActive(false);
                        }
                       // Guns[TapTapStaticValue.CurGun.ToString()].SetActive(false);
                    });
                    continue;
                }
                if (btn.name.Length >= 7 && btn.name.Substring(0, 7) == "btnGame")
                {
                    btn.gameObject.SetActive(false);

                    games.Add(btn.gameObject);

                    /*
                    btn.onClick.AddListener(() =>
                    {

                        //  UIRoot.UIManger.StartGame(btn.name.Substring(3));
                    }); continue;
                    */
                }
            }
        }
    }
}