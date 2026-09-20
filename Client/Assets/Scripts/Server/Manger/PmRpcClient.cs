/****************************************************
    大厅请求的「关联 ID + 超时重试」管理（计划 P1 / B3）

    背景：原实现的「请求-响应」完全依赖「同一条 TCP 连接上按顺序等到一个包」，
    MainPack 里既没有请求 ID 也没有 correlation 字段，因此：
      - 同一 ActionCode 连发两次时无法区分两个响应，只能由面板自己猜；
      - 响应丢失（服务端异常、连接抖动）时客户端无从感知，会一直等下去；
      - 无法实现超时、重试与重试去重。

    本类补齐这条链路：
      1. 发送前分配非 0 的 RequestId（0 表示「非请求」，即服务端主动推送/广播）；
      2. 记录待确认项，收到同 Id 的回包即清账；
      3. 每帧扫描超时项，按策略决定「重发」还是「上报超时」。

    重试策略为什么是白名单而不是默认开启：
    TCP 本身可靠，重试只针对「服务端收到但回包丢失」的窄窗口。
    对 CreateRoom / JoinFriendRoom 这类非幂等操作，重发会真的重复执行副作用
    （例如重复加入房间），因此只对幂等请求开启重试，其余只上报超时。
*****************************************************/

using System;
using System.Collections.Generic;
using SocketProto;
using UnityEngine;

namespace Server
{
    /// <summary>
    /// 大厅请求的待确认表与超时重试策略。
    /// </summary>
    public static class PmRpcClient
    {
        /// <summary>请求从发出到判定超时的秒数。</summary>
        public const float TimeoutSeconds = 5f;

        /// <summary>同一请求最多重发次数（仅对幂等请求生效）。</summary>
        public const int MaxRetryCount = 2;

        private sealed class PendingRequest
        {
            public int RequestId;
            public ActionCode ActionCode;
            public MainPack Pack;
            public BaseRequest Owner;
            public float SentTime;
            public int RetryCount;
        }

        /// <summary>
        /// 可以安全重试的请求：重复执行不会产生新的副作用，也不会改变结果。
        /// 不在表里的请求在超时后只上报，不重发。
        ///
        /// 为什么只留这三个（都是只读或「赋同值」类操作）：
        ///   - FindPlayerInfo / FindFriendsInfo 是纯查询；
        ///   - UpdateName 重复设同一个名字结果相同；
        ///   - Logon（注册）**不幂等**：重复注册会得到失败，客户端会把一次已经成功的注册误判为失败；
        ///   - Login **不幂等**：服务端 Login 开头就是「已登录则返回 Fail」，
        ///     若登录已成功但回包丢失，重发反而会让客户端看到登录失败；
        ///   - CreateRoom / JoinFriendRoom / 匹配 / 好友变更等都有真实副作用，重发会重复执行。
        /// 这类请求丢失回包时目前只能依赖「超时上报 + 用户重试 / 心跳重连」，
        /// 想做到可重试需要服务端幂等化（留给后续阶段）。
        /// </summary>
        private static readonly HashSet<ActionCode> IdempotentActions = new HashSet<ActionCode>
        {
            ActionCode.FindPlayerInfo,
            ActionCode.FindFriendsInfo,
            ActionCode.UpdateName,
        };

        private static readonly Dictionary<int, PendingRequest> _pending = new Dictionary<int, PendingRequest>();
        private static int _nextRequestId;

        /// <summary>当前待确认请求数量（调试用）。</summary>
        public static int PendingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>分配一个非 0 的请求 ID。0 被保留表示「非请求」。</summary>
        public static int NextRequestId()
        {
            _nextRequestId++;
            if (_nextRequestId <= 0)
            {
                // 溢出后回到 1，跳过被保留的 0。
                _nextRequestId = 1;
            }

            return _nextRequestId;
        }

        /// <summary>登记一个已分配的请求，等待回包。</summary>
        public static void Track(BaseRequest owner, MainPack pack)
        {
            if (pack == null || pack.RequestId == 0)
            {
                return;
            }

            PendingRequest item = new PendingRequest();
            item.RequestId = pack.RequestId;
            item.ActionCode = pack.Actioncode;
            item.Pack = pack;
            item.Owner = owner;
            item.SentTime = Time.time;
            item.RetryCount = 0;

            _pending[pack.RequestId] = item;
        }

        /// <summary>
        /// 收到回包时清账。
        /// 必须在路由层的任何早退分支之前调用：服务端对「处理但不回包」的请求
        /// （例如 Chat）会回一个 ActionNone 的空 ack，而 ActionNone 会被路由层直接忽略。
        /// </summary>
        public static void OnResponseReceived(MainPack pack)
        {
            if (pack == null || pack.RequestId == 0)
            {
                return;
            }

            PendingRequest item;
            if (_pending.TryGetValue(pack.RequestId, out item))
            {
                _pending.Remove(pack.RequestId);
            }
        }

        /// <summary>每帧扫描超时项。</summary>
        public static void Tick(float now)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            // 先收集，避免在遍历中修改字典。
            List<PendingRequest> timedOut = null;
            foreach (KeyValuePair<int, PendingRequest> kv in _pending)
            {
                if (now - kv.Value.SentTime >= TimeoutSeconds)
                {
                    if (timedOut == null)
                    {
                        timedOut = new List<PendingRequest>();
                    }

                    timedOut.Add(kv.Value);
                }
            }

            if (timedOut == null)
            {
                return;
            }

            for (int i = 0; i < timedOut.Count; i++)
            {
                HandleTimeout(timedOut[i], now);
            }
        }

        private static void HandleTimeout(PendingRequest item, float now)
        {
            bool canRetry = IdempotentActions.Contains(item.ActionCode);

            if (canRetry && item.RetryCount < MaxRetryCount)
            {
                item.RetryCount++;
                item.SentTime = now;
                Logging.HYLDDebug.LogError(
                    $"[RPC][超时重发] action={item.ActionCode} requestId={item.RequestId} retry={item.RetryCount}/{MaxRetryCount} 已等待 {TimeoutSeconds}s 未收到回包");
                HYLDManger.Instance.Send(item.Pack);
                return;
            }

            _pending.Remove(item.RequestId);

            if (!canRetry)
            {
                Logging.HYLDDebug.LogError(
                    $"[RPC][超时未重发] action={item.ActionCode} requestId={item.RequestId} —— 该请求非幂等，不做重发以避免重复副作用");
            }
            else
            {
                Logging.HYLDDebug.LogError(
                    $"[RPC][超时放弃] action={item.ActionCode} requestId={item.RequestId} 重发 {item.RetryCount} 次仍未收到回包");
            }

            if (item.Owner != null)
            {
                item.Owner.OnRequestTimeout(item.ActionCode, item.RequestId);
            }
        }

        /// <summary>清空全部待确认项（断线 / 重新初始化时调用）。</summary>
        public static void ClearAll()
        {
            _pending.Clear();
        }
    }
}
