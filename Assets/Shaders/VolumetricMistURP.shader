Shader "Custom/VolumetricMist"
{
    Properties
    {
        _MistColor ("Mist Color", Color) = (0.8, 0.8, 0.85, 0.6)
        _Density ("Density", Range(0.1, 2.0)) = 0.8
        _AnimationSpeed ("Animation Speed", Range(0.0, 5.0)) = 1.2
        _NoiseScale ("Noise Scale", Range(0.5, 5.0)) = 2.0
        _FadeDistance ("Fade Distance", Range(0.1, 10.0)) = 3.0
        _FlowIntensity ("Flow Intensity", Range(0.0, 2.0)) = 0.8
        _Turbulence ("Turbulence", Range(0.0, 2.0)) = 1.0
        _SwirlyAmount ("Swirly Amount", Range(0.0, 1.0)) = 0.4
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
            Name "VolumetricMist"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _MistColor;
                half _Density;
                half _AnimationSpeed;
                half _NoiseScale;
                half _FadeDistance;
                half _FlowIntensity;
                half _Turbulence;
                half _SwirlyAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Simple Perlin-like noise function
            float noise(float3 p)
            {
                return frac(sin(dot(p, float3(12.9898, 78.233, 45.164))) * 43758.5453);
            }

            float perlinNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = noise(i + float3(0, 0, 0));
                float n100 = noise(i + float3(1, 0, 0));
                float n010 = noise(i + float3(0, 1, 0));
                float n110 = noise(i + float3(1, 1, 0));
                float n001 = noise(i + float3(0, 0, 1));
                float n101 = noise(i + float3(1, 0, 1));
                float n011 = noise(i + float3(0, 1, 1));
                float n111 = noise(i + float3(1, 1, 1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);

                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);

                return lerp(nxy0, nxy1, f.z);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionOS = IN.positionOS.xyz;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Normalize position to [-1, 1] range
                float3 pos = IN.positionOS;

                // ── Swirly vortex effect ──
                float angle = atan2(pos.z, pos.x);
                float radius = length(pos.xz);
                float swirl = sin(angle * 3.0 + _Time.y * _AnimationSpeed * 0.5) * _SwirlyAmount;
                pos.xz += float2(cos(angle), sin(angle)) * swirl * 0.3;

                // ── Multi-layered animated noise ──
                float noiseValue = 0.0;
                
                // Layer 1: Slow, large billows
                float3 samplePos1 = pos * _NoiseScale + float3(_Time.y * _AnimationSpeed * 0.3, _Time.y * _AnimationSpeed * 0.2, _Time.y * _AnimationSpeed * 0.15);
                noiseValue += 0.4 * perlinNoise(samplePos1);
                
                // Layer 2: Medium, flowing streams
                float3 samplePos2 = pos * _NoiseScale * 1.5 + float3(-_Time.y * _AnimationSpeed * 0.5, _Time.y * _AnimationSpeed * 0.6, _Time.y * _AnimationSpeed * 0.4);
                noiseValue += 0.35 * perlinNoise(samplePos2);
                
                // Layer 3: Fast, turbulent details
                float3 samplePos3 = pos * _NoiseScale * 3.0 + float3(_Time.y * _AnimationSpeed * 0.8, -_Time.y * _AnimationSpeed * 0.9, _Time.y * _AnimationSpeed * 0.7);
                noiseValue += 0.15 * perlinNoise(samplePos3);
                
                // Layer 4: Extra fast turbulence
                float3 samplePos4 = pos * _NoiseScale * 5.0 + float3(_Time.y * _AnimationSpeed * 1.2, _Time.y * _AnimationSpeed * 1.1, -_Time.y * _AnimationSpeed * 1.3);
                noiseValue += 0.1 * perlinNoise(samplePos4) * _Turbulence;

                noiseValue = saturate(noiseValue);

                // ── Directional flow (upward + swirly) ──
                float verticalBias = (pos.y + 0.5) * 0.5; // Bias toward top
                float flowEffect = sin((pos.y + _Time.y * _AnimationSpeed * _FlowIntensity) * 3.0) * 0.5 + 0.5;
                float movementMod = lerp(noiseValue, noiseValue * flowEffect, _FlowIntensity * 0.5);

                // ── Distance-based fade from center ──
                float distFromCenter = length(pos);
                float fade = smoothstep(_FadeDistance, 0.0, distFromCenter);

                // ── Combine effects with enhanced animation ──
                half density = _Density * movementMod * fade * (0.7 + verticalBias * 0.3);
                half alpha = density * _MistColor.a;

                return half4(_MistColor.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
