// 视频 / 图片调色管线
//
// Pass 编排：
//   0 Bloom 阈值预处理 + 降采样
//   1 纯降采样（整体模糊用）
//   2 帐篷升采样
//   3 细节滤波：镜头畸变 + 双边降噪 + 通透度 + 纹理 + 智能锐化 —— 都关掉时 C# 端整趟跳过
//   4 合成 + 调色：LOG 解码 / 校色矩阵 / 一级 / 曲线 / 六条曲线 / LUT / 二级 / 风格化 / 监看
//
// 具体算法都在 .cginc 里（Common / Log / Color / Masks），这个文件只负责编排。
// 关掉的功能靠 shader_feature 在编译期消失，而不是运行期每像素判一次分支。
Shader "Hidden/Love/VideoGrade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        // ---------------- Pass 0：Bloom 阈值预处理（带软膝盖）+ 降采样 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex VertFullscreen
            #pragma fragment frag
            #include "VideoGradeCommon.cginc"

            half _Threshold;
            half _SoftKnee;

            half4 frag (v2f i) : SV_Target
            {
                half4 c = DownsampleBox(i.uv);

                half brightness = max(c.r, max(c.g, c.b));
                half knee = _Threshold * _SoftKnee + 1e-5h;
                half soft = brightness - _Threshold + knee;
                soft = clamp(soft, 0, 2.0h * knee);
                soft = soft * soft / (4.0h * knee + 1e-5h);
                half contribution = max(soft, brightness - _Threshold) / max(brightness, 1e-5h);

                return half4(c.rgb * contribution, 1);
            }
            ENDCG
        }

        // ---------------- Pass 1：纯降采样 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex VertFullscreen
            #pragma fragment frag
            #include "VideoGradeCommon.cginc"
            half4 frag (v2f i) : SV_Target { return half4(DownsampleBox(i.uv).rgb, 1); }
            ENDCG
        }

        // ---------------- Pass 2：升采样 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex VertFullscreen
            #pragma fragment frag
            #include "VideoGradeCommon.cginc"
            half4 frag (v2f i) : SV_Target { return half4(UpsampleTent(i.uv).rgb, 1); }
            ENDCG
        }

        // ---------------- Pass 3：细节滤波（画质提升）----------------
        // 双边降噪 + 通透度 + 纹理 + 智能锐化，一趟做完。
        // 分开成多个 Pass 的话每层都要一次全分辨率读写，静态图很大时带宽吃不消；
        // 这里靠两个半径的采样复用同一批取样点。
        Pass
        {
            CGPROGRAM
            #pragma vertex VertFullscreen
            #pragma fragment frag
            #pragma target 3.5
            #include "VideoGradeCommon.cginc"

            half _Sharpen;
            half _Denoise;
            half _Clarity;
            half _Texture;
            half _ClarityRadius;
            half _SharpenFocusOnly;
            half _DistortK1;
            half _DistortK2;
            half _DistortScale;

            half4 frag (v2f i) : SV_Target
            {
                // 镜头畸变放在这一层的最前面：之后所有邻域采样都基于畸变后的坐标，
                // 而这一层的输出又是下游模糊链和合成的输入，所以整条管线天然对齐。
                // 放到合成 Pass 里做的话，Bloom 和模糊图是按原坐标算的，会和画面错位。
                float2 c = i.uv - 0.5;
                half r2 = dot(c, c);
                float2 uv = 0.5 + c * (1.0h + _DistortK1 * r2 + _DistortK2 * r2 * r2) * _DistortScale;

                float2 texel = _MainTex_TexelSize.xy;

                half3 base = tex2D(_MainTex, uv).rgb;
                half baseLuma = Luma(base);

                // ---- 近邻 3x3：同时供降噪、纹理、锐化使用 ----
                half3 nearSum = 0;
                half  nearW   = 1e-5h;
                half3 plainSum = 0;

                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0) continue;
                        half3 c = tex2Dlod(_MainTex, float4(uv + float2(x, y) * texel, 0, 0)).rgb;
                        plainSum += c;

                        // 双边权重：颜色差得多的邻居不参与，边缘才不会被抹平
                        half diff = abs(Luma(c) - baseLuma);
                        half w = exp(-diff * diff / max(0.0009h + _Denoise * 0.02h, 1e-5h));
                        nearSum += c * w;
                        nearW   += w;
                    }
                }

                half3 plainMean = plainSum / 8.0h;      // 普通均值，做非锐化蒙版用
                half3 bilateral = nearSum / nearW;      // 保边均值，做降噪用

                // ---- 大半径环形采样：通透度用 ----
                half3 wideSum = 0;
                const half2 dirs[8] = {
                    half2( 1, 0), half2(-1, 0), half2(0,  1), half2(0, -1),
                    half2( 0.707,  0.707), half2(-0.707,  0.707),
                    half2( 0.707, -0.707), half2(-0.707, -0.707)
                };
                [unroll]
                for (int k = 0; k < 8; k++)
                    wideSum += tex2Dlod(_MainTex, float4(uv + dirs[k] * texel * _ClarityRadius, 0, 0)).rgb;
                half3 wideMean = wideSum / 8.0h;

                // ---- 降噪：往保边均值靠 ----
                half3 col = lerp(base, bilateral, saturate(_Denoise));

                // ---- 清晰度图：局部对比越强越可能是对焦区域 ----
                // 不是真正的对焦检测，只是"细节密度"，但对分离主体和虚化背景足够用
                half localContrast = abs(Luma(col) - Luma(plainMean));
                half focus = saturate(localContrast * 24.0h);

                // ---- 纹理：小半径细节，负值可以磨皮 ----
                col += (col - plainMean) * _Texture;

                // ---- 通透度：大半径局部对比，只作用中间调 ----
                // 不限制中间调的话，高光会溢出、暗部会堵死，这是 Clarity 最容易翻车的地方
                half midWeight = 1.0h - saturate(abs(baseLuma - 0.5h) * 2.0h);
                col += (col - wideMean) * _Clarity * midWeight;

                // ---- 智能锐化：只锐对焦区域，避免把背景噪点也锐出来 ----
                half sharpW = _Sharpen * lerp(1.0h, focus, saturate(_SharpenFocusOnly));
                col += (col - plainMean) * sharpW;

                return half4(max(col, 0), 1);
            }
            ENDCG
        }

        // ---------------- Pass 4：合成 + 调色 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex VertFullscreen
            #pragma fragment frag
            #pragma target 3.5

            // 关掉时整段代码不参与编译，省的是指令数和寄存器，不只是一次分支判断
            #pragma shader_feature_local LOVE_CURVE_ON
            #pragma shader_feature_local LOVE_SECONDARY_ON
            #pragma shader_feature_local LOVE_LUT_ON
            #pragma shader_feature_local LOVE_SIXCURVE_ON

            #include "VideoGradeCommon.cginc"
            #include "VideoGradeLog.cginc"
            #include "VideoGradeColor.cginc"
            #include "VideoGradeMasks.cginc"

            half _LogMode;

            sampler2D _BloomTex;
            sampler2D _BlurTex;
            sampler2D _MaskTex;      // AI 主体蒙版。没有时 C# 端指向纯白，下面所有运算自动退化成无操作

            half  _BloomIntensity;
            half  _BlurAmount;
            half  _Chromatic;

            half  _MaskInvert;
            half2 _MaskRemap;        // x=下限 y=上限，用来收缩/扩张边缘
            half  _BgBlur;           // 蒙版外的虚化强度
            half  _SecUseMask;

            half  _VignetteIntensity;
            half  _VignetteSmoothness;
            half  _Grain;
            half  _Dither;
            half  _Aspect;
            half  _GrainSeed;

            half  _ShowMask;
            half2 _Zebra;            // x=过曝阈值 y=欠曝阈值，任一 <=0 表示关
            half  _SplitEnabled;
            half  _SplitPos;

            half4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // 分屏对比要用的原图，必须是完全未经处理的采样
                half3 rawSource = tex2D(_MainTex, uv).rgb;

                // 色差：按到中心的距离把 R/B 通道往两边推。
                // 不用 if，避免分支内采样的梯度问题；_Chromatic 为 0 时三次采样落在同一点，结果等价
                float2 dir = (uv - 0.5) * _Chromatic * 0.01;
                half3 col;
                col.r = tex2D(_MainTex, uv - dir).r;
                col.g = rawSource.g;
                col.b = tex2D(_MainTex, uv + dir).b;

                // AI 主体蒙版：1 = 主体，0 = 背景。
                // 没生成蒙版时 _MaskTex 是纯白，remap 后恒为 1，后面的背景虚化和二级叠加自动失效
                half maskV = tex2D(_MaskTex, uv).r;
                maskV = saturate((maskV - _MaskRemap.x) / max(_MaskRemap.y - _MaskRemap.x, 1e-4h));
                maskV = lerp(maskV, 1.0h - maskV, _MaskInvert);

                // 整体模糊和背景虚化取较大者：前者是风格，后者是伪景深，可以叠加
                half blurMix = max(_BlurAmount, _BgBlur * (1.0h - maskV));
                // 关掉时 C# 端把 _BlurTex 指向原图、_BloomTex 指向纯黑，所以无条件执行
                col = lerp(col, tex2D(_BlurTex, uv).rgb, blurMix);
                col += tex2D(_BloomTex, uv).rgb * _BloomIntensity;   // Bloom 在线性空间相加才正确

                // LOG 解码必须在最前面：素材还是 LOG 编码时，
                // 曝光、白平衡这些线性运算的前提根本不成立
                col = DecodeLog(col, _LogMode);
                col = ApplyColorMatrix(col);

                col = GradeLinear(col);

                // 转到 gamma 空间做"手感"参数
                half3 g = LinearToGammaSpace(col);

                g = ApplyLevels(g);
                g = ApplyWheels(g);

                #ifdef LOVE_CURVE_ON
                    g = ApplyCurves(g);
                #endif

                g = ApplyContrastAndTone(g);
                g = ApplySaturationAndHue(g);
                g = max(g, 0);

                #ifdef LOVE_SIXCURVE_ON
                    g = ApplySixCurves(g);
                #endif

                // LUT 放在一级校色之后：.cube 是"看起来什么样"的最终风格，
                // 应该作用在已经校正过的画面上，而不是原始素材上
                #ifdef LOVE_LUT_ON
                    g = ApplyLut(g);
                #endif
                g = max(g, 0);

                #ifdef LOVE_SECONDARY_ON
                {
                    half mask = WindowMask(uv, _Aspect) * QualifierMask(g);
                    mask *= lerp(1.0h, maskV, _SecUseMask);   // 叠上 AI 主体蒙版

                    // 调窗口和限定器时必须能看见遮罩本身，否则完全是盲调
                    if (_ShowMask > 0.5h)
                        return half4(GammaToLinearSpace(half3(mask, mask, mask)), 1);

                    g = max(lerp(g, ApplySecondary(g), mask), 0);
                }
                #endif

                // 暗角
                if (_VignetteIntensity > 0.001h)
                {
                    float2 d = uv - 0.5;
                    d.x *= _Aspect;
                    half dist = saturate(length(d) * 1.4142h);
                    half vig = pow(saturate(1.0h - dist), _VignetteSmoothness * 4.0h + 0.4h);
                    g *= lerp(1.0h, vig, _VignetteIntensity);
                }

                // 胶片颗粒：暗部更明显，和真实胶片一致
                if (_Grain > 0.001h)
                {
                    float2 p = uv * _MainTex_TexelSize.zw + _GrainSeed;
                    half n = frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
                    g += (n - 0.5h) * _Grain * (1.0h - Luma(g) * 0.6h);
                }

                // 抖动：给渐变加一点噪声，消除 8bit 输出的色带
                if (_Dither > 0.001h)
                {
                    float2 p = uv * _MainTex_TexelSize.zw + _GrainSeed * 1.7;
                    half n = frac(sin(dot(p, float2(21.7381, 53.1297))) * 27183.1927);
                    g += (n - 0.5h) * _Dither * (2.0h / 255.0h);
                }

                // 斑马纹：过曝画红斜纹、欠曝画蓝斜纹，斜纹是监看惯例，
                // 用纯色块会和画面本身的高光分不清
                if (_Zebra.x > 0.001h || _Zebra.y > 0.001h)
                {
                    half lum = Luma(saturate(g));
                    half stripe = frac((uv.x * _MainTex_TexelSize.z + uv.y * _MainTex_TexelSize.w) * 0.12h);
                    if (stripe < 0.5h)
                    {
                        if (_Zebra.x > 0.001h && lum > _Zebra.x) g = half3(1.0h, 0.15h, 0.15h);
                        else if (_Zebra.y > 0.001h && lum < _Zebra.y) g = half3(0.15h, 0.35h, 1.0h);
                    }
                }

                g = max(g, 0);

                // 写回时 Unity 会做 sRGB 编码，所以这里要还原成线性
                half3 result = GammaToLinearSpace(g);

                // 分屏对比：左边原图，右边调色后，中间一条白线
                if (_SplitEnabled > 0.5h)
                {
                    result = uv.x < _SplitPos ? rawSource : result;
                    half pixelDist = abs(uv.x - _SplitPos) * _MainTex_TexelSize.z;
                    result = lerp(result, half3(1, 1, 1), saturate(1.5h - pixelDist));
                }

                return half4(result, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
