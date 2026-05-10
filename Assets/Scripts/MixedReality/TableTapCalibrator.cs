using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using UnityEngine.Video;

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
///   3. CONFIRM: Confirm / Redo Canvas buttons appear above the preview rectangle.
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
    [Tooltip("Cooldown before the buttons respond to a click after appearing.")]
    public float buttonArmDelay = 0.4f;

    [Header("Visuals")]
    [Tooltip("Radius of the pen-tip indicator ring (metres).")]
    public float tipIndicatorRadius = 0.012f;
    [Tooltip("Radius of the X-marker dropped at the confirmed tap (metres).")]
    public float tapMarkerRadius = 0.010f;

    [Header("Instruction Text")]
    public float instructionForward = 0.90f;
    public float instructionHeight = 0.18f;

    [Header("Video")]
    [SerializeField] VideoClip calibrationVideo;

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

    // 3D spatial — kept as-is (track real pen tip in world space)
    private GameObject tipIndicator;
    private Material   tipIndicatorMat;

    private GameObject tapMarker;
    private Material   tapMarkerMat;

    private LineRenderer previewRectangle;
    private GameObject  previewRectangleObj;

    private LineRenderer dragPathRenderer;
    private GameObject  dragPathObj;

    // 2D Canvas UI — instruction panel
    private GameObject      instructionCanvas;
    private TextMeshProUGUI instructionTmp;

    // 2D Canvas UI — Confirm / Redo choice panel
    private GameObject choiceCanvas;
    private Button     confirmBtn;
    private Button     redoBtn;

    // 2D Canvas UI — Video panel
    private GameObject    videoCanvas;
    private RenderTexture _videoRT;

    private XRHandSubsystem handSubsystem;

    // Tip / marker colors (3D spatial elements)
    private static readonly Color ColorTipIdle      = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTipDwelling  = new Color(1.00f, 0.95f, 0.25f, 0.95f);
    private static readonly Color ColorTipOnSurface = new Color(0.20f, 0.95f, 0.95f, 0.95f);
    private static readonly Color ColorTapMarker    = new Color(0.30f, 0.85f, 1.00f, 0.95f);
    private static readonly Color ColorPreview      = new Color(0.30f, 0.85f, 1.00f, 0.85f);
    private static readonly Color ColorDragPath     = new Color(0.20f, 0.95f, 0.95f, 0.70f);

    // 2D Canvas palette — mirrors JournalReviewController / CalibrationConfirmPanel
    private static readonly Color s_PanelBg     = new Color(0.969f, 0.918f, 0.918f, 1.00f);
    private static readonly Color s_AccentMauve = new Color(0.780f, 0.663f, 0.722f, 1.00f);
    private static readonly Color s_TextDark    = new Color(0.369f, 0.329f, 0.349f, 1.00f);
    private static readonly Color s_BtnConfirm  = new Color(0.490f, 0.730f, 0.560f, 1.00f); // sage green
    private static readonly Color s_BtnRedo     = new Color(0.790f, 0.470f, 0.450f, 1.00f); // warm coral

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
        if (choiceCanvas != null) choiceCanvas.SetActive(false);

        SetInstruction("Tap the near-left corner of your writing area.\nHold still" +
                       (requirePinchToCapture ? ", then pinch to capture." : " to capture."));
        if (videoCanvas != null)
        {
            videoCanvas.GetComponent<VideoPlayer>()?.Play();
            videoCanvas.SetActive(true);
        }
        if (logEvents) Debug.Log("[TableTapCalibrator] BeginCalibration — waiting for first tap + drag.");
    }

    public void Cleanup()
    {
        phase = Phase.Inactive;
        if (tipIndicator        != null) tipIndicator.SetActive(false);
        if (choiceCanvas        != null) choiceCanvas.SetActive(false);
        if (instructionCanvas   != null) instructionCanvas.SetActive(false);
        if (previewRectangleObj != null) previewRectangleObj.SetActive(false);
        if (dragPathObj         != null) dragPathObj.SetActive(false);
        if (tapMarker           != null) tapMarker.SetActive(false);
        if (videoCanvas != null)
        {
            videoCanvas.GetComponent<VideoPlayer>()?.Stop();
            videoCanvas.SetActive(false);
        }
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
            case Phase.FirstTap:        UpdateFirstTapPhase();   break;
            case Phase.Dragging:        UpdateDraggingPhase();   break;
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

        SetTipColor(onSurface ? ColorTipOnSurface : ColorTipIdle);

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

        int pts = dragPolyline.Count;
        bool enoughDrag = dragPathLength >= minDragLengthM;
        if (pts < 3)
            SetInstruction("Slide the pen along the table surface\nto the far-right corner.");
        else
            SetInstruction($"Dragging... {dragPathLength * 100f:F0} cm covered.\n" +
                           (enoughDrag ? "Lift the pen to finish." : "Keep going."));

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

        Vector3 fwdXZ = new Vector3(lastPt.x - firstPt.x, 0f, lastPt.z - firstPt.z);
        if (fwdXZ.sqrMagnitude < 1e-4f)
        {
            Camera cam = Camera.main;
            fwdXZ = cam != null ? cam.transform.forward : Vector3.forward;
            fwdXZ.y = 0f;
        }
        fwdXZ.Normalize();

        Vector3 rightXZ = new Vector3(-fwdXZ.z, 0f, fwdXZ.x);

        Vector3 dragDelta = new Vector3(lastPt.x - firstPt.x, 0f, lastPt.z - firstPt.z);
        float depth = Mathf.Abs(Vector3.Dot(dragDelta, fwdXZ));

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

    private readonly List<Vector3> previewTaps = new List<Vector3>(4);

    private void PopulateComputedCornersIntoPreview(DetectedTable t)
    {
        Vector3 fwd   = t.rotation * Vector3.forward;
        Vector3 right = t.rotation * Vector3.right;
        float hw = t.size.x * 0.5f;
        float hd = t.size.y * 0.5f;
        float sy = t.avgTapSurfaceY;

        previewTaps.Clear();
        previewTaps.Add(Y(t.position - right * hw - fwd * hd, sy));
        previewTaps.Add(Y(t.position + right * hw - fwd * hd, sy));
        previewTaps.Add(Y(t.position + right * hw + fwd * hd, sy));
        previewTaps.Add(Y(t.position - right * hw + fwd * hd, sy));
    }

    private static Vector3 Y(Vector3 v, float y) => new Vector3(v.x, y, v.z);

    // ================================================================
    // CONFIRM PHASE
    // ================================================================

    private void UpdateAwaitingConfirm()
    {
        UpdatePreviewRectangle();

        if (confirmBtn == null) return;
        bool armed = Time.time - awaitConfirmEnterTime >= buttonArmDelay;
        if (!confirmBtn.interactable && armed)
        {
            confirmBtn.interactable = true;
            redoBtn.interactable    = true;
        }
    }

    private void OnConfirmClicked()
    {
        if (phase != Phase.AwaitingConfirm) return;
        if (logEvents) Debug.Log("[TableTapCalibrator] Confirm pressed.");
        FinalizeConfirm();
    }

    private void OnRedoClicked()
    {
        if (phase != Phase.AwaitingConfirm) return;
        if (logEvents) Debug.Log("[TableTapCalibrator] Redo pressed.");
        BeginCalibration();
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
    // PINCH HELPERS
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
        // ── Tip indicator sphere (3D spatial) ─────────────────────────
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

        // ── Tap marker sphere (3D spatial) ────────────────────────────
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

        // ── Preview rectangle (3D LineRenderer) ───────────────────────
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

        // ── Drag path (3D LineRenderer) ────────────────────────────────
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

        // ── Instruction canvas ─────────────────────────────────────────
        if (instructionCanvas == null)
        {
            var root   = new GameObject("TableTapInstruction");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(640f, 300f);
            root.AddComponent<CanvasScaler>();
            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            root.transform.localScale = Vector3.one * 0.001f;
            PassthroughManager.SetLayerRecursive(root, passthroughLayer);

            var bg    = new GameObject("Background");
            bg.transform.SetParent(root.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = s_PanelBg;
            bg.GetComponent<RectTransform>().sizeDelta = new Vector2(640f, 300f);

            var bar    = new GameObject("AccentBar");
            bar.transform.SetParent(bg.transform, false);
            var barImg = bar.AddComponent<Image>();
            barImg.color = s_AccentMauve;
            var barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 1f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot     = new Vector2(0.5f, 1f);
            barRect.sizeDelta = new Vector2(0f, 26f);

            var qGO  = new GameObject("InstructionText");
            qGO.transform.SetParent(bg.transform, false);
            instructionTmp = qGO.AddComponent<TextMeshProUGUI>();
            instructionTmp.fontSize  = 24f;
            instructionTmp.alignment = TextAlignmentOptions.Center;
            instructionTmp.color     = s_TextDark;
            instructionTmp.textWrappingMode = TextWrappingModes.Normal;
            var qRect = qGO.GetComponent<RectTransform>();
            qRect.anchorMin = new Vector2(0.05f, 0.05f);
            qRect.anchorMax = new Vector2(0.95f, 0.92f);
            qRect.offsetMin = qRect.offsetMax = Vector2.zero;

            instructionCanvas = root;
            instructionCanvas.SetActive(false);
        }

        // ── Video canvas ───────────────────────────────────────────────
        if (videoCanvas == null && calibrationVideo != null)
        {
            _videoRT = new RenderTexture(512, 288, 0);
            _videoRT.Create();

            var root   = new GameObject("TableTapVideo");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(480f, 300f);
            root.AddComponent<CanvasScaler>();
            root.transform.localScale = Vector3.one * 0.001f;
            PassthroughManager.SetLayerRecursive(root, passthroughLayer);

            var bg    = new GameObject("Background");
            bg.transform.SetParent(root.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = s_PanelBg;
            bg.GetComponent<RectTransform>().sizeDelta = new Vector2(480f, 300f);

            var bar    = new GameObject("AccentBar");
            bar.transform.SetParent(bg.transform, false);
            var barImg = bar.AddComponent<Image>();
            barImg.color = s_AccentMauve;
            var barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 1f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot     = new Vector2(0.5f, 1f);
            barRect.sizeDelta = new Vector2(0f, 26f);

            var videoGO  = new GameObject("VideoDisplay");
            videoGO.transform.SetParent(bg.transform, false);
            var raw = videoGO.AddComponent<RawImage>();
            raw.texture = _videoRT;
            var videoRect = videoGO.GetComponent<RectTransform>();
            videoRect.anchorMin = new Vector2(0.04f, 0.04f);
            videoRect.anchorMax = new Vector2(0.96f, 0.92f);
            videoRect.offsetMin = videoRect.offsetMax = Vector2.zero;

            var vp = root.AddComponent<VideoPlayer>();
            vp.renderMode      = VideoRenderMode.RenderTexture;
            vp.targetTexture   = _videoRT;
            vp.clip            = calibrationVideo;
            vp.isLooping       = true;
            vp.playOnAwake     = false;
            vp.audioOutputMode = VideoAudioOutputMode.None;

            videoCanvas = root;
            videoCanvas.SetActive(false);
        }

        // ── Choice canvas (Confirm + Redo side-by-side) ────────────────
        if (choiceCanvas == null)
        {
            var panelSize = new Vector2(640f, 120f);

            var root   = new GameObject("TableTapChoicePanel");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.GetComponent<RectTransform>().sizeDelta = panelSize;
            root.AddComponent<CanvasScaler>();
            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            root.transform.localScale = Vector3.one * 0.001f;
            PassthroughManager.SetLayerRecursive(root, passthroughLayer);

            var bg    = new GameObject("Background");
            bg.transform.SetParent(root.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = s_PanelBg;
            bg.GetComponent<RectTransform>().sizeDelta = panelSize;

            var row     = new GameObject("Buttons");
            row.transform.SetParent(bg.transform, false);
            var rowRect = row.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0.03f, 0.1f);
            rowRect.anchorMax = new Vector2(0.97f, 0.9f);
            rowRect.offsetMin = rowRect.offsetMax = Vector2.zero;

            confirmBtn = MakeCanvasButton("Confirm", s_BtnConfirm, Color.white,
                row.transform, new Vector2(0f, 0f), new Vector2(0.44f, 1f));
            confirmBtn.onClick.AddListener(OnConfirmClicked);

            redoBtn = MakeCanvasButton("Redo", s_BtnRedo, Color.white,
                row.transform, new Vector2(0.56f, 0f), new Vector2(1f, 1f));
            redoBtn.onClick.AddListener(OnRedoClicked);

            choiceCanvas = root;
            choiceCanvas.SetActive(false);
        }
    }

    private static Button MakeCanvasButton(string label, Color bgColor, Color textColor,
        Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go  = new GameObject(label.Replace(' ', '_'));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var lblGO = new GameObject("Label");
        lblGO.transform.SetParent(go.transform, false);
        var tmp = lblGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 28f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color     = textColor;
        tmp.fontStyle = FontStyles.Bold;
        var lblRect = lblGO.GetComponent<RectTransform>();
        lblRect.anchorMin = Vector2.zero;
        lblRect.anchorMax = Vector2.one;
        lblRect.offsetMin = lblRect.offsetMax = Vector2.zero;
        return btn;
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
        previewRectangle.SetPosition(4, previewTaps[0]);
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
        if (choiceCanvas == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 centre = computedTable.position + Vector3.up * buttonHeightAboveRect;

        Vector3 toUser = cam.transform.position - centre;
        toUser.y = 0f;
        if (toUser.sqrMagnitude < 1e-4f) toUser = -cam.transform.forward;
        toUser.Normalize();

        choiceCanvas.transform.position = centre;
        choiceCanvas.transform.rotation = Quaternion.LookRotation(-toUser, Vector3.up);

        if (confirmBtn != null) confirmBtn.interactable = false;
        if (redoBtn    != null) redoBtn.interactable    = false;
        choiceCanvas.SetActive(true);
    }

    private void BillboardInstruction()
    {
        if (instructionCanvas == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 basePos = cam.transform.position + fwd * instructionForward + Vector3.up * instructionHeight;

        if (videoCanvas != null)
        {
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            instructionCanvas.transform.position = basePos + right * 0.250f;
            videoCanvas.transform.position       = basePos - right * 0.330f;
            videoCanvas.transform.rotation       = Quaternion.LookRotation(fwd);
        }
        else
        {
            instructionCanvas.transform.position = basePos;
        }
        instructionCanvas.transform.rotation = Quaternion.LookRotation(fwd);
        instructionCanvas.SetActive(true);
    }

    private void SetInstruction(string msg)
    {
        if (instructionTmp != null) instructionTmp.text = msg;
    }

    private void SetTipColor(Color c)
    {
        if (tipIndicatorMat != null) tipIndicatorMat.color = c;
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
    // MATERIAL HELPERS (for 3D spatial elements only)
    // ================================================================

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
        if (choiceCanvas        != null) Destroy(choiceCanvas);
        if (instructionCanvas   != null) Destroy(instructionCanvas);
        if (videoCanvas         != null) Destroy(videoCanvas);

        if (tipIndicatorMat != null) Destroy(tipIndicatorMat);
        if (tapMarkerMat    != null) Destroy(tapMarkerMat);
        if (_videoRT        != null) _videoRT.Release();
    }
}
