# auto-property 复制属性：独立复核（实现标志缺陷修 + 真实生成门禁收口）

- 委派类型：comprehensive（实现 + 门禁 + 测试）。
- 契约：`Docs/plans/net-property-authoring-contract.md`（§2 冻结生成接口 / §3 轮询 / §5 T-P2）。
- 上游报告：`Docs/plans/_property_generator.md`（A 组生成器）、`Docs/plans/_property_weaver.md`（B 组编织器）、
  `Docs/plans/_property_polling.md`（C 组轮询）、`Docs/plans/_rpc_weaving_integrated.md`。
- 状态唯一源仍在 `Docs/plans/net-architecture-migration.md`；本文件只是本轮的**复核与证据**，不另立状态源。
- 一句话结论：本轮修掉一个**真实缺陷** —— 编织器把 `PMNet_PropertyRawSet_<P>` 改成 `stfld` 形态时
  「只读不改」实现标志，于是原 setter 带 `[MethodImpl(MethodImplOptions.Synchronized)]` 的属性，
  **收包路径会丢锁**；现已改为「RawSet 继承原 setter 的 ImplAttributes 并在 check 逐位核对」，
  并用「反射标志 + 另一线程持锁的阻塞行为（含无同步属性的负向对照）」给出先失败后修复的证据。
  同时把 `PMPropertyWeaverTest` 的夹具从「按契约**手写**生成物」整体换成「**全部交真实 PMNetGen 生成**」，
  新增 `PMNetWorld + PMReplicationChannel` 的真实字节链、同类 RPC+属性一次 stamp、以及缺 guard /
  丢实现标志等反例。门禁 146/0（旧）→ **223/0**（新），RPC 回归 **314/0** 不回退，生成器门禁 **157/0**，
  生产 ID 锁 SHA256 与 `0xB09BCD1C` / `0xE6130FAA` 均未变。

---

## 1. 实际必读入口（按委派顺序，逐一完整阅读）

| # | 入口 | 读了什么 / 得到什么边界 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 完整（仓根入口，指向客户端/服务端入口与主计划） |
| 2 | `Client/Assets/AGENTS.md` | 完整（Unity 2019.4 / C# 7.3 / 生成入口 / 门禁 / 中文源码 BOM+CRLF / 不启第二个 Unity） |
| 3 | `Server/AGENTS.md` | 完整（不启停用户服务、不覆盖运行中的 `Server/bin`、独立输出） |
| 4 | `Docs/plans/net-architecture-migration.md` | 末尾「自动属性复制开工（用户批准）」段（T-P1..T-P6 表 + A/B/C/D/E 分组）+ 末尾 RPC 交付段与其后的验证表 |
| 5 | `Docs/plans/net-property-authoring-contract.md` | **完整**（§1 作者语义 / §2 生成器与编织器冻结接口 / §3 轮询 / §4 ID 与集成 / §5 验收 / §6 约束） |
| 6 | `Docs/plans/_property_generator.md` | 完整（A 组已落地的生成物形态、§5.1「与 B 组的读码核对」、§6 风险与残留） |
| 7 | `Docs/plans/_property_weaver.md` | 完整（B 组编织/check 口径、§7.1「A 组未落地、夹具手写」的两项缺口） |
| 8 | `Docs/plans/_rpc_weaving_integrated.md` | 完整（**方法实现标志教训**：克隆强制重置 ImplAttributes 会让远端体丢 `Synchronized`；PDB 用**完整类型名**定位；共同写盘/锁） |
| 9 | `windows-shell-compat` skill | 完整（bash 工具按 Bash 解析；需要 PowerShell 用 `pwsh.exe`；引用与路径规则） |

### 1.1 交叉必读（不读会写出「自洽但被编织器拒绝」的产物）

