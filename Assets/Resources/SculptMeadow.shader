// Meadow grass on the smooth height-map ground (graphics pass 2026-11-28). Every blade is made in the vertex shader
// from its instance number: a world-locked lattice of spots round the camera (so blades never swim as you move),
// jittered by a hash, rooted on the ground from a height texture, and left out where the mask says no grass (paving,
// buildings, rock faces, off the map). No mesh, no per-blade data and no instancing variant, so a built game draws it
// exactly like the Editor (the old GrassField's instanced variant was stripped from builds).
// Fairness: every blade is under 0.28 m, pressed flat within about a metre of any player, and the grass step only
// changes how many blades are drawn, never how tall: no setting lets grass hide anyone. Purple is never used here.
Shader "SCULPT/Meadow"
{
    Properties
    {
        [HideInInspector] _Ring ("Ring: spacing, cells across, inner radius, outer radius", Vector) = (0.25, 200, 0, 25)
        [HideInInspector] _Origin ("Ring origin (lattice cell of instance 0): x, z in cells", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "SculptNature.hlsl"

        float4 _Ring;
        float4 _Origin;

        // Ground: x = origin x, y = origin z, z = 1 / (cell * texels), w = texels (from MeadowView).
        float4 _MeadowMap;
        TEXTURE2D(_MeadowHeight); SAMPLER(sampler_MeadowHeight);
        TEXTURE2D(_MeadowMask); SAMPLER(sampler_MeadowMask);
        float4 _MeadowActors[16]; // player feet (xyz), w = 1 when used

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 rootWS : TEXCOORD1;
            float2 hf : TEXCOORD2;   // x = height along the blade 0..1, y = flower (1) or blade (0)
            float fogFactor : TEXCOORD3;
            float side : TEXCOORD4;
        };

        float MHash(float2 p)
        {
            float3 q = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
            q += dot(q, q.yzx + 33.33);
            return frac((q.x + q.y) * q.z);
        }

        // Blade shape: 3 segments, 5 triangles, 15 vertices (non-indexed). Each entry = (side -1/+1 or 0 at the tip, level).
        static const float2 Shape[15] =
        {
            float2(-1, 0), float2(1, 0), float2(-1, 1),
            float2(1, 0), float2(1, 1), float2(-1, 1),
            float2(-1, 1), float2(1, 1), float2(-1, 2),
            float2(1, 1), float2(1, 2), float2(-1, 2),
            float2(-1, 2), float2(1, 2), float2(0, 3),
        };

        Varyings Collapsed()
        {
            Varyings o = (Varyings)0;
            o.positionCS = float4(0, 0, -2, 1); // behind the near plane: clipped, costs nothing to rasterise
            return o;
        }

        Varyings MeadowVert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
        {
            float spacing = _Ring.x;
            uint across = (uint)_Ring.y;
            float2 cell = _Origin.xy + float2(iid % across, iid / across);
            float r0 = MHash(cell), r1 = MHash(cell + 17.31), r2 = MHash(cell + 41.7), r3 = MHash(cell + 73.1);
            float2 xz = (cell + float2(r0, r1)) * spacing;
            float dist = distance(xz, _WorldSpaceCameraPos.xz);
            if (dist < _Ring.z || dist > _Ring.w) return Collapsed();

            float2 uv = (xz - _MeadowMap.xy) * _MeadowMap.z + 0.5 / _MeadowMap.w;
            float mask = SAMPLE_TEXTURE2D_LOD(_MeadowMask, sampler_MeadowMask, uv, 0).r;
            if (mask <= r2 * 0.98 + 0.01) return Collapsed();
            float groundY = SAMPLE_TEXTURE2D_LOD(_MeadowHeight, sampler_MeadowHeight, uv, 0).r;
            float3 root = float3(xz.x, groundY - 0.03, xz.y);

            // Pressed flat round players (never hides feet or a crouched body).
            float squash = 1.0;
            for (int a = 0; a < 16; a++)
            {
                float4 p = _MeadowActors[a];
                if (p.w < 0.5) continue;
                float3 d = root - p.xyz;
                if (d.y > -1.2 && d.y < 0.8) squash = min(squash, smoothstep(0.4, 1.2, length(d.xz)));
            }
            // Fade out toward the ring's outer edge (the last ring shrinks to nothing; inner rings hand over to the next).
            float edge = 1.0 - smoothstep(_Ring.w - max(4.0, _Ring.w * 0.18), _Ring.w, dist);
            float dirt = NatureDirt(root);
            bool flower = r3 > 0.972 && dirt < 0.3;
            float height = lerp(0.13, 0.26, r3) * lerp(0.55, 1.0, mask) * lerp(1.0, 0.4, dirt);
            if (flower) height = lerp(0.18, 0.25, r0);
            height = min(height, 0.28) * lerp(0.12, 1.0, squash) * edge;
            if (height < 0.01) return Collapsed();

            float2 sv = Shape[vid];
            float level = sv.y / 3.0;
            float yaw = r2 * 6.2831853;
            float2 facing = float2(cos(yaw), sin(yaw));
            float2 across2 = float2(-facing.y, facing.x);
            float width = (flower ? 0.02 : 0.03) * lerp(1.0, 2.4, saturate((spacing - 0.2) / 0.6)) * (1.0 - level * 0.85);
            // Lean: a fixed tilt per blade plus the shared wind, growing toward the tip.
            float ph = dot(root.xz, float2(0.23, 0.31));
            float sway = (sin(_SculptWind.w * 2.1 + ph) * 0.5 + 0.5) * (0.3 + 0.7 * _SculptWind.z) + sin(_SculptWind.w * 4.7 + ph * 2.1) * 0.1;
            float2 lean = facing * (0.25 + 0.3 * r1) + _SculptWind.xy * sway * 0.6;
            float bend = level * level;
            float3 ws = root;
            ws.xz += across2 * sv.x * width + lean * bend * height;
            ws.y += level * height * (1.0 - 0.15 * bend);

            Varyings o;
            o.positionCS = TransformWorldToHClip(ws);
            o.positionWS = ws;
            o.rootWS = root;
            o.hf = float2(level, flower ? 1.0 : 0.0);
            o.fogFactor = ComputeFogFactor(o.positionCS.z);
            o.side = sv.x;
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
            #pragma vertex MeadowVert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include "SculptLook.hlsl"

            half4 frag(Varyings i) : SV_Target
            {
                float h = i.hf.x;
                half3 c = NatureGrass(i.rootWS);
                c = lerp(c, NatureSoil(i.rootWS) * 1.1, NatureDirt(i.rootWS) * 0.35);
                c *= lerp(0.72, 1.06, h);                                     // shaded root, sunlit tip
                c *= 0.94 + 0.12 * MHash(floor(i.rootWS.xz * 23.0));           // blade to blade
                if (i.hf.y > 0.5 && h > 0.66)
                {
                    float pick = MHash(floor(i.rootWS.xz * 3.0) + 9.1);
                    half3 head = pick < 0.4 ? half3(1.00, 0.84, 0.25)   // buttercup yellow
                               : pick < 0.72 ? half3(0.97, 0.96, 0.90)  // daisy white
                               : pick < 0.88 ? half3(0.86, 0.26, 0.18)  // poppy red
                               :               half3(0.50, 0.68, 0.95); // sky blue (never purple)
                    c = NLin(head);
                }
                // Lit like the ground under it (upward normal, a touch of the blade's side so it isn't flat).
                float3 n = normalize(float3(i.side * 0.25, 1.0, 0.0));
                c = SculptLight(c, i.positionWS, n, i.positionCS, 1.0);
                c = MixFog(c, i.fogFactor);
                c = SculptAtmosphere(c, i.positionWS, 1.0);
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
            #pragma vertex MeadowVert
            #pragma fragment fragDepth
            #pragma target 3.5
            half4 fragDepth(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
