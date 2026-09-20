# CLAUDE.md — 服务端项目架构速览

> 本文件用于会话启动时快速加载服务端上下文；面向客户端协作的细节请看 `Docs/ForClient.md`。

## 1. 启动与主链路

`Program.cs` → `Server/Server.cs:32` → `Server/Client.cs:155` → `Controller/ControllerManger.cs:51` → `Controller/Controllers.cs` → `Server/*`

- `Program.cs`（Main）：配置日志路径、覆写 ServerConfig、启动 TCP 服务器
- `Server/Server.cs:32`（构造）：TCP 监听 7778、UDP 监听 7777、启动监听线程和 Ping 检测
- `Server/Client.cs:155`（ReceiveCallBack）：TCP 收包 → 解析 MainPack → HandleRequest
- `Controller/ControllerManger.cs:51`（HandleRequest）：按 RequestCode 找 Controller、调用显式分发表里注册的委托
- `Controller/Controllers.cs`：User/Friend/FriendRoom/Matching/PingPong/ClearSence 控制器

### 1.1 目标框架与运行前提（2026-09 变更）

- **目标框架：`net8.0`**（原 `net6.0`）。
  原因：net6.0 已 EOL，且构建机上没有 .NET 6 运行时（只有 8/9/10），
  `dotnet Server.dll` 会直接报 `You must install or update .NET to run this application`，服务端起不来。
  对应迁移计划 Q5。
- **不依赖任何数据库**：账号数据改为进程内内存库 `DAO/UserStore.cs`，
  原先的 MySQL（`MySql.Data` 包 + `ServerConfig.DOMConectStr`）已完全移除。
  服务端不再需要预装 MySQL 才能启动。详见 §3.5。
- 启动：`dotnet Server/bin/Debug/net8.0/Server.dll`
  （或 `dotnet run --project Server/Server.csproj`）。

### 1.2 保活方式与无头运行

`Program.cs` 结尾根据 stdin 是否被重定向分两种保活方式：

- **交互式控制台**（`Console.IsInputRedirected == false`）：`Console.Read()`。
- **非交互**（服务化、CI、`start /B` 重定向日志等）：`Thread.Sleep(Timeout.Infinite)`。

原因：`Console.Read()` 在 stdin 被重定向时会**立刻返回 -1**，导致进程
「打印了『启动监听成功』随即退出」，端口瞬间消失、极难定位。迁移计划里 Lobby/DS
都需要能被非交互地拉起，所以必须区分。

## 2. 协议与网络通道

- `SocketProto.cs`：协议枚举与消息结构（`RequestCode` / `ActionCode` / `MainPack`）
- TCP `7778`：登录、好友、房间、匹配
- UDP `7777`：战斗实时消息（`BattleReady`、帧操作、`Ping/Pong`、`GameOver`）
- `Server/ClientUdp.cs`（LZJUDP 类）：UDP 收发与战斗路由
- 战斗结束后 `BattleManage.FinishBattle` 会通过 TCP 下发 `BattleReview` 完整帧历史；该包可能超过 1024 字节，服务端/客户端 TCP 半包缓冲都必须按包头扩容，不能用固定小缓冲接收。

## 3. 关键文件索引（函数级）

### 3.1 战斗系统（拆分后 7 文件）

**Server/Battle.cs**（549 行） — BattleController 主文件：字段 + 生命周期 + 帧循环 + 位置追踪

- `BattleController`:84 — 构造：注册 UDP 回调、TCP 下发 StartEnterBattle
- `Handle`:173 — UDP 消息路由：BattleReady / BattlePushDowmPlayerOpeartions / ClientSendGameOver
- `BeginBattle`:224 — 初始化所有战斗状态、HP、出生位置、ClientMove 时间轴、启动 BattleLoop 线程
- `BattleLoop`:320 — 帧循环：累加器驱动 + 每帧 CollectAndBroadcastCurrentFrame + GameOver 检测
- `CollectAndBroadcastCurrentFrame`:393 — 读取最新合法 ClientMove 移动意图/攻击 → 记录位置快照 → 打包权威状态 → 生成子弹 → 碰撞检测 → 广播
- `UpdatePlayerPositions`:497 — 历史保留函数；当前 CMC-style 移动位置推进发生在 `ApplyPendingClientMoves`
- `RecordPositionSnapshot`:526 — 记录位置到环形历史缓冲区（V2 延迟补偿）
- `TryGetPositionSnapshot`:543 — 按帧号查询历史位置快照
- `HandlePlayerDisconnect`:135 — 玩家断线：标记断线、触发 GameOver

