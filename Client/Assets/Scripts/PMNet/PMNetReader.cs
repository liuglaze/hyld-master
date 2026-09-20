using System;
using System.Text;

namespace PMNet
{
    /// <summary>
    /// protobuf 兼容的字节流读取器。
    ///
    /// 设计约束与 <see cref="PMNetWriter"/> 对称：
    /// 不依赖 Google.Protobuf、不依赖反射、可在 Unity 2019.4（.NET Standard 2.0，无 Span）与 .NET 6 上编译，
    /// 并逐字节对齐 protobuf 线格式。
    ///
    /// 嵌套 message 通过 <see cref="ReadSubReader"/> 切出子范围，不复制字节。
    /// </summary>
    public sealed class PMNetReader
    {
        private readonly byte[] _buffer;
        private int _pos;
        private readonly int _limit;
        private readonly int _start;

        public PMNetReader(byte[] buffer)
            : this(buffer, 0, buffer == null ? 0 : buffer.Length)
        {
        }

        public PMNetReader(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("count", "读取范围越界");
            }

            _buffer = buffer;
            _start = offset;
            _pos = offset;
            _limit = offset + count;
        }

        /// <summary>是否已读到范围末尾。</summary>
        public bool IsAtEnd
        {
            get { return _pos >= _limit; }
        }

        /// <summary>当前读取位置（相对整个缓冲区）。</summary>
        public int Position
        {
            get { return _pos; }
        }

        /// <summary>本次读取范围内已消费的字节数。</summary>
        public int Consumed
        {
            get { return _pos - _start; }
        }

        // ---------------- 标签与跳读 ----------------

        /// <summary>
        /// 读取下一个字段标签。返回 false 表示已到末尾。
        /// </summary>
        public bool ReadTag(out int fieldNumber, out PMWireType wireType)
        {
            if (_pos >= _limit)
            {
                fieldNumber = 0;
                wireType = PMWireType.Varint;
                return false;
            }

            ulong tag = ReadVarint();
            int rawFieldNumber = (int)(tag >> 3);
            if (rawFieldNumber <= 0)
            {
                throw new FormatException("非法的字段号 0（protobuf 不允许）");
            }

            fieldNumber = rawFieldNumber;
            int rawWireType = (int)(tag & 0x07UL);
            if (rawWireType != 0 && rawWireType != 1 && rawWireType != 2 && rawWireType != 5)
            {
                throw new FormatException("非法的 wire type: " + rawWireType);
            }

            wireType = (PMWireType)rawWireType;
            return true;
        }

        /// <summary>按 wire type 跳过当前字段（用于未知字段）。</summary>
        public void SkipField(PMWireType wireType)
        {
            switch (wireType)
            {
                case PMWireType.Varint:
                    ReadVarint();
                    return;
                case PMWireType.Fixed64:
                    Advance(8);
                    return;
                case PMWireType.LengthDelimited:
                    int length = checked((int)ReadVarint());
                    Advance(length);
                    return;
                case PMWireType.Fixed32:
                    Advance(4);
                    return;
                default:
                    throw new FormatException("无法跳过的 wire type: " + wireType);
            }
        }

        // ---------------- 原语 ----------------

        /// <summary>读取 base-128 varint（最多 10 字节）。</summary>
        public ulong ReadVarint()
        {
            ulong result = 0UL;
            int shift = 0;
            while (shift < 64)
            {
                byte b = ReadRawByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new FormatException("varint 超过 10 字节，数据非法");
        }

        /// <summary>
        /// 读取 int32。protobuf 对负数是 10 字节符号扩展，截断低 32 位即得原值。
        /// </summary>
        public int ReadInt32()
        {
            return unchecked((int)ReadVarint());
        }

        /// <summary>读取 int64。</summary>
        public long ReadInt64()
        {
            return unchecked((long)ReadVarint());
        }

        /// <summary>读取 uint32。</summary>
        public uint ReadUInt32()
        {
            return unchecked((uint)ReadVarint());
        }

        /// <summary>读取 uint64。</summary>
        public ulong ReadUInt64()
        {
            return ReadVarint();
        }

        /// <summary>读取 sint32（zigzag 解码）。</summary>
        public int ReadSInt32()
        {
            uint value = unchecked((uint)ReadVarint());
            return (int)(value >> 1) ^ -(int)(value & 1u);
        }

        /// <summary>读取 sint64（zigzag 解码）。</summary>
        public long ReadSInt64()
        {
            ulong value = ReadVarint();
            return (long)(value >> 1) ^ -(long)(value & 1UL);
        }

        /// <summary>读取 sfixed32。</summary>
        public int ReadSFixed32()
        {
            return unchecked((int)ReadFixed32());
        }

        /// <summary>读取 sfixed64。</summary>
        public long ReadSFixed64()
        {
            return unchecked((long)ReadFixed64());
        }

        /// <summary>读取 bool。</summary>
        public bool ReadBool()
        {
            return ReadVarint() != 0UL;
        }

        /// <summary>读取枚举（按 int32 语义）。</summary>
        public int ReadEnum()
        {
            return ReadInt32();
        }

        /// <summary>读取 float（fixed32，小端）。</summary>
        public float ReadFloat()
        {
            uint bits = ReadFixed32();
            FloatIntUnion u = default(FloatIntUnion);
            u.AsInt = unchecked((int)bits);
            return u.AsFloat;
        }

        /// <summary>读取 double（fixed64，小端）。</summary>
        public double ReadDouble()
        {
            ulong bits = ReadFixed64();
            DoubleLongUnion u = default(DoubleLongUnion);
            u.AsLong = unchecked((long)bits);
            return u.AsDouble;
        }

        /// <summary>读取 fixed32（小端）。</summary>
        public uint ReadFixed32()
        {
            if (_pos + 4 > _limit)
            {
                throw new FormatException("读取 fixed32 时越界");
            }

            uint value = (uint)(_buffer[_pos]
                | (_buffer[_pos + 1] << 8)
                | (_buffer[_pos + 2] << 16)
                | (_buffer[_pos + 3] << 24));
            _pos += 4;
            return value;
        }

        /// <summary>读取 fixed64（小端）。</summary>
        public ulong ReadFixed64()
        {
            if (_pos + 8 > _limit)
            {
                throw new FormatException("读取 fixed64 时越界");
            }

            ulong value = 0UL;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)_buffer[_pos + i] << (i * 8);
            }

