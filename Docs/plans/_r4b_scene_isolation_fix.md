# R4-B 物理世界隔离修复（scene isolation fix）

> 范围：**续做**上一组取消后的 R4-B/B3 物理隔离缺口。只动 hyld 宿主 / Editor 验证 / 两个 Tools 桩件 / 本报告。
> 不改 UE、不提交、不启动第二 Unity、不抢编辑器锁、不递归委派。
> 语言面：Unity 2019.4 / C# 7.3 / netstandard2.0；源文件 UTF8 BOM + CRLF（本报告按 `_r4b_*.md` 既有口径用 LF）。

---

## 0. 摘要（≤1500 字）

**问题**：两宿主用 `new PMUnityMoverCollisionQuery(mask, 1, allowlist)`（2 参重载）构造查询，该重载内部固定绑定
`Physics.defaultPhysicsScene` ⇒ 查询实际打在**默认物理世界**（大厅/旧地图）。layerMask 取自白名单自身 layer，
白名单只是 `IsAllowed` **后置过滤**，饱和检查又在过滤**之前** ⇒ 真实地图同层 Collider ≥32 时不是穿墙而是**整局失败**。

**修复**：隔离改为「**独立物理世界**」。`PMR3TestScene` 新增兼容接口 `BuildIsolated(...)`：
`SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics3D))` 建本地物理场景，地板/墙进去，
返回前硬校验 `physicsScene.IsValid() && !physicsScene.Equals(Physics.defaultPhysicsScene)`（2019.4：无本地物理场景时
`GetPhysicsScene()` 返回默认世界且 `IsValid()==true`，「只看 IsValid」即假隔离）。两宿主改走该入口，查询改**全参构造**；
白名单仍是地板+墙，**未**放松饱和门、**未**靠挑 layer 绕过。旧 `Build` 签名保留并被复用；新增 `EnsureCreatedInScene`
显式搬迁+校验对象确在隔离场景。失败/Stop/Dispose 均释放场景与对象（先对象后场景），活动场景临时切换必还原，
不改 `autoSimulation`、不调 `Simulate`，`Physics.SyncTransforms()` 仍每宿主帧一次。

**Editor 验证**：删除 preview-scene 假隔离路径；隔离判定加「非默认世界 + `IsEmpty()`」；新增 40 干扰体用例
（干扰体在另一 `LocalPhysicsMode.None` 临时场景 ⇒ 注册在默认世界），断言「隔离世界**无白名单**扫掠不被挡」
且「默认世界同段扫掠被挡/顶到容量显式失败」；编辑模式用真实 API 结果判定，失败即报告「仅 Play 可执行」并提前返回；
跑前后对比用户场景 `isDirty`。真实 PhysX 菜单未运行 ⇒ **PENDING_USER**。

**宿主缺陷**：场景失败分支改走 `ReleaseSession`（D1）；AP uid 不符显式 `Fail` 不降 SP（D2）；`EnsureMovementRig`
失败显式 `Fail` 不再以探针假成功掩盖；同局第二个 AP 拒绝；`GetSnapshot().ServerFrame` → `Timeline.PendingServerFrame`（D4）；
`PumpActive` 遇 `Endpoint.IsDisposed` → Fail+Freeze（D5）；保留每渲染帧 `PollHardware`。

**证据**：三个 Tools **Rebuild 0 错误 0 警告**；`PMR4IntegrationTest` 仍 **113/0**。真实 Unity/PhysX、真实 DS 二进制、
编辑模式本地物理可用性 ⇒ **PENDING_USER**（不宣称）。

---

## 1. 续接点（与 checkpoint / 主侧核对一致）

- checkpoint：`Saved/CLI-Delegation/checkpoints/cli-delegation-comprehensive-1789893419185-5e9c3b2a-a91a-4f91-bb22-547610c3c4da.md`（`CHECKPOINT_STATUS: FALLBACK`，收尾原因 cancelled，阶段 finishing）。
- 主侧核盘结论：前组**仅**在 `PMDsSessionHost.cs` 第 30 行新增 `using UnityEngine.SceneManagement;`，其它本项文件未动、报告未创建。
- 本次实际核对：上述 using 在位（现第 30 行）且被本次新代码真实使用；`PMClientSessionHost.cs` / `PMR4UnityValidation.cs` /
  两个桩件均无前组残留改动；`Docs/plans/_r4b_scene_isolation_fix.md` 本次新建。
