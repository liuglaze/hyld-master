using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class RoomData
{
    public string roomId,p1,p2,p3;
    public void toDisplay()
    {
        D.p("ROOMID="+roomId+"p1="+p1+"p2="+p2+"p3="+p3);
    }
}

public class User : MonoBehaviour
{
    public static bool needUpdateInformation = false;//更新所有玩家的消息（玩家手上剩下的卡牌數量、谁是地主）


    public static bool openEndPanal = false;
    public void LeftRoom(string s)
    {
        
        //ID+1 表示ID胜利
        if (int.Parse(s[1].ToString()) == 1)
        {
            StaticValue.roomWinerId = int.Parse(s[0].ToString());
            
        }
        else//玩家退出导致游戏结束
        {
           
        }

        openEndPanal = true;
        print("LeftRoom="+s);
    }

    public static int isPlayerPushCard = 0;
    public void PlayerPushCard(string s)
    {
        
        int l = (s.Length-1)/2;
        //s[0]号玩家，出了某牌[1+]
        
        if (int.Parse(s[0].ToString()) == StaticValue.roomPlayerLeftId)//左侧的人出了 牌
        {
            
            StaticValue.lastCardLeft -= l;
            string temp = "";
            print("LLLLLLL="+l);
            for (int i = 0; i <  l; i++)
            {
                temp= s.Substring(1+i*2,2);
                //牌权0
                int weight = StaticMethod.str2Weight(temp[0]);
                int color = StaticMethod.str2Color(temp[1]);
                print("WWWWWWWWweight="+weight+"color="+color);
                StaticValue.LeftSendCards.Add(new CardUGUISprit(weight,color,temp[0].ToString())); //new Card(weight,color));
            }
            isPlayerPushCard = 1;
        }
        else if (int.Parse(s[0].ToString()) == StaticValue.roomPlayerRightId)//右侧的人出了牌
        {
            
            StaticValue.lastCardRight -= l;
            string temp = "";
            for (int i = 0; i <  l; i++)
            {
                temp= s.Substring(1+i*2,2);
                //牌权0
                int weight = StaticMethod.str2Weight(temp[0]);
                int color = StaticMethod.str2Color(temp[1]);
                StaticValue.RightSendCards.Add(new CardUGUISprit(weight,color,temp[0].ToString())); //new Card(weight,color));
            }
            isPlayerPushCard = 2;
        }
        else
        {
            StaticValue.lastCardSelf -= l;
        }
        isPlayerBehaviorShouldMainPrcessDo = true;//下一个人出牌
    }
    
    
    
    public static bool isPlayerBehaviorShouldMainPrcessDo = false;//下一个人转圈圈，然后下一个人逻辑选择


    public void PlayerBehavior(string s)
    {
        print("PlayerBehavior:"+s);
        int id = int.Parse(s[0].ToString());
        
        int behaviorId=int.Parse(s[1].ToString());
        string temp = StaticValue.behaviorString[behaviorId];
        //"2#1"
        if (id == StaticValue.roomPlayerRightId)
        {
            MessageController.sendStringMessage(temp, MessageTypes.Right);
        }
        else if (id== StaticValue.roomPlayerLeftId)
        {
            MessageController.sendStringMessage(temp, MessageTypes.Left);
        }
        else if (id== StaticValue.roomPlayerSelfId)
        {
            MessageController.sendStringMessage(temp, MessageTypes.Game);
        }


        if (behaviorId == 5)//地主已经诞生，兄弟们不要再傻逼一样抢地主了
        {
            print("1111111111111");
            StaticValue.roomLandlordId = id;
            //后面就可以开始出牌了伙计们！！
            StaticValue.roomNextBehaviorButtonType = 3;
            
            if (StaticValue.roomLandlordId == 1) StaticValue.NowState = 3;
            else StaticValue.NowState =StaticValue.roomLandlordId - 1;
            print("生成了地主，所以置于上一步");
            needUpdateInformation = true;
        }
        
        //1为叫 2不叫 3为抢 4为不抢 5 是地主
        //call 1 want 3
        //call 0 call 1 want 1  jump want 1
        //call 0 call 0 call 1
        //call 0 call 0 call 0  reload
        //第一位同志
        if( behaviorId == 1)
            StaticValue.roomNextBehaviorButtonType = 2;//那后面的小兄弟你就只能抢了
        
        isPlayerBehaviorShouldMainPrcessDo = true;
        //小兄弟你怎么就也不叫 那，抱歉就得重新发牌了  哦，服务器兄弟会搞定这个重发事情 那我不判定了哦
        //2020-03-05：写写删删，一个全栈程序员就应该先整体规划后再打，不是从前端写了感觉不对再写后端，最后发现原来服务器处理就可以了

    }
    
    
    public static bool isDealCardMain = false;
    
