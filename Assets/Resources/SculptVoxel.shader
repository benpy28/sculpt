// Unlit terrain shader: colour comes from vertex colours (fixed-sun shading per normal is baked in by the mesher).
// Adds rock detail from world position (no grid lines: it must never read as a cube game) and darkens seams
// between pieces (uv.x = seam weight from the mesher). Purely visual: nothing here changes the shape. URP only.
Shader "SCULPT/Voxel"
{
    Properties
    {
        _Detail ("Rock Detail", Range(0, 0.3)) = 0.09
        _DetailScale ("Detail Scale (per metre)", Range(0.5, 12)) = 3.2
        _Strata ("Strata", Range(0, 0.2)) = 0.015
        _Macro ("Large-scale Colour Variation", Range(0, 0.3)) = 0.10
        _CrystalGloss ("Crystal Gloss", Range(0, 1)) = 0.55
        _CrystalGlow ("Crystal Inner Glow", Range(0, 1)) = 0.35
        _CrystalFacet ("Crystal Facet Scale (per metre)", Range(0.5, 8)) = 2.6
        _SeamDarken ("Seam Darken", Range(0, 1)) = 0.45
        _SculptGlow ("Sculptable Edge Glow", Range(0, 1)) = 0.08
        [HideInInspector] _SculptMode ("Sculpt Mode", Range(0, 1)) = 0
        [HideInInspector] _SculptEye ("Sculpt Eye", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SculptRange ("Sculpt Range", Float) = 6
        [HideInInspector] _SculptRipple ("Sculpt Ripple Radius", Float) = 100
        [HideInInspector] _SculptRippleGlow ("Sculpt Ripple Afterglow", Range(0, 1)) = 0
        _VeinColor ("Reinforced Vein Colour", Color) = (0.86, 0.95, 1.0, 1)
        _VeinScale ("Reinforced Vein Scale (per metre)", Range(0.5, 6)) = 1.7
        _VeinStrength ("Reinforced Vein Strength", Range(0, 1)) = 0.6
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
                float _Macro;
                float _CrystalGloss;
                float _CrystalGlow;
                float _CrystalFacet;
                float _SeamDarken;
                float _SculptGlow;
                float _SculptMode;
                float4 _SculptEye;
                float _SculptRange;
                float _SculptRipple;
                float _SculptRippleGlow;
                float4 _VeinColor;
                float _VeinScale;
                float _VeinStrength;
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

            // Cellular noise: id and edge distance of the nearest jittered point (irregular facets, no grid).
            float2 Cells(float3 p, out float3 cellId)
            {
                float3 b = floor(p);
                float d1 = 8.0, d2 = 8.0;
                cellId = b;
                for (int z = -1; z <= 1; z++)
                    for (int y = -1; y <= 1; y++)
                        for (int x = -1; x <= 1; x++)
                        {
                            float3 c = b + float3(x, y, z);
                            float3 j = c + float3(Hash3(c), Hash3(c + 17.3), Hash3(c + 41.9));
                            float d = distance(p, j);
                            if (d < d1) { d2 = d1; d1 = d; cellId = c; }
                            else if (d < d2) d2 = d;
                        }
                return float2(d1, d2 - d1);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float3 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float3 uv : TEXCOORD0;
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
                float crystal = saturate(i.uv.z);
                // Large, slow colour drift (warm/cool patches) so the ground never repeats on a grid rhythm.
                float macro = Noise3(i.positionWS * 0.16 + 3.7) - 0.5;
                half3 drift = half3(1.0 + macro * 0.6, 1.0, 1.0 - macro * 0.6);
                half3 c = i.color.rgb * (1.0 + _Detail * (1.0 - 0.6 * crystal) * (n - 0.5) * 2.0 - _Strata * strata * (1.0 - crystal))
                          * (1.0 - _SeamDarken * seam) * lerp(1.0 + macro * 2.0 * _Macro, 1.0, crystal) * lerp(drift, 1.0, crystal * 0.5 + 0.5);

                // Sculpted pieces: amethyst crystal. Irregular facets (each tilts the light a little), glossy highlights
                // and a soft fresnel rim from a fixed sun, brighter chamfered edges, and a slow inner glow. Opaque and
                // never brighter than a readable rock: it must not hide or outshine a player. Visual only.
                if (crystal > 0.001)
                {
                    float3 N = normalize(i.normalWS);
                    float3 V = normalize(GetWorldSpaceViewDir(i.positionWS));
                    float3 id;
                    float2 cd = Cells(i.positionWS * _CrystalFacet, id);
                    float3 tilt = float3(Hash3(id + 3.1), Hash3(id + 7.7), Hash3(id + 12.9)) - 0.5;
                    float3 fn = normalize(N + tilt * 0.55);
                    float3 L = normalize(float3(0.35, 0.85, 0.40));
                    float3 Hh = normalize(L + V);
                    float spec = pow(saturate(dot(fn, Hh)), 40.0);
                    float rim = pow(1.0 - saturate(dot(fn, V)), 3.0);
                    float facetShade = 0.88 + 0.24 * Hash3(id);
                    float facetLine = 1.0 - smoothstep(0.0, 0.05, cd.y);
                    float3 cA = abs(N);
                    float chamfer = saturate((1.0 - max(cA.x, max(cA.y, cA.z))) * 3.4);
                    float glowN = Noise3(i.positionWS * 1.1 + float3(0, _Time.y * 0.12, 0));
                    float inner = smoothstep(0.45, 0.95, glowN);
                    const half3 lilac = half3(0.80, 0.70, 1.00);
                    const half3 core = half3(0.55, 0.30, 0.95);
                    half3 cc = c * facetShade;
                    cc = lerp(cc, cc * 0.82, facetLine);
                    cc += lilac * (spec * _CrystalGloss + rim * 0.22 * _CrystalGloss + chamfer * 0.10);
                    cc += core * inner * _CrystalGlow * 0.35;
                    c = lerp(c, cc, crystal);
                }

                // Reinforced pieces (1 - vertex alpha): crystalline veins through the rock with a faint travelling
                // shimmer and sparse glints. Pale ice white, never purple. Visual only.
                float reinf = saturate(1.0 - i.color.a);
                if (reinf > 0.001)
                {
                    float3 vw = i.positionWS * _VeinScale;
                    float r1 = 1.0 - abs(Noise3(vw) * 2.0 - 1.0);
                    float r2 = 1.0 - abs(Noise3(vw * 2.3 + 7.7) * 2.0 - 1.0);
                    float vein = smoothstep(0.90, 0.985, r1) + 0.6 * smoothstep(0.93, 0.99, r2);
                    float shimmer = 0.5 + 0.5 * sin(_Time.y * 2.6 + dot(i.positionWS, float3(1.3, 2.1, 0.9)) * 1.7);
                    float glint = step(0.996, Hash3(floor(i.positionWS * 14.0) + floor(_Time.y * 5.0)));
                    c = lerp(c, _VeinColor.rgb, saturate(vein) * _VeinStrength * reinf * (0.65 + 0.35 * shimmer));
                    c += _VeinColor.rgb * (0.05 * shimmer + 0.5 * glint) * reinf;
                }

                // Sculpt overlay (local to this player's terrain material; players and enemies use other materials).
                // Purple means "sculptable" and nothing else: chamfered edges of sculptable rock within range glow faintly,
                // brighter in sculpt mode, with a ripple on entering; out-of-range rock dims, non-sculptables desaturate.
                float sculptable = saturate(i.uv.y);
                float d = distance(i.positionWS, _SculptEye.xyz);
                float inRange = 1.0 - smoothstep(_SculptRange - 0.5, _SculptRange + 0.3, d);
                float3 an = abs(normalize(i.normalWS));
                float edge = saturate((1.0 - max(an.x, max(an.y, an.z))) * 3.4);
                const half3 purple = half3(0.62, 0.33, 1.0);
                c += purple * edge * sculptable * inRange * (_SculptGlow + 0.14 * _SculptMode);
                float band = exp(-pow((d - _SculptRipple) / 0.45, 2.0)) * step(_SculptRipple, _SculptRange + 0.6);
                c += purple * band * sculptable * 0.35 * _SculptMode;
                c += purple * sculptable * inRange * step(d, _SculptRipple) * (0.03 + 0.15 * edge) * _SculptRippleGlow;
                float lum = dot(c, half3(0.3, 0.59, 0.11));
                c = lerp(c, lerp(c, lum.xxx, 0.35 * (1.0 - sculptable)), _SculptMode);
                c *= lerp(1.0, lerp(0.84, 1.0, inRange), _SculptMode * sculptable);
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
