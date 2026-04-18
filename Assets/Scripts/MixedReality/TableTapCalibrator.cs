using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Interactive table calibration via 4-tap rectangle. The already-calibrated
/// DIY stylus is used to physically tap the four corners of the writing area.
/// Surface Y, yaw, center, and size are all derived from the taps themselves —
/// no AR plane detection, no palm-flat gesture, no height-bias fudge factors.
///
/// Why this replaces the old ARPlaneManager + palm-flat flow:
///   • ARPlaneManager is slow (3–10 s indoors) and unreliable on Quest 3
///     without MRUK, which we cannot install.
///   • Palm-flat detection reports a Y that is 12–30 mm above the surface
///     because the palm joint sits above the hand, requiring height-bias
///     fields that drift between users.
///   • A tap places the pen tip AT the surface, so the Y we record is the
///     actual contact Y. Four taps also let us fit yaw and flag unevenness.
///
/// Tap trigger: the stylus tip must be (a) stationary (velocity below
/// <see cref="tapStillnessVelocity"/>) for <see cref="tapDwellSeconds"/>, and
/// (b) at a Y consistent with prior taps (within ±<see cref="tapYConsensus"/>)
/// once at least one tap has been recorded. A brief cooldown after each
/// capture prevents the same contact registering twice.
///
/// After all 4 taps land, a Confirm and Redo button float above the rectangle
/// preview. Either hand's index tip can poke them.
/// </summary>
public class TableTapCalibrator : MonoBehaviour
{
    // ================================================================
    // INSPECTOR
    // ================================================================

    [Header("References")]
    public StylusTipProvider stylusTipProvider;
    public PassthroughManager passthroughManager;

    [Header("Tap Detection")]
    [Tooltip("Stylus tip linear speed (m/s) below which stillness counts toward the dwell timer.")]
    public float tapStillnessVelocity = 0.03f;
    [Tooltip("How long (seconds) the tip must remain stationary to register a tap.")]
    public float tapDwellSeconds = 0.22f;
    [Tooltip("Cooldown (seconds) after a tap before another can register.")]
    public float tapCooldownSeconds = 0.55f;
    [Tooltip("Vertical tolerance (metres) between consecutive taps. A tap farther than this from the running mean Y is rejected (likely mid-air dwell).")]
    public float tapYConsensus = 0.025f;

    [Header("Rectangle Constraints")]
    [Tooltip("Minimum writing rectangle size in metres (width, depth).")]
    public Vector2 minSize = new Vector2(0.30f, 0.20f);
    [Tooltip("Maximum writing rectangle size in metres (width, depth).")]
    public Vector2 maxSize = new Vector2(1.20f, 0.80f);
    [Tooltip("If the tap Y values span more than this (metres), a warning is shown — the user likely tapped on an object.")]
    public float unevenWarnSpan = 0.015f;

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
    [Tooltip("Radius of an X-marker dropped at each confirmed tap (metres).")]
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
    /// Shape-compatible with the legacy ARTableDetector.DetectedTable so the
    /// JournalSessionManager downstream code can consume it unchanged.
    /// avgTapSurfaceY replaces avgPalmSurfaceY (semantic rename: direct tap Y,
    /// no palm-thickness offset needed).
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

    private enum Phase { Inactive, Tapping, AwaitingConfirm }

    // ================================================================
    // STATE
    // ================================================================

    private Phase phase = Phase.Inactive;
    private readonly List<Vector3> taps = new List<Vector3>(4);
    private const int RequiredTaps = 4;
    private static readonly string[] TapLabels =
    {
        "near-left", "near-right", "far-right", "far-left"
    };

    private float dwellAccum;
    private float lastTapTime = -999f;
    private bool hasLastTip;
    private Vector3 lastTipPos;
    private float lastTipTime;

    private float awaitConfirmEnterTime;

    // Eye-Y accumulator (averaged across the whole session for comfort calibration).
    private float eyeYAccum;
    private int eyeYSamples;

    // ================================================================
    // VISUAL ELEMENTS
    // ================================================================

    private int passthroughLayer = 31;

    private GameObject tipIndicator;
    private Material tipIndicatorMat;

    private readonly List<GameObject> tapMarkers = new List<GameObject>(4);
    private Material tapMarkerMat;

    private LineRenderer previewRectangle;
    private GameObject previewRectangleObj;

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

