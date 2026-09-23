# hyld-master 客户端入口（旧战斗网络已退役）

## 1. 当前架构与边界

- Unity **2019.4.8f1**，C# **7.3** / .NET Standard **2.0**，不要用 Unity 6 打开工程。
- 工作副本 `D:/UGit/hyld-master`，不是 UE ProjectMecury；不编译 UE、不启动第二个 Unity、不抢工程锁、不代提交。
- 唯一状态、决策与验收源：`Docs/plans/net-architecture-migration.md`。用户要求代码优先、实机集中后置；自动编译/测试通过不代表真实双客户端通过。
- 大厅 TCP 常驻；局内只连接 **Unity 打包的 HyldDS**，每局一进程。大厅不模拟战斗。
- 旧 BattleManger/BattleData/SavedMove/CommandManger/UDPSocketManger/BattleFrameHud 已删除。旧 UDP 7777、旧 BattleInfo/state_mask/MoveAck/回放大包协议与旧匹配 fallback 已删除，不得重新引入空壳来“兼容”。
- 单机试玩的旧 manager 自动启动也已停用。`HYLD1.0` 中仍保留场景/美术源所挂载的数据或表现脚本，不等于旧网络还活着；不要因目录名含 OldScripts 就批量删除资产或脚本。

## 2. 必读文档路由

| 任务 | 文档 |
|---|---|
| 网络/生命周期/声明复制 | `Docs/plans/net-r0-contract.md`、`net-r2-codegen-contract.md` |
| Lobby/DS认证、结果与回收 | `Docs/plans/net-r3-control-contract.md` |
| Mover/预测 | `Docs/plans/net-r4-prediction-contract.md`、`net-r4-network-contract.md` |
| 地图与角色资源 | `Docs/plans/net-r4c-content-contract.md` |
| 投射物 | `Docs/plans/net-r5-projectile-contract.md`、`net-r5-network-contract.md` |
| 资源/伤害/死亡/胜负 | `Docs/plans/net-r6-combat-contract.md` |
| 删除旧代码/资产挂载 | `Docs/plans/net-legacy-retirement-contract.md` |
| 双端接口 | `BothSide.md`、`Client/Assets/Docs/ForServer.md`、`Server/AGENTS.md` |

历史调查、早期假说与被替代的接口不能覆盖最新契约和主计划末尾结论。

## 3. 进程身份与入口

`PMNetBootstrap` 在 `SubsystemRegistration` 解析 `PMNetLaunchOptions` 并初始化 `PMNetRuntime`，在 `AfterSceneLoad` 建宿主。

- **不带 `-server` 就是客户端**；业务身份只用 `PMNetRuntime` / 对象 `PMNetRole`，不要用 Editor/场景名推断。
- DS 必须有合法 `-bootstrap`，由 Lobby 编排拉起。`PMDsHost` 只包装 `PMDsSessionHost`，缺 bootstrap/启动异常/帧驱动异常以非零退出；不绑定旧裸 UDP 路由。
- `PMUdpRouter/PMSingleBattleRegistry` 和旧 PMDsProbe/PMUdpRouterCheck/Test 已删除。`-server-smoke` 仍走 **新 bootstrap 会话** 的 Probe/结果链，不能与旧探针混淆。
- `UIMatchingPanel` 只接受 `PMDS1:` 通知，解析后进 `PMClientSessionHost.Enter`；非 PMDS1 不再加载旧战场。
- 大厅 `HYLDManger` / `TCPSocketManger` / `RequestManger` 仍保留。TCP 请求以 `request_id` 关联，只有白名单幂等请求可重试。

## 4. 当前局内主链

