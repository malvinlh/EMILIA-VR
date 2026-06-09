Shader "EMILIA/ToonWater"
{
    Properties
    {
        [Header(Colors)]
        _ShallowColor ("Shallow Color", Color) = (0.45, 0.78, 0.88, 0.85)
        _DeepColor ("Deep Color", Color) = (0.18, 0.38, 0.58, 0.85)
        _ColorGradientScale ("Color Gradient Scale", Range(0, 2)) = 1.0
        _ColorGradientOffset ("Color Gradient Offset", Range(-1, 1)) = 0.0

        [Header(Toon Lighting)]
        _ShadowColor ("Shadow Color", Color) = (0.7, 0.75, 0.8, 1)
        _ShadowStep ("Shadow Step", Range(0, 1)) = 0.45
        _ShadowFeather ("Shadow Feather", Range(0.001, 0.5)) = 0.08

        [Header(Specular)]
        _WaterSpecColor ("Specular Color", Color) = (1, 0.95, 0.9, 1)
        _SpecPower ("Specular Power", Range(1, 128)) = 32
        _SpecThreshold ("Specular Threshold", Range(0, 1)) = 0.6

        [Header(Foam)]
        _FoamTex ("Foam Texture", 2D) = "white" {}
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 0.5)
        _FoamSpeed ("Foam Scroll Speed", Range(0, 0.5)) = 0.03
        _FoamScale ("Foam UV Scale", Range(0.1, 5)) = 1.0
        _FoamIntensity ("Foam Intensity", Range(0, 1)) = 0.3

        [Header(Shoreline Foam)]
        _ShorelineFoamWidth ("Shoreline Foam Width", Range(0, 5)) = 1.0
        _ShorelineFoamColor ("Shoreline Foam Color", Color) = (1, 1, 1, 0.8)

        [Header(Waves)]
        _WaveAmplitude ("Wave Amplitude", Range(0, 0.2)) = 0.015
        _WaveFrequency ("Wave Frequency", Range(0.5, 10)) = 2.0
        _WaveSpeed ("Wave Speed", Range(0, 5)) = 0.4

        [Header(Fresnel Reflection)]
        _ReflectionColor ("Reflection Color", Color) = (0.7, 0.85, 1.0, 1)
        _ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.25
        _FresnelPower ("Fresnel Power", Range(1, 10)) = 3.0

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        // =====================================================
        // FORWARD PASS (Toon-lit Transparent Water)
        // =====================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;
                half4  _DeepColor;
                half   _ColorGradientScale;
                half   _ColorGradientOffset;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half4  _WaterSpecColor;
                half   _SpecPower;
                half   _SpecThreshold;
                float4 _FoamTex_ST;
                half4  _FoamColor;
                half   _FoamSpeed;
                half   _FoamScale;
                half   _FoamIntensity;
                half   _ShorelineFoamWidth;
                half4  _ShorelineFoamColor;
                half   _WaveAmplitude;
                half   _WaveFrequency;
                half   _WaveSpeed;
                half4  _ReflectionColor;
                half   _ReflectionStrength;
                half   _FresnelPower;
            CBUFFER_END

            TEXTURE2D(_FoamTex); SAMPLER(sampler_FoamTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half   fogFactor   : TEXCOORD1;
                float2 worldXZ     : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
                float3 normalWS    : TEXCOORD4;
                float4 screenPos   : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WaterVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posOS = IN.positionOS.xyz;

                // World position for wave calculation
                float3 posWS = TransformObjectToWorld(posOS);

                // Dual sine wave displacement
                float waveInput = posWS.x * _WaveFrequency + posWS.z * _WaveFrequency * 0.7
                                + _Time.y * _WaveSpeed;
                float wave  = sin(waveInput) * _WaveAmplitude;
                float wave2 = sin(waveInput * 1.3 + 2.1) * _WaveAmplitude * 0.5;
                posOS.y += wave + wave2;

                // Reconstruct normal from wave derivatives
                float dWave = cos(waveInput) * _WaveAmplitude * _WaveFrequency;
                float dWave2 = cos(waveInput * 1.3 + 2.1) * _WaveAmplitude * 0.5 * _WaveFrequency * 1.3;
                float dTotal = dWave + dWave2;
                // Approximate normal from wave slope
                float3 waveNormalOS = normalize(float3(-dTotal, 1.0, -dTotal * 0.7));

                OUT.positionCS = TransformObjectToHClip(posOS);
                OUT.positionWS = TransformObjectToWorld(posOS);
                OUT.normalWS   = TransformObjectToWorldNormal(waveNormalOS);
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                OUT.worldXZ = posWS.xz;
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);

                return OUT;
            }

            half4 WaterFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float3 normalWS = normalize(IN.normalWS);

                // Two-color gradient based on UV.y
                float gradientT = saturate(IN.uv.y * _ColorGradientScale + _ColorGradientOffset);
                half4 waterColor = lerp(_DeepColor, _ShallowColor, gradientT);

                // --- Toon lighting ---
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                Light mainLight = GetMainLight(shadowCoord);
                float NdotL = dot(normalWS, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;
                float toonRamp = smoothstep(
                    _ShadowStep - _ShadowFeather,
                    _ShadowStep + _ShadowFeather,
                    halfLambert);
                toonRamp = min(toonRamp, smoothstep(0.0, _ShadowFeather * 2.0, mainLight.shadowAttenuation));

                half3 litWater    = waterColor.rgb * mainLight.color;
                half3 shadowWater = waterColor.rgb * _ShadowColor.rgb;
                waterColor.rgb = lerp(shadowWater, litWater, toonRamp);

                // --- Toon specular highlight ---
                float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                float3 halfDir = normalize(viewDir + mainLight.direction);
                float NdotH = saturate(dot(normalWS, halfDir));
                float specular = pow(NdotH, _SpecPower);
                // Hard-step toon specular
                float toonSpec = smoothstep(_SpecThreshold - 0.05, _SpecThreshold + 0.05, specular);
                waterColor.rgb += _WaterSpecColor.rgb * toonSpec * toonRamp * 0.5;

                // --- Additional lights (point/spot reflections on water) ---
                #if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS)
                {
                    InputData inputData = (InputData)0;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                    inputData.positionWS = IN.positionWS;

                    uint lightsCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightsCount)
                        Light light = GetAdditionalLight(lightIndex, IN.positionWS);
                        half atten = light.distanceAttenuation * light.shadowAttenuation;

                        // Toon diffuse contribution
                        half addNdotL = dot(normalWS, light.direction) * 0.5 + 0.5;
                        half addRamp = smoothstep(_ShadowStep - _ShadowFeather, _ShadowStep + _ShadowFeather, addNdotL);
                        waterColor.rgb += waterColor.rgb * light.color * addRamp * atten * 0.5;

                        // Toon specular from additional lights
                        float3 addHalf = normalize(viewDir + light.direction);
                        half addSpec = pow(saturate(dot(normalWS, addHalf)), _SpecPower);
                        half addToonSpec = smoothstep(_SpecThreshold - 0.05, _SpecThreshold + 0.05, addSpec);
                        waterColor.rgb += _WaterSpecColor.rgb * addToonSpec * atten * 0.35;
                    LIGHT_LOOP_END
                }
                #endif

                // --- Scrolling foam overlay ---
                float2 foamUV = IN.worldXZ * _FoamScale
                    + float2(_Time.y * _FoamSpeed, _Time.y * _FoamSpeed * 0.7);
                half4 foam = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, foamUV);
                waterColor.rgb += foam.rgb * _FoamColor.rgb * _FoamIntensity * foam.a;

                // --- Depth-based shoreline foam ---
                #if defined(_RECEIVE_SHADOWS_OFF)
                // Skip depth if shadows off (likely no depth texture)
                #else
                {
                    float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                    float sceneDepthRaw = SampleSceneDepth(screenUV);
                    float sceneDepthEye = LinearEyeDepth(sceneDepthRaw, _ZBufferParams);
                    float fragDepthEye  = LinearEyeDepth(IN.positionWS, UNITY_MATRIX_V);
                    float depthDiff = sceneDepthEye - fragDepthEye;

                    float shorelineMask = 1.0 - saturate(depthDiff / max(_ShorelineFoamWidth, 0.01));
                    shorelineMask = smoothstep(0.0, 0.8, shorelineMask);
                    waterColor.rgb += _ShorelineFoamColor.rgb * shorelineMask * _ShorelineFoamColor.a;
                    // Slightly increase alpha near shore for visibility
                    waterColor.a = saturate(waterColor.a + shorelineMask * 0.15);
                }
                #endif

                // --- Fresnel reflection ---
                {
                    float NdotV = saturate(dot(normalWS, viewDir));
                    float fresnel = pow(1.0 - NdotV, _FresnelPower);
                    waterColor.rgb += _ReflectionColor.rgb * fresnel * _ReflectionStrength * toonRamp;
                }

                // Apply fog
                waterColor.rgb = MixFog(waterColor.rgb, IN.fogFactor);

                return waterColor;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
