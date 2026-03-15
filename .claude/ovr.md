# EMILIA-VR Mixed Reality Journaling System

## Overview

The MR Journaling system allows the player to transition between the virtual game world and real-world passthrough on Meta Quest 3. The player locates a real table in their physical space, confirms it with a hand gesture, and then returns to the virtual world where they are seated at a virtual desk with a whiteboard for handwriting.

### Key Concept: SeatPoint Calibration

Instead of complex world-alignment maths, the system uses a **SeatPoint anchor**:

1. Player enters **passthrough mode** (sees the real world).
2. Player places **both hands flat** on their real table (like the Quest 3 Surface Keyboard gesture).
3. The system measures the **real-world head-to-table distance**.
4. Player returns to the **virtual world** — the XR Rig is teleported to the `SeatPoint` GameObject.
5. If the real-world distance differs from the virtual chair-to-table distance, the **virtual table is offset** automatically.

This means the virtual table always feels like it's at the same distance as the real one.

---

## Architecture

### State Machine

The session progresses through these states (managed by `JournalSessionManager`):

```
Idle
  │
  ▼  (Start button pressed)
RequestingPermission
  │
  ▼  (USE_SCENE permission granted/denied)
Passthrough
  │
  ▼  (Fade complete → passthrough visible)
PlaneDiscovery
  │
  ▼  (AR planes found or fallback timeout)
HandConfirmation
  │
  ▼  (Both palms flat for 1.5s)
Preview
  │
  ▼  (Whiteboard shown on real table for 2s)
TransitionToVR
  │
  ▼  (Fade back to VR + teleport to SeatPoint)
Journaling
  │
  ├──▶ ReCalibrating  (mid-session re-do)
  │
  ▼  (End session)
Ending → Idle
```

### Scripts

All scripts live in `Assets/Scripts/MixedReality/`.

| Script | Purpose |
|--------|---------|
| `JournalSessionManager.cs` | Central orchestrator. Manages the state machine, coordinates all subsystems, handles SeatPoint teleportation and distance offset. |
| `PassthroughManager.cs` | Handles VR ↔ MR transitions. Uses ARCameraBackground for passthrough rendering. Creates a fade-to-black quad for smooth transitions. |
| `ARTableDetector.cs` | Detects real-world tables via AR Foundation planes + palm-flat hand gesture. Falls back to hand-only detection if no planes available. |
| `CalibrationGuide.cs` | Visual feedback during calibration — plane highlight quads, palm indicator spheres, progress ring, billboard instruction text. |
| `AlignmentAnchor.cs` | Creates an ARAnchor at the confirmed table position. Continuously corrects drift in LateUpdate. |
| `SurfaceDetector.cs` | Legacy hand-only detector (not used by JournalSessionManager — kept for reference). |
| `JournalStartButton.cs` | World-space VR button supporting both hand tracking (fingertip poke) and XRI controllers. |
| `ProximityTrigger.cs` | Simple spherical trigger for detecting player presence. |

### Layer Conventions

| Layer | Name | Purpose |
|-------|------|---------|
| 10 | Whiteboard | Whiteboard objects during VR interaction |
| 31 | PassthroughUI | Objects visible during passthrough (fade quad, instructions, indicators) |

---

## How Passthrough Works

`PassthroughManager` uses the **ARCameraBackground** approach (not OVRPassthroughLayer):

1. **Fade to black** — A full-screen quad fades from transparent to opaque black.
2. **Switch camera** — While the screen is black:
   - `ARCameraBackground` is enabled (renders passthrough feed).
   - Camera clear flags → `SolidColor` with transparent black.
   - Culling mask → only layer 31 (hides all VR geometry; passthrough shows through transparent areas).
3. **Fade from black** — The fade quad fades back to transparent, revealing passthrough + layer 31 objects.

To exit passthrough, the process reverses: fade to black, disable ARCameraBackground, restore camera state, fade from black.

### Prerequisites

- Enable **"Meta Quest: Camera (Passthrough)"** in OpenXR Feature Groups (Project Settings > XR Plug-in Management > OpenXR).
- An **ARSession** GameObject must exist in the scene.
- The main camera must have (or will auto-get) `ARCameraManager` and `ARCameraBackground` components.

---

## How SeatPoint Calibration Works

### The Problem

The player's real-world seating arrangement (chair position, table distance) rarely matches the virtual scene's chair-and-table layout exactly. If we simply overlay the virtual table on the real table, the virtual chair might end up behind the player or in a wall.

### The Solution

**SeatPoint** is an empty GameObject placed in the scene where you want the player to sit. After calibration:

1. **Teleport**: The XR Origin is repositioned so the player's camera aligns with SeatPoint (XZ only — Y preserves tracked head height for comfort).
2. **Rotate**: The XR Origin is rotated so the player faces SeatPoint's forward direction.
3. **Distance offset**: If the real table is 0.65m away but the virtual table is 0.50m away, the virtual table is pushed 0.15m forward along SeatPoint's forward axis.

### Why This Works

