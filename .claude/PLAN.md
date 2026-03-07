# Shader & Lighting Plan: Calming Anime VR Environment (MiSide-Inspired)

## Context

EMILIA-VR is a calming 3D VR app for reflection, inspired by MiSide's anime aesthetic. The project has 3 custom MiSide shaders (Environment, ToonSkyGradient, ToonWater) and 4 editor tools. The AZKi character model (33 materials) is currently on URP/Lit but carries serialized UTS property data. UTS is not installed. The 4 target scenes need cohesive toon shading, lighting, and post-processing for a warm, soothing anime atmosphere.

**Goal:** Revise the 3 existing shaders, create a new character shader, and define per-scene lighting/material/post-processing setup.

---

## Phase 1: Shared Foundation [DONE]

### 1.1 Create `Assets/Shaders/MiSide_Common.hlsl` [DONE]

Shared HLSL include for visual consistency between environment and character shaders:
- `ToonRamp(half NdotL, half step, half feather)` — smoothstep-based ramp
- `CalcRimLight(viewDir, normalWS, rimColor, power, intensity)` — shared rim function
- `CalcAdditionalLightsToon(positionWS, normalWS, baseColor, shadowStep)` — additional lights loop with simplified toon ramp per light; clamped to `min(count, 3)` on standard Forward, uncapped on Forward+

### 1.2 Revise `Assets/Shaders/MiSide_Environment.shader` [DONE]

**Fixes:**
- **Shadow formula**: Replace `baseColor.rgb * _ShadowColor.rgb * _ShadowIntensity` with integrated GI approach:
  ```hlsl
  half3 litColor = baseColor.rgb * (mainLight.color + bakedGI);
  half3 shadowCol = baseColor.rgb * _ShadowColor.rgb * saturate(bakedGI + 0.3);
  half3 finalColor = lerp(shadowCol, litColor, toonRamp);
  ```
  Removed `_ShadowIntensity` property entirely. `_ShadowColor` (default 0.85, 0.75, 0.72) acts as a direct warm tint at ~80% brightness — low contrast, calming.
- **GI over-brightening**: Removed the additive `+= baseColor * bakedGI * (1 - toonRamp*0.5)` — GI is now integrated into both lit/shadow paths above.

**Additions:**
- **Additional lights**: Added `#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS` and `_ADDITIONAL_LIGHT_SHADOWS`. Uses shared `MiSideAdditionalLights()` from Common.hlsl.
- **DepthNormals pass**: New pass (`LightMode = DepthNormalsOnly`) for SSAO compatibility. Required since PC_Renderer has SSAO enabled.
- **Normal map** (optional): Added `_BumpMap`, `_BumpScale` behind `#pragma shader_feature_local _NORMALMAP`. Tangent added to vertex attributes behind same toggle.
- Updated CBUFFER in ALL 5 passes (removed `_ShadowIntensity`, added `_BumpScale`).

### 1.3 Update `Assets/Shaders/Editor/MiSideShaderGUI.cs` [DONE]

- Added Normal Map foldout with texture + bump scale slider
- Removed _ShadowIntensity from Toon Shading section
- Updated `ApplyMiSideDefaults()` to remove `_ShadowIntensity` references, added `_NORMALMAP` keyword sync

### 1.4 Update `Assets/Shaders/Editor/MiSideMaterialConverter.cs` [DONE]

- Removed `_ShadowIntensity` references from `ConvertSingleMaterial()`
- Added normal map default values

---

## Phase 2: Sky & Water Shaders [DONE]

### 2.1 Revise `Assets/Shaders/MiSide_ToonSkyGradient.shader` [DONE]

Added 3-color gradient with horizon band for depth and warmth:
- New properties: `_HorizonColor` (Color), `_HorizonBandWidth` (Range 0.01-0.5, default 0.15), `_HorizonHaze` (Range 0-1, default 0.3)
- Fragment: 3-way lerp — bottom-to-horizon, horizon-to-top using `smoothstep` around `viewDir.y ≈ 0`
- Atmospheric haze: soft exponential falloff near horizon for distance feel

### 2.2 Revise `Assets/Shaders/MiSide_ToonWater.shader` [DONE]

- **Toon lighting**: Included Lighting.hlsl, added main light multi_compile. Applied smoothstep toon ramp. Reconstructed normal from wave derivatives.
- **Depth-based shoreline foam**: Samples `_CameraDepthTexture`, computes `sceneDepth - fragmentDepth`, uses as foam mask.
- **Toon specular**: Blinn-Phong stepped highlight for anime water glint.
- New properties: `_WaterSpecColor`, `_SpecPower`, `_SpecThreshold`, `_ShorelineFoamWidth`, `_ShorelineFoamColor`, toon shading properties (`_ShadowColor`, `_ShadowStep`, `_ShadowFeather`)

---

## Phase 3: Character Shader [DONE]

### 3.1 Create `Assets/Shaders/MiSide_Character.shader` [DONE]

Custom toon character shader reusing UTS property names already serialized in the 33 AZKi materials (confirmed in `髪.mat`). Existing materials work immediately after shader switch — no re-authoring needed.

