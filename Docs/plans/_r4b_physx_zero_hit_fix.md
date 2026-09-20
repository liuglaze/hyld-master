# R4-B 物理适配：PhysX 零距离命中语义修复（_r4b_physx_zero_hit_fix）

> 范围：**只修** `Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs` 与
> `Client/Assets/Editor/PMR4UnityValidation.cs`，**新增** `Tools/PMR4CollisionAdapterTest/{Program.cs,PMR4CollisionAdapterTest.csproj}`，外加本报告。
> 不改 UE、不提交、不启动第二 Unity、不抢编辑器锁、不递归委派、不改用户场景、不跑 `svn update`。
> 语言面：Unity 2019.4 / C# 7.3 / netstandard2.0；两个 Unity 源文件 UTF8 BOM + CRLF（与既有口径一致），
> 新增 `Tools` 的 `Program.cs` 亦 BOM + CRLF，`csproj` 与既有 Tools 一致为无 BOM + LF，`Tools` 目录不需要 Unity meta。

---

## 0. 摘要

真实 Unity Play 的 6 项失败是**同一个根因**：PhysX 的 `CapsuleCast` 对「起点已接触/重叠」的 Collider
返回 `distance = 0` 且 `normal = -dir`。贴地（足底 Y == 地板顶 Y）时它就以「脚下的地面」报出这条假命中；
适配器当时只用「法线与运动方向相反」过滤，而 `-dir` **恒**满足该判据，于是：

1. 贴地水平扫掠被判 `fraction=0` ⇒ 角色原地走不动（`walkStopsAtWall minX=3`）；
2. 贴地竖直向上（起跳）位移被同一条假命中抵消（`jumpLandsBackOnFloor maxY=1`）；
3. 真正的墙 TOI（`2.1/3 = 0.7`）被 0 距离假命中抢走（`sweepBlockedByWall` / `allowlistIsolates…` / `defaultWorldIsolation`）；
4. Cast 分支先于 Overlap 分支返回 ⇒ 真正的「起点嵌墙」路径永不执行（`startOverlapPathExercised StartOverlapResolutions=0`）。

修法：`Sweep` 改成**三分法**——① `distance > 1e-5` 的是可信前向 TOI（取**最近**、仍要求法线相对）；
② `distance ≈ 0` 的候选**一律不采信原始法线**，改用 `OverlapCapsule` + 几何分类判定「起点正穿透」
（`Fraction=0` + **有效推出方向**，并计入 `StartOverlapResolutions`）；③ 判为「仅接触」时用**几何法线**
再判是否相对：相对才作为 `Fraction = max(0,gap)/|delta|` 的保守阻挡，不相对（脚下的地面/身后的墙/平行掠射面）忽略并计数。
**没有**缩小查询尺寸（不做 skin，避免窄缝漏墙），**没有**只取首命中，**没有**忽略全部 `distance==0`（那会穿墙）。
精确性边界写死在代码与报告中：轴对齐 Box（冻结场景的地板/墙）上 `bounds` 精确；其它形状 `bounds` 只是**超集**近似 ⇒
保守（宁可早挡、绝不漏墙），非 Box 分类计数供审计。

门禁：新增 `Tools/PMR4CollisionAdapterTest`（受控 Unity 查询双件 + **真实**适配器与**真实** `PMMoverModel`）。
在**修复前等价**的适配器上 **38 项检查 17 项失败（exit=1）**，修复后 **38/0（exit=0）**；
其中模型级用例的最坏值 `minX=3 finalX=3.124991` 与 `maxY=1 finalY=1 mode=Walking grounded=True`
与真实 Play 报告**逐位一致**，证明双件忠实复现了已观测语义。真实 Unity/PhysX 复跑仍属 **PENDING_USER**（不宣称）。

---

## 1. 真实证据（只读，取自 `%LOCALAPPDATA%/Unity/Editor/Editor.log`）

用户 Play 运行（隔离 `runtime-local-physics-scene`，`PASS=16 FAIL=6`）的完整失败项：

