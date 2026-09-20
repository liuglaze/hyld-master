# R2 复制层大体把关（独立只读审查）

- **目标仓库**：`D:/UGit/hyld-master`（Unity 2019.4 C#，PMNet 新复制链）
- **审查对象**：`Client/Assets/Scripts/PMNet/Replication/*.cs`、`World/*.cs`、`PMNetObject.cs`、
  `PMNetProperty.cs`（仅沿直接引用）、`Tools/PMReplicationTest/Program.cs`、`Tools/PMNetE2E/Program.cs` + 两个 csproj
- **只读约束**：未编译、未改源码、未运行任何门禁；所有结论均来自源码与文档的静态阅读。
  本文件是本次审查唯一写入物。
- **冻结入口（已按序阅读）**：`AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` →
  `Docs/plans/net-r2-codegen-contract.md` → `Docs/plans/net-architecture-migration.md`（R2 行 / §5.4–5.6 / T40–T41 / §6.1）→
  `Docs/plans/net-r0-contract.md`（§2.1/§2.2/§2.4/§2.8 有界条款、§4、§5、§8、§9）。
  另读了既有三份子报告（`_r2a/_r2b/_r2c_report.md`）用于区分「已知缺口」与「新发现」。
- **能力路由记录**：`ue-project-search` skill 面向 UE 项目（`.uasset` / ue-code-server MCP），
  本目标是 Unity C# 仓库，无对应结构化 MCP 与资产类型 ⇒ 按有界降级，用源码文件读取；
  未使用 `fffind`（无 UE 资产路径需求）。降级原因与缺口已记入本报告「已检查范围」。

---

## 0. 结论排序（5 项）

| # | 严重度 | 结论 | 性质 | 当前可达性 |
|---|---|---|---|---|
| **F1** | **P1** | 变更掩码「写侧按字节、读侧按字（ulong）下标」错配 ⇒ **槽位 ≥ 8 的属性在接收侧全部解错并丢弃**，且会被 Ack 反向确认成「已同步」⇒ 静默永久不一致 | **新发现** | 生产未接线；**门禁已判绿（假绿）**，接线即命中 |
| **F2** | **P1** | D-R0-16「创建与初始状态同包」在复制路径上未闭合：初始值走**另一条 Unreliable 消息**；生成器**从不发射** `OnSerializeInitialState`；端到端门禁声称覆盖但夹具无初始状态 | **半已知（实现缺口未登记）+ 门禁假绿（新）** | 同上（接线即命中） |
| **F3** | P2 | 接收端**部分应用**：`ApplyRecord` 三条 `continue` 与 `OnMessage` 的 per-record `catch` 都会留下「半数属性已写入」；`_r2b_report` 只登记了「Reader 抛异常」一类，且字节码 A 段自称「不做部分采纳」 | 已知缺口（覆盖不全）+ 新证据 | 接线即命中（畸形/不同版本包） |
| **F4** | P2 | `PMNetWorld._allObjects` **只增不减**（销毁不摘、创建回滚不摘）⇒ 幽灵引用 + 内存无界（违反 D-R0-18），`TotalObjectCount/GetObjectAt` 语义失真 | **新发现** | 已可达（M04 已在用），长局放大 |
| **F5** | P2 | `MarkAll()` 在高位留**僵尸脏位** ⇒ 任何「<64 属性」的类在 `FlushNetDormancy/MarkAllPropertiesDirty` 之后**永久**留在待比较集合，每 Tick 白扫；文档把触发面限定为「>64 属性」，低估 | 文档口径不准（新） | 已可达（休眠唤醒路径） |

另附 3 条低优先级观察（§3），不计入前五项。

---

## 1. 逐项详述

### F1（P1，新发现）变更掩码读侧位序错配 —— 槽位 ≥ 8 的属性静默丢失

**证据（精确路径:行号）**

- 写侧按**字节**铺掩码（标准位序）：
  `Client/Assets/Scripts/PMNet/Replication/PMReplicationWriter.cs:76`
  `int maskBytes = PMRepMask.ByteCountFor(highest + 1);`
  `PMReplicationWriter.cs:95`
  `maskScratch[slot >> 3] |= (byte)(1 << (slot & 7));`   ← 第 i 位落在第 i/8 字节的第 i%8 位
- 读侧按**字节号当 ulong 下标**恢复，且无移位、且 `index > 3` 直接丢弃：
  `PMReplicationReader.cs:106-110`
  ```
  for (ulong b = 0; b < maskBytes; b++)
  {
      byte v = reader.ReadRawBytesCopy(1)[0];
      mask.SetByte((int)b, v);      // ← 行 109
      propertyCount += PopCount(v);
  }
  ```
  `PMReplicationTypes.cs:361-378`
  ```
  public void SetByte(int index, byte value)
  {
      if (index < 0 || index > 3) { return; }        // ← 第 5 字节起静默丢弃
      switch (index)
      {
          case 0: _w0 |= (ulong)value; return;       // ← 无移位；index 被当作「字下标」
          case 1: _w1 |= (ulong)value; return;
          ...
      }
  }
  ```
  而 `IsSet` 按 `bit >> 6` 选字、`bit & 63` 选位（`PMReplicationTypes.cs:286-299`）。
  ⇒ 线上第 b 字节（真实槽位 `8b..8b+7`）被解释为槽位 `64b..64b+7`。

