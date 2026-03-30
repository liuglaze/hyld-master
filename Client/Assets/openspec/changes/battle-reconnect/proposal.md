## Why

当前任何一方 TCP 断线（网络波动、进程崩溃、切后台被系统杀死），服务端立即终止整场战斗并判定为 GameOver。在实际对战中，短暂的网络中断（几秒级）不应直接结束比赛。需要实现断线容忍 + 自动重连机制，让断线玩家在 30 秒内重连回原战斗继续游戏。

## What Changes

### 服务端

- **HandlePlayerDisconnect 改为"挂起"模式**：断线不再立即结束战斗，而是标记玩家断线 + 启动 30s 超时计时器。BattleLoop 继续运行，断线玩家站桩（Input Buffer 缺帧补偿：不移动、不攻击）。对方可以正常攻击断线玩家。
- **30s 超时判负**：超时未重连 → 断线方所在队伍判负 → 正常 HandleBattleEnd 流程。
- **重连接入**：Login → FindPlayerInfo 时检测 `_uidToBattleIds`，若 uid 在活跃战斗中 → 响应包附带完整 BattleInfo（同 StartEnterBattle 数据），客户端识别后直接进入战斗场景。
- **BattleReady 扩展**：`isAllReady` 后仍接受重连玩家的 BattleReady，重新注册 UDP 端点，重置 `dic_playerAckedFrameId` 为 0 触发全量帧重发。
- **ClearScene 单人回复**：重连玩家单独走 ClearScene 加载流程，服务端对其单独回复 AllClearSenceReady（不阻塞在线对手）。
- **断线/重连通知**：向仍在线的对手发送断线通知和重连通知（新 ActionCode）。

### 客户端

- **断线不回主界面**：战斗中断线时进入"重连等待"状态（显示重连 UI），不切场景、不清战斗数据。
- **TCP 自动重连**：后台循环尝试重连 TCP（指数退避，30s 内持续尝试），成功后自动走 Login → FindPlayerInfo 流程。
- **战斗恢复入口**：FindPlayerInfo 检测到 "InBattle" 标记后，跳过匹配流程，直接 InitBattleInfo → ClearScene → 战斗场景 → BattleReady。
- **追帧模式**：重连后服务端补发全量帧历史，客户端快速追赶时跳过视觉表现（不生成子弹、不播受击动画），只应用权威位置/HP/IsDead。
- **重连 UI**：显示"网络断开，正在重连..."。收到对方断线通知时显示"对手断线，等待重连..."。

### 心跳优化

- **BREAKING**：TCP PingPong 间隔从 180s 缩短到 5s，超时阈值从 720s 缩短到 20s（4 倍间隔）。两端同步修改。
- 修复客户端 `ReceiveLoopAsync` 异常路径不触发 `CloseClient` 的 bug。

### 协议

- 新增 `ActionCode.PlayerDisconnected = 43`：服务端→在线客户端，通知对方掉线。
- 新增 `ActionCode.PlayerReconnected = 44`：服务端→在线客户端，通知对方已重连。
- `FindPlayerInfo` 响应扩展：当玩家在活跃战斗中时，`Str` 标记 "InBattle"，`BattleInfo` 填充完整战斗恢复数据。
- `UDPSocketManger` 新增 `CloseSocket()` 方法，重连时销毁旧 socket。

## Capabilities

### New Capabilities

- `battle-reconnect-server`: 服务端断线容忍机制——挂起模式、30s超时判负、重连接入（BattleReady扩展+帧重发）、断线/重连通知
- `battle-reconnect-client`: 客户端自动重连——断线状态管理、TCP重连循环、战斗恢复入口、追帧模式（跳过视觉表现）、重连UI
- `heartbeat-optimization`: TCP心跳间隔缩短（180s→5s）、超时检测优化、异常路径修复

### Modified Capabilities

（无现有 specs 需要修改）

## Impact

### 服务端文件

| 文件 | 改动范围 |
|---|---|
| `Server/Battle.cs` | HandlePlayerDisconnect、BattleLoop 超时、Handle(BattleReady)、BattleManage 查询接口 |
| `Controller/Controllers.cs` | FindPlayerInfo 附带恢复数据、ClearSenceController 单人回复 |
| `Server/Client.cs` | Close() 条件化（断线不立即触发战斗结束由 BattleManage 统一处理） |
| `Tool/PingPongManger.cs` | 心跳间隔常量 |

### 客户端文件

| 文件 | 改动范围 |
|---|---|
| `HYLDManger.cs` | CloseClient 改造、TCP 重连循环 |
| `TCPSocketManger.cs` | Reconnect 方法、异常路径修复 |
| `PingPongManger.cs` | 心跳间隔缩短 |
| `BattleManger.cs` | 追帧模式（跳过视觉） |
| `UDPSocketManger.cs` | CloseSocket 方法 |
| UI 面板（新增或复用） | 断线/重连提示 |

### 协议

| 变更 | 说明 |
|---|---|
| `ActionCode` 枚举 | 新增 43/44 |
| `FindPlayerInfo` 响应语义 | Str="InBattle" + BattleInfo 复用 |
| proto 文件 | 两端同步重新生成 |

### 关键依赖

- `SendUnsyncedFrames` 已支持按玩家单独发送、按 ack 差量重发——重连补帧零改动
- `dic_historyFrames` 保存全量帧历史——30s 内约 1800 帧 ≈ 180KB，可全量重发
- `PackPlayerStates` 每帧包含位置/HP/IsDead——追帧后状态自动恢复
- CSP 模式权威位置校正——客户端无需回滚历史，只需追到最新权威帧

### 风险

- 帧历史全量重发可能瞬间产生 130+ 个 UDP 包，需要评估是否需要节流
- 追帧期间大量旧帧的 HitEvent 不应触发视觉效果，需要追帧模式跳过
- ClearScene 单人回复需要绕开现有多人同步逻辑，可能需要特殊标记
