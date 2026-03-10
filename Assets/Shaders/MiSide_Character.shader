Shader "MiSide/Character"
{
    Properties
    {
        [MainTexture] _MainTex ("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color", Color) = (1,1,1,1)

        [Header(1st Shade)]
        _1st_ShadeColor ("1st Shade Tint", Color) = (0.85, 0.75, 0.72, 1)
        _1st_ShadeColor_Step ("1st Shade Step", Range(0, 1)) = 0.5
        _1st_ShadeColor_Feather ("1st Shade Feather", Range(0.001, 0.3)) = 0.06

        [Header(2nd Shade)]
        _2nd_ShadeColor ("2nd Shade Tint", Color) = (0.72, 0.62, 0.60, 1)
        _2nd_ShadeColor_Step ("2nd Shade Step", Range(0, 1)) = 0.15
        _2nd_ShadeColor_Feather ("2nd Shade Feather", Range(0.001, 0.3)) = 0.08

        [Header(Rim Light)]
        [Toggle(_RIMLIGHT_ON)] _RimLight ("Enable Rim Light", Float) = 1
        _RimLightColor ("Rim Light Color", Color) = (0.85, 0.75, 0.68, 1)
        _RimLight_Power ("Rim Light Power", Range(1, 20)) = 8
        _RimLight_InsideMask ("Rim Inside Mask", Range(0, 1)) = 0.2

        [Header(Outline)]
        [Toggle(_OUTLINE_ON)] _OUTLINE ("Enable Outline", Float) = 1
        _Outline_Width ("Outline Width", Range(0, 2)) = 0.3
        _Outline_Color ("Outline Color", Color) = (0.35, 0.30, 0.28, 1)
        [Toggle] _Is_BlendBaseColor ("Blend Base Color into Outline", Float) = 1
        [Toggle] _Is_LightColor_Outline ("Light Color Outline", Float) = 0

        [Header(Lighting)]
        _GI_Intensity ("GI Intensity", Range(0, 1)) = 0.35
        _Tweak_SystemShadowsLevel ("Shadow Level Tweak", Range(-1, 1)) = 0.1
        _HighColor_Power ("Specular Power (0=off)", Range(0, 1)) = 0

        [Header(Unlit and Brightness)]
        _UnlitBlend ("Unlit Blend", Range(0, 1)) = 0
        _MinBrightness ("Min Brightness", Range(0, 1)) = 0.04
        _ShadowSaturation ("Shadow Saturation", Range(0, 2)) = 1.2

        [Header(Alpha Cutout)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
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
        // FORWARD LIT PASS — 2-zone toon character shading
        // =====================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex CharVert
            #pragma fragment CharFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _RIMLIGHT_ON
            #pragma shader_feature_local _OUTLINE_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "MiSide_Common.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
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

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CharVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInput = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = vertexInput.positionCS;
                OUT.positionWS = vertexInput.positionWS;
                OUT.normalWS   = normalInput.normalWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.fogFactor  = ComputeFogFactor(vertexInput.positionCS.z);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                OUT.shadowCoord = GetShadowCoord(vertexInput);
                #endif

                OUTPUT_LIGHTMAP_UV(IN.lightmapUV, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                return OUT;
            }

            half4 CharFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 baseColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _BaseColor;

                #ifdef _ALPHATEST_ON
                    clip(baseColor.a - _Cutoff);
                #endif

                float3 normalWS = normalize(IN.normalWS);

                // Main light
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = IN.shadowCoord;
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                Light mainLight = GetMainLight(shadowCoord);

                // Half-Lambert
                float NdotL = dot(normalWS, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;

                // Shadow attenuation with tweak
                float shadowAtten = saturate(mainLight.shadowAttenuation + _Tweak_SystemShadowsLevel);

                // Baked GI / SH ambient
                half3 bakedGI = SAMPLE_GI(IN.lightmapUV, IN.vertexSH, normalWS);

                // ---- 2-zone toon ramp ----
                // Zone 1: lit vs 1st shade
                float ramp1 = smoothstep(
                    _1st_ShadeColor_Step - _1st_ShadeColor_Feather,
                    _1st_ShadeColor_Step + _1st_ShadeColor_Feather,
                    halfLambert);
                ramp1 = min(ramp1, shadowAtten);

                // Zone 2: 1st shade vs 2nd shade
                float ramp2 = smoothstep(
                    _2nd_ShadeColor_Step - _2nd_ShadeColor_Feather,
                    _2nd_ShadeColor_Step + _2nd_ShadeColor_Feather,
                    halfLambert);
                ramp2 = min(ramp2, shadowAtten);

                // ---- HSR-style lighting: base color is king ----
                // Shade tint: blend between 2nd and 1st shade tint colors
                half3 shadeTint = lerp(_2nd_ShadeColor.rgb, _1st_ShadeColor.rgb, ramp2);

                // Apply shadow saturation to the tint
                half shadeLuma = dot(shadeTint, half3(0.299, 0.587, 0.114));
                shadeTint = lerp(half3(shadeLuma, shadeLuma, shadeLuma), shadeTint, _ShadowSaturation);

                // Combine direct + indirect light (same as environment shader)
                // then soft-clamp to prevent overbright while PRESERVING
                // the environment's color temperature on the character.
                half3 totalLight = mainLight.color + bakedGI * _GI_Intensity;
                half peakLight = max(max(totalLight.r, totalLight.g), totalLight.b);
                // Proportional normalization: if peak > 1 scale down evenly
                // (keeps color ratio intact — pink room stays pink, not clipped)
                totalLight *= rcp(max(peakLight, 1.0));

                // Lit path: base color tinted by environment light
                half3 litColor = baseColor.rgb * totalLight;

                // Shadow path: base color * shade tint, lifted by ambient
                half3 shadowColor = baseColor.rgb * shadeTint * saturate(
                    bakedGI * _GI_Intensity + 0.3);

                // Final toon blend: shadow path <-> lit path
                half3 finalColor = lerp(shadowColor, litColor, ramp1);

                // Additional lights
                finalColor += MiSideAdditionalLights(IN.positionWS, normalWS, baseColor.rgb, _1st_ShadeColor_Step) * 0.6;

                // Rim light
                #ifdef _RIMLIGHT_ON
                {
                    float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                    finalColor += MiSideRimLightMasked(viewDir, normalWS,
                        _RimLightColor, _RimLight_Power, _RimLight_InsideMask);
                }
                #endif

                // ---- Unlit blend (eyes/special): preserve base color with environment tint ----
                half3 unlitColor = baseColor.rgb * saturate(totalLight * 0.8 + 0.15);
                finalColor = lerp(finalColor, unlitColor, _UnlitBlend);

                // ---- Min brightness floor ----
                half luma = dot(finalColor, half3(0.299, 0.587, 0.114));
                half brightnessFactor = max(luma, _MinBrightness) / max(luma, 0.001);
                finalColor *= brightnessFactor;

                // Apply fog
                finalColor = MixFog(finalColor, IN.fogFactor);

                return half4(finalColor, baseColor.a);
            }
            ENDHLSL
        }

        // =====================================================
        // OUTLINE PASS — Inverted hull
        // =====================================================
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex OutlineVert
            #pragma fragment OutlineFrag

            #pragma shader_feature_local _OUTLINE_ON
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                half   fogFactor  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings OutlineVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                #ifdef _OUTLINE_ON
                    // Transform to clip space first
                    float4 posCS = TransformObjectToHClip(IN.positionOS.xyz);
                    float3 normalCS = TransformWorldToHClipDir(TransformObjectToWorldNormal(IN.normalOS));

                    // Scale outline width by distance for consistent screen-space thickness in VR
                    float dist = length(GetCameraPositionWS() - TransformObjectToWorld(IN.positionOS.xyz));
                    float scaledWidth = _Outline_Width * 0.001 * dist;

                    // Extrude along normal in clip space
                    float2 offset = normalize(normalCS.xy) * scaledWidth;
                    posCS.xy += offset;

                    OUT.positionCS = posCS;
                #else
                    // No outline — collapse to zero
                    OUT.positionCS = float4(0, 0, 0, 1);
                #endif

                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 OutlineFrag(Varyings IN) : SV_Target
            {
                #ifndef _OUTLINE_ON
                    discard;
                #endif

                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half3 outlineColor = _Outline_Color.rgb;

                // HSR-style: outline is a darkened version of the surface color
                // This makes outlines color-match each part (skin=warm brown, hair=dark hair, etc.)
                if (_Is_BlendBaseColor > 0.5)
                {
                    half3 baseCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).rgb * _BaseColor.rgb;
                    // Darken base color by outline tint — richer than flat color
                    half baseLuma = dot(baseCol, half3(0.299, 0.587, 0.114));
                    // Mix: use base color hue but darken toward outline color
                    outlineColor = lerp(_Outline_Color.rgb, baseCol * _Outline_Color.rgb * 2.5, 0.7);
                    // Ensure outline is always darker than the surface
                    outlineColor = min(outlineColor, baseCol * 0.5);
                }

                // Optionally tint by main light color (subtle)
                if (_Is_LightColor_Outline > 0.5)
                {
                    Light mainLight = GetMainLight();
                    outlineColor *= lerp(half3(1,1,1), mainLight.color, 0.3);
                }

                outlineColor = MixFog(outlineColor, IN.fogFactor);

                return half4(outlineColor, 1.0);
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
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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

                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
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
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
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
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);

                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef _ALPHATEST_ON
                    half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                    clip(col.a * _BaseColor.a - _Cutoff);
                #endif

                return half4(normalize(IN.normalWS), 0.0);
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _BaseColor;
                half4  _1st_ShadeColor;
                half   _1st_ShadeColor_Step;
                half   _1st_ShadeColor_Feather;
                half4  _2nd_ShadeColor;
                half   _2nd_ShadeColor_Step;
                half   _2nd_ShadeColor_Feather;
                half4  _RimLightColor;
                half   _RimLight_Power;
                half   _RimLight_InsideMask;
                half   _Outline_Width;
                half4  _Outline_Color;
                half   _Is_BlendBaseColor;
                half   _Is_LightColor_Outline;
                half   _GI_Intensity;
                half   _Tweak_SystemShadowsLevel;
                half   _HighColor_Power;
                half   _UnlitBlend;
                half   _MinBrightness;
                half   _ShadowSaturation;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 MetaFrag(Varyings IN) : SV_Target
            {
                half4 baseColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _BaseColor;

                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = baseColor.rgb;

                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }

    CustomEditor "MiSideCharacterShaderGUI"
    FallBack "Universal Render Pipeline/Lit"
}