**Server/BattleController.Bullets.cs**（299 行） — 子弹生成 / 碰撞 / 追帧 / HP

- `SpawnBulletsFromOperations`:12 — 遍历帧操作中的 ServerAttack，逐攻击生成子弹 + 追帧
- `SpawnServerBullets`:70 — 按英雄配置生成子弹列表（支持散弹扇形），V2 历史位置回溯
- `CreateServerBullet`:137 — 创建单颗 ServerBullet 数据对象
- `CheckBulletCollision`:163 — 共享碰撞检测：距离判定 + HP 扣减 + 击杀判定 + GameOver 触发
- `SimulateBulletCatchUp`:233 — V2 延迟补偿追帧：逐帧推进 + 历史位置碰撞检测
- `TickServerBullets`:265 — 每帧推进活跃子弹 + 碰撞检测 + 超距清除

**Server/BattleController.Network.cs** — 网络收发 / 权威状态

- `PackPlayerStates`:18 — 生成当前帧完整权威状态（位置/HP/IsDead/Mana）并保存状态快照
- `SendUnsyncedFrames`:50 — 只下发当前权威帧，并按接收客户端已确认状态基准裁剪 `PlayerStates` 增量；每包仍携带当前 `server_frame`
- `UpdatePlayerOperation`:103 — 接收 `BattleInfo.client_input`：AckedServerFrame 单调更新 + ClientMoveFrame 单调处理 + ClientAttack 去重/超时/普通蓝量或大招能量扣除与 AttackAck
- `HandleBattleEnd`:183 — 战斗结束：停循环、清理资源、清 NetSim、注销 UDP、发送 FinishBattle
- `SendFinishBattle`:224 — 发送 GameOver 包（含 winnerTeamId）
- `UpdatePlayerGameOver`:235 — 处理客户端上报 GameOver

**Server/BattleManage.cs**（243 行） — 战斗管理单例

- `TryBeginBattle`:106 — 创建 BattleContext + BattleController，注册 uid 映射
- `FinishBattle`:191 — 清理映射、恢复参战玩家 `PlayerOnline` 状态、刷新好友活跃信息、构造回放包、TCP 下发 BattleReview
- `HandleClientDisconnect`:168 — 检测战斗中玩家断线，转发到 BattleController
- `TryGetBattleIDByUID`:38 — uid → battleId
- `TryGetBattlePlayerId`:89 — uid → battlePlayerId
- `TryGetController`:75 — battleId → BattleController

**Server/BattleContext.cs**（37 行） — 战斗元数据

- 属性：BattleId, FightPattern, MatchUsers, PlayerUids, UidToBattlePlayerId, Controller
- `AttachController`:31 — 关联 BattleController 实例

**Server/ServerVector3.cs**（26 行） — 服务端三维向量（避免依赖 Unity）

- 运算符 `+` / `*`、`Distance`、`Magnitude`、`Normalized`

**Server/ServerBullet.cs**（19 行） — 服务端子弹纯数据

- 字段：AttackId, OwnerBattleId, OwnerTeamId, Position, Direction, Speed, MaxDistance, TraveledDistance, Damage, ClientFrameId

### 3.2 网络层

**Server/Server.cs**（234 行） — TCP 服务器

- `Server`:32 — 构造：监听 7778、初始化 UDP、启动监听/心跳线程
- `HandleRequest`:157 — 转发 TCP 消息到 ControllerManger
- `ListenClientConnect`:174 — 后台阻塞等待新连接
- `CheckPing`:209 — 心跳超时检查（4 倍 pingInterval）
- `GetActiveClient`:59 — uid → Client

