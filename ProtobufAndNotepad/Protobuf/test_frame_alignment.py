"""
Task 7.3 单元测试：帧号对齐（uploadOperationId 语义 + 服务端 Ack 钳位）
"""
import unittest


# ── 客户端公式 ──

def client_upload_operation_id(predicted_frame_id: int) -> int:
    """
    Task 7 后，uploadOperationId = nextFrame = predicted_frameID + 1
    （BattleTick 在 CommitPredictedFrame 之前设 uploadOperationId = nextFrame）
    """
    next_frame = predicted_frame_id + 1
    return next_frame


# ── 服务端公式（镜像 Battle.cs UpdatePlayerOperation） ──

def server_ack_clamp(sync_frame_id: int, server_frameid: int) -> int:
    """
    服务端 Ack 钳位逻辑：
    1. incomingAckFrameId = syncFrameId - 1
    2. ackUpperBound = max(0, frameid - 1)
    3. clamp: if incoming > upper → incoming = upper
    4. lower bound: if incoming < 0 → incoming = 0
    """
    incoming_ack = sync_frame_id - 1
    ack_upper = max(0, server_frameid - 1)
    if incoming_ack > ack_upper:
        incoming_ack = ack_upper
    if incoming_ack < 0:
        incoming_ack = 0
    return incoming_ack


class TestUploadOperationId(unittest.TestCase):
    """(a) 客户端上报帧号"""

    def test_basic(self):
        """predicted=104 → uploadOperationId=105"""
        result = client_upload_operation_id(104)
        self.assertEqual(result, 105)

    def test_predicted_equals_sync(self):
        """predicted=98, sync=98 → uploadOperationId=99
           退化到改动前 sync+1 的行为"""
        # 当 predicted == sync 时（没有超前），等价于旧行为
        predicted = 98
        result = client_upload_operation_id(predicted)
        self.assertEqual(result, 99)

    def test_predicted_ahead_of_sync(self):
        """predicted=105, sync=98 → uploadOperationId=106"""
        result = client_upload_operation_id(105)
        self.assertEqual(result, 106)

    def test_first_frame(self):
        """predicted=0 → uploadOperationId=1"""
        result = client_upload_operation_id(0)
        self.assertEqual(result, 1)


class TestServerAckClamp(unittest.TestCase):
    """(b) 服务端 Ack 钳位"""

    def test_client_ahead_of_server(self):
        """客户端 predicted=105, uploadId=106, 服务端 frameid=100
           incoming=105, upper=99 → clamp to 99"""
        upload_id = client_upload_operation_id(105)  # 106
        ack = server_ack_clamp(upload_id, 100)
        self.assertEqual(ack, 99)

    def test_client_matches_server(self):
        """客户端 predicted=99, uploadId=100, 服务端 frameid=100
           incoming=99, upper=99 → 99 (不需要钳位)"""
        upload_id = client_upload_operation_id(99)  # 100
        ack = server_ack_clamp(upload_id, 100)
        self.assertEqual(ack, 99)

    def test_client_behind_server(self):
        """客户端 predicted=95, uploadId=96, 服务端 frameid=100
           incoming=95, upper=99 → 95 (不需要钳位)"""
        upload_id = client_upload_operation_id(95)  # 96
        ack = server_ack_clamp(upload_id, 100)
        self.assertEqual(ack, 95)

    def test_first_frame_server(self):
        """服务端 frameid=1, 客户端 uploadId=1
           incoming=0, upper=0 → 0"""
        ack = server_ack_clamp(1, 1)
        self.assertEqual(ack, 0)

    def test_server_frameid_zero(self):
        """服务端 frameid=0 (未启动), uploadId=1
           incoming=0, upper=0 → 0"""
        ack = server_ack_clamp(1, 0)
        self.assertEqual(ack, 0)

    def test_negative_protection(self):
        """uploadId=0 → incoming=-1 → clamp to 0"""
        ack = server_ack_clamp(0, 10)
        self.assertEqual(ack, 0)


class TestEndToEndScenario(unittest.TestCase):
    """(c) 端到端场景验证"""

    def test_dynamic_tick_ahead_scenario(self):
        """动态追帧：predicted=105, sync=98, server_frameid=100
           客户端上报 106，服务端安全钳位到 99"""
        predicted = 105
        upload_id = client_upload_operation_id(predicted)
        self.assertEqual(upload_id, 106)

        server_ack = server_ack_clamp(upload_id, 100)
        self.assertEqual(server_ack, 99)

    def test_steady_state(self):
        """稳态：predicted=102, sync=100, server_frameid=101
           上报 103, 服务端 clamp 到 100"""
        upload_id = client_upload_operation_id(102)  # 103
        server_ack = server_ack_clamp(upload_id, 101)
        self.assertEqual(server_ack, 100)

    def test_monotonic_ack_advance(self):
        """Ack 单调递增：连续多帧 ack 不回退"""
        last_ack = -1
        for server_frame in range(1, 20):
            predicted = server_frame + 3  # 客户端超前3帧
            upload_id = client_upload_operation_id(predicted)
            ack = server_ack_clamp(upload_id, server_frame)
            self.assertGreaterEqual(ack, last_ack,
                                    f"Ack went backwards at server_frame={server_frame}")
            last_ack = ack


if __name__ == '__main__':
    unittest.main(verbosity=2)
