> 已作废：用户否决C++式声明/Implementation分离。当前按net-rpc-weaving-contract.md实现单方法标记+IL编织；本文件仅历史，不得继续实现。

# RPC UE式声明体验契约

状态/测试唯一源仍为 net-architecture-migration.md 末尾「RPC UE式声明体验」。本文件冻结实现接口，不另立状态源。

## 1. 需求与证据
用户明确要求标记普通名声明、调用普通名、额外写 `_Implementation`，而非标记Implementation。UE证据 `_rpc_ue_reference.md`：UPMHeroComponent::ChangeFood头103行与gen.cpp329/373行：普通入口由UHT打包→ProcessEvent→native thunk→校验→Implementation。`_ForceValidate`是项目引擎扩展，不是原生UE特性。

C#7.3无C++分离声明/定义，也无C#9 public partial方法。因此采用生成器专用声明区，不引IL编织、不升级Unity/语言、不改用户函数体。不宣称语法逐字符等同C++。

```csharp
[PMNetworkObject]
public partial class Player : PMNetObject
{
#if PMNET_DECLARATIONS
    [PMServerRpc(Reliability = PMRpcReliability.Reliable,
                 Validator = PMRpcValidator.ForceValidate)]
    public void ServerAttack(int skillId);
#endif
    private void ServerAttack_Implementation(int skillId) { /* 业务 */ }
    private PMRpcValidation ServerAttack_ForceValidate(int skillId)
    { return skillId > 0 ? PMRpcValidation.Accept : PMRpcValidation.Reject; }
}
// 调用：player.ServerAttack(skillId: 1);
```

## 2. 冻结实现
- 只有生成器ParseOptions定义PMNET_DECLARATIONS；Unity/csproj/rsp不得定义。正常编译由同partial生成普通名字入口；生成物内加误定义该symbol的#error守卫。
- 只接受声明区内带标记的无体普通名RPC，原带体写法明确生成错误（不双模式兼容）。标记Implementation/Validate/ForceValidate名字也报错。
- 收集原始方法事实，不改PMDeclModel的稳定IR。规则14：普通编译视图不存在该RPC声明（避免只靠body-null误接收未被#if保护的声明）；有唯一、非static、非abstract、非async、非generic、有体且返回void的同类`<M>_Implementation`。implementation只允许private/protected（可virtual），防外部误调。声明/implementation参数顺序、规范化类型和名字一致；禁止ref/out/in/params/默认参数/泛型RPC及未支持修饰符。校验同伴也核对参数/返回类型/静态与有体，不能靠隐式转换或重载误匹配放行。
- 生成正常入口保留声明可访问性及参数名（支持命名实参），转义关键字标识符。为避免p0/callspace等生成局部与用户参数撞名，可正常入口转发到private PMNet_RpcCall_<M>(p0,...) helper，再保留现有内部模板；helper名只供生成器，不是业务API。
- 生成正常入口保留规范化RPC Attribute元信息。接收入口仍PMNet_RpcInvoke_<M>；描述符MethodName仍普通名；ID常量/属性setter/注册表/校验同伴名不改。不要手编生成文件。
- 收包查活对象、ClassId/RpcId、方向、Owner/IgnoreRpcs→解码完整载荷→验证→Implementation；不得通过普通入口再次发送/递归。
- 普通入口先callspace。远端分支先快照数组/长度预检再EnqueueRemote，然后本地分支，符合UE ProcessEvent Remote先Local后；纯Remote不执行校验/业务体。
- 本地ForceValidate调用其函数体，但忽略Accept/Report/Reject返回、不上报、不断连，再执行Implementation（对应UE项目默认SkipLocalCalls=1，暂不新增CVar）；本地原生Validate=false只跳过Implementation，不发起断连。网络Force三态和原生Validate失败断连维持现有语义。校验异常不吞成通过。不得用序列化往返冒充本地native thunk。
- 仍无运行反射分发。网络继承全特性不在本轮，不声称UHT全部功能对齐。

## 3. 数据兼容与范围
现有13个RPC普通名不改、锁文件字节不变、PMR3Player类hash0xB09BCD1C、整体hash0xE6130FAA不改、ParamLayoutId不改。两网络类13属性/13RPC。属性setter/复制/底层传输不改；大厅TCP不转此模型。
迁移PMR3Player13个实现+真实调用者+Tools消费者；不得把对registry.Invoke/Deliver的负向测试改成直接调用Implementation。E2E夹具同步新声明语法。

## 4. 生成与构建便利层
- CLI --decl-gen/--decl-check参数保持不变，补外部generate-rpcs.bat作为无Unity依赖恢复入口。
- Editor独立asmdef（Editor-only、无Assembly-CSharp引用）监听相关源码变化，debounce/防重入，在不编译不导入时调用外部生成；仅字节变化时Refresh，自己的Generated变化不再触发无限循环。工具失败可见，不以旧产物静默进入Play/Build。
- Play前同步检查，不同步时取消Play并生成，重编译后再进入；通用IPreprocessBuildWithReport使用check-only，不在已选定编译产物的Build中假装生成后即可用。
- CLI调用必须有时间/输出预算且排空stdout/stderr，失败/超时不能宣称成功；不触碰用户进程、不启动Unity、不调用内容烘焙。不依赖已出错的Assembly-CSharp来修复自己。
- 首次打开且没有有效已加载Editor钩子时，不能保证自动恢复；保留外部生成命令与入库生成产物/check门。Unity域重载/自动回调实际行为PENDING_USER，不能用真实API编译冒称实机验证。
- 本轮不借机扩大生成meta管理或删除其它生成集合：现有3份产物/meta保留，新Editor文件配唯一meta。读错误/语法错误/无声明等情况须避免覆盖为假空注册表。

## 5. 验收
T-UX1 scanner/emitter真实C#7.3编译：普通声明→正常入口、缺implementation/错签名/旧body/误宏定义等负例；不能仅字符串断言。
T-UX2 PMR3全迁移+ID锁/hash不变+零旧发送前缀调用；真实R3/R4/R5/R6字节链。
T-UX3 E2E：纯Remote不本地执行业务；本地一次Implementation不递归；Force本地与远端语义；Validate=false本地不远端断连；Multicast先发送快照再本地且数组不变；命名实参编译/运行。
T-UX4 Editor真实Unity2019 API编译、外部生成/check可运行、二次生成零diff；自动事件实际执行仍PENDING_USER。
T-UX5 最终Client/Glue/UnityAPI/Lobby独立输出build及关键回归/旧链禁回归；实际build0后run0才算通过。中文源BOM+CRLF，meta无BOM/LF，禁止提交/暂存/启动服务/UE修改。