| 文件 | 为什么读 | 是否改动 |
|---|---|---|
| `Tools/PMNetWeaver/PropertyWeaver.cs` | 本组主要修改对象：识别口径、有界抽象求值、编织与 check | **改** |
| `Tools/PMNetWeaver/RpcAssemblyWeaver.cs` | bucket 扩为「RPC 或属性」、统一预检/stamp/原子落盘、PDB 探针、`ValidateInstanceGate` / `VerifyGuardThrowsWhenUnwoven` | 只读，未改 |
| `Tools/PMNetWeaver/Program.cs` | 报告行格式（RPC 方法数 / 属性数 / 实际编织类数） | 只读 |
| `Tools/PMNetGen/DeclEmitter.cs` | 真实生成物形态：槽位常量、RawSet stub、PropertySet、Reader 走 RawSet、门与注册表 | 只读 |
| `Tools/PMNetGen/DeclScanner.cs` | `IsPlainAutoProperty` / `PushBased` 解析（确认访问器带 `[MethodImpl]` 不影响形态判定） | 只读 |
| `Client/Assets/Scripts/PMNet/Replication/PMRepConnectionState.cs` / `PMReplicationChannel.cs` / `PMReplicationTypes.cs` | 真实复制链 API：`AddConnection` / `RegisterObject` / `Tick` / `OnMessage` / `BuildAckMessage` / `TryGetBaselineValue` / `ViewRoleResolver` / `PMRepConditions.Evaluate` | 只读 |
| `Tools/PMReplicationTest/Program.cs` | 真实世界+通道的装置写法（连接替身、生命周期批量、投递与 ACK 回灌）作为新复制链用例的对照 | 只读 |
| `Client/Assets/Scripts/PMR3/PMR3Player.cs` · `PMR3Runtime.cs` | 生产如何把生成物的**私有** `PMNet_OnRepDispatch` 转发成 public 并注册进通道（本夹具照抄同一做法） | 只读 |

### 1.2 文档如何决定首搜边界

- 契约 §2 给出 auto-property 的四个生成成员与 check 口径 ⇒ 改动点限定在 `PropertyWeaver.cs`
  （以及测试工程），不必从业务昵称或宽泛目录猜测。
- `_property_weaver.md` §7.1 明确「A 组落地后应删除夹具手写区、改为断言真实生成物」——本组就是执行这一步。
- `_rpc_weaving_integrated.md` 的「方法实现标志教训」给出一条已被生产证明的**同类**缺陷模式
  （RPC 体丢 `Synchronized`）⇒ 本组据此审查属性侧的同一位置（RawSet 是否继承 setter 标志），命中真实缺陷。
- 委派的写入边界只有 4 个源文件 + 1 份报告 ⇒ 未做任何生产源码/生成器/复制层改动。

---

## 2. 本轮真实修复

### 2.1 真缺陷：`PMNet_PropertyRawSet_<P>` 没继承原 setter 的实现标志 ⇒ 收包丢锁

**缺陷形态。** 契约 §2 要求：`RawSet stub 改为 this,value → stfld <P>k__BackingField → ret`
（**保留 setter MethodImpl 标志**）。旧实现 `BuildRawSet` 却是「记下 RawSet 自己的 ImplAttributes，
改写体之后断言未被改变」——即**只读不改**。生成物的 RawSet 桩默认没有任何实现标志，
于是：

- 本地赋值路径：`set_<P>`（带 `Synchronized`，运行时对 `this` 加监视器锁）
  → `PMNet_PropertySet_<P>` → `RawSet`：**在锁内**；
- 收包路径：`PMNet_Read_<P>` → `RawSet`（刻意绕过 setter，以免反向标脏/触发副作用）：**不在锁内**。

两条路径写的是同一个 backing field ⇒ 同一属性上的「收包写」与「本地赋值」可以并发交错。
旧 check 也看不出问题（它只核对 setter 转发与 RawSet 的 `stfld` 目标）。

**先失败（修前实测，日志 `%TEMP%\pmproperty-weaver-before-fix.log`）。**

```
[FAIL] woven[Debug]：FAIL rawset-inherits-setter-impl-flags :: PMNet_PropertyRawSet_Locked
       ImplAttributes=0，set_Locked ImplAttributes=40（必须逐位相同，否则收包写丢锁）
[FAIL] woven[Debug]：FAIL rawset-locked-blocks-while-monitor-held :: 持锁期间
       PMNet_Read_Locked（→ RawSet）是否被阻塞=False（必须为 true：收包写要与同步 setter 共用同一把锁）
通过 171 项，失败 5 项   （其余 3 项为下述夹具自身缺陷）
```

`0` = IL|Managed 无任何标志；`40` = `Synchronized(32) | NoInlining(8)`。
阻塞观察为 `False` ⇒ 收包路径确实没走那把锁。

**修法（两处，均为窄修，不扩大功能）。**

