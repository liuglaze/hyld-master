// PMNet 编织便利层 —— 独立 Editor-only 接线（契约：Docs/plans/net-rpc-weaving-contract.md §4）。
//
// ============================================================================================
//  这一层做什么
// ============================================================================================
//  1) **编辑器编译路径**：订阅 `CompilationPipeline.assemblyCompilationFinished`，
//     只对 **Assembly-CSharp.dll** 这一个程序集，在"程序集已写盘、域重载之前"调用冻结 CLI
//     `Tools/PMNetWeaver` 把它编织掉；编译有 error 时**不处理**（契约 §4 原文）。
//  2) **Player 打包路径**：实现 `IPostBuildPlayerScriptDLLs`，在"托管脚本 DLL 已生成、
//     IL2CPP/Mono 转换之前"处理 BuildReport 给出的脚本 DLL 路径（契约 §4 原文）。
//     2019.4 真实存在的接口与签名已逐项核过（见 Docs/plans/_rpc_weaving_editor.md 的 API 证据段）。
//  3) **生成元数据新鲜度**：源声明变化时以 debounce + 防重入的方式跑外部
//     `PMNetGen --decl-check / --decl-gen`，避免"拿着过时发送签名去编织"（契约 §4 末段）。
//     先 check、只有**逐字节不一致**才 gen；生成物本身不参与指纹，因此不会自激循环。
//  4) **Play/Build 失败闭合**：漏编织不允许跑（本层只做"接线闭合"；真正让运行期拒绝未编织程序集的
//     是生成器侧的 `PMNet_RequireRpcWeave` guard，属于另一组工作）。
//
// ============================================================================================
//  这一层**不**做什么（诚实边界）
// ============================================================================================
//  · 不改已加载的程序集：磁盘编织完成后**不会**声称"内存里也修好了"。手动 Repair 之后必须脚本重载，
//    本层用 SessionState 的 PendingReload 标记把这条边界变成一个真实的 Play 门禁。
//  · 不猜缓存目录：目标路径只来自回调参数（`assemblyCompilationFinished` 的 assemblyPath /
//    BuildReport），或来自 `CompilationPipeline.GetAssemblies` 的 `outputPath`。
//  · 不动第三方 DLL：目标文件名必须**恰好**等于 `Assembly-CSharp.dll`（前缀匹配会误伤
//    `Assembly-CSharp-Editor.dll` / `Assembly-CSharp-firstpass.dll`）。
//  · 不启动、不终止用户进程：只终止本层自己启动的子进程树（见 PMNetToolProcess.cs）。
//  · 不承诺"首次编译自动恢复"：hook 只有在脚本域加载成功之后才存在，首编译若已损坏则无从运行；
//    能救的路径是菜单（本 asmdef 不引用 Assembly-CSharp，因此 Assembly-CSharp 编译失败时它仍可加载）
//    与外部命令（`Tools/PMNetGen` 是独立 net8.0 进程，不依赖任何 Unity 程序集）。
//
// ============================================================================================
//  fail closed（没有"过渡态休眠"这条路）
// ============================================================================================
//  编织格式 v1 已在生成器侧落地：每个含 RPC 的生成类都会发出 `internal static int PMNet_GetRpcWeaveVersion()`，
//  并配 `PMNet_RequireRpcWeave()`（未编织时 new / 注册即抛）。本层用生成物里的
//  `int PMNet_GetRpcWeaveVersion(` 作为"源侧是否还有编织契约"的判据（读**真实生成物文本**，跳过注释行）：
//    · 找不到该符号 ⇒ 契约缺失 ⇒ **fail closed**：不编织、拦 Play/Build，并给出可执行恢复入口
//      （菜单刷新生成元数据 → Unity 重编译 → 本回调下一轮正常编织）；**绝不**返回"放行/休眠"。
//    · 找到 ⇒ 全部严格 fail closed（编织失败 / 自检失败 / 无法验证都拦）。
//  为什么不允许"缺失时放行"：生成物缺失本身就是"这个程序集不会被编织"的证据；
//  放行等于把未编织的程序集交给运行期 guard 去炸，失败点被推迟到运行期、信息更差。
//
//  自动元数据刷新（"便利"）与 Play/Build 门禁（"硬门"）是两件事：
//    · 自动刷新可以关（它只是省手工步骤）；
//    · Play/Build 门禁**没有开关** —— 未编织的 Player/Play 会在运行期被 guard 拒绝，
//      因此构建期/进入期 fail closed 才是唯一诚实的口径。
//
//  纯判据（文件名匹配、Player 目标收集、工具输入内容指纹、契约探针、指纹算法）都在
//  `PMNetWeavingPolicy.cs`：那份文件不含 Unity API，因此有独立的纯 BCL 回归
//  （Tools/PMNetWeavingEditorTest），本文件只负责"取值 + 调工具 + 记状态"。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace PMNet.Weaving.Editor
{
    /// <summary>编织便利层的主接线（Editor-only asmdef：PMNet.Weaving.Editor）。</summary>
    [InitializeOnLoad]
    public static class PMNetWeavingEditor
    {
        // ------------------------------------------------------------------ 目标与工具（契约冻结名）
        /// <summary>需要编织的程序集名（当前 runtime 没有 asmdef，只有它含 RPC）。</summary>
        public const string TargetAssemblyName = "Assembly-CSharp";

        /// <summary>需要编织的程序集文件名（精确名，不做前缀匹配）。</summary>
        public const string TargetAssemblyFileName = "Assembly-CSharp.dll";

        private const string WeaverProjectRelative = "Tools/PMNetWeaver/PMNetWeaver.csproj";
        private const string WeaverOutputDirRelative = "Tools/PMNetWeaver/editor-tool";
        private const string WeaverAssemblyFileName = "PMNetWeaver.dll";

        private const string GenProjectRelative = "Tools/PMNetGen/PMNetGen.csproj";
        private const string GenOutputDirRelative = "Tools/PMNetGen/editor-tool";
        private const string GenAssemblyFileName = "PMNetGen.dll";

        // ------------------------------------------------------------------ 声明源与生成物（对齐 Client/Assets/AGENTS.md §7 的真实入口）
        private const string DeclSourceRootRelative = "Client/Assets/Scripts/PMR3";
        private const string DeclOutDirRelative = "Client/Assets/Scripts/PMR3/Generated";
        private const string DeclIdLockRelative = "Docs/plans/pmnet-r3-ids.json";

        /// <summary>额外一起探测的生成目录（PMNet 核心的生成物也放在这里）。</summary>
        private const string ExtraGeneratedDirRelative = "Client/Assets/Scripts/PMNet/Generated";

        /// <summary>工具新鲜度：除工具工程目录之外，还要一起看的显式输入（相对仓库根）。</summary>
        private static readonly string[] WeaverExtraInputs = new string[] { "Directory.Build.targets" };

        /// <summary>
        /// PMNetGen 的语义输入不止它自己的工程目录：<c>PMDeclModel</c>（IR + ID 锁）、
        /// 被链接进 IR 的 PMNet 核心（Client/Assets/Scripts/PMNet，排除 Generated/），以及仓库级
        /// Directory.Build.targets。漏掉任何一项都会变成"依赖变了却继续跑旧工具"。
        /// </summary>
        private static readonly string[] GenExtraInputs = new string[]
        {
            "Tools/PMDeclModel",
            "Client/Assets/Scripts/PMNet",
            "Directory.Build.targets"
        };

        private static readonly string[] WeaverInputExcludedDirs = new string[] { "bin", "obj", "editor-tool" };
        private static readonly string[] GenInputExcludedDirs = new string[] { "bin", "obj", "editor-tool", "Generated" };

        // ------------------------------------------------------------------ 预置参数
        private const double MetadataDebounceSeconds = 1.0;
        private const int WeaverTimeoutMs = 120000;
        private const int ToolBuildTimeoutMs = 300000;
        private const int MetadataToolTimeoutMs = 120000;
        private const int ToolLogBudgetChars = 6000;
        private const string LogPrefix = "[PMNetWeaving] ";

        // ------------------------------------------------------------------ 持久状态键
        // 注意：**没有** GateEnabled 之类的门禁开关（见文件头"fail closed"段）。
        private const string AutoMetadataRefreshPrefKey = "PMNetWeaving.AutoMetadataRefresh";
        private const string DotnetPathPrefKey = "PMNetWeaving.DotnetPath";

        private const string LastTargetAssemblyPathKey = "PMNetWeaving.TargetAssemblyPath";
        private const string WeaveStateKey = "PMNetWeaving.WeaveState";
        private const string WeaveStateMessageKey = "PMNetWeaving.WeaveStateMessage";
        private const string MetadataStateKey = "PMNetWeaving.MetadataState";
        private const string MetadataStateMessageKey = "PMNetWeaving.MetadataStateMessage";
        private const string PendingReloadKey = "PMNetWeaving.PendingReload";
        private const string RefreshPendingKey = "PMNetWeaving.RefreshPending";
        private const string HandledFingerprintKey = "PMNetWeaving.HandledFingerprint";
        private const string AttemptedFingerprintKey = "PMNetWeaving.AttemptedFingerprint";
        private const string BannerShownKey = "PMNetWeaving.BannerShown";

        // ------------------------------------------------------------------ debounce 运行态
        private static bool _editorUpdateHooked;
        private static double _refreshDueAt;
        private static bool _metadataBusy;
        private static bool _generatedDuringGate;

        /// <summary>编织状态（写入 SessionState，跨域重载保留）。</summary>
        private enum WeaveState
        {
            Unknown = 0,
            Ok = 1,
            Failed = 2,
            SkippedNoContract = 3,
            SkippedCompileErrors = 4
        }

        /// <summary>生成元数据状态。</summary>
        private enum MetadataState
        {
            Unknown = 0,
            InSync = 1,
            Regenerated = 2,
            Unresolved = 3,

            /// <summary>上一次核对仍在进行中：无法确认同步 ⇒ 门禁必须按"未同步"处理，不能返回旧 InSync。</summary>
            Busy = 4
        }

        // ============================================================================================
        //  启动接线
        // ============================================================================================
        static PMNetWeavingEditor()
        {
            // 新的脚本域 == 已经重载过：磁盘上编织过的映像此刻已经（或即将）进入内存。
            SessionState.SetBool(PendingReloadKey, false);

            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // 上一个域在 debounce 窗口里被重载打断了，这里续上（否则那次源变更永远不刷新）。
            if (SessionState.GetBool(RefreshPendingKey, false))
            {
                ScheduleMetadataRefresh();
            }

            LogStartupBanner();
        }

        private static void LogStartupBanner()
        {
            if (SessionState.GetBool(BannerShownKey, false))
            {
                return;
            }

            SessionState.SetBool(BannerShownKey, true);
            Debug.Log(LogPrefix + "接线已加载（独立 Editor asmdef，零 Assembly-CSharp 引用）。"
                + " 目标程序集=" + TargetAssemblyFileName
                + "；编织契约（源侧 " + PMNetWeavingPolicy.WeaveFormatProbeToken + "）="
                + (WeaveContractPresent() ? "存在（严格 fail closed）" : "**缺失**（fail closed：会拦 Play/Build，请先刷新生成元数据）")
                + "；Play/Build 门禁=始终硬门（无开关）"
                + "；工具输出=" + WeaverOutputDirRelative);
            Debug.Log(LogPrefix + "菜单：Tools/PMNet/Weaving/*（状态、Repair、Verify、刷新生成元数据、重建工具）。"
                + "注意：本层只改磁盘，**已加载程序集**的生效依赖脚本重载；首次编译没有已加载 hook，不能承诺自动恢复。");
        }

        // ============================================================================================
        //  编辑器编译路径：域重载前编织当前 Assembly-CSharp.dll
        // ============================================================================================
        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (!IsTargetAssemblyPath(assemblyPath))
            {
                return;
            }

            // 回调给的路径就是真实产物路径：记下来给 Play 门禁复用（不猜 Library 缓存位置）。
            assemblyPath = NormalizeEditorAssemblyPath(assemblyPath);
            if (string.IsNullOrEmpty(assemblyPath))
            {
                SetWeaveState(WeaveState.Failed, "无法解释编译回调的程序集路径。");
                return;
            }
            SessionState.SetString(LastTargetAssemblyPathKey, assemblyPath);

            // 生成元数据：源集合指纹变化才 debounce 刷新；生成物不参与指纹 ⇒ 不自激循环。
            ScheduleMetadataRefreshIfFingerprintChanged();

            if (HasCompileErrors(messages))
            {
                // 契约 §4：编译有 error 不得处理。这里的"不处理"是**不写程序集**，不是"什么都不记"。
                SetWeaveState(WeaveState.SkippedCompileErrors, "上一轮编译存在 error：按契约不编织该程序集。");
                return;
            }

            if (!WeaveContractPresent())
            {
                // fail closed：源侧没有编织契约（生成物缺 PMNet_GetRpcWeaveVersion）⇒ 这个程序集不会/不能
                // 被编织。这里既不编织也不放行，只把状态记为失败，并把"可恢复"的路径排进 debounce 队列
                // （生成元数据一旦被重新生成，Unity 会重编译，本回调下一轮就能正常编织）。
                SetWeaveState(WeaveState.Failed,
                    "生成物里找不到 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                    + "）：编织契约缺失，按 fail closed 处理（不编织、拦 Play/Build）；"
                    + "请用菜单刷新生成元数据后等 Unity 重新编译。");
                Debug.LogError(LogPrefix + "编织契约缺失（fail closed）：生成物 " + DeclOutDirRelative
                    + " / " + ExtraGeneratedDirRelative + " 里没有 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                    + "）。已排入生成元数据刷新；恢复后 Unity 重编译会自动编织。");
                ScheduleMetadataRefreshIfFingerprintChanged();
                return;
            }

            PMNetToolResult result = WeaveAssembly(assemblyPath, true);
            if (result.Success)
            {
                SetWeaveState(WeaveState.Ok, "已编织 " + Path.GetFileName(assemblyPath) + "（RPC 计数见工具输出）。");
            }
            else
            {
                SetWeaveState(WeaveState.Failed, "编织失败：" + result.ShortReason());
                Debug.LogError(LogPrefix + "自动编织失败：" + result.ShortReason() + "\n" + DescribeResult(result));
            }
        }

        private static bool IsTargetAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
            {
                return false;
            }

            // 精确文件名相等（非 StartsWith）：不然 Assembly-CSharp-Editor.dll / -firstpass 也会被卷进来。
            string fileName = Path.GetFileName(assemblyPath);
            return string.Equals(fileName, TargetAssemblyFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEditorAssemblyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string project = Directory.GetParent(Application.dataPath).FullName;
                return PMNetWeavingPolicy.ResolveEditorAssemblyPath(path, project);
            }
            catch (Exception ex)
            {
                Debug.LogError(LogPrefix + "程序集路径无法解释：" + path + "：" + ex.Message);
                return null;
            }
        }

        private static bool HasCompileErrors(CompilerMessage[] messages)
        {
            if (messages == null)
            {
                return false;
            }

            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].type == CompilerMessageType.Error)
                {
                    return true;
                }
            }

            return false;
        }

        // ============================================================================================
        //  编织调用
        // ============================================================================================
        private static PMNetToolResult WeaveAssembly(string assemblyPath, bool requireRpcs)
        {
            string toolDll = EnsureToolBuilt(WeaverProjectRelative, WeaverOutputDirRelative, WeaverAssemblyFileName, false, WeaverExtraInputs, WeaverInputExcludedDirs);
            if (string.IsNullOrEmpty(toolDll))
            {
                PMNetToolResult missing = new PMNetToolResult();
                missing.Failure = "PMNetWeaver 工具不可用（未构建成功或项目不存在）：" + WeaverProjectRelative;
                return missing;
            }

            List<string> args = new List<string>();
            args.Add(toolDll);
            args.Add("--weave");
            args.Add(assemblyPath);
            AddReferenceDirs(args, assemblyPath);
            if (requireRpcs)
            {
                // 格式已落地 ⇒ 该程序集必然含 RPC；非空断言能抓"编织了错文件/工具什么都没找到"。
                args.Add("--require-rpcs");
            }

            PMNetToolResult result = PMNetToolProcess.Run(ResolveDotnet(), args, GetRepoRoot(), WeaverTimeoutMs);
            LogToolRun("weave", result);
            return result;
        }

        private static PMNetToolResult CheckAssembly(string assemblyPath, bool requireRpcs)
        {
            string toolDll = EnsureToolBuilt(WeaverProjectRelative, WeaverOutputDirRelative, WeaverAssemblyFileName, false, WeaverExtraInputs, WeaverInputExcludedDirs);
            if (string.IsNullOrEmpty(toolDll))
            {
                PMNetToolResult missing = new PMNetToolResult();
                missing.Failure = "PMNetWeaver 工具不可用（未构建成功或项目不存在）：" + WeaverProjectRelative;
                return missing;
            }

            List<string> args = new List<string>();
            args.Add(toolDll);
            args.Add("--check");
            args.Add(assemblyPath);
            AddReferenceDirs(args, assemblyPath);
            if (requireRpcs)
            {
                args.Add("--require-rpcs");
            }

            PMNetToolResult result = PMNetToolProcess.Run(ResolveDotnet(), args, GetRepoRoot(), WeaverTimeoutMs);
            LogToolRun("check", result);
            return result;
        }

        /// <summary>
        /// 引用目录只给**真实存在且来源可解释**的几个：
        ///   · 被处理程序集自己所在目录（编辑器路径 = Library/ScriptAssemblies，Player 路径 = 打包产物目录）；
        ///   · Unity 托管程序集目录（`EditorApplication.applicationContentsPath` + Managed）；
        ///   · Unity 自带的 netstandard2.0 ref 目录（存在才给）。
        /// 不给任何"猜"的路径，也不给第三方目录。
        /// </summary>
        private static void AddReferenceDirs(List<string> args, string assemblyPath)
        {
            List<string> dirs = new List<string>();

            string ownDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(ownDir) && Directory.Exists(ownDir))
            {
                dirs.Add(ownDir);
            }

            string contents = null;
            try
            {
                contents = EditorApplication.applicationContentsPath;
            }
            catch (Exception)
            {
            }

            if (!string.IsNullOrEmpty(contents))
            {
                string managed = Path.Combine(contents, "Managed");
                if (Directory.Exists(managed))
                {
                    dirs.Add(managed);
                }

                string netstandardRef = Path.Combine(Path.Combine(Path.Combine(contents, "NetStandard"), "ref"), "2.0.0");
                if (Directory.Exists(netstandardRef))
                {
                    dirs.Add(netstandardRef);
                }
            }

            for (int i = 0; i < dirs.Count; i++)
            {
                args.Add("--reference-dir");
                args.Add(dirs[i]);
            }
        }

        // ============================================================================================
        //  Play 门禁（漏编织不允许跑）
        // ============================================================================================
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }

            string reason;
            if (CanEnterPlay(out reason))
            {
                return;
            }

            Debug.LogError(LogPrefix + "拒绝进入 Play：" + reason
                + "（菜单 Tools/PMNet/Weaving 可查看状态、Repair 或刷新生成元数据）");
            try
            {
                EditorApplication.isPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.LogError(LogPrefix + "取消失败（请手动退出 Play）：" + ex.Message);
            }

            ShowDialog("PMNet 编织门禁", reason + "\n\n菜单：Tools/PMNet/Weaving");
        }

        private static bool CanEnterPlay(out string reason)
        {
            reason = null;

            // Play/Build 门禁**没有开关**：未编织的程序集在运行期会被 PMNet_RequireRpcWeave 拒绝，
            // 让"关掉门禁就能 Play"存在只会把确定性失败推迟到运行期（见文件头"fail closed"段）。
            if (!WeaveContractPresent())
            {
                // 契约缺失本身就是"这个程序集不会被编织"的证据 ⇒ fail closed（不再有休眠旁路）。
                reason = "生成物里找不到 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                    + "）：编织契约缺失，无法确认 Assembly-CSharp 已被编织；"
                    + "请用菜单 Tools/PMNet/Weaving/Refresh Generated Metadata 恢复后等 Unity 重新编译。";
                ScheduleMetadataRefreshIfFingerprintChanged();
                return false;
            }

            if (EditorApplication.isCompiling)
            {
                reason = "脚本正在编译，无法验证 Assembly-CSharp 的编织状态。";
                return false;
            }

            // 元数据核对必须在 PendingReload 判定之前：刚重新生成过源时要做的是 Refresh + 等重编译，
            // 而不是让用户去点"Request Script Reload"（此刻内存里还是旧源码编出来的程序集）。
            _generatedDuringGate = false;
            MetadataState metadata = EnsureMetadataVerified(true);
            if (metadata == MetadataState.Regenerated || _generatedDuringGate)
            {
                // 源生成物刚被改写：必须让 Unity 重新导入并重编译，本次 Play 直接取消。
                AssetDatabase.Refresh();
                reason = "生成元数据刚刚被重新生成（" + SessionState.GetString(MetadataStateMessageKey, string.Empty)
                    + "），Unity 会重新编译；请等编译完成后**再按一次 Play**。";
                return false;
            }

            if (metadata != MetadataState.InSync)
            {
                reason = "生成元数据未能确认同步（" + metadata + "："
                    + SessionState.GetString(MetadataStateMessageKey, string.Empty) + "）。";
                return false;
            }

            if (SessionState.GetBool(PendingReloadKey, false))
            {
                reason = "磁盘上的程序集已经编织，但**当前已加载的程序集仍是旧映像**；请先脚本重载"
                    + "（Tools/PMNet/Weaving/Request Script Reload）。";
                return false;
            }

            string dll = ResolveTargetAssemblyPath();
            if (string.IsNullOrEmpty(dll))
            {
                reason = "拿不到 Assembly-CSharp.dll 的真实路径（回调没给过，CompilationPipeline 也没给出）。";
                return false;
            }

            PMNetToolResult check = CheckAssembly(dll, true);
            if (!check.Success)
            {
                reason = "编织自检失败（" + check.ShortReason() + "）。";
                return false;
            }

            SetWeaveState(WeaveState.Ok, "自检通过：" + dll);
            return true;
        }

        /// <summary>
        /// 目标程序集路径解析：**优先用编译回调给过的真实路径**，其次用
        /// `CompilationPipeline.GetAssemblies` 的 outputPath。两条都不猜缓存目录。
        /// </summary>
        private static string ResolveTargetAssemblyPath()
        {
            string fromCallback = NormalizeEditorAssemblyPath(SessionState.GetString(LastTargetAssemblyPathKey, string.Empty));
            if (!string.IsNullOrEmpty(fromCallback) && File.Exists(fromCallback))
            {
                return fromCallback;
            }

            string fromApi = NormalizeEditorAssemblyPath(FindAssemblyOutputPath(TargetAssemblyName));
            if (!string.IsNullOrEmpty(fromApi) && File.Exists(fromApi))
            {
                return fromApi;
            }

            return null;
        }

        private static string FindAssemblyOutputPath(string assemblyName)
        {
            // Player 先找（Assembly-CSharp 属于 player 程序集集合），Editor 兜底。
            string path = FindAssemblyOutputPath(assemblyName, AssembliesType.Player);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return FindAssemblyOutputPath(assemblyName, AssembliesType.Editor);
        }

        private static string FindAssemblyOutputPath(string assemblyName, AssembliesType type)
        {
            try
            {
                UnityEditor.Compilation.Assembly[] assemblies = CompilationPipeline.GetAssemblies(type);
                if (assemblies == null)
                {
                    return null;
                }

                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i] != null
                        && string.Equals(assemblies[i].name, assemblyName, StringComparison.Ordinal))
                    {
                        return assemblies[i].outputPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + "CompilationPipeline.GetAssemblies(" + type + ") 失败：" + ex.Message);
            }

            return null;
        }

        // ============================================================================================
        //  生成元数据新鲜度（debounce + 防重入；先 check，只有不一致才 gen）
        // ============================================================================================
        private static void ScheduleMetadataRefreshIfFingerprintChanged()
        {
            if (!AutoMetadataRefreshEnabled())
            {
                return;
            }

            string fingerprint = ComputeDeclarationFingerprint();
            if (string.IsNullOrEmpty(fingerprint))
            {
                return;
            }

            // 已处理过（成功）或刚尝试过（失败）的同一份源状态都不重复跑：
            // 否则"每次编译都起一个 dotnet"会变成新的噪音源。
            if (string.Equals(fingerprint, SessionState.GetString(HandledFingerprintKey, string.Empty), StringComparison.Ordinal)
                || string.Equals(fingerprint, SessionState.GetString(AttemptedFingerprintKey, string.Empty), StringComparison.Ordinal))
            {
                return;
            }

            ScheduleMetadataRefresh();
        }

        private static void ScheduleMetadataRefresh()
        {
            SessionState.SetBool(RefreshPendingKey, true);
            _refreshDueAt = EditorApplication.timeSinceStartup + MetadataDebounceSeconds;
            if (_editorUpdateHooked)
            {
                return;
            }

            _editorUpdateHooked = true;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _refreshDueAt)
            {
                return;
            }

            // 编译/导入进行中时不写盘、不 Refresh（重入与"编译中改脚本"都是坑）。
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _refreshDueAt = EditorApplication.timeSinceStartup + MetadataDebounceSeconds;
                return;
            }

            if (_editorUpdateHooked)
            {
                EditorApplication.update -= OnEditorUpdate;
                _editorUpdateHooked = false;
            }

            _generatedDuringGate = false;
            MetadataState state = EnsureMetadataVerified(true);
            if (state == MetadataState.Regenerated && _generatedDuringGate)
            {
                // 生成物已变化：让 Unity 重新导入并重新编译；指纹已记录 ⇒ 下一轮不会再生成。
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// 核对（必要时生成）声明元数据，返回最终状态。
        /// 语义：exit 0 = 一致；exit 2 = 逐字节不一致（唯一的"该生成"证据）；其它退出码 = 工具/声明错误。
        /// </summary>
        private static MetadataState EnsureMetadataVerified(bool allowGenerate)
        {
            if (_metadataBusy)
            {
                // 忙时**不能**返回上一次的 InSync：那等于拿旧结论为新状态背书。
                // 返回 Busy，让门禁按"无法确认同步"处理（fail closed）。
                SetMetadataState(MetadataState.Busy, "上一次生成元数据核对仍在进行中（无法确认同步，按未同步处理）。");
                return MetadataState.Busy;
            }

            _metadataBusy = true;
            try
            {
                string fingerprint = ComputeDeclarationFingerprint();
                if (string.IsNullOrEmpty(fingerprint))
                {
                    SetMetadataState(MetadataState.Unresolved, "声明源目录不存在或不可读：" + DeclSourceRootRelative);
                    return MetadataState.Unresolved;
                }

                SessionState.SetString(AttemptedFingerprintKey, fingerprint);

                PMNetToolResult check = RunGen("--decl-check");
                if (check.Success)
                {
                    SessionState.SetString(HandledFingerprintKey, fingerprint);
                    SetMetadataState(MetadataState.InSync, "decl-check 通过：生成物与声明一致。");
                    return MetadataState.InSync;
                }

                // 退出码语义（可单测的纯判据在 PMNetWeavingPolicy.ClassifyDeclCheck）：
                //   0 = 一致（上面已返回）；2 = 逐字节不一致（**唯一**的"该生成"证据）；其它 = 工具/声明错误。
                // 把"工具没跑起来"当成"需要生成"会在工具坏掉时反复写盘并掩盖真实故障。
                if (PMNetWeavingPolicy.ClassifyDeclCheck(check.ExitCode) != PMNetWeavingPolicy.MetadataProbeOutcome.NeedsGenerate)
                {
                    SetMetadataState(MetadataState.Unresolved,
                        "decl-check 非预期退出码 " + check.ExitCode.ToString(CultureInfo.InvariantCulture)
                        + "（" + check.ShortReason() + "）。");
                    Debug.LogError(LogPrefix + "decl-check 异常：" + DescribeResult(check));
                    return MetadataState.Unresolved;
                }

                if (!allowGenerate)
                {
                    SetMetadataState(MetadataState.Unresolved, "decl-check 报告生成物与声明不一致（未允许生成）。");
                    return MetadataState.Unresolved;
                }

                PMNetToolResult gen = RunGen("--decl-gen");
                if (!gen.Success)
                {
                    SetMetadataState(MetadataState.Unresolved, "decl-gen 失败：" + gen.ShortReason());
                    Debug.LogError(LogPrefix + "decl-gen 失败：" + DescribeResult(gen));
                    return MetadataState.Unresolved;
                }

                // 复验：这一步同时给出"二次生成零 diff"的证据（不一致就该在下面被抓住）。
                PMNetToolResult recheck = RunGen("--decl-check");
                if (!recheck.Success)
                {
                    SetMetadataState(MetadataState.Unresolved, "decl-gen 之后 decl-check 仍不一致：" + recheck.ShortReason());
                    Debug.LogError(LogPrefix + "decl-gen 复验失败：" + DescribeResult(recheck));
                    return MetadataState.Unresolved;
                }

                SessionState.SetString(HandledFingerprintKey, fingerprint);
                _generatedDuringGate = true;

                // 源生成物已被改写 ⇒ 当前已加载的程序集来自**旧**源码：标 PendingReload，
                // 在 Unity 完成重编译/重载之前 Play 必须被拦。
                SessionState.SetBool(PendingReloadKey, true);
                SetMetadataState(MetadataState.Regenerated, "已按声明重新生成元数据并通过复验。");
                Debug.Log(LogPrefix + "已重新生成声明元数据（" + DeclOutDirRelative + " + 锁文件），并通过二次 decl-check；"
                    + "已标记 PendingReload（等 Unity 重编译/重载后才算生效）。");
                return MetadataState.Regenerated;
            }
            finally
            {
                _metadataBusy = false;
            }
        }

        private static PMNetToolResult RunGen(string mode)
        {
            string toolDll = EnsureToolBuilt(GenProjectRelative, GenOutputDirRelative, GenAssemblyFileName, false, GenExtraInputs, GenInputExcludedDirs);
            if (string.IsNullOrEmpty(toolDll))
            {
                PMNetToolResult missing = new PMNetToolResult();
                missing.Failure = "PMNetGen 工具不可用（未构建成功或项目不存在）：" + GenProjectRelative;
                return missing;
            }

            List<string> args = new List<string>();
            args.Add(toolDll);
            args.Add(mode);

            // 源路径按 Client/Assets/AGENTS.md §7 的真实入口给（目录递归，生成器自己跳过 *.g.cs）。
            args.Add(DeclSourceRootRelative);
            args.Add("--out-dir");
            args.Add(DeclOutDirRelative);
            args.Add("--id-lock");
            args.Add(DeclIdLockRelative);

            PMNetToolResult result = PMNetToolProcess.Run(ResolveDotnet(), args, GetRepoRoot(), MetadataToolTimeoutMs);
            LogToolRun(mode, result);
            return result;
        }

        /// <summary>
        /// 声明源集合指纹：与 `PMNetGen` 的输入集合**同规则**（递归 *.cs，排除 *.g.cs / obj / bin），
        /// 取（相对路径、长度、mtime）做 FNV-1a 64 摘要。
        ///
        /// 关键性质：生成物（*.g.cs）**不在**指纹里 ⇒ 生成器写自己的产物不会把指纹改掉，
        /// 因此"生成 → 重编译 → 再生成"的自激循环在结构上被切断。
        /// </summary>
        private static string ComputeDeclarationFingerprint()
        {
            string root = GetAbsolutePath(DeclSourceRootRelative);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return null;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + "枚举声明源失败：" + ex.Message);
                return null;
            }

            // 过滤/摘要规则在 PMNetWeavingPolicy（与 Tools/PMNetGen/DeclScanner.IsScanCandidate 同规则），
            // 并有独立的纯 BCL 回归（Tools/PMNetWeavingEditorTest）。
            return PMNetWeavingPolicy.ComputeDeclarationFingerprint(root, files);
        }

        /// <summary>
        /// 编织格式 v1 是否已在生成物里落地（读真实生成物文本，带 mtime 缓存）。
        /// 只认代码行（跳过注释行），避免注释里提到符号就误判。
        /// </summary>
        private static bool WeaveContractPresent()
        {
            string[] dirs = new string[] { GetAbsolutePath(DeclOutDirRelative), GetAbsolutePath(ExtraGeneratedDirRelative) };
            List<string> candidates = new List<string>();

            for (int d = 0; d < dirs.Length; d++)
            {
                string dir = dirs[d];
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "PMNet*.g.cs", SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length; i++)
                {
                    candidates.Add(files[i]);
                }
            }

            // 缓存判据是**内容**签名（不是长度+mtime）：同长度改写不会改变长度签名，
            // 缓存就会继续返回旧结论而让 fail-closed 静默失效。
            string signature = PMNetWeavingPolicy.ComputeGeneratedProbeSignature(candidates);
            if (_contractProbeSignatureValid && string.Equals(signature, _contractProbeSignature, StringComparison.Ordinal))
            {
                return _contractProbeResult;
            }

            bool found = false;
            for (int i = 0; i < candidates.Count && !found; i++)
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(candidates[i]);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(LogPrefix + "读取生成物失败（按未找到处理）：" + candidates[i] + " -> " + ex.Message);
                }

                found = PMNetWeavingPolicy.TextContainsCodeToken(text, PMNetWeavingPolicy.WeaveFormatProbeToken);
            }

            _contractProbeSignature = signature;
            _contractProbeSignatureValid = true;
            _contractProbeResult = found;
            return found;
        }

        private static string _contractProbeSignature;
        private static bool _contractProbeSignatureValid;
        private static bool _contractProbeResult;

        // ============================================================================================
        //  构建接线
        // ============================================================================================
        /// <summary>构建前 preflight（由 IPreprocessBuildWithReport 调用）。</summary>
        internal static void RunBuildPreflight(BuildReport report)
        {
            if (!WeaveContractPresent())
            {
                // 契约缺失 ⇒ 构建必须失败：不能产出未编织的 Player（也不允许"跳过门禁"）。
                FailBuild("生成物里找不到 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                    + "）：编织契约缺失，无法确认 Player 脚本会被编织；"
                    + "请先用菜单 Tools/PMNet/Weaving/Refresh Generated Metadata 恢复，"
                    + "等 Unity 重新编译（本层会自动重新编织）后重新构建。");
                return;
            }

            // 构建前**只核对、不生成**：如果在 preflight 里改写生成源码，本次构建用的
            // Assembly-CSharp.dll 是**改写前**那一版源码编出来的，而下面又要去 check 它 ——
            // 那等于用"刚生成过"冒充"当前程序集新鲜"。正确做法是让用户先刷新元数据并重编译。
            _generatedDuringGate = false;
            MetadataState metadata = EnsureMetadataVerified(false);
            if (metadata != MetadataState.InSync)
            {
                FailBuild("生成元数据无法确认同步（" + metadata + "）："
                    + SessionState.GetString(MetadataStateMessageKey, string.Empty)
                    + "\n请先用菜单 Tools/PMNet/Weaving/Refresh Generated Metadata 刷新生成元数据，"
                    + "等 Unity 重新编译（本层会自动重新编织）后重新构建。");
                return;   // FailBuild 会抛；这里只是不让"万一异常被吞"时继续往下跑。
            }

            // 工具可用性 + 编辑器侧编织自检：让"工具坏了"在几秒内就失败，而不是等打包完在 Player 回调里炸。
            string dll = ResolveTargetAssemblyPath();
            if (string.IsNullOrEmpty(dll))
            {
                FailBuild("拿不到 Assembly-CSharp.dll 的真实路径，无法在构建前验证编织状态。");
                return;
            }

            PMNetToolResult check = CheckAssembly(dll, true);
            if (!check.Success)
            {
                FailBuild("构建前编织自检失败（" + check.ShortReason() + "）。");
                return;
            }

            Debug.Log(LogPrefix + "构建前检查通过（" + check.CommandLine + "）。");
        }

        /// <summary>
        /// Player 打包路径：托管脚本 DLL 已生成、IL2CPP/Mono 转换之前（IPostBuildPlayerScriptDLLs）。
        /// 找不到目标 DLL 时**明确失败**，绝不"改完包再补"或静默放过。
        /// </summary>
        internal static void WeavePlayerScriptDlls(BuildReport report)
        {
            if (!WeaveContractPresent())
            {
                // Player 路径同理：契约缺失时**不能**“先放过、以后再说”，那是把未编织的 Player 交出去。
                FailBuild("生成物里找不到 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                    + "）：编织契约缺失，拒绝产出未编织的 Player。");
                return;
            }

            List<string> reported = CollectReportFilePaths(report);
            List<string> candidates = PMNetWeavingPolicy.CollectPlayerScriptDllCandidates(reported);
            List<string> existing = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    existing.Add(candidates[i]);
                }
            }

            if (existing.Count == 0)
            {
                // 没有任何**当前 BuildReport 给出的绝对路径**候选 ⇒ 明确失败。
                // 不从 summary.outputPath 推导"最终包目录里的旧 DLL"来补位（那是猜测，不是证据）。
                FailBuild(PMNetWeavingPolicy.DescribeMissingPlayerTarget(reported));
                return;
            }

            for (int i = 0; i < existing.Count; i++)
            {
                PMNetToolResult result = WeaveAssembly(existing[i], true);
                if (!result.Success)
                {
                    FailBuild("Player 脚本 DLL 编织失败：" + existing[i] + " -> " + result.ShortReason());
                    return;   // 任何一个目标没编织成功都不允许继续产出 Player。
                }

                Debug.Log(LogPrefix + "Player 脚本 DLL 已编织：" + existing[i]);
            }
        }

        /// <summary>
        /// 把 BuildReport 的脚本 DLL 清单摊平成字符串列表（**只做取值**；筛选规则在 PMNetWeavingPolicy）。
        ///
        /// 2019.4 的 <c>BuildFile</c> 是**结构体**（不能写 <c>files[i] == null</c>，CS0019），只能判 path 空串。
        /// 刻意不读 <c>report.summary.outputPath</c>：见 policy 的 CollectPlayerScriptDllCandidates 注释
        /// —— 那个推导指向的是最终包目录，此刻可能还是上一轮打包留下的旧 DLL。
        /// </summary>
        private static List<string> CollectReportFilePaths(BuildReport report)
        {
            List<string> paths = new List<string>();
            if (report == null)
            {
                return paths;
            }

            BuildFile[] files = report.files;
            if (files == null)
            {
                return paths;
            }

            for (int i = 0; i < files.Length; i++)
            {
                if (!string.IsNullOrEmpty(files[i].path))
                {
                    paths.Add(files[i].path);
                }
            }

            return paths;
        }

        private static void FailBuild(string message)
        {
            Debug.LogError(LogPrefix + message);
            throw new BuildFailedException(LogPrefix + message);
        }

        // ============================================================================================
        //  工具构建（独立 editor-tool 输出，避免复用源代码目录里的旧 DLL）
        // ============================================================================================
        private static string EnsureToolBuilt(string projectRelative, string outputDirRelative, string assemblyFileName, bool forceRebuild, IList<string> extraInputs, IList<string> excludedDirs)
        {
            string repoRoot = GetRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                Debug.LogError(LogPrefix + "无法推导仓库根目录（Application.dataPath=" + Application.dataPath + "），工具不可用。");
                return null;
            }

            string projectPath = Path.Combine(repoRoot, projectRelative.Replace('/', Path.DirectorySeparatorChar));
            string outputDir = Path.Combine(repoRoot, outputDirRelative.Replace('/', Path.DirectorySeparatorChar));
            string toolDll = Path.Combine(outputDir, assemblyFileName);

            if (!File.Exists(projectPath))
            {
                Debug.LogWarning(LogPrefix + "工具工程不存在：" + projectPath);
                return null;
            }

            if (!forceRebuild && File.Exists(toolDll) && !IsToolStale(projectPath, toolDll, extraInputs, excludedDirs))
            {
                return toolDll;
            }

            // 指纹在**构建前**算（输入集合不会因为构建自己而改变，因为 bin/obj/editor-tool 已排除）。
            string fingerprint = ComputeToolInputFingerprint(projectPath, extraInputs, excludedDirs);

            List<string> args = new List<string>();
            args.Add("build");
            args.Add(projectPath);
            args.Add("-c");
            args.Add("Release");
            args.Add("-o");
            args.Add(outputDir);
            args.Add("--nologo");
            args.Add("-v");
            args.Add("minimal");

            PMNetToolResult build = PMNetToolProcess.Run(ResolveDotnet(), args, repoRoot, ToolBuildTimeoutMs);
            LogToolRun("build " + Path.GetFileName(projectPath), build);

            if (!build.Success || !File.Exists(toolDll))
            {
                Debug.LogError(LogPrefix + "工具构建失败：" + projectPath + " -> " + build.ShortReason());
                return null;
            }

            // 记录本次构建对应的输入**内容**指纹：下一次新鲜度判定按内容比对（与 mtime / 时钟无关）。
            string sidecar = PMNetWeavingPolicy.ToolFingerprintSidecarPath(toolDll);
            if (!PMNetWeavingPolicy.TryWriteToolFingerprint(sidecar, fingerprint))
            {
                Debug.LogWarning(LogPrefix + "工具输入指纹写入失败（下次会多重建一次）：" + sidecar);
            }

            return toolDll;
        }

        /// <summary>工具输入集合的内容指纹；无法计算（枚举失败/空集合）时返回 null，调用方按陈旧处理。</summary>
        private static string ComputeToolInputFingerprint(string projectPath, IList<string> extraInputs, IList<string> excludedDirs)
        {
            string repoRoot = GetRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                return null;
            }

            List<string> absoluteExtras = new List<string>();
            if (extraInputs != null)
            {
                for (int i = 0; i < extraInputs.Count; i++)
                {
                    string absolute = GetAbsolutePath(extraInputs[i]);
                    if (!string.IsNullOrEmpty(absolute))
                    {
                        absoluteExtras.Add(absolute);
                    }
                }
            }

            try
            {
                List<string> files = PMNetWeavingPolicy.CollectToolInputFiles(projectPath, absoluteExtras, excludedDirs);
                if (files.Count == 0)
                {
                    return null;
                }

                return PMNetWeavingPolicy.ComputeFileSetFingerprint(repoRoot, files);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + "工具输入指纹计算失败（按陈旧处理）：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 工具是否陈旧 = 当前输入**内容**指纹 != 产物旁边记录的指纹。
        ///
        /// 为什么不是"源码 mtime 比产物新"：
        ///   · mtime 只比"自己旧产物"新 ⇒ **删除**某个依赖文件时没有任何 mtime 变新，旧工具继续被用；
        ///   · 时钟回拨 / 从版本控制恢复旧文件会把 mtime 变**旧** ⇒ 同样漏检。
        /// 内容指纹对这两种情况都成立（删文件改变路径集合与计数，改动内容改变摘要）。
        /// </summary>
        private static bool IsToolStale(string projectPath, string toolDll, IList<string> extraInputs, IList<string> excludedDirs)
        {
            string current = ComputeToolInputFingerprint(projectPath, extraInputs, excludedDirs);
            if (string.IsNullOrEmpty(current))
            {
                return true;   // 无法计算 ⇒ 宁多建一次，不用不确定的产物
            }

            string recorded = PMNetWeavingPolicy.TryReadToolFingerprint(PMNetWeavingPolicy.ToolFingerprintSidecarPath(toolDll));
            return !string.Equals(current, recorded, StringComparison.Ordinal);
        }

        private static string ResolveDotnet()
        {
            string pref = EditorPrefs.GetString(DotnetPathPrefKey, string.Empty);
            if (!string.IsNullOrEmpty(pref))
            {
                if (File.Exists(pref))
                {
                    return pref;
                }

                Debug.LogWarning(LogPrefix + "EditorPrefs 里的 dotnet 路径不存在，忽略：" + pref);
            }

            string envOverride = Environment.GetEnvironmentVariable("PMNET_DOTNET");
            if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            {
                return envOverride;
            }

            string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root))
            {
                string exe = Path.Combine(root, "dotnet.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }

                string exeNoExt = Path.Combine(root, "dotnet");
                if (File.Exists(exeNoExt))
                {
                    return exeNoExt;
                }
            }

            // 交给 CreateProcess 走 PATH；找不到会在结果里显式报错（不静默）。
            return "dotnet";
        }

        // ============================================================================================
        //  菜单
        // ============================================================================================
        [MenuItem("Tools/PMNet/Weaving/Log Status", false, 10)]
        public static void MenuLogStatus()
        {
            Debug.Log(LogPrefix + "状态："
                + "\n  仓库根=" + GetRepoRoot()
                + "\n  目标程序集=" + TargetAssemblyFileName
                + "\n  回调记录路径=" + SessionState.GetString(LastTargetAssemblyPathKey, "(还没编译过)")
                + "\n  编织格式 v1=" + (WeaveContractPresent() ? "已落地" : "未落地")
                + "\n  编织状态=" + GetWeaveState() + "（" + SessionState.GetString(WeaveStateMessageKey, string.Empty) + "）"
                + "\n  元数据状态=" + GetMetadataState() + "（" + SessionState.GetString(MetadataStateMessageKey, string.Empty) + "）"
                + "\n  源集合指纹=" + ComputeDeclarationFingerprint()
                + "\n  待重载=" + SessionState.GetBool(PendingReloadKey, false)
                + "\n  工具运行中=" + PMNetToolProcess.IsRunning
                + "\n  dotnet=" + ResolveDotnet()
                + "\n  Play/Build 门禁=始终硬门（无开关：未编织的程序集在运行期会被 PMNet_RequireRpcWeave 拒绝）"
                + "\n  自动刷新生成元数据=" + (AutoMetadataRefreshEnabled() ? "开" : "关"));
        }

        [MenuItem("Tools/PMNet/Weaving/Repair: Weave Assembly-CSharp Now", false, 20)]
        public static void MenuRepairWeave()
        {
            string dll = ResolveTargetAssemblyPath();
            if (string.IsNullOrEmpty(dll))
            {
                Debug.LogError(LogPrefix + "Repair 中止：拿不到 Assembly-CSharp.dll 的真实路径。先让 Unity 编译一次（回调会给出路径）。");
                return;
            }

            if (!WeaveContractPresent())
            {
                // 契约缺失时不再"反正试一次"（那会把"没契约"当成"可以少要求"）：
                // 先把生成元数据刷新（可恢复路径），再重新判定；仍然缺失 ⇒ 明确中止（fail closed）。
                _generatedDuringGate = false;
                MetadataState recovered = EnsureMetadataVerified(true);
                if (recovered == MetadataState.Regenerated || _generatedDuringGate)
                {
                    AssetDatabase.Refresh();
                }

                if (!WeaveContractPresent())
                {
                    Debug.LogError(LogPrefix + "Repair 中止：生成物里找不到 " + PMNetWeavingPolicy.WeaveFormatProbeToken
                        + "，无法确认这个程序集可被编织（已尝试刷新生成元数据，结果=" + recovered
                        + "）。请先修复生成链路，再让 Unity 重新编译。");
                    return;
                }
            }

            PMNetToolResult result = WeaveAssembly(dll, WeaveContractPresent());
            if (result.Success)
            {
                SetWeaveState(WeaveState.Ok, "手动编织成功：" + dll);
                // 磁盘已改、内存没改：把这条边界变成真实门禁，而不是一句"应该没事"。
                SessionState.SetBool(PendingReloadKey, true);
                Debug.Log(LogPrefix + "手动编织成功（磁盘）：" + dll
                    + "\n  注意：**已加载的程序集仍是旧映像**，必须脚本重载后才生效（菜单 Request Script Reload），"
                    + "在此之前 Play 门禁会拦住。" + "\n" + DescribeResult(result));
            }
            else
            {
                SetWeaveState(WeaveState.Failed, "手动编织失败：" + result.ShortReason());
                Debug.LogError(LogPrefix + "手动编织失败：" + DescribeResult(result));
            }
        }

        [MenuItem("Tools/PMNet/Weaving/Verify: Check Assembly-CSharp (+require RPCs)", false, 21)]
        public static void MenuVerifyCheck()
        {
            string dll = ResolveTargetAssemblyPath();
            if (string.IsNullOrEmpty(dll))
            {
                Debug.LogError(LogPrefix + "Verify 中止：拿不到 Assembly-CSharp.dll 的真实路径。");
                return;
            }

            PMNetToolResult result = CheckAssembly(dll, true);
            if (result.Success)
            {
                SetWeaveState(WeaveState.Ok, "自检通过：" + dll);
                Debug.Log(LogPrefix + "Verify 通过（--check --require-rpcs，未写盘）：" + dll + "\n" + DescribeResult(result));
            }
            else
            {
                SetWeaveState(WeaveState.Failed, "自检失败：" + result.ShortReason());
                Debug.LogError(LogPrefix + "Verify 失败：" + DescribeResult(result));
            }
        }

        [MenuItem("Tools/PMNet/Weaving/Refresh Generated Metadata (decl-check/decl-gen)", false, 22)]
        public static void MenuRefreshMetadata()
        {
            _generatedDuringGate = false;
            MetadataState state = EnsureMetadataVerified(true);
            if (state == MetadataState.Regenerated && _generatedDuringGate)
            {
                AssetDatabase.Refresh();
            }

            Debug.Log(LogPrefix + "刷新生成元数据结果：" + state + "（" + SessionState.GetString(MetadataStateMessageKey, string.Empty) + "）");
        }

        [MenuItem("Tools/PMNet/Weaving/Rebuild Weaver Tool (editor-tool)", false, 23)]
        public static void MenuRebuildWeaverTool()
        {
            string tool = EnsureToolBuilt(WeaverProjectRelative, WeaverOutputDirRelative, WeaverAssemblyFileName, true, WeaverExtraInputs, WeaverInputExcludedDirs);
            if (string.IsNullOrEmpty(tool))
            {
                Debug.LogError(LogPrefix + "重建 PMNetWeaver 失败。");
                return;
            }

            Debug.Log(LogPrefix + "PMNetWeaver 已重建：" + tool);
        }

        [MenuItem("Tools/PMNet/Weaving/Request Script Reload (after repair)", false, 24)]
        public static void MenuRequestScriptReload()
        {
            Debug.Log(LogPrefix + "请求脚本重载（让磁盘上的编织结果真正进入内存）。");
            EditorUtility.RequestScriptReload();
        }

        [MenuItem("Tools/PMNet/Weaving/Auto Refresh Generated Metadata", false, 41)]
        public static void MenuToggleAutoMetadata()
        {
            EditorPrefs.SetBool(AutoMetadataRefreshPrefKey, !AutoMetadataRefreshEnabled());
            Debug.Log(LogPrefix + "自动刷新生成元数据 = " + (AutoMetadataRefreshEnabled() ? "开" : "关"));
        }

        [MenuItem("Tools/PMNet/Weaving/Auto Refresh Generated Metadata", true)]
        public static bool MenuToggleAutoMetadataValidate()
        {
            Menu.SetChecked("Tools/PMNet/Weaving/Auto Refresh Generated Metadata", AutoMetadataRefreshEnabled());
            return true;
        }

        // ============================================================================================
        //  小工具
        // ============================================================================================
        private static bool AutoMetadataRefreshEnabled()
        {
            return EditorPrefs.GetBool(AutoMetadataRefreshPrefKey, true);
        }

        private static string GetRepoRoot()
        {
            // Application.dataPath = <root>/Client/Assets ⇒ 上两级是仓库根。
            try
            {
                DirectoryInfo clientDir = Directory.GetParent(Application.dataPath);
                if (clientDir == null || clientDir.Parent == null)
                {
                    return null;
                }

                return clientDir.Parent.FullName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + "推导仓库根失败：" + ex.Message);
                return null;
            }
        }

        private static string GetAbsolutePath(string relative)
        {
            string root = GetRepoRoot();
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void SetWeaveState(WeaveState state, string message)
        {
            SessionState.SetInt(WeaveStateKey, (int)state);
            SessionState.SetString(WeaveStateMessageKey, message ?? string.Empty);
        }

        private static WeaveState GetWeaveState()
        {
            return (WeaveState)SessionState.GetInt(WeaveStateKey, (int)WeaveState.Unknown);
        }

        private static void SetMetadataState(MetadataState state, string message)
        {
            SessionState.SetInt(MetadataStateKey, (int)state);
            SessionState.SetString(MetadataStateMessageKey, message ?? string.Empty);
        }

        private static MetadataState GetMetadataState()
        {
            return (MetadataState)SessionState.GetInt(MetadataStateKey, (int)MetadataState.Unknown);
        }

        private static void LogToolRun(string label, PMNetToolResult result)
        {
            string text = "\n  cmd=" + result.CommandLine
                + "\n  cwd=" + result.WorkingDirectory
                + "\n  exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                + "  started=" + result.Started
                + "  timedOut=" + result.TimedOut
                + "  seconds=" + result.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture);

            string stdout = result.StdOut == null ? string.Empty : result.StdOut;
            string stderr = result.StdErr == null ? string.Empty : result.StdErr;
            text += "\n  stdout:\n" + Clamp(stdout, ToolLogBudgetChars);
            text += "\n  stderr:\n" + Clamp(stderr, ToolLogBudgetChars);

            if (result.Success)
            {
                Debug.Log(LogPrefix + "工具完成（" + label + "）" + text);
            }
            else
            {
                Debug.LogError(LogPrefix + "工具失败（" + label + "）：" + result.ShortReason() + text);
            }
        }

        private static string DescribeResult(PMNetToolResult result)
        {
            if (result == null)
            {
                return "(null)";
            }

            return "cmd=" + result.CommandLine
                + " exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                + " seconds=" + result.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)
                + (result.OutputTruncated ? " (输出已截断)" : string.Empty)
                + "\nstdout:\n" + Clamp(result.StdOut, ToolLogBudgetChars)
                + "\nstderr:\n" + Clamp(result.StdErr, ToolLogBudgetChars);
        }

        private static string Clamp(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(空)";
            }

            if (text.Length <= max)
            {
                return text;
            }

            return text.Substring(0, max) + "...(截断)";
        }

        private static void ShowDialog(string title, string message)
        {
            if (IsBatchMode())
            {
                return;   // 批处理下不弹窗（弹窗会干扰 -executeMethod/-quit 流程）
            }

            try
            {
                EditorUtility.DisplayDialog(title, message, "确定");
            }
            catch (Exception)
            {
            }
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
    }

    /// <summary>构建前 preflight：元数据同步 + 工具可用性/编辑器侧编织自检。</summary>
    internal sealed class PMNetWeavingBuildPreflight : IPreprocessBuildWithReport
    {
        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            PMNetWeavingEditor.RunBuildPreflight(report);
        }
    }

    /// <summary>
    /// Player 打包编织入口：在托管脚本 DLL 生成之后、IL2CPP/Mono 转换之前。
    /// Unity 2019.4 真实接口为 `UnityEditor.Build.IPostBuildPlayerScriptDLLs`
    /// （`void OnPostBuildPlayerScriptDLLs(BuildReport)`，且已核对它继承 IOrderedCallback）。
    /// </summary>
    internal sealed class PMNetPlayerScriptDllsWeaver : IPostBuildPlayerScriptDLLs
    {
        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            PMNetWeavingEditor.WeavePlayerScriptDlls(report);
        }
    }
}
