# forclient：客户端协作文档（Server）

> 目标：让客户端同学 **先快速定位代码，再深入联调细节**。

> 导航：
> - 客户端联调入口：本文档 `Docs/ForClient.md`
> - 服务端维护总览：`CLAUDE.md`

---

## 1. 60 秒建立全局认知

### 1.1 系统主链路

```text
Unity Client (MainPack)
   │
   ├─ TCP: 7778  登录/好友/房间/匹配
   ▼
Program.cs -> Server/Server.cs -> Server/Client.cs -> Controller/ControllerManger.cs -> Controller/Controllers.cs -> Server/*
   ▲
   └─ UDP: 7777  BattleReady/BattleStart/帧操作/GameOver
```

### 1.2 你先记住这 4 个入口

1. 协议定义：`SocketProto.cs:139`（`RequestCode`）/ `SocketProto.cs:151`（`ActionCode`）/ `SocketProto.cs:248`（`MainPack`）
2. 请求路由：`Controller/ControllerManger.cs:51`（`HandleRequest`）
3. 控制器实现：`Controller/Controllers.cs`（按 RequestCode 分业务）
4. 战斗实时链路：`Server/ClientUdp.cs:166`（UDP 路由）+ `Server/Battle.cs:173`（战斗包处理）

---

## 2. 目录与职责索引（客户端最常用）

- `Program.cs:17`：服务启动入口
- `Server/Server.cs:32`：TCP 监听、连接管理、心跳检测
- `Server/Client.cs:141`：单连接收包入口（TCP）
- `SocketProto.cs:139`：协议枚举与消息结构
- `Controller/ControllerManger.cs:51`：`RequestCode + ActionCode` 反射分发
- `Controller/Controllers.cs:1018`：用户/登录相关控制器（`UserController`）
- `Controller/Controllers.cs:979`：好友控制器（`FriendController`）
- `Controller/Controllers.cs:700`：房间控制器（`FriendRoomController`）
- `Controller/Controllers.cs:119`：匹配控制器（`MatchingController`）
- `Server/FriendRoom.cs:10`：房间状态对象（成员、广播、进退房）
- `Server/Battle.cs:173`：战斗 UDP 消息路由（`BattleController.Handle`）
- `Server/Battle.cs:224`：战斗开始（`BeginBattle`）
- `Server/Battle.cs:320`：帧循环（`BattleLoop`）
- `Server/BattleController.Bullets.cs:12`：子弹生成（`SpawnBulletsFromOperations`）
- `Server/BattleController.Bullets.cs:163`：碰撞检测（`CheckBulletCollision`）
- `Server/BattleController.Network.cs:103`：操作接收（`UpdatePlayerOperation`）
- `Server/BattleController.Network.cs:50`：帧下行广播（`SendUnsyncedFrames`）
- `Server/BattleController.Network.cs:183`：战斗结束（`HandleBattleEnd`）
- `Server/BattleManage.cs:106`：战斗管理（`TryBeginBattle`）
- `Server/BattleContext.cs`：战斗元数据
- `Server/ServerBullet.cs`：服务端子弹数据
- `Server/ServerVector3.cs`：服务端向量
- `Server/ClientUdp.cs:12`：UDP 收发与 battle 路由
- `DAO/UserData.cs`：用户/好友数据读写（DB）

---

## 3. 客户端场景 → 服务端定位