    public void DealCard(string s)
    {
        
        if (StaticValue.roomLandlordId == 0) //我获得了牌，
        {
            //MessageController.sendStringMessage("我的手牌："+s, MessageTypes.Game);
            StaticValue.selfHasCard += s;
        }

        if (StaticValue.roomLandlordId != 0) //地主兄弟获得了牌 告诉大家
        {
            MessageController.sendStringMessage("底牌:"+s[0]+s[2]+s[4], MessageTypes.Game02);

            //所有人的状态都将到地主前一个人 这样子下一个人就是地主出牌
            
        }

        if (StaticValue.roomLandlordId == StaticValue.roomPlayerSelfId) //没错 我就是那个屌地主
        {
            StaticValue.selfHasCard += s;
        }
        
        isDealCardMain = true;
        
        //StaticValue.selfHasCard = s;
    }
    
    
    
    
    public static bool isStartMacthingNormalShouldMainPrcessDo = false; 
    public static void StartMacthingNormalShouldMainPrcessDo()
    {
        string temp = "匹配成功！";// + StaticValue.selfPhoneNumber + "！";
        MessageController.sendStringMessage(temp, MessageTypes.Server);
        StartPanalUIController.Instance.showPannal("StartMatching");
        
        RoomUIController.Instance.showPannal("gamingPanal");
        
        GameObject.Find("PeopleSelf").GetComponentInChildren<Text>().text=StaticValue.selfPhoneNumber;
        GameObject.Find("PeopleLeft").GetComponentInChildren<Text>().text=StaticValue.roomPlayerLeft;
        GameObject.Find("PeopleRight").GetComponentInChildren<Text>().text=StaticValue.roomPlayerRight;
        GameObject.Find("RoomID").GetComponentInChildren<Text>().text="房间ID："+StaticValue.roomId;

        //第一个人作为地主开始
        PlayerController.Instance.show(1,1);
        
    }
    public void StartMacthingNormal(string s)
    {
        //print("cgcgcgcg);
        
        RoomData r1=JsonUtility.FromJson<RoomData>(s);
        r1.toDisplay();
        StaticValue.roomId = r1.roomId;
        //StaticValue.roomPlayer1 = r1.p1;
        //StaticValue.roomPlayer2 = r1.p2;
        //StaticValue.roomPlayer3 = r1.p3;

        
        //判断哪个是自己 出牌顺序由逆时针 喊地主由p1为第一位
        if (r1.p1 == StaticValue.selfPhoneNumber)
        {
            StaticValue.roomPlayerSelfId = 1;
            StaticValue.roomPlayerRightId = 2;
            StaticValue.roomPlayerLeftId = 3;
            
            StaticValue.roomPlayerLeft = r1.p3;
            StaticValue.roomPlayerRight = r1.p2;
        }else if (r1.p2 == StaticValue.selfPhoneNumber)
        {
            StaticValue.roomPlayerSelfId = 2;
            StaticValue.roomPlayerRightId = 3;
            StaticValue.roomPlayerLeftId = 1;

            StaticValue.roomPlayerLeft = r1.p1;
            StaticValue.roomPlayerRight = r1.p3;
        }else
        {
            StaticValue.roomPlayerSelfId = 3;
            StaticValue.roomPlayerRightId = 1;
            StaticValue.roomPlayerLeftId = 2;

            StaticValue.roomPlayerLeft = r1.p2;
            StaticValue.roomPlayerRight = r1.p1;
        }
        
        //StaticValue.roomPlayerLeftId = (StaticValue.roomPlayerSelfId+1)%3;
        //StaticValue.roomPlayerRightId = (StaticValue.roomPlayerSelfId-1)%3+1;
        
        
        isStartMacthingNormalShouldMainPrcessDo = true;
        
        
        
        
    }

   

    public static bool isVfCodeShouldMainPrcessDo = false; 
    public static void VfCodeShouldMainPrcessDo()
    {
        string temp = "游戏登录成功！";// + StaticValue.selfPhoneNumber + "！";
        StaticValue.isLogin = true;
        MessageController.sendStringMessage(temp, MessageTypes.Server);
        StaticMethod.setPhoneNumber();
        LoginUseSMS.Instance.closePannal();
        
    }
    public void SendSMS(string s)
    {
        if (s == "1")
        {
            string temp = "短信已成功发出，注意查收";
            MessageController.sendStringMessage(temp, MessageTypes.Server);
        }
        else if(s=="2")
        {
            string temp = "刚刚已发送过了，依然有效";
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
        else if(s=="3")
        {
            string temp = "服务器正在维护中，发送失败";
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
        else
        {
            string temp = "服务器正在维护中，发送失败";
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
        
        //print("!!!!!!!year");
    }
    public void VfCode(string s)
    {
        if (s == "1")
        { 
            isVfCodeShouldMainPrcessDo = true;
            
        }
        else if (s == "2")
        {
            string temp = "检测恶意验证，您已被屏蔽";
            TCPSocket.Instance.close();
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
        else if (s == "3")
        {
            string temp = "恭喜您，注册成功！";
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
        else
        {
            string temp = "验证码错误，请重新输入！";
            MessageController.sendStringMessage(temp, MessageTypes.Error);
        }
    }
    public void Logins(string s)
    {
        if (s == "1")
        {
            isVfCodeShouldMainPrcessDo = true;
            
        }
        else if(s=="0")
        {
            MessageController.sendStringMessage("IP不匹配，请重新验证", MessageTypes.Error);
        }
    }
}
