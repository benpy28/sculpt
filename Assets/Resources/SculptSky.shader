// Procedural sky for the plains: horizon haze to a clear blue zenith, a soft sun matching the fixed sun light, and
// slow fair-weather clouds. Background only (no gameplay meaning). Our own procedural art, no textures.
Shader "SCULPT/Sky"
{
    Properties
    {
        _Horizon ("Horizon", Color) = (0.80, 0.87, 0.92, 1)
        _Zenith ("Zenith", Color) = (0.36, 0.57, 0.86, 1)
        _Ground ("Below Horizon", Color) = (0.62, 0.68, 0.66, 1)
        _SunDir ("Sun Direction (towards the sun)", Vector) = (0.4, 0.8, 0.4, 0)
        _Clouds ("Cloud Cover", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SculptNature.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Horizon;
                float4 _Zenith;
                float4 _Ground;
                float4 _SunDir;
                float _Clouds;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.dir = i.positionOS.xyz;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 sun = normalize(_SunDir.xyz);
                float up = d.y;
                half3 c = lerp(_Horizon.rgb, _Zenith.rgb, pow(saturate(up), 0.55));
                c = up < 0 ? lerp(_Horizon.rgb, _Ground.rgb, saturate(-up * 4.0)) : c;

                // Sun: a soft disc and a wide warm glow.
                float s = saturate(dot(d, sun));
                c += half3(1.0, 0.92, 0.75) * (pow(s, 900.0) * 3.0 + pow(s, 12.0) * 0.18);

                // Clouds on a plane far overhead, drifting with the wind.
                if (up > 0.02)
                {
                    float2 p = d.xz / (up + 0.12) * 1.3 + _SculptWind.xy * _SculptWind.w * 0.004;
                    float n = NNoise(float3(p, 0)) * 0.55 + NNoise(float3(p * 2.3, 1.7)) * 0.3 + NNoise(float3(p * 5.1, 4.1)) * 0.15;
                    float cover = smoothstep(1.0 - _Clouds, 1.0 - _Clouds + 0.25, n) * smoothstep(0.02, 0.2, up);
                    half3 cloud = lerp(half3(0.78, 0.81, 0.86), half3(1.0, 0.99, 0.97), saturate(n * 1.3 - 0.2 + s * 0.3));
                    c = lerp(c, cloud, cover * 0.85);
                }
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
