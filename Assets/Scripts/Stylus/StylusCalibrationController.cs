using System;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// DIY stylus calibration via "touch the fingertip" — the user brings the
/// physical pen tip to their opposite hand's index fingertip, then pinches
/// their opposite thumb to middle finger to capture the sample. The pinch is
/// the explicit "capture now" signal: the system cannot observe the pen tip's
/// world position on its own (no pen sensors, no CV), so the user closes the
/// loop with a gesture they can time precisely against the physical contact
/// they feel.
///
/// Flow (per <see cref="BeginCalibration"/>):
///   1. A small sphere renders at the opposite hand's index fingertip and
///      follows it every frame.
///   2. Instruction: "Place pen tip on the sphere. Pinch opposite thumb +
///      middle to capture. Vary wrist angle. 0/N".
///   3. Capture trigger per sample: opposite-hand ThumbTip-to-MiddleTip gap
///      crosses below <see cref="pinchThreshold"/> on a rising edge, AND the
///      stylus wrist is nearly still (linear speed &lt;
///      <see cref="linearVelocityThreshold"/> m/s), AND we are past the
///      inter-sample cooldown.
///   4. After capture: short cooldown + colour flash. User releases the
///      pinch, varies wrist pose, re-pinches for the next sample.
///   5. After <see cref="samplesRequired"/> captures, the wrist tracker
///      computes the least-squares offset and the RMS residual. If residual
///      exceeds <see cref="residualWarnThreshold"/>, the user is asked to redo.
///   6. On success, the Next button appears — poke with either hand's index
///      tip to advance.
///
/// The opposite hand's index finger stays extended throughout, holding the
/// target. Only the thumb and middle finger move when pinching, so the
/// target position is not disturbed at the exact moment of capture.
/// </summary>
public class StylusCalibrationController : MonoBehaviour
{
    // ================================================================
    // INSPECTOR
    // ================================================================

    [Header("References")]
    public PassthroughManager passthroughManager;
    public StylusWristTracker wristTracker;

    [Header("Hands")]
    [Tooltip("Which hand holds the stylus. The opposite hand carries the contact target.")]
    public Handedness stylusHand = Handedness.Right;

    [Header("Sampling")]
    [Tooltip("Number of samples required before the solve runs.")]
    [Range(3, 10)] public int samplesRequired = 5;
    [Tooltip("Opposite-hand thumb-to-middle distance (metres) that registers as a pinch. A rising edge below this value fires a capture.")]
    public float pinchThreshold = 0.025f;
    [Tooltip("Wrist linear speed threshold (m/s). Pinches fired while the stylus hand is moving faster than this are ignored — prevents captures during mid-motion.")]
    public float linearVelocityThreshold = 0.05f;
    [Tooltip("Minimum delay (seconds) between captured samples — prevents a single pinch registering twice.")]
    public float captureCooldownSeconds = 0.6f;
    [Tooltip("Residual warn threshold (metres). If the solved RMS residual exceeds this, the user is prompted to retry.")]
    public float residualWarnThreshold = 0.008f;

    [Header("Target Sphere")]
    [Tooltip("Radius (m) of the target sphere that rides on the opposite index fingertip.")]
    public float targetRadius = 0.010f;

    [Header("Next Button")]
    [Tooltip("Forward distance for the Next button after calibration succeeds.")]
    public float buttonForward = 0.50f;
    [Tooltip("Vertical offset below eye level for the Next button.")]
    public float buttonBelowEye = 0.05f;
    [Tooltip("Horizontal offset from centre — positive goes toward the stylus side so the opposite hand can poke it.")]
    public float buttonSideOffset = 0.18f;
    [Tooltip("Proximity (m) at which an index-tip poke triggers the Next button.")]
    public float buttonPokeDistance = 0.030f;
    [Tooltip("Cooldown after calibration before Next becomes pokeable.")]
    public float nextArmDelay = 0.6f;

    [Header("Instruction Text")]
    public float instructionForward = 0.90f;
    public float instructionHeight = 0.18f;
    public float fontSize = 0.28f;

