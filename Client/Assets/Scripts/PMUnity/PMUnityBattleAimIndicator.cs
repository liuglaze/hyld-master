// ============================================================================
//  PMUnityBattleAimIndicator —— T-LIVE3：世界空间瞄准指示器（**纯表现**，本地 Owner 唯一）
// ============================================================================
//
//  契约来源（唯一依据）：
//    · Docs/plans/net-architecture-migration.md「T-LIVE」整节（T-LIVE3 冻结接口与验收口径）；
//    旁证：Docs/plans/_play_ui_visual.md（T-PLAY4 局内 UI 的只读复用边界）、
//          Client/Assets/AGENTS.md §1/§11（客户端表现层纪律）。
//
//  ---------------------------------------------------------------------------
//  为什么需要它（实机反馈，不是想象）
//  ---------------------------------------------------------------------------
//  旧链 `TouchLogic.OnJoystickMove`（Client/Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs）
//  在按住普通/大招摇杆时会：
//    · `EnsureSelfFireLineRenderer(player)` 取玩家角色**子节点**上的 `LineRenderer`；
//    · `selfFireLineRenderer.enabled = true`，按英雄的 `shootDistance` / `LaunchAngle` / `shootWidth`
//      画「中心 → 每股弹道端点 → 回中心」的世界空间折线（`launchAngle == 0` 时退化为两点直线）；
//    · `JoystickMoveEnd` 里 `enabled = false`（松手即关，**不**销毁对象）。
//  新正式表现 `PlayerVisualV1` 的组件白名单里没有 LineRenderer，`PMUnityBattleControls` 也只在松手时
//  排一次攻击 —— 于是实机上「按住摇杆时看不到任何瞄准提示」。本文件补上这条**纯表现**。
//
//  ---------------------------------------------------------------------------
//  四条硬边界（本文件不得越过）
//  ---------------------------------------------------------------------------
//  1) **本地 Owner 唯一**：宿主（PMClientSessionHost）只为本端 AP 的预测位姿调用本类；
//     远端 SP 与 DS 都不创建/不更新（DS 在 `Create` 第一行直接拒绝）。
//  2) **零权威、零 RPC**：本文件不发任何 RPC、不写任何复制/权威字段、不算伤害/命中/胜负，
//     也不读旧 HYLDStaticValue / BattleData / 旧 CommandManger。
//  3) **不借旧 prefab 的实例**：本类自己 `new GameObject(...)` + `AddComponent<LineRenderer>()`，
//     绝不 Instantiate 旧 GameUI（旧脚本的 OnEnable 可能重启已退役玩法）。
//  4) **几何只来自一份计划**：方向/长度/扇形由宿主按**同一次攻击**的
//     `PMCombatWeaponPlanner.TryBuild` 计划给出（`Spec.SpeedMps × Spec.LifetimeMs / 1000` 与
//     `Plan.Directions`），本类只把它铺成折线（纯数学在 `PMUnityBattleAimMath`，可离线逐条断言）。
//     因此「指示方向」与「本次上行方向 / planner 方向 / 枪口」不可能分叉。
//
//  ---------------------------------------------------------------------------
//  编译门（与 PMUnityBattleControls 同一套纪律）
//  ---------------------------------------------------------------------------
//    · Tools/PMR4UnityCheck（真实 Unity 2019 DLL，显式 UNITY_EDITOR）
//      与 Tools/PMNetUnityPlayerCheck（真实 **Player 变体**引擎程序集，UNITY_2019_1_OR_NEWER）
//      ⇒ 编译下面的**完整实现**（真实门里绝不跳过实现）；
//    · Tools/PMClientCheck / Tools/PMUnityGlueCheck（手写 UnityEngine 替身，两个宏都不存在）
//      ⇒ 只编译下面的「替身编译面」（同一公开面，全部显式失败，不静默成功）。
//
//  ---------------------------------------------------------------------------
//  已知边界（不许当成“实机已通过”）
//  ---------------------------------------------------------------------------
//    · 线宽（0.06m）与颜色 (1,1,1,0.5) 中的**颜色与旧 TouchLogic 同值**、线宽是派生选择
//      （旧链用的是英雄的 `shootWidth`，计划里没有该字段）；两者都写死在源码里可核对。
//    · 材质用 `Shader.Find` 找内建 shader 并**克隆一次**（绝不写共享资产）：
//      Player 若把内建 shader 全部剥离，`ShaderFound = false` 可观测（此时线不可见，但不抛、不影响玩法）。
//    · 真实的“看不看得见、粗不粗细、挡不挡角色”只能在 Unity 实机里看（T-LIVE5 PENDING_USER）。
// ============================================================================

