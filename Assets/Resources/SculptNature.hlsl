// Shared nature look for SCULPT/Voxel (ground, bark, leaves) and SCULPT/Grass (blades, flowers), so grass blades and
// the ground under them use the same colour field. Our own procedural art: no textures, nothing licensed.
// Visual only: nothing here changes a shape by more than ~1.5 cm (leaf sway), within the "what you see is what you
// hit" rule. Purple is never used here (it means "sculptable" only).
#ifndef SCULPT_NATURE_INCLUDED
#define SCULPT_NATURE_INCLUDED

// Global wind from code (WorldView): xy = direction on the ground, z = gust strength 0..1, w = time in seconds.
float4 _SculptWind;

float NHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float NNoise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(NHash(i + float3(0, 0, 0)), NHash(i + float3(1, 0, 0)), f.x),
                     lerp(NHash(i + float3(0, 1, 0)), NHash(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(NHash(i + float3(0, 0, 1)), NHash(i + float3(1, 0, 1)), f.x),
                     lerp(NHash(i + float3(0, 1, 1)), NHash(i + float3(1, 1, 1)), f.x), f.y), f.z);
}

// Nearest jittered point: x = distance, y = distance to the edge between cells (leaf clumps).
float2 NCells(float3 p, out float3 cellId)
{
    float3 b = floor(p);
    float d1 = 8.0, d2 = 8.0;
    cellId = b;
    for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                float3 c = b + float3(x, y, z);
                float3 j = c + float3(NHash(c), NHash(c + 17.3), NHash(c + 41.9));
                float d = distance(p, j);
                if (d < d1) { d2 = d1; d1 = d; cellId = c; }
                else if (d < d2) d2 = d;
            }
    return float2(d1, d2 - d1);
}

// Palette values are authored in sRGB; convert when the project renders in linear space.
half3 NLin(half3 c)
{
#if defined(UNITY_COLORSPACE_GAMMA)
    return c;
#else
    return pow(max(c, 0.0), 2.2);
#endif
}

// Grass colour field on the ground (xz only, so blades and the ground under them match).
half3 NatureGrass(float3 w)
{
    float3 p = float3(w.x, 0.0, w.z);
    float big = NNoise(p * 0.07 + 11.0);
    float mid = NNoise(p * 0.45 + 3.0);
    half3 lush = NLin(half3(0.29, 0.49, 0.16));
    half3 fresh = NLin(half3(0.46, 0.60, 0.21));
    half3 dry = NLin(half3(0.62, 0.61, 0.31));
    half3 c = lerp(lush, fresh, saturate(big * 1.4 - 0.2));
    return lerp(c, dry, smoothstep(0.62, 0.86, mid) * 0.5);
}

// Bare dirt patches in the meadow (0..1), also where no blades grow.
float NatureDirt(float3 w)
{
    return smoothstep(0.70, 0.79, NNoise(float3(w.x, 0.0, w.z) * 0.33 + 27.0));
}

half3 NatureSoil(float3 w)
{
    float n = NNoise(w * 3.1), m = NNoise(w * 9.7 + 5.0);
    return lerp(NLin(half3(0.42, 0.30, 0.20)), NLin(half3(0.29, 0.21, 0.14)), n) * (0.9 + 0.2 * m);
}

// Natural ground: grass on tops and over the lip of each step, soil just under it, rock deeper down.
// depth = metres below the top of the column this face belongs to (from the mesher).
half3 NatureGround(float3 w, float3 N, float depth)
{
    float n1 = NNoise(w * 3.1), n2 = NNoise(w * 9.7 + 5.0);
    half3 grass = NatureGrass(w) * (0.88 + 0.24 * n1) * (0.92 + 0.16 * n2);
    half3 soil = NatureSoil(w);
    half3 rock = NLin(half3(0.57, 0.55, 0.50)) * (0.80 + 0.35 * NNoise(w * 2.3 + 9.0));
    float wav = (NNoise(w * 1.7 + 2.0) - 0.5) * 0.25;
    half3 side = lerp(soil, rock, smoothstep(0.45, 0.95, depth + wav));
    float lip = (1.0 - smoothstep(0.03, 0.11, depth + wav * 0.3)) * step(-0.2, N.y);
    float topness = smoothstep(0.45, 0.8, N.y);
    half3 top = lerp(grass, soil * 1.05, NatureDirt(w) * 0.85);
    half3 c = lerp(side, top, saturate(max(topness, lip)));
    return N.y < -0.3 ? rock * 0.8 : c;
}

half3 NatureBark(float3 w, float3 N)
{
    float streak = NNoise(float3(w.x * 9.0, w.y * 1.2, w.z * 9.0));
    float knot = NNoise(w * 4.0);
    half3 b = lerp(NLin(half3(0.29, 0.21, 0.15)), NLin(half3(0.44, 0.33, 0.22)), streak) * (0.85 + 0.3 * knot);
    float moss = smoothstep(0.55, 0.8, NNoise(w * 1.5 + 4.0)) * saturate(1.2 - w.y * 0.3);
    return lerp(b, NLin(half3(0.30, 0.42, 0.16)), moss * 0.6);
}

half3 NatureLeaves(float3 w, float3 N)
{
    float3 id;
    float2 cd = NCells(w * 2.4, id);
    float tone = NHash(id);
    half3 c = lerp(NLin(half3(0.19, 0.37, 0.12)), NLin(half3(0.35, 0.54, 0.17)), tone);
    c = lerp(c, NLin(half3(0.53, 0.61, 0.22)), smoothstep(0.85, 1.0, tone) * 0.6);
    c *= lerp(0.55, 1.0, smoothstep(0.0, 0.12, cd.y));       // gaps between clumps read as depth
    c *= 0.85 + 0.3 * NNoise(w * 11.0);                       // leaf grain
    return c * lerp(0.65, 1.1, saturate(N.y * 0.5 + 0.5));    // sky light from above, darker underneath
}

// Leaf sway: at most 1.5 cm sideways (inside the ~2 cm geometry jitter the spec allows), so bullets still hit what
// is drawn. Used by every pass that draws the canopy so depth and colour agree.
float3 NatureLeafSway(float3 ws)
{
    float ph = dot(ws, float3(0.37, 0.21, 0.29));
    float s = sin(_SculptWind.w * 1.9 + ph) * 0.6 + sin(_SculptWind.w * 3.3 + ph * 1.7) * 0.4;
    ws.xz += _SculptWind.xy * s * 0.015 * (0.4 + 0.6 * _SculptWind.z);
    return ws;
}

#endif
