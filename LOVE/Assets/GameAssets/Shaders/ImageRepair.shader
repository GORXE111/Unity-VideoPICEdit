// 污点修复 / 仿制图章。
//
// 两种模式：
//   仿制 —— 把源处的像素原样搬到目标，和 PS 的仿制图章一样。
//   修复 —— 搬完再整体加一个色调补偿，让补上去的那块和四周的明暗色调对得上。
//
// 那个补偿量是 CPU 在找取样源时顺手算出来的：目标外圈的平均色减去源外圈的平均色。
// 为什么用外圈而不是圆盘：圆盘里就是要去掉的污点本身，拿它当基准会被污点带跑。
//
// 试过用一张低频图做逐像素的频率分离（healed = src + lowT - lowS），
// 在真实照片上和这个常量补偿几乎打平（平均误差 16.5 对 17.0），
// 但要多两趟降采样和一张临时图。简单的那个赢了。
Shader "Hidden/Love/ImageRepair"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            float4 _Spot;          // xy=目标中心 uv，z=半径（以画面高为单位），w=羽化 0~1
            float2 _SrcOffset;     // 源相对目标的 uv 偏移
            half3  _ToneOffset;    // 色调补偿，仅修复模式
            half   _Aspect;
            half   _HealMode;      // 0 仿制 1 修复
            half   _Opacity;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // 采样一律放在分支外——分支内 tex2D 的隐式梯度是未定义的
                half4 baseC = tex2D(_MainTex, uv);
                float2 suv = uv + _SrcOffset;
                half3 src = tex2D(_MainTex, suv).rgb;

                // 圆形羽化。x 乘宽高比，否则"圆"在宽画面上是扁的
                float2 d = uv - _Spot.xy;
                d.x *= _Aspect;
                half dist = length(d) / max(_Spot.z, 1e-5h);

                // 羽化从圆盘边缘往外扩，圆盘内部是全不透明的。
                // 反过来往里收的话，污点的外圈会补不到，留下一圈残影
                half a = (1.0h - smoothstep(1.0h, 1.0h + saturate(_Spot.w), dist)) * _Opacity;

                half3 outC = src + _ToneOffset * step(0.5h, _HealMode);

                // 源跑到画面外时那里没有有效内容，退回原样，别把边缘糊上去
                half inside = step(0.0h, suv.x) * step(suv.x, 1.0h) *
                              step(0.0h, suv.y) * step(suv.y, 1.0h);
                a *= inside;

                return half4(lerp(baseC.rgb, max(outC, 0), a), baseC.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