**最小场景（可逐步复现，不需要真实网络）**

1. 某类有 9 个复制属性（槽位 0..8）。
2. 只改脏槽位 8 → 发送侧 `highest = 8` → `maskBytes = ByteCountFor(9) = 2`
   → 线上掩码字节 `[0x00, 0x01]`。
3. 读侧：`b=0 → SetByte(0,0x00)`；`b=1 → SetByte(1,0x01)` ⇒ `_w1 & 0x01` ⇒ `IsSet(64) == true`，
   `propertyCount = 1`。
4. 读侧遍历 `slot 0..255` 命中的是 **64**（不是 8），于是把线上该属性的 `propertyId/value`
   写进 `slots=[64]`。
5. `PMReplicationChannel.cs:965-976` `ApplyRecord`：`64 >= SlotCount(9)` ⇒
   `ProtocolErrors++` + 告警「更新记录引用了越界槽位 64」⇒ `continue`，**值未应用**。
6. 关键放大点：`ApplyRecord` 没有抛异常 ⇒ `PMReplicationChannel.cs:955` 仍然
   `QueueAck(connection, rec.NetId, rec.Version)`；发送侧 `OnAck`（`PMReplicationChannel.cs:752-762`）
   用**自己那份 inflight 记录**的槽位表推进基线 ⇒ 槽位 8 的基线被推进成「已确认」，
   随后 `TryClearSettledDirty` 清掉脏位 ⇒ **发送侧认为已同步，永不重发**。
7. 结果：接收端槽位 8 永远停在默认值，且没有任何机制会再修它。**静默永久不一致**
   （唯一痕迹是 `ProtocolErrors` 计数与一条告警，宿主未接 `Warn` 时完全无感）。

**边界量化**

| 记录内最高槽位 | maskBytes | 读侧结果 |
|---|---|---|
| ≤ 7 | 1 | 正确（唯一被门禁覆盖的区间） |
| 8..31 | 2..4 | 计数对、槽位下标全错（8b+j → 64b+j）⇒ 槽位 ≥8 全丢 |
| ≥ 32 | 5..32 | 第 5 字节起被 `SetByte` 静默丢弃 ⇒ `propertyCount` 大于实际填充项，尾部 `slots/propertyIds/values` 留默认值（`slots=0, ids=0, values=null`）⇒ 记录整体作废（`values=null` 进 `new PMNetReader(null)`，抛异常被 940-950 的 catch 吞掉） |

**为什么门禁全绿（测试漏检原因）**

- B 块门禁的唯一描述符是 `MakeDescriptor`，槽位数写死为 `SlotNames.Length`：
  `Tools/PMReplicationTest/Program.cs:319`（`{"A","B","C","S"}` 共 4 项）、`:340` `int n = SlotNames.Length;`
  ⇒ 凡经该描述符的用例，`maskBytes` 恒为 1；`Program.cs:598` 编解码用例也只用 `slots = {0,2,3}`。
- C 块门禁把「4 属性」写成了硬断言：
  `Tools/PMNetE2E/Program.cs:248` `Check(props == 5, "...主类 4 + 第二类 1...")`、
  `:249-250` `Check(rep.Rep.ChangeMaskBitCount == 4, ...)`；夹具 `Tools/PMNetE2E/E2eFixtures.cs`
  的 `E2eReplicated` 只有 `_health/_speed/_title/_ammo` 4 个 `[PMReplicated]`。
- **两个门禁都不存在「≥ 9 属性」或「≥ 33 属性」的用例**，且 `MaxPropertiesPerUpdate = 64`
  （`PMReplicationTypes.cs:104`）与 `PMRepMask.MaxBits = 256`（`:244`）说明设计本来就预期
  类可以远超 8 个属性 —— 门禁的覆盖面与设计的自述能力不匹配。
- 负向验证（3/3 注入）**完全没有触及编解码层**：注入点全是那几个 `protected virtual`
  决策点（`PMReplicationChannel.cs:28-31` 自述「四处决策点做成 virtual 以便注入」）。
  编解码、掩码、世界记账这些非 virtual 逻辑在结构上不可能被这套负向验证覆盖。

**性质**：新发现。不属于 §5.4 的 #1–#8，也不在 `_r2b_report.md` 的「已知未覆盖」清单里
（该清单只提到「无载荷校验和」「无字符串上限」「值级异常」）。

**可达性**：`PMReplicationChannel` 与 `PMNetWorld` 在 `Client/Assets` 下**没有任何生产调用方**
（除 `PMNet/` 自身以外 grep 为空，生命周期消息同样无宿主）⇒ 今日不可达；
但它已被门禁判为「**129 项 0 失败 + 92 项 0 失败**」，属典型的**假绿**：接线（R3/R6）当天即命中。

---

