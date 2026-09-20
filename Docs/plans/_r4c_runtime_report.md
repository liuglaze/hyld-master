# R4-C / C2 正式内容运行时适配报告

> 交付范围：`Client/Assets/Scripts/PMUnity/` 三个新类 + 两个 CLI 门禁工程 + 本报告。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本轮只把 C2（P4C2）从 PENDING 推进到
> 「代码与命令行门禁通过、真实 Unity 运行/资源生成仍待用户侧」，**不得**据此宣称 C3/C4 完成。
> 未运行 Unity、未抢编辑器锁、未编译 UE、未提交、未递归委派。

---

## 0. 结论速览

| 项 | 结果 |
|---|---|
| C2 三个类 | `PMBattleContentManifest.cs`(505 行) / `PMUnityBattleMap.cs`(1225 行) / `PMUnityBattlePresentation.cs`(576 行)，各带 `.cs.meta`（GUID 唯一、UTF-8 BOM + CRLF） |
| 编译门禁（真实 Unity 2019.4 DLL） | `Tools/PMBattleContentRuntimeCheck` → **0 错误 0 警告**（netstandard2.0 + C#7.3，不桩 Unity API） |
| 纯规则门禁（net8 + 仅 JsonUtility 替身） | `Tools/PMBattleContentManifestTest` → **90 项 / 0 失败**，退出码 0 |
| manifest 强校验 | schema1 / mapId / seed=1380205313 / 固定 Resources 路径白名单 / contentDigest 小写 64 hex / collisionDigest 与 worldVersion **必须等于派生值**（手写常量被拒） |
| 地图加载 | manifest → map prefab → 专属 `LocalPhysicsMode.Physics3D` 场景（**拒绝默认物理世界**）→ 硬障碍白名单（≤4096、全 enabled/active、排除 trigger）→ 全参绑定既有 `PMUnityMoverCollisionQuery` |
| 出生位 | x=±15、z=-5/0/5，y 由**真实地面查询** + 半高求得，并用真实 Unity 胶囊重叠查询拒绝嵌墙；找不到地面/与障碍重叠都**显式失败**，不换假地面 |
| 角色表现 | 只实例化 C1 的干净 `PlayerVisualV1`；DS 与 Authority 一律抛异常；0 旧脚本 / 0 Collider / 0 Rigidbody 校验；唯一 Transform 写者；Animator `Speed` **存在才设**、root motion 关闭；`Dispose` 幂等 |
| 未执行（诚实边界） | 真实 Resources 加载、隔离物理世界、PhysX 出生位/墙判定、真实 JsonUtility 全行为 —— 全部 **PENDING（C1 生成资源 / C3 接线 / C4 实机）** |
| 资源状态 | `Client/Assets/Resources/PMNet/` **当前不存在**（C1 尚未产出）；因此本次**没有**任何"实机加载成功"证据 |
| C1↔C2 对账 | C1（并行写入组）已直接引用 C2 的冻结常量、派生函数与 `TryParseJson` 回读校验 ⇒ 接口一致（快照 20:39，见 §14） |

---

## 1. 必读文档与阅读次序（已按序完整阅读）

| 序 | 文档 | 对本轮的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口，指向三份下游文档 |
| 2 | `Client/Assets/AGENTS.md` | §2 坐标系与旧出生点（我方 `(15,1,-5/0/5)`、Y=1）、§1.1 客户端/DS 同二进制运行时判定、§11 文档同步约束 |
| 3 | `Server/AGENTS.md` | 服务端"不含 Unity"的边界、战斗链路索引（确认 C2 不碰服务端） |
| 4 | `Docs/plans/net-architecture-migration.md`（卷首 + 文末 R4-C 段） | 环境现状（本机 Unity 2019.4.8f1 路径）、禁止事项（不代提交/不抢锁）、P4C1–P4C4 状态表 |
| 5 | `Docs/plans/net-r4c-content-contract.md`（全文） | **冻结契约**：schema 字段、固定种子、三个 Resources 键、digest 派生规则、C2 逐条要求、验收边界 |
| 6 | `Docs/plans/_r4c_actor_survey.md` | 旧角色链事实（单一 `Remake/Player.prefab`、旧 `HYLDPlayerController`/`PlayerLogic` 争抢 Transform、missing script、新链 HeroId 未被消费） |
| 7 | `Docs/plans/_r4c_scene_survey.md` | 旧地图事实（`ScenseBuildLogic.InitData` 运行期生成、未播种随机、地形 Collider 构成、DS 目前只有 floor+wall 白名单容量 2） |
| 8 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 本机 shell 纪律（bash 工具按 Bash 解析、PowerShell 7 显式调用）——本轮未用 PowerShell |

