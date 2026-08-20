#ifndef LOVE_VIDEOGRADE_MASKS_INCLUDED
#define LOVE_VIDEOGRADE_MASKS_INCLUDED

// 二级校色的选区：Power Window（形状）∩ HSL 限定器（颜色）。
// 单独一个文件，因为后面要往这里加渐变蒙版、多窗口叠加这些东西。

half   _WindowShape;      // 0 不限 1 椭圆 2 矩形 3 线性渐变
half2  _WindowCenter;
half2  _WindowSize;
half2  _WindowRot;        // cos, sin
half   _WindowFeather;
half   _WindowInvert;

half   _QualEnabled;
half3  _QualHue;          // 中心, 范围, 柔和
half3  _QualSat;          // 下限, 上限, 柔和
half3  _QualLum;          // 下限, 上限, 柔和

// Power Window：椭圆或矩形，带羽化。aspect 用来做宽高比校正
half WindowMask (float2 uv, half aspect)
{
    if (_WindowShape < 0.5h) return 1.0h;

    float2 p = uv - _WindowCenter;
    p.x *= aspect;                      // 不校正的话"圆形"在宽画面上是扁的

    // 绕中心旋转
    float2 rp = float2(p.x * _WindowRot.x - p.y * _WindowRot.y,
                       p.x * _WindowRot.y + p.y * _WindowRot.x);

    float2 halfSize = max(_WindowSize, 1e-4h);
    half feather = max(_WindowFeather, 1e-3h);
    half m;

    if (_WindowShape > 2.5h)
    {
        // 线性渐变：只看旋转后的一个轴，一侧全选、另一侧全不选，中间柔和过渡。
        // 这就是 Lightroom 的渐变滤镜，压天空、提前景全靠它
        half t = rp.y / halfSize.y;
        m = 1.0h - smoothstep(-feather, feather, t);
    }
    else
    {
        half d = (_WindowShape > 1.5h)
            ? max(abs(rp.x) / halfSize.x, abs(rp.y) / halfSize.y)   // 矩形用切比雪夫距离
            : length(rp / halfSize);                                // 椭圆用欧氏距离
        m = 1.0h - smoothstep(1.0h - feather, 1.0h + feather, d);
    }

    return _WindowInvert > 0.5h ? 1.0h - m : m;
}

// 一段带柔和边界的区间隶属度
half BandMask (half value, half lo, half hi, half soft)
{
    soft = max(soft, 1e-4h);
    return smoothstep(lo - soft, lo + soft, value) *
           (1.0h - smoothstep(hi - soft, hi + soft, value));
}

// HSL 限定器：按色相/饱和度/亮度圈出一块区域
half QualifierMask (half3 c)
{
    if (_QualEnabled < 0.5h) return 1.0h;

    half3 hsv = RgbToHsv(saturate(c));

    half hueDist = abs(hsv.x - _QualHue.x);
    hueDist = min(hueDist, 1.0h - hueDist);      // 色相是环形的
    half hm = 1.0h - smoothstep(_QualHue.y, _QualHue.y + max(_QualHue.z, 1e-4h), hueDist);

    half sm = BandMask(hsv.y, _QualSat.x, _QualSat.y, _QualSat.z);
    half lm = BandMask(hsv.z, _QualLum.x, _QualLum.y, _QualLum.z);

    return hm * sm * lm;
}

#endif // LOVE_VIDEOGRADE_MASKS_INCLUDED
