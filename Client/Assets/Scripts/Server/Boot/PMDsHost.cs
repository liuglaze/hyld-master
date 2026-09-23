using System;
using Logging;
using PMNet;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 局内专用服务器（HyldDS）的进程宿主（旧链退役后定稿的轻量形态）。
    ///
    /// <para>
    /// 本类现在只是**新链会话宿主的 wrapper**，职责只有四条：
    ///   1) 只接受 <c>-bootstrap</c>：缺失/非法即明确以非 0 退出，且**不监听任何端口**；
    ///   2) 启动 <see cref="PMDsSessionHost"/>（读引导文件 → 校验局/摘要/端口 → 世界/桥/运动/投射物/战斗接线）；
    ///   3) 每帧唯一驱动 <see cref="PMDsSessionHost.Pump"/>；
    ///   4) 退出请求 / <see cref="OnApplicationQuit"/> / <see cref="OnDestroy"/> 走同一处**幂等**清理。
    /// </para>
    ///
    /// <para>
    /// **已退役**（早期 P3 的无 bootstrap 裸 UDP 诊断分支）：裸 <c>Socket</c> 绑定与接收线程、
    /// <c>PMUdpRouter</c> / <c>PMSingleBattleRegistry</c> 路由、空 tick 与心跳日志、
    /// 诊断 <c>MainPack</c> handler、跨线程日志队列。这些脚手架当时的目的是「证明能收包」，
    /// 现由 <see cref="PMDsSessionHost"/> 的分帧 UDP 端点 + 控制通道取代；同一端口上两套字节格式
    /// （裸 <c>MainPack</c> vs 带长度/序号头的分片帧）无法共存，因此**不再保留运行时选择**，
    /// 也**不会**在新链失败时回退旧路（那等于对同一端口生成第二套权威）。
    /// </para>
    ///
    /// <para>
    /// 创建时机由 <see cref="PMNetBootstrap"/> 固定在 <c>AfterSceneLoad</c>：<see cref="Start"/>
    /// 会调 <c>DontDestroyOnLoad</c>，而它需要常驻场景已存在（见 PMNetBootstrap 的注释）。
    /// 日志一律走 <see cref="HYLDDebug"/>：无头 DS 被强杀时不会执行清理，必须主动 FlushTrace 才落盘。
    /// </para>
    /// </summary>
    public sealed class PMDsHost : MonoBehaviour
    {
        /// <summary>
        /// 当前 DS 宿主。未启动或已销毁时为 null。供启动探针做幂等判定
        /// （Unity 可能因域重载重复触发初始化）。
        /// </summary>
        public static PMDsHost Instance { get; private set; }

        /// <summary>本局的新链会话宿主（<see cref="PMDsSessionHost"/>）。未启动或已释放时为 null。</summary>
        public PMDsSessionHost SessionHost { get { return _sessionHost; } }

        private PMDsSessionHost _sessionHost;

        /// <summary>退出请求已发出：此后不再驱动会话（Application.Quit 到进程真正结束之间仍有帧）。</summary>
        private bool _exitRequested;

        /// <summary>清理是否已完成。OnDestroy / OnApplicationQuit 可能先后各来一次，这是幂等门。</summary>
        private bool _shutdown;

        /// <summary>
        /// 启动 DS 宿主：创建常驻对象并接线新链会话。
        /// 只能由 <see cref="PMNetBootstrap"/> 在已确认 DS 身份、且**首张场景已加载完成**后调用。
        /// </summary>
        public static PMDsHost Start(PMNetLaunchOptions options)
        {
            GameObject go = new GameObject("[PMDsHost]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            PMDsHost host = go.AddComponent<PMDsHost>();
            Instance = host;
            host.Initialize(options);
            return host;
        }

        private void Initialize(PMNetLaunchOptions options)
        {
            // 无头 DS 没有前台窗口：若被当成窗口化进程拉起，需要显式要求后台运行。
            Application.runInBackground = true;

            if (options == null)
            {
                Fail("启动参数为 null");
                return;
            }

            // 单一入口契约：DS **只能**由 Lobby 用 -bootstrap 拉起（引导文件承载本局密钥/票据/名册，
            // 且密钥不进命令行）。缺失即明确失败并以非 0 退出，且**不监听任何端口** ——
            // 旧裸 UDP 诊断分支已删除，这里没有可回退的旧路，也不创建第二套权威。
            if (string.IsNullOrEmpty(options.BootstrapPath))
            {
                Fail("缺少 -bootstrap 引导文件路径：HyldDS 必须由 Lobby 编排拉起，拒绝裸 -port 启动");
                return;
            }

            HYLDDebug.Log("[PMDsHost] 检测到 -bootstrap，启动新链会话宿主：dsid="
                          + (options.DsId ?? "<none>")
                          + " matchid=" + (options.MatchId ?? "<none>")
                          + " port=" + options.ListenPort.ToString());

            string error;
            try { _sessionHost = PMDsSessionHost.Start(options, out error); }
            catch (Exception ex)
            {
                Fail("新链启动异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }
            if (_sessionHost == null)
            {
                // 失败语义：明确退出进程，不静默退回旧诊断路径。
                Fail("新链会话宿主启动失败：" + (error ?? "<unknown>"));
                return;
            }

            // 退出请求（含新链内部失败请求）统一走这里：会话宿主只请求，不直接结束进程。
            _sessionHost.ExitRequested = OnSessionExitRequested;
        }

        /// <summary>会话宿主请求退出（正常收尾或内部失败）时调用。幂等：重复请求只生效一次。</summary>
        private void OnSessionExitRequested(int exitCode)
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            HYLDDebug.Log("[PMDsHost] 新链会话请求退出，exitCode=" + exitCode.ToString());
            HYLDDebug.FlushTrace();
            Application.Quit(exitCode);
        }

        /// <summary>启动期明确失败（缺 bootstrap / 会话启动失败）：记错、刷盘、以 1 退出。</summary>
        private void Fail(string reason)
        {
            _exitRequested = true;
            HYLDDebug.LogError("[PMDsHost] " + reason + "（不回退旧诊断路径）");
            HYLDDebug.FlushTrace();
            Application.Quit(1);
        }

        private void Update()
        {
            // 退出请求已发出后**不再** Pump：Application.Quit 到进程真正结束之间仍有若干帧，
            // 继续驱动会在退出途中再吃一批入站（确定性地不做）。
            if (_exitRequested || _shutdown)
            {
                return;
            }

            if (_sessionHost == null)
            {
                return;
            }

            try { _sessionHost.Pump(); }
            catch (Exception ex)
            {
                Fail("新链帧驱动异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private void OnApplicationQuit()
        {
            HYLDDebug.Log("[PMDsHost] OnApplicationQuit 触发（应用正在退出）");
            Shutdown("OnApplicationQuit");
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            HYLDDebug.Log("[PMDsHost] OnDestroy 触发（对象被销毁/场景卸载）");
            Shutdown("OnDestroy");
        }

        /// <summary>
        /// 幂等清理：释放会话宿主 + 静态 Instance 收尾。OnDestroy / OnApplicationQuit 先后来到时，
        /// 第二次是 no-op（<see cref="PMDsSessionHost.Dispose"/> 自身也幂等，这里是双重保险）。
        /// </summary>
        private void Shutdown(string reason)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            if (_sessionHost != null)
            {
                HYLDDebug.Log("[PMDsHost] 开始关闭新链会话宿主，reason=" + reason
                              + " 已存活 " + Time.realtimeSinceStartup.ToString("F2") + " 秒");

                // 先摘回调再释放：避免 Dispose 过程中的失败回调再打一次 Quit。
                PMDsSessionHost session = _sessionHost;
                _sessionHost = null;
                session.ExitRequested = null;
                try { session.Dispose(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogError("[PMDsHost] 释放会话异常：" + ex.GetType().Name + " " + ex.Message);
                }
            }

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            HYLDDebug.Log("[PMDsHost] 已关闭（reason=" + reason + "）");
            HYLDDebug.FlushTrace();
        }
    }
}
