# 旧战斗链资产解挂执行报告（D 段：仅解挂，不删 class）

日期：2026-09-21
执行方式：Pi CLI Delegation（comprehensive 后端，本地直接执行，未递归委派）
范围文档：根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Docs/plans/net-legacy-retirement-contract.md`（D 段）→ `Docs/plans/_legacy_asset_boundary_survey.md`（GUID 证据）→ `skills/windows-shell-compat/SKILL.md`
任务性质：**仅 4 个 Unity 文本资产的最小解挂**（移除 `.cs`/`.meta` 不在本任务范围，由其它组负责；本任务不依赖它们仍存在）

---

## 摘要（≤1300 字）

按 D 段契约对 4 个 Unity 文本资产做**纯删除**：移除指向两个冻结 GUID 的 MonoBehaviour 文档及宿主 GameObject 对应 `m_Component` 项，其余全文逐字不变。冻结 GUID 写前经 `.cs.meta` 复核：BattleManger=`7200a0eb9673b6e4f8cb8386cdde31db`、HYLDCameraManger=`eec213bc5141674488046b737aede2bf`。

删除明细（均在 Client/Assets）：`Scenes/HYLDGameTest.unity` 删 `&413438274`（宿主 `413438266`）19 行；`HYLD1.0/Scenses/HYLDTryGame.unity` 删 `&413438271`（宿主 `413438266`）14 行；`HYLD1.0/Scenses/HYLDGame.unity` 删 `&413438271`（宿主 `413438266`）15 行；`HYLD1.0/Resources/Main Camera.prefab` 删 `&5705895439509941000`（宿主 `5705895439509941005`）14 行。

写前逐文档校验 id→class→script→gameobject 链及组件项归属全部通过，锚点无其它引用（§4.1）。写后与备份逐字节比对全部 PASS：新增行 0、删除集合恰为组件项+目标文档块、「原字节去掉被删行」与现文件逐字节相等、非目标文档逐字一致、UTF-8 无 BOM/全 CRLF 不变、宿主其余组件完好且未新增 `m_Script: {fileID: 0}`（§4.2）。

完备性扫描（`rg -F --no-ignore`，全 Client/Assets 的 `*.unity`/`*.prefab`）：两 GUID 命中 0，仓库内任一 `*.unity`/`*.prefab` 亦 0。

禁改项前后 SHA256 相同：`Scenes/HYLDGame.unity`、`Remake/Player.prefab`、`Resources/PMNet` 三产物、`EditorBuildSettings.asset`。未改 BuildSettings、材质、第三方、其它 Source、任何 `.cs.meta`；未启动 Editor、未写 UE 资产、未提交、未递归委派。

残留 1 处 `fileID: 413438274` 在 HYLD1.0/Scenses/HYLDGame.unity，属该文件自身另一组件（script guid `91f8c6588526dd146a4d7f802dd8cc58`）；本地 fileID 为文件级作用域，非被删文档引用，按异文件引用原则保留。

静态校验边界：仅文本/结构级，**未启动 Unity，不得声称原生加载已验**（实机读取与 missing script 计数仍 PENDING_USER）。

---

## 1. 边界与授权

硬写入边界（仅这 5 个路径）：

- `D:\UGit\hyld-master\Client\Assets\Scenes\HYLDGameTest.unity`
- `D:\UGit\hyld-master\Client\Assets\HYLD1.0\Scenses\HYLDTryGame.unity`
- `D:\UGit\hyld-master\Client\Assets\HYLD1.0\Scenses\HYLDGame.unity`
- `D:\UGit\hyld-master\Client\Assets\HYLD1.0\Resources\Main Camera.prefab`
- `D:\UGit\hyld-master\Docs\plans\_legacy_asset_detach_report.md`（本报告）

明确**不得触碰**：`Client/Assets/Scenes/HYLDGame.unity`（正式烘焙输入）、`Client/Assets/Resources/Remake/Player.prefab`（正式烘焙输入）、`Client/Assets/Resources/PMNet/**`（烘焙输出）、`Client/ProjectSettings/EditorBuildSettings.asset`、材质、第三方目录、其它 Source、任何 `.cs` / `.cs.meta`。

注意区分同名文件：本任务允许的 `HYLDGame.unity` 位于 `Client/Assets/HYLD1.0/Scenses/`；被禁止的是 `Client/Assets/Scenes/HYLDGame.unity`。两者在本报告中以完整路径区分。

未做父目录/祖先目录/相邻文件推断；未创建或修改边界外任何文件（临时备份与校验脚本写在仓库外 `%TEMP%`）。

---

## 2. 冻结 GUID 的写前复核

写前直接读取 `.cs.meta` 原文（而非依赖调查文档转述）：

```
Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs.meta
  guid: 7200a0eb9673b6e4f8cb8386cdde31db
  sha256: 126db0e14122b8f98a926fe84438a720a75a27e95c44dcd7d1ba05c7f18f9369

Client/Assets/Scripts/Server/Manger/Battle/HYLDCameraManger.cs.meta
  guid: eec213bc5141674488046b737aede2bf
  sha256: a40ebc784f21ee6165c7bd4883f18f3ad87ea5a5dffea9ea0e645e3d51b63edb
```

写前全量扫描（`rg -F --no-ignore`）确认两个 GUID 在 `Client/Assets` 内的资产引用面：

```
BattleManger 7200a0eb..  -> 仅 Client/Assets/Scenes/HYLDGameTest.unity:393
HYLDCameraManger eec213bc.. -> HYLDTryGame.unity:638 / HYLDGame.unity:297 / "Main Camera.prefab":176
```

即：每个目标文件恰有 1 处引用，与 D 段契约一致。

额外确认 `Main Camera.prefab` 的 `.meta` guid 为 `2e33f0d42e081664a8134c9af4868274`，且**没有任何** `.unity`/`.prefab` 引用该 guid → 不存在「场景以 PrefabInstance 覆写该组件」的跨文件路径，无需处理 `m_Modifications`。

---

## 3. 逐文件操作明细

所有文件写前状态均为 **git-clean**（`git status --porcelain` 对这 4 个路径为空），并且备份文件的 `git hash-object` 与 `HEAD` blob 完全一致 → 「写前内容 = 已提交内容」已证明。

| 资产 | HEAD blob | 写前 SHA256 | 写后 SHA256 | 文档数 | 行数 |
|---|---|---|---|---|---|
| `Scenes/HYLDGameTest.unity` | `ffdd05dba1c2c42d5698b88ea4adad97aee3b889` | `3b433bcc7aa8515a2768988b24f81f7a195d2de180e7c4ed3247c01a86043163` | `5affed207c1ed893e410993b309cd9b53d00abdf46c1c5804af87d8a0faadc74` | 1606 → 1605 | 32017 → 31998 |
| `HYLD1.0/Scenses/HYLDTryGame.unity` | `54ff259d511b1509383f7b677bd5bf4b23128e9a` | `e0cd4968b700ed0e6d2434e0f243c5498fbb39950ec23a13dd42bc6f2538eb12` | `7d986c8f08891b42fcf0e3ab19023af3b9193c7c70bd8a47f7cb9cd6034e3082` | 354 → 353 | 7654 → 7640 |
| `HYLD1.0/Scenses/HYLDGame.unity` | `635644215aa6d3941a3899a962a63e050d5142e1` | `8a67583d34b6c11df0f374019de7264d5a77e585f977d9ef3271990a361860d7` | `017072ad6005c78a4ba6153c8e4d28713b3cc695dbe10301862d00c159e73f9d` | 13 → 12 | 340 → 325 |
| `HYLD1.0/Resources/Main Camera.prefab` | `08bb511739061daea54c7598909616b543776f9d` | `32f0ae08b421839e83a9559df788c64bbd0df559635261708d2946f318cb5ea9` | `99d483defa3b1552f0a67835b610c69b300bffb4d90d9761b85bd989224f879b` | 9 → 8 | 218 → 204 |

写后 `git status --porcelain`（这 4 个路径）恰为 4 条 ` M`，无新增/删除文件：

```
 M "Client/Assets/HYLD1.0/Resources/Main Camera.prefab"
 M Client/Assets/HYLD1.0/Scenses/HYLDGame.unity
 M Client/Assets/HYLD1.0/Scenses/HYLDTryGame.unity
 M Client/Assets/Scenes/HYLDGameTest.unity
```

### 3.1 `Scenes/HYLDGameTest.unity` — BattleManger

- 文档锚点：`--- !u!114 &413438274`（写前第 384..400 行）
- 宿主：`!u!1 &413438266`，`m_Name: Main Camera`，`m_Component` 写前 6 项 → 写后 5 项（保留 `413438269/413438268/413438267/413438270/413438273`）
- 移除的组件项：第 225 行 `  - component: {fileID: 413438274}`
- 移除的文档正文（18 行）：`m_Script` 指向冻结 guid；序列化字段 `ISNet / playerManger / cameraManger / _StartGameAni / id / opt`
- 文件内锚点 `413438274` 的其它引用：**无**（grep 仅命中组件项与文档头两处），故无需归零任何引用

### 3.2 `HYLD1.0/Scenses/HYLDTryGame.unity` — HYLDCameraManger

- 文档锚点：`--- !u!114 &413438271`（写前第 629..640 行）
- 宿主：`!u!1 &413438266`，`m_Name: Main Camera`，`m_Component` 7 项 → 6 项（保留 `413438269/413438268/413438267/413438270/413438272/413438273`）
- 移除的组件项：第 478 行 `  - component: {fileID: 413438271}`
- 文档正文：`moden: TryGame`（13 行）
- 锚点 `413438271` 其它引用：**无**

### 3.3 `HYLD1.0/Scenses/HYLDGame.unity` — HYLDCameraManger

- 文档锚点：`--- !u!114 &413438271`（写前第 288..300 行）
- 宿主：`!u!1 &413438266`，`m_Name: Main Camera`，`m_Component` 8 项 → 7 项（保留 `413438269/413438268/413438267/413438270/413438272/413438273/413438274`）
- 移除的组件项：第 136 行 `  - component: {fileID: 413438271}`
- 文档正文：`moden: BaoShiZhengBa` + `isTest: 0`（14 行）
- 注意：保留的 `413438274` 是**该文件自身的另一组件**（`m_Script` guid `91f8c6588526dd146a4d7f802dd8cc58`，第 311 行起），与 `HYLDGameTest.unity` 中被删除的 BattleManger 锚点**数值相同但文件作用域不同**，未做任何改动。

### 3.4 `HYLD1.0/Resources/Main Camera.prefab` — HYLDCameraManger

- 文档锚点：`--- !u!114 &5705895439509941000`（写前第 167..178 行）
- 宿主：`!u!1 &5705895439509941005`，`m_Name: Main Camera`，`m_Component` 8 项 → 7 项（保留 `5705895439509941002/…1003/…1004/…1001/…1111/…1110/…1109`）
- 移除的组件项：第 15 行 `  - component: {fileID: 5705895439509941000}`
- 文档正文：`moden: BaoShiZhengBa`（13 行）
- 锚点 `5705895439509941000` 其它引用：**无**（全仓库 `.unity`/`.prefab` 亦仅此一处，无 PrefabInstance 覆写引用）

---

## 4. 校验方法与结果

### 4.1 写前校验（逐文档，全部通过）

对每个目标文件执行并断言：

1. 冻结 GUID 在文件内**恰出现 1 次**；
2. 该行位于一个 `--- !u!(114) &<anchor>` 文档内（类 ID 必须是 114，即 MonoBehaviour）；
3. 该文档有 `m_GameObject: {fileID: <go>}`；
4. `<go>` 存在，且其文档类 ID 为 1（GameObject），`m_Name: Main Camera`；
5. `<go>` 的 `m_Component` 列表中**恰有 1 项**等于 `<anchor>`，且该项属于该 GameObject 文档；
6. 该 GameObject 的组件项总数 ≥ 2（拒绝误删最后一个组件，保证不会把宿主 GameObject 变成空白）；
7. 除「目标文档块」与「其自身组件项」外，文件内**无其它**指向 `<anchor>` 的序列化引用（若存在，应显式归零——实际为无）。

### 4.2 写后比对（独立脚本 `post_verify.py`，对备份逐字节比对）

对每个文件断言，**全部 PASS**：

- **字节级最小删除证明**：`现文件字节 == 原文件字节去掉被删行的字节区间`
- **行级最小删除证明**：`现文件行 == 原文件行按序去掉被删行`
- **diff 无新增行**（`additions = 0`；实测各文件删除 18 / 13 / 14 / 13 行 + 组件项 1 行）
- **冻结 GUID 在现文件中不存在**
- **编码/EOL 不变**：UTF-8 无 BOM、全 CRLF（bare LF = 0）、文件尾仍为 CRLF、CRLF 总数恰好减少被删行数
- **宿主 GameObject 完好**：`m_Name: Main Camera` 保留、其余组件项保留、目标组件项消失
- **被删区间确为「目标组件项 + 目标 MonoBehaviour 文档」**（正文含 `MonoBehaviour:` 与该冻结 guid）

补充：4 文件的 YAML 头部（`%YAML 1.1` / `%TAG`）与文件结尾 2 行与备份**逐字一致**。

### 4.3 静态 YAML 结构校验（新增脚本 `verify_static.py`）

对 4 个写后文件：

- 文档锚点**无重复**
- 每个 GameObject 的 `m_Component` 项**均能解析**到本文件内存在的文档（`dangling m_Component -> none`）
- 每个 Transform / MonoBehaviour 的 `m_GameObject` 均能解析到本文件内的 `!u!1` 文档（唯一的 `m_GameObject: {fileID: 0}` 命中是 `HYLDTryGame.unity` 第 2193 行 `--- !u!114 &1723729128 stripped`，即 Unity 标准的 PrefabInstance `stripped` 占位文档，**写前备份中同样存在**，与本任务无关）
- Main Camera 宿主 GameObject 的写后组件列表已在上表列出

### 4.4 删除完备性扫描

```
rg -F --no-ignore "7200a0eb9673b6e4f8cb8386cdde31db" Client/Assets --glob '*.unity' --glob '*.prefab'   -> 0
rg -F --no-ignore "eec213bc5141674488046b737aede2bf" Client/Assets --glob '*.unity' --glob '*.prefab'   -> 0
rg -F --no-ignore -e <两个 guid>  .  -g '*.unity' -g '*.prefab'                                          -> 0
```

`m_Script: {fileID: 0}` 计数（missing script 标记）在这 4 个文件中均为 **0**。

**使用 `rg --no-ignore`（而非 ffgrep/FF 索引）的原因**：FF 内容索引是 partial 语义（冷启动等待、时间预算、gitignored 路径不进索引），且刚被外部进程改写的文件 watcher 可能滞后；本步骤要求的是「全 `Client/Assets` 的 `*.unity`/`*.prefab` 中归零」的完备性判定，必须用真 `rg` 实时全量扫描兜底。资产**路径**检索仍以 `fffind` 为准；本次不涉及 UE 资产语义读取，故未调用任何 UE MCP。

唯一残留命中：`HYLD1.0/Scenses/HYLDGame.unity:138` 的 `fileID: 413438274`，属该文件自身另一组件（见 3.3）。**不是**被删文档的引用。

### 4.5 禁改项证明（前后 SHA256 相同）

| 禁改项 | SHA256（写前 = 写后） |
|---|---|
| `Client/Assets/Scenes/HYLDGame.unity` | `72e708d51613804fc16eb21585ec6e625254833699dbb08ff80b160af4953bb4` |
| `Client/Assets/Resources/Remake/Player.prefab` | `6ba5cb30be52d7b759fe39311363803e16b13ae513d2c52be022ffc25c24dc54` |
| `Client/Assets/Resources/PMNet/BattleContentV1.json` | `898cbe51fddf10bbe0c9d782912fde93d263b67c2b2327ce1f0adc4e8f7e339a` |
| `Client/Assets/Resources/PMNet/BattleMapV1.prefab` | `dabe768d6e40fa5a7180dae342526080f16299b4eaf771a8d255f149b7c1c297` |
| `Client/Assets/Resources/PMNet/PlayerVisualV1.prefab` | `dc5c9db2dbc2f1b4b69cf4fe2908f85f43159e4b3d027044332981bad91b7ed5` |
| `Client/ProjectSettings/EditorBuildSettings.asset` | `0f49797cfd99d5ec3c2574aaa3d5218b222100fba26ea3194b2ab2033165e7bd` |

`BuildSettings` 未改；材质、第三方目录、其它 Source、任何 `.cs.meta` 未被本任务写操作触及（本任务的写操作只包含上表 4 个资产 + 本报告）。

---

## 5. 并发观察（重要，供主 Agent 对账）

执行期间另一个任务组正在**并行**删除旧类源码，`git status` 的工作区变更数从 125 增至 131；`-10 分钟` 内被改动的文件包括 `Server/Server/Client.cs`、`Server/Server/Server.cs`、`Server/Server/ServerConfig.cs`、`Tools/PMDsHostCheck/PMDsHostCheck.csproj`、`Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj`、`Client/Assets/Editor/PMDsBuild.cs`、`Client/Assets/Scripts/Server/Boot/PMDsHost.cs` 与两份 plans 文档——**均非本任务写入**。

本任务写入的 4 个资产共享同一写入时间戳（22:29:56），彼此一致，且写入后哈希未再变化。

并发期间另观察到：`Client/Assets/Scripts/Server/Manger/Battle/` 目录（含 `BattleManger.cs(.meta)`、`HYLDCameraManger.cs(.meta)`）在本任务执行窗口内被该组**整体删除**（执行中途 `ls` 还可见，随后即消失，最终目录为空）。这与「另一组会删 cs/meta，不能依赖它们仍存在」的提示一致；本任务据此只做**资产侧解挂**，不读写 `.cs`/`.cs.meta`，两条工作流最终一致：类删除 + 资产解挂 = 无 missing script。

---

## 6. 备份

仓库外临时目录（未纳入版本控制）：

```
C:\Users\luomingcong\AppData\Local\Temp\hyld-detach-backup\20260921-222956\
  Client\Assets\Scenes\HYLDGameTest.unity
  Client\Assets\HYLD1.0\Scenses\HYLDTryGame.unity
  Client\Assets\HYLD1.0\Scenses\HYLDGame.unity
  Client\Assets\HYLD1.0\Resources\Main Camera.prefab
```

备份 SHA256 已逐一复核等于写前哈希；`git hash-object` 亦等于 `HEAD` blob。校验脚本同样位于仓库外：`C:\Users\luomingcong\AppData\Local\Temp\hyld-detach\{analyze,edit,verify_static,post_verify}.py`。

---

## 7. 未验证项 / 边界声明

1. **静态 YAML ≠ 原生加载已验**：本报告只做文本与结构级校验。**未启动 Unity Editor**，因此「Unity 原生 YAML 反序列化成功」「缺失脚本计数为 0」「场景/prefab 打开无 Console 报错」均**未实测**，状态为 PENDING_USER。按 D 段验收口径（T-L4），Unity 实机核对必须由后续实机环节完成，本报告不得据静态分析宣称实机通过。
2. 未运行烘焙（`Build/Prepare PMNet Battle Content`）；未验证 `Resources/PMNet` 三产物的 `contentDigest` 是否仍与源指纹一致（需编辑器，超出边界）。但三产物哈希前后不变，且其中不含任何工程脚本 GUID（`.cs.meta` 交集为空，见 `_legacy_asset_boundary_survey.md` §1.3）。
3. 未执行 `svn`/`git` 写操作、未提交、未推送、未回滚。
4. 未递归委派（本进程未调用任何 `cli_delegate*`）。
5. 本任务不覆盖 D 段之外的 A/B/C/E 段（源码删除、proto 收缩）。因并行组已清空 `Battle/` 目录，编译级归零需在 A/B/C 段完成后由相应任务统一验证。
