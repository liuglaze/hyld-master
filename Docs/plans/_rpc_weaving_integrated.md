# RPC自然C#接口：主侧集成交付与验证

状态唯一源：net-architecture-migration.md末尾。接口：net-rpc-weaving-contract.md。旧#if声明区方案已取消，未实施其生产改造。

## 实际交付
- 业务普通带标记方法直接写体，普通名调用。13个PMR3 RPC保持原声明与参数，不写Implementation，不暴露PMNet_发送前缀。
- PMNetGen生成private发送helper、接收分发与构造/注册guard；PMNetWeaver离线自动拆私有业务体、接通原入口、修正两个内部调用避免递归。Cecil只在net8工具，不进Unity runtime。
- 根Directory.Build.targets先对正式PMR3做decl-check，只有差异exit2才gen+复验，并刷新Compile项；CoreCompile之后织obj并check，才复制bin。嵌套工具restore/build剥离父输出/TFM属性，工具自身不递归。
- Editor独立asmdef：源码生成刷新、编译回调、不可关闭的Play/Build硬门、Player脚本DLL阶段。只信当前BuildReport路径，不猜上一轮包；缺工具/缺格式/缺目标/未重载拒绝。
- 原业务发送前缀实时rg扫描0。RPC ID锁SHA256始终47f0ad21845f5810edb3705dbb7e1bcff4b6752595a83066f470766bd40fece8；13RPC、13属性、两类；ProtocolHash仍0xE6130FAA。

## 独立复核与主侧补修

1. 工具审查修复：合法重载PDB误拒、假version、假guard、构造gate漏检、旧PDB假通过、暂存清理、并发锁与失败回滚。详细反例见_rpc_weaver_review_fix.md。
2. Editor审查修复：缺格式休眠放行、硬门开关、猜旧Player包、生成后假新鲜、工具依赖漏检、忙时旧InSync；进程执行改分块有界输出、有界排空，exit0但Failure不算成功。见_rpc_weaving_editor_review.md。
3. 主侧发现PDB定位仍用简单类型名，不同namespace同名类会误拒。改完整类型名（嵌套用Cecil同格式），参数数排除返回参数行。加固夹具改成两个namespace下同名HardeningAlpha，保持两种业务行为的正反对照，314项通过。
4. E2E的故障注入旧逻辑直接调用Fire，在新模式下已经是网络入口，不能再复现“绕校验执行体”。仅注入分支反射调用编织私有体，生产接收仍走Deliver/Invoke；五个缺陷继续5/5命中，E2E209/0。
5. 主侧发现克隆把ImplAttributes强制重置IL，远端体丢失Synchronized。保留原实现标志并在check核对；真实夹具给ServerBranch加Synchronized/NoInlining并在本地/收包路径检查Monitor.IsEntered(this)。修前Debug/Release/无PDB远端均失败（6条报告），修后314/0。反例日志Tools/PMNetWeaverTest/methodimpl-before.log。
6. 主侧补路径边界：Windows C:foo与盘根相对路径不是完整绝对路径；Player必须拒绝。Editor API给相对程序集路径时明确按Unity项目根解释，不按外部dotnet仓库cwd。纯策略新增验证，EditorTest26/0。
7. MSBuild主侧加自动声明同步时曾出现metadata嵌套引号MSB4092；改为RootDir+Directory元数据匹配后全部重跑。沙盒新增类夹具曾漏一个右括号被扫描完整性门明确拒绝，修夹具而非放宽门。

## 本次真实验证
均本次build退出0才run；日志Tools各工程/weave-main-verification.log。服务端输出Tools/PMNetWeaverTest/bin/lobby-main，不覆盖用户Server/bin；未启动Unity/用户服务。

| 范围 | 结果 |
|---|---|
| PMNetWeaverTest | 314/0；57次CLI，22次真实夹具编译，Debug/Release/无PDB、同程序集/跨程序集覆盖 |
| PMDeclCheck | 107/0 |
| PMNetWeavingEditorTest | 26/0；纯BCL/受控子进程，非Unity回调运行 |
| PMNetWeavingEditorCheck | 真实Unity2019 API，0警告0错误 |
| PMNetE2E | 209/0，五个故障探针命中 |
| PMR3RuntimeTest / Integration | 180/0、143/0 |
| PMR4Network / Integration | PASS、113/0 |
| PMR5Declaration / Network | 139/0、PASS |
| PMR6Declaration / Network | 235/0、385/0 |
| PMCombatCoreTest | 1699/0 |
| PMCallspaceCheck / Replication / Transport | 93/0、265/0且5/5注入、173/0 |
| PMNetWorld / Session | 333/0、405/0 |
| PMDsControl / Lobby / Host | 623/0、268/0、73/0 |
| PMNetVerify / PMLegacyRetirement | 498/0；28静态+6负例通过 |
| ClientCheck / GlueCheck / R4UnityCheck / R6NetworkCheck / NetLangCheck | 全部build0 |
| Server真实构建+13RPC织入+check | build0，既有依赖兼容警告保留 |

## .NET构建闭环沙盒
`python Tools/test_rpc_build_pipeline.py`复制最小仓库到唯一TEMP，不注入生产源码。6场景通过：
1. 首次无Generated/无工具obj缓存：restore→gen→Compile项纳入→compile→weave→check。
2. 增量build：DLL/PDB/Generated/锁逐字节不变。
3. 只改RPC可靠性标记：自动发现旧元数据并刷新，ID不漂移。
4. 新增普通RPC：自动生成/编织，实际RPC数13→14。
5. 新增网络类：同一次构建纳入新增g.cs，实际RPC数15。
6. 注入virtual非法声明：build失败，原生成文件/锁均不被覆盖。
日志Tools/rpc-build-pipeline.log与rpc-build-sandbox-*.log。

## 未完成／明确边界
- Unity实际自动回调、首编译引导、Player脚本DLL阶段的实际路径、Mono/IL2CPP运行仍PENDING_USER；API编译不等于实机。
- 初次没有已加载hook时不能承诺自动修复；菜单与外部工具是恢复入口，漏编织guard必须保留。
- 只处理当前Assembly-CSharp（项目runtime无asmdef）；未来拆runtime程序集需扩明确注册范围，不自动扫改第三方。
- 不支持虚RPC/网络继承重写、泛型/async/重载/ref-out-in/params/default、强名称、非portable符号，明确拒绝。本地校验维持原PMNet语义，不声称完全照搬UE项目CVar。
- DLL/PDB替换支持本工具并发锁与可捕获I/O失败回滚，不保证断电/强杀时两文件绝对事务。
- 原框架四区审计仅立了计划，被用户插入RPC体验需求暂停；本轮不是对整个游戏架构出具无问题保证。
- 未提交/未暂存/未改源资产；5个新meta GUID唯一、无BOM/LF；中文源码BOM+CRLF。
