# R5-A2 目标历史与命中验证纯核心 —— 交付报告

> 范围：`D:/UGit/hyld-master`。任务类型 **comprehensive**（A2 子组：历史位置 / 几何 / L0–L3 验证）。
> 只读输入：`AGENTS.md` → `Client/Assets/AGENTS.md` → `Docs/plans/net-r5-projectile-contract.md`（全文）
> → `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（全文，冻结只读）
> → `Docs/plans/_r5_semantics_survey.md` §1.5/1.6 → `Docs/plans/_r5_host_survey.md` §A
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`；主计划仅读卷首当前状态与末尾 R5 计划。
> 未修改共享 `PMProjectileContracts.cs`，未改主计划，未改其它组文件，未启动 Unity，未提交，未递归委派。

## 1. 交付物（写入边界内）

| 文件 | 内容 |
|---|---|
| `Client/Assets/Scripts/PMProjectile/PMProjectileHistory.cs` | `IPMProjectileTargetHistory` 实现：每 epoch 有界环形目标历史 + 帧锚/回溯/当前三档还原 |
| `Client/Assets/Scripts/PMProjectile/PMProjectileHistory.cs.meta` | Unity meta（唯一 GUID，LF 无 BOM） |
| `Client/Assets/Scripts/PMProjectile/PMProjectileValidator.cs` | 命中验证纯核心：L0–L3 + 公开结果类/原因枚举 + 几何工具 |
| `Client/Assets/Scripts/PMProjectile/PMProjectileValidator.cs.meta` | Unity meta（唯一 GUID，LF 无 BOM） |
| `Tools/PMProjectileValidationCheck/PMProjectileValidationCheck.csproj` | netstandard2.0 + C#7.3 零 Unity 编译门（只编本组两文件 + 共享契约 + PMNetIdentity/PMMoverState） |
| `Tools/PMProjectileValidationTest/PMProjectileValidationTest.csproj` | net8.0 真实核心测试工程（同一文件面 + Program.cs） |
| `Tools/PMProjectileValidationTest/Program.cs` | T5A4/T5A5 真实运行测试（175 项断言，退出码 0/1） |
| `Docs/plans/_r5_validation_report.md` | 本报告 |

编码：新 `.cs` 全部 UTF-8 **BOM + CRLF**；`.meta` 为 **LF 无 BOM**（与既有 meta 一致）。
GUID：`PMProjectileHistory.cs.meta = 12d56b5527f44331b1cdd82c06049923`，`PMProjectileValidator.cs.meta = 8feefc055b3f467bb692b5b0a4d6f9b6`。

## 2. 验收命令与结果

```bat
dotnet build Tools/PMProjectileValidationCheck -c Release   REM netstandard2.0 + C#7.3，零 UnityEngine
dotnet build Tools/PMProjectileValidationTest  -c Release   REM net8.0（0 警告 0 错误）
dotnet Tools/PMProjectileValidationTest/bin/Release/net8.0/PMProjectileValidationTest.dll
```

| 命令 | 结果 |
|---|---|
| `PMProjectileValidationCheck` 构建 | **0 警告 / 0 错误**（证明：Unity 2019.4 能力上限内可编译、不自带 `Math.Clamp`/C#8 语法、不引 UnityEngine） |
| `PMProjectileValidationTest` 构建 | **0 警告 / 0 错误** |
| `PMProjectileValidationTest` 运行 | **通过 175 / 失败 0，退出码 0** |

**语言面要点（门禁覆盖）**：只用 `Math.Sqrt/Abs`、`double.IsNaN/IsInfinity`、`float.IsNaN/IsInfinity`、`default(T)`、`out`、`params`、`HashSet<T>`、泛型 `where T : struct`；未使用 `Math.Clamp`、`Span`、`??=`、`switch` 表达式、`using var`、元组。几何全部自算（无 `UnityEngine.Vector3/Physics`）。

## 3. 公开 API（交付主侧）

### 3.1 历史（`PMNet.Projectile.PMProjectileHistory`）

