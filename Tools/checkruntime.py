# -*- coding: utf-8 -*-
"""按出包的条件编译一遍运行时代码。

`Assets/Editor/` 里的代码不进包，`Assets/Scripts/` 里的进包。
所以 Scripts 里任何一处 `using UnityEditor;` 都是定时炸弹：
编辑器里一切正常，一出包就是编译失败或者功能静默失效。

Unity 自己在编辑器里编 Assembly-CSharp 时**是带 UnityEditor 引用的**，
所以在编辑器里根本发现不了。这个脚本把那些引用和 UNITY_EDITOR 宏都摘掉再编一遍，
逼出真实的出包条件。

    python Tools/checkruntime.py
"""
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOVE = os.path.join(ROOT, 'LOVE')
CSC = r'C:\Program Files\Unity 2022.3.62f3\Editor\Data\DotNetSdkRoslyn\csc.dll'


def find_rsp():
    """Bee 把完整的 csc 命令行留在 rsp 里，引用列表照抄就行。"""
    pats = os.path.join(LOVE, 'Library', 'Bee', 'artifacts', '*.dag', 'Assembly-CSharp.rsp')
    hits = glob.glob(pats)
    if not hits:
        print('找不到 Assembly-CSharp.rsp。先让 Unity 编译一次。')
        print('  找过：' + pats)
        sys.exit(2)
    return max(hits, key=os.path.getmtime)


def strip_comments(src):
    """去掉注释和字符串字面量，免得注释里提一句"UnityEditor"就被当成违规。"""
    src = re.sub(r'/\*.*?\*/', ' ', src, flags=re.S)
    src = re.sub(r'//[^\n]*', ' ', src)
    src = re.sub(r'"(?:\\.|[^"\\])*"', '""', src)
    return src


def strip_editor_only(src):
    """挖掉 `#if UNITY_EDITOR` 里的内容。

    那种写法是合法的——出包时那段根本不参与编译。
    `TitleScreen.HandleQuit` 就是这么写的：编辑器里停 Play，出包里 Application.Quit。

    只认最外层的条件，不求解嵌套里的复杂表达式；这条检查是防手滑的，
    真正的判据是下面那趟按出包条件的编译。
    """
    out, skip_depth, depth = [], 0, 0

    for line in src.split('\n'):
        t = line.strip()

        if t.startswith('#if'):
            depth += 1
            expr = t[3:].lstrip('defined').strip()
            if skip_depth == 0 and 'UNITY_EDITOR' in expr and '!' not in expr:
                skip_depth = depth
            out.append('')
            continue

        if t.startswith('#elif') or t.startswith('#else'):
            # #else 分支在出包时是要编译的，从这里开始恢复
            if skip_depth == depth:
                skip_depth = 0
            out.append('')
            continue

        if t.startswith('#endif'):
            if skip_depth == depth:
                skip_depth = 0
            depth = max(0, depth - 1)
            out.append('')
            continue

        out.append('' if skip_depth else line)

    return '\n'.join(out)


def grep_check(files):
    """文本层面再查一遍。

    光靠编译查不干净：一堆包的编辑器程序集在 UnityEditor.* 下声明了类型，
    所以一个没被用到的 `using UnityEditor;` 编得过——可真出包时那些程序集
    根本不存在，照样报错。编译能抓住"真去调 API"，这一趟抓住"光引了命名空间"，
    两道合起来才盖全。
    """
    bad = []
    for p in files:
        with open(p, encoding='utf-8') as f:
            body = strip_comments(strip_editor_only(f.read()))
        for m in re.finditer(r'\bUnityEditor\b', body):
            line = body.count('\n', 0, m.start()) + 1
            bad.append((p, line))
            break
    return bad


