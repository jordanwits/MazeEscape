Shader "Severance/FrostedGlass"
{
    // Refractive frosted glass. Unlike a plain transparent material — which can only tint what is
    // behind it — this samples URP's _CameraOpaqueTexture in a disc around the fragment, so
    // silhouettes behind the pane genuinely blur out instead of staying sharp.
    //
    // Requires "Opaque Texture" and "Depth Texture" on the active URP asset (both on in
    // Assets/Settings/PC_RPAsset.asset). Depth is used to reject taps that sit in FRONT of the
    // pane, which stops the player's held item / arms smearing across the glass.
    //
    // The pass composites the backdrop itself and writes with Blend One Zero, so there is no
    // hardware alpha blend — "how milky" is _Density, not an alpha channel. Consequence: two of
    // these panes overlapping do not stack (the opaque texture is captured before transparents
    // render, so the nearer pane simply wins).
    Properties
    {
        [HDR] _Tint ("Frost Tint", Color) = (0.87, 0.90, 0.93, 1)
        _Density ("Frost Density", Range(0, 1)) = 0.55

        _BlurRadius ("Blur Radius (texels)", Range(0, 24)) = 7
        _BlurPerspective ("Blur Perspective Falloff", Range(0, 1)) = 1

        _GrainScale ("Grain Tiling", Float) = 14
        _GrainDistortion ("Grain Distortion", Range(0, 3)) = 1
        _GrainMottle ("Grain Mottle", Range(0, 1)) = 0.3

        _Smoothness ("Smoothness", Range(0, 1)) = 0.55
        [HDR] _SpecTint ("Specular Tint", Color) = (0.30, 0.31, 0.33, 1)
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 4
        _FresnelStrength ("Fresnel Edge", Range(0, 1)) = 0.25

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FrostedGlass"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite Off
            ZTest LEqual
            Blend One Zero          // backdrop is composited in-shader, no hardware blend

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // Forward+ (the project's renderer is m_RenderingMode: 2) routes additional lights
            // through the cluster loop; _ADDITIONAL_LIGHTS covers the plain forward fallback.
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half4 _SpecTint;
                half  _Density;
                half  _BlurRadius;
                half  _BlurPerspective;
                half  _GrainScale;
                half  _GrainDistortion;
                half  _GrainMottle;
                half  _Smoothness;
                half  _FresnelPower;
                half  _FresnelStrength;
                half  _Cull;
            CBUFFER_END

            // Two rings of 8. Inner ring sits at half radius; the outer ring is rotated 22.5 deg
            // so the taps interleave instead of forming spokes.
            static const float2 kRingInner[8] =
            {
                float2( 0.5000,  0.0000), float2( 0.3536,  0.3536),
                float2( 0.0000,  0.5000), float2(-0.3536,  0.3536),
                float2(-0.5000,  0.0000), float2(-0.3536, -0.3536),
                float2( 0.0000, -0.5000), float2( 0.3536, -0.3536)
            };
            static const float2 kRingOuter[8] =
            {
                float2( 0.9239,  0.3827), float2( 0.3827,  0.9239),
                float2(-0.3827,  0.9239), float2(-0.9239,  0.3827),
                float2(-0.9239, -0.3827), float2(-0.3827, -0.9239),
                float2( 0.3827, -0.9239), float2( 0.9239, -0.3827)
            };

            static const half kCentreWeight = 1.2h;
            static const half kInnerWeight  = 1.0h;
            static const half kOuterWeight  = 0.6h;

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // Grain coordinates come from OBJECT space, not world space: the separator door swings,
            // and world-locked grain would visibly crawl across the glass as it moves.
            float2 FrostCoords(float3 positionOS, float3 normalOS)
            {
                float3 an = abs(normalOS);
                if (an.y > an.x && an.y > an.z) return positionOS.xz;   // horizontal pane
                if (an.x > an.z)               return positionOS.zy;   // pane facing X
                return positionOS.xy;                                  // pane facing Z
            }

            // One blurred tap. Anything nearer to the camera than the pane is rejected so
            // foreground geometry cannot bleed into the frost.
            half3 Backdrop(float2 uv, float paneEyeDepth, half3 fallback)
            {
                float tapEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                if (tapEye < paneEyeDepth - 0.05)
                    return fallback;
                return (half3)SampleSceneColor(uv);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos   : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 positionOS  : TEXCOORD3;
                float3 normalOS    : TEXCOORD4;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS);

                OUT.positionHCS = pos.positionCS;
                OUT.screenPos   = pos.positionNDC;
                OUT.positionWS  = pos.positionWS;
                OUT.normalWS    = nrm.normalWS;
                OUT.positionOS  = IN.positionOS.xyz;
                OUT.normalOS    = IN.normalOS;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 screenUV     = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                float  paneEyeDepth = IN.screenPos.w;

                half3 N = normalize(IN.normalWS);
                half3 V = (half3)GetWorldSpaceNormalizeViewDir(IN.positionWS);

                // --- frost grain -------------------------------------------------------------
                float2 grainUV = FrostCoords(IN.positionOS, normalize(IN.normalOS)) * _GrainScale;
                float  n1 = ValueNoise(grainUV);
                float  n2 = ValueNoise(grainUV * 2.17 + 31.4);
                half   grain = (half)(n1 * 0.65 + n2 * 0.35);

                // --- blur footprint ----------------------------------------------------------
                // Radius is expressed in opaque-texture texels, which keeps it isotropic
                // regardless of aspect ratio and scales automatically with render scale.
                float persp  = lerp(1.0, saturate(3.0 / max(paneEyeDepth, 0.1)), _BlurPerspective);
                float2 radius = _BlurRadius * persp * _CameraOpaqueTexture_TexelSize.xy;

                // Uneven surface: shove the sample centre around with the grain so the refraction
                // wobbles rather than reading as a clean gaussian.
                float2 warp   = (float2(n1, n2) - 0.5) * _GrainDistortion * radius * 2.0;
                float2 baseUV = screenUV + warp;

                half3 centre = (half3)SampleSceneColor(baseUV);
                half3 acc    = centre * kCentreWeight;
                half  wsum   = kCentreWeight;

                [unroll]
                for (int i = 0; i < 8; ++i)
                {
                    acc  += Backdrop(baseUV + kRingInner[i] * radius, paneEyeDepth, centre) * kInnerWeight;
                    acc  += Backdrop(baseUV + kRingOuter[i] * radius, paneEyeDepth, centre) * kOuterWeight;
                    wsum += kInnerWeight + kOuterWeight;
                }
                half3 backdrop = acc / wsum;

                // --- lighting on the pane itself ---------------------------------------------
                // Needed by LIGHT_LOOP_BEGIN under Forward+ (it reads inputData by name).
                InputData inputData = (InputData)0;
                inputData.positionWS              = IN.positionWS;
                inputData.normalWS                = N;
                inputData.viewDirectionWS         = V;
                inputData.normalizedScreenSpaceUV = screenUV;

                half  shininess = exp2(10.0h * _Smoothness + 1.0h);
                half3 diffuse   = SampleSH(N);
                half3 specular  = half3(0, 0, 0);

                Light mainLight = GetMainLight();
                half3 mainCol   = mainLight.color * mainLight.distanceAttenuation;
                diffuse  += mainCol * saturate(dot(N, mainLight.direction));
                specular += LightingSpecular(mainCol, mainLight.direction, N, V, _SpecTint, shininess);

                #if defined(_ADDITIONAL_LIGHTS)
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light light = GetAdditionalLight(lightIndex, IN.positionWS);
                        half3 lc = light.color * light.distanceAttenuation;
                        diffuse  += lc * saturate(dot(N, light.direction));
                        specular += LightingSpecular(lc, light.direction, N, V, _SpecTint, shininess);
                    LIGHT_LOOP_END
                #endif

                // --- composite ---------------------------------------------------------------
                // Mottling makes some patches clearer than others, which is what stops a flat
                // tint from reading as painted-on white.
                half density = saturate(_Density * lerp(1.0h, grain * 1.4h, _GrainMottle));
                half3 frost  = _Tint.rgb * diffuse;

                half3 col = lerp(backdrop, frost, density);

                half fresnel = pow(1.0h - saturate(dot(N, V)), _FresnelPower) * _FresnelStrength;
                col += specular;
                col += _Tint.rgb * fresnel;

                return half4(col, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
