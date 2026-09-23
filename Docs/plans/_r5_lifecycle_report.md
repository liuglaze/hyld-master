# R5-A1 报告：纯核心生命周期 / 假弹镜像接管 / 三类 Pending

> 任务：实现 R5-A1（M09 生命周期层）的纯核心。契约来源 = `Docs/plans/net-r5-projectile-contract.md`（冻结）、
> `Docs/plans/_r5_semantics_survey.md` §1.1–1.4、`Docs/plans/net-architecture-migration.md` §3.9.4 与 D-R5-01。
> 本组**只做**身份、生命周期、接管、墓碑与「谁在等激活」的记账；几何/历史/L0–L4 判定属于 A2 组，本组不依赖也不冒充 PMProjectileValidator。
> A 组不声称完成 B/C 或整个 T45（T45 仍 `PENDING_USER`）。

## 0. 交付物与验证命令

| 产物 | 路径 |
|---|---|
| 生命周期/接管/墓碑/激活账本 | `Client/Assets/Scripts/PMProjectile/PMProjectileLifecycle.cs`（+ `.cs.meta`） |
| 三类 Pending 暂存 | `Client/Assets/Scripts/PMProjectile/PMProjectilePending.cs`（+ `.cs.meta`） |
| 语言面门禁（netstandard2.0 + C#7.3，零 Unity 依赖） | `Tools/PMProjectileCoreCheck/PMProjectileCoreCheck.csproj` |
| 真实核心测试（net8.0） | `Tools/PMProjectileLifecycleTest/PMProjectileLifecycleTest.csproj` + `Program.cs` |

```
dotnet build Tools/PMProjectileCoreCheck/PMProjectileCoreCheck.csproj   -c Release   # 0 警告 0 错误
dotnet build Tools/PMProjectileLifecycleTest/PMProjectileLifecycleTest.csproj -c Release # 0 警告 0 错误
dotnet Tools/PMProjectileLifecycleTest/bin/Release/net8.0/PMProjectileLifecycleTest.dll   # 全部通过：294 项断言
```

编码约定：`.cs` = UTF-8 **BOM + CRLF**；`.cs.meta` = UTF-8 **无 BOM + LF**（`fileFormatVersion: 2` + 新 guid）。
源码 Include **逐文件给定**，不用 `PMProjectile/**` 或 `PMPrediction/**` 通配 —— 避免把 A2 组正在实现的
`PMProjectileHistory.cs` / `PMProjectileValidator.cs` 或其它组中间产物编进来造成跨组编译耦合。

## 1. 关键决策与与契约的字面差异

### 1.1 时间归属（G2 决策，禁止跨基相减）

| 用途 | 时间基 | 说明 |
|---|---|---|
| TTL / 墓碑窗口 / 挂起时长 / 墙钟单调性 | **墙钟 double 毫秒** | 统一经 `PMProjectileWallClock.Validate`；非 finite 或倒退 → 显式拒绝 |
| 帧锚新鲜度 `maxAnchorAgeMs = 600 + extraDefer` | `TotalSimTimeMs` | **本组不实现**（A2 负责），本组不做任何跨基相减 |

墙钟用 `double` 而不是整毫秒：宿主可直接喂 `Stopwatch` 毫秒，无需先取整；判据本身仍是「非 finite / 倒退拒绝」，
与契约的墙钟口径一致。相等（同毫秒内多次调用）视为合法。

### 1.2 有意偏离与自主命名（需主侧知悉）