```
[FAIL] sweepBlockedByWall :: Hit(blocking=True fraction=0 normal=(1, 0, 0))（期望 blocking=true / fraction≈0.70 / normal≈(+1,0,0)）
[FAIL] startOverlapPathExercised :: StartOverlapResolutions=0（期望 ≥1）
[FAIL] allowlistIsolatesOtherColliders :: 白名单=Hit(blocking=True fraction=0 normal=(1, 0, 0)) 无白名单=Hit(blocking=True fraction=0 normal=(1, 0, 0))
[FAIL] defaultWorldIsolation :: 隔离世界无白名单扫掠=Hit(blocking=True fraction=0 normal=(0, 0, -1)) 默认世界扫掠=命中顶到缓冲上限→显式失败
[FAIL] walkStopsAtWall :: minX=3 finalX=3.124991 mode=Walking（期望 minX≈0.9）
[FAIL] jumpLandsBackOnFloor :: maxY=1 finalY=1 mode=Walking grounded=True（期望 maxY>1.5）
```

同时 `[info] 扫掠后计数：sweep=3 ground=3 startOverlap=0 penetratingGround=1 saturationFailures=0 …`
与 `groundTouching/Ground(distance=0, normal=Up)`、`floor.bounds.max.y=0`、`wall.bounds.max.x=0.5`
一起，钉住了当时的几何与分支走向。

**关键观测（三条 0 距离命中的法线都恰好等于 `-dir`）**：

| 用例 | 起点 | 扫掠方向 | 原始命中 |
|---|---|---|---|
| 贴地水平 | (3,1,0) | `(-3,0,0)` | `fraction=0, normal=(1,0,0)` = −dir |
| 贴地水平（隔离世界） | (10,1,0) | `(0,0,6)` | `fraction=0, normal=(0,0,-1)` = −dir |
| 起点嵌墙 | (0,1,0) | `(0,0,1)` | `fraction=0, normal=(0,0,-1)` = −dir |

⇒ 「穿透」与「仅接触」在**原始命中**上无法区分，且该法线与真实接触面（地板是 +Y）无关。
`finalX=3.124991` 也可由旧代码逐帧复算：每次被 0 距离命中挡住后沿 `normal=(1,0,0)` 推出
`CollisionSkinMeters=0.001`，125 帧 × 0.001 = 0.125 ⇒ 3 + 0.125 ≈ 3.125（与报告一致）。

---

## 2. 根因（代码级，逐条与上面证据对应）

旧 `Sweep`（修复前）：

```
1) CapsuleCast → TryNearestCast：只做 allowlist 过滤 + IsOpposing(normal, dir)
2) 若 1) 未命中 → TryResolveStartOverlap（OverlapCapsule + AABB 最小重叠轴）
```

失效链条：

- 贴地时 cast **会**返回地板的 0 距离命中，法线 `-dir` ⇒ `IsOpposing` 成立（`dot = -1 ≤ -1e-3`）
  ⇒ 接受为 `Blocking=true / Fraction=0`，**在第 1) 步就返回**；
- 于是第 2) 步（真正的起点重叠判定）永不执行 ⇒ `StartOverlapResolutions` 恒为 0；
- 同一次 batch 里真正的墙 TOI（2.1）虽然也在 buffer 里，但「取最近」选中的是 0 ⇒ 0.7 被抢走；
- 模型 `Integrate` 拿到 `fraction=0 + normal=(±1,0,0)`：位移被吃掉、沿法线推 0.001、
  剩余位移投影后约等于 0 ⇒ 本帧不再前进（贴地水平走不动）；竖直向上同理被抵消（起跳无效）。

**为什么不能靠文档字面回避**：`Physics.CapsuleCast` 的 2019.4 文档写「不会检测 capsule 已重叠的 Collider」，
但实测它**确实**返回了这条 0 距离命中（三条证据），因此必须以**实测语义**为设计依据，而不是文档措辞。

---

## 3. 修法（`PMUnityMoverCollisionQuery.cs`）

### 3.1 新增/更换的语义

