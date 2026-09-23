# R5-B2a 声明与单一生成集合 — 交付报告

> 范围：`Docs/plans/net-r5-network-contract.md`「声明层（单一生成集合）」；实施映射 T5B2b（声明组）。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本文件只记录本批次**实际产物与实测证据**。
> 本批次**不含** B2b 网络 driver、不含宿主/Unity 接线、不含 SpawnReserved 接线、不含 R6 结算消费者。

---

## 1. 交付物

| 文件 | 变更性质 |
|---|---|
| `Client/Assets/Scripts/PMR3/PMR5Projectile.cs` | **新增**：投射物声明对象 + `PMR5ProjectileEvents` |
| `Client/Assets/Scripts/PMR3/PMR5Projectile.cs.meta` | 新增（LF，guid `bf603c2672874b53a6f19751326e95a2`，全仓唯一） |
| `Client/Assets/Scripts/PMR3/PMR3Player.cs` | 追加（**0 删除**）：`IPMProjectileNetworkDriver`、`ProjectileDriver`、三条 RPC、告警出口、计数 |
| `Client/Assets/Scripts/PMR3/PMR3Runtime.cs` | `Attach` 追加投射物工厂 + OnRep 分发表；`Shutdown` 追加 `PMR5ProjectileEvents.ClearAll()` |
| `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs` | 由 `Tools/PMNetGen --decl-gen` **重新生成**（新增 3 条 RPC 产物） |
| `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR5Projectile.g.cs` | 新增（生成物） |
| `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR5Projectile.g.cs.meta` | 新增（LF，guid `d4cdbbf46d334199af1af06f2b31f5f1`，全仓唯一） |
| `Client/Assets/Scripts/PMR3/Generated/PMNetGeneratedRegistry.g.cs` | 重新生成（类数 1→2） |
| `Docs/plans/pmnet-r3-ids.json` | 重新生成（锁文件；旧键逐条不变） |
| `Tools/PMR5DeclarationTest/{PMR5DeclarationTest.csproj,Program.cs}` | 新增门禁（139 项） |
| `Tools/PMR3RuntimeTest/PMR3RuntimeTest.csproj` + `Program.cs` | 显式 Include 新声明文件；`--decl-check` 源路径含新文件；硬编码类数 1→2 |
| `Tools/PMR3IntegrationTest/PMR3IntegrationTest.csproj` | 显式 Include 新声明文件 |
| `Tools/PMR3UnitySmoke/PMR3UnitySmoke.csproj` | 显式 Include 新声明文件 |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj` | 显式 Include 新声明文件 |
| `Tools/PMClientCheck/PMClientCheck.csproj` | 显式 Include 新声明文件 |

生成命令（**未改生成器**）：

```
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-gen Client/Assets/Scripts/PMR3 \
       --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
```

生成物全部由工具写出（BOM + CRLF），**无任何手改**：`git diff` 里生成物的 24 行删除全部是
RPC 槽位重排（`d2..d5` → `d3..d6`）与头注释「RPC：6 条」→「9 条」，没有任何既有描述符字段被改写。

---

## 2. 精确 ID（生成器实际分配）

### 2.1 旧 ID：逐条未漂移

| 稳定键 | ID |
|---|---|
| `CLASS:PMNet.R3.PMR3Player` | `405815557` |
| `PROP:...PMR3Player._uid` | `18801` |
| `PROP:...PMR3Player._probeCount` | `12656` |
| `PROP:...PMR3Player._movementSnapshotV1` | `61580` |
| `RPC:...PMR3Player.ServerProbe` | `34232` |
| `RPC:...PMR3Player.ClientEcho` | `63853` |
| `RPC:...PMR3Player.ServerMovementInputV1` | `25428` |
| `RPC:...PMR3Player.ServerMovementResyncV1` | `15021` |
| `RPC:...PMR3Player.ClientMovementEventsV1` | `5180` |
| `RPC:...PMR3Player.ClientMovementResyncV1` | `28381` |

### 2.2 本次新增 ID（从本批次起冻结）

| 稳定键 | ID | 说明 |
|---|---|---|
| `CLASS:PMNet.R3.PMR5Projectile` | `227098277`（0x0D893EA5） | 类协议摘要 `0xD4CB0B42` |
| `PROP:PMNet.R3.PMR5Projectile._projectileSnapshotV1` | `6683` | 唯一复制属性，`byte[]`，Push，`cond=None`，`OnRepMethodId=1` |
| `RPC:PMNet.R3.PMR3Player.ServerProjectileSpawnV1` | `21590` | Server / Reliable / ForceValidate |
| `RPC:PMNet.R3.PMR3Player.ServerProjectileHitV1` | `33011` | Server / Reliable / ForceValidate |
| `RPC:PMNet.R3.PMR3Player.ClientProjectileDecisionV1` | `38620` | Client / Reliable / 无校验档位 |

`PMR3Player` 类协议摘要仍为 `0x5C7B1234`（属性集合未变）；
程序集整体摘要 `PMNetGeneratedRegistry.ProtocolHash`：`0xDDC346C5` → **`0x43DD5A42`**（新增类/ RPC 必然改变整体摘要，属预期）。

---

## 3. 回调 API（声明层实际对外面）

### 3.1 `PMR3Player`（投射物 RPC 的承载对象）

```csharp
public interface IPMProjectileNetworkDriver     // 只依赖 byte[]，不引核心
{
    void OnServerSpawnPayload(byte[] payload);  // DS：上行生成
    void OnServerHitPayload(byte[] payload);    // DS：上行命中
    void OnClientDecisionPayload(byte[] payload); // AP：下行裁决
}

