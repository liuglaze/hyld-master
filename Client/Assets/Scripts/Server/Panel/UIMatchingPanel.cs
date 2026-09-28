/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/18 15:50:55
    Description:     Nothing
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
    /// 匹配面板「入局后不再遮住正式地图」的渲染抑制动作（T-VIS1）。
    ///
    /// 这里只描述**决策**，不引用 UnityEngine：面板与静态门禁（G9）都以本枚举为唯一动作源。
    /// </summary>
    internal enum PMMatchingPanelRenderAction
    {
        /// <summary>本帧不改动任何渲染状态。</summary>
        None = 0,

        /// <summary>记录所属 Canvas 的原始 enabled 值，并停用它的渲染。</summary>
        Suppress = 1,

        /// <summary>按记录到的原始值恢复所属 Canvas 的渲染。</summary>
        Restore = 2,
    }

    /// <summary>
    /// 匹配面板渲染抑制的**引擎无关纯策略**（唯一决策源；静态门禁 G9 直接核对本类的三条规则）。
    ///
    /// 背景（T-VIS1 冻结事实）：退役前 UIMatchingPanel 收到 PMDS1 入局通知后只做
    /// <c>ExitMathcing.SetActive(false)</c>（隐藏一个子按钮），全屏匹配 Canvas 仍然遮住
    /// 刚加载出来的正式地图与角色；而且那个子按钮被永久隐藏，第二局再也出不来「退出匹配」。
    ///
    /// 所以本策略只做两件事，且两件都可以被静态核对：
    ///   1. 入局成功 → 只停用**所属 Canvas 组件**的渲染。不停用面板 GameObject：
    ///      本面板的 Update/FixedUpdate 与宿主（PMClientSessionHostDriver）的事件更新必须继续跑，
    ///      否则连「会话结束」这件事都没有执行者来观察；
    ///   2. 会话结束（正常终局 / 会话故障 / 显式 Stop）→ 按进入时记录的**原始** enabled 值恢复。
    /// </summary>
    internal static class PMMatchingPanelRenderPolicy
    {
        /// <summary>
        /// 决策表（唯一决策源）：
        ///   suppressed=false, hostActive=true  → Suppress（入局成功，停用渲染）
        ///   suppressed=true,  hostActive=false → Restore（会话结束/故障/显式 Stop，恢复原值）
        ///   其它                                → None（含「已抑制时又收到重复入局通知」）
        /// </summary>
        public static PMMatchingPanelRenderAction Decide(bool hostActive, bool suppressed)
        {
            if (!hostActive)
            {
                return suppressed ? PMMatchingPanelRenderAction.Restore : PMMatchingPanelRenderAction.None;
            }

            return suppressed ? PMMatchingPanelRenderAction.None : PMMatchingPanelRenderAction.Suppress;
        }

        /// <summary>
        /// 原始 enabled 值**只在第一次**抑制时记录：重复入局通知（宿主会忽略重复 Enter）不得用
        /// 「当前已被抑制的 false」覆盖原始值，否则恢复后大厅 UI 永远回不来。
        /// </summary>
        public static bool SnapshotMustBeRecorded(bool suppressed)
        {
            return !suppressed;
        }

        /// <summary>
        /// 恢复必须回放进入时记录的原始值，绝不能用字面量 true 覆盖：原始值可能本来就是 false，
        /// 无条件点亮等于越权改写大厅 UI 状态。
        /// </summary>
        public static bool RestoreEnabledValue(bool recordedEnabled)
        {
            return recordedEnabled;
        }
    }


    /// <summary>
    /// T-VIS1b：「会话结束后**栈安全地**关掉匹配面板、回主菜单」的引擎无关纯策略。
    ///
    /// 与渲染策略**分工不同**：渲染策略只回答「Canvas 该不该亮」；本策略只回答
    /// 「要不要动旧 UI 栈、什么时候允许尝试」。真正怎么关由
    /// <c>LongZhiJie.StartUIManger.CloseIfTop</c> 负责（它自己再做栈顶 / 两层 / 注册表三重校验）。
    /// </summary>
    internal static class PMMatchingPanelExitPolicy
    {
        /// <summary>
        /// 只有「本帧确实把本面板压制过的 Canvas 按原值还回去了」（= 本局真的由本面板入过局）
        /// 才登记退场。从未入局（还在匹配中）绝不允许自动关面板。
        /// </summary>
        public static bool ShouldEnterReturnToMain(bool canvasRestoredThisFrame)
        {
            return canvasRestoredThisFrame;
        }

        /// <summary>
        /// 允许尝试安全关闭的条件：会话已经不在进行中（正常终局 / 会话故障 / 显式 Stop）+ 已登记退场。
        /// 会话仍在进行中一律不碰 UI 栈（避免第二轮入局时把面板关掉）。
        /// </summary>
        public static bool ShouldAttemptReturnToMain(bool hostActive, bool pendingReturnToMain)
        {
            return !hostActive && pendingReturnToMain;
        }

        /// <summary>
        /// 拿不到可用的旧 UI 栈管理器（不是 StartUIManger / 已被销毁 / UIRoot 还没初始化）时放弃登记：
        /// 这是**结构不可用**，不是栈状态暂时不满足，重试不会变好，只告警一次。
        /// </summary>
        public static bool ShouldGiveUpReturnToMain(bool stackManagerAvailable)
        {
            return !stackManagerAvailable;
        }

        /// <summary>
        /// 被 <c>CloseIfTop</c> 拒绝（栈顶不是本面板 / 栈里不足两层 / 注册表条目不可用）时继续等待：
        /// 保留登记、后续帧再试，但**本帧绝不自己弹栈、绝不改任何栈状态**。
        /// </summary>
        public static bool ShouldKeepWaitingForReturnToMain(bool closed)
        {
            return !closed;
        }
    }

	public class UIMatchingPanel : UIbasePanel
    {
        public Transform Star;

        /// <summary>
        /// 「退出匹配」子按钮（场景里序列化引用的 btnExitMatching）。
        ///
        /// T-VIS1 起本面板**不再隐藏它**：入局后遮住地图的是所属 Canvas 的渲染，只停用 Canvas 就够了；
        /// 保留该字段是为了不破坏场景（HYLDStart.unity）里的序列化引用，同时显式记录
        /// 「不再只隐藏按钮」这一行为变更（旧实现隐藏它以后，第二局再也出不来退出匹配）。
        /// </summary>
        public GameObject ExitMathcing;

        /// <summary>
        /// 本面板的**所属 Canvas**（最近的祖先 Canvas）。HYLDStart 场景里 UIMatchingPanel 位于
        /// <c>Canvas/recyclePool</c>（打开时被移到 <c>Canvas/workstationPool</c>），而该场景只有
        /// 一个 Canvas 组件，所以「所属 Canvas」就是用户实测里关掉后能看到正式地图的那个根 Canvas。
        /// </summary>
        private Canvas _owningCanvas;

        /// <summary>入局时所属 Canvas 的**原始** enabled 值（恢复的唯一依据）。</summary>
        private bool _owningCanvasWasEnabled;

        /// <summary>本面板当前是否停用了所属 Canvas 的渲染（幂等状态位）。</summary>
        private bool _renderSuppressed;

        /// <summary>「找不到所属 Canvas」只告警一次，避免每帧刷日志。</summary>
        private bool _canvasResolveWarned;

        /// <summary>T-VIS1b：本局退场登记（会话结束 → 请旧 UI 栈安全关掉本面板、回主菜单）。</summary>
        private bool _pendingReturnToMain;

        /// <summary>「拿不到可用的旧 UI 栈管理器」只告警一次。</summary>
        private bool _returnToMainWarned;

        /// <summary>「旧 UI 栈拒绝关闭本面板」只告警一次（拒绝原因是栈状态，允许后续帧再试）。</summary>
        private bool _safeCloseRefusedWarned;

        public override void Init()
        {
            base.Init();

            // T-LOOP6：先在**主线程**把工程既有的主线程泵（NetGlobal/ActionManger）建起来。
            //
            // 为什么必须在这里：入局/续局通知是在大厅 TCP 的异步收包线程上到达的，而“打开 UI”
            // 只能主线程做；RequestManger 靠 NetGlobal 把这两步搬回主线程。
            // 但 NetGlobal.Instance 是**懒创建**（首次访问会 new GameObject + DontDestroyOnLoad），
            // 只有主线程能建。若第一次访问发生在收包线程，创建会直接抛异常，入局通知就没法搬回主线程。
            // 面板 Init 发生在场景加载期、远早于任何登录/入局通知，是最早可靠的主线程锚点。
            // 只做“把已存在的泵建起来”这一件事：不新建自定义宿主、不改 UI 栈、不碰面板 GameObject。
            try
            {
                NetGlobal.Instance.Init();
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogWarning("[TLOOP6] 预建主线程泵（NetGlobal）失败："
                                             + ex.GetType().Name + " " + ex.Message);
            }

           // Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.AddMatchingPlayer));
            Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.StartEnterBattle));
        }

        /// <summary>
        /// T-LOOP6：把本面板**安全打开**，让大厅在登录成功后主动推来的入局/续局通知（<c>PMDS1:</c>/
        /// <c>PMDSR1:</c>）有一个**在跑**的接收者。
        ///
        /// 为何不能省这一步：入局通知进入的是本面板的 <c>BaseRequest</c> 队列，而该队列只在面板
        /// <see cref="UIbasePanel.IsOpen"/> 为真时由 <c>StartUIManger.Excute → Excute → BaseRequest.Update</c>
        /// 抽干；面板关闭时排进去的包永远不会被处理。所以“先打开、再投递”是通知链的必要前提。
        ///
        /// 只做打开（不自行弹栈、不新建管理器、不改面板 GameObject）：已打开则原样返回 true；
        /// 拿不到可用管理器/面板未注册/对象已销毁一律返回 false + 不含秘密的原因，
        /// 由调用方 fail closed（不投递、不回旧链）。
        /// </summary>
        internal static bool TryEnsureOpenForEntryNotice(out string error)
        {
            error = null;

            try
            {
                HYLDManger manger = HYLDManger.Instance;
                if (manger == null)
                {
                    error = "大厅管理器不可用（HYLDManger.Instance 为空或尚未初始化）";
                    return false;
                }

                LongZhiJie.UIBaseManger ui = manger.UIBaseManger;
                if (ui == null)
                {
                    error = "UI 管理器不可用（尚未初始化或尚未加载场景 UI）";
                    return false;
                }

                if (ui.IsOpen(nameof(UIMatchingPanel)))
                {
                    return true; // 已打开：不动 UI 栈
                }

                ui.Open(nameof(UIMatchingPanel));

                if (!ui.IsOpen(nameof(UIMatchingPanel)))
                {
                    error = "UI 管理器拒绝了打开匹配面板（注册表/栈未就绪）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // 不回显原文本/栈细节：这里只需要“能不能打开”这一个事实。
                error = "打开匹配面板异常：" + ex.GetType().Name;
                return false;
            }
        }

        private void Update()
        {
            // 会话结束（正常终局 / 故障 / 显式 Stop）没有事件可订阅，只能用宿主公开的只读状态判定；
            // Canvas.enabled=false 不影响 MonoBehaviour 执行，所以这条轮询在抑制期间照常运行。
            PumpRenderLifecycle();
        }

        private void FixedUpdate()
        {
            Star.Rotate(new Vector3(0, 0, 1), 1f);
        }

        private void OnDisable()
        {
            // 安全网：面板一旦被停用（旧 UI 栈 Close、场景切换、宿主失联收尾），本类的 Update 就
            // 不再执行。若不在这里恢复，所属 Canvas 会永久停在地图可见的状态（大厅 UI 整块消失且无法自愈）。
            RestoreOwnCanvasRender("面板已停用");
        }

        private void OnDestroy()
        {
            // 对象销毁恢复：面板被销毁而所属 Canvas 还活着时，必须把渲染还给大厅。
            RestoreOwnCanvasRender("面板已销毁");
        }

        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);

            // R3-B（契约 §7.1 / §7.4）：入局通知的识别必须**最先**做，并且
            // 一旦识别到 PMDS1 前缀就不允许再走旧链：
            //   解码失败 ⇒ 明确报错返回（绝不回退 BattleData / ClearSence，否则会生成第二套入局状态）；
            //   解码成功 ⇒ 交新链客户端宿主，直接 return。
            // 注意只打 offer 的 ToString()（只含公开字段与票据长度），**不打印完整 Str**。
            if (pack.Returncode == ReturnCode.Succeed && PMNet.Session.PMDsEntryCodec.HasPrefix(pack.Str))
            {
                PMNet.Session.PMDsEntryOffer offer;
                string decodeError;
                if (!PMNet.Session.PMDsEntryCodec.TryDecode(pack.Str, out offer, out decodeError))
                {
                    Logging.HYLDDebug.LogError("[R3B] 新链入局通知解码失败，拒绝回退旧链：" + decodeError);
                    return;
                }

                Logging.HYLDDebug.Log("[R3B] 收到新链入局通知（" + (offer.IsResume ? "续局 PMDSR1" : "初始 PMDS1") + "）：" + offer);
                PMNet.Unity.PMClientSessionHost.Enter(offer);

                // T-VIS1：入局成功当帧就按同一策略判定一次，避免匹配界面在刚加载出来的正式地图上多闪一帧。
                // 只停用所属 Canvas 的渲染：面板 GameObject 保持激活，本类的 Update/FixedUpdate 与
                // 宿主事件更新都继续跑。入局失败（Enter 内部拒绝）时 IsActive 为 false，这里什么都不改。
                PumpRenderLifecycle();
                return;
            }

            if (pack.Returncode == ReturnCode.Succeed)            
            {
                // 旧链退役（契约 §B）：原这里是旧房间入局的唯一回退落点
                // （Manger.BattleData.InitBattleInfo + ClearSenceManger.LoadScene 旧战场）。
                // 旧战斗链删除后，非 PMDS1 的旧成功通知**没有任何接收者**：
                // 必须显式报错，绝不能静默回退旧战场去生成第二套入局状态。
                Logging.HYLDDebug.LogError("[R3B] 收到旧链（非 PMDS1:/PMDSR1:）入局成功通知；旧战斗链已退役，"
                                           + "只接受 PMDS1:/PMDSR1: 前缀的入局通知，已明确拒绝");
            }
        }

        /// <summary>
        /// 用唯一策略判定「抑制 / 恢复 / 不动」并执行（幂等；入局当帧与每帧 Update 都调它）。
        /// </summary>
        private void PumpRenderLifecycle()
        {
            bool hostActive = PMNet.Unity.PMClientSessionHost.IsActive;

            PMMatchingPanelRenderAction action =
                PMMatchingPanelRenderPolicy.Decide(hostActive, _renderSuppressed);

            if (action == PMMatchingPanelRenderAction.Suppress)
            {
                SuppressOwnCanvasRender();
                return;
            }

            if (action == PMMatchingPanelRenderAction.Restore)
            {
                bool canvasRestored =
                    RestoreOwnCanvasRender("新链会话已结束（正常终局 / 会话故障 / 显式 Stop）");

                // T-VIS1b：只有「本帧确实把本面板压制过的 Canvas 还回去了」才登记退场 ——
                // 也就是本局真的由本面板入过局；从未入局（还在匹配中）绝不允许自动关面板。
                if (PMMatchingPanelExitPolicy.ShouldEnterReturnToMain(canvasRestored))
                {
                    _pendingReturnToMain = true;
                }
            }

            // T-VIS1b：先把 Canvas 渲染还回去，再谈退场；会话仍在进行中时这一支永远不成立。
            if (PMMatchingPanelExitPolicy.ShouldAttemptReturnToMain(hostActive, _pendingReturnToMain))
            {
                ReturnToMainMenuIfSafe();
            }
        }

        /// <summary>
        /// 记录所属 Canvas 的原始 enabled 值并停用其渲染。
        ///
        /// 只改 **Canvas 组件**：不改面板/子物体的 activeSelf、不动旧 UI 栈、不新建任何全局单例。
        /// </summary>
        private void SuppressOwnCanvasRender()
        {
            if (!PMMatchingPanelRenderPolicy.SnapshotMustBeRecorded(_renderSuppressed))
            {
                return; // 已抑制（重复入局通知）：绝不覆盖原始值
            }

            Canvas canvas = ResolveOwningCanvas();
            if (canvas == null)
            {
                if (!_canvasResolveWarned)
                {
                    _canvasResolveWarned = true;
                    Logging.HYLDDebug.LogWarning("[TVIS1] 未找到 UIMatchingPanel 的所属 Canvas，本局无法只停用 Canvas 渲染"
                                                 + "（匹配界面仍会遮住地图）；不伪造已抑制状态，后续帧继续尝试");
                }

                return;
            }

            _owningCanvasWasEnabled = canvas.enabled;

            try
            {
                canvas.enabled = false;
            }
            catch (Exception ex)
            {
                // 赋值抛异常（Canvas 已被销毁等）时不得假装已抑制：否则恢复阶段会去改一个不存在的对象。
                Logging.HYLDDebug.LogError("[TVIS1] 停用所属 Canvas 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            _renderSuppressed = true;
            _canvasResolveWarned = false;
            Logging.HYLDDebug.Log("[TVIS1] 入局成功：已只停用所属 Canvas 的渲染（原 enabled="
                                  + (_owningCanvasWasEnabled ? "true" : "false")
                                  + "；面板 GameObject 与宿主事件更新继续运行，会话结束后按原值恢复）");
        }

        /// <summary>
        /// 按进入时记录的原始值恢复所属 Canvas 的渲染（幂等；未抑制过则什么都不做）。
        ///
        /// 恢复值必须来自记录：<see cref="PMMatchingPanelRenderPolicy.RestoreEnabledValue"/>。
        /// 返回值只在**确实按记录把渲染还回去了**时为 true：OnDisable / OnDestroy 兜底调用忽略它，
        /// T-VIS1b 的退场登记只认 true（未入局 / Canvas 已随场景销毁 / 赋值抛异常都不算本局入过局）。
        /// </summary>
        private bool RestoreOwnCanvasRender(string reason)
        {
            if (!_renderSuppressed)
            {
                return false; // 未抑制过：不得改写大厅 Canvas 的任何状态
            }

            _renderSuppressed = false;
            _canvasResolveWarned = false;

            Canvas canvas = ResolveOwningCanvas();
            if (canvas == null)
            {
                // 面板与 Canvas 一起被销毁（场景切换等）：渲染载体已不存在，没有可恢复的对象。
                Logging.HYLDDebug.LogWarning("[TVIS1] 恢复所属 Canvas 渲染时已找不到所属 Canvas（" + reason + "）");
                return false;
            }

            try
            {
                canvas.enabled = PMMatchingPanelRenderPolicy.RestoreEnabledValue(_owningCanvasWasEnabled);
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError("[TVIS1] 恢复所属 Canvas 渲染失败：" + ex.GetType().Name + " " + ex.Message
                                           + "（原因：" + reason + "）");
                return false;
            }

            Logging.HYLDDebug.Log("[TVIS1] 已恢复所属 Canvas 渲染（" + reason + "；enabled="
                                  + (_owningCanvasWasEnabled ? "true" : "false")
                                  + "；退出匹配按钮随面板原样回到可点状态）");
            return true;
        }

        /// <summary>
        /// T-VIS1b：请旧 UI 框架**栈安全地**关掉本匹配面板、回主菜单。
        ///
        /// 只走显式新 API <c>LongZhiJie.StartUIManger.CloseIfTop</c>：它要求「栈顶就是本面板 +
        /// 栈里至少两层 + 注册表条目就是当前面板」三条同时成立才关闭弹栈，所以既不会误关别的面板，
        /// 也不会在单元素栈上 Peek 空栈。面板自己的 <c>UIbasePanel.Close()</c>（不动栈，会留下脏条目）
        /// 与无参 <c>UIBaseManger.Close()</c>（无脑弹栈顶）本方法一律不调。
        ///
        /// 被拒绝时**不改任何栈状态**、不自己弹栈，只保留登记等条件成立（后续帧再试）；
        /// 拿不到可用管理器就放弃登记（结构不可用，重试无意义）。
        /// </summary>
        private void ReturnToMainMenuIfSafe()
        {
            LongZhiJie.StartUIManger stackManager = ResolveStackManager();

            if (PMMatchingPanelExitPolicy.ShouldGiveUpReturnToMain(stackManager != null))
            {
                if (!_returnToMainWarned)
                {
                    _returnToMainWarned = true;
                    Logging.HYLDDebug.LogWarning("[TVIS1b] 找不到可用的旧 UI 栈管理器（StartUIManger）："
                                                 + "本轮不自动退场、不新建 manager、不改 UI 栈");
                }

                ClearReturnToMain();
                return;
            }

            bool closed = false;
            try
            {
                closed = stackManager.CloseIfTop(nameof(UIMatchingPanel));
            }
            catch (Exception ex)
            {
                // CloseIfTop 自身 fail-closed（异常时栈未改动）；这一层只保证异常不会冒到 Update 里刷屏。
                Logging.HYLDDebug.LogError("[TVIS1b] 调用 CloseIfTop 异常（UI 栈未改动）："
                                           + ex.GetType().Name + " " + ex.Message);
            }

            if (PMMatchingPanelExitPolicy.ShouldKeepWaitingForReturnToMain(closed))
            {
                if (!_safeCloseRefusedWarned)
                {
                    _safeCloseRefusedWarned = true;
                    Logging.HYLDDebug.LogWarning("[TVIS1b] 旧 UI 栈拒绝关闭匹配面板"
                                                 + "（栈顶校验 / 两层校验 / 注册表校验未通过）：本轮不改栈，等条件成立再试");
                }

                return;
            }

            Logging.HYLDDebug.Log("[TVIS1b] 已按栈安全条件关闭匹配面板并回到主菜单");
            ClearReturnToMain();
        }

        /// <summary>
        /// 取旧 UI 栈管理器：<see cref="UIRoot.UIManger"/> 是既有公开入口（<c>UIbasePanel.Open/Close</c>
        /// 已经在用 UIRoot），这里**只读引用**、不新建任何对象；不是 StartUIManger（别的场景 / 别的管理器）
        /// 或已被销毁时返回 null，由调用方 fail-closed。
        /// </summary>
        private LongZhiJie.StartUIManger ResolveStackManager()
        {
            try
            {
                LongZhiJie.UIBaseManger manager = UIRoot.UIManger;
                if (manager == null)
                {
                    return null; // Unity 的伪 null（对象已销毁）也走这一支
                }

                return manager as LongZhiJie.StartUIManger;
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError("[TVIS1b] 解析旧 UI 栈管理器异常："
                                           + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 消费本局退场登记：只有「确实关成功了」或「结构上不可能关」才允许清。
        /// 清掉之后必须重新入局（再次压制 Canvas）才会重新登记，重复局因此不会被上一轮的登记误关。
        /// </summary>
        private void ClearReturnToMain()
        {
            _pendingReturnToMain = false;
            _returnToMainWarned = false;
            _safeCloseRefusedWarned = false;
        }

        /// <summary>
        /// 解析所属 Canvas：优先用缓存引用，引用已被销毁（Unity 的伪 null）时沿父链重新解析。
        ///
        /// 用显式父链遍历而不是 <c>GetComponentInParent&lt;Canvas&gt;()</c>：不依赖
        /// includeInactive 重载在 Unity 2019.4 上的可用性，也不受父物体激活状态影响。
        /// </summary>
        private Canvas ResolveOwningCanvas()
        {
            if (_owningCanvas != null)
            {
                return _owningCanvas;
            }

            try
            {
                Transform current = transform;
                while (current != null)
                {
                    Canvas canvas = current.GetComponent<Canvas>();
                    if (canvas != null)
                    {
                        _owningCanvas = canvas;
                        return canvas;
                    }

                    current = current.parent;
                }
            }
            catch (Exception ex)
            {
                Logging.HYLDDebug.LogError("[TVIS1] 解析所属 Canvas 异常：" + ex.GetType().Name + " " + ex.Message);
            }

            return null;
        }
    }
}
