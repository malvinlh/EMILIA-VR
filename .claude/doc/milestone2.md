# Milestone 2 - Current MR Journaling System Snapshot

Reference snapshot of the EMILIA-VR MR journaling and companion systems as of the current workspace state on 2026-04-18 (latest pulled code), Unity 6000.3.10f1, Meta Quest 3, URP.

This document keeps milestone1 structure, but updates active runtime paths to the current implementation:
- Table calibration now uses direct 4-tap contact via TableTapCalibrator.
- Stylus calibration now uses multi-sample fingertip touch plus pinch capture.
- Post-journal keep/discard flow is hardened around cork sealing and explicit terminal detectors.

---

## 1. Environment and Package Constraints

- Unity version: 6000.3.10f1.
- Render pipeline: URP 17.3.0.
- XR/MR stack in Packages/manifest.json:
	- com.unity.xr.meta-openxr 2.4.0
	- com.unity.xr.hands 1.7.3
	- com.unity.xr.interaction.toolkit 3.3.1
	- com.unity.xr.arfoundation 6.1.2
	- com.unity.xr.openxr 1.16.1
- Not present in manifest snapshot:
	- com.meta.xr.sdk.core
	- com.meta.xr.mrutilitykit
- Practical impact:
	- No OVR/MRUK-specific APIs are used in active flow.
	- Hand tracking is XRHandSubsystem-based.
	- Any CV-fusion path that depends on direct camera CPU image access remains constrained on-device.
- Session-space to world-space conversion remains critical:
	- XRHand joints are converted through XROrigin.CameraFloorOffsetObject transform chain, not XR Origin root transform.

---

## 2. MR Session State Machine

Defined in [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs).

```csharp
public enum SessionState
{
		Idle,
		Passthrough,
		StylusCalibration,
		TablePlacement,
		Preview,
		TransitionToVR,
		Journaling,
		ReCalibrating,
		Ending
}
```

High-level runtime flow:

```
Idle
 └─ Start button (JournalStartButton)
Passthrough
 ├─ if stylus step enabled -> StylusCalibration
 └─ else                -> TablePlacement
StylusCalibration
 └─ Next button poke -> TablePlacement
TablePlacement (4-tap)
 ├─ confirmed -> Preview
 └─ timeout   -> fallback spawn path
Preview
 └─ short delay -> TransitionToVR
TransitionToVR
 └─ passthrough exit callback -> Journaling
Journaling
 ├─ RequestReCalibration() -> ReCalibrating -> TablePlacement
 └─ Done button           -> Ending
Ending
 └─ Review complete callback -> Idle
```

Primary transition triggers and contracts:

- Start:
	- [JournalStartButton.cs](../../Assets/Scripts/MixedReality/JournalStartButton.cs) -> JournalSessionManager.OnStartButtonPressed().
- Stylus phase complete:
	- [StylusCalibrationController.cs](../../Assets/Scripts/Stylus/StylusCalibrationController.cs):
		- OnCalibrationComplete (solve succeeded)
		- OnNextButtonPressed (user confirms and advances)
- Table placement complete:
	- [TableTapCalibrator.cs](../../Assets/Scripts/MixedReality/TableTapCalibrator.cs):
		- OnTableConfirmed(DetectedTable)
- VR return hook:
	- [PassthroughManager.cs](../../Assets/Scripts/MixedReality/PassthroughManager.cs):
		- OnPassthroughExited -> OnceAfterPassthroughExit() (teleport, anchor setup, locomotion lock)
- Session end:
	- [JournalDoneButton.cs](../../Assets/Scripts/MixedReality/JournalDoneButton.cs) -> EndSession().
	- [JournalReviewController.cs](../../Assets/Scripts/MixedReality/JournalReviewController.cs) completion callback drives final save/discard branch and reset.

Runtime guards and fallback behavior:

- TablePlacement timeout:
	- detectionTimeout (default 60 s) triggers fallback journaling entry.
- Mid-session recalibration:
	- RequestReCalibration() only accepted from Journaling.
	- Releases alignment anchor and re-enters passthrough -> TablePlacement.
