// ============================================================================
//  PMUnityCombatHud —— R6-C：客户端「正式直线攻击」的只读结算 HUD（薄表现）
// ============================================================================
//
//  契约来源：Docs/plans/net-r6-combat-contract.md「C 宿主接线冻结」末段：
//    · 「最小 PMUnityCombatHud 只读显示本人 HP/MaxHp/Mana/SuperEnergy、F/G 提示、
//       拒绝原因和胜负，不复用旧 HP 写入/旧 BattleReview」；
//    · 「结算 HUD 不是完整旧 UI 迁移，原英雄子弹美术/摇杆仍后置」。
//
//  三条硬边界（本文件不得越过）
//  --------------------------
//  1) **只读**：所有显示值都来自宿主 setter 传入的快照（复制字段快照 / 驱动拒绝原因）；
//     本类**不读**旧 BattleData / HYLDManger / UIMatchingPanel / 旧 BattleReview，
//     **不写**任何资源（不扣血、不改蓝/能量、不写复制字段），也不做任何胜负/命中判定
//     —— 判定只在 DS 的 PMCombatSession。
//  2) **零资源编辑**：只用真实 Unity 2019 的 IMGUI（OnGUI + GUI.Box / GUI.Label），
//     不新建 Canvas、不引用 Prefab、不创建 Material、不落盘/持久化任何资产。
//  3) **DS 禁止创建**：DS 是无头进程，没有可渲染界面；本 HUD 属于客户端接线。
//     <see cref="Create"/> 按 PMNet.PMNetRuntime.IsDedicatedServer 直接拒绝（返回 null，不抛）。
//
//  生命周期 / 所有权（「保留只读终局 HUD」的落地形式）
//  -------------------------------------------------------------------------
//  · Create 建一个 DontDestroyOnLoad 的宿主 GameObject + 本组件：它不属于任何业务场景，
//    因此隔离战斗场景（LocalPhysicsMode.Physics3D 的那张场景）卸载时不会被一起带走；
//  · 宿主（PMClientSessionHost）每帧用 setter 推值；显式 Stop / 下一 Enter 时调用 Dispose；
//  · 收到可信终局结果后，宿主只把结论推给本 HUD、**不** Dispose 它，于是「胜/负/平 +
//    winnerTeamId」会在回到原大厅后继续只读显示，直到下一次入局或显式 Stop
//    —— 这是「保留」而不是「新建」，全生命周期内最多存在一个本组件实例。
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
    /// 只读战斗 HUD：本地 HP/MaxHp/Mana/SuperEnergy、F/G 提示、最近拒绝原因、胜/负/平。
    ///
    /// 它不是完整 UI 迁移：没有摇杆、没有技能栏、没有旧英雄头像，只把「新链此刻的权威事实」
    /// 以最小代价显示出来，便于实机联调时一眼看出「谁掉了多少血、刚才那一枪为什么没出去」。
    /// </summary>
    public sealed class PMUnityCombatHud : MonoBehaviour, IDisposable
    {
        /// <summary>面板左上角 X（像素）。</summary>
        private const int PanelX = 8;

        /// <summary>面板左上角 Y（像素）。</summary>
        private const int PanelY = 8;

        /// <summary>面板宽度（像素）。</summary>
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

        /// <summary>已渲染的正文（只在 setter 里重建，不在 OnGUI 里拼字符串）。</summary>
        private string _body = string.Empty;

        /// <summary>已渲染的正文行数（决定面板高度）。</summary>
        private int _bodyLines;

        /// <summary>是否已释放（幂等；释放后不绘制）。</summary>
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>当前显示的对局 ID（未设置时为空串）。</summary>
        public string MatchId { get { return _matchId; } }

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
        /// </summary>
        public void SetOutcome(bool hasOutcome, PMCombatHudOutcome outcome, int winnerTeamId, int localTeamId)
        {
            if (_disposed) { return; }
            if (hasOutcome && _hasOutcome && _outcome == outcome && _winnerTeamId == winnerTeamId) { return; }

            _hasOutcome = hasOutcome;
            _outcome = hasOutcome ? outcome : PMCombatHudOutcome.None;
            _winnerTeamId = winnerTeamId;
            _localTeamId = localTeamId;
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
                   + " disposed=" + (_disposed ? 1 : 0) + ")";
        }

        // =================================================================================
        //  渲染（真实 Unity 2019 IMGUI；零资源、零 Canvas）
        // =================================================================================

        /// <summary>
        /// 重建正文文本。**只在 setter 里调用**：OnGUI 一帧可能被调用多次（Layout/Repaint 等事件），
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

        /// <summary>终局一行的文案（胜/负/平 + **原始** winnerTeamId）。</summary>
        private string OutcomeText()
        {
            if (!_hasOutcome) { return "进行中（等待 DS 终局结果）"; }

            if (_outcome == PMCombatHudOutcome.Draw) { return "平（无唯一胜者，winnerTeamId=" + _winnerTeamId + "）"; }

            string label = _outcome == PMCombatHudOutcome.Win ? "胜" : "负";
            return label + "（winnerTeamId=" + _winnerTeamId + "，本队=" + _localTeamId + "）";
        }

        private int PanelHeight()
        {
            return TitleHeight + _bodyLines * LineHeight + PanelPadding;
        }

        /// <summary>
        /// 真实 Unity 2019 的 IMGUI 绘制入口。未就绪/已释放时什么都不画（不建 Canvas、不改 UI 树）。
        /// </summary>
        private void OnGUI()
        {
            if (_disposed || !_visible) { return; }

            int height = PanelHeight();

            GUI.Box(new Rect(PanelX, PanelY, PanelWidth, height), "R6 Combat  " + _matchId);
            GUI.Label(new Rect(PanelX + PanelPadding, PanelY + TitleHeight,
                               PanelWidth - PanelPadding * 2, height - TitleHeight),
                      _body);
        }
    }
}
