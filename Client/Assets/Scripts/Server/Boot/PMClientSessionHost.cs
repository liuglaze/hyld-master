// R3-B：客户端会话宿主（契约 Docs/plans/net-r3-control-contract.md §7.1 / §7.2 / §7.4）。
//
// 入口只有两个：<see cref="PMClientSessionHost.Enter"/>（收到 PMDS1 入局通知后）与
// <see cref="PMClientSessionHost.Stop"/>（退出/切服）。
//
// 关键纪律（逐条对应契约）：
//   - 先校验 offer 的协议摘要与碰撞摘要，**不一致直接失败，绝不回退旧链**（§7.1「解码失败报错不得退旧链」）；
//   - 保留 Lobby TCP（大厅长连接不动），但**显式关掉旧战斗 UDP socket**（§7.4「关闭旧 UDP 后 OpenClient」）；
//   - 建共享的固定测试场景（可见简单几何，**不宣称真实玩法**，§7.4）；
//   - `OpenClient(offer, bridge)` 后由本宿主 Pump；`endpoint.Pump` 内部已调 `bridge.Update`，
//     因此**不再二次 Update**（§7.2）；
//   - 本端副本经生命周期消息创建后，**仅本地 owner** 发一次上行探针（§7.4），随后观察 Echo 与属性收敛；
//   - 重复 Enter 同一局不重绑定；换局先 Stop 再 Enter；退出时清理世界/socket/场景根/static 事件。
//
// ---------------------------------------------------------------------------------------------
// R4-B（B3）新增：本机运动主链
// ---------------------------------------------------------------------------------------------
// 每个复制到的副本（**AP 与 SP 都算**，旧实现把 SP 直接跳过，那会让别人的角色在客户端根本不动）
// 建一个 PMR4MovementDriver + 一个 PMUnityMoverPresentation：
//   · AP（本地 owner）：每帧用真实整 ms 余数累加出 1..50 的子步（每 Update 最多 8 步），
//     每个子步“先 Sample（不消费边沿）→ Tick → 接受后才 Consume”（契约 §B3：不能先消费再因冻结/超限丢按键），
//     然后把预测状态喂给表现层；
//   · SP：只 Advance + SamplePresentation（只插值、不外推），不预测、不回滚。
// 每帧次序：Physics.SyncTransforms 一次 → endpoint.Pump（含 bridge.Update）→ 采样/预测或插值 →
// 后续网络 flush（上行输入由**下一次** endpoint.Pump 发出，本帧不抢数据报预算）。
// 失联/失败：必须 Freeze（不再 Tick/Advance/Apply），但墙钟仍可推进（R4-A 的断线墙钟只在冻结时生效），
// 输入与表现都不得越界。
// 与旧链并存约束：本类不启动任何旧战斗逻辑；Enter 时已显式关掉旧战斗 UDP socket，
// 因此不会与旧 BattleManger 同一局双驱动。
//
// ---------------------------------------------------------------------------------------------
// R4-C（C3）新增：显式会话模式（诊断内容 vs 正式内容）
// ---------------------------------------------------------------------------------------------
// offer 里的 `CollisionDigest` 是模式选择的**唯一**来源（它由 Lobby 分配、与票据/MAC 绑定）：
//   · `0`           → 非法（未选择内容），拒绝入局；
//   · `0x52334201`  → 诊断：保留 R3-B 的 PMR3TestScene + PMUnityMoverPresentation（保住已验烟测）；
//   · 其它非 0      → 正式：加载 C2 的 PMUnityBattleMap（manifest 内部强校验 + 与本 offer 摘要相等），
//                       表现改用 PMUnityBattlePresentation，查询 WorldVersion 来自同一份 manifest。
// 两种模式的 rig（驱动 + 表现）走**同一套** private 接口，因此 Apply/Dispose 只有一处实现。
// 正式模式下本地 owner 的测试相机由宿主**临时**关闭其它已启用相机来确保可见，
// 退局时按记录恢复（**不**改旧场景的永久设置）。
// 首批输入仍是键盘 WASD/Space（PMUnityMoverInput），攻击尚未接入 —— 不宣称完整玩法。
//
// ---------------------------------------------------------------------------------------------
// R5-C 新增：诊断投射物（客户端预测 + 候选收集 + 薄表现）
// ---------------------------------------------------------------------------------------------
// 本批接的是**诊断投射物探针**，不是英雄普通攻击/大招，也不产生任何伤害：
//   · 会话建立时按「本局实际物理场景」创建 PMUnityProjectileMotion（独立 PhysicsScene + 白名单）
//     与 PMR5ProjectileDriver（客户端 policy = null）：
//       - history 是一个**空的** PMProjectileHistory，只作构造占位（本类从不 Record），
//         因此它不冒充 DS 权威历史；DS 才是目标历史的唯一来源；
//       - hostMotion 就是上面那颗 motion，它让本地预测弹具备「撞墙即停」的几何。
//   · 每个已复制副本（AP/SP）都 BindPlayer；退出/失败按 Driver → Motion → presentation → 地图 释放。
//   · Update 里 **采样一次** F 键边沿（与 Mover 输入边沿同纪律），只对本地 AP、未冻结时开火：
//     activationId 单调递增**绝不回绕**；方向由 Yaw 决定世界前向；出生点 = 预测位置 + 0.6m 枪口偏移；
//     spec 取共享只读的 PMProjectileDiagnosticConfig；另有 200ms 客户端墙钟门（DS 仍是权威）。
//   · 投射物按**固定 16ms** 子步推进（每 Update 最多 8 步）；**零步也 Pump(0)**（排裁决/清墓碑/重建视图）。
//     推进次序在 PumpMovement **之后**：目标样本必须来自「本帧已经算完的 Mover 呈现位姿」。
//   · 候选收集：对**自己预测来源**的弹（owner == 本地且 origin == ClientPredicted）逐**子步**做
//     线段 × 目标 Y 轴胶囊的纯数学最近距离判定；**不看** LocalFake/TakenOver，
//     因此镜像接管后不会突然停报；已停止只允许最后一段。
//     候选只走**同一条生成声明通道**上报（ServerProjectileHitV1），DS 才是最终裁决者。
//   · views → PMUnityProjectilePresentation 的字典有界；key 消失即 Dispose（**不依据 Hidden 判断销毁**），
//     Hidden/Stopped 只走 Apply。表现是诊断占位球，**不声称**英雄子弹外观已迁移。
//   · 驱动 IsFaulted / 任何处理异常一律走既有 Fail（冻结 + 报错），绝不吞掉继续。
//
// ---------------------------------------------------------------------------------------------
// 复审补强（Docs/plans/_r5_client_host_review.md）
// ---------------------------------------------------------------------------------------------
//   · **每帧排空驱动的视图增量通道**（DrainViewChanges）：本宿主的真值始终是**全量** CopyViews
//     （「快照差集」才是唯一可靠的移除判据），排空只为不让驱动内部那些按 key 累积的脏集合
//     （_dirtyViewOrder / _dirtyViewSet）把已退休的 key 一直留在里面 —— 不排空时它们会累计到
//     MaxDirtyViews(4096) 后整体作废重来（ViewDirtyOverflows 无界增长、脏集合常驻几千个退休 key）。
//   · **容量与驱动对齐**：表现字典与视图快照缓冲都取 PMProjectileLimits.MaxProjectiles(=1024，
//     与驱动的对象容量同值)；真的超过容量就**整局失败**，不再「计数 + continue」静默丢视图 ——
//     截断会连带丢掉候选，而静默丢视图正是契约明令禁止的。
//   · **视图追踪集与表现字典解耦**：尺寸尚未就绪（RadiusM 非法）而被跳过表现的 key 也要被追踪，
//     key 消失时一样要 Dispose/Forget，否则该 key 的候选账（每 key 最多 100 目标）会永久占住。
//
// ---------------------------------------------------------------------------------------------
// R6-C 新增：正式直线攻击输入 / 只读结算 HUD / 结果正常退场
// ---------------------------------------------------------------------------------------------
// 本批把「F 键诊断单发弹」换成**正式直线攻击**（F=普通攻击、G=大招），并补上客户端的结果退场：
//   · 会话建立后创建会话级 PMR6CombatDriver（model=null：客户端不持有权威核心），
//     并把它接到每个已复制副本上（`BindPlayer` 内含 R5 绑定，一个 player 的两条接缝一次接好）；
//   · 开火 = `R6.TryAttack(...)`：先共用 planner 建本地计划 → 先发 ServerCombatAttackV1 →
//     再按计划逐颗 R5.TryFire（**不等服务器批准**）。**不再** double-send 诊断弹；
//   · 客户端门 = `plan.FireIntervalMs`（planner 口径），替换原来的 200ms 诊断固定门；
//     本地 planner 拒绝（UnsupportedAttack / 未知英雄 / 非法方向）与 DS 裁决拒绝（蓝量/能量/间隔…）
//     **只显示不 fault**；只有 `driver.IsFaulted` 才是整局失败；
//   · Mover 真实 predicted yaw 决定世界瞄准方向，枪口 = 预测位置 + yaw 前向 × 0.6m（同一次攻击 N 颗同枪口）；
//   · 客户端**不写** HP/Mana/Energy（不设第三权威），只读 DS 复制字段；
//   · 每帧固定次序：`endpoint.Pump` → `R6.Pump(now)` → Movement → R5 子步（候选+表现）→ `R6.FlushState(now)`，
//     所有 driver/core 共用**同一根单调墙钟**（同一个 now 值，避免前后错差导致时钟倒退）；
//   · `CombatDead` ⇒ 冻结对应 Mover（客户端不做权威死亡判定，只吃复制结论）；
//     `CombatMatchEnded` ⇒ 冻结**全部**运动、停输入与候选，但**继续**协议 Pump（端点/R6/R5）等结果退出；
//   · 死者不进候选（复制值提前滤），同队可提前滤（DS 仍是最终裁决）；诊断球美术保留；
//   · 新增薄只读 PMUnityCombatHud（OnGUI，无 Canvas/无 Material）：本人 HP/MaxHp/Mana/Energy、
//     F/G 提示、最近拒绝原因、胜/负/平 + **原始** winnerTeamId；
//   · `ClientCombatMatchResult` 经 `R6.Pump` 幂等保存、ACK 由 `R6.FlushState` 发；客户端据此发布
//     `LastCombatOutcome/LastCombatWinnerTeamId/LastCombatMatchId` + 只读结果事件给后续大厅 UI；
//   · 已知可信终局后 DS 正常退场/端点 idle 关闭**不当作 Fail**；按正常路径释放会话（隔离物理场景卸载、
//     相机恢复、端点释放）回到原大厅，只读终局 HUD 保留到**下一 Enter 或显式 Stop**才销毁；
//   · 未收到可信终局时断网仍然 Fail（任何 disconnect 都不当胜利）；旧链 BattleReview 不参与伪造。

