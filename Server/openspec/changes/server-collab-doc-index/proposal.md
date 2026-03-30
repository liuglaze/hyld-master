## Why

当前 `Server/CLAUDE.md` 偏内部开发说明，缺少对客户端/外部协作者的快速索引与联调入口，导致跨端协作成本高、问题定位慢。需要将其升级为“对外协作入口文档”，并同步约束 OpenSpec 产物，确保后续改动可被客户端稳定消费。

## What Changes

- 重构 `Server/CLAUDE.md` 的信息架构，前置“60 秒上手、场景索引、跨端联调入口”。
- 新增“按协作场景定位文件/协议/观测点”的索引表，覆盖登录、房间、匹配、战斗帧同步主链路。
- 明确跨端协议协作规范（字段新增、枚举演进、兼容策略、日志键约定）。
- 更新 `Server/openspec/config.yaml` 的 artifact 规则，要求 proposal/design/specs/tasks 必须包含跨端影响、兼容矩阵、联调证据。
- 对根目录 `openspec/config.yaml` 补齐项目上下文最小必需信息，避免跨目录协作时丢失约束。

## Capabilities

### New Capabilities
- `server-collab-doc-index`: 定义服务端对外协作文档索引结构与最小联调信息集。
- `cross-end-spec-governance`: 定义 OpenSpec 产物中的跨端影响、兼容性与可验证证据规则。

### Modified Capabilities
- （无）本次变更不修改现有运行时能力规格，仅新增文档与协作治理能力。

## Impact

- 文档：`Server/CLAUDE.md` 将由“内部说明”升级为“对外协作入口”。
- 规范：`Server/openspec/config.yaml` 与根 `openspec/config.yaml` 的规则将更严格，影响后续所有变更工件内容质量。
- 协作流程：客户端与服务端联调时将统一使用同一套索引与验证证据，降低沟通歧义。
- 代码与运行时：无业务逻辑变更、无协议包体变更、无数据库结构变更。