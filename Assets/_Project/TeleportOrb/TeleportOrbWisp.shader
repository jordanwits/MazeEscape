// TeleportOrbWisp.shader
// URP-compatible additive particle shader for the orb's floating wisps. Procedurally
// generates a soft round dot from the particle quad UVs (no texture asset needed) and
// multiplies by the per-particle color so Color-over-Lifetime fades work. Visual only.
Shader "MazeEscape/TeleportOrbWisp"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (0.3, 0.6, 1.0, 1)
        _Softness ("Softness", Range(0.01, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Softness;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 d = IN.uv - 0.5;
                float r = saturate(length(d) * 2.0); // 0 at center -> 1 at edge
                float a = saturate(1.0 - r);
                a = pow(a, lerp(1.0, 4.0, 1.0 - _Softness));

                float3 col = _Color.rgb * IN.color.rgb;
                float alpha = a * _Color.a * IN.color.a;
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
