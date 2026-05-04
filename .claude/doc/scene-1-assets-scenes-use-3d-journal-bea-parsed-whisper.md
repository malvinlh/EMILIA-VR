# Plan: Calibration Confirm Panel + Second-Session Bug Fix

## Context

The journaling scenes (`3D_Journal_Beach.unity`, `3D_Journal_Bedroom.unity`) share a single `JournalSessionManager.cs` that orchestrates the full MR journaling flow (Idle → Passthrough → StylusCalibration → TablePlacement → Preview → TransitionToVR → Journaling).

**Bug reported**: Second, third, etc. sessions get stuck in passthrough mode with no calibration UI (instruction text, buttons, green sphere) appearing.

**Feature requested**: After clicking the Journal Start Button on subsequent sessions, show a confirmation panel in Indonesian asking whether to recalibrate or skip straight to journaling.

---

## Bug Status: Partially Fixed, One Gap Remaining

### What's already fixed
`OnStartButtonPressed()` ([JournalSessionManager.cs:322](Assets/Scripts/MixedReality/JournalSessionManager.cs#L322)) checks `hasCalibratedThisSceneVisit && calibrationDataValid`. If both are true, it calls `StartSubsequentSession()` which skips passthrough/calibration entirely and jumps directly to the `Journaling` state. This correctly prevents re-entering passthrough with a broken calibration UI.

`hasCalibratedThisSceneVisit` is set in `OnTableConfirmed()` (L456) and `calibrationDataValid` is set in `SpawnWhiteboardForPreview()` (L537). `EndSessionCoroutine()` (L1036) deliberately preserves both flags so subsequent sessions fast-path correctly.

### Remaining gap — no passthrough guard
`StartSubsequentSession()` (L334) proceeds unconditionally even if `passthroughManager.IsPassthroughActive` is still true (an edge case if the first session's passthrough exit was delayed or failed silently). In that state, `LockLocomotion()` + `TeleportToSeatPoint()` run fine but the user still sees the real world (passthrough is on), recreating the original "stuck in passthrough" symptom.

**Fix**: Add a passthrough-active guard at the start of `StartSubsequentSession()` — if passthrough is somehow still on, call `ExitPassthrough` first then re-enter the fast path inside the callback.

---

## Feature: Calibration Confirmation Panel

### UX flow
1. User presses the Journal Start Button (second+ session).
2. Start button hides immediately (existing behavior).
3. A floating confirmation panel appears in front of the user (WorldSpace, positioned like the instruction text).
4. Panel text (Indonesian): **"Apakah kamu ingin kalibrasi ulang?"**
5. Two buttons:
   - **"Ya, Kalibrasi Ulang"** → dismiss panel → call `ProceedToPassthrough()` (full recalibration)
   - **"Tidak, Lanjutkan"** → dismiss panel → call `StartSubsequentSession()` (skip calibration)

First sessions (no cached calibration) are unaffected — they go directly to `ProceedToPassthrough()` as before.

---

## Implementation

### File 1 — New: `Assets/Scripts/MixedReality/CalibrationConfirmPanel.cs`

New MonoBehaviour that procedurally creates the panel at runtime (no prefab, no scene changes needed — same pattern as `JournalStartButton`'s tooltip and `StylusCalibrationController`'s target sphere).

Key responsibilities:
- `Show(Action onRecalibrate, Action onSkip)` — creates panel GameObjects, positions panel in front of the user's camera at arm's length (~0.6 m), billboard-facing camera.
- `Hide()` — destroys panel GameObjects and resets callbacks.
- Internal button interaction: each button is a `GameObject` with `BoxCollider` (non-trigger) + `XRSimpleInteractable` (controller ray) + per-frame fingertip poke check (same `bounds.Contains(tipPose.position)` pattern as `JournalStartButton`).

Panel structure (procedurally created):
```
CalibrationConfirmPanel (root, follows camera on Show())
  ├── BackgroundQuad       (dark translucent quad for readability)
  ├── QuestionText         (TextMeshPro: "Apakah kamu ingin kalibrasi ulang?")
  ├── ButtonYa             (quad + BoxCollider + XRSimpleInteractable + TMP label "Ya, Kalibrasi Ulang")
  └── ButtonTidak          (quad + BoxCollider + XRSimpleInteractable + TMP label "Tidak, Lanjutkan")
```

Interaction:
- `XRSimpleInteractable.selectEntered` → controller ray select
- `Update()` fingertip poke: `XRHandJointID.IndexTip` bounds.Contains check on each button (both hands), with a short cooldown to prevent double-fire

### File 2 — Modified: `Assets/Scripts/MixedReality/JournalSessionManager.cs`

#### Change A — `OnStartButtonPressed()` (L312)
Replace the direct `StartSubsequentSession()` call for subsequent sessions with `ShowCalibrationConfirmPanel()`:

```csharp
// Before:
if (hasCalibratedThisSceneVisit && calibrationDataValid)
    StartSubsequentSession();
else
    ProceedToPassthrough();

// After:
if (hasCalibratedThisSceneVisit && calibrationDataValid)
    ShowCalibrationConfirmPanel();   // new
else
    ProceedToPassthrough();
```

#### Change B — New method `ShowCalibrationConfirmPanel()`
```csharp
private void ShowCalibrationConfirmPanel()
{
    if (_confirmPanel == null)
        _confirmPanel = new GameObject("CalibrationConfirmPanel")
                            .AddComponent<CalibrationConfirmPanel>();

    _confirmPanel.Show(
        onRecalibrate: () => ProceedToPassthrough(),
        onSkip:        () => StartSubsequentSession()
    );
}
private CalibrationConfirmPanel _confirmPanel;
```

#### Change C — `StartSubsequentSession()` passthrough guard (L334)
```csharp
private void StartSubsequentSession()
{
    Debug.Log("[JournalSession] Subsequent same-scene session — skipping calibration.");

    // Guard: exit passthrough if still active (edge case from unclean first-session exit)
    if (passthroughManager != null && passthroughManager.IsPassthroughActive)
    {
        passthroughManager.ExitPassthrough(RunSubsequentSession);
        return;
    }
    RunSubsequentSession();
}

private void RunSubsequentSession()
{
    LockLocomotion();
    TeleportToSeatPoint();
    if (alignmentAnchor != null && calibrationDataValid)
    {
        Pose tablePose = new Pose(pendingTable.position, pendingTable.rotation);
        alignmentAnchor.CreateAnchorAtTable(tablePose);
    }
    CurrentState = SessionState.Journaling;
    OnJournalingEntered();
    SetWhiteboardUIActive(true);
}
```

#### Change D — `CancelSession()` also hides the panel
In `CancelSession()` (L1173), add `_confirmPanel?.Hide();` before the existing cleanup steps so the panel disappears if the session is cancelled while it's visible.

---

## Critical Files

| File | Role |
|------|------|
| `Assets/Scripts/MixedReality/JournalSessionManager.cs` | Main orchestrator — modify `OnStartButtonPressed`, `StartSubsequentSession` |
| `Assets/Scripts/MixedReality/CalibrationConfirmPanel.cs` | **New** — procedural panel with poke-interactable buttons |
| `Assets/Scripts/MixedReality/JournalStartButton.cs` | Reference only — interaction pattern to replicate in `CalibrationConfirmPanel` |

No scene files need to be modified (the panel is created procedurally at runtime like the `JournalStartButton` tooltip).

---

## Verification

1. Build and deploy to Meta Quest 3.
2. Enter a journal scene (Beach or Bedroom).
3. **First session**: Press start → no confirmation panel → full calibration flow runs (stylus sphere appears, table tap UI appears) → journaling starts.
4. End the session.
5. **Second session**: Press start → confirmation panel appears in Indonesian ("Apakah kamu ingin kalibrasi ulang?").
   - Choose **"Tidak, Lanjutkan"** → panel closes → journaling starts immediately (no passthrough/calibration).
   - Choose **"Ya, Kalibrasi Ulang"** → panel closes → full calibration flow runs again (stylus + table tap) → journaling starts.
6. **Third session**: Panel appears again. Repeat.
7. Verify no "stuck in passthrough" regression — VR mode is active before and after the panel.
