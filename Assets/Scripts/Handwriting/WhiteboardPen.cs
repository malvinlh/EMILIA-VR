using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using System;

/// <summary>
/// Draws on a whiteboard using XR Hand Tracking (index finger tip).
/// Touch tolerance is ONLY applied after real contact has occurred
/// (i.e. when the finger has begun to pass through the board).
/// </summary>
[DefaultExecutionOrder(-20)]
public class WhiteboardPen : MonoBehaviour
{
    public JournalSessionManager journalSessionManager;

    [Tooltip("Allows this pen to run when no JournalSessionManager exists (for non-journal scenes such as login).")]
    public bool allowWithoutJournalSession;

    /// <summary>Login scene: hits inside this rect are treated as outside the handwriting area (blocks drawing over buttons).</summary>
    [NonSerialized] public RectTransform loginHandwritingExclusionArea;
    
    [Tooltip("Which hand drives this pen.")]
    public Handedness handedness = Handedness.Right;

    [Header("Stylus (Optional)")]
    [Tooltip("When assigned and calibrated, uses the physical stylus tip position " +
             "from StylusTipProvider instead of the index finger tip. Falls back to " +
             "finger tracking automatically if the stylus is not calibrated.")]
    public StylusTipProvider stylusTipProvider;

    private Whiteboard whiteboard;
    private RaycastHit touch;

    private XRHandSubsystem handSubsystem;

    // ── Contact state ────────────────────────────────────────────────
    // True only after a real, physical contact has been detected.
    private bool hasMadeContact;

    // Board-plane reference recorded on contact so we can check which
    // side of the board the fingertip is on.
    private Vector3 boardNormal;
    private Vector3 boardPoint;

    // ── Constants ─────────────────────────────────────────────────────
    private const int WHITEBOARD_LAYER = 10;

    // ── Pinky-pinch clear (edge detection) ────────────────────────────
    private bool pinkyPinchActive;
    private float pinkyPinchHoldTimer;
    private bool pinkyClearTriggeredThisHold;

    private int consecutiveNoContactFrames;
    private float lastTrackedTime;
    private float lastTouchTime = -999f;
    private Vector3 lastContactWorldPoint;
    private bool hasLastContactWorldPoint;

    // Extra depth allowed AFTER contact (meters)
    [Tooltip("Extra depth allowed after the finger has contacted the board.")]
    public float touchTolerance = 0.004f;

    [Header("Stability Profile")]
    [Tooltip("Apply recommended runtime minimums for contact continuity and recognition cadence.")]
    public bool enforceRecommendedStabilityProfile = false;

    [Header("Contact Stability")]
    [Tooltip("Consecutive no-contact frames required before a stroke is considered lifted.")]
    [Range(1, 6)]
    public int contactLossFramesToEndStroke = 1;

    [Tooltip("Grace window (seconds) to keep contact alive when hand tracking drops briefly.")]
    [Range(0f, 0.2f)]
    public float trackingLossGraceSeconds = 0.05f;

    [Tooltip("Idle time (seconds) before smoothing filters are reinitialized.")]
    [Range(0.05f, 1f)]
    public float smoothingIdleResetSeconds = 0.25f;

    [Tooltip("Minimum world-space distance (metres) before buffering a new stroke point.")]
    [Range(0f, 0.01f)]
    public float minBufferedPointDistance = 0.0008f;

    [Header("Stroke Finalization")]
    [Tooltip("Minimum points required for a standalone stroke segment.")]
    [Range(1, 12)]
    public int minPointsPerStroke = 3;

    [Tooltip("Minimum stroke duration (seconds) required for a standalone segment.")]
    [Range(0f, 0.5f)]
    public float minStrokeDurationSeconds = 0.08f;

    [Tooltip("Minimum stroke travel distance (metres) required for a standalone segment.")]
    [Range(0f, 0.05f)]
    public float minStrokeTravelDistance = 0.004f;

    [Tooltip("If a tiny stroke occurs shortly after the previous stroke, merge it instead of splitting.")]
    [Range(0f, 0.5f)]
    public float microStrokeMergeGapSeconds = 0.2f;

    [Tooltip("Maximum endpoint gap (metres) for merging a tiny stroke into the previous stroke.")]
    [Range(0f, 0.05f)]
    public float microStrokeMergeMaxDistance = 0.02f;

    [Header("Recognition Gating")]
    [Tooltip("Minimum total buffered points required before auto-recognition triggers.")]
    [Range(1, 256)]
    public int minBufferedPointsBeforeRecognize = 8;

    [Tooltip("Minimum interval (seconds) between ML Kit recognition requests.")]
    [Range(0f, 3f)]
    public float minRecognizeIntervalSeconds = 0.7f;

    [Header("Clear Gesture Hysteresis")]

    [Tooltip("Thumb-pinky distance to engage clear gesture (metres).")]
    [Range(0.005f, 0.05f)]
    public float pinkyPinchCloseThreshold = 0.018f;

    [Tooltip("Thumb-pinky distance to release clear gesture (metres).")]
    [Range(0.005f, 0.06f)]
    public float pinkyPinchOpenThreshold = 0.026f;

    [Tooltip("Hold time (seconds) required for pinky clear to trigger.")]
    [Range(0.1f, 1.5f)]
    public float pinkyPinchHoldSeconds = 0.45f;

