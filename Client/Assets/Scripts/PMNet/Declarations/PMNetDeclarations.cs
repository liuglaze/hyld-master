using System;
using System.Collections.Generic;
using System.Text;

namespace PMNet
{
    // =====================================================================================
    //  一、声明式标记（业务侧书写，生成器读取）
    //
    //  设计约束（D-R0-48 / T40）：
    //    - Attribute **不拦截调用**，它只是给生成器看的声明。真正的入口由生成代码提供。
    //    - 运行时**不扫反射**，因此这些 Attribute 只被外部 Roslyn 生成器读取；
    //      运行期需要的一切都编译成静态表（见 PMNetRegistry）。
    //    - 因此这里的 Attribute 不参与任何热路径，可以放心用 string 之类的字段。
    // =====================================================================================

    /// <summary>
    /// 标记一个类是可复制的网络对象。
    ///
    /// 被标记的类必须是 `partial`（生成器要往同一个类里补成员），并且必须继承
    /// <see cref="PMNetObject"/>。这两条都在生成器声明阶段强制（D-R0-50）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PMNetworkObjectAttribute : Attribute
    {
        /// <summary>
        /// 稳定类型键。为空时生成器用「命名空间 + 类型名」。
        ///
        /// 只有在**改名但仍需保持线协议兼容**时才需要显式指定它：一旦指定，
        /// 类型 ID 就锚在这个字符串上，改类名不会改变 ID（D-R0-49）。
        /// </summary>
        public string StableKey;

        public PMNetworkObjectAttribute()
        {
        }

        public PMNetworkObjectAttribute(string stableKey)
        {
            StableKey = stableKey;
        }
    }

    /// <summary>
    /// 标记一个字段或属性参与复制。
    ///
    /// 自动属性：普通赋值由编织器自动处理变化与权威标脏，收包 Reader 直接 RawSet。
    /// 字段：Push 模式仍需业务调用 MarkPropertyDirty 或生成的 PMNet_SetXxx。
    /// PushBased=false 时复制层轮询比较；数组原地修改不属于自动 setter 跟踪范围。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class PMReplicatedAttribute : Attribute
    {
        /// <summary>复制条件。默认无条件。</summary>
        public PMCond Condition = PMCond.None;

        /// <summary>
        /// 是否使用 Push Model：自动属性由编织 setter 标脏，普通字段由业务标脏。
        /// false 时在复制调度预算内轮询采样，与各连接基线比较；值未变化不发送。
        /// </summary>
        public bool PushBased = true;

        public PMReplicatedAttribute()
        {
        }

        public PMReplicatedAttribute(PMCond condition)
        {
            Condition = condition;
        }
    }

    /// <summary>
    /// 标记某个复制成员变化后要调用的方法（对应 UE 的 `ReplicatedUsing`）。
    ///
    /// 目标方法必须无参、返回 void。生成器在声明阶段校验它确实对应一个已声明的复制成员，
    /// 否则报错——拼错名字会让「收到更新但不触发表现」变成一个静默缺陷。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PMRepNotifyAttribute : Attribute
    {
        /// <summary>被监听的复制成员名。</summary>
        public readonly string ForMember;

        public PMRepNotifyAttribute(string forMember)
        {
            ForMember = forMember;
        }
    }

    /// <summary>
    /// 指定该成员使用的量化器（浮点定点化）。0 表示不量化。
    ///
    /// 量化在**属性层**做（float → 定点 int），再交给底层字节编码搬运，
    /// 因此不改变线格式的选择（见计划 §3.9.5 / D3）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class PMQuantizedAttribute : Attribute
    {
        /// <summary>量化器 ID；0 保留为「不量化」。</summary>
        public readonly ushort QuantizerId;

        public PMQuantizedAttribute(ushort quantizerId)
        {
            QuantizerId = quantizerId;
        }
    }

    // =====================================================================================
    //  二、描述符（生成器产物，运行期只读）
    // =====================================================================================

