# hyld-master Lobby 服务端入口（旧局内仿真已删除）

## 1. 角色与当前状态

本目录是 **net8.0 大厅/DS编排服务**，不再承载局内战斗仿真。旧 `BattleController/BattleManage/BattleContext/ServerBullet/ServerVector3/LZJUDP` 已删除，不监听 UDP 7777，不保留旧匹配 fallback。

局内权威在 Unity2019 打包的 HyldDS：`PMDsSessionHost → PMR4/5/6 drivers → PMCombatSession`。每局一个DS，主线程模拟；Lobby只负责账户/好友/房间/匹配、可信名册、进程与票据、结果确认及资源回收。

唯一进度源：`Docs/plans/net-architecture-migration.md`。最新契约：`net-r3-control-contract.md`、`net-r6-combat-contract.md`、`net-legacy-retirement-contract.md`；双端约定见 `BothSide.md` / `Server/Docs/ForClient.md`。
用户要求代码优先、实机集中后置。编译与纯网络测试不是完整Unity联机验收。

## 2. 启动与配置

```bat
dotnet build Server/Server.csproj
Server/run_lobby.bat --check-only
Server/run_lobby.bat
```

- `run_lobby.bat` 以自身目录定位仓库，不再硬编码某个用户目录；只在环境变量未设置时填**本机开发默认值**：
  - `HYLD_PMNET_DS_EXE` → 仓库 `HyldDS/HyldDS.exe`
  - `HYLD_PMNET_DS_WORKDIR` → 仓库 `HyldDS`
  - `HYLD_PMNET_DS_BOOTSTRAP_DIR` → `%TEMP%/HyldDSBootstrap`
  - `HYLD_PMNET_CONTENT_MANIFEST` → `Client/Assets/Resources/PMNet/BattleContentV1.json`
- 部署环境显式设置上面变量；进程内不猜目录。可配置 `HYLD_PMNET_DS_PORT_FIRST/LAST`、`HYLD_PMNET_DS_CONTROL_ADDR/PORT`（实际默认值见 `PMDsLobbyHostOptions`）。
- `HYLD_PMNET_DS` 旧选链开关已废弃；包括设为0也不能恢复旧战斗。缺配置/manifest非法/DS启动失败只明确失败。
- `--check-only` 只检查路径存在，不启动服务、不证明二进制新鲜或协议一致。集中联调前必须统一重建Lobby/DS/Client。
- `Program` 根据 stdin 是否重定向选择Console.Read或长期等待；长驻进程应真正分离启动，不能依赖短命父shell的stdin。
- 不覆盖运行中的Server.dll：编译检查可输出独立Tools目录；不主动停止用户服务。

## 3. 路由与文件

| 文件 | 职责 |
|---|---|
| `Program.cs` | 日志路径、配置、TCP服务启动与非交互保活 |
| `Server/Server.cs` | TCP7778、客户端集合、DS Lobby初始化、匹配入局gateway |
| `Server/Client.cs` | TCP接收与发送、账户绑定、断线通知新DS Lobby |
| `Controller/ControllerManger.cs` | 显式 `(RequestCode,ActionCode)` 分发表、request_id统一回填；无字符串反射分发 |
| `Controller/Controllers.cs` | 登录/好友/房间/匹配/PingPong；StartFighting只走专用服务器 |
| `Server/FriendRoom.cs` | 大厅房间、队伍、广播 |
| `DS/PMDsLobbyHost.cs` | 可信名册、端口/uid占用、启动文件、入局通知、结果通知队列 |
| `DS/PMDsCoordinator.cs` | DS状态机、Ready/结果幂等/退出/超时回收 |
| `DS/PMDsProcess.cs` | 白名单进程启动/结束，不经任意shell拼接 |
| `DS/PMDsBattleContentConfig.cs` | 正式manifest只读强校验，不回退诊断地图 |
| `DAO/UserStore.cs` / `UserData.cs` | 内存用户库与连接数据门面 |
| `Tool/Message.cs` | TCP四字节小端长度分帧、合法长度扩容与解析 |
| `Tool/Loging.cs` | `Logging.Debug.Log`，FileShare.ReadWrite，路径启动时可见 |

## 4. 新对局与结果链

