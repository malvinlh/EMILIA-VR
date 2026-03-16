# MR Journaling — Scene Setup & Audit Guide

> Scene: `Assets/Scenes/3D_Journal_playground.unity`
> Scripts: `Assets/Scripts/MixedReality/`
> Editor helper: `Assets/Editor/MRJournalSceneSetup.cs` (menu: **Tools > MR Journal > Setup Scene References**)

---

## 1. Architecture Overview

```
JournalSessionManager  (central state machine on "JournalChairTable")
  ├─ PassthroughManager           VR ↔ passthrough fade transitions (OpenXR + ARCameraBackground)
  ├─ ARTableDetector              XR Hand Tracking palm-flat detection + AR plane matching
  │     ├─ SessionToWorld()       Uses CameraFloorOffsetObject (NOT XR Origin root!)
  │     └─ (uses WhiteboardPen.GetHandSubsystem())
  ├─ CalibrationGuide             Visual feedback (palm dots, dot grid, progress ring)
  │     ├─ SurfaceDotGrid         Quest-style dot grid on detected surface
  │     └─ (subscribes to ARTableDetector events)
  ├─ AlignmentAnchor              ARAnchor drift correction
  ├─ JournalStartButton           Hand-poke / XRI world-space button
  └─ WhiteboardUtils              Whiteboard prefab spawning (from Handwriting layer)
```

**State flow:**
```
Idle → [button press] → Passthrough → PlaneDiscovery/HandConfirmation
  → Preview (whiteboard at hand coords, placeholder size) → TransitionToVR → Journaling
```

**Coordinate conversion (critical):**
```
XRHandSubsystem joint pose (session space)
  → CameraFloorOffsetObject.TransformPoint()     ← CORRECT
  → world space (matches camera position)

XRHandSubsystem joint pose (session space)
  → xrOrigin.TransformPoint()                    ← WRONG (misses CameraYOffset)
  → world space shifted by -CameraYOffset on Y
```

---

## 2. Required Scene Hierarchy

### Root GameObjects

| GameObject | Component(s) | Status |
|---|---|---|
| **XR Origin (XR Rig)** | XR Origin (Device mode, CameraYOffset=2, Y=2.98), ARPlaneManager, ARAnchorManager, 2× WhiteboardPen | ✅ |
| **AR Session** | ARSession, ARInputManager | ✅ |
| **JournalChairTable** | **JournalSessionManager** | ✅ |
| ↳ **JournalTable** | localScale=(160, 16.65, 279.9) | ✅ |
| ↳↳ **WhiteboardPlaceholder** | BoxCollider (trigger, size=0.005×0.002×0.005) | ✅ |
| ↳↳ **JournalStartButton** | JournalStartButton, XRSimpleInteractable, BoxCollider | ✅ |
| ↳ **Chair** | (Transform only) | ✅ |
| ↳↳ **SeatPoint** | (Transform only — teleport target) | ✅ |
| **PassthroughManager** | PassthroughManager | ✅ |
| **ARTableDetector** | ARTableDetector | ✅ |
| **CalibrationGuide** | CalibrationGuide, SurfaceDotGrid | ✅ |
| **AlignmentAnchor** | AlignmentAnchor | ✅ |
| **WhiteboardUtils** | WhiteboardUtils | ✅ |

---

## 3. Inspector Reference Wiring

Run **Tools > MR Journal > Setup Scene References** to auto-wire.

### JournalSessionManager fields

| Field | Assigned? | Notes |
|---|---|---|
| `passthroughManager` | ✅ | |
| `arTableDetector` | ✅ | |
| `calibrationGuide` | ✅ | |
| `alignmentAnchor` | ✅ | |
| `whiteboardUtils` | ✅ | |
| `startButton` | ✅ | |
| `arPlaneManager` | ✅ | |
| `journalChairTable` | ✅ | |
| `journalTable` | ✅ | |
| `chair` | ✅ | |
| `seatPoint` | ✅ | |
| `xrOrigin` | ✅ | |
| `mainIsland` | ✅ | Root of MainIsland prefab — moved vertically for height calibration |
| `whiteboardPlaceholder` | ✅ | BoxCollider on JournalTable child |
| `skipPlaneDetection` | ✅ | `true` (hand-only mode) |

### Cross-references