| # | 项 | 契约/UE | 本实现 | 理由 |
|---|---|---|---|---|
| D1 | 挂起 Spawn 有界性 | UE 追加处**无 cap**；R0 挂起项无上限 | 128/owner（`PMProjectileLimits.MaxPendingSpawnsPerOwner`）+ 总 8192，满则**拒新**并计数 `RejectedByCapacityCount` | 直接落 D-R5-01「拒新而非无界」；不用 TTL 代替容量 |
| D2 | 同一 `(owner, source)` 重复入队 | 未规定（UE 会追加两条） | 返回 `DuplicateSource` 并保留**原始**挂起时刻 | 一个生成源至多一条挂起项 ⇒ 解挂时不会双重生成 |
| D3 | 命中结论「同 key 合并」的组内条数 | 只说「128 组」，未规定组内条数 | 单 key 追加 ≤ 64（`PMProjectilePendingLimits.MaxHitBatchesPerKey`），超出丢弃并计数 `DroppedAppendCount` | 否则「128 组」不构成内存上界 |
| D4 | 账本 / 终态标记 / 待取释放队列 | 只说「不能无界」 | 激活账本 512（对应 UE 512 环形缓存）、retired 标记环 2048、待取释放队列 1024（溢出丢最旧并计数 `DroppedReleaseCount`） | 显式上界 + 可观测溢出 |
| D5 | 白名单 / 命中集合的清空时机 | D-R0-39 要求**回池**必须清 | **停止不清**（墓碑期迟到命中仍要用它去重），只在 `Retire` / `PurgeExpired` / `ClearOwner` 清 | 与 UE 语义一致：停止 ≠ 销毁 |
| D6 | `ServerDirect` 携带非 0 activationId | 权威弹按构造即成立 | 若账本已 `Rejected` → 拒绝登记（`RevokedRejected`）；已 `Confirmed` / 未记录 → `Confirmed`（**不写账本**） | 终态不倒退优先；账本是**可信单写入口**（只由 `TrySetActivationResult` 写） |
| D7 | 客户端预测 activationId | 「非 0 由权威账本裁决」 | `activationId == 0` → `InvalidActivationId`（0 仅可信 ServerDirect 可用）；非 0 → 账本 `Confirmed` 立即登记 / `Rejected` 拒绝并撤销 / 否则挂起 | 拒绝照搬 UE「客户端可伪造 server-origin 号段自动 Confirmed」的信任缺口 |
| D8 | 终态不倒退的可观测形式 | 未规定 | 已 `Retire` 的 key → 迟到镜像 `Retired`、迟到 Verify `Retired`、停止 `Retired`；已 `Rejected` 的 activation → 新登记 `RevokedRejected` | 「不复活」是可断言的结果，不是注释 |
| D9 | 接管与停止事件的区分 | 「不调 StopMovementImmediately 避免二次广播」 | 镜像自带 `Stopped` 只写状态，不算停止事件；`TryRecordStop` 第二次起 `AlreadyStopped` 且 `StopEventEmitted=false` | 两种到达顺序都只产生一次停止事件 |
| D10 | 无外部回调 | 「出队/去重提交先于外部回调」或「返回独占可消费结果」 | 全库**零回调**：所有消费型 API 返回独占数组/深 clone 对象，提交发生在返回之前 | 从结构上消除「回调抛异常 → 重复结算」 |

### 1.3 容量一览（全部有界，且都有可观测计数）

| 容器 | 上限 | 溢出行为 |
|---|---|---|
| 权威登记 / 预测登记 | 各 `PMProjectileLimits.MaxProjectiles` = 1024 | `RegistryCapacity`（拒新） |
| owner 状态 | `MaxOwners` = 64 | `OwnerCapacity`（拒新）；ID 水位**保留**（断连后同 ID 仍拒） |
| 每 owner 挂起 Spawn | 128 | `OwnerCapacity`（拒新）+ 计数 |
| 挂起 Spawn 总量 | 8192 | `TotalCapacity`（拒新）+ 计数 |
| Verify 暂存 | 单 key 5 / 总 128 | `KeyCapacity` / `TotalCapacity`（丢新到）+ 计数 |
| 命中结论组 | 128 | 丢最旧 + `DroppedGroupCount++` |
| 命中组内追加 | 64/key | 丢新到 + `DroppedAppendCount++` |
| 激活账本 | 512 | 淘汰最旧**终态**项；全为 Pending → `LedgerFull`（不静默丢） |
| 已兑现命中标记 | 2048 | FIFO 淘汰 |
| 回收终态标记 | 2048 | FIFO 淘汰 |
| 待取释放队列 | 1024 | 丢最旧 + `DroppedReleaseCount++` |
| 单弹去重目标 / 白名单 | 各 100 | 停止新增（去重、幂等） |

