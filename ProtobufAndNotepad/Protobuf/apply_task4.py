"""Apply Task 4 (Input Buffer) edits to Battle.cs"""
import sys

filepath = 'D:/unity/hyld-master/hyld-master/Server/Server/Battle.cs'
with open(filepath, 'r', encoding='utf-8-sig') as f:
    content = f.read()

errors = []

# === 4.1: Add input buffer fields after dic_playerGameOver ===
old_4_1 = '\t\tprivate Dictionary<int, bool> dic_playerGameOver;\n\t\tprivate bool isAllReady;'

new_4_1 = (
    '\t\tprivate Dictionary<int, bool> dic_playerGameOver;\n'
    '\t\t// ── Input Buffer（动态追帧系统 Task 4） ──\n'
    '\t\tprivate const int InputBufferSize = 2;\n'
    '\t\t// key=battlePlayerId, value=PlayerOperation[InputBufferSize]（环形 slot）\n'
    '\t\tprivate Dictionary<int, PlayerOperation[]> inputBuffer;\n'
    '\t\t// 缺帧补偿：上一帧消费的移动输入\n'
    '\t\tprivate Dictionary<int, (float moveX, float moveY)> lastConsumedMove;\n'
    '\t\t// 缺帧计数器（日志限流用）\n'
    '\t\tprivate Dictionary<int, int> _consecutiveMissCount;\n'
    '\t\tprivate bool isAllReady;'
)

if old_4_1 not in content:
    errors.append('ERROR 4.1: anchor not found')
else:
    content = content.replace(old_4_1, new_4_1, 1)
    print('4.1: Input buffer fields added')

# === 4.2a: BeginBattle initialization ===
old_4_2 = '\t\t\t\tdic_lastProcessedAttackId = new Dictionary<int, int>();\n\n\t\t\t\t// ---- 初始化伤害判定系统 ----'

new_4_2 = (
    '\t\t\t\tdic_lastProcessedAttackId = new Dictionary<int, int>();\n'
    '\t\t\t\t// ── Input Buffer 初始化 ──\n'
    '\t\t\t\tinputBuffer = new Dictionary<int, PlayerOperation[]>();\n'
    '\t\t\t\tlastConsumedMove = new Dictionary<int, (float moveX, float moveY)>();\n'
    '\t\t\t\t_consecutiveMissCount = new Dictionary<int, int>();\n'
    '\n\t\t\t\t// ---- 初始化伤害判定系统 ----'
)

if old_4_2 not in content:
    errors.append('ERROR 4.2a: anchor not found')
else:
    content = content.replace(old_4_2, new_4_2, 1)
    print('4.2a: Input buffer init in BeginBattle')

# 4.2b: Per-player init
old_4_2b = (
    '\t\t\t\tforeach (int battlePlayerId in uidToBattlePlayerId.Values)\n'
    '\t\t\t\t{\n'
    '\t\t\t\t\tdic_currentFrameOperationBuffer[battlePlayerId] = null;\n'
    '\t\t\t\t\tdic_playerAckedFrameId[battlePlayerId] = 0;\n'
    '\t\t\t\t\tdic_playerGameOver[battlePlayerId] = false;\n'
    '\t\t\t\t\tdic_lastProcessedAttackId[battlePlayerId] = 0;\n'
    '\t\t\t\t}'
)

new_4_2b = (
    '\t\t\t\tforeach (int battlePlayerId in uidToBattlePlayerId.Values)\n'
    '\t\t\t\t{\n'
    '\t\t\t\t\tdic_currentFrameOperationBuffer[battlePlayerId] = null;\n'
    '\t\t\t\t\tdic_playerAckedFrameId[battlePlayerId] = 0;\n'
    '\t\t\t\t\tdic_playerGameOver[battlePlayerId] = false;\n'
    '\t\t\t\t\tdic_lastProcessedAttackId[battlePlayerId] = 0;\n'
    '\t\t\t\t\t// Input Buffer per-player init\n'
    '\t\t\t\t\tinputBuffer[battlePlayerId] = new PlayerOperation[InputBufferSize];\n'
    '\t\t\t\t\tlastConsumedMove[battlePlayerId] = (0f, 0f);\n'
    '\t\t\t\t\t_consecutiveMissCount[battlePlayerId] = 0;\n'
    '\t\t\t\t}'
)

if old_4_2b not in content:
    errors.append('ERROR 4.2b: anchor not found')
else:
    content = content.replace(old_4_2b, new_4_2b, 1)
    print('4.2b: Per-player init in BeginBattle')

# === 4.3: UpdatePlayerOperation - add buffer slot write ===
old_4_3_anchor = '\t\t\t\tbufferedOperation.PlayerMoveX = operation.PlayerMoveX;\n\t\t\t\tbufferedOperation.PlayerMoveY = operation.PlayerMoveY;\n\n\t\t\t\tif (operation.AttackOperations != null'