- 已落地可直接消费的 API（本次消费，未重复实现）：`PMPredictionTimeline.PendingServerFrame`（第 339 行，无 clone）、
  `PMUdpSessionEndpoint.IsDisposed`（第 350 行）。
- **非重做**：本报告取代前组的半成品检查点，不从头重跑 §必读文档之外的调查。

## 2. 必读文档（按委派指定顺序，全篇读完）

1. `D:/UGit/hyld-master/AGENTS.md`
2. `Client/Assets/AGENTS.md`（客户端现状、DS 身份判定、`frameTime=0.016`、旧链抑制）
3. `Server/AGENTS.md`（服务端「不含 Unity」的历史边界；LZJUDP / BattleLoop 与本次无关）
4. `Docs/plans/net-r4-network-contract.md`（§B2 适配器「不能让旧地图/表现胶囊/其它局参与查询」、
   §B3「主线程在 endpoint.Pump 前 Physics.SyncTransforms 一次」、末尾复核修订）
5. `Docs/plans/_r4b_host_final_review.md`（D1/D2/D3/D4/D5 与 P1/P3/P4 的原始判据与行号）
6. `Docs/plans/_r4b_physics_final_review.md`（§2.1 假隔离、§2.2 活动场景、§2.3 饱和次序、§2.6 弱断言）
7. `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（bash = Git Bash 语义，未混用 PowerShell）

**由文档决定的首搜入口（非盲搜）**：`Server/Boot/PMDsSessionHost.cs`（`PMR3TestScene` + `Initialize` + `Dispose`）、
`Server/Boot/PMClientSessionHost.cs`（`Enter`/`Stop`/`PumpActive`/`PumpMovement`/`OnPlayerReplicated`/`ReleaseSession`）、
`Client/Assets/Editor/PMR4UnityValidation.cs`、`Tools/PMClientCheck/ClientStubs.cs`、`Tools/PMUnityGlueCheck/UnityStubs.cs`、
`PMUnity/PMUnityMoverCollisionQuery.cs`（唯一消费者契约）、`PMPrediction/PMPredictionTimeline.cs`、`PMNet/Session/PMUdpSessionEndpoint.cs`。

## 3. 已改文件与逐项改动

### 3.1 `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（DS 宿主 + `PMR3TestScene`）

| 位置 | 改动 |
|---|---|
| `PMR3TestScene.BuildIsolated(...)`（新增） | `SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics3D))` → 校验 `scene.IsValid()/isLoaded` → `scene.GetPhysicsScene()` → 校验 `IsValid()` 且 **`!= Physics.defaultPhysicsScene`** → 临时 `SetActiveScene(隔离场景)` 后复用旧 `Build` → `EnsureCreatedInScene` → finally 还原原活动场景。任何一步失败都 `ReleaseIsolatedScene` + 返回 false（**不**退回默认世界）。 |
| `PMR3TestScene.EnsureCreatedInScene(...)`（新增） | 把新建对象显式 `MoveGameObjectToScene(隔离场景)`，并逐对象校验 `go.scene.handle == scene.handle`；不成立即显式失败（防止「以为切了活动场景、其实建到宿主/用户场景」）。 |
| `PMR3TestScene.ReleaseIsolatedScene(Scene)`（新增） | 幂等、不抛；只用**运行时** `SceneManager.UnloadSceneAsync(Scene)`（本类同时被 DS 构建与客户端编译，**不得**引用 UnityEditor，故不用 `EditorSceneManager.CloseScene`）。 |
| `PMR3TestScene.Build(...)` | **签名与行为保留**（旧调用方兼容），成为 `BuildIsolated` 的内部步骤。 |
| `Initialize` 步骤 7 | 由 `Build(...)` 改为 `BuildIsolated(...)`；落 `_movementScene` / `_movementPhysicsScene`。 |
| `Initialize` 步骤 7b | 新增 `!_movementPhysicsScene.IsValid() \|\| Equals(Physics.defaultPhysicsScene) → 失败`；查询改**全参构造** `new PMUnityMoverCollisionQuery(_movementPhysicsScene, mask, 1, allowlist)`；构造后再复核 `_movementQuery.Scene` 非默认世界。 |
| `Dispose` | 在销毁 `_sceneObjects` 之后 `PMR3TestScene.ReleaseIsolatedScene(_movementScene)`（先对象、后场景），并清 `_movementScene/_movementPhysicsScene`；失败路径由 `Start` → `host.Dispose()` 覆盖。 |
| `Info(...)` / `Describe()` | 增加 `隔离=本地物理场景(<name> handle=<n> isDefaultWorld=<b>)` 与 `movementScene=` / `isolated=` 诊断字段，使隔离可审计。 |

