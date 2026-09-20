# R4-B 网络预测与Unity接线契约

主计划net-architecture-migration.md是状态唯一事实源。本文件展开接口；R4-A契约和实际PMPrediction/PMMover API为基础。不改UE、不提交、不关编辑器。Unity2019.4 / C#7.3 / netstandard2.0。源文件UTF8 BOM+CRLF、新cs配meta(无BOM/LF/唯一GUID)。

## B1 网络组冻结边界

复用PMR3Player，不另造注册表。新增运动声明直接写PMR3Player.cs，外部PMNetGen扫描整个PMR3输入目录、沿用pmnet-r3-ids.json，重新生成现有两个g.cs。原Probe/Echo保持可用。高频运动禁止可靠RPC逐帧排队。

新增纯C#文件放Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs、PMR4MovementDriver.cs，namespace PMNet.R3。PMR3Player只含字段、声明与委托出口，算法放Driver。

承载：ServerMovementInputV1(byte[] payload) Unreliable + ForceValidate；_movementSnapshotV1 byte[] Replicated(None)，OnRep只通知/入队；ClientMovementEventsV1(byte[] payload) Reliable 给owner；ServerMovementResyncV1(uint streamVersion) Reliable + ForceValidate；ClientMovementResyncV1(byte[] payload) Reliable给owner。生成PMNet_*桩，禁止绕过PMNetRpcReceive/Owner校验。byte[]内部由专门框架codec使用PMNetReader/Writer protobuf原语编码，不重新发明UDP/MainPack/业务state_mask。所有blob<=4096字节且各类有更紧字段/数量上限，编码头Version=1；V1声明名固定，使版本更替进入生成协议摘要。

身份=(会话Epoch, player.NetId.Value)绑定；额外StreamVersion非0是本对象输入流重置代次，不能冒充对象ID或SessionEpoch。重同步由DS递增StreamVersion，旧流输入/快照/事件丢弃；新流保持输出边界与累计仿真时间不倒退。AP只在可靠初始/显式resync或首次生命周期初值建立时重建。普通属性更新不得解冻。DS不得按客户端自报帧直接跳进度。

输入只允许MoveX/Z/Y[-1,1]、Yaw有限且规范范围、Jump边沿、原始整ms dt1..50。禁止上行Effects/Layers/RemovedLayerIds（任何此类请求编码失败/接收拒绝，不能直接当可信命令）。一包最多8条，未确认输入最多128条，按最旧未确认窗口持续重发防缺帧饥饿；可兼带最新但不能饿死旧缺口。本地input clone存档，退役只按DS已模拟输出边界。每宿主帧最多8个1..50ms真实子步，超长帧不拉大dt。

DS使用PMAuthorityInputBuffer与PMMoverModel(注入IPMMoverCollisionQuery)，所有执行在调用线程(主线程守卫)；每Pump8步/100ms+墙钟信用，上行0dt拒绝；同一宿主tick不得重复充值。输出n+1，ServerFrame独立AuthorityServer域。完整Sync/Aux所有字段必须编码（含ActiveLayers.ElapsedMs），数组<=16层，float有限，尺寸/参数可用，未知Mode拒绝，碰撞WorldVersion不符拒绝/明确失败。禁止只发Position/Velocity。

快照只保证当前状态收敛。事件必须独立于快照确认游标：可靠RPC承载每个权威非空事件边界及顺序序号，AP在状态已确认到该边界之后派发；若事件迟于快照也能派发，不依赖Timeline.ApplyAuthority已退役区间。身份/流代次/边界/事件键校验，缓存有界，重复不广播；溢出必须明确失败而非淘汰未交付事件。同一状态事件不得同时由Timeline与独立journal重复派发。暂不做SP技能事件，因为本批PMMover事件仅owner确认，R5/R6另定观察者事件。

初始完整快照在Spawn首次生命周期Flush之前设置。Driver支持DS在帧边界提交可信运动Effect/Layer（只由DS调用，适于强制差异验证），上行不开放此权限。参数/层网络不等于完整技能预测裁决，后者保留待实现。

Driver API由网络组具体实现并在报告精确列出：构造接PMR3Player+collision+role/world identity；AP采样/步进；DS Pump(elapsedMs, serverFrame)；SP Advance(elapsedMs)/取表现快照；处理属性更新/RPC，Freeze/Dispose，诊断计数；事件确认出口。不持有Unity类型，不自行socket/墙钟取时。

网络组测试Tools/PMR4NetworkTest使用真实生成物/World/Session/Transport搭可控字节链，构建期自动生成不能改变旧锁ID；构造owner/nonowner；丢首包、重复乱序、snapshot早于event/反向、历史溢出resync、旧流延迟、完整状态差异、SP不预测/静止yaw、信用防加速。报告准确列命令/API/生成命令/缺口。

## B2 Unity组冻结边界

新增Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs、PMUnityMoverInput.cs、PMUnityMoverPresentation.cs。namespace PMNet.Unity。仅依赖现有PMMover/PMPrediction/PMNetRole，不依赖网络组未交付结果、不改宿主。

