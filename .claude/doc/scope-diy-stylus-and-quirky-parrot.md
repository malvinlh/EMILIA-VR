# Scope: DIY Stylus & Table Calibration — Analysis

> **Plan-mode note.** I cannot edit `.claude/doc/calibration-analysis.md` while plan
> mode is active. The full draft for that file lives below — approve the plan and
> the next action is a verbatim `Write` to that path.

---

## Context

Two interactive calibrations gate every journaling session in EMILIA-VR:

1. **DIY stylus calibration** — solve the rigid wrist-to-pen-tip offset for a
   passive plastic pen ([Assets/Scripts/Stylus/StylusCalibrationController.cs](Assets/Scripts/Stylus/StylusCalibrationController.cs),
   [Assets/Scripts/Stylus/StylusWristTracker.cs](Assets/Scripts/Stylus/StylusWristTracker.cs)).
2. **Table calibration** — recover the writing surface’s Y, yaw, centre and
   size by tapping the four corners with the just-calibrated pen
   ([Assets/Scripts/MixedReality/TableTapCalibrator.cs](Assets/Scripts/MixedReality/TableTapCalibrator.cs)).

The user is asking why each design was chosen and what *free* improvements
could push quality further. The constraints that make this hard:

- **Quest 3, no MRUK, no ArUco markers, no IMU on the pen.** The pen is dumb
  plastic, the system never sees it directly.
- **No camera access.** OpenCV is in the project but only for example scenes;
  the runtime cannot get camera frames.
- The legacy palm-flat + ARPlaneManager flow was already tried and removed —
  see the archived [Assets/Scripts/_Archive/MixedReality/CalibrationGuide.cs](Assets/Scripts/_Archive/MixedReality/CalibrationGuide.cs) header for the post-mortem.

Everything below assumes those constraints stay fixed.

---

# PART A — Why the current designs are good

## 1. Stylus: sphere-on-the-opposite-fingertip is the right ergonomic primitive

