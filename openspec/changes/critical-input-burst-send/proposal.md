## Why

在当前 UDP 战斗链路里，停步与攻击都属于对体感影响明显的关键输入，但它们仍然受单次丢包影响。尤其是停步 zero-move 一旦在关键帧漏到服务端，服务端会继续沿用 `lastValidMove` fallback，客户端随后在权威纠正与重放时表现为“停步后往前弹”，因此需要为关键输入提供更高的首帧送达概率。

## What Changes

- 客户端为关键输入增加同帧 burst-send 机制：当本帧包含停步输入或新攻击输入时，在同一逻辑帧内重复发送 2~3 次相同操作包。
- 保持现有协议字段不变，重复发送仍复用相同的 `OperationID`、移动值与 `AttackId`，不引入新的网络消息类型。
- 服务端明确将关键输入重复包视为幂等输入：
  - 同一 `SyncFrameId` 的移动输入按“最后收到版本覆盖”处理。
  - 同一 `AttackId` 的攻击输入继续按既有去重规则只处理一次。
- 为关键输入 burst-send 增加可验证日志，便于联调确认客户端是否触发重复发送、服务端是否按幂等语义接收。

## Capabilities

### New Capabilities
- `critical-input-burst-send`: 定义客户端对停步与新攻击执行同帧重复发送，以及服务端对这些重复包执行幂等接收与验证日志的行为。

### Modified Capabilities
- `ordered-move-input-buffer`: 补充同一 `SyncFrameId` 重复移动输入的幂等覆盖语义，确保关键停步包重复发送不会破坏现有严格按帧消费窗口。

## Impact

- 客户端发送链路：`Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs`、`Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs`、`Client/Assets/Scripts/Manger/CommandManger.cs`
- 客户端攻击/停步状态判定：`Client/Assets/Scripts/Server/Manger/Battle/BattleData.Attack.cs`、`Client/Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs`
- 服务端接收与去重链路：`Server/Server/BattleController.Network.cs`、`Server/Server/Battle.cs`
- 联调与文档：`BothSide.md`、`Client/Assets/Docs/ForServer.md`、相关 CLAUDE 文档与验证日志