public IPMProjectileNetworkDriver ProjectileDriver;   // null ⇒ 上行 ForceValidate Reject
public const int ProjectileMaxPayloadBytes = 4096;    // 声明层准入常量（与生成物读入门同值）
public long ProjectileDecisionAppliedCount;           // AP 本地计数
public long ProjectileDecisionDiscardedCount;         // AP 可观察丢弃计数
public static Action<string> ProjectileWarn;          // 告警出口（未接线时丢弃）
public void WarnProjectile(string message);

[PMServerRpc(Reliable, ForceValidate)] public void ServerProjectileSpawnV1(byte[] payload);
[PMServerRpc(Reliable, ForceValidate)] public void ServerProjectileHitV1(byte[] payload);
[PMClientRpc(Reliable)]                public void ClientProjectileDecisionV1(byte[] payload);

// 生成的调用桩（业务唯一合法入口）
PMNet_ServerProjectileSpawnV1 / PMNet_ServerProjectileHitV1 / PMNet_ClientProjectileDecisionV1
PMGeneratedRpcId_ServerProjectileSpawnV1 = 21590 / _ServerProjectileHitV1 = 33011 / _ClientProjectileDecisionV1 = 38620
```

`ForceValidate` 同伴 `ServerProjectileSpawnV1_ForceValidate` / `ServerProjectileHitV1_ForceValidate`
一律走 `ValidateProjectileUpstreamPayload`：**driver==null ⇒ Reject**、`null`/空 ⇒ Reject、
`>4096` ⇒ Reject，其余 Accept（`Report`/非法枚举按生成物的失败关闭语义处理）。
Reject 语义为「上报后跳过实现、**不断连**」（实测 `RpcDisconnectRequests == 0`）。

### 3.2 `PMR5Projectile`（投射物副本声明对象）

```csharp
[PMNetworkObject] public partial class PMR5Projectile : PMNetObject
{
    [PMReplicated] private byte[] _projectileSnapshotV1;      // 唯一复制属性
    public byte[] ProjectileSnapshotPayload { get; }          // 只读语义
    public void PublishProjectileSnapshot(byte[] payload);    // 走生成 Setter（PMNet_Set_projectileSnapshotV1）
    [PMRepNotify(nameof(_projectileSnapshotV1))] private void OnRep_ProjectileSnapshot();
    protected internal override void OnReplicatedCreate();                       // ← 真实基类 API
    protected internal override void OnReplicatedDestroy(PMObjectDestroyReason); // ← 真实基类 API
    public static void PMR5DispatchOnRep(PMNetObject target, ushort onRepMethodId); // 包装生成物私有表
    public int ReplicatedCreateCount; public int ReplicatedDestroyCount;        // 本地计数
}
```

`OnRep_ProjectileSnapshot` 只调 `PMR5ProjectileEvents.NotifyUpdated(this)`（**不解码、不改驱动状态**）。

### 3.3 `PMR5ProjectileEvents`（进程级唯一事件出口）

```csharp
public static Subscription Subscribe(PMNetWorld world,
        Action<PMR5Projectile> onCreated,
        Action<PMR5Projectile> onUpdated,
        Action<PMR5Projectile, PMObjectDestroyReason> onDestroyed);
