# RPC自然C#接口与IL编织契约（当前方案）

用户已否决#if声明区/手写Implementation方案，批准：普通带标记方法直接写业务，调用普通名字，编译后处理自动拆实现。net-rpc-ue-authoring-contract.md作废；本文件定义接口，状态与测试仍仅在net-architecture-migration.md。

## 1. 作者体验与不变项
```csharp
[PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
public void ServerAttack(int skillId) { /* 业务体 */ }
private PMRpcValidation ServerAttack_ForceValidate(int skillId) { return PMRpcValidation.Accept; }
// 普通调用，支持命名实参：player.ServerAttack(skillId: 1);
```
不手写Implementation/声明区/PMNet_发送前缀，不采用C#9或升级Unity2019.4。不改变现有RPC ID/参数布局/网络权限/校验判据/本地callspace语义。尤其本轮是C#体验改造，不顺带复制UE项目ForceValidate本地调用/CVar细节。Class hash 0xB09BCD1C、全局0xE6130FAA、13个RPC与ID锁不变。

## 2. 生成器与编织工具的冻结接口（格式版本1）
PMNetGen仍扫描带体的普通标记方法，生成现有编解码、校验和callspace逻辑。
- 生成的发送helper仍叫 `PMNet_<M>`，改为private，仅编织后的同类入口可用，业务不再调用它。
- 接收helper仍 `PMNet_RpcInvoke_<M>`。编织前这两个helper恰各一次调用原始普通方法M；编织器只把这两处调用改到私有业务体，不替换其它业务里的普通调用。
- 每个有RPC的生成partial类增加 `internal static int PMNet_GetRpcWeaveVersion()`：编译前返回0，成功编织后返回1。
- 增加 `private static int PMNet_RequireRpcWeave()`：版本!=1抛InvalidOperationException（明确生成/编织修复命令），否则返回1。生成实例readonly字段初始化调用它（压掉仅此字段未读取编译警告）；PMNet_BuildEntry开头也调用它。这样正常new实例和注册均拒绝未编织程序集，不只靠Editor日志。只有完整验证的编织器才改version，不能用额外define跳过。
- 格式/version/辅助方法签名与原始RPC参数必须严格匹配。标记方法只支持非static、非virtual、非abstract、非extern、非async、非generic、有体、void；无ref/out/in/params/default参数，不支持RPC同名重载。元信息与生成模型不符失败，不允许无匹配helper却改stamp。

工具：Tools/PMNetWeaver（net8.0 CLI，Mono.Cecil 0.11.6；仅工具依赖，不进Unity runtime）。
CLI：`dotnet PMNetWeaver.dll --weave <assembly.dll> [--reference-dir <dir>]...`；`--check <assembly.dll>`只验证不得写；`--require-rpcs`可要求非空；支持--help。退出0=成功/无RPC（非空要求另行明确），非0=失败。至少输出RPC数、是否实际改写。
建议公开API可自定，但CLI/格式名必须按本契约，供独立Editor接线使用。

编织：
1. 预检所有带PMServerRpc/PMClientRpc/PMNetMulticast的同模块方法，精确全类型名，不碰第三方同名attribute。发现缺生成物/错误签名/未知版本/同名冲突等，整个输入不得改。
2. 完整克隆原业务体到private `PMNet_RpcBody_<M>`：参数/局部/分支/switch/异常处理/调试sequence point正确重映射；原调用点身份保留，业务体无RPC Attribute。原有method delegate引用仍指正常网络入口。
3. 原始M的IL替换为`this+各参数 → call PMNet_<M> → ret`（参数名和Attribute仍在M）。本地helper与收包helper的唯一业务调用改到RpcBody，避免递归。
4. 所有方法成功后stamp=1。二次weave先验证结构，不重复拆、不无条件信stamp。check同样验证wrapper与两个helper确实指向私有body，不能只有stamp就绿。
5. 对DLL与现有PDB做暂存/可恢复替换；任何预检/写失败不留下半程序集或错误stamp。无符号可工作；存在但无法处理的PDB/签名程序集/不支持PE等应明确失败，不静默丢调试信息。Mono.Cecil不需要运行时反射分发。
6. 初版不处理虚方法/网络继承/跨assembly RPC定义；明确拒绝，不猜重写继承链。新旧源码原本有大量未提交修改，不回退覆盖。

