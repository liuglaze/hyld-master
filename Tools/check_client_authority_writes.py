"""门禁：客户端不得在权威路径之外**改写权威字段**（P3-3c）。

## 为什么要这道门禁

P3-3c 修的是一类具体缺陷（S7 Q7 的 (C) 类）：客户端在自己的逻辑里直接改写
「只有服务端才有权决定」的字段。它造成的症状是**两个权威源**：

  · 客户端写一次，服务端每批权威帧再覆写回来 → HP 抖动 / 操作无效；
  · 客户端改「预测输入」（如移速）→ 本地预测与服务端权威持续对不上，
    被 MoveAck 反复回校正（表现为位置被「拉回」）。

这类缺陷的共同特点是**编译期完全看不出来**：类型对、签名对、能跑，
只是语义上是错的。所以必须靠一道显式的检查锁住，否则下次有人顺手加回一行
`Players[i].playerBloodValue -= dmg` 也没人会发现。

## 判定方式

扫描 `Client/Assets/**/*.cs`，找出对「权威字段」的**写入**（`=` / `+=` / `-=` / `*=` / `/=` / `++` / `--`），
然后分两类放行：

  · **许可路径**（ALLOWED）：这些文件本来就是「消费服务端权威结果 / 做本地预测」的地方；
  · **已登记的例外**（EXEMPT）：逐条写明「为什么这里无害」，必须带证据。

其余的**一律报错**。

## 权威字段清单

| 字段 | 服务端来源 |
|---|---|
| `playerBloodValue` | `PackPlayerStates` 的 Hp（BattleController.Network.cs） |
| `playerBloodMax` | 同上（首批初始化时写入，之后服务端不再改） |
| `playerManaValue` | 同上（Mana） |
| `当前能量` | 同上（SuperEnergy） |
| `可以按大招` | 由权威能量推导 |
| `playerPositon` | `ApplyPendingClientMoves` 算出的权威位置 |
| `isNotDie` | 服务端 `IsDead` |
| `移动速度` | 非权威字段，但是**预测输入**；在预测路径外改会制造预测分歧 |

运行：python Tools/check_client_authority_writes.py
退出码：0 = 无越界写入；1 = 存在越界写入
"""

import os
import re
import sys

# ---------------------------------------------------------------------------
# 权威字段：字段名 → 服务端来源（仅用于报错时说明）
# ---------------------------------------------------------------------------
AUTHORITATIVE = {
    'playerBloodValue': 'HP（服务端 PackPlayerStates.Hp）',
    'playerBloodMax': 'HP 上限（服务端首批 HP 初始化写入）',
    'playerManaValue': '蓝量（服务端 PackPlayerStates.Mana）',
    '当前能量': '大招能量（服务端 PackPlayerStates.SuperEnergy）',
    '可以按大招': '大招可用位（由权威能量推导）',
    'playerPositon': '位置（服务端 ApplyPendingClientMoves）',
    'isNotDie': '存活位（服务端 IsDead）',
    '移动速度': '预测输入用的移速（不得在预测路径外改）',
}

# ---------------------------------------------------------------------------
# 许可的写入位置：这些文件本身就是「权威结果消费 / 本地预测」路径
# ---------------------------------------------------------------------------
ALLOWED = {
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.HitEvent.cs':
        '权威 HP/蓝量/能量/存活 的唯一消费点（ApplyAuthoritativeHpAndDeath）',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.Authority.cs':
        '权威位置/动画参数的消费点（含 MoveAck 位置校正）',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs':
        'CSP 回滚重放：按权威位置重算预测位置',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.cs':
        'BattleData 自身的状态重置（ClearPredictionRuntimeState 等）',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.Attack.cs':
        '本地预测的蓝量/能量扣减（真值由 ApplyAttackAcks 覆写）',
    'Client/Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs':
        '本地预测推进：playerPositon + 移速（已走共享仿真核心）',
    'Client/Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs':
        '视觉子弹编排（读每玩家有效值）',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs':
        '战斗宿主：初始化玩家状态',
    'Client/Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs':
        'RTT 平滑（不涉及权威玩法字段）',
    'Client/Assets/HYLD1.0/Scripts/OldScripts/PlayerLogic.cs':
        'HP 的只读镜像 + 血上限跟随；P3-3c 后此处已无任何加减',
    'Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs':
        'PlayerInformation 构造初始化（初值，非玩法写入）',
    'Client/Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs':
        'UI 输入门：仅用「权威能量 >= 上限」这个同一谓词点亮大招摇杆；'
        '权威路径每批用同一规则重算，不会分叉',
}