### F2（P1，半已知 + 门禁假绿）D-R0-16「创建与初始状态同包」在复制路径上未闭合

**契约原文**：`net-r0-contract.md:93` D-R0-16「初始状态与创建在**同一包**内，接收侧无
'已创建但值未到'的可见态」；`:334` §5「对象创建：Create 与初始状态**同包**；接收侧无
'已创建空值'可见态」。T41 验收列（§6.1）明列「Create 原子性」。

**实现事实**

- M04 侧确实把初始状态嵌进 Create 记录：`PMNetWorld.cs:512`
  `obj.OnSerializeInitialState(_initialStateWriter);`，`:638` 读回时
  `if (rec.InitialState != null && rec.InitialState.Length > 0)`。
- 但 **`OnSerializeInitialState` / `OnDeserializeInitialState` 是业务手写的 virtual 空实现**
  （`PMNetObject.cs:145` / `:155`），而**生成器从不发射它们的 override**：
  在 `Tools/PMNetGen/*.cs` 与两份生成物（`Client/Assets/Scripts/PMNet/Generated/`、
  `Tools/PMNetE2E/Generated/`）里检索 `InitialState` **零命中**。
  ⇒ 常态下 `rec.InitialState == null`，Create 记录里没有任何初值。
- 初值实际由复制层补：`PMReplicationChannel.cs:590` `if (baseline == null) changed = true;`
  （`:518` 的 `UnknownBaselineCount > 0` 同义）⇒ 走 `Tick()` 从
  `PMReplicationChannel.cs:406` `connection.Send(_payloadScratch[k], PMRpcReliability.Unreliable);`
  发出 —— **与 Create 不同的消息、不同的顺序域**（Create 走可靠域，D-R0-03）。
- D-R0-05/D-R0-09 又明确「禁止跨顺序域假设顺序」⇒ 复制层自己就不能保证 Update 在 Create 之后到达。

**后果**

1. Create 生效到首条全量 Update 生效之间存在「对象已 Active、字段全是默认值」的**真实可见窗口**，
   正是 D-R0-16 要消除的那一档（`OnReplicatedCreate()` 在 `PMNetWorld.cs:660` 之前/之后即可被业务观察到）。
2. 若 Update 先到（跨域乱序或丢包），`World.TryFind` 失败 ⇒ `DroppedUnknownObject++` 丢弃，
   靠发送侧基线仍为 null 下轮重发自愈 —— **收敛没问题，但「原子可见性」不成立**。
3. 若业务按契约手写 `OnSerializeInitialState`（把全部属性写进 Create），复制层**仍会**再发一次
   全量 Update（基线缺失 ⇒ 全量）⇒ **同一份初值双写**、Create 记录体积翻倍。
   两条机制没有互斥约定，也没有任何文档说明该由谁负责。

**门禁为什么没抓到（自称覆盖，实为空洞）**

- `Tools/PMNetE2E/Program.cs:322-325` 注释自称：
  「客户端副本**由生命周期消息创建**……这样 Create 与初始状态的原子性（D-R0-16）也在链路上」。
- 但夹具 `E2eFixtures.cs` 的 `E2eReplicated` **既没覆写 `OnSerializeInitialState`，
  也没覆写 `OnDeserializeInitialState`** ⇒ 实测 `InitialState` 恒为 null，
  E2E 对「初始状态」**一条断言都没有**（J 段只断言复制收敛后的值，那是由 Update 路径写入的）。
- E2E 的负向验证三探针（F1/F3/F5：实参闭包、Reject 跳过实现、值未变不发）与创建原子性无关；
  B 块门禁的 F 组（初始状态全量）测的是「基线缺失 ⇒ 全量发送」，也不是「与 Create 同包」。
- 结论：**T41 的「Create 原子性」这一条在实现里未闭合、在门禁里零覆盖，而文档（§5.6 表格、
  `_r2c_report.md:32`）把它记为「客户端副本由生命周期消息创建」= 已覆盖** —— 这是本次审查发现的
  第二处假绿。

**性质**：实现缺口（§5.4 未登记的「#9」）+ 门禁假绿（新）。

**可达性**：同 F1（未接线，接线即命中）。属「未来接线风险」，但契约条款已冻结，需在 R6 前闭合。

---

### F3（P2，已知缺口但覆盖不全）接收端畸形/不一致包会部分应用

**契约/自述**：`PMReplicationChannel.cs:880-889` 注释与 `PMReplicationReader.cs:17-19` 注释
都声明「解析失败 ⇒ **整条丢弃**，不做部分采纳；半采纳会让接收端停在发送端从未存在过的状态」。

**实际代码存在三条部分应用路径**

1. per-slot 越界：`PMReplicationChannel.cs:971-976`
   `slot < 0 || slot >= entry.SlotCount ⇒ ProtocolErrors++ ; continue;`
2. per-slot 属性 ID 不一致（D-R0-46 的运行期兜底）：`:978-985` `continue`
3. per-slot 缺 Reader：`:987-991` `continue`
4. per-record 值级异常：`OnMessage` `:940-950` `catch { ProtocolErrors++; WarnInternal(...); continue; }`
   —— 注释自己承认「值级异常无法回滚已写入的属性」。

