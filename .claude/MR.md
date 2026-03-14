# Mixed Reality Architecture Analysis

## 1. Executive Summary

**Question:** Should the current AR Foundation + OpenXR approach be replaced with OVRPassthroughLayer + MRUK (EnvironmentRaycastManager, PlaceBox, CheckBox)?

**Answer: No — the current approach is architecturally superior for this project.** The proposal in the task description has several fundamental misconceptions about Meta's SDK landscape. The current implementation is well-designed, uses the correct modern APIs, and only needs targeted improvements — not a rewrite.

---

## 2. Why the Proposed MRUK/OVR Approach Is Wrong

### 2.1 OVRPassthroughLayer requires the deprecated Oculus XR Plugin

The task asks to "dynamically enable the `OVRPassthroughLayer` component." This component belongs to the **Oculus Integration SDK / Meta XR Core SDK**, which requires the **Oculus XR Plugin** (`com.unity.xr.oculus`). The project explicitly uses the **OpenXR stack** (`com.unity.xr.openxr` 1.16.1 + `com.unity.xr.meta-openxr` 2.4.0). These two plugin backends are **mutually exclusive** — you cannot use both simultaneously.

Switching to OVRPassthroughLayer would require:
- Removing the OpenXR plugin
- Adding the Oculus XR Plugin (which Meta is deprecating in favor of OpenXR)
- Rewriting all XR Interaction Toolkit interactions (the project uses XRI 3.3.1)
- Rewriting all XR Hands API code (WhiteboardPen, WhiteboardUtils, SurfaceDetector, ARTableDetector, JournalStartButton)
- Contradicting the task's own constraint: *"Do not use the deprecated Oculus XR Plugin"*

The current `PassthroughManager.cs` achieves the same result via `ARCameraManager` + `ARCameraBackground` (Meta Quest Camera Passthrough OpenXR feature). This is the **correct, modern approach**.

### 2.2 MRUK EnvironmentRaycastManager requires Scene Model, not "live depth buffer"

The task asks to "execute a Raycast against the live depth buffer to extract the Y-elevation and normal vector." This is not how MRUK's `EnvironmentRaycastManager` works. It raycasts against the **pre-scanned Scene Model** (the room mesh created during Space Setup), not a live depth buffer. Key problems:

- **Requires Space Setup:** The user must run Quest's Room Setup beforehand, which pre-scans the room and labels furniture. This is a one-time setup per physical space — it doesn't work "ad-hoc" as the task requests.
- **Not ad-hoc table detection:** MRUK's Scene Model gives you pre-labeled anchors (TABLE, COUCH, etc.). You query labeled anchors, you don't "detect" tables in real-time.
- **Heavy dependency:** MRUK (`com.meta.xr.mrutilitykit`) pulls in the full Meta XR Core SDK stack, which conflicts with the project's pure OpenXR architecture.

The current approach — `ARPlaneManager` detects planes in real-time via the Meta OpenXR Scene Understanding extension, combined with hand gesture confirmation — is genuinely **more ad-hoc and more flexible** than MRUK.

### 2.3 PlaceBox / CheckBox are MRUK Scene API utilities

The task asks to "use MRUK's PlaceBox and CheckBox APIs to validate that the physical table has enough unobstructed volumetric space." These APIs:

- Operate on the **Scene Model's semantic mesh**, not on live geometry
- Require labeled room anchors to define "obstructed" vs "free" space
- Are designed for persistent room-scale placement (e.g., placing a virtual TV on a real wall), not for ad-hoc table calibration
- Would add no value here: the user is physically placing their hands on a table they can see in passthrough — they ARE the spatial validator

### 2.4 "Interaction SDK to monitor hand kinematics" — wrong SDK

The task references "the Interaction SDK" for hand monitoring. The Meta Interaction SDK (`com.meta.xr.sdk.interaction`) is the OVR-ecosystem counterpart to Unity's XR Interaction Toolkit. This project uses **XRI 3.3.1 + XR Hands 1.7.3** — the cross-platform equivalent. Switching to Meta's Interaction SDK would again require the Oculus XR Plugin.

---

## 3. Current Architecture Assessment

### 3.1 What's Already Implemented (8 scripts, ~2,542 lines)

| Script | Lines | Role | Quality |
|--------|-------|------|---------|
| `JournalSessionManager.cs` | 723 | State machine orchestrator | Excellent — 10-state FSM, permission handling, fallback, re-calibration, cancel |
| `ARTableDetector.cs` | 508 | AR plane + hand gesture table detection | Very good — dual-path (AR planes primary, hand-only fallback), scoring heuristic |
| `CalibrationGuide.cs` | 362 | Visual feedback (plane highlights, palm indicators, instructions) | Good — comprehensive visual feedback system |
| `SurfaceDetector.cs` | 294 | Legacy hand-only detection | Good but superseded — `ARTableDetector` includes this as fallback |
| `PassthroughManager.cs` | 282 | VR↔MR transitions via ARCameraManager | Very good — fade-through-black, culling mask isolation, proper state management |
| `JournalStartButton.cs` | 208 | World-space button (hand + controller) | Good — dual input support, visual feedback, cooldown |
| `AlignmentAnchor.cs` | 123 | ARAnchor for drift correction | Good — async anchor creation, continuous drift correction in LateUpdate |
| `ProximityTrigger.cs` | 42 | Simple trigger zone | Fine — minimal correct implementation |

