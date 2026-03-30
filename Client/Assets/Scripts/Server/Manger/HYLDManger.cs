

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System.Linq;
using Server;
using SocketProto;
using LongZhiJie;
using System.IO;
using Google.Protobuf;
using Logging;

public class HYLDManger : Singleton<HYLDManger>
{
    private TCPServerManger _socketManger;
    private UIBaseManger _uiManger;
    private PingPongManger _pingpongManger;
    private float _nextTraceFlushTime = 0f;
    private const float TraceFlushIntervalSeconds = 1f;
    public UIBaseManger UIBaseManger
    {
        get { return _uiManger; }
    }
    protected override void Awake()
    {
        if (HYLDStaticValue.isNet)
        {
            base.Awake();
            NetConfigValue.ServiceIP = IPManager.GetIP(ADDRESSFAM.IPv4);

            // ★ 日志目录：桌面/HYLDLogs/2026-03-15_14时30分22秒/
            string traceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HYLDLogs");
            string sessionName = DateTime.Now.ToString("yyyy-MM-dd_HH时mm分ss秒");
            string traceDir = Path.Combine(traceRoot, sessionName);
            if (!Directory.Exists(traceDir))
            {
                Directory.CreateDirectory(traceDir);
            }

            Logging.HYLDDebug.TraceSavePath = Path.Combine(traceDir, "runtime.log");
            Logging.HYLDDebug.FrameTraceSavePath = Path.Combine(traceDir, "framesync_full.log");
            Logging.HYLDDebug.InitFrameLogFiles(traceDir);
            Logging.HYLDDebug.Log($"TraceSavePath={Logging.HYLDDebug.TraceSavePath}");
            Logging.HYLDDebug.Log($"FrameTraceSavePath={Logging.HYLDDebug.FrameTraceSavePath}");
            Logging.HYLDDebug.Log($"FrameLogDirectory={Logging.HYLDDebug.FrameLogDirectory}");
            Logging.HYLDDebug.Trace($"[LogChannel] runtime={Logging.HYLDDebug.TraceSavePath}");
            Logging.HYLDDebug.FrameTrace($"[LogChannel] framesync={Logging.HYLDDebug.FrameTraceSavePath}");
            Logging.HYLDDebug.FlushTrace();
            Logging.HYLDDebug.FlushFrameTrace();
            _nextTraceFlushTime = Time.unscaledTime + TraceFlushIntervalSeconds;

            OnInit();
            _socketManger = new TCPServerManger();
            _pingpongManger = new PingPongManger();
            HYLDDebug.Log("test");

            _socketManger.OnInit();
            _pingpongManger.Init();
        }
     
    }
    private void OnEnable()
    {
        
    }
    private void Update()
    {
        if (!HYLDStaticValue.isNet) return;
        if (HYLDStaticValue.是否为连接状态)
        {
            if (pingPongPack != null)
            {
                pingPongPack = null;
                _pingpongManger.OnResponse(Time.time);
            }
            _pingpongManger.Excute();
        }


        if (_uiManger != null && _uiManger.IsInit)
        {
            _uiManger.Excute(Time.deltaTime);
        }

        if (Time.unscaledTime >= _nextTraceFlushTime)
        {
            _nextTraceFlushTime = Time.unscaledTime + TraceFlushIntervalSeconds;
            Logging.HYLDDebug.FlushTrace();
            Logging.HYLDDebug.FlushFrameTrace();
        }
    }
   
    private MainPack pingPongPack = null;
    public void Pong(MainPack pack)
    {
        pingPongPack = pack;
    }
    public void AddBattleReview(MainPack pack)
    {
        Debug.LogError(pack);
        string SavePath = Application.streamingAssetsPath + "/Review/"+ DateTime.Now.ToLocalTime().ToString("yyyyMMddHHmmss") +".txt";
        if (string.IsNullOrEmpty(SavePath))
            return;

        var dir = Path.GetDirectoryName(SavePath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var stream = File.Open(SavePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = pack.ToByteArray();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
    public void GetBattleReview()
    {
        string SavePath = Application.streamingAssetsPath + "/Review.txt";
        if (string.IsNullOrEmpty(SavePath))
            return;

        var dir = Path.GetDirectoryName(SavePath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var stream = File.Open(SavePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] bytes = new byte[stream.Length];
            stream.Read(bytes, 0, (int)stream.Length);
            MainPack pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(bytes, 0, (int)stream.Length);
            Debug.LogError(pack);
        }
    }
    
    public void OnInit()
    {
        Server.RequestManger.RemoveAllRequest();
        _uiManger = GameObject.FindWithTag("UIManger").transform.GetComponent<UIBaseManger>();
        _uiManger.OnInit();

    }

    private void OnDestroy()
    {
        Server.RequestManger.RemoveAllRequest();
        Logging.HYLDDebug.Shutdown();
        if(HYLDStaticValue.isNet && _socketManger != null)
            _socketManger.CloseSocket();
        if (_socketManger != null)
            _socketManger.OnDestroy();
    }

    private void OnApplicationQuit()
    {
        Logging.HYLDDebug.Shutdown();
    }
    public void Send(MainPack pack)
    {
        //5.发送消息
        _socketManger.Send(pack);
    }
    private bool _disconnectHandling = false;
    public void CloseClient()
    {
        if (_disconnectHandling)
        {
            return;
        }

        _disconnectHandling = true;
        HYLDStaticValue.是否为连接状态 = false;

        bool isBattleScene = SceneManager.GetActiveScene().name == "HYLDGame";
        bool hasBattleManager = Manger.BattleManger.Instance != null;
        bool isBattleGameOver = hasBattleManager && Manger.BattleManger.Instance.IsGameOver;

        Logging.HYLDDebug.Trace("[Net][CloseClient] trigger=PingTimeoutOrManual -> close tcp");
        Logging.HYLDDebug.FrameTrace($"[Net][CloseClient] battleScene={SceneManager.GetActiveScene().name} isGameOver={isBattleGameOver}");

        if (_socketManger != null)
        {
            _socketManger.CloseSocket();
        }

        // 战斗场景中的 TCP 连接只承载大厅/补充链路，实时战斗主链路走 UDP。
        // 因此这里不能因为 TCP 断开就提前触发 BeginGameOver 或直接切回开始场景，
        // 否则会抢跑服务端权威的 BattlePushDowmGameOver，表现为“击杀后卡死/停住”。
        if (isBattleScene && hasBattleManager && !isBattleGameOver)
        {
            Logging.HYLDDebug.Trace("[Net][CloseClient] battle scene detected, keep UDP battle alive and wait for authoritative result");
            _disconnectHandling = false;
            return;
        }

        // 避免在非主线程直接切场景（CloseClient 可能由 ping 超时路径触发）
        NetGlobal.Instance.AddAction(() =>
        {
            SceneManager.LoadScene("HuangYeLuanDouStart");
            _disconnectHandling = false;
        });
    }
    public void ShowMessage(string str, bool sync = false)
    {
        _uiManger.ShowMessage(str, sync);
    }
    void OnLevelWasLoaded(int scenelevel)//每次加载完场景调用的函数
    {
        _uiManger = null;
        //Logging.HYLDDebug.LogError(" OnLevelWasLoaded:" + scenelevel);

        if (scenelevel!=2)
        OnInit();
    }
}
