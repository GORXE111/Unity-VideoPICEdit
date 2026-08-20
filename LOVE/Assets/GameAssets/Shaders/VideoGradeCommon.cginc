#ifndef LOVE_VIDEOGRADE_COMMON_INCLUDED
#define LOVE_VIDEOGRADE_COMMON_INCLUDED

// 各 Pass 共用的基础设施：顶点结构、采样核、色彩空间与色调映射。
// 拆出来是为了让每个 Pass 的 frag 只剩下自己那一层的逻辑。

#include "UnityCG.cginc"

sampler2D _MainTex;
float4    _MainTex_TexelSize;   // xy = 1/尺寸, zw = 尺寸

struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

v2f VertFullscreen (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv  = v.uv;
    return o;
}

// ---------------- 采样核 ----------------

// 4 点盒式降采样
half4 DownsampleBox (float2 uv)
{
    float2 d = _MainTex_TexelSize.xy;
    half4 s  = tex2D(_MainTex, uv + d * float2(-1, -1));
    s       += tex2D(_MainTex, uv + d * float2( 1, -1));
    s       += tex2D(_MainTex, uv + d * float2(-1,  1));
    s       += tex2D(_MainTex, uv + d * float2( 1,  1));
    return s * 0.25h;
}

// 3x3 帐篷升采样，比双线性放大柔和很多
half4 UpsampleTent (float2 uv)
{
    float4 d = _MainTex_TexelSize.xyxy * float4(1, 1, -1, 0);
    half4 s  = tex2D(_MainTex, uv - d.xy);
    s       += tex2D(_MainTex, uv - d.wy) * 2.0h;
    s       += tex2D(_MainTex, uv - d.zy);
    s       += tex2D(_MainTex, uv + d.zw) * 2.0h;
    s       += tex2D(_MainTex, uv)        * 4.0h;
    s       += tex2D(_MainTex, uv + d.xw) * 2.0h;
    s       += tex2D(_MainTex, uv + d.zy);
    s       += tex2D(_MainTex, uv + d.wy) * 2.0h;
    s       += tex2D(_MainTex, uv + d.xy);
    return s * 0.0625h;   // 1/16
}

// ---------------- 色彩基础 ----------------

half Luma (half3 c) { return dot(c, half3(0.2126h, 0.7152h, 0.0722h)); }

half3 RgbToHsv (half3 c)
{
    half4 K = half4(0.0h, -1.0h / 3.0h, 2.0h / 3.0h, -1.0h);
    half4 p = lerp(half4(c.bg, K.wz), half4(c.gb, K.xy), step(c.b, c.g));
    half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));
    half d = q.x - min(q.w, q.y);
    half e = 1.0e-4h;
    return half3(abs(q.z + (q.w - q.y) / (6.0h * d + e)), d / (q.x + e), q.x);
}

half3 HsvToRgb (half3 c)
{
    half4 K = half4(1.0h, 2.0h / 3.0h, 1.0h / 3.0h, 3.0h);
    half3 p = abs(frac(c.xxx + K.xyz) * 6.0h - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

// 白平衡走的是 CIE xy -> LMS 的标准做法，系数在 CPU 端算好传进来
static const half3x3 LIN_2_LMS = half3x3(
    3.90405e-1, 5.49941e-1, 8.92632e-3,
    7.08416e-2, 9.63172e-1, 1.35775e-3,
    2.31082e-2, 1.28021e-1, 9.36245e-1);

static const half3x3 LMS_2_LIN = half3x3(
     2.85847e+0, -1.62879e+0, -2.48910e-2,
    -2.10182e-1,  1.15820e+0,  3.24281e-4,
    -4.18120e-2, -1.18169e-1,  1.06867e+0);

// ---------------- 色调映射（都在线性空间做）----------------

half3 TonemapReinhard (half3 c) { return c / (1.0h + c); }

half3 TonemapFilmic (half3 c)
{
    // Hejl / Burgess-Dawson 的近似，自带 sRGB 编码，所以要还原回线性接回统一流程
    half3 x = max(0, c - 0.004h);
    half3 r = (x * (6.2h * x + 0.5h)) / (x * (6.2h * x + 1.7h) + 0.06h);
    return GammaToLinearSpace(r);
}

half3 TonemapACES (half3 c)
{
    // Narkowicz 的 ACES 拟合
    const half a = 2.51h, b = 0.03h, cc = 2.43h, d = 0.59h, e = 0.14h;
    return saturate((c * (a * c + b)) / (c * (cc * c + d) + e));
}

half3 ApplyTonemap (half3 c, half mode)
{
    if (mode > 2.5h)      return TonemapACES(c);
    else if (mode > 1.5h) return TonemapFilmic(c);
    else if (mode > 0.5h) return TonemapReinhard(c);
    return c;
}

#endif // LOVE_VIDEOGRADE_COMMON_INCLUDED
