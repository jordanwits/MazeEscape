// Lethal-Company-style stylization pass (see Acerola, "The Strange Graphics Of LETHAL COMPANY").
// Runs as a FullScreenPassRendererFeature on PC_Renderer at BeforeRenderingPostProcessing, so bloom /
// tonemapping / grading are applied AFTER this — same order as Lethal Company's custom pass.
//
// Two effects in one blit, parameterised per level by MazePostFx (Resources/PostFx/PosterizeEdge.mat):
//   1. Posterization — quantizes LUMINANCE only, so colour gradients survive while lighting collapses
//      into hard bands. Lethal Company gets this by re-compositing its quantized volumetric buffer;
//      URP has no volumetrics, so banding the lit image's brightness is the equivalent trick.
//   2. Double edge detection — Roberts cross on linear eye depth (silhouettes) and on luminance
//      (texture/lighting boundaries), both fading out by _EdgeFadeDistance because Lethal Company
//      only draws edges near the camera.
//
// _Intensity 0 (the default) bypasses to the untouched image — the renderer feature is always in the
// PC renderer, so the menu stays clean; MazePostFx raises the intensity while a level is active.
Shader "Hidden/MazeEscape/PosterizeEdge"
{
    Properties
    {
        _Intensity("Master Intensity", Range(0, 1)) = 0
        _PosterizeSteps("Posterize Steps", Float) = 8
        _PosterizeBlend("Posterize Blend", Range(0, 1)) = 0.55
        _DepthEdgeThreshold("Depth Edge Threshold (relative)", Float) = 0.08
        _LumEdgeThreshold("Luminance Edge Threshold", Float) = 0.35
        _EdgeFadeDistance("Edge Fade Distance (m)", Float) = 12
        _EdgeIntensity("Edge Intensity", Range(0, 1)) = 0.45
        _EdgeColor("Edge Color", Color) = (0.02, 0.02, 0.03, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "PosterizeEdge"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _Intensity;
            float _PosterizeSteps;
            float _PosterizeBlend;
            float _DepthEdgeThreshold;
            float _LumEdgeThreshold;
            float _EdgeFadeDistance;
            float _EdgeIntensity;
            half4 _EdgeColor;

            float EyeDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                half3 src = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv).rgb;
                if (_Intensity <= 0.001)
                    return half4(src, 1);

                // --- edges: Roberts cross over the 4 diagonal neighbours, shared by both detectors
                float2 o = _ScreenSize.zw; // 1/width, 1/height of the (render-scaled) target

                half3 cTL = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2(-o.x,  o.y)).rgb;
                half3 cBR = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2( o.x, -o.y)).rgb;
                half3 cTR = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2( o.x,  o.y)).rgb;
                half3 cBL = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv + float2(-o.x, -o.y)).rgb;

                float dC  = EyeDepth(uv);
                float dTL = EyeDepth(uv + float2(-o.x,  o.y));
                float dBR = EyeDepth(uv + float2( o.x, -o.y));
                float dTR = EyeDepth(uv + float2( o.x,  o.y));
                float dBL = EyeDepth(uv + float2(-o.x, -o.y));

                // Depth differences relative to the centre depth, so a 5 cm step counts up close
                // but the same step far away doesn't outline everything.
                float depthEdge = (abs(dTL - dBR) + abs(dTR - dBL)) / max(dC, 0.05);

                // Luminance contrast on tonally clamped colour (HDR brights would otherwise
                // out-shout every threshold).
                half lumEdgeVal = abs(Luminance(saturate(cTL)) - Luminance(saturate(cBR)))
                                + abs(Luminance(saturate(cTR)) - Luminance(saturate(cBL)));

                float fade = saturate(1.0 - dC / max(_EdgeFadeDistance, 0.1));
                float edge = max(step(_DepthEdgeThreshold, depthEdge), step(_LumEdgeThreshold, lumEdgeVal))
                           * fade * _EdgeIntensity;

                // --- posterize: quantize brightness, keep chroma
                // Quantize in perceptual (gamma-2) space: linear-space steps put nearly every band in
                // the highlights, so a dark game collapses into its bottom band (crushed blacks) and
                // mid-dark textures flicker across one boundary (floor speckle). sqrt spreads the
                // bands toward the dark end where this game actually lives.
                half lum = Luminance(src);
                half lumPerceptual = sqrt(lum);
                half quantized = round(lumPerceptual * _PosterizeSteps) / max(_PosterizeSteps, 1.0);
                quantized *= quantized; // back to linear
                half scale = min(quantized / max(lum, 1e-3), 8.0); // cap so near-black pixels can't explode
                half3 poster = src * scale;
                half3 col = lerp(src, poster, _PosterizeBlend);

                col = lerp(col, _EdgeColor.rgb, edge);
                col = lerp(src, col, _Intensity);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
