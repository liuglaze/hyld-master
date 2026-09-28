// ============================================================================
//  PMUnityBattleControls —— T-PLAY4：旧局内 HUD/摇杆的**纯视觉复用 + 新接口重接**
// ============================================================================
//
//  契约来源（唯一依据）：
//    · Docs/plans/net-architecture-migration.md
//        - 「T-PLAY 第一阶段接口与验收细化」末段（A 组相机/宿主先收口，UI 属于真实顺序依赖）；
//        - 「T-PLAY4 UI与输入接口冻结」整节 —— 本文件逐条落点见下。
//    旁证：Docs/plans/net-r6-combat-contract.md「C 宿主接线冻结」末段（「结算 HUD 不是完整旧 UI
//    迁移，原英雄子弹美术/摇杆仍后置」）；Docs/plans/net-legacy-retirement-contract.md（旧链退役：
//    不得复活旧 TouchLogic/EasyTouch/旧联网脚本）；Client/Assets/AGENTS.md §1/§3/§6。
//
//  ---------------------------------------------------------------------------
//  三条硬边界（本文件不得越过）
//  ---------------------------------------------------------------------------
//  1) **绝不激活旧资源**：旧 `Resources/Prefabs/GameUI.prefab`（guid af998dab9af610e4986aa70670bf2cc6）
//     只被**只读**读取 —— 取 `UnityEngine.UI.Image` 的 Sprite 引用与 `RectTransform`/`CanvasScaler`
//     的布局值。本文件**不 Instantiate 它**、不 `SetActive(true)`、不 `SendMessage`、
//     不 `FindObjectOfType`，也不引用 EasyTouch / EasyJoystick / EasyButton / TouchLogic /
//     GameUITeamGemLogic / HYLDHeropropertyUI 等任何旧类型。
//     这些旧组件在当前 prefab 文本里**确实存在**（根 GameUI 带 GameUITeamGemLogic +
//     HYLDHeropropertyUI；Android 带 TouchLogic；PlayerMove/FireNormal/FireSuper 带 EasyJoystick；
//     FireNormalButton 带 EasyButton；根下 EasyTouch；Button 的 UnityEvent 挂着 backStart），
//     其中的 OnEnable/Awake 可能重启已退役玩法，所以「零激活」必须由**结构**保证而不是靠自觉：
//     本项目把这条钉成静态负例门（Tools/PMBattleContentSceneFactsCheck 的 J 段）。
//  2) **不拥有权威**：UI 只把「屏幕口径 [-1,1] 的摇杆向量」交给宿主入口（本文件用委托绑定），
//     并只把宿主给的只读快照显示出来。它**不发任何 RPC**、**不写任何复制/权威字段**、
//     不做伤害/命中/胜负判定，也不读旧 HYLDStaticValue / BattleData 取"真值"。
//  3) **DS 禁止创建**：DS 是无头进程，没有可渲染界面。`Create` 按
//     `PMNetRuntime.IsDedicatedServer` 直接拒绝（返回 null + error，不抛）。
//
//  ---------------------------------------------------------------------------
//  与宿主（PMClientSessionHost / PMUnityMoverInput）的接口 —— 本文件**不改宿主**
//  ---------------------------------------------------------------------------
//  契约冻结的宿主入口是 `TrySetUiMove(float,float)` / `TryQueueUiAttack(bool,float,float)` /
//  `TryGetUiCombatSnapshot(out PMUiCombatSnapshot)`（输入组提供）。本文件**不引用宿主类型**，
//  而是用三个委托把两侧解耦：输入组落地前后本文件都能独立编译，主侧只要把一个适配 lambda 接上即可。
//  具体接线（含所需宿主行位）见报告 `Docs/plans/_play_ui_visual.md`「宿主接线」一节。
//
//  屏幕口径（与契约逐字一致，**不在这里换轴**）：
//    · 输入值域 [-1,1]，相对用户选定相机（世界 yaw −90）：screen up → world −X、screen right → +Z；
//    · uGUI 本地空间的 +Y **就是向上**，与冻结口径同向，因此本文件**不取反 Y**；
//    · 换轴到世界方向是宿主的事（PMClientSessionHost 的冻结入口），UI 只给屏幕向量。
//
//  ---------------------------------------------------------------------------
//  编译门（三条，缺一不可）
//  ---------------------------------------------------------------------------
//    · Tools/PMR4UnityCheck（真实 Unity 2019 DLL、显式 UNITY_EDITOR、真实 UnityEngine.UI.dll）
//      ⇒ 编译下面的**完整实现**（真实门里绝不跳过实现）；
//    · Tools/PMClientCheck / Tools/PMUnityGlueCheck（手写 UnityEngine 替身，**没有**
//      UNITY_EDITOR / UNITY_2019_1_OR_NEWER，也没有完整 UnityEngine.UI）
//      ⇒ 只编译下面的「替身编译面」（同一公开面，全部显式失败，不静默成功）；
//    · 正式 Player 构建里 UNITY_2019_1_OR_NEWER 存在 ⇒ 与 PMR4UnityCheck 走同一份完整实现。
//  这正是契约「真实 Unity 实现区用 #if UNITY_2019_1_OR_NEWER || UNITY_EDITOR 界定，
//  正式 Player 该宏存在，真实门里不跳过实现」的落地形式。
//
//  ---------------------------------------------------------------------------
//  T-LIVE3 / T-LIVE4（实机第二轮反馈，冻结接口见主计划 T-LIVE 段）
//  ---------------------------------------------------------------------------
//  问题一（T-LIVE3）：旧 `TouchLogic.OnJoystickMove` 在按住普通/大招摇杆时会把玩家角色子节点的
//      `LineRenderer` 画成「世界距离 + 散射扇形」（`enabled=true`，松开 `JoystickMoveEnd` 里
//      `enabled=false`），而新链只在松手时排一次攻击、按坑时没有任何可见瞄准。
//      本文件新增第 4 个**可选**委托出口 `setAim`（见 `PMUnityBattleAimInputHandler`）：按住/拖动
//      攻击摇杆时把**同一份屏幕向量**交给宿主做实时预览，松手/取消/停局立即隐隐藏；
//      它**不**发 RPC、**不**写任何权威，也**不**改变「松手才提交一次」的边沿语义（`QueueUiAttack` 不变）。
//      真正的世界空间画线在 `PMUnityBattleAimIndicator`（纯表现，DS/远端 SP 不创建）里，
//      方向/长度/扇形由宿主按同一份 `PMCombatWeaponPlanner.TryBuild` 计划给出；
//      本文件只提供零引擎依赖的 `PMUnityBattleAimMath`（距离 + 扇形折线纯数学）。
//  问题二（T-LIVE4）：当前大招能量是同根下面的纯色矩形条 + 22 号数字（实机看是一个大方块），
//      而旧 GameUI 的能量条是 FullBG 圆盘底图 + FullPower 的 `Image.Type.Filled` /
//      `FillMethod.Radial360` 径向填充 + UnFullImage 小图标。现改为**只读**复用这三张 Sprite
//      与旧节点布局/径向配置（`fillAmount = SuperEnergy / SuperEnergyMax`）。
//      HP/Mana 条与三根摇杆的只读/单次消费语义**不变**。
//
//  ---------------------------------------------------------------------------
//  已知边界（不许当成"完整旧 UI 还原"）
//  ---------------------------------------------------------------------------
//    · 旧摇杆是 EasyTouch 的 **GUI/Texture2D** 绘制（joystick.png / joystick2.png /
//      RadialJoy_Dead.png / Fulled.png），**不是 uGUI Sprite**；本文件只能复用旧 prefab 里
//      **真实存在且可验证**的 4 张 Sprite（FullBG / FullPower / UnFullImage / Gem 1）。
//      因此摇杆底盘/手柄是"用旧素材近似"而不是旧像素级还原 —— 详见报告。
//    · 旧 prefab 里没有血条/蓝条 Sprite（`RedValue`/`bluebgImage` 都是纯色 Image），
//      所以 HP/Mana 条是「旧几何 + 旧素材边框 + 纯色填充」，同样在报告里登记。
//    · 布局平移量直接copy自旧 prefab（ConstantPixelSize + scaleFactor 1），与旧 UI 一样是按
//      ~1920x1080 调的；极小分辨率下被 copy 的量表可能部分出屏（旧 UI 同样如此，不额外发明缩放）。
// ============================================================================

using System;
using UnityEngine;

namespace PMNet.Unity
{
    // =====================================================================================
    //  只读快照口径（UI 消费面）—— 顶层结构，供主侧把宿主快照适配进来
    // =====================================================================================

    /// <summary>
    /// 本局 UI 需要的**只读**快照（全部来自宿主复制值，UI 不自行推导任何权威量）。
    ///
    /// 字段顺序与契约冻结的 <c>PMUiCombatSnapshot</c> 一致（Hp/MaxHp/Mana/SuperEnergy/
    /// Dead/MatchEnded/NormalReady/SuperReady/LastReject）。本类型刻意与宿主类型**同名不同类**：
    /// 输入组尚未落地时本文件也要能独立编译，主侧用一个适配 lambda 转换即可。
    /// </summary>
    public struct PMUnityBattleUiStatus
    {
        /// <summary>本人当前 HP（DS 权威复制值）。</summary>
        public int Hp;

        /// <summary>本人 MaxHp（DS 权威复制值）。</summary>
        public int MaxHp;

        /// <summary>本人 Mana（OwnerOnly 复制值；上限 <c>BattleNumericConfig.ManaMax</c>）。</summary>
        public int Mana;

        /// <summary>本人大招能量（OwnerOnly 复制值；上限 <c>BattleNumericConfig.SuperEnergyMax</c>）。</summary>
        public int SuperEnergy;

        /// <summary>本人是否已死亡（DS 权威）。</summary>
        public bool Dead;

        /// <summary>本局是否已终局（DS 权威）。</summary>
        public bool MatchEnded;

        /// <summary>普通攻击此刻是否可用（宿主按身份核验/资源/冷却给出的判据，UI 不重算）。</summary>
        public bool NormalReady;

        /// <summary>大招此刻是否可用（同上；Unsupported 的英雄恒为 false）。</summary>
        public bool SuperReady;

        /// <summary>最近一次拒绝原因（本地 planner 或 DS 裁决）；null/空串表示无。</summary>
        public string LastReject;

        /// <summary>把全部字段置成"不可操作"的安全值（无快照/会话失败时的保守口径）。</summary>
        public static PMUnityBattleUiStatus Offline()
        {
            PMUnityBattleUiStatus status = new PMUnityBattleUiStatus();
            status.Hp = 0;
            status.MaxHp = 0;
            status.Mana = 0;
            status.SuperEnergy = 0;
            status.Dead = false;
            status.MatchEnded = false;
            status.NormalReady = false;
            status.SuperReady = false;
            status.LastReject = null;
            return status;
        }
    }

    /// <summary>
    /// 移动摇杆出口：把屏幕口径 [-1,1] 的摇杆向量交给宿主（对应冻结入口 <c>TrySetUiMove</c>）。
    /// 返回 false 表示宿主拒绝（非本局/冻结/死亡/非有限值），UI 只记计数、不做任何本地权威写。
    /// </summary>
    public delegate bool PMUnityBattleMoveInputHandler(float screenX, float screenY);

    /// <summary>
    /// 攻击摇杆出口：**只在松手那一个边沿**调用一次（对应冻结入口 <c>TryQueueUiAttack</c>）。
    /// <paramref name="isSuper"/> = true 走大招、false 走普通攻击；坐标是屏幕口径 [-1,1] 的瞄准向量。
    /// </summary>
    public delegate bool PMUnityBattleAttackInputHandler(bool isSuper, float screenX, float screenY);

    /// <summary>
    /// 只读快照查询（对应冻结入口 <c>TryGetUiCombatSnapshot</c>）。
    /// 返回 false 表示此刻没有可信快照（未入局/会话失败/换局中），UI 按 <see cref="PMUnityBattleUiStatus.Offline"/>
    /// 保守处理并清掉摇杆输入。
    /// </summary>
    public delegate bool PMUnityBattleStatusQuery(out PMUnityBattleUiStatus status);

