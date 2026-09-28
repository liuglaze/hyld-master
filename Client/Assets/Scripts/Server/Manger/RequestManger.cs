/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/23 21:3:13
    Description:   和服务器进行对接的请求管理
               使用Dic<ActionCode, BaseRequest>处理对应事件
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using System.Diagnostics;
using SocketProto;


namespace Server
{
	public class RequestManger
	{
        // T-LIVE1：本字典**同时**被两个线程访问 —— 大厅 TCP 异步收包线程（路由）与主线程
        // （面板注册 / 断线清理 / 主线程泵）。因此所有读写一律在 _requestLock 下完成。
        // 改动前：收包线程只读、主线程只写，两者完全不互锁；面板重注册与收包并发时可能读到
        // 半更新的 Dictionary（.NET 的 Dictionary 并发读写既不保证不抛，也不保证返回一致结果）。
        private static readonly object _requestLock = new object();
        private static Dictionary<ActionCode, BaseRequest> _requestDic = new Dictionary<ActionCode, BaseRequest>();

        /// <summary>按 Action 取当前注册的请求处理者（未注册返回 null）。线程安全。</summary>
        private static BaseRequest FindRequest(ActionCode action)
        {
            lock (_requestLock)
            {
                BaseRequest request;
                return _requestDic.TryGetValue(action, out request) ? request : null;
            }
        }

        public static void AddRequest(BaseRequest request)
        {
            if (request == null)
            {
                return;
            }

            // 用索引器而不是 Add：同一 Action 的面板在场景重载 / StartInit 重入时可能被
            // **重复注册**（旧的 BaseRequest 已随旧面板退役）。此时必须**替换**成当前处理者，
            // 否则 Add 会抛 ArgumentException，而主线程泵随后取到的还会是退役对象。
            lock (_requestLock)
            {
                _requestDic[request.GetActionCode] = request;
            }
        }

        public static void RemoveAllRequest()
        {
            lock (_requestLock)
            {
                _requestDic.Clear();
            }

            // 待确认表也一并清空：连接/面板重建后旧请求的回包不会再到达。
            PmRpcClient.ClearAll();

            // T-LIVE1：这里**故意不清** <see cref="PMEntryNoticeInbox"/>。
            // 入局/续局通知可能先于面板注册到达（实机缺陷就是这个次序），而 HYLDManger.OnInit
            // 会在场景加载时调用本方法。若顺手把暂存也清掉，等于把「先到的新票」丢在注册前一步，
            // 缺陷原样复现。暂存只能由「投递成功 / TTL 到期 / 连接关闭 / 显式退出」四种方式消失。
        }

        public static void RemoveRequest(ActionCode action)
        {
            lock (_requestLock)
            {
                _requestDic.Remove(action);
            }
        }