    private static readonly Color ColorTipIdle = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTipDwelling = new Color(1.00f, 0.95f, 0.25f, 0.95f);
    private static readonly Color ColorTapMarker = new Color(0.30f, 0.85f, 1.00f, 0.95f);
    private static readonly Color ColorPreview = new Color(0.30f, 0.85f, 1.00f, 0.85f);
    private static readonly Color ColorPreviewUneven = new Color(1.00f, 0.55f, 0.20f, 0.90f);
    private static readonly Color ColorConfirm = new Color(0.20f, 0.80f, 0.30f, 0.95f);
    private static readonly Color ColorRedo = new Color(0.85f, 0.40f, 0.40f, 0.95f);
    private static readonly Color ColorBtnDisabled = new Color(0.40f, 0.40f, 0.45f, 0.60f);

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void BeginCalibration()
    {
        if (passthroughManager != null)
            passthroughLayer = passthroughManager.GetPassthroughUILayer();

        EnsureVisuals();

        phase = Phase.Tapping;
        taps.Clear();
        dwellAccum = 0f;
        lastTapTime = -999f;
        hasLastTip = false;
        eyeYAccum = 0f;
        eyeYSamples = 0;

        ClearTapMarkers();
        HidePreviewRectangle();
        if (confirmButton != null) confirmButton.SetActive(false);
        if (redoButton != null)    redoButton.SetActive(false);

        SetInstructionForTap();
        if (logEvents) Debug.Log("[TableTapCalibrator] BeginCalibration — waiting for 4 taps.");
    }

    public void Cleanup()
    {
        phase = Phase.Inactive;
        if (tipIndicator     != null) tipIndicator.SetActive(false);
        if (confirmButton    != null) confirmButton.SetActive(false);
        if (redoButton       != null) redoButton.SetActive(false);
        if (instructionObj   != null) instructionObj.SetActive(false);
        if (previewRectangleObj != null) previewRectangleObj.SetActive(false);
        foreach (var m in tapMarkers) if (m != null) m.SetActive(false);
    }

    public void ResetState() => BeginCalibration();

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (phase == Phase.Inactive) return;

        // Sample eye Y continuously for calibration (small cost).
        Camera cam = Camera.main;
        if (cam != null)
        {
            eyeYAccum += cam.transform.position.y;
            eyeYSamples++;
        }

