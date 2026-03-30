/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/8/18  
 * Description:  增加游戏结束UI显示

 * * * * * * * * * * * * * * * * 
*/
using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
public enum ModelName
{
    HYLDBaoShiZhengBa,
    HYLDJinKuGongFang
}

public class Toolbox : MonoBehaviour
{
    private int CountMax = 16;
    public  int Count = 16;
    public  int BlueGem = 0;
    public  int RedGem = 0;
    public  bool RedWin = true;
    public static bool ToCount=false;
    public GameObject CenterText;
    public GameObject LeftUpBlack;
    public GameObject RightUpBlack;
    public GameObject LeftUpBlack_Blue;
    public GameObject RightUpBlack_Red;
    public GameObject LeftUpBlack_Text;
    public GameObject RightUpBlack_Text;
    public GameObject 游戏结束;
    public static bool 是否游戏结束 = false;


    public float 金库攻防红方总血UI;
    public float 金库攻防蓝方总血UI;

    public GameObject 宝石争霸蓝方UI;
    public GameObject 宝石争霸红方UI;
    public GameObject 试玩模式返回键;
    public Text 模式开始台词;
    // Start is called before the first frame update

    //工具盒初始化，根据模式不同改变UI、循环判断胜负条件等
    #region 邓龙浩部分
    IEnumerator Start()
    {
        ReStart();
        是否游戏结束 = false;
        //金库攻防
        if (HYLDStaticValue.ModenName == "HYLDJinKuGongFang")
        {
            CenterText.GetComponent<Text>().text = "";
        }
        宝石争霸红方UI.SetActive(HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa");
        宝石争霸蓝方UI.SetActive(HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa");
        试玩模式返回键.SetActive(HYLDStaticValue.ModenName == "HYLDTryGame");
            //宝石争霸
        if (HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa")
        {
            ClearMostlyUI();
            while (HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa")
            {
                yield return ConfirmWhoWin();
            }
        }
        if(HYLDStaticValue.ModenName == "HYLDTryGame")
        {
            ClearMostlyUI();
            CenterText.GetComponent<Text>().text = "";
        }
        
    }

    //透明化大部分UI
    void ClearMostlyUI()
    {
        LeftUpBlack.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        RightUpBlack.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        LeftUpBlack_Blue.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        RightUpBlack_Red.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        LeftUpBlack_Text.GetComponent<Text>().text = "";
        RightUpBlack_Text.GetComponent<Text>().text = "";
        CenterText.GetComponent<Text>().text = "";
    }

    //宝石争霸专用，确认是否满足倒计时条件
    public IEnumerator ConfirmWhoWin()
    {
            if (ToCount)
            {
                
                if (Count == 0)
                {
                /*
                HYLDStaticValue.MatchingPlayerTotal = 0;

                HYLDStaticValue.RoomEnemyTeamGemTotalValue = HYLDStaticValue.RoomSelfTeamGemTotalValue = 0;
                foreach (var pos in HYLDStaticValue.Players)
                {
                    pos.gemTotal = 0;
                }
                */
                //SceneManager.LoadScene("HuangYeLuanDouStart");
                    if (Manger.BattleManger.Instance.IsGameOver) yield return null;
                    Manger.BattleManger.Instance.BeginGameOver();
                    
                }
            else if (BlueGem== RedGem)
                {
                    ToCount = false;
                }
                else if(BlueGem> RedGem&& RedWin==false)
                {
                    HYLDStaticValue.玩家输了吗 = false;
                    yield return new WaitForSeconds(1f);
                    Count--;
                    CenterText.GetComponent<Text>().text = Count.ToString();
                }
                else if(BlueGem < RedGem && RedWin == true)
                {
                    HYLDStaticValue.玩家输了吗 = true;
                    yield return new WaitForSeconds(1f);
                    Count--;
                    CenterText.GetComponent<Text>().text = Count.ToString();
                }
                else if (RedWin)
                {
                    RedWin = false;
                    Count = CountMax;
                }
                else if (!RedWin)
                {
                    RedWin = true;
                    Count = CountMax;
                }
            }
            else
            {
                CenterText.GetComponent<Text>().text = "";
            }
    }
    //宝石争霸专用，取消倒计时
    public void CancelCountDown()
    {
        ToCount = false;
    }
    //宝石争霸专用，开始倒计时
    public void StartCountDown()
    {
        if (!ToCount)
        {
            Count = CountMax;//TODO:后面改成16
            ToCount = true;
        }
    }

    //重新开始的初始化
    public void ReStart()
    {
        HYLDStaticValue.ConfirmWinOrNot = true;
        //宝石争霸初始化
        HYLDStaticValue.MatchingPlayerTotal = 0;
        HYLDStaticValue.RoomEnemyTeamGemTotalValue = HYLDStaticValue.RoomSelfTeamGemTotalValue = 0;
        foreach (var pos in HYLDStaticValue.Players)
        {
            pos.gemTotal = 0;
        }
        //金库攻防初始化
        HYLDStaticValue.RedBP = 30000;
        HYLDStaticValue.BlueBP =30000;
        //弹出游戏结束界面
        //SceneManager.LoadScene("HuangYeLuanDouStart");
        
    }

  
    public void ChangeCenternText(string _Text)
    {
        CenterText.GetComponent<Text>().text = _Text;
    }
    public void ChangeLeftUpText(string _Text)
    {
        LeftUpBlack_Text.GetComponent<Text>().text = _Text;
    }
    public void ChangeRightUpText(string _Text)
    {
        RightUpBlack_Text.GetComponent<Text>().text = _Text;
    }
    #endregion
    IEnumerator 游戏结束协程方法()
    {
        yield return new WaitForSeconds(2);
        游戏结束.GetComponent<HYLDGameOver>().GameOver();
    }
    public void 游戏结束方法()
    {
        if(HYLDStaticValue.玩家输了吗)
        CenterText.GetComponent<Text>().text = "失败";
        else CenterText.GetComponent<Text>().text = "胜利";
        是否游戏结束 = true;
        StartCoroutine(游戏结束协程方法());
    }
    private void Update()
    {
        if (Manger.BattleManger.Instance.IsGameOver) return;
        if (HYLDStaticValue.ConfirmWinOrNot)
        {

            HYLDStaticValue.ConfirmWinOrNot = false;
            //控制宝石争霸输赢结束
            if (HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa")
            {
                int totalRedTemp = 0, totalBlueTemp = 0;
                foreach (var pos in HYLDStaticValue.Players)
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

                HYLDStaticValue.RoomEnemyTeamGemTotalValue = totalRedTemp;
                HYLDStaticValue.RoomSelfTeamGemTotalValue = totalBlueTemp;

                if (HYLDStaticValue.RoomEnemyTeamGemTotalValue >= 10 || HYLDStaticValue.RoomSelfTeamGemTotalValue >= 10)
                {

                   BlueGem = totalBlueTemp;
                   RedGem = totalRedTemp;
                   StartCountDown();
                }
                else
                {
                    CancelCountDown();
                }
            }
            //控制金库攻防输赢结束
            else if (HYLDStaticValue.ModenName == "HYLDJinKuGongFang")
            {
                RightUpBlack.GetComponent<Slider>().value = (float)HYLDStaticValue.BlueBP / 30000;
                LeftUpBlack.GetComponent<Slider>().value = (float)HYLDStaticValue.RedBP / 30000;
                //ToolBox.GetComponent<Toolbox>().ChangeCenternText(BlueBP.ToString());
                ChangeLeftUpText(HYLDStaticValue.BlueBP.ToString());
                ChangeRightUpText(HYLDStaticValue.RedBP.ToString());
                if (HYLDStaticValue.RedBP <= 0 || HYLDStaticValue.BlueBP <= 0)

                {
                    if (HYLDStaticValue.RedBP <= 0) HYLDStaticValue.玩家输了吗 = false;
                    else HYLDStaticValue.玩家输了吗 = true;

                    Manger.BattleManger.Instance.BeginGameOver();
                }
            }
        }
     
    }
    private void Awake()
    {
        if(HYLDStaticValue.ModenName == "HYLDBaoShiZhengBa")
        {
            模式开始台词.text = "宝石争霸\n收集10颗宝石";
        }
        else if(HYLDStaticValue.ModenName == "HYLDJinKuGongFang")
        {
            模式开始台词.text = "金库攻防\n摧毁敌方金库！";
        }
        else if(HYLDStaticValue.ModenName == "HYLDTryGame")
        {
            模式开始台词.text = "试玩模式\n请尽情体验英雄！";
        }
    }
}
