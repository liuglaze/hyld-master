## Why

当前客户端 `targetFrame` 仍按 `sync_frameID + RTT/2 + buffer` 估算，而服务端移动输入消费仍允许在缺失目标帧时直接跳吃更大的合法帧。这会把“客户端声明某条输入应在哪个未来权威帧生效”的语义打散：网络抖动下，输入可能被过早消费或跳帧消费，表现为节奏错位、补偿不稳定，以及缺帧时序不够可预测。

现在需要把这条链路改成更一致的“目标帧投递 + 当前帧严格消费”模型：客户端用完整 RTT、抖动和安全余量估算输入应命中的未来帧；服务端仅在当前权威帧消费对应输入，缺帧时沿用上一帧有效移动，而不是提前吃未来帧。

## What Changes

- 调整客户端 `targetFrame` 公式，从基于半 RTT 的估算改为基于完整 RTT、抖动余量和少量安全帧的未来目标帧估算。
- 明确客户端上行 `OperationID` 的语义为“该次输入声明要命中的目标权威帧”。
- 修改服务端移动输入消费规则：每个服务端帧仅消费 `SyncFrameId == 当前 frameid` 的移动输入，不再在缺帧时跳吃更大的合法帧。
- 保留缺帧兜底，但限定为沿用上一条有效移动输入，不推进移动消费进度，也不提前消费未来帧。
- 更新验证日志与联调文档，使客户端目标帧计算、服务端严格帧消费、缺帧回退行为可观测。

## Capabilities

### New Capabilities
- `strict-target-frame-consumption`: 定义客户端按完整 RTT 估算目标帧，以及服务端仅在当前权威帧消费对应移动输入的端到端语义。

### Modified Capabilities
- `ordered-move-input-buffer`: 现有服务端移动输入缓冲规则将从“缺帧时可跳吃最小更大合法帧”调整为“仅消费当前权威帧对应输入，缺帧时沿用上一有效移动”。

## Impact

- 客户端：`Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs` 的目标帧估算与 tick 调节语义。
- 服务端：`Server/Server/Battle.cs`、`Server/Server/BattleController.Network.cs` 的移动输入入缓冲、消费与缺帧回退逻辑。
- OpenSpec：新增/修改输入缓冲与目标帧相关规格。
- 联调文档：`BothSide.md` 及相关 CLAUDE/协作文档需要同步更新跨端时序语义。