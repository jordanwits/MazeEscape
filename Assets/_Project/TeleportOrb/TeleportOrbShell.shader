// TeleportOrbShell.shader
// URP-compatible transparent energy shell for the teleport orb. Additive fresnel rim
// halo + faint scrolling cloud noise. Sits on a slightly larger sphere than the inner
// orb to create an edge glow that reads from every angle. Motion is driven by _Time +
// procedural noise (no gameplay scripts). Visual only.
Shader "MazeEscape/TeleportOrbShell"
{
    Properties
    {
        [Header(Base)]
        _BaseColor ("Base Color", Color) = (0.01, 0.03, 0.08, 1)
        [HDR] _EmissionColor ("Emission Color", Color) = (0.07, 0.24, 0.65, 1)
        _EmissionStrength ("Emission Strength", Range(0, 8)) = 1.0

        [Header(Swirl)]
        _SwirlSpeed ("Swirl Speed", Range(0, 4)) = 0.35
        _NoiseScale ("Noise Scale", Range(0.5, 12)) = 2.5
        _DistortionStrength ("Distortion Strength", Range(0, 2)) = 0.5
        _SwirlContrast ("Swirl Contrast", Range(0, 1)) = 0.6

        [Header(Rim)]
        [HDR] _FresnelColor ("Fresnel Color", Color) = (0.15, 0.40, 0.90, 1)
        _FresnelPower ("Fresnel Power", Range(0.25, 10)) = 3.0
        _Alpha ("Alpha (Transparency)", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha One   // additive, weighted by alpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionOS  : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EmissionColor;
                float4 _FresnelColor;
                float  _EmissionStrength;
                float  _SwirlSpeed;
                float  _NoiseScale;
                float  _DistortionStrength;
                float  _SwirlContrast;
                float  _FresnelPower;
                float  _Alpha;
            CBUFFER_END

            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float vnoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash13(i + float3(0,0,0));
                float n100 = hash13(i + float3(1,0,0));
                float n010 = hash13(i + float3(0,1,0));
                float n110 = hash13(i + float3(1,1,0));
                float n001 = hash13(i + float3(0,0,1));
                float n101 = hash13(i + float3(1,0,1));
                float n011 = hash13(i + float3(0,1,1));
                float n111 = hash13(i + float3(1,1,1));
                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            float fbm(float3 p)
            {
                float sum = 0.0;
                float amp = 0.5;
                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    sum += amp * vnoise(p);
                    p *= 2.02;
                    amp *= 0.5;
                }
                return sum;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = nrmInputs.normalWS;
                OUT.positionOS  = IN.positionOS.xyz;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                float t = _Time.y * _SwirlSpeed;

                float3 p = IN.positionOS * _NoiseScale;
                float ang = t * 0.8 + length(p.xy) * _DistortionStrength;
                float s = sin(ang);
                float c = cos(ang);
                p.xy = float2(p.x * c - p.y * s, p.x * s + p.y * c);

                float3 warp = float3(
                    fbm(p + t * 0.30),
                    fbm(p + float3(3.1, 1.7, 4.2) - t * 0.20),
                    fbm(p + float3(7.7, 2.3, 8.9) + t * 0.25));
                float cloud = fbm(p + warp * _DistortionStrength * 2.0);
                cloud = saturate((cloud - 0.35) / 0.45);
                cloud = pow(cloud, lerp(1.5, 5.0, _SwirlContrast));

                float fres = pow(1.0 - saturate(dot(N, V)), _FresnelPower);

                float3 col = _BaseColor.rgb + _EmissionColor.rgb * cloud * _EmissionStrength;
                col += _FresnelColor.rgb * fres * _EmissionStrength;

                // Halo concentrated at the rim; only a faint internal cloud.
                float a = saturate(fres * fres * 1.0 + cloud * 0.12) * _Alpha;
                return half4(col, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
