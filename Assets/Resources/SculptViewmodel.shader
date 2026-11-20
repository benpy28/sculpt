// First-person gun. Opaque, vertex colour with baked per-axis shading (like the world), plus a faint rim so the
// silhouette reads against rock. Depth is squashed toward the near plane so the gun never clips into walls; it is
// only ever drawn for the local player's own view.
Shader "SCULPT/Viewmodel"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _DepthSquash ("Depth Squash", Range(0.001, 1)) = 0.04
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+50" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float _DepthSquash;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float3 normalVS : TEXCOORD0;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float4 cs = TransformObjectToHClip(i.positionOS.xyz);
                // Squash depth into the slab just behind the near plane.
            #if UNITY_REVERSED_Z
                cs.z = cs.w - (cs.w - cs.z) * _DepthSquash;
            #else
                cs.z = -cs.w + (cs.z + cs.w) * _DepthSquash;
            #endif
                o.positionCS = cs;
                o.color = i.color * _Tint;
                o.normalVS = TransformWorldToViewDir(TransformObjectToWorldNormal(i.normalOS));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float rim = pow(saturate(1.0 - abs(normalize(i.normalVS).z)), 3.0);
                return half4(i.color.rgb * (1.0 + 0.25 * rim), 1);
            }
            ENDHLSL
        }
    }
}
