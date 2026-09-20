# R4-B / B2 物理适配：对抗复审（冻结轴对齐 floor/wall 场景）

> 复审对象：`Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`、`Client/Assets/Editor/PMR4UnityValidation.cs`，直接引用仅 `PMMoverModel` 碰撞调用点、`IPMMoverCollisionQuery`、`PMUnityMoverPresentation`。
> 口径：**只读**复审。真实代码 + Unity 2019.4 程序集/官方文档为证据；**未知 PhysX 运行行为一律 PENDING_USER，不当作已确认缺陷**。
> 场景边界：**只覆盖冻结的轴对齐 floor/wall 测试场景**，不要求对任意地图等价。

## 0. 摘要（≤1500 字）

在冻结的轴对齐 floor/wall 场景下，适配器的**逻辑推演可以通过**全部 15 项真实 PhysX 断言：起点贴地水平移动不会被地面误判阻挡（`IsOpposing` 过滤 + 起点重叠路径的 `oy <= 0f` 零重叠守卫两条路都通向"不阻挡"）；墙壁阻挡 fraction=0.7、停在 x≈0.9+skin；`QueryGround` 的正/负/零距离语义与接口契约一致（穿透 −0.15 与 AABB 数学精确吻合）；allowlist 与 layerMask 与关系成立，诱饵盒测试期望值（0.7 / 0.133）与代码算术一致；饱和路径在 40 个薄盒下可达容量并显式抛出。**未发现会让该场景必然失败的逻辑阻断。**

但发现一个**验证有效性缺陷（高置信）**：`PMR4UnityValidation.cs:623` 只用 `scene.GetPhysicsScene().IsValid()` 判定隔离，而 2019.4 官方文档明确 `GetPhysicsScene()` 在场景没有本地物理场景时会**返回 `Physics.defaultPhysicsScene`**（它 `IsValid()==true`）。因此一旦 preview scene 不自带本地物理场景，验证会在报告里写 "mode=editor-preview-scene" 却实际查询**默认（用户）物理世界**——隔离证据不成立，且测试地板/墙会短暂进入默认物理世界（场景无 `LocalPhysicsMode` 时其 Collider 本就注册在全局世界）。最小修法：追加"不等于 `Physics.defaultPhysicsScene`"的判定并在报告里打印真实场景身份。

另有 4 项条件性风险（冻结场景不触发，但 B3 长链会）：① 饱和检查按**过滤前**原始命中数触发，宿主 `movementLayerMask`（`1 << layer`，测试地板/墙在默认层 0 ⇒ 近似"全部"）在真实地图上有 ≥32 个同层 Collider 穿过扫掠时会把 `InvalidOperationException` 抛出 `Simulate`；② Walking **永不**按 `ground.Distance` 回贴地面，若权威快照/重同步把足底放到地面顶面之下（哪怕 1e-7），水平扫掠会被判为地面起点重叠 ⇒ fraction=0 且只推出 0.001 m/帧，表现为卡死缓释；③ 编辑模式 `new GameObject` 先落在活动（用户）场景再搬入临时场景，很可能把用户场景标脏（与"不弄脏用户场景"口径冲突，需运行确认）；④ `IsAllowed` 用 `ReferenceEquals` 而非 Unity 实例相等，理论上存在静默漏掉地板/墙的风险。

证据强度问题：`Consume(MaxRealStepMs+1)` 断言是空断言（缓冲已被前一次 `Consume(50)` 清空）；`neverEnteredWall` 留了 0.05 m 穿墙余量却被表述成"永不入墙"。真实 PhysX 运行、preview scene 是否自带物理场景、饱和是否真达 32、贴地时 cast 距离是否为 ~0 ⇒ **全部 PENDING_USER**。

---

## 1. 已确认（真实代码 + 2019.4 程序集/官方文档）

### 1.1 API 面真实存在（独立复核，与本机 Unity 安装一致）

对 `D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine/UnityEngine.PhysicsModule.xml`（及 CoreModule / UnityEditor.xml）核对成员名与签名：

