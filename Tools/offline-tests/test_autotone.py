# -*- coding: utf-8 -*-
"""
自适应起手值的效果验证。

造几种典型的"问题片"，跑一遍 C# 的 AutoTone，把它给的参数套回去，
量三件事：中位亮度有没有靠近中级灰、溢出有没有变多、平片的反差有没有拉开。
"""
import numpy as np, os, subprocess
from PIL import Image, ImageDraw
Image.MAX_IMAGE_PIXELS = None

RT = 'rawtest'
DLL = os.path.join(RT, 'bin', 'Debug', 'net8.0', 'rawtest.dll')

# 中级灰 0.18 线性，换成 gamma 空间大约是 0.46
TARGET = 0.4663


def g2l(v):
    v = np.asarray(v, np.float64)
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def l2g(v):
    v = np.clip(np.asarray(v, np.float64), 0, None)
    return np.where(v <= 0.0031308, v * 12.92, 1.055 * v ** (1 / 2.4) - 0.055)


def luma(img):
    return 0.2126 * img[..., 0] + 0.7152 * img[..., 1] + 0.0722 * img[..., 2]


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def run_cs(img8, wb=False):
    h, w, _ = img8.shape
    rgba = np.dstack([img8, np.full((h, w, 1), 255, np.uint8)])
    open(os.path.join(RT, 'probe.raw'), 'wb').write(rgba.tobytes())
    open(os.path.join(RT, 'probe_meta.txt'), 'w').write('%d %d' % (w, h))
    cmd = ['dotnet', DLL, '--autotone', RT] + (['wb'] if wb else [])
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(r.stdout + r.stderr)
    lines = [l for l in r.stdout.strip().split('\n') if l.strip()]
    p = [float(x) for x in lines[0].split()]
    return dict(zip(['exposure', 'inBlack', 'inWhite', 'highlights', 'shadows',
                     'contrast', 'temperature', 'tint'], p)), lines[1]


def apply_params(img8, p):
    """按管线的顺序把参数套回去：曝光(线性) -> 色阶 -> 对比度 -> 高光/阴影，都在 gamma 空间"""
    g = img8.astype(np.float64) / 255.0

    lin = g2l(g) * (2.0 ** p['exposure'])
    g = l2g(lin)

    span = max(p['inWhite'] - p['inBlack'], 1e-4)
    g = np.clip((g - p['inBlack']) / span, 0, None)

    g = np.maximum((g - 0.5) * p['contrast'] + 0.5, 0)

    y = luma(g)[..., None]
    g = g * (1 + p['shadows'] * (1 - smoothstep(0, 0.5, y)) + p['highlights'] * smoothstep(0.5, 1, y))
    return np.clip(g, 0, 1)


def stats(g01):
    y = luma(g01).ravel()
    return dict(median=float(np.median(y)),
                iqr=float(np.percentile(y, 75) - np.percentile(y, 25)),
                clip_hi=float((y > 0.98).mean()),
                clip_lo=float((y < 0.02).mean()))


def main():
    base = np.asarray(Image.open('arw_decoded.jpg').convert('RGB')
                      .resize((900, 597), Image.BILINEAR)).astype(np.uint8)
    sea = np.asarray(Image.open('src_f0.png').convert('RGB')
                     .resize((900, 506), Image.BILINEAR)).astype(np.uint8)

    def scale(img, k):
        return np.clip(l2g(g2l(img / 255.0) * k) * 255, 0, 255).astype(np.uint8)

    def flatten(img, lo, hi):
        return np.clip((img / 255.0) * (hi - lo) + lo, 0, 1).__mul__(255).astype(np.uint8)

    cases = [
        ('正常曝光',   base),
        ('欠曝 2 档',  scale(base, 0.25)),
        ('过曝 1.5 档', scale(base, 2.8)),
        ('平片/雾感',  flatten(base, 0.38, 0.62)),
        ('水下（偏暗）', sea),
        ('欠曝的水下',  scale(sea, 0.3)),
    ]

    print('%-14s %-34s %-34s' % ('', '处理前', '处理后'))
    print('%-14s %-34s %-34s' % ('', '中位  IQR   溢出高/低', '中位  IQR   溢出高/低'))
    fails = 0
    outs = []

    for name, img in cases:
        p, info = run_cs(img)
        before = stats(img.astype(np.float64) / 255.0)
        after = stats(apply_params(img, p))
        outs.append((name, img, apply_params(img, p)))

        # 判据分两种情况。
        #
        # "中位数靠近中级灰"不是唯一目标：一张曝光本来就正常、只是发灰的片子，
        # 正确的处理是抬黑位加反差，中位数<b>反而会往下走</b>。拿单一判据去卡，
        # 卡掉的是对的结果。所以：
        #   曝光本来就偏 -> 必须明显拉回来
        #   曝光本来就正常 -> 不许推歪太多，而且动态范围的利用要变好
        d0 = abs(before['median'] - TARGET)
        d1 = abs(after['median'] - TARGET)

        if d0 > 0.10:
            median_ok = d1 < d0 * 0.6
        else:
            median_ok = d1 <= 0.10 and after['iqr'] >= before['iqr'] - 1e-3

        no_blowup = (after['clip_hi'] <= max(before['clip_hi'] * 1.5, 0.02) + 1e-6 and
                     after['clip_lo'] <= max(before['clip_lo'] * 1.5, 0.06) + 1e-6)

        ok = median_ok and no_blowup
        if not ok:
            fails += 1

        print('%-14s %.3f %.3f %.3f/%.3f   ->   %.3f %.3f %.3f/%.3f   %s'
              % (name, before['median'], before['iqr'], before['clip_hi'], before['clip_lo'],
                 after['median'], after['iqr'], after['clip_hi'], after['clip_lo'],
                 'OK' if ok else 'FAIL'))
        print('   参数 曝光%+.2f 色阶[%.3f,%.3f] 高光%+.2f 阴影%+.2f 反差%.2f'
              % (p['exposure'], p['inBlack'], p['inWhite'], p['highlights'], p['shadows'], p['contrast']))

    # 平片那一档必须真的把反差拉开
    flat_before = stats(cases[3][1].astype(np.float64) / 255.0)['iqr']
    pf, _ = run_cs(cases[3][1])
    flat_after = stats(apply_params(cases[3][1], pf))['iqr']
    print()
    print('平片的 IQR：%.3f -> %.3f  %s' % (flat_before, flat_after,
                                          'OK' if flat_after > flat_before * 1.5 else 'FAIL'))
    if flat_after <= flat_before * 1.5:
        fails += 1

    W = 190
    out = Image.new('RGB', (W * len(outs) + 8 * len(outs), int(W * 0.66) * 2 + 30), (30, 30, 30))
    d = ImageDraw.Draw(out)
    for i, (n, a, b) in enumerate(outs):
        ha = int(W * a.shape[0] / a.shape[1])
        out.paste(Image.fromarray(a).resize((W, ha)), (i * (W + 8), 14))
        out.paste(Image.fromarray((b * 255).astype(np.uint8)).resize((W, ha)), (i * (W + 8), 20 + ha))
        d.text((i * (W + 8) + 3, 2), n, fill=(255, 255, 255))
    out.save('autotone_compare.png')

    print()
    print('失败 %d 项' % fails)
    return 1 if fails else 0


if __name__ == '__main__':
    raise SystemExit(main())