using System;
using UnityEngine;

namespace PMNet.Unity
{
#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR
    /// <summary>
    /// T-LIVE3 的世界空间瞄准指示器：一根 <see cref="LineRenderer"/>，按宿主给的
    /// 「原点 + N 股世界方向 + 长度」画折线；<see cref="Hide"/> 只关 `enabled`，<see cref="Dispose"/> 才销毁。
    ///
    /// 生命周期由宿主驱动（与 PMUnityBattleControls 同纪律）：
    ///   <c>Create(matchId, out error)</c> → 按住期间每帧 <c>Update(...)</c> → 松手/停局 <c>Hide()</c>
    ///   → 退局 <c>Dispose()</c>（幂等）。
    /// </summary>
    public sealed class PMUnityBattleAimIndicator : MonoBehaviour, IDisposable
    {
        /// <summary>本文件是否编入了完整 LineRenderer 实现（真实门/Player 为 true；两个替身门为 false）。</summary>
        public const bool LineRendererImplementationCompiled = true;

        /// <summary>宿主 GameObject 名（Hierarchy 里一眼可辨；不属于任何业务场景）。</summary>
        private const string HostName = "[PMUnityBattleAimIndicator]";

        /// <summary>
        /// 指示线宽度（米）。旧链用的是英雄的 `shootWidth`，而冻结的计划里没有该字段，
        /// 故取一个显式常量（可核对、不每帧变），并已在文件头「已知边界」登记为派生选择。
        /// </summary>
        private const float AimLineWidthMeters = 0.06f;

        /// <summary>指示线颜色的 alpha（**与旧 TouchLogic 的 `new Color(1,1,1,0.5f)` 同值**）。</summary>
        private const float AimLineAlpha = 0.5f;

        /// <summary>
        /// 内建 shader 候选（按顺序取第一个可用的）。
        /// 为什么必须显式找：新建的 `LineRenderer` 默认**没有材质**，不改就是“画不出来”。
        /// </summary>
        private static readonly string[] ShaderNames = new string[]
        {
            "Sprites/Default",
            "Legacy Shaders/Particles/Alpha Blended",
            "Unlit/Color",
        };

        /// <summary>折线最大点数（= 2 × 最大股数 + 2；与 PMUnityBattleAimMath.MaxFanDirections 同源）。</summary>
        private const int MaxPoints = 2 * PMUnityBattleAimMath.MaxFanDirections + 2;

        // ---------------------------------------------------------------- 状态

        private string _matchId = string.Empty;
        private bool _disposed;
        private bool _visible;
        private LineRenderer _line;

        /// <summary>标准角色的近似DS可命中侧向覆盖带；中心细线与其使用同一批规划方向。</summary>
        private LineRenderer _hitBand;
        private Material _material;
        private bool _shaderFound;

        /// <summary>纯数学铺点用的暂存（构造后不再分配；避免每帧 GC 抖动）。</summary>
        private readonly float[] _positions = new float[MaxPoints * 3];

        // ---------------------------------------------------------------- 诊断（只增）

        public int UpdateCount { get; private set; }
        public int HideCount { get; private set; }
        public int RejectedCount { get; private set; }
        public int LastFanCount { get; private set; }
        public float LastDistanceMeters { get; private set; }
        public float LastPreviewHitRadiusMeters { get; private set; }
        public float LastOriginX { get; private set; }
        public float LastOriginY { get; private set; }
        public float LastOriginZ { get; private set; }

