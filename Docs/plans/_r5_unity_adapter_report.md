# R5-C 前置独立 Unity 适配报告（PMUnityProjectileMotion / PMUnityProjectilePresentation）

> 任务类型：**comprehensive**（实现 R5-C1/C2 两个独立 Unity 适配 + 门禁 + 受控语义回归 + 报告）
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r5-network-contract.md`（全文，末段「B2b/C适配可并行边界」C1/C2 为准）
> → `Docs/plans/net-r5-projectile-contract.md`（全文）
> → `Docs/plans/_r5_coordinator_review.md`（host hook 段：§1.3 fail closed / §3 API 变更 / §5 R7·R8）
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
>
> 只读读取的生产代码（**未修改**）：`Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`（全文）、
> `PMUnityBattleMap.cs`（全文）、`PMUnityMoverPresentation.cs`（全文）、
> `PMProjectile/PMProjectileCoordinator.cs`（文件头 + `IPMProjectileHostMotion` 接口 + `AdvanceMotion` 调用序）、
> `PMProjectile/PMProjectileContracts.cs`（全文）、`PMMover/PMMoverState.cs` 的 `PMVector3` 段、
> `Tools/PMR4CollisionAdapterTest/*`、`Tools/PMR4UnityCheck/*`、`Tools/PMProjectileIntegrationCheck.csproj`
> （作为"已有 CollisionAdapterTest 模式"与门禁口径的参照）。
>
> **未改 hosts（PMDsSessionHost / PMClientSessionHost 一行未动）、未依赖并行 PMR5 driver、
> 未改 coordinator/合同/主计划；未启动 Unity、未点菜单、未改场景；未提交（git/svn 均无写操作）；
> 未递归委派。** World 单位：**Y-up 米**，Yaw 度。

---

## 0. 交付物与验证命令（本机实测，全部 exit 0）

| 文件 | 性质 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/PMUnity/PMUnityProjectileMotion.cs` | C1 适配器（新增） | UTF-8 **BOM + CRLF** |
| `Client/Assets/Scripts/PMUnity/PMUnityProjectileMotion.cs.meta` | 新 meta（唯一 GUID `aa72d21e16234833a5f35d9be463b4e7`） | **无 BOM + LF** |
| `Client/Assets/Scripts/PMUnity/PMUnityProjectilePresentation.cs` | C2 表现层（新增） | UTF-8 **BOM + CRLF** |
| `Client/Assets/Scripts/PMUnity/PMUnityProjectilePresentation.cs.meta` | 新 meta（唯一 GUID `7d0077b3335543a5a0da5c32a8ce4c7f`） | **无 BOM + LF** |
| `Tools/PMR5UnityCheck/PMR5UnityCheck.csproj` | 真实 Unity 2019.4 DLL 编译门（库工程） | LF（与 `PMR4UnityCheck.csproj` 同口径） |
| `Tools/PMR5UnityAdapterTest/PMR5UnityAdapterTest.csproj` | 受控回归工程 | LF（与 `PMR4CollisionAdapterTest.csproj` 同口径） |
| `Tools/PMR5UnityAdapterTest/Program.cs` | 63 项断言用例 | UTF-8 **BOM + CRLF** |
| `Tools/PMR5UnityAdapterTest/UnityStubs.cs` | 受控 UnityEngine 替身（**非** PhysX） | UTF-8 **BOM + CRLF** |
| `Docs/plans/_r5_unity_adapter_report.md` | 本报告 | UTF-8 **BOM + CRLF** |

```bat
REM ① 真实 Unity API 编译门（netstandard2.0 + C#7.3 + D:/Unity/2019.4.8f1 真实 DLL，不 stub Unity）
dotnet build Tools/PMR5UnityCheck/PMR5UnityCheck.csproj -c Release          REM 0 警告 / 0 错误

REM ② 受控语义回归（net8.0 真跑，编真实适配器源码）
dotnet build Tools/PMR5UnityAdapterTest/PMR5UnityAdapterTest.csproj -c Release
dotnet Tools/PMR5UnityAdapterTest/bin/Release/net8.0/PMR5UnityAdapterTest.dll REM 63 通过 / 0 失败，exit 0

REM ③ 结构门（无编译器时的兜底，沿用既有工具）
python Tools/check_cs_braces.py <四个 .cs>                                  REM 全部 BALANCED，PASS
```

| 命令 | 结果 |
|---|---|
| `PMR5UnityCheck`（真实 Unity 2019.4 DLL，netstandard2.0 + C#7.3） | **0 警告 / 0 错误** |
| `PMR5UnityAdapterTest`（net8.0 运行） | **63 通过 / 0 失败**，退出码 0 |
| `check_cs_braces.py`（Motion / Presentation / Program / UnityStubs） | 4/4 BALANCED，深度末尾归零，PASS |
| 编码自检 | `.cs`/`.md` = BOM + CRLF（无裸 LF）；`.meta`/`.csproj` = 无 BOM + LF（无 CRLF） |

---

## 1. C1 `PMUnityProjectileMotion`：精确 API 面

命名空间 `PMNet.Unity`，`public sealed class PMUnityProjectileMotion : IPMProjectileHostMotion, IDisposable`。

### 1.1 构造与常量（冻结）

```csharp
public PMUnityProjectileMotion(PhysicsScene physicsScene, Collider[] allowedColliders, int layerMask)

public const int   HitBufferCapacity     = 32;    // 扫掠命中缓冲；满即 fail closed
public const int   OverlapBufferCapacity = 32;    // 起点重叠缓冲；满即 fail closed
public const int   MaxStopMarkRecords    = 256;   // 停止标记表上限（有界淘汰）
public const float ContactSkinMeters     = 1e-3f; // 命中后沿运动方向回退，避免嵌入
```

只读视图：`Scene` / `LayerMask` / `AllowlistCount` / `MainThreadId` / `StopMarkCount` / `Disposed`，
以及 `PMUnityProjectileMotionStats GetStats()`（结构体：`Steps`、`BlockedSteps`、`StartOverlapBlocks`、
`SaturationFailures`、`RejectedInputs`、`QueryFailures`、`ConsumedStopMarks`、`EvictedStopMarks`、
`ForfeitedStopMarks`、`DisposedCalls`、`LiveStopMarks`）。计数只增不减，仅用于证明"哪条分支真的跑到过"。

### 1.2 接口实现（逐字段对应 coordinator 的调用点）

```csharp
bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot, int deltaMs,
             PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
             out PMVector3 position, out PMVector3 velocity, out float yaw);

bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
             double wallNowMs, out PMVector3 stopPosition);

void Clear();      // 只清"本步停止标记"，不动计数器
void Dispose();    // 幂等；之后 TryStep/TryStop 恒 false
```

**三条出口**（与 `_r5_coordinator_review.md` §1.3 的 fail-closed 契约一一对应）：

| 出口 | 条件 | 返回 | 写回 |
|---|---|---|---|
| 无阻挡 | 扫掠无候选（白名单内、非 trigger、距离可信） | `true` | **原样回填**入参直线 position/velocity/yaw |
| 受阻 | 起点正重叠 **或** 沿线存在接触 | `true` | 接触点（沿向回退 `ContactSkinMeters`）+ **零 velocity** + 原飞行 yaw；并按 key 记一条停止标记 |
| 失败 | 入参非法 / 缓冲饱和 / 查询异常 / 已 Dispose / 非主线程 | `false`（跨线程为抛异常） | 停在 `snapshot.Position`（安全回填） |

`TryStop`：命中本 key 的标记 ⇒ **读后即删**并返回 `true` + 该位置；无标记 ⇒ `false`（正常语义，不是失败）。
`spec` / `wallNowMs` 不参与判定：本类**不读 Unity 全局时钟**（无 `Time.*`）。

### 1.3 逐条契约落点

| C1 契约原文（net-r5-network-contract.md 末段） | 落点 |
|---|---|
| 构造 `(PhysicsScene, Collider[], int)`；必须独立有效 PhysicsScene | 唯一公开构造；`!IsValid()` ⇒ `ArgumentException`；`Equals(Physics.defaultPhysicsScene)` ⇒ `ArgumentException` |
| 主线程 | 构造记 `ManagedThreadId`；查询期不等即抛 `InvalidOperationException`（coordinator 会转成 `HostMotionFaulted` 就地停止） |
| SphereCast/Overlap 在指定 physicsScene | 全部走 `_scene.SphereCast` / `_scene.OverlapSphere`；文件内**不出现** `Physics.SphereCast` / `Physics.OverlapSphere` / `Physics.SyncTransforms` / `autoSimulation` / `queriesHitTriggers`（用例 B4 断言 `Physics.StaticQueryCalls == 0`） |
| 忽略 trigger 与非白名单 | 查询参数恒 `QueryTriggerInteraction.Ignore` + 逐候选 `isTrigger` 再过滤 + 白名单**引用相等**过滤（两层都在） |
| 饱和明确失败不是无障碍 | `count >= capacity` ⇒ `TryStep=false` + `SaturationFailures++`（用例 G1/G2） |
| 半径取可信 spec | 只用 `spec.RadiusM`；`0/负/NaN/Inf` ⇒ `false` |
| 受阻返回 true + stop 位置 + 零 velocity | 见 §1.2 出口表 |
| TryStop 从该步命中记录返回 true | 每 `PMProjectileKey` 一条标记；`TryStop` 消费（用例 C3/C4/D2/D3） |
| 出错返回 false 让 core fail closed | 非 finite 位置/速度/朝向、null spec/state、饱和、查询异常一律 `false`；整合器落 `HostMotionFaulted`（该路径由 `PMProjectileIntegrationTest` P 段独立覆盖） |
| 不做角色伤害 | 文件内无任何 hit target / 结算 / 伤害类型 |
| 不反向引用网络 driver | 只 `using PMNet.Mover` + `PMNet.Projectile` + `UnityEngine*`；无 driver/World/Bridge 引用 |
| 同帧多弹、按 Key 记账、有界清理 | `Dictionary<PMProjectileKey, StopMark>`，无共享 mutable lastHit；超 256 按"插入序号最旧"淘汰并计数（用例 D/J） |
| 场景白名单引用有效且同 scene | 逐项校验：非 null、`gameObject` 可取、`scene.IsValid()`、`scene.GetPhysicsScene().Equals(physicsScene)`；否则 `ArgumentException`（用例 I3/I4/I5） |

### 1.4 判定算法（几何语义，本文件最关键处）

1. **零位移**（`|Δ| ≤ PMVector3.Epsilon`）：不查询、不记标记，原样回填直线（零位移不是错误输入）。
2. **起点正重叠**：`_scene.OverlapSphere(start, radius, …, Ignore)` + 白名单 + 非 trigger；
   命中 ⇒ **不推进**（`position = start`）+ 零速度 + 记标记。饱和 ⇒ `false`。
   为什么权威：PhysX 的 overlap 语义是确定的 "touching **or** inside"，它的候选集**包含**所有真正
   重叠的 Collider；而 `SphereCast` 对"起点已接触/重叠"的返回语义属"已观测"而非"可依赖契约"。
   **短路**：起点重叠时不再扫掠（用例 E2 断言 `SphereCastCalls` 不增长）。
3. **沿线扫掠**：`_scene.SphereCast(start, radius, dir, buffer, |Δ|, layerMask, Ignore)`；
   逐个候选过滤后**只按距离取最小值**（`earliest`），**不看原始法线**。
   - 为什么不用法线筛选：真实 PhysX 对"起点接触/重叠"返回的命中 `normal ≡ -dir`，与真实接触面无关
     （R4-B 真实 Play 实测，见 `Docs/plans/_r4b_physx_zero_hit_fix.md`）。投射物对接触只有一个合法反应
     —— **停止**，因此按距离取最早即可。
   - 为什么"零 TOI 不会跳过真正墙"：`distance == 0` 就是**最早**，取 min 必然命中它；既不忽略它
     （忽略就会漏墙），也不会因法线是 `-dir` 而误判成"无障碍"。
4. **受阻落点**：`travel = max(0, earliest − ContactSkinMeters)`，`contact = start + Δ·(travel/|Δ|)`。
   **不做尺寸收缩**（收缩会让弹在窄缝里挤过去 = 漏墙）；避免嵌入靠"沿向回退 1 mm"，只可能更早停。

### 1.5 几何近似与机器精度边界（诚实登记）

- 本适配器**不做任何 bounds/MTD 推演**：所有几何结论都来自 PhysX 自己的查询，因此
  **不引入第三套几何引擎**，也**不**承诺对旋转形状/网格的"最小推出量"。
- 唯一近似是"接触点回退 skin"：`travel` 只会 ≤ 真实接触距离（保守早停）。`1e-3 m` 的选取理由：
  远大于 float 往返噪声（贴地时实测 gap ≈ −3e-8），又远小于 PhysX 默认 `contactOffset = 0.01`
  与模型 `CollisionSkinMeters = 0.001`，因此不会把"正好接触"变成"穿透"。该值属**待实测收敛项**。
- 饱和上限 32 与 `PMUnityBattleMap.MaxColliderCount = 4096` 之间**存在量级差**（同样的登记见
  `PMUnityBattleMap` 的注释）：正式地图若把大量 Collider 堆在同一 layer 的同一查询体附近，
  查询会以 `false`（fail closed）失败而不是静默漏墙。这是**有意**的行为，但需要在真机用
  `GetStats().SaturationFailures` 量化后决定是否收紧 layerMask（PENDING_USER）。
- 一律不声称与 UE PhysX 逐位等价。

---

## 2. C2 `PMUnityProjectilePresentation`：精确 API 面

命名空间 `PMNet.Unity`，`public sealed class PMUnityProjectilePresentation : IDisposable`（**非 MonoBehaviour**）。

```csharp
public PMUnityProjectilePresentation(string label);                    // label 进 GameObject 名字与诊断
public PMUnityProjectilePresentation(string label, Transform attachRoot); // 可选挂点（应为单位缩放）
public static PMUnityProjectilePresentation Create(string label);      // 工厂
public static PMUnityProjectilePresentation Create(string label, Transform attachRoot);

public const float  PrimitiveSphereRadius = 0.5f;                       // 内建 Sphere 的未缩放半径
public const string PlaceholderKind = "diagnostic-sphere-not-hero-art";  // 名字里显式声明"不是最终英雄外观"
public static readonly Color PlaceholderColor = new Color(0.35f, 0.85f, 1f, 1f);

public void Apply(PMProjectileState state, PMProjectileSpec spec);
public void Dispose();                                                  // 幂等

public string Label { get; }      public bool Disposed { get; }
public GameObject Root { get; }   public Transform Body { get; }
public Material Material { get; } // 构造期克隆的唯一实例；未克隆成功为 null
public int ApplyCount { get; }    public bool Hidden { get; }
public PMVector3 LastPosition { get; }  public float LastYawDegrees { get; }  public float LastRadiusM { get; }
```

自建层级：根 `PMUnityProjectilePresentation[<label>|diagnostic-sphere-not-hero-art]`
（可选挂到 `attachRoot`）+ 子节点 `Body`（内建 Sphere primitive，其自带 Collider **先 disabled 再 destroyed**，
若存在 Rigidbody 同样先 `isKinematic = true` 再 destroyed）。

### 2.1 `Apply` 的**全部**写入面（4 项，此外什么都不做）

| 写入 | 来源 | 说明 |
|---|---|---|
| `_root.transform.position` | `state.Position` | **位置唯一来源**；不读 `SpawnPosition`/`PreviousPosition`/`Velocity` 去猜（用例 K5） |
| `_root.transform.rotation` | `Quaternion.Euler(0, state.Yaw, 0)` | 绕 Y（用例 K6） |
| `_body.localScale` | `spec.RadiusM / 0.5f`（三轴同值） | 球世界半径 = `spec.RadiusM`（要求挂点单位缩放，已在 ctor 注释声明） |
| `_root.SetActive(!hidden)` | `hidden = state.Hidden \|\| (state.Stopped && spec.HideOnStop)` | 仅状态真变化时才写引擎；`SetActive` 使整棵自建子树不可见（无 renderer/材质副作用） |

不查询物理、不推进时间、不读全局时钟、不发声、不建/删对象、不注册事件。**`Stopped` 无任何一次性副作用**：
本类没有音效/事件出口，"二次音效"在本实现里不可表达；重复 `Apply`（含已停止状态）逐字段幂等
（用例 K8 断言对象数/组件数/位置/可见性不变）。

### 2.2 DS 禁止创建

`PMNetRuntime.IsDedicatedServer` 为真时，构造函数与工厂**直接抛 `InvalidOperationException`**
（显式失败，而不是"悄悄不建"），并且**一个 GameObject 都不建**（用例 K1）。
**不**用 `Application.isEditor` / 场景名等间接条件代替它；`Application.isPlaying` 只用于选
`Destroy` / `DestroyImmediate`（生命周期 API，不用于身份判定）。

### 2.3 材质与资源边界

- 构造期**只克隆一次**：`new Material(primitive 自带内建共享材质)` + 设 `PlaceholderColor`
  + 赋给自己的 `renderer.sharedMaterial`；`Apply` 从不碰材质（用例 K9 断言创建数 == 1 且
  `renderer.sharedMaterial` 与该实例同一引用）。
- **只读**源材质与任何资产：从不写 `source`，**不引用/不实例化**任何旧子弹 Prefab、旧脚本、英雄外观配置。
- `Dispose` 清理由本类创建的对象与材质（先根节点后材质），**不触碰**摄像机（`Camera.main`）、
  已有角色、地图、UI（用例 K10/K11/K12：用户相机未被销毁，`AliveCount` 回到创建前）。
- 名字与常量显式声明**当前只是诊断占位**：不称"全部英雄子弹外观已移植"。

### 2.4 fail loud

`Apply(null, …)` / `Apply(…, null)` ⇒ `ArgumentNullException`；`Position` 非 finite、`Yaw` 非 finite、
`RadiusM` 非有限正值 ⇒ `ArgumentException`；释放后 `Apply` ⇒ `ObjectDisposedException`。
坏值**绝不**悄悄写进 Transform（用例 L1/L3：失败后合法输入仍正常工作）。

---

## 3. 测试覆盖（`PMR5UnityAdapterTest`，63 断言，全通过）

| 段 | 断言 | 内容 |
|---|---|---|
| A（4） | 4 | 受控替身自检：贴地扫掠 `distance=0 & normal=-dir`（复现已观测语义）、悬空 0.3 得到正 TOI、`OverlapSphere` 报出"正好接触"、世界同时含地板与墙 |
| B（5） | 5 | 无阻挡：`true` + 原样回填直线；`TryStop=false`；查询透传（mask / `Ignore` / `maxDistance=|Δ|`）；`Physics.StaticQueryCalls==0`；统计 `Steps=1/Blocked=0/Live=0` |
| C（5） | 5 | 沿线**最早**阻挡（近墙 4.4 而非远墙 8.4，也不是返回顺序第一条）；接触点 = `4.399`（回退 skin）；零 velocity；`TryStop` 消费标记且逐字段相同；二次 `TryStop=false` |
| D（4） | 4 | 多弹交错：三颗弹（撞 X 墙 / 撞 Z 墙 / 高处无阻挡）各自正确；未消费前 `Live=2`；交错取标记**不串**（Z 得 Z、X 得 X）；消费后 `Live=0/Consumed=2` |
| E（5） | 5 | 起点正重叠：嵌墙停在起点、**短路**不掉扫掠（`SphereCastCalls` 不增）、产生一次性标记、半嵌地板同样停、`StartOverlapBlocks=2` |
| F（5） | 5 | **零 TOI 不被当成无障碍**：贴地 + 前方有墙 ⇒ 停在起点（不飞到墙的 4.399）；对照（地板不在白名单）⇒ 飞到 4.399；贴地无墙 ⇒ 仍受阻；替身开关关掉 overlap 的"接触"报告后**只有扫掠**报零距离 ⇒ 仍停在起点且非失败路径；全程 `Saturation=0` |
| G（2） | 2 | 饱和：扫掠 40 候选（容量 32）⇒ `false`；起点重叠 40（容量 32）⇒ `false`（且不再扫掠） |
| H（3） | 3 | 白名单内的 trigger 不阻挡；场景内非白名单实体不阻挡；白名单里的墙照常阻挡（对照组） |
| I（11） | 11 | 拒绝默认物理世界 / 无效 PhysicsScene / null+空 白名单 / null 项 / **跨场景**白名单；同场景构造成功；拒绝 NaN·负·零半径、非 finite 直线位置·速度·朝向·快照位置、null spec·snapshot；被拒调用**不留标记**；**跨线程**查询显式抛 `InvalidOperationException` |
| J（4） | 4 | 标记表有界（300 颗弹 ⇒ `Live=256/Evicted=44`）；淘汰的是最旧的，最新仍可消费；`Clear()` 清空后同 key `TryStop=false`；`Dispose` 幂等且之后 `TryStep/TryStop` 恒 `false` |
| K（12） | 12 | DS 模式两入口都抛且零对象；切回客户端；根名带 `diagnostic-sphere-not-hero-art` + `Body` 层级；占位球无 Collider/Rigidbody/旧脚本但有 Renderer；位置唯一来源；yaw/尺寸；`Hidden` 与 `Stopped+HideOnStop`；停止态重复 Apply 无副作用；材质只克隆一次；不碰摄像机；`Dispose` 清理（物体+材质）+ 释放后 Apply 抛 + 重复 Dispose 无害；无残留对象 |
| L（3） | 3 | `Apply` 参数校验（null / NaN 位置 / NaN 朝向 / 零·负半径）；失败调用不建对象、不污染 Transform；失败后合法输入仍正常 |

测试**全部使用真实现**（真 `PMUnityProjectileMotion` / `PMUnityProjectilePresentation` / 真 `PMNetRuntime`），
只通过 `IPMProjectileHostMotion` 接口驱动适配器；期望值手算（`4.5 − 0.1 = 4.4`，减 skin = `4.399`）。

---

## 4. 受控替身（`UnityStubs.cs`）的语义边界 —— 必须写清

| 项 | 说明 |
|---|---|
| **不是** PhysX、**不是** Unity | 是"球 vs 轴对齐 AABB"的解析模型；只复现 R4-B 真实 Play **已观测**的语义 |
| 复现的已观测语义 | 起点已接触/重叠 ⇒ 扫掠返回 `distance == 0` 且 `normal == -dir`；批量查询返回 `min(命中数, 缓冲)`（饱和断言依赖它） |
| 扫掠几何 | 射线 vs **按半径外扩的 AABB** 的 slab 测试。面接触**精确**；棱/角处外扩盒是球体的**保守超集**（只会早报、不会漏报）。**有意不实现**旋转形状/网格/MTD |
| 有意简化 1 | `Quaternion` 只保存欧拉角（不做四元数乘法）；用例只比较 `Euler` 往返值 |
| 有意简化 2 | `Transform` 只做"父级位置平移"的父子合成（无旋转/缩放合成）；表现层只写自己根节点、只读子节点 `localScale`，不影响断言 |
| 有意简化 3 | 被销毁对象仍是非 null 托管引用（不实现 Unity 的"已销毁 == null"语义）；适配器与表现层都不依赖该语义 |
| 测试专用开关 | `PhysicsScene.ReportTouchingInOverlap`（默认 true）：置 false 时 `OverlapSphere` 只报真穿透（`gap < 0`），用来让适配器走到"**只有扫掠**报 `distance == 0`"这条路径。**它不改变任何真实引擎语义**，只用于覆盖两条路径 |
| `Camera.main` | 替身里是**显式设置的静态引用**（真实 Unity 是"找第一个 MainCamera 标签的启用相机"），唯一目的是让用例断言表现层没碰过它 |
| `Material.CreatedTotal` | 内建默认材质**不计入**，否则无法区分"引擎内建"与"代码里 new 的" |

因此本工程钉住的是**适配器的判定逻辑**，不是引擎的几何结论。

---

## 5. PENDING_USER（必须真机/后续批次处理，本批未做）

| # | 项 | 说明 |
|---|---|---|
| P1 | **真实 PhysX 行为未在真机验证** | 本批按用户要求**不启动 Unity、不点菜单**。真实 `PhysicsScene.SphereCast` / `OverlapSphere` 在 runtime-local-physics-scene 上的实际返回（含初始重叠是否报 `distance=0`）需要一次 Unity Play 跑（可复用 `Client/Assets/Editor/PMR4UnityValidation.cs` 的菜单模式）。本适配器**不依赖**"零距离命中会报"，它另有 `OverlapSphere` 权威判定，但真机确认仍待做 |
| P2 | `ContactSkinMeters = 1e-3` / 接触容差的收敛 | 属"待实测收敛项"；真机需要用一次贴墙飞行量出"是否残留嵌入/是否过早停" |
| P3 | 饱和上限与正式地图 Collider 密度 | `HitBufferCapacity = 32` 与 `PMUnityBattleMap.MaxColliderCount = 4096` 存在量级差；真机需读 `GetStats().SaturationFailures` 决定是否收紧 layerMask |
| P4 | 白名单同场景校验的运行时前提 | `Scene.GetPhysicsScene()` 对 `SceneManager.CreateScene(LocalPhysicsMode.Physics3D)` 建出的场景应与 `PMUnityBattleMap.PhysicsScene` 相等（同一 API，逻辑上一致），仍需真机确认一次 |
| P5 | 表现层的材质克隆在真机客户端构建上 | `new Material(primitive 内建材质)` 依赖内建材质未被剥离；真机客户端构建需确认（取不到时本类**不创建**材质，行为仍正确，只是外观用 primitive 默认材质） |
| P6 | **C 宿主集成** | 本批**只交付可被宿主消费的 API**：未改 `PMDsSessionHost` / `PMClientSessionHost`，未把 coordinator 或 driver 接到 Unity 宿主；"把这两颗适配器真正插进战斗链路"属后续 C 宿主批次 |
| P7 | 整合器侧 fail-closed 的联动 | 本工程只证明适配器**会返回 false**；`TryStep=false` ⇒ `HostMotionFaulted` ⇒ 就地停止的整合器链路由 `Tools/PMProjectileIntegrationTest` P 段（25 项）覆盖，本批**未重跑**（避免在别人的工程目录产生构建产物），仅引用其结论 |
| P8 | 旋转/网格几何 | 全部交给 PhysX；本适配器不做 bounds 近似（因此也不承担"通用形状精确 MTD"的承诺）。真机在含 MeshCollider 的正式地图上仍需量一次 |
| P9 | 实机 T45 | 仍未完成；本批不改变该状态 |

---

## 6. 未做 / 不做（诚实口径）

- 未改 `PMDsSessionHost` / `PMClientSessionHost` / `PMR5ProjectileDriver` / coordinator / 合同 / 主计划。
- 未实现弹跳、滑行、穿透厚度、拖尾、音效、命中特效、插值平滑 —— 首批契约只有"接触即停"。
- 未做骨骼精细校验、未做角色伤害：属整合器 + R6。
- **不声称** R5-C 阶段完成、不声称 R5-B2 完成、不声称英雄子弹外观已迁移：本批只交付 C1/C2 两个**独立适配**
  ＋编译门＋受控回归。
- 未提交（git/svn 无写操作）；未递归委派。

---

## 7. 已检查范围

**完整读取的文档**：`D:/UGit/hyld-master/AGENTS.md`、`Client/Assets/AGENTS.md`、
`Docs/plans/net-r5-network-contract.md`（全文）、`Docs/plans/net-r5-projectile-contract.md`（全文）、
`Docs/plans/_r5_coordinator_review.md`（全文，重点 §1.3 host hook fail closed / §3 API 变更 / §5 R7·R8）、
`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**文档如何决定入口**：`net-r5-network-contract.md` 末段的 C1/C2 给出**唯一**的构造签名、判定要求与
表现层边界，因此本实现直接从它落 API；`net-r5-projectile-contract.md` 给出单位口径（`PMNet.Mover.PMVector3`、
Y-up 米、Yaw 度）与"运动 hook 语义"；`_r5_coordinator_review.md` §1.3 给出 `TryStep`/`TryStop` 的
**失败形态表**（false / 异常 / 非 finite ⇒ fail closed；`TryStop` false = 正常），本实现逐条对应；
`Client/Assets/AGENTS.md` §1.1 明确"进程形态唯一来源是 `PMNetRuntime.IsDedicatedServer`"，
因此 C2 的 DS 判定走它而不是编辑器判定。

**只读读取的生产代码**：`PMUnityMoverCollisionQuery.cs`（全文，作为查询隔离/饱和/主线程/白名单的既有纪律参照）、
`PMUnityBattleMap.cs`（全文，作为物理场景与白名单的**上游**、其 `Colliders`/`PhysicsScene` 是本适配器的天然入参）、
`PMUnityMoverPresentation.cs`（全文，作为"DS 禁止创建 + primitive 去物理 + 单一自建根 + 材质一次"的既有模式参照）、
`PMProjectileCoordinator.cs`（文件头 + `IPMProjectileHostMotion` 接口文档 + `AdvanceMotion` 的 TryStep/TryStop 调用序
与 `FailClosedHostMotion`/`FailClosedHostStop` 落点）、`PMProjectileContracts.cs`（全文）、
`PMMover/PMMoverState.cs` 的 `PMVector3` 段、`Tools/PMR4CollisionAdapterTest/{Program.cs,csproj}`、
`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`、`Tools/PMProjectileIntegrationCheck.csproj`、
`Client/Assets/Scripts/PMNet/{PMNetRuntime,PMNetRole,PMNetLaunchOptions,PMNetIdentity}.cs`（依赖面所需段）。

**写入的文件（仅此 9 个，未创建/修改列表外任何文件）**：见 §0 表格。

**构建产物**：仅 `Tools/PMR5UnityCheck/{bin,obj}` 与 `Tools/PMR5UnityAdapterTest/{bin,obj}`
（两个新工程自身的编译输出）；**未**触发任何既有工程的构建。
