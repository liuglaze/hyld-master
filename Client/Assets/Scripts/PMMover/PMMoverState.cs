// ============================================================================
//  PMMover 纯状态与命令类型（R4-A / M08）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-prediction-contract.md 的「Mover 冻结字段与机制」一节；
//  边界（硬）：Client/Assets/Scripts/PMMover/** 只允许依赖
//      · PMNet（PMFrameId / PMTimeStep，来自 PMNetIdentity.cs）
//      · PMNet.Prediction（IPMPredictionModel / PMSimulationResult 契约）
//    严禁引用 UnityEngine / UE / 任何业务类型。门禁 Tools/PMMoverCoreCheck 用
//    netstandard2.0 + C#7.3 零依赖编译本目录，把这条纪律变成可自动检查的门。
//
//  本文件只放「数据」。行为在 PMMoverModel.cs，碰撞替身在 PMMoverTestWorld.cs。
//
//  三条不可违反的纪律（契约原文，逐条落在此处）：
//    1) 纯 float32 + Y-up + 米。模型内部**不**做队伍镜像/轴向重命名：
//       输入已是权威世界坐标；镜像是未来输入/表现适配边界的事。
//    2) 所有"影响未来模拟"的值都必须在 SyncState 内（含 Grounded / GroundNormal /
//       PreAdditiveVelocity / 有效运动参数 / 活跃层完整定义），否则回滚重放必然发散。
//       帧标签（帧号/累计时间）不放这里，放 Snapshot。
//    3) 深 clone 不产生数组别名：任何对外给出的 SyncState 都持有**新**数组；
//       调用方把引用型入参一律按不可变对待。
//
//  单位约定（全文件统一，不复述）：
//    · 位置 / 距离：米            · 速度：米/秒
//    · 时间：毫秒（int / float）   · 角度：度（Yaw 绕 Y 轴）
//    · 重力 / 加速度：米/秒²       · Scale：无量纲倍数
// ============================================================================

using System;

namespace PMNet.Mover
{
    /// <summary>
    /// 纯 float32 三维向量，坐标系固定 **Y-up**，长度单位 **米**，方向 Y 为正。
    ///
    /// 刻意不复用 <c>UnityEngine.Vector3</c>：本目录要能被 netstandard2.0 门禁零依赖编译，
    /// 而 UnityEngine 是 Unity 侧类型。这是「模型与引擎解耦」的第一块基石。
    ///
    /// 它是 struct：赋值即完整拷贝，不存在别名问题（数组里的元素同理）。
    /// </summary>
    public struct PMVector3 : IEquatable<PMVector3>
    {
        /// <summary>判零阈值。与 <c>PMNet.Shared.PMBattleSim.ZeroEpsilon</c> 同值，便于跨模块阅读。</summary>
        public const float Epsilon = 1e-6f;

        public float X;
        public float Y;
        public float Z;

        public PMVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>零向量。</summary>
        public static PMVector3 Zero { get { return new PMVector3(0f, 0f, 0f); } }

        /// <summary>世界"上"（Y-up 的 up 轴只有这一处定义，禁止在别处再写一遍）。</summary>
        public static PMVector3 Up { get { return new PMVector3(0f, 1f, 0f); } }

        public float LengthSquared { get { return X * X + Y * Y + Z * Z; } }

        public float Length { get { return (float)Math.Sqrt((double)(X * X + Y * Y + Z * Z)); } }

