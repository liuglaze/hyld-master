"""
Task 4.7 单元测试：Input Buffer 验证
模拟服务端 Battle.cs 的 Input Buffer 逻辑（Python 等价实现）

测试场景：
(a) 正常入队消费：帧号 100 的操作入队到 slot 0，frameid=100 时消费成功且 slot 变 null
(b) 缺帧补偿：slot 为 null 时验证移动值=上帧值、攻击列表为空
(c) 覆写：同一 slot 连续写入两次，验证取出的是后写入的操作
"""


# ============== 模拟数据结构 ==============

class PlayerOperation:
    """模拟 SocketProto.PlayerOperation"""
    def __init__(self, battle_id=0, move_x=0.0, move_y=0.0, attacks=None):
        self.Battleid = battle_id
        self.PlayerMoveX = move_x
        self.PlayerMoveY = move_y
        self.AttackOperations = attacks if attacks is not None else []


INPUT_BUFFER_SIZE = 2


class InputBufferSimulator:
    """
    模拟 Battle.cs 中 Input Buffer 的核心逻辑：
    - inputBuffer: Dict[int, List[Optional[PlayerOperation]]]  (环形 slot)
    - lastConsumedMove: Dict[int, Tuple[float, float]]
    - _consecutiveMissCount: Dict[int, int]
    """

    def __init__(self, player_ids):
        self.inputBuffer = {}
        self.lastConsumedMove = {}
        self._consecutiveMissCount = {}
        for pid in player_ids:
            self.inputBuffer[pid] = [None] * INPUT_BUFFER_SIZE
            self.lastConsumedMove[pid] = (0.0, 0.0)
            self._consecutiveMissCount[pid] = 0

    def write_slot(self, battle_player_id, sync_frame_id, operation):
        """
        模拟 UpdatePlayerOperation 中的 Input Buffer 写入逻辑：
        按 syncFrameId % InputBufferSize 写入 slot（覆写旧值）
        """
        if battle_player_id not in self.inputBuffer:
            return
        slots = self.inputBuffer[battle_player_id]
        slot_idx = sync_frame_id % INPUT_BUFFER_SIZE
        slot_op = PlayerOperation(
            battle_id=battle_player_id,
            move_x=operation.PlayerMoveX,
            move_y=operation.PlayerMoveY,
        )
        slots[slot_idx] = slot_op

    def consume_slot(self, battle_player_id, frameid):
        """
        模拟 CollectAndBroadcastCurrentFrame 中的消费逻辑。
        返回 (PlayerOperation, was_miss)
        """
        if battle_player_id not in self.inputBuffer:
            return None, True

        slots = self.inputBuffer[battle_player_id]
        slot_idx = frameid % INPUT_BUFFER_SIZE
        frame_op = slots[slot_idx]
        slots[slot_idx] = None  # 消费后清空

        if frame_op is not None:
            # 有效输入：重置缺帧计数，更新 lastConsumedMove
            self._consecutiveMissCount[battle_player_id] = 0
            self.lastConsumedMove[battle_player_id] = (
                frame_op.PlayerMoveX, frame_op.PlayerMoveY
            )
            return frame_op, False
        else:
            # 缺帧补偿：移动复制 lastConsumedMove，攻击为空
            mc = self._consecutiveMissCount.get(battle_player_id, 0)
            miss_count = mc + 1
            self._consecutiveMissCount[battle_player_id] = miss_count

            last_move = self.lastConsumedMove.get(battle_player_id, (0.0, 0.0))
            compensated_op = PlayerOperation(
                battle_id=battle_player_id,
                move_x=last_move[0],
                move_y=last_move[1],
                attacks=[],  # 攻击为空
            )
            return compensated_op, True


# ============== 测试用例 ==============

def test_normal_enqueue_consume():
    """
    (a) 正常入队消费：
    帧号 100 的操作入队到 slot 0 (100 % 2 = 0)，
    frameid=100 时消费成功且 slot 变 null
    """
    sim = InputBufferSimulator([1])
    op = PlayerOperation(battle_id=1, move_x=0.5, move_y=-0.3)

    # 写入 slot: syncFrameId=100, slot_idx=100%2=0
    sim.write_slot(1, 100, op)

    # 验证 slot 0 已写入
    assert sim.inputBuffer[1][0] is not None, "slot 0 should have data"
    assert sim.inputBuffer[1][0].PlayerMoveX == 0.5
    assert sim.inputBuffer[1][0].PlayerMoveY == -0.3

    # 消费 frameid=100, slot_idx=100%2=0
    result, was_miss = sim.consume_slot(1, 100)
    assert not was_miss, "Should not be a miss"
    assert result is not None
    assert result.PlayerMoveX == 0.5
    assert result.PlayerMoveY == -0.3

    # 验证 slot 已清空
    assert sim.inputBuffer[1][0] is None, "slot 0 should be null after consume"

    # 验证 lastConsumedMove 已更新
    assert sim.lastConsumedMove[1] == (0.5, -0.3)

    # 验证缺帧计数已重置
    assert sim._consecutiveMissCount[1] == 0

    print("[PASS] test_normal_enqueue_consume")


