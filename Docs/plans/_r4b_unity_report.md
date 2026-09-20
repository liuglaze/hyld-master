# R4-B / B2：Unity 碰撞 / 输入 / 表现 实施报告

> 契约：`Docs/plans/net-r4-network-contract.md` 的 **B2**（实现契约）
> 状态源：`Docs/plans/net-architecture-migration.md`（本报告只登记 B2 的实现证据，不改主计划）
> 结论口径：**真实 API 可编译 = 已验证**；**真实 PhysX 运行 = PENDING_USER**（需用户点菜单，本 Agent 未抢工程锁）

---

## 0. 摘要（≤1500 字）

按要求交付 R4-B / B2 的 Unity 侧三件套与两道门禁，**全部落在允许写入清单内，未越界**。

**核心结论**：(1) 三个适配器源文件 + 一个 Editor 验证菜单已落地；(2) `Tools/PMR4UnityCheck` 引用 `D:/Unity/2019.4.8f1` 下的**真实 UnityEngine/UnityEditor DLL**，以 netstandard2.0 + C#7.3 编译**0 错误**，产物含 4 个新类型——**接口签名真实可用**已证；(3) `Tools/PMR4UnityAdapterTest` 在 net8 真跑输入纯转换与跳跃边沿，**64/64 通过**；(4) 8 项缺陷注入**全部被抓住**（含 1 项被真实 DLL 编译门禁抓出的假 API）。

**实现期发现并修掉一个真实缺陷**：`PollHardware` 原本每次调用都读 `GetKeyDown`，而宿主按子步驱动时同一帧会被调用多次 ⇒ 第一个子步消费掉边沿后，第二个子步会**重新锁上同一个边沿**，表现为"一次按键跳两次"。已改为「一次按下只锁一次」（`_spacePressObserved` + 松开复位），并补 G9b/G9c/G9d 三条用例钉住。该缺陷是**负向验证 N2 先暴露了测试盲区**（注入后 0 失败 ⇒ 说明该路径没被测到）才浮出来的——正是项目纪律「注入后失败数为 0 = 这条路径没被测到」的又一次验证。

**两处关键 API 事实**（决定实现走向，均已用 Cecil 反射真实程序集核实）：
- `PhysicsScene.IsValid()` 是**方法**不是属性；`Scene.GetPhysicsScene()` 是 `UnityEngine.PhysicsSceneExtensions` 的**扩展方法**（不在 CoreModule）。按"想当然"写会直接编译失败。
- 胶囊**正好**站在地面上时，水平扫掠会被 PhysX 报出"与地面接触"（法线朝上、与运动方向垂直）；若算作阻挡则 `Fraction≈0`、角色**永远走不动**。因此适配器只接受"法线与运动方向相对"的命中（阈值 1e-3，风险不对称：误判阻挡=走不动的严重故障，忽略掠射面=亚毫米自愈）。

**真实运行证据的边界（诚实口径）**：真实 PhysX 行为（地板/墙/贴地平移/跳跃落地/起点重叠/饱和显式失败）只能在 Unity 内跑，入口为菜单 `Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）`——临时 `LocalPhysicsMode.Physics3D` 场景隔离、不碰用户场景、`finally` 清理，**未自动执行**（本 Agent 未抢工程锁、未点编辑器）。故 P4B3 判定为「API 真实可编 = PASS」；「真实运行 = PENDING_USER」。

**跨组提示**：`Tools/PMClientCheck` 与 `Tools/PMR3RuntimeTest` 当前各 1 个错误，根因是网络组（B1）正在改的 `Client/Assets/Scripts/PMR3/PMR3Player.cs` 引用了 `PMR4MovementDriver`，而这两个既有工程的 include 列表未包含 `PMR4Movement*.cs`。**与本次改动无关**（这两个工程不含 `PMUnity/**` 与 `Assets/Editor/**`），属 B1 / 主 Agent 收口范围；本 Agent 未越界修改。

---

## 1. 任务与写入边界

允许写入清单（全部已写，无额外文件）：

| 文件 | 作用 |
|---|---|
| `Client/Assets/Scripts/PMUnity.meta` | 新目录 meta（GUID `4d89a4cab3fe411aac39020d8aa91a99`） |
| `Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs` (+meta) | `IPMMoverCollisionQuery` 的真实 PhysX 适配器 |
| `Client/Assets/Scripts/PMUnity/PMUnityMoverInput.cs` (+meta) | 本机输入 → `PMMoverInput`，含跳跃边沿缓存 |
| `Client/Assets/Scripts/PMUnity/PMUnityMoverPresentation.cs` (+meta) | 每角色可见胶囊（无碰撞、DS 不建） |
| `Client/Assets/Editor/PMR4UnityValidation.cs` (+meta) | 菜单入口：真实 PhysX 验证（临时隔离场景） |
| `Tools/PMR4UnityCheck/PMR4UnityCheck.csproj` | 真实 Unity DLL 的编译门禁（类库，无 Program.cs） |
| `Tools/PMR4UnityAdapterTest/PMR4UnityAdapterTest.csproj` + `Program.cs` | net8 输入纯转换 / 边沿语义验收（**只有这两个文件**；UnityEngine 替身内联在 `Program.cs` 尾部，只桩 `Input` + `KeyCode`，替身内**无任何物理**） |
| `Docs/plans/_r4b_unity_report.md` | 本报告 |

