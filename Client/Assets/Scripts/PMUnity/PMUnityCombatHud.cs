// ============================================================================
//  PMUnityCombatHud —— R6-C / T-LOOP3 / T-LIVE2：客户端「正式直线攻击」的只读结算 HUD（薄表现）
// ============================================================================
//
//  契约来源：
//    · Docs/plans/net-r6-combat-contract.md「C 宿主接线冻结」末段：
//      「最小 PMUnityCombatHud 只读显示本人 HP/MaxHp/Mana/SuperEnergy、F/G 提示、
//        拒绝原因和胜负，不复用旧 HP 写入/旧 BattleReview」；
//    · Docs/plans/net-architecture-migration.md T-LOOP1「结算 UX 契约」（T-LOOP3 实施）：
//      「已通过 PMR6CombatDriver 可信 Result 且 PMClientSessionHost.OnTerminalResult 落地
//        才绘制居中胜负弹窗；基于被保留的 PMUnityCombatHud 可在对局及自动退场后继续显示，
//        GUI.Button『返回大厅』经会话绑定回调调用 **EndSessionNormally**，不调用会清结果的 Stop；
//        同帧先发 ServerCombatResultAck 才使按钮可点，重复点击/新会话旧回调必须无效。
//        自动 ≤7s 退场仍保留，退场后按钮标为已返回/只读结论，下次 Enter 或显式 Stop 清」。
//    · Docs/plans/net-architecture-migration.md T-LIVE2「返回大厅后整块结算 HUD 消失，可信结果内存仍留」
//      （**用户新口径，覆盖上面 T-LOOP1 末句的“退场后继续显示”**）：
//      「只在可信结局的 EndSessionNormally→SetReturnedToLobby 隐藏整个 HUD（OnGUI Box/Label/Button
//        全部零绘制、按钮解绑），对局结果刚到时仍居中可见/能点；自动 7s 与按钮路径同一结果，
//        双击与陈旧 token 不关新局，无结论不绘；在显示状态变化后不造额外结果或清权威。」
//
//  三条硬边界（本文件不得越过）
//  --------------------------
//  1) **只读**：所有显示值都来自宿主 setter 传入的快照（复制字段快照 / 驱动拒绝原因）；
//     本类**不读**旧 BattleData / HYLDManger / UIMatchingPanel / 旧 BattleReview，
//     **不写**任何资源（不扣血、不改蓝/能量、不写复制字段），也不做任何胜负/命中判定
//     —— 判定只在 DS 的 PMCombatSession。
//  2) **零资源编辑**：只用真实 Unity 2019 的 IMGUI（OnGUI + GUI.Box / GUI.Label / GUI.Button），
//     不新建 Canvas、不引用 Prefab、不创建 Material、不落盘/持久化任何资产。
//  3) **DS 禁止创建**：DS 是无头进程，没有可渲染界面；本 HUD 属于客户端接线。
//     <see cref="Create"/> 按 PMNet.PMNetRuntime.IsDedicatedServer 直接拒绝（返回 null，不抛）。
//
//  结算弹窗的两条**且仅这两条**可见路径（T-LOOP3 + T-LIVE2）
//  ----------------------------------------------------------
//  ① <see cref="SetResultPanel"/>(true, resultAckSent: true)：**可信终局结果已存**
//     （宿主 OnTerminalResult 已把结论推给本类）**且本帧终局 ACK 已发出** —— 此时按钮可点，
//     点击经 <see cref="BindLobbyReturn"/> 绑定的**会话身份令牌**回调走宿主的正常退场入口；
//  ② <see cref="SetReturnedToLobby"/>：会话已按**正常路径**退场 ⇒ **整个 HUD 隐藏**
//     （T-LIVE2 用户新口径，覆盖 T-LOOP3 的「弹窗保留只读可见」）：<c>OnGUI</c> 一帧都不画
//     （Box/Label/Button 全部零绘制），且把按钮入口与令牌**解绑**，因此双击/陈旧令牌
//     结构上不可能再回调宿主（更不可能关掉新会话）。
//
//  因此：只复制到 CombatMatchEnded、或不可信的断线/失败，**都不会**让本类绘制结算弹窗
//  （①要求有可信结论 + ACK 证据，②只有宿主正常退场路径才会调用）；Fail 路径不调用本类的
//  任何"显示结论"入口。返回大厅后**隐藏的是绘制**，不是结论：类内结果状态
//  （<see cref="_hasOutcome"/> 等）与宿主的 static 只读快照都继续保留到下一 Enter / 显式 Stop。
//
//  生命周期 / 所有权（「保留只读终局 HUD」的落地形式）
//  -------------------------------------------------------------------------
//  · Create 建一个 DontDestroyOnLoad 的宿主 GameObject + 本组件：它不属于任何业务场景，
//    因此隔离战斗场景（LocalPhysicsMode.Physics3D 的那张场景）卸载时不会被一起带走；
//  · 宿主（PMClientSessionHost）每帧用 setter 推值；显式 Stop / 下一 Enter 时调用 Dispose；
//  · 收到可信终局结果后，宿主只把结论推给本 HUD、**不** Dispose 它；进入下一 Enter / 显式 Stop
//    才真正销毁。这期间（T-LIVE2 用户新口径）：
//      - 对局终局刚到时：居中弹窗可见、按钮可点（正常结算）；
//      - 回到大厅后（EndSessionNormally→SetReturnedToLobby）：**整个 HUD 零绘制**，
//        左上角标与结算弹窗都不画、按钮解绑；只保留类内结果状态与宿主 static 只读快照。
//    —— 这是「保留只读结果」而不是「保留可见 UI」，全生命周期内最多存在一个本组件实例。
// ============================================================================

