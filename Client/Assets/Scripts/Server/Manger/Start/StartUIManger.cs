/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/28 17:57:21
    Description:     登陆界面管理
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace LongZhiJie
{
	public class StartUIManger : UIBaseManger
    {
        public HYLDStartUILogic HYLDStartUILogic;
        [SerializeField]
        public Dictionary<string, MVC.UIbasePanel> recycleDic = new Dictionary<string, MVC.UIbasePanel>();
        private Stack<string> _panelStack = new Stack<string>();
        public override void Excute(float deltaTime)
        {
            base.Excute(deltaTime);
            HYLDStartUILogic.Excute();
            foreach (MVC.UIbasePanel baseWindow in recycleDic.Values)
            {
                if (baseWindow.IsOpen || (baseWindow.gameObject.name == "UIInvatingFriendPanel"))
                {
                    baseWindow.Excute(deltaTime);
                }
            }


        }
        public void StartInit()
        {
            foreach (Transform view in recyclePool.transform)
            {
                MVC.UIbasePanel panel = view.GetComponent<MVC.UIbasePanel>();
                if (panel == null) continue;
                if (Type.GetType("MVC." + view.name).Name != nameof(MVC.UIStartMainPanel))
                {
                    panel.Init();
                    recycleDic.Add(Type.GetType("MVC." + view.name).Name, panel);
                    panel.Close();
                }
            }


        }
        public override void OnInit()
        {
            base.OnInit();
            recycleDic.Clear();
            foreach (Transform view in recyclePool.transform)
            {
                MVC.UIbasePanel panel = view.GetComponent<MVC.UIbasePanel>();
                if (panel == null) continue;
                if (Type.GetType("MVC." + view.name).Name == nameof(MVC.UIStartMainPanel))
                {
                    panel.Init();
                    recycleDic.Add(Type.GetType("MVC." + view.name).Name, panel);
                    Open(nameof(MVC.UIStartMainPanel));
                    break;
                }
            }
        }

    
        public override void Open(string panel)
        {
            if (recycleDic[panel].Open())
                _panelStack.Push(panel);
        }
        public override bool IsOpen(string panel)
        {
            return recycleDic[panel].IsOpen;

        }
        public override void Close()
        {
            base.Close();
            if (_panelStack.Count != 0)
            {
                string panel = _panelStack.Pop();
                recycleDic[panel].Close();
                recycleDic[_panelStack.Peek()].OnRecovery();
            }
        }
        public override void StartGame(string name)
        {
            if (PlayerPrefs.GetInt(PlayerPrefabsEnum.isFirst.ToString(), 0) == 0)
            {
                PlayerPrefs.SetInt(PlayerPrefabsEnum.isFirst.ToString(), 1);
                UnityEngine.SceneManagement.SceneManager.LoadScene("NewTestGame");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("LodingSence");
        }

        public void RefreshFriendListInfo()
        {
            MVC.UIAddFrindPanel panel = (MVC.UIAddFrindPanel)recycleDic[nameof(MVC.UIAddFrindPanel)];
            panel.RefreshFriendListInfo();
            MVC.UIInvatingFriendPanel panel1 = (MVC.UIInvatingFriendPanel)recycleDic[nameof(MVC.UIInvatingFriendPanel)];
            panel1.RefreshFriendListInfo();
        }
        public override MVC.UIbasePanel GetPanel(string panel)
        {
            return recycleDic[panel];
        }

    }
}