    /// <summary>
    /// 单个复制属性的描述符。
    ///
    /// **与 R0 契约 §3.5 的一处有意偏差**：契约里写的是 `byte[] ChangeMaskBits`，
    /// 实现改成 `MaskOffset + MaskBitCount`（在对象级变更掩码里的位区间）。
    /// 理由：每个属性一个 `byte[]` 意味着每个对象类型 N 次分配，而且运行时还要先读
    /// 那段字节再判定——正是复制热路径上最不该出现的东西。位区间是等价的表达，
    /// 且与 <see cref="PMReplicationDescriptor.ChangeMaskBitCount"/> 天然对齐。
    /// </summary>
    public struct PMPropertyDescriptor
    {
        /// <summary>属性 ID（生成器按稳定键分配的 16 位 ID，D-R0-49）。</summary>
        public ushort PropertyId;

        /// <summary>复制条件（D-R0-14 的 8 项之一）。</summary>
        public PMCond Condition;

        /// <summary>在对象变更掩码里的起始位。</summary>
        public ushort MaskOffset;

        /// <summary>在对象变更掩码里占用的位数（当前恒为 1；保留多位是为了将来放枚举/位域属性）。</summary>
        public ushort MaskBitCount;

        /// <summary>量化器 ID；0 = 不量化。</summary>
        public ushort QuantizerId;

        /// <summary>RepNotify 方法 ID；0 = 无。</summary>
        public ushort OnRepMethodId;

        /// <summary>写出该属性的当前值。</summary>
        public PMPropertyWriter Writer;

        /// <summary>读入并赋值该属性。</summary>
        public PMPropertyReader Reader;

        /// <summary>是否参与 Push 式标脏。</summary>
        public bool PushBased;

        /// <summary>成员名（诊断用）。</summary>
        public string MemberName;

        /// <summary>生成的访问器名（`PMNet_Set<Xxx>`），供业务"赋值即标脏"。</summary>
        public string SetterName;
    }

    /// <summary>写出某属性当前值的委托（由生成代码提供）。</summary>
    public delegate void PMPropertyWriter(PMNetObject target, PMNetWriter writer);

    /// <summary>读入某属性值的委托（由生成代码提供）。</summary>
    public delegate void PMPropertyReader(PMNetObject target, PMNetReader reader);

    /// <summary>
    /// 一个类的复制描述符集合。
    /// </summary>
    public sealed class PMReplicationDescriptor
    {
        /// <summary>类型 ID（32 位；与 M04 的 `PMNetObject.ClassId` 同源）。</summary>
        public uint ClassId;

        /// <summary>属性表，按 <see cref="PMPropertyDescriptor.PropertyId"/> 升序。</summary>
        public PMPropertyDescriptor[] Properties;

        /// <summary>对象变更掩码的总位数（= 各属性位区间之和）。</summary>
        public int ChangeMaskBitCount;

        /// <summary>是否含条件属性（D-R0-14）。</summary>
        public bool HasConditionalMask;

        /// <summary>本类的协议摘要（属性名+条件+量化器+类型签名的稳定哈希）。</summary>
        public uint ProtocolHash;

        /// <summary>类型名（诊断用）。</summary>
        public string TypeName;
    }

    /// <summary>
    /// 一条 RPC 的描述符。
    /// </summary>
    public struct PMRpcDescriptor
    {
        /// <summary>RPC ID（生成器按稳定键分配的 16 位 ID）。</summary>
        public ushort RpcId;

        /// <summary>方向（复用 M05 已有的 <see cref="PMRpcKind"/>；契约 §3.4 的 `PMRpcDirection` 与其同义）。</summary>
        public PMRpcKind Direction;

        /// <summary>是否可靠。</summary>
        public bool IsReliable;

        /// <summary>校验档位（D-R0-45）。</summary>
        public PMRpcValidator Validator;

        /// <summary>
        /// 参数布局哈希。用于协议比对（D-R0-46）：两端不一致 ⇒ 协议不兼容，
        /// 服务端断连 / 客户端标记不兼容，**不是**"忽略这条 RPC"。
        /// </summary>
        public ushort ParamLayoutId;

