# -*- coding: utf-8 -*-
"""
不开 Unity，离线编译 shader 的每个 Pass。

做法：把 CGPROGRAM..ENDCG 的内容抽出来（CGINCLUDE 块拼到前面），
补上 Unity 会注入的那批预处理宏，交给 Windows SDK 的 fxc 编译顶点和片元两个入口。
带 shader_feature 的 Pass 会把关键字全开和全关各编一遍，两条分支都覆盖到。

    python Tools/shadercheck.py                    # 默认 VideoGrade.shader
    python Tools/shadercheck.py MaskBrush.shader

需要 Windows SDK 的 fxc.exe。路径写死在下面，换机器改一下就行。

注意：这验的是 HLSL 能不能编译，不验画面对不对。着色逻辑的正确性还得进 Unity 看。
"""
import re, os, subprocess, sys, tempfile

FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe'
CGINC = r'C:\Program Files\Unity 2022.3.62f3\Editor\Data\CGIncludes'
SHDIR = r'E:\GalgameLOVE\LOVE\Assets\GameAssets\Shaders'

# Unity 自己会注入的一批宏。少了它们 HLSLSupport.cginc 走不到 D3D 分支
BASE_DEFS = [
    'SHADER_API_D3D11=1', 'SHADER_TARGET=40', 'UNITY_VERSION=202230',
    'UNITY_NO_SCREENSPACE_SHADOWS=1',
    'UNITY_PBS_USE_BRDF1=1', 'UNITY_SPECCUBE_BOX_PROJECTION=1',
    'UNITY_LIGHT_PROBE_PROXY_VOLUME=1',
]


def passes(path):
    src = open(path, encoding='utf-8').read()
    out = []

    # CGINCLUDE 块是所有 Pass 共享的前导，要拼到每段 CGPROGRAM 前面
    shared = ''
    mi = re.search(r'CGINCLUDE(.*?)ENDCG', src, re.S)
    if mi:
        shared = re.sub(r'^\s*#pragma.*$', '', mi.group(1), flags=re.M)
        src = src[:mi.start()] + src[mi.end():]

    # 每个 Pass 里的一段 CGPROGRAM
    for m in re.finditer(r'CGPROGRAM(.*?)ENDCG', src, re.S):
        body = m.group(1)
        vert = re.search(r'#pragma\s+vertex\s+(\w+)', body)
        frag = re.search(r'#pragma\s+fragment\s+(\w+)', body)
        feats = re.findall(r'#pragma\s+shader_feature(?:_local)?\s+(\w+)', body)
        if not vert or not frag:
            continue
        # 注释里标的 Pass 号
        head = src[:m.start()]
        tag = re.findall(r'Pass (\d+)[：:]([^\n]*)', head)
        name = ('Pass %s %s' % tag[-1]).strip() if tag else 'Pass ?'
        clean = shared + re.sub(r'^\s*#pragma.*$', '', body, flags=re.M)
        out.append((name, clean, vert.group(1), frag.group(1), feats))
    return out


def compile_one(body, entry, profile, defines, label):
    with tempfile.NamedTemporaryFile('w', suffix='.hlsl', delete=False,
                                     encoding='utf-8', dir=SHDIR) as f:
        f.write(body)
        tmp = f.name
    try:
        # /Gec 向后兼容：Unity 自己的 UnityShaderVariables.cginc 里有全局 half 变量，
        # SM4.0 严格模式下不允许，但 Unity 的编译器是开着这个开关的
        args = [FXC, '/nologo', '/Gec', '/T', profile, '/E', entry, '/I', CGINC, '/I', SHDIR,
                '/Fo', 'NUL']
        for d in defines:
            args += ['/D', d]
        args.append(tmp)
        r = subprocess.run(args, capture_output=True, text=True, errors='replace')
        if r.returncode != 0:
            msg = (r.stdout + r.stderr).strip().replace(tmp, label)
            return msg
        return None
    finally:
        try: os.unlink(tmp)
        except OSError: pass


def main():
    path = os.path.join(SHDIR, sys.argv[1] if len(sys.argv) > 1 else 'VideoGrade.shader')
    ps = passes(path)
    print('%s: %d passes' % (os.path.basename(path), len(ps)))
    bad = 0

    for name, body, vert, frag, feats in ps:
        # 关键字全开和全关各编一遍，两条分支都要覆盖
        variants = [('kw-off', list(BASE_DEFS))]
        if feats:
            variants.append(('kw-on ', BASE_DEFS + [f + '=1' for f in feats]))

        for vname, defs in variants:
            errs = []
            for entry, profile in ((vert, 'vs_4_0'), (frag, 'ps_4_0')):
                e = compile_one(body, entry, profile, defs, name)
                if e: errs.append('%s(%s): %s' % (entry, profile, e))
            status = 'OK' if not errs else 'FAIL'
            print('  %-10s %-8s %s' % (name, vname, status))
            for e in errs:
                bad += 1
                for line in e.split('\n')[:6]:
                    if line.strip(): print('      ' + line.strip())

    print()
    print('FAILURES: %d' % bad)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
