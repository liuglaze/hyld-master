# 自动属性复制／轮询补齐契约

主计划net-architecture-migration.md为状态唯一源。本契约接续net-rpc-weaving-contract.md，不修改UE、不升级Unity2019/C#7.3，不启动Unity或用户服务，不提交。用户批准自然C#自动属性赋值，避免PMNet_Set_*业务调用。

## 1. 作者体验与精确语义
```csharp
[PMReplicated] public int Hp { get; private set; }
[PMReplicated(PMCond.OwnerOnly)] public int Mana { get; private set; }
[PMRepNotify(nameof(Hp))] private void OnRep_Hp() { /* 表现 */ }
// 权威侧正常写：Hp -= damage;
```
- 新自动模式仅支持普通实例auto-property（get;set;，允许访问性不同和初始化器）；拒绝自定义getter/setter、只读、indexer、virtual/abstract/override、static、ref-return、显式接口等不支持形态。原字段模式保留为手动Push/Poll，不悄悄宣称任意stfld已拦截。
- 赋值总会存入新值；变化由EqualityComparer<T>.Default（编译期闭合类型，非业务运行反射扫描）判定。只有变化且HasAuthority且PushBased=true时MarkPropertyDirty；不以值相等跳过真正赋值（string引用也要保留普通赋值语义）。初始化器/构造期原值保留，初始全量不依赖标脏。
- 客户端可以改自己本地副本，但不产生权威上行、不标脏；这不是服务端授权。OnRep仅由现有复制层在提交后统一调用，正常setter不直接通知。
- 数组只自动跟踪整体引用替换；新数组同内容可标脏但线层值比较抑制无效包；同数组引用重赋/原地改元素不保证自动标脏，需显式MarkPropertyDirty或PushBased=false轮询。不扩列表/FastArray/自定义struct。
- 原有OwnerOnly/动态条件、量化、初始全量、丢包/ACK/OnRep隔离规则保留。属性名/类型/ID/掩码/OnRep不改，字段→同名auto-property不改线上摘要。

## 2. 生成器与编织器冻结接口
对每个auto-property P（包括PushBased=false）生成：
```csharp
public const int PMGeneratedPropertyIndex_<P> = <indexBase+slot>;
private void PMNet_PropertyRawSet_<P>(T value) { throw new System.InvalidOperationException("PMNet property has not been woven"); }
private void PMNet_PropertySet_<P>(T value)
{
    bool changed = !System.Collections.Generic.EqualityComparer<T>.Default.Equals(P, value);
    PMNet_PropertyRawSet_<P>(value);
    // 仅PushBased=true发射下一行
    if (changed && HasAuthority) MarkPropertyDirty(PMGeneratedPropertyIndex_<P>);
}
```
- 为兼容已有门禁/外部工具，旧public PMNet_Set<P>对auto-property仅转发P=value，不再次Mark；对原字段保留原赋值+Mark行为。生产迁移后业务不调用旧前缀。
- auto-property Reader必须先解码到临时值，再直接调用RawSet，不走正常setter。Writer读正常纯getter；初始/暂存/活对象Apply共享此Reader，从而无反向标脏、无setter副作用。
- 存在RPC **或** auto-property的类均发射既有version0/Require/readonly guard/BuildEntry门；沿用PMNet_GetRpcWeaveVersion/PMNet_RequireRpcWeave/PMNet_rpcWeaveGate名称与格式1，语义扩展为网络编织门，不改RPC既有CLI行为。
- 编织器预检所有[PMReplicated] PropertyDefinition（精确类型PMNet.PMReplicatedAttribute），确认自动getter/setter及同一CompilerGenerated backing field、非static、原始setter只是赋字段、getter只读该字段。复杂形态明确失败；字段仅旧模式不编织。
- setter IL改为this,value→call PMNet_PropertySet_<P>→ret；RawSet stub改为this,value→stfld <P>k__BackingField→ret（保留setter MethodImpl标志，必要同步标志不能丢）。两个改写方法清理失效sequence point，用独立PDB读取器核对；不改getter、构造初始化器、其它业务体。不重写所有程序集stfld。
- 同类RPC与属性必须在一次完整预检之后统一编织/stamp，沿用并发锁/暂存/PDB验证/回滚。纯属性零RPC类也必须编织和guard；旧--require-rpcs仍表示至少1RPC，不能改为1属性。无--require-rpcs的纯属性程序集不是noop。
- --check核对setter转发、RawSet精确backing field、helper赋值→变化/权威/脏位逻辑与slot常量、Reader只调用RawSet（不能先普通set再RawSet），不能只信stamp。二次weave零diff；缺helper/guard/未知形态失败且输入不变。
- 可新增Tools/PMNetWeaver/PropertyWeaver.cs，接口由本项落地，但不得另起独立写盘器绕原子/符号验证。报告增加属性数，RPC数语义不变。