            _pos += 8;
            return value;
        }

        /// <summary>读取 length-delimited 字符串（UTF-8）。</summary>
        public string ReadStringValue()
        {
            int length = checked((int)ReadVarint());
            if (length == 0)
            {
                return string.Empty;
            }

            if (_pos + length > _limit)
            {
                throw new FormatException("读取 string 时越界");
            }

            string value = Encoding.UTF8.GetString(_buffer, _pos, length);
            _pos += length;
            return value;
        }

        /// <summary>读取 length-delimited 字节数组（会复制一份）。</summary>
        public byte[] ReadBytesValue()
        {
            int length = checked((int)ReadVarint());
            if (length == 0)
            {
                return EmptyBytes;
            }

            if (_pos + length > _limit)
            {
                throw new FormatException("读取 bytes 时越界");
            }

            byte[] value = new byte[length];
            Buffer.BlockCopy(_buffer, _pos, value, 0, length);
            _pos += length;
            return value;
        }

        /// <summary>
        /// 预读一个 varint 长度前缀，**不推进读取位置**。
        ///
        /// 用途：在"按长度分配"之前做上限检查（见 `PMNetString.ReadBounded`）。
        /// 没有它就只能先 `ReadStringValue()`（内部已分配）再事后校验 ——
        /// 那对"伪造一个巨长前缀"的输入是无防御的。
        /// </summary>
        public int PeekVarintLength()
        {
            int save = _pos;
            try
            {
                return checked((int)ReadVarint());
            }
            finally
            {
                _pos = save;
            }
        }

        /// <summary>
        /// 读取指定长度的原始字节（会复制一份），**不自带长度前缀**。
        ///
        /// 与 <see cref="ReadBytesValue"/> 的区别：后者把长度前缀也读掉，用于 protobuf
        /// 的 length-delimited 字段；本方法用于「长度已由调用方在别处读出」的场景
        /// （例如 M04 的生命周期消息：长度用的是自定义 varint，不是 protobuf tag）。
        /// </summary>
        public byte[] ReadRawBytesCopy(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count", "读取长度为负");
            }

            if (count == 0)
            {
                return EmptyBytes;
            }

            if (_pos + count > _limit)
            {
                throw new FormatException("读取原始字节时越界");
            }

            byte[] value = new byte[count];
            Buffer.BlockCopy(_buffer, _pos, value, 0, count);
            _pos += count;
            return value;
        }

        /// <summary>
        /// 读取嵌套 message：切出一个只覆盖该字段内容的子读取器，不复制字节。
        /// 调用方用子读取器消费完后，父读取器已自动推进到该字段之后。
        /// </summary>
        public PMNetReader ReadSubReader()
        {
            int length = checked((int)ReadVarint());
            if (_pos + length > _limit)
            {
                throw new FormatException("读取嵌套 message 时越界");
            }

            PMNetReader sub = new PMNetReader(_buffer, _pos, length);
            _pos += length;
            return sub;
        }

        // ---------------- 内部 ----------------

        private byte ReadRawByte()
        {
            if (_pos >= _limit)
            {
                throw new FormatException("读取 varint 时越界");
            }

            return _buffer[_pos++];
        }

        private void Advance(int count)
        {
            if (count < 0 || _pos + count > _limit)
            {
                throw new FormatException("跳过字段时越界");
            }

            _pos += count;
        }

        private static readonly byte[] EmptyBytes = new byte[0];

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatIntUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float AsFloat;
            [System.Runtime.InteropServices.FieldOffset(0)] public int AsInt;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct DoubleLongUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public double AsDouble;
            [System.Runtime.InteropServices.FieldOffset(0)] public long AsLong;
        }
    }
}