```csharp
public sealed class PMProjectileHistory : IPMProjectileTargetHistory
{
    public const int MaxTargetStreams = 64;   // 目标数上限（满时拒新）
    public const int RingCapacity = 512;      // 每目标样本环形容量
    public const int RetainMs = 1000;         // WorldTimeMs 保留窗口
    public const int MaxRewindMs = 500;
    public const int AnchorAgeMs = 600;

    public PMProjectileHistory(uint epoch);                       // epoch 必须非 0
    public uint Epoch { get; }
    public int TargetCount { get; }
    public void Reset(uint newEpoch);                             // 清空 + 换 epoch（此后拒旧 epoch）
    public void Clear();                                          // 清空样本，保留 epoch

    public bool Record(PMProjectileTargetSample sample, out string rejectReason);
    public bool Record(PMProjectileTargetSample sample);
    public int  SampleCount(uint netId);
    public uint StreamVersionOf(uint netId);

    // 契约接口
    public bool TryResolve(uint epoch, PMProjectileHitCandidate candidate, double worldNowMs,
        int rewindMs, int extraDeferMs, out PMProjectileTargetSample sample,
        out PMProjectileHistoryResolution resolution);

    // 可观测计数（只增，诊断/测试）：RejectedRecords / RejectedNewTargets / StaleStreamRecords /
    // StaleEpochRequests / StaleStreamRequests / PrunedByAge / OverwrittenSamples /
    // IgnoredStaleDuplicates / ClampedRewinds / TeleportRefusals
}
```

`Record` 的拒因字符串（稳定、可测）：`epoch-mismatch`、`netid-invalid`、`stream-invalid`、
`serverframe-domain`、`outputframe-domain`、`position-not-finite`、`radius-not-finite`、
`halfheight-not-finite`、`radius-not-positive`、`halfheight-below-radius`、`time-not-finite`、
`target-capacity`、`stale-stream`、`totaltime-regression`、`worldtime-regression`、`serverframe-regression`。

### 3.2 验证器（`PMNet.Projectile.PMProjectileValidator`，纯静态、无跨调用状态）

```csharp
public static PMProjectileValidateResult Validate(PMProjectileValidateRequest request);
public static PMProjectileValidateResult Validate(
    PMProjectileState state, PMProjectileSpec spec, PMActivationResult activation,
    PMProjectileHitBatch batch, IPMProjectileTargetHistory history, IPMProjectileHitFilter filter,
    double worldNowMs, int extraDeferMs, int usedVerifyCalls);
```

输入类 `PMProjectileValidateRequest`：`State / Spec / Activation / Batch / History / Filter / WorldNowMs / ExtraDeferMs / UsedVerifyCalls`。
结果类 `PMProjectileValidateResult`：

- `Status`：`Rejected | Pending | Confirmed`
- `Reason`：整包被拒原因（`PMProjectileRejectReason`，21 个明确枚举值）
- `Hits`：`PMProjectileValidatedHit[]`（`TargetNetId / TargetStreamVersion / ImpactPoint / Resolution`）
- `SkippedTargets`：`PMProjectileSkippedTarget[]`（`TargetNetId / Reason`，13 个 `PMProjectileSkipReason`）
- `Reports`：诊断信号（`rewind-exceeds-1000ms:<n>`、`history-degraded-to-current:target=<id>`、
  `extra-defer-negative-clamped`、`l2-budget-*`、`tombstone-not-finite`）
- `VerifyConsumed`：本次是否消耗一次 Verify 配额（供主调用者更新自己的计数）
- `IsSettleable`：**只有** `Confirmed && Hits.Length > 0` 才为 true（Pending/Rejected 一律 false）

**约束遵守**：验证器**不持有字典/计数器**（配额由主调用者持有），不扣血、不创建 Unity 对象、
不修改任何输入对象、返回数组与输入无别名；`IsSettleable` 把「Pending 绝不调用伤害出口」和
「被拒整包不得部分输出可结算结果」变成可断言性质（被拒时 `Hits` 与 `SkippedTargets` 均为空数组）。

## 4. 规则落地映射（契约 → 实现）

### 4.1 时间与帧口径

