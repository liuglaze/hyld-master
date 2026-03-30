## Context

当前服务端已经具备多场战斗并发的基础能力：`BattleManage` 使用 battleID 管理多场 `BattleController`，`LZJUDP` 也已经支持按 battleID 注册和路由 handler，匹配层则允许同一模式下存在多个 `BattleRoom`。但从匹配到战斗的生命周期仍存在三个结构性问题：第一，battle 元数据散落在 `dic_battles`、`dic_pattern`、`dic_matchUserInfo`、`DIC_BattlePlayerUIDs` 等平行字典中，更新和清理需要多处同步；第二，battleId 与 battlePlayerId 命名混用，导致 UDP 路由、清场和 game over 路径的语义不稳定；第三，MatchingController、ClearSenceController 和 UDP 路由层会直接拼接 BattleManage 内部字典，导致状态边界分散。

本次设计不重写网络层，也不修改现有 protobuf 协议，而是在现有 TCP 建战斗、UDP 帧同步的双通道模型上收敛服务端内部模型，使多场战斗、退出回收、迟到包处理和后续扩展具备更稳定的边界。

## Goals / Non-Goals

**Goals:**
- 为一场战斗建立统一的 `BattleContext` 模型，集中保存 battle 元数据、玩家映射和控制器实例
- 将 `BattleManage` 收敛为 battle 注册中心，统一提供 `battleId -> context` 和 `uid -> battleId` 查询能力
- 规范匹配成功后的建战斗入口，使匹配层输出稳定的 match result，再由 battle 层负责创建上下文和控制器
- 统一 battleId、battlePlayerId、uid 三种标识的命名与访问约定，降低多场并发下的混用风险
- 补齐匹配房退出清理、空房回收与清场流程的边界处理，减少脏状态残留

**Non-Goals:**
- 不修改 TCP/UDP 传输模型，不引入 async/await、消息总线或新的网络依赖
- 不在本次变更中修改 protobuf 定义、数据库 schema 或 DAO 行为
- 不重构好友房、登录、好友系统等与战斗框架无直接关系的模块
- 不追求性能优化或锁粒度细化，优先保证状态正确性与可维护性

## Decisions

### D1: 用 BattleContext 取代 BattleManage 内的平行字典

**选择**: 新增 `BattleContext`，统一保存 BattleId、FightPattern、MatchUsers、PlayerUids、UidToBattlePlayerId、BattleController 等元数据。`BattleManage` 内部主索引只保留 `Dictionary<int, BattleContext>` 和 `Dictionary<int, int>`（uid -> battleId）。

**理由**: 当前平行字典模式要求 BeginBattle 和 FinishBattle 同时维护多张表，容易出现漏删、漏加和边界不一致。BattleContext 可以把“同一场 battle 的所有状态”收拢为一个对象，降低生命周期管理复杂度。

**替代方案**: 继续保留现有字典，仅增加更多封装方法。
**放弃原因**: 外部调用虽然能稍微收口，但内部状态仍然分散，后续扩展 battle 生命周期字段时仍会继续堆字典。

### D2: 匹配层与战斗层之间引入显式 MatchResult

**选择**: MatchingController 在房间凑满后先组装一个稳定的匹配结果对象（如 MatchResult / MatchedPlayerEntry 集合），再调用 BattleManage.CreateBattle 或 BeginBattle 创建 battle context 与 BattleController。

**理由**: 当前 `BattleRoom.GetRoomPlayerInfo()` 返回 `Tuple<int,string,string>`，语义弱，且 MatchingController 直接负责把它翻译成战斗所需结构，耦合了匹配和战斗初始化。引入 match result 能明确“匹配成功”与“开始战斗”之间的边界。

**替代方案**: 继续沿用 Tuple 和 `StartFighting(server, infos, fightPattern)`。
**放弃原因**: 可运行，但可读性差，扩展地图配置、组队信息、补位规则时会继续膨胀 MatchingController。

### D3: 统一 battleId / battlePlayerId / uid 的命名语义

**选择**: 将全局战斗实例标识统一命名为 `battleId`，将局内玩家位次标识统一命名为 `battlePlayerId`，账号/角色标识保持为 `uid`。`BattleController` 内的映射、UDP 路由信息和清场控制器接口都以这套命名为准。

**理由**: 当前代码中多个位置把 battle 内玩家编号也命名为 battleId，导致阅读和边界处理时容易把“对局 ID”和“局内玩家 ID”混淆，这在多场并发和 UDP 路由中尤其危险。

**替代方案**: 保持现有字段名不动，只补注释。
**放弃原因**: 注释无法消除误用风险，后续代码修改仍容易产生行为回归。

### D4: ClearSence 与 UDP 路由改为通过 BattleManage 查询 context

**选择**: `ClearSenceController`、`ClientUdp` 和其他边界控制逻辑不再直接串联访问 `DIC_BattleIDs`、`DIC_BattlePlayerUIDs` 等内部字典，而是通过 `TryGetBattleContextByUid`、`TryGetBattleContext` 等查询接口访问 battle 信息。

**理由**: 当前边界控制器直接耦合 BattleManage 内部结构，导致 battle 生命周期变化时这些位置都需要同步修改。查询接口能稳定边界，减少迟到包和已结束战斗的异常路径。

**替代方案**: 保持现状，继续公开字典给外部读取。
**放弃原因**: 外部依赖内部实现细节过多，不利于后续逐步替换为 BattleContext。

### D5: 匹配房退出与回收做最小收口，不重写匹配策略

**选择**: 保留现有 `BattleRoom` / `BattleTeam` 结构，但补齐 `PlayerIDMapRoomDic` 删除、空房回收和更明确的玩家条目结构，不更改当前按模式容量和队伍数组房的规则。

**理由**: 当前真实问题是房间生命周期回收不完整，而不是匹配算法本身错误。先修清理与状态残留，风险更低，也更符合最小重构目标。

**替代方案**: 直接重写匹配系统，引入独立队列或 MMR 分桶。
**放弃原因**: 与当前问题不匹配，改动过大，且会显著增加验证成本。

## Risks / Trade-offs

- **[BattleManage 内部模型调整会触及多个调用点]** → 通过先增加查询接口、再逐步替换直接字典访问的方式迁移，避免一次性重写全部控制器
- **[命名收敛可能与现有 protobuf 字段名不一致]** → 仅在服务端内部命名统一，保留协议字段兼容映射，不在本次直接修改 proto
- **[匹配层增加回收逻辑可能改变边界时序]** → 先保持原匹配策略不变，只在退出和空房场景增加清理，降低行为变化范围
- **[迟到包处理变严格后可能暴露客户端旧时序问题]** → 对无法查询到 battle context 的包统一记录日志并丢弃，先保证服务端状态一致，再根据日志评估是否需要补客户端兼容