new_4_3_anchor = (
    '\t\t\t\tbufferedOperation.PlayerMoveX = operation.PlayerMoveX;\n'
    '\t\t\t\tbufferedOperation.PlayerMoveY = operation.PlayerMoveY;\n'
    '\n'
    '\t\t\t\t// ── Input Buffer: 按客户端帧号写入环形 slot ──\n'
    '\t\t\t\tif (inputBuffer.TryGetValue(battlePlayerId, out PlayerOperation[] slots))\n'
    '\t\t\t\t{\n'
    '\t\t\t\t\tint slotIdx = syncFrameId % InputBufferSize;\n'
    '\t\t\t\t\tPlayerOperation slotOp = new PlayerOperation { Battleid = battlePlayerId };\n'
    '\t\t\t\t\tslotOp.PlayerMoveX = operation.PlayerMoveX;\n'
    '\t\t\t\t\tslotOp.PlayerMoveY = operation.PlayerMoveY;\n'
    '\t\t\t\t\tslots[slotIdx] = slotOp;\n'
    '\t\t\t\t}\n'
    '\n'
    '\t\t\t\tif (operation.AttackOperations != null'
)

if old_4_3_anchor not in content:
    errors.append('ERROR 4.3: anchor not found')
else:
    content = content.replace(old_4_3_anchor, new_4_3_anchor, 1)
    print('4.3: Input buffer slot write in UpdatePlayerOperation')

# === 4.4: CollectAndBroadcastCurrentFrame - consume from buffer ===
old_4_4 = (
    '\t\tprivate void CollectAndBroadcastCurrentFrame()\n'
    '\t\t{\n'
    '\t\t\tAllPlayerOperation nextFrameOp = new AllPlayerOperation();\n'
    '\t\t\ttry\n'
    '\t\t\t{\n'
    '\t\t\t\tforeach (int battlePlayerId in uidToBattlePlayerId.Values)\n'
    '\t\t\t\t{\n'
    '\t\t\t\t\tif (dic_currentFrameOperationBuffer.TryGetValue(battlePlayerId, out PlayerOperation operation) && operation != null)\n'
    '\t\t\t\t\t{\n'
    '\t\t\t\t\t\tnextFrameOp.Operations.Add(operation);\n'
    '\t\t\t\t\t}\n'
    '\t\t\t\t}\n'
    '\t\t\t}\n'
    '\t\t\tcatch (Exception ex)\n'
    '\t\t\t{\n'
    '\t\t\t\tLogging.Debug.Log(ex);\n'
    '\t\t\t\tnextFrameOp = new AllPlayerOperation();\n'
    '\t\t\t}'
)