| 场景 | 先看哪里 | 关键函数/入口 | 协议关注点 |
|---|---|---|---|
| 登录失败、重复登录 | `Controller/Controllers.cs` | `UserController.Login` `Controller/Controllers.cs:1044` | `RequestCode.User` + 登录 ActionCode |
| 登录后拉取玩家信息异常 | `Controller/Controllers.cs` | `FindPlayerInfo` `Controller/Controllers.cs:1078` | 活跃用户写入逻辑 |
| 好友申请/同意异常 | `Controller/Controllers.cs` | `FriendController.*` `Controller/Controllers.cs:979` | `RequestCode.Friend` |
| 创建房间/邀请失败 | `Controller/Controllers.cs` + `Server/FriendRoom.cs` | `CreateRoom` `Controller/Controllers.cs:717` / `JoinFriendRoom` `Controller/Controllers.cs:861` | `RequestCode.FriendRoom` |
| 匹配人数异常/未开局 | `Controller/Controllers.cs` | `AddMatchingPlayer` `Controller/Controllers.cs:417` | 匹配模式/队伍容量 |
| 开局后不进战斗 | `Server/BattleManage.cs` | `BattleManage.TryBeginBattle` `Server/BattleManage.cs:106` | `ActionCode.StartEnterBattle` |
| 帧不同步/丢包 | `Server/ClientUdp.cs` + `Server/Battle.cs` | `TryResolveBattleID` `Server/ClientUdp.cs:166` / `Handle` `Server/Battle.cs:173` | `BattleReady`、帧操作、GameOver |

---

## 4. TCP 请求完整调用链（排障主线）

1. 服务启动：`Program.cs:17`
2. 接入连接：`Server/Server.cs:174`（`ListenClientConnect`）
3. TCP 收包：`Server/Client.cs:155`（`ReceiveCallBack`）
4. 进入路由：`Server/Server.cs:141` -> `Controller/ControllerManger.cs:51`
5. 反射分发：`Controller/ControllerManger.cs:56`（`pack.Actioncode.ToString()`）
6. 控制器执行：`Controller/Controllers.cs` 对应业务方法

> 结论：如果“请求没生效”，先按 2→6 顺序确认：
> - 是否收到了包
> - RequestCode 是否命中 controller
> - ActionCode 方法名是否存在且拼写一致

---

## 5. UDP 战斗链路（实时联调主线）

1. UDP 接收线程：`Server/ClientUdp.cs:250`
2. 路由解析：`Server/ClientUdp.cs:166`（`TryResolveBattleID`）
3. `BattleReady` 建立 endpoint 映射：`Server/ClientUdp.cs:172`
4. 后续战斗包按 endpoint 路由：`Server/ClientUdp.cs:210`
5. 分发到战斗处理：`Server/Battle.cs:173`（`Handle`）
6. 全员 ready 后下发开战：`Server/Battle.cs:196`
7. 战斗已开始后若某客户端继续重发 `BattleReady`，服务端会视为其可能漏收首个 `BattleStart`，并对该 endpoint **单播补发** `BattleStart`（不重复 `BeginBattle`）：`Server/Battle.cs:196`
8. 帧下发与补帧：`Server/BattleController.Network.cs:50`（`SendUnsyncedFrames`）
9. **Pong 路由**：`Server/ClientUdp.cs:250`（Ping 识别 → Pong 构造）。Pong 发送**经过 NetSim**（`LZJUDP.SimDropRate/SimDelayMinMs/SimDelayMaxMs`），确保客户端 RTT 测量反映真实模拟延迟。NetSim 参数由 `BattleController.BeginBattle`（Battle.cs:224）写入、`HandleBattleEnd`（BattleController.Network.cs:183）清零

> 关键前提：客户端必须先成功发 `BattleReady`，否则后续 UDP 包无法命中 battle 路由。
>
> 开局握手补充：客户端会周期性重发 `BattleReady`，直到收到 `BattleStart` 为止；因此若首次 `BattleStart` 丢失，后续 `BattleReady` 仍可触发服务端补发，不会再次执行开局初始化。

---

## 6. 协议与路由约束（客户端必须知道）

### 6.1 强约束

- `RequestCode` 与控制器绑定：见 `Controller/ControllerManger.cs:53`
- `ActionCode` 与方法名强绑定（反射）：见 `Controller/ControllerManger.cs:56-57`
- 若 ActionCode 改名，服务端控制器方法名必须同步改，否则会出现“没有找到指定事件处理”。

### 6.2 通道约定

- TCP（7778）：登录/好友/房间/匹配
- UDP（7777）：战斗实时操作

### 6.3 状态归属

- 在线、好友、房间、战斗主要是服务端内存态（`Server/*`）
- 数据库（`DAO/*`）偏持久化，不是实时状态源

