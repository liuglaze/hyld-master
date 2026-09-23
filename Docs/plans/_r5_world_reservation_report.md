# R5-B2a：World NetId 预留能力（能力令牌）实施报告

- 计划项：**T5B2a（World 预留）** — 场景：取消 / 复用 / 容量 / 跨 world / 初始化
- 契约：`Docs/plans/net-r5-network-contract.md`「World 预留（新增兼容 API）」段；`Docs/plans/net-r0-contract.md` §2.1（D-R0-01/02/03/04）
- 边界：只改 **2 个代码文件 + 本报告**；未改 `PMNetIdentity.cs`、未改其它组文件、未改共享契约与主计划、未提交、未启动 Unity
- 结论：**oracle-local PASS（build exit 0 / run exit 0）**。这不是 T45、也不是双端实机验收（T45 仍 PENDING_USER）

---

## 1. 交付 API（精确签名）

`Client/Assets/Scripts/PMNet/World/PMNetWorld.cs`（同文件新增）：

```csharp
// PMNetWorld
public int  ReservedNetIdCount { get; }                                   // 未消费预留数
public bool TryReserveNetId(out PMNetSpawnReservation reservation, bool isStatic = false);
public bool CancelReservedNetId(PMNetSpawnReservation reservation);
public bool SpawnReserved(PMNetObject obj, PMNetSpawnReservation reservation,
                          uint classId, uint archetypeId = 0u);
public void Dispose();                                                    // = Reset() + 停止签发预留（幂等）
public void Reset();                                                      // 既有方法：新增作废全部预留

// PMNetSpawnReservation（同文件 public sealed，新增类型）
public PMNetId NetId { get; }            // 唯一的公开成员：预留到的真实身份（含 IsStatic）
internal PMNetWorld World { get; }       // 签发者（引用相等 = 跨 world 判据）
internal uint        Epoch { get; }      // 签发时会话 epoch
internal bool        IsConsumed { get; } // 已消费/作废
internal void        Invalidate();
internal PMNetSpawnReservation(PMNetWorld world, PMNetId netId, uint epoch);  // internal 构造
```

既有公开 API 签名与语义未变（`Spawn` / `DestroyObject` / `AddConnection` / `BuildLifecycleBatch` /
`OnLifecycleMessage` / 两个生命周期回调）。

---

## 2. 语义落地（逐条对照契约）

| 契约条款 | 实现落点 |
|---|---|
| 能力令牌、internal 构造、`NetId` 只读 | `PMNetSpawnReservation`（`_world/_netId/_epoch/_consumed` 全私有；只有 `NetId` 公开） |
| 禁止只凭 rawId 作为凭据 | 消费时 `_reservations[rawId]` 必须**引用相等**于传入令牌；跨 world 令牌的 rawId 即使相同也拒绝 |
| 只允许服务端 | `TryReserveNetId` 先判 `IsServer`（`null` session 也拒绝）；令牌只可能由服务端世界签出 |
| 使用既有 `_allocator`、ID 永不复用 | `_allocator.Allocate(isStatic)`；取消/Reset/Dispose 都不回退分配器 |
| 预留：不登记对象、不发 Create、不占存活表 | 预留只进 `_reservations`；不碰 `_byId/_allObjects/_liveObjects`，不排 `PendingEvent` |
| live + reserved 共享 `_maxObjects` | 容量判据统一为 `_byId.Count + _reservations.Count` |
| 普通 `Spawn` 也计预留 | `Spawn` 的容量判据已改为同一表达式（无预留时与改造前逐值等价） |
| 取消只删预留、不返还编号 | `CancelReservedNetId` 只 `Remove` + `Invalidate`，不动 `_allocator` |
| 完整 isStatic 匹配 | `mine.NetId.Equals(reservation.NetId)`（`PMNetId` 完整结构比较，Value+IsStatic），不只比 rawId |
| 上线后一次性消费 | 成功后 `Remove` + `Invalidate`；重复消费/取消一律拒绝 |
| 不能伪造 / 跨 world / 过期使用 | `IsReservationOwned` 四道判定：world 引用相等、epoch 相等、未消费、注册表实例相等+完整身份相等 |
| 成功前所有拒绝不丢预留 | 所有拒绝路径都在 `Remove`/`Invalidate` **之前**；失败后可用同一令牌重试 |
| 复用既有登记/生命周期入队逻辑 | `Spawn` 与 `SpawnReserved` 共用私有 `RegisterAndEnqueue(obj,id,classId,archetypeId)`（唯一一份语义） |
| Dispose/会话清理清掉预留、epoch 变更不复活 | `Reset()` → `InvalidateAllReservations()`；`Dispose()` = `Reset()` + `_disposed`（之后不再签发） |
| epoch 变更不复活 | 令牌存 `_epoch`；`IsReservationOwned` 比对 `CurrentEpoch`（`null` session 记为 0） |
| 既有 Spawn 行为不改 | 仅容量表达式与告警文案变化（无预留时逐值同值）；未给 `Spawn` 增加任何新前置条件 |
| 线程按 World 既有规范 | 无新锁、无新线程；唯一性由预留表 + `PMNetIdAllocator` 的主线程检查共同保证 |

