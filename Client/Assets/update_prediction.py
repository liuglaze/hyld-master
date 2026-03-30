import re

file_path = "D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs"

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# 替换类头部注释
class_header_old = """    public partial class BattleData
    {
        // CapturePlayerSnapshots 已删除（12.3），不再需要全量玩家快照"""

class_header_new = """    public partial class BattleData
    {
        // =========================================================================================
        // 【模块作用】：负责 CSP (Client-Side Prediction 客户端预测) 架构的核心逻辑。
        // 包括：预测输入历史的记录、权威帧位置快照的记录、收到权威帧后的攻击确认，以及未确认输入的重新播放(Replay)。
        // 
        // 【交互关系】：
        // 1. 记录预测输入:
        //    - `BattleManger.BattleTick` 每走一个预测帧，调用 `RecordPredictedHistory` 把摇杆输入存下来。
        // 2. 消费权威帧(和解机制):
        //    - `BattleData.Authority.OnLogicUpdate_sync_FrameIdCheck` 收到权威帧后：
        //      a. 调用 `RecordAuthorityConfirmation` 清理已确认的攻击。
        //      b. 调用 `TrimPredictionHistoryThroughFrame` 丢弃权威帧之前的预测历史（因为已经被服务端证实了）。
        //      c. 调用 `ReplayUnconfirmedInputs` 从权威位置开始，把剩下的预测输入重新执行一遍（修正拉扯）。
        // 3. 记录权威位置:
        //    - `BattleData.Authority.OnLogicUpdate_sync_FrameIdCheck` 权威帧应用完后调用 `RecordAuthoritySnapshotForFrame`，
        //      把真正的服务端位置存下来。主要给别人打我时，子弹找准位置用。
        //
        // 【核心设计原则】：
        // - 客户端永远领先于服务端，先走一步(预测)。
        // - 收到服务端的回执后，立刻修正错误，然后把期间还没收到回执的输入瞬间“快进”重做一遍。
        // =========================================================================================

        // CapturePlayerSnapshots 已删除（12.3），不再需要全量玩家快照"""

content = content.replace(class_header_old, class_header_new)

# 修改 RecordPredictedHistory 注释
record_history_old = """        public void RecordPredictedHistory(int frameId, PlayerOperation input)"""

record_history_new = """        /// <summary>
        /// 【函数作用】：在每次本地预测(Tick)执行前，把当前的摇杆输入记录到历史队列中。
        /// 【核心逻辑】：
        /// 1. 克隆一份当前的输入。
        /// 2. 如果这帧已经记录过，直接覆盖；否则加入链表尾部。
        /// 3. 调用 TrimPredictionHistory 保持历史队列不要无限增长（一般保留20-30帧）。
        /// </summary>
        public void RecordPredictedHistory(int frameId, PlayerOperation input)"""

content = content.replace(record_history_old, record_history_new)

# 修改 ReplayUnconfirmedInputs 注释
replay_old = """        /// <summary>
        /// 11.2: 从预测历史取 authorityFrameId+1 到 predicted_frameID 的本地输入，
        /// 以 lastAuthorityPosition 为起点逐帧推进本地玩家位置。
        /// 移动公式与 HYLDPlayerManger.ApplyPlayerOperation(PlayerOperation) 一致。
        /// </summary>
        private void ReplayUnconfirmedInputs(int authorityFrameId)"""

replay_new = """        /// <summary>
        /// 【函数作用】：核心 CSP 和解逻辑 —— 收到服务端发来的确切位置后，将期间尚未被服务端确认的“未来操作”快速重做一遍。
        /// 【核心逻辑】：
        /// 1. 此时玩家的位置已经被强行拉回到了服务端发来的 `lastAuthorityPosition`（过去的真实位置）。
        /// 2. 遍历预测历史记录，挑出属于 `权威帧号+1` 到 `当前预测帧号` 之间的所有输入。
        /// 3. 拿这些输入，用服务端的物理公式（极简版：dir * 速度 * deltaTime）重新计算一遍，在瞬间把玩家推到最终应该在的位置。
        /// 4. 这也是为什么网络不好时人会瞬移/拉扯的原因——服务端说你在A，客户端算了半天发现你在B，最后强行切到了B。
        /// </summary>
        private void ReplayUnconfirmedInputs(int authorityFrameId)"""

content = content.replace(replay_old, replay_new)

# 修改 RecordAuthoritySnapshotForFrame 注释
record_auth_old = """        /// <summary>
        /// 每次应用权威帧后调用，记录当前帧所有玩家的逻辑位置（已经是权威位置）。
        /// </summary>
        public void RecordAuthoritySnapshotForFrame(int frameId)"""

record_auth_new = """        /// <summary>
        /// 【函数作用】：把权威帧中所有玩家的确切位置做个快照存下来。
        /// 【为什么需要】：当别人开火打你时，客户端要画个特效子弹。因为有网络延迟，如果不存历史，子弹就会从别人【现在】的位置飞出来（看起来很怪）。
        /// 有了这个快照，就可以去查那个人开火那一帧（比如第10帧）到底在哪，从而在那个老位置生成子弹。
        /// </summary>
        public void RecordAuthoritySnapshotForFrame(int frameId)"""

content = content.replace(record_auth_old, record_auth_new)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Update completed.")