| 位置 | 修复后行为 |
|---|---|
| `Sweep` 阶段 1 | `ScanCast`：`distance > RawZeroDistanceEpsilon(1e-5)` ⇒ 可信 TOI，仍要求 `IsOpposing`，取**最近** |
| `Sweep` 阶段 2 | `hasZeroDistanceCandidate \|\| hasCastPenetration` ⇒ `TryResolveStartPenetration`（`OverlapCapsule` + 几何分类）判**正穿透**：`Fraction=0` + 有效推出方向 + `StartOverlapResolutions++` |
| `ScanCast` 的 0 距离分支 | **不采信**原始法线：`MeasureCapsuleBounds` 给出 `gap` 与**几何法线**；`gap < -1e-4` ⇒ 穿透；否则 `IsOpposing(几何法线, dir)` 相对 ⇒ `Fraction = max(0,gap)/|delta|` 阻挡（计数 `ZeroDistanceContactsAccepted`），不相对 ⇒ 忽略（计数 `ZeroDistanceContactsIgnored`） |
| `QueryGround` 分支 2 | 复用同一 `ScanCast`（分支 1 的「支撑面重叠测量」保持不变），因此向下扫掠的 `+Y` 法线要求与饱和语义原样保留 |
| 防御分支 | 若 cast 侧判穿透而 `OverlapCapsule` 未报（正常不应发生），仍按穿透处理并计数 `ZeroDistanceCastPenetrations`（fail closed，绝不放过穿透） |
| 其余 | allowlist 过滤、`RequireNotSaturated` 饱和显式失败、主线程约束、不动 Transform / 不 `SyncTransforms` / 不 `Simulate` **全部保持不变** |

### 3.2 阈值与理由（不是拍脑袋）

- `RawZeroDistanceEpsilon = 1e-5`：PhysX 对「起点接触/重叠」给的是**精确 0**，留 1e-5 只吸收 float 往返噪声。
  比它远的正距离 TOI 一律可信，**不做任何尺寸收缩补偿**（收缩半径会让胶囊从窄缝挤过去 = 漏墙）。
- `PenetrationToleranceMeters = 1e-4`：精确贴地时几何分类算出 `gap = -2.98e-8`（纯 float 噪声），
  比阈值小 3 个数量级 ⇒ 绝不会把「正好贴地」误判成穿透；PhysX 默认 `contactOffset = 0.01` 是阈值的 100 倍；
  模型 `CollisionSkinMeters = 0.001` 是阈值的 10 倍。阈值以下按「仅接触」处理（既不冻结移动、也不假装穿透）。
- `ContactOppositionEpsilon = 1e-3`（沿用）：继续用于「法线必须与运动方向相对」，避免浮点噪声把垂直接触算成阻挡。

### 3.3 精确性边界（写明，不假装通用）

几何分类用 `Collider.bounds`（世界 AABB）对**竖直胶囊**做可分离的精确距离计算：

```
separation = sqrt(dx² + dy² + dz²)        // dx/dz：段 X/Z 到 AABB 区间的间隙；dy：段 Y 区间到 AABB Y 区间的间隙
gap = separation − radius
特征法线 = 各轴间隙正交合成的单位向量（面/棱/角）；separation == 0 时用胶囊 AABB ∩ 目标 AABB 的最小重叠轴
```

- **轴对齐 Box**（`PMR3TestScene` 与 Editor 验证的地板/墙都是 identity 旋转的 `BoxCollider`）：
  `bounds` 就是几何精确的 AABB ⇒ 上表结论**精确**；
- **其它形状**（旋转 Box / Mesh / Terrain / Sphere…）：`形状 ⊆ bounds` ⇒
  `distance(段, bounds) ≤ distance(段, 形状)` ⇒ `gap_bounds ≤ gap_shape`，因此
  「判不穿透」可靠（**绝不漏墙**）、「判穿透/接触」可能偏早（保守多挡一点），
  推出方向把胶囊推出 `bounds` 就必然推离形状（方向有效，但不承诺「最小」）；
- 非 Box 形状的分类次数计入 `NonBoxBoundsClassifications`（明确记录边界）；
  适配器**不**用 `bounds` 声称通用形状的精确 MTD。

只读复核时可知：本实现不读取 `Collider.transform` / `Transform.right|up|forward`，
因此 `Tools/PMUnityGlueCheck`（手写桩件）与 `Tools/PMClientCheck`、`Tools/PMR4UnityCheck`（真实 Unity DLL）三者**都能零错误编译**，
不需要为本次修复扩张任何桩件面（这也是选择「bounds 保守分类」而非「旋转感知精测」的直接原因；后者需要 `Collider.transform`）。

### 3.4 有意保留的粗糙面（登记，不在本次范围）

- **棱角/掠射面的 TOI 法线仍来自 PhysX**：若 PhysX 对一个"擦着棱角掠过"的接触给出侧向法线（`dot ≈ 0`），
  它会被 `IsOpposing` 忽略；此时最多**过冲一帧**，下一帧的几何穿透分支会把胶囊推回，
  **不存在穿透通过**。适配器不声称棱角精确 MTD。
