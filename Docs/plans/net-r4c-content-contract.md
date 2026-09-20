# R4-C 正式地图与角色接入契约

状态唯一源net-architecture-migration.md。本轮C1资产准备/C2运行适配并行，C3宿主集成后顺序执行。用户授权继续，不改UE不抢编辑器锁不提交。Unity2019.4/C#7.3/netstandard2.0；新cs+meta，中文BOM CRLF。

## 决策

首图沿当前真实联机入口HYLDGame.unity+ScenseBuildLogic.map2，维持实际mapx=33/mapy=21循环范围，不擅自把表全部35行纳入。旧模板有随机且无统一播种，选择构建期固定种子0x52444301烘焙一次，双方加载同一生成资产，不运行时各自随机。旧原始场景/Player.prefab不改。生成资产属于可重建产物，Editor构建前检查/生成，不能手写Unity YAML假称编辑器验证。

共享资源路径：Resources/PMNet/BattleMapV1.prefab（Resources键PMNet/BattleMapV1）、Resources/PMNet/PlayerVisualV1.prefab（键PMNet/PlayerVisualV1）、Resources/PMNet/BattleContentV1.json（TextAsset键PMNet/BattleContentV1）。三个产物由Editor生成。未生成时运行明确失败，不回退临时地图或旧战斗。旧PMR3TestScene仍用于专属诊断smoke，正式内容选择在C3通过显式会话契约区分，不能服务器/客户端各自猜资源存在。

内容manifest固定JSON schema：formatVersion=1, mapId="hyld-map2-v1", seed=1380205313, mapResource="PMNet/BattleMapV1", playerResource="PMNet/PlayerVisualV1", contentDigest十六进制字符串SHA256(64字符), collisionDigest uint非0, worldVersion int正数。digest覆盖确定布局/模板源指纹/实际Collider类型与几何transform/mesh引用/源Player指纹；不得用实例ID/hashcode/运行期随机；collisionDigest/worldVersion由digest确定派生，不各写常量。manifest代码C2负责强校验。map出生点首版固定世界x=±15 z=-5/0/5 y=由实际地面查询+半高，队伍按名册排序映射，不做本地全局镜像，首批键盘输入世界坐标。技能/伤害/UI完整迁移后置R5/R6。

## C1 Editor内容烘焙（独立写入组）

新增Client/Assets/Editor/PMBattleContentBuild.cs(+meta)，namespace PMNet.UnityEditor，静态BuildContent()菜单Build/Prepare PMNet Battle Content。读Assets/Scenes/HYLDGame.unity里ScenseBuildLogic序列化模板。用Editor安全预览场景读取或另一经验证不会执行旧游戏Awake的编辑模式方案；若Application.isPlaying明确拒绝。不得调用ScenseBuildLogic.InitData（依赖全局随机/旧玩法），按原循环算法用局部确定PRNG构建map2（完整复刻floor/wall/obstacle实例位置旋转缩放与具体模板选择，边界树按源代码核对）。纯地图输出只保留Transform/MeshFilter/Renderer(含材质)/Collider(保留真实形状)/必要LODGroup，删除所有MonoBehaviour、动态Rigidbody、旧UI/相机/音频/玩法脚本，非允许组件需明确报告或失败而非静默残留。模板调色板不进成品；产物含实际地图，不用原始场景随便clone全部对象。不得改变原场景脏状态/选择/activeScene，临时对象finally清理，Unity Random.state不变。

角色源Assets/Resources/Remake/Player.prefab，提取干净表现保留Transform/Renderer/MeshFilter/Animator/必要骨骼，移除所有MonoBehaviour/missing script、Collider、Rigidbody、AudioListener等，Animator.applyRootMotion=false，Animator参数Speed供C2检查后用，禁止旧PlayerLogic/HYLDPlayerController或攻击脚本。安全复制原资源到临时内容后清理，不能原件Destroy。

生成prefab通过PrefabUtility.SaveAsPrefabAsset真实API，内容先生成临时产物全部验证后再发布manifest，失败不能留下valid manifest。可重复输出/摘要稳定，生成后回读检查脚本数0/地图Collider>0/角色无Collider、入口资源都存在；输出清晰summary。提供不修改资源的ValidateContent()给构建hook用。PMDsBuild.cs构建正式内容前调用Prepare/Validate(先不改变server-smoke默认场景)，Resources会随包自动纳入；加载仍runtime资源不是旧整数场景索引。新Editor代码必须在Tools/PMBattleContentBuildCheck中引用真实Unity2019Editor/Engine程序集C#7.3编译，必要仅ScenseBuildLogic类型/字段边界stub(真实类无法编入时说明)，不桩Unity API。不自动启动Unity执行菜单，不生成伪资产；用户下一次Build菜单可真正产出。

## C2 运行内容适配（独立写入组）

