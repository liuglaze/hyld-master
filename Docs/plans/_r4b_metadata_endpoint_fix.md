# R4-B：D4/D5 最小修法落地复核报告（PendingServerFrame / Endpoint.IsDisposed）

> 任务性质：前组已完成实现，本组为**取消后续接**的复核+收尾（build/run/报告）。
> 结论口径：新增逻辑与新增测试**真实、语义正确、与既有行为不冲突**；未发现需修复缺陷，未重写任何正确代码。
> 本次唯一写入文件是本报告；其余目标文件均为只读核验。

## 0. 结论摘要

- 两个测试工程在**当前工作区源码**上全量重建（`-t:Rebuild`）**0 警告 0 错误**；运行结果：
  - `PMPredictionTest`：**490 项通过 / 0 失败**，进程 exit=0（含新增 Section N 37 项）。
  - `PMUdpAdmissionTest`：**382 项通过 / 0 失败**，进程 exit=0（含新增 Section J 22 项）。
- 取消后恢复核验通过：4 个文件 md5 与主侧磁盘核验值**一致**；全部 UTF-8 **BOM + CRLF**、无游离 LF；
  `/tmp/r4bmeta/*.bak` 与工作区文件哈希相同 ⇒ 负向注入已被**干净还原**；仓库内无 `.bak` 残留。
- 两个新增 API 语义核验通过：`PendingServerFrame` 为纯只读、不深克隆、与 `GetSnapshot().ServerFrame` 逐次一致；
  `IsDisposed` 为纯只读快照，启动 false、`Dispose` 后 true、幂等，且不改变 `Dispose`/`Pump` 任何既有分支。
- **未完成项（属并行宿主组范围，本次未触碰）**：D4 的宿主消费（`PMClientSessionHost.cs:565` 仍用 `GetSnapshot()`）
  与 D5 的宿主 fail-closed（`PumpActive` 未读 `Endpoint.IsDisposed`）均**尚未接线**，故两者的实际收益尚未生效。

## 1. 必读证据（按委派指定顺序实读）

| 顺序 | 文件 | 从文档中取得的绑定 | 对本次复核的作用 |
|---|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 客户端/服务端/迁移计划文档路由 | 定位后续必读入口 |
| 2 | `Client/Assets/AGENTS.md` | 客户端架构、`Assets/Scripts` 新链路与 OldScripts 并存、帧时长 16ms | 确认 Timeline 属"新链路"、不改旧逻辑 |
| 3 | `Server/AGENTS.md` | 服务端帧循环/ClientMove 时间轴口径（ServerFrame / ClientMoveFrame / AckedServerFrame 命名空间分离） | 用于核对 `PMFrameDomain.AuthorityServer` 的 PendingServerFrame 语义 |
| 4 | `Docs/plans/net-r4-network-contract.md` | R4-B 冻结契约：B1 网络组边界、身份=(SessionEpoch, NetId)、StreamVersion 代次、快照只保证收敛、事件独立游标 | 确认 D4/D5 是**宿主侧**契约问题，API 必须保持只读与签名 |
| 5 | `Docs/plans/_r4b_host_final_review.md`（重点 D4/D5） | C9(D4)：AP 每子步 `GetSnapshot()` 仅为取一个 ServerFrame 却深克隆 Sync/Aux，量级"每渲染帧最多 8 子步 × 1 次 + 每帧 1 次"；C10(D5)：`_disposed` 置位后 `Pump` 首行早退，`ClientHandshakeTick`/`CheckClientSessionAfterUpdate` 永不执行，宿主不失败也不 Pump；两者的**最小修法原文**（增只读 `PendingServerFrame`；端点暴露"已释放"只读标志） | 本次复核的**判定基准**与签名来源 |
| 6 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | Pi `bash` 工具按 Bash 解析；需要 PowerShell 时显式 `pwsh.exe`；不混用 cmd 语法 | 本次命令全部为 Bash 语法（`dotnet`/`grep`/`md5sum`/`python`），未发生 shell 混用 |

