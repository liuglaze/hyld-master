"""Apply Task 5 (Accumulator Refactoring) edits to BattleManger.cs"""
import sys

filepath = 'D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs'
with open(filepath, 'r', encoding='utf-8-sig') as f:
    content = f.read()

errors = []

# === 5.1: Add accumulator fields after IsGameOver ===
old_5_1 = '        public bool IsGameOver { get; private set; }\n        public Toolbox toolbox;'

new_5_1 = (
    '        public bool IsGameOver { get; private set; }\n'
    '        // ── 累加器驱动（动态追帧系统 Task 5） ──\n'
    '        private float _tickAccumulator;\n'
    '        private float currentTickInterval = 0.016f;\n'
    '        private bool _battleTickActive;\n'
    '        public Toolbox toolbox;'
)

if old_5_1 not in content:
    errors.append('ERROR 5.1: anchor not found')
else:
    content = content.replace(old_5_1, new_5_1, 1)
    print('5.1: Accumulator fields added')

# === 5.2a: Replace InvokeRepeating("Send_operation") with _battleTickActive = true ===
old_5_2a = (
    '                        this.CancelInvoke("Send_BattleReady");\n'
    '                        float _time = Server.NetConfigValue.frameTime;\n'
    '                        this.InvokeRepeating("Send_operation", _time, _time);\n'
    '                        StartCoroutine(WaitForFirstMessage());'
)

new_5_2a = (
    '                        this.CancelInvoke("Send_BattleReady");\n'
    '                        // ── Task 5: InvokeRepeating -> 累加器驱动 ──\n'
    '                        _tickAccumulator = 0f;\n'
    '                        currentTickInterval = Server.NetConfigValue.frameTime;\n'
    '                        _battleTickActive = true;\n'
    '                        StartCoroutine(WaitForFirstMessage());'
)

if old_5_2a not in content:
    errors.append('ERROR 5.2a: anchor not found')
else:
    content = content.replace(old_5_2a, new_5_2a, 1)
    print('5.2a: InvokeRepeating replaced with _battleTickActive')

# === 5.2b: Replace CancelInvoke("Send_operation") in BeginGameOver ===
# In BeginGameOver, we also need to set _battleTickActive = false
old_5_2b_game_over = (
    '            IsGameOver = true;\n'
    '            Logging.HYLDDebug.FrameTrace($"[GameOver] begin frameSync={BattleData.Instance.sync_frameID} predictedFrame={BattleData.Instance.predicted_frameID} historyCount={BattleData.Instance.PredictionHistoryCount} notifyServer={shouldNotifyServer}");\n'
    '            this.CancelInvoke("Send_operation");'
)

new_5_2b_game_over = (
    '            IsGameOver = true;\n'
    '            Logging.HYLDDebug.FrameTrace($"[GameOver] begin frameSync={BattleData.Instance.sync_frameID} predictedFrame={BattleData.Instance.predicted_frameID} historyCount={BattleData.Instance.PredictionHistoryCount} notifyServer={shouldNotifyServer}");\n'
    '            // ── Task 5: 停止累加器 ──\n'
    '            _battleTickActive = false;\n'
    '            _tickAccumulator = 0f;\n'
    '            currentTickInterval = Server.NetConfigValue.frameTime;'
)

if old_5_2b_game_over not in content:
    errors.append('ERROR 5.2b: BeginGameOver anchor not found')
else:
    content = content.replace(old_5_2b_game_over, new_5_2b_game_over, 1)
    print('5.2b: CancelInvoke in BeginGameOver -> _battleTickActive=false')

# === 5.2c: Replace CancelInvoke("Send_operation") in OnDestroy ===
old_5_2c = (
    '        private void OnDestroy()\n'
    '        {\n'
    '            this.CancelInvoke("Send_operation");\n'
    '            this.CancelInvoke("Send_BattleReady");\n'
    '            this.CancelInvoke("SendGameOver");'
)

new_5_2c = (
    '        private void OnDestroy()\n'
    '        {\n'
    '            // ── Task 5: 停止累加器 ──\n'
    '            _battleTickActive = false;\n'
    '            this.CancelInvoke("Send_BattleReady");\n'
    '            this.CancelInvoke("SendGameOver");'
)

if old_5_2c not in content:
    errors.append('ERROR 5.2c: OnDestroy anchor not found')
else:
    content = content.replace(old_5_2c, new_5_2c, 1)
    print('5.2c: CancelInvoke in OnDestroy -> _battleTickActive=false')

# === 5.3: Rename Send_operation to BattleTick and extract DrainAndDispatch + Ping to Update ===
# The Send_operation body currently has:
#   1. IsGameOver check
#   2. DrainAndDispatch    <-- will move to Update
#   3. Ping scheduling     <-- will move to Update
#   4. cachedMainThreadTime
#   5. prediction logic (nextFrame, upload, resetOp, execute, bullets, etc.)
#   6. SendOperation
#
# BattleTick will contain items 4-6 (pure tick logic).
# DrainAndDispatch + Ping stay in Update (before accumulator loop).

