## Context

当前项目正在从"纯转发帧同步"升级为"服务端权威帧同步"混合架构。

**已完成的部分**：
- 服务端已实现子弹模拟、碰撞检测、HitEvent 广播（伤害判定权威）
- 服务端已维护 `playerPositions`（每帧根据 PlayerOperation 推进），但**不下发给客户端**
- 客户端已实现 HitEvent 消费（扣血/死亡）
- 客户端已删除 AuthorityBullet 系统

**本次设计目标**：
- 服务端将已有的 `playerPositions` 随帧下行包下发（权威位置）
- 客户端收到权威位置后，简化和解为"采用权威位置 + 重放未确认输入"
- 删除旧的输入匹配判定 + 条件回滚逻辑

### 已有基础设施（不需要重写）
- **服务端位置追踪**：`playerPositions`（Dictionary<int, ServerVector3>），每帧在 `UpdatePlayerPositions` 中推进（速度=3.9f, dt=0.016f）—— 已有，只需打包下发
- **服务端位置历史缓存**：`positionHistory`（环形缓冲区 30 帧）—— 已有，V2 延迟补偿用
- **攻击操作重发窗口**：FlushPendingAttacksToOperation + dic_lastProcessedAttackId + ConfirmAttacks 三段式 —— 已有，不变
- **客户端预测历史**：PredictedFrameHistoryEntry（LinkedList + Dictionary，窗口 20 帧）—— 已有，需简化（只保留输入，不再需要全量状态快照对比）
- **帧重传机制**：SendUnsyncedFrames 从 ackedFrameId+1 到当前帧全量重传 —— 已有，权威位置搭载其中自动获得可靠性

### 关键约束
- 服务端是 C#，单线程 BattleLoop，与客户端共享 protobuf 定义
- 协议通过 `SocketProto.proto` 定义，生成代码分别部署到两端
- 客户端视觉子弹系统（BulletLogic / shell）已稳定，不需要改动
- V1 全量下发所有玩家位置，V2 再考虑增量同步

## Goals / Non-Goals

**Goals:**
- 服务端每帧下发权威玩家位置，客户端直接采用
- 客户端和解简化为"权威位置 + 重放未确认输入"（CSP 模式）
- 删除旧的输入匹配判定逻辑（TryMatchPredictedInputWithAuthority 等）
- 协议纯新增，不破坏已有通道

**Non-Goals (V1):**
- 不做增量状态同步（先全量）
- ~~不做服务端权威 HP~~ → **已升级为 V2 目标**（见 D8）→ **已完成（2026-03）**
- 不启用延迟补偿（V2）
- 不做客户端预测扣血（V2）
- 不改变服务端 tick rate（仍 60fps）

## Decisions

### D1: 权威状态下发方式

**选择：搭载在权威帧下行包中，每帧全量下发**

在 `AllPlayerOperation` 中新增 `repeated PlayerState player_states` 字段。服务端在 `CollectAndBroadcastCurrentFrame` 中，将当前 `playerPositions` 打包到该帧的 `player_states` 中。每帧包含所有玩家的完整位置（pos_x, pos_y, pos_z）。

搭载优势：
- 复用已有的帧重传机制（SendUnsyncedFrames），权威位置随帧一起重传，自动获得可靠性
- 不增加额外 UDP 包，不增加丢包风险
- 权威位置与操作帧绑定在同一帧号下，语义清晰

替代方案：
- 独立通道下发 → 增加复杂度，需要额外可靠性处理
- 只在有变化时下发（delta sync）→ V2 优化项

带宽开销：6 玩家 × 3 float × 4 byte = 72 字节/帧，可忽略。

### D2: 客户端和解策略（CSP 模式）

**选择：每次收到权威帧都直接采用权威位置 + 无条件重放未确认输入**

