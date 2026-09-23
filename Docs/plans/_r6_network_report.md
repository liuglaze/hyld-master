# R6-B 纯会话网络驱动：PMR6CombatDriver 真实接 PMCombat 与 R5

> 范围：`Docs/plans/net-r6-combat-contract.md`「B 整合与宿主」+「B 驱动冻结补充」+ 验收 T6B。
> 依赖（只读消费）：`_r6_core_report.md`（PMCombatWeaponPlanner / PMCombatSession 精确 API）、
> `_r6_declaration_report.md`（A2 声明：9 条复制属性 / 4 条 RPC / `IPMCombatNetworkDriver`）、
> `_r5_network_review.md`（R5 公开 host API：TryFire / ResolveActivation / DrainSettlements / 会话 fault）。
> 本文只记录**本批实际产物与实测证据**；状态唯一源仍是 `net-architecture-migration.md`。
> **未启动 Unity、未提交、未做任何 SVN/git 写操作、未改主计划与 Shared 契约、未递归委派。**

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 语言面门禁 `Tools/PMR6NetworkCheck`（netstandard2.0 + C# 7.3，零 Unity，冻结 Include 面） | **0 警告 / 0 错误** |
| 行为门禁 `Tools/PMR6NetworkTest`（真实 PMTransport 字节链 + 真实 World/Bridge/Generated + 真实 PMCombatSession/R5driver/history） | **通过 220 / 失败 0**，exit 0 |
| 变异测试（证明门禁有牙齿） | **4/4 全部被捕获**，随后逐字还原（MD5 一致） |
| 回归 | 见 §8；唯一失败是 `PMR4NetworkTest` 的 **2 项 R6-A2 遗留过时计数断言**（写边界之外，主侧整合时改 3 → 12） |
| 诚实边界 | **host 尚未接线**（`PMClientSessionHost` / `PMDsSessionHost` 未创建本驱动）；**不能声称完整玩法完成**（T46/T47 继续 PENDING_USER） |

---

## 1. 交付文件与编码

