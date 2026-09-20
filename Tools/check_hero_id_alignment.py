# -*- coding: utf-8 -*-
"""P3'-2 门禁：英雄编号三处一致性校验。

为什么需要它：
    共享数值表用 `int heroId` 作键，这个编号必须同时等于：
      1) 权威 proto 枚举 SocketProto.Hero 的值（网络上的身份）
      2) 客户端手写枚举 HeroName 的值（客户端表 Heros 的键）
      3) 共享表里 PMHeroId 的常量（两端读数值的键）

    三者一旦漂移，表现是「某个英雄的数值静默变成另一个英雄的」——
    不会崩溃、不会报错，只会让战斗结果莫名其妙。所以必须逐值校验。

这是编译门禁（Tools/PMSharedConfigCheck）覆盖不到的部分：
    编译只能证明「能编过」，证明不了「编号含义对齐」。

退出码：0 = 全部一致；1 = 存在不一致（可用于 CI / 提交前检查）。
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

PROTO = os.path.join(ROOT, 'ProtobufAndNotepad', 'Protobuf', 'SocketProto.proto')

# 客户端手写枚举 HeroName 的位置。
# P3'-2 把它从 HYLDStaticValue.cs 抽到了 HeroData.cs（连同 Hero / SuperBulletParams），
# 以便用最小桩件建立真实编译门禁。这里兼容两个位置，避免以后再搬家就失效。
CLIENT_ENUM_CANDIDATES = [
    os.path.join(ROOT, 'Client', 'Assets', 'HYLD1.0', 'Scripts', 'OldScripts', 'HeroData.cs'),
    os.path.join(ROOT, 'Client', 'Assets', 'HYLD1.0', 'Scripts', 'OldScripts', 'HYLDStaticValue.cs'),
]

SHARED = os.path.join(ROOT, 'Client', 'Assets', 'Scripts', 'Shared', 'BattleNumericConfig.cs')

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


def parse_proto_hero():
    text = read(PROTO)
    m = re.search(r'enum\s+Hero\s*\{(.*?)\}', text, re.S)
    if not m:
        raise RuntimeError('proto 里找不到 enum Hero')
    return {mm.group(1): int(mm.group(2)) for mm in re.finditer(r'(\w+)\s*=\s*(\d+)\s*;', m.group(1))}


def parse_client_enum():
    path = None
    for candidate in CLIENT_ENUM_CANDIDATES:
        if os.path.exists(candidate):
            path = candidate
            break

    if path is None:
        raise RuntimeError('找不到客户端 HeroName 枚举所在文件，已尝试：%s' % CLIENT_ENUM_CANDIDATES)

    text = read(path)
    m = re.search(r'public\s+enum\s+HeroName\s*\{(.*?)\n\}', text, re.S)
    if not m:
        raise RuntimeError('客户端 %s 里找不到 public enum HeroName' % path)
    body = re.sub(r'//[^\n]*', '', m.group(1))          # 去行注释
    out = {}
    nxt = 0
    for item in [x.strip() for x in body.split(',') if x.strip()]:
        mm = re.match(r'(\w+)\s*=\s*(\d+)', item)
        if mm:
            out[mm.group(1)] = int(mm.group(2))
            nxt = int(mm.group(2)) + 1
        else:
            mm2 = re.match(r'^(\w+)$', item)
            if mm2:
                out[mm2.group(1)] = nxt
                nxt += 1
    return out


def parse_shared_ids():
    text = read(SHARED)
    m = re.search(r'public\s+static\s+class\s+PMHeroId\s*\{(.*?)\n    \}', text, re.S)
    if not m:
        raise RuntimeError('共享表里找不到 PMHeroId')
    out = {}
    for mm in re.finditer(r'public\s+const\s+int\s+(\w+)\s*=\s*(\d+)\s*;', m.group(1)):
        name = mm.group(1)
        if name == 'Count':      # Count 是容量常量，不是英雄
            continue
        out[name] = int(mm.group(2))
    return out


def parse_shared_count():
    text = read(SHARED)
    m = re.search(r'public\s+const\s+int\s+Count\s*=\s*(\d+)\s*;', text)
    return int(m.group(1)) if m else None


def main():
    print('===== 英雄编号一致性校验（P3\'-2）=====')
    print()

    proto = parse_proto_hero()
    client = parse_client_enum()
    shared = parse_shared_ids()
    shared_count = parse_shared_count()

    print('  来源规模: proto %d 个 / 客户端 HeroName %d 个 / PMHeroId %d 个'
          % (len(proto), len(client), len(shared)))
    print()

    def lower_map(d):
        return {k.lower(): (k, v) for k, v in d.items()}

    pl, cl, sl = lower_map(proto), lower_map(client), lower_map(shared)

    # ---- 1) proto vs 客户端 ----
    check('proto 与客户端 HeroName 的英雄集合相同',
          set(pl.keys()) == set(cl.keys()),
          'proto-only=%s client-only=%s' % (sorted(set(pl) - set(cl)), sorted(set(cl) - set(pl))))

    # ---- 2) proto vs 共享表 ----
    check('proto 与 PMHeroId 的英雄集合相同',
          set(pl.keys()) == set(sl.keys()),
          'proto-only=%s shared-only=%s' % (sorted(set(pl) - set(sl)), sorted(set(sl) - set(pl))))

    # ---- 3) 逐值一致 ----
    mismatches = []
    for key in sorted(set(pl) & set(cl) & set(sl)):
        pv, cv, sv = pl[key][1], cl[key][1], sl[key][1]
        if not (pv == cv == sv):
            mismatches.append('%s: proto=%d client=%d shared=%d' % (pl[key][0], pv, cv, sv))
    check('每个英雄在 proto / 客户端 / 共享表 三处的编号完全相同', not mismatches,
          '; '.join(mismatches))

    # ---- 4) 编号连续且从 0 开始（proto 里 enum 必须连续，否则网络取值会跳号）----
    vals = sorted(proto.values())
    check('proto 编号从 0 开始且连续', vals == list(range(len(vals))),
          '实际=%s' % vals)

    # ---- 5) Count 常量与实际条目数一致 ----
    check('PMHeroId.Count 等于实际英雄数', shared_count == len(proto),
          'Count=%s 实际=%d' % (shared_count, len(proto)))

    # ---- 6) 共享表的数组长度声明用 count 常量（避免写死数字漂移）----
    text = read(SHARED)
    hard = re.findall(r'new\s+(?:HeroNumeric|SuperNumeric|bool)\[(\d+)\]', text)
    check('共享表数组长度用 PMHeroId.Count 而不是硬编码数字', not hard,
          '发现硬编码长度: %s' % hard)

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