**未触碰**（逐项确认）：宿主 `PMDsSessionHost.cs` / `PMClientSessionHost.cs`（只读其 `PMR3TestScene` 定义）、`PMR3Player.cs`、网络组 `PMR4MovementCodec.cs` / `PMR4MovementDriver.cs`、PMMover / PMPrediction 预测核心、主计划 `net-architecture-migration.md`、任何既有 Tools 工程/桩件、旧业务代码。也未执行 `svn`/`git` 提交、未编译 UE、未递归委派、未启动第二个 Unity 实例。

**两处边界自查与整改**（写入边界比"能跑通"更优先）：

1. **清理核查**：负向验证脚本曾在 `Client/Assets/Scripts/PMUnity/` 遗留一个 `PMUnityMoverCollisionQuery.cs.negbak`。已定位并删除；删除前核对了它与正式文件 **sha256 完全相同**（`9222cfee…`），即"被注入的版本已正确还原"，非内容损坏。全仓 `*.negbak/*.bak` 复查为 0。
2. **越界文件整改**：最初把 net8 替身写成了独立文件 `Tools/PMR4UnityAdapterTest/UnityStubs.cs`。该文件**不在允许写入清单内**，且契约 B2 明确"新测试工程**最多 Program.cs/csproj**"。已把替身**内联**进 `Program.cs` 尾部、删除该文件、并同步去掉 csproj 里的 `<Compile>` 项；重建后 **64/64 仍全绿**，且重跑了 N2/N7 两项注入确认门禁未被削弱（分别 3 项 / 2 项失败）。整改后 `Tools/PMR4UnityAdapterTest/` 只剩 `Program.cs` + `.csproj`。

**边界证明**（按 mtime 复查全仓）：本次会话窗口内被改动的文件里，属于本 Agent 的**恰好是允许清单的 12 个路径**；其余同窗口改动全部来自并行工作的其他组（网络组 B1 的 `PMR3/PMR4Movement*.cs`、`Generated/*.g.cs`、`pmnet-r3-ids.json`、`Tools/PMR4Network*`；预测/主侧组的 `PMPredictionTimeline.cs`、`Tools/PMPredictionTest`、`Tools/PMMoverPredictionTest`、`Docs/plans/_r4a_*.md`、主计划与两份契约文档）。**本 Agent 未写其中任何一个**（只读过主计划与两份契约）。

---

## 2. 必读文档与实际证据（决定首搜 API 的路径）

按委派指定顺序完整阅读，未跳读：

| 顺序 | 文档 | 对本次落点的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 确认入口文档与"文档冲突时按更具体规则"；确认"Lobby/DS 二分"与禁止事项 |
| 2 | `Client/Assets/AGENTS.md` | 确认运行期身份唯一来源 `PMNetRuntime.IsDedicatedServer`、`frameTime=0.016`、Y 轴语义、旧输入链路（TouchLogic/CommandManger）**不在新链** |
| 3 | `Server/AGENTS.md` | 确认服务端不含 Unity、战斗收发的历史边界；确认 Unity DS 归属 |
| 4 | `Docs/plans/net-architecture-migration.md`（全篇 2088 行，分 4 段） | 取 §3.9.4 运行时契约（每对象 Role、回滚语义）、§5 阶段表（**P4B3 的验收文字就是本次的验收标准**）、§9.5 坑 #25（"没有自测编译手段会把用户变成编译器"→ 直接决定要建真实 DLL 门禁）、坑 #15（中文源必须 UTF-8+BOM） |
| 5 | `Docs/plans/net-r4-prediction-contract.md` | 冻结字段与阈值：`PMMoverHit/Ground` 语义、`PMMoverSyncState` 可回滚字段、位置/速度/朝向阈值 0.05m / 0.01m·s⁻¹ / 1°、Modes 数值 |
| 6 | `Docs/plans/net-r4-network-contract.md` | **B2 逐条要求**（实现契约，本报告的验收依据） |
| 7 | `.pi/agent/skills/windows-shell-compat/SKILL.md` | 全部命令按 Git Bash 语法写；Windows 路径避免反斜杠转义陷阱 |
| 8 | `ProjectMecury/.agents/skills/mover-quick-start/SKILL.md` | 作为**已授权的 C# 移植 NP 纪律**使用：红线 2（Input 只放输入意图、不放派生量）、红线 3/4（不在 SimTick 改外部状态、副作用只放 Finalize）、检查项 1/5（影响模拟的值必须可恢复/可序列化）。**按委派要求未发起问卷、未改 UE** |
| 9 | `…/mover-quick-start/references/network-prediction-checklist.md` | 同上逐项自查；其中"墙钟口径区分"（GenerateMove 禁墙钟／断线兑底必须墙钟）被写进输入的边沿语义注释 |