- **多 Collider 同时穿透**时取「最深」者；深度并列时取 PhysX 返回顺序中的第一个（该路径只决定推出方向，属退化路径）。
- **模型侧既有口径**（physics review §2.4）：足底**真穿透**（> 0.1 mm）时 `Walking` 分支不会回贴地面，
  `Integrate` 会以 `fraction=0` 结束该帧 ⇒ 水平位移被吃掉、每帧只沿法线推 1 mm。
  该行为属 `PMMoverModel`（本次范围之外，且冻结场景不会触发：贴地是精确接触、落地分支精确贴面），
  修复后与修复前一致（旧代码同样会挡住），故本次仅登记、不改模型。

---

## 4. 新增门禁：`Tools/PMR4CollisionAdapterTest`

- 编**真实源码**：`PMUnity/PMUnityMoverCollisionQuery.cs`、`PMMover/**`、`PMPrediction/PMPredictionContracts.cs`、
  `PMNet/PMNetIdentity.cs`、`PMNet/PMNetRole.cs`；`Program.cs` 尾部是**受控 Unity 查询双件**。
- 双件语义（**明确声明不是 PhysX**）：`gap ≤ 1e-5 ⇒ distance=0 且 normal=-dir`（复现实测语义）；
  正间隙且接近 ⇒ `TOI = gap / |dot(dir, 法线)|`；候选数超容量 ⇒ 返回恰好容量（复现饱和语义）；
  `Physics.SyncTransforms` 计数（断言恒 0）；记录最后传入的 `QueryTriggerInteraction` 与 mask。
- 覆盖：A 语义自检 / B 贴地可移动 / C 墙（0.7、向内阻挡、朝外、沿墙、高速不漏墙、贴墙 1 mm 小位移）/
  D 起点正穿透（有效推出方向 + 计数 + 与方向无关 + 非 Box 保守边界）/ E allowlist 隔离 /
  F 地面与竖直向下 / G 饱和显式失败 / H **真实 `PMMoverModel`** 走墙与起跳 / I 无副作用（世界不变、
  零 `SyncTransforms`、恒 `Ignore`、跨线程显式失败）。

### 4.1 红/绿（哈希校验的安全临时副本）

| 适配器 | sha256 | 结果 |
|---|---|---|
| 修复前（as found，首次运行） | `90aa08ec5a872c21917cbe2ec8dc2dfb67da2e961470b873b7af3db42f9f4d13` | 门禁 **checks=37 failed=15** |
| 「修复前等价」最小变体（只回退两处机制） | `345a4289cefc6f833ca327a908457e70e4fa2393b025e6b0690018ea0e38c2e6` | 门禁 **checks=38 failed=17（exit=1）** |
| 修复后（当前） | `90829409d4f4f47250b3ff20c1599201f5a34252b792fc47fb2a2865eb0199c8` | 门禁 **checks=38 failed=0（exit=0）** |

变体只回退两处（与根因一一对应）：① `ScanCast` 的 `distance ≈ 0` 分支直接采信原始法线；
② `Sweep` 不调用 `TryResolveStartPenetration`。复核脚本先备份修复后文件、写入变体、跑门禁、
再用**哈希校验**写回修复后文件并复跑（RED_EXIT=1、GREEN_EXIT=0）。

**与真实 Play 逐位一致的关键值**（证明双件忠实）：

```
变体（修复前等价）：H1 minX=3 finalX=3.124991 mode=Walking ；H2 maxY=1 finalY=1 mode=Walking grounded=True
                     ←→ 真实 Play：walkStopsAtWall minX=3 finalX=3.124991 ；jumpLandsBackOnFloor maxY=1 finalY=1 mode=Walking grounded=True
修复后：            H1 minX=0.90099996 finalX=0.90099996 mode=Walking ；H2 maxY=1.7838081 finalY=1 mode=Walking grounded=True
修复后其它关键值：   C1 fraction=0.7 normal=(1,0,0) ；C5 fraction=0.51666665 ；E1 白名单 0.7 / 无白名单 0.13333331
                     D1/D2 fraction=0 normal=(1,0,0)（沿 ±X 有效推出轴）；startOverlap=2 ；zeroDistanceContactsIgnored=126（模型走墙期间的地面接触）
```

---

## 5. Editor 验证菜单的改动（`Client/Assets/Editor/PMR4UnityValidation.cs`）