    // ── Position smoothing (One Euro Filter) ────────────────────
    [Tooltip("Min cutoff frequency (Hz). Lower = more smoothing when slow. " +
             "Higher values preserve fine handwriting details (serifs, dots, hooks).")]
    [Range(0.1f, 10f)]
    public float filterMinCutoff = 2.5f;

    [Tooltip("Speed coefficient. Higher = less latency during fast strokes.")]
    [Range(0f, 1f)]
    public float filterBeta = 0.03f;

    [Tooltip("Derivative cutoff frequency (Hz).")]
    [Range(0.1f, 5f)]
    public float filterDCutoff = 1.0f;

    private OneEuroFilter filterX;
    private OneEuroFilter filterY;
    private float smoothedX, smoothedY;
    private bool hasSmoothedPosition;

    // ── Touch-particle feedback ───────────────────────────────────────
    private ParticleSystem touchParticles;
    private bool wasTouchingLastFrame;
    [SerializeField] private Material particleMaterial;

    // ── Hover pointer (right hand only) ───────────────────────────
    [Tooltip("Maximum distance (metres) for the hover pointer ray.")]
    public float hoverDistance = 1.0f;
    private Whiteboard hoverWhiteboard;

    // ── Digital Ink Recognition ───────────────────────────────────
    private DigitalInkBridge inkBridge;
    private bool strokeActive;

    // ML Kit logical writing canvas (pixels).
    private const float MLKIT_AREA_W = 300f;
    private const float MLKIT_AREA_H = 200f;
    // Physical-to-pixel factor: 1 m of finger movement = 1000 ML Kit pixels.
    private const float MLKIT_PX_PER_M = 1000f;

    [Tooltip("Seconds of idle time after the last stroke before auto-recognition fires.")]
    public float autoRecognizeDelay = 1.0f;

    [Header("Diagnostics")]
    [Tooltip("Enable periodic stroke/contact diagnostics logs for on-device tuning.")]
    public bool enableStrokeDiagnostics;

    [Tooltip("Seconds between diagnostics log entries while journaling.")]
    [Range(1f, 60f)]
    public float diagnosticsLogIntervalSeconds = 8f;

    [Tooltip("Warn when stroke-finalize rate exceeds this value (splits per minute).")]
    [Range(1f, 180f)]
    public float strokeSplitWarnPerMinute = 24f;

    private float diagnosticsWindowStartTime;
    private float nextDiagnosticsLogTime;
    private int diagTouchStartCount;
    private int diagTouchEndCount;
    private int diagDropoutHoldFrames;
    private int diagTrackingGraceFrames;
    private int diagStrokeFinalizeCount;
    private int diagRecognizeCount;
    private int diagBufferedPointCount;
    private int diagClearGestureCount;

    // ── Buffered strokes for PCA-based rotation ─────────────────────
    // Strokes are buffered in world-space instead of being sent to ML Kit
    // in real-time.  When recognition triggers (idle timer), PCA determines
    // the principal writing direction and all coordinates are rotated so
    // ML Kit always sees left-to-right text — regardless of the physical
    // writing orientation on the board.
    private struct BufferedInkPoint
    {
        public Vector3 worldPos;
        public long timestamp;
    }
    private readonly List<List<BufferedInkPoint>> bufferedStrokes = new List<List<BufferedInkPoint>>();
    private List<BufferedInkPoint> currentStrokeBuffer;
    private float lastStrokeBufferEndTime;
    private bool hasBufferedStrokes;
    private float currentStrokeStartTime = -1f;
    private float currentStrokeTravelDistance;
    private int bufferedPointTotalCount;
    private float lastRecognizeTime = -999f;

    // ── Scribble integration ────────────────────────────────────────
    /// <summary>Metadata about the strokes that were just flushed for recognition.</summary>
    public struct StrokeMetadata
    {
        public Vector3 center;
        public Bounds bounds;
        public Vector3 right;
        public Vector3 forward;
    }

    /// <summary>Fired after strokes are flushed for recognition (before buffer is cleared).</summary>
    public event System.Action<StrokeMetadata> OnStrokesFlushed;

    /// <summary>Fired when the board is cleared via pinky-pinch.</summary>
    public event System.Action OnBoardCleared;

    /// <summary>World-space touch point while drawing, null when not touching.</summary>
    public Vector3? CurrentTouchWorldPoint { get; private set; }

    // Cached Camera Floor Offset Object — needed to convert XRHandJoint
    // session-space positions to world space. The XR Origin uses Device
    // tracking mode with a camera Y offset, so joint positions must go through
    // the same transform chain as the camera (not the XR Origin root).
    private Transform cameraOffsetTransform;

    /// <summary>True when the pen is actively drawing on a whiteboard.</summary>
    public bool IsDrawing => wasTouchingLastFrame;

    private void Start()
    {
        ApplyRecommendedStabilityProfileIfEnabled();
        CreateTouchParticles();
        inkBridge = DigitalInkBridge.Instance;
        ResolveCameraOffsetTransform();
        ResetDiagnosticsWindow();
    }

