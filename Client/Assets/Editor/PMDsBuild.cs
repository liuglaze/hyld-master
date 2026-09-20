using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// HyldDS（局内专用服务器）构建入口。
///
/// 背景与取向（对应计划 §2.8、§3.8）：
///   Unity 2019.4 没有 Windows 的 Dedicated Server 构建目标，也没有 UNITY_SERVER 宏，
///   因此 DS 只能打包成常规 StandaloneWindows64，靠启动参数 -server 在运行时判定身份。
///
/// 由此推出一个关键结论：**DS 构建与客户端构建的代码完全相同**。
/// 本脚本唯一做的事，是让 DS 产物
///   1) 只包含一张空引导场景（不加载任何客户端 UI/大厅场景），
///   2) 输出到独立的 HyldDS 目录，并附一个启动脚本。
/// 也就是说，直接用客户端构建产物加 -server 启动，同样会进入 DS 模式。
///
/// 用法：
///   编辑器菜单   Build / Build HyldDS (Windows Headless)
///   命令行       Unity.exe -quit -batchmode -nographics -projectPath <Client>
///                -executeMethod PMDsBuild.BuildWindowsHeadlessDs
///
/// 为什么这里不定义自定义宏（如 PM_DEDICATED_SERVER）：
///   保持 DS 与客户端二进制一致，才能在同一台机器上用同一个产物分别跑 DS 与客户端做联调。
///   若将来确实需要按编译期裁剪客户端代码，再引入自定义宏即可（届时 DS 产物会与客户端分叉）。
///
/// R4-C / C1 接线：构建前先调 PMNet.UnityEditor.PMBattleContentBuild.PrepareForBuild()，
/// 确保 Resources/PMNet 的三个正式内容产物存在且通过校验；失败即中止构建（不允许假成功）。
/// 该调用包在 #if UNITY_EDITOR 里，原因见调用点注释。
/// </summary>
public static class PMDsBuild
{
    /// <summary>DS 引导场景路径（项目内相对 Assets 的路径）。</summary>
    private const string BootScenePath = "Assets/Scenes/PMDsBoot.unity";

    /// <summary>DS 产物目录名（位于工程根目录，与 Client/ 平级，避免被 Unity 导入）。</summary>
    private const string OutputFolderName = "HyldDS";

    /// <summary>产物可执行文件名。</summary>
    private const string ExecutableName = "HyldDS.exe";

    [MenuItem("Build/Build HyldDS (Windows Headless)")]
    public static void BuildWindowsHeadlessDsMenu()
    {
        BuildWindowsHeadlessDs();
    }

