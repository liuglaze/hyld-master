// R5-B1 / T5B1：投射物网络的**专用载荷编解码**（契约 Docs/plans/net-r5-projectile-contract.md §B1，
// 尾段「A3/B1 集成冻结」逐条落地）。
//
// 为什么需要这一层（而不是把 DTO 直接交给 PMR3 生成桩）：
//   冻结声明把承载定成 `byte[] payload`。契约要求「blob 内部由专门 codec 用 PMNetReader/PMNetWriter
//   的 protobuf 原语编码」「Version=1」「字段头 kind=1/version=2/epoch=3/owner=4/projectileId=5/origin=6」
//   「载荷字段 10 起按单调 field 顺序」「未知/重复/错 wire/缺字段/尾部/超长全拒，不兼容旧 schema」。
//   因此本文件只做三件事：**固定字段号 + 强校验 + 有界**。
//
// 六个不变量（每条都有 Tools/PMProjectileCodecTest 的用例）：
//   1) 每种载荷带头部 kind，**跨通道喂错载荷在读头部时就被拒绝**；
//   2) version 必须 == 1，不兼容即拒；
//   3) 身份四件套 Epoch/OwnerNetId/ProjectileId/Origin 全在头部；**Key 不在载荷里重复出现**，
//      避免「头部与载荷两份身份互相矛盾」这种最难查的错；
//   4) 所有 float/double 必须有限（NaN/±Inf 一律拒绝）；yaw 只允许规范值 [0,360)；
//      direction 必须有限、非零，且**其长度平方必须有限**（否则权威归一化会得到 NaN）；
//   5) 数组/条数全部有上限（命中批 100、快照 HitTargets/AllowedTargets 各 100、Reason ≤ 64 字节），
//      且**读侧按上限预分配**，绝不用对端声明的长度去分配；
//   6) 字段号不得倒退、标量字段不得重复、必填字段必须全部存在、读完后必须恰好到底。
//
// 上行 / 下行的权限边界（B1 只做「表示层」的强制；OwnerNetId 的真实认证在 B2）：
//   · 上行 SpawnIntent / HitBatch —— origin **只允许 ClientPredicted**。
//     这样「客户端自称 ServerDirect」在 wire 边界就**不可表达**，而不是等权威层去识破。
//   · 上行 SpawnIntent —— **完全没有** spec / AuthorityNetId / HitTargets / Stopped 等权威字段；
//     载荷里只有 ActivationId/Position/Direction/Yaw/PredictionMs。
//   · 下行 Snapshot —— State + Spec；AuthorityNetId 必须非 0（没有权威身份的「快照」是伪快照）。
//     **注意**：头部 OwnerNetId/Origin 来自发送侧，B1 **不**把它当认证事实；B2 必须用认证会话
//     绑定的 owner 覆盖后再进 Coordinator（契约「OwnerNetId 必须取认证会话绑定网络玩家」）。
//   · 下行 Decision —— Result 只允许 Confirmed/Rejected；**Pending 是伪终态，直接拒绝**。
//
// 本文件**不做**的事（避免与 A2 验证器 / A3 协调器重复）：
//   · 不做几何与预算判定：segment ≤ 20m、visualOffset ≤ 20m、空命中批、目标白名单、
//     飞行预算、rewind > 1000ms 的 Report —— 全部留给 PMProjectileValidator（L0–L4）与 Coordinator。
//     codec 只保证「线上的数是可表示的、有限的、有界的、身份自洽的」。
//   · 不分配 ID（契约：ID 锁在 B2 的统一生成集合里再分配），不碰 PMR3Player / generated。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0），只依赖 PMNet 原语、PMNet.Mover 与共享契约。
// 本文件**不得**引用 UnityEngine，也**不得**引用并行 Coordinator（PMProjectileLifecycle /
// PMProjectilePending / PMProjectileValidator / PMProjectileHistory）——门禁
// Tools/PMProjectileCodecCheck 用「逐文件 Include」把这条纪律变成可编译的检查。
//
// 错误口径（按本批要求，与 R4 codec 的「encode 抛异常」不同）：
//   **编解码一律返回 false + error，不抛异常、不半应用、不返回部分对象**。
//   契约要求「所有非法输入返回 false+error 而非部分对象」，且 encode 侧执行与 decode 同等强校验，
//   因此不需要异常来区分「编程错误」。内部来自 PMNetReader 的 FormatException/OverflowException
//   （越界、varint 溢出）同样被捕获并转成 false + error。
//
// 长度口径（诚实登记，见 Docs/plans/_r5_codec_report.md）：
//   · codec 独立上限 16384 字节（契约 §B1）；
//   · 现有 PMR3 生成桩的 `byte[]` 参数上限是 PMGeneratedMaxArrayLength = 4096（生成代码里读出来的），
//     **100 个候选的命中批会超过 4096**。B1 不改声明 / 不改 generated，所以这里只把事实暴露成
//     常量 MaxGeneratedRpcBytes 并写入报告：**B2 必须切批或提升声明上限**，本文件不宣称现在能直发 16KB。

