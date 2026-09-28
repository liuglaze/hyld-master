// ============================================================================
//  PMUnityMoverInput —— 测试新场景的本地输入采集 → PMMoverInput（契约 B2）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-network-contract.md 的 B2 一节：
//    · 「PMUnityMoverInput：提供 Sample() 返回 PMMoverInput（测试新场景 WASD/Horizontal,Vertical
//       +Space 边沿）；提供纯输入转换入口（用于可控测试，不引用旧 CommandManger/TouchLogic）」；
//    · 「World 坐标 Y-up，不带派生速度，不双镜像」；
//    · 「跳跃边沿应缓存直到有真实输入步消费，零 ms 帧不能丢边沿，补子步不能重复跳跃」；
//    · 「API 命名/消费方法报告明确」→ 见下方"状态表"。
//
//  **纯洁性红线**（mover-quick-start skill 红线 2）：本类只产出"玩家按键/摇杆能直接决定的值"。
//  不产出速度、加速度、命中结果、是否着地、距地距离、墙钟时间。
//  唯一有"推导"性质的是朝向（Yaw）——它是**输入意图**（玩家想朝哪），不是物理派生量；
//  且它由显式开关控制、公式写死、零输入时保持不变。
//
//  ---------------------------------------------------------------------------
//  跳跃边沿状态表（这是本类唯一有状态的语义，务必按下表使用）
//  ---------------------------------------------------------------------------
//    事件                                  | _jumpEdgeBuffered | JumpPressed(本次产出)
//    --------------------------------------|-------------------|---------------------
//    PollHardware 首次观测到这次按下 (*)     | true              | —
//      (*) 同一次按下内重复 PollHardware 不产生新边沿（幂等，见 PollHardware 注释）
//    NotifyJumpEdge()（UI/网络注入）        | true              | —
//    Sample(n) / Peek()                     | 不变              | _jumpEdgeBuffered
//    Consume(n)，n ∈ [1,50]（真实步）        | false             | —
//    Consume(0) 或 n ∉ [1,50]（零 ms 占位）  | 不变（不丢边沿）   | —
//    SampleAndConsume(n)，n ∈ [1,50]        | false（原子消费）  | _jumpEdgeBuffered
//    SampleAndConsume(0)                    | 不变（零 ms 不消费）| _jumpEdgeBuffered
//    Reset()                                | false             | —
//
//    由此得到两条要求的行为：
//      · 「零 ms 帧不能丢边沿」：0ms 占位帧不消费缓冲，边沿留给下一个真实步；
//      · 「补子步不能重复跳跃」：第一个真实子步消费后缓冲清空，后续子步产出 JumpPressed=false。
//
//  ---------------------------------------------------------------------------
//  宿主推荐用法（两种，任选其一）
//  ---------------------------------------------------------------------------
//    A) 精细控制（推荐给 DS/R4-B 宿主）：
//         void Update() { _input.PollHardware(); }        // 每宿主帧/每子步调都安全：
//                                                         // 同一次按下只锁住一个边沿
//         // …后续任意时刻，对每个将要真正模拟的输入步：
//         PMMoverInput frame = _input.Sample(stepMs);      // stepMs 为 0 占位时不会丢边沿
//         _input.Consume(stepMs);                          // 只有 1..50 的真实步才清缓冲
//    B) 一步到位：
//         PMMoverInput frame = _input.SampleAndConsume(stepMs);
//
//  ---------------------------------------------------------------------------
//  T-PLAY4：UI 摇杆 / 独立瞄准的屏幕→世界变换与来源选择（本轮新增）
//  ---------------------------------------------------------------------------
//  用户选型冻结（Docs/plans/net-architecture-migration.md「T-PLAY4 UI与输入接口冻结」）：
//    · UI 输入是相对**用户选定相机**的 [-1,1] 屏幕坐标，该相机世界 yaw = −90
//      （与 T-PLAY1 的 PMBattleCameraGeometry.DefaultYawDegrees 同源）；
//    · screen up(+Y) → world −X；screen right(+X) → world +Z；
//    · 非 finite / 超限数一律拒绝（**不**静默钳制）；接受范围内限幅单位向量；
//    · 摇杆释放（0,0）清移动；UI 摇杆在时**优先于**键盘平面输入。
//
//  公开面（本类新增，宿主 PMClientSessionHost 的 TrySetUiMove 只会用到这两个）：
//    · 纯变换（不读硬件/不改状态，可在 net8 逐条断言）：
//        TryScreenVectorToWorld(screenX, screenY, out worldX, out worldZ)  → 移动/通用平面向量
//        TryScreenAimDirection(screenX, screenY, out worldX, out worldZ)   → **单位**瞄准方向
//    · 有状态（主线程；由宿主/UI 调用，不直接读硬件）：
//        SetUiMoveWorld(worldX, worldZ) / ClearUiMove() / HasUiMove
//        UiMoveWorldX / UiMoveWorldZ / UiMoveAcceptCount / UiMoveClearCount
//
//  消费方式：与键盘**同一条** Sample()/SampleAndConsume()/Consume() 通道（来源选择在
//  ReadPlanar 内完成），UI 摇杆**不**新增第二条输入步进路径，也不绕开任何边沿消费纪律。
//  竖直轴与跳跃边沿不受 UI 摇杆影响；攻击瞄准**不进**本类——它只是“屏幕方向→世界方向”，
//  与移动朝向（YawDegrees）是两件事，由宿主分别消费。
//
//  ---------------------------------------------------------------------------
//  依赖面（刻意最小）
//  ---------------------------------------------------------------------------
//    只用 UnityEngine.Input + UnityEngine.KeyCode（+ System.Math）。**不用** Mathf / Vector3 /
//    Debug，因此 Tools/PMR4UnityAdapterTest 能用极小的 Input/KeyCode 桩件把本文件的
//    **纯转换与边沿语义**在 net8 上真跑（物理部分不在此文件，故不存在"假跑物理"）。
//
//  不引用旧输入链路：不做 CommandManger / TouchLogic / 摇杆死区滞回 / 瞄准线。
// ============================================================================

