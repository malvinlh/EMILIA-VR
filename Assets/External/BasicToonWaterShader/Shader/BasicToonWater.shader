Shader "Custom/BasicToonWaterShader"
{
    Properties
    {
        _Color ("Water Color", Color) = (0.1, 0.4, 0.8, 0.8)
        _MainTex ("Water Texture", 2D) = "white" {}
        _WaveSpeed ("Wave Speed", Float) = 0.5
        _WaveStrength ("Wave Strength", Range(0, 0.1)) = 0.01
        _WaveAmount ("Wave Amount", Float) = 0.1
        _WaveFrequency ("Wave Frequency", Float) = 1
        _TextureDistortion ("Texture Distortion", Range(0, 1)) = 0.5
        _CartoonFactor ("Cartoon Factor", Range(0, 1)) = 0.5
        _ColorSteps ("Color Steps", Range(2, 10)) = 4
        _EdgeThreshold ("Edge Threshold", Range(0, 1)) = 0.2
        _EdgeColor ("Edge Color", Color) = (0, 0, 0, 1)
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineWidth ("Outline Width", Range(0, 0.1)) = 0.01
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 1)
        _FoamAmount ("Foam Amount", Range(0, 1)) = 0.1
        _FoamCutoff ("Foam Cutoff", Range(0, 1)) = 0.5
        _FoamSpeed ("Foam Speed", Float) = 0.1
        _FoamNoiseScale ("Foam Noise Scale", Float) = 20
        _FoamSmoothness ("Foam Smoothness", Range(0, 0.5)) = 0.1
        _FoamEdgeSize ("Foam Edge Size", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        // Outline Pass
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _EdgeColor;
                half4 _FoamColor;
                half4 _OutlineColor;
                float _WaveSpeed;
                float _WaveStrength;
                float _WaveAmount;
                float _WaveFrequency;
                float _TextureDistortion;
                float _CartoonFactor;
                float _ColorSteps;
                float _EdgeThreshold;
                float _OutlineWidth;
                float _FoamAmount;
                float _FoamCutoff;
                float _FoamSpeed;
                float _FoamNoiseScale;
                float _FoamSmoothness;
                float _FoamEdgeSize;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                // Transform normal to view space and project — matches original clip-space outline
                float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, input.normalOS));
                float2 offset = mul((float2x2)UNITY_MATRIX_P, normalVS.xy);
                output.positionHCS.xy += offset * _OutlineWidth * output.positionHCS.z;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _OutlineColor;
            }
            ENDHLSL
        }

        // Main Water Pass
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _EdgeColor;
                half4 _FoamColor;
                half4 _OutlineColor;
                float _WaveSpeed;
                float _WaveStrength;
                float _WaveAmount;
                float _WaveFrequency;
                float _TextureDistortion;
                float _CartoonFactor;
                float _ColorSteps;
                float _EdgeThreshold;
                float _OutlineWidth;
                float _FoamAmount;
                float _FoamCutoff;
                float _FoamSpeed;
                float _FoamNoiseScale;
                float _FoamSmoothness;
                float _FoamEdgeSize;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float3 viewDirWS : TEXCOORD4;
                float fogFactor : TEXCOORD5;
            };

            // Improved pseudo-random function
            float2 random2(float2 st)
            {
                st = float2(dot(st, float2(127.1, 311.7)),
                            dot(st, float2(269.5, 183.3)));
                return -1.0 + 2.0 * frac(sin(st) * 43758.5453123);
            }

            // Smooth noise function
            float noise(float2 st)
            {
                float2 i = floor(st);
                float2 f = frac(st);

                float2 u = f * f * (3.0 - 2.0 * f);

                return lerp(
                    lerp(dot(random2(i + float2(0.0, 0.0)), f - float2(0.0, 0.0)),
                         dot(random2(i + float2(1.0, 0.0)), f - float2(1.0, 0.0)), u.x),
                    lerp(dot(random2(i + float2(0.0, 1.0)), f - float2(0.0, 1.0)),
                         dot(random2(i + float2(1.0, 1.0)), f - float2(1.0, 1.0)), u.x), u.y);
            }

            // Smooth step function for foam
            float smootherstep(float edge0, float edge1, float x)
            {
                x = saturate((x - edge0) / (edge1 - edge0));
                return x * x * x * (x * (x * 6 - 15) + 10);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.screenPos = ComputeScreenPos(output.positionHCS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = normalize(_WorldSpaceCameraPos - posInputs.positionWS);
                output.fogFactor = ComputeFogFactor(output.positionHCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // Generate smoother wave movement
                float2 waveOffset = float2(
                    noise(uv * _WaveFrequency + _Time.y * _WaveSpeed),
                    noise(uv * _WaveFrequency * 1.2 + _Time.y * _WaveSpeed * 1.1)
                ) * _WaveAmount;

                // Apply distortion with control
                float2 distortedUV = uv + waveOffset * _WaveStrength * _TextureDistortion;

                // Use texture derivatives for better sampling
                half4 c = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, distortedUV, ddx(uv), ddy(uv));

                // Blend distorted texture with original
                c = lerp(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv), c, _TextureDistortion);

                // Apply cartoon edge effect
                float3 texNormal = UnpackNormal(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, distortedUV));
                float edge = 1 - saturate(dot(normalize(input.viewDirWS), texNormal));

                // Calculate foam
                float2 foamUV = input.positionWS.xz * _FoamNoiseScale + _Time.y * _FoamSpeed;
                float foamNoise = noise(foamUV);

                // Get scene depth (requires Depth Texture enabled on URP Asset)
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceEyeDepth = input.screenPos.w;
                float foamLine = 1 - saturate(_FoamAmount * (sceneEyeDepth - surfaceEyeDepth));

                // Create smooth foam transition
                float foamGradient = smootherstep(_FoamCutoff - _FoamSmoothness, _FoamCutoff + _FoamSmoothness, foamLine + foamNoise);
                float foam = foamGradient * _FoamEdgeSize;

                // Toon ramp lighting
                Light mainLight = GetMainLight();
                float NdotL = dot(normalize(input.normalWS), mainLight.direction);
                float h = NdotL * 0.5 + 0.5;
                float ramp = floor(h * _ColorSteps) / _ColorSteps;
                ramp = lerp(h, ramp, _CartoonFactor);
                half3 ambient = SampleSH(normalize(input.normalWS));
                half3 lighting = mainLight.color * ramp * mainLight.distanceAttenuation + ambient;

                // Apply edge color
                half3 finalColor;
                if (edge > _EdgeThreshold)
                {
                    finalColor = lerp(c.rgb * _Color.rgb, _EdgeColor.rgb, _CartoonFactor);
                }
                else
                {
                    finalColor = c.rgb * _Color.rgb;
                }

                // Blend in foam
                finalColor = lerp(finalColor, _FoamColor.rgb, foam);
                finalColor *= lighting;

                half alpha = c.a * _Color.a;

                // Apply fog
                finalColor = MixFog(finalColor, input.fogFactor);

                return half4(finalColor, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}