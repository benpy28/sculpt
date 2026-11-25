// Soft dust puffs for breaking walls and bullet chips (TileDebris). Camera-facing quads built on the CPU; uv runs -1..1
// across each quad, vertex alpha is the puff's opacity. Alpha blended, no depth write, never fully opaque, so dust
// softens a view for a moment but never hides a player behind it for long. URP only.
Shader "SCULPT/Dust"
{
    Properties
    {
        _Softness ("Edge Softness", Range(0.1, 1)) = 0.75
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Softness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float fogFactor : TEXCOORD1;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.color = i.color;
                o.uv = i.uv;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r = length(i.uv);
                // Lumpy edge: a few lobes so puffs aren't perfect discs.
                float ang = atan2(i.uv.y, i.uv.x);
                float lobes = 1.0 - 0.12 * (sin(ang * 3.0 + i.color.a * 40.0) * 0.5 + 0.5);
                float a = 1.0 - smoothstep(lobes * (1.0 - _Softness), lobes, r);
                a *= i.color.a;
                half3 c = MixFog(i.color.rgb, i.fogFactor);
                return half4(c, a);
            }
            ENDHLSL
        }
    }
}
