// VHS tape pass — the menu's background treatment.
//
// Runs as a FullScreenPassRendererFeature on PC_Renderer at AfterRenderingPostProcessing, so it is
// the last thing to touch the rendered image. The menu UI is a Screen Space - Overlay canvas, which
// draws after all camera rendering, so the panels and type stay perfectly crisp on top of a
// thoroughly degraded background — the same split the reference footage has.
//
// This has to be a camera pass rather than a UI overlay: splitting channels and smearing chroma
// means re-sampling the image behind the effect, and a UI quad can only tint what is already there.
//
// _VhsIntensity 0 (the default) bypasses to the untouched image, so the always-present renderer
// feature is a no-op in levels; MenuVhsFx raises it while a menu is on screen.
Shader "Hidden/MazeEscape/MenuVhs"
{
    Properties
    {
        _VhsIntensity("Master Intensity", Range(0, 1)) = 0
        _ChromaSplit("Chroma Split (px)", Float) = 3
        _SmearLength("Chroma Smear (px)", Float) = 26
        _SmearStrength("Chroma Smear Strength", Range(0, 1)) = 0.7
        _JitterStrength("Line Jitter (px)", Float) = 2.5
        _WarpStrength("Tape Warp (px)", Float) = 9
        _ScanlineStrength("Scanline", Range(0, 1)) = 0.12
        _ScanlineCount("Scanline Count", Float) = 240
        _NoiseStrength("Tape Noise", Range(0, 1)) = 0.10
        _Desaturation("Desaturation", Range(0, 1)) = 0.22
        _VignetteStrength("Vignette", Range(0, 1)) = 0.30
        _HeadSwitch("Head Switch Band", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "MenuVhs"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _VhsIntensity;
            float _ChromaSplit;
            float _SmearLength;
            float _SmearStrength;
            float _JitterStrength;
            float _WarpStrength;
            float _ScanlineStrength;
            float _ScanlineCount;
            float _NoiseStrength;
            float _Desaturation;
            float _VignetteStrength;
            float _HeadSwitch;

            // Multiplies DOWN, not up. The obvious formulation (frac(p * 123.34)) throws away the
            // fraction once p gets large: with a time-derived salt growing every frame, the product
            // leaves float32's precise range within seconds and the hash decays to a constant — grain,
            // jitter and dropouts all quietly flatten the longer a menu sits open. Scaling by 0.1031
            // keeps the value small enough that frac() still has bits to work with.
            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            // Value noise along one axis — used for per-scanline offsets, which is why it is 1D:
            // real tape error is constant across a line and only changes between lines.
            // t is wrapped for the same precision reason as above.
            float LineNoise(float line_, float t)
            {
                float tw = fmod(t, 1024.0);
                float a = Hash21(float2(floor(line_), floor(tw)));
                float b = Hash21(float2(floor(line_), floor(tw) + 1.0));
                return lerp(a, b, smoothstep(0.0, 1.0, frac(tw))) * 2.0 - 1.0;
            }

            half3 SampleSrc(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, saturate(uv)).rgb;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                if (_VhsIntensity <= 0.001)
                    return half4(SampleSrc(uv), 1);

                float2 texel = _ScreenSize.zw;
                float t = _Time.y;
                float line_ = uv.y * _ScreenSize.y;

                // --- horizontal displacement: fine per-line jitter plus slow rolling tape warp
                float jitter = LineNoise(line_, t * 14.0) * _JitterStrength;
                float warp = sin(uv.y * 5.0 + t * 0.7) * sin(uv.y * 17.0 - t * 0.31) * _WarpStrength;

                // head-switching noise band that crawls up the frame, heavier displacement inside it
                float bandPos = frac(t * 0.11);
                float band = smoothstep(0.045, 0.0, abs(frac(uv.y - bandPos + 0.5) - 0.5));
                float bandShift = band * _HeadSwitch * 55.0 * (LineNoise(line_, t * 30.0) * 0.5 + 0.6);

                float shift = (jitter + warp + bandShift) * texel.x;
                float2 duv = float2(uv.x + shift, uv.y);

                // --- chroma split: luma stays put, colour carriers drift apart toward the edges
                float edgeBias = 1.0 + 2.4 * abs(uv.x - 0.5);
                float split = _ChromaSplit * edgeBias * texel.x;
                half3 base;
                base.r = SampleSrc(duv + float2(split, 0)).r;
                base.g = SampleSrc(duv).g;
                base.b = SampleSrc(duv - float2(split, 0)).b;

                // --- chroma smear: 8 trailing taps to the left, exponential falloff. Only the colour
                // is dragged; keeping luminance sharp is what makes it read as tape bleed and not blur.
                half3 trail = 0;
                float wsum = 0;
                [unroll]
                for (int i = 0; i < 8; i++)
                {
                    float k = i / 7.0;
                    float w = exp(-k * 2.6);
                    trail += SampleSrc(duv - float2(k * _SmearLength * texel.x, 0)) * w;
                    wsum += w;
                }
                trail /= max(wsum, 1e-4);

                half baseLum = Luminance(base);
                half trailLum = Luminance(trail);
                half3 trailChroma = trail - trailLum;
                half3 col = lerp(base, baseLum + trailChroma, _SmearStrength);

                // --- scanlines (and a faint interlace crawl)
                float scan = sin(uv.y * _ScanlineCount * 3.14159265) * 0.5 + 0.5;
                float interlace = frac(line_ * 0.5 + t * 12.0) < 0.5 ? 1.0 : 0.99;
                col *= (1.0 - _ScanlineStrength * scan) * interlace;

                // --- tape noise. Scaled by local brightness on purpose: flat additive grain reads
                // as fog over a scene this dark, lifting every black to grey. Quantized in time so it
                // shimmers at tape speed instead of strobing per frame.
                // frame index wraps so the hash input cannot run away (see Hash21)
                float frameIdx = fmod(floor(t * 24.0), 512.0);
                float2 grainCell = floor(uv * _ScreenSize.xy * 0.5) + frameIdx * 13.0;
                float grain = Hash21(grainCell) - 0.5;
                col += grain * _NoiseStrength * (0.03 + Luminance(col) * 1.4);

                // --- worn tape colour: desaturate, then bias the remaining chroma warm
                half lum = Luminance(col);
                col = lerp(col, lum.xxx, _Desaturation);
                col *= half3(1.03, 1.0, 0.95);

                // --- vignette
                float2 v = (uv - 0.5) * 2.0;
                float vig = 1.0 - saturate(dot(v, v) * 0.42) * _VignetteStrength;
                col *= vig;

                half3 src = SampleSrc(uv);
                col = lerp(src, col, _VhsIntensity);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
