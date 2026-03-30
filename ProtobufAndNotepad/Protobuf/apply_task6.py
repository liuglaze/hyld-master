"""Apply Task 6 (Dynamic Tick Adjustment) edits to ConstValue.cs and BattleManger.cs"""
import sys

errors = []

# ============================================================
# Part A: ConstValue.cs — 6.1 新增配置常量
# ============================================================
const_path = 'D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/ConstValue.cs'
with open(const_path, 'r', encoding='utf-8-sig') as f:
    const_content = f.read()

old_const = (
    '        // ── 动态追帧参数 ──\n'
    '        public static readonly float pingIntervalMs = 200f;\n'
    '        public static readonly int maxCatchupPerUpdate = 3;\n'
    '    }\n'
    '}'
)

new_const = (
    '        // ── 动态追帧参数 ──\n'
    '        public static readonly float pingIntervalMs = 200f;\n'
    '        public static readonly int maxCatchupPerUpdate = 3;\n'
    '        public static readonly int inputBufferSize = 2;\n'
    '        public static readonly float adjustRate = 0.05f;\n'
    '        public static readonly float minSpeedFactor = 0.85f;\n'
    '        public static readonly float maxSpeedFactor = 1.15f;\n'
    '        public static readonly float smoothRate = 5.0f;\n'
    '    }\n'
    '}'
)

if old_const not in const_content:
    errors.append('ERROR 6.1: ConstValue.cs anchor not found')
else:
    const_content = const_content.replace(old_const, new_const, 1)
    print('6.1: Config constants added to ConstValue.cs')

with open(const_path, 'w', encoding='utf-8') as f:
    f.write(const_content)

# ============================================================
# Part B: BattleManger.cs — 6.2-6.6
# ============================================================
bm_path = 'D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs'
with open(bm_path, 'r', encoding='utf-8-sig') as f:
    bm_content = f.read()

# --- 6.2+6.3: Add actualSpeedFactor field + CalcTargetFrame + AdjustTickInterval methods ---
# Insert after the accumulator fields block (after _battleTickActive line, before toolbox)

old_fields = (
    '        // ── 累加器驱动（动态追帧系统 Task 5） ──\n'
    '        private float _tickAccumulator;\n'
    '        private float currentTickInterval = 0.016f;\n'
    '        private bool _battleTickActive;\n'
    '        public Toolbox toolbox;'
)

new_fields = (
    '        // ── 累加器驱动（动态追帧系统 Task 5） ──\n'
    '        private float _tickAccumulator;\n'
    '        private float currentTickInterval = 0.016f;\n'
    '        private bool _battleTickActive;\n'
    '        // ── 动态追帧（Task 6） ──\n'
    '        private float _actualSpeedFactor = 1.0f;\n'
    '        private bool _skipAccumulatorThisFrame;\n'
    '        public Toolbox toolbox;'
)

if old_fields not in bm_content:
    errors.append('ERROR 6.2a: fields anchor not found')
else:
    bm_content = bm_content.replace(old_fields, new_fields, 1)
    print('6.2a: actualSpeedFactor + _skipAccumulatorThisFrame fields added')

# --- 6.2+6.3: Insert CalcTargetFrame and AdjustTickInterval methods ---
# Insert before BattleTick method

old_battletick_header = (
    '        /// <summary>\n'
    '        /// 纯逻辑帧推进 + 发包。由 Update 累加器循环调用，不依赖 Time.deltaTime。\n'
    '        /// </summary>\n'
    '        void BattleTick()'
)

