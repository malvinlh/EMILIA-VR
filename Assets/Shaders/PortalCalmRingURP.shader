Shader "EMILIA/PortalCalmRingURP"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.42, 0.76, 0.86, 1.0)
        _AccentColor ("Accent Color", Color) = (0.82, 0.94, 0.98, 1.0)
        _InnerRadius ("Inner Radius", Range(0.0, 1.0)) = 0.62
        _OuterRadius ("Outer Radius", Range(0.0, 1.0)) = 0.95
        _Feather ("Feather", Range(0.001, 0.2)) = 0.03
        _Intensity ("Intensity", Range(0.0, 3.0)) = 0.7
        _SpinSpeed ("Spin Speed", Range(0.0, 8.0)) = 0.8
        _ArcCount ("Arc Count", Range(2.0, 24.0)) = 10.0
        _SparkleStrength ("Sparkle Strength", Range(0.0, 2.0)) = 0.35
        _SoftNoise ("Soft Noise", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "PortalRing"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _AccentColor;
                half _InnerRadius;
                half _OuterRadius;
                half _Feather;
                half _Intensity;
                half _SpinSpeed;
                half _ArcCount;
                half _SparkleStrength;
                half _SoftNoise;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 centered = input.uv * 2.0 - 1.0;
                float radius = length(centered);

                float innerMask = smoothstep(_InnerRadius - _Feather, _InnerRadius + _Feather, radius);
                float outerMask = 1.0 - smoothstep(_OuterRadius - _Feather, _OuterRadius + _Feather, radius);
                float ringMask = saturate(innerMask * outerMask);

                float angle = atan2(centered.y, centered.x);
                float t = _Time.y * _SpinSpeed;

                float arcWave = sin(angle * _ArcCount - t + radius * 22.0);
                float arcMask = pow(saturate(arcWave * 0.5 + 0.5), 2.6);

                float secondaryWave = sin(angle * (_ArcCount * 0.62) + t * 0.35 - radius * 15.0);
                float softBand = saturate(secondaryWave * 0.5 + 0.5);

                float2 sparkCell = floor(centered * 38.0 + float2(t * 0.25, -t * 0.2));
                float sparkle = step(0.988, Hash21(sparkCell)) * _SparkleStrength;
                sparkle *= smoothstep(_InnerRadius + 0.02, _OuterRadius - 0.02, radius);

                float noisePhase = sin((centered.x + centered.y) * 11.0 + t * 0.22);
                float comfortNoise = lerp(1.0, saturate(0.86 + 0.14 * noisePhase), _SoftNoise);

                float glow = (0.22 + 0.65 * arcMask + 0.22 * softBand + sparkle) * comfortNoise;
                float alpha = ringMask * glow * _Intensity;

                float3 color = lerp(_BaseColor.rgb, _AccentColor.rgb, saturate(0.25 + arcMask * 0.75));
                color += sparkle * 0.85;

                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