1. `PropertyWeaver.BuildRawSet`：把 setter 的 `ImplAttributes` **赋给** RawSet，并在赋值后自比对；
   同时**只允许抄标志位**——若 setter 的实现代码类型（IL/Native/Runtime、托管/非托管）与 RawSet 不同，
   明确拒绝（防止把非 IL/托管标志抄到收包路径上）。
2. `PropertyWeaver.RequireSameImplementationFlags`（新增，仅在 `woven==true` 的 check/post-weave 自检里调用）：
   RawSet 与 setter 的 `ImplAttributes` 必须**逐位相同**，否则明确失败并给出修复方向。
   只检查「RawSet 有没有 Synchronized」不够：那会放行「setter 带 NoInlining、RawSet 带 Synchronized」这类分叉；
   契约口径是「保留 setter 的 MethodImpl 标志」。

**修后证据（同一条用例，日志 `%TEMP%\pmproperty-weaver-after-fix.log`）。**

```
[woven[Debug]] PASS locked-setter-impl-flags-preserved :: set_Locked ImplAttributes=40（期望含 Synchronized=32）
[woven[Debug]] PASS rawset-inherits-setter-impl-flags :: PMNet_PropertyRawSet_Locked ImplAttributes=40，
                set_Locked ImplAttributes=40（必须逐位相同，否则收包写丢锁）
[woven[Debug]] PASS plain-rawset-not-synchronized :: PMNet_PropertyRawSet_Hp ImplAttributes=0
                （无 [MethodImpl] 的属性不得被强行加锁 —— 防止「给所有 RawSet 硬塞 Synchronized」蒙过断言）
[woven[Debug]] PASS monitor-hold-observable :: 另一线程持锁可观测（LockHeld=True，TryEnter(0) 拿到锁=False）
[woven[Debug]] PASS monitor-released-after-probe :: 放锁后当前线程持有监视器=False
[woven[Debug]] PASS rawset-locked-blocks-while-monitor-held :: 持锁期间 PMNet_Read_Locked（→ RawSet）是否被阻塞=True
[woven[Debug]] PASS locked-setter-shares-monitor :: 持锁期间 BusinessSetLocked（→ setter）是否被阻塞=True
[woven[Debug]] PASS rawset-unlocked-not-blocked :: 持锁期间 PMNet_Read_Hp（→ 无 Synchronized 的 RawSet）
                是否被阻塞=False（负向对照，必须为 false：观察手段能区分有无同步标志）
```

行为探针的做法：夹具里有一个 `[MethodImpl(MethodImplOptions.Synchronized)] void HoldMonitorUntilReleased()`
（进入即置位、等信号才返回），由**另一个线程**调用它把 `this` 的监视器拿在手里；主线程随后
分别在**新线程**上执行「生成物的收包 Reader」「业务 setter」和「无同步属性的收包 Reader」，
以 750ms 内是否完成作为「是否被阻塞」的观测值。`monitor-hold-observable` 是这三条观测的**前置校验**
（若 `Monitor.TryEnter(this, 0)` 反而成功，说明锁根本没被拿住，后面三条不成立）。
`rawset-unlocked-not-blocked` 是**负向对照**，用于证明该观测手段真的能区分有无同步标志。

**check/weave 的拒绝路径（旧版产物形态）。** 编织器每次都会把标志写齐，因此**源码级反例不存在**；
能造出这种程序集的只有旧版工具产物或手工篡改。为此新增一个**忠实反例**链：

1. 正常编织（stamp=1，RawSet 与 setter 都是 `40`）；
2. 用工具自身的依赖 `Mono.Cecil`（在 `%TEMP%` 沙箱里临时生成一个最小篡改工程，引用工具输出目录的
   `Mono.Cecil.dll`；Cecil 只是工具依赖，不进运行时）把 `PMNet_PropertyRawSet_Locked` 的
   `ImplAttributes` 改回 `IL`，复现「旧版编织产物丢锁」；
3. 删掉 PDB（篡改不带符号），避免绕到无关的符号分支；
4. 断言 `--check` 与 `--weave` **都必须失败**且失败原因含「实现标志与 setter 不一致」，且 DLL 未被改写、无残留。

实测输出：

```
=== 13. 负例：收包 RawSet 丢 setter 实现标志（旧版产物形态）⇒ weave/check 都必须拒绝 ===
  [info] before impl=40
         after  impl=0
（该 section 全绿 ⇒ --check/--weave 均按新断言拒绝，且零写）
```

### 2.2 测试证据纠正：夹具不再「手写生成物」，全部交真实生成