    [Header("Diagnostics")]
    [Tooltip("Log sample captures, dwell state, and solve results.")]
    public bool logEvents = true;

    // ================================================================
    // EVENTS
    // ================================================================

    public event Action OnCalibrationComplete;
    public event Action OnNextButtonPressed;

    // ================================================================
    // STATE
    // ================================================================

    private bool isActive;
    private bool calibrationDone;
    private float calibrationDoneTime;

    // Pinch edge-detection state.
    private bool wasPinched;
    private float lastCaptureTime;

    // Velocity tracking for the stylus wrist (linear only; used as a sanity
    // gate so a pinch fired mid-motion doesn't register a bad sample).
    private bool hasLastWristPose;
    private Vector3 lastWristPos;
    private float lastPoseTime;

    // ================================================================
    // VISUALS (runtime-created)
    // ================================================================

    private GameObject targetSphere;
    private Material targetMaterial;

    private GameObject nextButton;
    private Material nextMaterial;
    private TextMeshPro nextLabel;

    private TextMeshPro instructionText;
    private GameObject instructionObj;

    private int passthroughLayer = 31;
    private Vector3 nextButtonPos;

    private static readonly Color ColorTargetIdle = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTargetCaptured = new Color(0.30f, 0.85f, 1.00f, 1.00f); // post-capture flash
    private static readonly Color ColorTargetDone = new Color(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Color ColorBtnArmed = new Color(0.20f, 0.80f, 1.00f, 0.95f);
    private static readonly Color ColorBtnDisabled = new Color(0.40f, 0.40f, 0.45f, 0.60f);

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void BeginCalibration()
    {
        if (passthroughManager != null)
            passthroughLayer = passthroughManager.GetPassthroughUILayer();

        EnsureVisuals();

        isActive = true;
        calibrationDone = false;
        // Treat the starting state as "pinched" so the user must first release
        // before the next pinch registers as a rising edge. Prevents a spurious
        // capture if the user was already pinching when the state entered.
        wasPinched = true;
        lastCaptureTime = -999f;
        hasLastWristPose = false;

        if (wristTracker != null)
        {
            wristTracker.ClearCalibration();
            wristTracker.ClearSamples();
        }

        SetTargetColor(ColorTargetIdle);
        UpdateInstructionForProgress();

        if (nextButton != null) nextButton.SetActive(false);

        if (logEvents) Debug.Log("[StylusCalibration] BeginCalibration.");
    }

    public void Cleanup()
    {
        isActive = false;
        if (targetSphere   != null) targetSphere.SetActive(false);
        if (nextButton     != null) nextButton.SetActive(false);
        if (instructionObj != null) instructionObj.SetActive(false);
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (!isActive) return;

        if (!calibrationDone)
            UpdateCapturePhase();
        else
            UpdatePostCalibration();
    }

    private void LateUpdate()
    {
        if (!isActive) return;
        BillboardInstruction();
    }

    // ================================================================
    // CAPTURE PHASE
    // ================================================================

    private void UpdateCapturePhase()
    {
        Handedness oppositeHand = OppositeHand();

        // 1. Target sphere rides on the opposite index fingertip.
        Vector3 targetPos = Vector3.zero;
        bool hasTarget = wristTracker != null
                         && wristTracker.TryGetIndexTipForHand(oppositeHand, out targetPos);
        if (targetSphere != null)
        {
            targetSphere.SetActive(hasTarget);
            if (hasTarget) targetSphere.transform.position = targetPos;
        }
        if (!hasTarget)
        {
            SetInstruction("Show your " + (oppositeHand == Handedness.Left ? "left" : "right") +
                           " hand so the target can appear.");
            wasPinched = true; // force re-release before the next rising edge counts
            return;
        }

        // 2. Read stylus wrist pose for the stillness sanity gate.
        if (wristTracker == null ||
            !wristTracker.TryGetWristPose(out Vector3 wristPos, out _))
        {
            SetInstruction("Stylus hand not tracked.\nShow your " +
                           (stylusHand == Handedness.Left ? "left" : "right") + " hand.");
            wasPinched = true;
            hasLastWristPose = false;
            return;
        }

        float now = Time.time;
        float linearSpeed = 0f;
        if (hasLastWristPose)
        {
            float dt = Mathf.Max(now - lastPoseTime, 1e-4f);
            linearSpeed = (wristPos - lastWristPos).magnitude / dt;
        }
        lastWristPos = wristPos;
        lastPoseTime = now;
        hasLastWristPose = true;

        // 3. Opposite-hand pinch (thumb-to-middle) is the explicit "capture
        //    now" signal. The user presses the pen tip to the target, then
        //    pinches. The index finger stays extended carrying the target,
        //    so the target position is undisturbed at the capture instant.
        bool hasPinchReading = wristTracker.TryGetPinchGap(oppositeHand, out float pinchGap);
        bool isPinchedNow = hasPinchReading && pinchGap < pinchThreshold;
        bool risingPinch = isPinchedNow && !wasPinched;

        bool stillnessOK = linearSpeed < linearVelocityThreshold;
        bool onCooldown = (now - lastCaptureTime) < captureCooldownSeconds;

        if (risingPinch && stillnessOK && !onCooldown)
        {
            CaptureSample(targetPos);
        }
        else if (risingPinch && logEvents)
        {
            if (!stillnessOK)
                Debug.Log($"[StylusCalibration] Pinch ignored — stylus hand moving ({linearSpeed:F2} m/s).");
            else if (onCooldown)
                Debug.Log("[StylusCalibration] Pinch ignored — cooldown.");
        }

        wasPinched = isPinchedNow;

        // 4. Target sphere colour — green by default, blue while in the
        //    post-capture cooldown to acknowledge the sample visually.
        SetTargetColor(onCooldown ? ColorTargetCaptured : ColorTargetIdle);
    }

    private void CaptureSample(Vector3 targetPos)
    {
        if (wristTracker == null) return;
        if (!wristTracker.AccumulateSample(targetPos))
        {
            if (logEvents) Debug.LogWarning("[StylusCalibration] AccumulateSample failed (hand lost mid-capture).");
            return;
        }

        lastCaptureTime = Time.time;

        int n = wristTracker.SampleCount;
        if (logEvents) Debug.Log($"[StylusCalibration] Sample {n}/{samplesRequired} captured at {targetPos}.");

        if (n >= samplesRequired)
        {
            Solve();
        }
        else
        {
            UpdateInstructionForProgress();
        }
    }

    private void Solve()
    {
        if (wristTracker == null) return;
        if (!wristTracker.FinalizeOffset(out float rms))
        {
            SetInstruction("Calibration failed.\nPlease retry.");
            wristTracker.ClearSamples();
            return;
        }

        bool passedQuality = rms <= residualWarnThreshold;
        int sampleCount = wristTracker.SampleCount;

        if (logEvents)
            Debug.Log($"[StylusCalibration] Solve complete. N={sampleCount}, RMS={rms * 1000f:F1}mm, " +
                      $"passed={passedQuality}");

        // Always write the diagnostic record, regardless of pass/fail.
        StylusCalibrationStore.Save(stylusHand, wristTracker.CurrentOffset, rms, sampleCount);

        if (!passedQuality)
        {
            // Keep the offset (it is the best we have) but ask for a redo.
            SetInstruction($"Residual {rms * 1000f:F0} mm — quality low.\n" +
                           "Tap the sphere again from\nmore varied wrist angles.");
            wristTracker.ClearSamples(); // user will re-sample from scratch
            return;
        }

        calibrationDone = true;
        calibrationDoneTime = Time.time;

        SetTargetColor(ColorTargetDone);
        SetInstruction($"Calibrated! Residual {rms * 1000f:F1} mm.\nPress Next to continue.");

        PositionNextButton();
        if (nextButton != null)
        {
            nextButton.SetActive(true);
            SetButtonColor(nextMaterial, ColorBtnDisabled);
        }

        OnCalibrationComplete?.Invoke();
    }

    private void UpdateInstructionForProgress()
    {
        int have = wristTracker != null ? wristTracker.SampleCount : 0;
        string hand = OppositeHand() == Handedness.Left ? "left" : "right";
        SetInstruction($"Place pen tip on the sphere.\n" +
                       $"Pinch {hand} thumb + middle\nto capture. Vary wrist angle.\n" +
                       $"{have}/{samplesRequired}");
    }

    // ================================================================
    // POST-CALIBRATION: wait for Next poke
    // ================================================================

    private void UpdatePostCalibration()
    {
        bool nextArmed = Time.time - calibrationDoneTime >= nextArmDelay;
        SetButtonColor(nextMaterial, nextArmed ? ColorBtnArmed : ColorBtnDisabled);
        if (!nextArmed || wristTracker == null) return;

        // Either index tip can poke the Next button.
        if (!wristTracker.TryGetIndexTipForHand(Handedness.Left, out Vector3 leftTip)) leftTip = new Vector3(1e6f, 1e6f, 1e6f);
        if (!wristTracker.TryGetIndexTipForHand(Handedness.Right, out Vector3 rightTip)) rightTip = new Vector3(1e6f, 1e6f, 1e6f);

        float d = Mathf.Min(
            Vector3.Distance(leftTip, nextButtonPos),
            Vector3.Distance(rightTip, nextButtonPos));

        if (d <= buttonPokeDistance)
        {
            if (logEvents) Debug.Log("[StylusCalibration] Next pressed.");
            isActive = false;
            OnNextButtonPressed?.Invoke();
        }
    }

    // ================================================================
    // VISUALS
    // ================================================================

    private void EnsureVisuals()
    {
        if (targetSphere == null)
        {
            targetSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            targetSphere.name = "StylusCalibFingertipTarget";
            Destroy(targetSphere.GetComponent<Collider>());
            targetSphere.transform.localScale = Vector3.one * (targetRadius * 2f);
            targetMaterial = MakeMat(ColorTargetIdle);
            ApplyMat(targetSphere, targetMaterial);
            PassthroughManager.SetLayerRecursive(targetSphere, passthroughLayer);
        }

        if (nextButton == null)
        {
            nextButton = MakeButton("StylusCalibNext", "Next", out nextMaterial, out nextLabel);
            nextButton.SetActive(false);
        }

        if (instructionObj == null)
        {
            instructionObj = new GameObject("StylusCalibInstruction");
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

    private void PositionNextButton()
    {
        Camera cam = Camera.main;
        if (cam == null || nextButton == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 forward = cam.transform.forward; forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        // Place the Next button on the STYLUS side so the opposite (free) hand
        // crosses over minimally — and either hand can still poke it.
        Vector3 side = stylusHand == Handedness.Right
            ? cam.transform.right
            : -cam.transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.001f) side = Vector3.right;
        side.Normalize();

        nextButtonPos = camPos + forward * buttonForward
                        + Vector3.down * buttonBelowEye
                        + side * buttonSideOffset;

        nextButton.transform.position = nextButtonPos;
        nextButton.transform.rotation = Quaternion.LookRotation(forward);
    }

    private void BillboardInstruction()
    {
        if (instructionObj == null || !instructionObj.activeSelf) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
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

    private void SetTargetColor(Color c)
    {
        if (targetMaterial != null) targetMaterial.color = c;
    }

    private static void SetButtonColor(Material mat, Color c)
    {
        if (mat != null) mat.color = c;
    }

    private Handedness OppositeHand() =>
        stylusHand == Handedness.Right ? Handedness.Left : Handedness.Right;

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
        mat = MakeMat(ColorBtnArmed);
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
        if (targetSphere    != null) Destroy(targetSphere);
        if (nextButton      != null) Destroy(nextButton);
        if (instructionObj  != null) Destroy(instructionObj);
        if (targetMaterial  != null) Destroy(targetMaterial);
        if (nextMaterial    != null) Destroy(nextMaterial);
    }
}
