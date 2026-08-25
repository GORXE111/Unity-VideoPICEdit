// 蒙版手绘笔刷。
//
// 直接往蒙版图上画，不做乒乓——靠混合操作就够了：
//   涂抹用 Max，重叠的笔迹取较大值，不会越涂越过头；
//   擦除用 Min，笔迹给的是 1-衰减，中心处把蒙版压到 0。
// 这样一次拖拽甩出几十个笔迹也只是几十个小四边形，不用反复拷贝整张图。
Shader "Hidden/Love/MaskBrush"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }

    CGINCLUDE
    #include "UnityCG.cginc"

    half _Hardness;     // 0 全羽化，1 硬边
    half _Flow;         // 单次笔迹的强度

    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

    v2f vert (appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.uv;
        return o;
    }

    // uv 是笔迹局部坐标，(0.5,0.5) 是圆心
    half Falloff (float2 uv)
    {
        half d = length(uv - 0.5h) * 2.0h;          // 0 圆心 1 边缘
        half inner = clamp(_Hardness, 0.0h, 0.99h);
        return (1.0h - smoothstep(inner, 1.0h, d)) * _Flow;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // Pass 0：涂抹
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag (v2f i) : SV_Target { half a = Falloff(i.uv); return half4(a, a, a, a); }
            ENDCG
        }

        // Pass 1：擦除
        Pass
        {
            BlendOp Min
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag (v2f i) : SV_Target { half a = 1.0h - Falloff(i.uv); return half4(a, a, a, a); }
            ENDCG
        }
    }
    Fallback Off
}
