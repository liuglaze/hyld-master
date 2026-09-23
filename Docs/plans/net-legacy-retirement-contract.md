# 旧战斗链退役（用户明确优先删除代码）

状态唯一源net-architecture-migration.md。用户当前明确要求主要删除旧代码，撤销“必须等完整实机后才删除旧链”的实施门槛；不代表补齐特殊技能或实机验收。仅新PMNet/UnityDS常规联机可用，不保留旧协议兼容/自动fallback。先删运行入口与旧实现，再顺序收缩旧protobuf和失效工具/文档。编译/自动回归照常，Unity实机仍后置，不启动/关闭Editor、不提交。

## 入口规则
Server匹配StartFighting无条件走StartDedicatedServerBattle，默认初始化DS Lobby；HYLD_PMNET_DS不再是选择旧链开关，配置不齐/manifest错只明确匹配失败，绝不启动旧BattleController或LZJUDP。现有DS路径/工作目录/bootstrap/控制端口仍按显式部署配置，不瞎猜用户目录；可沿用有据的仓库相对默认路径但需明确日志与存在校验。Lobby只TCP+控制端口，新会话UDP由每局UnityDS独占。
Client UIMatchingPanel只接受PMDS1，非PMDS1旧成功通知显式报错而非BattleData.Init/LoadScene旧战场。HYLDManger断线清新PMClientSessionHost而不读BattleManger；PMClientSessionHost删除CloseExisting旧socket触点。Unity PMDsHost只接受-bootstrap，缺失或非法明确非0退出；不再创建裸socket/router/诊断packet handler/旧tick。server-smoke必须也是bootstrap+新PMDsSessionHost诊断，不同于退役PMDsProbe裸MainPack探针。

## 并行删除边界
A Server：删Server/Server/{Battle.cs,BattleController.Bullets.cs,BattleController.Network.cs,BattleManage.cs,BattleContext.cs,ServerBullet.cs,ServerVector3.cs,ClientUdp.cs}；改Server.cs/Client.cs/ServerConfig.cs、Controller/Controllers.cs与ControllerManger.cs去旧ClearSence/Battle入口、旧重要TCP帧/回放分支、旧UDP监听/旧断线通知；保留全部登录/好友/房间/匹配与新DS控制。PMDsLobbyHost options与NewChainEnabled允许保留内部服务可用性测试API，但production不得选择旧路；FromEnvironment默认新且显式0也不能恢复旧。
B Client：删Scripts/Server/Manger/Battle下BattleManger/BattleData所有partial/HYLDPlayerManger/HYLDBulletManger/HYLDCameraManger/HYLDBaoShiZhengBaManger/GameManger以及meta；删UDPSocketManger、CommandManger、BattleFrameHud及meta。改UIMatchingPanel/HYLDManger/PMClientSessionHost旧入口引用；HYLDStaticValue删除旧AddComponent自动启动（保留Hero表/序列化数据），Toolbox删除旧manager依赖、旧联机胜负控制，TouchLogic删除旧Command发送（其旧摇杆未接新链不伪造已支持）；PlayerLogic/HYLDPlayerController/移动型大招仅0.8位置跳变日志常量依赖内联或迁中立处，不能为取常量保BattleData。ConstValue（实际NetConfigValue）保留大厅地址与仍被源表现读的基础frameTime，删除只被已退役链使用的预测/重发/弱网字段。清理工具旧ALLOWED白名单与对旧socket的stub。仍挂资产的source PlayerLogic/Touch/Toolbox/shell等只做删除旧网引用必要改动，不批量删美术源资产或第三方；它们不是新链权威，不称全部旧试玩算法已删除。
C DS旧bootstrap分支：确定性重写PMDsHost为新session轻量wrapper（Start→Initialize→session.Pump→Dispose/Quit），删PMUdpRouter/PMSingleBattleRegistry及meta；删只测这条旧路的Tools/PMUdpRouterCheck/PMUdpRouterTest/PMDsProbe源文件（不清历史日志/用户构建产物），相应GlueCheck csproj显式include清除；PMDsBuild生成run_ds脚本必须要求bootstrap参数而不是提示旧dsid/matchid/port就能正常开局。保留PMNetLaunchOptions现新链消费参数/PMNetBootstrap身份，不改变默认客户端。
D 资产解挂：只改Scenes/HYLDGameTest.unity（BattleManger guid7200a0eb9673b6e4f8cb8386cdde31db），HYLD1.0/Scenses/{HYLDTryGame,HYLDGame}.unity与HYLD1.0/Resources/Main Camera.prefab（HYLDCameraManger guid eec213bc5141674488046b737aede2bf（已读取meta冻结））。移除对应MonoBehaviour YAML文档和所属GameObject.m_Component引用；其它本地fileID指向该组件应显式归零并核证据，不碰相同数值的外部guid引用。保存前后其它文档逐段比对确保最小修改。禁止动Scenes/HYLDGame.unity、Resources/Remake/Player.prefab与Resources/PMNet产物，这是正式烘焙输入/输出。