| 契约 | 实现 |
|---|---|
| `TotalSimTimeMs` 只用于同 stream 帧锚新鲜度 | `TryResolve` 帧锚档：`age = newest.TotalSimTimeMs - anchor.TotalSimTimeMs ∈ [0, 600 + max(0,extraDefer)]` |
| `WorldTimeMs` 用于各对象一致回溯 | 回溯档：`targetWorldMs = worldNowMs - clamp(rewindMs,0,500)`，在样本 `WorldTimeMs` 上插值 |
| **禁止帧差 × 16** | 新鲜度只用 `TotalSimTimeMs` 相减；测试 1a/1b 双向证伪（帧差 90/时间差 100ms → 帧锚；帧差 3/时间差 750ms → 退化） |
| AuthorityServer 帧锚与 Input 边界分域 | `Record` 拒 `ServerFrame.Domain != AuthorityServer`、`OutputFrame` 非空且非 `Input` |
| 同宿主帧可有多输入，取最后输出 | 同 `ServerFrame` 就地覆盖；`OutputFrame` 更旧的迟到重复被忽略（`IgnoredStaleDuplicates`） |
| 不跨 Teleport 插值 | 区间上界样本 `Teleported` → 取近侧 `s0`（不采用回溯区间之后的位置，等价于不外推） |
| 不外推 | `targetWorldMs >= newest.WorldTimeMs` → `Current`（最新权威样本） |
| 缺历史退当前并暴露退化 | 返回最新样本且 `resolution = Current`；验证器追加 `history-degraded-to-current` Report |
| 跨 epoch/stream 不退到另一世代 | epoch 不匹配或 `TargetStreamVersion != stream.Version` → 直接 `false`，不尝试其它世代 |

分辨率枚举 `FrameAnchor → Rewind → Current` 逐级退化，与契约 L3「帧锚不通过退 Rewind 而非硬拒」一致。

### 4.2 有界性

| 资源 | 上限 | 行为 |
|---|---|---|
| 每目标样本 | 512（环形） | 覆盖最旧并计 `OverwrittenSamples` |
| 目标数 | 64 | **拒新**，计 `RejectedNewTargets`（不淘汰既有目标） |
| 保留窗口 | 1000ms（WorldTimeMs） | 查询时惰性裁剪，计 `PrunedByAge` |
| epoch | 实例固定 | `Reset(newEpoch)` 后旧 epoch 记录/请求全部被拒 |

### 4.3 L0–L3

- **L0**：`ActivationId == 0` 仅 `ServerDirect` 可信 → `Confirmed`；`ClientPredicted` 携带 0 → 整包 Reject
  （`ActivationIdZeroNotServerDirect`，刻意不照搬 UE「server-origin 号段免校验」的信任缺口）。
  `ActivationId != 0` → 以调用方传入的权威账本 `PMActivationResult` 为准（`Rejected` 整包拒 / `Pending` 暂存候选）。
- **L1**：结构前置（Key 合法、`batch.Key == state.Key`）→ 入口硬门（全批）→ 墓碑截止 →
  Verify 配额 `UsedVerifyCalls >= 5` 拒（`VerifyQuotaExhausted`，`VerifyConsumed=false`）→ 每批目标 ≤ 100。
- **L2**：① 飞行锚 `state.Position`、预算 `speed*(2*clamp(rewind,0,500)+100+extraDefer)/1000` 米；
  ②a 停止+回放（`Stopped && extraDefer>0`）锚 `SpawnPosition`、同 ① 预算；
  ②b 停止+常规（`Stopped && extraDefer==0`）锚 `Position`、预算 `spec.RadiusM + 5.00 + 0.30` 米。
  `SkipFlyingTrajectoryValidation` **只**跳过 ①（未停止且 skip → 不施加预算，与 UE 同序）；L2 失败整包 `Rejected`。
- **L3**（逐目标，失败只 skip 不连坐）：已命中去重 → 批内去重 → 白名单 → 历史还原 → 尺寸合法
  （`radius>0 && halfHeight>=radius`，否则 `TargetSizeInvalid`）→ `Alive` → 位置 `+= VisualOffset` →
  `ImpactPoint` 钳制（到「竖直中心线 ± threshold」球面，`threshold = spec.RadiusM + sample.RadiusM + 0.30`，
  **只钳不拒**）→ 权威 Filter 收到**消毒后**的点 → 线段–线段几何（双精度，`dist <= threshold` 即命中）。
  「半胶囊中心线」= `center ± (halfHeight - radius) * Y-up`。
- **数值溢出 fail closed**：包级（L2 预算/锚/距离非有限）→ `Rejected`；目标级（threshold/几何非有限）→
  skip 该目标（`NumericOverflow`），绝不「当作通过」。

