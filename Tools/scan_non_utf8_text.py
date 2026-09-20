"""全仓扫描「非 UTF-8 的文本文件」，用于发现同类编码问题的影响面。

为什么需要：
  本机 ACP=65001（Windows UTF-8 全球语言支持 Beta），GBK 无 BOM 的文件会被
  按 UTF-8 宽松解码成 U+FFFD。对 .cs 会导致编译失败或字符串静默损坏；
  对其他被当文本解析的文件（.json/.asset/.shader/.proto/.txt/.xml 等）同样可能出问题。

判定方式：
  - **只扫本仓库的源与配置**：跳过 Unity 生成目录（Library/ Temp/ obj/ 等，见 PRUNE_DIRS）；
  - 排除已知二进制扩展名；
  - 若含 NUL 字节 → 视为二进制，跳过；
  - 若为合法 UTF-8 → 正常；
  - 若非 UTF-8 但能被 GBK 解码 → 报为「GBK 无 BOM」。

  PRUNE_DIRS 是必要的，不是优化：`Client/Library/PackageCache/` 里是 Unity 从包仓库
  解出来的**第三方**内容（含它自己的文档），既不是我们的源码、也被 .gitignore 排除。
  不剪掉它就会稳定吐出一个假阳（com.unity.textmeshpro 的一份 .md），
  让「可疑文件」这个信号失去意义。
"""

import os
import sys

BINARY_EXT = set([
    '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tga', '.psd', '.exr', '.hdr',
    '.dll', '.exe', '.so', '.a', '.bundle', '.pdb', '.mdb', '.lib', '.obj',
    '.fbx', '.max', '.blend', '.anim', '.mp3', '.ogg', '.wav', '.mp4', '.mov',
    '.uasset', '.umap', '.unity3d', '.assetbundle', '.bytes', '.ttf', '.otf',
    '.zip', '.7z', '.rar', '.gz', '.jar', '.class', '.pyc', '.pak',
    '.cubin', '.nfx', '.cuid', '.resS', '.resource',
])

TEXT_EXT = set([
    '.cs', '.json', '.txt', '.xml', '.proto', '.shader', '.cginc', '.hlsl',
    '.compute', '.yaml', '.yml', '.md', '.cfg', '.ini', '.config', '.properties',
    '.js', '.lua', '.py', '.bat', '.cmd', '.ps1', '.sh', '.csv', '.tsv',
    '.asset', '.meta', '.mat', '.asmdef', '.preset', '.shadersubgraph',
    '.uxml', '.uss', '.tss', '.gradle', '.pro', '.plist', '.storyboard',
])

# 构建/缓存产物目录：不是本仓库维护的源码，一律不扫。
# 名字按「目录是否恰好等于其中之一」匹配（大小写不敏感），不做路径前缀匹配 ——
# 避免误伤诸如 `Client/Assets/Scripts/MyLibrary/` 这种正常的源码目录。
PRUNE_DIRS = set([
    # Unity
    'library', 'temp', 'obj', 'logs', 'usersettings', 'memorycaptures',
    'build', 'builds',
    # .NET / 编辑器 / 包管理
    'bin', '.vs', 'node_modules', '__pycache__', '.idea',
])


def is_probably_binary(data):
    if b'\x00' in data[:8192]:
        return True
    return False


def main():
    roots = sys.argv[1:] or ['Client', 'Server', 'ProtobufAndNotepad', 'Tools', 'Docs']
    bad = []
    scanned = 0
    pruned = []

    for root in roots:
        if not os.path.isdir(root):
            continue
        for dp, dn, fn in os.walk(root):
            if '.git' in dp:
                continue

            # 剪掉生成目录（就地修改 dn 才能阻止 os.walk 继续下探）
            kept = [d for d in dn if d.lower() not in PRUNE_DIRS]
            for d in dn:
                if d.lower() in PRUNE_DIRS:
                    pruned.append(os.path.join(dp, d))
            dn[:] = kept

            for f in fn:
                ext = os.path.splitext(f)[1].lower()
                if ext in BINARY_EXT:
                    continue
                if ext and ext not in TEXT_EXT:
                    # 未知扩展名：仍扫，但只在发现问题时报告
                    pass
                p = os.path.join(dp, f)
                try:
                    if os.path.getsize(p) > 8 * 1024 * 1024:
                        continue
                    data = open(p, 'rb').read()
                except OSError:
                    continue
                if is_probably_binary(data):
                    continue
                scanned += 1
                if data[:3] == b'\xef\xbb\xbf':
                    continue
                try:
                    data.decode('utf-8')
                except UnicodeDecodeError:
                    try:
                        data.decode('gbk')
                        bad.append((p, ext, 'GBK 无 BOM'))
                    except UnicodeDecodeError:
                        bad.append((p, ext, '既非 UTF-8 也非 GBK'))

    print('已扫描文本文件 =', scanned)
    if pruned:
        print('已跳过生成目录 = %d 个（如 %s 等）' % (len(pruned), pruned[0].replace('\\', '/')))
    print('可疑文件 =', len(bad))
    print()
    if bad:
        by_ext = {}
        for p, ext, why in bad:
            by_ext.setdefault(ext, []).append((p, why))
        for ext in sorted(by_ext):
            print('[%s]  %d 个' % (ext or '(无扩展名)', len(by_ext[ext])))
            for p, why in by_ext[ext][:40]:
                print('    %-78s %s' % (p, why))
    return 0


if __name__ == '__main__':
    sys.exit(main())