墓碑公式（`PMProjectileTombstone.ComputeTombstoneMs`）已按契约固化并单独断言：
`max(150, delayDestroyMs, clamp(2*predictionMs + 100, 0, 1000))`。

## 2. 精确 API（主侧 A3 集成用）

命名空间统一 `PMNet.Projectile`（与冻结契约同）；依赖仅 `PMNet.Mover.PMVector3`、`PMNet.PMFrameId`（经契约）。

### 2.1 `PMProjectileLifecycle`（每 epoch 一份）

```csharp
public PMProjectileLifecycle(uint epoch);            // epoch 0 抛 ArgumentOutOfRangeException
public uint Epoch { get; }
// 只读观测
public int  RegistrationCount, AuthorityCount, PredictedCount, OwnerCount,
            ActivationLedgerCount, PendingReleaseCount, RetiredMarkerCount, DroppedReleaseCount;
public double LastWallTimeMs { get; }
public bool IsRegistered(PMProjectileKey key);
public bool IsRetired(PMProjectileKey key);                     // 终态标记环
public bool IsPredictedAlive(PMProjectileKey key);               // 位置闸门（只读、不消费）
public bool TryGetRegistration(PMProjectileKey key, out PMProjectileRegistration snapshot); // 深 clone
public bool TryGetFrozen(PMProjectileKey key, out PMProjectileSpec spec, out PMProjectileState state); // 深 clone
public bool TryGetActivationResult(uint ownerNetId, uint activationId, out PMActivationResult outcome);
// 目标集合（服务于 A2 的 L3：去重 / 白名单）
public int  HitTargetCount(PMProjectileKey key);
public int  AllowedTargetCount(PMProjectileKey key);
public bool ContainsHitTarget(PMProjectileKey key, uint targetNetId);
public bool IsTargetAllowed(PMProjectileKey key, uint targetNetId);   // 白名单空 = 不限制
public bool TryAddHitTarget(PMProjectileKey key, uint targetNetId);   // 去重、上限 100
public bool TrySetAllowedTargets(PMProjectileKey key, uint[] allowedTargets); // 深拷贝、去重、上限 100
// 登记（注册必须先于追赶）
public PMProjectileRegisterOutcome TryRegisterAuthority(
    PMProjectileTrust trust, uint ownerNetId, uint projectileId, uint authorityNetId,
    uint activationId, PMProjectileState state, PMProjectileSpec spec, double wallNowMs);
public PMProjectileRegisterOutcome TryRegisterPredicted(
    uint ownerNetId, uint projectileId, uint activationId,
    PMProjectileState state, PMProjectileSpec spec, double wallNowMs);
public PMProjectileCatchUpOutcome TryBeginCatchUp(PMProjectileKey key, double wallNowMs); // 每 key 一次
// 接管 / 镜像 / 停止
public PMProjectileTakeoverOutcome TryTakeoverPredicted(PMProjectileKey key);            // 一次消费
public PMProjectilePredictedEndOutcome NotifyPredictedEnded(
    PMProjectileKey key, double wallNowMs, int predictionMs, int delayDestroyMs);        // 假弹早结束墓碑
public PMProjectileMirrorOutcome TryApplyMirror(PMProjectileKey key, PMProjectileState mirrorState, double wallNowMs);
public PMProjectileStopOutcome   TryRecordStop(PMProjectileKey key, PMVector3 stopPosition,
    double wallNowMs, int predictionMs, int delayDestroyMs);
public PMProjectileVerifyAdmissionOutcome AdmitVerify(PMProjectileKey key, double wallNowMs);
// 激活账本（可信单写入口）+ 清理
public PMActivationLedgerResult TrySetActivationResult(uint ownerNetId, uint activationId,
    PMActivationResult outcome, double wallNowMs, out PMProjectileActivationRelease[] releases); // 独占
public int  PurgeExpired(double wallNowMs);                     // 墓碑到期清理，返回条数
public bool Retire(PMProjectileKey key, double wallNowMs);      // 回收/销毁（终态）
public int  ClearOwner(uint ownerNetId, double wallNowMs);      // 断连清理
public bool AdvanceEpoch(uint newEpoch);                        // 严格递增；旧 epoch 此后 StaleEpoch
public int  DrainPendingReleases(int max, out PMProjectileActivationRelease[] releases); // 独占 FIFO
```

