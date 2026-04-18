# Milestone 1 — Current MR Handwriting System Snapshot

Reference snapshot of the EMILIA-VR MR journaling / handwriting pipeline as of commit `8ff2b94f feat: diy stylus manual calib` on branch `journal-feature`, before the stylus + table calibration rework. Unity 6000.3.10f1, Meta Quest 3, URP.

---

## 1. Environment & Package Constraints

- `com.unity.xr.meta-openxr` 2.4.0, `com.unity.xr.hands` 1.7.3, `com.unity.xr.interaction.toolkit` 3.3.1, `com.unity.xr.arfoundation` 6.1.2, `com.unity.xr.openxr` 1.16.1.
- **No** `com.meta.xr.sdk.core` → no `OVRPlugin`, `OVRHand`, `OVRSpatialAnchor`.
- **No** `com.meta.xr.mrutilitykit` → no `EnvironmentRaycastManager`, no Meta Room Scan API.
- Camera CPU pixels (`XRCpuImage.TryAcquireLatestCpuImage`) are not exposed on Quest 3 via this OpenXR stack → any CV-fusion path is blocked on device.
- All hand tracking flows through `XRHandSubsystem` (`leftHand` / `rightHand`), with joints in **session space**. Session → world conversion must use `XROrigin.CameraFloorOffsetObject.TransformPoint`, NOT `xrOrigin.TransformPoint`, because Device tracking mode applies a camera Y offset on the floor-offset transform.

---

## 2. MR Session State Machine

Defined in [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs) `SessionState` enum (~lines 15-45).

```
Idle
 └─ (user taps Start)
RequestingPermission  ← scene permission prompt on Quest 3
 └─ granted
Passthrough            ← fade VR → passthrough, show real room
 └─ (if not skipStylusCalibration)
StylusCalibration      ← user calibrates DIY pen tip
 └─ user pokes "Next"
PlaneDiscovery         ← AR plane scan + palm-flat watching
HandConfirmation       ← (overlapped with PlaneDiscovery) palms-down hold
 └─ confirmed
Preview                ← 2s pause on the detected pose
TransitionToVR         ← fade passthrough → VR, align scene to real table
Journaling             ← user writes
 └─ (user ends, optional ReCalibrating or Review)
Ending
```

Transitions are driven by events:
- `ARTableDetector.OnTableConfirmed` → `JournalSessionManager.OnTableConfirmed` (line ~526).
- `StylusCalibrationController.OnNextButtonPressed` → `JournalSessionManager.OnStylusCalibrationNext` (line ~449).
- `PassthroughManager.OnPassthroughExited` → `JournalSessionManager.OnceAfterPassthroughExit` (line ~608).

Whiteboard is **static in the scene** under `MainIsland/JournalChairTable/JournalTable/` — during `TransitionToVR` the XR Origin is teleported to a fixed `SeatPoint` and `JournalChairTable` is Y-adjusted so the virtual whiteboard visually overlaps the real table.

---

## 3. DIY Stylus Calibration — Current Design

All scripts live in [Assets/Scripts/Stylus/](../../Assets/Scripts/Stylus/).

### 3.1 `StylusCalibrationController.cs` (UI / flow)

- `BeginCalibration()` (line 129) spawns a green target sphere ~40 cm forward, 20 cm below eye, plus Confirm/Next buttons offset to the non-stylus side.
- User visually aligns the DIY pen tip with the green dot and pokes "Confirm" with the opposite hand's `IndexTip`.
- `DoCalibrate()` (line 213) reads the stylus hand's wrist pose once and stores:

  ```csharp
  Vector3 offset = Quaternion.Inverse(wristRot) * (targetWorldPos - wristPos);
  wristTracker.SetWristOffset(offset);
  ```

- Target color flips green → gold, "Next" button arms after 0.6 s, user pokes Next, state advances.

### 3.2 `StylusWristTracker.cs`

- Anchor joint = `XRHandJointID.Wrist` (lowest jitter of all hand joints, natural pivot for a held pen).
- Stores one `Vector3 wristLocalOffset` (wrist-local frame) + `bool isCalibrated`.
- Runtime tip = `wristWorldPos + wristWorldRot * wristLocalOffset` (line 81). Rotation-aware because offset is stored in wrist-local space.
- `TryGetWristPose()` (line 91) resolves session → world via `CameraFloorOffsetObject`.

### 3.3 `StylusTipProvider.cs` (singleton, `DefaultExecutionOrder(-25)`)