- The player always ends up at the virtual desk, regardless of where they physically are.
- The virtual table distance matches the real table distance (within 5cm tolerance), so reaching for the whiteboard feels natural.
- No complex inverse-transform world alignment — just a translation and rotation of the XR Origin.

### Fallback

If `seatPoint` or `xrOrigin` is not assigned, the system falls back to the legacy `AlignVRWorldToTable` approach, which moves the JournalChairTable parent transform to match the detected real-world table position.

---

## How Table Detection Works

### Primary Path: AR Planes + Hand Confirmation

1. `ARPlaneManager` detects horizontal surfaces from the Quest 3's Scene Model.
2. `ARTableDetector` scores each plane:
   - **Proximity** to the user (closer = better, up to 3m)
   - **Size** (larger = better)
   - **Height** above floor (0.65–0.85m is ideal desk height)
   - **Classification** bonus if tagged as `Table`
3. While planes are being scored, the user places both hands palm-down on their table.
4. The system matches palm positions to the nearest candidate plane.
5. After holding for 1.5 seconds, the table is confirmed.

### Fallback Path: Hand-Only

If no AR planes appear within 5 seconds (e.g. Space Setup not completed), the system falls back:

- Table height = average Y of all palm + fingertip joints
- Table width = palm-to-palm distance × 1.5
- Table center = midpoint between palms

### Palm-Flat Detection Algorithm

A hand is considered "flat on a surface" when:

1. **Palm faces down**: The palm joint's negative-Y axis aligns with world-down within 20°.
2. **Fingers flat**: All 5 fingertip joint Y-positions are within 3cm of the palm Y-position.

Both hands must pass this check simultaneously for the hold timer to advance.

---

## Scene Setup Guide

### Step 1: Scene Hierarchy

Create these GameObjects in your scene:

```
Scene Root
├── XR Origin (XR Rig)                    ← Standard Unity XR Origin
│   └── Camera Offset
│       └── Main Camera                    ← Tag: MainCamera
├── ARSession                              ← Required for passthrough + plane detection
├── JournalChairTable                      ← Parent container
│   ├── JournalTable                       ← Virtual table surface
│   └── Chair                              ← Virtual chair
├── SeatPoint                              ← Empty GO: player teleport target
├── JournalStartButton                     ← World-space button with BoxCollider
└── [MR System]                            ← Empty GO for manager scripts
    ├── JournalSessionManager
    ├── PassthroughManager
    ├── ARTableDetector
    ├── CalibrationGuide
    └── AlignmentAnchor
```

### Step 2: Configure SeatPoint

1. Create an **empty GameObject** named `SeatPoint`.
2. Position it where you want the player to sit in the virtual world.
3. **Rotate it** so its blue arrow (forward/Z-axis) faces the table — this is where the player will look.
4. The Y-position doesn't matter (head height is preserved from tracking).

### Step 3: Add Components

**On `[MR System]` or individual GameObjects:**

| Component | Key Inspector Fields |
|-----------|---------------------|
| `JournalSessionManager` | Assign: `passthroughManager`, `arTableDetector`, `calibrationGuide`, `alignmentAnchor`, `whiteboardUtils`, `startButton`, `arPlaneManager`, `journalChairTable`, `journalTable`, `chair`, `seatPoint`, `xrOrigin` |
| `PassthroughManager` | `mainCamera` (auto-finds Camera.main), `passthroughUILayer` = 31 |
| `ARTableDetector` | `planeManager` (auto-finds ARPlaneManager) |
| `CalibrationGuide` | `tableDetector`, `passthroughManager`, `palmIndicatorPrefab` (any small sphere prefab) |
| `AlignmentAnchor` | `targetToAlign` = JournalChairTable transform |

**On the scene root:**

- Add `ARPlaneManager` (or on the same GO as ARSession).
- Add `ARAnchorManager` for drift correction.

### Step 4: Layer Setup

1. Go to **Project Settings > Tags and Layers**.
2. Set **Layer 10** = `Whiteboard`.
3. Set **Layer 31** = `PassthroughUI`.
4. Ensure the Main Camera's culling mask includes layer 31.

### Step 5: JournalStartButton

1. Create a 3D object (e.g. a Cube or custom mesh) for the button.
2. Add `JournalStartButton` component (auto-adds `BoxCollider` + `XRSimpleInteractable`).
3. Position it near the player's starting position in the virtual world.

### Step 6: XR Interaction Setup

- Ensure your XR Origin has **XR Ray Interactors** on the controllers (for button interaction).
- Ensure **XR Hand Tracking Manager** is present (for hand gesture detection).
- The `XRSimpleInteractable` on the start button handles controller selection automatically.

---

## How to Modify or Extend

### Changing Table Detection Sensitivity

In `ARTableDetector` inspector:

- `palmDownAngleThreshold` (default 20°): Increase to be more lenient with palm angle.
- `fingertipYTolerance` (default 0.03m): Increase to accept less-flat hands.
- `holdDuration` (default 1.5s): Increase for more deliberate confirmation.
- `minTableHeight` / `maxTableHeight`: Adjust for non-standard desk heights.

