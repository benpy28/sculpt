// Full-screen passes of our own (Rendering/SculptPostPass.cs), on top of URP's post-processing:
//   pass 0  Sharpen: contrast-adaptive sharpening (5 taps, limited to the local min/max so edges never ring or halo).
//   pass 1  Light shafts: the sky seen near the sun is smeared toward it and added back as warm light. Sky pixels
//           take the light; anything solid in front of the sun gets at most a faint warm wash, so a player there
//           stays a dark, readable shape.
// Both are skipped entirely when their setting is off. Our own code, nothing licensed.
Shader "SCULPT/Post"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "Sharpen"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _SculptSharpen;   // 0..1
            float4 _SculptTexel;    // xy = 1 / target size

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float2 t = _SculptTexel.xy;
                half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv).rgb;
                half3 n = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2(0, t.y)).rgb;
                half3 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv - float2(0, t.y)).rgb;
                half3 e = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2(t.x, 0)).rgb;
                half3 w = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv - float2(t.x, 0)).rgb;
                half3 mn = min(c, min(min(n, s), min(e, w)));
                half3 mx = max(c, max(max(n, s), max(e, w)));
                // Less sharpening where local contrast is already high (the adaptive part).
                half3 amp = saturate(min(mn, 1.0 - mx) / max(mx, 1e-4));
                amp = sqrt(amp);
                half peak = lerp(-0.125, -0.2, _SculptSharpen);
                half3 wgt = amp * peak;
                half3 r = (c + (n + s + e + w) * wgt) / (1.0 + 4.0 * wgt);
                r = clamp(r, mn, mx);
                return half4(lerp(c, r, saturate(_SculptSharpen * 1.5)), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "LightShafts"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _SculptSunScreen; // xy = sun in screen uv, z = strength (0 = off: sun behind or far off screen)
            float4 _SculptSunCol;

            #define SHAFT_SAMPLES 24

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                half4 src = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float strength = _SculptSunScreen.z;
                if (strength <= 0.0) return src;
                float2 toSun = _SculptSunScreen.xy - uv;
                float len = length(toSun);
                // Only within ~0.8 screen of the sun.
                float reach = 1.0 - smoothstep(0.25, 0.8, len);
                if (reach <= 0.0) return src;
                float2 stepUv = toSun / SHAFT_SAMPLES;
                float2 p = uv;
                float sum = 0.0, decay = 1.0, total = 0.0;
                // A per-pixel offset hides the steps (no visible banding), from screen position only.
                float jitter = frac(sin(dot(uv * _ScreenParams.xy, float2(12.9898, 78.233))) * 43758.5453);
                p += stepUv * jitter;
                [unroll]
                for (int k = 0; k < SHAFT_SAMPLES; k++)
                {
                    float raw = SampleSceneDepth(p);
                    #if UNITY_REVERSED_Z
                    float sky = raw <= 1e-6 ? 1.0 : 0.0;
                    #else
                    float sky = raw >= 1.0 - 1e-6 ? 1.0 : 0.0;
                    #endif
                    sum += sky * decay;
                    total += decay;
                    decay *= 0.93;
                    p += stepUv;
                }
                float shaft = sum / total;
                // Full light where this pixel is sky; solid things (players included) in front of the sun get at most
                // a faint warm wash (under a tenth of white), so they still read as clear dark silhouettes.
                float raw0 = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                float here = raw0 <= 1e-6 ? 1.0 : 0.2;
                #else
                float here = raw0 >= 1.0 - 1e-6 ? 1.0 : 0.2;
                #endif
                float glow = shaft * reach * reach * strength * here;
                src.rgb += _SculptSunCol.rgb * glow * 0.45;
                return src;
            }
            ENDHLSL
        }
    }
}