- Consumer-facing API: `Vector3? TipWorldPosition`, `bool IsCalibrated`, `Plane WritingPlane`.
- Each Update: pulls wrist-tracker tip, optionally blends in `GreenBandDetector` CV, applies per-axis `OneEuroFilter` (min cutoff 2.0 Hz, beta 0.05), then soft-snaps to writing plane within 2.5 cm band.
- `SetWritingPlane(Plane)` invoked by `JournalSessionManager` after table detection — currently `new Plane(Vector3.up, new Vector3(0, surfaceY, 0))`.
- If hand tracking lost or not calibrated, `TipWorldPosition = null` (clean signal to consumers).

### 3.4 `StylusVisualProp.cs` (cosmetic)

- Dark-grey cylinder shaft + red sphere tip, rendered along the vector from grip (thumb-index midpoint) → `TipWorldPosition`.
- `LateUpdate` driven; smooth-projects grip onto the wrist-tip line (0.15 blend) to reduce jitter.
- Visibility gated on `PropEnabled` (Journaling-only) + `tipProvider.IsCalibrated` + confidence.

### 3.5 `GreenBandDetector.cs` (blocked on-device)

- Scans passthrough camera for a green rubber band on the DIY pen via HSV threshold → contour → unproject onto writing plane.
- Blocked because camera CPU pixels are not accessible on Quest 3 via the current OpenXR stack. Effectively dead code; `StylusTipProvider.cvDetector` can be null without functional impact.

### 3.6 Known accuracy limitations

| # | Issue | Impact |
|---|---|---|
| 1 | Single-sample calibration | Wrist-rotation noise at one pose projects into offset → ~5–15 mm drift when wrist rotates during writing. |
| 2 | Mid-air target | User visually judges alignment of pen tip with a floating dot — no proprioceptive feedback. |
| 3 | No persistence | Every launch recalibrates from scratch. |
| 4 | No mid-session recovery | Any drift forces a full re-calibrate (currently via the `ReCalibrating` state). |
| 5 | CV fusion unreachable | `GreenBandDetector` can't run on device. |

---

## 4. Table / Plane Calibration — Current Design

All scripts live in [Assets/Scripts/MixedReality/](../../Assets/Scripts/MixedReality/).

### 4.1 `ARTableDetector.cs` (~686 lines — primary detector)

Two simultaneous detection paths:

**AR plane scan** (`ScanPlanes()`, line 333):
- Uses `ARPlaneManager` from AR Foundation.
- Filters: `alignment == PlaneAlignment.HorizontalUp`, not subsumed, height above floor in `[0.45 m, 1.10 m]`.
- Scores candidates: proximity to user (0–30), area (0–20), height plausibility (0–20), classification bonus (+50 if Meta's `PlaneClassifications.Table` flag set).
- Timeout: if no planes appear within `planeFallbackTimeout` (5 s), switches to hand-only mode.

**Palm-flat confirmation** (`IsPalmFlat`, line 581):
- Reads `XRHandJointID.Palm`; palm-local `-Y` must be within `palmDownAngleThreshold` (20°) of world down.
- All 5 fingertips (`ThumbTip, IndexTip, MiddleTip, RingTip, LittleTip`) must be within `fingertipYTolerance` (3 cm) of palm Y.
- Hold for `holdDuration` (1.5 s) while both palms flat; accumulate eye Y and fingertip Y over the hold.

On confirmation, emits `DetectedTable`:
```csharp
struct DetectedTable {
  Vector3 position; Quaternion rotation; Vector2 size;
  Vector3 userHeadPosition; Vector3 userForward;
  float avgEyeY;               // mean camera Y during hold
  float avgPalmSurfaceY;       // mean fingertip Y - 0.003 m (or palm Y - 0.012 m fallback)
  ARPlane sourcePlane;         // optional, present if AR path succeeded
}
```

### 4.2 `JournalSessionManager` consumption

- `OnTableConfirmed(DetectedTable)` (line ~526) stores `capturedRealEyeHeight = table.avgEyeY`.
- `AlignVRWorldToTable()` (line ~669) moves `JournalChairTable` so its internal `WhiteboardPlaceholder` Y matches `table.position.y + tableHeightBias`.
- `TeleportToSeatPoint()` (line ~739) computes `targetEyeY = virtualTableY + realEyeAboveTable + calibrationHeightBias` and moves the XR Origin accordingly.
- Rotation is set via `Quaternion.LookRotation(tableToUser, Vector3.up)` — the table is oriented to face the user at detection time, not respecting a user-drawn edge.

### 4.3 Height bias fields (commits `9929d61f`, `0c7277d4`)

- `tableHeightBias` (JournalSessionManager.cs line ~105) — compensates for the 12–30 mm overshoot between hand joints and the real table surface.
- `calibrationHeightBias` (line ~161) — post-session fine-tuning, ±15 cm slider for comfort. These exist because palm/fingertip Y is not the true surface Y; a measurement that directly samples the surface removes the need for them.

### 4.4 Supporting systems

- [CalibrationGuide.cs](../../Assets/Scripts/MixedReality/CalibrationGuide.cs) — palm indicator spheres + plane outlines during hold.
- [PassthroughManager.cs](../../Assets/Scripts/MixedReality/PassthroughManager.cs) — passthrough fade in/out; emits `OnPassthroughExited`.
- [AlignmentAnchor.cs](../../Assets/Scripts/MixedReality/AlignmentAnchor.cs) — spatial-anchor style drift correction at the detected table pose (no OVRSpatialAnchor — uses a custom mechanism given the package constraints).
- [SurfaceDetector.cs](../../Assets/Scripts/MixedReality/SurfaceDetector.cs) — legacy hand-only detector, kept only for the `3D_Journal_playground` scene.

### 4.5 Known accuracy limitations

| # | Issue | Impact |
|---|---|---|
| 1 | AR plane convergence | Plane detection takes 3–10 s indoors on Quest 3 and often misses small/thin tables. |
| 2 | Palm-Y ≠ surface-Y | Palm thickness adds ~12 mm, fingertip adds ~3 mm; compensated only by hand-tuned bias fields. |
| 3 | Yaw guessed from head | Whiteboard rotation is inferred from `tableToUser`, not user-specified; often misaligned with the user's comfortable writing direction. |
| 4 | Tiring hold | 1.5 s of both palms flat is fatiguing and easy to break mid-hold. |
| 5 | State timeout complexity | Multiple fallback paths (AR → hand-only, hold lost → restart) complicate the orchestrator. |

---

## 5. Handwriting / Drawing Pipeline — Reused As-Is

All scripts live in [Assets/Scripts/Handwriting/](../../Assets/Scripts/Handwriting/). The rework does NOT touch these.

### 5.1 Ink capture

- [WhiteboardPen.cs](../../Assets/Scripts/Handwriting/WhiteboardPen.cs) (~1100 lines) — per-frame hand/stylus → whiteboard texture coordinates.
  - Lines 641-663: STYLUS mode override. If `stylusTipProvider.IsCalibrated && TipWorldPosition.HasValue`, uses `tip + 2.5 cm up` as ray origin, `Vector3.down` as direction, 3 cm range. Otherwise falls back to finger tracking.
  - Two-pass raycast: first exact contact (no tolerance), then passthrough-tolerance after contact confirmed.
  - `AppendBufferedPoint(worldPos, timestampMs)` → `BufferedInkPoint[]` → `FinalizeActiveStrokeIfNeeded()` (line 380) validates min length/duration.

### 5.2 Rendering

- [Whiteboard.cs](../../Assets/Scripts/Handwriting/Whiteboard.cs) — owns a `Texture2D` at a fixed pixel density (`PPU = 1000`, 1 px = 1 mm). `SetTouchPosition` / `SetHoverPosition` called each frame; dirty-region tracking uploads only changed pixels to GPU.
- [DrawCircle.cs](../../Assets/Scripts/Handwriting/DrawCircle.cs) — anti-aliased circle stamping with block-based read/write for mobile GPU efficiency.

### 5.3 Recognition

- [RecognitionPipeline.cs](../../Assets/Scripts/Handwriting/RecognitionPipeline.cs) — debounces strokes, fires `OnFinalTextRecognized`.
- [DigitalInkBridge.cs](../../Assets/Scripts/Handwriting/DigitalInkBridge.cs) — C#/JNI bridge to Android ML Kit Digital Ink Recognition. Editor-skipped.
- [GeminiService.cs](../../Assets/Scripts/Handwriting/GeminiService.cs) — optional LLM refinement of top-K candidates (API key optional).

### 5.4 Text layout & pagination

- [ScribbleManager.cs](../../Assets/Scripts/Handwriting/ScribbleManager.cs) — converts recognized words into `ScribbleWord` bounds; manages per-page undo stack; detects scratch-to-delete.
- [WhiteboardPageManager.cs](../../Assets/Scripts/Handwriting/WhiteboardPageManager.cs) — owns the world-space UI canvas, result TMP, navigation buttons.

### 5.5 Save

- `JournalSessionManager.SaveJournalCoroutine` (line ~1106) collects title + content → `ServiceManager.Instance.JournalService.CreateJournal()` → async sentiment update via `AnalyzeAndSaveSentiment` (Gemini).
- Ink itself is not persisted as image — only the recognized text + timestamps.

---

## 6. Integration Points for the Rework

Single input to the drawing pipeline:

```
user hand (real)
  └─ XRHandSubsystem (session space)
       └─ StylusWristTracker.TryGetTipPosition()   ← calibration-dependent
            └─ StylusTipProvider.TipWorldPosition  ← filtered, snapped
                 └─ WhiteboardPen (raycast onto whiteboard)
                      └─ Whiteboard.SetTouchPosition → Texture2D
```

The rework only needs to:
1. Populate `StylusWristTracker.wristLocalOffset` more accurately (multi-sample "touch the fingertip" approach → Phase 2 of the plan).
2. Produce a better `DetectedTable` pose for `JournalSessionManager.OnTableConfirmed` (4-tap rectangle → Phase 3 of the plan).

Everything downstream — stroke capture, pixel stamping, ML Kit recognition, save pipeline — is untouched and known-good.

---

## 7. Scene Inventory (`Assets/Scenes/use/3D_Journal.unity`)

Key GameObjects:
- `XR Origin` → `Camera Offset` → `Main Camera` (TrackedPoseDriver). `ARPlaneManager` component attached.
- `MainIsland` → `JournalChairTable` (moved during alignment) → `JournalTable` → `WhiteboardPlaceholder` (BoxCollider, surface-Y reference) + `Whiteboard` (static mesh, Layer 10, `Whiteboard` script).
- `SeatPoint` — teleport target for seated player.
- `ARTableDetector`, `CalibrationGuide`, `PassthroughManager`, `StylusCalibrationController`, `StylusTipProvider`, `StylusWristTracker`, `StylusVisualProp` — all singleton managers parented at scene root.

---

## 8. Files at a Glance

**Stylus (rewritten in Phase 2):**
- [Assets/Scripts/Stylus/StylusCalibrationController.cs](../../Assets/Scripts/Stylus/StylusCalibrationController.cs) — 531 lines
- [Assets/Scripts/Stylus/StylusWristTracker.cs](../../Assets/Scripts/Stylus/StylusWristTracker.cs) — 174 lines
- [Assets/Scripts/Stylus/StylusTipProvider.cs](../../Assets/Scripts/Stylus/StylusTipProvider.cs) — 166 lines
- [Assets/Scripts/Stylus/StylusVisualProp.cs](../../Assets/Scripts/Stylus/StylusVisualProp.cs) — 220 lines
- [Assets/Scripts/Stylus/GreenBandDetector.cs](../../Assets/Scripts/Stylus/GreenBandDetector.cs) — 312 lines (to archive)

**Table (replaced in Phase 3):**
- [Assets/Scripts/MixedReality/ARTableDetector.cs](../../Assets/Scripts/MixedReality/ARTableDetector.cs) — 686 lines (to archive)
- [Assets/Scripts/MixedReality/SurfaceDetector.cs](../../Assets/Scripts/MixedReality/SurfaceDetector.cs) — 293 lines (already legacy, to archive)
- [Assets/Scripts/MixedReality/CalibrationGuide.cs](../../Assets/Scripts/MixedReality/CalibrationGuide.cs) — to simplify or archive

**Orchestrator (patched, not rewritten):**
- [Assets/Scripts/MixedReality/JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs) — swap detector reference, drop `tableHeightBias`, remove ARPlaneManager toggles, keep the rest intact.

**Untouched (drawing, recognition, persistence):**
- [Assets/Scripts/Handwriting/WhiteboardPen.cs](../../Assets/Scripts/Handwriting/WhiteboardPen.cs)
- [Assets/Scripts/Handwriting/Whiteboard.cs](../../Assets/Scripts/Handwriting/Whiteboard.cs)
- [Assets/Scripts/Handwriting/DrawCircle.cs](../../Assets/Scripts/Handwriting/DrawCircle.cs)
- [Assets/Scripts/Handwriting/DigitalInkBridge.cs](../../Assets/Scripts/Handwriting/DigitalInkBridge.cs)
- [Assets/Scripts/Handwriting/RecognitionPipeline.cs](../../Assets/Scripts/Handwriting/RecognitionPipeline.cs)
- [Assets/Scripts/Handwriting/ScribbleManager.cs](../../Assets/Scripts/Handwriting/ScribbleManager.cs)
- [Assets/Scripts/Handwriting/WhiteboardPageManager.cs](../../Assets/Scripts/Handwriting/WhiteboardPageManager.cs)
- [Assets/Scripts/Handwriting/GeminiService.cs](../../Assets/Scripts/Handwriting/GeminiService.cs)
- [Assets/Scripts/Handwriting/OneEuroFilter.cs](../../Assets/Scripts/Handwriting/OneEuroFilter.cs)