using System;
using PMNet.Mover;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 把本机输入采集为 <see cref="PMMoverInput"/>。只在客户端/编辑器测试场景里有意义；
    /// DS 的输入来自网络（B1 的 codec），不走本类。
    /// </summary>
    public sealed class PMUnityMoverInput
    {
        /// <summary>真实输入步的最小毫秒数（契约：上行 dt 1..50）。</summary>
        public const int MinRealStepMs = 1;

        /// <summary>真实输入步的最大毫秒数（契约：上行 dt 1..50）。</summary>
        public const int MaxRealStepMs = 50;

        // ---------------------------------------------------------------- UI 屏幕系常量（T-PLAY4）

        /// <summary>
        /// UI 屏幕坐标（相对用户选定相机世界 yaw −90）的**每分量**上限。
        ///
        /// 超出该上限、或出现 NaN/±Infinity 时，纯变换一律**返回 false**（不静默钳制）：
        /// 契约要求「全部拒 NaN/Infinity/超限数，不吞非法」——非法输入必须让调用方看得见，
        /// 而不是被本类悄悄改成某个合法值（那正是「UI 显示与上行方向不一致」的来源）。
        /// </summary>
        public const float ScreenInputMaxMagnitude = 1f;

        /// <summary>
        /// 上限比较的浮点容差。UI 端用 <c>dx / radius</c> 算归一化坐标时会带 1ulp 级误差，
        /// 不因此把一个合法的「推到底」误判成非法。
        /// </summary>
        public const float ScreenInputMagnitudeEpsilon = 1e-4f;

        /// <summary>
        /// 攻击瞄准方向的最小长度（归一化前的屏幕向量长度）。
        /// 低于它视为「没有方向」：攻击瞄准**不默认**取某个方向，直接拒绝。
        /// </summary>
        public const float AimDirectionMinLength = 1e-4f;

        // ---------------------------------------------------------------- 配置（构造后可直接改）

        /// <summary>
        /// 是否从"平面移动意图"推导朝向。默认 true：摇杆/按键给出方向时，朝向跟随该方向
        /// （0 输入时保持上一次朝向不变）。置 false 时朝向只来自 <see cref="ExternalYawDegrees"/>。
        /// </summary>
        public bool DeriveYawFromMoveInput = true;

        /// <summary>外部指定的朝向（度，绕 Y）。仅当不推导或平面输入为零时使用。</summary>
        public float ExternalYawDegrees;

        /// <summary>
        /// 竖直轴输入（世界 Y）。WASD/方向键**不**产生竖直轴；Flying 模式的竖直控制由宿主
        /// 或网络输入提供（本批不把 WASD 当竖直轴用，避免"双镜像式"的隐式语义）。
        /// </summary>
        public float ExternalVerticalInput;

        /// <summary>
        /// 轴死区（原始量，区间 [0,1)）。|值| ≤ 死区即视为 0；**不**做缩放补偿
        /// （缩放属于派生映射，本批刻意不做）。默认 0 = 完全不过滤。
        /// </summary>
        public float AxisDeadZone;

        /// <summary>
        /// 是否额外读取 Horizontal/Vertical 虚拟轴（默认 true）。
        /// 读虚拟轴在未配置该轴时会抛异常，因此读取失败会被"关掉该来源并计数"
        /// （见 <see cref="AxisAvailable"/> / <see cref="AxisReadFailures"/>）——
        /// 这不是静默降级：计数与开关都是公开可观测的，且按键来源仍然工作。
        /// </summary>
        public bool UseLegacyAxes = true;

        // ---------------------------------------------------------------- 状态

        private bool _jumpEdgeBuffered;
        private bool _spacePressObserved;
        private int _bufferedEdgePolls;
        private float _lastYawDegrees;
        private bool _axisUnavailable;
        private int _axisReadFailures;
        private int _pollCount;
        private int _sampleCount;
        private int _consumedEdges;
        private int _zeroStepSamples;
        private PMMoverInput _lastSampled;

        // ---- T-PLAY4：UI 摇杆（世界平面向量；来源选择在 ReadPlanar 内完成）----

        /// <summary>当前是否有活动的 UI 摇杆推动。false = 摇杆已释放（或从未设置）。</summary>
        private bool _hasUiMove;

        private float _uiMoveWorldX;
        private float _uiMoveWorldZ;
        private int _uiMoveSamples;
        private int _uiMoveClears;

        /// <summary>缓冲中是否有尚未被真实步消费的跳跃边沿。</summary>
        public bool HasBufferedJumpEdge { get { return _jumpEdgeBuffered; } }

        /// <summary>
        /// 缓冲中的跳跃边沿已存活多少个 <see cref="PollHardware"/> 周期（诊断用）。
        /// 该值只增不减，供宿主发现"边沿一直没被真实步消费"。
        /// </summary>
        public int BufferedJumpEdgePolls { get { return _bufferedEdgePolls; } }

        /// <summary>虚拟轴是否可用（首次读取失败后永久为 false；按键来源不受影响）。</summary>
        public bool AxisAvailable { get { return !_axisUnavailable && UseLegacyAxes; } }

        /// <summary>虚拟轴读取失败次数（异常被吞掉的次数都在这里，不是静默）。</summary>
        public int AxisReadFailures { get { return _axisReadFailures; } }

        /// <summary><see cref="PollHardware"/> 调用次数。</summary>
        public int PollCount { get { return _pollCount; } }

        /// <summary><see cref="Sample"/> / <see cref="SampleAndConsume"/> 调用次数。</summary>
        public int SampleCount { get { return _sampleCount; } }

        /// <summary>被真实步消费掉的跳跃边沿次数。</summary>
        public int ConsumedEdges { get { return _consumedEdges; } }

        /// <summary>零 ms 占位步的采样次数（契约要求它不消费边沿）。</summary>
        public int ZeroStepSamples { get { return _zeroStepSamples; } }

        /// <summary>最近一次产出的输入（诊断用；默认全零）。</summary>
        public PMMoverInput LastSampled { get { return _lastSampled; } }

        /// <summary>
        /// 当前是否有**活动**的 UI 摇杆推动（T-PLAY4）。摇杆释放（零输入）后为 false。
        ///
        /// 为 true 时平面移动来源 = UI（键盘 WASD/方向键与 Horizontal/Vertical 虚拟轴在这一帧被忽略），
        /// 竖直轴（<see cref="ExternalVerticalInput"/>）不受影响。
        /// </summary>
        public bool HasUiMove { get { return _hasUiMove; } }

        /// <summary>UI 摇杆当前的世界 X 分量（无输入时为 0；已限幅在单位向量内）。</summary>
        public float UiMoveWorldX { get { return _uiMoveWorldX; } }

        /// <summary>UI 摇杆当前的世界 Z 分量（无输入时为 0；已限幅在单位向量内）。</summary>
        public float UiMoveWorldZ { get { return _uiMoveWorldZ; } }

        /// <summary><see cref="SetUiMoveWorld"/> 被接受的次数（诊断；只增）。</summary>
        public int UiMoveAcceptCount { get { return _uiMoveSamples; } }

        /// <summary>UI 摇杆被清空（释放 / 停局 / 死亡 / 换局）的次数（诊断；只增）。</summary>
        public int UiMoveClearCount { get { return _uiMoveClears; } }

        // ---------------------------------------------------------------- 采集

        /// <summary>
        /// 读一次硬件并把**跳跃边沿**锁进缓冲。宿主应在每个宿主帧调用一次（早于任何
        /// "这帧要不要模拟"的决策），这样即使本帧最终不产生真实步，边沿也不会丢。
        /// 幂等：同一帧重复调用只会把边沿保持为 true，不会重复计数消费。
        /// </summary>
        public void PollHardware()
        {
            _pollCount++;

            bool spaceHeld = Input.GetKey(KeyCode.Space);
            bool spaceDown = Input.GetKeyDown(KeyCode.Space);

            // 键已松开 ⇒ 允许"下一次按下"再产生一个新边沿。
            if (!spaceHeld)
            {
                _spacePressObserved = false;
            }

            // **关键**：一次按下只锁一次。同一宿主帧内可能被调用多次（宿主按子步驱动），
            // 若不记住"这次按下已经观测过"，那么第一个子步消费掉边沿后，第二个子步的
            // PollHardware 会再次看到 GetKeyDown 为真并重新锁上 —— 表现就是"一次按键跳两次"。
            if (spaceDown && !_spacePressObserved)
            {
                _spacePressObserved = true;

                if (!_jumpEdgeBuffered)
                {
                    _jumpEdgeBuffered = true;
                    _bufferedEdgePolls = 0;
                }

                return;
            }

            if (_jumpEdgeBuffered)
            {
                _bufferedEdgePolls++;
            }
        }

        /// <summary>
        /// 外部注入一个跳跃边沿（UI 按钮、网络输入、自动化测试）。
        /// 与键盘边沿走同一套缓冲/消费语义，因此不会重复跳跃。
        /// </summary>
        public void NotifyJumpEdge()
        {
            if (!_jumpEdgeBuffered)
            {
                _jumpEdgeBuffered = true;
                _bufferedEdgePolls = 0;
            }
        }

        /// <summary>丢弃缓冲中的跳跃边沿（例如角色死亡、战斗结束、切场景）。</summary>
        public void ClearBufferedJumpEdge()
        {
            _jumpEdgeBuffered = false;
            _bufferedEdgePolls = 0;
        }

        /// <summary>
        /// 采样但**不消费**边沿（等价于 <c>Sample(0)</c>）。用于"只想看当前输入"的诊断路径。
        /// </summary>
        public PMMoverInput Peek()
        {
            return Sample(0);
        }

        /// <summary>
        /// 采样一个输入帧。**不消费**跳跃边沿（消费由 <see cref="Consume"/> 负责），
        /// 因此零 ms 占位帧不会丢边沿。
        /// </summary>
        /// <param name="stepMs">该输入帧的时长（毫秒）。只用于统计与诊断，不参与任何推导。</param>
        public PMMoverInput Sample(int stepMs)
        {
            PollHardware();
            return Build(stepMs);
        }

        /// <summary>
        /// 采样并（当且仅当 <paramref name="stepMs"/> 是 1..50 的真实步时）**原子消费**跳跃边沿。
        /// 这是最简单的正确用法：每个真实子步调一次，边沿只落在第一个真实子步上。
        /// </summary>
        public PMMoverInput SampleAndConsume(int stepMs)
        {
            PollHardware();
            PMMoverInput input = Build(stepMs);
            Consume(stepMs);
            return input;
        }

        /// <summary>
        /// 消费跳跃边沿：只有真实步（1..50ms）才清空缓冲；0ms 或越界值**不**清空
        /// （契约："零 ms 帧不能丢边沿"）。
        /// </summary>
        public void Consume(int stepMs)
        {
            if (!IsRealStepMs(stepMs))
            {
                return;
            }

            if (_jumpEdgeBuffered)
            {
                _consumedEdges++;
            }

            _jumpEdgeBuffered = false;
            _bufferedEdgePolls = 0;
        }

        /// <summary>清空全部本地状态（切场景/断线/测试用例之间）。不触碰任何全局物理/输入开关。</summary>
        public void Reset()
        {
            _jumpEdgeBuffered = false;
            _spacePressObserved = false;
            _bufferedEdgePolls = 0;
            _lastYawDegrees = 0f;
            _lastSampled = PMMoverInput.Empty();

            // T-PLAY4：UI 摇杆属于**本局输入状态**，换局/停局必须一起清掉（不带出上一局的推杆）。
            ClearUiMove();
        }

        // ---------------------------------------------------------------- UI 输入（T-PLAY4）

        /// <summary>
        /// **纯**变换：UI 屏幕向量（相对用户选定相机世界 yaw −90 的 [-1,1] 坐标）→ 世界平面向量。
        ///
        /// 轴约定（用户选型冻结，不得自行改成别的朝向）：
        ///   screen up(+Y) → world −X（旧相机水平前向 = 世界 −X，因为它世界 yaw = −90）；
        ///   screen right(+X) → world +Z（相机右向 = 世界 +Z）。
        ///
        /// 规则：
        ///   · NaN/±Infinity，或分量超出 [-1-ε, 1+ε] 的「超限数」→ **返回 false**
        ///     （不吞非法、不静默钳制成合法值，否则 UI 显示与上行方向会不一致）；
        ///   · 接受范围内**限幅单位向量**：模长 &gt; 1 时只缩长度、不改方向（斜向推杆不会更快）；
        ///   · 零输入是合法输入：返回 true 且 world = (0,0)，由调用方走「释放清移动」。
        ///
        /// 不读硬件、不改任何状态（与 <see cref="Convert"/> 同一条纯洁性纪律）。
        /// </summary>
        public static bool TryScreenVectorToWorld(float screenX, float screenY,
                                                  out float worldX, out float worldZ)
        {
            worldX = 0f;
            worldZ = 0f;

            // ① 非有限一律拒绝（含 NaN：与 NaN 的任何比较都为 false，因此必须先显式判掉）。
            if (float.IsNaN(screenX) || float.IsInfinity(screenX)) { return false; }
            if (float.IsNaN(screenY) || float.IsInfinity(screenY)) { return false; }

            // ② 超出屏幕坐标量程的分量 = 非法输入（不是「需要钳制」的输入）。
            float limit = ScreenInputMaxMagnitude + ScreenInputMagnitudeEpsilon;
            if (screenX > limit || screenX < -limit) { return false; }
            if (screenY > limit || screenY < -limit) { return false; }

            // ③ 轴变换：screen up → world −X；screen right → world +Z。
            float x = -screenY;
            float z = screenX;

            // ④ 限幅单位向量（只改长度，不改方向）。
            float magnitude = (float)Math.Sqrt((double)x * (double)x + (double)z * (double)z);
            if (magnitude > 1f)
            {
                x = (float)((double)x / (double)magnitude);
                z = (float)((double)z / (double)magnitude);
            }

            worldX = x;
            worldZ = z;
            return true;
        }

        /// <summary>
        /// **纯**变换：UI 瞄准摇杆的屏幕向量 → **单位**世界方向（供攻击的瞄准/枪口/planner/上行共用）。
        ///
        /// 与 <see cref="TryScreenVectorToWorld"/> 同一轴约定与同一套非法输入拒绝；
        /// 额外要求方向非零：屏幕向量长度 ≤ <see cref="AimDirectionMinLength"/> 时返回 false
        /// （「没有方向」不得被默认成某个方向，否则就是凭空替玩家选了一个弹道）。
        /// 返回的 (worldX, worldZ) 恒为单位向量（模长 1，除 0 以外）。
        /// </summary>
        public static bool TryScreenAimDirection(float screenX, float screenY,
                                                 out float worldX, out float worldZ)
        {
            worldX = 0f;
            worldZ = 0f;

            float x;
            float z;
            if (!TryScreenVectorToWorld(screenX, screenY, out x, out z)) { return false; }

            float length = (float)Math.Sqrt((double)x * (double)x + (double)z * (double)z);
            if (length <= AimDirectionMinLength) { return false; }

            worldX = (float)((double)x / (double)length);
            worldZ = (float)((double)z / (double)length);
            return true;
        }

        /// <summary>
        /// 设置 UI 摇杆的**世界**平面分量（宿主先用 <see cref="TryScreenVectorToWorld"/> 变换并校验）。
        ///
        /// 语义：模长 &gt; 1 时限幅到单位向量；零向量等价于 <see cref="ClearUiMove"/>（「释放摇杆清零」）。
        /// 非有限值直接抛异常（这里不接受坏值——校验应在纯变换入口完成）。
        /// 调用方必须是主线程（Unity 输入事件线程）。
        /// </summary>
        public void SetUiMoveWorld(float worldX, float worldZ)
        {
            RequireFinite(worldX, "uiMoveWorldX");
            RequireFinite(worldZ, "uiMoveWorldZ");

            float magnitude = (float)Math.Sqrt((double)worldX * (double)worldX + (double)worldZ * (double)worldZ);
            if (magnitude > 1f)
            {
                worldX = (float)((double)worldX / (double)magnitude);
                worldZ = (float)((double)worldZ / (double)magnitude);
            }
            else if (magnitude <= 0f)
            {
                ClearUiMove();
                return;
            }

            _uiMoveWorldX = worldX;
            _uiMoveWorldZ = worldZ;
            _hasUiMove = true;
            _uiMoveSamples++;
        }

        /// <summary>
        /// 清空 UI 摇杆（释放 / 停局 / 死亡 / 换局）。
        /// 清空后平面移动来源**立刻**回到键盘（不需要等到下一次采样）。
        /// </summary>
        public void ClearUiMove()
        {
            bool wasActive = _hasUiMove;
            _hasUiMove = false;
            _uiMoveWorldX = 0f;
            _uiMoveWorldZ = 0f;

            if (wasActive) { _uiMoveClears++; }
        }

        // ---------------------------------------------------------------- 纯转换（可离线测试）

        /// <summary>
        /// **纯**转换：把已读出的原始轴/朝向/边沿装成 <see cref="PMMoverInput"/>。
        /// 不读硬件、不改任何状态、不引用 Unity 类型——因此可在 net8 上逐条断言。
        ///
        /// 规则：
        ///   · 三个轴钳到 [-1,1]（契约冻结的输入域），**不做归一化**（归一化是模型的事）；
        ///   · 非有限值（NaN/±Inf）一律拒绝并抛异常（宁可显式失败，不发 NaN 进网络）；
        ///   · <c>Effects</c> / <c>Layers</c> / <c>RemovedLayerIds</c> 一律为 null：
        ///     它们是"宿主鉴权后的可信命令"，不属于输入设备能决定的值，由宿主另行注入。
        /// </summary>
        public static PMMoverInput Convert(float moveX, float moveZ, float moveY,
                                           float yawDegrees, bool jumpEdge)
        {
            RequireFinite(moveX, "moveX");
            RequireFinite(moveZ, "moveZ");
            RequireFinite(moveY, "moveY");
            RequireFinite(yawDegrees, "yawDegrees");

            PMMoverInput input = new PMMoverInput();
            input.MoveX = ClampUnit(moveX);
            input.MoveZ = ClampUnit(moveZ);
            input.MoveY = ClampUnit(moveY);
            input.YawDegrees = NormalizeDegrees(yawDegrees);
            input.JumpPressed = jumpEdge;
            input.Effects = null;
            input.Layers = null;
            input.RemovedLayerIds = null;
            return input;
        }

        /// <summary>
        /// **纯**朝向推导：平面移动意图 → 绕 Y 的朝向（度）。
        /// 与 Unity 的 Y-up 惯例一致：0 = 世界 +Z，90 = 世界 +X（公式 <c>atan2(x, z)</c>）。
        /// 零输入返回 0（调用方据"是否为零"决定要不要采用它）。
        /// **不做队伍镜像**（契约："不双镜像"；镜像发生在未来的输入/表现适配边界）。
        /// </summary>
        public static float DeriveYawDegrees(float moveX, float moveZ)
        {
            if (moveX == 0f && moveZ == 0f)
            {
                return 0f;
            }

            double radians = Math.Atan2(moveX, moveZ);
            return NormalizeDegrees((float)(radians * (180.0 / Math.PI)));
        }

        /// <summary>**纯**死区：|值| ≤ 死区 → 0，否则原样返回（不缩放补偿）。</summary>
        public static float ApplyDeadZone(float value, float deadZone)
        {
            if (deadZone <= 0f)
            {
                return value;
            }

            return Math.Abs(value) <= deadZone ? 0f : value;
        }

        /// <summary>**纯**判定：该毫秒数是否是"真实输入步"（1..50，契约冻结范围）。</summary>
        public static bool IsRealStepMs(int stepMs)
        {
            return stepMs >= MinRealStepMs && stepMs <= MaxRealStepMs;
        }

        /// <summary>**纯**角度归一化到 (-180, 180]，避免 181 与 -179 被当成不同朝向。</summary>
        public static float NormalizeDegrees(float degrees)
        {
            float value = degrees % 360f;
            if (value <= -180f) { value += 360f; }
            if (value > 180f) { value -= 360f; }
            return value;
        }

        // ---------------------------------------------------------------- 内部

        private PMMoverInput Build(int stepMs)
        {
            _sampleCount++;
            if (stepMs == 0) { _zeroStepSamples++; }

            float x;
            float z;
            ReadPlanar(out x, out z);

            float yaw;
            if (DeriveYawFromMoveInput && (x != 0f || z != 0f))
            {
                yaw = DeriveYawDegrees(x, z);
                _lastYawDegrees = yaw;
            }
            else
            {
                yaw = DeriveYawFromMoveInput ? _lastYawDegrees : ExternalYawDegrees;
            }

            PMMoverInput input = Convert(x, z, ExternalVerticalInput, yaw, _jumpEdgeBuffered);
            _lastSampled = input;
            return input;
        }

        private void ReadPlanar(out float x, out float z)
        {
            // T-PLAY4（来源选择）：UI 摇杆**优先**。
            //
            // 为真时键盘物理键与 Horizontal/Vertical 虚拟轴在本帧**都不参与**平面输入
            // （不叠加、也不各来一份）——否则会出现“UI 摇杆与键盘同时推”的混合位移，
            // 而玩家手上只有一个摇杆。竖直轴（ExternalVerticalInput）与跳跃边沿不受影响。
            // 摇杆释放（零输入）⇒ HasUiMove 为 false ⇒ 键盘立刻恢复，无需额外开关。
            if (_hasUiMove)
            {
                x = _uiMoveWorldX;
                z = _uiMoveWorldZ;
                return;
            }

            float deadZone = AxisDeadZone;

            // 键盘和旧虚拟轴都是「屏幕右/上」意图，必须与 UI 摇杆共用**同一**变换：
            // 屏上(+V)→世界−X、屏右(+H)→世界+Z。此前直接把 A/D 写 MoveX、W/S 写 MoveZ，
            // 导致实机 WASD 与摇杆方向旋转了 90°；不能只旋转键盘而漏掉 Horizontal/Vertical。
            float keyH = PickLarger(KeyAxis(KeyCode.A, KeyCode.D), KeyAxis(KeyCode.LeftArrow, KeyCode.RightArrow));
            float keyV = PickLarger(KeyAxis(KeyCode.S, KeyCode.W), KeyAxis(KeyCode.DownArrow, KeyCode.UpArrow));
            float screenH = ApplyDeadZone(keyH, deadZone);
            float screenV = ApplyDeadZone(keyV, deadZone);

            // 附加来源：Horizontal/Vertical 虚拟轴（未配置时会抛，故失败后关掉并计数）。
            if (UseLegacyAxes && !_axisUnavailable)
            {
                float axisH;
                float axisV;
                if (TryReadLegacyAxes(out axisH, out axisV))
                {
                    screenH = PickLarger(screenH, ApplyDeadZone(axisH, deadZone));
                    screenV = PickLarger(screenV, ApplyDeadZone(axisV, deadZone));
                }
            }

            // 单一轴约定在 TryScreenVectorToWorld；物理键与虚拟轴都走它，
            // 这样镜头映射以后若变更，不会再次出现键盘与摇杆的方向分叉。
            if (!TryScreenVectorToWorld(screenH, screenV, out x, out z))
            {
                throw new InvalidOperationException("PMUnityMoverInput: 键盘/虚拟轴屏幕向量非法");
            }
        }

        private bool TryReadLegacyAxes(out float x, out float z)
        {
            x = 0f;
            z = 0f;

            try
            {
                x = Input.GetAxisRaw("Horizontal");
                z = Input.GetAxisRaw("Vertical");
                return true;
            }
            catch (Exception)
            {
                // 未配置虚拟轴时 Unity 会抛。关掉该来源并计数（公开可观测，不是静默降级）；
                // 物理按键来源仍然工作，因此测试场景始终可用。
                _axisUnavailable = true;
                _axisReadFailures++;
                x = 0f;
                z = 0f;
                return false;
            }
        }

        private static float KeyAxis(KeyCode negative, KeyCode positive)
        {
            float value = 0f;
            if (Input.GetKey(negative)) { value -= 1f; }
            if (Input.GetKey(positive)) { value += 1f; }
            return value;
        }

        private static float PickLarger(float a, float b)
        {
            return Math.Abs(a) >= Math.Abs(b) ? a : b;
        }

        private static float ClampUnit(float v)
        {
            if (v <= -1f) { return -1f; }
            if (v >= 1f) { return 1f; }
            return v;
        }

        private static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException(
                    "PMUnityMoverInput: " + name + " 必须是有限值，实际 " + value.ToString("R"), name);
            }
        }
    }
}
