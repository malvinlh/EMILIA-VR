Shader "Custom/BasicWaterShader"
{
    Properties
    {
        _Color ("Background Color", Color) = (0.1, 0.4, 0.8, 0.8)
        _TextureColor ("Texture Color", Color) = (1, 1, 1, 1)
        _MainTex ("Water Texture", 2D) = "white" {}
        _WaveSpeed ("Wave Speed", Float) = 0.5
        _WaveStrength ("Wave Strength", Range(0, 0.1)) = 0.01
        _WaveAmount ("Wave Amount", Float) = 0.1
        _WaveFrequency ("Wave Frequency", Float) = 1
        _TextureDistortion ("Texture Distortion", Range(0, 1)) = 0.5
        _CartoonFactor ("Cartoon Factor", Range(0, 1)) = 0.5
        _TransparencySpeed ("Transparency Animation Speed", Float) = 1.0
        _TransparencyStrength ("Transparency Strength", Range(0, 1)) = 0.5
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 1)
        _FoamAmount ("Foam Amount", Range(0, 1)) = 0.1
        _FoamCutoff ("Foam Cutoff", Range(0, 1)) = 0.5
        _FoamSpeed ("Foam Speed", Float) = 0.1
        _FoamNoiseScale ("Foam Noise Scale", Float) = 20
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

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
                half4 _TextureColor;
                half4 _FoamColor;
                float _WaveSpeed;
                float _WaveStrength;
                float _WaveAmount;
                float _WaveFrequency;
                float _TextureDistortion;
                float _CartoonFactor;
                float _TransparencySpeed;
                float _TransparencyStrength;
                float _FoamAmount;
                float _FoamCutoff;
                float _FoamSpeed;
                float _FoamNoiseScale;
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
                float fogFactor : TEXCOORD4;
            };

            // Improved pseudo-random function
            float2 random2(float2 st)
            {
                st = float2(dot(st, float2(127.1, 311.7)),
                            dot(st, float2(269.5, 183.3)));
                return -1.0 + 2.0 * frac(sin(st) * 43758.5453123);
            }

            // Gradient noise function
            float gradientNoise(float2 st)
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

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.screenPos = ComputeScreenPos(output.positionHCS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(output.positionHCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // Generate smoother wave movement
                float2 waveOffset = float2(
                    gradientNoise(uv * _WaveFrequency + _Time.y * _WaveSpeed),
                    gradientNoise(uv * _WaveFrequency * 1.2 + _Time.y * _WaveSpeed * 1.1)
                ) * _WaveAmount;

                // Apply distortion with control
                float2 distortedUV = uv + waveOffset * _WaveStrength * _TextureDistortion;

                // Use texture derivatives for better sampling
                half4 c = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, distortedUV, ddx(uv), ddy(uv));

                // Blend distorted texture with original
                c = lerp(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv), c, _TextureDistortion);

                // Apply texture color
                c *= _TextureColor;

                // Pulsating transparency (only for texture)
                float transparencyPulse = (sin(_Time.y * _TransparencySpeed) + 1) * 0.5;
                float textureTransparency = lerp(1, transparencyPulse, _TransparencyStrength);

                // Calculate foam
                float2 foamUV = input.positionWS.xz * _FoamNoiseScale + _Time.y * _FoamSpeed;
                float foamNoise = gradientNoise(foamUV);

                // Get scene depth (requires Depth Texture enabled on URP Asset)
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceEyeDepth = input.screenPos.w;
                float foamLine = 1 - saturate(_FoamAmount * (sceneEyeDepth - surfaceEyeDepth));

                // Combine foam noise with foam line
                float foam = saturate(foamNoise + foamLine);
                foam = smoothstep(_FoamCutoff, 1, foam);

                // Lambert lighting
                Light mainLight = GetMainLight();
                float NdotL = saturate(dot(normalize(input.normalWS), mainLight.direction));
                half3 ambient = SampleSH(normalize(input.normalWS));
                half3 lighting = mainLight.color * NdotL * mainLight.distanceAttenuation + ambient;

                // Blend colors, apply transparency and foam
                half3 finalColor = lerp(_Color.rgb, c.rgb, c.a * textureTransparency);
                finalColor = lerp(finalColor, _FoamColor.rgb, foam);
                finalColor *= lighting;

                half alpha = lerp(_Color.a, c.a * _TextureColor.a, c.a * textureTransparency);

                // Apply fog
                finalColor = MixFog(finalColor, input.fogFactor);

                return half4(finalColor, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}