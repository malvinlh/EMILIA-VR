Shader "MiSide/Environment"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color Tint", Color) = (1,1,1,1)

        [Header(Toon Shading)]
        _ShadowColor ("Shadow Color", Color) = (0.85, 0.75, 0.72, 1)
        _ShadowStep ("Shadow Step", Range(0, 1)) = 0.5
        _ShadowFeather ("Shadow Feather", Range(0.001, 0.5)) = 0.10

        [Header(Translucency)]
        [Toggle(_TRANSLUCENCY)] _TranslucencyToggle ("Enable Translucency", Float) = 0
        _TranslucencyColor ("Translucency Color", Color) = (0.85, 0.95, 0.75, 1)
        _TranslucencyPower ("Translucency Power", Range(1, 10)) = 4
        _TranslucencyStrength ("Translucency Strength", Range(0, 1)) = 0.3

        [Header(Normal Map)]
        [Toggle(_NORMALMAP)] _NormalMapToggle ("Enable Normal Map", Float) = 0
        [NoScaleOffset] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1.0

        [Header(Rim Light)]
        [Toggle(_RIMLIGHT)] _RimLightToggle ("Enable Rim Light", Float) = 0
        _RimColor ("Rim Color", Color) = (1, 0.9, 0.85, 1)
        _RimPower ("Rim Power", Range(1, 10)) = 4
        _RimIntensity ("Rim Intensity", Range(0, 1)) = 0.15

        [Header(Emission)]
        [Toggle(_EMISSION)] _EmissionToggle ("Enable Emission", Float) = 0
        _EmissionMap ("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (1,1,1,0)

        [Header(Glass Reflection)]
        [Toggle(_GLASS)] _GlassToggle ("Enable Glass", Float) = 0
        _GlassColor ("Glass Tint", Color) = (0.85, 0.92, 1.0, 0.15)
        _GlassReflectivity ("Reflectivity", Range(0, 1)) = 0.4
        _GlassSmoothness ("Smoothness", Range(0, 1)) = 0.85
        _GlassFresnelPower ("Fresnel Power", Range(1, 10)) = 3.0

        [Header(Alpha Cutout)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2

        // Hidden blend properties — controlled by ShaderGUI
        [HideInInspector] _SrcBlend ("SrcBlend", Float) = 1
        [HideInInspector] _DstBlend ("DstBlend", Float) = 0
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "UniversalMaterialType" = "Lit"
        }

        // =====================================================
        // FORWARD LIT PASS
        // =====================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0

            // Vertex / Fragment
            #pragma vertex MiSideVert
            #pragma fragment MiSideFrag

            // Material keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _RIMLIGHT
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _TRANSLUCENCY
            #pragma shader_feature_local _GLASS

            // URP multi_compiles
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "MiSide_Common.hlsl"

            // -----------------------------------------------------------
            // SRP Batcher compatible CBUFFER
            // -----------------------------------------------------------
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half   _BumpScale;
                half4  _RimColor;
                half   _RimPower;
                half   _RimIntensity;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _Cutoff;
                half4  _TranslucencyColor;
                half   _TranslucencyPower;
                half   _TranslucencyStrength;
                half4  _GlassColor;
                half   _GlassReflectivity;
                half   _GlassSmoothness;
                half   _GlassFresnelPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);      SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);  SAMPLER(sampler_EmissionMap);

            // -----------------------------------------------------------
            // Structs
            // -----------------------------------------------------------
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                #ifdef _NORMALMAP
                float4 tangentOS  : TANGENT;
                #endif
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                half   fogFactor   : TEXCOORD3;

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD4;
                #endif

                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 5);

                #ifdef _NORMALMAP
                float3 tangentWS   : TEXCOORD6;
                float3 bitangentWS : TEXCOORD7;
                #endif

                half4  vertexColor : COLOR;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // -----------------------------------------------------------
            // Vertex Shader
            // -----------------------------------------------------------
            Varyings MiSideVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInput = GetVertexNormalInputs(IN.normalOS
                    #ifdef _NORMALMAP
                    , IN.tangentOS
                    #endif
                );

                OUT.positionCS = vertexInput.positionCS;
                OUT.positionWS = vertexInput.positionWS;
                OUT.normalWS   = normalInput.normalWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(vertexInput.positionCS.z);

                #ifdef _NORMALMAP
                OUT.tangentWS   = normalInput.tangentWS;
                OUT.bitangentWS = normalInput.bitangentWS;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                OUT.shadowCoord = GetShadowCoord(vertexInput);
                #endif

                OUTPUT_LIGHTMAP_UV(IN.lightmapUV, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                OUT.vertexColor = IN.color;

                return OUT;
            }

            // -----------------------------------------------------------
            // Fragment Shader
            // -----------------------------------------------------------
            half4 MiSideFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Sample base texture
                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                baseColor *= IN.vertexColor; // Vertex color tint

                #ifdef _ALPHATEST_ON
                    clip(baseColor.a - _Cutoff);
                #endif

                // Normal
                float3 normalWS = normalize(IN.normalWS);
                #ifdef _NORMALMAP
                    half3 normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                    float3x3 tbn = float3x3(
                        normalize(IN.tangentWS),
                        normalize(IN.bitangentWS),
                        normalWS);
                    normalWS = normalize(mul(normalTS, tbn));
                #endif

                // Main light
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = IN.shadowCoord;
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                Light mainLight = GetMainLight(shadowCoord);

                // Half-Lambert for softer shading
                float NdotL = dot(normalWS, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;

                // Soft 2-step toon ramp (MiSide style)
                float toonRamp = smoothstep(
                    _ShadowStep - _ShadowFeather,
                    _ShadowStep + _ShadowFeather,
                    halfLambert
                );

                // Blend with realtime shadow attenuation
                float shadowAtten = mainLight.shadowAttenuation;
                toonRamp = min(toonRamp, smoothstep(0.0, _ShadowFeather * 2.0, shadowAtten));

                // Baked GI / lightmap contribution
                half3 bakedGI = SAMPLE_GI(IN.lightmapUV, IN.vertexSH, normalWS);

                // Integrated GI: lit path gets direct + GI, shadow path gets warm tint + ambient lift
                half3 litColor    = baseColor.rgb * (mainLight.color + bakedGI);
                half3 shadowColor = baseColor.rgb * _ShadowColor.rgb * saturate(bakedGI + 0.3);
                half3 finalColor  = lerp(shadowColor, litColor, toonRamp);

                // Additional lights (toon-shaded)
                finalColor += MiSideAdditionalLights(IN.positionWS, normalWS, baseColor.rgb, _ShadowStep, IN.positionCS);

                // Optional rim light
                #ifdef _RIMLIGHT
                    float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                    finalColor += MiSideRimLight(viewDir, normalWS, _RimColor, _RimPower, _RimIntensity);
                #endif

                // Optional emission
                #ifdef _EMISSION
                    half3 emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb;
                    finalColor += emission * _EmissionColor.rgb;
                #endif

                // Translucency (foliage backlighting)
                #ifdef _TRANSLUCENCY
                {
                    float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                    half VdotL = saturate(dot(viewDir, -mainLight.direction));
                    half backlight = pow(VdotL, _TranslucencyPower) * _TranslucencyStrength;
                    finalColor += baseColor.rgb * _TranslucencyColor.rgb * backlight * mainLight.color * shadowAtten;
                }
                #endif

                // Glass reflection (Fresnel + reflection probe)
                half finalAlpha = baseColor.a;
                #ifdef _GLASS
                {
                    float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                    float3 reflectDir = reflect(-viewDir, normalWS);

                    // Fresnel: edges are more reflective
                    half NdotV = saturate(dot(normalWS, viewDir));
                    half fresnel = pow(1.0 - NdotV, _GlassFresnelPower);
                    half reflStrength = lerp(_GlassReflectivity * 0.3, _GlassReflectivity, fresnel);

                    // Sample reflection probe cubemap (roughness from smoothness)
                    half perceptualRoughness = 1.0 - _GlassSmoothness;
                    half mip = perceptualRoughness * 6.0; // UNITY_SPECCUBE_LOD_STEPS
                    half4 encodedIrradiance = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectDir, mip);
                    half3 reflColor = DecodeHDREnvironment(encodedIrradiance, unity_SpecCube0_HDR);

                    // Tint the reflection
                    reflColor *= _GlassColor.rgb;

                    // Blend reflection onto the surface
                    finalColor = lerp(finalColor, reflColor, reflStrength);

                    // Alpha: force opaque — prevents passthrough compositor from bleeding through glass
                    finalAlpha = 1.0;
                }
                #endif

                // Apply fog
                finalColor = MixFog(finalColor, IN.fogFactor);

                return half4(finalColor, finalAlpha);
            }
            ENDHLSL
        }

        // =====================================================
        // SHADOW CASTER PASS
        // =====================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half   _BumpScale;
                half4  _RimColor;
                half   _RimPower;
                half   _RimIntensity;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _Cutoff;
                half4  _TranslucencyColor;
                half   _TranslucencyPower;
                half   _TranslucencyStrength;
                half4  _GlassColor;
                half   _GlassReflectivity;
                half   _GlassSmoothness;
                half   _GlassFresnelPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
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

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                positionWS = ApplyShadowBias(positionWS, normalWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(positionWS);

                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                    clip(col.a * _BaseColor.a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }

        // =====================================================
        // DEPTH ONLY PASS
        // =====================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half   _BumpScale;
                half4  _RimColor;
                half   _RimPower;
                half   _RimIntensity;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _Cutoff;
                half4  _TranslucencyColor;
                half   _TranslucencyPower;
                half   _TranslucencyStrength;
                half4  _GlassColor;
                half   _GlassReflectivity;
                half   _GlassSmoothness;
                half   _GlassFresnelPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

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

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                    clip(col.a * _BaseColor.a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }

        // =====================================================
        // DEPTH NORMALS PASS (for SSAO)
        // =====================================================
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half   _BumpScale;
                half4  _RimColor;
                half   _RimPower;
                half   _RimIntensity;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _Cutoff;
                half4  _TranslucencyColor;
                half   _TranslucencyPower;
                half   _TranslucencyStrength;
                half4  _GlassColor;
                half   _GlassReflectivity;
                half   _GlassSmoothness;
                half   _GlassFresnelPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                #ifdef _NORMALMAP
                float4 tangentOS  : TANGENT;
                #endif
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                #ifdef _NORMALMAP
                float3 tangentWS   : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexNormalInputs normalInput = GetVertexNormalInputs(IN.normalOS
                    #ifdef _NORMALMAP
                    , IN.tangentOS
                    #endif
                );

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS = normalInput.normalWS;

                #ifdef _NORMALMAP
                OUT.tangentWS   = normalInput.tangentWS;
                OUT.bitangentWS = normalInput.bitangentWS;
                #endif

                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                    clip(col.a * _BaseColor.a - _Cutoff);
                #endif

                float3 normalWS = normalize(IN.normalWS);
                #ifdef _NORMALMAP
                    half3 normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                    float3x3 tbn = float3x3(
                        normalize(IN.tangentWS),
                        normalize(IN.bitangentWS),
                        normalWS);
                    normalWS = normalize(mul(normalTS, tbn));
                #endif

                return half4(normalWS, 0.0);
            }
            ENDHLSL
        }

        // =====================================================
        // META PASS (Lightmap Baking)
        // =====================================================
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }

            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex MetaVert
            #pragma fragment MetaFrag

            #pragma shader_feature_local _EMISSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadowColor;
                half   _ShadowStep;
                half   _ShadowFeather;
                half   _BumpScale;
                half4  _RimColor;
                half   _RimPower;
                half   _RimIntensity;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _Cutoff;
                half4  _TranslucencyColor;
                half   _TranslucencyPower;
                half   _TranslucencyStrength;
                half4  _GlassColor;
                half   _GlassReflectivity;
                half   _GlassSmoothness;
                half   _GlassFresnelPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap);  SAMPLER(sampler_EmissionMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uvLM       : TEXCOORD1;
                float2 uvDLM      : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings MetaVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = UnityMetaVertexPosition(IN.positionOS.xyz, IN.uvLM, IN.uvDLM);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 MetaFrag(Varyings IN) : SV_Target
            {
                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = baseColor.rgb;

                #ifdef _EMISSION
                    half3 emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb;
                    metaInput.Emission = emission * _EmissionColor.rgb;
                #endif

                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }

    CustomEditor "MiSideShaderGUI"
    FallBack "Universal Render Pipeline/Lit"
}
