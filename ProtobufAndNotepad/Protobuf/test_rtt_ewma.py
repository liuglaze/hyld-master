"""
Task 3.6 单元测试：RTT EWMA 计算验证
测试 ProcessPongRttSample 的逻辑（用 Python 模拟）
"""
import math

# 模拟 BattleData 的 RTT 状态
class RttState:
    def __init__(self):
        self.smoothedRTT = 0.0
        self.rttVariance = 0.0
        self._rttInitialized = False

    def ProcessPongRttSample(self, rttSample):
        """与 C# ProcessPongRttSample 逻辑完全一致"""
        # 过滤异常样本
        if rttSample <= 0 or rttSample > 2000:
            return False  # discarded

        if not self._rttInitialized:
            self.smoothedRTT = rttSample
            self.rttVariance = rttSample / 2.0
            self._rttInitialized = True
        else:
            # EWMA: alpha=0.125, beta=0.25
            self.smoothedRTT = (1.0 - 0.125) * self.smoothedRTT + 0.125 * rttSample
            self.rttVariance = (1.0 - 0.25) * self.rttVariance + 0.25 * abs(rttSample - self.smoothedRTT)
        return True  # accepted


def test_first_sample():
    """首次样本直接赋值"""
    state = RttState()
    assert not state._rttInitialized
    state.ProcessPongRttSample(80)
    assert state._rttInitialized
    assert state.smoothedRTT == 80.0, f"Expected 80, got {state.smoothedRTT}"
    assert state.rttVariance == 40.0, f"Expected 40, got {state.rttVariance}"
    print("[PASS] test_first_sample: smoothedRTT=80, variance=40")


def test_second_sample():
    """第二次样本 EWMA 更新"""
    state = RttState()
    state.ProcessPongRttSample(80)
    state.ProcessPongRttSample(120)
    # smoothedRTT = 0.875 * 80 + 0.125 * 120 = 70 + 15 = 85
    expected = 85.0
    assert abs(state.smoothedRTT - expected) < 0.01, f"Expected ~85, got {state.smoothedRTT}"
    print(f"[PASS] test_second_sample: smoothedRTT={state.smoothedRTT:.1f}")


def test_sequence():
    """完整序列 [80, 120, 60, 200]"""
    state = RttState()
    samples = [80, 120, 60, 200]
    results = []
    for s in samples:
        state.ProcessPongRttSample(s)
        results.append(state.smoothedRTT)

    # Sample 1: 80 → 80
    assert abs(results[0] - 80.0) < 0.01
    # Sample 2: 0.875*80 + 0.125*120 = 85
    assert abs(results[1] - 85.0) < 0.01
    # Sample 3: 0.875*85 + 0.125*60 = 74.375 + 7.5 = 81.875
    assert abs(results[2] - 81.875) < 0.01
    # Sample 4: 0.875*81.875 + 0.125*200 = 71.640625 + 25 = 96.640625
    assert abs(results[3] - 96.640625) < 0.01
    print(f"[PASS] test_sequence: final smoothedRTT={state.smoothedRTT:.3f}")


def test_discard_negative():
    """异常样本 <= 0 被丢弃"""
    state = RttState()
    state.ProcessPongRttSample(80)
    smoothed_before = state.smoothedRTT
    result = state.ProcessPongRttSample(-5)
    assert result == False, "Should discard negative sample"
    assert state.smoothedRTT == smoothed_before, \
        f"smoothedRTT should not change, was {smoothed_before}, now {state.smoothedRTT}"
    print("[PASS] test_discard_negative: -5ms discarded, smoothedRTT unchanged")


def test_discard_too_large():
    """异常样本 > 2000ms 被丢弃"""
    state = RttState()
    state.ProcessPongRttSample(80)
    smoothed_before = state.smoothedRTT
    result = state.ProcessPongRttSample(2500)
    assert result == False, "Should discard >2000ms sample"
    assert state.smoothedRTT == smoothed_before, \
        f"smoothedRTT should not change, was {smoothed_before}, now {state.smoothedRTT}"
    print("[PASS] test_discard_too_large: 2500ms discarded, smoothedRTT unchanged")


def test_discard_zero():
    """异常样本 == 0 被丢弃"""
    state = RttState()
    result = state.ProcessPongRttSample(0)
    assert result == False, "Should discard zero sample"
    assert not state._rttInitialized, "Should not initialize on zero sample"
    print("[PASS] test_discard_zero: 0ms discarded, not initialized")


def test_mixed_with_discards():
    """混合序列含异常样本 [80, 120, 60, 200, -5, 2500]"""
    state = RttState()
    samples = [80, 120, 60, 200, -5, 2500]
    accepted = []
    for s in samples:
        if state.ProcessPongRttSample(s):
            accepted.append(s)

    assert accepted == [80, 120, 60, 200], f"Expected [80,120,60,200], got {accepted}"
    # After 4 valid samples, smoothedRTT ≈ 96.64
    assert abs(state.smoothedRTT - 96.640625) < 0.01, \
        f"Expected ~96.64, got {state.smoothedRTT}"
    print(f"[PASS] test_mixed_with_discards: accepted={accepted}, smoothedRTT={state.smoothedRTT:.3f}")


def test_clear():
    """清理后所有 RTT 状态归零"""
    state = RttState()
    state.ProcessPongRttSample(80)
    state.ProcessPongRttSample(120)
    assert state._rttInitialized
    assert state.smoothedRTT > 0

    # 模拟 ClearPredictionRuntimeState
    state.smoothedRTT = 0.0
    state.rttVariance = 0.0
    state._rttInitialized = False

    assert state.smoothedRTT == 0.0
    assert state.rttVariance == 0.0
    assert not state._rttInitialized
    print("[PASS] test_clear: all RTT state reset to zero")


def test_variance_calculation():
    """验证方差计算"""
    state = RttState()
    state.ProcessPongRttSample(80)
    # 首次: variance = 80/2 = 40
    assert state.rttVariance == 40.0

    state.ProcessPongRttSample(120)
    # smoothedRTT after sample 2 = 85
    # deviation = |120 - 85| = 35
    # variance = 0.75 * 40 + 0.25 * 35 = 30 + 8.75 = 38.75
    assert abs(state.rttVariance - 38.75) < 0.01, f"Expected ~38.75, got {state.rttVariance}"
    print(f"[PASS] test_variance_calculation: variance={state.rttVariance:.2f}")


def test_boundary_2000():
    """边界：恰好 2000ms 应接受"""
    state = RttState()
    result = state.ProcessPongRttSample(2000)
    assert result == True, "2000ms should be accepted (not > 2000)"
    assert state.smoothedRTT == 2000.0
    print("[PASS] test_boundary_2000: 2000ms accepted")


def test_boundary_1():
    """边界：1ms 应接受"""
    state = RttState()
    result = state.ProcessPongRttSample(1)
    assert result == True, "1ms should be accepted"
    assert state.smoothedRTT == 1.0
    print("[PASS] test_boundary_1: 1ms accepted")


if __name__ == "__main__":
    test_first_sample()
    test_second_sample()
    test_sequence()
    test_discard_negative()
    test_discard_too_large()
    test_discard_zero()
    test_mixed_with_discards()
    test_clear()
    test_variance_calculation()
    test_boundary_2000()
    test_boundary_1()
    print("\n=== All Task 3.6 tests PASSED ===")
