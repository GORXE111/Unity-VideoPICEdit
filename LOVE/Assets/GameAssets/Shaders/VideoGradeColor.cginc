#ifndef LOVE_VIDEOGRADE_COLOR_INCLUDED
#define LOVE_VIDEOGRADE_COLOR_INCLUDED

// 调色本体，按「线性空间的物理量」和「gamma 空间的手感参数」分成两段。
//
// 曝光 / 白平衡 / 色调映射在线性空间做（物理正确）；
// 色阶 / LGGO / 曲线 / 对比度 / 饱和度在 gamma 空间做（和调色软件手感一致）。
// 这个分界是整套管线里最容易搞错的地方，所以两段函数刻意分开命名。

sampler2D _CurveLut;      // 256x1，RGB 三通道各存一条已经和主曲线复合过的曲线

half   _Exposure;
half3  _ColorBalance;
half   _TonemapMode;

half4  _Levels;           // x=输入黑 y=输入白 z=输出黑 w=输出白
half   _LevelsGamma;

half3  _Lift;
half3  _Gamma;
half3  _Gain;
half3  _Offset;

half   _Contrast;
half   _Highlights;
half   _Shadows;

half3  _ShadowTint;
half3  _HighlightTint;
half   _SplitBalance;

half   _Saturation;
half   _SkinProtect;
half   _HueShift;

// ---- 3D LUT（导入的 .cube）----
sampler3D _LutTex;
half  _LutSize;
half  _LutAmount;

half3 ApplyLut (half3 c)
{
    // 体素中心对齐：直接用 0~1 采样会在两端各丢半个体素，
    // 表现为最亮和最暗处的颜色偏掉
    half scale = (_LutSize - 1.0h) / _LutSize;
    half offset = 1.0h / (2.0h * _LutSize);
    half3 uvw = saturate(c) * scale + offset;
    return lerp(c, tex3D(_LutTex, uvw).rgb, _LutAmount);
}

// ---- 六条曲线 ----
// 一张 256x2 的 RGBA 贴图装下六条：
//   第 0 行 = 色相vs色相 / 色相vs饱和 / 色相vs亮度 / 亮度vs饱和
//   第 1 行 = 饱和vs饱和 / 饱和vs亮度
// 每条曲线的值域是 0~1，中性值是 0.5，所以要减 0.5 再乘 2 才是增减量
sampler2D _SixCurveTex;
half _SixCurveOn;

half3 ApplySixCurves (half3 c)
{
    half3 hsv = RgbToHsv(saturate(c));

    half4 byHue = tex2Dlod(_SixCurveTex, float4(hsv.x, 0.25, 0, 0));
    half4 bySat = tex2Dlod(_SixCurveTex, float4(hsv.y, 0.75, 0, 0));
    half4 byLum = tex2Dlod(_SixCurveTex, float4(hsv.z, 0.25, 0, 0));

    hsv.x = frac(hsv.x + (byHue.r - 0.5h) + 1.0h);          // 色相 vs 色相
    hsv.y = saturate(hsv.y * (1.0h + (byHue.g - 0.5h) * 2.0h)   // 色相 vs 饱和度
                            * (1.0h + (bySat.r - 0.5h) * 2.0h)  // 饱和度 vs 饱和度
                            * (1.0h + (byLum.a - 0.5h) * 2.0h)); // 亮度 vs 饱和度
    hsv.z = saturate(hsv.z * (1.0h + (byHue.b - 0.5h) * 2.0h)    // 色相 vs 亮度
                            * (1.0h + (bySat.g - 0.5h) * 2.0h)); // 饱和度 vs 亮度

    return HsvToRgb(hsv);
}

// 二级校色：遮罩内的调整
half   _SecExposure;
half   _SecContrast;
half   _SecSaturation;
half   _SecHueShift;
half3  _SecTint;

// ---------------- 线性空间段 ----------------

half3 GradeLinear (half3 col)
{
    col *= exp2(_Exposure);

    // 白平衡：线性 -> LMS -> 缩放 -> 线性
    half3 lms = mul(LIN_2_LMS, col);
    lms *= _ColorBalance;
    col = max(mul(LMS_2_LIN, lms), 0);

    return ApplyTonemap(col, _TonemapMode);
}

