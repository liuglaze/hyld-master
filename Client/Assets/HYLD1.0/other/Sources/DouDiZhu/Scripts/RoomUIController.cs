using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RoomUIController : GameUIController
{
    private static RoomUIController _instance;
    private void Start()
    {
        showPannal("startPanal");
        //Sprite obj = Resources.Load("poker") as Sprite;
        //GameObject poker = NGUITools.AddChild(GameObject.Find(type.ToString()), obj);
        
    }
    public static RoomUIController Instance { get {
        if (_instance == null)
        {
            _instance = GameObject.Find("UI/UIController/gamePanal").GetComponent<RoomUIController>();
        }
        return _instance;
    } }
    
    public void ClickChangeDesk()
    {
        showPannal("");
    }

    public void sortCard()
    {
        //CardRules.SortCards(StaticValue.selfHandCards, false);
    }

    private void Update()
    {
        if (User.isStartMacthingNormalShouldMainPrcessDo)
        {
            User.isStartMacthingNormalShouldMainPrcessDo = false;
            
            User.StartMacthingNormalShouldMainPrcessDo();
        }
    }
}
