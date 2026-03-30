#pragma warning disable 1234
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net.Sockets;
using System;
using Random = UnityEngine.Random;

public class TCPSocket : MonoBehaviour
{
    public static List<string> 被选择的英雄=new List<string>();
    public static List<string> 玩家名 = new List<string>();
    private const string IP = "";//Server.NetConfigValue.ServiceIP;
    private const int PORT = 6666;// Server.NetConfigValue.ServiceTCPPort;

    private Socket clientSocket;
    private TCPSocketMessage msg =new TCPSocketMessage();
    /*单例模式，一个客户端只需要一个socket实例即可*/
    private static TCPSocket _instance;
    public static bool 是否单机 = false;
    public static bool 是否创建 = false;
    public static TCPSocket Instance { get {
        if (_instance == null)
        {
             
            _instance = GameObject.Find("MainGameController").GetComponent<TCPSocket>();
           // GameObject.Find("MainGameController").GetComponent<TCPSocket>().这是挂载单例模式的物体=true;
        }
        return _instance;
    } }
    void Awake()
    {

        if (_instance == null)
        {
            DontDestroyOnLoad(this.gameObject);
            是否创建 = true;
            _instance = this as TCPSocket;
        }
        /*
        else
        {

            Destroy(gameObject);

        }
        */
    }
    public void 连接()
    {
       
        MessageController.sendStringMessage("连接中...", MessageTypes.Wait, 0.1f);
        
        clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            clientSocket.Connect(IP, PORT);
            Init();
            Logging.HYLDDebug.Log("连接成功");
            HYLDStaticValue.是否为连接状态 = true;
            
            //!!!!!!!!!!!!!!!!!!!!!Test

            //StaticValue.selfPhoneNumber = "1599083" +Random.Range(1000, 9999);
            //TCPSocket.Instance.Send(RequestCode.Game, ActionCode.Logins, StaticValue.selfPhoneNumber);
            //!!!!!!!!!!!!!!!!!!!!!Test
        }
        catch (Exception e)
        {
            MessageController.sendStringMessage("抱歉，无法连接到服务器", MessageTypes.Error);
            Logging.HYLDDebug.LogWarning("无法连接到服务器端，请检查您的网络！！" + e);
        }

        Init();
    }
    
    private void Start()
    {
        if (HYLDStaticValue.是否为连接状态 == false)
        {
            连接();
        }
    }
    
    void Init()
    {
        clientSocket.BeginReceive(msg.Data,msg.StartIndex,msg.RemainSize, SocketFlags.None, ReceiveCallback, null);
    }
    private void ReceiveCallback(IAsyncResult ar)
    {
        //Logging.HYLDDebug.Log(clientSocket.Connected);
        
        try
        {
        
            if (clientSocket == null || clientSocket.Connected == false) return;
            
            int count = clientSocket.EndReceive(ar);//len of thing
            Logging.HYLDDebug.Log(count);
            //msg.ReadMessage(count, OnProcessDataCallback);
            msg.ReadMessage(count);
            Init();
        
        }
        
        catch(Exception e)
        {
            print(e);
        }
        
      // Init();
    }
    private void OnProcessDataCallback(OldActionCode actionCode,string data)
    {
        print("data");
        print("OnProcessDataCallback");
        //HandleReponse(actionCode, data);
    }
    //public void SendRequest(RequestCode requestCode, ActionCode actionCode, string data)
    public bool Send(OldRequestCode requestCode, OldActionCode actionCode, string data)
    {
        if (requestCode == OldRequestCode.Room) data = StaticValue.roomId+"#"+StaticValue.roomPlayerSelfId+"#"+data;
        
        print("SendData"+requestCode.ToString()+"SendData="+actionCode.ToString()+"SendData:"+data);
        byte[] bytes = msg.PackData(requestCode, actionCode, data);
        try
        {
            clientSocket.Send(bytes);
            return true;
        }
        catch (Exception e)
        {
            MessageController.sendStringMessage("发送失败",MessageTypes.Error);
            Logging.HYLDDebug.LogWarning("Send失败：" + e);
            return false;
        }
       
            
    }

    public void close()
    {
        try
        {
            clientSocket.Close();
        }catch(Exception e)
        {
            Logging.HYLDDebug.LogWarning("无法关闭跟服务器端的连接！！" + e);
        }
    }
    
    public void OnDestroy()
    {
        print("OnDestroy");
        close();
    }
    
}
