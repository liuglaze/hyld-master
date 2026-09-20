using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PMNet
{
    /// <summary>
    /// protobuf 兼容的字节流写入器。
    ///
    /// 设计约束：
    /// - 目标平台为 Unity 2019.4 / .NET Standard 2.0（无 Span），以及 .NET 6 服务端，故只使用两者共有的 API。
    /// - 不依赖 Google.Protobuf，不依赖反射，可 AOT 化（决策 D1）。
    /// - 逐字节对齐 protobuf 线格式，保证与现有 protobuf 代码互通（决策 D3）。
    ///
    /// 命名保留 UE/Iris 的 Bit 语义以便后续扩展位打包；P0 阶段只实现字节级 protobuf 兼容原语。
    /// </summary>
    public sealed class PMNetWriter
    {
        private byte[] _buffer;
        private int _length;

        // 嵌套 message 需要先算出长度再写长度前缀，因此需要一份临时子写入器。
        // 用池化避免每次嵌套都分配。
        private Stack<PMNetWriter> _subPool;

        public PMNetWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 16)
            {
                initialCapacity = 16;
            }

            _buffer = new byte[initialCapacity];
            _length = 0;
        }

        /// <summary>已写入的字节数。</summary>
        public int Length
        {
            get { return _length; }
        }

        /// <summary>取底层缓冲区（有效数据为 [0, Length)）。</summary>
        public byte[] GetBuffer()
        {
            return _buffer;
        }

        /// <summary>清空内容，保留容量，便于复用。</summary>
        public void Reset()
        {
            _length = 0;
        }

        /// <summary>把内容复制为新的字节数组。</summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        private void EnsureCapacity(int additional)
        {
            int required = _length + additional;
            if (required <= _buffer.Length)
            {
                return;
            }

            int newSize = _buffer.Length;
            while (newSize < required)
            {
                newSize *= 2;
            }

            byte[] grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }

        // ---------------- 原语 ----------------

        /// <summary>写入字段标签：tag = (fieldNumber &lt;&lt; 3) | wireType。</summary>
        public void WriteTag(int fieldNumber, PMWireType wireType)
        {
            if (fieldNumber <= 0)
            {
                throw new ArgumentOutOfRangeException("fieldNumber", "字段号必须为正数");
            }

            WriteVarint(((ulong)(uint)fieldNumber << 3) | (ulong)(byte)wireType);
        }

        /// <summary>写入无符号可变长整数（protobuf base-128 varint）。</summary>
        public void WriteVarint(ulong value)
        {
            EnsureCapacity(10);
            while (value >= 0x80UL)
            {
                _buffer[_length++] = (byte)((value & 0x7FUL) | 0x80UL);
                value >>= 7;
            }

            _buffer[_length++] = (byte)value;
        }

        /// <summary>
        /// 写入 int32。
        /// 注意：protobuf 对负 int32 做 64 位符号扩展，写出 10 字节 varint，不是 5 字节。
        /// 这是与 Google.Protobuf 逐字节一致的关键点之一。
        /// </summary>
        public void WriteInt32(int value)
        {
            WriteVarint(unchecked((ulong)(long)value));
        }

        /// <summary>写入 int64（负数为 10 字节 varint）。</summary>
        public void WriteInt64(long value)
        {
            WriteVarint(unchecked((ulong)value));
        }

        /// <summary>写入 uint32。</summary>
        public void WriteUInt32(uint value)
        {
            WriteVarint(value);
        }

        /// <summary>写入 uint64。</summary>
        public void WriteUInt64(ulong value)
        {
            WriteVarint(value);
        }

        /// <summary>写入 sint32（zigzag 编码，负数不膨胀到 10 字节）。</summary>
        public void WriteSInt32(int value)
        {
            WriteVarint(ZigZagEncode32(value));
        }

        /// <summary>写入 sint64（zigzag 编码）。</summary>
        public void WriteSInt64(long value)
        {
            WriteVarint(ZigZagEncode64(value));
        }

        /// <summary>写入 sfixed32（fixed32 小端，按位重解释）。</summary>
        public void WriteSFixed32(int value)
        {
            WriteFixed32(unchecked((uint)value));
        }

        /// <summary>写入 sfixed64（fixed64 小端，按位重解释）。</summary>
        public void WriteSFixed64(long value)
        {
            WriteFixed64(unchecked((ulong)value));
        }

        /// <summary>写入 bool（1 字节 varint）。</summary>
        public void WriteBool(bool value)
        {
            EnsureCapacity(1);
            _buffer[_length++] = value ? (byte)1 : (byte)0;
        }

        /// <summary>写入枚举（按 int32 语义编码）。</summary>
        public void WriteEnum(int value)
        {
            WriteInt32(value);
        }

        /// <summary>写入 float（fixed32，小端）。</summary>
        public void WriteFloat(float value)
        {
            FloatIntUnion u = default(FloatIntUnion);
            u.AsFloat = value;
            WriteFixed32(unchecked((uint)u.AsInt));
        }

        /// <summary>写入 double（fixed64，小端）。</summary>
        public void WriteDouble(double value)
        {
            DoubleLongUnion u = default(DoubleLongUnion);
            u.AsDouble = value;
            WriteFixed64(unchecked((ulong)u.AsLong));
        }

        /// <summary>写入 fixed32（小端）。</summary>
        public void WriteFixed32(uint value)
        {
            EnsureCapacity(4);
            _buffer[_length++] = (byte)(value & 0xFF);
            _buffer[_length++] = (byte)((value >> 8) & 0xFF);
            _buffer[_length++] = (byte)((value >> 16) & 0xFF);
            _buffer[_length++] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>写入 fixed64（小端）。</summary>
        public void WriteFixed64(ulong value)
        {
            EnsureCapacity(8);
            for (int i = 0; i < 8; i++)
            {
                _buffer[_length++] = (byte)((value >> (i * 8)) & 0xFF);
            }
        }

        /// <summary>写入 length-delimited 原始字节。</summary>
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            EnsureCapacity(count);
            Buffer.BlockCopy(data, offset, _buffer, _length, count);
            _length += count;
        }

        /// <summary>写入字符串（UTF-8，length-delimited）。调用方负责先写 tag。</summary>
        public void WriteStringValue(string value)
        {
            // 与 Google.Protobuf 一致，统一使用 Encoding.UTF8（含非法代理对的替换行为）。
            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteVarint((ulong)byteCount);
            if (byteCount > 0)
            {
                EnsureCapacity(byteCount);
                Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
                _length += byteCount;
            }
        }

        /// <summary>写入 bytes（length-delimited）。调用方负责先写 tag。</summary>
        public void WriteBytesValue(byte[] value)
        {
            int count = value == null ? 0 : value.Length;
            WriteVarint((ulong)count);
            if (count > 0)
            {
                WriteRawBytes(value, 0, count);
            }
        }

        // ---------------- 嵌套 message ----------------

        /// <summary>
        /// 借出一个临时写入器用于先序列化嵌套 message（子写入器自身还有池，支持多层嵌套）。
        /// </summary>
        public PMNetWriter RentSubWriter()
        {
            if (_subPool == null)
            {
                _subPool = new Stack<PMNetWriter>();
            }

            PMNetWriter sub = _subPool.Count > 0 ? _subPool.Pop() : new PMNetWriter(64);
            sub.Reset();
            return sub;
        }

        /// <summary>
        /// 写入嵌套 message 字段：tag + 长度 + 内容，并把子写入器归还池。
        /// 生成的序列化器用法：
        ///   var s = w.RentSubWriter();
        ///   PMXxxSerializer.Write(s, msg.Xxx);
        ///   w.WriteSubMessage(fieldNumber, s);
        /// </summary>
        public void WriteSubMessage(int fieldNumber, PMNetWriter sub)
        {
            WriteTag(fieldNumber, PMWireType.LengthDelimited);
            WriteVarint((ulong)sub._length);
            if (sub._length > 0)
            {
                WriteRawBytes(sub._buffer, 0, sub._length);
            }

            sub.Reset();
            if (_subPool == null)
            {
                _subPool = new Stack<PMNetWriter>();
            }

            _subPool.Push(sub);
        }

        /// <summary>
        /// float/int 位重解释联合体。避免 BitConverter.GetBytes 的分配，
        /// 也避免依赖 .NET Standard 2.0 不提供的 BitConverter.SingleToInt32Bits。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatIntUnion
        {
            [FieldOffset(0)] public float AsFloat;
            [FieldOffset(0)] public int AsInt;
        }

        /// <summary>double/long 位重解释联合体。</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct DoubleLongUnion
        {
            [FieldOffset(0)] public double AsDouble;
            [FieldOffset(0)] public long AsLong;
        }

        /// <summary>protobuf zigzag 编码（32 位）。</summary>
        private static ulong ZigZagEncode32(int value)
        {
            return (ulong)(uint)((value << 1) ^ (value >> 31));
        }

        /// <summary>protobuf zigzag 编码（64 位）。</summary>
        private static ulong ZigZagEncode64(long value)
        {
            return (ulong)((value << 1) ^ (value >> 63));
        }
    }
}
