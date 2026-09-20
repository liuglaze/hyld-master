# R4-B / B3 真实 Unity DS 权威运动验收（_r4b_real_unity_movement）

> 范围：只改 `Tools/PMR3UnitySmoke/Program.cs` 与 `Tools/PMR3UnitySmoke/PMR3UnitySmoke.csproj`，新增本报告。
> **未改任何生产代码/资产/场景**；未提交、未 `svn update`、未编译 UE、未关编辑器/杀其它进程、未递归委派。
> 语言面：net8.0 工具（纯 C#，不引 UnityEngine）；`Program.cs` 保持 UTF-8 BOM + CRLF，`.csproj` 沿用仓库既有约定（无 BOM + LF）。
> 状态与验收以 `Docs/plans/net-architecture-migration.md` 的 P4B6 为准；本文是该阶段**本轮**的实施与证据报告。

---

## 0. 摘要

1. **S109b 精确修口径（不削弱强杀/非 0 拒绝）**。旧检查只认「协调器在优雅退出宽限内观测到进程自行退出（`Observed>=1`）」，把另一条**同样合法**的时序判失败：进程在协调器进终态 `Exited` **之前**已自行退出（`Waits=0`，宽限窗口从未建立，`Observed` 天然为 0）。新口径三条**共同**前提：`OS 退出码==0`、`KillRequests==0`、资源真实回收（`SessionReleased` + 端口池 0 + uid 账本 0 + 两 uid 释放 + `!ResourcesHeld`）；再分支：`Waits>0` ⇒ 必须 `Observed>=1` 且 `Timeouts==0`；否则 ⇒ 必须「OS 退出观测时刻 <= 终态时刻」。`Timeouts>0` 在两条分支下都失败。两条时序本轮**都被真实跑出**（§3）。

2. **新增 `--movement` 模式**（默认 smoke 与 hold/drop 保留，三者互斥）。复用真实 LobbyHost + 真实进程启动 + 两个纯 C# 协议客户端，不造 DS 替身；`csproj` 逐个链接 `PMPrediction/**`、`PMMover/**`、`PMR4MovementCodec/Driver`（不通配 `PMR3\**`，避免与既有 Include 重复编译）。客户端 AP 用 R4-A **AABB 替身**（`PMMoverTestWorld`，`WorldVersion` 包装为 1）——**不是 PhysX**，只用于产出行输入；权威断言全部读**真实 Unity DS 复制载荷**与 owner 可靠事件。节拍用真实 Stopwatch 墙钟（16ms），输入 dt 恒 16ms，DS 预算由 DS 自己墙钟累计。

3. **实测（真实 Unity DS/PhysX 隔离场景）**：Create 初值非空且出生点正确；每端 1 AP + 1 SP；输入驱动真实位移；**撞墙停在 `x=-0.901`、全程 `min|x|=0.901`（不穿墙）**、`z` 不被推动；**Space 起跳峰值 `y=1.783808`** 后**落回 `y=1` 且 `Walking`**；owner 经可靠通道收到 **`ModeChanged`×2（→Falling/→Walking）+ `Landed`×1**，Key 无重复（2105/2497/2498）；对手 SP 收 2809 个权威快照并独立复现撞墙/起跳/终态（Δ=0）；升流 `1→2` 后**旧流输入完全无效**（同帧号同结构仅 stream 不同，27 帧反向输入权威 x 不动），**新流对照被接纳并驱动 -3.145m**。

4. **门禁**：构建 0 错误；默认 smoke **128/0 exit 0（745ms）**；`--movement` **232/0 exit 0（12285ms）**；两跑都走完「结果 → ResultAck → 认证 Exited(0) → OS exit 0（Kill 0）→ 端口/uid 回收」。

5. **诚实边界**：客户端是纯 C# 协议客户端（非 UnityUI、无表现层/相机），跨机与 UI 分流仍属 P4B6；AP 碰撞世界是替身，不作 PhysX 等价声明；真实 Physics 菜单通过属用户证据（§10 单列）。

---

## 1. 必读文档与实际阅读次序