// ---------------- gamma 空间段 ----------------

// 肤色遮罩：色相接近橙红、且有一定饱和度的区域
half SkinMask (half3 hsv)
{
    half hueDist = abs(hsv.x - 0.06h);
    hueDist = min(hueDist, 1.0h - hueDist);          // 色相是环形的，取最近距离
    half h = exp(-(hueDist * hueDist) / 0.0018h);    // 以 0.06 为中心的钟形
    half s = smoothstep(0.08h, 0.35h, hsv.y);        // 灰的地方不算肤色
    half v = smoothstep(0.08h, 0.25h, hsv.z);        // 死黑不算
    return saturate(h * s * v);
}

half3 ApplyLevels (half3 g)
{
    g = saturate((g - _Levels.x) / max(_Levels.y - _Levels.x, 1e-4h));
    g = pow(max(g, 1e-5h), 1.0h / _LevelsGamma);
    return g * (_Levels.w - _Levels.z) + _Levels.z;
}

half3 ApplyWheels (half3 g)
{
    g = g + _Lift * (1.0h - g);                  // Lift 抬黑位，白点基本不动
    g = g * _Gain;                               // Gain 提白位，黑点不动
    g = pow(max(g, 1e-5h), 1.0h / _Gamma);       // Gamma 只动中间调
    g = g + _Offset;                             // Offset 整体平移
    return max(g, 0);
}

half3 ApplyCurves (half3 g)
{
    // 用 tex2Dlod 而不是 tex2D：这段可能在分支里，而隐式梯度在分支内是未定义的。
    // 查找贴图没有 mip，显式指定 lod 0 既正确又安全。
    half3 cg = saturate(g);
    return half3(tex2Dlod(_CurveLut, float4(cg.r, 0.5, 0, 0)).r,
                 tex2Dlod(_CurveLut, float4(cg.g, 0.5, 0, 0)).g,
                 tex2Dlod(_CurveLut, float4(cg.b, 0.5, 0, 0)).b);
}

half3 ApplyContrastAndTone (half3 g)
{
    g = (g - 0.5h) * _Contrast + 0.5h;           // 对比度绕 0.5 旋转
    g = max(g, 0);

    half lum = Luma(g);
    g *= 1.0h + _Shadows    * (1.0h - smoothstep(0.0h, 0.5h, lum))
              + _Highlights * smoothstep(0.5h, 1.0h, lum);
    g = max(g, 0);

    // 色调分离：阴影和高光分别染色
    half splitW = saturate((Luma(g) - 0.5h - _SplitBalance) * 2.0h + 0.5h);
    g *= lerp(_ShadowTint, _HighlightTint, splitW);
    return max(g, 0);
}

half3 ApplySaturationAndHue (half3 g)
{
    half3 hsv = RgbToHsv(saturate(g));
    half satAmount = lerp(_Saturation, 1.0h, _SkinProtect * SkinMask(hsv));
    half satLum = Luma(g);
    g = lerp(half3(satLum, satLum, satLum), g, satAmount);
    g = max(g, 0);

    if (abs(_HueShift) > 0.001h)
    {
        half3 h2 = RgbToHsv(saturate(g));
        h2.x = frac(h2.x + _HueShift + 1.0h);
        g = HsvToRgb(h2);
    }
    return g;
}

// 遮罩内的二级调整
half3 ApplySecondary (half3 c)
{
    c *= exp2(_SecExposure);
    c = (c - 0.5h) * _SecContrast + 0.5h;
    c = max(c, 0);

    half l = Luma(c);
    c = lerp(half3(l, l, l), c, _SecSaturation);
    c = max(c, 0);

    c *= _SecTint;

    if (abs(_SecHueShift) > 0.001h)
    {
        half3 hsv = RgbToHsv(saturate(c));
        hsv.x = frac(hsv.x + _SecHueShift + 1.0h);
        c = HsvToRgb(hsv);
    }
    return max(c, 0);
}

#endif // LOVE_VIDEOGRADE_COLOR_INCLUDED