## 2. 冻结契约（并行组正在消费，签名必须保持）

```csharp
// Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs:339  （类 PMPredictionTimeline<TInput, TSync, TAux>）
public PMFrameId PendingServerFrame { get { return GetBoundaryServerFrame(_pendingFrame); } }

// Client/Assets/Scripts/PMNet/Session/PMUdpSessionEndpoint.cs:350  （类 PMUdpSessionEndpoint）
public bool IsDisposed { get { return _disposed; } }
```

两处均为**公开只读实例属性**，无参数、无副作用、不改写任何字段。本次复核未改动其签名与语义。

有界检索（**非全项目盲搜**，仅确认消费状态）：
- `PendingServerFrame` 当前项目内**只有** `Tools/PMPredictionTest/Program.cs` 引用 ⇒ 宿主尚未接线。
- `Endpoint.IsDisposed` 当前只有 `Tools/PMUdpAdmissionTest/Program.cs` 引用；`PMClientSessionHost.cs`/`PMDsSessionHost.cs`
  中的 `IsDisposed` 全部挂在 `rig.Driver`（`PMUnityMoverDriver`）上，**不是**端点属性 ⇒ D5 宿主侧同样尚未接线。

## 3. 新增逻辑真实性核验

### 3.1 `PMPredictionTimeline.PendingServerFrame`（D4 最小修法）

- **只读、不深克隆**：`PendingServerFrame` → `GetBoundaryServerFrame(_pendingFrame)`（:1469）→
  `ResolveBoundaryIndex(boundary)`（:1474）→ 返回 `_baseServerFrame` 或 `IndexToStep(index).ServerFrame`。
  全程只返回 `PMFrameId` 值类型，**不触碰** `CloneSyncOrThrow`/`CloneAuxOrThrow`
  （`:1034` 起，内部 `_model.CloneSync/CloneAux` 才会克隆）。
- **与 `GetSnapshot().ServerFrame` 逐次一致（同源同参）**：`GetSnapshot()`（:834）= `BuildSnapshot(PendingFrame)`；
  `BuildSnapshot`（:894）内 `long b = boundary.Value;` 后 `snapshot.ServerFrame = GetBoundaryServerFrame(b);`。
  宿主/属性传入的 `boundary` 即 `PendingFrame`，其 `Value == _pendingFrame` ⇒ 与属性使用**同一 helper、同一入参**，
  不存在"属性走另一套推断"的可能。
- **`BuildSnapshot` 重构的等价性与安全性**：`ServerFrame` 与 `Sync`/`Aux`/`TotalSimTimeMs` 三项原本就共用
  同一 `boundary` 且都经 `ResolveBoundaryIndex` 判界；把 `ServerFrame` 切到同一 helper **不新增任何异常路径**
  （越界情形仍由既有的 `GetBoundarySyncRef` 先抛）。
- **边界不变式**：`_boundaryStartFrame ≤ _confirmedFrame ≤ _pendingFrame`，裁剪时二者同步推进
  （`TryPruneConfirmedForCapacity` :1521-1545），故 `index = _pendingFrame - 1 - _boundaryStartFrame = _count - 1`
  恒落在 `[0, _count)` ⇒ 属性读取**不会**抛"读取边界越界"。
- **None / 非 0 / Tick / 校正重放 / Resync 全覆盖**：None 仅来自"元数据未建立"（`_baseServerFrame`/`Step.ServerFrame`
  写入的是 `PMFrameId.None`），属性**不合成**假 `AuthorityServer` 帧号；Tick 写入 `step.ServerFrame`；
  校正重放与 Resync/重绑分别写入权威快照/新快照的 `ServerFrame`——四条路径都由 §3.3 的 N 段独立期望值覆盖。

### 3.2 `PMUdpSessionEndpoint.IsDisposed`（D5 最小修法）

