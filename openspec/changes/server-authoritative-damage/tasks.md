## 阶段一：服务端权威伤害（已完成 ✅）

### 1. 协议扩展
- [x] 1.1 AttackOperation 新增 client_frame_id 字段
- [x] 1.2 新增 HitEvent 消息定义
- [x] 1.3 BattleInfo 新增 repeated HitEvent hit_events
- [x] 1.4 ActionCode 新增 BattlePushDownHitEvents
- [x] 1.5 重新生成两端 protobuf C# 代码

### 2. 服务端 - 玩家位置追踪
- [x] 2.1 新增 ServerVector3 结构体
- [x] 2.2 BattleController 新增 playerPositions / playerTeamIds / playerHeroes
- [x] 2.3 BeginBattle 初始化出生点位置
- [x] 2.4 CollectAndBroadcastCurrentFrame 中推进玩家位置
- [x] 2.5 新增服务端 moveSpeed 参数

### 3. 服务端 - 位置历史缓存
- [x] 3.1 positionHistory 环形缓冲区（窗口 30 帧）
- [x] 3.2 RecordPositionSnapshot
- [x] 3.3 TryGetPositionSnapshot（V2 查询接口）
- [x] 3.4 环形窗口淘汰逻辑
- [x] 3.5 在 CollectAndBroadcastCurrentFrame 中调用

### 4. 服务端 - 英雄子弹配置
- [x] 4.1 HeroConfig 静态类
- [x] 4.2 从客户端提取参数值

### 5. 服务端 - 子弹模拟与碰撞检测
- [x] 5.1 ServerBullet 数据结构
- [x] 5.2 activeBullets 和 SpawnServerBullets
- [x] 5.3 攻击操作处理（方向镜像、散弹扇形）
- [x] 5.4 TickServerBullets（推进、碰撞、移除）
- [x] 5.5 HitEvent 收集与帧绑定
- [x] 5.6 战斗结束清理

### 6. 客户端 - 协议适配
- [x] 6.1 AttackOperation 填充 client_frame_id

### 7. 客户端 - HitEvent 接收与扣血
- [x] 7.1 解析 BattleInfo.hit_events
- [x] 7.2 ApplyHitEvents 主线程执行
- [x] 7.3 HitEvent 去重（attack_id + victim_battle_id）
- [x] 7.4 扣血 + 死亡流程
- [x] 7.5 受击表现（动画/特效）

### 8. 客户端 - 删除 AuthorityBullet
- [x] 8.1 删除 AuthorityBullet 类定义
- [x] 8.2 删除 authorityBullets 列表和 SpawnAuthorityBullet
- [x] 8.3 删除 TickAuthorityBullets
- [x] 8.4 保留 authoritySnapshotHistory（视觉子弹复用）
- [x] 8.5 清理 OnLogicUpdate_sync_FrameIdCheck 中权威子弹段落
- [x] 8.6 清理和解路径中 AuthorityBullet 分支
- [x] 8.7 移除客户端侧碰撞参数

---

## 阶段二：服务端权威位置下发 + 和解简化（本次新增）

### 9. 协议扩展 - 权威状态

- [x] 9.1 在 `SocketProto.proto` 中新增 `AuthoritativePlayerState` 消息（battle_id, pos_x, pos_y, pos_z, hp, is_dead）。注意：proto 中已有 `enum PlayerState`（玩家在线状态），故消息命名为 `AuthoritativePlayerState` 避免冲突
- [x] 9.2 在 `AllPlayerOperation` 中新增 `repeated AuthoritativePlayerState player_states` 字段
- [x] 9.3 重新生成两端 protobuf C# 代码，部署到 Client 和 Server
- [x] 9.V 验证：两端编译通过，字段可访问（ApplyAuthoritativePositions / ReplayUnconfirmedInputs 中均正常使用）

---

### 10. 服务端 - 权威状态打包下发

- [x] 10.1 新增 `PackPlayerStates(AllPlayerOperation frameOp)` 方法：遍历 playerPositions，为每个玩家创建 AuthoritativePlayerState 写入 frameOp.PlayerStates
- [x] 10.2 在 `CollectAndBroadcastCurrentFrame` 中，RecordPositionSnapshot 之后、SpawnBulletsFromOperations 之前调用 PackPlayerStates
- [x] 10.3 在 PackPlayerStates 中加验证日志（每 60 帧打印一次，避免刷屏）
- [x] 10.V 验证服务端下发正确性：启动战斗后观察服务端日志 `[AuthState]`，确认 ① Count == 参战人数 ② 初始位置与出生点一致 ③ 移动后位置持续变化。不通过判定：Count=0、位置全零、位置不随移动变化

