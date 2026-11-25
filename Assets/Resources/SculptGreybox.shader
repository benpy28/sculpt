// Greybox buildings (Ben 2026-11-21 pivot). Unlit: colour comes from vertex colours with the fixed-sun shading per
// face baked in by BrushView. Vertex alpha picks the surface:
//   1   = unbreakable greybox: faint 1.5 m tile seams from world position, so the grid reads at a glance.
//   0   = breakable brick: world-space running-bond bricks whose joints line up with the tile edges, per-brick tone and
//         slow macro variation, so a wall reads as one wall and the tile grid disappears.
//   0.5 = rough (clay, steel, rubble, decals): no seams, fine grain and macro variation only.
// Breakable walls and floors also carry smooth per-vertex damage, cement, steel and break-edge fields (uv1, uv2) for
// cracks and patches by area (see WallState).
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
                // Breakable walls and floors (BrushView.Cells): x damage 0..1, y patched 0..1 (uv1); x reinforced 0..1,
                // y break edge 0..1 (uv2). Smooth over the wall: each vertex takes the cells round it, so cracks,
                // cement and steel spread by area and never show the tile grid. Zero on every other mesh.
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float fogFactor : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 cell : TEXCOORD3;
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
                o.cell = float4(i.uv1, i.uv2);
                return o;
            }

            // Distance to the nearest edge between Voronoi cells in 2D (F2 - F1): thin lines along the edges, like the
            // cracks of a broken material. Scale chosen by the caller.
            float CrackLines(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float d1 = 8.0, d2 = 8.0;
                for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 g = float2(x, y);
                        float2 o = float2(Hash3(float3(i + g, 3.1)), Hash3(float3(i + g, 7.7)));
                        float d = length(g + o - f);
                        if (d < d1) { d2 = d1; d1 = d; }
                        else if (d < d2) d2 = d;
                    }
                return d2 - d1;
            }

            // Damage, cement patches and steel on a breakable face, from the smooth per-vertex fields. Purely visual.
            half3 WallState(half3 c, float3 p, float3 n, float4 s, float fw)
            {
                float damage = s.x, patch = s.y, steel = s.z, edge = s.w;
                float2 uv = n.y > 0.5 ? p.xz : (n.z > n.x ? p.xy : p.zy);
                // Rough, fixed edges for the cement and steel areas (so they never look like squares).
                float rough = ValueNoise(float3(uv * 5.0, 1.3)) * 0.5 + ValueNoise(float3(uv * 13.0, 2.9)) * 0.25;
                float isPatch = smoothstep(0.46, 0.54, patch + rough * 0.6);
                float isSteel = smoothstep(0.46, 0.54, steel + rough * 0.6);
                // The fixed sun shading of the face (as BrushView bakes it), for colours that replace the brick.
                float shade = n.y > 0.5 ? 1.0 : (n.z > n.x ? 0.8 : 0.68);
                half3 cement = half3(0.50, 0.48, 0.42) * shade; // linear: a dull grey-beige render
                float cementGrain = 1.0 + ValueNoise(float3(uv * 40.0, 5.0)) * 0.10 * saturate(1.0 - (fw - 0.01) / 0.02);
                cementGrain *= 1.0 + ValueNoise(float3(uv * 3.0, 8.0)) * 0.12; // trowel marks
                // A darker ring where the cement meets the old wall.
                float rim = 1.0 - 0.3 * (1.0 - smoothstep(0.0, 0.1, abs(patch + rough * 0.6 - 0.5)));
                c = lerp(c, cement * cementGrain, isPatch) * lerp(1.0, rim, saturate(patch * 4.0));
                // Steel: cool brushed plate, no pattern that could read as a grid.
                float brushed = 1.0 + ValueNoise(float3(uv.x * 2.0, uv.y * 60.0, 4.0)) * 0.10 + ValueNoise(float3(uv * 1.3, 6.0)) * 0.10;
                half3 steelCol = half3(0.36, 0.40, 0.45) * shade * brushed;
                float steelRim = 1.0 - 0.35 * (1.0 - smoothstep(0.0, 0.08, abs(steel + rough * 0.6 - 0.5)));
                c = lerp(c, steelCol, isSteel) * lerp(1.0, steelRim, saturate(steel * 4.0));
                // Cracks: lines grow in as the damage rises (wider and more of them), with a darker spalled tone.
                float grow = saturate(damage * 1.4);
                float scale = 4.0 + 6.0 * grow;
                float lines = CrackLines(uv * scale + rough);
                // Where the cracks reach: a soft, ragged edge (never the cell grid), and line width from the damage.
                float reach = smoothstep(0.18, 0.5, damage + rough * 0.45);
                float width = (0.012 + 0.045 * grow) * reach;
                float aa = fw * scale; // a pixel, in crack-pattern units: thin far lines fade instead of breaking into dots
                float crack = (1.0 - smoothstep(width - aa, width + aa, lines)) * saturate(width / max(aa, 1e-4)) * reach;
                c *= 1.0 - 0.18 * smoothstep(0.2, 1.0, damage);       // spalled, dusty
                c = lerp(c, c * 0.25, crack * lerp(1.0, 0.5, saturate(fw * 20.0)));
                // Scorched and chipped right at a break.
                c *= 1.0 - 0.35 * edge * (0.7 + rough * 0.6);
                return c;
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
                    if (dot(i.cell, i.cell) > 1e-6) c = WallState(c, p, n, i.cell, fw);
                }
                c = MixFog(c, i.fogFactor);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