### 3.2 Strengths of Current Design

1. **Correct SDK choices:** OpenXR + Meta OpenXR extension + AR Foundation + XR Hands + XRI. This is the forward-looking Meta-recommended stack.
2. **Graceful degradation:** ARTableDetector falls back to hand-only if AR planes aren't available. JournalSessionManager falls back to default spawn after 15s timeout. AlignmentAnchor degrades without anchors.
3. **Clean state machine:** 10-state FSM with proper guards, timeout handling, and cancellation at every stage.
4. **Separation of concerns:** Detection, visual feedback, passthrough management, anchoring, and orchestration are all separate components.
5. **Permission handling:** Runtime `USE_SCENE` permission request with denial fallback.
6. **Re-calibration support:** Mid-session re-calibration via `RequestReCalibration()`.

---

## 4. Real Issues & Suggested Improvements

While the architecture is correct, there are genuine improvements worth making:

### 4.1 HIGH: CalibrationGuide.UpdatePalmIndicators() is never called

`CalibrationGuide` exposes `UpdatePalmIndicators()` (line 113) but `JournalSessionManager` never calls it during the PlaneDiscovery/HandConfirmation states. The palm indicators will never appear.

**Fix:** Add a call in `JournalSessionManager.Update()` during detection phases:

```csharp
// In JournalSessionManager.Update(), inside the detection state block:
if (calibrationGuide != null && arTableDetector != null)
{
    var hs = WhiteboardPen.GetHandSubsystem();
    if (hs != null)
    {
        bool leftFlat = arTableDetector.IsPalmFlat(hs.leftHand, out Vector3 lp);
        bool rightFlat = arTableDetector.IsPalmFlat(hs.rightHand, out Vector3 rp);
        calibrationGuide.UpdatePalmIndicators(
            hs.leftHand.isTracked, lp, leftFlat,
            hs.rightHand.isTracked, rp, rightFlat);
    }
}
```

### 4.2 HIGH: SurfaceDetector.cs is dead code

`SurfaceDetector` is the legacy hand-only detector, fully superseded by `ARTableDetector` (which includes the same fallback logic). No script references `SurfaceDetector`. It should either be:
- Deleted to reduce confusion, OR
- Kept but marked with `[Obsolete("Use ARTableDetector instead")]`

### 4.3 MEDIUM: No haptic feedback on button press or table confirmation

`JournalStartButton.TriggerPress()` provides visual feedback but no haptic pulse. For VR, haptic feedback on confirmation events significantly improves perceived responsiveness.

**Fix:** Use `XRBaseController.SendHapticImpulse()` or `OpenXRInput.SendHapticImpulse()` on press.

### 4.4 MEDIUM: Fade quad FOV calculation assumes symmetric projection

`PassthroughManager.CreateFadeQuad()` (line 233) uses `Camera.fieldOfView` and `Camera.aspect` which may not be accurate on Quest 3 with asymmetric projection. The 1.5x scale multiplier compensates but is a rough heuristic.

**Fix:** Either increase the multiplier to 2.0x for safety, or use `Camera.projectionMatrix` to compute exact corners:

```csharp
Matrix4x4 proj = mainCamera.projectionMatrix;
float nearDist = nearClip + 0.01f;
// Extract frustum dimensions from projection matrix
float left   = nearDist * (proj[2,0] - 1f) / proj[0,0];
float right  = nearDist * (proj[2,0] + 1f) / proj[0,0];
float bottom = nearDist * (proj[2,1] - 1f) / proj[1,1];
float top    = nearDist * (proj[2,1] + 1f) / proj[1,1];
float width  = (right - left) * 1.2f;  // small margin
float height = (top - bottom) * 1.2f;
```

### 4.5 MEDIUM: CalibrationGuide leaks materials

`CreatePlaneHighlight()` (line 336) creates `new Material(...)` but the materials are never explicitly destroyed. When `ClearPlaneHighlights()` calls `Destroy(obj)`, the GameObject is destroyed but Unity does not automatically destroy materials created with `new Material()`.

**Fix:** Track materials or destroy them in `ClearPlaneHighlights()`:

```csharp
private void ClearPlaneHighlights()
{
    foreach (var obj in planeHighlights)
    {
        if (obj != null)
        {
            var rend = obj.GetComponent<Renderer>();
            if (rend != null && rend.material != null)
                Destroy(rend.material);
            Destroy(obj);
        }
    }
    planeHighlights.Clear();
}
```

### 4.6 MEDIUM: PassthroughManager also leaks fade quad material on scene unload

