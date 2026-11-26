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

            // Picture settings (Rendering/LookSettings): y = rich lighting, z = atmospheric fog density (0 = off).
            float4 _SculptLook;
            float4 _SculptFogCol;
            float4 _SculptSunCol;

            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 sun = normalize(_SunDir.xyz);
                float up = d.y;
                float s = saturate(dot(d, sun));
                // Three-stop gradient: horizon haze, a soft band of paler blue, then the zenith.
                half3 mid = lerp(_Horizon.rgb, _Zenith.rgb, 0.55);
                half3 c = up < 0.18 ? lerp(_Horizon.rgb, mid, saturate(up / 0.18)) : lerp(mid, _Zenith.rgb, pow(saturate((up - 0.18) / 0.82), 0.7));
                c = up < 0 ? lerp(_Horizon.rgb, _Ground.rgb, saturate(-up * 4.0)) : c;
                // The horizon is warmer and brighter on the sun's side (light scattered low through more air).
                float az = saturate(dot(normalize(float3(d.x, 0.0, d.z) + 1e-5), normalize(float3(sun.x, 0.0, sun.z) + 1e-5)) * 0.5 + 0.5);
                float low = 1.0 - smoothstep(0.0, 0.35, abs(up));
                c += half3(0.10, 0.07, 0.02) * low * az * az;

                // Sun: a crisp disc with a soft limb, a tight bright corona and a wide warm glow. The disc is above
                // white, so with bloom on (Video settings) it glows; without bloom it simply clamps like before.
                float disc = smoothstep(0.99955, 0.99975, s);
                c += half3(1.0, 0.93, 0.78) * (disc * 4.0 + pow(s, 900.0) * 1.5 + pow(s, 64.0) * 0.22 + pow(s, 8.0) * 0.12);

                // Clouds on a plane far overhead, drifting with the wind, lit from the sun's side and darker beneath.
                if (up > 0.02)
                {
                    float2 p = d.xz / (up + 0.12) * 1.3 + _SculptWind.xy * _SculptWind.w * 0.004;
                    float n = NNoise(float3(p, 0)) * 0.55 + NNoise(float3(p * 2.3, 1.7)) * 0.3 + NNoise(float3(p * 5.1, 4.1)) * 0.15;
                    float cover = smoothstep(1.0 - _Clouds, 1.0 - _Clouds + 0.25, n) * smoothstep(0.02, 0.2, up);
                    // A second sample nudged toward the sun: thinner there means that side of the cloud is lit.
                    float2 toSun = normalize(sun.xz + 1e-5) * 0.06;
                    float n2 = NNoise(float3(p + toSun, 0)) * 0.55 + NNoise(float3((p + toSun) * 2.3, 1.7)) * 0.3;
                    float litSide = saturate((n - n2) * 6.0 + 0.5);
                    half3 shadowed = half3(0.70, 0.74, 0.82);
                    half3 bright = half3(1.0, 0.98, 0.95);
                    half3 cloud = lerp(shadowed, bright, saturate(litSide * 0.7 + n * 0.5 - 0.15 + s * 0.3));
                    cloud += half3(1.0, 0.85, 0.6) * pow(s, 6.0) * 0.25 * (1.0 - cover * 0.5); // silver lining near the sun
                    c = lerp(c, cloud, cover * 0.85);
                }

                // Atmospheric fog on: the low sky melts into the same haze the ground fades to.
                if (_SculptLook.z > 0.0)
                {
                    half3 fog = lerp(_SculptFogCol.rgb, _SculptFogCol.rgb * _SculptSunCol.rgb * 1.12, pow(s, 6.0));
                    c = lerp(c, fog, (1.0 - smoothstep(-0.02, 0.22, up)) * 0.75);
                }
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