| 成员 | 证据 |
|---|---|
| `PhysicsScene.CapsuleCast(Vector3,Vector3,float,Vector3,RaycastHit[],float,int,QueryTriggerInteraction) -> int` | 存在于 XML |
| `PhysicsScene.OverlapCapsule(Vector3,Vector3,float,Collider[],int,QueryTriggerInteraction) -> int` | 存在于 XML |
| `PhysicsScene.IsValid` / `PhysicsScene.IsEmpty` | XML 前缀为 `M:`（**方法**，非属性）；摘要 "Gets whether the physics Scene is valid or not." |
| `Physics.defaultPhysicsScene` / `Physics.defaultContactOffset` | XML 前缀 `P:`（属性） |
| `Physics.SyncTransforms`、`Physics.autoSimulation`(P)、`Physics.queriesHitTriggers`(P)、`Physics.ComputePenetration` | 均存在 |
| `PhysicsSceneExtensions.GetPhysicsScene(this Scene)` | 在 **PhysicsModule**（不在 CoreModule）|
| `LocalPhysicsMode.Physics3D` / `CreateSceneParameters` / `SceneManager.CreateScene(string,CreateSceneParameters)` | 在 **CoreModule** |
| `EditorSceneManager.NewPreviewScene/IsPreviewScene/ClosePreviewScene/CloseScene` | 存在于 UnityEditor.xml |

结论：B2 报告"用真实 DLL 编译 0 错误 + Cecil 核实签名"的三条关键 API 事实（`IsValid` 是方法、`GetPhysicsScene` 是 PhysicsModule 的扩展方法、物理场景查询族齐全）**与本机 2019.4 文档一致**，实现未使用虚构 API。

### 1.2 官方文档确认的两条关键语义（直接决定实现走向）

- `Physics.CapsuleCast`（2019.4 页）原文：**"CapsuleCast will not detect colliders for which the capsule overlaps the collider."**
  ⇒ 适配器"扫掠未命中但起点重叠 ⇒ 用 `OverlapCapsule` + AABB 最小平移轴推出"的分支**是必需的**，不是冗余；也意味着验证里"胶囊中心 (0,1,0) 完全嵌在墙体内"的用例**不可能**由 cast 满足，必然走该分支（`startOverlapPathExercised` 断言可满足）。
- `Physics.OverlapCapsule` 返回 "Colliders **touching** or inside the capsule"（XML returns 文本）。
  ⇒ 足底**正好**贴地（gap=0）时 `OverlapCapsule` 会返回地板，`TryMeasureSupportPenetration` 给出 `signed = 0.0`，`QueryGround` 分支 1 直接返回 `Found=true, Distance=0, Normal=Up`，与 `groundTouching` 的 `|distance| ≤ 0.005` 期望一致。
- `PhysicsSceneExtensions.GetPhysicsScene`（2019.4 页）原文：**"Alternately the Scene may be using the default 3D physics Scene (Physics.defaultPhysicsScene) in which case that will be returned instead."** ⇒ 见 §2.1 的隔离判定缺陷。

### 1.3 冻结场景的逻辑推演（默认参数：radius=0.4、halfHeight=1.0、MaxSpeed=3.9、Accel=20、JumpSpeed=4、g=−9.81）