- Locomotion:
	- Locked during writing transition and journaling setup.
	- Re-enabled during bottle phase and fully restored on session end.

---

## 3. DIY Stylus Calibration - Current Design

All active stylus scripts live in [Assets/Scripts/Stylus](../../Assets/Scripts/Stylus).

### 3.1 StylusCalibrationController.cs (capture UX)

Current capture model in [StylusCalibrationController.cs](../../Assets/Scripts/Stylus/StylusCalibrationController.cs):

- User holds stylus in stylusHand.
- Opposite-hand index tip carries the target sphere.
- Capture trigger per sample:
	- Opposite thumb-middle pinch rising edge.
	- Stylus wrist linear speed below threshold.
	- Inter-sample cooldown satisfied.
- Multi-sample loop:
	- Accumulate samples until samplesRequired.
	- Solve via StylusWristTracker.FinalizeOffset(out rms).
	- If residual too high, user is prompted to re-sample.
- On success:
	- CalibrationComplete event fires.
	- Next button appears and can be poked by either index tip.

This replaces a one-shot target-only solve with a higher-confidence, pose-diverse fit.

### 3.2 StylusWristTracker.cs (solver and hand-joint access)

Core logic in [StylusWristTracker.cs](../../Assets/Scripts/Stylus/StylusWristTracker.cs):

- Wrist joint is the anchor.
- Each sample stores (wristPos, wristRot, targetWorldPos).
- Closed-form least-squares offset solve:

```
offset = (1/N) * sum_i [ inverse(wristRot_i) * (target_i - wristPos_i) ]
```

- RMS residual is computed in world space across all samples.
- Exposes helper reads for:
	- any joint world position by hand
	- index tip position
	- thumb-middle pinch gap

### 3.3 StylusTipProvider.cs (runtime tip stream)

Current behavior in [StylusTipProvider.cs](../../Assets/Scripts/Stylus/StylusTipProvider.cs):

- DefaultExecutionOrder(-25), before WhiteboardPen (-20).
- Output API:
	- TipWorldPosition (nullable)
	- Confidence
	- IsCalibrated
- Pipeline each frame:
	1. Read wrist-tracker tip.
	2. Apply OneEuro filter per axis.
	3. Soft-snap toward writing plane within configurable snap band.
- Writing plane is injected by JournalSessionManager once table calibration is known.

### 3.4 StylusVisualProp.cs (pen visualization)

Current visual in [StylusVisualProp.cs](../../Assets/Scripts/Stylus/StylusVisualProp.cs):

- Builds shaft + tip prop at runtime.
- Aligns shaft from grip point (thumb-index midpoint) toward calibrated tip.
- Visibility gated by:
	- PropEnabled (state-controlled)
	- tip availability
	- confidence threshold

JournalSessionManager toggles PropEnabled so the virtual pen only appears in writing phases.

### 3.5 StylusCalibrationStore.cs (diagnostic persistence)

Current behavior in [StylusCalibrationStore.cs](../../Assets/Scripts/Stylus/StylusCalibrationStore.cs):

- Saves diagnostic record to Application.persistentDataPath/stylus_calibration.json.
- Captures:
	- timestamp
	- hand
	- solved offset
	- offset magnitude
	- RMS residual
	- sample count
- Not auto-loaded into runtime boot path; this is diagnostic persistence, not implicit calibration reuse.

### 3.6 Current limitations

| # | Limitation | Impact |
|---|---|---|
| 1 | skipStylusCalibration can bypass the UX step, but TableTapCalibrator still waits for calibrated stylus tip data | In this configuration, table placement can stall on "Waiting for stylus tracking" unless tip calibration already exists by other means |
| 2 | Residual gate quality depends on user pose diversity during sample capture | Repeated similar wrist poses can pass with weak rotational robustness |
| 3 | Diagnostic persistence is not auto-apply | Users still recalibrate each session by design |

---

## 4. Table / Surface Calibration - Current Design

### 4.1 TableTapCalibrator.cs (active detector)

Active table calibration now lives in [TableTapCalibrator.cs](../../Assets/Scripts/MixedReality/TableTapCalibrator.cs).

Flow:

