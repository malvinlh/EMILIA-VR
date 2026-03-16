# MR Journaling — Scene Setup & Audit Guide

> Scene: `Assets/Scenes/3D_Journal_playground.unity`
> Scripts: `Assets/Scripts/MixedReality/`
> Editor helper: `Assets/Editor/MRJournalSceneSetup.cs` (menu: **Tools > MR Journal > Setup Scene References**)

---

## 1. Architecture Overview

```
JournalSessionManager  (central state machine on "JournalChairTable")
  ├─ PassthroughManager           VR ↔ passthrough fade transitions
  ├─ ARTableDetector              AR planes + palm-flat hand confirmation
  │     └─ (uses WhiteboardPen.GetHandSubsystem())
  ├─ CalibrationGuide             Visual feedback (plane highlights, palm dots, progress ring)
  │     ├─ SurfaceDotGrid         Quest-style dot grid on detected surface
  │     └─ (subscribes to ARTableDetector events)
  ├─ AlignmentAnchor              ARAnchor drift correction
  ├─ JournalStartButton           Hand-poke / XRI world-space button
  └─ WhiteboardUtils              Whiteboard prefab spawning (from Handwriting layer)
```

**State flow:**
```
Idle → [button press] → Passthrough → PlaneDiscovery → HandConfirmation
  → Preview (whiteboard on real table) → TransitionToVR → Journaling
```

---

## 2. Required Scene Hierarchy

### Root GameObjects (all present in scene ✅)

| GameObject | Component(s) | Status |
|---|---|---|
| **XR Origin (XR Rig)** | XR Origin, ARPlaneManager, ARAnchorManager, 2× WhiteboardPen (on hand children) | ✅ Present |
| **AR Session** | ARSession, ARInputManager | ✅ Present |
| **JournalChairTable** | **JournalSessionManager** | ✅ Present |
| ↳ **JournalTable** | (Transform only) | ✅ Present |
| ↳↳ **JournalStartButton** | JournalStartButton, XRSimpleInteractable, BoxCollider | ✅ Present |
| ↳↳↳ **BookMesh** | MeshFilter, MeshRenderer, BoxCollider | ✅ Present |
| ↳ **Chair** | (Transform only) | ✅ Present |
| ↳↳ **SeatPoint** | (Transform only — teleport target) | ✅ Present |
| **PassthroughManager** | PassthroughManager | ✅ Present |
| **ARTableDetector** | ARTableDetector | ✅ Present |
| **CalibrationGuide** | CalibrationGuide, SurfaceDotGrid | ✅ Present |
| **AlignmentAnchor** | AlignmentAnchor | ✅ Present |
| **WhiteboardUtils** | WhiteboardUtils | ✅ Present |
| **DigitalInkManager** | 3 recognition components | ✅ Present |
| **SurfaceDetector** | SurfaceDetector (legacy, unused) | ⚠️ Legacy — not referenced by active flow |

### Named GameObjects (required by `MRJournalSceneSetup.cs` / `GameObject.Find()`)

All names match: ✅ JournalChairTable, JournalTable, Chair, SeatPoint, "XR Origin (XR Rig)"

---

## 3. Inspector Reference Wiring

Run **Tools > MR Journal > Setup Scene References** to auto-wire. Current status from scene file:

### JournalSessionManager fields

| Field | Assigned? | Value |
|---|---|---|
| `passthroughManager` | ✅ | PassthroughManager GO |
| `arTableDetector` | ✅ | ARTableDetector GO |
| `calibrationGuide` | ✅ | CalibrationGuide GO |
| `alignmentAnchor` | ✅ | AlignmentAnchor GO |
| `whiteboardUtils` | ✅ | WhiteboardUtils GO |
| `startButton` | ✅ | JournalStartButton (on JournalTable child) |
| `arPlaneManager` | ✅ | ARPlaneManager (on XR Origin) |
| `journalChairTable` | ✅ | JournalChairTable transform |
| `journalTable` | ✅ | JournalTable transform |
| `chair` | ✅ | Chair transform |
| `seatPoint` | ✅ | SeatPoint transform |
| `xrOrigin` | ✅ | XR Origin (XR Rig) transform |
| `skipPlaneDetection` | ✅ | `true` (hand-only mode) |
| `instructionText` | — | `null` (auto-created at runtime — OK) |
| **`whiteboardPlaceholder`** | ❌ **MISSING** | `null` — see issue #1 below |

