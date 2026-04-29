using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Interactive table calibration — 1 tap + 1 diagonal drag protocol.
///
/// Replaces the previous 4-tap rectangle flow with a faster two-gesture sequence:
///   1. TAP: the user taps the near-left corner of the writing area (dwell + optional
///      pinch), which locks the surface Y.  A cyan tip-indicator confirms the pen is
///      on-surface; a faint horizontal ring brightens as the pen approaches surface Y.
///   2. DRAG: the user slides the pen from the tapped corner diagonally across the
///      table to the far-right corner.  The drag path is recorded as a polyline at
///      ~50 Hz.  Dragging ends when the pen lifts &gt; <see cref="dragLiftThresholdM"/>
///      above surface Y for &gt; <see cref="dragLiftDurationSec"/> seconds.
///   3. CONFIRM: existing Confirm / Redo buttons appear above the preview rectangle.
///
/// Solver (from drag data):
///   • surfaceY   = first-tap Y (single dwell tap = sub-mm accuracy).
///   • forward    = direction from first to last drag point (projected to XZ).
///   • depth      = drag length along the forward axis.
///   • width      = max perpendicular span of the polyline × 2 when the user
///                  curves &gt; 3 cm; otherwise depth × defaultTableAspect.x / y.
///   • centre     = drag centroid at surface Y, offset by half-perpendicular span.
///
/// After the solver fires, the 4 computed corners are fed into UpdatePreviewRectangle
/// so the existing rectangle visualisation works unchanged.  The DetectedTable struct
/// is identical to what the original 4-tap flow produced, so JournalSessionManager
/// needs no changes.
/// </summary>
public class TableTapCalibrator : MonoBehaviour
{
    // ================================================================
    // INSPECTOR
    // ================================================================

    [Header("References")]
    public StylusTipProvider stylusTipProvider;
    public PassthroughManager passthroughManager;

    [Header("Tap Detection (first corner)")]
    [Tooltip("Stylus tip linear speed (m/s) below which stillness counts toward the dwell timer.")]
    public float tapStillnessVelocity = 0.03f;
    [Tooltip("How long (seconds) the tip must remain stationary to register a tap.")]
    public float tapDwellSeconds = 0.22f;
    [Tooltip("Cooldown (seconds) after a tap before drag recording can begin.")]
    public float tapCooldownSeconds = 0.30f;

    [Header("Tap Confirmation")]
    [Tooltip("When enabled, a dwell-ready tap is only captured when a pinch edge is detected.")]
    public bool requirePinchToCapture = true;
    [Tooltip("Which hand can trigger pinch capture.")]
    public PinchCaptureHandMode pinchCaptureHand = PinchCaptureHandMode.OppositeStylusHand;
    [Tooltip("Thumb-to-middle distance (metres) below which pinch is considered active.")]
    public float pinchThreshold = PinchConstants.Default;

    [Header("Drag Detection")]
    [Tooltip("How far above surface Y (metres) the tip must rise to count as a 'lift'.")]
    public float dragLiftThresholdM = 0.010f;
    [Tooltip("How long (seconds) the pen must be lifted to end the drag.")]
    public float dragLiftDurationSec = 0.30f;
    [Tooltip("Interval (seconds) between drag polyline samples (~50 Hz).")]
    public float dragRecordIntervalSec = 0.020f;
    [Tooltip("Minimum drag path length (metres) before a lift can end the drag.")]
    public float minDragLengthM = 0.080f;
    [Tooltip("Minimum perpendicular deviation (metres) of the drag path before using " +
             "measured width rather than the aspect-ratio fallback.")]
    public float minWidthDeviationM = 0.030f;
    [Tooltip("Default width-to-depth aspect ratio used when the drag path is straight.")]
    public Vector2 defaultTableAspect = new Vector2(3f, 2f);

    [Header("Rectangle Constraints")]
    [Tooltip("Minimum writing rectangle size in metres (width, depth).")]
    public Vector2 minSize = new Vector2(0.30f, 0.20f);
    [Tooltip("Maximum writing rectangle size in metres (width, depth).")]
    public Vector2 maxSize = new Vector2(1.20f, 0.80f);