| Component.Field | Status |
|---|---|
| `ARTableDetector.xrOrigin` | ✅ (also auto-finds at runtime) |
| `ARTableDetector.cameraOffsetTransform` | ✅ (resolved at runtime from XROrigin.CameraFloorOffsetObject) |
| `CalibrationGuide.tableDetector` | ✅ |
| `CalibrationGuide.passthroughManager` | ✅ |
| `CalibrationGuide.dotGrid` | ✅ |
| `AlignmentAnchor.targetToAlign` | ✅ |

---

## 4. How the MR Flow Works

1. **Button press** → hides button, enters passthrough via fade-to-black
2. **Passthrough** → camera renders real world (culling mask = layer 31 only)
3. **Hand detection** → ARTableDetector checks both palms flat on surface:
   - Palm-down angle < 20° (XR Hand Tracking)
   - All fingertips within 3cm of palm Y (session space — relative check)
   - Positions converted to world space via `CameraFloorOffsetObject.TransformPoint()`
4. **Dot grid** → CalibrationGuide shows dots around palm positions, progress ring between hands
5. **Hold 1.5s** → table confirmed, whiteboard spawns at hand-detected position
   - Size matches `whiteboardPlaceholder` BoxCollider (same as game world)
   - Placed on passthrough UI layer (31) so visible in passthrough
6. **Preview** → user sees whiteboard on real table for ~1.5s
7. **Transition** → fade to black → exit passthrough → fade from black
8. **VR setup** (during black screen):
   - Teleport XR Origin: match SeatPoint XZ position + yaw; set camera Y = SeatPoint designed height
   - Skip `AdjustTableForDistanceMismatch` (placeholder is authoritative)
   - `AdjustIslandHeight`: shift entire MainIsland so virtual table surface = cameraY − realEyeAboveTable
   - Snap whiteboard to placeholder BoxCollider center (now at calibrated height), rotation = identity
   - Move whiteboard to layer 10 (Whiteboard layer)
   - Create spatial anchor for drift correction
9. **Journaling** → locomotion locked, player writes on whiteboard

---

## 5. Known Gotchas

### CameraYOffset coordinate mismatch
The XR Origin uses Device tracking mode with `CameraYOffset = 2`. The Camera Floor Offset Object sits at `Y = XROrigin.Y + CameraYOffset`. Hand tracking session-space positions must go through this same transform — using `xrOrigin.TransformPoint()` skips the offset and places hands 2m too low.

### WhiteboardPlaceholder parent scale
JournalTable has localScale=(160, 16.65, 279.9). The placeholder's BoxCollider size (0.005, 0.002, 0.005) is in local space — world size = `Vector3.Scale(boxCol.size, lossyScale)`.

### AdjustTableForDistanceMismatch
When `whiteboardPlaceholder` is assigned, this method returns early. Moving journalTable at runtime shifts the placeholder away from its designed position, breaking alignment. The scene layout is authoritative when a placeholder exists.

---

## 6. Layer Configuration

| Layer | Name | Used By |
|---|---|---|
| 10 | Whiteboard | WhiteboardPen, JournalSessionManager (post-transition) |
| 31 | PassthroughUI | PassthroughManager, CalibrationGuide, SurfaceDotGrid, instruction text |

---

## 7. Quick Setup Checklist

- [ ] Open `3D_Journal_playground` scene
- [ ] Run **Tools > MR Journal > Setup Scene References**
- [ ] Verify `whiteboardPlaceholder` is assigned (BoxCollider child of JournalTable)
- [ ] Verify XR Origin prefab contains **XRInteractionManager**
- [ ] Set **Gemini API key** on DigitalInkManager (if using LLM/VLM recognition)
- [ ] Build target: **Android** (Meta Quest 3)
- [ ] Verify **OpenXR Feature Groups** are all enabled

---

## 8. Testing Tips

- **skipPlaneDetection = true** (current) → hand-only mode, no Scene Model needed
- Test passthrough transitions in headset only (XR Simulator doesn't support ARCameraManager)
- Check adb logcat for `[ARTableDetector] Using CameraFloorOffsetObject:` to verify correct transform is used
- If detection times out (15s), fallback whiteboard spawns at virtual table position
- Re-calibration available mid-session via `RequestReCalibration()`