即：一条记录里前 3 个属性写进去了、第 4 个 ID 对不上，前 3 个**留在对象上**；
`QueueAck`（`:955`）仍然执行 ⇒ 与 F1 同源的放大：发送侧把「到达」当成「这些槽位已是对方基线」
（`OnAck` `:752-762` 直接用 inflight 的槽位表推进基线）⇒ 被跳过的那一槽永久不一致。

**已知/新**：`Docs/plans/_r2b_report.md:295`（§6 第 9 条）**只登记了第 4 条**（Reader 抛异常）；
第 1–3 条（不抛异常、静默 `continue`）**未登记**，而且对「本层是否真的不部分采纳」这个问题的
回答与代码不一致。`net-architecture-migration.md` §5.4 的 8 项里没有这一条。

**测试漏检原因**：`Tools/PMReplicationTest/Program.cs` 与 `Tools/PMNetE2E/Program.cs` 中
**没有任何用例**构造「掩码/槽位合法但 `propertyIds` 与本地描述符不符」或「越界槽位」的记录
并断言「零应用 / 对象状态不变」。B 报告自述的「整条丢弃、不部分采纳」因此是无断言的口径。

**最小场景**：一条 Update 记录带 `slots=[0,1]`、`propertyIds=[正确, 错误]` ⇒ slot 0 被写入、
slot 1 被跳过、该版本仍被 Ack，两端各存一份「部分新」的状态。

**可达性**：需要畸形包或两端描述符不同版本；接线后即成为可被恶意构造的输入面
（D-R0-18 的「不信任输入」口径）。

---

### F4（P2，新发现）`PMNetWorld._allObjects` 只增不减 —— 幽灵引用与无界内存

**证据**

- 声明与用途：`Client/Assets/Scripts/PMNet/World/PMNetWorld.cs:124`（`_allObjects`）、
  `:179` `TotalObjectCount => _allObjects.Count`、`:252-256`（`AddConnection` 遍历它补发 Create）、
  `:321-326`（`GetObjectAt`）。
- 只增不减的三处：
  - `Spawn` 加：`:374` `_allObjects.Add(obj);`
  - `DestroyObject` **不加不减**：`:414-415` 只做 `obj.State = Destroying; _byId.Remove(id.Value);`
    —— 整段 `395-448` 没有任何 `_allObjects.Remove`。
  - 创建回滚（初始状态反序列化失败）**不摘**：`:647` 只 `_byId.Remove(rec.NetId.Value)`，
    `:636` 加进去的那一项留在表里（`:648` 起把 `obj.State = Destroyed`）。
  - 只有 `Reset()` `:687` 才 `_allObjects.Clear()`。
- 上限只管活对象：`:357` 与 `:602` 都是 `if (_byId.Count >= _maxObjects)` ⇒
  `_allObjects.Count` 可以无限超过 `MaxObjects`。

**后果**

1. **内存无界**（违反 D-R0-18「复制队列与内存**必须有界**」）：一局里反复 spawn/destroy 的对象
   （子弹、特效体、临时机关）会在 `_allObjects` 里累积**永久**强引用，直到 `Reset()`。
   上限 65535 只对「同时存活」生效，对「累计创建」无效。
2. `TotalObjectCount` / `GetObjectAt` 的语义失真（`:321-326` 注释只说「含已销毁」，
   没提「含回滚失败的幽灵」）。诊断与门禁若用它们做断言会得到误导性结果。
3. 行为上暂无功能 bug：`AddConnection` 的循环（`:254-257`）会 `State != Active` 跳过，
   所以不会给对端发幽灵 Create —— 但代价是每新添加一条连接都要**线性扫过全部历史对象**。

**测试漏检原因**：本次审查范围内没有断言 `TotalObjectCount` 在销毁后回落；
两个 R2 门禁从不销毁对象（`PMNetE2E` 只在 rig 内 Spawn 一次）。
（相邻的 M04 门禁 `Tools/PMNetWorldTest` **不在本次硬边界内**，未读 —— 见 §「无法确定」。）

**已注册/新**：`net-architecture-migration.md` §5.3 记录的是 M04 的「筛选+清空」缺陷（已修），
未涉及 `_allObjects` 的单调增长；§5.4 的 8 项也没有。⇒ 新发现。

**可达性**：M04 已在用（`Spawn/DestroyObject` 是生产 API），长局放大。属「已可达」。

---

### F5（P2，文档口径不准）`MarkAll()` 僵尸脏位 —— 对象永久留在待比较集合

**证据**

- 置满 64 位：`Client/Assets/Scripts/PMNet/PMNetProperty.cs:112-116` `MarkAll() { _bits = ulong.MaxValue; }`；
  `:99-110` `Mark(i)` 在 `i >= 64` 时也 `_bits = ulong.MaxValue`。
