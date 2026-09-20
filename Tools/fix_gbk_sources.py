"""扫描并（可选）修复 GBK 无 BOM 的 C# 源文件。

问题背景
--------
仓库里部分 .cs 是中文 Windows 的 GBK(ANSI 936) 编码，且**没有 BOM**。
本机启用了 Windows 的 "Beta: 使用 Unicode UTF-8 提供全球语言支持"（ACP=65001），
系统 ANSI 代码页不再是 936，于是 Roslyn 对无 BOM 文件按 UTF-8 宽松解码，
GBK 字节变成 U+FFFD，分三种后果：

  1) 落在**标识符**里  → error CS1056 等语法错误，编译直接失败
  2) 落在**字符串字面量**里 → 能编过，但运行期字符串是 U+FFFD（静默数据损坏）
  3) 落在**注释**里    → 无害，仅错误信息里的中文会花

修复方式：把文件从 GBK 转成 UTF-8 + BOM。
带 BOM 的理由：编码对任何工具都无歧义，且仓库中本来就含中文的文件多数已带 BOM。

用法
----
  python Tools/fix_gbk_sources.py            # 只扫描报告，不改文件
  python Tools/fix_gbk_sources.py --apply    # 执行转换
"""

import os
import re
import sys

DQ = chr(34)
BS = chr(92)
BS2 = BS + BS
SQ = chr(39)

# C# 字符串字面量（含转义）；模式文本里的反斜杠必须是两个
STR_LITERAL = re.compile(DQ + '(?:[^' + DQ + BS2 + ']|' + BS2 + '.)*' + DQ)

UTF8_BOM = b'\xef\xbb\xbf'


def strip_comments_and_strings(text):
    """去掉注释、字符串字面量与预处理指令的“消息”部分。

    剩下的正文里若含非 ASCII，才是真正的**标识符/关键字**位置。

    注意 #region / #endregion：它们后面跟的是“消息”（实质是注释），
    不是标识符。若不当指令处理，`#region 字段` 会被误判成标识符含中文
    （这是本脚本初版真实踩过的误报）。其余指令（#if/#define/#pragma）
    后面可能真是标识符，因此不做跳过。
    """
    out = []
    i, n = 0, len(text)
    line_start = True
    while i < n:
        c = text[i]

        # 预处理指令行：仅 #region / #endregion 跳过后面的消息
        if line_start and c == '#':
            j = i + 1
            while j < n and text[j] in ' \t':
                j += 1
            k = j
            while k < n and (text[k].isalpha() or text[k] == '_'):
                k += 1
            directive = text[j:k].lower()
            if directive in ('region', 'endregion'):
                while i < n and text[i] != '\n':
                    i += 1
                line_start = True
                continue

        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                i += 1
            i += 2
            continue
        if c == '@' and i + 1 < n and text[i + 1] == DQ:
            i += 2
            while i < n:
                if text[i] == DQ:
                    if i + 1 < n and text[i + 1] == DQ:
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            line_start = False
            continue
        if c == DQ:
            i += 1
            while i < n:
                if text[i] == BS:
                    i += 2
                    continue
                if text[i] == DQ:
                    i += 1
                    break
                i += 1
            line_start = False
            continue
        if c == SQ:
            i += 1
            while i < n:
                if text[i] == BS:
                    i += 2
                    continue
                if text[i] == SQ:
                    i += 1
                    break
                i += 1
            line_start = False
            continue

        out.append(c)
        # 行首判定必须容忍缩进：`        #region 字段` 前面有空格，
        # 若在第一个空格就把 line_start 置 False，到 '#' 时就认不出指令了。
        if c == '\n':
            line_start = True
        elif line_start and c not in ' \t':
            line_start = False
        i += 1
    return ''.join(out)


def collect_bad_sources(root):
    """返回 [(路径, 原始字节)]：所有非 UTF-8（且能被 GBK 解码）的 .cs。"""
    bad = []
    for dp, dn, fn in os.walk(root):
        if '.git' in dp:
            continue
        for f in fn:
            if not f.endswith('.cs'):
                continue
            p = os.path.join(dp, f)
            raw = open(p, 'rb').read()
            if raw[:3] == UTF8_BOM:
                continue
            try:
                raw.decode('utf-8')
            except UnicodeDecodeError:
                bad.append((p, raw))
    return bad


def classify(raw):
    """把非 ASCII 的出现位置分类。GBK 解不开时返回 None。"""
    try:
        text = raw.decode('gbk')
    except UnicodeDecodeError:
        return None

    code_only = strip_comments_and_strings(text)
    id_zh = sum(1 for c in code_only if ord(c) > 127)
    zh_literals = [s for s in STR_LITERAL.findall(text) if any(ord(c) > 127 for c in s)]
    nonascii = sum(1 for b in raw if b > 127)
    return {
        'text': text,
        'nonascii_bytes': nonascii,
        'identifier_zh': id_zh,
        'zh_literals': zh_literals,
    }


def main():
    apply = '--apply' in sys.argv
    root = 'Client/Assets'

    bad = collect_bad_sources(root)
    print('扫描 %s 下 .cs' % root)
    print('非 UTF-8（无 BOM）文件数 =', len(bad))
    print()

    must_fix, latent, comment_only, undecodable = [], [], [], []

    for p, raw in bad:
        info = classify(raw)
        short = p.replace('Client/Assets/', '').replace(BS, '/')
        if info is None:
            undecodable.append(p)
            print('  [GBK 也解不开] %s' % short)
            continue
        if info['identifier_zh'] > 0:
            must_fix.append((p, info))
            verdict = '编译会失败（标识符含中文 %d 字符）' % info['identifier_zh']
        elif info['zh_literals']:
            latent.append((p, info))
            verdict = '能编过但 %d 个中文字符串会损坏' % len(info['zh_literals'])
        elif info['nonascii_bytes']:
            comment_only.append((p, info))
            verdict = '仅注释含中文（无害）'
        else:
            verdict = '无非 ASCII（可忽略）'
        print('  %-58s %4d字节  %s' % (short[:58], info['nonascii_bytes'], verdict))

    print()
    print('========== 汇总 ==========')
    print('  1) 编译会失败            : %d' % len(must_fix))
    for p, i in must_fix:
        print('       %s' % p)
    print('  2) 能编过但字符串会损坏  : %d' % len(latent))
    for p, i in latent:
        print('       %s  (%d 个中文字面量)' % (p, len(i['zh_literals'])))
    print('  3) 仅注释含中文（无害）  : %d' % len(comment_only))
    for p, i in comment_only:
        print('       %s' % p)
    if undecodable:
        print('  4) GBK 也解不开          : %d' % len(undecodable))
        for p in undecodable:
            print('       %s' % p)

    if not apply:
        print()
        print('（未改动任何文件；加 --apply 执行转换）')
        return 0

    # ---- 转换：GBK -> UTF-8 + BOM ----
    print()
    print('========== 执行转换（GBK -> UTF-8 + BOM）==========')
    changed = 0
    for p, raw in bad:
        info = classify(raw)
        if info is None:
            print('  跳过（解不开）: %s' % p)
            continue
        new = UTF8_BOM + info['text'].encode('utf-8')
        if new == raw:
            continue
        # 安全检查：转换后再解码回来必须与原文一致（内容零损失）
        assert new[3:].decode('utf-8') == info['text'], '往返校验失败: %s' % p
        open(p, 'wb').write(new)
        changed += 1
        print('  已转换: %s' % p.replace('Client/Assets/', '').replace(BS, '/'))
    print('共转换 %d 个文件' % changed)
    return 0


if __name__ == '__main__':
    sys.exit(main())