The system needs one thing it cannot directly observe: the world position of
the pen tip relative to a tracked joint. To learn that offset it needs paired
samples of `(wristPose_i, knownTipWorldPos_i)`. Every design choice in
[StylusCalibrationController.cs:140-273](Assets/Scripts/Stylus/StylusCalibrationController.cs#L140-L273) is in service of producing those pairs cheaply and accurately:

### 1.1 Why the *target* is a fingertip
- **Co-located feel and sight.** The user can *see* the green sphere riding on
  their index tip and *feel* the pen press against the fingernail. That double
  channel collapses the human alignment error to ~1–2 mm — better than any
  airborne crosshair.
- **No external props.** No printed marker, no measured fixture, no extra
  hardware. A user in a hotel room calibrates with the same gear they’d use at
  home.
- **Hand tracking is already paid for.** `XRHandSubsystem` is reading the
  index tip every frame regardless. Marginal cost is zero.
- **Joint stability.** Among hand joints, the *tip* of an extended,
  unloaded index finger is one of the most stable Quest 3 reports — far
  steadier than the knuckles or palm centre, because it’s the most
  silhouette-defining landmark for the cameras.

### 1.2 Why an *opposite-hand thumb+middle pinch* is the capture trigger
- **Index stays extended.** [Lines 36 and 245-249](Assets/Scripts/Stylus/StylusCalibrationController.cs#L36-L36) call this
  out explicitly: only the thumb and middle move during the pinch, so the
  target position is undisturbed at the *exact* moment of capture. A pinch on
  the same finger that holds the target would shift the target by 5–15 mm at
  capture instant.
- **Rising-edge detection** ([line 251](Assets/Scripts/Stylus/StylusCalibrationController.cs#L251)) means one pinch = one sample. No accidental
  doubles, no need for a debounce timer beyond the cooldown.
- **No XR Input Action plumbing.** Distance between two joints is enough; the
  controller doesn’t need a custom input mapping or controller button (the
  user’s hands are full of pen).

### 1.3 Why the *stillness gate* + *cooldown* matter
- A pinch fired mid-motion produces a sample where wristPose was reported at
  one instant and the user’s “contact” was actually 3 cm away. The
  [`linearVelocityThreshold = 0.05 m/s`](Assets/Scripts/Stylus/StylusCalibrationController.cs#L57) gate filters those out before they pollute the
  least-squares solve.
- Cooldown prevents one slow pinch from registering as two captures across
  consecutive frames.

### 1.4 Why the *math* is the right closed form
The solver in [StylusWristTracker.FinalizeOffset:119-148](Assets/Scripts/Stylus/StylusWristTracker.cs#L119-L148) is the textbook minimiser of

```
J(p) = Σᵢ ‖ wristPosᵢ + wristRotᵢ · p − targetᵢ ‖²
```

Setting ∂J/∂p = 0 yields the *exact* closed-form average

```
p* = (1/N) Σᵢ wristRotᵢ⁻¹ (targetᵢ − wristPosᵢ)
```

Properties this gives you for free:
- **Linear-time.** No iterative solver, no Jacobian, no convergence concerns.
- **Wrist rotation is already 6-DoF**, so a single Vector3 (3 unknowns) fully
  determines the rigid pen-tip relationship. Adding rotational unknowns would
  be over-parameterised — the pen *is* rigidly attached to the wrist frame in
  practice.
- **Diversity of wrist angles cancels rotation-dependent noise.** If a user
  always holds the wrist the same way at capture, all `wristRotᵢ⁻¹` are
  ~identical and the average degenerates into a single-sample solve. Hence the
  on-screen prompt: *“Vary wrist angle.”*
- **RMS residual is published** ([lines 140 and 309](Assets/Scripts/Stylus/StylusWristTracker.cs#L140)). A high RMS is the only honest signal that
  the solve is bad — the controller uses it as a hard gate
  ([residualWarnThreshold = 8 mm](Assets/Scripts/Stylus/StylusCalibrationController.cs#L61)) and asks for a redo.

### 1.5 Why fresh-each-session beats persistence
The store *exists* ([Assets/Scripts/Stylus/StylusCalibrationStore.cs](Assets/Scripts/Stylus/StylusCalibrationStore.cs))
but is intentionally **not** auto-loaded on launch (file header lines
6-15). The grip on a DIY plastic pen drifts between days — even between
sit-downs — so any cached offset is a worse prior than re-calibrating in 10s.
The persisted file is purely diagnostic.

### Verdict for stylus
The sphere-on-fingertip + opposite-hand pinch is the **minimum-friction,
zero-cost, physically grounded** way to teach the system a value it cannot
observe. The math behind it is exact rather than heuristic. The known
weaknesses (5 samples, linear-only velocity gate, no joint redundancy) are
addressable with the free upgrades in Part B.

---

## 2. Table: 4-tap rectangle is the right substitute for plane detection

The previous flow was “place both palms flat → wait for ARPlaneManager
to confirm”. The header comment of [TableTapCalibrator.cs:7-33](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L7-L33)
documents why it was replaced; my analysis of *why those reasons are
correct*:

### 2.1 Why we abandoned the old approach
- **ARPlaneManager on Quest 3 without MRUK** depends on point-cloud densify +
  plane fit; it takes 3–10 s indoors and silently fails on glossy or
  cluttered surfaces. MRUK fixes this but cannot be installed in this build.
- **Palm-flat Y is biased high by 12–30 mm** because the palm centre joint
  sits above the metacarpal plane, and that bias is hand-shape-dependent (it
  drifts per user). The team had a `palmHeightBias` fudge factor that nobody
  could tune well across users.
- **Two flat palms only sample two points** — there’s no yaw observability,
  and the rectangle that downstream code wants has to be guessed from head
  facing.

### 2.2 Why “tap the corner with the calibrated pen” fixes all of it
- **The pen tip is *at* the surface at tap time.** No anatomical offset, no
  bias factor. `tapY = surfaceY` by construction.
- **Four taps over-determine the rectangle.** With 4 corner positions you can
  solve simultaneously for centre, yaw, width, depth, *and* surface Y, and
  still have one redundant constraint that flags unevenness. With the 2-palm
  approach the system was guessing 5 unknowns from 4 numbers (two palm
  positions = 4 horizontal coords, surface Y from one of them).
- **Reuses the calibration we just did.** No new tracking modality. The
  weakness compounds — if the stylus calibration is bad, table is bad — but
  the user can also instantly *see* the bad pen-tip (green sphere) sitting
  off the table surface and re-calibrate. That visible failure mode is a
  feature.
- **Y-consensus gate** ([tapYConsensus = 25 mm](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L53)) auto-rejects taps on a stray water bottle. Combined with the
  uneven-warn span ([15 mm](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L69)), the user is told *which* tap to redo
  rather than the system silently averaging a bad value.
- **Pinch-confirmation** ([requirePinchToCapture, line 56](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L56)) closes the loop the same way the
  stylus calibration does: dwell tells the system *the user is committed*, the
  pinch tells the system *capture now*. This eliminated the “air-dwell ghost
  tap” class of error.
- **User defines the writing region.** Downstream code aligns the VR
  whiteboard to those four corners — so the writeable area matches what the
  user actually has free, not what AR plane detection happened to find.

### 2.3 Why the *math* is the right closed form
[BuildDetectedTable:524-579](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L524-L579) does the obvious things:
- `surfaceY = mean(taps.y)` — robust to one bad tap because of the upstream
  consensus gate.
- `forward = midFar − midNear` — yaw from edge midpoints averages out
  per-corner error along the perpendicular axis.
- `width = 0.5 (|t0−t1| + |t3−t2|)` — averaging the two near/far edges
  cancels the non-orthogonality bias.
- Clamps to `[minSize, maxSize]` so a clearly bogus rectangle can’t propagate.

The downstream consumer ([JournalSessionManager.cs:391, 605, 618](Assets/Scripts/MixedReality/JournalSessionManager.cs#L391)) reads `avgEyeY` and `avgTapSurfaceY`
to compute `realEyeAboveTable` for VR comfort scaling. The structural choice
to keep this `DetectedTable` *shape-compatible* with the legacy struct (line
105-110 of TableTapCalibrator.cs) means the swap was zero-risk to downstream
code.

### Verdict for table
4-tap rectangle is **strictly better information** than palm-flat or
ARPlaneManager could provide, on the same hardware, in less time, with the
same hands. It also gracefully degrades: tap on a book = consensus rejection,
uneven span = user warning rather than silent failure.

---

# PART B — Free improvements that would raise quality

All items below need **only** XR Hands data, the existing math libraries,
and the components already in the repo. No new SDKs, no MRUK, no camera
access, no hardware.

## B.1 Stylus calibration — improvements

### B.1.1 Rolling RMS displayed during sampling (HIGH value, ~30 LOC)
Right now the user only learns whether their solve is good *after* the 5th
sample. Compute the running RMS after sample 2 and surface it on the
instruction text:

> *“3/5 captured · current RMS 4.1 mm — try a tilted wrist next.”*

Implementation: in `CaptureSample`, if `samples.Count ≥ 2`, call a new
`PreviewResidual()` on the wrist tracker that runs the same closed-form
solve on the current accumulator without committing it. The user sees
quality climbing and can self-correct *before* hitting the redo gate.

### B.1.2 Pose-diversity gate (HIGH value, ~50 LOC)
A 5-sample solve where all wrist rotations are clustered (user kept the same
grip throughout) is mathematically a 1-sample solve in disguise. Reject the
solve when wrist-rotation diversity is below threshold.

Cheap diversity metric: compute the 3×3 covariance of the sample wrist
*forward* axes and require its smallest eigenvalue > some threshold (e.g.
`0.05`). Or simpler: require pairwise angular distances between the N
captured wrist orientations to span ≥ 60° in *each* of pitch, yaw, and roll.

If diversity is low, prompt the user to rotate the pen in the missing axis
(“tilt your wrist down 30° and re-pinch”). This is the single biggest
quality lever because it attacks the fundamental observability problem.

### B.1.3 IRLS / one-pass outlier rejection (MEDIUM value, ~40 LOC)
[StylusWristTracker.FinalizeOffset](Assets/Scripts/Stylus/StylusWristTracker.cs#L119-L148) currently averages all
samples equally. After the first solve, compute per-sample residuals;
discard any with residual > 2σ above the mean and re-solve once. Robust to
one fat-finger sample without needing more captures from the user.

### B.1.4 Use BOTH wrist and middle-MCP joints (MEDIUM/HIGH value, ~80 LOC)
Solve TWO independent rigid offsets — one from the wrist joint, one from the
middle-finger MCP (knuckle) — using the same accumulated samples, and at
runtime average the two predicted tip positions. The middle-finger MCP is
much closer to the actual gripping fingers than the wrist, so its
rotation-dependent moment arm is shorter, which means rotational error in
the joint pose translates to less tip error.

Variance of an average of two ~independent estimators is ~½ the variance of
either alone. This is essentially free (no extra captures, both joints are
already in `XRHandJoints`). Best paired with B.1.2 because the two solvers
benefit from the same rotational diversity.

### B.1.5 Replace the linear-speed gate with N-frame stillness (LOW/MEDIUM value, ~20 LOC)
The current `linearVelocityThreshold = 0.05 m/s` is an *instantaneous* check
that can pass during the brief zero-velocity moment between two motion
phases. Borrow the [TableTapCalibrator dwell pattern](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L307-L332): require ≥ 150 ms of continuous stillness
before a pinch is allowed to capture. Adds ~150 ms latency, eliminates a
whole class of bad samples.

### B.1.6 Filter the wrist pose before solving (LOW value, ~30 LOC)
[StylusTipProvider](Assets/Scripts/Stylus/StylusTipProvider.cs#L92-L137)
already runs OneEuroFilter at runtime. The *calibration* path uses raw
unfiltered wrist samples. Pass each sample through a short OneEuro pre-roll
(or take the median of the last 5 frames at the moment of capture) before
storing. Reduces per-sample noise floor by ~30%.

### B.1.7 Reload last calibration as an *editable prior*, not a replacement (LOW value, ~30 LOC)
Currently the calibration is intentionally redone every session. Compromise:
on launch, *show* the previous offset as a ghost pen tip and ask “is your
pen still gripped the same? Press to confirm, or pinch to re-calibrate.”
For users whose grip is stable across days this drops calibration to one
gesture. The store already exists — only the boot path needs wiring.

### B.1.8 Press the pen against a *fingernail edge*, not the volar pad (LOW value, ~10 LOC of UX text)
The volar pad of the index tip deforms 2–4 mm under pen pressure, which is
silent slop in the calibration. The cuticle/nail junction (the lunula) is
much stiffer and a more repeatable anatomical landmark. Just an instruction
change, but worth saying explicitly: *“Touch the pen tip to the edge of
your fingernail, not the soft pad.”*

---

## B.2 Table calibration — improvements

### B.2.1 Allow ≥ 4 taps with SVD plane fit (HIGH value, ~50 LOC)
4 taps exactly determine surface Y (mean) but don’t exploit redundancy. With
5 or more taps, run a full SVD plane fit
(`plane = principal-components(taps, take smallest singular vector as
normal)`). Three benefits:
- Sub-mm Y noise floor instead of ~quarter-mm-per-tap.
- Detects table *warp* (best-fit plane residual > tolerance).
- Same `tapYConsensus` infrastructure — just fits N points instead of 4.

UX change: after the 4th tap, allow a 5th/6th optional tap *anywhere on the
surface* before pressing Confirm. Backward compatible — if the user just
hits Confirm at 4, current behaviour is preserved.

### B.2.2 Procrustes orthogonalisation of the rectangle (MEDIUM value, ~70 LOC)
[BuildDetectedTable](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L524-L579) currently averages opposite edges as
width/depth, which silently absorbs non-orthogonality (a non-rectangular
trapezoid still produces *some* width/depth). Replace with a Procrustes fit:
project the 4 measured corners onto the closest *true rectangle* (axis = yaw
direction). Two outputs become available:
- True axis-aligned width/depth.
- Per-corner residual — a corner residual > 2 cm flags either user
  mis-ordered corners (e.g. tapped near-left, near-right, near-right again)
  or a non-rectangular table (round table, trapezoid).

### B.2.3 Pen-lift gate between corners (LOW/MEDIUM value, ~30 LOC)
Today the only thing preventing accidental sliding-finger captures is the
0.55 s cooldown. Add an explicit “pen must lift ≥ 1 cm above current
surface-Y estimate” between consecutive captures. Removes the failure mode
where a user drags the pen from corner 1 to corner 2 and accidentally
captures along the way.

### B.2.4 Cross-check stylus quality against tap Y span (MEDIUM value, ~40 LOC)
After 4 taps, you have a redundant check: if `tapYSpan > 15 mm` AND the
wrist-Y at each tap is *consistent*, the user tapped on objects (table is
fine). If both wrist-Y *and* tap-Y are inconsistent in tandem, the **stylus
calibration** is likely wrong — different wrist rotations produced different
projected tip-Y errors. Surface this explicitly:

> *“Tap heights are inconsistent. Likely cause: one tap landed on an object
>  (Redo). Less likely: stylus calibration is off (recalibrate stylus).”*

This is the kind of diagnostic that turns a frustrating mystery into a
self-service fix.

### B.2.5 Pre-tap pen-up indicator showing predicted surface Y (LOW value, ~50 LOC)
After tap 1, the system *knows* the surface Y. As the user moves the pen
toward corner 2, render a faint horizontal line at that Y plane visible in
the user’s peripheral vision (a translucent ring around the pen tip,
brightening as `|tipY − surfaceY|` shrinks). Helps the user place their
next tap onto the same surface without overshooting.

### B.2.6 Independent forearm-Y sanity prior (LOW value, ~20 LOC)
At a comfortable seated posture, the user’s wrist-Y typically sits ~5–10 cm
*above* the table surface during a tap. If the calibrator infers
`surfaceY > wristY` at any tap (i.e. tap is reported above the wrist that
made it), that tap is almost certainly garbage. Hard reject it with a
specific message. Different from `tapYConsensus` because it doesn’t need the
N≥1 prior.

---

## B.3 Cross-cutting (cheapest, often biggest impact)

### B.3.1 Calibration-quality breadcrumb in JournalSessionManager (HIGH value, ~10 LOC)
[StylusCalibrationStore.Save](Assets/Scripts/Stylus/StylusCalibrationStore.cs#L37-L62) writes RMS to disk
but JournalSessionManager doesn’t read it back. After session start, log
both calibrations’ residuals together so when the user reports drift you
can correlate it (was the pen RMS 7.9 mm? was the table span 12 mm? both?).
No UX change, just diagnostic plumbing.

### B.3.2 Consistent `pinchThreshold` across the two calibrators (LOW value, ~5 LOC)
Both calibrators set `pinchThreshold = 0.025 m`. Move it to a shared static
constant on `WhiteboardPen` (or a new `PinchConstants` class) so a single
edit can re-tune both. Trivial, prevents the “I changed it in one place
only” drift.

### B.3.3 Per-user pinch baseline (MEDIUM value, ~40 LOC)
Hand sizes diverge — a 25 mm thumb-to-middle gap is loose for an adult and
already-pinched for a child. Add a one-shot 1 s prompt at the *start* of
the very first calibration: *“Close your thumb and middle finger.”* Record
the closed gap, set `pinchThreshold = closedGap + 8 mm`. Stored alongside
the stylus offset for re-use across sessions.

---

# PART B-2 — How to *reduce calibration time* without raising fail rate

The user’s concern: "5 samples × pinch coordination is slow, and if the
wrist isn’t varied the solve fails." Both halves of that complaint trace
to the same root cause — **the current loop counts samples, but quality
depends on rotational diversity, not sample count.** A clustered 5-sample
run is mathematically a 1-sample solve.

Three strategies, ordered from "minimal change" to "rethink the loop":

## B-2.A Conservative: keep N=5, but *prescribe* the rotations (~30 LOC)
Today the prompt says "vary wrist angle" — many users don’t.
Replace with explicit per-sample prompts that walk the user through a
guaranteed-diverse capture set:

```
1/3  Hold pen flat on sphere, wrist neutral.            → pinch
2/3  Tilt wrist DOWN ~30°, hold pen on sphere.          → pinch
3/3  Tilt wrist SIDEWAYS ~30°, hold pen on sphere.      → pinch
```

- **Drop `samplesRequired` from 5 → 3.** With prescribed rotations, 3
  diverse samples beat 5 random clustered ones in the residual sense.
- **Total time drops from ~12 s → ~6 s.**
- **Fail rate drops** because the solve sees real rotational variety.
- **No new math.** Just instruction text + counter changes in
  [StylusCalibrationController.UpdateInstructionForProgress:344-351](Assets/Scripts/Stylus/StylusCalibrationController.cs#L344-L351).

This is the one-night fix.

## B-2.B Better: continuous-capture mode (~80 LOC, ⭐ recommended)
Drop the pinch-per-sample loop entirely. The whole protocol becomes:

> *"Touch the sphere with your pen tip and slowly rotate your wrist."*

Behaviour:
- The system samples wrist+target every ~150 ms while the pen tip is near
  the fingertip target (proximity check < 2 cm) and **wrist angular
  velocity is non-zero but moderate** (i.e. user is rotating, not still).
- After each new sample, run the closed-form solve. Show:
  > *"Quality: 4.1 mm · diversity 65°/180° · 7 samples"*
- Auto-confirm when *both* RMS < 5 mm AND rotational coverage > 90°. User
  can also pinch to early-commit, or just stop rotating to bail out.
- Failure mode is recoverable in real time: if RMS climbs, the user is
  already moving and just needs to keep rotating.

Implementation notes:
- The pinch-rising-edge logic in [StylusCalibrationController.UpdateCapturePhase:202-273](Assets/Scripts/Stylus/StylusCalibrationController.cs#L202-L273) becomes proximity-based instead of pinch-based.
- The stillness gate inverts: capture when the user IS rotating slowly
  (between 5°/s and 90°/s), so you’re actually getting diverse rotations
  by construction.
- The `pinchThreshold` becomes optional — pinch becomes "commit early".
- Borrows the pose-diversity gate from B.1.2 as the auto-confirm trigger.

Why this is better than B-2.A:
- **Time: ~3–5 s typical** vs ~6 s prescribed.
- **No pinch coordination.** That coordination (touch with one hand,
  pinch with the other, hold both still) is the dominant fail-mode for
  first-time users.
- **Diversity is guaranteed by construction** — you literally only sample
  during rotation.
- **Quality is observable in real time.** No "5/5 captured then surprise,
  redo" experience.

The only downside: it needs careful tuning of the proximity gate (how
close does the pen tip need to be to the fingertip to count?). A
conservative 1.5 cm proximity, with the green sphere brightening as the
user gets closer, makes the protocol self-explanatory.

## B-2.C Ultra-fast: single-pose with multi-joint redundancy (~60 LOC)
For users whose grip is genuinely stable across days (the persisted
calibration scenario, B.1.7):

> *"Touch the sphere with your pen tip and hold for one second."*

- Capture ~30 wrist+joint samples in 1 s of dwell.
- Solve **three** independent rigid offsets in parallel: wrist,
  middle-MCP, index-MCP. Average the three predicted tip positions at
  runtime.
- Compare the three solved offsets — if they predict the same tip
  position to within 4 mm, calibration is good. If they diverge, fall
  back to B-2.B continuous-capture mode automatically.
- Total time: **~1 second** in the success path.

This is the only single-pose protocol that can be quality-checked without
rotational diversity, because the three joints provide independent
geometric anchors.

## Recommendation: ship B-2.A this week, plan B-2.B as the v2

B-2.A is a one-line change to `samplesRequired` plus rewritten
instruction text. Ship it and you immediately get faster + more reliable
calibration. B-2.B is the proper redesign and worth doing once B-2.A
proves the pose-prescription hypothesis. B-2.C is only worth building if
you also implement B.1.7 (persisted calibration as editable prior).

---

# PART B-3 — Honest re-evaluation: are B-2.A/B/C the *best* options?

**Short answer:** B-2.B is genuinely strong for stylus, but I missed two
important alternatives. And for the **table**, every option in PART B was
about *accuracy*, not *speed* — I never gave you a faster table calibration.
Let me fix that.

## B-3.1 Stylus: alternatives I didn’t propose

### B-3.A "In-grip" model — solve a *scalar*, not a vector (~120 LOC)
The current model treats the wrist-to-tip relationship as an unknown
3-vector that needs ≥ 2 differently-oriented samples to solve. But the
pen is held between thumb and index. A different model:

```
tip_world = midpoint(thumbTip, indexTip) + penAxis · L
```

where `penAxis` is approximately `normalize(grip_midpoint − wristJoint)`
and `L` is a single scalar (how far the tip protrudes past the grip).

- **Unknowns: 1 scalar** instead of 3.
- **Calibration: 1 capture.** "Touch the sphere, hold for 1 s."
  Solve `L = distance(grip_midpoint, target_sphere)`.
- **Time: ~1.5 s.**
- **Accuracy: depends on whether `(grip − wrist)` actually points along
  the pen axis** — true for most thin-pen grips, false for unusual grips
  (e.g. someone gripping the pen at the very back, or with a tripod grip
  where the pen tilts differently from grip-to-wrist line).

Not a strict win over B-2.B — it’s a different *bet* (grip-anchored vs
wrist-anchored). The right move is to run BOTH at runtime and trust the
one with lower per-frame variance, or fuse them as in B.1.4. Worth a
proof-of-concept before committing.

### B-3.B Auto-improving from natural use (~150 LOC, no separate calibration UI)
The bigger reframe: **delete the calibration phase entirely**. The
project already has two pieces of ground-truth ink:

1. [`StylusTipProvider.HasWritingPlane`](Assets/Scripts/Stylus/StylusTipProvider.cs#L43-L43): a writing plane snap that pulls the tip
   onto the table when within 1 cm.
2. [Persisted calibration](Assets/Scripts/Stylus/StylusCalibrationStore.cs):
   prior offset from last session.

Combine them:
- **Boot:** load the persisted offset as the initial guess (no UI).
- **First use:** when user starts writing, the snap is already pulling
  the tip onto the table for any wrist offset close to last session’s.
- **Background refinement:** every frame the snap fires, record
  `(wristPose, snappedTipWorld)` as a free training pair. Run an
  incremental Kalman-style update on the wrist-local offset so it
  converges over the first ~30 seconds of writing, *invisibly to the
  user*. The snap distance shrinks as confidence rises.

- **Interactive calibration time: 0 s.**
- **Accuracy: matches B-2.B within ~1 minute of writing**, because the
  table is now the calibration target instead of a fingertip — and there
  are hundreds of "samples" available during normal writing.
- **Failure mode: previous-session offset wildly wrong (DIY pen swapped
  out).** Detect this by RMS of snap residuals over the first 5 strokes;
  if > 1 cm, fall back to interactive calibration (B-2.B).

This is the strongest answer I can give to *"reduce time without losing
accuracy"* — because the calibration time goes to **zero**.

## B-3.2 Table: alternatives that actually save *time*

Everything in PART B for the table was about *quality* (extra taps, SVD,
Procrustes). For the time goal, the relevant axes are:

### B-3.C 2-tap diagonal corners (~40 LOC)
Tap NEAR-LEFT, then FAR-RIGHT.
- Yaw: derived from the user’s head facing direction at confirmation
  (assumed to face the table — already true 95% of the time when seated).
- Width / depth: derived from the diagonal length, assuming a 3:2 aspect
  ratio (or a configurable preset).
- Surface Y: mean of 2 tap Ys.
- **Time: ~5 s** (vs ~10–12 s for 4 taps).
- **Accuracy loss:** size is a ~10–20% guess; yaw assumes user faces the
  table (already true); Y is identical accuracy.

For a journaling app where the writing region is configurable in VR
post-calibration anyway, this is probably good enough.

### B-3.D 1 tap + 1 diagonal drag (~80 LOC, ⭐ recommended for table)
Tap NEAR-LEFT for surface Y. Then drag pen tip along the surface from
NEAR-LEFT to FAR-RIGHT in one continuous slide. Sample the drag path:
- **Y precision:** locked from the initial tap (single tap is sub-mm
  vs. "pick from a noisy drag").
- **Yaw:** from drag direction (excellent — the drag covers the full
  diagonal, ~30 cm of motion, so direction error is < 1°).
- **Width / depth:** from drag-path bounding box if the user makes a
  subtle U-shape, or assumed aspect ratio if they go straight.
- **Time: ~3–4 s.**
- **Accuracy: equal-or-better Y and yaw than current 4-tap.** Size is
  somewhat lossier — same caveat as B-3.C.

This trades the *quantization* of 4-tap for the *continuity* of a drag,
which is what hand-tracking is good at (catching velocity profiles, not
discrete poses).

### B-3.E 3 taps + right-angle prior (~30 LOC)
Tap NEAR-LEFT, NEAR-RIGHT, FAR-RIGHT. Compute the 4th corner by
perpendicular projection. Verify the measured `(p1 − p0) ⊥ (p2 − p1)` to
within 10°; otherwise fall back to "tap the 4th corner too."
- **Time: ~8 s** (25% faster than 4-tap).
- **Accuracy: identical to 4-tap when corners are right-angled (always
  for rectangular tables).**
- **Failure mode auto-detected.**

### B-3.F Skip table calibration, derive from first written strokes (~200 LOC)
Symmetric with B-3.B for the stylus. The first 2–3 strokes the user
writes provide:
- Surface Y: from minimum tip-Y in the strokes.
- Yaw: from dominant stroke direction (left-to-right writing).
- Centre: from stroke centroid.
- Size: assume a fixed comfortable region (e.g. 40 × 25 cm centred on
  centroid), expandable if user writes outside it.

- **Interactive calibration time: 0 s.**
- **Accuracy: Y is excellent. Size is a guess. Yaw depends on how the
  user actually writes.**

Only useful if size/yaw can be loose. For journaling where the VR
whiteboard is rendered on top of the inferred surface, the user’s
perception of "where the writing area is" comes from the rendered
whiteboard, so a slightly arbitrary size is OK.

## B-3.3 Re-evaluating my prior B-2.A/B/C suggestions

| Option | Time | Accuracy vs current | Verdict |
|---|---|---|---|
| **B-2.A** prescribed 3-sample | ~6 s | Equal or higher (diversity guaranteed) | Good — ship as quick win |
| **B-2.B** continuous capture | ~3–5 s | Higher (auto-diversity + live RMS) | Strong — best "explicit calibration" option |
| **B-2.C** single-pose multi-joint | ~1 s | Variable; needs 3-joint cross-check | OK fallback, not standalone |
| **B-3.A** in-grip scalar | ~1.5 s | Different model — needs A/B test | Unproven, worth POC |
| **B-3.B** auto-improve from snap | 0 s | Matches B-2.B in ~30 s of writing | **Strongest answer for "no time"** |

For **table**, my PART B suggestions were *more accurate but not faster*.
The PART B-3 options above are the time-reducing ones.

| Option | Time | Accuracy vs current | Verdict |
|---|---|---|---|
| Current 4-tap | ~10–12 s | Baseline | — |
| **B-3.C** 2-tap diagonal | ~5 s | Y equal; size ~15% lossier | Good if size is configurable post-cal |
| **B-3.D** 1-tap + diagonal drag | ~3–4 s | Y equal; yaw better; size lossier | **Recommended** |
| **B-3.E** 3-tap + right-angle | ~8 s | Equal | Safe modest win |
| **B-3.F** derive from first strokes | 0 s | Y excellent; size assumed | Strongest answer for "no time" |

## B-3.4 Updated recommendation

**If you want one change per system that maximises time savings while
keeping accuracy at-or-above today:**

- **Stylus → B-2.B (continuous-capture during rotation).** Time ~3–5 s,
  accuracy strictly higher than today thanks to enforced diversity. Pairs
  with a B.1.7-style persistence prior to drop subsequent sessions to
  near-zero.
- **Table → B-3.D (1 tap + diagonal drag).** Time ~3–4 s, Y accuracy
  equal, yaw accuracy better, size somewhat lossier (acceptable because
  the user can resize the rendered whiteboard later).

**If you want to swing for the fence (and accept ~1 week of work):**

- **Stylus → B-3.B + B-2.B fallback.** Calibration is invisible on
  return-user sessions; new pen / new user falls back to ~3–5 s
  continuous capture.
- **Table → B-3.F + B-3.D fallback.** Calibration is invisible if the
  user just starts writing; explicit drag is offered if the auto-derived
  region looks wrong.

**The original B-2.A "3 samples with prescribed rotations" still wins as
the one-night ship-it-tomorrow change** if a full redesign is too much.

---

# PART C — Recommended priority order if you implement only some of this

If you have time for **one** improvement: **B.1.2 (pose-diversity gate)**.
It addresses the single biggest source of latent error in the stylus solve
— users naturally hold the pen the same way and produce a degenerate
sample set without realising it.

If you have time for **three**: add **B.1.1 (rolling RMS)** and **B.2.1
(SVD plane fit with optional extra taps)**. Together they make both
calibrations *self-aware*, with users seeing live quality and the table
solve exploiting redundancy when offered.

If you have time for **five**: add **B.1.4 (wrist + middle-MCP)** and
**B.2.4 (cross-check stylus vs tap span)**. These are the
quality-amplifying ones rather than the quality-revealing ones.

Everything else is polish.

---

# Critical files to modify (for the recommended top-3)

| Improvement | File | Approximate lines touched |
|---|---|---|
| B.1.1 Rolling RMS | [StylusCalibrationController.cs:275-297](Assets/Scripts/Stylus/StylusCalibrationController.cs#L275-L297), [StylusWristTracker.cs:119-148](Assets/Scripts/Stylus/StylusWristTracker.cs#L119-L148) | +30 |
| B.1.2 Pose-diversity gate | [StylusWristTracker.cs](Assets/Scripts/Stylus/StylusWristTracker.cs) (new method), [StylusCalibrationController.Solve:299-342](Assets/Scripts/Stylus/StylusCalibrationController.cs#L299-L342) | +50 |
| B.2.1 SVD plane fit | [TableTapCalibrator.cs:524-579](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L524-L579), [TableTapCalibrator.cs:420-446](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L420-L446) (allow > 4 taps) | +50 |

# Verification (when implementation begins)

1. Build to Quest 3 device, enter passthrough mode.
2. Stylus: run `BeginCalibration`, capture 5 samples deliberately keeping
   the wrist in the same pose. With B.1.2, the solve should refuse and
   instruct rotation.
3. Stylus: read `Application.persistentDataPath/stylus_calibration.json`
   after a known-good run, expect `rmsResidualMeters < 0.005`.
4. Table: place a book in the corner, tap on the book accidentally, expect
   the consensus gate to reject it (already works) — and with B.2.4, the
   diagnostic message should mention objects, not stylus.
5. Table: with B.2.1, tap 4 corners + 2 centre taps, check that reported
   `surfaceY` differs by <1 mm from a reference pen-tip-against-table
   measurement.

---

# PART F — Final recommended implementation

This is the concrete, decided plan. Two changes — one per calibrator — to be
implemented as a single PR. Targets: ~70% reduction in interactive time,
strictly equal-or-better accuracy, all dependencies already in the repo.

## F.1 Stylus → Continuous-capture during natural rotation

**Replaces** the current 5-sample pinch-gated loop in
[StylusCalibrationController.cs:202-273](Assets/Scripts/Stylus/StylusCalibrationController.cs#L202-L273).

### User-visible protocol
1. Calibration phase begins — green sphere appears on opposite index tip
   as today.
2. Single instruction: *"Touch the pen tip to the green sphere and slowly
   rotate your wrist in different directions."*
3. Live readout below: *"Quality 6.2 mm · diversity 40°/180° · 4 samples"*.
   Numbers update in real time.
4. Sphere turns yellow when actively capturing, green when idle, blue
   when auto-confirm fires.
5. Auto-confirm when **all three** hold:
   - RMS residual < 5 mm
   - Wrist rotation diversity > 90° (max pairwise angular distance across
     samples in any axis)
   - At least 4 samples accumulated
6. Manual early-commit on opposite-hand pinch (today’s gesture, kept as
   an escape hatch).
7. On confirm: existing Next button flow.

### Capture rules per frame
A new sample is recorded when:
- Pen tip is within 2 cm of the target sphere (proximity gate replaces
  the pinch).
- Wrist angular speed is between 5°/s and 90°/s (i.e. user is rotating,
  not static and not jerking).
- Time since last capture > 100 ms.

This guarantees diversity by construction: samples can only land while
the user is actively rotating.

### Implementation notes
- `StylusCalibrationController.UpdateCapturePhase` is rewritten around
  the proximity + angular-speed gates; pinch logic kept only for early
  commit.
- `StylusWristTracker` gets two new methods: `PreviewSolve(out float rms,
  out float diversityDeg)` for the live readout (runs the existing solve
  on the current accumulator without committing), and
  `ComputeRotationalDiversity()` returning the max pairwise angular
  distance.
- `samplesRequired` is removed; the gate is quality + diversity, not count.
- Persisted offset (B.1.7): on `BeginCalibration`, if
  [`StylusCalibrationStore.Load()`](Assets/Scripts/Stylus/StylusCalibrationStore.cs#L68-L81)
  returns a record, pre-load it into `wristTracker.SetWristOffset` and
  enter "verify mode" — the green sphere becomes the previous offset’s
  predicted tip, and a single 0.5 s dwell anywhere accepts the prior. If
  the user rotates instead, the prior is discarded and continuous
  capture begins.

### Time budget
- New user / fresh pen: **3–5 s** typical from sphere appearing to
  auto-confirm.
- Returning user (verified prior): **~1 s** to dwell-confirm.

### Accuracy guarantee
RMS gate ensures published offset is at least as good as today’s
`residualWarnThreshold = 8 mm`. Diversity gate ensures the
mathematically-degenerate "all rotations clustered" case cannot pass
silently — which it can today.

### Critical files
- [Assets/Scripts/Stylus/StylusCalibrationController.cs](Assets/Scripts/Stylus/StylusCalibrationController.cs) — capture loop
  rewrite (~120 LOC delta)
- [Assets/Scripts/Stylus/StylusWristTracker.cs](Assets/Scripts/Stylus/StylusWristTracker.cs) — add `PreviewSolve` and
  `ComputeRotationalDiversity` (~50 LOC)
- [Assets/Scripts/Stylus/StylusCalibrationStore.cs](Assets/Scripts/Stylus/StylusCalibrationStore.cs) — wire `Load()` into
  the boot path (~10 LOC)
- [Assets/Scripts/MixedReality/JournalSessionManager.cs](Assets/Scripts/MixedReality/JournalSessionManager.cs) — no change to
  state machine; existing `OnCalibrationComplete` event still fires.

## F.2 Table → 1 tap + 1 diagonal drag

**Replaces** the current 4-tap loop in
[TableTapCalibrator.cs:272-446](Assets/Scripts/MixedReality/TableTapCalibrator.cs#L272-L446).

### User-visible protocol
1. Instruction: *"Tap the near-left corner of your writing area and hold
   for half a second."* — same dwell + (optional) pinch gesture as today.
2. After the tap is captured, surface Y is locked. Visual: faint
   horizontal line at that Y appears, brightening as the pen tip
   approaches it.
3. Instruction updates: *"Now slide the pen tip across the table to the
   far-right corner."*
4. The drag is recorded as a polyline of pen-tip positions sampled at
   ~50 Hz, while the tip stays within ±1 cm of surface Y. Dragging stops
   when the pen lifts > 1 cm above surface Y for 0.3 s.
5. Existing Confirm + Redo buttons appear; Confirm → existing
   `OnTableConfirmed` event with the same `DetectedTable` shape.

### Solver
- `surfaceY` = the tap Y (sub-mm accuracy from a single dwell tap).
- `forward` = principal-component direction of the drag polyline (PCA on
  XZ); near-corner = path start, far-corner = path end.
- `width` = max perpendicular extent of the polyline from its principal
  axis × 2 (if user makes a slight U-shape) — falls back to a
  configurable default aspect ratio (3:2 width:depth) if the path is
  straight.
- `depth` = polyline length along the principal axis × `aspectAdjust`
  (or from the U-shape extent).
- `centre` = polyline centroid projected to surface Y.

### Time budget
- 1 tap (~1 s dwell) + 1 drag (~2 s) = **~3–4 s total** vs. ~10–12 s today.

### Accuracy guarantee
- Y: equal (single tap dwell is no noisier than 4-tap mean).
- Yaw: better (30 cm of drag has ~1° angular error vs ~3–5° from 4
  separate corner taps).
- Size: width is OK if user makes a slight curve; otherwise a 10–15%
  guess. Acceptable because the rendered VR whiteboard is configurable
  post-calibration anyway, and the user can re-do calibration if needed.

### Critical files
- [Assets/Scripts/MixedReality/TableTapCalibrator.cs](Assets/Scripts/MixedReality/TableTapCalibrator.cs) — replace tapping
  phase with tap-then-drag; reuse existing visuals (tip indicator, preview
  line, confirm/redo buttons). ~150 LOC delta.
- [Assets/Scripts/MixedReality/JournalSessionManager.cs](Assets/Scripts/MixedReality/JournalSessionManager.cs) — no change;
  consumes the same `DetectedTable` struct.

## F.3 Cross-cutting

- **Move `pinchThreshold` to a shared `PinchConstants.Default = 0.025f`**
  used by both calibrators (B.3.2 from earlier section).
- **Add a one-line log on session start** dumping the loaded stylus
  offset RMS and (after table calibration) the table tap residual.
  Lets you correlate user-reported drift across runs (B.3.1).

## F.4 Total impact

| Phase | Today | After F.1+F.2 | Reduction |
|---|---|---|---|
| Stylus calibration (new user) | ~12 s + retries | **~3–5 s** | ~65–70% |
| Stylus calibration (returning user) | ~12 s | **~1 s** (verify) | ~92% |
| Table calibration | ~10–12 s | **~3–4 s** | ~70% |
| **Total interactive calibration time** | ~22–24 s | **~6–9 s** | ~65–70% |

Accuracy is **strictly equal or better** in every dimension that matters
(stylus RMS, table Y, table yaw). The one minor regression is table
size precision, which is acceptable as documented.

## F.5 Verification protocol

1. Build to Quest 3, enter passthrough mode.
2. **Stylus quick-fail test:** start calibration, hold the pen tip on
   the sphere with the wrist completely still. Expect: no auto-confirm
   ever (diversity gate never satisfied). Slowly rotate → confirm fires
   within 3 s.
3. **Stylus accuracy test:** after auto-confirm, tap the pen tip on a
   known reference point (e.g. a thumbtack on the desk). Read
   `wristTracker.TryGetTipPosition` and compare to ground truth — expect
   < 5 mm error.
4. **Stylus return-user test:** restart the app. Expect the previous
   offset to be loaded; calibration phase shows verify-mode sphere; a
   half-second dwell confirms.
5. **Table accuracy test:** perform F.2 protocol on a known table. Place
   a ruler along the diagonal and confirm the reported `forward`
   direction matches within 2°. Confirm `surfaceY` matches a manual
   pen-down measurement within 2 mm.
6. **Table size fallback test:** drag in a perfectly straight line.
   Expect `size.x = aspectRatio.x × dragLength`, `size.y` = drag length
   × aspectRatio.y / aspectRatio.x, both clamped to `[minSize, maxSize]`.
7. **Cross-check:** after both calibrations, write a few words. Expect
   ink to appear on-surface (no Z-floating, no Z-clipping).

---

# Final action when this plan is approved

Single `Write` call to `.claude/doc/calibration-analysis.md` with the
content of this file (sections **PART A** through **PART F** — the
plan-mode preamble at the top should not ship to the doc).
