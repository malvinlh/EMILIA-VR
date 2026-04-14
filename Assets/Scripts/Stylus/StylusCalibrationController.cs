using System;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Runs during the StylusCalibration session state (in passthrough, before
/// table detection). Shows a virtual target sphere; the user touches the
/// pen tip to the target and holds steady. During the hold, the index-tip
/// joint is used as a proxy for the pen tip to compute the wrist-to-tip
/// offset stored on <see cref="StylusWristTracker"/>.
///
/// After calibration, a "Next" button appears. Poking it fires
/// <see cref="OnNextButtonPressed"/> so <see cref="JournalSessionManager"/>
/// can advance to table detection.
/// </summary>
public class StylusCalibrationController : MonoBehaviour
{
    [Header("References")]
    public PassthroughManager passthroughManager;
    public StylusTipProvider tipProvider;
    public StylusWristTracker wristTracker;

    [Header("Calibration")]
    [Tooltip("Hand used for calibration. Must match StylusWristTracker.handedness.")]
    public Handedness calibrationHand = Handedness.Right;
    [Tooltip("Distance (metres) below which the index tip is considered 'on' the target.")]
    public float tipProximityThreshold = 0.02f;
    [Tooltip("Hold duration (seconds) to complete calibration.")]
    public float holdDuration = 1.0f;

    [Header("Target Placement")]
    [Tooltip("Distance forward from the user's head at which the target appears.")]
    public float targetForwardDistance = 0.35f;
    [Tooltip("Vertical offset below eye level at which the target appears.")]
    public float targetBelowEye = 0.25f;
    [Tooltip("Radius of the target sphere (metres).")]
    public float targetRadius = 0.015f;

    [Header("Next Button")]
    [Tooltip("Distance forward from the user's head at which the Next button appears.")]
    public float nextButtonForward = 0.5f;
    [Tooltip("Vertical offset below eye level for the Next button.")]
    public float nextButtonBelowEye = 0.1f;
    [Tooltip("Proximity (metres) at which an index fingertip poke triggers the button.")]
    public float nextButtonPokeDistance = 0.025f;
    [Tooltip("Cooldown after calibration before Next can be pressed (prevents accidental poke).")]
    public float nextButtonArmDelay = 0.5f;

    [Header("Text")]
    [Tooltip("Distance from the user's head for instruction text.")]
    public float instructionDistance = 1.0f;
    [Tooltip("Height offset above eye level for instruction text.")]
    public float instructionHeight = 0.2f;
    [Tooltip("Font size for instruction text.")]
    public float fontSize = 0.3f;

    [Header("Diagnostics")]
    [Tooltip("Show a visible marker at the tracked index-tip position and a guide line " +
             "to the target. Essential while dialling in pen grip / calibration distance.")]
    public bool showDiagnostics = true;
    [Tooltip("Radius of the debug marker at the tracked index tip.")]
    public float diagMarkerRadius = 0.008f;
    [Tooltip("Interval (seconds) between diagnostic log entries.")]
    public float diagLogInterval = 0.5f;

    // ── Events ───────────────────────────────────────────────────────
    public event Action OnCalibrationComplete;
    public event Action OnNextButtonPressed;

    // ── State ────────────────────────────────────────────────────────
    private bool isActive;
    private bool calibrationDone;
    private float holdTimer;
    private Vector3 accumulatedOffset;
    private int accumulatedSamples;
    private float calibrationCompleteTime;

    // ── Visual elements (runtime-created) ────────────────────────────
    private GameObject targetSphere;
    private Renderer targetRenderer;
    private Material targetMaterial;

    private GameObject nextButton;
    private Renderer nextButtonRenderer;
    private Material nextButtonMaterial;
    private TextMeshPro nextButtonLabel;

    private TextMeshPro instructionText;

    // Diagnostics visuals
    private GameObject indexTipMarker;
    private Material indexTipMarkerMaterial;
    private LineRenderer guideLine;
    private GameObject guideLineObj;
    private float nextDiagLogTime;

    private int passthroughLayer = 31;
    private Vector3 targetWorldPos;
    private Vector3 nextButtonWorldPos;

    // Colours
    private static readonly Color IdleTargetColor = new Color(0.2f, 0.9f, 0.3f, 0.8f);
    private static readonly Color HoldingTargetColor = new Color(1.0f, 0.85f, 0.2f, 0.9f);
    private static readonly Color DoneTargetColor = new Color(0.2f, 1.0f, 0.4f, 1.0f);
    private static readonly Color NextIdleColor = new Color(0.25f, 0.55f, 0.95f, 0.85f);
    private static readonly Color NextArmedColor = new Color(0.2f, 0.8f, 1.0f, 0.95f);

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void BeginCalibration()
    {
        if (passthroughManager != null)
            passthroughLayer = passthroughManager.GetPassthroughUILayer();

        EnsureVisuals();
        PositionVisualsInFrontOfUser();

        isActive = true;
        calibrationDone = false;
        holdTimer = 0f;
        accumulatedOffset = Vector3.zero;
        accumulatedSamples = 0;

        if (wristTracker != null)
            wristTracker.ClearCalibration();

        SetInstruction("Hold your pen and touch the tip\nto the green dot.");
        SetTargetColor(IdleTargetColor);
        if (nextButton != null) nextButton.SetActive(false);

        Debug.Log("[StylusCalibration] Calibration started.");
    }

    public void Cleanup()
    {
        isActive = false;

        if (targetSphere != null) targetSphere.SetActive(false);
        if (nextButton != null) nextButton.SetActive(false);
        if (instructionText != null) instructionText.gameObject.SetActive(false);
        if (indexTipMarker != null) indexTipMarker.SetActive(false);
        if (guideLineObj != null) guideLineObj.SetActive(false);
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (!isActive) return;

        if (!calibrationDone)
            UpdateCalibrating();
        else
            UpdateWaitingForNext();
    }

    private void LateUpdate()
    {
        if (!isActive) return;

        // Billboard the instruction text toward the user.
        if (instructionText != null && instructionText.gameObject.activeSelf)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 forward = cam.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.001f) forward.Normalize();
                else forward = Vector3.forward;

                instructionText.transform.position =
                    cam.transform.position + forward * instructionDistance
                    + Vector3.up * instructionHeight;
                instructionText.transform.rotation = Quaternion.LookRotation(forward);
            }
        }

        // Billboard the Next button label toward the user.
        if (nextButton != null && nextButton.activeSelf && nextButtonLabel != null)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 lookDir = nextButton.transform.position - cam.transform.position;
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > 0.001f)
                    nextButtonLabel.transform.rotation = Quaternion.LookRotation(lookDir.normalized);
            }
        }
    }

    // ================================================================
    // CALIBRATION
    // ================================================================

    private void UpdateCalibrating()
    {
        if (wristTracker == null)
        {
            SetInstruction("Missing StylusWristTracker reference.");
            UpdateDiagnosticsVisuals(false, Vector3.zero);
            return;
        }

        // Read index tip (proxy for pen tip during calibration).
        bool tipTracked = wristTracker.TryGetIndexTipWorldPosition(out Vector3 indexTipWorld);
        UpdateDiagnosticsVisuals(tipTracked, indexTipWorld);

        if (!tipTracked)
        {
            SetInstruction("Hand not tracked.\nShow your hand to the headset.");
            ResetHold();
            SetTargetColor(IdleTargetColor);
            LogDiag("no-tip", Vector3.zero, -1f);
            return;
        }

        float distance = Vector3.Distance(indexTipWorld, targetWorldPos);
        int distanceCm = Mathf.RoundToInt(distance * 100f);

        if (distance > tipProximityThreshold)
        {
            ResetHold();
            SetTargetColor(IdleTargetColor);
            SetInstruction($"Touch the pen tip to the green dot.\nDistance: {distanceCm} cm");
            LogDiag("far", indexTipWorld, distance);
            return;
        }

        // Within proximity: accumulate wrist-local offset samples.
        if (!wristTracker.TryGetWristPose(out Vector3 wristWorldPos, out Quaternion wristWorldRot))
        {
            ResetHold();
            LogDiag("no-wrist", indexTipWorld, distance);
            return;
        }

        Vector3 sampleOffset = Quaternion.Inverse(wristWorldRot) * (targetWorldPos - wristWorldPos);
        accumulatedOffset += sampleOffset;
        accumulatedSamples++;

        holdTimer += Time.deltaTime;
        SetTargetColor(HoldingTargetColor);

        float progress = Mathf.Clamp01(holdTimer / holdDuration);
        SetInstruction($"Hold steady... {Mathf.RoundToInt(progress * 100)}%\nDistance: {distanceCm} cm");
        LogDiag($"hold {progress:F2}", indexTipWorld, distance);

        if (holdTimer >= holdDuration && accumulatedSamples > 0)
            FinalizeCalibration();
    }

    private void UpdateDiagnosticsVisuals(bool tipTracked, Vector3 indexTipWorld)
    {
        if (!showDiagnostics) return;

        if (indexTipMarker != null)
        {
            indexTipMarker.SetActive(tipTracked);
            if (tipTracked) indexTipMarker.transform.position = indexTipWorld;
        }
        if (guideLineObj != null && guideLine != null)
        {
            guideLineObj.SetActive(tipTracked);
            if (tipTracked)
            {
                guideLine.SetPosition(0, indexTipWorld);
                guideLine.SetPosition(1, targetWorldPos);
            }
        }
    }

    private void LogDiag(string tag, Vector3 tipWorld, float distance)
    {
        if (Time.time < nextDiagLogTime) return;
        nextDiagLogTime = Time.time + diagLogInterval;
        Debug.Log($"[StylusCalibration][{tag}] target={targetWorldPos} tip={tipWorld} " +
                  $"distance={(distance < 0 ? "n/a" : distance.ToString("F3") + "m")}");
    }

    private void ResetHold()
    {
        holdTimer = 0f;
        accumulatedOffset = Vector3.zero;
        accumulatedSamples = 0;
    }

    private void FinalizeCalibration()
    {
        Vector3 averagedOffset = accumulatedOffset / accumulatedSamples;
        wristTracker.SetWristOffset(averagedOffset);

        calibrationDone = true;
        calibrationCompleteTime = Time.time;

        SetTargetColor(DoneTargetColor);
        SetInstruction("Calibrated! Press Next to continue.");

        if (nextButton != null)
        {
            nextButton.SetActive(true);
            SetNextButtonColor(NextIdleColor);
        }

        Debug.Log($"[StylusCalibration] Complete. samples={accumulatedSamples} " +
                  $"offset={averagedOffset} (|offset|={averagedOffset.magnitude:F3}m)");

        OnCalibrationComplete?.Invoke();
    }

    // ================================================================
    // NEXT BUTTON
    // ================================================================

    private void UpdateWaitingForNext()
    {
        if (nextButton == null || !nextButton.activeSelf) return;

        bool armed = Time.time - calibrationCompleteTime >= nextButtonArmDelay;
        SetNextButtonColor(armed ? NextArmedColor : NextIdleColor);
        if (!armed) return;

        if (wristTracker == null) return;
        if (!wristTracker.TryGetIndexTipWorldPosition(out Vector3 indexTipWorld)) return;

        float distance = Vector3.Distance(indexTipWorld, nextButtonWorldPos);
        if (distance <= nextButtonPokeDistance)
        {
            Debug.Log("[StylusCalibration] Next button pressed.");
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
            targetSphere.name = "StylusCalibrationTarget";
            var col = targetSphere.GetComponent<Collider>();
            if (col != null) Destroy(col);
            targetSphere.transform.localScale = Vector3.one * (targetRadius * 2f);
            targetMaterial = CreateUnlitTransparentMaterial(IdleTargetColor);
            var rend = targetSphere.GetComponent<Renderer>();
            rend.material = targetMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            targetRenderer = rend;
            PassthroughManager.SetLayerRecursive(targetSphere, passthroughLayer);
        }

        if (nextButton == null)
        {
            nextButton = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nextButton.name = "StylusCalibrationNext";
            var col = nextButton.GetComponent<Collider>();
            if (col != null) Destroy(col);
            nextButton.transform.localScale = new Vector3(0.10f, 0.05f, 0.02f);
            nextButtonMaterial = CreateUnlitTransparentMaterial(NextIdleColor);
            var rend = nextButton.GetComponent<Renderer>();
            rend.material = nextButtonMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            nextButtonRenderer = rend;
            PassthroughManager.SetLayerRecursive(nextButton, passthroughLayer);

            // Label on the button
            var labelObj = new GameObject("NextLabel");
            labelObj.transform.SetParent(nextButton.transform, false);
            labelObj.layer = passthroughLayer;
            nextButtonLabel = labelObj.AddComponent<TextMeshPro>();
            nextButtonLabel.text = "Next";
            nextButtonLabel.fontSize = 0.5f;
            nextButtonLabel.alignment = TextAlignmentOptions.Center;
            nextButtonLabel.color = Color.white;
            nextButtonLabel.rectTransform.sizeDelta = new Vector2(0.12f, 0.06f);
            // Parent cube scale is non-uniform, so compensate by resetting local scale
            labelObj.transform.localScale = new Vector3(1f / 0.10f * 0.08f,
                                                       1f / 0.05f * 0.04f,
                                                       1f / 0.02f * 0.01f);
            labelObj.transform.localPosition = new Vector3(0f, 0f, -0.6f); // in front of cube
            nextButton.SetActive(false);
        }

        if (showDiagnostics && indexTipMarker == null)
        {
            indexTipMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            indexTipMarker.name = "StylusCalibrationIndexTipMarker";
            var col = indexTipMarker.GetComponent<Collider>();
            if (col != null) Destroy(col);
            indexTipMarker.transform.localScale = Vector3.one * (diagMarkerRadius * 2f);
            indexTipMarkerMaterial = CreateUnlitTransparentMaterial(new Color(1f, 0.3f, 0.3f, 0.95f));
            var rend = indexTipMarker.GetComponent<Renderer>();
            rend.material = indexTipMarkerMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            PassthroughManager.SetLayerRecursive(indexTipMarker, passthroughLayer);
            indexTipMarker.SetActive(false);
        }

        if (showDiagnostics && guideLineObj == null)
        {
            guideLineObj = new GameObject("StylusCalibrationGuideLine");
            guideLineObj.layer = passthroughLayer;
            guideLine = guideLineObj.AddComponent<LineRenderer>();
            guideLine.positionCount = 2;
            guideLine.startWidth = 0.003f;
            guideLine.endWidth = 0.003f;
            guideLine.material = CreateUnlitTransparentMaterial(new Color(1f, 1f, 0.3f, 0.7f));
            guideLine.useWorldSpace = true;
            guideLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            guideLine.receiveShadows = false;
            guideLineObj.SetActive(false);
        }

        if (instructionText == null)
        {
            var obj = new GameObject("StylusCalibrationInstruction");
            obj.layer = passthroughLayer;

            var bgObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bgObj.name = "InstructionBG";
            bgObj.transform.SetParent(obj.transform, false);
            bgObj.transform.localPosition = new Vector3(0f, 0f, 0.001f);
            bgObj.transform.localScale = new Vector3(1.4f, 0.35f, 1f);
            bgObj.layer = passthroughLayer;
            var bgCol = bgObj.GetComponent<Collider>();
            if (bgCol != null) Destroy(bgCol);
            var bgRend = bgObj.GetComponent<Renderer>();
            bgRend.material = CreateUnlitTransparentMaterial(new Color(0f, 0f, 0f, 0.6f));
            bgRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRend.receiveShadows = false;

            instructionText = obj.AddComponent<TextMeshPro>();
            instructionText.fontSize = fontSize;
            instructionText.alignment = TextAlignmentOptions.Center;
            instructionText.color = Color.white;
            instructionText.rectTransform.sizeDelta = new Vector2(1.3f, 0.4f);
            instructionText.textWrappingMode = TextWrappingModes.Normal;
            instructionText.sortingOrder = 1;
        }
    }

    private void PositionVisualsInFrontOfUser()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.001f) forward.Normalize();
        else forward = Vector3.forward;

        // Target dot: comfortable arm's reach, below eye level
        targetWorldPos = camPos + forward * targetForwardDistance + Vector3.down * targetBelowEye;
        if (targetSphere != null)
        {
            targetSphere.transform.position = targetWorldPos;
            targetSphere.transform.rotation = Quaternion.identity;
            targetSphere.SetActive(true);
        }

        // Next button: slightly forward and off to the side of the target
        Vector3 right = cam.transform.right;
        right.y = 0f;
        if (right.sqrMagnitude > 0.001f) right.Normalize();
        nextButtonWorldPos = camPos + forward * nextButtonForward
                             + Vector3.down * nextButtonBelowEye
                             + right * 0.15f;
        if (nextButton != null)
        {
            nextButton.transform.position = nextButtonWorldPos;
            nextButton.transform.rotation = Quaternion.LookRotation(forward);
        }
    }

    private void SetInstruction(string msg)
    {
        if (instructionText == null) return;
        instructionText.gameObject.SetActive(true);
        instructionText.text = msg;
    }

    private void SetTargetColor(Color c)
    {
        if (targetMaterial != null) targetMaterial.color = c;
    }

    private void SetNextButtonColor(Color c)
    {
        if (nextButtonMaterial != null) nextButtonMaterial.color = c;
    }

    private static Material CreateUnlitTransparentMaterial(Color color)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
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

    private void OnDestroy()
    {
        if (targetSphere != null) Destroy(targetSphere);
        if (nextButton != null) Destroy(nextButton);
        if (instructionText != null) Destroy(instructionText.gameObject);
        if (targetMaterial != null) Destroy(targetMaterial);
        if (nextButtonMaterial != null) Destroy(nextButtonMaterial);
        if (indexTipMarker != null) Destroy(indexTipMarker);
        if (indexTipMarkerMaterial != null) Destroy(indexTipMarkerMaterial);
        if (guideLineObj != null) Destroy(guideLineObj);
    }
}