---

## 3. 关键设计决策（为什么这样做）

1. **凭据是"实例"而不是"号码"**。`PMNetIdAllocator` 是**每 world 一个**，所以两个 world 都可以有
   `NetId=1`。若把 `uint` 当凭据，B world 会把 A world 的号当成"自己的预留"放行。因此 world 侧保存
   `rawId → 令牌实例`，消费要求引用相等；伪造一个同 world 同 rawId 的令牌（`internal` 构造在
   同程序集内可见，测试正是这样构造的）也会被注册表判定挡住。
2. **容量在"预留"处收口，消费不再新增占用**。预留即占名额，`SpawnReserved` 因此只做防御性的
   `> max` 检查（`== max` 允许）—— 否则"预留已满"的世界永远无法把预留转成存活对象。
3. **拒绝顺序 = 先验令牌/对象/状态/容量，后作废令牌**。这是"失败不丢预留"的机械保证：任何一处
   `return false` 都不会动 `_reservations`/`_consumed`。
4. **`RegisterAndEnqueue` 是唯一一份注册语义**。预留消费必须是"同一个 Spawn，只是号提前"，
   不允许出现"预留路径把 `Role/NetMode/State/入队` 写歪"的分叉。
5. **`Dispose` 故意不给 `Spawn` 加前置条件**。契约写的是"Dispose/清理清掉预留…既有 Spawn 行为不改"，
   所以 `Dispose` 只作废预留并停止签发；`Spawn` 保持改造前的可调用性（`Reset` 后世界本来就可复用）。
   该取舍被 J11m 显式固化成断言，B2b 若要"Dispose 后全体拒绝"须另行提出，不在本阶段偷偷改。

---

## 4. 测试（T5B2a）

新增第 J 段（`Tools/PMNetWorldTest/Program.cs`，夹具 `ReservedNode` + `ClassReservedNode=103`）：

```
dotnet build Tools/PMNetWorldTest -c Release      → 已成功生成，0 错误（5 个既有 CS0649 警告，非本次引入）
dotnet Tools/PMNetWorldTest/bin/Release/net8.0/PMNetWorldTest.dll
                                                  → 全部通过：333 项检查，0 项失败（exit 0）
                                                    （其中 J 段 136 项；改造前基线 197 项）
```

| 组 | 覆盖 |
|---|---|
| J1 | **先预留不发任何 Create**：无对象/无槽位/查不到/无待发/批次为 null/`CreatesSent==0`，但 `LastAllocated` 已前进 |
| J2 | 客户端侧、无会话 world 一律拒绝签发（`out` 为 null） |
| J3 | **取消后下一个 ID 不复用**（严格大于）、取消不返还编号、重复取消 false、已取消令牌不可消费 |
| J4 | **跨 world 相同 rawId 不能消费**：A↔B 双向拒绝、各自预留不互相误删、各自令牌仍可消费 |
| J5 | 伪造（未进注册表）/epoch 不符/无效身份/null 令牌全部拒绝，且**不吃掉真实预留** |
| J6 | **isStatic**：静态位按预留参数记录、共用单调编号空间、伪造"同号不同静态位"被拒、Create 记录携带同一静态身份、消费后号不复用 |
| J7 | **SpawnReserved 初值在 Create 回调前就绪**：手写 snapshot 在 `OnReplicatedCreate` 里已是 4242；创建回调一次；入队 1 条 Create |
| J8 | **重复消费**同一令牌被拒；已消费令牌不可取消；不产生第二个对象、统计只加一次 |
| J9 | **错误状态失败不丢令牌**：null 对象、`Active` 对象被拒后令牌仍在，修正后可成功 |
| J10 | **reserved + Spawn 容量**：上限 2 时第 3 个预留被拒；普通 Spawn 计预留被拒且不消耗编号；预留→存活占用不变；满容量再拒；销毁腾出容量 |
| J11 | **dispose 清理**：`Reset`/`Dispose` 清掉预留、旧令牌失效且不复活、Reset 不复位分配器、Dispose 幂等、`Dispose` 后不再签发 |
| J12 | 预留不参与"新连接补齐"（无幽灵 Create）；上线后才排 Create |
| J13 | 预留创建的对象走完正常生命周期（Create→Destroy），统计与普通路径同源 |
| J14 | **普通 Spawn 回归**：无预留时容量/拒绝/不消耗编号/计数/销毁腾位/编号单调逐项与改造前同值 |

