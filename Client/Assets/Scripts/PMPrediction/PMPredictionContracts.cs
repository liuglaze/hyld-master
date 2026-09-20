using System;

namespace PMNet.Prediction
{
    /// <summary>纯模型契约：输入与起点只读，输出不得与历史中的可变成员共享引用。</summary>
    public interface IPMPredictionModel<TInput, TSync, TAux>
    {
        TInput CloneInput(TInput input);
        TSync CloneSync(TSync sync);
        TAux CloneAux(TAux aux);
        PMSimulationResult<TSync, TAux> Simulate(PMTimeStep step, TInput input, TSync start, TAux aux);
        bool ShouldReconcile(TSync predicted, TSync authority, TAux predictedAux, TAux authorityAux);
        TSync Interpolate(TSync from, TSync to, float alpha);
    }

    public struct PMPredictionEvent
    {
        public ulong Key;
        public int Kind;
        public int Value;
        public PMPredictionEvent(ulong key, int kind, int value)
        {
            if (key == 0UL) { throw new ArgumentOutOfRangeException("key"); }
            Key = key; Kind = kind; Value = value;
        }
    }

    public sealed class PMSimulationResult<TSync, TAux>
    {
        public TSync Sync;
        public TAux Aux;
        public PMPredictionEvent[] Events = new PMPredictionEvent[0];
    }

    /// <summary>
    /// 一个输出边界的**权威事件证据**：边界号 + 该边界产生的权威事件集合。
    /// 只在「边界号 + 事件集合」成对给出时才有证据语义；<see cref="Events"/> 为 null = 未提供证据。
    /// </summary>
    public struct PMPredictionEventFrame
    {
        /// <summary>输出边界（必须是 <see cref="PMFrameDomain.Input"/> 命名空间）。</summary>
        public PMFrameId OutputFrame;

        /// <summary>该输出边界产生的权威事件集合（空数组 = 确定无事件；null = 未提供证据）。</summary>
        public PMPredictionEvent[] Events;

        public PMPredictionEventFrame(PMFrameId outputFrame, PMPredictionEvent[] events)
        {
            OutputFrame = outputFrame;
            Events = events;
        }
    }

    /// <summary>OutputFrame 是输入时间轴的输出边界，ServerFrame 是独立权威元数据。</summary>
    public sealed class PMPredictionSnapshot<TSync, TAux>
    {
        public uint Epoch;
        public uint InstanceId;
        public PMFrameId OutputFrame;
        public PMFrameId ServerFrame;
        public double TotalSimTimeMs;
        public TSync Sync;
        public TAux Aux;

        /// <summary>
        /// 本输出边界（<see cref="OutputFrame"/>）产生的**权威**事件集合，即该边界的不逆事件证据：
        /// null = 未提供事件证据（预测核心**不得**用预测事件冒充权威，也不得广播该边界事件）；
        /// 空数组 = 确定该边界没有不可逆事件（证据成立，只是集合为空）。
        /// 非 null 时预测核心会深克隆；调用方事后改写此数组不影响已确认的历史。
        /// </summary>
        public PMPredictionEvent[] ConfirmedEvents;

        /// <summary>
        /// 按边界给出的权威事件证据批：一次确认跨过多个边界时，用「边界号 + 该边界事件」成对提供。
        /// 这是首选承载（有界，上限见 PMPredictionTimeline.MaxConfirmedEventFrames / MaxConfirmedEventsTotal）。
        /// null/空 = 本次只确认状态、不广播任何事件（宿主可经可靠事件通道补）。
        /// 只允许描述本次调用真正要确认的边界，即 (ConfirmedFrame, OutputFrame]。
        /// </summary>
        public PMPredictionEventFrame[] ConfirmedEventFrames;
    }
}