    private void ApplyRecommendedStabilityProfileIfEnabled()
    {
        if (!enforceRecommendedStabilityProfile)
            return;

        contactLossFramesToEndStroke = Mathf.Max(contactLossFramesToEndStroke, 4);
        trackingLossGraceSeconds = Mathf.Max(trackingLossGraceSeconds, 0.12f);
        touchTolerance = Mathf.Max(touchTolerance, 0.018f);
        autoRecognizeDelay = Mathf.Max(autoRecognizeDelay, 1.3f);
        minBufferedPointDistance = Mathf.Max(minBufferedPointDistance, 0.001f);

        minPointsPerStroke = Mathf.Max(minPointsPerStroke, 3);
        minStrokeDurationSeconds = Mathf.Max(minStrokeDurationSeconds, 0.08f);
        minStrokeTravelDistance = Mathf.Max(minStrokeTravelDistance, 0.004f);
        microStrokeMergeGapSeconds = Mathf.Max(microStrokeMergeGapSeconds, 0.2f);
        microStrokeMergeMaxDistance = Mathf.Max(microStrokeMergeMaxDistance, 0.02f);

        minBufferedPointsBeforeRecognize = Mathf.Max(minBufferedPointsBeforeRecognize, 8);
        minRecognizeIntervalSeconds = Mathf.Max(minRecognizeIntervalSeconds, 0.7f);

        Debug.Log("[WhiteboardPen] Recommended stability profile applied.");
    }

    private void LateUpdate()
    {
        MaybeLogDiagnostics();
    }

    private void ResetDiagnosticsWindow()
    {
        diagnosticsWindowStartTime = Time.time;
        nextDiagnosticsLogTime = diagnosticsWindowStartTime + Mathf.Max(0.1f, diagnosticsLogIntervalSeconds);
        diagTouchStartCount = 0;
        diagTouchEndCount = 0;
        diagDropoutHoldFrames = 0;
        diagTrackingGraceFrames = 0;
        diagStrokeFinalizeCount = 0;
        diagRecognizeCount = 0;
        diagBufferedPointCount = 0;
        diagClearGestureCount = 0;
    }

    private void MaybeLogDiagnostics(bool force = false)
    {
        if (!enableStrokeDiagnostics)
            return;

        if (!force && Time.time < nextDiagnosticsLogTime)
            return;

        float duration = Mathf.Max(0.001f, Time.time - diagnosticsWindowStartTime);
        float touchStartsPerMin = (diagTouchStartCount * 60f) / duration;
        float splitsPerMin = (diagStrokeFinalizeCount * 60f) / duration;

        Debug.Log($"[WhiteboardPen][Diag] dur={duration:F1}s touchStart={diagTouchStartCount} touchEnd={diagTouchEndCount} " +
                  $"dropoutHoldFrames={diagDropoutHoldFrames} trackingGraceFrames={diagTrackingGraceFrames} " +
                  $"strokeFinalize={diagStrokeFinalizeCount} recognize={diagRecognizeCount} bufferedPts={diagBufferedPointCount} " +
                  $"clear={diagClearGestureCount} starts/min={touchStartsPerMin:F1} splits/min={splitsPerMin:F1}");

        if (splitsPerMin > strokeSplitWarnPerMinute)
        {
            Debug.LogWarning($"[WhiteboardPen][Diag] High stroke split rate: {splitsPerMin:F1}/min " +
                             $"(warn>{strokeSplitWarnPerMinute:F1}). Increase hysteresis/grace or reduce jitter.");
        }

        ResetDiagnosticsWindow();
    }