        public static PMVector3 operator +(PMVector3 a, PMVector3 b)
        {
            return new PMVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static PMVector3 operator -(PMVector3 a, PMVector3 b)
        {
            return new PMVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static PMVector3 operator -(PMVector3 a)
        {
            return new PMVector3(-a.X, -a.Y, -a.Z);
        }

        public static PMVector3 operator *(PMVector3 a, float s)
        {
            return new PMVector3(a.X * s, a.Y * s, a.Z * s);
        }

        public static PMVector3 operator *(float s, PMVector3 a)
        {
            return new PMVector3(a.X * s, a.Y * s, a.Z * s);
        }

        public static float Dot(PMVector3 a, PMVector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>两点距离。</summary>
        public static float Distance(PMVector3 a, PMVector3 b)
        {
            return (a - b).Length;
        }

        /// <summary>
        /// 归一化。长度 ≤ <see cref="Epsilon"/> 时返回零向量（**不抛异常、不产生 NaN**）：
        /// 零输入是正常输入（松开摇杆），不是错误。
        /// </summary>
        public static PMVector3 Normalized(PMVector3 v)
        {
            float len = v.Length;
            if (len <= Epsilon)
            {
                return Zero;
            }

            return v * (1f / len);
        }

        /// <summary>是否全部分量为有限值（拒绝 NaN / ±Infinity）。</summary>
        public bool IsFinite
        {
            get
            {
                return !float.IsNaN(X) && !float.IsInfinity(X)
                       && !float.IsNaN(Y) && !float.IsInfinity(Y)
                       && !float.IsNaN(Z) && !float.IsInfinity(Z);
            }
        }

        /// <summary>分量逐个精确相等（-0.0f 与 +0.0f 视为相等）。比较阈值由调用方决定，见 <see cref="PMMoverDefaults"/>。</summary>
        public bool Equals(PMVector3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is PMVector3 && Equals((PMVector3)obj);
        }

        public override int GetHashCode()
        {
            return (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode();
        }

        public static bool operator ==(PMVector3 a, PMVector3 b) { return a.Equals(b); }
        public static bool operator !=(PMVector3 a, PMVector3 b) { return !a.Equals(b); }

        public override string ToString()
        {
            return "(" + X.ToString("R") + ", " + Y.ToString("R") + ", " + Z.ToString("R") + ")";
        }
    }

    /// <summary>
    /// 运动模式。**数值已冻结**（契约「Modes 数值 Walking=0 Falling=1 Flying=2 Inactive=3」）：
    /// 数值会进网络/快照，任何重排都是协议破坏。
    ///
    /// Inactive 的语义是「不施加任何速度/加速度，但时间轴继续」——
    /// 不是「关掉模拟」（关模拟会让预测/回滚整段冻结，属另一个概念）。
    /// </summary>
    public enum PMMoverMode : byte
    {
        Walking = 0,
        Falling = 1,
        Flying = 2,
        Inactive = 3,
    }

    /// <summary>运动模式枚举的显式判定。**禁止**用数值比较或 switch 的默认分支猜模式。</summary>
    public static class PMMoverModes
    {
        /// <summary>已定义的模式个数（Walking/Falling/Flying/Inactive）。</summary>
        public const int Count = 4;

        /// <summary>该整数值是否对应一个已定义模式。未知值必须由调用方显式拒绝。</summary>
        public static bool IsDefined(int value)
        {
            return value >= 0 && value < Count;
        }

        public static bool IsDefined(PMMoverMode mode)
        {
            return IsDefined((int)mode);
        }

        /// <summary>诊断用名字（未知值返回 "Unknown(n)"，不假装它是 Walking）。</summary>
        public static string Name(PMMoverMode mode)
        {
            switch (mode)
            {
                case PMMoverMode.Walking: return "Walking";
                case PMMoverMode.Falling: return "Falling";
                case PMMoverMode.Flying: return "Flying";
                case PMMoverMode.Inactive: return "Inactive";
                default: return "Unknown(" + (int)mode + ")";
            }
        }
    }

    /// <summary>
    /// 叠加层（LayeredMove）的混合种类。首批**只**实现两个，数值已冻结；
    /// <c>OverrideVelocityAdditiveVertical</c> / <c>OverrideAllAdditiveAll</c> 等项目族后置
    /// （契约「Layers AdditiveVelocity=0/OverrideVelocity=1」）。
    ///
    /// Additive 门槛：只要存在 <see cref="OverrideVelocity"/> 层，本帧全部 AdditiveVelocity 层
    /// 都不参与混合（见 PMMoverModel 的 LayerMix）。
    /// </summary>
    public enum PMMoverLayerKind : byte
    {
        AdditiveVelocity = 0,
        OverrideVelocity = 1,
    }

    /// <summary>叠加层种类的显式判定（未知值必须拒绝，不默认成 Additive）。</summary>
    public static class PMMoverLayerKinds
    {
        public const int Count = 2;

        public static bool IsDefined(int value)
        {
            return value >= 0 && value < Count;
        }

        public static bool IsDefined(PMMoverLayerKind kind)
        {
            return IsDefined((int)kind);
        }

        public static string Name(PMMoverLayerKind kind)
        {
            switch (kind)
            {
                case PMMoverLayerKind.AdditiveVelocity: return "AdditiveVelocity";
                case PMMoverLayerKind.OverrideVelocity: return "OverrideVelocity";
                default: return "Unknown(" + (int)kind + ")";
            }
        }
    }

    /// <summary>
    /// 一个**活跃**叠加层（进 SyncState，随回滚 clone/恢复/比较）。
    ///
    /// 字段完整性的判据：只要漏掉任何一个会影响未来模拟的字段，回滚重放就会在两端发散。
    /// 因此 instanceId / kind / priority / velocity / durationMs **全部**入 SyncState。
    ///
    /// <see cref="ElapsedMs"/> 也入 SyncState（参与 clone/恢复），但**不参与**
    /// <c>ShouldReconcile</c> 的实例比较：它是"进度"而不是"实例定义"，
    /// 权威端与预测端的进度差一帧属正常，把它算作差异会导致每帧无谓回滚。
    /// </summary>
    public struct PMMoverLayer
    {
        /// <summary>层实例身份。**必须非零**；同 id 再次请求视为替换（重置进度）。</summary>
        public uint InstanceId;

        public PMMoverLayerKind Kind;

        /// <summary>稳定排序键（先 priority，再 InstanceId）。数值越大越"后"生效。</summary>
        public int Priority;

        /// <summary>层自带速度（米/秒）。Additive 为叠加量，Override 为替换值。</summary>
        public PMVector3 Velocity;

        /// <summary>
        /// 持续时长（毫秒）。<c>&gt; 0</c> 有限时长；<c>0</c> 表示无时长（直到被显式移除）；
        /// 负数非法，由模型显式拒绝。
        /// </summary>
        public int DurationMs;

        /// <summary>已存活毫秒（按**原 dt** 累加，不回读墙钟）。</summary>
        public int ElapsedMs;

        /// <summary>是否有限时长层。</summary>
        public bool HasFiniteDuration { get { return DurationMs > 0; } }

        /// <summary>是否已到期（仅有限时长层可到期）。到期层在**帧首**清扫，不参与本帧混合。</summary>
        public bool IsExpired { get { return DurationMs > 0 && ElapsedMs >= DurationMs; } }

        /// <summary>
        /// 两个层是否为"同一实例定义"（用于 reconcile 的实例比较）。
        /// <see cref="ElapsedMs"/> 刻意不参与，理由见类型注释。
        /// </summary>
        public bool SameDefinition(PMMoverLayer other)
        {
            return InstanceId == other.InstanceId
                   && Kind == other.Kind
                   && Priority == other.Priority
                   && DurationMs == other.DurationMs
                   && Velocity.Equals(other.Velocity);
        }

        public override string ToString()
        {
            return "Layer#" + InstanceId + "(" + PMMoverLayerKinds.Name(Kind) + " p=" + Priority
                   + " v=" + Velocity + " " + ElapsedMs + "/" + DurationMs + "ms)";
        }
    }

    /// <summary>
    /// 新增（或替换）叠加层的**请求**（属于 InputCmd：每帧重建、必须过网、可重放）。
    /// 与 <see cref="PMMoverLayer"/> 的差别：请求没有进度，进度由模型在帧末累加。
    /// </summary>
    public struct PMMoverLayerRequest
    {
        public uint InstanceId;
        public PMMoverLayerKind Kind;
        public int Priority;
        public PMVector3 Velocity;

        /// <summary>同 <see cref="PMMoverLayer.DurationMs"/>：<c>&gt;0</c> 有限、<c>0</c> 无时长、负数非法。</summary>
        public int DurationMs;

        public PMMoverLayerRequest(uint instanceId, PMMoverLayerKind kind, int priority,
                                   PMVector3 velocity, int durationMs)
        {
            InstanceId = instanceId;
            Kind = kind;
            Priority = priority;
            Velocity = velocity;
            DurationMs = durationMs;
        }

        /// <summary>转成活跃层（进度从 0 开始）。调用前必须已完成合法性校验。</summary>
        public PMMoverLayer ToLayer()
        {
            PMMoverLayer layer = new PMMoverLayer();
            layer.InstanceId = InstanceId;
            layer.Kind = Kind;
            layer.Priority = Priority;
            layer.Velocity = Velocity;
            layer.DurationMs = DurationMs;
            layer.ElapsedMs = 0;
            return layer;
        }
    }

    /// <summary>
    /// 瞬时效果（InstantEffect）的种类。首批**只**实现这四个（契约
    /// 「InstantEffect 最小 SetVelocity/Teleport/SetMode/SetParameters」）。
    ///
    /// 身份/裁决说明（本批明确不做）：这些请求由**已鉴权的宿主可信命令**携带，
    /// 模型把它们当作已裁定的指令执行，不做权限、来源、重放裁决——
    /// 网络策略与"谁有权下发/如何拒绝"是 R4-B 的接线，本批不宣称已完成。
    /// </summary>
    public enum PMMoverEffectKind : byte
    {
        /// <summary>把速度设为给定值（同时写 Velocity 与 PreAdditiveVelocity）。</summary>
        SetVelocity = 0,

        /// <summary>把位置直接设为给定值（不重置速度）。</summary>
        Teleport = 1,

        /// <summary>切换运动模式；未知模式值必须**显式拒绝**（禁止默认回退到 Walking）。</summary>
        SetMode = 2,

        /// <summary>覆写有效运动参数。</summary>
        SetParameters = 3,
    }

    /// <summary>效果种类的显式判定（未知值必须拒绝）。</summary>
    public static class PMMoverEffectKinds
    {
        public const int Count = 4;

        public static bool IsDefined(int value)
        {
            return value >= 0 && value < Count;
        }

        public static bool IsDefined(PMMoverEffectKind kind)
        {
            return IsDefined((int)kind);
        }

        public static string Name(PMMoverEffectKind kind)
        {
            switch (kind)
            {
                case PMMoverEffectKind.SetVelocity: return "SetVelocity";
                case PMMoverEffectKind.Teleport: return "Teleport";
                case PMMoverEffectKind.SetMode: return "SetMode";
                case PMMoverEffectKind.SetParameters: return "SetParameters";
                default: return "Unknown(" + (int)kind + ")";
            }
        }
    }

    /// <summary>
    /// 一条瞬时效果请求。刻意用一个宽结构而不是继承体系：
    /// 它要跨越 netstandard2.0 门禁、要能被逐字段比较、要能整体拷贝，
    /// 多态会引入装箱与引用别名，得不偿失。
    ///
    /// 只有与 <see cref="Kind"/> 对应的字段有意义；其余字段一律忽略
    /// （不做"猜一个默认值"的隐式回退）。
    /// </summary>
    public struct PMMoverEffectRequest
    {
        public PMMoverEffectKind Kind;

        /// <summary>SetVelocity：速度；Teleport：位置。其余种类忽略。</summary>
        public PMVector3 Vector;

        /// <summary>SetMode：<c>(int)PMMoverMode</c>。未知值必须拒绝。其余种类忽略。</summary>
        public int Mode;

        // ---- SetParameters 载荷（其余种类忽略）----
        public float MaxSpeed;
        public float Acceleration;
        public float Braking;
        public float GravityScale;
        public float JumpSpeed;

        public static PMMoverEffectRequest SetVelocity(PMVector3 velocity)
        {
            PMMoverEffectRequest e = new PMMoverEffectRequest();
            e.Kind = PMMoverEffectKind.SetVelocity;
            e.Vector = velocity;
            return e;
        }

        public static PMMoverEffectRequest Teleport(PMVector3 position)
        {
            PMMoverEffectRequest e = new PMMoverEffectRequest();
            e.Kind = PMMoverEffectKind.Teleport;
            e.Vector = position;
            return e;
        }

        public static PMMoverEffectRequest SetMode(PMMoverMode mode)
        {
            PMMoverEffectRequest e = new PMMoverEffectRequest();
            e.Kind = PMMoverEffectKind.SetMode;
            e.Mode = (int)mode;
            return e;
        }

        public static PMMoverEffectRequest SetParameters(float maxSpeed, float acceleration, float braking,
                                                        float gravityScale, float jumpSpeed)
        {
            PMMoverEffectRequest e = new PMMoverEffectRequest();
            e.Kind = PMMoverEffectKind.SetParameters;
            e.MaxSpeed = maxSpeed;
            e.Acceleration = acceleration;
            e.Braking = braking;
            e.GravityScale = gravityScale;
            e.JumpSpeed = jumpSpeed;
            return e;
        }

        public override string ToString()
        {
            return "Effect " + PMMoverEffectKinds.Name(Kind);
        }
    }

    /// <summary>
    /// 一帧的输入命令（每帧重建、必须过网、可重放）。
    ///
    /// 纯洁性红线（skill 红线 2）：**不放派生数据**。
    /// 这里只有"玩家按键/摇杆能直接决定的值"：
    ///   移动轴原始量（未归一化）、期望朝向、跳跃边沿、
    ///   以及宿主鉴权后的 Effect / Layer 命令队列。
    /// 速度、命中结果、是否着地、距地距离、墙钟时间 —— 一律不许出现在 InputCmd。
    ///
    /// <see cref="JumpPressed"/> 是**边沿**（采集层保证 true 表示本帧起跳沿），
    /// 模型不做去抖、不记忆上一次值。
    /// </summary>
    public struct PMMoverInput
    {
        /// <summary>移动横轴原始量（世界 X）。**不归一化**——归一化是派生，由模型做。</summary>
        public float MoveX;

        /// <summary>移动纵轴原始量（世界 Z；hyld 战斗平面为 XZ）。</summary>
        public float MoveZ;

        /// <summary>竖直轴原始量（世界 Y）。仅 Flying 模式消费（契约「Flying 三维控制」）。</summary>
        public float MoveY;

        /// <summary>期望朝向（度）。本批无转向速率，模型直接采用该朝向。</summary>
        public float YawDegrees;

        /// <summary>跳跃**边沿**。仅 Walking 模式消费。</summary>
        public bool JumpPressed;

        /// <summary>
        /// 已鉴权的瞬时效果命令队列（可为 null，等同空）。
        /// 模型按顺序在**帧前**消费一遍；帧内新入队的在帧末再消费一遍。
        /// </summary>
        public PMMoverEffectRequest[] Effects;

        /// <summary>新增/替换叠加层请求（可为 null，等同空）。</summary>
        public PMMoverLayerRequest[] Layers;

        /// <summary>按 instanceId 显式移除叠加层（可为 null，等同空）。移除不存在的 id 是幂等空操作。</summary>
        public uint[] RemovedLayerIds;

        /// <summary>空输入（全零轴、无命令）。诊断/测试用。</summary>
        public static PMMoverInput Empty()
        {
            PMMoverInput input = new PMMoverInput();
            input.MoveX = 0f;
            input.MoveZ = 0f;
            input.MoveY = 0f;
            input.YawDegrees = 0f;
            input.JumpPressed = false;
            input.Effects = null;
            input.Layers = null;
            input.RemovedLayerIds = null;
            return input;
        }
    }

    /// <summary>
    /// 每实例的运动状态（逐帧持久、参与比较、可重算）。
    ///
    /// 全部字段都在这里、且都要被 clone/恢复/比较（契约「所有影响未来模拟的值参与
    /// clone/恢复/比较」）。**帧标签不放这里**（OutputFrame/ServerFrame/TotalSimTimeMs
    /// 属 Snapshot，硬编码 ShouldReconcile=false）。
    ///
    /// 两个不变量（模型保证，宿主也须遵守）：
    ///   1) <see cref="PreAdditiveVelocity"/> 是"模式自驱的干净速度"，**不含**当前帧叠加层贡献；
    ///      模型每帧以它为积分起点，因此 Additive 层不会跨帧永久累积。
    ///      构造初始状态时必须同时写 Velocity 与 PreAdditiveVelocity（<see cref="CreateDefault"/> 已如此）。
    ///   2) <see cref="ActiveLayers"/> 按 (Priority, InstanceId) 升序排列，是**规范顺序**；
    ///      模型总是返回新数组，调用方不得改写它的元素。
    /// </summary>
    public struct PMMoverSyncState
    {
        /// <summary>角色**中心**位置（米）。中心 Y == halfHeight 时足底正好落在地面上。</summary>
        public PMVector3 Position;

        /// <summary>最终速度（米/秒，含叠加层贡献）。</summary>
        public PMVector3 Velocity;

        /// <summary>模式自驱的干净速度（米/秒，不含叠加层贡献）。见类型注释的不变量 1。</summary>
        public PMVector3 PreAdditiveVelocity;

        /// <summary>朝向（度，绕 Y）。</summary>
        public float YawDegrees;

        public PMMoverMode Mode;

        /// <summary>是否着地（地面查询结果，属 SyncState 而非事件）。</summary>
        public bool Grounded;

        /// <summary>地面法线；未着地时为 <see cref="PMVector3.Up"/>。</summary>
        public PMVector3 GroundNormal;

        /// <summary>缩放倍数（同时作用于胶囊半径与半高）。本批没有改它的 Effect，由权威快照写入。</summary>
        public float Scale;

        // ---- 有效运动参数（改它们必须走 SetParameters Effect / 权威快照）----
        public float MaxSpeed;
        public float Acceleration;
        public float Braking;
        public float GravityScale;
        public float JumpSpeed;

        /// <summary>活跃叠加层（规范顺序，可为 null 表示无层）。</summary>
        public PMMoverLayer[] ActiveLayers;

        /// <summary>
        /// 一个可用的初始状态：站在原点的地面层高度、Walking、参数取
        /// <see cref="PMMoverDefaults"/> 默认值、无层。
        /// </summary>
        public static PMMoverSyncState CreateDefault()
        {
            PMMoverSyncState state = new PMMoverSyncState();
            state.Position = new PMVector3(0f, PMMoverDefaults.CapsuleHalfHeightMeters, 0f);
            state.Velocity = PMVector3.Zero;
            state.PreAdditiveVelocity = PMVector3.Zero;
            state.YawDegrees = 0f;
            state.Mode = PMMoverMode.Walking;
            state.Grounded = true;
            state.GroundNormal = PMVector3.Up;
            state.Scale = PMMoverDefaults.DefaultScale;
            state.MaxSpeed = PMMoverDefaults.DefaultMaxSpeed;
            state.Acceleration = PMMoverDefaults.DefaultAcceleration;
            state.Braking = PMMoverDefaults.DefaultBraking;
            state.GravityScale = PMMoverDefaults.DefaultGravityScale;
            state.JumpSpeed = PMMoverDefaults.DefaultJumpSpeed;
            state.ActiveLayers = null;
            return state;
        }
    }

    /// <summary>
    /// 很少变、逐帧透传的环境状态。
    ///
    /// 它**参与 reconcile**（契约「AuxGravity/版本变化参与 reconcile」）：
    /// 重力或碰撞世界版本一变，之前算出的预测就不再可信。
    ///
    /// <see cref="CollisionWorldVersion"/> 必须与当前
    /// <see cref="IPMMoverCollisionQuery.WorldVersion"/> 一致；本批**不**在模型里抛错
    /// （模型不该决定拒绝裁决），但宿主/R4-B 接线必须保证一致，否则碰撞结果不可比。
    /// </summary>
    public struct PMMoverAuxState
    {
        /// <summary>世界重力（米/秒²，Y 向下为负）。默认见 <see cref="PMMoverDefaults.DefaultGravityY"/>。</summary>
        public PMVector3 Gravity;

        /// <summary>碰撞世界版本（对应 <see cref="IPMMoverCollisionQuery.WorldVersion"/>）。</summary>
        public int CollisionWorldVersion;

        /// <summary>运动参数配置版本（配置一变即需 reconcile）。</summary>
        public int ConfigVersion;

        public static PMMoverAuxState CreateDefault()
        {
            PMMoverAuxState aux = new PMMoverAuxState();
            aux.Gravity = new PMVector3(0f, PMMoverDefaults.DefaultGravityY, 0f);
            aux.CollisionWorldVersion = 0;
            aux.ConfigVersion = 0;
            return aux;
        }
    }

    /// <summary>
    /// 模型内所有可调常量与阈值的**唯一**来源。
    ///
    /// 阈值的量纲与取值来自契约「位置阈值 0.05m（对应源 5cm）、速度阈值 0.01m/s、朝向阈值 1 度」；
    /// 运动参数默认值里有实测依据的只有 MaxSpeed（项目冻结移速 3.9 m/s，见
    /// <c>Shared/BattleNumericConfig</c> 的 MoveSpeed），其余是**首批占位值**，
    /// 需后续按玩法实测收敛（报告"未决项"已逐条列出）。
    /// </summary>
    public static class PMMoverDefaults
    {
        // ---- 运动参数默认值 ----

        /// <summary>世界重力 Y 分量（米/秒²）。契约默认值。</summary>
        public const float DefaultGravityY = -9.81f;

        /// <summary>默认最大水平速度（米/秒）。取自项目冻结移速 3.9f（服务端原常量 / 多数英雄配置）。</summary>
        public const float DefaultMaxSpeed = 3.9f;

        /// <summary>默认加速度（米/秒²）。**占位值，未实测。**</summary>
        public const float DefaultAcceleration = 20f;

        /// <summary>默认制动减速度（米/秒²）。**占位值，未实测。**</summary>
        public const float DefaultBraking = 20f;

        /// <summary>默认重力缩放倍数。</summary>
        public const float DefaultGravityScale = 1f;

        /// <summary>默认起跳竖直速度（米/秒）。**占位值，未实测**（旧项目没有竖直轴）。</summary>
        public const float DefaultJumpSpeed = 4f;

        /// <summary>默认缩放倍数。</summary>
        public const float DefaultScale = 1f;

        // ---- 胶囊尺寸（占位值，需按玩法实测/配置收敛）----

        /// <summary>胶囊半径（米），缩放前。**占位值。**</summary>
        public const float CapsuleRadiusMeters = 0.4f;

        /// <summary>胶囊半高（米），缩放前。与 AGENTS「玩家 Y=1」一致。</summary>
        public const float CapsuleHalfHeightMeters = 1f;

        // ---- 地面/落地探测量 ----

        /// <summary>Walking/Flying 判定"是否贴地"时向下探的距离（米）。**占位值。**</summary>
        public const float GroundProbeMeters = 0.05f;

        /// <summary>落地查询的额外余量（米），用于吸收浮点误差，避免"刚好接触"被判成未落地。</summary>
        public const float LandingProbeEpsilonMeters = 1e-4f;

        // ---- 扫掠/滑动 ----

        /// <summary>
        /// 接触安全余量（米）。两个用途：
        ///   · 模型滑动时沿法线推出这么远，避免下一次扫掠在零距离上重复命中同一面；
        ///   · 测试替身用它收缩膨胀盒，避免"正好贴面"被当成"重叠"。
        /// </summary>
        public const float CollisionSkinMeters = 0.001f;

        /// <summary>滑动求解的最大迭代次数（超出后丢弃剩余位移——宁可少走，也绝不穿墙）。</summary>
        public const int MaxSlideIterations = 4;

        /// <summary>单帧内允许的模式切换次数上限（防御性保护，见 PMMoverModel.StepModes）。</summary>
        public const int MaxModeTransitionsPerStep = 4;

        // ---- 一步 dt 的合法范围（契约：整毫秒 int，范围 1..50）----

        /// <summary>单步最小毫秒数。</summary>
        public const int MinStepMs = 1;

        /// <summary>单步最大毫秒数。</summary>
        public const int MaxStepMs = 50;

        // ---- reconcile 阈值（契约冻结值）----

        /// <summary>位置差异阈值（米）。</summary>
        public const float PositionToleranceMeters = 0.05f;

        /// <summary>速度差异阈值（米/秒）。</summary>
        public const float VelocityToleranceMetersPerSecond = 0.01f;

        /// <summary>朝向差异阈值（度，最短弧）。</summary>
        public const float YawToleranceDegrees = 1f;

        /// <summary>地面法线夹角阈值（度）。**缺口说明**：本批自定，契约只列了位置/速度/朝向三项。</summary>
        public const float GroundNormalToleranceDegrees = 1f;

        /// <summary>重力分量差异阈值（米/秒²）。**缺口说明**：本批自定。</summary>
        public const float GravityToleranceMetersPerSecondSquared = 1e-4f;
    }
}
