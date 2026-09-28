// ============================================================================
//  PMCombatHudTest —— T-LOOP3/T-LIVE2「可信胜负弹窗 + 返回大厅（返回后整块隐藏）」纯替身可执行门
// ============================================================================
//
//  它证明什么（都是**可执行断言**，不是静态检查）
//  ---------------------------------------------
//    1) 居中结算弹窗的可见性：必须「可信终局结论已存」**且**「本帧终局 ACK 已发出」；
//       只复制到 CombatMatchEnded、或只有结论没有 ACK 证据时**一律不弹**（且计数）；
//    2) 按钮语义：可点时画真 Button，不可点时画只读 Box（结构上不可能误触）；
//       点击把**绑定时的会话身份令牌**原样回传给宿主入口；
//    3) 返回大厅后（T-LIVE2 用户新口径）：**整个 HUD 隐藏**（Box/Label/Button 全部零绘制）、
//       「返回大厅」按钮入口/令牌解绑且**不再回调**（双击/陈旧令牌结构上不可能二次退场）；
//       隐藏的只是绘制，类内结果状态仍保留（不清权威/不造额外结果）；
//    4) 宿主入口拒绝（陈旧令牌/未受理）时 HUD 只计入拒绝，**不**自行改写结论、不自行销毁；
//    5) 回调抛异常被隔离（HUD 不崩、结论不变）；
//    6) 无结论时调用「已返回大厅」不产生弹窗（不受不可信断线刺激）；
//    7) 撤销结论时弹窗立即收回；Dispose 后不再绘制且幂等；Screen 为 0 时版面兜底不崩；
//    8) T-LIVE2：**返回大厅后整个结算 HUD 隐藏**（OnGUI Box/Label/Button 全部零绘制）、
//       「返回大厅」按钮解绑（双击/陈旧 token 到不了宿主），但类内结果只读状态仍保留，
//       返回后宿主误驱也不会让结算 UI 复活；无结论时「已返回大厅」不制造隐藏态。
//
//  它**不**证明什么（必须诚实说清，避免把替身当实机）
//  ------------------------------------------------
//    · 不证明真实 Unity 的渲染/字体/皮肤/版面观感（那要 Player 实机，见 T-LOOP8 / T-LIVE5），
//      也不证明“返回大厅后画面真的干净”——本门只钉住“OnGUI 一帧都不调 IMGUI”；
//    · 不证明 uGUI 会不会把 IMGUI 盖住（T-LOOP1 已注明"UI 是否被 uGUI 遮挡需 Player 实机"）；
//    · 不证明真实 socket 上 ServerCombatResultAckV1 一定送达（宿主侧只有"桥发出过 RPC"这一
//      有界证据，见 PMClientSessionHost.FlushCombatState 的注释）；
//    · 宿主侧的**接线**（按钮落到哪个入口、入口的令牌/ACK 闸门、走 EndSessionNormally 而不是
//      Stop）由本文件末尾的**有界源码约束**补充 —— 它读真实源码做结构性断言，**不等于**实机，
//      也**不能**替代上面的可执行行为断言。
//
//  运行
//  ----
//    dotnet build Tools/PMCombatHudTest -c Release      （必须 exit 0）
//    dotnet Tools/PMCombatHudTest/bin/Release/net8.0/PMCombatHudTest.dll
//  退出码：0 = 全部通过；1 = 存在失败（含源码约束）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using PMNet.Unity;
using UnityEngine;

namespace PMCombatHudTest
{
    internal static class Program
    {
        private const string HostSourceRelativePath =
            "Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs";

        private const string HudSourceRelativePath =
            "Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs";

        private static int _passed;
        private static int _failed;

        private static MethodInfo _onGuiMethod;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception) { }

            Console.WriteLine("=== PMCombatHudTest —— T-LOOP3 结算弹窗 / 返回大厅（纯替身可执行门）===");
            Console.WriteLine("被测源码：" + HudSourceRelativePath + "（真实源码，非快照）");
            Console.WriteLine();

            RunHudBehaviourChecks();

            Console.WriteLine();
            Console.WriteLine("--- 有界源码约束（宿主接线；静态结构断言，不等于实机）---");
            RunHostSourceConstraints();

