# Mixed Reality Journaling Plan for `3D_Journal_playground.unity`

## Context

The current journaling whiteboard is spawned via a left thumb+middle finger pinch held for 2 seconds (`WhiteboardUtils.cs`). The user wants to replace this with a Mixed Reality flow — similar to Meta Quest 3's Surface Keyboard — where the user places palms flat on a real table to spawn a **horizontal** whiteboard aligned with the real surface. This grounds the virtual writing on physical furniture, improving comfort during self-reflection journaling.

The project currently has **no passthrough support** — it uses pure OpenXR (1.16.1) with XR Hands (1.7.3) on Meta Quest 3.

---

## User Flow

```
[VR World] → User approaches JournalChairTable
     ↓ (enter trigger zone, 2m radius)
[Fade to black] (0.5s)
     ↓
[MR Passthrough] → User sees real world + floating instruction:
                    "Place both hands flat on your table"
     ↓
[Palms-Flat Detection] → User rests both palms on real table for ~2s
                          (progress ring grows, like Surface Keyboard)
     ↓ (table plane detected from hand positions)
[Alignment] → Translucent whiteboard preview appears on table (0.5s)
     ↓ (auto-confirm after 1.5s)
[Fade to black] (0.5s)
     ↓
[VR World] → JournalTable aligned to real table, horizontal whiteboard spawned
     ↓
[Journaling] → User writes with right hand index finger (existing system)
```

Total transition: ~5s (excluding user finding their table).

---

## Phase 1: Package Setup

### Add `com.unity.xr.meta-openxr` to [manifest.json](Packages/manifest.json)

```json
"com.unity.xr.meta-openxr": "2.1.0"
```

This provides passthrough via `XR_FB_passthrough` (environment blend mode API).

**No Scene Understanding needed** — we detect the table surface purely from hand positions (palms-flat), keeping it simple like Surface Keyboard.

### Enable OpenXR Feature (Unity Editor)

**Project Settings > XR Plug-in Management > OpenXR > Android:**
- Enable **Meta Quest: Environment** (passthrough)

### Android Manifest

**New file:** `Assets/Plugins/Android/AndroidManifest.xml`

```xml
<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />
```

---

## Phase 2: New Scripts

### 2.1 `Assets/Scripts/MixedReality/PassthroughManager.cs`

Controls VR ↔ MR transitions:

- `EnterPassthrough()` — fade to black (0.5s) → set `XrEnvironmentBlendMode.AlphaBlend` → fade in (0.5s)
- `ExitPassthrough()` — reverse, set `Opaque`
- Uses a world-space **fade quad** parented to camera at near-clip distance (unlit transparent shader, animate alpha 0→1→0)
- Camera background color set to `(0,0,0,0)` during passthrough for transparency
- All transitions use ease-in-out curves (matching `SubtleLightBreathing.cs` pattern)

### 2.2 `Assets/Scripts/MixedReality/SurfaceDetector.cs`

Detects table surface from hand poses — **the core Surface Keyboard-like interaction**:

**Detection logic:**
1. Check both hands tracked via XR Hands API
2. Read `XRHandJointID.Palm` pose for each hand — check palm normal faces down (dot with `Vector3.down` > 0.85)
3. Verify all 10 fingertip joints are within 3cm Y-range of their respective palm center (fingers spread flat, not curled)
4. Both hands must satisfy conditions simultaneously
5. Start a **2-second timer** with visual progress ring (reuse existing `SpherePrefab` indicator pattern from `WhiteboardUtils`, scaling + color lerp red→green)
6. On completion: calculate table plane from 10 fingertip + 2 palm positions (average Y for height, least-squares for orientation)
7. Return: `TablePlane` struct with `Vector3 position`, `Quaternion rotation`, `Vector2 estimatedSize`

**Estimated table size:** based on distance between left and right palm centers (typically 30-60cm apart). Width = palm distance × 1.5, depth = 40cm default. Clamped to min 30×20cm, max 80×60cm.

**Events:**
- `OnDetectionProgress(float 0-1)` — for UI progress ring
- `OnTableDetected(TablePlane)` — detection complete
- `OnDetectionLost()` — hands lifted before timer completes

### 2.3 `Assets/Scripts/MixedReality/JournalSessionManager.cs`

State machine orchestrating the full flow:

```
Idle → Approaching → Passthrough → SurfaceDetection → Aligning → TransitionToVR → Journaling
```

| State | Behavior |
|-------|----------|
| **Idle** | JournalChairTable at default position. Waiting for proximity. |
| **Approaching** | User enters 2m trigger zone. Shows gentle floating text prompt. |
| **Passthrough** | Calls `PassthroughManager.EnterPassthrough()`. Displays instruction: *"Place both hands flat on your table"* |
| **SurfaceDetection** | Listens to `SurfaceDetector` events. Progress ring visible near hands. |
| **Aligning** | Moves `JournalChairTable` parent so `JournalTable` matches detected surface (height, position, rotation). Chair positioned behind table relative to user facing direction. Smooth lerp 0.5s. Shows translucent whiteboard preview on table for 1.5s. |
| **TransitionToVR** | `PassthroughManager.ExitPassthrough()`. Calls `WhiteboardUtils.SpawnAligned()` to create horizontal whiteboard on table. |
| **Journaling** | Existing `WhiteboardPen` + `DigitalInkBridge` + `RecognitionPipeline` take over. |