| 复审问题 | 结论 | 依据 |
|---|---|---|
| 起点贴地水平移动 | **不会被误判阻挡** | 双路安全：① cast 若报出地板（法线 +Y，与水平 dir 点积 0）被 `IsOpposing` 过滤；② 若 cast 不报（贴地命中不确定性），`TryResolveStartOverlap` 的 `oy = Overlap1D(capMin.y=0, capMax.y=2, −1, 0) = 0` 触发 `oy <= 0f` 守卫 ⇒ 跳过地板 ⇒ `bestAxis<0` ⇒ 返回 `Blocking=false`。**两条路都通向"可移动"** |
| 起点穿透扫掠（(0,1,0) 在墙内） | 走重叠分支，断言可满足 | 文档确认 cast 不检出自重叠；`ox=0.8 / oy=2 / oz=0.8` ⇒ 最小轴 X ⇒ `|normal|=1`，`Fraction=0` |
| 胶囊壁滑动 | 稳定停在 x≈0.9，无发散 | `Integrate`：命中 ⇒ 移动到接触点（fraction=(2.6−0.5)/3=0.7 ⇒ x=0.9）⇒ 沿 +X 推出 `CollisionSkinMeters=0.001` ⇒ 剥掉剩余位移法向分量（纯 −X 位移 ⇒ remaining≈0 ⇒ 返回）。下一帧再扫掠在 0.001 量级重新收束 ⇒ 收敛在 0.9~0.911，`minX` 断言带 [0.85,1.05] 有足够余量 |
| QueryGround 正负距离 | **符合接口契约** | 分支 1 返回 `footY − top`（负=穿透；(3,0.85,0) ⇒ 精确 −0.15，与断言 (−0.20,−0.10) 吻合）；分支 2 用 `maxDistance = distance` ⇒ 恒有 `Distance ≤ distance`；无支撑 ⇒ `Found=false, Distance=0, Normal=Zero`；`TryNearestCast` 用 `IsOpposing(dir=down)` 只接受向上法线（排除天花板/墙侧面） |
| allowlist 隔离 | 成立（AND 语义 + 取最近允许命中） | 构造做防御性拷贝并丢弃 null；`IsAllowed` 线性扫描；诱饵盒 (2,1,0) size(0.4,2,2) ⇒ 无白名单 fraction=(2.6−2.2)/3=0.133 <0.25，白名单 ⇒ 0.7 |
| 缓冲饱和 | 可达且显式失败 | `HitBufferCapacity=32`；40 个薄盒（z 间距 0.05 < z 尺寸 0.2，均在 6 m 内）⇒ `count` 达容量 ⇒ `RequireNotSaturated` 抛 `InvalidOperationException` 且计数 +1 |
| 落地/起跳 | 逻辑成立 | 顶点 `4²/(2·9.81)=0.815` > 0.5；落地分支用 `Position.Y -= ground.Distance` 精确把足底放到支撑面顶 ⇒ `|Y−1|` 应 ≈0 |

附加的两个**设计优点**（值得保留）：

- 几何全部走 `BoxCollider.center/size` + identity Transform（`PMR4UnityValidation.cs:AddWorldBox`），因此**不受 2019.4 默认 `Physics.autoSyncTransforms=false` 的 Transform→PhysX 投递时机影响**；适配器本身也从不调用 `Physics.SyncTransforms`（与契约"宿主每帧单次"一致）。
- 表观层 primitive 的 `CapsuleCollider` 是"先 disable 再销毁"，在 Play 模式下 `Destroy` 延迟到帧末，但 disable 已立即退出查询面 ⇒ 不会成为第二个阻挡体（契约要求的隔离）。

---

## 2. 高概率推断（依据与置信度）

### 2.1 【高置信 · 验证有效性】隔离判定不足，可能把"默认物理世界"当成隔离世界

- 位置：`Client/Assets/Editor/PMR4UnityValidation.cs:623` `if (scene.IsValid() && scene.GetPhysicsScene().IsValid()) { return true; }`（`mode` 已在 614 行写成 `"editor-preview-scene"`）。
- 机制：2019.4 文档明确 `GetPhysicsScene()` 在场景**没有**本地物理场景时返回 `Physics.defaultPhysicsScene`（该对象 `IsValid()==true`）。因此该判定**无法区分**"preview scene 自带的物理场景"与"回退到默认世界"。
- 后果（触发时）：报告显示 `mode=editor-preview-scene`（隔离），实际所有查询打在默认物理世界：
  - `query`（白名单 floor/wall）仍只认新地板/墙 ⇒ 走墙/跳落两条断言**仍能通过**（掩盖问题）；
  - `unscopedQuery` 与 `satQuery`（无白名单、`AllLayers`）会看到用户场景的全部 Collider ⇒ "隔离旧场景"的对照证据不再是它声称的东西；
  - 新建地板/墙会**短暂进入默认物理世界**（Play 模式下即真实玩法世界），并在 `RemoveObject` 后消失。