**由文档决定的首搜入口**（不是盲搜）：
- 接口/字段 → `Client/Assets/Scripts/PMMover/IPMMoverCollisionQuery.cs`、`PMMoverState.cs`、`PMPrediction/PMPredictionContracts.cs`、`PMNet/PMNetIdentity.cs`（`PMTimeStep` 必须整毫秒、范围 1..50）
- 场景几何 → `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` 的 `PMR3TestScene`（只读）：地板中心 `(0,-0.5,0)` 尺寸 `(40,1,40)`、墙中心 `(0,1,0)` 尺寸 `(1,2,8)` ⇒ 地板顶 `y=0`、墙右面 `x=0.5`（**校验里对这两条写了显式断言**，因为编辑器验证重复了这四个常量）
- 身份判定 → `Client/Assets/Scripts/PMNet/PMNetRuntime.cs`（`IsDedicatedServer`）、`PMNetRole.cs`（`PMNetRole` 1..3，禁止数值比较）
- 编译面纪律 → `Tools/PMMoverCoreCheck/PMMoverCoreCheck.csproj`（逐文件 include、netstandard2.0+C#7.3、无 OutputType）
- 既有工具链风格 → `Tools/PMR3RuntimeTest`、`Tools/PMMoverTest`（断言计数 + 退出码 0/1 + 头部"能测/不能测"声明）

---

## 3. 真实 Unity API 核验（先核验再写码）

方法：用 Unity 自带的 `Unity.Cecil.dll` 对 `D:/Unity/2019.4.8f1/Editor/Data/Managed/**` 做真实程序集反射，并与官方 XML 文档交叉核对（探测脚本为仓库外临时文件，未落入 hyld 仓库）。

**确认存在且签名精确**：

```
PhysicsScene.CapsuleCast(Vector3 p1, Vector3 p2, float radius, Vector3 direction,
        RaycastHit[] results, float maxDistance, int layerMask, QueryTriggerInteraction) -> int
PhysicsScene.CapsuleCast(... , out RaycastHit hitInfo, ...) -> bool
PhysicsScene.OverlapCapsule(Vector3 point0, Vector3 point1, float radius,
        Collider[] results, int layerMask, QueryTriggerInteraction) -> int
PhysicsScene.IsValid() -> bool                      // ★ 是方法
Physics.defaultPhysicsScene -> PhysicsScene
PhysicsSceneExtensions.GetPhysicsScene(this Scene) -> PhysicsScene   // ★ 是扩展方法，在 PhysicsModule
Physics.SyncTransforms() -> void
Physics.autoSimulation { get; set; }                // 只读不写
Collider.bounds -> Bounds / Collider.enabled / Collider.isTrigger
BoxCollider.center / size
QueryTriggerInteraction { UseGlobal=0, Ignore=1, Collide=2 }
SceneManager.CreateScene(string, CreateSceneParameters) -> Scene
SceneManager.MoveGameObjectToScene(GameObject, Scene) -> void
SceneManagement.CreateSceneParameters(LocalPhysicsMode) ; LocalPhysicsMode { None=0, Physics2D=1, Physics3D=2 }
Input.GetKey/GetKeyDown(KeyCode) , Input.GetAxisRaw(string)
GameObject.CreatePrimitive(PrimitiveType) ; PrimitiveType.Capsule=1
Application.isPlaying / isBatchMode ; Camera / CameraClearFlags.SolidColor ; Quaternion.Euler/identity
EditorSceneManager.NewPreviewScene() / ClosePreviewScene(Scene) / IsPreviewScene(Scene) / CloseScene(Scene,bool)
EditorUtility.DisplayDialog(string,string,string) ; MenuItem(string)
```

**由核验改变实现的三处**（若按直觉写会编译失败或行为错误）：
1. `IsValid` 写成方法调用（`PhysicsScene.isValid` 会在门禁里报 CS1061——已作为负向验证 N6 实测到）。
2. 取场景物理世界用扩展方法 `scene.GetPhysicsScene()`（依赖 `using UnityEngine;`，且**类型在 PhysicsModule**，所以门禁必须同时引用 `UnityEngine.PhysicsModule.dll`）。
3. `PMMoverSyncState.CreateDefault()` 的初值恰是"足底正好贴地"，直接暴露出"水平扫掠被地面误判为阻挡"的问题 ⇒ 引入"接触必须与运动方向相对"的过滤（见 §5.2）。

---

## 4. 交付物 API 一览（契约要求"报告明确列出"）

### 4.1 `PMUnityMoverCollisionQuery`（namespace `PMNet.Unity`，`sealed`，实现 `IPMMoverCollisionQuery`）

**初始化（三个构造重载）**
```csharp
PMUnityMoverCollisionQuery(int layerMask, int worldVersion);                                  // 默认物理场景，不限制 Collider
PMUnityMoverCollisionQuery(int layerMask, int worldVersion, Collider[] allowlist);            // 默认场景 + 白名单
PMUnityMoverCollisionQuery(PhysicsScene scene, int layerMask, int worldVersion, Collider[] allowlist); // 全参（Editor 隔离场景用）
```
- 构造即校验 `scene.IsValid()`（无效场景显式抛，绝不悄悄退回默认场景）；白名单做**防御性拷贝**并丢弃 null 项；空/全 null 白名单等于"不限制"。
- 构造时记录 `Thread.CurrentThread.ManagedThreadId`，**任何跨线程查询抛 `InvalidOperationException`**。
- 查询期零分配（4 个固定长度缓冲数组在构造时建好；`Sweep` 与 `QueryGround` 缓冲分离）。