### Cross-references (wired by editor script)

| Component.Field | Status |
|---|---|
| `ARTableDetector.xrOrigin` | ✅ |
| `ARTableDetector.planeManager` | ✅ |
| `CalibrationGuide.tableDetector` | ✅ |
| `CalibrationGuide.passthroughManager` | ✅ |
| `CalibrationGuide.dotGrid` → SurfaceDotGrid | ✅ |
| `CalibrationGuide.palmIndicatorPrefab` | ✅ (Sphere prefab) |
| `AlignmentAnchor.anchorManager` | ✅ (ARAnchorManager on XR Origin) |
| `AlignmentAnchor.targetToAlign` | ✅ (JournalChairTable) |

---

## 4. Issues Found

### Issue #1 — `whiteboardPlaceholder` is NOT assigned (Functional impact: Medium)

**What:** `JournalSessionManager.whiteboardPlaceholder` is `null` (`{fileID: 0}` in scene).

**Impact:** When the whiteboard transitions from passthrough to VR (`MoveWhiteboardToVRLayer()`), the code checks `whiteboardPlaceholder` to reposition and rescale the whiteboard onto the virtual table surface. With it `null`, the whiteboard stays at the raw MR-detected world position, which may not align with the virtual table mesh. The whiteboard also won't be scaled to match the table area.

**Fix:**
1. On the **JournalTable** GameObject, add an **empty child** named e.g. "WhiteboardArea"
2. Add a **BoxCollider** (set as **Trigger**) sized to the desired whiteboard area on the table surface
3. Assign this transform to `JournalSessionManager.whiteboardPlaceholder`
4. The collider's lossy scale (X, Z) defines the whiteboard dimensions; Y should be thin (e.g. 0.01)

### Issue #2 — `XRSimpleInteractable.m_InteractionManager` is `{fileID: 0}` (Functional impact: Low)

**What:** The `XRSimpleInteractable` on the JournalStartButton has no explicit `m_InteractionManager` reference.

**Impact:** XRI auto-discovers the `XRInteractionManager` at runtime via `FindObjectOfType`, so this technically works if the XR Origin prefab contains one. However, if the XR Interaction Manager is missing or loads late, the controller-based button press path won't work (hand-poke still works via direct distance checks in `JournalStartButton.Update()`).

**Fix:** Verify that the XR Origin prefab includes an `XRInteractionManager`. If not, add one as a root scene GO or ensure the prefab contains it.

### Issue #3 — Camera culling mask includes PassthroughUI layer (Functional impact: Low)

**What:** The main XR camera renders all layers (`m_Bits: 4294967295`), including layer 31 (PassthroughUI).

**Impact:** Objects placed on the PassthroughUI layer (calibration dots, instruction text, whiteboard preview) would be visible during normal VR gameplay. In practice this is mitigated because those objects are destroyed/deactivated after calibration. But if any leak, they'd render in VR.

**Fix (optional):** Exclude layer 31 from the camera culling mask in the prefab or at runtime when not in passthrough mode. `PassthroughManager.cs` handles this dynamically for the transition, but a default exclusion is safer.

### Issue #4 — Gemini API key is empty (Functional impact: High for handwriting recognition)

**What:** `DigitalInkManager` → Gemini API client has `apiKey: ""`.

**Impact:** The 2nd and 3rd tier handwriting recognition (Gemini LLM refinement, Gemini VLM fallback) will fail. ML Kit (1st tier, on-device) still works.