## 3. 分阶段边界
P1先在独立测试生成夹具中证明编织，不改PMR3生产源码和真实Generated、不启动Unity/现有服务。
并行可做Editor独立便利层（CLI契约已冻结），只编真实Unity2019 API，不实际改Library/用户构建。
P2主侧核验P1后：改发射器guard/私有helper、真实生成，迁移约90处业务/Tools发送调用，再接入.NET构建目标并跑真实PMNetE2E/R3/R4/R5/R6。
P3独立对抗复核：漏编织/重复编织/损坏helper/异常体/符号/增量构建；主侧验证与文档。

## 4. Editor与构建接口
Editor文件独立Editor-only asmdef，不能引用Assembly-CSharp。普通编辑器编译通过CompilationPipeline.assemblyCompilationFinished，在域重载前编织当前Assembly-CSharp.dll；编译有error不得处理。不要在域已加载后靠改磁盘冒称内存已生效。
Player打包必须在托管脚本DLL生成之后且Mono打包/IL2CPP转换之前处理；优先核实Unity2019真实`IPostBuildPlayerScriptDLLs`接口与BuildReport脚本DLL路径，API不支持则报告BLOCKED，不能拿IPostprocessBuild后改包替代。
当前runtime没有asmdef，只Assembly-CSharp需处理（独立Editor asmdef不包含RPC）。目标路径从Unity回调/BuildReport取，不能硬编码猜缓存目录。不修改第三方DLL。
工具构建/启动有超时与并发/输出预算、排空stdout/stderr；失败阻止Play/Build并给清楚错误。初次启动hook尚未加载不能保证自动救援，需外部生成/编织命令+运行guard，实机回调仍待验。
.NET工程后续用统一MSBuild target，在目标DLL复制完成后weave并check；只有包含生成RPC集合的目标参与，排除工具自身及其依赖，防递归构建。避免覆盖运行中Server.dll，用独立输出；生成物源码仍入库，DLL不提交。
源声明变化仍需要生成元数据；便利层必须检查/刷新源生成物，不能只weave过时发送签名。重生成只写Generated和锁，不改业务源码/不烘焙资源。

## 5. 验收口径
T-W1：真实编译夹具（C#7.3/Release与Debug/带PDB），编织后普通M本地一次/远端只序列化；真实生成receive→validate→业务体，不递归；分支/循环/switch/try/finally/委托体保持行为。
T-W2：未编织正常new与注册必须失败；损坏helper/version/重载/virtual/缺marker/符号问题明确拒绝，失败前后input hash不变；二次weave零diff、check零写入；非空断言。
T-W3：现有生成器和PMR3迁移，ID锁/hash不变，实际字节链通过。校验Reject不断连、Validate失败断连和多播数组快照不弱化。
T-W4：真实Unity2019 API编译及Player管线位置证据；自动回调和真实Mono/IL2CPP运行只可PENDING_USER，禁止声称静态编译已经证明运行。
T-W5：增量.NET build/weave/check/本次build0后run0，全量关键回归；恢复遗漏guard须实际负例命中。
报告Docs/plans/_rpc_weaver_proof.md、_rpc_weaving_editor.md、后续integrated/review。禁止提交/暂存/启动UE/Unity/用户服务；中文源码BOM+CRLF，Unity新增cs/asmdef/目录配唯一meta（无BOM/LF）。


## 6. 集成收口修订
.NET使用CoreCompile后对@(IntermediateAssembly)编织+check，**先织obj再复制bin**，替代早期“复制完成后处理TargetPath”的建议。正式PMR3编译前自动check（仅差异exit2才gen并复验），刷新Compile文件项，覆盖初次无生成文件/新增类；工具嵌套restore/build剥离父TFM/输出等属性。
PDB定位用完整声明类型名（含命名空间/嵌套）+方法名/参数数唯一映射RID（RPC不允许重载），普通非RPC重载不再误拒；Windows盘符相对路径不视为Player绝对路径。Editor API相对路径明确以Unity项目根为基准，不能以dotnet的仓库cwd定位。
当前保留既有PMNet本地分支语义，不执行网络校验；没有移植UE本地ForceValidate函数体/CVar细节。恢复失败会明确报保留备份，进程崩溃/断电下两文件事务不保证，必须经check/重建恢复。

业务体克隆必须保留原MethodImpl实现标志（如Synchronized/NoInlining），已编织check核对入口与体一致；不能只在wrapper持有锁而让远端私有体失去同步语义。


## 7. 自动属性扩展（同一编织事务）
net-property-authoring-contract.md扩展既有格式1：bucket/guard触发RPC或PMReplicated auto-property；PropertySet/RawSet按冻结接口，纯属性类不能当noop。--require-rpcs仍只要求RPC，不把属性计入。RawSet继承原setter MethodImpl并核对；与RPC统一锁/预检/PDB/写盘回滚，无第二写盘器。