        /// <summary>
        /// 根据Action获取Request
        /// </summary>
        /// <param name="pack"></param>
        public static void HandleRequest(MainPack pack)
        {
            if (pack == null)
            {
                return;
            }

            // 先做 request_id 配对，必须落在下面所有早退分支之前。
            // 服务端对「已处理但不回包」的请求（例如 Chat）会补一个 ActionNone 的空 ack，
            // 而 ActionNone 会被本方法直接忽略；若把清账放在那之后，这类请求会一直重试。
            PmRpcClient.OnResponseReceived(pack);

            //8.根据RequestCode处加入到消息队列

            if (pack.Requestcode == RequestCode.PingPong)
            {
                // 本方法运行在收包线程：这里只做「把包交给主线程 Update 消费」的一次引用写，
                // 不调用任何 Unity API（Singleton<T>.Instance 是普通静态属性，不是 Unity API）。
                HYLDManger manager = HYLDManger.Instance;
                if (manager != null)
                {
                    manager.Pong(pack);
                }

                return;
            }
            if (pack.Actioncode == ActionCode.ActionNone) return;

            // T-LIVE1：入局/续局通知（StartEnterBattle + PMDS1:/PMDSR1:）**先有界暂存**，
            // 由主线程泵在面板就绪后恰好一次投递。
            //
            // 为什么不能在这里直接投递：
            //   1. 本方法是收包线程，而「打开匹配面板」是 Unity API，只能主线程做；
            //   2. 面板（UIMatchingPanel.Init → AddRequest）可能**还没注册**，此时无接收者，
            //      旧实现直接落进 else 分支并丢掉通知（同时把含票据的整包写日志）；
            //   3. Lobby 在登录成功后**主动推送**续局新票，这次实机失败就断在这个竞态上。
            if (IsMatchingEntryNotice(pack))
            {
                // 收包线程：只做「有界暂存」。接收者此刻可能还不存在（UIMatchingPanel.Init/AddRequest
                // 尚未跑），所以这里既不解析也不投递 —— 真正的「先开面板、再交包」交给主线程泵。
                QueueMatchingEntryNotice(FindRequest(pack.Actioncode), pack);
                return;
            }

            BaseRequest request = FindRequest(pack.Actioncode);
            if (request != null)
            {
                request.OnResponse(pack);
                return;
            }

            // 未注册的普通请求：只记**非秘密元数据**。
            // 改动前这里打的是 pack.ToString()，Google.Protobuf 的 ToString 会输出 Str 全文，
            // 而入局通知正是靠 Str 承载 `PMDS1:` + Base64(含票据) —— 等于把短时效凭据写进日志。
            // 旧日志无法撤回，但新代码不得再产生任何一条这样的记录。
            Logging.HYLDDebug.LogError("[R3B] 不能找到对应的处理   request=" + pack.Requestcode
                                       + "   action=" + pack.Actioncode
                                       + "   return=" + pack.Returncode
                                       + "   requestId=" + pack.RequestId
                                       + "   strLen=" + StrLength(pack.Str));
            Logging.HYLDDebug.LogError("[R3B] 未注册请求只记非秘密元数据：MainPack 内容（Str 可能携带票据/凭据）不写日志");
        }

        /// <summary>字符串长度（null 记 0）；只用于日志里给出「有没有载荷」这一非秘密事实。</summary>
        private static int StrLength(string value)
        {
            return value == null ? 0 : value.Length;
        }

        /// <summary>
        /// 是否为本模块要特殊接线的「匹配/入局通知」：已成功的 StartEnterBattle 且带新链前缀。
        ///
        /// 判据与 <c>UIMatchingPanel.OnResponse</c> 的分流口径同源（<c>PMDsEntryCodec.HasPrefix</c>，
        /// 同时覆盖 <c>PMDS1:</c> 与 <c>PMDSR1:</c>）：只有新链通知才进暂存路径；
        /// 旧链/失败包不进这条路径，行为与改动前逐字一致。
        /// 额外要求 Actioncode 就是 <c>StartEnterBattle</c>：别的 Action 下出现 PMDS 样式的文本
        /// 不得被停放进入局通知暂存槽（它既解不出入局语义，也永远不会被匹配面板消费）。
        /// 注意：这里只做前缀识别，**不做**解码 —— 严格解码在 <see cref="PMEntryNoticeInbox.Stage"/>
        /// 里用真实 <c>PMDsEntryCodec</c> 做一次，在面板里还要再做一次（面板仍是唯一入局裁决者）。
        /// </summary>
        private static bool IsMatchingEntryNotice(MainPack pack)
        {
            return pack != null
                && pack.Returncode == ReturnCode.Succeed
                && pack.Actioncode == ActionCode.StartEnterBattle
                && PMNet.Session.PMDsEntryCodec.HasPrefix(pack.Str);
        }

