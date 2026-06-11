## Why

当前仓库中存在多份 `SocketProto.proto` 与多份 `SocketProto.cs` 生成产物，且内容已经分叉，导致协议修改时存在“改错源文件、双端生成不一致、历史产物误导维护者”的风险。随着战斗协议继续演进，如果不先收敛协议源与生成链路，后续任何 battle 协议整理都会缺乏稳定基础。

## What Changes

- 明确 battle/通用协议的唯一 proto 源文件，以及客户端、服务端运行时代码应从该源统一生成。
- 建立协议生成链路约束，明确哪些 `SocketProto.cs` 属于运行时产物，哪些目录属于工具输出或历史残留。
- 为历史 proto 与旧生成产物定义治理策略，包括保留、标记废弃、停止引用或移出主维护路径。
- 增加协议源变更时的同步检查要求，确保客户端、服务端与文档对协议来源的描述一致。
- 不在本 change 中直接重构 battle 消息结构，不修改 `BattleInfo`、`operationID`、`AttackOperation` 等业务语义。

## Capabilities

### New Capabilities
- `protocol-source-governance`: 约束唯一 proto 源、生成产物归属、历史协议产物治理与同步检查规则。

### Modified Capabilities
- None.

## Impact

- 协议源文件：`Client/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto`
- 运行时代码：`Client/Assets/Scripts/Server/SocketProto.cs`、`Server/Server/SocketProto.cs`
- 工具/历史产物目录：`ProtobufAndNotepad/Protobuf/**`
- 文档与规范：`BothSide.md`、`Client/Assets/CLAUDE.md`、`Server/CLAUDE.md`、OpenSpec artifacts