**由文档决定的首搜入口（非盲搜）**：`Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`、
`Client/Assets/Scripts/PMMover/IPMMoverCollisionQuery.cs`、`PMMoverState.cs`、
`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（`PMR3TestScene.BuildIsolated` 的隔离做法与出生位参照）、
`Client/Assets/Scripts/PMNet/PMNetRole.cs` / `PMNetRuntime.cs`、`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`（门禁模板）。

---

## 2. 契约逐条落点（C2 要求 → 实现位置）

| 契约原文（要点） | 落点 |
|---|---|
| manifest 类字段与 schema 一致 | `PMBattleContentManifest` 的 8 个 public 字段；字段名/类型由 `PMBattleContentManifestTest` 的反射门禁钉死（多一个字段即失败） |
| `formatVersion=1, mapId="hyld-map2-v1", seed=1380205313, mapResource/playerResource` 固定 | `Validate()` + `Expected*` 常量；`IsAllowedResourceKey` 做结构 + 精确键双重白名单 |
| `contentDigest` 十六进制字符串 SHA256(64) | `IsLowercaseSha256Hex`（严格小写，拒绝大写/长度/非 hex/null） |
| `collisionDigest` uint 非 0、由 digest 确定派生 | `TryDeriveCollisionDigest`（前 4 字节**小端**，0→1）+ `Validate` 强制相等 |
| `worldVersion` int 正数、由 digest 确定派生 | `DeriveWorldVersion`（`& 0x7fffffff`，0→1）+ `Validate` 强制相等 |
| `Load+Validate 采用 JsonUtility` | `PMBattleContentManifest.TryParseJson`（JsonUtility.FromJson + 紧接强校验）；资源读取在 `PMUnityBattleMap.TryLoadManifest`（见 §4 设计决策） |
| 失败不退回 | 所有失败路径只回 `error` 字符串；无任何"临时地图/旧战斗"回退分支 |
| 地图 `IDisposable` | `PMUnityBattleMap`，`Dispose()` 幂等 |
| `static TryLoad(out map,out error)` | `PMUnityBattleMap.TryLoad(out PMUnityBattleMap map, out string error)` |
| 读取 manifest + map prefab 到专属 Physics3D 场景 | `TryLoad`：`Resources.Load<TextAsset/GameObject>` → `SceneManager.CreateScene(..., LocalPhysicsMode.Physics3D)` |
| 保证非默认 physics | `physicsScene.Equals(Physics.defaultPhysicsScene)` ⇒ 卸载场景 + 失败（**绝不**假隔离） |
| 先临时 activeScene 实例化、finally 还原 | `TryLoad` 的 `try/finally`（与 `PMR3TestScene.BuildIsolated` 同做法）+ `MoveGameObjectToScene` 兜底 + 落点校验 |
| Collider 清单有界 4096 | `MaxColliderCount = 4096`；达上限即失败（不静默截断） |
| 所有 Collider enabled/active | 逐个校验 `collider.enabled` 与 `gameObject.activeInHierarchy` |
| 排除 trigger 不作硬障碍 | `isTrigger` 计入 `TriggerColliderCount` 并**不进白名单** |
| 验证无任何 MonoBehaviour / 动态刚体 / 旧驱动 / 相机 | `CollectAndValidate`：missing script(null 项)/MonoBehaviour/Camera/Animator/AudioListener/非 kinematic Rigidbody/其它类型 全部点名失败 |
| 不重复实现物理系统 | 移动查询复用 `PMUnityMoverCollisionQuery` 的**全参构造**；出生位重叠用 Unity 官方 `PhysicsScene.OverlapCapsule`（与适配器同一场景/层/白名单），不自造碰撞 |
| 碰撞适配的通用形状限制必须报告 | `NonBoxColliderCount`/`BoxColliderCount` 暴露给 C3；限制本身见 §8 |
| 提供 `Manifest/Scene/PhysicsScene/Colliders/Query` | 同名属性（释放后语义见 §3） |
| `TryGetSpawn(teamIndex,slot,out PMMoverSyncState,out error)` | x=±15、z=-5/0/5；`Query.QueryGround` 找真实足底 + 半高；再 `OverlapCapsule` 拒障碍；失败不换假地面 |
| `GetSceneDigest` 与 manifest 数据协议一致 | 运行时结构摘要（uint、非 0；类型+layer+flag+量化世界 AABB+`worldVersion`）；**不是**编辑器内容摘要（§8） |
| 表现 `IDisposable`、`(label,role,manifest)` | `PMUnityBattlePresentation` 两个构造（第 4 参可选建测试相机） |
| 只加载清理后的 `PlayerVisualV1` | `Resources.Load<GameObject>(manifest.playerResource)`；**不**触碰 `Resources/Remake/Player.prefab` |
| 拒绝 DS / Authority 创建 | `PMNetRuntime.IsDedicatedServer` / `PMNetRoles.IsAuthority(role)` 抛 `InvalidOperationException`（另拒 `None`） |
| 验证无旧脚本/Collider/Rigidbody | `ValidateComponents`（允许集：Transform/MeshFilter/Renderer/Animator/LODGroup；其余点名失败） |
| 不在运行期 Instantiate 脏原件再禁脚本 | 无任何"实例化后禁用/删除组件"的路径，脏原件根本不会被加载 |
| `ApplyPredicted/ApplyInterpolated` 唯一 Transform 写入口 | 只写根节点 `position`/`rotation(yaw)`/`localScale`；子层级一律不写 |
| Animator 按确实存在的 `Speed` 驱动 | `HasFloatParameter(animator,"Speed")`（Float 且同名）为真才 `SetFloat`；不存在则完全不设，绝不 `AddParameter` |
| root motion 关闭 | `animator.applyRootMotion = false`（构造时显式再设一次） |
| 平滑不是第二仿真、不查询物理 | 本类零插值/零物理调用；SP 插值由驱动侧产出后喂入 |
| 可选相机只跟新根、不读旧静态、不改旧相机 | `CreateTestCamera` 挂角色根节点下；全程不读 `HYLDStaticValue`、不碰 `Camera.main` |
| 提供 `Transform Root`、相机不强制 | `Root`（`Transform`）+ `RootObject`；相机只在显式调用时创建 |
| `Dispose` 幂等 | 两者都用 `_disposed` 早退 + `Destroy/DestroyImmediate` |

---

## 3. 精确 API（供 C3 接线）

命名空间统一 `PMNet.Unity`。以下签名与实盘一致（由 §7 的编译门禁保证）。

### 3.1 `PMBattleContentManifest`（纯数据 + 规则）

```csharp
[Serializable] public class PMBattleContentManifest
{
    // 冻结常量
    public const int    ExpectedFormatVersion = 1;
    public const string ExpectedMapId         = "hyld-map2-v1";
    public const int    ExpectedSeed          = 1380205313;          // 0x52444301
    public const string ManifestResourceKey   = "PMNet/BattleContentV1";
    public const string MapResourceKey        = "PMNet/BattleMapV1";
    public const string PlayerResourceKey     = "PMNet/PlayerVisualV1";
    public const string ResourceDirectory     = "PMNet/";
    public const int    ContentDigestHexLength = 64;
    public const int    MaxResourceKeyLength   = 128;

    // 协议字段（字段名/类型即协议）
    public int    formatVersion;
    public string mapId;
    public int    seed;
    public string mapResource;
    public string playerResource;
    public string contentDigest;
    public uint   collisionDigest;
    public int    worldVersion;

    // 规则
    public static bool Validate(PMBattleContentManifest manifest, out string error);
    public static bool TryParseJson(string json, out PMBattleContentManifest manifest, out string error); // 解析 + 校验
    public static bool IsLowercaseSha256Hex(string value);
    public static bool TryDeriveCollisionDigest(string contentDigest, out uint collisionDigest, out string error);
    public static int  DeriveWorldVersion(uint collisionDigest);
    public static bool TryDeriveCollisionDigestAndWorldVersion(string contentDigest,
                                                               out uint collisionDigest,
                                                               out int worldVersion,
                                                               out string error);
    public static bool IsAllowedResourceKey(string key, string expectedKey, out string error);

    public PMBattleContentManifest Clone();
    public string Describe();
}
```

**给 C1 的接线点（重要）**：Editor 侧写 manifest 时请**直接调用**
`PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(...)` 生成 `collisionDigest`/`worldVersion`，
不要各写一份派生代码。C1 的 Editor 程序集（`Assembly-CSharp-Editor`）可以引用本运行时类，
这样"派生规则只有一处实现"就是结构性的，而不是靠两边人工对齐。

### 3.2 `PMUnityBattleMap`

```csharp
public sealed class PMUnityBattleMap : IDisposable
{
    // 有界/冻结常量
    public const int   MaxColliderCount           = 4096;
    public const int   TeamCount                  = 2;
    public const int   SpawnSlotCount             = 3;
    public const float SpawnXPositiveSide         = 15f;    // teamIndex 0
    public const float SpawnXNegativeSide         = -15f;   // teamIndex 1
    public const float SpawnProbeStartY           = 25f;
    public const float SpawnGroundProbeMeters     = 40f;
    public const float SpawnSupportUpMinDot       = 0.99f;
    public const float SpawnCenterYMin            = -10f;
    public const float SpawnCenterYMax            = 20f;
    public const float SpawnSupportToleranceMeters= 1e-3f;
    public const int   SpawnOverlapCapacity       = 64;
    public const float SpawnYawDegrees            = 0f;
    public const float DigestQuantizationPerMeter = 10000f;