    /// <summary>
    /// T-LIVE3 瞄准预览出口（可选绑定）：把攻击摇杆的**屏幕口径**坐标交给宿主做实时瞄准预览。
    ///
    /// 与 <see cref="PMUnityBattleAttackInputHandler"/> 的分工（这是本文件最容易搞混的一条）：
    ///   · 本委托在**按下/拖动**期间反复调用（值变化才调），只为"看得到瞄准方向"；
    ///   · <paramref name="active"/> = false 表示**隐藏**（松手 / 取消 / 停局），此时坐标无意义；
    ///   · 真正的一次攻击仍在松手那一个边沿由 <see cref="PMUnityBattleAttackInputHandler"/> 提交，
    ///     两者用**同一个屏幕向量**，因此预览方向与本次上行方向不可能分叉。
    ///
    /// 返回 false 表示宿主拒纳（非本局/冻结/死亡/非有限值）；UI 只记计数并允许下一次拖动重试。
    /// </summary>
    public delegate bool PMUnityBattleAimInputHandler(bool isSuper, bool active, float screenX, float screenY);

    /// <summary>
    /// T-LIVE3 瞄准指示的**纯数学**（零 Unity 依赖）。
    ///
    /// 放在 <c>#if</c> 之外，因此三个编译门都会类型检查它，
    /// `Tools/PMR4UnityAdapterTest` 会真正执行它（不是镜像）：距离与扇形折线都是纯函数。
    ///
    /// 它只做两件事，都直接对应冻结口径：
    ///   · <see cref="TryDistanceMeters"/>：`Spec.SpeedMps × Spec.LifetimeMs / 1000`（长度）；
    ///   · <see cref="TryBuildFanPolyline"/>：把 `Plan.Directions` 铺成旧 `TouchLogic` 那种
    ///     「中心 → 每股边 → 回中心」的扇形折线（单发退化为两点直线）。
    /// 它**不**做弹道模拟、**不**算伤害/命中，也不认识任何权威对象。
    /// </summary>
    public static class PMUnityBattleAimMath
    {
        /// <summary>
        /// 指示线长度的**有界**上限（米）。超过它的计划只可能是坏配置，
        /// 这里钳到上限而不是拒绘：宁可画短也不要画一条穿整个地图的线（且不会 hide 整条指示器）。
        /// </summary>
        public const float MaxAimDistanceMeters = 500f;

        /// <summary>扇形最大股数（= 计划单次攻击的最大弹数上限，本文件不引 PMCombat 保持零依赖）。</summary>
        public const int MaxFanDirections = 64;

        /// <summary>预览侧向半径的安全上限（米）；超限配置拒画覆盖带，不静默缩窄真实范围。</summary>
        public const float MaxPreviewHitRadiusMeters = 5f;

        /// <summary>
        /// T-AIM：标准玩家的**近似可命中侧向半径**，与 DS 数学验算同一加法口径：
        /// 本次权威弹 Spec.RadiusM + 标准玩家 CapsuleRadiusMeters + 验证容差 HitToleranceM。
        ///
        /// 宿主显式传入三个**生产常量/计划值**；这里不引用权威类型，方便独立运行真纯函数测试。
        /// 结果只是预览：目标缩放/竖直高度、权威历史和墙体阻挡会改变实际结果；
        /// 绝不是“画进带内就必命中”。拒 NaN/Inf/非正尺寸/负容差/过大总半径，
        /// 不用旧 ShootWidth（纯视觉参数）冒充判定值，也不静默钳成更窄的带。
        /// </summary>
        public static bool TryPreviewHitRadius(float projectileRadiusM, float standardTargetRadiusM,
                                               float hitToleranceM, out float radiusM)
        {
            radiusM = 0f;
            if (!PMUnityBattleControlMath.IsFinite(projectileRadiusM) || projectileRadiusM <= 0f
                || !PMUnityBattleControlMath.IsFinite(standardTargetRadiusM) || standardTargetRadiusM <= 0f
                || !PMUnityBattleControlMath.IsFinite(hitToleranceM) || hitToleranceM < 0f)
            {
                return false;
            }

            double sum = (double)projectileRadiusM + (double)standardTargetRadiusM + hitToleranceM;
            if (double.IsNaN(sum) || double.IsInfinity(sum) || sum <= 0.0
                || sum > MaxPreviewHitRadiusMeters)
            {
                return false;
            }

            radiusM = (float)sum;
            return true;
        }

        /// <summary>
        /// 由计划口径求指示线长度：<c>SpeedMps × LifetimeMs / 1000</c>（米）。
        ///
        /// 拒绝：非有限/非正速度、非正寿命、非有限结果。
        /// 有界：结果超过 <see cref="MaxAimDistanceMeters"/> 时钳到上限（不是拒绝）。
        /// </summary>
        public static bool TryDistanceMeters(float speedMps, int lifetimeMs, out float distance)
        {
            distance = 0f;

            if (!PMUnityBattleControlMath.IsFinite(speedMps) || speedMps <= 0f) { return false; }
            if (lifetimeMs <= 0) { return false; }

            double raw = (double)speedMps * (double)lifetimeMs / 1000.0;
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0.0) { return false; }

            if (raw > (double)MaxAimDistanceMeters) { raw = (double)MaxAimDistanceMeters; }

            distance = (float)raw;
            return true;
        }

        /// <summary>
        /// 把「原点 + N 股世界方向 + 长度」铺成**世界空间折线**（旧 `TouchLogic.OnJoystickMove` 的同一形状）：
        ///
        ///   · <paramref name="count"/> == 1（旧 `launchAngle == 0` 分支）⇒ 2 个点：
        ///     origin → origin + dir×distance；
        ///   · <paramref name="count"/> &gt; 1（旧扇形分支）⇒ `2N+2` 个点：
        ///     origin, edge0, origin, edge1, origin, …, edge(N−1), origin, origin。
        ///
        /// `positions` 以 **x,y,z 三元组**逐点写入，长度至少 `3 × 点数`（不够则拒绝，不写任何东西）。
        /// 股数必须 ∈ [1, <see cref="MaxFanDirections"/>]，所有方向必须有限且非零；
        /// 任何一支坏方向都让整条折线失败（**不画半条线**）。
        /// 它**不**归一化方向（计划的 Directions 已经是单位向量），也不改 y（所有点与 origin 同高）。
        /// </summary>
        public static bool TryBuildFanPolyline(float originX, float originY, float originZ,
                                              float[] dirX, float[] dirZ, int count,
                                              float distance, float[] positions, out int floatCount)
        {
            floatCount = 0;

            if (dirX == null || dirZ == null || positions == null) { return false; }
            if (!PMUnityBattleControlMath.IsFinite(originX) || !PMUnityBattleControlMath.IsFinite(originY)
                || !PMUnityBattleControlMath.IsFinite(originZ)) { return false; }
            if (!PMUnityBattleControlMath.IsFinite(distance) || distance <= 0f) { return false; }
            if (count <= 0 || count > MaxFanDirections) { return false; }
            if (dirX.Length < count || dirZ.Length < count) { return false; }

            int pointCount = count <= 1 ? 2 : count * 2 + 2;
            if (positions.Length < pointCount * 3) { return false; }

            for (int i = 0; i < count; i++)
            {
                if (!PMUnityBattleControlMath.IsFinite(dirX[i]) || !PMUnityBattleControlMath.IsFinite(dirZ[i]))
                {
                    return false;
                }

                if (dirX[i] == 0f && dirZ[i] == 0f) { return false; }
            }

            int write = 0;

            if (count <= 1)
            {
                write = WritePoint(positions, write, originX, originY, originZ);
                write = WritePoint(positions, write,
                    originX + dirX[0] * distance, originY, originZ + dirZ[0] * distance);
                floatCount = write;
                return true;
            }

            write = WritePoint(positions, write, originX, originY, originZ);
            for (int i = 0; i < count; i++)
            {
                write = WritePoint(positions, write,
                    originX + dirX[i] * distance, originY, originZ + dirZ[i] * distance);
                write = WritePoint(positions, write, originX, originY, originZ);
            }

            write = WritePoint(positions, write, originX, originY, originZ);
            floatCount = write;
            return true;
        }

        private static int WritePoint(float[] positions, int write, float x, float y, float z)
        {
            positions[write] = x;
            positions[write + 1] = y;
            positions[write + 2] = z;
            return write + 3;
        }
    }

    /// <summary>
    /// 摇杆纯数学（**零 Unity 依赖**）。
    ///
    /// 放在 <c>#if</c> 之外，因此三个编译门（真实 Unity 门 + 两个替身门）都会类型检查它；
    /// Tools/PMBattleContentSceneFactsCheck 的 J 段再用**同一套规则的可执行镜像**跑正负例
    /// （该工具只编 Program.cs、不能引用本文件，镜像与这里的口径一致性由 J 段的静态标记断言 + 报告
    ///  明确登记的"镜像而非同一实现"边界共同约束）。
    /// </summary>
    public static class PMUnityBattleControlMath
    {
        /// <summary>移动推送去重阈值（避免每帧把同一个摇杆值反复推给宿主）。</summary>
        public const float MovePushEpsilon = 0.0005f;

        /// <summary>攻击瞄准的最小有效长度；低于它视为"没有方向"，**不提交**这次攻击。</summary>
        public const float MinAimLength = 0.05f;

        /// <summary>是否有限值（NaN/±Inf 一律拒绝，绝不把坏值交给宿主或写进 Transform）。</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>钳到 [-1,1]（契约冻结的输入域）。</summary>
        public static float ClampUnit(float value)
        {
            if (value <= -1f) { return -1f; }
            if (value >= 1f) { return 1f; }
            return value;
        }

        /// <summary>二维模长（纯 double 计算，避免 Unity Vector2 依赖）。</summary>
        public static float Magnitude(float x, float y)
        {
            double magnitude = Math.Sqrt((double)x * (double)x + (double)y * (double)y);
            return (float)magnitude;
        }

        /// <summary>
        /// **纯**摇杆归一化：uGUI 本地偏移（相对摇杆中心，单位=canvas 单位）→ 冻结的屏幕口径。
        ///
        /// 规则：
        ///   · 非有限输入/非正半径 ⇒ 返回 false（调用方据此**不动**，不把坏值推出去）；
        ///   · 模长 ≤ deadZoneRatio × radius ⇒ 返回 true 且输出显式 (0,0)（旧 EasyJoystick 的死区语义）；
        ///   · uGUI 本地 +Y 向上 ⇒ screenY = +localY/radius（**不取反**），与契约
        ///     「screen up → world −X、screen right → +Z」的屏幕口径同向；
        ///   · 输出各自钳到 [-1,1]（超出半径的拖动被钳在最大幅值，不放大）。
        /// </summary>
        public static bool TryNormalizeStick(float localX, float localY, float radius,
                                             float deadZoneRatio, out float screenX, out float screenY)
        {
            screenX = 0f;
            screenY = 0f;

            if (!IsFinite(localX) || !IsFinite(localY)) { return false; }
            if (!IsFinite(radius) || radius <= 0f) { return false; }
            if (!IsFinite(deadZoneRatio) || deadZoneRatio < 0f) { deadZoneRatio = 0f; }

            float magnitude = Magnitude(localX, localY);
            if (!IsFinite(magnitude)) { return false; }

            if (magnitude <= deadZoneRatio * radius) { return true; }

            screenX = ClampUnit(localX / radius);
            screenY = ClampUnit(localY / radius);
            return true;
        }

        /// <summary>摇杆手柄的显示位置（钳在半径内；纯几何，用于视觉回中/贴边）。</summary>
        public static bool TryClampToRadius(float localX, float localY, float radius,
                                            out float clampedX, out float clampedY)
        {
            clampedX = 0f;
            clampedY = 0f;

            if (!IsFinite(localX) || !IsFinite(localY)) { return false; }
            if (!IsFinite(radius) || radius <= 0f) { return false; }

            float magnitude = Magnitude(localX, localY);
            if (!IsFinite(magnitude)) { return false; }
            if (magnitude <= radius || magnitude == 0f)
            {
                clampedX = localX;
                clampedY = localY;
                return true;
            }

            float scale = radius / magnitude;
            clampedX = localX * scale;
            clampedY = localY * scale;
            return true;
        }

        /// <summary>瞄准向量是否足够长（低于阈值不提交，避免必然被 DS/planner 拒的空方向）。</summary>
        public static bool IsMeaningfulAim(float screenX, float screenY, float minLength)
        {
            if (!IsFinite(screenX) || !IsFinite(screenY)) { return false; }
            if (!IsFinite(minLength) || minLength < 0f) { return false; }
            return Magnitude(screenX, screenY) >= minLength;
        }

        /// <summary>浮点近似相等（推送去重口径）。</summary>
        public static bool NearlyEqual(float a, float b, float epsilon)
        {
            return Math.Abs(a - b) <= epsilon;
        }
    }

