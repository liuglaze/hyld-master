# auto-property 复制属性编织（PMNetWeaver 扩展）交付与验证

契约：`Docs/plans/net-property-authoring-contract.md`（§2 冻结生成接口、§3 轮询、§5 T-P2）
继续自：`Docs/plans/net-rpc-weaving-contract.md`、`_rpc_weaving_integrated.md`
状态唯一源：`Docs/plans/net-architecture-migration.md`（本文件只是执行证据，不另立状态源）

## 0. 本次范围

用户批准的「自然 C# auto-property 赋值即自动 Push 标脏」分两组并行落地：

- **A 组 = 生成器**（`Tools/PMNetGen`）：发射 `PMGeneratedPropertyIndex_<P>` / `PMNet_PropertyRawSet_<P>` /
  `PMNet_PropertySet_<P>`，并把收包 Reader 改成只走 RawSet。
- **B 组 = 本次交付**（既有 Weaver 扩展 + 真实夹具）：实现编织与门禁，**不改生成器、不改生产源码、
  不改复制 runtime**。

本文件只登记 B 组。`--require-rpcs` 的语义、RPC 既有 CLI 行为、13 个生产成员/ID/掩码/OnRep 与
`0xE6130FAA` 摘要都不在本次改动范围内。

## 1. 交付物（严格在本组写边界内）

