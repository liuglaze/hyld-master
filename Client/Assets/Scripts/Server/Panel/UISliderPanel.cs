/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/29 11:44:50
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using Server;

namespace MVC
{
	public class UISliderPanel : UIbasePanel
    {
       // public Button Login;
        public bool IsCanEnterBattle { get; private set; }
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            //Login.onClick.AddListener(() => { CheckName(); });
        }
        public override void Init()
        {
            base.Init();
            IsCanEnterBattle = false;
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.ClearSence, SocketProto.ActionCode.ClientSendClearSenceReady));
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.ClearSence, SocketProto.ActionCode.AllClearSenceReady));
        }
        public void SendLoadOver()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0].requestCode;
            pack.Actioncode = Requests[0].actionCode;
            //LoginPack loginPack = new LoginPack();
            //loginPack.Username = HYLDStaticValue.UseName;
            //pack.Loginpack = loginPack;
            pack.Str = HYLDStaticValue.PlayerUID.ToString();//PlayerPrefs.GetString(PlayerPrefabConstValue.HYLDPlayerHero, HeroName.XueLi.ToString());
            //pack.Str = "FindName";
          //  Logging.HYLDDebug.Log("FindName : " + pack);
            Requests[0].SendRequest(pack);
            HYLDManger.Instance.ShowMessage(pack.Str);
        }
        public override void OnResponse(MainPack pack)
        {
            HYLDManger.Instance.ShowMessage(pack.Actioncode.ToString());
            base.OnResponse(pack);
            //HYLDStaticValue.PlayerUID = pack.UserInfopack.Id;
            switch (pack.Actioncode)
            {
                case ActionCode.AllClearSenceReady:
                    IsCanEnterBattle = true;
                    /*
                    HYLDStaticValue.PlayerName = pack.UserInfopack.Playername;
                    //Logging.HYLDDebug.Log("   name: " +HYLDStaticValue.PlayerName + "   id: " + HYLDStaticValue.PlayerUID);
                    //UIManger.ShowMessage("注册成功");
                    //UIManger.PopPanel();
                    print("注册成功");
                    HYLDManger.Instance.ShowMessage("登陆成功");
                    //UIManger.PushPanel(UIPanelType.RoomList);
                    
                    //Logging.HYLDDebug.Log("注册成功");
                    */
                    break;
               
                default:
                    print("Def");
                    break;
            }
            //UnityEngine.SceneManagement.SceneManager.LoadScene("HYLDStart");
        }


    }
}