**查询**
```csharp
public int WorldVersion { get; }                       // 进 AuxState 参与 reconcile
public PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight);
public PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance);
public PMUnityCollisionQueryStats GetStats();           // 分支执行证据（见 §4.2）
```
- `Sweep` 分支：① 零 delta（`|delta| ≤ 1e-6`）⇒ 不阻挡、`Fraction=1`、法线零；② 批量 `CapsuleCast` 取**最近**且**与运动相对**的白名单命中 ⇒ `Fraction = hit.distance/|delta|` 钳到 [0,1]；③ 未命中但起点已重叠 ⇒ `OverlapCapsule` + "胶囊世界 AABB ∩ Collider.bounds"取**全局最小平移轴**（轴序固定 X→Y→Z、严格小于，保证确定性；方向取背离障碍物中心一侧）⇒ `Blocking=true, Fraction=0, |Normal|=1`。
- `QueryGround` 分支（顺序有意）：① 先 `OverlapCapsule` 做**支撑面重叠测量**——判据「顶面 `bounds.max.y ≤ 胶囊中心 Y`」+「顶面 XZ 与胶囊足迹 `[center ± radius]` 有交集」，取候选里**最高**顶面 `top`，`Distance = footY − top`（负=穿透，0=接触）⇒ 只在 `≤ 0` 时直接返回；② 悬空时用 `CapsuleCast` 向下探 `distance` 取精确间隙；③ 都无 ⇒ 接口约定的 `Found=false, Distance=0, Normal=零`。
- 饱和：`CapusleCast/OverlapCapsule` 返回数 `≥ HitBufferCapacity(32)` ⇒ **抛 `InvalidOperationException`** 并计数（PhysX 批量查询不保证含最近者，静默取"碰巧的"= 漏墙）。
- 显式拒绝：非有限坐标、`radius ≤ 0`、`halfHeight ≤ 0`、`halfHeight < radius`（几何退化）、`distance` 为负/非有限。
- **永不做**：`Transform` 写入、`Physics.Simulate`、`Physics.autoSimulation/autoSyncTransforms`、`Physics.SyncTransforms`（契约要求由宿主每帧单次做——已写成类注释的"宿主前置条件"）、建/删 `GameObject`、读写全局 `queriesHitTriggers`（一律传 `QueryTriggerInteraction.Ignore`）。

### 4.2 `PMUnityCollisionQueryStats`（`struct`）
`SweepQueries / GroundQueries / StartOverlapResolutions / PenetratingGroundHits / SaturationFailures / UnresolvableOverlaps / NonOpposingHitsIgnored` + `ToString()`。用途：让"起点重叠路径""穿透地距路径""饱和路径""被过滤的非对立命中"这些分支**有可观测证据**，而不是只靠"测试通过"。

### 4.3 `PMUnityMoverInput`（输入采集 + 边沿状态机）

**初始化**：`new PMUnityMoverInput()`，可调字段 `DeriveYawFromMoveInput`(默认 true) / `ExternalYawDegrees` / `ExternalVerticalInput` / `AxisDeadZone`(默认 0) / `UseLegacyAxes`(默认 true)。

**输出与消费（契约要求"API 命名/消费方法报告明确"）**
```csharp
public void PollHardware();                       // 每宿主帧（或每子步）调用都安全：同一次按下只锁一个边沿
public PMMoverInput Sample(int stepMs);           // 采样，**不消费**边沿（零 ms 帧因此不丢边沿）
public PMMoverInput SampleAndConsume(int stepMs); // 采样 + 原子消费（最简正确用法）
public void Consume(int stepMs);                  // 仅 1..50 的真实步清缓冲；0/越界不清
public PMMoverInput Peek();                       // == Sample(0)
public void NotifyJumpEdge();                     // UI/网络注入边沿，与硬件同一套语义
public void ClearBufferedJumpEdge(); public void Reset();
```
**纯转换（可用于可控测试，不引旧 CommandManger/TouchLogic）**
```csharp
public static PMMoverInput Convert(float moveX, float moveZ, float moveY, float yawDegrees, bool jumpEdge);
public static float DeriveYawDegrees(float moveX, float moveZ);   // Y-up: atan2(x,z) → 0=+Z, 90=+X，不做镜像
public static float ApplyDeadZone(float value, float deadZone);   // 不缩放补偿
public static bool  IsRealStepMs(int stepMs);                     // 契约 1..50
public static float NormalizeDegrees(float degrees);              // (-180,180]
```
**跳跃边沿状态表（写入文件头，逐条有测例）**