---

## 7. 联调执行清单（可直接照做）

1. 对齐协议：确认客户端 MainPack 与 `SocketProto.cs` 一致
2. 对齐通道：业务走 TCP，战斗走 UDP
3. 验证路由：确认 `ControllerManger.HandleRequest` 是否命中
4. 验证状态：登录态 → 房间态 → 战斗态迁移是否完整
5. 验证收尾：GameOver / BattleReview 是否只走一次主路径

推荐排查顺序：

`协议字段 -> 路由分发 -> 业务状态 -> 广播/回包 -> 客户端消费`

---

## 8. 已知风险与边界（跨端必读）

1. 反射分发强绑定：ActionCode 与方法名不一致会直接丢处理（`Controller/ControllerManger.cs:56-61`）
2. UDP 路由依赖 BattleReady 建映射：若 ready 包缺失或 battlePlayerId 不一致会被丢弃（`Server/ClientUdp.cs:185`、`Server/ClientUdp.cs:224`）
3. 实时状态以内存为主：重连/断线场景要优先看 `Server/Battle.cs`（BattleController）、`Server/BattleManage.cs`（BattleManage）与 `Server/Client.cs`

---

## 8.6 服务端日志系统（客户端排障必读）

### 8.6.1 日志文件在哪里

```text
D:\unity\hyld-master\hyld-master\Server\log\
  └── {启动时间}\          ← 每次启动服务端自动创建
        └── server.log    ← 当次运行的完整日志
```

- 子目录命名格式：`yyyy-MM-dd_HH时mm分ss秒`（如 `2026-03-17_09时57分48秒`）
- 每次启动服务端产生一个新目录，**按时间倒序找最新的就是当前运行日志**
- 配置位置：`Program.cs:20-22`

### 8.6.2 日志级别

| 级别 | 说明 | 控制台附加信息 |
|---|---|---|
| `Info` | 常规业务流水 | 无 |
| `Warn` | 警告 | 无 |
| `Error` | 错误 | 附加完整调用堆栈 |
| `Exception` | 异常 | 附加完整调用堆栈 |

默认全部级别均输出。级别定义见 `Tool/Loging.cs:57`。

### 8.6.3 日志格式

每行格式：`[HH:mm:ss.fff] [级别] 消息内容`

### 8.6.4 客户端排障怎么用

1. 找到服务端 `log/` 目录，按时间排序打开最新的子目录
2. 打开 `server.log`，搜索关键词：
   - **登录问题** → 搜 `[Login]` 或玩家 username
   - **UDP/战斗问题** → 搜 `[LZJUDP]` 或 `battleID`
   - **匹配问题** → 搜 `Matching` 或 `StartFighting`
   - **异常/报错** → 搜 `Error` 或 `Exception`
3. 日志带毫秒时间戳，可以和客户端日志做时间对齐

### 8.6.5 注意事项

- 日志缓冲区满 128KB 才刷盘，服务端正常关闭时会强制 flush（`Program.cs:31`）
- 如果服务端进程被强杀，最后一段日志可能丢失
- `log/` 根目录下还有一些旧版手动备份文件（`server_copy*.log`），可忽略
- 日志工具类：`Tool/Loging.cs`，调用方式 `Logging.Debug.Log(...)`

---

## 8.5 服务端伤害判定系统（server-authoritative-damage，2026-03）

### 8.5.1 伤害判定链路

伤害判定已完全迁移至服务端，客户端子弹系统仅作视觉表现：

1. 客户端上报 `AttackOperation`（含 `attack_id`、`client_frame_id`）
2. 服务端 `SpawnServerBullets`（BattleController.Bullets.cs:70）生成 ServerBullet
3. 每帧 `TickServerBullets`（BattleController.Bullets.cs:265）模拟子弹飞行 + 碰撞检测（`CheckBulletCollision`，BattleController.Bullets.cs:163，hitRadius 距离判定）
4. 命中 → 生成 `HitEvent`（含 `damage`、`is_kill`）→ 随 `BattleInfo.hit_events` 下行
5. 客户端 `ApplyHitEvents` 触发受击动画（纯表现，不修改 HP）
6. 客户端 `ApplyAuthoritativeHpAndDeath` 从 `PlayerStates.Hp` 覆写 HP，从 `PlayerStates.IsDead` 驱动死亡判定

