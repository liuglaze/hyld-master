# R6-A2 报告：PMR3Player 战斗声明层（纯声明 / 真实 codegen / OwnerOnly 权限边界）

> 任务：实现 R6-A2「PMR3Player 纯声明/属性及真实 codegen」，**不依赖并行 PMCombat**。
> 契约：`Docs/plans/net-r6-combat-contract.md` §A2（声明）+ `Docs/plans/net-r2-codegen-contract.md`（§2 稳定 ID / §4 API 面 / §5 规则 13）。
> 门禁：`Tools/PMR6DeclarationTest`（新增）+ `Tools/PMR3RuntimeTest`、`Tools/PMR5DeclarationTest`（回归）。
> 边界：只改下列文件；**不重写** PMR3Player 既有属性/RPC/生成 ID；不新增 runtime `.cs`/`.meta`；不新增 .NET 运行期反射；不另造 registry/锁。

---

## 1. 交付内容（声明层）

### 1.1 复制属性（9 条，全部 Push 模型 + 每属性 RepNotify）

| 成员 | 类型 | 条件 | PropertyId | MaskOffset |
|---|---|---|---|---|
| `_combatHeroId` | int | `None` | 36554 | 8 |
| `_combatTeamId` | int | `None` | 24395 | 5 |
| `_combatHp` | int | `None` | 31999 | 6 |
| `_combatMaxHp` | int | `None` | 64920 | 11 |
| `_combatDead` | bool | `None` | 4259 | 0 |
| `_combatMatchEnded` | bool | `None` | 21371 | 4 |
| `_combatWinnerTeamId` | int | `None` | 38154 | 9 |
| `_combatMana` | int | **`OwnerOnly`** | 34826 | 7 |
| `_combatSuperEnergy` | int | **`OwnerOnly`** | 5878 | 1 |

- 只读 Pascal getter：`CombatHeroId / CombatTeamId / CombatHp / CombatMaxHp / CombatDead / CombatMatchEnded / CombatWinnerTeamId / CombatMana / CombatSuperEnergy`。
- 每属性一个 `[PMRepNotify(nameof(_x))]` 私有方法，统一转发到 `NotifyCombatStateReplicated()`：
  调用 `CombatDriver.OnCombatStateReplicated()`（**只通知/入队**）+ 广播 `CombatStateUpdated` 事件。
  **回调内不发任何 RPC**；两处都逐订阅者隔离异常并计数 `CombatNotifyFailureCount`（「通知被吞」保持可观测）。
- 契约允许同一批多次触发（复制层对每条送达的 Update 记录都派发 OnRep，不做值比较），测试按「至少一次 + 读当前值」断言。

### 1.2 写入口

```csharp
public bool PublishCombatState(int heroId, int teamId, int hp, int maxHp, bool dead,
                               int mana, int energy, bool ended, int winner)
```

- 九条属性全部经**生成的 `PMNet_Set_combatXxx`** 写入（赋值即标脏），外部不得改私有字段。
- **权限（World role）**：`World != null && !World.IsServer` ⇒ 拒绝写入、计 `CombatPublishRejectedCount`、发可观察告警、返回 `false`，值保持不变（「AP 不写复制字段」）；
  `World == null`（未上线）放行，保留「先写初值再 Spawn」的手法（与 R5 投射物一致）；
  `World.IsServer` 放行。
- 本地记账：`CombatPublishCount / CombatPublishRejectedCount / CombatStateNotifyCount / CombatNotifyFailureCount / CombatAttackResult{Applied,Discarded}Count / CombatMatchResult{Applied,Discarded}Count`。

### 1.3 RPC（4 条）

| 方法 | 方向 | 可靠 | 校验 | RpcId |
|---|---|---|---|---|
| `ServerCombatAttackV1(uint activationId, bool isSuper, float aimX, float aimZ)` | Server | 可靠 | **ForceValidate** | 42343 |
| `ServerCombatResultAckV1(uint outcomeId)` | Server | 可靠 | **ForceValidate** | 50908 |
| `ClientCombatAttackResultV1(uint activationId, bool accepted, int reason)` | Client | 可靠 | — | 44706 |
| `ClientCombatMatchResultV1(uint outcomeId, int winnerTeamId)` | Client | 可靠 | — | 39181 |

