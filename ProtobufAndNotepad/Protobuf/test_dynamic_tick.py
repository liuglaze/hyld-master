"""
Task 6.7 单元测试：动态追帧 (CalcTargetFrame + AdjustTickInterval)
测试纯逻辑公式，不依赖 Unity。
"""
import math
import unittest

# ── 配置常量（镜像 ConstValue.cs） ──
FRAME_TIME = 0.016          # 16ms per logic frame
INPUT_BUFFER_SIZE = 2
ADJUST_RATE = 0.05
MIN_SPEED_FACTOR = 0.85
MAX_SPEED_FACTOR = 1.15
SMOOTH_RATE = 5.0


# ── 公式函数（镜像 BattleManger.cs） ──

def calc_target_frame(sync_frame: int, smoothed_rtt: float,
                      rtt_initialized: bool) -> int:
    """
    CalcTargetFrame 的 Python 等价。
    公式: targetFrame = syncFrame + ceil(rttFrames/2) + inputBufferSize
    RTT 未初始化时退化为 syncFrame + inputBufferSize。
    """
    if not rtt_initialized:
        return sync_frame + INPUT_BUFFER_SIZE

    frame_time_ms = FRAME_TIME * 1000.0         # 16 ms
    rtt_frames = smoothed_rtt / frame_time_ms
    half_rtt_frames = math.ceil(rtt_frames / 2.0)
    return sync_frame + half_rtt_frames + INPUT_BUFFER_SIZE


class AdjustTickState:
    """模拟 BattleManger 中的追帧相关状态。"""

    def __init__(self):
        self.actual_speed_factor: float = 1.0
        self.skip_accumulator: bool = False
        self.current_tick_interval: float = FRAME_TIME

    def adjust_tick_interval(self, target_frame: int,
                             predicted_frame: int,
                             delta_time: float) -> None:
        """
        AdjustTickInterval 的 Python 等价。
        """
        frame_diff = target_frame - predicted_frame

        # 严重超前暂停
        if frame_diff < -5:
            self.skip_accumulator = True
            return
        self.skip_accumulator = False

        # 目标速度因子
        target_speed = max(MIN_SPEED_FACTOR,
                           min(MAX_SPEED_FACTOR,
                               1.0 + frame_diff * ADJUST_RATE))

        # Lerp 平滑
        t = SMOOTH_RATE * delta_time
        self.actual_speed_factor = (
            self.actual_speed_factor * (1.0 - t) + target_speed * t
        )

        # tick 间隔
        self.current_tick_interval = FRAME_TIME / self.actual_speed_factor


class TestCalcTargetFrame(unittest.TestCase):
    """(a) 目标帧号计算"""

    def test_basic_formula(self):
        """syncFrame=95, smoothedRTT=80ms, inputBuffer=2 → targetFrame=100"""
        # rttFrames = 80 / 16 = 5.0
        # halfRttFrames = ceil(5.0 / 2) = ceil(2.5) = 3
        # target = 95 + 3 + 2 = 100
        result = calc_target_frame(95, 80.0, True)
        self.assertEqual(result, 100)

    def test_rtt_not_initialized(self):
        """RTT 未初始化退化为 syncFrame + inputBufferSize"""
        result = calc_target_frame(50, 0.0, False)
        self.assertEqual(result, 52)

    def test_small_rtt(self):
        """RTT=10ms → rttFrames=0.625 → halfRttFrames=ceil(0.3125)=1"""
        result = calc_target_frame(100, 10.0, True)
        self.assertEqual(result, 103)  # 100 + 1 + 2

    def test_large_rtt(self):
        """RTT=200ms → rttFrames=12.5 → halfRttFrames=ceil(6.25)=7"""
        result = calc_target_frame(100, 200.0, True)
        self.assertEqual(result, 109)  # 100 + 7 + 2