using System;
using System.Collections.Generic;
using System.Globalization;
using Logging;
using PMNet;
using PMNet.Combat;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.Projectile;
using PMNet.R3;
using PMNet.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// 客户端侧新链会话宿主（契约 §7.4）。
    ///
    /// 生命周期：<see cref="Enter"/> → 由内部驱动的 MonoBehaviour 每帧 <c>PumpActive</c> →
    /// <see cref="Stop"/>（或退出时自动收尾）。
    /// </summary>
    public static class PMClientSessionHost
    {
        /// <summary>心跳日志间隔（秒）。</summary>
        private const float HeartbeatLogIntervalSeconds = 5f;

        /// <summary>
        /// **诊断模式**下本局运动碰撞世界的版本号。**必须与 PMDsSessionHost.MovementWorldVersion 同值**：
        /// 快照携带的 Aux.CollisionWorldVersion 会被本端比对，不一致则拒绝并请求重同步。
        ///
        /// R4-C（C3）：正式内容模式使用 manifest 里的 `worldVersion`（见 Session.SelectedWorldVersion）。
        /// </summary>
        public const int MovementWorldVersion = 1;

        /// <summary>单个子步的最小/最大整毫秒（契约 §B1：1..50）。</summary>
        private const int MinStepMs = 1;
        private const int MaxStepMs = 50;

        /// <summary>每宿主 Update 最多推进的子步数（契约 §B1：最多 8 步不拉大 dt）。</summary>
        private const int MaxSubstepsPerUpdate = 8;

        /// <summary>累加器的上限（毫秒）。落后太多时宁可丢时间，也不一帧内追赶出巨量子步。</summary>
        private const double MaxStepAccumulatorMs = 200.0;

        // ---- R5-C：诊断投射物（契约「C宿主首个可执行入口」）----

        /// <summary>投射物子步固定毫秒（共享只读配置）。**不**复用 Mover 的变步累加器。</summary>
        private const int ProjectileStepMs = PMProjectileDiagnosticConfig.StepMs;

        /// <summary>每宿主 Update 最多推进的投射物子步数（契约：最多 8 步）。</summary>
        private const int ProjectileMaxStepsPerUpdate = PMProjectileDiagnosticConfig.MaxStepsPerPump;

        /// <summary>
        /// 薄表现字典与视图快照缓冲的容量。
        ///
        /// **必须与驱动自身的对象容量对齐**（<see cref="PMProjectileLimits.MaxProjectiles"/> = 1024）：
        /// 小于驱动的上界就会把「拷不进来」变成**静默丢视图**（表现与候选一起丢，契约明令禁止）。
        /// 真出现超容量时 <see cref="SyncProjectileViews"/> 显式整局失败，既不截断也不丢弃。
        /// </summary>
        private const int MaxPresentationViews = PMProjectileLimits.MaxProjectiles;

        /// <summary>
        /// 正式直线攻击上行携带的本地预测提前量（毫秒）。
        ///
        /// 本批**不**做实测 RTT 折算（客户端没有 RTT 测量口），因此取一个固定占位值；
        /// DS 侧的追赶预算 = prediction/2 + 挂起时长，全部由整合器自记账（见 _r5_network_review.md）。
        /// 该值只影响“权威初值发布时是否追赶”，不影响任何判定。
        /// </summary>
        private const int CombatAttackPredictionMs = 100;

        /// <summary>
        /// rig 里表现的**统一写入面**：诊断用 PMUnityMoverPresentation（胶囊），
        /// 正式用 PMUnityBattlePresentation（C1 烘焙的正式角色）。
        ///
        /// 为什么用接口而不是改两组公开 API：两个表现类的 Apply/Dispose 语义一致、类型不同，
        /// 在宿主内部做一层薄适配即可让“清理 / Apply”只有一处调用点（C2 的公开面不动）。
        /// </summary>
        private interface IRigPresentation
        {
            void ApplyPredicted(PMMoverSyncState state);

            void ApplyInterpolated(PMMoverSyncState state);

            void Dispose();
        }

        private sealed class MoverRigPresentation : IRigPresentation
        {
            private readonly PMUnityMoverPresentation _inner;

            public MoverRigPresentation(PMUnityMoverPresentation inner)
            {
                _inner = inner;
            }

            public void ApplyPredicted(PMMoverSyncState state) { _inner.ApplyPredicted(state); }

            public void ApplyInterpolated(PMMoverSyncState state) { _inner.ApplyInterpolated(state); }

            public void Dispose() { _inner.Dispose(); }
        }

        private sealed class BattleRigPresentation : IRigPresentation
        {
            private readonly PMUnityBattlePresentation _inner;

            public BattleRigPresentation(PMUnityBattlePresentation inner)
            {
                _inner = inner;
            }

            public void ApplyPredicted(PMMoverSyncState state) { _inner.ApplyPredicted(state); }

            public void ApplyInterpolated(PMMoverSyncState state) { _inner.ApplyInterpolated(state); }

            public void Dispose() { _inner.Dispose(); }
        }

        /// <summary>
        /// 一个副本的本地运动链（驱动 + 表现）。
        /// AP 与 SP **都**需要它（区别在于每帧调 Tick 还是 Advance）。
        /// </summary>
        private sealed class MovementRig
        {
            public PMR3Player Player;
            public PMR4MovementDriver Driver;
            public IRigPresentation Presentation;
            public bool IsOwner;

            /// <summary>
            /// R6-C：该副本已按**复制结论**（<c>CombatDead</c>）冻结（幂等，只冻结一次）。
            ///
            /// 为什么不用驱动的 <c>PlayerDied</c> 事件：那个事件只在权威侧（DS）由结算产生，
            /// 客户端（AP）根本不会收到结算 ⇒ 事件永远不会触发。客户端能看到的唯一死亡真值
            /// 就是 DS 复制过来的 <c>CombatDead</c>。
            /// </summary>
            public bool DeathFrozen;
        }

        /// <summary>被宿主临时关闭的相机（以及它原来的启用状态），退局时按此恢复。</summary>
        private sealed class SuppressedCamera
        {
            public Camera Camera;
            public bool WasEnabled;
        }

        /// <summary>
        /// 单颗投射物视图的表现 + **复用**的 state/spec。
        ///
        /// 为什么复用：表现层的 `Apply(PMProjectileState, PMProjectileSpec)` 只读这两者，
        /// 每子步新建一对就是每帧最多 8×视图数 的无谓分配；而表现层自己不做任何持久化，
        /// 因此「就地覆写 + 复用」与「每次新建」语义完全等价。
        /// </summary>
        private sealed class ProjectileViewEntry
        {
            public PMUnityProjectilePresentation Presentation;
            public readonly PMProjectileState State = new PMProjectileState();
            public readonly PMProjectileSpec Spec = new PMProjectileSpec();
        }

        private sealed class Session
        {
            public PMDsEntryOffer Offer;
            public PMSession NetSession;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;
            public readonly List<GameObject> SceneObjects = new List<GameObject>();
            public readonly List<PMR3Player> PendingProbes = new List<PMR3Player>();
            public readonly List<PMR3Player> KnownPlayers = new List<PMR3Player>();
            public long TickCount;
            public int ProbeNonce;
            public float NextHeartbeatLogTime;
            public bool Faulted;
            public string FaultReason;
            public Action<string> PreviousRuntimeWarn;

            // ---- R4-B（B3）：运动主链 ----

            /// <summary>碰撞查询（白名单 = 场景地板/墙，WorldVersion = 1，与 DS 同值）。</summary>
            public PMUnityMoverCollisionQuery Query;

            // ---- R4-C（C3）：显式会话模式 ----

            /// <summary>本局是否走正式内容（false = 诊断 PMR3TestScene）。</summary>
            public bool ContentFormal;

            /// <summary>正式内容地图（仅正式模式非空；它持有隔离物理场景与查询，释放走它自己的 Dispose）。</summary>
            public PMUnityBattleMap BattleMap;

            /// <summary>本局实际选定的碰撞摘要（诊断 = 保留值；正式 = manifest 值）。</summary>
            public uint SelectedCollisionDigest;

            /// <summary>本局运动碰撞世界版本（诊断 = 1；正式 = manifest.worldVersion）。</summary>
            public int SelectedWorldVersion;

            /// <summary>被宿主临时关闭的相机（正式模式重开相机可见性用；退局恢复）。</summary>
            public readonly List<SuppressedCamera> SuppressedCameras = new List<SuppressedCamera>();

            /// <summary>
            /// R4-B（B3）：地板/墙所在的**本地物理场景**（`LocalPhysicsMode.Physics3D`）。
            /// 这是碰撞隔离的载体：查询必须打在它上面，而不是 Physics.defaultPhysicsScene。
            /// 失效场景（default）表示本局没有隔离世界，ReleaseSession 里尝试释放。
            /// </summary>
            public Scene MovementScene;

            /// <summary>
            /// 本端唯一的 AP 副本（同局出现第二个 AP 直接失败：一份输入不能驱动两个 AP）。
            /// </summary>
            public PMR3Player OwnerPlayer;

            /// <summary>本机输入采样（AP 用；SP 不读输入）。</summary>
            public PMUnityMoverInput Input;

            /// <summary>全部副本的本地运动链（AP + SP）。</summary>
            public readonly List<MovementRig> Movements = new List<MovementRig>();

            /// <summary>真实整毫秒累积的剩余量（子步驱动）。</summary>
            public double StepAccumulatorMs;


            /// <summary>已因失联/失败冻结全部运动（不再 Tick/Advance/Apply）。</summary>
            public bool MovementFrozen;

            // ---- R5-C：诊断投射物 ----

            /// <summary>
            /// 目标历史：客户端**不记录**任何样本（DS 才是权威历史源）。
            /// 这里只是一个空实例，用来满足驱动的构造入参 —— **绝不**用它伪造 DS 历史。
            /// </summary>
            public PMProjectileHistory ProjectileHistory;

            /// <summary>本地投射物运动（独立物理场景 + 白名单；与 Mover 运动查询互相隔离）。</summary>
            public PMUnityProjectileMotion ProjectileMotion;

            /// <summary>会话级投射物驱动（客户端 policy = null；不可信 payload 因此无从被采信）。</summary>
            public PMR5ProjectileDriver ProjectileDriver;

            /// <summary>
            /// 候选收集器。**只有在本端 AP 副本已经复制下来之后**才能构造：
            /// 「本地 owner NetId」是它的构造入参，而 uid 并不是 NetId。
            /// </summary>
            public PMProjectileCandidateCollector CandidateCollector;

            /// <summary>views → 薄表现（有界；key 消失即 Dispose，不留幽灵弹）。</summary>
            public readonly Dictionary<PMProjectileKey, ProjectileViewEntry> ProjectileViews =
                new Dictionary<PMProjectileKey, ProjectileViewEntry>();

            /// <summary>本帧 CopyViews 的条数（候选收集与表现写入共用同一份快照）。</summary>
            public int ProjectileViewCount;

            /// <summary>视图快照缓冲（容量即表现字典上限；被截断时不做移除判定）。</summary>
            public PMR5ProjectileView[] ProjectileViewBuffer;

            /// <summary>视图差集用暂存（避免每子步 O(n²) 扫描）。</summary>
            public readonly HashSet<PMProjectileKey> ProjectileViewSeen = new HashSet<PMProjectileKey>();

            /// <summary>待移除的视图 key 暂存。</summary>
            public readonly List<PMProjectileKey> ProjectileViewRemovals = new List<PMProjectileKey>();

            /// <summary>
            /// 上帧快照里出现过的**全部** key（含因尺寸尚未就绪而跳过表现的那些）。
            ///
            /// 为什么不能只拿 <see cref="ProjectileViews"/> 做移除判定：表现字典只装「成功建过表现」的 key，
            /// 「RadiusM 非法 → 跳过表现」或「表现上限」的 key 不在字典里；只看字典就会漏掉它们的退休，
            /// 候选账（每 key 最多 100 目标）便永久占住，最终撞上收集器的 key 上限、真实命中再也发不出去。
            /// </summary>
            public readonly HashSet<PMProjectileKey> ProjectileTrackedKeys = new HashSet<PMProjectileKey>();

            /// <summary>投射物固定 16ms 子步累加器（与 Mover 的变步累加器**互相独立**）。</summary>
            public double ProjectileAccumulatorMs;

            /// <summary>本帧实际推进的投射物子步数（0 表示本帧只 Pump(0)）。</summary>
            public int ProjectileStepsThisFrame;

            /// <summary>本帧采集的目标样本（来自各副本**本帧真实呈现**的状态）。</summary>
            public readonly List<PMProjectileTargetSample> ProjectileTargets =
                new List<PMProjectileTargetSample>(8);

            /// <summary>候选暂存（每个 key 每次上报前清空）。</summary>
            public readonly List<PMProjectileHitCandidate> ProjectileCandidates =
                new List<PMProjectileHitCandidate>(8);

            /// <summary>投射物告警出口的上一任处理者（ReleaseSession 时无条件还原，与 PMR3Runtime.Warn 同纪律）。</summary>
            public Action<string> PreviousProjectileWarn;

            // ---- R5-C 可观测计数（只增；诊断/门禁对账用）----

            public long ProjectilePumps;
            public long ProjectileHitRpcs;
            public long ProjectileHitReportRejected;
            public long ProjectileViewOverflows;
            public long ProjectileViewSkipped;
            public long ProjectileHitQuotaSkipped;
            public string LastHitReportError;

            // ---- R6-C：正式直线攻击 / 只读 HUD / 结果退场 ----

            /// <summary>
            /// 会话级战斗网络驱动（客户端 <c>model = null</c>：AP 不持有权威核心）。
            /// 它是本会话 **四条战斗 RPC** 与 planner 的唯一出口（不再有诊断开火通道）。
            /// </summary>
            public PMR6CombatDriver CombatDriver;

            /// <summary>薄只读战斗 HUD（客户端专有；显式 Stop / 下一 Enter 时 Dispose）。</summary>
            public PMUnityCombatHud Hud;

            /// <summary>
            /// 本人身份已与 offer 核对通过（hero/team 都等于 offer 的 Identity）。
            ///
            /// 为什么必须有这个位：复制字段未就位时 <c>CombatHeroId</c>/<c>CombatTeamId</c>
            /// 是**默认 0**，而 0 恰好是一个合法英雄号 —— 不核对就等于「默认 hero0 即可放行」。
            /// </summary>
            public bool OwnerIdentityVerified;

            /// <summary>已因本人身份与 offer 不一致而判整局失败（幂等，不重复报）。</summary>
            public bool OwnerIdentityMismatch;

            /// <summary>已观测到 <c>CombatMatchEnded</c>（全部运动已冻结；协议仍在 Pump，等结果退出）。</summary>
            public bool MatchEndedFrozen;

            /// <summary>已收到**可信终局结果**（可靠 ClientCombatMatchResult 经 R6 幂等保存）。</summary>
            public bool TerminalResultObserved;

            /// <summary>收到可信终局的墙钟（毫秒）；仅当 <see cref="TerminalResultObserved"/> 为真时有意义。</summary>
            public double TerminalResultWallMs;

            /// <summary>
            /// 端点失败回调（<see cref="OnEndpointFailed"/>）记下的「待本帧定性」原因。
            ///
            /// 为什么需要它：端点失败事件由 <c>endpoint.Pump</c> **同步**抛出，而那一刻
            /// 「同帧发结果 + 关会话」的合法终局还躺在 R6 的入站队列里（<c>R6.Pump</c> 尚未运行）。
            /// 若在回调里当即判失败，就会把一份已到达的合法终局降级成断线失败；因此失败原因先记在这里，
            /// 由本帧 <see cref="PumpActive"/> 在 <c>R6.Pump</c> 之后按「结果是否真的已保存」定性。
            /// </summary>
            public string PendingDisconnectReason;

            /// <summary>本帧 F 键（普通攻击）边沿：每 Update **只采样一次**，不消费直到本次尝试定论。</summary>
            public bool NormalAttackEdgeBuffered;

            /// <summary>本帧 G 键（大招）边沿（同上）。</summary>
            public bool SuperAttackEdgeBuffered;

            /// <summary>最近一次攻击失败/被拒原因（本地 planner 或 DS 裁决）；null 表示无。</summary>
            public string LastAttackError;
            public long ObservedCombatRejections;

            /// <summary>上一次**成功**发起攻击的墙钟（初值 -∞ 表示从未开火）。</summary>
            public double LastAttackWallMs = double.NegativeInfinity;

            // ---- R6-C 可观测计数（只增；心跳/门禁对账用）----

            public long CombatPumps;
            public long CombatFlushes;
            public long AttackAttempts;
            public long AttackPlannerRejected;
            public long AttackGateBlocked;
            public long AttackDriverRejected;
            public long AttackFiresAccepted;
            public long TargetsSkippedDead;
            public long TargetsSkippedTeam;
        }

        private static Session _active;
        private static bool _stopping;

        /// <summary>
        /// R6-C：保留中的只读终局 HUD（收到可信终局结果后不销毁，直到**下一 Enter 或显式 Stop**）。
        ///
        /// 它同时是「不泄漏无限创建」的阀门：全生命周期最多存在一个实例（Enter 前先释放旧的，
        /// 会话失败路径由 ReleaseSession 释放本会话那一份）。
        /// </summary>
        private static PMUnityCombatHud _retainedHud;

        // ---- R6-C：最近一次可信终局快照（只读，供后续大厅 UI；下一 Enter / 显式 Stop 清除）----

        private static bool _lastCombatResultValid;
        private static uint _lastCombatOutcomeId;
        private static int _lastCombatWinnerTeamId;
        private static string _lastCombatMatchId;
        private static Action<uint, int, string> _combatResultObserved;

        /// <summary>是否已在局内（新链）。</summary>
        public static bool IsActive { get { return _active != null && !_active.Faulted; } }

        /// <summary>最近一次失败原因（无失败时为 null）。</summary>
        public static string LastError { get { return _active != null ? _active.FaultReason : null; } }

        /// <summary>当前局的 matchId（不在局内时为 null）。</summary>
        public static string CurrentMatchId { get { return _active != null ? _active.Offer.MatchId : null; } }

        /// <summary>当前局实际激活的连接（未激活时为 null）。</summary>
        public static PMTransportConnection Connection
        {
            get { return _active != null && _active.Endpoint != null ? _active.Endpoint.ClientConnection : null; }
        }

        /// <summary>已观察到的本端副本（诊断/门禁）。</summary>
        public static int KnownPlayerCount { get { return _active != null ? _active.KnownPlayers.Count : 0; } }

        /// <summary>已发出的探针数（诊断/门禁）。</summary>
        public static int ProbeSentCount { get { return _active != null ? _active.ProbeNonce : 0; } }

        /// <summary>已收到的 Echo 次数（诊断/门禁）。</summary>
        public static int EchoCount
        {
            get
            {
                if (_active == null) { return 0; }

                int total = 0;
                for (int i = 0; i < _active.KnownPlayers.Count; i++)
                {
                    PMR3Player player = _active.KnownPlayers[i];
                    if (player != null) { total += player.EchoCount; }
                }

                return total;
            }
        }

        // =================================================================================
        //  R6-C：只读终局结果（供后续大厅 UI）
        // =================================================================================

        /// <summary>
        /// 是否已经观察到一个**可信**终局结果。
        ///
        /// 「可信」的唯一来源是 DS 通过可靠域下发的 <c>ClientCombatMatchResultV1</c>，经
        /// <see cref="PMR6CombatDriver"/> 幂等保存后由本宿主取出。断线/端点关闭**不**会把它置真
        /// （契约：未收到可信终局时断网仍 Fail，任何 disconnect 都不能当胜利）。
        /// </summary>
        public static bool HasLastCombatResult { get { return _lastCombatResultValid; } }

        /// <summary>最近一次可信终局的结果号（无结果时为 0）。</summary>
        public static uint LastCombatOutcome { get { return _lastCombatResultValid ? _lastCombatOutcomeId : 0u; } }

        /// <summary>最近一次可信终局的胜方队伍号（**原始** TeamId；无唯一胜者时为 0）。</summary>
        public static int LastCombatWinnerTeamId { get { return _lastCombatResultValid ? _lastCombatWinnerTeamId : 0; } }

        /// <summary>最近一次可信终局所属的对局 ID（无结果时为 null）。</summary>
        public static string LastCombatMatchId { get { return _lastCombatResultValid ? _lastCombatMatchId : null; } }

        /// <summary>
        /// 只读结果事件（大厅 UI 订阅入口）：每次收到第一份可信终局时触发一次。
        ///
        /// 参数 =（outcomeId, winnerTeamId 原始队伍号, matchId）。本宿主只负责发布；
        /// 订阅者生命周期由订阅者自己管理（不随本会话释放而失效）。
        /// </summary>
        public static event Action<uint, int, string> CombatResultObserved
        {
            add { _combatResultObserved += value; }
            remove { _combatResultObserved -= value; }
        }

        /// <summary>读最近一次可信终局（无结果时返回 false，三个 out 均为默认值）。</summary>
        public static bool TryGetLastCombatResult(out uint outcomeId, out int winnerTeamId, out string matchId)
        {
            outcomeId = LastCombatOutcome;
            winnerTeamId = LastCombatWinnerTeamId;
            matchId = LastCombatMatchId;
            return _lastCombatResultValid;
        }

        /// <summary>保留中的只读终局 HUD 是否还在（诊断/门禁对账）。</summary>
        public static bool HasRetainedHud { get { return _retainedHud != null && !_retainedHud.IsDisposed; } }

        // =================================================================================
        //  入口
        // =================================================================================

        /// <summary>
        /// 进入一局（新链）。**只在这里做一次性的摘要校验与全套接线**，失败即失败，不回旧链。
        ///
        /// 重复 Enter 同一局（MatchId + Epoch 相同）会被忽略：既有的世界/socket/副本保持不变。
        /// </summary>
        public static void Enter(PMDsEntryOffer offer)
        {
            if (offer == null) { throw new ArgumentNullException("offer"); }

            // 1) 生成表必须先注册：摘要校验的唯一来源（未注册时 ProtocolHash 为 0）。
            PMR3Runtime.Register();

            if (PMR3Runtime.ProtocolHash == 0u)
            {
                Fail(null, "PMR3 协议摘要为 0：生成表未注册，拒绝入局（不回旧链）");
                return;
            }

            if (offer.ProtocolHash != PMR3Runtime.ProtocolHash)
            {
                Fail(null, "协议摘要不一致（offer 0x" + offer.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                           + " vs 本机 0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                           + "），拒绝入局（不回旧链）");
                return;
            }

            // R4-C（C3）：摘要决定模式 —— 0 非法；保留值 0x52334201 = 诊断；其它非 0 = 正式内容。
            // **不按资源是否存在猜模式**：正式模式缺 manifest/地图就是失败（不回旧链、不回落诊断）。
            if (offer.CollisionDigest == 0u)
            {
                Fail(null, "入局通知 CollisionDigest 为 0（禁止）：新链必须显式选择正式内容或诊断保留摘要，"
                           + "拒绝入局（不回旧链）");
                return;
            }

            bool contentFormal = offer.CollisionDigest != PMR3Runtime.CollisionDigest;

            if (offer.Epoch == 0u || offer.Port <= 0 || string.IsNullOrEmpty(offer.Host))
            {
                Fail(null, "入局通知字段非法（epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                           + " port=" + offer.Port.ToString(CultureInfo.InvariantCulture)
                           + " host='" + (offer.Host ?? "<null>") + "'）");
                return;
            }

            // 2) 重复 Enter 同一局：不重绑定（避免把已有世界/socket 拆掉重建）。
            if (_active != null && SameMatch(_active.Offer, offer))
            {
                HYLDDebug.Log("[PMClientSessionHost] 已在同一局内（match=" + offer.MatchId
                              + " epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                              + "），忽略重复 Enter");
                return;
            }

            if (_active != null)
            {
                // 换局：先完整收尾旧局（含旧 socket 与 static 事件），再建新局。
                HYLDDebug.Log("[PMClientSessionHost] 检测到换局，先收尾旧局（match=" + _active.Offer.MatchId + "）");
                Stop();
            }

            // R6-C：上一局的只读终局 HUD 与结果快照属于「上一局」——新入局是显式清理点之一
            //（另一个是显式 Stop）。Stop() 已经清过一遍，这里覆盖的是「终局后已释放会话、
            // _active 已为 null、只等着下一次入局」的路径。幂等。
            ReleaseRetainedHud();
            ClearLastCombatResult();

            Session session = new Session();
            session.Offer = offer;
            session.ContentFormal = contentFormal;
            session.SelectedCollisionDigest = offer.CollisionDigest;
            session.SelectedWorldVersion = contentFormal ? 0 : MovementWorldVersion;

            // 3) 世界 / 桥 / 运行时接线（客户端侧：world.IsServer 为假）。
            session.NetSession = new PMSession(offer.Epoch, false);
            session.World = new PMNetWorld(session.NetSession);
            session.World.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] world: " + m); };
            session.Bridge = new PMNetSessionBridge(session.World);
            session.Bridge.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] bridge: " + m); };

            PMR3Runtime.Attach(session.World, session.Bridge);
            session.PreviousRuntimeWarn = PMR3Runtime.Warn;
            PMR3Runtime.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] runtime: " + m); };

            // 4) 运动碰撞世界（R4-C / C3 显式会话模式）：
            //    · 诊断（offer digest == 保留值）：R3-B 的固定测试场景（可见简单几何），保住已验烟测；
            //    · 正式（其它非 0）：C2 的正式地图（manifest 内部强校验 + 与本 offer 摘要相等），
            //      查询的 WorldVersion 来自同一份 manifest。
            //    两条路径都必须在**本地物理场景**里（LocalPhysicsMode.Physics3D），
            //    否则查询会打在默认物理世界（= 大厅/旧地图）上——layer 掩码与白名单都是
            //    “后置过滤”，不能当作隔离。
            // R5-C：投射物运动链需要的「本局实际物理场景 + 白名单」由下面两条模式分支各自给出。
            PhysicsScene projectilePhysicsScene = default(PhysicsScene);
            Collider[] projectileColliders = null;

            if (session.ContentFormal)
            {
                PMUnityBattleMap battleMap;
                string mapError;
                if (!PMUnityBattleMap.TryLoad(out battleMap, out mapError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式内容地图加载失败：" + mapError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                if (battleMap.Manifest.collisionDigest != session.SelectedCollisionDigest)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式内容摘要不一致：offer 0x"
                                       + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                                       + "，本机 manifest 0x"
                                       + battleMap.Manifest.collisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                session.BattleMap = battleMap;
                session.Query = battleMap.Query;
                session.MovementScene = battleMap.Scene;
                session.SelectedWorldVersion = battleMap.Manifest.worldVersion;

                // R5-C：投射物运动复用同一份隔离物理场景与同一批白名单 Collider。
                projectilePhysicsScene = battleMap.PhysicsScene;
                projectileColliders = battleMap.Colliders;

                string consistencyError;
                if (!battleMap.ValidateManifestConsistency(out consistencyError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式地图与 manifest 不一致：" + consistencyError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                string spawnCheckError;
                if (!battleMap.TryValidateAllSpawns(out spawnCheckError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式地图出生位自检失败：" + spawnCheckError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }
            }
            else
            {
                string sceneError;
                Scene movementScene;
                PhysicsScene movementPhysicsScene;
                if (!PMR3TestScene.BuildIsolated("[PMClientScene]", true, session.SceneObjects,
                                                out movementScene, out movementPhysicsScene, out sceneError))
                {
                    // 与相邻失败分支逐字一致：必须走 ReleaseSession（还原静态 Warn + 释放桥），
                    // 否则静态出口永久停在本会话的 lambda 上，下一次 Enter 还会把它当“上一个”存下来。
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 测试场景不可用（本地物理场景隔离失败）："
                                       + sceneError + "，拒绝入局（不回旧链）");
                    return;
                }

                session.MovementScene = movementScene;

                // 4b) 诊断模式：运动碰撞查询 + 本机输入。
                //     白名单**必须**是刚建的地板/墙两个 Collider（契约 B2）：不能让旧地图/旧相机/
                //     表现胶囊参与查询。WorldVersion 与 DS 取同一个冻结值 1。
                if (!movementPhysicsScene.IsValid() || movementPhysicsScene.Equals(Physics.defaultPhysicsScene))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞隔离失败：本地物理场景无效或等于默认物理世界，"
                                       + "拒绝入局（不回旧链）");
                    return;
                }

                Collider[] allowlist = new Collider[2];
                int allowCount;
                string allowError;
                if (!PMR3TestScene.TryCollectColliders(session.SceneObjects, allowlist, out allowCount, out allowError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞白名单不完整：" + allowError + "，拒绝入局（不回旧链）");
                    return;
                }

                int movementLayerMask = 0;
                for (int i = 0; i < allowCount; i++)
                {
                    if (allowlist[i] == null || allowlist[i].gameObject == null)
                    {
                        ReleaseSession(session);
                        HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞白名单第 " + i + " 项无效，拒绝入局（不回旧链）");
                        return;
                    }

                    movementLayerMask |= 1 << allowlist[i].gameObject.layer;
                }

                // R5-C：投射物运动复用同一套隔离物理场景与白名单（去掉未填充的槽位），
                // 但**不**复用 Mover 的查询实例：两者是各自独立的适配器（隔离语义不共享可变状态）。
                projectilePhysicsScene = movementPhysicsScene;
                projectileColliders = new Collider[allowCount];
                for (int i = 0; i < allowCount; i++) { projectileColliders[i] = allowlist[i]; }

                try
                {
                    session.Query = new PMUnityMoverCollisionQuery(movementPhysicsScene, movementLayerMask,
                                                                   session.SelectedWorldVersion, allowlist);
                }
                catch (Exception ex)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞查询构造失败：" + ex.GetType().Name + " " + ex.Message
                                       + "（不回旧链）");
                    return;
                }

                if (!session.Query.Scene.IsValid() || session.Query.Scene.Equals(Physics.defaultPhysicsScene))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞查询未绑定到隔离物理场景（isDefaultWorld="
                                       + session.Query.Scene.Equals(Physics.defaultPhysicsScene) + "），拒绝入局（不回旧链）");
                    return;
                }
            }

            // R5-C：诊断投射物链（Motion + Driver）。
            //
            // 客户端**不**建权威历史：history 是一个空实例，只作构造占位（本类从不 Record），
            // 因此它既不冒充 DS 历史，也不会产生第二套权威。
            if (projectileColliders == null || projectileColliders.Length == 0)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] 投射物碰撞白名单为空，拒绝入局（不回旧链）");
                return;
            }

            int projectileLayerMask = 0;
            for (int i = 0; i < projectileColliders.Length; i++)
            {
                if (projectileColliders[i] == null || projectileColliders[i].gameObject == null)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 投射物碰撞白名单第 "
                                       + i.ToString(CultureInfo.InvariantCulture) + " 项无效，拒绝入局（不回旧链）");
                    return;
                }

                projectileLayerMask |= 1 << projectileColliders[i].gameObject.layer;
            }

            session.ProjectileHistory = new PMProjectileHistory(offer.Epoch);
            session.ProjectileViewBuffer = new PMR5ProjectileView[MaxPresentationViews];

            try
            {
                session.ProjectileMotion = new PMUnityProjectileMotion(projectilePhysicsScene, projectileColliders,
                                                                      projectileLayerMask);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] 投射物运动适配器构造失败：" + ex.GetType().Name + " "
                                   + ex.Message + "（不回旧链）");
                return;
            }

            try
            {
                // policy = null（客户端不持有可信授权策略）；hostMotion = 上面那颗 motion。
                session.ProjectileDriver = new PMR5ProjectileDriver(session.World, session.Bridge, offer.Epoch,
                                                                   session.ProjectileHistory, null,
                                                                   session.ProjectileMotion);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] 投射物驱动构造失败：" + ex.GetType().Name + " " + ex.Message
                                   + "（不回旧链）");
                return;
            }

            session.PreviousProjectileWarn = PMR3Player.ProjectileWarn;
            PMR3Player.ProjectileWarn = delegate(string m)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] projectile: " + m);
            };

            // R6-C：会话级战斗网络驱动（**客户端 model = null**：AP 不持有权威核心）。
            //
            // 创建次序（契约「C 宿主接线冻结」）：R5 driver 先建，R6 driver 后建并绑定它；
            // 之后一个 player 的两条接缝由 `R6.BindPlayer` 一次性接好。
            //
            // 刻意**不**订阅 `PlayerDied`：那个事件只在权威侧（DS）由结算产生，AP 根本不会收到结算，
            // 订阅只会变成一条永不触发的死接线；客户端的死亡真值是复制字段 `CombatDead`
            //（见 UpdateCombatReplicationState）。
            try
            {
                session.CombatDriver = new PMR6CombatDriver(session.World, session.Bridge, offer.Epoch,
                                                           session.ProjectileDriver, null);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] R6 战斗驱动构造失败：" + ex.GetType().Name + " " + ex.Message
                                   + "（不回旧链）");
                return;
            }

            session.Input = new PMUnityMoverInput();

            // 旧链退役（契约 §B）：原第 5 步在这里调 `global::Server.UDPSocketManger.CloseExisting()`
            // 关掉硬编码 7777 的旧战斗 UDP socket。旧客户端 UDP 链已整条删除，桌面端不再有那条 socket，
            // 该触点连同门禁里的 stub 一并移除；新链 UDP 端点由下面的 OpenClient 自建。

            // 6) UDP 入局端点：验票相关的关联校验在端点内部完成（§7.2）。
            try
            {
                session.Endpoint = PMUdpSessionEndpoint.OpenClient(offer, session.Bridge);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] OpenClient 失败：" + ex.GetType().Name + " " + ex.Message
                                   + "（不回旧链）");
                return;
            }

            session.Endpoint.Failed += delegate(string reason) { OnEndpointFailed(reason); };

            // R6-C：只读战斗 HUD（客户端专有；DS 侧由 PMUnityCombatHud.Create 自己拒绝）。
            //
            // 失败即整局失败：静默地「没有 HUD」会让实机联调把「没显示」误读成「没数据」，
            // 而创建失败本身就意味着本端环境异常。
            try
            {
                string hudError;
                session.Hud = PMUnityCombatHud.Create(offer.MatchId, out hudError);
                if (session.Hud == null)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 只读战斗 HUD 创建失败：" + hudError + "（不回旧链）");
                    return;
                }
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] 只读战斗 HUD 创建异常：" + ex.GetType().Name + " " + ex.Message
                                   + "（不回旧链）");
                return;
            }

            _active = session;

            // static 事件是跨帧的：必须在这里订阅，并在 Stop 里退订（否则退出后再 Enter 会拿到旧宿主）。
            PMR3Runtime.PlayerReplicated += OnPlayerReplicated;

            PMClientSessionHostDriver.Ensure();

            HYLDDebug.Log("[PMClientSessionHost] ===== 新链客户端会话已建立 =====");
            HYLDDebug.Log("[PMClientSessionHost] match=" + offer.MatchId + " ds=" + offer.DsId
                          + " host=" + offer.Host + " port=" + offer.Port.ToString(CultureInfo.InvariantCulture)
                          + " epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                          + " hash=0x" + offer.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                          + " mode=" + (session.ContentFormal ? "formal" : "diagnostic")
                          + " digest=0x" + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                          + " worldVersion=" + session.SelectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                          + " 本地uid=" + offer.Identity.Uid.ToString(CultureInfo.InvariantCulture));
            HYLDDebug.Log("[PMClientSessionHost] 保留 Lobby TCP 长连接；旧战斗 UDP socket 已随旧链删除；"
                          + "输入=键盘 WASD/Space（移动）+ F 普通攻击 / G 大招（正式直线攻击，客户端预测 + DS 裁决）；"
                          + "等待握手激活");
            HYLDDebug.Log("[PMClientSessionHost] ================================");
        }

        /// <summary>
        /// 退出当前局（**显式 Stop**）：退订 static 事件、释放端点、摘除运行时接线、销毁场景对象，
        /// 并销毁保留中的只读终局 HUD 与清空结果快照。幂等。
        ///
        /// 它也是 `OnApplicationQuit` / driver `OnDestroy` 的收尾入口，因此「已无会话但还挂着
        /// 终局 HUD」的情况也要被它覆盖（否则退出时会把那个 DontDestroyOnLoad 对象带到进程外）。
        /// </summary>
        public static void Stop()
        {
            if (_stopping) { return; }

            _stopping = true;
            try
            {
                Session session = _active;
                _active = null;

                if (session != null)
                {
                    PMR3Runtime.PlayerReplicated -= OnPlayerReplicated;

                    if (session.Endpoint != null)
                    {
                        try { session.Endpoint.Dispose(); }
                        catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放端点异常：" + ex.GetType().Name); }
                    }

                    ReleaseSession(session);

                    PMClientSessionHostDriver.Release();

                    HYLDDebug.Log("[PMClientSessionHost] 已退出新链会话（match=" + session.Offer.MatchId + "）");
                }

                // 显式 Stop / 换局：只读终局 HUD 与结果快照到此为止（下一 Enter 也会清）。
                ReleaseRetainedHud();
                ClearLastCombatResult();
            }
            finally
            {
                _stopping = false;
            }
        }

        // =================================================================================
        //  主线程驱动
        // =================================================================================

        /// <summary>Unity Update 的唯一驱动点（由内部 driver 调用）。</summary>
        internal static void PumpActive()
        {
            Session session = _active;
            if (session == null) { return; }

            long nowMs = (long)(Time.realtimeSinceStartup * 1000f);
            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            session.TickCount++;

            if (session.Faulted)
            {
                // R6-C：终局已落定的会话不再「冻结挂着」。
                //
                // 为什么需要这条：失败标记可能发生在终局之后的某一帧（例如端点还在「看起来可用」时
                // 回 ACK 才崩），而契约要求「已知合法 terminal 结果后按正常路径退场」。
                // 若只走 FrozenWallClockPump，玩家会永远停在失败帧上，既回不了大厅也看不到终局 HUD。
                if (session.TerminalResultObserved)
                {
                    EndSessionNormally(session, "终局已落定（后续故障不再延续本会话）");
                    return;
                }

                // 失联/失败：必须 Freeze（Fail 里已冻结）。墙钟仍可推进（R4-A 的断线墙钟只在冻结时生效，
                // 也是唯一能恢复的路径），但**不再**发输入、不再推进表现，避免越界外推。
                FrozenWallClockPump(session);
                return;
            }

            // 复审 D5：端点自身 _disposed 置位后 Pump 直接 return，既不报 ClientFailed 也不跑
            // CheckClientSessionAfterUpdate。若不在这里兜底，就会出现「端点不再 Pump、宿主一直预测」的
            // 无告警静默通道。IsDisposed 是端点暴露的只读快照，正是为这个判据准备的。
            if (session.Endpoint == null || session.Endpoint.IsDisposed)
            {
                // R6-C：已拿到可信终局的路径下端点被释放 = DS 正常退场，**不当作 Fail**。
                if (session.TerminalResultObserved)
                {
                    EndSessionNormally(session, "端点已释放（终局后正常退场）");
                    return;
                }

                Fail(session, "UDP 端点已释放但会话未 Stop：拒绝继续预测（不静默挂着）");
                FrozenWallClockPump(session);
                return;
            }

            // R6-C：终局后的有界退场。
            //
            // 终局已落定但 DS 迟迟不关会话时，本端不能无限期地“冻结着等”：契约把 DS 侧的宽限
            // 定为 5000ms，这里再多给一点余量就自行按**正常路径**（非失败）退场回大厅。
            // **超时是有界退场，不是「客户端都已观察到结果」的证明。**
            if (session.TerminalResultObserved
                && nowMs - session.TerminalResultWallMs >= (double)TerminalExitGraceMs)
            {
                EndSessionNormally(session, "终局后等待 DS 退场超时（有界退场）");
                return;
            }

            // 契约 §B3：「主线程在 endpoint.Pump 前 Physics.SyncTransforms 一次」。
            // 适配器自己永不调它（契约 B2），因此这里是本端唯一调用点，且每帧只调一次；
            // 不能每条 resim/每次查询都扫一遍（那会把预测预算吃光）。
            Physics.SyncTransforms();

            session.Endpoint.Pump(nowMs, nowUnixSeconds);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            // 终局结果的两个出参在这里**提前声明**：端点失败分支（下面）也要用它做同一次「取出并定性」。
            uint terminalOutcomeId;
            int terminalWinnerTeamId;

            if (session.Endpoint.ClientFailed)
            {
                // ① 已知可信终局后的 DS 退场：正常结束，**不当作 Fail**。
                if (session.TerminalResultObserved)
                {
                    EndSessionNormally(session, "DS 正常退场（端点已关闭）");
                    return;
                }

                // ② 端点刚在本帧关闭：**先把已经入站的合法结果真正取出来**（R6.Pump），再定性。
                //
                // 次序不能反：本帧「DS 发结果 + 关会话」时，结果此刻还躺在 R6 的入站队列里
                // （R6.Pump 尚未运行）。先判失败就会把一份**已经到达**的合法终局降级成断线失败。
                PumpCombatInbound(session, nowMs);

                // 「可信终局」的**唯一**判据是「可靠结果已通过校验并幂等保存」，
                // 不是「收到过结果 RPC」的计数：那条计数在**入队那一刻**自增，包含被驱动拒绝
                // （outcomeId=0 / 未知胜方 / 结论冲突 → fault）或静默丢弃（非本人 owner / owner 未就位）
                // 的包。拿计数当判据的后果不是「误判胜利」而是更糟的**静默挂死**：
                // 真实断线时 fault 不置位 → 本帧不 Fail、`TerminalResultObserved` 也永不为真
                // → 会话停在 failed 端点上无限 Pump，而 `IsActive` 仍为真、运动不再冻结。
                if (session.CombatDriver != null
                    && session.CombatDriver.TryGetStoredMatchResult(out terminalOutcomeId, out terminalWinnerTeamId))
                {
                    OnTerminalResult(session, terminalOutcomeId, terminalWinnerTeamId, nowMs);

                    // 端点已不可用：这里跳过 ACK（发给没人收的 socket 只会把正常退场变成 SendFailed fault）。
                    FlushCombatState(session, nowMs);
                    UpdateCombatHud(session);

                    EndSessionNormally(session, "DS 正常退场（端点已关闭）");
                    return;
                }

                if (!session.Faulted)
                {
                    // 未收到可信终局的断网/握手失败：**仍然失败**（任何 disconnect 都不能当胜利）。
                    string disconnectReason = string.IsNullOrEmpty(session.PendingDisconnectReason)
                        ? "入局握手失败（对端不在本局/票据已过期/关联校验不通过）"
                        : session.PendingDisconnectReason;
                    Fail(session, disconnectReason);
                }

                FrozenWallClockPump(session);
                return;
            }

            // ---- R6-C：复制结论 → 死亡/终局冻结、身份核对（在任何驱动调用之前）----
            //
            // 值在这一帧的 `endpoint.Pump` 里刚被应用完，因此这里拿到的是**本帧最新**的复制快照。
            // 它只做两件事：冻结 Mover / 核对本人身份；不驱动任何 core（R6 时钟在后面）。
            UpdateCombatReplicationState(session);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            // 输入边沿每 Update **只采样一次**（与 Mover 边沿同纪律）。
            SampleCombatInputEdges(session);

            // ---- R6-C 固定次序：R6.Pump(now) → Movement → R5 子步（候选+表现）→ R6.FlushState(now) ----
            //
            // 同一根单调墙钟 now：三层 core（R6/PMCombat、R5、endpoint）全部用这个值，
            // 绝不“复用上一帧的 now”或“前一步得到更小的 now”，否则 core 会把倒退的时钟当成
            // 非法输入（NonMonotonicWallClock / InvalidClock）而丢伤害或整局 fault。
            PumpCombatInbound(session, nowMs);

            // 终局闭环（**早于任何 fault 短路与后续子链**）：`R6.Pump` 刚把可靠结果幂等保存下来，
            // 因此本帧立即把它落成只读事实。次序理由：一条**已经到达的合法终局**不得因为
            // 本帧后续（或同一帧内 R6 在保存结果之后）的某条子链出问题而被降级为一次失败退场。
            if (!session.TerminalResultObserved
                && session.CombatDriver != null
                && session.CombatDriver.TryGetStoredMatchResult(out terminalOutcomeId, out terminalWinnerTeamId))
            {
                OnTerminalResult(session, terminalOutcomeId, terminalWinnerTeamId, nowMs);

                // 端点还可用时补一次 ACK（AP 的 `FlushState` 唯一职责）；端点不可用时不发（发了必抛）。
                FlushCombatState(session, nowMs);

                if (session.Faulted)
                {
                    FrozenWallClockPump(session);
                    return;
                }

                UpdateCombatHud(session);

                if (session.Endpoint == null || session.Endpoint.IsDisposed || session.Endpoint.ClientFailed)
                {
                    EndSessionNormally(session, "DS 正常退场（端点已关闭）");
                }

                return;
            }

            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            // 次序：网络入站/校正（上面） → R6 命令/core.Tick → 采样/预测/插值 → 攻击+R5 子步
            //       → R6 状态复制与结果闭环 → 后续网络 flush（下一次 Pump）。
            PumpMovement(session);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            // R5-C/R6-C：投射物链必须排在 Mover **之后** —— 目标样本要来自本帧已经算完的呈现位姿，
            // 开火用的枪口/瞄准也要来自本帧的 predicted 位姿。
            PumpProjectiles(session, nowMs);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            FlushCombatState(session, nowMs);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            SendPendingProbes(session);

            UpdateCombatHud(session);

            if (Time.unscaledTime >= session.NextHeartbeatLogTime)
            {
                session.NextHeartbeatLogTime = Time.unscaledTime + HeartbeatLogIntervalSeconds;
                LogHeartbeat(session);
            }
        }

        // =================================================================================
        //  R4-B（B3）：运动主链
        // =================================================================================

        /// <summary>本帧真实经过的毫秒（非有限/负值归 0；不包含任何推测性平滑）。</summary>
        private static double CurrentFrameElapsedMs()
        {
            double elapsedMs = (double)Time.deltaTime * 1000.0;
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) || elapsedMs < 0.0)
            {
                return 0.0;
            }

            return elapsedMs;
        }

        /// <summary>
        /// 冻结后的墙钟推进：**只**调用 Driver.Update（消费入站 + 断线墙钟）。
        /// 不 Tick、不 SendInputPayload、不 Advance、不 Apply——这就是"墙钟可推进但
        /// 不继续发输入/不让表现越界"的落地形式。
        /// </summary>
        private static void FrozenWallClockPump(Session session)
        {
            double elapsedMs = CurrentFrameElapsedMs();

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Update(elapsedMs); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 冻结期墙钟推进异常：" + ex.GetType().Name);
                }
            }
        }

        /// <summary>每帧的运动驱动：全部副本 Update → AP 子步预测/发送 → SP 插值与表现。 </summary>
        private static void PumpMovement(Session session)
        {
            // R6-C：终局冻结（CombatMatchEnded）或失联冻结后，**不再**采样/预测/插值/发输入，
            // 只推墙钟让驱动消费入站（“冻结但不抢数据报”）。
            if (session.MovementFrozen)
            {
                FrozenWallClockPump(session);
                return;
            }

            if (session.Movements.Count == 0) { return; }

            double elapsedMs = CurrentFrameElapsedMs();

            // 1) 消费入站（所有角色；Driver 的步进入口也会自动 ApplyPending）。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Update(elapsedMs); }
                catch (Exception ex)
                {
                    Fail(session, "运动驱动 Update 异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }

            // 输入轮询早于步数决策：不足 1ms 的渲染帧也必须缓存边沿。
            session.Input.PollHardware();

            // 2) AP：真实整毫秒余数累加 → 1..50 子步，每 Update 最多 8 步。
            session.StepAccumulatorMs += elapsedMs;
            if (session.StepAccumulatorMs > MaxStepAccumulatorMs)
            {
                session.StepAccumulatorMs = MaxStepAccumulatorMs;
            }

            int steps = 0;
            while (steps < MaxSubstepsPerUpdate && session.StepAccumulatorMs >= MinStepMs)
            {
                int stepMs = (int)Math.Floor(session.StepAccumulatorMs);
                if (stepMs < MinStepMs) { stepMs = MinStepMs; }
                if (stepMs > MaxStepMs) { stepMs = MaxStepMs; }

                if (!TickAutonomousProxies(session, stepMs))
                {
                    // 冻结/历史耗尽/未确认窗口满：剩余时间留在累加器里，本帧不再推进。
                    // 这是故意的：既不能拆分 dt，也不能因为服务器跟不上就凭空丢掉玩家的输入。
                    break;
                }

                session.StepAccumulatorMs -= stepMs;
                steps++;
            }

            // 3) SP：只插值（不外推）。SamplePresentation 超出最新权威值时会钳到最新，
            //    因此“不能越界”由驱动层保证，表现层只负责写 Transform。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed || rig.Presentation == null)
                {
                    continue;
                }

                // R6-C：已按复制结论冻结的副本不推进表现时钟（停在最近权威位姿）。
                if (rig.DeathFrozen) { continue; }

                try
                {
                    rig.Driver.Advance(elapsedMs);
                    PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> sample = rig.Driver.SamplePresentation();
                    if (sample.HasValue)
                    {
                        rig.Presentation.ApplyInterpolated(sample.Sync);
                    }
                }
                catch (Exception ex)
                {
                    Fail(session, "SP 插值异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }

            // 4) AP：表现喂**预测输出**（与 SP 的写入面完全相同，写入点只有这一处）。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (!rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed || rig.Presentation == null)
                {
                    continue;
                }

                try { rig.Presentation.ApplyPredicted(rig.Driver.GetPredictedSync()); }
                catch (Exception ex)
                {
                    Fail(session, "AP 表现写入异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// 推进本端 AP 一个真实子步。返回 false 表示本帧不应再继续（冻结/历史耗尽/超限）。
        ///
        /// 关键契约（§B3 末段）：「Input 在 Tick 真正接受后 Consume 边沿，不能先消费再因冻结/超限失去按键」。
        /// 所以这里先 <see cref="PMUnityMoverInput.Sample"/>（**不**消费）→ Tick → 接受后才 Consume。
        /// </summary>
        private static bool TickAutonomousProxies(Session session, int stepMs)
        {

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (!rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                // R6-C：已按复制结论（CombatDead）冻结的副本**不再 Tick**。
                // 不靠驱动自身的 frozen 语义兼容：本宿主只调 Update（消费入站）+ 表现写入，
                // 「推进」类调用全部跳过，意图在调用点就写明。
                if (rig.DeathFrozen) { continue; }

                PMMoverInput input = session.Input.Sample(stepMs);

                PMTickResult result;
                try
                {
                    // AP 不生成 DS 帧号；沿用最后权威元数据（尚未建立则为 None）。
                    // 用只读访问器 PendingServerFrame（不 clone Sync/Aux）而不是 GetSnapshot()：
                    // 后者每个子步都会深克隆带数组的 Sync/Aux，在非增量 GC 下是可见尖峰。
                    PMFrameId serverFrame = rig.Driver.Timeline.PendingServerFrame;
                    result = rig.Driver.Tick(stepMs, input, serverFrame);
                }
                catch (Exception ex)
                {
                    Fail(session, "AP Tick 异常：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }

                if (!result.Accepted)
                {
                    // 不消费边沿（键还在缓冲里，下一个真实步再试）。
                    return false;
                }

                session.Input.Consume(stepMs);

                try { rig.Driver.SendInputPayload(); }
                catch (Exception ex)
                {
                    Fail(session, "上行输入发送异常：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }
            }

            return true;
        }

        /// <summary>为副本建运动链（AP/SP 都需要）；已建过或无法建立时安全返回。</summary>
        private static MovementRig EnsureMovementRig(Session session, PMR3Player player, bool isOwner)
        {
            for (int i = 0; i < session.Movements.Count; i++)
            {
                if (ReferenceEquals(session.Movements[i].Player, player))
                {
                    return session.Movements[i];
                }
            }

            if (session.Query == null)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 碰撞查询未建立，无法建立运动链（调用方会显式整局失败，不会以探针假成功掩盖）");
                return null;
            }

            string error;
            PMR4MovementDriver driver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                player, session.Query, session.Offer.Epoch, out error);

            if (driver == null)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 运动驱动建立失败（netId="
                                     + player.NetId.Value + " role=" + player.Role + "）：" + error
                                     + "（调用方会显式整局失败）");
                return null;
            }

            string label = "uid" + player.Uid + "/net" + player.NetId.Value;

            IRigPresentation presentation;
            try
            {
                if (session.ContentFormal)
                {
                    // R4-C（C3）正式：C1 烘培的正式角色表现（C2 在构造时校验组件集并拒 DS/Authority）。
                    // 只为本地 owner 建测试相机（契约 B2：可选、不强制）；相机的销毁由表现层自己负责。
                    PMUnityBattlePresentation battle = new PMUnityBattlePresentation(
                        label, player.Role, session.BattleMap.Manifest, isOwner);
                    presentation = new BattleRigPresentation(battle);

                    if (isOwner)
                    {
                        // “确保可见”：临时关闭其它已启用相机（防旧主相机遮住新角色），退局时恢复。
                        SuppressOtherCameras(session, battle.TestCamera);
                    }
                }
                else
                {
                    // 诊断：R4-B 的胶囊表现（保留已验烟测；不改两组公开 API）。
                    presentation = new MoverRigPresentation(
                        new PMUnityMoverPresentation(label, player.Role, isOwner));
                }
            }
            catch (Exception ex)
            {
                driver.Dispose();
                HYLDDebug.LogWarning("[PMClientSessionHost] 表现层建立失败（" + label + "）："
                                     + ex.GetType().Name + " " + ex.Message + "（调用方会显式整局失败）");
                return null;
            }

            MovementRig rig = new MovementRig();
            rig.Player = player;
            rig.Driver = driver;
            rig.Presentation = presentation;
            rig.IsOwner = isOwner;
            session.Movements.Add(rig);

            HYLDDebug.Log("[PMClientSessionHost] 运动链已建立 " + label
                          + " role=" + player.Role
                          + " owner=" + (isOwner ? 1 : 0)
                          + " presentation=" + (session.ContentFormal ? "battle" : "capsule")
                          + " 驱动=" + driver.Describe());
            return rig;
        }

        /// <summary>
        /// 副本创建后的本地接线：**所有** AP/SP 副本都要 Driver + 表现；上行探针仍只给本地 owner。
        ///
        /// 旧实现把 SP 直接跳过（`if (player.Role != AutonomousProxy) return;`），
        /// 结果就是"看不到别人动"——这是必须改掉的过滤。但探针语义不变：它只证明本端
        /// owner 的上行能被 DS 执行，不该替别人的角色发。
        /// </summary>
        private static void OnPlayerReplicated(PMR3Player player)
        {
            Session session = _active;
            if (session == null || session.Faulted || player == null) { return; }

            bool isOwner = player.Role == PMNetRole.AutonomousProxy;

            if (isOwner && player.Uid != session.Offer.Identity.Uid)
            {
                // 角色说是我的、uid 却说不是：**显式整局失败**，不能降为 SP。
                // 降级断点：工厂的角色取自 player.Role（AutonomousProxy），而宿主下面会按 !IsOwner 调
                // Advance → 驱动 EnsureRole(SimulatedProxy) 抛异常并报成“SP 插值异常”（误导）；
                // 另一半组合（owner 副本被判为 SP）则**静默**失去控制。两种都不接。
                Fail(session, "副本角色与 uid 不一致（role=" + player.Role
                     + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                     + " 期望 " + session.Offer.Identity.Uid.ToString(CultureInfo.InvariantCulture)
                     + "）：拒绝降级为观察副本");
                return;
            }

            if (isOwner)
            {
                // 契约 §B3：输入是**每会话一份**，一个客户端只应有一个 AP。
                // 否则同一按键会被两个 rig 各消费一次（近似双驱动同一输入）。
                if (session.OwnerPlayer != null && !ReferenceEquals(session.OwnerPlayer, player))
                {
                    Fail(session, "同一局出现第二个 AP 副本（已持有 uid="
                         + session.OwnerPlayer.Uid.ToString(CultureInfo.InvariantCulture)
                         + " netId=" + session.OwnerPlayer.NetId.Value.ToString(CultureInfo.InvariantCulture)
                         + "，又收到 uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                         + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                         + "）：拒绝多 AP 共享同一份输入");
                    return;
                }

                session.OwnerPlayer = player;

                // R5-C：候选收集器需要本地 owner 的 **NetId**（owner 过滤是构造入参；uid 不是 NetId），
                // 因此只在拿到 AP 副本之后构造；在此之前不收集任何候选。
                if (session.CandidateCollector == null)
                {
                    session.CandidateCollector = new PMProjectileCandidateCollector(
                        session.Offer.Epoch, player.NetId.Value);
                }

                // R6-C：拿 AP 的那一刻就核对一次身份（复制创建包里已经带了初值 CombatHeroId/TeamId）。
                // 若此刻尚未就位则提前返回、下一帧的 UpdateCombatReplicationState 会再核一遍。
                VerifyOwnerIdentity(session);
                if (session.Faulted) { return; }
            }

            MovementRig rig = EnsureMovementRig(session, player, isOwner);
            if (rig == null)
            {
                // 不能“只告警、照常发探针”：那会让探针 Echo 成功掩盖“运动链实际没接上”。
                Fail(session, "运动链建立失败（netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                     + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                     + " role=" + player.Role + "）：拒绝以探针假成功掩盖");
                return;
            }

            // R5-C：把该副本接到会话级投射物驱动上（AP/SP **都要**接：SP 也要能收到权威镜像）。
            if (!EnsureProjectileBinding(session, player))
            {
                Fail(session, "投射物接线失败（netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                     + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                     + " role=" + player.Role + "）：拒绝以探针假成功掩盖");
                return;
            }

            if (!isOwner)
            {
                return;   // 非本地 owner：只需表现（已在上面建），不发探针。
            }

            if (player.ProbeSentCount > 0) { return; }

            for (int i = 0; i < session.PendingProbes.Count; i++)
            {
                if (ReferenceEquals(session.PendingProbes[i], player)) { return; }
            }

            session.PendingProbes.Add(player);
            session.KnownPlayers.Add(player);

            HYLDDebug.Log("[PMClientSessionHost] 本地副本已创建 netId="
                          + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                          + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                          + "，排队发送一次上行探针");
        }

        /// <summary>
        /// 发送排队中的探针。
        ///
        /// **刻意放在复制回调之外**：`OnPlayerReplicated` 发生在入站 drain 之中（endpoint.Pump 内部），
        /// 那时直接发送会重入桥；改在本帧的 Pump 里发，既只发一次，也不重入。
        /// </summary>
        private static void SendPendingProbes(Session session)
        {
            if (session.PendingProbes.Count == 0) { return; }

            for (int i = 0; i < session.PendingProbes.Count; i++)
            {
                PMR3Player player = session.PendingProbes[i];
                if (player == null) { continue; }
                if (player.ProbeSentCount > 0) { continue; }

                if (player.State != PMNetObjectState.Active)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 本地副本已不 Active，跳过探针 netId="
                                         + player.NetId.Value.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                session.ProbeNonce++;
                int nonce = session.ProbeNonce;

                player.ProbeSentCount++;

                // 经**声明层入口**发（普通名 `ServerProbe`），不手写业务状态包（§7.4）。
                player.ServerProbe(nonce);

                HYLDDebug.Log("[PMClientSessionHost] 已发送 ServerProbe nonce=" + nonce.ToString(CultureInfo.InvariantCulture)
                              + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture));
            }

            session.PendingProbes.Clear();
        }

        // =================================================================================
        //  R5-C：诊断投射物链（网络驱动 + 候选收集 + 薄表现）
        // =================================================================================

        /// <summary>
        /// 共享诊断 spec 的只读模板。
        ///
        /// 只用于读常量（`HideOnStop`）；开火时直接把它交给驱动，驱动内部会 `Clone()`，
        /// 因此本对象不会被任何一方改写（不是「第二套配置」，它**就是**
        /// `PMProjectileDiagnosticConfig.CreateSpec()` 的产物）。
        /// </summary>
        private static readonly PMProjectileSpec DiagnosticSpecTemplate =
            PMProjectileDiagnosticConfig.CreateSpec();

        /// <summary>
        /// 把一个副本同时接到 R5 投射物驱动与 R6 战斗驱动上（幂等；失败即由调用方整局失败，
        /// 不静默降级）。
        ///
        /// 为什么要在一个函数里接两条缝：一个 player 的「上行投射物声明」与「上行攻击声明」
        /// 必须同生同死；只接一侧会出现「能收到权威镜像但发不出攻击声明」这种半接状态。
        /// <c>R6.BindPlayer</c> 内部也会绑 R5，但这里先显式绑 R5 并校验结果，
        /// 避免「R6 接上了、R5 没接上」却返回成功的假绿灯。
        /// </summary>
        private static bool EnsureProjectileBinding(Session session, PMR3Player player)
        {
            PMR5ProjectileDriver driver = session != null ? session.ProjectileDriver : null;
            if (driver == null || player == null) { return false; }
            if (driver.IsFaulted || driver.IsDisposed) { return false; }

            if (!driver.IsPlayerBound(player) && !driver.BindPlayer(player)) { return false; }

            PMR6CombatDriver combat = session.CombatDriver;
            if (combat == null)
            {
                // R6 驱动未建立（构造失败已在 Enter 里单独报错并释放）；此处不算成功。
                return false;
            }

            if (combat.IsFaulted || combat.IsDisposed) { return false; }
            if (!combat.IsPlayerBound(player) && !combat.BindPlayer(player)) { return false; }

            return true;
        }

        /// <summary>
        /// 投射物/攻击链一帧：已有副本补绑定 → 目标样本 → 正式直线攻击（F/G）→ 固定 16ms 子步（≤ 8）
        /// → 每子步（视图/表现 + 候选收集与上报）。
        ///
        /// **零步也 Pump(0)**：裁决下行、墓碑清理与视图重建不能因为「这一帧不是 16ms 的整数倍」而停摆。
        ///
        /// R6-C：终局冻结（CombatMatchEnded）后仍然按 **Pump(0)** 继续推协议（排裁决/清墓碑/重建视图），
        /// 但不再推进运动、不再开火、不再收集候选 —— 这就是「冻结但继续协议 Pump 等待退出」。
        /// </summary>
        private static void PumpProjectiles(Session session, double wallNowMs)
        {
            PMR5ProjectileDriver driver = session.ProjectileDriver;
            if (driver == null) { return; }

            if (driver.IsFaulted || driver.IsDisposed)
            {
                Fail(session, "投射物驱动不可用（faulted=" + driver.IsFaulted.ToString(CultureInfo.InvariantCulture)
                     + " disposed=" + driver.IsDisposed.ToString(CultureInfo.InvariantCulture)
                     + " reason=" + driver.FaultReason + " " + driver.FaultError + "）");
                return;
            }

            // 已有副本也要绑定：OnPlayerReplicated 只在复制那一刻触发（本帧之前就存在的副本可能漏接）。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig == null || rig.Player == null) { continue; }

                if (!EnsureProjectileBinding(session, rig.Player))
                {
                    Fail(session, "投射物/战斗接线失败（netId="
                         + rig.Player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                         + "）：拒绝继续推进投射物");
                    return;
                }
            }

            // 目标样本：必须在本帧 Mover 已算完（PumpMovement 已在前面）之后采集。
            BuildProjectileTargets(session);

            // 终局冻结：只推协议，不推动、不开火、不收集候选（累加器也清零，避免之后残留追赶）。
            if (session.MatchEndedFrozen || session.MovementFrozen)
            {
                session.ProjectileAccumulatorMs = 0.0;
                session.ProjectileStepsThisFrame = 0;

                PumpProjectileOnce(session, wallNowMs, 0);
                DrainProjectileViewChanges(session);
                return;
            }

            TryCombatAttack(session, wallNowMs);
            if (session.Faulted) { return; }

            session.ProjectileAccumulatorMs += CurrentFrameElapsedMs();
            double maxAccumulatorMs = (double)(ProjectileMaxStepsPerUpdate * ProjectileStepMs);
            if (session.ProjectileAccumulatorMs > maxAccumulatorMs)
            {
                // 落后太多时宁可丢时间，也不在一帧里追赶出巨量子步（与 Mover 累加器同纪律）。
                session.ProjectileAccumulatorMs = maxAccumulatorMs;
            }

            int steps = 0;
            while (steps < ProjectileMaxStepsPerUpdate
                   && session.ProjectileAccumulatorMs >= (double)ProjectileStepMs)
            {
                session.ProjectileAccumulatorMs -= (double)ProjectileStepMs;
                steps++;
            }

            session.ProjectileStepsThisFrame = steps;

            if (steps == 0)
            {
                PumpProjectileOnce(session, wallNowMs, 0);
            }
            else
            {
                for (int i = 0; i < steps; i++)
                {
                    PumpProjectileOnce(session, wallNowMs, ProjectileStepMs);
                    if (session.Faulted) { break; }
                }
            }

            // 本帧所有子步产生的视图脏标记都在这里被消费掉（见本方法说明）。
            DrainProjectileViewChanges(session);
        }

        /// <summary>
        /// 排空驱动的「视图增量」通道（<see cref="PMR5ProjectileDriver.DrainViewChanges"/>）。
        ///
        /// 本宿主的真值是**全量** <c>CopyViews</c>（见 <see cref="SyncProjectileViews"/> 的差集判据），
        /// 因此这里**只排空、不使用**增量结果。
        ///
        /// 为什么必须排空：驱动内部那条脏通道是按 key 累积的，没有消费者时已退休的 key 会一直留在
        /// `_dirtyViewOrder` / `_dirtyViewSet` 里；累计到 <c>MaxDirtyViews</c>(4096) 后驱动只能整体
        /// 作废重来（`ViewDirtyOverflows` 无界增长，且脏集合常驻几千个已退休 key）。排空本身不产生
        /// 第二次「真值」：每次调用只按**实际脏条数**分配，稳态每帧几条，代价与变化量同阶且有界。
        /// </summary>
        private static void DrainProjectileViewChanges(Session session)
        {
            PMR5ProjectileDriver driver = session.ProjectileDriver;
            if (driver == null) { return; }

            PMR5ProjectileView[] drained;
            // max 取驱动的脏集合上限：单次调用即可把稳态与任何积压全部取走。
            driver.DrainViewChanges(PMR5ProjectileDriver.MaxDirtyViews, out drained);
        }

        /// <summary>一个投射物子步：Pump → fault 检查 → 视图/表现同步 → 候选收集与上报。</summary>
        private static void PumpProjectileOnce(Session session, double wallNowMs, int stepMs)
        {
            PMR5ProjectileDriver driver = session.ProjectileDriver;
            if (driver == null) { return; }

            try
            {
                driver.Pump(wallNowMs, stepMs);
            }
            catch (Exception ex)
            {
                Fail(session, "投射物 Pump 异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            session.ProjectilePumps++;

            if (driver.IsFaulted)
            {
                Fail(session, "投射物驱动失败：" + driver.FaultReason + " " + driver.FaultError);
                return;
            }

            if (!SyncProjectileViews(session)) { return; }

            // R6-C：终局冻结后停收集/上报（但视图仍同步、协议仍 Pump）。
            if (session.MatchEndedFrozen || session.MovementFrozen) { return; }

            CollectAndSubmitProjectileHits(session, wallNowMs);
        }

        /// <summary>
        /// 把驱动视图同步到薄表现字典（**有界**）：新增即建、已存在即 Apply、消失即 Dispose。
        ///
        /// 为什么用 `CopyViews`（全量）而不是 `DrainViewChanges`（增量）：后者对「视图消失」给出的是
        /// `Hidden + Stopped` 的墓碑（契约明说「不能依据 Hidden 判断 Destroyed」），
        /// **全量集合的差集**才是「该 key 真的不在了」的唯一可靠判据。
        ///
        /// 与增量的关系：增量通道每帧仍被**排空**（见 <see cref="DrainProjectileViewChanges"/>），
        /// 但结果不参与任何判定 —— 排空只为不让驱动内部那条按 key 累积的脏集合把已退休 key 留住。
        ///
        /// 容量：全量快照一旦被缓冲截断，就会有视图既拿不到表现、也不参与候选收集，
        /// 因此容量与驱动的对象容量对齐，真超过即整局失败（不截断、不静默丢弃）。
        /// </summary>
        private static bool SyncProjectileViews(Session session)
        {
            PMR5ProjectileDriver driver = session.ProjectileDriver;
            PMR5ProjectileView[] buffer = session.ProjectileViewBuffer;
            if (driver == null || buffer == null) { return true; }

            int n = driver.CopyViews(buffer);
            session.ProjectileViewCount = n;

            // 截断判据必须用驱动**真实的**视图数：`n == buffer.Length` 只说明「缓冲刚好填满」，
            // 并不说明被截断。真截断 = 有视图既不建表现、也不参与候选收集（静默丢视图），
            // 契约明令禁止 —— 这里显式整局失败（容量已与 PMProjectileLimits.MaxProjectiles 对齐）。
            int liveViews = driver.ViewCount;
            if (liveViews > buffer.Length)
            {
                Fail(session, "投射物视图数 " + liveViews.ToString(CultureInfo.InvariantCulture)
                     + " 超过宿主容量 " + buffer.Length.ToString(CultureInfo.InvariantCulture)
                     + "（对齐 PMProjectileLimits.MaxProjectiles）：不得静默丢视图与候选");
                return false;
            }

            // 本帧快照的全部 key（含下面因尺寸非法而跳过表现的），退休判定与表现建解耦。
            session.ProjectileViewSeen.Clear();
            for (int i = 0; i < n; i++) { session.ProjectileViewSeen.Add(buffer[i].Key); }

            // 先退休「本帧快照里已经没有」的 key：表现 Dispose + 候选账 Forget + 追踪集移除。
            // **必须先移除再新建**：同一帧整体换一批弹时，先建后删会先撞上表现字典上限。
            session.ProjectileViewRemovals.Clear();
            foreach (PMProjectileKey tracked in session.ProjectileTrackedKeys)
            {
                if (!session.ProjectileViewSeen.Contains(tracked)) { session.ProjectileViewRemovals.Add(tracked); }
            }

            for (int i = 0; i < session.ProjectileViewRemovals.Count; i++)
            {
                PMProjectileKey goneKey = session.ProjectileViewRemovals[i];
                session.ProjectileTrackedKeys.Remove(goneKey);

                ProjectileViewEntry gone;
                if (session.ProjectileViews.TryGetValue(goneKey, out gone))
                {
                    if (gone != null && gone.Presentation != null)
                    {
                        try { gone.Presentation.Dispose(); }
                        catch (Exception ex)
                        {
                            HYLDDebug.LogWarning("[PMClientSessionHost] 释放投射物表现异常：" + ex.GetType().Name);
                        }
                    }

                    session.ProjectileViews.Remove(goneKey);
                }

                // 视图移除 → 候选去重账同步退休（契约：view 移除/退出清）。
                // **没有表现的 key 同样必须 Forget**：否则它的账（每 key 最多 100 目标）永久占住。
                if (session.CandidateCollector != null) { session.CandidateCollector.Forget(goneKey); }
            }

            for (int i = 0; i < n; i++)
            {
                PMR5ProjectileView view = buffer[i];

                // 先记账：这颗弹本帧能不能建表现都要被追踪（否则退休时 Forget 会漏掉它）。
                session.ProjectileTrackedKeys.Add(view.Key);

                // 尺寸非法（例如镜像快照尚未带来 spec）：跳过表现并计数，
                // **不**拿默认值冒充尺寸 —— 表现层是 fail loud，写坏值会让整局失败。
                if (float.IsNaN(view.RadiusM) || float.IsInfinity(view.RadiusM) || view.RadiusM <= 0f)
                {
                    session.ProjectileViewSkipped++;
                    continue;
                }

                ProjectileViewEntry entry;
                if (!session.ProjectileViews.TryGetValue(view.Key, out entry) || entry == null)
                {
                    if (session.ProjectileViews.Count >= MaxPresentationViews)
                    {
                        // 上面已保证 liveViews ≤ 容量，因此走到这里只可能是宿主自身记账错乱；
                        // 静默跳过会让这颗弹变成「看不见的幽灵弹」，按契约改为显式整局失败。
                        session.ProjectileViewOverflows++;
                        Fail(session, "投射物表现字典已达容量上限 "
                             + MaxPresentationViews.ToString(CultureInfo.InvariantCulture)
                             + "（不得静默丢视图）");
                        return false;
                    }

                    try
                    {
                        entry = new ProjectileViewEntry();
                        entry.Presentation = PMUnityProjectilePresentation.Create(ProjectileLabel(view.Key));
                    }
                    catch (Exception ex)
                    {
                        Fail(session, "投射物表现创建失败：" + ex.GetType().Name + " " + ex.Message);
                        return false;
                    }

                    session.ProjectileViews.Add(view.Key, entry);
                }

                // 复用同一对 state/spec 就地覆写（表现层不持久化它们，语义等价于每次新建）。
                entry.State.Key = view.Key;
                entry.State.AuthorityNetId = view.AuthorityNetId;
                entry.State.Position = view.Position;
                entry.State.PreviousPosition = view.PreviousPosition;
                entry.State.Velocity = view.Velocity;
                entry.State.Yaw = view.Yaw;
                entry.State.MoveTimeMs = view.MoveTimeMs;
                entry.State.Hidden = view.Hidden;
                entry.State.Stopped = view.Stopped;

                entry.Spec.RadiusM = view.RadiusM;
                entry.Spec.HideOnStop = DiagnosticSpecTemplate.HideOnStop;

                try
                {
                    // Hidden / Stopped 只走 Apply（**没有**任何一次性副作用）。
                    entry.Presentation.Apply(entry.State, entry.Spec);
                }
                catch (Exception ex)
                {
                    Fail(session, "投射物表现写入失败：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 采集本帧目标样本：**只**来自其他副本**本帧真实呈现的**状态，
        /// 并携带该副本当前输入流版本与其展示快照的 **AuthorityServer 帧**：
        /// 帧锚取自真实权威帧，**不拿本地 input 帧冒充**。
        /// </summary>
        private static void BuildProjectileTargets(Session session)
        {
            session.ProjectileTargets.Clear();

            double worldNowMs = (double)Time.realtimeSinceStartup * 1000.0;

            PMR3Player owner = session.OwnerPlayer;
            int localTeamId = owner != null ? owner.CombatTeamId : 0;

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig == null || rig.IsOwner) { continue; }   // 自己不是自己的目标
                if (rig.Player == null || rig.Driver == null || rig.Driver.IsDisposed) { continue; }
                if (!rig.Player.NetId.IsValid) { continue; }

                // R6-C①：**死者不进候选**（协议拒绝 TargetNotAlive，本地先滤即不浪费上行配额）。
                // 真值来自 DS 复制字段：客户端不做任何存活裁决，DS 仍是最终裁决者。
                if (rig.Player.CombatDead)
                {
                    session.TargetsSkippedDead++;
                    continue;
                }

                // R6-C②：同队可据**复制**的 teamId 提前滤（契约 C：「team 可据复制提前滤但 DS 最终判」）。
                // 只在两侧都是**已知队伍**（> 0）时过滤：未就位时不得把「未知」当成「同队」而误滤。
                if (localTeamId > 0 && rig.Player.CombatTeamId == localTeamId)
                {
                    session.TargetsSkippedTeam++;
                    continue;
                }

                PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> sample = rig.Driver.SamplePresentation();
                if (!sample.HasValue) { continue; }

                // 锚必须是权威帧：非 AuthorityServer 的帧一律不作为目标（fail closed）。
                if (!sample.ServerFrame.IsValid || sample.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
                {
                    continue;
                }

                if (!sample.Sync.Position.IsFinite) { continue; }

                float scale = sample.Sync.Scale;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) { continue; }

                PMProjectileTargetSample target = default(PMProjectileTargetSample);
                target.Epoch = session.Offer.Epoch;
                target.NetId = rig.Player.NetId.Value;
                target.StreamVersion = rig.Driver.StreamVersion;      // 目标当前输入流（DS 要求同流）
                target.ServerFrame = sample.ServerFrame;              // 帧锚 = 展示快照的权威帧
                target.OutputFrame = sample.OutputFrame;
                target.TotalSimTimeMs = sample.TotalSimTimeMs;
                target.WorldTimeMs = worldNowMs;
                target.Position = sample.Sync.Position;
                target.RadiusM = PMMoverDefaults.CapsuleRadiusMeters * scale;          // 含 Sync.Scale
                target.HalfHeightM = PMMoverDefaults.CapsuleHalfHeightMeters * scale;  // 含 Sync.Scale
                target.Teleported = false;

                // 新链本批的目标存活真值：**DS 复制字段**（不是本地推测）。
                // 上面已把死者滤掉，因此走到这里必为存活；保留该字段是因为命中协议本身就要求它，
                // 而 DS 侧还会用 model.Dead / Connected 复核一遍。
                target.Alive = !rig.Player.CombatDead;

                session.ProjectileTargets.Add(target);
            }
        }

        /// <summary>
        /// R6-C：每 Update **只采样一次**的 F（普通攻击）/ G（大招）边沿。
        ///
        /// 与 Mover 输入边沿同纪律：不在子步里重复采样（否则一次按键会被多个子步各当成一次新按下），
        /// 也不在冻结帧里静静地把按键吃掉（消费点在 <see cref="TryCombatAttack"/> 里，先确认本帧能尝试才消费）。
        /// </summary>
        private static void SampleCombatInputEdges(Session session)
        {
            if (Input.GetKeyDown(KeyCode.F)) { session.NormalAttackEdgeBuffered = true; }
            if (Input.GetKeyDown(KeyCode.G)) { session.SuperAttackEdgeBuffered = true; }
        }

        /// <summary>
        /// R6-C：正式直线攻击（F = 普通攻击，G = 大招）。**取代**原来的 F 键诊断单发弹。
        ///
        /// 链路（契约「B 整合与宿主」原文）：
        ///   共用 planner 建本地计划 → **先发** ServerCombatAttackV1 → 再按计划逐颗 R5.TryFire
        ///   （**不等**服务器批准；DS 的拒绝经 ClientCombatAttackResultV1 回来，
        ///   届时由 R6 驱动真实撤销该 activation 的假弹）。
        ///
        /// 两条本地门（**都只是优化，不写任何权威状态**）：
        ///   · 身份/资源就绪（hero/team 与 offer 一致、MaxHp 已复制、未死/未终局）；
        ///   · `plan.FireIntervalMs`（planner 口径）替代原来的 200ms 诊断固定门。
        /// 本地 planner 拒绝（抛物线/无子弹型大招/未知英雄/非法方向）与 DS 裁决拒绝（蓝/能量/间隔…）
        /// **只显示不 fault**；只有 `driver.IsFaulted` 才是整局失败。
        /// </summary>
        private static void TryCombatAttack(Session session, double wallNowMs)
        {
            if (!session.NormalAttackEdgeBuffered && !session.SuperAttackEdgeBuffered) { return; }

            // 边沿纪律与 Mover 输入一致：**先确认本帧真的能尝试开火，再消费边沿**，
            // 否则冻结帧/失去 AP 帧会把按键静静地吃掉。
            if (session.Faulted || session.MovementFrozen || session.MatchEndedFrozen) { return; }

            PMR6CombatDriver combat = session.CombatDriver;
            if (combat == null)
            {
                Fail(session, "R6 战斗驱动未建立：拒绝在没有攻击通道的情况下继续预测");
                return;
            }

            if (combat.IsFaulted || combat.IsDisposed)
            {
                Fail(session, "R6 战斗驱动不可用（faulted=" + combat.IsFaulted.ToString(CultureInfo.InvariantCulture)
                     + " disposed=" + combat.IsDisposed.ToString(CultureInfo.InvariantCulture)
                     + " reason=" + combat.FaultReason + " " + combat.FaultError + "）");
                return;
            }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null) { return; }

            MovementRig rig = FindMovementRig(session, owner);
            if (rig == null || rig.Driver == null || rig.Driver.IsDisposed) { return; }

            // 到这里才消费这次按键边沿（后续所有分支都是「本次尝试」的结局）。
            bool isSuper = session.SuperAttackEdgeBuffered;
            session.NormalAttackEdgeBuffered = false;
            session.SuperAttackEdgeBuffered = false;

            session.AttackAttempts++;

            string readyError;
            if (!IsCombatFireReady(session, owner, out readyError))
            {
                // 状态未就绪（身份未核对/未收到 HP 复制/已死/已终局）：**明确禁开火**，只显示。
                session.AttackDriverRejected++;
                session.LastAttackError = readyError;
                return;
            }

            PMMoverSyncState predicted = rig.Driver.GetPredictedSync();
            if (!predicted.Position.IsFinite || float.IsNaN(predicted.YawDegrees)
                || float.IsInfinity(predicted.YawDegrees))
            {
                session.AttackDriverRejected++;
                session.LastAttackError = "预测位姿非法（position/yaw 非 finite），本次不开火";
                return;
            }

            // 「Yaw 决定世界前向」：与表现层 Quaternion.Euler(0, yaw, 0) 同一口径。
            // 世界瞄准方向**只能**来自 Mover 真实 predicted yaw（不接旧输入链的假方向）。
            double yawRad = (double)predicted.YawDegrees * Math.PI / 180.0;
            PMVector3 forward = new PMVector3((float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
            if (!forward.IsFinite)
            {
                session.AttackDriverRejected++;
                session.LastAttackError = "瞄准方向非 finite，本次不开火";
                return;
            }

            // 双方同源：客户端用**同一份** planner 建立本地计划（DS 的 core 内部再复核一遍）。
            PMCombatAttackPlan plan;
            PMCombatRejectReason planReason;
            if (!PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId, isSuper, forward.X, forward.Z,
                                               out plan, out planReason) || plan == null)
            {
                // UnsupportedAttack（抛物线 / 无子弹型大招）/ InvalidHero / InvalidAim：**不 fault**，不算整局失败。
                session.AttackPlannerRejected++;
                session.LastAttackError = "本地攻击计划被拒：" + planReason;
                return;
            }

            // 客户端门＝ planner 的 FireIntervalMs（替代原 200ms 诊断固定门）。
            // 共享的只是「间隔口径」；本端**不**扣 HP/Mana/Energy，不新增第三权威。
            if (wallNowMs - session.LastAttackWallMs < (double)plan.FireIntervalMs)
            {
                session.AttackGateBlocked++;
                session.LastAttackError = "开火间隔未到（planner 门 "
                    + plan.FireIntervalMs.ToString(CultureInfo.InvariantCulture) + "ms）";
                return;
            }

            // 同一次攻击的 N 颗弹**共用同一个枪口**：预测位置 + yaw 前向 × 0.6m。
            PMVector3 origin = predicted.Position + forward * PMProjectileDiagnosticConfig.MuzzleOffsetM;
            if (!origin.IsFinite)
            {
                session.AttackDriverRejected++;
                session.LastAttackError = "枪口位置非 finite，本次不开火";
                return;
            }

            uint activationId;
            string error;
            bool fired;
            try
            {
                fired = combat.TryAttack(owner, isSuper, forward.X, forward.Z, origin,
                                        CombatAttackPredictionMs, wallNowMs, out activationId, out error);
            }
            catch (Exception ex)
            {
                Fail(session, "R6 攻击调用异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (combat.IsFaulted)
            {
                Fail(session, "R6 战斗驱动失败：" + combat.FaultReason + " " + combat.FaultError);
                return;
            }

            if (!fired)
            {
                // 非致命拒绝（含 planner 之外的未就绪/部分预测失败前已 fault 的分支）：只显示，不 fault。
                session.AttackDriverRejected++;
                session.LastAttackError = error == null ? "攻击被拒（未给原因）" : error;
                return;
            }

            session.AttackFiresAccepted++;
            session.LastAttackWallMs = wallNowMs;
            session.LastAttackError = null;
        }

        /// <summary>
        /// 逐子步收集「自己预测来源」的候选并**经同一条生成声明通道**上报
        /// （先命中生成，再 `SubmitPredictedHits`）。
        ///
        /// 判定只看 **owner + origin**，**不看** `LocalFake` / `TakenOver`：
        /// 镜像接管后 `LocalFake` 会变成 false，但那并不改变「这颗弹是我自己预测出来的」；
        /// 按 `LocalFake` 停报会让自己的预测来源在接管瞬间永久失去命中。
        /// 已停止的弹只允许最后一段（由收集器的 StopSegment 规则与去重账保证有界）。
        /// </summary>
        private static void CollectAndSubmitProjectileHits(Session session, double wallNowMs)
        {
            PMProjectileCandidateCollector collector = session.CandidateCollector;
            PMR5ProjectileDriver driver = session.ProjectileDriver;
            PMR3Player owner = session.OwnerPlayer;
            PMR5ProjectileView[] buffer = session.ProjectileViewBuffer;

            if (collector == null || driver == null || owner == null || buffer == null) { return; }
            if (session.ProjectileTargets.Count == 0) { return; }

            uint ownerNetId = owner.NetId.Value;
            int n = session.ProjectileViewCount;

            for (int i = 0; i < n; i++)
            {
                PMR5ProjectileView view = buffer[i];

                // owner + origin 是唯二判据（对 ServerDirect / 非本地 owner 一律不上报）。
                if (view.Key.Origin != PMProjectileOrigin.ClientPredicted) { continue; }
                if (view.Key.OwnerNetId != ownerNetId) { continue; }

                // 该 key 的上行 Verify 总配额已用尽：驱动**必然**拒绝本次上报（配额按 key 计、
                // 不因分包重置），继续逐子步收集/编码只是空转，故直接跳过并计数（可观测，不是静默丢弃）。
                if (driver.UplinkVerifyUsed(view.Key) >= PMR5ProjectileDriver.MaxVerifyPerKey)
                {
                    session.ProjectileHitQuotaSkipped++;
                    continue;
                }

                PMProjectileCandidateSegment segment = new PMProjectileCandidateSegment();
                segment.Key = view.Key;
                segment.PreviousPosition = view.PreviousPosition;
                segment.Position = view.Position;
                segment.RadiusM = view.RadiusM;

                // 终态判据：权威已停止（Stopped）或本地已结束/隐藏 —— 两者都表示「这段是收官段」。
                segment.Stopped = view.Stopped || view.Hidden;

                session.ProjectileCandidates.Clear();
                int collected = collector.Collect(segment, session.ProjectileTargets,
                                                 session.ProjectileCandidates);
                if (collected <= 0) { continue; }

                PMProjectileHitBatch batch = new PMProjectileHitBatch();
                batch.Key = view.Key;
                batch.PreviousPosition = segment.PreviousPosition;
                batch.HitPosition = segment.Position;

                // 候选自带 AuthorityServer 帧锚 + 目标当前 stream，因此本批不要求时间回溯；
                // 真正的 rewind（按实测 RTT 折算）留给后续批次 —— 本批不做，也不假装做了。
                batch.RewindMs = 0;
                batch.Targets = session.ProjectileCandidates.ToArray();

                int rpcCount;
                string error;
                bool sent;
                try
                {
                    // 唯一发送入口：生成的 ServerProjectileHitV1（不新造 socket / 不手写 MainPack）。
                    sent = driver.SubmitPredictedHits(owner, batch, wallNowMs, out rpcCount, out error);
                }
                catch (Exception ex)
                {
                    Fail(session, "投射物命中上报异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }

                if (driver.IsFaulted)
                {
                    Fail(session, "投射物命中上报失败：" + driver.FaultReason + " " + driver.FaultError);
                    return;
                }

                if (sent)
                {
                    // 只有**发送成功**才把在途候选并入去重账。
                    collector.Commit(view.Key);
                    session.ProjectileHitRpcs += rpcCount;
                }
                else
                {
                    // 失败发送**不得永久预占去重**：回滚在途候选，后续子步可以重试。
                    collector.Rollback(view.Key);
                    session.ProjectileHitReportRejected++;

                    if (!string.Equals(session.LastHitReportError, error, StringComparison.Ordinal))
                    {
                        session.LastHitReportError = error;
                        HYLDDebug.LogWarning("[PMClientSessionHost] 投射物候选上报被拒（最终裁决仍在 DS）：" + error);
                    }
                }
            }
        }

        private static MovementRig FindMovementRig(Session session, PMR3Player player)
        {
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig != null && ReferenceEquals(rig.Player, player)) { return rig; }
            }

            return null;
        }

        private static string ProjectileLabel(PMProjectileKey key)
        {
            return "proj/e" + key.Epoch.ToString(CultureInfo.InvariantCulture)
                   + "/o" + key.OwnerNetId.ToString(CultureInfo.InvariantCulture)
                   + "/p" + key.ProjectileId.ToString(CultureInfo.InvariantCulture)
                   + "/" + (key.Origin == PMProjectileOrigin.ClientPredicted ? "ap" : "sd");
        }

        /// <summary>
        /// 释放投射物链：**Driver → Motion → presentation**（地图由 ReleaseSession 随后释放）。
        ///
        /// 顺序理由：驱动持有 motion 作为 hostMotion；先释放驱动就不会再有人查询 motion；
        /// 表现层随后销毁（此时已无人会再 Apply）。
        /// </summary>
        private static void ReleaseProjectiles(Session session)
        {
            if (session == null) { return; }

            // R6-C：**战斗驱动必须先于 R5 驱动释放**（契约「C 宿主接线冻结」：R6Dispose 在 R5Dispose 前）。
            //
            // 理由：R6 驱动持有 R5 的 Settlement 订阅与玩家接缝；反过来释放（先 R5）会让 R6 在一个已经
            // 死掉的 R5 上解绑/清账，并把会话失败的原因指向错误的一侧。
            if (session.CombatDriver != null)
            {
                PMR6CombatDriver combat = session.CombatDriver;
                session.CombatDriver = null;

                // 先解绑（幂等；与 R5 同套路），再 Dispose 会话级驱动。
                // 这不触发「断线判胜」：R6 的 Dispose 不调 core.Disconnect（客户端本来也没有 core）。
                for (int i = 0; i < session.Movements.Count; i++)
                {
                    MovementRig rig = session.Movements[i];
                    if (rig == null || rig.Player == null) { continue; }

                    try { combat.UnbindPlayer(rig.Player); }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 战斗解绑异常：" + ex.GetType().Name);
                    }
                }

                try { combat.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放战斗驱动异常：" + ex.GetType().Name);
                }
            }

            if (session.ProjectileDriver != null)
            {
                // 契约：退出时对每个副本 Unbind（幂等），再 Dispose 整个会话驱动。
                for (int i = 0; i < session.Movements.Count; i++)
                {
                    MovementRig rig = session.Movements[i];
                    if (rig == null || rig.Player == null) { continue; }

                    try { session.ProjectileDriver.UnbindPlayer(rig.Player); }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 投射物解绑异常：" + ex.GetType().Name);
                    }
                }

                try { session.ProjectileDriver.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放投射物驱动异常：" + ex.GetType().Name);
                }

                session.ProjectileDriver = null;
            }

            if (session.ProjectileMotion != null)
            {
                try { session.ProjectileMotion.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放投射物运动异常：" + ex.GetType().Name);
                }

                session.ProjectileMotion = null;
            }

            foreach (KeyValuePair<PMProjectileKey, ProjectileViewEntry> kv in session.ProjectileViews)
            {
                ProjectileViewEntry entry = kv.Value;
                if (entry == null || entry.Presentation == null) { continue; }

                try { entry.Presentation.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放投射物表现异常：" + ex.GetType().Name);
                }
            }

            session.ProjectileViews.Clear();
            session.ProjectileViewSeen.Clear();
            session.ProjectileViewRemovals.Clear();
            session.ProjectileTrackedKeys.Clear();
            session.ProjectileTargets.Clear();
            session.ProjectileCandidates.Clear();
            session.ProjectileViewBuffer = null;
            session.ProjectileViewCount = 0;
            session.ProjectileAccumulatorMs = 0.0;
            session.ProjectileStepsThisFrame = 0;
            session.ProjectileHistory = null;

            if (session.CandidateCollector != null)
            {
                session.CandidateCollector.Clear();
                session.CandidateCollector = null;
            }

            // 与 PRM3Runtime.Warn 同纪律：无条件还原（null 也是合法状态）。
            PMR3Player.ProjectileWarn = session.PreviousProjectileWarn;
        }

        /// <summary>投射物链的单行诊断（心跳与门禁对账用）。</summary>
        private static string DescribeProjectiles(Session session)
        {
            if (session == null) { return "projectiles=<none>"; }

            string driverText = "<none>";
            if (session.ProjectileDriver != null)
            {
                driverText = "faulted=" + (session.ProjectileDriver.IsFaulted ? 1 : 0).ToString(CultureInfo.InvariantCulture)
                             + " views=" + session.ProjectileDriver.ViewCount.ToString(CultureInfo.InvariantCulture)
                             + " fakes=" + session.ProjectileDriver.FakeCount.ToString(CultureInfo.InvariantCulture)
                             + " mirrors=" + session.ProjectileDriver.MirrorCount.ToString(CultureInfo.InvariantCulture);
            }

            return "projectiles cap=" + MaxPresentationViews.ToString(CultureInfo.InvariantCulture)
                   + " views=" + session.ProjectileViews.Count.ToString(CultureInfo.InvariantCulture)
                   + " tracked=" + session.ProjectileTrackedKeys.Count.ToString(CultureInfo.InvariantCulture)
                   + " targets=" + session.ProjectileTargets.Count.ToString(CultureInfo.InvariantCulture)
                   + " pumps=" + session.ProjectilePumps.ToString(CultureInfo.InvariantCulture)
                   + " steps=" + session.ProjectileStepsThisFrame.ToString(CultureInfo.InvariantCulture)
                   + " skipped=" + session.ProjectileViewSkipped.ToString(CultureInfo.InvariantCulture)
                   + " overflows=" + session.ProjectileViewOverflows.ToString(CultureInfo.InvariantCulture)
                   + " quotaSkipped=" + session.ProjectileHitQuotaSkipped.ToString(CultureInfo.InvariantCulture)
                   + " hitRpcs=" + session.ProjectileHitRpcs.ToString(CultureInfo.InvariantCulture)
                   + " hitRejected=" + session.ProjectileHitReportRejected.ToString(CultureInfo.InvariantCulture)
                   + " accumulatorMs=" + session.ProjectileAccumulatorMs.ToString("0.#", CultureInfo.InvariantCulture)
                   + " driver{" + driverText + "}";
        }

        // =================================================================================
        //  R6-C：正式直线攻击 / 只读 HUD / 结果退场
        // =================================================================================

        /// <summary>
        /// 终局结果到达后，等待 DS 正常退场（端点关闭）的最长时间（毫秒）。
        ///
        /// 取值依据：DS 侧的「全 ACK 或 5000ms 宽限」一旦满足就 SubmitResult 并退出
        /// （<see cref="PMCombatLimits.ResultGraceMs"/>），因此客户端最多等它 5000ms + 一点余量就该自行
        /// 有界退场；而客户端传输层的空闲看门狗默认是 10s（<c>PMTransportConfig.IdleTimeoutMs</c>），
        /// 只靠它会让玩家在终局画面里白等 10s。
        /// **超时是有界退场，不是「结果已被观察」的证明。**
        /// </summary>
        private const int TerminalExitGraceMs = PMCombatLimits.ResultGraceMs + 2000;

        /// <summary>
        /// R6-C：`R6.Pump(now)`（契约固定次序的第一步）。
        ///
        /// 它是本帧唯一处理入站战斗命令的地方：DS 侧是上行攻击 → core.RequestAttack → R5 权威裁决；
        /// AP 侧是攻击裁决/比赛结果/结果 ACK 的取出与 core 时钟推进。
        /// </summary>
        private static void PumpCombatInbound(Session session, double wallNowMs)
        {
            PMR6CombatDriver combat = session.CombatDriver;
            if (combat == null) { return; }

            if (combat.IsFaulted || combat.IsDisposed)
            {
                Fail(session, "R6 战斗驱动不可用（faulted=" + combat.IsFaulted.ToString(CultureInfo.InvariantCulture)
                     + " disposed=" + combat.IsDisposed.ToString(CultureInfo.InvariantCulture)
                     + " reason=" + combat.FaultReason + " " + combat.FaultError + "）");
                return;
            }

            try { combat.Pump(wallNowMs); }
            catch (Exception ex)
            {
                Fail(session, "R6 Pump 异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            session.CombatPumps++;

            if (combat.IsFaulted)
            {
                Fail(session, "R6 战斗驱动失败：" + combat.FaultReason + " " + combat.FaultError);
            }
        }

        /// <summary>
        /// R6-C：客户端 `R6.FlushState(now)`（契约固定次序的末步）。
        ///
        /// AP 侧它在驱动里的唯一职责是**回 ServerCombatResultAckV1**（ACK 不在复制回调里发），
        /// 因此端点已经不可用时直接跳过：发给一个已经没人收的 socket 只会把「正常退场」变成
        /// `SendFailed` fault，而契约明确要求「已知合法 terminal 结果后 DS 正常退出不应当作 Fail」。
        /// </summary>
        private static void FlushCombatState(Session session, double wallNowMs)
        {
            PMR6CombatDriver combat = session.CombatDriver;
            if (combat == null) { return; }

            if (combat.IsFaulted || combat.IsDisposed)
            {
                Fail(session, "R6 战斗驱动不可用（faulted=" + combat.IsFaulted.ToString(CultureInfo.InvariantCulture)
                     + " disposed=" + combat.IsDisposed.ToString(CultureInfo.InvariantCulture)
                     + " reason=" + combat.FaultReason + " " + combat.FaultError + "）");
                return;
            }

            if (!IsEndpointUsable(session)) { return; }

            try { combat.FlushState(wallNowMs); }
            catch (Exception ex)
            {
                Fail(session, "R6 FlushState 异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            session.CombatFlushes++;

            if (combat.IsFaulted)
            {
                Fail(session, "R6 战斗驱动失败：" + combat.FaultReason + " " + combat.FaultError);
            }
        }

        /// <summary>
        /// 端点是否还能上行（已释放/已失败/连接未就绪都算不能）。
        ///
        /// 它只用于「值不值得尝试发一包」，**不**用于任何权威判定。
        /// </summary>
        private static bool IsEndpointUsable(Session session)
        {
            if (session == null || session.Endpoint == null) { return false; }
            if (session.Endpoint.IsDisposed || session.Endpoint.ClientFailed) { return false; }

            PMTransportConnection connection = session.Endpoint.ClientConnection;
            return connection != null && connection.IsReady;
        }

        /// <summary>
        /// 把本帧的**复制结论**翻译成宿主行为：身份核对、死亡冻结、终局冻结。
        ///
        /// 客户端不做任何权威判定：这里只读 DS 复制过来的 <c>CombatDead</c> /
        /// <c>CombatMatchEnded</c> / <c>CombatHeroId</c> / <c>CombatTeamId</c>，
        /// 并把结论落成「冻结 / 停输入 / 禁开火」。
        /// </summary>
        private static void UpdateCombatReplicationState(Session session)
        {
            VerifyOwnerIdentity(session);
            if (session.Faulted) { return; }

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig == null || rig.Player == null) { continue; }

                // ① 死亡：冻结**对应** Mover（客户端只吃复制结论，不做存活裁决）。
                if (rig.Player.CombatDead) { FreezeRigOnce(rig); }

                // ② 终局：冻结**全部**运动，停输入/候选，但协议 Pump 继续（等结果退出）。
                if (rig.Player.CombatMatchEnded) { FreezeOnMatchEnded(session); }
            }
        }

        /// <summary>
        /// 把本人副本的**复制身份**与本地 offer 逐项核对（只做一次，幂等）。
        ///
        /// 为什么必须核对：<c>CombatHeroId</c>/<c>CombatTeamId</c> 在复制到达前是**默认 0**，
        /// 而 0 恰好是一个合法英雄号（队号 0 则表示「未知」）。只判「非 0」等于默认放行，
        /// 只判「>= 0」更没有意义 —— 契约要求「本人 Hero/Team 必须与 offer 一致」。
        /// 不一致 ⇒ 整局失败（旧 UI 自选英雄 / 错位入局的唯一可见信号），绝不默默接着打。
        /// </summary>
        private static void VerifyOwnerIdentity(Session session)
        {
            if (session.OwnerIdentityVerified || session.OwnerIdentityMismatch) { return; }
            if (session.Offer == null) { return; }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null) { return; }

            // 队号仍未就位（<= 0 = 未知）：不判、不缓存失败，下一帧再看。
            if (owner.CombatTeamId <= 0) { return; }

            int heroId = owner.CombatHeroId;
            int teamId = owner.CombatTeamId;
            int offerHero = session.Offer.Identity.HeroId;
            int offerTeam = session.Offer.Identity.TeamId;

            if (heroId != offerHero || teamId != offerTeam)
            {
                session.OwnerIdentityMismatch = true;
                Fail(session, "本人战斗身份与入局 offer 不一致（复制 hero="
                     + heroId.ToString(CultureInfo.InvariantCulture)
                     + " team=" + teamId.ToString(CultureInfo.InvariantCulture)
                     + "；offer hero=" + offerHero.ToString(CultureInfo.InvariantCulture)
                     + " team=" + offerTeam.ToString(CultureInfo.InvariantCulture)
                     + "）：拒绝以旧 UI 自选身份继续（不回旧链）");
                return;
            }

            session.OwnerIdentityVerified = true;
        }

        /// <summary>
        /// 开火就绪判据（**本地门，只用于「不开火」的优化，不写任何复制字段**）。
        ///
        /// 契约：本人 hero/team 必须与 offer 一致，且**不能拿「不等于 0 的默认值」当放行条件**。
        /// 因此这里要求：身份已核对（hero/team 逐项比较） → 已收到 MaxHp 复制（它是「DS 已发布初值」
        /// 的可观测判据，默认 0 即未就绪） → 未死、未终局。
        /// 真正的裁决仍在 DS：core 会再判 hero/team/资源/间隔。
        /// </summary>
        private static bool IsCombatFireReady(Session session, PMR3Player owner, out string error)
        {
            error = null;

            if (!session.OwnerIdentityVerified)
            {
                error = "战斗状态未就绪：hero/team 复制尚未与 offer 核对完成";
                return false;
            }

            if (owner.CombatHeroId != session.Offer.Identity.HeroId
                || owner.CombatTeamId != session.Offer.Identity.TeamId)
            {
                error = "战斗状态与 offer 不一致（hero="
                        + owner.CombatHeroId.ToString(CultureInfo.InvariantCulture) + "/"
                        + session.Offer.Identity.HeroId.ToString(CultureInfo.InvariantCulture)
                        + " team=" + owner.CombatTeamId.ToString(CultureInfo.InvariantCulture) + "/"
                        + session.Offer.Identity.TeamId.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            if (owner.CombatMaxHp <= 0)
            {
                error = "战斗状态未就绪：未收到 HP 复制（maxHp="
                        + owner.CombatMaxHp.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            if (owner.CombatDead) { error = "本人已死亡，禁止开火"; return false; }
            if (owner.CombatMatchEnded) { error = "比赛已结束，禁止开火"; return false; }

            return true;
        }

        /// <summary>按复制结论冻结**对应**副本的 Mover（幂等，只冻结一次）。</summary>
        private static void FreezeRigOnce(MovementRig rig)
        {
            if (rig == null || rig.DeathFrozen) { return; }
            rig.DeathFrozen = true;

            if (rig.Driver == null || rig.Driver.IsDisposed) { return; }

            try { rig.Driver.Freeze(); }
            catch (Exception ex)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 冻结死亡副本运动异常：" + ex.GetType().Name);
            }

            HYLDDebug.Log("[PMClientSessionHost] 副本已按复制结论冻结（CombatDead）netId="
                          + (rig.Player != null ? rig.Player.NetId.Value.ToString(CultureInfo.InvariantCulture) : "<none>"));
        }

        /// <summary>
        /// 终局复制（<c>CombatMatchEnded</c>）到达：冻结**全部**运动。
        ///
        /// 契约：MatchEnded / 已收到结果 ⇒ 冻结全部并停止输入/候选，而**继续**协议 Pump 等待退出。
        /// 这里只冻结运动（<see cref="FreezeMovements"/>）；输入/候选的停摆由
        /// <see cref="PumpMovement"/> 与 <see cref="PumpProjectileOnce"/> 里的 <c>MovementFrozen</c> 分支保证。
        /// </summary>
        private static void FreezeOnMatchEnded(Session session)
        {
            if (session.MatchEndedFrozen) { return; }
            session.MatchEndedFrozen = true;

            HYLDDebug.Log("[PMClientSessionHost] 已复制到 MatchEnded：冻结全部运动并停止输入/候选，"
                          + "继续协议 Pump 等待结果与退场");

            FreezeMovements(session);
        }

        /// <summary>
        /// 把**可信终局结论**落成只读事实：推给 HUD、发布 static 快照与只读事件，然后冻结全部。
        ///
        /// 注意它**不**在这里释放会话：契约要求「receivedresult：冻结全部并停止输入/候选，
        /// 继续协议 Pump 等待退出」，而 DS 正是靠客户端的 ACK 才能结束自己的结果闭环。
        /// 真正的释放发生在端点关闭（DS 正常退场）或有界宽限到期时，见 <see cref="PumpActive"/>。
        /// </summary>
        private static void OnTerminalResult(Session session, uint outcomeId, int winnerTeamId, double wallNowMs)
        {
            session.TerminalResultObserved = true;
            session.TerminalResultWallMs = wallNowMs;

            int localTeamId = session.OwnerPlayer != null ? session.OwnerPlayer.CombatTeamId : 0;

            PMCombatHudOutcome hudOutcome;
            if (winnerTeamId == 0) { hudOutcome = PMCombatHudOutcome.Draw; }
            else if (localTeamId > 0 && winnerTeamId == localTeamId) { hudOutcome = PMCombatHudOutcome.Win; }
            else { hudOutcome = PMCombatHudOutcome.Lose; }

            // 胜/负/平由本地队伍号与**原始** winnerTeamId 比较得出；这里不重算任何权威结论。
            if (session.Hud != null && !session.Hud.IsDisposed)
            {
                PMR3Player owner = session.OwnerPlayer;
                if (owner != null)
                {
                    session.Hud.SetLocal(owner.CombatHp, owner.CombatMaxHp, owner.CombatMana, owner.CombatSuperEnergy);
                }

                session.Hud.SetReady(false);
                session.Hud.SetOutcome(true, hudOutcome, winnerTeamId, localTeamId);
            }

            PublishLastCombatResult(session.Offer != null ? session.Offer.MatchId : null, outcomeId, winnerTeamId);

            HYLDDebug.Log("[PMClientSessionHost] 已收到可信终局结果：match="
                          + (session.Offer != null ? session.Offer.MatchId : "<none>")
                          + " outcome=" + outcomeId.ToString(CultureInfo.InvariantCulture)
                          + " winnerTeamId=" + winnerTeamId.ToString(CultureInfo.InvariantCulture)
                          + " 本队=" + localTeamId.ToString(CultureInfo.InvariantCulture)
                          + " ⇒ " + hudOutcome.ToString()
                          + "；冻结全部并等待 DS 退场（只读终局 HUD 将保留）");

            // 「冻结全部」：连 MatchEnded 位也一起置上（同一个终局语义；幂等）。
            FreezeOnMatchEnded(session);
        }

        /// <summary>
        /// 发布 static 只读结果快照 + 只读事件（逐订阅者隔离异常：大厅 UI 的问题不能让宿主崩）。
        /// </summary>
        private static void PublishLastCombatResult(string matchId, uint outcomeId, int winnerTeamId)
        {
            _lastCombatResultValid = true;
            _lastCombatOutcomeId = outcomeId;
            _lastCombatWinnerTeamId = winnerTeamId;
            _lastCombatMatchId = matchId == null ? string.Empty : matchId;

            Action<uint, int, string> handler = _combatResultObserved;
            if (handler == null) { return; }

            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<uint, int, string>)list[i])(outcomeId, winnerTeamId, _lastCombatMatchId);
                }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 结果事件订阅者异常（已隔离）："
                                         + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>清空 static 只读结果快照（显式 Stop / 下一 Enter 的显式清理点）。</summary>
        private static void ClearLastCombatResult()
        {
            _lastCombatResultValid = false;
            _lastCombatOutcomeId = 0u;
            _lastCombatWinnerTeamId = 0;
            _lastCombatMatchId = null;
        }

        /// <summary>销毁保留中的只读终局 HUD（幂等；最多一个实例）。</summary>
        private static void ReleaseRetainedHud()
        {
            PMUnityCombatHud hud = _retainedHud;
            _retainedHud = null;
            if (hud == null) { return; }

            try { hud.Dispose(); }
            catch (Exception ex)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 释放保留只读 HUD 异常：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 正常结束会话（**已知可信终局之后**的退场）：与 <see cref="Stop"/> 同一条释放链，但
        ///   · 不把「DS 正常退场 / 端点 idle 关闭」当作失败（<c>Faulted</c> 不置位）；
        ///   · **保留**只读终局 HUD：摘到 static 引用，交给下一 Enter / 显式 Stop 销毁。
        ///
        /// 幂等（<c>_stopping</c> 闩锁 + 「必须仍是当前会话」校验，避免重复释放）。
        /// </summary>
        private static void EndSessionNormally(Session session, string reason)
        {
            if (session == null) { return; }
            if (_stopping) { return; }

            _stopping = true;
            try
            {
                if (!ReferenceEquals(_active, session))
                {
                    // 已被 Stop / 换局收尾过：什么都不做（不重复释放）。
                    return;
                }

                _active = null;

                PMR3Runtime.PlayerReplicated -= OnPlayerReplicated;

                // 保留只读终局 HUD：先摘出（ReleaseSession 只销毁「会话自己那一份」）。
                if (session.Hud != null)
                {
                    ReleaseRetainedHud();
                    _retainedHud = session.Hud;
                    session.Hud = null;
                }

                // `_active` 已置 null，因此端点 Dispose / 断线事件不可能再重入会话失败（防重入）。
                if (session.Endpoint != null)
                {
                    try { session.Endpoint.Dispose(); }
                    catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放端点异常：" + ex.GetType().Name); }
                }

                ReleaseSession(session);

                // **刻意不在这里调 `PMClientSessionHostDriver.Release()`**：
                //
                // Unity 的 `Object.Destroy` 是**帧末延迟**执行的，它触发的 `OnDestroy → Stop()` 会在本方法
                // 返回、`_stopping` 已经复位之后才跑；那时 `Stop()` 会走「无会话」分支，把我们刚保留的
                // 只读终局 HUD 与结果快照一起清掉 —— 正好违反契约要求的「保留到下一 Enter / 显式 Stop」。
                // 而保留着这个 per-frame driver 无害：`_active == null` 时 `PumpActive()` 直接返回，
                // HUD 的 `OnGUI` 由 Unity 自己驱动（不依赖本 driver），下一 Enter 会复用同一个实例。

                HYLDDebug.Log("[PMClientSessionHost] 会话已正常结束并回到原大厅（" + reason + "，match="
                              + (session.Offer != null ? session.Offer.MatchId : "<none>")
                              + "）：隔离物理场景/表现/端点已释放，只读终局 HUD 保留"
                              + "（帧驱动保留至下一 Enter / 显式 Stop）");
            }
            finally
            {
                _stopping = false;
            }
        }

        /// <summary>
        /// 只读 HUD 每帧推值。**只走 setter**：HUD 自己不读旧链、不写资源、不做判定。
        /// </summary>
        private static void UpdateCombatHud(Session session)
        {
            PMUnityCombatHud hud = session.Hud;
            if (hud == null || hud.IsDisposed) { return; }

            PMR3Player owner = session.OwnerPlayer;
            if (owner != null)
            {
                hud.SetLocal(owner.CombatHp, owner.CombatMaxHp, owner.CombatMana, owner.CombatSuperEnergy);

                string readyError;
                bool fireReady = IsCombatFireReady(session, owner, out readyError);

                // 就绪提示必须与 `TryCombatAttack` 的**真实门**一致：那边除了 `IsCombatFireReady` 还会因
                // Faulted / MovementFrozen / MatchEndedFrozen 直接不开火。终局结果已到、但复制位
                // `CombatMatchEnded` 还没到的那些帧里，只看 `IsCombatFireReady` 会一边显示「[就绪]」
                // 一边显示「结果：胜/负」——自相矛盾并误导实机联调。
                hud.SetReady(fireReady
                             && !session.Faulted
                             && !session.MovementFrozen
                             && !session.MatchEndedFrozen);
            }

            if (session.CombatDriver != null
                && session.CombatDriver.AttackRejections > session.ObservedCombatRejections)
            {
                session.ObservedCombatRejections = session.CombatDriver.AttackRejections;
                session.LastAttackError = "DS 拒绝攻击 #" + session.CombatDriver.LastRejectedActivationId
                    + "：" + session.CombatDriver.LastAttackRejectionReason;
            }
            hud.SetNotice(session.LastAttackError);
        }

        /// <summary>战斗链的单行诊断（心跳与门禁对账用）。</summary>
        private static string DescribeCombat(Session session)
        {
            if (session == null) { return "combat=<none>"; }

            string driverText = "<none>";
            if (session.CombatDriver != null)
            {
                string storedText = "none";
                uint outcomeId;
                int winnerTeamId;
                if (session.CombatDriver.TryGetStoredMatchResult(out outcomeId, out winnerTeamId))
                {
                    storedText = outcomeId.ToString(CultureInfo.InvariantCulture) + "/"
                                 + winnerTeamId.ToString(CultureInfo.InvariantCulture);
                }

                driverText = "faulted=" + (session.CombatDriver.IsFaulted ? 1 : 0).ToString(CultureInfo.InvariantCulture)
                             + " pending=" + session.CombatDriver.PendingAttackCount.ToString(CultureInfo.InvariantCulture)
                             + " result=" + storedText;
            }

            return "combat identity=" + (session.OwnerIdentityVerified ? 1 : 0).ToString(CultureInfo.InvariantCulture)
                   + " ended=" + (session.MatchEndedFrozen ? 1 : 0).ToString(CultureInfo.InvariantCulture)
                   + " terminal=" + (session.TerminalResultObserved ? 1 : 0).ToString(CultureInfo.InvariantCulture)
                   + " pumps=" + session.CombatPumps.ToString(CultureInfo.InvariantCulture)
                   + " flushes=" + session.CombatFlushes.ToString(CultureInfo.InvariantCulture)
                   + " attacks=" + session.AttackAttempts.ToString(CultureInfo.InvariantCulture)
                   + " accepted=" + session.AttackFiresAccepted.ToString(CultureInfo.InvariantCulture)
                   + " plannerRej=" + session.AttackPlannerRejected.ToString(CultureInfo.InvariantCulture)
                   + " gate=" + session.AttackGateBlocked.ToString(CultureInfo.InvariantCulture)
                   + " driverRej=" + session.AttackDriverRejected.ToString(CultureInfo.InvariantCulture)
                   + " skippedDead=" + session.TargetsSkippedDead.ToString(CultureInfo.InvariantCulture)
                   + " skippedTeam=" + session.TargetsSkippedTeam.ToString(CultureInfo.InvariantCulture)
                   + " lastError=" + (string.IsNullOrEmpty(session.LastAttackError) ? "<none>" : session.LastAttackError)
                   + " driver{" + driverText + "}";
        }

        /// <summary>
        /// 端点失败事件（握手超时/被拒、激活失败、会话断开、socket 层不可恢复错误）。
        ///
        /// **本方法不判失败**：事件由 <c>endpoint.Pump</c> **同步**抛出，而那一刻「同帧发结果 + 关会话」的
        /// 合法终局还躺在 R6 的入站队列里（<c>R6.Pump</c> 尚未运行）。在这里 `Fail` 会抢先置上 <c>Faulted</c>，
        /// 把一份已到达的合法终局降级为断线失败。因此只记下「待定性」的原因，
        /// 由本帧 <see cref="PumpActive"/> 的 `ClientFailed` 分支先跑 <c>R6.Pump</c> 再用
        /// 「结果是否真的已保存」定性。
        ///
        /// 退出判据**只**看 <c>TryGetStoredMatchResult</c>（可靠结果已过校验并幂等保存）或
        /// `TerminalResultObserved`，绝不看「收到过结果 RPC」的计数（理由见
        /// <see cref="PumpActive"/> 的端点失败分支）。
        /// </summary>
        private static void OnEndpointFailed(string reason)
        {
            Session session = _active;
            if (session == null) { return; }

            // 已拿到可信终局后的 DS 正常退场：不判失败（也不再记「待定性」）。
            if (session.TerminalResultObserved)
            {
                HYLDDebug.Log("[PMClientSessionHost] 端点已关闭（" + reason
                              + "），但可信终局已到达：按正常退场处理，不判失败");
                return;
            }

            // 端点会对「入局会话关闭」也报这条事件；这里按事实区分：已经激活过再关闭 = 会话断了。
            if (session.Endpoint != null && session.Endpoint.ClientConnection != null
                && !session.Endpoint.ClientConnection.IsReady)
            {
                session.PendingDisconnectReason = "入局会话已断开：" + reason;
                HYLDDebug.LogWarning("[PMClientSessionHost] 端点事件（待本帧按可信终局定性）："
                                     + session.PendingDisconnectReason);
                return;
            }

            // 未激活就报失败（握手阶段）：不改变原行为——失败消息仍由本帧 `ClientFailed` 分支给出。
            HYLDDebug.LogWarning("[PMClientSessionHost] 端点事件：" + reason);
        }

        // =================================================================================
        //  失败 / 清理
        // =================================================================================

        private static void Fail(Session session, string reason)
        {
            if (session != null)
            {
                if (session.Faulted) { return; }
                session.Faulted = true;
                session.FaultReason = reason;

                // 契约 §B3：「失联/失败必须 Freeze」。冻结在前：先让 AP 停止预测、
                // SP 停止推进，再由调用方进入"只推墙钟"的冻结帧。
                FreezeMovements(session);
            }

            // 新链失败**只报失败**：不切回旧链、不改走 BattleData/ClearSence（契约 §7.1 / §7.4）。
            HYLDDebug.LogError("[PMClientSessionHost] 新链失败：" + reason + "（不回落旧链；运动已冻结）");
        }

        /// <summary>冻结全部运动链（幂等）。不 Dispose：释放由 Stop/换局路径负责。</summary>
        private static void FreezeMovements(Session session)
        {
            if (session.MovementFrozen) { return; }
            session.MovementFrozen = true;

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Freeze(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 冻结运动驱动异常：" + ex.GetType().Name);
                }
            }
        }

        /// <summary>释放全部运动链：先停驱动，再销毁表现（避免 Apply 到已释放对象），最后 Dispose 驱动。</summary>
        private static void ReleaseMovements(Session session)
        {
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];

                if (rig.Presentation != null)
                {
                    try { rig.Presentation.Dispose(); }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 释放表现层异常：" + ex.GetType().Name);
                    }

                    rig.Presentation = null;
                }

                if (rig.Driver != null)
                {
                    try
                    {
                        rig.Driver.Freeze();
                        rig.Driver.Dispose();
                    }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 释放运动驱动异常：" + ex.GetType().Name);
                    }

                    rig.Driver = null;
                }

                rig.Player = null;
            }

            session.Movements.Clear();
            session.StepAccumulatorMs = 0.0;
            session.Query = null;

            if (session.Input != null)
            {
                session.Input.Reset();
                session.Input = null;
            }
        }

        private static void ReleaseSession(Session session)
        {
            if (session == null) { return; }

            // R6-C：端点也必须在这里兜底释放（幂等）。
            //
            // 为什么必须有这一步：`Stop` / `EndSessionNormally` 是**先** Dispose 端点再进本方法
            // （保证断线事件不会重入会话失败），但 `Enter` 的构造失败路径（例如只读 HUD 创建失败）
            // 在 `OpenClient` 成功之后就只调本方法 —— 而本方法原来只是把 `session.Endpoint` 置 null，
            // 于是那个已经 bind 的 UDP socket 永不被关闭。先置 null 的语义不变（Dispose 幂等）。
            if (session.Endpoint != null)
            {
                try { session.Endpoint.Dispose(); }
                catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放端点异常：" + ex.GetType().Name); }
            }

            // R6-C：只读 HUD 属于“会话自己那一份”。**收到可信终局时它已被摘出**（见
            // EndSessionNormally），因此正常退场路径上这里不会销毁终局 HUD；
            // 失败/换局路径上它必须在这里被销毁，保证「不泄漏无限创建」。
            if (session.Hud != null)
            {
                PMUnityCombatHud hud = session.Hud;
                session.Hud = null;

                try { hud.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放只读战斗 HUD 异常：" + ex.GetType().Name);
                }
            }

            // R5-C：投射物链先释放（Driver → Motion → presentation）；地图在下面按模式释放。
            // R6-C：战斗驱动在 ReleaseProjectiles 内部**先于** R5 驱动释放。
            ReleaseProjectiles(session);

            // R4-B（B3）：运动链先释放（表现 → 驱动），再拆世界/桥。
            ReleaseMovements(session);

            // R4-C（C3）：把正式模式临时关闭的旧相机恢复原状（不改旧场景的永久设置）。
            RestoreSuppressedCameras(session);

            if (session.World != null)
            {
                PMR3Runtime.Detach(session.World);
            }

            // 无条件还原：PreviousRuntimeWarn 为 null 也是合法状态（没有人装过出口），
            // 留着本会话的 lambda 会让已释放对象被后续调用（static 出口泄漏）。
            PMR3Runtime.Warn = session.PreviousRuntimeWarn;

            if (session.Bridge != null)
            {
                try { session.Bridge.Dispose(); }
                catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放桥异常：" + ex.GetType().Name); }
            }

            for (int i = 0; i < session.SceneObjects.Count; i++)
            {
                if (session.SceneObjects[i] != null)
                {
                    UnityEngine.Object.Destroy(session.SceneObjects[i]);
                }
            }

            session.SceneObjects.Clear();

            // R4-B（B3）：释放承载地板/墙的**本地物理场景**（先销毁对象、后卸载场景）。
            // R4-C（C3）：正式地图持有自己的隔离物理场景与查询，释放必须走它自己的 Dispose；
            // 诊断模式仍按原路径卸载本地物理场景。
            if (session.BattleMap != null)
            {
                try { session.BattleMap.Dispose(); }
                catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放正式地图异常：" + ex.GetType().Name); }

                session.BattleMap = null;
                session.MovementScene = default(Scene);
            }
            else
            {
                PMR3TestScene.ReleaseIsolatedScene(session.MovementScene);
                session.MovementScene = default(Scene);
            }

            session.OwnerPlayer = null;
            session.SelectedWorldVersion = 0;

            // R6-C：会话级战斗账本一律随会话消失（不跨局留任何引用）。
            session.CombatDriver = null;
            session.OwnerIdentityVerified = false;
            session.OwnerIdentityMismatch = false;
            session.MatchEndedFrozen = false;
            session.TerminalResultObserved = false;
            session.TerminalResultWallMs = 0.0;
            session.PendingDisconnectReason = null;
            session.NormalAttackEdgeBuffered = false;
            session.SuperAttackEdgeBuffered = false;
            session.LastAttackError = null;
            session.LastAttackWallMs = double.NegativeInfinity;

            session.PendingProbes.Clear();
            session.KnownPlayers.Clear();
            session.Endpoint = null;
            session.Bridge = null;
            session.World = null;
            session.NetSession = null;
        }

        /// <summary>
        /// R4-C（C3）正式模式：**临时**关闭除自己之外的所有已启用相机，避免旧主相机把新角色遮住。
        ///
        /// 为什么必须记录+恢复：本类不能改旧场景的永久设置（Contract C3），退局/失败路径靠
        /// <see cref="RestoreSuppressedCameras"/> 逐个还原。
        /// 为什么只关“已启用”的：<c>Camera.allCameras</c> 本身只返回启用中的相机，
        /// 未启用的相机不参与渲染，也就无需（也不应）改动。
        /// </summary>
        private static void SuppressOtherCameras(Session session, Camera mine)
        {
            if (session == null || mine == null) { return; }

            Camera[] cameras = Camera.allCameras;
            if (cameras == null) { return; }

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera other = cameras[i];
                if (other == null || ReferenceEquals(other, mine)) { continue; }
                if (!other.enabled) { continue; }

                SuppressedCamera entry = new SuppressedCamera();
                entry.Camera = other;
                entry.WasEnabled = true;
                session.SuppressedCameras.Add(entry);

                other.enabled = false;
            }

            if (session.SuppressedCameras.Count > 0)
            {
                HYLDDebug.Log("[PMClientSessionHost] 正式模式相机接管：临时关闭其它启用相机 "
                              + session.SuppressedCameras.Count.ToString(CultureInfo.InvariantCulture)
                              + " 个（退局恢复）");
            }
        }

        /// <summary>把 <see cref="SuppressOtherCameras"/> 关掉的相机恢复原状（幂等）。</summary>
        private static void RestoreSuppressedCameras(Session session)
        {
            if (session == null) { return; }

            for (int i = 0; i < session.SuppressedCameras.Count; i++)
            {
                SuppressedCamera entry = session.SuppressedCameras[i];
                if (entry == null || entry.Camera == null) { continue; }

                entry.Camera.enabled = entry.WasEnabled;
            }

            session.SuppressedCameras.Clear();
        }

        private static bool SameMatch(PMDsEntryOffer a, PMDsEntryOffer b)
        {
            if (a == null || b == null) { return false; }
            return string.Equals(a.MatchId, b.MatchId, StringComparison.Ordinal) && a.Epoch == b.Epoch;
        }

        private static void LogHeartbeat(Session session)
        {
            HYLDDebug.Log("[PMClientSessionHost] heartbeat tick=" + session.TickCount.ToString(CultureInfo.InvariantCulture)
                          + " activated=" + (session.Endpoint.ClientConnection != null ? 1 : 0)
                          + " objects=" + session.World.ObjectCount.ToString(CultureInfo.InvariantCulture)
                          + " probes=" + session.ProbeNonce.ToString(CultureInfo.InvariantCulture)
                          + " echoes=" + EchoCount.ToString(CultureInfo.InvariantCulture)
                          + " udpIn=" + session.Endpoint.DatagramsReceived.ToString(CultureInfo.InvariantCulture)
                          + " udpOut=" + session.Endpoint.DatagramsSent.ToString(CultureInfo.InvariantCulture)
                          + " handshakeRej=" + session.Endpoint.HandshakeRejections.ToString(CultureInfo.InvariantCulture)
                          + " | " + DescribeMovements(session)
                          + " | " + DescribeProjectiles(session)
                          + " | " + DescribeCombat(session));
        }

        /// <summary>运动链的单行诊断（心跳与门禁对账用）。</summary>
        private static string DescribeMovements(Session session)
        {
            if (session == null) { return "movement=<none>"; }

            int owners = 0;
            int proxies = 0;
            long snapshotsApplied = 0;
            long inputsSent = 0;
            long events = 0;

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.IsOwner) { owners++; } else { proxies++; }

                if (rig.Driver == null) { continue; }

                snapshotsApplied += rig.Driver.SnapshotPayloadsApplied;
                inputsSent += rig.Driver.InputPayloadsSent;
                events += rig.Driver.EventBatchesAccepted;
            }

            return "movementRigs=" + session.Movements.Count.ToString(CultureInfo.InvariantCulture)
                   + "(ap=" + owners.ToString(CultureInfo.InvariantCulture)
                   + ",sp=" + proxies.ToString(CultureInfo.InvariantCulture) + ")"
                   + " snapshots=" + snapshotsApplied.ToString(CultureInfo.InvariantCulture)
                   + " inputsSent=" + inputsSent.ToString(CultureInfo.InvariantCulture)
                   + " eventBatches=" + events.ToString(CultureInfo.InvariantCulture)
                   + " accumulatorMs=" + session.StepAccumulatorMs.ToString("0.#", CultureInfo.InvariantCulture)
                   + " scene=" + (session.MovementScene.IsValid() ? session.MovementScene.name : "<none>")
                   + " isolated=" + (session.Query != null && session.Query.Scene.IsValid()
                                      && !session.Query.Scene.Equals(Physics.defaultPhysicsScene) ? 1 : 0)
                   + " frozen=" + (session.MovementFrozen ? 1 : 0);
        }

        /// <summary>单行诊断摘要（测试宿主/日志对账用）。</summary>
        public static string Describe()
        {
            Session session = _active;
            if (session == null) { return "client=<none>"; }

            return "match=" + session.Offer.MatchId
                   + " mode=" + (session.ContentFormal ? "formal" : "diagnostic")
                   + " digest=0x" + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                   + " worldVersion=" + session.SelectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                   + " tick=" + session.TickCount.ToString(CultureInfo.InvariantCulture)
                   + " probes=" + session.ProbeNonce.ToString(CultureInfo.InvariantCulture)
                   + " echoes=" + EchoCount.ToString(CultureInfo.InvariantCulture)
                   + " " + DescribeMovements(session)
                   + " " + DescribeProjectiles(session)
                   + " " + DescribeCombat(session)
                   + " fault=" + (session.Faulted ? session.FaultReason : "<none>");
        }
    }

    /// <summary>
    /// 客户端会话宿主的 Unity 驱动（每帧一次 <c>PumpActive</c> + 退出收尾）。
    ///
    /// 单独一个内部 MonoBehaviour 的原因：宿主本身是纯逻辑（可被门禁/工具复用），
    /// 而 Unity 的 Update 只能挂在组件上。这里不承载任何逻辑，只转发。
    /// </summary>
    internal sealed class PMClientSessionHostDriver : MonoBehaviour
    {
        private static PMClientSessionHostDriver _instance;

        internal static void Ensure()
        {
            if (_instance != null) { return; }

            GameObject go = new GameObject("[PMClientSessionHost]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<PMClientSessionHostDriver>();
        }

        internal static void Release()
        {
            PMClientSessionHostDriver driver = _instance;
            if (driver == null) { return; }

            _instance = null;

            GameObject go = driver.gameObject;
            if (go != null)
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        private void Update()
        {
            PMClientSessionHost.PumpActive();
        }

        private void OnApplicationQuit()
        {
            PMClientSessionHost.Stop();
        }

        private void OnDestroy()
        {
            // R6-C：**只有当前实例**的销毁才收尾。
            //
            // 为什么不能无条件 Stop：Unity 的 `Object.Destroy` 是帧末延迟执行的，因此
            // 「`Release()` 建新实例 / 或 `_active` 已换成新会话」时，旧实例的 `OnDestroy` 可能晚到
            // 同一帧末尾。无条件调 `Stop()` 会把刚建起来的新会话（或刚保留的只读终局 HUD）一并收掉。
            // 被 `Release()` 释放掉的旧实例（`_instance` 已不是它）在这里什么都不做 ——
            // 收尾已由 `Stop()` / `EndSessionNormally()` 当时做完。
            if (!ReferenceEquals(_instance, this)) { return; }

            // 幂等：Stop 内部有 _stopping 闩锁，重复调用无副作用。
            PMClientSessionHost.Stop();
        }
    }
}
