using System;
using System.IO;
using Logging;
using PMNet;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 进程启动探针：在任何场景加载之前完成「客户端 / 专用服务器」身份判定与分流。
    ///
    /// 为什么用 <see cref="RuntimeInitializeOnLoadMethodAttribute"/> + SubsystemRegistration：
    ///   1) 它在任何场景加载、任何 MonoBehaviour.Awake 之前执行，因此 HYLDManger 之类的
    ///      场景组件一定能在判定完成之后才做决定；
    ///   2) 它不依赖场景里存在任何对象，无头 DS 因此可以是一张空场景。
    ///
    /// 为什么需要这一层（对应计划 §2.8 的硬约束）：
    ///   Unity 2019.4 既没有 Windows 的 Dedicated Server 构建目标，也没有 UNITY_SERVER 宏，
    ///   客户端与 DS 是同一个二进制，身份只能运行时从命令行参数判定。
    ///
    /// 默认取向：**不带 -server 就是客户端**。解析失败、未初始化等一切异常路径都退回客户端行为，
    /// 因此本类对现有客户端路径是零影响的（判定结果由 Tools/PMNetLaunchCheck 覆盖）。
    /// </summary>
    public static class PMNetBootstrap
    {
        /// <summary>DS 日志根目录名（相对可执行文件所在目录）。</summary>
        private const string DsLogRootName = "HYLDLogs";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnSubsystemRegistration()
        {
            PMNetLaunchOptions options = PMNetRuntime.InitializeFromCurrentProcess();

            HYLDDebug.Log("[PMNetBootstrap] " + PMNetRuntime.Describe());

            for (int i = 0; i < options.Warnings.Count; i++)
            {
                HYLDDebug.LogWarning("[PMNetBootstrap] 启动参数告警：" + options.Warnings[i]);
            }

            if (!PMNetRuntime.IsDedicatedServer)
            {
                // 客户端：保持原有启动流程不变，后续由 HYLDManger 走原有链路。
                return;
            }

            InitializeDedicatedServerLogging(options);

            // 注意：这里**不**创建 DS 宿主。原因见 OnAfterSceneLoad 的注释。
        }

        /// <summary>
        /// 第一张场景加载完成后，才创建 DS 宿主。
        ///
        /// 为何必须放到 AfterSceneLoad，而不是上面的 SubsystemRegistration：
        ///   `PMDsHost.Start()` 会调用 `DontDestroyOnLoad`，而它需要把一个对象
        ///   移进「已存在的常驻场景」。在 SubsystemRegistration 阶段连初始场景都还没加载，
        ///   那时创建的对象会留在随后就被卸载的启动场景里，一加载就一起被销毁。
        ///
        ///   实测症状（本工程真实踩到，2026-09）：DS 日志完整打出
        ///   「===== HyldDS 就绪 =====」「监听=0.0.0.0:7801」，紧接着就是
        ///   「[PMDsHost] 已关闭」与 `UnloadTime`——即端口刚绑上就被销毁，
        ///   外部表现为「日志说就绪了，但 UDP 端口根本连不上」。
        ///
        /// 为什么 SubsystemRegistration 那一步不能一起挪过来：
        ///   身份判定必须早于一切场景加载，否则 HYLDManger.Awake 之类的场景组件
        ///   看不到 DedicatedServer 标志，就会把客户端链路初始化起来。
        ///   所以拆成两段：身份判定早、宿主创建晚。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnAfterSceneLoad()
        {
            if (!PMNetRuntime.IsDedicatedServer)
            {
                return;
            }

            if (PMDsHost.Instance != null)
            {
                // 幂等：Unity 可能因域重载/多次调用重复触发。
                return;
            }

            PMDsHost.Start(PMNetRuntime.Options);
        }

        /// <summary>
        /// DS 的日志落点与客户端不同：客户端写在「桌面/HYLDLogs/时间戳」，
        /// 而服务器上通常没有可用的桌面目录，且一局一个进程需要按 DsId 区分。
        /// 因此这里优先用 -logFile 所在目录，其次用可执行文件旁边的 HYLDLogs。
        /// </summary>
        private static void InitializeDedicatedServerLogging(PMNetLaunchOptions options)
        {
            try
            {
                string dir = ResolveDsLogDirectory(options);
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                HYLDDebug.TraceSavePath = Path.Combine(dir, "runtime.log");
                HYLDDebug.FrameTraceSavePath = Path.Combine(dir, "framesync_full.log");
                HYLDDebug.InitFrameLogFiles(dir);
                HYLDDebug.Log("[PMNetBootstrap] DS 日志目录=" + dir);
            }
            catch (Exception e)
            {
                // 日志初始化失败不应阻止 DS 起来：连不上日志也要能把「我起来了」写进控制台。
                HYLDDebug.LogError("[PMNetBootstrap] DS 日志初始化失败：" + e);
            }
        }

        /// <summary>推导 DS 日志目录；失败时返回 null（调用方会退回控制台输出）。</summary>
        private static string ResolveDsLogDirectory(PMNetLaunchOptions options)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH时mm分ss秒");
            string dsSuffix = string.IsNullOrEmpty(options.DsId) ? "ds" : "ds_" + options.DsId;

            if (!string.IsNullOrEmpty(options.LogFile) && options.LogFile != "-")
            {
                string dir = Path.GetDirectoryName(options.LogFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    return dir;
                }
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir))
            {
                return null;
            }

            return Path.Combine(Path.Combine(baseDir, DsLogRootName), dsSuffix + "_" + stamp);
        }
    }
}
