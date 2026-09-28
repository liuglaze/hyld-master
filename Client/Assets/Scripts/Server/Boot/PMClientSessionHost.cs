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
// 视觉修复（T-VIS2）：相机与按键 Yaw 解耦 + 隔离物理场景遮挡回退
// ---------------------------------------------------------------------------------------------
// 用户实机现象：正式地图里按左右方向键镜头 90/180 度瞬转，贴墙时镜头穿进几何体出现黑遮挡。
// 根因（代码事实）：PMUnityBattlePresentation.CreateTestCamera 把相机**挂在角色根节点下**，
// 而宿主每帧按 Mover predicted Yaw 旋转根，而 PMUnityMoverInput 的左右键会推导出 ∓90/180 的
// 世界 Yaw —— 子节点因此继承了按键转向；相机的固定后距也不会因墙而收短。
//
// ---------------------------------------------------------------------------------------------
// T-PLAY1/2（本轮）：把取景与背影还原成**用户选型的旧游戏**
// ---------------------------------------------------------------------------------------------
// 用户二次实机反馈：新链能跑通，但“不是原俯视射击”——诊断期的相机（yaw 0 / 俯角 12 / 后距 6）
// 不是旧游戏风格；可见角色转身与画面看到的朝向对不上（旧 Capsule 自带 270° 被叠加）。
// 本轮修复（仅在允许写入的三个文件内）：
//   · 取景改为旧预制体确定值（纯几何模块的 LegacyPrefab* 常量）：yaw −90 / 俯角 68.191 /
//     FOV 60 / 近裁面 0.3 / 远裁面 1000；后距 = 旧 HYLDCameraManger 相对偏移 (6,12,0) 的模长；
//   · 位置按旧链 SmoothTime（0.08s）临界阻尼跟随（平滑的是观察目标；rotation 仍为固定常量，
//     且首帧/出生/瞬移（跳变 ≥ 8m）直接贴合，不拖尾）；
//   · 遮挡探测方向与相机后向同一口径（即按新 yaw/俯角更新），仍只读、仍不改权威碰撞；
//   · 可见转身：权威 Mover Yaw 写到旧链同一个 **Capsule** 可见节点上（角色根只做位置/scale），
//     烘焙的 270° 不参与叠加；**不动**权威 Mover.Yaw / 网络协议 / 攻击朝向。
// 修复（只在本轮允许的文件内）：
//   · 相机改为**独立顶层对象**（不挂在角色根下）⇒ 结构上不可能继承按键 Yaw；
//   · 每帧位姿由纯几何模块 PMBattleCameraGeometry 求解：固定方位角 + 固定观察目标
//     （角色世界位置 + 观察高度）+ 旧预制体确定的后距/近裁面；
//   · 遮挡：在**同一隔离 PhysicsScene** 里对地图白名单 layer 做只读球体扫掠
//     （小半径，保守近似；不用无体积射线，以免从墙角擦过），
//     命中则把后距收短到命中距离 - 安全边距（并保留下限）。查询仅影响表现，
//     **不改**权威碰撞、不参与 Mover 仿真、不需要改 DS。
//   · 生命周期：建 rig 时创建（占位位姿）→ 每 Update 的 PumpMovement 里随 predicted 位姿重写；
//     冻结（断线/终局/死亡）时 PumpMovement 早返回 ⇒ 相机停住；Dispose 先单独销毁相机对象
//     再销毁角色根（相机不在根下，不单独销毁就是“留下第二个活跃相机”）。
//   · 隔离性：只对**正式模式 + 本地 owner** 生效；远端 SP 与诊断（胶囊）路径逐字不变。
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
//
// ---------------------------------------------------------------------------------------------
// T-PLAY4 新增：旧局内 UI / 摇杆的**后端输入接口**（不含 Canvas/Prefab，不做美术）
// ---------------------------------------------------------------------------------------------
// 用户选型冻结（Docs/plans/net-architecture-migration.md「T-PLAY4 UI与输入接口冻结」）：
// 旧 `Resources/Prefabs/GameUI.prefab` 只当**只读美术源**，新建独立 Canvas；输入组（本文件）
// 只提供三个对外冻结入口，UI 组**只读**依赖：
//   · `TrySetUiMove(float screenX, float screenY) bool`     —— 移动摇杆；
//   · `TryQueueUiAttack(bool isSuper, float screenX, float screenY) bool` —— 攻击 + 独立瞄准；
//   · `TryGetUiCombatSnapshot(out PMUiCombatSnapshot) bool` —— 只读 HP/MaxHp/Mana/Energy/死/终局/就绪/拒绝。
// 屏幕系（相对用户选定相机，世界 yaw −90，与 T-PLAY1 同源）：screen up → world −X，
// screen right → world +Z；纯变换与来源选择在 `PMUnityMoverInput`（可在 net8 逐条断言）。
//
// 纪律（逐条落实）：
//   · UI **永不**直接发 RPC、**永不**写 HP/Mana/Energy/复制字段/本地伤害；
//   · 非法输入（NaN/±Infinity/超量程）一律显式拒（不吞非法、不静默钳制）；
//   · 限幅单位向量；摇杆释放（零输入）清移动，键盘立即恢复；
//   · 攻击边沿在主线程 Pump **统一一次消费**；只有活动会话/Owner 已核对/未 dead/未 terminal/未 frozen 才排攻击；
//   · **一次攻击的方向 / 枪口 / planner / 上行声明用同一个向量**：UI 取独立瞄准向量（不绑 Mover yaw），
//     键盘 F/G 仍用 predicted yaw 兼底；玩家可见朝向仍只跟移动；
//   · 重复提交不堆上行（未消费的边沿还在时直接拒）；间隔门（planner FireIntervalMs）与 DS 裁决未弱化；
//   · Stop / 死亡 / 终局 / 换局一律清 UI 输入，不带出上一局的推杆与排队攻击；
//   · 客户端仍只做 planner + 预测，DS 的审批与资源权威不变；反例（UI 只改显示）由 R6 真实字节链的
//     方向槽校验拦住（见 Tools/PMR6NetworkTest 的 UI 瞄准同源一节）。
// 本文件在此覆盖上面 R4-C 「首批输入仍是键盘 WASD/Space，攻击尚未接入」的历史描述：
// 键盘仍是可用兼底，UI 是新增的**独立**输入源，两者共用同一条上行/预测链。

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
using PMNet.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// T-PLAY4：UI 只读战斗快照（**公开值类型**，namespace PMNet.Unity）。
    ///
    /// 公开契约（UI 表现组**只读**依赖，见 Docs/plans/net-architecture-migration.md
    /// 「T-PLAY4 UI与输入接口冻结」）：Hp / MaxHp / Mana / SuperEnergy / Dead / MatchEnded /
    /// NormalReady / SuperReady / LastReject（附 MatchId）。
    ///
    /// 为什么必须是值类型、且**只含值**：
    ///   · UI 只能拿到「这一刻的数字」，拿不到会话对象、拿不到 <c>PMR3Player</c>、也没有任何可写入口
    ///     —— 结构上排除「UI 顺手改资源 / 写复制字段」这类越权；
    ///   · 资源四项全部来自 DS 的复制字段（HP/MaxHp/Dead/MatchEnded 为公共复制，
    ///     Mana/SuperEnergy 为 OwnerOnly）：本快照**不**读旧 HYLDManger/BattleData/旧 BattleReview，
    ///     也不做任何胜负/命中/资源判定。
    ///
    /// 就绪位（NormalReady/SuperReady）是「本地门 + 复制资源」的合成建议（**DS 仍是唯一裁决**）：
    /// 身份已与 offer 核对、未死/未终局/未冻结、planner 支持该形态（抛物线/无子弹型大招会被拒）、
    /// 本地开火间隔已过，且本人复制资源足够（普通攻击看 Mana ≥ 该英雄普通攻击蓝耗；大招看能量满）。
    /// 它只用于 UI 置灰/提示，**不**构成新的权威，也不阻止 UI 尝试（尝试仍由 TryCombatAttack 真实判定）。
    ///
    /// <see cref="LastReject"/> 是最近一次「本地 planner 拒绝」或「DS 裁决拒绝」的人读原因（无则 null）。
    /// </summary>
    public struct PMUiCombatSnapshot
    {
        /// <summary>本人当前 HP（DS 复制值）。</summary>
        public readonly int Hp;

        /// <summary>本人 MaxHp（DS 复制值；尚未复制到时为 0）。</summary>
        public readonly int MaxHp;

        /// <summary>本人当前 Mana（OwnerOnly 复制值）。</summary>
        public readonly int Mana;

        /// <summary>本人当前 SuperEnergy（OwnerOnly 复制值）。</summary>
        public readonly int SuperEnergy;

        /// <summary>本人是否已死（DS 复制结论）。</summary>
        public readonly bool Dead;

        /// <summary>本局是否已终局（DS 复制结论，或已收到可信结果）。</summary>
        public readonly bool MatchEnded;

        /// <summary>普通攻击此刻是否满足**本地**就绪门（只用于 UI 提示）。</summary>
        public readonly bool NormalReady;

        /// <summary>大招此刻是否满足**本地**就绪门（只用于 UI 提示）。</summary>
        public readonly bool SuperReady;

        /// <summary>最近一次拒绝原因（本地 planner 或 DS 裁决）；无拒绝时为 null。</summary>
        public readonly string LastReject;

        /// <summary>当前对局 ID（不在局内/未知时为 null）。</summary>
        public readonly string MatchId;

        public PMUiCombatSnapshot(int hp, int maxHp, int mana, int superEnergy,
                                  bool dead, bool matchEnded, bool normalReady, bool superReady,
                                  string lastReject, string matchId)
        {
            Hp = hp;
            MaxHp = maxHp;
            Mana = mana;
            SuperEnergy = superEnergy;
            Dead = dead;
            MatchEnded = matchEnded;
            NormalReady = normalReady;
            SuperReady = superReady;
            LastReject = lastReject;
            MatchId = matchId;
        }
    }

    /// <summary>
    /// T-LOOP6：**同局（MatchId+Epoch）重入**的准入决策（唯一决策源，引擎无关）。
    ///
    /// 为什么需要一台独立决策：三条路径在运行期可区分、但后果完全不同 ——
    ///   · <see cref="RejectActiveHealthy"/>：旧会话仍健康（端点就绪）却拿新票进来，
    ///     顶替会把一套**活着的** 世界/预测/输入/表现拆掉重建（双权威、重复输入的源头）⇒ 必须拒绝；
    ///   · <see cref="RejectEndedMatch"/>：该对局已有**可信终局**，恢复它就是回档胜负
    ///     （冻结接口：终局后不恢复输入/战斗）⇒ 必须拒绝；
    ///   · <see cref="ReplaceFaulted"/>：旧会话已故障 / 端点已失败 ⇒ 这是断线续局唯一合法的入口：
    ///     允许**先安全释放**旧状态，再建一个**全新**的 World/预测/表现/输入。
    ///
    /// 注意它**不是**准入校验：票/名册/摘要/世代的核验在 <c>Enter</c> 与 DS 侧照旧执行，
    /// 本决策只管「同局重入时旧状态能不能被替换」。
    /// </summary>
    internal enum PMClientSameMatchEntry
    {
        /// <summary>旧会话仍活跃健康：拒绝顶替（不拆活会话）。</summary>
        RejectActiveHealthy = 0,

        /// <summary>该对局已结束（已有可信终局）：拒绝恢复（终局不可逆）。</summary>
        RejectEndedMatch = 1,

        /// <summary>旧会话已故障 / 端点已失败：允许释放旧状态并重建全新会话。</summary>
        ReplaceFaulted = 2,
    }

    /// <summary><see cref="PMClientSameMatchEntry"/> 的纯策略（单一决策源）。</summary>
    internal static class PMClientSameMatchEntryPolicy
    {
        /// <summary>
        /// 决策表：
        ///   matchEndedKnown=true                → RejectEndedMatch（终局优先，端点故障也不例外）
        ///   sessionFaulted || !endpointUsable   → ReplaceFaulted
        ///   其它（活跃健康）                     → RejectActiveHealthy
        /// </summary>
        public static PMClientSameMatchEntry Decide(bool sessionFaulted, bool endpointUsable, bool matchEndedKnown)
        {
            if (matchEndedKnown)
            {
                return PMClientSameMatchEntry.RejectEndedMatch;
            }

            if (sessionFaulted || !endpointUsable)
            {
                return PMClientSameMatchEntry.ReplaceFaulted;
            }

            return PMClientSameMatchEntry.RejectActiveHealthy;
        }
    }

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

        // ---- 本轮视觉修复：正式模式相机遮挡探测 ----

        /// <summary>
        /// 相机遮挡球体扫掠的命中缓冲容量（只取最近的一个，容量小且有界）。
        /// **静态复用**：宿主同一时刻只有一个会话、且这些方法只在主线程调（与所有驱动调用同纪）。
        /// </summary>
        private const int CameraOcclusionHitCapacity = 8;

        /// <summary>复用的命中缓冲（避免每帧遮挡探测都新分配数组）。</summary>
        private static readonly RaycastHit[] CameraOcclusionHits = new RaycastHit[CameraOcclusionHitCapacity];

        /// <summary>
        /// 相机遮挡球体扫掠**命中缓冲饱和**的累计次数（T-VIS2b 登记的残留风险度量）。
        ///
        /// 饱和 = <c>count &gt;= 容量</c>：批量重载不保证有序，被丢弃的命中里理论上可能有更近的一面墙。
        /// 本宿主**不**因此把遮挡当 "无遮挡"（那会让相机退到想要距离 = 穿墙），
        /// 而是仍然取已返回的最近命中，并把饱和次数暴露出来（首次出现时上一条警告）。
        /// 这只能度量、不能强保证——真实防穿墙仍需 Unity 内实机验证（见 Docs/plans/_visual_camera_fix.md）。
        /// </summary>
        private static int CameraOcclusionSaturationCount;

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
            /// 正式模式（ContentFormal）下的正式角色表现；诊断模式或 SP 非正式路径下为 null。
            ///
            /// 相机位姿需要直接写到 <see cref="PMUnityBattlePresentation.ApplyTestCameraPose"/>，
            /// 而 <see cref="IRigPresentation"/> 只有 Apply/Dispose 两个面，因此这里额外持一个强类型引用；
            /// 它只用于相机（本地 owner），不改变 Apply 的唯一写入口语义。
            /// </summary>
            public PMUnityBattlePresentation BattlePresentation;

            /// <summary>
            /// 正式相机跟随的**观察目标**平滑状态（米，世界坐标）。
            ///
            /// 为什么平滑的是“观察目标”而不是“相机位置”：相机位置 = 观察目标 + 固定后向 × 距离，
            /// 平滑观察目标就等价于按旧链的 SmoothTime（0.08s）跟随，同时让遮挡探测 / 近裁面 /
            /// 遮挡回退都继续在**同一条固定后向**上求解（不会因为平滑而与探测方向脱节）。
            /// </summary>
            public PMVector3 CameraLookTarget;

            /// <summary>平滑速度状态（交给 <c>PMBattleCameraGeometry.SmoothFollowPosition</c> 逐帧带回）。</summary>
            public PMVector3 CameraLookVelocity;

            /// <summary>
            /// 相机跟随是否已初始化。false 时首个位姿**直接贴合**：出生/进场/换局不得看到
            /// 镜头从占位位姿滑动过去（旧链同样有首帧直接到位的语义）。
            /// </summary>
            public bool CameraFollowInitialized;

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

            /// <summary>本局安全局内摇杆/UI；只读旧皮肤、输入委托新链，退局一定释放。</summary>
            public PMUnityBattleControls Controls;

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
            /// T-LOOP3：本会话的**终局 ACK 已发出**证据（见 <see cref="FlushCombatState"/>）。
            ///
            /// 只有它为真时，宿主的正常退场入口才受理「返回大厅」按钮，HUD 才绘制可点的结算弹窗
            /// （契约：「同帧先发 ServerCombatResultAck 才使按钮可点」）。
            /// </summary>
            public bool TerminalAckSent;

            /// <summary>T-LOOP3：本会话发过终局 ACK 的帧数（只增；诊断/门禁对账用）。</summary>
            public long TerminalAckSends;

            /// <summary>
            /// T-LOOP3：本次会话的身份令牌（<see cref="PMClientSessionHost"/> 发号，非 0）。
            ///
            /// 它是「结算弹窗的返回大厅按钮属于哪一局」的唯一凭据：HUD 在终局后会被保留
            /// （跨到大厅、可能跨到下一局），宿主入口逐次比对令牌，不匹配一律拒绝
            /// （跨局陈旧回调绝不关闭新会话）。
            /// </summary>
            public long IdentityToken;

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

            // ---- T-PLAY4：UI 摇杆与独立瞄准（对外冻结入口是 static 方法；状态属于本会话）----

            /// <summary>
            /// UI 攻击边沿（由 <see cref="TryQueueUiAttack"/> 入队，主线程 Pump 统一消费一次）。
            ///
            /// 与 F/G 键盘边沿**分开两个位**：它们是两个不同的输入源，且 UI 边沿自带
            /// 独立瞄准向量（“攻击摇杆独立决定弹道”），不能与“按 predicted yaw 推前向”混成一个位。
            /// </summary>
            public bool UiAttackEdgeBuffered;

            /// <summary>排队的 UI 攻击是否为大招（与边沿同生同死）。</summary>
            public bool UiAttackIsSuper;

            /// <summary>
            /// 排队的 UI 攻击的**世界单位瞄准向量**。
            ///
            /// 同一次攻击的 planner 方向、枪口偏移、与上行方向全部取这一个向量
            /// （不能用“UI 显示一个方向、上行另一个方向”）。
            /// </summary>
            public float UiAimWorldX;
            public float UiAimWorldZ;

            // ---- T-PLAY4 可观测计数（只增；心跳/门禁对账用）----

            public long UiMoveAccepted;
            public long UiMoveRejected;
            public long UiAttackQueued;
            public long UiAttackRejected;
            public long UiAttackDuplicate;

            // ---- T-LIVE3：世界空间瞄准指示（纯表现；只为本地 Owner）----

            /// <summary>
            /// 瞄准指示器（纯表现）。创建失败时保持 null（降级为“无瞄准线”，**不**判整局失败）——
            /// 一条瞄准线不应该让玩家打不了这一局；但原因一定记在 <see cref="AimIndicatorError"/>
            /// 并进心跳，绝不静默。DS/远端 SP 不创建、不更新。
            /// </summary>
            public PMUnityBattleAimIndicator AimIndicator;

            /// <summary>瞄准指示器创建失败的原因（null = 正常）；只用于诊断/对账。</summary>
            public string AimIndicatorError;

            /// <summary>当前是否处于“按住瞄准摇杆”的实时预览态（由 <see cref="TrySetUiAim"/> 维护）。</summary>
            public bool AimPreviewActive;

            /// <summary>预览中的摇杆是否大招（仅为重建计划缓存用）。</summary>
            public bool AimPreviewIsSuper;

            /// <summary>
            /// 预览中的**世界单位**瞄准向量。
            ///
            /// 与排队攻击的 <see cref="UiAimWorldX"/> **分开两个字段**：排队向量属于“即将上行的一次攻击”，
            /// 本字段属于“此刻正按住的方向”；两者取值路径相同（都是 TryScreenAimDirection），
            /// 但生命周期不同（前者在消费点清，后者在松手/停局清）。
            /// </summary>
            public float AimPreviewWorldX;
            public float AimPreviewWorldZ;

            /// <summary>预览用的计划缓存（长度/扇形只来自它；与 TryCombatAttack 同一 planner 入口）。</summary>
            public PMCombatAttackPlan AimPlan;
            public int AimPlanHeroId;
            public bool AimPlanIsSuper;
            public float AimPlanDirX;
            public float AimPlanDirZ;

            /// <summary>扇形方向的复用缓冲（避免每帧为 LineRenderer 分配数组）。</summary>
            public float[] AimFanDirX;
            public float[] AimFanDirZ;

            // ---- T-LIVE3 可观测计数（只增；心跳/门禁对账用）----

            public long AimPreviewPushes;
            public long AimPreviewRejected;
            public long AimPreviewClears;
            public long AimPlanBuilds;
            public long AimIndicatorUpdates;
            public long AimIndicatorRejected;
        }

        private static Session _active;
        private static bool _stopping;

        /// <summary>
        /// T-LIVE3：预览计划的**缓存量化**阈值（世界方向分量差）。
        ///
        /// 为什么需要：拖动摇杆时方向几乎每帧都变，但 planner 每建一次计划就要分配一个
        /// `Directions` 数组；把“几乎同向”的连续帧视为同一方向，可避免无意义分配，
        /// 同时不会让画出的方向与上行方向出现可感知差异（这只是**预览**缓存，上行仍用当帧真实向量）。
        /// </summary>
        private const float AimPlanDirectionEpsilon = 1e-3f;

        /// <summary>
        /// 会话建立时记录的**主线程** id（T-PLAY4）。
        ///
        /// 为什么必须记：UI 输入只能在主线程入队（Unity 的 uGUI 事件本来就在主线程，
        /// 但本入口是 static 的，其他线程也能调到）。若允许异线程入队，它就会与本帧 Pump 里
        /// 的消费争同一个状态位（“谁先跑”不确定）；因此这里**显式拒绝并计数**，
        /// 而不是“看上去能用”。0 = 尚未记录（不入局时不误拒）。
        /// </summary>
        private static int _mainThreadId;

        /// <summary>
        /// R6-C：保留中的只读终局 HUD（收到可信终局结果后不销毁，直到**下一 Enter 或显式 Stop**）。
        ///
        /// 它同时是「不泄漏无限创建」的阀门：全生命周期最多存在一个实例（Enter 前先释放旧的，
        /// 会话失败路径由 ReleaseSession 释放本会话那一份）。
        /// </summary>
        private static PMUnityCombatHud _retainedHud;

        /// <summary>
        /// T-LOOP3：会话身份令牌的发号器（每个 <see cref="Enter"/> 递增，全生命周期内唯一）。
        ///
        /// 用途：把「结算弹窗的返回大厅按钮」与**具体某一次会话**绑定。HUD 在终局后会被保留
        /// （跨到大厅、可能跨到下一局），令牌是「这次点击属于哪一局」的唯一凭据；
        /// 令牌不匹配一律拒绝，因此陈旧回调不可能关掉新会话。
        /// 从 1 开始（0 = 未绑定）；到达 long.MaxValue 时回到 1（实际不可能到达，仅为有界性）。
        /// </summary>
        private static long _nextSessionToken = 1L;

        // ---- R6-C：最近一次可信终局快照（只读，供后续大厅 UI；下一 Enter / 显式 Stop 清除）----

        private static bool _lastCombatResultValid;
        private static uint _lastCombatOutcomeId;
        private static int _lastCombatWinnerTeamId;
        private static int _lastCombatLocalTeamId;
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
        /// 最近一次可信终局时**本方**的队伍号（原始 TeamId；未知为 0）。
        ///
        /// 只供大厅只读展示（“胜/负/平”）用：它来自当时会话的 offer / 本人副本，
        /// **不参与任何权威判定**，也不是从上行包自报采纳的。
        /// </summary>
        public static int LastCombatLocalTeamId { get { return _lastCombatResultValid ? _lastCombatLocalTeamId : 0; } }

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

        /// <summary>
        /// T-LOOP3 公开入口：请求从「结算弹窗」返回大厅（**会话身份绑定**）。
        ///
        /// 它是 <see cref="PMUnityCombatHud"/> 里「返回大厅」按钮唯一的宿主落点，四条判据全部 fail closed：
        ///   · 已退场/已换局（<c>_active</c> 为空）→ 拒绝（陈旧点击无效）；
        ///   · 令牌与**当前**会话不一致（含令牌为 0 的未绑定态）→ 拒绝
        ///     —— **跨局陈旧回调绝不关闭新会话**；
        ///   · 未拿到可信终局（<see cref="Session.TerminalResultObserved"/> 为假）→ 拒绝
        ///     （不得绕过结算直接从按钮退场）；
        ///   · 本帧终局 ACK 未发出（<see cref="Session.TerminalAckSent"/> 为假）→ 拒绝
        ///     （契约：「同帧先发 ServerCombatResultAck 才使按钮可点」）。
        ///
        /// 受理后走 <see cref="EndSessionNormally"/>（**不是** <see cref="Stop"/>）：
        /// 会话按正常路径释放（隔离物理场景卸载、相机恢复、端点释放、帧驱动保留），
        /// 只读终局 HUD/结论**保留**到下一 Enter 或显式 Stop。
        /// </summary>
        /// <param name="sessionToken">HUD 绑定时登记、点击时回传的会话身份令牌。</param>
        /// <returns>true = 已受理并真正退场；false = 拒绝（任何一条判据不满足）。</returns>
        public static bool TryRequestReturnToLobbyFromHud(long sessionToken)
        {
            Session session = _active;
            if (session == null) { return false; }
            if (session.IdentityToken == 0L || session.IdentityToken != sessionToken) { return false; }
            if (!session.TerminalResultObserved) { return false; }
            if (!session.TerminalAckSent) { return false; }

            EndSessionNormally(session, "玩家点击『返回大厅』（会话身份校验通过）");

            // “受理”必须以**真的退场了**为准：若 EndSessionNormally 因重入闩锁/已收尾而早退，
            // 这里必须返回 false，让 HUD 把这次点击计入拒绝（而不是假装成功、按钮却还点着）。
            return !ReferenceEquals(_active, session);
        }

        /// <summary>保留中的只读终局 HUD 是否还在（诊断/门禁对账）。</summary>
        public static bool HasRetainedHud { get { return _retainedHud != null && !_retainedHud.IsDisposed; } }

        // =================================================================================
        //  T-PLAY4：UI 输入与只读快照（对外冻结面）
        // =================================================================================
        //  公开契约（UI 表现组**只读**依赖，Docs/plans/net-architecture-migration.md
        //  「T-PLAY4 UI与输入接口冻结」）：
        //    · TrySetUiMove(float screenX, float screenY) bool
        //    · TryQueueUiAttack(bool isSuper, float screenX, float screenY) bool
        //    · TryGetUiCombatSnapshot(out PMUiCombatSnapshot) bool
        //  屏幕系约定：相对**用户选定相机**（世界 yaw −90，与 T-PLAY1 同源）：
        //    screen up → world −X；screen right → world +Z（变换在 PMUnityMoverInput 里，可在 net8 断言）。
        //
        //  三条铁律（本区的实现约束）：
        //    1) UI **永不**直接发 RPC、永不写 HP/Mana/Energy/复制字段：它只能“入队一次输入意图”，
        //       上行声明仍然由 Pump 里的 TryCombatAttack 经生成的 RPC 发出（主线程统一消费一次）；
        //    2) 非法输入（NaN/±Infinity/超量程）一律**显式拒绝**（返回 false + 计数），
        //       不静默钳制、不吞掉——否则 UI 显示与上行方向可能不一致；
        //    3) 一次攻击的方向/枪口/planner/上行必须是**同一个向量**（UI 路径取 UI 瞄准向量，
        //       键盘 F/G 取 predicted yaw 兼底），不允许“只改 UI 显示”。

        /// <summary>
        /// UI 移动摇杆（冻结入口）：屏幕坐标（相对用户选定相机世界 yaw −90 的 [-1,1]）→ 本局移动输入。
        ///
        /// 返回值语义：true = 本次输入已生效（含「释放」这种零输入）；false = 拒纳
        /// （不在局内 / 非主线程 / 非 finite / 超量程 / 本局已冻结、终局或本人已死）。
        /// **拒绝不改变已有摇杆状态**（非法值不会被静静写成某个合法值），但下列两种情形会顺手清掉推杆：
        ///   · 本局不再接受输入（冻结/终局/死亡）时的非零推动；
        ///   · 释放（零输入）本身。
        /// 这样“摇杆粘住”不可能跨过冻结/终局/换局（契约：Stop/结束/换局不带出上局 UI 输入）。
        /// </summary>
        public static bool TrySetUiMove(float screenX, float screenY)
        {
            Session session = _active;
            if (session == null || session.Faulted) { return false; }
            if (!IsMainThreadForUi(session)) { session.UiMoveRejected++; return false; }

            PMUnityMoverInput input = session.Input;
            if (input == null) { return false; }

            float worldX;
            float worldZ;
            if (!PMUnityMoverInput.TryScreenVectorToWorld(screenX, screenY, out worldX, out worldZ))
            {
                // 非 finite / 超量程：不吞非法（返回 false 让 UI 能看见），也不改已有状态。
                session.UiMoveRejected++;
                return false;
            }

            // 释放（零向量）：**合法且总是接受**——清空不会产生任何位移，
            // 因此即使在冻结/死亡帧也必须能清（否则冻结后再释放也清不掉，摇杆会永久粘住）。
            if (worldX == 0f && worldZ == 0f)
            {
                input.ClearUiMove();
                return true;
            }

            // 非零推动：本局已不接受输入时拒绝，并顺手清掉可能残留的推动。
            if (session.MovementFrozen || session.MatchEndedFrozen || session.TerminalResultObserved
                || IsOwnerDeadOrEnded(session))
            {
                input.ClearUiMove();
                session.UiMoveRejected++;
                return false;
            }

            input.SetUiMoveWorld(worldX, worldZ);
            session.UiMoveAccepted++;
            return true;
        }

        /// <summary>
        /// T-LIVE3 冻结入口：攻击摇杆的**实时瞄准预览**（按住/拖动期间反复调用；<paramref name="active"/>=false 表示隐藏）。
        ///
        /// 与 <see cref="TryQueueUiAttack"/> 的关系（本区最关键的一条）：
        ///   · 两者用**同一份** <c>PMUnityMoverInput.TryScreenAimDirection</c> 把屏幕向量转成世界**单位**向量，
        ///     因此“看到的方向”就是“打出去的方向”（不偏轴、不是两个源）；
        ///   · 本入口**只**改预览状态：绝不排队攻击、绝不发 RPC、绝不写权威 ——
        ///     一次手势仍只由松手边沿的 <see cref="TryQueueUiAttack"/> 提交一次。
        ///
        /// 返回值：true = 已受理（含 active=false 的隐藏）；false = 拒纳（非本局/非主线程/非有限值/
        /// 本局已冻结、终局、本人已死、本人身份未核对）。
        /// </summary>
        public static bool TrySetUiAim(bool isSuper, bool active, float screenX, float screenY)
        {
            Session session = _active;
            if (session == null || session.Faulted) { return false; }
            if (!IsMainThreadForUi(session)) { session.AimPreviewRejected++; return false; }

            if (!active)
            {
                // 隐藏总是合法：松手/取消/停局路径必须能**立刻**关掉线（不等下一帧）。
                ClearUiAim(session);
                return true;
            }

            float aimX;
            float aimZ;
            if (!PMUnityMoverInput.TryScreenAimDirection(screenX, screenY, out aimX, out aimZ))
            {
                // 非 finite / 超量程 / 零方向：不吞非法（返回 false 让 UI 能看见），也不改已有预览。
                session.AimPreviewRejected++;
                return false;
            }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null || !session.OwnerIdentityVerified)
            {
                session.AimPreviewRejected++;
                return false;
            }

            if (session.MovementFrozen || session.MatchEndedFrozen || session.TerminalResultObserved
                || IsOwnerDeadOrEnded(session))
            {
                // 本局已不接受输入：拒纳并清掉预览（不留一根冻住的线）。
                ClearUiAim(session);
                session.AimPreviewRejected++;
                return false;
            }

            session.AimPreviewActive = true;
            session.AimPreviewIsSuper = isSuper;
            session.AimPreviewWorldX = aimX;
            session.AimPreviewWorldZ = aimZ;
            session.AimPreviewPushes++;
            return true;
        }

        /// <summary>
        /// UI 攻击（冻结入口）：把一次「攻击手势 + 独立瞄准方向」入队，由主线程 Pump **统一消费一次**。
        ///
        /// 排攻击的前置（契约原文「只有活动会话 Owner 正确/未 dead/未 terminal/未 frozen 时排攻击」）：
        ///   · 在局内且会话未 fault；
        ///   · 主线程；
        ///   · 本人副本已复制且身份已与 offer 核对（<see cref="Session.OwnerIdentityVerified"/>）
        ///     —— “Owner 正确”不能拿“默认 0 也是合法英雄”蒙过去；
        ///   · 未 dead、未终局、未冻结；
        ///   · 瞄准方向合法（非 finite/超量程/零方向一律拒绝：**不默认**替玩家选一个方向）。
        ///
        /// 资源/间隔门**不**在这里判（它们由消费点的 planner + 本地间隔门 + DS 裁决负责），
        /// 但就绪与否由 <see cref="TryGetUiCombatSnapshot"/> 的 NormalReady/SuperReady 提前告知 UI。
        ///
        /// 重复提交（同一帧重复调、按住拖动反复调）**不会**堆出多次上行：已有未消费的攻击边沿时直接拒绝
        /// （计 UiAttackDuplicate），因此一次手势只对应一次 RPC，不会因为 UI 毛刺多打一枪。
        /// </summary>
        public static bool TryQueueUiAttack(bool isSuper, float screenX, float screenY)
        {
            Session session = _active;
            if (session == null || session.Faulted) { return false; }
            if (!IsMainThreadForUi(session)) { session.UiAttackRejected++; return false; }

            float aimX;
            float aimZ;
            if (!PMUnityMoverInput.TryScreenAimDirection(screenX, screenY, out aimX, out aimZ))
            {
                session.UiAttackRejected++;
                return false;
            }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null || !session.OwnerIdentityVerified)
            {
                session.UiAttackRejected++;
                return false;
            }

            if (owner.CombatDead || owner.CombatMatchEnded
                || session.MovementFrozen || session.MatchEndedFrozen || session.TerminalResultObserved)
            {
                // 本局已不接受攻击：拒绝并清掉本会话全部 UI 输入（含已排队的边沿）。
                session.UiAttackRejected++;
                ClearUiInput(session);
                return false;
            }

            if (session.UiAttackEdgeBuffered)
            {
                // 同一帧已有未消费的攻击边沿：不重复排队（一次手势 = 一次上行）。
                session.UiAttackDuplicate++;
                return false;
            }

            session.UiAttackEdgeBuffered = true;
            session.UiAttackIsSuper = isSuper;
            session.UiAimWorldX = aimX;
            session.UiAimWorldZ = aimZ;
            session.UiAttackQueued++;
            return true;
        }

        /// <summary>
        /// 只读战斗快照（冻结入口）：本人 HP/MaxHp/Mana/SuperEnergy、死亡/终局、F/G 就绪位、
        /// 最近拒绝原因，以及本局 matchId（可空）。
        ///
        /// 返回 false 的两种情况：不在局内（已 Stop/换局/从未入局），或本人副本尚未复制。
        /// 它**只读**：不读旧链真值、不写任何资源、不改变本会话输入状态。
        /// </summary>
        public static bool TryGetUiCombatSnapshot(out PMUiCombatSnapshot snapshot)
        {
            snapshot = default(PMUiCombatSnapshot);

            Session session = _active;
            if (session == null) { return false; }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null) { return false; }

            bool normalReady;
            bool superReady;
            ComputeUiReadyFlags(session, owner, out normalReady, out superReady);

            bool matchEnded = owner.CombatMatchEnded || session.MatchEndedFrozen || session.TerminalResultObserved;

            snapshot = new PMUiCombatSnapshot(
                owner.CombatHp, owner.CombatMaxHp, owner.CombatMana, owner.CombatSuperEnergy,
                owner.CombatDead, matchEnded, normalReady, superReady,
                session.LastAttackError,
                session.Offer != null ? session.Offer.MatchId : null);
            return true;
        }

        /// <summary>
        /// 计算 UI 就绪位。**本地建议**：真正的裁决仍在 DS（core 会再判身份/资源/间隔）。
        ///
        /// 它刻意复用两个已有的同一口径判据（不另造第三份规则）：
        ///   · <see cref="IsCombatFireReady"/>（身份已核对 + MaxHp 已复制 + 未死/未终局）；
        ///   · <see cref="PMCombatWeaponPlanner.TryBuild"/>（这种形态是否可打：抛物线/无子弹型大招会被拒）。
        /// 额外的是「本地开火间隔已过」与「本人**复制资源**是否够」（Mana/Energy 均为 OwnerOnly 复制值），
        /// 它们只用于把按钮置灰，不阻止 UI 尝试。
        /// </summary>
        private static void ComputeUiReadyFlags(Session session, PMR3Player owner,
                                               out bool normalReady, out bool superReady)
        {
            normalReady = false;
            superReady = false;

            string readyError;
            if (!IsCombatFireReady(session, owner, out readyError)) { return; }
            if (session.MovementFrozen || session.MatchEndedFrozen || session.TerminalResultObserved) { return; }

            // 与 TryCombatAttack 同一个墙钟口径（realtimeSinceStartup 毫秒）。
            double wallNowMs = (double)(Time.realtimeSinceStartup * 1000f);

            normalReady = ComputeUiReadyForAttack(session, owner, false, wallNowMs);
            superReady = ComputeUiReadyForAttack(session, owner, true, wallNowMs);
        }

        private static bool ComputeUiReadyForAttack(Session session, PMR3Player owner, bool isSuper,
                                                   double wallNowMs)
        {
            // 方向任意单位向量即可：这里只问「这个形态能不能打」，与朝向无关。
            PMCombatAttackPlan plan;
            PMCombatRejectReason reason;
            if (!PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId, isSuper, 1f, 0f, out plan, out reason)
                || plan == null)
            {
                return false;
            }

            if (wallNowMs - session.LastAttackWallMs < (double)plan.FireIntervalMs) { return false; }

            if (isSuper)
            {
                // 大招：满能量才能放（契约 A1：SuperEnergyMax 满后清 0，Mana 不变）。
                return owner.CombatSuperEnergy >= BattleNumericConfig.SuperEnergyMax;
            }

            // 普通攻击：planner 的 ManaCost 就是该英雄的普通攻击蓝耗（不另算一份）。
            return owner.CombatMana >= plan.ManaCost;
        }

        /// <summary>
        /// 清空本会话的**全部 UI 输入**（摇杆 + 已排队的攻击边沿与瞄准向量）。幂等。
        ///
        /// 契约：「Stop/结束/换局不带出上局 UI 输入」。因此凡「本局不再接受输入」的路径
        /// （冻结、死亡、终局、显式 Stop、ReleaseSession）都必须调它。
        /// </summary>
        private static void ClearUiInput(Session session)
        {
            if (session == null) { return; }

            if (session.Input != null) { session.Input.ClearUiMove(); }

            session.UiAttackEdgeBuffered = false;
            session.UiAttackIsSuper = false;
            session.UiAimWorldX = 0f;
            session.UiAimWorldZ = 0f;

            // T-LIVE3：瞄准预览也属于“本局不再接受输入”的范畴（它虽有独立的 setAim 入口，
            // 但停局/死亡/终局/换局时同样必须清干净，否则下一局会先闪一根旧方向的线）。
            ClearUiAim(session);
        }

        /// <summary>
        /// T-LIVE3：清空本会话的瞄准预览状态（并**立即隐藏**指示器）。幂等。
        ///
        /// 与 <see cref="ClearUiInput"/> 同一条“停局不带出上一局输入”的纪律：
        /// 死亡 / 终局 / 冻结 / 显式 Stop / 换局 / 退局 都经它清干净。
        /// </summary>
        private static void ClearUiAim(Session session)
        {
            if (session == null) { return; }

            if (session.AimPreviewActive) { session.AimPreviewClears++; }

            session.AimPreviewActive = false;
            session.AimPreviewIsSuper = false;
            session.AimPreviewWorldX = 0f;
            session.AimPreviewWorldZ = 0f;
            session.AimPlan = null;
            session.AimPlanDirX = 0f;
            session.AimPlanDirZ = 0f;

            PMUnityBattleAimIndicator indicator = session.AimIndicator;
            if (indicator != null && !indicator.IsDisposed) { indicator.Hide(); }
        }

        /// <summary>本人是否已死或本局已按复制结论终局（UI 排攻击/推摇杆的前置）。</summary>
        private static bool IsOwnerDeadOrEnded(Session session)
        {
            if (session == null) { return false; }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null) { return false; }

            return owner.CombatDead || owner.CombatMatchEnded;
        }

        /// <summary>
        /// UI 入口的主线程门。
        ///
        /// 为什么需要它：本入口是 static 的（UI 组只能看到这个面），任何线程都能调到，
        /// 而消费点在每帧 Pump（主线程）里。若允许异线程入队，边沿位就会被两个线程竞写，
        /// “这次攻击算不算”就变成调度顺序的函数。这里显式拒绝 + 警告（可对账），不静默接受。
        /// </summary>
        private static bool IsMainThreadForUi(Session session)
        {
            if (session == null) { return false; }
            if (_mainThreadId == 0) { return true; }

            int current = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (current == _mainThreadId) { return true; }

            HYLDDebug.LogWarning("[PMClientSessionHost] UI 输入来自非主线程（thread="
                                 + current.ToString(CultureInfo.InvariantCulture)
                                 + " 主线程=" + _mainThreadId.ToString(CultureInfo.InvariantCulture)
                                 + "）：拒绝入队（UI 只能在主线程与 Pump 同线程交互）");
            return false;
        }

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

            // T-LOOP6：**已结束的对局不得恢复**（冻结接口：终局不可逆，登录/重入只能只读展示结果）。
            //
            // 它必须在下面「清空上一局只读结果快照」**之前**判定：那份快照正是“该对局已结束”的唯一本地凭据
            // （只由通过校验的可信结果写入，不从任何上行包自报采纳）。命中即明确拒绝，不回旧链、不建世界。
            if (IsMatchAlreadyEnded(offer.MatchId))
            {
                HYLDDebug.LogError("[PMClientSessionHost] 拒绝为**已结束**的对局建会话：match=" + offer.MatchId
                                   + " outcome=" + _lastCombatOutcomeId.ToString(CultureInfo.InvariantCulture)
                                   + " winner=" + _lastCombatWinnerTeamId.ToString(CultureInfo.InvariantCulture)
                                   + "（可信终局已落定，不恢复输入/战斗，不回旧链）");
                return;
            }

            // 2) 同局重入（match+epoch 相同）：**只允许在旧会话已故障/端点失败时**释放旧状态并重建全新会话。
            //    活跃健康会话不得被顶替（否则会拆掉一套活着的世界/预测/输入）；已结束对局在上面已被拒。
            if (_active != null && SameMatch(_active.Offer, offer))
            {
                PMClientSameMatchEntry decision = PMClientSameMatchEntryPolicy.Decide(
                    _active.Faulted, IsEndpointUsable(_active), IsMatchAlreadyEnded(offer.MatchId));

                if (decision == PMClientSameMatchEntry.RejectActiveHealthy)
                {
                    HYLDDebug.LogError("[PMClientSessionHost] 拒绝同局重入：当前会话仍活跃健康（match=" + offer.MatchId
                                       + " epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                                       + "），不顶替活跃会话、不重建世界（拒绝明示，不回旧链）");
                    return;
                }

                if (decision == PMClientSameMatchEntry.RejectEndedMatch)
                {
                    HYLDDebug.LogError("[PMClientSessionHost] 拒绝同局重入：该对局已有可信终局（match=" + offer.MatchId
                                       + "），已结束对局不得恢复（不回旧链）");
                    return;
                }

                // ReplaceFaulted：断线续局唯一合法的入口。先**安全释放**旧局（旧 socket / static 事件 /
                // 旧世界与表现），随后与“无会话”路径逐字相同地建一个**全新**会话
                // （新 World / 新 Prediction / 新表现 / 新输入）—— 绝不把旧本地预测或旧假弹带过来。
                HYLDDebug.Log("[PMClientSessionHost] 同局重入：旧会话已故障/端点已失败（reason="
                              + (_active.FaultReason ?? "<none>")
                              + "），先释放旧状态再重建全新会话（match=" + offer.MatchId + "）");
                Stop();
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

            // T-PLAY4：记下本会话的主线程（UI 输入必须与每帧 Pump 同线程，见 IsMainThreadForUi）。
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            Session session = new Session();
            session.Offer = offer;

            // T-LOOP3：本次会话的身份令牌（结算弹窗的「返回大厅」按钮与它绑定）。
            // 发号器与 HUD 的令牌比对是「跨局陈旧回调不关闭新会话」的唯一凭据。
            session.IdentityToken = _nextSessionToken;
            _nextSessionToken = _nextSessionToken >= long.MaxValue ? 1L : _nextSessionToken + 1L;

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

            // T-LOOP3：把「返回大厅」入口与**本次会话的身份令牌**一并绑到 HUD 上。
            //   · 令牌保证陈旧绑定/跨局点击不可能关掉新会话（宿主入口逐次比对）；
            //   · 宿主入口只在「可信终局已存 + 本帧终局 ACK 已发」时受理，受理后走
            //     EndSessionNormally（**不是** Stop）—— 结论与只读终局 HUD 都会保留。
            session.Hud.BindLobbyReturn(session.IdentityToken, TryRequestReturnToLobbyFromHud);

            // T-PLAY4：新局内 Canvas 与旧 GameUI 只有只读 Sprite/布局关联，绝不激活旧 prefab。
            // 缺资源/挂接失败和 HUD 一样明确拒绝入局，而不是静默回到只有 F/G 的诊断画面。
            try
            {
                string controlsError;
                session.Controls = PMUnityBattleControls.Create(offer.MatchId, out controlsError);
                if (session.Controls == null)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 局内操控 UI 创建失败：" + controlsError + "（不回旧链）");
                    return;
                }

                session.Controls.Bind(TrySetUiMove, TryQueueUiAttack, QueryBattleControlsStatus, TrySetUiAim);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] 局内操控 UI 接线失败："
                                   + ex.GetType().Name + " " + ex.Message + "（不回旧链）");
                return;
            }

            // T-LIVE3：世界空间瞄准指示器（**纯表现**，只为本地 Owner 服务；DS/远端 SP 不创建）。
            //
            // 与 HUD/操控 UI 的差别：它失败**不**判整局失败。理由：它不承载任何输入/权威/数据语义，
            // 一条线缺失不可能被误读成“没有数据”；而因一条线让玩家整局打不了反而更糟。
            // 但失败绝不静默：原因记进会话并随心跳输出（AimIndicatorError），玩家仍能正常开火。
            try
            {
                string aimError;
                session.AimIndicator = PMUnityBattleAimIndicator.Create(offer.MatchId, out aimError);
                if (session.AimIndicator == null)
                {
                    session.AimIndicatorError = aimError;
                    HYLDDebug.LogWarning("[PMClientSessionHost] 世界瞄准指示器创建失败（降级为无瞄准线，"
                                         + "不判整局失败）：" + aimError);
                }
            }
            catch (Exception ex)
            {
                session.AimIndicatorError = ex.GetType().Name + " " + ex.Message;
                HYLDDebug.LogWarning("[PMClientSessionHost] 世界瞄准指示器创建异常（降级为无瞄准线）："
                                     + ex.GetType().Name + " " + ex.Message);
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
                    UpdateBattleControls(session);

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

                UpdateBattleControls(session);

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

            UpdateBattleControls(session);

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

                try
                {
                    // 相机与角色表现同源：都用**同一个** predicted 位姿，避免两者相差一帧。
                    PMMoverSyncState predicted = rig.Driver.GetPredictedSync();

                    rig.Presentation.ApplyPredicted(predicted);

                    // 正式模式：本地 owner 的相机跟着刚写入的呈现位姿走（固定方位角 + 遮挡回退）。
                    if (!UpdateOwnerBattleCamera(session, rig, predicted))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Fail(session, "AP 表现写入异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// 把本地 owner 的正式相机跟到**刚写入的**呈现位姿上。
        ///
        /// 关键点（逐条对应 T-PLAY1 验收）：
        ///   · 位置跟随角色**世界坐标**，但方位角/俯角/后距取旧预制体冻结常量
        ///     （<see cref="PMBattleCameraGeometry.FixedCameraYawDegrees"/>，**没有** owner Yaw 入参）
        ///     ⇒ 左右方向键不可能再让镜头 90/180 度瞬转，且取景回到旧俯视（yaw −90 / 俯角 68.191）；
        ///   · 观察目标 = 角色世界位置 + 观察高度（固定目标，不是角色前方点），
        ///     并用旧链的 SmoothTime（0.08s）临界阻尼跟随；首帧直接贴合、大跳变（出生/瞬移）直接贴合；
        ///   · 相机位置 = 观察目标 + 固定后向 × （遮挡钳制后的）距离，另得到随距离收紧的近裁面；
        ///   · 遮挡用**本局隔离物理场景**里的只读球体扫掠（地图白名单 layer mask），
        ///     探测方向与相机后向同一口径（即按新的 yaw/俯角更新查询），只决定表现层后退距离：
        ///     **不改**权威碰撞、不参与 Mover 仿真、不需要改 DS；
        ///   · 只对“正式模式 + 本地 owner”生效：远端 SP 与诊断（胶囊）路径完全不变。
        ///
        /// 返回 false 表示已调用 Fail（整局失败）；true 包括“本 rig 不归本路径管”。
        /// </summary>
        private static bool UpdateOwnerBattleCamera(Session session, MovementRig rig, PMMoverSyncState state)
        {
            PMUnityBattlePresentation battle = rig.BattlePresentation;
            if (battle == null || battle.Disposed || battle.TestCamera == null)
            {
                return true;
            }

            // 观察目标 = 角色世界位置 + 观察高度（固定目标；不含任何朝向信息）。
            PMVector3 desiredLookTarget = PMBattleCameraGeometry.ComputeLookTarget(
                state.Position, PMBattleCameraGeometry.DefaultLookHeightMeters);

            // 旧镜头手感：位置按旧链 SmoothTime 临界阻尼跟随（rotation 是固定常量，不参与平滑）。
            PMVector3 lookTarget;
            if (!rig.CameraFollowInitialized)
            {
                // 首帧直接贴合：出生/进场/换局不得看到镜头从占位位姿滑过去。
                lookTarget = desiredLookTarget;
                rig.CameraLookVelocity = new PMVector3(0f, 0f, 0f);
                rig.CameraFollowInitialized = true;
            }
            else
            {
                // 用与子步累加器同源的帧时长（非法/零帧时长时平滑退化为“保持不动”）。
                float deltaSeconds = (float)(CurrentFrameElapsedMs() / 1000.0);

                lookTarget = PMBattleCameraGeometry.SmoothFollowPosition(
                    rig.CameraLookTarget, desiredLookTarget, ref rig.CameraLookVelocity,
                    PMBattleCameraGeometry.FollowSmoothTimeSeconds, deltaSeconds,
                    PMBattleCameraGeometry.FollowSnapDistanceMeters);
            }

            rig.CameraLookTarget = lookTarget;

            float occlusion = ProbeBattleCameraOcclusion(session, lookTarget);

            PMBattleCameraPose pose = PMBattleCameraGeometry.ComputeAtLookTarget(
                lookTarget,
                PMBattleCameraGeometry.DefaultDistanceMeters,
                PMBattleCameraGeometry.DefaultYawDegrees,
                PMBattleCameraGeometry.DefaultPitchDegrees,
                PMBattleCameraGeometry.DefaultNearClipMeters,
                occlusion,
                PMBattleCameraGeometry.MinDistanceMeters,
                PMBattleCameraGeometry.OcclusionMarginMeters);

            try
            {
                battle.ApplyTestCameraPose(pose.CameraPosition, pose.YawDegrees, pose.PitchDegrees,
                                          pose.NearClipMeters);
            }
            catch (Exception ex)
            {
                Fail(session, "相机位姿写入异常：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 可见障碍投射：从**观察目标**沿“后向”在**本局隔离物理场景**里做一次只读**球体扫掠**。
        ///
        /// 参数就是当前帧的观察目标（已包含观察高度与跟随平滑），因此探测起点/方向与
        /// 相机实际求解口径完全一致（按新的 yaw −90 / 俯角 68.191 更新，最大距离 = 新的后距）。
        ///
        /// 为什么用球体扫掠而不是一条无体积射线：相机有体积（近裁面 + 视锥宽度），单条射线会从
        /// 墙角/棱边擦过去，相机贴墙时仍然穿模。球体半径在纯几何模块里定义为
        /// <see cref="PMBattleCameraGeometry.ProbeRadiusMeters"/>，再配合安全边距作保守近似。
        ///
        /// 边界（为什么这样是安全的）：
        ///   · 只用 <c>session.Query</c> 绑定的那个 <see cref="PhysicsScene"/>（本地非默认物理世界）
        ///     与地图白名单 layer mask —— 与运动查询同源，不会打到大厅/旧地图几何；
        ///   · 只得到**表现层要用的距离**：不移动任何 Transform、不调用 Physics.Simulate、
        ///     不改任何全局物理开关、更不写权威碰撞（DS 完全不需要参与）；
        ///   · 忽略 trigger（与地图校验"trigger 不算硬障碍"同口径）；
        ///   · 命中缓冲饱和、命中起点重叠的处理见下（T-VIS2b）。
        ///
        /// 为什么本探测**不可能**打到角色自己（否则会每帧自遮挡）：正式模式的角色表现
        /// （<c>PMUnityBattlePresentation</c> / 投射物占位球）按契约**不带 Collider**，
        /// 隔离场景里的 Collider 只有地图烘焙产物那一份；且运动查询也是同一白名单。
        /// 该结论由代码事实（构造时拒 Collider + 白名单）支撑，不由实机截屏支撑。
        ///
        /// 诚实边界（登记为残留风险，不声明"完全防穿墙"）：
        ///   · 起点（角色头部观察点）嵌在障碍体内时，PhysX 会把它报成 distance ≈ 0 的
        ///     "起点接触/重叠"且法线不可信：已由
        ///     <see cref="PMBattleCameraGeometry.ResolveProbeOcclusion"/> 归一为**贴脸距离**
        ///     （而不是静默当无遮挡），但真实遮挡距离为 0 时无法做到"严格小于它"；
        ///   · 比探测球（半径 <see cref="PMBattleCameraGeometry.ProbeRadiusMeters"/>）还薄的墙、
        ///     低矮天花板下的盲区等情形，扫掠可能一次都不命中——那时只能返回无遮挡哨兵，
        ///     无法强保证不穿墙（要鲁棒需要多次投射/体积裁剪，本轮不做）；
        ///   · 命中缓冲饱和（count &gt;= 容量）时批量重载不保证有序，被丢弃的命中里理论上可能
        ///     有更近的一面墙；本方法不静默当无遮挡且计数告警（
        ///     <see cref="CameraOcclusionSaturationCount"/>），但不能强保证取到的就是最近命中。
        /// </summary>
        private static float ProbeBattleCameraOcclusion(Session session, PMVector3 lookTarget)
        {
            if (session == null || session.Query == null)
            {
                return PMBattleCameraGeometry.NoOcclusionDistance;
            }

            PhysicsScene scene = session.Query.Scene;
            if (!scene.IsValid() || scene.Equals(Physics.defaultPhysicsScene))
            {
                return PMBattleCameraGeometry.NoOcclusionDistance;
            }

            PMVector3 back = PMBattleCameraGeometry.BackDirection(
                PMBattleCameraGeometry.DefaultYawDegrees, PMBattleCameraGeometry.DefaultPitchDegrees);

            Vector3 origin = new Vector3(lookTarget.X, lookTarget.Y, lookTarget.Z);
            Vector3 direction = new Vector3(back.X, back.Y, back.Z);

            int count = scene.SphereCast(origin, PMBattleCameraGeometry.ProbeRadiusMeters, direction,
                                         CameraOcclusionHits, PMBattleCameraGeometry.DefaultDistanceMeters,
                                         session.Query.LayerMask, QueryTriggerInteraction.Ignore);
            if (count <= 0)
            {
                return PMBattleCameraGeometry.NoOcclusionDistance;
            }

            if (PMBattleCameraGeometry.IsProbeSaturated(count, CameraOcclusionHits.Length))
            {
                // 饱和不是"无遮挡"（下面仍取已返回的最近命中），但被丢弃的命中里理论上可能更近：
                // 只度量 + 首次警告，不静默吞掉（正式防穿墙仍需实机验证）。
                if (CameraOcclusionSaturationCount == 0)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 相机遮挡球扫命中缓冲饱和（count="
                                         + count.ToString(CultureInfo.InvariantCulture) + "，容量="
                                         + CameraOcclusionHits.Length.ToString(CultureInfo.InvariantCulture)
                                         + "）：本次仍取已返回的最近命中，但被丢弃的命中理论上可能更近"
                                         + "（登记为残留风险，见 Docs/plans/_visual_camera_fix.md）。");
                }

                CameraOcclusionSaturationCount++;
            }

            // 批量重载不保证顺序：自己取最近命中，避免取到远处的另一面墙。
            float nearest = float.PositiveInfinity;
            int limit = count < CameraOcclusionHits.Length ? count : CameraOcclusionHits.Length;

            for (int i = 0; i < limit; i++)
            {
                float distance = CameraOcclusionHits[i].distance;
                if (float.IsNaN(distance) || distance < 0f) { continue; }
                if (distance < nearest) { nearest = distance; }
            }

            // 归一化（T-VIS2b）：只要有一次命中就绝不返回"无遮挡"。
            // 否则 PhysX 的"起点重叠"（球心已在障碍内，命中 distance ≈ 0）会被 ResolveDistance
            // 当成 0 = 无可用信息，静默退回想要距离（= 相机穿到墙后）。
            return PMBattleCameraGeometry.ResolveProbeOcclusion(count, nearest);
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
            PMUnityBattlePresentation battlePresentation = null;
            try
            {
                if (session.ContentFormal)
                {
                    // R4-C（C3）正式：C1 烘培的正式角色表现（C2 在构造时校验组件集并拒 DS/Authority）。
                    //
                    // T-PLAY1：相机**不由构造函数隐式创建**（createTestCamera=false）——
                    // 取景（后距/方位角/俯角/观察高度/FOV/近远裁面）必须从纯几何模块的
                    // 旧预制体冻结常量**显式**传入宿主唯一一处调用点，避免出现第二份“默认取景”。
                    PMUnityBattlePresentation battle = new PMUnityBattlePresentation(
                        label, player.Role, session.BattleMap.Manifest, false);
                    presentation = new BattleRigPresentation(battle);
                    battlePresentation = battle;

                    if (isOwner)
                    {
                        // 只为本地 owner 建测试相机（远端 SP 绝不建相机：一局只能有一个本地相机）。
                        battle.CreateTestCamera(
                            PMBattleCameraGeometry.DefaultDistanceMeters,
                            PMBattleCameraGeometry.DefaultYawDegrees,
                            PMBattleCameraGeometry.DefaultPitchDegrees,
                            PMBattleCameraGeometry.DefaultLookHeightMeters,
                            PMBattleCameraGeometry.LegacyPrefabFieldOfViewDegrees,
                            PMBattleCameraGeometry.LegacyPrefabNearClipMeters,
                            PMBattleCameraGeometry.LegacyPrefabFarClipMeters);

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
            rig.BattlePresentation = battlePresentation;
            session.Movements.Add(rig);

            // 正式 + 本地 owner：本帧的 PumpMovement 会按 predicted 位姿重写相机；
            // 万一本帧不会再走 PumpMovement（例如刚建完就整局失败），这里先摆一次，
            // 不让占位位姿被渲染出来。失败已由 Fail 幂等登记，不重复报错。
            if (isOwner && battlePresentation != null)
            {
                UpdateOwnerBattleCamera(session, rig, driver.GetPredictedSync());
            }

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
        ///
        /// T-PLAY4 输入来源（两个源共用同一条上行/预测链，但**瞄准向量不同**）：
        ///   · **UI**（<see cref="TryQueueUiAttack"/> 入队）：瞄准取 UI 摇杆的世界向量，
        ///     **独立于 Mover predictedYaw**（这就是“攻击摇杆独立决定弹道”）；
        ///   · **键盘 F/G**（兑底）：仍按 predicted yaw 推世界前向（与表现层同一口径）。
        /// 无论哪个源，本次的 planner 方向、枪口偏移与上行方向都是**同一个** <c>forward</c>。
        /// </summary>
        private static void TryCombatAttack(Session session, double wallNowMs)
        {
            if (!session.NormalAttackEdgeBuffered && !session.SuperAttackEdgeBuffered
                && !session.UiAttackEdgeBuffered)
            {
                return;
            }

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

            // 到这里才消费这次输入边沿（后续所有分支都是「本次尝试」的结局）。
            //
            // T-PLAY4：三个边沿位（F / G / UI）在**同一个消费点**一起清 —— 主线程统一一次消费，
            // 不分开“多次消费”，也不会因为某个源先被处理而把另一个源留在队列里发两份上行。
            // 瞄准向量与 isSuper 必须在清位前取出（UI 的瞄准向量就存在边沿旁边）。
            bool uiEdge = session.UiAttackEdgeBuffered;
            bool isSuper = uiEdge ? session.UiAttackIsSuper : session.SuperAttackEdgeBuffered;
            float uiAimX = session.UiAimWorldX;
            float uiAimZ = session.UiAimWorldZ;

            session.NormalAttackEdgeBuffered = false;
            session.SuperAttackEdgeBuffered = false;
            session.UiAttackEdgeBuffered = false;

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

            // 世界瞄准方向：与表现层同一口径，且**同一次攻击只能用一个向量**。
            //   · UI 源：取 UI 摇杆已校验的世界**单位**向量（独立于 predictedYaw）；
            //   · 键盘 F/G 源：仍按 Mover 真实 predicted yaw 推世界前向（不接旧输入链的假方向）。
            PMVector3 forward;
            if (uiEdge)
            {
                forward = new PMVector3(uiAimX, 0f, uiAimZ);
            }
            else
            {
                double yawRad = (double)predicted.YawDegrees * Math.PI / 180.0;
                forward = new PMVector3((float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
            }

            if (!forward.IsFinite || (forward.X == 0f && forward.Z == 0f))
            {
                session.AttackDriverRejected++;
                session.LastAttackError = "瞄准方向非法（非 finite 或零长度），本次不开火";
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

            // 同一次攻击的 N 颗弹**共用同一个枪口**：预测位置 + 瞄准向量 × 0.6m。
            // 注意：这里的 forward 就是 planner 与上行用的**同一个**向量（UI 时为 UI 摇杆方向），
            // 不存在“只改 UI 显示、枪口/上行还用 yaw”的分叉。
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
        /// R6-C / T-LOOP3：客户端 `R6.FlushState(now)`（契约固定次序的末步）。
        ///
        /// AP 侧它在驱动里的唯一职责是**回 ServerCombatResultAckV1**（ACK 不在复制回调里发），
        /// 因此端点已经不可用时直接跳过：发给一个已经没人收的 socket 只会把「正常退场」变成
        /// `SendFailed` fault，而契约明确要求「已知合法 terminal 结果后 DS 正常退出不应当作 Fail」。
        ///
        /// 返回值 = **本帧是否真的发出了终局 ACK**（T-LOOP3 「返回大厅」按钮的闸门证据）：
        ///   · 驱动在终局帧对客户端的唯一上行出口就是 `ServerCombatResultAckV1`（可靠域生成 RPC），
        ///     因此「<c>PMNetSessionBridge.RpcSent</c> 在**本次** FlushState 调用期间增加」就是
        ///     「ACK 已发出」的可观测证据（测量窗口只有这一次调用，不含本帧更早/更晚的其它上行）；
        ///   · 它是**有界约束**：证明「至少一条生成 RPC 已由桥真实发出」，不宣称 socket 送达对端
        ///     （送达属传输层语义，宿主无法也不应在此断言）。
        /// 端点不可用/缺桥/驱动 fault/异常一律返回 false（由正常退场路径接管，绝不放行按钮）。
        /// </summary>
        private static bool FlushCombatState(Session session, double wallNowMs)
        {
            PMR6CombatDriver combat = session.CombatDriver;
            if (combat == null) { return false; }

            if (combat.IsFaulted || combat.IsDisposed)
            {
                Fail(session, "R6 战斗驱动不可用（faulted=" + combat.IsFaulted.ToString(CultureInfo.InvariantCulture)
                     + " disposed=" + combat.IsDisposed.ToString(CultureInfo.InvariantCulture)
                     + " reason=" + combat.FaultReason + " " + combat.FaultError + "）");
                return false;
            }

            if (!IsEndpointUsable(session)) { return false; }

            PMNetSessionBridge bridge = session.Bridge;
            long rpcSentBefore = bridge != null ? bridge.RpcSent : 0L;

            try { combat.FlushState(wallNowMs); }
            catch (Exception ex)
            {
                Fail(session, "R6 FlushState 异常：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            session.CombatFlushes++;

            if (combat.IsFaulted)
            {
                Fail(session, "R6 战斗驱动失败：" + combat.FaultReason + " " + combat.FaultError);
                return false;
            }

            if (bridge == null) { return false; }

            bool resultAckSent = bridge.RpcSent > rpcSentBefore;
            if (session.TerminalResultObserved && resultAckSent)
            {
                session.TerminalAckSent = true;
                session.TerminalAckSends++;
            }

            return resultAckSent;
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
                if (rig.Player.CombatDead)
                {
                    FreezeRigOnce(rig);

                    // T-PLAY4：本人已死 ⇒ 摇杆与排队攻击立即失效（不是等下一帧的禁开火门）。
                    if (rig.IsOwner) { ClearUiInput(session); }
                }

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

            PublishLastCombatResult(session.Offer != null ? session.Offer.MatchId : null, outcomeId, winnerTeamId,
                                    session.Offer != null ? session.Offer.Identity.TeamId : localTeamId);

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
        private static void PublishLastCombatResult(string matchId, uint outcomeId, int winnerTeamId, int localTeamId)
        {
            _lastCombatResultValid = true;
            _lastCombatOutcomeId = outcomeId;
            _lastCombatWinnerTeamId = winnerTeamId;
            _lastCombatLocalTeamId = localTeamId;
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
            _lastCombatLocalTeamId = 0;
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

                // T-LOOP3：本会话已按**正常路径**退场 ⇒ 结算弹窗保留只读、按钮变「已返回大厅」（不可点）。
                //
                // 为什么只能在这里做：只有「可信终局已存」的会话才会走到 EndSessionNormally
                // （四个调用点均带 TerminalResultObserved / 已保存结果），因此这里绝不会让
                // 不可信断线弹出结算结论；HUD 自身还会再核一次「类内是否真有无可信结论」。
                if (_retainedHud != null) { _retainedHud.SetReturnedToLobby(); }

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
                              + "（帧驱动保留至下一 Enter / 显式 Stop）；hud="
                              + (_retainedHud != null ? _retainedHud.Describe() : "<none>"));
            }
            finally
            {
                _stopping = false;
            }
        }

        /// <summary>
        /// 只读 HUD 每帧推值。**只走 setter**：HUD 自己不读旧链、不写资源、不做判定。
        /// </summary>
        /// <summary>把本局复制快照转成纯 UI 数据；UI 不接触任何 PMR3Player 引用。</summary>
        private static bool QueryBattleControlsStatus(out PMUnityBattleUiStatus status)
        {
            PMUiCombatSnapshot snapshot;
            if (!TryGetUiCombatSnapshot(out snapshot))
            {
                status = PMUnityBattleUiStatus.Offline();
                return false;
            }

            status = new PMUnityBattleUiStatus();
            status.Hp = snapshot.Hp;
            status.MaxHp = snapshot.MaxHp;
            status.Mana = snapshot.Mana;
            status.SuperEnergy = snapshot.SuperEnergy;
            status.Dead = snapshot.Dead;
            status.MatchEnded = snapshot.MatchEnded;
            status.NormalReady = snapshot.NormalReady;
            status.SuperReady = snapshot.SuperReady;
            status.LastReject = snapshot.LastReject;
            return true;
        }

        private static void UpdateBattleControls(Session session)
        {
            if (session == null) { return; }

            if (session.Controls != null && !session.Controls.IsDisposed)
            {
                session.Controls.UpdateStatus();
            }

            // T-LIVE3：瞄准线在 UI 状态刷新**之后**更新 —— UI 可能刚在本帧（死亡/终局）Cancel 掉摇杆，
            // 那时 setAim(false) 已经隐藏了线；这里再做一次幂等的状态对齐（只画本端 Owner）。
            UpdateAimIndicator(session);
        }

        /// <summary>
        /// T-LIVE3：按本端 Owner 的预测位姿与当前 UI 瞄准向量更新世界瞄准指示器（纯表现）。
        ///
        /// 四条纪律：
        ///   · **只为本端 AP 画**（<c>session.OwnerPlayer</c> + 它自己的 MovementRig）：远端 SP 与 DS 零绘制；
        ///   · 几何来自**同一份** <c>PMCombatWeaponPlanner.TryBuild</c> 计划（长度 = Spec.SpeedMps ×
        ///     Spec.LifetimeMs / 1000，扇形 = Plan.Directions），并按方向缓存（不每帧重建、不每帧分配）；
        ///   · 任何“本局不再接受输入”的状态（未按住/冻结/终局/死亡/已收结果/fault/身份未核对）一律隐藏；
        ///   · 预测位姿或几何非法时不写线（指示器自身会拒绝并计数），绝不用 NaN 污染表现。
        /// </summary>
        private static void UpdateAimIndicator(Session session)
        {
            PMUnityBattleAimIndicator indicator = session.AimIndicator;
            if (indicator == null || indicator.IsDisposed) { return; }

            if (!session.AimPreviewActive
                || session.Faulted
                || session.MovementFrozen
                || session.MatchEndedFrozen
                || session.TerminalResultObserved
                || IsOwnerDeadOrEnded(session))
            {
                indicator.Hide();
                return;
            }

            PMR3Player owner = session.OwnerPlayer;
            if (owner == null || !session.OwnerIdentityVerified)
            {
                indicator.Hide();
                return;
            }

            MovementRig rig = FindMovementRig(session, owner);
            if (rig == null || rig.Driver == null || rig.Driver.IsDisposed)
            {
                indicator.Hide();
                return;
            }

            PMMoverSyncState predicted;
            try
            {
                predicted = rig.Driver.GetPredictedSync();
            }
            catch (Exception)
            {
                indicator.Hide();
                return;
            }

            if (!predicted.Position.IsFinite)
            {
                // 坏位姿不得进表现（且不能拿它当“画在原点”的借口）。
                indicator.Hide();
                return;
            }

            PMCombatAttackPlan plan = GetOrBuildAimPlan(session, owner);
            if (plan == null || plan.Spec == null || plan.Directions == null || plan.Directions.Length == 0)
            {
                // UnsupportedAttack（抛物线 / 无子弹型大招）等：**不画**（也不 fault）——
                // 这种形态本来就不会有本次攻击，画一根线反而是伪造。
                indicator.Hide();
                return;
            }

            float distanceMeters;
            if (!PMUnityBattleAimMath.TryDistanceMeters(plan.Spec.SpeedMps, plan.Spec.LifetimeMs, out distanceMeters))
            {
                indicator.Hide();
                return;
            }

            // T-AIM：覆盖带只按 DS 玩家几何验算的同一加法口径估计：
            // 权威弹半径 + 标准玩家胶囊半径 + DS 验证容差。旧 ShootWidth 是美术尺寸，
            // 不能拿它冒充命中范围。目标缩放/历史/墙遮挡等仍会改变实际结论。
            float previewHitRadius;
            if (!PMUnityBattleAimMath.TryPreviewHitRadius(
                    plan.Spec.RadiusM, PMMoverDefaults.CapsuleRadiusMeters,
                    PMProjectileValidator.HitToleranceM, out previewHitRadius))
            {
                indicator.Hide();
                return;
            }

            int fanCount = plan.Directions.Length;
            if (session.AimFanDirX == null || session.AimFanDirX.Length < fanCount)
            {
                session.AimFanDirX = new float[fanCount];
                session.AimFanDirZ = new float[fanCount];
            }

            for (int i = 0; i < fanCount; i++)
            {
                session.AimFanDirX[i] = plan.Directions[i].X;
                session.AimFanDirZ[i] = plan.Directions[i].Z;
            }

            if (indicator.Update(predicted.Position.X, predicted.Position.Y, predicted.Position.Z,
                                 session.AimFanDirX, session.AimFanDirZ, fanCount,
                                 distanceMeters, previewHitRadius))
            {
                session.AimIndicatorUpdates++;
            }
            else
            {
                session.AimIndicatorRejected++;
            }
        }

        /// <summary>
        /// T-LIVE3：取（或按当前瞄准向量/形态重建）本轮的**预览计划**。
        ///
        /// 缓存键 = 英雄 + 是否大招 + 世界方向（量化到 <see cref="AimPlanDirectionEpsilon"/>）。理由：
        ///   · 拖动过程中方向连续变化，但同一方向的连续帧不该重复建计划（TryBuild 会分配 Directions 数组）；
        ///   · 与 <see cref="TryCombatAttack"/> 用的是**同一个** planner 入口 ⇒ 预览与上行必然同源。
        /// </summary>
        private static PMCombatAttackPlan GetOrBuildAimPlan(Session session, PMR3Player owner)
        {
            if (session.AimPlan != null
                && session.AimPlanHeroId == owner.CombatHeroId
                && session.AimPlanIsSuper == session.AimPreviewIsSuper
                && NearlySameDirection(session.AimPlanDirX, session.AimPlanDirZ,
                                       session.AimPreviewWorldX, session.AimPreviewWorldZ))
            {
                return session.AimPlan;
            }

            PMCombatAttackPlan plan;
            PMCombatRejectReason reason;
            bool built = PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId, session.AimPreviewIsSuper,
                                                       session.AimPreviewWorldX, session.AimPreviewWorldZ,
                                                       out plan, out reason);

            // 失败也记缓存键：同一个不可打的方向不必每帧重试 planner（只会在方向/形态变化时再试）。
            session.AimPlan = built ? plan : null;
            session.AimPlanHeroId = owner.CombatHeroId;
            session.AimPlanIsSuper = session.AimPreviewIsSuper;
            session.AimPlanDirX = session.AimPreviewWorldX;
            session.AimPlanDirZ = session.AimPreviewWorldZ;

            if (built) { session.AimPlanBuilds++; }
            return session.AimPlan;
        }

        /// <summary>T-LIVE3：两个世界方向是否“同向到不必重建计划”（缓存键的量化口径）。</summary>
        private static bool NearlySameDirection(float ax, float az, float bx, float bz)
        {
            return Math.Abs(ax - bx) <= AimPlanDirectionEpsilon
                   && Math.Abs(az - bz) <= AimPlanDirectionEpsilon;
        }

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
            // T-LOOP3：居中结算弹窗的**唯一**驱动点（每帧一次）。
            //
            // 可见性要求「可信终局已存」**且**「本帧终局 ACK 已发」（后者由 FlushCombatState 给出）：
            //   · 仅复制到 `CombatMatchEnded`（没有可信结果）不会为真；
            //   · 不可信断线/失败不会为真（Fail 不改这两个位）。
            // HUD 自身还会再 fail closed 一次（无结论或缺 ACK 证据时拒绘并计数）。
            hud.SetResultPanel(session.TerminalResultObserved, session.TerminalAckSent);

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
                   + " terminalAck=" + (session.TerminalAckSent ? 1 : 0).ToString(CultureInfo.InvariantCulture)
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
                   + " ui{move=" + session.UiMoveAccepted.ToString(CultureInfo.InvariantCulture)
                   + "/" + session.UiMoveRejected.ToString(CultureInfo.InvariantCulture)
                   + " attack=" + session.UiAttackQueued.ToString(CultureInfo.InvariantCulture)
                   + "/" + session.UiAttackRejected.ToString(CultureInfo.InvariantCulture)
                   + " dup=" + session.UiAttackDuplicate.ToString(CultureInfo.InvariantCulture) + "}"
                   + " aim{push=" + session.AimPreviewPushes.ToString(CultureInfo.InvariantCulture)
                   + "/" + session.AimPreviewRejected.ToString(CultureInfo.InvariantCulture)
                   + " clearr=" + session.AimPreviewClears.ToString(CultureInfo.InvariantCulture)
                   + " plans=" + session.AimPlanBuilds.ToString(CultureInfo.InvariantCulture)
                   + " updates=" + session.AimIndicatorUpdates.ToString(CultureInfo.InvariantCulture)
                   + " rejected=" + session.AimIndicatorRejected.ToString(CultureInfo.InvariantCulture)
                   + " indicator=" + (session.AimIndicator != null ? session.AimIndicator.Describe()
                                                                  : (session.AimIndicatorError ?? "<none>"))
                   + "}"
                   + " driver{" + driverText + "}"
                   + " hud{" + (session.Hud != null ? session.Hud.Describe() : "<none>") + "}";
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

            // T-PLAY4：冻结即“本局不再接受输入”——UI 推杆与已排队的攻击边沿一并清掉，
            // 否则解冻/退场时会把冻结期间的手势补发出去（契约：结束/换局不带出 UI 输入）。
            ClearUiInput(session);

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

            // 先摘除本局 UI 的委托和指针监听，再拆端点/副本；新局与正常终局都不留旧控件。
            PMUnityBattleControls controls = session.Controls;
            session.Controls = null;
            if (controls != null)
            {
                try { controls.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放局内操控 UI 异常：" + ex.GetType().Name);
                }
            }

            // T-LIVE3：世界瞄准指示器（纯表现）随会话释放（幂等）。
            // 它自己的几何缓冲也挂在会话对象上，随引用一起消失，不跨局。
            if (session.AimIndicator != null)
            {
                try { session.AimIndicator.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 释放世界瞄准指示器异常：" + ex.GetType().Name);
                }
            }

            session.AimIndicator = null;
            session.AimIndicatorError = null;

            // T-PLAY4：会话级清理里也清一次 UI 输入（显式 Stop / 换局 / 构造失败路径都要干净）。
            ClearUiInput(session);

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
            session.TerminalAckSent = false;
            session.IdentityToken = 0L;
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

        /// <summary>
        /// T-LOOP6：本机是否已为该对局落定**可信终局**（“已结束”的唯一本地判据）。
        ///
        /// 为什么不用“收到过结果 RPC”或“看起来像结束”当判据：那些都会把未校验/冲突的包
        /// 当成结论，从而把一个**还在打的对局**误判成已结束（或相反，把已结束的对局恢复）。
        /// 这里只认被幂等保存下来的那份结果快照（见 <see cref="PublishLastCombatResult"/>）。
        /// </summary>
        private static bool IsMatchAlreadyEnded(string matchId)
        {
            return _lastCombatResultValid
                && !string.IsNullOrEmpty(matchId)
                && string.Equals(_lastCombatMatchId, matchId, StringComparison.Ordinal);
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
                          + " | " + DescribeRigChain(session)
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

        /// <summary>
        /// T-MOVE2：仅在既有低频心跳打印逐副本位置链（不改仿真、不每帧读额外快照）。
        /// raw=本副本最后收到的复制字节，accepted=Driver 确实采纳数，shown=最近一次
        /// ApplyInterpolated/Predicted 的输入，root=Unity 最终可见根位置。四个值分开，
        /// 才能辨别“包里本地预测在动，但 DS 根本没前进”和“DS 在动但 SP/表现卡住”。
        /// </summary>
        private static string DescribeRigChain(Session session)
        {
            if (session == null) { return "rigChain=<none>"; }

            string summary = "rigChain=";
            for (int i = 0; i < session.Movements.Count && i < 8; i++)
            {
                MovementRig rig = session.Movements[i];
                if (i > 0) { summary += ";"; }
                if (rig == null || rig.Player == null || rig.Driver == null)
                {
                    summary += "[missing]";
                    continue;
                }

                PMR4MovementDriver driver = rig.Driver;
                summary += "[net" + rig.Player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                    + (rig.IsOwner ? "/AP" : "/SP")
                    + " stream=" + driver.StreamVersion.ToString(CultureInfo.InvariantCulture)
                    + " queued=" + driver.SnapshotPayloadsQueued.ToString(CultureInfo.InvariantCulture)
                    + " accepted=" + driver.SnapshotPayloadsApplied.ToString(CultureInfo.InvariantCulture)
                    + " decodeBad=" + driver.SnapshotDecodeFailed.ToString(CultureInfo.InvariantCulture)
                    + " idBad=" + driver.SnapshotRejectedIdentity.ToString(CultureInfo.InvariantCulture)
                    + " worldBad=" + driver.SnapshotRejectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                    + " drops=" + driver.SnapshotQueueDrops.ToString(CultureInfo.InvariantCulture);

                byte[] payload = rig.Player.MovementSnapshotPayload;
                PMR4MovementSnapshotBlob raw;
                string error;
                if (payload != null && PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                    PMR4PayloadKind.Snapshot, out raw, out error))
                {
                    summary += " rawFrame=" + raw.OutputFrame.ToString(CultureInfo.InvariantCulture)
                        + " raw=" + FormatRigPosition(raw.Sync.Position);
                }
                else
                {
                    summary += " raw=<invalid>";
                }

                PMUnityBattlePresentation battle = rig.BattlePresentation;
                if (battle != null && !battle.Disposed && battle.Root != null)
                {
                    Vector3 root = battle.Root.position;
                    summary += " apply=" + battle.ApplyCount.ToString(CultureInfo.InvariantCulture)
                        + " shown=" + FormatRigPosition(battle.LastApplied.Position)
                        + " root=" + FormatRigPosition(new PMVector3(root.x, root.y, root.z));
                }
                else
                {
                    summary += " visual=<none>";
                }

                summary += " frozen=" + (rig.DeathFrozen || driver.IsFrozen ? 1 : 0) + "]";
            }

            if (session.Movements.Count > 8) { summary += ";more=" + (session.Movements.Count - 8); }
            return summary;
        }

        private static string FormatRigPosition(PMVector3 pos)
        {
            return "(" + pos.X.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + pos.Y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + pos.Z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
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