新流程：
```
收到权威帧批次 (server_sync_frameid, allPlayerOperations)
│
├─ 1. 取批次中**最后一帧**的 player_states
│      → 将所有玩家位置设为权威位置
│
├─ 2. 更新 sync_frameID = server_sync_frameid
│
├─ 3. TrimPredictionHistoryThroughFrame(server_sync_frameid)
│      → 丢弃已被权威确认的预测历史
│
├─ 4. ReplayUnconfirmedInputs(server_sync_frameid)
│      → 从 sync_frameID+1 到 predicted_frameID
│      → 遍历剩余预测历史中的本地输入
│      → 以权威位置为起点，逐帧应用自己的移动输入推进位置
│
├─ 5. 处理 HitEvent（已有）
│
└─ 6. 生成视觉子弹（已有）
```

与旧流程对比：
- **删除**：TryMatchPredictedInputWithAuthority —— 不再需要判断"是否需要回滚"
- **删除**：TryRestoreAuthorityAnchor / TryRestorePredictedBootstrapAnchor —— 不再需要从本地快照恢复
- **删除**：RecordAuthorityStateSnapshot（旧用法）—— 被服务端下发的权威位置替代
- **删除**：ApplyAuthorityOperations 对所有玩家的操作应用 —— 其他玩家的位置直接用权威值
- **简化**：ReplayPredictedHistoryAfter → ReplayUnconfirmedInputs —— 只重放**自己**的输入

核心简化：旧流程需要判断"是否回滚"、管理多级快照锚点、对比所有玩家输入。新流程**每次都直接校正**，逻辑线性无分支。

### D3: 预测历史结构简化

**选择：PredictedFrameHistoryEntry 只保留 FrameId + 本地 Input**

旧结构包含 `List<PredictedPlayerStateSnapshot> PlayerSnapshots`（所有玩家的位置/方向/血量/法力值快照）。这些快照是为了回滚时恢复状态用的。

新架构中，其他玩家的状态由权威帧直接提供，不需要本地快照。只需要保留**自己的输入**（MoveX/MoveY + AttackOperations），用于收到权威帧后重放。

简化后的预测历史条目：
```csharp
public class PredictedFrameHistoryEntry {
    public int FrameId;
    public PlayerOperation Input;  // 该帧自己的输入快照
    // 删除：PlayerSnapshots（不再需要）
    // 删除：BulletCount（不再需要）
    // 删除：OperationId（与 FrameId 重复）
}
```

### D4: 其他玩家位置的处理

**选择：其他玩家位置完全由权威帧驱动，不做本地预测**

在旧帧同步架构下，所有玩家的位置都由客户端本地推进（OnLogicUpdate 遍历所有 PlayerOperation 推位置）。新架构中：

- **本地玩家**：预测推进（本地输入），收到权威帧后校正 + 重放
- **其他玩家**：每次收到权威帧直接设为权威位置（可加插值平滑）

这意味着 `OnLogicUpdate` 中推进其他玩家位置的逻辑可以简化。其他玩家的移动不再需要本地推进，而是由权威帧"跳到"正确位置。表现层（HYLDPlayerController）可以用 SmoothDamp/Lerp 做平滑追赶。

### D5: 协议设计（一步到位）

**选择：PlayerState 包含 V2 可能需要的冗余字段**

```protobuf
message PlayerState {
    int32 battle_id = 1;     // 玩家 battlePlayerId
    float pos_x     = 2;     // 权威位置 X（世界坐标）
    float pos_y     = 3;     // 权威位置 Y（高度层，通常=1）
    float pos_z     = 4;     // 权威位置 Z（世界坐标）
    int32 hp        = 5;     // V2: 服务端权威 HP
    bool is_dead    = 6;     // V2: 服务端权威死亡状态
}
```

V1 行为：只填充 battle_id / pos_x / pos_y / pos_z。hp / is_dead 不填充（protobuf 3 默认 0/false）。

### D6: 服务端下发时机

**选择：在 `CollectAndBroadcastCurrentFrame` 中，UpdatePlayerPositions 之后、SpawnBulletsFromOperations 之前打包**

