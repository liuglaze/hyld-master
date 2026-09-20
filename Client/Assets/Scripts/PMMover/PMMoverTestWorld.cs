// ============================================================================
//  PMMoverTestWorld —— 确定性碰撞替身（R4-A / M08）
// ============================================================================
//
//  ⚠️ 这是**测试替身**，不是物理引擎适配器，也不是"简化版 PhysX"：
//    · 它只做 AABB 求交（射线 vs 膨胀盒 + 圆心足迹的地面查询），没有任何形状旋转、
//      没有连续/离散混合求解、没有穿透回退、没有材质/摩擦/通道。
//    · 它与 R3 已冻结的固定测试场景**数值对齐**（地板中心 (0,-0.5,0) 尺寸 (40,1,40)；
//      墙中心 (0,1,0) 尺寸 (1,2,8)；版本号沿用 PMR3Runtime.CollisionDigest 的字节值），
//      目的是"让 R4-A 的数学有可比对的环境"，**不声称**它等价于 Unity PhysX，
//      也**不声称**它能代表任何真实地图。
//    · Unity 侧（Physics.CapsuleCast / SweepTest → PMVector3）的适配器属 R4-B 接线，
//      本机无 Unity 可验证，状态 PENDING_USER。
//
//  为什么替身放在 Client/Assets/Scripts/PMMover/ 而不是测试工程里：
//    模型必须在**同源**几何下被验证（Unity 与 net8 门禁各编一份会引入"两份几何"），
//    且 PMMoverCoreCheck 门禁要把它一起编译进来验证 netstandard2.0/C#7.3 兼容。
//    正因如此，本文件必须保持零 UnityEngine 依赖。
//
//  几何口径（与 IPMMoverCollisionQuery 注释一致）：
//    · Sweep：把障碍盒按胶囊尺寸**膨胀**，再做射线 vs 盒（slab 法）；
//      膨胀量先减去 1mm 安全余量 —— 否则"正好贴面站立"会被判成"起点重叠"，
//      水平移动会被脚下的地板整段吃掉。
//    · QueryGround：XZ 平面"圆心足迹 vs 盒"求水平重叠，Y 只比"盒顶 vs 足底"的有符号间隙；
//      盒顶高出足底超过 SupportMaxAboveFeetMeters 的（例如旁边的墙）**不算支撑面**。
// ============================================================================

using System;

namespace PMNet.Mover
{
    /// <summary>轴对齐盒（世界空间，已展开为 min/max）。仅供 <see cref="PMMoverTestWorld"/> 使用。</summary>
    public struct PMMoverTestBox
    {
        public PMVector3 Min;
        public PMVector3 Max;

        public PMMoverTestBox(PMVector3 min, PMVector3 max)
        {
            Min = min;
            Max = max;
        }

        public override string ToString()
        {
            return "Box[" + Min + " .. " + Max + "]";
        }
    }

    /// <summary>
    /// R4-A 的确定性碰撞环境：一块 40×40 的地板 + 一面 1×2×8 的墙。
    ///
    /// 它是 <see cref="IPMMoverCollisionQuery"/> 的真实实现（不是 mock），
    /// 因此 PMMoverModel 在门禁里跑的是"真几何 + 真滑动"，而不是被桩住的空实现。
    /// 但它**只**用于测试/门禁，正式玩法必须换成 R4-B 的 Unity 适配器。
    /// </summary>
    public sealed class PMMoverTestWorld : IPMMoverCollisionQuery
    {
        /// <summary>
        /// 本替身几何的版本号。字节值与 <c>PMNet.R3.PMR3Runtime.CollisionDigest</c> 相同
        /// （0x52334201），因为两者描述的是同一套冻结测试布局。
        ///
        /// 这里**不复用** PMR3Runtime 的常量：那会把 M08 拖进 PMNet.R3/PMNet.Session 的依赖面
        /// （与"只依赖 Contracts + Identity"的边界冲突）。两边同值是**约定**，
        /// 由 R4-B 接线时的一致性检查负责守护，不由编译期保证。
        /// </summary>
        public const uint FrozenCollisionWorldVersion = 0x52334201u;

        /// <summary>地板中心（世界坐标）。与 R3 冻结场景一致。</summary>
        public static PMVector3 FloorCenter { get { return new PMVector3(0f, -0.5f, 0f); } }

        /// <summary>地板尺寸。与 R3 冻结场景一致。</summary>
        public static PMVector3 FloorSize { get { return new PMVector3(40f, 1f, 40f); } }

        /// <summary>墙中心（世界坐标）。与 R3 冻结场景一致。</summary>
        public static PMVector3 WallCenter { get { return new PMVector3(0f, 1f, 0f); } }

        /// <summary>墙尺寸。与 R3 冻结场景一致。</summary>
        public static PMVector3 WallSize { get { return new PMVector3(1f, 2f, 8f); } }