old_5_3 = (
    '        void Send_operation()\n'
    '        {\n'
    '            if (IsGameOver)\n'
    '            {\n'
    '                return;\n'
    '            }\n'
    '\n'
    '            // \u2605 \u5148\u6d88\u8d39\u6240\u6709\u5f85\u5904\u7406\u7684\u6743\u5a01\u5e27\uff08\u4e3b\u7ebf\u7a0b\u5b89\u5168\uff0c\u4e0e\u9884\u6d4b\u63a8\u8fdb\u987a\u5e8f\u4e00\u81f4\uff09\n'
    '            Server.UDPSocketManger.Instance.DrainAndDispatch();\n'
    '\n'
    '            // \u2500\u2500 Ping \u8c03\u5ea6\uff1a\u6218\u6597\u4e2d\u6bcf pingIntervalMs \u53d1\u9001\u4e00\u6b21 UDP Ping \u2500\u2500\n'
    '            float pingIntervalSec = Server.NetConfigValue.pingIntervalMs / 1000f;\n'
    '            if (Time.time - Manger.BattleData.Instance._lastPingTime >= pingIntervalSec)\n'
    '            {\n'
    '                Manger.BattleData.Instance._lastPingTime = Time.time;\n'
    '                MainPack pingPack = new MainPack();\n'
    '                pingPack.Actioncode = ActionCode.Ping;\n'
    '                pingPack.Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();\n'
    '                Server.UDPSocketManger.Instance.Send(pingPack);\n'
    '            }\n'
    '\n'
    '            Manger.BattleData.Instance.cachedMainThreadTime = Time.time;'
)

new_5_3 = (
    '        /// <summary>\n'
    '        /// \u7eaf\u903b\u8f91\u5e27\u63a8\u8fdb + \u53d1\u5305\u3002\u7531 Update \u7d2f\u52a0\u5668\u5faa\u73af\u8c03\u7528\uff0c\u4e0d\u4f9d\u8d56 Time.deltaTime\u3002\n'
    '        /// </summary>\n'
    '        void BattleTick()\n'
    '        {\n'
    '            Manger.BattleData.Instance.cachedMainThreadTime = Time.time;'
)

if old_5_3 not in content:
    errors.append('ERROR 5.3: Send_operation body anchor not found')
else:
    content = content.replace(old_5_3, new_5_3, 1)
    print('5.3: Send_operation renamed to BattleTick, DrainAndDispatch+Ping extracted')

# === 5.4 + 5.5: Rewrite Update method with accumulator loop and pipeline order ===
old_5_4 = (
    '        private void Update()\n'
    '        {\n'
    '#if UNITY_EDITOR || DEVELOPMENT_BUILD\n'
    '            if (!EnableDebugGameOverHotkey || IsGameOver)\n'
    '            {\n'
    '                return;\n'
    '            }\n'
    '\n'
    '            if (Input.GetKeyDown(DebugGameOverHotkey))\n'
    '            {\n'
    '                TriggerDebugGameOver();\n'
    '            }\n'
    '#endif\n'
    '        }'
)

new_5_4 = (
    '        private void Update()\n'
    '        {\n'
    '#if UNITY_EDITOR || DEVELOPMENT_BUILD\n'
    '            if (EnableDebugGameOverHotkey && !IsGameOver && Input.GetKeyDown(DebugGameOverHotkey))\n'
    '            {\n'
    '                TriggerDebugGameOver();\n'
    '            }\n'
    '#endif\n'
    '\n'
    '            if (!_battleTickActive || IsGameOver)\n'
    '            {\n'
    '                return;\n'
    '            }\n'
    '\n'
    '            // ── \u7ba1\u7ebf\u6b65\u9aa4 1: \u6d88\u8d39 UDP \u961f\u5217\u4e2d\u7684\u6743\u5a01\u5e27 ──\n'
    '            Server.UDPSocketManger.Instance.DrainAndDispatch();\n'
    '\n'
    '            // ── \u7ba1\u7ebf\u6b65\u9aa4 2: Ping \u8c03\u5ea6 ──\n'
    '            float pingIntervalSec = Server.NetConfigValue.pingIntervalMs / 1000f;\n'
    '            if (Time.time - Manger.BattleData.Instance._lastPingTime >= pingIntervalSec)\n'
    '            {\n'
    '                Manger.BattleData.Instance._lastPingTime = Time.time;\n'
    '                MainPack pingPack = new MainPack();\n'
    '                pingPack.Actioncode = ActionCode.Ping;\n'
    '                pingPack.Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();\n'
    '                Server.UDPSocketManger.Instance.Send(pingPack);\n'
    '            }\n'
    '\n'
    '            // ── \u7ba1\u7ebf\u6b65\u9aa4 3: \u76ee\u6807\u5e27\u53f7\u8ba1\u7b97 + tick \u8c03\u8282\uff08Task 6 \u5360\u4f4d\uff09 ──\n'
    '            // TODO: CalcTargetFrame() + AdjustTickInterval() \u5c06\u5728 Task 6 \u63a5\u5165\n'
    '\n'
    '            // ── \u7ba1\u7ebf\u6b65\u9aa4 4: \u7d2f\u52a0\u5668\u5faa\u73af ──\n'
    '            _tickAccumulator += Time.deltaTime;\n'
    '\n'
    '            // \u9632\u5f39\u7c07\uff1a\u7d2f\u52a0\u5668\u4e0d\u8d85\u8fc7 maxCatchupPerUpdate \u6b21 tick \u7684\u91cf\n'
    '            float maxAccum = currentTickInterval * Server.NetConfigValue.maxCatchupPerUpdate;\n'
    '            if (_tickAccumulator > maxAccum)\n'
    '            {\n'
    '                _tickAccumulator = maxAccum;\n'
    '            }\n'
    '\n'
    '            while (_tickAccumulator >= currentTickInterval)\n'
    '            {\n'
    '                BattleTick();\n'
    '                _tickAccumulator -= currentTickInterval;\n'
    '            }\n'
    '        }'
)

if old_5_4 not in content:
    errors.append('ERROR 5.4: Update method anchor not found')
else:
    content = content.replace(old_5_4, new_5_4, 1)
    print('5.4+5.5: Update rewritten with accumulator loop and pipeline order')

if errors:
    for e in errors:
        print(e)
    sys.exit(1)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
print('\nAll Task 5 edits applied to BattleManger.cs')
