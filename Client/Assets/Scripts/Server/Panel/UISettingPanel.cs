/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/28 19:47:58
    Description:     游戏设置
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace MVC
{
	public class UISettingPanel :UIbasePanel
	{
        public GameObject yinXiaoOpen, yinXiaoClose;
        public GameObject yinyueOpen, yinyueClose;
        public GameObject FriendOpen, FriendClose;
        public override void Init()
        {
            base.Init();
            yinXiaoOpen.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseSoundEffect, 1) == 1); yinXiaoClose.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseSoundEffect, 1)==0);
            yinyueOpen.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseMusic, 1) == 1); yinyueClose.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseMusic, 1) == 0);
            FriendOpen.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseFreind, 1) == 1); FriendClose.SetActive(PlayerPrefs.GetInt(PlayerPrefabConstValue.UseFreind, 1) == 0);
        }
        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            foreach (Button btn in buttonList)
            {
                if (btn.name == "btnsoundeffectsOpen"|| btn.name == "btnsoundeffectsClose")
                {
                    btn.onClick.AddListener(() => { yinXiaoOpen.SetActive(!yinXiaoOpen.activeSelf);yinXiaoClose.SetActive(!yinXiaoClose.activeSelf); PlayerPrefs.SetInt(PlayerPrefabConstValue.UseSoundEffect, yinXiaoOpen.activeSelf ? 1 : 0); });
                }
                else if (btn.name == "btnMusicOpen" || btn.name == "btnMusicClose")
                {
                    btn.onClick.AddListener(() => { yinyueOpen.SetActive(!yinyueOpen.activeSelf); yinyueClose.SetActive(!yinyueClose.activeSelf); PlayerPrefs.SetInt(PlayerPrefabConstValue.UseMusic, yinyueOpen.activeSelf ? 1 : 0); });
                }
                else if (btn.name == "btnFriendOpen" || btn.name == "btnFriendClose")

                {
                    btn.onClick.AddListener(() => { FriendOpen.SetActive(!FriendOpen.activeSelf); FriendClose.SetActive(!FriendClose.activeSelf); PlayerPrefs.SetInt(PlayerPrefabConstValue.UseFreind,FriendOpen.activeSelf ? 1 : 0); });
                }
            }
        }
    }
}