**Server/Client.cs**（350 行） — 单 TCP 连接

- `Client`:110 — 构造：启动异步接收（原实现还会在此打开 MySQL 连接，现已移除）
- `ReceiveCallBack`:155 — 收包 → 解析 → HandleRequest
- `Send`:182 — 序列化 + 异步发送
- `Close`:310 — 幂等关闭：清理状态、通知好友、关闭 Socket

**Server/ClientUdp.cs**（339 行，LZJUDP 类） — UDP 单例

- `RegisterBattle`:42 — 按 battleID 注册回调
- `UnregisterBattle`:54 — 注销回调 + 清理端点路由
- `TryResolveBattleID`:166 — BattleReady 建映射、后续包按端点路由
- `RecvThread`:250 — UDP 接收：解包 → Ping/Pong 拦截 → 路由到 BattleController
- `Send`:108 — 序列化 + SendTo
- `BattleSetNetSimConfig`：客户端调参控制包，更新 battle-scoped NetSim

### 3.3 业务控制器

**Controller/ControllerManger.cs**（78 行） — 请求路由

- `HandleRequest`:51 — RequestCode → Controller → 反射 ActionCode 方法
- `CloseClient`:43 — 广播下线事件给所有 Controller

**Controller/Controllers.cs**（1141 行） — 6 个控制器

- `UserController.Login`:1044 — 登录
- `FindPlayerInfo`:1078 — 拉取玩家信息
- `FriendController`:979 — 好友系统
- `FriendRoomController`:700 — 房间管理（`CreateRoom`:717、`JoinFriendRoom`:861）
- `MatchingController`:119 — 匹配系统（`AddMatchingPlayer`:417）

### 3.4 配置与工具

**Server/HeroConfig.cs**（108 行） — 英雄配置（静态）

- `Get`:64 — 按英雄枚举返回 BulletParams（速度/射程/伤害/散弹数/扇形角/碰撞半径）
- `GetHp`:103 — 按英雄枚举返回最大 HP（当前为原值约 1/5）

**Server/ServerConfig.cs**（14 行） — 常量

- TCPservePort=7778, UDPservePort=7777, frameTime=16ms

**Server/FriendRoom.cs**（208 行） — 房间状态

- `Join`:101 — 加入房间
- `Exit`:115 — 退出房间（游戏中/等待中两种路径）
- `BroadCastTCP`:79 — 排除自身广播
- `BroadcastToAll`:92 — 全员广播

**Tool/Loging.cs**（132 行） — 日志：Info/Warn/Error/Exception 四级
**Tool/IPManager.cs**（48 行） — 获取本机 IPv4/IPv6 地址
**Tool/Message.cs**（163 行） — TCP 帧封装（ByteArray）
**DAO/UserStore.cs** — **进程内内存用户库**（替代原 MySQL）：`users` + `friends` 两张表的内存实现。
  线程安全（服务端每连接一线程），对外只返回 `UserSnapshot` 值副本。详见 §3.5。
**DAO/UserData.cs** — 单连接用户数据门面：持有「这条连接当前是谁」（UID/UserName/PlayerName/PlayerHero），
  共享数据委托给 `UserStore`。

### 3.5 用户数据：内存库（数据库已移除）

**背景**：本工程不做账号持久化。原实现要求本机装 MySQL 才能登录，
而构建/联调环境没有数据库，结果就是「服务端起不来 → 客户端连不上」。
现改为内存实现，零外部依赖。

| 项 | 原实现 | 现实现 |
|---|---|---|
| 存储 | MySQL `hyld` 库 | `DAO/UserStore.cs` 内存字典 |
| 依赖 | `MySql.Data` NuGet + `ServerConfig.DOMConectStr` | 无 |
| `users` | `UserName`(PK) / `Password` / `name` / `id` 自增 | 同名内存记录，`id` 从 1 自增 |
| `friends` | 单向边 `(UserID, FriendID)`，查询时双向匹配 | 同样的单向边 + 双向匹配（语义等价） |
| 注册 | `INSERT INTO users`，主键冲突返回 false | `UserStore.TryRegister`，同名返回 false |
| 登录 | `SELECT ... WHERE UserName AND Password` | `UserStore.TryLoginOrCreate` |