    // 加载
    public static bool TryLoadManifest(out PMBattleContentManifest manifest, out string error);
    public static bool TryLoad(out PMUnityBattleMap map, out string error);

    // 读
    public PMBattleContentManifest      Manifest           { get; }   // 释放后仍可读（纯数据）
    public UnityEngine.SceneManagement.Scene Scene          { get; }  // 释放后 default(Scene)
    public UnityEngine.PhysicsScene     PhysicsScene        { get; }  // 释放后 default(PhysicsScene)
    public UnityEngine.Collider[]       Colliders           { get; }  // 硬障碍白名单，不含 trigger；释放后零长数组
    public PMUnityMoverCollisionQuery   Query               { get; }  // 释放后 null
    public UnityEngine.GameObject       Root                { get; }  // 释放后 null
    public int  TriggerColliderCount                        { get; }
    public int  KinematicRigidbodyCount                     { get; }
    public int  BoxColliderCount                            { get; }
    public int  NonBoxColliderCount                         { get; }  // bounds 近似生效面的量化证据
    public int  SpawnQueryCount                             { get; }
    public bool Disposed                                    { get; }

    // 出生位
    public static bool  TryResolveSpawnTeamIndex(int teamId, out int teamIndex); // 2→0(+x) / 1→1(-x)，其它 false
    public static float SpawnXForTeamIndex(int teamIndex);   // 非法 → NaN
    public static float SpawnZForSlot(int slot);             // 非法 → NaN
    public bool TryGetSpawn(int teamIndex, int slot, out PMMoverSyncState state, out string error);
    public bool TryValidateAllSpawns(out string error);      // 2×3 全量自检（C3 上报就绪前调用）
    public string DescribeSpawns();                          // 一行多列文本，便于日志/报告

    // 摘要与一致性
    public uint GetSceneDigest();                            // 运行时结构摘要（非编辑器内容摘要）
    public bool ValidateManifestConsistency(out string error);

    public string Describe();
    public void Dispose();                                   // 幂等
}
```

**释放后语义（已实现，C3 必须按此写代码）**：`Root`/`Query` → `null`；`Colliders` → 零长数组；
`Scene`/`PhysicsScene` → `default`（`IsValid()==false`）；`Manifest` 仍可读；
`TryGetSpawn`/`TryValidateAllSpawns`/`ValidateManifestConsistency` 抛 `ObjectDisposedException`。

### 3.3 `PMUnityBattlePresentation`

```csharp
public sealed class PMUnityBattlePresentation : IDisposable
{
    public const float  DefaultCameraDistanceMeters = 6f;
    public const string SpeedParameterName          = "Speed";
    public const float  MaxSpeedParameterValue      = 1f;

    public PMUnityBattlePresentation(string label, PMNetRole role, PMBattleContentManifest manifest);
    public PMUnityBattlePresentation(string label, PMNetRole role, PMBattleContentManifest manifest,
                                     bool createTestCamera);

    public string                        Label                      { get; } // 构造参数
    public PMNetRole                     Role                       { get; }
    public PMBattleContentManifest       Manifest                   { get; } // 内部副本
    public string                        PlayerResourceKey          { get; }
    public UnityEngine.Transform         Root                       { get; } // 唯一被写入的 Transform
    public UnityEngine.GameObject        RootObject                 { get; }
    public UnityEngine.Animator          Animator                   { get; } // 可空
    public int                           AnimatorCount              { get; }
    public bool                          AnimatorHasSpeedParameter  { get; }
    public UnityEngine.Camera            TestCamera                 { get; } // 未创建/已释放 → null
    public int                           ApplyCount                 { get; }
    public PMMoverSyncState              LastApplied                { get; }
    public string                        LastSource                 { get; } // "predicted"/"interpolated"/"none"
    public float                         LastSpeedParameterValue    { get; }
    public bool                          Disposed                   { get; }

