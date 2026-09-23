# R5-B1 独立对抗复核：PMProjectileCodec（真字节 / 身份 / 裁决语义）

复核对象：`Client/Assets/Scripts/PMProjectile/PMProjectileCodec.cs`（B1 生产 codec，2132 行）
复核基线：`Docs/plans/net-r5-projectile-contract.md`（§B1 + 尾段「A3/B1 集成冻结」+「身份与安全」）、
`Docs/plans/_r5_codec_report.md`（B1 自评报告）、`Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（冻结契约）。
只读依赖：`PMNet/PMNetReader.cs`、`PMNet/PMNetWriter.cs`（未修改）。
测试：`Tools/PMProjectileCodecTest/Program.cs`（新增 N 节）。

**结论：找到并修复 1 个真 bug（裁决的 ActivationId 校验与 origin 无关），其余清单项逐条核对无真 bug。**
修复后 `CodecCheck` 编译 0 警告 0 错误；`CodecTest` 编译 0 警告 0 错误、运行 453 项断言全绿（改动前 395 项）。

---

## 1. 真 bug（已修）：Decision 的 ActivationId 对 origin 不敏感

### 1.1 契约要求

- 契约「身份与安全」：**「激活ID=0仅可信ServerDirect可用」**。
- 契约「A3/B1 集成冻结」B1：`Decision = Key + ActivationId + Confirmed/Rejected(+reason…)`。
- 因此 `ActivationId` 的合法性**必须随 origin 变化**：
  - `origin = ClientPredicted` → 必须非 0（0 不是有效激活账本项，客户端也不得自报 0）；
  - `origin = ServerDirect` → **允许 0**：可信权威弹按构造 Confirmed、不进客户端激活队列
    （`PMProjectileLifecycle.RegisterAuthority` 走 `PMProjectileTrust.AuthorityServerDirect`），
    但它仍可能需要下发**终态裁决**；此时 `activationId=0` 必须是**可表达**的。

### 1.2 修改前的行为（bug）

```csharp
// TryDecodeDecision 末尾 / TryEncodeDecision 中部（修改前）
if (result.ActivationId == 0u) { error = "…激活账本以 0 为非法"; return false; }
if (decision.ActivationId == 0u) { error = "…必须非 0…"; return false; }
```

无条件拒绝 `ActivationId == 0`，**不看 origin**。后果：`origin=ServerDirect` 的合法终态在 wire 边界被判成畸形数据，
**DS 根本发不出「可信权威弹的终态」**；B2 接线后只能二选一 —— 不下发（丢终态，违反「终态不得被迟到 Pending/Confirmed 反转」的收敛前提），
或伪造一个非 0 激活号（污染激活账本，与「ID 在同 epoch/owner/origin 单调增加永不复用」冲突）。两者都不可接受。
B1 报告 §4.4（`activationId` 非 0）与 §5.6（`activationId != 0`（上行 spawn 与决策））把这条口径写死为「非 0」，
即**接口文档本身继承了这处偏差**（本轮不改该报告，见 §5）。

### 1.3 修复（4 处，均在 `PMProjectileCodec.cs`）

| # | 位置 | 修改 |
|---|---|---|
| 1 | `PMProjectileDecision.ActivationId` 的 XML 注释 | 改为「随 origin 变化：ClientPredicted 必须非 0；ServerDirect 允许 0」 |
| 2 | `TryEncodeDecision`（L1651） | `ActivationId == 0u` 无条件拒绝 → `!IsActivationIdAllowed(activationId, origin)` |
| 3 | `TryDecodeDecision`（L1782） | 同上（读侧同样按 origin 判） |
| 4 | 新增私有原语 `IsActivationIdAllowed`（L1821） | `activationId != 0u \|\| origin == ServerDirect`，单点定义 + 注释契约出处 |

上行 `SpawnIntent` 的 `ActivationId != 0`（L446 encode / L621 decode）**有意保持无条件非 0**：
上行 origin 被 `requireClientPredicted` 强制为 `ClientPredicted`，两种写法等价，保留原判定与错误文案更贴近 DTO 语义。
`HitBatch` 无 activation 字段。快照 `activationId` 仍允许 0（见 §3 观察项 O3，未改）。

### 1.4 复现证据（负向对照）

把 `IsActivationIdAllowed` 临时退回修复前语义 `return activationId != 0u;`，重编译重跑：

```
FAIL N2 裁决 origin=ServerDirect + ActivationId=0 必须可解码（可信权威弹终态）：…
     …ActivationId 非法：0 只允许 origin=ServerDirect（可信权威弹）…（收到 activationId=0, origin=ServerDirect）
