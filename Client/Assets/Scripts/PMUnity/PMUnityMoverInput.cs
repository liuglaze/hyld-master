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
            float deadZone = AxisDeadZone;

            // 主来源：物理按键（永不抛异常，因此在任何 InputManager 配置下都可用）。
            float keyX = PickLarger(KeyAxis(KeyCode.A, KeyCode.D), KeyAxis(KeyCode.LeftArrow, KeyCode.RightArrow));
            float keyZ = PickLarger(KeyAxis(KeyCode.S, KeyCode.W), KeyAxis(KeyCode.DownArrow, KeyCode.UpArrow));

            x = ApplyDeadZone(keyX, deadZone);
            z = ApplyDeadZone(keyZ, deadZone);

            // 附加来源：Horizontal/Vertical 虚拟轴（未配置时会抛，故失败后关掉并计数）。
            if (!UseLegacyAxes || _axisUnavailable)
            {
                return;
            }

            float axisX;
            float axisZ;
            if (!TryReadLegacyAxes(out axisX, out axisZ))
            {
                return;
            }

            x = PickLarger(x, ApplyDeadZone(axisX, deadZone));
            z = PickLarger(z, ApplyDeadZone(axisZ, deadZone));
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