- 为什么不是"已确认缺陷"：preview scene 是否自带本地物理场景属运行期事实（见 §3），代码只缺"能发现失败模式"的判定。
- 最小修法（1 行 + 1 行日志）：把 623 行改为同时要求**不是默认物理场景**，例如 `scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene) == false`（`PhysicsScene` 在 2019.4 文档未列出 `op_Equality`，故用 `.Equals`；若不可用则退化为"建 Collider 之前 `IsEmpty()==true`"作信号），并把解析到的 `physicsScene` 身份/`mode` 打进报告，使隔离可审计。

### 2.2 【中高置信 · 触碰用户场景】编辑模式 `new GameObject` 先把对象放进用户活动场景

- 位置：`PMR4UnityValidation.cs:686` `NewTestObject`：`GameObject go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, tempScene);`
- 机制：`new GameObject` 落在**活动场景**（编辑模式下即用户场景），随后才搬入临时场景。Unity 编辑期对活动场景做增删会置脏；因此 `RunValidation` 结束后用户场景很可能出现"*"未保存标记，与文件头/报告"不弄脏用户场景""不保存任何资产"的口径冲突（对象本身确实被清掉了，但脏标记是另一回事）。
- 置信度：中高（这是 Unity 编辑期常见行为，但本次未运行 Unity，无法给出实测证据）。同类问题在 `PMUnityMoverPresentation` 构造里也存在（仅在 Play 模式下由验证调用，报告已就 Play 模式说明）。
- 最小修法/建议：运行时先记录 `SceneManager.GetActiveScene().isDirty` 与所有已加载场景的脏状态，跑完对比并把结果写进报告；若确认置脏，至少要在菜单摘要里明说（当前实现无法通过公开 API 回滚脏标记）。

### 2.3 【中置信 · 条件触发】饱和按"过滤前"原始命中数判定，宿主的宽 layerMask 会把可过滤命中变成硬失败

- 位置：`PMUnityMoverCollisionQuery.cs:356 / 410 / 489`（三处 `RequireNotSaturated(count, ...)` 都在 **allowlist 过滤之前**）。
- 契约本意（"饱和必须显式失败，不能静默漏墙"）使这个选择在**正确性**上是保守的：一旦返回数达容量，就无法证明未被返回者里没有"允许的墙"。因此**不改适配器**。
- 但直接消费者 `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs:531-545` 用 `movementLayerMask |= 1 << allowlist[i].gameObject.layer` 组装掩码；测试地板/墙由 `PMR3TestScene` 用默认层（0）创建 ⇒ 掩码等价于"默认层全体"。若宿主进程里还加载着旧地图（默认层 Collider），一次扫掠穿过 ≥32 个默认层 Collider 就会把 `InvalidOperationException` 抛穿 `PMMoverModel.Simulate`（模型无捕获）。
- 冻结场景不可达（世界里只有 2 个 Collider）。
- 最小修法（宿主侧，最小且不削弱门禁）：给测试地板/墙一个**专用层**，掩码只置该位；必要时实测真实命中数后上调 `HitBufferCapacity`。

### 2.4 【中置信 · 条件触发】Walking 不回贴地面 + 起点重叠 fraction=0 ⇒ 足底一旦穿透就会"卡死缓释"

- 位置：`PMMoverModel.cs` Walking 分支只读 `ground.Found/Normal`，**不消费 `ground.Distance`**；`Integrate` 对 `hit.Blocking && Fraction<=0 && |dot(remaining,normal)|<=eps` 直接 `return`（本帧不再前进）。
- 机制：若足底低于地面顶面 δ>0，则 `IsOpposing` 过滤掉 cast 的地板命中（法线 ⊥ 运动），进入 `TryResolveStartOverlap`；`oy=δ < ox/oz` ⇒ 最小轴 = Y ⇒ `Blocking=true, Fraction=0, Normal=Up` ⇒ 模型只 `Position += Up*0.001` 并结束本帧（水平不移动）。下一帧仍未脱离重叠（只抬了 0.001）⇒ 重复 ⇒ 垂直逃离速率 0.001 m/帧，且期间水平冻结。
- 为什么冻结场景不触发：起跳落地用 `Position.Y -= ground.Distance` 精确贴面（足底 == 顶面 ⇒ `oy == 0` 触发 `oy <= 0f` 守卫，地板被跳过），初始状态也是精确贴面；故 125 步内不产生 δ>0。
- 触发条件（均在冻结场景之外）：权威快照/重同步/参数写入把 Walking 状态的足底放到地面之下；或宿主自行构造带穿透的初始状态。
- 最小修法（可选，属模型侧而非 B2）：Walking 分支在 `ground.Found && ground.Distance < 0` 时同样把 `Position.Y -= ground.Distance`；或在 `Sweep` 的起点重叠解算里对"MTD 轴 = 运动方向之外且为支撑面"的情况返回不阻挡。**本次不建议动**，仅登记风险。