1. BeginCalibration() enters Tapping phase.
2. User taps 4 corners in order:
	 - near-left
	 - near-right
	 - far-right
	 - far-left
3. Tap capture requires:
	 - stylus tip available and calibrated
	 - stillness dwell
	 - cooldown
	 - Y consensus against previous taps
4. Rectangle preview and unevenness warning shown.
5. User confirms or redoes via floating button poke.

After confirm, OnTableConfirmed(DetectedTable) is raised.

### 4.2 DetectedTable payload (shape-compatible downstream)

DetectedTable contains:

```csharp
public struct DetectedTable
{
		public Vector3 position;
		public Quaternion rotation;
		public Vector2 size;
		public Vector3 userHeadPosition;
		public Vector3 userForward;
		public float avgEyeY;
		public float avgTapSurfaceY;
}
```

Notable semantics:

- avgTapSurfaceY is direct contact-derived surface Y (no palm-offset semantics).
- rotation forward is derived from near-edge midpoint -> far-edge midpoint.
- size is computed from horizontal corner distances, clamped to min/max bounds.

### 4.3 JournalSessionManager consumption and teleport calibration

Consumption path in [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs):

- OnTableConfirmed stores capturedRealEyeHeight = table.avgEyeY.
- pendingTable stores detected pose and surface metrics.
- TeleportToSeatPoint computes target camera eye Y from:

```
realEyeAboveTable = clamp(capturedRealEyeHeight - pendingTable.avgTapSurfaceY)
targetEyeY = virtualTableY + realEyeAboveTable + calibrationHeightBias
```

- virtualTableY source order:
	1. explicit tableWritingSurface
	2. auto path search (WhiteboardPlaceholder/Whiteboard)
	3. fallback to journalTable pivot with warning

This is the warning source for "Calibration may be inaccurate" fallback logs when tableWritingSurface cannot be resolved precisely.

### 4.4 Passthrough and drift stabilization

- [PassthroughManager.cs](../../Assets/Scripts/MixedReality/PassthroughManager.cs):
	- fade-through-black transitions
	- ARCameraBackground toggling
	- passthrough-only culling mask during MR phase
- [AlignmentAnchor.cs](../../Assets/Scripts/MixedReality/AlignmentAnchor.cs):
	- TryAddAnchorAsync-based anchor creation
	- applies late drift correction offset to target transform
	- guards against stale async completion after release

### 4.5 Current limitations and operational risks

| # | Limitation | Impact |
|---|---|---|
| 1 | TablePlacement timeout fallback enters journaling even without confirmed table calibration | Session can proceed with lower positional fidelity |
| 2 | virtualTableY fallback to journalTable pivot when writing-surface transform not found | Height alignment warning path may appear, with reduced calibration accuracy |
| 3 | Plane is currently treated as horizontal for writing snap | Sloped surfaces are not explicitly fit with full plane normal |

---

## 5. Handwriting / Drawing Pipeline - Stable Core with Runtime Hardening

Primary scripts in [Assets/Scripts/Handwriting](../../Assets/Scripts/Handwriting).

### 5.1 Capture and contact handling (WhiteboardPen)

Current behavior in [WhiteboardPen.cs](../../Assets/Scripts/Handwriting/WhiteboardPen.cs):

- Runs only in Journaling state unless allowWithoutJournalSession is enabled.
- Right-hand writing path; left hand reserved for other interactions.
- Supports both:
	- finger ray path
	- stylus override path via StylusTipProvider
- Contact continuity hardening:
	- contact-loss frame hysteresis
	- tracking-loss grace window
	- min point distance before buffering
	- micro-stroke merge logic
- Recognition trigger hardening:
	- buffered point threshold
	- min recognition interval
	- idle delay before flush

Flush path:

- Buffered world points are PCA-aligned before submission.
- Coordinates are normalized to ML Kit writing area.
- DigitalInkBridge receives begin/add/end stroke sequence.

### 5.2 Whiteboard rendering (Whiteboard)

Current behavior in [Whiteboard.cs](../../Assets/Scripts/Handwriting/Whiteboard.cs):