---

### 11. 客户端 - 权威位置接收与应用

- [x] 11.1 新增 `ApplyAuthoritativePositions(RepeatedField<AllPlayerOperation> frames)` 方法（BattleManger.cs:878-939）：取批次中最后一帧的 player_states，将所有玩家逻辑位置设为权威位置，对本地玩家记录 `lastAuthorityPosition` 作为重放起点。含坐标翻转逻辑（needFlip/sign）
- [x] 11.1.V 已通过实测验证：两客户端联机战斗，权威位置正常应用，玩家位置同步正确

---

- [x] 11.2 新增 `ReplayUnconfirmedInputs(int authorityFrameId)` 方法（BattleManger.cs:946-993）：以 lastAuthorityPosition 为起点，遍历预测历史中 frameId > authorityFrameId 的帧，逐帧按 HYLDPlayerManger.OnLogicUpdate 一致公式推进本地玩家位置
- [x] 11.2.V 已通过实测验证：本地玩家移动+权威校正后位置平滑，无明显跳变

---

- [x] 11.3 重写 `OnLogicUpdate_sync_FrameIdCheck`（BattleManger.cs:1064-1166）：新 7 步流程 ① ApplyAuthoritativePositions ② 更新 sync_frameID ③ RecordAuthorityConfirmation（仅 ConfirmAttacks） ④ TrimPredictionHistory ⑤ ReplayUnconfirmedInputs ⑥ RecordAuthoritySnapshotForFrame ⑦ 生成视觉子弹。已删除 ReconcileAuthoritativeFrame 调用
- [x] 11.3.V 已通过实测验证：两客户端联机对战，移动+攻击流程均正常，视觉子弹跨端可见

---

- [x] 11.4 删除旧和解方法（纯删除，编译验证）：ReconcileAuthoritativeFrame / ReconcileAuthorityBatch / TryMatchPredictedInputWithAuthority / TryComparePredictedInputWithAuthority / TryRestoreAuthorityAnchor / TryRestorePredictedBootstrapAnchor / RecordAuthorityStateSnapshot / RestorePlayerSnapshots / ResetBulletStateForReconciliation / GetActiveBulletCount / lastAuthorityStateSnapshot / ReconciliationResult / AuthorityStateSnapshot / ApplyAuthorityOperations / ReplayPredictedHistoryAfter — **全部已删除，Grep 零匹配**
- [x] 11.4.V 编译通过，全局搜索上述所有方法名/字段名确认零引用

---

- [x] 11.5 适配 `authoritySnapshotHistory`（视觉子弹用，BattleManger.cs:735）：AuthorityStateSnapshot 类已删除，改为 `Dictionary<int, Dictionary<int, Vector3>>`（FrameId → BattleId → Position）；RecordAuthoritySnapshotForFrame 从已应用的权威位置取值；TryGetAuthorityPosition 返回 Vector3；视觉子弹 spawnPos 取值链路正常
- [x] 11.5.V 已通过实测验证：联机攻击后视觉子弹从正确位置生成，跨端可见

---

### 12. 客户端 - 预测历史简化

- [x] 12.1 简化 `PredictedFrameHistoryEntry`：保留 FrameId + Input（PlayerOperation clone），删除 PlayerSnapshots / BulletCount / OperationId
- [x] 12.2 简化 `RecordPredictedHistory`：只记录 FrameId + Input clone（方法签名从 4 参数简化为 2 参数）
- [x] 12.3 删除 `PredictedPlayerStateSnapshot` 类定义和 `CapturePlayerSnapshots` 方法（确认无其他引用后删除）
- [x] 12.V 验证预测历史：已通过实测验证，historySize 在 1~20 之间正常

---

### 13. 客户端 - 预测路径调整

- [x] 13.1 调整 `Send_operation` 中的预测路径（BattleManger.cs:137-185）：DrainAndDispatch → ResetOperation → RecordPredictedHistory → OnLogicUpdate → CommitPredictedFrame → SendOperation，标准 CSP 预测路径已就位
- [x] 13.2 ~~调整 `OnLogicUpdate` 中其他玩家的处理~~ — **分析后确认不做**：`HYLDPlayerManger.OnLogicUpdate(PlayerOperation)` 除了推位置外还设置 `playerMoveDir` / `playerMoveMagnitude` / `fireState` / `fireTowards`，这些字段驱动渲染层动画和朝向。跳过其他玩家会导致动画失效。位置部分被 `ApplyAuthoritativePositions` 正确覆盖，功能已正确
- [x] 13.V ~~验证预测推进范围~~ — **不适用**：13.2 分析后确认所有玩家都需要在 OnLogicUpdate 中推进（驱动动画/朝向），故此验证项不再适用

