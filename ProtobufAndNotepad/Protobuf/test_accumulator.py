"""
Task 5.7 单元测试：累加器驱动验证
模拟 BattleManger.Update() 的累加器循环逻辑（Python 等价实现）

测试场景：
(a) 启动战斗后 _battleTickActive=true, 不使用 InvokeRepeating
(b) deltaTime=0.048f → BattleTick 调用 3 次 (0.048/0.016=3)
(c) deltaTime=0.010f → BattleTick 不被调用，accumulator 保留 0.010f
(d) maxCatchupPerUpdate=3 钳位
(e) 余量保留跨帧
"""


class AccumulatorSimulator:
    """模拟 BattleManger 的累加器逻辑"""

    def __init__(self):
        self._tickAccumulator = 0.0
        self.currentTickInterval = 0.016
        self._battleTickActive = False
        self.isGameOver = False
        self.maxCatchupPerUpdate = 3
        self._tickCount = 0  # 用于追踪 BattleTick 调用次数

    def start_battle(self):
        """模拟收到 BattleStart 后的初始化"""
        self._tickAccumulator = 0.0
        self.currentTickInterval = 0.016
        self._battleTickActive = True

    def stop_battle(self):
        """模拟 BeginGameOver"""
        self._battleTickActive = False
        self._tickAccumulator = 0.0
        self.currentTickInterval = 0.016

    def battle_tick(self):
        """模拟 BattleTick()"""
        self._tickCount += 1

    def update(self, deltaTime):
        """
        模拟 Update() 的累加器循环。
        返回本次 Update 中 BattleTick 被调用的次数。
        """
        if not self._battleTickActive or self.isGameOver:
            return 0

        # 管线步骤 1-3: DrainAndDispatch, Ping, CalcTarget (省略)

        # 管线步骤 4: 累加器循环
        self._tickAccumulator += deltaTime

        # 防弹簇钳位
        maxAccum = self.currentTickInterval * self.maxCatchupPerUpdate
        if self._tickAccumulator > maxAccum:
            self._tickAccumulator = maxAccum

        count_before = self._tickCount
        while self._tickAccumulator >= self.currentTickInterval:
            self.battle_tick()
            self._tickAccumulator -= self.currentTickInterval

        return self._tickCount - count_before


# ============== 测试用例 ==============

def test_battle_start_activates():
    """(a) 启动战斗后 _battleTickActive=true"""
    sim = AccumulatorSimulator()
    assert not sim._battleTickActive

    sim.start_battle()
    assert sim._battleTickActive
    assert sim._tickAccumulator == 0.0
    assert sim.currentTickInterval == 0.016

    print("[PASS] test_battle_start_activates")


def test_no_invoke_repeating():
    """(a) 验证不使用 InvokeRepeating（累加器通过 Update 驱动）"""
    # 这个测试验证概念：BattleTick 只通过 update() 调用
    sim = AccumulatorSimulator()
    sim.start_battle()

    # 不调用 update → BattleTick 不应被调用
    assert sim._tickCount == 0

    # 调用 update → BattleTick 通过累加器调用
    sim.update(0.016)
    assert sim._tickCount == 1

    print("[PASS] test_no_invoke_repeating")


def test_three_ticks_048():
    """(b) deltaTime=0.048f → BattleTick 调用 3 次"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    ticks = sim.update(0.048)
    assert ticks == 3, f"Expected 3 ticks, got {ticks}"
    # 余量应该是 0.048 - 3*0.016 = 0.0
    assert abs(sim._tickAccumulator) < 1e-9, f"Expected ~0 remainder, got {sim._tickAccumulator}"

    print("[PASS] test_three_ticks_048")


def test_no_tick_010():
    """(c) deltaTime=0.010f → BattleTick 不被调用，accumulator 保留 0.010f"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    ticks = sim.update(0.010)
    assert ticks == 0, f"Expected 0 ticks, got {ticks}"
    assert abs(sim._tickAccumulator - 0.010) < 1e-9, \
        f"Expected accumulator=0.010, got {sim._tickAccumulator}"

    print("[PASS] test_no_tick_010")