**行为变更（有意，需知晓）**：

1. **登录自动建号**：账号不存在时直接建号并成功登录——不需要先走注册流程。
   账号存在但密码不符仍**失败**（密码校验没有被削弱）。
2. **新账号默认昵称 = 账号名**（原 MySQL 实现写入空串 `name=''`）。
   理由：数据库已移除，没有别的途径产生昵称，而空昵称会让好友流程与大厅显示不可用。
   客户端「昵称为空则引导改名」的判断仍保留，改名流程不受影响。
3. **进程重启即清空**：内存实现的定义，不是缺陷。
4. `hyld.sql`（仓库根）是原 MySQL 的建表/种子数据，**已不再被任何代码使用**，
   仅作为历史 schema 记录保留。

**未变的行为（原样保留，改动时勿破坏）**：

- **一条 TCP 连接只能登录一次**：`UserController.Login` 开头 `if (client.UserName != null) → Fail`。
- **同一账号同时只能有一条活跃连接**（防顶号）：`server.GetActiveClientByUserName(...) != null → Fail`。
  注意副作用：客户端崩溃后立即用同账号重连，可能在服务端 Ping 超时清理之前被拒。

**回归工具**：`Tools/PMServerSmokeTest/`（不需 Unity、不需数据库），
以真实 TCP 客户端跑 10 个场景（自动建号、重复登录保护、密码校验、FindPlayerInfo、
FindFriendsInfo、UpdateName 读回、Logon 重复、`request_id` 回带）。

```bat
dotnet build Server\Server.csproj
dotnet build Tools\PMServerSmokeTest -c Release
REM 先启动服务端，再跑：
dotnet Tools\PMServerSmokeTest\bin\Release\net8.0\PMServerSmokeTest.dll
```

## 4. 战斗主链路（全链路函数级）

### 4.1 匹配 → 开战

```
MatchingController.AddMatchingPlayer (Controllers.cs:417)
  → MatchingController.StartFighting (Controllers.cs)
    → BattleManage.TryBeginBattle (BattleManage.cs:106)
      → new BattleContext (BattleContext.cs)
      → new BattleController (Battle.cs:84)
        → LZJUDP.RegisterBattle (ClientUdp.cs:42)
        → TCP 广播 StartEnterBattle 到所有参战客户端
```

### 4.2 BattleReady → BattleStart

```
客户端 UDP → LZJUDP.RecvThread (ClientUdp.cs:250)
  → TryResolveBattleID (ClientUdp.cs:166) 建立 endpoint 映射
  → BattleController.Handle (Battle.cs:173)
    → case BattleReady: 记录 ready 状态 + 刷新 battlePlayerId → endpoint
    → 首次全员 ready → 预写入 NetSim 参数 → UDP 广播 BattleStart → BeginBattle
    → 若战斗已开始后某客户端仍持续发送 BattleReady
      → 视为该客户端可能未收到 BattleStart
      → 服务端对该 endpoint 单播补发 BattleStart（不重复 BeginBattle）
```

### 4.3 帧循环（BattleLoop）

```
BattleLoop (Battle.cs:320) — 后台线程 16ms 步进
  while (_isRun):
    accum += dt
    while (accum >= frameIntervalMs):
      if (oneGameOver) → HandleBattleEnd → return
      else:
        CollectAndBroadcastCurrentFrame (Battle.cs:393):
          1. 读取每个玩家最新合法 ClientMove 移动意图
          2. 合并攻击操作（从 dic_currentFrameOperationBuffer）
          3. 初始化本帧 HitEvent 列表
          4. RecordPositionSnapshot (Battle.cs:526) — 环形缓冲区
          5. SpawnBulletsFromOperations (Bullets.cs:12) — 生成子弹 + V2 追帧，追帧命中写入同一 HitEvent 列表
          6. TickServerBullets (Bullets.cs:265) — 推进 + 碰撞 + HitEvent
          7. PackPlayerStates (Network.cs:18) — HP/IsDead/Mana/位置完整快照
          8. SendUnsyncedFrames (Network.cs:50) — 只组织当前权威帧，`PlayerStates` 按接收客户端状态基准增量下发，并按 `CurrentFrameRepeatSendCount` 重复发送
        frameid++
```