        /// <summary>
        /// 入局/续局通知的**收包侧入口**（大厅 TCP 异步收包线程调用）。
        ///
        /// 为什么这里只做暂存、不做投递：
        ///   · 本方法运行在收包线程上，而「打开匹配面板」是 Unity API，只能主线程做；
        ///   · 面板（UIMatchingPanel.Init → AddRequest）可能**还没注册**，此时没有任何接收者
        ///     —— 旧实现就是在这一步把通知连同票据一起写进日志并丢掉的；
        ///   · 因此「打开面板 + 交包」整体推迟到主线程泵（<see cref="PumpEntryNoticeInbox"/>）。
        ///
        /// <paramref name="request"/> 允许为 null（面板尚未注册），它只用于诊断，不参与投递：
        /// 投递时按 ActionCode 重新解析**当前**处理者（见 <see cref="OpenMatchingPanelThenDeliver"/>）。
        /// </summary>
        private static void QueueMatchingEntryNotice(BaseRequest request, MainPack pack)
        {
            // 严格解码 + 有界暂存：纯 C#，不触碰任何 Unity API。
            PMEntryNoticeInbox.Stage(pack, pack.Actioncode);

            if (request == null)
            {
                Logging.HYLDDebug.Log("[TLIVE1] 入局通知到达时面板尚未注册：已暂存，等主线程泵在面板就绪后投递");
            }
        }

        /// <summary>
        /// T-LIVE1 主线程泵：把收包线程暂存下来的入局/续局通知，在**面板就绪**后**恰好一次**投递。
        ///
        /// 调用点必须是主线程（当前是 <c>HYLDManger.Update</c>，并且刻意排在
        /// <c>_uiManger.Excute</c> 之前：先入面板队列，同一帧就能被抽干）。
        ///
        /// 语义（每条都能被门禁的负例钉住）：
        ///   · 没有暂存 → 直接返回（零开销，不做任何 Unity 调用）；
        ///   · 暂存已过期 / 属于旧连接 → 丢弃并计数，明确拒绝，**不投递、不回退旧链**；
        ///   · 面板尚未注册 → **保留暂存**，下一帧再试，直到 TTL 到期（不丢、不伪造接收者）；
        ///   · 打不开面板 → fail closed：保留暂存、只记一次错误，绝不投递给关闭中的面板；
        ///   · 投递 → 先消费暂存再交包，保证「恰好一次」（面板抛异常也不会二次投递）。
        /// </summary>
        public static void PumpEntryNoticeInbox()
        {
            MainPack pack;
            ActionCode action;
            string dropReason;
            if (!PMEntryNoticeInbox.TryPeekValid(out pack, out action, out dropReason))
            {
                if (dropReason != null)
                {
                    Logging.HYLDDebug.LogWarning("[TLIVE1] 暂存的入局通知已被丢弃（" + dropReason
                                                 + "）：不投递、不回退旧链");
                }

                return;
            }

            // 面板还没注册（UIMatchingPanel.Init → AddRequest 尚未跑）：保留暂存，等下一帧或 TTL。
            BaseRequest request = FindRequest(action);
            if (request == null)
            {
                return;
            }

            OpenMatchingPanelThenDeliver(request, pack);
        }