new_4_4 = (
    '\t\tprivate void CollectAndBroadcastCurrentFrame()\n'
    '\t\t{\n'
    '\t\t\tAllPlayerOperation nextFrameOp = new AllPlayerOperation();\n'
    '\t\t\ttry\n'
    '\t\t\t{\n'
    '\t\t\t\tforeach (int battlePlayerId in uidToBattlePlayerId.Values)\n'
    '\t\t\t\t{\n'
    '\t\t\t\t\tPlayerOperation frameOp = null;\n'
    '\n'
    '\t\t\t\t\t// ── 从 Input Buffer 按帧号消费 ──\n'
    '\t\t\t\t\tif (inputBuffer.TryGetValue(battlePlayerId, out PlayerOperation[] bufSlots))\n'
    '\t\t\t\t\t{\n'
    '\t\t\t\t\t\tint slotIdx = frameid % InputBufferSize;\n'
    '\t\t\t\t\t\tframeOp = bufSlots[slotIdx];\n'
    '\t\t\t\t\t\tbufSlots[slotIdx] = null; // 消费后清空\n'
    '\n'
    '\t\t\t\t\t\tif (frameOp != null)\n'
    '\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\t// 有效输入：重置缺帧计数，更新 lastConsumedMove\n'
    '\t\t\t\t\t\t\t_consecutiveMissCount[battlePlayerId] = 0;\n'
    '\t\t\t\t\t\t\tlastConsumedMove[battlePlayerId] = (frameOp.PlayerMoveX, frameOp.PlayerMoveY);\n'
    '\t\t\t\t\t\t}\n'
    '\t\t\t\t\t\telse\n'
    '\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\t// ── 缺帧补偿：移动复制 lastConsumedMove，攻击为空 ──\n'
    '\t\t\t\t\t\t\tint missCount = _consecutiveMissCount.TryGetValue(battlePlayerId, out int mc) ? mc + 1 : 1;\n'
    '\t\t\t\t\t\t\t_consecutiveMissCount[battlePlayerId] = missCount;\n'
    '\n'
    '\t\t\t\t\t\t\t// Buffer miss 日志（限流：每 60 帧最多一次汇总）\n'
    '\t\t\t\t\t\t\tif (frameid % 60 == 0 || missCount == 1)\n'
    '\t\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\t\tLogging.Debug.Log($"[InputBuffer] MISS bp={battlePlayerId} frame={frameid} consecutive={missCount}");\n'
    '\t\t\t\t\t\t\t}\n'
    '\n'
    '\t\t\t\t\t\t\tvar lastMove = lastConsumedMove.TryGetValue(battlePlayerId, out var lm) ? lm : (0f, 0f);\n'
    '\t\t\t\t\t\t\tframeOp = new PlayerOperation { Battleid = battlePlayerId };\n'
    '\t\t\t\t\t\t\tframeOp.PlayerMoveX = lastMove.moveX;\n'
    '\t\t\t\t\t\t\tframeOp.PlayerMoveY = lastMove.moveY;\n'
    '\t\t\t\t\t\t}\n'
    '\t\t\t\t\t}\n'
    '\t\t\t\t\telse\n'
    '\t\t\t\t\t{\n'
    '\t\t\t\t\t\t// 回退到旧逻辑（不应到达，但兜底）\n'
    '\t\t\t\t\t\tif (dic_currentFrameOperationBuffer.TryGetValue(battlePlayerId, out PlayerOperation operation) && operation != null)\n'
    '\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\tframeOp = operation;\n'
    '\t\t\t\t\t\t}\n'
    '\t\t\t\t\t}\n'
    '\n'
    '\t\t\t\t\t// 合并攻击操作：从 dic_currentFrameOperationBuffer 取攻击（去重后的权威攻击）\n'
    '\t\t\t\t\tif (dic_currentFrameOperationBuffer.TryGetValue(battlePlayerId, out PlayerOperation attackBuf) && attackBuf != null\n'
    '\t\t\t\t\t\t&& attackBuf.AttackOperations != null && attackBuf.AttackOperations.Count > 0)\n'
    '\t\t\t\t\t{\n'
    '\t\t\t\t\t\tif (frameOp == null)\n'
    '\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\tframeOp = new PlayerOperation { Battleid = battlePlayerId };\n'
    '\t\t\t\t\t\t}\n'
    '\t\t\t\t\t\tforeach (var atk in attackBuf.AttackOperations)\n'
    '\t\t\t\t\t\t{\n'
    '\t\t\t\t\t\t\tframeOp.AttackOperations.Add(atk);\n'
    '\t\t\t\t\t\t}\n'
    '\t\t\t\t\t}\n'
    '\n'
    '\t\t\t\t\tif (frameOp != null)\n'
    '\t\t\t\t\t{\n'
    '\t\t\t\t\t\tnextFrameOp.Operations.Add(frameOp);\n'
    '\t\t\t\t\t}\n'
    '\t\t\t\t}\n'
    '\t\t\t}\n'
    '\t\t\tcatch (Exception ex)\n'
    '\t\t\t{\n'
    '\t\t\t\tLogging.Debug.Log(ex);\n'
    '\t\t\t\tnextFrameOp = new AllPlayerOperation();\n'
    '\t\t\t}'
)

if old_4_4 not in content:
    errors.append('ERROR 4.4: anchor not found')
else:
    content = content.replace(old_4_4, new_4_4, 1)
    print('4.4: Input buffer consume in CollectAndBroadcastCurrentFrame')

# === 4.6: HandleBattleEnd cleanup ===
old_4_6 = '\t\t\t\t// 清理子弹和位置历史\n\t\t\t\tactiveBullets?.Clear();\n\t\t\t\tpositionHistory?.Clear();\n\t\t\t\tpositionHistoryOrder?.Clear();'

new_4_6 = (
    '\t\t\t\t// 清理子弹和位置历史\n'
    '\t\t\t\tactiveBullets?.Clear();\n'
    '\t\t\t\tpositionHistory?.Clear();\n'
    '\t\t\t\tpositionHistoryOrder?.Clear();\n'
    '\t\t\t\t// ── Input Buffer 清理 ──\n'
    '\t\t\t\tinputBuffer?.Clear();\n'
    '\t\t\t\tlastConsumedMove?.Clear();\n'
    '\t\t\t\t_consecutiveMissCount?.Clear();'
)

if old_4_6 not in content:
    errors.append('ERROR 4.6: anchor not found')
else:
    content = content.replace(old_4_6, new_4_6, 1)
    print('4.6: Input buffer cleanup in HandleBattleEnd')

if errors:
    for e in errors:
        print(e)
    sys.exit(1)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
print('\nAll Task 4 edits applied to Battle.cs')
