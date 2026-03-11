#ifndef MISIDE_TERRAIN_INPUT_INCLUDED
#define MISIDE_TERRAIN_INPUT_INCLUDED

// ============================================================
// MiSide Toon Terrain — Shared Input Definitions
// Included by every pass in MiSide_ToonTerrain.shader
// ============================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// -----------------------------------------------------------
// SRP Batcher compatible CBUFFER — identical across ALL passes
// -----------------------------------------------------------
CBUFFER_START(UnityPerMaterial)
    // Toon shading (artist-controlled)
    half4  _ShadowColor;
    half   _ShadowStep;
    half   _ShadowFeather;
    half4  _RimColor;
    half   _RimPower;
    half   _RimIntensity;

    // Terrain layer UV transforms (auto-set by Terrain component)
    float4 _Control_ST;
    float4 _Control_TexelSize;
    float4 _Splat0_ST;
    float4 _Splat1_ST;
    float4 _Splat2_ST;
    float4 _Splat3_ST;
    half   _NormalScale0;
    half   _NormalScale1;
    half   _NormalScale2;
    half   _NormalScale3;
    half   _NumLayersCount;
CBUFFER_END

// -----------------------------------------------------------
// Terrain textures (populated by Terrain component)
// -----------------------------------------------------------
TEXTURE2D(_Control);             SAMPLER(sampler_Control);
TEXTURE2D(_Splat0);              SAMPLER(sampler_Splat0);  // shared for all layers
TEXTURE2D(_Splat1);
TEXTURE2D(_Splat2);
TEXTURE2D(_Splat3);
TEXTURE2D(_Normal0);             SAMPLER(sampler_Normal0); // shared for all normals
TEXTURE2D(_Normal1);
TEXTURE2D(_Normal2);
TEXTURE2D(_Normal3);
TEXTURE2D(_TerrainHolesTexture); SAMPLER(sampler_TerrainHolesTexture);

// Heightmap / normalmap (for instanced & per-pixel normal paths)
TEXTURE2D(_TerrainHeightmapTexture);
TEXTURE2D(_TerrainNormalmapTexture);
SAMPLER(sampler_TerrainNormalmapTexture);

float4 _TerrainHeightmapRecipSize; // {1/w, 1/h, 1/(w-1), 1/(h-1)}
float4 _TerrainHeightmapScale;     // {size.x, hmScale.y, size.z, 0}

// -----------------------------------------------------------
// Terrain instancing buffer
// -----------------------------------------------------------
#ifdef UNITY_INSTANCING_ENABLED
    UNITY_INSTANCING_BUFFER_START(Terrain)
        UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)
    UNITY_INSTANCING_BUFFER_END(Terrain)
#endif

// -----------------------------------------------------------
// Terrain vertex processing (handles instanced heightmap)
// When instancing is off (m_DrawInstanced = 0), this is a no-op
// and standard mesh vertices are used.
// -----------------------------------------------------------
void TerrainInstancing(inout float4 positionOS, inout float3 normalOS, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;

    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));
    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;

    #ifdef _TERRAIN_INSTANCED_PERPIXEL_NORMAL
        normalOS = float3(0, 1, 0); // fragment will fetch real normal
    #else
        float2 ts = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rg;
        normalOS.xz = ts * 2.0 - 1.0;
        normalOS.y = sqrt(max(0, 1.0 - dot(normalOS.xz, normalOS.xz)));
    #endif

    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

// -----------------------------------------------------------
// Clip terrain holes
// -----------------------------------------------------------
void ClipTerrainHoles(float2 terrainUV)
{
#ifdef _ALPHATEST_ON
    half hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, terrainUV).r;
    clip(hole - 0.5);
#endif
}

// -----------------------------------------------------------
// Sample and normalize splatmap weights
// -----------------------------------------------------------
half4 SampleSplatWeights(float2 terrainUV)
{
    half4 w = SAMPLE_TEXTURE2D(_Control, sampler_Control, terrainUV);
    half sum = dot(w, half4(1, 1, 1, 1));
    return w / max(sum, half(0.001));
}

// -----------------------------------------------------------
// Blend 4 layer albedos weighted by splatmap
// -----------------------------------------------------------
half3 BlendSplatAlbedo(float2 terrainUV, half4 weights)
{
    float2 uv0 = terrainUV * _Splat0_ST.xy + _Splat0_ST.zw;
    float2 uv1 = terrainUV * _Splat1_ST.xy + _Splat1_ST.zw;
    float2 uv2 = terrainUV * _Splat2_ST.xy + _Splat2_ST.zw;
    float2 uv3 = terrainUV * _Splat3_ST.xy + _Splat3_ST.zw;

    half3 c = half3(0, 0, 0);
    c += SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0).rgb * weights.r;
    c += SAMPLE_TEXTURE2D(_Splat1, sampler_Splat0, uv1).rgb * weights.g;
    c += SAMPLE_TEXTURE2D(_Splat2, sampler_Splat0, uv2).rgb * weights.b;
    c += SAMPLE_TEXTURE2D(_Splat3, sampler_Splat0, uv3).rgb * weights.a;
    return c;
}

// -----------------------------------------------------------
// Blend 4 layer normal maps (returns tangent-space normal)
// -----------------------------------------------------------
half3 BlendSplatNormals(float2 terrainUV, half4 weights)
{
    float2 uv0 = terrainUV * _Splat0_ST.xy + _Splat0_ST.zw;
    float2 uv1 = terrainUV * _Splat1_ST.xy + _Splat1_ST.zw;
    float2 uv2 = terrainUV * _Splat2_ST.xy + _Splat2_ST.zw;
    float2 uv3 = terrainUV * _Splat3_ST.xy + _Splat3_ST.zw;

    half3 n = half3(0, 0, 0);
    n += UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal0, sampler_Normal0, uv0), _NormalScale0) * weights.r;
    n += UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal1, sampler_Normal0, uv1), _NormalScale1) * weights.g;
    n += UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal2, sampler_Normal0, uv2), _NormalScale2) * weights.b;
    n += UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal3, sampler_Normal0, uv3), _NormalScale3) * weights.a;
    return normalize(n);
}

// -----------------------------------------------------------
// Per-pixel terrain normal (from _TerrainNormalmapTexture)
// Unity stores XZ in RG channels, Y is derived
// -----------------------------------------------------------
float3 GetTerrainPerPixelNormal(float2 terrainUV)
{
    float2 ts = SAMPLE_TEXTURE2D(_TerrainNormalmapTexture, sampler_TerrainNormalmapTexture, terrainUV).rg;
    float3 normal;
    normal.xz = ts * 2.0 - 1.0;
    normal.y = sqrt(max(0, 1.0 - dot(normal.xz, normal.xz)));
    return TransformObjectToWorldNormal(normal);
}

// -----------------------------------------------------------
// Build TBN from terrain geometry normal
// Used when blending per-layer normal maps onto the surface
// -----------------------------------------------------------
float3x3 GetTerrainTBN(float3 normalWS)
{
    // Choose a reference vector that isn't parallel to the normal
    float3 ref = abs(normalWS.y) > 0.99 ? float3(0, 0, 1) : float3(0, 1, 0);
    float3 t = normalize(cross(normalWS, ref));
    float3 b = normalize(cross(normalWS, t));
    return float3x3(t, b, normalWS);
}

#endif // MISIDE_TERRAIN_INPUT_INCLUDED