        /// <summary>方法名（诊断用）。</summary>
        public string MethodName;
    }

    /// <summary>
    /// 字符串属性的**有界**读写（D-R0-18：复制队列与内存必须有界）。
    ///
    /// 为什么单独抽出来而不是直接调 `PMNetWriter.WriteStringValue` / `PMNetReader.ReadStringValue`：
    /// 那两个是无约束的通用 AP I，任何长度都能进/出。属性复制必须**两端都有上限**——
    /// 只在上限一侧设防没有意义（恶意端可以发一个超长串让对端分配）。
    ///
    /// 上限放在**运行时单点**而不是每类一个生成常量：契约 §8 把它列为待收敛参数，
    /// 收敛时应当只改一处。生成物只调用本类，不重复这个数字。
    ///
    /// 超限的行为：
    /// 写侧 —— 抛 `FormatException`（本地业务错误，应当尽早暴露，而不是静默截断）；
    /// 读侧 —— 抛 `FormatException`（远端数据不可信，必须拒绝而不是照单分配）。
    /// 复制层会把它计入 `ProtocolErrors` 并丢弃该条记录，不会崩。
    /// </summary>
    public static class PMNetString
    {
        /// <summary>UTF-8 字节数上限（契约 §8 的待收敛初值）。</summary>
        public const int MaxBytes = 1024;

        /// <summary>
        /// 长度门（写侧）：超过上限即抛 <see cref="FormatException"/>。
        ///
        /// **为什么要有它**：RPC 调用桩需要"在入队之前"就把超长实参拒掉。
        /// 只靠编码时刻的 <see cref="WriteBounded"/> 不够 —— 编码发生在待发队列被排空的时候，
        /// 那时已经"接受了这次调用"，失败会落在与调用点无关的栈上（且可靠 RPC 不允许静默丢弃）。
        /// 两个入口共用本实现，因此"入队前"与"真正编码时"的判定口径不可能分叉。
        /// </summary>
        /// <param name="value">待检字符串（null / 空串直接放行）。</param>
        /// <param name="what">诊断用来源描述（如 `RPC Fire 的实参 p1`）。</param>
        public static void EnsureWithinLimit(string value, string what)
        {
            if (value == null || value.Length == 0)
            {
                return;
            }

            // 按 UTF-8 字节数判定（不是字符数）—— 上限的语义是"线上的字节数"。
            int bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxBytes)
            {
                throw new FormatException((what == null ? "字符串" : what)
                    + " 超过复制上限：" + bytes + " 字节 > " + MaxBytes + " 字节");
            }
        }

        /// <summary>有界写入一个字符串。</summary>
        public static void WriteBounded(PMNetWriter writer, string value)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            if (value == null || value.Length == 0)
            {
                writer.WriteStringValue(string.Empty);
                return;
            }

            EnsureWithinLimit(value, null);

