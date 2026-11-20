// Instanced meadow grass and flowers (GrassField). Unlit like the terrain; colour from the same field as the ground
// under it (SculptNature.hlsl). Blades sway in the shared wind, squash flat within about a metre of any player and
// shrink with distance, so grass never hides a player. Visual only, no collision. Flower heads are yellow, white,
// red or sky blue: never purple (purple means "sculptable" only).
Shader "SCULPT/Grass"
{
    Properties
    {
        _Flower ("Flower", Float) = 0
        _Fade ("Draw Distance", Float) = 46
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "SculptNature.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _Flower;
            float _Fade;
        CBUFFER_END

        float4 _GrassActors[16];  // player feet (xyz), from GrassField
        float _GrassActorCount;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 rootWS : TEXCOORD1;
            float fogFactor : TEXCOORD2;
            float dirt : TEXCOORD3;
        };

        Varyings GrassVert(Attributes i)
        {
            UNITY_SETUP_INSTANCE_ID(i);
            Varyings o;
            float3 root = TransformObjectToWorld(float3(0, 0, 0));
            float3 ws = TransformObjectToWorld(i.positionOS.xyz);
            float h = saturate(i.uv.y);

            // Squash near players: never hides feet or a crouched body.
            float k = 1.0;
            for (int a = 0; a < 16; a++)
            {
                if (a >= (int)_GrassActorCount) break;
                float3 d = root - _GrassActors[a].xyz;
                if (d.y > -1.0 && d.y < 0.6) k = min(k, smoothstep(0.45, 1.3, length(d.xz)));
            }
            float far = 1.0 - smoothstep(_Fade * 0.7, _Fade, distance(root, _WorldSpaceCameraPos));
            float dirt = NatureDirt(root);
            float scaleH = lerp(0.1, 1.0, k) * far * lerp(1.0, 0.35, dirt);

            float3 local = ws - root;
            local.y *= scaleH;
            local.xz *= lerp(0.6, 1.0, far);
            float ph = dot(root.xz, float2(0.23, 0.31));
            float sway = (sin(_SculptWind.w * 2.2 + ph) * 0.5 + 0.5) * (0.3 + 0.7 * _SculptWind.z)
                       + sin(_SculptWind.w * 5.1 + ph * 2.3) * 0.12;
            local.xz += _SculptWind.xy * sway * 0.09 * h * h * scaleH;
            ws = root + local;

            o.positionCS = TransformWorldToHClip(ws);
            o.uv = i.uv;
            o.rootWS = root;
            o.fogFactor = ComputeFogFactor(o.positionCS.z);
            o.dirt = dirt;
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex GrassVert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            half4 frag(Varyings i) : SV_Target
            {
                float h = saturate(i.uv.y);
                float n = NHash(floor(i.rootWS * 37.0));
                half3 c = NatureGrass(i.rootWS) * (0.9 + 0.2 * n);
                c = lerp(c, NLin(half3(0.62, 0.61, 0.31)), i.dirt * 0.6);
                c *= lerp(0.55, 1.18, h); // darker at the root, sunlit tips

                if (_Flower > 0.5 && i.uv.x > 1.5)
                {
                    float pick = NHash(floor(i.rootWS * 7.0) + 3.3);
                    half3 head = pick < 0.35 ? half3(1.00, 0.84, 0.22)   // buttercup yellow
                               : pick < 0.65 ? half3(0.96, 0.95, 0.90)   // daisy white
                               : pick < 0.85 ? half3(0.88, 0.24, 0.16)   // poppy red
                               :               half3(0.45, 0.66, 0.96);  // sky blue
                    c = NLin(head) * lerp(0.85, 1.05, h);
                    if (i.uv.y > 0.95) c = lerp(c, NLin(half3(0.95, 0.75, 0.2)), 0.6); // centre
                }
                c = MixFog(c, i.fogFactor);
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex GrassVert
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            half4 fragDepth(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