**新增 2b) 段（真实 PhysX 可观测 + 更窄回归）**：

- **原始命中可观测**（在验证工具内、不进每帧日志）：直接对隔离物理场景做一次
  `CapsuleCast((3,1,0) → (-1,0,0))`，把每个候选的 `collider.name / distance / normal` 打成 `[info]`，
  让「distance=0 且 normal=−dir」这条语义以后可审计；
- **7 条新断言**（修复前会红）：`sweepGroundTouchHorizontalUnblocked`（贴地可走）、
  `sweepGroundTouchVerticalUpUnblocked`（起跳不被抵消）、`sweepVerticalDownBlockedByFloor`（向下仍被地面挡，fraction≈0.6）、
  `sweepIntoWallBlockedNearZero`（触墙向内、fraction≈0）、`sweepAwayFromWallUnblocked`（朝外可走）、
  `sweepAlongWallUnblocked`（沿墙可走）、`sweepHighSpeedDoesNotTunnel`（一帧 6 m 不漏墙，fraction≈0.5167）；
- `zeroDistanceSemanticsObserved`：`ZeroDistanceCandidates ≥ 1 && ZeroDistanceContactsIgnored ≥ 1`
  （证明「地面以 0 距离报出 ⇒ 几何分类忽略」这条修复路径真的执行了）。

**原有门槛不降低，且有一处收紧**：

- `sweepResolvesStartOverlap` 由「`fraction=0` 且 `|normal|=1`」**加强**为「法线还必须沿 ±X」
  —— 修复前它是**假通过**（`normal=(0,0,-1)` 的长度也是 1，但那是朝墙内 4 m 的 −dir 假法线）；
- `walkStopsAtWall` 的穿墙余量从 `0.05` 收紧到 `0.02`（原 0.05 = 半径的 12.5%），
  取值理由（覆盖 `defaultContactOffset=0.01` 可能造成的「接触点略早」）写在代码注释里；
- 其余失败用例（`sweepBlockedByWall` 的 `[0.55,0.75]`、`allowlistIsolatesOtherColliders`、
  `defaultWorldIsolation`、`saturationFailsExplicitly`、`jumpLandsBackOnFloor`）判据**原样保留**。

隔离纪律（`BuildIsolated` / 非默认世界校验 / 不触碰用户场景 / 跑前后 `isDirty` 对比 / 编辑模式提前返回）**未改动**。

---

## 6. 构建与回归证据（本机实测）

```
dotnet build Tools/PMR4CollisionAdapterTest -c Release -t:Rebuild   → 0 警告 / 0 错误
dotnet Tools/PMR4CollisionAdapterTest/bin/Release/net8.0/PMR4CollisionAdapterTest.dll
   → 全部通过（checks=38 failed=0），exit=0（修复前等价变体：checks=38 failed=17，exit=1）

dotnet build Tools/PMR4UnityCheck    -c Release -t:Rebuild          → 0 警告 / 0 错误（真实 2019.4 DLL + UNITY_EDITOR）
dotnet build Tools/PMClientCheck     -c Release -t:Rebuild          → 0 警告 / 0 错误
dotnet build Tools/PMUnityGlueCheck  -c Release -t:Rebuild          → 0 警告 / 0 错误

dotnet Tools/PMMoverTest/bin/Release/net8.0/PMMoverTest.dll                    → 全部通过：245 项检查，0 项失败
dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll → 全部通过：264 项检查，0 项失败
dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll   → 全部通过（checks=64 failed=0）
dotnet Tools/PMR4IntegrationTest/bin/Release/net8.0/PMR4IntegrationTest.dll     → 全部通过：113 项检查，0 项失败
```

文件卫生：两个 Unity 源文件与 `Tools/PMR4CollisionAdapterTest/Program.cs` 均为 `BOM=true / bareLF=0 / 全 CRLF`；
`PMR4CollisionAdapterTest.csproj` 与既有 Tools 一致为无 BOM + LF；`Tools` 侧未创建任何 `.meta`。

---

## 7. PENDING_USER（不宣称，需用户复跑一次真实 Unity）

