using System;
using System.Collections.Generic;

namespace PMNet.Codegen
{
    /// <summary>
    /// 声明模型的**冻结中间表示（IR）**（R2）。
    ///
    /// 定位：Roslyn 扫描器（`PMDeclScanner`）产出本模型，发射器（`PMDeclEmitter`）与
    /// 校验器（`PMDeclCheck`）消费它。三方只通过本模型通信，不互相引用。
    ///
    /// **本模型里的每个 ID 都已经分配完毕**（由 `PMIdLock` 决定）。
    /// 扫描器只负责"发现声明"，不负责编号；编号是独立的一步，
    /// 这样"重排源文件不改变线协议"才能被单独验证（D-R0-49）。
    /// </summary>
    public sealed class PMDeclModel
    {
        /// <summary>全部被声明的网络类，按 <see cref="PMDeclClass.ClassId"/> 升序。</summary>
        public List<PMDeclClass> Classes = new List<PMDeclClass>();

        /// <summary>整体协议摘要（D-R0-46），由 PMStableHash.GlobalProtocolHash 计算。</summary>
        public uint GlobalProtocolHash;

        /// <summary>生成期诊断：错误会让生成失败（D-R0-50），告警不阻断。</summary>
        public List<string> Errors = new List<string>();

        /// <summary>生成期告警。</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>是否无错误。</summary>
        public bool Ok { get { return Errors.Count == 0; } }
    }

    /// <summary>一个被声明的网络类。</summary>
    public sealed class PMDeclClass
    {
        /// <summary>C# 命名空间（可为空字符串，表示全局命名空间）。</summary>
        public string Namespace = string.Empty;

        /// <summary>类名（不含泛型参数；泛型类不支持复制，声明阶段报错）。</summary>
        public string TypeName = string.Empty;

        /// <summary>稳定键（`CLASS:命名空间.类型名`，或显式指定的 StableKey）。</summary>
        public string StableKey = string.Empty;

        /// <summary>类型 ID（32 位）。</summary>
        public uint ClassId;

        /// <summary>基类名（用于生成 override；必须能推到 PMNetObject）。</summary>
        public string BaseTypeName = string.Empty;

        /// <summary>是否声明为 partial。非 partial 无法生成（要在同一个类里补成员）。</summary>
        public bool IsPartial;

        /// <summary>源文件路径（诊断用）。</summary>
        public string FilePath = string.Empty;

        /// <summary>声明所在行（诊断用）。</summary>
        public int Line;

        /// <summary>复制属性，按 <see cref="PMDeclProperty.PropertyId"/> 升序。</summary>
        public List<PMDeclProperty> Properties = new List<PMDeclProperty>();

        /// <summary>RPC，按 <see cref="PMDeclRpc.RpcId"/> 升序。</summary>
        public List<PMDeclRpc> Rpcs = new List<PMDeclRpc>();

        /// <summary>本类协议摘要。</summary>
        public uint ClassProtocolHash;

        /// <summary>变更掩码总位数。</summary>
        public int ChangeMaskBitCount;

        /// <summary>限定名（命名空间.类型名），用于生成代码里的类型引用。</summary>
        public string QualifiedName
        {
            get
            {
                return string.IsNullOrEmpty(Namespace) ? TypeName : Namespace + "." + TypeName;
            }
        }
    }

    /// <summary>一个被声明的复制成员。</summary>
    public sealed class PMDeclProperty
    {
        /// <summary>成员名。</summary>
        public string MemberName = string.Empty;

        /// <summary>声明类型名（源码书写形式，已去空白）。</summary>
        public string TypeName = string.Empty;

        /// <summary>属性 ID（16 位）。</summary>
        public ushort PropertyId;

        /// <summary>复制条件。</summary>
        public PMCond Condition = PMCond.None;

        /// <summary>是否 Push 式标脏。</summary>
        public bool PushBased = true;

        /// <summary>量化器 ID；0 = 不量化。</summary>
        public ushort QuantizerId;

        /// <summary>在对象变更掩码里的起始位。</summary>
        public ushort MaskOffset;

        /// <summary>在对象变更掩码里占用的位数。</summary>
        public ushort MaskBitCount = 1;

        /// <summary>RepNotify 方法名；null = 无。</summary>
        public string OnRepMethodName;

        /// <summary>是字段（true）还是属性（false）。</summary>
        public bool IsField;

        /// <summary>是否 static：static 成员不能参与复制，声明阶段报错。</summary>
        public bool IsStatic;

        /// <summary>是否 readonly：readonly 字段无法被复制层写入，声明阶段报错。</summary>
        public bool IsReadOnly;

        /// <summary>声明所在行（诊断用）。</summary>
        public int Line;
    }

    /// <summary>一个被声明的 RPC。</summary>
    public sealed class PMDeclRpc
    {
        /// <summary>方法名。</summary>
        public string MethodName = string.Empty;

        /// <summary>RPC ID（16 位）。</summary>
        public ushort RpcId;

        /// <summary>方向。</summary>
        public PMRpcKind Kind = PMRpcKind.Server;

        /// <summary>可靠性。</summary>
        public PMRpcReliability Reliability = PMRpcReliability.Unreliable;

        /// <summary>校验档位。</summary>
        public PMRpcValidator Validator = PMRpcValidator.None;

        /// <summary>参数列表（有序）。</summary>
        public List<PMDeclParam> Parameters = new List<PMDeclParam>();

        /// <summary>参数布局哈希（D-R0-46）。</summary>
        public ushort ParamLayoutId;

        /// <summary>是否 static 方法：static 不能作为 RPC，声明阶段报错。</summary>
        public bool IsStatic;

        /// <summary>返回类型是否为 void：非 void 的 RPC 不支持，声明阶段报错。</summary>
        public bool ReturnsVoid = true;

        /// <summary>声明所在行（诊断用）。</summary>
        public int Line;
    }

    /// <summary>一个 RPC 参数。</summary>
    public sealed class PMDeclParam
    {
        /// <summary>参数名。</summary>
        public string Name = string.Empty;

        /// <summary>参数类型名（源码书写形式，已去空白）。</summary>
        public string TypeName = string.Empty;
    }
}
