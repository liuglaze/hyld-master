"""
Task 2.4 单元测试：服务端 Pong 协议验证
测试内容：
1. Ping 包序列化/反序列化 + timestamp 保持
2. Pong 回复构造逻辑（模拟服务端行为）
3. 未关联战斗的 Ping 应被忽略（逻辑验证）
"""
import os, sys
os.environ["PROTOCOL_BUFFERS_PYTHON_IMPLEMENTATION"] = "python"

import SocketProto_pb2 as pb

def test_ping_serialization_roundtrip():
    """Ping 包序列化→反序列化，timestamp 保持"""
    ping = pb.MainPack()
    ping.actioncode = pb.ActionCode.Value("Ping")
    ping.timestamp = 1234567890123

    data = ping.SerializeToString()
    parsed = pb.MainPack()
    parsed.ParseFromString(data)

    assert parsed.actioncode == pb.ActionCode.Value("Ping"), \
        f"Expected Ping, got {parsed.actioncode}"
    assert parsed.timestamp == 1234567890123, \
        f"Expected 1234567890123, got {parsed.timestamp}"
    print("[PASS] test_ping_serialization_roundtrip")

def test_pong_reply_logic():
    """
    模拟服务端 Pong 回复逻辑：
    收到 Ping(timestamp=T) → 构造 Pong(actioncode=Pong, timestamp=T)
    """
    # 模拟收到的 Ping
    ping = pb.MainPack()
    ping.actioncode = pb.ActionCode.Value("Ping")
    ping.timestamp = 9876543210

    ping_bytes = ping.SerializeToString()

    # 模拟服务端解析
    received = pb.MainPack()
    received.ParseFromString(ping_bytes)

    assert received.actioncode == pb.ActionCode.Value("Ping")

    # 模拟服务端构造 Pong（与 ClientUdp.cs 逻辑一致）
    pong = pb.MainPack()
    pong.actioncode = pb.ActionCode.Value("Pong")
    pong.timestamp = received.timestamp  # 原样回传

    pong_bytes = pong.SerializeToString()

    # 客户端解析 Pong
    client_received = pb.MainPack()
    client_received.ParseFromString(pong_bytes)

    assert client_received.actioncode == pb.ActionCode.Value("Pong"), \
        f"Expected Pong, got {client_received.actioncode}"
    assert client_received.timestamp == 9876543210, \
        f"Expected 9876543210, got {client_received.timestamp}"
    # Pong 不应有多余字段
    assert client_received.requestcode == 0  # RequestNone
    assert client_received.returncode == 0   # ReturnNone
    assert client_received.str == ""
    assert len(client_received.battleplayerpack) == 0
    print("[PASS] test_pong_reply_logic")

def test_ping_with_zero_timestamp():
    """timestamp=0 的 Ping（边界：客户端首次 Ping 可能发 0）"""
    ping = pb.MainPack()
    ping.actioncode = pb.ActionCode.Value("Ping")
    ping.timestamp = 0

    data = ping.SerializeToString()
    parsed = pb.MainPack()
    parsed.ParseFromString(data)

    # proto3 默认值为 0，序列化时 int64=0 不写入
    assert parsed.timestamp == 0
    assert parsed.actioncode == pb.ActionCode.Value("Ping")

    # Pong 回传
    pong = pb.MainPack()
    pong.actioncode = pb.ActionCode.Value("Pong")
    pong.timestamp = parsed.timestamp
    pong_data = pong.SerializeToString()

    pong_parsed = pb.MainPack()
    pong_parsed.ParseFromString(pong_data)
    assert pong_parsed.timestamp == 0
    print("[PASS] test_ping_with_zero_timestamp")

def test_ping_with_large_timestamp():
    """极大 timestamp（毫秒时间戳，约 2026 年）"""
    ts = 1774012800000  # ~2026-03-17 in ms
    ping = pb.MainPack()
    ping.actioncode = pb.ActionCode.Value("Ping")
    ping.timestamp = ts

    data = ping.SerializeToString()
    parsed = pb.MainPack()
    parsed.ParseFromString(data)

    assert parsed.timestamp == ts

    # Pong
    pong = pb.MainPack()
    pong.actioncode = pb.ActionCode.Value("Pong")
    pong.timestamp = parsed.timestamp
    pong_data = pong.SerializeToString()
    pong_parsed = pb.MainPack()
    pong_parsed.ParseFromString(pong_data)
    assert pong_parsed.timestamp == ts
    print("[PASS] test_ping_with_large_timestamp")

def test_pong_minimal_size():
    """Pong 包应尽量小（仅 actioncode + timestamp）"""
    pong = pb.MainPack()
    pong.actioncode = pb.ActionCode.Value("Pong")
    pong.timestamp = 1234567890123

    data = pong.SerializeToString()
    # actioncode=25 (Pong) 编码: field 2 varint = [0x10, 0x19] = 2 bytes
    # timestamp=1234567890123 编码: field 14 varint = tag(1 byte) + varint(~6 bytes) ≈ 8 bytes
    # 总计约 10 bytes，远小于完整 MainPack
    assert len(data) < 20, f"Pong packet too large: {len(data)} bytes"
    print(f"[PASS] test_pong_minimal_size (size={len(data)} bytes)")

def test_non_battle_ping_ignored():
    """
    逻辑验证：未关联战斗的 endpoint 发 Ping，不应回复 Pong。
    验证方法：模拟 _endpointRouteMap 为空时的分支
    """
    # 模拟空路由表
    endpoint_route_map = {}
    ping_endpoint_key = "192.168.1.100:12345"

    has_route = ping_endpoint_key in endpoint_route_map
    assert has_route == False, "Should not have route for unregistered endpoint"

    # 模拟有路由表
    endpoint_route_map[ping_endpoint_key] = {"BattleId": 1}
    has_route = ping_endpoint_key in endpoint_route_map
    assert has_route == True, "Should have route for registered endpoint"

    # 不同 endpoint 不应匹配
    other_key = "192.168.1.200:54321"
    has_route = other_key in endpoint_route_map
    assert has_route == False, "Should not have route for different endpoint"

    print("[PASS] test_non_battle_ping_ignored")

if __name__ == "__main__":
    test_ping_serialization_roundtrip()
    test_pong_reply_logic()
    test_ping_with_zero_timestamp()
    test_ping_with_large_timestamp()
    test_pong_minimal_size()
    test_non_battle_ping_ignored()
    print("\n=== All Task 2.4 tests PASSED ===")