**问题（委派指出、B 组报告 §7.1 自述）。** 旧 `Fixture.cs` 里有一整段 `GEN-EXCLUDE` 区块，
**按契约手写**了 `PMGeneratedPropertyIndex_<P>` / `PMNet_PropertyRawSet_<P>` / `PMNet_PropertySet_<P>` /
`PMNet_Read_<P>` / 版本门 / 实例 gate / `PMNet_BuildEntry` / `CollectLifetimeReplicatedProps` /
`PMNet_OnRepDispatch`。它只能证明「weaver 认识这一种形状」，不能证明「**真实生成物**恰好落在
weaver 可接受的形状内」——而这正是跨组集成唯一需要证明的东西。

**做法。**

- `Fixture.cs`：删除全部手写生成物，只保留
  （a）复制属性声明（含 `[MethodImpl]` 同步 setter、`OwnerOnly`、`PushBased=false`、数组、初始化器）、
  （b）业务普通赋值（`Hp = value` / `Hp -= amount`，零 `PMNet_` 前缀调用）、
  （c）驱动、（d）RPC 夹具、（e）同类「RPC + 属性」混合夹具。
  唯一的 1 行胶水是 `PMDispatchOnRep`，把生成物的**私有** `PMNet_OnRepDispatch` 转发成 public ——
  与生产 `PMR3Player.PMR3DispatchOnRep` / `PMR5Projectile.PMR5DispatchOnRep` **逐字同一做法**，
  真正的 switch 分发表仍是生成物。
- `Program.cs`：生成输入改为**整份夹具源码**（不再剔除任何「生成物区」）；
  「无手写生成物」升级为**硬门**（第 0 节逐条断言夹具里不存在手写 helper / 槽位常量 / Reader /
  guard / 注册入口 / 注册表 / 分发表 / 版本门的**声明形态**）。
- 负例注入边界按委派要求收紧：**生成物层负例全部注入 `%TEMP%` 里真实生成产物的副本**
  （`Dictionary<文件名, 文本>`，先统一成 LF 再注入）；
  只有 1 个「声明形态」负例（`corrupt-setter`：把 auto-property 换成自定义访问器）改的是
  `%TEMP%` 里的夹具源码副本 —— 该形态本身就不该由生成器产出（生成期规则 14 会先拒），
  这一点在报告里显式登记，不作模糊处理。
- 纯属性零 RPC 场景改为**另一份独立的真实生成集合**：把属性区块切成独立源码 → 独立 `--decl-gen`
  （独立 out-dir 与 ID 锁）→ 断言生成物含槽位常量/RawSet/PropertySet、**不含任何 `PMGeneratedRpcId_`**、
  注册表 `GeneratedClassCount = 1` → 再编译/编织/check。
- 缺 helper 必须失败，**不得用字符串模板补**：`missing-rawset-helper`、`missing-property-set-helper`
  两个反例 + 第 0 节的「夹具不含手写 helper」硬门共同保证。

### 2.3 新增的真实复制链（PMNetWorld + PMReplicationChannel）

委派要求「增加实际 `PMNetWorld + PMReplicationChannel` 字节往返 / ACK / OwnerOnly / 初始全量 /
Reader 不脏 / OnRep 一次，不能反射直接调用 OnRep 冒充复制层」。新增入口
`PropertyDriver.RunReplication()`，在**已编织的夹具程序集内部**跑真实回路：

`PMNetGeneratedRegistry.RegisterAll()`（生产注册路径，未编织会在此被 guard 拒）
→ 权威世界 `Spawn`（`Role=Authority`）→ 业务普通赋值初值
→ 两个连接（拥有者/非拥有者，真实决策点 `ViewRoleResolver` 决定 Autonomous/Simulated）
→ 真实生命周期批量投递建副本
→ `sender.Tick()` → 逐条 `receiver.OnMessage(字节)` → `receiver.BuildAckMessage()` → 回灌 `sender.OnMessage(ACK)`。

实测（日志两配置各一遍，均为 PASS）：