        /// <summary>是否已释放。</summary>
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>当前指示线是否可见（`enabled` 的只读快照）。</summary>
        public bool Visible { get { return _visible; } }

        /// <summary>是否成功找到内建 shader 并挂上材质（false ⇒ 线不可见，但不抛、不影响玩法）。</summary>
        public bool ShaderFound { get { return _shaderFound; } }

        /// <summary>本局对局 ID（对账用）。</summary>
        public string MatchId { get { return _matchId; } }

        // =================================================================================
        //  创建 / 更新 / 隐藏 / 释放
        // =================================================================================

        /// <summary>
        /// 建一个世界空间瞄准指示器。**失败返回 null + 原因**（不抛也不静默）：
        /// DS 进程直接拒绝；Unity 对象创建异常返回 null 并清理半成品。
        ///
        /// 注意：它**不**做「是不是本地 Owner」的判断 —— 那是宿主的责任（宿主只为本端 AP 调用）。
        /// 本类只保证「不创建、就不画」。
        /// </summary>
        public static PMUnityBattleAimIndicator Create(string matchId, out string error)
        {
            error = null;

            // 契约：DS 是无头进程，没有渲染面，也不该有任何瞄准表现。
            if (PMNetRuntime.IsDedicatedServer)
            {
                error = "DS 禁止创建世界瞄准指示器（契约：纯客户端表现，DS/远端 SP 均不创建）";
                return null;
            }

            GameObject root = null;
            try
            {
                root = new GameObject(HostName);
                UnityEngine.Object.DontDestroyOnLoad(root);

                PMUnityBattleAimIndicator indicator = root.AddComponent<PMUnityBattleAimIndicator>();
                if (indicator == null)
                {
                    UnityEngine.Object.Destroy(root);
                    error = "AddComponent<PMUnityBattleAimIndicator> 未返回组件";
                    return null;
                }

                indicator._matchId = matchId == null ? string.Empty : matchId;
                indicator.BuildLine();
                indicator.Hide();
                return indicator;
            }
            catch (Exception ex)
            {
                // 「失败创建清理」：任何构造期异常都拆掉宿主对象，不留孤儿 GameObject。
                if (root != null)
                {
                    try { UnityEngine.Object.Destroy(root); }
                    catch (Exception) { }
                }

                error = "创建世界瞄准指示器异常：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 配置那一根 <see cref="LineRenderer"/>：显式材质/颜色/宽度（不依赖引擎默认）。
        ///
        /// 找不到任何内建 shader 时**不抛**：只是 `ShaderFound = false`（线不可见），
        /// 因为一条瞄准线不应该让玩家整局失败；该事实由诊断串与心跳可观测。
        /// </summary>
        private void BuildLine()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = 0;
            _line.startWidth = AimLineWidthMeters;
            _line.endWidth = AimLineWidthMeters;
            _line.startColor = new Color(1f, 1f, 1f, AimLineAlpha);
            _line.endColor = new Color(1f, 1f, 1f, AimLineAlpha);
            _line.sortingOrder = 1;

            // 半透明覆盖带独立于细中心线：同一组真实规划射线，不把旧ShootWidth当命中宽度。
            // TransformZ 的渲染平面法线置为Y轴，使1.6m等宽带落在世界XZ平面，
            // 而不是随镜头变化的“面向摄像机的丝带”。世界坐标由宿主每帧给定。
            GameObject bandObject = new GameObject("ApproximateHitBand");
            bandObject.transform.SetParent(transform, false);
            bandObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _hitBand = bandObject.AddComponent<LineRenderer>();
            _hitBand.useWorldSpace = true;
            _hitBand.alignment = LineAlignment.TransformZ;
            _hitBand.numCapVertices = 8;
            _hitBand.numCornerVertices = 2;
            _hitBand.positionCount = 0;
            _hitBand.startColor = new Color(0.32f, 0.78f, 1f, 0.18f);
            _hitBand.endColor = new Color(0.32f, 0.78f, 1f, 0.18f);
            _hitBand.sortingOrder = 0;

            // 显式材质（克隆一次，两层只引用自建实例，从不写共享资产）。
            for (int i = 0; i < ShaderNames.Length; i++)
            {
                Shader shader = Shader.Find(ShaderNames[i]);
                if (shader == null) { continue; }

                _material = new Material(shader);
                _material.color = Color.white;
                _line.sharedMaterial = _material;
                _hitBand.sharedMaterial = _material;
                _shaderFound = true;
                break;
            }

            _line.enabled = false;
            _hitBand.enabled = false;
        }

        /// <summary>
        /// 按「原点 + N 股世界方向 + 长度」更新折线（宿主每帧调用；值不变也安全）。
        ///
        /// 返回 false 表示本次几何非法（NaN/非有限/零方向/股数越界/缓冲不足）⇒ **不改线**
        /// （保留上一帧的合法几何，或保持隐藏），并计入 <see cref="RejectedCount"/>。
        /// 所有几何构造走 <see cref="PMUnityBattleAimMath.TryBuildFanPolyline"/>（纯函数，可离线断言）。
        /// </summary>
        public bool Update(float originX, float originY, float originZ,
                           float[] dirX, float[] dirZ, int fanCount, float distanceMeters,
                           float previewRadiusMeters)
        {
            if (_disposed || _line == null || _hitBand == null)
            {
                RejectedCount++;
                return false;
            }

            int floatCount;
            if (!PMUnityBattleControlMath.IsFinite(previewRadiusMeters)
                || previewRadiusMeters <= 0f
                || previewRadiusMeters > PMUnityBattleAimMath.MaxPreviewHitRadiusMeters
                || !PMUnityBattleAimMath.TryBuildFanPolyline(originX, originY, originZ, dirX, dirZ, fanCount,
                                                            distanceMeters, _positions, out floatCount))
            {
                // 坏配置不能保留上一帧较窄或方向已经过期的指示器。
                Hide();
                RejectedCount++;
                return false;
            }

            int pointCount = floatCount / 3;
            try
            {
                // 两层始终用**同一**批世界坐标点：没有“UI显示扇形A、攻击计划为扇形B”。
                // 在禁用状态批量写完后再同时启用，避免显示半帧旧宽度/旧方向。
                _line.enabled = false;
                _hitBand.enabled = false;
                _line.positionCount = pointCount;
                _hitBand.positionCount = pointCount;
                float diameter = previewRadiusMeters * 2f;
                _hitBand.startWidth = diameter;
                _hitBand.endWidth = diameter;
                for (int i = 0; i < pointCount; i++)
                {
                    Vector3 point = new Vector3(_positions[i * 3], _positions[i * 3 + 1],
                                                _positions[i * 3 + 2]);
                    _line.SetPosition(i, point);
                    _hitBand.SetPosition(i, point);
                }
                _hitBand.enabled = true;
                _line.enabled = true;
            }
            catch (Exception)
            {
                // 视觉层的 LineRenderer/Material 若在换局时失效，只收线、不把整局战斗 fault。
                Hide();
                RejectedCount++;
                return false;
            }

            _visible = true;
            UpdateCount++;
            LastFanCount = fanCount;
            LastDistanceMeters = distanceMeters;
            LastPreviewHitRadiusMeters = previewRadiusMeters;
            LastOriginX = originX;
            LastOriginY = originY;
            LastOriginZ = originZ;
            return true;
        }

        /// <summary>
        /// 隐藏指示线（松手 / 取消 / 死亡 / 终局 / 冻结 / 停局 / 换局）。幂等。
        ///
        /// 与旧 `TouchLogic.JoystickMoveEnd` 同一生命周期：只关 `enabled`，**不**销毁对象
        /// （下一帧重新按住时不需要重建 GameObject/材质）。
        /// </summary>
        public void Hide()
        {
            if (_disposed) { return; }

            if (_visible) { HideCount++; }
            _visible = false;

            if (_line != null && _line.enabled) { _line.enabled = false; }
            if (_hitBand != null && _hitBand.enabled) { _hitBand.enabled = false; }
        }

        /// <summary>
        /// 释放：关线 → 销毁**自己克隆的**材质 → 销毁自己的 root。幂等；不碰任何共享资产。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _visible = false;

            DestroyOwnedMaterial();

            if (_line != null)
            {
                try { _line.enabled = false; }
                catch (Exception) { }
            }
            if (_hitBand != null)
            {
                try { _hitBand.enabled = false; }
                catch (Exception) { }
            }

            _line = null;
            _hitBand = null;

            GameObject root = null;
            try { root = gameObject; }
            catch (Exception) { }

            if (root != null)
            {
                try { UnityEngine.Object.Destroy(root); }
                catch (Exception) { }
            }
        }