def test_normal_enqueue_slot1():
    """
    帧号 101 写入 slot 1 (101 % 2 = 1)，frameid=101 消费成功
    """
    sim = InputBufferSimulator([1])
    op = PlayerOperation(battle_id=1, move_x=0.8, move_y=0.6)

    sim.write_slot(1, 101, op)
    assert sim.inputBuffer[1][1] is not None, "slot 1 should have data"

    result, was_miss = sim.consume_slot(1, 101)
    assert not was_miss
    assert result.PlayerMoveX == 0.8
    assert result.PlayerMoveY == 0.6
    assert sim.inputBuffer[1][1] is None

    print("[PASS] test_normal_enqueue_slot1")


def test_miss_compensation_initial():
    """
    (b) 缺帧补偿 - 初始状态（lastConsumedMove=(0,0)）：
    slot 为 null 时验证移动值=上帧值(0,0)、攻击列表为空
    """
    sim = InputBufferSimulator([1])

    # 不写入任何操作，直接消费 frameid=100
    result, was_miss = sim.consume_slot(1, 100)
    assert was_miss, "Should be a miss"
    assert result is not None, "Should return compensated operation"
    assert result.PlayerMoveX == 0.0, f"Expected 0.0, got {result.PlayerMoveX}"
    assert result.PlayerMoveY == 0.0, f"Expected 0.0, got {result.PlayerMoveY}"
    assert result.AttackOperations == [], "Attack list should be empty"
    assert sim._consecutiveMissCount[1] == 1

    print("[PASS] test_miss_compensation_initial")


def test_miss_compensation_with_previous():
    """
    (b) 缺帧补偿 - 有上帧值：
    先正常消费一帧(move=0.5, 0.3)，然后缺帧，验证补偿值=上帧移动值
    """
    sim = InputBufferSimulator([1])

    # 帧 100：正常写入并消费
    op = PlayerOperation(battle_id=1, move_x=0.5, move_y=0.3)
    sim.write_slot(1, 100, op)
    sim.consume_slot(1, 100)

    # 帧 101：不写入，缺帧
    result, was_miss = sim.consume_slot(1, 101)
    assert was_miss
    assert result.PlayerMoveX == 0.5, f"Expected 0.5, got {result.PlayerMoveX}"
    assert result.PlayerMoveY == 0.3, f"Expected 0.3, got {result.PlayerMoveY}"
    assert result.AttackOperations == []
    assert sim._consecutiveMissCount[1] == 1

    print("[PASS] test_miss_compensation_with_previous")


def test_consecutive_miss_count():
    """
    连续缺帧计数器递增
    """
    sim = InputBufferSimulator([1])

    # 先正常消费一帧
    op = PlayerOperation(battle_id=1, move_x=0.7, move_y=-0.2)
    sim.write_slot(1, 100, op)
    sim.consume_slot(1, 100)
    assert sim._consecutiveMissCount[1] == 0

    # 连续缺帧
    sim.consume_slot(1, 101)
    assert sim._consecutiveMissCount[1] == 1

    sim.consume_slot(1, 102)
    assert sim._consecutiveMissCount[1] == 2

    sim.consume_slot(1, 103)
    assert sim._consecutiveMissCount[1] == 3

    # 补偿值始终是上帧(0.7, -0.2)
    result, was_miss = sim.consume_slot(1, 104)
    assert was_miss
    assert result.PlayerMoveX == 0.7
    assert result.PlayerMoveY == -0.2
    assert sim._consecutiveMissCount[1] == 4

    # 来一帧正常输入，计数器重置
    op2 = PlayerOperation(battle_id=1, move_x=0.1, move_y=0.1)
    sim.write_slot(1, 105, op2)
    result2, was_miss2 = sim.consume_slot(1, 105)
    assert not was_miss2
    assert sim._consecutiveMissCount[1] == 0

    print("[PASS] test_consecutive_miss_count")


def test_overwrite():
    """
    (c) 覆写：同一 slot 连续写入两次，验证取出的是后写入的操作
    """
    sim = InputBufferSimulator([1])

    # 帧号 100 和 102 都映射到 slot 0 (100%2=0, 102%2=0)
    op1 = PlayerOperation(battle_id=1, move_x=0.1, move_y=0.1)
    op2 = PlayerOperation(battle_id=1, move_x=0.9, move_y=0.9)

    sim.write_slot(1, 100, op1)
    sim.write_slot(1, 102, op2)  # 覆写同一 slot

    # 消费时取的应该是后写入的 op2
    result, was_miss = sim.consume_slot(1, 102)
    assert not was_miss
    assert result.PlayerMoveX == 0.9, f"Expected 0.9 (overwritten), got {result.PlayerMoveX}"
    assert result.PlayerMoveY == 0.9, f"Expected 0.9 (overwritten), got {result.PlayerMoveY}"

    print("[PASS] test_overwrite")


