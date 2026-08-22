Shader "MazeEscape/ShadowEyes"
{
    // The wraith's two eyes. Unlit with an HDR base colour rather than a lit material with
    // emission: URP strips the _EMISSION keyword from script-edited materials on reimport,
    // so every emissive-only surface in this project is authored as URP-style Unlit + HDR
    // (same call as Ceiling Light.mat). Values above 1 trip MazePostFx bloom (threshold 0.95).
    //
    // Opaque on purpose. The body is ZWrite Off, so these render first in the opaque pass and
    // the transparent shadow then blends over them -- wisps drift across the eyes for free,
    // and from behind the near-solid head hides them to roughly 3%, below the bloom threshold.
    Properties
    {
        [HDR] _EyeColor    ("Eye colour", Color)                 = (3.4, 3.4, 3.2, 1)
        [HDR] _HaloColor   ("Outer halo colour", Color)          = (0.55, 0.56, 0.70, 1)
        _CoreTightness     ("Core tightness", Range(0.5, 8))     = 2.6
        _PulseSpeed        ("Pulse speed", Float)                = 0.85
        _PulseAmount       ("Pulse amount", Range(0, 1))         = 0.18
        _FlickerSpeed      ("Flicker speed", Float)              = 7.0
        _FlickerAmount     ("Flicker amount", Range(0, 1))       = 0.10
        _Dim               ("Dim (script driven)", Range(0, 1))  = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent+100"
        }

        Pass
        {
            Name "ShadowEyes"
            Tags { "LightMode" = "UniversalForward" }

            // Drawn AFTER the body so the eyes punch through the hair. This is what lets the
            // face be covered in dense strings instead of carving a bald exclusion zone around
            // the eyes to keep them visible -- that exclusion was thinning the whole face.
            // Cull Back on a single-sided patch means they vanish when he faces away, so the
            // eyes never glow through the back of his head. ZTest LEqual keeps walls occluding.
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _EyeColor;
                float4 _HaloColor;
                float  _CoreTightness;
                float  _PulseSpeed;
                float  _PulseAmount;
                float  _FlickerSpeed;
                float  _FlickerAmount;
                float  _Dim;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float  fogFactor   : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogFactor   = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // facing the camera = hot core, glancing = dim halo, so it reads as a glowing orb
                float facing = saturate(dot(N, V));
                float core   = pow(facing, _CoreTightness);

                float t       = _Time.y;
                float pulse   = 1.0 + sin(t * _PulseSpeed) * _PulseAmount;
                float flicker = 1.0 - _FlickerAmount * saturate(frac(sin(floor(t * _FlickerSpeed) * 12.9898) * 43758.5453));

                float3 col = lerp(_HaloColor.rgb, _EyeColor.rgb, core) * pulse * flicker;
                col *= (1.0 - _Dim);

                col = MixFog(col, IN.fogFactor);
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
