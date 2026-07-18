Shader "Severance/MirrorReflection"
{
    // Screen-space planar-mirror surface. The actual reflected image is rendered every frame by
    // PlanarMirror.cs into a RenderTexture (a second camera reflected across this plane) and pushed
    // into _ReflectionTex through a MaterialPropertyBlock — so this shader just samples that texture
    // at the fragment's on-screen position. No lighting: the reflection already contains the lit
    // world. See PlanarMirror.cs.
    Properties
    {
        [HDR] _Tint ("Tint", Color) = (0.92, 0.94, 0.97, 1)
        _ReflectionStrength ("Reflection Strength", Range(0,1)) = 1
        _BaseColor ("Base (behind reflection)", Color) = (0.02, 0.02, 0.03, 1)

        [NoScaleOffset] _ReflectionTex ("Reflection (runtime)", 2D) = "black" {}

        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 4
        _FresnelStrength ("Fresnel Edge Boost", Range(0, 1)) = 0.25

        [NoScaleOffset] _Smudge ("Smudge / Grime (mul)", 2D) = "white" {}
        _SmudgeTiling ("Smudge Tiling", Float) = 1
        _SmudgeStrength ("Smudge Strength", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "MirrorReflection"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ReflectionTex);   SAMPLER(sampler_ReflectionTex);
            TEXTURE2D(_Smudge);          SAMPLER(sampler_Smudge);

            CBUFFER_START(UnityPerMaterial)
                half4  _Tint;
                half4  _BaseColor;
                half   _ReflectionStrength;
                half   _FresnelPower;
                half   _FresnelStrength;
                half   _SmudgeTiling;
                half   _SmudgeStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos   : TEXCOORD0;
                float2 uv          : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = pos.positionCS;
                OUT.screenPos   = pos.positionNDC;   // ComputeScreenPos equivalent (xy/w -> [0,1])
                OUT.uv          = IN.uv;
                OUT.normalWS    = nrm.normalWS;
                OUT.positionWS  = pos.positionWS;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 screenUV = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);

                half3 reflection = SAMPLE_TEXTURE2D(_ReflectionTex, sampler_ReflectionTex, screenUV).rgb;
                reflection *= _Tint.rgb;

                // Optional grime layer breaks up a perfectly clean mirror.
                if (_SmudgeStrength > 0.0h)
                {
                    half3 smudge = SAMPLE_TEXTURE2D(_Smudge, sampler_Smudge, IN.uv * _SmudgeTiling).rgb;
                    reflection *= lerp(half3(1,1,1), smudge, _SmudgeStrength);
                }

                // Grazing-angle brighten — real glass reflects more strongly at the edges.
                half3 N = normalize(IN.normalWS);
                half3 V = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                half fresnel = pow(1.0h - saturate(dot(N, V)), _FresnelPower) * _FresnelStrength;
                half strength = saturate(_ReflectionStrength + fresnel);

                half3 col = lerp(_BaseColor.rgb, reflection, strength);
                return half4(col, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
