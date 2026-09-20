# -*- coding: utf-8 -*-
"""P3'-2 门禁：英雄表（HYLDStaticValue 里的 AddHero 调用）的形状与内容校验。

为什么需要它：
    英雄表是「一段格式固定、行数固定」的代码，用脚本生成或批量改写时最容易犯的错是
    **参数没包进构造函数、或参数个数/顺序不对**。这类错误：
      - 不会破坏括号平衡（所以 check_cs_braces.py 抓不到）；
      - 不会破坏"集合"语义（所以只查英雄名单的门禁也抓不到）；
      - 却能一路编到 Unity 才报错，而每次往返都要用户在编辑器里等一次编译。

    本项目真实踩过三次（同一段代码）：
      CS1022 多括号 → CS1501 漏 new Hero(...) 包装 → CS1503 参数整体错位
    前两次修完还各引入了一个新错误。这个脚本就是为「不再让用户当编译器」而写的。

它检查：
    1) 行数 = 20
    2) 每行形状 = AddHero(<HeroName 常量>, "显示名", "定位", <shell>, <大招实体>, <boom>, <bool>)
    3) 参数个数与 Hero 构造函数签名相容
    4) 无重复英雄
    5) 英雄集合与 PMHeroId（共享数值表的编号常量）完全一致
    6) 行序与 PMHeroId 编号一致（便于与共享表逐行交叉核对）
    7) 表现引用与改造前的冻结基准逐字段一致（换错预制体会被拦住）

退出码：0 = 通过；1 = 存在问题
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TABLE_FILE = os.path.join(ROOT, 'Client', 'Assets', 'HYLD1.0', 'Scripts', 'OldScripts', 'HYLDStaticValue.cs')
HERO_FILE = os.path.join(ROOT, 'Client', 'Assets', 'HYLD1.0', 'Scripts', 'OldScripts', 'HeroData.cs')
SHARED = os.path.join(ROOT, 'Client', 'Assets', 'Scripts', 'Shared', 'BattleNumericConfig.cs')
BASELINE = os.path.join(ROOT, 'Tools', '_hero_presentation_baseline.json')

# Hero 构造函数的参数顺序（用于把 AddHero 的参数映射成字段名）
CTOR_FIELDS = ['heroName', 'displayName', 'positioning', 'shell', 'superEntity', 'boom', 'isSuperMovingType']

checks = 0
failures = []


def check(name, ok, detail=''):
    global checks
    checks += 1
    if ok:
        print('  [OK]   %s' % name)
    else:
        failures.append('%s  -> %s' % (name, detail))
        print('  [FAIL] %s  -> %s' % (name, detail))


def read(path):
    with open(path, 'rb') as f:
        raw = f.read()
    if raw.startswith(b'\xef\xbb\xbf'):
        raw = raw[3:]
    return raw.decode('utf-8')


def split_top_level(s):
    """按顶层逗号切分（忽略括号/方括号内的逗号）。"""
    parts = []
    depth = 0
    cur = ''
    for ch in s:
        if ch in '([{':
            depth += 1
        elif ch in ')]}':
            depth -= 1
        if ch == ',' and depth == 0:
            parts.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        parts.append(cur.strip())
    return parts


def parse_hero_ctor():
    """返回 (必填参数数, 总参数数, 参数名列表)。"""
    text = read(HERO_FILE)
    m = re.search(r'public\s+Hero\s*\((.*?)\)\s*\n\s*\{', text, re.S)
    if not m:
        raise RuntimeError('HeroData.cs 里找不到 Hero 的构造函数')
    params = split_top_level(m.group(1))
    names = [p.split('=')[0].strip().split()[-1] for p in params]
    required = sum(1 for p in params if '=' not in p)
    return required, len(params), names


def parse_addhero_signature():
    """解析 AddHero 自己的参数列表（行数校验应依据它，而不是从 Hero 推导）。"""
    text = read(TABLE_FILE)
    m = re.search(r'private\s+static\s+void\s+AddHero\s*\((.*?)\)\s*\r?\n\s*\{', text, re.S)
    if not m:
        raise RuntimeError('HYLDStaticValue.cs 里找不到 AddHero 方法定义')
    params = split_top_level(m.group(1))
    names = [p.split('=')[0].strip().split()[-1] for p in params]
    return len(params), names


def parse_shared_ids():
    text = read(SHARED)
    m = re.search(r'public\s+static\s+class\s+PMHeroId\s*\{(.*?)\n    \}', text, re.S)
    if not m:
        raise RuntimeError('共享表里找不到 PMHeroId')
    out = {}
    for mm in re.finditer(r'public\s+const\s+int\s+(\w+)\s*=\s*(\d+)\s*;', m.group(1)):
        if mm.group(1) == 'Count':
            continue
        out[mm.group(1)] = int(mm.group(2))
    return out


def parse_table_rows():
    """返回 [(原始行, 参数列表)]，只取 AddHero(...) 的调用行（排除方法定义）。"""
    rows = []
    for line in read(TABLE_FILE).split('\n'):
        st = line.strip()
        if st.startswith('//') or 'private static void AddHero' in st:
            continue
        if not st.startswith('AddHero('):
            continue
        m = re.match(r'^AddHero\((.*)\);\s*$', st)
        if m:
            rows.append((st, split_top_level(m.group(1))))
    return rows


def main():
    print("===== 英雄表形状校验（P3'-2）=====")
    print()

    required, total, ctor_names = parse_hero_ctor()
    print('  Hero 构造函数: 必填 %d 个 / 共 %d 个参数 -> %s' % (required, total, ', '.join(ctor_names)))
    print()

    rows = parse_table_rows()
    print('  解析到 %d 行 AddHero' % len(rows))
    print()

    # ---- 1) 行数 ----
    check('英雄表行数 = 20', len(rows) == 20, '实际 %d' % len(rows))

    # ---- 2) 形状 + 参数个数 ----
    # 一行 AddHero 的参数个数 = AddHero 自己的参数个数（后者再转交给 Hero 构造）
    expect_args, addhero_names = parse_addhero_signature()
    print('  AddHero 签名: %d 个参数 -> %s' % (expect_args, ', '.join(addhero_names)))
    print()
    shape_bad = []
    arg_bad = []
    enums = []
    by_hero = {}
    for raw_line, parts in rows:
        if not parts:
            shape_bad.append('空参数列表: %s' % raw_line[:70])
            continue
        mk = re.match(r'^HeroName\.(\w+)$', parts[0])
        if not mk:
            shape_bad.append('第一个参数不是 HeroName 常量: %s' % raw_line[:70])
            continue
        enums.append(mk.group(1))
        by_hero[mk.group(1)] = parts

        if len(parts) != expect_args:
            arg_bad.append('%s: AddHero 收到 %d 个参数，应为 %d 个'
                           % (mk.group(1), len(parts), expect_args))

        # 显示名/定位必须是字符串字面量
        for idx, label in ((1, '显示名'), (2, '定位')):
            if idx < len(parts) and not re.match(r'^".*"$', parts[idx]):
                shape_bad.append('%s 的%s不是字符串字面量: %s' % (mk.group(1), label, parts[idx]))
        # 最后一个必须是 bool 字面量
        if parts and parts[-1] not in ('true', 'false'):
            shape_bad.append('%s 的最后一个参数不是 bool 字面量: %s' % (mk.group(1), parts[-1]))

    check('每行都是 AddHero(<HeroName>, "显示名", "定位", <shell>, <大招实体>, <boom>, <bool>) 形状',
          not shape_bad, '; '.join(shape_bad[:5]))
    check('每行的参数个数与 Hero 构造函数签名相容', not arg_bad, '; '.join(arg_bad[:5]))

    # ---- 3) 无重复 ----
    dup = [e for e in set(enums) if enums.count(e) > 1]
    check('表里没有重复英雄', not dup, '重复: %s' % dup)

    # ---- 4) 集合与 PMHeroId 一致 ----
    shared = parse_shared_ids()
    check('表里的英雄集合与 PMHeroId 完全一致',
          set(enums) == set(shared.keys()),
          '表独有=%s 共享表独有=%s'
          % (sorted(set(enums) - set(shared.keys())), sorted(set(shared.keys()) - set(enums))))

    # ---- 5) 行序与编号一致 ----
    mismatch = []
    for pos, e in enumerate(enums):
        if e in shared and pos != shared[e]:
            mismatch.append('%s: 表内第 %d 行，PMHeroId=%d' % (e, pos, shared[e]))
    check('表内顺序与 PMHeroId 编号一致（错位会让人看错行）', not mismatch,
          '; '.join(mismatch[:5]))

    # ---- 6) 表现引用与冻结基准逐字段一致 ----
    if os.path.exists(BASELINE):
        base = json.load(open(BASELINE, encoding='utf-8'))['presentation']
        diffs = []
        for e, want in base.items():
            parts = by_hero.get(e)
            if parts is None:
                diffs.append('%s: 表里缺失' % e)
                continue
            # AddHero 参数 = HeroName, displayName, positioning, shell, superEntity, boom, isSuperMovingType
            got = {
                'displayName': parts[1].strip('"') if len(parts) > 1 else None,
                'positioning': parts[2].strip('"') if len(parts) > 2 else None,
                'shell': parts[3] if len(parts) > 3 else None,
                'superEntity': (None if len(parts) <= 4 or parts[4] == 'null' else parts[4]),
                'boom': (None if len(parts) <= 5 or parts[5] == 'null' else parts[5]),
                'isSuperMovingType': (len(parts) > 6 and parts[6] == 'true'),
            }
            for f in ('displayName', 'positioning', 'shell', 'superEntity', 'boom', 'isSuperMovingType'):
                if want.get(f) != got.get(f):
                    diffs.append('%s.%s: 基准=%s 实际=%s' % (e, f, want.get(f), got.get(f)))
        check('表现引用与冻结基准逐字段一致（换错预制体会被这里拦住）', not diffs,
              '; '.join(diffs[:6]))
    else:
        print('  [SKIP] 未找到表现引用基准文件，跳过该项：%s' % BASELINE)

    print()
    print('== 汇总: %d 项检查, %d 项失败 ==' % (checks, len(failures)))
    if failures:
        print()
        print('失败明细：')
        for f in failures:
            print('  - ' + f)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