```
CollectAndBroadcastCurrentFrame()
├─ 1. 收集操作
├─ 2. UpdatePlayerPositions()           ← 推进位置
├─ 3. RecordPositionSnapshot()          ← 记录历史（已有）
├─ 4. PackPlayerStates(nextFrameOp)     ← 【新增】打包位置到 AllPlayerOperation.player_states
├─ 5. SpawnBulletsFromOperations()      ← 生成子弹（已有）
├─ 6. TickServerBullets()               ← 推进子弹（已有）
├─ 7. SendUnsyncedFrames()              ← 广播（已有）
```

这个时机确保：下发的位置是推进后的结果（玩家移动已应用），子弹碰撞检测用的也是同一个位置。

### D7: 已有决策继承（从伤害判定阶段）

以下决策不变，直接继承：

- **D_old1: 子弹模拟策略** — 服务端逐帧推进 + Distance 碰撞检测（已实现）
- **D_old2: HitEvent 搭载帧下行包** — 不新建独立通道（已实现）
- **D_old3: 客户端扣血在主线程直接执行** — HandleMessage → ApplyHitEvents（已实现）
- **D_old4: 英雄配置服务端硬编码** — HeroConfig 静态类（已实现）
- **D_old5: 散弹扇形模拟** — 按 bulletCount 多颗子弹（已实现）
- **D_old6: 攻击方向编码** — towardx=Z 分量, towardy=X 分量，服务端直接还原世界方向（已实现）

## Risks / Trade-offs

### R1: 全量下发带宽（V1 可接受，V2 优化）
**[风险]** 每帧下发 6 人 × 12 字节 = 72 字节。60fps × 72 = 4.3KB/s，加上 protobuf overhead 约 100 字节/帧 = 6KB/s。
**→ 缓解**：对于 UDP 局域网/移动网络均可接受。V2 改增量同步可降至 ~1KB/s。

### R2: 其他玩家位置跳变
**[风险]** 其他玩家位置从本地预测推进改为权威帧驱动后，如果网络延迟波动，可能出现位置跳变。
**→ 缓解**：表现层（HYLDPlayerController）已有 SmoothDamp/MoveTowards 追赶逻辑，可以平滑处理。如果跳变过大，可加 maxSmoothableOffset 限制（已有先例：1.5f）。

### R3: 本地玩家重放延迟
**[风险]** 每次收到权威帧都做"校正 + 重放"，如果预测窗口大（20 帧），重放需要推进 20 帧的移动计算。
**→ 缓解**：移动推进计算极轻量（pos += dir * speed * dt），20 帧 × 1 次乘法 = 微秒级。且重放只推进**自己**的位置，不需要处理所有玩家。

### R4: 位置精度累积误差
**[风险]** 服务端和客户端使用不同的 float 精度（服务端 ServerVector3 vs 客户端 UnityEngine.Vector3），长时间运行可能累积微小偏差。
**→ 缓解**：每帧权威位置校正会清零累积误差。这正是权威状态同步的核心优势。

### R5: 已有风险继承
以下风险不变，直接继承自伤害判定阶段：
- 延迟体验（HitEvent RTT/2 延迟）→ V2 客户端预测扣血
- 服务端性能（散弹 120 颗子弹同时模拟）→ 已验证微秒级
- 英雄参数同步（服务端硬编码 vs 客户端）→ 后续统一配置

### D8: 服务端权威 HP + 击杀判定 + GameOver（V2）

**背景**：V1 中服务端不维护 HP，HitEvent 的 `is_kill` 始终为 false，GameOver 完全依赖客户端上报。实测发现 HP 扣到 -40 后客户端执行旧的 `playerDieLogic()` — 3 秒自动复活，没有触发 GameOver。需要服务端权威判定死亡并主动结束战斗。

**选择：服务端维护 HP，击杀时触发 GameOver 下发**