### 4.4 操作接收

```
客户端 UDP 上行 → LZJUDP.RecvThread → TryResolveBattleID → BattleController.Handle
  → case BattlePushDowmPlayerOpeartions:
    → UpdatePlayerOperation (Network.cs:103):
      1. ClientAckedFrame 单调更新（客户端已应用的最新 ServerFrame）
      2. ClientMove 按 moveFrame 单调处理：先处理 OldMove，再按包内顺序处理所有非 OldMove；旧帧丢弃，超前过多拒绝
      3. 合法 ClientMove 入队为 pending client move，同时保存当前移动意图供权威帧广播
      4. 攻击去重（dic_lastProcessedAttackId）
      5. 攻击超时（frameDelay > MaxAcceptableAttackDelay=8 → REJECT）
      6. 攻击资源门控：普通攻击蓝量足够才扣蓝；子弹型大招能量满且有服务端配置才清零并进入 pendingAttacks；不足回 `AttackAck(accepted=false, reject_reason="mana"|"super_energy"|"unsupported_super")`
```

### 4.5 伤害判定链路

```
SpawnBulletsFromOperations (Bullets.cs:12):
  遍历帧操作中每个玩家的 ServerAttack
  → SpawnServerBullets (Bullets.cs:70):
    V2 历史位置回溯（positionHistory[clientFrameId]）
    方向编解码（baseX=-Towardy*teamSign, baseZ=Towardx*teamSign）
    散弹扇形生成 → CreateServerBullet (Bullets.cs:137)
    写入 atk.SpawnPosX/Y/Z（供客户端半空推进）
  → 延迟攻击追帧：SimulateBulletCatchUp (Bullets.cs:233)
    逐帧推进 + 历史位置碰撞 → 命中生成 HitEvent
    未命中 → 加入 activeBullets

TickServerBullets (Bullets.cs:265):
  推进活跃子弹 → CheckBulletCollision (Bullets.cs:163):
    距离判定 (hitRadius) → HP 扣减 → 击杀 → oneGameOver
    → 生成 HitEvent（含 damage/is_kill）

客户端消费:
  HandleMessage → ApplyHitEvents (纯受击动画)
  → ApplyAuthoritativeHpAndDeath (HP 覆写 + IsDead 死亡判定)
```

### 4.6 战斗结束

```
oneGameOver = true（来源：击杀 / 断线）
→ BattleLoop 检测到 → HandleBattleEnd (Network.cs:183):
  1. _hasEnded = true, _isRun = false
  2. 清理子弹/历史/ClientMove 状态
  3. 清零 LZJUDP NetSim 参数
  4. LZJUDP.UnregisterBattle
  5. SendFinishBattle (Network.cs:224) → UDP 广播 GameOver（winnerTeamId）
  6. BattleManage.FinishBattle (BattleManage.cs:191)
    → 清理 uid 映射
    → 恢复在线参战玩家 PlayerState=PlayerOnline，并刷新好友活跃信息
    → TCP 下发 BattleReview（含完整帧历史回放数据）
```

## 5. 动态追帧系统（服务端部分）

### 5.1 CMC-style ClientMove 时间轴

- 帧号语义：
  - `ServerFrame`：服务端 `frameid`，只由 BattleLoop 每 16ms 推进；下行 `BattleInfo.server_frame` 与 `BattleFrame.server_frame` 都是 ServerFrame。
  - `ClientMoveFrame`：客户端本地预测 tick，写在 `ClientMove.move_frame`；只用于移动上行排序、OldMove 去重、SavedMove 确认。
  - `AckedServerFrame`：客户端上行 `BattleInfo.client_input.acked_server_frame`，表示客户端已应用到的最新 ServerFrame。
  - `AckedMoveFrame`：服务端下行 `BattleInfo.server_update.move_ack.acked_move_frame`，表示权威位置已实际模拟到的 ClientMoveFrame。