        if (phase == Phase.Tapping)
            UpdateTappingPhase();
        else if (phase == Phase.AwaitingConfirm)
            UpdateAwaitingConfirm();
    }

    private void LateUpdate()
    {
        if (phase == Phase.Inactive) return;
        BillboardInstruction();
    }

    // ================================================================
    // TAPPING PHASE
    // ================================================================

    private void UpdateTappingPhase()
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
        bool yConsistent = true;
        if (taps.Count > 0)
        {
            float meanY = RunningMeanY();
            yConsistent = Mathf.Abs(tipPos.y - meanY) <= tapYConsensus;
        }

        if (!onCooldown && linearSpeed < tapStillnessVelocity && yConsistent)
        {
            dwellAccum += Time.deltaTime;
            SetTipColor(ColorTipDwelling);
            if (dwellAccum >= tapDwellSeconds)
                CaptureTap(tipPos);
        }
        else
        {
            dwellAccum = 0f;
            SetTipColor(ColorTipIdle);
        }
    }

    private float RunningMeanY()
    {
        float sum = 0f;
        for (int i = 0; i < taps.Count; i++) sum += taps[i].y;
        return sum / taps.Count;
    }

    private void CaptureTap(Vector3 tipPos)
    {
        int idx = taps.Count;
        taps.Add(tipPos);
        lastTapTime = Time.time;
        dwellAccum = 0f;

        // Drop a marker
        EnsureTapMarker(idx).transform.position = tipPos;
        tapMarkers[idx].SetActive(true);

        if (logEvents) Debug.Log($"[TableTapCalibrator] Tap {idx + 1}/{RequiredTaps} at {tipPos}.");

        UpdatePreviewRectangle();

        if (taps.Count >= RequiredTaps)
        {
            phase = Phase.AwaitingConfirm;
            awaitConfirmEnterTime = Time.time;
            ShowConfirmAndRedo();
            SetInstructionForConfirm();
        }
        else
        {
            SetInstructionForTap();
        }
    }

    // ================================================================
    // CONFIRM PHASE
    // ================================================================

    private void UpdateAwaitingConfirm()
    {
        // Keep preview rectangle updated each frame in case visuals drift.
        UpdatePreviewRectangle();

        bool armed = Time.time - awaitConfirmEnterTime >= buttonArmDelay;
        SetButtonColor(confirmButtonMat, armed ? ColorConfirm : ColorBtnDisabled);
        SetButtonColor(redoButtonMat, armed ? ColorRedo : ColorBtnDisabled);
        if (!armed) return;

        if (handSubsystem == null || !handSubsystem.running)
            handSubsystem = WhiteboardPen.GetHandSubsystem();
        if (handSubsystem == null) return;

        Vector3 leftTip = GetIndexTipOr(Handedness.Left);
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
            Finalize();
        }
    }

    private Vector3 GetIndexTipOr(Handedness hand)
    {
        XRHand xrHand = hand == Handedness.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!xrHand.isTracked) return new Vector3(1e6f, 1e6f, 1e6f);

        XRHandJoint joint = xrHand.GetJoint(XRHandJointID.IndexTip);
        if (!joint.TryGetPose(out Pose pose)) return new Vector3(1e6f, 1e6f, 1e6f);

        // Convert session → world via the camera offset transform.
        var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        Transform offset = (origin != null && origin.CameraFloorOffsetObject != null)
            ? origin.CameraFloorOffsetObject.transform
            : (Camera.main != null ? Camera.main.transform.parent : null);
        return offset != null ? offset.TransformPoint(pose.position) : pose.position;
    }

    private void Finalize()
    {
        DetectedTable result = BuildDetectedTable();
        phase = Phase.Inactive;

        if (logEvents)
            Debug.Log($"[TableTapCalibrator] Confirmed: pos={result.position}, " +
                      $"size={result.size}, yawDeg={result.rotation.eulerAngles.y:F1}, " +
                      $"surfaceY={result.avgTapSurfaceY:F3}m, eyeY={result.avgEyeY:F3}m");

        OnTableConfirmed?.Invoke(result);
    }

    private DetectedTable BuildDetectedTable()
    {
        // tap 0 = near-left, 1 = near-right, 2 = far-right, 3 = far-left
        Vector3 tap0 = taps[0], tap1 = taps[1], tap2 = taps[2], tap3 = taps[3];

        // Surface Y = mean of 4 tap Ys (robust to one tap on an object)
        float surfaceY = (tap0.y + tap1.y + tap2.y + tap3.y) * 0.25f;

        // Center = centroid projected to surface Y
        Vector3 centroidXZ = (tap0 + tap1 + tap2 + tap3) * 0.25f;
        Vector3 center = new Vector3(centroidXZ.x, surfaceY, centroidXZ.z);

        // Forward direction = from midpoint(near edge) to midpoint(far edge)
        Vector3 midNear = 0.5f * (tap0 + tap1);
        Vector3 midFar  = 0.5f * (tap2 + tap3);
        Vector3 forward = midFar - midNear;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
        {
            // Degenerate: fall back to "facing the user"
            Camera cam = Camera.main;
            forward = cam != null ? (cam.transform.position - center) : Vector3.forward;
            forward.y = 0f;
        }
        forward.Normalize();
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

        // Size: width from near edge length, depth from side edge length.
        float width = HorizontalDistance(tap0, tap1);
        float widthFar = HorizontalDistance(tap3, tap2);
        width = (width + widthFar) * 0.5f;

        float depthLeft  = HorizontalDistance(tap0, tap3);
        float depthRight = HorizontalDistance(tap1, tap2);
        float depth = (depthLeft + depthRight) * 0.5f;

        Vector2 size = new Vector2(
            Mathf.Clamp(width, minSize.x, maxSize.x),
            Mathf.Clamp(depth, minSize.y, maxSize.y));

        Camera c = Camera.main;
        Vector3 headPos = c != null ? c.transform.position : Vector3.zero;
        Vector3 headFwd = c != null ? c.transform.forward : Vector3.forward;
        float avgEyeY = eyeYSamples > 0 ? eyeYAccum / eyeYSamples : headPos.y;

        return new DetectedTable
        {
            position = center,
            rotation = rotation,
            size = size,
            userHeadPosition = headPos,
            userForward = headFwd,
            avgEyeY = avgEyeY,
            avgTapSurfaceY = surfaceY,
        };
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private float TapYSpan()
    {
        if (taps.Count == 0) return 0f;
        float minY = taps[0].y, maxY = taps[0].y;
        for (int i = 1; i < taps.Count; i++)
        {
            if (taps[i].y < minY) minY = taps[i].y;
            if (taps[i].y > maxY) maxY = taps[i].y;
        }
        return maxY - minY;
    }

    // ================================================================
    // INSTRUCTION TEXT
    // ================================================================

    private void SetInstructionForTap()
    {
        int next = taps.Count; // 0..3
        if (next >= RequiredTaps) return;
        string label = TapLabels[next];
        SetInstruction($"Tap the {label} corner.\n({taps.Count}/{RequiredTaps})");
    }

    private void SetInstructionForConfirm()
    {
        float span = TapYSpan();
        if (span > unevenWarnSpan)
        {
            SetInstruction($"Table seems uneven (±{span * 1000f:F0} mm).\n" +
                           "Press Redo to retry, or Confirm to keep.");
        }
        else
        {
            SetInstruction("Press Confirm to place your whiteboard,\nor Redo to start over.");
        }
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

        if (previewRectangleObj == null)
        {
            previewRectangleObj = new GameObject("TableTapPreviewRect");
            previewRectangleObj.layer = passthroughLayer;
            previewRectangle = previewRectangleObj.AddComponent<LineRenderer>();
            previewRectangle.useWorldSpace = true;
            previewRectangle.loop = false;
            previewRectangle.positionCount = 0;
            previewRectangle.startWidth = 0.005f;
            previewRectangle.endWidth = 0.005f;
            previewRectangle.numCapVertices = 2;
            previewRectangle.numCornerVertices = 2;
            var lineMat = MakeMat(ColorPreview);
            previewRectangle.material = lineMat;
            previewRectangle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            previewRectangle.receiveShadows = false;
            previewRectangleObj.SetActive(false);
        }

        if (confirmButton == null)
        {
            confirmButton = MakeButton("TableTapConfirm", "Confirm", out confirmButtonMat, out confirmLabel);
            confirmButton.SetActive(false);
            // Prime with confirm-style colour
            SetButtonColor(confirmButtonMat, ColorConfirm);
        }

        if (redoButton == null)
        {
            redoButton = MakeButton("TableTapRedo", "Redo", out redoButtonMat, out redoLabel);
            redoButton.SetActive(false);
            SetButtonColor(redoButtonMat, ColorRedo);
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

    private GameObject EnsureTapMarker(int index)
    {
        while (tapMarkers.Count <= index)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            m.name = $"TapMarker_{tapMarkers.Count + 1}";
            Destroy(m.GetComponent<Collider>());
            m.transform.localScale = Vector3.one * (tapMarkerRadius * 2f);
            if (tapMarkerMat == null) tapMarkerMat = MakeMat(ColorTapMarker);
            ApplyMat(m, tapMarkerMat);
            PassthroughManager.SetLayerRecursive(m, passthroughLayer);
            m.SetActive(false);
            tapMarkers.Add(m);
        }
        return tapMarkers[index];
    }

    private void ClearTapMarkers()
    {
        foreach (var m in tapMarkers) if (m != null) m.SetActive(false);
    }

    private void UpdatePreviewRectangle()
    {
        if (previewRectangle == null) return;

        if (taps.Count < 2)
        {
            HidePreviewRectangle();
            return;
        }

        previewRectangleObj.SetActive(true);
        // Draw the polyline through existing taps, closing only once we have 4.
        int points = taps.Count + (taps.Count >= 4 ? 1 : 0);
        previewRectangle.positionCount = points;
        for (int i = 0; i < taps.Count; i++)
            previewRectangle.SetPosition(i, taps[i]);
        if (taps.Count >= 4)
            previewRectangle.SetPosition(4, taps[0]);

        bool uneven = TapYSpan() > unevenWarnSpan;
        previewRectangle.startColor = previewRectangle.endColor =
            uneven ? ColorPreviewUneven : ColorPreview;
    }

    private void HidePreviewRectangle()
    {
        if (previewRectangleObj != null) previewRectangleObj.SetActive(false);
    }

    private void ShowConfirmAndRedo()
    {
        if (confirmButton == null || redoButton == null) return;

        // Compute where the buttons should sit: above the rectangle centre, on
        // the user's side, facing them.
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 centroidXZ = Vector3.zero;
        foreach (var t in taps) centroidXZ += t;
        centroidXZ /= taps.Count;

        float surfaceY = RunningMeanY();
        Vector3 centre = new Vector3(centroidXZ.x, surfaceY + buttonHeightAboveRect, centroidXZ.z);

        // Face the user (XZ-only forward)
        Vector3 toUser = cam.transform.position - centre;
        toUser.y = 0f;
        if (toUser.sqrMagnitude < 1e-4f) toUser = -cam.transform.forward;
        toUser.Normalize();

        Quaternion rot = Quaternion.LookRotation(-toUser, Vector3.up); // face user

        Vector3 right = Vector3.Cross(Vector3.up, -toUser).normalized;

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
    // HELPERS
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
        tmp.fontSize = 0.5f;
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
        if (tipIndicator != null)      Destroy(tipIndicator);
        foreach (var m in tapMarkers) if (m != null) Destroy(m);
        if (previewRectangleObj != null) Destroy(previewRectangleObj);
        if (confirmButton != null)     Destroy(confirmButton);
        if (redoButton != null)        Destroy(redoButton);
        if (instructionObj != null)    Destroy(instructionObj);

        if (tipIndicatorMat != null) Destroy(tipIndicatorMat);
        if (tapMarkerMat != null) Destroy(tapMarkerMat);
        if (confirmButtonMat != null) Destroy(confirmButtonMat);
        if (redoButtonMat != null) Destroy(redoButtonMat);
    }
}