**诚实口径**：以上是纯核心（netstandard2.0/C#7.3 源码 + net8.0 测试宿主）的 oracle-local 断言，
**不包含真网络、不包含 Unity、不包含 driver 接线**。J 段断言的是 World 自身的记账与拒绝边界。

---

## 5. 回归门禁（build / run）

| 门禁 | 结果 |
|---|---|
| `PMNetLangCheck`（netstandard2.0 + C#7.3，编全部 PMNet 源） | build 0 错误 / 0 警告 |
| **`PMNetWorldTest`（本次目标）** | build 0 错误；run **333/0，exit 0** |
| `PMNetSessionTest` | build 0 错误；run 405/0 |
| `PMNetE2E` | build 0 错误；run 209/0 PASS |
| `PMCallspaceCheck` | build 0 错误；run 93/0 |
| `PMReplicationTest` | build 0 错误；run PASS（缺陷注入 5/5 命中） |
| `PMTransportTest` | build 0 错误；run 173/0 |
| `PMUdpAdmissionTest` | build 0 错误；run 382/0 |
| `PMDeclCheck` | build 0 错误；run 全部通过 |
| `PMR3RuntimeTest` | build 0 错误；run **180/0**（首轮曾出现 2 项失败，是并行 B2a 声明组正在改写 PMNetGen/生成物造成的瞬时竞态；待其落盘后重跑全绿） |
| `PMR3IntegrationTest` / `PMR4NetworkTest` / `PMR4IntegrationTest` | build 0 错误；run 143/0、PASS、113/0 |
| `PMProjectileIntegrationTest` / `PMProjectileWireTest` | 952 断言通过 / 19 PASS 0 FAIL |
| `PMNetLaunchCheck` / `PMNetVerify` / `PMUnityGlueCheck` / `PMMoverTest` / `PMMoverCoreCheck` | build 0 错误 |

---

## 6. 风险 / 未决（给 B2b 与复核）

1. **容量语义边界**：`SpawnReserved` 允许 `live+reserved == max`（消费不新增占用）。若 B2b 希望
   "预留转正后仍留一格给别的对象"，需改契约而不是改这里 —— 当前实现被 J10h/J10k 固化为"占用不变"。
2. **`Dispose` 不封 `Spawn`**（有意，见 §3.5）。这是契约字面取舍，被 J11m 固化；如复核认为不妥，
   应作为契约变更处理。
3. **`internal` 可见性不是安全边界**：本仓测试把 PMNet 源码编进测试程序集，因此"同程序集攻击者"
   能看见 `internal` 构造。安全性来自 world 侧 `_reservations` 的引用相等判定，不是构造可见性。
   跨程序集消费方（B2b driver）只能拿到 world 签发的实例，无法自造。
4. **诊断口径扩展**：`Stats.RejectedOverCapacity` 现在也计预留拒绝（原语义是"创建被拒"）；
   `Spawn` 的容量告警文案改为"对象数（存活 + 预留）已达上限"。两者都是**诊断可观察**变化，
   已 grep 确认无任何测试/代码依赖旧文案；若线上有按文案聚合的看板需同步。
5. **无专门预留统计**：`PMNetWorldStats` 未新增字段（避免动公共结构）；预留泄漏只能通过
   `ReservedNetIdCount` 与 `RejectedOverCapacity` 观察。B2b 若需要"取消/TTL 淘汰"计数需另行提出。
6. **本阶段不含 driver**：契约里"Pending 生成在 RequestSpawn 之前预留、拒绝/TTL/断开 Cancel、
   Confirmed/SpawnReady 才 `SpawnReserved`"属于 B2b。本阶段只提供 World 侧原语，**没有**
   "预留 + snapshot 打包成一次原子上线"的包装 —— 由 driver 自己先写好对象初值再调 `SpawnReserved`。
7. **未验证**：真实 Transport、双连接 AP/SP、Unity 宿主、弱网（均属 T45 / T5B2c，`PENDING_USER`）。

---

## 7. 变更文件清单

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMNet/World/PMNetWorld.cs` | +314/-5：令牌类型、4 个公开 API + `Dispose`、容量记账、`RegisterAndEnqueue` 抽取、`IsReservationOwned`、`Reset` 作废预留 |
| `Tools/PMNetWorldTest/Program.cs` | +389：第 J 段（J1–J14，136 项）与 `ReservedNode` 夹具 |
| `Docs/plans/_r5_world_reservation_report.md` | 本报告（唯一文档写入） |

未触碰：`PMNetIdentity.cs`（`PMNetId`/`PMNetIdAllocator`/`PMSession`）、`PMNetObject.cs`、
`PMNetLifecycleCodec`、PMR3/PMR4/PMProjectile 任何文件、共享契约与主计划。