- 协议字段：`BattleInfo.client_input.moves` 携带 `ClientMove`；`BattleInfo.client_input.attacks` 携带 `ClientAttack`；`BattleInfo.server_update.move_ack` 携带服务端对本地玩家 Move 的确认/修正结果。
- 客户端每个预测 tick 生成 SavedMove；普通帧可先挂起为 `pendingMove`，下一帧若存在 pending 且 current 是新帧，则同包按顺序发送 pending + current 两个 `NewMove`（DualMove）。当前暂不做真正 SavedMove 合并。每个上行包最多附带 4 个未确认 important `OldMove`，按 `moveFrame` 升序发送。
- 服务端状态（Battle.cs）：`dic_lastReceivedMoveFrame`、`dic_lastAckedMoveFrame`、`dic_pendingClientMoves`、`dic_lastProcessedMoveInput`、`dic_lastProcessedClientMoveFrame`、`dic_lastMoveAck`。
- 接收（BattleController.Network.cs `UpdatePlayerOperation` / `ProcessClientMove`）：
  - 先收集所有 `OldMove` 并按 `move_frame` 升序处理，再按包内顺序处理所有非 `OldMove`；客户端 DualMove 用两个顺序 `NewMove` 表达。
  - 非 `OldMove` 的 `moveFrame <= lastReceivedMoveFrame` 直接丢弃并打印 `[ClientMove][STALE_RECEIVED]`；`OldMove` 只在已有 pending move 覆盖该帧时丢弃。
  - 不再按 `moveFrame - lastReceivedMoveFrame` 做 future lead 拒收；合法 move 只要不旧于接收水位，就会入队等待 BattleLoop 模拟。
  - 合法 move 入队为 pending client move，并在入队成功后只允许单调更新 `lastReceivedMoveFrame = max(lastReceivedMoveFrame, moveFrame)`；OldMove 低于当前 received 水位时不能回退该水位。
  - BattleLoop 固定阶段按 `MoveFrame` 升序处理 pending move，并一次性处理完当前 pending 队列。
  - 每条 move 处理前计算 `serverDeltaSinceLastProcessedMove = frameid - lastServerFrameWhenProcessedMove`，表示距离上一次处理该玩家 move，服务器真实经过了多少帧；处理完这条 move 后立即把 `lastServerFrameWhenProcessedMove` 更新为当前 `frameid`。
  - 正常态先按 `baseFrames = min(clientDelta, MaxDeltaFramesPerMove)` 直接模拟；误差用 `rawError = clientDelta - serverDeltaSinceLastProcessedMove` 进入 debt，`debt = max(0, debt + rawError)`，客户端慢下来时允许抵消之前的正误差。
  - 当 debt 大于 0 后切入 resolving；触发 resolving 的当前 move 立刻走偿还分支，不等下一条 move。
  - resolving 态用 `serverBoundFrames = min(baseFrames, serverDeltaSinceLastProcessedMove)` 限制当前 move，再用 `MoveDiscrepancyResolutionRate` 和 `paybackCarry` 按整数帧偿还 debt；最终 `framesToApply` 不低于 `MinMoveQuantum=1`，对应 UE 的 `MIN_TICK_TIME`。
  - 同一个 ServerFrame 内连续处理 move 时，第一条可能看到 `serverDeltaSinceLastProcessedMove=1`，后面的 move 因上一条已更新处理时间，通常自然看到 0；这里没有全局服务器帧资源池。
  - 如果高帧 move 先到，例如 100 后收到 104，则使用 104 的输入一次性覆盖模拟 101-104；迟到 101-103 会被丢弃。
  - `OldMove` 完整模拟不写最终 MoveAck。
  - 非 `OldMove` 完整模拟后，使用 `moveFrame` 作为 `acked_move_frame`，再比较权威位置与 `ClientMove.predicted_pos_x/y/z`；误差不超过 `MovementMaxPositionError=0.6f` 时下发 `ack_good_move=true`，否则下发 `ack_good_move=false + correct_pos_x/y/z + correct_vel_x/y/z`。
  - 非 `OldMove` 产生 correction 后不再让后续 good ack 覆盖本批 correction；服务端继续模拟后续 pending move。
