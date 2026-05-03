# Quest 3 Comfort & UX Tuning — 3D_Journal_Beach / 3D_Journal_Bedroom / 3D_Login

## Context

The three scenes are the production VR scenes for the EMILIA-VR project, deployed to Meta Quest 3 (Android). A scan revealed several settings that risk simulator sickness, sub-target frame rate, or visual softness:

- The XR Rig (a `XRI Starter Assets` prefab instance shared across scenes) ships with locomotion defaults (45° snap turn, 2.5 m/s continuous move, strafe enabled) that are above modern comfort baselines.
- Camera near clip plane is `0.01` — wastes depth precision on Quest 3 and can cause z-fighting.
- The Mobile URP asset (used on Android builds) has render scale `0.8` — softer than necessary on Quest 3's eye buffer.
- Bedroom contains **two realtime point lights at intensity 20** with 50m range, no shadows — a serious mobile-GPU cost.
- Login has a **realtime directional light with hard shadows** that should be baked since the scene is static.
- `ProjectSettings/TimeManager.asset` fixed timestep is `0.02` (50 Hz), out of sync with Quest 3's 72/90/120 Hz refresh; physics-driven hand interactions will judder.
- No general continuous-locomotion comfort vignette is enabled on the rig (the existing `PortalSceneTransition` vignette is portal-only).

Per the user's direction:
- **Do NOT touch any rig world position or `CameraYOffset` values** — these compensate for elevated environment floors and are intentional.
- **Apply maximum-comfort locomotion defaults**.
- **Raise render scale to 1.2**.
- **Override per scene only** — do NOT modify the shared XR Rig prefab under `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/`.

The intended outcome: all three scenes hit a sustained 90 FPS on Quest 3, look crisp, and minimise simulator sickness during locomotion and portal transitions — without altering the spatial layout the developer has already tuned.

---

## Changes

### 1. Per-scene XR Rig PrefabInstance overrides