    /// <summary>命令行入口（-executeMethod 调用点）。失败时以非 0 退出码结束，便于 CI 判定。</summary>
    public static void BuildWindowsHeadlessDs()
    {
        bool batchMode = IsBatchMode();

        try
        {
            // R4-C / C1：正式内容先就绪（缺资源不允许假成功）。
            // 该调用包在 #if UNITY_EDITOR： Tools/PMUnityGlueCheck 用手写桩件编译本文件且不编 C1 实现；
            // Tools/PMBattleContentBuildCheck 用真实 Unity DLL 且定义 UNITY_EDITOR，会编译校验它。
#if UNITY_EDITOR
            if (!PMNet.UnityEditor.PMBattleContentBuild.PrepareForBuild())
            {
                Debug.LogError("[PMDsBuild] 正式内容未就绪或校验失败，构建中止（缺资源不允许假成功）。");
                Finish(batchMode, 1);
                return;
            }
#endif

            string bootScene = EnsureBootScene();
            string outputPath = Path.Combine(ResolveProjectRoot(), OutputFolderName, ExecutableName);

            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            BuildPlayerOptions options = new BuildPlayerOptions();
            options.scenes = new string[] { bootScene };
            options.locationPathName = outputPath;
            options.target = BuildTarget.StandaloneWindows64;

            Debug.Log("[PMDsBuild] 开始构建 HyldDS");
            Debug.Log("[PMDsBuild] 场景（仅引导场景）= " + bootScene);
            Debug.Log("[PMDsBuild] 输出 = " + outputPath);

            // 先试真正的无头构建（BuildOptions.EnableHeadlessMode）。
            //
            // 背景：官方文档说 2019.4 的 EnableHeadlessMode 「仅 Linux」，
            // 但本机安装目录里四个 Windows variation 全都带了 WindowsPlayerHeadless.exe，
            // 且 UnityEditor.dll 中存在 BuildingForHeadlessPlayer / IsHeadlessMode 符号，
            // 因此这个结论存疑，值得实测。
            //
            // 收益：真无头包不初始化图形设备，体积与启动开销都更小，
            // 而这正好是 T25（单 DS 启动耗时/内存）关心的两个数字。
            //
            // 策略：先试 headless；若抛异常或报告失败，自动回退到 BuildOptions.None
            //（None 时靠运行时 -batchmode -nographics 实现无头）。
            BuildReport report = null;
            bool usedHeadless = false;

            try
            {
                options.options = BuildOptions.EnableHeadlessMode;
                report = BuildPipeline.BuildPlayer(options);
                if (report != null && report.summary.result == BuildResult.Succeeded)
                {
                    usedHeadless = true;
                    Debug.Log("[PMDsBuild] 无头构建成功（BuildOptions.EnableHeadlessMode 在本平台可用）");
                }
                else
                {
                    Debug.LogWarning("[PMDsBuild] EnableHeadlessMode 构建未成功（result="
                        + (report == null ? "null" : report.summary.result.ToString())
                        + "），回退到 BuildOptions.None 重试");
                }
            }
            catch (Exception headlessEx)
            {
                Debug.LogWarning("[PMDsBuild] EnableHeadlessMode 不可用（" + headlessEx.GetType().Name
                    + ": " + headlessEx.Message + "），回退到 BuildOptions.None 重试");
            }

            if (!usedHeadless)
            {
                options.options = BuildOptions.None;
                report = BuildPipeline.BuildPlayer(options);
            }

            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log("[PMDsBuild] 构建成功，大小 = " + summary.totalSize + " 字节，产物 = " + outputPath
                    + "，无头方式=" + (usedHeadless ? "编译期 EnableHeadlessMode" : "运行时 -batchmode -nographics"));
                WriteLauncher(outputDir, usedHeadless);
                Finish(batchMode, 0);
                return;
            }

            Debug.LogError("[PMDsBuild] 构建失败，result=" + summary.result
                + " 错误数=" + summary.totalErrors
                + " 警告数=" + summary.totalWarnings);
            Finish(batchMode, 1);
        }
        catch (Exception e)
        {
            Debug.LogError("[PMDsBuild] 构建异常：" + e);
            Finish(batchMode, 1);
        }
    }

    /// <summary>
    /// 确保 DS 引导场景存在。由 Unity 自己创建（而不是手工写 .unity YAML），
    /// 以免场景格式随版本变化而失效。
    ///
    /// 用 <see cref="NewSceneMode.Additive"/> 而不是 Single：
    ///   Single 会**关掉当前正在编辑的场景**，若用户有未保存改动会弹出保存对话框阻断构建，
    ///   甚至可能丢失工作。Additive 只额外开一张临时场景，保存后立即关掉，不碰用户的场景。
    /// </summary>
    private static string EnsureBootScene()
    {
        string absolute = Path.Combine(Application.dataPath, BootScenePath.Substring("Assets/".Length));
        if (File.Exists(absolute))
        {
            return BootScenePath;
        }

        string dir = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Debug.Log("[PMDsBuild] 引导场景不存在，创建空场景（Additive，不影响当前编辑场景）：" + BootScenePath);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            if (!EditorSceneManager.SaveScene(scene, BootScenePath))
            {
                throw new InvalidOperationException("保存引导场景失败：" + BootScenePath);
            }
        }
        finally
        {
            // 无论保存成败都要关掉这张临时场景，别把用户留在它上面。
            EditorSceneManager.CloseScene(scene, true);
        }

        AssetDatabase.Refresh();
        return BootScenePath;
    }

    /// <summary>
    /// 写一个启动脚本，把参数形态固定下来（谁拉起 DS 都应照此组装命令行）。
    ///
    /// R3-B 补充（契约 §7.3 / §7.4）：脚本现在同时描述两种启动形态，并**不写死任何绝对路径** ——
    /// 可执行文件与日志都用 `%~dp0`（脚本自身所在目录）推导，Lobby/控制地址可用环境变量覆盖。
    /// 引导文件必须由调用方（Lobby）传绝对路径：它是本局密钥的载体，不走命令行内的拼接。
    /// </summary>
    private static void WriteLauncher(string outputDir, bool usedHeadless)
    {
        try
        {
            string bat = Path.Combine(outputDir, "run_ds.bat");

            // -nographics 在真正无头包里是冗余的（不会初始化图形设备），但留着无害；
            // 在回退（BuildOptions.None）的情况下它是必需的，所以两种都带上。
            string[] lines = new string[]
            {
                "@echo off",
                "REM HyldDS 启动模板（由 PMDsBuild 生成，可自由修改默认值）",
                "REM 本次构建的无头方式：" + (usedHeadless ? "编译期 EnableHeadlessMode（真无头包）" : "运行时 -batchmode -nographics"),
                "REM",
                "REM 身份判定：-server 为必需项；其余参数由 Lobby 拉起时下发。",
                "REM",
                "REM == 旧形态（不带 -bootstrap，走 P3' 诊断路由）==",
                "REM    run_ds.bat [dsid] [matchid] [port]",
                "REM",
                "REM == R3-B 新链形态（带 -bootstrap；旧诊断路由不会启动）==",
                "REM    run_ds.bat [dsid] [matchid] [port] <bootstrapAbsPath> [controlHost:port] [smoke]",
                "REM",
                "REM 参数说明：",
                "REM    -bootstrap <绝对路径>   本局引导文件（由 Lobby 临时写后原子 rename 发布）。",
                "REM                            只传路径：控制密钥与玩家票据**不会**出现在命令行上。",
                "REM    -control   <host:port>  Lobby 控制 listener。首版只允许 loopback（缺省 127.0.0.1:7800）。",
                "REM    -server-smoke           显式自动验收：全部名册玩家完成探针后提交 smoke 结果。",
                "REM                            **默认不开**，正常路径不自动胜利（结果由权威玩法调 SubmitResult 提交）。",
                "REM",
                "REM 注意：",
                "REM    * 所有路径都相对本脚本所在目录（%~dp0）推导，模板里不写死任何绝对路径；",
                "REM    * -port 必须每局不同（旧客户端把战斗 UDP 端口硬编码为 7777）；",
                "REM    * 同一端口上不能同时跑旧诊断路由与新链（报文字节格式不同）；",
                "REM    * 日志可用 HYLD_DS_LOGDIR 覆盖，Lobby/控制地址可用 HYLD_DS_LOBBY / HYLD_DS_CONTROL 覆盖。",
                "",
                "set DSID=%~1",
                "set MATCHID=%~2",
                "set PORT=%~3",
                "set BOOTSTRAP=%~4",
                "set CONTROL=%~5",
                "set SMOKEARG=",
                "if \"%DSID%\"==\"\" set DSID=ds-local",
                "if \"%MATCHID%\"==\"\" set MATCHID=match-local",
                "if \"%PORT%\"==\"\" set PORT=7801",
                "if \"%CONTROL%\"==\"\" set CONTROL=%HYLD_DS_CONTROL%",
                "if \"%CONTROL%\"==\"\" set CONTROL=127.0.0.1:7800",
                "if /I \"%~6\"==\"smoke\" set SMOKEARG=-server-smoke",
                "set LOBBY=%HYLD_DS_LOBBY%",
                "if \"%LOBBY%\"==\"\" set LOBBY=127.0.0.1:7778",
                "set LOGDIR=%HYLD_DS_LOGDIR%",
                "if \"%LOGDIR%\"==\"\" set LOGDIR=%~dp0logs",
                "if not exist \"%LOGDIR%\" mkdir \"%LOGDIR%\"",
                "",
                "if not \"%BOOTSTRAP%\"==\"\" goto newchain",
                "",
                "REM ---- 旧形态 ----",
                "\"%~dp0" + ExecutableName + "\" -batchmode -nographics -server ^",
                "  -dsid %DSID% -matchid %MATCHID% ^",
                "  -listen 0.0.0.0 -port %PORT% ^",
                "  -lobby %LOBBY% ^",
                "  -tickrate 30 ^",
                "  -logFile \"%LOGDIR%\\ds_%DSID%.log\"",
                "goto :eof",
                "",
                ":newchain",
                "REM ---- R3-B 新链形态 ----",
                "\"%~dp0" + ExecutableName + "\" -batchmode -nographics -server ^",
                "  -dsid %DSID% -matchid %MATCHID% ^",
                "  -listen 0.0.0.0 -port %PORT% ^",
                "  -bootstrap \"%BOOTSTRAP%\" -control %CONTROL% ^",
                "  -lobby %LOBBY% ^",
                "  -tickrate 30 %SMOKEARG% ^",
                "  -logFile \"%LOGDIR%\\ds_%DSID%.log\"",
                "",
            };

            File.WriteAllLines(bat, lines);
            Debug.Log("[PMDsBuild] 已生成启动脚本：" + bat);
        }
        catch (Exception e)
        {
            // 启动脚本只是便利品，失败不应让构建判定为失败。
            Debug.LogWarning("[PMDsBuild] 生成启动脚本失败（不影响产物）：" + e.Message);
        }
    }

    /// <summary>工程根目录（与 Client/ 平级的那一层）。</summary>
    private static string ResolveProjectRoot()
    {
        // Application.dataPath 形如 <root>/Client/Assets，上两级即工程根。
        DirectoryInfo clientDir = Directory.GetParent(Application.dataPath);
        if (clientDir == null || clientDir.Parent == null)
        {
            throw new InvalidOperationException("无法推导工程根目录，Application.dataPath=" + Application.dataPath);
        }

        return clientDir.Parent.FullName;
    }

    private static bool IsBatchMode()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-batchmode", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>批量模式下必须显式退出，否则 Unity 进程不会结束。</summary>
    private static void Finish(bool batchMode, int exitCode)
    {
        if (batchMode)
        {
            EditorApplication.Exit(exitCode);
        }
        else if (exitCode != 0)
        {
            EditorUtility.DisplayDialog("HyldDS 构建", "构建失败，详见 Console。", "确定");
        }
    }
}