#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR
    /// <summary>某一根摇杆的**视觉件**（没有状态语义，状态在 <see cref="JoystickState"/>）。</summary>
    internal sealed class JoystickVisual
    {
        /// <summary>命中区（自身就是射线目标）。</summary>
        public RectTransform Base;

        /// <summary>手柄（纯视觉，raycastTarget=false，避免抢命中）。</summary>
        public RectTransform Knob;

        /// <summary>底盘 Image（用于按 ready/dead 调透明度）。</summary>
        public UnityEngine.UI.Image BaseImage;

        /// <summary>手柄 Image（大招摇杆会在 ready 前后换 Sprite）。</summary>
        public UnityEngine.UI.Image KnobImage;
    }

    /// <summary>
    /// 一根摇杆的**输入状态**（多指安全 + 事件去重）。
    ///
    /// 语义表（J 段可执行镜像按同一张表跑正负例）：
    ///   事件                                   | _activePointer | 推送
    ///   ---------------------------------------|----------------|---------------------------
    ///   OnPointerDown（未激活、且可用）          | = pointerId    | 移动：按当前值；攻击：不推送
    ///   OnPointerDown（已激活，任意 pointerId）  | 不变           | 不推送（去重，不重置）
    ///   OnDrag（pointerId == 激活者）            | 不变           | 移动：按当前值；攻击：不推送
    ///   OnDrag（别的 pointerId）                 | 不变           | 不推送
    ///   OnPointerUp（pointerId == 激活者）       | = 无           | 移动：**清零**；攻击：**提交一次**
    ///   OnPointerUp（别的 pointerId / 已释放）   | 不变           | 不推送（去重）
    ///   Cancel()（死亡/终局/无快照/Dispose）      | = 无           | 移动：**清零**；攻击：**不提交**
    /// </summary>
    internal sealed class JoystickState
    {
        /// <summary>无激活手指的哨兵（pointerId 合法值非负）。</summary>
        public const int NoPointer = int.MinValue;

        public JoystickVisual Visual;

        /// <summary>true = 攻击瞄准摇杆（松手才提交），false = 移动摇杆（拖动即推送）。</summary>
        public bool IsAttack;

        /// <summary>攻击摇杆：true = 大招（FireSuper），false = 普通（FireNormal）。</summary>
        public bool IsSuper;

        /// <summary>本轮是否可用（由快照的 ready/dead/ended 决定；不可用时按下被忽略）。</summary>
        public bool Interactable;

        /// <summary>当前激活的手指（<see cref="NoPointer"/> 表示没有）。</summary>
        public int ActivePointer = NoPointer;

        /// <summary>最近一次有效输出（屏幕口径 [-1,1]）。</summary>
        public float ScreenX;
        public float ScreenY;

        /// <summary>攻击摇杆本轮推给宿主的瞄准向量是否已被记录下来（T-LIVE3 去重口径）。</summary>
        public bool AimPushed;

        /// <summary>最近一次推给宿主的瞄准屏幕向量（去重用；非按住时为 0）。</summary>
        public float AimScreenX;
        public float AimScreenY;

        /// <summary>手柄是否处于"被按住"的位移状态（仅诊断/视觉）。</summary>
        public bool Held { get { return ActivePointer != NoPointer; } }
    }

    /// <summary>
    /// T-PLAY4 的局内操控 UI：移动摇杆（PlayerMove）+ 普通攻击瞄准摇杆（FireNormal）+ 大招摇杆
    /// （FireSuper），外加本人 HP / Mana / 大招能量的**只读**显示。
    ///
    /// 它**不是**旧 UI 的完整迁移（见文件头「已知边界」），也不拥有任何权威：
    /// 一切判定仍在 PMCombatSession（DS），UI 只转发输入意图并显示复制值。
    ///
    /// 生命周期由主侧（会话宿主）驱动：
    ///   <c>Create(matchId, out error)</c> → （可选）<c>Bind(...)</c> → 每帧 <c>UpdateStatus()</c>
    ///   → <c>Dispose()</c>（Stop / 下一 Enter / 正常退场都走它；幂等）。
    /// </summary>
    public sealed class PMUnityBattleControls : MonoBehaviour, IDisposable
    {
        /// <summary>本文件是否编入了完整 uGUI 实现（真实门/Player 为 true；两个替身门为 false）。</summary>
        public const bool UguiImplementationCompiled = true;

        // ---------------------------------------------------------------- 只读资源口径（J 段门禁对着 prefab 文本核过）

        /// <summary>旧局内 UI 预制体的 Resources 键（源文件 `Client/Assets/HYLD1.0/Resources/Prefabs/GameUI.prefab`）。</summary>
        public const string LegacyPrefabResourceKey = "Prefabs/GameUI";

        /// <summary>旧 prefab 里大招能量条（Slider）节点名。</summary>
        public const string EnergyGaugeNodePath = "能量条";

        /// <summary>旧 prefab 里"本队"血条行节点名（左侧，scale x = +1；对队是 GemEnemyTeamUI，scale x = −1）。</summary>
        public const string TeamSelfNodePath = "GemSelfTeamUI";

        /// <summary>摇杆底盘 Sprite 的节点路径（FullBG.png，1030x1004 圆盘）。</summary>
        public const string EnergyFrameSpritePath = EnergyGaugeNodePath + "/Background";

        /// <summary>能量填充/摇杆手柄 Sprite 的节点路径（FullPower.png，1030x1004 圆盘）。</summary>
        public const string EnergyFillSpritePath = EnergyGaugeNodePath + "/Fill Area/Fill";

        /// <summary>大招"未满"图标 Sprite 的节点路径（UnFullImage.png，91x89 小圆）。</summary>
        public const string SuperNotFullSpritePath = EnergyGaugeNodePath + "/Image";

        /// <summary>队伍宝石图标 Sprite 的节点路径（Gem 1.png，87x98）。</summary>
        public const string TeamGemSpritePath = TeamSelfNodePath + "/Image";

        /// <summary>
        /// 旧 EasyJoystick 的作用半径（px）。来源：prefab 里 PlayerMove/FireNormal/FireSuper 三个
        /// EasyJoystick 组件的 `zoneRadius: 100`（J 段门禁逐字核对）。
        ///
        /// 为什么不运行期读：`EasyJoystick` 是旧类型，本文件不得引用它；反射读私有序列化字段在
        /// IL2CPP 下不可靠。因此取"门禁核对过的常量"，而不是发明新数值。
        /// </summary>
        public const float LegacyJoystickZoneRadiusPixels = 100f;

        /// <summary>
        /// 旧 EasyJoystick 死区比例 = `deadZone: 20` / `zoneRadius: 100`。
        /// 死区内输出显式 (0,0)（旧摇杆同语义）。派生口径由 J 段门禁守着。
        /// </summary>
        public const float LegacyJoystickDeadZoneRatio = 0.2f;

        /// <summary>旧 PlayerMove 的 `joyAnchor: 7` = JoystickAnchor.LowerLeft（屏幕左下）。</summary>
        public const int LegacyMoveJoystickAnchor = 7;

        /// <summary>旧 FireNormal / FireSuper 的 `joyAnchor: 9` = JoystickAnchor.LowerRight（屏幕右下）。</summary>
        public const int LegacyFireJoystickAnchor = 9;

        /// <summary>旧 prefab 里 FireSuper 的 `m_IsActive: 0`（未满时隐藏）——新 UI 用 SuperReady 驱动同样语义。</summary>
        public const bool LegacyFireSuperStartsInactive = true;

        /// <summary>摇杆中心距屏幕角的边距（canvas 单位）。旧 EasyTouch 不含此量，是本 UI 的显式选择。</summary>
        private const float JoystickCornerMargin = 40f;

        /// <summary>手柄视觉直径 / 底盘直径。</summary>
        private const float KnobDiameterRatio = 0.45f;

        /// <summary>
        /// T-LIVE4：能量盘“未满”小图标的收窄比（相对旧 `能量条/Image` 的 125.32×126.76 几何）。
        /// 旧值本身是盘面尺寸，直接拿来会盖满盤面；本实现按报告登记的派生口径收窄。
        /// </summary>
        private const float EnergyIconScale = 0.4f;

        /// <summary>Canvas 排序序（高于大厅 UI，保证本局操控不被大厅 Canvas 盖住；不修改大厅 Canvas 本体）。</summary>
        private const int CanvasSortingOrder = 100;

        /// <summary>提示文本字体（Unity 2019 内建 Arial；取不到时只影响文字，不影响摇杆与血条）。</summary>
        private const string BuiltinFontName = "Arial.ttf";

        /// <summary>宿主 GameObject 名（Hierarchy 里一眼可辨；不属于任何业务场景）。</summary>
        private const string HostName = "[PMUnityBattleControls]";

        /// <summary>拒绝原因文本最大长度（UI 不做换行排版，超出截断）。</summary>
        private const int MaxNoticeChars = 64;

        // ---------------------------------------------------------------- 只读快照与输入出口

        private PMUnityBattleMoveInputHandler _setMove;
        private PMUnityBattleAttackInputHandler _queueAttack;
        private PMUnityBattleStatusQuery _queryStatus;

        /// <summary>T-LIVE3：瞄准预览出口（可选；null = 未绑定，UI 不推送预览）。</summary>
        private PMUnityBattleAimInputHandler _setAim;

        // ---------------------------------------------------------------- 运行状态

        private string _matchId = string.Empty;
        private bool _disposed;
        // 宿主 PumpActive 与 Unity Update 可能在同一帧各刷新一次；就绪判定会构造攻击计划，
        // 同帧只做一次快照读取，避免 Unity 2019 非增量 GC 的无意义分配。
        private int _lastStatusFrame = -1;

        private GameObject _canvasObject;
        private Canvas _canvas;
        private bool _ownsEventSystem;
        private UnityEngine.EventSystems.EventSystem _ownEventSystem;

        private JoystickState _moveStick;
        private JoystickState _normalStick;
        private JoystickState _superStick;

        private RectTransform _hpFill;
        private RectTransform _manaFill;

        /// <summary>T-LIVE4：大招能量的**径向**填充（旧 FullPower 图，Type.Filled / Radial360）。</summary>
        private UnityEngine.UI.Image _energyFillImage;

        /// <summary>T-LIVE4：大招“未满”小图标（旧 UnFullImage 图）。</summary>
        private UnityEngine.UI.Image _energyIcon;
        private UnityEngine.UI.Image _superKnobImage;
        private UnityEngine.UI.Text _hpText;
        private UnityEngine.UI.Text _manaText;
        private UnityEngine.UI.Text _energyText;
        private UnityEngine.UI.Text _noticeText;

        private Sprite _frameSprite;
        private Sprite _fillSprite;
        private Sprite _superNotFullSprite;
        private Sprite _gemSprite;

        private bool _fontAvailable;
        private bool _legacyPrefabRootWasInactive;
        private bool _legacyCanvasScalerCopied;
        private PMUnityBattleControlsPointerRelay _pointerRelay;

        // ---- 最近一次显示 / 推送（去重口径）----

        private bool _statusAvailable;
        private PMUnityBattleUiStatus _status = PMUnityBattleUiStatus.Offline();
        private int _visualKey = int.MinValue;
        private float _lastPushedMoveX = float.NaN;
        private float _lastPushedMoveY = float.NaN;
        private string _lastNotice = string.Empty;

        // ---- 可观测计数（只增；心跳/门禁/报告对账用）----

        /// <summary><see cref="UpdateStatus"/> 调用次数（Unity 自动 Update 与宿主显式调用都算）。</summary>
        public int StatusTicks { get; private set; }

        /// <summary>快照不可用的帧数（未入局/会话失败/换局中；这些帧按保守口径禁用输入）。</summary>
        public int StatusUnavailableTicks { get; private set; }

        /// <summary>真正推给宿主的移动向量次数（已去重）。</summary>
        public int MovePushCount { get; private set; }

        /// <summary>宿主拒绝移动推送的次数（例如非本局/冻结）。</summary>
        public int MovePushRejectedCount { get; private set; }

        /// <summary>真正交给宿主的攻击次数（松手边沿，已去重）。</summary>
        public int AttackQueueCount { get; private set; }

        /// <summary>宿主拒绝攻击入队的次数。</summary>
        public int AttackQueueRejectedCount { get; private set; }

        /// <summary>因瞄准向量过短/非有限而被 UI 自己抑制的攻击次数（不提交，避免必然被拒）。</summary>
        public int AttackSuppressedCount { get; private set; }

        /// <summary>因死亡/终局/无快照而取消摇杆的次数（这些路径**不**提交攻击）。</summary>
        public int CancelCount { get; private set; }

        /// <summary>T-LIVE3：推给宿主的瞄准预览次数（值变化才推）。</summary>
        public int AimPushCount { get; private set; }

        /// <summary>T-LIVE3：宿主拒纳瞄准预览的次数（非本局/冻结/死亡/非有限值）。</summary>
        public int AimPushRejectedCount { get; private set; }

        /// <summary>T-LIVE3：隐藏瞄准线的次数（松手/取消/停局）。</summary>
        public int AimHideCount { get; private set; }

        /// <summary>是否已释放。</summary>
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>本局对局 ID（对账用）。</summary>
        public string MatchId { get { return _matchId; } }

        /// <summary>旧 prefab 根在本端加载时是否确实是 inactive（诊断：契约要求它就是 inactive）。</summary>
        public bool LegacyPrefabRootWasInactive { get { return _legacyPrefabRootWasInactive; } }

        /// <summary>是否成功copy了旧 CanvasScaler 的缩放口径（false = 用了等价默认值，已登记）。</summary>
        public bool LegacyCanvasScalerCopied { get { return _legacyCanvasScalerCopied; } }

        /// <summary>内建字体是否可用（不可用时只少文字，摇杆与血条不受影响）。</summary>
        public bool FontAvailable { get { return _fontAvailable; } }

        /// <summary>最近一次显示用的快照（诊断；UI 从不改写它）。</summary>
        public PMUnityBattleUiStatus LastStatus { get { return _status; } }

        /// <summary>最近一次是否有可信快照。</summary>
        public bool StatusAvailable { get { return _statusAvailable; } }

        /// <summary>本 UI 自己的 Canvas（只用于诊断/对账；宿主不需要直接操作它）。</summary>
        public Canvas CanvasObject { get { return _canvas; } }

        // =================================================================================
        //  创建 / 释放
        // =================================================================================

        /// <summary>
        /// 建出本局操控 UI。**失败一律返回 null 并给出原因**（不抛也不静默）：宿主据此把本次入局
        /// 显式失败并清理，而不是"静默地没有 UI"。
        ///
        /// 失败口径（契约「旧 GameUI 缺失/无 Sprite 明确失败，不伪装绿色」）：
        ///   · DS 进程 ⇒ 拒绝；旧 prefab 缺失 ⇒ 拒绝；任一必需 Sprite 缺失 ⇒ 拒绝（逐个点名）；
        ///   · 必需布局节点（能量条 / GemSelfTeamUI 的 RectTransform）缺失 ⇒ 拒绝。
        /// 字体缺失**不**拒绝（只少文字），但会在 <see cref="FontAvailable"/> 上可见。
        /// </summary>
        public static PMUnityBattleControls Create(string matchId, out string error)
        {
            error = null;

            // 契约：DS 禁创建（无头进程没有可渲染界面）。
            if (PMNetRuntime.IsDedicatedServer)
            {
                error = "DS 禁止创建局内操控 UI（契约：客户端专有，DS 不得创建）";
                return null;
            }

            GameObject host = null;
            try
            {
                // ---- 1) 只读加载旧 prefab（**只读**：绝不 Instantiate / 绝不激活）----
                GameObject legacy;
                try
                {
                    legacy = Resources.Load<GameObject>(LegacyPrefabResourceKey);
                }
                catch (Exception ex)
                {
                    error = "读取旧局内 UI 预制体异常（键 \"" + LegacyPrefabResourceKey + "\"）："
                            + ex.GetType().Name + " " + ex.Message;
                    return null;
                }

                if (legacy == null)
                {
                    error = "旧局内 UI 预制体缺失（Resources 键 \"" + LegacyPrefabResourceKey
                            + "\"）：T-PLAY4 只复用它的 Sprite/布局，不伪造完整 UI，也不回退旧脚本。";
                    return null;
                }

                // ---- 2) 只读取材：4 张必需 Sprite（缺失逐个点名）----
                Sprite frameSprite;
                Sprite fillSprite;
                Sprite superNotFullSprite;
                Sprite gemSprite;
                string spriteError;
                if (!TryResolveSprites(legacy, out frameSprite, out fillSprite, out superNotFullSprite,
                                       out gemSprite, out spriteError))
                {
                    error = spriteError;
                    return null;
                }

                // ---- 3) 只读取布局：能量条 / 本队血条行的 RectTransform，以及旧 CanvasScaler ----
                RectLayout gaugeLayout;
                RectLayout teamLayout;
                string layoutError;
                if (!TryReadRectLayout(legacy, EnergyGaugeNodePath, out gaugeLayout, out layoutError))
                {
                    error = layoutError;
                    return null;
                }

                if (!TryReadRectLayout(legacy, TeamSelfNodePath, out teamLayout, out layoutError))
                {
                    error = layoutError;
                    return null;
                }

                // T-LIVE4：能量表的底图与径向填充几何**也只读**自旧 prefab（不自行发明坐标）。
                RectLayout gaugeFrameLayout;
                if (!TryReadRectLayout(legacy, EnergyFrameSpritePath, out gaugeFrameLayout, out layoutError))
                {
                    error = layoutError;
                    return null;
                }

                RectLayout gaugeInnerLayout;
                if (!TryReadRectLayout(legacy, SuperNotFullSpritePath, out gaugeInnerLayout, out layoutError))
                {
                    error = layoutError;
                    return null;
                }

                EnergyFillConfig energyFillConfig;
                string fillConfigError;
                if (!TryReadEnergyFillConfig(legacy, out energyFillConfig, out fillConfigError))
                {
                    error = fillConfigError;
                    return null;
                }

                CanvasScalerScalars scaler;
                bool scalerCopied = TryReadCanvasScaler(legacy, out scaler);

                bool legacyRootInactive = !legacy.activeSelf;

                // ---- 4) 建我们自己的 Canvas（独立 root，不属于旧场景，也不碰大厅 Canvas）----
                host = new GameObject(HostName);
                UnityEngine.Object.DontDestroyOnLoad(host);

                PMUnityBattleControls controls = host.AddComponent<PMUnityBattleControls>();
                if (controls == null)
                {
                    UnityEngine.Object.Destroy(host);
                    error = "AddComponent<PMUnityBattleControls> 未返回组件";
                    return null;
                }

                controls._matchId = matchId == null ? string.Empty : matchId;
                controls._frameSprite = frameSprite;
                controls._fillSprite = fillSprite;
                controls._superNotFullSprite = superNotFullSprite;
                controls._gemSprite = gemSprite;
                controls._legacyPrefabRootWasInactive = legacyRootInactive;
                controls._legacyCanvasScalerCopied = scalerCopied;

                controls.BuildCanvas(scaler);

                // 事件系统：既有（大厅/HYLDStart）可用就借用；确实没有才建本局专属，并在 Dispose 收回。
                controls.EnsureEventSystem();

                controls.BuildControls(gaugeLayout, gaugeFrameLayout, gaugeInnerLayout, energyFillConfig, teamLayout);

                controls.RefreshVisuals(force: true);
                return controls;
            }
            catch (Exception ex)
            {
                // 「失败创建清理」：任何构造期异常都拆掉宿主对象，不留孤儿 GameObject。
                if (host != null)
                {
                    try { UnityEngine.Object.Destroy(host); }
                    catch (Exception) { }
                }

                error = "创建局内操控 UI 异常：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 绑定三个宿主出口（可多次调用；传 null 表示"这一路暂时没有"，UI 会保守地禁用相应功能）。
        ///
        /// 主侧接线示例（输入组落地后）：
        /// <code>
        ///   // PMClientSessionHost.Enter 里（创建 HUD 之后）：
        ///   ui = PMUnityBattleControls.Create(offer.MatchId, out uiError);
        ///   ui.Bind(host.TrySetUiMove, host.TryQueueUiAttack, QueryUiStatus);   // 三个方法组直接绑
        ///
        ///   // PumpActive 每帧（紧邻 UpdateCombatHud(session)）：
        ///   ui.UpdateStatus();
        ///
        ///   // Stop / ReleaseSession：
        ///   ui.Dispose();
        ///
        ///   static bool QueryUiStatus(out PMUnityBattleUiStatus status) {
        ///       PMUiCombatSnapshot snapshot;
        ///       if (!host.TryGetUiCombatSnapshot(out snapshot)) { status = PMUnityBattleUiStatus.Offline(); return false; }
        ///       status = new PMUnityBattleUiStatus { Hp = snapshot.Hp, MaxHp = snapshot.MaxHp, ... };
        ///       return true;
        ///   }
        /// </code>
        /// 若宿主方法的返回类型不是 bool（例如 void），用一个 lambda 包一层即可
        /// （<c>(x, y) => { host.TrySetUiMove(x, y); return true; }</c>）。
        /// </summary>
        public void Bind(PMUnityBattleMoveInputHandler setMove,
                         PMUnityBattleAttackInputHandler queueAttack,
                         PMUnityBattleStatusQuery queryStatus,
                         PMUnityBattleAimInputHandler setAim)
        {
            if (_disposed) { return; }

            if (setMove != null) { _setMove = setMove; }
            if (queueAttack != null) { _queueAttack = queueAttack; }
            if (queryStatus != null) { _queryStatus = queryStatus; }
            if (setAim != null) { _setAim = setAim; }
        }

        /// <summary>
        /// 每帧刷新：查只读快照 → 按 ready/dead/ended 决定摇杆可用性 → 刷新显示（全部去重）。
        ///
        /// 本类同时提供 Unity 自动 <see cref="Update"/> 作为安全网；宿主**推荐**在 PumpActive 里
        /// 紧邻 <c>UpdateCombatHud(session)</c> 显式调用本方法（顺序确定、不依赖 Unity 回调时序）。
        /// 两者都调也安全：本方法是幂等的（内部按状态去重）。
        /// </summary>
        public void UpdateStatus()
        {
            if (_disposed || _lastStatusFrame == Time.frameCount) { return; }
            _lastStatusFrame = Time.frameCount;

            StatusTicks++;

            PMUnityBattleUiStatus status;
            bool available = false;

            if (_queryStatus != null)
            {
                try
                {
                    available = _queryStatus(out status);
                }
                catch (Exception)
                {
                    // 宿主查询抛异常 ⇒ 当成"没有可信快照"（保守禁用），绝不把异常吞掉后继续用旧值。
                    available = false;
                    status = PMUnityBattleUiStatus.Offline();
                }
            }
            else
            {
                status = PMUnityBattleUiStatus.Offline();
            }

            if (!available)
            {
                StatusUnavailableTicks++;
                status = PMUnityBattleUiStatus.Offline();
            }

            _statusAvailable = available;
            _status = status;

            // 可用性口径（UI 不重算权威，只把快照里的判据翻译成"能不能按"）：
            //   · 移动：未死、未终局、且有宿主出口；
            //   · 普通攻击：同上 + NormalReady；
            //   · 大招：同上 + SuperReady（Unsupported 英雄恒 false ⇒ 摇杆禁用并显示"未满"图标）。
            bool alive = !status.Dead && !status.MatchEnded;
            bool moveEnabled = available && alive && _setMove != null;
            bool normalEnabled = available && alive && status.NormalReady && _queueAttack != null;
            bool superEnabled = available && alive && status.SuperReady && _queueAttack != null;

            ApplyInteractable(_moveStick, moveEnabled);
            ApplyInteractable(_normalStick, normalEnabled);
            ApplyInteractable(_superStick, superEnabled);

            // 任何"不可操作"的成因都要把摇杆输入清干净（死亡/终局/无快照/换局），
            // 否则会把上一帧的方向继续推给宿主（契约：停局清缓存）。
            if (!moveEnabled) { CancelStick(_moveStick, submit: false); }
            if (!normalEnabled) { CancelStick(_normalStick, submit: false); }
            if (!superEnabled) { CancelStick(_superStick, submit: false); }

            RefreshVisuals(force: false);
        }

        /// <summary>
        /// Unity 自动回调（安全网）。**不**在这里做任何权威写；与宿主显式调用等价（幂等）。
        /// </summary>
        private void Update()
        {
            UpdateStatus();
        }

        /// <summary>
        /// 释放：先清掉摇杆输入（不提交攻击），再销毁自己的 Canvas 根对象（连同三个控件）——
        /// 若事件系统是本局专属创建的，一并收回。
        ///
        /// **只释放自己**：不碰旧 prefab 资产、不碰大厅 Canvas、不碰会话/驱动/端点。幂等。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;

            // 释放前先把"输入意图"归零，避免宿主仍按上一帧的摇杆值继续移动（死亡/退局路径同纪律）。
            CancelStick(_moveStick, submit: false);
            CancelStick(_normalStick, submit: false);
            CancelStick(_superStick, submit: false);

            _setMove = null;
            _queueAttack = null;
            _queryStatus = null;
            _setAim = null;

            // 本局专属事件系统：只有当我们确实创建了它、且它仍是当前事件系统时才销毁（不误删别人的）。
            if (_ownsEventSystem && _ownEventSystem != null)
            {
                try
                {
                    if (UnityEngine.EventSystems.EventSystem.current == _ownEventSystem)
                    {
                        UnityEngine.EventSystems.EventSystem.current = null;
                    }

                    GameObject eventHost = _ownEventSystem.gameObject;
                    _ownEventSystem = null;
                    if (eventHost != null && eventHost != gameObject)
                    {
                        UnityEngine.Object.Destroy(eventHost);
                    }
                }
                catch (Exception) { }
            }

            _ownsEventSystem = false;

            if (_pointerRelay != null)
            {
                // 先摘掉转发目标（防止事件回调仍指回已释放的本组件）。
                _pointerRelay.Target = null;
                _pointerRelay = null;
            }

            GameObject canvasObject = _canvasObject;
            _canvasObject = null;
            _canvas = null;
            _moveStick = null;
            _normalStick = null;
            _superStick = null;
            _hpFill = null;
            _manaFill = null;
            _energyFillImage = null;
            _energyIcon = null;
            _superKnobImage = null;
            _hpText = null;
            _manaText = null;
            _energyText = null;
            _noticeText = null;

            GameObject host = null;
            try { host = gameObject; }
            catch (Exception) { }

            if (canvasObject != null && canvasObject != host)
            {
                try { UnityEngine.Object.Destroy(canvasObject); }
                catch (Exception) { }
            }

            if (host != null)
            {
                try { UnityEngine.Object.Destroy(host); }
                catch (Exception) { }
            }
        }

        /// <summary>单行诊断（门禁/心跳对账用；不含任何敏感数据）。</summary>
        public string Describe()
        {
            return "controls(match=" + _matchId
                   + " disposed=" + (_disposed ? 1 : 0)
                   + " status=" + (_statusAvailable ? 1 : 0)
                   + " ticks=" + StatusTicks
                   + " unavailable=" + StatusUnavailableTicks
                   + " movePush=" + MovePushCount
                   + " moveReject=" + MovePushRejectedCount
                   + " attack=" + AttackQueueCount
                   + " attackReject=" + AttackQueueRejectedCount
                   + " attackSuppressed=" + AttackSuppressedCount
                   + " aim=" + AimPushCount
                   + " aimReject=" + AimPushRejectedCount
                   + " aimHide=" + AimHideCount
                   + " cancel=" + CancelCount
                   + " hp=" + _status.Hp + "/" + _status.MaxHp
                   + " mana=" + _status.Mana
                   + " energy=" + _status.SuperEnergy
                   + " legacyInactive=" + (_legacyPrefabRootWasInactive ? 1 : 0)
                   + " scalerCopied=" + (_legacyCanvasScalerCopied ? 1 : 0)
                   + " font=" + (_fontAvailable ? 1 : 0) + ")";
        }

        // =================================================================================
        //  只读资源解析（绝不 Instantiate 旧 prefab）
        // =================================================================================

        /// <summary>取一张 Image 的 Sprite（按节点路径；节点或 Sprite 缺失返回 null）。</summary>
        private static Sprite TryReadSpriteAtPath(GameObject prefab, string path)
        {
            if (prefab == null) { return null; }

            Transform node = prefab.transform.Find(path);
            if (node == null) { return null; }

            UnityEngine.UI.Image image = node.GetComponent<UnityEngine.UI.Image>();
            if (image == null) { return null; }

            return image.sprite;
        }

        /// <summary>
        /// 按 <c>sprite.name</c> 在整棵旧 prefab 子树（含 inactive）里回退查找 —— 只作为
        /// "节点被改名/重排"的**兜底**，不改变"必须有 Sprite"的硬要求。
        /// </summary>
        private static Sprite TryFindSpriteByName(GameObject prefab, string spriteName)
        {
            if (prefab == null || string.IsNullOrEmpty(spriteName)) { return null; }

            UnityEngine.UI.Image[] images = prefab.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            if (images == null) { return null; }

            for (int i = 0; i < images.Length; i++)
            {
                UnityEngine.UI.Image image = images[i];
                if (image == null) { continue; }

                Sprite sprite = image.sprite;
                if (sprite == null) { continue; }
                if (string.Equals(sprite.name, spriteName, StringComparison.Ordinal)) { return sprite; }
            }

            return null;
        }

        /// <summary>
        /// 解析 4 张必需 Sprite：节点路径优先，<c>sprite.name</c> 兜底；仍然缺失则**逐个点名**失败。
        /// </summary>
        private static bool TryResolveSprites(GameObject prefab, out Sprite frameSprite, out Sprite fillSprite,
                                              out Sprite superNotFullSprite, out Sprite gemSprite,
                                              out string error)
        {
            frameSprite = TryReadSpriteAtPath(prefab, EnergyFrameSpritePath);
            if (frameSprite == null) { frameSprite = TryFindSpriteByName(prefab, "FullBG"); }

            fillSprite = TryReadSpriteAtPath(prefab, EnergyFillSpritePath);
            if (fillSprite == null) { fillSprite = TryFindSpriteByName(prefab, "FullPower"); }

            superNotFullSprite = TryReadSpriteAtPath(prefab, SuperNotFullSpritePath);
            if (superNotFullSprite == null) { superNotFullSprite = TryFindSpriteByName(prefab, "UnFullImage"); }

            gemSprite = TryReadSpriteAtPath(prefab, TeamGemSpritePath);
            if (gemSprite == null) { gemSprite = TryFindSpriteByName(prefab, "Gem 1"); }

            error = null;
            string missing = string.Empty;

            if (frameSprite == null) { missing += " " + EnergyFrameSpritePath + "(FullBG)"; }
            if (fillSprite == null) { missing += " " + EnergyFillSpritePath + "(FullPower)"; }
            if (superNotFullSprite == null) { missing += " " + SuperNotFullSpritePath + "(UnFullImage)"; }
            if (gemSprite == null) { missing += " " + TeamGemSpritePath + "(Gem 1)"; }

            if (missing.Length != 0)
            {
                error = "旧局内 UI 预制体的必需 Sprite 缺失（节点路径与 sprite.name 两种查找都未命中）："
                        + missing
                        + "。T-PLAY4 只复用可验证的 Sprite，不伪造图形、不伪装成功。";
                return false;
            }

            return true;
        }

        /// <summary>旧 prefab 里某个节点的 RectTransform 布局值（只读拷到新控件上）。</summary>
        private struct RectLayout
        {
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector3 LocalScale;
        }

        /// <summary>
        /// T-LIVE4：只读自旧 `能量条/Fill Area/Fill` 的**径向**填充配置。
        ///
        /// 为什么要把它作为“硬前置”：实机反馈里新链把能量画成纯色矩形，而旧资源里
        /// `Fill` 本来就是 `Image.Type.Filled` + `FillMethod.Radial360`（J1b 对着 YAML 核过）。
        /// 与其自己写死“径向”，不如把旧资源的配置**只读**拿过来并校验，
        /// 形态不符时**显式失败**（不把旧资源当作纯色块，也不伪造一个圆形）。
        /// </summary>
        private struct EnergyFillConfig
        {
            public UnityEngine.UI.Image.FillMethod FillMethod;
            public int FillOrigin;
            public bool FillClockwise;
            public Color FillColor;
        }

        /// <summary>
        /// 只读旧能量条填充配置；不是 Filled/Radial360 即失败（**fail closed**，不降级成纯色块）。
        /// </summary>
        private static bool TryReadEnergyFillConfig(GameObject prefab, out EnergyFillConfig config,
                                                   out string error)
        {
            config = new EnergyFillConfig();
            config.FillMethod = UnityEngine.UI.Image.FillMethod.Radial360;
            config.FillOrigin = 0;
            config.FillClockwise = true;
            config.FillColor = new Color(1f, 1f, 1f, 1f);
            error = null;

            Transform node = prefab != null ? prefab.transform.Find(EnergyFillSpritePath) : null;
            UnityEngine.UI.Image fill = node != null ? node.GetComponent<UnityEngine.UI.Image>() : null;

            if (fill == null)
            {
                error = "旧局内 UI 预制体缺少能量填充节点 \"" + EnergyFillSpritePath + "\" 或其 Image 组件："
                        + "T-LIVE4 的径向配置只读自旧资源，不自行发明、也不降级成纯色块。";
                return false;
            }

            if (fill.type != UnityEngine.UI.Image.Type.Filled
                || fill.fillMethod != UnityEngine.UI.Image.FillMethod.Radial360)
            {
                error = "旧 能量条 的 Fill 不是 Filled/Radial360（实际 type=" + fill.type.ToString()
                        + " fillMethod=" + fill.fillMethod.ToString()
                        + "）：T-LIVE4 只复用可验证的径向填充配置，不把旧资源当作纯色块。";
                return false;
            }

            config.FillMethod = fill.fillMethod;
            config.FillOrigin = fill.fillOrigin;
            config.FillClockwise = fill.fillClockwise;
            config.FillColor = fill.color;
            return true;
        }

        /// <summary>只读读取某个节点的 RectTransform 布局（缺节点或不是 RectTransform ⇒ 显式失败）。</summary>
        private static bool TryReadRectLayout(GameObject prefab, string path, out RectLayout layout, out string error)
        {
            layout = new RectLayout();
            error = null;

            Transform node = prefab != null ? prefab.transform.Find(path) : null;
            if (node == null)
            {
                error = "旧局内 UI 预制体缺少布局节点 \"" + path + "\"：T-PLAY4 的布局只读自旧资源，"
                        + "不自行发明坐标。";
                return false;
            }

            RectTransform rect = node as RectTransform;
            if (rect == null)
            {
                error = "旧局内 UI 预制体的布局节点 \"" + path + "\" 不是 RectTransform（无法复用 uGUI 布局）。";
                return false;
            }

            layout.AnchorMin = rect.anchorMin;
            layout.AnchorMax = rect.anchorMax;
            layout.Pivot = rect.pivot;
            layout.AnchoredPosition = rect.anchoredPosition;
            layout.SizeDelta = rect.sizeDelta;
            layout.LocalScale = rect.localScale;
            return true;
        }

        /// <summary>旧 CanvasScaler 的缩放口径（只读copy；缺组件时用 Unity 默认值并在报告登记）。</summary>
        private struct CanvasScalerScalars
        {
            public UnityEngine.UI.CanvasScaler.ScaleMode ScaleMode;
            public float ReferencePixelsPerUnit;
            public float ScaleFactor;
            public Vector2 ReferenceResolution;
            public UnityEngine.UI.CanvasScaler.ScreenMatchMode ScreenMatchMode;
            public float MatchWidthOrHeight;
        }

        /// <summary>只读读取旧 prefab 根的 CanvasScaler（缺失返回 false，不失败：它不是"资源缺失"级问题）。</summary>
        private static bool TryReadCanvasScaler(GameObject prefab, out CanvasScalerScalars scaler)
        {
            scaler = new CanvasScalerScalars();
            scaler.ScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.ReferencePixelsPerUnit = 100f;
            scaler.ScaleFactor = 1f;
            scaler.ReferenceResolution = new Vector2(800f, 600f);
            scaler.ScreenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.MatchWidthOrHeight = 0f;

            if (prefab == null) { return false; }

            UnityEngine.UI.CanvasScaler scalerComponent = prefab.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scalerComponent == null) { return false; }

            scaler.ScaleMode = scalerComponent.uiScaleMode;
            scaler.ReferencePixelsPerUnit = scalerComponent.referencePixelsPerUnit;
            scaler.ScaleFactor = scalerComponent.scaleFactor;
            scaler.ReferenceResolution = scalerComponent.referenceResolution;
            scaler.ScreenMatchMode = scalerComponent.screenMatchMode;
            scaler.MatchWidthOrHeight = scalerComponent.matchWidthOrHeight;
            return true;
        }

        // =================================================================================
        //  建 UI（全部是我们自己的对象，零旧组件）
        // =================================================================================

        private void BuildCanvas(CanvasScalerScalars scaler)
        {
            _canvasObject = gameObject;

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            _canvas = canvas;

            UnityEngine.UI.CanvasScaler canvasScaler = _canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasScaler.uiScaleMode = scaler.ScaleMode;
            canvasScaler.referencePixelsPerUnit = scaler.ReferencePixelsPerUnit;
            canvasScaler.scaleFactor = scaler.ScaleFactor;
            canvasScaler.referenceResolution = scaler.ReferenceResolution;
            canvasScaler.screenMatchMode = scaler.ScreenMatchMode;
            canvasScaler.matchWidthOrHeight = scaler.MatchWidthOrHeight;

            // 射线：只命中我们自己的控件矩形（没有全屏背景图，所以不会吞掉其它 UI 的输入）。
            _canvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // 指针事件转发器：uGUI 的事件会从命中对象沿父链**冒泡**，因此把它挂在 Canvas 根上，
            // 就能用**一个**组件覆盖三根摇杆（命中谁由 pointerPressRaycast 路由）。
            _pointerRelay = _canvasObject.AddComponent<PMUnityBattleControlsPointerRelay>();
            _pointerRelay.Target = this;
        }

        /// <summary>
        /// 事件系统：借用既有的（HYLDStart 大厅 Canvas 那套）；确实没有才建本局专属并记下所有权。
        /// </summary>
        private void EnsureEventSystem()
        {
            UnityEngine.EventSystems.EventSystem current = UnityEngine.EventSystems.EventSystem.current;
            if (current != null && current.isActiveAndEnabled)
            {
                _ownsEventSystem = false;
                _ownEventSystem = null;
                return;
            }

            // 建在自己的 root 上（随 Dispose 一起销毁；不与大厅共用对象）。
            _ownEventSystem = _canvasObject.AddComponent<UnityEngine.EventSystems.EventSystem>();
            _canvasObject.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            _ownsEventSystem = true;
        }

        private void BuildControls(RectLayout gaugeLayout, RectLayout gaugeFrameLayout,
                                   RectLayout gaugeInnerLayout, EnergyFillConfig energyFillConfig,
                                   RectLayout teamLayout)
        {
            RectTransform rootRect = _canvasObject.GetComponent<RectTransform>();
            if (rootRect == null)
            {
                rootRect = _canvasObject.AddComponent<RectTransform>();
            }

            _fontAvailable = false;
            Font builtinFont = null;
            try
            {
                builtinFont = Resources.GetBuiltinResource<Font>(BuiltinFontName);
            }
            catch (Exception)
            {
                builtinFont = null;
            }

            _fontAvailable = builtinFont != null;

            // ---- 大招能量表（T-LIVE4）：几何/素材/径向配置**只读**自旧 prefab 的
            //      能量条(218.78×181.83) / Background(FullBG 圆盘) / Image(125.32×126.76) /
            //      Fill Area/Fill(FullPower，Type.Filled + FillMethod.Radial360)。
            //      比例用 Image.fillAmount = SuperEnergy / SuperEnergyMax，
            //      不再用纯色矩形条 + 22 号大数字（实机反馈的“大方块”）。----
            RectTransform gauge = CreateRect(rootRect, "EnergyGauge",
                gaugeLayout.AnchorMin, gaugeLayout.AnchorMax, gaugeLayout.Pivot,
                gaugeLayout.AnchoredPosition, gaugeLayout.SizeDelta);
            gauge.localScale = gaugeLayout.LocalScale;

            // 圆盘底图：旧 能量条/Background 的几何与 Sprite（preserveAspect 与旧 m_PreserveAspect=1 同口径）。
            RectTransform gaugeFrame = CreateRect(gauge, "Frame",
                gaugeFrameLayout.AnchorMin, gaugeFrameLayout.AnchorMax, gaugeFrameLayout.Pivot,
                gaugeFrameLayout.AnchoredPosition, gaugeFrameLayout.SizeDelta);
            gaugeFrame.localScale = gaugeFrameLayout.LocalScale;
            UnityEngine.UI.Image gaugeFrameImage = CreateImage(gaugeFrame, "FrameImage", _frameSprite,
                new Color(1f, 1f, 1f, 0.45f), raycast: false);
            gaugeFrameImage.preserveAspect = true;

            // 径向填充：旧 能量条/Image 的几何 + 旧 Fill 的径向配置（颜色也只读自旧 Fill）。
            _energyFillImage = CreateRadialFill(gauge, "EnergyFill", _fillSprite, gaugeInnerLayout,
                                                energyFillConfig);

            // “未满”小图标：同一中心轴上的 UnFullImage（几何按旧值收窄，属报告登记过的派生尺寸）。
            RectTransform gaugeIconRect = CreateRect(gauge, "EnergyIcon",
                gaugeInnerLayout.AnchorMin, gaugeInnerLayout.AnchorMax, gaugeInnerLayout.Pivot,
                gaugeInnerLayout.AnchoredPosition, gaugeInnerLayout.SizeDelta * EnergyIconScale);
            _energyIcon = CreateImage(gaugeIconRect, "EnergyIconImage", _superNotFullSprite, Color.white,
                raycast: false);

            if (_fontAvailable)
            {
                // 辅助小数值：贴**表底**，不盖住圆盘（实机反馈的“大号数字盖满图”负例）。
                RectTransform gaugeTextRect = CreateRect(gauge, "EnergyValue",
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    Vector2.zero, new Vector2(gaugeLayout.SizeDelta.x, 18f));
                _energyText = CreateText(gaugeTextRect, builtinFont, 14, TextAnchor.LowerCenter,
                                         new Color(1f, 1f, 1f, 0.9f));
            }

            // ---- 本队血条行（几何只读copy自旧 GemSelfTeamUI；对队行不建：快照只有本人值）----
            RectTransform hpBar = CreateRect(rootRect, "HpBar",
                teamLayout.AnchorMin, teamLayout.AnchorMax, teamLayout.Pivot,
                teamLayout.AnchoredPosition, teamLayout.SizeDelta);
            hpBar.localScale = Vector3.one;

            CreateImage(hpBar, "Frame", _frameSprite, new Color(1f, 1f, 1f, 0.35f), raycast: false);
            _hpFill = CreateFillBar(hpBar, new Color(0.90f, 0.25f, 0.25f, 0.95f));

            // 队伍宝石图标：几何沿用旧 `GemSelfTeamUI/Image` 的"行左端"语义（旧值 87x98 / x −181.9，
            // 对 53.24 高的条行偏大），这里按行高做了收窄 —— 属于报告登记过的派生尺寸，不是像素级还原。
            RectTransform gem = CreateRect(hpBar, "Gem", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                                           new Vector2(0.5f, 0.5f), new Vector2(28f, 0f),
                                           new Vector2(52f, 58f));
            CreateImage(gem, "GemImage", _gemSprite, Color.white, raycast: false);

            if (_fontAvailable)
            {
                _hpText = CreateText(hpBar, builtinFont, 24, TextAnchor.MiddleCenter,
                                     new Color(1f, 1f, 1f, 0.95f));
            }

            // 蓝条：旧 prefab 只有一行血条，故复用同一行几何并向下平移一行高（报告登记为派生布局）。
            Vector2 manaPosition = new Vector2(teamLayout.AnchoredPosition.x,
                                               teamLayout.AnchoredPosition.y - teamLayout.SizeDelta.y - 6f);
            RectTransform manaBar = CreateRect(rootRect, "ManaBar",
                teamLayout.AnchorMin, teamLayout.AnchorMax, teamLayout.Pivot,
                manaPosition, teamLayout.SizeDelta);
            manaBar.localScale = Vector3.one;

            CreateImage(manaBar, "Frame", _frameSprite, new Color(1f, 1f, 1f, 0.35f), raycast: false);
            _manaFill = CreateFillBar(manaBar, new Color(0.25f, 0.55f, 1f, 0.95f));

            if (_fontAvailable)
            {
                _manaText = CreateText(manaBar, builtinFont, 22, TextAnchor.MiddleCenter,
                                       new Color(1f, 1f, 1f, 0.95f));
            }

            // ---- 三根摇杆 ----
            // 位置口径来自旧 EasyJoystick 的 joyAnchor（PlayerMove=7 LowerLeft、FireNormal/FireSuper=9 LowerRight）
            // 与 zoneRadius=100；这些数值由 J 段门禁对着 prefab 文本核对。
            float radius = LegacyJoystickZoneRadiusPixels;
            float diameter = radius * 2f;
            float offset = radius + JoystickCornerMargin;

            Vector2 lowerLeft = new Vector2(0f, 0f);
            Vector2 lowerRight = new Vector2(1f, 0f);

            _moveStick = new JoystickState { IsAttack = false, IsSuper = false };
            _moveStick.Visual = CreateJoystick(rootRect, "PlayerMove", lowerLeft,
                                               new Vector2(offset, offset), diameter, _fillSprite);

            _normalStick = new JoystickState { IsAttack = true, IsSuper = false };
            _normalStick.Visual = CreateJoystick(rootRect, "FireNormal", lowerRight,
                                                 new Vector2(-offset, offset), diameter, _fillSprite);

            _superStick = new JoystickState { IsAttack = true, IsSuper = true };
            _superStick.Visual = CreateJoystick(rootRect, "FireSuper", lowerRight,
                                                new Vector2(-offset - diameter - JoystickCornerMargin, offset),
                                                diameter, _superNotFullSprite);
            _superKnobImage = _superStick.Visual.KnobImage;

            // ---- 提示行（拒绝原因 / 大招不可用说明）----
            if (_fontAvailable)
            {
                RectTransform noticeAnchor = CreateRect(rootRect, "Notice",
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(0f, 8f), new Vector2(900f, 28f));
                _noticeText = CreateText(noticeAnchor, builtinFont, 20, TextAnchor.LowerCenter,
                                         new Color(1f, 0.85f, 0.4f, 0.95f));
            }
        }

        /// <summary>建一根摇杆（底盘 + 手柄 + 指针事件入口）。</summary>
        private JoystickVisual CreateJoystick(RectTransform parent, string name, Vector2 anchor,
                                              Vector2 anchoredPosition, float diameter, Sprite knobSprite)
        {
            JoystickVisual visual = new JoystickVisual();

            RectTransform baseRect = CreateRect(parent, name, anchor, anchor, new Vector2(0.5f, 0.5f),
                                                anchoredPosition, new Vector2(diameter, diameter));
            visual.Base = baseRect;
            visual.BaseImage = CreateImage(baseRect, "Base", _frameSprite, new Color(1f, 1f, 1f, 0.55f),
                                           raycast: true);

            float knobDiameter = diameter * KnobDiameterRatio;
            RectTransform knobRect = CreateRect(baseRect, "Knob", new Vector2(0.5f, 0.5f),
                                                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                                Vector2.zero, new Vector2(knobDiameter, knobDiameter));
            visual.Knob = knobRect;
            visual.KnobImage = CreateImage(knobRect, "KnobImage", knobSprite, Color.white, raycast: false);

            return visual;
        }

        private static RectTransform CreateRect(RectTransform parent, string name,
                                                Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                                                Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
            rect.localScale = Vector3.one;
            return rect;
        }

        private static UnityEngine.UI.Image CreateImage(RectTransform parent, string name, Sprite sprite,
                                                        Color color, bool raycast)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;

            UnityEngine.UI.Image image = go.AddComponent<UnityEngine.UI.Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = raycast;
            image.type = UnityEngine.UI.Image.Type.Simple;
            return image;
        }

        /// <summary>
        /// T-LIVE4：建一个**径向填充**件（旧 FullPower 图 + 只读自旧 Fill 的 Filled/Radial360 配置）。
        ///
        /// 与 <see cref="CreateFillBar"/> 的区别：条状填充用锚点宽度表达比例，
        /// 径向填充只能写在 `Image.fillAmount` 上（旧 GameUI 的口径：`m_Type: 3` / `m_FillMethod: 4`）。
        /// 几何（锚点/尺寸/缩放）由调用方从旧 prefab **只读**拷来。
        /// </summary>
        private static UnityEngine.UI.Image CreateRadialFill(RectTransform parent, string name, Sprite sprite,
                                                            RectLayout layout, EnergyFillConfig config)
        {
            RectTransform rect = CreateRect(parent, name, layout.AnchorMin, layout.AnchorMax, layout.Pivot,
                                            layout.AnchoredPosition, layout.SizeDelta);
            rect.localScale = layout.LocalScale;

            UnityEngine.UI.Image image = CreateImage(rect, name + "Image", sprite, config.FillColor,
                                                    raycast: false);
            image.type = UnityEngine.UI.Image.Type.Filled;
            image.fillMethod = config.FillMethod;
            image.fillOrigin = config.FillOrigin;
            image.fillClockwise = config.FillClockwise;
            image.fillAmount = 0f;
            return image;
        }

        /// <summary>建一条"从左往右填充"的条（用锚点宽度表达比例，不依赖 Sprite 的 Filled 支持）。</summary>
        private static RectTransform CreateFillBar(RectTransform parent, Color color)
        {
            GameObject go = new GameObject("Fill", typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;

            UnityEngine.UI.Image image = go.AddComponent<UnityEngine.UI.Image>();
            image.sprite = null;                 // 纯色填充（旧 prefab 的 RedValue/bluebgImage 也是纯色 Image）
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static UnityEngine.UI.Text CreateText(RectTransform parent, Font font, int fontSize,
                                                      TextAnchor alignment, Color color)
        {
            GameObject go = new GameObject("Text", typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;

            UnityEngine.UI.Text text = go.AddComponent<UnityEngine.UI.Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        // =================================================================================
        //  指针事件（单组件 + 冒泡：命中任一子件都会落到本组件，再按命中的对象路由到对应摇杆）
        // =================================================================================

        private void OnDisable()
        {
            // 禁用/对象失活时立刻松掉所有摇杆（不提交攻击），避免"卡住的方向"。
            CancelStick(_moveStick, submit: false);
            CancelStick(_normalStick, submit: false);
            CancelStick(_superStick, submit: false);
        }

        /// <summary>把命中对象路由到对应的摇杆（命中手柄也会冒泡到本组件，故沿父链比对）。</summary>
        private bool TryResolveStick(GameObject hit, out JoystickState stick)
        {
            stick = null;
            if (hit == null) { return false; }

            Transform node = hit.transform;
            while (node != null)
            {
                if (_moveStick != null && _moveStick.Visual != null && node == _moveStick.Visual.Base)
                {
                    stick = _moveStick;
                    return true;
                }

                if (_normalStick != null && _normalStick.Visual != null && node == _normalStick.Visual.Base)
                {
                    stick = _normalStick;
                    return true;
                }

                if (_superStick != null && _superStick.Visual != null && node == _superStick.Visual.Base)
                {
                    stick = _superStick;
                    return true;
                }

                node = node.parent;
            }

            return false;
        }

        // uGUI 的指针事件由 EventSystem 发给命中对象，未实现时沿父链**冒泡**；
        // 因此这里只暴露普通方法给同文件内的转发器（PMUnityBattleControlsPointerRelay）调用，
        // 不需要给每根摇杆各挂一个 MonoBehaviour。
        internal void OnPointerDown_Forward(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_disposed || eventData == null) { return; }

            JoystickState stick;
            if (!TryResolveStick(eventData.pointerPressRaycast.gameObject, out stick)) { return; }

            // 去重：同一根摇杆已在按住状态时，后续 down 不重置、不重复推送。
            if (stick.Held) { return; }
            if (!stick.Interactable) { return; }

            stick.ActivePointer = eventData.pointerId;
            ApplyPointer(stick, eventData);
        }

        internal void OnDrag_Forward(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_disposed || eventData == null) { return; }

            JoystickState stick;
            if (!TryResolveStickByPointer(eventData.pointerId, out stick)) { return; }
            if (!stick.Held || stick.ActivePointer != eventData.pointerId) { return; }

            ApplyPointer(stick, eventData);
        }

        internal void OnPointerUp_Forward(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_disposed || eventData == null) { return; }

            JoystickState stick;
            if (!TryResolveStickByPointer(eventData.pointerId, out stick)) { return; }

            // 去重：不是当前激活手指标识的 up（或已经释放过）一律忽略。
            if (!stick.Held || stick.ActivePointer != eventData.pointerId) { return; }

            // 最后一次拖动位置也计入（避免"按下即抬起"时用过期方向）。
            ApplyPointer(stick, eventData);

            ReleaseStick(stick, submit: true);
            stick.ActivePointer = JoystickState.NoPointer;
        }

        private bool TryResolveStickByPointer(int pointerId, out JoystickState stick)
        {
            if (_moveStick != null && _moveStick.ActivePointer == pointerId) { stick = _moveStick; return true; }
            if (_normalStick != null && _normalStick.ActivePointer == pointerId) { stick = _normalStick; return true; }
            if (_superStick != null && _superStick.ActivePointer == pointerId) { stick = _superStick; return true; }

            stick = null;
            return false;
        }

        /// <summary>把指针位置换算成摇杆输出（移动立刻推送；攻击只记瞄准，松手才提交）。</summary>
        private void ApplyPointer(JoystickState stick, UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (stick == null || stick.Visual == null || stick.Visual.Base == null) { return; }

            Vector2 local;
            bool ok = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                stick.Visual.Base, eventData.position, null, out local);

            if (!ok)
            {
                return;
            }

            float screenX;
            float screenY;
            if (!PMUnityBattleControlMath.TryNormalizeStick(local.x, local.y,
                                                            LegacyJoystickZoneRadiusPixels,
                                                            LegacyJoystickDeadZoneRatio,
                                                            out screenX, out screenY))
            {
                // 非有限值：不动、不推（绝不把坏值交给宿主）。
                return;
            }

            stick.ScreenX = screenX;
            stick.ScreenY = screenY;

            // 手柄视觉：钳在半径内（超出半径的拖动不会让手柄飞出去）。
            float clampedX;
            float clampedY;
            if (PMUnityBattleControlMath.TryClampToRadius(local.x, local.y, LegacyJoystickZoneRadiusPixels,
                                                          out clampedX, out clampedY)
                && stick.Visual.Knob != null)
            {
                stick.Visual.Knob.anchoredPosition = new Vector2(clampedX, clampedY);
            }

            if (!stick.IsAttack)
            {
                PushMove(screenX, screenY);
            }
            else
            {
                // T-LIVE3：按住/拖动就把**同一份**屏幕向量交给宿主做实时瞄准预览
                //（松手才提交攻击；预览与上行方向来自同一个向量，不可能分叉）。
                PushAim(stick, true, screenX, screenY);
            }
        }

        /// <summary>松手/取消：回中手柄；移动清零；攻击只在 <paramref name="submit"/> 时提交一次。</summary>
        private void ReleaseStick(JoystickState stick, bool submit)
        {
            if (stick == null) { return; }

            if (stick.Visual != null && stick.Visual.Knob != null)
            {
                stick.Visual.Knob.anchoredPosition = Vector2.zero;
            }

            float screenX = stick.ScreenX;
            float screenY = stick.ScreenY;
            stick.ScreenX = 0f;
            stick.ScreenY = 0f;

            if (!stick.IsAttack)
            {
                PushMove(0f, 0f);
                return;
            }

            // T-LIVE3：释放/取消**立即隐藏**瞄准线（隐藏与提交解耦：隐藏本身不是一次攻击）。
            PushAim(stick, false, 0f, 0f);

            if (!submit)
            {
                return;
            }

            if (!PMUnityBattleControlMath.IsMeaningfulAim(screenX, screenY,
                                                           PMUnityBattleControlMath.MinAimLength))
            {
                // 没有方向：不提交（DS/planner 必然拒绝空方向，提交只会制造假拒绝）。
                AttackSuppressedCount++;
                SetNoticeLocal(stick.IsSuper ? "大招瞄准未给出方向，未提交" : "攻击瞄准未给出方向，未提交");
                return;
            }

            QueueAttack(stick.IsSuper, screenX, screenY);
        }

        /// <summary>释放摇杆（不提交攻击）；已在释放状态时是空操作（幂等，计入计数）。</summary>
        private void CancelStick(JoystickState stick, bool submit)
        {
            if (stick == null) { return; }

            if (!stick.Held)
            {
                // 仍要保证"没有卡住的方向"：如果上一次真的推过非零方向，这里补一次清零。
                if (!stick.IsAttack && !float.IsNaN(_lastPushedMoveX) && !(_lastPushedMoveX == 0f && _lastPushedMoveY == 0f))
                {
                    PushMove(0f, 0f);
                }

                return;
            }

            stick.ActivePointer = JoystickState.NoPointer;
            CancelCount++;
            ReleaseStick(stick, submit);
        }

        // =================================================================================
        //  推送（全部经宿主的委托出口；UI 永不直接发 RPC / 写权威字段）
        // =================================================================================

        /// <summary>移动推送（去重：只有值真的变了才推；清零不重复推）。</summary>
        private void PushMove(float screenX, float screenY)
        {
            if (_setMove == null) { return; }

            if (!float.IsNaN(_lastPushedMoveX)
                && PMUnityBattleControlMath.NearlyEqual(screenX, _lastPushedMoveX,
                                                        PMUnityBattleControlMath.MovePushEpsilon)
                && PMUnityBattleControlMath.NearlyEqual(screenY, _lastPushedMoveY,
                                                        PMUnityBattleControlMath.MovePushEpsilon))
            {
                return;
            }

            _lastPushedMoveX = screenX;
            _lastPushedMoveY = screenY;
            MovePushCount++;

            try
            {
                if (!_setMove(screenX, screenY)) { MovePushRejectedCount++; }
            }
            catch (Exception)
            {
                MovePushRejectedCount++;
            }
        }

        /// <summary>攻击入队（只在松手边沿调用；去重由摇杆状态机保证）。</summary>
        private void QueueAttack(bool isSuper, float screenX, float screenY)
        {
            if (_queueAttack == null) { return; }

            AttackQueueCount++;

            try
            {
                if (!_queueAttack(isSuper, screenX, screenY)) { AttackQueueRejectedCount++; }
            }
            catch (Exception)
            {
                AttackQueueRejectedCount++;
            }
        }

        /// <summary>
        /// T-LIVE3：把攻击摇杆的瞄准屏幕向量推给宿主（<paramref name="active"/>=false 表示隐藏）。
        ///
        /// 三条纪律：
        ///   · **值变化才推**（按住不动不会每帧刷宿主；与移动推送同一条去重口径）；
        ///   · **隐藏与提交解耦**：本方法只影响“看不看得到瞄准线”，从不提交攻击；
        ///   · 宿主拒纳（非本局/冻结/死亡/非有限值）时清掉本轮的已推标记，
        ///     使玩家“不动摇杆”时也会在下一次拖动事件里重试（而不是永久不显示）。
        /// </summary>
        private void PushAim(JoystickState stick, bool active, float screenX, float screenY)
        {
            if (stick == null) { return; }

            if (stick.AimPushed == active
                && (!active
                    || (PMUnityBattleControlMath.NearlyEqual(screenX, stick.AimScreenX,
                                                              PMUnityBattleControlMath.MovePushEpsilon)
                        && PMUnityBattleControlMath.NearlyEqual(screenY, stick.AimScreenY,
                                                               PMUnityBattleControlMath.MovePushEpsilon))))
            {
                return;
            }

            stick.AimPushed = active;
            stick.AimScreenX = active ? screenX : 0f;
            stick.AimScreenY = active ? screenY : 0f;

            if (active) { AimPushCount++; }
            else { AimHideCount++; }

            if (_setAim == null) { return; }

            try
            {
                if (!_setAim(stick.IsSuper, active, screenX, screenY))
                {
                    AimPushRejectedCount++;

                    // 拒纳 ⇒ 允许下一次拖动重试同一向量（不同步永久失效果）。
                    if (active) { stick.AimPushed = false; }
                }
            }
            catch (Exception)
            {
                AimPushRejectedCount++;
                if (active) { stick.AimPushed = false; }
            }
        }

        private void SetNoticeLocal(string notice)
        {
            _lastNotice = notice == null ? string.Empty : notice;
            _visualKey = int.MinValue;      // 强制刷新一次显示
        }

        // =================================================================================
        //  可用性与显示刷新（按"显示键"去重，避免每帧重写 UI 组件）
        // =================================================================================

        private static void ApplyInteractable(JoystickState stick, bool enabled)
        {
            if (stick == null) { return; }

            stick.Interactable = enabled;

            if (stick.Visual == null || stick.Visual.BaseImage == null) { return; }

            Color color = stick.Visual.BaseImage.color;
            color.a = enabled ? 0.55f : 0.20f;
            stick.Visual.BaseImage.color = color;
        }

        private void RefreshVisuals(bool force)
        {
            bool alive = !_status.Dead && _status.MatchEnded == false;

            float hpRatio = Ratio(_status.Hp, _status.MaxHp);
            float manaRatio = Ratio(_status.Mana, PMNet.Shared.BattleNumericConfig.ManaMax);
            float energyRatio = Ratio(_status.SuperEnergy, PMNet.Shared.BattleNumericConfig.SuperEnergyMax);

            string notice = ComposeNotice();

            int key = ComputeVisualKey(hpRatio, manaRatio, energyRatio, alive, notice);
            if (!force && key == _visualKey) { return; }
            _visualKey = key;

            SetFill(_hpFill, hpRatio);
            SetFill(_manaFill, manaRatio);
            SetRadialFill(_energyFillImage, energyRatio);

            // “未满”小图标：能量未满时显示（与旧 UnFullImage 的“未满”语义同向）。
            if (_energyIcon != null)
            {
                bool showIcon = energyRatio < 1f;
                if (_energyIcon.enabled != showIcon) { _energyIcon.enabled = showIcon; }
            }

            // 大招摇杆手柄：未满 → UnFullImage；可用 → FullPower（旧 SuperFireUI 的"未满/满"一对素材）。
            if (_superKnobImage != null)
            {
                Sprite wanted = (_status.SuperReady && alive) ? _fillSprite : _superNotFullSprite;
                if (_superKnobImage.sprite != wanted) { _superKnobImage.sprite = wanted; }
            }

            if (_hpText != null)
            {
                _hpText.text = _status.Hp.ToString() + " / " + _status.MaxHp.ToString();
            }

            if (_manaText != null)
            {
                _manaText.text = "蓝 " + _status.Mana.ToString();
            }

            if (_energyText != null)
            {
                _energyText.text = _status.SuperEnergy.ToString();
            }

            if (_noticeText != null && !string.Equals(_noticeText.text, notice, StringComparison.Ordinal))
            {
                _noticeText.text = notice;
            }
        }

        /// <summary>
        /// 提示行文案（优先级：无快照 &gt; 终局 &gt; 阵亡 &gt; 本地抑制原因 &gt; 宿主拒绝原因 &gt; 键位提示）。
        /// UI 只复述别人给的结论，不自行推断胜负/资源。
        /// </summary>
        private string ComposeNotice()
        {
            string notice;
            if (!_statusAvailable)
            {
                notice = "本局数据不可用（等待握手/会话已结束）";
            }
            else if (_status.MatchEnded)
            {
                notice = "本局已结束";
            }
            else if (_status.Dead)
            {
                notice = "已阵亡（等待终局结果）";
            }
            else if (!string.IsNullOrEmpty(_lastNotice))
            {
                notice = _lastNotice;
            }
            else if (!string.IsNullOrEmpty(_status.LastReject))
            {
                notice = _status.LastReject;
            }
            else
            {
                notice = "F 普攻 / G 大招（键盘兜底）";
            }

            if (notice != null && notice.Length > MaxNoticeChars)
            {
                notice = notice.Substring(0, MaxNoticeChars) + "…";
            }

            return notice == null ? string.Empty : notice;
        }

        private int ComputeVisualKey(float hpRatio, float manaRatio, float energyRatio, bool alive,
                                    string notice)
        {
            int key = 17;
            key = key * 31 + Quantize(hpRatio);
            key = key * 31 + Quantize(manaRatio);
            key = key * 31 + Quantize(energyRatio);
            key = key * 31 + (alive ? 1 : 0);
            key = key * 31 + (_status.SuperReady && alive ? 1 : 0);
            key = key * 31 + (_status.NormalReady && alive ? 1 : 0);
            key = key * 31 + Hash(notice);
            return key;
        }

        /// <summary>简单稳定字符串哈希（只用于“显示是否需要重刷”的去重键，不持久化、不进协议）。</summary>
        private static int Hash(string value)
        {
            if (string.IsNullOrEmpty(value)) { return 0; }

            int hash = 23;
            for (int i = 0; i < value.Length; i++)
            {
                hash = hash * 31 + value[i];
            }

            return hash;
        }

        /// <summary>把比例量化到 1/1000（避免浮点噪声让"显示键"每帧变化）。</summary>
        private static int Quantize(float ratio)
        {
            if (float.IsNaN(ratio) || float.IsInfinity(ratio)) { return 0; }
            if (ratio <= 0f) { return 0; }
            if (ratio >= 1f) { return 1000; }
            return (int)(ratio * 1000f);
        }

        private static float Ratio(int value, int max)
        {
            if (max <= 0) { return 0f; }
            if (value <= 0) { return 0f; }
            if (value >= max) { return 1f; }
            return (float)value / (float)max;
        }

        private static void SetFill(RectTransform fill, float ratio)
        {
            if (fill == null) { return; }

            float clamped = ratio;
            if (float.IsNaN(clamped) || float.IsInfinity(clamped)) { clamped = 0f; }
            if (clamped < 0f) { clamped = 0f; }
            if (clamped > 1f) { clamped = 1f; }

            fill.anchorMax = new Vector2(clamped, 1f);
        }

        /// <summary>
        /// T-LIVE4：径向填充的比例写入（`Image.fillAmount`）。
        ///
        /// 与 <see cref="SetFill"/> 的区别：条状填充用锚点宽度表达比例，而旧能量盘是
        /// `Image.Type.Filled + FillMethod.Radial360`，比例只能写在 `fillAmount` 上。
        /// 非有限值归 0（与条状同口径），且值未变化时不写引擎（避免每帧无意义重建）。
        /// </summary>
        private static void SetRadialFill(UnityEngine.UI.Image image, float ratio)
        {
            if (image == null) { return; }

            float clamped = ratio;
            if (float.IsNaN(clamped) || float.IsInfinity(clamped)) { clamped = 0f; }
            if (clamped < 0f) { clamped = 0f; }
            if (clamped > 1f) { clamped = 1f; }

            if (image.fillAmount != clamped) { image.fillAmount = clamped; }
        }
    }

    /// <summary>
    /// uGUI 指针事件的**转发器**：挂在本 UI 的 Canvas 根上，把命中任一子件的 down/drag/up
    /// 冒泡事件转成 <see cref="PMUnityBattleControls"/> 的调用。
    ///
    /// 为什么单独一个类型：uGUI 的 <c>ExecuteHierarchy</c> 只把事件交给"命中对象自身或父链上"
    /// 实现了 <c>IPointer*Handler</c> 的 <c>MonoBehaviour</c>；把转发器放在根上，就能用**一个**
    /// 组件覆盖三根摇杆（命中谁由 <c>pointerPressRaycast</c> 路由），不需要给每个控件挂脚本。
    /// </summary>
    public sealed class PMUnityBattleControlsPointerRelay : MonoBehaviour,
        UnityEngine.EventSystems.IPointerDownHandler,
        UnityEngine.EventSystems.IDragHandler,
        UnityEngine.EventSystems.IPointerUpHandler
    {
        /// <summary>被转发的目标（由 <see cref="PMUnityBattleControls"/> 在装配时设置）。</summary>
        internal PMUnityBattleControls Target;

        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (Target != null) { Target.OnPointerDown_Forward(eventData); }
        }

        public void OnDrag(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (Target != null) { Target.OnDrag_Forward(eventData); }
        }

        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (Target != null) { Target.OnPointerUp_Forward(eventData); }
        }
    }