def test_overwrite_same_frame_different_values():
    """
    同一帧号多次写入（模拟 UDP 重传），取最后写入值
    """
    sim = InputBufferSimulator([1])

    op1 = PlayerOperation(battle_id=1, move_x=0.3, move_y=0.3)
    op2 = PlayerOperation(battle_id=1, move_x=0.7, move_y=0.7)

    sim.write_slot(1, 100, op1)
    sim.write_slot(1, 100, op2)  # 同一帧号再次写入

    result, was_miss = sim.consume_slot(1, 100)
    assert not was_miss
    assert result.PlayerMoveX == 0.7, f"Expected 0.7, got {result.PlayerMoveX}"
    assert result.PlayerMoveY == 0.7, f"Expected 0.7, got {result.PlayerMoveY}"

    print("[PASS] test_overwrite_same_frame_different_values")


def test_multi_player():
    """
    多玩家独立 buffer
    """
    sim = InputBufferSimulator([1, 2])

    # 玩家1 帧100 写入
    op1 = PlayerOperation(battle_id=1, move_x=0.5, move_y=0.5)
    sim.write_slot(1, 100, op1)

    # 玩家2 不写入

    # 消费帧100
    r1, miss1 = sim.consume_slot(1, 100)
    r2, miss2 = sim.consume_slot(2, 100)

    assert not miss1, "Player 1 should hit"
    assert r1.PlayerMoveX == 0.5

    assert miss2, "Player 2 should miss"
    assert r2.PlayerMoveX == 0.0  # 初始 lastConsumedMove
    assert r2.PlayerMoveY == 0.0

    print("[PASS] test_multi_player")


def test_ring_buffer_wraparound():
    """
    环形 buffer：验证帧号递增跨越多个周期后 slot 正确映射
    """
    sim = InputBufferSimulator([1])

    # 帧 100 -> slot 0
    op100 = PlayerOperation(battle_id=1, move_x=0.1, move_y=0.1)
    sim.write_slot(1, 100, op100)
    r, m = sim.consume_slot(1, 100)
    assert not m and r.PlayerMoveX == 0.1

    # 帧 101 -> slot 1
    op101 = PlayerOperation(battle_id=1, move_x=0.2, move_y=0.2)
    sim.write_slot(1, 101, op101)
    r, m = sim.consume_slot(1, 101)
    assert not m and r.PlayerMoveX == 0.2

    # 帧 102 -> slot 0 (又回到 slot 0)
    op102 = PlayerOperation(battle_id=1, move_x=0.3, move_y=0.3)
    sim.write_slot(1, 102, op102)
    r, m = sim.consume_slot(1, 102)
    assert not m and r.PlayerMoveX == 0.3

    # 帧 103 -> slot 1
    op103 = PlayerOperation(battle_id=1, move_x=0.4, move_y=0.4)
    sim.write_slot(1, 103, op103)
    r, m = sim.consume_slot(1, 103)
    assert not m and r.PlayerMoveX == 0.4

    print("[PASS] test_ring_buffer_wraparound")


def test_consume_does_not_affect_other_slot():
    """
    消费 slot 0 不影响 slot 1
    """
    sim = InputBufferSimulator([1])

    # 写入两个 slot
    op0 = PlayerOperation(battle_id=1, move_x=0.5, move_y=0.5)
    op1 = PlayerOperation(battle_id=1, move_x=0.8, move_y=0.8)
    sim.write_slot(1, 100, op0)  # slot 0
    sim.write_slot(1, 101, op1)  # slot 1

    # 消费 slot 0
    r, m = sim.consume_slot(1, 100)
    assert not m and r.PlayerMoveX == 0.5
    assert sim.inputBuffer[1][0] is None, "slot 0 should be null"
    assert sim.inputBuffer[1][1] is not None, "slot 1 should still have data"

    # 消费 slot 1
    r, m = sim.consume_slot(1, 101)
    assert not m and r.PlayerMoveX == 0.8
    assert sim.inputBuffer[1][1] is None, "slot 1 should be null"

    print("[PASS] test_consume_does_not_affect_other_slot")


def test_cleanup():
    """
    战斗结束清理
    """
    sim = InputBufferSimulator([1, 2])

    op = PlayerOperation(battle_id=1, move_x=0.5, move_y=0.5)
    sim.write_slot(1, 100, op)
    sim.consume_slot(1, 100)

    # 模拟 HandleBattleEnd 中的清理
    sim.inputBuffer.clear()
    sim.lastConsumedMove.clear()
    sim._consecutiveMissCount.clear()

    assert len(sim.inputBuffer) == 0
    assert len(sim.lastConsumedMove) == 0
    assert len(sim._consecutiveMissCount) == 0

    print("[PASS] test_cleanup")


if __name__ == "__main__":
    test_normal_enqueue_consume()
    test_normal_enqueue_slot1()
    test_miss_compensation_initial()
    test_miss_compensation_with_previous()
    test_consecutive_miss_count()
    test_overwrite()
    test_overwrite_same_frame_different_values()
    test_multi_player()
    test_ring_buffer_wraparound()
    test_consume_does_not_affect_other_slot()
    test_cleanup()
    print("\n=== All Task 4.7 tests PASSED ===")
