// Shared picture effects for the world shaders (SCULPT/Voxel, SCULPT/Greybox): sun shadows, screen-space ambient
// occlusion, rich directional lighting and atmospheric height fog. Driven by globals from Rendering/LookSettings.cs,
// which follow the Video settings (all zero = the clean competitive picture, exactly the old unlit look).
// Visual only. Fairness rules: only scenery (_Scenery = 1 on its material) takes any of this. Players, loot and
// effects are drawn exactly as before, so nothing here can make a player harder to see, and players never cast
// shadows (their renderers have shadow casting off), so a shadow can never give someone away round a corner.
#ifndef SCULPT_LOOK_INCLUDED
#define SCULPT_LOOK_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// x = shadow strength (0 = off), y = rich lighting 0..1, z = atmospheric fog density per metre (0 = off),
// w = ambient occlusion strength (0 = off).
float4 _SculptLook;
// xyz = direction toward the sun (unit), w = 1 when set from code.
float4 _SculptSunDir;
// rgb = sun tint on lit faces and the fog's glow toward the sun.
float4 _SculptSunCol;
// rgb = colour of everything out of the sun (sky light), multiplied in.
float4 _SculptShadeCol;
// rgb = atmospheric fog colour (matches the sky's horizon), a = most the fog may cover (0..1).
float4 _SculptFogCol;

// The world's lighting on top of the baked per-axis shade. n = world normal (unit), positionCS = SV_POSITION.
half3 SculptLight(half3 c, float3 ws, float3 n, float4 positionCS, float scenery)
{
    if (scenery < 0.5) return c;
    float shadowOn = _SculptLook.x;
    float rich = _SculptLook.y;
    if (shadowOn > 0.0 || rich > 0.0)
    {
        float3 L = _SculptSunDir.w > 0.5 ? _SculptSunDir.xyz : float3(0.33, 0.82, -0.47);
        float ndl = dot(n, L);
        // Faces turned from the sun are out of it; the edge is quick so walls read as clean planes.
        float lit = saturate(ndl * 4.0);
        float atten = 1.0;
        #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
        if (shadowOn > 0.0)
        {
            float4 sc = TransformWorldToShadowCoord(ws);
            atten = MainLightRealtimeShadow(sc);
            atten = lerp(atten, 1.0, GetMainLightShadowFade(ws));
        }
        #endif
        lit *= atten;
        // How deep "out of the sun" goes: shadows alone give a cool shade, rich lighting adds sun warmth and sky
        // and bounce colour by facing.
        float depth = max(shadowOn, rich);
        half3 shade = lerp(half3(1, 1, 1), _SculptShadeCol.rgb, depth);
        half3 sun = lerp(half3(1, 1, 1), _SculptSunCol.rgb, rich);
        c *= lerp(shade, sun, lit);
        if (rich > 0.0)
        {
            // Hemisphere: tops pick up a little sky, undersides a little warm bounce from the ground.
            half3 hemi = n.y >= 0.0 ? lerp(half3(1, 1, 1), half3(0.97, 1.0, 1.05), n.y) : lerp(half3(1, 1, 1), half3(1.06, 1.0, 0.92), -n.y);
            c *= lerp(half3(1, 1, 1), hemi, rich);
        }
    }
    #if defined(_SCREEN_SPACE_OCCLUSION)
    if (_SculptLook.w > 0.0)
    {
        AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(positionCS));
        // Never deeper than ~45% darker, and faded out right in front of the camera: pressed against a wall the
        // screen-space estimate sees "everything occluded" and turned the whole view black and noisy.
        // positionCS.w in the fragment stage is the view depth in metres.
        float nearFade = smoothstep(0.6, 2.0, positionCS.w);
        half occ = max(ao.indirectAmbientOcclusion, half(0.55));
        c *= lerp(1.0, occ, _SculptLook.w * nearFade);
    }
    #endif
    return c;
}

// Atmospheric height fog over the scenery (after the classic linear haze): thicker low down, thinner with height,
// glowing toward the sun. Exact integral of an exponential height fog along the view ray.
half3 SculptAtmosphere(half3 c, float3 ws, float scenery)
{
    float density = _SculptLook.z;
    if (scenery < 0.5 || density <= 0.0) return c;
    float3 v = ws - _WorldSpaceCameraPos.xyz;
    float dist = length(v);
    if (dist < 1e-3) return c;
    const float k = 0.045; // height falloff per metre: half as thick ~15 m up
    float camH = max(_WorldSpaceCameraPos.y, 0.0);
    float dy = v.y * k;
    float h = abs(dy) > 1e-4 ? (1.0 - exp(-dy)) / dy : 1.0;
    float optical = density * exp(-k * camH) * dist * h;
    float f = (1.0 - exp(-optical)) * _SculptFogCol.a;
    float3 L = _SculptSunDir.w > 0.5 ? _SculptSunDir.xyz : float3(0.33, 0.82, -0.47);
    float toSun = pow(saturate(dot(v / dist, L)), 6.0);
    half3 fog = lerp(_SculptFogCol.rgb, _SculptFogCol.rgb * _SculptSunCol.rgb * 1.12, toSun);
    return lerp(c, fog, saturate(f));
}

#endif