    private void ResolveCameraOffsetTransform()
    {
        // Try XROrigin component first
        var xrOriginComp = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOriginComp != null && xrOriginComp.CameraFloorOffsetObject != null)
        {
            cameraOffsetTransform = xrOriginComp.CameraFloorOffsetObject.transform;
            return;
        }
        // Fallback: camera's direct parent
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null)
            cameraOffsetTransform = cam.transform.parent;
    }

    /// <summary>
    /// Converts an XRHandJoint pose position from session/tracking space to
    /// world space via the Camera Floor Offset Object. This is required because
    /// Device tracking mode applies a camera Y offset — raw joint positions must
    /// follow the same transform chain as the camera to align with world space.
    /// </summary>
    private Vector3 JointToWorld(Vector3 sessionPos)
    {
        if (cameraOffsetTransform != null)
            return cameraOffsetTransform.TransformPoint(sessionPos);
        return sessionPos;
    }

    private static bool UpdatePinchState(ref bool pinchActive,
                                         float distance,
                                         float closeThreshold,
                                         float openThreshold)
    {
        float engageThreshold = Mathf.Min(closeThreshold, openThreshold);
        float releaseThreshold = Mathf.Max(closeThreshold, openThreshold);
        pinchActive = pinchActive ? distance < releaseThreshold : distance < engageThreshold;
        return pinchActive;
    }

    private void AppendBufferedPoint(Vector3 worldPoint, long timestamp)
    {
        if (!strokeActive || currentStrokeBuffer == null)
        {
            currentStrokeBuffer = new List<BufferedInkPoint>();
            currentStrokeBuffer.Add(new BufferedInkPoint { worldPos = worldPoint, timestamp = timestamp });
            currentStrokeStartTime = Time.time;
            currentStrokeTravelDistance = 0f;
            if (enableStrokeDiagnostics) diagBufferedPointCount++;
            strokeActive = true;
            return;
        }

        Vector3 lastPoint = currentStrokeBuffer[currentStrokeBuffer.Count - 1].worldPos;
        float stepDistance = Vector3.Distance(lastPoint, worldPoint);
        if (stepDistance < minBufferedPointDistance)
            return;

        currentStrokeBuffer.Add(new BufferedInkPoint { worldPos = worldPoint, timestamp = timestamp });
        currentStrokeTravelDistance += stepDistance;
        if (enableStrokeDiagnostics) diagBufferedPointCount++;
    }

    private void FinalizeActiveStrokeIfNeeded()
    {
        if (!strokeActive) return;

        if (currentStrokeBuffer != null && currentStrokeBuffer.Count > 0)
        {
            float strokeDuration = currentStrokeStartTime > 0f
                ? Time.time - currentStrokeStartTime
                : 0f;

            bool tinyStroke = currentStrokeBuffer.Count < minPointsPerStroke
                || strokeDuration < minStrokeDurationSeconds
                || currentStrokeTravelDistance < minStrokeTravelDistance;

            bool mergedIntoPrevious = false;
            if (tinyStroke && bufferedStrokes.Count > 0)
            {
                var previousStroke = bufferedStrokes[bufferedStrokes.Count - 1];
                if (previousStroke != null && previousStroke.Count > 0)
                {
                    BufferedInkPoint previousEnd = previousStroke[previousStroke.Count - 1];
                    BufferedInkPoint currentStart = currentStrokeBuffer[0];

                    float gapSeconds = (currentStart.timestamp - previousEnd.timestamp) / 1000f;
                    float gapDistance = Vector3.Distance(previousEnd.worldPos, currentStart.worldPos);

                    if (gapSeconds >= 0f
                        && gapSeconds <= microStrokeMergeGapSeconds
                        && gapDistance <= microStrokeMergeMaxDistance)
                    {
                        previousStroke.AddRange(currentStrokeBuffer);
                        bufferedPointTotalCount += currentStrokeBuffer.Count;
                        mergedIntoPrevious = true;
                    }
                }
            }

            if (!tinyStroke)
            {
                bufferedStrokes.Add(currentStrokeBuffer);
                bufferedPointTotalCount += currentStrokeBuffer.Count;
                hasBufferedStrokes = true;
                lastStrokeBufferEndTime = Time.time;
                if (enableStrokeDiagnostics) diagStrokeFinalizeCount++;
            }
            else if (mergedIntoPrevious)
            {
                hasBufferedStrokes = true;
                lastStrokeBufferEndTime = Time.time;
            }

            currentStrokeBuffer = null;
        }

        currentStrokeStartTime = -1f;
        currentStrokeTravelDistance = 0f;
        strokeActive = false;
    }

    private void ConfirmTouchLoss(Vector3 tip, Vector3 direction, bool allowHover)
    {
        if (whiteboard != null)
            whiteboard.ToggleTouch(false);

        CurrentTouchWorldPoint = null;
        FinalizeActiveStrokeIfNeeded();

        HideTouchParticles();
        hasMadeContact = false;
        hasLastContactWorldPoint = false;
        consecutiveNoContactFrames = 0;

        if (Time.time - lastTouchTime >= smoothingIdleResetSeconds)
            hasSmoothedPosition = false;

        if (hasBufferedStrokes && !strokeActive &&
            bufferedPointTotalCount >= minBufferedPointsBeforeRecognize &&
            Time.time - lastStrokeBufferEndTime >= autoRecognizeDelay &&
            Time.time - lastRecognizeTime >= minRecognizeIntervalSeconds)
        {
            FlushAndRecognize();
        }

        if (allowHover)
        {
            DetectHover(tip, direction);
        }
        else if (hoverWhiteboard != null)
        {
            hoverWhiteboard.ToggleHover(false);
            hoverWhiteboard = null;
        }
    }

    private void HandleTrackingLoss()
    {
        bool inGraceWindow = wasTouchingLastFrame
            && hasSmoothedPosition
            && Time.time - lastTrackedTime <= trackingLossGraceSeconds;

        if (inGraceWindow)
        {
            if (enableStrokeDiagnostics) diagTrackingGraceFrames++;

            if (whiteboard != null)
            {
                whiteboard.SetTouchPosition(smoothedX, smoothedY);
                whiteboard.ToggleTouch(true);
            }

            if (hasLastContactWorldPoint)
            {
                CurrentTouchWorldPoint = lastContactWorldPoint;
                ShowTouchParticles(lastContactWorldPoint, boardNormal);
            }

            return;
        }

        pinkyPinchActive = false;
        pinkyPinchHoldTimer = 0f;
        pinkyClearTriggeredThisHold = false;

        ConfirmTouchLoss(Vector3.zero, Vector3.forward, allowHover: false);
        wasTouchingLastFrame = false;
    }

    private void TriggerBoardClear()
    {
        if (whiteboard == null) return;

        if (enableStrokeDiagnostics) diagClearGestureCount++;

        var session = journalSessionManager != null
            ? journalSessionManager
            : JournalSessionManager.Instance;
        session?.EndSession();

        if (inkBridge != null)
        {
            inkBridge.ClearInk();
            inkBridge.ClearPreContext();
        }

        bufferedStrokes.Clear();
        currentStrokeBuffer = null;
        hasBufferedStrokes = false;
        strokeActive = false;
        bufferedPointTotalCount = 0;
        currentStrokeStartTime = -1f;
        currentStrokeTravelDistance = 0f;

        hasMadeContact = false;
        hasLastContactWorldPoint = false;
        consecutiveNoContactFrames = 0;
        CurrentTouchWorldPoint = null;
        wasTouchingLastFrame = false;

        OnBoardCleared?.Invoke();
    }

    private void UpdatePinkyClearGesture(Vector3 thumbWorld, bool hasLittleTipPose, Pose littleTipPose)
    {
        if (!hasLittleTipPose)
        {
            pinkyPinchActive = false;
            pinkyPinchHoldTimer = 0f;
            pinkyClearTriggeredThisHold = false;
            return;
        }

        Vector3 littleWorld = JointToWorld(littleTipPose.position);
        bool pinkyPinching = UpdatePinchState(
            ref pinkyPinchActive,
            Vector3.Distance(thumbWorld, littleWorld),
            pinkyPinchCloseThreshold,
            pinkyPinchOpenThreshold);

        if (pinkyPinching)
        {
            pinkyPinchHoldTimer += Time.deltaTime;
            if (!pinkyClearTriggeredThisHold && pinkyPinchHoldTimer >= pinkyPinchHoldSeconds)
            {
                pinkyClearTriggeredThisHold = true;
                TriggerBoardClear();
            }
        }
        else
        {
            pinkyPinchHoldTimer = 0f;
            pinkyClearTriggeredThisHold = false;
        }
    }

    private void Update()
    {
        // Journal scenes stay locked to the Journaling state. Non-journal scenes
        // can opt in via allowWithoutJournalSession.
        var session = journalSessionManager != null
            ? journalSessionManager
            : JournalSessionManager.Instance;

        bool sessionBlocksInput = session != null &&
            session.CurrentState != JournalSessionManager.SessionState.Journaling;
        bool missingSessionBlocksInput = session == null && !allowWithoutJournalSession;

        if (sessionBlocksInput || missingSessionBlocksInput)
        {
            if (whiteboard != null) whiteboard.ToggleTouch(false);
            if (hoverWhiteboard != null) { hoverWhiteboard.ToggleHover(false); hoverWhiteboard = null; }
            wasTouchingLastFrame = false;
            hasMadeContact = false;
            pinkyPinchActive = false;
            pinkyPinchHoldTimer = 0f;
            pinkyClearTriggeredThisHold = false;
            consecutiveNoContactFrames = 0;
            CurrentTouchWorldPoint = null;
            return;
        }

        // Only the right hand can draw on the whiteboard.
        if (handedness == Handedness.Left) return;

        // Acquire XR Hand subsystem
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand hand = handedness == Handedness.Left
            ? handSubsystem.leftHand
            : handSubsystem.rightHand;

        if (!hand.isTracked)
        {
            HandleTrackingLoss();
            return;
        }
        lastTrackedTime = Time.time;

        // Required joints
        XRHandJoint indexTipJoint = hand.GetJoint(XRHandJointID.IndexTip);
        XRHandJoint indexDistalJoint = hand.GetJoint(XRHandJointID.IndexDistal);
        XRHandJoint indexIntermediateJoint = hand.GetJoint(XRHandJointID.IndexIntermediate);
        XRHandJoint thumbTipJoint = hand.GetJoint(XRHandJointID.ThumbTip);
        XRHandJoint littleTipJoint = hand.GetJoint(XRHandJointID.LittleTip);

        if (!indexTipJoint.TryGetPose(out Pose indexTipPose)) { HandleTrackingLoss(); return; }
        if (!indexDistalJoint.TryGetPose(out Pose indexDistalPose)) { HandleTrackingLoss(); return; }
        if (!thumbTipJoint.TryGetPose(out Pose thumbTipPose)) { HandleTrackingLoss(); return; }
        bool hasLittleTipPose = littleTipJoint.TryGetPose(out Pose littleTipPose);

        // Convert joint positions from session/tracking space to world space.
        // Raw XRHandJoint poses are in session space; the camera is offset by
        // CameraYOffset via the Camera Floor Offset Object, so we must use the
        // same transform chain or raycasts will miss by ~2m vertically.
        Vector3 origin = JointToWorld(indexDistalPose.position);
        Vector3 tip    = JointToWorld(indexTipPose.position);
        Vector3 direction = Vector3.Normalize(tip - origin);
        float fingerLength = Vector3.Distance(origin, tip);
        Vector3 thumbWorld = JointToWorld(thumbTipPose.position);

        // ── STYLUS MODE OVERRIDE ─────────────────────────────────────
        // When a calibrated StylusTipProvider is assigned, replace the
        // finger-tip-based origin/tip/direction with the physical pen tip.
        // The existing raycast-based contact detection, tolerance, and
        // stroke-buffering logic below works unchanged — it just uses a
        // pen-derived tip position instead of the index finger.
        // Gesture reads (thumbWorld, littleTipPose) remain finger-based
        // so pinky-clear still works while holding a pen.
        if (stylusTipProvider != null
            && stylusTipProvider.IsCalibrated
            && stylusTipProvider.TipWorldPosition.HasValue)
        {
            tip = stylusTipProvider.TipWorldPosition.Value;
            // Ray points downward from slightly above the tip — the writing
            // plane is horizontal (table), so this ray reliably intersects
            // the whiteboard collider. fingerLength determines the effective
            // raycast range (kept small to avoid false hits from far away).
            origin = tip + Vector3.up * 0.025f;
            direction = Vector3.down;
            fingerLength = 0.03f;
        }

        bool hitBoard = false;

        // ===============================================================
        // 1️⃣ REAL CONTACT CHECK (NO tolerance)
        //    The ray spans from the distal joint to the tip.  It only
        //    hits when the board is physically between the two joints,
        //    meaning the tip has reached / begun to pass through.
        // ===============================================================
        if (Physics.Raycast(origin, direction, out touch, fingerLength, 1 << WHITEBOARD_LAYER))
        {
            hitBoard = true;
            hasMadeContact = true;
            boardNormal = touch.normal;   // points toward the approach side
            boardPoint  = touch.point;
        }
        // ===============================================================
        // 2️⃣ PASSTHROUGH TOLERANCE
        //    Only activated when:
        //      a) real contact has occurred before, AND
        //      b) the index tip is on the FAR side of the board
        //         (dot product with stored normal < 0).
        //    This prevents ghost-touches when hovering in front.
        // ===============================================================
        else if (hasMadeContact)
        {
            float tipSide = Vector3.Dot(tip - boardPoint, boardNormal);

            if (tipSide < 0f)   // tip is behind the board → passed through
            {
                float passthroughDistance = fingerLength + touchTolerance;

                if (Physics.Raycast(origin, direction, out touch, passthroughDistance, 1 << WHITEBOARD_LAYER))
                {
                    hitBoard   = true;
                    boardPoint = touch.point;   // keep reference current
                }
                // Fallback ray from intermediate joint
                else if (indexIntermediateJoint.TryGetPose(out Pose intermediatePose))
                {
                    Vector3 intOrigin = JointToWorld(intermediatePose.position);
                    Vector3 intDir    = Vector3.Normalize(tip - intOrigin);
                    float   intDist   = Vector3.Distance(intOrigin, tip) + touchTolerance;

                    if (Physics.Raycast(intOrigin, intDir, out touch, intDist, 1 << WHITEBOARD_LAYER))
                    {
                        hitBoard   = true;
                        boardPoint = touch.point;
                    }
                }
            }
            // tipSide >= 0 → finger is in front of the board → require
            // real contact again (no tolerance applied).
        }

        // ===============================================================
        // HANDWRITING AREA BOUNDS CHECK
        // Reject touches that land on the whiteboard but outside the
        // HandwritingArea RectTransform (e.g. header, footer, margins).
        // ===============================================================
        if (hitBoard && !IsInsideHandwritingArea(touch.point))
            hitBoard = false;

        // ===============================================================
        // APPLY RESULT
        // ===============================================================
        bool keepTouchThisFrame = false;
        bool previousTouching = wasTouchingLastFrame;

        if (hitBoard)
        {
            consecutiveNoContactFrames = 0;
            whiteboard = touch.collider.GetComponent<Whiteboard>();
            if (whiteboard != null)
            {
                // Clear any hover state since we're now touching
                if (hoverWhiteboard != null)
                {
                    hoverWhiteboard.ToggleHover(false);
                    hoverWhiteboard = null;
                }

                float rawX = touch.textureCoord.x;
                float rawY = touch.textureCoord.y;

                // Smoothing via One Euro Filter
                bool shouldReinitializeFilter = !hasSmoothedPosition
                    || Time.time - lastTouchTime > smoothingIdleResetSeconds;

                if (shouldReinitializeFilter)
                {
                    filterX = new OneEuroFilter(filterMinCutoff, filterBeta, filterDCutoff);
                    filterY = new OneEuroFilter(filterMinCutoff, filterBeta, filterDCutoff);
                    hasSmoothedPosition = true;
                }

                float t = Time.time;
                smoothedX = filterX.Filter(rawX, t);
                smoothedY = filterY.Filter(rawY, t);
                lastTouchTime = t;

                whiteboard.SetTouchPosition(smoothedX, smoothedY);
                whiteboard.ToggleTouch(true);

                CurrentTouchWorldPoint = touch.point;
                lastContactWorldPoint = touch.point;
                hasLastContactWorldPoint = true;

                // ── Buffer stroke for deferred PCA-based recognition ─────
                {
                    Vector3 worldPoint = touch.point;
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    AppendBufferedPoint(worldPoint, ts);
                }
            }

            ShowTouchParticles(touch.point, touch.normal);
        }
        else
        {
            consecutiveNoContactFrames++;

            if (wasTouchingLastFrame && consecutiveNoContactFrames < contactLossFramesToEndStroke)
            {
                keepTouchThisFrame = true;
                if (enableStrokeDiagnostics) diagDropoutHoldFrames++;

                if (whiteboard != null)
                {
                    whiteboard.SetTouchPosition(smoothedX, smoothedY);
                    whiteboard.ToggleTouch(true);
                }

                if (hasLastContactWorldPoint)
                {
                    CurrentTouchWorldPoint = lastContactWorldPoint;
                    ShowTouchParticles(lastContactWorldPoint, boardNormal);
                }
            }
            else
            {
                ConfirmTouchLoss(tip, direction, allowHover: true);
            }
        }

        bool effectiveTouching = hitBoard || keepTouchThisFrame;

        if (enableStrokeDiagnostics)
        {
            if (!previousTouching && effectiveTouching) diagTouchStartCount++;
            if (previousTouching && !effectiveTouching) diagTouchEndCount++;
        }

        wasTouchingLastFrame = effectiveTouching;

        // Pinky pinch-hold → clear board (debounced + hysteresis)
        UpdatePinkyClearGesture(thumbWorld, hasLittleTipPose, littleTipPose);
    }

    // ==================================================================
    // HOVER DETECTION
    // ==================================================================

    /// <summary>
    /// Cast a ray from the index fingertip forward to detect whether
    /// the user is pointing at a whiteboard.  If so, show a hover cursor.
    /// </summary>
    private void DetectHover(Vector3 tip, Vector3 direction)
    {
        if (Physics.Raycast(tip, direction, out RaycastHit hoverHit,
                            hoverDistance, 1 << WHITEBOARD_LAYER))
        {
            Whiteboard wb = hoverHit.collider.GetComponent<Whiteboard>();
            if (wb != null && IsInsideHandwritingArea(hoverHit.point))
            {
                // Switched to a different board → clear old hover
                if (hoverWhiteboard != null && hoverWhiteboard != wb)
                {
                    hoverWhiteboard.ToggleHover(false);
                }

                wb.SetHoverPosition(hoverHit.textureCoord.x, hoverHit.textureCoord.y);
                wb.ToggleHover(true);
                hoverWhiteboard = wb;
                return;
            }
        }

        // No board hit or outside HandwritingArea → clear hover
        if (hoverWhiteboard != null)
        {
            hoverWhiteboard.ToggleHover(false);
            hoverWhiteboard = null;
        }
    }

    // ==================================================================
    // PARTICLES
    // ==================================================================

    private void CreateTouchParticles()
    {
        GameObject obj = new GameObject($"TouchParticles_{handedness}");
        obj.transform.SetParent(transform);

        touchParticles = obj.AddComponent<ParticleSystem>();

        var main = touchParticles.main;
        main.startLifetime = 0.35f;
        main.startSpeed = 0.04f;
        main.startSize = 0.003f;
        main.maxParticles = 60;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.loop = true;

        var emission = touchParticles.emission;
        emission.rateOverTime = 25f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 15) });

        var shape = touchParticles.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.004f;

        var sol = touchParticles.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, 0f);

        var col = touchParticles.colorOverLifetime;
        col.enabled = true;

        Gradient grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        col.color = grad;

        // ── Configure renderer with a URP-compatible material ──────────
        var pRenderer = obj.GetComponent<ParticleSystemRenderer>();
        pRenderer.renderMode = ParticleSystemRenderMode.Billboard;

        if (particleMaterial != null)
        {
            Material particleMat = new Material(particleMaterial);
            particleMat.SetColor("_BaseColor", Color.white);

            // Transparent + Alpha blend
            particleMat.SetFloat("_Surface", 1f);
            particleMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            particleMat.SetFloat("_Blend", 0f);
            particleMat.SetFloat("_AlphaClip", 0f);
            particleMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            particleMat.SetOverrideTag("RenderType", "Transparent");
            particleMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            particleMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            particleMat.SetFloat("_ZWrite", 0f);

            pRenderer.material = particleMat;
        }
        else
        {
            Debug.LogWarning("WhiteboardPen: URP Particles/Unlit shader not found. " +
                             "Ensure Universal RP is installed.");
        }

        touchParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void ShowTouchParticles(Vector3 point, Vector3 normal)
    {
        Transform t = touchParticles.transform;
        t.position = point;
        t.rotation = Quaternion.LookRotation(normal);

        if (!wasTouchingLastFrame)
        {
            touchParticles.Clear();
            touchParticles.Play();
        }
    }

    private void HideTouchParticles()
    {
        if (touchParticles != null && touchParticles.isPlaying)
        {
            touchParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    // ==================================================================
    // PCA-BASED RECOGNITION FLUSH
    // ==================================================================

    /// <summary>
    /// Compute PCA on all buffered strokes to find the writing direction,
    /// rotate coordinates so ML Kit sees left-to-right text, send all
    /// strokes to the bridge, and trigger recognition.
    /// </summary>
    private void FlushAndRecognize()
    {
        if (inkBridge == null) inkBridge = DigitalInkBridge.Instance;
        if (inkBridge == null || !inkBridge.IsModelReady)
        {
            bufferedStrokes.Clear();
            hasBufferedStrokes = false;
            bufferedPointTotalCount = 0;
            return;
        }

        // Wait if a previous recognition is still in progress
        if (inkBridge.IsRecognizing) return;

        // Collect all world-space points
        var allPoints = new List<Vector3>();
        foreach (var stroke in bufferedStrokes)
            foreach (var pt in stroke)
                allPoints.Add(pt.worldPos);

        if (allPoints.Count < 2)
        {
            bufferedStrokes.Clear();
            hasBufferedStrokes = false;
            bufferedPointTotalCount = 0;
            return;
        }

        // Compute writing direction via PCA on the XZ plane
        ComputeWritingDirection(allPoints, out Vector3 right, out Vector3 forward);

        // Use centroid as projection origin
        Vector3 centroid = Vector3.zero;
        foreach (var p in allPoints) centroid += p;
        centroid /= allPoints.Count;

        // Clear any leftover ink and send rotated strokes
        inkBridge.ClearInk();
        inkBridge.SetWritingArea(MLKIT_AREA_W, MLKIT_AREA_H);

        foreach (var stroke in bufferedStrokes)
        {
            if (stroke.Count == 0) continue;

            for (int i = 0; i < stroke.Count; i++)
            {
                Vector3 delta = stroke[i].worldPos - centroid;
                float pxX = MLKIT_AREA_W * 0.5f + Vector3.Dot(delta, right)   * MLKIT_PX_PER_M;
                float pxY = MLKIT_AREA_H * 0.5f - Vector3.Dot(delta, forward) * MLKIT_PX_PER_M;
                pxX = Mathf.Clamp(pxX, 0f, MLKIT_AREA_W);
                pxY = Mathf.Clamp(pxY, 0f, MLKIT_AREA_H);

                if (i == 0)
                    inkBridge.BeginStroke(pxX, pxY, stroke[i].timestamp);
                else
                    inkBridge.AddPoint(pxX, pxY, stroke[i].timestamp);
            }
            inkBridge.EndStroke();
        }

        inkBridge.Recognize();
        lastRecognizeTime = Time.time;
        if (enableStrokeDiagnostics) diagRecognizeCount++;

        // Notify ScribbleManager with stroke metadata before clearing
        if (OnStrokesFlushed != null)
        {
            Bounds strokeBounds = new Bounds(centroid, Vector3.zero);
            foreach (var p in allPoints) strokeBounds.Encapsulate(p);

            OnStrokesFlushed.Invoke(new StrokeMetadata
            {
                center  = centroid,
                bounds  = strokeBounds,
                right   = right,
                forward = forward
            });
        }

        bufferedStrokes.Clear();
        hasBufferedStrokes = false;
        bufferedPointTotalCount = 0;
    }

    /// <summary>
    /// Determines the writing direction from world-space points using PCA
    /// on the XZ plane (horizontal table surface).
    /// Returns 'right' (writing direction) and 'forward' (perpendicular,
    /// pointing away from user — maps to ML Kit's upward / -Y direction).
    /// </summary>
    private void ComputeWritingDirection(List<Vector3> points, out Vector3 right, out Vector3 forward)
    {
        // ── Project to XZ plane and compute 2D covariance ────────
        float meanX = 0f, meanZ = 0f;
        foreach (var p in points) { meanX += p.x; meanZ += p.z; }
        meanX /= points.Count;
        meanZ /= points.Count;

        float covXX = 0f, covXZ = 0f, covZZ = 0f;
        foreach (var p in points)
        {
            float dx = p.x - meanX;
            float dz = p.z - meanZ;
            covXX += dx * dx;
            covXZ += dx * dz;
            covZZ += dz * dz;
        }

        // ── Principal eigenvector of 2×2 covariance matrix ───────
        float theta = 0.5f * Mathf.Atan2(2f * covXZ, covXX - covZZ);
        right   = new Vector3(Mathf.Cos(theta), 0f, Mathf.Sin(theta));
        forward = new Vector3(-right.z, 0f, right.x);

        // ── Disambiguate 'right' using temporal flow ─────────────
        // For left-to-right writing the last point should project
        // farther along the 'right' axis than the first point.
        if (Vector3.Dot(points[points.Count - 1] - points[0], right) < 0f)
            right = -right;

        // ── Disambiguate 'forward' using camera direction ────────
        // 'forward' should point away from the user so that
        // "up on paper" maps to ML Kit's negative-Y (top of canvas).
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude > 0.001f)
            {
                camFwd.Normalize();
                if (Vector3.Dot(forward, camFwd) < 0f)
                    forward = -forward;
            }
        }
    }

    // ==================================================================
    // SCRIBBLE API
    // ==================================================================

    /// <summary>Clear buffered strokes without triggering recognition (used by ScribbleManager after scratch-to-delete).</summary>
    public void ClearStrokeBuffer()
    {
        bufferedStrokes.Clear();
        currentStrokeBuffer = null;
        hasBufferedStrokes = false;
        strokeActive = false;
        bufferedPointTotalCount = 0;
        currentStrokeStartTime = -1f;
        currentStrokeTravelDistance = 0f;
        if (inkBridge != null) inkBridge.ClearInk();
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    /// <summary>
    /// Returns true if <paramref name="worldPoint"/> falls within the
    /// HandwritingArea RectTransform bounds. If no HandwritingArea is
    /// assigned, returns true (no constraint).
    /// </summary>
    private bool IsInsideHandwritingArea(Vector3 worldPoint)
    {
        if (loginHandwritingExclusionArea != null)
        {
            Vector3 localEx = loginHandwritingExclusionArea.InverseTransformPoint(worldPoint);
            Rect rEx = loginHandwritingExclusionArea.rect;
            if (localEx.x >= rEx.xMin && localEx.x <= rEx.xMax
             && localEx.y >= rEx.yMin && localEx.y <= rEx.yMax)
                return false;
        }

        var pm = WhiteboardPageManager.Instance;
        if (pm == null || pm.handwritingArea == null) return true;

        RectTransform ha = pm.handwritingArea;
        Vector3 local = ha.InverseTransformPoint(worldPoint);
        Rect r = ha.rect;
        return local.x >= r.xMin && local.x <= r.xMax
            && local.y >= r.yMin && local.y <= r.yMax;
    }

    public static XRHandSubsystem GetHandSubsystem()
    {
        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var s in subsystems)
        {
            if (s.running) return s;
        }
        return null;
    }
}