    [Header("Confirm / Redo Buttons")]
    [Tooltip("Height above the rectangle centre where the buttons float.")]
    public float buttonHeightAboveRect = 0.22f;
    [Tooltip("Horizontal spacing between Confirm and Redo buttons.")]
    public float buttonSpacing = 0.18f;
    [Tooltip("Proximity (m) at which an index-tip poke triggers a button.")]
    public float buttonPokeDistance = 0.030f;
    [Tooltip("Cooldown before the buttons respond to a poke after appearing.")]
    public float buttonArmDelay = 0.4f;

    [Header("Visuals")]
    [Tooltip("Radius of the pen-tip indicator ring (metres).")]
    public float tipIndicatorRadius = 0.012f;
    [Tooltip("Radius of the X-marker dropped at the confirmed tap (metres).")]
    public float tapMarkerRadius = 0.010f;

    [Header("Instruction Text")]
    public float instructionForward = 0.90f;
    public float instructionHeight = 0.18f;
    public float fontSize = 0.28f;

    [Header("Diagnostics")]
    public bool logEvents = true;

    // ================================================================
    // EVENTS
    // ================================================================

    public event Action<DetectedTable> OnTableConfirmed;

    // ================================================================
    // DATA TYPES
    // ================================================================

    /// <summary>
    /// Shape-compatible with the legacy ARTableDetector.DetectedTable so
    /// JournalSessionManager downstream code can consume it unchanged.
    /// </summary>
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

    public enum PinchCaptureHandMode
    {
        OppositeStylusHand,
        Left,
        Right,
        Either
    }

    private enum Phase { Inactive, FirstTap, Dragging, AwaitingConfirm }

    // ================================================================
    // STATE
    // ================================================================

    private Phase phase = Phase.Inactive;

    // First-tap state
    private float dwellAccum;
    private float lastTapTime = -999f;
    private bool hasLastTip;
    private Vector3 lastTipPos;
    private float lastTipTime;
    private bool wasLeftPinched;
    private bool wasRightPinched;
    private Vector3 firstTapPos;

    // Drag state
    private readonly List<Vector3> dragPolyline = new List<Vector3>(256);
    private float surfaceYLocked;
    private float dragLiftAccum;
    private float lastDragRecordTime;
    private float dragPathLength;

    // Confirm state
    private float awaitConfirmEnterTime;
    private DetectedTable computedTable;

    // Eye-Y accumulator
    private float eyeYAccum;
    private int eyeYSamples;

    // ================================================================
    // VISUAL ELEMENTS
    // ================================================================

    private int passthroughLayer = 31;

    private GameObject tipIndicator;
    private Material tipIndicatorMat;

    private GameObject tapMarker;
    private Material tapMarkerMat;

    private LineRenderer previewRectangle;
    private GameObject previewRectangleObj;

    private LineRenderer dragPathRenderer;
    private GameObject dragPathObj;

    private GameObject confirmButton;
    private Material confirmButtonMat;
    private TextMeshPro confirmLabel;
    private Vector3 confirmButtonPos;

    private GameObject redoButton;
    private Material redoButtonMat;
    private TextMeshPro redoLabel;
    private Vector3 redoButtonPos;

    private TextMeshPro instructionText;
    private GameObject instructionObj;

    private XRHandSubsystem handSubsystem;

    private static readonly Color ColorTipIdle      = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTipDwelling  = new Color(1.00f, 0.95f, 0.25f, 0.95f);
    private static readonly Color ColorTipOnSurface = new Color(0.20f, 0.95f, 0.95f, 0.95f); // cyan = on surface during drag
    private static readonly Color ColorTapMarker    = new Color(0.30f, 0.85f, 1.00f, 0.95f);
    private static readonly Color ColorPreview      = new Color(0.30f, 0.85f, 1.00f, 0.85f);
    private static readonly Color ColorDragPath     = new Color(0.20f, 0.95f, 0.95f, 0.70f);
    private static readonly Color ColorConfirm      = new Color(0.20f, 0.80f, 0.30f, 0.95f);
    private static readonly Color ColorRedo         = new Color(0.85f, 0.40f, 0.40f, 0.95f);
    private static readonly Color ColorBtnDisabled  = new Color(0.40f, 0.40f, 0.45f, 0.60f);

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void BeginCalibration()
    {
        if (passthroughManager != null)
            passthroughLayer = passthroughManager.GetPassthroughUILayer();

        EnsureVisuals();

        phase = Phase.FirstTap;
        dwellAccum = 0f;
        lastTapTime = -999f;
        hasLastTip = false;
        wasLeftPinched = false;
        wasRightPinched = false;
        dragPolyline.Clear();
        dragPathLength = 0f;
        dragLiftAccum = 0f;
        lastDragRecordTime = -999f;
        eyeYAccum = 0f;
        eyeYSamples = 0;

        ClearTapMarker();
        HidePreviewRectangle();
        HideDragPath();
        if (confirmButton != null) confirmButton.SetActive(false);
        if (redoButton    != null) redoButton.SetActive(false);

        SetInstruction("Tap the near-left corner of your writing area.\nHold still" +
                       (requirePinchToCapture ? ", then pinch to capture." : " to capture."));
        if (logEvents) Debug.Log("[TableTapCalibrator] BeginCalibration — waiting for first tap + drag.");
    }