| 文件 | 变更 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/PMR3/PMR6CombatDriver.cs` | **新增**（会话级战斗网络驱动，1854 行） | UTF-8 **BOM** + CRLF |
| `Client/Assets/Scripts/PMR3/PMR6CombatDriver.cs.meta` | **新增**，唯一 GUID `e2ea8c0c81fb4e23b044d2a4c9c3655f`（全仓库 `.meta` 中出现 1 次，已复核） | ASCII + LF（无 BOM） |
| `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs` | 修改：新增 `CancelPredictedActivation`（owner 受限 API）、`FakeEntry.ActivationId`、`CancelledPredictions`；`RevokeFake` 退休上行 Verify 账 | UTF-8 BOM + CRLF |
| `Client/Assets/Scripts/PMR3/PMR3Runtime.cs` | 修改：`SendRemoteRpc` 的「失败即抛」集合扩到**四条战斗 RPC**，判定仍按 **(ClassId, RpcId)**；probe/movement 行为逐字不变 | UTF-8 BOM + CRLF |
| `Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj` | **新增**（语言面门禁，库） | UTF-8 无 BOM + LF（Tools 既有惯例） |
| `Tools/PMR6NetworkTest/PMR6NetworkTest.csproj` | **新增**（行为门禁，net8.0 Exe） | 同上 |
| `Tools/PMR6NetworkTest/Program.cs` | **新增**（220 项断言） | UTF-8 BOM + CRLF |
| `Docs/plans/_r6_network_report.md` | 本报告 | UTF-8 BOM + CRLF |

**依赖面（`<Compile Include>` 逐条给定，无通配符以外的例外）**：`PMNet/**`（核心 + 生成产物）、
`PMPrediction/**`、`PMMover/**`、`PMProjectile/**`（纯核心）、`PMCombat/**`（纯核心）、
`Shared/**`（数值源）、`PMR3/**`（声明层 + 生成产物 + R4/R5/R6 driver）。
**零 `UnityEngine`、零 R3、零 `Google.Protobuf`**。

> `Server/Server.csproj` 的 `PMR3/**` 通配符**已经预置** `Exclude=...PMR6CombatDriver.cs`（Lobby 只消费声明层），
> 因此本批新增文件不会把 R6 driver 拖进 Lobby；Server 构建实测 0 错误。

---

## 2. 公开 host API

### 2.1 `PMNet.R3.PMR6CombatDriver`（新增）

```csharp
namespace PMNet.R3
{
    /// <summary>DS 侧「owner 真实位置」的有效性查询（返回 false ⇒ 策略拒绝授权，fail closed）。</summary>
    public delegate bool PMCombatTryGetOwnerPosition(PMR3Player player, out PMVector3 position);

    public enum PMR6CombatFaultReason : byte
    {
        None = 0, SendFailed = 1, QueueOverflow = 2, InvalidField = 3, ModelMissing = 4,
        R5Faulted = 5, Disposed = 6, ThreadViolation = 7, NonMonotonicWallClock = 8,
        ConflictingOutcome = 9, ResultEncodeFailed = 10, NotLocalOwner = 11, InvalidActivation = 12,
    }

    public sealed class PMR6CombatDriver : IDisposable
    {
        // 冻结上限
        public const int MaxInboundAttacks = 256;
        public const int MaxInboundAttackResults = 256;
        public const int MaxInboundMatchResults = 64;
        public const int MaxInboundResultAcks = 256;
        public const int MaxPendingAttacks = 512;
        public const int MaxBufferedSettlements = 256;
        public const int MaxApplyPerPump = 64;
        public const int ResultResendMs = 1000;   // == PMCombatLimits.ResultResendMs
        public const int ResultGraceMs  = 5000;   // == PMCombatLimits.ResultGraceMs
        public const int PendingAttackTtlMs = 5000;

        // 构造：DS 必须给 model；AP 传 null；epoch 非 0 且必须与 R5 driver 一致；bridge.World == world
        public PMR6CombatDriver(PMNetWorld world, PMNetSessionBridge bridge, uint epoch,
                                PMR5ProjectileDriver projectiles, PMCombatSession model = null);

        // 接缝（与 R5 同套路：一个 player 的两条接缝一次性接好）
        public bool BindPlayer(PMR3Player player);       // 同时把 player 接到 R5 driver（幂等）
        public bool UnbindPlayer(PMR3Player player);     // DS 额外 core.Disconnect + 去掉一个等待 Ack
        public bool IsPlayerBound(PMR3Player player);

        // DS 可信名册（uid/team/hero 只能来自 DS 票据验签结果）
        public bool AddPlayer(PMR3Player player, int uid, int teamId, int heroId);
        public bool StartMatch(int expectedPlayerCount);

        // AP 攻击（先发包、再逐颗预测）
        public bool TryAttack(PMR3Player player, bool isSuper, float aimX, float aimZ,
                              PMVector3 position, int predictionMs, double wallNow,
                              out uint activationId, out string error);

        // 宿主每帧固定次序：R6.Pump(now) → R5.Pump(now, step) → R6.FlushState(now)
        public void Pump(double wallNowMs);          // 命令队列（攻击/裁决/结果/ACK）+ core.Tick + Drain 兜底
        public void FlushState(double wallNowMs);    // 9 字段复制 + 终局结果冻结/下发/就绪判定（AP：回 ACK）

        // 时钟（宿主可显式推高水位；单调，倒退不改状态）
        public bool UpdateClock(double wallNowMs);
        public double WallTimeMs { get; }

        // 只读视图
        public PMCombatSession Model { get; }
        public PMR5ProjectileDriver Projectiles { get; }
        public PMR3Player LocalOwner { get; }
        public bool IsServer { get; }
        public bool IsFaulted { get; }  public PMR6CombatFaultReason FaultReason { get; }  public string FaultError { get; }
        public int BoundPlayerCount { get; }  public int PendingAttackCount { get; }  public int BufferedSettlementCount { get; }

        // 终局结果（仅 DS 有意义）
        public bool OutcomeIsFrozen { get; }
        public uint FrozenOutcomeId { get; }     public int WinnerTeamId { get; }     public PMCombatEndReason FrozenEndReason { get; }
        public bool ResultReadyForLobby { get; } // 全 ACK 或 5000ms 宽限；**不**宣称客户端都看到了
        public int ResultAckCount { get; }
        public byte[] ResultSummary { get; }     // 深拷贝（空数组 = 未冻结）

        // AP 结果幂等保存 + 观测
        public bool TryGetStoredMatchResult(out uint outcomeId, out int winnerTeamId);
        public bool HasPendingAttack(uint ownerNetId, uint activationId);
        public bool WasActivationRevoked(uint ownerNetId, uint activationId);
        public bool WasActivationConfirmed(uint ownerNetId, uint activationId);
        public bool TryGetPlayerSnapshot(uint netId, out PMCombatPlayerSnapshot snapshot);

        // 宿主注入面
        public event Action<PMR3Player> PlayerDied;   // 首次死：宿主据此 freeze Mover / 对齐 history.Alive
        public event Action<uint, int> OutcomeFrozen; // 终局冻结一次

        // DS 授权策略工厂（**独立 public**：宿主在创建 R5 driver 之前就能拿到）
        public static IPMR5ProjectileAuthorityPolicy CreateAuthorityPolicy(
            PMCombatSession model,
            PMCombatTryGetOwnerPosition tryGetOwnerPosition,
            Func<double> wallClockNow);

        // 观测计数（本地记账，非复制）
        public long Pumps, Flushes, StatePublishCount, StatePublishRejected, StateReplicatedNotices;
        public long AttacksReceived, AttacksProcessed, AttackResultsApplied, AttackResultsUnknown;
        public long MatchResultsReceived, MatchResultDuplicates, ResultAcksReceived, ResultAckMismatches, ResultAckDuplicates;
        public long ResultSends, ResultResends, ResultReadyByAckCount, ResultReadyByGraceCount;
        public long OutcomeFrozenCount, OutcomeConflictCount;
        public long SettlementsApplied, SettlementsRejected, SettlementsBuffered, SettlementsDrainedFromR5;
        public long PendingAttackTimeouts, PendingAttacksPruned, ActivationWraps, PredictionsCancelled;
        public long ThreadViolations, PlayerDeathsReported;

        public void Dispose();   // 只撤自己那一份：结算订阅 + 本地账 + 接缝引用；不 Dispose R5、不碰别的会话
        public string Describe();
    }
}
```

### 2.2 `PMNet.R3.PMR5ProjectileDriver`（本批增量）

```csharp
/// <summary>客户端（AP）：真实撤销某个 activation 的全部本地假弹（owner 受限、幂等、跳过已接管）。</summary>
public int CancelPredictedActivation(PMR3Player owner, uint activationId, double wallNowMs);

public long CancelledPredictions;   // 观测：被上述 API 真实撤销的假弹颗数
// 语义补充：RevokeFake 现在同时退休该 key 的上行 Verify 账（有界回落）
```

### 2.3 `PMNet.R3.PMR3Runtime`（本批增量，签名不变）

`SendRemoteRpc` 的临界集合从「3 条投射物 RPC」扩为 **3 条投射物 + 4 条战斗 RPC**，
判定一律 **(ClassId, RpcId)**：`ServerCombatAttackV1` / `ServerCombatResultAckV1` /
`ClientCombatAttackResultV1` / `ClientCombatMatchResultV1` 发送失败 ⇒ **抛异常**（驱动转会话 fault）。
`ServerProbe` / movement 四条 / 其它类的同名 `RpcId` **行为逐字不变**（仍然只告警）。

---

## 3. 契约条款 → 实现落点对照

| 契约条款 | 落点 |
|---|---|
| session 级 `IDisposable`，ctor World/Bridge/epoch/R5driver/可空 PMCombatSession（DS 必有） | ctor 校验：epoch≠0、bridge.World==world、`projectiles.Epoch==epoch`、DS 缺 model ⇒ **抛** |
| `BindPlayer/UnbindPlayer` adapter 同 R5 套路 | 私有 `PlayerAdapter : IPMCombatNetworkDriver`；Bind 同时接 R5；Unbind 走 R5 owner 收拾 + `core.Disconnect` |
| DS core AddPlayer 由 host 可信 roster 后绑定 | `AddPlayer(player, uid, teamId, heroId)` → `core.AddPlayer(netId, ...)` + `BindPlayer`；`StartMatch(count)` |
| Client 用 CombatState 复制 getter，不取旧 UI | `TryAttack` 只读 `CombatHeroId/CombatTeamId/CombatMaxHp/CombatDead/CombatMatchEnded` |
| IPMCombat callbacks **仅有界队列** | 4 条有界入站队列 + 溢出即 `QueueOverflow` 会话 fault；`OnCombatStateReplicated` 只计数 |
| Pump(now) 先处理 attack/ACK 与 core.Tick | `Pump`：ApplyInboundAttacks → AttackResults → MatchResults → ResultAcks → `core.Tick(now)` |
| FlushState(now) 经 `PublishCombatState` 9 字段提交 | `PublishStateIfDirty()`（脏才写；名册成员未绑定则保持脏、下帧重试） |
| 资源 OwnerOnly 声明已兑现 | 只写复制字段，权限由 A2 的 `PMCond.OwnerOnly` + `PublishCombatState` 的 World role 把关 |
| DS 请求调用 `core.RequestAttack` **单次** | `HandleServerAttack` 一次调用，不重试、不双扣 |
| 生成 `ClientCombatAttackResult` | `player.PMNet_ClientCombatAttackResultV1(activationId, accepted, reason)`（reason = core 拒绝码） |
| `R5.ResolveActivation` 用权威 decision（duplicate 不能反向拒） | Accepted → `Confirmed`；否则 `Rejected`；duplicate 返回**原始** decision ⇒ 绝不把已接受翻成拒绝 |
| R5 Spawn policy 只认 core 已批准 slot，取 trustedSpec/hostPosition | `AuthorityPolicy`：`core.TryAuthorizeProjectile`（slot/epoch/owner/origin/方向容差/TTL）→ spec 深拷贝；位置只来自 `PMCombatTryGetOwnerPosition` |
| 独立 public `CreateAuthorityPolicy(model, 真实 position provider, 显式 clock)`，不引 Unity；position 有效性 delegate | `PMR6CombatDriver.CreateAuthorityPolicy` + `PMCombatTryGetOwnerPosition`（delegate 带 `out`，不接受 lambda 伪造位置） |
| policy 所有源信息来自 player 身份 + core，不能客户端自报 | 只读 `player.NetId`；`intent` 仅用于 core 校验（方向与已批准槽比对）；spec/damage/枪口**不采信** |
| Client `TryAttack(...)`：合法 AP owner + MaxHp>0、alive、已知 hero/team | `IsAliveForAttack`：hero∈[0,20)、team>0、MaxHp>0、!Dead、!MatchEnded；且必须是 `R5.LocalOwner` |
| planner 双方同源 | 客户端用 **同一份** `PMCombatWeaponPlanner.TryBuild`；DS core 内部再复核 |
| activation 单调非 0 不回绕 | per-owner `_nextActivationByOwner`；`uint.MaxValue`/0 ⇒ `InvalidActivation` fault |
| `ServerCombatAttackV1` 在**所有** R5.TryFire 前 | 先发包，成功后进入 N 颗预测循环 |
| N 弹同 activation 同 muzzle，按 planDirections/spec 逐颗预测，无等待批准 | 同一 `position`、同一 `activationId`、`plan.Spec.Clone()` 逐颗 `R5.TryFire`；不阻塞等结果 |
| 不本地扣复制字段 | `TryAttack` 只调 `PublishCombatState` 之外的只读 getter；不写任何 `_combat*` |
| 任一发包/部分 TryFire 失败 ⇒ session fault 并撤该 activation | 发送抛异常 ⇒ `SendFailed` fault；TryFire 失败 ⇒ `CancelPredictedActivation` + `SendFailed` fault |
| Rejected 实际取消所有对应假弹（允许加 `CancelPredictedActivation`） | `HandleAttackResult(false)` → `_projectiles.CancelPredictedActivation(player, activationId, wall)` |
| Confirmed 仅记录，不抢 R5 镜像接管 | 只置 `pending.Confirmed = true`，不动 R5 的接管/高水位 |
| 未知 outcome/字段 fail closed | 拒绝码越界 ⇒ `InvalidField` fault；outcomeId 0 / 未知胜方 ⇒ `InvalidField` fault；结果冲突 ⇒ `ConflictingOutcome` fault |
| 有界 pendingAttack 跟踪按终态/TTL 退 | `_pendingAttacks` ≤512，`PendingAttackTtlMs=5000` 到期退休（未终态计 `PendingAttackTimeouts`） |
| dead/end 禁止新攻击 | `TryAttack` 拒绝（error 明确）；DS core 侧 `Dead`/`MatchEnded` 亦拒绝且**无写** |
| R5 唯一 settlement 消费由 driver 订阅 + Drain 兜底 | ctor（DS）`_projectiles.Settlement += OnProjectileSettlement`；`Pump` 里 `DrainBufferedSettlements()`（订阅者异常落缓冲 + `R5.DrainSettlements`） |
| `core.ApplySettlement` 后 state 脏 | `SettlementsApplied++` + `_stateDirty = true` |
| Dispose 取消自己的订阅、不清别会话 | `_projectiles.Settlement -= ...`；**不** 调 `ClearAll`、**不** Dispose R5 |
| 不与 diagnostic host 订阅并存（宿主须换掉） | 见 §7 的 U1：`PMDsSessionHost` 目前仍 `_projectileDriver.Settlement += OnProjectileSettlement`（诊断），宿主接线时必须移除 |
| Outcome 结束冻结摘要（PMNetWriter 原语 / 有限数值、不必新 protobuf DTO） | `TryBuildResultSummary`：`WriteTag/WriteUInt32/WriteInt32/WriteEnum/WriteBool`，无字符串 |
| 可靠 `ClientCombatMatchResult` 逐在线 owner | `SendMatchResult` 遍历 DS 已绑定玩家，跳过 `!snapshot.Connected` |
| ClientPump 只幂等保存结果 + ack（**不在回调内发送**） | `OnClientMatchResult` 只入队；`Pump` 处理并置 `_clientResultAckPending`；ACK 在 `FlushState` 发 |
| DS 收到认证 Ack 标记 | `HandleResultAck`（归属由 `PMNetRpcReceive` 校验）；幂等 `_resultAcked.Add` |
| 全 ACK 或 5000ms grace ⇒ `ResultReadyForLobby=true`、WinnerTeamId、ResultSummary 深 clone | `AllConnectedOwnersAcked()` / `wallNow - _frozenWallMs >= ResultGraceMs`；`ResultSummary` 返回 `Clone()` |
| 1000ms 重发相同 outcome | `_lastResultSendMs` + `ResultResendMs`；内容永不改变 |
| duplicate/conflicting winner/outcome 测试 | §6 的 G（duplicate 幂等 + 深拷贝 + 冻结不变）；`ConflictingOutcome` 分支为 fail closed 防御 |
| 一旦 Frozen outcome 不因断线/重复结算改变 | `_outcomeFrozen` 只写一次；后续只做 `OutcomeConflictCount++` 观测 |
| Unbind 允许少一个等待 Ack | `UnbindPlayer` 里 `_resultAcked.Remove(netId)`；`AllConnectedOwnersAcked` 只看**在线** owner |
| epoch 不复用 | ctor 拒绝 epoch 0 与「与 R5 不一致」；本批不做任何 epoch 复用式重开 |
| Faulted/queueoverflow/sendfailure 显式 Session fault，不吞旧已接受结果 | 4 类队列溢出、4 条 RPC 发送失败、R5 fault、字段越界、时钟倒退、冲突结果全部 `Fault(...)`；已接受结论**只观测不改写** |

---

## 4. 宿主固定次序与接线步骤

```
每帧（同一根单调墙钟 now）：
  1) r6.Pump(now)         // 上行攻击 → core.RequestAttack（单次）→ R5.ResolveActivation(权威裁决)
                          // + core.Tick(now)（回蓝）+ 结算 Drain 兜底
  2) r5.Pump(now, step)   // 真实投射物：入站 spawn/hit/decision → 授权 → SpawnReserved → 运动 → settlement
                          //（settlement 在 R5.Pump 内回调 → r6 立即 core.ApplySettlement → state 脏）
  3) r6.FlushState(now)   // 脏则 PublishCombatState(9 字段)；终局则冻结结果 + 逐 owner 下发 + 就绪判定
                          //（AP：此处回 ServerCombatResultAckV1）
```

DS 启动顺序（契约「创建先后」）：

1. `PMR3Runtime.Register()` / `Attach(serverWorld, serverBridge)`；
2. `new PMCombatSession(epoch)`；
3. `PMR6CombatDriver.CreateAuthorityPolicy(model, TryGetOwnerPosition, clock)` ← **R5 之前**；
4. `new PMR5ProjectileDriver(world, bridge, epoch, history, policy, hostMotion?, hitFilter?)`；
5. `new PMR6CombatDriver(world, bridge, epoch, r5, model)`；
6. 逐人 `PMR3Runtime.SpawnPlayer(...)` → `r6.AddPlayer(player, uid, teamId, heroId)`（同时绑 R5）；
7. `r6.StartMatch(rosterCount)`；**在首次生命周期 Flush 之前** `r6.FlushState(now)`（写初值）；
8. 之后每帧按上面的 1→2→3。

AP：`Attach(clientWorld, clientBridge)` → `new PMR5ProjectileDriver(...)`（无 policy）→
`new PMR6CombatDriver(...)`（model=null）→ 发现副本后 `r5.BindPlayer(replica)` + `r6.BindPlayer(replica)`
（`r6.BindPlayer` 已内含 R5 绑定，重复调用幂等）。

**宿主必须补齐的三件事**（本批在本文件外，见 §7）：

- `PMDsSessionHost`：**移除** `_projectileDriver.Settlement += OnProjectileSettlement` 诊断订阅（否则双消费者）；
  改为 `r6.PlayerDied` → freeze 对应 Mover + 写 `history` 的 `Alive=false`；`r6.ResultReadyForLobby` → `Lobby.SubmitResult`。
- `PMClientSessionHost`：创建 R6 driver、按上表接线、`ResultReadyForLobby` 不适用（客户端恒 false）。
- 两端：位置/Freeze 有效性由 `PMCombatTryGetOwnerPosition` 注入（引擎无关）；墙钟只从一处喂入。

---

## 5. 测试与运行结果

```
dotnet build Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj -c Release   → 0 警告 0 错误
dotnet build Tools/PMR6NetworkTest/PMR6NetworkTest.csproj  -c Release   → 0 警告 0 错误
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll     → exit 0，通过 220 / 失败 0
python Tools/check_cs_braces.py <4 个文件>                              → PASS（深度全程非负、末尾归零）
```

### 5.1 覆盖矩阵（对应 T6B）

| 段 | 覆盖与关键断言 |
|---|---|
| A 契约常量与故障口径 | 重发 1000ms / 宽限 5000ms / 入站与 pending 上限 / R5 结算缓冲 512；`CreateAuthorityPolicy` 三个 null 参数都抛 |
| B **真实链路** | 柯尔特(2 发×340) 打 雪莉(960HP)：`TryAttack` 产出 2 颗预测弹、activation=1、pending 在册；**未收到权威裁决前复制蓝量不变**（不本地扣）；DS `RequestAttack` 1 次 + `ServerSpawnsAuthorized==2`；DS 上线 2 个权威对象；客户端 `SubmitPredictedHits` 报 2 颗 → DS history 验证 → `SettlementsApplied>=2`、`SettlementsRejected==0`；权威 `HP == maxHp - N*damage`（280）、`SuperEnergy == min(N*damage/2,200)`（=200）、`Dead==false`；**回流**：AP 副本 `CombatMana==60`/`CombatSuperEnergy==200`/自己的 HP 1180；**SP 副本（observer）mana/energy 恒 0**，但公共 HP 两边都到；另一客户端上受害者自己副本蓝量未被误扣；`StatePublishRejected==0`、无 fault |
| C Mana 不足 → 拒绝 → 真实撤 fake | 1s 内连打 3 次精确耗尽 90 蓝（不命中 ⇒ 不掉血 ⇒ 不提前终局）；第 4 次客户端**仍乐观预测 2 颗假弹**；DS 拒绝 ⇒ `FakesRevoked >= +2`、**`PredictionsCancelled == 2`（证明是攻击级拒绝路径真撤销，不是等 R5 逐颗裁决）**、该 activation 终态 `Revoked`、2 个 key 的视图消失、`Mana` 仍 0、无 fault |
| D 伪造 spawn | D1 方向不在计划（+X vs 计划 +Z）被拒且不生成对象；D2 方向合法但超出 2 槽上限被拒且不生成对象；D3 未批准 activation(777) 被拒；全程无 fault |
| E 未知 hero / 抛物线 | E1 巴利（IsParabola）客户端 planner 直接拒（`UnsupportedAttack`、activationId=0、**零上行**、蓝量满）；E2 佩佩无子弹型大招：**绕过客户端**直接伪造真实上行 RPC ⇒ DS 处理但拒绝，蓝/能量**零变化**、不终局；E3 DS 侧复制出越界 heroId=99 ⇒ 客户端 `TryAttack` 拒（「未知英雄」）且零上行 |
| F 重复 attack / 重复 Spawn | F1 同 activationId 重复上行 → core 幂等：蓝/能量不二次扣、`IdConflictCount` 不变、`ServerSpawnsRejected` 不变（**不反向拒**）；F2 同一颗弹的 Spawn RPC 重复 ⇒ `ServerSpawnsDuplicateIgnored+1`、`Authorized`/`Rejected`/`DecisionsSent` 全不变；F3 新 activation 正常授权、只扣一次 |
| G FirstKill → ACK → ready | 击杀后 core `Ended`+`FirstKill`+胜方=真实 TeamId 1；DS 冻结（outcomeId 与 core 一致、摘要非空、已下发）；**两个**客户端都幂等保存且胜方=1；`ResultAckCount>=2` ⇒ `ResultReadyForLobby==true` 且 `ResultReadyByAckCount>=1`；`ResultSummary` 是深拷贝（就地改不影响内部）；冻结后重复 tick 结论不变、客户端无 fault |
| H 无 ACK → 5000ms 宽限 | 客户端**不发 ACK**（抑制其 FlushState，但正常收结果并保存）：`ResultReadyForLobby==false`、`ResultAckCount==0`、`MatchResultsReceived>=1`、已保存结果；推进 1100ms 见 `ResultResends` 增长且仍未就绪；再推进 4000ms ⇒ 就绪且 `ResultReadyByGraceCount>=1`、胜方仍是冻结值 |
| I dead/ended 后无写 | 死者副本复制到 `Dead`；双方复制到 `MatchEnded`；客户端 TryAttack 被拒（原因含死亡/结束）且**零上行**；伪造上行 ⇒ HP/蓝/能量/结算计数**全部不变**、无 fault |
| J 资源无篡改 + RPC 失败抛异常 | J1 AP 副本 `PublishCombatState` 被拒（`CombatPublishRejectedCount+1`）、mana/energy/winner 全不变、DS 权威不受影响；J2 分别拆客户端/服务端世界：四条战斗 RPC 发送失败**全部抛**（上行两条从 AP、下行两条从 DS 侧），旧 `ServerProbe` 仍只告警不抛，旧投射物 RPC 仍抛（口径未被破坏） |
| K 隔离 world / Dispose / epoch | 两个会话同时在册（订阅数 ≥6）；epoch=0 / 与 R5 不一致 / DS 缺 model 三者都抛；Dispose A 后其副本接缝被摘、`Pump`/`FlushState` 不抛、`TryAttack` 返回 false；**B 仍能完整跑通一次攻击**（投射物正常上线、无 fault）；A 的 Dispose 只摘自己的订阅 |

### 5.2 变异测试（证明门禁有牙齿，4/4 被捕获，随后逐字还原）

| 临时变异 | 被捕获的失败断言 |
|---|---|
| A：攻击级拒绝不再调 `R5.CancelPredictedActivation`（只置终态） | 1 条：`C 撤销由**攻击级拒绝**（R6）真实完成…（期望 2，实际 0）` |
| B：授权策略跳过 `core.TryAuthorizeProjectile`（永远放行 + 自带 spec） | 6 条以上：`B R6 消费了 ≥2 条权威结算（实测 0）`、`B 结算没有被拒（期望 0，实际 2）`、`B DS 权威 HP（期望 280，实际 960）`、`B DS 权威能量（期望 200，实际 0）`、`B SP 副本 HP`、`B AP 副本能量` —— 证明 **policy 是唯一批准来源**，绕过 core 后连结算都无法成立 |
| C：冻结时直接把 `ResultReadyForLobby = true` | 4 条：`G 就绪来自 ACK 路径`、`H 无 ACK 时不得提前就绪`、`H 1100ms 时仍未就绪`、`H 就绪来自宽限路径` |
| D：`ApplySettlementNow` 不调 `core.ApplySettlement` | 6 条以上：`B R6 消费了 ≥2 条权威结算（实测 0）`、`B 结算没有被拒（期望 0，实际 2）`、`B DS 权威 HP/能量`、`B SP 副本 HP`、`B AP 副本能量` |

变异后已用备份逐字还原，`PMR6CombatDriver.cs` MD5 与变异前一致（`79a09cc949c67e090096a668dbbb030c`），
并在还原后重新 build(0/0) + run(220/0)。

---

## 6. 回归门禁（本批 build + run）

| 门禁 | 结果 |
|---|---|
| `Tools/PMR6NetworkCheck`（新增，netstandard2.0 + C#7.3，零 Unity） | **0 错误** |
| `Tools/PMR6NetworkTest`（新增） | **220 / 0**，exit 0 |
| `Tools/PMR5NetworkCheck` / `Tools/PMR5NetworkTest` | 0 错误 / **417 项通过，0 失败** |
| `Tools/PMR3RuntimeTest` | **180 项，0 失败**（`PMR3Runtime` 改动回归） |
| `Tools/PMR3IntegrationTest` | **143 项，0 失败** |
| `Tools/PMR6DeclarationTest` | **235 项，0 失败**（R6-A2 未被破坏） |
| `Tools/PMR5DeclarationTest` | **139 项，0 失败** |
| `Tools/PMCombatCoreCheck` / `Tools/PMCombatCoreTest` | 0 错误 / **1500 / 0** |
| `Tools/PMProjectileIntegrationCheck` / `Test` | 0 错误 / **977 项断言**（0 失败） |
| `Tools/PMProjectileLifecycleTest` | **510 项断言**（0 失败） |
| `Tools/PMR4NetworkCheck` / `Tools/PMR4IntegrationTest` | 0 错误 / **113 项，0 失败** |
| `Tools/PMR4NetworkTest` | **通过 387 / 失败 2** —— 见下方「唯一失败」 |
| `Tools/PMDeclCheck` | **PASS 71 / FAIL 0** |
| `Tools/PMNetVerify` | **27 项，0 失败** |
| `Tools/PMClientCheck` / `Tools/PMUnityGlueCheck` / `Tools/PMR3UnitySmoke` | 0 错误（`PMR5ProjectileDriver` 改动的 C# 7.3 编译回归；顺带证明 R6 driver 在 7.3 下可编） |
| `Server/Server.csproj`（Lobby，排除 R6 driver） | **0 错误** |

**唯一失败**：`Tools/PMR4NetworkTest` 的 2 项断言 ——
`复制属性位宽 == 3（期望 3，实际 12）`、`复制描述符属性数 == 3（期望 3，实际 12）`（`Program.cs:283/289`）。
这是 **R6-A2 已记录的过时计数断言**（`_r6_declaration_report.md` §4.3/§8），**在本次写边界之外**，未修改；
其原 ID/权限/档位断言全部通过。主侧整合时把 3 改成 12 即可。

> `Tools/PMR3UnitySmoke` 的 S35 需要重建 `HyldDS.exe`（预构建二进制协议摘要过期，见 A2 报告 §4.3），
> 本任务禁止启动 Unity，故只做**编译**回归，不声称实机就绪。

---

## 7. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | **host 未接线** | `PMClientSessionHost` / `PMDsSessionHost` 尚未创建 `PMR6CombatDriver`；§4 的接线步骤是**给宿主的要求**，不是已完成事实。**且 `PMDsSessionHost` 现在仍以诊断方式 `_projectileDriver.Settlement += OnProjectileSettlement`（`PMDsSessionHost.cs:1995`）—— 与本驱动的订阅构成双消费者，宿主接线时必须换掉。** |
| U2 | 死亡时「freeze Mover + `history.Alive=false`」 | 本驱动只提供 `PlayerDied` 事件与 `PlayerDied/PlayerDiedReported` 计数；引擎侧冻结与 history 写入属宿主（契约原文：host 真实 owner 位置/当前 alive 来自 model + movement）。R5 `PMProjectileHistory` 由宿主注入，本批不伪造。 |
| U3 | 完整玩法 | 特殊技能 / 爆炸 / 弹射 / 穿透 / 位移 / 道具 / 完整 UI / 回放**均未实现**；14 个英雄无子弹型大招仍是「明确拒绝」。T46/T47 继续 PENDING_USER。 |
| U4 | ServerDirect | `core.TryAuthorizeProjectile` 与 `ApplySettlement` 只接受 `ClientPredicted`；DS 自产弹（activation 0）走 R5 `TryServerDirectSpawn` 但不进 R6 结算（沿用 A1 的边界）。 |
| U5 | 实机/真 UDP | 测试用「真实 PMTransport 字节链 + 手工 link/hub」，**不是**真实 UDP socket / 跨机 MTU / Unity PhysX。 |
| U6 | 全局 2048 authorized key / 512 攻击记录上限 | 单局实际不可达（A1 报告 §5.5），属有界防御；本批未构造溢出用例。 |
| U7 | 死亡与「首杀即终局」的关系 | 首杀即终局 ⇒ **不存在**「已死但比赛未结束」的玩家（除断线 forfeit 外）。因此「dead 后攻击无写」在本批的可达形态是「死者副本 `Dead=true` + 双方 `MatchEnded` + 伪造上行零写」（§5.1 I）。 |
| U8 | `PredictionsCancelled` 的帧序前提 | C 段的「R6 先于 R5 撤销」依赖宿主次序（R6.Pump 早于 R5.Pump）与可靠域保序；这也是契约要求的固定次序，若宿主违反次序该断言会暴露。 |
| U9 | 结果摘要格式 | 用 `PMNetWriter` 原语（无字符串、无新 protobuf DTO），字段编号 1..11 为**本批冻结口径**；Lobby 侧消费方需按同一口径解码（本批未改 Lobby）。 |
| U10 | 断线 Forfeit 的网络路径 | `UnbindPlayer` 会 `core.Disconnect`（可能触发 Forfeit/winner 0）；本批未单独构造「对局中途断线 ⇒ 结果就绪」的端到端用例（core 语义已由 A1 的 T6A3 覆盖）。 |

---

## 8. 剩余问题

1. **`Tools/PMR4NetworkTest` 两项过时计数断言**（`Program.cs:283/289`，3 → 12）：写边界之外，未改；主侧整合时更新。
2. **`PMDsSessionHost` 的双消费者**：必须移除其诊断 `Settlement` 订阅，或改为只 `DrainSettlements` 兜底（并与 R6 的分工写清）。
3. **`PMClientSessionHost` / `PMDsSessionHost` 尚未创建 R6 driver**：`net-architecture-migration.md` 的 C 阶段欠账。
4. **DS 重建**：实机前必须重建 `HyldDS.exe`（AGENTS §14「集中联调前两端及 Lobby 需同版本重建」）。
5. **结果摘要的 Lobby 消费面**：`ClientCombatMatchResultV1` 现在是唯一胜负通道；Lobby gateway 仍只 log，需按 §7 U9 的口径补齐（本批不伪造旧 `BattleReview`）。
6. **`DesignHp` 恢复 / `ShootWidth` 与判定半径最终收敛**：仍按主计划欠账。

---

## 9. 复现命令

```
# 语言面（netstandard2.0 + C# 7.3，零 Unity）
dotnet build Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj -c Release

# 行为面（真实字节链）
dotnet build Tools/PMR6NetworkTest/PMR6NetworkTest.csproj -c Release
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll        # 220 / 0，exit 0

# 回归
dotnet build Tools/PMR5NetworkTest/PMR5NetworkTest.csproj -c Release
dotnet Tools/PMR5NetworkTest/bin/Release/net8.0/PMR5NetworkTest.dll        # 417 项 / 0 失败
dotnet Tools/PMR3RuntimeTest/bin/Release/net8.0/PMR3RuntimeTest.dll        # 180 / 0
dotnet Tools/PMR3IntegrationTest/bin/Release/net8.0/PMR3IntegrationTest.dll# 143 / 0
dotnet Tools/PMR6DeclarationTest/bin/Release/net8.0/PMR6DeclarationTest.dll# 235 / 0
dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll      # 1500 / 0
dotnet Tools/PMProjectileIntegrationTest/bin/Release/net8.0/PMProjectileIntegrationTest.dll # 977 断言
dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll                # 71 PASS / 0 FAIL
dotnet Tools/PMNetVerify/bin/Release/net8.0/PMNetVerify.dll                # 27 / 0
```

---

## 10. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r6-combat-contract.md`（全文，含尾「B 驱动冻结补充」）
→ `Docs/plans/_r6_core_report.md`（全文）→ `Docs/plans/_r6_declaration_report.md`（全文）
→ `Docs/plans/_r5_network_review.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**读取的实现（必要段/全文）**：`PMR6CombatDriver.cs`（本次新增）、`PMCombatContracts.cs`、`PMCombatWeaponPlanner.cs`、
`PMCombatSession.cs`（全文）、`PMR5ProjectileDriver.cs`（全文）、`PMR3Player.cs`（战斗声明段）、`PMR3Runtime.cs`（全文）、
`PMR3Player.g.cs`（战斗属性/RPC 描述符与发送桩）、`PMProjectileContracts.cs`、`PMProjectileCoordinator.cs`
（RequestSpawn / ResolveActivation / Drain* / 限额）、`PMProjectileLifecycle.cs`（TrySetActivationResult / Registration）、
`PMProjectileCodec.cs`（SpawnIntent / Yaw / Prediction 边界）、`PMNetWriter.cs`、`PMNetObject.cs` / `PMNetWorld.cs`
（公开面）、`BattleNumericConfig.cs`（英雄表 / ResolvedAttack）、`PMR5Projectile.cs`（事件出口）、
`Tools/PMR5NetworkTest/Program.cs`（夹具手法参照）、全部相关 `.csproj`（Include 面与既有 Exclude）。

**本次写入的文件**：§1 表中 8 个（3 源 + 3 新工具文件 + 1 报告 + 1 meta）。

**未做**：未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改主计划、Shared 契约、`PMCombatContracts.cs`、
生成产物、锁文件；未改 `PMDsSessionHost` / `PMClientSessionHost`（写边界外，已在 §7/§8 列为宿主必办）。