            Console.WriteLine();
            Console.WriteLine("=== 合计：" + _passed + " 通过 / " + _failed + " 失败 ===");
            return _failed == 0 ? 0 : 1;
        }

        // =========================================================================================
        //  A. HUD 行为（可执行）
        // =========================================================================================

        private static void RunHudBehaviourChecks()
        {
            Console.WriteLine("--- A. HUD 行为（真实源码 + 功能性替身，可执行）---");

            // H1：创建成功、初始无弹窗、无按钮绑定。
            {
                PMNet.PMNetRuntime.IsDedicatedServer = false;
                string error;
                PMUnityCombatHud hud = PMUnityCombatHud.Create("match-T1", out error);
                Check("H1a Create 成功且 error 为 null", hud != null && error == null);
                Check("H1b 初始未释放、matchId 正确", hud != null && !hud.IsDisposed && hud.MatchId == "match-T1");
                Check("H1c 初始无结算弹窗/未可点/无绑定", hud != null
                      && !hud.IsResultPanelVisible && !hud.IsLobbyReturnClickable
                      && !hud.IsReturnedToLobby && !hud.HasLobbyReturnHandler && hud.SessionToken == 0L);

                if (hud != null) { Draw(hud); }
                Check("H1d 仅角标：1 个 Box + 1 个 Label，无 Button 无第二面板",
                      GUI.BoxTexts.Count == 1 && GUI.LabelTexts.Count == 1 && GUI.ButtonsDrawn == 0);
                Check("H1e 角标正文含 HP/Mana/Energy 与 F/G 提示",
                      GUI.LabelTexts.Count == 1 && GUI.LabelTexts[0].Contains("HP")
                      && GUI.LabelTexts[0].Contains("Mana") && GUI.LabelTexts[0].Contains("G = 大招"));

                if (hud != null) { hud.Dispose(); }
            }

            // H2：DS 禁建（无头进程不允许有 HUD）。
            {
                PMNet.PMNetRuntime.IsDedicatedServer = true;
                string error;
                PMUnityCombatHud hud = PMUnityCombatHud.Create("match-ds", out error);
                Check("H2a DS 下 Create 返回 null 且不抛", hud == null);
                Check("H2b DS 拒建原因含 DS", error != null && error.Contains("DS"));
                PMNet.PMNetRuntime.IsDedicatedServer = false;
            }

            // H3：只复制到 CombatMatchEnded（没有可信结果）**不得**弹结算弹窗。
            {
                PMUnityCombatHud hud = CreateForTest("match-T3");
                hud.SetReady(false);
                // 宿主在该帧会调用 SetResultPanel(terminalObserved=false, ack=false)：
                hud.SetResultPanel(false, false);
                Draw(hud);
                Check("H3a 无结论时不弹窗（无 Button、无第二 Box）",
                      !hud.IsResultPanelVisible && GUI.ButtonsDrawn == 0 && GUI.BoxTexts.Count == 1);
                Check("H3b 无结论时不被计入 premature", hud.ResultPanelPrematureRequests == 0);

                // 即使宿主**错误地**请求显示（例如把复制位当结论），HUD 也必须 fail closed。
                hud.SetResultPanel(true, true);
                Draw(hud);
                Check("H3c 无可信结论时即使请求显示也不弹（fail closed）+ 计数",
                      !hud.IsResultPanelVisible && GUI.ButtonsDrawn == 0 && hud.ResultPanelPrematureRequests == 1);
                hud.Dispose();
            }

            // H4：有结论但**本帧 ACK 未发出** → 不弹（契约：同帧先发 ServerCombatResultAck 才使按钮可点）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T4");
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 2, 2);
                hud.SetResultPanel(true, false);
                Draw(hud);
                Check("H4a 结论已存但 ACK 未发 → 不弹窗", !hud.IsResultPanelVisible && GUI.ButtonsDrawn == 0);
                Check("H4b 缺 ACK 的显示请求被计入 premature", hud.ResultPanelPrematureRequests == 1);

                // 同一份结论 + ACK 证据 → 立即弹。
                hud.SetResultPanel(true, true);
                Draw(hud);
                Check("H4c 补上 ACK 证据后弹窗出现", hud.IsResultPanelVisible);
                Check("H4d 弹窗与角标各自画了胜负文案（Win ⇒ 胜利）",
                      AnyLabelContains("胜利") && AnyLabelContains("结果：胜"));
                Check("H4e 未绑定宿主入口时按钮不可点（画成只读 Box）",
                      !hud.IsLobbyReturnClickable && GUI.ButtonsDrawn == 0
                      && GUI.BoxTexts.Contains("等待结果确认…"));
                Check("H4f 本帧 Box = 角标标题 + 弹窗标题 + 不可点按钮占位",
                      GUI.BoxTexts.Count == 3 && GUI.BoxTexts.Contains("R6 Combat 结算"));
                hud.Dispose();
            }

            // H5：绑定会话身份令牌 → 可点；点击把令牌回传宿主。
            {
                PMUnityCombatHud hud = CreateForTest("match-T5");
                long receivedToken = -1L;
                int calls = 0;
                hud.BindLobbyReturn(4242L, delegate(long token)
                {
                    calls++;
                    receivedToken = token;
                    return true;   // 宿主假装受理但**不**标记已返回
                });

                hud.SetOutcome(true, PMCombatHudOutcome.Lose, 1, 2);
                hud.SetResultPanel(true, true);
                Draw(hud);

                Check("H5a 绑定后按钮可点且画了真 Button",
                      hud.IsLobbyReturnClickable && GUI.ButtonsDrawn == 1 && GUI.LastButtonText == "返回大厅");
                Check("H5b 令牌原样登记", hud.SessionToken == 4242L);

                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H5c 点击回传了绑定令牌", calls == 1 && receivedToken == 4242L);
                Check("H5d 受理计数 +1、未自行标记已返回",
                      hud.LobbyReturnAcceptedCount == 1 && hud.LobbyReturnRejectedCount == 0
                      && !hud.IsReturnedToLobby && hud.IsLobbyReturnClickable);

                // 宿主未标记已返回时：按钮仍可点，第二次点击仍回调（HUD 不替宿主做决定）。
                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H5e 宿主未受理完成前，第二次点击仍回调（HUD 不改写结论）",
                      calls == 2 && hud.LobbyReturnAcceptedCount == 2);
                hud.Dispose();
            }

            // H6：宿主受理成功后（同步 SetReturnedToLobby）→ 弹窗保留只读、按钮不可点、重复点击不再回调。
            {
                PMUnityCombatHud hud = CreateForTest("match-T6");
                int calls = 0;
                hud.BindLobbyReturn(77L, delegate(long token)
                {
                    calls++;
                    hud.SetReturnedToLobby();   // 宿主在受理里同步标记「已返回大厅」
                    return true;
                });

                hud.SetOutcome(true, PMCombatHudOutcome.Win, 3, 3);
                hud.SetResultPanel(true, true);
                Draw(hud);
                Check("H6a 受理前可点", hud.IsLobbyReturnClickable && GUI.ButtonsDrawn == 1);

                GUI.ClickNextButton = true;
                Draw(hud);
                // T-LIVE2：用户新口径 = 返回大厅后**整个结算 UI 隐藏**（不再保留可见）。
                // 保留的是「结果状态」（IsReturnedToLobby + 类内结论 + 宿主 static 快照），不是绘制。
                Check("H6b 受理后：已返回、不可点、整个结算 UI 已隐藏（T-LIVE2 新口径）",
                      calls == 1 && hud.IsReturnedToLobby && !hud.IsResultPanelVisible
                      && !hud.IsLobbyReturnClickable && !hud.HasLobbyReturnHandler);

                // 点击发生的那一帧里按钮在点击**前**仍是可点的（所以它确实画过 Button）；
                // “不可点”是**下一帧**才成立的版面事实。
                Check("H6c 受理帧本身确实画过可点 Button", GUI.ButtonsDrawn == 1);

                Draw(hud);
                Check("H6d 下一帧整个 HUD 零绘制（角标与结算弹窗都不再画）",
                      GUI.ButtonsDrawn == 0 && GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0
                      && GUI.BoxTexts.Count == 0 && GUI.LabelTexts.Count == 0);

                // 重复点击 / 陈旧回调：不可点 ⇒ 没有 Button 可点，注入的点击不会被消费，回调次数不变。
                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H6e 重复点击不再回调（无 Button 可点，陈旧令牌到不了宿主）",
                      calls == 1 && hud.LobbyReturnAcceptedCount == 1 && hud.LobbyReturnRejectedCount == 0
                      && GUI.ButtonsDrawn == 0 && GUI.HasPendingClick);
                GUI.ClickNextButton = false;   // 清掉未消费的注入点击

                // 反复重绘不改变状态（幂等）。
                Draw(hud);
                Draw(hud);
                Check("H6f 重复重绘幂等（仍为已返回、仍零绘制）",
                      calls == 1 && hud.IsReturnedToLobby && !hud.IsResultPanelVisible
                      && GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0);

                // T-LIVE2：隐藏的是**绘制**，不是**结果状态** —— 结论与「已返回」标志必须还在。
                string retained = hud.Describe();
                Check("H6g 返回后结果状态保留（outcome/winner/team/returned 不变，仅 panel/token 归零）",
                      retained.Contains("outcome=Win") && retained.Contains("winner=3")
                      && retained.Contains("team=3") && retained.Contains("returned=1")
                      && retained.Contains("panel=0") && retained.Contains("clickable=0")
                      && retained.Contains("token=0"));
                hud.Dispose();
            }

            // H7：宿主拒绝（陈旧令牌/未受理）→ HUD 只计数、按钮仍可点（由宿主决定何时退场）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T7");
                int calls = 0;
                hud.BindLobbyReturn(9L, delegate(long token)
                {
                    calls++;
                    return false;   // 宿主拒绝（例如令牌属于旧会话）
                });

                hud.SetOutcome(true, PMCombatHudOutcome.Draw, 0, 1);
                hud.SetResultPanel(true, true);
                Draw(hud);
                Check("H7a 平局大字为「平局」", AnyLabelContains("平局"));

                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H7b 宿主拒绝 ⇒ 计入 rejected，不标记已返回",
                      calls == 1 && hud.LobbyReturnRejectedCount == 1 && hud.LobbyReturnAcceptedCount == 0
                      && !hud.IsReturnedToLobby && hud.IsResultPanelVisible);
                hud.Dispose();
            }

            // H8：回调抛异常被隔离（HUD 不崩、结论与状态不变）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T8");
                hud.BindLobbyReturn(5L, delegate(long token) { throw new InvalidOperationException("宿主内部异常"); });
                hud.SetOutcome(true, PMCombatHudOutcome.Lose, 4, 1);
                hud.SetResultPanel(true, true);

                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H8a 回调异常被隔离并计数", hud.LobbyReturnExceptionCount == 1);
                Check("H8b 异常后结论与弹窗状态不变",
                      hud.IsResultPanelVisible && !hud.IsReturnedToLobby
                      && hud.LobbyReturnAcceptedCount == 0 && hud.LobbyReturnRejectedCount == 0);

                // 解绑后按钮不可点（即使弹窗还在）。
                hud.ClearLobbyReturn();
                Draw(hud);
                Check("H8c 解绑后不可点且令牌清零",
                      !hud.IsLobbyReturnClickable && hud.SessionToken == 0L && GUI.ButtonsDrawn == 0);
                hud.Dispose();
            }

            // H9：无结论时调用「已返回大厅」不得弹窗（不受不可信断线刺激）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T9");
                hud.SetReturnedToLobby();
                Draw(hud);
                Check("H9a 无可信结论 ⇒ 「已返回大厅」不产生弹窗",
                      !hud.IsReturnedToLobby && !hud.IsResultPanelVisible
                      && GUI.ButtonsDrawn == 0 && GUI.BoxTexts.Count == 1);
                hud.Dispose();
            }

            // H10：结论被撤销（正常不会发生）⇒ 弹窗立即收回（fail closed）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T10");
                hud.BindLobbyReturn(1L, delegate(long token) { return true; });
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 1, 1);
                hud.SetResultPanel(true, true);
                Check("H10a 撤销前弹窗可见", hud.IsResultPanelVisible);

                hud.SetOutcome(false, PMCombatHudOutcome.None, 0, 0);
                Draw(hud);
                Check("H10b 撤销结论后弹窗与按钮立即收回",
                      !hud.IsResultPanelVisible && !hud.IsLobbyReturnClickable && GUI.ButtonsDrawn == 0);
                hud.Dispose();
            }

            // H11：Dispose 幂等、之后不再绘制。
            {
                PMUnityCombatHud hud = CreateForTest("match-T11");
                hud.BindLobbyReturn(2L, delegate(long token) { return true; });
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 1, 1);
                hud.SetResultPanel(true, true);
                hud.Dispose();
                hud.Dispose();

                Draw(hud);
                Check("H11a Dispose 后不再绘制", hud.IsDisposed && GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0);
                Check("H11b Dispose 清掉绑定与弹窗", !hud.HasLobbyReturnHandler && !hud.IsResultPanelVisible);

                hud.SetResultPanel(true, true);
                hud.SetReturnedToLobby();
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 1, 1);
                Check("H11c 释放后 setter 幂等无副作用", !hud.IsResultPanelVisible && !hud.IsReturnedToLobby);
            }

            // H12：Screen 为 0 / 非有限值时版面兜底不崩，且弹窗仍在屏内。
            {
                int savedWidth = Screen.width;
                int savedHeight = Screen.height;

                PMUnityCombatHud hud = CreateForTest("match-T12");
                hud.BindLobbyReturn(3L, delegate(long token) { return true; });
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 1, 1);
                hud.SetResultPanel(true, true);

                Screen.width = 0;
                Screen.height = 0;
                Draw(hud);
                Check("H12a Screen=0 时不崩且弹窗照画", hud.IsResultPanelVisible && GUI.ButtonsDrawn == 1);
                Check("H12b Screen=0 时按钮矩形有限且有界",
                      IsFinite(GUI.LastButtonRect.x) && IsFinite(GUI.LastButtonRect.y)
                      && GUI.LastButtonRect.width > 0f && GUI.LastButtonRect.height > 0f);

                Screen.width = 1920;
                Screen.height = 1080;
                Draw(hud);
                Rect button = GUI.LastButtonRect;

                // 版面契约：520x280 的弹窗居中 ⇒ 水平中心 = 960；
                // 按钮画在弹窗**底部**（距弹窗底 24px）⇒ 垂直中心 = 400 + 280 - 24 - 52/2 = 630。
                const float expectedCenterX = 960f;
                const float expectedCenterY = 630f;
                Check("H12c 1920x1080 下按钮水平居中（±1px）",
                      Math.Abs((button.x + button.width * 0.5f) - expectedCenterX) <= 1f);
                Check("H12d 1920x1080 下按钮位于弹窗底部居中（±1px）",
                      Math.Abs((button.y + button.height * 0.5f) - expectedCenterY) <= 1f);

                hud.Dispose();
                Screen.width = savedWidth;
                Screen.height = savedHeight;
            }

            // H13：Describe 单行诊断包含弹窗/令牌/点击计数（实机对账用）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T13");
                hud.BindLobbyReturn(31L, delegate(long token) { return false; });
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 1, 1);
                hud.SetResultPanel(true, true);
                GUI.ClickNextButton = true;
                Draw(hud);

                string text = hud.Describe();
                Check("H13a Describe 含 panel/returned/clickable/token/clicks/rejected",
                      text.Contains("panel=1") && text.Contains("returned=0") && text.Contains("clickable=1")
                      && text.Contains("token=31") && text.Contains("clicks=0") && text.Contains("rejected=1"));
                hud.Dispose();
            }

            // H14：T-LIVE2 —— 「返回大厅后整个结算 HUD 隐藏」专项行为（用户实机新口径）。
            //
            // 契约（net-architecture-migration.md T-LIVE2）：
            //   “只在可信结局的 EndSessionNormally→SetReturnedToLobby 隐藏整个 HUD
            //     （OnGUI Box/Label/Button 全部零绘制、按钮解绑），而对局结果刚到时仍居中可见/能点；
            //     自动 7s 与按钮路径同一结果，双击与陈旧 token 不关新局，无结论不绘；
            //     在显示状态变化后不造额外结果或清权威。”
            {
                PMUnityCombatHud hud = CreateForTest("match-T14");
                int calls = 0;
                hud.BindLobbyReturn(141L, delegate(long token) { calls++; return true; });
                hud.SetOutcome(true, PMCombatHudOutcome.Win, 2, 2);
                hud.SetResultPanel(true, true);
                Draw(hud);

                // 返回前：终局刚到时仍居中可见、按钮可点（新口径不得回退这一步）。
                Check("H14a 终局刚到时弹窗可见且按钮可点（返回前行为不变）",
                      hud.IsResultPanelVisible && hud.IsLobbyReturnClickable && GUI.ButtonsDrawn == 1
                      && AnyLabelContains("胜利"));

                // EndSessionNormally（按钮点击与 7s 有界退场走的是同一个入口）把整个 HUD 关掉。
                hud.SetReturnedToLobby();
                Draw(hud);
                Check("H14b 返回大厅后整个 HUD 零绘制（Box/Label/Button 全为 0）",
                      GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0
                      && GUI.BoxTexts.Count == 0 && GUI.LabelTexts.Count == 0);
                Check("H14c 返回后弹窗不可见、按钮已解绑且令牌归零",
                      !hud.IsResultPanelVisible && !hud.IsLobbyReturnClickable
                      && !hud.HasLobbyReturnHandler && hud.SessionToken == 0L && hud.IsReturnedToLobby);

                // 双击 / 陈旧 token：返回后结构上已经没有 Button 可点，注入的点击必须没人消费。
                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H14d 返回后注入点击不回调宿主（双击/陈旧令牌不可能关掉新会话）",
                      calls == 0 && GUI.HasPendingClick && GUI.ButtonsDrawn == 0);
                GUI.ClickNextButton = false;

                // 粘性解绑：即使有人在返回后重新绑定入口/令牌，也不得复活点击路径。
                hud.BindLobbyReturn(999L, delegate(long token) { calls++; return true; });
                Check("H14d2 返回后重新绑定被拒（点击路径保持不存在）",
                      !hud.HasLobbyReturnHandler && hud.SessionToken == 0L && !hud.IsLobbyReturnClickable);
                GUI.ClickNextButton = true;
                Draw(hud);
                Check("H14d3 重新绑定尝试后仍零绘制、仍无回调",
                      calls == 0 && GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0);
                GUI.ClickNextButton = false;

                // 结果只读状态保留：隐藏的只是绘制，不是类内结论（更不是宿主的 static 快照）。
                string hidden = hud.Describe();
                Check("H14e 结果快照仍保留（只隐藏绘制，不清权威/不造额外结果）",
                      hidden.Contains("outcome=Win") && hidden.Contains("winner=2") && hidden.Contains("team=2")
                      && hidden.Contains("returned=1") && hidden.Contains("panel=0")
                      && hidden.Contains("token=0") && hidden.Contains("clickable=0"));

                // 返回后宿主即使**误调** SetResultPanel(true,true)，结算 UI 也不得复活（fail closed）。
                hud.SetResultPanel(true, true);
                Draw(hud);
                Check("H14f 返回后宿主误驱 SetResultPanel 不会让结算 UI 复活",
                      !hud.IsResultPanelVisible && !hud.IsLobbyReturnClickable
                      && GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0);
                Check("H14g 误驱不改变结论（不造额外结果、不改胜负）",
                      hud.IsReturnedToLobby && hud.Describe().Contains("outcome=Win")
                      && hud.Describe().Contains("winner=2"));
                hud.Dispose();
            }

            // H15：T-LIVE2 —— 自动退场（7s 有界退场）与按钮走**同一条**结果：
            // 两者都经宿主 EndSessionNormally → SetReturnedToLobby，所以 HUD 侧看到的终态完全一致。
            {
                PMUnityCombatHud byAuto = CreateForTest("match-T15a");
                byAuto.SetOutcome(true, PMCombatHudOutcome.Lose, 1, 2);
                byAuto.SetResultPanel(true, true);
                byAuto.SetReturnedToLobby();       // 模拟宿主自动退场分支
                Draw(byAuto);

                PMUnityCombatHud byButton = CreateForTest("match-T15b");
                int buttonCalls = 0;
                byButton.BindLobbyReturn(151L, delegate(long token) { buttonCalls++; return true; });
                byButton.SetOutcome(true, PMCombatHudOutcome.Lose, 1, 2);
                byButton.SetResultPanel(true, true);
                GUI.ClickNextButton = true;
                Draw(byButton);
                byButton.SetReturnedToLobby();      // 模拟宿主在按钮受理里同步标记
                Draw(byButton);

                Check("H15a 两条路径都回调/退场成功", buttonCalls == 1);
                Check("H15b 自动退场与按钮路径的 HUD 终态一致（都零绘制、都保留结论）",
                      GUI.BoxesDrawn == 0 && GUI.LabelsDrawn == 0 && GUI.ButtonsDrawn == 0
                      && byAuto.IsReturnedToLobby && byButton.IsReturnedToLobby
                      && byAuto.Describe().Contains("outcome=Lose") && byButton.Describe().Contains("outcome=Lose")
                      && !byAuto.IsResultPanelVisible && !byButton.IsResultPanelVisible);
                byAuto.Dispose();
                byButton.Dispose();
            }

            // H16：T-LIVE2 —— 无结论不绘：无结论时「已返回大厅」不得制造隐藏态/弹窗，
            // 也不能把仍在进行的角标面板关掉（本局无结果时 HUD 属正常战斗显示）。
            {
                PMUnityCombatHud hud = CreateForTest("match-T16");
                hud.SetReturnedToLobby();
                Draw(hud);
                Check("H16a 无结论 ⇒ 「已返回大厅」不产生隐藏态/弹窗",
                      !hud.IsReturnedToLobby && !hud.IsResultPanelVisible && GUI.ButtonsDrawn == 0);
                Check("H16b 无结论时角标仍正常绘制（不被误当成退场清理）",
                      GUI.BoxesDrawn == 1 && GUI.LabelsDrawn == 1 && AnyLabelContains("进行中"));
                hud.Dispose();
            }
        }

        // =========================================================================================
        //  B. 宿主接线（有界源码约束）
        // =========================================================================================

        private static void RunHostSourceConstraints()
        {
            string root = FindRepoRoot();
            if (root == null)
            {
                Check("S0 找到仓库根（含 Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs）", false);
                return;
            }

            Check("S0 找到仓库根", true);

            string hostPath = Path.Combine(root, HostSourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string hudPath = Path.Combine(root, HudSourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(hostPath) || !File.Exists(hudPath))
            {
                Check("S1 读到宿主编译（真实源码）", false);
                return;
            }

            string hostCode = StripComments(File.ReadAllText(hostPath, Encoding.UTF8));
            string hudCode = StripComments(File.ReadAllText(hudPath, Encoding.UTF8));

            // S1：返回大厅公开入口的闸门（会话身份 + 可信结论 + 本帧 ACK），且**只**走 EndSessionNormally。
            {
                string error;
                string body = ExtractMethodBody(hostCode, "public static bool TryRequestReturnToLobbyFromHud(long sessionToken)", out error);
                Check("S1a 存在返回大厅公开入口", body != null);
                if (body != null)
                {
                    Check("S1b 入口校验会话身份令牌（跨局陈旧回调被拒）",
                          body.Contains("session.IdentityToken != sessionToken"));
                    Check("S1c 入口要求可信终局已存", body.Contains("!session.TerminalResultObserved"));
                    Check("S1d 入口要求本帧终局 ACK 已发", body.Contains("!session.TerminalAckSent"));
                    Check("S1e 入口走 EndSessionNormally（保留结论）", body.Contains("EndSessionNormally("));
                    Check("S1f 入口**不**调用 Stop（不因按钮清结果）", !body.Contains("Stop("));
                }
            }

            // S2：HUD 弹窗的每帧唯一驱动点必须同时带上「可信结论」与「ACK 证据」。
            {
                string error;
                string body = ExtractMethodBody(hostCode, "private static void UpdateCombatHud(Session session)", out error);
                Check("S2a 存在 UpdateCombatHud", body != null);
                if (body != null)
                {
                    Check("S2b 弹窗驱动点在 UpdateCombatHud 内且带上 ACK 证据",
                          body.Contains("hud.SetResultPanel(session.TerminalResultObserved, session.TerminalAckSent)"));
                }
            }

            // S3：ACK 证据的来源（桥 RPC 计数增量）与位设置。
            {
                string error;
                string body = ExtractMethodBody(hostCode, "private static bool FlushCombatState(Session session, double wallNowMs)", out error);
                Check("S3a FlushCombatState 返回 bool（ACK 证据）", body != null);
                if (body != null)
                {
                    Check("S3b 以桥 RPC 计数增量作为 ACK 已发证据",
                          body.Contains("bridge.RpcSent > rpcSentBefore"));
                    Check("S3c 仅在可信终局已存时置 TerminalAckSent",
                          body.Contains("session.TerminalResultObserved && resultAckSent")
                          && body.Contains("session.TerminalAckSent = true"));
                }
            }

            // S4：正常退场路径把保留中的 HUD 标为「已返回大厅」（按钮只读）。
            {
                string error;
                string body = ExtractMethodBody(hostCode, "private static void EndSessionNormally(Session session, string reason)", out error);
                Check("S4a 存在 EndSessionNormally", body != null);
                if (body != null)
                {
                    Check("S4b 正常退场标记保留 HUD 为已返回大厅",
                          body.Contains("_retainedHud.SetReturnedToLobby()"));
                    Check("S4c 正常退场摘出 HUD 而不是销毁（保留只读结论）",
                          body.Contains("_retainedHud = session.Hud") && body.Contains("session.Hud = null"));
                }
            }

            // S5：Enter 把会话身份令牌与 HUD 绑定（按钮属于哪一局由此确定）。
            {
                string error;
                string body = ExtractMethodBody(hostCode, "public static void Enter(PMDsEntryOffer offer)", out error);
                Check("S5a 存在 Enter", body != null);
                if (body != null)
                {
                    Check("S5b Enter 为会话分配身份令牌",
                          body.Contains("session.IdentityToken = _nextSessionToken"));
                    Check("S5c Enter 把令牌与公开入口绑给 HUD",
                          body.Contains("session.Hud.BindLobbyReturn(session.IdentityToken, TryRequestReturnToLobbyFromHud)"));
                    Check("S5d Enter 仍是显式清理点（释放保留 HUD + 清结果）",
                          body.Contains("ReleaseRetainedHud()") && body.Contains("ClearLastCombatResult()"));
                }
            }

            // S6：HUD 自己不得退场（它只能回调整宿主；绝不碰 EndSessionNormally/Stop）。
            Check("S6a HUD 源码不含 EndSessionNormally（退场只能由宿主决定）",
                  !hudCode.Contains("EndSessionNormally"));
            Check("S6b HUD 源码不含 Stop(（不得用 Stop 清结果）",
                  !hudCode.Contains("Stop("));
            Check("S6c HUD 用真实 GUI.Button 画可点按钮",
                  hudCode.Contains("GUI.Button(") && hudCode.Contains("\"返回大厅\""));
            Check("S6d HUD 弹窗自守卫（无结论或缺 ACK 证据即拒绘）",
                  hudCode.Contains("bool next = visible && _hasOutcome && resultAckSent;"));

            // S7：T-LIVE2 —— HUD 侧「返回大厅 ⇒ 整块 HUD 隐藏 + 按钮解绑」的落地形态。
            //
            // 与 T-LOOP3 的区别（用户新口径）：返回大厅后不再「保留弹窗只读可见」，
            // 而是**整个 HUD 零绘制**；保留的只有类内的结果状态与宿主 static 只读快照。
            {
                Check("S7a HUD 在 _resultReturned 时于 OnGUI 提前返回（整块 HUD 零绘制）",
                      hudCode.Contains("if (_resultReturned) { return; }"));

                string error;
                string body = ExtractMethodBody(hudCode, "public void SetReturnedToLobby()", out error);
                Check("S7b 存在 SetReturnedToLobby 方法体", body != null);
                if (body != null)
                {
                    Check("S7c 返回后仍先要求「类内已有可信结论」（无结论不绘）",
                          body.Contains("if (!_hasOutcome) { return; }"));
                    Check("S7d 返回后收起弹窗（_resultPanelVisible = false）",
                          body.Contains("_resultPanelVisible = false;"));
                    Check("S7e 返回后解绑按钮入口与令牌（_lobbyReturnHandler = null / _sessionToken = 0L）",
                          body.Contains("_lobbyReturnHandler = null;") && body.Contains("_sessionToken = 0L;"));
                    Check("S7f 返回只改显示/绑定，不改写结论（不赋 _hasOutcome / _outcome / _winnerTeamId）",
                          !body.Contains("_hasOutcome =")
                          && !body.Contains("_outcome =")
                          && !body.Contains("_winnerTeamId ="));
                }

                // S7g：返回后的解绑是**粘性**的 —— 不接受事后重新绑定（不能给旧会话复活点击路径）。
                string bindBody = ExtractMethodBody(
                    hudCode, "public void BindLobbyReturn(long sessionToken, PMCombatHudLobbyReturnHandler handler)",
                    out error);
                Check("S7g BindLobbyReturn 在已返回大厅后拒绝重新绑定",
                      bindBody != null && bindBody.Contains("if (_resultReturned) { return; }"));
            }

            // S8：T-LIVE2 —— 宿主侧**只读**不变量（本项**不改** PMClientSessionHost）：
            //   · 整个 HUD 的隐藏只能由可信结局的 EndSessionNormally→SetReturnedToLobby 触发；
            //   · 结果 static 只读快照仍保留供必要时查询，下次 Enter/Stop 才清；
            //   · 自动 7s 有界退场与按钮点击走同一条退场路径。
            {
                int calls = CountOccurrences(hostCode, "SetReturnedToLobby()");
                Check("S8a 宿主仅在 EndSessionNormally 调用一次 SetReturnedToLobby（不可信路径碰不到）",
                      calls == 1);

                string error;
                string body = ExtractMethodBody(hostCode, "internal static void PumpActive()", out error);
                Check("S8b 存在 PumpActive（7s 有界退场所在方法）", body != null);
                if (body != null)
                {
                    Check("S8c 自动 7s 有界退场与按钮走同一条 EndSessionNormally",
                          body.Contains("TerminalExitGraceMs") && body.Contains("EndSessionNormally("));
                }

                string normalBody = ExtractMethodBody(
                    hostCode, "private static void EndSessionNormally(Session session, string reason)", out error);
                Check("S8d 正常退场不清结果快照（ClearLastCombatResult 只属 Enter/Stop 清理点）",
                      normalBody != null && !normalBody.Contains("ClearLastCombatResult()"));

                Check("S8e 宿主仍保留可信结果只读快照查询入口（HasLastCombatResult/TryGetLastCombatResult）",
                      hostCode.Contains("public static bool HasLastCombatResult")
                      && hostCode.Contains("public static bool TryGetLastCombatResult("));
            }
        }

        // =========================================================================================
        //  工具
        // =========================================================================================

        private static PMUnityCombatHud CreateForTest(string matchId)
        {
            PMNet.PMNetRuntime.IsDedicatedServer = false;
            string error;
            PMUnityCombatHud hud = PMUnityCombatHud.Create(matchId, out error);
            if (hud == null)
            {
                throw new InvalidOperationException("Create 失败：" + error);
            }

            return hud;
        }

        /// <summary>
        /// 调用 HUD 的私有 OnGUI 一次（模拟 Unity 的一帧 IMGUI 事件）。
        ///
        /// 为什么用反射：OnGUI 是 Unity 的**魔术方法**，刻意保持 private（真实 Unity 按名字反射调用）。
        /// 门自己要能报警：一旦它被改名/删除，这里必须显式失败，而不是"没画 = 通过"。
        /// </summary>
        private static void Draw(PMUnityCombatHud hud)
        {
            if (_onGuiMethod == null)
            {
                _onGuiMethod = typeof(PMUnityCombatHud).GetMethod(
                    "OnGUI", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (_onGuiMethod == null)
                {
                    throw new InvalidOperationException(
                        "PMUnityCombatHud.OnGUI 不存在：本门失效（Unity 消息方法被改名/删除），必须报警");
                }
            }

            GUI.ResetFrame();
            _onGuiMethod.Invoke(hud, null);
        }

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FAIL] " + name);
            }
        }

        /// <summary>
        /// 任一 Label 文案**包含**某片段。
        ///
        /// 为什么不用 <c>GUI.LabelTexts.Contains(fragment)</c>：那是 <c>List&lt;string&gt;</c> 的
        /// **元素相等**判定，不是子串匹配 —— 角标正文是一整段多行文本，元素永远不等于某个片段，
        /// 会写出"字符串明明在里面却报 False"的假红。（本门第一版就踩过这个坑。）
        /// </summary>
        private static bool AnyLabelContains(string fragment)
        {
            for (int i = 0; i < GUI.LabelTexts.Count; i++)
            {
                string text = GUI.LabelTexts[i];
                if (text != null && text.Contains(fragment)) { return true; }
            }

            return false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>数某段文本出现次数（用于“这个入口在宿主里恰好只有一个调用点”这类结构断言）。</summary>
        private static int CountOccurrences(string code, string needle)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(needle)) { return 0; }

            int count = 0;
            int index = 0;
            while (true)
            {
                index = code.IndexOf(needle, index, StringComparison.Ordinal);
                if (index < 0) { return count; }

                count++;
                index += needle.Length;
            }
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string probe = Path.Combine(dir.FullName,
                    HostSourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(probe)) { return dir.FullName; }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// 去掉注释（行注释 / 块注释 / 字符串与字符字面量按原样保留）。
        ///
        /// 为什么必须先去注释：本门是**源码结构断言**，注释里出现 "Stop(" / "EndSessionNormally"
        /// 之类的字样（本项目的注释非常爱引用契约原文）会把断言变成假绿/假红。
        /// </summary>
        private static string StripComments(string code)
        {
            StringBuilder sb = new StringBuilder(code.Length);

            bool inLine = false;
            bool inBlock = false;
            bool inString = false;
            bool inChar = false;
            bool verbatim = false;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                char n = i + 1 < code.Length ? code[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n') { inLine = false; sb.Append(c); }
                    continue;
                }

                if (inBlock)
                {
                    if (c == '*' && n == '/') { inBlock = false; i++; }
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);

                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (n == '"') { sb.Append(n); i++; continue; }
                            inString = false;
                            verbatim = false;
                        }

                        continue;
                    }

                    if (c == '\\')
                    {
                        if (i + 1 < code.Length) { sb.Append(code[i + 1]); i++; }
                        continue;
                    }

                    if (c == '"') { inString = false; }
                    continue;
                }

                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\')
                    {
                        if (i + 1 < code.Length) { sb.Append(code[i + 1]); i++; }
                        continue;
                    }

                    if (c == '\'') { inChar = false; }
                    continue;
                }

                if (c == '/' && n == '/') { inLine = true; i++; continue; }
                if (c == '/' && n == '*') { inBlock = true; i++; continue; }

                if (c == '@' && n == '"')
                {
                    inString = true;
                    verbatim = true;
                    sb.Append(c);
                    sb.Append(n);
                    i++;
                    continue;
                }

                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == '\'') { inChar = true; sb.Append(c); continue; }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 取出某个方法的方法体（含大括号）——用于把"结构断言"限定在该方法内部，
        /// 而不是满文件 indexOf（那会把其它方法的写法当成这个方法的行为）。
        /// </summary>
        private static string ExtractMethodBody(string code, string signature, out string error)
        {
            int index = code.IndexOf(signature, StringComparison.Ordinal);
            if (index < 0)
            {
                error = "找不到签名：" + signature;
                return null;
            }

            int brace = code.IndexOf('{', index);
            if (brace < 0)
            {
                error = "签名后没有方法体：" + signature;
                return null;
            }

            int depth = 0;
            for (int i = brace; i < code.Length; i++)
            {
                if (code[i] == '{') { depth++; }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        error = null;
                        return code.Substring(brace, i - brace + 1);
                    }
                }
            }

            error = "方法体大括号不配对：" + signature;
            return null;
        }
    }
}
