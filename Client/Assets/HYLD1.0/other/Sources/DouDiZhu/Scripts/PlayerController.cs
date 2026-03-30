using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
public class PlayerController : MonoBehaviour
{
    
    public GameObject PlayerSelf;
    public GameObject PlayerLeft;
    public GameObject PlayerRight;
    // Start is called before the first frame update
    public GameObject[] wait;
    public GameObject[] buttonType;

    public GameObject startPanal;
    public Text nowTimeText;
    public Text lastCardRight;
    public Text lastCardLeft;
    public Text lastCardSelf;

    public Image landloadLeft;
    public Image landloadRight;
    public Image landloadSelf;

    
    
    private static PlayerController _instance;
    GameObject Card ;
    public static PlayerController Instance { get {
        if (_instance == null)
        {
            _instance = GameObject.Find("UI/UIController/gamePanal/gamingPanal").GetComponent<PlayerController>();
        }
        return _instance;
    } }

    private void Awake()
    {
        
    }
    private void Start()
    {
        initGaming();
        Card = GameObject.Find("Card");
        
        //每局的开始要初始化所有的单局信息

    }

 

    private void initGaming()
    {
        updateLastCardCnt();
        showLandlord(0);
        //上一把打完的牌的实例化对象全部清空
        
        
        
    }
    
    

    private  int  Next()
    {
        
        if (StaticValue.NowState == 3) StaticValue.NowState= 1;
        else
        {
            StaticValue.NowState = StaticValue.NowState + 1;
        }

        return StaticValue.NowState;
    }

