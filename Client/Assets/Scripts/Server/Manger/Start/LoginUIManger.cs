/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/10 22:8:30
    Description:     游戏开始菜单UI
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using System.Reflection;

namespace LongZhiJie
{
	public class LoginUIManger : UIBaseManger
	{
        [SerializeField]
        public Dictionary<string,MVC.UIbasePanel> recycleDic=new Dictionary<string, MVC.UIbasePanel>();
        //private Stack<GameObject> workstationStack=new Stack<GameObject>();
        //private Stack<GameObject> noticeStack = new Stack<GameObject>();
        private Stack<string> _panelStack=new Stack<string>();
        private void Update()
        {

        }
        public override void Excute(float deltaTime)
        {
            base.Excute(deltaTime);
            foreach (MVC.UIbasePanel baseWindow in recycleDic.Values)
            {
                if (baseWindow.IsOpen)
                {
                    baseWindow.Excute(deltaTime);
                }
            }
        }
        public override bool IsOpen(string panel)
        {
            return recycleDic[panel].Open();

        }
        public override void OnInit()
        {
            base.OnInit();
            recycleDic.Clear();
            foreach (Transform view in recyclePool.transform)
            {
                MVC.UIbasePanel panel = view.GetComponent<MVC.UIbasePanel>();
                if (panel == null) continue;
                panel.Init();
                //Logging.HYLDDebug.LogError(panel);
                //Logging.HYLDDebug.LogError(recycleDic);
                recycleDic.Add(Type.GetType("MVC."+view.name).Name, panel);
                //panel.Open();
            }

            Open(nameof(MVC.UILoginPanel));
            /*
            foreach (Transform view in recyclePool.transform)
            {
                if (LZJExternTool.IsSubClassOf(Type.GetType("MVC.View." + view.name), typeof(MVC.View.UIbasePanel)))
                {
                    MVC.View.UIbasePanel panel =(MVC.View.UIbasePanel)Type.GetType("MVC.View." + view.name).Assembly.CreateInstance("MVC.View." + view.name);
                    panel.Init();
                    //Logging.HYLDDebug.LogError(Type.GetType("MVC.View." + view.name).Name);
                    recycleDic.Add(Type.GetType("MVC.View." + view.name).Name, panel);

                    if (view.name == "MainPanel")
                    {
                        panel.Open();
                    }
                }
            }
            */
        }

        /*
        private void OpenXuanshang()
        {
            Xuanshang.SetActive(true);
        }
        private void CloseXuanshang()
        {
            Xuanshang.SetActive(false);
        }
        */
        public override void Open(string panel)
        {
            if (recycleDic[panel].Open())
            _panelStack.Push(panel);

            //print(_panelStack.Count);
        }
        public override void Close()
        {
            base.Close();
            //print(_panelStack.Count);
            if (_panelStack.Count!=0)
            {
                string panel=_panelStack.Pop();
                recycleDic[panel].Close();
                recycleDic[_panelStack.Peek()].OnRecovery();
            }
        }
        public override void StartGame(string name)
        {
            //TapTapStaticValue.NextSence = name;
            if (PlayerPrefs.GetInt(PlayerPrefabsEnum.isFirst.ToString(), 0) == 0)
            {
                PlayerPrefs.SetInt(PlayerPrefabsEnum.isFirst.ToString(), 1);
                UnityEngine.SceneManagement.SceneManager.LoadScene("NewTestGame");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("LodingSence");
            //龙之介场景切换工具.SetActive(true);
        }
        /*
        public override void ChangeGun()
        {
            base.ChangeGun();
            MVC.View.PreGameWindow prewindows=(MVC.View.PreGameWindow)recycleDic["PreGameWindow"];
            prewindows.ChangeGun();
        }
        */
    }
}