            writer.WriteStringValue(value);
        }

        /// <summary>有界读入一个字符串。</summary>
        public static string ReadBounded(PMNetReader reader)
        {
            if (reader == null) { throw new ArgumentNullException("reader"); }

            // 先按上限"预检"：PMNetReader 读 length-delimited 时若长度越界会抛，
            // 但我们要在**分配之前**就用明确的上限拒绝，避免一个伪造的巨长前缀先分配再失败。
            int declared = reader.PeekVarintLength();
            if (declared > MaxBytes)
            {
                throw new FormatException("收到的字符串长度 " + declared + " 字节超过上限 " + MaxBytes + " 字节");
            }

            return reader.ReadStringValue();
        }
    }

    /// <summary>
    /// 项目式三态校验结果（对应项目 `NetAccept` / `NetReport` / `NetReject`）。
    ///
    /// 三个取值的**动作差异是契约的一部分**（D-R0-45）：
    /// <list type="bullet">
    ///   <item><see cref="Accept"/>：静默放行，执行实现，**不上报**。</item>
    ///   <item><see cref="Report"/>：上报可观测性，**仍然执行**实现。</item>
    ///   <item><see cref="Reject"/>：上报并**跳过实现**，但**不断连**。</item>
    /// </list>
    /// 注意 `Reject` 与 UE 原生 `Validate` 失败的语义不同：后者会**断开连接**。
    /// 两者互斥（规则 9），不允许在同一个 RPC 上同时声明。
    /// </summary>
    public enum PMRpcValidation : byte
    {
        /// <summary>校验通过：静默放行（对应项目的 `NetAccept`）。</summary>
        Accept = 0,

        /// <summary>上报但继续执行（对应项目的 `NetReport`）。</summary>
        Report = 1,

        /// <summary>上报并跳过实现，但不断连（对应项目的 `NetReject`）。</summary>
        Reject = 2,
    }

    /// <summary>
    /// 校验结果的出站接缝（生成物的接收侧分发会调用它）。
    ///
    /// **为什么要经过这一层**：生成代码必须保持与传输/日志/反外挂上报系统的
    /// 解耦——它只表达"这次校验的结论"，不该知道谁来记日志、谁来决定断连。
    /// 宿主（M05 的接收流程 / R3 的 DS 宿主）在启动期把这些钩子接上即可。
    ///
    /// 未接线时的默认行为：**什么都不做**，但一切都会经过 <see cref="Unhandled"/> 计数，
    /// 从而让"校验结论被产生但没人消费"这件事**可观测**，而不是静默消失。
    /// </summary>
    public static class PMRpcValidationSink
    {
        /// <summary>UE 原生 `_Validate` 返回 false ⇒ 请求断开该连接（生成物不做决定）。</summary>
        public static Action<PMNetObject, ushort, string> OnValidateFailed;

        /// <summary>三态校验产生 `Report` / `Reject` 时的上报入口。</summary>
        public static Action<PMNetObject, ushort, PMRpcValidation, string> OnReported;

        /// <summary>没有任何钩子被接线时，每次上报都会计数（可观测性兜底）。</summary>
        public static long Unhandled;

        /// <summary>原生校验失败：请求断连。</summary>
        public static void NotifyValidateFailed(PMNetObject target, ushort rpcId, string methodName)
        {
            Action<PMNetObject, ushort, string> handler = OnValidateFailed;
            if (handler != null)
            {
                handler(target, rpcId, methodName);
                return;
            }

            Unhandled++;
        }

        /// <summary>三态校验的非 Accept 结论上报。</summary>
        public static void NotifyReported(PMNetObject target, ushort rpcId, PMRpcValidation verdict, string methodName)
        {
            Action<PMNetObject, ushort, PMRpcValidation, string> handler = OnReported;
            if (handler != null)
            {
                handler(target, rpcId, verdict, methodName);
                return;
            }

            Unhandled++;
        }
    }

    /// <summary>
    /// 执行一条 RPC 的委托：从 reader 读出参数、过校验、调用业务的 `_Implementation`。
    /// </summary>
    public delegate void PMRpcInvoker(PMNetObject target, PMNetReader reader);

    /// <summary>
    /// 发送一条 RPC 的委托：把参数写进 writer。
    ///
    /// **这个委托是在生成调用桩的调用点用闭包构造的**，实参被闭包捕获。
    /// 早期设计把实参暂存在对象字段（"实参帧"）再传这个委托，那是错的：
    /// 发送可能被推迟（未接线/排队），而编码发生在推迟之后 ——
    /// 届时实参帧可能已被后续同 RPC 调用覆写，于是发出**错误的参数**。
    /// </summary>
    public delegate void PMRpcWriter(PMNetObject target, PMNetWriter writer);

    /// <summary>一个类的注册项。</summary>
    public sealed class PMNetClassEntry
    {
        /// <summary>类型 ID。</summary>
        public uint ClassId;

        /// <summary>类型名（诊断用）。</summary>
        public string TypeName;

        /// <summary>复制描述符。</summary>
        public PMReplicationDescriptor Rep;

        /// <summary>构造工厂（与 M04 `PMNetWorld.RegisterClass` 对接）。</summary>
        public Func<PMNetObject> Factory;

        /// <summary>本类的 RPC 清单。</summary>
        public PMNetRpcEntry[] Rpcs;
    }

    /// <summary>一条 RPC 的注册项。</summary>
    public sealed class PMNetRpcEntry
    {
        /// <summary>描述符。</summary>
        public PMRpcDescriptor Descriptor;

        /// <summary>归属类型 ID。</summary>
        public uint OwningClassId;

        /// <summary>执行（接收侧）。</summary>
        public PMRpcInvoker Invoke;

        // 这里**没有** Write 字段是刻意的：发送侧的实参由调用桩的闭包捕获，
        // 不存在"从对象上读出参数再编码"的路径（那正是实参帧设计的错误来源）。
    }

    // =====================================================================================
    //  三、运行时注册表（生成代码填充，运行期零反射查询）
    // =====================================================================================

    /// <summary>
    /// 生成产物的运行时注册表。
    ///
    /// **为什么必须由生成代码显式 `Register` 而不是运行期扫反射**（D-R0-48 / T40）：
    /// Unity 2019.4 的 Mono 后端 + IL2CPP 下反射扫描既慢又容易被裁剪，
    /// 而且"哪些类型参与网络"必须是**编译期可知**的，否则打包后才发现漏注册。
    ///
    /// 线程约束：注册只在启动期发生，此后只读；与 M03/M04 一样不做锁。
    /// </summary>
    public static class PMNetRegistry
    {
        private static readonly Dictionary<uint, PMNetClassEntry> _classes = new Dictionary<uint, PMNetClassEntry>(64);

        /// <summary>
        /// RPC 表，键是 **(ClassId, RpcId)** 复合键。
        ///
        /// **为什么必须是复合键**（契约 §2.2）：RPC ID 是 16 位，且**按所属类分桶**
        /// （类内唯一即可，与 UE `FLifetimeProperty` 同口径）。因此「RpcId 全局唯一」不是契约，
        /// 两个类各自声明 `RpcId = 6043` 是完全合法的。若只按 RpcId 建表，
        /// 后注册的类要么被误判为重复、要么覆盖先注册的类 —— 两种结果都是把一次调用
        /// 交给**别的类**的实现（串线），而不是拒绝。
        /// </summary>
        private static readonly Dictionary<long, PMNetRpcEntry> _rpcs = new Dictionary<long, PMNetRpcEntry>(128);

        /// <summary>
        /// 只按 RpcId 的**兼容/诊断**索引。
        ///
        /// 值为 null 是刻意保留的歧义标记：同一 RpcId 出现在多个类里时，
        /// 「按裸 RpcId 查」没有唯一答案，此时查询必须返回 false，而不是任意挑一个
        /// —— 挑错的后果同样是串线。接收路径不使用本索引（它无法表达"发给哪个类"）。
        /// </summary>
        private static readonly Dictionary<ushort, PMNetRpcEntry> _rpcByBareId = new Dictionary<ushort, PMNetRpcEntry>(128);

        private static uint _protocolHash;
        private static bool _sealed;

        /// <summary>把 (ClassId, RpcId) 折成一个字典键。ClassId 32 位 + RpcId 16 位 = 48 位，long 装得下且无碰撞。</summary>
        private static long RpcKey(uint classId, ushort rpcId)
        {
            return ((long)classId << 16) | (long)rpcId;
        }

        /// <summary>全部生成代码贡献的协议摘要（握手期比对用，D-R0-46）。</summary>
        public static uint ProtocolHash { get { return _protocolHash; } }

        /// <summary>是否已完成注册（注册后不应再增删）。</summary>
        public static bool IsSealed { get { return _sealed; } }

        /// <summary>已注册的类数。</summary>
        public static int ClassCount { get { return _classes.Count; } }

        /// <summary>已注册的 RPC 数（按 (ClassId, RpcId) 计数：两个类各有一条同号 RPC 时计 2）。</summary>
        public static int RpcCount { get { return _rpcs.Count; } }

        /// <summary>清空注册表（测试与热重载用）。</summary>
        public static void Reset()
        {
            _classes.Clear();
            _rpcs.Clear();
            _rpcByBareId.Clear();
            _protocolHash = 0u;
            _sealed = false;
        }

        /// <summary>
        /// 注册一个类（连同它的 RPC 清单）。
        ///
        /// **原子性**：先把整份清单校验完，再写入任何一张表。任一条不合法 ⇒ 抛异常，
        /// 且注册表**与调用前逐项相同**（不会留下"类已登记、RPC 只登记了一半"的中间态）。
        /// 中间态比直接失败更危险：它会让查表结果取决于注册顺序，而调用方无从察觉。
        ///
        /// ClassId 重复、类内 RpcId 重复、RPC 的归属类与 ClassId 不一致都抛异常：
        /// 这些都说明生成器产出了冲突 ID（D-R0-49），静默覆盖会让类型/方法悄悄映射错位。
        /// 注意**跨类**的 RpcId 相同不是错误（契约 §2.2：RPC ID 按类分桶）。
        /// </summary>
        public static void RegisterClass(PMNetClassEntry entry)
        {
            if (entry == null) { throw new ArgumentNullException("entry"); }
            if (entry.ClassId == 0u) { throw new ArgumentException("ClassId 0 保留为无效值", "entry"); }
            if (_sealed) { throw new InvalidOperationException("注册已封板，不能再注册 ClassId=" + entry.ClassId); }
            if (_classes.ContainsKey(entry.ClassId))
            {
                throw new InvalidOperationException("ClassId " + entry.ClassId + " 被重复注册"
                    + "（已有 " + _classes[entry.ClassId].TypeName + "，新来 " + entry.TypeName + "）");
            }

            PMNetRpcEntry[] rpcs = entry.Rpcs;

            // ── 校验阶段：只读，不写任何表 ────────────────────────────────────────────
            if (rpcs != null)
            {
                // 类内 RpcId 必须唯一（这是硬失败：同一类的两条 RPC 抢同一个 ID 时，
                // 接收侧无法判断该执行哪一个）。
                HashSet<ushort> seenInClass = new HashSet<ushort>();

                for (int i = 0; i < rpcs.Length; i++)
                {
                    PMNetRpcEntry rpc = rpcs[i];
                    if (rpc == null)
                    {
                        throw new ArgumentException("类 " + entry.TypeName + " 的 RPC 表第 " + i + " 项为 null", "entry");
                    }

                    if (rpc.Descriptor.RpcId == 0)
                    {
                        throw new ArgumentException("RpcId 0 保留为无效值（类 " + entry.TypeName + "）", "entry");
                    }

                    if (rpc.OwningClassId != entry.ClassId)
                    {
                        throw new ArgumentException("RPC " + rpc.Descriptor.MethodName + " 的归属类 "
                            + rpc.OwningClassId + " 与注册的 ClassId " + entry.ClassId + " 不一致"
                            + "（RPC 必须登记在它声明的那个类之下）", "entry");
                    }

                    if (_rpcs.ContainsKey(RpcKey(entry.ClassId, rpc.Descriptor.RpcId)))
                    {
                        throw new InvalidOperationException("RPC 已通过 RegisterRpc 登记，不能再次随类登记：ClassId="
                            + entry.ClassId + " RpcId=" + rpc.Descriptor.RpcId);
                    }

                    if (!seenInClass.Add(rpc.Descriptor.RpcId))
                    {
                        throw new InvalidOperationException("RpcId " + rpc.Descriptor.RpcId + " 在类 "
                            + entry.TypeName + " 内被重复注册（类内 RPC ID 必须唯一）");
                    }
                }
            }

            // ── 提交阶段：以下写入不可能失败（前面的校验已排除全部可预期冲突）──────────
            _classes.Add(entry.ClassId, entry);

            if (rpcs != null)
            {
                for (int i = 0; i < rpcs.Length; i++)
                {
                    RegisterRpc(rpcs[i]);
                }
            }
        }

        /// <summary>
        /// 注册一条 RPC。键是 **(OwningClassId, RpcId)**：不同类的同号 RPC 各自合法，
        /// 同一类的同号 RPC 仍然抛异常（类内重复是声明错误，不能静默择一）。
        /// </summary>
        public static void RegisterRpc(PMNetRpcEntry entry)
        {
            if (entry == null) { throw new ArgumentNullException("entry"); }
            if (entry.Descriptor.RpcId == 0)
            {
                throw new ArgumentException("RpcId 0 保留为无效值", "entry");
            }

            if (entry.OwningClassId == 0u)
            {
                throw new ArgumentException("RPC " + entry.Descriptor.MethodName
                    + " 没有归属类（OwningClassId=0）：RPC 必须挂在某个类之下才能分桶", "entry");
            }

            if (_sealed) { throw new InvalidOperationException("注册已封板，不能再注册 RpcId=" + entry.Descriptor.RpcId); }

            long key = RpcKey(entry.OwningClassId, entry.Descriptor.RpcId);
            if (_rpcs.ContainsKey(key))
            {
                throw new InvalidOperationException("RpcId " + entry.Descriptor.RpcId + " 在 ClassId "
                    + entry.OwningClassId + " 上被重复注册（已有 " + _rpcs[key].Descriptor.MethodName
                    + "，新来 " + entry.Descriptor.MethodName + "）");
            }

            _rpcs.Add(key, entry);

            // 兼容索引：同一个裸 RpcId 再次出现 ⇒ 置 null 标记歧义（此后 TryGetRpc(ushort) 返回 false）。
            if (_rpcByBareId.ContainsKey(entry.Descriptor.RpcId))
            {
                _rpcByBareId[entry.Descriptor.RpcId] = null;
            }
            else
            {
                _rpcByBareId.Add(entry.Descriptor.RpcId, entry);
            }
        }

        /// <summary>封板并记录整体协议摘要。必须在所有 RegisterClass 之后调用一次。</summary>
        public static void Seal(uint protocolHash)
        {
            _protocolHash = protocolHash;
            _sealed = true;
        }

        /// <summary>按 ClassId 查类。</summary>
        public static bool TryGetClass(uint classId, out PMNetClassEntry entry)
        {
            return _classes.TryGetValue(classId, out entry);
        }

        /// <summary>
        /// 按 **(ClassId, RpcId)** 精确查询。
        ///
        /// 这是接收侧的**唯一**查询方式：RpcId 只在类内唯一，所以定位一条 RPC 必须
        /// 先知道目标对象的 ClassId。只按 RpcId 查会让不同类的同号 RPC 互相串线。
        /// </summary>
        public static bool TryGetRpc(uint classId, ushort rpcId, out PMNetRpcEntry entry)
        {
            return _rpcs.TryGetValue(RpcKey(classId, rpcId), out entry);
        }

        /// <summary>
        /// 只按 RpcId 的兼容查询：**仅当整个注册表里该 RpcId 唯一**时才返回 true。
        ///
        /// 歧义（不同类各自声明了同一个 RpcId）⇒ 返回 false，而不是任意挑一个 ——
        /// 挑错的后果是把调用交给另一个类的实现。诊断/单类程序集用它；
        /// 接收路径不使用它（它无法表达"发给哪个类"）。
        /// </summary>
        public static bool TryGetRpc(ushort rpcId, out PMNetRpcEntry entry)
        {
            PMNetRpcEntry found;
            if (!_rpcByBareId.TryGetValue(rpcId, out found) || found == null)
            {
                entry = null;
                return false;
            }

            entry = found;
            return true;
        }
    }
}
