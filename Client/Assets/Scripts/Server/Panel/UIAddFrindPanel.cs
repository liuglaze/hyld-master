/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/28 19:55:44
    Description:     添加好友的数据库
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;

namespace MVC
{
    public class UIAddFrindPanel : UIbasePanel
    {
        [Header("控制好友列表和好友请求切换")]
        public Color SelectedButtonColor;
        public Color NotSelectedButtonColor;
        public Button btnfriendList;
        public Button btnfriendrequest;
        public GameObject friendListGameobject;
        public GameObject friendRequestGameObject;
        [Header("好友列表")]
        public Text txtFriendCount;
        public GameObject FriendItemPrefab;
        public GameObject myItemPrefab;
        private Transform _friendListParent;
        private List<FriendItem> _friendItems = new List<FriendItem>();
        [Header("添加好友")]
        public Button btnAddFriend;
        public Text txtFriendId;
        public Text txtMyId;
        public Color canAddFriendClick;
        public Color canNotAddFriendClick;
        private bool _canAddFiend;
        [Header("添加好友请求列表")]
        public GameObject AplyAddFriendItemPrefab;
        public Transform AplyFriendGroupParent;
        public GameObject _GreenPoint;
        public Text txtAplyFriendCount;
        public override void Init()
        {
            base.Init();
            _friendListParent = friendListGameobject.transform.GetChild(0).transform.GetChild(0);
            OnSelectListClick();
            AddFriendItem(HYLDStaticValue.PlayerName, true);
            Requests.Add(new Server.BaseRequest(this, SocketProto.RequestCode.Friend, SocketProto.ActionCode.AplyAddFriend));
            Requests.Add(new Server.BaseRequest(this, SocketProto.RequestCode.Friend, SocketProto.ActionCode.AcceptAddFriend));
            Requests.Add(new Server.BaseRequest(this, SocketProto.RequestCode.Friend, SocketProto.ActionCode.RejectAddFriend));
            txtMyId.text = $"您的玩家标签为:{HYLDStaticValue.PlayerUID}";
            //AddFriendItem("游戏开发");
            //AddFriendItem("笑傲江湖");
        }
        public void RefreshFriendListInfo()
        {
            foreach (PlayerPack playerPack in HYLDStaticValue.FriendLists)
            {
                AddFriendItem(playerPack.Playername);
            }
        }
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            btnfriendList.onClick.AddListener(OnSelectListClick);
            btnfriendrequest.onClick.AddListener(SelectRequestFriendClick);
            btnAddFriend.onClick.AddListener(OnAddFriendClick);
        }

        private void OnSelectListClick()
        {
            btnfriendList.gameObject.GetComponent<Image>().color = SelectedButtonColor;
            btnfriendrequest.gameObject.GetComponent<Image>().color = NotSelectedButtonColor;
            friendListGameobject.SetActive(true);
            friendRequestGameObject.SetActive(false);
        }
        private void SelectRequestFriendClick()
        {
            btnfriendList.gameObject.GetComponent<Image>().color = NotSelectedButtonColor;
            btnfriendrequest.gameObject.GetComponent<Image>().color = SelectedButtonColor;
            friendListGameobject.SetActive(false);
            friendRequestGameObject.SetActive(true);
        }

        public void AddFriendItem(string name, bool isMyself = false)
        {

            GameObject friendItemOBJ;
            if (isMyself)
            {
                friendItemOBJ = Instantiate(myItemPrefab, _friendListParent);
            }
            else
            {
                friendItemOBJ = Instantiate(FriendItemPrefab, _friendListParent);
            }
            FriendItem friendItem = friendItemOBJ.GetComponent<FriendItem>();
            friendItem.Init(_friendItems.Count + 1, name);
            _friendItems.Add(friendItem);
        }

