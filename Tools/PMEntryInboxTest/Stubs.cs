// ============================================================================
//  PMEntryInboxTest 的 UnityEngine / UI 边界替身
// ============================================================================
//
//  本工程要编的是**真正生产**的 RequestManger / BaseRequest / PmRpcClient / Loging /
//  PMDsEntryCodec（见 csproj）。只有 Unity 与大厅 UI 边界是替身，且替身是**功能性**的：
//
//    · UnityEngine.Debug：**逐条记录** Log/LogWarning/LogError，门禁据此断言
//      「没有任何一条日志包含票据/Str 全文」；
//    · UnityEngine.Time：PmRpcClient 只用到 Time.time（单调性不参与本门禁的判定）；
//    · HYLDManger：只提供 RequestManger/PmRpcClient 真正调用的 Pong / Send 两个入口；
//    · MVC.UIbasePanel：BaseRequest 持有它并在 Update 里回调 OnResponse（真实签名的形状）；
//    · MVC.UIMatchingPanel.TryEnsureOpenForEntryNotice：**面板能不能被安全打开**这一个布尔
//      事实的替身（真实实现要摸 Canvas / UI 栈，属 Unity 面）。返回 false 时 RequestManger
//      必须 fail closed（保留暂存、不投递），这条语义由门禁钉住。
//
//  刻意不做的事：不模拟真实渲染、UI 栈、场景加载与 Unity 线程模型；这些由真实 Unity 编译门
//  （PMClientCheck / PMR4UnityCheck / PMNetUnityPlayerCheck）覆盖。
// ============================================================================

using System;
using System.Collections.Generic;
using SocketProto;

namespace UnityEngine
{
    /// <summary>Unity 日志替身：**逐条记录**（门禁据此做「日志不含秘密」的断言）。</summary>
    public static class Debug
    {
        public static readonly List<string> Logs = new List<string>();
        public static readonly List<string> Warnings = new List<string>();
        public static readonly List<string> Errors = new List<string>();

        public static void Log(object message)
        {
            lock (Logs) { Logs.Add(message == null ? string.Empty : message.ToString()); }
        }

        public static void LogWarning(object message)
        {
            lock (Warnings) { Warnings.Add(message == null ? string.Empty : message.ToString()); }
        }

        public static void LogError(object message)
        {
            lock (Errors) { Errors.Add(message == null ? string.Empty : message.ToString()); }
        }

        public static void LogException(Exception e) { LogError(e); }

        /// <summary>清空三类记录（每个场景开头调用）。</summary>
        public static void ClearAll()
        {
            lock (Logs) { Logs.Clear(); }
            lock (Warnings) { Warnings.Clear(); }
            lock (Errors) { Errors.Clear(); }
        }

        /// <summary>把三类记录拼成一个可搜索的大字符串（顺序 = 记录顺序，类间顺序不承诺）。</summary>
        public static string DumpAll()
        {
            var sb = new System.Text.StringBuilder();
            lock (Logs) { foreach (string s in Logs) { sb.Append(s).Append('\n'); } }
            lock (Warnings) { foreach (string s in Warnings) { sb.Append(s).Append('\n'); } }
            lock (Errors) { foreach (string s in Errors) { sb.Append(s).Append('\n'); } }
            return sb.ToString();
        }

        /// <summary>是否出现过包含指定片段的记录（三类都查）。</summary>
        public static bool Contains(string fragment)
        {
            return DumpAll().Contains(fragment);
        }
    }

    /// <summary>PmRpcClient 只用到 <c>Time.time</c>。</summary>
    public static class Time
    {
        public static float time;
    }
}

/// <summary>
/// <c>using UnityEngine.UI;</c> 在 RequestManger/BaseRequest 里是历史遗留（本门禁不用到任何 UI 控件）。
/// 真实 Unity 由程序集提供该命名空间；这里给一个**空命名空间**只是为了保留真实 using 行，
/// 不伪造任何控件类型。
/// </summary>
namespace UnityEngine.UI
{
}

/// <summary>
/// 大厅管理器替身：RequestManger 用 <c>Instance.Pong</c>，PmRpcClient 用 <c>Instance.Send</c>。
/// 真实类型是 MonoBehaviour 单例；本替身只保证这两个入口的行为可观测。
/// </summary>
public class HYLDManger
{
    public static HYLDManger Instance = new HYLDManger();

    /// <summary>收到的 PingPong 包（收包线程写入，门禁单线程读取）。</summary>
    public readonly List<MainPack> ReceivedPongs = new List<MainPack>();

    /// <summary>被重发的请求包（PmRpcClient 超时重发路径）。</summary>
    public readonly List<MainPack> SentPacks = new List<MainPack>();

    public void Pong(MainPack pack)
    {
        lock (ReceivedPongs) { ReceivedPongs.Add(pack); }
    }

    public void Send(MainPack pack)
    {
        lock (SentPacks) { SentPacks.Add(pack); }
    }
}

namespace MVC
{
    /// <summary>
    /// <c>MVC.UIbasePanel</c> 的签名形状替身：BaseRequest.Update 会把队列里的包回调到这里。
    /// 真实基类还带 UI 栈/Unity 组件；本门禁只需要「回调发生了几次、回调到了什么」。
    /// </summary>
    public class UIbasePanel
    {
        public virtual void OnResponse(MainPack pack)
        {
        }
    }

    /// <summary>
    /// 「能不能安全打开 UIMatchingPanel」这一个事实的替身（真实实现是 Unity UI 面的 Canvas/栈操作）。
    ///
    /// 关键语义（由门禁钉住）：返回 false 时 RequestManger 必须 **fail closed** ——
    /// 保留暂存、不投递、不回退旧链；返回 true 才允许投递。
    /// </summary>
    public static class UIMatchingPanel
    {
        /// <summary>true = 面板可安全打开；false = 尚未就绪（模拟面板未注册/对象已销毁）。</summary>
        public static bool Ready;

        /// <summary>TryEnsureOpen 被调用的次数（用于断言「未就绪时确实反复尝试过」）。</summary>
        public static int EnsureOpenCalls;

        /// <summary>未就绪时给出的原因（真实实现返回的是不含秘密的结构性原因）。</summary>
        public static string NotReadyReason = "UI 管理器不可用（尚未初始化或尚未加载场景 UI）";

        public static bool TryEnsureOpenForEntryNotice(out string error)
        {
            EnsureOpenCalls++;
            if (!Ready)
            {
                error = NotReadyReason;
                return false;
            }

            error = null;
            return true;
        }
    }
}