```
PASS rep-initial-full-state :: applied(owner/observer)=1/1 Hp=100 Mana=50 Polled=7 Inited=42
PASS rep-owner-only-sent-to-owner :: 拥有者连接收到的槽位=[0,1,2,3,4,5]
PASS rep-owner-only-not-sent-to-nonowner :: 非拥有者连接收到的槽位=[0,1,2,3,5] Mana=0
PASS rep-reader-apply-not-dirty :: owner.Dirty=False observer.Dirty=False
PASS rep-onrep-dispatched-once :: owner.Copy.OnRepCount=1（Hp 有 OnRep，本轮只发一次）
PASS rep-no-repeat-without-change :: owner/observer 待发消息数=0/0
PASS rep-ack-advances-baseline :: Hp 基线是否已建立=True（字节数=1）
PASS rep-change-after-ack-resent :: 本轮槽位=[2] Hp=101 OnRepCount=2
PASS rep-poll-after-ack-without-dirty :: 本属性脏位=False 本轮槽位=[0] Polled=9
        （轮询必须发 Polled，且未被改动的 Hp 必须仍被基线比较抑制）
```

槽位映射（真实生成物实测，PropertyId 升序）：`Polled=0`（`PushBased=false`）、`Inited=1`、
`Hp=2`（`OnRepMethodId=1`）、`Locked=3`、`Mana=4`（`OwnerOnly`）、`Blob=5`。
因此「非拥有者收到的 `[0,1,2,3,5]`」恰好缺 `4=Mana`，而 `Mana` 值仍为 `0` —— 双重证据。
`rep-onrep-dispatched-once` 的 OnRep 由**通道提交路径**派发（`PMReplicationChannel.DispatchOnRep`
→ 注册进去的 `PMDispatchOnRep` → 生成物 `PMNet_OnRepDispatch`），不是反射直接调分发表；
隔离低层用例（`onrep-dispatch-once`）保留，但不再冒充复制层。

### 2.4 同类「RPC + 属性」一次 stamp 与失败零写

新增 `PMWeave.Mixed.MixedFixture`：**同一个类**里 1 条 RPC + 2 个 auto-property（含 `OwnerOnly`）。
它与属性是「同一个类」，因此只有「同一轮预检、同一个 stamp、同一次落盘里两侧都织好」才能同时满足：

```
PASS mixed-stamp-is-one :: PMNet_GetRpcWeaveVersion() = 1
PASS mixed-rpc-body-woven :: 私有业务体 PMNet_RpcBody_ServerMixed 存在=True
PASS mixed-property-woven-same-pass :: Value=7 dirty=True（与 RPC 同一次 stamp 的属性侧）
```

混合装配的 `--weave` 报告实测：`RPC 方法数：2`、`auto-property 复制属性数：8`、
`本次实际编织的属性数：8`、`本次实际编织的类数：3`。

失败零写：`mixed-property-wrong-index` 反例打在**混合类**的真实生成产物副本上
（把 `MarkPropertyDirty(PMGeneratedPropertyIndex_Value)` 改成 `...OwnerValue`）⇒ `--weave` 必须失败，
且 **DLL/PDB hash 不变、长度不变、无暂存残留**，`--check` 也必须失败 ——
即「属性侧坏掉不会留下 RPC 已织、属性没织的半成品」（属性与 RPC 共用同一把锁、同一个原子落盘器）。

### 2.5 反例清单（16 条链，全部要求「明确失败 + hash 不变 + 无残留」）

| # | 反例 | 注入对象 | 期望关键字 |
|---|---|---|---|
| 1 | `corrupt-setter` | 夹具源码副本（**声明形态层**，唯一一条） | `backing field` |
| 2 | `rawset-not-stub` | 真生成物副本 | `占位 stub` |
| 3 | `helper-wrong-index` | 真生成物副本 | `槽位` |
| 4 | `helper-no-authority` | 真生成物副本 | `没有权威` |
| 5 | `helper-ignores-change`（总是标脏） | 真生成物副本 | `值没有变化` |
| 6 | `reader-uses-setter` | 真生成物副本 | `普通 setter` |
| 7 | `reader-setter-then-rawset`（先普通 set 再 RawSet） | 真生成物副本 | `普通 setter` |
| 8 | `missing-rawset-helper` | 真生成物副本 | `缺少生成物 helper` |
| 9 | `missing-property-set-helper` | 真生成物副本 | `缺少生成物 helper` |
| 10 | `missing-registration-slot` | 真生成物副本 | `注册` |
| 11 | `pushbased-mismatch` | 真生成物副本 | `PushBased` |
| 12 | `missing-instance-gate`（缺 guard：实例侧） | 真生成物副本 | `实例 gate` |
| 13 | `missing-buildentry-guard`（缺 guard：注册侧） | 真生成物副本 | `没有调用` |
| 14 | `mixed-property-wrong-index`（混合类槽位串位） | 真生成物副本 | `槽位` |
| 15 | RawSet 写错 backing field | 已编织 DLL 的 IL 字段 token（4 字节） | `backing field` |
| 16 | RawSet 丢 setter 实现标志（旧版产物形态） | 已编织 DLL 的元数据（Cecil 篡改） | `实现标志与 setter 不一致` |