- 消费（Battle.cs `CollectAndBroadcastCurrentFrame`）：
  - 每个 ServerFrame 读取当前移动意图用于权威帧广播和动画参数。
  - 每帧完成 pending move 一次性模拟后记录当前权威位置。
  - `RecordPositionSnapshot(frameid)` 记录当前服务端权威位置历史。
- Ack：`ClientAckedFrame` 表示客户端已消费到的服务端权威帧；移动确认/修正单独使用 `MoveAckResult`，`AckedMoveFrame = moveFrame`，只确认完整模拟的非 `OldMove` SavedMove。

### 5.2 Ping/Pong

- 客户端每 200ms 发 `ActionCode.Ping`（含 timestamp）
- `ClientUdp.RecvThread`（ClientUdp.cs:250）统一经过 `ProcessInboundBattlePacket`，战斗期 `Ping` 先走统一 NetSim 上行入口，再由服务端构造 `Pong`（timestamp 原样回传）
- **Battle UDP 统一 NetSim**：`LZJUDP` 按 `ActionCode` 区分 `Data / Control / RouteSetup` 策略；`BattlePushDowmPlayerOpeartions`、`BattlePushDowmAllFrameOpeartions`、`Ping`、`Pong` 走数据策略，`BattleStart`、`ClientSendGameOver`、`BattlePushDowmGameOver` 走控制策略，`BattleReady` 走建链保护策略
- NetSim 参数由 `BattleReady` 全员完成后预写入（保证 `BattleStart` 进入统一入口），`BeginBattle` 保持战斗期激活，`HandleBattleEnd` 在 `BattlePushDowmGameOver` 调度完成后清零

## 6. 网络模拟（NetSim，测试用）

- 常量位于 `Server/Server/Battle.cs:87-91`：当前默认档为 `DefaultSimDropRate=0.10f`, `DefaultSimDelayMinMs=30`, `DefaultSimDelayMaxMs=60`；当前帧重复下发参数为 `CurrentFrameRepeatSendCount=3`
- 战斗期 UDP 统一经 `LZJUDP` battle-scoped NetSim 入口处理；`SendUnsyncedFrames` 只负责组织当前权威帧内容并重复发送，不再按 `ClientAckedFrame` 组织最近窗口补帧
- `Ping/Pong`、上行操作、下行权威帧共享同一套战斗期 NetSim 参数；`BattleStart/GameOver` 也进入统一框架但采用控制包策略
- `ClientUdp.cs` 已从“每包 `ThreadPool + Sleep`”改为“单独调度线程 + 延迟队列”，避免 ThreadPool 排队抖动污染 RTT 测量
- 当前实测：在 `8% + 70~100ms` 与 `10% + 80~120ms` 两档下，战斗整体仍保持可演示的顺滑度，说明早期“前期爆卡、后期突然顺滑”的主因已不再是 NetSim 调度污染
- 客户端可以通过 `ActionCode.BattleSetNetSimConfig` 动态调整当前战斗的 `drop_rate`、`delay_min_ms`、`delay_max_ms`；服务端按 `0~1`、`0~2000ms`、`min<=max` 处理并立即生效
- 发布前需将 `SimDropRate` 设为 0

## 7. 状态所有权

- **服务端权威**：帧状态（frameid）、玩家 HP（playerHp）、死亡（playerIsDead）、普通攻击蓝量（playerMana）、大招能量（playerSuperEnergy）、击杀判定、GameOver、胜负结果
- **服务端维护**：playerPositions、positionHistory、activeBullets、ClientMove 处理进度
- **客户端上报**：移动 SavedMove（ClientMove）、攻击操作（ClientAttack）、BattleReady、ClientSendGameOver

## 8. 日志系统