### Adjusting Passthrough Transitions

In `PassthroughManager` inspector:

- `fadeDuration` (default 0.5s): Duration of each fade half (fade-out + fade-in). Total transition = 2× this value.

### Customising Calibration Visuals

In `CalibrationGuide` inspector:

- `candidateColor` / `bestCandidateColor`: Colors for AR plane highlight overlays.
- `palmIdleColor` / `palmFlatColor` / `palmConfirmingColor`: Palm indicator state colors.
- `palmIndicatorPrefab`: Any prefab with a Renderer (sphere recommended).

### Adding a New Detection Method

1. Create a new MonoBehaviour that fires a `DetectedTable` event.
2. Subscribe `JournalSessionManager.OnTableConfirmed` to your event.
3. Disable `ARTableDetector` if not needed.

### Changing the Distance Mismatch Threshold

In `JournalSessionManager.AdjustTableForDistanceMismatch()`:

- The 0.05m threshold means mismatches under 5cm are ignored. Change this value if you want tighter or looser tolerance.

---

## Troubleshooting

### Passthrough doesn't appear

- **Check**: "Meta Quest: Camera (Passthrough)" is enabled in Project Settings > XR Plug-in Management > OpenXR > Meta Quest Feature Group.
- **Check**: An `ARSession` GameObject exists in the scene.
- **Check**: The camera's culling mask includes layer 31 during VR mode.
- **Check**: The Oculus app has camera permission enabled in headset settings.

### No AR planes detected

- **Check**: The user has completed **Space Setup** on the Quest 3 (Settings > Space Setup).
- **Check**: `com.oculus.permission.USE_SCENE` is in the Android manifest and granted at runtime.
- **Check**: `ARPlaneManager` exists in the scene and is properly connected to `JournalSessionManager.arPlaneManager`.
- If Space Setup is not done, the system will fall back to hand-only detection after 5 seconds.

### Hands aren't detected as flat

- Ensure lighting is adequate — Quest 3 hand tracking requires visible hands.
- Try relaxing `palmDownAngleThreshold` (e.g. 25° instead of 20°).
- Try increasing `fingertipYTolerance` (e.g. 0.04m instead of 0.03m).
- Ensure hands are fully open with fingers spread, not curled.

### Player teleports to wrong position

- Verify `SeatPoint` is positioned and rotated correctly in the scene.
- The **forward (blue) arrow** of SeatPoint should point toward the virtual table.
- Ensure `xrOrigin` references the **root** of the XR Origin, not a child.

### Virtual table feels too far or too close

- The distance mismatch system adjusts automatically, but only if the difference exceeds 5cm.
- Check the Console for `[JournalSession] Table distance offset applied` logs to see the measured distances.
- If the offset doesn't feel right, verify that `journalTable` is the correct child transform (the actual table surface, not the parent).

### Whiteboard not visible after transition

- Check that the whiteboard layer (10) is included in the camera's VR culling mask.
- Ensure `WhiteboardUtils` and its `WhiteboardPrefab` are assigned.
- Check Console for `[JournalSession] Whiteboard spawned` and `Whiteboard moved to layer 10` logs.

### Drift during journaling session

- Ensure `ARAnchorManager` exists in the scene.
- Verify `AlignmentAnchor.targetToAlign` is assigned to the `JournalChairTable` transform.
- Check Console for `[AlignmentAnchor] Anchor created` — if it says "failed", anchor support may not be available.

### CalibrationGuide palm indicators don't appear

- Assign a `palmIndicatorPrefab` in the CalibrationGuide inspector (any prefab with a Renderer).
- Ensure `CalibrationGuide.tableDetector` references the `ARTableDetector`.
- The indicators only show during `PlaneDiscovery` and `HandConfirmation` states.

---

## Quick Reference: Inspector Wiring Checklist

```
JournalSessionManager
  ├── passthroughManager     → PassthroughManager component
  ├── arTableDetector        → ARTableDetector component
  ├── calibrationGuide       → CalibrationGuide component
  ├── alignmentAnchor        → AlignmentAnchor component
  ├── whiteboardUtils        → WhiteboardUtils component (from Handwriting scripts)
  ├── startButton            → JournalStartButton component
  ├── arPlaneManager         → ARPlaneManager component
  ├── journalChairTable      → JournalChairTable transform (parent)
  ├── journalTable           → JournalTable transform (child of above)
  ├── chair                  → Chair transform (child of above)
  ├── seatPoint              → SeatPoint transform (empty GO)
  └── xrOrigin               → XR Origin root transform

PassthroughManager
  ├── mainCamera             → Main Camera (auto-found if null)
  └── passthroughUILayer     → 31

ARTableDetector
  └── planeManager           → ARPlaneManager (auto-found if null)

CalibrationGuide
  ├── tableDetector          → ARTableDetector component
  ├── passthroughManager     → PassthroughManager component
  └── palmIndicatorPrefab    → Any sphere/marker prefab

AlignmentAnchor
  ├── anchorManager          → ARAnchorManager (auto-found if null)
  └── targetToAlign          → JournalChairTable transform
```