**Properties** (matching existing serialized UTS names):
- `_MainTex` / `_BaseColor`
- `_1st_ShadeColor`, `_1st_ShadeColor_Step` (0.5), `_1st_ShadeColor_Feather` (0.06)
- `_2nd_ShadeColor`, `_2nd_ShadeColor_Step` (0.15), `_2nd_ShadeColor_Feather` (0.1)
- `_RimLight` (toggle), `_RimLightColor`, `_RimLight_Power`, `_RimLight_InsideMask`
- `_OUTLINE` (toggle), `_Outline_Width`, `_Outline_Color`, `_Is_BlendBaseColor`, `_Is_LightColor_Outline`
- `_GI_Intensity`, `_Tweak_SystemShadowsLevel`, `_HighColor_Power`
- `_Cull`, `_AlphaClip`, `_Cutoff`

**Passes:**
1. **ForwardLit** — 2-zone toon shading with rim light, main + additional lights, GI
2. **Outline** — Inverted hull, `Cull Front`, distance-scaled width for VR
3. **ShadowCaster** — Standard
4. **DepthOnly** — Standard
5. **DepthNormalsOnly** — For SSAO
6. **Meta** — For lightmap GI bounce

### 3.2 Create `Assets/Shaders/Editor/MiSideCharacterShaderGUI.cs` [DONE]

Custom inspector with foldouts: Base, 1st Shade, 2nd Shade, Rim Light, Outline, Lighting, Alpha Cutout, Rendering. Category preset buttons (Skin, Hair, Eyes, Clothing, Special) with tuned defaults.

### 3.3 Update `Assets/Shaders/Editor/MiSideCharacterTuner.cs` [DONE]

- **Fixed path**: `Assets/Graphics/3D/Character_AZKi/materials` → `Assets/Graphics/3D/Character/AZKi/materials`
- Added shader switch to `MiSide/Character` before applying properties (preserves base texture)
- Added `SyncKeywords()` for `_RIMLIGHT_ON` and `_OUTLINE_ON`
- Removed obsolete `_BaseShade_Feather` and `_OUTLINE_NML` references

---

## Phase 4: Per-Scene Setup [TODO — requires Unity Editor]

### 4.1 Volume Profiles (create new assets)

Create per-scene Volume Profile assets under `Assets/Settings/`:

| Setting | 3D_Chat (Indoor Room) | 3D_Journal (Beach) | 3D_LoginArea (Ethereal) | 3D_StartArea (Entrance) |
|---|---|---|---|---|
| **Bloom threshold** | 0.75 | 0.70 | 0.65 | 0.80 |
| **Bloom intensity** | 0.15 | 0.20 | 0.25 | 0.10 |
| **Bloom scatter** | 0.65 | 0.70 | 0.75 | 0.60 |
| **Tonemapping** | Neutral | Neutral | Neutral | Neutral |
| **Vignette** | 0.08 | 0.05 | 0.08 | 0.05 |
| **Color temp** | +5 (warm) | +8 (golden) | -3 (cool) | +2 (neutral-warm) |
| **Contrast** | -5 (soft) | -5 (soft) | -3 | -2 |
| **Saturation** | -5 (pastel) | 0 | -5 (pastel) | 0 |

**VR safety rules applied across all scenes:**
- Motion Blur: OFF (causes nausea) — also fix `SampleSceneProfile.asset` which has it active
- Depth of Field: OFF (contradicts natural VR accommodation)
- Vignette: max 0.1 (higher causes discomfort)
- Tonemapping: Neutral (not ACES — ACES adds contrast, counterproductive for soft pastels)

### 4.2 Lighting Setup

#### 3D_Chat (Indoor Cozy Room)
- **Directional light**: Warm key ~4500K, intensity 1.2, soft shadows, angle simulating window light
- **2-3 warm point lights**: Intensity 0.5-0.8, range 5-8m, placed at lamp/fixture positions for coziness
- **Ambient**: Override scene ambient to warm (0.25, 0.22, 0.20) — warmer than global default
- **Sky**: ToonSkyGradient visible through window — warm peach bottom, soft blue top, peachy horizon
- **Bake GI**: Yes — baked lightmaps for soft indirect bounce
- **SSAO**: Enabled (PC only), intensity 0.3 for subtle depth

#### 3D_Journal (Outdoor Beach/Island)
- **Directional light**: Golden hour ~3500K, intensity 1.5, 4-cascade shadows, low angle (30-40 degrees)
- **Ambient**: Warm sky ambient from gradient, equator color matching horizon
- **Sky**: ToonSkyGradient — peach/coral bottom, warm horizon band, soft blue-violet top
- **Water**: ToonWater with warm shallow color (0.5, 0.8, 0.85), gentle waves (amplitude 0.02, speed 1.0)
- **Fog**: Enable linear fog, warm tint (0.85, 0.8, 0.75), start 30m, end 80m — replaces DOF for distance softening
- **Bake GI**: Yes

