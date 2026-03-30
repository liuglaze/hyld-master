# CLAUDE.md — 服务端项目架构速览

> 本文件用于会话启动时快速加载服务端上下文；面向客户端协作的细节请看 `Docs/ForClient.md`。

## 1. 启动与主链路

`Program.cs:8` → `Server/Server.cs:32` → `Server/Client.cs:155` → `Controller/ControllerManger.cs:51` → `Controller/Controllers.cs` → `Server/*`

- `Program.cs:8`（Main）：配置日志路径、覆写 ServerConfig、启动 TCP 服务器
- `Server/Server.cs:32`（构造）：TCP 监听 7778、UDP 监听 7777、启动监听线程和 Ping 检测
- `Server/Client.cs:155`（ReceiveCallBack）：TCP 收包 → 解析 MainPack → HandleRequest
- `Controller/ControllerManger.cs:51`（HandleRequest）：按 RequestCode 找 Controller、反射调用 ActionCode 方法
- `Controller/Controllers.cs`：User/Friend/FriendRoom/Matching/PingPong/ClearSence 控制器

## 2. 协议与网络通道

- `SocketProto.cs`：协议枚举与消息结构（`RequestCode` / `ActionCode` / `MainPack`）
- TCP `7778`：登录、好友、房间、匹配
- UDP `7777`：战斗实时消息（`BattleReady`、帧操作、`Ping/Pong`、`GameOver`）
- `Server/ClientUdp.cs`（LZJUDP 类）：UDP 收发与战斗路由

## 3. 关键文件索引（函数级）

### 3.1 战斗系统（拆分后 7 文件）

**Server/Battle.cs**（549 行） — BattleController 主文件：字段 + 生命周期 + 帧循环 + 位置追踪
- `BattleController`:84 — 构造：注册 UDP 回调、TCP 下发 StartEnterBattle
- `Handle`:173 — UDP 消息路由：BattleReady / BattlePushDowmPlayerOpeartions / ClientSendGameOver
- `BeginBattle`:224 — 初始化所有战斗状态、HP、出生位置、Input Buffer、启动 BattleLoop 线程
- `BattleLoop`:320 — 帧循环：累加器驱动 + 每帧 CollectAndBroadcastCurrentFrame + GameOver 检测
- `CollectAndBroadcastCurrentFrame`:393 — 按 `lastConsumedMoveFrame + 1` 优先、否则取最小更大合法帧来消费 Input Buffer → 推进位置 → 记录快照 → 打包权威状态 → 生成子弹 → 碰撞检测 → 广播
- `UpdatePlayerPositions`:497 — 根据移动输入推进服务端玩家位置
- `RecordPositionSnapshot`:526 — 记录位置到环形历史缓冲区（V2 延迟补偿）
- `TryGetPositionSnapshot`:543 — 按帧号查询历史位置快照
- `HandlePlayerDisconnect`:135 — 玩家断线：标记断线、触发 GameOver

**Server/BattleController.Bullets.cs**（299 行） — 子弹生成 / 碰撞 / 追帧 / HP
- `SpawnBulletsFromOperations`:12 — 遍历帧操作中的 AttackOperation，逐攻击生成子弹 + 追帧
- `SpawnServerBullets`:70 — 按英雄配置生成子弹列表（支持散弹扇形），V2 历史位置回溯
- `CreateServerBullet`:137 — 创建单颗 ServerBullet 数据对象
- `CheckBulletCollision`:163 — 共享碰撞检测：距离判定 + HP 扣减 + 击杀判定 + GameOver 触发
- `SimulateBulletCatchUp`:233 — V2 延迟补偿追帧：逐帧推进 + 历史位置碰撞检测
- `TickServerBullets`:265 — 每帧推进活跃子弹 + 碰撞检测 + 超距清除

**Server/BattleController.Network.cs**（253 行） — 网络收发 / 权威状态
- `PackPlayerStates`:18 — 打包所有玩家权威状态（位置/HP/IsDead）到帧数据
- `SendUnsyncedFrames`:50 — 帧下行广播（含 NetSim 丢包/延迟模拟，含 HitEvent 搭载）
- `UpdatePlayerOperation`:103 — 接收客户端操作：Ack 钳位 + 移动写入 Input Buffer + 攻击去重/超时
- `HandleBattleEnd`:183 — 战斗结束：停循环、清理资源、清 NetSim、注销 UDP、发送 FinishBattle
- `SendFinishBattle`:224 — 发送 GameOver 包（含 winnerTeamId）
- `UpdatePlayerGameOver`:235 — 处理客户端上报 GameOver

**Server/BattleManage.cs**（243 行） — 战斗管理单例
- `TryBeginBattle`:106 — 创建 BattleContext + BattleController，注册 uid 映射
- `FinishBattle`:191 — 清理映射、构造回放包、TCP 下发 BattleReview
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
- `Client`:110 — 构造：MySQL 连接、启动异步接收
- `ReceiveCallBack`:155 — 收包 → 解析 → HandleRequest
- `Send`:182 — 序列化 + 异步发送
- `UpdateMyselfInfo`:242 — 状态变更广播给在线好友
- `Close`:310 — 幂等关闭：清理状态、通知好友、关闭 Socket/DB