### 2.6 夹具/门禁自身的两个缺陷（自曝，如实记录）

首次跑新门禁时暴露的是**测试代码自己的**问题，不是产品缺陷，但照实记录：

1. `corrupt-setter` 的注入锚点用了 `\n`，而夹具源码是 CRLF ⇒ 锚点不命中、反例被静默跳过。
   已改为 CRLF 锚点（生成物副本仍统一成 LF 后注入，两者分开处理）。
2. `RemoveLineContaining` 用「上一个换行符**本身**」作为行首起点，会把上一行的换行一起删掉，
   导致生成物里上一行与下一行粘连（实测 `error CS1513: 应输入 }`）。
   已改为「上一个换行符之后」为行首、行尾含换行，并保留「锚点必须唯一」的硬约束。

---

## 3. 测试与证据（本次 build 退出 0 之后才 run）

### 3.1 命令

```bat
rem 1) 构建（含工具；两者由 ProjectReference 连带构建）
dotnet build Tools/PMPropertyWeaverTest/PMPropertyWeaverTest.csproj -c Release

rem 2) auto-property 门禁（真实生成 + 真实编译 + 真实编织 + 真实赋值/锁/复制链执行）
dotnet Tools/PMPropertyWeaverTest/bin/Release/net8.0/PMPropertyWeaverTest.dll

rem 3) RPC 侧回归（必须仍 314/0）
dotnet Tools/PMNetWeaverTest/bin/Release/net8.0/PMNetWeaverTest.dll

rem 4) 生成器门禁（必须仍 157/0）
dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll

rem 5) 生产声明/ID 锁逐字节回归（只读，不写生产产物）
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check Client/Assets/Scripts/PMR3 ^
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
```

### 3.2 结果

| 门禁 | 结果 | 说明 |
|---|---|---|
| `PMPropertyWeaverTest`（本组） | **223 / 0** | 改前基线 146/0（34 次 CLI、15 次编译）；现 46 次 CLI、22 次真实编译；沙箱本次唯一（`%TEMP%\pmproperty-weaver-test-<pid>-<ticks>`，通过后清理，失败保留现场） |
| `PMNetWeaverTest`（RPC 回归） | **314 / 0** | 与改前一致，未回退（该夹具 0 个 `[PMReplicated]` ⇒ 属性改动对它零影响） |
| `PMDeclCheck`（生成器） | **157 / 0** | 未改生成器，仅确认无交叉影响 |
| 生产 `--decl-check` | **exit 0** | `PMR3Player ClassId=405815557 hash=0xB09BCD1C`、13 属性 / 13 RPC、`PMR5Projectile hash=0xD4CB0B42`；ID 锁 SHA256 = `47f0ad21845f5810edb3705dbb7e1bcff4b6752595a83066f470766bd40fece8` 未变 |
| C# 7.3 真实编译 | Debug + Release 各一遍 | 夹具工程 `netstandard2.0` + `LangVersion 7.3` + portable PDB；混合链两配置都跑「未编织负例 → weave → check → 幂等 → 行为/锁 → 复制链」 |
| 关键日志 | `%TEMP%\pmproperty-weaver-before-fix.log`（修前 171/5）、`%TEMP%\pmproperty-weaver-after-fix.log`（修后 223/0） | 失败现场与日志均不在仓库内（不改生产 `Generated`、不覆盖用户 `Server/bin`） |

### 3.3 新建用例数（防止被静默删掉）

`CheckDriver` 对每个入口断言「用例集合**恰好**等于期望集合」。当前规模：
未编织 3、已编织 25（原 17 + 实现标志 3 + 监视器 5）、复制链 9、混合类 3；
反例 14（生成物/声明层）+ 2（IL 元数据层）= 16 条链；
另加第 0 节「无手写生成物」硬门、纯属性独立生成集合断言、混合类一次 stamp 报告断言等。

---

