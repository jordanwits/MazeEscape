Shader "MazeEscape/ShadowFog"
{
    // Dense dark fog that pours off the wraith and pools on the floor.
    //
    // ONE shader drives both halves of the effect:
    //   * billboard PARTICLES for the cascade running down his body, and
    //   * a flat ground QUAD for the pool it settles into.
    // Both want the same thing -- a soft-edged blob of dark, eaten into by drifting noise --
    // so they differ only in material settings and the mesh they sit on.
    //
    // Noise is sampled in WORLD space, not UV space. On the ground pool that makes the fog
    // creep across the floor instead of sliding with the quad, and on the particles it means
    // every puff samples a different region, so a hundred billboards of the same quad never
    // show the same silhouette twice.
    //
    // Vertex COLOR is respected so a ParticleSystem's Color-over-Lifetime and
    // Size-over-Lifetime modules do the fade in/out rather than the shader guessing.
    Properties
    {
        _FogColor    ("Fog colour", Color)                    = (0.020, 0.020, 0.030, 1)
        _Density     ("Density", Range(0,3))                  = 1.15
        _EdgeSoftness("Edge softness", Range(0.01,1))         = 0.62
        _NoiseScale  ("Noise scale (cycles per world metre)", Float) = 1.9
        _NoiseGain   ("Noise bite", Range(0,1.5))             = 0.85
        _ScrollSpeed ("Scroll speed", Float)                  = 0.16
        _SinkSpeed   ("Downward drift", Float)                = 0.22
        _SoftFade    ("Soft depth fade (m)", Float)           = 0.45
        _HeightFade  ("Height fade (m, 0 = off)", Float)      = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }

        Pass
        {
            Name "ShadowFog"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float  _Density;
                float  _EdgeSoftness;
                float  _NoiseScale;
                float  _NoiseGain;
                float  _ScrollSpeed;
                float  _SinkSpeed;
                float  _SoftFade;
                float  _HeightFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 color       : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float  viewDepth   : TEXCOORD3;
                float  fogFactor   : TEXCOORD4;
            };

            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.71, 0.113, 0.419));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float vnoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash31(i + float3(0,0,0)), n100 = hash31(i + float3(1,0,0));
                float n010 = hash31(i + float3(0,1,0)), n110 = hash31(i + float3(1,1,0));
                float n001 = hash31(i + float3(0,0,1)), n101 = hash31(i + float3(1,0,1));
                float n011 = hash31(i + float3(0,1,1)), n111 = hash31(i + float3(1,1,1));
                return lerp(lerp(lerp(n000,n100,f.x), lerp(n010,n110,f.x), f.y),
                            lerp(lerp(n001,n101,f.x), lerp(n011,n111,f.x), f.y), f.z);
            }

            float fbm(float3 p)
            {
                float a = 0.5, s = 0.0, n = 0.0;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    s += a * vnoise(p); n += a; p *= 2.11; a *= 0.5;
                }
                return s / max(n, 1e-4);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv          = IN.uv;
                OUT.color       = IN.color;
                OUT.viewDepth   = -TransformWorldToView(OUT.positionWS).z;
                OUT.fogFactor   = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                // soft round blob from the quad UVs -- a billboard puff, or the pool's edge
                float2 d = IN.uv - 0.5;
                float  r = saturate(length(d) * 2.0);
                float  blob = 1.0 - smoothstep(1.0 - _EdgeSoftness, 1.0, r);

                // world-space drift: sideways crawl plus a slow sink
                float3 np = IN.positionWS * _NoiseScale
                          + float3(_Time.y * _ScrollSpeed, -_Time.y * _SinkSpeed,
                                   _Time.y * _ScrollSpeed * 0.7);
                float n = fbm(np);
                float n2 = fbm(np * 2.6 + 31.7);

                float density = blob * saturate(1.0 - _NoiseGain * (1.0 - (n * 0.65 + n2 * 0.35)));
                float alpha = saturate(density * _Density) * IN.color.a;

                // optional vertical fade so a tall emitter thins out with height
                if (_HeightFade > 0.001)
                    alpha *= saturate(1.0 - (IN.positionWS.y / _HeightFade));

                // Soft depth fade so the pool does not slice into the floor and puffs do not
                // show a hard intersection line against walls.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                float  sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                alpha *= saturate((sceneEye - IN.viewDepth) / max(_SoftFade, 1e-3));

                float3 col = _FogColor.rgb * IN.color.rgb;

                // Same guard as LivingShadow: with no fog keyword active ComputeFogFactor
                // returns 0, and an unguarded multiply would erase the effect entirely.
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                    alpha *= saturate(IN.fogFactor);
                #endif

                return float4(col, saturate(alpha));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