- 上行参数域（三态，`Reject` 只跳过实现、**不断连**）：
  - `ServerCombatAttackV1`：`driver != null`、`activationId != 0`、方向**有限**（非 NaN/Inf）、**非零**、逐分量 `|aim| <= CombatAimComponentLimit = 1.001f`（归一化留给 core）。
  - `ServerCombatResultAckV1`：`driver != null`、`outcomeId != 0`（0 是无效哨兵）。
- 下行无 driver ⇒ `CombatAttackResultDiscardedCount / CombatMatchResultDiscardedCount` 计数（可观察丢弃，不静默吞）。
- 归属（owner）校验仍由 `PMNetRpcReceive` 完成，**不绕过**；本层不做身份判定。

### 1.4 驱动接缝（无核心依赖）

```csharp
public interface IPMCombatNetworkDriver          // 只在声明文件里定义，参数只用原生类型
{
    void OnCombatStateReplicated();                                        // 只入队
    void OnServerAttack(uint activationId, bool isSuper, float aimX, float aimZ);
    void OnClientAttackResult(uint activationId, bool accepted, int reason);
    void OnClientMatchResult(uint outcomeId, int winnerTeamId);
    void OnServerResultAck(uint outcomeId);
}
public IPMCombatNetworkDriver CombatDriver;       // 由 B 组 PMR6CombatDriver 绑定
```

声明层**不引用** PMCombat / PMProjectile / Coordinator —— 只编「声明层 + PMNet 核心」的旧消费者工程无需新增任何引用（本批次由 PMR6/PMR3Runtime/PMR5Declaration 三个工程验证）。

---

## 2. 协议摘要与 ID（**新 ID 从此冻结**）

| 项 | 值 |
|---|---|
| `PMNetRegistry.ProtocolHash`（全局，生成期 == 运行期 == `PMR3Runtime.ProtocolHash`） | **0xE6130FAA**（3860008874） |
| `PMR3Player` 类协议摘要 | **0xB09BCD1C**（2963000604） |
| `PMR5Projectile` 类协议摘要 | **0xD4CB0B42**（未变） |
| `PMR3Player.PMGeneratedClassId` | 405815557（未变） |
| `PMR3Player.PMGeneratedChangeMaskBitCount` | 3 → **12** |
| 生成集合类数 / RPC 数 | 2 个类（未新增类）/ PMR3Player 13 条 RPC |

**旧键一条未漂移**（字面量核对，测试内以外部 oracle 断言，不与生成物自比较）：
`_uid`=18801、`_probeCount`=12656、`_movementSnapshotV1`=61580、`ServerProbe`=34232、`ClientEcho`=63853、
`ServerMovementInputV1`=25428、`ServerMovementResyncV1`=15021、`ClientMovementEventsV1`=5180、`ClientMovementResyncV1`=28381、
`ServerProjectileSpawnV1`=21590、`ServerProjectileHitV1`=33011、`ClientProjectileDecisionV1`=38620、`PMR5Projectile` 类=227098277、`_projectileSnapshotV1`=6683。

**mask 区间注意（有意变更，两端必须同版本重建）**：属性掩码位按 `PropertyId` 升序分配，新键插入后既有属性的 `MaskOffset` 会平移
（`_uid` 0→3、`_probeCount` 1→2、`_movementSnapshotV1` 2→10）。这属于线格式内部布局变化，两端/生成物同源重生成即一致；
**ID（线协议身份）本身未变**。

---

## 3. codegen 与文件

生成命令（与 R5 同目录、同 `pmnet-r3-ids.json` 专用锁，未动共享锁 `pmnet-ids.json`）：

```
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-gen  \
  Client/Assets/Scripts/PMR3/PMR3Player.cs Client/Assets/Scripts/PMR3/PMR5Projectile.cs \
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
dotnet ... --decl-check ...   # 逐字节比对：3 个产物 + 锁文件，退出码 0
```

- 生成器输出：`网络类 2 个，复制属性 13 个，RPC 13 条`；`PMR3Player props=12 rpcs=13 maskBits=12`。
- **未手改任何生成物**；产物保持 UTF-8 BOM + CRLF（`cr == lf`，首字节 `EF BB BF`）。
- `PMNet.PMNet.R3.PMR5Projectile.g.cs` 重生成后与旧内容逐字节相同（未受影响）。
- 生成目录仍恰好 3 个 `.cs` + 3 个 `.meta`；`RegisterAll` 仍是显式列出两个类，**无任何 `Reflection`/`GetTypes(`**。

