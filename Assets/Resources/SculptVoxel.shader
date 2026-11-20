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
        _SculptGlow ("Sculptable Edge Glow", Range(0, 1)) = 0.12
        [HideInInspector] _SculptMode ("Sculpt Mode", Range(0, 1)) = 0
        [HideInInspector] _SculptEye ("Sculpt Eye", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SculptRange ("Sculpt Range", Float) = 6
        [HideInInspector] _SculptRipple ("Sculpt Ripple Radius", Float) = 100
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
                float _SculptGlow;
                float _SculptMode;
                float4 _SculptEye;
                float _SculptRange;
                float _SculptRipple;
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
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float fogFactor : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
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
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
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

                // Sculpt overlay (local to this player's terrain material; players and enemies use other materials).
                // Purple means "sculptable" and nothing else: chamfered edges of sculptable rock within range glow faintly,
                // brighter in sculpt mode, with a ripple on entering; out-of-range rock dims, non-sculptables desaturate.
                float sculptable = saturate(i.uv.y);
                float d = distance(i.positionWS, _SculptEye.xyz);
                float inRange = 1.0 - smoothstep(_SculptRange - 0.5, _SculptRange + 0.3, d);
                float3 an = abs(normalize(i.normalWS));
                float edge = saturate((1.0 - max(an.x, max(an.y, an.z))) * 3.4);
                const half3 purple = half3(0.62, 0.33, 1.0);
                c += purple * edge * sculptable * inRange * (_SculptGlow + 0.30 * _SculptMode);
                float band = exp(-pow((d - _SculptRipple) / 0.45, 2.0)) * step(_SculptRipple, _SculptRange + 0.6);
                c += purple * band * sculptable * 0.55 * _SculptMode;
                float lum = dot(c, half3(0.3, 0.59, 0.11));
                c = lerp(c, lerp(c, lum.xxx, 0.5 * (1.0 - sculptable)), _SculptMode);
                c *= lerp(1.0, lerp(0.72, 1.0, inRange), _SculptMode * sculptable);
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
