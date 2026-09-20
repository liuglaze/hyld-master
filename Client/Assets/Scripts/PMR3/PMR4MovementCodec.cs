// R4-B / B1：运动网络的**专用载荷编解码**（契约 Docs/plans/net-r4-network-contract.md §B1）。
//
// 为什么要有这一层（而不是直接用生成桩的参数）：
//   声明出来的承载是 `byte[] payload`。契约要求「blob 内部由专门框架 codec 使用
//   PMNetReader/PMNetWriter 的 protobuf 原语编码」「编码头 Version=1」「各类有更紧的字段/数量上限」
//   「禁止重新发明 UDP/MainPack/业务 state_mask」。
//   因此这里只做三件事：**固定字段号 + 严格拒绝 + 有界**。
//
// 六个不变量（每一条都有对应的测试用例，见 Tools/PMR4NetworkTest）：
//   1) 每种载荷带 kind 头（Inputs/Snapshot/Events/Resync），**跨通道喂错载荷一律拒绝**；
//   2) version 必须等于 ProtocolVersion，不兼容即拒绝；
//   3) 身份三件套（epoch / instanceId / streamVersion）非 0 且必须匹配；streamVersion 是
//      「本对象输入流重置代次」，**不得**拿来冒充对象身份或会话 Epoch；
//   4) 所有 float 必须有限（NaN/±Inf 一律拒绝）；Mode/Kind 必须是已知取值（按整型判定，
//      不做 8 位截断，避免 0x105 被静默当成 Walking）；
//   5) 数组/条数全部有上限（层 ≤ 16、一包输入 ≤ 8 且必须连续升序、事件 ≤ 64）；
//   6) 字段号**不得倒退**、**标量字段不得重复**（数组用重复的 message 字段承载：输入条目 10 /
//      活跃层 22 / 事件条目 11）、**完整 Sync/Aux 所有字段必须存在**，
//      读完后必须恰好到底 —— 任何未知字段/重复标量/缺字段/尾随字节都拒绝。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0），只依赖 PMNet 原语与 PMMover 纯数据。
// 本文件**不得**引用 UnityEngine。
//
// 编码侧（本地数据非法）**抛 FormatException**：那是编程错误，必须尽早暴露，
// 不允许静默截断或"尽力而为"地发出去。
// 解码侧（远端数据不可信）**返回 false + error**：调用方 fail-closed 拒绝，绝不半应用。

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.R3
{
    /// <summary>
    /// 载荷种类。放在 blob 头部而不是靠"哪条 RPC"来区分：
    /// 这样「Resync 载荷被喂给普通快照通道」这类错用**在读头部时就被拒绝**，
    /// 而不是等到某个字段碰巧不合法才失败。
    /// </summary>
    public enum PMR4PayloadKind
    {
        None = 0,

        /// <summary>上行输入批（AP → DS）。</summary>
        Inputs = 1,

        /// <summary>普通权威快照（DS → 所有相关端，经复制属性）。</summary>
        Snapshot = 2,

        /// <summary>权威事件批（DS → owner，经可靠 RPC）。</summary>
        Events = 3,

        /// <summary>显式重同步的完整快照（DS → owner，经可靠 RPC；携带新的 streamVersion）。</summary>
        Resync = 4,
    }

    /// <summary>上行输入批里的一条输入（Effects/Layers/RemovedLayerIds 一律为空：上行不开放）。</summary>
    public struct PMR4MovementInputEntry
    {
        /// <summary>输入帧号（Input 命名空间）。</summary>
        public long InputFrame;

        /// <summary>本步整毫秒 dt（1..50；0 是 DS 侧缺帧占位，**不允许上行**）。</summary>
        public int StepMs;

        /// <summary>原始输入意图（只含轴/朝向/跳跃边沿；不含任何派生量）。</summary>
        public PMMoverInput Input;
    }

    /// <summary>
    /// 一批上行输入。条目按帧号**严格升序且不重复**。
    /// 刻意**不要求连续**：这样同一包既能补最旧的缺口、又能带最新的输入
    /// （契约「按最旧未确认窗口持续重发防缺帧饥饿；可兼带最新但不能饿死旧缺口」）。
    /// 排序与去重由 DS 侧的输入缓冲（<c>PMAuthorityInputBuffer</c>）负责。
    /// </summary>
    public sealed class PMR4MovementInputBatch
    {
        public uint Epoch;
        public uint InstanceId;
        public uint StreamVersion;
        public PMR4MovementInputEntry[] Entries;
    }

    /// <summary>权威快照 blob（普通快照与 Resync 快照共用同一份结构，只差 kind 头）。</summary>
    public sealed class PMR4MovementSnapshotBlob
    {
        public uint Epoch;
        public uint InstanceId;
        public uint StreamVersion;

        /// <summary>该快照对应的**输出边界**（Input 命名空间）；输入帧 n 产出边界 n+1。</summary>
        public long OutputFrame;

        /// <summary>DS 自身权威帧（AuthorityServer 命名空间）；0 = 未建立。</summary>
        public long ServerFrame;

        /// <summary>该边界上的累计仿真时间（毫秒；DS 权威口径）。</summary>
        public double TotalSimTimeMs;

        public PMMoverSyncState Sync;
        public PMMoverAuxState Aux;
    }

    /// <summary>一条权威事件。Key 非 0 且**只由 (边界, 种类) 决定**，因此天然可去重、可跨重发识别。</summary>
    public struct PMR4MovementEventRecord
    {
        public long Boundary;
        public ulong Key;
        public int Kind;
        public int Value;
    }

    /// <summary>
    /// 一批权威事件：**顺序序号 + 事件集合**。
    /// 序号（<see cref="Sequence"/>）是「本流内事件批的连续编号」，从 1 起；
    /// 它让接收侧能区分「重复」（序号 ≤ 已收）与「缺口」（序号 &gt; 期望 + 1，必须显式失败而不是跳过）。
    /// </summary>
    public sealed class PMR4MovementEventBatch
    {
        public uint Epoch;
        public uint InstanceId;
        public uint StreamVersion;
        public int Sequence;
        public PMR4MovementEventRecord[] Events;
    }

    /// <summary>
    /// 运动载荷编解码。全部字段号与上限都在这里单点定义（不散到调用方）。
    /// </summary>
    public static class PMR4MovementCodec
    {
        // ================================================================ 冻结常量

        /// <summary>blob 内部编码版本。V1 声明名固定，版本更替走新声明名（进入生成协议摘要）。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>单个 blob 的字节上限（契约 §B1「所有 blob ≤ 4096 字节」）。</summary>
        public const int MaxBlobBytes = 4096;

        /// <summary>一包最多携带的输入条数（契约 §B1）。</summary>
        public const int MaxInputsPerPacket = 8;

        /// <summary>未确认输入窗口上限（契约 §B1：最多 128 条）。</summary>
        public const int MaxUnackedInputs = 128;

        /// <summary>活跃层数组上限（契约 §B1「数组 ≤ 16 层」）。</summary>
        public const int MaxLayers = 16;

        /// <summary>一批最多携带的事件条数。</summary>
        public const int MaxEventsPerBatch = 64;

        /// <summary>输入轴分量的合法范围（含端点）。</summary>
        public const float MaxMoveAxis = 1f;

        /// <summary>上行 dt 的下限（0 是 DS 侧占位，不允许上行）。</summary>
        public const int MinInputStepMs = 1;

        /// <summary>上行 dt 的上限。</summary>
        public const int MaxInputStepMs = 50;

        /// <summary>朝向的规范范围：线上只允许 [0, 360)。</summary>
        public const float MaxYawDegrees = 360f;

        // ---- 头部字段号 ----

        private const int FKind = 1;
        private const int FVersion = 2;
        private const int FEpoch = 3;
        private const int FInstance = 4;
        private const int FStream = 5;

        // ---- 载荷字段号 ----

        private const int FInputEntry = 10;

        private const int FOutputFrame = 10;
        private const int FServerFrame = 11;
        private const int FTotalMs = 12;
        private const int FSync = 13;
        private const int FAux = 14;

        private const int FSequence = 10;
        private const int FEvent = 11;

        /// <summary>SyncState 的 1..21 号标量字段全部必填。</summary>
        private const uint SyncRequiredMask = (1u << 22) - 1u - (1u << 0) - 0u; // bits 1..21

        /// <summary>AuxState 的 1..5 号字段全部必填。</summary>
        private const uint AuxRequiredMask = (1u << 6) - 1u - (1u << 0); // bits 1..5

        private static readonly PMR4MovementInputEntry[] NoInputEntries = new PMR4MovementInputEntry[0];
        private static readonly PMR4MovementEventRecord[] NoEventRecords = new PMR4MovementEventRecord[0];

        // ================================================================ 公共校验

        private static FormatException Bad(string what)
        {
            return new FormatException("[PMR4MovementCodec] " + what);
        }

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

        /// <summary>
        /// 规范朝向：把任意有限角度折算到 [0, 360)。非有限值返回 false。
        /// 线上只承载规范值，因此「越界 yaw」在读侧是**畸形**而不是需要归一化的输入。
        /// </summary>
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

            // -0f 与 360f 都要归一到 0，避免同一朝向出现两种线上表示。
            if (wrapped == MaxYawDegrees || wrapped == 0f)
            {
                wrapped = 0f;
            }

            normalized = wrapped;
            return true;
        }

        /// <summary>某条输入是否满足上行契约（轴范围/有限性/dt/无 Effect/Layer 权限）。</summary>
        public static bool IsValidUplinkInput(in PMMoverInput input, int stepMs, out string error)
        {
            error = null;

            if (stepMs < MinInputStepMs || stepMs > MaxInputStepMs)
            {
                error = "上行 dt 必须在 " + MinInputStepMs + ".." + MaxInputStepMs + "（收到 " + stepMs
                        + "；0 是 DS 侧缺帧占位，不允许上行）";
                return false;
            }

            if (!IsFinite(input.MoveX) || !IsFinite(input.MoveZ) || !IsFinite(input.MoveY))
            {
                error = "输入轴出现 NaN/Infinity";
                return false;
            }

            if (Math.Abs(input.MoveX) > MaxMoveAxis || Math.Abs(input.MoveZ) > MaxMoveAxis
                || Math.Abs(input.MoveY) > MaxMoveAxis)
            {
                error = "输入轴超出 [-1,1]（" + input.MoveX + "," + input.MoveZ + "," + input.MoveY + "）";
                return false;
            }

            if (!IsFinite(input.YawDegrees))
            {
                error = "朝向为 NaN/Infinity";
                return false;
            }

            if (input.Effects != null && input.Effects.Length > 0)
            {
                error = "上行不得携带 Effects（运动 Effect 只能由 DS 在帧边界提交可信命令）";
                return false;
            }

            if (input.Layers != null && input.Layers.Length > 0)
            {
                error = "上行不得携带 Layers";
                return false;
            }

            if (input.RemovedLayerIds != null && input.RemovedLayerIds.Length > 0)
            {
                error = "上行不得携带 RemovedLayerIds";
                return false;
            }

            return true;
        }

        /// <summary>完整 SyncState 的字段合法性（尺寸/参数可用、Mode 已知、层有界且有限）。</summary>
        public static bool IsValidSync(in PMMoverSyncState sync, out string error)
        {
            error = null;

            if (!sync.Position.IsFinite || !sync.Velocity.IsFinite || !sync.PreAdditiveVelocity.IsFinite)
            {
                error = "Position/Velocity/PreAdditiveVelocity 出现 NaN/Infinity";
                return false;
            }

            if (!IsFinite(sync.YawDegrees))
            {
                error = "YawDegrees 为 NaN/Infinity";
                return false;
            }

            if (!PMMoverModes.IsDefined(sync.Mode))
            {
                error = "未知 Mode 取值 " + (int)sync.Mode + "（必须落在 PMMoverMode 取值域内）";
                return false;
            }

            if (!sync.GroundNormal.IsFinite)
            {
                error = "GroundNormal 出现 NaN/Infinity";
                return false;
            }

            if (!IsFinite(sync.Scale) || sync.Scale <= 0f)
            {
                error = "Scale 必须有限且 > 0（收到 " + sync.Scale + "）";
                return false;
            }

            if (!IsFinite(sync.MaxSpeed) || !IsFinite(sync.Acceleration) || !IsFinite(sync.Braking)
                || !IsFinite(sync.GravityScale) || !IsFinite(sync.JumpSpeed))
            {
                error = "运动参数出现 NaN/Infinity";
                return false;
            }

            if (sync.MaxSpeed < 0f || sync.Acceleration < 0f || sync.Braking < 0f || sync.GravityScale < 0f)
            {
                error = "运动参数不得为负（MaxSpeed/Acceleration/Braking/GravityScale）";
                return false;
            }

            PMMoverLayer[] layers = sync.ActiveLayers;
            if (layers != null)
            {
                if (layers.Length > MaxLayers)
                {
                    error = "活跃层数 " + layers.Length + " 超过上限 " + MaxLayers;
                    return false;
                }

                for (int i = 0; i < layers.Length; i++)
                {
                    if (layers[i].InstanceId == 0u)
                    {
                        error = "第 " + i + " 层的 InstanceId 为 0";
                        return false;
                    }

                    if (!PMMoverLayerKinds.IsDefined(layers[i].Kind))
                    {
                        error = "第 " + i + " 层的 Kind 未知：" + (int)layers[i].Kind;
                        return false;
                    }

                    if (!layers[i].Velocity.IsFinite)
                    {
                        error = "第 " + i + " 层的 Velocity 出现 NaN/Infinity";
                        return false;
                    }

                    if (layers[i].DurationMs < 0)
                    {
                        error = "第 " + i + " 层的 DurationMs 为负";
                        return false;
                    }

                    if (layers[i].ElapsedMs < 0)
                    {
                        error = "第 " + i + " 层的 ElapsedMs 为负";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>环境状态合法性。</summary>
        public static bool IsValidAux(in PMMoverAuxState aux, out string error)
        {
            error = null;

            if (!aux.Gravity.IsFinite)
            {
                error = "Gravity 出现 NaN/Infinity";
                return false;
            }

            if (aux.CollisionWorldVersion < 0 || aux.ConfigVersion < 0)
            {
                error = "碰撞世界/配置版本为负";
                return false;
            }

            return true;
        }

        // ================================================================ 编码：输入批

        /// <summary>
        /// 编码一批上行输入。本地数据非法 ⇒ 抛 <see cref="FormatException"/>。
        /// 条目必须**严格升序且不重复**，条数 1..8。
        /// </summary>
        public static byte[] EncodeInputs(uint epoch, uint instanceId, uint streamVersion,
                                          PMR4MovementInputEntry[] entries)
        {
            ValidateIdentity(epoch, instanceId, streamVersion);

            if (entries == null || entries.Length == 0)
            {
                throw Bad("输入批为空（至少要有一条输入）");
            }

            if (entries.Length > MaxInputsPerPacket)
            {
                throw Bad("一包输入条数 " + entries.Length + " 超过上限 " + MaxInputsPerPacket);
            }

            for (int i = 0; i < entries.Length; i++)
            {
                PMR4MovementInputEntry entry = entries[i];
                string why;
                if (!IsValidUplinkInput(entry.Input, entry.StepMs, out why))
                {
                    throw Bad("第 " + i + " 条输入非法：" + why);
                }

                if (entry.InputFrame < 0L)
                {
                    throw Bad("第 " + i + " 条输入帧号不得为负（收到 " + entry.InputFrame + "）");
                }

                float canonicalYaw;
                if (!TryNormalizeYaw(entry.Input.YawDegrees, out canonicalYaw))
                {
                    throw Bad("第 " + i + " 条输入的朝向非有限");
                }

                if (i > 0 && entries[i].InputFrame <= entries[i - 1].InputFrame)
                {
                    throw Bad("输入条目的帧号必须严格升序且不重复（第 " + (i - 1) + " 条 " + entries[i - 1].InputFrame
                              + " → 第 " + i + " 条 " + entry.InputFrame + "）");
                }
            }

            PMNetWriter writer = new PMNetWriter(128);
            WriteHeader(writer, PMR4PayloadKind.Inputs, epoch, instanceId, streamVersion);

            for (int i = 0; i < entries.Length; i++)
            {
                PMR4MovementInputEntry entry = entries[i];
                float canonicalYaw;
                TryNormalizeYaw(entry.Input.YawDegrees, out canonicalYaw);

                PMNetWriter sub = writer.RentSubWriter();
                sub.WriteTag(1, PMWireType.Varint);
                sub.WriteSInt64(entry.InputFrame);
                sub.WriteTag(2, PMWireType.Varint);
                sub.WriteVarint((ulong)entry.StepMs);
                sub.WriteTag(3, PMWireType.Fixed32);
                sub.WriteFloat(entry.Input.MoveX);
                sub.WriteTag(4, PMWireType.Fixed32);
                sub.WriteFloat(entry.Input.MoveZ);
                sub.WriteTag(5, PMWireType.Fixed32);
                sub.WriteFloat(entry.Input.MoveY);
                sub.WriteTag(6, PMWireType.Fixed32);
                sub.WriteFloat(canonicalYaw);
                sub.WriteTag(7, PMWireType.Varint);
                sub.WriteBool(entry.Input.JumpPressed);
                writer.WriteSubMessage(FInputEntry, sub);
            }

            return Finish(writer, "输入批");
        }

        /// <summary>
        /// 解码上行输入批。远端数据不可信 ⇒ 任何疑问都返回 false（fail-closed，绝无部分应用）。
        /// </summary>
        public static bool TryDecodeInputs(byte[] payload, int offset, int count,
                                          out PMR4MovementInputBatch batch, out string error)
        {
            batch = null;

            PMR4OpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMR4PayloadKind.Inputs, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMR4MovementInputEntry[] entries = new PMR4MovementInputEntry[MaxInputsPerPacket];
            int used = 0;

            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;
            int lastField = FStream;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, FInputEntry, out error))
                    {
                        return false;
                    }

                    if (field != FInputEntry)
                    {
                        error = "输入批出现未知字段 " + field;
                        return false;
                    }

                    if (wire != PMWireType.LengthDelimited)
                    {
                        error = "输入条目字段的 wire type 不是 length-delimited";
                        return false;
                    }

                    if (used >= MaxInputsPerPacket)
                    {
                        error = "输入条数超过上限 " + MaxInputsPerPacket;
                        return false;
                    }

                    PMR4MovementInputEntry entry;
                    if (!TryReadInputEntry(reader.ReadSubReader(), out entry, out error))
                    {
                        return false;
                    }

                    if (used > 0 && entry.InputFrame <= entries[used - 1].InputFrame)
                    {
                        error = "输入条目的帧号必须严格升序且不重复（" + entries[used - 1].InputFrame
                                + " → " + entry.InputFrame + "）";
                        return false;
                    }

                    entries[used] = entry;
                    used++;
                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "输入批解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "输入批解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "输入批解码溢出：" + ex.Message;
                return false;
            }

            if (used == 0)
            {
                error = "输入批不含任何条目";
                return false;
            }

            PMR4MovementInputEntry[] exact = new PMR4MovementInputEntry[used];
            Array.Copy(entries, exact, used);

            batch = new PMR4MovementInputBatch();
            batch.Epoch = open.Epoch;
            batch.InstanceId = open.InstanceId;
            batch.StreamVersion = open.StreamVersion;
            batch.Entries = exact;
            error = null;
            return true;
        }

        private static bool TryReadInputEntry(PMNetReader reader, out PMR4MovementInputEntry entry, out string error)
        {
            entry = default(PMR4MovementInputEntry);
            error = null;
            entry.Input = PMMoverInput.Empty();

            bool hasFrame = false;
            bool hasStep = false;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, 0, out error))
                    {
                        return false;
                    }

                    switch (field)
                    {
                        case 1:
                            if (wire != PMWireType.Varint) { error = "inputFrame 的 wire type 不对"; return false; }
                            entry.InputFrame = reader.ReadSInt64();
                            hasFrame = true;
                            break;
                        case 2:
                            if (wire != PMWireType.Varint) { error = "stepMs 的 wire type 不对"; return false; }
                            entry.StepMs = checked((int)reader.ReadVarint());
                            hasStep = true;
                            break;
                        case 3:
                            if (wire != PMWireType.Fixed32) { error = "moveX 的 wire type 不对"; return false; }
                            entry.Input.MoveX = reader.ReadFloat();
                            break;
                        case 4:
                            if (wire != PMWireType.Fixed32) { error = "moveZ 的 wire type 不对"; return false; }
                            entry.Input.MoveZ = reader.ReadFloat();
                            break;
                        case 5:
                            if (wire != PMWireType.Fixed32) { error = "moveY 的 wire type 不对"; return false; }
                            entry.Input.MoveY = reader.ReadFloat();
                            break;
                        case 6:
                            if (wire != PMWireType.Fixed32) { error = "yaw 的 wire type 不对"; return false; }
                            entry.Input.YawDegrees = reader.ReadFloat();
                            break;
                        case 7:
                            if (wire != PMWireType.Varint) { error = "jump 的 wire type 不对"; return false; }
                            entry.Input.JumpPressed = reader.ReadBool();
                            break;
                        default:
                            error = "输入条目出现未知字段 " + field;
                            return false;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "输入条目解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "输入条目解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "输入条目解码溢出：" + ex.Message;
                return false;
            }

            if (!hasFrame || !hasStep)
            {
                error = "输入条目缺少必填字段（inputFrame/stepMs）";
                return false;
            }

            string why;
            if (!IsValidUplinkInput(entry.Input, entry.StepMs, out why))
            {
                error = "输入条目违反上行契约：" + why;
                return false;
            }

            float canonicalYaw;
            if (!TryNormalizeYaw(entry.Input.YawDegrees, out canonicalYaw) || canonicalYaw != entry.Input.YawDegrees)
            {
                error = "输入条目的朝向不是规范范围 [0,360)（收到 " + entry.Input.YawDegrees + "）";
                return false;
            }

            if (entry.InputFrame < 0L)
            {
                error = "输入帧号不得为负（收到 " + entry.InputFrame + "）";
                return false;
            }

            return true;
        }

        // ================================================================ 编码：快照 / 重同步

        /// <summary>编码完整权威快照。本地数据非法 ⇒ 抛 <see cref="FormatException"/>。</summary>
        public static byte[] EncodeSnapshot(bool resync, PMR4MovementSnapshotBlob snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            ValidateIdentity(snapshot.Epoch, snapshot.InstanceId, snapshot.StreamVersion);

            if (snapshot.OutputFrame < 0L)
            {
                throw Bad("输出边界为负：" + snapshot.OutputFrame);
            }

            if (snapshot.ServerFrame < 0L)
            {
                throw Bad("权威帧为负：" + snapshot.ServerFrame);
            }

            if (!IsFinite(snapshot.TotalSimTimeMs) || snapshot.TotalSimTimeMs < 0.0)
            {
                throw Bad("累计仿真时间非有限或为负：" + snapshot.TotalSimTimeMs);
            }

            string why;
            if (!IsValidSync(snapshot.Sync, out why))
            {
                throw Bad("SyncState 非法：" + why);
            }

            if (!IsValidAux(snapshot.Aux, out why))
            {
                throw Bad("AuxState 非法：" + why);
            }

            float canonicalYaw;
            if (!TryNormalizeYaw(snapshot.Sync.YawDegrees, out canonicalYaw))
            {
                throw Bad("SyncState 的朝向非有限");
            }

            PMMoverSyncState sync = snapshot.Sync;
            sync.YawDegrees = canonicalYaw;

            PMNetWriter writer = new PMNetWriter(256);
            WriteHeader(writer, resync ? PMR4PayloadKind.Resync : PMR4PayloadKind.Snapshot,
                snapshot.Epoch, snapshot.InstanceId, snapshot.StreamVersion);

            writer.WriteTag(FOutputFrame, PMWireType.Varint);
            writer.WriteSInt64(snapshot.OutputFrame);
            writer.WriteTag(FServerFrame, PMWireType.Varint);
            writer.WriteSInt64(snapshot.ServerFrame);
            writer.WriteTag(FTotalMs, PMWireType.Fixed64);
            writer.WriteDouble(snapshot.TotalSimTimeMs);
            writer.WriteSubMessage(FSync, EncodeSyncValue(sync));
            writer.WriteSubMessage(FAux, EncodeAuxValue(snapshot.Aux));

            return Finish(writer, resync ? "重同步快照" : "权威快照");
        }

        /// <summary>解码快照（普通 / 重同步按 <paramref name="expectedKind"/> 区分）。</summary>
        public static bool TryDecodeSnapshot(byte[] payload, int offset, int count, PMR4PayloadKind expectedKind,
                                             out PMR4MovementSnapshotBlob snapshot, out string error)
        {
            snapshot = null;

            if (expectedKind != PMR4PayloadKind.Snapshot && expectedKind != PMR4PayloadKind.Resync)
            {
                throw new ArgumentOutOfRangeException("expectedKind");
            }

            PMR4OpenPayload open;
            if (!TryOpenPayload(payload, offset, count, expectedKind, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            PMR4MovementSnapshotBlob blob = new PMR4MovementSnapshotBlob();
            blob.Epoch = open.Epoch;
            blob.InstanceId = open.InstanceId;
            blob.StreamVersion = open.StreamVersion;

            bool hasOutputFrame = false;
            bool hasTotalMs = false;
            bool hasSync = false;
            bool hasAux = false;

            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;
            int lastField = FStream;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, 0, out error))
                    {
                        return false;
                    }

                    switch (field)
                    {
                        case FOutputFrame:
                            if (wire != PMWireType.Varint) { error = "outputFrame 的 wire type 不对"; return false; }
                            blob.OutputFrame = reader.ReadSInt64();
                            hasOutputFrame = true;
                            break;
                        case FServerFrame:
                            if (wire != PMWireType.Varint) { error = "serverFrame 的 wire type 不对"; return false; }
                            blob.ServerFrame = reader.ReadSInt64();
                            break;
                        case FTotalMs:
                            if (wire != PMWireType.Fixed64) { error = "totalSimTimeMs 的 wire type 不对"; return false; }
                            blob.TotalSimTimeMs = reader.ReadDouble();
                            hasTotalMs = true;
                            break;
                        case FSync:
                            if (wire != PMWireType.LengthDelimited) { error = "sync 的 wire type 不对"; return false; }
                            if (!TryReadSync(reader.ReadSubReader(), out blob.Sync, out error)) { return false; }
                            hasSync = true;
                            break;
                        case FAux:
                            if (wire != PMWireType.LengthDelimited) { error = "aux 的 wire type 不对"; return false; }
                            if (!TryReadAux(reader.ReadSubReader(), out blob.Aux, out error)) { return false; }
                            hasAux = true;
                            break;
                        default:
                            error = "快照出现未知字段 " + field;
                            return false;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "快照解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "快照解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "快照解码溢出：" + ex.Message;
                return false;
            }

            if (!hasOutputFrame || !hasTotalMs || !hasSync || !hasAux)
            {
                error = "快照缺少必填字段（outputFrame/totalSimTimeMs/sync/aux）";
                return false;
            }

            if (blob.OutputFrame < 0L || blob.ServerFrame < 0L)
            {
                error = "快照的边界/权威帧为负";
                return false;
            }

            if (!IsFinite(blob.TotalSimTimeMs) || blob.TotalSimTimeMs < 0.0)
            {
                error = "快照的累计仿真时间非有限或为负";
                return false;
            }

            string why;
            if (!IsValidSync(blob.Sync, out why))
            {
                error = "快照 SyncState 非法：" + why;
                return false;
            }

            if (!IsValidAux(blob.Aux, out why))
            {
                error = "快照 AuxState 非法：" + why;
                return false;
            }

            float canonicalYaw;
            if (!TryNormalizeYaw(blob.Sync.YawDegrees, out canonicalYaw) || canonicalYaw != blob.Sync.YawDegrees)
            {
                error = "快照朝向不是规范范围 [0,360)（收到 " + blob.Sync.YawDegrees + "）";
                return false;
            }

            snapshot = blob;
            error = null;
            return true;
        }

        private static PMNetWriter EncodeSyncValue(in PMMoverSyncState sync)
        {
            PMNetWriter sub = new PMNetWriter(192);

            sub.WriteTag(1, PMWireType.Fixed32); sub.WriteFloat(sync.Position.X);
            sub.WriteTag(2, PMWireType.Fixed32); sub.WriteFloat(sync.Position.Y);
            sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(sync.Position.Z);
            sub.WriteTag(4, PMWireType.Fixed32); sub.WriteFloat(sync.Velocity.X);
            sub.WriteTag(5, PMWireType.Fixed32); sub.WriteFloat(sync.Velocity.Y);
            sub.WriteTag(6, PMWireType.Fixed32); sub.WriteFloat(sync.Velocity.Z);
            sub.WriteTag(7, PMWireType.Fixed32); sub.WriteFloat(sync.PreAdditiveVelocity.X);
            sub.WriteTag(8, PMWireType.Fixed32); sub.WriteFloat(sync.PreAdditiveVelocity.Y);
            sub.WriteTag(9, PMWireType.Fixed32); sub.WriteFloat(sync.PreAdditiveVelocity.Z);
            sub.WriteTag(10, PMWireType.Fixed32); sub.WriteFloat(sync.YawDegrees);
            sub.WriteTag(11, PMWireType.Varint); sub.WriteVarint((ulong)(byte)sync.Mode);
            sub.WriteTag(12, PMWireType.Varint); sub.WriteBool(sync.Grounded);
            sub.WriteTag(13, PMWireType.Fixed32); sub.WriteFloat(sync.GroundNormal.X);
            sub.WriteTag(14, PMWireType.Fixed32); sub.WriteFloat(sync.GroundNormal.Y);
            sub.WriteTag(15, PMWireType.Fixed32); sub.WriteFloat(sync.GroundNormal.Z);
            sub.WriteTag(16, PMWireType.Fixed32); sub.WriteFloat(sync.Scale);
            sub.WriteTag(17, PMWireType.Fixed32); sub.WriteFloat(sync.MaxSpeed);
            sub.WriteTag(18, PMWireType.Fixed32); sub.WriteFloat(sync.Acceleration);
            sub.WriteTag(19, PMWireType.Fixed32); sub.WriteFloat(sync.Braking);
            sub.WriteTag(20, PMWireType.Fixed32); sub.WriteFloat(sync.GravityScale);
            sub.WriteTag(21, PMWireType.Fixed32); sub.WriteFloat(sync.JumpSpeed);

            PMMoverLayer[] layers = sync.ActiveLayers;
            if (layers != null)
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    PMMoverLayer layer = layers[i];
                    PMNetWriter layerWriter = sub.RentSubWriter();
                    layerWriter.WriteTag(1, PMWireType.Varint); layerWriter.WriteVarint(layer.InstanceId);
                    layerWriter.WriteTag(2, PMWireType.Varint); layerWriter.WriteVarint((ulong)(byte)layer.Kind);
                    layerWriter.WriteTag(3, PMWireType.Varint); layerWriter.WriteSInt32(layer.Priority);
                    layerWriter.WriteTag(4, PMWireType.Fixed32); layerWriter.WriteFloat(layer.Velocity.X);
                    layerWriter.WriteTag(5, PMWireType.Fixed32); layerWriter.WriteFloat(layer.Velocity.Y);
                    layerWriter.WriteTag(6, PMWireType.Fixed32); layerWriter.WriteFloat(layer.Velocity.Z);
                    layerWriter.WriteTag(7, PMWireType.Varint); layerWriter.WriteSInt32(layer.DurationMs);
                    layerWriter.WriteTag(8, PMWireType.Varint); layerWriter.WriteSInt32(layer.ElapsedMs);
                    sub.WriteSubMessage(22, layerWriter);
                }
            }

            return sub;
        }

        private static bool TryReadSync(PMNetReader reader, out PMMoverSyncState sync, out string error)
        {
            sync = PMMoverSyncState.CreateDefault();
            sync.ActiveLayers = null;
            error = null;

            uint seen = 0u;
            List<PMMoverLayer> layers = null;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, 22, out error))
                    {
                        return false;
                    }

                    if (field == 22)
                    {
                        if (wire != PMWireType.LengthDelimited) { error = "layer 的 wire type 不对"; return false; }
                        if (layers == null) { layers = new List<PMMoverLayer>(4); }
                        if (layers.Count >= MaxLayers)
                        {
                            error = "活跃层数超过上限 " + MaxLayers;
                            return false;
                        }

                        PMMoverLayer layer;
                        if (!TryReadLayer(reader.ReadSubReader(), out layer, out error)) { return false; }
                        layers.Add(layer);
                        lastField = field;
                        continue;
                    }

                    if (field < 1 || field > 21)
                    {
                        error = "SyncState 出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << field;

                    switch (field)
                    {
                        case 1:
                            if (wire != PMWireType.Fixed32) { error = "positionX 的 wire type 不对"; return false; }
                            sync.Position.X = reader.ReadFloat();
                            break;
                        case 2:
                            if (wire != PMWireType.Fixed32) { error = "positionY 的 wire type 不对"; return false; }
                            sync.Position.Y = reader.ReadFloat();
                            break;
                        case 3:
                            if (wire != PMWireType.Fixed32) { error = "positionZ 的 wire type 不对"; return false; }
                            sync.Position.Z = reader.ReadFloat();
                            break;
                        case 4:
                            if (wire != PMWireType.Fixed32) { error = "velocityX 的 wire type 不对"; return false; }
                            sync.Velocity.X = reader.ReadFloat();
                            break;
                        case 5:
                            if (wire != PMWireType.Fixed32) { error = "velocityY 的 wire type 不对"; return false; }
                            sync.Velocity.Y = reader.ReadFloat();
                            break;
                        case 6:
                            if (wire != PMWireType.Fixed32) { error = "velocityZ 的 wire type 不对"; return false; }
                            sync.Velocity.Z = reader.ReadFloat();
                            break;
                        case 7:
                            if (wire != PMWireType.Fixed32) { error = "preAdditiveVelocityX 的 wire type 不对"; return false; }
                            sync.PreAdditiveVelocity.X = reader.ReadFloat();
                            break;
                        case 8:
                            if (wire != PMWireType.Fixed32) { error = "preAdditiveVelocityY 的 wire type 不对"; return false; }
                            sync.PreAdditiveVelocity.Y = reader.ReadFloat();
                            break;
                        case 9:
                            if (wire != PMWireType.Fixed32) { error = "preAdditiveVelocityZ 的 wire type 不对"; return false; }
                            sync.PreAdditiveVelocity.Z = reader.ReadFloat();
                            break;
                        case 10:
                            if (wire != PMWireType.Fixed32) { error = "yaw 的 wire type 不对"; return false; }
                            sync.YawDegrees = reader.ReadFloat();
                            break;
                        case 11:
                            if (wire != PMWireType.Varint) { error = "mode 的 wire type 不对"; return false; }
                            int modeValue = checked((int)reader.ReadVarint());
                            if (!PMMoverModes.IsDefined(modeValue))
                            {
                                error = "未知 Mode 取值 " + modeValue;
                                return false;
                            }

                            sync.Mode = (PMMoverMode)(byte)modeValue;
                            break;
                        case 12:
                            if (wire != PMWireType.Varint) { error = "grounded 的 wire type 不对"; return false; }
                            sync.Grounded = reader.ReadBool();
                            break;
                        case 13:
                            if (wire != PMWireType.Fixed32) { error = "groundNormalX 的 wire type 不对"; return false; }
                            sync.GroundNormal.X = reader.ReadFloat();
                            break;
                        case 14:
                            if (wire != PMWireType.Fixed32) { error = "groundNormalY 的 wire type 不对"; return false; }
                            sync.GroundNormal.Y = reader.ReadFloat();
                            break;
                        case 15:
                            if (wire != PMWireType.Fixed32) { error = "groundNormalZ 的 wire type 不对"; return false; }
                            sync.GroundNormal.Z = reader.ReadFloat();
                            break;
                        case 16:
                            if (wire != PMWireType.Fixed32) { error = "scale 的 wire type 不对"; return false; }
                            sync.Scale = reader.ReadFloat();
                            break;
                        case 17:
                            if (wire != PMWireType.Fixed32) { error = "maxSpeed 的 wire type 不对"; return false; }
                            sync.MaxSpeed = reader.ReadFloat();
                            break;
                        case 18:
                            if (wire != PMWireType.Fixed32) { error = "acceleration 的 wire type 不对"; return false; }
                            sync.Acceleration = reader.ReadFloat();
                            break;
                        case 19:
                            if (wire != PMWireType.Fixed32) { error = "braking 的 wire type 不对"; return false; }
                            sync.Braking = reader.ReadFloat();
                            break;
                        case 20:
                            if (wire != PMWireType.Fixed32) { error = "gravityScale 的 wire type 不对"; return false; }
                            sync.GravityScale = reader.ReadFloat();
                            break;
                        case 21:
                            if (wire != PMWireType.Fixed32) { error = "jumpSpeed 的 wire type 不对"; return false; }
                            sync.JumpSpeed = reader.ReadFloat();
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "SyncState 解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "SyncState 解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "SyncState 解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & SyncRequiredMask) != SyncRequiredMask)
            {
                error = "SyncState 缺少必填字段（契约要求完整 Sync 全部编码；缺失位掩码 0x"
                        + (~seen & SyncRequiredMask).ToString("X8") + "）";
                return false;
            }

            if (layers != null)
            {
                sync.ActiveLayers = layers.ToArray();
            }

            string why;
            if (!IsValidSync(sync, out why))
            {
                error = "SyncState 非法：" + why;
                return false;
            }

            return true;
        }

        private static bool TryReadLayer(PMNetReader reader, out PMMoverLayer layer, out string error)
        {
            layer = new PMMoverLayer();
            error = null;

            uint seen = 0u;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, 0, out error))
                    {
                        return false;
                    }

                    if (field < 1 || field > 8)
                    {
                        error = "layer 出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << field;

                    switch (field)
                    {
                        case 1:
                            if (wire != PMWireType.Varint) { error = "layer.instanceId 的 wire type 不对"; return false; }
                            layer.InstanceId = checked((uint)reader.ReadVarint());
                            break;
                        case 2:
                            if (wire != PMWireType.Varint) { error = "layer.kind 的 wire type 不对"; return false; }
                            int kindValue = checked((int)reader.ReadVarint());
                            if (!PMMoverLayerKinds.IsDefined(kindValue))
                            {
                                error = "layer 的 Kind 未知：" + kindValue;
                                return false;
                            }

                            layer.Kind = (PMMoverLayerKind)(byte)kindValue;
                            break;
                        case 3:
                            if (wire != PMWireType.Varint) { error = "layer.priority 的 wire type 不对"; return false; }
                            layer.Priority = reader.ReadSInt32();
                            break;
                        case 4:
                            if (wire != PMWireType.Fixed32) { error = "layer.velocityX 的 wire type 不对"; return false; }
                            layer.Velocity.X = reader.ReadFloat();
                            break;
                        case 5:
                            if (wire != PMWireType.Fixed32) { error = "layer.velocityY 的 wire type 不对"; return false; }
                            layer.Velocity.Y = reader.ReadFloat();
                            break;
                        case 6:
                            if (wire != PMWireType.Fixed32) { error = "layer.velocityZ 的 wire type 不对"; return false; }
                            layer.Velocity.Z = reader.ReadFloat();
                            break;
                        case 7:
                            if (wire != PMWireType.Varint) { error = "layer.durationMs 的 wire type 不对"; return false; }
                            layer.DurationMs = reader.ReadSInt32();
                            break;
                        case 8:
                            if (wire != PMWireType.Varint) { error = "layer.elapsedMs 的 wire type 不对"; return false; }
                            layer.ElapsedMs = reader.ReadSInt32();
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "layer 解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "layer 解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "layer 解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & 0x1FEu) != 0x1FEu)
            {
                error = "layer 缺少必填字段（instanceId/kind/priority/velocity×3/durationMs/elapsedMs 全部必填）";
                return false;
            }

            if (layer.InstanceId == 0u)
            {
                error = "layer.instanceId 为 0";
                return false;
            }

            if (!layer.Velocity.IsFinite)
            {
                error = "layer.velocity 出现 NaN/Infinity";
                return false;
            }

            if (layer.DurationMs < 0 || layer.ElapsedMs < 0)
            {
                error = "layer 的 DurationMs/ElapsedMs 为负";
                return false;
            }

            return true;
        }

        private static PMNetWriter EncodeAuxValue(in PMMoverAuxState aux)
        {
            PMNetWriter sub = new PMNetWriter(32);
            sub.WriteTag(1, PMWireType.Fixed32); sub.WriteFloat(aux.Gravity.X);
            sub.WriteTag(2, PMWireType.Fixed32); sub.WriteFloat(aux.Gravity.Y);
            sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(aux.Gravity.Z);
            sub.WriteTag(4, PMWireType.Varint); sub.WriteSInt32(aux.CollisionWorldVersion);
            sub.WriteTag(5, PMWireType.Varint); sub.WriteSInt32(aux.ConfigVersion);
            return sub;
        }

        private static bool TryReadAux(PMNetReader reader, out PMMoverAuxState aux, out string error)
        {
            aux = PMMoverAuxState.CreateDefault();
            aux.Gravity = PMVector3.Zero;
            error = null;

            uint seen = 0u;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, 0, out error))
                    {
                        return false;
                    }

                    if (field < 1 || field > 5)
                    {
                        error = "AuxState 出现未知字段 " + field;
                        return false;
                    }

                    seen |= 1u << field;

                    switch (field)
                    {
                        case 1:
                            if (wire != PMWireType.Fixed32) { error = "aux.gravityX 的 wire type 不对"; return false; }
                            aux.Gravity.X = reader.ReadFloat();
                            break;
                        case 2:
                            if (wire != PMWireType.Fixed32) { error = "aux.gravityY 的 wire type 不对"; return false; }
                            aux.Gravity.Y = reader.ReadFloat();
                            break;
                        case 3:
                            if (wire != PMWireType.Fixed32) { error = "aux.gravityZ 的 wire type 不对"; return false; }
                            aux.Gravity.Z = reader.ReadFloat();
                            break;
                        case 4:
                            if (wire != PMWireType.Varint) { error = "aux.collisionWorldVersion 的 wire type 不对"; return false; }
                            aux.CollisionWorldVersion = reader.ReadSInt32();
                            break;
                        case 5:
                            if (wire != PMWireType.Varint) { error = "aux.configVersion 的 wire type 不对"; return false; }
                            aux.ConfigVersion = reader.ReadSInt32();
                            break;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "AuxState 解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "AuxState 解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "AuxState 解码溢出：" + ex.Message;
                return false;
            }

            if ((seen & AuxRequiredMask) != AuxRequiredMask)
            {
                error = "AuxState 缺少必填字段（gravity×3/collisionWorldVersion/configVersion 全部必填）";
                return false;
            }

            string why;
            if (!IsValidAux(aux, out why))
            {
                error = "AuxState 非法：" + why;
                return false;
            }

            return true;
        }

        // ================================================================ 编码：事件批

        /// <summary>
        /// 编码一批权威事件。本地数据非法 ⇒ 抛 <see cref="FormatException"/>。
        /// 事件必须按边界升序、同边界 Key 唯一且非 0、Sequence ≥ 1。
        /// </summary>
        public static byte[] EncodeEvents(uint epoch, uint instanceId, uint streamVersion, int sequence,
                                          PMR4MovementEventRecord[] events)
        {
            ValidateIdentity(epoch, instanceId, streamVersion);

            if (sequence < 1)
            {
                throw Bad("事件批的序号必须 ≥ 1（收到 " + sequence + "）");
            }

            if (events == null || events.Length == 0)
            {
                throw Bad("事件批为空（契约要求只在**非空**事件边界承载事件）");
            }

            if (events.Length > MaxEventsPerBatch)
            {
                throw Bad("事件条数 " + events.Length + " 超过上限 " + MaxEventsPerBatch);
            }

            for (int i = 0; i < events.Length; i++)
            {
                if (events[i].Key == 0UL)
                {
                    throw Bad("第 " + i + " 条事件的 Key 为 0（契约要求稳定非 0 Key）");
                }

                if (events[i].Boundary <= 0L)
                {
                    throw Bad("第 " + i + " 条事件的边界必须为正（收到 " + events[i].Boundary + "）");
                }

                if (i > 0)
                {
                    if (events[i].Boundary < events[i - 1].Boundary)
                    {
                        throw Bad("事件必须按边界升序（第 " + (i - 1) + " 条 " + events[i - 1].Boundary
                                  + " → 第 " + i + " 条 " + events[i].Boundary + "）");
                    }

                    if (events[i].Boundary == events[i - 1].Boundary && events[i].Key == events[i - 1].Key)
                    {
                        throw Bad("同一边界内出现重复事件 Key（" + events[i].Key + "）");
                    }
                }
            }

            PMNetWriter writer = new PMNetWriter(128);
            WriteHeader(writer, PMR4PayloadKind.Events, epoch, instanceId, streamVersion);

            writer.WriteTag(FSequence, PMWireType.Varint);
            writer.WriteVarint((ulong)sequence);

            for (int i = 0; i < events.Length; i++)
            {
                PMNetWriter sub = writer.RentSubWriter();
                sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(events[i].Boundary);
                sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint(events[i].Key);
                sub.WriteTag(3, PMWireType.Varint); sub.WriteSInt32(events[i].Kind);
                sub.WriteTag(4, PMWireType.Varint); sub.WriteSInt32(events[i].Value);
                writer.WriteSubMessage(FEvent, sub);
            }

            return Finish(writer, "事件批");
        }

        /// <summary>解码事件批（远端数据不可信 ⇒ fail-closed）。</summary>
        public static bool TryDecodeEvents(byte[] payload, int offset, int count,
                                          out PMR4MovementEventBatch batch, out string error)
        {
            batch = null;

            PMR4OpenPayload open;
            if (!TryOpenPayload(payload, offset, count, PMR4PayloadKind.Events, out open, out error))
            {
                return false;
            }

            PMNetReader reader = open.Reader;
            int sequence = 0;
            bool hasSequence = false;
            PMR4MovementEventRecord[] events = new PMR4MovementEventRecord[MaxEventsPerBatch];
            int used = 0;

            int field = open.Field;
            PMWireType wire = open.Wire;
            bool hasField = open.HasField;
            int lastField = FStream;

            try
            {
                while (hasField || reader.ReadTag(out field, out wire))
                {
                    hasField = false;

                    if (!IsFieldOrderAccepted(lastField, field, FEvent, out error))
                    {
                        return false;
                    }

                    if (field == FSequence)
                    {
                        if (wire != PMWireType.Varint) { error = "sequence 的 wire type 不对"; return false; }
                        sequence = checked((int)reader.ReadVarint());
                        hasSequence = true;
                        continue;
                    }

                    if (field != FEvent)
                    {
                        error = "事件批出现未知字段 " + field;
                        return false;
                    }

                    if (wire != PMWireType.LengthDelimited)
                    {
                        error = "事件字段的 wire type 不对";
                        return false;
                    }

                    if (used >= MaxEventsPerBatch)
                    {
                        error = "事件条数超过上限 " + MaxEventsPerBatch;
                        return false;
                    }

                    PMR4MovementEventRecord record;
                    if (!TryReadEventRecord(reader.ReadSubReader(), out record, out error))
                    {
                        return false;
                    }

                    if (used > 0)
                    {
                        if (record.Boundary < events[used - 1].Boundary)
                        {
                            error = "事件必须按边界升序";
                            return false;
                        }

                        if (record.Boundary == events[used - 1].Boundary && record.Key == events[used - 1].Key)
                        {
                            error = "同一边界内出现重复事件 Key";
                            return false;
                        }
                    }

                    events[used] = record;
                    used++;
                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "事件批解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "事件批解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "事件批解码溢出：" + ex.Message;
                return false;
            }

            if (!hasSequence || sequence < 1)
            {
                error = "事件批缺少合法的 sequence（必须 ≥ 1）";
                return false;
            }

            if (used == 0)
            {
                error = "事件批不含任何事件";
                return false;
            }

            PMR4MovementEventRecord[] exact = new PMR4MovementEventRecord[used];
            Array.Copy(events, exact, used);

            batch = new PMR4MovementEventBatch();
            batch.Epoch = open.Epoch;
            batch.InstanceId = open.InstanceId;
            batch.StreamVersion = open.StreamVersion;
            batch.Sequence = sequence;
            batch.Events = exact;
            error = null;
            return true;
        }

        private static bool TryReadEventRecord(PMNetReader reader, out PMR4MovementEventRecord record, out string error)
        {
            record = default(PMR4MovementEventRecord);
            error = null;

            bool hasBoundary = false;
            bool hasKey = false;
            int lastField = 0;
            int field;
            PMWireType wire;

            try
            {
                while (reader.ReadTag(out field, out wire))
                {
                    if (!IsFieldOrderAccepted(lastField, field, 0, out error))
                    {
                        return false;
                    }

                    switch (field)
                    {
                        case 1:
                            if (wire != PMWireType.Varint) { error = "event.boundary 的 wire type 不对"; return false; }
                            record.Boundary = reader.ReadSInt64();
                            hasBoundary = true;
                            break;
                        case 2:
                            if (wire != PMWireType.Varint) { error = "event.key 的 wire type 不对"; return false; }
                            record.Key = reader.ReadVarint();
                            hasKey = true;
                            break;
                        case 3:
                            if (wire != PMWireType.Varint) { error = "event.kind 的 wire type 不对"; return false; }
                            record.Kind = reader.ReadSInt32();
                            break;
                        case 4:
                            if (wire != PMWireType.Varint) { error = "event.value 的 wire type 不对"; return false; }
                            record.Value = reader.ReadSInt32();
                            break;
                        default:
                            error = "事件条目出现未知字段 " + field;
                            return false;
                    }

                    lastField = field;
                }

                if (!reader.IsAtEnd)
                {
                    error = "事件条目解码后仍有未消费字节";
                    return false;
                }
            }
            catch (FormatException ex)
            {
                error = "事件条目解码失败：" + ex.Message;
                return false;
            }
            catch (OverflowException ex)
            {
                error = "事件条目解码溢出：" + ex.Message;
                return false;
            }

            if (!hasBoundary || !hasKey)
            {
                error = "事件条目缺少必填字段（boundary/key）";
                return false;
            }

            if (record.Key == 0UL)
            {
                error = "事件 Key 为 0";
                return false;
            }

            if (record.Boundary <= 0L)
            {
                error = "事件边界必须为正（收到 " + record.Boundary + "）";
                return false;
            }

            return true;
        }

        // ================================================================ 头部与公用

        /// <summary>打开载荷：校验长度上限、kind、version，并读出身份三件套。</summary>
        private sealed class PMR4OpenPayload
        {
            public PMNetReader Reader;
            public uint Epoch;
            public uint InstanceId;
            public uint StreamVersion;

            /// <summary>头部之后已经读到的第一个字段（0 = 头部之后恰好结束）。</summary>
            public int Field;
            public PMWireType Wire;
            public bool HasField;
        }

        private static void ValidateIdentity(uint epoch, uint instanceId, uint streamVersion)
        {
            if (epoch == 0u)
            {
                throw Bad("epoch 必须非 0");
            }

            if (instanceId == 0u)
            {
                throw Bad("instanceId（= NetId）必须非 0");
            }

            if (streamVersion == 0u)
            {
                throw Bad("streamVersion 必须非 0（非 0 才是有效流代次）");
            }
        }

        private static void WriteHeader(PMNetWriter writer, PMR4PayloadKind kind,
                                        uint epoch, uint instanceId, uint streamVersion)
        {
            writer.WriteTag(FKind, PMWireType.Varint);
            writer.WriteVarint((ulong)(byte)kind);
            writer.WriteTag(FVersion, PMWireType.Varint);
            writer.WriteVarint((ulong)ProtocolVersion);
            writer.WriteTag(FEpoch, PMWireType.Varint);
            writer.WriteVarint(epoch);
            writer.WriteTag(FInstance, PMWireType.Varint);
            writer.WriteVarint(instanceId);
            writer.WriteTag(FStream, PMWireType.Varint);
            writer.WriteVarint(streamVersion);
        }

        private static byte[] Finish(PMNetWriter writer, string what)
        {
            if (writer.Length > MaxBlobBytes)
            {
                throw Bad(what + " 编码后 " + writer.Length + " 字节超过上限 " + MaxBlobBytes + " 字节");
            }

            return writer.ToArray();
        }

        private static bool TryOpenPayload(byte[] payload, int offset, int count, PMR4PayloadKind expectedKind,
                                           out PMR4OpenPayload open, out string error)
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

            if (count > MaxBlobBytes)
            {
                error = "载荷 " + count + " 字节超过上限 " + MaxBlobBytes + " 字节";
                return false;
            }

            PMR4OpenPayload result = new PMR4OpenPayload();
            result.Reader = new PMNetReader(payload, offset, count);

            int field;
            PMWireType wire;

            try
            {
                // 头部严格顺序：kind(1) → version(2) → epoch(3) → instance(4) → stream(5)。
                // 头部字段号在写侧与读侧都是固定的（1..5），这里逐一按名字校验，
                // 因此头部字段缺失/重复/乱序都会被拒绝。
                if (!result.Reader.ReadTag(out field, out wire) || field != FKind)
                {
                    error = "载荷头部缺少 kind 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "kind 的 wire type 不对"; return false; }
                int kindValue = checked((int)result.Reader.ReadVarint());
                if (kindValue < 1 || kindValue > 4)
                {
                    error = "未知载荷种类 " + kindValue;
                    return false;
                }

                PMR4PayloadKind kind = (PMR4PayloadKind)(byte)kindValue;
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

                if (!result.Reader.ReadTag(out field, out wire) || field != FInstance)
                {
                    error = "载荷头部缺少 instanceId 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "instanceId 的 wire type 不对"; return false; }
                result.InstanceId = checked((uint)result.Reader.ReadVarint());

                if (!result.Reader.ReadTag(out field, out wire) || field != FStream)
                {
                    error = "载荷头部缺少 streamVersion 字段";
                    return false;
                }

                if (wire != PMWireType.Varint) { error = "streamVersion 的 wire type 不对"; return false; }
                result.StreamVersion = checked((uint)result.Reader.ReadVarint());

                if (result.Epoch == 0u || result.InstanceId == 0u || result.StreamVersion == 0u)
                {
                    error = "载荷身份非法（epoch/instanceId/streamVersion 必须非 0）";
                    return false;
                }

                // 头部之后的第一个字段：读出来交给正文解码器（PMNetReader 没有 seek，不能回退）。
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
        /// 字段顺序校验：字段号**不得倒退**，且除**数组承载字段**（同一 tag 重复出现）外不得重复。
        ///
        /// 为什么不能写成“严格升序”：数组用重复的 message 字段承载
        /// （补充句：输入条目 10 / 活跃层 22 / 事件条目 11），它们必须能连续出现多次。
        /// 契约里的“严格升序且不重复”指的是**标量字段**：任何标量重复都在这里被拒绝
        /// （配合各解码器的 seen 位掩码与字段范围校验，缺字段/未知字段同样被拒）。
        /// </summary>
        private static bool IsFieldOrderAccepted(int lastField, int field, int repeatableField, out string error)
        {
            error = null;

            if (field < lastField)
            {
                error = "字段号不得倒退（上一个 " + lastField + "，本次 " + field + "）";
                return false;
            }

            if (field == lastField && field != repeatableField)
            {
                error = "字段号重复（" + field + " 出现两次；该字段不允许重复）";
                return false;
            }

            return true;
        }

        /// <summary>空输入批的只读表示（诊断用；不参与编码）。</summary>
        public static PMR4MovementInputEntry[] EmptyInputEntries { get { return NoInputEntries; } }

        /// <summary>空事件集合的只读表示（诊断用；不参与编码）。</summary>
        public static PMR4MovementEventRecord[] EmptyEventRecords { get { return NoEventRecords; } }
    }
}