    public void Cleanup()
    {
        phase = Phase.Inactive;
        if (tipIndicator        != null) tipIndicator.SetActive(false);
        if (confirmButton       != null) confirmButton.SetActive(false);
        if (redoButton          != null) redoButton.SetActive(false);
        if (instructionObj      != null) instructionObj.SetActive(false);
        if (previewRectangleObj != null) previewRectangleObj.SetActive(false);
        if (dragPathObj         != null) dragPathObj.SetActive(false);
        if (tapMarker           != null) tapMarker.SetActive(false);
    }

    public void ResetState() => BeginCalibration();

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (phase == Phase.Inactive) return;

        Camera cam = Camera.main;
        if (cam != null)
        {
            eyeYAccum += cam.transform.position.y;
            eyeYSamples++;
        }

        switch (phase)
        {
            case Phase.FirstTap:       UpdateFirstTapPhase();   break;
            case Phase.Dragging:       UpdateDraggingPhase();   break;
            case Phase.AwaitingConfirm: UpdateAwaitingConfirm(); break;
        }
    }

    private void LateUpdate()
    {
        if (phase == Phase.Inactive) return;
        BillboardInstruction();
    }

    // ================================================================
    // FIRST-TAP PHASE
    // ================================================================

    private void UpdateFirstTapPhase()
    {
        if (stylusTipProvider == null || !stylusTipProvider.IsCalibrated ||
            !stylusTipProvider.TipWorldPosition.HasValue)
        {
            if (tipIndicator != null) tipIndicator.SetActive(false);
            SetInstruction("Waiting for stylus tracking...");
            dwellAccum = 0f;
            hasLastTip = false;
            return;
        }

        Vector3 tipPos = stylusTipProvider.TipWorldPosition.Value;
        tipIndicator.SetActive(true);
        tipIndicator.transform.position = tipPos;

        float now = Time.time;
        float linearSpeed = 0f;
        if (hasLastTip)
        {
            float dt = Mathf.Max(now - lastTipTime, 1e-4f);
            linearSpeed = (tipPos - lastTipPos).magnitude / dt;
        }
        lastTipPos = tipPos;
        lastTipTime = now;
        hasLastTip = true;

        bool onCooldown = (now - lastTapTime) < tapCooldownSeconds;

        if (!onCooldown && linearSpeed < tapStillnessVelocity)
        {
            dwellAccum += Time.deltaTime;
            SetTipColor(ColorTipDwelling);
            if (dwellAccum >= tapDwellSeconds)
            {
                if (!requirePinchToCapture)
                    CaptureFirstTap(tipPos);
                else if (ConsumePinchCaptureEdge())
                    CaptureFirstTap(tipPos);
            }
        }
        else
        {
            dwellAccum = 0f;
            SetTipColor(ColorTipIdle);
            RefreshPinchStateHistory();
        }
    }

    private void CaptureFirstTap(Vector3 tipPos)
    {
        firstTapPos = tipPos;
        surfaceYLocked = tipPos.y;
        lastTapTime = Time.time;
        dwellAccum = 0f;

        // Drop a marker at the tapped corner
        if (tapMarker != null)
        {
            tapMarker.transform.position = tipPos;
            tapMarker.SetActive(true);
        }

        if (logEvents)
            Debug.Log($"[TableTapCalibrator] First tap at {tipPos}. Surface Y locked = {surfaceYLocked:F3} m.");

        dragPolyline.Clear();
        dragPathLength = 0f;
        dragLiftAccum = 0f;
        lastDragRecordTime = Time.time;

        phase = Phase.Dragging;
        HideDragPath();
        dragPathObj.SetActive(true);

        SetInstruction("Now slide the pen across the table\nto the far-right corner.\n" +
                       "Lift the pen when done.");
    }

    // ================================================================
    // DRAGGING PHASE
    // ================================================================

    private void UpdateDraggingPhase()
    {
        if (stylusTipProvider == null || !stylusTipProvider.IsCalibrated ||
            !stylusTipProvider.TipWorldPosition.HasValue)
        {
            SetInstruction("Stylus lost — please show the pen.");
            return;
        }

        Vector3 tipPos = stylusTipProvider.TipWorldPosition.Value;
        tipIndicator.SetActive(true);
        tipIndicator.transform.position = tipPos;

        float aboveY = tipPos.y - surfaceYLocked;
        bool onSurface = Mathf.Abs(aboveY) <= dragLiftThresholdM;

        // Tip indicator colour: cyan = on surface, green = off surface
        SetTipColor(onSurface ? ColorTipOnSurface : ColorTipIdle);

        // Record drag sample at ~50 Hz when tip is on-surface
        float now = Time.time;
        if (onSurface && (now - lastDragRecordTime) >= dragRecordIntervalSec)
        {
            if (dragPolyline.Count > 0)
                dragPathLength += Vector3.Distance(
                    new Vector3(tipPos.x, surfaceYLocked, tipPos.z),
                    new Vector3(dragPolyline[dragPolyline.Count - 1].x,
                                surfaceYLocked,
                                dragPolyline[dragPolyline.Count - 1].z));

            dragPolyline.Add(tipPos);
            lastDragRecordTime = now;
            UpdateDragPathVisual();
        }

        // Update instruction with progress
        int pts = dragPolyline.Count;
        bool enoughDrag = dragPathLength >= minDragLengthM;
        if (pts < 3)
            SetInstruction("Slide the pen along the table surface\nto the far-right corner.");
        else
            SetInstruction($"Dragging... {dragPathLength * 100f:F0} cm covered.\n" +
                           (enoughDrag ? "Lift the pen to finish." : "Keep going."));

        // Lift detection: pen above surface for dragLiftDurationSec
        if (aboveY > dragLiftThresholdM)
        {
            dragLiftAccum += Time.deltaTime;
            if (dragLiftAccum >= dragLiftDurationSec && enoughDrag)
            {
                FinalizeDrag();
                return;
            }
        }
        else
        {
            dragLiftAccum = 0f;
        }
    }

    private void UpdateDragPathVisual()
    {
        if (dragPathRenderer == null) return;
        dragPathRenderer.positionCount = dragPolyline.Count;
        for (int i = 0; i < dragPolyline.Count; i++)
            dragPathRenderer.SetPosition(i, dragPolyline[i]);
    }

    private void FinalizeDrag()
    {
        if (dragPolyline.Count < 2)
        {
            SetInstruction("Drag too short — please try again.");
            BeginCalibration();
            return;
        }

        computedTable = BuildDetectedTableFromDrag();
        PopulateComputedCornersIntoPreview(computedTable);

        phase = Phase.AwaitingConfirm;
        awaitConfirmEnterTime = Time.time;

        HideDragPath();
        UpdatePreviewRectangle();
        ShowConfirmAndRedo();
        SetInstructionForConfirm();

        if (logEvents)
            Debug.Log($"[TableTapCalibrator] Drag complete. Computed table: pos={computedTable.position}, " +
                      $"size={computedTable.size}, yaw={computedTable.rotation.eulerAngles.y:F1}°, " +
                      $"surfaceY={computedTable.avgTapSurfaceY:F3} m.");
    }

    // ================================================================
    // TABLE SOLVER (drag-based)
    // ================================================================

    private DetectedTable BuildDetectedTableFromDrag()
    {
        Vector3 firstPt = dragPolyline.Count > 0 ? dragPolyline[0] : firstTapPos;
        Vector3 lastPt  = dragPolyline.Count > 1 ? dragPolyline[dragPolyline.Count - 1] : firstPt;

        // Forward direction: first drag point → last drag point (XZ only).
        Vector3 fwdXZ = new Vector3(lastPt.x - firstPt.x, 0f, lastPt.z - firstPt.z);
        if (fwdXZ.sqrMagnitude < 1e-4f)
        {
            Camera cam = Camera.main;
            fwdXZ = cam != null ? cam.transform.forward : Vector3.forward;
            fwdXZ.y = 0f;
        }
        fwdXZ.Normalize();

        // Right direction: 90° CW from forward in XZ plane.
        Vector3 rightXZ = new Vector3(-fwdXZ.z, 0f, fwdXZ.x);

        // Depth: projected drag length along the forward axis.
        Vector3 dragDelta = new Vector3(lastPt.x - firstPt.x, 0f, lastPt.z - firstPt.z);
        float depth = Mathf.Abs(Vector3.Dot(dragDelta, fwdXZ));

        // Width: max perpendicular span of the polyline.
        float minPerp = 0f, maxPerp = 0f;
        foreach (Vector3 pt in dragPolyline)
        {
            Vector3 offs = new Vector3(pt.x - firstPt.x, 0f, pt.z - firstPt.z);
            float p = Vector3.Dot(offs, rightXZ);
            if (p < minPerp) minPerp = p;
            if (p > maxPerp) maxPerp = p;
        }
        float perpSpan = maxPerp - minPerp;
        float aspectRatio = defaultTableAspect.y > 0f
            ? defaultTableAspect.x / defaultTableAspect.y
            : 1.5f;
        float width = perpSpan >= minWidthDeviationM
            ? perpSpan
            : depth * aspectRatio;

        // Centre: centroid offset from firstPt by half depth along forward and
        // half perpendicular span along right.
        float centerDepth = depth * 0.5f;
        float centerPerp  = (minPerp + maxPerp) * 0.5f;
        Vector3 center = new Vector3(
            firstPt.x + fwdXZ.x * centerDepth + rightXZ.x * centerPerp,
            surfaceYLocked,
            firstPt.z + fwdXZ.z * centerDepth + rightXZ.z * centerPerp);

        Quaternion rotation = Quaternion.LookRotation(fwdXZ, Vector3.up);

        Vector2 size = new Vector2(
            Mathf.Clamp(width, minSize.x, maxSize.x),
            Mathf.Clamp(depth, minSize.y, maxSize.y));

        Camera c = Camera.main;
        Vector3 headPos = c != null ? c.transform.position : Vector3.zero;
        Vector3 headFwd = c != null ? c.transform.forward : Vector3.forward;
        float avgEyeY = eyeYSamples > 0 ? eyeYAccum / eyeYSamples : headPos.y;

        return new DetectedTable
        {
            position         = center,
            rotation         = rotation,
            size             = size,
            userHeadPosition = headPos,
            userForward      = headFwd,
            avgEyeY          = avgEyeY,
            avgTapSurfaceY   = surfaceYLocked,
        };
    }

    // Populate the preview taps list with 4 computed corners so UpdatePreviewRectangle
    // can draw the rectangle outline without changes.
    private readonly List<Vector3> previewTaps = new List<Vector3>(4);

    private void PopulateComputedCornersIntoPreview(DetectedTable t)
    {
        Vector3 fwd   = t.rotation * Vector3.forward;
        Vector3 right = t.rotation * Vector3.right;
        float hw = t.size.x * 0.5f;
        float hd = t.size.y * 0.5f;
        float sy = t.avgTapSurfaceY;

        previewTaps.Clear();
        previewTaps.Add(Y(t.position - right * hw - fwd * hd, sy)); // near-left
        previewTaps.Add(Y(t.position + right * hw - fwd * hd, sy)); // near-right
        previewTaps.Add(Y(t.position + right * hw + fwd * hd, sy)); // far-right
        previewTaps.Add(Y(t.position - right * hw + fwd * hd, sy)); // far-left
    }

    private static Vector3 Y(Vector3 v, float y) => new Vector3(v.x, y, v.z);

    // ================================================================
    // CONFIRM PHASE
    // ================================================================

    private void UpdateAwaitingConfirm()
    {
        UpdatePreviewRectangle();

        bool armed = Time.time - awaitConfirmEnterTime >= buttonArmDelay;
        SetButtonColor(confirmButtonMat, armed ? ColorConfirm : ColorBtnDisabled);
        SetButtonColor(redoButtonMat,    armed ? ColorRedo    : ColorBtnDisabled);
        if (!armed) return;

        if (!EnsureHandSubsystem()) return;

        Vector3 leftTip  = GetIndexTipOr(Handedness.Left);
        Vector3 rightTip = GetIndexTipOr(Handedness.Right);

        float dConfirm = Mathf.Min(
            Vector3.Distance(leftTip,  confirmButtonPos),
            Vector3.Distance(rightTip, confirmButtonPos));
        float dRedo = Mathf.Min(
            Vector3.Distance(leftTip,  redoButtonPos),
            Vector3.Distance(rightTip, redoButtonPos));

        if (dRedo <= buttonPokeDistance)
        {
            if (logEvents) Debug.Log("[TableTapCalibrator] Redo pressed.");
            BeginCalibration();
            return;
        }

        if (dConfirm <= buttonPokeDistance)
        {
            if (logEvents) Debug.Log("[TableTapCalibrator] Confirm pressed.");
            FinalizeConfirm();
        }
    }

    private void FinalizeConfirm()
    {
        phase = Phase.Inactive;

        if (logEvents)
            Debug.Log($"[TableTapCalibrator] Confirmed: pos={computedTable.position}, " +
                      $"size={computedTable.size}, yawDeg={computedTable.rotation.eulerAngles.y:F1}, " +
                      $"surfaceY={computedTable.avgTapSurfaceY:F3} m, eyeY={computedTable.avgEyeY:F3} m");

        OnTableConfirmed?.Invoke(computedTable);
    }

    // ================================================================
    // PINCH HELPERS (reused from original)
    // ================================================================

    private bool EnsureHandSubsystem()
    {
        if (handSubsystem == null || !handSubsystem.running)
            handSubsystem = WhiteboardPen.GetHandSubsystem();
        return handSubsystem != null;
    }

    private bool ConsumePinchCaptureEdge()
    {
        if (!EnsureHandSubsystem()) return false;

        bool leftPinched  = GetPinchState(Handedness.Left);
        bool rightPinched = GetPinchState(Handedness.Right);

        bool leftEdge  = leftPinched  && !wasLeftPinched;
        bool rightEdge = rightPinched && !wasRightPinched;

        wasLeftPinched  = leftPinched;
        wasRightPinched = rightPinched;

        switch (pinchCaptureHand)
        {
            case PinchCaptureHandMode.Left:   return leftEdge;
            case PinchCaptureHandMode.Right:  return rightEdge;
            case PinchCaptureHandMode.Either: return leftEdge || rightEdge;
            case PinchCaptureHandMode.OppositeStylusHand:
            default:
                if (TryGetStylusHand(out Handedness stylusHandLocal))
                {
                    Handedness opp = stylusHandLocal == Handedness.Right
                        ? Handedness.Left : Handedness.Right;
                    return opp == Handedness.Left ? leftEdge : rightEdge;
                }
                return leftEdge || rightEdge;
        }
    }

    private void RefreshPinchStateHistory()
    {
        if (!EnsureHandSubsystem())
        {
            wasLeftPinched = false;
            wasRightPinched = false;
            return;
        }
        wasLeftPinched  = GetPinchState(Handedness.Left);
        wasRightPinched = GetPinchState(Handedness.Right);
    }

    private bool GetPinchState(Handedness hand)
    {
        Vector3 thumb  = GetJointTipOr(hand, XRHandJointID.ThumbTip);
        Vector3 middle = GetJointTipOr(hand, XRHandJointID.MiddleTip);
        if (thumb.x >= 1e5f || middle.x >= 1e5f) return false;
        return Vector3.Distance(thumb, middle) < pinchThreshold;
    }

    private bool TryGetStylusHand(out Handedness hand)
    {
        hand = Handedness.Right;
        if (stylusTipProvider == null || stylusTipProvider.wristTracker == null) return false;
        hand = stylusTipProvider.wristTracker.handedness;
        return true;
    }

    // ================================================================
    // INSTRUCTION TEXT
    // ================================================================

    private void SetInstructionForConfirm()
    {
        SetInstruction("Press Confirm to place your whiteboard,\nor Redo to start over.");
    }

    // ================================================================
    // VISUALS
    // ================================================================

    private void EnsureVisuals()
    {
        if (tipIndicator == null)
        {
            tipIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tipIndicator.name = "TableTapTipIndicator";
            Destroy(tipIndicator.GetComponent<Collider>());
            tipIndicator.transform.localScale = Vector3.one * (tipIndicatorRadius * 2f);
            tipIndicatorMat = MakeMat(ColorTipIdle);
            ApplyMat(tipIndicator, tipIndicatorMat);
            PassthroughManager.SetLayerRecursive(tipIndicator, passthroughLayer);
            tipIndicator.SetActive(false);
        }

        if (tapMarker == null)
        {
            tapMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tapMarker.name = "TableTapMarker";
            Destroy(tapMarker.GetComponent<Collider>());
            tapMarker.transform.localScale = Vector3.one * (tapMarkerRadius * 2f);
            tapMarkerMat = MakeMat(ColorTapMarker);
            ApplyMat(tapMarker, tapMarkerMat);
            PassthroughManager.SetLayerRecursive(tapMarker, passthroughLayer);
            tapMarker.SetActive(false);
        }

        if (previewRectangleObj == null)
        {
            previewRectangleObj = new GameObject("TableTapPreviewRect");
            previewRectangleObj.layer = passthroughLayer;
            previewRectangle = previewRectangleObj.AddComponent<LineRenderer>();
            previewRectangle.useWorldSpace = true;
            previewRectangle.loop = false;
            previewRectangle.positionCount = 0;
            previewRectangle.startWidth = 0.005f;
            previewRectangle.endWidth   = 0.005f;
            previewRectangle.numCapVertices    = 2;
            previewRectangle.numCornerVertices = 2;
            previewRectangle.material = MakeMat(ColorPreview);
            previewRectangle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            previewRectangle.receiveShadows = false;
            previewRectangleObj.SetActive(false);
        }

        if (dragPathObj == null)
        {
            dragPathObj = new GameObject("TableTapDragPath");
            dragPathObj.layer = passthroughLayer;
            dragPathRenderer = dragPathObj.AddComponent<LineRenderer>();
            dragPathRenderer.useWorldSpace = true;
            dragPathRenderer.loop = false;
            dragPathRenderer.positionCount = 0;
            dragPathRenderer.startWidth = 0.004f;
            dragPathRenderer.endWidth   = 0.004f;
            dragPathRenderer.material = MakeMat(ColorDragPath);
            dragPathRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dragPathRenderer.receiveShadows = false;
            dragPathObj.SetActive(false);
        }

        if (confirmButton == null)
        {
            confirmButton = MakeButton("TableTapConfirm", "Confirm", out confirmButtonMat, out confirmLabel);
            confirmButton.SetActive(false);
        }

        if (redoButton == null)
        {
            redoButton = MakeButton("TableTapRedo", "Redo", out redoButtonMat, out redoLabel);
            redoButton.SetActive(false);
        }

        if (instructionObj == null)
        {
            instructionObj = new GameObject("TableTapInstruction");
            instructionObj.layer = passthroughLayer;

            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "BG";
            bg.transform.SetParent(instructionObj.transform, false);
            bg.transform.localScale = new Vector3(1.5f, 0.45f, 1f);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.001f);
            bg.layer = passthroughLayer;
            Destroy(bg.GetComponent<Collider>());
            ApplyMat(bg, MakeMat(new Color(0f, 0f, 0f, 0.65f)));

            instructionText = instructionObj.AddComponent<TextMeshPro>();
            instructionText.fontSize = fontSize;
            instructionText.alignment = TextAlignmentOptions.Center;
            instructionText.color = Color.white;
            instructionText.rectTransform.sizeDelta = new Vector2(1.4f, 0.5f);
            instructionText.textWrappingMode = TextWrappingModes.Normal;
            instructionText.sortingOrder = 1;
        }
    }

    private void ClearTapMarker()
    {
        if (tapMarker != null) tapMarker.SetActive(false);
    }

    private void UpdatePreviewRectangle()
    {
        if (previewRectangle == null || previewTaps.Count < 4)
        {
            HidePreviewRectangle();
            return;
        }

        previewRectangleObj.SetActive(true);
        previewRectangle.positionCount = 5;
        for (int i = 0; i < 4; i++)
            previewRectangle.SetPosition(i, previewTaps[i]);
        previewRectangle.SetPosition(4, previewTaps[0]); // close the loop
        previewRectangle.startColor = previewRectangle.endColor = ColorPreview;
    }

    private void HidePreviewRectangle()
    {
        if (previewRectangleObj != null) previewRectangleObj.SetActive(false);
    }

    private void HideDragPath()
    {
        if (dragPathObj != null) dragPathObj.SetActive(false);
    }

    private void ShowConfirmAndRedo()
    {
        if (confirmButton == null || redoButton == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Position buttons above the centre of the computed table.
        Vector3 centre = computedTable.position + Vector3.up * buttonHeightAboveRect;

        Vector3 toUser = cam.transform.position - centre;
        toUser.y = 0f;
        if (toUser.sqrMagnitude < 1e-4f) toUser = -cam.transform.forward;
        toUser.Normalize();

        Quaternion rot   = Quaternion.LookRotation(-toUser, Vector3.up);
        Vector3 right    = Vector3.Cross(Vector3.up, -toUser).normalized;

        confirmButtonPos = centre + right * (buttonSpacing * 0.5f);
        redoButtonPos    = centre - right * (buttonSpacing * 0.5f);

        confirmButton.transform.position = confirmButtonPos;
        confirmButton.transform.rotation = rot;
        confirmButton.SetActive(true);

        redoButton.transform.position = redoButtonPos;
        redoButton.transform.rotation = rot;
        redoButton.SetActive(true);
    }

    private void BillboardInstruction()
    {
        if (instructionObj == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.forward;
        fwd.Normalize();

        instructionObj.transform.position =
            cam.transform.position + fwd * instructionForward + Vector3.up * instructionHeight;
        instructionObj.transform.rotation = Quaternion.LookRotation(fwd);
        instructionObj.SetActive(true);
    }

    private void SetInstruction(string msg)
    {
        if (instructionText != null) instructionText.text = msg;
    }

    private void SetTipColor(Color c)
    {
        if (tipIndicatorMat != null) tipIndicatorMat.color = c;
    }

    private static void SetButtonColor(Material mat, Color c)
    {
        if (mat != null) mat.color = c;
    }

    // ================================================================
    // HAND JOINT HELPERS
    // ================================================================

    private Vector3 GetIndexTipOr(Handedness hand)
    {
        return GetJointTipOr(hand, XRHandJointID.IndexTip);
    }

    private Vector3 GetJointTipOr(Handedness hand, XRHandJointID jointId)
    {
        if (!EnsureHandSubsystem()) return new Vector3(1e6f, 1e6f, 1e6f);

        XRHand xrHand = hand == Handedness.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!xrHand.isTracked) return new Vector3(1e6f, 1e6f, 1e6f);

        XRHandJoint joint = xrHand.GetJoint(jointId);
        if (!joint.TryGetPose(out Pose pose)) return new Vector3(1e6f, 1e6f, 1e6f);

        var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        Transform offset = (origin != null && origin.CameraFloorOffsetObject != null)
            ? origin.CameraFloorOffsetObject.transform
            : (Camera.main != null ? Camera.main.transform.parent : null);
        return offset != null ? offset.TransformPoint(pose.position) : pose.position;
    }

    // ================================================================
    // MATERIAL / BUTTON HELPERS
    // ================================================================

    private GameObject MakeButton(string objName, string label,
                                   out Material mat, out TextMeshPro tmp)
    {
        var btn = GameObject.CreatePrimitive(PrimitiveType.Cube);
        btn.name = objName;
        Destroy(btn.GetComponent<Collider>());
        btn.transform.localScale = new Vector3(0.12f, 0.055f, 0.02f);
        mat = MakeMat(Color.white);
        ApplyMat(btn, mat);
        PassthroughManager.SetLayerRecursive(btn, passthroughLayer);

        var labelObj = new GameObject("Label");
        labelObj.transform.SetParent(btn.transform, false);
        labelObj.layer = passthroughLayer;
        tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.text = label;
        tmp.fontSize = 0.25f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(0.14f, 0.07f);
        labelObj.transform.localScale = new Vector3(
            1f / 0.12f * 0.10f,
            1f / 0.055f * 0.045f,
            1f / 0.02f * 0.01f);
        labelObj.transform.localPosition = new Vector3(0f, 0f, -0.6f);

        return btn;
    }

    private static Material MakeMat(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(shader != null ? shader : Shader.Find("Unlit/Color"));
        mat.SetFloat("_Surface", 1f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.color = color;
        return mat;
    }

    private static void ApplyMat(GameObject go, Material mat)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;
        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private void OnDestroy()
    {
        if (tipIndicator        != null) Destroy(tipIndicator);
        if (tapMarker           != null) Destroy(tapMarker);
        if (previewRectangleObj != null) Destroy(previewRectangleObj);
        if (dragPathObj         != null) Destroy(dragPathObj);
        if (confirmButton       != null) Destroy(confirmButton);
        if (redoButton          != null) Destroy(redoButton);
        if (instructionObj      != null) Destroy(instructionObj);

        if (tipIndicatorMat  != null) Destroy(tipIndicatorMat);
        if (tapMarkerMat     != null) Destroy(tapMarkerMat);
        if (confirmButtonMat != null) Destroy(confirmButtonMat);
        if (redoButtonMat    != null) Destroy(redoButtonMat);
    }
}
