// Unlit terrain shader: colour comes from vertex colours (fixed-sun shading per normal is baked in by the mesher).
// Adds rock detail from world position (no grid lines: it must never read as a cube game) and darkens seams
// between pieces (uv.x = seam weight from the mesher). Purely visual: nothing here changes the shape. URP only.
Shader "SCULPT/Voxel"
{
    Properties
    {
        _Detail ("Rock Detail", Range(0, 0.3)) = 0.09
        _DetailScale ("Detail Scale (per metre)", Range(0.5, 12)) = 3.2
        _Strata ("Strata", Range(0, 0.2)) = 0.04
        _SeamDarken ("Seam Darken", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Detail;
                float _DetailScale;
                float _Strata;
                float _SeamDarken;
            CBUFFER_END

            float Hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash3(i + float3(0, 0, 0)), Hash3(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash3(i + float3(0, 1, 0)), Hash3(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash3(i + float3(0, 0, 1)), Hash3(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash3(i + float3(0, 1, 1)), Hash3(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

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
                float3 positionWS : TEXCOORD2;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.color = i.color;
                o.uv = i.uv;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                o.positionWS = p.positionWS;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 w = i.positionWS * _DetailScale;
                float n = Noise3(w) * 0.6 + Noise3(w * 2.7 + 13.1) * 0.3 + Noise3(w * 7.3 + 4.7) * 0.1;
                float strata = sin(i.positionWS.y * 9.0 + Noise3(i.positionWS * 0.8) * 4.0) * 0.5 + 0.5;
                float seam = saturate(i.uv.x);
                seam = seam * seam;
                half3 c = i.color.rgb * (1.0 + _Detail * (n - 0.5) * 2.0 - _Strata * strata) * (1.0 - _SeamDarken * seam);
                c = MixFog(c, i.fogFactor);
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
