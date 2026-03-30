## 1. 统一战斗期 UDP NetSim 入口

- [x] 1.1 在 `Server/Server/ClientUdp.cs` 中整理战斗期 UDP 包分类，明确哪些包进入统一 NetSim、哪些包使用保守控制策略
- [x] 1.2 在 `LZJUDP` 层实现统一的战斗期 UDP NetSim 判定入口，覆盖上行收包与下行发送两个方向
- [x] 1.3 将战斗期 NetSim 的启停继续绑定到 `BattleController.BeginBattle` 与 `HandleBattleEnd` 生命周期，确保非战斗 UDP 保持原行为

## 2. 收敛现有局部模拟逻辑

- [x] 2.1 删除 `ClientUdp.cs` 中 Pong 的局部 NetSim 分支，改为统一入口处理
- [x] 2.2 删除 `BattleController.Network.cs` 中权威帧的局部 NetSim 分支，并修正 authority frame drop 为真实丢弃
- [x] 2.3 为统一 NetSim 增加方向、包型、battleId、endpoint、决策结果等一致日志，便于联调验证

## 3. 客户端观测校验与联调文档同步

- [x] 3.1 在 `Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs` 与 `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs` 上校验现有 Ping、RTT 平滑、目标帧调节链路在统一 NetSim 下的观测含义，无需新增客户端本地 NetSim 代码
- [x] 3.2 视联调需要补充客户端观测日志或说明，确保能从客户端侧解释 RTT 变化、权威帧到达节奏与追帧表现之间的关系
- [ ] 3.3 验证归属: 1.1-3.2 完成后，在 3.4 集成验证 中统一验证
- [ ] 3.4 集成验证: 战斗阶段统一 UDP NetSim - [双端本地开战并观察 Ping/Pong、上行操作、下行权威帧、BattleStart/GameOver 日志] - [应确认仅战斗期启用模拟、上下行映射清晰、数据包共享统一口径、控制包保持可开局/可收尾、authority frame drop 为真实丢弃]
- [x] 3.5 同步更新 `Server/CLAUDE.md`、`Client/Assets/CLAUDE.md` 与 `BothSide.md` 中涉及战斗期 UDP NetSim 范围、入口、上下行映射与联调说明的内容
