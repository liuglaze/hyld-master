## 1. ApplyHitEvents 降级为纯表现

- [x] 1.1 在 `BattleManger.cs` 的 `ApplyHitEvents` 中移除 `playerBloodValue -= evt.Damage` 扣血逻辑，保留受击动画触发 `SetTrigger("Hit")` 和去重机制 `_appliedHitEventKeys`
- [x] 1.2 移除 `ApplyHitEvents` 中基于 `playerBloodValue <= 0` 的死亡判定分支（死亡改由权威 IsDead 驱动）
- [x] 1.3 保留 `ApplyHitEvents` 中 `evt.IsKill` 的死亡触发作为向后兼容兜底（IsDead 未到达前的安全网），后续可移除
- [x] 1.4 在 `ApplyHitEvents` 中新增 `HashSet<int> hitAnimatedPlayers` 记录本批次已触发受击动画的玩家索引，供模块 3 兜底逻辑使用
- [x] 1.5 节点验证: ApplyHitEvents 降级正确性
  - **Log**: 在 `ApplyHitEvents` 每次处理 HitEvent 时输出 `Debug.Log($"[AHS-1] HitEvent anim-only: victim={victimIndex} attackId={evt.AttackId} damage={evt.Damage} bloodBefore={player.playerBloodValue} bloodAfter={player.playerBloodValue}")`，确认 bloodBefore == bloodAfter（HP 不变）
  - **Log**: 去重跳过时输出 `Debug.Log($"[AHS-1] HitEvent dedup skip: key={key}")`
  - **验证方法**: 启动联网战斗，攻击对方玩家，在 Unity Console 过滤 `[AHS-1]`：
    1. 确认每条 HitEvent 日志中 `bloodBefore == bloodAfter`（扣血逻辑已移除）
    2. 确认受击动画仍正常播放（观察被攻击角色的 Hit 动画触发）
    3. 确认 `hitAnimatedPlayers` 被正确记录（由模块 3 验证节点覆盖）

## 2. 权威 HP/IsDead 消费

- [x] 2.1 在 `OnLogicUpdate_sync_FrameIdCheck` 中 `ApplyAuthoritativePositions` 附近新增 `ApplyAuthoritativeHpAndDeath` 方法，遍历批次最后一帧的 `PlayerStates`，用 `state.Hp` 覆写对应玩家的 `playerBloodValue`
- [x] 2.2 在 `ApplyAuthoritativeHpAndDeath` 中处理 `state.IsDead == true`：若玩家 `isNotDie == true`，强制 `playerBloodValue = -1`，调用 `playerDieLogic()`
- [x] 2.3 记录覆写前的旧 HP 值，用于 HP 差值兜底动画判断（传递给模块 3）
- [x] 2.4 节点验证: 权威 HP 覆写与死亡判定
  - **Log**: HP 覆写时输出 `Debug.Log($"[AHS-2] AuthHP: player={playerIndex} oldHp={oldHp} newHp={state.Hp} isDead={state.IsDead} frame={frameId}")`
  - **Log**: 死亡触发时输出 `Debug.Log($"[AHS-2] AuthDeath: player={playerIndex} isNotDie_was=true -> playerDieLogic() frame={frameId}")`
  - **验证方法**: 启动联网战斗，在 Unity Console 过滤 `[AHS-2]`：
    1. 确认每次权威帧到达都有 HP 覆写日志，`newHp` 与服务端下发值一致
    2. 攻击对方至死亡，确认 `AuthDeath` 日志出现，且 `playerDieLogic()` 只触发一次（后续帧 `isNotDie` 已为 false，不重复触发）
    3. 对比 `[AHS-1]` 日志，确认 HP 变化只来自 `[AHS-2]`（权威覆写），不来自 `[AHS-1]`（HitEvent）

## 3. HP 差值兜底受击动画

- [x] 3.1 在 `ApplyAuthoritativeHpAndDeath` 中，若某玩家 HP 下降（`newHp < oldHp`）且该玩家不在 `hitAnimatedPlayers` 中，补播通用受击动画 `SetTrigger("Hit")`
- [x] 3.2 节点验证: 兜底受击动画触发逻辑
  - **Log**: 兜底动画触发时输出 `Debug.Log($"[AHS-3] FallbackHitAnim: player={playerIndex} hpDrop={oldHp - newHp} noHitEvent=true frame={frameId}")`
  - **Log**: HP 下降但已有 HitEvent 动画时输出 `Debug.Log($"[AHS-3] FallbackHitAnim SKIP: player={playerIndex} hpDrop={oldHp - newHp} hasHitEvent=true frame={frameId}")`
  - **验证方法**: 启动联网战斗，在 Unity Console 过滤 `[AHS-3]`：
    1. 正常命中场景：确认大多数情况下出现 `SKIP` 日志（HitEvent 正常到达，无需兜底）
    2. 模拟丢包验证（可选）：若有网络模拟工具，丢弃部分 HitEvent 后确认 `FallbackHitAnim` 日志出现，且角色仍播放受击动画
    3. HP 未下降时：确认无 `[AHS-3]` 日志输出

