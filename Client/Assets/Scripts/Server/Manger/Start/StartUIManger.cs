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

        /// <summary>
        /// T-VIS1b：显式「栈安全的匹配面板退场」API（只**新增**这一个入口，既有 Close() 语义一个字都不改）。
        ///
        /// 只有**四个条件全部成立**时才关闭面板并弹掉一条栈；任何一条不成立都返回 false 且**一个字节都不改**：
        ///   1. panel 非空，且 recycleDic 里确实注册了它、条目可用（Unity 伪 null / 已销毁一律按不可用处理）；
        ///   2. 注册条目确实是「当前面板」：它的 GameObject 名与 panel 相同（本框架的注册口径就是 GameObject 名）；
        ///   3. 栈里至少 2 条：弹出后必须还剩一层可以做 OnRecovery，**绝不 Peek 空栈**；
        ///   4. 栈顶**就是** panel：绝不误关别的面板（旧 Close() 无脑弹栈顶，正是本 API 要替代的路径）。
        ///
        /// 顺序保证：全部校验（含弹出后要恢复的下一层）都在 Pop 之前完成；Close() 抛异常时栈还没被动过，
        /// 因此不会留下「已经弹掉却没关成」的脏状态。调用方（UIMatchingPanel）只在本局真的入过局
        /// 且宿主已不在进行中时才调它。
        /// </summary>
        public bool CloseIfTop(string panel)
        {
            if (string.IsNullOrEmpty(panel))
            {
                return false;
            }

            MVC.UIbasePanel target;
            if (!recycleDic.TryGetValue(panel, out target) || target == null)
            {
                return false;
            }

            try
            {
                // 注册口径：recycleDic 的键就是面板 GameObject 名（StartInit / OnInit 里就是这么登记的）。
                if (target.gameObject.name != panel)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // 访问已销毁对象的 gameObject 会抛：一律按条目不可用处理。
                return false;
            }

            // 条件 3：至少两层。弹出后还要 Peek 下一层做 OnRecovery，单元素栈会让 Peek 抛异常。
            if (_panelStack.Count < 2)
            {
                return false;
            }

            // 条件 4：栈顶必须就是目标面板，绝不误关别的面板。
            if (_panelStack.Peek() != panel)
            {
                return false;
            }

            // Stack.ToArray() 是 LIFO 顺序：[0] 就是栈顶，[1] 是弹出后要做 OnRecovery 的下一层。
            string[] order = _panelStack.ToArray();
            if (order.Length < 2 || order[0] != panel)
            {
                return false;
            }

            MVC.UIbasePanel next;
            if (!recycleDic.TryGetValue(order[1], out next) || next == null)
            {
                return false;
            }

            // 到这里才真正改状态：上面任何一步失败都没有动过栈。
            try
            {
                target.Close(); // 沿旧框架语义：OnHide + SetActive(false) + 回 recyclePool（非强制销毁）
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError("[TVIS1b] 关闭匹配面板失败（UI 栈未改动）："
                                           + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            _panelStack.Pop(); // 上面已证明栈顶就是 panel，且校验期间没有任何东西改过栈

            try
            {
                next.OnRecovery();
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError("[TVIS1b] 弹栈后恢复下一层面板失败："
                                           + ex.GetType().Name + " " + ex.Message);
            }

            return true;
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