### 8.5.2 死亡判定（authoritative-hp-sync 升级）

- 客户端死亡判定以服务端 `PlayerStates.IsDead` 为权威（由 `ApplyAuthoritativeHpAndDeath` 消费）
- `ApplyHitEvents` 中 `IsKill` 兜底保留为安全网（IsDead 未到达前的备用路径）
- `IsDead=true` 时强制 `playerBloodValue = -1`，确保 `PlayerLogic.playerDieLogic()` 触发
- HP 唯一修改来源：`ApplyAuthoritativeHpAndDeath`，从 `PlayerStates.Hp` 覆写

### 8.5.3 HP 配置

- 服务端 `HeroConfig._hpConfig` 全英雄 HP 为原值约 1/5（例：XueLi 960，原 4680）
- ~~客户端 `HYLDStaticValue` 英雄 `BloodValue` 仍为原始值，血条显示比例不匹配~~ **已解决**：`ApplyAuthoritativeHpAndDeath` 首帧从服务端 `PlayerStates.Hp` 初始化 `maxHp` 和 `hero.BloodValue`，血条比例自动对齐
- 后续计划：对象池优化完成后恢复原始 HP

### 8.5.4 攻击方向编解码对齐

proto 字段语义（摇杆轴与世界轴互换）：
- `AttackOperation.towardx` = 摇杆 X（对应世界 Z 轴分量）
- `AttackOperation.towardy` = 摇杆 Y（对应世界 X 轴分量）

服务端消费（`BattleController.Bullets.cs:70 SpawnServerBullets`）：
```
teamSign = (playerTeam != baseTeamId) ? -1 : 1
baseX = -Towardy * teamSign   // X 轴取反 + 队伍镜像
baseZ = Towardx * teamSign    // 队伍镜像
baseDir = Normalize(baseX, 0, baseZ)
```

客户端消费（`HYLDPlayerManger / BattleManger`）：
```
dir = xAndY2UnitVector3(Towardy, Towardx)
dir.x *= -1 * sign   // sign=1 同队, sign=-1 对方队
dir.z *= sign
```

**约定**：后续修改方向编解码时，必须同时更新两端对应代码。

### 8.5.5 攻击去重

- 服务端 `dic_lastProcessedAttackId`（BattleController.Network.cs:166）记录每个玩家最后处理的 AttackId
- 客户端 UDP 丢包时自动重发未确认攻击（pendingAttacks 窗口），服务端忽略已处理的旧 AttackId

### 8.5.6 权威 HP/IsDead 下行（authoritative-hp-sync）

- **proto 字段**：`PlayerState` 消息含 `hp`（int32）和 `is_dead`（bool），随 `AllPlayerOperation.PlayerStates` 每帧下发
- **服务端写入**：`PackPlayerStates`（BattleController.Network.cs:18）将 `playerHp[battleId]` 和 `playerIsDead[battleId]` 写入帧数据
- **客户端消费**：`ApplyAuthoritativeHpAndDeath` 取批次最后一帧的 `PlayerStates` 覆写 `playerBloodValue`
- **帧序保护**：`_lastAuthHpFrameId` 跳过乱序到达的旧批次（UDP 包乱序时防止 HP 回弹）
- **攻击超时**：服务端 `MaxAcceptableAttackDelay=6`，超过此帧数的延迟攻击被 REJECT

---

## 9. 快速提问模板（发给服务端）

建议按下面格式给问题，定位速度最快：

- 场景：登录 / 房间 / 匹配 / 战斗
- 协议：RequestCode + ActionCode
- 用户标识：uid / playerName
- 时间点：具体到秒
- 现象：预期 vs 实际
- 证据：客户端日志、抓包片段、关键帧号

---

（文档版本：forclient-v7，Battle.cs 拆分后更新所有文件引用和行号）