        /// <summary>
        /// 支撑面允许高出足底的最大值（米）。超过它的（例如紧贴身旁的墙）不是"脚下的地面"，
        /// 否则穿墙瞬间会把角色"顶"到墙顶上去。
        /// </summary>
        public const float SupportMaxAboveFeetMeters = 0.1f;

        /// <summary>slab 求交时判定"射线与该轴平行"的阈值。</summary>
        private const float ParallelEpsilon = 1e-9f;

        private readonly PMMoverTestBox[] _boxes;

        public PMMoverTestWorld()
        {
            _boxes = new PMMoverTestBox[2];
            _boxes[0] = MakeBox(FloorCenter, FloorSize);
            _boxes[1] = MakeBox(WallCenter, WallSize);
        }

        /// <summary>碰撞世界版本（进 AuxState 并参与 reconcile）。</summary>
        public int WorldVersion { get { return (int)FrozenCollisionWorldVersion; } }

        /// <summary>盒体个数（测试断言用）。</summary>
        public int BoxCount { get { return _boxes.Length; } }

        /// <summary>按索引取盒（只读；测试用它算手算预期值）。</summary>
        public PMMoverTestBox GetBox(int index)
        {
            if (index < 0 || index >= _boxes.Length)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            return _boxes[index];
        }

        /// <summary>地板盒索引。</summary>
        public const int FloorBoxIndex = 0;

        /// <summary>墙盒索引。</summary>
        public const int WallBoxIndex = 1;

        private static PMMoverTestBox MakeBox(PMVector3 center, PMVector3 size)
        {
            PMVector3 min = new PMVector3(
                center.X - size.X * 0.5f,
                center.Y - size.Y * 0.5f,
                center.Z - size.Z * 0.5f);
            PMVector3 max = new PMVector3(
                center.X + size.X * 0.5f,
                center.Y + size.Y * 0.5f,
                center.Z + size.Z * 0.5f);
            return new PMMoverTestBox(min, max);
        }

        // =================================================================================
        //  扫掠
        // =================================================================================

        public PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight)
        {
            if (!position.IsFinite || !delta.IsFinite)
            {
                throw new ArgumentException("[PMMoverTestWorld] Sweep 收到非有限输入。");
            }

            if (!(radius >= 0f) || !(halfHeight >= 0f))
            {
                throw new ArgumentException("[PMMoverTestWorld] Sweep 的半径/半高必须非负。");
            }

            // 膨胀量先扣掉安全余量：避免"正好贴面"被当成"起点重叠"。
            float expandXZ = Math.Max(0f, radius - PMMoverDefaults.CollisionSkinMeters);
            float expandY = Math.Max(0f, halfHeight - PMMoverDefaults.CollisionSkinMeters);

            PMMoverHit best = new PMMoverHit();
            best.Blocking = false;
            best.Fraction = 1f;
            best.Normal = PMVector3.Zero;

            for (int i = 0; i < _boxes.Length; i++)
            {
                PMMoverTestBox box = _boxes[i];
                PMVector3 boxMin = new PMVector3(
                    box.Min.X - expandXZ, box.Min.Y - expandY, box.Min.Z - expandXZ);
                PMVector3 boxMax = new PMVector3(
                    box.Max.X + expandXZ, box.Max.Y + expandY, box.Max.Z + expandXZ);

                float fraction;
                PMVector3 normal;
                if (!RayBox(position, delta, boxMin, boxMax, out fraction, out normal))
                {
                    continue;
                }

                // 取最早接触。相等时保留先到的盒（盒顺序固定 → 结果确定）。
                if (!best.Blocking || fraction < best.Fraction)
                {
                    best.Blocking = true;
                    best.Fraction = fraction;
                    best.Normal = normal;
                }
            }

            return best;
        }

        /// <summary>
        /// 射线 vs 轴对齐盒（slab 法）。返回是否相交，并给出比例 [0,1] 与**朝外**的接触法线。
        /// 起点已在盒内时返回 Fraction=0 与"最小推出"方向的法线（不猜、不除零）。
        /// </summary>
        private static bool RayBox(PMVector3 origin, PMVector3 direction,
                                   PMVector3 boxMin, PMVector3 boxMax,
                                   out float fraction, out PMVector3 normal)
        {
            fraction = 0f;
            normal = PMVector3.Zero;

            float tMin = 0f;
            float tMax = 1f;
            int axis = -1;
            float sign = 0f;

            if (!Slab(origin.X, direction.X, boxMin.X, boxMax.X, ref tMin, ref tMax, ref axis, ref sign, 0))
            {
                return false;
            }

            if (!Slab(origin.Y, direction.Y, boxMin.Y, boxMax.Y, ref tMin, ref tMax, ref axis, ref sign, 1))
            {
                return false;
            }

            if (!Slab(origin.Z, direction.Z, boxMin.Z, boxMax.Z, ref tMin, ref tMax, ref axis, ref sign, 2))
            {
                return false;
            }

            if (tMin > tMax)
            {
                return false;
            }

            if (axis < 0)
            {
                // 起点已在（膨胀后的）盒内：给一个最小推出方向，Fraction=0。
                fraction = 0f;
                normal = PushOutNormal(origin, boxMin, boxMax);
                return true;
            }

            fraction = tMin;
            normal = AxisNormal(axis, sign);
            return true;
        }