- **纯读快照**：`get { return _disposed; }`，不改变 `Dispose`/`Pump`/`SendDatagram` 任何分支。
- **启动值为 false**：`private bool _disposed;`（:123）默认 false，构造/`OpenServer`/`OpenClient` 均不置位。
- **`Dispose` 后为 true 且幂等**：`Dispose()` 首行 `if (_disposed) return;` 后立即 `_disposed = true;`（:393-399），
  重复调用走早退、不抛、不重复清理。
- **既有 `_disposed` 语义完全保留**：`Pump` 首行 `if (_disposed) return;`（:364）、`ReceiveDatagrams` 的
  `catch (ObjectDisposedException) { _disposed = true; break; }`（:468）、`SendDatagram` 的
  `if (_disposed || _socket == null || target == null)`（:882）**均未被修改**；新增属性只是把该状态**可观测化**。
- 与 D5 审查给出的最小修法一致：仅在端点侧暴露只读标志，真正的 `Fail(...)` 判定留给宿主（未接线，见 §8 R2）。

### 3.3 新增测试的真实性（可判别，非同源重言）

- **`Tools/PMPredictionTest/Program.cs` Section N**（`:717` 起，37 次 `Check*` 调用）：
  - N1：`None` 初值（域 `None`、`Value=0`）+ **克隆计数对照**——`CloneCountingModel`（:687，覆写 `CloneSync/CloneAux`
    递增计数）下，读 `PendingServerFrame` 两次 ⇒ `CloneSyncCount==0 && CloneAuxCount==0`；同一时间轴
    `GetSnapshot()` ⇒ 恰 `1/1`。这是**独立于被测实现**的判别式（若属性内部走 `GetSnapshot()` 则立即失败）。
  - N2：`ServerFrame(70)` 非 0 快照 ⇒ 域 `AuthorityServer`、`Value=70`；"非 0 边界 + None 元数据" ⇒ 仍 `None`（不合成）。
  - N3：`Tick`（`step.ServerFrame=80`）后 `PendingFrame=8` 且属性 `=80`；None 元数据步进后仍 `None`。
  - N4：`ApplyAuthority`（权威 `ServerFrame=777`）后属性 `=777`，`ResimulatedFrames==1`（校正重放路径）。
  - N5：同绑定 `Resync(555)` ⇒ `555` 且 `!EpochRebound`；跨 epoch `Resync(888)` ⇒ `PendingFrame=4`、属性 `=888`（重绑路径）。
  - N6：`Freeze()` 后属性仍可读并保持 `300`（只读读取不参与冻结判定）。
  - 期望值（0/70/80/777/555/888/300）均为**手工独立给定**，且每段都与 `GetSnapshot().ServerFrame` 交叉比对。
- **`Tools/PMUdpAdmissionTest/Program.cs` Section J**（`TestEndpointDisposedFlag`，`:1407`，22 项）：
  真实 localhost UDP 两客户端建链后：Open 后三端点 `IsDisposed==false`；服务端 `Dispose` 只置位自身（不抛、
  不影响 A/B）；重复 `Dispose` 不抛且仍 true；已释放端点 `Pump` 不抛、不翻转标志、`DatagramsSent/Received` 不变
  （证明"原 `_disposed` 早退路径未变"）；客户端 A/B 同样 false→true、幂等；全部释放后 `ConnectionCount==0`。
- **规模对账（只新增、未删改既有断言）**：
  - `PMPredictionTest` 既有基线 **453**（`Tools/PMPredictionTest/r4b-main-verification.log` 末行"通过: 453"）
    → 本次 **490**，增量 **+37** 恰等于 N 段 `Check*` 调用数 37。
  - `PMUdpAdmissionTest` 既有基线 **360**（`Tools/PMUdpAdmissionTest/r3b-verification.log` 末行"全部通过：360 项检查"）
    → 本次 **382**，增量 **+22** 恰等于报告出的 `[J] 22 项通过`。
  - 因此既有断言（含本次 `BuildSnapshot` 改动所影响的快照/边界路径）**全部仍在通过**，不存在"删旧添新"掩盖回归。

