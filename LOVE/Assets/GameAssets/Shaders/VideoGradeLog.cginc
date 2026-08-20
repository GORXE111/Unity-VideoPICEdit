#ifndef LOVE_VIDEOGRADE_LOG_INCLUDED
#define LOVE_VIDEOGRADE_LOG_INCLUDED

// LOG 解码：把各厂商的 LOG 编码值还原成线性场景亮度。
//
// 为什么必须先解码：LOG 是为了在有限位深里塞下高动态范围而设计的编码，
// 画面看起来又灰又平是正常的。直接对 LOG 拉对比度和饱和度，
// 等于在一条被压扁的曲线上再压一次，暗部会糊死、高光会断层。
//
// 常数取自各厂商公开的白皮书/技术文档：
//   S-Log3  Sony  白皮书的 S-Log3 到 linear reflection 公式
//   V-Log   Panasonic  V-Log/V-Gamut 参考手册
//   C-Log3  Canon  Canon Log 3 Transfer Characteristics
//   LogC3   ARRI  ALEXA LogC Curve Usage in VFX（EI 800）
//   D-Log   DJI  D-Log 说明（不同来源常数略有出入，以实拍验证为准）
//
// 这里只做色调曲线，不做色域矩阵。色域交给 24 色卡实测解出来的矩阵——
// 手填色域矩阵一旦填错，偏色很隐蔽、极难排查，实测反而更可靠。

half SLog3ToLinear (half x)
{
    // 分段点 171.2102946929/1023
    return (x >= 0.1673609f)
        ? (pow(10.0h, (x * 1023.0h - 420.0h) / 261.5h) * 0.19h - 0.01h)
        : ((x * 1023.0h - 95.0h) * 0.01125h / (171.2102946929h - 95.0h));
}

half VLogToLinear (half x)
{
    const half b = 0.00873h, c = 0.241514h, d = 0.598206h;
    return (x < 0.181h) ? ((x - 0.125h) / 5.6h)
                        : (pow(10.0h, (x - d) / c) - b);
}

half CLog3ToLinear (half x)
{
    if (x < 0.097465473h)
        return -(pow(10.0h, (0.12783901h - x) / 0.36726845h) - 1.0h) / 14.98325h;
    if (x <= 0.15277891h)
        return (x - 0.12512219h) / 1.9754798h;
    return (pow(10.0h, (x - 0.12240537h) / 0.36726845h) - 1.0h) / 14.98325h;
}

half LogC3ToLinear (half x)
{
    // EI 800 的参数集
    const half cut = 0.010591h, a = 5.555556h, b = 0.052272h;
    const half c = 0.247190h, d = 0.385537h, e = 5.367655h, f = 0.092809h;
    return (x > e * cut + f) ? ((pow(10.0h, (x - d) / c) - b) / a)
                             : ((x - f) / e);
}

half DLogToLinear (half x)
{
    return (x <= 0.14h) ? ((x - 0.0929h) / 6.025h)
                        : ((pow(10.0h, (3.89616h * x - 2.27752h)) - 0.0108h) / 0.9892h);
}

/// mode 见 C# 侧的 LogMode 枚举
half3 DecodeLog (half3 c, half mode)
{
    if (mode < 0.5h) return c;

    half3 r;
    if (mode < 1.5h)      { r.x = SLog3ToLinear(c.x); r.y = SLog3ToLinear(c.y); r.z = SLog3ToLinear(c.z); }
    else if (mode < 2.5h) { r.x = VLogToLinear(c.x);  r.y = VLogToLinear(c.y);  r.z = VLogToLinear(c.z);  }
    else if (mode < 3.5h) { r.x = CLog3ToLinear(c.x); r.y = CLog3ToLinear(c.y); r.z = CLog3ToLinear(c.z); }
    else if (mode < 4.5h) { r.x = LogC3ToLinear(c.x); r.y = LogC3ToLinear(c.y); r.z = LogC3ToLinear(c.z); }
    else                  { r.x = DLogToLinear(c.x);  r.y = DLogToLinear(c.y);  r.z = DLogToLinear(c.z);  }

    return max(r, 0);
}

// 24 色卡最小二乘解出来的 3x4 校色矩阵（3x3 线性 + 偏移）
half4 _ColorMatrixR;
half4 _ColorMatrixG;
half4 _ColorMatrixB;
half  _ColorMatrixOn;

half3 ApplyColorMatrix (half3 c)
{
    if (_ColorMatrixOn < 0.5h) return c;
    half4 v = half4(c, 1.0h);
    return max(half3(dot(_ColorMatrixR, v), dot(_ColorMatrixG, v), dot(_ColorMatrixB, v)), 0);
}

#endif // LOVE_VIDEOGRADE_LOG_INCLUDED