## 4. 与其它组的接口核对（本轮由「读码声明」升级为「实测」）

| A 组报告的原声明 | 本轮结论 |
|---|---|
| §5.1「注册表三来源一致（`outProps.Add(index, cond, push)` 与常量/属性一致、`ChangeMaskBitCount` == 条数）」——当时是**读码**核对 | **已实测**：真实生成物的 `outProps.Add(PMGeneratedPropertyIndex_<P>, ...)` 通过 `ValidateRegistration`、`--check` 与二次 weave 幂等；`PMGeneratedPropertyIndexBase=0`、`ChangeMaskBitCount=6`（属性夹具）/`2`（混合类）自洽 |
| §5.1「Reader 只调 RawSet（不得先普通 set 再 RawSet）」 | **已实测**：真实生成物的 `PMNet_Read_Hp` 只调 `self.PMNet_PropertyRawSet_Hp(pmValue)`；`reader-uses-setter` 与 `reader-setter-then-rawset` 两条反例都被拒 |
| §5.1「`PushBased=false` 只发射 changed + RawSet 两行，不读 `HasAuthority`」 | **已实测**：`pushbased-false-not-dirty` + `rep-poll-after-ack-without-dirty`（脏位为空仍被轮询发出） |
| §6「生成物能过 B 组预检」是读码结论、非实跑 | **已实测**：真实生成物 + 真实 CLI weave + 真实执行全链通过（本组即为该实测） |
| 旧 `PropertyWeaver.ReadRegistration` 只认 `ldc.i4` 常量、不处理 `ldsfld` | **不是缺陷**：C# `const int` 在使用点被内联成立即数，注册表 IL 里就是 `ldc.i4.N`；实测真实生成物注册校验通过。若将来把槽位常量改成 `static readonly`，才会命中该分支 —— 已在本报告登记为**潜在口径**，本轮不扩功能 |
| B 组 §7.3「OnRep『由真复制层一次』未做端到端」 | **本轮补上**：`rep-onrep-dispatched-once` 走真实通道提交路径；隔离低层用例保留 |
| B 组 §7.2「`PushBased=false` 真实采样/公平预算」属 C 组 | 未扩范围；本轮只断言「轮询候选 + 基线比较」在真实链上生效（`rep-poll-after-ack-without-dirty`），未声称公平预算/相关性/频率 |
| B 组 §7.1（a）（b）两项「A 组待落地」 | 已由 A 组落地且本轮实测（Reader 走 RawSet、纯属性零 RPC 类带门） |

---

## 5. 未验证边界与风险（不声称）

1. **旧版工具产物会被新版 `--check` 明确拒绝**：任何在本次修复**之前**由旧编织器产出的、
   含 auto-property 且 setter 带 `[MethodImpl]` 的程序集，其 RawSet 没有该标志 ⇒
   新版 `--check` 会在预检阶段报「实现标志与 setter 不一致」。**这是正确的失败**（那种产物确实丢锁），
   修复方式是重新 `--weave`。本轮**未**修改任何生产 DLL / `Generated` / `Server/bin`，
   也未在 `Client`/`Server` 上执行生产 weave，因此生产侧是否存在这种陈旧产物未核（属主侧构建链范围）。
2. **Unity / Player / IL2CPP 仍 `PENDING_USER`**：未启动 Unity、未抢工程锁、未做 Player 阶段验证。
   本轮所有「真实」均在 **netstandard2.0 + C# 7.3 真实编译 + 真实 CLI 编织 + `AssemblyLoadContext` 执行**
   的口径内；Mono/IL2CPP 对 `MethodImplOptions.Synchronized` 与 ALC 行为不由此推定。
3. **锁行为探针依赖运行时真的实现 `Synchronized`**：CoreCLR 实测有效（另一线程持锁 ⇒ 收包路径阻塞；
   无标志 ⇒ 不阻塞），并配了 `monitor-hold-observable` 前置校验与 `rawset-unlocked-not-blocked` 负向对照；
   探针用 750ms 窗口，极慢机器上仍可能受调度影响（对照项是「必须不阻塞」，被误判的方向是把
   未阻塞读成阻塞 ⇒ 会报 FAIL 而非假绿，属可接受方向）。
4. **`RequireSameImplementationFlags` 的拒绝路径**只能被「旧版产物 / 元数据篡改」触发，
   源码级反例不存在（编织器每次都会写齐）。本组用 Cecil 篡改构造了忠实反例（第 2.1 节），
   但**篡改程序集**本身不是生产形态；它证明的是「该断言会拒绝丢标志的程序集」，不是
   「生产上存在这种程序集」。
