using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class StartPanalUIController : GameUIController
{
    private static StartPanalUIController _instance;
    public static StartPanalUIController Instance { get {
        if (_instance == null)
        {
            _instance = GameObject.Find("UI/UIController/gamePanal/startPanal").GetComponent<StartPanalUIController>();
        }
        return _instance;
    } }

    void Start()
    {
        showPannal("StartMatching");    
    }
    public void ClickStartMatching()
    {
        
        showPannal("Matching");
        StaticValue.initGaming();

        TCPSocket.Instance.Send(OldRequestCode.Game, OldActionCode.StartMacthingNormal, StaticValue.selfPhoneNumber);
        //所有的手牌
        
    }
}