### 2.5 【低中置信 · 健壮性】`IsAllowed` 用 `ReferenceEquals` 而不是 Unity 实例相等

- 位置：`PMUnityMoverCollisionQuery.cs:533` `if (ReferenceEquals(_allowlist[i], collider))`。
- 影响：同一 native Collider 若被返回成不同的托管包装实例，白名单会**静默**判不通过 ⇒ 表现为"穿地板/穿墙"。Unity 对存活组件通常返回同一实例，故风险低；用 `Equals`（Unity 重载）或 `GetInstanceID()` 比较更稳。

### 2.6 【低置信 · 证据强度】两处断言比其表述弱

- `PMR4UnityValidation.cs:442` `input.Consume(PMUnityMoverInput.MaxRealStepMs + 1)` 之后的 `!HasBufferedJumpEdge` 是**空断言**：缓冲已被上一行 `Consume(50)` 清空，与"越界值不清空"无关 ⇒ 该语义实际未被测试覆盖（应在 `NotifyJumpEdge()` 之后直接 `Consume(51)` 再断言仍为 true）。
- `PMR4UnityValidation.cs:339` `neverEnteredWall = minX >= expectedStop - 0.05f` 允许 0.05 m 穿墙，却与 B2 报告中"永不入墙/不穿墙"的表述不等价（半径 0.4 的 12.5%）。建议收紧到 `expectedStop - 1e-3`（skin 量级）或如实写明容差。

---

## 3. 无法确定（缺少证据）——PENDING_USER

1. **preview scene 是否自带本地 `PhysicsScene`**：决定 §2.1 是否触发。若自带 ⇒ 623 行走"真隔离"分支；若不自带 ⇒ 走"默认世界"分支且报告仍写隔离。运行后 `isolatedScene` 那行的 `mode=` 会显式暴露（`editor-preview-scene` vs `runtime-local-physics-scene(fallback)`）——但两者都不足以证明隔离，需按 §2.1 修法补判定。
2. **编辑模式下 `SceneManager.CreateScene`（回退路径）是否可用**：该 API 文档表述为 "Create an empty new Scene **at runtime**"；编辑模式下的行为未验证（有 try/catch，最坏结果是 `isolatedScene=false` 并提前结束）。
3. **`PhysicsScene.CapsuleCast` 数组重载是否对 40 个重叠薄盒返回 ≥32 项**（饱和断言能否满足）；PhysX 批量查询的截断/去重策略无可离线证据。
4. **贴地时 cast 报告的 `hit.distance` 是否 ≈0**：若 623 分支 1 未命中而落到分支 2，`groundTouching` 的 `|d| ≤ 0.005` 容差与 `Physics.defaultContactOffset = 0.01` **同量级**，存在边界失败可能（`jumpLandsBackOnFloor` 的 0.02 容差更安全）。
5. **贴地水平扫掠时 cast 是否报出地板、以及其法线**：两种物理结果在逻辑上都不影响"不阻挡"结论，只影响诊断计数 `NonOpposingHitsIgnored`。
6. **`new GameObject` 是否把用户场景置脏**（§2.2）。
7. **`-executeMethod` 接受返回 `string` 的静态方法**：文件头把 CLI 入口作为正式用法，若 2019.4 拒绝非 void 签名，仅影响 CLI 路径（菜单路径不受影响）。未验证。

---

## 4. 已检查范围

