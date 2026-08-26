# -*- coding: utf-8 -*-
"""找源算法的差分测试：Python 参考实现 vs 真实的 C# ImageRepair。"""
import numpy as np, os, subprocess, math, decimal
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

RT = 'rawtest'
PROBE_MAX = 1024
TAPS = 24
RING = [(math.cos(i / TAPS * 2 * math.pi), math.sin(i / TAPS * 2 * math.pi)) for i in range(TAPS)]


def rnd(f):
    """Unity 的 Mathf.RoundToInt 走银行家舍入，环上的采样点才对得齐"""
    return int(decimal.Decimal(float(f)).quantize(0, decimal.ROUND_HALF_EVEN))


def build(src_path, spots_uv):
    im = Image.open(src_path).convert('RGB')
    k = min(1.0, PROBE_MAX / float(max(im.width, im.height)))
    w, h = max(16, int(round(im.width * k))), max(16, int(round(im.height * k)))
    small = np.asarray(im.resize((w, h), Image.BILINEAR)).astype(np.uint8)
    rgba = np.dstack([small, np.full((h, w, 1), 255, np.uint8)])
    # C# 的 GetPixels 是自下而上的
    open(os.path.join(RT, 'probe.raw'), 'wb').write(rgba[::-1].tobytes())
    open(os.path.join(RT, 'probe_meta.txt'), 'w').write('%d %d' % (w, h))
    open(os.path.join(RT, 'probe_spots.txt'), 'w').write(
        '\n'.join('%.6f %.6f %.6f' % s for s in spots_uv))
    return small[::-1], w, h


def main():
    SRC = 'arw_decoded.jpg'
    spots = [(0.30, 0.45, 0.028), (0.55, 0.55, 0.020), (0.15, 0.20, 0.035),
             (0.70, 0.70, 0.025), (0.42, 0.31, 0.015), (0.80, 0.25, 0.030)]
    img, w, h = build(SRC, spots)

    def px(x, y):
        return img[min(max(y, 0), h - 1), min(max(x, 0), w - 1)].astype(np.float64) / 255.0

    def to_probe(uv):
        return (rnd(uv[0] * (w - 1)), rnd(uv[1] * (h - 1)))

    def ring_mean(uv, radius):
        cx, cy = to_probe(uv); rr = max(2, rnd(radius * 1.6 * h))
        acc = np.zeros(3)
        for dx, dy in RING:
            acc += px(cx + rnd(dx * rr), cy + rnd(dy * rr))
        return acc / TAPS

    def find_source(uv, radius):
        cx, cy = to_probe(uv); rr = max(2, rnd(radius * 1.6 * h))
        target = [px(cx + rnd(dx * rr), cy + rnd(dy * rr)) for dx, dy in RING]
        best, best_off = None, (max(4, rr * 2), 0)
        for dist in (2.2, 3.2, 4.5, 6.0):
            rad = rnd(radius * h * dist)
            for k in range(TAPS):
                ox, oy = rnd(RING[k][0] * rad), rnd(RING[k][1] * rad)
                ccx, ccy = cx + ox, cy + oy
                if ccx - rr < 0 or ccy - rr < 0 or ccx + rr >= w or ccy + rr >= h:
                    continue
                ssd = 0.0
                for i, (dx, dy) in enumerate(RING):
                    d = px(ccx + rnd(dx * rr), ccy + rnd(dy * rr)) - target[i]
                    ssd += float(np.dot(d, d))
                if best is None or ssd < best:
                    best, best_off = ssd, (ox, oy)
        return best_off

    print('缩略图 %dx%d，%d 个取样点' % (w, h, len(spots)))
    out = subprocess.run(['dotnet', os.path.join(RT, 'bin', 'Debug', 'net8.0', 'rawtest.dll'),
                          '--repair', RT], capture_output=True, text=True)
    cs = [l.split() for l in out.stdout.strip().split('\n') if l.strip()]
    if out.returncode != 0 or len(cs) != len(spots):
        print('C# 跑失败:', out.stdout, out.stderr)
        return 1

    # 判据不是"坐标逐位相同"。
    # 环上的采样点由 cos/sin 定，C# 算 float、Python 算 double，
    # 在 cos(60)*33 = 16.5 这种正好落在舍入边界的地方会差一个像素，
    # 于是评分和候选网格都会有微小差异。这是启发式搜索的固有敏感性，不是逻辑错。
    #
    # 真正该验的是：C# 选中的那个点，拿参考实现的尺子量，是不是也足够好。
    TOL = 0.15

    def score(uv, radius, off):
        cx, cy = to_probe(uv); rr = max(2, rnd(radius * 1.6 * h))
        tgt = [px(cx + rnd(dx * rr), cy + rnd(dy * rr)) for dx, dy in RING]
        s2 = 0.0
        for i, (dx, dy) in enumerate(RING):
            d = px(cx + off[0] + rnd(dx * rr), cy + off[1] + rnd(dy * rr)) - tgt[i]
            s2 += float(np.dot(d, d))
        return s2

    bad = 0
    print('  %-22s %-14s %-14s %s' % ('目标 uv', 'C# 选点', 'Py 最优', '按 Py 的尺子量'))
    for i, (u, v, r) in enumerate(spots):
        ox, oy = find_source((u, v), r)
        cox, coy = int(cs[i][0]), int(cs[i][1])
        best = score((u, v), r, (ox, oy))
        got = score((u, v), r, (cox, coy))
        ratio = got / max(best, 1e-9)
        ok = ratio <= 1.0 + TOL
        if not ok:
            bad += 1
        print('  (%.2f,%.2f) r=%.3f  (%+4d,%+4d)    (%+4d,%+4d)    劣于最优 %5.1f%%  %s'
              % (u, v, r, cox, coy, ox, oy, (ratio - 1) * 100, 'OK' if ok else 'FAIL'))

    print()
    print('超出 %.0f%% 容差的 %d / %d' % (TOL * 100, bad, len(spots)))
    return 1 if bad else 0


if __name__ == '__main__':
    raise SystemExit(main())
