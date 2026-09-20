// ============================================================================
//  PMMover 碰撞查询接口（R4-A / M08）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-prediction-contract.md 的
//    「碰撞接口 IPMMoverCollisionQuery { int WorldVersion{get;}
//       PMMoverHit Sweep(position, delta, radius, halfHeight);
//       PMMoverGround QueryGround(position, radius, halfHeight, distance); }」
//    「hit 含 Blocking/Fraction/Normal；Ground 含 Found/Distance/Normal；
//      足底与中心明确，角色中心 Y = halfHeight 落地」
//
//  这是一个**纯 C#** 接口：不出现 UnityEngine.Vector3 / Collider / Physics，
//  也不出现 UE 的 FVector。这样模型才能在 netstandard2.0 门禁下被零依赖编译，
//  并且"确定性测试替身"与"真实引擎适配器"可以互换。
//
//  当前实现者：
//    · PMMoverTestWorld  —— 确定性 AABB 替身，**仅测试**，不声称与 Unity PhysX 等价；
//    · Unity 适配器（包 Physics.CapsuleCast 等）—— 属 R4-B 接线，本批不存在，
//      状态 PENDING_USER（本机无 Unity 可验证）。
// ============================================================================

namespace PMNet.Mover
{
    /// <summary>
    /// 一次扫掠（sweep）的结果。
    ///
    /// 约定（实现者必须遵守，模型依赖它做有界滑动）：
    ///   · <see cref="Blocking"/>=false 时，<see cref="Fraction"/>=1、<see cref="Normal"/>=零向量；
    ///   · <see cref="Fraction"/> 是"允许移动的比例"，范围 [0,1]；0 表示起点已重叠；
    ///   · <see cref="Normal"/> 是**障碍物在该接触点朝外**的单位法线（背离障碍物）。
    ///     滑动求解用它的符号决定推出方向，因此实现者不得返回朝向障碍物内部的反向法线。
    /// </summary>
    public struct PMMoverHit
    {
        /// <summary>是否发生阻挡。</summary>
        public bool Blocking;

        /// <summary>允许移动的比例（0..1）。</summary>
        public float Fraction;

        /// <summary>接触点处背离障碍物的单位法线；未阻挡时为零向量。</summary>
        public PMVector3 Normal;

        public override string ToString()
        {
            return "Hit(blocking=" + Blocking + " fraction=" + Fraction + " normal=" + Normal + ")";
        }
    }

    /// <summary>
    /// 一次地面查询的结果。
    ///
    /// 约定：
    ///   · <see cref="Distance"/> 从**足底**（<c>position.Y - halfHeight</c>）量到支撑面顶部的
    ///     **有符号**距离：正=悬空间隙，0=正好接触，负=已经穿透。
    ///     模型用 <c>position.Y -= Distance</c> 把角色放到支撑面上（穿透时为向上顶出）。
    ///   · <paramref name="distance"/> 参数是向下探测的最大距离；只有
    ///     <c>Distance &lt;= distance</c> 的支撑面才算 Found。
    ///   · <see cref="Found"/>=false 时 <see cref="Distance"/>=0、<see cref="Normal"/>=零向量。
    /// </summary>
    public struct PMMoverGround
    {
        /// <summary>是否找到支撑面。</summary>
        public bool Found;

        /// <summary>足底到支撑面顶部的有符号距离（米）。</summary>
        public float Distance;

        /// <summary>支撑面法线（平地 = <see cref="PMVector3.Up"/>）；未找到时为零向量。</summary>
        public PMVector3 Normal;

        public override string ToString()
        {
            return "Ground(found=" + Found + " distance=" + Distance + " normal=" + Normal + ")";
        }
    }

    /// <summary>
    /// 运动模型需要的最小碰撞查询面。纯数据进出，**不允许**任何副作用
    /// （实现者不得在查询里移动角色、改世界、记日志刷盘）。
    ///
    /// 为什么是"模型构造时注入"：<c>IPMPredictionModel.Simulate(step, input, start, aux)</c>
    /// 没有查询参数——模型必须自己持有查询对象。注入点只有构造函数一处，
    /// 因此"用哪个碰撞世界"是完全显式、可替换的。
    /// </summary>
    public interface IPMMoverCollisionQuery
    {
        /// <summary>
        /// 碰撞世界版本。进 AuxState 并参与 reconcile：
        /// 世界一变，之前的预测一律不可信。
        /// </summary>
        int WorldVersion { get; }

        /// <summary>
        /// 胶囊体沿 <paramref name="delta"/> 扫掠，返回最早接触。
        /// <paramref name="position"/> 是**胶囊中心**；<paramref name="radius"/>/<paramref name="halfHeight"/>
        /// 是当前生效尺寸（已含 Scale 缩放），由调用方算好传进来。
        /// </summary>
        PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight);

        /// <summary>
        /// 从 <paramref name="position"/> 的足底向下 <paramref name="distance"/> 米找支撑面。
        /// 与 <see cref="Sweep"/> 共用同一套几何（"复用同一地面查询"）。
        /// </summary>
        PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance);
    }
}
