using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class StaticValue:MonoBehaviour 
{
    public static void initGaming()//初始化所有的局内成员变量
    {
        roomId = "";
        roomPlayerLeft = "";
        roomPlayerRight = "";
        roomPlayerSelfId = 1;
        roomPlayerLeftId = 2;
        roomPlayerRightId = 3;
        roomLandlordId = 0;
        SelfCardHaveFlag = false;
        roomBaseValue = 5;
        roomMultipleValue = 175;
        lastCardLeft = 17;
        lastCardRight = 17;
        lastCardSelf = 17;
        roomWinerId = 0;
        NowState=1;
        Multiples = 50;
        foreach (var pos in selfHandCards)
        {
            GameObject.Destroy(pos.CardBody,2);
        }

        foreach (var pos in selfReadSend)
        {
            GameObject.Destroy(pos.CardBody,2);
        }
        selfHandCards=new List<CardUGUISprit>(1);
        selfReadSend=new List<CardUGUISprit>(1);
        
        LeftSendCards=new List<CardUGUISprit>(1);
        RightSendCards=new List<CardUGUISprit>(1);
        roomNextBehaviorButtonType = 1;
        selfHasCard = "";
    }

    public static string selfPhoneNumber="";
    public static bool isLogin = false;
    
    public static string roomId = "";
    public static string roomPlayerLeft = "";
    public static string roomPlayerRight = "";
    public static int roomPlayerSelfId = 1;
    public static int roomPlayerLeftId = 2;
    public static int roomPlayerRightId = 3;
    public static int roomLandlordId = 0;
    public static bool SelfCardHaveFlag = false;
    public static int roomBaseValue = 5;
    public static int roomMultipleValue = 175;
    /// 剩余手牌数量
    public static int lastCardLeft = 17;
    public static int lastCardRight = 17;
    public static int lastCardSelf = 17;

    public static int roomWinerId = 0;
    
    public static int NowState=1;
    public static int Multiples = 50;
    public static List<CardUGUISprit> selfHandCards=new List<CardUGUISprit>(1);
    public static List<CardUGUISprit> selfReadSend=new List<CardUGUISprit>(1);

    public static List<CardUGUISprit> LeftSendCards=new List<CardUGUISprit>(1);
    public static List<CardUGUISprit> RightSendCards=new List<CardUGUISprit>(1);


   // public static List<GameObject> selfHandCardGameobjs=new List<GameObject>(1);

    public static int roomNextBehaviorButtonType = 1;

    public static string selfHasCard = "";
    
    public static string[] behaviorString = {"过","叫地主","不叫","抢地主","不抢","地主已选出"};
    
}

public class StaticMethod
{
    public static CardUGUISprit FindCardInLists(List<CardUGUISprit> lists,Weight weight0,Suits color0) //删除temp元素
    {
        foreach (CardUGUISprit pos in lists)
        {
            if (pos.weight == weight0 && pos.color == color0)
            {
                return pos;
            }
        }
        return null;
    }
    
    public static List<CardUGUISprit> SortCards0(List<CardUGUISprit> Temp)
    {
        if (Temp.Count == 0)
        {
            return Temp;
        }
        
        
        Temp.Sort(delegate(CardUGUISprit x, CardUGUISprit y)
        {
            if (x.weight != y.weight)
                return y.weight.CompareTo(x.weight);
            else
                return y.color.CompareTo(x.color);
        });
        return Temp;
    }
    public static int str2Weight(char c)
    {
        int v = 0;
        if (c >= '3' && c <= '9')
            v = c - '3';
        else if (c == 'T')
            v = 7;
        else if (c == 'J')
            v = 8;
        else if (c == 'Q')
            v = 9;
        else if (c == 'K')
            v = 10;
        else if (c == 'A')
            v = 11;
        else if (c == '2')
            v = 12;
        else if (c == 'S')
            v = 13;
        else if (c == 'G')
            v = 14;
        return v;
    }

    public static int str2Color(char c)
    {
        return c-'a';
    }


    
    public static void setPhoneNumber()
    {
        PlayerPrefs.SetString("LoginUserPhoneNumber",StaticValue.selfPhoneNumber);
        PlayerPrefs.SetString("LoginUserPhoneNumberLastTime",DateTime.Now.ToString().Substring(0,14));
        
    }

    public static string getStringCard(string temp)
    {
        string card = temp;
        int l = temp.Length;
        string name="";
        if (l == 1)
        {
            //3456789TJQKA2SG 
            if (card[0] == 'T') name = "十";
            else if (card[0] == 'J') name = "勾";
            else if (card[0] == 'Q') name = "圈";
            else if (card[0] == 'A') name = "尖";
            else if (card[0] == 'S') name = "小王";
            else if (card[0] == 'G') name = "大王";
            else name += card[0];
        }
        else if (l == 2)
        {
            if (card[0] == 'T') name = "十";
            else if (card[0] == 'J') name = "勾";
            else if (card[0] == 'Q') name = "圈";
            else if (card[0] == 'A') name = "尖";
            else name += card[0];
            name = '对' + name;
        }
        else if (l == 3)
        {
            if (card[0] == 'T') name = "十";
            else if (card[0] == 'J') name = "勾";
            else if (card[0] == 'Q') name = "圈";
            else if (card[0] == 'A') name = "尖";
            else name += card[0];
            name = "三个" + name;
        }
        else
        {
            name = "大你！";
        }
        
        
        
        
        
        
        
        return name;
    }
}





public class D : MonoBehaviour
{
    static bool isNeedPrint = true;
    public static void p(object o)
    {
        if(isNeedPrint)
        print(o);
    }
}