---

## 4. 测试结果

### 4.1 必跑三项（全部通过）

| 门禁 | 结果 | 说明 |
|---|---|---|
| `Tools/PMR6DeclarationTest`（新增） | **235 项通过 / 0 失败** | A 99 / B 15 / C 32 / D 31 / E 26 / F 22 / G 10 |
| `Tools/PMR3RuntimeTest` | **180 项通过 / 0 失败** | 首跑出现 H15/H16/H17 三项失败，**复跑全绿**（真实 TCP 控制面时序抖动，与本次改动无关） |
| `Tools/PMR5DeclarationTest` | **139 项通过 / 0 失败**（未改一行） | 说明 R5 声明面未被破坏 |

`dotnet build`：PMNetGen / PMR6DeclarationTest / PMR3RuntimeTest / PMR5DeclarationTest 均 **0 错误 0 警告**。

### 4.2 PMR6DeclarationTest 覆盖与关键证据

- **A 声明/生成集合（99）**：`--decl-check` 退出码 0；锁文件含全部旧键（字面量）与新键（字面量）；共享锁未被改写；
  12 条属性描述符逐条核对（名字/ID/条件/Push/Setter/OnRep ID，并额外断言 `HasConditionalMask`）；
  13 条 RPC 档位（旧 9 条逐条不变 + 新 4 条按契约冻结）；类/全局摘要 == 冻结字面量；两条上行 RPC 的 `ParamLayoutId` 非 0 且互不相同；registry 零反射。
- **B OwnerOnly 权限边界（15，逐连接掩码）**：真实 `PMNetWorld` + 真实 `PMReplicationChannel` + 真实生命周期 codec，按连接解码 Create 初值与 Update 记录：
  - owner 的 Create 初值 = **12 槽位**（含 mana/energy）；observer = **10 槽位**（不含 mana/energy）→ **初始 Create 不泄资源**；
  - 首次 Update（全量基线）：observer 仍无 mana/energy 属性 ID（34826/5878）；
  - 后续资源变更（mana 99/energy 5 + 公共 hp 55/dead）：observer **始终拿不到** mana/energy，但**拿到了**公共 HP（排除「整体漏发」）；`ConditionFiltered = 4`。
- **C 真实 Transport 双连接（32，owner + observer）**：真实 PMTransport 字节链 + 真实 Bridge + 生成桩。
  - Create 回调当场读值：owner `mana=42/energy=17`；observer `mana=0/energy=0`（**不泄**）；公共 hero/team/hp/maxHp 两边都是真值。
  - 后续变更：owner `mana=99/energy=5`；observer 仍 `mana=0/energy=0`，但 **hp=55 + dead 两边都到**（公共 HP/死亡全到）。
  - OnRep 真派发：owner/observer 的 `CombatDriver.OnCombatStateReplicated` 均被真的调用，`CombatStateNotifyCount` 增长，复制层 `OnRepDispatched` 增长且 `OnRepUnhandled == 0`，`CombatNotifyFailureCount == 0`。
- **D RPC 归属与参数域（31）**：owner 的上行实参（activationId/isSuper/float×2）逐字段原样到达 DS 驱动；
  observer 冒名上行被服务端判 `NotOwner`（实现未执行、`RpcRejected` 增长、`RpcApplied` 保持 0）；
  `activationId=0` / 零方向 / NaN / `1.5>1.001` 全部 Reject（实现未执行、经 `PMRpcValidationSink` 上报、**不断连**）；
  边界 `1.001` 与极小非零方向**放行**（证明拒的是范围而不是一律拒）；带尾随字节判 `Malformed`。
- **E null driver 失败关闭（26）**：无 driver 时两条上行均 ForceValidate `Reject`（已投递并被处置、不断连、无断连请求）；
  两条下行无 driver ⇒ **可观察丢弃** 各 +1，接上 driver 后 applied +1 且丢弃不再增长；DS 权威副本不会应用下行裁决（方向正确）。