1. **真实 Unity/PhysX 复跑**：本次禁止自动起 Unity / 抢锁，因此 2b) 段的新断言与全部既有断言的
   真实 PhysX 结果 **未取得**。请用户在 **Play 模式**下点一次
   `Tools / PMR4 / 验证 Unity 碰撞适配（真实 PhysX）`（菜单不自动执行），并把 Console 报告回贴。
   预期（Play 模式）：`PASS=30 FAIL=0` —— 真实 Play 那次是 `PASS=16 FAIL=6`（共 22 项），
   本次新增 8 项（7 条几何语义 + `zeroDistanceSemanticsObserved`），因此 22 + 8 = 30；
   其中 `[info] 原始 CapsuleCast(3,1,0)→(-1,0,0)` 应显示地板的 `distance=0` 与反向法线。
   （编辑模式下本验证会因 `SceneManager.CreateScene` 不可用而提前返回，检查项少于 30，属既有行为。）
2. **PhysX 在「正间隙但小于 contactOffset」区间是否也报 0 距离**：本机无法离线判定。
   本修法在**两种可能下都成立**（见 §3.1/§3.3）：报正 TOI ⇒ 走可信 TOI；报 0 ⇒ 几何分类给出
   `Fraction = max(0,gap)/|delta|` 的保守小 fraction，既不冻结也不漏墙。
3. **棱角/掠射面的原始法线选择**：由 PhysX 决定；本适配器只保证「最多一帧过冲、绝不穿透通过」，
   不声称棱角精确 MTD（§3.4）。
4. **真实 DS 二进制 / 双客户端联调**：本次未构建 DS、未起 Lobby 与客户端。

---

## 8. 边界与未改动项（明确列出）

- **不改**：allowlist 语义、饱和 fail-closed（仍按**过滤前**命中数抛 `InvalidOperationException`）、
  独立本地物理场景隔离与「非默认世界」校验、`Physics.SyncTransforms` 宿主每帧单次、
  表现层、`Tools` 既有桩件、任何宿主（`PMDsSessionHost` / `PMClientSessionHost`）。
- **不做**查询期尺寸收缩（skin），不需要几何/距离补偿论证；
- **不新增**查询副作用：不创建/移动/销毁 Unity 对象、不写 Transform、不 `Simulate`、不改全局物理开关、
  不打日志（门禁 I 段逐条断言：世界几何/启用位/Transform 逐项比对不变、`SyncTransforms` 调用数 = 0、恒 `Ignore`）。
- `IsAllowed` 仍用 `ReferenceEquals`（physics review §2.5 的既有登记项，**有意未改**，避免扩大 diff）。

---

## 9. 本次触碰的文件

| 文件 | 状态 | sha256 |
|---|---|---|
| `Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs` | 修改（仅此一个生产文件） | `90829409d4f4f47250b3ff20c1599201f5a34252b792fc47fb2a2865eb0199c8` |
| `Client/Assets/Editor/PMR4UnityValidation.cs` | 修改 | `2bd2aa3f97073f13385f94a0ad88721e170328f0c95b53fd2278a25b1fa85a0e` |
| `Tools/PMR4CollisionAdapterTest/Program.cs` | 新增 | `3aa9f0caaff07eb615b2149eaafa75ecea8ba35a411c07f439b811276e7df8e8` |
| `Tools/PMR4CollisionAdapterTest/PMR4CollisionAdapterTest.csproj` | 新增 | 见文件（无 BOM + LF） |
| `Docs/plans/_r4b_physx_zero_hit_fix.md` | 新增（本报告） | — |

未提交、未 `svn update`、未触碰其它源码/资产/场景；只读的 Editor.log 尾部用于取证（日志位于
`C:\Users\luomingcong\AppData\Local\Unity\Editor\Editor.log`，属工作区外且被 gitignore 覆盖，
故本次未用 `ffgrep` 而直接以 bash 只读尾部，符合全局检索规则的例外条款）。

---

## 10. 建议下一步（仍不自动执行）

1. 用户在 Play 模式跑一次菜单（§7.1），回贴报告；若 `zeroDistanceSemanticsObserved` 未通过，
   说明真实 PhysX 的 0 距离报告形态与已观测证据不同，需按回贴的实际 `distance/normal` 复算阈值。
2. 若将来真实玩法世界使用非 Box 地面（Mesh/Terrain），按 §3.3 的边界评估是否需要旋转感知精测
   （那会需要 `Collider.transform` 面，届时需同步补 `Tools/PMUnityGlueCheck/UnityStubs.cs` 的桩件面）。
3. `PMMoverModel` 的「足底真穿透 ⇒ 水平冻结」（§3.4）属模型侧取舍，若需要修，应作为独立批次处理。