#### 3D_LoginArea (Ethereal/Magical)
- **Directional light**: Cool blue-white ~6500K, intensity 0.8, subtle
- **2 warm spot lights**: Accent lights on focal points (pedestal/book), intensity 1.0, warm 3500K
- **Ambient**: Cool blue (0.18, 0.22, 0.28) for ethereal feel
- **Sky**: ToonSkyGradient — deep navy top, purple-blue horizon, warm amber bottom glow
- **Water ground**: ToonWater with dreamy teal shallow (0.3, 0.6, 0.75), slow gentle waves
- **Add Global Volume** (currently missing from this scene)

#### 3D_StartArea (Welcoming Entrance)
- **Directional light**: Balanced ~5000K, intensity 1.0
- **Ambient**: Neutral warm (0.22, 0.21, 0.20)
- **Sky**: ToonSkyGradient — clean blue top, white-peach horizon, warm bottom
- Minimal post-processing — clean, inviting first impression

### 4.3 Material Assignment Summary

| Material Type | Shader | Scenes Used |
|---|---|---|
| Room/building surfaces | MiSide/Environment (default toon) | Chat, Journal, Login, Start |
| Plants/foliage | MiSide/Environment (alpha cutout, cull off) | Journal |
| Lamps/monitors/emissive | MiSide/Environment (emission on) | Chat, Login |
| Water surfaces | MiSide/ToonWater | Journal, Login |
| Skybox | MiSide/ToonSkyGradient (per-scene material) | All 4 scenes |
| AZKi character (body, hair, clothes) | MiSide/Character | Chat, Journal |
| AZKi eyes | MiSide/Character (minimal shadow, no outline) | Chat, Journal |
| AZKi special (blush, lashes) | MiSide/Character (outline off, pass-through) | Chat, Journal |

---

## Phase 5: QA & VR Comfort [TODO]

- Verify single-pass instanced stereo: all shaders have `UNITY_VERTEX_INPUT_INSTANCE_ID`, `UNITY_TRANSFER_INSTANCE_ID`, `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO`, `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX`
- Verify SRP Batcher: identical CBUFFER across all passes of each shader
- Verify shader variant count: use `shader_feature_local` for material toggles, `multi_compile` only for global keywords
- Disable Motion Blur in `SampleSceneProfile.asset` (currently active at intensity 0.6)
- Test at 90Hz VR — profile with Frame Debugger

---

## Execution Order & Dependencies

```
Phase 1.1 (Common.hlsl) ─── must be first           [DONE]
  ├──> Phase 1.2 (Environment shader)                [DONE]
  │      ├──> Phase 1.3 (ShaderGUI)                  [DONE]
  │      └──> Phase 1.4 (Converter)                  [DONE]
  ├──> Phase 2.1 (Sky shader)                        [DONE]
  ├──> Phase 2.2 (Water shader)                      [DONE]
  └──> Phase 3.1 (Character shader)                  [DONE]
         ├──> Phase 3.2 (CharacterGUI)               [DONE]
         └──> Phase 3.3 (Tuner fix)                  [DONE]
Phase 4 (Volume profiles + scene setup)              [TODO — Unity Editor]
Phase 5 (QA)                                         [TODO]
```

## Critical Files

| File | Action | Status |
|---|---|---|
| `Assets/Shaders/MiSide_Common.hlsl` | **Create** — shared toon functions | DONE |
| `Assets/Shaders/MiSide_Environment.shader` | **Revise** — add lights, DepthNormals, fix shadow/GI, normal map | DONE |
| `Assets/Shaders/MiSide_ToonSkyGradient.shader` | **Revise** — add horizon color, haze | DONE |
| `Assets/Shaders/MiSide_ToonWater.shader` | **Revise** — add toon lighting, depth foam, specular | DONE |
| `Assets/Shaders/MiSide_Character.shader` | **Create** — 2-zone toon + outline | DONE |
| `Assets/Shaders/Editor/MiSideShaderGUI.cs` | **Revise** — add normal map section, remove ShadowIntensity | DONE |
| `Assets/Shaders/Editor/MiSideCharacterShaderGUI.cs` | **Create** — character shader inspector | DONE |
| `Assets/Shaders/Editor/MiSideCharacterTuner.cs` | **Revise** — fix path, add shader switch | DONE |
| `Assets/Shaders/Editor/MiSideMaterialConverter.cs` | **Revise** — remove ShadowIntensity refs | DONE |
| `Assets/Settings/SampleSceneProfile.asset` | **Fix** — disable Motion Blur | TODO |
| `Assets/Settings/VolumeProfile_3D_*.asset` (x4) | **Create** — per-scene post-processing | TODO |

## Verification

1. Open each scene in Unity Editor, verify no shader errors in Console
2. Assign MiSide/Environment to a test environment material — confirm toon shading, shadows, rim light, additional lights all work
3. Run MiSideCharacterTuner on AZKi materials — confirm shader switches to MiSide/Character and toon parameters apply
4. Enter VR Play mode in each scene — confirm stereo rendering, no visual artifacts, comfortable 90Hz
5. Check SRP Batcher panel (Frame Debugger) — all MiSide shaders should show "SRP Batcher compatible"
6. Verify SSAO works with new shaders (check DepthNormals output in Frame Debugger)
