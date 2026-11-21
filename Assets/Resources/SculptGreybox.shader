// Greybox buildings (Ben 2026-11-21 pivot). Unlit: colour comes from vertex colours with the fixed-sun shading per
// face baked in by BrushView. Adds faint 1.5 m tile seams from world position, so the tile grid reads at a glance,
// and a little noise so big faces aren't flat. Purely visual: nothing here changes the shape. URP only.
Shader "SCULPT/Greybox"
{
    Properties
    {
        _Seam ("Tile Seam Darken", Range(0, 1)) = 0.22
        _SeamWidth ("Tile Seam Width (m)", Range(0.005, 0.08)) = 0.025
        _TileSize ("Tile Size (m)", Float) = 1.5
        _Noise ("Surface Noise", Range(0, 0.2)) = 0.04
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
                // Seams on the two axes that lie in the face.
                float d = 1e3;
                if (n.x < 0.9) d = min(d, LineDist(p.x));
                if (n.y < 0.9) d = min(d, LineDist(p.y));
                if (n.z < 0.9) d = min(d, LineDist(p.z));
                float seam = 1.0 - _Seam * (1.0 - smoothstep(0.0, _SeamWidth, d));
                float grain = 1.0 + (Hash3(floor(p * 12.0)) - 0.5) * _Noise;
                half3 c = i.color.rgb * seam * grain;
                c = MixFog(c, i.fogFactor);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
