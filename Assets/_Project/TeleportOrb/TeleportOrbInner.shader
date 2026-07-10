// TeleportOrbInner.shader
// URP-compatible, procedurally-animated dark "void orb" surface.
// Glossy near-black base with blue-black swirling smoke emission, fresnel rim, and a
// main-light specular hotspot so it reads as glossy/reflective. Fully self-illuminated
// so it stays readable in a dark maze. All motion is driven by _Time + procedural noise
// (no gameplay scripts). Visual only.
Shader "MazeEscape/TeleportOrbInner"
{
    Properties
    {
        [Header(Base)]
        _BaseColor ("Base Color", Color) = (0.015, 0.02, 0.04, 1)
        [HDR] _EmissionColor ("Emission Color", Color) = (0.06, 0.20, 0.55, 1)
        _EmissionStrength ("Emission Strength", Range(0, 8)) = 0.7

        [Header(Swirl)]
        _SwirlSpeed ("Swirl Speed", Range(0, 4)) = 0.5
        _NoiseScale ("Noise Scale", Range(0.5, 12)) = 3.5
        _DistortionStrength ("Distortion Strength", Range(0, 2)) = 0.7
        _SwirlContrast ("Swirl Contrast", Range(0, 1)) = 0.6

        [Header(Rim and Gloss)]
        [HDR] _FresnelColor ("Fresnel Color", Color) = (0.06, 0.20, 0.55, 1)
        _FresnelPower ("Fresnel Power", Range(0.25, 10)) = 4.0
        _Smoothness ("Smoothness (Gloss)", Range(0, 1)) = 0.92
        _Reflectivity ("Reflectivity", Range(0, 2)) = 0.4
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                float  _Smoothness;
                float  _Reflectivity;
            CBUFFER_END

            // --- Procedural value-noise fBm (no textures) ---
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

                // Object-space swirl domain (internal, view-independent).
                float3 p = IN.positionOS * _NoiseScale;

                // Twirl around Y: outer shells rotate more than the core -> swirling smoke.
                float ang = t + length(p.xz) * _DistortionStrength * 1.5;
                float s = sin(ang);
                float c = cos(ang);
                p.xz = float2(p.x * c - p.z * s, p.x * s + p.z * c);

                // Domain-warped fBm for smoky, liquid-like motion.
                float3 warp = float3(
                    fbm(p + t * 0.35),
                    fbm(p + float3(5.2, 1.3, 2.7) + t * 0.27),
                    fbm(p + float3(9.1, 4.4, 6.1) - t * 0.22));
                float smoke = fbm(p + warp * _DistortionStrength * 2.0);
                // Normalize typical fBm range, then sharpen -> mostly black with thin energy veins.
                smoke = saturate((smoke - 0.35) / 0.45);
                smoke = pow(smoke, lerp(1.5, 5.0, _SwirlContrast));

                // Fresnel rim.
                float ndv = saturate(dot(N, V));
                float fres = pow(1.0 - ndv, _FresnelPower);

                float3 baseCol = _BaseColor.rgb;
                float3 emis = _EmissionColor.rgb * smoke * _EmissionStrength;
                float3 rim  = _FresnelColor.rgb * fres;

                // Cheap ambient + main-light gloss hotspot (glossy read even in low light).
                half3 ambient = SampleSH(N) * baseCol;
                Light mainLight = GetMainLight();
                float3 H = normalize(mainLight.direction + V);
                float specTerm = pow(saturate(dot(N, H)), lerp(16.0, 256.0, _Smoothness));
                float3 spec = mainLight.color * specTerm * _Smoothness * _Reflectivity;
                float3 pseudoRefl = _FresnelColor.rgb * fres * _Reflectivity * 0.5;

                float3 color = baseCol * 0.5 + ambient + emis + rim + spec + pseudoRefl;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    // Fallback supplies ShadowCaster / DepthOnly / DepthNormals passes.
    FallBack "Universal Render Pipeline/Lit"
}