    public void clickLeftRoom()
    {
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.LeftRoom, "0");
        gameObject.SetActive(false);
        startPanal.SetActive(true);
    }
    public void clickPush()
    {
        int l = StaticValue.selfReadSend.Count;
        print("l="+l);
        if (l!= 0)//匹配出牌的是否符合规则
        {
            //将要出的牌进行排序
            //StaticValue.selfReadSend=StaticMethod.SortCards(StaticValue.selfReadSend);
            
            
            string tempsendStringMessage="";
            string tempTCPSocketSend="";

            //print("添加自己出的牌");
            for (int i = 0; i < l; i++)
            {
                tempsendStringMessage += StaticValue.selfReadSend[i].value;
                tempTCPSocketSend += StaticValue.selfReadSend[i].value;
                char a =(char)('a' + (int)StaticValue.selfReadSend[i].color);
                
                tempTCPSocketSend += a;

            }
            
            TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerPushCard, tempTCPSocketSend);
            MessageController.sendStringMessage(StaticMethod.getStringCard(tempsendStringMessage), MessageTypes.Game,0.1f);

            //将StaticValue.selfHandCards打出的牌去除
            for (int i = 0; i < l; i++)
            {
                List<CardUGUISprit> temp = new List<CardUGUISprit>(StaticValue.selfHandCards);
                int shcl = temp.Count;
                StaticValue.selfHandCards=new List<CardUGUISprit>();//.Clear();
                //print("ii=+"+i+"len="+shcl);
                for (int j=0 ;j<shcl;j++)
                {
                    //print("i="+i+"j="+j);
                    if (temp[j].color!=StaticValue.selfReadSend[i].color||temp[j].value!=StaticValue.selfReadSend[i].value)
                    {
                        //print("oki="+i+"j="+j);
                        StaticValue.selfHandCards.Add(temp[j]);
                    }
                    else
                    {
                        Destroy(temp[j].CardBody);//把原有序列中的牌在前段进行删除
                    }
                }
            }
            
            //print("StaticValue.selfHandCards len="+StaticValue.selfHandCards.Count);
            //手牌少了之后进行调整
            StaticValue.selfHandCards=DisplaySelfHandCards(StaticValue.selfHandCards,0);
            
            //打出的牌 排序和显示
            StaticValue.selfReadSend=DisplaySelfHandCards(StaticValue.selfReadSend,3);
            
            //手牌出完了之后对队列进行清空
            StaticValue.selfReadSend.Clear();
            
            clickFinish();
        }
    }
    public void clicknoPush()
    {
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerPushCard, "0");
        //MessageController.sendStringMessage("不要", MessageTypes.Game);openButton("null");
        
    }

    /// <summary>
    /// PlayerBehavior
    /// </summary>

    void clickFinish()
    {
        openButton("null");
        //waitTimeText.text = "-1";
    }
    public void clickCall()
    {
        
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerBehavior, "1");
        //MessageController.sendStringMessage("叫地主", MessageTypes.Game);
        clickFinish();

    }
    public void clicknoCall()
    {
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerBehavior, "2");
        //MessageController.sendStringMessage("不叫", MessageTypes.Game);
        clickFinish();
        
    }
    public void clickWant()
    {
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerBehavior, "3");
        //MessageController.sendStringMessage("抢地主", MessageTypes.Game);
        clickFinish();
    }
    public void clicknoWant()
    {
        TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerBehavior, "4");
        //MessageController.sendStringMessage("不抢", MessageTypes.Game);
        clickFinish();
    }



    private Text waitTimeText;
    private DateTime waitTimeStart;

    void doCountdown(GameObject g)
    {
        waitTimeText=g.GetComponentInChildren<Text>();
        waitTimeText.text = "15";
        waitTimeStart=DateTime.Now;
    }
    
    void openWait(string name)
    {
        //print("openWait");
        foreach (GameObject pos in  wait)
        {
            if (pos.name == name)
            {
                pos.SetActive(true);
                doCountdown(pos);
            }
            else 
                pos.SetActive(false);
        }
    }

    void openButton(string name)
    {
        foreach (GameObject pos in  buttonType)
        {
            if(pos.name==name)
                pos.SetActive(true);
            else 
                pos.SetActive(false);
        }
    }
    public void show(int x,int type)
    {
        //print("x="+x+" type="+type+" selfroomPlayerSelfId="+StaticValue.roomPlayerSelfId);
        if ( StaticValue.roomPlayerSelfId==x)
        {
            openButton("ButtonType"+type);
            ///到自己这里啦，把readSend的牌就可以全部关掉删掉啦
            //int l = StaticValue.selfReadSend.Count;
            //for(int i=l-1;i>=0;i-- )
            {
                //StaticValue.selfReadSend[i].CardBody.SetActive(false);
                //StaticValue.selfReadSend.Remove(StaticValue.selfReadSend[i]);
            }
            //StaticValue.selfReadSend.Clear();
        }
        else
        {
            openButton("null");
        }
        
        if (StaticValue.roomPlayerLeftId == x)
        {
            openWait("waitLeft");
        }
        else if (StaticValue.roomPlayerRightId == x)
        {
            openWait("waitRight");
        }
        else if (StaticValue.roomPlayerSelfId == x)
        {
            openWait("waitSelf");
        }
    }

    void creatCard()
    {
        
    }

    public List<CardUGUISprit> DisplaySelfHandCards(List<CardUGUISprit> temp,int pos=0,float deleteTime=5f)
    {
        print("显示卡牌() 数量="+temp.Count+" pos="+pos);
        temp=StaticMethod.SortCards0(temp);
        //职责，作为调整和显示这个牌的所在位置位置
        int l = temp.Count;
        if (pos == 0)
        {
            StaticValue.SelfCardHaveFlag = !StaticValue.SelfCardHaveFlag;
        }
        
        
        for (int i = 0; i < l; i++)
        {
            GameObject clon=Card;
            if (temp[i].CardBody == null)
            {
                print("^first DisplaySelfHandCards");
                clon = Instantiate(Card);
                clon.GetComponent<CardUGUISprit>().color = temp[i].color;
                clon.GetComponent<CardUGUISprit>().value = temp[i].value;
                clon.GetComponent<CardUGUISprit>().isCanChoose = false;
                clon.name = temp[i].color+temp[i].value  ;
                temp[i].CardBody = clon;
            }
            if (pos == 0)
            {
                temp[i].CardBody.transform.SetParent(GameObject.Find("SelfCardHave"+(StaticValue.SelfCardHaveFlag==false?"A":"B")).transform);
                temp[i].CardBody.transform.localScale=new Vector3(1,1,1);
                if (StaticValue.roomLandlordId != 0) //地主已经选出了
                {
                    temp[i].CardBody.GetComponent<CardUGUISprit>().isCanChoose = true;
                }
                temp[i].CardBody.GetComponent<RectTransform>().anchoredPosition=new Vector2(-(l/2)*80+i*80,-290);
            }
            if (pos == 1)
            {
                temp[i].CardBody.transform.SetParent(GameObject.Find("LeftCardHave").transform);
                temp[i].CardBody.transform.localScale=new Vector3(0.5f,0.5f,1);
                temp[i].CardBody.GetComponent<RectTransform>().anchoredPosition=new Vector2(220,-(l/2)*80-i*40);
                temp[i].CardBody.GetComponent<CardUGUISprit>().isCanChoose = false;
                Destroy(temp[i].CardBody,deleteTime);
                
            }
            if (pos == 2)
            {
                temp[i].CardBody.transform.SetParent(GameObject.Find("RightCardHave").transform);
                temp[i].CardBody.transform.localScale=new Vector3(0.5f,0.5f,1);
                temp[i].CardBody.GetComponent<RectTransform>().anchoredPosition=new Vector2(0,-(l/2)*80-i*40);
                temp[i].CardBody.GetComponent<CardUGUISprit>().isCanChoose = false;
                Destroy(temp[i].CardBody,deleteTime);
                
            }
            if (pos == 3)//自己的牌打出需要进行删除和显示
            {
                temp[i].CardBody.transform.SetParent(GameObject.Find("SelfSendCard").transform);
                temp[i].CardBody.transform.localScale=new Vector3(0.5f,0.5f,1);
                temp[i].CardBody.GetComponent<RectTransform>().anchoredPosition=new Vector2(-(l/2)*80+i*80,75);
                temp[i].CardBody.GetComponent<CardUGUISprit>().isCanChoose = false;
                Destroy(temp[i].CardBody,deleteTime);
                
            }
        }

        if (pos == 1 || pos == 2 || pos == 3) //如果是显示打出的牌，直接进行清空处理
        {
            temp.Clear();
        }
            
        return temp;
    }

    void showLandlord(int landlordId)
    {
        landloadSelf.enabled = false;
        landloadLeft.enabled = false;
        landloadRight.enabled = false;
        if (StaticValue.roomPlayerRightId==landlordId )//右侧玩家是地主
        {
            landloadRight.enabled = true;
            StaticValue.lastCardRight = 20;
        }
        else if (StaticValue.roomPlayerLeftId ==landlordId)
        {
            landloadLeft.enabled = true;
            StaticValue.lastCardLeft = 20;
        }
        else if (StaticValue.roomPlayerSelfId == landlordId)
        {
            landloadSelf.enabled = true;
            StaticValue.lastCardSelf = 20;
        }

    }

    void updateLastCardCnt()
    {
        lastCardRight.text=StaticValue.lastCardRight.ToString();
        lastCardLeft.text=StaticValue.lastCardLeft.ToString();
        lastCardSelf.text=StaticValue.lastCardSelf.ToString();
    }

    void endTemp()//勝利音樂，勝利動畫
    {
        RoomUIController.Instance.showPannal("endPanal");
    }
    void Update()
    {
        if (User.openEndPanal)
        {
            User.openEndPanal = false;
            MessageController.sendStringMessage("牌出完啦，回合结束", MessageTypes.Server,1.5f);

            Invoke("endTemp",3.0f);
        }
       
        
        
        
        if (User.needUpdateInformation)
        {
            showLandlord(StaticValue.roomLandlordId);
            updateLastCardCnt();
            User.needUpdateInformation = false;
        }
        

        
        if (User.isPlayerPushCard!=0)//左右两位兄弟出牌
        {
            
            if (User.isPlayerPushCard == 1) //左兄弟出牌
            {
                StaticValue.LeftSendCards=DisplaySelfHandCards(StaticValue.LeftSendCards,1);
            }
            else if(User.isPlayerPushCard == 2)//y兄弟出牌
            {
                StaticValue.RightSendCards=DisplaySelfHandCards(StaticValue.RightSendCards,2);
            }
            User.isPlayerPushCard = 0;
        }
        
        
        if (User.isDealCardMain)//在这里进行实例化
        {
            User.isDealCardMain = false;
            int l = StaticValue.selfHasCard.Length / 2;
            string temp = "";
            
            for (int i = 0; i <  l; i++)
            {
                temp= StaticValue.selfHasCard.Substring(i*2,2);
                
                //牌权0
                int weight = StaticMethod.str2Weight(temp[0]);
                //print("$weight+="+weight);
                int color = StaticMethod.str2Color(temp[1]);
                StaticValue.selfHandCards.Add(new CardUGUISprit(weight,color,temp[0].ToString())); //new Card(weight,color));
            }
            StaticValue.selfHandCards=DisplaySelfHandCards(StaticValue.selfHandCards,0);
            StaticValue.selfHasCard = "";
        }
        
        if (waitTimeText != null)
        {
            TimeSpan temp= DateTime.Now-waitTimeStart;
            waitTimeText.text = (15 - temp.Seconds).ToString();
            if (User.isPlayerBehaviorShouldMainPrcessDo == true)//服务器告知这个玩家行为
            {
                updateLastCardCnt();
                
                User.isPlayerBehaviorShouldMainPrcessDo = false;
                print("Wow!!!");
                
                show(Next(),StaticValue.roomNextBehaviorButtonType);
            }
            if (int.Parse(waitTimeText.text)<=0)//倒计时结束 就是过
            {
                if (StaticValue.roomPlayerSelfId == StaticValue.NowState)
                {
                    waitTimeText.text = "15";
                    waitTimeStart=DateTime.Now;
                    TCPSocket.Instance.Send(OldRequestCode.Room, OldActionCode.PlayerBehavior, "0");

                }
                
            }
            
            

        }
        
        
        
        nowTimeText.text=DateTime.Now.ToShortTimeString(); 
    }

    
}
