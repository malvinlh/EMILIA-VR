using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Draws on a whiteboard using XR Hand Tracking (index finger tip).
/// Touch tolerance is ONLY applied after real contact has occurred
/// (i.e. when the finger has begun to pass through the board).
/// </summary>
public class WhiteboardPen : MonoBehaviour
{
    public JournalSessionManager journalSessionManager;
    
    [Tooltip("Which hand drives this pen.")]
    public Handedness handedness = Handedness.Right;

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
    private const float PINCH_THRESHOLD = 0.02f;

    // ── Pinky-pinch clear (edge detection) ────────────────────────────
    private bool pinkyPinchActive;

    // Extra depth allowed AFTER contact (meters)
    [Tooltip("Extra depth allowed after the finger has contacted the board.")]
    public float touchTolerance = 0.015f;

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
    // tracking mode with CameraYOffset=2, so joint positions must go through
    // the same transform chain as the camera (not the XR Origin root).
    private Transform cameraOffsetTransform;

    /// <summary>True when the pen is actively drawing on a whiteboard.</summary>
    public bool IsDrawing => wasTouchingLastFrame;

    private void Start()
    {
        CreateTouchParticles();
        inkBridge = DigitalInkBridge.Instance;
        ResolveCameraOffsetTransform();
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
    /// the scene uses Device tracking mode with CameraYOffset=2 — raw joint
    /// positions are 2m below where they should appear in world space.
    /// </summary>
    private Vector3 JointToWorld(Vector3 sessionPos)
    {
        if (cameraOffsetTransform != null)
            return cameraOffsetTransform.TransformPoint(sessionPos);
        return sessionPos;
    }

    private void Update()
    {
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

        if (!hand.isTracked) return;

        // Required joints
        XRHandJoint indexTipJoint = hand.GetJoint(XRHandJointID.IndexTip);
        XRHandJoint indexDistalJoint = hand.GetJoint(XRHandJointID.IndexDistal);
        XRHandJoint indexIntermediateJoint = hand.GetJoint(XRHandJointID.IndexIntermediate);
        XRHandJoint thumbTipJoint = hand.GetJoint(XRHandJointID.ThumbTip);
        XRHandJoint littleTipJoint = hand.GetJoint(XRHandJointID.LittleTip);

        if (!indexTipJoint.TryGetPose(out Pose indexTipPose)) return;
        if (!indexDistalJoint.TryGetPose(out Pose indexDistalPose)) return;
        if (!thumbTipJoint.TryGetPose(out Pose thumbTipPose)) return;

        // Convert joint positions from session/tracking space to world space.
        // Raw XRHandJoint poses are in session space; the camera is offset by
        // CameraYOffset via the Camera Floor Offset Object, so we must use the
        // same transform chain or raycasts will miss by ~2m vertically.
        Vector3 origin = JointToWorld(indexDistalPose.position);
        Vector3 tip    = JointToWorld(indexTipPose.position);
        Vector3 direction = Vector3.Normalize(tip - origin);
        float fingerLength = Vector3.Distance(origin, tip);

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
        // APPLY RESULT
        // ===============================================================
        if (hitBoard)
        {
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
                if (!hasSmoothedPosition)
                {
                    filterX = new OneEuroFilter(filterMinCutoff, filterBeta, filterDCutoff);
                    filterY = new OneEuroFilter(filterMinCutoff, filterBeta, filterDCutoff);
                    hasSmoothedPosition = true;

                    // Pre-seed filters with initial position
                    float t = Time.time;
                    smoothedX = filterX.Filter(rawX, t);
                    smoothedY = filterY.Filter(rawY, t);
                }
                else
                {
                    float t = Time.time;
                    smoothedX = filterX.Filter(rawX, t);
                    smoothedY = filterY.Filter(rawY, t);
                }

                whiteboard.SetTouchPosition(smoothedX, smoothedY);
                whiteboard.ToggleTouch(true);

                CurrentTouchWorldPoint = touch.point;

                // ── Buffer stroke for deferred PCA-based recognition ─────
                {
                    Vector3 worldPoint = touch.point;
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (!wasTouchingLastFrame)
                    {
                        currentStrokeBuffer = new List<BufferedInkPoint>();
                        currentStrokeBuffer.Add(new BufferedInkPoint { worldPos = worldPoint, timestamp = ts });
                        strokeActive = true;
                    }
                    else if (currentStrokeBuffer != null)
                    {
                        currentStrokeBuffer.Add(new BufferedInkPoint { worldPos = worldPoint, timestamp = ts });
                    }
                }
            }

            ShowTouchParticles(touch.point, touch.normal);
        }
        else
        {
            if (whiteboard != null)
            {
                whiteboard.ToggleTouch(false);
            }

            CurrentTouchWorldPoint = null;

            // End active stroke — finalize in buffer
            if (strokeActive)
            {
                if (currentStrokeBuffer != null && currentStrokeBuffer.Count > 0)
                {
                    bufferedStrokes.Add(currentStrokeBuffer);
                    currentStrokeBuffer = null;
                    hasBufferedStrokes = true;
                    lastStrokeBufferEndTime = Time.time;
                }
                strokeActive = false;
            }

            HideTouchParticles();
            hasMadeContact = false; // reset contact state
            hasSmoothedPosition = false; // reset smoothing for next stroke

            // Auto-recognize after idle delay
            if (hasBufferedStrokes && !strokeActive &&
                Time.time - lastStrokeBufferEndTime >= autoRecognizeDelay)
            {
                FlushAndRecognize();
            }

            // ── Hover detection (pointer cursor, right hand only) ──────
            DetectHover(tip, direction);
        }

        wasTouchingLastFrame = hitBoard;

        // Pinky pinch → clear board (edge-detected: fires once per pinch)
        if (littleTipJoint.TryGetPose(out Pose littleTipPose))
        {
            bool pinkyPinching = IsPinching(thumbTipPose.position, littleTipPose.position);

            if (pinkyPinching && !pinkyPinchActive)
            {
                pinkyPinchActive = true;

                if (whiteboard != null)
                {
                    // whiteboard.Initialize();
                    journalSessionManager?.EndSession();

                    // Also clear accumulated ink, recognised text, and pre-context
                    if (inkBridge != null)
                    {
                        inkBridge.ClearInk();
                        inkBridge.ClearPreContext();
                    }

                    bufferedStrokes.Clear();
                    currentStrokeBuffer = null;
                    hasBufferedStrokes = false;
                    strokeActive = false;

                    OnBoardCleared?.Invoke();

                    var textDisplay = whiteboard.GetComponent<RecognizedTextDisplay>();
                    if (textDisplay != null) textDisplay.ClearText();
                }
            }
            else if (!pinkyPinching)
            {
                pinkyPinchActive = false;
            }
        }
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
            if (wb != null)
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

        // No board hit → clear hover
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
        if (inkBridge != null) inkBridge.ClearInk();
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    public static bool IsPinching(Vector3 a, Vector3 b)
    {
        return Vector3.Distance(a, b) < PINCH_THRESHOLD;
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