using System;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// DIY stylus calibration — prescribed-rotation pinch mode (plan B-2.A).
///
/// Flow (per <see cref="BeginCalibration"/>):
///
///   Verify mode (when a prior calibration exists on disk and passed quality):
///     The predicted tip sphere appears.  Dwell the pen tip on the sphere for
///     <see cref="verifyDwellSeconds"/> to confirm the prior (~1 s).  Rotating
///     the wrist exits verify mode and starts a fresh capture pass.
///
///   Capture mode (new user, or after verify-mode exit):
///     Three samples, each with an explicit wrist-angle instruction so that
///     rotational diversity is guaranteed by construction:
///       1/3  "Hold pen flat on sphere, wrist neutral."    → pinch
///       2/3  "Tilt wrist DOWN ~30°, hold pen on sphere."  → pinch
///       3/3  "Tilt wrist SIDEWAYS ~30°, hold pen on sphere." → pinch
///     Capture trigger per sample: opposite-hand thumb-to-middle gap crosses
///     below <see cref="pinchThreshold"/> on a rising edge, AND the stylus
///     wrist linear speed is below <see cref="linearVelocityThreshold"/>, AND
///     the inter-sample cooldown has elapsed.
///     After all 3 samples the wrist tracker solves the offset.  If residual
///     exceeds <see cref="residualWarnThreshold"/> the user is asked to redo.
///     On success the Next button appears.
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
    [Tooltip("Number of prescribed-pose samples required before the solve runs. " +
             "Matches the length of the PoseInstructions array — change both together.")]
    [Range(2, 6)] public int samplesRequired = 3;
    [Tooltip("Opposite-hand thumb-to-middle gap (metres) that registers as a pinch.")]
    public float pinchThreshold = PinchConstants.Default;
    [Tooltip("Wrist linear speed (m/s) below which a pinch capture is accepted.")]
    public float linearVelocityThreshold = 0.05f;
    [Tooltip("Minimum seconds between captured samples — prevents one slow pinch registering twice.")]
    public float captureCooldownSeconds = 0.6f;
    [Tooltip("RMS residual (metres) above which the user is asked to redo.")]
    public float residualWarnThreshold = 0.008f;

    [Header("Verify Mode")]
    [Tooltip("Dwell time (seconds) near the predicted sphere to accept a prior calibration.")]
    public float verifyDwellSeconds = 0.50f;
    [Tooltip("Wrist angular speed (°/s) above which verify mode is exited and fresh capture begins.")]
    public float verifyExitAngularSpeedDegS = 10f;

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
    // PRESCRIBED POSE INSTRUCTIONS  (length must match samplesRequired)
    // ================================================================

    private string GetPoseInstruction(int poseIndex)
    {
        string penHand = stylusHand == Handedness.Right ? "right" : "left";
        switch (poseIndex)
        {
            case 0:  return $"Hold pen flat on sphere,\n{penHand} (pen) wrist neutral.";
            case 1:  return $"Tilt your {penHand} (pen) wrist DOWN ~30°,\nhold pen on sphere.";
            case 2:  return $"Tilt your {penHand} (pen) wrist SIDEWAYS ~30°,\nhold pen on sphere.";
            default: return "Hold pen on sphere and pinch to capture.";
        }
    }

    // ================================================================
    // STATE
    // ================================================================

    private bool isActive;
    private bool calibrationDone;
    private float calibrationDoneTime;

    // Pinch edge-detection.
    private bool wasPinched;
    private float lastCaptureTime;

    // Wrist linear speed (stillness gate).
    private bool hasLastWristPose;
    private Vector3 lastWristPos;
    private float lastPoseTime;

    // Wrist angular speed (verify-mode exit only).
    private bool hasLastWristRot;
    private Quaternion lastWristRot;

    // Verify mode.
    private bool isVerifyMode;
    private float verifyDwellAccum;

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

    private static readonly Color ColorTargetIdle      = new Color(0.20f, 0.90f, 0.30f, 0.85f); // green
    private static readonly Color ColorTargetCaptured  = new Color(0.30f, 0.85f, 1.00f, 1.00f); // blue flash after capture
    private static readonly Color ColorTargetCapturing = new Color(1.00f, 0.90f, 0.20f, 0.95f); // yellow — verify dwell
    private static readonly Color ColorTargetDone      = new Color(1.00f, 0.85f, 0.20f, 1.00f); // gold
    private static readonly Color ColorBtnArmed        = new Color(0.20f, 0.80f, 1.00f, 0.95f);
    private static readonly Color ColorBtnDisabled     = new Color(0.40f, 0.40f, 0.45f, 0.60f);

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
        wasPinched = true; // require a fresh rising edge before first capture
        lastCaptureTime = -999f;
        hasLastWristPose = false;
        hasLastWristRot = false;
        verifyDwellAccum = 0f;
        isVerifyMode = false;

        if (wristTracker != null)
        {
            wristTracker.ClearSamples();

            // If a prior calibration exists and passed quality, offer verify mode.
            var prior = StylusCalibrationStore.Load();
            if (prior != null
                && prior.handedness == stylusHand.ToString()
                && prior.rmsResidualMeters < residualWarnThreshold)
            {
                wristTracker.SetWristOffset(new Vector3(prior.offsetX, prior.offsetY, prior.offsetZ));
                isVerifyMode = true;
                if (logEvents)
                    Debug.Log($"[StylusCalibration] Prior loaded (RMS {prior.rmsResidualMeters * 1000f:F1} mm). " +
                              "Entering verify mode.");
            }
            else
            {
                wristTracker.ClearCalibration();
            }
        }

        SetTargetColor(ColorTargetIdle);

        if (isVerifyMode)
            SetInstruction("Previous calibration loaded.\nTouch sphere to confirm,\nor rotate wrist to recalibrate.");
        else
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
        if (!calibrationDone) UpdateCapturePhase();
        else UpdatePostCalibration();
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

        // 1. Target sphere on opposite index fingertip.
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
            wasPinched = true;
            return;
        }

        // 2. Wrist pose — linear speed for stillness gate, angular speed for verify exit.
        if (wristTracker == null ||
            !wristTracker.TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
        {
            SetInstruction("Stylus hand not tracked.\nShow your " +
                           (stylusHand == Handedness.Left ? "left" : "right") + " hand.");
            wasPinched = true;
            hasLastWristPose = false;
            hasLastWristRot = false;
            return;
        }

        float now = Time.time;
        float linearSpeed = 0f;
        float angularSpeedDegS = 0f;
        if (hasLastWristPose)
        {
            float dt = Mathf.Max(now - lastPoseTime, 1e-4f);
            linearSpeed = (wristPos - lastWristPos).magnitude / dt;
            if (hasLastWristRot)
                angularSpeedDegS = Quaternion.Angle(lastWristRot, wristRot) / dt;
        }
        lastWristPos = wristPos;
        lastWristRot = wristRot;
        lastPoseTime = now;
        hasLastWristPose = true;
        hasLastWristRot  = true;

        // 3. Verify mode: dwell to accept prior, rotate to recalibrate.
        if (isVerifyMode)
        {
            UpdateVerifyMode(targetPos, angularSpeedDegS);
            return;
        }

        // 4. Pinch rising edge = explicit "capture now" signal.
        //    The index finger stays extended (holding the target), so only thumb
        //    and middle move — the target position is undisturbed at capture instant.
        bool hasPinchReading = wristTracker.TryGetPinchGap(oppositeHand, out float pinchGap);
        bool isPinchedNow    = hasPinchReading && pinchGap < pinchThreshold;
        bool risingPinch     = isPinchedNow && !wasPinched;

        bool stillnessOK = linearSpeed < linearVelocityThreshold;
        bool onCooldown  = (now - lastCaptureTime) < captureCooldownSeconds;

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

        // Blue flash during cooldown acknowledges the capture visually.
        SetTargetColor(onCooldown ? ColorTargetCaptured : ColorTargetIdle);
    }

    private void UpdateVerifyMode(Vector3 targetPos, float angularSpeedDegS)
    {
        // Rotate to exit verify mode and start fresh capture.
        if (angularSpeedDegS > verifyExitAngularSpeedDegS)
        {
            isVerifyMode = false;
            verifyDwellAccum = 0f;
            wristTracker.ClearCalibration();
            wristTracker.ClearSamples();
            SetTargetColor(ColorTargetIdle);
            UpdateInstructionForProgress();
            if (logEvents) Debug.Log("[StylusCalibration] Verify mode exited — user rotating.");
            return;
        }

        // Dwell near the predicted tip position to accept the prior.
        if (wristTracker.TryGetTipPosition(out Vector3 tipWorld, out _))
        {
            // Use the proximity gate from the prior offset.
            const float kVerifyProximity = 0.025f;
            if (Vector3.Distance(tipWorld, targetPos) < kVerifyProximity)
            {
                verifyDwellAccum += Time.deltaTime;
                SetTargetColor(ColorTargetCapturing);
                SetInstruction($"Hold still to confirm prior calibration...\n" +
                               $"({verifyDwellAccum:F1} / {verifyDwellSeconds:F1} s)");

                if (verifyDwellAccum >= verifyDwellSeconds)
                {
                    if (logEvents) Debug.Log("[StylusCalibration] Prior verified via dwell.");
                    AcceptCalibration(residualWarnThreshold * 0.5f);
                }
            }
            else
            {
                verifyDwellAccum = 0f;
                SetTargetColor(ColorTargetIdle);
                SetInstruction("Previous calibration loaded.\nTouch sphere to confirm,\nor rotate wrist to recalibrate.");
            }
        }
        else
        {
            SetInstruction("Previous calibration loaded.\nTouch sphere to confirm,\nor rotate wrist to recalibrate.");
        }
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
            Solve();
        else
            UpdateInstructionForProgress();
    }

    private void UpdateInstructionForProgress()
    {
        int have = wristTracker != null ? wristTracker.SampleCount : 0;
        string hand = OppositeHand() == Handedness.Left ? "left" : "right";

        // Pick the instruction for the NEXT pose to capture.
        int poseIdx = Mathf.Clamp(have, 0, samplesRequired - 1);
        string poseText = GetPoseInstruction(poseIdx);

        SetInstruction($"{poseText}\n" +
                       $"Pinch {hand} thumb + middle to capture.\n" +
                       $"{have}/{samplesRequired}");
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

        bool passed = rms <= residualWarnThreshold;
        int n = wristTracker.SampleCount;
        if (logEvents)
            Debug.Log($"[StylusCalibration] Solve. N={n}, RMS={rms * 1000f:F1} mm, passed={passed}.");

        StylusCalibrationStore.Save(stylusHand, wristTracker.CurrentOffset, rms, n);

        if (!passed)
        {
            SetInstruction($"Residual {rms * 1000f:F0} mm — quality low.\n" +
                           "Tap the sphere again from\nmore varied wrist angles.");
            wristTracker.ClearSamples();
            return;
        }

        AcceptCalibration(rms);
    }

    private void AcceptCalibration(float rms)
    {
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

    // ================================================================
    // POST-CALIBRATION: wait for Next poke
    // ================================================================

    private void UpdatePostCalibration()
    {
        bool nextArmed = Time.time - calibrationDoneTime >= nextArmDelay;
        SetButtonColor(nextMaterial, nextArmed ? ColorBtnArmed : ColorBtnDisabled);
        if (!nextArmed || wristTracker == null) return;

        if (!wristTracker.TryGetIndexTipForHand(Handedness.Left,  out Vector3 leftTip))  leftTip  = new Vector3(1e6f, 1e6f, 1e6f);
        if (!wristTracker.TryGetIndexTipForHand(Handedness.Right, out Vector3 rightTip)) rightTip = new Vector3(1e6f, 1e6f, 1e6f);

        float d = Mathf.Min(
            Vector3.Distance(leftTip,  nextButtonPos),
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
            bg.transform.localScale = new Vector3(1.6f, 0.56f, 1f);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.001f);
            bg.layer = passthroughLayer;
            Destroy(bg.GetComponent<Collider>());
            ApplyMat(bg, MakeMat(new Color(0f, 0f, 0f, 0.65f)));

            instructionText = instructionObj.AddComponent<TextMeshPro>();
            instructionText.fontSize = fontSize;
            instructionText.alignment = TextAlignmentOptions.Center;
            instructionText.color = Color.white;
            instructionText.rectTransform.sizeDelta = new Vector2(1.5f, 0.60f);
            instructionText.textWrappingMode = TextWrappingModes.Normal;
            instructionText.sortingOrder = 1;
        }
    }

    private void PositionNextButton()
    {
        Camera cam = Camera.main;
        if (cam == null || nextButton == null) return;

        Vector3 forward = cam.transform.forward; forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 side = stylusHand == Handedness.Right
            ? cam.transform.right : -cam.transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.001f) side = Vector3.right;
        side.Normalize();

        nextButtonPos = cam.transform.position
                        + forward * buttonForward
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
        tmp.fontSize = 0.28f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(0.16f, 0.08f);
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