        public override void Excute(float deltaTime)
        {
            base.Excute(deltaTime);
            txtFriendCount.text = $"好友 ({_friendItems.Count})";

            if (txtFriendId.text.Length ==0 )
            {
                btnAddFriend.transform.GetComponent<Image>().color = canNotAddFriendClick;
                _canAddFiend = false;
            }
            else
            {
                btnAddFriend.transform.GetComponent<Image>().color = canAddFriendClick;
                _canAddFiend = true;
            }

            if (AplyFriendGroupParent.childCount == 0)
            {
                _GreenPoint.SetActive(false);
            }
            else
            {
                _GreenPoint.SetActive(true);
                txtAplyFriendCount.text = AplyFriendGroupParent.childCount.ToString();
            }
        }
        private void OnAddFriendClick()
        {

            try
            {
                if (int.Parse(txtFriendId.text) == HYLDStaticValue.PlayerUID)
                {
                    txtFriendId.transform.parent.GetComponent<InputField>().text = "";
                    HYLDManger.Instance.ShowMessage("自己加自己你要干嘛?");
                    return;
                }
                foreach (PlayerPack playerPack1 in HYLDStaticValue.FriendLists)
                {
                    if (int.Parse(txtFriendId.text) == playerPack1.Id)
                    {
                        txtFriendId.transform.parent.GetComponent<InputField>().text = "";
                        HYLDManger.Instance.ShowMessage("好友已存在");
                        return;
                    }
                }
                if (_canAddFiend)
                {
                    MainPack pack = new MainPack();
                    pack.Requestcode = Requests[0].requestCode;
                    pack.Actioncode = Requests[0].actionCode;
                    PlayerPack playerPack = new PlayerPack();
                    playerPack.Playername = HYLDStaticValue.PlayerName;
                    playerPack.Id = HYLDStaticValue.PlayerUID;
                    pack.UserInfopack = playerPack;
                    pack.Str = txtFriendId.text;
                    //pack.Str = "FindName";
                    Logging.HYLDDebug.Log("AddFriend : " + pack);
                    Requests[0].SendRequest(pack);
                }
            }
            catch(Exception ex)
            {
                Logging.HYLDDebug.Log(ex);
                HYLDManger.Instance.ShowMessage("输入的ID不行啊");
            }
            txtFriendId.transform.parent.GetComponent<InputField>().text = "";
        }
        /// <summary>
        /// 接受加好友
        /// </summary>
        private void OnAccptAplyAddFriendClick(string friendname)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[1].requestCode;
            pack.Actioncode = Requests[1].actionCode;
            PlayerPack playerPack = new PlayerPack
            {
                Playername = HYLDStaticValue.PlayerName,
                Id = HYLDStaticValue.PlayerUID
            };
            pack.UserInfopack = playerPack;
            pack.Str = friendname;
            //pack.Str = "FindName";
            Logging.HYLDDebug.Log("OnAccptAplyAddFriendClick : " + pack);
            Requests[1].SendRequest(pack);
        }
        /// <summary>
        /// 拒绝加好友
        /// </summary>
        private void OnRejectAplyAddFriendClick(string friendname)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[2].requestCode;
            pack.Actioncode = Requests[2].actionCode;
            PlayerPack playerPack = new PlayerPack();
            playerPack.Playername = HYLDStaticValue.PlayerName;
            playerPack.Id = HYLDStaticValue.PlayerUID;
            pack.UserInfopack = playerPack;
            pack.Str = friendname;
            //pack.Str = "FindName";
            Logging.HYLDDebug.Log("OnRejectAplyAddFriendClick : " + pack);
            Requests[2].SendRequest(pack);
        }

        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
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
                        HYLDStaticValue.ActiveFriend.Add(playerPack.Id, playerPack);
                        LongZhiJie.StartUIManger manger = (LongZhiJie.StartUIManger)HYLDManger.Instance.UIBaseManger;
                        UIInvatingFriendPanel panel = (UIInvatingFriendPanel)manger.recycleDic[nameof(UIInvatingFriendPanel)];
                        panel.AddFriendItem(playerPack.Playername, playerPack.Id);
                        
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
                    addFriendItem.Init(pack.Str,OnAccptAplyAddFriendClick,OnRejectAplyAddFriendClick);

                    break;
                default:
                    Logging.HYLDDebug.Log("Def");
                    break;
            }
            //Logging.HYLDDebug.LogError(pack.Returncode.ToString());
        }
    }
}
