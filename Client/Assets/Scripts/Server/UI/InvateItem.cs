/****************************************************
    Author:            龙之介
    CreatTime:    2021/10/5 20:58:26
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
    public class InvateItem : MonoBehaviour
    {
        public enum State
        {
            None = 0,
            WantToInvate = 1,
            WaitToInvate = 2,
            ShowHero = 3
        }

        public Image Hero;
        [Header("欲邀请")]
        public GameObject Invated;
        public Button btnInvate;
        public Image cloud;
        public Text InvatefriendName;
        [Header("等待同意")]
        public GameObject Invating;
        public Text InvatingfriendName;
        public Button btnClose;
        [Header("展示英雄")]
        public Text txtfriendName;
        public HeroName heroName;
        public Image imghero;
        private Sprite[] HeroSprites;
        [HideInInspector]
        public State ItemState
        {
            get; private set;
        }
        public void Close()
        {

            Hero.gameObject.SetActive(false);
            Invated.gameObject.SetActive(false);
            Invating.gameObject.SetActive(false);
            ItemState = State.None;
            gameObject.SetActive(false);
        }
        public void RegisterUIEvent(Action<string> closeInvating)
        {
            btnInvate.onClick.AddListener(() => {
                HYLDManger.Instance.UIBaseManger.Open(nameof(MVC.UIInvatingFriendPanel));
            });
            ItemState = State.None;
            btnClose.onClick.AddListener(() => { closeInvating.Invoke(InvatingfriendName.text); });
        }

        public void ShowActiveFriendName()
        {
            gameObject.SetActive(true);
            WantToInvate();
            //Logging.HYLDDebug.Log(HYLDStaticValue.ActiveFriend.Count);
            foreach (var friendpack in HYLDStaticValue.ActiveFriend.Values)
            {
                //Logging.HYLDDebug.Log(friendpack.State);
                if (friendpack.State == SocketProto.PlayerState.PlayerOnline)
                {
                    //Logging.HYLDDebug.Log(HYLDStaticValue.ActiveFriend[i].State);
                    cloud.gameObject.SetActive(true);
                    //Logging.HYLDDebug.Log(HYLDStaticValue.ActiveFriend[i].State);
                    InvatefriendName.text = friendpack.Playername;
                    return;
                }
            }
            ItemState = State.None;
            cloud.gameObject.SetActive(false);
        }

        private void WantToInvate()
        {
            gameObject.SetActive(true);
            Hero.gameObject.SetActive(false);
            Invated.gameObject.SetActive(true);
            Invating.gameObject.SetActive(false);
            ItemState = State.WantToInvate;
        }
        public void WaitToInvate(string friendname)
        {
            gameObject.SetActive(true);
            //Logging.HYLDDebug.LogError(ItemState);
            Hero.gameObject.SetActive(false);
            Invated.gameObject.SetActive(false);
            Invating.gameObject.SetActive(true);
            ItemState = State.WaitToInvate;
            InvatingfriendName.text = friendname;
            //Logging.HYLDDebug.LogError(ItemState);
        }
        public void ShowHero(string username,string heroname)
        {
            gameObject.SetActive(true);
            txtfriendName.text = username;
            ChangeHero(heroname);
            //Logging.HYLDDebug.LogError(Hero.gameObject);
            Hero.gameObject.SetActive(true);
            Invated.gameObject.SetActive(false);
            Invating.gameObject.SetActive(false);
            ItemState = State.ShowHero;
        }

        private void ChangeHero(string heroname)
        {
            heroName= (HeroName)Enum.Parse(typeof(HeroName), heroname);
            if (HeroSprites == null)
            {
                HeroSprites = GameObject.Find("Canvas").GetComponent<HYLDStartUILogic>().HeroSprites;
            }
            //菜单图片改为对应英雄
            foreach (var sp in HeroSprites)
            {
                if (sp.name == heroname)
                {
                    imghero.sprite = sp;
                    break;
                }
            }
        }
    }
}