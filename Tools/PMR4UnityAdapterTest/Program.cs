// ============================================================================
//  PMR4UnityAdapterTest —— PMUnityMoverInput 的纯转换与跳跃边沿语义验收（R4-B / B2）
// ============================================================================
//
//  被验收对象（**真实源码**，不复制不快照）：
//    Client/Assets/Scripts/PMUnity/PMUnityMoverInput.cs
//
//  本工程能证明什么：
//    · 轴钳制 / NaN 拒绝 / 朝向推导 / 死区 / 真实步判定的**纯函数**行为；
//    · 跳跃边沿状态机：零 ms 帧不丢边沿、真实步恰好消费一次、补子步不重复跳跃、
//      注入式边沿与硬件边沿走同一套语义；
//    · **Convert 的纯洁性**：在"虚拟轴未配置"（任何 Input 访问都会抛）的前提下调用
//      Convert 仍然成功 —— 直接证明它不读硬件、无副作用。
//
//  本工程**不能**证明什么（写在这里以免被误读）：
//    · Unity 真实 Input 的键位/InputManager 行为（这里用的是 Input 替身）；
//    · 任何物理行为 —— PMUnityMoverCollisionQuery / PMUnityMoverPresentation 刻意**不在**
//      本工程编译（它们在 net8 上用替身跑就是"假验物理"）。
//      物理与表现分别由 Tools/PMR4UnityCheck（真实 Unity DLL 编译）与
//      Client/Assets/Editor/PMR4UnityValidation.cs（Unity 内真实 PhysX 运行）覆盖。
//
//  运行：dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using PMNet.Mover;
using PMNet.Unity;
using UnityEngine;

