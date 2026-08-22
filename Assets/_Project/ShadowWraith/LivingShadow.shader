Shader "MazeEscape/LivingShadow"
{
    // Living-shadow body for the Severance wraith. Fully procedural -- no textures.
    //
    // The mesh carries three UV sets authored in Blender (Blender/ShadowWraith.blend):
    //   TEXCOORD0  UVMap      cylindrical unwrap, unused here but kept for future art
    //   TEXCOORD1  WispFlow   x = wispiness (0 solid core .. 1 strand tip)
    //                         y = flow coord (0 crown .. 1 tips, follows arms/strands)
    //   TEXCOORD2  PhaseOpa   x = per-strand sway phase, y = core opacity
    // Data rides in UV channels rather than vertex colours on purpose: UVs are never
    // gamma-converted on FBX import, vertex colours can be.
    //
    // LOOK TARGET: murky, gooey, stringy. NOT cloudy, and NOT generally see-through.
    // Three things get us there, and all three matter:
    //   1. RIDGED noise (fold + sharpen) instead of plain fbm. Smooth value noise makes round
    //      soft blobs, which is literally how you render clouds. Folding it about its midpoint
    //      turns the smooth hills into sharp filament ridges.
    //   2. The sample coordinate is SQUASHED IN Y (_StringStretch) before lookup, so features
    //      elongate vertically into strands and drips rather than staying isotropic puffs.
    //   3. A near-BINARY alpha cut (_HoleSharp ~0.05). The body is fully opaque; alpha only
    //      drops where the goo density falls under the cut, which opens discrete crisp-edged
    //      holes. A wide soft cut is what produced the old translucent haze.
    // Cull Off means you see the shell's far side through those holes, which reads as volume.
    //
    // Noise is sampled in OBJECT space so the churn is glued to his skin and travels with him
    // instead of sliding through him. Scales are "cycles per object metre" and he is 2.2 m tall
    // -- keep them single-digit to low-teens or the pattern falls under a pixel and aliases.
    Properties
    {
        [Header(Colour)]
        _CoreColor        ("Core colour", Color)                           = (0.004, 0.004, 0.008, 1)
        _EdgeColor        ("Gap / recess colour", Color)                   = (0.105, 0.105, 0.140, 1)
        _Opacity          ("Overall opacity", Range(0,1))                  = 1.0
        _OpacityFalloff   ("Strand opacity falloff", Range(0,1))           = 0.15

        [Header(Gooey skin)]
        _ChurnScale       ("Goo scale (cycles per object metre)", Float)   = 6.0
        _StringStretch    ("String stretch (lower = longer strings)", Range(0.05,1)) = 0.26
        _ChurnSpeed       ("Churn speed", Float)                           = 0.55
        _DetailScale      ("Detail scale", Float)                          = 17.0
        _DetailSpeed      ("Detail speed", Float)                          = 1.10
        _FlowSpeed        ("Downward flow speed", Float)                   = 0.45
        _Contrast         ("Goo contrast", Range(0,3))                     = 1.15

        [Header(Holes and silhouette)]
        _HoleBias         ("Hole threshold", Range(0,0.6))                 = 0.16
        _HoleSharp        ("Hole edge sharpness (low = crisp)", Range(0.005,0.35)) = 0.055
        _ErodeGain        ("Silhouette erosion gain", Range(0,1.5))        = 0.78
        _RimPower         ("Edge falloff", Range(0.4,8))                   = 3.0
        _RimErode         ("Edge erosion", Range(0,1))                     = 0.85
        _WispErode        ("Strand erosion", Range(0,1))                   = 0.55
        _EdgeLift         ("Edge lift", Range(0,1))                        = 0.35
        _FringeScale      ("Edge fringe scale", Float)                     = 46.0

        [Header(Wet sheen)]
        _Sheen            ("Wet sheen strength", Range(0,4))               = 1.10
        _Gloss            ("Sheen tightness", Range(4,128))                = 42.0
        _SheenColor       ("Sheen colour", Color)                          = (0.30, 0.32, 0.42, 1)
        _BumpStrength     ("Surface relief", Range(0,2))                   = 0.55

        [Header(Motion)]
        _SwayAmount       ("Strand sway (m)", Float)                       = 0.075
        _SwaySpeed        ("Sway speed", Float)                            = 1.45
        _WritheAmount     ("Surface writhe (m)", Float)                    = 0.020

        [Header(Light reaction)]
        _LightSensitivity ("Light sensitivity", Range(0,4))                = 1.60
        _LightBite        ("Light edge bite", Range(0,2))                  = 0.55
        _LightThin        ("Light see-through", Range(0,1))                = 0.05
        _LightErode       ("Light erosion (script driven)", Range(0,1))    = 0.0
        _Agitation        ("Agitation (script driven)", Range(0,1))        = 0.0

        _SoftDepth        ("Soft intersection (m)", Float)                 = 0.22
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
            Name "LivingShadow"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CoreColor;
                float4 _EdgeColor;
                float4 _SheenColor;
                float  _Opacity;
                float  _OpacityFalloff;
                float  _ChurnScale;
                float  _StringStretch;
                float  _ChurnSpeed;
                float  _DetailScale;
                float  _DetailSpeed;
                float  _FlowSpeed;
                float  _Contrast;
                float  _HoleBias;
                float  _HoleSharp;
                float  _ErodeGain;
                float  _RimPower;
                float  _RimErode;
                float  _WispErode;
                float  _EdgeLift;
                float  _FringeScale;
                float  _Sheen;
                float  _Gloss;
                float  _BumpStrength;
                float  _SwayAmount;
                float  _SwaySpeed;
                float  _WritheAmount;
                float  _LightSensitivity;
                float  _LightBite;
                float  _LightThin;
                float  _LightErode;
                float  _Agitation;
                float  _SoftDepth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 wispFlow   : TEXCOORD1;
                float2 phaseOpa   : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 positionOS  : TEXCOORD2;  // xyz undisplaced, w = view depth
                float4 data        : TEXCOORD3;  // xy = wisp/flow, zw = phase/opacity
                float  fogFactor   : TEXCOORD4;
            };

            // ---- cheap hash value noise, no texture dependency ------------------
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
                float n000 = hash31(i + float3(0, 0, 0)), n100 = hash31(i + float3(1, 0, 0));
                float n010 = hash31(i + float3(0, 1, 0)), n110 = hash31(i + float3(1, 1, 0));
                float n001 = hash31(i + float3(0, 0, 1)), n101 = hash31(i + float3(1, 0, 1));
                float n011 = hash31(i + float3(0, 1, 1)), n111 = hash31(i + float3(1, 1, 1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }

            // Fold the noise about its midpoint, then sharpen. Smooth noise gives round soft
            // blobs (clouds); folding turns every smooth hill into a sharp crease, and the
            // creases chain together into filaments. This is the whole difference between
            // "cloud" and "sinew".
            float ridged(float3 p)
            {
                float n = vnoise(p);
                n = 1.0 - abs(n * 2.0 - 1.0);
                return pow(saturate(n), 1.5);
            }

            float fbm3(float3 p)
            {
                float amp = 0.5, sum = 0.0, norm = 0.0;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    sum  += amp * vnoise(p);
                    norm += amp;
                    p    *= 2.03;
                    amp  *= 0.5;
                }
                return sum / max(norm, 1e-4);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                float wisp  = IN.wispFlow.x;
                float flow  = IN.wispFlow.y;
                float phase = IN.phaseOpa.x;
                float t     = _Time.y;
                float ph    = phase * 6.2831853;
                float agi   = 1.0 + _Agitation;

                float3 posOS = IN.positionOS.xyz;
                OUT.positionOS = float4(posOS, 0.0);       // xyz captured BEFORE displacement

                // strands sway laterally; amplitude follows wispiness squared so the core holds still
                float  w2   = wisp * wisp;
                float3 dir  = float3(sin(ph * 1.7), 0.0, cos(ph * 1.3));
                float3 disp = dir * (sin(t * _SwaySpeed * agi + ph + flow * 3.1) * _SwayAmount * w2 * agi);
                disp.y += cos(t * _SwaySpeed * 0.83 * agi + ph * 1.9) * _SwayAmount * 0.35 * w2;

                // the whole surface creeps along its own normal -- this is the "skin is alive" part
                float writhe = vnoise(posOS * 6.0 + float3(0.0, -t * 0.7, t * 0.35)) - 0.5;
                disp += IN.normalOS * (writhe * _WritheAmount * (0.35 + wisp) * agi);

                float3 displaced = posOS + disp;
                OUT.positionWS  = TransformObjectToWorld(displaced);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                // Carry true view depth ourselves. SV_POSITION.w in a fragment shader is 1/w,
                // not view depth, so reading it back for the soft-intersection test is wrong.
                OUT.positionOS.w = -TransformWorldToView(OUT.positionWS).z;
                OUT.data        = float4(wisp, flow, phase, IN.phaseOpa.y);
                OUT.fogFactor   = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            // Incident light plus a specular lobe for the wet look. The PC renderer is Forward+
            // (PC_Renderer m_RenderingMode: 2), so additional lights do NOT arrive through a plain
            // GetAdditionalLightsCount loop -- it needs LIGHT_LOOP_BEGIN/END, and that macro
            // expands to code referencing a local named exactly "inputData". The declaration
            // below is load-bearing, not decoration.
            void GatherLight(float3 positionWS, float3 N, float3 V, float4 positionHCS,
                             out float lit, out float spec)
            {
                lit = 0.0; spec = 0.0;

                Light mainLight = GetMainLight();
                float lum = Luminance(mainLight.color) * mainLight.distanceAttenuation;
                lit  += lum * saturate(0.35 + 0.65 * saturate(dot(N, mainLight.direction)));
                spec += lum * pow(saturate(dot(N, normalize(V + mainLight.direction))), _Gloss);

                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionHCS);

                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light l = GetAdditionalLight(lightIndex, positionWS);
                        float ll = Luminance(l.color) * l.distanceAttenuation;
                        lit  += ll * saturate(0.35 + 0.65 * saturate(dot(N, l.direction)));
                        spec += ll * pow(saturate(dot(N, normalize(V + l.direction))), _Gloss);
                    LIGHT_LOOP_END
                #endif
            }

            float4 frag(Varyings IN, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float wisp    = IN.data.x;
                float flow    = IN.data.y;
                float coreOpa = IN.data.w;
                float t       = _Time.y;
                float agi     = 1.0 + _Agitation * 1.5;

                float3 N = normalize(IN.normalWS);
                N *= IS_FRONT_VFACE(facing, 1.0, -1.0);      // Cull Off: back faces need a flipped normal
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // ---- gooey strings: ridged noise on a Y-squashed coordinate ------
                float3 gp = IN.positionOS.xyz;
                gp.y *= _StringStretch;                       // squash Y -> features stretch vertically

                float3 p1 = gp * _ChurnScale
                          + float3(0.0, -t * _FlowSpeed, 0.0)
                          + t * _ChurnSpeed * float3(0.13, 0.0, 0.21);
                float goo1 = ridged(p1);
                float goo  = goo1 * 0.62 + ridged(p1 * 2.7 + 11.3) * 0.38;

                // Perturb the shading normal by the field's gradient. He is jet black, so the
                // specular lobe is the ONLY channel that can show surface relief -- without this
                // even real geometric displacement barely registers and the limbs read as smooth
                // tubes. Three extra noise taps, cheap at this resolution.
                {
                    const float e = 0.30;
                    float3 gr = float3(ridged(p1 + float3(e, 0, 0)) - goo1,
                                       ridged(p1 + float3(0, e, 0)) - goo1,
                                       ridged(p1 + float3(0, 0, e)) - goo1) / e;
                    gr -= N * dot(gr, N);                 // keep the perturbation tangential
                    N = normalize(N - gr * _BumpStrength);
                }

                // low-frequency murk: thick and thin regions so it is not uniformly stringy
                float murk = fbm3(gp * _ChurnScale * 0.34 + float3(0.0, -t * _FlowSpeed * 0.5, 0.0));

                float3 p2 = gp * _DetailScale
                          + float3(0.0, -t * _FlowSpeed * 2.1, 0.0)
                          + t * _DetailSpeed * float3(0.31, 0.0, -0.17);
                float detail = ridged(p2);

                // density: 1 = thick goo, 0 = a gap between strings
                float density = saturate((goo * 0.70 + murk * 0.42 + detail * 0.18 - 0.15)
                                         * _Contrast * agi + 0.12);

                // ---- silhouette erosion ------------------------------------------
                float3 pf = gp * _FringeScale
                          + float3(0.0, -t * _FlowSpeed * 1.4, 0.0)
                          + t * _ChurnSpeed * float3(0.07, 0.0, -0.11);
                float fringe = ridged(pf);

                float rim = pow(1.0 - saturate(dot(N, V)), _RimPower);

                float lit, spec;
                GatherLight(IN.positionWS, N, V, IN.positionHCS, lit, spec);
                float lightAmt = saturate(lit * _LightSensitivity);

                float erode = saturate(rim  * _RimErode
                                     + wisp * _WispErode
                                     + lightAmt * _LightBite
                                     + _LightErode);

                // fine fringe takes over from the goo field as we approach the silhouette
                float cutField = lerp(density, min(density, fringe), saturate(rim * 1.3));

                // Near-binary cut. alpha is exactly 1 wherever the goo is thicker than the
                // threshold, so the body is genuinely OPAQUE -- the only way to see through him
                // is a hole where the strings pull apart. A wide soft cut here is what made the
                // old version a translucent cloud.
                float cut   = _HoleBias + erode * _ErodeGain;
                float alpha = smoothstep(cut - _HoleSharp, cut + _HoleSharp, cutField);

                alpha *= lerp(1.0, coreOpa, _OpacityFalloff) * _Opacity;
                alpha *= saturate(1.0 - lightAmt * _LightThin);

                // ---- colour: near black, with a wet highlight riding the strings --
                float3 col = lerp(_CoreColor.rgb, _EdgeColor.rgb,
                                  saturate((1.0 - density) * 0.5 + rim * _EdgeLift + flow * 0.08));
                // sheen keyed to density so highlights sit on the raised goo, not in the gaps
                col += _SheenColor.rgb * (spec * _Sheen * saturate(0.25 + density));

                // soft intersection so strands sink into the floor instead of slicing it
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                float  sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float  fragEye  = IN.positionOS.w;
                alpha *= saturate((sceneEye - fragEye) / max(_SoftDepth, 1e-3));

                // Fade OUT with distance instead of washing toward the level fog colour.
                // The keyword guard is essential: with no fog keyword active ComputeFogFactor
                // returns 0, so an unguarded multiply drives alpha to 0 and the body vanishes
                // completely on any camera or level that has fog switched off.
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
