using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using SocketProto;

namespace Server.Tool
{
    class Message
    {
        
        private byte[] buffer = new byte[1024];

        private int startindex;

        public byte[] Buffer
        {
            get
            {
                return buffer;
            }
        }

        public int StartIndex
        {
            get
            {
                return startindex;
            }
        }

        public int Remsize
        {
            get
            {
                return buffer.Length - startindex;
            }
        }
        /// <summary>
        /// 解析数据 已处理粘包，半包问题 大小端问题
        /// </summary>
        /// <param name="len"></param>
        /// <param name="HandleRequest"></param>
        public void ReadBuffer(int len, Action<MainPack> HandleRequest)
        {
            //Logging.Debug.Log(StartIndex + " += " + len  );
            //Logging.Debug.Log(System.Text.Encoding.UTF8.GetString(buffer));
            startindex += len;
            while (true)
            {
                if (startindex <= 4) return;
                //C#中的BitConverter.ToUInt32()方法用于返回从字节数组中指定位置的四个字节转换而来的32位无符号整数。
                int count = BitConverter.ToInt32(buffer, 0);
                //Logging.Debug.Log("消息处理  " + startindex + "  ???>=????  " + (count + 4));
                ///如果包的信息和
                if (startindex >= (count + 4))
                {
                    //Logging.Debug.Log("消息处理  " + startindex + "  >=  " + (count + 4));
                    MainPack pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(buffer, 4, count);
                   // Logging.Debug.Log(pack);
                    HandleRequest(pack);
                    Array.Copy(buffer, count + 4, buffer, 0, startindex - count - 4);
                    startindex -= (count + 4);
                }
                else
                {
                    break;
                }
            }
        }
        
        public static byte[] PackData(MainPack pack)
        {
            byte[] data = pack.ToByteArray();//包体
                
            byte[] head = BitConverter.GetBytes(data.Length);//包头
            if (!BitConverter.IsLittleEndian)///解决大小端问题
            {
                Logging.Debug.Log("小端需要翻转");
                head.Reverse();
            }
            return head.Concat(data).ToArray();
        }

        public static Byte[] PackDataUDP(MainPack pack)
        {
            return pack.ToByteArray();
        }

    }
    class ByteArray
    {
        //默认大小
        const int DEFAULT_SIZE = 1024;
        //初始化大小
        int initSize = 0;

        //缓冲区
        public byte[] bytes;
        //读写位置
        public int ReadIdx = 0;
        public int WriteIdx = 0;
        //数据长度
        public int Length { get { return WriteIdx - ReadIdx; } }
        //容量
        private int capacity = 0;
        //剩余空间
        public int Remain { get { return capacity - WriteIdx; } }

        public ByteArray(byte[] defaultBetys)
        {
            bytes= defaultBetys;
            capacity = defaultBetys.Length;
            initSize = defaultBetys.Length;
            ReadIdx = 0;
            WriteIdx = defaultBetys.Length;
        }
        public ByteArray(int size = DEFAULT_SIZE)
        {
            bytes = new byte[size];
            capacity = size;
            initSize = size;
            ReadIdx = 0;
            WriteIdx = 0;
        }
        /// <summary>
        /// 判断是否需要扩容如果需要就自动扩容为原来的2倍
        /// </summary>
        /// <param name="size"></param>
        public void ReSize(int size)
        {
           
            if (size < Length) return;
            if (size < initSize) return;
            int n = 1;
            while (n < size) n *= 2;
            capacity = n;
            byte[] newbyte = new byte[capacity];
            Array.Copy(bytes, ReadIdx, newbyte, 0, Length);
            bytes = newbyte;
            WriteIdx = Length;
            ReadIdx = 0;
        }
        
    }
}