### 2.2 三类 Pending（各自独立，键与语义不同）

```csharp
// A. 挂起 Spawn：128/owner、总 8192、TTL 2000ms、容量满拒新
public sealed class PMProjectilePendingSpawns {
    public PMProjectilePendingSpawns(uint epoch);
    public int Count, OwnerCount, RejectedByCapacityCount;
    public int  CountForOwner(uint ownerNetId);
    public bool Contains(PMProjectilePendingSpawnKey key);
    public bool TryGet(PMProjectilePendingSpawnKey key, out PMProjectilePendingSpawn clone); // 深 clone
    public PMProjectilePendingSpawnOutcome TryEnqueue(uint ownerNetId, uint sourceBehaviorInstanceId,
        uint activationId, uint projectileId, uint authorityNetId, int predictionMs,
        PMProjectileSpawnRequest request, double wallNowMs);
    public int PurgeExpired(double wallNowMs, out PMProjectilePendingSpawnRelease[] dropped); // 不生成
    public int ResolveByActivation(uint ownerNetId, uint activationId, PMActivationResult outcome,
        double wallNowMs, out PMProjectilePendingSpawnRelease[] releases);   // Confirmed 带 ExtraDeferMs
    public int  DiscardOwner(uint ownerNetId, double wallNowMs, out PMProjectilePendingSpawnRelease[] dropped);
    public bool AdvanceEpoch(uint newEpoch);
    public void Clear();
}

// B. Verify-before-Spawn：单 key 5 / 总 128 / FIFO / TTL 随 Spawn 2000ms
public sealed class PMProjectilePendingVerifies {
    public PMProjectilePendingVerifies(uint epoch);
    public int Count, KeyCount, PendingMarkerCount, RejectedByCapacityCount;
    public int  CountForKey(PMProjectileKey key);
    public bool IsPendingMarkerSet(PMProjectileKey key);
    public PMProjectileVerifyStashOutcome TryStash(PMProjectileVerifyRequest request, double wallNowMs);
    // 先解除 pending 标记，再按 FIFO 返回；要求 lifecycle 中已登记（注册先于回放）
    public PMProjectileVerifyReplayOutcome TryBeginReplay(PMProjectileLifecycle lifecycle,
        PMProjectileKey key, int extraDeferMs, double wallNowMs);            // Items 独占、深 clone
    public int  Discard(PMProjectileKey key);
    public int  PurgeExpired(double wallNowMs);
    public bool AdvanceEpoch(uint newEpoch);
    public void Clear();
}

// C. 命中结论：128 组 / 合并不刷新原始时刻 / 满丢最旧 / Confirm 只兑现一次
public sealed class PMProjectilePendingHits {
    public PMProjectilePendingHits(uint epoch);
    public int GroupCount, DroppedGroupCount, DroppedAppendCount;
    public int  CandidateCount();
    public bool Contains(PMProjectileKey key);
    public bool IsResolvedMarkerSet(PMProjectileKey key);
    public bool TryGetGroup(PMProjectileKey key, out PMProjectilePendingHitGroup group);
    public PMProjectileHitStashOutcome TryStash(PMProjectileKey key, PMProjectileHitBatch batch, double wallNowMs);
    public PMProjectileHitResolveOutcome TryResolve(PMProjectileKey key, PMActivationResult outcome, double wallNowMs);
    public int  PurgeExpired(double wallNowMs, out PMProjectilePendingHitExpiry[] dropped); // 不结算
    public bool AdvanceEpoch(uint newEpoch);
    public void Clear();
}
```

