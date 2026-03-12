# EMILIA-VR: VR UX Comfort Audit & Improvement Plan

**Target Device:** Meta Quest 3
**Framework:** XR Interaction Toolkit 3.3.1 / OpenXR 1.16.1 / URP 17.3.0
**Goal:** Reduce motion sickness and VR discomfort

---

## Current State: What's Already Good

| Feature | Status | Notes |
|---------|--------|-------|
| Locomotion Vignette | Enabled | `LocomotionVignetteController` with `TunnelingVignette` in all scenes, 8 providers |
| Post-Processing | Safe | Motion blur, DoF, chromatic aberration, film grain, lens distortion all disabled |
| Locomotion Modes | Multiple | Teleport, continuous move, snap turn, continuous turn via `ControllerInputActionManager` |
| Adaptive Performance | Enabled | `m_UseAdaptivePerformance: 1` in `Mobile_RPAsset.asset` |
| VSync | Disabled | Correct for VR; OpenXR handles frame pacing |
| Render Mode | Single Pass Multiview | `m_renderMode: 1` - correct for Quest 3 |
| Latency Optimization | Enabled | `m_latencyOptimization: 1` in Android OpenXR settings |
| MSAA | 4x (Mobile) | Good balance for text readability and aliasing |
| SRP Batcher | Enabled | Consistent frame timing |
| Ambient Comfort | Good | `SubtleLightBreathing.cs` - 0.08-0.12 Hz breathing on lights |
| Movement Feedback | Present | `WaterFootstepEffect.cs` - audio/visual grounding during movement |
| Hand Tracking | Integrated | XR Hands 1.7.3 with `OneEuroFilter` smoothing on `WhiteboardPen` |
| Snap Turn Default | Yes | `m_SmoothTurnEnabled: 0` - snap turn is default (good) |

---

## Issues Found

### Priority 1 - Critical (Direct Motion Sickness Causes)

---

#### 1.1 Canvas-Based FadeManager Causes Binocular Depth Conflict

**File:** `Assets/_legacy/2D Emilia/Scripts/Manager/FadeManager.cs`

**Problem:** `FadeManager` uses a `CanvasGroup` overlay for scene transitions. In VR, Screen Space Canvas overlays render at infinity depth or a fixed plane, creating depth conflicts between left and right eyes. This causes binocular rivalry (each eye sees slightly different overlay alignment), leading to eye strain, headaches, and disorientation during every scene transition. With 4+ scenes and frequent transitions, users experience this discomfort repeatedly.

**Fix:** Replace the `CanvasGroup` approach with an inverted-normals sphere mesh positioned as a child of the XR Camera.

**Implementation:**
1. Create `Assets/Shaders/VRFade.shader` - Unlit shader with `ZTest Always`, `ZWrite Off`, `Blend SrcAlpha OneMinusSrcAlpha`, `Cull Front` (renders inside of sphere), queue `Overlay`. Single property: `_Alpha`.
2. Modify `FadeManager.cs`:
   - Replace `CanvasGroup _fadeCanvas` with `MeshRenderer _fadeSphere` + `Material _fadeMat`
   - In `FadeRoutine`, use `_fadeMat.SetFloat("_Alpha", Mathf.Lerp(...))` instead of `_fadeCanvas.alpha`
   - The sphere should be a child of the XR Camera at local position `(0,0,0)`, scale `(0.5, 0.5, 0.5)`
   - Remove all `CanvasGroup`/`blocksRaycasts` references
3. Keep the same public API (`FadeInCoroutine`, `FadeOutCoroutine`, `InstantBlack`, `InstantClear`) so `SceneFlowManager` requires zero changes.

---

#### 1.2 No Target Frame Rate Set (Quest 3 May Default to 72Hz)

**Problem:** No `Application.targetFrameRate` is set anywhere in the codebase. Quest 3 supports 72/90/120Hz. Without explicit targeting, the runtime may default to 72Hz. Running below 90Hz increases reprojection artifacts (ASW/ATW ghosting) that directly trigger nausea.

**Fix:** Create a bootstrapper script that sets frame rate and requests 90Hz display refresh.

**Implementation:** Create `Assets/Scripts/VR/VRPerformanceBootstrapper.cs`:
```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class VRPerformanceBootstrapper : MonoBehaviour
{
    [SerializeField] int targetFrameRate = 90;

    void Awake()
    {
        Application.targetFrameRate = targetFrameRate;
        TrySetRefreshRate(targetFrameRate);
    }

    void TrySetRefreshRate(float rate)
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);
        foreach (var display in displays)
        {
            if (display.running && display.TryRequestDisplayRefreshRate(rate))
                break;
        }
    }
}
```
Attach to XR Rig or a persistent GameManager object.