## 4. HP 初始值统一

- [x] 4.1 在 `HYLDStaticValue.cs` 中定位硬编码 HP 赋值（`playerBloodValue` 初始值），改为临时默认值（如 100），标记注释等待服务端覆写
- [x] 4.2 在 `ApplyAuthoritativeHpAndDeath` 首次执行时，用 `PlayerStates.Hp` 同时设置 HP 上限（用于血条比例计算），存储为 `maxHp` 字段
- [x] 4.3 确保血条 UI 使用 `playerBloodValue / maxHp` 比例显示，而非硬编码上限
- [x] 4.4 节点验证: HP 初始化与血条比例
  - **Log**: maxHp 首次初始化时输出 `Debug.Log($"[AHS-4] MaxHpInit: player={playerIndex} maxHp={state.Hp} oldDefault={oldBloodValue} frame={frameId}")`
  - **Log**: 血条比例计算时输出（仅首次或变化时）`Debug.Log($"[AHS-4] HpBar: player={playerIndex} blood={playerBloodValue} maxHp={maxHp} ratio={playerBloodValue/(float)maxHp:F2}")`
  - **验证方法**: 启动联网战斗，在 Unity Console 过滤 `[AHS-4]`：
    1. 确认 `MaxHpInit` 日志出现于首批权威帧到达时，`maxHp` 值与服务端 `HeroConfig.GetHp()` 一致
    2. 确认战斗开始时血条显示为满（ratio ≈ 1.00）
    3. 被攻击后血条比例正确下降（ratio 与 `[AHS-2]` 中的 newHp/maxHp 一致）
    4. 确认 `HYLDStaticValue.cs` 中硬编码 HP 不再参与实际赋值

## 5. 清理与调用顺序

- [x] 5.1 确认 `OnLogicUpdate_sync_FrameIdCheck` 中调用顺序：先 `ApplyHitEvents`（触发动画+记录 hitAnimatedPlayers）→ 再 `ApplyAuthoritativeHpAndDeath`（覆写 HP + 兜底动画 + 死亡判定）
- [x] 5.2 在 `ClearPredictionRuntimeState` 中清理新增的运行时状态（`hitAnimatedPlayers`、`maxHp` 等）
- [x] 5.3 节点验证: 调用顺序与状态清理
  - **Log**: 在 `OnLogicUpdate_sync_FrameIdCheck` 中两个方法调用前后各加时序标记 `Debug.Log($"[AHS-5] Seq: ApplyHitEvents START frame={frameId}")` / `END` 和 `Debug.Log($"[AHS-5] Seq: ApplyAuthHpDeath START frame={frameId}")` / `END`
  - **Log**: 在 `ClearPredictionRuntimeState` 中清理时输出 `Debug.Log($"[AHS-5] Cleanup: hitAnimatedPlayers.Clear() maxHp reset")`
  - **验证方法**: 在 Unity Console 过滤 `[AHS-5]`：
    1. 确认每个权威帧批次中，`ApplyHitEvents START/END` 在 `ApplyAuthHpDeath START/END` 之前
    2. 战斗结束后确认 `Cleanup` 日志出现
    3. 新一局战斗开始后确认状态从零开始（`[AHS-4] MaxHpInit` 再次出现）

## 6. 文档更新

- [x] 6.1 更新 `Assets/CLAUDE.md` 中 §4.2 HitEvent 消费描述：标注 ApplyHitEvents 不再扣血，HP 由权威帧覆写
- [x] 6.2 更新 `Assets/CLAUDE.md` 新增 §4.2.2 描述权威 HP/IsDead 消费链路
- [x] 6.3 更新 `Assets/Docs/ForServer.md` 中客户端 HP 消费说明（如有相关章节）
- [x] 6.4 节点验证: 文档一致性
  - **验证方法**: 人工检查以下内容：
    1. `Assets/CLAUDE.md` §4.2 不再描述 `playerBloodValue -= evt.Damage`
    2. `Assets/CLAUDE.md` 新增段落描述 `ApplyAuthoritativeHpAndDeath` 方法的职责和调用位置
    3. `Assets/CLAUDE.md` §5 关键文件索引无需新增文件（改动在现有文件内）
    4. HP 相关参数表（§7）中移除硬编码 HP 条目或标注已弃用
