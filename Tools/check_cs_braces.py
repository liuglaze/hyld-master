"""轻量 C# 结构检查：用于在没有编译器的情况下核对改动过的文件括号是否配平。

只做「结构」判定，不做语义判定：
  - 剔除行注释、块注释、逐字字符串、普通字符串、字符字面量后统计括号；
  - **顺序感知**：追踪运行嵌套深度，报告首次「深度变负」的行号与文件末尾的残留深度；
  - 报告文件行数与括号总数，便于确认补丁插在了预期的位置。

为什么必须做顺序感知（真实踩到过的坑）：
    这段脚本最初只比对左右括号的**数量**。但 C# 的 CS1022（「类型或命名空间定义或文件尾预期」）
    典型成因是「多一个 } 且少一个 {」——数量可能刚好相等，于是纯计数会判定 BALANCED，
    而编译器直接报错。本项目在 P3'-2 用脚本删除类时就放过了一个这样的真实错误，
    直到在 Unity 里编译才暴露。现在改为追踪深度：深度出现负数或末尾不为 0，一律 FAIL。

用法：python check_cs_braces.py <file.cs> [<file.cs> ...]
退出码：0 = 全部通过；1 = 存在结构问题；2 = 用法错误
"""

import sys

BS = chr(92)      # 反斜杠
DQ = chr(34)      # 双引号
SQ = chr(39)      # 单引号


def scan(path):
    """返回 (文本, 计数表, 首个负深度行号或 None, 末尾花括号深度)。"""
    s = open(path, encoding='utf-8-sig').read()
    i, n = 0, len(s)

    counts = {'{': 0, '}': 0, '(': 0, ')': 0}
    brace_depth = 0
    first_neg_line = None
    line_no = 1

    while i < n:
        c = s[i]

        # 行注释
        if c == '/' and i + 1 < n and s[i + 1] == '/':
            while i < n and s[i] != '\n':
                i += 1
            continue

        # 块注释
        if c == '/' and i + 1 < n and s[i + 1] == '*':
            i += 2
            while i + 1 < n and not (s[i] == '*' and s[i + 1] == '/'):
                if s[i] == '\n':
                    line_no += 1
                i += 1
            i += 2
            continue

        # 逐字字符串 @"..."
        if c == '@' and i + 1 < n and s[i + 1] == DQ:
            i += 2
            while i < n:
                if s[i] == '\n':
                    line_no += 1
                if s[i] == DQ:
                    if i + 1 < n and s[i + 1] == DQ:
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue

        # 普通字符串
        if c == DQ:
            i += 1
            while i < n:
                if s[i] == BS:
                    i += 2
                    continue
                if s[i] == '\n':
                    line_no += 1
                if s[i] == DQ:
                    i += 1
                    break
                i += 1
            continue

        # 字符字面量
        if c == SQ:
            i += 1
            while i < n:
                if s[i] == BS:
                    i += 2
                    continue
                if s[i] == SQ:
                    i += 1
                    break
                i += 1
            continue

        # ---- 真正计入统计与深度 ----
        if c == '\n':
            line_no += 1
        elif c in counts:
            counts[c] += 1
            if c == '{':
                brace_depth += 1
            elif c == '}':
                brace_depth -= 1
                if brace_depth < 0 and first_neg_line is None:
                    first_neg_line = line_no

        i += 1

    return s, counts, first_neg_line, brace_depth


def main():
    paths = sys.argv[1:]
    if not paths:
        print('用法: python check_cs_braces.py <file.cs> ...')
        return 2

    ok = True
    for path in paths:
        try:
            s, c, first_neg, final_depth = scan(path)
        except Exception as e:
            print('--- %s ---' % path)
            print('  *** 读取/扫描失败: %s' % e)
            ok = False
            continue

        open_brace, close_brace = c['{'], c['}']
        open_paren, close_paren = c['('], c[')']

        count_ok = open_brace == close_brace and open_paren == close_paren
        order_ok = first_neg is None and final_depth == 0
        this_ok = count_ok and order_ok
        ok = ok and this_ok

        print('--- %s (%d 行) ---' % (path, s.count('\n') + 1))
        print('  花括号 { } = %d / %d  %s' % (
            open_brace, close_brace, 'BALANCED' if open_brace == close_brace else '*** 数量不平衡 ***'))
        print('  圆括号 ( ) = %d / %d  %s' % (
            open_paren, close_paren, 'BALANCED' if open_paren == close_paren else '*** 数量不平衡 ***'))

        if first_neg is not None:
            print('  *** 第 %d 行出现多余的 }（深度变负）—— 典型 CS1022 成因' % first_neg)
        elif final_depth != 0:
            print('  *** 末尾花括号深度 = %d（应为 0），说明有未闭合的块' % final_depth)
        else:
            print('  顺序检查: 深度全程非负且末尾归零  OK')

    print()
    print('结果:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