---

#### 1.3 Physics Timestep Mismatch (50Hz Physics vs 90Hz Display)

**File:** `ProjectSettings/TimeManager.asset`
**Current Value:** `Fixed Timestep: 0.02` (50Hz)

**Problem:** Quest 3 runs at 90Hz. Physics-driven objects update at 50Hz, causing visible micro-stuttering between physics ticks. The vestibular system cannot predict stuttering motion, triggering nausea - especially on any grabbable or physics-driven objects.

**Fix:** Change `Fixed Timestep` to `0.01111` (approximately 1/90s).

**Alternative:** If the timestep change hurts CPU performance, keep `0.02` but set `Rigidbody.interpolation = RigidbodyInterpolation.Interpolate` on all physics objects.

---

#### 1.4 Foveated Rendering is Disabled (Massive GPU Savings Left on Table)

**File:** `Assets/XR/Settings/OpenXRPackageSettings.asset`

**Problem:** `FoveatedRenderingFeature Android` exists but is disabled (`m_enabled: 0`). Additionally, `m_foveatedRenderingApi: 0` (none). Quest 3 has eye-tracking hardware that supports eye-tracked foveated rendering. Without it, the GPU renders full resolution across the entire view, wasting 20-30% GPU budget on peripheral pixels.

**Fix:**
1. Set `FoveatedRenderingFeature Android` → `m_enabled: 1`
2. Set `m_foveatedRenderingApi` → `1` (Meta Foveation) in Android OpenXR settings
3. Optionally enable `enableSubsampledLayout: 1` for additional savings

**Why this is critical for comfort:** Foveated rendering is not just a performance optimization - it provides the GPU headroom needed to maintain stable 90Hz. Dropped frames directly cause nausea. This single change can save 20-30% GPU time.

---

#### 1.5 No Speed Ramping on Continuous Movement

**File:** `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Scripts/DynamicMoveProvider.cs`

**Problem:** `DynamicMoveProvider` maps stick input linearly to movement speed via `ContinuousMoveProvider.ComputeDesiredMove()`. Abrupt start/stop creates instant velocity changes that the vestibular system cannot anticipate, causing vestibular-visual mismatch.

**Fix:** Create `Assets/Scripts/VR/ComfortMoveProvider.cs` extending `DynamicMoveProvider`:
```csharp
public class ComfortMoveProvider : DynamicMoveProvider
{
    [SerializeField] float m_AccelerationTime = 0.3f;
    [SerializeField] float m_DecelerationTime = 0.2f;
    [SerializeField] AnimationCurve m_AccelerationCurve
        = AnimationCurve.EaseInOut(0, 0, 1, 1);

    float m_CurrentSpeedFactor;

    protected override Vector3 ComputeDesiredMove(Vector2 input)
    {
        float target = input.sqrMagnitude > 0.01f ? 1f : 0f;
        float rate = target > m_CurrentSpeedFactor
            ? Time.deltaTime / m_AccelerationTime
            : Time.deltaTime / m_DecelerationTime;
        m_CurrentSpeedFactor = Mathf.MoveTowards(
            m_CurrentSpeedFactor, target, rate);

        var move = base.ComputeDesiredMove(input);
        return move * m_AccelerationCurve.Evaluate(m_CurrentSpeedFactor);
    }
}
```
Replace `DynamicMoveProvider` on the XR Rig prefab with `ComfortMoveProvider`.

---

#### 1.6 Verify Teleportation is Default Locomotion

**File:** `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab`

**Problem:** `ControllerInputActionManager.m_SmoothMotionEnabled` may be serialized as `true` in the prefab. Continuous movement is the #1 cause of VR sickness in new users.

**Fix:** Ensure `m_SmoothMotionEnabled: 0` in the prefab (teleport as default). Power users can opt in to continuous movement via comfort settings.

---

### Priority 2 - High (Significant Comfort Improvements)

---

#### 2.1 No Haptic Feedback on Interactions

**Problem:** Zero references to `SendHapticImpulse` in any custom script. Without haptic feedback, interactions feel disconnected. Haptics provide proprioceptive grounding that reduces disorientation (research shows 15-20% reduction).

