/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/23 20:51:46
    Description:     TCP消息
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using Google.Protobuf;

namespace Server
{
    class ByteArray
    {
        //缓冲区
        public byte[] bytes;
        //读写位置
        public int readIdx = 0;
        public int writeIdx = 0;
        //数据长度
        public int Lenth { get { return writeIdx - readIdx; } }

        public ByteArray(byte[] defaultBetys)
        {
            bytes = defaultBetys;
            readIdx = 0;
            writeIdx = defaultBetys.Length;
        }
    }
    public class TCPSocketMessage
	{
        private byte[] _data = new byte[1024];

        private int startIndex;//我们存取了多少个字节的数据在数组里面

        public byte[] Data
        {
            get
            {
                return _data;
            }
        }

        public int StartIndex
        {
            get
            {
                return startIndex;
            }
        }

        public int RemainSize
        {
            get
            {
                return _data.Length - startIndex;
            }
        }

        public void ReadBuffer(int len, Action<MainPack> HandleResponse)
        {
            startIndex += len;
            while (true)
            {
                if (startIndex <= 4) return;
                int count = BitConverter.ToInt32(_data, 0);
                //Logging.HYLDDebug.LogError("消息处理  " + startindex + "  ??>=??  " + (count + 4));
                if ((startIndex-4) >= count )
                {
                    //Logging.HYLDDebug.LogError("消息处理  " + startindex + "  >  " + (count + 4));
                    MainPack pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(_data, 4, count);
                    //Logging.HYLDDebug.LogError(pack);
                    HandleResponse(pack);

                    //此处处理了粘包问题 自己占了位  复制到数据头,从0开始。。
                    Array.Copy(_data, count + 4, _data, 0, startIndex - count - 4);
                    startIndex -= (count + 4);
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
            return head.Concat(data).ToArray();
        }

        public static byte[] PackDataUDP(MainPack pack)
        {
            return pack.ToByteArray();
        }


    }
}