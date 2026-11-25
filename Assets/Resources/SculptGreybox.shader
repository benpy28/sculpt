// Greybox buildings (Ben 2026-11-21 pivot). Unlit: colour comes from vertex colours with the fixed-sun shading per
// face baked in by BrushView. Vertex alpha picks the surface:
//   1   = unbreakable greybox: faint 1.5 m tile seams from world position, so the grid reads at a glance.
//   0   = breakable brick: world-space running-bond bricks whose joints line up with the tile edges, per-brick tone and
//         slow macro variation, so a wall reads as one wall and the tile grid disappears.
//   0.5 = rough (clay, steel, rubble, decals): no seams, fine grain and macro variation only.
// Purely visual: nothing here changes the shape. URP only.
Shader "SCULPT/Greybox"
{
    Properties
    {
        _Seam ("Tile Seam Darken", Range(0, 1)) = 0.22
        _SeamWidth ("Tile Seam Width (m)", Range(0.005, 0.08)) = 0.025
        _TileSize ("Tile Size (m)", Float) = 1.5
        _Noise ("Surface Noise", Range(0, 0.2)) = 0.04
        _BrickW ("Brick Length (m)", Float) = 0.25
        _BrickH ("Brick Course (m)", Float) = 0.075
        _Mortar ("Mortar Width (m)", Range(0.002, 0.02)) = 0.007
        _Macro ("Macro Variation", Range(0, 0.3)) = 0.09
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
                float _Seam;
                float _SeamWidth;
                float _TileSize;
                float _Noise;
                float _BrickW;
                float _BrickH;
                float _Mortar;
                float _Macro;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float fogFactor : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            float Hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // Smooth value noise (trilinear over the hash lattice), about -0.5..0.5.
            float ValueNoise(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = lerp(Hash3(i), Hash3(i + float3(1, 0, 0)), f.x);
                float b = lerp(Hash3(i + float3(0, 1, 0)), Hash3(i + float3(1, 1, 0)), f.x);
                float c = lerp(Hash3(i + float3(0, 0, 1)), Hash3(i + float3(1, 0, 1)), f.x);
                float d = lerp(Hash3(i + float3(0, 1, 1)), Hash3(i + float3(1, 1, 1)), f.x);
                return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z) - 0.5;
            }

            // Running-bond brick on a vertical face: per-brick tone, worn bevel and mortar joints.
            // fw = how many metres one pixel covers here. Mortar joints are 7 mm, so from a few metres away they are
            // thinner than a pixel and the pattern turns into moire; the joints and per-brick tones fade into their
            // average as a pixel grows toward half a brick course.
            half3 Brick(float3 p, float3 n, half3 c, float fw)
            {
                bool alongX = n.z > n.x;
                float2 uv = alongX ? p.xy : p.zy;
                float row = floor(uv.y / _BrickH);
                float u = uv.x / _BrickW + (row - 2.0 * floor(row * 0.5)) * 0.5;
                float col = floor(u);
                float fx = frac(u) * _BrickW, fy = frac(uv.y / _BrickH) * _BrickH;
                float edge = min(min(fx, _BrickW - fx), min(fy, _BrickH - fy));
                float inBrick = smoothstep(_Mortar * 0.5 - fw * 0.5, _Mortar + fw * 0.5, edge);
                float tone = 1.0 + (Hash3(float3(col, row, alongX ? 1.0 : 7.0)) - 0.5) * 0.18;
                float bevel = lerp(0.86, 1.0, smoothstep(_Mortar, _Mortar * 2.5, edge));
                half3 brick = c * tone * bevel;
                half lum = dot(c, half3(0.3, 0.5, 0.2));
                half3 mortar = lerp(c, lum.xxx * 1.25, 0.75);
                float mortarShare = saturate(1.5 * _Mortar * (1.0 / _BrickH + 1.0 / _BrickW));
                half3 average = lerp(c * 0.97, mortar, mortarShare);
                float fade = saturate((fw - _BrickH * 0.12) / (_BrickH * 0.38));
                return lerp(lerp(mortar, brick, inBrick), average, fade);
            }

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.color = i.color;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.positionWS = ws;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }

            // Distance (in metres) to the nearest tile line along one axis.
            float LineDist(float v)
            {
                float t = v / _TileSize;
                return abs(t - round(t)) * _TileSize;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = abs(normalize(i.normalWS));
                float3 p = i.positionWS;
                half3 c = i.color.rgb;
                float surface = i.color.a;
                // Pixel footprint in metres, taken outside the branches below (derivatives need uniform control flow).
                float fw = max(max(fwidth(p.x), fwidth(p.y)), fwidth(p.z));
                if (surface > 0.75)
                {
                    // Unbreakable greybox: seams on the two axes that lie in the face.
                    float d = 1e3;
                    if (n.x < 0.9) d = min(d, LineDist(p.x));
                    if (n.y < 0.9) d = min(d, LineDist(p.y));
                    if (n.z < 0.9) d = min(d, LineDist(p.z));
                    float seam = 1.0 - _Seam * (1.0 - smoothstep(0.0, _SeamWidth, d));
                    float grain = 1.0 + (Hash3(floor(p * 12.0)) - 0.5) * _Noise;
                    c *= seam * grain;
                }
                else
                {
                    // Breakable: no seams. Slow variation over a few metres so neighbouring tiles never read as
                    // identical squares, plus fine grain.
                    float macro = 1.0 + (ValueNoise(p * 0.37) + ValueNoise(p * 1.1) * 0.5) * _Macro * 1.4;
                    // Fine grain (about 3 cm) fades out once a pixel is bigger than it, or it shimmers.
                    float grain = 1.0 + ValueNoise(p * 31.0) * _Noise * 1.6 * saturate(1.0 - (fw - 0.01) / 0.02);
                    if (surface < 0.25 && n.y < 0.5) c = Brick(p, n, c, fw);
                    c *= macro * grain;
                }
                c = MixFog(c, i.fogFactor);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