| 模块 | 位置与职责 |
|---|---|
| 对象/传输/声明/复制 | `Scripts/PMNet/`，纯 C#；预留 NetId 必须用绑定 World 的能力令牌，Pending 不发 Create |
| 声明与单一生成集合 | `Scripts/PMR3/PMR3Player.cs`、`PMR5Projectile.cs`；生成物在同目录 `Generated/` |
| 运动预测 | `Scripts/PMPrediction/`、`PMMover/`、`PMR3/PMR4MovementDriver.cs` |
| 投射物 | `Scripts/PMProjectile/` 与 `PMR3/PMR5ProjectileDriver.cs`：假弹/接管/拒绝/历史验证/墓碑 |
| 战斗真值 | `Scripts/PMCombat/` 与 `PMR3/PMR6CombatDriver.cs`：攻击计划、资源、HP、死亡、结果 |
| Unity适配 | `Scripts/PMUnity/`：独立物理世界、查询、表现、只读 HUD |
| 会话宿主 | `Scripts/Server/Boot/PMClientSessionHost.cs` / `PMDsSessionHost.cs` |

位置 Y-up、单位米；输入帧与 AuthorityServer 帧不能相减猜偏移；TTL用单调墙钟。
DS 每帧从真实运动结果记录目标历史，先战斗授权，再投射物推进/命中，再发布状态；客户端只报候选，永不决定伤害。

## 5. 已实现与尚缺

- F 普攻、G 已支持的直线大招。17 个 normal 直线配置、5 个 super 直线配置；抛物线/无配置大招明确 Unsupported，不扣资源、不偷偷改直线。
- 双端共用 `BattleNumericConfig` / `PMCombatWeaponPlanner`。普通弹数=共享 PerShot，大招=共享 Total；不复活旧客户端多画弹幕的差异。
- DS权威 HP/死亡、Mana90、SuperEnergy200；资源 OwnerOnly，公共HP/角色/队伍/胜负复制。首杀结束沿旧服规则；断线不再固定队伍1赢。
- 可靠结果通知→客户端ACK或5s宽限→Lobby结果确认→DS退出。客户端仅凭验证保存的结果正常退场，不拿“收到过包”的计数当可信结果。
- `PMUnityCombatHud` 只读资源/拒绝理由/胜负；正常退场保留终局HUD至下一Enter/显式Stop，旧对象延迟OnDestroy不能关闭新会话。
- **仍待**：特殊技能/抛物线/AoE/弹射/道具、原英雄子弹美术、摇杆与完整UI、回放、R4完整Modifier/拒绝补偿/平台、真实双客户端/长局/性能。当前子弹球形表现不等于全部美术迁移；predictionMs首批固定100。

## 6. 资产与生成

- 正式源：`Assets/Scenes/HYLDGame.unity`、`Assets/Resources/Remake/Player.prefab`、`ScenseBuildLogic` 的 map2；不随代码清理删除。
- 产物：`Resources/PMNet/BattleMapV1.prefab`、`PlayerVisualV1.prefab`、`BattleContentV1.json`。菜单 `Build/Prepare PMNet Battle Content`；manifest 最后发布，缺失/非法拒绝入局。
- 资产编辑校验用内部 `activeSelf` 父链和控制器资产参数，不把未实例化 prefab 的 `activeInHierarchy/Animator.parameters` 当运行事实。
- 4份旧测试/试玩资产已移除 BattleManger/HYLDCameraManger 挂载；删其它脚本前仍必须查 `.meta` GUID 引用，不扩大修改原烘焙输入。
- 含中文源码 **UTF-8 BOM + CRLF**；新 `.cs` 配唯一 `.meta`（无BOM、LF）。

## 7. 协议与门禁

权威大厅 proto：`ProtobufAndNotepad/Protobuf/SocketProto.proto`（7 message/7 enum）；MainPack 13/15、Request7/8、Action31..41已 reserved，禁止重用。保留 `BattlePlayerPack` / `Hero` / `FightPattern` 是大厅匹配身份，不是旧战斗同步。

真实生成入口：`ProtobufAndNotepad/Protobuf/build.bat`；`--check-only` 校验3份protoc产物与PMNet产物。其它旧 proto 副本已删。
声明入口：`Tools/PMNetGen --decl-gen Client/Assets/Scripts/PMR3 --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json`；不要手改生成物或另造同名注册表。