### 2.3 建议的调用顺序（主侧接线口径）

```
镜像/直创：TryRegisterAuthority(AuthorityServerDirect,...) → TryBeginCatchUp(key) → 追赶 → 按需 TryApplyMirror
预测假弹：TryRegisterPredicted(owner,id,activationId) → 本地生成假弹
          → 假弹活着：TryApplyMirror 返回 GateFakeAlive（不得写位置）
          → 权威镜像到：TryTakeoverPredicted(key)（一次）→ 复制冻结 Spec/State → 之后的镜像 Applied
          → 假弹提前结束：NotifyPredictedEnded(...)（墓碑）→ 迟到镜像 HiddenNoRevive（隐藏、无二次停止）
Verify 先到：PendingVerifies.TryStash(...) → 生成完成后 TryBeginReplay(lifecycle,key,extraDefer,now)（先清标记再回放）
命中结论：PendingHits.TryStash(...) → TrySetActivationResult 得到 Confirmed 后 TryResolve(...,Confirmed,now) 只兑现一次
激活裁决：TrySetActivationResult(owner, activationId, Confirmed|Rejected, now, out releases)  // 多弹同时解挂
停止：TryRecordStop(...) → 墓碑期内 AdmitVerify → AllowedInTombstone；超窗 → TombstoneExpired + PurgeExpired
```

## 3. 契约 → API → 测试 映射

| 契约条目 | API | 测试节 |
|---|---|---|
| 双 origin 身份；跨 owner 同号合法 | `TryRegisterAuthority` / `TryRegisterPredicted` | A(57) |
| 同 epoch/owner/origin ID 单调、拒重号、耗尽显式失败 | 水位 `_watermarks` + `DuplicateId`/`NonMonotonicId` | A |
| server-direct 可信入口（客户端不可伪造权威弹） | `PMProjectileTrust` | A |
| 客户端非 0 activationId 由账本裁决；0 仅 ServerDirect | `InvalidActivationId` | A/B |
| 有界 registry / owner；跨 epoch 拒；升 epoch 清理不复活 | `RegistryCapacity`/`OwnerCapacity`/`AdvanceEpoch` | A |
| 冻结副本深 Clone（入参与返回值双向隔离） | `Clone()`/`TryGetRegistration`/`TryGetFrozen` | A/B/D/E/F |
| 注册必须先于追赶、外部不可重入重复注册 | `TryBeginCatchUp`（NotRegistered/AlreadyStarted/NotAuthority/Retired） | B |
| 一次消费接管 + 位置闸门 | `TryTakeoverPredicted` / `IsPredictedAlive` / `GateFakeAlive` | B |
| 假弹已结束的迟到镜像不复活、不二次停止通知 | `NotifyPredictedEnded` + `HiddenNoRevive`/`HideWithoutStopEvent` | B |
| 拒绝即实际撤销预测实例 | `RemovedInstance=true` 释放项 | B |
| 同一 activation 多弹同时解挂 | `TrySetActivationResult` 返回多条释放项 | B |
| 终态不倒退 | `AlreadyTerminal` / `Retired` / `RevokedRejected` | B/C |
| 停止保留权威记录、重复停止不二次事件 | `TryRecordStop`（`AlreadyStopped`） | C |
| 墓碑窗口内允许迟到 Verify、超窗明确过期 | `AdmitVerify` + 公式 | C |
| 白名单/命中集合停止不清、回收才清 | `TryAddHitTarget`/`TrySetAllowedTargets`/`Retire` | C |
| 墙钟非法/倒退显式拒绝 | `PMProjectileWallClock.Validate` | A/D/E/F |
| 挂起 Spawn 128/owner、TTL 2000、解挂带挂起时长 | `TryEnqueue`/`PurgeExpired`/`ResolveByActivation` | D(35) |
| Verify 单 key 5 / 总 128 / FIFO / 先清标记 / 注册先于回放 | `TryStash`/`TryBeginReplay` | E(41) |
| 命中 128 组 / 合并不刷新原始时刻 / 满丢最旧并统计 | `TryStash` | F(52) |
| Confirm 只兑现一次；Reject/TTL/Pending 零结算 | `TryResolve`/`PurgeExpired` | F |

