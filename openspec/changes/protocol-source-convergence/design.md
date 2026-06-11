## Context

当前仓库中的协议定义与生成产物分布在多个目录：`Client/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto`，以及多份 `SocketProto.cs` 生成文件。现有 battle 协议已经持续演进，例如 `client_acked_frame`、`player_states`、`spawn_pos_x/y/z` 等字段已被当前运行时代码使用，但并非所有 proto 源与生成产物都保持一致。

同时，`ProtobufAndNotepad/Protobuf/build.bat` 已经隐含了一条当前最接近真实运行方式的生成链路：以 `ProtobufAndNotepad/Protobuf/SocketProto.proto` 为输入，生成到工具目录、客户端运行目录和服务端运行目录。这说明“唯一源”事实上已经隐约存在，但尚未被正式确权，也没有明确约束历史 proto 与旧生成目录的地位。

本 change 是治理型变更，目标是先收敛协议来源与生成链路，为后续 battle 协议语义整理提供稳定基线，而不是直接修改 battle 消息结构。

## Goals / Non-Goals

**Goals:**
- 确定唯一受支持的 proto 源文件，并将客户端/服务端运行时代码的生成来源固定下来。
- 明确哪些 `SocketProto.cs` 属于运行时必需产物，哪些属于工具输出、历史备份或不再参与主维护路径的残留。
- 定义历史 proto/旧生成产物的治理策略，避免后续维护时误修改或误引用。
- 建立协议变更时的同步约束与检查点，确保客户端、服务端与文档对协议来源描述一致。

**Non-Goals:**
- 不重构 `MainPack`、`BattleInfo`、`operationID`、`AttackOperation` 等业务协议语义。
- 不在本 change 中优化 battle 包体、字段冗余或上下行消息分层。
- 不引入新的网络协议格式，也不改变当前客户端/服务端的线协议兼容性。

## Decisions

### 1. 将 `ProtobufAndNotepad/Protobuf/SocketProto.proto` 作为唯一 proto 源
- **决策**：将 `ProtobufAndNotepad/Protobuf/SocketProto.proto` 定义为唯一受支持的协议源。
- **理由**：当前运行时代码所需字段（如 `client_acked_frame`、`spawn_pos_x/y/z`）已存在于该文件，且 `build.bat` 也以该文件为输入生成客户端和服务端代码。
- **备选方案**：
  - 继续允许 `Client/SocketProto.proto` 作为并行维护源：会继续制造分叉，不可接受。
  - 改以 `Client/SocketProto.proto` 为唯一源：需要先补齐当前运行字段，且与现有生成链路不一致，迁移成本更高。

### 2. 运行时代码只认可客户端与服务端各自目录中的 `SocketProto.cs`
- **决策**：运行时权威生成产物限定为 `Client/Assets/Scripts/Server/SocketProto.cs` 与 `Server/Server/SocketProto.cs`。
- **理由**：这两份是当前客户端/服务端实际引用的代码位置，其他生成文件不应再被视为等价运行时产物。
- **备选方案**：
  - 保持工具目录中多份 `SocketProto.cs` 与运行时代码同等地位：会继续模糊“哪份才是要同步更新的目标”。

### 3. 历史 proto 与旧生成目录保留，但退出主维护路径
- **决策**：对于 `Client/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto` 及工具目录下的旧生成产物，采用“保留但明确标记为历史/非权威”的策略，而不是立即删除。
- **理由**：这些文件可能仍承担历史对照、工具链兼容或人工排查用途；直接删除风险高，且超出本 change 的最小治理范围。
- **备选方案**：
  - 立即删除所有历史文件：最干净，但误删仍被依赖的工具产物风险较高。
  - 完全不处理：继续保留混乱现状，无法解决维护歧义。

### 4. 先用文档与检查约束固化治理边界，再考虑后续语义重构
- **决策**：本 change 先通过规范、文档和任务约束固化协议来源治理，不与 battle 协议拆分重构绑定交付。
- **理由**：协议语义重构（如拆 `BattleInfo`、拆 `operationID` 双语义）属于下一阶段问题；如果本次把“源收敛”和“语义重构”混在一起，会放大实施与回归风险。
- **备选方案**：
  - 在同一 change 中同时做源收敛与消息语义重构：范围过大，难以验证和回滚。

## Risks / Trade-offs

- **[历史文件仍保留，可能继续被误读]** → 通过文档、注释或目录约定明确其“非权威”身份，并在任务中加入同步检查。
- **[唯一源切换后，可能存在未纳入 build.bat 的手工生成流程]** → 在实施前先确认客户端与服务端当前实际生成方式，并把生成命令记录为统一流程。
- **[只治理来源、不解决 battle 协议语义问题，短期内结构仍显混乱]** → 明确本 change 只是打基础；后续 battle 协议重构以该治理结果为前置依赖。
- **[工具目录中某些生成文件可能被隐式依赖]** → 在调整前先逐个确认引用与用途，优先做“退出主维护路径”而非激进删除。