## 5. 测试覆盖（T5A4 / T5A5，175 项）

| 分组 | 覆盖点 |
|---|---|
| T5A4-1 | variable dt 帧锚新鲜度；帧差 90/时间差 100ms → `FrameAnchor`；帧差 3/时间差 750ms → 退 `Rewind`（双向证伪「帧差×16」）；`extraDefer=200` 扩窗至 800ms |
| T5A4-2 | 同 AuthorityServer 帧多 Input 边界取最后输出；迟到更旧输出被忽略且样本数不增 |
| T5A4-3 | 升代清旧；旧流记录/请求拒收；未知 stream 拒收；跨 epoch 记录/请求拒收；`Reset` 拒旧 epoch；缺失目标拒收 |
| T5A4-4 | 位置 NaN/Inf、`radius=0`、`halfHeight<radius`、`halfHeight` NaN、帧域非法/未建立、`stream=0` 拒收；时间/帧号回归拒收；1000ms 保留窗口裁剪；环形上界 512 + 覆盖计数 88；目标 64 满后拒新（第 65 个） |
| T5A4-5 | 跨 Teleport 不插值取近侧；不外推（请求晚于最新 → `Current`）；`rewind=0` → `Current`；未来锚/锚帧缺失 → 回溯；早于保留窗口钳到最旧（`ClampedRewinds`）；缺历史时验证器按 `HistoryUnavailable` skip 而非整包拒 |
| T5A5-1 | L2 三分支各自边界内通过/边界外整包拒；②a 锚出生点 vs ②b 锚停止位置（反向设置使错误锚必然 Rejected）；`skip` 只跳飞行、不跳 ②b |
| T5A5-2 | `HitPosition` NaN / `PreviousPosition` Inf；20.0m 线段通过 vs 20.01m 拒；`VisualOffset` 20.0m 仅目标级 `GeometryMiss` vs 20.01m 整包拒；`rewind<0` 拒；**`rewind>1000` 先 Report、同包坏目标仍必须 Reject**；合法包只 Report；目标数 101 拒 |
| T5A5-3 | 同批大/小目标逐目标独立尺寸与几何（大命中、小 `GeometryMiss`）；替身 provider 下 `radius=0` / `halfHeight<radius` / `radius NaN` 明确 `TargetSizeInvalid`；`Alive=false` 不命中 |
| T5A5-4 | `state.HitTargets` 去重；批内重复只结算一次；白名单外 `NotInWhitelist`、白名单内命中 |
| T5A5-5 | `ImpactPoint` 钳到 threshold 球面、Filter 收到钳制值（y=1.4 = 0.5+0.9）且原始值未被采用；Filter 拒绝 → `FilterRejected`；无权威 filter → `FilterUnavailable`（fail-closed） |
| T5A5-6 | 平行且 `dist == threshold` 命中、0.91m > threshold miss；交叉命中；退化中心线（点）命中；退化子弹线段（点）命中；共线/重叠命中 |
| T5A5-7 | `Pending` → 不可结算但保留候选、消耗配额、带 resolution；`Confirmed` 可结算；`activation Rejected` → 整包拒且 0 hits；配额 `used=4` 消耗 / `used=5` 拒且不消耗；`ClientPredicted + activation0` 拒；`ServerDirect + activation0` 恒 `Confirmed`；墓碑超窗拒 / 边界（`worldNow == until`）受理；`batch.Key` 不匹配拒；空目标批拒 |
| T5A5-8 | 结果数组与输入无别名；事后改输入不影响已产出结果；不写 `state.HitTargets`；整包被拒时无 hits、无 skip、不可结算 |

**负向样本已内建**：NaN/Inf（位置/尺寸/时间）、`radius=0`、`halfHeight<radius`、帧域非法、
`stream=0`、时间/帧号回归、旧流/跨 epoch、未来锚、超保留窗口、20m/20.01m 边界、
`rewind<0`/`rewind>1000`、超配额、墓碑超窗、`batch.Key` 不匹配、空批、超目标数、
`ActivationId=0`（预测侧）、`SkipFlyingTrajectoryValidation` 错误放大、缺失历史、`Alive=false`、
Filter 拒绝/缺失。

## 6. 明确限制与刻意不做（诚实边界）

