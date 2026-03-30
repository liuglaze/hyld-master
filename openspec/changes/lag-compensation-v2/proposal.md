## Why

当前服务端伤害判定（V1）使用攻击到达服务端时的**当前帧位置**做碰撞检测，未补偿网络延迟。在高延迟场景下（50~150ms），玩家瞄准的位置与服务端碰撞检测的位置存在偏差，导致"明明打中了却没命中"的体验问题。同时，远端玩家的视觉子弹从权威帧到达时的位置生成，没有补偿已经过的飞行时间，造成子弹视觉延迟感。

## What Changes

- 服务端启用历史位置回溯碰撞检测：子弹生成时根据 `clientFrameId` 查找 `positionHistory` 中攻击发起时刻的目标位置，在历史位置上做碰撞判定
- Proto 扩展：`AttackOperation` 新增 `spawn_pos_x/y/z` 字段，服务端将攻击者历史位置写入下行包
- 客户端半空子弹生成：权威帧子弹（路径 B）使用服务端下发的 `spawn_pos` 生成视觉子弹，并根据帧差瞬间推进已飞行距离
- 本地预测子弹（路径 A）不受影响，保持零延迟生成

## Capabilities

### New Capabilities
- `lag-compensation`: 服务端历史位置回溯碰撞检测 + proto 字段扩展 + 客户端半空子弹视觉补偿

### Modified Capabilities

## Impact

- **服务端**: `Battle.cs` — `SpawnServerBullets` 启用 `useHistoryLookup=true`，`SpawnBulletsFromOperations` 写入 spawn 位置到 AttackOperation
- **Proto**: `SocketProto.proto` — `AttackOperation` 消息新增 3 个 float 字段（spawn_pos_x/y/z）
- **客户端**: `BattleManger.cs` — `SpawnVisualBullet`（路径 B）读取 spawn 位置并计算半空推进距离；`shell.cs` 或 `BulletLogic.cs` 支持初始位置偏移
- **文档**: `Assets/CLAUDE.md`、`Assets/Docs/ForServer.md` 需同步更新延迟补偿相关描述