新增Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs、PMUnityBattleMap.cs、PMUnityBattlePresentation.cs各+meta，namespace PMNet.Unity。不改宿主/PMUnityMoverPresentation旧诊断对象。manifest class字段与上schema一致，Load+Validate采用JsonUtility，检查路径白名单、版本、digest格式及派生值，失败不退回。地图PMUnityBattleMap IDisposable，静态TryLoad(out map,out error)读取manifest+map prefab到专属LocalPhysicsMode.Physics3D Scene；保证非默认physics、先临时activeScene实例化且finally还原；加载纯地图Collider清单有界4096，所有Collider enabled/active，排除trigger不作硬障碍，验证无任何MonoBehaviour/动态刚体/旧驱动/相机。query使用现有PMUnityMoverCollisionQuery全参绑定，不新增第二物理框架，碰撞适配的通用形状限制必须报告；任意地图真实PhysX待验。提供Manifest、Scene、PhysicsScene、Colliders、Query、TryGetSpawn(teamIndex,slot,out PMMoverSyncState,out error)（x=±15,z=-5/0/5，向下合理有界查询找真实足底+半高并Overlap拒绝障碍），GetSceneDigest检查与manifest数据协议一致；需额外canon schema与Editor组对齐只能遵守已冻结字段，不能私加共享字段。

角色PMUnityBattlePresentation IDisposable：(label,role,manifest)，加载清理后PlayerVisualV1，拒绝DS/Authority创建，验证无旧脚本/Collider/Rigidbody，不在运行期Instantiate脏原件再禁脚本。ApplyPredicted/ApplyInterpolated(PMMoverSyncState)唯一Transform写入口，yaw/position/scale，Animator按确实存在的Speed参数驱动、root motion关闭；平滑不是第二仿真，不查询物理。可选CreateTestCamera独立相机只跟新根、不读旧静态，不修改旧相机，Dispose清理。提供Transform Root供未来宿主绑定UI，相机不强制创建。

Tools/PMBattleContentRuntimeCheck引用真实Unity2019Engine程序集编译，只列C2+PMMover/PMPrediction必要依赖。可有纯manifest规则测试，真实Resources/场景加载明确未执行。报告精确API供主侧C3消费。

## 验收边界

T-C1真实API编译/Editor重复生成与无旧脚本检查；T-C2manifest路径版本/身份摘要校验与资源缺失failclosed；T-C3宿主显式会话模式+同资源/同摘要/场景就绪前不发Ready；T-C4正式地图两客户端移动/障碍/出生/退出真实Unity。C1/C2不代表C3已接线，不标整体完成。

摘要派生冻结：contentDigest是小写SHA256 hex；collisionDigest=前4个摘要字节按LE uint（若0则1）；worldVersion=(int)(collisionDigest & 0x7fffffff)（若0则1）。C2不重算Editor源资产指纹，校验manifest内部一致/资源组件约束，完整内容一致性通过C3握手digest与同构建生成门守护；不能声称客户端资源防篡改。


## C3冻结接线（消费C1/C2实际API）

显式会话模式使用已有MAC/票据绑定CollisionDigest：0禁止，0x52334201保留诊断场景；正式内容digest不得等于保留值。正式Lobby HYLD_PMNET_DS=1启动时从Client/Assets/Resources/PMNet/BattleContentV1.json读取并校验正式manifest，缺失拒绝新链启动并明确路径，不默认诊断。允许HYLD_PMNET_CONTENT_MANIFEST显式绝对路径部署，未设置从仓库根推导。不改PMDsLobbyHost的通用options测试默认，直接构造smoke options保留诊断digest。无需再造平行mode来源。

DS bootstrap与client offer选择：仅digest==诊断保留值才走PMR3TestScene；其它非0必须PMUnityBattleMap.TryLoad并manifest collisionDigest相同，绝不按资源存在猜/回退。正式DS SceneReady前加载完成/核Collider/ValidateAllSpawns；控制Ready回报实际选定摘要，不再固定PMR3Runtime.CollisionDigest。正式客户端同步加载资源后OpenClient，场景失败释放且不回旧链。查询WorldVersion来自同manifest。

DS正式出生名册稳定按TeamId分成最多2队、各最多3人，再按PlayerId/Uid排序分槽，TryGetSpawn校验几何，HeroId读取共享BattleNumericConfig合法表取MaxSpeed，禁止信客户端速度或默认同速。诊断模式保持旧±3/default速度以保住已验烟测。正式客户端表现用PMUnityBattlePresentation，诊断保留PMUnityMoverPresentation；可使用private rig接口/委托统一Apply/Dispose，避免改两组公开API。新相机控制自己对象并退局还原，不让旧主相机遮住；不能改旧场景永久设置。首批键盘WASD/Space，TouchLogic摇杆/攻击UI迁移仍待，不能宣称完整玩法。

Lobby manifest解析net8 System.Text.Json(字段)，独立Server/DS/PMDsBattleContentConfig.cs，不把UnityEngine链接到Server。与C2验证的相同schema/seed/资源白名单/hash派生通过有正反用例的对照门禁守住，保持代码量小。源字段与manifest一致后options.CollisionDigest设置正式digest；manifest目录部署文件是只读配置，不打印票据/秘密。现有入口HYLD_PMNET_DS缺省旧链暂保留到R6。
