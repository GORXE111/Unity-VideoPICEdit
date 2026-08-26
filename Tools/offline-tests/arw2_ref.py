# -*- coding: utf-8 -*-
"""
ARW2 参考解码器：逐行照抄 dcraw 的 sony_arw2_load_raw()，只做 C 到 Python 的直译。
存在的唯一目的是给 C# 实现当对照组——两边独立写，输出必须逐位相同。
"""
import struct, sys, os

D = os.path.dirname(os.path.abspath(__file__))
RT = os.path.join(D, 'rawtest')


def build_curve(pts):
    """dcraw: FORC4 sony_curve[c+1] = get2() >> 2 & 0xfff; 然后五段斜率 1/2/4/8/16"""
    sony_curve = [0, 0, 0, 0, 0, 4095]
    for c in range(4):
        sony_curve[c + 1] = (pts[c] >> 2) & 0xfff
    curve = [0] * 4096
    for i in range(5):
        for j in range(sony_curve[i] + 1, sony_curve[i + 1] + 1):
            curve[j] = curve[j - 1] + (1 << i)
    return curve


def decode(data, w, h, curve):
    # 尾部补两个零字节。
    #
    # dcraw 是 malloc(raw_width+1) 再 fread(raw_width)，末尾那一字节从没被写过。
    # 正常码流永远读不到它（最后一个增量起于 bit 121，落在块内第 15 字节），
    # 但 imax==imin 时会多读一个增量，bit 走到 128，指针就越过整块了。
    # 那种码流是非法的，dcraw 在那里读的是未初始化内存、输出不确定，没法当对照组。
    # 这里补零，和 C# 判界后当 0 的行为对齐。
    buf = data + b'\x00\x00'
    out = [0] * (w * h)
    pix = [0] * 16

    for row in range(h):
        base = row * w
        col = 0
        dp = 0
        while col < w - 30:
            val = struct.unpack_from('<I', buf, base + dp)[0]
            mx = val & 0x7ff
            mn = (val >> 11) & 0x7ff
            imax = (val >> 22) & 0x0f
            imin = (val >> 26) & 0x0f

            sh = 0
            while sh < 4 and (0x80 << sh) <= mx - mn:
                sh += 1

            bit = 30
            for i in range(16):
                if i == imax:
                    pix[i] = mx
                elif i == imin:
                    pix[i] = mn
                else:
                    word = struct.unpack_from('<H', buf, base + dp + (bit >> 3))[0]
                    v = (((word >> (bit & 7)) & 0x7f) << sh) + mn
                    pix[i] = 0x7ff if v > 0x7ff else v
                    bit += 7

            for i in range(16):
                out[base + col] = curve[pix[i] << 1] >> 2
                col += 2

            col -= 1 if (col & 1) else 31
            dp += 16

    return out


def main():
    w, h, p0, p1, p2, p3, seed = open(os.path.join(RT, 'arw2_meta.txt')).read().split()
    w, h = int(w), int(h)
    pts = [int(p0), int(p1), int(p2), int(p3)]

    data = open(os.path.join(RT, 'arw2_in.bin'), 'rb').read()
    got = open(os.path.join(RT, 'arw2_out.bin'), 'rb').read()
    cs = list(struct.unpack('<%dH' % (w * h), got))

    ref = decode(data, w, h, build_curve(pts))

    diff = [(i, ref[i], cs[i]) for i in range(w * h) if ref[i] != cs[i]]
    nz = sum(1 for v in ref if v)
    print('seed=%s  %dx%d  points=%s  nonzero=%d/%d  max=%d'
          % (seed, w, h, pts, nz, w * h, max(ref)))
    if diff:
        print('  MISMATCH %d, first: %s' % (len(diff), diff[:6]))
        return 1
    print('  PASS  bit-identical to dcraw reference')
    return 0


if __name__ == '__main__':
    sys.exit(main())
