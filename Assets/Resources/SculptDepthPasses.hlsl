// Shadow caster, depth and depth-normals passes for the world shaders (SCULPT/Voxel, SCULPT/Greybox). These only
// run when a picture setting asks for them (sun shadows, ambient occlusion, depth of field, motion blur, light
// shafts); the competitive picture never draws them. Define SCULPT_LEAF_SWAY before including to move leaves exactly
// like the colour pass does, so shadows and depth match what you see.
#ifndef SCULPT_DEPTH_PASSES_INCLUDED
#define SCULPT_DEPTH_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#ifdef SCULPT_LEAF_SWAY
#include "SculptNature.hlsl"
#endif

// Set by URP for the shadow caster pass: direction the main light travels toward.
float3 _LightDirection;

struct SculptDepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 uv1 : TEXCOORD1;
};

struct SculptDepthVaryings
{
    float4 positionCS : SV_POSITION;
    float3 normalWS : TEXCOORD0;
};

float3 SculptDepthWorld(SculptDepthAttributes i)
{
    float3 ws = TransformObjectToWorld(i.positionOS.xyz);
#ifdef SCULPT_LEAF_SWAY
    if (i.uv1.x > 2.5) ws = NatureLeafSway(ws);
#endif
    return ws;
}

SculptDepthVaryings SculptShadowVert(SculptDepthAttributes i)
{
    SculptDepthVaryings o;
    float3 ws = SculptDepthWorld(i);
    float3 n = TransformObjectToWorldNormal(i.normalOS);
    float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, n, _LightDirection));
#if UNITY_REVERSED_Z
    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
#else
    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
#endif
    o.positionCS = cs;
    o.normalWS = n;
    return o;
}

SculptDepthVaryings SculptDepthVert(SculptDepthAttributes i)
{
    SculptDepthVaryings o;
    o.positionCS = TransformWorldToHClip(SculptDepthWorld(i));
    o.normalWS = TransformObjectToWorldNormal(i.normalOS);
    return o;
}

half4 SculptDepthFrag(SculptDepthVaryings i) : SV_Target { return 0; }

half4 SculptDepthNormalsFrag(SculptDepthVaryings i) : SV_Target
{
    float3 n = normalize(i.normalWS);
#if defined(_GBUFFER_NORMALS_OCT)
    float2 oct = PackNormalOctQuadEncode(n);
    return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0.0);
#else
    return half4(n, 0.0);
#endif
}

#endif