1. 大厅匹配得到可信uid/team/hero名册；`StartFightingDedicatedServer` 调DS Lobby，不能缩编伪造剩余名册。
2. Lobby写bootstrap并带 `-server -bootstrap` 拉起白名单UnityDS；DS真正场景与端口就绪才发地址/票据。
3. 客户端收到 `StartEnterBattle(30)` 的 `PMDS1:` 通知，连接本局UDP；认证玩家副本决定RPC归属，不信自报uid。
4. DS非smoke局全名册ready后开战，30s缺人明确失败。HP/资源/死亡在PMCombatSession唯一结算。
5. 现首批规则沿旧服首杀终局；断线判剩余唯一原始TeamId，无唯一胜方0。特殊技能等未全部迁完。
6. DS先可靠通知客户端终局，收到全部在线ACK或5s宽限后 `PMDsLobbyAgent.SubmitResult` 提交冻结摘要。
7. Lobby幂等受理Result、回Ack；DS发Exited并退出；确认OS进程退出后才释放端口/名册占用。

Lobby gateway不伪造旧BattleReview。当前客户端结果通过DS可靠RPC到达；回放功能尚未重做。
`server-smoke` 仍是带bootstrap的新链Probe/结果测试，不能拿它当完整攻击/结算实机验收。

## 5. 用户数据（无数据库）

- 不依赖MySQL/MongoDB/Redis。`UserStore`进程内字典，线程安全、对外返回快照。
- 登录不存在的账号会自动创建；已有账号密码错误仍失败。新昵称默认账号名。
- 一条TCP连接只能登录一次；同账号只能一条活跃连接。测试错误密码/重登时注意这两个独立门。
- 进程重启清空内存用户数据；`hyld.sql`仅历史文件，不再执行。

## 6. 协议与代码来源

- 大厅权威proto仅 `ProtobufAndNotepad/Protobuf/SocketProto.proto`，真实 `build.bat` 生成两端与工具目录SocketProto及PMNet产物。
- 旧BattleInfo、MoveAck、state_mask、BattleReview、弱网调参协议已删除。Request7/8、Action31..41、MainPack13/15保留reserved，禁止复用。
- `BattlePlayerPack/Hero/FightPattern/StartEnterBattle`继续服务大厅匹配身份；名字含Battle不代表旧仿真。
- PMNet框架源码链接自Client/Assets；PMR3声明产物两端同集合。Lobby明确排除PMR4MovementDriver/Codec、PMR5ProjectileDriver、PMR6CombatDriver，不编局内仿真。
- 当前声明ProtocolHash=0xE6130FAA；以后以真实生成注册表为准。不同版本不能混跑，不提供旧链兼容。

## 7. 验证与协作纪律

- 本次build成功后才运行测试，不能用旧DLL打印假绿。
- `PMDsControlTest/PMDsLobbyTest/PMR3IntegrationTest`覆盖控制与会话；`PMR6NetworkTest`覆盖战斗字节链；`PMNetVerify`覆盖大厅wire；`PMLegacyRetirementTest`禁旧链复活。
- `PMServerSmokeTest`需要真实监听中的大厅；编译它不等于已运行登录/匹配验收。
- 双端接口或部署变更同步 `BothSide.md`、`Docs/ForClient.md`、主计划；不要再从已删除旧Battle文件猜当前行为。
- 日志用本项目 `Logging.Debug.Log`，不使用UE宏。新中文源码UTF-8 BOM+CRLF。禁止无范围暂存与代提交；不主动SVN更新/回退；不启动第二Unity、不编译UE。


## 8. RPC编织构建（当前）
声明仍为带Attribute的普通业务方法，调用普通名字；PMNet_发送helper已private，由编译后PMNetWeaver自动接通。不要手写Implementation/#if声明区。
根Directory.Build.targets对编入生成RPC集合的程序集自动weave+check，PMR3消费者先check/gen声明；工具自身/无生成集合工程不参与，无全局跳过开关。处理obj后才复制到bin/交付依赖，漏编织new与注册会失败。初次构建自动restore构建工具；中文错误中给出缺生成/未编织原因。
Cecil只在net8工具进程里，不是Server/Unity runtime依赖。源码改动后必须本次build0再运行，不能继续使用旧DLL。当前ID/线上ProtocolHash不变。细节见net-rpc-weaving-contract.md；全量状态仍在主计划。


## 9. 自动属性复制（当前）
共享声明支持PMReplicated自动属性正常赋值，编织后变化且Authority且PushBased才Mark；Reader只RawSet，不反向标脏。PMR3Player12+PMR5Projectile1成员已同名迁移，ID/hash不变。纯属性零RPC类也纳入编织/构造注册guard。字段手动模式保留，PushBased=false现有真实轮询与可见性/预算公平处理。数组原地修改不承诺自动Push。完整接口net-property-authoring-contract.md；不以build/纯测试冒称Unity实机通过。
