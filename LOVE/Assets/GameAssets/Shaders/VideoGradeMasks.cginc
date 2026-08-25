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

// ================= 多蒙版：单个部件的求值与合并 =================
//
// 一个蒙版组由若干部件按「加 / 减 / 交」合成。每个部件走一趟 Pass，
// 结果乒乓写进一张单通道图——部件数量不定，在 shader 里展开不现实。

half   _PartShape;      // 0 椭圆 1 矩形 2 线性渐变 3 颜色范围 4 亮度范围 5 深度范围 6/7 贴图
half   _PartOp;         // 0 加 1 减 2 交
half   _PartInvert;
half   _PartOpacity;

half2  _PartCenter;
half2  _PartSize;
half2  _PartRot;        // cos, sin
half   _PartFeather;

half3  _PartHue;        // 中心, 范围, 柔和
half3  _PartSat;        // 下限, 上限, 柔和
half3  _PartLum;
half3  _PartDepth;

sampler2D _PartTex;     // 主体分割或手绘笔刷
sampler2D _DepthTex;    // 深度图，没有时 C# 端指向纯黑
sampler2D _PrevMask;    // 已经累积到这一步的蒙版

half PartShapeMask (float2 uv, half aspect)
{
    float2 p = uv - _PartCenter;
    p.x *= aspect;                      // 不校正的话"圆形"在宽画面上是扁的

    float2 rp = float2(p.x * _PartRot.x - p.y * _PartRot.y,
                       p.x * _PartRot.y + p.y * _PartRot.x);

    float2 hs = max(_PartSize, 1e-4h);
    half fe = max(_PartFeather, 1e-3h);

    if (_PartShape > 1.5h)
    {
        // 线性渐变：只看旋转后的一个轴。压天空、提前景全靠它
        half t = rp.y / hs.y;
        return 1.0h - smoothstep(-fe, fe, t);
    }

    half d = (_PartShape > 0.5h)
        ? max(abs(rp.x) / hs.x, abs(rp.y) / hs.y)   // 矩形用切比雪夫距离
        : length(rp / hs);                          // 椭圆用欧氏距离
    return 1.0h - smoothstep(1.0h - fe, 1.0h + fe, d);
}

/// col 传 gamma 空间的值：颜色和亮度范围要和用户在直方图上看到的一致
half EvalPart (float2 uv, half3 col, half aspect)
{
    // 采样一律用 tex2Dlod。这些分支的条件虽然是 uniform 的，
    // 但分支内隐式梯度未定义是这套管线反复踩过的坑，不留侥幸
    if (_PartShape < 2.5h) return PartShapeMask(uv, aspect);

    if (_PartShape < 3.5h)
    {
        half3 hsv = RgbToHsv(saturate(col));
        half hd = abs(hsv.x - _PartHue.x);
        hd = min(hd, 1.0h - hd);                    // 色相是环形的
        half hm = 1.0h - smoothstep(_PartHue.y, _PartHue.y + max(_PartHue.z, 1e-4h), hd);
        return hm * BandMask(hsv.y, _PartSat.x, _PartSat.y, _PartSat.z);
    }

    if (_PartShape < 4.5h)
        return BandMask(Luma(saturate(col)), _PartLum.x, _PartLum.y, _PartLum.z);

    if (_PartShape < 5.5h)
        return BandMask(tex2Dlod(_DepthTex, float4(uv, 0, 0)).r,
                        _PartDepth.x, _PartDepth.y, _PartDepth.z);

    return tex2Dlod(_PartTex, float4(uv, 0, 0)).r;   // 主体分割 / 手绘笔刷
}

half CombineMask (half prev, half m)
{
    if (_PartOp < 0.5h) return max(prev, m);          // 加：并集
    if (_PartOp < 1.5h) return saturate(prev - m);    // 减：差集
    return prev * m;                                   // 交：交集
}

#endif // LOVE_VIDEOGRADE_MASKS_INCLUDED