def test_remainder_carries_over():
    """(e) 余量保留跨帧"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    # Frame 1: deltaTime=0.010 → 0 ticks, accumulator=0.010
    ticks1 = sim.update(0.010)
    assert ticks1 == 0
    assert abs(sim._tickAccumulator - 0.010) < 1e-9

    # Frame 2: deltaTime=0.010 → accumulator=0.020 → 1 tick, remainder=0.004
    ticks2 = sim.update(0.010)
    assert ticks2 == 1, f"Expected 1 tick, got {ticks2}"
    assert abs(sim._tickAccumulator - 0.004) < 1e-6, \
        f"Expected accumulator≈0.004, got {sim._tickAccumulator}"

    print("[PASS] test_remainder_carries_over")


def test_max_catchup_clamp():
    """(d) maxCatchupPerUpdate=3 钳位"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    # deltaTime = 0.100 → 正常应该 6.25 次 tick，但钳位到 3
    ticks = sim.update(0.100)
    assert ticks == 3, f"Expected 3 ticks (clamped), got {ticks}"
    # 钳位后 accumulator = 3*0.016 - 3*0.016 = 0
    assert abs(sim._tickAccumulator) < 1e-9, \
        f"Expected ~0 remainder after clamp, got {sim._tickAccumulator}"

    print("[PASS] test_max_catchup_clamp")


def test_inactive_no_ticks():
    """_battleTickActive=false 时不调用 BattleTick"""
    sim = AccumulatorSimulator()
    # 没有 start_battle
    ticks = sim.update(0.050)
    assert ticks == 0
    assert sim._tickAccumulator == 0.0  # accumulator 不应被修改

    print("[PASS] test_inactive_no_ticks")


def test_game_over_no_ticks():
    """IsGameOver=true 时不调用 BattleTick"""
    sim = AccumulatorSimulator()
    sim.start_battle()
    sim.isGameOver = True

    ticks = sim.update(0.050)
    assert ticks == 0

    print("[PASS] test_game_over_no_ticks")


def test_stop_battle_resets():
    """BeginGameOver 重置累加器状态"""
    sim = AccumulatorSimulator()
    sim.start_battle()
    sim.update(0.010)  # accumulator = 0.010

    sim.stop_battle()
    assert not sim._battleTickActive
    assert sim._tickAccumulator == 0.0
    assert sim.currentTickInterval == 0.016

    print("[PASS] test_stop_battle_resets")


def test_variable_tick_interval():
    """currentTickInterval 可变时累加器正确工作（为 Task 6 准备）"""
    sim = AccumulatorSimulator()
    sim.start_battle()
    sim.currentTickInterval = 0.020  # 加速后 tick 间隔变大

    # deltaTime=0.050 → 0.050/0.020 = 2.5 → 2 ticks, remainder=0.010
    ticks = sim.update(0.050)
    assert ticks == 2, f"Expected 2 ticks, got {ticks}"
    assert abs(sim._tickAccumulator - 0.010) < 1e-9, \
        f"Expected remainder≈0.010, got {sim._tickAccumulator}"

    print("[PASS] test_variable_tick_interval")


def test_exact_boundary():
    """deltaTime 恰好等于 tickInterval"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    ticks = sim.update(0.016)
    assert ticks == 1, f"Expected 1 tick, got {ticks}"
    assert abs(sim._tickAccumulator) < 1e-9

    print("[PASS] test_exact_boundary")


def test_two_ticks_032():
    """deltaTime=0.032 → 2 ticks"""
    sim = AccumulatorSimulator()
    sim.start_battle()

    ticks = sim.update(0.032)
    assert ticks == 2, f"Expected 2 ticks, got {ticks}"
    assert abs(sim._tickAccumulator) < 1e-9

    print("[PASS] test_two_ticks_032")


if __name__ == "__main__":
    test_battle_start_activates()
    test_no_invoke_repeating()
    test_three_ticks_048()
    test_no_tick_010()
    test_remainder_carries_over()
    test_max_catchup_clamp()
    test_inactive_no_ticks()
    test_game_over_no_ticks()
    test_stop_battle_resets()
    test_variable_tick_interval()
    test_exact_boundary()
    test_two_ticks_032()
    print("\n=== All Task 5.7 tests PASSED ===")
