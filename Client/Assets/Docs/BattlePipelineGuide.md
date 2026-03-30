# HYLD 客户端战斗全链路文档（演示讲解用）

> 本文档面向项目演示答辩，覆盖客户端战斗系统的每一个脚本、每一个关键函数、关键行。
> 最后更新：2026-03-17

---

## 目录

1. [架构总览](#1-架构总览)
2. [战斗初始化流程](#2-战斗初始化流程)
3. [输入采集层](#3-输入采集层)
4. [命令聚合层](#4-命令聚合层)
5. [战斗主循环（发送与预测）](#5-战斗主循环发送与预测)
6. [UDP 网络层](#6-udp-网络层)
7. [权威帧接收与处理](#7-权威帧接收与处理)
8. [CSP 位置校正与输入重放](#8-csp-位置校正与输入重放)
9. [子弹系统（视觉表现层）](#9-子弹系统视觉表现层)
10. [伤害判定与死亡系统](#10-伤害判定与死亡系统)
11. [动态追帧系统](#11-动态追帧系统)
12. [玩家移动与渲染层](#12-玩家移动与渲染层)
13. [摄像机系统](#13-摄像机系统)
14. [英雄配置系统](#14-英雄配置系统)
15. [文件索引与行数统计](#15-文件索引与行数统计)
16. [端到端数据流图](#16-端到端数据流图)

---

## 1. 架构总览

本项目为 **帧同步（Lockstep）+ 客户端预测（CSP）** 的联网对战游戏。

**核心架构分层**：

```
┌──────────────────────────────────────────────────────────────┐
│                       输入采集层                              │
│   TouchLogic.cs  (摇杆 → 移动/攻击输入)                      │
├──────────────────────────────────────────────────────────────┤
│                       命令聚合层                              │
│   CommandManger.cs  (输入 → 命令 → BattleData.selfOperation) │
├──────────────────────────────────────────────────────────────┤
│                    战斗主循环 + 网络层                         │
│   BattleManger.cs  (累加器驱动 Tick → 预测 → 发包)            │
│   UDPSocketManger.cs  (UDP 收发 → ConcurrentQueue)           │
├──────────────────────────────────────────────────────────────┤
│                      战斗数据层                               │
│   BattleData.cs              (核心数据/帧号/初始化)           │
│   BattleData.Prediction.cs   (预测历史/权威确认/输入重放)     │
│   BattleData.Authority.cs    (权威帧入口/位置校正/动画)       │
│   BattleData.HitEvent.cs     (HitEvent/HP 覆写/死亡)         │
│   BattleData.Attack.cs       (攻击队列/预测子弹去重)          │
│   BattleData.Rtt.cs          (RTT 测量/EWMA 平滑)            │
├──────────────────────────────────────────────────────────────┤
│                      子弹系统                                 │
│   HYLDBulletManger.cs (视觉子弹管理/Attack/和解)              │
│   BulletLogic.cs      (发射逻辑/散弹/扇形/直线)              │
│   shell.cs            (单颗子弹飞行/特效/销毁)                │
├──────────────────────────────────────────────────────────────┤
│                  玩家移动 + 渲染层                             │
│   HYLDPlayerManger.cs      (逻辑帧位移/镜像/出生点)          │
│   HYLDPlayerController.cs  (渲染位置追赶/动画)               │
│   HYLDCameraManger.cs      (摄像机跟随/SmoothDamp)           │
├──────────────────────────────────────────────────────────────┤
│                      数据 / 配置                              │
│   HYLDStaticValue.cs  (全局静态/PlayerInformation/Hero 配置)  │
│   ConstValue.cs       (网络参数常量)                          │
└──────────────────────────────────────────────────────────────┘
```

**所有权分工**：
- **服务端权威**：最终帧状态、玩家 HP、死亡判定、击杀/GameOver
- **客户端负责**：输入采集→预测→发包→收权威帧→校正→视觉表现

---

## 2. 战斗初始化流程

### 入口：`HYLDStaticValue.Awake()` → `BattleManger.Init()`

**文件**：`HYLDStaticValue.cs:102-188`

```
Awake()
├─ Heros.Clear() + 20 个 Heros.Add(...)          // 注册所有英雄配置
├─ 大招子弹参数配置（数据驱动）                     // :158-184 SuperBulletParams
├─ heroName 枚举反向赋值                           // :186-190 foreach
├─ 根据 ModenName 选择游戏模式
│  ├─ HYLDBaoShiZhengBa → AddComponent<HYLDBaoShiZhengBaManger>()
│  └─ HYLDJinKuGongFang → AddComponent<BattleManger>()
└─ BattleManger.Init()
```

### `BattleManger.Init()` — 文件：`BattleManger.cs:44-65`

```csharp
Instance = this;                              // :51 单例
playerManger = AddComponent<HYLDPlayerManger>();  // :55 玩家管理
cameraManger = AddComponent<HYLDCameraManger>();  // :56 摄像机
bulletManger = AddComponent<HYLDBulletManger>();  // :57 子弹管理
ScenseBuildLogic.InitData();                      // :58 场景
playerManger.InitData();                          // :60 初始化玩家
cameraManger.InitData();                          // :61 初始化摄像机
bulletManger.InitData();                          // :62 初始化子弹池
NetGlobal.Instance.Init();                        // :64 主线程回调队列
```

### `BattleManger.Start()` — 文件：`BattleManger.cs:66-74`

```csharp
Loacal_IP_port = UDPSocketManger.Instance.InitSocket();  // :70 建立 UDP 连接
UDPSocketManger.Instance.Handle = HandleMessage;          // :71 注册消息回调
StartCoroutine(WaitInitData());                           // :72 等待组件就绪
```

### `WaitInitData()` → `Send_BattleReady()` — 文件：`BattleManger.cs:157-182`

等待 `playerManger.initFinish && cameraManger.initFinish && bulletManger.initFinish && ScenseBuildLogic.InitFinish` 全部为 true 后，每 200ms 循环发送 `BattleReady` 包直到服务端回复 `BattleStart`。

### `HandleMessage(BattleStart)` — 文件：`BattleManger.cs:379-393`

```csharp
isBattleStart = true;                         // :386 防重复
CancelInvoke("Send_BattleReady");             // :387 停止准备包
_tickAccumulator = 0f;                        // :389 累加器归零
_battleTickActive = true;                     // :391 启动战斗 Tick
StartCoroutine(WaitForFirstMessage());        // :392 等待第一帧数据
```

---

## 3. 输入采集层

**文件**：`Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs`（231 行）

### 移动输入：`OnJoystickMove()` — `:111-228`

**核心逻辑**（move.joystickName == "PlayerMove"）— `:181-226`：

1. **滞回死区**（Hysteresis Dead Zone）— `:186-207`
   - `MoveStartDeadZone = 0.18f`（开始阈值），`MoveStopDeadZone = 0.12f`（停止阈值）
   - 双阈值避免摇杆在边界处反复触发移动/停止
   - `isMoveInputActive` 状态机：超过 start 阈值激活，低于 stop 阈值停止

2. **归一化**（float 域）— `:210-223`
   ```csharp
   float mag = Mathf.Sqrt(axisX * axisX + axisY * axisY);  // :210
   float normX = axisX / mag;                                // :214
   HYLDStaticValue.PlayerMoveX = new Fixed(normX);           // :216
   ```
   归一化后写入全局静态变量，保证不同摇杆偏移量下移速恒定。

3. **写入 CommandManger** — `:225`
   ```csharp
   CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
   ```

### 攻击输入：`JoystickMoveEnd()` — `:47-75`

攻击是**离散输入**（摇杆松手触发），不是连续输入：

```csharp
// :67 死区过滤
if (MathFixed.Abs(FirePositionX) <= 0.02f && MathFixed.Abs(FirePositionY) <= 0.02f)
    return;
// :73 入队攻击命令
CommandManger.Instance.AddCommad_Attack(FirePositionX.ToFloat(), FirePositionY.ToFloat());
```

### 射击瞄准线：`OnJoystickMove()` — `:115-179`

FireNormal/FireSuper 摇杆移动时：
- 实时计算射击方向和 LineRenderer 显示瞄准线
- `LaunchAngle == 0` → 直线瞄准（`:141-151`）
- `LaunchAngle != 0` → 扇形多线瞄准（`:152-175`）

### 大招能量：`FixedUpdate()` — `:22-38`

```csharp
if (当前能量 >= 最大能量) → 可以按大招 = true   // :29
能量条.SetActive(!可以按大招)                    // :31 切换 UI
大招遥感.SetActive(可以按大招)                   // :32
```

---

## 4. 命令聚合层

**文件**：`Assets/Scripts/Manger/CommandManger.cs`（105 行）

### 设计模式：命令模式（Command Pattern）

- `Commad` 接口（`:14-17`）：`void Execute()`
- `AttackCommad` 类（`:49-65`）：封装攻击方向，Execute 时调用 `BattleData.Instance.EnqueueAttack(dx, dy)`
- 单例：`CommandManger.Instance`（`:24-36`）

### 移动输入：`AddCommad_Move()` — `:75-85`

**连续输入**，不入队，只记录最新值：
```csharp
latestMoveX = dx;  latestMoveY = dy;  // :78-79
```

### 攻击输入：`AddCommad_Attack()` — `:67-70`

**离散输入**，入队到 `allCommad` 列表：
```csharp
allCommad.Add(new AttackCommad(dx, dy));  // :69
```

### 核心方法：`Execute()` — `:87-104`

每个发送帧被 `BattleManger.BattleTick()` 调用一次：

```csharp
// Step 1: 写入最新移动值
selfOperation.PlayerMoveX = latestMoveX;   // :90
selfOperation.PlayerMoveY = latestMoveY;   // :91

// Step 2: 消费离散命令（攻击入队 pendingAttacks）
for (i = 0; i < allCommad.Count; i++)
    allCommad[i].Execute();                // :96
allCommad.Clear();                         // :100

// Step 3: 待确认攻击 → selfOperation.AttackOperations
BattleData.Instance.FlushPendingAttacksToOperation();  // :103
```

### 攻击队列机制（BattleData.Attack.cs）

- `EnqueueAttack()` — 分配唯一 AttackId，锁定 `ClientFrameId = predicted_frameID`
- `FlushPendingAttacksToOperation()` — 超时清理（帧龄 > MaxClientAttackAge=10）+ 全量打包到 selfOperation
- `ConfirmAttacks()` — 收到权威确认后移除 `<= maxConfirmedId` 的攻击
- 丢包时自动重传未确认攻击，不依赖可靠传输层

---

## 5. 战斗主循环（发送与预测）

**文件**：`Assets/Scripts/Server/Manger/Battle/BattleManger.cs`（496 行）

### 驱动方式：累加器（Accumulator）驱动

不用 `InvokeRepeating`，而是在 `Update()` 中每帧累加 `Time.deltaTime`，达到 `currentTickInterval` 时执行一次 `BattleTick()`。

### `Update()` — `:81-134`

四步管线：

```
Step 1: DrainAndDispatch()         // :96  消费 UDP 队列中的权威帧
Step 2: Ping 调度                   // :99-107  每 200ms 发 Ping
Step 3: CalcTargetFrame + Adjust   // :110-111  动态追帧调节
Step 4: 累加器循环                  // :120-133  while(acc >= interval) BattleTick()
```

关键约束：`_tickAccumulator` 上限 = `currentTickInterval * maxCatchupPerUpdate(3)`（`:123-127`），防止暂停后追帧爆发。

### `BattleTick()` — `:249-315`

每次逻辑 Tick 的完整流程：

```
1. cachedMainThreadTime = Time.time        // :251  缓存时间（子线程安全）
2. nextFrame = predicted_frameID + 1       // :254-256
3. ResetOperation()                        // :264  清空上一帧操作
4. CommandManger.Instance.Execute()        // :265  聚合本帧输入
5. 本地预测子弹                             // :268-289
   ├─ 检测 selfOperation.AttackOperations
   ├─ 跳过已预测的 (IsAttackPredicted)
   └─ SpawnVisualBullet + MarkAttackPredicted
6. RecordPredictedHistory(nextFrame)                           // 快照本帧输入
7. ApplyLocalPredictedInputAndRefreshAllObjects(nextFrame, localOps) // 只预测本地输入，但刷新全体对象并推进子弹
8. CommitPredictedFrame(nextFrame)                             // 提交预测帧号
9. SendOperation(uploadOperationId)        // 上行到服务端
```

**关键行解读**：
- `:254-256`：预测模式用 `NextPredictedFrameId`，非预测模式用 `sync_frameID + 1`
- `:262`：`uploadOperationId = nextFrame`，动态追帧下可能远大于服务端 frameid
- `:282`：方向还原 `dir.x *= -1`，因为客户端坐标系 X 轴翻转
- `:284`：调用 `bulletManger.SpawnVisualBullet` 立即生成子弹（零延迟体验）

### `ApplyLocalPredictedInputAndRefreshAllObjects()` / `ApplyFullFrame()`

当前主链路已不再经过旧的 `BattleData.OnLogicUpdate` 委托，而是直接走显式入口：

```csharp
// 预测帧：只预测本地输入，但刷新全体玩家对象
ApplyLocalPredictedInputAndRefreshAllObjects(...)
  -> ApplyPlayerOperations(...)
  -> playerManger.RefreshAllPlayerObjects()
  -> LogPlayerStates()
  -> bulletManger.OnLogicUpdate()

// 完整逻辑帧
ApplyFullFrame(...)
  -> ApplyFrameCore(...)
  -> LogPlayerStates()
  -> bulletManger.OnLogicUpdate()
```

---

## 6. UDP 网络层

**文件**：`Assets/Scripts/Server/Manger/UDPSocketManger.cs`（141 行）

### 架构：生产者-消费者模式

```
子线程 ReceiveLoop → ConcurrentQueue<MainPack> → 主线程 DrainAndDispatch
```

### `InitSocket()` — `:43-65`

```csharp
client = new Socket(Dgram, UDP);                // :45
client.Connect(ServiceIP, ServiceUDPPort=7777); // :51
new Thread(ReceiveLoop).Start();                // :56
```

### `ReceiveLoop()` — `:70-95`

子线程死循环收包：
```csharp
int length = client.Receive(receiveBuffer, ...);         // :77
MainPack pack = MainPack.Descriptor.Parser.ParseFrom(...); // :86
_recvQueue.Enqueue(pack);                                  // :87
```

**关键**：子线程**只入队，不碰游戏状态**。所有状态修改在主线程 `DrainAndDispatch` 中完成。

### `DrainAndDispatch()` — `:101-112`

主线程每帧调用（BattleManger.Update 第一步）：
```csharp
while (_recvQueue.TryDequeue(out pack))
    _drainBuffer.Add(pack);          // :104
for (i = 0; i < _drainBuffer.Count; i++)
    Handle?.Invoke(_drainBuffer[i]); // :110  → BattleManger.HandleMessage
```

### `SendOperation()` — `:114-123`

```csharp
pack.BattleInfo.SelfOperation = BattleData.Instance.selfOperation;  // :120
pack.BattleInfo.OperationID = operationFrameId;                      // :121
```

---

## 7. 权威帧接收与处理

### `HandleMessage()` — `BattleManger.cs:361-428`

消息分发入口：

| ActionCode | 处理 |
|---|---|
| `Pong` | `:364-369` RTT 采样 |
| `BattleStart` | `:379-393` 启动战斗 |
| `BattlePushDowmAllFrameOpeartions` | `:394-411` 权威帧处理（核心） |
| `BattlePushDowmGameOver` | `:413-425` GameOver |

### 权威帧处理流水线（`:394-411`）

```
1. OnLogicUpdate_sync_FrameIdCheck()   → 位置校正 + 动画 + 子弹生成
2. ApplyHitEvents()                    → 受击动画（纯表现）
3. ApplyAuthoritativeHpAndDeath()      → HP 覆写 + 死亡判定
```

### `OnLogicUpdate_sync_FrameIdCheck()` — `BattleData.Authority.cs:169-305`

权威帧入口，7 步流水线：

```
Step 1: ApplyAuthoritativePositions()            // :185  权威位置覆写所有玩家
Step 1.5: UpdateAnimationStateFromAuthority()    // :188  远端玩家动画参数
Step 2: sync_frameID = server_sync_frameid       // :191  更新同步帧号
        AlignPredictedFrameWithAuthority()       // :192  校正预测帧号
Step 3: RecordAuthorityConfirmation()            // :196  攻击确认 + 移除已确认
Step 4: TrimPredictionHistoryThroughFrame()      // :199  裁剪旧预测历史
Step 5: ReplayUnconfirmedInputs()                // :204  重放本地未确认输入
Step 6: RecordAuthoritySnapshotForFrame()        // :208  记录权威快照
Step 7: 视觉子弹生成                              // :211-290  遍历权威帧攻击操作
```

---

## 8. CSP 位置校正与输入重放

### `ApplyAuthoritativePositions()` — `BattleData.Authority.cs:20-81`

从权威帧批次**最后一帧**的 `PlayerStates` 取所有玩家位置，直接覆写 `playerPositon`：

```csharp
// :38-41 坐标翻转判定
bool needFlip = Players[0].teamID != Players[selfIDInServer].teamID;
float sign = needFlip ? -1f : 1f;

// :61 应用权威位置
Vector3 authorityPos = new Vector3(state.PosX * sign, state.PosY, state.PosZ * sign);
Players[playerIndex].playerPositon = authorityPos;

// :69 记录本地玩家权威位置（重放起点）
lastAuthorityPosition = authorityPos;
```

### `ReplayUnconfirmedInputs()` — `BattleData.Prediction.cs:209-256`

以 `lastAuthorityPosition` 为起点，逐帧重放 `authorityFrameId+1` 到 `predicted_frameID` 的本地输入：

```csharp
Vector3 pos = lastAuthorityPosition;             // :224
while (node != null)                              // :229
{
    if (entry.FrameId > authorityFrameId && entry.FrameId <= predicted_frameID)
    {
        // 移动公式与 HYLDPlayerManger.ApplyPlayerOperation 完全一致
        LZJ.Fixed3 tempDir = new LZJ.Fixed3(-mx, 0f, mz);
        LZJ.Fixed3 move = tempDir * 移动速度 * frameTime;
        pos = (new LZJ.Fixed3(pos) + move).ToVector3();
    }
}
Players[selfPlayerIndex].playerPositon = pos;     // :249
```

**为什么需要重放**：权威帧有延迟，覆写位置后本地玩家会"跳回"几帧前的位置。重放本地未确认的输入（predicted_frameID 比 sync_frameID 超前的部分），让位置追回到预测状态。

---

## 9. 子弹系统（视觉表现层）

联网模式子弹**只做视觉**，伤害判定在服务端。

### `HYLDBulletManger.cs`（221 行）

| 函数 | 行号 | 职责 |
|---|---|---|
| `InitData()` | :32-45 | 创建 BulletPool 父节点、加载子弹预制体、初始化 Timer 数组 |
| `OnLogicUpdate()` | :47-54 | 遍历 AllShells 调用 `shell.OnUpdateLogic()`（飞行更新） |
| `changeGun()` | :55-60 | 设置 BulletLogic 的 ownerID + 拷贝 Hero 参数 |
| `AddShell()` | :65-71 | 标记 `isVisualOnly`（联网模式）+ 加入 AllShells 列表 |
| `RemoveShell()` | :61-64 | 从 AllShells 移除 |
| `ResetForReconciliation()` | :76-127 | 和解时保留视觉子弹、销毁非视觉子弹 |
| `SpawnVisualBullet()` | :137-159 | 权威帧/预测路径直接调用生成子弹 |
| `Attack()` | :161-218 | 核心发射逻辑（数据驱动，无硬编码） |

### 双路径子弹生成

**路径 A — 本地预测子弹（零延迟）**：
```
BattleTick() → 检测 selfOperation.AttackOperations
  → SpawnVisualBullet(selfIdx, selfPos, dir, PstolNormal)
  → MarkAttackPredicted(attackId)
```

**路径 B — 权威帧子弹（其他玩家 + 未预测的本地攻击）**：
```
OnLogicUpdate_sync_FrameIdCheck Step 7
  → 遍历 allPlayerOperation.Operations.AttackOperations
  → 跳过已预测的 (_predictedBulletAttackIds)
  → 读取 SpawnPosX/Y/Z（延迟补偿 V2）
  → 计算 elapsedFrames 做半空推进
  → SpawnVisualBullet(playerIndex, bulletSpawnPos, dir)
```

**去重**：`_predictedBulletAttackIds`（HashSet\<int\>），预测时 Add，权威帧到达时 Remove+skip。

### `Attack()` — `HYLDBulletManger.cs:161-218`

重构后的数据驱动版本：

```csharp
Hero hero = Players[playerID].hero;                // :168 取英雄配置

// ── 普通攻击 ──
if (fireState == PstolNormal)
{
    // 数据驱动蓝耗
    if (playerManaValue < hero.normalAttackManaCost) return;  // :175
    playerManaValue -= hero.normalAttackManaCost;              // :179
    playerManaValue += hero.normalAttackManaRecover;           // :180
    changeGun(temp, hero, playerID);                           // :182
}

// ── 大招 ──
if (fireState == ShotgunSuper)
{
    if (hero.isSuperMovingType)                                // :190 移动型大招
    {
        Instantiate(hero.大招实体, ...);                        // :193
        移动型大招.当前英雄 = hero.heroName;                     // :196
    }
    else                                                       // 子弹型大招
    {
        changeGun(temp, hero, playerID);                       // :202
        bulletLogic.bulletPrefab = hero.大招实体;                // :206
        bulletLogic.applySuperParams(hero.superBullet);         // :210 数据驱动覆写
    }
}
```

### `BulletLogic.cs`（280 行）

| 函数 | 行号 | 职责 |
|---|---|---|
| `setBulletInformation(Hero)` | :17-31 | 从 Hero 拷贝普通攻击参数 |
| `applySuperParams(SuperBulletParams)` | :37-51 | 大招参数覆写（-1 值保留普通值） |
| `InitData(callback)` | :56-66 | 设开火点 + Invoke("Shoot") + 5秒自毁 |
| `Shoot()` | :69-86 | 分发：蜜蜂大招/扇形/直线 |
| `ShanxingShoot()` | :87-116 | 扇形发射协程（角度分散+随机抖动） |
| `StraightShoot()` | :117-185 | 直线发射协程（单发/排列/连发） |
| `蜜蜂大招()` | :187-227 | 贝亚大招专用曲线子弹 |
| `ParadolaShoot()` | :229-266 | 设 Layer + 抛物线/直线 + 回调 AddShell |

### `shell.cs`（515 行）

| 函数 | 职责 |
|---|---|
| `InitData(speed, health, callback)` | 禁用 Collider（视觉模式）+ isKinematic + 设移动方向 |
| `OnUpdateLogic()` | 逻辑帧飞行：`Net_pos += moveDir * speed * frameTime`，寿命到期 Die() |
| `FixedUpdate()` | 渲染同步：`transform.position = Net_pos` + 拖尾特效（0.15s 间隔） |
| `Die()` | Destroy + 从 AllShells 移除 |

---

## 10. 伤害判定与死亡系统

### 服务端权威伤害链路

```
服务端 BattleController
  → TickServerBullets / SimulateBulletCatchUp（碰撞检测）
  → playerHp[victim] -= damage
  → HitEvent（isKill=true 当 HP<=0）
  → 随权威帧广播
  → 客户端 HandleMessage
```

### `ApplyHitEvents()` — `BattleData.HitEvent.cs`

**纯表现层**，不修改 HP：

```
1. 去重（_appliedHitEventKeys: attackId*100000+victimBattleId）
2. 触发 bodyAnimator.SetTrigger("Hit")
3. 记录 _hitAnimatedPlayers（供兜底动画判断）
4. IsKill 兜底：强制 playerBloodValue = -1
```

### `ApplyAuthoritativeHpAndDeath()` — `BattleData.HitEvent.cs`

**权威 HP + 死亡判定**（在 ApplyHitEvents 之后调用）：

```
1. 帧序保护：batchLastFrameId <= _lastAuthHpFrameId 时跳过（防 UDP 乱序 HP 回弹）
2. 首次初始化 _playerMaxHp[i] 和 hero.BloodValue
3. HP 覆写：playerBloodValue = state.Hp
4. 兜底受击动画：newHp < oldHp 且不在 _hitAnimatedPlayers → 补播 Hit
5. 死亡判定：state.IsDead && isNotDie → playerBloodValue = -1 + 死亡动画
```

### GameOver 流程

```
服务端：击杀 → oneGameOver=true → HandleBattleEnd → SendFinishBattle(winnerTeamId)
客户端：HandleMessage(BattlePushDowmGameOver)
  → winnerTeamId vs BattleData.teamID
  → 玩家输了吗 = (winnerTeamId != teamID)
  → BeginGameOver() + toolbox.游戏结束方法()
```

---

## 11. 动态追帧系统

### 目标

客户端预测帧号应保持在 `targetFrame = sync_frameID + ceil(RTT/2/frameTime) + inputBufferSize` 附近。超前时减速，落后时加速。

### RTT 测量（`BattleData.Rtt.cs`）

- 每 200ms 发 Ping（UTC 毫秒时间戳）
- 收到 Pong 后 `rttSample = now - timestamp`
- EWMA 平滑：`smoothedRTT = (1-0.125)*old + 0.125*sample`

### `CalcTargetFrame()` — `BattleManger.cs:191-208`

```csharp
halfRttFrames = CeilToInt(smoothedRTT / frameTimeMs / 2f);  // :205
return syncFrame + halfRttFrames + inputBuf;                 // :207
```

### `AdjustTickInterval()` — `BattleManger.cs:216-244`

```csharp
frameDiff = targetFrame - predicted_frameID;             // :219
if (frameDiff < -5) → 暂停（严重超前）                    // :222
targetSpeedFactor = Clamp(1 + frameDiff * 0.05, 0.85, 1.15);  // :231
actualSpeedFactor = Lerp(actual, target, 5 * dt);              // :237
currentTickInterval = 0.016f / actualSpeedFactor;              // :243
```

---

## 12. 玩家移动与渲染层

### 逻辑层：`HYLDPlayerManger.ApplyPlayerOperation(PlayerOperation)` — `:152-194`

```csharp
// :141-145 队伍翻转 sign（敌方坐标取反）
sign = (sameTeam ? 1 : -1);

// :150 方向计算
LZJ.Fixed3 tempDir = new LZJ.Fixed3(-moveX * sign, 0, moveY * sign);

// :166 移动公式（确定性）
move = tempDir * 移动速度 * frameTime;
playerPositon = (Fixed3(playerPositon) + move).ToVector3();

// :170-180 攻击方向：取 AttackOperations 最后一个的 Towardx/Towardy
```

### 渲染层：`HYLDPlayerController.cs`（118 行）

| 函数 | 行号 | 职责 |
|---|---|---|
| `Update()` | :55-99 | 渲染位置追赶逻辑（与逻辑帧解耦） |
| `FixedUpdate()` | :106-116 | 动画参数同步（Animator.SetFloat("Speed")） |

**本地玩家**（`:64-76`）：
```csharp
selfTransform.position = Vector3.MoveTowards(current, logicPos, selfSmoothSpeed * dt);
```
匀速追赶，`selfSmoothSpeed = 30f`，跟手感好。

**远端玩家**（`:77-98`）：
```csharp
if (Distance > maxSmoothableOffset=3.0) → 直接传送（跳位）
else → Lerp(current, target, dt * movespeed=5)
```

---

## 13. 摄像机系统

**文件**：`HYLDCameraManger.cs`（78 行）

### `LateUpdate()` — `:59-77`

```csharp
// :68 读取角色当前渲染位置（Update 中 MoveTowards 已执行完毕）
endPos = selfBody.transform.GetChild(0).position;
endPos.x += tempx;  endPos.y += tempy;           // :69-70 偏移
endPos.z = transform.position.z;                  // :71 Z 轴锁死

// :74-76 SmoothDamp 平滑跟随
pos.x = Mathf.SmoothDamp(pos.x, endPos.x, ref _velocity.x, SmoothTime=0.08f);
pos.y = Mathf.SmoothDamp(pos.y, endPos.y, ref _velocity.y, SmoothTime=0.08f);
```

---

## 14. 英雄配置系统

**文件**：`HYLDStaticValue.cs` — Hero 类 `:454-491` / SuperBulletParams 类 `:393-424`

### Hero 字段一览

| 字段 | 类型 | 含义 |
|---|---|---|
| `Name` | string | 英雄显示名 |
| `heroName` | HeroName | 英雄枚举 |
| `BloodValue` | int | 血量（占位默认值，战斗时由服务端权威） |
| `移动速度` | float | 移动速度（units/sec） |
| `shell` | GameObject | 子弹预制体 |
| `shootDistance` | float | 射程 |
| `shootWidth` | float | 射击宽度（直线模式）/ 爆炸半径（投掷手） |
| `bulletCount` | int | 子弹总数 |
| `bulletDamage` | int | 单发伤害 |
| `LaunchAngle` | float | 扇形角度（0=直线） |
| `speed` | float | 子弹飞行速度 |
| `bulletCountByEachTime` | int | 每次发射数量 |
| `EachTimebulletsShootSpace` | float | 每次发射间隔 |
| `IsParadola` | bool | 是否抛物线 |
| `Boom` | GameObject | 爆炸预制体 |
| `大招实体` | GameObject | 大招预制体 |
| `isSuperMovingType` | bool | 大招是否为移动型（如麦克斯） |
| `superBullet` | SuperBulletParams | 大招子弹参数覆写 |
| `normalAttackManaCost` | int | 普通攻击蓝耗（默认30） |
| `normalAttackManaRecover` | int | 普通攻击蓝回（默认0） |

### 有特殊大招配置的英雄

| 英雄 | 特殊机制 |
|---|---|
| 麦克斯 | `isSuperMovingType=true`，蓝回=3 |
| 贝亚 | 蓝耗=90，大招6发散射（蜜蜂大招） |
| 瑞科 | 大招 shootDistance=14, speed=19, bulletCount=12 |
| 柯尔特 | 大招 shootDistance=12, speed=18, bulletCount=12 |
| 雪莉 | 大招 bulletCount=40（散弹x2），LaunchAngle=40 |
| 格尔 | 大招 shootWidth=4, bulletCount=4, speed=14 |
| 帕姆 | 大招全覆写：抛物线，bulletDamage=300 |

---

## 15. 文件索引与行数统计

| 文件路径 | 行数 | 职责 |
|---|---|---|
| `Scripts/Server/Manger/Battle/BattleManger.cs` | 496 | 战斗主循环、消息分发、动态追帧 |
| `Scripts/Server/Manger/Battle/BattleData.cs` | 290 | 核心数据/单例/帧号/初始化 |
| `Scripts/Server/Manger/Battle/BattleData.Prediction.cs` | 258 | 预测历史/权威确认/输入重放 |
| `Scripts/Server/Manger/Battle/BattleData.Authority.cs` | 307 | 权威帧入口/位置校正/动画 |
| `Scripts/Server/Manger/Battle/BattleData.HitEvent.cs` | 223 | HitEvent/HP 覆写/死亡 |
| `Scripts/Server/Manger/Battle/BattleData.Attack.cs` | 116 | 攻击队列/预测子弹去重 |
| `Scripts/Server/Manger/Battle/BattleData.Rtt.cs` | 57 | RTT 测量/EWMA |
| `Scripts/Server/Manger/Battle/HYLDBulletManger.cs` | 221 | 视觉子弹管理/Attack |
| `Scripts/Server/Manger/Battle/HYLDPlayerManger.cs` | 219 | 玩家逻辑帧移动/镜像 |
| `Scripts/Server/Manger/Battle/HYLDCameraManger.cs` | 78 | 摄像机跟随 |
| `Scripts/Server/Manger/UDPSocketManger.cs` | 141 | UDP 收发 |
| `Scripts/Server/ConstValue.cs` | 37 | 网络参数常量 |
| `Scripts/Manger/CommandManger.cs` | 105 | 命令模式/输入聚合 |
| `HYLD1.0/Scripts/OldScripts/TouchLogic.cs` | 231 | 摇杆输入采集 |
| `HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs` | 118 | 渲染位置追赶/动画 |
| `HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs` | ~500 | 全局数据/Hero/PlayerInfo |
| `HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/BulletLogic.cs` | 280 | 发射逻辑 |
| `HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs` | 515 | 子弹飞行/特效 |

---

## 16. 端到端数据流图

### 一次普通攻击的完整旅程

```
[玩家松手摇杆]
     │
     ▼
TouchLogic.JoystickMoveEnd()
     │  CommandManger.AddCommad_Attack(dx, dy)
     ▼
CommandManger.Execute()
     │  AttackCommad.Execute() → BattleData.EnqueueAttack(dx, dy)
     │  FlushPendingAttacksToOperation() → selfOperation.AttackOperations
     ▼
BattleManger.BattleTick()
     │  ★ 路径A: SpawnVisualBullet(selfIdx, selfPos, dir)  ← 本地预测子弹（零延迟）
     │  RecordPredictedHistory + ApplyLocalPredictedInputAndRefreshAllObjects + CommitPredictedFrame
     │  SendOperation(uploadOperationId)
     ▼
UDP 上行 ──────────────────────────────────────────────→ 服务端
                                                          │
                                                          ▼
                                              UpdatePlayerOperation
                                              inputBuffer[frame%2] = op
                                                          │
                                              CollectAndBroadcast(frameid=F)
                                              SpawnServerBullets(攻击者历史位置)
                                              TickServerBullets / SimulateBulletCatchUp
                                                          │ 命中 → HitEvent
                                                          ▼
客户端 ←────────────────────────────────────── 权威帧广播（含 PlayerStates + HitEvents）
     │
     ▼
UDPSocketManger.DrainAndDispatch()
     │  Handle → BattleManger.HandleMessage()
     ▼
OnLogicUpdate_sync_FrameIdCheck()
     │  Step 1: ApplyAuthoritativePositions    ← 权威位置覆写
     │  Step 3: ConfirmAttacks                 ← 移除已确认攻击
     │  Step 5: ReplayUnconfirmedInputs        ← 本地输入重放
     │  Step 7: ★ 路径B: SpawnVisualBullet    ← 其他玩家子弹
     │          （跳过已预测的 _predictedBulletAttackIds）
     ▼
ApplyHitEvents()                               ← 受击动画（纯表现）
     │
     ▼
ApplyAuthoritativeHpAndDeath()                 ← 权威 HP + 死亡
     │  state.IsDead → playerDieLogic()
     ▼
[战斗结束 or 继续]
```

### 一帧内的执行顺序（渲染帧）

```
Update()
  ├─ UDPSocketManger.DrainAndDispatch()     // 消费 UDP 队列
  ├─ Ping 调度（每 200ms）
  ├─ CalcTargetFrame + AdjustTickInterval   // 动态追帧
  └─ while(accumulator >= interval)
       └─ BattleTick()                     // 逻辑 Tick（可能 0-3 次）
            ├─ CommandManger.Execute()
            ├─ 本地预测子弹
            ├─ ApplyLocalPredictedInputAndRefreshAllObjects() // 仅预测本地输入/刷新全体对象
            └─ SendOperation()              // UDP 上行

HYLDPlayerController.Update()
  └─ MoveTowards / Lerp                    // 渲染位置追赶

FixedUpdate()
  ├─ HYLDPlayerController.FixedUpdate()    // 动画同步
  ├─ BattleManger.FixedUpdate()            // cameraManger.OnLogicUpdate()（空）
  └─ shell.FixedUpdate()                   // transform.position = Net_pos

LateUpdate()
  └─ HYLDCameraManger.LateUpdate()         // 摄像机 SmoothDamp
```
