# Shader & Lighting Improvement Plan — EMILIA-VR Calm Room

> **Purpose:** Analysis of current shader and lighting implementation in the 3D_Chat and 3D_Journal scenes, with improvement recommendations grounded in environmental psychology, VR UX, and Technical Art practice.
> **Target Experience:** A VR calm room where users feel safe enough to do reflection, journaling, and relaxation.

---

## Table of Contents

1. [Current System Analysis](#1-current-system-analysis)
2. [Per-Scene Analysis: 3D_Chat](#2-per-scene-analysis-3d_chat)
3. [Per-Scene Analysis: 3D_Journal](#3-per-scene-analysis-3d_journal)
4. [Shader Improvements](#4-shader-improvements)
5. [Lighting & Post-Processing Improvements](#5-lighting--post-processing-improvements)
6. [Implementation Reference](#6-implementation-reference)
7. [Priority Action Tiers](#7-priority-action-tiers)

---

## 1. Current System Analysis

### 1.1 Shared Foundation — `MiSide_Common.hlsl`

**What it does:** Provides three shared functions used by all MiSide shaders:
- `MiSideToonRamp()` / `MiSideToonRampHL()` — smoothstep-based soft toon ramp converting NdotL into a 0–1 shadow-to-lit blend. Half-Lambert (NdotL × 0.5 + 0.5) softens the transition versus raw Lambert, which is **correct for calm aesthetics** — hard shadow edges create visual tension that promotes alertness.
- `MiSideRimLight()` / `MiSideRimLightMasked()` — Fresnel-based rim glow. The masked variant suppresses rim on camera-facing surfaces, preventing the "uncanny glowing outline" effect on characters. This is **psychologically important** — uncontrolled rim highlights on eyes/faces trigger the "uncanny valley" discomfort.
- `MiSideAdditionalLights()` — loops through additional lights with a simplified single-step toon ramp. Supports Forward+ path. **Well-implemented** for multi-light scenes.

**Assessment:** The foundation is solid. Half-precision (`half`) types are mobile-friendly. The smoothstep ramp is the right choice for soft toon shading. No changes needed to the core functions themselves — improvements are needed at the shader and configuration levels.

### 1.2 Environment Shader — `MiSide_Environment.shader`

**What it does:** Single-zone toon shader for static props (walls, furniture, plants, lamps). Key features:
- Half-Lambert + smoothstep ramp with configurable `_ShadowStep` (0.5) and `_ShadowFeather` (0.05)
- **Integrated GI approach:** Lit path = `baseColor × (mainLight.color + bakedGI)`, Shadow path = `baseColor × _ShadowColor × saturate(bakedGI + 0.3)`. The `+ 0.3` ambient floor prevents pure-black shadows, which is **essential for calm** — dark voids in peripheral vision trigger threat detection in the amygdala.
- Warm shadow color default `(0.85, 0.75, 0.72, 1)` — psychologically effective. Warm shadows feel protective; cool/neutral shadows feel sterile or threatening.
- Optional rim light, emission, normal mapping, alpha cutout.
- All 5 URP passes present (ForwardLit, ShadowCaster, DepthOnly, DepthNormalsOnly, Meta) — **correct for SSAO and lightmap baking**.

**What works well for calm:**
- Shadow color warmth ✓
- GI integration preventing black shadows ✓
- Soft ramp via smoothstep ✓
- Emission support for cozy light sources ✓

**What needs improvement:**
- `_ShadowFeather` default of 0.05 is too tight — creates near-hard shadow boundaries
- No vertex color support — many 3D assets encode AO/tinting in vertex colors
- No translucency/backlight term for organic materials (leaves, curtains)
- No slow animated properties — static visuals feel "dead"

### 1.3 Character Shader — `MiSide_Character.shader`

**What it does:** 2-zone toon shader for the AZKi companion character. Key features:
- **2-zone ramp:** Zone 1 (lit ↔ 1st shade) and Zone 2 (1st shade ↔ 2nd shade), each with independent step/feather
- `_ShadowSaturation` preserves warm hue in shadows instead of desaturating to gray
- `_UnlitBlend` for eyes — keeps them vibrant regardless of light direction (critical: dead eyes in a companion character destroy trust)
- `_MinBrightness` floor — no surface goes below a threshold, preventing "gray void" areas
- Inverted-hull outline with distance-scaling for VR (screen-space consistent thickness)
- `_Is_BlendBaseColor` makes outlines color-match the surface (HSR-style), avoiding harsh black borders
- MatCap support, Blinn-Phong specular with toon stepping, normal mapping
- Full VR stereo instancing on all 6 passes

**What works well for calm:**
- Eye vibrancy via UnlitBlend ✓ — warm, "alive" eyes build trust essential for reflection activities
- Warm shadow tints ✓
- Min brightness prevents unsettling dark patches ✓
- Distance-scaled outlines avoid VR-specific "too thick / too thin" issues ✓

**What needs improvement:**
- `_GI_Intensity` default is 0.45 — character can appear darker than environment, breaking visual harmony
- Outline width default at 0.25 could be softer to integrate with the calm room environment
- No subsurface scattering approximation for skin (acceptable for toon style)

### 1.4 Sky Gradient Shader — `MiSide_ToonSkyGradient.shader`

**What it does:** Procedural 3-color vertical gradient (bottom → horizon → top) with atmospheric haze near the horizon. Uses smoothstep blending and exponential falloff for haze.

**Calm scene assessment:**
- 3D_Chat palette: Soft blue top (#8AAFC8) → soft peach horizon (#F0C4A0) → warm peach bottom (#E8A880). **Excellent** — warm-to-cool gradient evokes golden hour, universally associated with safety and winding-down.
- 3D_Journal palette: Blue-violet top (#8878B0) → warm gold horizon (#E8A06A) → peach coral bottom (#E08870). **Good** — sunset tones appropriate for contemplative journaling.
- Horizon haze at 0.3 adds atmospheric depth, signaling "warm humid air" — consistent with relaxed environments.

**What needs improvement:**
- Static sky with no temporal variation — a very slow color shift (imperceptible in real-time but noticeable over minutes) would mimic natural light progression, which sustains the Attention Restoration effect (Kaplan, 1995)
- 3D_Journal may benefit from slightly more haze (0.4–0.5) to reinforce the beach/humid atmosphere

### 1.5 Water Shader — `MiSide_ToonWater.shader`

**What it does:** Transparent toon water with dual-sine wave vertex displacement, depth-based shallow/deep color gradient, toon specular, scrolling foam, and depth-sampled shoreline foam. Supports additional lights for point/spot reflections.

**Calm assessment:**
- Toon specular creates "sparkle" effect on water — gentle glints are psychologically positive (associated with sunlit water, a strong Attention Restoration cue)
- Shoreline foam via depth sampling is convincing without extra geometry
- Transparent blend with fog integration

**What needs improvement:**
- Default `_WaveSpeed` (1.5) and `_WaveAmplitude` (0.03) are too energetic for a calm scene — rapid motion signals wind/urgency
- `_FoamSpeed` (0.08) too fast — foam should drift, not flow
- No environment reflection — water without sky reflection looks like colored glass
- No Fresnel term for transparency — water should be more transparent at steep view angles and more reflective at glancing angles

---

## 2. Per-Scene Analysis: 3D_Chat

**Intent:** Warm, safe, intimate indoor room — like a therapist's office or cozy living room. Users chat with the AI companion here.

### 2.1 Lighting Configuration

| Property | Current Value | Assessment |
|---|---|---|
| Ambient Sky Color | (0.251, 0.220, 0.200) | ✓ Warm amber — correct for cozy indoor |
| Skybox Material | MiSide_ToonSkyGradient | ✓ Soft peach/blue gradient visible through windows |
| Fog | None | ⚠ Indoor scenes don't need distance fog, but a very subtle atmospheric haze could add depth |
| Directional Light Color Temp | ~4500K implied | ✓ Warm white — inviting |
| Baked GI | 5 lightmaps + reflection probe | ✓ Proper baked setup |

### 2.2 Volume Profile (`Assets/Settings/3D_Chat.asset`)

| Override | Value | Assessment |
|---|---|---|
| Bloom threshold | 0.75 | ✓ Only bright emissives glow — prevents over-bloom |
| Bloom intensity | 0.25 | ⚠ Slightly high for VR — halos around bright objects lack proper stereo depth. Recommend 0.12–0.18 |
| Bloom scatter | 0.70 | ⚠ Wide scatter + 0.25 intensity may produce visible halos. Reduce to 0.55–0.60 |
| Tonemapping | Mode 1 (Neutral) | ✓ Correct — Neutral preserves soft pastels. ACES would crush them |
| Vignette intensity | 0.22 | ❌ **Too high for VR.** Meta VR guidelines recommend max 0.1 for static vignette. 0.22 creates visible darkening in peripheral vision, which in a VR headset becomes distractingly obvious and can cause eyestrain. Reduce to 0.08–0.10 |
| Vignette smoothness | 0.35 | ⚠ OK if intensity is reduced |
| Color contrast | -7 | ✓ Reduced contrast = softer image = less visual tension |
| Color saturation | -9 | ✓ Desaturated palette prevents sensory overload |
| White Balance temp | +10 | ⚠ +10 is quite warm (not 2500K — this is Unity's relative scale, adding warmth). Acceptable for a cozy room but verify visually that UI text remains readable. Recommend testing with +7 to +10 range |

### 2.3 Shader Usage in Scene

- **Environment objects:** MiSide/Environment shader with baked lightmaps. Shadow color warmth + GI integration creates unified soft lighting.
- **Character:** MiSide/Character shader. The 2-zone toon with UnlitBlend eyes ensures the companion looks warm and trustworthy — critical for a chat/reflection context.
- **Emissive objects:** Lamps/monitors should use MiSide/Environment with `_EMISSION` enabled and HDR emission color for bloom interaction.
- **Skybox:** MiSide/ToonSkyGradient with cozy sunset palette.

### 2.4 What's Working for Calm (3D_Chat)

1. Warm shadow tinting across all environment materials creates a unified "enveloped in warmth" feeling
2. Baked GI prevents harsh realtime shadow flickering — stability is essential for relaxation
3. Neutral tonemapping preserves the deliberately soft color palette
4. The character's eye shader keeps the companion "alive" — trust is the foundation for reflection activities
5. Reflection probe captures provide subtle environmental reflections on glossy surfaces

### 2.5 What Needs Improvement (3D_Chat)

1. **Vignette at 0.22 is a VR comfort issue** — peripheral darkening is much more noticeable inside a headset than on a flat screen
2. **Bloom may halo in stereo** — the bloom effect renders in screen-space without depth, creating a flat glow that the brain perceives as "wrong" in 3D
3. **No temporal light variation** — the room feels frozen in time. Even very slow intensity pulsing on warm point lights (±0.03, ~0.08 Hz) would add life. This is not a shader change but a script-driven material/light property animation
4. **No light cookies** — uniform point light illumination creates featureless light pools. A simple cookie texture (soft organic pattern) would add the visual complexity that natural environments provide
5. **Shadow feather on environment materials should be wider** — 0.05 is too crisp for a room that should feel soft and dreamy
6. **Character `_GI_Intensity` should match the room's ambient level** — if the room is bright with baked GI, the character at 0.45 GI may feel like a separately-lit cutout

---

## 3. Per-Scene Analysis: 3D_Journal

**Intent:** Open, contemplative, sunset warmth — like journaling on a peaceful beach at golden hour. Users write journal entries here.

### 3.1 Lighting Configuration

| Property | Current Value | Assessment |
|---|---|---|
| Ambient Sky Color | (0.290, 0.251, 0.271) | ✓ Warm neutral with slight pink — sunset appropriate |
| Ambient Equator | (0.439, 0.314, 0.188) | ✓ Golden tones — excellent golden hour cue |
| Fog mode | Linear, 30m–80m | ✓ Atmospheric depth — correct for beach/outdoor |
| Fog color | (0.851, 0.800, 0.749) | ✓ Warm golden haze — signals warm humid air |
| Skybox | MiSide_ToonSkyGradient | ✓ Blue-violet/gold/coral = sunset contemplation palette |

### 3.2 Volume Profile (`Assets/Settings/3D_Journal.asset`)

| Override | Value | Assessment |
|---|---|---|
| Bloom threshold | 0.70 | ✓ Slightly lower than Chat — catches more sunset glow. Appropriate for outdoor scene |
| Bloom intensity | 0.20 | ⚠ Same halo concern as Chat. Recommend 0.10–0.15 for VR |
| Bloom scatter | 0.70 | ⚠ Reduce to 0.50–0.60 |
| Tonemapping | Mode 1 (Neutral) | ✓ Correct |
| Vignette intensity | 0.15 | ⚠ Still above comfort threshold. Reduce to 0.08–0.10 |
| Vignette smoothness | 0.30 | OK |
| Color contrast | -5 | ✓ Slightly less reduced than Chat — outdoor scenes need marginally more contrast for depth perception |
| Color saturation | 0 | ✓ Neutral — sunset colors come from sky/lighting, not saturation boost |
| White Balance temp | +8 | ✓ Golden warmth without over-tinting. Good for golden hour |

### 3.3 Shader Usage in Scene

- **Environment:** MiSide/Environment for beach structures (cabana, racks, ground). Baked lightmaps with warm shadow color.
- **Vegetation:** MiSide/Environment with `_ALPHATEST_ON` for plants/palm leaves. Currently static — no wind animation.
- **Water:** MiSide/ToonWater with depth-based color, shoreline foam, dual-sine waves.
- **Sky:** MiSide/ToonSkyGradient with sunset palette + horizon haze.
- **Character:** MiSide/Character — same shader, potentially different GI intensity tuned for outdoor lighting.

### 3.4 What's Working for Calm (3D_Journal)

1. Linear fog at 30–80m with golden color creates atmospheric depth — the world "fades gently" into the horizon rather than ending abruptly
2. Sky gradient sunset palette is universally associated with winding-down and reflection
3. Ambient equator color at golden tones provides warm uplight from the ground plane
4. Water shader with shoreline foam creates a convincing beach setting
5. Toon specular on water produces pleasant sunlit sparkles

### 3.5 What Needs Improvement (3D_Journal)

1. **Water animation too energetic** — `_WaveSpeed` at 1.5 and `_FoamSpeed` at 0.08 contradict the "peaceful beach" goal. Calm water bodies have slow, breath-like undulation (research shows 0.1–0.3 Hz visual rhythms synchronize with relaxed breathing)
2. **Vignette at 0.15 still above VR comfort range** — reduce to 0.08–0.10
3. **Bloom scatter may produce halos around sun-facing surfaces**
4. **Water lacks environment reflection** — a beach without sky reflection in the water feels "wrong" to the brain. Even a simple Fresnel-based sky color lerp at 10–20% would dramatically improve believability
5. **Vegetation is static** — in an outdoor scene with water animation and fog, static trees/plants create an uncanny dissonance. A simple vertex shader wind sway (sine-based world-space offset) is recommended as a future enhancement
6. **Sky `_HorizonHaze` should be higher** — 0.40–0.45 on the 3D_Journal material instance to reinforce humid beach atmosphere
7. **Shadow feather should be even softer than indoor** — outdoor golden hour light produces extremely soft shadow transitions. Environment materials in this scene should use `_ShadowFeather` of 0.10–0.15

---

## 4. Shader Improvements

### 4.1 `MiSide_Environment.shader` — Increase Shadow Softness Defaults

**Current:** `_ShadowFeather ("Shadow Feather", Range(0.001, 0.5)) = 0.05`
**Proposed:** Change default to `0.10`

**Rationale:** A calm room should have dreamy, soft shadow transitions. The 0.05 default creates a near-hard edge that the human visual system detects as a "boundary" (edge detection is a primary function of V1 cortex). Doubling the feather blurs this boundary below the attention-capture threshold, allowing the eye to rest.

**Implementation:** In `MiSide_Environment.shader`, change the `_ShadowFeather` property default from `0.05` to `0.10`. Existing scene materials that already have serialized values will keep their values — this only affects newly created materials.

### 4.2 `MiSide_Environment.shader` — Add Vertex Color Support

**Current:** Vertex colors are ignored.
**Proposed:** Add `float4 color : COLOR` to the Attributes struct in the ForwardLit pass, pass it through Varyings, and multiply with `baseColor` in the fragment shader.

**Rationale:** Many purchased/downloaded 3D assets encode ambient occlusion, tinting, or blend weights in vertex colors. Without this, that data is wasted and environments appear flatter than intended. Baked vertex AO provides grounding depth at zero runtime cost — critical for Quest 3 performance.

**Implementation:**
- Add `float4 color : COLOR;` to `Attributes` in the ForwardLit pass
- Add `half4 vertexColor : COLOR;` to `Varyings`
- In vertex shader: `OUT.vertexColor = IN.color;`
- In fragment shader: `baseColor *= IN.vertexColor;`
- Repeat for the Meta pass to include vertex color in lightmap baking

### 4.3 `MiSide_Environment.shader` — Add Simple Translucency Keyword

**Current:** No backlight translucency effect.
**Proposed:** Add `[Toggle(_TRANSLUCENCY)] _TranslucencyToggle` with `_TranslucencyColor`, `_TranslucencyPower`, `_TranslucencyStrength` properties. In the fragment shader, compute a backlight term: `saturate(dot(viewDir, -mainLight.direction))^power × strength × translucencyColor`, and add it to `finalColor`.

**Rationale:** Leaves, curtains, and thin fabrics in the 3D_Journal and 3D_Chat scenes should glow softly when backlit (light passing through). This "living" quality is a strong calm environment cue — translucent surfaces signal organic, natural materials that the brain associates with nature and safety.

**Implementation:**
- Add property block under a `[Header(Translucency)]` section
- Add `#pragma shader_feature_local _TRANSLUCENCY`
- In fragment, inside `#ifdef _TRANSLUCENCY`: compute view-light dot product for backlight term, add to finalColor
- Default values: `_TranslucencyColor (0.8, 0.9, 0.6, 1)`, `_TranslucencyPower 3`, `_TranslucencyStrength 0.3`

### 4.4 `MiSide_ToonWater.shader` — Add Fresnel-Based Sky Reflection

**Current:** Water color is purely based on shallow/deep gradient + toon lighting. No reflection.
**Proposed:** Add a simple Fresnel reflection term that blends the sky/horizon color at glancing angles.

**Rationale:** Water without reflection contradicts deep-seated visual expectations. The brain expects to see sky reflected in water surfaces. This mismatch causes subtle unease rather than the calm that a beach scene intends. Even a fake reflection (lerp toward a `_ReflectionColor` based on Fresnel angle) dramatically improves water believability.

**Implementation:**
- Add properties: `_ReflectionColor ("Reflection Color", Color)`, `_ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.15`, `_FresnelPower ("Fresnel Power", Range(1, 10)) = 4`
- In fragment: `half fresnel = pow(1.0 - saturate(dot(viewDir, normalWS)), _FresnelPower);`
- Blend: `waterColor.rgb = lerp(waterColor.rgb, _ReflectionColor.rgb, fresnel * _ReflectionStrength);`
- Default reflection color should match scene's horizon color: warm golden for 3D_Journal

### 4.5 `MiSide_ToonWater.shader` — Calm Water Animation Defaults

**Current:** `_WaveSpeed = 1.5`, `_WaveAmplitude = 0.03`, `_FoamSpeed = 0.08`
**Proposed:** `_WaveSpeed = 0.4`, `_WaveAmplitude = 0.015`, `_FoamSpeed = 0.03`

**Rationale:** Research on restorative environments shows that slow rhythmic visual motion at 0.1–0.3 Hz frequency synchronizes with relaxed breathing patterns (6–8 breaths/minute). The current wave speed produces visible motion that demands attention tracking — the opposite of relaxation. Halved amplitude and one-quarter speed creates a "barely breathing" water surface that the user perceives as peaceful without consciously attending to it.

### 4.6 `MiSide_ToonSkyGradient.shader` — Horizon Haze for Outdoor Scenes

**Current:** `_HorizonHaze = 0.3`
**Proposed:** Keep 0.3 as default (appropriate for indoor). For 3D_Journal's material instance, set `_HorizonHaze` to 0.45 in the Unity Editor.

**Rationale:** Humid air near water bodies produces thicker atmospheric haze at the horizon. This visual cue signals "warm, moist, tropical atmosphere" — consistent with a beach retreat setting. The current 0.3 works for the indoor Chat scene (where sky is only seen through windows), but the Journal scene's open sky needs more atmospheric density.

### 4.7 `MiSide_Character.shader` — Raise Default GI Intensity

**Current:** `_GI_Intensity = 0.45`
**Proposed:** `_GI_Intensity = 0.55`

**Rationale:** The character must feel like they *belong* in the room, not like a separately-lit object pasted in. When the environment receives full baked GI but the character only picks up 45%, the brightness mismatch is subconsciously read as "this entity is not part of this space" — eroding trust. At 0.55, the character blends more naturally while still maintaining the slight contrast that makes the toon style readable.

### 4.8 `MiSide_Character.shader` — Soften Outline for Calm Context

**Current default:** `_Outline_Width = 0.25`
**Proposed:** `_Outline_Width = 0.18`

**Rationale:** In standard anime games, strong outlines enhance readability during fast action. In a calm room, the user is close to the character, often making eye contact during reflection/chat. At close VR distances, even the distance-scaled outline becomes prominent. A thinner outline reduces the "drawn on paper" feel and helps the character merge with the environment — promoting the sense that the companion is truly present in the space.

---

## 5. Lighting & Post-Processing Improvements

### 5.1 Critical VR Safety Fixes

| Fix | File | Change |
|---|---|---|
| **Reduce 3D_Chat vignette** | `Assets/Settings/3D_Chat.asset` | Vignette intensity: 0.22 → **0.10** |
| **Reduce 3D_Journal vignette** | `Assets/Settings/3D_Journal.asset` | Vignette intensity: 0.15 → **0.08** |
| **Verify SampleSceneProfile Motion Blur** | `Assets/Settings/SampleSceneProfile.asset` | Ensure MotionBlur override is deleted (not just disabled). If any scene accidentally references this profile, motion blur at 0.6 will cause nausea |
| **Verify DoF is OFF everywhere** | All volume profiles | Confirm no Depth of Field override exists. DoF fights VR eye accommodation |

### 5.2 Bloom Adjustments for VR Stereo

| Scene | Current | Proposed | Rationale |
|---|---|---|---|
| 3D_Chat | intensity 0.25, scatter 0.70 | intensity **0.15**, scatter **0.55** | Bloom renders per-eye without depth, creating flat halos that feel wrong in stereo 3D. Lower intensity + tighter scatter preserves the cozy glow from emissive lamps without creating distracting halos |
| 3D_Journal | intensity 0.20, scatter 0.70 | intensity **0.12**, scatter **0.55** | Same rationale. Outdoor scenes have more bright surfaces (sky, water specular), so lower bloom prevents the whole scene from looking "foggy" |

### 5.3 Lighting Enhancements

**3D_Chat — Add Warm Fill Light:**
- Add a secondary directional light at low intensity (0.15–0.25), warm color (~3200K), from the opposite direction of the main light
- This fills shadow regions with warm glow rather than relying solely on ambient color, creating the "multiple soft sources" feel of a therapist's office
- Alternative: increase `bakedGI + 0.3` floor in MiSide_Environment.shader to `bakedGI + 0.4` specifically for this scene via material override

**3D_Chat — Light Cookies:**
- Apply a soft organic pattern cookie to the directional light (simulating window blind shadows or foliage dappling)
- Fractal-like light patterns on surfaces reduce physiological stress markers (Salingaros, 2012) — the brain reads regular geometric patterns as artificial and organic patterns as natural

**3D_Journal — Enhance Golden Hour:**
- Add a low-angle (15–20° elevation) directional light with orange-gold tint to create long, dramatic but soft shadows
- Enable a cloud/foliage light cookie for dappled sunlight through vegetation
- Consider increasing fog near-plane from 30m to 20m for enhanced atmospheric envelopment

**Both Scenes — Slow Light Animation (Script-Driven):**
- A `SubtleLightBreathing.cs` script slowly oscillates point light intensity (±0.03) using `Mathf.Sin(Time.time * 0.08f * Mathf.PI * 2)` — this produces a 0.08 Hz cycle (~12.5 seconds), well below the conscious perception threshold but above the "nothing is happening" detection threshold
- Alternatively, slowly rotate the directional light 2–3° over 90 seconds and back — simulating cloud shadow movement

### 5.4 Post-Processing Additions

**Both Scenes — Shadows/Midtones/Highlights:**
- Push shadows slightly warm: shadow lift red +0.02, green +0.01
- Push highlights slightly cool: highlight gain blue +0.01
- Creates "warm close, cool far" depth cue that the brain naturally finds comfortable

**Optional — Very Subtle Film Grain (3D_Chat only):**
- Film grain type: Thin, intensity: 0.05–0.06
- Adds organic texture that reduces the "digital flatness" of 3D rendering
- Common in meditation/relaxation app aesthetics
- Skip for 3D_Journal — outdoor scenes with natural complexity don't need it

---

## 6. Implementation Reference

### Files Modified

| File | Changes | Priority |
|---|---|---|
| `Assets/Settings/3D_Chat.asset` | Vignette 0.22→0.10, bloom intensity 0.25→0.15, bloom scatter 0.70→0.55 | Critical + High |
| `Assets/Settings/3D_Journal.asset` | Vignette 0.15→0.08, bloom intensity 0.20→0.12, bloom scatter 0.70→0.55 | Critical + High |
| `Assets/Settings/SampleSceneProfile.asset` | Motion Blur override removed | Critical |
| `Assets/Shaders/MiSide_Environment.shader` | Default `_ShadowFeather` 0.05→0.10, vertex color support, `_TRANSLUCENCY` keyword + properties | High + Medium |
| `Assets/Shaders/MiSide_ToonWater.shader` | Defaults: wave speed 1.5→0.4, amplitude 0.03→0.015, foam speed 0.08→0.03; Fresnel reflection | High |
| `Assets/Shaders/MiSide_Character.shader` | Default `_GI_Intensity` 0.45→0.55, `_Outline_Width` 0.25→0.18 | Medium |
| `Assets/Shaders/MiSide_Common.hlsl` | No changes needed | — |
| `Assets/Shaders/MiSide_ToonSkyGradient.shader` | No code change — adjust 3D_Journal material instance `_HorizonHaze` to 0.45 in Editor | Medium |

### New Files Created

| File | Purpose | Priority |
|---|---|---|
| `Assets/Scripts/SubtleLightBreathing.cs` | Slow sine-wave pulsing of point light intensity | Medium |

---

## 7. Priority Action Tiers

### Tier 1 — Critical (VR Safety)
1. ✅ Reduce 3D_Chat vignette from 0.22 → 0.10
2. ✅ Reduce 3D_Journal vignette from 0.15 → 0.08
3. ✅ Remove Motion Blur override from SampleSceneProfile.asset
4. Verify Depth of Field is OFF in all volume profiles (editor check)

### Tier 2 — High (Calm Experience Fundamentals)
5. ✅ Reduce water animation defaults: `_WaveSpeed` → 0.4, `_WaveAmplitude` → 0.015, `_FoamSpeed` → 0.03
6. ✅ Reduce bloom intensity/scatter in both scenes
7. ✅ Increase `_ShadowFeather` default to 0.10 on MiSide/Environment shader
8. Set 3D_Chat environment materials' shadow feather to 0.10–0.12 in Editor
9. Set 3D_Journal environment materials' shadow feather to 0.12–0.15 in Editor
10. Add warm fill light (secondary directional) to 3D_Chat scene in Editor

### Tier 3 — Medium (Psychology & Polish)
11. ✅ Add Fresnel sky reflection to ToonWater shader
12. ✅ Add vertex color support to MiSide/Environment shader
13. ✅ Increase character `_GI_Intensity` default to 0.55
14. ✅ Reduce character `_Outline_Width` default to 0.18
15. Add Shadows/Midtones/Highlights post-processing to both volume profiles (editor)
16. Increase 3D_Journal sky material `_HorizonHaze` to 0.45 (editor)
17. ✅ Add slow light breathing script for point lights
18. Add light cookie to 3D_Chat directional light (editor asset)

### Tier 4 — Enhancement (When Fundamentals Are Solid)
19. ✅ Add `_TRANSLUCENCY` keyword to MiSide/Environment for organic materials
20. Add vegetation wind sway (vertex animation) for 3D_Journal plants
21. Add subtle film grain to 3D_Chat volume profile
22. Add underwater caustics for any future water-floor interactions
23. Add dynamic locomotion vignette (movement-triggered, not static)

---

> **References:** Kaplan, 1995 (Attention Restoration Theory); Ulrich, 1991 (Stress Reduction Theory); Alvarsson et al., 2010 (Nature soundscapes and stress recovery); Salingaros, 2012 (Fractal patterns and stress reduction); Meta Quest VR Best Practices (vignette, motion blur, DoF guidelines).