using System;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 终局结论的分类（**由宿主判定后传入**，本类不自行推断胜负）。
    ///
    /// 语义与 <c>PMCombatSession</c> 的 DS 终局口径一一对应：
    /// WinnerTeamId == 0 ⇒ 无唯一胜者（平/两端都不记录胜方）。
    /// </summary>
    public enum PMCombatHudOutcome
    {
        /// <summary>尚无终局结论（战斗进行中）。</summary>
        None = 0,

        /// <summary>本队获胜。</summary>
        Win = 1,

        /// <summary>本队落败。</summary>
        Lose = 2,

        /// <summary>无唯一胜者（winnerTeamId == 0）。</summary>
        Draw = 3,
    }

    /// <summary>
    /// 「返回大厅」回调（T-LOOP3）：参数是 <see cref="PMUnityCombatHud.BindLobbyReturn"/> 绑定的
    /// **会话身份令牌**；返回 true = 宿主已受理本次退场请求。
    ///
    /// 为什么带令牌而不是无参 Action：本 HUD 在终局后会被**保留**（跨到大厅、可能跨到下一局），
    /// 一个陈旧绑定若只靠"当前是否在局内"判断，就可能把**新会话**关掉。宿主用令牌与当前会话
    /// 逐次比对，令牌不匹配一律拒绝（见 PMClientSessionHost.TryRequestReturnToLobbyFromHud）。
    /// </summary>
    /// <param name="sessionToken">绑定时登记的会话身份令牌（0 = 未绑定）。</param>
    /// <returns>true = 已受理并走出正常退场路径；false = 拒绝（陈旧令牌/无可信结论/未 ACK）。</returns>
    public delegate bool PMCombatHudLobbyReturnHandler(long sessionToken);

    /// <summary>
    /// 只读战斗 HUD：本地 HP/MaxHp/Mana/SuperEnergy、F/G 提示、最近拒绝原因、胜/负/平，
    /// 以及 T-LOOP3 的**居中醒目结算弹窗 + 返回大厅按钮**。
    ///
    /// 它不是完整 UI 迁移：没有摇杆、没有技能栏、没有旧英雄头像，只把「新链此刻的权威事实」
    /// 以最小代价显示出来，便于实机联调时一眼看出「谁掉了多少血、刚才那一枪为什么没出去」，
    /// 以及「本局赢了还是输了、怎么回大厅」。
    /// </summary>
    public sealed class PMUnityCombatHud : MonoBehaviour, IDisposable
    {
        /// <summary>左上角角标面板的 X（像素）。</summary>
        private const int PanelX = 8;

        /// <summary>左上角角标面板的 Y（像素）。</summary>
        private const int PanelY = 8;

        /// <summary>角标面板宽度（像素）。</summary>
        private const int PanelWidth = 360;

        /// <summary>面板内边距（像素）。</summary>
        private const int PanelPadding = 8;

        /// <summary>标题行高（像素）。</summary>
        private const int TitleHeight = 22;

        /// <summary>正文行高（像素）。</summary>
        private const int LineHeight = 17;

        /// <summary>拒绝原因的最大显示字符数（只读薄 HUD 不做换行排版，超出即截断）。</summary>
        private const int MaxNoticeChars = 96;

        /// <summary>宿主 GameObject 名（便于在 Hierarchy 里一眼定位/对账）。</summary>
        private const string HostName = "[PMUnityCombatHud]";

        // ---- T-LOOP3：居中结算弹窗的版面常量（像素；纯表现，不参与任何判定）----

        /// <summary>结算弹窗宽度（像素）。</summary>
        private const int ResultPanelWidth = 520;

        /// <summary>结算弹窗高度（像素）。</summary>
        private const int ResultPanelHeight = 280;

        /// <summary>结算弹窗内边距（像素）。</summary>
        private const int ResultPadding = 16;

        /// <summary>胜负大字距弹窗顶部的偏移（像素）。</summary>
        private const int ResultBigTop = 44;

        /// <summary>胜负大字高度（像素）。</summary>
        private const int ResultBigHeight = 96;

        /// <summary>胜负大字字号（醒目：远大于正文的 17px）。</summary>
        private const int ResultBigFontSize = 44;

        /// <summary>明细行高度（像素）。</summary>
        private const int ResultDetailHeight = 22;

        /// <summary>按钮宽度（像素）。</summary>
        private const int ResultButtonWidth = 220;

        /// <summary>按钮高度（像素）。</summary>
        private const int ResultButtonHeight = 52;

        /// <summary>按钮字号（像素）。</summary>
        private const int ResultButtonFontSize = 24;

        /// <summary>弹窗标题（GUI.Box 的标题文本；固定文案，不含任何玩家数据）。</summary>
        private const string ResultPanelTitle = "R6 Combat 结算";

        private bool _visible;
        private bool _disposed;

        private string _matchId = string.Empty;

        private int _hp;
        private int _maxHp;
        private int _mana;
        private int _superEnergy;
        private bool _ready;

        private string _notice = string.Empty;

        private bool _hasOutcome;
        private PMCombatHudOutcome _outcome = PMCombatHudOutcome.None;
        private int _winnerTeamId;
        private int _localTeamId;

        /// <summary>已渲染的角标正文（只在 setter 里重建，不在 OnGUI 里拼字符串）。</summary>
        private string _body = string.Empty;

        /// <summary>已渲染的正文行数（决定面板高度）。</summary>
        private int _bodyLines;

        // ---- T-LOOP3：结算弹窗与「返回大厅」会话绑定状态 ----

        /// <summary>居中结算弹窗是否可见（**只**由 SetResultPanel 置位；SetReturnedToLobby 一律置假）。</summary>
        private bool _resultPanelVisible;

        /// <summary>
        /// 是否已按正常路径返回大厅（T-LIVE2）。
        ///
        /// 为真 ⇒ **整个 HUD 零绘制**且「返回大厅」入口/令牌已解绑，但类内结果状态保留。
        /// 一经置真就不再由任何 setter 复位（只由 <see cref="Dispose"/> 清）。
        /// </summary>
        private bool _resultReturned;

        /// <summary>会话身份令牌（宿主 BindLobbyReturn 时传入；0 = 未绑定）。</summary>
        private long _sessionToken;

        /// <summary>宿主绑定的「返回大厅」入口（null = 未绑定，按钮不可点）。</summary>
        private PMCombatHudLobbyReturnHandler _lobbyReturnHandler;

        /// <summary>被宿主要求**提前**绘制弹窗（无可信结论或缺 ACK 证据）的次数（只增，诊断用）。</summary>
        private int _resultPanelPrematureRequests;

        /// <summary>宿主受理成功的「返回大厅」点击数（只增，诊断用）。</summary>
        private int _lobbyReturnAccepted;

        /// <summary>被拒（未绑定/已返回/宿主拒绝）的点击数（只增，诊断用）。</summary>
        private int _lobbyReturnRejected;

        /// <summary>回调抛异常的次数（只增；隔离订阅者异常，绝不让 HUD 崩）。</summary>
        private int _lobbyReturnExceptions;

        /// <summary>缓存的大字/按钮 GUIStyle（首次绘制时按 GUI.skin 建，之后复用）。</summary>
        private GUIStyle _bigStyle;
        private GUIStyle _buttonStyle;

        /// <summary>是否已释放（幂等；释放后不绘制）。</summary>
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>当前显示的对局 ID（未设置时为空串）。</summary>
        public string MatchId { get { return _matchId; } }

        /// <summary>居中结算弹窗是否可见（诊断/门禁对账用）。</summary>
        public bool IsResultPanelVisible { get { return _resultPanelVisible; } }

        /// <summary>
        /// 「返回大厅」按钮此刻是否可点：弹窗可见 **且** 未返回大厅 **且** 宿主已绑定入口。
        /// </summary>
        public bool IsLobbyReturnClickable
        {
            get { return _resultPanelVisible && !_resultReturned && _lobbyReturnHandler != null; }
        }

        /// <summary>是否已按正常路径返回大厅（其后整个 HUD 零绘制、按钮入口已解绑）。</summary>
        public bool IsReturnedToLobby { get { return _resultReturned; } }

        /// <summary>宿主绑定入口时登记的会话身份令牌（0 = 未绑定）。</summary>
        public long SessionToken { get { return _sessionToken; } }

        /// <summary>宿主是否已绑定「返回大厅」入口。</summary>
        public bool HasLobbyReturnHandler { get { return _lobbyReturnHandler != null; } }

        /// <summary>被宿主要求提前绘制弹窗（无可信结论或缺 ACK 证据）的次数。</summary>
        public int ResultPanelPrematureRequests { get { return _resultPanelPrematureRequests; } }

        /// <summary>宿主受理成功的返回大厅点击数。</summary>
        public int LobbyReturnAcceptedCount { get { return _lobbyReturnAccepted; } }

        /// <summary>被拒的返回大厅点击数。</summary>
        public int LobbyReturnRejectedCount { get { return _lobbyReturnRejected; } }

        /// <summary>返回大厅回调抛异常的次数。</summary>
        public int LobbyReturnExceptionCount { get { return _lobbyReturnExceptions; } }

        /// <summary>
        /// 创建一个只读 HUD。**失败一律返回 null 并给出原因**（不抛）：宿主据此把本次入局
        /// 显式失败并清理，而不是「静默地没有 HUD」。
        /// </summary>
        /// <param name="matchId">本局对局 ID（只用于显示与对账）。</param>
        /// <param name="error">失败原因（成功时为 null）。</param>
        public static PMUnityCombatHud Create(string matchId, out string error)
        {
            error = null;

            // 契约：DS 禁止创建（无头进程没有可渲染界面）。
            if (PMNet.PMNetRuntime.IsDedicatedServer)
            {
                error = "DS 禁止创建只读战斗 HUD";
                return null;
            }

            GameObject host = null;
            try
            {
                host = new GameObject(HostName);
                UnityEngine.Object.DontDestroyOnLoad(host);

                PMUnityCombatHud hud = host.AddComponent<PMUnityCombatHud>();
                if (hud == null)
                {
                    // 真实 Unity 不会走到这里；桩件/异常路径下必须自己清理，绝不留半个宿主对象。
                    UnityEngine.Object.Destroy(host);
                    error = "AddComponent<PMUnityCombatHud> 未返回组件";
                    return null;
                }

                hud._matchId = matchId == null ? string.Empty : matchId;
                hud._visible = true;
                hud.Rebuild();
                return hud;
            }
            catch (Exception ex)
            {
                // 「失败创建清理」：任何构造期异常都必须把宿主对象拆掉，不留孤儿 GameObject。
                if (host != null)
                {
                    try { UnityEngine.Object.Destroy(host); }
                    catch (Exception) { }
                }

                error = "创建只读战斗 HUD 异常：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        /// <summary>设置/更新对局 ID（幂等；空串合法）。</summary>
        public void SetMatchId(string matchId)
        {
            if (_disposed) { return; }

            string next = matchId == null ? string.Empty : matchId;
            if (string.Equals(_matchId, next, StringComparison.Ordinal)) { return; }

            _matchId = next;
            Rebuild();
        }

        /// <summary>本次入局 ID 快照。**只读显示**：不参与任何判定。</summary>
        public void SetLocal(int hp, int maxHp, int mana, int superEnergy)
        {
            if (_disposed) { return; }

            if (hp == _hp && maxHp == _maxHp && mana == _mana && superEnergy == _superEnergy) { return; }

            _hp = hp;
            _maxHp = maxHp;
            _mana = mana;
            _superEnergy = superEnergy;
            Rebuild();
        }

        /// <summary>
        /// 开火就绪提示（是否已收到可信 hero/team/MaxHp 复制且未死/未终局）。
        ///
        /// 这里**只显示**宿主给出的判据，本类不自行比较任何数值（避免 HUD 变成第二套权威）。
        /// </summary>
        public void SetReady(bool ready)
        {
            if (_disposed) { return; }
            if (ready == _ready) { return; }

            _ready = ready;
            Rebuild();
        }

        /// <summary>最近一次（本地 planner 或 DS 裁决）的拒绝原因；null/空串表示无。</summary>
        public void SetNotice(string notice)
        {
            if (_disposed) { return; }

            string next = notice == null ? string.Empty : notice;
            if (next.Length > MaxNoticeChars) { next = next.Substring(0, MaxNoticeChars) + "…"; }
            if (string.Equals(_notice, next, StringComparison.Ordinal)) { return; }

            _notice = next;
            Rebuild();
        }

        /// <summary>
        /// 终局结论（**由宿主按复制/可靠结果算出后传入**）。
        ///
        /// 一旦 <paramref name="hasOutcome"/> 为真就不再回到「进行中」：契约要求结论冻结，
        /// 客户端不得让后来到达的旧值把它改回去。
        ///
        /// 注意：本方法**只记录结论**，不显示居中弹窗 —— 弹窗的可见性由
        /// <see cref="SetResultPanel"/>（需 ACK 证据）与 <see cref="SetReturnedToLobby"/> 单独控制。
        /// </summary>
        public void SetOutcome(bool hasOutcome, PMCombatHudOutcome outcome, int winnerTeamId, int localTeamId)
        {
            if (_disposed) { return; }
            if (hasOutcome && _hasOutcome && _outcome == outcome && _winnerTeamId == winnerTeamId) { return; }

            _hasOutcome = hasOutcome;
            _outcome = hasOutcome ? outcome : PMCombatHudOutcome.None;
            _winnerTeamId = winnerTeamId;
            _localTeamId = localTeamId;

            // T-LOOP3：结论被**撤销**（正常不会发生：宿主在终局后不再下发无结论）时立即收回弹窗 ——
            // fail closed，绝不让一个「没有可信结论的结算弹窗」留在屏幕上。
            if (!_hasOutcome) { _resultPanelVisible = false; }

            Rebuild();
        }

        /// <summary>
        /// T-LOOP3：设置居中结算弹窗的可见性。**fail closed**：
        /// 只有「可信结论已存」**且** <paramref name="resultAckSent"/> 为真才会显示；
        /// 否则一律拒绝显示并计数（<see cref="ResultPanelPrematureRequests"/>）。
        ///
        /// 为什么要 ACK 证据：契约要求「同帧先发 ServerCombatResultAck 才使按钮可点」——
        /// 玩家一点「返回大厅」会话就正常退场，若此刻 ACK 还没出去，DS 只能等 5s 宽限才收局。
        /// 因此本类不接受"只看有没有结论"的放行。
        /// </summary>
        /// <param name="visible">宿主是否请求显示（false = 隐藏）。</param>
        /// <param name="resultAckSent">宿主观测到的「本帧终局 ACK 已发出」证据。</param>
        public void SetResultPanel(bool visible, bool resultAckSent)
        {
            if (_disposed) { return; }

            // T-LIVE2 fail closed：已按正常路径返回大厅 ⇒ 结算 UI 已整块隐藏，
            // 绝不允许宿主（或任何后续帧的驱动）把它重新点亮（返回后整个 HUD 零绘制）。
            if (_resultReturned) { return; }

            bool next = visible && _hasOutcome && resultAckSent;
            if (visible && !next) { _resultPanelPrematureRequests++; }

            if (next == _resultPanelVisible) { return; }

            _resultPanelVisible = next;
            Rebuild();
        }

        /// <summary>
        /// T-LIVE2：会话已按**正常路径**退场（<c>EndSessionNormally</c>：DS 正常退场 / 终局宽限到期 /
        /// 玩家点击返回大厅）—— 此时**整个 HUD 隐藏**：
        ///
        ///   · <see cref="OnGUI"/> 之后一帧都不画（左上角标与居中结算弹窗的 Box/Label/Button 全部零绘制）；
        ///   · 「返回大厅」入口与令牌**解绑** ⇒ 双击/陈旧令牌结构上不可能再回调宿主（不可能关掉新会话）；
        ///   · 但**不**改结论、不销毁自己、不请宿主清理：可借的只读结果快照
        ///     （类内状态 + 宿主 static）保留至下一 Enter / 显式 Stop。
        ///
        /// 自我守卫：类内没有可信结论（<see cref="_hasOutcome"/> 为假）时**什么都不做** ——
        /// 失败/断线路径永远不会因此隐藏 HUD，也不会伪造「已返回大厅」。
        /// </summary>
        public void SetReturnedToLobby()
        {
            if (_disposed) { return; }
            if (!_hasOutcome) { return; }

            _resultReturned = true;

            // T-LIVE2：收起结算 UI（用户新口径：返回后整块隐藏，而不是保留只读可见）。
            _resultPanelVisible = false;

            // T-LIVE2：解绑按钮入口与令牌 —— 返回后不存在任何点击路径（防双击/陈旧回调）。
            _lobbyReturnHandler = null;
            _sessionToken = 0L;

            Rebuild();
        }

        /// <summary>
        /// T-LOOP3：绑定「返回大厅」入口与会话身份令牌（未绑定时按钮不可点）。
        ///
        /// 令牌由宿主分配并在点击时原样回传给回调；宿主负责比对令牌与**当前**会话，
        /// 不匹配一律拒绝 —— 这是「跨局陈旧回调不关闭新会话」的落地形式。
        /// </summary>
        /// <param name="sessionToken">本次入局的会话身份令牌（0 = 未绑定）。</param>
        /// <param name="handler">宿主入口（null = 解绑）。</param>
        public void BindLobbyReturn(long sessionToken, PMCombatHudLobbyReturnHandler handler)
        {
            if (_disposed) { return; }

            // T-LIVE2：已按正常路径返回大厅 ⇒ 不再接受绑定——按钮已在返回时解绑，
            // 禁止事后把一个属于旧会话的入口/令牌重新装回去（点击路径保持结构上不存在）。
            if (_resultReturned) { return; }

            _sessionToken = sessionToken;
            _lobbyReturnHandler = handler;
            Rebuild();
        }

        /// <summary>解绑「返回大厅」入口（幂等；释放前的显式清理）。</summary>
        public void ClearLobbyReturn()
        {
            if (_disposed) { return; }

            _lobbyReturnHandler = null;
            _sessionToken = 0L;
            Rebuild();
        }

        /// <summary>
        /// 销毁宿主 GameObject 并清空显示值（幂等）。**只释放自己**：不碰任何会话/驱动/资产。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _visible = false;

            _body = string.Empty;
            _bodyLines = 0;
            _notice = string.Empty;

            // T-LOOP3：弹窗与按钮绑定一并清掉（下一 Enter / 显式 Stop 的清理点）。
            _resultPanelVisible = false;
            _resultReturned = false;
            _lobbyReturnHandler = null;
            _sessionToken = 0L;
            _bigStyle = null;
            _buttonStyle = null;

            GameObject host = null;
            try { host = gameObject; }
            catch (Exception) { }

            if (host != null)
            {
                try { UnityEngine.Object.Destroy(host); }
                catch (Exception) { }
            }
        }

        /// <summary>单行诊断（门禁/心跳对账用；不含任何敏感数据）。</summary>
        public string Describe()
        {
            return "hud(match=" + _matchId
                   + " hp=" + _hp + "/" + _maxHp
                   + " mana=" + _mana
                   + " energy=" + _superEnergy
                   + " ready=" + (_ready ? 1 : 0)
                   + " outcome=" + _outcome
                   + " winner=" + _winnerTeamId
                   + " team=" + _localTeamId
                   + " panel=" + (_resultPanelVisible ? 1 : 0)
                   + " returned=" + (_resultReturned ? 1 : 0)
                   + " clickable=" + (IsLobbyReturnClickable ? 1 : 0)
                   + " token=" + _sessionToken
                   + " clicks=" + _lobbyReturnAccepted
                   + " rejected=" + _lobbyReturnRejected
                   + " premature=" + _resultPanelPrematureRequests
                   + " disposed=" + (_disposed ? 1 : 0) + ")";
        }

        // =================================================================================
        //  渲染（真实 Unity 2019 IMGUI；零资源、零 Canvas）
        // =================================================================================

        /// <summary>
        /// 重建角标正文文本。**只在 setter 里调用**：OnGUI 一帧可能被调用多次（Layout/Repaint 等事件），
        /// 在 OnGUI 里拼字符串等于每帧多次无谓分配。
        /// </summary>
        private void Rebuild()
        {
            string readyText = _ready ? "就绪" : "未就绪";
            string noticeText = string.IsNullOrEmpty(_notice) ? "<无>" : _notice;

            _body = "HP      " + _hp + " / " + _maxHp + "\n"
                    + "Mana    " + _mana + "\n"
                    + "Energy  " + _superEnergy + "\n"
                    + "F = 普通攻击   G = 大招   [" + readyText + "]\n"
                    + "最近拒绝：" + noticeText + "\n"
                    + "结果：" + OutcomeText();

            _bodyLines = 6;
        }

        /// <summary>角标里终局一行的文案（胜/负/平 + **原始** winnerTeamId）。</summary>
        private string OutcomeText()
        {
            if (!_hasOutcome) { return "进行中（等待 DS 终局结果）"; }

            if (_outcome == PMCombatHudOutcome.Draw) { return "平（无唯一胜者，winnerTeamId=" + _winnerTeamId + "）"; }

            string label = _outcome == PMCombatHudOutcome.Win ? "胜" : "负";
            return label + "（winnerTeamId=" + _winnerTeamId + "，本队=" + _localTeamId + "）";
        }

        /// <summary>居中弹窗的胜负**大字**（醒目：44px 粗体居中）。</summary>
        private string OutcomeBigText()
        {
            if (!_hasOutcome) { return "结果未确认"; }
            if (_outcome == PMCombatHudOutcome.Win) { return "胜利"; }
            if (_outcome == PMCombatHudOutcome.Lose) { return "失败"; }
            if (_outcome == PMCombatHudOutcome.Draw) { return "平局"; }
            return "结果未确认";
        }

        /// <summary>
        /// 居中弹窗的明细行（原始 winnerTeamId / 本队 / 状态）。
        ///
        /// T-LIVE2：已返回大厅时本行**不会被绘制**（OnGUI 整块提前返回），“已返回大厅”
        /// 只作为纵深防御文案保留 —— 万一将来有人拆掉返回守卫，也只会看到不可点的只读文字，
        /// 绝不会重新出现一个可点的退场按钮。
        /// </summary>
        private string OutcomeDetailText()
        {
            if (!_hasOutcome) { return "<无可信结论>"; }
            if (_resultReturned) { return "已返回大厅（本局已结束）"; }

            return "winnerTeamId=" + _winnerTeamId + "   本队=" + _localTeamId
                   + "   点击『返回大厅』正常退场";
        }

        /// <summary>胜负大字的颜色（纯表现：绿=胜 / 红=负 / 黄=平）。</summary>
        private Color OutcomeColor()
        {
            if (_outcome == PMCombatHudOutcome.Win) { return new Color(0.35f, 1f, 0.35f); }
            if (_outcome == PMCombatHudOutcome.Lose) { return new Color(1f, 0.45f, 0.35f); }
            return new Color(1f, 0.9f, 0.35f);
        }

        private int PanelHeight()
        {
            return TitleHeight + _bodyLines * LineHeight + PanelPadding;
        }

        /// <summary>
        /// 胜负大字样式（首次绘制时按 <c>GUI.skin.label</c> 克隆一个 44px 粗体居中样式）。
        ///
        /// <c>GUI.skin</c> 在真实 Unity 里恒非空；这里仍保留 null 兜底（替身/极早期帧），
        /// 使本类在任何环境下都不会因为样式表缺失而抛异常。
        /// </summary>
        private GUIStyle BigOutcomeStyle()
        {
            if (_bigStyle == null)
            {
                GUISkin skin = GUI.skin;
                GUIStyle source = skin != null ? skin.label : null;
                _bigStyle = source != null ? new GUIStyle(source) : new GUIStyle();
                _bigStyle.fontSize = ResultBigFontSize;
                _bigStyle.fontStyle = FontStyle.Bold;
                _bigStyle.alignment = TextAnchor.MiddleCenter;
                _bigStyle.wordWrap = false;
            }

            // 颜色随胜/负/平变化：GUIStyleState 是可变对象（真实 Unity 语义），不重建样式。
            GUIStyleState state = _bigStyle.normal;
            if (state != null) { state.textColor = OutcomeColor(); }
            return _bigStyle;
        }

        /// <summary>按钮样式（按 <c>GUI.skin.button</c> 克隆一个 24px 居中样式；同样保留 null 兜底）。</summary>
        private GUIStyle ResultButtonStyle()
        {
            if (_buttonStyle == null)
            {
                GUISkin skin = GUI.skin;
                GUIStyle source = skin != null ? skin.button : null;
                _buttonStyle = source != null ? new GUIStyle(source) : new GUIStyle();
                _buttonStyle.fontSize = ResultButtonFontSize;
                _buttonStyle.fontStyle = FontStyle.Bold;
                _buttonStyle.alignment = TextAnchor.MiddleCenter;
            }

            return _buttonStyle;
        }

        /// <summary>
        /// 真实 Unity 2019 的 IMGUI 绘制入口。未就绪/已释放时什么都不画（不建 Canvas、不改 UI 树）。
        /// </summary>
        private void OnGUI()
        {
            if (_disposed || !_visible) { return; }

            // T-LIVE2：已按正常路径返回大厅 ⇒ **整个 HUD 零绘制**（左上角标与结算弹窗都不画）。
            // 这是「返回大厅后整个结算 UI 隐藏」的唯一落地形式（不在 DrawXxx 里逐处判断）。
            if (_resultReturned) { return; }

            DrawCornerPanel();

            // T-LOOP3：居中结算弹窗（可见性只由 SetResultPanel / SetReturnedToLobby 决定）。
            if (_resultPanelVisible) { DrawResultOverlay(); }
        }

        /// <summary>左上角只读角标（原 R6-C 面板：HP/Mana/Energy/F·G/拒绝原因/结果）。</summary>
        private void DrawCornerPanel()
        {
            int height = PanelHeight();

            GUI.Box(new Rect(PanelX, PanelY, PanelWidth, height), "R6 Combat  " + _matchId);
            GUI.Label(new Rect(PanelX + PanelPadding, PanelY + TitleHeight,
                               PanelWidth - PanelPadding * 2, height - TitleHeight),
                      _body);
        }

        /// <summary>
        /// 居中、醒目的胜负弹窗 + 「返回大厅」按钮（T-LOOP3）。
        ///
        /// 版面：屏幕正中 520×280 的面板 → 44px 粗体胜负大字 → 明细行 → 220×52 的按钮。
        /// 按钮可点时是真正的 <c>GUI.Button</c>；不可点（未绑定宿主入口 / 缺 ACK 证据 /
        /// 已返回大厅的纵深防御）时画成 <c>GUI.Box</c> 只读文字，**结构上不可能**误触退场。
        /// </summary>
        private void DrawResultOverlay()
        {
            // 屏幕尺寸兜底：桩件/极早期帧可能给 0 或非有限值（只影响版面，不改任何状态）。
            float screenWidth = Screen.width;
            float screenHeight = Screen.height;
            if (!(screenWidth > 1f)) { screenWidth = ResultPanelWidth; }
            if (!(screenHeight > 1f)) { screenHeight = ResultPanelHeight; }

            Rect panel = new Rect((screenWidth - ResultPanelWidth) * 0.5f,
                                  (screenHeight - ResultPanelHeight) * 0.5f,
                                  ResultPanelWidth, ResultPanelHeight);

            GUI.Box(panel, ResultPanelTitle);

            GUI.Label(new Rect(panel.x + ResultPadding, panel.y + ResultBigTop,
                               ResultPanelWidth - ResultPadding * 2, ResultBigHeight),
                      OutcomeBigText(), BigOutcomeStyle());

            GUI.Label(new Rect(panel.x + ResultPadding,
                               panel.y + ResultBigTop + ResultBigHeight + 8,
                               ResultPanelWidth - ResultPadding * 2, ResultDetailHeight),
                      OutcomeDetailText());

            Rect button = new Rect(panel.x + (ResultPanelWidth - ResultButtonWidth) * 0.5f,
                                   panel.y + ResultPanelHeight - ResultButtonHeight - 24,
                                   ResultButtonWidth, ResultButtonHeight);

            if (IsLobbyReturnClickable)
            {
                if (GUI.Button(button, "返回大厅", ResultButtonStyle())) { OnLobbyReturnClicked(); }
            }
            else
            {
                // 不可点：复用 Box 画只读文字（不需要 GUI.enabled，也不会有第二个点击路径）。
                GUI.Box(button, _resultReturned ? "已返回大厅" : "等待结果确认…");
            }
        }

        /// <summary>
        /// 「返回大厅」点击处理：把**会话身份令牌**原样回传给宿主入口，并按受理结果计数。
        ///
        /// 三条纪律：
        ///   · 已返回 / 未绑定入口时**不回调**（只计数）—— 重复点击不可能二次退场；
        ///   · 回调异常被隔离（HUD 绝不因宿主异常而崩，也不改任何结论）；
        ///   · 本类**不**自行清结论、不自行销毁：退场由宿主走 EndSessionNormally 决定。
        /// </summary>
        private void OnLobbyReturnClicked()
        {
            PMCombatHudLobbyReturnHandler handler = _lobbyReturnHandler;
            if (_resultReturned || handler == null)
            {
                _lobbyReturnRejected++;
                return;
            }

            bool accepted;
            try
            {
                accepted = handler(_sessionToken);
            }
            catch (Exception)
            {
                _lobbyReturnExceptions++;
                return;
            }

            if (accepted) { _lobbyReturnAccepted++; }
            else { _lobbyReturnRejected++; }
        }
    }
}