5. **未执行生产 weave / 未编译 Client 与 Server 本体**：委派明确禁止。因此
   「主侧已迁移的 13 个生产成员（`PMR3Player` 12 + `PMR5Projectile` 1）在新编织器下真的织出带锁语义的
   RawSet」是**未实测边界** —— 需要主侧在允许构建的时机跑一次真实 `--decl-gen + --weave + --check`。
   本轮只证明了：生产声明/ID 锁逐字节未漂移（`--decl-check` exit 0、两个类 hash 与 13/13 计数不变），
   以及「同类 RPC+属性」在夹具上的一次 stamp 与零写。
6. **轮询公平预算 / 相关性 / 频率 / 休眠**未触碰（C 组范围）；**数组原地写**仍不承诺自动标脏（契约允许）。
7. **环境观察（非本组动作）**：作业期间观测到 `Client/Library/**` 与 `Client/Assets/Scripts/PMR3/**`
   被外部进程改写（时间戳落在本会话内）。本组未启动 Unity、未读写 `Client`/`Library`、
   未执行任何生产生成或 weave；这些写入来自工作副本上的其它进程（主侧/已打开的 Unity）。
   如实登记，避免把它当成本组产物。

---

## 6. 修改文件（严格限于委派写入边界）

| 文件 | 改动 |
|---|---|
| `Tools/PMNetWeaver/PropertyWeaver.cs` | `BuildRawSet` 改为**继承** setter 的 `ImplAttributes`（并只允许抄标志位）+ 赋值后自比对；新增 `RequireSameImplementationFlags` 并在 woven 分支调用；注释与 check 清单同步更新 |
| `Tools/PMPropertyWeaverTest/Fixture.cs` | **删除全部手写生成物**（槽位常量/helper/Reader/guard/BuildEntry/注册表/OnRep 分发表）；只保留属性声明 + 业务普通赋值 + 驱动 + RPC 夹具 + 混合夹具；新增同步 setter 属性 `Locked`、监视器探针、`PMDispatchOnRep`（1 行生产同款转发）、真实复制链驱动 |
| `Tools/PMPropertyWeaverTest/Program.cs` | 生成输入改为整份夹具源码；新增「无手写生成物」硬门；负例改为注入真生成产物副本（+1 条声明形态层，显式登记）；新增复制链 / 混合类 / 缺 guard / 丢实现标志（Cecil 篡改）等入口与用例清单；`CheckDriver` 加 `echo` 让新增证据可读；沙箱改为唯一目录 + 清理重试 |
| `Tools/PMPropertyWeaverTest/PMPropertyWeaverTest.csproj` | 更新过时注释（不再声称「按契约手写生成物形状」） |
| `Docs/plans/_property_weaver_review.md` | 本报告 |

未触碰（明确保持只读）：`Tools/PMNetWeaver/RpcAssemblyWeaver.cs`、`Tools/PMNetWeaver/Program.cs`、
`Tools/PMNetGen/**`、`Client/**`、`Server/**`、`Docs/plans/net-*.md`、`Docs/plans/_property_generator.md`、
`Docs/plans/_property_weaver.md`、`Docs/plans/_property_polling.md`、`Docs/plans/*ids.json`。

## 7. 约束遵守

- 未提交 / 未暂存 / 未回退 / 未 `svn update`；未启动 Unity、未启停用户服务；未编译 UE。
- 未执行生产 weave、未改 `Library`、未覆盖运行中的 `Server/bin`；一切夹具构建与执行落在
  **唯一 `%TEMP%` 沙箱**与工程自身 `bin/obj`。
- 未使用 `cli_delegate` / 递归委派；本进程即执行后端。
- 中文源码统一 **UTF-8 BOM + CRLF**（逐字节核对：4 个 `.cs`/`.csproj` 全部 BOM=True、lone LF=0）。
- PDB 处理沿用既有路径：被改写方法按**完整类型名 + 方法名 + 参数数**定位 RID、清空 sequence point、
  由 `RpcAssemblyWeaver.WriteAtomically` 的**同一个**原子写盘器 + 同一把并发锁 + 独立 PDB 读取器核对
  （本轮未新增第二个写盘器，也未改 PDB 定位代码）。
