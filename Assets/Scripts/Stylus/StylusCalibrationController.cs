using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.UI;
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
    [Tooltip("Cooldown after calibration before Next becomes clickable.")]
    public float nextArmDelay = 0.6f;

    [Header("Instruction Text")]
    public float instructionForward = 0.90f;
    public float instructionHeight = 0.18f;

    [Header("Panduan Visual")]
    [Tooltip("6 gambar panduan: [0,1]=langkah 1, [2,3]=langkah 2, [4,5]=langkah 3.")]
    [SerializeField] Sprite[] stepGuideImages;

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
        string penHand = stylusHand == Handedness.Right ? "kanan" : "kiri";
        switch (poseIndex)
        {
            case 0:  return $"Tahan pena mendatar di atas bola,\npergelangan tangan {penHand} (pena) netral.";
            case 1:  return $"Miringkan pergelangan tangan {penHand} (pena) ke BAWAH ~30°,\ntahan pena di atas bola.";
            case 2:  return $"Miringkan pergelangan tangan {penHand} (pena) ke SAMPING ~30°,\ntahan pena di atas bola.";
            default: return "Tahan pena di atas bola dan cubit untuk merekam.";
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

    // 3D spatial — kept as-is (rides on real fingertip position)
    private GameObject targetSphere;
    private Material    targetMaterial;

    // 2D Canvas UI — instruction panel
    private GameObject       instructionCanvas;
    private TextMeshProUGUI  instructionTmp;

    // 2D Canvas UI — Next button
    private GameObject nextCanvas;
    private Button     nextBtn;

    // 2D Canvas UI — Guide image panel
    private GameObject      guideCanvas;
    private Image           guideImg1;
    private Image           guideImg2;
    private TextMeshProUGUI stepLabel;

    private int passthroughLayer = 31;

    // Target sphere colors
    private static readonly Color ColorTargetIdle      = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTargetCaptured  = new Color(0.30f, 0.85f, 1.00f, 1.00f);
    private static readonly Color ColorTargetCapturing = new Color(1.00f, 0.90f, 0.20f, 0.95f);
    private static readonly Color ColorTargetDone      = new Color(1.00f, 0.85f, 0.20f, 1.00f);

    // 2D Canvas palette — mirrors JournalReviewController / CalibrationConfirmPanel
    private static readonly Color s_PanelBg     = new Color(0.969f, 0.918f, 0.918f, 1.00f);
    private static readonly Color s_AccentMauve = new Color(0.780f, 0.663f, 0.722f, 1.00f);
    private static readonly Color s_TextDark    = new Color(0.369f, 0.329f, 0.349f, 1.00f);
    private static readonly Color s_BtnNext     = new Color(0.490f, 0.730f, 0.560f, 1.00f); // sage green

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void BeginCalibration()
    {
        if (passthroughManager != null)
            passthroughLayer = passthroughManager.GetPassthroughUILayer();

        EnsureVisuals();

        // Cleanup() from a prior run leaves these inactive — re-show now.
        if (instructionCanvas != null) instructionCanvas.SetActive(true);
        if (targetSphere      != null) targetSphere.SetActive(true);
        if (nextCanvas        != null) nextCanvas.SetActive(false);
        if (guideCanvas != null)
        {
            SetGuideStep(0);
            guideCanvas.SetActive(true);
        }

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
            SetInstruction("Kalibrasi sebelumnya ditemukan.\nSentuh bola untuk konfirmasi,\natau putar pergelangan untuk kalibrasi ulang.");
        else
            UpdateInstructionForProgress();

        if (logEvents) Debug.Log("[StylusCalibration] BeginCalibration.");
    }

    public void Cleanup()
    {
        isActive = false;
        if (targetSphere      != null) targetSphere.SetActive(false);
        if (nextCanvas        != null) nextCanvas.SetActive(false);
        if (instructionCanvas != null) instructionCanvas.SetActive(false);
        if (guideCanvas != null) guideCanvas.SetActive(false);
    }

    /// <summary>
    /// Silently applies the last saved stylus calibration from disk, if quality is acceptable.
    /// Returns true if the offset was applied. Call this when skipping the calibration UI
    /// (e.g., cross-scene fast-path reuse via JournalCalibrationCache).
    /// </summary>
    public bool TryAutoApplyStored()
    {
        if (wristTracker == null) return false;

        var record = StylusCalibrationStore.Load();
        if (record == null) return false;
        if (record.rmsResidualMeters >= residualWarnThreshold) return false;
        if (record.handedness != stylusHand.ToString()) return false;

        wristTracker.SetWristOffset(new Vector3(record.offsetX, record.offsetY, record.offsetZ));
        Debug.Log($"[StylusCalibration] Auto-applied stored offset (RMS={record.rmsResidualMeters * 1000f:F1} mm) — no UI needed.");
        return true;
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
            SetInstruction("Tunjukkan tangan " + (oppositeHand == Handedness.Left ? "kiri" : "kanan") +
                           " Anda agar target muncul.");
            wasPinched = true;
            return;
        }

        // 2. Wrist pose — linear speed for stillness gate, angular speed for verify exit.
        if (wristTracker == null ||
            !wristTracker.TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
        {
            SetInstruction("Tangan stylus tidak terdeteksi.\nTunjukkan tangan " +
                           (stylusHand == Handedness.Left ? "kiri" : "kanan") + " Anda.");
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
            const float kVerifyProximity = 0.025f;
            if (Vector3.Distance(tipWorld, targetPos) < kVerifyProximity)
            {
                verifyDwellAccum += Time.deltaTime;
                SetTargetColor(ColorTargetCapturing);
                SetInstruction($"Tahan posisi untuk konfirmasi...\n" +
                               $"({verifyDwellAccum:F1} / {verifyDwellSeconds:F1} dtk)");

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
                SetInstruction("Kalibrasi sebelumnya ditemukan.\nSentuh bola untuk konfirmasi,\natau putar pergelangan untuk kalibrasi ulang.");
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
        string hand = OppositeHand() == Handedness.Left ? "kiri" : "kanan";

        int poseIdx = Mathf.Clamp(have, 0, samplesRequired - 1);
        string poseText = GetPoseInstruction(poseIdx);
        SetGuideStep(poseIdx);

        SetInstruction($"{poseText}\n" +
                       $"Cubit ibu jari + jari tengah tangan {hand} untuk merekam.\n" +
                       $"{have}/{samplesRequired}");
    }

    private void Solve()
    {
        if (wristTracker == null) return;
        if (!wristTracker.FinalizeOffset(out float rms))
        {
            SetInstruction("Kalibrasi gagal.\nCoba lagi.");
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
            SetInstruction($"Residual {rms * 1000f:F0} mm — kualitas rendah.\n" +
                           "Sentuh bola lagi dari\nsudut pergelangan lebih bervariasi.");
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
        SetInstruction($"Kalibrasi selesai! Residual {rms * 1000f:F1} mm.\nTekan Lanjut untuk melanjutkan.");

        PositionNextButton();
        if (nextCanvas != null)
        {
            nextCanvas.SetActive(true);
            if (nextBtn != null) nextBtn.interactable = false; // armed after nextArmDelay
        }

        OnCalibrationComplete?.Invoke();
    }

    // ================================================================
    // POST-CALIBRATION: arm the Next button after the delay
    // ================================================================

    private void UpdatePostCalibration()
    {
        if (nextBtn == null || nextBtn.interactable) return;
        if (Time.time - calibrationDoneTime >= nextArmDelay)
            nextBtn.interactable = true;
    }

    private void OnNextButtonPokeDetected()
    {
        if (!calibrationDone) return;
        if (logEvents) Debug.Log("[StylusCalibration] Next pressed.");
        isActive = false;
        OnNextButtonPressed?.Invoke();
    }

    // ================================================================
    // VISUALS
    // ================================================================

    private void EnsureVisuals()
    {
        // ── Target sphere (3D spatial — rides on real fingertip) ──────
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

        // ── Instruction canvas ────────────────────────────────────────
        if (instructionCanvas == null)
        {
            var root   = new GameObject("StylusCalibInstruction");
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

        // ── Guide canvas ──────────────────────────────────────────────
        if (guideCanvas == null && stepGuideImages != null && stepGuideImages.Length >= 6)
        {
            var root   = new GameObject("StylusCalibGuide");
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

            var labelGO = new GameObject("StepLabel");
            labelGO.transform.SetParent(bar.transform, false);
            stepLabel = labelGO.AddComponent<TextMeshProUGUI>();
            stepLabel.fontSize  = 13f;
            stepLabel.alignment = TextAlignmentOptions.Center;
            stepLabel.color     = Color.white;
            stepLabel.fontStyle = FontStyles.Bold;
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            var img1GO = new GameObject("GuideImage1");
            img1GO.transform.SetParent(bg.transform, false);
            guideImg1 = img1GO.AddComponent<Image>();
            guideImg1.preserveAspect = true;
            var img1Rect = img1GO.GetComponent<RectTransform>();
            img1Rect.anchorMin = new Vector2(0.02f, 0.05f);
            img1Rect.anchorMax = new Vector2(0.48f, 0.92f);
            img1Rect.offsetMin = img1Rect.offsetMax = Vector2.zero;

            var img2GO = new GameObject("GuideImage2");
            img2GO.transform.SetParent(bg.transform, false);
            guideImg2 = img2GO.AddComponent<Image>();
            guideImg2.preserveAspect = true;
            var img2Rect = img2GO.GetComponent<RectTransform>();
            img2Rect.anchorMin = new Vector2(0.52f, 0.05f);
            img2Rect.anchorMax = new Vector2(0.98f, 0.92f);
            img2Rect.offsetMin = img2Rect.offsetMax = Vector2.zero;

            guideCanvas = root;
            guideCanvas.SetActive(false);
        }

        // ── Next button canvas ────────────────────────────────────────
        if (nextCanvas == null)
        {
            var root   = new GameObject("StylusCalibNext");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 80f);
            root.AddComponent<CanvasScaler>();
            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            root.transform.localScale = Vector3.one * 0.001f;
            PassthroughManager.SetLayerRecursive(root, passthroughLayer);

            var bg    = new GameObject("Background");
            bg.transform.SetParent(root.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = s_BtnNext;
            bg.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 80f);

            nextBtn = bg.AddComponent<Button>();
            nextBtn.targetGraphic = bgImg;
            nextBtn.onClick.AddListener(OnNextButtonPokeDetected);
            nextBtn.interactable = false;

            var lblGO = new GameObject("Label");
            lblGO.transform.SetParent(bg.transform, false);
            var tmp   = lblGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = "Lanjut";
            tmp.fontSize  = 28f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
            tmp.fontStyle = FontStyles.Bold;
            var lblRect   = lblGO.GetComponent<RectTransform>();
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            lblRect.offsetMin = lblRect.offsetMax = Vector2.zero;

            nextCanvas = root;
            nextCanvas.SetActive(false);
        }
    }

    private void PositionNextButton()
    {
        Camera cam = Camera.main;
        if (cam == null || nextCanvas == null) return;

        Vector3 forward = cam.transform.forward; forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 side = stylusHand == Handedness.Right
            ? cam.transform.right : -cam.transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.001f) side = Vector3.right;
        side.Normalize();

        nextCanvas.transform.position = cam.transform.position
                                        + forward * buttonForward
                                        + Vector3.down * buttonBelowEye
                                        + side * buttonSideOffset;
        nextCanvas.transform.rotation = Quaternion.LookRotation(forward);
    }

    private void BillboardInstruction()
    {
        if (instructionCanvas == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 basePos = cam.transform.position + fwd * instructionForward + Vector3.up * instructionHeight;

        if (guideCanvas != null)
        {
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            instructionCanvas.transform.position = basePos + right * 0.250f;
            guideCanvas.transform.position       = basePos - right * 0.330f;
            guideCanvas.transform.rotation       = Quaternion.LookRotation(fwd);
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

    private void SetGuideStep(int stepIndex)
    {
        if (guideImg1 == null || guideImg2 == null || stepGuideImages == null) return;
        int baseIdx = stepIndex * 2;
        if (baseIdx + 1 >= stepGuideImages.Length) return;
        guideImg1.sprite = stepGuideImages[baseIdx];
        guideImg2.sprite = stepGuideImages[baseIdx + 1];
        if (stepLabel != null) stepLabel.text = $"Langkah {stepIndex + 1}/{samplesRequired}";
    }

    private void SetTargetColor(Color c)
    {
        if (targetMaterial != null) targetMaterial.color = c;
    }

    private Handedness OppositeHand() =>
        stylusHand == Handedness.Right ? Handedness.Left : Handedness.Right;

    // ================================================================
    // MATERIAL HELPERS (for target sphere only)
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
        if (targetSphere      != null) Destroy(targetSphere);
        if (nextCanvas        != null) Destroy(nextCanvas);
        if (instructionCanvas != null) Destroy(instructionCanvas);
        if (targetMaterial    != null) Destroy(targetMaterial);
        if (guideCanvas       != null) Destroy(guideCanvas);
    }
}