- 触发它的公开入口（都不是测试专用）：
  `PMNetObject.cs:321-330` `MarkAllPropertiesDirty()`、`:339-343` `FlushNetDormancy()`
  （`Dirty.MarkAll()`），而 `MarkPropertyDirty` `:309-319` 在休眠时会调 `FlushNetDormancy()` ⇒
  **休眠唤醒路径必然置满 64 位**。
- 清理只覆盖前半段：`PMReplicationChannel.cs:795-800`
  ```
  int limit = entry.SlotCount;
  if (limit > 64) { limit = 64; }
  for (int slot = 0; slot < limit; slot++) { ... obj.Dirty.ClearBits(1UL << slot); }
  ```
  ⇒ 位 `[SlotCount, 64)` 永远进不了循环，永远清不掉。
- 只要 `HasAny` 为真，对象每轮都被判为「需要工作」：`PMReplicationChannel.cs:528-531`
  `if (obj.Dirty.HasAny) { return true; }` ⇒ 每 Tick 走一遍 `ScanAndAppend`
  （全槽位序列化 + 与基线比对），并在 `count == 0` 时进 `TryClearSettledDirty`
  （`:625`，其中 `IsSlotFullyConfirmed` `:819-887` 会对每个脏槽位重新序列化并与**所有连接**的基线比字节）。

**最小场景**：4 属性类，调用一次 `MarkAllPropertiesDirty()`（或任意一次休眠唤醒）⇒ 收敛完成后
位 4..63 仍为 1 ⇒ `Dirty.HasAny` 永久为真 ⇒ 该对象终身每 Tick 空扫一次，
`PMRepStats.SuppressedUnchanged` 无界增长。

**文档口径问题**：`net-architecture-migration.md:937`（§5.4 #7）与 `_r2b_report.md:267-268` 都把
触发条件写成「**属性数 > 64** 时槽位 ≥ 64 的脏位无法按位清除」，并把后果限定为「该对象持续参与比较」。
本审查的读码结论是：**触发面是「任何 < 64 属性的类 + 一次 MarkAll/FlushNetDormancy/Mark(≥64)」**，
即全部现有类都会中招 —— 结论方向一致（都是「持续参与比较」），但**触发面被严重低估**
（按现在的写法，读者会以为只有属性数逼近 64 的类才需要关注）。

**测试漏检原因**：
- 门禁里**没有任何** `Dirty.HasAny` 断言（`grep HasAny Tools/*/Program.cs` 零命中）；
  B 块只有单点断言 `Program.cs:752` `Check(!rig.ServerObj.Dirty.IsDirty(0), "C4 追平后脏位被清")` ——
  只查位 0。
- 门禁的「整对象标脏」是**逐槽位**做的：`Program.cs:457-465` `Rig.MarkAllDirty()` 对 4 个真实槽位
  逐个 `MarkPropertyDirty(i)`，**从不调用** `MarkAllPropertiesDirty()/FlushNetDormancy()` ——
  恰好绕过了唯一会制造僵尸脏位的调用。
- 休眠交互本身就没接（`_r2b_report.md:301` 第 11 条自述「休眠 + ForceNetUpdate 这条耦合在 R2 只算半个」）。

**性质**：新增证据 + 文档口径不准；正确性不受影响（比较才是判据），属性能/可观测性问题。

---

## 2. 低优先级观察（不计入前五项）

| ID | 观察 | 位置 | 影响 |
|---|---|---|---|
| O1 | `OnAck` 对**找不到在途记录**的版本仍推进 `AckedVersion` 并 `DropInflightUpTo` | `PMReplicationChannel.cs:752-765`（`batch == null` 也走到 `:764-765`） | 伪造/重放 Ack 可把确认水位推到从未发送过的版本，使后续**真实** Ack 被判 stale 而忽略（基线推迟推进，靠重发自愈）；容量账本被提前释放 |
| O2 | `UpdateMessagesApplied++` 在「所有记录都被丢弃」时也自增 | `PMReplicationChannel.cs:957` | 统计口径偏乐观（`applied` 已是 0 仍计一条消息） |
| O3 | `_ackQueues` 不随连接注销清理；`RemoveConnection` 只清 `_buckets` | `PMReplicationChannel.cs:64`、`:142-157` | 每会话最多留下「历史连接数」个空 List 条目（数量小，属卫生问题） |

---

## 3. 固定五段

### 3.1 已确认

1. **F1（掩码位序错配，P1，新发现）**：写侧按字节铺（`PMReplicationWriter.cs:95`），
   读侧把字节号当 ulong 下标且无移位、第 5 字节起静默丢弃
   （`PMReplicationReader.cs:109` + `PMReplicationTypes.cs:361-378`）。
   结果：记录内最高槽位 ≥ 8 时，槽位 8..31 被误映射到 64..95（越界 ⇒ 丢弃），
   ≥ 33 属性时第 5 字节起整段丢失。**且 `ApplyRecord` 不抛异常 ⇒ 仍发 Ack ⇒
   发送侧用自己的 inflight 槽位表推进基线并清脏（`:752-762`、`:955`）⇒ 静默永久不一致。**