## 4. 测试结果

```
=== R5-A1：PMProjectile 生命周期 / 接管 / 三类 Pending（手算 oracle）===
-- A. 身份与登记（双 origin / 拒重号 / 水位 / epoch / 容量 / 深 clone / 清理）   通过 57 / 失败 0
-- B. 假弹镜像接管（位置闸门 / 一次消费 / 迟到镜像 / 拒绝撤销 / 多弹解挂）        通过 69 / 失败 0
-- C. 停止与墓碑（重复停止 / 窗口内外 Verify / 墓碑公式 / 清理时机）             通过 40 / 失败 0
-- D. Pending A：挂起 Spawn（128/owner、TTL、解挂、深 clone）                    通过 35 / 失败 0
-- E. Pending B：Verify 先于 Spawn（单 key 5 / 总 128 / FIFO / 先清标记）        通过 41 / 失败 0
-- F. Pending C：命中结论（合并 / 满丢最旧 / Confirm 仅一次 / 无结算）           通过 52 / 失败 0
全部通过：294 项断言。   退出码 0
```

负向用例是**真边界值**而非字符串门，例如：容量 128/129、512/513、1024/1025；
TTL 1999/2000；墓碑窗口 +149/+150/+151ms；墙钟 NaN/+Inf/倒退/同毫秒；
`activationId=0`、owner=0、source=0、负数 RewindMs、null 载荷、跨 epoch key。

**首次运行 12 项失败**（证明用例非空转）：其中 **5 项暴露了一个真实实现缺陷**（见 §5），
7 项是本测试自身期望值写错（`conLife` 第三条挂起时长应为 0 而非 50；镜像带 `Stopped` 后的重复停止
应为 `AlreadyStopped` 而非 `Recorded`；TTL 清理后测试时钟未推过 `s0+2000`）。修正后全部通过。

## 5. 实现期发现并修复的缺陷

**缺陷 1（真实、会导致「只撤销一半」）：激活解挂时边遍历边删等待表。**
`ReleaseWaiters` 在 `for (i = 0; i < waiters.Count; i++)` 里对 Rejected 的每个 key 调用 `Retire`，
而 `Retire` 会经 `RemoveWaiter` 从同一个 `List<PMProjectileKey>` 中移除元素 —— 于是
（a）循环条件里的 `Count` 变小导致**后半段等待弹被静默跳过**（未被撤销、计数不归零），
（b）`releases[i]` 留下 `default` 项（`RemovedInstance=false`）。
修复：先把等待列表 `ToArray()` 快照，并**先把映射从字典摘掉**，使 `RemoveWaiter` 在删除路径上自然成为 no-op。
对应断言：`B: Rejected 一次解挂两颗` / `Rejected 释放项要求实际撤销实例` / `预测计数归零`。
这条正好是契约「同一 activation 多弹可同时解挂」的核心，属必须修的语义错误，不是测试瑕疵。

**缺陷 2（编译面）：`PMProjectilePendingHits` 缺 `AcceptWallClock` 辅助方法**（三处调用编译失败）。
由 netstandard2.0 门禁在构建期直接拦住。