**Server/ClientUdp.cs**（339 行，LZJUDP 类） — UDP 单例
- `RegisterBattle`:42 — 按 battleID 注册回调
- `UnregisterBattle`:54 — 注销回调 + 清理端点路由
- `TryResolveBattleID`:166 — BattleReady 建映射、后续包按端点路由
- `RecvThread`:250 — UDP 接收：解包 → Ping/Pong 拦截 → 路由到 BattleController
- `Send`:108 — 序列化 + SendTo

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
**DAO/UserData.cs**（425 行） — MySQL 数据访问

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
          1. 从 inputBuffer 按帧号消费操作（缺帧 → lastConsumedMove 补偿）
          2. 合并攻击操作（从 dic_currentFrameOperationBuffer）
          3. UpdatePlayerPositions (Battle.cs:497) — 推进逻辑位置
          4. RecordPositionSnapshot (Battle.cs:526) — 环形缓冲区
          5. PackPlayerStates (Network.cs:18) — HP/IsDead/位置 打包
          6. SpawnBulletsFromOperations (Bullets.cs:12) — 生成子弹 + V2 追帧
          7. TickServerBullets (Bullets.cs:265) — 推进 + 碰撞 + HitEvent
          8. SendUnsyncedFrames (Network.cs:50) — 帧下行广播（含 NetSim）
        frameid++
```

### 4.4 操作接收

```
客户端 UDP 上行 → LZJUDP.RecvThread → TryResolveBattleID → BattleController.Handle
  → case BattlePushDowmPlayerOpeartions:
    → UpdatePlayerOperation (Network.cs:103):
      1. Ack 钳位（不超 frameid-1）
      2. 移动写入 Input Buffer（clientFrameId % 2）
      3. 攻击去重（dic_lastProcessedAttackId）
      4. 攻击超时（frameDelay > MaxAcceptableAttackDelay=6 → REJECT）
```

### 4.5 伤害判定链路

```
SpawnBulletsFromOperations (Bullets.cs:12):
  遍历帧操作中每个玩家的 AttackOperation
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
  2. 清理子弹/历史/Input Buffer
  3. 清零 LZJUDP NetSim 参数
  4. LZJUDP.UnregisterBattle
  5. SendFinishBattle (Network.cs:224) → UDP 广播 GameOver（winnerTeamId）
  6. BattleManage.FinishBattle (BattleManage.cs:191)
    → 清理 uid 映射
    → TCP 下发 BattleReview（含完整帧历史回放数据）