# ---------------------------------------------------------------------------
# 已登记的例外：(相对路径, 字段) → 为什么无害（必须带证据）
# ---------------------------------------------------------------------------
EXEMPT = {
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs', 'playerBloodValue'):
        '单机碰撞回调 OnTrigger_Player；联机视觉子弹碰撞体被禁用（shell.cs:129-139）→ 不可达',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs', '当前能量'):
        '同上',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs', 'isNotDie'):
        '同上',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/Boom.cs', 'playerBloodValue'):
        '单机投掷物爆炸；联机生成的两组 prefab（BetterShells / 大招实体）与含 BoomCreater 的 '
        'prefab 交集为 0 → 不可达',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs', 'playerPositon'):
        '被 `被控制` 门控；`被控制` 的唯一写入点是 shell.cs 的不可达碰撞回调 → 不可达',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs', 'isNotDie'):
        '同上，被 `被控制` 门控 → 不可达',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Moden/ts/Moden/HYLDModenProp.cs', 'playerBloodMax'):
        '!! 联机可达的真实违反（狂暴瓶道具）：+30% 血上限写在本地，服务端不知；'
        '且服务端 maxHp 只在首批初始化写入 → 本地加的上限不会被纠正。'
        '修它需要把道具效果纳入复制，属 P5；见计划「有意保留、未修的项」',
    ('Client/Assets/HYLD1.0/Scripts/OldScripts/Moden/ts/Moden/HYLDModenProp.cs', '移动速度'):
        '!! 联机可达的真实违反（狂暴瓶 +1 移速）：改的是预测输入，而服务端移速来自配置表 '
        '→ 该玩家会持续被 MoveAck 回校正。属 P5',
}

ROOT = 'Client/Assets'
SCAN_EXT = '.cs'

# 跳过第三方 / 示例 / 构建产物
PRUNE_DIRS = {'xlua', 'plugins', 'textmeshpro', 'library', 'temp', 'obj', 'editor default resources'}


def strip_line_comment(line):
    """去掉行内 `//` 之后的内容，避免注释里的示例代码被误判。"""
    idx = line.find('//')
    if idx >= 0:
        line = line[:idx]
    return line


def field_write_regex(field):
    # 形如  X.<field> =  /  +=  /  -=  /  *=  /  /=  /  ++  /  --
    return re.compile(r'\.' + re.escape(field) + r'\s*(?:[-+*/]?=|\+\+|--)')


def main():
    args = sys.argv[1:] or [ROOT]
    # 允许直接传单个文件（便于定位问题）。
    # 之前只当目录处理，传文件时 os.walk 什么都不产出 → 「已扫描 0 个」却报 PASS，
    # 这是个危险的假阴性，所以下面还有一道 scanned == 0 的硬校验。
    files = []
    dirs = []
    for a in args:
        if os.path.isfile(a):
            files.append(a.replace(os.sep, '/'))
        elif os.path.isdir(a):
            dirs.append(a)
        else:
            print('路径不存在：' + a)
            return 1

    violations = []
    scanned = 0
    allowed_hits = 0
    exempt_hits = 0

    def scan_one(rel):
        nonlocal scanned, allowed_hits, exempt_hits
        try:
            text = open(rel, 'rb').read().decode('utf-8-sig', errors='replace')
        except OSError:
            return
        scanned += 1
        allowed_why = ALLOWED.get(rel)
        for lineno, line in enumerate(text.split('\n'), 1):
            code = strip_line_comment(line)
            for field in AUTHORITATIVE:
                if not field_write_regex(field).search(code):
                    continue
                if allowed_why is not None:
                    allowed_hits += 1
                    continue
                if (rel, field) in EXEMPT:
                    exempt_hits += 1
                    continue
                violations.append((rel, lineno, field, line.strip()))

    for rel in files:
        if rel.endswith(SCAN_EXT):
            scan_one(rel)

    for root in dirs:
        for dp, dn, fn in os.walk(root):
            dn[:] = [d for d in dn if d.lower() not in PRUNE_DIRS]
            for f in fn:
                if not f.endswith(SCAN_EXT):
                    continue
                scan_one(os.path.join(dp, f).replace(os.sep, '/'))

    print('已扫描 .cs =', scanned)
    print('权威字段 = %d 个；许可路径 = %d 个文件；已登记例外 = %d 条'
          % (len(AUTHORITATIVE), len(ALLOWED), len(EXEMPT)))
    print('命中：许可路径 %d 处，已登记例外 %d 处' % (allowed_hits, exempt_hits))
    print()

    # 硬校验：一个文件都没扫到就必须报错，不能报 PASS ——
    # 「0 个文件 + PASS」会让路径写错/被排除这类问题静默通过。
    if scanned == 0:
        print('判定：ERROR —— 没有扫到任何 .cs，无法给出结论（请检查传入路径）。')
        return 1

    print('越界写入 =', len(violations))
    if violations:
        print()
        cur = None
        for rel, lineno, field, text in violations:
            if rel != cur:
                cur = rel
                print('  ' + rel)
            print('    L%-5d %-18s %s' % (lineno, field, text[:78]))
            print('           -> %s' % AUTHORITATIVE[field])
        print()
        print('判定：FAIL —— 这些位置不得改写权威字段。')
        print('  · 若属于「联机不可达的单机层」，请连同不可达证据加入 EXEMPT；')
        print('  · 若属于联机路径，应改为「消费服务端下发的值」，而不是本地计算。')
        return 1

    print()
    print('判定：PASS —— 权威字段的写入只出现在许可路径或已登记的例外里。')
    print('提示：EXEMPT 里带 "!!" 的条目是**已知的真实违反**（尚未修复），不是「无害」。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
