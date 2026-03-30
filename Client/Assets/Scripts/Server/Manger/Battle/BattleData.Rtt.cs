/****************************************************
    BattleData.Rtt.cs  --  partial class: RTT 测量（动态追帧系统）
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using UnityEngine;

namespace Manger
{
    public partial class BattleData
    {
        // ═══════ RTT 测量（动态追帧系统） ═══════

        /// <summary> EWMA 平滑后的往返延迟（毫秒）。 </summary>
        public float smoothedRTT { get; private set; }

        /// <summary> RTT 方差（毫秒），用于抖动估计。 </summary>
        public float rttVariance { get; private set; }

        /// <summary> 是否已收到首个有效 Pong 样本。 </summary>
        private bool _rttInitialized;

        /// <summary> 上次发送 Ping 的 Time.time（秒）。 </summary>
        public float _lastPingTime;

        /// <summary> 最近一次已消费的 Pong 时间戳，用于丢弃旧包/重复包。 </summary>
        private long _lastAcceptedPongTimestamp;

        /// <summary>
        /// 处理 Pong 包中的 RTT 样本：过滤异常 + 旧包去重 + EWMA 平滑。
        /// </summary>
        public void ProcessPongRttSample(long pongTimestamp, long localNowMs)
        {
            if (pongTimestamp <= 0)
            {
                Logging.HYLDDebug.FrameTrace($"[RTT] DISCARD timestamp={pongTimestamp} reason=invalid_timestamp");
                return;
            }

            if (pongTimestamp <= _lastAcceptedPongTimestamp)
            {
                Logging.HYLDDebug.FrameTrace($"[RTT] DISCARD timestamp={pongTimestamp} lastAccepted={_lastAcceptedPongTimestamp} reason=stale_or_duplicate_pong");
                return;
            }

            long rttSample = localNowMs - pongTimestamp;
            // 过滤异常样本
            if (rttSample <= 0 || rttSample > 2000)
            {
                Logging.HYLDDebug.FrameTrace($"[RTT] DISCARD sample={rttSample}ms timestamp={pongTimestamp} reason=out_of_range");
                return;
            }

            _lastAcceptedPongTimestamp = pongTimestamp;
            if (!_rttInitialized)
            {
                // 首次样本直接赋值
                smoothedRTT = rttSample;
                rttVariance = rttSample / 2f;
                _rttInitialized = true;
            }
            else
            {
                // EWMA: alpha=0.125, beta=0.25
                smoothedRTT = (1f - 0.125f) * smoothedRTT + 0.125f * rttSample;
                rttVariance = (1f - 0.25f) * rttVariance + 0.25f * Mathf.Abs(rttSample - smoothedRTT);
            }
            Logging.HYLDDebug.FrameTrace($"[RTT] sample={rttSample}ms smoothed={smoothedRTT:F1}ms variance={rttVariance:F1}ms timestamp={pongTimestamp}");
        }

        /// <summary> RTT 是否已初始化（至少收到一个有效 Pong）。 </summary>
        public bool IsRttInitialized => _rttInitialized;
    }
}
