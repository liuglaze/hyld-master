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
//    · **T-VIS2 视觉修复**：PMBattleCameraGeometry 的纯几何（固定方位角跟随、观察目标、
//      后距/近裁面钳制、遮挡回退），含“旧挂根节点时按 owner Yaw 瞬转”的回归反例。
//    · **T-PLAY1（B3/B4 节）**：取景回到旧预制体确定值（yaw −90 / 俯角 68.191 / FOV 60 /
//      近裁面 0.3）、后距 = 旧相对偏移 (6,12,0) 的模长、按旧 SmoothTime 0.08s 临界阻尼跟随
//      （首帧/大跳变直接贴合、不过冲、不啖 NaN）；
//    · **T-PLAY2（D 节）**：可见朝向 = 权威 Mover Yaw（不带烘焙偏转入参）的纯组合，
//      加上资产/源码静态契约（烘焙 Capsule 本地 Yaw 270、旧 prefab 的
//      HYLDPlayerController.selfTransform 指向 Capsule、角色根不再承载 yaw）。
//
//  本工程**不能**证明什么（写在这里以免被误读）：
//    · Unity 真实 Input 的键位/InputManager 行为（这里用的是 Input 替身）；
//    · 任何物理行为 —— PMUnityMoverCollisionQuery / PMUnityMoverPresentation 刻意**不在**
//      本工程编译（它们在 net8 上用替身跑就是"假验物理"）。
//      物理与表现分别由 Tools/PMR4UnityCheck（真实 Unity DLL 编译）与
//      Client/Assets/Editor/PMR4UnityValidation.cs（Unity 内真实 PhysX 运行）覆盖。
//    · 相机遮挡的**真实射线**也不在这里：本工程只验证"给定障碍距离时的纯几何决策"，
//      真实 PhysicsScene.Raycast 由宿主在 Unity 内执行（PMClientSessionHost）。
//    · **实机画面**（镜头到底看起来像不像旧俯视、模型到底朝哪）一律不在本工程声明：
//      PMUnityBattlePresentation 依赖 GameObject/Transform/Animator/Resources，
//      用替身跑等于假验表现层，因此 D 节对表现层只能做**源码/资产静态契约**，
//      真正的验收仍是 T-PLAY5（Unity 同版本两端实机，PENDING_USER）。
//
//  运行：dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
            Console.WriteLine("===== PMR4UnityAdapterTest：PMUnityMoverInput 纯转换 + 跳跃边沿 + 相机/朝向 =====");

            TestPureConvert();
            TestYawDerivation();
            TestBattleCameraGeometry();
            TestPlayCameraFraming();
            TestPlayFollowSmoothing();
            TestPlayFacingContract();
            TestNormalizeDegrees();
            TestDeadZone();
            TestRealStepPredicate();
            TestJumpEdgeStateMachine();
            TestHardwareSampling();
            TestKeyboardMatchesStick();
            TestPurityOfConvert();
            TestUiScreenVector();
            TestUiMoveSourceSelection();
            TestUiHostSourceContracts();
            TestLoop6ResumeSourceContracts();
            TestRealUiStickMath();
            TestLive3AimIndicatorMath();
            TestLive3AimEdgeContracts();
            TestLive3AimSourceContracts();

            Console.WriteLine();
            Console.WriteLine("结果：" + (_failed == 0 ? "全部通过" : "存在失败")
                              + "（checks=" + _checks + " failed=" + _failed + "）");
            Console.WriteLine("边界：本工程不验证 Unity 物理与真实 Input 行为；"
                              + "物理由 Tools/PMR4UnityCheck（真实 DLL 编译）与 Unity 内 PMR4UnityValidation 菜单覆盖；"
                              + "相机遮挡的真实射线由宿主在 Unity 内执行（本工程只验纯几何决策）。");

            return _failed == 0 ? 0 : 1;
        }

        /// <summary>直接执行真实 UI 源码里的纯数学，不拿测试镜像冒充实现。</summary>
        private static void TestRealUiStickMath()
        {
            Section("L. 真实PMUnityBattleControlMath（不执行uGUI/旧prefab）");
            float x, y;
            bool up = PMUnityBattleControlMath.TryNormalizeStick(0f, 100f, 100f, 0.2f, out x, out y);
            Check("L1 UI向上不取反", up && Near(x, 0f, 1e-6f) && Near(y, 1f, 1e-6f));
            bool dead = PMUnityBattleControlMath.TryNormalizeStick(12f, 0f, 100f, 0.2f, out x, out y);
            Check("L2 死区清零", dead && x == 0f && y == 0f);
            bool outer = PMUnityBattleControlMath.TryNormalizeStick(-150f, 0f, 100f, 0.2f, out x, out y);
            Check("L3 超半径限幅", outer && x == -1f && y == 0f);
            Check("L4 非finite不交宿主",
                  !PMUnityBattleControlMath.TryNormalizeStick(float.NaN, 5f, 100f, 0.2f, out x, out y));
            bool clamp = PMUnityBattleControlMath.TryClampToRadius(300f, 400f, 100f, out x, out y);
            Check("L5 手柄半径收敛", clamp && Near(x, 60f, 1e-5f) && Near(y, 80f, 1e-5f));
            Check("L6 攻击空方向拒绝", !PMUnityBattleControlMath.IsMeaningfulAim(0f, 0f, 0.05f));
            Check("L7 独立瞄准正交", PMUnityBattleControlMath.IsMeaningfulAim(0f, 1f, 0.05f)
                  && PMUnityBattleControlMath.IsMeaningfulAim(1f, 0f, 0.05f));
            Check("L8 零半径拒绝", !PMUnityBattleControlMath.TryNormalizeStick(1f, 1f, 0f, 0.2f, out x, out y));
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

        // ------------------------------------------------------------------ B2. 相机纯几何（本轮视觉修复）

        /// <summary>
        /// PMBattleCameraGeometry：正式模式本地相机跟随的纯几何。
        ///
        /// 这一节是“镜头不继承按键 Yaw”的可执行证据：
        ///   · 新算法（Compute / FixedCameraYawDegrees）**没有 owner Yaw 入参**，
        ///     因此无论角色朝哪，相机方位角都是固定常量；
        ///   · 旧算法（LegacyHierarchyChildCameraWorldYaw = 挂根节点的世界方位角）
        ///     在 owner Yaw 0→90→180 时会返回 0→90→180 —— 这正是实机“瞬转”的代码事实。
        /// </summary>
        private static void TestBattleCameraGeometry()
        {
            Section("B2. PMBattleCameraGeometry（固定方位角跟随 + 遮挡回退）");

            float lookHeight = PMBattleCameraGeometry.DefaultLookHeightMeters;
            float desired = PMBattleCameraGeometry.DefaultDistanceMeters;
            float yaw = PMBattleCameraGeometry.DefaultYawDegrees;
            float pitch = PMBattleCameraGeometry.DefaultPitchDegrees;
            float nearClip = PMBattleCameraGeometry.DefaultNearClipMeters;
            float minDistance = PMBattleCameraGeometry.MinDistanceMeters;
            float margin = PMBattleCameraGeometry.OcclusionMarginMeters;
            float noOcclusion = PMBattleCameraGeometry.NoOcclusionDistance;

            PMVector3 origin = new PMVector3(3f, 1f, -5f);

            // (1) 无遮挡：用想要的距离，且不改朝向。
            PMBattleCameraPose free = PMBattleCameraGeometry.Compute(
                origin, lookHeight, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin);

            Check("B2-1 无遮挡时使用想要的后距",
                Near(free.DistanceMeters, desired, 1e-4f) && !free.Occluded,
                "distance=" + free.DistanceMeters + " occluded=" + free.Occluded);

            Check("B2-2 观察目标 = 角色世界位置 + 观察高度（与角色朝向无关）",
                Near(free.LookTarget.X, origin.X, 1e-5f)
                && Near(free.LookTarget.Y, origin.Y + lookHeight, 1e-5f)
                && Near(free.LookTarget.Z, origin.Z, 1e-5f),
                "look=" + free.LookTarget.X + "/" + free.LookTarget.Y + "/" + free.LookTarget.Z);

            float cosPitch = (float)Math.Cos(pitch * (Math.PI / 180.0));
            float sinPitch = (float)Math.Sin(pitch * (Math.PI / 180.0));
            PMVector3 backUnit = PMBattleCameraGeometry.BackDirection(yaw, pitch);

            // （T-PLAY1 修订）相机不再落在“世界 −Z”，而是落在**旧预制体固定后向**上：
            //   yaw = −90 时后向 = (+0.3715, +0.9284, ≈0)，即世界 +X 侧上方（旧俯视取景）。
            //   断言按“后向 × 距离”逐分量成立（不是只查 Z 符号），因此换回 yaw 0 仍会 FAIL。
            Check("B2-3 相机精确落在观察目标的固定后向 × 距离处（且高于目标、有水平后撤）",
                Near(free.CameraPosition.X - free.LookTarget.X, backUnit.X * desired, 1e-3f)
                && Near(free.CameraPosition.Y - free.LookTarget.Y, backUnit.Y * desired, 1e-3f)
                && Near(free.CameraPosition.Z - free.LookTarget.Z, backUnit.Z * desired, 1e-3f)
                && free.CameraPosition.Y > free.LookTarget.Y
                && Math.Abs(backUnit.X) > 1e-3f,
                "cam=(" + free.CameraPosition.X + ", " + free.CameraPosition.Y + ", "
                + free.CameraPosition.Z + ") back=(" + backUnit.X.ToString("R") + ", "
                + backUnit.Y.ToString("R") + ", " + backUnit.Z.ToString("R") + ")");

            float freeHorizontal = (float)Math.Sqrt(
                (double)((free.CameraPosition.X - free.LookTarget.X) * (free.CameraPosition.X - free.LookTarget.X)
                         + (free.CameraPosition.Z - free.LookTarget.Z) * (free.CameraPosition.Z - free.LookTarget.Z)));

            Check("B2-4 俯仰角使水平投影 = 后距 × cos(pitch)、抬升 = 后距 × sin(pitch)（不是直接等于后距）",
                Near(freeHorizontal, desired * cosPitch, 1e-3f)
                && Near(free.CameraPosition.Y - free.LookTarget.Y, desired * sinPitch, 1e-3f),
                "horizontal=" + freeHorizontal + " dy=" + (free.CameraPosition.Y - free.LookTarget.Y)
                + " cosPitch=" + cosPitch.ToString("R") + " sinPitch=" + sinPitch.ToString("R"));

            Check("B2-5 后向向量是单位向量（yaw/pitch 任意都不变）",
                Near(PMBattleCameraGeometry.BackDirection(0f, 0f).Length, 1f, 1e-5f)
                && Near(PMBattleCameraGeometry.BackDirection(90f, 0f).Length, 1f, 1e-5f)
                && Near(PMBattleCameraGeometry.BackDirection(-137f, 33f).Length, 1f, 1e-5f),
                "|back(0,0)|=" + PMBattleCameraGeometry.BackDirection(0f, 0f).Length.ToString("R"));

            Check("B2-6 yaw=0,pitch=0 时相机正好在目标 -Z 侧",
                Near(PMBattleCameraGeometry.BackDirection(0f, 0f).X, 0f, 1e-6f)
                && Near(PMBattleCameraGeometry.BackDirection(0f, 0f).Y, 0f, 1e-6f)
                && Near(PMBattleCameraGeometry.BackDirection(0f, 0f).Z, -1f, 1e-6f),
                "back=(0,0,-1)");

            // (2) 【核心回归】镜头方位角与角色 Yaw 解耦。
            //     旧行为：挂根节点 ⇒ 世界方位角 = ownerYaw + localYaw（owner 一转就跟着转）。
            //     新行为：固定常量，Compute 根本没有 owner Yaw 入参。
            float[] ownerYaws = new float[] { 0f, 90f, -90f, 180f, 359f };
            bool newStable = true;
            bool oldJumps = false;
            string detail = string.Empty;

            for (int i = 0; i < ownerYaws.Length; i++)
            {
                PMBattleCameraPose pose = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin);

                if (!Near(pose.YawDegrees, yaw, 1e-6f)) { newStable = false; }

                float legacy = PMBattleCameraGeometry.LegacyHierarchyChildCameraWorldYaw(ownerYaws[i], 0f);
                if (!Near(legacy, PMBattleCameraGeometry.FixedCameraYawDegrees(yaw), 1e-6f)) { oldJumps = true; }

                detail += "ownerYaw=" + ownerYaws[i] + "->newYaw=" + pose.YawDegrees
                          + "/oldWorldYaw=" + legacy + "  ";
            }

            Check("B2-7 新算法：角色 Yaw 0/±90/180/359 都不改变相机方位角", newStable, detail.Trim());
            Check("B2-8 旧反例（挂根节点）：同样序列下方位角会跳到 90/180（这就是实机瞬转）",
                oldJumps, "legacy(90,0)=" + PMBattleCameraGeometry.LegacyHierarchyChildCameraWorldYaw(90f, 0f)
                          + " legacy(180,0)=" + PMBattleCameraGeometry.LegacyHierarchyChildCameraWorldYaw(180f, 0f));
            Check("B2-9 固定方位角函数不接受 owner Yaw（只归一化配置值）",
                Near(PMBattleCameraGeometry.FixedCameraYawDegrees(33f), 33f, 1e-5f)
                && Near(PMBattleCameraGeometry.FixedCameraYawDegrees(-400f), -40f, 1e-5f),
                "fixed(33)=" + PMBattleCameraGeometry.FixedCameraYawDegrees(33f)
                + " fixed(-400)=" + PMBattleCameraGeometry.FixedCameraYawDegrees(-400f));

            // (3) 遮挡回退：纯函数、保守、有下限。
            Check("B2-10 命中 3m → 后距 = 3 - 安全边距",
                Near(PMBattleCameraGeometry.ResolveDistance(desired, 3f, minDistance, margin),
                     3f - margin, 1e-4f),
                "resolved=" + PMBattleCameraGeometry.ResolveDistance(desired, 3f, minDistance, margin));

            // B2-11（T-VIS2b 修订）：命中距离与安全边距挤到比"构图最小距离"还近时，
            // 「安全（相机必须严格在障碍物之前）」优先于「构图（不要把相机贴进角色）」：
            // 旧实现硬钳到 MinDistanceMeters(1.6) ⇒ 命中 0.5m 时相机被放到墙后 1.1m。
            Check("B2-11 命中过近 → 严格小于命中距离（安全优先于构图下限）",
                PMBattleCameraGeometry.ResolveDistance(desired, 0.5f, minDistance, margin) < 0.5f
                && PMBattleCameraGeometry.ResolveDistance(desired, 0.5f, minDistance, margin) > 0f,
                "resolved=" + PMBattleCameraGeometry.ResolveDistance(desired, 0.5f, minDistance, margin));

            Check("B2-12 命中点已在想要位置更远处 → 不缩短（不误判）",
                Near(PMBattleCameraGeometry.ResolveDistance(desired, desired + 0.5f, minDistance, margin), desired, 1e-4f),
                "resolved=" + PMBattleCameraGeometry.ResolveDistance(desired, desired + 0.5f, minDistance, margin));

            Check("B2-13 无命中哨兵/NaN/≤0 一律视为无遮挡",
                Near(PMBattleCameraGeometry.ResolveDistance(desired, noOcclusion, minDistance, margin), desired, 1e-4f)
                && Near(PMBattleCameraGeometry.ResolveDistance(desired, float.NaN, minDistance, margin), desired, 1e-4f)
                && Near(PMBattleCameraGeometry.ResolveDistance(desired, 0f, minDistance, margin), desired, 1e-4f)
                && Near(PMBattleCameraGeometry.ResolveDistance(desired, -3f, minDistance, margin), desired, 1e-4f),
                "inf/nan/0/neg 均= " + desired);

            PMBattleCameraPose blocked = PMBattleCameraGeometry.Compute(
                origin, lookHeight, desired, yaw, pitch, nearClip, 3f, minDistance, margin);
            Check("B2-14 被遮挡时相机沿同一方向收短（方位角不变）",
                blocked.Occluded && Near(blocked.DistanceMeters, 3f - margin, 1e-4f)
                && Near(blocked.YawDegrees, yaw, 1e-6f),
                "distance=" + blocked.DistanceMeters + " yaw=" + blocked.YawDegrees);

            // (3b) 【T-VIS2b 反例，刻意先失败后修】极近遮挡：相机必须**严格**留在障碍物之前。
            //
            //   上一轮遗留缺陷（本轮任务确认的风险）：ResolveDistance 在
            //   `occlusion - margin <= effectiveMin` 时返回 effectiveMin = MinDistanceMeters，
            //   于是命中 1.5/0.5/0.1m 时相机被放到 1.6m —— 正好**在墙的后面**。
            //   下面这一组断言就是那个缺陷的失败证据（修复前应看到 FAIL）。
            float[] occlusionCases = new float[] { 0.1f, 0.5f, 1.5f };
            bool strictAll = true;
            bool positiveAll = true;
            bool selfConsistent = true;
            bool horizontalClear = true;
            string strictDetail = string.Empty;

            for (int i = 0; i < occlusionCases.Length; i++)
            {
                float occlusion = occlusionCases[i];

                PMBattleCameraPose pose = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, occlusion, minDistance, margin);

                if (!(pose.DistanceMeters < occlusion)) { strictAll = false; }
                if (!(pose.DistanceMeters > 0f)) { positiveAll = false; }

                float dx = pose.CameraPosition.X - pose.LookTarget.X;
                float dy = pose.CameraPosition.Y - pose.LookTarget.Y;
                float dz = pose.CameraPosition.Z - pose.LookTarget.Z;
                float toTarget = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (!Near(toTarget, pose.DistanceMeters, 1e-4f)) { selfConsistent = false; }

                // 非循环检查：相机相对"原始观察点"的水平后撤量也必须严格小于命中距离
                // （抬升观察目标不会改变水平后撤量，所以这条能独立证明相机没退到墙后）。
                float hx = pose.CameraPosition.X - origin.X;
                float hz = pose.CameraPosition.Z - origin.Z;
                float horizontal = (float)Math.Sqrt(hx * hx + hz * hz);

                if (!(horizontal < occlusion)) { horizontalClear = false; }

                strictDetail += "occ=" + occlusion.ToString("R") + "->d=" + pose.DistanceMeters.ToString("R")
                                + "(hz=" + horizontal.ToString("R") + ")  ";
            }

            Check("B2-20 【T-VIS2b 反例】命中 0.1/0.5/1.5m 时后距严格小于命中距离（旧 1.6 硬钳会放到墙后）",
                strictAll, strictDetail.Trim());
            Check("B2-21 极近遮挡下后距仍是有限正值（不做『距离归零』的伪修复）",
                positiveAll, strictDetail.Trim());
            Check("B2-22 相机到观察目标的真实距离 == DistanceMeters（三者自洽）",
                selfConsistent, strictDetail.Trim());
            Check("B2-23 【非循环】相机水平后撤量也严格小于命中距离（不会在水平面上退到墙后）",
                horizontalClear, strictDetail.Trim());

            // 【精确负例】把**旧公式**写在测试里算一遍，证明旧结果确实大于命中距离（= 墙后），
            // 而新结果同时满足"严格在墙前"。这样"1.6 硬钳"这个缺陷被逐条钉住，
            // 不会因为将来有人把 MinDistance 调小/注释掉而静默回归。
            float oldLimit = 0.5f - margin;
            float oldEffectiveMin = minDistance < desired ? minDistance : desired;
            float oldResult = oldLimit >= desired ? desired : (oldLimit <= oldEffectiveMin ? oldEffectiveMin : oldLimit);
            float newResult = PMBattleCameraGeometry.ResolveDistance(desired, 0.5f, minDistance, margin);

            Check("B2-24 【精确负例】旧公式命中 0.5m → 1.6m（> 0.5m，即墙后）；新结果必须更近且严格在墙前",
                Near(oldResult, minDistance, 1e-6f) && oldResult > 0.5f && newResult < 0.5f,
                "old=" + oldResult.ToString("R") + " new=" + newResult.ToString("R"));

            // (3c) 不把相机放进角色**权威胶囊**内部（否则会自遮挡/画面全黑）。
            //   权威胶囊（PMMoverDefaults）：中心 = state.Position，半高 1.0m、半径 0.4m
            //   ⇒ 胶囊占 [y-1, y+1] × 水平半径 0.4。观察高度 1.5m 已高于胶囊顶 0.5m，
            //   因此"极近遮挡"也不会把相机放进角色 Collider —— 这条把该结论钉成断言。
            bool outsideCapsule = true;
            string capsuleDetail = string.Empty;

            for (int i = 0; i < occlusionCases.Length; i++)
            {
                float occlusion = occlusionCases[i];

                PMBattleCameraPose pose = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, occlusion, minDistance, margin);

                float relY = pose.CameraPosition.Y - origin.Y;
                float cx = pose.CameraPosition.X - origin.X;
                float cz = pose.CameraPosition.Z - origin.Z;
                float radial = (float)Math.Sqrt(cx * cx + cz * cz);

                bool inside = relY > -PMMoverDefaults.CapsuleHalfHeightMeters
                              && relY < PMMoverDefaults.CapsuleHalfHeightMeters
                              && radial < PMMoverDefaults.CapsuleRadiusMeters;

                if (inside) { outsideCapsule = false; }

                capsuleDetail += "occ=" + occlusion.ToString("R") + "->relY=" + relY.ToString("R")
                                 + " radial=" + radial.ToString("R") + "  ";
            }

            Check("B2-25 极近遮挡下相机仍在角色权威胶囊之外（不在 Collider 内部，不自遮挡）",
                outsideCapsule, capsuleDetail.Trim());

            // (3d) 【T-VIS2b 反例：命中起点重叠 / 命中距离不可用】
            //   PhysX 对"球心已碰到或在障碍内"的命中报 distance ≈ 0，而 0 在本模块里是
            //   "无可用遮挡信息"的哨兵 ⇒ 旧实现会把"贴脸命中"静默当无遮挡，相机退回 6m（穿墙）。
            //   ResolveProbeOcclusion 把"至少一次命中"归一成有限正值，下面逐条钉住该不变式。
            float startOverlap = PMBattleCameraGeometry.ResolveProbeOcclusion(1, 0f);

            Check("B2-26 起点重叠（命中 distance=0）不得静默当无遮挡：归一为有限正值",
                startOverlap > 0f && PMBattleCameraGeometry.IsFinite(startOverlap)
                && startOverlap != PMBattleCameraGeometry.NoOcclusionDistance,
                "startOverlap=" + startOverlap.ToString("R"));

            Check("B2-27 只要有一次命中，命中距离不可用（NaN/±Inf/负）也不退回无遮挡",
                PMBattleCameraGeometry.ResolveProbeOcclusion(2, float.NaN) > 0f
                && PMBattleCameraGeometry.ResolveProbeOcclusion(2, float.PositiveInfinity) > 0f
                && PMBattleCameraGeometry.ResolveProbeOcclusion(2, -5f) > 0f
                && PMBattleCameraGeometry.ResolveProbeOcclusion(2, float.NegativeInfinity) > 0f,
                "nan/inf/neg 均归一为正值");

            Check("B2-28 只有『一次都没命中』才允许当无遮挡；有效命中距离原样透传",
                PMBattleCameraGeometry.ResolveProbeOcclusion(0, 0f) == PMBattleCameraGeometry.NoOcclusionDistance
                && PMBattleCameraGeometry.ResolveProbeOcclusion(0, float.NaN) == PMBattleCameraGeometry.NoOcclusionDistance
                && Near(PMBattleCameraGeometry.ResolveProbeOcclusion(3, 2f), 2f, 1e-6f),
                "0 命中 → 无遮挡；3 命中@2m → 2m");

            PMBattleCameraPose overlap = PMBattleCameraGeometry.Compute(
                origin, lookHeight, desired, yaw, pitch, nearClip, startOverlap, minDistance, margin);

            Check("B2-29 起点重叠时相机收到贴脸距离（不退到想要距离 = 穿墙）",
                overlap.DistanceMeters < startOverlap && overlap.DistanceMeters > 0f
                && overlap.DistanceMeters < 0.01f,
                "occ=" + startOverlap.ToString("R") + " d=" + overlap.DistanceMeters.ToString("R"));

            // (3e) 命中缓冲饱和：不能静默当无遮挡（但也不能强保证拿到的一定是最近命中）。
            Check("B2-30 饱和判定：count >= 容量才算饱和，且有命中时仍给出正距",
                PMBattleCameraGeometry.IsProbeSaturated(8, 8)
                && PMBattleCameraGeometry.IsProbeSaturated(9, 8)
                && !PMBattleCameraGeometry.IsProbeSaturated(7, 8)
                && !PMBattleCameraGeometry.IsProbeSaturated(0, 8)
                && PMBattleCameraGeometry.ResolveProbeOcclusion(8, 1.25f) > 0f,
                "sat(8,8)=True sat(7,8)=False");

            // (3f) 【属性扫描】不变式：任何 > 0 的命中距离下，结果都严格更小、不超过想要距离、且为正。
            //  这是"永远不穿墙"的逐点证据（而不是只验几个点）；
            //  同时检查"命中距离越小 ⇒ 后距不增"的单调性（防止抖动）。
            bool sweepStrict = true;
            bool sweepMonotone = true;
            float previous = float.NegativeInfinity;
            string sweepDetail = string.Empty;

            for (int i = 1; i <= 800; i++)
            {
                float occlusion = i * 0.01f;
                float resolved = PMBattleCameraGeometry.ResolveDistance(desired, occlusion, minDistance, margin);

                if (!(resolved < occlusion) || !(resolved > 0f) || resolved > desired)
                {
                    sweepStrict = false;
                    if (sweepDetail.Length == 0) { sweepDetail = "首次越界 occ=" + occlusion.ToString("R") + " d=" + resolved.ToString("R"); }
                }

                if (resolved < previous - 1e-5f)
                {
                    sweepMonotone = false;
                    sweepDetail = "非单调 occ=" + occlusion.ToString("R") + " d=" + resolved.ToString("R");
                }

                previous = resolved;
            }

            Check("B2-31 【属性扫描】命中 0.01…8m 共 800 点：结果严格小于命中距离、为正、不超想要距离",
                sweepStrict, sweepDetail);
            Check("B2-32 【属性扫描】命中距离减小 ⇒ 后距不增（无抖动）", sweepMonotone, sweepDetail);

            // (3g) 极近遮挡下近裁面也不得越过观察目标（T-VIS2b 的"相应调整 nearClip"）。
            bool nearClipSane = true;
            string nearClipDetail = string.Empty;

            for (int i = 0; i < occlusionCases.Length; i++)
            {
                float occlusion = occlusionCases[i];

                PMBattleCameraPose pose = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, occlusion, minDistance, margin);

                if (!(pose.NearClipMeters > 0f) || !(pose.NearClipMeters < pose.DistanceMeters))
                {
                    nearClipSane = false;
                }

                nearClipDetail += "occ=" + occlusion.ToString("R") + "->near=" + pose.NearClipMeters.ToString("R")
                                  + "/d=" + pose.DistanceMeters.ToString("R") + "  ";
            }

            Check("B2-33 极近遮挡下近裁面严格小于实际距离（不切掉观察目标）",
                nearClipSane
                && PMBattleCameraGeometry.ResolveNearClip(0.1f, 0.02f) < 0.02f
                && PMBattleCameraGeometry.ResolveNearClip(0.1f, 0.02f) > 0f,
                nearClipDetail.Trim() + " | 0.02m 距离 -> near="
                + PMBattleCameraGeometry.ResolveNearClip(0.1f, 0.02f).ToString("R"));

            Check("B2-34 近裁面拒绝非正/非有限实际距离（不把坏值写进相机）",
                ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.ResolveNearClip(0.1f, 0f))
                && ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.ResolveNearClip(0.1f, -1f))
                && ThrowsArgument(() => PMBattleCameraGeometry.ResolveNearClip(0.1f, float.NaN)));

            // (4) 近裁面：不为 0、不越过观察目标。
            Check("B2-15 近裁面钳制到距离的一半以内且不低于下限",
                Near(PMBattleCameraGeometry.ResolveNearClip(5f, 2f), 1f, 1e-5f)
                && Near(PMBattleCameraGeometry.ResolveNearClip(0.001f, 6f),
                       PMBattleCameraGeometry.MinNearClipMeters, 1e-6f)
                && Near(PMBattleCameraGeometry.ResolveNearClip(0.1f, 6f), 0.1f, 1e-6f),
                "5@2=" + PMBattleCameraGeometry.ResolveNearClip(5f, 2f)
                + " 0.001@6=" + PMBattleCameraGeometry.ResolveNearClip(0.001f, 6f));

            Check("B2-16 俯仰角钳制到 ±85（拒绝极端姿态）",
                Near(PMBattleCameraGeometry.ClampPitch(90f), 85f, 1e-5f)
                && Near(PMBattleCameraGeometry.ClampPitch(-90f), -85f, 1e-5f)
                && Near(PMBattleCameraGeometry.ClampPitch(12f), 12f, 1e-5f),
                "clamp(90)=" + PMBattleCameraGeometry.ClampPitch(90f));

            Check("B2-16b 遮挡探测半径是有限正值且小于最小后距（不会把相机推穿角色）",
                PMBattleCameraGeometry.ProbeRadiusMeters > 0f
                && !float.IsNaN(PMBattleCameraGeometry.ProbeRadiusMeters)
                && !float.IsInfinity(PMBattleCameraGeometry.ProbeRadiusMeters)
                && PMBattleCameraGeometry.ProbeRadiusMeters < PMBattleCameraGeometry.MinDistanceMeters,
                "probeRadius=" + PMBattleCameraGeometry.ProbeRadiusMeters
                + " minDistance=" + PMBattleCameraGeometry.MinDistanceMeters);

            // (5) 非法输入显式失败（不把 NaN 写进相机）。
            Check("B2-17 拒绝非有限角色位置",
                ThrowsArgument(() => PMBattleCameraGeometry.Compute(
                    new PMVector3(float.NaN, 0f, 0f), lookHeight, desired, yaw, pitch, nearClip,
                    noOcclusion, minDistance, margin)));
            Check("B2-18 拒绝非正后距/负边距/负最小距离",
                ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.ResolveDistance(0f, noOcclusion, minDistance, margin))
                && ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.ResolveDistance(desired, 3f, minDistance, -1f))
                && ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.ResolveDistance(desired, 3f, -1f, margin)));
            Check("B2-19 拒绝 NaN 观察高度",
                ThrowsArgument(() => PMBattleCameraGeometry.Compute(
                    origin, float.NaN, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin)));
        }

        // ================================================================== T-PLAY1 / T-PLAY2
        //
        //  本节对应用户冻结选型（Docs/plans/net-architecture-migration.md）：
        //    · T-PLAY1 旧透视俯视镜头：固定世界 yaw −90 / 俯角 68.191 / FOV60、旧相对取景 (6,12,0)；
        //    · T-PLAY2 旧移动转身：可见角色跟移动方向转身，且不把射击瞄准强绑到转身。
        //
        //  只读冻结证据（本文件里的常量就是它们，断言的期望值不靠记忆）：
        //    · Client/Assets/HYLD1.0/Resources/Main Camera.prefab：field of view 60、
        //      near clip plane 0.3、m_LocalEulerAnglesHint = (68.191, −90.00001, 0)；
        //    · 旧 HYLDCameraManger：相对偏移 (temp x=min(6,…), temp y=min(12,…))、
        //      z 冻结、SmoothDamp 0.08 只作用于位置（rotation 从不写）；
        //    · Client/Assets/Resources/Remake/Player.prefab：HYLDPlayerController.selfTransform
        //      指向 **Capsule** 子节点（旧代码旋转的就是可见节点）；
        //    · 烘焙产物 Resources/PMNet/PlayerVisualV1.prefab：根 PlayerVisualV1 旋转为单位阵，
        //      直系子节点 Capsule 烘焙 m_LocalEulerAnglesHint.y = 270。
        //
        //  为什么把资产/源码静态契约也放在这里：本工程是 net8 纯逻辑门，**不能**启动 Unity，
        //  可见朝向的最终事实（模型到底朝哪）只能在实机看。因此这里以“旧预制体确定值 +
        //  旧代码旋转哪个节点 + 烘焙层次未变”三条可离线核对的静态事实把契约钉住，
        //  不假装验证了实机画面（真实画面仍是 PENDING_USER）。

        /// <summary>旧预制体 Main Camera.prefab 的 FOV（透视，不是正式源场景的正交尺寸 17）。</summary>
        private const float LegacyPrefabFieldOfViewDegrees = 60f;

        /// <summary>旧预制体 Main Camera.prefab 的俯仰/方位角（m_LocalEulerAnglesHint）。</summary>
        private const float LegacyPrefabPitchDegrees = 68.191f;
        private const float LegacyPrefabYawDegrees = -90f;

        /// <summary>旧预制体 Main Camera.prefab 的近裁面。</summary>
        private const float LegacyPrefabNearClipMeters = 0.3f;

        /// <summary>旧 HYLDCameraManger 的相机相对角色偏移（世界轴 (6,12,0)，z 冻结）。</summary>
        private const float LegacyFollowHorizontalMeters = 6f;
        private const float LegacyFollowHeightMeters = 12f;

        /// <summary>烘焙/源角色 prefab 里 Capsule 子节点自带的本地 Yaw（旧可见节点）。</summary>
        private const float LegacyCapsuleBakedLocalYawDegrees = 270f;

        private static void TestPlayCameraFraming()
        {
            Section("B3. T-PLAY1 旧透视俯视取景（旧预制体确定值 + 旧相对轴跟随）");

            float desired = PMBattleCameraGeometry.DefaultDistanceMeters;
            float yaw = PMBattleCameraGeometry.DefaultYawDegrees;
            float pitch = PMBattleCameraGeometry.DefaultPitchDegrees;
            float lookHeight = PMBattleCameraGeometry.DefaultLookHeightMeters;
            float nearClip = PMBattleCameraGeometry.DefaultNearClipMeters;
            float minDistance = PMBattleCameraGeometry.MinDistanceMeters;
            float margin = PMBattleCameraGeometry.OcclusionMarginMeters;
            float noOcclusion = PMBattleCameraGeometry.NoOcclusionDistance;

            Check("B3-1 相机世界 yaw 取旧预制体 −90（固定常量，与角色/移动方向无关）",
                Near(yaw, LegacyPrefabYawDegrees, 1e-4f),
                "yaw=" + yaw.ToString("R") + "（期望 " + LegacyPrefabYawDegrees + "）");

            Check("B3-2 相机俯仰角取旧预制体 68.191（不再是诊断用 12）",
                Near(pitch, LegacyPrefabPitchDegrees, 1e-3f),
                "pitch=" + pitch.ToString("R") + "（期望 " + LegacyPrefabPitchDegrees + "）");

            Check("B3-3 近裁面取旧预制体 0.3",
                Near(nearClip, LegacyPrefabNearClipMeters, 1e-4f),
                "nearClip=" + nearClip.ToString("R") + "（期望 " + LegacyPrefabNearClipMeters + "）");

            float legacyOffsetLength = (float)Math.Sqrt(
                (double)(LegacyFollowHorizontalMeters * LegacyFollowHorizontalMeters
                         + LegacyFollowHeightMeters * LegacyFollowHeightMeters));

            Check("B3-4 后距 = 旧相对偏移 (6,12,0) 的模长 ≈ 13.416（不是诊断用 6）",
                Near(desired, legacyOffsetLength, 1e-3f) && desired > 10f,
                "distance=" + desired.ToString("R") + " 旧偏移模长=" + legacyOffsetLength.ToString("R"));

            Check("B3-5 相机 FOV 取旧预制体 60（透视，不是正式源场景的正交尺寸 17）",
                Near(PMBattleCameraGeometry.LegacyPrefabFieldOfViewDegrees, LegacyPrefabFieldOfViewDegrees, 1e-4f),
                "fov=" + PMBattleCameraGeometry.LegacyPrefabFieldOfViewDegrees.ToString("R"));

            PMVector3 back = PMBattleCameraGeometry.BackDirection(yaw, pitch);

            Check("B3-6 后向向量落回旧相对轴（+X 水平后撤、+Y 抬升、|Z|≈0 ⇒ 屏幕水平轴是世界 Z）",
                back.X > 0f && back.Y > 0f && Math.Abs(back.Z) < 1e-5f && Near(back.Length, 1f, 1e-5f),
                "back=(" + back.X.ToString("R") + ", " + back.Y.ToString("R") + ", "
                + back.Z.ToString("R") + ")");

            PMVector3 origin = new PMVector3(3f, 1f, -5f);

            PMBattleCameraPose free = PMBattleCameraGeometry.Compute(
                origin, lookHeight, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin);

            float dx = free.CameraPosition.X - free.LookTarget.X;
            float dz = free.CameraPosition.Z - free.LookTarget.Z;
            float horizontal = (float)Math.Sqrt((double)(dx * dx + dz * dz));
            float vertical = free.CameraPosition.Y - free.LookTarget.Y;

            Check("B3-7 相机在角色右上方俯视：水平后撤与竖直抬升都落在旧 (6,12) 量级",
                horizontal > LegacyFollowHorizontalMeters * 0.6f && horizontal < LegacyFollowHorizontalMeters
                && vertical > LegacyFollowHeightMeters * 0.8f && vertical < LegacyFollowHeightMeters * 1.3f,
                "horizontal=" + horizontal.ToString("R") + " vertical=" + vertical.ToString("R"));

            Check("B3-8 相机与观察目标同 Z（旧链 z 冻结，不随角色漂移）",
                Math.Abs(dz) < 1e-4f,
                "camZ=" + free.CameraPosition.Z.ToString("R") + " lookZ=" + free.LookTarget.Z.ToString("R"));

            Check("B3-9 水平投影 = 后距 × cos(pitch)，且竖直抬升远大于水平后撤（俯视而非近地面）",
                Near(horizontal, desired * (float)Math.Cos(pitch * (Math.PI / 180.0)), 1e-3f)
                && vertical > horizontal * 2f,
                "horizontal=" + horizontal.ToString("R") + " vertical=" + vertical.ToString("R")
                + " cos(pitch)=" + Math.Cos(pitch * (Math.PI / 180.0)).ToString("R"));

            // 【初次跳变负例】宿主在 rig 建立时会立刻用同一状态摆一次相机；若“首帧解”与
            // “稳定帧解”不一致，实机就会在进场瞬间看到镜头跳一下。纯求解必须逐位可复现。
            bool firstFrameStable = true;
            string firstFrameDetail = string.Empty;

            for (int i = 0; i < 3; i++)
            {
                PMBattleCameraPose again = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin);

                if (!Near(again.CameraPosition.X, free.CameraPosition.X, 0f)
                    || !Near(again.CameraPosition.Y, free.CameraPosition.Y, 0f)
                    || !Near(again.CameraPosition.Z, free.CameraPosition.Z, 0f)
                    || !Near(again.YawDegrees, free.YawDegrees, 0f)
                    || !Near(again.PitchDegrees, free.PitchDegrees, 0f))
                {
                    firstFrameStable = false;
                    firstFrameDetail = "第 " + (i + 1) + " 次求解与首帧不一致";
                }
            }

            Check("B3-10 【初次跳变负例】同一状态重复求解逐位一致（首帧摆位 == 稳定帧）",
                firstFrameStable,
                string.IsNullOrEmpty(firstFrameDetail) ? "三次求解逐位一致" : firstFrameDetail);

            // 整帧位姿（位置 + 朝向）与角色 Yaw 无关：Compute 连 owner Yaw 入参都没有。
            float[] ownerYaws = new float[] { 0f, 90f, -90f, 180f, 359f };
            bool poseStable = true;
            string poseDetail = "5 个角色 Yaw 位姿逐点一致";

            for (int i = 0; i < ownerYaws.Length; i++)
            {
                PMBattleCameraPose pose = PMBattleCameraGeometry.Compute(
                    origin, lookHeight, desired, yaw, pitch, nearClip, noOcclusion, minDistance, margin);

                if (!Near(pose.CameraPosition.X, free.CameraPosition.X, 0f)
                    || !Near(pose.CameraPosition.Y, free.CameraPosition.Y, 0f)
                    || !Near(pose.CameraPosition.Z, free.CameraPosition.Z, 0f)
                    || !Near(pose.YawDegrees, free.YawDegrees, 0f))
                {
                    poseStable = false;
                    poseDetail = "ownerYaw=" + ownerYaws[i] + " 改变了整帧位姿";
                }
            }

            Check("B3-11 左右键（角色 Yaw 0/±90/180/359）都不改变整帧位姿（位置+朝向逐点一致）",
                poseStable, poseDetail);
        }

        // ------------------------------------------------------------------ B4. T-PLAY1 旧镜头平滑跟随

        private static void TestPlayFollowSmoothing()
        {
            Section("B4. T-PLAY1 旧镜头平滑跟随（临界阻尼 + 首帧/大跳变直接贴合）");

            // (1) 同一观察目标求解：Compute 与 ComputeAtLookTarget 必须逐位一致
            //     （宿主用后者喂平滑后的目标，两者不能是两套几何）。
            PMVector3 ownerPos = new PMVector3(2f, 0f, 7f);
            float lookHeight = PMBattleCameraGeometry.DefaultLookHeightMeters;
            float desired = PMBattleCameraGeometry.DefaultDistanceMeters;

            PMBattleCameraPose viaOwner = PMBattleCameraGeometry.Compute(
                ownerPos, lookHeight, desired, PMBattleCameraGeometry.DefaultYawDegrees,
                PMBattleCameraGeometry.DefaultPitchDegrees, PMBattleCameraGeometry.DefaultNearClipMeters,
                PMBattleCameraGeometry.NoOcclusionDistance, PMBattleCameraGeometry.MinDistanceMeters,
                PMBattleCameraGeometry.OcclusionMarginMeters);

            PMBattleCameraPose viaLook = PMBattleCameraGeometry.ComputeAtLookTarget(
                PMBattleCameraGeometry.ComputeLookTarget(ownerPos, lookHeight), desired,
                PMBattleCameraGeometry.DefaultYawDegrees, PMBattleCameraGeometry.DefaultPitchDegrees,
                PMBattleCameraGeometry.DefaultNearClipMeters, PMBattleCameraGeometry.NoOcclusionDistance,
                PMBattleCameraGeometry.MinDistanceMeters, PMBattleCameraGeometry.OcclusionMarginMeters);

            Check("B4-1 Compute 与 ComputeAtLookTarget 同解（平滑后的目标不会走另一套几何）",
                Near(viaOwner.CameraPosition.X, viaLook.CameraPosition.X, 0f)
                && Near(viaOwner.CameraPosition.Y, viaLook.CameraPosition.Y, 0f)
                && Near(viaOwner.CameraPosition.Z, viaLook.CameraPosition.Z, 0f)
                && Near(viaOwner.YawDegrees, viaLook.YawDegrees, 0f),
                "owner 解=" + viaOwner.CameraPosition.X + "/" + viaOwner.CameraPosition.Y
                + " look 解=" + viaLook.CameraPosition.X + "/" + viaLook.CameraPosition.Y);

            // (2) 小步追踪：缓慢逼近、不过冲、最终收敛。
            const float dt = 1f / 60f;
            float smoothTime = PMBattleCameraGeometry.FollowSmoothTimeSeconds;
            float snap = PMBattleCameraGeometry.FollowSnapDistanceMeters;

            PMVector3 current = new PMVector3(0f, 0f, 0f);
            PMVector3 velocity = new PMVector3(0f, 0f, 0f);
            PMVector3 target = new PMVector3(1f, 0f, 0f);

            PMVector3 firstStep = PMBattleCameraGeometry.SmoothFollowPosition(
                current, target, ref velocity, smoothTime, dt, snap);

            Check("B4-2 追赶不平移直送：第一帧只走一小段（既非 0 也远小于 1m）",
                firstStep.X > 0.01f && firstStep.X < 0.25f,
                "firstStep.X=" + firstStep.X.ToString("R"));

            bool monotone = true;
            bool noOvershoot = true;
            PMVector3 position = current;
            string trace = string.Empty;

            for (int i = 0; i < 60; i++)
            {
                PMVector3 next = PMBattleCameraGeometry.SmoothFollowPosition(
                    position, target, ref velocity, smoothTime, dt, snap);

                if (!(next.X >= position.X - 1e-6f)) { monotone = false; }
                if (next.X > 1f + 1e-5f) { noOvershoot = false; }

                if (i < 4 || i == 59)
                {
                    trace += "f" + i + "=" + next.X.ToString("R") + " ";
                }

                position = next;
            }

            Check("B4-3 60 帧（1s）内单调逼近且不过冲（不振荡）", monotone && noOvershoot, trace.Trim());
            Check("B4-4 平滑最终收敛到目标（误差 ≤ 1mm）",
                Math.Abs(position.X - 1f) <= 1e-3f, "finalX=" + position.X.ToString("R"));

            // (3) 大跳变直接贴合：出生/瞬移/重同步不得看到镜头滑过去。
            PMVector3 farVelocity = new PMVector3(3f, 0f, 0f);
            PMVector3 farTarget = new PMVector3(snap + 12f, 0f, 0f);
            PMVector3 snapped = PMBattleCameraGeometry.SmoothFollowPosition(
                current, farTarget, ref farVelocity, smoothTime, dt, snap);

            Check("B4-5 跳变 ≥ 贴合阈值时直接到位（并把速度归零，不留拖尾）",
                Near(snapped.X, farTarget.X, 0f) && Near(farVelocity.X, 0f, 0f),
                "snapped=" + snapped.X.ToString("R") + " velocity=" + farVelocity.X.ToString("R"));

            // (4) 零帧时长不平滑（也不得把位置推走）。
            PMVector3 zeroVelocity = new PMVector3(0f, 0f, 0f);
            PMVector3 zeroStep = PMBattleCameraGeometry.SmoothFollowPosition(
                new PMVector3(0.3f, 0f, 0f), target, ref zeroVelocity, smoothTime, 0f, snap);

            Check("B4-6 Δt=0 时保持原位（缺帧/暂停帧不得把相机拎走）",
                Near(zeroStep.X, 0.3f, 1e-6f), "zeroStep.X=" + zeroStep.X.ToString("R"));

            // (5) 非法输入显式失败（不把 NaN/负时长写进相机）。
            PMVector3 badVelocity = new PMVector3(0f, 0f, 0f);

            Check("B4-7 拒绝非正平滑时间/负帧时长/非正贴合阈值",
                ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.SmoothFollowPosition(
                    current, target, ref badVelocity, 0f, dt, snap))
                && ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.SmoothFollowPosition(
                    current, target, ref badVelocity, smoothTime, -0.01f, snap))
                && ThrowsArgumentOutOfRange(() => PMBattleCameraGeometry.SmoothFollowPosition(
                    current, target, ref badVelocity, smoothTime, dt, 0f)),
                "smoothTime=0 / dt<0 / snap=0 均抛最值异常");

            Check("B4-8 拒绝非有限位置/目标（不把 NaN 写进相机）",
                ThrowsArgument(() => PMBattleCameraGeometry.SmoothFollowPosition(
                    new PMVector3(float.NaN, 0f, 0f), target, ref badVelocity, smoothTime, dt, snap))
                && ThrowsArgument(() => PMBattleCameraGeometry.SmoothFollowPosition(
                    current, new PMVector3(0f, float.PositiveInfinity, 0f), ref badVelocity,
                    smoothTime, dt, snap)),
                "NaN 起点 / +Inf 目标 均抛参数异常");

            // (6) 旧链手感值必须真的是 0.08（不得被人静默改成别的）。
            Check("B4-9 平滑时间就是旧链 HYLDCameraManger.SmoothTime = 0.08s",
                Near(PMBattleCameraGeometry.FollowSmoothTimeSeconds, 0.08f, 1e-6f),
                "smoothTime=" + PMBattleCameraGeometry.FollowSmoothTimeSeconds.ToString("R"));
        }

        private static void TestPlayFacingContract()
        {
            Section("D. T-PLAY2 可见角色转身（旧 Capsule 节点承载 yaw，不叠加烘焙的 270）");

            // D1：旧组合（把权威 Yaw 写进**角色根**，Capsule 自带的 270 参与叠加）
            //     ⇒ 可见朝向 = 权威 Yaw + 270：“角色转身和画面看到的朝向对不上”的量化形态。
            float legacyVisible = PMBattleCameraGeometry.LegacyHierarchyChildCameraWorldYaw(
                90f, LegacyCapsuleBakedLocalYawDegrees);

            Check("D1 旧组合（权威 90 + 烘焙 270 = 0）可见朝向偏离权威 Yaw 270°",
                Near(legacyVisible, 0f, 1e-4f) && !Near(legacyVisible, 90f, 1e-4f),
                "legacyVisible=" + legacyVisible.ToString("R"));

            // D2：修复后的组合——把权威 Yaw 直接写在**可见节点**上，烘焙偏转不参与。
            float fixedVisible = PMBattleCameraGeometry.VisibleFacingYawDegrees(90f);

            Check("D2 修复后可见朝向 == 权威 Mover Yaw（烘焙 270 不参与可见朝向）",
                Near(fixedVisible, 90f, 1e-4f), "fixedVisible=" + fixedVisible.ToString("R"));

            Check("D3 可见朝向函数不接受烘焙偏转入参（结构上不可能再叠加 270）",
                Near(PMBattleCameraGeometry.VisibleFacingYawDegrees(-400f), -40f, 1e-4f)
                && Near(PMBattleCameraGeometry.VisibleFacingYawDegrees(359f), -1f, 1e-4f),
                "visible(-400)=" + PMBattleCameraGeometry.VisibleFacingYawDegrees(-400f)
                + " visible(359)=" + PMBattleCameraGeometry.VisibleFacingYawDegrees(359f));

            Check("D4 烘焙偏转常量就是旧 Capsule 的 270（负例的基准值不得漂移）",
                Near(PMBattleCameraGeometry.LegacyCapsuleBakedLocalYawDegrees,
                     LegacyCapsuleBakedLocalYawDegrees, 1e-4f),
                "baked=" + PMBattleCameraGeometry.LegacyCapsuleBakedLocalYawDegrees.ToString("R"));

            // 对一整圈权威 Yaw：可见朝向必须逐点等于权威 Yaw（不是 +270 也不是 −270）。
            bool allMatch = true;
            string yawDetail = string.Empty;
            float[] yaws = new float[] { 0f, 45f, 90f, -90f, 135f, 180f, -135f, 359f };

            for (int i = 0; i < yaws.Length; i++)
            {
                float visible = PMBattleCameraGeometry.VisibleFacingYawDegrees(yaws[i]);
                float expected = PMBattleCameraGeometry.NormalizeDegrees(yaws[i]);

                if (!Near(visible, expected, 1e-4f)) { allMatch = false; }

                yawDetail += "yaw=" + yaws[i] + "->visible=" + visible.ToString("R") + "  ";
            }

            Check("D5 一整圈权威 Yaw（0/±45/±90/135/180/−135/359）可见朝向逐点等于权威 Yaw",
                allMatch, yawDetail.Trim());

            TestPlayFacingAssets();
            TestPlayFacingSourceContracts();
        }

        /// <summary>
        /// 资产静态契约：烘焙产物与旧源 prefab 的可见层次/烘焙偏转必须仍是“旧 Capsule 转身”的形状。
        /// 本项不启动 Unity，只读 prefab YAML 的 Transform/GameObject 事实。
        /// </summary>
        private static void TestPlayFacingAssets()
        {
            // ---- 烘焙产物：PlayerVisualV1 的根/Capsule/Animator 关系 ----
            string visualPath = FindRepoFile("Client/Assets/Resources/PMNet/PlayerVisualV1.prefab");
            string visualText = visualPath == null ? null : System.IO.File.ReadAllText(visualPath);

            Check("D6 正式角色表现 prefab 存在（可见朝向契约的核对对象）",
                visualText != null,
                visualPath == null ? "未找到 Resources/PMNet/PlayerVisualV1.prefab" : visualPath);

            if (visualText != null)
            {
                PrefabModel model = ParsePrefab(visualText);

                string rootTransformId = model.RootTransformId();
                string rootName = rootTransformId == null ? null : model.GameObjectName(rootTransformId);

                Check("D7 烘焙角色根是 PlayerVisualV1 且根旋转为单位阵（根不参与可见朝向）",
                    rootName == "PlayerVisualV1"
                    && rootTransformId != null
                    && IsIdentityRotation(model.Transforms[rootTransformId].LocalRotation),
                    "root=" + (rootName ?? "<null>") + " rotation="
                    + (rootTransformId == null ? "<null>" : model.Transforms[rootTransformId].LocalRotation));

                List<PrefabTransform> capsuleChildren = model.DirectChildrenNamed(rootTransformId, "Capsule");

                Check("D8 烘焙角色根的直系子节点恰好一个 Capsule（可见转身节点的严格查找目标）",
                    capsuleChildren.Count == 1,
                    "count=" + capsuleChildren.Count);

                if (capsuleChildren.Count == 1)
                {
                    PrefabTransform capsule = capsuleChildren[0];
                    float baked = ParseFloat(capsule.EulerY);

                    Check("D9 Capsule 烘焙本地 Yaw 恰为 270（新链必须不叠加它）",
                        Near(baked, LegacyCapsuleBakedLocalYawDegrees, 1e-3f),
                        "baked=" + capsule.EulerY + " eulerHint");

                    Check("D10 可见模型（Animator）在 Capsule 之下 ⇒ 转 Capsule 就是转可见角色",
                        model.HasDescendantAnimator(capsule.FileId),
                        "capsule=" + capsule.FileId);
                }
            }

            // ---- 旧源 prefab：旧代码旋转的确实是 Capsule 子节点 ----
            string legacyPath = FindRepoFile("Client/Assets/Resources/Remake/Player.prefab");
            string legacyText = legacyPath == null ? null : System.IO.File.ReadAllText(legacyPath);
            string controllerMetaPath = FindRepoFile(
                "Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs.meta");
            string controllerMeta = controllerMetaPath == null ? null : System.IO.File.ReadAllText(controllerMetaPath);

            Check("D11 旧源角色 prefab 与 HYLDPlayerController 元数据都在（旧转身约定的证据源）",
                legacyText != null && controllerMeta != null,
                (legacyPath ?? "<缺 Player.prefab>") + " | " + (controllerMetaPath ?? "<缺 meta>"));

            if (legacyText != null && controllerMeta != null)
            {
                string controllerGuid = MetaGuid(controllerMeta);
                PrefabModel model = ParsePrefab(legacyText);
                string rootTransformId = model.RootTransformId();
                string rootName = rootTransformId == null ? null : model.GameObjectName(rootTransformId);

                Check("D12 旧源角色根名为 Player（与烘焙产物的层次同形）",
                    rootName == "Player", "root=" + (rootName ?? "<null>"));

                List<PrefabTransform> capsuleChildren = model.DirectChildrenNamed(rootTransformId, "Capsule");
                string selfTransform = model.ControllerTransformReference(controllerGuid);

                Check("D13 【旧转身约定】HYLDPlayerController.selfTransform 指向 Capsule 子节点（不是根）",
                    controllerGuid != null && capsuleChildren.Count == 1
                    && selfTransform == capsuleChildren[0].FileId,
                    "guid=" + (controllerGuid ?? "<null>") + " selfTransform=" + (selfTransform ?? "<null>")
                    + " capsule=" + (capsuleChildren.Count == 1 ? capsuleChildren[0].FileId : "<非唯一>"));
            }
        }

        /// <summary>
        /// 源码静态契约：本轮允许写入的三个源文件必须保持“根做位置/scale + 可见节点做 yaw +
        /// 相机只给本地 owner + 取景常量单一来源”的形状。
        ///
        /// 为什么用静态契约而不是行为断言：PMUnityBattlePresentation 依赖 GameObject/Transform/
        /// Animator/Resources，在本工程里只能用替身，而那等于“假验表现层”（本工程明确拒绝）。
        /// 行为事实（模型朝哪、镜头看起来如何）只能在 Unity 实机（T-PLAY5）确认。
        /// </summary>
        private static void TestPlayFacingSourceContracts()
        {
            string presentationPath = FindRepoFile("Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs");
            string presentation = presentationPath == null ? null : System.IO.File.ReadAllText(presentationPath);

            Check("D14 正式角色表现源码可读（可见朝向的写入点核对对象）",
                presentation != null, presentationPath ?? "<未找到 PMUnityBattlePresentation.cs>");

            if (presentation != null)
            {
                string compact = Compact(presentation);

                Check("D15 角色根不再承载 yaw（旧的 _root.transform.rotation = Euler(0, state.Yaw, 0) 已移除）",
                    !compact.Contains("_root.transform.rotation=Quaternion.Euler(0f,state.YawDegrees,0f)"),
                    "根不得再写 state.YawDegrees");

                Check("D16 可见 yaw 写在 Capsule 节点上（_facingNode.rotation = Euler(0, state.Yaw, 0)）",
                    compact.Contains("_facingNode.rotation=Quaternion.Euler(0f,state.YawDegrees,0f)"),
                    "可见节点承载权威 yaw");

                Check("D17 缺 Capsule 的正式内容显式失败 + 根仍写位置/scale",
                    compact.Contains("FacingNodeName") && compact.Contains("\"Capsule\"")
                    && compact.Contains("_root.transform.position=")
                    && compact.Contains("_root.transform.localScale="),
                    "Capsule 严格查找 + 根做位置/scale");
            }

            string hostPath = FindRepoFile("Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs");
            string host = hostPath == null ? null : System.IO.File.ReadAllText(hostPath);

            Check("D18 宿主源码可读（相机接线核对对象）", host != null, hostPath ?? "<未找到 PMClientSessionHost.cs>");

            if (host != null)
            {
                string compactHost = Compact(host);
                string ensureRig = ExtractMethod(compactHost, "private static MovementRig EnsureMovementRig");

                Check("D19 相机只给本地 owner 建（EnsureMovementRig 内 CreateTestCamera 与 isOwner 守卫同处）",
                    ensureRig != null && ensureRig.Contains("CreateTestCamera(") && ensureRig.Contains("if(isOwner)"),
                    ensureRig == null ? "未定位到 EnsureMovementRig" : "owner 守卫存在");

                Check("D20 相机取景常量单一来源 = 纯几何模块的旧预制体常量（宿主引用 FOV/俯仰）",
                    compactHost.Contains("PMBattleCameraGeometry.LegacyPrefabFieldOfViewDegrees")
                    && compactHost.Contains("PMBattleCameraGeometry.DefaultPitchDegrees"),
                    "宿主引用旧预制体取景常量");
            }
        }

        // ------------------------------------------------------------------ 静态契约辅助

        /// <summary>去掉所有空白，便于对多行语句做稳定的子串契约核对。</summary>
        private static string Compact(string text)
        {
            return Regex.Replace(text, "\\s+", string.Empty);
        }

        /// <summary>取“从某方法签名到下一个 `private static` 成员”之间的紧凑文本（方法体近似）。</summary>
        private static string ExtractMethod(string compactSource, string signature)
        {
            string needle = Compact(signature);
            int start = compactSource.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) { return null; }

            int end = compactSource.IndexOf("privatestatic", start + needle.Length, StringComparison.Ordinal);
            if (end < 0) { end = compactSource.Length; }

            return compactSource.Substring(start, end - start);
        }

        /// <summary>从测试程序集目录向上查找仓库内文件（不依赖 CWD）；找不到返回 null。</summary>
        private static string FindRepoFile(string relativePath)
        {
            System.IO.DirectoryInfo dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            string tail = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

            for (int i = 0; i < 12 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, tail);
                if (System.IO.File.Exists(candidate)) { return candidate; }
                dir = dir.Parent;
            }

            return null;
        }

        private static string MetaGuid(string metaText)
        {
            Match match = Regex.Match(metaText, "^guid: ([0-9a-fA-F]+)", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static float ParseFloat(string text)
        {
            float value;
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out value)
                ? value : float.NaN;
        }

        private static bool IsIdentityRotation(string localRotation)
        {
            if (string.IsNullOrEmpty(localRotation)) { return false; }

            Match match = Regex.Match(localRotation, "\\{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)\\}");
            if (!match.Success) { return false; }

            return Near(ParseFloat(match.Groups[1].Value), 0f, 1e-6f)
                && Near(ParseFloat(match.Groups[2].Value), 0f, 1e-6f)
                && Near(ParseFloat(match.Groups[3].Value), 0f, 1e-6f)
                && Near(ParseFloat(match.Groups[4].Value), 1f, 1e-6f);
        }

        private sealed class PrefabTransform
        {
            public string FileId;
            public string GameObjectId;
            public string FatherId;
            public string EulerY;
            public string LocalRotation;
        }

        private sealed class PrefabBlock
        {
            public string ClassId;
            public string FileId;
            public string Text;
        }

        private sealed class PrefabModel
        {
            public readonly Dictionary<string, string> GameObjectNames = new Dictionary<string, string>();
            public readonly Dictionary<string, PrefabTransform> Transforms = new Dictionary<string, PrefabTransform>();
            public readonly List<PrefabBlock> Blocks = new List<PrefabBlock>();

            /// <summary>根 Transform（m_Father = 0）；没有就返回 null。</summary>
            public string RootTransformId()
            {
                foreach (KeyValuePair<string, PrefabTransform> pair in Transforms)
                {
                    if (pair.Value.FatherId == "0") { return pair.Value.FileId; }
                }

                return null;
            }

            public string GameObjectName(string transformId)
            {
                PrefabTransform transform;
                if (!Transforms.TryGetValue(transformId, out transform)) { return null; }

                string name;
                return GameObjectNames.TryGetValue(transform.GameObjectId, out name) ? name : null;
            }

            /// <summary>某 Transform 的直系子节点里名字等于 name 的那些。</summary>
            public List<PrefabTransform> DirectChildrenNamed(string fatherTransformId, string name)
            {
                List<PrefabTransform> found = new List<PrefabTransform>();
                if (fatherTransformId == null) { return found; }

                foreach (KeyValuePair<string, PrefabTransform> pair in Transforms)
                {
                    if (pair.Value.FatherId != fatherTransformId) { continue; }
                    if (GameObjectName(pair.Value.FileId) == name) { found.Add(pair.Value); }
                }

                return found;
            }

            /// <summary>从 father 出发沿 m_Father 上溯，是否经过 ancestorTransformId。</summary>
            public bool DescendsFrom(string transformId, string ancestorTransformId)
            {
                string current = transformId;

                for (int i = 0; i < 512 && current != null; i++)
                {
                    if (current == ancestorTransformId) { return true; }

                    PrefabTransform transform;
                    if (!Transforms.TryGetValue(current, out transform)) { return false; }
                    current = transform.FatherId == "0" ? null : transform.FatherId;
                }

                return false;
            }

            /// <summary>是否存在 Animator（!u!95），其所在 GameObject 在 rootTransformId 之下。</summary>
            public bool HasDescendantAnimator(string rootTransformId)
            {
                for (int i = 0; i < Blocks.Count; i++)
                {
                    PrefabBlock block = Blocks[i];
                    if (block.ClassId != "95") { continue; }

                    string owner = RefId(block.Text, "m_GameObject");
                    if (owner == null) { continue; }

                    foreach (KeyValuePair<string, PrefabTransform> pair in Transforms)
                    {
                        if (pair.Value.GameObjectId == owner && DescendsFrom(pair.Value.FileId, rootTransformId))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            /// <summary>
            /// 取“脚本 guid == scriptGuid 的 MonoBehaviour（!u!114）”的 selfTransform 引用。
            /// 这是旧 HYLDPlayerController 旋转哪个节点的直接证据。
            /// </summary>
            public string ControllerTransformReference(string scriptGuid)
            {
                if (string.IsNullOrEmpty(scriptGuid)) { return null; }

                for (int i = 0; i < Blocks.Count; i++)
                {
                    PrefabBlock block = Blocks[i];
                    if (block.ClassId != "114") { continue; }

                    Match script = Regex.Match(
                        block.Text, "m_Script: \\{fileID: 11500000, guid: ([0-9a-fA-F]+)");
                    if (!script.Success) { continue; }
                    if (!string.Equals(script.Groups[1].Value, scriptGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return RefId(block.Text, "selfTransform");
                }

                return null;
            }
        }

        private static List<PrefabBlock> SplitPrefab(string text)
        {
            List<PrefabBlock> blocks = new List<PrefabBlock>();
            string[] parts = Regex.Split(text, "^--- ", RegexOptions.Multiline);

            for (int i = 0; i < parts.Length; i++)
            {
                Match head = Regex.Match(parts[i], "^!u!(\\d+) &(\\d+)");
                if (!head.Success) { continue; }

                PrefabBlock block = new PrefabBlock();
                block.ClassId = head.Groups[1].Value;
                block.FileId = head.Groups[2].Value;
                block.Text = parts[i];
                blocks.Add(block);
            }

            return blocks;
        }

        private static PrefabModel ParsePrefab(string text)
        {
            PrefabModel model = new PrefabModel();
            model.Blocks.AddRange(SplitPrefab(text));

            for (int i = 0; i < model.Blocks.Count; i++)
            {
                PrefabBlock block = model.Blocks[i];

                if (block.ClassId == "1")
                {
                    string name = ScalarField(block.Text, "m_Name");
                    model.GameObjectNames[block.FileId] = name ?? string.Empty;
                }
                else if (block.ClassId == "4")
                {
                    PrefabTransform transform = new PrefabTransform();
                    transform.FileId = block.FileId;
                    transform.GameObjectId = RefId(block.Text, "m_GameObject");
                    transform.FatherId = RefId(block.Text, "m_Father");
                    transform.EulerY = EulerY(block.Text);
                    transform.LocalRotation = ScalarField(block.Text, "m_LocalRotation");
                    model.Transforms[block.FileId] = transform;
                }
            }

            return model;
        }

        /// <summary>取 `field: {fileID: N}` 冒号后的 N（找不到返回 null）。</summary>
        private static string RefId(string blockText, string field)
        {
            Match match = Regex.Match(blockText, Regex.Escape(field) + @": \{fileID: (-?\d+)\}");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>取两空格缩进的单行标量字段（找不到返回 null）。</summary>
        private static string ScalarField(string blockText, string field)
        {
            Match match = Regex.Match(blockText, "^  " + Regex.Escape(field) + @": (.+?)\r?$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        /// <summary>取 m_LocalEulerAnglesHint 的 y 分量（找不到返回 null）。</summary>
        private static string EulerY(string blockText)
        {
            Match match = Regex.Match(blockText,
                @"m_LocalEulerAnglesHint: \{x: [^,]+, y: ([^,]+), z: [^}]+\}");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static bool ThrowsArgumentOutOfRange(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
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

        // T-MOVE1：用固定预期（而非转换函数的输出当预期）对比四方向。修前必须红：
        // 相机世界 yaw−90 时屏幕上=-X、右=+Z，键盘/方向键/旧虚拟轴须同源。
        private static void TestKeyboardMatchesStick()
        {
            Section("M. WASD/方向键/旧虚拟轴与屏幕摇杆四方向一致");
            KeyCode[] keys = { KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D,
                               KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow };
            float[] expectedX = { -1f, 1f, 0f, 0f, -1f, 1f, 0f, 0f };
            float[] expectedZ = { 0f, 0f, -1f, 1f, 0f, 0f, -1f, 1f };
            for (int i = 0; i < keys.Length; i++)
            {
                Input.ResetStub();
                Press(keys[i]);
                PMMoverInput sample = new PMUnityMoverInput().Sample(16);
                Check("M-key-" + keys[i] + " 对应摇杆世界方向",
                    Near(sample.MoveX, expectedX[i], 1e-6f) && Near(sample.MoveZ, expectedZ[i], 1e-6f),
                    "X=" + sample.MoveX + " Z=" + sample.MoveZ);
            }

            Input.ResetStub();
            Input.Horizontal = 0.6f;
            Input.Vertical = 0.8f;
            PMMoverInput axis = new PMUnityMoverInput().Sample(16);
            Check("M-axis 右上虚拟轴等同屏幕摇杆右上",
                Near(axis.MoveX, -0.8f, 1e-6f) && Near(axis.MoveZ, 0.6f, 1e-6f),
                "X=" + axis.MoveX + " Z=" + axis.MoveZ);

            Input.ResetStub();
            Press(KeyCode.W);
            Press(KeyCode.D);
            PMMoverInput diagonal = new PMUnityMoverInput().Sample(16);
            Check("M-diag 右上键盘方向+朝向与摇杆右上同象限",
                Near(diagonal.MoveX, -0.70710678f, 1e-6f)
                && Near(diagonal.MoveZ, 0.70710678f, 1e-6f)
                && Near(diagonal.YawDegrees, -45f, 1e-3f),
                "X=" + diagonal.MoveX + " Z=" + diagonal.MoveZ + " yaw=" + diagonal.YawDegrees);
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
            Check("G2 W+D → 屏幕右上：世界(-√½,+√½) / yaw=-45",
                Near(wasd.MoveX, -0.70710678f, 1e-6f) && Near(wasd.MoveZ, 0.70710678f, 1e-6f)
                && Near(wasd.YawDegrees, -45f, 1e-3f),
                "MoveX=" + wasd.MoveX + " MoveZ=" + wasd.MoveZ + " yaw=" + wasd.YawDegrees);

            Input.ResetStub();
            Press(KeyCode.A);
            Press(KeyCode.S);
            PMMoverInput backward = input.Sample(16);
            Check("G3 A+S → 屏幕左下：世界(+√½,-√½) / yaw=135",
                Near(backward.MoveX, 0.70710678f, 1e-6f) && Near(backward.MoveZ, -0.70710678f, 1e-6f)
                && Near(backward.YawDegrees, 135f, 1e-3f),
                "MoveX=" + backward.MoveX + " MoveZ=" + backward.MoveZ + " yaw=" + backward.YawDegrees);

            // 虚拟轴：无按键时由轴提供；与按键同时存在时不叠加（取绝对值较大者）。
            Input.ResetStub();
            Input.Horizontal = 0.5f;
            Input.Vertical = 0f;
            PMMoverInput axisOnly = input.Sample(16);
            Check("G4a 仅水平虚拟轴 → 世界+Z=0.5，X=0",
                Near(axisOnly.MoveZ, 0.5f, 1e-6f) && Near(axisOnly.MoveX, 0f, 1e-6f),
                "MoveX=" + axisOnly.MoveX + " MoveZ=" + axisOnly.MoveZ);

            Press(KeyCode.D);
            PMMoverInput both = input.Sample(16);
            Check("G4b 按键与轴同时存在 → 屏幕右取较大者，不叠加",
                Near(both.MoveZ, 1f, 1e-6f) && Near(both.MoveX, 0f, 1e-6f),
                "MoveX=" + both.MoveX + " MoveZ=" + both.MoveZ + "（期望Z=1，不是1.5）");

            Input.ResetStub();
            PMUnityMoverInput deadZoned = new PMUnityMoverInput();
            deadZoned.AxisDeadZone = 0.3f;
            Input.Horizontal = 0.2f;
            Check("G5a 轴在死区内 → 0", deadZoned.Sample(16).MoveZ == 0f);
            Input.Horizontal = 0.6f;
            Check("G5b 轴超出死区 → 世界+Z原值",
                Near(deadZoned.Sample(16).MoveZ, 0.6f, 1e-6f), "MoveZ=" + deadZoned.Sample(16).MoveZ);

            // 虚拟轴不可用：抛一次被捕获 → 关掉该来源并计数；按键来源仍工作；不再重复抛。
            Input.ResetStub();
            Input.AxesConfigured = false;
            Press(KeyCode.D);
            PMUnityMoverInput noAxes = new PMUnityMoverInput();
            PMMoverInput keyOnly = noAxes.Sample(16);
            int callsAfterFirst = Input.GetAxisRawCalls;
            PMMoverInput keyOnly2 = noAxes.Sample(16);
            Check("G6 轴不可用时降级为按键（且不再重复尝试）",
                Near(keyOnly.MoveZ, 1f, 1e-6f) && Near(keyOnly2.MoveZ, 1f, 1e-6f)
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
            Press(KeyCode.W);
            float firstYaw = yawHold.Sample(16).YawDegrees;
            Release(KeyCode.W);
            float heldYaw = yawHold.Sample(16).YawDegrees;
            Check("G11 零输入时朝向保持上一次（W 对应世界−X、yaw=-90）",
                Near(firstYaw, -90f, 1e-3f) && Near(heldYaw, -90f, 1e-3f),
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

        // ================================================================== T-PLAY4 / I
        //
        //  本节对应用户冻结选型（Docs/plans/net-architecture-migration.md「T-PLAY4 UI与输入接口冻结」）：
        //    · UI 输入是相对**用户选定相机**的 [-1,1] 屏幕坐标（该相机世界 yaw = −90，
        //      与 T-PLAY1 的 PMBattleCameraGeometry.DefaultYawDegrees 同源）；
        //    · screen up → world −X、screen right → world +Z；
        //    · 非 finite / 超限一律拒绝（不吞非法）；接受范围内限幅单位向量；
        //    · 摇杆释放清零；UI 摇杆在时**优先于**键盘平面输入（来源选择）。
        //
        //  为什么能在这里真跑：屏幕→世界变换与来源选择都在 PMUnityMoverInput 里，
        //  且刻意只用 System.Math + UnityEngine.Input/KeyCode（本工程恰好只有这两个替身）。
        //  宿主（PMClientSessionHost.TrySetUiMove / TryQueueUiAttack / TryGetUiCombatSnapshot）
        //  依赖真实 Unity 会话与物理场景，**不**在本工程假装运行，
        //  它由 PMClientCheck / PMUnityGlueCheck / PMR4UnityCheck 三个编译面 + 静态契约覆盖。
        // ==================================================================

        private static void TestUiScreenVector()
        {
            Section("I. T-PLAY4 屏幕向量 → 世界向量（±轴 / 组合 / NaN / 超限 / 限幅 / 归一化）");

            float wx;
            float wz;

            // (1) 四条轴：screen right → +Z，screen left → −Z，screen up → −X，screen down → +X。
            bool right = PMUnityMoverInput.TryScreenVectorToWorld(1f, 0f, out wx, out wz);
            Check("I1 screen right(+X) → world +Z",
                right && Near(wx, 0f, 1e-6f) && Near(wz, 1f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            bool left = PMUnityMoverInput.TryScreenVectorToWorld(-1f, 0f, out wx, out wz);
            Check("I2 screen left(−X) → world −Z",
                left && Near(wx, 0f, 1e-6f) && Near(wz, -1f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            bool up = PMUnityMoverInput.TryScreenVectorToWorld(0f, 1f, out wx, out wz);
            Check("I3 screen up(+Y) → world −X",
                up && Near(wx, -1f, 1e-6f) && Near(wz, 0f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            bool down = PMUnityMoverInput.TryScreenVectorToWorld(0f, -1f, out wx, out wz);
            Check("I4 screen down(−Y) → world +X",
                down && Near(wx, 1f, 1e-6f) && Near(wz, 0f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            // (2) 组合：0.6/0.8 已经是单位长度 ⇒ 只换轴、不缩放。
            bool diag = PMUnityMoverInput.TryScreenVectorToWorld(0.6f, 0.8f, out wx, out wz);
            Check("I5 (0.6,0.8)（单位长度）→ (−0.8,0.6)，不缩放",
                diag && Near(wx, -0.8f, 1e-6f) && Near(wz, 0.6f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            // (3) 斜向推到底：模长 √2 > 1 ⇒ 限幅到单位向量（方向不变，不会更快）。
            bool full = PMUnityMoverInput.TryScreenVectorToWorld(1f, 1f, out wx, out wz);
            float fullLen = (float)Math.Sqrt((double)wx * (double)wx + (double)wz * (double)wz);
            Check("I6 (1,1) → 限幅到单位向量（模长 1，方向 (−1,1)/√2）",
                full && Near(fullLen, 1f, 1e-6f)
                && Near(wx, (float)(-1.0 / Math.Sqrt(2.0)), 1e-6f)
                && Near(wz, (float)(1.0 / Math.Sqrt(2.0)), 1e-6f),
                "world=(" + wx + ", " + wz + ") len=" + fullLen);

            // (4) 模长 < 1 的斜向输入**不**被放大（保留推杆量，不强行归一化）。
            bool soft = PMUnityMoverInput.TryScreenVectorToWorld(0.6f, 0.6f, out wx, out wz);
            Check("I7 (0.6,0.6)（模长 0.848）→ 原样换轴（不放大成单位向量）",
                soft && Near(wx, -0.6f, 1e-6f) && Near(wz, 0.6f, 1e-6f),
                "world=(" + wx + ", " + wz + ")");

            // (5) 非有限值：显式拒绝（不吞非法，也不静默钳制成 0）。
            float tmpX;
            float tmpZ;
            Check("I8 NaN/±Infinity 一律拒绝",
                !PMUnityMoverInput.TryScreenVectorToWorld(float.NaN, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(0f, float.NaN, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(float.PositiveInfinity, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(0f, float.NegativeInfinity, out tmpX, out tmpZ),
                "四类非有限值都被拒");

            // (6) 超限数：超出 [-1,1] 屏幕量程的分量一律拒绝（不是“帮我钳一下”）。
            Check("I9 超限量程（|v| > 1）一律拒绝",
                !PMUnityMoverInput.TryScreenVectorToWorld(1.5f, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(-1.5f, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(0f, -2f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenVectorToWorld(2f, 2f, out tmpX, out tmpZ),
                "四类超限值都被拒");

            // (7) 释放 = 零向量：合法输入（返回 true，但 world = 0 由调用方走“清移动”）。
            bool zero = PMUnityMoverInput.TryScreenVectorToWorld(0f, 0f, out wx, out wz);
            Check("I10 释放（0,0）是合法输入 ⇒ world=(0,0)",
                zero && wx == 0f && wz == 0f, "world=(" + wx + ", " + wz + ")");

            // (8) 轴约定与 T-PLAY1 冻结的相机 yaw 同源（不是硬编码一个巧合）：
            //     相机水平前向（yaw −90）必须等于 screen up 的世界向量；
            //     相机右向（yaw + 90）必须等于 screen right 的世界向量。
            double camYaw = (double)PMBattleCameraGeometry.DefaultYawDegrees;
            float fwdX = (float)Math.Sin(camYaw * Math.PI / 180.0);
            float fwdZ = (float)Math.Cos(camYaw * Math.PI / 180.0);
            float rgtX = (float)Math.Sin((camYaw + 90.0) * Math.PI / 180.0);
            float rgtZ = (float)Math.Cos((camYaw + 90.0) * Math.PI / 180.0);

            PMUnityMoverInput.TryScreenVectorToWorld(0f, 1f, out wx, out wz);
            float upX = wx;
            float upZ = wz;
            PMUnityMoverInput.TryScreenVectorToWorld(1f, 0f, out wx, out wz);

            Check("I11 轴约定与 T-PLAY1 相机 yaw 同源（screen up == 相机水平前向，screen right == 相机右向）",
                Near(upX, fwdX, 1e-5f) && Near(upZ, fwdZ, 1e-5f)
                && Near(wx, rgtX, 1e-5f) && Near(wz, rgtZ, 1e-5f),
                "camYaw=" + camYaw + " up=(" + upX + ", " + upZ + ") fwd=(" + fwdX + ", " + fwdZ
                + ") right=(" + wx + ", " + wz + ") camRight=(" + rgtX + ", " + rgtZ + ")");

            // (9) 瞄准方向：额外要求**单位向量**且方向非零（“没有方向”不得默认成某个方向）。
            bool aimUp = PMUnityMoverInput.TryScreenAimDirection(0f, 0.5f, out wx, out wz);
            Check("I12 瞄准 (0,0.5) → 单位向量 (−1,0)（长度不参与，只取方向）",
                aimUp && Near(wx, -1f, 1e-6f) && Near(wz, 0f, 1e-6f),
                "aim=(" + wx + ", " + wz + ")");

            bool aimRight = PMUnityMoverInput.TryScreenAimDirection(0.25f, 0f, out wx, out wz);
            Check("I13 瞄准 (0.25,0) → 单位向量 (0,1)",
                aimRight && Near(wx, 0f, 1e-6f) && Near(wz, 1f, 1e-6f),
                "aim=(" + wx + ", " + wz + ")");

            Check("I14 瞄准方向：零向量/NaN/超限全部拒绝（不默认方向）",
                !PMUnityMoverInput.TryScreenAimDirection(0f, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenAimDirection(float.NaN, 0f, out tmpX, out tmpZ)
                && !PMUnityMoverInput.TryScreenAimDirection(1.5f, 0f, out tmpX, out tmpZ),
                "三类都拒");

            // (10) 属性扫描：|world| == min(|screen|, 1)（换轴是旋转，不改变长度；只有超过 1 才缩），
            //      且 (−screenY, screenX) 与 (screenX, screenY) 正交（即确实是相差 90° 的换轴）。
            bool lengthExact = true;
            bool orthogonal = true;
            bool accepted = true;
            string firstBad = string.Empty;

            for (int ix = -4; ix <= 4; ix++)
            {
                for (int iy = -4; iy <= 4; iy++)
                {
                    float sx = ix * 0.25f;
                    float sy = iy * 0.25f;

                    float screenLen = (float)Math.Sqrt((double)sx * (double)sx + (double)sy * (double)sy);
                    float expected = screenLen > 1f ? 1f : screenLen;

                    float gx;
                    float gz;
                    if (!PMUnityMoverInput.TryScreenVectorToWorld(sx, sy, out gx, out gz))
                    {
                        accepted = false;
                        if (firstBad.Length == 0) { firstBad = "(" + sx + "," + sy + ") 被拒"; }
                        continue;
                    }

                    float got = (float)Math.Sqrt((double)gx * (double)gx + (double)gz * (double)gz);
                    if (!Near(got, expected, 1e-5f))
                    {
                        lengthExact = false;
                        if (firstBad.Length == 0)
                        {
                            firstBad = "(" + sx + "," + sy + ") |world|=" + got + " 期望 " + expected;
                        }
                    }

                    float dot = gx * sx + gz * sy;
                    if (!Near(dot, 0f, 1e-5f))
                    {
                        orthogonal = false;
                        if (firstBad.Length == 0) { firstBad = "(" + sx + "," + sy + ") dot=" + dot; }
                    }
                }
            }

            Check("I15 【属性扫描】81 个网格点全部接受、|world| == min(|screen|,1)、换轴正交",
                accepted && lengthExact && orthogonal,
                accepted && lengthExact && orthogonal
                    ? "81 点全部符合"
                    : ("不符：" + firstBad));

            // (11) 纯函数不见硬件（与本工程 H 节同一纪律）。
            Input.ResetStub();
            Input.AxesConfigured = false;
            int callsBefore = Input.GetAxisRawCalls;
            PMUnityMoverInput.TryScreenVectorToWorld(0.3f, 0.4f, out tmpX, out tmpZ);
            PMUnityMoverInput.TryScreenAimDirection(0.3f, 0.4f, out tmpX, out tmpZ);
            Check("I16 两个纯变换都不读硬件（Input 访问次数不变）",
                Input.GetAxisRawCalls == callsBefore,
                "getAxisRawCalls=" + Input.GetAxisRawCalls);
        }

        // ================================================================== T-PLAY4 / J

        private static void TestUiMoveSourceSelection()
        {
            Section("J. T-PLAY4 UI 摇杆来源选择（UI 优先于键盘）+ 释放清零 + 钳制");

            // J1 基线：没有 UI 摇杆时键盘必须照旧工作（来源选择不能把键盘弄坏）。
            Input.ResetStub();
            PMUnityMoverInput kbd = new PMUnityMoverInput();
            Press(KeyCode.D);
            PMMoverInput keyboardOnly = kbd.Sample(16);
            Check("J1 无 UI 摇杆时键盘仍是平面移动来源",
                !kbd.HasUiMove && Near(keyboardOnly.MoveX, 0f, 1e-6f)
                && Near(keyboardOnly.MoveZ, 1f, 1e-6f),
                "MoveX=" + keyboardOnly.MoveX + " MoveZ=" + keyboardOnly.MoveZ);

            // J2 【来源选择】UI 摇杆在时键盘平面输入被忽略（不叠加、不各来一份）。
            //    这里走**完整两层**：屏幕向量 → 世界向量 → 塞进输入，再采样。
            Input.ResetStub();
            PMUnityMoverInput ui = new PMUnityMoverInput();
            float uiWorldX;
            float uiWorldZ;
            bool converted = PMUnityMoverInput.TryScreenVectorToWorld(0f, 0.5f, out uiWorldX, out uiWorldZ);
            ui.SetUiMoveWorld(uiWorldX, uiWorldZ);
            Press(KeyCode.D);
            PMMoverInput uiWins = ui.Sample(16);
            Check("J2 UI 摇杆存在时忽略键盘平面输入（UI 优先，不叠加）",
                converted && ui.HasUiMove
                && Near(uiWins.MoveX, -0.5f, 1e-6f) && Near(uiWins.MoveZ, 0f, 1e-6f),
                "MoveX=" + uiWins.MoveX + " MoveZ=" + uiWins.MoveZ
                + "（键盘 D 对应世界+Z；摇杆在时不能叠加到 MoveZ）");

            // J3 虚拟轴同样被忽略（否则会出现“另一个摇杆偷偷推动”）。
            Input.ResetStub();
            Input.Horizontal = 1f;
            PMMoverInput uiWinsOverAxis = ui.Sample(16);
            Check("J3 UI 摇杆存在时也忽略 Horizontal/Vertical 虚拟轴",
                Near(uiWinsOverAxis.MoveX, -0.5f, 1e-6f) && Near(uiWinsOverAxis.MoveZ, 0f, 1e-6f),
                "MoveX=" + uiWinsOverAxis.MoveX + " MoveZ=" + uiWinsOverAxis.MoveZ);

            // J4 释放摇杆（零输入）⇒ 立刻回到键盘来源。
            Input.ResetStub();
            ui.SetUiMoveWorld(0f, 0f);
            Press(KeyCode.D);
            PMMoverInput afterRelease = ui.Sample(16);
            Check("J4 摇杆释放（零输入）后键盘立刻恢复为来源",
                !ui.HasUiMove && Near(afterRelease.MoveX, 0f, 1e-6f)
                && Near(afterRelease.MoveZ, 1f, 1e-6f),
                "HasUiMove=" + ui.HasUiMove + " MoveX=" + afterRelease.MoveX);

            // J5 世界向量钳制：模长 > 1 时缩到单位（方向不变），不放大也不抛出。
            Input.ResetStub();
            PMUnityMoverInput clamp = new PMUnityMoverInput();
            clamp.SetUiMoveWorld(3f, 4f);
            PMMoverInput clamped = clamp.Sample(16);
            float clampedLen = (float)Math.Sqrt((double)clamped.MoveX * (double)clamped.MoveX
                                                + (double)clamped.MoveZ * (double)clamped.MoveZ);
            Check("J5 SetUiMoveWorld(3,4) → 限幅到单位向量（0.6,0.8）",
                Near(clamped.MoveX, 0.6f, 1e-6f) && Near(clamped.MoveZ, 0.8f, 1e-6f)
                && Near(clampedLen, 1f, 1e-6f),
                "MoveX=" + clamped.MoveX + " MoveZ=" + clamped.MoveZ + " len=" + clampedLen);

            // J6 朝向仍由**移动方向**推导（攻击瞄准的独立性由宿主/网络侧覆盖，不在本类）。
            Input.ResetStub();
            PMUnityMoverInput yawInput = new PMUnityMoverInput();
            yawInput.SetUiMoveWorld(0f, 1f);   // 世界 +Z ⇒ yaw 0
            Press(KeyCode.W);                  // 键盘世界 −X ⇒ 若无来源选择会得到 yaw −90
            PMMoverInput uiYaw = yawInput.Sample(16);
            Check("J6 朝向跟随 UI 移动方向（世界 +Z ⇒ yaw 0），不被键盘 −X 覆盖",
                Near(uiYaw.YawDegrees, 0f, 1e-3f), "yaw=" + uiYaw.YawDegrees);

            // J7 Reset（换局/停局路径）清掉 UI 摇杆，不把上一局的推杆带过来。
            Input.ResetStub();
            PMUnityMoverInput resetInput = new PMUnityMoverInput();
            resetInput.SetUiMoveWorld(0.5f, 0.5f);
            bool hadBefore = resetInput.HasUiMove;
            resetInput.Reset();
            PMMoverInput afterReset = resetInput.Sample(16);
            Check("J7 Reset 清空 UI 摇杆（换局不带出上一局输入）",
                hadBefore && !resetInput.HasUiMove && afterReset.MoveX == 0f && afterReset.MoveZ == 0f,
                "had=" + hadBefore + " has=" + resetInput.HasUiMove
                + " MoveX=" + afterReset.MoveX + " MoveZ=" + afterReset.MoveZ);

            // J8 计数可观测：接受与清空都可对账（不是静默内部状态）。
            Input.ResetStub();
            PMUnityMoverInput counted = new PMUnityMoverInput();
            counted.SetUiMoveWorld(0f, 1f);
            counted.SetUiMoveWorld(0f, 0.5f);
            int accepts = counted.UiMoveAcceptCount;
            counted.ClearUiMove();
            counted.ClearUiMove();   // 幂等：已清空后再清不算一次“清空事件”
            Check("J8 UI 摇杆接受/清空计数可观测且清空幂等",
                accepts == 2 && counted.UiMoveClearCount == 1,
                "accepts=" + accepts + " clears=" + counted.UiMoveClearCount);

            Input.ResetStub();
        }

        // ================================================================== T-PLAY4 / K
        //
        //  宿主接线静态契约（**不是**行为测试，边界写清楚）：
        //  PMClientSessionHost 的三个 UI 入口与 Pump 里的消费点都需要真实 Unity 会话/物理场景，
        //  本工程（net8 纯逻辑）无法运行它们，因此**不假装跑宿主**。这里用“源码形状 + 负例”
        //  把「路由」钉住：UI 不直接发 RPC、一次攻击只用一个瞄准向量、边沿只在一个地方消费、
        //  冻结/停局/死亡一定清 UI 输入、轴变换只有一份（在 PMUnityMoverInput 里）。
        //  真实编译面由 PMClientCheck / PMUnityGlueCheck / PMR4UnityCheck 三个门覆盖。
        // ==================================================================

        private static void TestUiHostSourceContracts()
        {
            Section("K. T-PLAY4 宿主 UI 入口静态契约（路由 / 单向量 / 单消费点 / 清理）");

            string hostPath = FindRepoFile("Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs");
            string host = hostPath == null ? null : System.IO.File.ReadAllText(hostPath);

            Check("K1 宿主源码可读（UI 入口核对对象）", host != null, hostPath ?? "<未找到 PMClientSessionHost.cs>");

            if (host == null) { return; }

            // 负例检查必须看**代码**而不是注释：先去掉 // 与 /* */ 再压缩空白。
            // 否则“文档注释里写了 PMR3Player / 写了某个不存在就应拒的调用”会让断言假绿（也可能假红），
            // 那就不是契约门而是文本巧合门。
            string compact = Compact(StripComments(host));

            // ---- 三个冻结入口的签名 ----
            Check("K2 冻结入口 TrySetUiMove(float,float) bool 存在",
                compact.Contains("publicstaticboolTrySetUiMove(floatscreenX,floatscreenY)"),
                "签名必须是 (float screenX, float screenY) → bool");

            Check("K3 冻结入口 TryQueueUiAttack(bool,float,float) bool 存在",
                compact.Contains("publicstaticboolTryQueueUiAttack(boolisSuper,floatscreenX,floatscreenY)"),
                "签名必须是 (bool isSuper, float screenX, float screenY) → bool");

            Check("K4 冻结入口 TryGetUiCombatSnapshot(out PMUiCombatSnapshot) bool 存在",
                compact.Contains("publicstaticboolTryGetUiCombatSnapshot(outPMUiCombatSnapshotsnapshot)"),
                "签名必须是 (out PMUiCombatSnapshot) → bool");

            // ---- 只读快照是公开值类型，且不泄露 PMR3Player ----
            // 只取**字段声明区**（struct 开头到构造函数），避免把后面类的成员扫进来。
            int structStart = compact.IndexOf("publicstructPMUiCombatSnapshot", StringComparison.Ordinal);
            int structCtor = structStart < 0
                ? -1
                : compact.IndexOf("publicPMUiCombatSnapshot(", structStart, StringComparison.Ordinal);
            string snapshotStruct = structStart < 0 || structCtor < 0
                ? null
                : compact.Substring(structStart, structCtor - structStart);

            Check("K5 PMUiCombatSnapshot 是公开 struct，含冻结的 Hp/MaxHp/Mana/SuperEnergy/Dead/MatchEnded/NormalReady/SuperReady/LastReject",
                snapshotStruct != null
                && snapshotStruct.Contains("readonlyintHp")
                && snapshotStruct.Contains("readonlyintMaxHp")
                && snapshotStruct.Contains("readonlyintMana")
                && snapshotStruct.Contains("readonlyintSuperEnergy")
                && snapshotStruct.Contains("readonlyboolDead")
                && snapshotStruct.Contains("readonlyboolMatchEnded")
                && snapshotStruct.Contains("readonlyboolNormalReady")
                && snapshotStruct.Contains("readonlyboolSuperReady")
                && snapshotStruct.Contains("readonlystringLastReject"),
                snapshotStruct == null ? "未定位到 PMUiCombatSnapshot 声明" : "字段齐全");

            Check("K6 【反例】快照不泄出会话/玩家引用（结构里不得出现 PMR3Player / PMNetWorld / Session）",
                snapshotStruct != null
                && !snapshotStruct.Contains("PMR3Player")
                && !snapshotStruct.Contains("PMNetWorld")
                && !snapshotStruct.Contains("Session"),
                snapshotStruct == null ? "未定位到 PMUiCombatSnapshot 声明" : "只含值/字符串");

            // ---- UI 永不直接发 RPC / 不写复制字段 ----
            string enqueue = ExtractMember(compact, "publicstaticboolTryQueueUiAttack");
            Check("K7 【反例】TryQueueUiAttack 不直接发 RPC、不写复制字段（只入队一次输入意图）",
                enqueue != null
                && !enqueue.Contains("ServerCombatAttackV1")
                && !enqueue.Contains("PMNet_")
                && !enqueue.Contains("MarkPropertyDirty")
                && !enqueue.Contains("TryAttack("),
                enqueue == null ? "未定位到 TryQueueUiAttack" : "无发送/无写入调用");

            string uiMove = ExtractMember(compact, "publicstaticboolTrySetUiMove");
            Check("K8 【反例】TrySetUiMove 不直接写 Mover/不写复制字段（只把摇杆值交给输入层）",
                uiMove != null
                && !uiMove.Contains("SetActorLocation")
                && !uiMove.Contains("MarkPropertyDirty")
                && !uiMove.Contains("CombatMana=")
                && !uiMove.Contains("CombatHp="),
                uiMove == null ? "未定位到 TrySetUiMove" : "无权威写入");

            // ---- 轴变换只有一份（在 PMUnityMoverInput），宿主不得自行换轴 ----
            Check("K9 宿主只委托纯变换入口（调 PMUnityMoverInput.TryScreenVectorToWorld / TryScreenAimDirection）",
                compact.Contains("PMUnityMoverInput.TryScreenVectorToWorld(")
                && compact.Contains("PMUnityMoverInput.TryScreenAimDirection("),
                "轴变换在 PMUnityMoverInput（可在 I 节断言）");

            Check("K10 【反例】宿主内不得再写一份换轴术（不得出现 -screenY / screenX 自行拼世界向量的痕迹）",
                !compact.Contains("-screenY") && !compact.Contains("worldX=screenY"),
                "单一份轴约定");

            // ---- 一次攻击：planner / 枪口 / 上行必须同一个 forward ----
            string attack = ExtractMember(compact, "privatestaticvoidTryCombatAttack");
            Check("K11 一次攻击的 planner 方向、枪口、上行都用同一个 forward 向量",
                attack != null
                && attack.Contains("PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId,isSuper,forward.X,forward.Z,")
                && attack.Contains("origin=predicted.Position+forward*")
                && attack.Contains("combat.TryAttack(owner,isSuper,forward.X,forward.Z,origin,"),
                attack == null ? "未定位到 TryCombatAttack" : "三处共用 forward");

            Check("K12 UI 瞄准独立于 Mover yaw（if(uiEdge) 分支取 UI 向量），键盘 F/G 仍用 predicted yaw 兼底",
                attack != null
                && attack.Contains("if(uiEdge){forward=newPMVector3(uiAimX,0f,uiAimZ);}")
                && attack.Contains("predicted.YawDegrees"),
                attack == null ? "未定位到 TryCombatAttack" : "两个源分开取方向");

            Check("K13 三个输入边沿（F / G / UI）在**同一个消费点**一次性清空（主线程统一消费）",
                attack != null
                && attack.Contains("session.NormalAttackEdgeBuffered=false;session.SuperAttackEdgeBuffered=false;session.UiAttackEdgeBuffered=false;"),
                attack == null ? "未定位到 TryCombatAttack" : "三点同处清位");

            Check("K14 【反例】UI 攻击不绕过本地门：边沿消费后仍先过 IsCombatFireReady 与 planner 间隔门",
                attack != null
                && attack.IndexOf("IsCombatFireReady(", StringComparison.Ordinal) > -1
                && attack.Contains("plan.FireIntervalMs")
                && attack.IndexOf("IsCombatFireReady(", StringComparison.Ordinal)
                   < attack.IndexOf("combat.TryAttack(", StringComparison.Ordinal),
                attack == null ? "未定位到 TryCombatAttack" : "就绪门 + 间隔门仍在前面");

            // ---- 停局 / 冻结 / 死亡 / 换局一定清 UI 输入 ----
            int clearCalls = CountOccurrences(compact, "ClearUiInput(session)");
            Check("K15 Stop/冻结/死亡/换局都不带出上一局 UI 输入（ClearUiInput 至少在冻结/释放/复制死亡三处调用）",
                clearCalls >= 4
                && ExtractMember(compact, "privatestaticvoidFreezeMovements") != null
                && ExtractMember(compact, "privatestaticvoidFreezeMovements").Contains("ClearUiInput(session)")
                && ExtractMember(compact, "privatestaticvoidReleaseSession").Contains("ClearUiInput(session)")
                && ExtractMember(compact, "privatestaticvoidUpdateCombatReplicationState").Contains("ClearUiInput(session)"),
                "ClearUiInput 调用点数=" + clearCalls.ToString());

            Check("K16 UI 输入有主线程门（UI 与每帧 Pump 同线程）",
                compact.Contains("IsMainThreadForUi(session)")
                && compact.Contains("_mainThreadId=System.Threading.Thread.CurrentThread.ManagedThreadId;")
                && compact.Contains("System.Threading.Thread.CurrentThread.ManagedThreadId"),
                "非主线程入队被拒");

            // ---- 来源选择在输入层：UI 摇杆优先，释放后键盘恢复 ----
            string inputPath = FindRepoFile("Client/Assets/Scripts/PMUnity/PMUnityMoverInput.cs");
            string inputSource = inputPath == null ? null : System.IO.File.ReadAllText(inputPath);
            string compactInput = inputSource == null ? null : Compact(inputSource);

            Check("K17 来源选择在输入层：ReadPlanar 里 UI 摇杆优先、否则回键盘（摇杆由宿主写入）",
                compactInput != null
                && compactInput.Contains("if(_hasUiMove){x=_uiMoveWorldX;z=_uiMoveWorldZ;return;}")
                && compact.Contains("input.SetUiMoveWorld(worldX,worldZ);")
                && compact.Contains("input.ClearUiMove();"),
                compactInput == null ? "未找到 PMUnityMoverInput.cs" : "UI→输入层单一通路");
        }

        // ==================================================================
        //  N. T-LOOP6 续局重入 / 前端生命周期静态契约（防陈旧）
        //
        //  边界（与 K 节同一口径）：本工程不运行宿主与旧 UI（它们需要真实 Unity 会话/场景），
        //  因此这里只把**源码形状**钉住，并用“负例自证”证明这些断言真的能抓出修前/变异状态：
        //    · 同局重入只在旧会话已故障/端点失败时允许（不顶替活跃会话、不恢复已结束对局）；
        //    · 入局/续局通知必须先在主线程把匹配面板安全打开，再交给它的接收队列；
        //    · 面板/管理器不得回退旧链。
        //  真实编译面由 PMClientCheck / PMR4UnityCheck / PMNetUnityPlayerCheck 覆盖。
        // ==================================================================

        private static void TestLoop6ResumeSourceContracts()
        {
            Section("N. T-LOOP6 续局重入与前端生命周期静态契约（含负例自证）");

            string hostPath = FindRepoFile("Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs");
            string host = hostPath == null ? null : System.IO.File.ReadAllText(hostPath);
            string managerPath = FindRepoFile("Client/Assets/Scripts/Server/Manger/RequestManger.cs");
            string manager = managerPath == null ? null : System.IO.File.ReadAllText(managerPath);
            string panelPath = FindRepoFile("Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs");
            string panel = panelPath == null ? null : System.IO.File.ReadAllText(panelPath);

            Check("N1 被核对的三个源码可读（宿主 / RequestManger / UIMatchingPanel）",
                host != null && manager != null && panel != null,
                (hostPath ?? "<host 缺失>") + " | " + (managerPath ?? "<manger 缺失>") + " | "
                + (panelPath ?? "<panel 缺失>"));

            if (host == null || manager == null || panel == null) { return; }

            // 负例检查必须看**代码**而不是注释；隔离掉的字符串也会被剔除。
            string hostCode = Compact(StripComments(host));
            string managerCode = Compact(StripComments(manager));
            string panelCode = Compact(StripComments(panel));

            string reason;

            // ---- ① 宿主：同局重入的准入 ----
            Check("N2 真实宿主满足同局重入门纯判据（策略/拒绝分支/释放后重建/终局不可恢复）",
                HostResumeGateOk(hostCode, out reason), reason ?? "OK");

            Check("N3 同局重入门是独立决策源（枚举 + 纯策略类都在宿源码里）",
                hostCode.Contains("enumPMClientSameMatchEntry")
                && hostCode.Contains("classPMClientSameMatchEntryPolicy")
                && hostCode.Contains("publicstaticPMClientSameMatchEntryDecide(boolsessionFaulted,boolendpointUsable,boolmatchEndedKnown)"),
                "策略形态单一来源");

            Check("N4 【反例】宿主的同局重入门不得再写一份“健康判定”（不得出现 IsEndpointUsable 之外的连接就绪内联判定）",
                !hostCode.Contains("session.Endpoint.ClientConnection!=null&&session.Endpoint.ClientConnection.IsReady"),
                "连接就绪判定只有 IsEndpointUsable 一份");

            Check("N5 宿主已结束判定只认可信结果快照（不用计数/复制位冒充结论）",
                ExtractMethod(hostCode, "private static bool IsMatchAlreadyEnded") is string ended
                && ended.Contains("_lastCombatResultValid") && ended.Contains("_lastCombatMatchId")
                && !ended.Contains("CombatMatchEnded") && !ended.Contains("Received"),
                "IsMatchAlreadyEnded 只看快照有效性 + matchId");

            // ---- ①-b 负例自证：抽掉/弄坏关键字句必须被同一条判据拒绝 ----
            Check("N6 【负例自证】抽掉同局重入决策（修前行为）必须被判据判为不满足",
                !HostResumeGateOk(hostCode.Replace("PMClientSameMatchEntryPolicy.Decide(", "DisabledProbe.Noop("), out reason),
                "仍判为通过 ⇒ 这条门是假绿（" + (reason ?? "<无>") + "）");

            Check("N7 【负例自证】把“已结束对局”拒绝分支换掉必须被判据判为不满足",
                !HostResumeGateOk(hostCode.Replace("PMClientSameMatchEntry.RejectEndedMatch", "PMClientSameMatchEntry.ReplaceFaulted"), out reason),
                "仍判为通过 ⇒ 终局不可恢复这条没有被真正核住（" + (reason ?? "<无>") + "）");

            Check("N8 【负例自证】把“已结束”判据换成常量必须被判据判为不满足",
                !HostResumeGateOk(hostCode.Replace("IsMatchAlreadyEnded(offer.MatchId)", "false"), out reason),
                "仍判为通过 ⇒ 终局恢复防线缺失（" + (reason ?? "<无>") + "）");

            Check("N9 【负例自证】不重建新输入（沿用旧对象）必须被判据判为不满足",
                !HostResumeGateOk(hostCode.Replace("newPMUnityMoverInput()", "session.ReusedInput"), out reason),
                "仍判为通过 ⇒ “全新世界/输入”没有被真正核住（" + (reason ?? "<无>") + "）");

            // ---- ② RequestManger：先开面板、再交包 ----
            Check("N10 真实 RequestManger 满足“先安全打开面板、再投递入局通知”纯判据",
                RequestManagerEntryOrderOk(managerCode, out reason), reason ?? "OK");

            Check("N11 【负例自证】把“先开面板”抹掉（直接把包交给 BaseRequest）必须被判据判为不满足",
                !RequestManagerEntryOrderOk(
                    managerCode.Replace("if(!MVC.UIMatchingPanel.TryEnsureOpenForEntryNotice(outerror))", "if(false)"),
                    out reason),
                "仍判为通过 ⇒ “先开面板”这条没有被真正核住（" + (reason ?? "<无>") + "）");

            Check("N12 【负例自证】不经有界暂存而在收包线程直接交包必须被判据判为不满足",
                !RequestManagerEntryOrderOk(
                    managerCode.Replace("PMEntryNoticeInbox.Stage(pack,pack.Actioncode);", "request.OnResponse(pack);"),
                    out reason),
                "仍判为通过 ⇒ 未注册+主线程安全这条没有被真正核住（" + (reason ?? "<无>") + "）");

            // ---- ③ UIMatchingPanel：严格解码 + IsResume + 不回旧链 ----
            Check("N13 真实匹配面板满足“严格解码 PMDS1/PMDSR1 + IsResume 入局 + 不回旧链”纯判据",
                MatchingPanelStrictEntryOk(panelCode, out reason), reason ?? "OK");

            Check("N14 【负例自证】把严格解码换成“只判前缀就入局”必须被判据判为不满足",
                !MatchingPanelStrictEntryOk(
                    panelCode.Replace("PMDsEntryCodec.TryDecode(", "ProbeBypass("), out reason),
                "仍判为通过 ⇒ 严格解码这条没有被真正核住（" + (reason ?? "<无>") + "）");

            Check("N15 【负例自证】面板出现旧链回退标识必须被判据判为不满足",
                !MatchingPanelStrictEntryOk(panelCode + "BattleData.InitBattleInfo();", out reason),
                "仍判为通过 ⇒ 旧链禁回归这条没有被真正核住（" + (reason ?? "<无>") + "）");
        }

        /// <summary>
        /// T-LOOP6 同局重入门的纯判据（对给定宿主源码文本判定，失败给出原因）。
        /// 抽成函数的目的：负例自证要对**变异后的文本**重跑同一判据，证明它不是恒绿的文本巧合门。
        /// </summary>
        private static bool HostResumeGateOk(string compactSource, out string reason)
        {
            reason = null;

            string enter = ExtractMember(compactSource, "publicstaticvoidEnter");
            if (enter == null) { reason = "未定位到 Enter"; return false; }

            if (!enter.Contains("PMClientSameMatchEntryPolicy.Decide("))
            {
                reason = "Enter 未调用同局重入决策策略";
                return false;
            }

            if (!enter.Contains("_active.Faulted") || !enter.Contains("IsEndpointUsable(_active)"))
            {
                reason = "决策输入未回到旧会话的真实状态（故障位/端点可用性）";
                return false;
            }

            if (!enter.Contains("PMClientSameMatchEntry.RejectActiveHealthy")
                || !enter.Contains("PMClientSameMatchEntry.RejectEndedMatch"))
            {
                reason = "缺少“拒绝顶替活跃会话/拒绝恢复已结束对局”两条分支";
                return false;
            }

            if (enter.Contains("已在同一局内"))
            {
                reason = "旧的无条件“忽略重复 Enter”早退仍在（会把续局重入吞掉）";
                return false;
            }

            int endedCheck = enter.IndexOf("IsMatchAlreadyEnded(offer.MatchId)", StringComparison.Ordinal);
            if (endedCheck < 0)
            {
                reason = "缺少“已结束对局不得恢复”判据";
                return false;
            }

            int clearsResult = enter.IndexOf("ClearLastCombatResult()", StringComparison.Ordinal);
            if (clearsResult < 0 || endedCheck > clearsResult)
            {
                reason = "已结束判定晚于清空结果快照（判据会永久失效）";
                return false;
            }

            if (!enter.Contains("Stop();"))
            {
                reason = "允许替换的分支没有先安全释放旧状态";
                return false;
            }

            if (!enter.Contains("newPMNetWorld(") || !enter.Contains("newPMUnityMoverInput()")
                || !enter.Contains("newSession()"))
            {
                reason = "重入后不是全新世界/输入/会话";
                return false;
            }

            return true;
        }

        /// <summary>T-LOOP6 RequestManger 的纯判据：PMDS 入局通知必须先开面板、再投递，且要搬到主线程。</summary>
        private static bool RequestManagerEntryOrderOk(string compactSource, out string reason)
        {
            reason = null;

            string handle = ExtractMember(compactSource, "publicstaticvoidHandleRequest");
            if (handle == null) { reason = "未定位到 HandleRequest"; return false; }

            int branch = handle.IndexOf("if(IsMatchingEntryNotice(pack))", StringComparison.Ordinal);
            int direct = handle.IndexOf("request.OnResponse(pack);", StringComparison.Ordinal);
            if (branch < 0 || direct < 0 || branch > direct)
            {
                reason = "入局通知分支不在直接投递之前";
                return false;
            }

            if (!handle.Contains("QueueMatchingEntryNotice(FindRequest(pack.Actioncode),pack);"))
            {
                reason = "入局通知仍依赖已注册request（先到的PMDSR1会被丢弃）";
                return false;
            }

            string notice = ExtractMember(compactSource, "privatestaticboolIsMatchingEntryNotice");
            if (notice == null || !notice.Contains("PMNet.Session.PMDsEntryCodec.HasPrefix(pack.Str)")
                || !notice.Contains("ReturnCode.Succeed"))
            {
                reason = "未按“已成功 + PMDS1:/PMDSR1: 前缀”识别入局通知";
                return false;
            }

            string queue = ExtractMember(compactSource, "privatestaticvoidQueueMatchingEntryNotice");
            if (queue == null || !queue.Contains("PMEntryNoticeInbox.Stage(pack,pack.Actioncode);"))
            {
                reason = "收包线程没有先用真实codec把先到的通知有界暂存";
                return false;
            }

            string mainPump = ExtractMember(compactSource, "publicstaticvoidPumpEntryNoticeInbox");
            if (mainPump == null || !mainPump.Contains("PMEntryNoticeInbox.TryPeekValid(")
                || !mainPump.Contains("OpenMatchingPanelThenDeliver(request,pack);"))
            {
                reason = "主线程泵没有等面板注册后消费暂存（早到的offer仍会丢）";
                return false;
            }

            string deliver = ExtractMember(compactSource, "privatestaticvoidOpenMatchingPanelThenDeliver");
            if (deliver == null)
            {
                reason = "未定位到 OpenMatchingPanelThenDeliver";
                return false;
            }

            int open = deliver.IndexOf("TryEnsureOpenForEntryNotice(outerror)", StringComparison.Ordinal);
            int consume = deliver.IndexOf("PMEntryNoticeInbox.ConsumeForDelivery();", StringComparison.Ordinal);
            int hand = deliver.IndexOf("request.OnResponse(pack);", StringComparison.Ordinal);
            if (open < 0 || consume <= open || hand <= consume)
            {
                reason = "没有先开面板、再一次消费暂存、最后交包";
                return false;
            }

            if (!deliver.Contains("return;"))
            {
                reason = "面板打不开时没有明确拒绝（会把包排进不会执行的队列）";
                return false;
            }

            if (compactSource.Contains("BattleData") || compactSource.Contains("ClearSence"))
            {
                reason = "出现旧链回退标识";
                return false;
            }

            return true;
        }

        /// <summary>T-LOOP6 UIMatchingPanel 的纯判据：严格解码两个前缀、用 IsResume 入局、不回旧链。</summary>
        private static bool MatchingPanelStrictEntryOk(string compactSource, out string reason)
        {
            reason = null;

            if (!compactSource.Contains("PMDsEntryCodec.HasPrefix("))
            {
                reason = "面板未按新链完整前缀分流";
                return false;
            }

            if (!compactSource.Contains("PMDsEntryCodec.TryDecode("))
            {
                reason = "面板没有严格解码（解码失败必须明确拒绝）";
                return false;
            }

            if (!compactSource.Contains("offer.IsResume"))
            {
                reason = "面板没有把 IsResume 带进入局路径（续局无法与普通开局区分）";
                return false;
            }

            if (!compactSource.Contains("TryEnsureOpenForEntryNotice(outstringerror)"))
            {
                reason = "缺少宿主可调用的“安全打开”入口";
                return false;
            }

            string[] legacy = new string[]
            {
                "BattleData", "BattleManger", "ClearSenceManger", "LoadScene", "InitBattleInfo", "AddBattleReview",
            };

            for (int i = 0; i < legacy.Length; i++)
            {
                if (compactSource.Contains(legacy[i]))
                {
                    reason = "面板出现旧链标识：" + legacy[i];
                    return false;
                }
            }

            return true;
        }

        // ==================================================================
        //  O. T-LIVE3 瞄准指示（纯数学 + 状态机镜像 + 源码契约）
        //
        //  为什么需要它：实机反馈「普攻/大招摇杆按住时看不到世界瞄准线」（旧 TouchLogic.OnJoystickMove
        //  会在按住拖动时把玩家角色子节点的 LineRenderer 画成距离/散射扇形，松开即关；新链没有）。
        //  本条被钉成三层，与 K/J 节同一口径：
        //    · O1 纯数学：距离（Spec.SpeedMps×LifetimeMs）与扇形折线（旧 LineRenderer 的形状）
        //      直接编**真实源码**（PMUnityBattleAimMath 在 #if 之外，三个编译门都会类型检查它），
        //      在 net8 上跑正负例；
        //    · O2 状态机**可执行镜像**：按住即推送 / 同值去重 / 松手隐藏 / 重复隐藏幂等 /
        //      NaN 不污染 / 清理（死亡·终局·Freeze·Stop·换局）幂等 / 隐藏不提交攻击；
        //    · O3 源码形状契约（含**负例自证**）：UI 的 setAim 出口、宿主同一份屏幕→世界变换、
        //      只为本端 Owner 更新、清理与释放全覆盖、指示器零 RPC/零权威/DS 拒绝/显式可见材质。
        //
        //  **镜像边界（必须写清）**：O2 是本文件独立实现的规则模型，不是跑同一个实现
        //  （uGUI 分支在 UNITY_2019_1_OR_NEWER/UNITY_EDITOR 里，本工程编不到）；
        //  实现与镜像的一致性由 O3 的标记断言守住，等价性不构成机器证明。
        // ==================================================================

        private static void TestLive3AimIndicatorMath()
        {
            Section("O1. T-LIVE3 瞄准指示纯数学（距离 / 扇形折线 / 非法输入）");

            // ---- 距离 = Spec.SpeedMps × LifetimeMs / 1000（与本次攻击计划同一来源） ----
            float distance;
            bool ok = PMUnityBattleAimMath.TryDistanceMeters(11f, 545, out distance);
            Check("O1.1 距离 = SpeedMps × LifetimeMs / 1000", ok && Near(distance, 5.995f, 1e-3f),
                  "distance=" + distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            Check("O1.2 负例自证：漏掉 /1000 的口径（11×545=5995）与 O1.1 的断言不同",
                  !Near(5995f, 5.995f, 1e-2f), "5995 与 5.995 的差异远大于容差");

            Check("O1.3 非有限/非正速度被拒",
                  !PMUnityBattleAimMath.TryDistanceMeters(0f, 500, out distance)
                  && !PMUnityBattleAimMath.TryDistanceMeters(-1f, 500, out distance)
                  && !PMUnityBattleAimMath.TryDistanceMeters(float.NaN, 500, out distance)
                  && !PMUnityBattleAimMath.TryDistanceMeters(float.PositiveInfinity, 500, out distance),
                  "坏速度不得生成几何");

            Check("O1.4 非正寿命被拒",
                  !PMUnityBattleAimMath.TryDistanceMeters(11f, 0, out distance)
                  && !PMUnityBattleAimMath.TryDistanceMeters(11f, -5, out distance),
                  "坏寿命不得生成几何");

            bool huge = PMUnityBattleAimMath.TryDistanceMeters(1e30f, int.MaxValue, out distance);
            Check("O1.5 超大距离被有界钳制（不画一条穿世界的线）",
                  huge && distance == PMUnityBattleAimMath.MaxAimDistanceMeters,
                  "distance=" + distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                  + " 上限=" + PMUnityBattleAimMath.MaxAimDistanceMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            // ---- 扇形折线：单发 = 2 点；多发 = 2N+2 点（旧 TouchLogic 的 LineRenderer 形状） ----
            float[] scratch = new float[3 * (2 * PMUnityBattleAimMath.MaxFanDirections + 2)];

            int singleCount;
            bool singleOk = PMUnityBattleAimMath.TryBuildFanPolyline(
                1f, 2f, 3f, new float[] { 1f }, new float[] { 0f }, 1, 10f, scratch, out singleCount);
            Check("O1.6 单发折线 = 2 点（origin → origin + dir×dist，与旧 launchAngle==0 分支同形）",
                  singleOk && singleCount == 6
                  && Near(scratch[0], 1f, 1e-5f) && Near(scratch[1], 2f, 1e-5f) && Near(scratch[2], 3f, 1e-5f)
                  && Near(scratch[3], 11f, 1e-5f) && Near(scratch[4], 2f, 1e-5f) && Near(scratch[5], 3f, 1e-5f),
                  "floatCount=" + singleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // 5 股扇形：方向按 PMBattleSim.SpreadDirection 的口径（±angle/2 首尾包含）铺开。
            const int fanCount = 5;
            float[] dirX = new float[fanCount];
            float[] dirZ = new float[fanCount];
            for (int i = 0; i < fanCount; i++)
            {
                double angleDeg = -15.0 + 30.0 / (fanCount - 1) * i;
                double angleRad = angleDeg * Math.PI / 180.0;
                dirX[i] = (float)Math.Cos(angleRad);
                dirZ[i] = (float)Math.Sin(angleRad);
            }

            int fanFloats;
            bool fanOk = PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, dirX, dirZ, fanCount, 10f,
                                                                 scratch, out fanFloats);
            Check("O1.7 扇形折线点数 = 2N+2（中心起、每股边+回中心、尾部回中心）",
                  fanOk && fanFloats == 3 * (2 * fanCount + 2),
                  "floatCount=" + fanFloats.ToString(System.Globalization.CultureInfo.InvariantCulture));

            bool fanGeometry = fanOk;
            for (int i = 0; i < fanCount && fanGeometry; i++)
            {
                int edge = 3 * (1 + 2 * i);
                int back = 3 * (2 + 2 * i);

                if (!Near(scratch[edge], dirX[i] * 10f, 1e-4f)) { fanGeometry = false; }
                if (!Near(scratch[edge + 1], 0f, 1e-5f)) { fanGeometry = false; }
                if (!Near(scratch[edge + 2], dirZ[i] * 10f, 1e-4f)) { fanGeometry = false; }
                if (!Near(scratch[back], 0f, 1e-5f) || !Near(scratch[back + 1], 0f, 1e-5f)
                    || !Near(scratch[back + 2], 0f, 1e-5f)) { fanGeometry = false; }
            }

            Check("O1.8 每股弹道角点都落在 dir_i×距离 上、且每股都回到中心（扇形而不是连成一圈）",
                  fanGeometry, "5 股各 2 个点 + 中心节点");

            Check("O1.9 扇形两侧对称（首尾方向互为镜像 ⇒ 与 ±LaunchAngle/2 的计划同口径）",
                  Near(dirX[0], dirX[fanCount - 1], 1e-6f) && Near(dirZ[0], -dirZ[fanCount - 1], 1e-6f),
                  "首=" + dirX[0] + "/" + dirZ[0] + " 尾=" + dirX[fanCount - 1] + "/" + dirZ[fanCount - 1]);

            float[] tooSmall = new float[6];
            int rejected;
            Check("O1.10 非法输入一律拒绝（NaN 方向 / 零长度方向 / count 越界 / 距离非正 / 原点非有限 / 缓冲过小 / 空数组）",
                  !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, new float[] { float.NaN }, new float[] { 0f }, 1, 10f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, new float[] { 0f }, new float[] { 0f }, 1, 10f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, dirX, dirZ, 0, 10f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, dirX, dirZ, PMUnityBattleAimMath.MaxFanDirections + 1, 10f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, dirX, dirZ, fanCount, 0f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, float.NaN, dirX, dirZ, fanCount, 10f, scratch, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, dirX, dirZ, fanCount, 10f, tooSmall, out rejected)
                  && !PMUnityBattleAimMath.TryBuildFanPolyline(0f, 0f, 0f, null, null, fanCount, 10f, scratch, out rejected),
                  "坏几何绝不进 LineRenderer");

            float previewRadius;
            bool standard = PMUnityBattleAimMath.TryPreviewHitRadius(0.1f, 0.4f, 0.30f,
                                                                       out previewRadius);
            Check("O1.11a 标准目标+首批权威弹：覆盖半径0.8m、视觉宽1.6m，不是0.06m细线",
                  standard && Near(previewRadius, 0.8f, 1e-6f)
                  && Near(previewRadius * 2f, 1.6f, 1e-6f),
                  "radius=" + previewRadius + " width=" + (previewRadius * 2f));
            bool otherSpec = PMUnityBattleAimMath.TryPreviewHitRadius(0.25f, 0.4f, 0.30f,
                                                                        out previewRadius);
            Check("O1.11b 覆盖半径随权威Spec变化（不硬编码0.8或复用ShootWidth）",
                  otherSpec && Near(previewRadius, 0.95f, 1e-6f),
                  "radius=" + previewRadius);
            Check("O1.11c 非法弹/目标/容差/溢出范围拒画，不能产生假宽度",
                  !PMUnityBattleAimMath.TryPreviewHitRadius(0f, 0.4f, 0.3f, out previewRadius)
                  && !PMUnityBattleAimMath.TryPreviewHitRadius(0.1f, 0f, 0.3f, out previewRadius)
                  && !PMUnityBattleAimMath.TryPreviewHitRadius(0.1f, 0.4f, -0.1f, out previewRadius)
                  && !PMUnityBattleAimMath.TryPreviewHitRadius(float.NaN, 0.4f, 0.3f, out previewRadius)
                  && !PMUnityBattleAimMath.TryPreviewHitRadius(0.1f, float.PositiveInfinity, 0.3f, out previewRadius)
                  && !PMUnityBattleAimMath.TryPreviewHitRadius(1e30f, 0.4f, 0.3f, out previewRadius)
                  && previewRadius == 0f,
                  "坏配置既不扩大也不悄悄缩小预览");

            // T-AIM 修前反例：当前只有0.06m中心线。必须再有按DS阈值估算的覆盖带；
            // 这是源码接线检查，不代替下面即将新增的纯数学半径负例与Unity真实画面。
            string aimPath = FindRepoFile("Client/Assets/Scripts/PMUnity/PMUnityBattleAimIndicator.cs");
            string hostPath = FindRepoFile("Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs");
            string aimSource = aimPath == null ? null : Compact(StripComments(System.IO.File.ReadAllText(aimPath)));
            string hostSource = hostPath == null ? null : Compact(StripComments(System.IO.File.ReadAllText(hostPath)));
            Check("O1.11 T-AIM Host从Spec/标准胶囊/DS容差算预览半径、Renderer双层同点画",
                  PreviewBandWiringOk(aimSource, hostSource),
                  "必须两层同点同方向，半径出自DS几何而非ShootWidth；释放须同时关线");
            Check("O1.11d 【负例自证】若把DS容差改成0，同一接线判据必须为假",
                  hostSource != null && !PreviewBandWiringOk(aimSource,
                      hostSource.Replace("PMProjectileValidator.HitToleranceM,outpreviewHitRadius)",
                                         "0f,outpreviewHitRadius)")),
                  "改掉容差输入后同一条接线门必须转红");
            Check("O1.11e 【负例自证】若覆盖带不再画同一批角点，同一判据必须为假",
                  aimSource != null && !PreviewBandWiringOk(
                      aimSource.Replace("_hitBand.SetPosition(i,point);", string.Empty), hostSource),
                  "错位覆盖带不得通过同一条接线门");
        }

        private static bool PreviewBandWiringOk(string aimSource, string hostSource)
        {
            return aimSource != null && hostSource != null
                && aimSource.Contains("LineRenderer_hitBand")
                && aimSource.Contains("_hitBand.startWidth=diameter;")
                && aimSource.Contains("_hitBand.endWidth=diameter;")
                && aimSource.Contains("_line.SetPosition(i,point);")
                && aimSource.Contains("_hitBand.SetPosition(i,point);")
                && aimSource.Contains("_hitBand.alignment=LineAlignment.TransformZ;")
                && aimSource.Contains("if(_hitBand!=null&&_hitBand.enabled){_hitBand.enabled=false;}")
                && hostSource.Contains("PMUnityBattleAimMath.TryPreviewHitRadius(")
                && hostSource.Contains("plan.Spec.RadiusM,PMMoverDefaults.CapsuleRadiusMeters,")
                && hostSource.Contains("PMProjectileValidator.HitToleranceM,outpreviewHitRadius)")
                && hostSource.Contains("distanceMeters,previewHitRadius)");
        }

        private static void TestLive3AimEdgeContracts()
        {
            Section("O2. T-LIVE3 瞄准状态机镜像（重复边沿 / 释放隐藏 / 清理幂等）");

            UiAimMirror aim = new UiAimMirror();
            aim.Push(true, 0.5f, 0.5f);
            Check("O2.1 按住（down）即推送瞄准 —— 拖动阶段就应看到瞄准线",
                  aim.Active && aim.PushCount == 1, "push=" + aim.PushCount.ToString());

            aim.Push(true, 0.5f, 0.5f);
            Check("O2.2 同值重复推送被去重（不刷屏、不重复上行）",
                  aim.PushCount == 1, "push=" + aim.PushCount.ToString());

            aim.Push(true, -0.7f, 0.2f);
            Check("O2.3 方向变化才推送",
                  aim.PushCount == 2 && Near(aim.ScreenX, -0.7f, 1e-6f) && Near(aim.ScreenY, 0.2f, 1e-6f),
                  "screen=(" + aim.ScreenX + "," + aim.ScreenY + ")");

            aim.Hide();
            Check("O2.4 松手/取消立即隐藏瞄准线", !aim.Active && aim.HideCount == 1,
                  "hide=" + aim.HideCount.ToString());

            aim.Hide();
            Check("O2.5 重复隐藏幂等（不重复清）", aim.HideCount == 1 && !aim.Active,
                  "hide=" + aim.HideCount.ToString());

            aim.Push(true, -0.7f, 0.2f);
            Check("O2.6 隐藏后同一向量再按下必须**重新**显示（去重不能吃掉重新激活的边沿）",
                  aim.Active && aim.PushCount == 3 && aim.HideCount == 1, "push=" + aim.PushCount.ToString());

            bool nanAccepted = aim.Push(true, float.NaN, 0f);
            Check("O2.7 NaN 输入被拒且不污染已有瞄准状态",
                  !nanAccepted && aim.Active && Near(aim.ScreenX, -0.7f, 1e-6f),
                  "screenX=" + aim.ScreenX);

            aim.Clear();
            Check("O2.8 清理（死亡/终局/Freeze/Stop/换局）立即隐藏",
                  !aim.Active && aim.ClearCount == 1 && aim.HideCount == 2,
                  "clear=" + aim.ClearCount.ToString() + " hide=" + aim.HideCount.ToString());

            aim.Clear();
            Check("O2.9 重复清理幂等", aim.ClearCount == 1 && aim.HideCount == 2, "clear=" + aim.ClearCount.ToString());

            UiAimMirror attack = new UiAimMirror();
            attack.Push(false, 0.9f, 0f);
            Check("O2.10 拖动阶段**不**提交攻击（只记瞄准）",
                  attack.AttackSubmits == 0 && attack.Active && attack.PushCount == 1,
                  "submits=" + attack.AttackSubmits.ToString());

            attack.Submit();
            attack.Hide();
            attack.Hide();
            Check("O2.11 松手：提交一次 + 隐藏一次；重复隐藏不重复提交",
                  attack.AttackSubmits == 1 && attack.HideCount == 1 && !attack.Active,
                  "submits=" + attack.AttackSubmits.ToString() + " hide=" + attack.HideCount.ToString());
        }

        private static void TestLive3AimSourceContracts()
        {
            Section("O3. T-LIVE3 宿主/UI/指示器 静态契约（含负例自证）");

            string uiPath = FindRepoFile("Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs");
            string hostPath = FindRepoFile("Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs");
            string aimPath = FindRepoFile("Client/Assets/Scripts/PMUnity/PMUnityBattleAimIndicator.cs");

            Check("O3.1 UI / 宿主 / 瞄准指示器三个源码都可读",
                  uiPath != null && hostPath != null && aimPath != null,
                  (uiPath ?? "<无 UI>") + " | " + (hostPath ?? "<无宿主>") + " | " + (aimPath ?? "<无指示器>"));
            if (uiPath == null || hostPath == null || aimPath == null) { return; }

            string ui = Compact(StripComments(System.IO.File.ReadAllText(uiPath)));
            string host = Compact(StripComments(System.IO.File.ReadAllText(hostPath)));
            string aim = Compact(StripComments(System.IO.File.ReadAllText(aimPath)));

            // ---- UI：setAim 出口 / 按住推送 / 释放隐藏 / 单次提交 ----
            Check("O3.2 UI 新增瞄准预览出口（setAim 委托 + 四参数 Bind）",
                  ui.Contains("publicdelegateboolPMUnityBattleAimInputHandler(boolisSuper,boolactive,floatscreenX,floatscreenY)")
                  && ui.Contains("publicvoidBind(PMUnityBattleMoveInputHandlersetMove,PMUnityBattleAttackInputHandlerqueueAttack,PMUnityBattleStatusQueryqueryStatus,PMUnityBattleAimInputHandlersetAim)"),
                  "冻结的 setAim 出口");

            string apply = Between(ui, "private void ApplyPointer(", "private void ReleaseStick(");
            Check("O3.3 ★ 按住/拖动即推送瞄准（ApplyPointer 内 PushAim(stick,true,screenX,screenY)）",
                  HasAimPushWhileHeld(apply),
                  apply == null ? "未定位到 ApplyPointer" : "拖动阶段即可见");

            Check("O3.4 【负例自证】删掉按住推送后同一判据为假（该断言不是空转）",
                  apply != null && !HasAimPushWhileHeld(apply.Replace("PushAim(stick,true,screenX,screenY)", string.Empty)),
                  "同一谓词对变异后的文本返回 false");

            string release = Between(ui, "private void ReleaseStick(", "private void CancelStick(");
            Check("O3.5 ★ 释放/取消都隐藏瞄准线（ReleaseStick 内 PushAim(stick,false,0f,0f)）",
                  HasAimHideOnRelease(release),
                  release == null ? "未定位到 ReleaseStick" : "松手即关");

            Check("O3.6 【负例自证】删掉隐藏调用后同一判据为假（该断言不是空转）",
                  release != null && !HasAimHideOnRelease(release.Replace("PushAim(stick,false,0f,0f)", string.Empty)),
                  "同一谓词对变异后的文本返回 false");

            Check("O3.7 原一次攻击边沿不变（QueueAttack 只有 ReleaseStick(submit:true) 一个调用点）",
                  CountOccurrences(ui, "QueueAttack(stick.IsSuper,") == 1
                  && ui.Contains("ReleaseStick(stick,submit:true)"),
                  "一次手势 = 一次上行（瞄准预览与提交分离）");

            Check("O3.8 Dispose 摘掉 setAim 出口（防销毁瞬间回调仍生效）",
                  ui.Contains("_setAim=null;"), "与 _setMove/_queueAttack 同纪律");

            // ---- 宿主：冻结入口 / 同一变换 / 单消费点 / owner-only / 清理 / 释放 ----
            Check("O3.9 宿主新增冻结入口 TrySetUiAim(bool,bool,float,float) bool",
                  host.Contains("publicstaticboolTrySetUiAim(boolisSuper,boolactive,floatscreenX,floatscreenY)"),
                  "第四个对外冻结入口");

            string tryAim = Between(host, "public static bool TrySetUiAim(", "public static bool TryQueueUiAttack(");
            string tryAttack = Between(host, "public static bool TryQueueUiAttack(", "public static bool TryGetUiCombatSnapshot(");
            Check("O3.10 ★ 预览与提交共用同一份屏幕→世界变换（TryScreenAimDirection）⇒ 不偏轴",
                  tryAim != null && tryAim.Contains("PMUnityMoverInput.TryScreenAimDirection(")
                  && tryAttack != null && tryAttack.Contains("PMUnityMoverInput.TryScreenAimDirection("),
                  "两边同一个世界向量");

            Check("O3.11 宿主只绑定一次（三输入 + setAim 四个方法组）",
                  host.Contains("session.Controls.Bind(TrySetUiMove,TryQueueUiAttack,QueryBattleControlsStatus,TrySetUiAim);"),
                  "接线面唯一");

            string aimUpdate = Between(host, "private static void UpdateAimIndicator(", "private static void UpdateCombatHud(");
            Check("O3.12 ★ 瞄准线只为本端 Owner 更新（用 session.OwnerPlayer + FindMovementRig；不遍历 Movements）",
                  aimUpdate != null && aimUpdate.Contains("session.OwnerPlayer")
                  && aimUpdate.Contains("FindMovementRig(session,owner)")
                  && !aimUpdate.Contains("session.Movements"),
                  aimUpdate == null ? "未定位到 UpdateAimIndicator" : "DS/远端 SP 零绘制");

            Check("O3.13 ★ 距离与扇形来自同一份共用 planner（TryBuild 的 Spec/Directions）",
                  aimUpdate != null && aimUpdate.Contains("PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId,")
                  && aimUpdate.Contains(".Spec.SpeedMps")
                  && aimUpdate.Contains(".Spec.LifetimeMs")
                  && aimUpdate.Contains(".Directions"),
                  "与 TryCombatAttack 同一口径");

            Check("O3.14 清理：ClearUiInput 里同时清瞄准（冻结/死亡/终局/停局/换局全覆盖）",
                  host.Contains("ClearUiAim(session);")
                  && Between(host, "private static void ClearUiInput(", "private static bool IsOwnerDeadOrEnded(") != null
                  && Between(host, "private static void ClearUiInput(", "private static bool IsOwnerDeadOrEnded(")
                      .Contains("ClearUiAim(session)"),
                  "四条清局路径都经 ClearUiInput");

            Check("O3.15 释放：ReleaseSession 里 Dispose 瞄准指示器（并清会话引用）",
                  Between(host, "private static void ReleaseSession(", "private static void SuppressOtherCameras(") != null
                  && Between(host, "private static void ReleaseSession(", "private static void SuppressOtherCameras(")
                      .Contains("AimIndicator.Dispose()"),
                  "不退局残留");

            Check("O3.16 【负例】瞄准更新里不得出现远端副本循环（不得给 SP 画线）",
                  aimUpdate != null && !aimUpdate.Contains("for(inti=0;i<session.Movements.Count"),
                  "只画本端 Owner");

            // ---- 指示器：纯表现 / DS 拒绝 / 显式材质颜色 / 零 RPC ----
            Check("O3.17 指示器只在真实 Unity/Editor 编入完整实现，替身面显式失败",
                  aim.Contains("publicconstboolLineRendererImplementationCompiled=true;")
                  && aim.Contains("publicconstboolLineRendererImplementationCompiled=false;")
                  && aim.Contains("#ifUNITY_2019_1_OR_NEWER||UNITY_EDITOR")
                  && aim.Contains("#else"),
                  "两个编译面同形");

            Check("O3.18 DS 拒绝创建（无头进程不画任何东西）",
                  aim.Contains("PMNetRuntime.IsDedicatedServer"), "与 UI/HUD 同纪律");

            Check("O3.19 显式可见材质与颜色（Shader.Find + new Material + startColor/endColor + 线宽）",
                  aim.Contains("Shader.Find(") && aim.Contains("newMaterial(")
                  && aim.Contains("startColor=") && aim.Contains("endColor=")
                  && aim.Contains("startWidth=") && aim.Contains("endWidth="),
                  "不依赖默认（默认 LineRenderer 材质不可见）");

            Check("O3.20 隐藏走 LineRenderer.enabled=false（与旧 TouchLogic 同一生命周期）",
                  aim.Contains(".enabled=false"), "松手即关，不销毁对象");

            string[] aimForbidden = new string[]
            {
                "ServerCombatAttackV1", "ClientCombatAttackResultV1", "ClientCombatMatchResultV1", "ServerCombatResultAckV1",
                "PMNet_", "MarkPropertyDirty", "TryAttack(", "CombatHp=", "CombatMana=", "CombatSuperEnergy=",
                "Instantiate", "FindObjectOfType", "SendMessage", "LoadScene", "TouchLogic", "EasyJoystick",
            };

            int aimForbiddenHits = 0;
            for (int i = 0; i < aimForbidden.Length; i++)
            {
                if (aim.Contains(aimForbidden[i]))
                {
                    aimForbiddenHits++;
                    Check("指示器（去注释后）不得出现 RPC/权威写/旧链标记：" + aimForbidden[i], false);
                }
            }

            Check("O3.21 指示器零 RPC / 零权威写 / 零旧链（禁用标记命中 0 处，共查 "
                  + aimForbidden.Length.ToString() + " 个）", aimForbiddenHits == 0, "纯表现");

            int aimMutated = 0;
            for (int i = 0; i < aimForbidden.Length; i++)
            {
                if ((aim + "\n" + aimForbidden[i] + "();\n").Contains(aimForbidden[i])) { aimMutated++; }
            }

            Check("O3.22 【负例自证】把禁用标记注入指示器文本后扫描器 100% 命中",
                  aimMutated == aimForbidden.Length,
                  aimMutated.ToString() + "/" + aimForbidden.Length.ToString() + " ⇒ 0 命中不是空转");
        }

        /// <summary>O3 用：取紧凑文本里两个签名之间的片段（找不到返回 null）。</summary>
        private static string Between(string compact, string startSignature, string endSignature)
        {
            if (compact == null) { return null; }

            string startToken = Compact(startSignature);
            string endToken = Compact(endSignature);

            int start = compact.IndexOf(startToken, StringComparison.Ordinal);
            if (start < 0) { return null; }

            int end = compact.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
            if (end < 0) { return null; }

            return compact.Substring(start, end - start);
        }

        /// <summary>
        /// O3 用：UI 是否在“按住/拖动”阶段就把瞄准向量推给宿主（“按住即预览”的唯一判据）。
        /// 单独抽成谓词，是为了让 O3.4 的**变异自证**能直接对同一个谓词取反 —— 否则它只是文本巧合门。
        /// </summary>
        private static bool HasAimPushWhileHeld(string applyPointerBody)
        {
            return applyPointerBody != null
                   && applyPointerBody.Contains("PushAim(stick,true,screenX,screenY)");
        }

        /// <summary>O3 用：UI 是否在松手/取消路径隐藏瞄准线（同 <see cref="HasAimPushWhileHeld"/> 的口径）。</summary>
        private static bool HasAimHideOnRelease(string releaseStickBody)
        {
            return releaseStickBody != null
                   && releaseStickBody.Contains("PushAim(stick,false,0f,0f)");
        }

        /// <summary>
        /// O2 用的**可执行镜像**：T-LIVE3 瞄准状态机的规则表。
        ///
        ///   规则                                                  | _active | 计数
        ///   ------------------------------------------------------|---------|------------------------
        ///   Push(active=true, 值)（down/drag）                     | true    | 值变化才 PushCount++
        ///   Push(active=false) / Hide()（松手 / 取消）              | false   | 首次 HideCount++
        ///   Clear()（死亡/终局/Freeze/Stop/换局）                   | false   | 首次 ClearCount++ 并隐藏
        ///   非有限值                                               | 不变    | 拒绝（返回 false）
        ///   隐藏**不**提交攻击；提交只在松手边沿（Submit()）         | —       | AttackSubmits++
        ///
        /// 它不是 UI 的实现（本工程编不到 uGUI 分支），只把“重复边沿/去重/清理幂等”跑成正负例。
        /// </summary>
        private sealed class UiAimMirror
        {
            public const float Epsilon = PMUnityBattleControlMath.MovePushEpsilon;

            public bool Active;
            public bool IsSuper;
            public float ScreenX;
            public float ScreenY;

            public int PushCount;
            public int HideCount;
            public int ClearCount;
            public int AttackSubmits;
            public int Rejected;

            public bool Push(bool isSuper, float screenX, float screenY)
            {
                if (!PMUnityBattleControlMath.IsFinite(screenX) || !PMUnityBattleControlMath.IsFinite(screenY))
                {
                    Rejected++;
                    return false;
                }

                bool unchanged = Active && IsSuper == isSuper
                                 && Near(ScreenX, screenX, Epsilon) && Near(ScreenY, screenY, Epsilon);

                Active = true;
                IsSuper = isSuper;
                ScreenX = screenX;
                ScreenY = screenY;

                if (!unchanged) { PushCount++; }
                return true;
            }

            public void Hide()
            {
                bool wasActive = Active;
                Active = false;
                ScreenX = 0f;
                ScreenY = 0f;

                if (wasActive) { HideCount++; }
            }

            public void Clear()
            {
                if (Active) { ClearCount++; }
                Hide();
            }

            public void Submit()
            {
                if (Active) { AttackSubmits++; }
            }
        }

        /// <summary>
        /// 去掉 <c>//</c> 行注释与 <c>/* */</c> 块注释（识别字符串/字符字面量，不误删字面量里的 <c>//</c>）。
        ///
        /// 为什么需要：静态契约里的**负例**（“不得出现 X”）必须在代码上成立，
        /// 而不是因为文档注释里提到 X 就变红、或者因为注释里写着 Y 就变绿。
        /// </summary>
        private static string StripComments(string source)
        {
            if (string.IsNullOrEmpty(source)) { return source; }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(source.Length);
            bool inString = false;
            bool inChar = false;
            bool inLine = false;
            bool inBlock = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char n = i + 1 < source.Length ? source[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n') { inLine = false; sb.Append(c); }
                    continue;
                }

                if (inBlock)
                {
                    if (c == '*' && n == '/') { inBlock = false; i++; }
                    else if (c == '\n') { sb.Append(c); }
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') { if (n != '\0') { sb.Append(n); i++; } }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\') { if (n != '\0') { sb.Append(n); i++; } }
                    else if (c == '\'') { inChar = false; }
                    continue;
                }

                if (c == '/' && n == '/') { inLine = true; i++; continue; }
                if (c == '/' && n == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == '\'') { inChar = true; sb.Append(c); continue; }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 取「从某方法签名到下一个成员（public static / private static）」之间的紧凑文本。
        ///
        /// 为什么不用现成的 <see cref="ExtractMethod"/>：它只认 <c>private static</c> 边界，
        /// 而本节要核对的三个 UI 入口都是 <c>public static</c>，用旧辅助会把后一个方法一起拿进来，
        /// 使得“本方法内不得发 RPC”的负例失去意义。
        /// </summary>
        private static string ExtractMember(string compactSource, string signature)
        {
            if (compactSource == null) { return null; }

            string needle = Compact(signature);
            int start = compactSource.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) { return null; }

            int from = start + needle.Length;
            int end = compactSource.Length;

            int pub = compactSource.IndexOf("publicstatic", from, StringComparison.Ordinal);
            if (pub >= 0 && pub < end) { end = pub; }

            int priv = compactSource.IndexOf("privatestatic", from, StringComparison.Ordinal);
            if (priv >= 0 && priv < end) { end = priv; }

            return compactSource.Substring(start, end - start);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) { return 0; }

            int count = 0;
            int index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }

            return count;
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
