Shader "Severance/DiscoFloor"
{
    // Backlit dance-floor panels on a single quad. The grid, the grout lines, the panel wear and the
    // edge diffuser are all procedural from UV; the only thing that changes per frame is _ColorTex,
    // a tiny point-filtered texture holding one pixel per tile that DiscoFloor.cs rewrites on the beat.
    // Unlit on purpose: these panels are light sources, not lit surfaces (DiscoFloor.cs drives a few
    // real point lights above them so the colour actually spills onto the walls).
    Properties
    {
        [NoScaleOffset] _ColorTex ("Tile colours (driven by DiscoFloor.cs)", 2D) = "black" {}
        _TileCount ("Tiles per side", Float) = 16
        _Intensity ("Emissive intensity", Float) = 2.2

        _GroutWidth ("Grout width (fraction of a tile)", Range(0, 0.25)) = 0.06
        _GroutSoftness ("Grout softness", Range(0.001, 0.2)) = 0.02
        _GroutColor ("Grout colour", Color) = (0.02, 0.02, 0.025, 1)

        _EdgeGlow ("Diffuser edge glow", Range(0, 1)) = 0.25
        _CentreFalloff ("Panel centre falloff", Range(0, 1)) = 0.18
        _Grime ("Grime / scuffing", Range(0, 1)) = 0.35
        _TileVariation ("Per-tile brightness variation", Range(0, 1)) = 0.30
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "DiscoFloor"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ColorTex);   SAMPLER(sampler_ColorTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _GroutColor;
                float _TileCount;
                float _Intensity;
                float _GroutWidth;
                float _GroutSoftness;
                float _EdgeGlow;
                float _CentreFalloff;
                float _Grime;
                float _TileVariation;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fog : TEXCOORD1; };

            // Cheap hashes. hash21 keys per-tile constants (wear, brightness), hash noise keys grime.
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.uv = IN.uv;
                OUT.fog = ComputeFogFactor(p.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float n = max(1.0, _TileCount);
                float2 grid = IN.uv * n;
                float2 cell = floor(grid);
                float2 f = grid - cell;

                // One texel per tile, sampled dead centre so point filtering can't bleed neighbours.
                float2 tileUV = (cell + 0.5) / n;
                half3 tint = SAMPLE_TEXTURE2D(_ColorTex, sampler_ColorTex, tileUV).rgb;

                // Distance to the nearest tile edge, in tile units.
                float2 e = min(f, 1.0 - f);
                float d = min(e.x, e.y);

                // Screen-space derivative keeps the grout from aliasing into moire at grazing angles.
                float aa = max(fwidth(d), 1e-4);
                float panel = smoothstep(_GroutWidth - aa, _GroutWidth + aa + _GroutSoftness, d);

                // Panel shaping: a bright diffuser line just inside the grout, dimming toward the centre.
                float inner = saturate((d - _GroutWidth) / max(0.02, 0.5 - _GroutWidth));
                float shade = 1.0 + _EdgeGlow * (1.0 - smoothstep(0.0, 0.22, inner))
                                  - _CentreFalloff * smoothstep(0.25, 1.0, inner);

                // Worn floor: every panel sits at its own brightness, plus scuffing across the surface.
                float wear = 1.0 - _TileVariation * hash21(cell * 1.37 + 7.1);
                float grime = lerp(1.0, 0.55 + 0.55 * valueNoise(IN.uv * n * 6.0), _Grime);

                half3 lit = tint * (_Intensity * shade * wear * grime);
                half3 col = lerp(_GroutColor.rgb, lit, panel);

                col = MixFog(col, IN.fog);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
