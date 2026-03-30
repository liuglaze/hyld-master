/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/20 15:5:19
    Description:     UI面板基类，拥有开启关闭等功能
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using SocketProto;
using Server;

namespace MVC
{
    /// <summary>
    /// 面板基类
    /// </summary>
    public class UIbasePanel : MonoBehaviour
    {
        protected List<BaseRequest> Requests=new List<BaseRequest>();
        //窗体类型
        protected WindowType selfType;
        //场景类型
        protected ScenesType scenesType;
        //UI控件
        protected Button[] buttonList;
        
        protected Text[] textList;

        public bool IsOpen
        {
            get{ return transform.gameObject.activeSelf;}
        }

        /// <summary>
        /// 初始化
        /// </summary>
        public virtual void Init()
        {
            //参数为true表示包括隐藏的物体
            buttonList = transform.GetComponentsInChildren<Button>(true);
            textList = transform.GetComponentsInChildren<Text>(true);

            //注册UI事件（细节由子类实现）
            RegisterUIEvent();
        }
        /// <summary>
        /// UI事件的注册
        /// </summary>
        protected virtual void RegisterUIEvent()
        {
            foreach (Button btn in buttonList)
            {
                switch (btn.name)
                {
                    case "btnClose":
                        btn.onClick.AddListener(() =>
                        {
                            HYLDManger.Instance.UIBaseManger.Close();
                        });
                        break;
                }
            }
        }
        /// <summary>
        /// 恢复UI
        /// </summary>
        public virtual void OnRecovery() { }
        /// <summary>
        /// 隐藏UI
        /// </summary>
        protected virtual void OnHide() { }

        /// <summary>
        /// 退出UI
        /// </summary>
        public virtual void OnExit()
        {

        }
        /// <summary>
        /// 处理请求回调
        /// </summary>
        /// <param name="pack"></param>
        public virtual void OnResponse(MainPack pack)
        {
            //Logging.HYLDDebug.LogError(pack);
        }
        //每帧更新
        public virtual void Excute(float deltaTime) 
        {
            if (Requests.Count != 0)
                foreach (BaseRequest baseRequest in Requests)
                {
                    //9.处理消息队列
                    baseRequest.Update();
                }

        }

        //-----------针对WindowManager的方法 (被WindowManager调用)
        public bool Open()
        {
            if (!IsOpen)
            {
                UIRoot.SetParent(transform, true, selfType == WindowType.TipsWindow);
                transform.gameObject.SetActive(true);
                OnRecovery(); //调用激活时的事件
                return true;
            }
            return false;
        }
        public void Close(bool isForceClose = false)
        {
            if (IsOpen)
            {
                OnHide();  //隐藏的事件
                if (!isForceClose)  //非强制
                {
                    transform.gameObject.SetActive(false);
                    //将窗口从work区域放到recycle区域
                    UIRoot.SetParent(transform, false, false);
                }
                else
                {
                    Destroy(transform.gameObject);
                }
            }
        }

        //获取场景类型
        public ScenesType GetScenesType()
        {
            return scenesType;
        }
        //获取窗口类型
        public WindowType GetWindowType()
        {
            return selfType;
        }
    }
}