| 顺序 | 文档 | 对本次落点的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口，确认三份下游文档 |
| 2 | `Client/Assets/AGENTS.md` | 客户端坐标系（X 水平 / Z 纵向 / Y 固定高度）、`frameTime=0.016f`、联机链路现状 |
| 3 | `Server/AGENTS.md` | 服务端 Lobby/NetSim/BattleLoop 现状与「服务端不含 Unity」的历史边界 |
| 4 | `Docs/plans/net-r4-network-contract.md` | **本批冻结依据**：§B1 承载/上限/流代次/事件契约；§B2 Unity 适配器；§B3 主侧集成（含末尾复核修订：出生点墙外 x=±3/z 分行/y=1、`Aux.WorldVersion` 统一 1、`endpoint.Pump` 前一次 `Physics.SyncTransforms`、Input 在 Tick 真实接受后 Consume） |
| 5 | `Docs/plans/_r4b_host_integration_report.md` | B3 宿主接线次序、Driver 冻结 API 清单、`MovementWorldVersion=1`、测试出生点规则、释放链 |
| 6 | `Docs/plans/_r4b_physx_zero_hit_fix.md` | PhysX 零距离命中修复：贴地水平可走、起跳不被抵消、墙 TOI≈0.7、`minX≈0.90099996`、`maxY=1.7838081` —— 本轮真实 DS 撞墙/起跳值逐位吻合 |
| 7 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | bash/PowerShell 边界；命令一律按 Git Bash 写 |