**Exit:** Pinky-thumb pinch (existing gesture) cancels at any state and returns to Idle/VR.

### 2.4 `Assets/Scripts/MixedReality/ProximityTrigger.cs`

Simple trigger on JournalChairTable:
- SphereCollider (isTrigger, radius 2m)
- `OnPlayerEnter` / `OnPlayerExit` events
- Detects XR Origin camera or player collider

---

## Phase 3: Modifications to Existing Scripts

### 3.1 [WhiteboardUtils.cs](Assets/Scripts/Handwriting/WhiteboardUtils.cs) — Add `SpawnAligned()`

New public method:
```csharp
public GameObject SpawnAligned(Vector3 position, Quaternion rotation, Vector2 size)
```
- Instantiates `WhiteboardPrefab` at exact position/rotation/size
- **Horizontal orientation**: whiteboard face-up (normal = `Vector3.up`), rotated so "top" of writing surface faces away from user
- The existing Whiteboard prefab's default plane is 10×10 scaled at 0.1 — `SpawnAligned` will set `localScale` based on the `size` parameter
- Calls `whiteboard.GetComponent<Whiteboard>().Initialize()` after positioning
- Keep existing pinch-to-spawn code as manual override (untouched)

Add event: `public event Action<GameObject> OnWhiteboardSpawned;`

### 3.2 [Whiteboard.cs](Assets/Scripts/Handwriting/Whiteboard.cs) — Background Color

- Add `public Color backgroundColor = Color.white`
- Apply in `Initialize()` when filling texture — use warm cream `new Color(1f, 0.97f, 0.92f)` for journal mode
- Gives a paper-like feel instead of sterile white

### 3.3 [WhiteboardPen.cs](Assets/Scripts/Handwriting/WhiteboardPen.cs) — IsDrawing Property

- Add `public bool IsDrawing => hitBoard;` (or equivalent existing field)
- Used by `JournalSessionManager` to prevent transitions while user is actively writing

---

## Phase 4: Scene Setup in `3D_Journal_playground.unity`

```
JournalChairTable (existing)
  ├── JournalTable (existing)
  ├── Chair (existing)
  ├── [ADD] SphereCollider (trigger, radius 2.0)
  ├── [ADD] JournalSessionManager component
  └── [ADD] ProximityTrigger component

[NEW] PassthroughManager (empty GameObject)
  └── PassthroughManager component

[NEW] SurfaceDetector (empty GameObject)
  └── SurfaceDetector component

[NEW] FadeQuad (child of Main Camera)
  └── Quad with unlit transparent-black material
```

---

## Comfort & Psychology Considerations

1. **Gentle transitions**: All fades use ease-in-out curves, never abrupt cuts
2. **No time pressure**: Instruction text is calm, no countdown timer shown. The 2s hold is a minimum, not a deadline — progress ring communicates state without urgency
3. **Warm color palette**: MR overlays use muted amber/cream/sage. Whiteboard background is warm cream, not clinical white
4. **Exit affordance**: Pinky-thumb pinch cancels at any point → returns to pure VR
5. **Seated safety**: After alignment, subtle prompt *"You can sit down now"* before VR transition
6. **Horizontal writing**: Flat-on-table mimics real paper journaling — more natural than wall-mounted whiteboard

---

## Fallback Strategy

| Failure | Fallback |
|---------|----------|
| `com.unity.xr.meta-openxr` unavailable | Skip passthrough entirely, spawn whiteboard at default JournalTable position in VR |
| Hands not tracked | Show prompt to enable hand tracking in Quest settings |
| Palms-flat detection fails | After 15s timeout, offer to spawn whiteboard at default position |
| Passthrough blend mode unsupported | Stay in VR, go directly to Journaling state |

---

## Verification Plan

1. **Package**: Confirm `com.unity.xr.meta-openxr` resolves and Meta Quest Environment feature appears in OpenXR settings
2. **Passthrough**: Build to Quest 3, test VR→passthrough→VR fade transitions
3. **Palms-flat detection**: Test with hands on various surfaces — desk, kitchen table, lap. Verify 2s timing and progress ring
4. **Alignment**: Place a real object on table, confirm virtual whiteboard overlaps the table surface
5. **Horizontal drawing**: Confirm `WhiteboardPen` raycasting works correctly on a face-up whiteboard (index finger pointing down onto table)
6. **Full flow**: Walk through Idle → Journaling end-to-end on Quest 3
7. **Fallbacks**: Test with hands untracked, with no table, with passthrough disabled

---

## Implementation Order

1. Add `com.unity.xr.meta-openxr` package + enable passthrough feature + AndroidManifest
2. `PassthroughManager.cs` — fade transitions + blend mode switching
3. `SurfaceDetector.cs` — palms-flat detection with progress ring
4. `WhiteboardUtils.SpawnAligned()` — horizontal whiteboard spawning
5. `JournalSessionManager.cs` — state machine wiring the full flow
6. `ProximityTrigger.cs` + scene setup (colliders, components)
7. `Whiteboard.cs` background color + `WhiteboardPen.cs` IsDrawing property
8. Polish: fallbacks, easing curves, warm colors, seated prompt