---

### 14. 客户端 - 渲染层适配

- [x] 14.1 HYLDPlayerController 追赶参数已调整：本地玩家 selfSmoothSpeed=30f + MoveTowards 匀速追赶；远端玩家超过 3.0f 直接跳位，否则 Lerp 平滑
- [x] 14.2 `maxSmoothableOffset` 已提取为 Inspector 可调节的具名变量（`public float maxSmoothableOffset = 3.0f`），替换原硬编码 `3.0f`
- [x] 14.V 已通过实测验证：联机战斗中本地和远端玩家视觉平滑，无明显跳变

---

### 14B. 客户端 - 本地预测子弹（待实现）

当前状态：所有视觉子弹（含自己）都在权威帧到达后才生成（OnLogicUpdate_sync_FrameIdCheck 步骤 7），本地攻击有 1 RTT 延迟才能看到自己的子弹。

目标：本地玩家攻击时，立即在预测路径中生成视觉子弹（不等权威帧），提升打击手感。

- [x] 14B.1 在 `Send_operation` 预测路径中（CommandManger.Execute 之后），检测本帧是否有新攻击输入，立即调用 `SpawnVisualBullet` 生成本地预测子弹；权威帧到达时检查 `_predictedBulletAttackIds`，跳过已预测的本地攻击（AttackId 去重），避免重复生成。实现位置：BattleManger.cs Send_operation + BattleData._predictedBulletAttackIds + OnLogicUpdate_sync_FrameIdCheck 步骤 7
- [x] 14B.V 验证本地预测子弹：已通过实测验证，本地攻击子弹立即可见，权威帧去重正常，无重复子弹

---

## 阶段 2.5：服务端权威 HP + 击杀 + GameOver（D8）

### 17. 服务端 - HP 追踪与击杀判定

- [x] 17.1 BattleController 新增 `playerHp`（Dictionary<int, int>）和 `playerIsDead`（Dictionary<int, bool>），在 `BeginBattle` 中按 `HeroConfig.GetHp(hero)` 初始化
- [x] 17.2 `HeroConfig` 新增 `GetHp(Hero hero)` 方法，含所有 20 个英雄的 HP 配置（雪莉=4680，与客户端 `playerBloodMax` 一致）
- [x] 17.3 `TickServerBullets` 碰撞命中后：扣血 `playerHp[victim] -= bullet.Damage`；若 `playerHp <= 0 && !playerIsDead[victim]`，设 `playerIsDead[victim] = true`，设 `HitEvent.IsKill = true`
- [x] 17.4 `TickServerBullets` 碰撞检测时跳过 `playerIsDead[target] == true` 的玩家
- [x] 17.5 击杀时设置 `oneGameOver = true`，记录赢家信息（`_killerBattlePlayerId` / `_killerTeamId`）
- [x] 17.V 验证：服务端日志 `[HitEvent]` 最后一条 `isKill=True`，后续帧不再生成该 victim 的 HitEvent

---

### 18. 服务端 - PackPlayerStates 填充 HP / IsDead

- [x] 18.1 `PackPlayerStates` 中填充 `Hp = playerHp[bpId]` 和 `IsDead = playerIsDead[bpId]`（原 V1 注释改为正式代码）
- [x] 18.V 验证：客户端日志中 `AuthoritativePlayerState.Hp` 从初始值逐步递减，`IsDead` 在击杀帧变为 true

---

### 19. 服务端 - 击杀触发 GameOver 下发

- [x] 19.1 击杀时调用 `UpdatePlayerGameOver(killedBattlePlayerId)`（已有方法），触发 `oneGameOver = true` → BattleLoop 检测到后调用 `HandleBattleEnd()`
- [x] 19.2 `HandleBattleEnd` 中确保广播 `BattlePushDowmGameOver` 包含胜负信息（`pack.Str` 写入赢家 teamId）
- [x] 19.3 确保击杀帧的 HitEvent（含 is_kill=true）在 GameOver 包之前送达（先 SendUnsyncedFrames 再 HandleBattleEnd）
- [x] 19.V 验证：服务端日志 `HandleBattleEnd` 在击杀后触发，客户端收到 `BattlePushDowmGameOver`