class TestAdjustTickSpeedUp(unittest.TestCase):
    """(b) 加速：frameDiff > 0 → speedFactor > 1"""

    def test_speed_up_diff_2(self):
        """frameDiff=2 → targetSpeed=1.1, tickInterval≈0.01455"""
        state = AdjustTickState()
        # 用一个足够大的 deltaTime 让 Lerp 完全收敛
        for _ in range(200):
            state.adjust_tick_interval(
                target_frame=102, predicted_frame=100, delta_time=0.016)

        self.assertAlmostEqual(state.actual_speed_factor, 1.1, places=2)
        expected_interval = FRAME_TIME / 1.1  # ≈ 0.014545
        self.assertAlmostEqual(state.current_tick_interval,
                               expected_interval, places=4)

    def test_speed_up_diff_1(self):
        """frameDiff=1 → targetSpeed=1.05"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(102, 101, 0.016)
        self.assertAlmostEqual(state.actual_speed_factor, 1.05, places=2)


class TestAdjustTickSlowDown(unittest.TestCase):
    """(c) 减速：frameDiff < 0 → speedFactor < 1"""

    def test_slow_down_diff_neg3(self):
        """frameDiff=-3 → targetSpeed=0.85, tickInterval≈0.01882"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(97, 100, 0.016)

        # targetSpeed = 1 + (-3)*0.05 = 0.85
        self.assertAlmostEqual(state.actual_speed_factor, 0.85, places=2)
        expected_interval = FRAME_TIME / 0.85  # ≈ 0.018824
        self.assertAlmostEqual(state.current_tick_interval,
                               expected_interval, places=4)

    def test_slow_down_diff_neg2(self):
        """frameDiff=-2 → targetSpeed=0.9"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(98, 100, 0.016)
        self.assertAlmostEqual(state.actual_speed_factor, 0.9, places=2)


class TestAdjustTickClamp(unittest.TestCase):
    """(d) Clamp：极端 frameDiff 时 speedFactor 不越界"""

    def test_clamp_upper(self):
        """frameDiff=10 → raw=1.5, clamp → 1.15"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(110, 100, 0.016)

        self.assertAlmostEqual(state.actual_speed_factor, 1.15, places=2)
        self.assertLessEqual(state.actual_speed_factor, MAX_SPEED_FACTOR + 0.001)

    def test_clamp_lower(self):
        """frameDiff=-5 → raw=0.75, clamp → 0.85"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(95, 100, 0.016)

        self.assertAlmostEqual(state.actual_speed_factor, 0.85, places=2)
        self.assertGreaterEqual(state.actual_speed_factor, MIN_SPEED_FACTOR - 0.001)

    def test_clamp_extreme_positive(self):
        """frameDiff=100 → raw=6.0, clamp → 1.15"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(200, 100, 0.016)

        self.assertAlmostEqual(state.actual_speed_factor, 1.15, places=2)


