/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/28 19:56:50
    Description:     UI加好友
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using Server;
using Google.Protobuf.Collections;

namespace MVC
{
    /// <summary>
    /// 这里写的一坨屎，wsfw耦合度极高
    /// </summary>
    public class UIInvatingFriendPanel : UIbasePanel
    {

        public override void Init()
        {
            base.Init();
            //初始化主菜单交互控件
            RoomIDObj.SetActive(false);//房间ID
            RightInvateItem.RegisterUIEvent(OnInvatingCloseClick);//取消邀请好友
            leftInvateItem.RegisterUIEvent(OnInvatingCloseClick);//取消邀请好友
            RightInvateItem.Close();
            leftInvateItem.Close();
            btnEixtRoom.onClick.AddListener(OnExitRoomClick);//离开房间按钮
            btnStartMatching.onClick.AddListener(StartMathching);//开始匹配按钮
            btnExitMatching.onClick.AddListener(ExitMathching);//离开匹配按钮
            //初始化弹出框交互控件
            btnAccepte.onClick.AddListener(OnAccptAplyInvateFriendClick);//同意邀请
            btnReject.onClick.AddListener(OnRejectAplyInvateFriendClick);//拒绝邀请


            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.CreateRoom));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.InviteFriend));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.AcceptInvateFriend));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.RejectInvateFriend));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.JoinRoom));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.ExitRoom));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.CancalInvateFriend));
            Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.UpDateActiveFriendInfo));
            Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.AddMatchingPlayer));
            Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.RemoveMatchingPlayer));
            //Requests.Add(new BaseRequest(this, RequestCode.FriendRoom, ActionCode.ChangeHero));
        }
        public override void Excute(float deltaTime)
        {
            base.Excute(deltaTime);
            UIInvateExcute();
        }
        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
            if (pack.Requestcode == RequestCode.FriendRoom)
            {
                OnFriendRoomResponse(pack);
                OnApllyToFriendRoomResponse(pack);
                if (pack.Actioncode == ActionCode.ExitRoom)
                {
                    Logging.HYLDDebug.Log(ActionCode.ExitRoom);
                    _RoomFriendsDic.Clear();
                    foreach (PlayerPack playerPack in pack.Playerspack)
                    {
                        if (playerPack.Id == HYLDStaticValue.PlayerUID) continue;
                        _RoomFriendsDic.Add(playerPack.Id, playerPack);
                    }
                    //RefreshFriendShow(pack.Playerspack); 
                }
                if (pack.Actioncode == ActionCode.CancalInvateFriend)
                {
                    Logging.HYLDDebug.Log("CancalInvateFriend  : " + pack.Returncode);
                    if (HYLDManger.Instance.UIBaseManger.IsOpen(nameof(MVC.UIAplyInvateFriendPanel)))
                    {
                        HYLDManger.Instance.UIBaseManger.Close();
                    }
                }
                if (pack.Actioncode == ActionCode.UpDateActiveFriendInfo)
                {

                    if (pack.Str != null && pack.Str == "ChangeHero")
                    {
                        Logging.HYLDDebug.Log("ChangeHero  : " + pack);
                        Logging.HYLDDebug.Log(pack.UserInfopack);
                        //HYLDStaticValue.ActiveFriend[pack.UserInfopack.Id] = pack.UserInfopack;
                        //RefreshFriendListInfo();
                        _RoomFriendsDic[pack.UserInfopack.Id] = pack.UserInfopack;
                    }
                    else
                    {
                        UpDateActiveFriendInfo(pack);
                    }
                }
            }
            else if (pack.Requestcode == RequestCode.Matching)
            {
                //1.3开始匹配
                if (pack.Actioncode == ActionCode.AddMatchingPlayer)
                {
                    MatchingInfo.text = "已找到玩家 " + pack.Str;
                    HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIMatchingPanel));
                }
                else
                {
                    if (pack.Str.Equals("-1"))
                    {
                        MatchingInfo.text = "已找到玩家 " + pack.Str;
                        //  Logging.HYLDDebug.LogError(">>>>");
                        HYLDManger.Instance.UIBaseManger.Close();
                    }
                    else
                    {
                        // Logging.HYLDDebug.LogError("sadadadad");
                        MatchingInfo.text = "已找到玩家 " + pack.Str;
                        HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIMatchingPanel));
                    }
                }
            }

        }
        /// <summary>
        /// 更新活跃好友信息
        /// </summary>
        /// <param name="pack"></param>
        public void UpDateActiveFriendInfo(MainPack pack)
        {
            //Logging.HYLDDebug.Log("UpDateActiveFriendInfo  : " + pack);
            HYLDStaticValue.ActiveFriend.Clear();

            //Logging.HYLDDebug.Log(pack.Playerspack);
            if (pack.Playerspack.Count == 0)
            {
                FriendItemsDicClear();
                return;
            }
            foreach (PlayerPack playerPack in pack.Playerspack)
            {
                if (playerPack.State == PlayerState.PlayerOnline)
                {
                    HYLDStaticValue.ActiveFriend.Add(playerPack.Id, playerPack);
                }
                print(playerPack);
            }

            RefreshFriendListInfo();
        }
     
       
        /************************邀请好友面板********************************/
        #region
        [Header("邀请好友面板")]
        public Transform GroupParent;
        public GameObject InvateFriendItemPrefab;
        
        private Dictionary<int, InvateFriendItem> _invateFriendItemslist = new Dictionary<int, InvateFriendItem>();
        public void RefreshFriendListInfo()
        {
            FriendItemsDicClear();
            foreach (var playerPack in HYLDStaticValue.ActiveFriend)
            {
                AddFriendItem(playerPack.Value.Playername, playerPack.Value.Id);
            }
        }

        public void AddFriendItem(string name, int id)
        {
            if (_invateFriendItemslist.ContainsKey(id)) return;
            try
            {
                InvateFriendItem item = Instantiate(InvateFriendItemPrefab, GroupParent).GetComponent<InvateFriendItem>();
                item.Init(name, OnInvateFriendClick, id);
                _invateFriendItemslist.Add(id, item);
            }
            catch
            {
                Logging.HYLDDebug.LogError("添加在线好友失败！");
            }
        }

        public void FriendItemsDicClear()
        {
            foreach (InvateFriendItem Itemsa in _invateFriendItemslist.Values)
            { 
                Itemsa.gameObject.SetActive(false);
                Destroy(Itemsa.gameObject, 0.5f);
            }
            _invateFriendItemslist.Clear();
        }
        private PlayerPack GetActiveFriend(string name)
        {
            foreach (PlayerPack player in HYLDStaticValue.ActiveFriend.Values)
            {
                if (player.Playername == name)
                {
                    return player;
                }
            }
            return null;
        }
        private void OnInvateFriendClick(string friendname)
        {
            PlayerPack friendpack = GetActiveFriend(friendname);
            HYLDManger.Instance.UIBaseManger.Close();
            if (friendpack == null)
            {
                return;
            }
            //ShowWaitToFriend(friendname);
            _InvatingFriendsDic.Add(friendpack.Id, friendpack);

            MainPack pack = new MainPack();
            if (HYLDStaticValue.Myroom == null)
            {
                pack.Requestcode = Requests[0].requestCode;
                pack.Actioncode = Requests[0].actionCode;
                FriendRoomPack room = new FriendRoomPack();
                room.Roomid = $"{UnityEngine.Random.Range(100, 1000) + HYLDStaticValue.PlayerUID} ";
                room.Maxnum = 3;
                pack.Friendroompack.Add(room);
                pack.Str = friendname;
                Requests[0].SendRequest(pack);
                Logging.HYLDDebug.Log("OnCreateRoom : " + pack);
                return;
            }
            pack.Requestcode = Requests[1].requestCode;
            pack.Actioncode = Requests[1].actionCode;
            pack.Str = friendname;
            pack.Friendroompack.Add(HYLDStaticValue.Myroom);
            Requests[1].SendRequest(pack);
        }