        /// <summary>单个轴的 slab 裁剪；返回 false 表示该轴已排除相交。</summary>
        private static bool Slab(float origin, float direction, float boxMin, float boxMax,
                                 ref float tMin, ref float tMax, ref int axis, ref float sign, int axisIndex)
        {
            if (direction > -ParallelEpsilon && direction < ParallelEpsilon)
            {
                // 平行于该轴：只要起点在区间内就继续（否则永不相交）。
                return origin >= boxMin && origin <= boxMax;
            }

            float inverse = 1f / direction;
            float t1 = (boxMin - origin) * inverse;
            float t2 = (boxMax - origin) * inverse;

            // 默认从 min 面进入 → 外法线为 -axis；交换后表示从 max 面进入 → +axis。
            float entrySign = -1f;
            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
                entrySign = 1f;
            }

            if (t1 > tMin)
            {
                tMin = t1;
                axis = axisIndex;
                sign = entrySign;
            }

            if (t2 < tMax)
            {
                tMax = t2;
            }

            return tMin <= tMax;
        }

        private static PMVector3 AxisNormal(int axis, float sign)
        {
            switch (axis)
            {
                case 0: return new PMVector3(sign, 0f, 0f);
                case 1: return new PMVector3(0f, sign, 0f);
                case 2: return new PMVector3(0f, 0f, sign);
                default: return PMVector3.Zero;
            }
        }

        /// <summary>起点在盒内时的最小推出方向（沿穿透最浅的轴）。</summary>
        private static PMVector3 PushOutNormal(PMVector3 origin, PMVector3 boxMin, PMVector3 boxMax)
        {
            float toMinX = origin.X - boxMin.X;
            float toMaxX = boxMax.X - origin.X;
            float toMinY = origin.Y - boxMin.Y;
            float toMaxY = boxMax.Y - origin.Y;
            float toMinZ = origin.Z - boxMin.Z;
            float toMaxZ = boxMax.Z - origin.Z;

            float depthX = Math.Min(toMinX, toMaxX);
            float depthY = Math.Min(toMinY, toMaxY);
            float depthZ = Math.Min(toMinZ, toMaxZ);

            if (depthX <= depthY && depthX <= depthZ)
            {
                return new PMVector3(toMinX <= toMaxX ? -1f : 1f, 0f, 0f);
            }

            if (depthY <= depthX && depthY <= depthZ)
            {
                return new PMVector3(0f, toMinY <= toMaxY ? -1f : 1f, 0f);
            }

            return new PMVector3(0f, 0f, toMinZ <= toMaxZ ? -1f : 1f);
        }

        // =================================================================================
        //  地面查询
        // =================================================================================

        public PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance)
        {
            if (!position.IsFinite)
            {
                throw new ArgumentException("[PMMoverTestWorld] QueryGround 收到非有限位置。");
            }

            if (!(radius >= 0f) || !(halfHeight >= 0f))
            {
                throw new ArgumentException("[PMMoverTestWorld] QueryGround 的半径/半高必须非负。");
            }

            // 负探测距离按 0 处理（"只看是否正好接触"）；不做静默的"往下多探一点"。
            float maxDistance = distance < 0f ? 0f : distance;
            float feetY = position.Y - halfHeight;
            float radiusSquared = radius * radius;

            PMMoverGround result = new PMMoverGround();
            result.Found = false;
            result.Distance = 0f;
            result.Normal = PMVector3.Zero;

            bool found = false;
            float bestTop = 0f;

            for (int i = 0; i < _boxes.Length; i++)
            {
                PMMoverTestBox box = _boxes[i];

                float closestX = Clamp(position.X, box.Min.X, box.Max.X);
                float closestZ = Clamp(position.Z, box.Min.Z, box.Max.Z);
                float dx = position.X - closestX;
                float dz = position.Z - closestZ;
                if (dx * dx + dz * dz > radiusSquared)
                {
                    continue;
                }

                float gap = feetY - box.Max.Y;

                // 高出足底太多的是"障碍"不是"地面"（否则贴墙站立会把墙顶当地面）。
                if (gap < -SupportMaxAboveFeetMeters)
                {
                    continue;
                }

                if (gap > maxDistance)
                {
                    continue;
                }

                // 支撑面取**最高**的那块（脚下的地板优先于更低的装饰面）。
                if (!found || box.Max.Y > bestTop)
                {
                    found = true;
                    bestTop = box.Max.Y;
                    result.Distance = gap;
                }
            }

            if (found)
            {
                result.Found = true;
                result.Normal = PMVector3.Up;
            }

            return result;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }
    }
}
