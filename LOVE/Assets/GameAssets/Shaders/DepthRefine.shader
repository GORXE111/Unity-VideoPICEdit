// 深度图精修：联合双边上采样（Joint Bilateral Upsampling）
//
// MiDaS small 只输出 256x256 的平滑深度，直接拉伸会糊成一团。
// 这里用全分辨率的彩色原图当引导：取邻域深度做加权平均，
// 权重 = 空间距离 × 颜色相似度。颜色差得多的邻居几乎不参与，
// 于是深度的边界会自动吸附到真实的颜色边缘上。
//
// 局限要清楚：它只能把边界对齐到颜色边缘，
// 主体和背景颜色接近时帮不上忙，也变不出模型本来就没有的细节（比如发丝）。
Shader "Hidden/Love/DepthRefine"
{
    Properties
    {
        _MainTex ("Guide (full res color)", 2D) = "white" {}
        _DepthTex ("Depth (low res)", 2D) = "gray" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _DepthTex;
            float4    _DepthTex_TexelSize;

            half  _SigmaSpace;    // 空间权重的衰减，越大越平滑
            half  _SigmaColor;    // 颜色权重的衰减，越小越贴边
            half  _SampleScale;   // 采样步长，按深度图的像素走

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            #define R 3   // 7x7 邻域

            half4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                half3 guideC = tex2D(_MainTex, uv).rgb;

                float2 step = _DepthTex_TexelSize.xy * _SampleScale;

                half sumW = 1e-5h;
                half sumD = 0;

                half invS = 1.0h / max(2.0h * _SigmaSpace * _SigmaSpace, 1e-4h);
                half invC = 1.0h / max(2.0h * _SigmaColor * _SigmaColor, 1e-4h);

                [unroll]
                for (int dy = -R; dy <= R; dy++)
                {
                    [unroll]
                    for (int dx = -R; dx <= R; dx++)
                    {
                        float2 duv = uv + float2(dx, dy) * step;

                        // 显式 lod：这里在循环里采样，隐式梯度不可靠
                        half d = tex2Dlod(_DepthTex, float4(duv, 0, 0)).r;
                        half3 g = tex2Dlod(_MainTex, float4(duv, 0, 0)).rgb;

                        half wS = exp(-(half)(dx * dx + dy * dy) * invS);

                        half3 dc = g - guideC;
                        half wC = exp(-dot(dc, dc) * invC);

                        half w = wS * wC;
                        sumW += w;
                        sumD += w * d;
                    }
                }

                half depth = sumD / sumW;
                return half4(depth, depth, depth, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
