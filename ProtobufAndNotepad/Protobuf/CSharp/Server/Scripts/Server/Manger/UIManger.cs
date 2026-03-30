/****************************************************
    Author:            龙之介
    CreatTime:    #CreateTime#
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace LongZhiJie
{
    public enum UIPanelType
    {
        Message,
        Start,
        Login,
        Logon,
        TalkingROOM,
        Room,
        Game,
        GameOver
    }
	public class UIManger :BaseManger
	{
        public UIManger(GameFace face) : base(face) { }
        private Transform canvasTransfrom;
        private Stack<BasePanel> _panelStack=new Stack<BasePanel>();
        private Dictionary<UIPanelType, BasePanel> _panelDic = new Dictionary<UIPanelType, BasePanel>();
        private Dictionary<UIPanelType, string> _panelPathDic = new Dictionary<UIPanelType, string>();

        private MessagePanel _messagePanel;
        public override void OnInit()
        {
            base.OnInit();
            InitPanel();
            canvasTransfrom = GameObject.Find("Canvas").transform;
            PushPanel(UIPanelType.Message);
            PushPanel(UIPanelType.Start);

        }

        //把UI显示在界面
        public void PushPanel(UIPanelType panelType)
        {
            if (_panelDic.TryGetValue(panelType, out BasePanel basePanel))
            {
                if (_panelStack.Count > 0)
                {
                    BasePanel toppanel = _panelStack.Peek();
                    toppanel.OnPause();
                }
                _panelStack.Push(basePanel);
                basePanel.OnEnter();
            }
            else 
            {
                BasePanel panel=SpawnPanel(panelType);
                if (_panelStack.Count > 0)
                {
                    BasePanel toppanel = _panelStack.Peek();
                    toppanel.OnPause();
                }
                _panelStack.Push(panel);
                panel.OnEnter();
            }
        }
        /// <summary>
        /// 关闭当前UI
        /// </summary>
        public  void PopPanel()
        {
            if (_panelStack.Count == 0) return;
            BasePanel toppanel = _panelStack.Pop();
            toppanel.OnExit();
            BasePanel panel = _panelStack.Peek();
            panel.OnRecovery();

        }
        public void ShowMessage(string str,bool sync=false)
        {
            _messagePanel.ShowMessage(str,sync);
        }

        /// <summary>
        /// 实例化UI
        /// </summary>
        /// <param name="panelType"></param>
        private BasePanel SpawnPanel(UIPanelType panelType)
        {
            if (_panelPathDic.TryGetValue(panelType, out string path))
            {
                GameObject g =GameObject.Instantiate(Resources.Load(path)) as GameObject;
                g.transform.SetParent(canvasTransfrom,false);
                BasePanel panel = g.GetComponent<BasePanel>();
                panel.SetUIManger(this);
                _panelDic.Add(panelType, panel);

                return panel;
            }
            else
            {
                return null;
            }
        }

        public void SetMeessagePanel(MessagePanel messagePanel)
        {
            _messagePanel = messagePanel;
        }

        /// <summary>
        /// 初始化UI
        /// </summary>
        private void InitPanel()
        {
            string panelpath = "Panel/";
            string[] path = new string[] { "LoginPanel" , "LogonPanel" , "MessagePanel", "StartPanel", "RoomPanel", "TalkingRoomPanel" };
            _panelPathDic.Add(UIPanelType.Login, panelpath+path[0]);
            _panelPathDic.Add(UIPanelType.Logon, panelpath + path[1]);
            _panelPathDic.Add(UIPanelType.Message, panelpath + path[2]);
            _panelPathDic.Add(UIPanelType.Start, panelpath + path[3]);
            _panelPathDic.Add(UIPanelType.Room, panelpath + path[4]);
            _panelPathDic.Add(UIPanelType.TalkingROOM, panelpath + path[5]);
        }
    }
}