namespace PMR4UnityAdapterTest
{
    internal static class Program
    {
        private static int _checks;
        private static int _failed;

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("---- " + title + " ----");
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            _checks++;
            if (!ok) { _failed++; }
            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name
                              + (string.IsNullOrEmpty(detail) ? string.Empty : (" :: " + detail)));
        }

        private static bool Near(float a, float b, float tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        /// <summary>模拟"按下某键"（GetKeyDown 与 GetKey 同时为真，与真实 Unity 一致）。</summary>
        private static void Press(KeyCode key)
        {
            if (!Input.Held.Contains(key)) { Input.Held.Add(key); }
            if (!Input.DownThisFrame.Contains(key)) { Input.DownThisFrame.Add(key); }
        }

        /// <summary>模拟"换帧但键仍按住"：GetKeyDown 归假，GetKey 仍为真。</summary>
        private static void NextFrameKeepHeld()
        {
            Input.DownThisFrame.Clear();
        }

        /// <summary>模拟"松开某键"。</summary>
        private static void Release(KeyCode key)
        {
            Input.Held.Remove(key);
            Input.DownThisFrame.Remove(key);
        }

        private static int Main()
        {
            Console.WriteLine("===== PMR4UnityAdapterTest：PMUnityMoverInput 纯转换 + 跳跃边沿 =====");

            TestPureConvert();
            TestYawDerivation();
            TestNormalizeDegrees();
            TestDeadZone();
            TestRealStepPredicate();
            TestJumpEdgeStateMachine();
            TestHardwareSampling();
            TestPurityOfConvert();

            Console.WriteLine();
            Console.WriteLine("结果：" + (_failed == 0 ? "全部通过" : "存在失败")
                              + "（checks=" + _checks + " failed=" + _failed + "）");
            Console.WriteLine("边界：本工程不验证 Unity 物理与真实 Input 行为；"
                              + "物理由 Tools/PMR4UnityCheck（真实 DLL 编译）与 Unity 内 PMR4UnityValidation 菜单覆盖。");

            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ A. 纯转换

        private static void TestPureConvert()
        {
            Section("A. Convert（纯转换）");

            PMMoverInput high = PMUnityMoverInput.Convert(3f, -2f, 0.5f, 400f, true);
            Check("A1 轴钳制到 [-1,1]",
                high.MoveX == 1f && high.MoveZ == -1f && high.MoveY == 0.5f,
                "MoveX=" + high.MoveX + " MoveZ=" + high.MoveZ + " MoveY=" + high.MoveY);
            Check("A2 朝向归一化到 (-180,180]",
                Near(high.YawDegrees, 40f, 1e-4f), "yaw=" + high.YawDegrees + "（期望 40）");
            Check("A3 边沿原样透传", high.JumpPressed, "JumpPressed=" + high.JumpPressed);
            Check("A4 鉴权命令（Effects/Layers/RemovedLayerIds）一律为 null",
                high.Effects == null && high.Layers == null && high.RemovedLayerIds == null,
                "effects=" + (high.Effects == null ? "null" : "非null")
                + " layers=" + (high.Layers == null ? "null" : "非null")
                + " removed=" + (high.RemovedLayerIds == null ? "null" : "非null"));

            Check("A5 拒绝 NaN（moveX）", ThrowsArgument(() => PMUnityMoverInput.Convert(float.NaN, 0f, 0f, 0f, false)));
            Check("A6 拒绝 +Infinity（moveZ）", ThrowsArgument(() => PMUnityMoverInput.Convert(0f, float.PositiveInfinity, 0f, 0f, false)));
            Check("A7 拒绝 NaN（yaw）", ThrowsArgument(() => PMUnityMoverInput.Convert(0f, 0f, 0f, float.NaN, false)));
            Check("A8 拒绝 -Infinity（moveY）", ThrowsArgument(() => PMUnityMoverInput.Convert(0f, 0f, float.NegativeInfinity, 0f, false)));
        }

        private static bool ThrowsArgument(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------ B. 朝向推导

        private static void TestYawDerivation()
        {
            Section("B. DeriveYawDegrees（Y-up：0=+Z，90=+X）");

            Check("B1 (+X) → 90", Near(PMUnityMoverInput.DeriveYawDegrees(1f, 0f), 90f, 1e-3f));
            Check("B2 (+Z) → 0", Near(PMUnityMoverInput.DeriveYawDegrees(0f, 1f), 0f, 1e-3f));
            Check("B3 (-X) → -90", Near(PMUnityMoverInput.DeriveYawDegrees(-1f, 0f), -90f, 1e-3f));
            Check("B4 (-Z) → 180", Near(PMUnityMoverInput.DeriveYawDegrees(0f, -1f), 180f, 1e-3f));
            Check("B5 (-X,-Z) → -135", Near(PMUnityMoverInput.DeriveYawDegrees(-1f, -1f), -135f, 1e-3f));
            Check("B6 (+X,+Z) → 45", Near(PMUnityMoverInput.DeriveYawDegrees(1f, 1f), 45f, 1e-3f));
            Check("B7 零输入 → 0（调用方据此决定是否采用）",
                PMUnityMoverInput.DeriveYawDegrees(0f, 0f) == 0f, "0");
            Check("B8 与模长无关（100,0）→ 90",
                Near(PMUnityMoverInput.DeriveYawDegrees(100f, 0f), 90f, 1e-3f));
            Check("B9 不做队伍镜像（-X 就是 -90，不是 +90）",
                Near(PMUnityMoverInput.DeriveYawDegrees(-1f, 0f), -90f, 1e-3f));
        }

        private static void TestNormalizeDegrees()
        {
            Section("C. NormalizeDegrees");

            Check("C1 400 → 40", Near(PMUnityMoverInput.NormalizeDegrees(400f), 40f, 1e-4f));
            Check("C2 -400 → -40", Near(PMUnityMoverInput.NormalizeDegrees(-400f), -40f, 1e-4f));
            Check("C3 180 保持 180", Near(PMUnityMoverInput.NormalizeDegrees(180f), 180f, 1e-4f));
            Check("C4 -180 → 180（唯一表示）", Near(PMUnityMoverInput.NormalizeDegrees(-180f), 180f, 1e-4f));
            Check("C5 181 → -179", Near(PMUnityMoverInput.NormalizeDegrees(181f), -179f, 1e-4f));
            Check("C6 -181 → 179", Near(PMUnityMoverInput.NormalizeDegrees(-181f), 179f, 1e-4f));
        }

        private static void TestDeadZone()
        {
            Section("D. ApplyDeadZone（不缩放补偿）");

            Check("D1 小于死区 → 0", PMUnityMoverInput.ApplyDeadZone(0.1f, 0.2f) == 0f);
            Check("D2 等于死区 → 0", PMUnityMoverInput.ApplyDeadZone(0.2f, 0.2f) == 0f);
            Check("D3 负方向同样归零", PMUnityMoverInput.ApplyDeadZone(-0.1f, 0.2f) == 0f);
            Check("D4 超过死区 → 原值（不缩放）",
                Near(PMUnityMoverInput.ApplyDeadZone(0.5f, 0.2f), 0.5f, 1e-6f), "0.5");
            Check("D5 死区 ≤ 0 → 原值",
                Near(PMUnityMoverInput.ApplyDeadZone(0.001f, 0f), 0.001f, 1e-9f), "0.001");
        }

        private static void TestRealStepPredicate()
        {
            Section("E. IsRealStepMs（契约 1..50）");

            Check("E1 1 是真实步", PMUnityMoverInput.IsRealStepMs(1));
            Check("E2 50 是真实步", PMUnityMoverInput.IsRealStepMs(50));
            Check("E3 16 是真实步", PMUnityMoverInput.IsRealStepMs(16));
            Check("E4 0 不是（缺帧占位）", !PMUnityMoverInput.IsRealStepMs(0));
            Check("E5 51 不是", !PMUnityMoverInput.IsRealStepMs(51));
            Check("E6 -1 不是", !PMUnityMoverInput.IsRealStepMs(-1));
            Check("E7 边界常量自洽",
                PMUnityMoverInput.MinRealStepMs == 1 && PMUnityMoverInput.MaxRealStepMs == 50,
                "min=" + PMUnityMoverInput.MinRealStepMs + " max=" + PMUnityMoverInput.MaxRealStepMs);
        }

        // ------------------------------------------------------------------ F. 边沿状态机（不读硬件）

        private static void TestJumpEdgeStateMachine()
        {
            Section("F. 跳跃边沿状态机（NotifyJumpEdge / Consume，不读硬件）");

            Input.ResetStub();

            PMUnityMoverInput input = new PMUnityMoverInput();
            Check("F1 初始无缓冲", !input.HasBufferedJumpEdge);

            input.NotifyJumpEdge();
            Check("F2 注入后缓冲成立", input.HasBufferedJumpEdge);

            input.NotifyJumpEdge();
            Check("F3 重复注入幂等（不会变成「两次跳跃」）", input.HasBufferedJumpEdge && input.ConsumedEdges == 0,
                "buffered=" + input.HasBufferedJumpEdge + " consumed=" + input.ConsumedEdges);

            input.Consume(0);
            Check("F4 零 ms 步不消费边沿", input.HasBufferedJumpEdge);

            input.Consume(PMUnityMoverInput.MaxRealStepMs + 1);
            Check("F5 越界步不消费边沿", input.HasBufferedJumpEdge);

            input.Consume(PMUnityMoverInput.MinRealStepMs);
            Check("F6 真实步消费边沿", !input.HasBufferedJumpEdge && input.ConsumedEdges == 1,
                "consumed=" + input.ConsumedEdges);

            input.Consume(16);
            Check("F7 已空时消费不增加计数", input.ConsumedEdges == 1, "consumed=" + input.ConsumedEdges);

            input.NotifyJumpEdge();
            input.ClearBufferedJumpEdge();
            Check("F8 显式清空（死亡/离场）", !input.HasBufferedJumpEdge);

            input.NotifyJumpEdge();
            input.Peek();
            Check("F9 Peek 不消费", input.HasBufferedJumpEdge);

            input.Reset();
            Check("F10 Reset 清空", !input.HasBufferedJumpEdge);
        }

        // ------------------------------------------------------------------ G. 硬件采样（替身驱动）

        private static void TestHardwareSampling()
        {
            Section("G. Sample / SampleAndConsume（Input 替身驱动）");

            Input.ResetStub();
            PMUnityMoverInput input = new PMUnityMoverInput();

            PMMoverInput idle = input.Sample(16);
            Check("G1 无按键无轴 → 全零",
                idle.MoveX == 0f && idle.MoveZ == 0f && idle.MoveY == 0f && !idle.JumpPressed,
                "MoveX=" + idle.MoveX + " MoveZ=" + idle.MoveZ);

            Press(KeyCode.W);
            Press(KeyCode.D);
            PMMoverInput wasd = input.Sample(16);
            Check("G2 W+D → MoveX=1 / MoveZ=1 / yaw=45",
                Near(wasd.MoveX, 1f, 1e-6f) && Near(wasd.MoveZ, 1f, 1e-6f) && Near(wasd.YawDegrees, 45f, 1e-3f),
                "MoveX=" + wasd.MoveX + " MoveZ=" + wasd.MoveZ + " yaw=" + wasd.YawDegrees);

            Input.ResetStub();
            Press(KeyCode.A);
            Press(KeyCode.S);
            PMMoverInput backward = input.Sample(16);
            Check("G3 A+S → MoveX=-1 / MoveZ=-1 / yaw=-135",
                Near(backward.MoveX, -1f, 1e-6f) && Near(backward.MoveZ, -1f, 1e-6f)
                && Near(backward.YawDegrees, -135f, 1e-3f),
                "MoveX=" + backward.MoveX + " MoveZ=" + backward.MoveZ + " yaw=" + backward.YawDegrees);

            // 虚拟轴：无按键时由轴提供；与按键同时存在时不叠加（取绝对值较大者）。
            Input.ResetStub();
            Input.Horizontal = 0.5f;
            Input.Vertical = 0f;
            PMMoverInput axisOnly = input.Sample(16);
            Check("G4a 仅虚拟轴 → 采用轴值",
                Near(axisOnly.MoveX, 0.5f, 1e-6f), "MoveX=" + axisOnly.MoveX);

            Press(KeyCode.D);
            PMMoverInput both = input.Sample(16);
            Check("G4b 按键与轴同时存在 → 取较大者，不叠加",
                Near(both.MoveX, 1f, 1e-6f), "MoveX=" + both.MoveX + "（期望 1，不是 1.5）");

            Input.ResetStub();
            PMUnityMoverInput deadZoned = new PMUnityMoverInput();
            deadZoned.AxisDeadZone = 0.3f;
            Input.Horizontal = 0.2f;
            Check("G5a 轴在死区内 → 0", deadZoned.Sample(16).MoveX == 0f);
            Input.Horizontal = 0.6f;
            Check("G5b 轴超出死区 → 原值",
                Near(deadZoned.Sample(16).MoveX, 0.6f, 1e-6f), "MoveX=" + deadZoned.Sample(16).MoveX);

            // 虚拟轴不可用：抛一次被捕获 → 关掉该来源并计数；按键来源仍工作；不再重复抛。
            Input.ResetStub();
            Input.AxesConfigured = false;
            Press(KeyCode.D);
            PMUnityMoverInput noAxes = new PMUnityMoverInput();
            PMMoverInput keyOnly = noAxes.Sample(16);
            int callsAfterFirst = Input.GetAxisRawCalls;
            PMMoverInput keyOnly2 = noAxes.Sample(16);
            Check("G6 轴不可用时降级为按键（且不再重复尝试）",
                Near(keyOnly.MoveX, 1f, 1e-6f) && Near(keyOnly2.MoveX, 1f, 1e-6f)
                && !noAxes.AxisAvailable && noAxes.AxisReadFailures == 1
                && Input.GetAxisRawCalls == callsAfterFirst,
                "AxisAvailable=" + noAxes.AxisAvailable + " failures=" + noAxes.AxisReadFailures
                + " getAxisRawCalls=" + Input.GetAxisRawCalls);

            // (1) 零 ms 帧不丢边沿 + 真实步恰好消费一次 + 补子步不重复跳跃。
            Input.ResetStub();
            PMUnityMoverInput jump = new PMUnityMoverInput();
            Press(KeyCode.Space);

            PMMoverInput zeroStep = jump.Sample(0);
            jump.Consume(0);
            bool survivedZeroStep = jump.HasBufferedJumpEdge;

            NextFrameKeepHeld(); // 换帧但不松开：GetKeyDown 归假、GetKey 仍真
            PMMoverInput firstRealStep = jump.Sample(16);
            jump.Consume(16);
            PMMoverInput secondSubStep = jump.SampleAndConsume(16);

            Check("G7 零 ms 帧不丢边沿", zeroStep.JumpPressed && survivedZeroStep,
                "zeroStep.JumpPressed=" + zeroStep.JumpPressed + " buffered=" + survivedZeroStep);
            Check("G8 第一个真实步消费边沿", firstRealStep.JumpPressed);
            Check("G9 补子步不重复跳跃（Sample+Consume 路径）", !secondSubStep.JumpPressed,
                "SecondSubStep.JumpPressed=" + secondSubStep.JumpPressed);

            // (2) 【负向验证补上的盲区】连续子步**只用** SampleAndConsume：
            //     第一次必须带边沿、第二/三次必须不带。若 SampleAndConsume 漏掉消费，
            //     同一次按下会在每个子步各锁一次 —— "一次按键跳三次"只有本用例能抓。
            Input.ResetStub();
            PMUnityMoverInput onlySampled = new PMUnityMoverInput();
            Press(KeyCode.Space);
            PMMoverInput s1 = onlySampled.SampleAndConsume(16);
            PMMoverInput s2 = onlySampled.SampleAndConsume(16);
            PMMoverInput s3 = onlySampled.SampleAndConsume(16);
            Check("G9b 只用 SampleAndConsume 时补子步不重复跳跃",
                s1.JumpPressed && !s2.JumpPressed && !s3.JumpPressed,
                "s1=" + s1.JumpPressed + " s2=" + s2.JumpPressed + " s3=" + s3.JumpPressed);

            // (3) PollHardware 幂等：同一帧重复调用（宿主按子步驱动时会这样）只有一个边沿。
            Input.ResetStub();
            PMUnityMoverInput idem = new PMUnityMoverInput();
            Press(KeyCode.Space);
            idem.PollHardware();
            idem.PollHardware();
            idem.PollHardware();
            PMMoverInput idemFirst = idem.SampleAndConsume(16);
            idem.PollHardware(); // 同一帧、同一次按下：不得重新锁上
            PMMoverInput idemSecond = idem.SampleAndConsume(16);
            Check("G9c 同一帧重复 PollHardware 不产生第二个边沿",
                idemFirst.JumpPressed && !idemSecond.JumpPressed,
                "first=" + idemFirst.JumpPressed + " second=" + idemSecond.JumpPressed);

            // (4) 松开后再按下必须产生**新**边沿（不能变成"永久吞掉"）。
            Release(KeyCode.Space);
            idem.PollHardware();
            Press(KeyCode.Space);
            idem.PollHardware();
            PMMoverInput repress = idem.SampleAndConsume(16);
            Check("G9d 松开后再按下产生新边沿（不是永久吞掉）",
                repress.JumpPressed, "JumpPressed=" + repress.JumpPressed);

            // (5) PollHardware 早锁：边沿出现在"本帧不产生真实步"的时候也不能丢。
            Input.ResetStub();
            PMUnityMoverInput latched = new PMUnityMoverInput();
            Press(KeyCode.Space);
            latched.PollHardware();      // 该帧只锁不模拟
            NextFrameKeepHeld();         // 后续帧 GetKeyDown 恒 false（键还按着）
            latched.PollHardware();
            latched.PollHardware();
            PMMoverInput later = latched.SampleAndConsume(16);
            Check("G10 PollHardware 提前锁定，跨越无真实步的帧仍不丢",
                later.JumpPressed && latched.ConsumedEdges == 1,
                "JumpPressed=" + later.JumpPressed + " consumed=" + latched.ConsumedEdges
                + " bufferedPolls=" + latched.BufferedJumpEdgePolls);

            // 朝向保持：零输入时不回到 0。
            Input.ResetStub();
            PMUnityMoverInput yawHold = new PMUnityMoverInput();
            Press(KeyCode.D);
            float firstYaw = yawHold.Sample(16).YawDegrees;
            Release(KeyCode.D);
            float heldYaw = yawHold.Sample(16).YawDegrees;
            Check("G11 零输入时朝向保持上一次",
                Near(firstYaw, 90f, 1e-3f) && Near(heldYaw, 90f, 1e-3f),
                "first=" + firstYaw + " held=" + heldYaw);

            yawHold.DeriveYawFromMoveInput = false;
            yawHold.ExternalYawDegrees = 33f;
            Press(KeyCode.D);
            Check("G12 关闭推导时用 ExternalYawDegrees",
                Near(yawHold.Sample(16).YawDegrees, 33f, 1e-3f), "yaw=" + yawHold.Sample(16).YawDegrees);

            Input.ResetStub();
            PMUnityMoverInput vertical = new PMUnityMoverInput();
            vertical.ExternalVerticalInput = 0.7f;
            Check("G13 ExternalVerticalInput → MoveY（WASD 不产生竖直轴）",
                Near(vertical.Sample(16).MoveY, 0.7f, 1e-6f), "MoveY=" + vertical.Sample(16).MoveY);
        }

        // ------------------------------------------------------------------ H. Convert 的纯洁性

        private static void TestPurityOfConvert()
        {
            Section("H. Convert 纯洁性");

            Input.ResetStub();
            Input.AxesConfigured = false; // 任何 Input 访问都会抛

            bool ok = true;
            string detail = string.Empty;

            try
            {
                PMMoverInput a = PMUnityMoverInput.Convert(0.25f, -0.5f, 0f, 10f, false);
                PMMoverInput b = PMUnityMoverInput.Convert(0.25f, -0.5f, 0f, 10f, false);
                ok = a.MoveX == b.MoveX && a.MoveZ == b.MoveZ && a.YawDegrees == b.YawDegrees
                     && a.JumpPressed == b.JumpPressed && Input.GetAxisRawCalls == 0;
                detail = "相同输入 → 相同输出；Input 访问次数=" + Input.GetAxisRawCalls;
            }
            catch (Exception ex)
            {
                ok = false;
                detail = "Convert 触碰了硬件：" + ex.GetType().Name + " " + ex.Message;
            }

            Check("H1 Convert 不读硬件、不改状态、可重复", ok, detail);
        }
    }
}

// ============================================================================
//  以下为测试专用的 UnityEngine 替身（只桩 Input / KeyCode；无任何物理）
// ============================================================================

namespace UnityEngine
{
    /// <summary>与 Unity 数值一致的 KeyCode 子集（只列本套用例用到的键）。</summary>
    public enum KeyCode
    {
        None = 0,
        Space = 32,
        A = 97,
        D = 100,
        S = 115,
        W = 119,
        UpArrow = 273,
        DownArrow = 274,
        RightArrow = 275,
        LeftArrow = 276,
    }

    /// <summary>
    /// 可被测试驱动的 Input 替身（**测试专用，不是 Unity 实现**；替身里没有任何物理）。
    ///
    /// 为什么只桩 Input / KeyCode：PMUnityMoverInput.cs 刻意只依赖 UnityEngine.Input +
    /// UnityEngine.KeyCode（+ System.Math），不碰 Mathf / Vector3 / Debug / Physics，
    /// 因此本替身可以极小，且**不会**出现"拿替身冒充 Unity 物理"的问题。
    ///
    /// 边界：本替身不能证明 Unity 真实 Input 的键位映射与 InputManager 配置；
    /// 只能证明适配器"给定 Input 返回什么"时的纯逻辑。真实 Unity 侧的采集由
    /// Client/Assets/Editor/PMR4UnityValidation.cs 的菜单覆盖。
    ///
    /// GetKeyDown 的帧语义由测试自己管理：测试设置 DownThisFrame，并负责在"换帧时"清空，
    /// 从而能构造"边沿出现在没有真实步的那一帧"这种最关键的场景。
    /// </summary>
    public static class Input
    {
        /// <summary>当前按住的键。</summary>
        public static readonly System.Collections.Generic.List<KeyCode> Held
            = new System.Collections.Generic.List<KeyCode>();

        /// <summary>本帧刚按下的键（"帧"由测试界定，测试负责在换帧时清空）。</summary>
        public static readonly System.Collections.Generic.List<KeyCode> DownThisFrame
            = new System.Collections.Generic.List<KeyCode>();

        /// <summary>false 时 GetAxisRaw 抛异常，用于模拟"虚拟轴未配置"的工程。</summary>
        public static bool AxesConfigured = true;

        /// <summary>Horizontal 虚拟轴取值。</summary>
        public static float Horizontal;

        /// <summary>Vertical 虚拟轴取值。</summary>
        public static float Vertical;

        /// <summary>GetAxisRaw 被调用次数（用于断言"轴不可用后不再重复调用"）。</summary>
        public static int GetAxisRawCalls;

        public static bool GetKey(KeyCode key)
        {
            return Held.Contains(key);
        }

        public static bool GetKeyDown(KeyCode key)
        {
            return DownThisFrame.Contains(key);
        }

        public static float GetAxisRaw(string axisName)
        {
            GetAxisRawCalls++;

            if (!AxesConfigured)
            {
                // 与 Unity 的异常同一形态：未配置的轴会抛 ArgumentException。
                throw new ArgumentException("Input Axis " + axisName + " is not setup.");
            }

            if (axisName == "Horizontal") { return Horizontal; }
            if (axisName == "Vertical") { return Vertical; }
            return 0f;
        }

        /// <summary>把替身恢复到初始状态（每个用例前调用）。</summary>
        public static void ResetStub()
        {
            Held.Clear();
            DownThisFrame.Clear();
            AxesConfigured = true;
            Horizontal = 0f;
            Vertical = 0f;
            GetAxisRawCalls = 0;
        }
    }
}