Apply the following PrefabInstance modifications in each of the three scenes via the Unity Inspector (overrides on the XR Origin (XR Rig) instance). Do **not** edit the source prefab. The rig's source prefab is `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab` (guid `d6878e1999eb4b44a9f5a263af86c185`); same instance is referenced in:
- [Assets/Scenes/use/3D_Journal_Beach.unity](Assets/Scenes/use/3D_Journal_Beach.unity#L21911) (PrefabInstance &3000000012)
- [Assets/Scenes/use/3D_Journal_Bedroom.unity](Assets/Scenes/use/3D_Journal_Bedroom.unity#L2905) (PrefabInstance &375782284)
- [Assets/Scenes/use/3D_Login.unity](Assets/Scenes/use/3D_Login.unity) (corresponding XR Origin instance)

#### 1a. Continuous Move Provider
| Property | Current | New | Reason |
|---|---|---|---|
| `m_MoveSpeed` | 2.5 | **1.6** | Slow walking speed; reduces vection-driven nausea |
| `m_EnableStrafe` | 1 | **0** | Strafing without head turn is one of the strongest sickness triggers |
| `m_EnableFly` | 0 | 0 | Keep — disables disorienting flight |
| `m_UseGravity` | 1 | 1 | Keep — anchors locomotion |
| `m_InAirControlModifier` | 0.5 | 0.5 | Keep |
| `m_ForwardSource` | Camera | Camera | Keep — head-relative forward is most comfortable |

#### 1b. Snap Turn Provider
| Property | Current | New | Reason |
|---|---|---|---|
| `m_TurnAmount` | 45 | **30** | 30° is the comfort default; 45° causes more peripheral disorientation |
| `m_DebounceTime` | 0.5 | 0.5 | Keep |
| `m_EnableTurnLeftRight` | 1 | 1 | Keep |
| `m_EnableTurnAround` | (default) | **0** | Disable 180° turn; sudden flips trigger sickness |

#### 1c. Continuous Turn Provider
| Property | Current | New | Reason |
|---|---|---|---|
| `m_TurnSpeed` | 60 | **45** | Slower angular velocity reduces vestibular conflict |

If the project's UX favors snap-only turning, also set this provider's input action to unbound (or disable the GameObject) — but leave behavior selectable.

#### 1d. Tunneling Vignette Provider (Comfort)

The XR Rig prefab includes a `TunnelingVignetteController` and a `TunnelingVignetteProvider` GameObject. Currently inactive. **Enable it** and wire its locomotion provider list to include the Continuous Move and Continuous Turn providers (NOT Snap Turn — vignette during snap turn is jarring).

Recommended vignette parameters (override per scene):
- `apertureSize`: 0.7 (default 0.7 — visible black ring during motion)
- `featheringEffect`: 0.2
- `easeInTime`: 0.1
- `easeOutTime`: 0.3 (faster fade-out feels more responsive)
- `vignetteColor`: pure black `(0,0,0)`

#### 1e. Main Camera (under XR Rig → Camera Floor Offset → Main Camera)
| Property | Current | New | Reason |
|---|---|---|---|
| Near clip plane | 0.01 | **0.05** | Better depth precision on Quest 3; 0.01 wastes z-buffer and can cause far-plane z-fighting |
| Far clip plane | 1000 | **300** in Bedroom/Login (interior); leave 1000 for Beach (outdoor) | Tighter culling on indoor scenes; saves draw calls |
| `m_AllowHDROutput` | 1 | **0** | Quest 3 mobile pipeline doesn't use HDR output; flag adds no benefit and may waste a bit of bandwidth |
| `m_Antialiasing` (camera-level) | 0 | 0 | Keep — MSAA comes from the URP asset (set to 4× in Mobile_RPAsset). Setting per-camera is unnecessary. |

> The TAA quality (3) and post-processing rendering (off) are already correct; do not change.

---

### 2. Bedroom-specific lighting fixes

File: [Assets/Scenes/use/3D_Journal_Bedroom.unity](Assets/Scenes/use/3D_Journal_Bedroom.unity)

Two **realtime point lights with intensity 20, range 50** are present:
- Light at fileID `&178217444`
- Light at fileID `&2047163489`

These are the single biggest mobile-GPU risk in the project. Even without shadows, two overlapping 50 m unit-spheres at 20× intensity will saturate the additional-light pass and cost both fillrate and per-pixel light cycles on Quest 3.

**Apply both:**
1. Change `m_Lightmapping`: 4 (Realtime) → **2 (Mixed)** if anything dynamic must receive them, or **1 (Baked)** if the room is fully static. Bedroom is static — choose **Baked**.
2. Reduce `m_Intensity`: 20 → **2.0** and `m_Range`: 50 → **8**.

After: re-bake lighting (`Window → Rendering → Lighting → Generate Lighting`).

**Directional light** (`&1256331763`, m_Intensity 2.5, hard shadows, baked) — leave as-is.

---

### 3. Login-specific lighting fix

File: [Assets/Scenes/use/3D_Login.unity](Assets/Scenes/use/3D_Login.unity)

Directional light `&763486262`:
- `m_Lightmapping: 4` (Realtime) — change to **`1` (Baked)**. The login scene is fully static.
- `m_Shadows.m_Type: 2` (Hard) — keep, but baked shadowmap costs nothing at runtime.

Also: the scene's `LightingSettings` reference is `{fileID: 0}`. Assign a LightingSettings asset (reuse `Assets/Settings/3D_LoginArea.asset` if it's the right one — confirm in editor). Without it, lightmapper falls back to defaults.

After: re-bake.

---

### 4. Mobile URP asset — render scale

File: [Assets/Settings/Mobile_RPAsset.asset](Assets/Settings/Mobile_RPAsset.asset#L29)

| Property | Current | New |
|---|---|---|
| `m_RenderScale` | 0.8 | **1.2** |

Already optimal (leave as-is): `m_MSAA: 4`, `m_SupportsHDR: 0`, `m_ShadowDistance: 20`, `m_ShadowCascadeCount: 1`, `m_AdditionalLightShadowsSupported: 0`, `m_SoftShadowsSupported: 0`, `m_UseAdaptivePerformance: 1`, `m_MainLightShadowmapResolution: 1024`, `m_UseFastSRGBLinearConversion: 1`.

Since Adaptive Performance is on, the headset will scale down automatically if 1.2 ever stresses the GPU.

---

### 5. Fixed timestep

File: `ProjectSettings/TimeManager.asset`

| Property | Current | New |
|---|---|---|
| `Fixed Timestep` | 0.02 (50 Hz) | **0.01111** (1/90, matches Quest 3 default refresh) |

This aligns physics ticks with the headset display rate. Hand interactions (whiteboard pen, stylus, grabbables) will feel smoother and stop their micro-judder.

If the app intentionally targets 72 Hz (legacy mode), use `0.01388` instead. 90 Hz is Quest 3's default app refresh, so 1/90 is the right choice.

---

### 6. Verification: UI canvases (read-only check, fix per-scene if needed)

Each of the three scenes contains 6–7 `RenderMode: 0` (Screen Space – Overlay) canvases. Screen Space Overlay does **not render** in VR. Most are likely either:
- Inactive debug/popup canvases (no fix needed).
- Editor-only HUDs (no fix needed in builds).
- Real UI that should be `RenderMode: 2` (World Space) with a Tracked Device Graphic Raycaster.

**Open each scene, list active overlay canvases**, and for any that are user-visible:
- Change `RenderMode` to World Space.
- Position 1.5–2 m in front of the user, scale ~0.001, font size ≥ 24.
- Replace `GraphicRaycaster` with `TrackedDeviceGraphicRaycaster`.

Do not delete unfamiliar canvases; toggle inactive and document instead.

---

### 7. Verification: OpenXR Meta features (read-only check)

File: `Assets/XR/Settings/OpenXRPackageSettings.asset` (large; do not modify blindly)

Confirm the Android target has these features enabled:
- **Meta Quest Support** (foveated rendering hook)
- **Foveated Rendering** — set to `Quality: High` or `Performance` based on tested headroom
- **Hand Tracking Subsystem**
- **Meta XR Eye Tracked Foveated Rendering** (Quest 3 only — uses eye tracking for dynamic foveation; large clarity win at no perf cost)

If any are missing, enable via `Edit → Project Settings → XR Plug-in Management → OpenXR (Android tab)`.

---

## Critical files modified

| File | Change |
|---|---|
| [Assets/Scenes/use/3D_Journal_Beach.unity](Assets/Scenes/use/3D_Journal_Beach.unity) | XR Rig PrefabInstance overrides (locomotion, vignette, camera near clip, HDR output) |
| [Assets/Scenes/use/3D_Journal_Bedroom.unity](Assets/Scenes/use/3D_Journal_Bedroom.unity) | XR Rig PrefabInstance overrides + two point lights baked & dimmed |
| [Assets/Scenes/use/3D_Login.unity](Assets/Scenes/use/3D_Login.unity) | XR Rig PrefabInstance overrides + directional light baked + LightingSettings reference |
| [Assets/Settings/Mobile_RPAsset.asset](Assets/Settings/Mobile_RPAsset.asset) | `m_RenderScale: 0.8 → 1.2` |
| `ProjectSettings/TimeManager.asset` | `Fixed Timestep: 0.02 → 0.01111` |
| `Assets/XR/Settings/OpenXRPackageSettings.asset` | Verify Quest 3 features (read-only check; only modify if missing) |

## Files NOT modified (per user direction)

- `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab` — shared prefab; left untouched
- Any rig world position or `CameraYOffset` value in any scene
- The `CharacterController` `m_Height: 8` / `m_Center.y: -1.09` in Bedroom

## Reused existing functionality

- `TunnelingVignetteController` / `TunnelingVignetteProvider` — already present on the XR Rig, just need to be activated (no new code).
- [Assets/Scripts/MixedReality/PortalSceneTransition.cs](Assets/Scripts/MixedReality/PortalSceneTransition.cs) — already implements scene-transition vignette/fade; no changes needed.
- [Assets/Scripts/MixedReality/PassthroughManager.cs](Assets/Scripts/MixedReality/PassthroughManager.cs) — handles VR↔MR fade; no changes needed.
- Mobile URP asset's Adaptive Performance hook — already on; will compensate if render scale 1.2 ever pushes the GPU.

---

## Verification

### Editor smoke test (XR Device Simulator)
For each scene:
1. Open scene, Enter Play Mode.
2. Walk forward with the simulated stick — confirm:
   - Movement feels slower (≈1.6 m/s).
   - Strafe is disabled.
   - Black tunneling vignette fades in during motion, fades out on stop.
3. Press snap-turn input — confirm 30° per click.
4. Visual check: no z-fighting on far objects (validates 0.05 near clip).

### Quest 3 device test (build & deploy)
1. Build APK, deploy to Quest 3.
2. Open Meta Quest Developer Hub → Performance Analyzer (or in-headset OVR Metrics Tool):
   - Confirm sustained 90 FPS in all three scenes.
   - GPU utilization should drop noticeably after the Bedroom point-light fix.
3. Walk through each scene for ~5 minutes:
   - No peripheral wobble, no nausea spike.
   - Whiteboard pen and stylus feel smooth (validates 1/90 fixed timestep).
   - Text on UI is crisp (validates 1.2 render scale).
4. Trigger a portal transition in each journal scene:
   - Existing portal vignette should still play correctly.
   - Scene load completes within `minimumComfortLoadDelay` (0.9 s) plus actual load.

### Lighting bake check
After steps 2 and 3:
- `Window → Rendering → Lighting → Generate Lighting` → bake all three scenes.
- Confirm lightmap atlas size (1024) is not exceeded; if it is, re-evaluate any other large-scale lights.
- Visual check: bedroom retains its lit feel after the intensity-20 lights are baked at 2.0.

### External user test (sickness pass)
- Run a non-developer through ~10 minutes of the full flow (Login → Journal Beach → Journal Bedroom → portal-back).
- Ask: any nausea, eye strain, peripheral distortion, or "swimming" feeling?
- Specifically watch for issues during continuous move (vignette working?) and snap turns (30° comfortable?).

### Rollback safety
Each change is isolated:
- Locomotion/snap/vignette — revert single PrefabInstance overrides per scene.
- Lighting changes — revert via git on scene files; re-bake.
- `m_RenderScale` — single-line revert in Mobile_RPAsset.
- `Fixed Timestep` — single-line revert in TimeManager.asset.

No code changes are required. All work is asset/configuration tuning.