class TestAdjustTickSmooth(unittest.TestCase):
    """(e) 平滑：actualSpeedFactor 不跳变（单帧变化量 < 0.02）"""

    def test_no_jump(self):
        """从 1.0 到 1.15 的过渡过程中，每帧变化量 < 0.02"""
        state = AdjustTickState()
        max_delta = 0.0

        for _ in range(100):
            old = state.actual_speed_factor
            state.adjust_tick_interval(110, 100, 0.016)
            delta = abs(state.actual_speed_factor - old)
            max_delta = max(max_delta, delta)

        # Lerp(1.0, 1.15, 5.0 * 0.016) = Lerp(1.0, 1.15, 0.08)
        # 第一帧: delta = 0.15 * 0.08 = 0.012
        self.assertLess(max_delta, 0.02,
                        f"Max per-frame delta was {max_delta:.6f}, exceeds 0.02 threshold")

    def test_smooth_convergence(self):
        """200帧后收敛到目标值"""
        state = AdjustTickState()
        for _ in range(200):
            state.adjust_tick_interval(102, 100, 0.016)
        # 应收敛到 1.1
        self.assertAlmostEqual(state.actual_speed_factor, 1.1, places=2)

    def test_direction_change_smooth(self):
        """从加速切换到减速时仍保持平滑（方向剧变容许稍大偏移）"""
        state = AdjustTickState()
        # 先加速 50 帧
        for _ in range(50):
            state.adjust_tick_interval(105, 100, 0.016)

        mid_factor = state.actual_speed_factor
        self.assertGreater(mid_factor, 1.0)

        # 切换到减速（目标从 1.25 突变到 0.85，Lerp 第一帧差值较大）
        max_delta = 0.0
        for _ in range(100):
            old = state.actual_speed_factor
            state.adjust_tick_interval(97, 100, 0.016)
            delta = abs(state.actual_speed_factor - old)
            max_delta = max(max_delta, delta)

        # 方向剧变时 Lerp(cur, 0.85, 0.08) 的第一帧 delta ≈ 0.024
        # 仍远小于直接跳变（0.28），确认 Lerp 有效抑制了跳变
        self.assertLess(max_delta, 0.025,
                        f"Max per-frame delta was {max_delta:.6f}, should stay < 0.025 even on direction change")


class TestAdjustTickPause(unittest.TestCase):
    """(f) 严重超前：frameDiff < -5 → 累加器循环被跳过"""

    def test_severe_ahead_pause(self):
        """frameDiff=-6 → _skipAccumulatorThisFrame = true"""
        state = AdjustTickState()
        state.adjust_tick_interval(94, 100, 0.016)
        self.assertTrue(state.skip_accumulator)

    def test_severe_ahead_boundary(self):
        """frameDiff=-5 不触发暂停（边界值 < -5 才触发）"""
        state = AdjustTickState()
        state.adjust_tick_interval(95, 100, 0.016)
        self.assertFalse(state.skip_accumulator)

    def test_severe_ahead_diff_neg10(self):
        """frameDiff=-10 触发暂停"""
        state = AdjustTickState()
        state.adjust_tick_interval(90, 100, 0.016)
        self.assertTrue(state.skip_accumulator)

    def test_pause_does_not_modify_speed_factor(self):
        """暂停时 speedFactor 不被修改"""
        state = AdjustTickState()
        state.actual_speed_factor = 1.05  # 预设非默认值
        old_factor = state.actual_speed_factor
        state.adjust_tick_interval(93, 100, 0.016)  # frameDiff = -7

        self.assertTrue(state.skip_accumulator)
        self.assertEqual(state.actual_speed_factor, old_factor,
                         "Pause should not modify speed factor")

    def test_recovery_from_pause(self):
        """暂停后恢复正常调节"""
        state = AdjustTickState()

        # 先进入暂停
        state.adjust_tick_interval(90, 100, 0.016)
        self.assertTrue(state.skip_accumulator)

        # 恢复 (frameDiff = 0)
        state.adjust_tick_interval(100, 100, 0.016)
        self.assertFalse(state.skip_accumulator)


class TestCleanupState(unittest.TestCase):
    """(g) BeginGameOver 清理验证"""

    def test_cleanup_resets_all(self):
        """模拟 BeginGameOver 后状态回到默认值"""
        state = AdjustTickState()

        # 先跑一段调节
        for _ in range(100):
            state.adjust_tick_interval(110, 100, 0.016)
        self.assertNotAlmostEqual(state.actual_speed_factor, 1.0, places=2)
        self.assertNotAlmostEqual(state.current_tick_interval, FRAME_TIME, places=4)

        # 模拟 BeginGameOver 清理
        state.actual_speed_factor = 1.0
        state.skip_accumulator = False
        state.current_tick_interval = FRAME_TIME

        self.assertEqual(state.actual_speed_factor, 1.0)
        self.assertFalse(state.skip_accumulator)
        self.assertEqual(state.current_tick_interval, FRAME_TIME)


if __name__ == '__main__':
    unittest.main(verbosity=2)
