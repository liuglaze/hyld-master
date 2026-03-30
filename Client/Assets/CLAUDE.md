- 客户端项目架构速览（联机战斗）

  > 本文件用于会话启动时快速加载项目上下文；面向服务端协作的细节请看 Assets/Docs/ForServer.md。

  ## 1. 项目现状

  - 项目是演进式代码库：Assets/Scripts 新链路与 Assets/HYLD1.0/Scripts/OldScripts 历史逻辑并存。
  - 联机主要模式：大厅/登录多为 TCP；战斗帧同步主要走 UDP。
  - 战斗逻辑帧时长：ConstValue.frameTime = 0.016f（16ms，约 60fps 逻辑帧）。

  ## 2. 坐标系与摄像机

  - **游戏坐标系**：X = 水平，Z = 屏幕纵向（上下），Y = 固定高度层（玩家 Y=1）。
  - **摄像机**：沿 Z 轴观察，HYLDCameraManger.LateUpdate 中 endPos.z = transform.position.z（Z 轴锁死）。
  - **出生点**：我方 (15, 1, -5/0/5)，敌方 (-15, 1, 5/0/-5)（镜像）。

  ## 3. 当前战斗主链路（客户端）

  ### 3.1 输入采集层

  Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs

  - 移动输入采用滞回阈值（start=0.18/stop=0.12 dead zone），归一化后写入 HYLDStaticValue.PlayerMoveX/Y。
  - 攻击为离散输入（摇杆松手触发），调用 CommandManger.Instance.AddCommad_Attack()。

  ### 3.2 命令聚合层

  Assets/Scripts/Manger/CommandManger.cs

  - AddCommad_Move 只缓存"最新移动值"（连续输入）。
  - AddCommad_Attack 入队到 BattleData.pendingAttacks（分配唯一 AttackID）。
  - Execute() 每个发送帧先写移动，再消费离散命令，最后 FlushPendingAttacksToOperation 把所有待确认攻击打包。

  **攻击操作重发窗口机制（四段式）**：

  1. **EnqueueAttack**：入队时锁定 `ClientFrameId = predicted_frameID`，后续重发不刷新（避免丢包恢复后 predicted_frameID 跳跃导致延迟补偿 delay<0）。
  2. **FlushPendingAttacksToOperation**：每帧先做**超时清理**（`predicted_frameID - ClientFrameId > MaxClientAttackAge=10` 的攻击直接移除），然后将剩余待确认攻击全部打包到 selfOperation.AttackOperations。
  3. **dic_lastProcessedAttackId**（服务端）：服务端记录每个玩家最后处理的 AttackId，重复收到旧攻击时忽略。超过 `MaxAcceptableAttackDelay=6` 帧的攻击直接 REJECT。
  4. **ConfirmAttacks**：收到权威帧后，若该帧包含本玩家的攻击确认（HasSelfAuthorityInput），调用 ConfirmAttacks 从 pendingAttacks 移除已确认的攻击（通过 maxConfirmedId 批量移除 <= maxConfirmedId 的项）。

  这样可在 UDP 丢包时自动重传未确认的攻击，不依赖可靠传输层。客户端超时清理防止确认丢包时攻击无限重发浪费带宽。

  ### 3.3 发送与本地预测层（累加器驱动 + 动态追帧）

  Assets/Scripts/Server/Manger/Battle/BattleManger.cs

  - **驱动方式**：已从 `InvokeRepeating("Send_operation", _time, _time)` 改为 `Update()` 中累加器驱动。`_tickAccumulator += Time.deltaTime`，当 `_tickAccumulator >= currentTickInterval` 时调用 `BattleTick()`（原 Send_operation 逻辑），每帧最多执行 `maxCatchupPerUpdate=3` 次 tick。
  - **动态 Tick 调节**：`currentTickInterval = 0.016f / actualSpeedFactor`，`actualSpeedFactor` 由 `AdjustTickInterval()` 每帧平滑调整（详见 §X 动态追帧系统）。
  - **BattleReady / BattleStart 握手语义**：客户端在初始化完成后每 200ms 重发一次 `BattleReady`；收到 `BattleStart` 后才会 `CancelInvoke("Send_BattleReady")` 并开启 `_battleTickActive`。服务端已支持：若首次 `BattleStart` 丢失，客户端继续重发的 `BattleReady` 会触发服务端单播补发 `BattleStart`。
  - **Update 管线顺序**：DrainAndDispatch → Ping 调度 → CalcTargetFrame + AdjustTickInterval → 累加器循环（while tick）。
  - 联机预测路径（IsPredictionEnabled=true），BattleTick() 内部逻辑：
    1. DrainAndDispatch() -> 消费 UDP 队列中已收到的权威帧
    2. ResetOperation -> CommandManger.Execute
    3. **本地预测子弹**：检测本帧是否有新攻击输入，若有则立即 SpawnVisualBullet（不等权威帧），通过 `_predictedBulletAttackIds` 记录已预测的 AttackId
    4. RecordPredictedHistory(nextFrame) -> 快照当前状态（输入应用前）
    5. OnLogicUpdate(nextFrame, localOps, isBuzhen=true, isReconciliation=false) -> 推进逻辑
    6. CommitPredictedFrame(nextFrame) -> 提交预测帧号
    7. SendOperation(uploadOperationId) -> 上行到服务端（uploadOperationId = nextFrame = predicted_frameID + 1）

  ### 3.4 权威帧接收入口

  BattleData.OnLogicUpdate_sync_FrameIdCheck(server_sync_frameid, allPlayerOperation)

  - UDP 回调 HandleMessage -> BattlePushDowmAllFrameOpeartions -> 调用此方法。
  - 流程：和解 -> 记录权威确认 -> 更新 sync_frameID -> **生成视觉子弹** -> ApplyHitEvents（受击动画，不扣血）-> ApplyAuthoritativeHpAndDeath（权威 HP 覆写 + 死亡判定）。

  ### 3.5 权威位置校正 (CSP 模式)

  旧的回滚式和解（ReconcileAuthoritativeFrame / ReconcileAuthorityBatch 等）已全部删除，替换为 CSP（Client-Side Prediction）权威位置校正：

  OnLogicUpdate_sync_FrameIdCheck 收到权威帧批次后：
  1. **ApplyAuthoritativePositions**：取批次最后一帧的 player_states，将所有玩家逻辑位置设为服务端权威位置
  2. **更新 sync_frameID**
  3. **RecordAuthorityConfirmation**：ConfirmAttacks 移除已确认的攻击
  4. **TrimPredictionHistory**：裁剪已确认帧的预测历史
  5. **ReplayUnconfirmedInputs**：以权威位置为起点，重放本地玩家 frameId > authorityFrameId 的预测输入
  6. **RecordAuthoritySnapshotForFrame**：记录权威快照供视觉子弹定位
  7. **生成视觉子弹**：遍历权威帧中各玩家攻击操作，跳过已本地预测的（_predictedBulletAttackIds 去重）

  ## 4. 子弹系统（服务端权威判定 + 客户端纯表现）

  ### 4.1 概述

  联网模式伤害判定已迁移至服务端（server-authoritative-damage）。客户端子弹系统仅作视觉表现。

  | 类别         | 数据位置              | 职责                       | 生命周期       |
  | ------------ | --------------------- | -------------------------- | -------------- |
  | **视觉子弹** | shell + BulletLogic   | HYLDBulletManager.AllShells (Unity GameObject) | 视觉表现 |
  | **HitEvent** | BattleInfo.hit_events | 服务端下行，ApplyHitEvents 触发受击动画（不扣血） | 主线程消费 |

  **伤害判定链路**：服务端 BattleController 做子弹模拟 → 命中生成 HitEvent → 随帧下行包广播 → 客户端 HandleMessage → ApplyHitEvents → 受击动画（纯表现）→ ApplyAuthoritativeHpAndDeath → 权威 HP 覆写 + 死亡判定。

  ### 4.1.1 延迟补偿 V2（服务端追帧模拟 + 客户端半空推进）

  **服务端延迟补偿**（BattleController.Bullets.cs）：
  - 收到延迟攻击（`clientFrameId < frameid`）时，从 `positionHistory[clientFrameId]` 取攻击者历史位置生成子弹
  - `SimulateBulletCatchUp`（BattleController.Bullets.cs:233）：从 clientFrameId 到 frameid 逐帧推进子弹，每帧用 `positionHistory[f]` 做碰撞检测
  - 追帧期间命中：生成 HitEvent，子弹销毁。未命中：加入 `activeBullets` 继续正常模拟
  - `CheckBulletCollision`（BattleController.Bullets.cs:163）：碰撞检测共享方法，`TickServerBullets` 和 `SimulateBulletCatchUp` 复用
  - `SpawnServerBullets`（BattleController.Bullets.cs:70）将最终 spawnPos 写入 `atk.SpawnPosX/Y/Z`，随帧下行广播给客户端

  **Proto 扩展**：
  - `AttackOperation` 新增 `spawn_pos_x/y/z`（field 5/6/7），protobuf float 默认 0，旧客户端不受影响

  **客户端半空推进**（BattleManger.cs 路径 B）：
  - 读取 `attack.SpawnPosX/Y/Z`，非零时用作子弹生成位置（替代权威快照位置）
  - 计算 `elapsedFrames = frameId - attack.ClientFrameId`
  - 子弹初始位置沿飞行方向推进 `elapsedFrames * hero.speed * frameTime`
  - 路径 A（本地预测子弹）不受影响，仍使用本地当前位置、零延迟

  ### 4.2 HitEvent 消费（纯表现层）

  **入口**：`BattleData.ApplyHitEvents(RepeatedField<HitEvent> hitEvents)`，在 `HandleMessage` 处理 BattlePushDowmAllFrameOpeartions 后调用（主线程）。

  **职责**：仅触发受击动画，**不修改 `playerBloodValue`**。HP 的唯一修改来源为 `ApplyAuthoritativeHpAndDeath`。

  **去重**：`_appliedHitEventKeys`（HashSet<long>），key = `attackId * 100000 + victimBattleId`，防止帧重传重复播放动画。

  **流程**：
  1. 按 `victim_battle_id` 从 `playerIndexBattleIds` 找到 `victimIndex`
  2. 触发受击动画：`bodyAnimator.SetTrigger("Hit")`
  3. 记录 `_hitAnimatedPlayers.Add(victimIndex)`（供 §4.2.2 兜底动画判断）
  4. `IsKill` 兜底：若 `evt.IsKill && isNotDie`，强制 `playerBloodValue = -1`，触发死亡动画（作为 IsDead 未到达前的安全网，后续可移除）

  **清理**：战斗结束时 `ClearPredictionRuntimeState()` 调用 `ClearHitEventState()` + `_hitAnimatedPlayers.Clear()`。

  ### 4.2.2 权威 HP/IsDead 消费（ApplyAuthoritativeHpAndDeath）

  **入口**：`BattleData.ApplyAuthoritativeHpAndDeath(RepeatedField<AllPlayerOperation> frames, int frameId)`，在 `ApplyHitEvents` 之后调用。

  **职责**：以服务端 `PlayerStates.Hp` 和 `PlayerStates.IsDead` 为唯一真值，覆写客户端 HP 并驱动死亡判定。

  **调用顺序**：HandleMessage → ApplyHitEvents（动画 + 记录 hitAnimatedPlayers）→ ApplyAuthoritativeHpAndDeath（HP 覆写 + 兜底动画 + 死亡判定）

  **HP 覆写流程**：
  1. 取批次最后一帧的 `PlayerStates`，检查帧序（`batchLastFrameId <= _lastAuthHpFrameId` 时跳过整个批次，防止 UDP 乱序导致 HP 回弹）
  2. 首次到达时初始化 `_playerMaxHp[playerIndex]` 和 `hero.BloodValue`（用于血条比例）
  3. 覆写 `playerBloodValue = state.Hp`

  **兜底受击动画**：
  - 若 `newHp < oldHp` 且该玩家不在 `_hitAnimatedPlayers` 中 → 补播 `SetTrigger("Hit")`
  - 若已有 HitEvent 动画 → 跳过
  - 首次初始化（硬编码→服务端值）→ 跳过（不是真实扣血）

  **死亡判定**：
  - `state.IsDead == true` 且 `isNotDie == true` → 强制 `playerBloodValue = -1`，设 `isNotDie = false`，触发死亡动画

  **清理**：`ClearPredictionRuntimeState()` 中 `_hitAnimatedPlayers.Clear()` + `_playerMaxHp.Clear()` + `_lastAuthHpFrameId = 0`。

  ### 4.2.1 服务端权威 HP + 击杀 + GameOver（D8）

  **服务端**（拆分后文件结构）：
  - `playerHp`（Dictionary<int, int>）：`BeginBattle`（Battle.cs:224）按 `HeroConfig.GetHp(hero)`（HeroConfig.cs:103）初始化
  - `playerIsDead`（Dictionary<int, bool>）：初始 false
  - `CheckBulletCollision`（BattleController.Bullets.cs:163）碰撞命中后：`playerHp[victim] -= bullet.Damage`；若 HP <= 0 且未死亡，设 `playerIsDead = true`，`HitEvent.IsKill = true`
  - 碰撞检测跳过 `playerIsDead == true` 的玩家
  - 击杀时 `oneGameOver = true` → `BattleLoop`（Battle.cs:320）下次循环检测到 → `HandleBattleEnd()`（BattleController.Network.cs:183）→ `SendFinishBattle`（BattleController.Network.cs:224）（pack.Str = winnerTeamId）
  - `PackPlayerStates`（BattleController.Network.cs:18）每帧下发 `Hp` 和 `IsDead`

  **时序保证**：击杀帧的 HitEvent（IsKill=true）通过 `SendUnsyncedFrames`（BattleController.Network.cs:50）在 `CollectAndBroadcastCurrentFrame`（Battle.cs:393）中发送，`HandleBattleEnd` 在 BattleLoop 下次迭代才执行，客户端先收到含 kill 的 HitEvent 再收到 GameOver。

  **客户端**：
  - `ApplyHitEvents`：纯表现层，仅触发受击动画，不修改 HP（详见 §4.2）
  - `ApplyAuthoritativeHpAndDeath`：以 `PlayerStates.Hp` 覆写 `playerBloodValue`，以 `PlayerStates.IsDead` 驱动死亡判定（详见 §4.2.2）
  - `HandleMessage` 收到 `BattlePushDowmGameOver`：解析 `pack.Str` 为 winnerTeamId，与 `BattleData.Instance.teamID` 比较，设置 `HYLDStaticValue.玩家输了吗`，然后 `BeginGameOver(false)` + `toolbox.游戏结束方法()` 显示胜利/失败 UI

  ### 4.3 视觉子弹（表现层）

  **层级关系**：
  BulletPool (空 GameObject, bulletParent) └─ Bullet(Clone) [BulletLogic 组件] ⟵ Instantiate(bullet预制体) └─ 由 BulletLogic.Shoot() 协程生成的实际子弹：子弹14雪莉(Clone) [shell 组件, Collider, Rigidbody, Renderer] 子弹14雪莉(Clone) ... (散弹可能生成 20 颗)

  **生成链路（联网模式 — 双路径）**：

  **路径 A — 本地预测子弹（自己的攻击，零延迟）**：
  Send_operation ➞ CommandManger.Execute ➞ 检测 selfOperation.AttackOperations ➞ 跳过已预测的（IsAttackPredicted） ➞ SpawnVisualBullet(selfIdx, selfPos, dir) ➞ MarkAttackPredicted(attackId)

  **路径 B — 权威帧子弹（其他玩家的攻击 + 未预测的本地攻击）**：
  OnLogicUpdate_sync_FrameIdCheck 步骤 7 ➞ 遍历 allPlayerOperation[i].Operations ➞ 若 battleId==self 且 AttackId 已预测则 SKIP_AUTH ➞ 读取 attack.SpawnPosX/Y/Z（非零时用作 spawnPos，含坐标翻转）➞ 计算 elapsedFrames 做半空推进 ➞ SpawnVisualBullet(playerIndex, bulletSpawnPos, dir)

  **去重机制**：`_predictedBulletAttackIds`（HashSet<int>），预测时 Add，权威帧到达时 Remove+skip。确保同一次攻击不生成两波子弹。

  **底层链路（两路径共享）**：
  SpawnVisualBullet ➞ NetGlobal.Instance.AddAction(lambda) // 排队到主线程 ➞ [主线程] Attack(playerID, fireState) ➞ Instantiate(bullet预制体) as BulletLogic ➞ changeGun() 设置英雄参数 ➞ bulletLogic.Towards = fireTowardsTemp ➞ bulletLogic.InitData(AddShell callback) ➞ Invoke("Shoot", 0) // 下一帧执行 ➞ Destroy(this.gameObject, 30) // 30秒后自毁 ➞ Shoot() ➞ ShanxingShoot / StraightShoot / 蜜蜂大招 (协程) ➞ 循环 Instantiate(bulletPrefab) as shell ➞ go.transform.LookAt(go.transform.position + Towards) ➞ ParabolaShoot(go) ➞ 设置 Bullet Layer ➞ shell.bulletDamage/bulletOnwerID 赋值 ➞ CallBack(shell, speed, shootDistance/speed) ➞ AddShell(shell, speed, health) ➞ shell.isVisualOnly = true (联网模式) ➞ shell.InitData(speed, health, RemoveShell) ➞ 禁用所有 Collider ➞ Rigidbody.isKinematic = true ➞ moveDir = transform.forward ➞ Net_pos = transform.position

  **飞行更新**：

  - shell.OnUpdateLogic()（由 bulletManger.OnLogicUpdate 每逻辑帧调用）：
    - Net_pos += moveDir * speed * frameTime
    - 寿命到期 -> Die() -> Destroy(gameObject) + 从 AllShells 移除
  - shell.FixedUpdate()（Unity 物理帧）：
    - transform.position = Net_pos (直接同步，不插值)
    - 更新拖尾特效

  **视觉子弹的碰撞处理（当前状态）**：

  - isVisualOnly=true 时，InitData 中**禁用所有 Collider** + 设 Rigidbody.isKinematic=true
  - 由于 Collider 已禁用，OnTriggerEnter 不会触发，视觉子弹不执行任何伤害判定

  **和解时的处理**：

  - ResetForReconciliation 中 isVisualOnly=true 的子弹**不会被销毁**，保留在 AllShells 中继续飞行
  - 只有非视觉子弹（单机模式的 shell）会被和解清除

  ### 4.4 单机模式子弹（计划剥离）

  > **注意**：单机模式已决定全面剥离，将作为独立 OpenSpec change 执行。以下描述仅供残留代码理解参考。

  - HYLDBulletManager.OnLogicUpdate 中，!isNet 时遍历玩家的 fireState，满足条件直接 Attack()
  - shell 的 isVisualOnly=false，保留完整碰撞体，OnTriggerEnter 执行伤害判定
  - 不经过 HitEvent 系统

  ### 4.5 已知问题与调试残留

  - ~~shell.OnUpdateLogic 中 Die 逻辑被注释掉~~ **已修复**：视觉子弹 3 秒后 Die()，非视觉子弹 health 到期后 Die()
  - BulletLogic.InitData 中 Destroy(this.gameObject, 5) 为父级 BulletLogic 的自毁保底
  - shell.FixedUpdate 中拖尾特效 Instantiate 间隔从 0.04s 调整为 0.15s（散弹 20 颗 × 高频 Instantiate 导致 Editor 卡死）
  - 非抛物线模式下 rigidbody.velocity 赋值被注释（BulletLogic.cs:252），飞行完全靠 shell.OnUpdateLogic
  - Quaternion.Euler(Towards) 将方向向量误用为欧拉角（BulletLogic 所有 Instantiate 处），被后续 LookAt 覆盖
  - ~~**客户端-服务端 HP 不同步（临时状态）**~~ **已解决**：`ApplyAuthoritativeHpAndDeath` 从服务端 `PlayerStates.Hp` 覆写客户端 HP，首帧初始化 `maxHp` 和 `hero.BloodValue`，客户端不再使用硬编码 HP 参与战斗逻辑（HYLDStaticValue.cs 中硬编码值仅作非战斗场景的占位默认值）

  ## 5. 关键文件索引（函数级）

  ### 5.1 战斗主循环与数据

  **Assets/Scripts/Server/Manger/Battle/BattleManger.cs**（496 行） — 战斗主循环入口
  - `Init`:44 — 挂载子管理器、初始化 UDP
  - `Update`:81 — DrainAndDispatch → Ping → CalcTargetFrame + AdjustTickInterval → 累加器 BattleTick
  - `CalcTargetFrame`:191 — targetFrame = sync + RTT/2 + inputBuffer
  - `AdjustTickInterval`:216 — 动态 Tick 间隔 + 严重超前暂停
  - `BattleTick`:249 — 单次逻辑帧：预测子弹 + 历史录入 + OnLogicUpdate + 发送
  - `OnLogicUpdate`:331 — 遍历操作驱动玩家移动和子弹更新
  - `HandleMessage`:361 — UDP 消息入口：Pong/BattleStart/权威帧/GameOver 分发
  - `BeginGameOver`:436 — GameOver 入口：停 tick、清理预测、通知服务端

  **Assets/Scripts/Server/Manger/Battle/BattleData.cs**（291 行） — 战斗数据单例（partial class 主文件）
  - `InitBattleInfo`:234 — 初始化玩家列表、battleID、teamID
  - `CommitPredictedFrame`:182 — 提交预测帧号（只增不减）
  - `ResetOperation`:258 — 每帧发送前清零移动（保留攻击队列）
  - `ClearPredictionRuntimeState`:200 — 战斗结束全量清理

  **Assets/Scripts/Server/Manger/Battle/BattleData.Authority.cs**（307 行） — 权威帧消费
  - `ApplyAuthoritativePositions`:20 — 批次最后帧 PlayerStates → 所有玩家逻辑位置
  - `UpdateAnimationStateFromAuthority`:88 — 权威帧操作 → 动画驱动参数
  - `OnLogicUpdate_sync_FrameIdCheck`:169 — 权威帧主入口：位置校正 → 帧号更新 → 确认 → 裁剪 → 重放 → 视觉子弹

  **Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs**（258 行） — 预测与和解
  - `RecordPredictedHistory`:16 — 记录本帧预测输入快照
  - `RecordAuthorityConfirmation`:68 — 记录权威确认帧 + ConfirmAttacks
  - `ReplayUnconfirmedInputs`:209 — 以权威位置为起点重放未确认输入
  - `RecordAuthoritySnapshotForFrame`:163 — 记录权威位置快照（供视觉子弹定位）

  **Assets/Scripts/Server/Manger/Battle/BattleData.HitEvent.cs**（223 行） — HitEvent + 权威 HP
  - `ApplyHitEvents`:35 — 受击动画（纯表现，不扣血）+ IsKill 兜底
  - `ApplyAuthoritativeHpAndDeath`:126 — 权威 HP 覆写 + 兜底动画 + IsDead 死亡判定

  **Assets/Scripts/Server/Manger/Battle/BattleData.Attack.cs**（116 行） — 攻击重发窗口
  - `EnqueueAttack`:48 — 入队攻击（锁定 ClientFrameId）
  - `FlushPendingAttacksToOperation`:63 — 超时清理 + 打包到 selfOperation
  - `ConfirmAttacks`:93 — 批量移除已确认攻击

  **Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs**（57 行） — RTT 测量
  - `ProcessPongRttSample`:29 — EWMA 平滑 RTT（alpha=0.125, beta=0.25）

  ### 5.2 子弹系统（客户端纯表现）

  **Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs**（221 行） — 视觉子弹管理
  - `OnLogicUpdate`:47 — 每逻辑帧遍历 AllShells 推进飞行
  - `SpawnVisualBullet`:137 — 权威帧/预测路径生成视觉子弹（AddAction 排队主线程）
  - `Attack`:161 — 主线程实例化 BulletLogic + 设置英雄参数 + 触发射击动画
  - `AddShell`:65 — 联网模式设 isVisualOnly=true
  - `ResetForReconciliation`:76 — 和解重置：销毁非视觉子弹，保留视觉子弹

  **Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/BulletLogic.cs**（371 行） — 子弹发射逻辑
  - `setBulletInformation`:17 — 从 Hero 配置批量写入子弹参数
  - `applySuperParams`:37 — 大招参数覆写（-1 保留原值）
  - `InitData`:78 — 初始化回调、调度 Shoot、5 秒自毁
  - `Shoot`:91 — 分发到 ShanxingShoot/StraightShoot/蜜蜂大招
  - `ShanxingShoot`:109 — 扇形发射协程
  - `StraightShoot`:139 — 直线发射协程（单发/多排/连续）
  - `ParadolaShoot`:251 — 后处理：Layer/物理/BulletBehavior 标志位/CallBack
  - `SetBehaviorFlags`:297 — 按英雄名+isSuper 写入 shell.behavior 枚举

  **Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs**（530 行） — 单颗子弹实体
  - `InitData`:102 — 速度/寿命/回调、视觉子弹禁用碰撞体+isKinematic
  - `OnUpdateLogic`:169 — 逻辑帧：视觉3秒超时Die、非视觉health到期Die、推进Net_pos
  - `FixedUpdate`:148 — 渲染同步 + 拖尾特效计时
  - `OnTriggerEnter`:196 — 碰撞分发：按 tag 路由到 OnTrigger_*
  - `OnTrigger_VisualOnly`:236 — 视觉子弹碰撞：跳过发射者/队友
  - `ShowTrailer`:460 — 按 trailerType 枚举生成拖尾粒子
  - `Die`:515 — 防重入销毁

  ### 5.3 输入与命令

  **Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs**（231 行） — 输入摇杆
  - `OnJoystickMove`:111 — 摇杆移动：瞄准线 + 滞回死区 + CommandManger
  - `JoystickMoveEnd`:47 — 松手触发攻击或重置移动
  - `Start`:87 — 初始化瞄准线、isNotDie=true

  **Assets/Scripts/Manger/CommandManger.cs**（105 行） — 命令聚合
  - `AddCommad_Attack`:67 — AttackCommad 入队
  - `AddCommad_Move`:75 — 覆写最新移动值
  - `Execute`:87 — 写移动 + 消费离散命令 + FlushPendingAttacksToOperation

  ### 5.4 玩家与摄像机

  **Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs**（219 行） — 玩家管理
  - `InitData`:32 — 出生点、英雄实例、CreatePlayers 协程
  - `OnLogicUpdate(PlayerOperation)`:139 — 解析移动/攻击方向 + 推进逻辑位置
  - `ClearMissingPlayerMovement`:100 — 权威帧缺失操作玩家清零移动

  **Assets/HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs**（119 行） — 渲染层追赶
  - `Update`:55 — 本地 MoveTowards / 远端 Lerp + 超阈值传送
  - `FixedUpdate`:106 — Animator 写入 moveMagnitude

  **Assets/Scripts/Server/Manger/Battle/HYLDCameraManger.cs** — 摄像机跟随（LateUpdate SmoothDamp）

  ### 5.5 网络与配置

  **Assets/Scripts/Server/Manger/UDPSocketManger.cs**（140 行） — UDP 收发
  - `InitSocket`:43 — 创建 Socket、连接、启动接收线程
  - `ReceiveLoop`:70 — 后台收包 → Protobuf 反序列化 → ConcurrentQueue
  - `DrainAndDispatch`:101 — 主线程排空队列、逐包 Handle 分发
  - `SendOperation`:114 — 构建战斗操作包并发送

  **Assets/Scripts/Server/ConstValue.cs**（37 行） — 网络配置常量（纯静态字段）

  **Assets/Scripts/Server/SocketProto.cs** — 协议定义（生成代码）

  **Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs** — 全局静态数据、PlayerInformation、英雄配置

  ## 6. 状态所有权

  - **服务端权威**：最终帧状态、最终战斗结果、**玩家 HP（playerHp）**、**死亡状态（playerIsDead）**、**击杀判定**、**GameOver 触发与胜负**。
  - **客户端临时状态**：本地预测历史、权威位置快照（authoritySnapshotHistory）、回滚后表现层平滑状态。
  - 高风险共享状态：sync_frameID、predicted_frameID、selfOperation、BattleID:PlayerIndex 映射。
  - **不参与和解快照的字段**：isNotDie（由 TouchLogic.Start() 设 true / PlayerLogic.playerDieLogic() 设 false 在主线程管理，和解快照不捕获/不恢复此字段）。

  ## 7. 当前参数值

  

  

  

  

  

  

  

  | 参数                                   | 值                 | 文件          |
  | -------------------------------------- | ------------------ | ------------- |
  | frameTime                              | 0.016f (16ms, 约 60fps) | ConstValue.cs |
  | PredictionHistoryWindowSize            | 20                 | ConstValue.cs |
  | EnablePredictionReconciliationPipeline | true               | ConstValue.cs |
  | ReconciliationPositionThreshold        | 0.6f (仅监控)      | ConstValue.cs |
  | canPlayerRestoreHealthTime             | 2 秒               | ConstValue.cs |
  | MaxClientAttackAge                     | 10                 | ConstValue.cs |
  | MaxAcceptableAttackDelay（服务端）       | 6                  | Battle.cs:79  |
  | inputBufferSize（两端）                  | 2                  | ConstValue.cs / Battle.cs:41 |
  | adjustRate                              | 0.05f              | ConstValue.cs |
  | minSpeedFactor                          | 0.85f              | ConstValue.cs |
  | maxSpeedFactor                          | 1.15f              | ConstValue.cs |
  | smoothRate                              | 5.0f               | ConstValue.cs |
  | maxCatchupPerUpdate                     | 3                  | ConstValue.cs |
  | pingIntervalMs                          | 200f               | ConstValue.cs |
  | BattleNetSim.DropRate（服务端压测档）     | 0.10f              | Server/Server/Battle.cs |
  | BattleNetSim.DelayRange（服务端压测档）   | 80~120ms           | Server/Server/Battle.cs |

  ## 8. 动态追帧系统（dynamic-tick-adjustment）

  ### 8.1 概述

  客户端通过 UDP Ping/Pong 测量 RTT，动态调整 tick 频率使本地 predicted_frameID 保持在 `targetFrame = sync_frameID + ceil(rttFrames/2) + inputBufferSize` 附近。服务端通过 Input Buffer 存储客户端超前发来的输入，按帧号消费。

  ### 8.2 UDP Ping/Pong RTT 测量

  - **客户端**：战斗中每 200ms 通过 `UDPSocketManger` 发送 Ping 包（`ActionCode.Ping`，`timestamp = 当前毫秒时间戳`）
  - **服务端**：`ClientUdp.cs` 将战斗期 UDP 包统一送入 `LZJUDP` 的 battle-scoped NetSim 入口。`Ping` 作为上行数据包先参与统一延迟/丢包判定，随后服务端构造 `Pong`（`ActionCode.Pong`，`timestamp` 原样回传）并按同一套 NetSim 参数作为下行数据包发送；`BattleStart` / `BattlePushDowmGameOver` 也进入同一框架，但采用更保守的控制包策略。`ClientUdp.cs` 的延迟执行已从每包 `ThreadPool + Sleep` 改为统一调度线程 + 延迟队列，避免调度排队抖动污染 RTT。这样 RTT、上行操作和权威帧共享同一口径的战斗期网络模型；非战斗 UDP 仍绕过该模拟
  - **EWMA 平滑**：`smoothedRTT = (1-α)*smoothedRTT + α*rttSample`（α=0.125），`rttVariance = (1-β)*rttVariance + β*|rttSample-smoothedRTT|`（β=0.25）。异常样本（<=0 或 >2000ms）丢弃
  - **Proto 扩展**：`MainPack` 新增 `int64 timestamp = 14`

  ### 8.3 目标帧号与 Tick 调节

  - **目标帧号**：`CalcTargetFrame()` — `targetFrame = sync_frameID + ceil(smoothedRTT / frameTimeMs / 2) + inputBufferSize`。RTT 未初始化时退化为 `sync_frameID + inputBufferSize`
  - **速度因子**：`frameDiff = targetFrame - predicted_frameID`，`targetSpeedFactor = Clamp(1 + frameDiff * adjustRate, minSpeedFactor, maxSpeedFactor)`
  - **Lerp 平滑**：`actualSpeedFactor = Lerp(actual, target, smoothRate * deltaTime)`，防止帧间跳变
  - **Tick 间隔**：`currentTickInterval = 0.016f / actualSpeedFactor`
  - **严重超前暂停**：`frameDiff < -5` 时跳过本帧累加器循环（不发 tick）
  - **累加器驱动**：`Update()` 中 `_tickAccumulator += Time.deltaTime`，`while(accumulator >= interval)` 调用 `BattleTick()`，每帧最多 3 次

  ### 8.4 服务端 Input Buffer

  - **数据结构**（Battle.cs:49-53）：`Dictionary<int, List<BufferedMoveInput>> dic_movementInputBuffer`，并配套 `dic_lastValidMove`、`dic_consecutiveMissedFrames`
  - **窗口参数**：`InputBufferSize=8`、`InputFutureLeadTolerance=6`
  - **入队**（`UpdatePlayerOperation`，BattleController.Network.cs:103）：
    - 先按客户端 ack 清理 `SyncFrameId <= clientAckedFrame - 2` 的旧输入
    - 同一 `SyncFrameId` 输入覆盖更新，不同输入插入后按 `SyncFrameId` 排序
    - 总量超过 8 条时，只保留最近 8 条移动输入
  - **消费**（`CollectAndBroadcastCurrentFrame`，Battle.cs:393）：服务端每帧从后往前找满足 `candidate.SyncFrameId <= frameid + 6` 的**最新可用输入**，命中后将该输入及其之前更老的输入一起出队
  - **缺帧补偿**：当前帧没有合适新输入时，服务端沿用 `lastValidMove` 做短时移动惯性；攻击不走该窗口，仍独立并入 `dic_pendingAttacks`
  - **语义**：服务端对移动输入采取“最新状态优先”的滑动窗口消费，不再要求 `clientFrameId` 与服务端 `frameid` 严格一一对应
  - **清理**：`HandleBattleEnd` 释放 movement buffer、lastValidMove、miss 计数

  ### 8.5 上报帧号适配

  - **客户端**：`uploadOperationId = nextFrame`（= `predicted_frameID + 1`，动态追帧下可能远大于服务端 frameid）
  - **服务端 Ack 钳位**（`UpdatePlayerOperation`）：`incomingAckFrameId = syncFrameId - 1`，`ackUpperBound = max(0, frameid - 1)`，超出则钳位到 upper bound。确保 Ack 不超过服务端已处理帧

  ### 8.6 端到端数据流

  ```
  客户端                                          服务端
  ──────                                          ──────
  CalcTargetFrame → targetFrame=sync+RTT/2+buf
  AdjustTickInterval → currentTickInterval
  BattleTick() × N 次 (累加器驱动)
    ├─ 推进 predicted_frameID
    ├─ uploadOperationId = predicted_frameID+1
    └─ UDP 发送操作 ─────────────────────────────→ UpdatePlayerOperation
                                                    ├─ inputBuffer[clientFrame%2] = op
                                                    ├─ Ack 钳位（不超 frameid-1）
                                                    └─ 等待 frameid 推进到 clientFrame
                                                  CollectAndBroadcast(frameid=F)
                                                    ├─ 从 inputBuffer[F%2] 取操作
                                                    ├─ 缺帧 → lastConsumedMove 补偿
                                                    └─ 广播权威帧
  DrainAndDispatch ←──────────────────────────── 权威帧下行
    └─ 更新 sync_frameID
  Ping (每200ms) ────────────────────────────────→ Pong (经 NetSim 丢包/延迟)
  ←──────────────────────────────────────────────  Pong (timestamp 原样回传)
  EWMA → smoothedRTT → CalcTargetFrame
  ```

  ## 9. 已修复问题历史

  1. **渲染层 Lerp 追赶速度不足** -> 本地玩家 MoveTowards 匀速追赶
  2. **inputMatched 导致连续 full-rollback** -> 改为基于批次内输入对比
  3. **skipSelf 条件过严导致双重推进** -> 和解内部全量应用，skipSelf 移除
  4. **和解视觉偏移累积** -> maxSmoothableOffset=1.5f 限制
  5. **UDP 子线程调用 Unity 主线程 API** -> 改用纯数据字段，cachedMainThreadTime
  6. **isNotDie 被和解快照覆写** -> 快照固定写 true，不恢复此字段
  7. **和解路径执行完整游戏逻辑** -> isReconciliation 参数跳过子弹和详细日志
  8. **ResetForReconciliation 子线程直接 Destroy** -> AddAction 延迟到主线程
  9. **渲染层本地玩家 15fps 步进** -> Update 中直接读摇杆输入驱动 Transform
  10. **视觉子弹碰到发射者自己立刻 Die** -> isVisualOnly 分支增加队友过滤
  11. **视觉子弹碰到 Gun/Capsule/其他子弹** -> 禁用所有 Collider + isKinematic
  12. **和解频繁清除视觉子弹** -> ResetForReconciliation 保留 isVisualOnly=true 的 shell
  13. **死亡后自动复活** -> playerDieLogic 去掉 Invoke("playerRevive") 和自动满血，死亡即终局等服务端 GameOver
  14. **GameOver 胜负未传递给 UI** -> HandleMessage 解析 pack.Str 为 winnerTeamId，设置 玩家输了吗 标志
  15. **拖尾特效高频 Instantiate 导致 Editor 卡死** -> shell.FixedUpdate 中 ShowTrailer 间隔从 0.04s 调整为 0.15s，减少 ~75% 的 GameObject 创建压力
  16. **服务端子弹方向镜像** -> SpawnServerBullets 对 baseX 增加取反和 teamSign 翻转，与客户端 `dir.x *= -1 * sign` 一致
  17. **服务端 HP 临时降低** -> HeroConfig._hpConfig 全英雄 HP 降至约 1/5，减少测试期子弹累积量避免 Editor 崩溃（后续对象池优化完成后恢复）
  18. **客户端收到 GameOver 后卡死（死亡不触发）** -> ApplyHitEvents 死亡判定改为以服务端 IsKill 为权威，强制 playerBloodValue=-1 确保 PlayerLogic.playerDieLogic() 被触发
  19. **重发攻击 ClientFrameId 被刷新导致 delay<0** -> PendingAttack 入队时锁定 ClientFrameId，FlushPendingAttacksToOperation 不再用 predicted_frameID 覆写；服务端增加 clamp 兜底（clientFrameId > frameid 时钳位）
  20. **确认丢包时攻击无限重发浪费带宽** -> FlushPendingAttacksToOperation 增加客户端超时清理（帧龄 > MaxClientAttackAge=10 的攻击移除）；服务端 MaxAcceptableAttackDelay=6 拒绝过期攻击
  21. **客户端-服务端 HP 不同步** -> ApplyAuthoritativeHpAndDeath 从服务端 PlayerStates.Hp 覆写客户端 HP，首帧初始化 maxHp；ApplyHitEvents 降级为纯表现（不扣血），死亡由 IsDead 权威驱动
  22. **UDP 乱序导致 HP 回弹** -> ApplyAuthoritativeHpAndDeath 增加 `_lastAuthHpFrameId` 帧序保护，跳过帧号 <= 已消费最高帧号的旧批次，防止乱序到达的旧 HP 值覆写新 HP
  23. **Pong 绕过 NetSim 导致 RTT 失真** -> `LZJUDP` 新增 `SimDropRate/SimDelayMinMs/SimDelayMaxMs` 公共静态字段，Pong 发送经过与战斗帧相同的丢包/延迟模拟；参数由 `BattleController.BeginBattle` 写入、`HandleBattleEnd` 清零。修复后 RTT 从 7ms 修正为 ~117ms（50% 丢包 + 60-150ms 延迟），攻击确认率从 78% 提升至 100%

  ## 10. 协作约定

  - 与服务端协作时，先看：Assets/Docs/ForServer.md。
  - 客户端分析服务端协议时参考：D:/unity/hyld-master/hyld-master/Server/Docs/ForClient.md。
  - 若改动涉及两端协议/同步行为，务必记录到：D:/unity/hyld-master/hyld-master/BothSide.md。

  ## 11. 代码变动后的文档检查（必做）

  - 每次完成代码改动后，必须检查是否需要同步更新以下文档：
    - Assets/CLAUDE.md（项目总览、主链路、关键参数）
    - Assets/Docs/ForServer.md（服务端协作链路、联调检查项）
    - D:/unity/hyld-master/hyld-master/BothSide.md（涉及两端交互/协议/同步语义时）
  - 若改动影响“输入采集、命令聚合、发送节拍、权威帧消费、和解策略、子弹系统、GameOver 边界”，默认需要至少更新 Assets/CLAUDE.md 或 Assets/Docs/ForServer.md。