- **F Publish 权限（22）**：DS（`World.IsServer`）放行且九字段真写入；AP 副本（`IsServer=false`）**被拒**（返回 false、拒绝计数 +1、值不变、有告警）；
  AP 的越权尝试不传播回 DS/其它连接（owner/observer 仍只看到 DS 真值，observer mana 始终 0）。
- **G 边界与清理（10）**：生成目录恰好 3 个 `.cs`+3 个 `.meta`（未另造 registry/锁）；重复 `Attach` 幂等；`Detach` 解除；`Shutdown` 清静态事件且可重复调用；复位后摘要仍为冻结值。

### 4.3 边界之外的门禁（**未越界修改，供主侧整合**）

| 门禁 | 结果 | 具体报错 | 归属 |
|---|---|---|---|
| `Tools/PMR4NetworkTest` | **2 项失败** | `复制属性位宽 == 3（期望 3，实际 12）`、`复制描述符属性数 == 3（期望 3，实际 12）`（`Program.cs:283/289`）| 仅过时**计数**断言，主侧按 R6-A2 改为 12 即可；其原 ID/权限/档位断言全部通过 |
| `Tools/PMR3UnitySmoke` | 35 通过 / **S35 1 项失败** | `[PMDsHost] 协议摘要不一致：引导文件 0xE6130FAA，本机 0xDDC346C5` → DS 从不发 Ready，Lobby 45000ms 超时 | **预构建的 `HyldDS/HyldDS.exe`（mtime 2026-09-21 14:35）是旧协议二进制**。注意其本机哈希 `0xDDC346C5` 与「R6-A2 之前」的 `0x43DD5A42` 也不同，说明它来自更早/在飞的另一版声明（R6 并行批次）；本任务禁止启动 Unity，**必须在声明合并定型后重建 DS 再验**（AGENTS §14「集中联调前两端及 Lobby 需同版本重建」）。S1–S34 全绿且 S9 已确认当前生成表 hash=0xE6130FAA |
| `Tools/PMR5NetworkTest` / `PMR3IntegrationTest` / `PMR4IntegrationTest` / `PMNetE2E` / `PMNetVerify` / `PMDeclCheck` | 全绿 | PMR5NetworkTest 0 失败、PMR3IntegrationTest 143/0、PMR4IntegrationTest 113/0、PMNetE2E 0 失败、PMNetVerify 27/0、PMDeclCheck 71/0 | 无需改动 |
| `Tools/PMClientCheck` / `PMUnityGlueCheck` | 编译 0 错误 | 两者是 `netstandard2.0` 编译门（默认 LangVersion 7.3）| 顺带证明**声明层 + 生成物在 C# 7.3 下可编译** |

---

## 5. 未接入的驱动职责（本批次刻意不做，交给 B 组 driver）

- `PMR6CombatDriver`（session 级）：`BindPlayer` 转接、DS 侧 trusted model → `PublishCombatState`、AP 侧不写复制字段、上行授权队列与「先战斗授权后 ProjectilePump」的处理顺序。
- `PMR3Runtime.SendRemoteRpc` 的故障策略**未扩**：新战斗 RPC 仍走既有「未接线/被拒 ⇒ 告警不抛」路径（只有 R5 三条投射物 RPC 抛异常）。契约 A2 未要求本批扩，留给 driver 组按会话 fault 口径处理。
- **不做**：授权/资源/伤害/死亡/胜负/首杀/断线 winner 的任何规则（在 PMCombat 纯核心）；客户端假弹撤销（R5 侧）；`OnCombatStateReplicated` 的具体入队实现。
- 新属性通道**不承诺逐次 hit 事件**（hit 语义在 R6-B/结算链，不在声明复制通道）。

---

