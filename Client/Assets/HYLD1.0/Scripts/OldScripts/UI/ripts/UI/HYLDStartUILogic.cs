/*
 ****************
 * Author:        赵元恺
 * CreatTime:  2020/7/28
 * Description:游戏开始界面的UI逻辑 
 *             选英雄选模式试玩界面
 ****************
*/
using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine.UI;
using UnityEngine;
//using UnityEngine.Experimental.UIElements;
using UnityEngine.UIElements;
using UnityEngine.Serialization;
using Button = UnityEngine.UI.Button;
using Image = UnityEngine.UI.Image;
using Random = UnityEngine.Random;
public class HYLDStartUILogic : MonoBehaviour
{
    public GameObject[] panals;

    public Text PlayerNameText;
    public Image CreatNameSure;
    public Text HallPlayerNameText;
    public Text MatchingText;

    public GameObject ChangeGameType;
    public GameObject HeroXianShi;
    public Sprite[] HeroSprites;
    public Sprite[] 模式Sprites;
    public GameObject 限制联机模式打开按钮;
    // Start is called before the first frame update
    public void Start()
    {
        LoadAsyncScene.isBackStart = false;
        HYLDStaticValue.Players.Clear();
        HYLDStaticValue.Heros.Clear();
        SeclectHero(PlayerPrefs.GetString(PlayerPrefabConstValue.HYLDPlayerHero, HeroName.XueLi.ToString()));
        SeclectHero(HYLDStaticValue.myheroName);
        string modelName = PlayerPrefs.GetString(PlayerPrefabConstValue.ModenName, "");
        if (modelName == "")
        {
            PlayerPrefs.SetString(PlayerPrefabConstValue.ModenName, 模式Sprites[0].name);
            ChangeGameType.GetComponent<Image>().sprite = 模式Sprites[0];
        }
        else
        {
            foreach (Sprite 模式 in 模式Sprites)
            {
                if (modelName == 模式.name)
                {
                    ChangeGameType.GetComponent<Image>().sprite = 模式;
                    break;
                }
            }
        }
    }

    // Update is called once per frame
    private bool isDo = false;
    public void Excute()
    {
        //限制联机模式打开按钮.SetActive(!HYLDStaticValue.是否为连接状态);
        if (HYLDStaticValue.是否为连接状态&&HYLDStaticValue.PlayerName==""&&isDo==false)
        {
            openPanal("CreateName");
            if (PlayerNameText.text.Length >1)
            {
                CreatNameSure.color=new Color(1,1,0,1);
            }
            else
            {
                CreatNameSure.color=new Color(1,1,1,1);
            }
        }
        else if (isDo==false)
        {
            isDo = true;
            openPanal("Hall");
            //HYLDStaticValue.PlayerName = PlayerPrefs.GetString(PlayerPrefabConstValue.HYLDPlayerName,"");
            HallPlayerNameText.text = HYLDStaticValue.PlayerName;
            gameObject.GetComponent<LongZhiJie.StartUIManger>().StartInit();
        }
        //print(MatchingText.text.Length+"?"+MatchingText.text);

        MatchingText.text = "已找到玩家 "+HYLDStaticValue.MatchingPlayerTotal.ToString()+"/6";

        if (HYLDStaticValue.MatchingPlayerTotal == 6)
        {
            // SceneManager.LoadScene(HYLDStaticValue.ModenName);
            SceneManager.LoadScene("HYLDAsyncScence");
        }
        

    }


    void openPanal(string name,bool isNeedCloseOther = true)
    {
        foreach(var pos in panals)
        {
            if (pos.name == name)
            {
                pos.SetActive(true);
            }
            else if(isNeedCloseOther)
                pos.SetActive(false);
        }
    }
    public void 开启单机游戏()
    {
        TCPSocket.是否单机 = true;
        TCPSocket.被选择的英雄.Clear();
        HYLDStaticValue.playerSelfIDInServer = 0;
        HYLDStaticValue.myheroName = (HYLDStaticValue._myheroName).ToString();
        TCPSocket.被选择的英雄.Add(HYLDStaticValue.myheroName);
        for(int i=1;i<6;i++)
        TCPSocket.被选择的英雄.Add(Enum.GetName(typeof(HeroName), Random.Range(0, 18)));
        TCPSocket.玩家名.Clear();
        TCPSocket.玩家名.Add(HYLDStaticValue.PlayerName);
        for (int i = 1; i < 6; i++)
            TCPSocket.玩家名.Add("A");
        SceneManager.LoadScene("HYLDAsyncScence");
    }
    public  void maching()
    {
        openPanal("Matching");
        HYLDStaticValue.MatchingPlayerTotal = 0;
        //TCPSocket.是否单机 = false;
        //TCPSocket.Instance.Send(OldRequestCode.HYLDGame, OldActionCode.StartBSZBMacthing, HYLDStaticValue.PlayerName+"*"+HYLDStaticValue.myheroName);//+"*"+HYLDStaticValue.ModenName
    }
    public  void AddAIToMaching()
    {
        // openPanal("Matching");
        TCPSocket.Instance.Send(OldRequestCode.HYLDGame, OldActionCode.StartBSZBMacthing, "A" + "*" + Enum.GetName(typeof(HeroName),Random.Range(0,18)));// + "*" + HYLDStaticValue.ModenName
    }
    public void GameTypeSelect(Sprite sprite)
    {
        PlayerPrefs.SetString(PlayerPrefabConstValue.ModenName, sprite.name);
        HYLDStaticValue.ModenName = sprite.name;
        ChangeGameType.GetComponent<Image>().sprite = sprite;
        openPanal("Hall");
    }
    public void GameType()
    {
        openPanal("GameType");
    }
    public void ChickHero(string heroname)
    {
        openPanal(heroname);
    }
    public void Hall()
    {
        openPanal("Hall");
    }
    public void HeroSelct()
    {
        openPanal("HeroSelct");
    }
    public void SeclectHero(string name)
    {
        //更改当前静态选择英雄
        HYLDStaticValue.myheroName = name;

        foreach (HeroName hero in Enum.GetValues(typeof(HeroName)))
        {
           if(name==hero.ToString())
            {
                HYLDStaticValue._myheroName = hero;
                break;
            }
        }
        //菜单图片改为对应英雄
        foreach (var sp in HeroSprites)
        {
            if (sp.name == name)
            {
                HeroXianShi.GetComponent<Image>().sprite = sp;
                break;
            }
        }
        PlayerPrefs.SetString(PlayerPrefabConstValue.HYLDPlayerHero, name);
        MVC.UIStartMainPanel panel1 = (MVC.UIStartMainPanel)HYLDManger.Instance.UIBaseManger.GetPanel(nameof(MVC.UIStartMainPanel));
        panel1.ChangeHero();
        //Logging.HYLDDebug.LogError(2);
        openPanal("Hall");
    }
    public void TryGame(string name)
    {
        TCPSocket.是否单机 = true;
        HYLDStaticValue.myheroName = name;
        HYLDStaticValue.ModenName = "HYLDTryGame";
        
        foreach (HeroName hero in Enum.GetValues(typeof(HeroName)))
        {
            if (name == hero.ToString())
            {
                HYLDStaticValue._myheroName = hero;
                break;
            }
        }
        HYLDStaticValue.playerSelfIDInServer = 0;
        //SceneManager.LoadScene("HYLDAsyncScence");
        ZYKGameLoop.instance.SetScenseStateController(new TryGameState(ZYKGameLoop.instance.ZYKScenseStateController));
    }
}