### 3.2 `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`（客户端宿主）

| 位置 | 改动 | 对应复审项 |
|---|---|---|
| `Enter` 步骤 4 | 改走 `BuildIsolated`；失败分支由「只 `PMR3Runtime.Detach` + `return`」改为 `ReleaseSession(session); log; return;`（与相邻 4 个失败分支逐字同构）。 | **D1** |
| `Enter` 步骤 4b | 新增隔离复核（非默认物理世界）；查询改全参构造；构造后复核 `session.Query.Scene`。 | D3/C8 |
| `Enter` 失败链 | 步骤 4/4b/白名单全失败分支都走 `ReleaseSession`（释放场景 + 对象 + 桥 + 静态 Warn）。 | C5/D1 |
| `OnPlayerReplicated` | `isOwner && uid 不符` → `Fail(session, ...)` 并 return（**不再降为 SP**）。 | **D2** |
| `OnPlayerReplicated` | 新增**同局多 AP 拒绝**：`session.OwnerPlayer` 非空且非同一 `player` → `Fail`。 | P3 |
| `OnPlayerReplicated` | `EnsureMovementRig` 返回 null → `Fail(session, ...)` 并 return（**不再照常排队探针**）。 | 假成功路径 |
| `EnsureMovementRig` 日志 | 去掉「不影响探针」等误导措辞，改为「调用方会显式整局失败」。 | 同上 |
| `TickAutonomousProxies` | `rig.Driver.Timeline.GetSnapshot().ServerFrame` → `rig.Driver.Timeline.PendingServerFrame`（每渲染帧最多 8 子步不再深克隆 Sync/Aux）。 | **D4** |
| `PumpActive` | 新增 `session.Endpoint == null \|\| IsDisposed → Fail + FrozenWallClockPump + return`。 | **D5** |
| `PumpMovement` | **保留** `session.Input.PollHardware()`（每渲染帧一次，不足 1ms 的帧也缓存边沿；`Sample` 内部那次是幂等必要重复）。 | P4 |
| `ReleaseSession` | 销毁 `SceneObjects` 后 `PMR3TestScene.ReleaseIsolatedScene(session.MovementScene)`，清 `MovementScene` / `OwnerPlayer`。 | C5 |
| `DescribeMovements` | 增加 `scene=` / `isolated=` 诊断（心跳与门禁对账可读）。 | 可审计性 |

### 3.3 `Client/Assets/Editor/PMR4UnityValidation.cs`（真实 PhysX 验证入口）

- **删除 preview-scene 路径**：`EditorSceneManager.NewPreviewScene` 不再参与隔离（预览场景不保证带本地物理世界 ⇒
  `GetPhysicsScene()` 回退默认世界 ⇒ §2.1 的假隔离）。`TryCreateIsolatedPhysicsScene` 现在只有一条路：
  `SceneManager.CreateScene(name, CreateSceneParameters(LocalPhysicsMode.Physics3D))`。
- **隔离判定加强**：`isolatedScene` 断言从「`scene.IsValid() && physicsScene.IsValid()`」改为
  「`scene.IsValid() && physicsScene.IsValid() && !physicsScene.Equals(Physics.defaultPhysicsScene)`」；
  报告额外打印 `scene=` / `handle=` / `isDefaultWorld=` / `physicsEmpty=`（`IsEmpty()` 作为“全新空世界”的正向信号）。
- **编辑模式不假设**：照常调用真实 API，若抛异常或 `GetPhysicsScene()` 等于默认世界，则
  `PlayerSafeClose` 关掉那个（空且未修改的）临时场景，报告写明「`SceneManager.CreateScene` 是运行时 API（文档原文 "at runtime"），
  本验证只在 Play 模式下执行」，并提前 return；**绝不**改用用户场景 Collider 假装隔离。
- **活动场景**：新增 `SwitchActiveScene(target, out previous)`；对象创建期间临时切到临时场景，`finally` 必还原；
  另加跑前/跑后 `SceneManager.GetActiveScene().isDirty` 对比，变化写进报告尾注（公开 API 无法回滚脏标记，如实写明）。