## 6. 修改文件

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMR3/PMR3Player.cs` | 追加 `IPMCombatNetworkDriver`、9 条复制属性 + getter、`PublishCombatState`、9 条 RepNotify、`CombatStateUpdated`、4 条 RPC + 2 条 ForceValidate 同伴、本地计数/告警出口。**既有属性/RPC/生成 ID 一字未改** |
| `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs` | 生成器重生成（12 属性 / 13 RPC / maskBits=12） |
| `Client/Assets/Scripts/PMR3/Generated/PMNetGeneratedRegistry.g.cs` | 生成器重生成（摘要 0xE6130FAA） |
| `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR5Projectile.g.cs` | 在写入边界内被重生成，内容与旧版**逐字节相同**（无实质变更） |
| `Docs/plans/pmnet-r3-ids.json` | 追加 9 条属性键 + 4 条 RPC 键（旧键值全部原样） |
| `Tools/PMR6DeclarationTest/PMR6DeclarationTest.csproj` | 新增门禁工程（只编 PMNet 核心 + PMR3 声明层/生成物，不引 PMCombat/PMProjectile/Unity） |
| `Tools/PMR6DeclarationTest/Program.cs` | 新增 235 项门禁（A–G） |
| `Tools/PMR3RuntimeTest/Program.cs` | 仅更新两处过时**计数**断言（3 → 12）与注释；原 ID/权限/档位断言未弱化 |
| `Docs/plans/_r6_declaration_report.md` | 本报告 |

**运行 PMR3UnitySmoke 的副作用（诊断用，非产品产物）**：产生新目录 `Tools/PMR3UnitySmoke/artifacts/20260921-200108-52c37357/`（内含 `hyldds-1-*.log` 的协议摘要不一致证据、`summary.txt`、`launch-args.txt`、`bootstrap/`）。该目录是**跑既有门禁时的测试输出**（与 `bin/obj` 同类），主侧可保留作 S35 证据或直接删除。

未改（越界）：`PMR3Runtime.cs`（无需为战斗扩 `IsProjectileRpc` 式故障策略）、`PMR5DeclarationTest/Program.cs`（仍全绿）、`PMR4NetworkTest/Program.cs`、共享锁 `pmnet-ids.json`、`net-architecture-migration.md`（主计划由主侧维护）。

---

## 7. 复现命令

```
dotnet build Tools/PMNetGen/PMNetGen.csproj -c Release
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check \
  Client/Assets/Scripts/PMR3/PMR3Player.cs Client/Assets/Scripts/PMR3/PMR5Projectile.cs \
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json

dotnet build Tools/PMR6DeclarationTest/PMR6DeclarationTest.csproj -c Release
dotnet build Tools/PMR3RuntimeTest/PMR3RuntimeTest.csproj -c Release
dotnet build Tools/PMR5DeclarationTest/PMR5DeclarationTest.csproj -c Release

dotnet Tools/PMR6DeclarationTest/bin/Release/net8.0/PMR6DeclarationTest.dll        # 235/0
dotnet Tools/PMR3RuntimeTest/bin/Release/net8.0/PMR3RuntimeTest.dll              # 180/0
dotnet Tools/PMR5DeclarationTest/bin/Release/net8.0/PMR5DeclarationTest.dll      # 139/0
```

---

## 8. 剩余问题与风险（诚实口径）

1. **`PMR4NetworkTest` 两处过时计数断言**（`Program.cs:283/289`，3 → 12）：在写边界之外，未改；主侧整合时一并更新，其既有 ID/权限断言无需放宽。
2. **`PMR3UnitySmoke::S35` 失败源于预构建 DS 二进制**（本机哈希 `0xDDC346C5`）：本任务禁止启动 Unity，无法重建 `HyldDS.exe`。本批**未宣称就绪**实机；实机前必须重建 DS（AGENTS §14）。S1–S34 全绿（生成表封板、UDP 端口、控制面参数、真实进程启动等）。
3. **mask 区间平移**：既有 `_uid/_probeCount/_movementSnapshotV1` 的 `MaskOffset` 变化（0/1/2 → 3/2/10）。线协议身份（ID）未变，但**两端必须同版本重生成**，不可只更新一侧。
4. **`PMR3RuntimeTest` 首跑 H15–H17（控制面 ResultAck）失败、复跑全绿**：真实 TCP + 虚拟时钟时序抖动，与本次改动无因果关系（该段不涉及 PMR3Player 声明）。
5. `PublishCombatState` 相比契约文本多返回 `bool`（权限结果可判定）；调用方按语句使用即可，不影响契约给出的签名形状。
6. 未验证（明确不在本批）：任何战斗规则、授权/结算、R5 假弹撤销、`CombatDriver` 的入队实现、实机字节链（属 T6B/T6C 与 B 组）。