public sealed class Subscription : IDisposable { public PMNetWorld World; public bool IsDisposed; }
public static void ClearAll();          // 仅进程整体 Shutdown 调用
public static int SubscriptionCount;    // 诊断/门禁
public static Action<string> Warn;
```

事件名严格为 **`Created` / `Updated` / `Destroyed`**（本批次以「订阅凭证 + 按 world 筛选」的形式落地，
而不是裸 `event` 字段：契约要求「事件须按 obj.World 筛选、各宿主 Dispose 取消自己的订阅、
不因一个 world 退出清其它 world 事件」，裸静态 event 无法表达这三点）。
分发为同步、按进入分发时的订阅集合快照，逐订阅者异常隔离。

### 3.4 `PMR3Runtime`

- `Attach(world, bridge)`：新增 `world.RegisterClass(PMR5Projectile.PMGeneratedClassId, CreateProjectileInstance)`
  （先 `IsClassRegistered` 查再注册，幂等）与
  `bridge.Replication.RegisterOnRepDispatcher(PMR5Projectile.PMGeneratedClassId, PMR5Projectile.PMR5DispatchOnRep)`；
  `PMR3Player` 的两条注册原样保留（**追加不替换**）。
- `Shutdown()`：追加 `PMR5ProjectileEvents.ClearAll()`。
- `Detach(world)`：**不碰**投射物事件（一个 world 退出不得清其它 world）。

---

## 4. 明确不包含（诚实边界）

1. **B2b 网络 driver 不存在**：本批次没有 `PMR5ProjectileDriver`/等价会话驱动，没有有界队列、
   主线程 Pump、codec 解码、身份准入、`Faulted`/`SessionFaulted` 出口、周期 Drain、Settlement。
   三条 RPC 的载荷语义（生成 spec、命中候选 ≤48 条、5 次 Verify 配额、身份与 epoch 校验）
   **一律未实现**；声明层只保证「字节可靠到达接缝」与「参数域准入」。
2. **没有 SpawnReserved 接线**：投射物对象的创建仍由测试用 `PMNetWorld.Spawn + RegisterReplicatedObject`
   演示；`TryReserveNetId` / `SpawnReserved` 属并行 World 组，本批次未调用、未假设。
   `PMR5Projectile` 也不提供 Spawn 辅助函数（契约原文：driver 自己写初值再上线）。
3. **没有宿主/表现**：Unity DS/客户端宿主未创建 driver、未采集目标历史、未生成可见子弹；
   T45（真网络/Unity 投射物全链）仍 `PENDING_USER`。
4. **无 R6 结算消费者**：没有伤害接收者，不存在「已扣血」的任何断言。
5. 工作副本里 `Client/Assets/Scripts/PMNet/World/PMNetWorld.cs`、`Client/Assets/Scripts/PMProjectile/**`、
   `Docs/plans/net-architecture-migration.md`、`Tools/PMNetWorldTest/**`、`Tools/PMBattleContentBuildCheck/**`
   的改动属**并行组**，本批次未触碰（主计划与协议契约均未改）。

---

## 5. 测试与实测证据（全部本次 build + run，退出码 0）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 声明同步 `decl-check` | `PMNetGen.dll --decl-check <PMR3Player.cs> <PMR5Projectile.cs> --out-dir .../Generated --id-lock Docs/plans/pmnet-r3-ids.json` | 退出码 0；2 类 / 4 属性 / 9 RPC；「3 个产物 + 锁文件」同步 |
| 新声明门禁 | `dotnet Tools/PMR5DeclarationTest/bin/Release/net8.0/PMR5DeclarationTest.dll` | **139 项 0 失败**（exit 0） |
| R3 运行时 | `dotnet Tools/PMR3RuntimeTest/.../PMR3RuntimeTest.dll` | **180 项 0 失败**（exit 0；原 179 项全保留） |
| R3 集成 | `dotnet Tools/PMR3IntegrationTest/.../PMR3IntegrationTest.dll` | **143 项 0 失败**（exit 0） |
| 语言面构建 | `dotnet build Tools/PMClientCheck -c Release` | 0 警告 0 错误 |
| Unity 胶水构建 | `dotnet build Tools/PMUnityGlueCheck -c Release` | 0 警告 0 错误 |
| R3 Unity 烟测构建 | `dotnet build Tools/PMR3UnitySmoke -c Release` | 0 警告 0 错误 |
| 新门禁构建 | `dotnet build Tools/PMR5DeclarationTest -c Release` | 0 警告 0 错误 |

### 5.1 `PMR5DeclarationTest` 覆盖（A42 / B21 / C9 / D12 / E19 / F11 / G18 / H7 = 139）

- **A 声明与单一生成集合（42）**：`--decl-check` 退出码 0；锁文件含新类/新属性/三条新 RPC 稳定键；
  **旧 ClassId 与 3 属性 + 6 RPC 的 ID 逐条按字面量比对未漂移**；共享锁未被改写；
  运行期注册表 2 类、新属性描述符（成员名/Push/None/OnRepId/Setter）逐项；
  **旧 6 条 RPC 仍全部注册且方向/可靠性/档位未变**（条目新增不替换旧注册）；
  新 3 条 RPC 的档位与生成桩常量同源；生成期摘要 == 运行期摘要。
- **B 真实派发 / 初始化 / 通知（21）**：真实 `PMTransport` 字节链（手工 link，非直调实现）下，
  Create 初值与创建**原子到达**（`Created` 回调里载荷已就位）；`Created` 事件按 world 命中；
  owner 经**生成桩**发 `ServerProjectileSpawnV1`/`ServerProjectileHitV1`，DS 的 fake driver
  收到**逐字节一致**的载荷，且 `PendingRpcCount == 0`（RemoteSender 已接线）；
  `PublishProjectileSnapshot` 后客户端 RepNotify 触发且快照收敛。
- **C 销毁（9）**：`DestroyObject` 后客户端 `OnReplicatedDestroy` 恰好 1 次、`Destroyed` 事件携带真实原因，
  且世界索引已摘除（不是只发事件）。
- **D 非 owner 拒（12）**：client2 的副本冒名上行 → 服务端 `RpcRejected` 增长、`RpcApplied` 保持 0、
  接收入口状态 `NotOwner`、driver **未收到载荷**；owner 对照仍被接受。
- **E driver 缺失的失败关闭（19）**：上行被 `ForceValidate` 判 `Reject`（`PMRpcValidationSink.OnReported`
  可观测），仍算「已投递并被处置」，且 **`RpcDisconnectRequests == 0`**（不断连）；
  下行无 driver 时 `ProjectileDecisionDiscardedCount == 1` 且 `AppliedCount == 0`（可观察丢弃），
  接上 driver 后同一条下行正常投递。
- **F 数组上限（11）**：4097 字节在**发送侧生成桩**抛 `FormatException`（未入队、未发出）；
  接收侧伪造 4097 长度前缀判 `Malformed` 且 driver 未收到；**恰好 4096 正常放行且逐字节一致**。
- **G 多 world 事件（18）**：两个客户端 world 各自订阅，事件不串台；`subA.Dispose()` 只摘自己；
  **`PMR3Runtime.Detach(worldA)` 后别的 world 的订阅仍在并继续收到 Created/Destroyed**。
- **H 清理（7）**：`Detach` 不清事件；`Shutdown()` 走 `ClearAll` 清空；之后旧凭证 `Dispose` 幂等。

### 5.2 声明层零核心依赖（契约硬要求）

`Tools/PMR5DeclarationTest` 只编 `PMNet/**` + `PMR3/{PMR3Player,PMR3Runtime,PMR5Projectile,Generated/**}`，
**刻意不引 `PMProjectile` 纯核心与 Coordinator**，编译并跑通 139 项 ⇒ 声明层未反向依赖核心实现；
旧消费者 `PMR3RuntimeTest` / `PMR3IntegrationTest` 同样无需新增核心引用（只补了新声明文件的 Include）。

### 5.3 观察记录（非缺陷）

复制层对**每一条送达的 Update 记录**都派发 OnRep（不做值比较），未确认属性跨帧重传，
因此一次业务变更实测触发 **4 次** `Updated` 通知。这与契约 §3.9.4「OnRep 是通知/入队、不是事件计数」
一致，故门禁断言口径为「至少 1 次 + 每次参数都是本 world 的那个副本 + 载荷最终收敛」，
不钉死次数（钉死次数等于在验收复制层的重传节奏）。

---

## 6. 剩余问题 / 交接

1. **B2b driver 是下一步的硬依赖**：真正解码/身份准入/有界队列/主线程 Pump/出口失败语义/周期 Drain/
   Settlement 全未实现；本批次只交付「声明承载 + 参数域准入 + 事件接缝」。
2. **投射物对象创建路径未接线**：等待并行 World 组的 `TryReserveNetId/SpawnReserved` 落地后，
   由 B2b driver 用「先写初始 snapshot 再 SpawnReserved」接入；当前测试用 `World.Spawn` 演示同一序。
3. **`PMR5ProjectileEvents` 用「订阅凭证」而非裸静态 event**：若主侧希望保持与 `PMR3Player.PlayerReplicated`
   完全同形的裸 event 形态，需要补一份能被「按 world 筛选 + 独立取消」的表达（当前形态是为满足契约原文）。
4. **`ProjectileMaxPayloadBytes = 4096` 与生成物 `PMGeneratedMaxArrayLength` 同值但两处独立声明**：
   生成物那一处由生成器常量固定，声明层这一处是契约要求的显式准入值；修改任一处都不影响另一处
   （不静默放宽线上预算）。
5. T5B2a（World 预留）与 T5B2c（真 Transport AP/DS/SP）仍 `PENDING`，分别属并行组与 B2b。