- Texture size derived from world lossyScale.
- CPU-side pixel buffer with dirty-region uploads.
- Touch cursor and hover cursor are rendered non-destructively via backup/restore of affected pixels.
- Upload cadence can be rate-limited to control render cost.

### 5.3 Recognition dispatch (DigitalInkBridge + RecognitionPipeline)

- [DigitalInkBridge.cs](../../Assets/Scripts/Handwriting/DigitalInkBridge.cs):
	- Android ML Kit bridge
	- model readiness polling
	- pending stroke count and idle auto-recognize
	- pre-context accumulation for next recognition cycles
- [RecognitionPipeline.cs](../../Assets/Scripts/Handwriting/RecognitionPipeline.cs):
	- candidate queueing while processing
	- best candidate selection by score/text quality
	- optional noise-token suppression before final emit

### 5.4 Text accumulation and paging (ScribbleManager + WhiteboardPageManager)

- [ScribbleManager.cs](../../Assets/Scripts/Handwriting/ScribbleManager.cs):
	- title/content paging model with _titlePageCount
	- title overflow inserts additional title pages contiguously
	- title/content boundary locks once user advances from final title page
	- per-page undo stacks
	- inline insertion cursor and range delete integration
- [WhiteboardPageManager.cs](../../Assets/Scripts/Handwriting/WhiteboardPageManager.cs):
	- renders title label/page numbers/button states
	- SetCreatedAt() displays session timestamp on title page
	- blocks page flips while writing is in progress

### 5.5 Voice injection path (JournalMicController)

Current behavior in [JournalMicController.cs](../../Assets/Scripts/Handwriting/JournalMicController.cs):

- Mic panel visible only during Journaling.
- RecordAudio lifecycle + anti-double-click cooldown.
- Transcribe through ServiceManager.TranscribeApi.
- Injects text into ScribbleManager.AddVoiceText().

### 5.6 Save path

Current save in [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs):

- Title/content assembled from ScribbleManager.
- If content empty but title has text, title text is used as content fallback.
- Journal persisted through ServiceManager.JournalService.CreateJournal().
- Sentiment update performed asynchronously through ServiceManager.SentimentApi and JournalService.UpdateJournalSentiment().

---

## 6. Integration Points for the Rework

Primary runtime integration path:

```
XRHandSubsystem joints (session space)
	-> StylusWristTracker (world conversion + offset solver)
		-> StylusTipProvider (smoothing + writing-plane snap)
			-> TableTapCalibrator (4-tap surface solve)
				-> JournalSessionManager (teleport + state transitions)
					-> WhiteboardPen (stroke capture)
						-> DigitalInkBridge / RecognitionPipeline
							-> ScribbleManager / WhiteboardPageManager
```

Post-journal branch integration:

```
JournalSessionManager.EndSession()
	-> JournalReviewController.BeginReview()
		-> Keep path: CorkSnapZone -> WineRackProximity -> saveJournal=true
		-> Release path: CorkSnapZone -> SeaBottleDetector -> saveJournal=false
	-> EndSessionCoroutine(saveJournal)
```

Key design property:

- The MR calibration and review branch are deeply integrated into JournalSessionManager, while handwriting recognition and pagination remain mostly modular and reusable.

---

## 7. Scene Inventory

Current scene set in [Assets/Scenes](../../Assets/Scenes):

- [3D_Journal_CURRENT_IMPROVE.unity](../../Assets/Scenes/3D_Journal_CURRENT_IMPROVE.unity)
	- Main journaling scene.
	- Contains JournalSessionManager, TableTapCalibrator, PassthroughManager,
		stylus stack, Whiteboard stack, and review terminal objects.
- [3D_StartArea.unity](../../Assets/Scenes/3D_StartArea.unity)
	- Entry/start scene and flow handoff context.
- [AZKi_Workshop.unity](../../Assets/Scenes/AZKi_Workshop.unity)
	- Chat/workshop context with VRChatBridge and dialogue systems.
- [WhiteboardScene-XRI.unity](../../Assets/Scenes/WhiteboardScene-XRI.unity)
	- Focused handwriting/testing context.