| 事件 | 缓冲 | 本次产出 JumpPressed |
|---|---|---|
| PollHardware **首次**观测到这次按下 | true | — |
| 同一次按下内重复 PollHardware | 不变（幂等） | — |
| NotifyJumpEdge | true | — |
| Sample(n) / Peek() | 不变 | = 缓冲 |
| Consume(n)，n∈[1,50] | false | — |
| Consume(0)/越界 | 不变（**零 ms 不丢边沿**） | — |
| SampleAndConsume(n∈[1,50]) | false（原子） | = 缓冲 |
| Reset() | false | — |

⇒ 由此得到契约点名的两条行为：**零 ms 帧不丢边沿**、**补子步不重复跳跃**。

**建模纪律**：轴取"按键(WASD/方向键)"与"虚拟轴(Horizontal/Vertical)"中**绝对值较大者**（不叠加，避免双倍速度）；虚拟轴未配置时 Unity 会抛异常 ⇒ 捕获一次后**永久关闭该来源并计数**（`AxisAvailable/AxisReadFailures` 公开可观测，不是静默降级），按键来源照常工作；`Effects/Layers/RemovedLayerIds` 一律 `null`——它们是"宿主鉴权后的可信命令"，**不属于输入设备能决定的值**（红线 2），由宿主另行注入。

### 4.4 `PMUnityMoverPresentation`（表现层）

```csharp
PMUnityMoverPresentation(string label, PMNetRole role);
PMUnityMoverPresentation(string label, PMNetRole role, bool createTestCamera);   // 默认 false，绝不自动建相机
public void Apply(PMMoverSyncState state);            // 一次写 position/yaw/尺寸；不含任何仿真
public void ApplyPredicted(PMMoverSyncState state);   // AP：喂预测输出（同一写入面）
public void ApplyInterpolated(PMMoverSyncState state);// SP：喂插值输出（同一写入面）
public Camera CreateTestCamera(float distanceMeters);  // 可选，仅挂在角色根节点下
public void Dispose();                                 // 幂等
```
- **DS 绝不创建**：`PMNetRuntime.IsDedicatedServer` 为真时构造**直接抛**（显式失败，而不是"悄悄不建"让宿主以为表现层在工作）。判定只走 `PMNetRuntime`，符合计划 §3.8 的纪律。
- **无碰撞**：primitive 自带的 `CapsuleCollider` **先 `enabled=false` 再销毁**（只 disable 会依赖后续代码不改回来；只销毁会留下"帧末才真删"的可被查询窗口）。
- `Apply` 校验 `Position/Velocity/Yaw/Scale` 有限且 `Scale > 0`，否则抛；释放后再 `Apply` 抛 `ObjectDisposedException`。
- 尺寸换算：`localScale = (radiusWorld/0.5, halfHeightWorld/1.0, radiusWorld/0.5)`（Unity 内置胶囊 mesh 半径 0.5、半高 1.0），半径/半高取自 `PMMoverDefaults.CapsuleRadiusMeters/CapsuleHalfHeightMeters`。

### 4.5 `PMR4UnityValidation`（Editor 菜单，`#if UNITY_EDITOR`）

- 入口：`[MenuItem("Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）")]`，或 `-executeMethod PMNet.UnityEditor.PMR4UnityValidation.RunValidation`。**不自动执行**（无 `InitializeOnLoad`、无静态构造副作用）。
- 隔离：编辑模式优先 `EditorSceneManager.NewPreviewScene()`（不进 Hierarchy、不需保存、不弄脏用户场景），并校验 `scene.GetPhysicsScene().IsValid()`；不可用时**显式退回** `SceneManager.CreateScene(..., LocalPhysicsMode.Physics3D)` 并在报告里标注所用模式。运行期一律用运行时本地物理场景。**全部测试对象都 `MoveGameObjectToScene` 进临时场景**，`finally` 里逐个 `DestroyImmediate` + 关场景。
- 用例（15 项断言 + info）：`isolatedScene / sceneCollidersReady / floorTopIsZero / wallRightFaceIsHalf / groundTouching / groundFloatingNotFound / groundPenetratingSignedDistance / sweepZeroDeltaUnblocked / sweepBlockedByWall / sweepResolvesStartOverlap / startOverlapPathExercised / allowlistIsolatesOtherColliders / walkStopsAtWall / jumpLandsBackOnFloor / saturationFailsExplicitly / jumpEdgeSurvivesZeroStep / inputPureConversions / presentation*（仅 Play）`。
- 其中 **`allowlistIsolatesOtherColliders` 是隔离性的直接证据**：造一个**不在白名单**的诱饵盒挡在路上，要求白名单查询仍 ≈0.70（无视诱饵）、无白名单查询 ≈0.133（被诱饵挡住）。
- `walkStopsAtWall` / `jumpLandsBackOnFloor` 用**真实 `PMMoverModel` + 真实 PhysX 查询**跑 125 步 ×16ms，断言"从 x=3 能走到 x≈0.9 并停住、永不入墙"与"起跳（顶点>+0.5m）后落回 y≈1 且 Mode=Walking"。
- Play 模式下才跑 `presentation*`：编辑模式下 `new GameObject` 会落到**用户当前场景**并把它标脏（违反"不触碰用户场景"），故显式跳过并在报告里写明原因与"请在 Play 模式再点一次"。