---

### 20. 客户端 - 死亡不复活 + GameOver 接入

- [x] 20.1 `playerDieLogic()` 中：去掉 `Invoke("playerRevive", 3f)` 和 `playerBloodValue = playerBloodMax`（不自动复活、不自动满血）
- [x] 20.2 `HandleMessage` 中 `BattlePushDowmGameOver` 分支：解析 `pack.Str` 为 winnerTeamId，与 `BattleData.Instance.teamID` 比较，设置 `HYLDStaticValue.玩家输了吗` 标志
- [x] 20.3 确认 `HandleMessage` 中 `BattlePushDowmGameOver` 分支的 `BeginGameOver(false)` + `toolbox.游戏结束方法()` 链路正常触发 GameOver UI
- [x] 20.V 验证：一方被打死后，两个客户端都显示胜利/失败 UI，不出现 3 秒复活

---

### 21. 端到端死亡/GameOver 验证

- [x] 21.1 **击杀测试**：一方站桩不动，另一方持续攻击直到击杀。观察：① 服务端 HitEvent 最后一条 is_kill=True ② 服务端 HandleBattleEnd 触发 ③ 两个客户端都显示 GameOver UI ④ 死亡方显示"失败"，存活方显示"胜利"
- [x] 21.2 **死亡后不复活测试**：击杀后等待 5 秒，确认被杀玩家不会复活
- [x] 21.3 **死亡后不继续扣血测试**：已通过实测验证，击杀后服务端不再生成 HitEvent

---

## 阶段三：文档更新

### 15. 文档

- [x] 15.1 更新 `Assets/CLAUDE.md`：架构更新为"服务端权威帧同步"、和解重写、主链路更新
- [x] 15.2 更新 `Assets/Docs/ForServer.md`：新增权威状态下发链路、更新 CollectAndBroadcastCurrentFrame 流程
- [x] 15.3 更新 `BothSide.md`：新增 PlayerState 协议、AllPlayerOperation.player_states 语义

---

## 阶段四：集成验证

### 16. 端到端验证

每项测试前确保：服务端和客户端都使用新生成的 protobuf 代码，客户端开启 `EnablePredictionReconciliationPipeline=true`。

- [x] 16.1 **编译验证**
  - 两端分别编译通过（服务端 `dotnet build` 0 错误，客户端 Unity 编译通过）
  - 全局搜索已删除方法名确认零引用（仅注释中保留说明性提及）

- [x] 16.2 **静止测试**：已通过，900 帧位置稳定在出生点，零漂移

- [x] 16.3 **单人移动测试**：已通过。delta 全程 0.000（局域网），移动轨迹 (15.00,-5.00)→(2.95,0.14) 连续平滑，replayedCount 0~1，静止方位置不变
  - 操作：一个客户端持续向右移动，另一个静止
  - 观察移动方客户端日志 `[AuthPos]`：delta 应在 0~0.5 之间（取决于 RTT）
  - 观察移动方客户端日志 `[Replay]`：replayedCount > 0，endPos 持续向右推进
  - 观察静止方客户端日志 `[Render]`：对方玩家的 renderPos 持续追赶 logicPos，gap < 1.0
  - **通过标准**：移动方位置与服务端一致（每秒 delta < 0.5），静止方看到对方平滑移动

- [x] 16.4 **双人移动测试**：已通过。两方 delta 均为 0.000，Liuliclone 曾有 batchSize=12 帧积压导致 replayedCount=9，但权威校正后 delta 仍为 0，位置连续无跳变
  - 操作：两个客户端同时在不同方向移动
  - 观察两方日志 `[AuthPos]`：各自 delta 正常
  - 观察两方日志 `[Render]`：对方玩家平滑移动，无明显跳变
  - **通过标准**：两方 gap 都 < 1.5

- [x] 16.5 **移动+攻击联合测试**：已通过。两端各发 3 次攻击，HitEvent 正常触发，扣血递减一致（4680→4600→4520→4440），去重正常（[Dup] skip），无重复扣血
  - 操作：一方移动并攻击，另一方站桩
  - 观察 HitEvent 仍正常触发（`[HitEvent]` 日志）
  - 观察视觉子弹生成正常
  - **通过标准**：被击方扣血正常，无重复扣血