FAIL N2 裁决 encode 允许 origin=ServerDirect + ActivationId=0：…（同上）
FAIL N2 裁决 codec 编码 == 字面 golden 字节（期望 18 字节，实际 -1 字节）
通过 446 项，失败 3 项。结果：FAILED
```

- 该对照证明：**修复前确实拒了可信 ServerDirect 终态**（真 bug，不是「设计如此」）；
- 同时证明新增断言对这条语义**敏感**（不是空跑）；
- 对照中 `N3`（ClientPredicted + ActivationId=0 必拒）仍全绿 ⇒ 修复没有放松客户端侧。

---

## 2. 逐项复核（任务清单 → 结论 → 证据）

| 复核项 | 结论 | 证据 / 位置 |
|---|---|---|
| 长度前缀溢出（读端分配前上限） | 通过 | `TryOpenPayload`（L1868）先查 `count > MaxPayloadBytes`/越界/空；`TryReadBoundedString`（L2059）先 `PeekVarintLength` 判 64 字节再 `ReadStringValue`；测试 H/N7 |
| 错 wire type | 通过 | 每个字段逐一 `wire != 期望`，含数组元素（32/33）；F 节 11 处错 wire 负向（含 Snapshot 的 fixed64/fixed32 互换） |
| 字段重复 | 通过 | `IsFieldOrderAccepted` + `NoRepeatable=-1`；标量与嵌套（候选/spec）都拒；F 节 + N11 |
| 缺必填字段 | 通过 | 6 张位掩码（Spawn 0x1FF / Hit 0x7F / Candidate 0x3FF / Snapshot 0x1FFFFFF 去掉 32,33 / Spec 0x7F / Decision 0x7）；E 节 59 项逐个删字段 |
| 未知字段 | 通过 | 逐个 message 的 `field < 首 || field > 尾` 拒绝，**不做 skip**；K 节 17 项注入 + N11 |
| 嵌套长度（子消息） | 通过（fail-closed，见观察项 O1） | 候选(field 17)/spec(field 34) 声明长度 `0x7FFFFFFF` 时 `PMNetReader.ReadSubReader` 的 `_pos + length` 在 int 上回绕 → 子读取器 limit 为负 → `IsAtEnd` 恒真 → 必填掩码不全 → false；**无异常逃逸、无部分对象**（新增 N6 实测） |
| zero-copy 别名 / 对外数组克隆 | 通过 | `Array.Copy` 出精确长度新数组（`exact`、`CopyUInts`）；`Snapshot.Clone` 深拷贝 State/Spec/数组；改载荷/改一个解不影响另一个（L 节） |
| long varint 截断 | 通过 | `ReadVarint` 第 10 字节仍带续位即 `FormatException` → 被捕获转 false；新增 N7（10×0x80） |
| Key 身份（epoch/owner/projectileId 非 0、origin ∈ {1,2}） | 通过 | 头部逐字段读完后 `Key.IsValid` 复核；J 节 |
| 客户端不能上行 spec / AuthorityNetId / HitTargets / Stopped | 通过 | 上行 SpawnIntent 只认 10..18（19/7/8/9/34 全拒）；上行 HitBatch 同样；K 节 |
| Snapshot 的 true/false 字段 | 通过 | 26/27/28 三 bool 双向极性往返 + 字节级往返（新增 N9） |
| Snapshot 数组上限 / negative counts | 通过 | HitTargets/AllowedTargets 各 ≤100，第 101 项在读之前拒绝（`used >= MaxSnapshotTargets`）；`offset < 0 \|\| count < 0` 拒绝；新增 N5（负 count / count=0） |
| Snapshot 时间字段（tombstone 时刻） | 通过 | 25/29/30/31 全查有限且 ≥0；I 节 + 新增 N8（29/30/31 各补 -1） |
| Decision：`origin=ServerDirect` 应允许 `ActivationId=0` | **修复** | 见 §1 |
| Decision：`origin=ClientPredicted` 必须非 0 | 通过（修复后仍成立） | 新增 N3：字面 golden 只改 origin 字节 → decode 拒；encode 拒；对照 activationId=1 允许 |
| 100 候选 > 4096 生成桩上限（登记项，不得擅自改生成器） | 通过（保持登记） | `MaxGeneratedRpcBytes = 4096` 常量保留；实测 100 候选 = **4321 字节** > 4096，M 节断言 + 报告 §7.1；本轮**未**改声明源/generated/PMR3Player |
| 接口文档 vs 实际「空 batch / partial repeated 尾边界」语义一致 | 一致 | 契约/报告：HitBatch 的 `targets[]` 0 合法（空批由 L0 `NoTargets` 拒）；protobuf 无包内总长 ⇒ 截断在 repeated 边界会产生「结构完整但更短」的合法载荷。新增 N10 实测：0 候选载荷是 1 候选载荷的**严格字节前缀**且可解出 `Targets.Length == 0`；报告 §7.5 登记的切点数与「N 候选 → N 个更短合法产物」一致（G 节 2 候选 → 2 个） |
| 独立手造 golden bytes（不得是 encode→decode 自比较） | 通过（新增） | N1：**完全字面量** 55 字节 SpawnIntent（tag/varint/fixed32/2 字节 tag 全手写），decode 字段对 + `encode == 字面量`；N2：字面量 18 字节 Decision（ServerDirect + activationId=0） |
| codec 未引用 Coordinator / 零 Unity 依赖 / C#7.3 | 通过 | `PMProjectileCodecCheck`（netstandard2.0 + LangVersion 7.3 + 逐文件 Include）编译 exit 0、0 警告 0 错误 |

---

## 3. 静默观察项（已核，**未改**，附理由）

- **O1 嵌套长度 int 回绕只被「容器语义」兜住**：`PMNetReader.ReadSubReader`（`PMNetReader.cs:349`）的
  `_pos + length > _limit` 与子读取器构造里的 `offset + count > buffer.Length` 都是 int 加法，长度取 `0x7FFFFFFF` 时两处一起回绕成负数。
  实际不可利用：子读取器 `_limit` 为负 ⇒ `IsAtEnd` 恒真 ⇒ 任何子解析都走「缺必填字段」失败分支；
  父读取器只在子解析**成功**后才继续（而成功要求真实的可用字节），因此没有负下标读取路径。
  N6 已把它钉成断言（false + 不抛异常 + 不返回部分对象）。修它要动 `PMNetReader`（共享只读原语），按边界**不改**，
  仅登记：B2 接线时若想让错误更可诊断，可在 `ReadSubReader` 前补一次「声明长度 ≤ 剩余字节」的预览检查。
- **O2 非规范（超长但 ≤10 字节）varint 编码被接受**：`ReadVarint` 会接受用 10 字节写的小值
  （与 Google protobuf 的宽容度一致，第 10 字节为 0x00/0x01 时合法）。值与短编码完全等价，无别名/越界影响。
  codec 无法单方面收紧（解析原语是共享只读的，收紧会破坏与 Google.Protobuf 编码器的互通）。
  注意这与 yaw 的「非规范值一律拒」不矛盾：yaw 的非规范表示会破坏去重/比较语义，varint 的超长编码不会。
- **O3 快照的 `activationId` 不随 origin 收紧**（`ServerDirect` 与 `ClientPredicted` 都允许 0）：
  这是 `_r5_codec_report.md` §4.3 明写的接口口径，且 J 节现有断言
  `CheckAcceptedOrigin(3, …, origin=ClientPredicted, "快照 origin=ClientPredicted 允许")` 用的正是 `activationId=0`。
  按「接口文档与实现一致 + 只改已证实 bug」的要求**不动**；真正的把关在 A3
  （`PMProjectileCoordinator` 对客户端请求 `ActivationId == 0` 直接拒）。若主 Agent 想让快照也随 origin 收紧，
  需要同步改契约/报告与 J 节断言，属独立决策，不在本轮。
- **O4 空数组用共享只读哨兵**：`NoUInts` / `NoCandidates`（L1478/1479、L934）在「0 个目标 / 0 个候选」时是
  同一个静态实例，两个空结果的 `Targets` 会 `ReferenceEquals`。零长数组不可写入，不存在跨对象串写；
  `HitBatch.Clone()` / `Snapshot.Clone()` 仍会新建数组。仅登记为「实现细节」，不加断言以免冻结非契约行为。
- **O5 候选的 `ValidateCandidate` 诊断下标恒为 0**：`TryReadCandidate` 用 `ValidateCandidate(candidate, 0, …)`，
  错误串里的「第 0 个命中候选」对 2..100 号候选不精确（仅文案，不影响判定）。不改。

---

## 4. 测试（新增 N 节，58 条断言；总计 453）

新增内容全部落在 `Tools/PMProjectileCodecTest/Program.cs`：

- **N1** 字面 golden SpawnIntent（55 字节，含 2 字节 tag：`0x85 0x01` / `0x8D 0x01` / `0x90 0x01`）：
  长度自校验 → 解码字段全对 → `encode == 字面量`。
- **N2** 字面 golden Decision（18 字节，`origin=ServerDirect` + `activationId=0` + empty reason）：
  解码成功 + `encode == 字面量`（修复后的正向）。
- **N3** 同一 golden 只把 origin 字节从 `0x02` 改成 `0x01` ⇒ decode 必须拒、不返回部分对象；
  encode 侧 `ClientPredicted + 0` 拒；对照 `ClientPredicted + 1` 与 `ServerDirect + 9` 允许。
- **N5** 载荷内嵌在 64 字节缓冲区 offset=4（前后 0xEE 垃圾）时能正确解码；
  把尾部垃圾计入 count 必拒；负 count 必拒；count=0 必拒。
- **N6** 候选/spec 子消息声明长度 `0x7FFFFFFF` ⇒ false、不抛异常、不返回部分对象（嵌套长度回绕负向）。
- **N7** 10 字节全续位 varint ⇒ false，错误串含 "varint"（long varint 截断）。
- **N8** `stopWallTimeMs` / `timeAfterStoppedMs` / `tombstoneUntilMs` 各补 -1 负向（补齐 tombstone 时刻覆盖）。
- **N9** 快照 bool 反极性（false/true/false）往返 + 字节级往返。
- **N10** HitBatch repeated 尾边界：0 候选载荷是 1 候选载荷的严格字节前缀，且解出 `Targets.Length == 0`
  而保留 `rewindMs`/`previousPosition`/头部身份（与报告 §7.5 一致）。
- **N11** 以字面 golden 为基准的负向：未知字段 19、重复 `predictionMs`、截断 1 字节、
  yaw 位型换成 NaN（`0x7FC00000`，带 yaw 偏移自校验）。

门禁命令与结果（先 build 再 run，任何一步非 0 即失败）：

```
dotnet build Tools/PMProjectileCodecCheck/PMProjectileCodecCheck.csproj -c Release   # exit 0，0 警告 0 错误
dotnet build Tools/PMProjectileCodecTest/PMProjectileCodecTest.csproj  -c Release   # exit 0，0 警告 0 错误
dotnet Tools/PMProjectileCodecTest/bin/Release/net8.0/PMProjectileCodecTest.dll      # exit 0，通过 453 / 失败 0
python Tools/check_cs_braces.py <两个 .cs>                                            # PASS（括号平衡）
```

文件卫生：两个 .cs 均保持 UTF-8 **BOM + CRLF**（`PMProjectileCodec.cs` 2132 行、`Program.cs` 2228 行），
未经 Unity、未提交、未改 generated / 声明源 / `PMR3Player` / PMNetReader / PMNetWriter。

---

## 5. 未验 / 交回主 Agent 与 B2

1. **B1 报告的接口文档偏差未就地修正**：`_r5_codec_report.md` §4.4「`activationId` 非 0（激活账本以 0 为非法）」与
   §5.6「`activationId != 0`（上行 spawn 与决策）」在 Decision 一项上已被本轮的契约口径取代
   （应为「ClientPredicted 必须非 0；ServerDirect 允许 0」）。该报告不在本轮写入边界内，**需主 Agent 决定是否回填**，
   否则 B2 照旧文档接线仍会把可信 ServerDirect 终态当成非法输入。
2. **B2 承载限制保持登记、未动**：100 候选 = 4321 字节 > `PMGeneratedMaxArrayLength = 4096`
   ⇒ 仍须切批或提升声明上限（本轮按任务口径只记录，未改生成器/声明）。
3. **未接线**：真实 PMR3 RPC 收发、认证 owner/muzzle admission（`ownerNetId` 目前仍是发送侧自报值）、
   ID 生成集合、Unity 宿主与输入、双端实机 T45 —— 全部 **PENDING**，本报告不声称完成。
4. **未做**：L0–L4 几何/预算（属 A2 `PMProjectileValidator`）、墓碑与容量（属 A3 Coordinator）、
   骨骼级命中校验（契约后置登记项）、`RewindReportThresholdMs` 的消费侧（codec 只导出阈值不判定）。
5. **本轮无实机/Unity 验证**：按任务约束未启动 Unity、无 SVN/git 写操作，全部结论来自真实字节测试。

---

## 6. 一句话摘要

B1 codec 的字节级不变量（长度/线型/顺序/必填/未知/嵌套/别名/长 varint/身份/有限性/有界/克隆）
逐条核对与实测无真 bug；唯一真 bug 是**裁决的 `ActivationId` 校验对 origin 不敏感**，
导致契约允许的「可信 ServerDirect + ActivationId=0 终态」在 wire 上不可表达 —— 已按 origin 条件修复，
并用**完全字面量手写 golden 字节**（SpawnIntent 55B / Decision 18B）与负向对照（退回旧语义时 3 条断言转红）钉死；
其余 4 项静默观察（嵌套长度 int 回绕已被容器语义兜住、超长 varint 宽容、快照 activationId 口径、空数组哨兵）
均有证据支持「非真 bug」并附不改理由。