#else
    /// <summary>
    /// 替身编译面（Tools/PMClientCheck、Tools/PMUnityGlueCheck 使用手写 UnityEngine 替身、
    /// **没有** UNITY_2019_1_OR_NEWER / UNITY_EDITOR、也没有完整 UnityEngine.UI）。
    ///
    /// 这里保留与真实实现**相同**的公开面，但每个入口都**显式失败**（返回 null / 空操作 + 原因），
    /// 绝不静默成功，也绝不试图在替身环境里"假装建出 UI"。
    /// 完整实现见上面的 <c>#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR</c> 分支。
    /// </summary>
    public sealed class PMUnityBattleControls : IDisposable
    {
        /// <summary>本文件是否编入了完整 uGUI 实现（替身门为 false）。</summary>
        public const bool UguiImplementationCompiled = false;

        /// <summary>替身面：不提供实现，显式失败（返回 null + 原因）。</summary>
        public static PMUnityBattleControls Create(string matchId, out string error)
        {
            error = "PMUnityBattleControls 的完整实现只在真实 Unity（UNITY_2019_1_OR_NEWER）或 Editor"
                    + "（UNITY_EDITOR）下编译；当前编译面是替身（Tools/PMClientCheck 或"
                    + " Tools/PMUnityGlueCheck），不提供 uGUI 实现，也不伪造成功。";
            return null;
        }

        /// <summary>替身面：无操作（不持有任何宿主出口）。</summary>
        public void Bind(PMUnityBattleMoveInputHandler setMove,
                         PMUnityBattleAttackInputHandler queueAttack,
                         PMUnityBattleStatusQuery queryStatus,
                         PMUnityBattleAimInputHandler setAim)
        {
        }

        /// <summary>替身面：无操作。</summary>
        public void UpdateStatus()
        {
        }

        /// <summary>替身面：无操作。</summary>
        public void Dispose()
        {
        }

        /// <summary>替身面：恒为已释放。</summary>
        public bool IsDisposed { get { return true; } }

        /// <summary>替身面：空串。</summary>
        public string MatchId { get { return string.Empty; } }

        /// <summary>替身面：诊断串。</summary>
        public string Describe()
        {
            return "controls(stub-build)";
        }
    }
#endif
}
