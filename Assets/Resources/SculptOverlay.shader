// Unlit transparent overlay for sculpt feedback: ghosts, piece outlines, windup tells, arrows, throw lanes,
// flying-piece trails and particles. Vertex colour times _Color, optional soft round sprite (uv) for particles.
// _ZTest 4 = LEqual (hidden behind terrain), 8 = Always (drawn through). Never used on players.
Shader "SCULPT/Overlay"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Soft ("Round soft sprite (particles)", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Soft;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.color = i.color * _Color;
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 c = i.color;
                float r = length(i.uv - 0.5) * 2.0;
                c.a *= lerp(1.0, saturate(1.0 - r * r), _Soft);
                return c;
            }
            ENDHLSL
        }
    }
}
