import re

file_path = "D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/Manger/Battle/BattleData.HitEvent.cs"

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# 替换整个类的头部注释，增加详细说明
class_header_old = """    public partial class BattleData
    {
        // ═══════ HitEvent 消费（7.1-7.6） ═══════"""

class_header_new = """    public partial class BattleData
    {
        // =========================================================================================
        // 【模块作用】：处理服务端的伤害事件(HitEvent)与权威血量/死亡状态(Hp/IsDead)。
        // 
        // 【交互关系】：
        // 1. 调用方:
        //    - 由 `UDPSocketManger.HandleMessage` (收到权威帧 `BattlePushDowmAllFrameOpeartions` 时) 调用。
        //    - 调用顺序: 必须先调用 `ApplyHitEvents`，再调用 `ApplyAuthoritativeHpAndDeath`。
        // 2. 被影响方:
        //    - `HYLDStaticValue.Players`: 更新角色的动画(受击/死亡)、HP(`playerBloodValue`)、生存状态(`isNotDie`)。
        //    - `PlayerLogic`: 当 `isNotDie` 被置为 false 或 `playerBloodValue` <= 0 时，触发其 `playerDieLogic`。
        //
        // 【核心设计原则】：
        // - HitEvent (ApplyHitEvents) 仅作为【视觉表现】（触发受击动画），绝对不修改真实血量。
        // - 真实血量与死亡判定必须由 【权威帧状态】 (ApplyAuthoritativeHpAndDeath) 覆写决定。
        // =========================================================================================

        // ═══════ HitEvent 消费（7.1-7.6） ═══════"""

content = content.replace(class_header_old, class_header_new)

# 修改 ApplyHitEvents 注释
apply_hit_events_old = """        /// <summary>
        /// 在主线程中直接处理服务端下行的 HitEvent 列表，执行扣血/死亡/受击表现。
        /// 应在 HandleMessage 主线程路径中调用（DrainAndDispatch -> HandleMessage）。
        /// </summary>
        public void ApplyHitEvents(Google.Protobuf.Collections.RepeatedField<HitEvent> hitEvents)"""

apply_hit_events_new = """        /// <summary>
        /// 【函数作用】：处理服务端发来的命中事件（HitEvent）。
        /// 【核心职责】：仅负责纯视觉表现（播放受击动画）。坚决不在这里扣血！
        /// 
        /// 【执行流程】：
        /// 1. 清空本批次的受击动画记录 (_hitAnimatedPlayers.Clear)。
        /// 2. 遍历服务端下发的 HitEvents。
        /// 3. 去重拦截：如果这个攻击ID之前处理过（防止丢包重传导致重复播放），直接跳过。
        /// 4. 表现层触发：找到受击者，触发 Animator 的 "Hit" 动画，并记录到 _hitAnimatedPlayers 中。
        /// 5. 死亡兜底(安全网)：如果服务端明确标记 `IsKill=true`，强制把客户端 HP 置 -1，并播放死亡动画。
        ///    (这是在权威 IsDead 状态到达前的提前表现补偿)。
        /// </summary>
        public void ApplyHitEvents(Google.Protobuf.Collections.RepeatedField<HitEvent> hitEvents)"""

content = content.replace(apply_hit_events_old, apply_hit_events_new)

# 修改 ApplyAuthoritativeHpAndDeath 注释
apply_auth_hp_old = """        /// <summary>
        /// [AHS-2] 从权威帧批次最后一帧的 PlayerStates 读取 Hp/IsDead，
        /// 覆写本地 playerBloodValue，处理死亡判定，并执行兜底受击动画。
        /// 应在 ApplyHitEvents 之后调用（依赖 _hitAnimatedPlayers）。
        /// </summary>
        public void ApplyAuthoritativeHpAndDeath(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> frames, int frameId)"""

apply_auth_hp_new = """        /// <summary>
        /// 【函数作用】：处理服务端的权威状态（血量 HP 和 是否死亡 IsDead）。
        /// 【核心职责】：这里才是真正修改客户端血量和决定生死的地方！绝对服从服务端。
        /// 
        /// 【执行流程】：
        /// 1. 帧序保护：检查批次最新帧号是否 <= 已处理的 _lastAuthHpFrameId，如果是，说明是乱序旧包，直接丢弃（防止血量回弹）。
        /// 2. 遍历最新一帧中所有玩家的权威状态(PlayerStates)。
        /// 3. 初始化(首次包)：如果本地还没记录过该玩家最大血量，以服务端第一次发来的血量为准初始化本地 UI 血条上限。
        /// 4. 【核心覆写】：直接用服务端的 Hp 强行覆盖客户端的 playerBloodValue。
        /// 5. 【兜底动画补偿】：如果发现血量减少了（挨打了），但是刚才 ApplyHitEvents 里没播过受击动画（可能 HitEvent 包丢了），
        ///    则在这里强行补播一个 "Hit" 受击动画，防止玩家莫名其妙掉血却没反馈。
        /// 6. 【权威死亡判定】：如果服务端说你死了(IsDead=true)且客户端还活着，立刻强制血量-1，置标志位 isNotDie=false，并播放死亡动画。
        /// </summary>
        public void ApplyAuthoritativeHpAndDeath(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> frames, int frameId)"""

content = content.replace(apply_auth_hp_old, apply_auth_hp_new)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Update completed.")
