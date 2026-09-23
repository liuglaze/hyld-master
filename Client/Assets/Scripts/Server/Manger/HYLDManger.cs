

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
        // 专用服务器进程（由 Lobby 以 -server 拉起）：不初始化客户端链路。
        // 判定来自 PMNetRuntime（启动期参数解析，见 PMNetBootstrap）。
        // 不带 -server 时该值恒为 false，因此客户端路径与改动前逐字一致。
        //
        // 保留 base.Awake() 是为了让 Instance 有效（避免散落各处的 HYLDManger.Instance
        // 调用点空引用）；被跳过的只是 UI / 大厅 TCP / PingPong 这些客户端专属部分。
        if (PMNet.PMNetRuntime.IsDedicatedServer)
        {
            base.Awake();
            Logging.HYLDDebug.Log("[HYLDManger] 专用服务器进程，跳过客户端初始化（UI / 大厅TCP / PingPong）");
            return;
        }

        if (HYLDStaticValue.isNet)
        {
            base.Awake();
            if (!IsSingletonInstance)
            {
                return;
            }

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
        // DS 进程不跑客户端 Update 管线（大厅 PingPong、UI、Trace 刷盘）。
        // 当前 DS 上 `是否为连接状态` 恒为 false，因此这里并不是在修复一个已发生的故障，
        // 而是把「客户端 Update 管线不参与 DS」变成显式约定，避免日后有人在下面新增
        // 一条不依赖该标志的调用（如直接在 Update 里用 _socketManger）而引入空引用。
        // DS 侧的日志刷盘由 PMDsHost 的心跳负责。
        if (PMNet.PMNetRuntime.IsDedicatedServer)
        {
            return;
        }

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

        // 扫描大厅请求超时（计划 P1 / B3）。
        // 只处理「请求-响应」类超时，与下方心跳一起构成客户端侧的网络看护。
        PmRpcClient.Tick(Time.time);

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

    public void OnInit()
    {
        Server.RequestManger.RemoveAllRequest();
        _uiManger = GameObject.FindWithTag("UIManger").transform.GetComponent<UIBaseManger>();
        _uiManger.OnInit();

    }

    private void OnDestroy()
    {
        if (!IsSingletonInstance)
        {
            return;
        }

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
        if (_socketManger == null)
        {
            // DS 进程不建立大厅 TCP 链路。若仍有调用点走到这里，记为告警而不是空引用，
            // 避免一个非关键包把无头进程直接打死。
            Logging.HYLDDebug.LogWarning("[HYLDManger] Send 时大厅 TCP 未初始化（DS 进程或尚未连接），已忽略该包");
            return;
        }

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

        // 旧链退役（契约 §B）：不再读 Manger.BattleManger。断线时改看**新链**客户端宿主的生命周期：
        //   · IsActive            = 新链局内会话仍在跑（局内战斗走 UDP，与大厅 TCP 无关）
        //   · HasLastCombatResult = 已收到并保存**可信终局**（只有通过校验的合法结果才会置位）
        bool newChainActive = PMNet.Unity.PMClientSessionHost.IsActive;
        bool hasTrustworthyTerminal = PMNet.Unity.PMClientSessionHost.HasLastCombatResult;

        Logging.HYLDDebug.Trace("[Net][CloseClient] trigger=PingTimeoutOrManual -> close tcp");
        Logging.HYLDDebug.FrameTrace($"[Net][CloseClient] scene={SceneManager.GetActiveScene().name} newChainActive={newChainActive} terminal={hasTrustworthyTerminal}");

        if (_socketManger != null)
        {
            _socketManger.CloseSocket();
        }

        // 避免在非主线程直接切场景 / 释放新链会话（CloseClient 可能由 TCP 收包线程触发）
        NetGlobal.Instance.AddAction(() =>
        {
            // 新链断线收尾（契约「HYLDManger 断线清新 PMClientSessionHost」）：
            //   · 已拿到可信终局 ⇒ 什么都不停：按新 host 的**正常结束**语义，EndSessionNormally
            //     已把只读终局 HUD 摘成 static 保留，Stop 会把它一起抹掉，所以这里绝不 Stop；
            //   · 否则（尚未终局）⇒ 显式 Stop 收尾，避免留下一个拿不到大厅的孤岛会话。
            if (PMNet.Unity.PMClientSessionHost.IsActive
                && !PMNet.Unity.PMClientSessionHost.HasLastCombatResult)
            {
                PMNet.Unity.PMClientSessionHost.Stop();
            }

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