## 4. build 与 run 的真实结果

| 步骤 | 命令（工作目录 `D:/UGit/hyld-master`） | exit | 结果 | 日志 |
|---|---|---|---|---|
| 构建1 | `dotnet build Tools/PMPredictionTest/PMPredictionTest.csproj -c Release` | 0 | 0 警告 0 错误 | `/tmp/r4bmeta2/pred_build.log` |
| 构建2 | `dotnet build Tools/PMUdpAdmissionTest/PMUdpAdmissionTest.csproj -c Release` | 0 | 0 警告 0 错误 | `/tmp/r4bmeta2/udp_build.log` |
| **全量重建1** | `dotnet build Tools/PMPredictionTest/PMPredictionTest.csproj -c Release -t:Rebuild` | 0 | **0 警告 0 错误**（排除增量跳过） | `/tmp/r4bmeta2/pred_rebuild.log` |
| **全量重建2** | `dotnet build Tools/PMUdpAdmissionTest/PMUdpAdmissionTest.csproj -c Release -t:Rebuild` | 0 | **0 警告 0 错误** | `/tmp/r4bmeta2/udp_rebuild.log` |
| 运行1 | `dotnet Tools/PMPredictionTest/bin/Release/net8.0/PMPredictionTest.dll` | **0** | **通过 490 / 失败 0**，含 Section N 标题 | `/tmp/r4bmeta2/pred_run.log` |
| 运行2 | `dotnet Tools/PMUdpAdmissionTest/bin/Release/net8.0/PMUdpAdmissionTest.dll` | **0** | **全部通过 382 项检查 / 0 失败**，`[J] 22 项通过 / 0 项失败` | `/tmp/r4bmeta2/udp_run.log` |

说明：`PMPredictionTest` 的 `Check` 只在**失败时**收集，正常仅打印各 Section 标题与末尾汇总（`:301-313`），
因此其日志天然短小（21 行）不代表未执行；Section N 标题出现在输出中且总数 490 与基线对账吻合。

## 5. 取消后恢复核验

1. **md5（与主侧磁盘核验值逐字节一致）**

   | 文件 | md5 |
   |---|---|
   | `Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs` | `c3c7750010d3b82ea4b39f1385677870` |
   | `Client/Assets/Scripts/PMNet/Session/PMUdpSessionEndpoint.cs` | `1dbc0a85e2ad616621bfe222d4d65e22` |
   | `Tools/PMPredictionTest/Program.cs` | `6d6ed7ebdee417674815a3185e257b6a` |
   | `Tools/PMUdpAdmissionTest/Program.cs` | `ee4d553f1b8e7db037d3c7c2f31cff3c` |

   build/run 前后 md5 不变（构建不污染源文件）。

2. **编码**：4 文件均为 UTF-8 **BOM + CRLF**，游离 LF 计数 0
   （Timeline 1606 CRLF/67237B、Endpoint 1062 CRLF/44323B、pred 2329 CRLF/136392B、udp 2192 CRLF/127055B）。
3. **注入还原证据**：`/tmp/r4bmeta/PMPredictionTimeline.cs.bak` 与 `PMUdpSessionEndpoint.cs.bak` 的 md5
   分别为 `c3c775...` 与 `1dbc0a85...`，**与工作区当前文件相同**；`/tmp/r4bmeta/md5_before.txt`、`md5_ep_before.txt`
   记录同值 ⇒ 注入实验后已用备份精确还原到"已修好"状态。
4. **无残留**：仓库内 `Tools/PMPredictionTest/*.bak`、`Tools/PMUdpAdmissionTest/*.bak`、
   `Client/Assets/Scripts/PMPrediction/*.bak` 均不存在；临时产物只在 `/tmp/r4bmeta`、`/tmp/r4bmeta2`。
5. **工作区状态**：`git status --porcelain` 对这 4 个文件仅报 `??`（未跟踪，与本批开始前一致），
   本次未新增/删除/修改任何同范围文件；`Docs/plans/_r4b_metadata_endpoint_fix.md`（本报告）为唯一新增。