PMUnityMoverCollisionQuery实现IPMMoverCollisionQuery，构造(int layerMask,int worldVersion)，可选Collider[] allowlist重载（宿主把新建测试地板/墙传入隔离旧场景；不能让旧地图、表现胶囊或其它局参与查询）。胶囊中心/半高/半径遵守接口；主线程约束；查询忽略trigger；最近阻挡、有限值、零delta、起点接触/重叠、足底有符号地距处理有证据；查询缓冲固定上限、饱和显式失败而非静默忽略墙。禁止移动Transform、Physics.Simulate、设全局autoSimulation或修改旧场景；构建/每帧开始的Physics.SyncTransforms由宿主单次做，不能每条resim扫描同步。真实Physics.CapsuleCast/Overlap/ComputePenetration等API按Unity2019.4程序集验证，不用虚构API桩件证明可编译。

PMUnityMoverInput：提供Sample()返回PMMoverInput（测试新场景WASD/Horizontal,Vertical+Space边沿）；提供纯输入转换入口（用于可控测试，不引用旧CommandManger/TouchLogic）。World坐标Y-up，不带派生速度，不双镜像。跳跃边沿应缓存直到有真实输入步消费，零ms帧不能丢边沿，补子步不能重复跳跃。API命名/消费方法报告明确。

PMUnityMoverPresentation：每角色可见简单胶囊；无碰撞（创建primitive自带Collider必须禁用），DS绝不创建该对象。构造角色标识/Role；Apply(PMMoverSyncState state)一次写position/yaw/scale，运动仿真不在里面。Dispose幂等；可选简单Camera测试显示，不改旧camera、不强制自动创建，最终宿主自行取舍。AP呈现预测输出，SP呈现插值输出。

Tools/PMR4UnityCheck引用D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine里的真实DLL，编译上述三个文件及纯依赖；测试输入的纯转换/边沿；PhysX运行测试要在Unity内另有可执行入口/步骤，不能宣称net8运行真实Physics。可新增Client/Assets/Editor/PMR4UnityValidation.cs菜单测试（不自动执行、不触碰用户场景，使用临时本地PhysicsScene/创建后清理），允许对纯构造世界进行墙/地面/贴面扫掠验证。Tools新测试工程最多Program.cs/csproj报告明确能测和不能测。

## B3 主侧集成

消费B1/B2实际API，再改PMDsSessionHost/PMClientSessionHost，两端建立相同静态场景，碰撞allowlist明确。DS Spawn时建Driver与初值；客户端PlayerReplicated处理所有AP/SP，不再把SP当不需要的对象跳过。每Update顺序=网络入站/校正→采样或DS模拟→Finalize→后续网络flush(Endpoint已负责bridge.Update，不增加重复budget消费)。断线冻结，停止释放driver/表现/事件/场景。正常模式不自动smoke退出，原ServerSmoke探针流程保留。

集成同时补Tools中明确Include的消费者，Lobby构建可链接纯预测/Mover代码但不得实例化仿真。新增PMR4IntegrationTest真实loopback UDP两客户端。独立复核后回归现有R3与R4-A门禁；真实DS二进制旧版本需用户构建，禁止拿旧包验新代码。


## 主侧集成前复核修订

B1产物发现：快照构造函数未初始化非零_boundaryStartFrame，必须修核心并删除Driver借假Epoch重绑的绕行。SP不会接收owner-only ClientResync，故较新StreamVersion的合法DS复制快照必须允许SP在验证身份、世界版本、边界/仿真时间不倒退后更新插值绑定；这不适用于冻结SP（断线冻结保持），更不能放松AP普通快照不得解冻的门。新增AP升流后其他客户端SP继续运动/旧流不回写的真实链路回归。

事件与Resync虽然共享可靠顺序域，但Driver分开队列后可能先处理多条Resync再处理更早事件，不能假设传输保序自动等于消费保序。实现应保留可靠消息接收次序，或用可证明等价的批处理与事件序号重绑策略；至少覆盖同一次Update收旧流事件→Resync2→流2事件→Resync3→流3事件。不得用只保留上一流掩盖丢事件。

B3接线的API按_r4b_network_report.md与_r4b_unity_report.md。测试出生点必须在墙外（如x=±3,z按名册分行，y=1）；Aux.WorldVersion统一1。主线程在endpoint.Pump前Physics.SyncTransforms一次（RPC回调仅入队），Pump后更新Driver/模拟/Finalize，发送由下一endpoint.Pump完成，不能多调bridge.Update抢预算。Input在Tick真正接受后Consume边沿，不能先消费再因冻结/超限失去按键。


## 最终场景隔离实现

宿主使用BuildIsolated建立LocalPhysicsMode.Physics3D场景，硬校验PhysicsScene有效且非默认，query显式绑定该世界；失败/退出销毁对象并卸载场景。验证菜单同样拒绝假隔离，编辑模式不可用时提示在Play运行，真实运行保持PENDING_USER。AP读Timeline.PendingServerFrame免深克隆，端点IsDisposed使宿主显式失败冻结。