    public void   Apply(PMMoverSyncState state);                 // 唯一写入口
    public void   ApplyPredicted(PMMoverSyncState state);        // AP
    public void   ApplyInterpolated(PMMoverSyncState state);     // SP（插值在驱动侧）
    public static float ComputeSpeedParameterValue(PMMoverSyncState state); // 纯映射（0..1）
    public UnityEngine.Camera CreateTestCamera(float distanceMeters);
    public void   Dispose();                                     // 幂等
}
```

**构造失败语义（全部抛异常，不静默降级）**：

| 条件 | 异常 |
|---|---|
| `PMNetRuntime.IsDedicatedServer` | `InvalidOperationException`（DS 拒建） |
| `PMNetRoles.IsAuthority(role)` | `InvalidOperationException`（权威侧无表现） |
| `PMNetRoles.IsNone(role)` | `InvalidOperationException`（未参与复制） |
| manifest 未通过强校验 / 为 null | `ArgumentException` |
| `Resources` 缺 `playerResource` | `InvalidOperationException`（提示先跑 C1 菜单） |
| prefab 根节点未激活 | `InvalidOperationException` |
| 含 missing script / MonoBehaviour / Collider / Rigidbody / Camera / AudioListener / 未允许类型 | `InvalidOperationException`（点名类型与对象路径） |

---

## 4. C3 初始化顺序（建议接线序列 + 结果）

```text
① PMUnityBattleMap.TryLoadManifest(out manifest, out error)
     ├ 失败 ⇒ 立刻失败并停止（"资源未生成"或"manifest 不合法"，error 已可归因）
     └ 成功 ⇒ manifest.Describe() 进日志（mapId/seed/worldVersion/digest）

② （可选）把 manifest 或 resourceDigest 交给会话握手（C3 的显式会话契约）
     └ C2 只提供数据，不决定握手语义；**不要**把 GetSceneDigest() 当内容摘要发给对端做防篡改

③ PMUnityBattleMap.TryLoad(out map, out error)
     ├ 失败 ⇒ 失败并停止（绝不回退临时地图）
     └ 成功 ⇒ 顺序固定：
          · SceneManager.CreateScene("[PMNetBattleMap]Physics<n>", LocalPhysicsMode.Physics3D)
          · 校验 physicsScene.IsValid() 且 != Physics.defaultPhysicsScene
          · 临时 SetActiveScene(隔离场景) → Instantiate(map prefab) → finally 还原原活动场景
          · MoveGameObjectToScene 兜底 + 落点校验
          · CollectAndValidate：白名单 ≤4096、全 enabled/active、trigger 排除、0 旧脚本/相机
          · layerMask = ∪(1 << collider.layer)
          · Physics.SyncTransforms()（宿主前置条件，一次性）
          · new PMUnityMoverCollisionQuery(physicsScene, layerMask, manifest.worldVersion, colliders)
          · 计算 GetSceneDigest() + ValidateManifestConsistency()（失败即 Dispose 并拒绝）

④ map.TryValidateAllSpawns(out error)（建议在"上报就绪"之前做一次 fail-fast）
     ├ 失败 ⇒ 拒绝就绪（不会带着一个嵌墙出生位开局）
     └ 成功 ⇒ map.Describe() + map.DescribeSpawns() 进日志

⑤ 每个玩家副本（客户端）：
     new PMUnityBattlePresentation(label, role, map.Manifest)     // role ∈ {AP, SP}
     ├ AP：驱动 Sample/预测输出 → presentation.ApplyPredicted(state)
     └ SP：驱动插值输出        → presentation.ApplyInterpolated(state)
     相机/UI 绑定 presentation.Root（本类不强制建相机）

⑥ 退出/换局：
     presentation.Dispose() → map.Dispose()（都幂等；先停止驱动再释放）