2. **门禁对 F1 是假绿**：两个门禁的描述符/夹具上限都是 4 个属性
   （`PMReplicationTest/Program.cs:319,340,598`；`PMNetE2E/Program.cs:248-250`；`E2eFixtures.cs`），
   `maskBytes` 恒为 1，恰好落在唯一正确的区间；负向验证只注入那几个 `protected virtual` 决策点
   （`PMReplicationChannel.cs:28-31`），编解码层结构上不被覆盖。
3. **F2（D-R0-16 未闭合，P1）**：生成器从不发射 `OnSerializeInitialState`（全仓 `InitialState`
   在 `Tools/PMNetGen/*` 与生成物零命中），Create 记录常态无初值；初值改由**另一条
   Unreliable 消息**补（`:406`、`:590`），跨顺序域（D-R0-05/D-R0-09 禁止假设顺序）；
   E2E 注释（`Program.cs:322-325`）自称覆盖原子性，但夹具无覆写、无断言 ⇒ 第二处假绿。
4. **F3（部分应用，P2）**：`ApplyRecord` 三条 `continue`（`:971-991`）与 `OnMessage` 的
   per-record catch（`:940-950`）都会留下半写状态，与字节码自身声明（`:880-889`、
   `PMReplicationReader.cs:17-19`）矛盾；`_r2b_report.md:295` 只登记了「Reader 抛异常」一类。
5. **F4（`_allObjects` 无界，P2，新发现）**：`PMNetWorld.cs:374` 加、`:414-415` 销毁不摘、
   `:647` 回滚不摘、`:687` 仅 `Reset` 清；上限只算 `_byId.Count`（`:357`/`:602`）⇒
   累计创建量无界、幽灵引用与 `GetObjectAt/TotalObjectCount` 语义失真。
6. **F5（僵尸脏位，P2）**：`MarkAll` 置满 64 位（`PMNetProperty.cs:112`），
   清理只到 `min(SlotCount,64)`（`PMReplicationChannel.cs:797-800`），
   而 `HasAny` 是进入比较的门槛（`:528`）⇒ 任何类在 `FlushNetDormancy/MarkAllPropertiesDirty`
   之后终身每 Tick 空扫。文档把触发面写成「属性数 > 64」，与代码不符。
7. **契约侧一致的部分**（抽查确认实现与契约相符）：每连接基线 +「ACK 只能前进」
   （`OnAck:752-765` + `IsAckStale:774-783`）；「标脏后仍要比较」（`:583-598`）；
   8 项条件按 legacy 口径（`PMRepConditions.Evaluate`）；条件跃迁补发
   （`NeedsWork:505-514` + `ScanAndAppend:551-557` + `RequireSend`）；初始/基线缺失 ⇒ 全量
   （`:518`/`:590`）；在途/调度/待发 Ack 三类上限（`PMRepOptions`）；解析侧记录数/掩码字节数上限
   （`PMReplicationReader.cs:88-101`）；生命周期消息 Create/Destroy 同批保序
   （`PMLifecycleCodec`，`:509-521` 逐条 kind）。这些不是本次的问题点。

### 3.2 高概率推断（含依据与置信度）

1. **F1 的触发概率 ≈ 100%（置信度高）**：只要任何业务类有 ≥9 个 `[PMReplicated]` 成员即命中。
   依据：设计自述上限 64 属性（`PMReplicationTypes.cs:104`）、掩码 256 位（`:244`）、
   `PMNetObject` 是「可复制状态的宿主」（类注释），角色/道具这类对象的属性数必然超过 8。
   本报告未构造大属性类实证运行（只读约束 + 不编译），故列为「高概率推断」而非「实测已确认」。
2. **F1 会以「Ack 已确认但对方没值」的形态出现（置信度高）**：同 F1 第 6 步，
   Ack 语义目前是「版本到达」而非「槽位已应用」（`:955` 无条件 `QueueAck`）。
   反过来看，**只要把 Ack 语义收紧为「全部槽位应用成功才 Ack」**，F1/F3 都会退化为「持续重发」
   的显性故障 —— 这也是一条低成本的临时兜底（见 §3.5）。
3. **F4 在长局里会成为可观的内存增长（置信度中）**：取决于业务是否频繁 spawn/destroy
   （子弹系统在旧链路里正是高频创建）。未实测，故为推断。
4. **F5 的性能影响量级有限但覆盖面广（置信度中高）**：每 Tick 一个对象多一次全槽位序列化 +
   与全部连接比字节；在 150 对象/连接/帧的预算下是固定常数开销，不改变正确性。
   未做实测（不编译、不跑门禁）。
5. **F2 的双写风险是真实存在的（置信度中）**：任何按契约实现 `OnSerializeInitialState` 的类，
   都会同时被复制层的「基线缺失 ⇒ 全量」再发一遍（`:518`/`:590`）。依据是两条机制之间
   没有任何互斥位或文档约定。

### 3.3 无法确定（缺少证据）

1. **M04 门禁是否覆盖 F4**：`Tools/PMNetWorldTest` 不在本次硬边界内（未读）。
   因此「`_allObjects` 单调增长从未被断言」只对 R2 两个门禁成立，对 M04 门禁不做结论。
