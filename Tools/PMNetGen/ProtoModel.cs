using System.Collections.Generic;

namespace PMNetGen
{
    /// <summary>proto 标量类型（生成器内部使用，与运行时的 PMNet.PMScalarType 解耦）。</summary>
    internal enum ProtoScalar
    {
        None = 0,
        Double,
        Float,
        Int32,
        Int64,
        UInt32,
        UInt64,
        SInt32,
        SInt64,
        Fixed32,
        Fixed64,
        SFixed32,
        SFixed64,
        Bool,
        String,
        Bytes,
    }

    /// <summary>字段的类别。</summary>
    internal enum ProtoFieldKind
    {
        Scalar = 0,
        Enum = 1,
        Message = 2,
    }

    internal sealed class ProtoEnumValue
    {
        public string Name;
        public int Number;
    }

    internal sealed class ProtoEnum
    {
        public string Name;
        public readonly List<ProtoEnumValue> Values = new List<ProtoEnumValue>();
    }

    internal sealed class ProtoField
    {
        /// <summary>proto 中的字段名（原样，例如 battle_net_sim_config、battleInfo）。</summary>
        public string Name;

        /// <summary>字段号。</summary>
        public int Number;

        /// <summary>是否 repeated。</summary>
        public bool Repeated;

        /// <summary>proto 中写的类型名（原样）。</summary>
        public string TypeName;

        /// <summary>解析后的类别。</summary>
        public ProtoFieldKind Kind;

        /// <summary>Kind == Scalar 时有效。</summary>
        public ProtoScalar Scalar;

        /// <summary>
        /// 是否 packed。仅对「可 pack 的 repeated 标量」有意义。
        /// proto3 默认 packed = true；可 pack 类型为数值型与 bool/enum，不含 string/bytes/message。
        /// </summary>
        public bool Packed;

        /// <summary>生成的 C# 成员名（protoc 风格 PascalCase）。</summary>
        public string CsName;

        /// <summary>生成的 C# 类型名（含 PM 前缀）。</summary>
        public string CsType;

        /// <summary>是否可 pack（数值/bool/enum）。</summary>
        public bool IsPackable
        {
            get
            {
                if (Kind == ProtoFieldKind.Enum)
                {
                    return true;
                }

                if (Kind != ProtoFieldKind.Scalar)
                {
                    return false;
                }

                return Scalar != ProtoScalar.String && Scalar != ProtoScalar.Bytes;
            }
        }
    }

    internal sealed class ProtoMessage
    {
        public string Name;
        public readonly List<ProtoField> Fields = new List<ProtoField>();

        /// <summary>生成的 C# 类型名（含 PM 前缀）。</summary>
        public string CsType
        {
            get { return "PM" + Name; }
        }
    }

    /// <summary>解析后的 proto 文件模型。</summary>
    internal sealed class ProtoFile
    {
        public string Syntax = "proto3";
        public string Package = string.Empty;
        public readonly List<ProtoEnum> Enums = new List<ProtoEnum>();
        public readonly List<ProtoMessage> Messages = new List<ProtoMessage>();

        public ProtoEnum FindEnum(string name)
        {
            for (int i = 0; i < Enums.Count; i++)
            {
                if (Enums[i].Name == name)
                {
                    return Enums[i];
                }
            }

            return null;
        }

        public ProtoMessage FindMessage(string name)
        {
            for (int i = 0; i < Messages.Count; i++)
            {
                if (Messages[i].Name == name)
                {
                    return Messages[i];
                }
            }

            return null;
        }

        /// <summary>把标量关键字映射为 ProtoScalar；不是标量则返回 None。</summary>
        public static ProtoScalar ParseScalarKeyword(string typeName)
        {
            switch (typeName)
            {
                case "double": return ProtoScalar.Double;
                case "float": return ProtoScalar.Float;
                case "int32": return ProtoScalar.Int32;
                case "int64": return ProtoScalar.Int64;
                case "uint32": return ProtoScalar.UInt32;
                case "uint64": return ProtoScalar.UInt64;
                case "sint32": return ProtoScalar.SInt32;
                case "sint64": return ProtoScalar.SInt64;
                case "fixed32": return ProtoScalar.Fixed32;
                case "fixed64": return ProtoScalar.Fixed64;
                case "sfixed32": return ProtoScalar.SFixed32;
                case "sfixed64": return ProtoScalar.SFixed64;
                case "bool": return ProtoScalar.Bool;
                case "string": return ProtoScalar.String;
                case "bytes": return ProtoScalar.Bytes;
                default: return ProtoScalar.None;
            }
        }
    }
}