new_methods_plus_battletick = (
    '        // ═══════ 动态追帧（Task 6） ═══════\n'
    '\n'
    '        /// <summary>\n'
    '        /// 6.2: 计算客户端应达到的目标帧号。\n'
    '        /// 公式: targetFrame = sync_frameID + ceil(rttFrames/2) + inputBufferSize\n'
    '        /// RTT 未初始化时使用默认超前量 inputBufferSize。\n'
    '        /// </summary>\n'
    '        private int CalcTargetFrame()\n'
    '        {\n'
    '            int syncFrame = Manger.BattleData.Instance.sync_frameID;\n'
    '            int inputBuf = Server.NetConfigValue.inputBufferSize;\n'
    '\n'
    '            if (!Manger.BattleData.Instance.IsRttInitialized)\n'
    '            {\n'
    '                // RTT 未初始化，用默认超前量\n'
    '                return syncFrame + inputBuf;\n'
    '            }\n'
    '\n'
    '            float rttMs = Manger.BattleData.Instance.smoothedRTT;\n'
    '            float frameTimeMs = Server.NetConfigValue.frameTime * 1000f; // 16ms\n'
    '            float rttFrames = rttMs / frameTimeMs;\n'
    '            int halfRttFrames = Mathf.CeilToInt(rttFrames / 2f);\n'
    '\n'
    '            return syncFrame + halfRttFrames + inputBuf;\n'
    '        }\n'
    '\n'
    '        /// <summary>\n'
    '        /// 6.3+6.4: 根据当前帧号与目标帧号的差值动态调节 tick 间隔。\n'
    '        /// frameDiff > 0 → 客户端落后，加速（speedFactor > 1）\n'
    '        /// frameDiff < 0 → 客户端超前，减速（speedFactor < 1）\n'
    '        /// frameDiff < -5 → 严重超前，暂停本帧累加器循环\n'
    '        /// </summary>\n'
    '        private void AdjustTickInterval(int targetFrame)\n'
    '        {\n'
    '            int predictedFrame = Manger.BattleData.Instance.predicted_frameID;\n'
    '            int frameDiff = targetFrame - predictedFrame;\n'
    '\n'
    '            // 6.4: 严重超前暂停\n'
    '            if (frameDiff < -5)\n'
    '            {\n'
    '                _skipAccumulatorThisFrame = true;\n'
    '                Logging.HYLDDebug.FrameTrace($"[TickAdj] PAUSE frameDiff={frameDiff} target={targetFrame} predicted={predictedFrame}");\n'
    '                return;\n'
    '            }\n'
    '            _skipAccumulatorThisFrame = false;\n'
    '\n'
    '            // 6.3: 计算目标速度因子\n'
    '            float targetSpeedFactor = Mathf.Clamp(\n'
    '                1f + frameDiff * Server.NetConfigValue.adjustRate,\n'
    '                Server.NetConfigValue.minSpeedFactor,\n'
    '                Server.NetConfigValue.maxSpeedFactor);\n'
    '\n'
    '            // Lerp 平滑过渡\n'
    '            _actualSpeedFactor = Mathf.Lerp(\n'
    '                _actualSpeedFactor,\n'
    '                targetSpeedFactor,\n'
    '                Server.NetConfigValue.smoothRate * Time.deltaTime);\n'
    '\n'
    '            // 输出 tick 间隔\n'
    '            currentTickInterval = Server.NetConfigValue.frameTime / _actualSpeedFactor;\n'
    '        }\n'
    '\n'
    '        /// <summary>\n'
    '        /// 纯逻辑帧推进 + 发包。由 Update 累加器循环调用，不依赖 Time.deltaTime。\n'
    '        /// </summary>\n'
    '        void BattleTick()'
)

if old_battletick_header not in bm_content:
    errors.append('ERROR 6.2b: BattleTick header anchor not found')
else:
    bm_content = bm_content.replace(old_battletick_header, new_methods_plus_battletick, 1)
    print('6.2+6.3+6.4: CalcTargetFrame + AdjustTickInterval methods added')

# --- 6.5: Replace Task 6 TODO placeholder in Update with actual calls ---

old_pipeline3 = (
    '            // ── 管线步骤 3: 目标帧号计算 + tick 调节（Task 6 占位） ──\n'
    '            // TODO: CalcTargetFrame() + AdjustTickInterval() 将在 Task 6 接入\n'
    '\n'
    '            // ── 管线步骤 4: 累加器循环 ──\n'
    '            _tickAccumulator += Time.deltaTime;\n'
    '\n'
    '            // 防弹簇：累加器不超过 maxCatchupPerUpdate 次 tick 的量\n'
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
    '            }'
)

new_pipeline3 = (
    '            // ── 管线步骤 3: 目标帧号计算 + tick 调节 ──\n'
    '            int targetFrame = CalcTargetFrame();\n'
    '            AdjustTickInterval(targetFrame);\n'
    '\n'
    '            // ── 管线步骤 4: 累加器循环 ──\n'
    '            if (_skipAccumulatorThisFrame)\n'
    '            {\n'
    '                // 6.4: 严重超前，跳过本帧累加器循环（不累加 deltaTime）\n'
    '                return;\n'
    '            }\n'
    '\n'
    '            _tickAccumulator += Time.deltaTime;\n'
    '\n'
    '            // 防弹簇：累加器不超过 maxCatchupPerUpdate 次 tick 的量\n'
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
    '            }'
)

if old_pipeline3 not in bm_content:
    errors.append('ERROR 6.5: Update pipeline 3 anchor not found')
else:
    bm_content = bm_content.replace(old_pipeline3, new_pipeline3, 1)
    print('6.5: Update pipeline wired with CalcTargetFrame + AdjustTickInterval')

# --- 6.6: Add state cleanup in BeginGameOver ---

old_cleanup = (
    '            // ── Task 5: 停止累加器 ──\n'
    '            _battleTickActive = false;\n'
    '            _tickAccumulator = 0f;\n'
    '            currentTickInterval = Server.NetConfigValue.frameTime;'
)

new_cleanup = (
    '            // ── Task 5+6: 停止累加器 + 追帧状态清理 ──\n'
    '            _battleTickActive = false;\n'
    '            _tickAccumulator = 0f;\n'
    '            currentTickInterval = Server.NetConfigValue.frameTime;\n'
    '            _actualSpeedFactor = 1.0f;\n'
    '            _skipAccumulatorThisFrame = false;'
)

count = bm_content.count(old_cleanup)
if count == 0:
    errors.append('ERROR 6.6: BeginGameOver cleanup anchor not found')
else:
    bm_content = bm_content.replace(old_cleanup, new_cleanup)
    print(f'6.6: State cleanup added in {count} location(s)')

if errors:
    for e in errors:
        print(e)
    sys.exit(1)

with open(bm_path, 'w', encoding='utf-8') as f:
    f.write(bm_content)
print('\nAll Task 6 edits applied successfully')