        private void DestroyOwnedMaterial()
        {
            Material material = _material;
            _material = null;
            if (material == null) { return; }

            try { UnityEngine.Object.Destroy(material); }
            catch (Exception) { }
        }

        /// <summary>单行诊断（心跳/门禁对账用；不含任何敏感数据）。</summary>
        public string Describe()
        {
            return "aim(match=" + _matchId
                   + " disposed=" + (_disposed ? 1 : 0)
                   + " visible=" + (_visible ? 1 : 0)
                   + " updates=" + UpdateCount
                   + " hides=" + HideCount
                   + " rejected=" + RejectedCount
                   + " shader=" + (_shaderFound ? 1 : 0)
                   + " points=" + (_line != null ? _line.positionCount : 0)
                   + " fan=" + LastFanCount
                   + " dist=" + LastDistanceMeters
                   + " previewRadius=" + LastPreviewHitRadiusMeters
                   + " band=" + (_hitBand != null && _hitBand.enabled ? 1 : 0) + ")";
        }
    }
#else
    /// <summary>
    /// 替身编译面（Tools/PMClientCheck、Tools/PMUnityGlueCheck 使用手写 UnityEngine 替身、
    /// **没有** UNITY_2019_1_OR_NEWER / UNITY_EDITOR）。
    ///
    /// 与 PMUnityBattleControls 的替身面同纪律：保留相同公开面，但每个入口**显式失败**
    /// （返回 null / 返回 false / 空操作 + 原因），绝不静默成功、也不假装画了线。
    /// 完整实现见上面的 <c>#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR</c> 分支。
    /// </summary>
    public sealed class PMUnityBattleAimIndicator : IDisposable
    {
        /// <summary>本文件是否编入了完整 LineRenderer 实现（替身门为 false）。</summary>
        public const bool LineRendererImplementationCompiled = false;