- [playground.unity](../../Assets/Scenes/playground.unity)
	- Experimental/development scene context.

---

## 8. Files at a Glance

### 8.1 Session orchestration and MR transitions

- [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs)
- [PassthroughManager.cs](../../Assets/Scripts/MixedReality/PassthroughManager.cs)
- [AlignmentAnchor.cs](../../Assets/Scripts/MixedReality/AlignmentAnchor.cs)
- [JournalStartButton.cs](../../Assets/Scripts/MixedReality/JournalStartButton.cs)
- [JournalDoneButton.cs](../../Assets/Scripts/MixedReality/JournalDoneButton.cs)

### 8.2 Table and stylus calibration

- [TableTapCalibrator.cs](../../Assets/Scripts/MixedReality/TableTapCalibrator.cs)
- [StylusCalibrationController.cs](../../Assets/Scripts/Stylus/StylusCalibrationController.cs)
- [StylusWristTracker.cs](../../Assets/Scripts/Stylus/StylusWristTracker.cs)
- [StylusTipProvider.cs](../../Assets/Scripts/Stylus/StylusTipProvider.cs)
- [StylusCalibrationStore.cs](../../Assets/Scripts/Stylus/StylusCalibrationStore.cs)
- [StylusVisualProp.cs](../../Assets/Scripts/Stylus/StylusVisualProp.cs)

### 8.3 Handwriting and recognition

- [WhiteboardPen.cs](../../Assets/Scripts/Handwriting/WhiteboardPen.cs)
- [Whiteboard.cs](../../Assets/Scripts/Handwriting/Whiteboard.cs)
- [DrawCircle.cs](../../Assets/Scripts/Handwriting/DrawCircle.cs)
- [DigitalInkBridge.cs](../../Assets/Scripts/Handwriting/DigitalInkBridge.cs)
- [RecognitionPipeline.cs](../../Assets/Scripts/Handwriting/RecognitionPipeline.cs)
- [ScribbleManager.cs](../../Assets/Scripts/Handwriting/ScribbleManager.cs)
- [WhiteboardPageManager.cs](../../Assets/Scripts/Handwriting/WhiteboardPageManager.cs)
- [JournalMicController.cs](../../Assets/Scripts/Handwriting/JournalMicController.cs)

### 8.4 Post-journal keep/discard terminal flow

- [JournalReviewController.cs](../../Assets/Scripts/MixedReality/JournalReviewController.cs)
- [CorkSnapZone.cs](../../Assets/Scripts/MixedReality/CorkSnapZone.cs)
- [ItemAutoReset.cs](../../Assets/Scripts/MixedReality/ItemAutoReset.cs)
- [WineRackProximity.cs](../../Assets/Scripts/MixedReality/WineRackProximity.cs)
- [SeaBottleDetector.cs](../../Assets/Scripts/MixedReality/SeaBottleDetector.cs)

### 8.5 Companion systems (concise)

- [VRLoginHandwritingBridge.cs](../../Assets/Scripts/Login/VRLoginHandwritingBridge.cs)
- [VRChatBridge.cs](../../Assets/Scripts/Chat/VRChatBridge.cs)
- [VRDialoguePanel.cs](../../Assets/Scripts/Chat/VRDialoguePanel.cs)
- [PortalSceneTransition.cs](../../Assets/Scripts/MixedReality/PortalSceneTransition.cs)
- [PortalCalmRingVfx.cs](../../Assets/Scripts/MixedReality/PortalCalmRingVfx.cs)
- [AzkiIslandRoamingController.cs](../../Assets/Scripts/MixedReality/AzkiIslandRoamingController.cs)
- [AzkiBlendShapeExpressionController.cs](../../Assets/Scripts/MixedReality/AzkiBlendShapeExpressionController.cs)

### 8.6 Data and service layer

- [ServiceManager.cs](../../Assets/_legacy/2D%20Emilia/Scripts/Manager/ServiceManager.cs)
- [LocalJournalService.cs](../../Assets/_legacy/2D%20Emilia/Scripts/Service/LocalJournalService.cs)
- [LocalChatService.cs](../../Assets/_legacy/2D%20Emilia/Scripts/Service/LocalChatService.cs)

