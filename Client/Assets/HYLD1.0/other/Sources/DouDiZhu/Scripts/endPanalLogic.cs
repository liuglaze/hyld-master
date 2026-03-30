using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class endPanalLogic : MonoBehaviour
{
    public GameObject winGameObject;
    public GameObject loseGameObject;

    // Start is called before the first frame update
    void Start()
    {
        ///////////////
        //StaticValue.roomWinerId = 2;
    }

    public void clickExitButton()
    {
        print("退出房间");
        RoomUIController.Instance.showPannal("startPanal");
    }
    void showGameEndUI(int winerId)
    {
        
        if (winerId == StaticValue.roomPlayerSelfId &&
            StaticValue.roomLandlordId == StaticValue.roomPlayerSelfId)//地主胜利自己是地主  
        {
            showInformationLogic(true,true);
            print("地主胜利自己是地主  ");
        }
        else if(winerId != StaticValue.roomLandlordId&&
                StaticValue.roomLandlordId != StaticValue.roomPlayerSelfId)////地主没有胜利自己不是地主  
        {
            //自己是属于农民阵容胜利 获得单倍收益
            showInformationLogic(false,true);
            print("农民胜利自己是农民  ");
                    
        }
        else //失败了
        {
            if (StaticValue.roomPlayerSelfId != StaticValue.roomLandlordId) //自己是农民输了
            {
                showInformationLogic(false,false);
                print("自己是农民输了 ");
                //农民阵容胜利 抠出单倍收益
            }
            else if (StaticValue.roomPlayerSelfId == StaticValue.roomLandlordId)//自己是地主输了
            {
                showInformationLogic(true,false);
                print("自己是地主输了 ");
                //抠出两倍收益
            }
        }
    }

    void changeInformation(string informationName,bool islandload,string name,int roomBaseValue,int roomMultipleValue,int goldValue)
    {
        EndInformationShow endInformationShow = GameObject.Find(informationName).GetComponent<EndInformationShow>();
        endInformationShow.name = name;
        endInformationShow.baseValue = roomBaseValue;
        endInformationShow.multipleValue =roomMultipleValue;
        endInformationShow.goldValue =goldValue;
        if (islandload)//是地主
        {
            endInformationShow.islandLoad = true;
            
        }
        else
        {
            endInformationShow.islandLoad = false;
        }
        endInformationShow.needUpdate = true;
    }
    void showInformationLogic(bool islandload,bool iswin)
    {
        int selfGoldValue=0;
        int GoldValue = (StaticValue.roomBaseValue * StaticValue.roomMultipleValue);//赢了的钱
        selfGoldValue = GoldValue;
        if (iswin)//我赢了
        {
            winGameObject.SetActive(true);
            loseGameObject.SetActive(false);
            
        }
        else
        {
            winGameObject.SetActive(false);
            loseGameObject.SetActive(true);
            selfGoldValue = -selfGoldValue;
        }
        
        
        changeInformation("EndInformationSelf",islandload,StaticValue.selfPhoneNumber,StaticValue.roomBaseValue, StaticValue.roomMultipleValue,
            islandload==true?(selfGoldValue*2):selfGoldValue);
        
        if (StaticValue.roomLandlordId == StaticValue.roomPlayerLeftId) //左侧玩家是地主
        {
            changeInformation("EndInformationLeft",true,StaticValue.roomPlayerLeft,StaticValue.roomBaseValue, StaticValue.roomMultipleValue,
                iswin==true?(-GoldValue*2):(GoldValue*2));
        }
        else//左侧玩家是农民
        {
            changeInformation("EndInformationLeft",false,StaticValue.roomPlayerLeft,StaticValue.roomBaseValue, StaticValue.roomMultipleValue,
                (iswin&&islandload||islandload==false&&iswin==false)?(-GoldValue):(GoldValue));
            /// 我是地主，我赢了，那么他扣钱
            /// 我是农民，我赢了，那么他加钱
            /// 我是农民，我输了，那么他扣钱
            /// 我是地主，我输了，那么他加钱
            
            
        }

        if (StaticValue.roomLandlordId == StaticValue.roomPlayerRightId) //右侧玩家是地主
        {
            changeInformation("EndInformationRight",true,StaticValue.roomPlayerRight,StaticValue.roomBaseValue, StaticValue.roomMultipleValue,
                iswin==true?(-GoldValue*2):(GoldValue*2));
        }
        else
        {
            changeInformation("EndInformationRight",false,StaticValue.roomPlayerRight,StaticValue.roomBaseValue, StaticValue.roomMultipleValue,
                (iswin&&islandload||islandload==false&&iswin==false)?(-GoldValue):(GoldValue));

        }
    }

    // Update is called once per frame
    void Update()
    {
        if (StaticValue.roomWinerId != 0)//出现了胜利玩家
        {
            showGameEndUI(StaticValue.roomWinerId);
            //自己胜利了 ： 作为出完牌的一方胜利 //且自己是地主 获得双倍收益
            StaticValue.roomWinerId = 0;
        }
    }
}