`PassthroughManager.OnDestroy()` destroys `fadeMaterial` but doesn't destroy `fadeQuadObj`. If the manager is destroyed before the scene unloads, the quad orphans.

**Fix:** Also destroy the quad:

```csharp
private void OnDestroy()
{
    if (fadeQuadObj != null)
        Destroy(fadeQuadObj);
    if (fadeMaterial != null)
        Destroy(fadeMaterial);
}
```

### 4.7 LOW: Floor detection heuristic is fragile

`ARTableDetector.DetectFloorHeight()` assumes the lowest horizontal plane is the floor. On Quest 3, if the user's room has a lower shelf or step stool detected first, the floor height will be wrong, causing table height filtering to fail.

**Fix:** Use `PlaneClassifications.Floor` when available:

```csharp
private void DetectFloorHeight()
{
    if (floorDetected) return;
    float lowestY = float.MaxValue;
    bool haveClassified = false;

    foreach (var plane in planeManager.trackables)
    {
        if (plane.alignment != PlaneAlignment.HorizontalUp) continue;

        // Prefer classified floor planes
        if (plane.classifications.HasFlag(PlaneClassifications.Floor))
        {
            if (!haveClassified || plane.transform.position.y < floorHeight)
            {
                floorHeight = plane.transform.position.y;
                haveClassified = true;
            }
        }
        else if (!haveClassified && plane.transform.position.y < lowestY)
        {
            lowestY = plane.transform.position.y;
        }
    }

    if (haveClassified)
    {
        floorDetected = true;
    }
    else if (lowestY < float.MaxValue)
    {
        floorHeight = lowestY;
        floorDetected = true;
    }
}
```

### 4.8 LOW: JournalSessionManager.Update() allocates instruction text billboard every frame

`UpdateInstructionPosition()` runs every frame during passthrough when CalibrationGuide is null. This is fine performance-wise, but the fallback TextMeshPro instruction and the CalibrationGuide instruction do overlapping work. Minor cleanup: the fallback path in `JournalSessionManager` should be unnecessary if CalibrationGuide is always assigned.

### 4.9 LOW: No spatial validation before whiteboard spawn

The task's concern about spatial validation (checking there's room for the whiteboard) is legitimate, just not solved by MRUK. A simpler approach using AR Foundation:

```csharp
// Before spawning, check no other AR planes intersect the whiteboard area
private bool ValidateSpawnArea(Vector3 center, Vector2 size, float planeY)
{
    // Simple overlap check: no vertical planes within the whiteboard footprint
    foreach (var plane in arPlaneManager.trackables)
    {
        if (plane.alignment == PlaneAlignment.Vertical)
        {
            Vector3 planePos = plane.transform.position;
            float dx = Mathf.Abs(planePos.x - center.x);
            float dz = Mathf.Abs(planePos.z - center.z);
            if (dx < size.x / 2f && dz < size.y / 2f
                && planePos.y > planeY && planePos.y < planeY + 0.5f)
            {
                return false; // Vertical obstacle within whiteboard footprint
            }
        }
    }
    return true;
}
```

This is much simpler and doesn't require MRUK's heavy Scene Model dependency.

---

## 5. Summary: What to Do

| Action | Priority | Effort |
|--------|----------|--------|
| Wire up `CalibrationGuide.UpdatePalmIndicators()` in JournalSessionManager | HIGH | 15 min |
| Delete or deprecate `SurfaceDetector.cs` | HIGH | 5 min |
| Fix material leak in `CalibrationGuide.ClearPlaneHighlights()` | MEDIUM | 5 min |
| Fix fade quad cleanup in `PassthroughManager.OnDestroy()` | MEDIUM | 2 min |
| Add haptic feedback on button press and table confirmation | MEDIUM | 30 min |
| Improve fade quad sizing for asymmetric projection | MEDIUM | 15 min |
| Improve floor detection to prefer classified floor planes | LOW | 10 min |
| Add simple spatial validation before whiteboard spawn | LOW | 20 min |

**Do NOT:**
- Switch to OVRPassthroughLayer (wrong SDK, deprecated path)
- Add MRUK dependency (requires Oculus XR Plugin, overkill for this use case)
- Use Meta Interaction SDK (conflicts with XRI 3.3.1)
- Use AR Foundation for spatial queries per the task (AR Foundation IS already being used correctly — ARPlaneManager)

---

## 6. Package Stack Confirmation

Current packages (correct, keep as-is):

| Package | Version | Role |
|---------|---------|------|
| `com.unity.xr.openxr` | 1.16.1 | OpenXR runtime |
| `com.unity.xr.meta-openxr` | 2.4.0 | Meta Quest features (passthrough, scene) |
| `com.unity.xr.arfoundation` | 6.1.2 | AR plane detection, anchors |
| `com.unity.xr.hands` | 1.7.3 | Hand tracking |
| `com.unity.xr.interaction.toolkit` | 3.3.1 | XR interaction (rays, grabs, pokes) |

This is Meta's recommended OpenXR stack for Unity 6. No changes needed.