def main():
    rsp = find_rsp()
    print('参照 rsp:', os.path.relpath(rsp, ROOT))

    with open(rsp, encoding='utf-8') as f:
        lines = f.read().replace('\r\n', '\n').split('\n')

    out = tempfile.mkdtemp(prefix='loveruntime')
    kept, dropped_refs = [], 0

    for ln in lines:
        s = ln.strip()
        if not s:
            continue
        # 源文件列表整个换掉，用当前磁盘上的
        if re.search(r'\.cs"?$', s):
            continue
        # 编辑器引用：出包时一条都没有。
        #
        # 不能只摘 "UnityEditor"：一堆包的编辑器程序集（Unity.Sentis.Editor 之类）
        # 在 UnityEditor.* 下声明了类型，留着它们的话 `using UnityEditor;` 依然合法，
        # 只有真去用 EditorPrefs 这种才报错——那就漏掉了一半问题。
        # 出包时这些程序集本来就不存在，全摘才是真实条件。
        if re.match(r'-r:.*[\\/][^\\/]*Editor[^\\/]*\.dll"?$', s, re.IGNORECASE):
            dropped_refs += 1
            continue
        # 这个宏出包时也没有
        if s.startswith('-define:UNITY_EDITOR'):
            continue
        if s.startswith('-out:'):
            s = '-out:"%s"' % os.path.join(out, 'Runtime.dll').replace('\\', '/')
        elif s.startswith('-refout:'):
            s = '-refout:"%s"' % os.path.join(out, 'Runtime.ref.dll').replace('\\', '/')
        kept.append(s)

    srcs = []
    for dirpath, _, names in os.walk(os.path.join(LOVE, 'Assets', 'Scripts')):
        for n in names:
            if n.endswith('.cs'):
                p = os.path.relpath(os.path.join(dirpath, n), LOVE).replace('\\', '/')
                srcs.append('"%s"' % p)

    print('摘掉 %d 条编辑器引用，编 %d 个源文件' % (dropped_refs, len(srcs)))

    text_bad = grep_check([os.path.join(LOVE, s.strip('"').replace('/', os.sep)) for s in srcs])
    if text_bad:
        print()
        for p, line in text_bad:
            print('  %s:%d  引了 UnityEditor' % (os.path.relpath(p, LOVE), line))
        print()
        print('FAILURES: %d' % len(text_bad))
        print('运行时代码里不该出现 UnityEditor。编辑器专有的能力走')
        print('Love.Tools.AppHost 注入，或者把这个文件放回 Assets/Editor/。')
        shutil.rmtree(out, ignore_errors=True)
        return 1

    args = os.path.join(out, 'check.rsp')
    with open(args, 'w', encoding='utf-8') as f:
        f.write('\n'.join(kept + sorted(srcs)))

    r = subprocess.run(['dotnet', CSC, '@' + args], cwd=LOVE,
                       capture_output=True, text=True, errors='replace')
    text = (r.stdout or '') + (r.stderr or '')
    errs = [l for l in text.split('\n') if 'error CS' in l]

    shutil.rmtree(out, ignore_errors=True)

    # csc 没跑起来（路径不对、dotnet 缺失）时也是没有 error CS 行的，
    # 不看退出码的话这里会报"通过"——一个永远绿的检查比没有检查更糟
    if r.returncode != 0 and not errs:
        print()
        # 控制台可能是 GBK，编译器的原始输出里什么字符都可能有
        print(text.strip()[:1500].encode('ascii', 'replace').decode('ascii'))
        print()
        print('FAILURES: 1')
        print('编译器没能正常跑起来（退出码 %d），这一趟什么也没验到。' % r.returncode)
        return 2

    if errs:
        print()
        for e in errs[:30]:
            print('  ' + e.strip())
        print()
        print('FAILURES: %d' % len(errs))
        print('运行时代码碰了编辑器 API。能跑在运行时的应当放 Assets/Scripts/，')
        print('只有编辑器能用的（AssetDatabase / EditorPrefs 之类）走 Love.Tools.AppHost 注入。')
        return 1

    print('FAILURES: 0')
    print('运行时代码在没有 UnityEditor 的条件下编译通过。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
