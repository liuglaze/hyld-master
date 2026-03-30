/****************************************************
    Author:            龙之介
    CreatTime:    2021/2/11 2:38:20
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
	public class UIBaseManger : MonoBehaviour
    {
        [HideInInspector]
        public GameObject recyclePool;
        [HideInInspector]
        public GameObject workstationPool;
        [HideInInspector]
        public GameObject noticePool;
        protected MVC.UIMessagePanel messagePanel;
        public bool IsInit { get { return isInit; } }
        private bool isInit = false;
        public virtual void OnInit()
        {
            UIRoot.Init(transform, this);
            recyclePool = transform.Find("recyclePool").gameObject;
            workstationPool = transform.Find("workstationPool").gameObject;
            noticePool = transform.Find("noticePool").gameObject;
            foreach (Transform view in noticePool.transform)
            {
                MVC.UIbasePanel panel = view.GetComponent<MVC.UIbasePanel>();
                if (panel == null) continue;
                if (panel is MVC.UIMessagePanel)
                {
                    panel.Init();
                    messagePanel = panel as MVC.UIMessagePanel;
                }
            }
            isInit = true;
        }
        public virtual void StartGame(string name) { }
        public virtual void Excute(float deltaTime) { }
        public virtual void ChangeGun() { }

        public virtual void Open(string panel) { }
        public virtual bool IsOpen(string panel) { return true; }

        public virtual void Close() { }

        public virtual void ShowMessage(string str, bool sync = false)
        {
            messagePanel.ShowMessage(str, sync);
        }
        public virtual MVC.UIbasePanel GetPanel(string panel)
        {
            return null;
        }
    }
}