- **新增隔离对照用例** `defaultWorldIsolation`（`CheckDefaultWorldIsolation`）：
  1. 建**第二个临时场景**（`LocalPhysicsMode.None` ⇒ 无本地物理世界 ⇒ 其 Collider 注册在 `Physics.defaultPhysicsScene`）；
  2. 放 **40** 个薄盒干扰体（`InterferenceBodyCount=40` > `HitBufferCapacity=32`，位于 `x=10`，远离墙盒 `x∈[-0.5,0.5]`，
     沿 `+Z` 方向间距 0.05、尺寸 0.2 ⇒ 与胶囊扫掠全部相交）；
  3. **隔离世界 + 无白名单**（`allowlist=null`）扫掠 → 必须 `!Blocking`（无白名单是关键：不能靠后置过滤伪装隔离）；
  4. **默认世界 + 白名单=干扰体本身**扫掠 → 必须 `Blocking` 或顶到容量 `InvalidOperationException`（显式失败）；
  5. 断言 `interferenceIsDefaultWorld && isolatedUnaffected && defaultSeesInterference`；
  6. 干扰体与其临时场景在本方法内创建、在本方法内清理（`RemoveObject` + `PlayerSafeClose`），不污染用户场景。

### 3.4 `Tools/PMClientCheck/ClientStubs.cs` 与 `Tools/PMUnityGlueCheck/UnityStubs.cs`（桩件）

按「补真实签名、**不桩本项目类型**」补足宿主新用到的 Unity 面（两文件对称）：

- `UnityEngine.PhysicsSceneExtensions.GetPhysicsScene(this Scene) -> PhysicsScene`（真实在 **PhysicsModule**，不在 CoreModule）；
- `PhysicsScene.Equals(PhysicsScene)` / `Equals(object)` / `GetHashCode()` / `op_Equality` / `op_Inequality`
  （真实 2019.4 的 equality 面，已用 MetadataLoadContext 核实）；
- `UnityEngine.SceneManagement.CreateSceneParameters`（ctor 取 `LocalPhysicsMode`，属性名 `localPhysicsMode`）、
  `LocalPhysicsMode { None=0, Physics2D=1, Physics3D=2 }`（与真实枚举值一致）；
- `SceneManager.CreateScene(string)` / `CreateScene(string, CreateSceneParameters)` / `SetActiveScene(Scene) -> bool` /
  `GetActiveScene()` / `MoveGameObjectToScene(GameObject, Scene)` / `UnloadSceneAsync(Scene)`；
- `Scene.handle (int)` / `isLoaded` / `IsValid()`（ClientStubs 原有 `name/path/buildIndex/GetRootGameObjects` 保留）；
- `GameObject.scene -> Scene`（用于 `EnsureCreatedInScene` 的场景校验）。

**有意简化并注明**：桩件 `SceneManager.UnloadSceneAsync` 返回 `void`（真实为 `AsyncOperation`，唯一调用点是语句、忽略返回值，
避免再引入 `AsyncOperation` 类型）；`PhysicsScene` 无字段、`Equals` 恒 true，**不**模拟“是否默认世界”语义
⇒ 门禁通过 ≠ 隔离正确。未新增任何本项目类型替身（Driver/Physics/Session/PMNet/PMMover/PMPrediction 仍全编真实源码）。

## 4. 隔离判据（为什么必须是「物理世界」而不是 layer / 白名单）

- `PhysicsSceneExtensions.GetPhysicsScene(Scene)` 的真实语义（2019.4 文档）：
  「Alternately the Scene may be using the default 3D physics Scene (`Physics.defaultPhysicsScene`) in which case that will be returned instead.」
  ⇒ 只在「场景自带本地物理世界」时才有隔离；`IsValid()` 对默认世界同样为 true，**不能**作为隔离判据。
- layerMask 取自白名单自身 layer（地板/墙 Default(0)）⇒ 掩码≈“Default 层全体”，对旧地图**零隔离作用**；
  白名单是 `IsAllowed` 后置过滤，且 `RequireNotSaturated` 在过滤**之前**触发 ⇒ 旧地图同层 ≥32 命中是**整局失败**而非穿墙。
- 因此本次**不**给地板/墙挑 layer、**不**把饱和降级为“按已返回命中裁定”（那会削弱契约「绝不静默漏墙」），
  而是把隔离下沉到物理世界：查询只可能看到隔离场景里的地板/墙，饱和在真实宿主里结构上不再可达。
- 静态碰撞只需查询（`CapsuleCast`/`OverlapCapsule`/`ComputePenetration`），**不需要** `PhysicsScene.Simulate`：
  本次未调用 `Simulate`，未修改 `Physics.autoSimulation`，未修改任何场景的 `active` 语义（临时切换必还原）。

## 5. 清理逻辑（失败 / Stop / 换局）

