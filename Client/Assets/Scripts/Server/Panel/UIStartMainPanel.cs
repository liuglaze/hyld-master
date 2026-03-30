/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/24 16:21:44
    Description:     UI主菜单面板
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using Server;
using SocketProto;

namespace MVC
{
    public class UIStartMainPanel : UIbasePanel
    {

        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            foreach (Button btn in buttonList)
            {
                if (btn.name == "btnFriend")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIFriendPanel)); });
                }
                else if (btn.name == "btnShop")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIShopPanel)); });
                }
                else if (btn.name == "btnSetting")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UISettingPanel)); });
                }
                else if (btn.name == "btnInvatingFriend")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIInvatingFriendPanel)); });
                }
                else if (btn.name == "btnTeam")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIFriendPanel)); });
                }
                else if (btn.name == "btnAddFriend")
                {
                    btn.onClick.AddListener(() => { HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIAddFrindPanel)); });
                }
                /*
                else if (btn.name == "btnStartGame")
                {
                    btn.onClick.AddListener(StartMathching);
                }
                */
            }
        }

        public override void Init()
        {
            base.Init();
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.User, SocketProto.ActionCode.UpdateName));
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.User, SocketProto.ActionCode.FindFriendsInfo));
            Requests.Add(new BaseRequest(this, RequestCode.Friend, ActionCode.FriendLogin));
            Requests.Add(new BaseRequest(this, RequestCode.Friend, ActionCode.FriendLogout));
            Requests.Add(new BaseRequest(this, RequestCode.User, ActionCode.ChangeHero));
            Requests.Add(new BaseRequest(this, RequestCode.User, ActionCode.BattleReview));
            if (HYLDStaticValue.PlayerName != "")
            {
                FindFriendsInfo();
            }
        }
        public override void OnResponse(MainPack pack)
        {
            HYLDManger.Instance.ShowMessage(pack.Actioncode.ToString());
            base.OnResponse(pack);
            if (pack.Requestcode == RequestCode.User)
            {
                if (pack.Actioncode == ActionCode.BattleReview)
                {
                    Debug.LogError(pack);
                }
                else
                {
                    ResponseUser(pack);
                }
            }
            else if (pack.Requestcode == RequestCode.Friend)
            {
                OnFriendResponse(pack);
            }
          
        }

        /***************************User部分********************************/
        public Text PlayerNameText;
        public void CreatNameClick()
        {
            print("!!!!!!!!!!!!!!");
            UpdateName();
            //PlayerPrefs.SetString("HYLDPlayerName", PlayerNameText.text);

        }
      
        private void UpdateName()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0].requestCode;
            pack.Actioncode = Requests[0].actionCode;
            LoginPack loginPack = new LoginPack();
            loginPack.Username = HYLDStaticValue.UseName;
            pack.Loginpack = loginPack;
            pack.Str = PlayerNameText.text;
            Logging.HYLDDebug.Log("UpdateName :" + pack);
            Requests[0].SendRequest(pack);
        }
        private void FindFriendsInfo()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[1].requestCode;
            pack.Actioncode = Requests[1].actionCode;
            PlayerPack playerPack =new PlayerPack();
            playerPack.Id= HYLDStaticValue.PlayerUID;
            pack.UserInfopack = playerPack;
            //Logging.HYLDDebug.Log("FindFriendsInfo :" + pack);
            HYLDManger.Instance.ShowMessage("FindFriendInfo");
            Requests[1].SendRequest(pack);
        }
        public void ChangeHero()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[4].requestCode;
            pack.Actioncode = Requests[4].actionCode;
            PlayerPack playerPack = new PlayerPack();
            playerPack.Hero = (SocketProto.Hero)Enum.Parse(typeof(SocketProto.Hero),HYLDStaticValue.myheroName);
            pack.UserInfopack = playerPack;
            if (HYLDStaticValue.Myroom != null)
            {
                pack.Str = "Room";
            }
            Requests[4].SendRequest(pack);
        }
        private void ResponseUser(MainPack pack)
        {

            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    if (pack.Actioncode == ActionCode.UpdateName)
                    {
                        HYLDStaticValue.PlayerName = pack.Str;
                        print("username : " + HYLDStaticValue.PlayerName);
                    }
                    else if (pack.Actioncode == ActionCode.FindFriendsInfo)
                    {
                        /*
                        HYLDStaticValue.FriendLists = pack.Friendspack;
                        print("FindFriendsInfo ac!");
                        foreach (PlayerPack playerPack in HYLDStaticValue.FriendLists)
                        {
                            if(playerPack.State!=PlayerState.PlayerOutline)
                            {
                                HYLDStaticValue.ActiveFriend.Add(playerPack.Id, playerPack);
                            }
                            print(playerPack);
                        }
                        LongZhiJie.StartUIManger startUIManger = (LongZhiJie.StartUIManger)HYLDManger.Instance.UIBaseManger;
                        startUIManger.RefreshFriendListInfo();
                        */
                        MVC.UIInvatingFriendPanel panel1 = (MVC.UIInvatingFriendPanel)HYLDManger.Instance.UIBaseManger.GetPanel(nameof(MVC.UIInvatingFriendPanel));
                        panel1.UpDateActiveFriendInfo(pack);
                    }
                    break;
                case ReturnCode.Fail:
                    if (pack.Actioncode == ActionCode.UpdateName)
                    {
                        HYLDStaticValue.PlayerName = "";
                    }
                    break;
                default:
                    print("Def");
                    break;
            }
        }


        /***************************活跃好友显示************************************/
        public void OnFriendResponse(MainPack pack)
        {
            base.OnResponse(pack);
            if (pack.Actioncode == ActionCode.FriendLogin)
            {
                HYLDStaticValue.ActiveFriend.Add(pack.UserInfopack.Id, pack.UserInfopack);
                LongZhiJie.StartUIManger manger=(LongZhiJie.StartUIManger)HYLDManger.Instance.UIBaseManger;
                UIInvatingFriendPanel panel = (UIInvatingFriendPanel)manger.recycleDic[nameof(UIInvatingFriendPanel)];
                panel.AddFriendItem(pack.UserInfopack.Playername, pack.UserInfopack.Id);
                //panel.ShowActiveFriendName();
            }
            else if (pack.Actioncode == ActionCode.FriendLogout)
            {
                Logging.HYLDDebug.LogError("ActionCode.FriendLogout  :" + pack);
                //HYLDStaticValue.ActiveFriend.Remove(pack.UserInfopack.Id);
            }
            /*
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    if (pack.Actioncode == ActionCode.AcceptAddFriend)
                    {

                        string[] message = pack.Str.Split('#');
                        PlayerPack playerPack = new PlayerPack();
                        playerPack.Playername = message[0];
                        playerPack.Id = int.Parse(message[1]);
                        HYLDStaticValue.FriendLists.Add(playerPack);
                        AddFriendItem(playerPack.Playername);
                    }

                    break;
                case ReturnCode.Fail:
                    if (pack.Actioncode == ActionCode.AplyAddFriend)
                    {
                        HYLDManger.Instance.ShowMessage("好友id不存在或未登录");
                    }
                    break;
                case ReturnCode.AddFriend:
                    AplyAddFriendItem addFriendItem = Instantiate(AplyAddFriendItemPrefab, AplyFriendGroupParent).GetComponent<AplyAddFriendItem>();
                    addFriendItem.Init(pack.Str, OnAccptAplyAddFriendClick, OnRejectAplyAddFriendClick);

                    break;
                default:
                    Logging.HYLDDebug.Log("Def");
                    break;
            }
            */
            //Logging.HYLDDebug.LogError(pack.Returncode.ToString());
        }
    }
}