## 6. 关于"负向注入"证据的诚实说明

- 委派台账可见前组确实做过"注入 → 构建 → 运行 → `cp /tmp/r4bmeta/*.bak` 还原 → 重算 md5"的动作序列
  （16:42:11 / 16:42:24 / 16:42:37 / 16:42:44 等条目）。
- 但**注入期的失败输出未落盘为文件**：`/tmp/r4bmeta` 只有 `pmpred.log`/`pmpred_final.log`（两版**逐字节相同**）与
  `pmudp.log`/`pmudp_final.log`（仅临时端口号 60589/65069 不同），四份**全部是修法后的全绿版本**。
- 因此"若注入缺陷则新增测试必红"这一条，目前**只有台账记录 + 还原哈希**，**没有可独立复现的证据文件**。
  本组遵嘱**未重复注入**，也未把台账记录冒充为本人实测。
- 若要把 Section N/J 升级为可回归的负向门禁，建议单独开一轮专项（本次写入边界只允许 4 个目标文件 + 本报告，
  不适合再做注入-还原）。

## 7. 明确未做（边界）

- 未修改宿主：`Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`、`PMDsSessionHost.cs` 全程只读；
  D4/D5 的宿主接线**未做**（属并行宿主组）。
- 未触碰 D1/D2/D3 相关代码，未修改 Unity 工程其它文件、场景、资产、meta。
- 未启动 Unity/编辑器，未编译 UE，未提交（git/svn），未递归委派。

## 8. 剩余问题（交接主 Agent）

- **R1（重要）D4 收益未生效**：`PMClientSessionHost.cs:565` 仍为
  `PMFrameId serverFrame = rig.Driver.Timeline.GetSnapshot().ServerFrame;`
  ⇒ 每子步仍 `CloneSync + CloneAux`。需并行宿主组改用 `PendingServerFrame` 才能消除分配尖峰。
  本 API 已冻结，宿主改动不影响本报告结论。
- **R2 D5 fail-closed 未生效**：宿主尚无"端点已释放但会话未 Stop ⇒ Fail"判定；当前只是状态可观测，
  静默通道仍存在（可近不可达）。
- **R3 负向门禁缺失**：见 §6。
- **R4**：`_r4b_host_final_review.md` 的 P2/P3/P4 推断（≥200ms 丢时间、每会话一份输入、PollHardware 重复调用）
  本次未验证，不在本次范围。

## 9. 文件路径清单

**读取（复核）**
- `D:/UGit/hyld-master/AGENTS.md`
- `D:/UGit/hyld-master/Client/Assets/AGENTS.md`
- `D:/UGit/hyld-master/Server/AGENTS.md`
- `D:/UGit/hyld-master/Docs/plans/net-r4-network-contract.md`
- `D:/UGit/hyld-master/Docs/plans/_r4b_host_final_review.md`
- `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`
- `D:/UE_Project/ProjectMecury/Saved/CLI-Delegation/checkpoints/cli-delegation-comprehensive-1789893419185-a2ad9155-b18e-4e60-b577-684aa5d0d375.md`
- `Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs`（仅新增 API 与其直接实现：:339、:834、:894-910、:1449-1489）
- `Client/Assets/Scripts/PMNet/Session/PMUdpSessionEndpoint.cs`（仅新增 API 与直接实现：:123、:345-351、:358-399、:468、:882）
- `Tools/PMPredictionTest/Program.cs`（新测试段 :687-855、:279-280）
- `Tools/PMUdpAdmissionTest/Program.cs`（新测试段 :67、:1396-1474）
- `/tmp/r4bmeta/*`（前组证据）、`Tools/PMPredictionTest/r4b-main-verification.log`、`Tools/PMUdpAdmissionTest/r3b-verification.log`（基线对账）

**写入（唯一）**
- `D:/UGit/hyld-master/Docs/plans/_r4b_metadata_endpoint_fix.md`（本报告）