```

**结果（本轮实际取得的证据）**：③④⑤⑥ 的**代码路径**已在真实 Unity 2019.4 API 上编译通过（§7），
但**没有**在 Unity 内执行过——原因见 §6（`Resources/PMNet/*` 尚不存在，C1 未运行）。
`PMBattleContentRuntimeCheck` 不加载资源、不建场景，因此它证明的是"API 与类型正确"，不是"加载成功"。

### 4.1 `worldVersion` 统一（C3 必须处理）

`PMUnityBattleMap` 把运动查询的 `WorldVersion` 绑成 **`manifest.worldVersion`**（由 digest 派生，
两端同构建即同值），因此：

- 现有宿主的常量 `PMDsSessionHost.MovementWorldVersion = 1` 与
  `PMR3Runtime.CollisionDigest` 的测试布局口径**必须**在 C3 换成 manifest 口径
  （`map.Manifest.worldVersion` + `map.GetSceneDigest()` 或等价握手），
  否则快照里的 `Aux.CollisionWorldVersion` 与客户端不一致会立刻触发重同步/拒绝。
- `PMUnityBattleMap.ValidateManifestConsistency` 已把"查询 WorldVersion == manifest.worldVersion"
  做成一次显式自检，C3 可直接复用它做启动期断言。

---

## 5. 冻结规则与常量的可视化核对

### 5.1 摘要派生（契约原文 → 实现）

```
contentDigest   = 小写 SHA256 hex（64 字符）
b0..b3          = contentDigest[0..1] [2..3] [4..5] [6..7] 解析出的 4 个字节
collisionDigest = b0 | b1<<8 | b2<<16 | b3<<24         （小端；为 0 ⇒ 1）
worldVersion    = (int)(collisionDigest & 0x7fffffff)  （为 0 ⇒ 1）
```

反例（全部被 `Validate` 拒绝，且有对应断言）：大端解释、`+1`、`0`、`worldVersion=0/-1`、
`contentDigest` 大写/长度错/非 hex。

### 5.2 manifest 形态示例（C1 应产出这个形状）

```json
{
  "formatVersion": 1,
  "mapId": "hyld-map2-v1",
  "seed": 1380205313,
  "mapResource": "PMNet/BattleMapV1",
  "playerResource": "PMNet/PlayerVisualV1",
  "contentDigest": "1f2e3d4c……（64 位小写 hex）",
  "collisionDigest": 1279077919,
  "worldVersion": 1279077919
}
```

（上例的两个数字就是 `contentDigest` 前 8 个 hex 字符 `1f2e3d4c` 按小端派生的结果，
本报告的数字与 `PMBattleContentManifestTest` 的断言一致。）

### 5.3 出生位规则（首版冻结）

| 项 | 值 / 规则 |
|---|---|
| teamIndex | `0 → x=+15`，`1 → x=-15`；非法索引失败 |
| slot | `0 → z=-5`，`1 → z=0`，`2 → z=5`；非法槽位失败 |
| y | 由 `QueryGround` 在 x/z 上向下 40 m 找**真实**支撑面，取 `supportTopY + halfHeight`；不在 [-10,20] 内失败 |
| 朝向 | 固定 `0°`（与现有 DS 的 `BuildSpawnSync` 一致）；模型前向轴未实机确认，本类**不猜** |
| 镜像 | **不做本地全局镜像**：世界坐标即网络坐标；两端都用同一 `teamIndex→x` 映射 |
| 团队映射 | `TryResolveSpawnTeamIndex`：`TeamId 2 → teamIndex 0 (+x)`、`TeamId 1 → teamIndex 1 (-x)`（与现有 DS 的 `sign` 约定一致，禁用"行号奇偶"兜底） |
| 失败 | 找不到地面 / 支撑面非朝上平面 / 起点已穿透 / 中心 Y 越界 / 与障碍重叠 ⇒ 全部**显式失败**，**绝不**换假地面 |

**注意（供 C4 对账）**：旧链 `AGENTS.md §2` 记录的"敌方 `(-15,1,5/0/-5)`"是**旧链本地镜像视图**下的坐标；
本版按契约「不做本地全局镜像」采用 z 槽位不镜像（两队共用 `-5/0/5`，仅 x 分侧）。
若 C4 实机发现需要 z 镜像，改动点唯一：`SpawnZBySlot` 的取用规则（一处），并在契约里显式登记。

---

## 6. 待用户 / C1 生成的资源（当前阻塞项）

| 资源 | 期望路径（Resources 键） | 生产者 | 现状 |
|---|---|---|---|
| 地图 prefab | `Client/Assets/Resources/PMNet/BattleMapV1.prefab`（键 `PMNet/BattleMapV1`） | C1 `Build/Prepare PMNet Battle Content` | **不存在** |
| 角色表现 prefab | `Client/Assets/Resources/PMNet/PlayerVisualV1.prefab`（键 `PMNet/PlayerVisualV1`） | C1 | **不存在** |
| manifest | `Client/Assets/Resources/PMNet/BattleContentV1.json`（键 `PMNet/BattleContentV1`，TextAsset） | C1 | **不存在** |

⇒ 本轮**没有**任何"真实加载成功"的证据，C2 只做到"缺资源时明确失败"这条纪律的代码落地。
用户下一次在 Unity 里跑 C1 菜单后，可用 §12 的命令链 + §4 的顺序做首次真实验证。

### 6.1 给 C1 的接口约束（C2 会点名失败，请勿踩）

1. 地图 prefab **只允许**：`Transform` / `MeshFilter` / `MeshRenderer` / `Collider` / `LODGroup` /
   kinematic `Rigidbody`。**不得**含：任何 `MonoBehaviour`、missing script、`Camera`、`Animator`、
   `AudioListener`、**Light**、`Rigidbody`(非 kinematic)、任何其它组件（C2 报类型全名）。
   → 旧场景里的 `Directional Light` 之类必须由 C1 删除，否则 C2 直接拒绝加载。
2. 地图 prefab 内**所有** Collider 必须 `enabled` 且所在 GameObject `activeInHierarchy`
   （"藏一个没启用的 Collider"等于几何缺失，C2 会失败）。
3. 角色 prefab **只允许**：`Transform` / `MeshFilter` / `Renderer`(含 SkinnedMeshRenderer 等) /
   `Animator` / `LODGroup`。**不得**含 Collider / Rigidbody / Camera / AudioListener / 任何
   `MonoBehaviour` / missing script / ParticleSystem 等其它组件。
4. 两个 prefab 的**根节点必须激活**（否则 C2 拒绝）。
5. `Animator.applyRootMotion` 请烘焙为 `false`（C2 仍会再设一次）；
   `Speed` 参数请保留为 **Float** 类型（C2 只在"确实存在该 Float 参数"时才驱动它；
   不存在不会报错，但角色就不会有移动混合）。
6. manifest 的 `collisionDigest`/`worldVersion` 请用
   `PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion` 生成。

---

## 7. 验证证据与复现命令

### 7.1 编译门禁：`Tools/PMBattleContentRuntimeCheck`

```
dotnet build Tools/PMBattleContentRuntimeCheck -c Release
→ 已成功生成。0 个警告 / 0 个错误
→ bin/Release/netstandard2.0/PMBattleContentRuntimeCheck.dll
```

- 目标框架 `netstandard2.0`，`LangVersion 7.3`（与 Unity 2019.4 的 `apiCompatibilityLevel=6` 一致）。
- 引用**真实** Unity 2019.4.8f1 程序集：`UnityEngine`、`CoreModule`、`PhysicsModule`、
  `AnimationModule`、`JSONSerializeModule`、`AudioModule`、`TerrainPhysicsModule`、`SharedInternalsModule`。
  **不写任何 Unity 桩件**，**不**引用 `UnityEditor`（C2 三文件进播放器构建，不得依赖编辑器程序集）。
- 编译面（刻意最小）：C2 三个文件 + `PMUnity/PMUnityMoverCollisionQuery.cs`（地图复用的真实适配器）+
  `PMMover/{PMMoverState,IPMMoverCollisionQuery,PMMoverModel}.cs` +
  `PMPrediction/PMPredictionContracts.cs` + `PMNet/{PMNetIdentity,PMNetRole,PMNetRuntime,PMNetLaunchOptions}.cs`。
- 迭代过程（首轮"最小骨架"→ 修编译面）：首轮 4 个错误全是**缺少模块引用**
  （`Animator`→AnimationModule；`AudioListener`→AudioModule；`TerrainCollider`→TerrainPhysicsModule；
  `JsonUtility`→JSONSerializeModule），补引用后 0 错误。这也是"真实 DLL 才能发现的错"的一例。

### 7.2 纯规则门禁：`Tools/PMBattleContentManifestTest`

```
dotnet build Tools/PMBattleContentManifestTest -c Release      → 0 警告 / 0 错误
dotnet Tools/PMBattleContentManifestTest/bin/Release/net8.0/PMBattleContentManifestTest.dll
→ PMBattleContentManifestTest: 90 项，失败 0 项。  退出码 0
```

分节覆盖（每节都由断言钉住，失败会打印原因）：

| 节 | 内容 | 关键断言举例 |
|---|---|---|
| A | 冻结常量 | `seed == 1380205313 == 0x52444301`；三个 Resources 键 |
| B | schema 形状 | public 实例字段**恰好 8 个**（禁止私加共享字段）；8 个字段名/类型逐一比对；`[Serializable]` |
| C | 摘要派生 | `01000000…`⇒`1`（**小端**，大端会是 0x01000000）；全 0⇒1；`0x80000000`⇒`worldVersion=1`；全 ff⇒`0x7fffffff`；大写/长度/非 hex 被拒 |
| D | 合法 manifest 往返 | `Validate` 通过；JsonUtility 往返后 8 字段一致；未知字段被忽略；`Clone` 解耦 |
| E | 资源键白名单 | 非冻结键 / 目录不符 / 无前缀 / 含扩展名 / 上跳 / 反斜杠 / 绝对路径 / 空目录段 / 空白 / 空串 / null / 超长 全部被拒 |
| F | 固定值门 | `formatVersion=2/0`、`mapId=map4/null`、`seed=0/2`、两个资源键互换 全部被拒 |
| G | digest 内部一致 | `collisionDigest=0`、`+1`、**大端解释**、`worldVersion=0/-1/+1`、`contentDigest` 大写/63/65/非 hex/null、`null manifest` 全部被拒 |
| H | JsonUtility 边界 | `null`/空串/`"{"`/`"[]"`/`"{}"`/缺 `worldVersion` 全部被拒，且原因可读 |
| I | Unity 依赖面纪律 | 源码扫描（剥注释与字符串字面量）：除 `JsonUtility` 外**0 个** Unity 类型用法；`JsonUtility` 恰好出现 1 次 |

**"只桩 JsonUtility 边界"的实现方式**（`Program.cs` 尾部，刻意不做成独立文件，因为 C2 限定本工程最多
`Program.cs`/`csproj` 两个文件）：用 `System.Text.Json` 解析 + 反射按 public 字段名赋值，
只提供 `UnityEngine.JsonUtility.FromJson<T>` 一个成员。这同时把
"manifest 悄悄引入第二个 Unity 依赖"变成**编译错误**（I 节的源码扫描把它变成一条可读断言）。

**区分实机的口径（本工程承担 vs 实机未验）**：

| 行为 | 本工程 | 实机 |
|---|---|---|
| 未知字段忽略 | 替身断言通过 | 待 C1 回读 / C3 加载确认 |
| 缺字段留默认值 | 替身断言通过 | 待确认 |
| **`uint collisionDigest` 可被解析** | 替身断言通过 | **待确认**；已由 C1 的写盘后回读门禁覆盖（见 §14），但 C1 尚未跑过 |
| 非法 JSON 抛异常、根必须为对象 | 替身断言通过 | 待确认（语义同 Unity 文档） |
| 资源加载 / 隔离物理世界 / PhysX 出生位 | **未执行** | C3 / C4 |

---

## 8. 现碰撞查询对"非 Box 近似"的限制报告（供后续处理）

**事实（来自既有实现，本轮未改动）**：`PMUnityMoverCollisionQuery` 对 `distance ≈ 0` 的候选
改用**几何分类**，分类输入是 `Collider.bounds`（世界 AABB）：

- 轴对齐 `BoxCollider` 上 `bounds` 就是精确 AABB ⇒ 分离量/特征法线**精确**；
- 旋转 Box / `MeshCollider`(多为 `convex=0`) / Sphere… 上 `bounds` 只是**超集** ⇒
  `gap_bounds ≤ gap_shape`，因此「判为穿透/接触」可能偏早（保守早挡），
  「判为不穿透」**一定不穿透**（绝不漏墙）；推出方向有效但**不承诺最小**。
- 该分支的命中数计入 `PMUnityMoverCollisionQuery.GetStats().NonBoxBoundsClassifications`。

**本轮新增的量化暴露**：`PMUnityBattleMap.NonBoxColliderCount` / `BoxColliderCount`
（白名单里的形状构成）。正式 map2 的 Collider 构成需要 C1 产出后由这条计数**实测**，
当前**没有数字**（不猜）。

**结论（诚实口径，不许被读成"任意网格精确 MTD"）**：

1. 正式地图（含 MeshCollider + 旋转 Box）**会**走保守近似分支，行为是"宁可早挡、绝不漏墙"，
   会产生"离墙还有一点点就被挡住 / 沿棱角擦过时的一帧过冲（下一帧自愈）"这类**可接受但非精确**的表现；
2. 首版**不**声称"任意 Mesh 的精确 MTD / 通用碰撞"，也**不**为此改动适配器（越界）；
3. 后续处理建议（留给 C3/C4 或独立批次，本报告只登记）：
   - 用 `NonBoxColliderCount` + `GetStats().NonBoxBoundsClassifications` 量化"近似生效面"，
     必要时把高频接触面的形状换成轴对齐 Box（几何代价最小、精度收益最大）；
   - 若需要精确 MTD，应作为**独立议题**（收敛轴对齐、或引入凸包距离查询），
     不得在本轮用"看起来更准"的局部改动悄悄替换掉已被门禁覆盖的行为。

**另一条已登记的限制**：出生位重叠判定同样基于 `Collider.bounds.max.y` 与
"顶面 ≤ 支撑面顶"的判据（`SpawnSupportToleranceMeters = 1e-3`）。
在非 Box 形状上它同样是保守逻辑（宁可判成"有障碍"导致出生位失败，也不会把嵌墙判成安全）。

---

## 9. 设计决策与理由（便于复核，不做事后合理化）

| # | 决策 | 理由 |
|---|---|---|
| D1 | manifest 类的 Unity 依赖面**只有 `JsonUtility`**；`Resources.Load` 放在 `PMUnityBattleMap` | 让"纯规则测试"只需桩 JsonUtility 一个类型（任务硬要求），同时把"资源加载"集中在地图加载器一处；`TryParseJson` 仍由 manifest 类负责强校验 |
| D2 | `TryParseJson` = 解析 **+ 强校验** | 返回一个未通过校验的 manifest 只会把失败推迟到更难定位的地方 |
| D3 | 地图用**独立实现**的隔离场景创建/释放（而非调用 `PMR3TestScene.BuildIsolated`） | `PMUnity/**` 依赖 `Server/Boot/**` 会造成分层倒置，并让编译门禁被迫拖入整套宿主依赖；释放逻辑与 `ReleaseIsolatedScene` **语义一致**（同样只做 `UnloadSceneAsync`），差异仅在"不引用 Server 层" |
| D4 | 地图组件用**白名单 + 点名黑名单**（含 `Light`）而不是"只查 MonoBehaviour" | 契约要求"非允许组件需明确报告或失败而非静默残留"；只查 MonoBehaviour 会放过 Light/AudioListener/Collider 等 |
| D5 | 出生位失败**全部**显式失败 | 契约原文"失败不悄悄换假地面"；任何"看起来合理"的常量兜底都会在实机上变成"偶尔掉进地底/卡在墙里" |
| D6 | 出生朝向固定 0° | 与现有 DS 一致，且模型前向轴未实机确认；不猜 |
| D7 | `Speed` 参数归一化为 0..1（水平速率 / `MaxSpeed`） | 旧链写入的是摇杆量级 0..1（`HYLDPlayerController` 写 `playerMoveMagnitude`），Animator 混合树按该量级建立；直接用 m/s 会让混合树饱和。**这是表现映射选择（非协议）**，已登记供 C3/美术确认 |
| D8 | 表现**不做**任何插值/平滑 | 契约"平滑不是第二仿真"；SP 插值已由驱动侧产出 |
| D9 | `GetSceneDigest` 是**运行时结构摘要**，不复用 `manifest.collisionDigest` 口径 | 编辑器内容指纹（模板源/mesh 引用/源 Player）运行期不可得；把它当"可重算"就是假装能防篡改 |
| D10 | 额外拒绝 `PMNetRole.None` | `None` 副本没有意义，静默建出一个"无主可见体"比抛异常更难排查；已作为接口约束登记（C3 必须传 AP/SP） |

---

## 10. 诚实边界（明确不宣称的东西）

1. **不宣称资源防篡改**：C2 只校验 manifest 的内部一致性（版本/固定值/路径白名单/摘要格式/派生值），
   **不重算** C1 的编辑器内容指纹；"两端同一份资源"由 C3 的显式会话契约（同构建 + digest 握手）守护。
2. **不宣称任意 Mesh 精确 MTD / 通用碰撞**：见 §8；首版行为是"保守早挡、绝不漏墙"。
3. **不宣称已做真实运行验证**：本轮**没有**运行 Unity、**没有**真实 Resources 加载、
   **没有**隔离物理世界与 PhysX 出生位证据（`Resources/PMNet/*` 尚不存在）。
4. **不宣称 C3/C4 完成**：C2 只是把运行适配的代码与门禁交出来；宿主接线（会话模式、同摘要握手、
   就绪前地图加载、替换测试场景）属 C3，两客户端实机属 C4。
5. **不宣称 old 链已退役/双权威已消除**：本轮未触碰旧链任何文件，新旧并存状态与 R4-B 结束时相同。
6. **不宣称性能**：地图加载成本（白名单遍历 + 一次插入排序 ≤4096 + 一次 `SyncTransforms`）与
   出生位查询成本未实测；`SpawnQueryCount` 可用于后续量化。

---

## 11. 改动清单（严格限定在授权范围内）

| 文件 | 状态 | 说明 |
|---|---|---|
| `Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs` (+`.meta`) | 新增 | 505 行；GUID `fc67f8ccb09547a290a88b9e08385ec8` |
| `Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs` (+`.meta`) | 新增 | 1225 行；GUID `259f2dcee6a24c8589adcd56566720cf` |
| `Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs` (+`.meta`) | 新增 | 576 行；GUID `74dddfa0996940cda67c3a3702d8ba8a` |
| `Tools/PMBattleContentRuntimeCheck/PMBattleContentRuntimeCheck.csproj` | 新增 | 129 行；`netstandard2.0` + C#7.3 + 真实 Unity 2019 DLL |
| `Tools/PMBattleContentManifestTest/PMBattleContentManifestTest.csproj` | 新增 | 52 行；`net8.0` + C#7.3 |
| `Tools/PMBattleContentManifestTest/Program.cs` | 新增 | 798 行；90 项断言 + 文件尾部 JsonUtility 替身 |
| `Docs/plans/_r4c_runtime_report.md` | 新增 | 本报告 |

**未改动（边界声明）**：`PMUnityMoverPresentation.cs`（B2 测试胶囊）、`PMUnityMoverInput.cs`、
`PMUnityMoverCollisionQuery.cs`、`PMMover/**`、`PMPrediction/**`、`PMNet/**`、任何宿主
（`PMDsSessionHost` / `PMClientSessionHost` / `PMDsHost` / `PMNetBootstrap`）、任何 Editor 脚本、
任何旧链（`HYLD1.0/**`、`Server/Manger/**`）、任何资产/场景、主计划 `net-architecture-migration.md`
与 `net-r4c-content-contract.md`、C1/C3 的同伴文件。
工作区里 `.claude/**`、`openspec/**`、`.github/**` 等既有未提交删除**不是**本轮造成的（主计划 §"本机环境的坑"已记录）。

**编码纪律**：三个 `.cs` 与 `Program.cs`、两个 `.csproj`、本报告均按要求处理；
三个 `.cs` 为 **UTF-8 with BOM + CRLF**，`.meta` 沿用仓库既有格式（ASCII、LF、243 字节），
三个 GUID 经全仓 `.meta` 检索确认唯一（各 0 命中）。

---

## 12. 复现命令（本机已实跑）

```bat
REM ① C2 编译门禁（真实 Unity 2019.4 DLL，不桩 Unity API）
dotnet build Tools/PMBattleContentRuntimeCheck -c Release

REM ② manifest 纯规则门禁（net8 + 仅 JsonUtility 替身）
dotnet build Tools/PMBattleContentManifestTest -c Release
dotnet Tools/PMBattleContentManifestTest/bin/Release/net8.0/PMBattleContentManifestTest.dll

REM ③ 资源生成（**需要用户在 Unity 内点菜单，AI 不抢编辑器锁**）
REM    Unity 2019.4.8f1 打开 Client/ → 菜单 Build/Prepare PMNet Battle Content（C1）
REM    产出：Assets/Resources/PMNet/{BattleContentV1.json, BattleMapV1.prefab, PlayerVisualV1.prefab}

REM ④ 首次真实验证（C3 接线后 / C4 实机；本轮**未执行**）
REM    · Play 模式加载 → 期望 map.Describe() / DescribeSpawns() 全 6 槽位成功
REM    · 两个客户端 WASD/Space → 出生无嵌墙、障碍阻挡、退出清理
```

---

## 13. 交付给 C3 的"待接线 / 待决策"清单

| # | 项 | 类型 | 说明 |
|---|---|---|---|
| 1 | `MovementWorldVersion` 口径统一 | **必须** | 宿主常量 `1` / `PMR3Runtime.CollisionDigest` → `map.Manifest.worldVersion`；见 §4.1 |
| 2 | `SceneDigest` 的握手语义 | **必须（C3 定义）** | `map.GetSceneDigest()` 只用于"同构建两端同几何"；**不得**与 `contentDigest`/`collisionDigest` 互比，也不得对外宣称防篡改 |
| 3 | 就绪前置校验 | 建议 | 上报 `SceneReady` 之前调用 `map.TryValidateAllSpawns` + `ValidateManifestConsistency` |
| 4 | 显式会话模式 | 契约 C3 | 正式内容 vs `PMR3TestScene` 诊断必须由显式模式区分（不能两端各自猜资源是否存在、不能静默回退） |
| 5 | 测试场景白名单容量 | 注意 | 现宿主 `new Collider[2]` 只服务测试 floor+wall；接正式地图时应改用 `map.Colliders`（容量 4096 有界） |
| 6 | `Speed` 映射量级 | 待美术/C3 确认 | 见 D7（0..1 归一化）；若 Animator 实际需要 m/s，改 `ComputeSpeedParameterValue` 一处 |
| 7 | 出生朝向 / 是否 z 镜像 | 待 C4 实机确认 | 见 §5.3；改动点各一处且有常量 |
| 8 | `AnimatorCount > 1` 的处理 | 待确认 | 当前只使用第一个并暴露计数；若 C1 会产出多 Animator，需要 C3 明确用哪个 |
| 9 | 非 Box 近似面量化 | 后续批次 | 见 §8；用 `BoxColliderCount`/`NonBoxColliderCount`/`GetStats().NonBoxBoundsClassifications` |
| 10 | `uint` 字段的真实 JsonUtility 行为 | 待 C1/C3 确认 | 见 §7.2 与 §14：C1 的写盘后回读已构成写时门禁，但尚未执行过 |

---

## 14. C1 ↔ C2 接口对账（快照时间 2026-09-20 20:39，C1 当时仍在写盘）

C1（`Client/Assets/Editor/PMBattleContentBuild.cs`，2627 行）与本轮 C2 是并行写入组。
为避免「C2 只是一个互相不认识的旁支」，本轮对 C1 的 manifest 写出路径做了一次**只读**对账
（快照时刻 20:39；C1 的 `Tools/PMBattleContentBuildCheck/HostDependencies.cs` 当时正在被写入，
故本节只对应当前快照）：

| 对账项 | C1 侧实现（文件:行） | C2 侧 | 结论 |
|---|---|---|---|
| 冻结常量 | 直接引用 `PMBattleContentManifest.Expected*` / `MapResourceKey` / `PlayerResourceKey`（`PMBattleContentBuild.cs:1141-1144, 1917-1921`） | 同名常量 | **一致（单一来源）** |
| 摘要派生 | 调用 `PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(...)`（`:431-437`） | 同一函数 | **一致（派生规则只有一处实现）** |
| 写出 JSON | `JsonUtility.ToJson(manifest)` + UTF-8 **无 BOM** 写盘（`:1926-1934`） | `TryParseJson`（`JsonUtility.FromJson`） | 对称 |
| 写盘后回读 | `PMBattleContentManifest.TryParseJson(written.text, ...)` 必须通过（`:1949` 一带） | 同一入口 | **一致；它同时是「uint 字段真实 JsonUtility 往返」的写时自检** |
| 发布顺序 | 全部验证通过后才写 manifest（契约「失败不能留下 valid manifest」） | — | 一致 |
| 资源路径 | `Resources/PMNet/{BattleMapV1.prefab, PlayerVisualV1.prefab, BattleContentV1.json}`（`:160-162`） | 键白名单 `PMNet/*` 精确匹配 | 一致 |

**影响与仍然待确认的部分**：

1. §7.2 登记的三条「替身承担、实机未验」行为里，最关键的一条——**`uint collisionDigest` 能被真实
   JsonUtility 正确往返**——已被 C1 的「写盘后回读必须通过 `TryParseJson`」覆盖成一道门禁：
   C1 一旦在 Unity 里跑起来，uint 往返失败会立刻在 Editor 汇总里报错，而不是拖到 C3 才暴露。
2. 但该门禁**尚未执行过**（`Client/Assets/Resources/PMNet/` 仍不存在），因此 §13 第 10 项
   仍保留为「待确认」，只是风险等级从「可能拖到 C3 才发现」降为「C1 第一次跑菜单时就暴露」。
3. C1 仍在写盘；若其最终版本的常量来源/派生调用发生变化，请以 C1 自己的报告与主计划为准。
   本节只声明「快照时刻两侧接口一致」。

## 15. 未做（符合硬边界）

未运行 Unity / 未点 Editor 菜单 / 未抢编辑器锁 / 未生成任何资产 /
未编译 UE / 未 `svn`/`git` 写操作 / 未提交 / 未递归委派 /
未修改契约与主计划 / 未触碰 C1 与 C3 的文件 / 未改动旧链与任何宿主文件。
