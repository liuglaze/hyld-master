## 1. Proto 扩展

- [x] 1.1 在 `SocketProto.proto` 的 `AttackOperation` 消息中新增 `spawn_pos_x`、`spawn_pos_y`、`spawn_pos_z` 三个 float 字段
- [x] 1.2 重新生成 proto 代码（C# 服务端 + 客户端），确保编译通过

## 2. 服务端延迟补偿 — 追帧模拟

- [x] 2.1 提取碰撞检测逻辑为 `CheckBulletCollision(bullet, playerPositionsSnapshot)` 共享方法，返回 HitEvent 或 null。`TickServerBullets` 和追帧模拟复用此方法
- [x] 2.2 新增 `SimulateBulletCatchUp(ServerBullet bullet, int fromFrame, int toFrame, List<HitEvent> hitEvents)` 方法：从 fromFrame 到 toFrame 逐帧推进子弹，每帧用 `positionHistory[f]` 做碰撞检测。命中时生成 HitEvent 并返回 true（子弹已销毁），未命中返回 false（子弹存活）
- [x] 2.3 `SpawnBulletsFromOperations` 中将 `useHistoryLookup` 从 `false` 改为 `true`，启用历史位置回溯生成子弹
- [x] 2.4 `SpawnBulletsFromOperations` 中生成子弹后：若 `clientFrameId < frameid` 且历史位置存在，调用 `SimulateBulletCatchUp` 追帧模拟；追帧期间命中的子弹不加入 `activeBullets`，未命中的加入 `activeBullets` 继续正常模拟
- [x] 2.5 `SpawnServerBullets` 内部：历史位置查询成功后，将最终使用的 spawnPos 写入 `atk.SpawnPosX/Y/Z` 字段（供下行广播给客户端）
- [x] 2.6 服务端编译通过，添加回溯日志（`[LagComp]` 标签记录回溯帧范围、命中/未命中结果）

## 3. 客户端半空子弹生成

- [x] 3.1 `BattleManger.cs` 路径 B（`OnLogicUpdate_sync_FrameIdCheck` 中的权威帧子弹生成）：读取 AttackOperation 的 `spawn_pos_x/y/z`，非零时传入 `SpawnVisualBullet` 作为生成位置
- [x] 3.2 `SpawnVisualBullet` 增加可选参数支持自定义生成位置（spawn_pos）和已飞帧数（elapsedFrames）
- [x] 3.3 视觉子弹生成时，根据 elapsedFrames 计算推进距离 `advance = elapsedFrames * speed * frameTime`，子弹初始位置沿飞行方向偏移
- [x] 3.4 本地预测子弹路径 A 不传入 spawn_pos / elapsedFrames，确保行为不变
- [x] 3.5 客户端编译通过

## 4. 测试验证

- [ ] 4.1 服务端 + 客户端双端编译通过
- [ ] 4.2 双人战斗测试（50% 丢包 + 50~150ms 动态延迟）：验证攻击命中、HP 扣减、击杀正常
- [ ] 4.3 检查服务端日志：确认 `[LagComp]` 追帧日志输出（回溯帧数、追帧命中/存活）
- [ ] 4.4 视觉验证：远端玩家子弹从历史位置生成并有半空推进效果

## 5. 文档更新

- [x] 5.1 更新 `Assets/CLAUDE.md` 中子弹系统和延迟补偿相关描述
- [x] 5.2 更新 `Assets/Docs/ForServer.md` 中延迟补偿链路描述
