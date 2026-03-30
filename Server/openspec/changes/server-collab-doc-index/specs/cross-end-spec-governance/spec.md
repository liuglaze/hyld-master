## ADDED Requirements

### Requirement: Proposal 必须包含跨端影响摘要
涉及客户端-服务端协作的变更提案 MUST 提供跨端影响摘要，至少包含 Server 影响面、Client 影响面、Proto 影响面与兼容策略。

#### Scenario: 提案涉及协议字段扩展
- **WHEN** 变更需要新增或修改协议字段
- **THEN** Proposal MUST 明确字段变化、默认值策略与旧客户端兼容方式

### Requirement: Design 必须包含 TCP/UDP 通道影响分析
涉及网络交互的设计文档 MUST 分析 TCP/UDP 双通道影响，明确状态流转入口、出口与失败回退路径。

#### Scenario: 设计涉及战斗准备流程
- **WHEN** 设计变更触及 BattleReady、BattleStart 或帧操作通道
- **THEN** Design MUST 描述 TCP/UDP 分工变化和异常回退策略

### Requirement: Specs 必须提供跨版本兼容矩阵
涉及跨端行为变化的规格文档 MUST 包含兼容矩阵，至少覆盖 new-server/old-client 与 old-server/new-client 组合。

#### Scenario: 规格定义跨端行为调整
- **WHEN** 新规格改变客户端可见行为
- **THEN** Specs MUST 给出版本组合下的预期行为与不兼容边界

### Requirement: Tasks 必须包含联调可验证证据
跨端变更的任务清单 MUST 包含可验证证据子任务，至少包括复现步骤、期望日志字段和抓包/报文检查点。

#### Scenario: 执行跨端任务后进入验证
- **WHEN** 开发完成并准备联调
- **THEN** Tasks MUST 提供可直接执行的验证步骤与期望观测结果
