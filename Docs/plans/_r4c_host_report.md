# R4-C / C3 宿主接线实施报告（消费 C1/C2 实际 API）

> 范围：实施 `Docs/plans/net-r4c-content-contract.md` 的 **C3 冻结接线**——两个宿主（DS `PMDsSessionHost` /
> 客户端 `PMClientSessionHost`）显式消费 C1/C2 的正式内容，且 Lobby（net8）改为从正式 manifest 取碰撞摘要。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本报告**不改**主计划与契约。
>
> 本轮**未启动 Unity、未执行 Editor 菜单、未生成/改写任何资产、未抢编辑器锁、未提交、未递归委派**。
> C1 的三个产物（`Resources/PMNet/{BattleContentV1.json, BattleMapV1.prefab, PlayerVisualV1.prefab}`）
> 在本机**仍不存在**，因此「正式内容的实机加载/两客户端」仍是 **PENDING_USER**。

---

## 0. 结论速览（摘要）

| 项 | 结论 |
|---|---|
| 显式会话模式 | ✅ `CollisionDigest` 是唯一来源：`0` 禁止；`0x52334201` = 诊断；其它非 0 = 正式内容（DS 与客户端同口径） |
| Lobby 正式摘要 | ✅ `Server.cs` 改读 `PMDsBattleContentConfig`（net8 + System.Text.Json 强校验），**缺失即拒绝拉局**，不回落诊断 |
| 正式 DS | ✅ 加载 C2 正式地图 + `ValidateManifestConsistency` + `TryValidateAllSpawns` + 名册槽位 + 英雄移速，全部通过后才 `SceneReady` |
| 正式名册 | ✅ 队伍 ≤2（TeamId 2→+X / 1→−X）、每队 ≤3、队内按 `PlayerId`→`Uid` 定槽；`HeroId` 合法且移速取自 `Shared/BattleNumericConfig` |
| 正式客户端 | ✅ 加载 C2 正式地图并核 `offer` 摘要；表现切 `PMUnityBattlePresentation`；诊断仍 `PMUnityMoverPresentation` |
| rig 统一 | ✅ 宿主内 private `IRigPresentation` 适配层：Apply/Dispose 只有一处调用点，**不改** C2 两组公开 API |
| `WorldVersion` | ✅ 同 manifest：正式 = `manifest.worldVersion`；诊断 = 1（旧口径） |
| Ready 摘要 | ✅ 上报本局**实际选定**的摘要（不再固定 `PMR3Runtime.CollisionDigest`） |
| 相机 | ✅ 正式模式临时关闭其它**已启用**相机（确保新角色可见），退局/失败按记录恢复；不改旧场景永久设置 |
| 失败清理 | ✅ 失败路径统一 `ReleaseSession`：表现→驱动→桥→地图/隔离场景→static 出口；**不按资源存在猜模式** |
| 编译 | ✅ 12 个工程 0 错误（含真实 Unity 2019.4 DLL 的 `PMR4UnityCheck`；三个 Unity 面门禁干净 obj **双跑全绿**） |
| 回归 | ✅ `PMR3UnitySmoke` 默认 **128/0 exit 0**、`PMR3IntegrationTest` 143/0、`PMR4IntegrationTest` 113/0、`PMDsLobbyTest` 268/0、`PMDsControlTest` 623/0 |
| 新门禁 | ✅ `Tools/PMBattleContentSessionTest`：**82 项 / 0 失败**（临时目录）+ **85 项 / 0 失败**（伪造仓库布局，覆盖默认路径 happy path） |
| **正式实机** | ❌ **PENDING_USER**：需要 (a) 用户在 Unity 里跑 C1 生成三个产物，(b) 重编 `HyldDS.exe`（现有 19:16 包是**旧代码**） |

---

## 1. 改动清单（严格限定在授权范围）

