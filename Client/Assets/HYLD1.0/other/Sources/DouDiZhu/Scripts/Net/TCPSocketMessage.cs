using System.Collections;
using System.Collections.Generic;

using System.Text.RegularExpressions;
using UnityEngine;
using System;
using System.Text;
using System.Linq;
public class TCPSocketMessage : MonoBehaviour
{
    
    private byte[] data = new byte[1024];
    private int startIndex = 0;//我们存取了多少个字节的数据在数组里面

    //private RequestManager requestManager = new RequestManager();
     public byte[] Data
    {
        get { return data; }
    }
    public int StartIndex
    {
        get { return startIndex; }
    }
    public int RemainSize
    {
        get { return data.Length - startIndex; }
    }
    /// <summary>
    /// 解析数据或者叫做读取数据
    /// </summary>
    public void ReadMessage(int newDataAmount)//, Action<ActionCode, string> processDataCallback)
    {
        startIndex += newDataAmount;
        while (true)
        {
            if (startIndex <= 2) return;
            short count = BitConverter.ToInt16(data, 0);
            //Logging.HYLDDebug.LogError(data);
            if ((startIndex - 2) >= count)
            {
                //后期需要的话再对RequestCode进行拓展
                OldRequestCode requestCode = (OldRequestCode)(data[2]);
                
                
                OldActionCode actionCode = (OldActionCode)(data[3]);
                string s = Encoding.UTF8.GetString(data, 4, count - 2);
                D.p("ReadMessage :"+s);
                RequestManager.request(requestCode, actionCode, s);
                
                
                
                //此处处理了粘包问题 自己占了位  复制到数据头,从0开始。。
                Array.Copy(data, count + 2, data, 0, startIndex - 2 - count);
                startIndex -= (count + 2);
            }
            else
            {
                break;
            }
        }
    }
    public byte[] PackData(OldRequestCode requestData,OldActionCode actionCode, string data)
    {
        byte[] requestCodeBytes = BitConverter.GetBytes((byte) requestData);
        requestCodeBytes[1] = (byte)actionCode;
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        short dataAmount = short.Parse((2 + dataBytes.Length).ToString());
        byte[] dataAmountBytes = BitConverter.GetBytes(dataAmount);
        return (dataAmountBytes.Concat(requestCodeBytes).Concat(dataBytes)).ToArray<byte>();
    }
}
