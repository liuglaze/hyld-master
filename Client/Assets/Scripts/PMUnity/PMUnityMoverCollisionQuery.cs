// ============================================================================
//  PMUnityMoverCollisionQuery —— 真实 Unity PhysX 的 IPMMoverCollisionQuery 适配器
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-network-contract.md 的 B2 一节。逐条对应关系：
//    · 「实现 IPMMoverCollisionQuery，构造 (int layerMask, int worldVersion)，
//       可选 Collider[] allowlist 重载（宿主把新建测试地板/墙传入隔离旧场景；
//       不能让旧地图、表现胶囊或其它局参与查询）」 → 三个公开构造函数 + 白名单过滤；
//    · 「胶囊中心/半高/半径遵守接口」 → position 是胶囊中心，半高含两端半球；
//    · 「主线程约束」 → 构造时记录线程 ID，任何跨线程查询显式失败；
//    · 「查询忽略 trigger」 → 一律 QueryTriggerInteraction.Ignore（不读也不写全局 queriesHitTriggers）；
//    · 「最近阻挡、有限值、零 delta、起点接触/重叠、足底有符号地距处理有证据」
//       → 见 Sweep / QueryGround 的分支与被 Client/Assets/Editor/PMR4UnityValidation.cs 逐项断言的路径；
//    · 「查询缓冲固定上限、饱和显式失败而非静默忽略墙」 → HitBufferCapacity 满即抛异常；
//    · 「禁止移动 Transform、Physics.Simulate、设全局 autoSimulation 或修改旧场景」
//       → 本文件不出现 Transform 写入 / Simulate / autoSimulation / autoSyncTransforms；
//    · 「构建/每帧开始的 Physics.SyncTransforms 由宿主单次做，不能每条 resim 扫描同步」
//       → 本适配器**永不**调用 Physics.SyncTransforms()，宿主负责（见类注释"宿主前置条件"）。
//
//  ----------------------------------------------------------------------------
//  零距离命中语义（本文件最关键的一处：2026-09-20 真实 Play 失败的根因与修法）
//  ----------------------------------------------------------------------------
//  实测事实（Unity 2019.4 + PhysX，真实 Play 报告见 Docs/plans/_r4b_physx_zero_hit_fix.md）：
//    胶囊**正好**贴地（足底 Y == 地面顶 Y）时，任意方向的 CapsuleCast 都会返回该地面的
//    一条命中，其 `distance == 0` 且 `normal == -dir`（起点已接触/重叠的标记，法线与
//    真实接触面（地面是 +Y）无关）。真实 Play 里 (3,1,0)→(-3,0,0) 拿到
//    `fraction=0 normal=(1,0,0)`、(10,1,0)→(0,0,6) 拿到 `fraction=0 normal=(0,0,-1)`、
//    嵌墙 (0,1,0)→(0,0,1) 也拿到 `fraction=0 normal=(0,0,-1)` —— 三者都是同一条语义。
//
//  为什么必须区分（修复前的缺陷）：`-dir` **恒**满足"法线与运动方向相反"这条朴素判据，
//  于是贴地时水平/竖直向上的扫掠全被判 fraction=0：角色原地走不动、起跳位移被抵消、
//  真正的墙 TOI（2.1/3=0.7）被 0 距离假命中抢走，且 Cast 分支先返回使真正的"起点嵌墙"
//  路径永不执行（StartOverlapResolutions 恒为 0）。
//
//  本文件的处理（三分法，全部只做**查询**，不创建/移动任何 Unity 对象）：
//    1) **有效前向 TOI**：`distance > RawZeroDistanceEpsilon` ⇒ 原始法线可信，
//       仍要求 `dot(normal, dir) ≤ −ContactOppositionEpsilon`（掠射面不计入前进量），命中取最近；
//    2) **起点正穿透**：`OverlapCapsule` + 几何分类（见"精确性边界"）
//       ⇒ `Blocking=true / Fraction=0 / Normal=<有效推出方向>`，并计入 StartOverlapResolutions；
//    3) **仅接触 / contactOffset 级假命中**：`distance ≈ 0` 的候选**一律不采信原始法线**，
//       改用几何分类得到的特征法线再判"是否与运动方向相对"：
//         相对 ⇒ 作为 `Fraction = max(0, gap)/|delta|` 的保守阻挡（绝不晚于真实接触点）；
//         不相对 ⇒ 忽略（记入 ZeroDistanceContactsIgnored：脚下的地面/身后的墙/平行掠射面）。
//
//  **精确性边界（明确记录，不假装通用）**：几何分类用 `Collider.bounds`（世界 AABB）。
//    · 轴对齐 Box（冻结场景 PMR3TestScene 与 Editor 验证的地板/墙都是 identity 旋转的
//      BoxCollider）上，bounds 就是几何精确的 AABB ⇒ 分离量、面/棱/角特征法线、
//      最小重叠轴都是**精确**结论；
//    · 其它形状（旋转 Box / Mesh / Terrain / Sphere…）上 bounds 只是**超集**近似：
//      因为「形状 ⊆ bounds」，所以 `distance(段, bounds) ≤ distance(段, 形状)`，于是
//        「判为穿透/接触」可能偏早（保守：最多多挡一点、提前贴住），
//        「判为不穿透」则一定不穿透（**绝不漏墙**）；
//        推出方向把胶囊推出 bounds，也就一定推离了形状（方向有效，但不承诺"最小"）。
//      ⇒ 保守方向固定为"宁可早挡、绝不漏墙"，**不**声称通用形状的精确 MTD。
//      落在非 BoxCollider 上的分类次数计入 NonBoxBoundsClassifications 供宿主/报告审计。
//
//  不做尺寸收缩（query skin）：收缩半径会让胶囊在窄缝里"挤过去"（等于漏墙），且需要补偿
//  距离与法线；本文件改用几何分类，不需要任何尺寸补偿。棱角/掠射面的**原始法线**仍来自 PhysX，
//  因此"擦着棱角掠过"最多产生一帧过冲——下一帧的几何穿透分支会把它纠正回来，不存在穿透通过。
//  该边界同样登记在报告里。
//
//  API 真实性：下列成员已对 Unity 2019.4.8f1 的真实程序集核实（Cecil 反射 + 官方 XML 文档），
//  并由 Tools/PMR4UnityCheck 引用 D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine 下的
//  **真实 DLL** 做 netstandard2.0 + C#7.3 编译；不依赖任何手写 Unity 桩件：
//    · UnityEngine.PhysicsScene.CapsuleCast(Vector3 point1, Vector3 point2, float radius,
//        Vector3 direction, RaycastHit[] results, float maxDistance, int layerMask,
//        QueryTriggerInteraction) → int
//    · UnityEngine.PhysicsScene.OverlapCapsule(Vector3 point0, Vector3 point1, float radius,
//        Collider[] results, int layerMask, QueryTriggerInteraction) → int
//    · UnityEngine.PhysicsScene.IsValid() → bool（**方法**，不是属性）
//    · UnityEngine.Physics.defaultPhysicsScene → PhysicsScene
//    · UnityEngine.PhysicsSceneExtensions.GetPhysicsScene(this Scene) → PhysicsScene
//    · UnityEngine.Collider.bounds → Bounds；UnityEngine.BoxCollider（仅用于"是否 Box"计数）
//    · UnityEngine.QueryTriggerInteraction.Ignore
//
//  宿主前置条件（本适配器不替宿主做）：
//    1) 每帧（或每次批量 resim 之前）由宿主调用一次 `Physics.SyncTransforms()`，
//       把 Transform 改动提交给 PhysX。本适配器按"世界已是权威几何"来查询。
//    2) allowlist 非 null 时，只有白名单内的 Collider 参与判定（与 layerMask 是"与"关系）。
//       白名单预期很小（测试场景的地板/墙），匹配是线性扫描：O(命中数 × 白名单长度)。
//
//  明确不做（诚实边界）：
//    · 不声称与 UE PhysX/其它引擎逐位等价；本适配器只保证"用真实 Unity 查询实现接口语义"。
//    · 不做 trigger、不做 CharacterController、不做移动平台/斜坡台阶（属后续批次）。
//    · 不缓存/不记忆跨帧状态：同一个世界在同一个 Thread 上查询，结果只取决于传入参数与
//      PhysX 世界（例外：内部命中缓冲数组会被复用，因此**不重入**）。
// ============================================================================