---

## 5. 契约 B2 逐条对应

| B2 要求 | 落点 | 证据 |
|---|---|---|
| 三个文件、namespace `PMNet.Unity`、只依赖 PMMover/PMPrediction/PMNetRole | 已按此建 | `PMR4UnityCheck` 编译面恰好是这三类只读依赖 |
| 实现 `IPMMoverCollisionQuery`，构造 `(layerMask, worldVersion)`，可选 allowlist 重载 | 三个构造重载 | 门禁 + Editor 用例 `allowlistIsolatesOtherColliders` |
| 胶囊中心/半高/半径遵守接口（中心 Y=halfHeight 落地） | `SphereOffset = halfHeight − radius`；`QueryGround` 用 `footY = center.Y − halfHeight` | `groundTouching` / `groundPenetratingSignedDistance` |
| 主线程约束 | 构造记线程 ID，跨线程抛 | 代码 + 类的显式契约注释 |
| 查询忽略 trigger | 一律 `QueryTriggerInteraction.Ignore` | 代码（不读写全局开关） |
| 最近阻挡 / 有限值 / 零 delta / 起点接触重叠 / 足底有符号地距"有证据" | 各有分支 + 统计计数 + Editor 断言 | `sweepZeroDeltaUnblocked` / `sweepResolvesStartOverlap` / `startOverlapPathExercised` / `groundPenetratingSignedDistance` |
| 查询缓冲固定上限、饱和显式失败而非静默忽略墙 | `HitBufferCapacity=32`，满即抛 | `saturationFailsExplicitly`（放置 40 个重叠薄盒、断言抛出且 `SaturationFailures==1`） |
| 禁移动 Transform / `Physics.Simulate` / 全局 autoSimulation / 改旧场景 | 代码中不出现 | 门禁编译 + 只读代码审查 |
| `Physics.SyncTransforms` 由宿主单次做、不能每条 resim 扫 | 适配器**永不**调用，写成宿主前置条件 | 类注释 + 本报告 |
| 真实 `CapsuleCast/Overlap/ComputePenetration` 按 2019.4 程序集验证，不用虚构桩件证明可编译 | 引真实 DLL 编译（`PMR4UnityCheck`）；`ComputePenetration/ClosestPoint` 一并核验存在，本实现未用（避免建临时 collider 的副作用） | 0 错误 + 43 个公开类型（含 4 新类型） |
| `PMUnityMoverInput`：`Sample()` 返回 `PMMoverInput`、WASD/Horizontal,Vertical+Space 边沿、纯转换入口、Y-up、不带派生速度、不双镜像 | 见 §4.3 | 64/64 用例（含 B9"不做镜像"断言） |
| `Tools` 新测试工程"最多 Program.cs/csproj" | `Tools/PMR4UnityAdapterTest/` 只有 `Program.cs` + `.csproj`（替身内联） | 边界自查见 §1 |
| `Tools/PMR4UnityCheck` 是类库（不冒充运行验证） | 无 `OutputType` / 无 `Program.cs` | 文件面 + 报告边界声明 |
| 跳跃边沿缓存到真实步、零 ms 不丢、补子步不重复 | 状态表 + `Consume` 只在 1..50 清 | F4/F5/G7/G8/G9/G9b/G9c/G9d/G10 |
| `PMUnityMoverPresentation`：可见胶囊、无碰撞、DS 绝不创建、构造 label/Role、`Apply` 一次写 pos/yaw/scale、Dispose 幂等、可选相机不改旧相机 | 见 §4.4 | 编译门禁 + Play 模式用例（PENDING_USER 运行） |
| `Tools/PMR4UnityCheck` 引真实 DLL、编译三文件及纯依赖、测试输入纯转换/边沿、**不宣称 net8 跑真实 Physics** | 见 §6 | 0 错误；AdapterTest 头部显式声明边界 |
| 可新增 Editor 菜单（不自动执行、不触碰用户场景、临时本地 PhysicsScene、创建后清理） | `PMR4UnityValidation.cs` | 预览场景路径 + `finally` 清理；报告显式标注未执行 |

---

## 6. 测试命令与结果

```bash
# 1) 真实 Unity 2019.4 程序集编译门禁（netstandard2.0 + C#7.3，UNITY_EDITOR 显式定义）
dotnet build Tools/PMR4UnityCheck -c Release -v q -nologo
# 2) 输入纯转换/边沿语义验收（net8，仅桩 Input/KeyCode）
dotnet build Tools/PMR4UnityAdapterTest -c Release -v q -nologo
dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
# 3) 结构完整性
python Tools/check_cs_braces.py Client/Assets/Scripts/PMUnity/*.cs Client/Assets/Editor/PMR4UnityValidation.cs \
       Tools/PMR4UnityAdapterTest/Program.cs
# 4) 既有 R4-A 回归（本次未改其文件，用于证明无回归）
dotnet build Tools/PMMoverCoreCheck -c Release -v q -nologo
dotnet build Tools/PMPredictionCoreCheck -c Release -v q -nologo
dotnet Tools/PMMoverTest/bin/Release/net8.0/PMMoverTest.dll
dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll
```