        /// <summary>
        /// 主线程侧：**先**安全打开 UIMatchingPanel，成功后才把包交给它的 BaseRequest 队列。
        ///
        /// 注意这里**不再**经过 NetGlobal/ActionManger 再兜一圈：主线程泵本身已经在主线程上，
        /// 多一次全局单例跳转只会多一帧延迟并引入「入队后未抽干时被重复入队」的重复投递风险。
        /// （收包线程一侧更不允许触碰 NetGlobal：那是懒创建的 GameObject，属于 Unity API。）
        ///
        /// 面板打不开（管理器未就绪 / 面板未注册 / 对象已销毁）时 fail closed：只记一次错误、不投递，
        /// 也绝不改走旧链；暂存保留在槽里，由后续帧继续尝试，直到 TTL 到期。
        /// </summary>
        private static void OpenMatchingPanelThenDeliver(BaseRequest request, MainPack pack)
        {
            string error;
            if (!MVC.UIMatchingPanel.TryEnsureOpenForEntryNotice(out error))
            {
                if (PMEntryNoticeInbox.TryBeginDeliveryFailureLog())
                {
                    Logging.HYLDDebug.LogError("[TLIVE1] 暂存的入局通知暂未投递：无法安全打开 UIMatchingPanel（"
                                               + error + "）；保留暂存、不回退旧链");
                }

                return;
            }

            // 从暂存到本帧执行之间可能已经换过面板（RemoveAllRequest → 重新 AddRequest）：
            // 按 ActionCode 重新取**当前**处理者，绝不把包排进已退役对象的无主队列。
            if (request != null)
            {
                request = FindRequest(request.GetActionCode);
            }

            if (request == null)
            {
                return;
            }

            // 恰好一次：**先**消费暂存再交包。若面板 OnResponse 抛异常也不会出现第二次投递
            // （重复投递会把入局通知排进面板队列两次，等于拿同一张票走两次 Enter）。
            PMEntryNoticeInbox.ConsumeForDelivery();

            try
            {
                request.OnResponse(pack);
            }
            catch (Exception ex)
            {
                // 面板抛异常不允许把整个主线程 Update 打断，也不允许在这里改走旧链。
                Logging.HYLDDebug.LogError("[TLIVE1] 投递入局通知时面板抛出异常（已消费暂存、不重投）："
                                           + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 新的大厅 TCP 连接建立：暂存代次 +1。此后到达的通知属于这条新连接。
        /// </summary>
        public static void NotifySocketConnected()
        {
            PMEntryNoticeInbox.BeginConnection();
        }

        /// <summary>
        /// 大厅 TCP 连接关闭：代次 +1 **并清空暂存**（关闭 socket 必须清掉待用票）。
        /// </summary>
        public static void NotifySocketClosed(string reason)
        {
            PMEntryNoticeInbox.EndConnection(reason);
        }

        /// <summary>显式清理暂存（退出/销毁路径用；与「连接关闭」互补）。</summary>
        public static void ClearStagedEntryNotice(string reason)
        {
            PMEntryNoticeInbox.Clear(reason);
        }

    }


    /// <summary>
    /// T-LIVE1：入局/续局通知（<c>StartEnterBattle</c> + <c>PMDS1:</c>/<c>PMDSR1:</c>）的
    /// **有界单槽暂存**（收包线程写、主线程读）。
    ///
    /// 为什么需要它（本次实机缺陷的最小复现）：
    ///   Lobby 在登录成功后**主动推送**续局新票（实机 20:26:27 已发出），而这条通知是在大厅 TCP
    ///   的**异步收包线程**上到达的；此时 <c>UIMatchingPanel.Init</c> / <c>AddRequest</c> 可能还没跑
    ///   （面板未注册、未打开）。旧实现只有「已注册」这一条分支，未注册时落进
    ///   「不能找到对应的处理」并把整包打日志 —— 通知被丢掉，同时把短时效票据写进了日志。
    ///
    /// 本类只承担「暂存」这一件事，且是**线程安全 + 有界 + 有 TTL + 绑连接**的：
    ///   · 全程在 <c>_lock</c> 下访问；不碰任何 Unity API（收包线程可以安全调用）；
    ///   · **最多一条**：新通知覆盖旧通知（覆盖次数可观测），不排队、不无界增长；
    ///   · TTL 硬上限 30 秒（<see cref="MaxTtlMs"/>）：到点即丢弃并计数，绝不在过期后投递；
    ///   · 只接受**严格解码成功**的载荷（走真实 <see cref="PMDsEntryCodec"/>），非法一律拒收；
    ///   · 绑定「暂存时的 TCP 连接代次」：断开/重连后属于旧连接的暂存一律作废。
    ///
    /// 它**不做**的事（有意留给调用方与面板）：
    ///   · 不生成任何入局状态、不判定是否真能入局 —— <c>UIMatchingPanel.OnResponse</c> 仍会自己对
    ///     同一条文本做一次严格解码并交 <c>PMClientSessionHost.Enter</c>，本类不绕过那道校验；
    ///   · 不打印载荷内容（<c>MainPack.Str</c> 含票据）：日志只记类型/计数/连接代次这类非秘密元数据。
    /// </summary>
    public static class PMEntryNoticeInbox
    {
        /// <summary>暂存有效期上限（毫秒）。冻结上限就是 30s：不允许无界等待。</summary>
        public const long MaxTtlMs = 30000L;

        /// <summary>暂存槽容量：恒为 1（最多一条待通知；新通知覆盖旧通知，不排队）。</summary>
        public const int Capacity = 1;

        /// <summary>暂存槽里的一条通知（只保存事实，不复制/不改写载荷）。</summary>
        private sealed class Entry
        {
            public MainPack Pack;
            public ActionCode ActionCode;

            /// <summary>暂存时刻（单调毫秒）。</summary>
            public long StagedAtMs;

            /// <summary>暂存时的 TCP 连接代次（投递时必须与当前代次一致）。</summary>
            public int Generation;

            /// <summary>本条暂存是否已经为「打不开面板」记过一次错误（防每帧刷屏）。</summary>
            public bool DeliveryFailureLogged;
        }

        private static readonly object _lock = new object();

        /// <summary>默认单调时钟：只用 <see cref="Stopwatch"/>，不依赖 UnityEngine.Time（收包线程也能用）。</summary>
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private static Entry _entry;
        private static int _generation;
        private static long _ttlMs = MaxTtlMs;

        /// <summary>门禁注入时钟（null = 用默认 <see cref="Stopwatch"/>）。</summary>
        private static volatile Func<long> _nowMsProvider;

        private static long _stagedCount;
        private static long _overwriteCount;
        private static long _rejectedIllegalCount;
        private static long _expiredCount;
        private static long _staleConnectionCount;
        private static long _deliveredCount;
        private static long _clearedCount;

        /// <summary>当前单调毫秒。</summary>
        public static long NowMs
        {
            get
            {
                Func<long> provider = _nowMsProvider;
                return provider != null ? provider() : _clock.ElapsedMilliseconds;
            }
        }

        /// <summary>当前 TCP 连接代次（0 = 尚未建立任何连接）。</summary>
        public static int CurrentGeneration
        {
            get { lock (_lock) { return _generation; } }
        }

        /// <summary>暂存槽是否非空（最多 1）。</summary>
        public static bool HasPending
        {
            get { lock (_lock) { return _entry != null; } }
        }

        /// <summary>当前生效的 TTL（默认 <see cref="MaxTtlMs"/>，门禁可临时调小）。</summary>
        public static long TtlMs
        {
            get { lock (_lock) { return _ttlMs; } }
        }

        /// <summary>成功暂存次数（含覆盖）。</summary>
        public static long StagedCount { get { lock (_lock) { return _stagedCount; } } }

        /// <summary>「新通知覆盖旧通知」的次数（可观测性：覆盖必须能被看见）。</summary>
        public static long OverwriteCount { get { lock (_lock) { return _overwriteCount; } } }

        /// <summary>严格解码失败被拒收的次数。</summary>
        public static long RejectedIllegalCount { get { lock (_lock) { return _rejectedIllegalCount; } } }

        /// <summary>TTL 到期被丢弃的次数。</summary>
        public static long ExpiredCount { get { lock (_lock) { return _expiredCount; } } }

        /// <summary>属于旧连接（或没有活动连接）被丢弃的次数。</summary>
        public static long StaleConnectionCount { get { lock (_lock) { return _staleConnectionCount; } } }

        /// <summary>被成功投递（消费）的次数。</summary>
        public static long DeliveredCount { get { lock (_lock) { return _deliveredCount; } } }

        /// <summary>被连接关闭/显式清理丢弃的次数。</summary>
        public static long ClearedCount { get { lock (_lock) { return _clearedCount; } } }

        /// <summary>新连接建立：代次 +1。</summary>
        public static int BeginConnection()
        {
            lock (_lock)
            {
                _generation++;
                return _generation;
            }
        }

        /// <summary>
        /// 连接关闭：代次 +1 **并清空暂存**。
        /// 「关闭 socket 清待用票」是硬要求：旧连接上到达、尚未被主线程消费的通知不允许在重连后
        /// 继续生效（否则等于拿旧连接的票去开新连接）。
        /// </summary>
        public static void EndConnection(string reason)
        {
            bool hadPending;
            lock (_lock)
            {
                _generation++;
                hadPending = _entry != null;
                if (hadPending)
                {
                    _entry = null;
                    _clearedCount++;
                }
            }

            if (hadPending)
            {
                Logging.HYLDDebug.Trace("[TLIVE1] 关闭连接：已清空暂存的入局通知（reason=" + reason + "）");
            }
        }

        /// <summary>
        /// 严格解码 + 暂存（收包线程调用）。
        ///
        /// 返回 true 表示「这条通知已被暂存」；false 表示被拒收（非法载荷 / 无活动连接）。
        /// 拒收一律**不留半状态**：不生成入局状态、不投递给任何人、日志不含 <c>Str</c>。
        /// </summary>
        public static bool Stage(MainPack pack, ActionCode actionCode)
        {
            if (pack == null)
            {
                return false;
            }

            // 严格解码必须用**真实 codec**（不拿字符串前缀冒充校验）：只有解得出来的才算入局通知。
            // 这里只用它的 resume 位做日志分类，不把 offer 传给任何人、也不据此生成入局状态。
            PMNet.Session.PMDsEntryOffer offer;
            string error;
            if (!PMNet.Session.PMDsEntryCodec.TryDecode(pack.Str, out offer, out error))
            {
                lock (_lock)
                {
                    _rejectedIllegalCount++;
                }

                Logging.HYLDDebug.LogError("[TLIVE1] 入局通知未暂存：严格解码失败（拒绝、不回退旧链、不打印 Str）：" + error);
                return false;
            }

            bool overwritten;
            bool noConnection = false;
            long stagedCount;
            long overwriteCount;
            int generation;
            long stagedAtMs;

            lock (_lock)
            {
                if (_generation <= 0)
                {
                    // 没有活动连接时到达的入局通知在结构上不成立：拒收而不是留在槽里等 30 秒。
                    _staleConnectionCount++;
                    noConnection = true;
                    overwritten = false;
                    stagedCount = _stagedCount;
                    overwriteCount = _overwriteCount;
                    generation = _generation;
                    stagedAtMs = 0;
                }
                else
                {
                    overwritten = _entry != null;
                    Entry entry = new Entry();
                    entry.Pack = pack;
                    entry.ActionCode = actionCode;
                    entry.StagedAtMs = NowMs;
                    entry.Generation = _generation;
                    _entry = entry;

                    _stagedCount++;
                    if (overwritten)
                    {
                        _overwriteCount++;
                    }

                    stagedCount = _stagedCount;
                    overwriteCount = _overwriteCount;
                    generation = _generation;
                    stagedAtMs = entry.StagedAtMs;
                }
            }

            if (noConnection)
            {
                Logging.HYLDDebug.LogError("[TLIVE1] 入局通知未暂存：当前没有活动的大厅连接（不回退旧链）");
                return false;
            }

            // 日志只记非秘密元数据：类型、覆盖次数、连接代次、TTL。绝不输出 offer/Str/票据。
            Logging.HYLDDebug.Log("[TLIVE1] 入局通知已暂存：kind=" + (offer.IsResume ? "PMDSR1(续局)" : "PMDS1(初始)")
                                  + " resumed=" + (offer.IsResume ? 1 : 0)
                                  + " connGen=" + generation
                                  + " stagedAtMs=" + stagedAtMs
                                  + " staged=#" + stagedCount
                                  + " overwrite=#" + overwriteCount
                                  + (overwritten ? "（覆盖了上一条暂存）" : string.Empty)
                                  + " ttlMs=" + TtlMs);
            return true;
        }

        /// <summary>
        /// 取出「当前可投递」的暂存（主线程调用；**不移除**，由 <see cref="ConsumeForDelivery"/> 负责消费）。
        ///
        /// 返回 false 的两种含义由 <paramref name="dropReason"/> 区分：
        ///   · <c>dropReason == null</c> → 本来就没有暂存（正常无事可做）；
        ///   · <c>dropReason != null</c> → 有一条暂存但已被**丢弃**（过期 / 旧连接），原因已写进它。
        /// </summary>
        public static bool TryPeekValid(out MainPack pack, out ActionCode actionCode, out string dropReason)
        {
            pack = null;
            actionCode = default(ActionCode);
            dropReason = null;

            lock (_lock)
            {
                Entry entry = _entry;
                if (entry == null)
                {
                    return false;
                }

                long elapsed = NowMs - entry.StagedAtMs;
                if (elapsed >= _ttlMs)
                {
                    _entry = null;
                    _expiredCount++;
                    dropReason = "TTL 到期（已等待 " + elapsed + "ms >= " + _ttlMs + "ms）";
                    return false;
                }

                if (_generation <= 0 || entry.Generation != _generation)
                {
                    _entry = null;
                    _staleConnectionCount++;
                    dropReason = "属于旧连接（暂存 connGen=" + entry.Generation + "，当前 connGen=" + _generation + "）";
                    return false;
                }

                pack = entry.Pack;
                actionCode = entry.ActionCode;
                return true;
            }
        }

        /// <summary>
        /// 消费暂存（投递前调用，**恰好一次**语义的落点）：
        /// 只有真正要把包交给面板前才调用它，调用后 <see cref="HasPending"/> 必为 false。
        /// </summary>
        public static void ConsumeForDelivery()
        {
            lock (_lock)
            {
                if (_entry == null)
                {
                    return;
                }

                _entry = null;
                _deliveredCount++;
            }
        }

        /// <summary>
        /// 「打不开面板」这类**可重试**失败的日志门：每条暂存只允许记一次，避免每帧刷屏。
        /// </summary>
        public static bool TryBeginDeliveryFailureLog()
        {
            lock (_lock)
            {
                if (_entry == null || _entry.DeliveryFailureLogged)
                {
                    return false;
                }

                _entry.DeliveryFailureLogged = true;
                return true;
            }
        }

        /// <summary>显式清理暂存（退出/销毁路径）。</summary>
        public static void Clear(string reason)
        {
            bool hadPending;
            lock (_lock)
            {
                hadPending = _entry != null;
                if (hadPending)
                {
                    _entry = null;
                    _clearedCount++;
                }
            }

            if (hadPending)
            {
                Logging.HYLDDebug.Trace("[TLIVE1] 已清空暂存的入局通知（reason=" + reason + "）");
            }
        }

        /// <summary>
        /// 门禁专用：注入单调时钟（毫秒）。传 null 恢复默认 <see cref="Stopwatch"/>。
        /// </summary>
        public static void SetNowMsProvider(Func<long> provider)
        {
            _nowMsProvider = provider;
        }

        /// <summary>门禁专用：把 TTL 临时调小（不得超过 <see cref="MaxTtlMs"/>）。</summary>
        public static void SetTtlMsForTests(long ttlMs)
        {
            if (ttlMs <= 0 || ttlMs > MaxTtlMs)
            {
                throw new ArgumentOutOfRangeException("ttlMs", "TTL 必须落在 1.." + MaxTtlMs + " 之间");
            }

            lock (_lock)
            {
                _ttlMs = ttlMs;
            }
        }

        /// <summary>门禁专用：清空暂存与全部计数（不改变进程内的真实连接代次语义之外的东西）。</summary>
        public static void ResetForTests()
        {
            lock (_lock)
            {
                _entry = null;
                _generation = 0;
                _ttlMs = MaxTtlMs;
                _nowMsProvider = null;
                _stagedCount = 0;
                _overwriteCount = 0;
                _rejectedIllegalCount = 0;
                _expiredCount = 0;
                _staleConnectionCount = 0;
                _deliveredCount = 0;
                _clearedCount = 0;
            }
        }
    }
}