- 工具类：`Tool/Loging.cs`，`Logging.Debug.Log(消息[, 级别])`
- 级别：Info / Warn / Error（附堆栈） / Exception（附堆栈）
- 路径：`Server/log/{yyyy-MM-dd_HH时mm分ss秒}/server.log`
  - 由 `Program.cs` 的 `ResolveLogDirectory()` 推导：从可执行文件目录向上找到
    「同时含 `Server` 与 `Client` 子目录」的那一层（即工程根），再拼 `Server/log`；
    找不到就退回 `<可执行文件目录>/log`。
  - 启动时会打印日志文件绝对路径，避免「日志不知道去哪了」。
  - 历史问题：原实现把路径**硬编码**为 `D:/unity/hyld-master/hyld-master/Server/log/...`
    （某台旧开发机的绝对路径）。工程换位置后日志会被写到一个与代码无关的目录树里，
    且因父目录会被自动创建，失败也不报错。
- 128KB 缓冲刷盘，正常退出 flush，强杀可能丢尾部

## 9. 场景速查

| 场景               | 先看哪里                       | 关键入口                                             |
| ------------------ | ------------------------------ | ---------------------------------------------------- |
| 登录失败/重复登录  | Controllers.cs                 | `UserController.Login`:1044                          |
| 好友申请异常       | Controllers.cs                 | `FriendController`:979                               |
| 创建/进房异常      | Controllers.cs + FriendRoom.cs | `CreateRoom`:717 / `JoinFriendRoom`:861              |
| 匹配未开局         | Controllers.cs                 | `AddMatchingPlayer`:417                              |
| 开局不进战斗       | BattleManage.cs                | `TryBeginBattle`:106                                 |
| 帧不同步/丢包      | ClientUdp.cs + Battle.cs       | `TryResolveBattleID`:166 / `Handle`:173              |
| 子弹未命中/方向错  | BattleController.Bullets.cs    | `SpawnServerBullets`:70 / `CheckBulletCollision`:163 |
| HP 不扣/死亡不触发 | BattleController.Bullets.cs    | `CheckBulletCollision`:163（HP 扣减 + 击杀判定）     |
| GameOver 未发送    | BattleController.Network.cs    | `HandleBattleEnd`:183 / `SendFinishBattle`:224       |

## 10. 协作约定

- 客户端到服务端协作文档：`D:\unity\hyld-master\hyld-master\Client\Assets\Docs\ForServer.md`
- 双端联动记录：`D:\unity\hyld-master\hyld-master\BothSide.md`
- 客户端联调文档：`Docs/ForClient.md`
- 若改动涉及两端交互（协议字段、接口行为、时序或状态同步），务必写入 `BothSide.md`

## 11. 文档同步约束

每次代码变动后必须检查：

- `AGENTS.md`（本文件）
- `Docs/ForClient.md`（客户端联调链路）
- `D:\unity\hyld-master\hyld-master\BothSide.md`（两端交互变更）

判定标准：改动影响"入口、路由、协议、状态流、联调步骤、跨端行为"时必须更新。

## 12. 协议来源与生成约定

- **唯一权威 proto 源**：`D:\unity\hyld-master\hyld-master\ProtobufAndNotepad\Protobuf\SocketProto.proto`
- **服务端运行时生成产物**：`Server/Server/SocketProto.cs`
- **客户端运行时对应产物**：`D:\unity\hyld-master\hyld-master\Client\Assets\Scripts\Server\SocketProto.cs`
- **生成脚本**：`D:\unity\hyld-master\hyld-master\ProtobufAndNotepad\Protobuf\build.bat`
- **非权威历史文件**：`D:\unity\hyld-master\hyld-master\Client\SocketProto.proto`、`D:\unity\hyld-master\hyld-master\ProtobufAndNotepad\Protobuf\Proto\SocketProto.proto`
- **维护要求**：需要改协议字段时，只改权威 proto 源并重新生成客户端/服务端运行时 `SocketProto.cs`；不要把工具目录或历史 proto 副本当成并行维护入口。


## 13. 注意

- `ActionCode` 与控制器方法名强绑定（反射）：改名必须同步，否则"没有找到指定事件处理"
- `HeroConfig._hpConfig` 当前为测试值（约原值 1/5），后续需恢复