| 路径 | 顺序 |
|---|---|
| `BuildIsolated` 内部失败 | 关掉临时场景（`ReleaseIsolatedScene`）并置 `scene=default`；`created` 里的对象交由调用方列表销毁。 |
| DS `Initialize` 任一后续步骤失败 | `Start` → `host.Dispose()`：端点/控制通道/世界/桥 → 运动 Driver `Freeze+Dispose` → 销毁 `_sceneObjects` → `ReleaseIsolatedScene(_movementScene)`。 |
| DS `Dispose`（正常退出） | 同上的幂等路径（`_disposed` 闸）。 |
| 客户端 `Enter` 失败分支 | `ReleaseSession`：`ReleaseMovements`（表现 `Dispose` → 驱动 `Freeze+Dispose`，清 `Query`/`Input`）→ `PMR3Runtime.Detach` → 还原静态 `Warn` → 桥 `Dispose` → 销毁 `SceneObjects` → `ReleaseIsolatedScene(MovementScene)`。 |
| 客户端 `Stop` | `_active` 置空 → 退订 `PlayerReplicated` → `Endpoint.Dispose` → `ReleaseSession` → `PMClientSessionHostDriver.Release`。 |
| Editor 验证 | 单个 `finally`：销毁全部测试对象 → 关临时场景（Play: `UnloadSceneAsync`；Edit: `EditorSceneManager.CloseScene`）→ 还原活动场景 → 记录 `isDirty` 变化。干扰体用例自带 `finally`（对象 + 场景）。 |

## 6. API 事实（本机实测，非虚构）

以 `MetadataLoadContext` 直接读 `D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine/*.dll`：

- `PhysicsScene`：实现 `IEquatable<PhysicsScene>`，成员含 `Equals(PhysicsScene)`、`Equals(object)`、`GetHashCode`、
  `op_Equality`、`op_Inequality`、`IsValid()`、`IsEmpty()`、`Simulate(float)`、`CapsuleCast`（数组/out 两种重载）、`OverlapCapsule`。
- `PhysicsSceneExtensions.GetPhysicsScene(Scene) -> PhysicsScene`（命名空间 `UnityEngine`，程序集 **UnityEngine.PhysicsModule**）。
- `SceneManager.CreateScene(string, CreateSceneParameters) -> Scene`、`SetActiveScene(Scene) -> bool`、`GetActiveScene() -> Scene`、
  `UnloadSceneAsync(Scene) -> AsyncOperation`、`MoveGameObjectToScene(GameObject, Scene)`。
- `CreateSceneParameters` 唯一 ctor 签名 `(LocalPhysicsMode physicsMode)`，属性 `localPhysicsMode`；
  `LocalPhysicsMode` 值 `None=0, Physics2D=1, Physics3D=2`。
- `Scene`：`handle : Int32`、`isLoaded`、`isDirty`、`IsValid()`、`name`。
- `EditorSceneManager`：`NewPreviewScene` / `IsPreviewScene` / `ClosePreviewScene` / `CloseScene(Scene,bool)` / `MarkSceneDirty` 均在（本次只保留 `CloseScene` 清理与不再使用 preview）。
- `UnityEngine.PhysicsScene.IsValid` / `IsEmpty` 在文档中均为 **M:**（方法）而非属性。

## 7. 编译与回归证据

```
dotnet build Tools/PMClientCheck      -c Release -t:Rebuild   → 0 警告 / 0 错误
dotnet build Tools/PMUnityGlueCheck   -c Release -t:Rebuild   → 0 警告 / 0 错误
dotnet build Tools/PMR4UnityCheck     -c Release -t:Rebuild   → 0 警告 / 0 错误   （真实 2019.4 DLL，DEFINE UNITY_EDITOR）
dotnet build Tools/PMR4IntegrationTest -c Release              → 0 警告 / 0 错误
dotnet Tools/PMR4IntegrationTest/bin/Release/net8.0/PMR4IntegrationTest.dll
   → 「全部通过：113 项检查，0 项失败」（A–G 全通过，未回归）
```

编译产物交叉核对（二进制符号存在性，确认不是“编了 0 行”的假绿）：

- `PMR4UnityCheck.dll` 含 `PMR4UnityValidation` / `CheckDefaultWorldIsolation` / `SwitchActiveScene` / `BuildIsolated` /
  `ReleaseIsolatedScene` / `EnsureCreatedInScene` / `IsDisposed` / `PendingServerFrame` / `CreateSceneParameters` / `PhysicsSceneExtensions`。