        /// <summary>替身面：不提供实现，显式失败（返回 null + 原因）。</summary>
        public static PMUnityBattleAimIndicator Create(string matchId, out string error)
        {
            error = "PMUnityBattleAimIndicator 的完整实现只在真实 Unity（UNITY_2019_1_OR_NEWER）或 Editor"
                    + "（UNITY_EDITOR）下编译；当前编译面是替身（Tools/PMClientCheck 或"
                    + " Tools/PMUnityGlueCheck），不提供 LineRenderer 实现，也不伪造成功。";
            return null;
        }

        /// <summary>替身面：恒为拒绝（没有真实 LineRenderer 可写）。</summary>
        public bool Update(float originX, float originY, float originZ,
                           float[] dirX, float[] dirZ, int fanCount, float distanceMeters,
                           float previewRadiusMeters)
        {
            return false;
        }

        /// <summary>替身面：无操作。</summary>
        public void Hide()
        {
        }

        /// <summary>替身面：无操作。</summary>
        public void Dispose()
        {
        }

        /// <summary>替身面：恒为已释放。</summary>
        public bool IsDisposed { get { return true; } }

        /// <summary>替身面：恒不可见。</summary>
        public bool Visible { get { return false; } }

        /// <summary>替身面：恒无材质。</summary>
        public bool ShaderFound { get { return false; } }

        /// <summary>替身面：空串。</summary>
        public string MatchId { get { return string.Empty; } }

        /// <summary>替身面：诊断串。</summary>
        public string Describe()
        {
            return "aim(stub-build)";
        }
    }
#endif
}
