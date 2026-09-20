"""校验 GBK -> UTF-8+BOM 转换是否零内容损失。

为什么不能用 `git diff` 直接看：
  本仓库 core.autocrlf=true —— 仓库内（git object）存 LF，检出到工作区是 CRLF。
  因此 `git show HEAD:file` 拿到的是 LF 版本，与工作区的 CRLF 版本逐字符比较必然不等。
  必须先把两侧行尾归一到 LF 再比内容。

本脚本对每个被转换的文件做三项检查：
  1. 新文件必须是 UTF-8 且带 BOM；
  2. 新文件去掉 BOM 后按 UTF-8 解码，与 HEAD 版本按 GBK 解码的结果，
     在**行尾归一化之后**必须逐字符相同（内容零损失）；
  3. 逐字节对比 ASCII 部分：转换只应改变非 ASCII 字节，ASCII 结构不应变。
"""

import subprocess
import sys


def git_show(path):
    r = subprocess.run(['git', 'show', 'HEAD:' + path], capture_output=True)
    if r.returncode != 0:
        return None
    return r.stdout


def norm_eol(s):
    return s.replace('\r\n', '\n').replace('\r', '\n')


def main():
    paths = sys.argv[1:]
    if not paths:
        # 默认取 git 报告的改动 .cs
        out = subprocess.run(['git', 'diff', '--name-only', '--', 'Client/Assets'],
                             capture_output=True, text=True).stdout.split()
        paths = [p for p in out if p.endswith('.cs')]

    print('待校验文件 %d 个' % len(paths))
    print()

    ok, ng, skipped = 0, 0, 0
    for p in paths:
        old = git_show(p)
        if old is None:
            print('  [跳过] %s （HEAD 中不存在）' % p)
            skipped += 1
            continue

        # 仅校验「原本非 UTF-8」的文件——那才是本次转换的目标
        try:
            old.decode('utf-8')
            is_utf8_already = True
        except UnicodeDecodeError:
            is_utf8_already = False

        if is_utf8_already:
            skipped += 1
            continue

        new = open(p, 'rb').read()

        # 检查 1：新文件必须 UTF-8 + BOM
        if new[:3] != b'\xef\xbb\xbf':
            print('  [FAIL] %s : 新文件缺 UTF-8 BOM' % p)
            ng += 1
            continue
        try:
            new_txt = new[3:].decode('utf-8')
        except UnicodeDecodeError as e:
            print('  [FAIL] %s : 新文件不是合法 UTF-8 (%s)' % (p, e))
            ng += 1
            continue

        # 检查 2：内容零损失（行尾归一后逐字符比较）
        old_txt = old.decode('gbk')
        a, b = norm_eol(old_txt), norm_eol(new_txt)
        if a != b:
            i = next((k for k in range(min(len(a), len(b))) if a[k] != b[k]),
                     min(len(a), len(b)))
            print('  [FAIL] %s : 内容不同 @%d' % (p, i))
            print('         old=%r' % a[max(0, i - 20):i + 40])
            print('         new=%r' % b[max(0, i - 20):i + 40])
            ng += 1
            continue

        # 检查 3：ASCII 结构不变（仅比较 ASCII 字符序列）
        old_ascii = ''.join(c for c in a if ord(c) < 128)
        new_ascii = ''.join(c for c in b if ord(c) < 128)
        if old_ascii != new_ascii:
            print('  [FAIL] %s : ASCII 结构变化' % p)
            ng += 1
            continue

        ok += 1

    print()
    print('  通过 = %d   失败 = %d   跳过(非本次转换目标) = %d' % (ok, ng, skipped))
    return 0 if ng == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