2. **真实传输下的表现**：M03 未接（`_r2b_report.md` §6.3 自述），未验证真实 MTU/分片下
   掩码字节数与 `MaxPropertiesPerUpdate` 的相互作用（例如单载荷 64 属性必跨分片时的重组有界性）。
3. **`PMRepMask.GetByte` 是否有隐藏调用方**：全仓检索只有 `SetByte` 被读者调用、`GetByte`
   **零调用方** ⇒ 无法确定它是否是为未来读者准备的（若未来有人按字节序用它写掩码，
   会再引入一次同类错配）。
4. **Unity/Mono 2019.4 下的实际行为**：未编译、未运行（任务禁区）。所有结论为静态读码。
5. **业务侧属性数分布**：未扫描全仓 `[PMReplicated]` 使用面（超出硬边界），
   故无法给出「当前已有多少类会命中 F1」的准确数字。

### 3.4 已检查范围（文档如何决定入口）

**文档（决定首搜表/入口）**

| 文档 | 决定了什么 |
|---|---|
| `AGENTS.md` | 三个入口文档的路由（客户端 / 服务端 / 迁移计划） |
| `Client/Assets/AGENTS.md` | **决定性**：§1.1 进程形态、§3.3 战斗主链路、§12 协议来源。它明确当前联机链路是旧 `MainPack`/UDP，PMNet 新链尚未接管 ⇒ 直接决定「本次审查对象是 R2 新增的 PMNet 层，而非旧战斗链」 |
| `Server/AGENTS.md` | 服务端侧边界（M12/Lobby），确认 R2 不涉及服务端改动 |
| `net-r2-codegen-contract.md` | 冻结接口表、§4 生成物 API 面（据此定位 `Generated/` 与发射器）、§4.3.1 闭包实参、§5 十三条规则、§6 类型集、§7 A/B/C 三块与硬边界 ⇒ 决定「先看 Replication/ 与 Tools/PMReplicationTest」，并给出 B 块自述的 129 项门禁口径 |
| `net-architecture-migration.md` R2 行 / §5.4–5.6 / §6.1 T40–T41 | R2 的验收口径与**已知缺陷清单**（8 项 + 已闭合 3 项）⇒ 用于区分「已知缺口 vs 新发现」；§5.6 的「Create 原子性」表述用于核对 F2 的假绿 |
| `net-r0-contract.md` §2.1/2.2/2.4/2.8、§4、§5、§8、§9 | 复制/生命周期/有界条款的**判定基准**：D-R0-13/14/15/16/18、D-R0-05/09（禁止跨域顺序假设）、§5「旧 ACK 不得清除新脏位」、§9 待收敛参数 |
| `_r2a/_r2b/_r2c_report.md` | 既有已知缺口清单 ⇒ F3（第 9 条）、F5（第 5 条口径）据此判定为「已知/口径问题」，F1/F2/F4 据此判定为「新发现」 |

**代码（按函数局部读取，未整份回显千行文件）**

- `PMNet/Replication/PMReplicationChannel.cs`：`AddConnection/RemoveConnection/RegisterObject/UnregisterObject`
  （123-200）、`SetCustomConditionActive/SetDynamicCondition/RequireSendOnAllConnections`（220-296）、
  `BuildUpdate`（298-385）、`Tick`（387-424）、`OnLoss/GetInflightVersions/FlushPayload`（425-492）、
  `NeedsWork`（493-535）、`ScanAndAppend`（536-679）、`HasValueChangedSinceBaseline/ShouldForceIncludeOnTransition`（699-720）、
  `OnAck/IsAckStale`（729-785）、`TryClearSettledDirty/IsSlotFullyConfirmed`（786-888）、
  `OnMessage`（890-963）、`ApplyRecord/DispatchOnRep`（965-1022）、`QueueAck/BuildAckMessage/PendingAckCount`（1023-1087）、
  `ResolveViewRole/ResolveFlags/ResolveEntry`（1089-1216）、门禁查询接口（1282-1387）
- `PMNet/Replication/PMReplicationTypes.cs`（全文，含 `PMRepMask` 全部方法）、
  `PMRepConnectionState.cs`（全文）、`PMReplicationWriter.cs`（全文）、`PMReplicationReader.cs`（全文）
- `PMNet/World/PMNetWorld.cs`：`RegisterClass/AddConnection/RemoveConnection/TryFind/GetObjectAt`（190-330）、
  `Spawn`（337-385）、`DestroyObject`（395-448）、`BuildLifecycleBatch`（465-548）、
  `OnLifecycleMessage/ApplyCreate/ApplyDestroy/Reset`（555-695）
- `PMNet/World/PMNetObjectLifecycle.cs`（全文，含 `PMLifecycleCodec`）
- `PMNet/PMNetObject.cs`（全文）、`PMNet/PMNetProperty.cs`（`PMDirtyTracker` 78-147）、
  `PMNet/PMNetFieldDesc.cs`（未命中相关符号）
