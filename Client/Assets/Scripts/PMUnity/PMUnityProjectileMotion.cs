// ============================================================================
//  PMUnityProjectileMotion —— R5-C1 独立适配：真实 Unity PhysX 的投射物运动 hook
// ============================================================================
//
//  契约来源（只读）：
//    · Docs/plans/net-r5-network-contract.md 末段「B2b/C适配可并行边界」的 C1 一节；
//    · Docs/plans/net-r5-projectile-contract.md（单位/时钟、运动 hook 语义）；
//    · Docs/plans/_r5_coordinator_review.md 的 §1.3「宿主运动 hook 失败必须 fail closed」
//      （本文件是该契约的 Unity 侧实现，不改整合器、不改 driver、不改任何宿主）。
//
//  C1 逐条落点：
//    · 「构造 (PhysicsScene physicsScene, Collider[] allowedColliders, int layerMask)」
//       → 唯一公开构造；只接受**独立有效**的 PhysicsScene。
//    · 「必须独立有效 PhysicsScene，主线程」 → 构造期拒绝 default physics scene 与无效场景；
//       查询期校验"构造线程 == 当前线程"，跨线程显式抛（整合器会 fail closed）。
//    · 「SphereCast/Overlap 在指定 physicsScene」 → 全部查询走 `_scene.*`，
//       本文件**不出现** `Physics.SphereCast` / `Physics.OverlapSphere` / `Physics.SyncTransforms` /
//       `Physics.queriesHitTriggers` / `Physics.autoSimulation`：默认物理世界零接触。
//    · 「忽略 trigger 与非白名单」 → 查询参数恒为 QueryTriggerInteraction.Ignore，
//       且对每个候选再做一次 `isTrigger` 与白名单（引用相等）过滤；两层都在。
//    · 「饱和明确失败不是无障碍」 → 命中缓冲 / 重叠缓冲返回数 == 容量 ⇒ 返回 false（fail closed），
//       绝不把"碰巧拿到的前 N 个"当成"没有墙"。
//    · 「半径取可信 spec」 → 半径只来自入参 `spec.RadiusM`（权威配置），本文件不从 state/其它字段猜。
//    · 「移动受阻返回 true + stop 位置 + 零 velocity」 → 见 TryStep 的受阻分支。
//    · 「TryStop 从该步命中记录返回 true」 → 每 PMProjectileKey 一条"本步停止标记"，
//       TryStop **消费**（读后即删）该标记；多颗弹交错调用不会互相串。
//    · 「出错返回 false 让 core fail closed」 → 入参非法（负/NaN 半径、非 finite 位置速度朝向）、
//       查询饱和、查询抛异常一律返回 false；整合器据此**停在原位**（= 整合器已实现并已测的
//       HostMotionFaulted 路径），不会退化成"接口失败 ⇒ 穿墙"。
//    · 「运动不做角色伤害 / 不反向引用网络 driver」 → 本文件不引用任何伤害、结算、网络类型；
//       只依赖 PMNet.Projectile 的 hook 接口与 PMNet.Mover 的 PMVector3。
//
//  ---------------------------------------------------------------------------
//  几何语义（本文件最关键的一处）
//  ---------------------------------------------------------------------------
//  【观测 vs 契约：措辞纪律】R4-B 的**一次**真实 Play 实测（Docs/plans/_r4b_physx_zero_hit_fix.md，
//  独立复核见 Docs/plans/_r5_unity_adapter_review.md 第 3 节）观察到：对"起点已接触/重叠"的
//  Collider，扫掠返回的**那一条**命中其 `distance == 0`，并且该条命中的 `normal` 为 `-dir`。
//  这**只是一次观测**，不是 PhysX 的接口契约：不得表述为"所有接触法线恒为 `-dir`"，
//  也不得据此推断"任何命中的法线都不可信"。本适配器因此**完全不读 `RaycastHit.normal`**
//  （既不筛、也不修、更不做法线推演），这个决定与上面的观测无关，理由如下：
//
//  投射物对"接触"只有一个合法反应 —— **停止**。所以只按**距离**取"沿线最早的可信阻挡"：
//      · 起点已经正重叠（OverlapSphere 权威判定）⇒ 不推进（停在当前真实位置）；
//      · 否则扫掠取所有候选里 `distance` 最小的那条（含 `distance == 0` 的接触）。
//        `distance == 0` 是**最早**，因此天然满足"零 TOI 不能随机跳过真正墙"——
//        既不忽略它（那样会漏墙），也不依赖其法线做任何判断。
//
//  不做**尺寸收缩**（query skin）：收缩半径会让弹在窄缝里挤过去（等于漏墙）。避免嵌入用的是
//  另一件事——命中后沿运动方向**回退** `ContactSkinMeters`（默认 1 mm），使球心停在"刚好不接触"
//  的位置，而不是精确贴着接触点（浮点噪声会让下一帧的 Overlap 判定变成"起点正穿透"）。
//  该回退是保守的（只会更早停，绝不更晚），且被钳在 `[0, 全程]` 内。
//
//  API 真实性：下列成员已对 Unity 2019.4.8f1 的真实程序集核实（UnityEngine.PhysicsModule.xml
//  的成员表 + Tools/PMR5UnityCheck 引用 D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine 下
//  的**真实 DLL** 做 netstandard2.0 + C#7.3 编译；不依赖任何手写 Unity 桩件）：
//    · UnityEngine.PhysicsScene.SphereCast(Vector3 origin, float radius, Vector3 direction,
//        RaycastHit[] results, float maxDistance, int layerMask, QueryTriggerInteraction) → int
//    · UnityEngine.PhysicsScene.OverlapSphere(Vector3 position, float radius, Collider[] results,
//        int layerMask, QueryTriggerInteraction) → int
//    · UnityEngine.PhysicsScene.IsValid() → bool（**方法**，不是属性）
//    · UnityEngine.Physics.defaultPhysicsScene → PhysicsScene
//    · UnityEngine.PhysicsSceneExtensions.GetPhysicsScene(this Scene) → PhysicsScene
//    · UnityEngine.Collider.isTrigger / enabled / gameObject.scene / layer
//    · UnityEngine.QueryTriggerInteraction.Ignore
//
//  宿主前置条件（本适配器不替宿主做）：
//    1) 查询前世界几何必须已提交给 PhysX（宿主在加载/搬移静态几何之后自行做一次同步；
//       本适配器**永不**调用 `Physics.SyncTransforms()`，因为它只覆盖默认物理世界）。
//    2) allowedColliders 是"这张地图的硬障碍"（PMUnityBattleMap.Colliders 就是现成的输入）。
//    3) 停止标记表是**有界**的，且满时**不会**淘汰已接受标记（只会在需要记账的步上显式失败）：
//       宿主应保证每个受阻步都被 TryStop 消费（正常整合器调用序已保证），并在对账/重连等
//       阶段调一次 <see cref="PMUnityProjectileMotion.Clear"/>；
//       若 <see cref="PMUnityProjectileMotionStats.StopMarkCapacityFailures"/> 出现非 0，
//       必须把这当作**宿主纪律告警**处理（详见 Docs/plans/_r5_unity_adapter_review.md）。
//
//  明确不做（诚实边界）：
//    · 不做弹跳/滑行/穿透厚度判定：接触即停（首批契约只有"停"）。
//    · 不声称与 UE PhysX 或其它引擎逐位等价；只保证"用真实 Unity 查询实现 hook 语义"。
//    · 不查骨骼、不做角色伤害、不生成结算：命中与伤害属整合器 + R6。
//    · 非 BoxCollider / 旋转几何由 PhysX 自己回答，本文件不做任何 bounds 近似推演
//      （因此也不引入"第三套几何引擎"）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using PMNet.Mover;
using PMNet.Projectile;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// 用真实 Unity 物理（<see cref="PhysicsScene"/> 查询族）实现
    /// <see cref="IPMProjectileHostMotion"/> 的适配器。
    ///
    /// 生命周期：构造成本 = 2 个固定长度缓冲 + 白名单拷贝；查询期零分配。
    /// 线程：只允许在构造它的那个线程（主线程）上查询。
    /// 副作用：查询期不改 Transform、不 Simulate、不碰全局物理开关、不建/删 GameObject、不读全局时钟。
    /// </summary>
    public sealed class PMUnityProjectileMotion : IPMProjectileHostMotion, IDisposable
    {
        /// <summary>
        /// 单次扫掠可容纳的命中数上限。**满即显式失败**（契约：饱和必须显式失败，不能静默忽略墙 ——
        /// PhysX 的批量查询不保证返回最近的那一个）。
        /// </summary>
        public const int HitBufferCapacity = 32;

        /// <summary>单次起点重叠查询可容纳的 Collider 数上限。**满即显式失败**（同上）。</summary>
        public const int OverlapBufferCapacity = 32;

        /// <summary>
        /// "本步停止标记"表的容量上限（条）。按 key 记账的表必须**有界**。
        ///
        /// **满时的语义（契约，不是实现细节）**：表满且本步确实需要记一条新标记（即本步受阻）时，
        /// <see cref="TryStep"/> **显式返回 false**（fail closed：整合器据此本步不推进、就地停止），
        /// 并计入 <see cref="PMUnityProjectileMotionStats.StopMarkCapacityFailures"/>。
        /// **绝不允许**为了塞进新标记而淘汰/覆盖**尚未被 <see cref="TryStop"/> 消费的**已接受标记 ——
        /// 那会把"这一步确实撞墙了"静默变成"TryStop 说没撞"，等价于漏墙。
        ///
        /// 清空通道只有两个显式入口：<see cref="Clear"/>（宿主在对账/重连准备阶段丢弃陈旧标记）
        /// 与 <see cref="Dispose"/>。
        ///
        /// 取值理由：正常链路上标记在同一个 AdvanceMotion 内就被 <see cref="TryStop"/> 消费，
        /// 表长约为"同帧在飞的弹数"（远小于 256）；只有"整合器在该步未走到 TryStop"
        /// （例如寿命到期/运动账本失败提前返回）才会留下陈旧标记。256 是防御上限，不是工作容量。
        /// </summary>
        public const int MaxStopMarkRecords = 256;

        /// <summary>
        /// 命中后沿运动方向回退的距离（米）。见文件头"不做尺寸收缩"一节：
        /// 它只用来避免球心精确贴在接触点上（浮点噪声会把下一帧变成"起点正穿透"），
        /// 因此必须远小于最小的可信几何尺度，又大于 float 往返噪声。
        /// 取 1e-3 与既有的 CollisionSkinMeters 同量级。
        /// </summary>
        public const float ContactSkinMeters = 1e-3f;

        /// <summary>判"没有位移"的阈值（与 <see cref="PMVector3.Epsilon"/> 同值）。</summary>
        private const float ZeroEpsilon = PMVector3.Epsilon;

        // ---------------------------------------------------------------- 冻结状态

        private readonly PhysicsScene _scene;
        private readonly int _layerMask;
        private readonly Collider[] _allowlist;
        private readonly int _mainThreadId;

        // 查询缓冲（固定长度、复用）。扫掠与起点重叠各自独立，避免互相踩。
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[HitBufferCapacity];
        private readonly Collider[] _overlapBuffer = new Collider[OverlapBufferCapacity];

        /// <summary>每 key 一条"本步被阻挡"的停止标记（位置）。</summary>
        private struct StopMark
        {
            public PMVector3 Position;
        }

        private readonly Dictionary<PMProjectileKey, StopMark> _stopMarks =
            new Dictionary<PMProjectileKey, StopMark>();

        // 诊断计数（只增不减；用于证明"哪些分支真的跑到了"，不承载任何权威语义）。
        private int _steps;
        private int _blockedSteps;
        private int _startOverlapBlocks;
        private int _saturationFailures;
        private int _rejectedInputs;
        private int _queryFailures;
        private int _consumedStopMarks;
        private int _stopMarkCapacityFailures;
        private int _forfeitedStopMarks;
        private int _disposedCalls;
        private bool _disposed;

        /// <summary>
        /// 构造：绑定一个**独立**物理场景 + 一张硬障碍白名单。
        /// </summary>
        /// <param name="physicsScene">
        /// 查询目标物理场景。必须是有效场景，且**不得**是 <see cref="Physics.defaultPhysicsScene"/>
        /// （契约：只允许独立有效 physics scene；拿默认世界当隔离世界会让旧大厅几何进入查询）。
        /// </param>
        /// <param name="allowedColliders">
        /// 硬障碍白名单（与 layerMask 是"与"关系）。**必填且不得为空**：
        /// 白名单为空时"允许一切"与"允许零个"都不是安全默认值（前者放进旧几何，后者让弹穿墙），
        /// 因此构造期显式失败。每一项都必须引用有效、且与该 PhysicsScene 属于同一物理世界。
        /// </param>
        /// <param name="layerMask">图层掩码；为 0 时不会命中任何 Collider（显式行为，不做"猜默认值"回退）。</param>
        public PMUnityProjectileMotion(PhysicsScene physicsScene, Collider[] allowedColliders, int layerMask)
        {
            if (!physicsScene.IsValid())
            {
                throw new ArgumentException(
                    "PMUnityProjectileMotion: PhysicsScene 无效（IsValid()==false）。"
                    + "构造前必须确认场景已创建且带 LocalPhysicsMode.Physics3D。", "physicsScene");
            }

            if (physicsScene.Equals(Physics.defaultPhysicsScene))
            {
                throw new ArgumentException(
                    "PMUnityProjectileMotion: 拒绝绑定**默认物理世界**（physicsScene == Physics.defaultPhysicsScene）。"
                    + "契约要求只允许独立有效 physics scene：默认世界会命中旧大厅/旧战斗的 Collider，"
                    + "而本适配器没有任何『这是旧对象』的判据。宿主应传入 PMUnityBattleMap.PhysicsScene。",
                    "physicsScene");
            }

            _scene = physicsScene;
            _layerMask = layerMask;
            _allowlist = CopyAndValidateAllowlist(allowedColliders, physicsScene);
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        // ---------------------------------------------------------------- 只读视图

        /// <summary>构造时绑定的物理场景（诊断用）。</summary>
        public PhysicsScene Scene { get { return _scene; } }

        /// <summary>图层掩码（诊断用）。</summary>
        public int LayerMask { get { return _layerMask; } }

        /// <summary>白名单长度（构造期已保证 &gt; 0）。</summary>
        public int AllowlistCount { get { return _allowlist.Length; } }

        /// <summary>当前构造线程（主线程）的托管线程 ID（诊断用）。</summary>
        public int MainThreadId { get { return _mainThreadId; } }

        /// <summary>
        /// 当前未被消费的停止标记条数（诊断/断言用；恒 &lt;= <see cref="MaxStopMarkRecords"/>）。
        /// 达到上限后，需要记账的步会显式失败（见 <see cref="TryStep"/>），而**不是**淘汰最旧。
        /// </summary>
        public int StopMarkCount { get { return _stopMarks.Count; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        /// <summary>诊断计数快照（值类型，可自由传递）。</summary>
        public PMUnityProjectileMotionStats GetStats()
        {
            PMUnityProjectileMotionStats stats = new PMUnityProjectileMotionStats();
            stats.Steps = _steps;
            stats.BlockedSteps = _blockedSteps;
            stats.StartOverlapBlocks = _startOverlapBlocks;
            stats.SaturationFailures = _saturationFailures;
            stats.RejectedInputs = _rejectedInputs;
            stats.QueryFailures = _queryFailures;
            stats.ConsumedStopMarks = _consumedStopMarks;
            stats.StopMarkCapacityFailures = _stopMarkCapacityFailures;
            stats.ForfeitedStopMarks = _forfeitedStopMarks;
            stats.DisposedCalls = _disposedCalls;
            stats.LiveStopMarks = _stopMarks.Count;
            return stats;
        }

        // ================================================================================
        //  IPMProjectileHostMotion
        // ================================================================================

        /// <summary>
        /// 计算一步运动（**场景阻挡的唯一来源**）。
        ///
        /// 三条出口：
        ///   1) **无阻挡** ⇒ `true` + 原样回填入参给的直线位置/速度/朝向（契约：想表达"本步无阻挡"
        ///      必须显式 true 并回填直线结果）；
        ///   2) **受阻** ⇒ `true` + 接触点位置 + **零 velocity** + 原飞行朝向，
        ///      并在本文件内按 key 记下"本步停止标记"（供随后的 <see cref="TryStop"/> 消费）；
        ///   3) **失败**（入参非法 / 查询缓冲饱和 / 查询异常 / 停止标记表满 / 已 Dispose / 非主线程）
        ///      ⇒ `false`。整合器对 false 的处置是**本步不推进 + 就地停止**（HostMotionFaulted），
        ///      因此返回 false 永远不会变成"接口失败 ⇒ 穿墙"。
        ///
        /// 特别地：**标记表满时只会在"本步确实需要记一条新标记"（即受阻）时才返回 false**；
        /// 无阻挡的步不受表满影响（不需要记账）。表满**绝不**触发淘汰/覆盖已接受的标记 ——
        /// 那会把"这步撞墙了"静默改成"TryStop 说没撞"（= 漏墙）。恢复通道是
        /// <see cref="Clear"/> / <see cref="Dispose"/>。
        ///
        /// 注意：`snapshot` 是整合器给出的**深 clone**，其 `Position` 是弹在账本里的真实当前位置；
        /// 扫掠一律从它出发（而不是从调用方给的直线终点反推）。
        /// </summary>
        public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot, int deltaMs,
            PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw)
        {
            // 失败时的安全回填：停在弹的当前真实位置（绝不给一个"往前飞"的默认值）。
            position = snapshot != null ? snapshot.Position : PMVector3.Zero;
            velocity = PMVector3.Zero;
            yaw = straightLineYaw;

            if (_disposed)
            {
                // 已释放：不查询、不记账（返回 false ⇒ 整合器就地停止）。
                _disposedCalls++;
                return false;
            }

            RequireMainThread();

            if (spec == null || snapshot == null)
            {
                _rejectedInputs++;
                return false;
            }

            // 半径只取**可信 spec**；拒绝 0/负/NaN/Inf（PhysX 对退化半径没有可定义语义）。
            float radius = spec.RadiusM;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                _rejectedInputs++;
                return false;
            }

            if (!snapshot.Position.IsFinite || !straightLinePosition.IsFinite
                || !straightLineVelocity.IsFinite || !IsFinite(straightLineYaw))
            {
                _rejectedInputs++;
                return false;
            }

            _steps++;

            // 本步开始时先作废该 key 的上一条标记：标记的语义恒为"最近一步的结果"，
            // 绝不让上一次（例如未被 TryStop 消费的）结果影响本次判定。
            ForfeitStopMark(key);

            PMVector3 start = snapshot.Position;
            PMVector3 delta = straightLinePosition - start;
            float distance = delta.Length;

            if (float.IsNaN(distance) || float.IsInfinity(distance))
            {
                _rejectedInputs++;
                return false;
            }

            if (distance <= ZeroEpsilon)
            {
                // 零位移不是错误输入（速度可能刚好为 0 或 dt=0）：不阻挡，原样回填直线结果。
                position = start;
                velocity = straightLineVelocity;
                yaw = straightLineYaw;
                return true;
            }

            Vector3 origin = ToUnity(start);
            Vector3 direction = new Vector3(delta.X / distance, delta.Y / distance, delta.Z / distance);

            try
            {
                // ---- 1) 起点正重叠（权威判定：OverlapSphere 的语义是 "touching or inside"）----
                bool saturated;
                if (HasStartOverlap(origin, radius, out saturated))
                {
                    _startOverlapBlocks++;
                    _blockedSteps++;

                    if (!TryRecordStopMark(key, start))
                    {
                        // 标记表已满且本步确实需要记一条：**不得淘汰/覆盖已接受的标记**，
                        // 只能显式失败（out 参数仍是开头写的安全值：停在 snapshot.Position）。
                        _stopMarkCapacityFailures++;
                        return false;
                    }

                    position = start;
                    velocity = PMVector3.Zero;
                    yaw = straightLineYaw;
                    return true;
                }

                if (saturated)
                {
                    // 重叠缓冲饱和：无法证明"起点没有重叠" ⇒ 显式失败（不能让弹从墙里飞出去）。
                    _saturationFailures++;
                    return false;
                }

                // ---- 2) 沿直线扫掠，取**沿线最早的可信阻挡**（只按距离，不按法线）----
                int count = _scene.SphereCast(origin, radius, direction, _hitBuffer, distance,
                                              _layerMask, QueryTriggerInteraction.Ignore);

                if (count >= HitBufferCapacity)
                {
                    // 饱和 ⇒ 显式失败。PhysX 不保证返回最近的那条，静默取"碰巧拿到的"就是漏墙。
                    _saturationFailures++;
                    return false;
                }

                bool blocked = false;
                float earliest = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    RaycastHit candidate = _hitBuffer[i];
                    Collider collider = candidate.collider;
                    if (collider == null) { continue; }
                    if (!IsAllowed(collider)) { continue; }

                    // 第二层 trigger 过滤：查询参数已经是 Ignore，这里仍然显式判一次
                    // （参数被宿主改动/替身实现不同都可能让它漏进来）。
                    if (collider.isTrigger) { continue; }

                    float hitDistance = candidate.distance;
                    if (float.IsNaN(hitDistance) || float.IsInfinity(hitDistance) || hitDistance < 0f)
                    {
                        continue;
                    }

                    if (hitDistance > distance) { continue; }

                    if (hitDistance < earliest)
                    {
                        earliest = hitDistance;
                        blocked = true;
                    }
                }

                if (!blocked)
                {
                    // 无阻挡：显式返回 true 并**原样回填**直线结果。
                    position = straightLinePosition;
                    velocity = straightLineVelocity;
                    yaw = straightLineYaw;
                    return true;
                }

                // ---- 3) 受阻：停在接触点回退 skin 之后的位置，速度归零 ----
                float travel = earliest - ContactSkinMeters;
                if (travel < 0f) { travel = 0f; }

                PMVector3 contact = start + delta * (travel / distance);

                _blockedSteps++;

                if (!TryRecordStopMark(key, contact))
                {
                    // 同起点重叠分支：表满时显式失败，绝不为了腾位而出卖已接受的标记。
                    _stopMarkCapacityFailures++;
                    return false;
                }

                position = contact;
                velocity = PMVector3.Zero;
                yaw = straightLineYaw;
                return true;
            }
            catch (Exception)
            {
                // 查询期异常（PhysX/宿主封装）：fail closed。绝不回退直线（那等于穿墙）。
                _queryFailures++;
                return false;
            }
        }

        /// <summary>
        /// true = 本步应停止在 <paramref name="stopPosition"/>（该位置来自最近一次 <see cref="TryStep"/>
        /// 记下的"本步停止标记"，本调用会**消费**它）。
        ///
        /// false = 正常语义（本步不停止），**不是**失败。
        /// 标记按 <see cref="PMProjectileKey"/> 记账，因此同一帧内多颗弹交错调用
        /// TryStep/TryStop 不会互相串（这是 R5-C1 明确点名的一条）。
        /// </summary>
        public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition)
        {
            stopPosition = snapshot != null ? snapshot.Position : PMVector3.Zero;

            if (_disposed)
            {
                _disposedCalls++;
                return false;
            }

            RequireMainThread();

            StopMark mark;
            if (!_stopMarks.TryGetValue(key, out mark))
            {
                return false;
            }

            // 先消费再返回（拖到返回之后再删就可能被下一次 TryStep 覆盖/重复消费）。
            _stopMarks.Remove(key);
            _consumedStopMarks++;
            stopPosition = mark.Position;
            return true;
        }

        /// <summary>
        /// 清空全部"本步停止标记"。只影响标记表，不影响任何计数器与几何数据（诊断仍可读）。
        ///
        /// 两个用途：① 宿主在对账/重连准备阶段主动丢弃陈旧标记；
        /// ② **从标记表饱和中恢复** —— 当 <see cref="PMUnityProjectileMotionStats.StopMarkCapacityFailures"/>
        /// 非 0（表中堆了未被消费的陈旧标记，新的阻挡步只能 fail closed）时，宿主调本方法即可恢复记录能力。
        /// 清空本身是显式动作，因此它"丢弃未消费标记"是被允许的（不像容量淘汰会静默发生）。
        /// </summary>
        public void Clear()
        {
            _stopMarks.Clear();
        }

        /// <summary>
        /// 释放：清空标记并让后续 TryStep/TryStop 一律返回 false（= 不推进、就地停止的 fail-closed 语义），
        /// 而不是抛异常打断整合器的 Drain 循环。幂等。
        /// 注： Dispose 也是清空停止标记的合法入口（与 <see cref="Clear"/> 同效），
        /// 释后调用计入 <see cref="PMUnityProjectileMotionStats.DisposedCalls"/>。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }

            _disposed = true;
            _stopMarks.Clear();
        }

        // ================================================================================
        //  内部实现
        // ================================================================================

        /// <summary>
        /// 起点正重叠查询。<paramref name="saturated"/> = true 表示缓冲饱和，
        /// 此时**无法证明**起点是干净的，调用方必须 fail closed。
        ///
        /// 为什么必须显式做这一步（而不是只依赖 SphereCast 的零距离命中）：
        ///   · `SphereCast` 对"起点已接触/重叠"的返回语义依赖 PhysX 的初始重叠配置，
        ///     属"已观测"而非"可依赖的接口契约"；
        ///   · `OverlapSphere` 的语义是确定的 "touching **or** inside"，它的候选集**包含**
        ///     所有真正重叠的 Collider，是"起点是否嵌在几何里"的权威答案。
        /// 两个都做，才能同时覆盖"嵌墙（穿透）"与"贴着墙（接触）"。
        /// </summary>
        private bool HasStartOverlap(Vector3 origin, float radius, out bool saturated)
        {
            saturated = false;

            int count = _scene.OverlapSphere(origin, radius, _overlapBuffer, _layerMask,
                                            QueryTriggerInteraction.Ignore);

            if (count >= OverlapBufferCapacity)
            {
                saturated = true;
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlapBuffer[i];
                if (collider == null) { continue; }
                if (!IsAllowed(collider)) { continue; }
                if (collider.isTrigger) { continue; }
                return true;
            }

            return false;
        }

        /// <summary>白名单判定（**引用相等**）：只有地图自己的硬障碍参与，旧大厅/表现胶囊一律不参与。</summary>
        private bool IsAllowed(Collider collider)
        {
            for (int i = 0; i < _allowlist.Length; i++)
            {
                if (ReferenceEquals(_allowlist[i], collider))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 记下"本步停止标记"。**有界且不牺牲已接受结果**：表已满（条数 ==
        /// <see cref="MaxStopMarkRecords"/>）且本 key 尚未在表中时返回 false，
        /// 调用方（<see cref="TryStep"/>）据此显式 fail closed；既不淘汰最旧，也不覆盖任何
        /// 尚未被 <see cref="TryStop"/> 消费的标记。重复记录同一 key 只更新位置，不增加条数。
        ///
        /// 注：<see cref="TryStep"/> 在判定前已对本 key 调过 <see cref="ForfeitStopMark"/>，
        /// 因此这里正常情况下不会碰到"同 key 已存在"；那个分支仅为防御性。
        /// </summary>
        private bool TryRecordStopMark(PMProjectileKey key, PMVector3 stopPosition)
        {
            if (!_stopMarks.ContainsKey(key) && _stopMarks.Count >= MaxStopMarkRecords)
            {
                // 满：不淘汰、不覆盖，返回失败让 TryStep 显式 false（fail closed）。
                return false;
            }

            StopMark mark;
            mark.Position = stopPosition;

            _stopMarks[key] = mark;
            return true;
        }

        /// <summary>作废某 key 的标记（TryStep 开始时调用，保证标记只描述"最近一步"）。
        /// 这是**同一 key**的"最新一步胜出"语义，与容量满时的处理无关：容量满时本方法
        /// 不会为别的 key 腾位（那是 <see cref="TryRecordStopMark"/> 返回 false 与 TryStep 显式失败）。</summary>
        private void ForfeitStopMark(PMProjectileKey key)
        {
            if (_stopMarks.Remove(key))
            {
                _forfeitedStopMarks++;
            }
        }

        private void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "PMUnityProjectileMotion: 查询必须在构造它的主线程上执行（构造线程 "
                    + _mainThreadId + "，当前 " + Thread.CurrentThread.ManagedThreadId + "）。"
                    + "整合器会把该异常当作 hook 失败并就地停止（fail closed）。");
            }
        }

        /// <summary>
        /// 白名单校验 + 防御性拷贝。三条硬性：
        ///   1) 非空（见构造注释：空白的两种默认解释都不安全）；
        ///   2) 每一项都不是 null 且引用有效（已销毁的 Collider 在 Unity 里取 .gameObject 会抛）；
        ///   3) 每一项与该 PhysicsScene **属于同一物理世界**（防止旧大厅 Collider 混进来）。
        /// </summary>
        private static Collider[] CopyAndValidateAllowlist(Collider[] allowedColliders, PhysicsScene physicsScene)
        {
            if (allowedColliders == null || allowedColliders.Length == 0)
            {
                throw new ArgumentException(
                    "PMUnityProjectileMotion: allowedColliders 不能为 null 或空。"
                    + "空白的两种解释都不安全（允许一切 ⇒ 旧几何进入查询；允许零个 ⇒ 弹穿墙），"
                    + "因此显式失败。宿主应传入 PMUnityBattleMap.Colliders。", "allowedColliders");
            }

            Collider[] copy = new Collider[allowedColliders.Length];

            for (int i = 0; i < allowedColliders.Length; i++)
            {
                Collider collider = allowedColliders[i];

                if (collider == null)
                {
                    throw new ArgumentException(
                        "PMUnityProjectileMotion: allowedColliders[" + i + "] 为 null（白名单不允许空项）。",
                        "allowedColliders");
                }

                GameObject owner;
                try
                {
                    owner = collider.gameObject;
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        "PMUnityProjectileMotion: allowedColliders[" + i + "] 的 Collider 引用失效"
                        + "（对象已销毁？）：" + ex.GetType().Name + " " + ex.Message, "allowedColliders");
                }

                if (owner == null)
                {
                    throw new ArgumentException(
                        "PMUnityProjectileMotion: allowedColliders[" + i + "] 没有宿主 GameObject"
                        + "（引用已失效）。", "allowedColliders");
                }

                Scene ownerScene = owner.scene;
                if (!ownerScene.IsValid())
                {
                    throw new ArgumentException(
                        "PMUnityProjectileMotion: allowedColliders[" + i + "] 不属于任何有效场景"
                        + "（Scene.IsValid()==false），无法证明它与 physicsScene 是同一个物理世界。",
                        "allowedColliders");
                }

                PhysicsScene ownerPhysicsScene = ownerScene.GetPhysicsScene();
                if (!ownerPhysicsScene.Equals(physicsScene))
                {
                    throw new ArgumentException(
                        "PMUnityProjectileMotion: allowedColliders[" + i + "]（对象=\"" + SafeName(owner)
                        + "\"）所在的物理世界与构造传入的 physicsScene 不一致。"
                        + "白名单必须与 physicsScene 同场景，否则旧大厅/旧场景的 Collider 会进入投射物查询。",
                        "allowedColliders");
                }

                copy[i] = collider;
            }

            return copy;
        }

        private static string SafeName(GameObject go)
        {
            if (go == null) { return "<null>"; }
            return go.name == null ? "<unnamed>" : go.name;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Vector3 ToUnity(PMVector3 v)
        {
            return new Vector3(v.X, v.Y, v.Z);
        }
    }

    /// <summary>
    /// <see cref="PMUnityProjectileMotion"/> 的诊断计数快照（值类型，可自由传递）。
    /// 只增不减，仅用于"证明某条分支真的被执行过"，不承载任何权威语义。
    /// </summary>
    public struct PMUnityProjectileMotionStats
    {
        /// <summary><see cref="PMUnityProjectileMotion.TryStep"/> 通过入参校验并开始判定的次数。</summary>
        public int Steps;

        /// <summary>被判为受阻（起点重叠或沿线接触）的步数。</summary>
        public int BlockedSteps;

        /// <summary>其中由"起点正重叠"权威判定挡下的步数。</summary>
        public int StartOverlapBlocks;

        /// <summary>查询缓冲饱和而 fail closed 的次数（TryStep 返回 false）。</summary>
        public int SaturationFailures;

        /// <summary>入参非法（null spec/state、0/负/NaN 半径、非 finite 位置速度朝向）而拒收的次数。</summary>
        public int RejectedInputs;

        /// <summary>查询期异常而 fail closed 的次数。</summary>
        public int QueryFailures;

        /// <summary>被 <see cref="PMUnityProjectileMotion.TryStop"/> 消费掉的停止标记数。</summary>
        public int ConsumedStopMarks;

        /// <summary>
        /// 因停止标记表已满（<see cref="PMUnityProjectileMotion.MaxStopMarkRecords"/>）且本步确实
        /// 需要记一条新标记，而由 TryStep **显式返回 false** 的次数（fail closed）。
        /// 非 0 说明有**未被消费**的陈旧标记把表撑满了：宿主必须调
        /// <see cref="PMUnityProjectileMotion.Clear"/>（或 Dispose）才能恢复记录能力。
        /// **注意**：本适配器**不会**为腾位淘汰/覆盖已接受的标记，因此不会出现
        /// "把撞墙的步静默变成没撞"。
        /// </summary>
        public int StopMarkCapacityFailures;

        /// <summary>被下一次 TryStep 开始时主动作废的标记数（同一 key 的"最近一步胜出"语义）。</summary>
        public int ForfeitedStopMarks;

        /// <summary>释放后仍被调用的次数（宿主纪律问题；该调用一律返回 false）。</summary>
        public int DisposedCalls;

        /// <summary>当前未被消费的停止标记条数（恒 &lt;= MaxStopMarkRecords）。</summary>
        public int LiveStopMarks;
    }
}
