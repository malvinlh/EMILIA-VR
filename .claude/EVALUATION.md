# EMILIA-VR: Shader & Lighting Evaluation

> **Goal:** Evoke a relaxing, calming feeling in the user as they reflect and traverse a 3D VR Calm Room.
> **Evaluated from:** Technical Art, Environmental Psychology, and VR UX Design perspectives.
> **Target platform:** Meta Quest 3 (standalone Android via OpenXR)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Lighting & Atmosphere](#2-lighting--atmosphere)
3. [Shader Quality & Calm Aesthetics](#3-shader-quality--calm-aesthetics)
4. [Post-Processing & Color Grading](#4-post-processing--color-grading)
5. [Water & Natural Elements](#5-water--natural-elements)
6. [Audio-Visual Coherence](#6-audio-visual-coherence)
7. [VR Comfort & Safety](#7-vr-comfort--safety)
8. [Performance on Quest 3](#8-performance-on-quest-3)
9. [Scene-by-Scene Recommendations](#9-scene-by-scene-recommendations)
10. [Priority Action Items](#10-priority-action-items)

---

## 1. Executive Summary

The project has a solid technical foundation: custom toon shaders (MiSide/Environment, Character, ToonWater, ToonSkyGradient) are well-structured, SRP-Batcher compatible, and VR-stereo-aware. The setup guide (`Assets/Shaders/setup-guide.md`) demonstrates thoughtful per-scene volume profile planning. However, several gaps remain between the *technical plan* and what would truly evoke a sustained calming experience in VR. The issues fall into three categories:

- **Atmosphere gaps:** Lighting is configured but lacks slow temporal dynamics (animated light, gentle color shifts) that the psychology of calm environments relies on.
- **Sensory incompleteness:** The calm room concept needs more layered ambient feedback — the visual system carries almost all the weight while audio and haptic channels are underutilized.
- **Quest 3 reality check:** Some planned features (SSAO, 4-cascade shadows, 2048 shadow maps) are PC-oriented and will either not run or degrade the 72/90 Hz framerate needed for VR comfort on Quest 3.

---

## 2. Lighting & Atmosphere

### What's Working
- Per-scene color temperature differentiation (4500K Chat, 3500K Journal, 6500K Login, 5000K Start) follows environmental psychology principles — warm tones promote relaxation, cool tones create contemplation.
- Baked GI with Baked Indirect mode is the right choice for static room interiors.
- Soft shadows (no hard edges) align with the calming goal.

### What Needs Revision

| Issue | Why It Matters (Psychology/UX) | Recommendation |
|---|---|---|
| **Static lighting with no temporal variation** | Calm environments in nature are never static — sunlight shifts, candle flames flicker, clouds pass. Static lighting feels "dead" and subconsciously prevents full relaxation. Research on restorative environments (Kaplan, 1995) shows that gentle, predictable change sustains attention restoration. | Add a slow animated light script: rotate the directional light 2-5° over 60-90 seconds, or gently pulse point light intensity (±0.05) with a sine wave at ~0.1 Hz. Keep changes sub-perceptual threshold to avoid distraction. |
| **No warm fill light opposing the main light** | Single-source lighting creates harsh shadow-to-light ratios even with baked GI. In calming spaces (spas, therapy rooms), ambient fill from multiple directions prevents any surface from feeling "abandoned." | Add a very low-intensity (0.2-0.3) secondary directional or hemisphere light with warm color (~3000K) from the opposite direction. This fills shadow regions with a warm glow rather than relying solely on ambient color. |
| **3D_LoginArea uses 6500K cool light at 0.8 intensity** | Cool-white light (6500K) is associated with alertness, clinical settings, and increased cortisol. For a login/entry area that first greets the user, this can create an unwelcoming, anxious first impression. | Warm the Login light to ~5000-5500K and raise intensity to 1.0. Reserve the cool-ethereal palette for accent lights, not the dominant illumination. First impressions set the emotional baseline for the entire session. |
| **No light cookies or projected patterns** | Flat, uniform light lacks the visual complexity that natural environments provide (dappled sunlight, window shadow patterns). Environmental psychology research shows fractal-like light patterns reduce physiological stress markers. | Apply a simple light cookie texture to the directional light in 3D_Chat (simulating window blinds or foliage shadows) and in 3D_Journal (leaf dappling). Use soft, organic patterns — avoid geometric precision. |
| **Point lights in Chat scene lack falloff visualization** | 0.5-0.8 intensity point lights at 5-8m range may create visible attenuation boundaries — the "edge of light" effect — which draws attention and feels artificial. | Use quadratic falloff with a gentle fade. Consider setting range slightly larger (10-12m) with lower intensity (0.3-0.5) so the light "disappears" before its boundary becomes visible. |

### Missing Lighting Elements

- **Emissive accent objects:** The setup guide mentions emission support in MiSide/Environment but no specific emissive objects are called out. Add warm-glowing lamps, soft LED strips, or illuminated panels in 3D_Chat to provide cozy focal points.
- **Light probes for dynamic objects:** The AZKi character is dynamic and won't receive baked GI. Place light probes in a grid (1-2m spacing) around character positions so she receives believable ambient illumination, not just direct light + black ambient.

---

## 3. Shader Quality & Calm Aesthetics

### What's Working
- The toon shading approach (half-Lambert with smoothstep ramp) naturally softens harsh lighting, which aligns with the calm aesthetic.
- Shadow color tinting (warm `(0.85, 0.75, 0.72)` in Environment shader) is psychologically effective — warm shadows feel protective, not threatening.
- Rim light with inside mask on the character shader prevents the "glowing outline" effect that can feel eerie.

### What Needs Revision

| Issue | Why It Matters | Recommendation |
|---|---|---|
| **Environment shader shadow feather too tight (0.05 default)** | A 0.05 feather creates a nearly hard shadow boundary. In calm environments, hard edges create visual tension. The eye is drawn to contrast boundaries, which promotes alertness rather than relaxation. | Increase default `_ShadowFeather` to 0.08-0.12 for environment materials. Wider feathering creates dreamy, soft transitions that psychologically signal "safe, gentle space." |
| **No ambient occlusion baked into environment textures** | SSAO is planned as a renderer feature but is expensive on Quest 3. Without either SSAO or baked AO, corners and crevices lack depth, making rooms feel flat and unrealistic. | Bake AO into environment textures or use a separate AO map channel. This costs zero runtime and provides the grounding depth that SSAO would have delivered. Alternatively, use lightmap AO baking in Unity's Lighting settings. |
| **Character outline may feel harsh in calm context** | The MiSide/Character shader has a 0.3 outline width with dark color. In anime/game contexts this is standard, but in a calm room designed for relaxation, strong outlines can make the character feel like a cutout — detached from the environment rather than part of it. | Reduce outline width to 0.15-0.2 and tint the outline color toward the scene's ambient hue (warm brown in Chat, golden in Journal) rather than pure dark. Enable `_Is_BlendBaseColor` to let the outline harmonize with the character's local color. |
| **No sub-surface scattering approximation for organic materials** | Plants, curtains, and skin benefit from a translucency effect when backlit. Without it, organic objects feel opaque and "dead." Living, breathing environments promote calm. | Add a simple translucency term to MiSide/Environment: `half backlight = saturate(dot(viewDir, -lightDir)) * thickness * translucencyColor`. Enable it via a `_TRANSLUCENCY` keyword for select materials (leaves, curtains). |
| **GI intensity on character is 0.3 — too dim** | The character shader's `_GI_Intensity` at 0.3 means baked ambient only contributes 30%. In a softly-lit room, this makes the character unnaturally dark compared to the environment, breaking visual harmony. | Raise `_GI_Intensity` to 0.5-0.7 so the character better matches the room's ambient light level. The character should feel like they belong in the space, not like a separately-lit object. |

### Missing Shader Features

- **Vertex color support for environment shader:** Many 3D assets encode AO, tinting, or blending data in vertex colors. The MiSide/Environment shader currently ignores vertex colors. Adding `float4 color : COLOR` to the Attributes struct and multiplying with base color would unlock significant per-vertex detail without additional textures.
- **Detail/secondary UV support:** For large surfaces (walls, floors), a tiled detail texture prevents blurriness at close VR inspection distances. Without this, standing close to a wall reveals texture stretching, which breaks immersion.

---

## 4. Post-Processing & Color Grading

### What's Working
- Neutral tonemapping (not ACES) is correct for pastel/soft aesthetics — ACES would crush the gentle tones.
- Vignette kept below 0.1 is VR-appropriate.
- Desaturated palette (saturation -5 in Chat/Login) prevents sensory overload.

### What Needs Revision

| Issue | Why It Matters | Recommendation |
|---|---|---|
| **SampleSceneProfile.asset has Motion Blur at 0.6** | Motion blur in VR causes nausea. Even if this profile isn't actively assigned, if any scene references it (intentionally or accidentally), users will experience discomfort. This is a **critical VR safety issue.** | Immediately set Motion Blur intensity to 0 or remove the override entirely from `Assets/Settings/SampleSceneProfile.asset`. Do not merely disable it — delete the override to prevent accidental re-activation. |
| **3D_Chat tonemapping shows Mode 1 in profile (may be ACES)** | The setup guide specifies Neutral tonemapping, but the actual profile asset reportedly uses Mode 1, which may correspond to ACES in Unity 6000.x. ACES increases contrast and color saturation, fighting the "soft pastel" goal. | Verify and force Neutral tonemapping in the 3D_Chat volume profile. Test by comparing a pastel-colored material under both modes. |
| **White Balance temperature of 2.5K in Chat profile is extremely warm** | 2.5K color temperature approximates candlelight, which may over-tint the scene with orange/amber and make UI text difficult to read. While warm = calming, extreme warmth = claustrophobic and tiring. | Adjust to 5-8 range as specified in the setup guide (the profile may have drifted). Target a "golden hour" warmth, not a "inside a furnace" warmth. |
| **No Color Curves or Lift/Gamma/Gain shaping** | The shadows are tinted via the shader's `_ShadowColor` property, but post-processing shadows remain neutral. Slightly warm shadows and slightly cool highlights in post creates depth and visual comfort (warm = close/safe, cool = distant/open). | Add Lift/Gamma/Gain or Shadows/Midtones/Highlights override: push shadows slightly warm (+0.02 red, +0.01 green), push highlights slightly cool (+0.01 blue). Keep adjustments subtle. |
| **Bloom scatter values may cause halo artifacts in VR** | Bloom scatter at 0.60-0.75 with intensity 0.10-0.25 can create visible halos around bright objects, which in VR stereoscopy can cause eye strain because the halo doesn't have proper depth. | Test bloom in headset. If halos are visible, reduce scatter to 0.50-0.55 and intensity to 0.08-0.12. Bloom should add "glow" without creating distinct halos. |

### Missing Post-Processing

- **Film Grain (very subtle):** A tiny amount of film grain (0.05-0.08 intensity, "thin" type) can add organic texture to the image that subconsciously reduces the "digital flatness" of 3D rendering. This is widely used in meditation and relaxation apps.
- **Panini Projection or Lens Distortion (off):** Verify these are disabled. Any barrel/pincushion distortion on top of the VR headset's own lens correction will cause nausea.

---

## 5. Water & Natural Elements

### What's Working
- ToonWater shader has proper dual-sine wave animation, depth-based coloring, and shoreline foam — all good for a natural feel.
- WaterFootstepEffect.cs provides embodied interaction (feeling your presence affect the environment is grounding).
- Object pooling for ripples is performance-conscious.

### What Needs Revision

| Issue | Why It Matters | Recommendation |
|---|---|---|
| **Wave speed 1.5 is too fast for calm water** | Rapid wave motion communicates energy, wind, and urgency. Calm water bodies (ponds, protected coves) have very slow, gentle undulation. Research shows that slow rhythmic visual motion (0.1-0.3 Hz) synchronizes with relaxed breathing patterns. | Reduce `_WaveSpeed` to 0.3-0.5 and `_WaveAmplitude` to 0.01-0.015 for calm scenes. The water should barely move — like breathing. |
| **Foam scroll speed 0.08 may be too fast for calm beach** | Combined with wave animation, dual-speed movement creates visual complexity that demands attention rather than allowing it to rest. | Reduce `_FoamSpeed` to 0.03-0.04 in calm scenes. Foam should drift, not flow. |
| **Water has no reflection or environment sampling** | Water without any reflection of the sky or surroundings looks like colored glass rather than a natural element. Reflection provides a sense of "openness" (seeing sky below) that psychologically expands perceived space. | Add a simple environment reflection: sample the skybox cubemap or use a planar probe, blend it at 10-20% with the water color based on the Fresnel angle. Even a fake reflection (lerp toward sky color at glancing angles) would help. |
| **Splash SFX volume 0.4 with sudden onset** | Sudden sounds are startle responses. In VR where audio is spatialized, an unexpected splash at 0.4 volume can jolt the user out of a calm state. | Reduce volume to 0.15-0.25 and add a 50ms fade-in on the AudioSource (attack time). Use the gentlest splash clips available. Consider adding a continuous gentle lapping sound instead of only event-driven splashes. |
| **Ripple duration 0.8s is too fast** | Quick ripple expansion and disappearance feels frantic. Natural ripples in still water persist for 3-5 seconds. | Increase `rippleDuration` to 2.0-3.0 seconds with a very gentle fade-out curve. The ripple should linger and slowly dissolve. |
| **No underwater caustics** | In the LoginArea (ethereal water ground), caustic light patterns projected onto the floor beneath the water would create the dancing-light effect that is universally associated with calm water environments. | Add a simple projected caustic texture (animated UV offset) either as a decal or as a shader feature on the floor material beneath water. |

---

## 6. Audio-Visual Coherence

### What's Currently Missing

The calm room experience is heavily vision-centric. Effective calming environments engage multiple senses in coherent, predictable ways:

| Missing Element | Psychological Basis | Recommendation |
|---|---|---|
| **Continuous ambient soundscape** | The BGM folder is empty. Silence in VR is not calming — it's isolating. Without ambient audio, the user becomes hyper-aware of real-world sounds leaking through the headset, breaking immersion. Nature soundscapes reduce cortisol and heart rate (Alvarsson et al., 2010). | Add looping ambient audio per scene: 3D_Chat = soft rain or fireplace crackle, 3D_Journal = ocean waves + distant seabirds, 3D_LoginArea = ethereal drone/pad + gentle water, 3D_StartArea = wind through leaves. Layer 2-3 sounds per scene for depth. |
| **No visual-audio synchronization** | When water waves and water sounds are independent, the mismatch creates cognitive dissonance. The brain expects wave crests to correspond with sound peaks. | Sync wave animation timing with the ambient ocean loop in 3D_Journal. Or use the wave shader's time to modulate audio volume slightly. |
| **No particle effects for atmosphere** | Floating dust motes in indoor light beams, fireflies at dusk, slowly falling leaves — these ambient particles add "life" without demanding attention. They provide gentle visual motion that supports relaxation. | Add subtle particle systems: dust motes in 3D_Chat light beams (small, slow, warm-tinted), fireflies or floating light particles in 3D_LoginArea, pollen/petals drifting in 3D_Journal wind. Keep particle count low (20-50) and speed very slow. |
| **No breathing or pulsing UI feedback** | If any UI is visible during the calm experience, static UI feels mechanical. Gentle pulsing (opacity breathing at ~0.15 Hz matching normal breathing rate) can serve as an unconscious breathing pacer. | If there are ambient UI elements, animate their opacity with a slow sine wave (6-7 second cycle). This subtly guides the user toward a relaxed breathing rate. |

---

## 7. VR Comfort & Safety

### Critical Issues

| Issue | Severity | Action |
|---|---|---|
| **Motion Blur at 0.6 in SampleSceneProfile** | **CRITICAL** | Remove immediately. Motion blur in VR causes simulator sickness. |
| **Depth of Field must remain OFF** | HIGH | Verify no volume profile enables DoF. DoF fights the eye's natural accommodation in VR and causes discomfort. |
| **Vignette above 0.1 in any scene** | MEDIUM | Current values (0.05-0.08) are safe. Add a maximum clamp in code if values are exposed to designers. |

### Comfort Recommendations

| Area | Recommendation |
|---|---|
| **Locomotion vignette** | If the user moves via thumbstick/teleport, apply a dynamic tunnel vignette (0.3-0.4 intensity, fast onset/decay) during movement only. This is the #1 comfort technique for VR locomotion. Static vignette alone doesn't help with motion-induced nausea. |
| **Stable horizon reference** | Ensure the skybox gradient's horizon line is always visible and level. In enclosed rooms (3D_Chat), add a window or opening that reveals the horizon. Vestibular-visual mismatch is reduced when the brain can reference a stable horizon. |
| **Fixed-world UI anchoring** | Any UI panels should be world-anchored (not head-locked) to avoid causing nausea from lag between head movement and UI tracking. |
| **Brightness adaptation** | When transitioning between scenes (Login → Chat), avoid abrupt brightness changes. Add a 1-2 second cross-fade or gradual exposure ramp to prevent pupil shock. |

---

## 8. Performance on Quest 3

### Critical Performance Issues

The current configuration in `setup-guide.md` is designed for PC VR. Meta Quest 3 is a standalone mobile device with a Snapdragon XR2 Gen 2 chip. Several settings will fail to maintain 72/90/120 Hz:

| Setting | Current Value | Quest 3 Risk | Recommendation |
|---|---|---|---|
| **Shadow map resolution** | 2048×2048 (main + additional) | HIGH — mobile GPU cannot sustain 2K shadow maps at 90 Hz stereo rendering | Use 1024×1024 for main light, 512 for additionals. Use Mobile_RPAsset for Quest builds. |
| **Shadow cascades** | 4 | HIGH — each cascade multiplies shadow rendering cost | Use 2 cascades maximum on Quest 3. Shadow distance can stay at 50m if cascades are reduced. |
| **SSAO renderer feature** | Enabled, intensity 0.3 | HIGH — SSAO is a full-screen post-process that runs per-eye. On Quest 3, this can drop 10-15 fps. | Disable SSAO on Quest 3 builds. Use baked AO in lightmaps or textures instead. SSAO is a PC-only luxury. |
| **Soft shadows quality** | 3 (highest) | MEDIUM — high-quality soft shadows use more PCF samples | Set to 1 (low) on Quest 3. The toon aesthetic's shadow feathering already softens edges, making high PCF samples redundant. |
| **Additional lights cap** | 3 in Forward mode | MEDIUM — each additional light multiplies fragment work | Cap at 2 on Quest 3. Use baked lights for accent lighting instead of realtime point/spot lights. |
| **HDR rendering** | Enabled | LOW-MEDIUM — HDR requires 16-bit framebuffer, doubling bandwidth | Consider LDR on Quest 3 if bloom is not critical. Alternatively, keep HDR but reduce render scale to 0.9. |

### Quest 3 Optimization Checklist

- [ ] Create a separate URP asset for Quest 3 (or use the existing `Mobile_RPAsset`) with the above adjustments
- [ ] Set the Mobile renderer as default for Android platform in Quality settings
- [ ] Bake all accent lights (point lights in Chat, spot lights in Login) instead of running them realtime
- [ ] Ensure Single-Pass Instanced stereo rendering is active (not Multi-Pass)
- [ ] Target 90 Hz refresh rate, drop to 72 Hz as fallback
- [ ] Profile with Meta Quest Developer Hub (MQBH) GPU profiler to identify overdraw hotspots, especially from the transparent ToonWater shader

---

## 9. Scene-by-Scene Recommendations

### 3D_Chat (Indoor Cozy Room)

**Current mood target:** Warm, safe, intimate — like a therapist's office or cozy living room.

| What to Revise | Details |
|---|---|
| **Add a visible warm light source** | Place a lamp model with an emissive material (MiSide/Environment with `_EMISSION` enabled, HDR color ~(1.0, 0.8, 0.5) × 2.0). The user should be able to see *where* the warmth comes from — an identifiable light source creates psychological "control" (the user understands the environment). |
| **Add a window or opening** | Even if it shows only the ToonSky gradient, a window provides: (a) a horizon reference for VR comfort, (b) a sense of "I can leave" which reduces claustrophobia, (c) depth variety that rests the eyes. |
| **Warm the shadow color further** | Current `(0.85, 0.75, 0.72)` is good. Push slightly to `(0.88, 0.78, 0.72)` for more warmth. Shadows in firelit rooms lean more amber. |
| **Add subtle dust motes particle system** | 20-30 particles, very slow drift (0.01-0.02 m/s), warm-tinted, visible in light beams. This is the single most effective "cozy room" visual cue. |
| **Ambient sound: soft rain or fireplace** | Layer a rain loop (low volume, slightly muffled as if heard through walls) with occasional distant thunder (very faint). This creates a "shelter" feeling — you're safe inside while it's raining outside. |

### 3D_Journal (Beach/Island/Farm)

**Current mood target:** Open, contemplative, sunset warmth — like journaling on a peaceful beach.

| What to Revise | Details |
|---|---|
| **Slow down everything** | Wave speed → 0.3, foam speed → 0.03, any wind animation → half speed. The beach should feel like time has slowed down. |
| **Enhance the golden hour lighting** | The 3500K/1.5 intensity is good. Add a second warm light from low angle (X = 15-20°) with orange tint to create long, dramatic but soft shadows. Enable light cookie with a cloud/foliage pattern for dappled light. |
| **Add fog with warm tint** | The setup guide mentions fog (linear, 30-80m) with `(0.85, 0.8, 0.75)` — ensure this is implemented. Fog softens the horizon and creates atmospheric depth that cues relaxation. |
| **Ocean ambient sound is essential** | This scene absolutely needs a continuous ocean wave loop. Without it, a beach is psychologically wrong — the brain expects ocean sounds. This mismatch causes unease rather than calm. |
| **Add distant horizon haze** | Increase `_HorizonHaze` on the sky shader to 0.4-0.5 for this scene. Hazy horizons signal humid, warm air — consistent with a beach setting. |
| **Vegetation should sway** | If trees/plants are present, add a simple vertex-animated wind sway (sine-based world-space offset in the vertex shader). Static vegetation in an outdoor scene feels uncanny. |

### 3D_LoginArea (Ethereal/Contemplative)

**Current mood target:** Mystical, welcoming threshold — the transition into the calm space.

| What to Revise | Details |
|---|---|
| **Warm up the dominant light** | As noted in Section 2, 6500K is too clinical. Shift to 5000-5500K. Keep the ethereal feel through accent lighting (cool spots) and water color, not the main illumination. |
| **Add Global Volume** | The setup guide notes this scene has NO Global Volume. This means no bloom, no tonemapping, no color grading — the scene will look raw and flat compared to others. Immediate fix. |
| **Add underwater caustics** | The water-ground environment is perfect for caustic patterns. Project an animated caustic texture onto surfaces below water level. This creates the mesmerizing "swimming pool ceiling" light dance. |
| **Floating light particles** | Add 30-50 slowly floating orbs of warm light (small emissive spheres or billboard particles). These create a sense of magic/wonder that eases the user from the real world into the VR calm space. |
| **Ambient pad sound** | A slow, evolving synth pad (C major, no dissonance) with reverb creates the "liminal space" feeling appropriate for a login area. Layer with very quiet water drops. |

### 3D_StartArea (Hub/Entry)

**Current mood target:** Neutral, clear, inviting — the user chooses where to go.

| What to Revise | Details |
|---|---|
| **Ensure visual clarity** | This is a decision space. Lighting should be balanced (5000K) with good visibility. Don't over-stylize. |
| **Wayfinding through light** | Use warmer accent lights near doors/portals to other scenes. Users naturally move toward warm light. This replaces the need for explicit UI arrows. |
| **Transition preview** | If possible, let the sky gradient or light color near each door hint at the destination scene's palette. This sets expectations and reduces the "shock" of scene transitions. |
| **Brief ambient music** | A simple, clean ambient loop (piano + pad) at low volume. This space should feel like a calm lobby, not dramatic. |

---

## 10. Priority Action Items

### Immediately (Critical / VR Safety)

1. **Remove Motion Blur** from `Assets/Settings/SampleSceneProfile.asset` — set intensity to 0 or delete the override
2. **Add Global Volume** to 3D_LoginArea scene with the planned volume profile
3. **Verify Depth of Field is OFF** in all volume profiles
4. **Verify tonemapping is Neutral** (not ACES) in the 3D_Chat volume profile

### High Priority (Calm Experience Fundamentals)

5. **Add ambient soundscapes** to all 4 scenes — this is the single biggest gap in the calm room experience
6. **Slow down water animation** — reduce `_WaveSpeed` to 0.3-0.5, `_FoamSpeed` to 0.03-0.04
7. **Increase shadow feathering** on environment materials — `_ShadowFeather` from 0.05 to 0.08-0.12
8. **Add subtle dust/particle effects** to at least 3D_Chat and 3D_LoginArea
9. **Create Quest 3 URP asset** with reduced shadow resolution, 2 cascades, no SSAO

### Medium Priority (Polish & Psychology)

10. **Add slow light animation** — gentle directional light rotation or point light intensity pulsing
11. **Add light cookies** for dappled/patterned lighting in Chat and Journal scenes
12. **Soften character outlines** — reduce width to 0.15-0.2, tint toward scene ambient color
13. **Raise character GI intensity** from 0.3 to 0.5-0.7
14. **Add water sky reflection** — even a simple Fresnel-based sky color blend
15. **Reduce splash SFX volume** to 0.15-0.25 and lengthen ripple duration to 2-3 seconds
16. **Fix White Balance** in Chat profile — 2.5K temperature is too extreme, target 5-8 as setup guide specifies

### Low Priority (Enhancement)

17. Add vertex color support to MiSide/Environment shader
18. Add translucency keyword for organic materials (leaves, curtains)
19. Add detail/secondary UV tiling for close-range surface inspection
20. Add caustic projection for LoginArea water ground
21. Add vegetation wind sway vertex animation for Journal scene
22. Add dynamic locomotion vignette for movement comfort
23. Consider film grain post-processing (0.05-0.08, very subtle)

---

---

## Appendix: Changes Already Implemented

The following issues from this evaluation have been addressed in code:

### Character Shader Overhaul (`Assets/Shaders/MiSide_Character.shader`)

**Problem:** Eyes appeared grayed out and dead due to the Eyes preset setting `_1st_ShadeColor_Step = 0.8`, which pushed recessed eye geometry permanently into shadow. The character looked creepy rather than warm and welcoming.

**Changes made:**
- Added `_UnlitBlend` property (0-1): lerps between toon-shaded and flat-lit base color. Eyes use 0.75 to stay vibrant regardless of light direction.
- Added `_MinBrightness` property (0-1): floor brightness guarantee so no surface goes dead-gray. Eyes use 0.35, skin uses 0.15.
- Added `_ShadowSaturation` property (0-2): preserves warm hue in shadow zones instead of desaturating toward gray.
- Warmer default shade colors: 1st shade `(0.92, 0.84, 0.80)` instead of `(0.9, 0.85, 0.82)`, 2nd shade `(0.83, 0.74, 0.70)` instead of `(0.8, 0.72, 0.68)`.
- Increased default GI intensity from 0.3 to 0.5 (character matches room ambient better).
- Softer default outline: width 0.2 (was 0.3), `_Is_BlendBaseColor` on by default (outline harmonizes with character color).
- Softer feathering defaults: 0.08/0.12 (was 0.06/0.10).

### Eyes Preset Fix (`Assets/Shaders/Editor/MiSideCharacterShaderGUI.cs`)

**Problem:** Eyes preset set `_1st_ShadeColor_Step = 0.8` (almost everything in shadow) with no outline and no rim — creating "dead doll eyes."

**Fix:** Eyes preset now uses:
- `_1st_ShadeColor_Step = 0.15` (almost nothing enters shadow)
- `_UnlitBlend = 0.75` (eyes are 75% unlit — always vibrant)
- `_MinBrightness = 0.35` (eyes never go below 35% brightness)
- `_Tweak_SystemShadowsLevel = 0.5` (realtime shadows barely affect eyes)
- Very light shade colors `(0.95, 0.92, 0.90)` for the remaining 25% toon influence

### Character Material Tuner Rewrite (`Assets/Shaders/Editor/MiSideCharacterTuner.cs`)

**Improvements:**
- 3-step workflow: Scan & Preview → Override detections → Apply All
- Multi-strategy auto-detection: Japanese name matching → English name matching → texture filename heuristics → texture average color analysis (skin tone detection)
- Visual preview grid with color-coded categories and per-material override dropdowns
- Undo support for all material changes
- Configurable materials folder path with browse button
- Same warm presets as the ShaderGUI buttons

**Access:** Tools > MiSide > Character Material Tuner

---

*This evaluation references environmental psychology research including Kaplan's Attention Restoration Theory (1995), Ulrich's Stress Reduction Theory (1991), and Alvarsson et al.'s work on nature soundscapes and physiological stress recovery (2010). VR comfort guidelines follow Oculus/Meta best practices for Quest development.*