- `Tools/PMReplicationTest/Program.cs`：`Main` 与套件入口（35-130）、`Rig`（376-466）、
  `Deliver/DeliverAll/TickAndDeliver`（483-526）、`MakeDescriptor`（338-375）、
  `TestCodec`（595-680）、`TestBaselineAndAck`（685-725）、`TestBounds`（1000-1124）、故障子类（266-317）
- `Tools/PMNetE2E/Program.cs`：`Main`（50-80）、`CheckRegistry`（223-270）、复制替身与 rig（271-500）、
  `TestReplication` 及各子用例（502-711）、故障注入（1123-1370）
- `Tools/PMNetE2E/E2eFixtures.cs`（全文）、`Tools/PMNetE2E/PMNetE2E.csproj`、
  `Tools/PMReplicationTest/PMReplicationTest.csproj`、`Tools/PMNetE2E/Generated/*.g.cs`（检索式确认）
- 生成器侧只做了**检索式**核对：`Tools/PMNetGen/*.cs` 中 `InitialState` 零命中（用于 F2）

**路由降级记录**：目标为 Unity C# 仓库，无 ue-asset-reader/ue-datatable/ue-code-server 适用的资产或
UE 符号；`ue-project-search` skill 的 MCP 路径不适用 ⇒ 只能用源码读取。证据缺口：
无法用结构化索引确认「全仓 `[PMReplicated]` 属性数分布」，也无法运行任何门禁实证 F1。

### 3.5 建议下一步（最小补充查询 / 运行时验证）

1. **F1 最小验证（不接线也能做，优先级最高）**：在 `Tools/PMReplicationTest` 增一个
   「10 属性」描述符用例 + 一个编解码单测（`slots = {9}` 往返），断言
   `r.Slots[0] == 9` 且客户端 `ReadHealth()`/对应字段真的变了。
   预期：现状必失败（`Slots[0] == 65` 或记录被丢弃）。修法二选一：
   - 把 `PMRepMask.SetByte/GetByte` 改成字节语义（`_w[index >> 3] |= (ulong)value << (8 * (index & 7))`，
     并放开 `index > 3` 的限制到 32）；或
   - 让读者不经 `PMRepMask`，直接按 `slot >> 3` / `slot & 7` 组装（与写侧同源）。
   同时给 `ByteCountFor` 与 32 字节上限加一条断言（覆盖「≥ 33 属性」分支）。
2. **把 Ack 语义收紧为「应用成功才 Ack」**（F1/F3 的通用兜底）：
   `OnMessage` 仅在 `ApplyRecord` 真正应用了全部槽位且无 `continue` 时才 `QueueAck`；
   或让 Ack 带上「已应用槽位掩码」。这会把「静默不一致」转成「显性重发」，
   使同类缺陷在弱网/回归中可被发现。建议在 M05 接收侧接线前先落这一条。
3. **F2 需一次契约裁定**：明确「初始状态由 Create 记录承载（生成器发射 `OnSerializeInitialState`）
   还是由复制层首次全量承载」，二选一；若选前者，生成器需发射该 override 且复制层对
   首帧基线做「已含初值」的短路；若选后者，需修改 D-R0-16 的口径（属契约变更，
   要写进 `BothSide.md` 与 R0 契约）。同时给 E2E 至少加一条断言：Create 消息内
   `InitialState` 非空 且 接收侧在 `OnReplicatedCreate` 之前既有初值（或明确断言由 Update 补）。
4. **F4**：在 `DestroyObject` 与创建回滚分支里同步摘除 `_allObjects`（用
   `RemoveAtSwap` 或「墓碑 + 压缩」），并把 `_maxObjects` 的判据同时覆盖累计量；
   补一条断言：`TotalObjectCount` 在销毁后回落。
5. **F5**：`TryClearSettledDirty` 的 `limit` 改为「全 64 位都参与清理」——
   对 `slot >= SlotCount` 的位，只要基线全部追平就直接清（这些位是 `MarkAll` 的伪位）；
   或把 `MarkAll` 在 `SlotCount < 64` 时只置真实槽位（`(1UL << SlotCount) - 1`，`SlotCount==64` 时特判）。
   补一条 `HasAny == false` 断言（收敛后）。
6. **门禁结构性补强（本次审查最有价值的元结论）**：现有负向验证只在
   「`protected virtual` 决策点」这一层注入（`PMReplicationChannel.cs:28-31` 自述的设计），
   因此**编解码层、掩码层、世界记账层结构上不可能被负向验证覆盖**。
   建议：把注入面下移到「字节级」（篡改一条已编码 payload 的掩码/ID/长度），
   并把「属性数 ≥ 9 / ≥ 33 / ≥ 64」作为门禁的固定矩阵维度之一。
7. **审查口径同步**：F2/F3/F5 与本文的「假绿」结论建议回写
   `net-architecture-migration.md` §5.4（或新增 §5.7「R2 大体把关复核」）与
   `Docs/plans/net-r2-codegen-contract.md` §7 的交付表，避免「129/92 项 0 失败」
   被后续会话当成「复制层已被验证」。
