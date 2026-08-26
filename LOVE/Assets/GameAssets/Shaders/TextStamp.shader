// 把字渲进贴图用的。
//
// 内置的 "GUI/Text Shader" 写的是 Color [_Color] + combine primary,
// 固定管线里那个 primary 是材质颜色不是顶点色，所以 GL.Color 对它无效——
// 一句话：拿它画出来的字永远是材质上那个颜色。自己写一个才能按顶点色上色。
Shader "Hidden/Love/TextStamp"
{
    Properties { _MainTex ("字体图集", 2D) = "white" {} }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        Lighting Off Cull Off ZTest Always ZWrite Off Fog { Mode Off }
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 动态字体图集是 Alpha8，形状只在 alpha 里，rgb 不可用
                fixed a = tex2D(_MainTex, i.uv).a;
                return fixed4(i.color.rgb, i.color.a * a);
            }
            ENDCG
        }
    }

    Fallback Off
}