```

## 5. 动态追帧系统（服务端部分）

### 5.1 Input Buffer

- 数据结构（Battle.cs:49-54）：`Dictionary<int, List<BufferedMoveInput>> dic_movementInputBuffer` + `dic_lastConsumedMoveFrame` + `dic_lastValidMove` + `dic_consecutiveMissedFrames`
- 窗口参数：`InputBufferSize=4`、`InputFutureLeadTolerance=6`
- 入队（Network.cs:87 `UpdatePlayerOperation`）：
  - 先按客户端 ack 清理 `SyncFrameId <= clientAckedFrame - 2` 的旧输入
  - 若 `syncFrameId <= lastConsumedMoveFrame`，直接拒绝入缓冲并打印 `REJECT_STALE`
  - 同 `SyncFrameId` 输入覆盖更新，不同帧输入插入后按 `SyncFrameId` 排序
  - 缓冲满时淘汰最小 `SyncFrameId`，打印 `EVICT_OLDEST_ON_FULL`，仅保留最近 `InputBufferSize=4` 条移动输入
- 消费（Battle.cs:366 `CollectAndBroadcastCurrentFrame`）：
  - 优先消费 `lastConsumedMoveFrame + 1`
  - 若目标帧缺失，则取 `SyncFrameId > lastConsumedMoveFrame` 且 `<= frameid + InputFutureLeadTolerance` 的最小合法帧，并打印 `SKIP_GAP_ACCEPT`
  - 命中严格下一帧时打印 `ACCEPT_IN_ORDER`
  - 成功消费后推进 `lastConsumedMoveFrame`，并移除该输入及其之前更老的输入
  - 未命中新输入时，沿用 `dic_lastValidMove` 做短时惯性，且不推进消费进度；攻击仍独立走 `dic_pendingAttacks`
- 语义：移动输入按 `SyncFrameId` 升序推进；缺帧不等待，但一旦跳过更大帧，后续迟到旧帧会在接收阶段被拒绝
- Ack 钳位：使用客户端显式上报的 `ClientAckedFrame` 更新 `dic_playerAckedFrameId`

### 5.2 Ping/Pong

- 客户端每 200ms 发 `ActionCode.Ping`（含 timestamp）
- `ClientUdp.RecvThread`（ClientUdp.cs:250）统一经过 `ProcessInboundBattlePacket`，战斗期 `Ping` 先走统一 NetSim 上行入口，再由服务端构造 `Pong`（timestamp 原样回传）
- **Battle UDP 统一 NetSim**：`LZJUDP` 按 `ActionCode` 区分 `Data / Control / RouteSetup` 策略；`BattlePushDowmPlayerOpeartions`、`BattlePushDowmAllFrameOpeartions`、`Ping`、`Pong` 走数据策略，`BattleStart`、`ClientSendGameOver`、`BattlePushDowmGameOver` 走控制策略，`BattleReady` 走建链保护策略
- NetSim 参数由 `BattleReady` 全员完成后预写入（保证 `BattleStart` 进入统一入口），`BeginBattle` 保持战斗期激活，`HandleBattleEnd` 在 `BattlePushDowmGameOver` 调度完成后清零

## 6. 网络模拟（NetSim，测试用）

- 常量位于 `Server/Server/Battle.cs:85-87`：当前压测档为 `SimDropRate=0.10f`, `SimDelayMinMs=80`, `SimDelayMaxMs=120`
- 战斗期 UDP 统一经 `LZJUDP` battle-scoped NetSim 入口处理；`SendUnsyncedFrames` 只负责组织权威帧内容，不再单独做局部丢包/延迟
- `Ping/Pong`、上行操作、下行权威帧共享同一套战斗期 NetSim 参数；`BattleStart/GameOver` 也进入统一框架但采用控制包策略
- `ClientUdp.cs` 已从“每包 `ThreadPool + Sleep`”改为“单独调度线程 + 延迟队列”，避免 ThreadPool 排队抖动污染 RTT 测量
- 当前实测：在 `8% + 70~100ms` 与 `10% + 80~120ms` 两档下，战斗整体仍保持可演示的顺滑度，说明早期“前期爆卡、后期突然顺滑”的主因已不再是 NetSim 调度污染
- 发布前需将 `SimDropRate` 设为 0

## 7. 状态所有权

- **服务端权威**：帧状态（frameid）、玩家 HP（playerHp）、死亡（playerIsDead）、击杀判定、GameOver、胜负结果
- **服务端维护**：playerPositions、positionHistory、activeBullets、inputBuffer
- **客户端上报**：移动输入（PlayerMoveX/Y）、攻击操作（AttackOperation）、BattleReady、ClientSendGameOver

## 8. 日志系统

- 工具类：`Tool/Loging.cs`，`Logging.Debug.Log(消息[, 级别])`
- 级别：Info / Warn / Error（附堆栈） / Exception（附堆栈）
- 路径：`Server/log/{yyyy-MM-dd_HH时mm分ss秒}/server.log`（Program.cs:12）
- 128KB 缓冲刷盘，正常退出 flush，强杀可能丢尾部

## 9. 场景速查

| 场景 | 先看哪里 | 关键入口 |
|---|---|---|
| 登录失败/重复登录 | Controllers.cs | `UserController.Login`:1044 |
| 好友申请异常 | Controllers.cs | `FriendController`:979 |
| 创建/进房异常 | Controllers.cs + FriendRoom.cs | `CreateRoom`:717 / `JoinFriendRoom`:861 |
| 匹配未开局 | Controllers.cs | `AddMatchingPlayer`:417 |
| 开局不进战斗 | BattleManage.cs | `TryBeginBattle`:106 |
| 帧不同步/丢包 | ClientUdp.cs + Battle.cs | `TryResolveBattleID`:166 / `Handle`:173 |
| 子弹未命中/方向错 | BattleController.Bullets.cs | `SpawnServerBullets`:70 / `CheckBulletCollision`:163 |
| HP 不扣/死亡不触发 | BattleController.Bullets.cs | `CheckBulletCollision`:163（HP 扣减 + 击杀判定） |
| GameOver 未发送 | BattleController.Network.cs | `HandleBattleEnd`:183 / `SendFinishBattle`:224 |

## 10. 协作约定

- 客户端到服务端协作文档：`D:\unity\hyld-master\hyld-master\Client\Assets\Docs\ForServer.md`
- 双端联动记录：`D:\unity\hyld-master\hyld-master\BothSide.md`
- 客户端联调文档：`Docs/ForClient.md`
- 若改动涉及两端交互（协议字段、接口行为、时序或状态同步），务必写入 `BothSide.md`

## 11. 文档同步约束

每次代码变动后必须检查：
- `CLAUDE.md`（本文件）
- `Docs/ForClient.md`（客户端联调链路）
- `D:\unity\hyld-master\hyld-master\BothSide.md`（两端交互变更）

判定标准：改动影响"入口、路由、协议、状态流、联调步骤、跨端行为"时必须更新。

## 12. 注意

- `ActionCode` 与控制器方法名强绑定（反射）：改名必须同步，否则"没有找到指定事件处理"
- `HeroConfig._hpConfig` 当前为测试值（约原值 1/5），后续需恢复

## OpenSpec

仅当任务明确涉及 OpenSpec、`/opsx`、proposal/design/spec/tasks 工件时，再进入完整 OpenSpec 流程。默认不启用。