- [x] 16.6 **变向测试（重放压力）**：已通过。Liuliclone 执行 5 次以上方向反转（Z 轴 -4.81→-2.63→-4.17→-2.95→-3.94→-0.38→-0.70...），delta 全程 0.000，replayedCount 最大 10（初期帧积压），变向 1~2 帧内响应，位置连续无跳变
  - 操作：快速左右切换移动方向（在 1 秒内多次变向）
  - 观察 `[Replay]` 日志：replayedCount 在 1~20 之间，endPos 与实际表现位置一致
  - 观察 `[AuthPos]` 日志：delta 可能短暂增大（因为变向导致预测偏差），但权威帧到达后迅速收敛
  - **通过标准**：delta 峰值 < 2.0，并在 0.5 秒内收敛到 < 0.5

- [x] 16.7 **丢包模拟测试**：已通过。服务端 SimDropRate=0.30（30% 下行丢包），两端 delta 全程 0.000，batchSize 出现 2~3（丢包后重传批量到达），replayedCount 最大 5（Liuliclone 末尾），HitEvent 去重正常（[Dup] skip 4 次），攻击扣血一致，无位置永久偏移。缺失的采样帧号（frame=360/600/840 等）符合 30% 丢包预期
  - 操作：在服务端或网络层模拟 30% 丢包率
  - 观察 `[SyncCheck]` 日志：batchSize 可能 > 1（帧重传批量到达），syncAfter 仍正确更新
  - 观察 `[AuthPos]` 日志：delta 可能增大，但权威帧到达后立刻校正
  - **通过标准**：丢包下位置仍能正确校正，无位置永久偏移

- [ ] 16.8 **日志清理**
  - 验证全部通过后，将所有 `% 60 == 0` 的调试日志改为 `FrameTrace`（仅 Debug 构建输出）或删除
  - 保留 `[AuthPos]` 的 delta 日志作为长期监控（降频到每 300 帧）

---

## 附录：额外已完成的改动（不在原计划内）

### A1. 单机模式代码清理 ✅

移除了联机模式中残留的单机分支代码，减少维护复杂度：

- [x] A1.1 BattleManger.cs — `Send_operation` 中删除 `!isNet` 分支（单机无预测路径）
- [x] A1.2 BattleManger.cs — `OnLogicUpdate` 中删除 `!isNet` 时的单机子弹发射逻辑
- [x] A1.3 HYLDBulletManger.cs — `OnLogicUpdate` 中删除 `!isNet` 时直接 Attack 的单机子弹逻辑
- [x] A1.4 HYLDPlayerManger.cs — `GeneratePlayer` 中删除 `!isNet` 时直接 Instantiate 的单机路径
- [x] A1.5 TouchLogic.cs — `Start` 中删除 `!isNet` 时的单机初始化分支

### A2. 攻击输入死区修复 ✅

- [x] A2.1 TouchLogic.cs — 攻击触发条件从 `distanceLenth < 0.18f || distanceLenth < 0.12f`（逻辑错误，永真）修复为 `distanceLenth < 0.18f && distanceLenth < 0.12f`，恢复正常的滞回死区判定

### A3. 攻击管线诊断日志 ✅

- [x] A3.1 TouchLogic.cs — 攻击输入入口添加 `[AttackInput]` 日志（ACCEPTED/REJECTED_DEAD）
- [x] A3.2 CommandManger.cs — `AddCommad_Attack` 添加 `[AttackPipeline] EnqueueAttack` 日志
- [x] A3.3 BattleManger.cs — `FlushPendingAttacksToOperation` 添加 `Flush pendingCount` 日志
- [x] A3.4 已通过实测验证攻击管线完整链路：TouchLogic → CommandManger → BattleData → UDP 上行 → 服务端收到攻击 → HitEvent 下行

### A4. D8 实现过程额外修复 ✅

- [x] A4.1 服务端子弹方向镜像修复：`SpawnServerBullets` 新增 `baseX = -Towardy * teamSign`、`baseZ = Towardx * teamSign`，与客户端方向变换一致
- [x] A4.2 客户端死亡判定权威化：`ApplyHitEvents` 死亡判定改为 `shouldDie = evt.IsKill || playerBloodValue <= 0`；`IsKill=true` 时强制 `playerBloodValue = -1`
- [x] A4.3 服务端 HP 临时降低：`HeroConfig._hpConfig` 全英雄 HP 降至约原值 1/5（测试阶段，后续对象池优化后恢复）
- [x] A4.4 文档全面更新：`Assets/CLAUDE.md`、`Assets/Docs/ForServer.md`、`BothSide.md`、`Server/CLAUDE.md`、`Server/Docs/ForClient.md` 均已同步更新 D8 变更
