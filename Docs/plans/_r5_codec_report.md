# R5-B1 独立线协议 codec 报告（PMProjectileCodec）

契约来源：`Docs/plans/net-r5-projectile-contract.md` §B1 + 尾段「A3/B1 集成冻结」。
任务类型：comprehensive（B1 实施 + 验收）。
状态：**B1 已实现并自验收通过**；B2（接线 / 认证 owner / 生成集合 ID / 传输上限）与 C（Unity 宿主）、
实机 T45 均**未**完成，本报告不声称它们已完成。

---

## 1. 交付物

| 文件 | 作用 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/PMProjectile/PMProjectileCodec.cs` | 生产 codec（2112 行，`namespace PMNet.Projectile`） | UTF-8 BOM + CRLF |
| `Client/Assets/Scripts/PMProjectile/PMProjectileCodec.cs.meta` | Unity 资产 meta（guid `e7ebf4fe58ba49dea2ee76509879afd4`，全库唯一） | 与同目录 meta 逐字节同格式 |
| `Tools/PMProjectileCodecCheck/PMProjectileCodecCheck.csproj` | 语言面门禁：netstandard2.0 + C# 7.3 + 零 UnityEngine | LF / 无 BOM（与既有 `Tools/*.csproj` 一致） |
| `Tools/PMProjectileCodecTest/PMProjectileCodecTest.csproj` | net8.0 真实字节测试工程 | LF / 无 BOM |
| `Tools/PMProjectileCodecTest/Program.cs` | 测试程序（1954 行，13 节，395 项断言） | UTF-8 BOM + CRLF |

**依赖面（只读，未修改）**：`PMProjectileContracts.cs`（冻结共享契约）、
`PMNet/{PMNetReader,PMNetWriter,PMNetIdentity,PMWireType}.cs`、`PMMover/PMMoverState.cs`。
**未引用**并行 Coordinator（`PMProjectileLifecycle` / `PMProjectilePending` / `PMProjectileValidator` /
`PMProjectileHistory`），也**未**引用 Google.Protobuf、UnityEngine、生成桩或任何声明文件。
B1 **未**修改 `PMR3Player`、`PMNet/Generated/**`、`PMR3/Generated/**`、契约文件或任何声明源。

---

## 2. 精确 public API

```csharp
namespace PMNet.Projectile
{
    public enum PMProjectileWireKind : byte
    { None = 0, SpawnIntent = 1, HitBatch = 2, Snapshot = 3, Decision = 4 }

    public sealed class PMProjectileSpawnIntent      // 上行专用 DTO
    {
        public PMProjectileKey Key;
        public uint ActivationId;
        public PMVector3 Position;
        public PMVector3 Direction;
        public float Yaw;
        public int PredictionMs;
        public PMProjectileSpawnIntent Clone();
    }

    public sealed class PMProjectileSnapshot          // 下行：State + Spec
    {
        public PMProjectileState State;
        public PMProjectileSpec  Spec;
        public PMProjectileSnapshot Clone();          // 深克隆（数组新建）
    }

    public sealed class PMProjectileDecision          // 下行：终态裁决
    {
        public PMProjectileKey Key;
        public uint ActivationId;
        public PMActivationResult Result;             // 只允许 Confirmed / Rejected
        public string Reason = string.Empty;          // 有界诊断串（UTF-8 ≤ 64 字节）
        public PMProjectileDecision Clone();
    }

    public static class PMProjectileCodec
    {
        public const int   ProtocolVersion          = 1;
        public const int   MaxPayloadBytes          = 16384;   // codec 独立上限（契约 §B1）
        public const int   MaxGeneratedRpcBytes     = 4096;    // 登记：PMR3 生成桩 byte[] 上限
        public const int   MaxBatchTargets          = 100;
        public const int   MaxSnapshotTargets       = 100;     // HitTargets / AllowedTargets 各自
        public const int   MaxReasonBytes           = 64;
        public const int   MinPredictionMs          = 0;
        public const int   MaxPredictionMs          = 500;
        public const int   RewindReportThresholdMs  = 1000;    // Report 阈值，不是 codec 硬上限
        public const float MaxYawDegrees            = 360f;    // 规范区间 [0,360)

        // 校验原语（供 B2 / 测试复用）
        public static bool IsFinite(float value);
        public static bool IsFinite(double value);
        public static bool IsValidKey(PMProjectileKey key);
        public static bool IsUplinkOrigin(PMProjectileOrigin origin);
        public static bool IsCanonicalYaw(float yaw);
        public static bool TryNormalizeYaw(float yaw, out float normalized);
        public static bool IsValidDirection(PMVector3 direction);
        public static bool IsTerminalResult(PMActivationResult result);

        // 四类载荷：TryEncode / TryDecode（解码另有「整段载荷」便利重载）
        public static bool TryEncodeSpawnIntent(PMProjectileSpawnIntent intent, out byte[] payload, out string error);
        public static bool TryDecodeSpawnIntent(byte[] payload, int offset, int count, out PMProjectileSpawnIntent intent, out string error);
        public static bool TryDecodeSpawnIntent(byte[] payload, out PMProjectileSpawnIntent intent, out string error);

        public static bool TryEncodeHitBatch(PMProjectileHitBatch batch, out byte[] payload, out string error);
        public static bool TryDecodeHitBatch(byte[] payload, int offset, int count, out PMProjectileHitBatch batch, out string error);
        public static bool TryDecodeHitBatch(byte[] payload, out PMProjectileHitBatch batch, out string error);

        public static bool TryEncodeSnapshot(PMProjectileSnapshot snapshot, out byte[] payload, out string error);
        public static bool TryDecodeSnapshot(byte[] payload, int offset, int count, out PMProjectileSnapshot snapshot, out string error);
        public static bool TryDecodeSnapshot(byte[] payload, out PMProjectileSnapshot snapshot, out string error);

        public static bool TryEncodeDecision(PMProjectileDecision decision, out byte[] payload, out string error);
        public static bool TryDecodeDecision(byte[] payload, int offset, int count, out PMProjectileDecision decision, out string error);
        public static bool TryDecodeDecision(byte[] payload, out PMProjectileDecision decision, out string error);
    }
}
```

错误口径（本批要求，**刻意**不同于 R4 codec「encode 抛异常」）：**编解码一律返回 `false` + `error`**，
不抛异常、不半应用、**不返回部分对象**（失败时 `out` 参数恒为 `null`）。`TryEncodeHitBatch` 用共享冻结类型
`PMProjectileHitBatch`（「HitBatch 共享」），不另造 DTO。

---

## 3. 线格式：固定头部（契约冻结 `kind=1/version=2/epoch=3/owner=4/projectileId=5/origin=6`）

四类载荷**共用**同一头部；身份四件套只在头部承载一次（载荷里不重复 Key，避免两份身份互相矛盾）。
正文一律从 **field 10** 起、按**单调 field 顺序**写；未知/重复/错 wire/缺字段/尾部字节全拒。

| # | 字段 | wire | 读侧要求 |
|---|---|---|---|
| 1 | `kind` | varint | 必须 = 期望种类（1..4），跨通道错用在此拒绝 |
| 2 | `version` | varint | 必须 = 1，不兼容即拒 |
| 3 | `epoch` | varint | 非 0 |
| 4 | `ownerNetId` | varint | 非 0（**客户端自报值，不作认证事实**，见 §9） |
| 5 | `projectileId` | varint | 非 0 |
| 6 | `origin` | varint | 1=ClientPredicted / 2=ServerDirect；上行两类载荷额外要求 == 1 |

---

## 4. 四类消息字段表

### 4.1 SpawnIntent（kind=1，**上行**，codec 专用 DTO）

| field | 字段 | wire | 必填 | 约束 |
|---|---|---|---|---|
| 10 | `activationId` | varint | ✔ | **非 0**（0 只允许可信 ServerDirect） |
| 11/12/13 | `position.{x,y,z}` | fixed32 | ✔ | 全部有限 |
| 14/15/16 | `direction.{x,y,z}` | fixed32 | ✔ | 有限、非零、**长度平方有限**（防归一化 NaN） |
| 17 | `yaw` | fixed32 | ✔ | 规范值 `[0,360)` |
| 18 | `predictionMs` | varint | ✔ | 整数范围 `[0,500]` |

**上行禁止携带**：Spec（速度/半径/寿命/StopOnHit/…）、`AuthorityNetId`、`HitTargets`、
`AllowedTargets`、`Stopped`、所有 wall-clock 字段。任何 field ≥ 19（或 7..9）一律按未知字段拒绝。
`origin` 只允许 ClientPredicted。

### 4.2 HitBatch（kind=2，**上行**，共享冻结类型）

| field | 字段 | wire | 必填 | 约束 |
|---|---|---|---|---|
| 10/11/12 | `previousPosition.{x,y,z}` | fixed32 | ✔ | 有限 |
| 13/14/15 | `hitPosition.{x,y,z}` | fixed32 | ✔ | 有限 |
| 16 | `rewindMs` | varint | ✔ | **≥ 0**；> 1000 **不截断**（Report 阈值由调用方判定） |
| 17 | `targets[]` | length-delimited msg（可重复，0..100） | — | 0 合法（空批由 L0 `NoTargets` 拒） |

命中候选（嵌套 message，1 起）：

| field | 字段 | wire | 必填 | 约束 |
|---|---|---|---|---|
| 1 | `targetNetId` | varint | ✔ | 非 0 |
| 2 | `targetStreamVersion` | varint | ✔ | 非 0（0=「未建立」，不是有效流代次） |
| 3 | `targetServerFrame.domain` | varint | ✔ | **必须 = AuthorityServer(2)**（错域帧线上不可表达） |
| 4 | `targetServerFrame.value` | sint64 | ✔ | **> 0** |
| 5/6/7 | `impactPoint.{x,y,z}` | fixed32 | ✔ | 有限 |
| 8/9/10 | `visualOffset.{x,y,z}` | fixed32 | ✔ | 有限 |

### 4.3 Snapshot（kind=3，**下行**：State + Spec）

| field | 字段 | wire | 必填 | 约束 |
|---|---|---|---|---|
| 10 | `authorityNetId` | varint | ✔ | **非 0**（权威 ID = 0 直接拒） |
| 11 | `activationId` | varint | ✔ | 允许 0（ServerDirect 语义） |
| 12/13/14 | `spawnPosition.{x,y,z}` | fixed32 | ✔ | 有限 |
| 15/16/17 | `previousPosition.{x,y,z}` | fixed32 | ✔ | 有限 |
| 18/19/20 | `position.{x,y,z}` | fixed32 | ✔ | 有限 |
| 21/22/23 | `velocity.{x,y,z}` | fixed32 | ✔ | 有限 |
| 24 | `yaw` | fixed32 | ✔ | 规范值 `[0,360)` |
| 25 | `moveTimeMs` | fixed64 | ✔ | 有限、≥ 0 |
| 26/27/28 | `stopped` / `hidden` / `takenOver` | varint bool | ✔ | — |
| 29 | `stopWallTimeMs` | fixed64 | ✔ | 有限、≥ 0 |
| 30 | `timeAfterStoppedMs` | fixed64 | ✔ | 有限、≥ 0 |
| 31 | `tombstoneUntilMs` | fixed64 | ✔ | 有限、≥ 0 |
| 32 | `hitTargets[]` | varint（可重复，0..100） | — | 每项非 0 |
| 33 | `allowedTargets[]` | varint（可重复，0..100） | — | 每项非 0 |
| 34 | `spec` | length-delimited msg | ✔ | **必填**（权威配置不允许静默默认值） |

Spec（嵌套 message，1 起，**7 项全部必填**）：

| field | 字段 | wire | 约束 |
|---|---|---|---|
| 1 | `speedMps` | fixed32 | 有限、≥ 0（与 A2 验证器 `SpecInvalid` 口径一致） |
| 2 | `radiusM` | fixed32 | 有限、≥ 0 |
| 3 | `lifetimeMs` | varint | ≥ 0 |
| 4 | `delayDestroyMs` | varint | ≥ 0 |
| 5/6/7 | `stopOnHit` / `hideOnStop` / `skipFlyingTrajectoryValidation` | varint bool | — |

Key 只在头部承载，decode 时回填到 `State.Key`。该类型只能由 State+Spec 构造，
因此**不可能**把客户端 `SpawnRequest` 序列化后冒充 trusted Spec。

### 4.4 Decision（kind=4，**下行**）

| field | 字段 | wire | 必填 | 约束 |
|---|---|---|---|---|
| 10 | `activationId` | varint | ✔ | **非 0**（激活账本以 0 为非法） |
| 11 | `result` | varint | ✔ | 只允许 1=Confirmed / 2=Rejected；**0=Pending 直接拒**，>2 未知拒 |
| 12 | `reason` | length-delimited | ✔ | UTF-8 ≤ **64 字节**；读侧**先偷看长度前缀判上限再分配** |

`Reason` 选「有界字符串」而不是自造枚举：契约允许二者之一，字符串让 codec 不必凭空冻结一套拒绝原因
枚举去和权威/R6 的口径打架。唯一一处宽松归一：`Reason == null` → 空串（诊断串，非关键字段）。

---

## 5. 强校验清单（编解码**同一套**，双端对称）

1. **身份**：`Epoch/OwnerNetId/ProjectileId` 非 0 且 `Origin` ∈ {1,2}；`PMProjectileKey.IsValid` 复核。
2. **origin 权限**：上行 SpawnIntent / HitBatch **只允许 ClientPredicted**（客户端自称 ServerDirect
   在 wire 边界即不可表达）；下行 Snapshot / Decision 不额外设限。
3. **有限性**：所有 float/double（位置/速度/时间/spec/候选点位）NaN、±Inf 一律拒。
4. **yaw 规范**：只接受 `[0,360)`；`-0.0f` 按 0 接受。非规范值**不**静默归一化（编解码一致）。
5. **方向**：有限、非零、**长度平方有限**（分量 1e30 → 长度平方 +Inf、1e-30 → 下溢 0，都拒），
   避免权威侧朴素归一化得到 NaN / 零向量。
6. **范围**：`predictionMs ∈ [0,500]`；`rewindMs ≥ 0`（**不**在 codec 截断 >1000）；
   时间字段 ≥ 0；`targetServerFrame.value > 0`；`targetStreamVersion != 0`；目标 NetId != 0；
   `authorityNetId != 0`；`activationId != 0`（上行 spawn 与决策）。
7. **帧域**：候选帧锚必须 `AuthorityServer`，错域（None/Input/SimTime/Session）一律拒。
8. **数组上限**：命中候选 0..100；快照 HitTargets / AllowedTargets 各 0..100。
9. **长度上限**：单载荷 ≤ **16384** 字节（读侧在**任何分配之前**先判 `count`）；
   有界字符串先 `PeekVarintLength()` 判上限再 `ReadStringValue()`；
   **数组一律按上限预分配**（`new T[100]` + 计数），绝不用对端声明的长度去分配。
10. **wire 结构**：字段号不得倒退；标量不得重复；必填字段必须全部出现（缺失位掩码进 error）；
    读完后必须恰好到底（尾部字节拒）；wire type 逐字段校验；kind/version 严格匹配。
11. **数组复制**：解码产出的数组是**新数组**（`Array.Copy` 出精确长度），不与载荷/内部缓冲别名；
    `PMProjectileSnapshot.Clone()` 深拷贝 State/Spec 与全部数组。

**明确不做**（避免与 A2/A3 重复，也避免与它们口径打架）：segment ≤ 20m、visualOffset ≤ 20m、
空命中批判定、目标白名单、飞行预算（L2）、逐目标几何（L3）、rewind > 1000 的 Report 记录、
墓碑/配额、ID 分配。这些是 `PMProjectileValidator`（L0–L4）与 Coordinator 的职责。

---

## 6. 验证与结果

命令（先 build 再 run，任何一步非 0 即为失败）：

```
dotnet build Tools/PMProjectileCodecCheck/PMProjectileCodecCheck.csproj -c Release   # exit 0，0 警告 0 错误
dotnet build Tools/PMProjectileCodecTest/PMProjectileCodecTest.csproj  -c Release   # exit 0，0 警告 0 错误
dotnet Tools/PMProjectileCodecTest/bin/Release/net8.0/PMProjectileCodecTest.dll      # exit 0，通过 395 / 失败 0
python Tools/check_cs_braces.py <两个新 .cs>                                          # PASS（括号平衡）
```

| 节 | 覆盖 | 结果 |
|---|---|---|
| A | SpawnIntent 全字段非默认往返 + 边界（yaw 0/359.999、predMs 0/500）+ 字节级往返 + 手写字节对照 | OK |
| B | HitBatch 全字段往返、0/100 候选、101 拒、rewind -1/0/500/1001 | OK |
| C | Snapshot State+Spec 全字段往返、空/100+100 数组、101 拒、AuthorityNetId=0 双向拒 | OK |
| D | Decision Confirmed/Rejected 往返、Pending(0)/未知(3) 拒、Reason 64/65 字节与 UTF-8 字节计 | OK |
| E | 逐字段缺必填：Spawn 9 + Hit 7 + Snapshot 23 + Decision 3 + 候选 10 + spec 7 = **59 项** | OK |
| F | 字段乱序/标量重复/未知字段/错 wire（11 处）/尾部字节/头部乱序重复缺字段 | OK |
| G | 逐字节截断（Spawn/Snapshot/Decision **全部**拒；HitBatch 见 §7 登记）+ null/空/越界 | OK |
| H | Reason 长度前缀 65 字节 / 声明 50 实际 3 / ulong 最大值前缀 | OK |
| I | NaN/±Inf（位置/方向/yaw/时间/spec）、方向溢出与下溢、负值编码、predMs 501 | OK |
| J | epoch/owner/projectile 为 0、origin 0/3、上行 origin=ServerDirect、快照/裁决两 origin 允许、候选帧域 4 种错域、version=2、kind 0/5 | OK |
| K | 上行注入 spec(field 34) / 9..35 逐个注入 / 跨 kind 错用 4 组 | OK |
| L | 两次解码数组互不影响、改载荷不影响已解码结果、`Snapshot.Clone` 双向隔离、空数组非 null | OK |
| M | 常量值、16385 字节拒、实测长度、**100 候选 > 4096 登记项** | OK |

实测线长（供 B2 预算参考）：

| 载荷 | 字节数 |
|---|---|
| SpawnIntent（全字段） | 58 |
| HitBatch（3 候选） | 132 |
| HitBatch（100 候选） | **4320 ~ 4321** |
| Snapshot（全字段 + 5 目标） | 181 |
| Snapshot（100 + 100 目标） | 966 |

---

## 7. 剩余限制与登记项（**必须由 B2 处理**，B1 不冒充已解决）

1. **生成桩 `byte[]` 上限 4096 < codec 上限 16384（最硬的接线缺口）**。
   `PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs` 里 `PMGeneratedMaxArrayLength = 4096`
   （由 `Tools/PMNetGen/DeclEmitter.cs` 的 `MaxArrayLength` 决定）。
   **实测 100 候选命中批 = 4320~4321 字节 > 4096**，所以「一遍发 100 个候选」在现有生成桩上**发不出去**。
   B1 按任务口径**只登记不修**：没有改声明源 / generated / `PMR3Player`（那属于 B2 的统一生成集合）。
   B2 二选一：**(a)** 切批（按 4096 预算把候选分成多包，例如每包 ~90 个）；或 **(b)** 提升声明上限
   （需改 `DeclEmitter.MaxArrayLength` 并重新生成、复核所有 `byte[]` 参数与 MTU/分片预算）。
   codec 侧的上限 16384 只是「独立编解码上限」，**不宣称现在能直发 16KB**。
2. **HitBatch 仅上行且 origin 只允许 ClientPredicted**。依据：契约「客户端只能 ClientPredicted」+
   「ServerDirect 镜像不收集候选/不自行结算」。若 B2 发现需要 DS→端 的 HitBatch（本报告未找到该需求），
   需要把该限制改成显式的方向参数，并重新过负向用例。
3. **Decision.Reason 是自由串（≤64 字节），不是枚举**。R6 若需要程序化分支，需要自己把原因码映射进该串
   （或后续引入与权威共享的枚举并同步两端）。
4. **`ownerNetId` / `origin` 来自发送侧，不是认证事实**。B2 必须用认证会话绑定的 owner 覆盖
   （契约「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报」）；B1 只能保证「上游不可能自称
   ServerDirect」，不能保证 owner 真实性。
5. **protobuf 无包内总长 ⇒ 截断到 repeated 字段边界时产物是「结构完整但更短的合法载荷」**（实测 HitBatch
   有 2 个这样的切点：0 候选 / 1 候选）。这是格式固有性质，不是半应用；其余截断全部拒。
   若 B2 需要「传输层截断必须整包拒」的强保证，需在承载层加总长/校验（属 B2/B3 的传输设计）。
6. **候选数组的上行「同一弹同一目标仅一次」去重、空批拒绝、多目标独立尺寸、ImpactPoint 钳制等仍在 A2/L0–L4**。
7. **未接线的部分不算完成**：真实 PMR3 RPC 收发、认证 owner/muzzle admission、ID 生成集合、
   Unity 宿主与输入、双端实机（T45）全部 **PENDING**。

---

## 8. 兼容性与纪律

- **不兼容旧 schema**：`version` 只接受 1；`kind` 只接受 1..4；任何未知/缺/重复/错序字段即拒 —— 与契约
  「未知/重复/错 wire/缺字段/尾部/超长全拒，不兼容旧 schema」逐条对应。
- **零 Unity 依赖**：`Tools/PMProjectileCodecCheck` 用 netstandard2.0 + C# 7.3 **逐文件** Include 编译
  （刻意不用 `PMProjectile/**/*.cs` 通配，避免把并行 A1/A2/A3 文件编进来造成跨组耦合），
  通过即证明生产 codec 在 Unity 2019.4 语言面内且不引 UnityEngine。
- **不引 Google.Protobuf**：只使用项目自有的 `PMNetReader/PMNetWriter` protobuf 原语（逐字节对齐线格式）。
- 未启动 Unity、未保存/提交资产、未执行任何 SVN/git 写操作。

---

## 9. 诚实边界总结

- 本报告证明的是 **B1 的编解码不变量**（身份/权限/有限性/规范值/有界/结构严格/复制隔离）在
  **真实生产字节**上成立，且有独立手写字节的负向对照。
- **没有**证明：真实 RPC 能承载（§7.1 的 4096 缺口）、认证 owner 生效、权威 ID 生成唯一、
  命中验证的几何正确性（属 A2）、端到端实机表现（属 C/T45）。
- 因此 B1 的完成**不等于** B 阶段或整阶段 T45 完成。