| 文件 | 性质 | 作用 |
|---|---|---|
| `Tools/PMNetWeaver/PropertyWeaver.cs` | **新增** | auto-property 的收集 / 预检 / 编织 / 校验（隔离在独立文件，不把属性逻辑塞进 RPC 编织器） |
| `Tools/PMNetWeaver/RpcAssemblyWeaver.cs` | 修改 | bucket 扩为「RPC 或 auto-property」；统一预检/编织/stamp/PDB 探针/**同一把锁与同一个原子落盘器**；报告加属性计数 |
| `Tools/PMNetWeaver/Program.cs` | 修改 | 打印属性计数；`--help` 说明「auto-property 不计入 `--require-rpcs`」 |
| `Tools/PMPropertyWeaverTest/PMPropertyWeaverTest.csproj` | **新增** | 门禁工程（net8.0；只保证两个工具被构建，不引用其程序集） |
| `Tools/PMPropertyWeaverTest/Program.cs` | **新增** | 真实编译 / 真实 CLI 编织 / 真实执行 + 负例注入的驱动 |
| `Tools/PMPropertyWeaverTest/Fixture.cs` | **新增** | 夹具源：RPC 夹具（真实 PMNetGen 输入）+ auto-property 夹具与驱动 |
| `Docs/plans/_property_weaver.md` | **新增** | 本报告 |

没有新增第二个写盘器：属性与 RPC 共用 `RpcAssemblyWeaver.WriteAtomically`（同一个暂存 + 备份 +
回滚 + 并发锁 + 独立 PDB 读取器），属性只**追加** `PdbProbe`。

## 2. 冻结接口的识别口径（遇到未知就明确失败）

对每个带 `PMNet.PMReplicatedAttribute`（**精确全类型名**）的 auto-property `P`，编织器要求存在：

```
public const int  PMGeneratedPropertyIndex_<P>        // 必须是 const int（Constant 非空）
private void      PMNet_PropertyRawSet_<P>(T)         // 编织前：抛 System.InvalidOperationException 的 stub
private void      PMNet_PropertySet_<P>(T)            // 冻结语义（见 §3）
private static void PMNet_Read_<P>(PMNetObject, PMNetReader)   // 生成物收包 Reader（既有命名）
```

并校验：

- **纯 auto-property**：不允许索引器 / static / virtual / abstract / 只读 / `ref`-return / 指针 /
  显式接口实现（访问器名必须是 `get_<P>` / `set_<P>`）。
- **CompilerGenerated backing field**：`<P>k__BackingField` 必须存在、`private`、非 static、
  类型与属性一致、带 `System.Runtime.CompilerServices.CompilerGeneratedAttribute`。
  （实现中踩过的坑：Roslyn 的名字**含尖括号** `.ctor` 之前写成 `Pk__BackingField` 会误报
  「这不是 auto-property」——已按真实名字修正。）
- **getter 必须纯**：有界抽象求值判定它只做「读 `this` 上自己的 backing field → 返回该值」，
  同时容忍 Debug 的「值先落局部变量再返回」（`ldfld; stloc; br; ldloc; ret`）。
- **槽位/条件/PushBased 三来源一致**：`[PMReplicated]` 上的 `Condition` / `PushBased`（唯一权威来源）
  必须与生成物注册表 `CollectLifetimeReplicatedProps` 里**同一槽位**那条 `outProps.Add(index, cond, push)`
  一致；槽位必须唯一、落在注册表范围内；`PMGeneratedPropertyIndexBase` 与注册表最小槽位一致；
  `PMGeneratedChangeMaskBitCount` 与注册条数一致。
  —— 理由：常量与注册表一旦分叉，运行期 `MarkPropertyDirty(常量)` 会**静默串到别的属性**
  或打到没有属性的伪位上（后者永远清不掉 ⇒ 对象终身每 Tick 空扫）。
- **字段旧模式不改**：`[PMReplicated]` 字段完全不参与（仍由业务显式 `PMNet_Set_<P>` 标脏），
  也不宣称「任意 `stfld` 都被拦」。这一点用生产源码现状反证：`PMR3Player`/`PMR5Projectile`/
  `PME2E`/`PMDeclCheck` 夹具里**全部** `[PMReplicated]` 都挂在字段上，因此本次改动对现有
  构建的编织结果为零影响。

## 3. 编织只改两个方法（并保留实现标志）

| 目标 | 编织前 | 编织后 |
|---|---|---|
| `set_<P>` | `this, value → stfld <P>k__BackingField → ret` | `this, value → call PMNet_PropertySet_<P> → ret` |
| `PMNet_PropertyRawSet_<P>` | `ldstr …; newobj InvalidOperationException(string); throw` | `this, value → stfld <P>k__BackingField → ret` |

- 两处都**只读不改** `ImplAttributes`（`Synchronized` / `AggressiveInlining` 等不丢）；改写前后
  还会自比对一次，不一致即内部一致性失败。
- 两个方法各自清空 sequence point（合成桩的旧行号指向已不存在的源码位置），并各自登记
  `PdbProbe`（`ExpectedSequencePoints = 0`）。
- **不改**：getter、构造初始化器、其它业务体；**不重写**程序集里其它 `stfld`。
- 落盘由既有 `WriteAtomically` 完成；写出的 PDB 由**独立读取器**
  （`System.Reflection.Metadata`）逐方法按 RID 核对（不是 Cecil 自己读回来）。

## 4. check 不能只信 stamp / 只数调用

`--check` 逐条验证（且 `--check` 全程零写）：

1. **normal setter** 的 IL 恰好是 `this, value → call PMNet_PropertySet_<P> → ret`（不是被塞了别的业务）。
2. **RawSet** 的 IL 恰好是 `this, value → stfld <P>k__BackingField → ret`（**写错字段必须被拒**）。
3. **赋值 helper 的语义**：用**有界抽象求值**在
   `(属性值 == 参值 / != 参值) × (HasAuthority / 否)` 四种组合下真的跑一遍，断言：
   - 恰好一次 `RawSet(入参)`；
   - `PushBased=true`：只在 `变化 && 权威` 时标脏一次，且标的是**本属性自己的槽位常量**；
     相同值不标脏、非权威不标脏（**槽位串位 / 忽略变化 / 忽略权威都必须被拒**）；
   - `PushBased=false`：完全不读 `HasAuthority`、完全不标脏。
   求值器只认白名单指令（`EqualityComparer<T>.Default` / `get_<P>` / `Equals` /
   `RawSet` / `get_HasAuthority` / `MarkPropertyDirty` 常量参数），其余一律明确失败。
4. **Reader 不回环**：`PMNet_Read_<P>` 必须调用 RawSet，且**不得**调用普通 `set_<P>`、
   不得调用 `PMNet_PropertySet_<P>`（否则收包会反向标脏 / 触发 setter 副作用）。
5. **符号**：wrapper / 业务体 / setter / RawSet 都按（完整类型名 + 方法名 + 参数数）唯一定位 RID
   并核对 sequence point 条数；旧 PDB 会立刻暴露。

## 5. 与 RPC 编织统一（不新增独立写盘器）

- `ClassBucket` 的入选条件从「含 RPC」扩为「含 RPC **或** auto-property」；
  **纯属性零 RPC 类也是参与对象**（`RpcCount = 0 && PropertyCount > 0` 时**不再**走 noop 早退）。
- 同一个 bucket 集合、同一次预检、同一个 `version`（stamp）/实例 gate / `PMNet_RequireRpcWeave` /
  `PMNet_BuildEntry` 门；属性与 RPC 在**同一次** `PropertyWeaver.Weave` + `WeaveMethod` 后
  一起 `SetWeaveVersion`、一起原子落盘。
- `--require-rpcs` 语义**未变**：仍表示「至少 1 条 RPC」，属性不计入。判定已刻意放在
  「无 RPC 且无属性 ⇒ noop」早退**之前**（否则会把 `--require-rpcs` 的负例放行——这一条正是
  回归门禁抓到并修掉的）。构建接线只在生成源码里真的出现 `PMGeneratedRpcId_` 时才传它，
  因此纯属性程序集在正常构建里会被正常编织、不会被判红。
- 报告新增 `auto-property 复制属性数 / 本次实际编织的属性数`；RPC 计数语义不变。

## 6. 门禁结果（均本次 build 退出 0 才 run）

| 门禁 | 结果 | 说明 |
|---|---|---|
| `PMPropertyWeaverTest`（新增） | **146 / 0** | 34 次 CLI 调用、15 次真实夹具编译；Debug + Release 各跑一遍完整链 |
| `PMNetWeaverTest`（既有 RPC 回归） | **314 / 0** | 57 次 CLI、22 次夹具编译；RPC 侧未回退 |

`PMPropertyWeaverTest` 覆盖（摘要口径）：

- **真实链条**：真实编译 PMNet 运行时（netstandard2.0 / C# 7.3）→ 真实 `PMNetGen --decl-gen` 产出
  RPC 生成物 → auto-property 夹具按冻结契约手写生成物形状 → 两者编进**同一个** DLL
  （Debug/Release 均带 portable PDB）→ `--check` 必失败 → `--weave --require-rpcs` → `--check` →
  二次 `--weave` 幂等 → `AssemblyLoadContext` 加载并跑真实行为链。
- **真实行为**（`PropertyDriver.RunWoven`，17 个用例）：
  `stamp-is-one`、`initializer-preserved`（`Inited==42` 且构造后无脏位）、
  `authority-dirty`、`authority-same-value-not-dirty`、`compound-assignment-dirty`（`Hp -= 3`）、
  `client-not-dirty`、`rawset-correct-field-and-slot`、`pushbased-false-not-dirty`、
  `array-whole-ref-dirty`、`array-new-ref-same-content-dirty`、`array-same-ref-not-dirty`、
  `array-inplace-not-dirty`、`reader-apply-not-dirty`（权威端收包也不反向标脏）、
  `setter-and-reader-no-onrep`、`onrep-dispatch-once`、`legacy-setter-forwards-and-pushes`、
  `registration-conditions`（OwnerOnly / PushBased=false 与注册表一致）。
  —— 赋值全部走**业务侧普通 C# 赋值**（`Hp = v` / `Hp -= x`，经类内业务方法），
  **没有手写标脏 setter、没有反射写字段冒充普通赋值**；反射只用于**调用**私有 Reader 与 OnRep 分发。
- **未编织负例**：`version == 0`、`new PropFixture()` 被 gate 拒、`PMNet_BuildEntry()` 被 guard 拒。
- **纯属性零 RPC 程序集**：`--weave` 报告 `RPC 方法数：0 / auto-property 复制属性数：5 /
  本次实际编织的属性数：5` 且 DLL 真的被改写（**不是 noop**）；同一程序集加 `--require-rpcs` 必须失败
  （语义未变）；`--check` 通过。
- **负例族（源码注入，全部要求 weave/check 失败且 DLL/PDB hash 不变、无暂存残留）**：
  `corrupt-setter`（改自定义访问器 ⇒ 找不到 backing field）、`rawset-not-stub`、
  `helper-wrong-index`（标脏槽位串位）、`helper-no-authority`、`helper-ignores-change`、
  `reader-uses-setter`、`missing-rawset-helper`、`missing-property-set-helper`、
  `missing-registration-slot`、`pushbased-mismatch`。
- **负例：RawSet 写错 backing field**：先正常编织，再把 `PMNet_PropertyRawSet_Hp` 的
  `stfld <Hp>k__BackingField` 的字段 token 换成 `<Mana>k__BackingField`（只改 4 个 token 字节，
  所以 DLL 与 PDB 仍“匹配”、符号校验抓不到），`--check` 与 `--weave` 都必须失败且文件不变。
  这是「写错字段 ⇒ 静默串到别的属性」这条最难自查的缺陷的唯一真实形态。

## 7. 明确未完成 / 不声称

1. **A 组生成器尚未落地**：本次没有改生成器，也没有「魔改生成字符串」。`PMNetGen` 目前仍发射
   `PMNet_Set_<P>` / `PMNet_Write_<P>` / `PMNet_Read_<P>`，且 `PMNet_Read_<P>` 对 auto-property
   会写普通 setter（正是契约禁止的形态）。因此夹具里的 auto-property 生成物区域是**按冻结契约
   手写的独立实现**（`Fixture.cs` 顶部有完整说明），A 组落地后应删除该手写区、改为断言真实生成物
   ——这一步属于**主侧最终验证**，不在本组。
   另外，生成器目前只在 `cls.Rpcs.Count > 0` 时发射 version/Require/实例 gate/BuildEntry；
   契约要求「存在 RPC **或** auto-property 的类均发射」，这同样是 A 组的改动，本组只保证
   **weaver 侧已经按该要求校验**（缺 gate 的纯属性类会被明确拒绝）。
2. **轮询（`PushBased=false` 的真实采样/公平预算）不在本轮**：契约 §3 与主计划把它划给 C 组
   （`Docs/plans/_property_polling.md`，工作副本里已存在该文件）。本轮只证明
   「`PushBased=false` 的 setter 存值但**完全不标脏**」。
3. **OnRep「由真复制层一次」未做端到端**：本轮证明的是 setter / 收包 Reader 都**不通知** OnRep，
   而 `PMNet_OnRepDispatch` 这一分发入口被调用时**恰好一次**。把它接到真实 `PMReplicationChannel`
   的提交路径属于复制层验证（C 组 / 后续），本轮不声称已跑通真实复制链路。
4. **Unity 实机 / Player / IL2CPP 仍 PENDING_USER**：没有启动 Unity、没有抢工程锁、没有做 Player
   阶段验证。自动回调与真实运行必须由用户集中实机确认。
5. **生产迁移（T-P4）未做**：`PMR3Player` 的 12 个成员与 `PMR5Projectile` 的 1 个成员仍是字段，
   没有改成同名 auto-property，`ProtocolHash` 与 ID 锁未触碰（也不需要触碰）。因此
   `Client/Server` 侧真实构建本次没有跑（避免写 `Client/Assets/Scripts/PMR3/Generated` 与
   用户 `Server/bin`）；本组的「不改生产」由源码现状（全部 `[PMReplicated]` 都在字段上 ⇒
   属性计划数为 0）反证。

## 8. 复现命令

```bat
dotnet build Tools/PMNetWeaver/PMNetWeaver.csproj -c Release
dotnet build Tools/PMPropertyWeaverTest/PMPropertyWeaverTest.csproj -c Release

rem auto-property 门禁（真实编译 + 真实编织 + 真实普通赋值执行）
dotnet Tools/PMPropertyWeaverTest/bin/Release/net8.0/PMPropertyWeaverTest.dll

rem RPC 侧回归（必须仍 314/0）
dotnet Tools/PMNetWeaverTest/bin/Release/net8.0/PMNetWeaverTest.dll
```

门禁输出关键行（本次实测）：

```
通过 146 项，失败 0 项
PMNetWeaver CLI 调用次数：34；夹具真实编译次数：15
结果：PASS
```

``` 
[PMNetWeaver] RPC 方法数：0；含 RPC 的类数：0；本次实际编织的类数：1
[PMNetWeaver] auto-property 复制属性数：5；本次实际编织的属性数：5（[PMReplicated] 字段旧模式不参与，需业务显式 PMNet_Set_<P> 标脏）
```

## 9. 约束遵守情况

- 未提交 / 未暂存 / 未回退 / 未 `svn update`；未启动 Unity、未启停用户服务；未编译 UE。
- 未改生成器、生产源码、复制 runtime；写入严格限于 §1 列出的 7 个路径
  （`PropertyWeaver.cs`、`RpcAssemblyWeaver.cs`、`Program.cs`、三个测试工程文件、本报告）。
- 夹具与子组构建全部落在**唯一 `%TEMP%` 沙箱**（`%TEMP%/pmproperty-weaver-test`）与工程自身
  `bin/obj`，使用独立 `-o` 输出；未覆盖运行中的 `Server/bin`、未碰 `Library`。
- 中文源码统一 **UTF-8 BOM + CRLF**（`Tools/PMNetWeaver/{PropertyWeaver,RpcAssemblyWeaver,Program}.cs`、
  `Tools/PMPropertyWeaverTest/{Fixture,Program}.cs`、csproj 与本报告）。
- 未新增 `cli_delegate` / 递归委派。
