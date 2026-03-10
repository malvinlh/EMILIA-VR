#ifndef MISIDE_COMMON_INCLUDED
#define MISIDE_COMMON_INCLUDED

// ============================================================
// MiSide Common — Shared toon shading functions
// Used by MiSide/Environment and MiSide/Character shaders
// ============================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// -----------------------------------------------------------
// Toon Ramp — smoothstep-based soft step
// Returns 0 in shadow, 1 in light, smooth transition at step
// -----------------------------------------------------------
half MiSideToonRamp(half NdotL, half step, half feather)
{
    half halfLambert = NdotL * 0.5 + 0.5;
    return smoothstep(step - feather, step + feather, halfLambert);
}

// Overload taking pre-computed half-lambert
half MiSideToonRampHL(half halfLambert, half step, half feather)
{
    return smoothstep(step - feather, step + feather, halfLambert);
}

// -----------------------------------------------------------
// Rim Light — Fresnel-based rim with configurable falloff
// -----------------------------------------------------------
half3 MiSideRimLight(float3 viewDir, float3 normalWS, half4 rimColor, half power, half intensity)
{
    half rim = pow(1.0 - saturate(dot(viewDir, normalWS)), power);
    return rimColor.rgb * rim * intensity;
}

// Rim light with inside mask (for character shader — UTS-compatible)
half3 MiSideRimLightMasked(float3 viewDir, float3 normalWS, half4 rimColor, half power, half insideMask)
{
    half NdotV = saturate(dot(viewDir, normalWS));
    half rim = pow(1.0 - NdotV, power);
    // Inside mask: suppress rim on surfaces facing the camera
    half mask = saturate((NdotV - insideMask) / (1.0 - insideMask + 0.0001));
    rim *= (1.0 - mask);
    return rimColor.rgb * rim;
}

// -----------------------------------------------------------
// Additional Lights — Toon-shaded loop
// Applies a simplified single-step toon ramp per light
// Supports both standard Forward and Forward+ rendering paths
// -----------------------------------------------------------
half3 MiSideAdditionalLights(float3 positionWS, float3 normalWS, half3 baseColor, half shadowStep, float4 positionCS)
{
    half3 additionalColor = half3(0, 0, 0);

#if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS)
    // Set up inputData for Forward+ cluster-based light culling
    InputData inputData = (InputData)0;
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    inputData.positionWS = positionWS;

    uint lightsCount = GetAdditionalLightsCount();

    LIGHT_LOOP_BEGIN(lightsCount)
        Light light = GetAdditionalLight(lightIndex, positionWS);

        half NdotL = dot(normalWS, light.direction);
        half ramp = step(shadowStep, NdotL * 0.5 + 0.5);

        half attenuation = light.distanceAttenuation * light.shadowAttenuation;
        additionalColor += baseColor * light.color * ramp * attenuation;
    LIGHT_LOOP_END
#endif

    return additionalColor;
}

#endif // MISIDE_COMMON_INCLUDED