| 门禁 | 结果 |
|---|---|
| `PMR4UnityCheck`（真实 UnityEngine/UnityEditor DLL） | **build 0 错误 0 警告**；产物 43 个公开类型，含 `PMUnityMoverCollisionQuery` / `PMUnityMoverInput` / `PMUnityMoverPresentation` / `PMUnityCollisionQueryStats` / `PMNet.UnityEditor.PMR4UnityValidation`；程序集引用确认为 `UnityEngine.CoreModule + PhysicsModule + InputLegacyModule + UnityEditor`（⇒ 不是"空 glob 假绿"） |
| `PMR4UnityAdapterTest`（`Program.cs` + `.csproj`，替身内联） | **64 项 0 失败，退出码 0** |
| `check_cs_braces.py`（6 个新 .cs） | **PASS**（花括号/圆括号配平 + 顺序感知深度检查全 OK；含整改后的 `Program.cs`） |
| `Tools/PMMoverTest`（R4-A） | **245 项 0 失败** |
| `Tools/PMMoverPredictionTest`（R4-A 集成） | **264 项 0 失败** |
| `PMMoverCoreCheck` / `PMPredictionCoreCheck` | 0 错误 |
| 新增 `.cs` 编码 | 6 个文件全部 **UTF-8 + BOM + CRLF**（坑 #15） |
| `Tools/PMR4UnityAdapterTest` 文件面 | 仅 `Program.cs` + `PMR4UnityAdapterTest.csproj`（契约 B2 的"最多 Program.cs/csproj"） |
| 新增 `.meta` | 5 个，格式与仓库既有 meta 一致（LF），GUID 在**全仓 6830 个 meta 中唯一** |

### 6.1 负向验证（注入 → 必须被抓）

| # | 注入 | 期望通道 | 实测 |
|---|---|---|---|
| N1 | `Consume` 无视 `stepMs`（零 ms 也清） | AdapterTest | **抓**（4 项失败：F4/F5/G7/G8） |
| N2 | `SampleAndConsume` 漏掉消费 | AdapterTest | 首轮 **0 失败（测试盲区！）** → 补 G9b/G9c 后 **抓**（3 项失败） |
| N3 | `Convert` 去掉轴钳制 | AdapterTest | **抓**（A1：`MoveX=3`） |
| N4 | `DeriveYawDegrees` 交换 `atan2` 参数 | AdapterTest | **抓**（B1/B2/B3…） |
| N5 | `ApplyDeadZone` 用严格小于 | AdapterTest | **抓**（D2 边界相等） |
| N6 | 把 `PhysicsScene.IsValid()` 当属性写 | **真实 DLL 编译门禁** | **抓**（`CS1061: "PhysicsScene" 未包含 "isValid" 的定义`）——证明门禁校验的是**真实签名**，不是"能过就行" |
| N7 | `PollHardware` 去掉"一次按下只锁一次" | AdapterTest | **抓**（G9b/G9c：`s1=s2=s3=True`） |
| N8 | 去掉"松开复位"（边沿永久吞掉） | AdapterTest | **抓**（6 项失败） |

注入后每次都从备份还原，**并用 sha256 核对还原结果与原文件完全一致**（`restored sha == original: True`）。

> **N2 的价值**：它先暴露的是"这条路径没被测到"（0 失败），按项目纪律补齐用例后同一注入才变成 3 项失败。顺着它又发现真实缺陷 `PollHardware` 同帧重复锁边沿（G9b/G9c 现在同时守住这两个问题）。

---

## 7. 初始化 / 输入消费 / 调用次序（契约要求的四个口径）

**初始化（宿主 B3 应做的顺序）**
1. `PMNetRuntime` 已由 `PMNetBootstrap` 在 `SubsystemRegistration` 阶段完成身份判定（唯一来源）。
2. 每局：建/拿到权威碰撞场景（DS 或客户端用 `PMR3TestScene` 同值），**构造一次** `PMUnityMoverCollisionQuery`（客户端/DS 各自把地板/墙 `Collider` 作为 allowlist 传入以隔离旧场景），并把它注入 `PMMoverModel`（注入点只有模型构造函数一处）。
3. `PMMoverModel` 的 `PMMoverAuxState.CollisionWorldVersion` 必须等于适配器的 `WorldVersion`（契约：不一致则碰撞结果不可比）。
4. 表现层（仅客户端/非 DS）`new PMUnityMoverPresentation(label, role)`；DS 上不要实例化（会抛）。

**每帧调用次序（建议）**
```
每帧开始（一次性）：Physics.SyncTransforms()        // 宿主职责；适配器永不调用
输入：_input.PollHardware()                          // 或直接 Sample/SampleAndConsume
本地预测：SampleAndConsume(stepMs) → timeline 推进入 AP（重放同一输入帧时边沿随输入一起重放，符合 NP 语义）
DS：Pump/AuthorityInputBuffer 出步 → PMMoverModel.Simulate(step, input, sync, aux)
一轮校正/模拟收尾：Finalize（副作用只在这里，不在 resim 循环里）
表现：AP→ApplyPredicted(预测状态) / SP→ApplyInterpolated(插值状态)  // 只写 Transform，不查询物理
断线/离场：先停止驱动 → Dispose 表现层 + Freeze 预测（顺序不能反，否则会触发 ObjectDisposedException）
```