#endregion
        /************************主菜单面板******************************/
        #region
        [Header("主菜单面板")]
        public InvateItem leftInvateItem;
        public InvateItem RightInvateItem;
        public GameObject RoomIDObj;
        public Button btnEixtRoom;
        public Button btnStartMatching;
        public Text txtRoomID;
        private Dictionary<int, PlayerPack> _InvatingFriendsDic=new Dictionary<int, PlayerPack>();
        private Dictionary<int, PlayerPack> _RoomFriendsDic=new Dictionary<int, PlayerPack>();
        private void UIInvateExcute()
        {
            int cnt = 2;
            /*cnt记录左右Item被使用次数。
             * 其中Room优先级最高
             * 邀请中优先级其次
             * 正常状态最低
             */
            //处理room
            //Logging.HYLDDebug.Log($"Room {_RoomFriendsDic.Count}");
            foreach (PlayerPack playerPack in _RoomFriendsDic.Values)
            {
                //Logging.HYLDDebug.Log(playerPack);
                if (cnt == 2)
                {
                    leftInvateItem.ShowHero(playerPack.Playername, playerPack.Hero.ToString());
                }
                else if (cnt == 1)
                {
                    RightInvateItem.ShowHero(playerPack.Playername, playerPack.Hero.ToString());
                }
                cnt--;
            }
            if (cnt == 0) return;

            //处理邀请中
            foreach (PlayerPack playerPack in _InvatingFriendsDic.Values)
            {
                //Logging.HYLDDebug.Log(playerPack);
                if (cnt == 2)
                {
                    leftInvateItem.WaitToInvate(playerPack.Playername);
                }
                else if (cnt == 1)
                {
                    RightInvateItem.WaitToInvate(playerPack.Playername);
                }
                cnt--;
            }
            if (cnt == 0) return;
            //Logging.HYLDDebug.Log(cnt);
            //处理正常状态
            if (cnt == 2)
            {
                leftInvateItem.ShowActiveFriendName();
                RightInvateItem.Close();
            }
            else
            {
                RightInvateItem.ShowActiveFriendName();
            }
        }

        private void OnInvatingCloseClick(string friendname)
        {
            _InvatingFriendsDic.Remove(GetActiveFriend(friendname).Id);
            //ShowActiveFriendName();
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[6].requestCode;
            pack.Actioncode = Requests[6].actionCode;
            pack.Str = friendname;
            Logging.HYLDDebug.Log("OnInvatingClose : " + pack);
            Requests[6].SendRequest(pack);
        }
        public void ChangeHeroClick()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[9].requestCode;
            pack.Actioncode = Requests[9].actionCode;
            PlayerPack playerPack = new PlayerPack();
            playerPack.Hero = (SocketProto.Hero)Enum.Parse(typeof(SocketProto.Hero), HYLDStaticValue.myheroName);
            pack.UserInfopack = playerPack;
            Requests[9].SendRequest(pack);
        }
        private void OnExitRoomClick()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[5].requestCode;
            pack.Actioncode = Requests[5].actionCode;
            pack.Str = "x";
            Logging.HYLDDebug.Log("OnInvatingClose : " + pack);
            Requests[5].SendRequest(pack);
            //Remake();
            _RoomFriendsDic.Clear();
            _InvatingFriendsDic.Clear();
            RoomIDObj.SetActive(false);
            OtherFriendRoomID = "";

            HYLDStaticValue.Myroom = null;
            //HYLDManger.Instance.UIBaseManger.Close();
            //ShowActiveFriendName(true);
        }
        /*
        #region 客户端UI显示部分
        
        private void RefreshFriendShow(RepeatedField<PlayerPack> Playerspack)
        {
            Remake(false);
            foreach (PlayerPack playerPack in Playerspack)
            {
                if (playerPack.Playername != HYLDStaticValue.PlayerName)
                {
                    ShowHero(playerPack.Playername, playerPack.Hero.ToString(), true);
                    //ShowActiveFriendName();
                }
            }
            if(HYLDStaticValue.ActiveFriend.Count>1)
            ShowActiveFriendName();
        }
        public void RefreshFriendListInfo()
        {
            foreach (var playerPack in HYLDStaticValue.ActiveFriend)
            {
                AddFriendItem(playerPack.Value.Playername, playerPack.Value.Id);
            }
            ShowActiveFriendName();
        }
        private void Remake(bool isReallyDestroyRoom=true)
        {
            if (isReallyDestroyRoom)
            {
                RoomIDObj.SetActive(false);
                OtherFriendRoomID = "";
                myroom = null;
                ShowActiveFriendName();
            }
            
            RightInvateItem.Close();
            leftInvateItem.Close();

        }
        public void ShowWaitToFriend(string friendname)
        {
            Logging.HYLDDebug.LogError(leftInvateItem.ItemState + "      ||||ShowWaitToFriend|||    " + RightInvateItem.ItemState);
            if (leftInvateItem.ItemState == InvateItem.State.WantToInvate)
            {
                leftInvateItem.WaitToInvate(friendname);
                //Logging.HYLDDebug.LogError(leftInvateItem.ItemState + "      |||||||    " + RightInvateItem.ItemState);
                if (_invateFriendItemslist.Count>1&&RightInvateItem.ItemState == InvateItem.State.None)
                {
                    RightInvateItem.ShowActiveFriendName();
                }
                Logging.HYLDDebug.LogError(leftInvateItem.ItemState + "   <--   |||||||  -->  " + RightInvateItem.ItemState);
            }
            else if (RightInvateItem.ItemState == InvateItem.State.WantToInvate)
            {
                RightInvateItem.WaitToInvate(friendname);
            }
            Logging.HYLDDebug.LogError(leftInvateItem.ItemState + "      |||||||    " + RightInvateItem.ItemState);
        }
        public void ShowHero(string username,string heroname,bool isForce=false)
        {
            Logging.HYLDDebug.Log(leftInvateItem.ItemState+"      |||showHero||||    "+RightInvateItem.ItemState +"   isForce :"+isForce);
            if (isForce)
            {
                if (leftInvateItem.ItemState != InvateItem.State.ShowHero)
                {
                    leftInvateItem.ShowHero(username,heroname);
                }
                else if (RightInvateItem.ItemState != InvateItem.State.ShowHero)
                {
                    RightInvateItem.ShowHero(username, heroname);
                }
            }
            else
            {
                if (leftInvateItem.ItemState == InvateItem.State.WaitToInvate)
                {
                    leftInvateItem.ShowHero(username, heroname);
                    if (RightInvateItem.ItemState == InvateItem.State.None)
                        RightInvateItem.ShowActiveFriendName();
                }
                else if (RightInvateItem.ItemState == InvateItem.State.WaitToInvate)
                {
                    RightInvateItem.ShowHero(username, heroname);
                }
            }
        }
         public void ShowActiveFriendName(bool isReject=false)
        {
            Logging.HYLDDebug.Log(leftInvateItem.ItemState + "      |||ShowActiveFriendName||||    " + RightInvateItem.ItemState + "   isReject :" + isReject);
            if (isReject)
            {
                if (leftInvateItem.ItemState == InvateItem.State.WaitToInvate)
                {
                    leftInvateItem.ShowActiveFriendName();
                }
                else if (RightInvateItem.ItemState == InvateItem.State.WaitToInvate)
                {
                    RightInvateItem.ShowActiveFriendName();
                }
                return;
            }

            if (leftInvateItem.ItemState == InvateItem.State.WantToInvate) return;
            if (leftInvateItem.ItemState == InvateItem.State.None)
            {
                leftInvateItem.ShowActiveFriendName();
            }
            else if (RightInvateItem.ItemState == InvateItem.State.None)
            {
                RightInvateItem.ShowActiveFriendName();
            }
        }
        
        #endregion*/
        public void OnFriendRoomResponse(MainPack pack)
        {
            if (pack.Actioncode == ActionCode.CreateRoom)
            {
                Logging.HYLDDebug.Log("CreateRoom   " + pack.Friendroompack[0].Roomid);
                if (pack.Returncode == ReturnCode.Succeed)
                {
                    RoomIDObj.SetActive(true);
                    HYLDStaticValue.Myroom = pack.Friendroompack[0];
                    txtRoomID.text = HYLDStaticValue.Myroom.Roomid;
                }
                else
                {
                    RoomIDObj.SetActive(false);
                    HYLDManger.Instance.ShowMessage("创建房间失败，请重试！");
                    //ShowActiveFriendName();
                }
            }
            if (pack.Actioncode == ActionCode.InviteFriend)
            {
                FriendName.text = pack.Str;
                txtFriendWantToInvateYouToTeam.text = pack.Str + "想和您组队游戏!";
                OtherFriendRoomID=pack.Friendroompack[0].Roomid;

                HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIAplyInvateFriendPanel));
            }

        }
       
        #endregion
        /***************************弹出框设置************************************/
        #region
        [Header("弹出框设置")]
        public Text FriendName;
        public Text txtFriendWantToInvateYouToTeam;
        public Button btnAccepte;
        public Button btnReject;
        private string OtherFriendRoomID;

        /// <summary>
        /// 接受邀请好友
        /// </summary>
        private void OnAccptAplyInvateFriendClick()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[2].requestCode;
            pack.Actioncode = Requests[2].actionCode;
            PlayerPack playerPack = new PlayerPack
            {
                Playername = HYLDStaticValue.PlayerName,
                Id = HYLDStaticValue.PlayerUID
            };
            pack.UserInfopack = playerPack;
            pack.Str = OtherFriendRoomID;
            //pack.Str = "FindName";
            Logging.HYLDDebug.Log("OnAccptInvateAddFriendClick : " + pack);
            Requests[2].SendRequest(pack);
            HYLDManger.Instance.UIBaseManger.Close();
        }
        /// <summary>
        /// 拒绝邀请好友
        /// </summary>
        private void OnRejectAplyInvateFriendClick()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[3].requestCode;
            pack.Actioncode = Requests[3].actionCode;
            PlayerPack playerPack = new PlayerPack();
            playerPack.Playername = HYLDStaticValue.PlayerName;
            playerPack.Id = HYLDStaticValue.PlayerUID;
            pack.UserInfopack = playerPack;
            pack.Str = OtherFriendRoomID;
            //pack.Str = "FindName";
            Logging.HYLDDebug.Log("OnRejectAplyInvateFriendClick : " + pack);
            Requests[3].SendRequest(pack);
            HYLDManger.Instance.UIBaseManger.Close();
        }
        /// <summary>
        /// 回调
        /// </summary>
        /// <param name="pack"></param>
        public void OnApllyToFriendRoomResponse(MainPack pack)
        {
            if (pack.Actioncode == ActionCode.AcceptInvateFriend)
            {
                if (pack.Returncode == ReturnCode.Succeed)
                {
                    Logging.HYLDDebug.Log("OnApllyToFriendRoomResponse   AcceptAddFriend  Succeed");
                    RoomIDObj.SetActive(true);
                    HYLDStaticValue.Myroom = pack.Friendroompack[0];
                    txtRoomID.text = HYLDStaticValue.Myroom.Roomid;
                    _RoomFriendsDic.Clear();
                    foreach (PlayerPack playerPack in pack.Playerspack)
                    {
                        if (playerPack.Id == HYLDStaticValue.PlayerUID) continue;
                        _RoomFriendsDic.Add(playerPack.Id, playerPack);
                    }
                }
                else if (pack.Returncode == ReturnCode.NotRoom)
                {
                    HYLDManger.Instance.ShowMessage("房间已销毁");
                }
                else
                {
                    HYLDManger.Instance.ShowMessage("加入房间失败");
                }
            }
            if (pack.Actioncode == ActionCode.RejectInvateFriend)
            {
                if (pack.Returncode == ReturnCode.Succeed)
                {
                    _InvatingFriendsDic.Remove(pack.UserInfopack.Id);
                    //RoomIDObj.SetActive(false);
                    //OtherFriendRoomID = "";
                    //HYLDManger.Instance.UIBaseManger.Close();
                    //ShowActiveFriendName(true);
                }
                else if (pack.Returncode == ReturnCode.Fail)
                {
                    Logging.HYLDDebug.LogError("程序崩溃");
                }
                
            }
            if (pack.Actioncode == ActionCode.JoinRoom)
            {
                //有玩家加入房间了
                if (pack.Returncode == ReturnCode.Succeed)
                {
                    Logging.HYLDDebug.Log("OnJoinRoomResponse  Succeed   " + pack.Playerspack.Count);//+pack.UserInfopack);
                    _RoomFriendsDic.Clear();
                    foreach (PlayerPack playerPack in pack.Playerspack)
                    {
                        if (playerPack.Id == HYLDStaticValue.PlayerUID) continue;
                        if (_InvatingFriendsDic.ContainsKey(playerPack.Id))
                        {
                            _InvatingFriendsDic.Remove(playerPack.Id);
                        }                        
                        _RoomFriendsDic.Add(playerPack.Id, playerPack);
                    }
                    //RefreshFriendShow(pack.Playerspack);
                                                                                         //_RoomFriendsDic.Add(pack.UserInfopack.Id, pack.UserInfopack);
                }
                else
                {
                    Logging.HYLDDebug.Log("OnJoinRoomResponse  Fail   ");

                }
            }
            
        }
        #endregion
        /************************匹配面板********************************/
        [Header("*匹配面板*")]
        public Text MatchingInfo;
        public Button btnExitMatching;
        private void StartMathching()
        {
            /*
             1.1点击对战开开始匹配
            将玩家的BattlePlayerPack信息传给服务器
             
             */
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[8].requestCode;
            pack.Actioncode = Requests[8].actionCode;
            PlayerPack roomMaster = new PlayerPack();
            roomMaster.Fightpattern = FightPattern.BaoShiZhengBa;
            roomMaster.Id = HYLDStaticValue.PlayerUID;
            pack.Playerspack.Add(roomMaster);
            if (_RoomFriendsDic.Count > 0)
            {
                foreach (var friend in _RoomFriendsDic.Keys)
                {
                    PlayerPack friendPack = new PlayerPack();
                    friendPack.Id = friend;
                    pack.Playerspack.Add(friendPack);
                }
            }
            //LoginPack loginPack = new LoginPack();
            //loginPack.Username = HYLDStaticValue.UseName;
            //pack.Loginpack = loginPack;
            //pack.Str = PlayerNameText.text;
            Logging.HYLDDebug.Log("StartMathching :" + pack);
            Requests[8].SendRequest(pack);
        }
        private void ExitMathching()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[9].requestCode;
            pack.Actioncode = Requests[9].actionCode;
            PlayerPack roomMaster = new PlayerPack();
            roomMaster.Fightpattern = FightPattern.BaoShiZhengBa;
            roomMaster.Id = HYLDStaticValue.PlayerUID;
            pack.Playerspack.Add(roomMaster);
            if (_RoomFriendsDic.Count > 0)
            {
                foreach (var friend in _RoomFriendsDic.Keys)
                {
                    PlayerPack friendPack = new PlayerPack();
                    friendPack.Id = friend;
                    pack.Playerspack.Add(friendPack);
                }
            }
            //LoginPack loginPack = new LoginPack();
            //loginPack.Username = HYLDStaticValue.UseName;
            //pack.Loginpack = loginPack;
            //pack.Str = PlayerNameText.text;
            Logging.HYLDDebug.Log("ExitMathching :" + pack);
            Requests[9].SendRequest(pack);
        }
    }
}