## 6. 未验项 / 交接

| # | 项 | 归属 |
|---|---|---|
| U1 | 与 A2 的 `PMProjectileHistory.cs` / `PMProjectileValidator.cs` 的**整目录**编译 | A3 集成。本组只做了**类型名唯一性词法检查**：目录 4 文件共 67 个类型，**无重名**（A2 拥有 History/Validator/Validate*/Skip*，本组拥有 Lifecycle/Pending/Registration/Release/WallClock/Tombstone/Limits/Outcome*），因此不预期符号冲突；但这是词法证据，不等于编译证据 |
| U2 | 真网络 / Unity 宿主 / 表现（T45 全链） | B/C 阶段，`PENDING_USER` |
| U3 | 权威 NetId 认证（Owner 取认证会话绑定玩家而非客户端自报） | B（本组只接 `authorityNetId` 参数，不冒充认证） |
| U4 | 追赶过程本身（`TryBeginCatchUp` 只建模「可以开始」与不可重入） | B/C 消费方 |
| U5 | 未注册镜像（本地没有假弹）的裸镜像路径：本组按 `UnknownKey` 拒绝，宿主需先走 `TryRegisterAuthority` | A3 决定 |
| U6 | L0–L4 几何/历史/预算、`IPMProjectileTargetHistory` 实现 | A2（本组未依赖其实现） |
| U7 | 多 epoch 并存与 DS 会话切换的真实时序（本组只保证严格递增 + 旧 epoch 不复活） | B/C |
| U8 | 「挂起时长叠加进追赶与校验窗口」的实际接线（本组只产出 `ExtraDeferMs`） | A3/B |

**主侧集成提示**：`TryRegisterAuthority` 是**唯一**能产生 `ServerDirect` 身份的入口，需要 `PMProjectileTrust.AuthorityServerDirect`；
`TrySetActivationResult` 是激活账本的**唯一**写入口；三个 Pending 类与 `PMProjectileLifecycle` 需用**同一个 epoch** 构造，
并在 `AdvanceEpoch` 时同步推进（本组不做跨对象联动，避免隐式耦合）。

## 7. 已检查范围

**完整读取的文档（按委派指定顺序）**：`AGENTS.md`（仓库路由）→ `Client/Assets/AGENTS.md`（客户端总览、坐标系、
§4 子弹系统、§7 参数表）→ `Docs/plans/net-r5-projectile-contract.md`（全文 30 行，冻结契约）→
`Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（全文 156 行，主侧冻结只读）→
`Docs/plans/_r5_semantics_survey.md` §1.1–1.4（另含 §1.5/§1.6 因同区间读到）→
`C:\Users\luomingcong\.pi\agent\skills\windows-shell-compat\SKILL.md`；
并按需读取迁移计划 §3.9.3/§3.9.4 与 R5 阶段表、D-R5-01 原文（`net-architecture-migration.md` 672–740、838–843、1029、2341–2353）。

**读取的依赖源码**：`Client/Assets/Scripts/PMNet/PMNetIdentity.cs`（`PMNetId` / `PMFrameId` / `PMTimeStep`）、
`Client/Assets/Scripts/PMMover/PMMoverState.cs`（`PMVector3` / `PMMoverDefaults`）、
`Tools/PMMoverCoreCheck/PMMoverCoreCheck.csproj` 与 `Tools/PMMoverTest`、`Tools/PMPredictionTest`、
`Tools/PMPredictionCoreCheck` 的 csproj（作为 Include 面与门禁形态参考）、
同目录 A2 文件的**类型声明清单**（仅用于重名检查）。

**未做**：未改共享契约（`PMProjectileContracts.cs` 与本组写入边界外的一切文件）；未改主计划；
未启动 Unity；未提交（git/svn 均无写入）；未递归委派；未做跨组 glob。