常用自动门禁（**必须本次build成功后再run**）：
- `PMClientCheck` / `PMUnityGlueCheck`：全层替身编译；`PMR4UnityCheck`：真实Unity2019 DLL+两个宿主+HUD编译。
- `PMNetVerify`：大厅protobuf独立protoc字节oracle。
- `PMR4NetworkTest` / `PMR5NetworkTest` / `PMR6NetworkTest`：真实核心及Transport字节链，不等于Unity实机。
- `PMDsHostCheck`：真实wrapper+受控session替身；`PMLegacyRetirementTest`：旧类型/文件/协议/GUID禁回归。
- `check_client_authority_writes.py`：保留脚本的权威写入检查，已登记例外不等于已修复。

## 8. 协作与交付

修改跨端协议/时序必须同步 `BothSide.md` 与主计划；公共入口变化同步本文件及协作文档。禁止无范围 `git add .` / `git add -A`，不代提交。历史构建产物不随源码自动更新；集中联调前 Lobby/DS/Client 必须同版本重建，旧 HyldDS 包可能仍含已删除分支，不能用来验证当前源码。


## 9. RPC作者接口（自然C#，当前）
普通带标记方法直接写业务体，直接调用普通名。例如`player.ServerCombatAttackV1(id, isSuper, x, z)`。
**不写Implementation后缀，不写#if声明区，不调用PMNet_发送前缀**。校验同伴`_ForceValidate/_Validate`继续使用。
生成器提供private发送helper与接收执行器；Tools/PMNetWeaver（仅工具依赖Mono.Cecil）编译后自动拆体、接通入口。
含RPC类未经编织不能正常new或注册，禁止加跳过guard的define。ID锁/参数布局/ProtocolHash保持0xE6130FAA不变。

- .NET构建自动检查/刷新正式PMR3声明；编译后先织obj，再拷bin。非法声明/编织失败即构建失败。
- Unity接线在`Assets/Editor/PMNetWeaving/`独立Editor-only asmdef：源码生成刷新、编译结束回调、Play/Build硬门、Player脚本DLL阶段；菜单`Tools/PMNet/Weaving`。
- 元数据刷新或手动Repair后必须等重编译/脚本重载，磁盘改好不等于内存新鲜。Player只认当前BuildReport给的绝对路径，缺失拒绝，不猜旧包位置。
- Editor工具需本机dotnet SDK；首次构建可能恢复NuGet。独立产物在Tools/**/editor-tool（gitignored），依赖源码内容变化会重建。
- 虚方法/继承重写RPC、同名重载、async/泛型/ref/out/in/params/默认参数目前明确不支持。校验与属性setter语义未因此扩展。
- 新门禁：PMNetWeaverTest、PMNetWeavingEditorTest、PMNetWeavingEditorCheck、`python Tools/test_rpc_build_pipeline.py`。自动回调/真实Player/IL2CPP尚未实机验，不得写已完成。
- 当前细节契约`Docs/plans/net-rpc-weaving-contract.md`。前段R2的旧PMNet_公开调用方式仅历史。


## 10. 复制属性作者接口（当前）
推荐`[PMReplicated] public int Hp { get; private set; }`，业务正常`Hp -= damage`；编织器自动处理Push变化，不再业务调用PMNet_Set_*。原字段模式保留手动Mark/旧setter，或用`PushBased=false`轮询。普通auto-get/set允许初始化器/不同访问性；自定义/只读/virtual/indexer等不支持形态明确生成错误。
- 值总会存入；变化且HasAuthority且PushBased时才标脏。客户端本地写不标脏，不代表拥有服务端写权限。
- 收包Reader直接RawSet，不回环、不因setter触发OnRep；OnRep仍由复制层应用后统一分发。
- 数组整体换引用可自动发现；array[i]、同引用重赋不承诺自动标脏，需要显式MarkPropertyDirty或轮询。当前整对象采样可能顺便发现其它变化，不作为自动跟踪保证。
- PushBased=false现已实际解析并运行期轮询；无变化不发，按连接可见性筛候选，预算轮转防饥饿。不是完整频率/休眠/相关性实现。
- 含RPC或auto-property的类都必须编织；纯属性零RPC不可绕guard。原13成员迁成同名private auto-property以保ID，外部只读视图保留。
- 门禁新增PMPropertyWeaverTest；复制反例扩到14类。详见net-property-authoring-contract.md与_property_integrated.md，实机仍后置。