**必读文档（按指定顺序，全篇读完，除主计划按指示只读首尾）**
- `D:/UGit/hyld-master/AGENTS.md`（入口 + 文档路由）
- `Client/Assets/AGENTS.md`、`Server/AGENTS.md`（确认本项目 Unity 侧无服务端物理、Y 轴语义、frameTime、旧输入链不在新链）
- `Docs/plans/net-r4-network-contract.md`（**B2 全文**，本复审的验收依据）
- `Docs/plans/_r4b_unity_report.md`（全文；本次即对其结论做对抗性复核）
- `Docs/plans/net-architecture-migration.md`（卷首 1–60 行 + 末尾 R4-B 实施计划与 P4B1–P4B6 表 2060–2087）

**源码（只读）**
- `Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`（全 710 行）
- `Client/Assets/Scripts/PMUnity/PMUnityMoverInput.cs`（全 456 行）
- `Client/Assets/Scripts/PMUnity/PMUnityMoverPresentation.cs`（全 316 行）
- `Client/Assets/Editor/PMR4UnityValidation.cs`（全 848 行）
- 直接引用：`PMMover/IPMMoverCollisionQuery.cs`（全 96 行）、`PMMover/PMMoverModel.cs`（560–860 行 `StepModes`/`Integrate` + `EffectiveRadius/EffectiveHalfHeight`）、`PMMover/PMMoverState.cs`（默认值/`CreateDefault`/`Epsilon`）
- 身份面：`PMNet/PMNetRole.cs`、`PMNet/PMNetRuntime.cs`（`IsDedicatedServer` 来源）
- **唯一超出清单的读取**：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` 约 500–560 行（适配器的直接消费者：`movementLayerMask` + allowlist 装配），仅用于评估 §2.3 的触发面；未扩散到其它宿主逻辑。

**2019.4 证据**
- 本机程序集文档：`D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine/UnityEngine.PhysicsModule.xml`、`UnityEngine.CoreModule.xml`、`UnityEditor.xml`（成员存在性/前缀/签名）
- 官方 2019.4 页面：`Physics.CapsuleCast`、`PhysicsSceneExtensions.GetPhysicsScene`、`EditorSceneManager.NewPreviewScene`（2022.3 同名页做交叉参考）

**未做（严格遵守边界）**：未编译、未运行 Unity、未抢编辑器锁、未改任何源码/资产、未提交、未递归委派；**本次唯一写入 = 本报告**。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

1. **修 §2.1（1 行）**：`PMR4UnityValidation.cs:623` 增加"物理场景 ≠ `Physics.defaultPhysicsScene`"判定，并把解析到的场景身份/模式写进报告 ⇒ 隔离可审计后再谈"隔离证据"。
2. **跑一次菜单**（编辑模式 1 次、Play 模式 1 次），把 Console 报告回贴，重点核对：`isolatedScene` 的 `mode=` 与 `scene=`、`[info] 扫掠后计数` 里 `startOverlap`/`nonOpposingIgnored`、`groundTouching` 的 distance、`saturationFailures=1`、`walkStopsAtWall` 的 `minX`、`jumpLandsBackOnFloor` 的 `finalY`。
   - 若 `groundTouching` 的 |distance| 落在 0.005~0.01 ⇒ 确认 §3.4 的 contactOffset 边界，容差需放宽到 0.02（或明确依赖分支 1）。
3. **运行期补一条脏标记检查**（§2.2）：跑前后对比 `SceneManager.GetActiveScene().isDirty`，把结论写进报告口径。
4. **B3 侧收口 §2.3**：给测试地板/墙分配专用层（掩码只置该位），不要依赖"层 0 + 白名单"在真实地图上成立；若确需保留宽掩码，先实测命中数再决定是否上调 `HitBufferCapacity`（**不要**改成过滤后计数，会削弱"不静默漏墙"门禁）。
5. **§2.6 两处断言收紧**（可选、低成本）：`Consume(51)` 改成"注入边沿后再越界消费"；`neverEnteredWall` 容差收到 1e-3 量级。
6. 只有在 1–3 完成后，才把 P4B3 的"真实运行"从 PENDING_USER 改为 PASS，并在证据列写清所用场景模式（preview / runtime-local / fallback）。
