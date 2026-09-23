# 自动属性复制：主侧整合与验收

接口冻结：net-property-authoring-contract.md。状态唯一源：net-architecture-migration.md末尾。

## 交付
- `[PMReplicated]` auto-property正常赋值自动Push标脏；复合赋值也走setter。赋值总存入新值，只有值变化且HasAuthority且PushBased才标脏。setter不调用OnRep。
- 收包Reader解码后直接RawSet，绕普通setter；初始状态、暂存、实际提交都不反向标脏，OnRep仍由复制层提交后统一分发。
- 纯属性零RPC类也必须编织，构造/注册守卫与RPC共用；`--require-rpcs`仍只认RPC，不拿属性数冒充。setter/RawSet使用共同预检、PDB验证、锁和回滚。
- 生产PMR3Player12成员与PMR5Projectile1成员改为同名private auto-property，公开只读视图不变。Publish方法普通赋值；认证uid由PMR3Player.InitializeIdentity从可信调用点赋值。生产业务不再调用PMNet_Set前缀，生成物的旧兼容setter仍保留。
- 保留字段手动模式；PushBased=false的字段/属性均真正进入轮询。数组只跟踪引用替换，不承诺原地元素/同引用重赋自动标脏；需要显式MarkPropertyDirty或轮询。无运行时反射扫描、Cecil不进runtime。

## 核证据与复核修复
1. 生成器原本把PushBased具名实参当条件，并未正确写入false；已真实解析并补负例。普通属性严格要求auto-get/set，不支持形态硬失败。
2. 初版属性测试曾手写生成形状，主侧不将它作为最终整链验收；已删除，全部属性/混合/纯属性集合由真实PMNetGen生成，缺helper即失败。新增真实World+Channel字节往返、初始全量、ACK、OwnerOnly、OnRep由通道一次调用。
3. 属性RawSet初版未继承setter实现标志，收到数据时会丢Synchronized锁；真实跨线程持锁反例先失败，复制MethodImpl并逐位核对后通过。无锁属性保留负向对照，不给所有属性强加锁。
4. 轮询补齐五类问题：未消费PushBased=false、不可见未知基线长期占预算、可见性loss未记、不可见槽位仍调用Writer、固定表头预算导致饥饿。
5. 独立复核再修两处：Dirty/ForceInclude同样按可见性过滤；预算顺延前立即记住visibility loss，避免条件恢复后无触发源、客户端永久旧值。游标表长变化反例未发现额外缺陷，未盲改。

## 主侧本次测试
全部build0之后才run，独立输出Tools各工程/bin/property-main；日志Tools各工程/property-main.log。

| 项 | 结果 |
|---|---|
| PMPropertyWeaverTest | 223/0，真实生成/编译/编织/复制链及负例 |
| PMNetWeaverTest | 原RPC314/0（57次CLI、22次真实夹具编译） |
| PMDeclCheck | 157/0 |
| PMReplicationTest | 364/0，14/14缺陷注入命中 |
| PMNetE2E | 209/0 |
| PMR3Runtime / Integration | 180/0、143/0 |
| PMR4Network / Integration | PASS、113/0 |
| PMR5Declaration / Network | 139/0、PASS |
| PMR6Declaration / Network | 235/0、385/0 |
| PMCombatCore / NetSession / NetWorld | 1699/0、405/0、333/0 |
| PMLegacyRetirementTest | 28静态+6负例通过 |
| PMNetWeavingEditorTest | 26/0 |
| Client / Glue / R4Unity / EditorWeaving / R6Network 编译门 | 全部0错误，真实Unity API门未执行Unity |
| Server独立输出 | build0，Tools/PMPropertyWeaverTest/bin/lobby-main（不写用户Server/bin） |
| python Tools/test_rpc_build_pipeline.py | 6/0；含现有13auto-property的冷/增量/元数据变更/新增类场景 |

最终decl-check通过：两网络类、13属性、13RPC；12+1个真实PropertySet helper。
ID锁SHA256始终`47f0ad21845f5810edb3705dbb7e1bcff4b6752595a83066f470766bd40fece8`，
PMR3Player类摘要`0xB09BCD1C`、全局`0xE6130FAA`均不变。

## 边界与后续
- Unity真实自动回调、Player/Mono/IL2CPP与双客户端仍PENDING_USER，未启动Unity/服务，未提交/暂存，未改源资源。
- 自动属性支持已列类型与纯get/set；不扩自定义setter/网络继承/FastArray/结构体。旧字段仍要手动标脏或选择Poll。
- Poll每次候选采样，但无变化不发送；现仍整对象扫描，混合对象可能顺便发现未标脏Push字段变化，不把这种偶然发现当数组自动跟踪承诺。
- 未因此完成频率/休眠/完整相关性或原框架四区审计。旧功能欠账不消失。
- 较早报告中的“A未落地/夹具手写/RawSet只读不改实现标志”均被后续review和本报告覆盖。