链路设计：
```
服务端 TickServerBullets 碰撞命中
├─ playerHp[victim] -= bullet.Damage
├─ if (playerHp[victim] <= 0 && !playerIsDead[victim])
│   ├─ playerIsDead[victim] = true
│   ├─ HitEvent.is_kill = true
│   └─ 标记 oneGameOver = true（已有机制）
│       → BattleLoop 下一轮检测到 → HandleBattleEnd()
│           → 下发 BattlePushDowmGameOver 给所有客户端
│           → 战斗循环结束
└─ 客户端收到 BattlePushDowmGameOver
    ├─ BeginGameOver(false)  // 已有，停止发送帧
    ├─ toolbox.游戏结束方法() // 已有，显示胜利/失败 UI
    └─ 不再调用 playerRevive（死亡即终局）
```

关键决策：
- **HP 初始值**：从 `HeroConfig` 读取（~~与客户端 `playerBloodMax` 一致，当前雪莉=4680~~ → 临时降至约 1/5，雪莉=960，避免测试期子弹累积导致 Editor 崩溃。客户端 `HYLDStaticValue` 仍为原始值 4680）
- **死亡玩家跳过碰撞**：`TickServerBullets` 中跳过 `playerIsDead[target]==true` 的玩家
- **PackPlayerStates 填充 hp / is_dead**：V2 正式启用这两个字段，客户端可用于血条同步
- **GameOver 胜负判定**：谁死了谁输。通过 HitEvent 的 `attacker_battle_id` 确定赢家 teamId
- **客户端死亡不复活**：联网模式下 `playerDieLogic` 去掉 `Invoke("playerRevive", 3f)`，改为等服务端 GameOver

替代方案：
- 客户端本地判定死亡后主动发 `ClientSendGameOver` → 不安全，依赖客户端，且两端可能不同步
- 服务端只发 `is_kill` 让客户端自己触发 GameOver → 仍是客户端驱动，不够权威

**选用服务端主动触发的原因**：服务端已有完整的 `HandleBattleEnd` + `BattlePushDowmGameOver` 机制，只需在击杀时接入即可。

### D8 实现过程中发现的问题与修复

**D8a: 子弹方向镜像修复**
- 问题：服务端 `SpawnServerBullets` 直接使用 proto 原始值 `(Towardy, Towardx)` 作为子弹方向，未做 X 轴取反和队伍镜像翻转
- 修复：`baseX = -Towardy * teamSign`、`baseZ = Towardx * teamSign`，与客户端一致
- 约定：后续修改方向编解码时，必须同时更新两端对应代码

**D8b: 客户端死亡判定权威化**
- 问题：服务端 HP=960 击杀后 `IsKill=true`，但客户端 HP=4680 未归零，`ApplyHitEvents` 中 `playerBloodValue <= 0` 永远不满足，死亡动画不触发，GameOver 弹出时角色还站着
- 修复：`ApplyHitEvents` 改为 `shouldDie = evt.IsKill || playerBloodValue <= 0`；当 `IsKill=true` 时强制 `playerBloodValue = -1`（注意 `PlayerLogic.Update()` 检查 `playerBlood < 0` 而非 `<= 0`）

**D8c: HP 临时降低**
- 服务端 `HeroConfig._hpConfig` 全英雄 HP 降至约原值 1/5
- 原因：原始 HP 下需多轮攻击击杀，每次攻击生成大量视觉子弹 GameObject，累积导致 Editor 崩溃
- 后续计划：对象池优化完成后恢复原始 HP

## Open Questions

1. ~~**是否需要下发其他状态？**~~ — 已解决：V2 启用 hp / is_dead 字段。
2. **本地玩家重放是否需要包含攻击输入？** — 移动重放只需要 MoveX/MoveY。攻击操作通过 pendingAttacks 重发窗口独立管理，不需要在位置重放中处理。重放只影响位置。
3. **渲染层平滑策略是否需要调整？** — 当前 HYLDPlayerController 对本地玩家用 MoveTowards，对其他玩家用 SmoothDamp。新架构下其他玩家位置可能有更大的帧间跳变，可能需要调整 SmoothDamp 参数。实测后决定。