| 文件 | 状态 | 说明 |
|---|---|---|
| `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` | 修改 | 模式选择 + 正式地图/名册/出生位/移速 + Ready 摘要 + 释放；修掉两处既有缺陷（§8） |
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 修改 | 模式选择 + 正式地图/表现 + rig 适配 + 相机接管/恢复 + 状态摘要 |
| `Server/Server/Server.cs` | 修改 | `InitializeDedicatedServerLobby`：碰撞摘要改由 manifest 决定；缺失即拒绝拉局（NewChainEnabled 仍为 true → 匹配显式失败） |
| `Server/DS/PMDsBattleContentConfig.cs` | **新增** | net8（System.Text.Json）正式 manifest 只读配置 + 强校验 + 派生 + 路径解析 |
| `Tools/PMClientCheck/ClientStubs.cs` | 修改 | 补 `Renderer.sharedMaterials` / `SkinnedMeshRenderer.sharedMesh+bones` / `MeshFilter` / `LODGroup` / `AudioListener` / `TerrainCollider` / `Animator.applyRootMotion+parameters` / `AnimatorControllerParameter(Type)` / `Camera.allCameras` |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | 修改 | 同上 + `Object.Instantiate` / `GetComponentsInChildren` / `Rigidbody` / `Sphere|Capsule|MeshCollider` / `Material` / `Mesh` / `TextAsset` / `Resources` / `JsonUtility` / `Behaviour.enabled+isActiveAndEnabled` |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj` | 修改 | 增 `Client/Assets/Scripts/Shared/**`（DS 宿主真实依赖 `PMNet.Shared`） |
| `Tools/PMR4UnityCheck/PMR4UnityCheck.csproj` | 修改 | 增 `Shared/**` + 真实模块 `AnimationModule / JSONSerializeModule / AudioModule / TerrainPhysicsModule` |
| `Tools/PMBattleContentSessionTest/PMBattleContentSessionTest.csproj` | **新增** | net8 对照门禁（真实 config + 真实 C2 manifest 类，不虚构 Unity 运行） |
| `Tools/PMBattleContentSessionTest/Program.cs` | **新增** | 85 项断言（含 2 个 JsonUtility 解析边界替身，规则不替） |
| `Docs/plans/_r4c_host_report.md` | **新增** | 本报告 |

**未改动**：`PMDsLobbyHost.cs`（测试 options 默认不动）、`PMDsCoordinator.cs`、`PMDsControlProtocol.cs`、`PMR3Runtime.cs`、
C1/C2 的四个文件（`PMBattleContentBuild.cs` / `PMBattleContentManifest.cs` / `PMUnityBattleMap.cs` / `PMUnityBattlePresentation.cs`）、
`PMUnityMoverPresentation.cs`、`PMR4MovementDriver.cs`、任何资产/场景、任何旧链、主计划与契约。
编码：`.cs` = UTF-8 BOM + CRLF；`.csproj` 保持各自原有风格（`PMUnityGlueCheck` 为 BOM+CRLF，`PMR4UnityCheck` 为无 BOM+LF，新增 test csproj 为无 BOM+LF）。

---

## 2. 精确入口（C3 接线点）

### 2.1 Lobby（net8，`Server/Server/Server.cs` → `InitializeDedicatedServerLobby`）

```
HYLD_PMNET_DS=1
  → NewChainEnabled = true（不变）
  → IsLaunchConfigured 检查（不变）
  → PMR3Runtime.Register()；options.ProtocolHash = PMR3Runtime.ProtocolHash
  → ★ PMDsBattleContentConfig.TryLoad(out content, out error)
       失败 → 日志（含完整路径/字段原因）+ return  ⇒ 新链**不拉局**（匹配显式失败，不回退诊断/旧链）
  → options.CollisionDigest = content.CollisionDigest
  → 白名单/启动器/PMDsLobbyHost（不变）
  → 启动日志追加 worldVersion / contentDigest / manifest 绝对路径
```

`Server/DS/PMDsBattleContentConfig.cs` 的对外面（供 C3 与门禁消费）：

```csharp
public struct PMDsBattleContentInfo { ManifestPath; ContentDigest; CollisionDigest; WorldVersion; MapResource; PlayerResource; }

public static class PMDsBattleContentConfig
{
    public const string ManifestEnvironmentVariable = "HYLD_PMNET_CONTENT_MANIFEST";   // 绝对路径覆盖
    public const string DefaultRelativeManifestPath = "Client/Assets/Resources/PMNet/BattleContentV1.json";
    public const int    ExpectedFormatVersion = 1;      public const string ExpectedMapId = "hyld-map2-v1";
    public const int    ExpectedSeed = 1380205313;      public const string ExpectedMapResource = "PMNet/BattleMapV1";
    public const string ExpectedPlayerResource = "PMNet/PlayerVisualV1";
    public const int    ContentDigestHexLength = 64;    public const uint ReservedDiagnosticCollisionDigest = 0x52334201u;

    static bool TryResolveManifestPath(out string path, out string error);
    static bool TryLoad(out PMDsBattleContentInfo info, out string error);
    static bool TryParse(string json, string manifestPath, out PMDsBattleContentInfo info, out string error);
    static bool IsLowercaseSha256Hex(string value);
    static bool TryDeriveCollisionDigest(string contentDigest, out uint collisionDigest, out string error);
    static int  DeriveWorldVersion(uint collisionDigest);
}
```

强校验（与 C2 逐条一致）：`formatVersion=1` / `mapId` / `seed` / 两个资源键精确白名单 / `contentDigest` 小写 hex64 /
`collisionDigest` = 前 4 字节**小端** uint 派生（0→1）且 **≠0、≠保留值** / `worldVersion` = `(int)(digest & 0x7fffffff)`（0→1）且 >0。
未知字段忽略（与 JsonUtility 同口径）；`JsonDocument` 严格模式（拒绝注释/尾逗号）。

### 2.2 DS（Unity 侧，`PMDsSessionHost.Initialize`）

```
boot.CollisionDigest == 0                       → 失败（"0 禁止"）
boot.CollisionDigest == PMR3Runtime.CollisionDigest(0x52334201)
                                               → 诊断：PMR3TestScene.BuildIsolated + 地板/墙白名单 + WorldVersion=1
其它非 0                                        → 正式：
      PMUnityBattleMap.TryLoadManifest(out manifest)         // 缺资源 → 失败，不冒充正式
      manifest.collisionDigest != boot.CollisionDigest       → 失败（Lobby 与 DS 不同一份 manifest）
      PMUnityBattleMap.TryLoad(out map)                      // 隔离物理场景 + 组件白名单 + 查询
      map.ValidateManifestConsistency + map.TryValidateAllSpawns
      BuildFormalRoster（TeamId≤2 队 / ≤3 人 / PlayerId→Uid 定槽 / HeroId 合法 / MaxSpeed=BattleNumericConfig.Get(hero).MoveSpeed）
      BuildFormalSpawns（逐个 map.TryGetSpawn(teamIndex, slot) 并把 MaxSpeed 写进 SyncState）
→ 查询自检（隔离场景 / 非默认物理世界 / WorldVersion == 选定值 / 白名单非空）
→ ★ 到这里才 _sceneReady = true
→ OpenServer → new PMDsLobbyAgent(boot, host, port, BoundPort, ★_selectedCollisionDigest)
```

名册外的 uid 在正式模式下**拒绝**建立运动驱动（不允许用兜底出生位）。
`Dispose`：驱动 Freeze+Dispose → 表现/桥 → `PMUnityBattleMap.Dispose()`（正式）或 `ReleaseIsolatedScene`（诊断）。

### 2.3 客户端（`PMClientSessionHost.Enter`）

```
offer.CollisionDigest == 0                      → Fail（拒绝入局，不回旧链）
offer.CollisionDigest == PMR3Runtime.CollisionDigest
                                               → 诊断：PMR3TestScene + PMUnityMoverPresentation + WorldVersion=1
其它非 0                                        → 正式：PMUnityBattleMap.TryLoad → 核 manifest.collisionDigest == offer
                                                 → ValidateManifestConsistency + TryValidateAllSpawns
                                                 → 记录 SelectedWorldVersion = manifest.worldVersion
→ 关旧战斗 UDP socket → OpenClient
per-player: PMR4MovementDriver + IRigPresentation
             正式 → PMUnityBattlePresentation(label, role, map.Manifest, isOwner) 且 owner 建相机 + SuppressOtherCameras
             诊断 → PMUnityMoverPresentation(label, role, isOwner)
退局/失败 → ReleaseMovements（表现 Dispose → 驱动 Freeze/Dispose）→ RestoreSuppressedCameras → Bridge/static → 地图 Dispose
```

---

## 3. 冻结语义（本轮实现，供复核）

| 场景 | 行为 |
|---|---|
| digest = 0 | 两端 + Lobby 全部**拒绝**（此前 DS/客户端把 0 当"跳过校验"，属 fail-open；见 §8） |
| digest = 0x52334201 | **仅**诊断内容（PMR3TestScene + 默认移速），正式 manifest 撞该值被 Lobby/DS 拒绝 |
| 正式内容缺失/非法 | Lobby 不拉局；DS 失败；客户端失败 —— 任何一端都**不回退**诊断或旧链 |
| `WorldVersion` | 正式 = `manifest.worldVersion`；诊断 = 1。查询、快照 Aux、`ValidateManifestConsistency` 三者一致 |
| 出生点 | 正式 = `PMUnityBattleMap.TryGetSpawn`（真实地面查询 + 半高 + 胶囊重叠拒障）；诊断 = 旧 x=±3 / z=名册行居中 |
| 移速 | 正式 = 英雄表 `MoveSpeed`（`PMHeroId` 越界即失败）；诊断 = `PMMoverDefaults.DefaultMaxSpeed`（3.9） |
| Ready | `SceneReady` 只在"地图 + 碰撞 + 全部 spawn"通过后为 true；`Ready.CollisionDigest` = 选定值（`PMDsCoordinator` 侧仍校验 == 分配值） |
| 相机 | 仅正式模式、仅本地 owner 建相机；创建后把其它**已启用**相机 `enabled=false` 并记录，退局恢复（不改旧场景永久设置） |
| 输入 | 首批仅键盘 WASD/Space（`PMUnityMoverInput`）；**攻击未接入**；不启动旧 `BattleManger`/`TouchLogic`/旧场景 |

---

## 4. 验证证据（本机实际执行）

所有构建一律把 `obj/bin` 重定向到系统临时目录（`-p:BaseIntermediateOutputPath` / `-p:BaseOutputPath`），
除 `Server.csproj`（其 `obj/` 内有历史生成物，重定向会触发 CS0579 重复特性）——该工程的 `bin/obj` 均在 `.gitignore` 内。

**独立输出日志**：每一项编译/运行都有各自的日志文件，全部写在仓库**外**的临时目录
`%TEMP%\pmr4c\logs\`（`final2-<工程>.log` 编译日志、`final2-run-<套件>.log` 运行日志；
三个 Unity 面门禁的干净 obj **双跑**日志为 `f1-<门禁>.log` / `f2-<门禁>.log`；
`PMBattleContentSessionTest` 另有 `final2-run-session-fakerepo.log`）。仓库内未生成任何日志文件。

### 4.1 编译（全部 0 错误 0 警告）

```bat
REM Lobby（net8）
dotnet build Server\Server.csproj -c Debug                              → exit 0，0 错误
REM 统一客户端面 / Unity 胶水面 / 真实 Unity 2019.4 编译面（干净 obj 各跑 2 遍，稳定全绿）
dotnet build Tools\PMClientCheck      -c Release -p:BaseIntermediateOutputPath=...   → exit 0，0 错误（pass1/pass2）
dotnet build Tools\PMUnityGlueCheck   -c Release -p:BaseIntermediateOutputPath=...   → exit 0，0 错误（pass1/pass2）
dotnet build Tools\PMR4UnityCheck     -c Release -p:BaseIntermediateOutputPath=...   → exit 0，0 错误（pass1/pass2；真实 D:\Unity\2019.4.8f1 DLL）
REM 其余回归/门禁工程
PMR3IntegrationTest / PMR4IntegrationTest / PMDsLobbyTest / PMDsControlTest /
PMR3UnitySmoke / PMBattleContentRuntimeCheck / PMBattleContentManifestTest /
PMBattleContentSessionTest                                          → 全部 exit 0，0 错误 0 警告
```

### 4.2 运行（全部 exit 0）

| 套件 | 结果 | 备注 |
|---|---|---|
| `Tools/PMBattleContentSessionTest`（新增） | **82 项 / 0 失败**（临时目录） | 见 §5 |
| 同上（伪造仓库布局 `…/fakerepo/{Server,Client}`） | **85 项 / 0 失败** | 覆盖"默认路径推导 + 产物存在 → TryLoad 成功" |
| `Tools/PMBattleContentManifestTest`（C2 自带，未改） | **90 项 / 0 失败** | C2 口径未漂移 |
| `Tools/PMR3IntegrationTest` | **143 项 / 0 失败** | 含 `Server/DS/**` glob（新配置已编入） |
| `Tools/PMR4IntegrationTest` | **113 项 / 0 失败** | R4-B 运动链回归 |
| `Tools/PMDsLobbyTest` | **268 项 / 0 失败** | Lobby 宿主（options 默认未动） |
| `Tools/PMDsControlTest` | **623 项 / 0 失败** | 控制通道 |
| `Tools/PMR3UnitySmoke`（默认，无参数） | **128 项 / 0 失败，exit 0** | 用**用户 19:16 的 HyldDS 包**（旧代码）→ 只证明**诊断路径**未被破坏；artifacts 落临时目录 |

### 4.3 源码口径交叉校验（`ffgrep`/`grep` 只读）

| 检查 | 结果 |
|---|---|
| `PMR3Runtime.cs:42` | `public const uint CollisionDigest = 0x52334201u;` ⇒ 与 `PMDsBattleContentConfig.ReservedDiagnosticCollisionDigest` 同值 |
| `Server.cs` 旧固定赋值 | **0 处**（`options.CollisionDigest = PMNet.R3.PMR3Runtime.CollisionDigest` 已删除） |
| `Server.cs` 新入口 | `PMDsBattleContentConfig.TryLoad` → `options.CollisionDigest = content.CollisionDigest` |
| `Server/DS/**` glob 的工程 | `PMR3IntegrationTest`/`PMR3UnitySmoke` —— 均 **net8**（System.Text.Json 在框架内）；`PMClientCheck` 命中的是 `Client/Assets/Scripts/Server/**`（假阳性）；`PMDsLobbyTest`/`PMDsControlTest` 显式列文件 ⇒ **无 netstandard 工程被迫引入 System.Text.Json** |

---

## 5. `Tools/PMBattleContentSessionTest` 覆盖点（新门禁）

| 节 | 内容 |
|---|---|
| A 常量/派生 | 保留值 = 0x52334201；`"1f2e3d4c…"` → **1279077919**（小端）；`"01…"` → 1（大端会是 0x01000000）；全 0 → 1；`worldVersion(0)=1`、`(0x80000000)=1`、`(0xffffffff)=0x7fffffff`；大写/63/65/非 hex/null/空串拒绝 |
| B 有效 manifest | 字段回读 + 未知字段忽略（与 JsonUtility 同口径） |
| C **对照门禁** | 同一 JSON 两侧同判：有效（且 digest/worldVersion 相同）、零 digest（两侧拒）、**保留值（net8 拒 / C2 接受——保留值规则属 C3）**、手写/旧 digest（两侧拒）、worldVersion 不一致、formatVersion=2、mapId=map4、seed=0/2、资源键互换、大写、63 位、缺字段、根为数组、空对象、非法 JSON、空串 |
| D 路径口径 | 未设 env：能定位仓库根 → 默认路径 = 根 + `Client/Assets/Resources/PMNet/BattleContentV1.json`；定位不到 → 显式失败并说明原因。产物在 → `TryLoad` 成功；不在 → 失败原因含**完整路径**与"不回退诊断内容" |
| E 部署路径 | `HYLD_PMNET_CONTENT_MANIFEST` 绝对路径原样接受 + `TryLoad` 成功；相对路径拒绝；文件不存在失败（含路径）；旧 digest 的 manifest 被拒 |
| 替身边界 | 只替 `UnityEngine.JsonUtility.FromJson/ToJson`（System.Text.Json + 反射按 public 字段赋值），**规则一行都不替** |

---

## 6. 部署（本次改动带来的运维变化）

```
① 正式内容产物（由 C1 在 Unity 里生成，本轮未生成）
   Client/Assets/Resources/PMNet/BattleContentV1.json
   Client/Assets/Resources/PMNet/BattleMapV1.prefab
   Client/Assets/Resources/PMNet/PlayerVisualV1.prefab

② Lobby（Server.exe）读同一份 manifest：
   默认：<仓库根>/Client/Assets/Resources/PMNet/BattleContentV1.json（从 exe 目录向上找含 Server+Client 的那一层）
   覆盖：set HYLD_PMNET_CONTENT_MANIFEST=<绝对路径>    ← 必须是绝对路径
   缺失/非法：日志给出完整路径与字段原因，**不拉局**（匹配入口显式失败，不回退旧链/诊断）

③ DS：正式模式从**打包内的 Resources** 读同一份 manifest（`PMUnityBattleMap.TryLoadManifest`），
   因此必须在 C1 生成产物之后**重编 HyldDS.exe**；现有 19:16 包是旧代码（不含本轮 C3 逻辑）。

④ 客户端：用 offer 里的 digest 选择模式；正式模式加载打包内 Resources；失败即失败（不回旧链）。
```

---

## 7. 未验证（PENDING_USER，明确不宣称）

1. **正式内容实机加载**：`Resources/PMNet/*` 尚不存在 ⇒ 正式 DS/客户端的 `PMUnityBattleMap.TryLoad`、
   `TryGetSpawn` 真实 PhysX 行为、`PMUnityBattlePresentation`（Animator `Speed`、相机接管）**均未在 Unity 内执行**。
2. **正式两客户端实机**（C4）：出生位/障碍/移动/退出未验。
3. **本轮新 DS 代码的运行期行为**：`PMR3UnitySmoke` 用的是用户 19:16 旧包 ⇒ 它证明的是"诊断路径与 Lobby 编排未被破坏"，
   **不是**新代码跑通。新代码目前只有"真实 Unity 2019.4 API 编译通过 + 纯 C# 规则门禁"两类证据。
4. **`PMR3UnitySmoke` 的 `--movement`/`--hold-seconds`** 未重跑（默认模式已覆盖诊断路径；两者与 C3 无交集）。
5. **`CollisionDigest` 端到端握手**：正式 Lobby → DS → 客户端三方一致只在代码层保证（DS 侧比较 + Coordinator 比较 + 客户端比较），
   未在真机跑过。
6. **非 Box 碰撞近似面**：沿用 C2 报告的限制（保守早挡、绝不漏墙）；正式地图的 `BoxColliderCount/NonBoxColliderCount` 需 C1 产物后实测。
7. `PMHandshake.cs:1528` 的握手层仍把 `CollisionDigest == 0` 当"跳过校验"（**不在本轮写入边界**）；
   C3 已使 0 无法从 Lobby/DS/客户端路径产生，且 `PMDsControlProtocol` 的 Bootstrap/Ready 都硬拒 0 ⇒ 属剩余纵深防御点，建议主侧登记。

---

## 8. 本轮修掉的既有缺陷（都在授权文件内）

| # | 位置 | 事实 | 处置 |
|---|---|---|---|
| D1 | `PMDsSessionHost`（R4-B） | `_sceneReady = true` 在**碰撞查询校验之前**执行 ⇒ 一旦查询构造/隔离校验失败，"场景已就绪"已被置位（虽然当时 `_lobby` 尚未创建，属潜在 fail-open） | 改为所有地图/碰撞/spawn 校验通过后才置位（C3 契约"就绪前必须校验"） |
| D2 | `PMDsSessionHost.Initialize` | `if (boot.CollisionDigest != 0u && ...)` ⇒ **0 被当作"任意摘要"**（静默走诊断布局） | 0 现在**显式拒绝**（"0 禁止"） |
| D3 | `PMClientSessionHost.Enter` | 同上，`offer.CollisionDigest == 0` 时跳过摘要校验 | 0 现在显式拒绝 |
| D4 | `PMDsSessionHost` Ready | 固定上报 `PMR3Runtime.CollisionDigest`（即使跑的是别的布局也会报错摘要） | 上报本局实际选定摘要 |
| D5 | `PMDsSessionHost` Dispose | 只有 `ReleaseIsolatedScene` 一条释放路径 | 正式地图走 `PMUnityBattleMap.Dispose()`（自带隔离场景卸载） |

### 送给主侧的观察（只读发现，未改）

| # | 观察 | 影响 |
|---|---|---|
| O1 | `Client/Assets/Resources/PMNet/` 仍不存在（C1 未执行） | 正式内容链当前**必然失败**（这是设计：失败关闭），需用户跑 C1 菜单 |
| O2 | `PMUnityBattleMap.TryGetSpawn` 产出的 `SyncState.MaxSpeed` 是 `PMMoverDefaults` 默认值（3.9） | 不是缺陷（C2 声明"由权威快照写入"），但**每个消费方都必须自己写 MaxSpeed**；C3 已在 DS 侧按英雄表覆写 |
| O3 | **桩件门禁（`PMClientCheck` / `PMUnityGlueCheck`）会给出"不完整但全绿"的结果** | 实测：同一份源码，修掉上一层声明错误后才逐层暴露后续错误（Animator → Mesh → SphereCollider/Rigidbody/Instantiate → Material/sharedMaterials）。一次绿不能当作"编译面完整"；本轮已改为**干净 obj 连跑两遍**并补全桩件。建议主侧把"双跑"写进门禁惯例 |
| O4 | `PMR3UnitySmoke` 无参数默认跑出的检查数会随其自身改动变化（本轮 128，与 `_r4b_real_unity_movement.md` 一致） | 仅记录，未改该工具 |
| O5 | `PMBattleContentManifestTest` 本轮实跑 **90 项**（与 C2 报告一致） | C2 口径未漂移 |

---

## 9. 未做（符合硬边界）

未启动 Unity / 未点 Editor 菜单 / 未生成或改写任何资产 / 未抢编辑器锁 /
未改 C1/C2 的任何文件（`PMBattleContentBuild.cs`、`PMBattleContentManifest.cs`、`PMUnityBattleMap.cs`、`PMUnityBattlePresentation.cs` 一字未动）/
未改 `PMDsLobbyHost.cs` 与 `PMDsControlProtocol.cs` / 未改任何旧链与场景 /
未 `svn`/`git` 写操作 / 未提交 / 未递归委派 / 未改主计划与契约。
运行期产物（`bin/obj`、冒烟 artifacts）全部落在系统临时目录，仓库内未新增非源码文件。