## 3. 轮询调度修复（与生成/编织可独立）
当前PMReplicationChannel.NeedsWork仅看UnknownBaseline/ForceInclude/Dirty/条件跃迁，未消费PushBased=false。补真正Poll候选：可见且非Push属性即需采样，基线比较仍抑制没变化的数据。纯字段和auto-property均应支持。
- 不可见OwnerOnly/Never等属性不能因永远未知基线占用采样预算；失去可见性必须记录，重新可见强制补发，即使没脏且数值相同。
- Poll使每帧可工作对象变多，必须确保每连接预算公平推进，不因列表前缀常驻Poll让后面对象永久饥饿；Round-robin游标或等效有界方案，可在连接桶维护。
- 不扩重写整个复制层，不改变ACK单调/基线/强制跃迁/多连接确认。当前Push对象内已有整对象采样，允许维持；本轮不承诺数组原地写绝不会因其它候选顺便被发现。
- 不顺手声称实现了完整频率/休眠/相关性；保持现有宿主分工。

## 4. 集成边界与ID
主侧最后把PMR3Player12成员与PMR5Projectile1成员改成同名私有auto-property，保持现有公开只读视图；Publish方法改正常赋值，新增业务示例可使用public get/private set，不为美化现有名字换ID。
锁SHA256=47f0ad21845f5810edb3705dbb7e1bcff4b6752595a83066f470766bd40fece8；PMR3Player hash0xB09BCD1C、总hash0xE6130FAA必须不变。
.NET/Editor既有工具会编全部生产程序集；纯属性场景必须在测试中覆盖（特别PMR5Projectile类）。运行时不引Cecil，不做反射分发。

## 5. 验收
T-P1生成器：auto/public-private/private初始化器、字段旧模式、拒绝自定义/只读/indexer等、ID锁与C#7.3编译。
T-P2真实编织+运行：纯属性及RPC混合、普通赋值/复合赋值、相同值不脏/客户端不脏/authority脏、整体数组替换、收包Reader不脏/OnRep一次、初始Apply、未weave new与注册阻断、二次零diff与损坏setter/helper/RawSet拒绝；不得靠手写setter或反射写值冒充普通赋值。
T-P3复制：完全建立/ACK基线并清脏后，Poll直接改字段/属性仍同步；无变化不发；Push与Poll混合；OwnerOnly/Never/条件翻转；不同连接ACK滞后/丢包；budget=1多对象不饥饿；无权限项不耗尽预算。
T-P4生产：13成员字段→同名auto-property，真实gen/check/锁不变，R3/R4/R5/R6/E2E/复制/编织全回归；源码里业务PMNet_Set调用归零（保留生成与测试夹具）。
T-P5编译：Client/Glue/真实Unity2019/Server独立输出build0；SDK冷/增量gen→weave链。
T-P6真实Unity/Player/IL2CPP仍PENDING_USER，用户选择后置。

所有中文源码BOM+CRLF，Unity新cs配唯一meta无BOM/LF；不覆盖运行Server/bin，子组独立输出；先build0才run。状态只更新主计划；报告在Docs/plans/_property_*.md。