**由文档决定的首搜入口**（非盲搜）：Driver/Codec 公开面 → `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`、`PMR4MovementCodec.cs`；DS 权威侧 → `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（`MovementWorldVersion`、`BuildSpawnSync`、`PMR3TestScene` 几何）；客户端消费侧 → `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`（每帧 `driver.Update` 次序）；运动数学/替身几何 → `PMMover/PMMoverModel.cs`（Walking 平面 `MoveTowards(MaxSpeed=3.9, Accel=20, Jump=4, g=9.8)`）、`PMMover/PMMoverTestWorld.cs`；主计划只读卷首/尾部 R4-B 状态，未重跑历史调查。

---

## 2. 交付物与写入边界

| 文件 | 动作 | 说明 |
|---|---|---|
| `Tools/PMR3UnitySmoke/Program.cs` | 改 | (a) S109b 新口径；(b) `--movement` 模式：碰撞替身包装、运动观察台、权威快照轨迹、真实输入 RPC 驱动、撞墙/起跳/事件/SP/重同步 A/B 验收；(c) DS 运动符号预检记录；(d) 运动证据落盘 |
| `Tools/PMR3UnitySmoke/PMR3UnitySmoke.csproj` | 改 | 新增 `PMPrediction/**`、`PMMover/**`、`PMR4MovementCodec.cs`、`PMR4MovementDriver.cs` 的显式 `Compile Include`（不通配 `PMR3\**`）；头部注释与运行说明补 `movement` 模式 |
| `Docs/plans/_r4b_real_unity_movement.md` | 新增 | 本报告 |

**只读未改**：`PMR4MovementDriver.cs`、`PMR4MovementCodec.cs`、`PMR3Player.cs`、`Generated/*.g.cs`、`PMPrediction/**`、`PMMover/**`、`PMUnity/**`、`Server/DS/**`、两个宿主、`Server/Server.csproj`、主计划与三份契约文档、任何 UE 工程文件、任何 `.meta`（未新增 Unity 资产）。未执行 `svn`/`git` 写操作。

---

## 3. S109b：精确修口径与两条合法时序的真实证据

**旧判据的漏洞**：只接受「宽限内自行退出」。但真实 DS 收到 `ResultAck` 后是**先发认证 `Exited(0)`、随即 `Application.Quit`**，进程退出与终态记录之间存在毫秒级竞争：协调器处理 `Exited` 消息时进程可能**已经**不在了，于是 `Finalize` 的 `graceStarted=false`、`TryFinishTerminalCleanup` 直接成功释放资源，`GracefulExitWaits` 与 `GracefulExitObserved` **都保持 0**。这是**合法且更干净**的时序，却被旧检查判死。

**新判据**（`Program.cs` 的 S109b）：
```
共同前提 exitEvidenceOk   = 取到 OS 退出码 && 退出码 == 0 && KillRequests == 0
共同前提 resourcesReclaimed = SessionReleased && 端口池 InUseCount == 0
                              && uid 账本 Count == 0 && 两个 uid 均未占用 && !ResourcesHeld
分支 A（GracefulExitWaits > 0）：Observed >= 1 && Timeouts == 0
分支 B（GracefulExitWaits == 0）：osExitObservedAtMs > 0 && <= exitedTerminalAtMs
S109b 通过 = exitEvidenceOk && resourcesReclaimed && (分支 A 或 分支 B)
```
**没有削弱任何拒绝**：`S104`（`exit==0`）、`S104d`（`KillRequests==0`）、`S104e`（`Timeouts==0`）与 `S99`（认证显式 `Exited(0)` 才是终态）全部原样保留；分支 A 下 `Timeouts>0` 仍然失败，分支 B 下 `KillRequests>0` 或 `exit!=0` 也仍然失败。

**两条时序都被真实跑出**（同一份 DS 二进制、同一工具）：

| 证据 | 跑次 | `KillRequests` | `Waits` / `Observed` / `Timeouts` | `osExit` vs 终态 | S109b 走的分支 |
|---|---|---|---|---|---|
| 默认 smoke（`artifacts/20260920-193252-acb972b8`） | 修好后首跑 | 0 | 0 / 0 / 0 | `...973340` **早于** `...973342` | **B**（无宽限窗口） |
| 默认 smoke 最终（`artifacts/20260920-194014-82da365e`） | 最终 | 0 | 2 / 1 / 0 | `...415618` 晚于 `...415592` | **A**（宽限内自行退出） |
| `--movement` 最终（`artifacts/20260920-194025-24c1918f`） | 最终 | 0 | 2 / 1 / 0 | `...437872` 晚于 `...437844` | **A** |

三跑 `S109b` 全 OK。上表第二行与第三行就是「旧判据会误报」的那条路径的**反例**：旧判据在 193252 那次 `Observed=0` 会失败（`summary.txt` 记录的失败原文为 `S109b 进程在优雅退出宽限内自行退出（全程未强杀；实际 0）`）。

---

## 4. `--movement` 模式：实现与口径

### 4.1 装配（与真实宿主同次序）

```
真实 PMDsLobbyHost（control=系统临时端口）→ 真实 PMDsSystemProcessLauncher 拉起 HyldDS.exe
（唯一包装缝 = 参数尾部追加 -server-smoke / -logFile）
→ 真实 Ready → 真实 PMDS1 offer（真实 PMDsEntryCodec 往返）
→ 两个纯 C# 客户端真实 UDP 握手/验票/复制（客户端侧 Enter/Pump 次序对齐 PMClientSessionHost）
→ 【--movement】探针闸门保持关闭 → 建运动观察台 → 运动验收
→ 开闸 → 原 smoke 闭环（探针 → ProbeCount/Echo 收敛 → DS 提交结果 → ResultAck
   → 认证 Exited(0) → OS 退出 0 → 端口/uid 回收）
```

每客户端观察台 = 本端副本（AP，真实预测时间轴）+ 对手副本（SP，真实插值缓冲），两个都是**真实 `PMR4MovementDriver`**（`CreateFromPlayerInitialSnapshot` 从 Create 初值建立）。每拍（真实墙钟 16ms）：
`driver.Update(elapsedMs)`（**入站队列的唯一排空入口**：复制快照 / 可靠事件 / 重同步载荷都在这里被应用）→ AP `Tick(16, input, serverFrame)`（**接受才** `SendInputPayload()`）→ `PumpAll()`（两条 UDP + 控制面）。

客户端 AP 的碰撞查询是 `MovementCollisionStub`：把 `PMMoverTestWorld`（40×40 地板中心 `(0,-0.5,0)`、墙 `1×2×8` 中心 `(0,1,0)` ⇒ `x∈[-0.5,0.5]`）逐方法委托，**只覆盖 `WorldVersion=MovementCollisionWorldVersion=1`**。原因：DS 的 `PMUnityMoverCollisionQuery`（真实 PhysX）写死 1，而 `PMMoverTestWorld` 沿用 R3 冻结摘要 `0x52334201`；不覆盖会让 `CreateFromPlayerInitialSnapshot` 以「碰撞世界版本不符」拒绝建 Driver。**这不是 PhysX 等价声明**：它只让 AP 能产出行输入并做回滚重放。

### 4.2 验收项（M0–M61，共 104 项检查）

| 组 | 检查 | 判据要点 |
|---|---|---|
| M0–M6 | 探针闸门 / 角色构成 | 验收开始时 `ProbeNonceSent==0`；每客户端恰 1 AP + 1 SP；AP 有预测时间轴、SP 无（只插值） |
| M7–M13d | **Create 初值** | Create 记录自带 151 字节非空运动载荷，可按 V1 完整解码；`streamVersion=1`、`epoch` 一致、`OutputFrame=0`、`Aux.CollisionWorldVersion=1`、`InstanceId=NetId`、`Walking+Grounded`、出生点 `(-3,1,-0.75)` / `(-3,1,0.75)` 且 `|x|=3`（墙外）`|z|<=4`（墙的 z 范围内，撞墙用例才成立） |
| M14–M18 | **撞墙** | 输入 `MoveX=+1` 驱动 ⇒ 权威 `min|x| >= 0.89`（**不穿墙**）；终态 `|x|` ∈ `0.9±0.06`（实测 `-0.901` = 墙面 0.5 + 真实 PhysX 胶囊半径 0.4）；`z` 不被推动；`Walking+Grounded`；`|Δx|>1m`（真实输入驱动位移） |
| M19–M23 | **起跳** | 全程 `TicksRejected==0`（边沿不丢）；Space 边沿 ⇒ 权威峰值 `y>1.5`（实测 `1.783808`）；期间出现过 `Falling`；落回 `y≈1`（实测 1）；终态 `Walking+Grounded` |
| M24–M29 | **owner 可靠事件** | 经 `ClientMovementEventsV1` 到达并被日志接纳；`ModeChanged` ≥2（含 →Falling 与 →Walking）、`Landed` ≥1（值 = Walking）；**Key 无重复**；`journal.Failed==false && SequenceGaps==0` |
| M30–M35 | **对手 SP 副本** | SP 收到真实 DS 快照（实测 2809 样本）；独立复现撞墙 `0.901` / 起跳 `1.783808`；与 AP 侧终态 `Δx=Δy=0`；`Interpolation.AuthorityAccepted>0`；`SamplePresentation().HasValue` 且贴最近权威（差 0） |
| M36–M44 | **重同步升流 A/B** | DS 升流 `1→2` 被复制快照观测到；AP `ResyncApplied 0→1` 且 `driver.StreamVersion==2`；**旧流**合法 V1 载荷（帧号正是 DS 期望的下一帧）反向输入 27 帧 ⇒ 权威 `x` **完全不动**；**对照组**同帧号、同结构、仅 `streamVersion=2` ⇒ 被接纳并驱动 `-3.145m`、边界 `393→449`；全程只升一次流 |
| M60–M61 | 隔离 | 验收期间 `ResultAccepted==0`（探针闸门挡住 smoke 提前结束）；真实 DS 进程始终存活 |

---

## 5. 实测命令与计数（本机真实执行）

```bash
dotnet build Tools/PMR3UnitySmoke -c Release -v q -nologo                     # 0 警告 / 0 错误
dotnet Tools/PMR3UnitySmoke/bin/Release/net8.0/PMR3UnitySmoke.dll             # 128 项检查 0 失败 exit 0（总耗时 745ms）
dotnet Tools/PMR3UnitySmoke/bin/Release/net8.0/PMR3UnitySmoke.dll --movement  # 232 项检查 0 失败 exit 0（总耗时 12285ms）
dotnet Tools/PMR3UnitySmoke/bin/Release/net8.0/PMR3UnitySmoke.dll --drop-control-after-ready  # 89 项检查 0 失败 exit 0（回归；总耗时 20648ms）
```

| 跑次 | 模式 | 计数 | exit | 总耗时 | artifacts |
|---|---|---|---|---|---|
| 20260920-193252-acb972b8 | 默认（S109b 首验） | 128 / 0 | 0 | 689ms | 含 `summary.txt` |
| 20260920-194014-82da365e | 默认（最终） | 128 / 0 | 0 | 745ms | 含 `console-smoke.log` |
| 20260920-194025-24c1918f | `--movement`（最终） | 232 / 0 | 0 | 12285ms | 含 `console-movement.log`、`movement-evidence.txt`（1.0MB） |
| 20260920-194328-df5888ea | `--drop-control-after-ready`（回归） | 89 / 0 | 0 | 20648ms | 含 `console-drop.log`；真实 DS 退出码 1、停下行 15111ms |

`--movement` 的运动窗口（真实墙钟，非虚拟）：approach-wall 2400ms / settle 500ms / drain 200ms / back-off 800ms / settle 500ms / drain 200ms / jump 1500ms / drain 350ms / settle-for-resync 600ms / drain 250ms / 旧流窗口 420ms（27 帧）/ 新流对照 900ms（56 帧）/ drain 250ms。整局 12.3s，**远小于 120s 预算且未逼近票据期限**。

### 5.1 真实 DS 构建预检（固定记录）

`summary.txt` / `preflight.txt` 记录：`assemblyMtime=2026-09-20 19:16:14`、`assemblySize=1170432`、`assemblySha256=8abc8dacf1ae014e4b16ebf04fd1c0c479ecaa59241b6ea0232e544de06747da`（与本轮给定值逐位一致）；`protocolHash=0xDDC346C5`、`collisionDigest=0x52334201`；`exe=D:\UGit\hyld-master\HyldDS\HyldDS.exe`。

新增**运动符号**预检（`--movement` 下逐项硬判定，默认模式只记录）：
```
PMR4MovementDriver=1 PMR4MovementCodec=1 PMUnityMoverCollisionQuery=1 MovementWorldVersion=1
BuildIsolated=1 PendingServerFrame=1 ZeroDistanceContactsIgnored=1 ServerMovementInputV1=1 ClientMovementEventsV1=1
```
DS 日志侧证据：`R4-B 运动接线：碰撞白名单=2（地板+墙） WorldVersion=1 隔离=本地物理场景([PMDsAuthority]Physics1 handle=-78 isDefaultWorld=False) 每帧 Physics.SyncTransforms 一次 + 逐副本 authority Pump（预算 8步/100ms，实际墙钟）`、`R4-B 出生规则：x=±3（墙外）…`、`玩家副本已创建 uid=301 playerId=1 netId=1 … 出生=(-3,1,-0.75)` / `uid=302 … 出生=(-3,1,0.75)`。

---

## 6. 权威位置轨迹（真实 DS 快照，AP 侧客户端 A）

摘自 `movement-evidence.txt`（列：`outputFrame,stream,totalMs,x,y,z,mode,grounded,vel`）：

| 时刻 | 事件 | 权威状态 |
|---|---|---|
| of=0 | Create 初值 | `stream=1 pos=(-3,1,-0.75) Walking grounded vel=(0,0,0)` |
| of=40 `totalMs=640` | **撞墙停住** | `pos=(-0.901,1,-0.75) Walking grounded vel=(3.9,0,0)`（输入持续推 +X，位置被墙面挡住） |
| of=287 `totalMs=4592` | **起跳峰值** | `pos=(-4.020043,1.783808,-0.75) Falling !grounded vel=(0,0.076,0)` |
| of=312 `totalMs=4992` | **落地** | `pos=(-4.020043,1,-0.75) Walking grounded vel=(0,0,0)` |
| of=393 | 重同步升流后的首个新流快照 | `stream=2 pos=(-4.020043,1,-0.75) Walking grounded` |
| of=449 `totalMs=7184` | 新流对照结束时 | `stream=2 pos=(-7.164997,1,-0.75) Walking grounded vel=(-3.9,0,0)` |

全轨迹统计：`min|x| = 0.901`（**从未 <0.89 ⇒ 未穿墙**）、`max y = 1.783808`、`z` 恒 `-0.75`。撞墙位移 `-3 → -0.901 = 2.099m`（额定时速 3.9 m/s + 加速度 20 m/s² 的手算量级）、回退位移 `-0.901 → -4.020043 = 3.119m`、新流对照位移 `-4.020043 → -7.164997 = 3.145m`。

**owner 可靠事件（客户端 A）**：`kind=1 value=1 key=2105`（→Falling）、`kind=1 value=0 key=2497`（→Walking）、`kind=2 value=0 key=2498`（Landed，值=Walking）。三 Key 互不重复；`journalDuplicateEvents=0`、`journalDispatched=3`、`journalSeqGaps=0`、`journalFailed=False`。客户端 B 同构（`key` 亦为独立流内的稳定值）。

**SP 侧（客户端 A 看到的 uid=302 副本）**：2809 个真实快照样本，独立复现 `min|x|=0.901` / `maxY=1.783808` / 与 AP 侧终态 `Δx=Δy=0`；`Interpolation.AuthorityAccepted=2809`、`SamplePresentation().HasValue=True` 且贴最近权威（差 0）⇒ **只插值、不外推**。

**重同步 A/B 的机制证据**（`apCounters`）：`snapshotRejectedStreamNewer=8`（普通复制快照携带更新的 `streamVersion=2` 时，AP **拒绝自动重绑**，必须等显式重同步）、`resyncPayloadsReceived=1`、`resyncApplied=1`、`resyncRejectedRegressing=0`、`resyncRejectedOldStream=0`、`resyncInlineRejected=0`、`rebindFallbacks=0`、`threadViolations=0`。旧流注入包先在本端用 `PMR4MovementCodec.TryDecodeInputs` 校验为**合法 V1 载荷**（帧号 = DS 期望的下一帧，只有 `streamVersion` 与当前流不同），因此「旧流无效」不是靠畸形包达成的；对照组的**同一批帧号**换成新流即被接纳并驱动位移，差别只能来自流代次。

---

## 7. 资源证据（两跑一致）

`summary.txt`：`osExitCode=0`、`osExitObservations=1`、`coordinatorKillRequests=0`、`coordinatorGracefulExitTimeouts=0`、`exitedTerminal=True`、`processExited=True`、`sessionReleased=True`、`postExitPortProbeError=<ok>`（退出后独占 bind 本局 UDP 端口成功 ⇒ OS 真的回收）、`coordinatorKillRequests=0`、`checks/passed/failed` 如 §5 表。运行期 `midRunPortProbeError` 为「每个套接字地址只允许使用一次」⇒ DS 确实持有该端口。`S106/S107/S108/S109`（端口池 0 / uid 账本 0 / 两个 uid 释放 / `!ResourcesHeld`）全 OK；`S113` 退出后独占 bind 成功。默认模式的 `S74–S121` 全绿，`--movement` 模式下 `M0–M61` 与 `S74–S121` 全绿。

---

## 8. 首轮 `--movement` 的 10 项失败与根因（诚实记录，已修，**未改生产**）

首轮 `--movement` 为 `224 通过 / 10 失败`，失败全在重同步组（`M36/M37/M42/M43/M44` × 两客户端）。逐条用诊断计数定位，**根因两处都在本工具的运动驱动次序**：

1. **漏了宿主每帧的 `driver.Update(elapsedMs)`**。`MovementDriveStep` 当时只调 `Tick`（`Tick` 内部虽会 `ApplyPending`，但重同步等待窗口里不 Tick），于是入站可靠载荷一直躺在 `_pendingReliable` 里没被应用：诊断显示 `resyncPayloadsReceived=1` 而 `resyncApplied=0`、且所有 `ResyncRejected*` 全为 0（即**根本没走到** `ApplyResync`）。修法：新增 `MovementPumpDrivers(elapsedMs)`（对齐真实 `PMClientSessionHost.PumpMovement`：全部 Driver `Update` → SP 再 `Advance`+`SamplePresentation`），并在每个窗口/排空/手工窗口调用。修后 `resyncApplied=1`、`driver.StreamVersion=2`。
2. **手工注入的帧号对齐错一帧**。重绑后 AP 的 `PendingFrame` 与 DS 的 `_nextInputFrame` 都等于重同步快照的 `OutputFrame`（契约「输入帧 n 产出边界 n+1」），因此 DS 期望的下一帧是 `OutputBoundary` **本身**而非 `+1`；写成 `+1` 会让 DS 看到缺帧、500ms 后触发**多余的第二次升流**。修后两组窗口都从同一期望帧起，`M44`（只升一次流）通过。
3. 顺带修正：复制更新在 UDP 上可能轻微乱序到达，「最后 append 的样本」不一定最新，故把「终态读取」改为取**输出边界最大**的样本（`NewestAp/NewestSp`），消除偶发飘忽。
4. 另把手工窗口的每拍帧数从 4 降为 **1**：DS 的长期消耗率就是 1 帧/16ms 真实墙钟，一次多帧会把 DS 内部队列（容量 128 / 未来窗口 128）越推越满，反而让后面的帧被当成「远未来」拒收——那就不再是「流代次」这一个变量了。

**这 10 项失败不构成生产缺陷**：没有触碰任何生产文件；`resyncApplied` 的缺失由工具漏调 `Update` 解释，且真实宿主本来就每帧调用它。

---

## 9. 诚实边界 / 未覆盖

**已证（本轮）**：`--movement` 在**真实 Unity DS 进程**（真 PhysX 隔离物理场景、真无头包、真 UDP、真生成桩）上完成「Create 初值 → 输入驱动位移 → 撞墙不穿墙 → 起跳落回 → owner 可靠事件去重 → 对手 SP 副本 → 重同步升流 + 旧流无效/新流推进」整条链路；且验收完仍能回到原 smoke 闭环并**真实 OS exit 0、0 次强杀、资源全回收**。

**未覆盖 / 待办（不得据此宣称 P4B6 完成）**：

1. **真实 UnityUI 与跨机**：本轮客户端是**纯 C# 协议客户端**（`ClientHarness`），没有 `HYLDManger`/UI 分流/表现层胶囊/相机；`UIMatchingPanel` 的 `PMDS1:` 分支、真机跨机（非 loopback）仍是 P4B6 待办。
2. **客户端表现层语义未在 Unity 内验证**：`PMUnityMoverPresentation.Apply/Dispose`、`PMUnityMoverInput` 的 WASD/Space 采样、真实相机，只由 B2 的真实编译面保证；本轮客户端输入是测试显式给出的 `PMMoverInput`。
3. **客户端 AP 的碰撞世界不是 PhysX**：`MovementCollisionStub`（委托 `PMMoverTestWorld`，`WorldVersion=1`）只用于产出行输入；**所有权威断言读 DS 复制载荷**。两侧世界版本的**一致性**被真实走到（DS 初值 `Aux.CollisionWorldVersion=1`，不符即拒建 Driver），但「替身与 PhysX 几何等价」不做任何声明。
4. **早于本轮的一次真实 Unity PhysX 菜单通过属用户证据**（见 §10），不由本 Agent 运行或宣称。
5. **`--movement` 与 `--hold-seconds` / `--drop-control-after-ready` 互斥**（组合即 `S0b` 失败）。本轮**重跑了 `--drop-control-after-ready` 回归**（真实 DS 停下行后以**非 0** 退出码 `1` 退出、`>9s`、仍完成 `SessionReleased`/端口/uid 回收：**89 项 0 失败 exit 0**）；`--hold-seconds` 本轮未重跑（其探针闸门条件与 drop 共用，drop 已覆盖），上次真实跑为 `artifacts/20260920-153616-71873010`。两模式逻辑未被本次改动触碰（只在探针闸门条件**新增** `|| _movementMode`，默认模式行为不变，已由默认跑 128/0 佐证）。
6. **`journalLateDispatches`** 在个别跑次非 0（事件迟于快照仍被派发）——这是契约允许的（事件必须独立于快照确认游标），不构成失败；`SequenceGaps`/`Failed` 恒为 0。
7. **R3 冻结碰撞摘要 `0x52334201` 与 DS `MovementWorldVersion=1` 仍是两份常量**（分别用于 R3 场景摘要与 R4-B 运动世界版本），一致性靠本工具的运行期校验与 DS 的启动期校验守护，不是编译期保证。

---

## 10. 用户真实 Physics 菜单证据（单列，非本 Agent 运行）

用户在真实 Unity 编辑器 Play 模式执行 `Tools / PMR4 / 验证 Unity 碰撞适配（真实 PhysX）`，结果为 `PASS=30 FAIL=0`（承接 `_r4b_physx_zero_hit_fix.md` §7.1 的 PENDING_USER）。该结论**由用户提供并已重新打包**（本轮使用的 `Assembly-CSharp.dll` 即用户重 build 的 `2026-09-20 19:16:14 / 1170432 / 8abc8dac…`）。本 Agent **未**启动 Unity、未抢编辑器锁、未复跑该菜单，故不据此宣称任何本 Agent 的验证结果。

---

## 11. 建议下一步

1. 主侧可在**同一份 DS 二进制**上复跑 `--movement` 做独立复核；`movement-evidence.txt` 给出逐样本权威轨迹、owner 事件与全部 Driver 计数，可逐值对照。
2. 若要覆盖「真实 UnityUI 客户端 + 跨机」，应作为 P4B6 的独立批次（需要真机/多机与环境准备），不在本轮范围。
3. 生产侧仍保留两项登记：`PMMoverModel` 的「足底真穿透 ⇒ 水平冻结」（`_r4b_physx_zero_hit_fix.md` §3.4）与 `0x52334201 / 1` 双常量口径统一（见 §9.7）。