**Fix:** Create `Assets/Scripts/VR/HapticFeedbackProvider.cs`:
```csharp
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class HapticFeedbackProvider : MonoBehaviour
{
    [SerializeField] XRBaseInteractor m_Interactor;
    [SerializeField] float m_SelectAmplitude = 0.3f;
    [SerializeField] float m_SelectDuration = 0.1f;
    [SerializeField] float m_ReleaseAmplitude = 0.1f;
    [SerializeField] float m_ReleaseDuration = 0.05f;

    void OnEnable()
    {
        m_Interactor.selectEntered.AddListener(OnSelect);
        m_Interactor.selectExited.AddListener(OnRelease);
    }

    void OnDisable()
    {
        m_Interactor.selectEntered.RemoveListener(OnSelect);
        m_Interactor.selectExited.RemoveListener(OnRelease);
    }

    void OnSelect(SelectEnterEventArgs args)
        => m_Interactor.SendHapticImpulse(m_SelectAmplitude, m_SelectDuration);

    void OnRelease(SelectExitEventArgs args)
        => m_Interactor.SendHapticImpulse(m_ReleaseAmplitude, m_ReleaseDuration);
}
```

**Recommended impulse values:**
| Event | Amplitude | Duration |
|-------|-----------|----------|
| Grab start | 0.3 | 0.1s |
| Grab release | 0.1 | 0.05s |
| UI button press | 0.2 | 0.05s |
| Teleport arrival | 0.15 | 0.15s |
| Snap turn | 0.1 | 0.03s |

---

#### 2.2 No Comfort Settings Menu

**Problem:** No way for users to adjust comfort levels. Different users have vastly different susceptibility to VR sickness. This is also a Meta Quest Store requirement.

**Fix:** Create a world-space comfort settings panel accessible via controller menu button.

**Settings to include:**
| Setting | Type | Default | Range |
|---------|------|---------|-------|
| Movement Type | Toggle | Teleport | Teleport / Continuous |
| Turn Type | Toggle | Snap | Snap / Continuous |
| Movement Speed | Slider | 1.5 m/s | 0.5 - 3.0 |
| Vignette Intensity | Slider | 0.5 | 0 - 1 |
| Snap Turn Angle | Selector | 45 deg | 15 / 30 / 45 / 90 |

**Files to create:**
- `Assets/Scripts/VR/ComfortSettingsManager.cs` - Runtime settings with `PlayerPrefs` persistence
- `Assets/Scripts/VR/ComfortSettingsUI.cs` - World-space UI panel controller

**Integration points:**
- `ControllerInputActionManager.smoothMotionEnabled` / `.smoothTurnEnabled`
- `ContinuousMoveProvider.moveSpeed`
- `LocomotionVignetteController` parameters

---

#### 2.3 No Rest Frame During Continuous Movement

**Problem:** No static visual reference during locomotion. A rest frame gives the brain a stable visual anchor, reducing vestibular-visual conflict. Research by Purdue University showed a virtual nose reduces motion sickness by 13.5%.

**Fix:** Create `Assets/Scripts/VR/RestFrameOverlay.cs`:
- A subtle mesh (virtual nose or peripheral grid) as a child of the XR Camera
- Only visible during continuous movement (detect via locomotion state)
- Uses `ZTest Always` to render on top, with low opacity
- Fade in/out over 0.2s when movement starts/stops

---

#### 2.4 Render Scale Too Low (Text Blur Causes Eye Strain)

**File:** `Assets/Settings/Mobile_RPAsset.asset`
**Current Value:** `m_RenderScale: 0.8`

**Problem:** 80% render scale causes blurriness, especially on text (critical in Chat, Journal, and Login scenes). This forces users to squint, causing accommodation strain - the #2 cause of VR discomfort after motion sickness.

**Fix (Preferred):** Enable Dynamic Resolution + increase base render scale:
1. Enable `AutomaticDynamicResolutionFeature Android` in `OpenXRPackageSettings.asset` (currently disabled)
2. Set `m_RenderScale: 1.0` in `Mobile_RPAsset.asset`
3. Dynamic resolution will scale down when GPU-bound, keeping text sharp when possible

**Fix (Alternative):** Increase `m_RenderScale` to `0.9` and enable FSR upscaling filter (`m_UpscalingFilter: 1`, already configured with `m_FsrSharpness: 0.92`).

**Note:** Combine with foveated rendering (1.4) to offset the GPU cost.

---

#### 2.5 Realtime GI CPU Usage at 100%

**File:** `ProjectSettings/QualitySettings.asset`
**Current Value:** `realtimeGICPUUsage: 100` (Mobile tier)