using System;
using System.Text;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>
    /// 载荷种类（wire 头部字段 1）。放在 blob 头部而不是靠「哪条 RPC」区分：
    /// 这样「快照载荷被喂给上行 spawn 通道」这类错用**在读头部时就被拒绝**。
    /// </summary>
    public enum PMProjectileWireKind : byte
    {
        None = 0,

        /// <summary>上行 spawn 意图（客户端 → DS）。</summary>
        SpawnIntent = 1,

        /// <summary>上行命中批（客户端 → DS，供 L0–L4 验证）。</summary>
        HitBatch = 2,

        /// <summary>下行权威快照（DS → 相关端）：State + Spec。</summary>
        Snapshot = 3,

        /// <summary>下行激活裁决（DS → owner）：Confirmed / Rejected。</summary>
        Decision = 4,
    }

    /// <summary>
    /// 上行 spawn 意图（**codec 专用 DTO**）。
    ///
    /// 字段刻意只有六个：Key / ActivationId / Position / Direction / Yaw / PredictionMs。
    /// 契约 §B1 明确「禁止上行 Spec / AuthorityNetId / HitTargets / Stopped 等权威字段」——
    /// 客户端上传的是**意图**，不是权威配置；速度/半径/寿命由 DS 的 trustedSpec 决定。
    /// </summary>
    public sealed class PMProjectileSpawnIntent
    {
        /// <summary>四元组身份；Origin 上行只允许 <see cref="PMProjectileOrigin.ClientPredicted"/>。</summary>
        public PMProjectileKey Key;

        /// <summary>激活 ID。上行必须非 0（0 只允许可信 ServerDirect；见契约「激活ID=0仅可信ServerDirect可用」）。</summary>
        public uint ActivationId;

        /// <summary>枪口/出生点世界坐标（Y-up 米）。必须有限。</summary>
        public PMVector3 Position;

        /// <summary>朝向意图。必须有限且非零（权威侧再归一化）。</summary>
        public PMVector3 Direction;

        /// <summary>朝向角（度）。线上只承载规范值 [0,360)。</summary>
        public float Yaw;

        /// <summary>预测提前量（毫秒），范围 [0,500]。</summary>
        public int PredictionMs;

        public PMProjectileSpawnIntent Clone()
        {
            return new PMProjectileSpawnIntent
            {
                Key = Key,
                ActivationId = ActivationId,
                Position = Position,
                Direction = Direction,
                Yaw = Yaw,
                PredictionMs = PredictionMs,
            };
        }
    }

    /// <summary>
    /// 下行权威快照：<see cref="PMProjectileState"/> + <see cref="PMProjectileSpec"/>。
    ///
    /// Key 只在头部承载一次（decode 时回填到 State.Key），载荷里不再重复，避免两份身份互相矛盾。
    /// 该类型只能由 State+Spec 构造，因此**不可能**把客户端的 SpawnRequest 序列化后冒充 trusted Spec。
    /// </summary>
    public sealed class PMProjectileSnapshot
    {
        public PMProjectileState State;
        public PMProjectileSpec Spec;

        /// <summary>深克隆：State/Spec 与其中的数组全部为新对象，不与源共享可变状态。</summary>
        public PMProjectileSnapshot Clone()
        {
            return new PMProjectileSnapshot
            {
                State = State == null ? null : State.Clone(),
                Spec = Spec == null ? null : Spec.Clone(),
            };
        }
    }

    /// <summary>
    /// 下行激活裁决。Result **只允许** Confirmed / Rejected —— Pending 是伪终态，wire 上直接拒绝
    /// （契约「终态不得被迟到 Pending/Confirmed 反转」，与 Coordinator 的激活账本口径一致）。
    ///
    /// Reason 是**有界诊断串**（UTF-8 ≤ 64 字节）：契约允许「固定枚举或有界字符串」，这里选后者，
    /// 这样 codec 不必凭空冻结一套拒绝原因枚举去和权威/R6 的口径打架；R6 可以把它的原因码写进这里。
    /// </summary>
    public sealed class PMProjectileDecision
    {
        /// <summary>四元组身份；Origin 不做上行限制（下行两侧 origin 都可能）。</summary>
        public PMProjectileKey Key;

        /// <summary>
        /// 激活 ID。**随 origin 变化**（契约「激活ID=0仅可信ServerDirect可用」）：
        /// ClientPredicted 必须非 0；ServerDirect 允许 0（可信权威弹没有客户端激活账本项，
        /// 但仍可能需要下发终态裁决 —— codec 不能把「DS 想表达」这件事本身判成非法 wire）。
        /// </summary>
        public uint ActivationId;

        /// <summary>终态结果：Confirmed 或 Rejected。Pending 会被编解码两侧拒绝。</summary>
        public PMActivationResult Result;

        /// <summary>有界诊断原因（≤ 64 UTF-8 字节）。null 视为空串（唯一的一处宽松归一）。</summary>
        public string Reason = string.Empty;

        public PMProjectileDecision Clone()
        {
            return new PMProjectileDecision
            {
                Key = Key,
                ActivationId = ActivationId,
                Result = Result,
                Reason = Reason,
            };
        }
    }

    /// <summary>
    /// 投射物载荷编解码。全部字段号与上限都在这里**单点定义**（不散到调用方）。
    /// 编解码两侧执行同一套强校验：返回 false + error，绝不半应用。
    /// </summary>
    public static class PMProjectileCodec
    {
        // ================================================================ 冻结常量

        /// <summary>blob 内部编码版本（契约 §B1「Version=1」）。版本更替走新声明名。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>codec 的独立载荷上限（契约 §B1「payload ≤ 16384」）。</summary>
        public const int MaxPayloadBytes = 16384;

        /// <summary>
        /// **登记项**：现有 PMR3 生成桩的 `byte[]` 参数上限（生成代码里读出的 PMGeneratedMaxArrayLength）。
        /// 100 个候选的命中批会超过它 → B2 必须切批或提升声明上限。B1 不改声明/generated。
        /// </summary>
        public const int MaxGeneratedRpcBytes = 4096;

        /// <summary>一批命中候选的上限（契约「每批 100 目标」）。</summary>
        public const int MaxBatchTargets = 100;

        /// <summary>快照里 HitTargets / AllowedTargets 各自的上限（契约「每 key 目标去重有界 100」）。</summary>
        public const int MaxSnapshotTargets = 100;

        /// <summary>Decision.Reason 的字节上限（UTF-8）。</summary>
        public const int MaxReasonBytes = 64;

        /// <summary>预测提前量下限（毫秒）。</summary>
        public const int MinPredictionMs = 0;

        /// <summary>预测提前量上限（毫秒，契约「预测 ms[0,500]」）。</summary>
        public const int MaxPredictionMs = 500;

        /// <summary>
        /// rewind 的 **Report 阈值**（毫秒）。注意：这是「要被观测」的阈值，**不是** codec 的硬上限 ——
        /// 契约要求 rewind&gt;1000 只 Report 但**仍检查所有候选**，所以 codec 只要求 ≥ 0，
        /// 绝不在 wire 上截断它（截断会让 Verify 看不到真实值）。
        /// </summary>
        public const int RewindReportThresholdMs = 1000;

        /// <summary>朝向的规范范围：线上只允许 [0,360)。</summary>
        public const float MaxYawDegrees = 360f;

        // ---- 头部字段号（契约冻结：kind=1/version=2/epoch=3/owner=4/projectileId=5/origin=6）----

        private const int FKind = 1;
        private const int FVersion = 2;
        private const int FEpoch = 3;
        private const int FOwner = 4;
        private const int FProjectile = 5;
        private const int FOrigin = 6;

        // ---- 上行 SpawnIntent 字段号（10 起，单调）----

        private const int FSpawnActivation = 10;
        private const int FSpawnPosX = 11;
        private const int FSpawnPosY = 12;
        private const int FSpawnPosZ = 13;
        private const int FSpawnDirX = 14;
        private const int FSpawnDirY = 15;
        private const int FSpawnDirZ = 16;
        private const int FSpawnYaw = 17;
        private const int FSpawnPredictionMs = 18;

        // ---- 上行 HitBatch 字段号 ----

        private const int FHitPrevX = 10;
        private const int FHitPrevY = 11;
        private const int FHitPrevZ = 12;
        private const int FHitPosX = 13;
        private const int FHitPosY = 14;
        private const int FHitPosZ = 15;
        private const int FHitRewindMs = 16;
        private const int FHitCandidate = 17;

        // ---- 命中候选字段号（嵌套 message，1 起）----

        private const int FCandTargetNetId = 1;
        private const int FCandStreamVersion = 2;
        private const int FCandFrameDomain = 3;
        private const int FCandFrameValue = 4;
        private const int FCandImpactX = 5;
        private const int FCandImpactY = 6;
        private const int FCandImpactZ = 7;
        private const int FCandOffsetX = 8;
        private const int FCandOffsetY = 9;
        private const int FCandOffsetZ = 10;

        // ---- 下行 Snapshot 字段号 ----

        private const int FSnapAuthority = 10;
        private const int FSnapActivation = 11;
        private const int FSnapSpawnX = 12;
        private const int FSnapSpawnY = 13;
        private const int FSnapSpawnZ = 14;
        private const int FSnapPrevX = 15;
        private const int FSnapPrevY = 16;
        private const int FSnapPrevZ = 17;
        private const int FSnapPosX = 18;
        private const int FSnapPosY = 19;
        private const int FSnapPosZ = 20;
        private const int FSnapVelX = 21;
        private const int FSnapVelY = 22;
        private const int FSnapVelZ = 23;
        private const int FSnapYaw = 24;
        private const int FSnapMoveTimeMs = 25;
        private const int FSnapStopped = 26;
        private const int FSnapHidden = 27;
        private const int FSnapTakenOver = 28;
        private const int FSnapStopWallMs = 29;
        private const int FSnapTimeAfterStopMs = 30;
        private const int FSnapTombstoneUntilMs = 31;
        private const int FSnapHitTarget = 32;
        private const int FSnapAllowedTarget = 33;
        private const int FSnapSpec = 34;

        // ---- Spec 字段号（嵌套 message，1 起）----

        private const int FSpecSpeed = 1;
        private const int FSpecRadius = 2;
        private const int FSpecLifetime = 3;
        private const int FSpecDelayDestroy = 4;
        private const int FSpecStopOnHit = 5;
        private const int FSpecHideOnStop = 6;
        private const int FSpecSkipTrajectory = 7;

        // ---- 下行 Decision 字段号 ----

        private const int FDecActivation = 10;
        private const int FDecResult = 11;
        private const int FDecReason = 12;

        /// <summary>必填位掩码：位下标 = 字段号 - 10。SpawnIntent 的 10..18 全部必填。</summary>
        private const uint SpawnRequiredMask = 0x1FFu;

        /// <summary>HitBatch 的 10..16 全部必填（候选数组可空，由 L0 判 NoTargets）。</summary>
        private const uint HitRequiredMask = 0x7Fu;

        /// <summary>命中候选的 1..10 全部必填。</summary>
        private const uint CandidateRequiredMask = 0x3FFu;

        /// <summary>快照 10..34 全部必填，**除了**两个数组字段（32/33 允许为空）。</summary>
        private const uint SnapshotRequiredMask = 0x1FFFFFFu & ~(1u << (FSnapHitTarget - 10)) & ~(1u << (FSnapAllowedTarget - 10));

        /// <summary>Spec 的 1..7 全部必填（权威配置不允许有「静默默认值」）。</summary>
        private const uint SpecRequiredMask = 0x7Fu;

        /// <summary>Decision 的 10..12 全部必填。</summary>
        private const uint DecisionRequiredMask = 0x7u;

        /// <summary>「没有可重复字段」的哨兵（见 <see cref="IsFieldOrderAccepted"/>）。</summary>
        private const int NoRepeatable = -1;

        private static readonly uint[] NoUInts = new uint[0];
        private static readonly PMProjectileHitCandidate[] NoCandidates = new PMProjectileHitCandidate[0];

        // ================================================================ 公共校验原语

        /// <summary>float 必须有限（NaN / ±Inf 一律拒绝）。</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>double 必须有限。</summary>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Key 自洽：Epoch/OwnerNetId/ProjectileId 非 0 且 Origin 是已知取值。</summary>
        public static bool IsValidKey(PMProjectileKey key)
        {
            return key.IsValid;
        }

        /// <summary>上行唯一允许的 origin。</summary>
        public static bool IsUplinkOrigin(PMProjectileOrigin origin)
        {
            return origin == PMProjectileOrigin.ClientPredicted;
        }

        /// <summary>
        /// 朝向是否**已经是**规范表示 [0,360)。编解码两侧都用它：非规范值不是「需要归一化的输入」，
        /// 而是畸形数据 → 拒绝（否则同一朝向会出现两种线上表示，去重/比较全部失真）。
        /// 注：-0.0f 与 0.0f 比较相等，按 0 接受。
        /// </summary>
        public static bool IsCanonicalYaw(float yaw)
        {
            return IsFinite(yaw) && yaw >= 0f && yaw < MaxYawDegrees;
        }

        /// <summary>把任意有限角度折算到 [0, 360)。非有限值返回 false。</summary>
        public static bool TryNormalizeYaw(float yaw, out float normalized)
        {
            normalized = 0f;
            if (!IsFinite(yaw))
            {
                return false;
            }

            float wrapped = yaw % MaxYawDegrees;
            if (wrapped < 0f)
            {
                wrapped += MaxYawDegrees;
            }

            if (wrapped == MaxYawDegrees || wrapped == 0f)
            {
                wrapped = 0f;
            }

            normalized = wrapped;
            return true;
        }

        /// <summary>
        /// 方向向量是否可用于上行：有限、非零，且**长度平方有限**。
        ///
        /// 最后一条不是洁癖：若分量大到长度平方溢出为 +Inf，权威侧任何朴素归一化都会算出 NaN；
        /// 若小到长度平方下溢为 0，归一化会得到零向量。两者都会把「脏数据」变成「静默的错物理」，
        /// 所以在 wire 边界就拒掉。
        /// </summary>
        public static bool IsValidDirection(PMVector3 direction)
        {
            if (!direction.IsFinite)
            {
                return false;
            }

            float lengthSquared = direction.LengthSquared;
            return IsFinite(lengthSquared) && lengthSquared > 0f;
        }

        // ================================================================ 编码：上行 SpawnIntent

        /// <summary>
        /// 编码上行 spawn 意图。任何非法输入 ⇒ false + error（**不抛异常、不写出部分载荷**）。
        /// </summary>
        public static bool TryEncodeSpawnIntent(PMProjectileSpawnIntent intent,
                                                out byte[] payload, out string error)
        {
            payload = null;

            if (intent == null)
            {
                error = "spawn 意图为 null";
                return false;
            }

            if (intent.Key.Origin != PMProjectileOrigin.ClientPredicted)
            {
                error = "上行 spawn 意图的 origin 只能是 ClientPredicted（收到 " + intent.Key.Origin + "）";
                return false;
            }

            if (!IsValidKey(intent.Key))
            {
                error = "spawn 意图的 Key 非法（Epoch/OwnerNetId/ProjectileId 必须非 0，Origin 必须已知）："
                        + FormatKey(intent.Key);
                return false;
            }

            if (intent.ActivationId == 0u)
            {
                error = "上行 spawn 意图的 ActivationId 必须非 0（0 只允许可信 ServerDirect）";
                return false;
            }

            if (!intent.Position.IsFinite)
            {
                error = "spawn 意图的 Position 出现 NaN/Infinity";
                return false;
            }

            if (!IsValidDirection(intent.Direction))
            {
                error = "spawn 意图的 Direction 必须有限、非零且长度平方有限（收到 " + intent.Direction + "）";
                return false;
            }

            if (!IsCanonicalYaw(intent.Yaw))
            {
                error = "spawn 意图的 Yaw 必须是规范值 [0,360)（收到 " + intent.Yaw + "）";
                return false;
            }

            if (intent.PredictionMs < MinPredictionMs || intent.PredictionMs > MaxPredictionMs)
            {
                error = "spawn 意图的 PredictionMs 必须在 " + MinPredictionMs + ".." + MaxPredictionMs
                        + "（收到 " + intent.PredictionMs + "）";
                return false;
            }

            PMNetWriter writer = new PMNetWriter(96);
            WriteHeader(writer, PMProjectileWireKind.SpawnIntent, intent.Key);

            writer.WriteTag(FSpawnActivation, PMWireType.Varint);
            writer.WriteVarint(intent.ActivationId);
            WriteVector3(writer, FSpawnPosX, intent.Position);
            WriteVector3(writer, FSpawnDirX, intent.Direction);
            writer.WriteTag(FSpawnYaw, PMWireType.Fixed32);
            writer.WriteFloat(intent.Yaw);
            writer.WriteTag(FSpawnPredictionMs, PMWireType.Varint);
            writer.WriteVarint((ulong)intent.PredictionMs);

            return Finish(writer, "上行 spawn 意图", out payload, out error);
        }

        /// <summary>解码上行 spawn 意图。远端数据不可信 ⇒ 任何疑问都 false（fail-closed）。</summary>
        public static bool TryDecodeSpawnIntent(byte[] payload, int offset, int count,
                                                out PMProjectileSpawnIntent intent, out string error)
        {
            intent = null;

            PMProjectileOpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMProjectileWireKind.SpawnIntent, true, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMProjectileSpawnIntent result = new PMProjectileSpawnIntent();
            result.Key = open.Key;

            uint seen = 0u;
            int lastField = FOrigin;
            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, NoRepeatable, NoRepeatable, out error))
                    {
                        return false;
                    }

                    if (field < FSpawnActivation || field > FSpawnPredictionMs)
                    {
                        error = "上行 spawn 意图出现未知字段 " + field
                                + "（上游不得携带 spec / AuthorityNetId / HitTargets / Stopped 等权威字段）";
                        return false;
                    }

                    seen |= 1u << (field - FSpawnActivation);

                    switch (field)
                    {
                        case FSpawnActivation:
                            if (wire != PMWireType.Varint) { error = "activationId 的 wire type 不对"; return false; }
                            result.ActivationId = checked((uint)reader.ReadVarint());
                            break;
                        case FSpawnPosX:
                            if (wire != PMWireType.Fixed32) { error = "positionX 的 wire type 不对"; return false; }
                            result.Position.X = reader.ReadFloat();
                            break;
                        case FSpawnPosY:
                            if (wire != PMWireType.Fixed32) { error = "positionY 的 wire type 不对"; return false; }
                            result.Position.Y = reader.ReadFloat();
                            break;
                        case FSpawnPosZ:
                            if (wire != PMWireType.Fixed32) { error = "positionZ 的 wire type 不对"; return false; }
                            result.Position.Z = reader.ReadFloat();
                            break;
                        case FSpawnDirX:
                            if (wire != PMWireType.Fixed32) { error = "directionX 的 wire type 不对"; return false; }
                            result.Direction.X = reader.ReadFloat();
                            break;
                        case FSpawnDirY:
                            if (wire != PMWireType.Fixed32) { error = "directionY 的 wire type 不对"; return false; }
                            result.Direction.Y = reader.ReadFloat();
                            break;
                        case FSpawnDirZ:
                            if (wire != PMWireType.Fixed32) { error = "directionZ 的 wire type 不对"; return false; }
                            result.Direction.Z = reader.ReadFloat();
                            break;
                        case FSpawnYaw:
                            if (wire != PMWireType.Fixed32) { error = "yaw 的 wire type 不对"; return false; }
                            result.Yaw = reader.ReadFloat();
                            break;
                        case FSpawnPredictionMs:
                            if (wire != PMWireType.Varint) { error = "predictionMs 的 wire type 不对"; return false; }
                            result.PredictionMs = checked((int)reader.ReadVarint());
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "上行 spawn 意图解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "上行 spawn 意图解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "上行 spawn 意图解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & SpawnRequiredMask) != SpawnRequiredMask)
            {
                error = "上行 spawn 意图缺少必填字段（缺失位掩码 0x"
                        + (~seen & SpawnRequiredMask).ToString("X8") + "）";
                return false;
            }

            string why;
            if (!ValidateSpawnSemantics(result, out why))
            {
                error = "上行 spawn 意图违反上行契约：" + why;
                return false;
            }

            intent = result;
            error = null;
            return true;
        }

        /// <summary>解码便利重载（整段载荷）。</summary>
        public static bool TryDecodeSpawnIntent(byte[] payload, out PMProjectileSpawnIntent intent, out string error)
        {
            return TryDecodeSpawnIntent(payload, 0, payload == null ? 0 : payload.Length, out intent, out error);
        }

        private static bool ValidateSpawnSemantics(PMProjectileSpawnIntent intent, out string error)
        {
            if (intent.ActivationId == 0u)
            {
                return Fail("ActivationId 为 0（0 只允许可信 ServerDirect）", out error);
            }

            if (!intent.Position.IsFinite)
            {
                return Fail("Position 出现 NaN/Infinity", out error);
            }

            if (!IsValidDirection(intent.Direction))
            {
                return Fail("Direction 必须有限、非零且长度平方有限（收到 " + intent.Direction + "）", out error);
            }

            if (!IsCanonicalYaw(intent.Yaw))
            {
                return Fail("Yaw 不是规范值 [0,360)（收到 " + intent.Yaw + "）", out error);
            }

            if (intent.PredictionMs < MinPredictionMs || intent.PredictionMs > MaxPredictionMs)
            {
                return Fail("PredictionMs 超出 " + MinPredictionMs + ".." + MaxPredictionMs
                            + "（收到 " + intent.PredictionMs + "）", out error);
            }

            error = null;
            return true;
        }

        // ================================================================ 编码：上行 HitBatch（共享冻结类型）

        /// <summary>
        /// 编码一批上行命中候选（**共享冻结类型** <see cref="PMProjectileHitBatch"/>，不自造 DTO）。
        /// 候选条数 0..100：**空批在 codec 层合法**（L0 会以 NoTargets 拒绝），codec 只保证有界。
        /// </summary>
        public static bool TryEncodeHitBatch(PMProjectileHitBatch batch, out byte[] payload, out string error)
        {
            payload = null;

            if (batch == null)
            {
                error = "命中批为 null";
                return false;
            }

            if (batch.Key.Origin != PMProjectileOrigin.ClientPredicted)
            {
                error = "上行命中批的 origin 只能是 ClientPredicted（收到 " + batch.Key.Origin + "）";
                return false;
            }

            if (!IsValidKey(batch.Key))
            {
                error = "命中批的 Key 非法：" + FormatKey(batch.Key);
                return false;
            }

            if (!batch.PreviousPosition.IsFinite || !batch.HitPosition.IsFinite)
            {
                error = "命中批的 PreviousPosition/HitPosition 出现 NaN/Infinity";
                return false;
            }

            if (batch.RewindMs < 0)
            {
                error = "命中批的 RewindMs 为负（" + batch.RewindMs + "）；只允许非负，阈值 "
                        + RewindReportThresholdMs + "ms 仅供调用方 Report，codec 不截断";
                return false;
            }

            PMProjectileHitCandidate[] targets = batch.Targets;
            if (targets == null)
            {
                targets = NoCandidates;
            }

            if (targets.Length > MaxBatchTargets)
            {
                error = "命中候选数 " + targets.Length + " 超过上限 " + MaxBatchTargets;
                return false;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                string why;
                if (!ValidateCandidate(targets[i], i, out why))
                {
                    error = why;
                    return false;
                }
            }

            PMNetWriter writer = new PMNetWriter(256);
            WriteHeader(writer, PMProjectileWireKind.HitBatch, batch.Key);
            WriteVector3(writer, FHitPrevX, batch.PreviousPosition);
            WriteVector3(writer, FHitPosX, batch.HitPosition);
            writer.WriteTag(FHitRewindMs, PMWireType.Varint);
            writer.WriteVarint((ulong)batch.RewindMs);

            for (int i = 0; i < targets.Length; i++)
            {
                PMNetWriter sub = writer.RentSubWriter();
                WriteCandidate(sub, targets[i]);
                writer.WriteSubMessage(FHitCandidate, sub);
            }

            return Finish(writer, "上行命中批", out payload, out error);
        }

        private static void WriteCandidate(PMNetWriter sub, PMProjectileHitCandidate candidate)
        {
            sub.WriteTag(FCandTargetNetId, PMWireType.Varint);
            sub.WriteVarint(candidate.TargetNetId);
            sub.WriteTag(FCandStreamVersion, PMWireType.Varint);
            sub.WriteVarint(candidate.TargetStreamVersion);
            sub.WriteTag(FCandFrameDomain, PMWireType.Varint);
            sub.WriteVarint((ulong)(byte)candidate.TargetServerFrame.Domain);
            sub.WriteTag(FCandFrameValue, PMWireType.Varint);
            sub.WriteSInt64(candidate.TargetServerFrame.Value);
            WriteVector3(sub, FCandImpactX, candidate.ImpactPoint);
            WriteVector3(sub, FCandOffsetX, candidate.VisualOffset);
        }

        private static bool ValidateCandidate(PMProjectileHitCandidate candidate, int index, out string error)
        {
            string prefix = "第 " + index + " 个命中候选：";

            if (candidate.TargetNetId == 0u)
            {
                error = prefix + "TargetNetId 为 0";
                return false;
            }

            if (candidate.TargetStreamVersion == 0u)
            {
                error = prefix + "TargetStreamVersion 为 0（0 表示「未建立」，不是有效流代次）";
                return false;
            }

            if (!candidate.TargetServerFrame.IsValid
                || candidate.TargetServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                error = prefix + "TargetServerFrame 不是 AuthorityServer 域（收到 "
                        + candidate.TargetServerFrame + "）；target 帧锚与 Input 边界严格分域";
                return false;
            }

            if (candidate.TargetServerFrame.Value <= 0L)
            {
                error = prefix + "TargetServerFrame 必须为正（收到 " + candidate.TargetServerFrame.Value + "）";
                return false;
            }

            if (!candidate.ImpactPoint.IsFinite)
            {
                error = prefix + "ImpactPoint 出现 NaN/Infinity";
                return false;
            }

            if (!candidate.VisualOffset.IsFinite)
            {
                error = prefix + "VisualOffset 出现 NaN/Infinity";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>解码上行命中批。远端数据不可信 ⇒ 任何疑问都 false。</summary>
        public static bool TryDecodeHitBatch(byte[] payload, int offset, int count,
                                             out PMProjectileHitBatch batch, out string error)
        {
            batch = null;

            PMProjectileOpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMProjectileWireKind.HitBatch, true, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMProjectileHitCandidate[] candidates = new PMProjectileHitCandidate[MaxBatchTargets];
            int used = 0;
            uint seen = 0u;
            int lastField = FOrigin;
            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;
            int rewindMs = 0;
            PMVector3 previous = PMVector3.Zero;
            PMVector3 hit = PMVector3.Zero;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, FHitCandidate, FHitCandidate, out error))
                    {
                        return false;
                    }

                    if (field < FHitPrevX || field > FHitCandidate)
                    {
                        error = "上行命中批出现未知字段 " + field;
                        return false;
                    }

                    if (field == FHitCandidate)
                    {
                        if (wire != PMWireType.LengthDelimited)
                        {
                            error = "命中候选字段的 wire type 不是 length-delimited";
                            return false;
                        }

                        if (used >= MaxBatchTargets)
                        {
                            error = "命中候选数超过上限 " + MaxBatchTargets;
                            return false;
                        }

                        PMProjectileHitCandidate candidate;
                        if (!TryReadCandidate(reader.ReadSubReader(), out candidate, out error))
                        {
                            return false;
                        }

                        candidates[used] = candidate;
                        used++;
                        lastField = field;
                        continue;
                    }

                    seen |= 1u << (field - FHitPrevX);

                    switch (field)
                    {
                        case FHitPrevX:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionX 的 wire type 不对"; return false; }
                            previous.X = reader.ReadFloat();
                            break;
                        case FHitPrevY:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionY 的 wire type 不对"; return false; }
                            previous.Y = reader.ReadFloat();
                            break;
                        case FHitPrevZ:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionZ 的 wire type 不对"; return false; }
                            previous.Z = reader.ReadFloat();
                            break;
                        case FHitPosX:
                            if (wire != PMWireType.Fixed32) { error = "hitPositionX 的 wire type 不对"; return false; }
                            hit.X = reader.ReadFloat();
                            break;
                        case FHitPosY:
                            if (wire != PMWireType.Fixed32) { error = "hitPositionY 的 wire type 不对"; return false; }
                            hit.Y = reader.ReadFloat();
                            break;
                        case FHitPosZ:
                            if (wire != PMWireType.Fixed32) { error = "hitPositionZ 的 wire type 不对"; return false; }
                            hit.Z = reader.ReadFloat();
                            break;
                        case FHitRewindMs:
                            if (wire != PMWireType.Varint) { error = "rewindMs 的 wire type 不对"; return false; }
                            rewindMs = checked((int)reader.ReadVarint());
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "上行命中批解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "上行命中批解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "上行命中批解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & HitRequiredMask) != HitRequiredMask)
            {
                error = "上行命中批缺少必填字段（缺失位掩码 0x"
                        + (~seen & HitRequiredMask).ToString("X8") + "）";
                return false;
            }

            if (!previous.IsFinite || !hit.IsFinite)
            {
                error = "上行命中批的 PreviousPosition/HitPosition 出现 NaN/Infinity";
                return false;
            }

            if (rewindMs < 0)
            {
                error = "上行命中批的 RewindMs 为负（" + rewindMs + "）";
                return false;
            }

            PMProjectileHitCandidate[] exact;
            if (used == 0)
            {
                exact = NoCandidates;
            }
            else
            {
                exact = new PMProjectileHitCandidate[used];
                Array.Copy(candidates, exact, used);
            }

            PMProjectileHitBatch result = new PMProjectileHitBatch();
            result.Key = open.Key;
            result.PreviousPosition = previous;
            result.HitPosition = hit;
            result.RewindMs = rewindMs;
            result.Targets = exact;

            batch = result;
            error = null;
            return true;
        }

        /// <summary>解码便利重载（整段载荷）。</summary>
        public static bool TryDecodeHitBatch(byte[] payload, out PMProjectileHitBatch batch, out string error)
        {
            return TryDecodeHitBatch(payload, 0, payload == null ? 0 : payload.Length, out batch, out error);
        }

        private static bool TryReadCandidate(PMNetReader reader, out PMProjectileHitCandidate candidate, out string error)
        {
            candidate = default(PMProjectileHitCandidate);
            error = null;

            uint seen = 0u;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, NoRepeatable, NoRepeatable, out error))
                    {
                        return false;
                    }

                    if (field < FCandTargetNetId || field > FCandOffsetZ)
                    {
                        error = "命中候选出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << (field - FCandTargetNetId);

                    switch (field)
                    {
                        case FCandTargetNetId:
                            if (wire != PMWireType.Varint) { error = "candidate.targetNetId 的 wire type 不对"; return false; }
                            candidate.TargetNetId = checked((uint)reader.ReadVarint());
                            break;
                        case FCandStreamVersion:
                            if (wire != PMWireType.Varint) { error = "candidate.targetStreamVersion 的 wire type 不对"; return false; }
                            candidate.TargetStreamVersion = checked((uint)reader.ReadVarint());
                            break;
                        case FCandFrameDomain:
                            if (wire != PMWireType.Varint) { error = "candidate.targetFrameDomain 的 wire type 不对"; return false; }
                            candidate.TargetServerFrame = new PMFrameId(
                                (PMFrameDomain)checked((byte)reader.ReadVarint()), candidate.TargetServerFrame.Value);
                            break;
                        case FCandFrameValue:
                            if (wire != PMWireType.Varint) { error = "candidate.targetFrameValue 的 wire type 不对"; return false; }
                            candidate.TargetServerFrame = new PMFrameId(
                                candidate.TargetServerFrame.Domain, reader.ReadSInt64());
                            break;
                        case FCandImpactX:
                            if (wire != PMWireType.Fixed32) { error = "candidate.impactPointX 的 wire type 不对"; return false; }
                            candidate.ImpactPoint.X = reader.ReadFloat();
                            break;
                        case FCandImpactY:
                            if (wire != PMWireType.Fixed32) { error = "candidate.impactPointY 的 wire type 不对"; return false; }
                            candidate.ImpactPoint.Y = reader.ReadFloat();
                            break;
                        case FCandImpactZ:
                            if (wire != PMWireType.Fixed32) { error = "candidate.impactPointZ 的 wire type 不对"; return false; }
                            candidate.ImpactPoint.Z = reader.ReadFloat();
                            break;
                        case FCandOffsetX:
                            if (wire != PMWireType.Fixed32) { error = "candidate.visualOffsetX 的 wire type 不对"; return false; }
                            candidate.VisualOffset.X = reader.ReadFloat();
                            break;
                        case FCandOffsetY:
                            if (wire != PMWireType.Fixed32) { error = "candidate.visualOffsetY 的 wire type 不对"; return false; }
                            candidate.VisualOffset.Y = reader.ReadFloat();
                            break;
                        case FCandOffsetZ:
                            if (wire != PMWireType.Fixed32) { error = "candidate.visualOffsetZ 的 wire type 不对"; return false; }
                            candidate.VisualOffset.Z = reader.ReadFloat();
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "命中候选解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "命中候选解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "命中候选解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & CandidateRequiredMask) != CandidateRequiredMask)
            {
                error = "命中候选缺少必填字段（缺失位掩码 0x"
                        + (~seen & CandidateRequiredMask).ToString("X8") + "）";
                return false;
            }

            string why;
            if (!ValidateCandidate(candidate, 0, out why))
            {
                error = why;
                return false;
            }

            return true;
        }

        // ================================================================ 编码：下行 Snapshot（State + Spec）

        /// <summary>编码下行权威快照。任何非法输入 ⇒ false + error。</summary>
        public static bool TryEncodeSnapshot(PMProjectileSnapshot snapshot, out byte[] payload, out string error)
        {
            payload = null;

            if (snapshot == null)
            {
                error = "快照为 null";
                return false;
            }

            if (snapshot.State == null)
            {
                error = "快照缺少 State";
                return false;
            }

            if (snapshot.Spec == null)
            {
                error = "快照缺少 Spec（权威配置必须显式给出，不允许静默默认值）";
                return false;
            }

            PMProjectileState state = snapshot.State;
            if (!IsValidKey(state.Key))
            {
                error = "快照的 Key 非法：" + FormatKey(state.Key);
                return false;
            }

            if (state.AuthorityNetId == 0u)
            {
                error = "快照的 AuthorityNetId 必须非 0（没有权威身份的「快照」是伪快照）";
                return false;
            }

            string why;
            if (!ValidateState(state, out why))
            {
                error = "快照 State 非法：" + why;
                return false;
            }

            if (!ValidateSpec(snapshot.Spec, out why))
            {
                error = "快照 Spec 非法：" + why;
                return false;
            }

            PMNetWriter writer = new PMNetWriter(320);
            WriteHeader(writer, PMProjectileWireKind.Snapshot, state.Key);

            writer.WriteTag(FSnapAuthority, PMWireType.Varint);
            writer.WriteVarint(state.AuthorityNetId);
            writer.WriteTag(FSnapActivation, PMWireType.Varint);
            writer.WriteVarint(state.ActivationId);
            WriteVector3(writer, FSnapSpawnX, state.SpawnPosition);
            WriteVector3(writer, FSnapPrevX, state.PreviousPosition);
            WriteVector3(writer, FSnapPosX, state.Position);
            WriteVector3(writer, FSnapVelX, state.Velocity);
            writer.WriteTag(FSnapYaw, PMWireType.Fixed32);
            writer.WriteFloat(state.Yaw);
            writer.WriteTag(FSnapMoveTimeMs, PMWireType.Fixed64);
            writer.WriteDouble(state.MoveTimeMs);
            writer.WriteTag(FSnapStopped, PMWireType.Varint);
            writer.WriteBool(state.Stopped);
            writer.WriteTag(FSnapHidden, PMWireType.Varint);
            writer.WriteBool(state.Hidden);
            writer.WriteTag(FSnapTakenOver, PMWireType.Varint);
            writer.WriteBool(state.TakenOver);
            writer.WriteTag(FSnapStopWallMs, PMWireType.Fixed64);
            writer.WriteDouble(state.StopWallTimeMs);
            writer.WriteTag(FSnapTimeAfterStopMs, PMWireType.Fixed64);
            writer.WriteDouble(state.TimeAfterStoppedMs);
            writer.WriteTag(FSnapTombstoneUntilMs, PMWireType.Fixed64);
            writer.WriteDouble(state.TombstoneUntilMs);

            uint[] hits = state.HitTargets == null ? NoUInts : state.HitTargets;
            for (int i = 0; i < hits.Length; i++)
            {
                writer.WriteTag(FSnapHitTarget, PMWireType.Varint);
                writer.WriteVarint(hits[i]);
            }

            uint[] allowed = state.AllowedTargets == null ? NoUInts : state.AllowedTargets;
            for (int i = 0; i < allowed.Length; i++)
            {
                writer.WriteTag(FSnapAllowedTarget, PMWireType.Varint);
                writer.WriteVarint(allowed[i]);
            }

            PMNetWriter specWriter = writer.RentSubWriter();
            WriteSpec(specWriter, snapshot.Spec);
            writer.WriteSubMessage(FSnapSpec, specWriter);

            return Finish(writer, "下行权威快照", out payload, out error);
        }

        private static bool ValidateState(PMProjectileState state, out string error)
        {
            if (!state.SpawnPosition.IsFinite || !state.PreviousPosition.IsFinite
                || !state.Position.IsFinite || !state.Velocity.IsFinite)
            {
                return Fail("SpawnPosition/PreviousPosition/Position/Velocity 出现 NaN/Infinity", out error);
            }

            if (!IsCanonicalYaw(state.Yaw))
            {
                return Fail("Yaw 不是规范值 [0,360)（收到 " + state.Yaw + "）", out error);
            }

            if (!IsFinite(state.MoveTimeMs) || state.MoveTimeMs < 0.0)
            {
                return Fail("MoveTimeMs 非有限或为负（" + state.MoveTimeMs + "）", out error);
            }

            if (!IsFinite(state.StopWallTimeMs) || state.StopWallTimeMs < 0.0)
            {
                return Fail("StopWallTimeMs 非有限或为负（" + state.StopWallTimeMs + "）", out error);
            }

            if (!IsFinite(state.TimeAfterStoppedMs) || state.TimeAfterStoppedMs < 0.0)
            {
                return Fail("TimeAfterStoppedMs 非有限或为负（" + state.TimeAfterStoppedMs + "）", out error);
            }

            if (!IsFinite(state.TombstoneUntilMs) || state.TombstoneUntilMs < 0.0)
            {
                return Fail("TombstoneUntilMs 非有限或为负（" + state.TombstoneUntilMs + "）", out error);
            }

            string why;
            if (!ValidateTargetList(state.HitTargets, "HitTargets", out why))
            {
                return Fail(why, out error);
            }

            if (!ValidateTargetList(state.AllowedTargets, "AllowedTargets", out why))
            {
                return Fail(why, out error);
            }

            error = null;
            return true;
        }

        private static bool ValidateTargetList(uint[] targets, string name, out string error)
        {
            if (targets == null)
            {
                error = null;
                return true;
            }

            if (targets.Length > MaxSnapshotTargets)
            {
                return Fail(name + " 长度 " + targets.Length + " 超过上限 " + MaxSnapshotTargets, out error);
            }

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == 0u)
                {
                    return Fail(name + " 第 " + i + " 项为 0（NetId 0 不是有效目标）", out error);
                }
            }

            error = null;
            return true;
        }

        private static bool ValidateSpec(PMProjectileSpec spec, out string error)
        {
            if (!IsFinite(spec.SpeedMps) || spec.SpeedMps < 0f)
            {
                return Fail("SpeedMps 非有限或为负（" + spec.SpeedMps + "）", out error);
            }

            if (!IsFinite(spec.RadiusM) || spec.RadiusM < 0f)
            {
                return Fail("RadiusM 非有限或为负（" + spec.RadiusM + "）", out error);
            }

            if (spec.LifetimeMs < 0)
            {
                return Fail("LifetimeMs 为负（" + spec.LifetimeMs + "）", out error);
            }

            if (spec.DelayDestroyMs < 0)
            {
                return Fail("DelayDestroyMs 为负（" + spec.DelayDestroyMs + "）", out error);
            }

            error = null;
            return true;
        }

        /// <summary>解码下行权威快照。远端数据不可信 ⇒ 任何疑问都 false。</summary>
        public static bool TryDecodeSnapshot(byte[] payload, int offset, int count,
                                             out PMProjectileSnapshot snapshot, out string error)
        {
            snapshot = null;

            PMProjectileOpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMProjectileWireKind.Snapshot, false, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMProjectileState state = new PMProjectileState();
            state.Key = open.Key;
            PMProjectileSpec spec = null;

            uint[] hitBuffer = new uint[MaxSnapshotTargets];
            uint[] allowedBuffer = new uint[MaxSnapshotTargets];
            int hitUsed = 0;
            int allowedUsed = 0;

            uint seen = 0u;
            int lastField = FOrigin;
            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, FSnapHitTarget, FSnapAllowedTarget, out error))
                    {
                        return false;
                    }

                    if (field < FSnapAuthority || field > FSnapSpec)
                    {
                        error = "下行快照出现未知字段 " + field;
                        return false;
                    }

                    if (field == FSnapHitTarget || field == FSnapAllowedTarget)
                    {
                        if (wire != PMWireType.Varint)
                        {
                            error = "目标数组元素的 wire type 不是 varint";
                            return false;
                        }

                        bool isHit = field == FSnapHitTarget;
                        int used = isHit ? hitUsed : allowedUsed;
                        if (used >= MaxSnapshotTargets)
                        {
                            error = (isHit ? "HitTargets" : "AllowedTargets") + " 长度超过上限 " + MaxSnapshotTargets;
                            return false;
                        }

                        uint target = checked((uint)reader.ReadVarint());
                        if (isHit)
                        {
                            hitBuffer[hitUsed] = target;
                            hitUsed++;
                        }
                        else
                        {
                            allowedBuffer[allowedUsed] = target;
                            allowedUsed++;
                        }

                        // 数组字段同样参与「字段号不得倒退」的判定（否则 32 → 31 的回退会被漏掉）。
                        lastField = field;
                        continue;
                    }

                    seen |= 1u << (field - FSnapAuthority);

                    switch (field)
                    {
                        case FSnapAuthority:
                            if (wire != PMWireType.Varint) { error = "authorityNetId 的 wire type 不对"; return false; }
                            state.AuthorityNetId = checked((uint)reader.ReadVarint());
                            break;
                        case FSnapActivation:
                            if (wire != PMWireType.Varint) { error = "activationId 的 wire type 不对"; return false; }
                            state.ActivationId = checked((uint)reader.ReadVarint());
                            break;
                        case FSnapSpawnX:
                            if (wire != PMWireType.Fixed32) { error = "spawnPositionX 的 wire type 不对"; return false; }
                            state.SpawnPosition.X = reader.ReadFloat();
                            break;
                        case FSnapSpawnY:
                            if (wire != PMWireType.Fixed32) { error = "spawnPositionY 的 wire type 不对"; return false; }
                            state.SpawnPosition.Y = reader.ReadFloat();
                            break;
                        case FSnapSpawnZ:
                            if (wire != PMWireType.Fixed32) { error = "spawnPositionZ 的 wire type 不对"; return false; }
                            state.SpawnPosition.Z = reader.ReadFloat();
                            break;
                        case FSnapPrevX:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionX 的 wire type 不对"; return false; }
                            state.PreviousPosition.X = reader.ReadFloat();
                            break;
                        case FSnapPrevY:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionY 的 wire type 不对"; return false; }
                            state.PreviousPosition.Y = reader.ReadFloat();
                            break;
                        case FSnapPrevZ:
                            if (wire != PMWireType.Fixed32) { error = "previousPositionZ 的 wire type 不对"; return false; }
                            state.PreviousPosition.Z = reader.ReadFloat();
                            break;
                        case FSnapPosX:
                            if (wire != PMWireType.Fixed32) { error = "positionX 的 wire type 不对"; return false; }
                            state.Position.X = reader.ReadFloat();
                            break;
                        case FSnapPosY:
                            if (wire != PMWireType.Fixed32) { error = "positionY 的 wire type 不对"; return false; }
                            state.Position.Y = reader.ReadFloat();
                            break;
                        case FSnapPosZ:
                            if (wire != PMWireType.Fixed32) { error = "positionZ 的 wire type 不对"; return false; }
                            state.Position.Z = reader.ReadFloat();
                            break;
                        case FSnapVelX:
                            if (wire != PMWireType.Fixed32) { error = "velocityX 的 wire type 不对"; return false; }
                            state.Velocity.X = reader.ReadFloat();
                            break;
                        case FSnapVelY:
                            if (wire != PMWireType.Fixed32) { error = "velocityY 的 wire type 不对"; return false; }
                            state.Velocity.Y = reader.ReadFloat();
                            break;
                        case FSnapVelZ:
                            if (wire != PMWireType.Fixed32) { error = "velocityZ 的 wire type 不对"; return false; }
                            state.Velocity.Z = reader.ReadFloat();
                            break;
                        case FSnapYaw:
                            if (wire != PMWireType.Fixed32) { error = "yaw 的 wire type 不对"; return false; }
                            state.Yaw = reader.ReadFloat();
                            break;
                        case FSnapMoveTimeMs:
                            if (wire != PMWireType.Fixed64) { error = "moveTimeMs 的 wire type 不对"; return false; }
                            state.MoveTimeMs = reader.ReadDouble();
                            break;
                        case FSnapStopped:
                            if (wire != PMWireType.Varint) { error = "stopped 的 wire type 不对"; return false; }
                            state.Stopped = reader.ReadBool();
                            break;
                        case FSnapHidden:
                            if (wire != PMWireType.Varint) { error = "hidden 的 wire type 不对"; return false; }
                            state.Hidden = reader.ReadBool();
                            break;
                        case FSnapTakenOver:
                            if (wire != PMWireType.Varint) { error = "takenOver 的 wire type 不对"; return false; }
                            state.TakenOver = reader.ReadBool();
                            break;
                        case FSnapStopWallMs:
                            if (wire != PMWireType.Fixed64) { error = "stopWallTimeMs 的 wire type 不对"; return false; }
                            state.StopWallTimeMs = reader.ReadDouble();
                            break;
                        case FSnapTimeAfterStopMs:
                            if (wire != PMWireType.Fixed64) { error = "timeAfterStoppedMs 的 wire type 不对"; return false; }
                            state.TimeAfterStoppedMs = reader.ReadDouble();
                            break;
                        case FSnapTombstoneUntilMs:
                            if (wire != PMWireType.Fixed64) { error = "tombstoneUntilMs 的 wire type 不对"; return false; }
                            state.TombstoneUntilMs = reader.ReadDouble();
                            break;
                        case FSnapSpec:
                            if (wire != PMWireType.LengthDelimited) { error = "spec 的 wire type 不对"; return false; }
                            if (!TryReadSpec(reader.ReadSubReader(), out spec, out error)) { return false; }
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "下行快照解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "下行快照解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "下行快照解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & SnapshotRequiredMask) != SnapshotRequiredMask)
            {
                error = "下行快照缺少必填字段（缺失位掩码 0x"
                        + (~seen & SnapshotRequiredMask).ToString("X8") + "）";
                return false;
            }

            if (state.AuthorityNetId == 0u)
            {
                error = "下行快照的 AuthorityNetId 为 0（伪快照）";
                return false;
            }

            state.HitTargets = hitUsed == 0 ? NoUInts : CopyUInts(hitBuffer, hitUsed);
            state.AllowedTargets = allowedUsed == 0 ? NoUInts : CopyUInts(allowedBuffer, allowedUsed);

            string why;
            if (!ValidateState(state, out why))
            {
                error = "下行快照 State 非法：" + why;
                return false;
            }

            if (!ValidateSpec(spec, out why))
            {
                error = "下行快照 Spec 非法：" + why;
                return false;
            }

            PMProjectileSnapshot result = new PMProjectileSnapshot();
            result.State = state;
            result.Spec = spec;

            snapshot = result;
            error = null;
            return true;
        }

        /// <summary>解码便利重载（整段载荷）。</summary>
        public static bool TryDecodeSnapshot(byte[] payload, out PMProjectileSnapshot snapshot, out string error)
        {
            return TryDecodeSnapshot(payload, 0, payload == null ? 0 : payload.Length, out snapshot, out error);
        }

        private static uint[] CopyUInts(uint[] source, int count)
        {
            uint[] copy = new uint[count];
            Array.Copy(source, copy, count);
            return copy;
        }

        private static void WriteSpec(PMNetWriter writer, PMProjectileSpec spec)
        {
            writer.WriteTag(FSpecSpeed, PMWireType.Fixed32);
            writer.WriteFloat(spec.SpeedMps);
            writer.WriteTag(FSpecRadius, PMWireType.Fixed32);
            writer.WriteFloat(spec.RadiusM);
            writer.WriteTag(FSpecLifetime, PMWireType.Varint);
            writer.WriteVarint((ulong)spec.LifetimeMs);
            writer.WriteTag(FSpecDelayDestroy, PMWireType.Varint);
            writer.WriteVarint((ulong)spec.DelayDestroyMs);
            writer.WriteTag(FSpecStopOnHit, PMWireType.Varint);
            writer.WriteBool(spec.StopOnHit);
            writer.WriteTag(FSpecHideOnStop, PMWireType.Varint);
            writer.WriteBool(spec.HideOnStop);
            writer.WriteTag(FSpecSkipTrajectory, PMWireType.Varint);
            writer.WriteBool(spec.SkipFlyingTrajectoryValidation);
        }

        private static bool TryReadSpec(PMNetReader reader, out PMProjectileSpec spec, out string error)
        {
            spec = null;
            error = null;

            PMProjectileSpec result = new PMProjectileSpec();
            uint seen = 0u;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, NoRepeatable, NoRepeatable, out error))
                    {
                        return false;
                    }

                    if (field < FSpecSpeed || field > FSpecSkipTrajectory)
                    {
                        error = "spec 出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << (field - FSpecSpeed);

                    switch (field)
                    {
                        case FSpecSpeed:
                            if (wire != PMWireType.Fixed32) { error = "spec.speedMps 的 wire type 不对"; return false; }
                            result.SpeedMps = reader.ReadFloat();
                            break;
                        case FSpecRadius:
                            if (wire != PMWireType.Fixed32) { error = "spec.radiusM 的 wire type 不对"; return false; }
                            result.RadiusM = reader.ReadFloat();
                            break;
                        case FSpecLifetime:
                            if (wire != PMWireType.Varint) { error = "spec.lifetimeMs 的 wire type 不对"; return false; }
                            result.LifetimeMs = checked((int)reader.ReadVarint());
                            break;
                        case FSpecDelayDestroy:
                            if (wire != PMWireType.Varint) { error = "spec.delayDestroyMs 的 wire type 不对"; return false; }
                            result.DelayDestroyMs = checked((int)reader.ReadVarint());
                            break;
                        case FSpecStopOnHit:
                            if (wire != PMWireType.Varint) { error = "spec.stopOnHit 的 wire type 不对"; return false; }
                            result.StopOnHit = reader.ReadBool();
                            break;
                        case FSpecHideOnStop:
                            if (wire != PMWireType.Varint) { error = "spec.hideOnStop 的 wire type 不对"; return false; }
                            result.HideOnStop = reader.ReadBool();
                            break;
                        case FSpecSkipTrajectory:
                            if (wire != PMWireType.Varint) { error = "spec.skipFlyingTrajectoryValidation 的 wire type 不对"; return false; }
                            result.SkipFlyingTrajectoryValidation = reader.ReadBool();
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "spec 解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "spec 解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "spec 解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & SpecRequiredMask) != SpecRequiredMask)
            {
                error = "spec 缺少必填字段（缺失位掩码 0x"
                        + (~seen & SpecRequiredMask).ToString("X8") + "）";
                return false;
            }

            string why;
            if (!ValidateSpec(result, out why))
            {
                error = "spec 非法：" + why;
                return false;
            }

            spec = result;
            return true;
        }

        // ================================================================ 编码：下行 Decision

        /// <summary>编码下行激活裁决。Pending 是伪终态，编码侧同样拒绝。</summary>
        public static bool TryEncodeDecision(PMProjectileDecision decision, out byte[] payload, out string error)
        {
            payload = null;

            if (decision == null)
            {
                error = "裁决为 null";
                return false;
            }

            if (!IsValidKey(decision.Key))
            {
                error = "裁决的 Key 非法：" + FormatKey(decision.Key);
                return false;
            }

            if (!IsActivationIdAllowed(decision.ActivationId, decision.Key.Origin))
            {
                error = "裁决的 ActivationId 非法：0 只允许 origin=ServerDirect（可信权威弹），"
                        + "ClientPredicted 必须非 0（收到 activationId=" + decision.ActivationId
                        + ", origin=" + decision.Key.Origin + "）";
                return false;
            }

            if (!IsTerminalResult(decision.Result))
            {
                error = "裁决的 Result 必须是 Confirmed/Rejected（Pending 是伪终态；收到 " + decision.Result + "）";
                return false;
            }

            string reason = decision.Reason == null ? string.Empty : decision.Reason;
            int reasonBytes = Encoding.UTF8.GetByteCount(reason);
            if (reasonBytes > MaxReasonBytes)
            {
                error = "裁决的 Reason 为 " + reasonBytes + " 字节，超过上限 " + MaxReasonBytes + " 字节";
                return false;
            }

            PMNetWriter writer = new PMNetWriter(96);
            WriteHeader(writer, PMProjectileWireKind.Decision, decision.Key);
            writer.WriteTag(FDecActivation, PMWireType.Varint);
            writer.WriteVarint(decision.ActivationId);
            writer.WriteTag(FDecResult, PMWireType.Varint);
            writer.WriteVarint((ulong)(byte)decision.Result);
            writer.WriteTag(FDecReason, PMWireType.LengthDelimited);
            writer.WriteStringValue(reason);

            return Finish(writer, "下行激活裁决", out payload, out error);
        }

        /// <summary>解码下行激活裁决。Pending 与未知 result 一律拒绝。</summary>
        public static bool TryDecodeDecision(byte[] payload, int offset, int count,
                                             out PMProjectileDecision decision, out string error)
        {
            decision = null;

            PMProjectileOpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMProjectileWireKind.Decision, false, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMProjectileDecision result = new PMProjectileDecision();
            result.Key = open.Key;

            uint seen = 0u;
            int lastField = FOrigin;
            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, NoRepeatable, NoRepeatable, out error))
                    {
                        return false;
                    }

                    if (field < FDecActivation || field > FDecReason)
                    {
                        error = "下行裁决出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << (field - FDecActivation);

                    switch (field)
                    {
                        case FDecActivation:
                            if (wire != PMWireType.Varint) { error = "activationId 的 wire type 不对"; return false; }
                            result.ActivationId = checked((uint)reader.ReadVarint());
                            break;
                        case FDecResult:
                            if (wire != PMWireType.Varint) { error = "result 的 wire type 不对"; return false; }
                            int raw = checked((int)reader.ReadVarint());
                            if (raw < (int)PMActivationResult.Pending || raw > (int)PMActivationResult.Rejected)
                            {
                                error = "裁决 result 取值未知：" + raw;
                                return false;
                            }

                            result.Result = (PMActivationResult)(byte)raw;
                            break;
                        case FDecReason:
                            if (wire != PMWireType.LengthDelimited) { error = "reason 的 wire type 不对"; return false; }
                            string reason;
                            if (!TryReadBoundedString(reader, MaxReasonBytes, "reason", out reason, out error))
                            {
                                return false;
                            }

                            result.Reason = reason;
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "下行裁决解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "下行裁决解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "下行裁决解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & DecisionRequiredMask) != DecisionRequiredMask)
            {
                error = "下行裁决缺少必填字段（缺失位掩码 0x"
                        + (~seen & DecisionRequiredMask).ToString("X8") + "）";
                return false;
            }

            if (!IsActivationIdAllowed(result.ActivationId, result.Key.Origin))
            {
                error = "下行裁决的 ActivationId 非法：0 只允许 origin=ServerDirect（可信权威弹），"
                        + "ClientPredicted 必须非 0（收到 activationId=" + result.ActivationId
                        + ", origin=" + result.Key.Origin + "）";
                return false;
            }

            if (!IsTerminalResult(result.Result))
            {
                error = "下行裁决的 Result 不是终态（Pending 是伪终态）；收到 " + result.Result;
                return false;
            }

            decision = result;
            error = null;
            return true;
        }

        /// <summary>解码便利重载（整段载荷）。</summary>
        public static bool TryDecodeDecision(byte[] payload, out PMProjectileDecision decision, out string error)
        {
            return TryDecodeDecision(payload, 0, payload == null ? 0 : payload.Length, out decision, out error);
        }

        /// <summary>终态判定：只有 Confirmed / Rejected 是终态（Pending 不是）。</summary>
        public static bool IsTerminalResult(PMActivationResult result)
        {
            return result == PMActivationResult.Confirmed || result == PMActivationResult.Rejected;
        }

        /// <summary>
        /// 激活 ID 的合法性**取决于 origin**（契约「激活ID=0仅可信ServerDirect可用」）：
        ///   · ClientPredicted —— 必须非 0（0 不是有效激活账本项，客户端也不得自报 0）；
        ///   · ServerDirect  —— 允许 0（权威弹按构造 Confirmed，不需要客户端激活账本项，
        ///     但若需要下发终态裁决，activationId=0 必须是**可表达**的，否则 DS 的合法终态
        ///     会被本层当成畸形 wire 拒掉）。
        /// 只用于**下行裁决**；上行 spawn 恒为 ClientPredicted，因此仍要求非 0。
        /// </summary>
        private static bool IsActivationIdAllowed(uint activationId, PMProjectileOrigin origin)
        {
            return activationId != 0u || origin == PMProjectileOrigin.ServerDirect;
        }

        // ================================================================ 头部与公用

        /// <summary>已打开的载荷：头部身份 + 头部之后的第一个字段。</summary>
        private sealed class PMProjectileOpenPayload
        {
            public PMNetReader Reader;
            public uint Epoch;
            public uint OwnerNetId;
            public uint ProjectileId;
            public PMProjectileOrigin Origin;

            /// <summary>头部之后已经读到的第一个字段（HasField=false 表示头部之后恰好结束）。</summary>
            public int Field;
            public PMWireType Wire;
            public bool HasField;

            public PMProjectileKey Key
            {
                get { return new PMProjectileKey(Epoch, OwnerNetId, ProjectileId, Origin); }
            }
        }

        private static void WriteHeader(PMNetWriter writer, PMProjectileWireKind kind, PMProjectileKey key)
        {
            writer.WriteTag(FKind, PMWireType.Varint);
            writer.WriteVarint((ulong)(byte)kind);
            writer.WriteTag(FVersion, PMWireType.Varint);
            writer.WriteVarint((ulong)ProtocolVersion);
            writer.WriteTag(FEpoch, PMWireType.Varint);
            writer.WriteVarint(key.Epoch);
            writer.WriteTag(FOwner, PMWireType.Varint);
            writer.WriteVarint(key.OwnerNetId);
            writer.WriteTag(FProjectile, PMWireType.Varint);
            writer.WriteVarint(key.ProjectileId);
            writer.WriteTag(FOrigin, PMWireType.Varint);
            writer.WriteVarint((ulong)(byte)key.Origin);
        }

        /// <summary>
        /// 打开载荷：长度上限 → kind（必须等于期望值）→ version → 身份四件套取头部 → 偷看首个正文字段。
        /// 头部字段号固定为 1..6，因此缺字段/重复/乱序/未知 kind 在这里就被拒绝。
        /// </summary>
        private static bool TryOpenPayload(byte[] payload, int offset, int count,
                                           PMProjectileWireKind expectedKind, bool requireClientPredicted,
                                           out PMProjectileOpenPayload open, out string error)
        {
            open = null;
            error = null;

            if (payload == null)
            {
                error = "载荷为 null";
                return false;
            }

            if (offset < 0 || count < 0 || offset + count > payload.Length)
            {
                error = "载荷区间越界（offset=" + offset + " count=" + count + " len=" + payload.Length + "）";
                return false;
            }

            if (count == 0)
            {
                error = "载荷为空";
                return false;
            }

            if (count > MaxPayloadBytes)
            {
                error = "载荷 " + count + " 字节超过上限 " + MaxPayloadBytes + " 字节";
                return false;
            }

            PMProjectileOpenPayload result = new PMProjectileOpenPayload();
            result.Reader = new PMNetReader(payload, offset, count);

            int field;
            PMWireType wire;

            try
            {
                if (!result.Reader.ReadTag(out field, out wire) || field != FKind)
                {
                    error = "载荷头部缺少 kind 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "kind 的 wire type 不对"; return false; }
                int kindValue = checked((int)result.Reader.ReadVarint());
                if (kindValue < (int)PMProjectileWireKind.SpawnIntent || kindValue > (int)PMProjectileWireKind.Decision)
                {
                    error = "未知载荷种类 " + kindValue;
                    return false;
                }

                PMProjectileWireKind kind = (PMProjectileWireKind)(byte)kindValue;
                if (kind != expectedKind)
                {
                    error = "载荷种类不符：期望 " + expectedKind + "，实际 " + kind + "（跨通道错用被拒绝）";
                    return false;
                }

                if (!result.Reader.ReadTag(out field, out wire) || field != FVersion)
                {
                    error = "载荷头部缺少 version 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "version 的 wire type 不对"; return false; }
                int version = checked((int)result.Reader.ReadVarint());
                if (version != ProtocolVersion)
                {
                    error = "编码版本不兼容：收到 " + version + "，本端只支持 " + ProtocolVersion;
                    return false;
                }

                if (!result.Reader.ReadTag(out field, out wire) || field != FEpoch)
                {
                    error = "载荷头部缺少 epoch 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "epoch 的 wire type 不对"; return false; }
                result.Epoch = checked((uint)result.Reader.ReadVarint());

                if (!result.Reader.ReadTag(out field, out wire) || field != FOwner)
                {
                    error = "载荷头部缺少 ownerNetId 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "ownerNetId 的 wire type 不对"; return false; }
                result.OwnerNetId = checked((uint)result.Reader.ReadVarint());

                if (!result.Reader.ReadTag(out field, out wire) || field != FProjectile)
                {
                    error = "载荷头部缺少 projectileId 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "projectileId 的 wire type 不对"; return false; }
                result.ProjectileId = checked((uint)result.Reader.ReadVarint());

                if (!result.Reader.ReadTag(out field, out wire) || field != FOrigin)
                {
                    error = "载荷头部缺少 origin 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "origin 的 wire type 不对"; return false; }
                int originValue = checked((int)result.Reader.ReadVarint());
                if (originValue != (int)PMProjectileOrigin.ClientPredicted
                    && originValue != (int)PMProjectileOrigin.ServerDirect)
                {
                    error = "未知 origin 取值 " + originValue;
                    return false;
                }

                result.Origin = (PMProjectileOrigin)(byte)originValue;

                if (!result.Key.IsValid)
                {
                    error = "载荷身份非法（Epoch/OwnerNetId/ProjectileId 必须非 0）：" + FormatKey(result.Key);
                    return false;
                }

                if (requireClientPredicted && result.Origin != PMProjectileOrigin.ClientPredicted)
                {
                    error = "上行载荷的 origin 只能是 ClientPredicted（收到 " + result.Origin
                            + "）；客户端不得自称 ServerDirect";
                    return false;
                }

                if (result.Reader.ReadTag(out field, out wire))
                {
                    result.Field = field;
                    result.Wire = wire;
                    result.HasField = true;
                }
            }
            catch (FormatException ex)
            {
                error = "载荷头部解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "载荷头部解码溢出：" + ex.Message;
                return false;
            }

            open = result;
            return true;
        }

        /// <summary>
        /// 字段顺序校验：字段号**不得倒退**，且只有 [repeatableFrom, repeatableTo] 这个闭区间内的字段号
        /// 允许连续重复（数组承载字段）。传 <see cref="NoRepeatable"/> 表示该 message 不允许任何重复字段。
        /// </summary>
        private static bool IsFieldOrderAccepted(int lastField, int field, int repeatableFrom, int repeatableTo,
                                                 out string error)
        {
            error = null;

            if (field < lastField)
            {
                error = "字段号不得倒退（上一个 " + lastField + "，本次 " + field + "）";
                return false;
            }

            if (field == lastField && (field < repeatableFrom || field > repeatableTo))
            {
                error = "字段号重复（" + field + " 出现两次；该字段不允许重复）";
                return false;
            }

            return true;
        }

        private static void WriteVector3(PMNetWriter writer, int firstField, PMVector3 value)
        {
            writer.WriteTag(firstField, PMWireType.Fixed32);
            writer.WriteFloat(value.X);
            writer.WriteTag(firstField + 1, PMWireType.Fixed32);
            writer.WriteFloat(value.Y);
            writer.WriteTag(firstField + 2, PMWireType.Fixed32);
            writer.WriteFloat(value.Z);
        }

        /// <summary>
        /// 读有界字符串：**先偷看长度前缀并判上限，再分配**。
        /// 没有这一步，一个伪造的巨长前缀就会让对端按声明长度去分配（契约「读端分配前长度上限」）。
        /// </summary>
        private static bool TryReadBoundedString(PMNetReader reader, int maxBytes, string name,
                                                 out string value, out string error)
        {
            value = null;

            int declared;
            try
            {
                declared = reader.PeekVarintLength();
            }
            catch (FormatException ex)
            {
                return Fail(name + " 长度前缀解码失败：" + ex.Message, out error);
            }
            catch (OverflowException ex)
            {
                return Fail(name + " 长度前缀溢出：" + ex.Message, out error);
            }

            if (declared < 0)
            {
                return Fail(name + " 长度前缀为负（" + declared + "）", out error);
            }

            if (declared > maxBytes)
            {
                return Fail(name + " 声明长度 " + declared + " 字节超过上限 " + maxBytes + " 字节（分配前拒绝）",
                    out error);
            }

            try
            {
                value = reader.ReadStringValue();
            }
            catch (FormatException ex)
            {
                return Fail(name + " 解码失败：" + ex.Message, out error);
            }

            error = null;
            return true;
        }

        private static bool Finish(PMNetWriter writer, string what, out byte[] payload, out string error)
        {
            payload = null;
            if (writer.Length > MaxPayloadBytes)
            {
                error = what + " 编码后 " + writer.Length + " 字节超过 codec 上限 " + MaxPayloadBytes + " 字节";
                return false;
            }

            payload = writer.ToArray();
            error = null;
            return true;
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        /// <summary>
        /// 诊断用的 Key 文本。共享契约里的 <see cref="PMProjectileKey"/> 没有重写 ToString
        /// （本批不得修改契约文件），所以这里自己格式化，避免错误信息退化成类型名。
        /// </summary>
        private static string FormatKey(PMProjectileKey key)
        {
            return "(epoch=" + key.Epoch + ",owner=" + key.OwnerNetId + ",projectile=" + key.ProjectileId
                   + ",origin=" + key.Origin + ")";
        }
    }
}