## 后续顺序E（不要并行抢共享proto）
A/B/C删后实时扫描剩余SocketProto.BattleInfo/BattleFrame/ClientMove等业务引用，消灭剩余调用再删权威proto旧战斗消息、MoveType/AttackType、MainPack battleInfo13/battle_net_sim_config15与旧Action31..34/37..41；保留大厅BattlePlayerPack/FightPattern/Hero/StartEnterBattle30（匹配名册/新offer）。号码reserved不可重用，生成器支持reserved（ProtoParser已核）。ClearSence35/36/Request8根据剩余通用加载场景入口核定，不机械删导致大厅挂载丢失。真实build.bat重新生成四份SocketProto与PMNet产物，不能手删generated。同步旧PMNetVerify/语言门/冒烟夹具改测仍存在大厅DTO；新增旧链禁回归门，对活源码断言旧类型/协议/7777监听不存在（历史Docs/日志不扫）。其它非权威proto副本删除避免复活，不改包体/第三方代码。

## 验收
T-L1 Server独立输出编译+Lobby/Control/房间匹配自动门禁，无旧Battle/LZJUDP真实调用，无7777监听代码。
T-L2 ClientCheck/GlueCheck/真实Unity2019宿主编译、R4/R5/R6回归，旧BattleData/SavedMove/Command/UDP收发源码归零。
T-L3 PMDsHost不带bootstrap明确失败（静态/替身门禁），带bootstrap新链不回落；server-smoke新链不受影响。
T-L4 GUID实时反查：已删类对应资产引用0，无新增missing script，4资产改动仅对应组件/引用；原source/正式输出哈希不变。静态YAML不替代Unity实机加载。
T-L5 旧proto消费归零→生成/同步/字节序列化回归，不能靠旧二进制跑绿。
T-L6 docs入口与BothSide明确只有新链、部署配置缺失失败、残留数据/挂载脚本边界和实机PENDING_USER。


## E冻结补充（A/B/C/D实际删除后实时引用核验）
主侧rg --no-ignore限定Client/Assets/Server/Tools C#去generated/bin/obj后，旧BattleInfo等实体活引用仅Tools/PMNetVerify。剩余旧Action消费仅UIStartMainPanel/RequestManger BattleReview、UISliderPanel ClearSence35/36；其唯一SendLoadOver/IsCanEnterBattle调用者ClearSenceManger.AsyncLoadScene，且Get/AddBattleReview无UnityEvent资产方法名引用。因此E2移除旧回放处理/持久化方法，UISliderPanel保资产挂载纯UI壳不发RPC，ClearSenceManger变通用本地异步加载（删等待远端Ready分支，不能挂死）。E1可同步删RequestCode Battle7/ClearSence8、Action31..41全部reserved、MainPack字段13/15reserved、旧14消息以及孤立BattleRoomPack（活源码零引用），保BattlePlayerPack/Hero/FightPattern/StartEnterBattle30。
PMNetVerify改测真正剩余大厅DTO，全字段与嵌套/repeated/UTF8/正负int/unknown字段往返仍保留独立protoc字节oracle，不依赖旧二进制。历史非权威Client/SocketProto.proto、Client/hyld-master/hyld-master/ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto若不存在勿编造、根ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto实际存在删除；其它第三方google protobuf模板不删。不改protoc版本，真实build.bat生成4产物+check-only，不手编删generated类。
子代理前A组误用git rm暂存8文件，主侧已仅git restore --staged这8条，保留工作区删除；不得git add-A/暂存/提交，报告的相关建议不采纳。
