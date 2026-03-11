// ============================================================
// MiSide/ToonTerrain — Anime-style toon shader for Unity Terrain
//
// Blends up to 4 terrain layers via splatmap with MiSide-style
// toon lighting (half-Lambert, smoothstep ramp, warm shadow).
// Supports instanced rendering, per-pixel terrain normals,
// per-layer normal maps, terrain holes, and VR stereo.
//
// Assign to material → set as Terrain MaterialTemplate.
// Terrain layers (textures) are configured from the Terrain
// component Inspector, NOT from the material Inspector.
// ============================================================

Shader "MiSide/ToonTerrain"
{
    Properties
    {
        [Header(Toon Shading)]
        _ShadowColor ("Shadow Color", Color) = (0.78, 0.72, 0.65, 1)
        _ShadowStep ("Shadow Step", Range(0, 1)) = 0.5
        _ShadowFeather ("Shadow Feather", Range(0.001, 0.5)) = 0.12

        [Header(Normal Maps)]
        [Toggle(_NORMALMAP)] _NormalMapToggle ("Enable Layer Normals", Float) = 0

        [Header(Rim Light)]
        [Toggle(_RIMLIGHT)] _RimLightToggle ("Enable Rim Light", Float) = 0
        _RimColor ("Rim Color", Color) = (1, 0.9, 0.85, 1)
        _RimPower ("Rim Power", Range(1, 10)) = 4
        _RimIntensity ("Rim Intensity", Range(0, 1)) = 0.1

        // ----- Terrain system properties (auto-populated) -----
        [HideInInspector] _Control ("Splatmap", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Layer 1", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Layer 2", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Layer 3", 2D) = "grey" {}
        [HideInInspector] _Normal0 ("Normal 0", 2D) = "bump" {}
        [HideInInspector] _Normal1 ("Normal 1", 2D) = "bump" {}
        [HideInInspector] _Normal2 ("Normal 2", 2D) = "bump" {}
        [HideInInspector] _Normal3 ("Normal 3", 2D) = "bump" {}
        [HideInInspector] _Mask0 ("Mask 0", 2D) = "black" {}
        [HideInInspector] _Mask1 ("Mask 1", 2D) = "black" {}
        [HideInInspector] _Mask2 ("Mask 2", 2D) = "black" {}
        [HideInInspector] _Mask3 ("Mask 3", 2D) = "black" {}
        [HideInInspector] _NormalScale0 ("Normal Scale 0", Float) = 1
        [HideInInspector] _NormalScale1 ("Normal Scale 1", Float) = 1
        [HideInInspector] _NormalScale2 ("Normal Scale 2", Float) = 1
        [HideInInspector] _NormalScale3 ("Normal Scale 3", Float) = 1
        [HideInInspector] _Metallic0 ("Metallic 0", Float) = 0
        [HideInInspector] _Metallic1 ("Metallic 1", Float) = 0
        [HideInInspector] _Metallic2 ("Metallic 2", Float) = 0
        [HideInInspector] _Metallic3 ("Metallic 3", Float) = 0
        [HideInInspector] _Smoothness0 ("Smoothness 0", Float) = 0
        [HideInInspector] _Smoothness1 ("Smoothness 1", Float) = 0
        [HideInInspector] _Smoothness2 ("Smoothness 2", Float) = 0
        [HideInInspector] _Smoothness3 ("Smoothness 3", Float) = 0
        [HideInInspector] _NumLayersCount ("Layer Count", Float) = 1
        [HideInInspector] _TerrainHolesTexture ("Holes Map", 2D) = "white" {}
        [HideInInspector] [ToggleOff] _EnableInstancedPerPixelNormal ("Per-Pixel Normal", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-100"
            "TerrainCompatible" = "True"
        }

        // =====================================================
        // FORWARD LIT PASS — Toon-shaded terrain
        // =====================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex ToonTerrainVert
            #pragma fragment ToonTerrainFrag

            // Material keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _RIMLIGHT
            #pragma shader_feature_local _ALPHATEST_ON

            // Terrain keywords
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL

            // URP multi_compiles
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "MiSide_TerrainInput.hlsl"
            #include "MiSide_Common.hlsl"

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
                float2 terrainUV   : TEXCOORD0;
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

            Varyings ToonTerrainVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                // Terrain instanced vertex processing (no-op when instancing is off)
                TerrainInstancing(IN.positionOS, IN.normalOS, IN.uv);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInput = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = vertexInput.positionCS;
                OUT.positionWS = vertexInput.positionWS;
                OUT.normalWS   = normalInput.normalWS;
                OUT.terrainUV  = IN.uv;
                OUT.fogFactor  = ComputeFogFactor(vertexInput.positionCS.z);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                OUT.shadowCoord = GetShadowCoord(vertexInput);
                #endif

                OUTPUT_LIGHTMAP_UV(IN.lightmapUV, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                return OUT;
            }

            half4 ToonTerrainFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 terrainUV = IN.terrainUV;

                // Terrain holes
                ClipTerrainHoles(terrainUV);

                // Splatmap blending — blend up to 4 terrain layers
                half4 weights = SampleSplatWeights(terrainUV);
                half3 albedo  = BlendSplatAlbedo(terrainUV, weights);

                // Geometry normal
                #ifdef _TERRAIN_INSTANCED_PERPIXEL_NORMAL
                    float3 geomNormalWS = GetTerrainPerPixelNormal(terrainUV);
                #else
                    float3 geomNormalWS = normalize(IN.normalWS);
                #endif

                // Per-layer normal map blending (optional)
                #ifdef _NORMALMAP
                    half3 normalTS = BlendSplatNormals(terrainUV, weights);
                    float3x3 tbn = GetTerrainTBN(geomNormalWS);
                    float3 normalWS = normalize(mul(normalTS, tbn));
                #else
                    float3 normalWS = geomNormalWS;
                #endif

                // Shadow coordinates
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = IN.shadowCoord;
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                Light mainLight = GetMainLight(shadowCoord);

                // MiSide-style toon ramp (half-Lambert + smoothstep)
                float NdotL = dot(normalWS, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;
                float toonRamp = smoothstep(
                    _ShadowStep - _ShadowFeather,
                    _ShadowStep + _ShadowFeather,
                    halfLambert
                );

                // Blend with realtime shadow attenuation
                float shadowAtten = mainLight.shadowAttenuation;
                toonRamp = min(toonRamp, smoothstep(0.0, _ShadowFeather * 2.0, shadowAtten));

                // Baked GI / lightmap
                half3 bakedGI = SAMPLE_GI(IN.lightmapUV, IN.vertexSH, normalWS);

                // Toon color integration (matches MiSide/Environment)
                half3 litColor    = albedo * (mainLight.color + bakedGI);
                half3 shadowColor = albedo * _ShadowColor.rgb * saturate(bakedGI + 0.3);
                half3 finalColor  = lerp(shadowColor, litColor, toonRamp);

                // Additional lights (toon-shaded, from MiSide_Common.hlsl)
                finalColor += MiSideAdditionalLights(IN.positionWS, normalWS, albedo, _ShadowStep, IN.positionCS);

                // Optional rim light
                #ifdef _RIMLIGHT
                    float3 viewDir = normalize(GetCameraPositionWS() - IN.positionWS);
                    finalColor += MiSideRimLight(viewDir, normalWS, _RimColor, _RimPower, _RimIntensity);
                #endif

                // Fog
                finalColor = MixFog(finalColor, IN.fogFactor);

                return half4(finalColor, 1.0);
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

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "MiSide_TerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

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
                float2 terrainUV  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                TerrainInstancing(IN.positionOS, IN.normalOS, IN.uv);

                float3 posWS  = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);
                posWS = ApplyShadowBias(posWS, normWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(posWS);

                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.terrainUV = IN.uv;
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                ClipTerrainHoles(IN.terrainUV);
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

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "MiSide_TerrainInput.hlsl"

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
                float2 terrainUV  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                TerrainInstancing(IN.positionOS, IN.normalOS, IN.uv);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.terrainUV  = IN.uv;
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                ClipTerrainHoles(IN.terrainUV);
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

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "MiSide_TerrainInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 terrainUV   : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                TerrainInstancing(IN.positionOS, IN.normalOS, IN.uv);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.terrainUV  = IN.uv;
                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                ClipTerrainHoles(IN.terrainUV);

                float2 terrainUV = IN.terrainUV;

                #ifdef _TERRAIN_INSTANCED_PERPIXEL_NORMAL
                    float3 normalWS = GetTerrainPerPixelNormal(terrainUV);
                #else
                    float3 normalWS = normalize(IN.normalWS);
                #endif

                #ifdef _NORMALMAP
                    half4 weights = SampleSplatWeights(terrainUV);
                    half3 normalTS = BlendSplatNormals(terrainUV, weights);
                    float3x3 tbn = GetTerrainTBN(normalWS);
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

            #include "MiSide_TerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

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
                float2 terrainUV  : TEXCOORD0;
            };

            Varyings MetaVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = UnityMetaVertexPosition(IN.positionOS.xyz, IN.uvLM, IN.uvDLM);
                OUT.terrainUV  = IN.uv;
                return OUT;
            }

            half4 MetaFrag(Varyings IN) : SV_Target
            {
                half4 weights = SampleSplatWeights(IN.terrainUV);
                half3 albedo  = BlendSplatAlbedo(IN.terrainUV, weights);

                MetaInput metaInput = (MetaInput)0;
                metaInput.Albedo = albedo;
                return UnityMetaFragment(metaInput);
            }
            ENDHLSL
        }
    }

    CustomEditor "MiSideToonTerrainGUI"
    FallBack "Universal Render Pipeline/Terrain/Lit"
}