1. **本组只交付 A2 纯核心**。RPC 声明/复制承载（B）、Unity 宿主与表现（C）、R6 业务切换**均未做**；
   **T45 整阶段不因此改 PASS**，实机双端验收仍为 `PENDING_USER`。
2. **不依赖并行 A1 组任何未完成文件**：`PMProjectileValidationCheck`/`Test` 逐文件列出
   `PMProjectileContracts.cs` + 本组两文件 + `PMNetIdentity.cs` + `PMMoverState.cs`，**不用通配**，
   避免把 A1 生命周期/假弹接管/三等待的在途实现编进来。
3. **未做骨骼精细校验**：`BoneName` 相关逻辑后置登记；不发明伤害部位倍率；首批按胶囊中心线判定。
4. **L2 失败按整包 `Rejected`**（本文件冻结口径）：L2 输入是每包的单一客户端命中点，属包级约束；
   L3 才逐目标 skip。与 UE「轨迹校验在 ForceValidate 内」一致。
5. **`rewind>1000` 的 Report 位置**：本实现把 Report 放在入口帧域/目标元素校验**之前**且不提前 return，
   以保证「Report 不跳过候选 Reject」；这与 UE「Report 必须在元素校验之后」的字面顺序不同（口径已在
   §4.3 与测试 T5A5-2 固定）。若主侧要求逐字对齐 UE 顺序，只需移动一行 `reports.Add(...)`。
6. **`TargetSizeInvalid` / `Alive=false` 依赖 provider 样本**：真实 `PMProjectileHistory.Record` 在记录期
   即拒 `radius<=0`/`halfHeight<radius`，所以这两个 skip 分支在真实历史的端到端路径上不会出现；
   测试用替身 provider 直接注入非法样本来覆盖验证器侧防御。
7. **`SkipFlyingTrajectoryValidation` 且未停止**时不施加任何 L2 预算（UE 同序：② 分支要求 `Stopped`）。
   若主侧要求「未停止也施加某种上限」，属新增语义，需先改契约。
8. **未启动 Unity、未提交、未跑 SVN、未递归委派**；未触碰 A1 组与主计划文件。
9. **未被验证的真实链路**：DS 侧按宿主帧记录权威位置历史的 recorder、`AuthorityServer` 帧只读访问器、
   投射物声明生成集合归属 —— 属 `_r5_host_survey.md` §下一步 的 B/C 阶段工作。
   `TotalSimTimeMs` 与 UE `MatchTime` 是否等价（survey U-4）仍待主侧冻结；本实现只依赖
   「同 stream 内两者均单调、且只用 `TotalSimTimeMs` 做帧锚新鲜度」这一不变量。
10. **每会话不无限保留已销毁 key**：本组只提供 `(epoch, netId, stream)` 的目标样本存储；
    「已销毁 key 的高水位拒绝重用」属 A1 注册表职责，本组不重复实现。

## 7. 与冻结契约的一致性自检

| 契约条款 | 状态 |
|---|---|
| 复用 `PMNet.Mover.PMVector3`（Y-up 米）、`PMFrameId` 命名空间 | ✅ 未新增单位类型 |
| 只允许改本组两文件；共享 `PMProjectileContracts.cs` 未改 | ✅ |
| 维度：`HistoryCapacity=512` / `HistoryAgeMs=1000` / `MaxRewindMs=500` / `AnchorAgeMs=600` / `TickBufferMs=100` / `HitToleranceM=0.30` / `MaxSegmentM=20` / `MaxVisualOffsetM=20` / `MaxVerifyCalls=5` / `MaxTargets=100` | ✅ 全部从 `PMProjectileLimits` 取，无重复字面量 |
| 目标数 64 / 目标级去重 / 白名单 / alive 截止 / 墓碑截止 | ✅ |
| 停止三分支、skip 只跳飞行、半胶囊中心线（halfheight−radius） | ✅ |
| ImpactPoint 只钳不拒、钳制后再 Filter、再几何，segment–segment 双精度覆盖平行/退化 | ✅ |
| 数值溢出 fail closed；不发明骨骼倍率；Pending 不结算；整包被拒不部分输出 | ✅ |
| `netstandard2.0 + C#7.3` 零 Unity 依赖编译；net8 真实核心测试 | ✅（0/0 与 175/0） |