- `PMClientCheck.dll` / `PMUnityGlueCheck.dll` 含 `BuildIsolated` / `ReleaseIsolatedScene` / `EnsureCreatedInScene` /
  `IsDisposed` / `PendingServerFrame` / `CreateSceneParameters` / `PhysicsSceneExtensions`。

源码卫生：5 个被改文件均 `BOM=true`、`bareLF=0`、全部 CRLF、结尾 CRLF（python 二进制核对）。

## 8. 未验 / PENDING_USER（不宣称）

1. **真实 Unity 内运行**：`Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）` 菜单**未运行**（禁止自动起 Unity / 抢锁）。
   因此 `defaultWorldIsolation`、`saturationFailsExplicitly`、`walkStopsAtWall`、`jumpLandsBackOnFloor`、
   `startOverlapPathExercised`、`groundTouching` 等在真实 PhysX 下的结果 ⇒ **PENDING_USER**。
2. **Unity 2019.4 编辑模式是否支持 `SceneManager.CreateScene(..., LocalPhysicsMode.Physics3D)`** ⇒
   **未从文档断言、也未实测**；代码改为运行时判定：失败或 `GetPhysicsScene()` 等于默认世界即报告「仅 Play 模式执行」并提前返回。
   编辑模式下若确实支持，关闭临时未命名场景时 Unity **可能**弹出保存询问（在该分支的报告尾注已说明，选择“不保存”即可；那不是用户场景）。
   若编辑模式下不支持，本验证**不会**在编辑模式产生任何 PhysX 结论（不做假隔离）。
3. **真实 DS 二进制（`HyldDS/HyldDS.exe`）与真实双客户端联调**：本次未重构建 DS、未起 Lobby/客户端；
   「隔离世界生效 / 断线两侧冻结 / 无饱和失败」在真实进程里的表现 ⇒ **PENDING_USER**（需用户按 §5 构建后在 Unity 内验证）。
4. **贴地/重叠的 PhysX 几何语义**（`contactOffset` 边界、`hit.distance≈0` 分支选择）沿用 physics review 的 §3 结论，本次未新增证据。
5. **`PMR3TestScene.Build` 可见路径（客户端 `CreatePrimitive`）的 Collider 注册时机**：几何由显式
   `BoxCollider.center/size`/`transform` 决定，配合宿主每帧一次 `Physics.SyncTransforms()`；真实首帧表现未实测。
6. **`ReferenceEquals` 白名单比较**（physics review §2.5）与 **§2.6 两处弱断言**：本次**有意未改**（不在委派范围）。
7. 客户端 `allowCount != 2` 的显式校验（DS 侧有）本次未补（`TryCollectColliders` 对 <2 已返回 false）——**有意保持不动**，避免扩大 diff。

## 9. 与 `_r4b_host_final_review` 缺陷清单的对应

| 复审项 | 本次状态 |
|---|---|
| D1 Enter 场景构建失败分支漏还原 Warn / 不释放 Bridge | **已修**（改走 `ReleaseSession`） |
| D2 角色/uid 不一致降级后以误导信息整局失败（+反向静默失去控制） | **已修**（显式 `Fail`，不降级） |
| D3 碰撞隔离只靠后置白名单、饱和在过滤前抛异常（中危） | **已修根因**：查询绑定本地物理场景（隔离世界），宿主进程内不再有旧地图 Collider 参与；饱和门保留不放松。真实地图对照证据 ⇒ 菜单 PENDING_USER |
| D4 AP 每子步 `GetSnapshot()` 深克隆只为取帧号 | **已修**（`PendingServerFrame`） |
| D5 端点 `_disposed` 后既不失败也不 Pump | **已修**（`IsDisposed` → `Fail` + Freeze） |
| P3 多 AP 共享 `session.Input` | **已修**（第二个 AP 直接拒绝） |
| P4 每渲染帧 `PollHardware` | **保留**（并保留“接受后才 Consume 边沿”） |
| 假成功路径（探针 Echo 掩盖运动链未接上） | **已修**（`EnsureMovementRig` 失败 → 整局失败） |

## 10. 边界与安全

- 只修改委派列出的 6 个文件（5 个源/桩 + 本报告）；未改父/相邻文件，未创建 meta，未提 scm。
- 未执行 git/svn 写入、未提交、未回滚；未运行 Unity（不抢锁、不启动第二实例）；未编译 UE；未递归委派。
- 未运行 `svn update`；未触碰 `Content/` 资产与 DataTable。
