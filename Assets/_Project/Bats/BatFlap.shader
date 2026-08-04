// Wing flap for the decorative dungeon bats, done entirely in the vertex shader.
//
// The bat mesh (Prefabs/Maze Components/Bats/Bat.fbx) is a static ~180-tri mesh with no rig and no
// animation clip. Flapping is a rotation of the wing vertices about the bat's local forward (+Z)
// axis, weighted so the body stays rigid and the bend accelerates toward the tip. That buys three
// things a skinned rig would not: no Animator and no CPU skinning per bat, a per-renderer _Phase so
// a swarm never flaps in unison, and a flap rate the flight script can drive continuously (fast
// burst out of the roost, slower once gliding) instead of cross-fading animation states.
//
// The deform lives in BatDeform() and is applied identically by EVERY pass. That matters here:
// PostFx/PosterizeEdge.shader runs a Roberts-cross edge detector over SCENE DEPTH, so if the
// DepthOnly pass drew undeformed wings the stylized outline would sit off the rendered silhouette.
// Same reason the ShadowCaster deforms — the wall shadow is most of the scare.
//
// Cull Off: the wing membrane is a single-sided sheet. The fragment stage flips the normal on back
// faces so both sides light correctly; the body is closed so it costs nothing there.
Shader "MazeEscape/BatFlap"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.075, 0.062, 0.058, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.12

        [Header(Flap)]
        _FlapAmplitude("Flap Amplitude (radians)", Range(0, 1.6)) = 0.85
        _FlapRate("Flap Rate (Hz)", Range(0, 20)) = 7
        _Phase("Phase Offset (radians)", Float) = 0
        // Above ~0.8 the spanwise phase spread curls the wingtips past vertical and the wing
        // reads as a closing loop rather than a wingbeat.
        _WaveLag("Tip Wave Lag", Range(0, 3)) = 0.7
        _BendPower("Bend Falloff", Range(0.5, 4)) = 1.7

        [Header(Wing extents in object space)]
        _ShoulderX("Shoulder X", Float) = 0.0187
        _TipX("Tip X", Float) = 0.22
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
            float _FlapAmplitude;
            float _FlapRate;
            float _Phase;
            float _WaveLag;
            float _BendPower;
            float _ShoulderX;
            float _TipX;
        CBUFFER_END

        // Rotates wing vertices about local Z (the bat's forward axis). Weight is 0 inboard of the
        // shoulder and 1 at the tip, so the body and head never move. sign(x) mirrors the rotation
        // so both wingtips rise together instead of one rolling the bat over.
        void BatDeform(inout float3 positionOS, inout float3 normalOS)
        {
            float span = max(1e-4, _TipX - _ShoulderX);
            float w = saturate((abs(positionOS.x) - _ShoulderX) / span);
            w = pow(w, _BendPower);

            // The -w * _WaveLag term delays the outer wing behind the root, so the flap travels
            // outward as a wave rather than the wing swinging as one rigid plank.
            float angle = _FlapAmplitude * w
                        * sin(6.2831853 * _FlapRate * _TimeParameters.x + _Phase - w * _WaveLag);
            angle *= (positionOS.x >= 0.0) ? 1.0 : -1.0;

            float s, c;
            sincos(angle, s, c);
            positionOS.xy = float2(positionOS.x * c - positionOS.y * s,
                                   positionOS.x * s + positionOS.y * c);
            normalOS.xy   = float2(normalOS.x * c - normalOS.y * s,
                                   normalOS.x * s + normalOS.y * c);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fogCoord   : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                float3 posOS = IN.positionOS.xyz;
                float3 nrmOS = IN.normalOS;
                BatDeform(posOS, nrmOS);

                VertexPositionInputs pos = GetVertexPositionInputs(posOS);

                Varyings OUT;
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS   = TransformObjectToWorldNormal(nrmOS);
                OUT.fogCoord   = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                // Membrane back faces need the normal flipped or the underside of the wing goes black.
                float3 normalWS = normalize(IN.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);

                InputData inputData = (InputData)0;
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord        = IN.fogCoord;
                inputData.bakedGI         = SampleSH(normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = _BaseColor.rgb;
                surfaceData.alpha      = 1.0h;
                surfaceData.metallic   = 0.0h;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion  = 1.0h;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, IN.fogCoord);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma target 3.0
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings shadowVert(Attributes IN)
            {
                float3 posOS = IN.positionOS.xyz;
                float3 nrmOS = IN.normalOS;
                BatDeform(posOS, nrmOS);

                float3 positionWS = TransformObjectToWorld(posOS);
                float3 normalWS   = TransformObjectToWorldNormal(nrmOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                Varyings OUT;
                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 shadowFrag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Feeds _CameraDepthTexture. PosterizeEdge derives its silhouette outlines from that depth,
        // so this pass must deform exactly like ForwardLit.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag
            #pragma target 3.0

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings depthVert(Attributes IN)
            {
                float3 posOS = IN.positionOS.xyz;
                float3 nrmOS = IN.normalOS;
                BatDeform(posOS, nrmOS);

                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(posOS);
                return OUT;
            }

            half4 depthFrag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex depthNormalsVert
            #pragma fragment depthNormalsFrag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            Varyings depthNormalsVert(Attributes IN)
            {
                float3 posOS = IN.positionOS.xyz;
                float3 nrmOS = IN.normalOS;
                BatDeform(posOS, nrmOS);

                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(posOS);
                OUT.normalWS   = TransformObjectToWorldNormal(nrmOS);
                return OUT;
            }

            half4 depthNormalsFrag(Varyings IN, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                return half4(NormalizeNormalPerPixel(normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