**Problem:** Maximum CPU allocation to realtime GI competes with physics, scripts, and XR tracking on Quest 3's Snapdragon XR2 Gen 2. CPU and GPU share thermal budget, so CPU overload also impacts GPU performance.

**Fix:** Set `realtimeGICPUUsage: 25` for the Mobile quality tier. GI updates converge slower but frees CPU for frame-critical work. If scenes use fully baked lighting, consider disabling realtime GI entirely.

---

### Priority 3 - Medium (Polish & Additional Comfort)

---

#### 3.1 Enable Streaming Mipmaps

**File:** `ProjectSettings/QualitySettings.asset`
**Current Value:** `streamingMipmapsActive: 0` (Mobile tier)

**Fix:** Set `streamingMipmapsActive: 1` with `streamingMipmapsMemoryBudget: 256`. Prevents frame stutters from loading all texture mip levels at scene load.

---

#### 3.2 Enable Symmetric Projection

**File:** `Assets/XR/Settings/OpenXRPackageSettings.asset`
**Current Value:** `m_symmetricProjection: 0` (Android)

**Fix:** Set `m_symmetricProjection: 1`. Simplifies GPU projection matrix, allowing both eyes to share more rendering work in multiview. Free performance gain with no visual impact.

---

#### 3.3 Add Audio Cues for Spatial Orientation

**What to add:**
- **Teleport whoosh:** Brief spatial audio on teleport arrival (confirms landing)
- **Snap turn click:** Soft click on snap turn (confirms rotation)
- **Movement wind:** Very subtle ambient wind during continuous movement (audio velocity confirmation)

Multi-sensory feedback (audio + visual + haptic) reduces cognitive load and helps the brain reconcile perceived vs actual motion.

---

#### 3.4 Verify Camera Near/Far Clip Planes

**File:** XR Rig prefab Main Camera

**Check:** Ensure near clip plane >= 0.1m (10cm). Objects rendered closer cause accommodation-vergence conflict. Unity default for XR may be 0.01m which is too close.

---

#### 3.5 Add Comfort Rating Labels to Locomotion Options

In the comfort settings menu (2.2), label each option:
- Teleport: **Comfortable** (green)
- Continuous Move (slow): **Moderate** (yellow)
- Continuous Move (fast): **Intense** (orange)
- Snap Turn: **Comfortable** (green)
- Continuous Turn: **Moderate** (yellow)

Follows the Meta Quest Store comfort rating system.

---

## Implementation Sequence

### Phase 1: Quick Wins (Settings-only changes, no code)
1. Enable Foveated Rendering (1.4) - toggle in OpenXR settings
2. Fix physics timestep to 1/90 (1.3) - one value in `TimeManager.asset`
3. Enable Symmetric Projection (3.2) - toggle in OpenXR settings
4. Reduce Realtime GI CPU to 25 (2.5) - one value in `QualitySettings.asset`
5. Enable Streaming Mipmaps (3.1) - toggle in `QualitySettings.asset`

### Phase 2: Core Code Changes
6. Set target frame rate to 90Hz (1.2) - new `VRPerformanceBootstrapper.cs`
7. Replace Canvas FadeManager with sphere fade (1.1) - new shader + refactor
8. Add speed ramping to movement (1.5) - new `ComfortMoveProvider.cs`
9. Verify/fix teleport as default (1.6) - prefab check
10. Enable Dynamic Resolution + raise render scale (2.4) - settings

### Phase 3: New Features
11. Add haptic feedback (2.1) - new `HapticFeedbackProvider.cs`
12. Add rest frame overlay (2.3) - new `RestFrameOverlay.cs`
13. Add comfort settings menu (2.2) - new UI + manager scripts
14. Add audio cues (3.3) - audio sources on interactors
15. Add comfort labels (3.5) - UI update
16. Verify camera clip planes (3.4) - prefab check

---

## Verification Checklist

- [ ] Build to Quest 3 and check Settings > Developer > Frame Rate shows 90fps
- [ ] Scene transitions fade smoothly without eye strain or depth flicker
- [ ] Continuous movement ramps up gradually (no instant velocity)
- [ ] Teleport is the default locomotion mode on fresh install
- [ ] Snap turn at 45 degrees with haptic click
- [ ] Text in Chat/Journal/Login scenes is readable without squinting
- [ ] No frame drops during scene transitions or heavy scenes
- [ ] Vignette appears during all continuous movement/turning
- [ ] Haptic feedback on grab, release, UI press, teleport, snap turn