**Fix:** Set the API key in the DigitalInkManager inspector or via a ScriptableObject config before building.

### Issue #5 — `SurfaceDetector` is a legacy/unused GO (Functional impact: None)

**What:** A `SurfaceDetector` GO exists in the scene but is not referenced by `JournalSessionManager` or any active script.

**Impact:** No functional impact — it just clutters the hierarchy. The active flow uses `ARTableDetector`.

**Fix (optional):** Delete the `SurfaceDetector` GameObject to reduce clutter.

---

## 5. OpenXR Feature Group Status

All required Meta Quest features are **enabled** for both Android and Standalone:

| Feature | Android | Standalone |
|---|---|---|
| Meta Quest: Camera (Passthrough) | ✅ enabled | ✅ enabled |
| Meta Quest: Planes | ✅ enabled | ✅ enabled |
| Meta Quest: Anchors | ✅ enabled | ✅ enabled |
| Meta Quest: Session | ✅ enabled | ✅ enabled |
| Meta Quest: Meshing | ✅ enabled | ✅ enabled |

---

## 6. Layer Configuration

| Layer | Name | Used By |
|---|---|---|
| 10 | Whiteboard | WhiteboardPen, JournalSessionManager (post-transition) |
| 31 | PassthroughUI | PassthroughManager, CalibrationGuide, SurfaceDotGrid, instruction text |

Both are correctly defined in `ProjectSettings/TagManager.asset` ✅

---

## 7. Package Dependencies

| Package | Version | Required By |
|---|---|---|
| `com.unity.xr.arfoundation` | — | ARSession, ARPlaneManager, ARAnchorManager, ARCameraManager, ARCameraBackground |
| `com.unity.xr.hands` | 1.7.3 | Hand tracking (palm-flat detection, button poke) |
| `com.unity.xr.interaction.toolkit` | 3.3.1 | XRSimpleInteractable, LocomotionProvider |
| `com.unity.xr.meta-openxr` | 2.4.0 | OpenXR backend + Meta features |
| `com.unity.textmeshpro` | — | Runtime instruction text |
| URP | — | Unlit shader for fade quad, plane highlights, dot grid |

---

## 8. Runtime Permissions (Android)

The `JournalSessionManager` requests `com.oculus.permission.USE_SCENE` at runtime before enabling plane detection. If denied, it falls back to hand-only mode. When `skipPlaneDetection = true` (current setting), this permission request is skipped entirely.

---

## 9. Quick Setup Checklist

For a fresh scene or after pulling changes:

- [ ] Open `3D_Journal_playground` scene
- [ ] Run **Tools > MR Journal > Setup Scene References** — auto-wires all inspector refs
- [ ] **Create `whiteboardPlaceholder`**: add empty child to JournalTable with BoxCollider trigger, assign to JournalSessionManager
- [ ] Verify XR Origin prefab contains **XRInteractionManager**
- [ ] Set **Gemini API key** on DigitalInkManager (if using LLM/VLM recognition)
- [ ] (Optional) Delete the legacy `SurfaceDetector` GO
- [ ] (Optional) Exclude layer 31 from XR camera culling mask in prefab
- [ ] Build target: **Android** (Meta Quest 3)
- [ ] Verify **OpenXR Feature Groups** are all enabled (Project Settings > XR Plug-in Management > OpenXR)

---

## 10. Testing Tips

- **skipPlaneDetection = true** (current) → hand-only mode, no Scene Model needed. Faster calibration.
- **skipPlaneDetection = false** → requires `com.oculus.permission.USE_SCENE` + Space Setup completed on Quest 3.
- Test passthrough transitions in headset only (XR Simulator doesn't support ARCameraManager).
- If detection times out (15s default), a fallback whiteboard spawns at the virtual table position.
- Re-calibration available mid-session via `JournalSessionManager.RequestReCalibration()`.