using System;
using System.Threading;
using PMNet.Mover;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 用真实 Unity 物理（<see cref="PhysicsScene"/> 查询族）实现
    /// <see cref="IPMMoverCollisionQuery"/> 的适配器。
    ///
    /// 生命周期：构造成本 = 4 个固定长度缓冲数组；查询期零分配。
    /// 线程：只允许在构造它的那个线程（主线程）上查询。
    /// 副作用：查询期不改 Transform、不 Simulate、不碰全局物理开关、不建/删 GameObject。
    /// </summary>
    public sealed class PMUnityMoverCollisionQuery : IPMMoverCollisionQuery
    {
        /// <summary>
        /// 单次查询可容纳的命中数上限。**满即显式失败**（契约：饱和必须显式失败，
        /// 不能静默忽略墙 —— 因为 PhysX 的批量查询不保证返回最近的那个）。
        /// </summary>
        public const int HitBufferCapacity = 32;

        /// <summary>
        /// “接触必须与运动方向相对”的判定阈值（法线与方向的点积上界）。
        ///
        /// 几何上：法线与运动方向垂直（或同向）的接触面不可能减少前进量 —— 只有法线与运动方向
        /// 相反的接触才真正阻挡。因此过滤掉"不相对"的命中不会少算前进量，反而避免把
        /// "脚下的地面 / 身后的墙 / 平行掠射面"当成阻挡。
        ///
        /// 阈值取 1e-3（≈0.06°）而不是更小的值，理由是两个方向的**风险不对称**：
        ///   · 阈值太小（例如 1e-7）⇒ 浮点噪声让"脚下的地面"带出一点点迎向运动的法线分量，
        ///     就会被当成阻挡，Fraction≈0 ⇒ 角色**走不动**（严重、且表现为"卡住"难归因）；
        ///   · 阈值略大 ⇒ 最多忽略一个"几乎与运动平行"的掠射面，代价是这一帧往回贴入
        ///     ≤ 1e-3 × |delta|（亚毫米量级），下一帧的扫掠/重叠解算会把它顶出来（自愈）。
        /// 所以这里刻意选偏保守的一侧。该取值属"待实测收敛项"，已在报告中登记。
        /// </summary>
        public const float ContactOppositionEpsilon = 1e-3f;

        /// <summary>
        /// 判定"起点**正穿透**"的几何深度阈值（米）。
        ///
        /// 取值 1e-4（0.1 mm）的依据（三个量级都差得很开，不存在"恰好卡在阈值上"的现实风险）：
        ///   · 精确贴地（足底 Y == 地面顶 Y）时，本文件的几何分类算出 gap = −2.98e-8
        ///     （纯 float 往返噪声），比阈值小 3 个数量级 ⇒ 绝不会把"正好贴地"误判成穿透；
        ///   · PhysX 默认 contactOffset = 0.01，是阈值的 100 倍 ⇒ "contactOffset 级假重叠"
        ///     也不会被误判成穿透；
        ///   · 模型滑动用的 CollisionSkinMeters = 0.001，是阈值的 10 倍 ⇒ 模型自己推出的
        ///     1 mm 安全间隙不会被判成穿透。
        /// 阈值以下（−1e-4 &lt; gap ≤ 0）按"仅接触"处理：既不冻结移动，也不假装穿透。
        /// </summary>
        public const float PenetrationToleranceMeters = 1e-4f;

        /// <summary>
        /// 判定"原始命中是零距离（起点接触/重叠）报告"的阈值（米）。
        /// PhysX 对起点接触/重叠给的是**精确 0**（真实 Play 实测），留 1e-5 只为吸收 float 往返噪声；
        /// 比它更远的正距离 TOI 一律视为可信的前向命中，不做任何"缩小尺寸"补偿。
        /// </summary>
        public const float RawZeroDistanceEpsilon = 1e-5f;

        /// <summary>判定"层无时长/零长度"等退化情形时使用的极小量（与 <see cref="PMVector3.Epsilon"/> 同值）。</summary>
        private const float ZeroEpsilon = PMVector3.Epsilon;

        private readonly PhysicsScene _scene;
        private readonly int _layerMask;
        private readonly int _worldVersion;
        private readonly Collider[] _allowlist;
        private readonly int _mainThreadId;

        // 查询缓冲（固定长度、复用）。Sweep 与 QueryGround 各自独立，避免互相踩。
        private readonly RaycastHit[] _sweepHits = new RaycastHit[HitBufferCapacity];
        private readonly RaycastHit[] _groundHits = new RaycastHit[HitBufferCapacity];
        private readonly Collider[] _overlapHits = new Collider[HitBufferCapacity];

        // 诊断计数（只增不减；宿主/R4-B 报告可用它证明"哪些路径真的跑到了"）。
        private int _sweepQueries;
        private int _groundQueries;
        private int _startOverlapResolutions;
        private int _penetratingGroundHits;
        private int _saturationFailures;
        private int _unresolvableOverlaps;
        private int _nonOpposingHitsIgnored;
        private int _zeroDistanceCandidates;
        private int _zeroDistanceContactsAccepted;
        private int _zeroDistanceContactsIgnored;
        private int _zeroDistanceCastPenetrations;
        private int _nonBoxBoundsClassifications;

        /// <summary>
        /// 用**默认物理场景**构造（对应 <see cref="Physics.defaultPhysicsScene"/>）。
        /// 不传白名单 ⇒ 该 layerMask 上的**所有** Collider 都参与查询。
        /// </summary>
        public PMUnityMoverCollisionQuery(int layerMask, int worldVersion)
            : this(Physics.defaultPhysicsScene, layerMask, worldVersion, null)
        {
        }

        /// <summary>
        /// 用**默认物理场景** + Collider 白名单构造。
        /// 白名单用于把查询隔离到宿主新建的测试地板/墙，避免旧地图、表现胶囊或其它局参与
        /// （契约 B2 原文）。传 null 或空数组等同于"不限制"。
        /// </summary>
        public PMUnityMoverCollisionQuery(int layerMask, int worldVersion, Collider[] allowlist)
            : this(Physics.defaultPhysicsScene, layerMask, worldVersion, allowlist)
        {
        }

        /// <summary>
        /// 完整构造：指定物理场景（真实宿主用默认场景；Editor 验证用临时 localPhysicsScene 隔离）。
        /// </summary>
        /// <param name="scene">查询目标物理场景，必须有效。</param>
        /// <param name="layerMask">图层掩码；为 0 时不会命中任何 Collider（显式行为，不做"猜默认值"回退）。</param>
        /// <param name="worldVersion">碰撞世界版本，进 <see cref="WorldVersion"/> 并被预测层的 AuxState 比较。</param>
        /// <param name="allowlist">可选 Collider 白名单；null/空 = 不限制。构造时做防御性拷贝。</param>
        public PMUnityMoverCollisionQuery(PhysicsScene scene, int layerMask, int worldVersion, Collider[] allowlist)
        {
            if (!scene.IsValid())
            {
                throw new ArgumentException(
                    "PMUnityMoverCollisionQuery: PhysicsScene 无效（IsValid()==false）。"
                    + "构造前必须确认场景已创建且带 LocalPhysicsMode.Physics3D。", "scene");
            }

            _scene = scene;
            _layerMask = layerMask;
            _worldVersion = worldVersion;
            _allowlist = CopyAllowlist(allowlist);
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>碰撞世界版本（契约：进 AuxState 并参与 reconcile）。</summary>
        public int WorldVersion { get { return _worldVersion; } }

        /// <summary>构造时绑定的物理场景（诊断用）。</summary>
        public PhysicsScene Scene { get { return _scene; } }

        /// <summary>图层掩码（诊断用）。</summary>
        public int LayerMask { get { return _layerMask; } }

        /// <summary>白名单长度；0 表示"不限制"（诊断用）。</summary>
        public int AllowlistCount { get { return _allowlist == null ? 0 : _allowlist.Length; } }

        /// <summary>查询是否被限制在 Collider 白名单内。</summary>
        public bool HasAllowlist { get { return _allowlist != null; } }

        /// <summary>当前构造线程（主线程）的托管线程 ID（诊断用）。</summary>
        public int MainThreadId { get { return _mainThreadId; } }

        // ================================================================================
        //  IPMMoverCollisionQuery
        // ================================================================================

        /// <summary>
        /// 胶囊沿 <paramref name="delta"/> 扫掠，返回最早接触。
        ///
        /// 分支与语义（每一条都对应契约里点名要求"有证据"的路径）：
        ///   1) 零 delta（|delta| ≤ 1e-6）⇒ 不阻挡（Fraction=1、Normal=零向量）。零位移不是错误输入；
        ///   2) 前向扫掠：`distance > RawZeroDistanceEpsilon` 的命中是可信 TOI，取**最近**的
        ///      "与运动方向相对"的那条（不是首命中）；
        ///   3) `distance ≈ 0` 的命中是 PhysX 的"起点接触/重叠"报告（法线恒为 −dir，**不可信**）：
        ///      先由 `OverlapCapsule` + 几何分类判定有没有**正穿透** ⇒ 有则
        ///      `Blocking=true / Fraction=0 / Normal=<有效推出方向>`；没有则按几何法线判"是否相对"，
        ///      相对才是 `Fraction≈0` 的接触阻挡，不相对（脚下的地面等）一律忽略。
        /// 细节与"精确性边界"见 <see cref="ScanCast"/> / <see cref="TryResolveStartPenetration"/>。
        /// </summary>
        public PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight)
        {
            RequireMainThread();
            RequireFinite(position, "position");
            RequireFinite(delta, "delta");
            RequireShape(radius, halfHeight);

            _sweepQueries++;

            PMMoverHit hit = new PMMoverHit();
            hit.Blocking = false;
            hit.Fraction = 1f;
            hit.Normal = PMVector3.Zero;

            float distance = delta.Length;
            if (distance <= ZeroEpsilon)
            {
                // 零 delta：不阻挡（契约点名的"零 delta"路径）。
                return hit;
            }

            Vector3 center = ToUnity(position);
            float offset = SphereOffset(radius, halfHeight);
            Vector3 p1 = center + Vector3.up * offset;
            Vector3 p2 = center - Vector3.up * offset;
            Vector3 dir = ToUnity(delta) * (1f / distance);

            bool found;
            float forwardDistance;
            Vector3 castNormal;
            bool hasZeroDistanceCandidate;
            bool hasCastPenetration;
            Vector3 castPenetrationNormal;
            float castPenetrationDepth;

            ScanCast(p1, p2, radius, dir, distance, _sweepHits,
                     out found, out forwardDistance, out castNormal,
                     out hasZeroDistanceCandidate, out hasCastPenetration,
                     out castPenetrationNormal, out castPenetrationDepth);

            if (hasZeroDistanceCandidate || hasCastPenetration)
            {
                // 起点几何穿透是权威判定：原始 cast 命中的 distance=0 无法区分"穿透/仅接触"，
                // 而 OverlapCapsule 的候选集包含所有"接触或在内"的 Collider。
                Vector3 pushOut;
                float depth;
                if (TryResolveStartPenetration(p1, p2, radius, out pushOut, out depth))
                {
                    _startOverlapResolutions++;
                    hit.Blocking = true;
                    hit.Fraction = 0f;
                    hit.Normal = ToPM(pushOut);
                    return hit;
                }
            }

            if (hasCastPenetration)
            {
                // 防御分支：几何分类在 cast 候选上发现了穿透，但 OverlapCapsule 没有（正常不该发生）。
                // fail-closed：仍按"起点正穿透"处理，绝不因为两处结论不一致而放过穿透。
                _zeroDistanceCastPenetrations++;
                hit.Blocking = true;
                hit.Fraction = 0f;
                hit.Normal = NormalOrDefault(castPenetrationNormal);
                return hit;
            }

            if (found)
            {
                hit.Blocking = true;
                hit.Fraction = Clamp01(forwardDistance / distance);
                hit.Normal = NormalOrDefault(castNormal);
            }

            return hit;
        }

        /// <summary>
        /// 从胶囊**足底**向下 <paramref name="distance"/> 米找支撑面，返回**有符号**距离。
        ///
        /// 分支顺序（顺序本身是有意的，见下方理由）：
        ///   1) 先用 <see cref="PhysicsScene.OverlapCapsule"/> 做“支撑面重叠测量”：
        ///      若存在“顶面位于胶囊中心以下、且 XZ 覆盖胶囊足迹”的白名单 Collider，
        ///      则 signed = footY − top；**signed ≤ 0（接触/穿透）时直接返回它**；
        ///   2) 否则（悬空）用 <see cref="PhysicsScene.CapsuleCast"/> 向下探 distance，
        ///      命中即 Distance = 命中沿 -Y 的距离 ≥ 0（悬空间隙）；
        ///   3) 两者都不成立 ⇒ Found=false、Distance=0、Normal=零向量（接口约定）。
        ///
        /// 为什么先做重叠测量：PhysX 对“起始已重叠”的扫掠返回什么（命中 t=0 / 直接不报）
        /// 不属于可依赖的接口语义，如果先扫掠、再拿“距离 0”当结果，就会把 0.3 m 的穿透
        /// 报成 0。重叠测量是**确定**的：footY − top 直接给出有符号地距。
        /// 悬空时（signed > 0）才用扫掠取精确间隙——那正是扫掠可靠的区间。
        ///
        /// 不用“抬高再下探”的单一写法：抬高的起点若仍与墙/地板重叠，PhysX 会给出
        /// 距离 0 的命中，从而把水平穿透误判成"头顶有很深的支撑面"。方法 1 的
        /// “顶面必须在中心以下”判据正好把墙排除掉（墙顶 y=2 > 胶囊中心 y=1）。
        /// </summary>
        public PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance)
        {
            RequireMainThread();
            RequireFinite(position, "position");
            RequireShape(radius, halfHeight);
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
            {
                throw new ArgumentOutOfRangeException("distance",
                    "QueryGround: 向下探测距离必须是有限非负值，实际 " + distance.ToString("R"));
            }

            _groundQueries++;

            PMMoverGround ground = new PMMoverGround();
            ground.Found = false;
            ground.Distance = 0f;
            ground.Normal = PMVector3.Zero;

            Vector3 center = ToUnity(position);
            float offset = SphereOffset(radius, halfHeight);
            Vector3 p1 = center + Vector3.up * offset;
            Vector3 p2 = center - Vector3.up * offset;

            // 分支 1：重叠/接触 → 有符号地距（负 = 穿透，0 = 接触）。
            float signedDistance;
            if (TryMeasureSupportPenetration(center, radius, halfHeight, out signedDistance)
                && signedDistance <= 0f)
            {
                _penetratingGroundHits++;
                ground.Found = true;
                ground.Distance = signedDistance;
                ground.Normal = PMVector3.Up;
                return ground;
            }

            // 分支 2：悬空 → 扫掠取精确间隙（扫掠在“起点未重叠”时是可靠的）。
            if (distance > 0f)
            {
                bool found;
                float forwardDistance;
                Vector3 normal;
                bool hasZeroDistanceCandidate;
                bool hasCastPenetration;
                Vector3 castPenetrationNormal;
                float castPenetrationDepth;

                ScanCast(p1, p2, radius, Vector3.down, distance, _groundHits,
                         out found, out forwardDistance, out normal,
                         out hasZeroDistanceCandidate, out hasCastPenetration,
                         out castPenetrationNormal, out castPenetrationDepth);

                if (found)
                {
                    ground.Found = true;
                    ground.Distance = forwardDistance;
                    ground.Normal = NormalOrDefault(normal);
                    return ground;
                }
            }

            // 分支 3：无支撑（接口约定：Found=false 时 Distance=0、Normal=零向量）。
            return ground;
        }

        /// <summary>
        /// 诊断计数快照。用于宿主/R4-B 报告证明"哪些分支真的被执行过"，
        /// 以及 Editor 验证里断言"起点重叠路径被走到"。
        /// </summary>
        public PMUnityCollisionQueryStats GetStats()
        {
            PMUnityCollisionQueryStats stats = new PMUnityCollisionQueryStats();
            stats.SweepQueries = _sweepQueries;
            stats.GroundQueries = _groundQueries;
            stats.StartOverlapResolutions = _startOverlapResolutions;
            stats.PenetratingGroundHits = _penetratingGroundHits;
            stats.SaturationFailures = _saturationFailures;
            stats.UnresolvableOverlaps = _unresolvableOverlaps;
            stats.NonOpposingHitsIgnored = _nonOpposingHitsIgnored;
            stats.ZeroDistanceCandidates = _zeroDistanceCandidates;
            stats.ZeroDistanceContactsAccepted = _zeroDistanceContactsAccepted;
            stats.ZeroDistanceContactsIgnored = _zeroDistanceContactsIgnored;
            stats.ZeroDistanceCastPenetrations = _zeroDistanceCastPenetrations;
            stats.NonBoxBoundsClassifications = _nonBoxBoundsClassifications;
            return stats;
        }

        // ================================================================================
        //  内部实现
        // ================================================================================

        /// <summary>
        /// 批量扫掠 + 白名单过滤 + 命中分类（"有效前向 TOI / 起点穿透 / 仅接触"三分）。
        ///
        /// 为什么不能只看原始法线：`distance ≈ 0` 是 PhysX 的"起点已接触/重叠"报告，
        /// 实测其法线恒为 **−dir**（与真实接触面无关）。−dir 恒满足"法线与方向相对"，
        /// 因此必须换成**几何分类**得到的特征法线，否则脚下地面会被当成阻挡（角色走不动）。
        ///
        /// 输出 <paramref name="forwardDistance"/> 是"沿扫掠方向到接触点的距离"：
        ///   · 正 TOI ⇒ 就是 PhysX 的 hit.distance；
        ///   · 仅接触候选 ⇒ max(0, gap)（**保守**：真实接触点只会更远，绝不更近）。
        /// 饱和（返回数 == 容量）时**显式失败**：PhysX 的批量查询不保证包含最近的那个，
        /// 静默取"碰巧拿到的"就等于漏掉真正的墙。
        /// </summary>
        private void ScanCast(Vector3 p1, Vector3 p2, float radius, Vector3 dir, float totalDistance,
                              RaycastHit[] buffer,
                              out bool found, out float forwardDistance, out Vector3 normal,
                              out bool hasZeroDistanceCandidate, out bool hasPenetration,
                              out Vector3 penetrationNormal, out float penetrationDepth)
        {
            found = false;
            forwardDistance = 0f;
            normal = Vector3.zero;
            hasZeroDistanceCandidate = false;
            hasPenetration = false;
            penetrationNormal = Vector3.zero;
            penetrationDepth = 0f;

            int count = _scene.CapsuleCast(p1, p2, radius, dir, buffer, totalDistance,
                                           _layerMask, QueryTriggerInteraction.Ignore);
            RequireNotSaturated(count, "CapsuleCast");

            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = buffer[i];
                Collider collider = candidate.collider;
                if (collider == null) { continue; }
                if (!IsAllowed(collider)) { continue; }

                if (candidate.distance > RawZeroDistanceEpsilon)
                {
                    // (1) 有效前向 TOI：原始法线可信。
                    if (!IsOpposing(candidate.normal, dir))
                    {
                        _nonOpposingHitsIgnored++;
                        continue;
                    }

                    ConsiderHit(candidate.distance, candidate.normal,
                                ref found, ref forwardDistance, ref normal);
                    continue;
                }

                // (2) distance ≈ 0：PhysX 的"起点接触/重叠"报告，原始法线**不可信**。
                _zeroDistanceCandidates++;
                hasZeroDistanceCandidate = true;

                if (!(collider is BoxCollider))
                {
                    // 非 Box 形状：bounds 只是超集近似（保守：宁可早挡、绝不漏墙）。计数以便审计。
                    _nonBoxBoundsClassifications++;
                }

                float gap;
                Vector3 geometricNormal;
                MeasureCapsuleBounds(collider, p1, p2, radius, out gap, out geometricNormal);

                if (gap < -PenetrationToleranceMeters)
                {
                    // 起点正穿透：由 Sweep 走"Fraction=0 + 有效推出方向"的权威分支。
                    float depth = -gap;
                    if (!hasPenetration || depth > penetrationDepth)
                    {
                        hasPenetration = true;
                        penetrationDepth = depth;
                        penetrationNormal = geometricNormal;
                    }

                    continue;
                }

                if (!IsOpposing(geometricNormal, dir))
                {
                    // 几何法线与运动方向不相对：脚下的地面 / 身后的墙 / 平行掠射面 ⇒ 不阻挡。
                    _zeroDistanceContactsIgnored++;
                    continue;
                }

                // 仅接触（或极小间隙）：作为保守的小 fraction 阻挡（绝不晚于真实接触点）。
                float contactGap = gap > 0f ? gap : 0f;
                _zeroDistanceContactsAccepted++;
                ConsiderHit(contactGap, geometricNormal, ref found, ref forwardDistance, ref normal);
            }
        }

        /// <summary>命中择优：只保留"沿扫掠方向最近"的那条（并列时保留先见者，结果确定）。</summary>
        private static void ConsiderHit(float candidateDistance, Vector3 candidateNormal,
                                       ref bool found, ref float forwardDistance, ref Vector3 normal)
        {
            if (found && candidateDistance >= forwardDistance)
            {
                return;
            }

            found = true;
            forwardDistance = candidateDistance;
            normal = candidateNormal;
        }

        /// <summary>
        /// 起点"正穿透"的权威判定：<see cref="PhysicsScene.OverlapCapsule"/> + 几何分类。
        ///
        /// 为什么 Overlap 是权威：PhysX 的 overlap 语义是"touching **or** inside"，
        /// 因此它的候选集**包含**所有真正重叠的 Collider（即使某次 CapsuleCast 没有把它报出来）。
        /// 相对地，原始 cast 命中的 `distance=0` 在"穿透"与"仅接触"两个方向上都不可信。
        ///
        /// 返回 true 时 <paramref name="pushOut"/> 是背离障碍物、长度 1 的**有效推出方向**
        /// （见 <see cref="MeasureCapsuleBounds"/>：分离 &gt; 0 时是面/棱/角特征法线；
        /// 分离 == 0 时是 bounds 最小重叠轴 —— 两者都能把胶囊推出去）。
        /// 多个 Collider 同时穿透时取**最深**者；深度并列时保留 PhysX 返回顺序中的第一个
        /// （该路径只用于决定推出方向，且本身是退化路径——已登记）。
        /// </summary>
        private bool TryResolveStartPenetration(Vector3 p1, Vector3 p2, float radius,
                                                out Vector3 pushOut, out float depth)
        {
            pushOut = Vector3.zero;
            depth = 0f;

            int count = _scene.OverlapCapsule(p1, p2, radius, _overlapHits,
                                              _layerMask, QueryTriggerInteraction.Ignore);
            RequireNotSaturated(count, "OverlapCapsule");

            float bestDepth = PenetrationToleranceMeters;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlapHits[i];
                if (collider == null) { continue; }
                if (!IsAllowed(collider)) { continue; }

                if (!(collider is BoxCollider))
                {
                    _nonBoxBoundsClassifications++;
                }

                float gap;
                Vector3 geometricNormal;
                MeasureCapsuleBounds(collider, p1, p2, radius, out gap, out geometricNormal);

                float candidateDepth = -gap;
                if (candidateDepth <= bestDepth)
                {
                    continue;
                }

                bestDepth = candidateDepth;
                depth = candidateDepth;
                pushOut = geometricNormal;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// **竖直胶囊 vs Collider.bounds** 的几何测量（本文件里给出"可信几何法线"的地方）。
        ///
        /// 推导（轴对齐盒上是**精确**的；对其它形状是**保守**的，见下）：胶囊轴是竖直线段
        /// `x = segX, z = segZ, y ∈ [segMinY, segMaxY]`；任一世界 AABB 都是三个区间的笛卡尔积，
        /// 因此"线段到 AABB 的距离"在三个轴上**可分离**：
        ///     separation = sqrt(dx² + dy² + dz²)
        /// 其中 dx/dz 是 segX/segZ 到 AABB X/Z 区间的间隙，dy 是段 Y 区间到 AABB Y 区间的间隙。
        /// 于是：
        ///   · gap = separation − radius（&gt;0 间隙、≈0 接触、&lt;0 穿透）；
        ///   · separation &gt; 0 时特征法线 = 各轴间隙正交合成的单位向量（面/棱/角都对）；
        ///   · separation == 0（线段已进入 AABB）时退化为"胶囊 AABB ∩ 目标 AABB 的最小重叠轴"
        ///     —— 它不一定是**最小**平移量，但一定是**有效**的分离方向（把胶囊 AABB 推出目标
        ///     AABB，就必然把胶囊推出该 AABB 内的任何形状）。模型的滑动/推出只用方向，因此足够。
        ///
        /// 精确 vs 保守（必须写清楚，不允许被读成"对任意形状精确"）：
        ///   目标 = 形状，且 形状 ⊆ bounds ⇒ distance(段, bounds) ≤ distance(段, 形状) ⇒
        ///     gap_bounds ≤ gap_shape。因此：
        ///       「gap_bounds ≥ −阈值」⇒ 「gap_shape ≥ −阈值」：判"不穿透"是**可靠**的（绝不漏墙）；
        ///       「gap_bounds &lt; −阈值」可能只是 bounds 比形状大（旋转盒/网格的角外侧）⇒ 保守地
        ///       提前阻挡，方向仍然有效。轴对齐 Box 上 bounds == 形状，两者重合 ⇒ 结论精确。
        /// </summary>
        private void MeasureCapsuleBounds(Collider collider, Vector3 segA, Vector3 segB, float radius,
                                          out float gap, out Vector3 normal)
        {
            gap = 0f;
            normal = Vector3.zero;

            Bounds bounds = collider.bounds;

            float segX = segA.x; // 胶囊轴竖直：两端球心的 X/Z 相同
            float segZ = segA.z;
            float segMinY = segA.y < segB.y ? segA.y : segB.y;
            float segMaxY = segA.y > segB.y ? segA.y : segB.y;

            float dx = AxisGap(segX, bounds.min.x, bounds.max.x);
            float dy = IntervalGap(segMinY, segMaxY, bounds.min.y, bounds.max.y);
            float dz = AxisGap(segZ, bounds.min.z, bounds.max.z);

            float separation = (float)Math.Sqrt((double)(dx * dx + dy * dy + dz * dz));
            gap = separation - radius;

            if (separation > ZeroEpsilon)
            {
                float signX = segX < bounds.min.x ? -1f : (segX > bounds.max.x ? 1f : 0f);
                float signY = segMaxY < bounds.min.y ? -1f : (segMinY > bounds.max.y ? 1f : 0f);
                float signZ = segZ < bounds.min.z ? -1f : (segZ > bounds.max.z ? 1f : 0f);

                // 各轴间隙正交 ⇒ 合成向量长度恒等于 separation，直接归一化即可（无除零风险）。
                normal = new Vector3(signX * dx, signY * dy, signZ * dz) * (1f / separation);
                return;
            }

            // 线段已进入 AABB：用"胶囊世界 AABB ∩ 目标 AABB"的最小重叠轴作为保守推出方向。
            // 轴遍历顺序固定（X→Y→Z）且用严格小于比较，因此并列时结果仍然确定。
            float capMinX = segX - radius;
            float capMaxX = segX + radius;
            float capMinY = segMinY - radius;
            float capMaxY = segMaxY + radius;
            float capMinZ = segZ - radius;
            float capMaxZ = segZ + radius;

            float overlapX = Overlap1D(capMinX, capMaxX, bounds.min.x, bounds.max.x);
            float overlapY = Overlap1D(capMinY, capMaxY, bounds.min.y, bounds.max.y);
            float overlapZ = Overlap1D(capMinZ, capMaxZ, bounds.min.z, bounds.max.z);

            if (overlapX <= 0f || overlapY <= 0f || overlapZ <= 0f)
            {
                // 防御：分离为 0 时三轴必然都有正重叠（段在 AABB 内 ⇒ 胶囊 AABB ⊇ 段）。
                // 真的出现几何退化就 fail closed（返回无方向），绝不给一个可能朝内的方向。
                _unresolvableOverlaps++;
                return;
            }

            int axis = 0;
            float bestOverlap = overlapX;
            if (overlapY < bestOverlap) { axis = 1; bestOverlap = overlapY; }
            if (overlapZ < bestOverlap) { axis = 2; bestOverlap = overlapZ; }

            // 方向 = 背离障碍物中心的一侧（并列时取正向，保证确定性）。
            switch (axis)
            {
                case 0:
                    normal = new Vector3(segX < bounds.center.x ? -1f : 1f, 0f, 0f);
                    return;

                case 1:
                    normal = new Vector3(0f, (segMinY + segMaxY) * 0.5f < bounds.center.y ? -1f : 1f, 0f);
                    return;

                default:
                    normal = new Vector3(0f, 0f, segZ < bounds.center.z ? -1f : 1f);
                    return;
            }
        }

        /// <summary>点到一个区间的一维间隙（在区间内为 0）。</summary>
        private static float AxisGap(float value, float lo, float hi)
        {
            if (value < lo) { return lo - value; }
            if (value > hi) { return value - hi; }
            return 0f;
        }

        /// <summary>两个一维区间之间的间隙（相交时为 0）。</summary>
        private static float IntervalGap(float aMin, float aMax, float bMin, float bMax)
        {
            if (aMax < bMin) { return bMin - aMax; }
            if (bMax < aMin) { return aMin - bMax; }
            return 0f;
        }

        /// <summary>接触面法线是否与运动方向相对（见 <see cref="ContactOppositionEpsilon"/> 的说明）。</summary>
        private static bool IsOpposing(Vector3 normal, Vector3 dir)
        {
            float dot = normal.x * dir.x + normal.y * dir.y + normal.z * dir.z;
            return dot <= -ContactOppositionEpsilon;
        }

        /// <summary>
        /// 测量“足底相对支撑面”的有符号地距（见 <see cref="QueryGround"/> 分支 1）。
        ///
        /// 支撑面判据（三条同时成立，缺一不可）：
        ///   · Collider 的世界 AABB 顶面 max.y **不高于**胶囊中心（排除墙这类"头顶结构"）；
        ///   · 该顶面在 XZ 上与胶囊足迹 [中心 ± radius] 有交集；
        ///   · 结果是所有候选里**最高**的顶面（离足底最近的那个）。
        /// 返回 false 表示当前姿态下没有可判定的支撑面。返回 true 时 signed 可能为正（悬空）。
        /// </summary>
        private bool TryMeasureSupportPenetration(Vector3 center, float radius, float halfHeight,
                                                  out float signedDistance)
        {
            signedDistance = 0f;

            float offset = SphereOffset(radius, halfHeight);
            Vector3 p1 = center + Vector3.up * offset;
            Vector3 p2 = center - Vector3.up * offset;

            int count = _scene.OverlapCapsule(p1, p2, radius, _overlapHits,
                                              _layerMask, QueryTriggerInteraction.Ignore);
            RequireNotSaturated(count, "OverlapCapsule");

            float footY = center.y - halfHeight;
            float bestTop = float.MinValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlapHits[i];
                if (collider == null) { continue; }
                if (!IsAllowed(collider)) { continue; }

                Bounds bounds = collider.bounds;

                if (bounds.max.y > center.y) { continue; }

                if (bounds.max.x < center.x - radius || bounds.min.x > center.x + radius) { continue; }
                if (bounds.max.z < center.z - radius || bounds.min.z > center.z + radius) { continue; }

                if (bounds.max.y > bestTop)
                {
                    bestTop = bounds.max.y;
                    found = true;
                }
            }

            if (!found)
            {
                return false;
            }

            signedDistance = footY - bestTop;
            return true;
        }

        private bool IsAllowed(Collider collider)
        {
            if (_allowlist == null)
            {
                return true;
            }

            for (int i = 0; i < _allowlist.Length; i++)
            {
                if (ReferenceEquals(_allowlist[i], collider))
                {
                    return true;
                }
            }

            return false;
        }

        private void RequireNotSaturated(int count, string what)
        {
            if (count < HitBufferCapacity)
            {
                return;
            }

            _saturationFailures++;
            throw new InvalidOperationException(
                "PMUnityMoverCollisionQuery: " + what + " 命中缓冲饱和（count=" + count
                + "，容量=" + HitBufferCapacity + "）。契约要求饱和显式失败，绝不静默忽略墙："
                + "请收紧 layerMask/allowlist，或提高 HitBufferCapacity。");
        }

        private void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "PMUnityMoverCollisionQuery: 查询必须在构造它的主线程上执行（构造线程 "
                    + _mainThreadId + "，当前 " + Thread.CurrentThread.ManagedThreadId + "）。");
            }
        }

        private static void RequireFinite(PMVector3 v, string name)
        {
            if (!v.IsFinite)
            {
                throw new ArgumentException(
                    "PMUnityMoverCollisionQuery: " + name + " 必须全部分量有限，实际 " + v, name);
            }
        }

        private static void RequireShape(float radius, float halfHeight)
        {
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                throw new ArgumentOutOfRangeException("radius",
                    "PMUnityMoverCollisionQuery: 胶囊半径必须为有限正值，实际 " + radius.ToString("R"));
            }

            if (float.IsNaN(halfHeight) || float.IsInfinity(halfHeight) || halfHeight <= 0f)
            {
                throw new ArgumentOutOfRangeException("halfHeight",
                    "PMUnityMoverCollisionQuery: 胶囊半高必须为有限正值，实际 " + halfHeight.ToString("R"));
            }

            if (halfHeight < radius - ZeroEpsilon)
            {
                throw new ArgumentOutOfRangeException("halfHeight",
                    "PMUnityMoverCollisionQuery: 半高 " + halfHeight.ToString("R")
                    + " 小于半径 " + radius.ToString("R") + "，Geometry 退化（Unity 胶囊要求 height >= 2*radius）。");
            }
        }

        /// <summary>胶囊两端球心相对中心的偏移量：halfHeight − radius（半高含半球）。</summary>
        private static float SphereOffset(float radius, float halfHeight)
        {
            float offset = halfHeight - radius;
            return offset > 0f ? offset : 0f;
        }

        private static float Overlap1D(float aMin, float aMax, float bMin, float bMax)
        {
            float lo = aMin > bMin ? aMin : bMin;
            float hi = aMax < bMax ? aMax : bMax;
            return hi - lo;
        }

        private static float Clamp01(float v)
        {
            if (v <= 0f) { return 0f; }
            if (v >= 1f) { return 1f; }
            return v;
        }

        private static PMVector3 NormalOrDefault(Vector3 normal)
        {
            PMVector3 result = ToPM(normal);
            if (!result.IsFinite || result.LengthSquared <= ZeroEpsilon)
            {
                // 退化法线：模型会自行判退化并结束滑动，这里不猜一个"合理方向"。
                return PMVector3.Zero;
            }

            return result;
        }

        private static Collider[] CopyAllowlist(Collider[] allowlist)
        {
            if (allowlist == null || allowlist.Length == 0)
            {
                return null;
            }

            int valid = 0;
            for (int i = 0; i < allowlist.Length; i++)
            {
                if (allowlist[i] != null) { valid++; }
            }

            if (valid == 0)
            {
                return null;
            }

            Collider[] copy = new Collider[valid];
            int index = 0;
            for (int i = 0; i < allowlist.Length; i++)
            {
                if (allowlist[i] != null)
                {
                    copy[index] = allowlist[i];
                    index++;
                }
            }

            return copy;
        }

        private static Vector3 ToUnity(PMVector3 v)
        {
            return new Vector3(v.X, v.Y, v.Z);
        }

        private static PMVector3 ToPM(Vector3 v)
        {
            return new PMVector3(v.x, v.y, v.z);
        }
    }

    /// <summary>
    /// <see cref="PMUnityMoverCollisionQuery"/> 的诊断计数快照（值类型，可自由传递）。
    /// 这些计数只增不减，仅用于"证明某条分支真的被执行过"，不承载任何权威语义。
    /// </summary>
    public struct PMUnityCollisionQueryStats
    {
        /// <summary><see cref="PMUnityMoverCollisionQuery.Sweep"/> 调用次数。</summary>
        public int SweepQueries;

        /// <summary><see cref="PMUnityMoverCollisionQuery.QueryGround"/> 调用次数。</summary>
        public int GroundQueries;

        /// <summary>
        /// 起点**正穿透**被权威判定并给出有效推出方向的次数（Fraction=0 分支）。
        /// 修复前该计数恒为 0：Cast 分支先返回，真正的起点重叠路径永不执行。
        /// </summary>
        public int StartOverlapResolutions;

        /// <summary>地面查询返回负距离（足底穿入支撑面）的次数。</summary>
        public int PenetratingGroundHits;

        /// <summary>饱和失败次数（抛出前计数）。</summary>
        public int SaturationFailures;

        /// <summary>起点重叠但无法给出推出方向的次数（几何退化；fail closed，返回无方向）。</summary>
        public int UnresolvableOverlaps;

        /// <summary>因“法线不与运动方向相对”而被忽略的命中数（例如水平扫掠时脚下的地面）。</summary>
        public int NonOpposingHitsIgnored;

        /// <summary>收到的 `distance ≈ 0` 候选数（PhysX 的"起点接触/重叠"报告，原始法线不可信）。</summary>
        public int ZeroDistanceCandidates;

        /// <summary>`distance ≈ 0` 候选经几何分类后判为"仅接触且与运动方向相对"而接受的次数。</summary>
        public int ZeroDistanceContactsAccepted;

        /// <summary>
        /// `distance ≈ 0` 候选经几何分类后判为"不相对"而忽略的次数。
        /// **修复后贴地行走必然 &gt; 0**：脚下的地面正是这一类（几何法线 +Y 与水平运动垂直）。
        /// </summary>
        public int ZeroDistanceContactsIgnored;

        /// <summary>
        /// 几何分类在 cast 候选上发现穿透、但 OverlapCapsule 未报的防御分支次数
        /// （正常应为 0；非 0 说明两处结论不一致，但两端都按 fail-closed 处理）。
        /// </summary>
        public int ZeroDistanceCastPenetrations;

        /// <summary>
        /// 落在**非 BoxCollider** 形状上的几何分类次数：这些形状的 bounds 只是超集近似，
        /// 结论是保守的（宁可早挡、绝不漏墙），不是精确 MTD。这是明确记录的支持边界。
        /// </summary>
        public int NonBoxBoundsClassifications;

        public override string ToString()
        {
            return "sweep=" + SweepQueries + " ground=" + GroundQueries
                   + " startOverlap=" + StartOverlapResolutions
                   + " penetratingGround=" + PenetratingGroundHits
                   + " saturationFailures=" + SaturationFailures
                   + " unresolvableOverlaps=" + UnresolvableOverlaps
                   + " nonOpposingIgnored=" + NonOpposingHitsIgnored
                   + " zeroDistanceCandidates=" + ZeroDistanceCandidates
                   + " zeroDistanceContactsAccepted=" + ZeroDistanceContactsAccepted
                   + " zeroDistanceContactsIgnored=" + ZeroDistanceContactsIgnored
                   + " zeroDistanceCastPenetrations=" + ZeroDistanceCastPenetrations
                   + " nonBoxBoundsClassifications=" + NonBoxBoundsClassifications;
        }
    }
}