**输入消费口径**：硬件边沿 → `PollHardware` 锁进缓冲 → 采样进输入帧 → **第一个真实步（1..50ms）消费**。零 ms 占位步（DS 缺帧）**不消费**，因此边沿不会被它吞掉；同一按下在同一帧被多次轮询也**只产生一个边沿**。

**真实编译 vs 真实运行的严格区分**

| | 已证（本次） | 未证（需用户/后续） |
|---|---|---|
| 编译面 | 三文件 + Editor 菜单在**真实 2019.4 程序集**上 netstandard2.0+C#7.3 **可编译**；引用的物理/输入/场景/Editor API 签名真实存在 | — |
| 输入逻辑 | 纯转换、朝向、死区、真实步判定、边沿状态机在 net8 **真跑** 64/64 | Unity 真实 Input 的键位/InputManager 行为（替身不能证明） |
| 物理行为 | **未证**。`CapusleCast/OverlapCapsule` 的真实返回（尤其"水平扫掠时地面是否被报出""起始重叠是否返回 t=0"）只能由 Unity 内菜单给出 | floor/wall 扫掠、贴地平移、跳跃落地、起点重叠推出、饱和显式失败 ⇒ **PENDING_USER** |
| 表现层 | 编译面 + Play 模式用例（未执行） | 真实相机/视觉 ⇒ PENDING_USER |
| 网络/多端 | **未证、也未涉及**（B2 明确不接宿主） | 属 B3/B4 与 P4B6 |

---

## 8. 限制、未决项与剩余问题

**限制（诚实登记）**
1. **真实 PhysX 未运行**：本 Agent 未抢 Unity 工程锁、未点编辑器。菜单 `Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）` 需用户主动点一次；Play 模式下再点一次可得表现层证据。**不把 net8 通过宣传成物理已验证**（`PMR4UnityAdapterTest` 刻意不编译碰撞/表现代码，就是为了不制造这种假绿）。
2. **编辑器验证重复了 4 个场景常量**（地板/墙的中心与尺寸）：不能引用 `PMR3TestScene`，因为那会把整个 `PMDsSessionHost`（Control/Session/Logging 依赖）拖进门禁。缓解：验证里对"地板顶=0""墙右面=0.5"写了显式断言，常量被改动会先失败。
3. **两个参数属"待实测收敛项"**：`ContactOppositionEpsilon=1e-3`（见 §5.2 的论证，实测若报 `nonOpposingIgnored` 异常偏高需复核）、`HitBufferCapacity=32`（饱和阈值；测试场景只有 2 个 Collider，真实玩法对象更多时需按实测放大）。
4. **`QueryGround` 的支撑面判据基于 `Collider.bounds`（世界 AABB）**：对轴对齐盒体精确；对旋转/曲面碰撞体是"有界近似"（只在"足底已穿透"分支使用，且以"顶面不高于胶囊中心 + XZ 覆盖足迹"双重约束把墙这类头顶结构排除）。契约要求的是"有证据"，本批不声称对任意凸体精确。
5. 未实现（**不在 B2，也不声称已完成**）：斜坡/台阶/移动平台、trigger 语义、CharacterController 兼容、`ComputePenetration` 精确推出（刻意不用，避免建临时 collider 的副作用）。

**剩余问题 / 交接**
- **P4B3 状态建议**：`API 真实可编` = **PASS（有证据）**；`真实运行` = **PENDING_USER**（一条菜单即可闭合）。主计划 P4B6 亦为 PENDING_USER。
- **跨组阻塞（非本次改动引起）**：`Tools/PMClientCheck` 与 `Tools/PMR3RuntimeTest` 当前各 **1 个错误**：
  `Client/Assets/Scripts/PMR3/PMR3Player.cs(103) CS0246: 未能找到类型 PMR4MovementDriver`。
  根因：网络组（B1）新增的 `PMR4MovementDriver.cs` / `PMR4MovementCodec.cs`（mtime 15:09/15:11）不在上述两个既有工程的 include 列表里，而 `PMR3Player.cs`（mtime 15:11）已引用该类型。**这两个工程的结构里没有任何 `PMUnity/**` 或 `Assets/Editor/**`**，故与本次改动无因果关系。修法（把 B1 新文件加进那两个工程）属 B1 / 主 Agent 收口范围，**本 Agent 未越界修改任何既有 Tools 工程**。
- **B3 接线时需知的适配器契约**：每帧 `Physics.SyncTransforms()` 由宿主调用一次（适配器不调）；`WorldVersion` 必须与 `AuxState.CollisionWorldVersion` 一致；allowlist 传入新建测试地板/墙以隔离旧场景；查询必须在该适配器构造线程（主线程）上执行。