### 8.7 Archived legacy detectors (superseded)

- [ARTableDetector.cs](../../Assets/Scripts/_Archive/MixedReality/ARTableDetector.cs)
- [CalibrationGuide.cs](../../Assets/Scripts/_Archive/MixedReality/CalibrationGuide.cs)

---

## 9. Milestone 1 -> Milestone 2 Delta Summary

| Area | Milestone 1 snapshot | Milestone 2 current snapshot | Effect |
|---|---|---|---|
| Table calibration | AR plane + palm-flat confirmation | Direct stylus 4-tap rectangle via TableTapCalibrator | Faster and less palm-offset-dependent surface capture |
| Session states | Included RequestingPermission, PlaneDiscovery, HandConfirmation | Simplified around StylusCalibration + TablePlacement (+ explicit ReCalibrating) | Lower orchestration complexity in active path |
| Stylus calibration | Predominantly single-shot solve workflow | Multi-sample solve with RMS quality gate and diagnostics persistence | Better repeatability and explicit quality signal |
| Height alignment inputs | Palm/fingertip-derived with bias tuning emphasis | avgTapSurfaceY + avgEyeY direct consumption path | Cleaner eye-above-table derivation |
| Post-journal terminal | More basic branch behavior | Explicit cork-seal prerequisite + rack/sea detectors + reset hardening | More deterministic keep/discard outcomes |
| Title paging | Simpler model in prior snapshot | _titlePageCount + title boundary lock model | Stable title/content separation across overflow |

---

## 10. Known Risks and Validation Gaps

| # | Risk | Why it matters |
|---|---|---|
| 1 | skipStylusCalibration can bypass capture UX while table tap still expects calibrated stylus tip | Can leave table calibration waiting indefinitely in some test configurations |
| 2 | TablePlacement timeout fallback can enter writing without confirmed table pose | Calibration quality may degrade while still allowing session progress |
| 3 | writing-surface auto-discovery fallback can use less precise transform | Triggers "Calibration may be inaccurate" path and weakens height alignment |
| 4 | Fixed thresholds (pinch, stillness, residual warn, eye clamp) may vary by user/device context | Comfort and calibration success can differ across users |
| 5 | DigitalInkBridge is Android ML Kit path | Editor and non-Android contexts have reduced feature parity |

---

## 11. Sources Consulted (Primary)

- [milestone1.md](milestone1.md)
- [JournalSessionManager.cs](../../Assets/Scripts/MixedReality/JournalSessionManager.cs)
- [TableTapCalibrator.cs](../../Assets/Scripts/MixedReality/TableTapCalibrator.cs)
- [StylusCalibrationController.cs](../../Assets/Scripts/Stylus/StylusCalibrationController.cs)
- [StylusWristTracker.cs](../../Assets/Scripts/Stylus/StylusWristTracker.cs)
- [StylusTipProvider.cs](../../Assets/Scripts/Stylus/StylusTipProvider.cs)
- [JournalReviewController.cs](../../Assets/Scripts/MixedReality/JournalReviewController.cs)
- [CorkSnapZone.cs](../../Assets/Scripts/MixedReality/CorkSnapZone.cs)
- [ItemAutoReset.cs](../../Assets/Scripts/MixedReality/ItemAutoReset.cs)
- [WhiteboardPen.cs](../../Assets/Scripts/Handwriting/WhiteboardPen.cs)
- [RecognitionPipeline.cs](../../Assets/Scripts/Handwriting/RecognitionPipeline.cs)
- [ScribbleManager.cs](../../Assets/Scripts/Handwriting/ScribbleManager.cs)
- [WhiteboardPageManager.cs](../../Assets/Scripts/Handwriting/WhiteboardPageManager.cs)
- [VRChatBridge.cs](../../Assets/Scripts/Chat/VRChatBridge.cs)
- [VRLoginHandwritingBridge.cs](../../Assets/Scripts/Login/VRLoginHandwritingBridge.cs)
- [ServiceManager.cs](../../Assets/_legacy/2D%20Emilia/Scripts/Manager/ServiceManager.cs)

