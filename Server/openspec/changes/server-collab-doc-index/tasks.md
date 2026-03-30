## 1. 对外协作文档重构（Server/CLAUDE.md）

- [ ] 1.1 设计“先导航后细节”的文档目录：读者范围、60 秒上手、场景索引、联调清单、风险边界（文件：Server/CLAUDE.md，层级：协作文档层）
- [ ] 1.2 新增“按场景索引表”，覆盖登录/房间/匹配/战斗帧同步，并给出入口文件、协议通道、观测点（文件：Server/CLAUDE.md，层级：协作文档层）
- [ ] 1.3 补充“跨端协作规范”章节，定义字段新增、枚举演进、兼容策略与日志键约定（文件：Server/CLAUDE.md，层级：协作文档层）

## 2. OpenSpec 协作规则增强（Server/openspec/config.yaml）

- [ ] 2.1 在 proposal 规则中增加“跨端影响摘要”硬约束（Server/Client/Proto/兼容策略）（文件：Server/openspec/config.yaml，层级：规范配置层）
- [ ] 2.2 在 design 规则中增加 TCP/UDP 通道影响与状态流转说明要求（文件：Server/openspec/config.yaml，层级：规范配置层）
- [ ] 2.3 在 specs 规则中增加跨版本兼容矩阵与可观测性字段要求（文件：Server/openspec/config.yaml，层级：规范配置层）
- [ ] 2.4 在 tasks 规则中增加联调证据要求（复现步骤、日志键、抓包检查点）（文件：Server/openspec/config.yaml，层级：规范配置层）

## 3. 根目录 OpenSpec 对齐（openspec/config.yaml）

- [ ] 3.1 补齐根配置最小项目上下文，确保跨目录协作时不丢基础约束（文件：openspec/config.yaml，层级：仓库级规范层）
- [ ] 3.2 添加通用跨端协作底线规则，避免与 Server 专项规则冲突（文件：openspec/config.yaml，层级：仓库级规范层）

## 4. 联调与验收证据沉淀

- [ ] 4.1 产出“客户端可读”的联调清单样例（请求路径、期望日志、失败排查顺序）（文件：Server/CLAUDE.md，层级：协作文档层）
- [ ] 4.2 自检文档可导航性与规则可执行性，确认后续 /opsx:apply 可直接按任务推进（文件：openspec/changes/server-collab-doc-index/*，层级：变更治理层）
