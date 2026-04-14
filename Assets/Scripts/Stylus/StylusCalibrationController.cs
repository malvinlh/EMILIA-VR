using System;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Calibration UI for the DIY stylus. Runs during the StylusCalibration
/// session state, before table detection.
///
/// Flow:
///   1. A green target sphere appears ~35cm ahead at comfortable height.
///   2. Instruction: "Align the pen tip with the green dot, then press Confirm."
///   3. User touches pen tip to the target visually and presses the Confirm
///      button with the non-stylus hand (default: left index tip).
///   4. On Confirm: reads wrist pose once and computes wrist-to-target offset.
///      No proximity approximation — the user decides when alignment is done.
///   5. Target turns gold → "Calibrated! Press Next to continue."
///   6. User presses the Next button to advance to table detection.
///
/// Why Confirm instead of auto-proximity? The stylus tip extends past the
/// index finger tip by a variable amount depending on grip. Auto-detecting
/// proximity via IndexTip fires at the wrong moment. The user is the only one
/// who can see whether the pen tip is on the dot.
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
    [Tooltip("Which hand holds the stylus. The OTHER hand presses Confirm/Next.")]
    public Handedness stylusHand = Handedness.Right;

    [Header("Target Placement")]
    [Tooltip("Distance (m) forward from the user's head where the target appears.")]
    public float targetForwardDistance = 0.40f;
    [Tooltip("Vertical offset (m) below eye level for the target.")]
    public float targetBelowEye = 0.20f;
    [Tooltip("Radius (m) of the target sphere.")]
    public float targetRadius = 0.018f;

    [Header("Buttons")]
    [Tooltip("Forward distance for Confirm / Next buttons.")]
    public float buttonForward = 0.50f;
    [Tooltip("Vertical offset below eye level for buttons.")]
    public float buttonBelowEye = 0.05f;
    [Tooltip("Horizontal offset to the non-stylus side.")]
    public float buttonSideOffset = 0.18f;
    [Tooltip("Proximity (m) at which an index-tip poke triggers a button.")]
    public float buttonPokeDistance = 0.030f;
    [Tooltip("Cooldown after calibration before Next becomes pokeable.")]
    public float nextArmDelay = 0.6f;

    [Header("Instruction Text")]
    public float instructionForward = 0.90f;
    public float instructionHeight = 0.18f;
    public float fontSize = 0.28f;

    [Header("Diagnostics")]
    [Tooltip("Show a red marker at the non-stylus index tip so you can see where the " +
             "poke will land relative to the buttons.")]
    public bool showPokeMarker = true;
    [Tooltip("Log button arm/poke events.")]
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

    // Confirm button armed state — short delay after appearing to avoid
    // accidental poke from the hand that just entered frame.
    private bool confirmArmed;
    private float confirmArmTime;
    private float confirmArmDelay = 0.5f;

    // ================================================================
    // VISUAL ELEMENTS (runtime-created)
    // ================================================================

    private GameObject targetSphere;
    private Material targetMaterial;

    private GameObject confirmButton;
    private Material confirmMaterial;
    private TextMeshPro confirmLabel;

    private GameObject nextButton;
    private Material nextMaterial;
    private TextMeshPro nextLabel;

    private TextMeshPro instructionText;
    private GameObject instructionObj;

    // Diagnostics
    private GameObject pokeMarker;
    private Material pokeMarkerMaterial;

    private int passthroughLayer = 31;
    private Vector3 targetWorldPos;
    private Vector3 confirmButtonPos;
    private Vector3 nextButtonPos;

    private static readonly Color ColorTargetIdle   = new Color(0.20f, 0.90f, 0.30f, 0.85f);
    private static readonly Color ColorTargetDone   = new Color(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Color ColorBtnIdle      = new Color(0.25f, 0.55f, 0.95f, 0.85f);
    private static readonly Color ColorBtnArmed     = new Color(0.20f, 0.80f, 1.00f, 0.95f);
    private static readonly Color ColorBtnDisabled  = new Color(0.40f, 0.40f, 0.45f, 0.60f);

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
        confirmArmed = false;
        confirmArmTime = Time.time + confirmArmDelay;

        if (wristTracker != null)
            wristTracker.ClearCalibration();

        SetTargetColor(ColorTargetIdle);
        SetInstruction("Align the pen tip with\nthe green dot, then\npress Confirm.");

        if (confirmButton != null)
        {
            confirmButton.SetActive(true);
            SetButtonColor(confirmMaterial, ColorBtnDisabled);
        }
        if (nextButton != null) nextButton.SetActive(false);

        if (logEvents) Debug.Log("[StylusCalibration] BeginCalibration.");
    }

    public void Cleanup()
    {
        isActive = false;
        if (targetSphere   != null) targetSphere.SetActive(false);
        if (confirmButton  != null) confirmButton.SetActive(false);
        if (nextButton     != null) nextButton.SetActive(false);
        if (instructionObj != null) instructionObj.SetActive(false);
        if (pokeMarker     != null) pokeMarker.SetActive(false);
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (!isActive) return;

        // Arm confirm button after short delay.
        if (!confirmArmed && !calibrationDone && Time.time >= confirmArmTime)
        {
            confirmArmed = true;
            SetButtonColor(confirmMaterial, ColorBtnArmed);
        }

        UpdatePokeMarker();

        if (!calibrationDone)
            UpdatePreCalibration();
        else
            UpdatePostCalibration();
    }

    private void LateUpdate()
    {
        if (!isActive) return;
        BillboardInstruction();
    }

    // ================================================================
    // PRE-CALIBRATION: wait for Confirm poke
    // ================================================================

    private void UpdatePreCalibration()
    {
        if (!confirmArmed) return;

        Handedness confirmHand = stylusHand == Handedness.Right ? Handedness.Left : Handedness.Right;
        if (!TryGetIndexTip(confirmHand, out Vector3 confirmTip)) return;

        float dist = Vector3.Distance(confirmTip, confirmButtonPos);
        if (dist <= buttonPokeDistance)
            DoCalibrate();
    }

    private void DoCalibrate()
    {
        if (wristTracker == null)
        {
            SetInstruction("Missing StylusWristTracker.\nCheck inspector.");
            return;
        }
        if (!wristTracker.TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
        {
            SetInstruction("Stylus hand not tracked.\nShow your hand and try again.");
            return;
        }

        // Compute wrist → target offset in wrist-local space.
        // targetWorldPos is where the pen tip should be (the dot centre).
        Vector3 offset = Quaternion.Inverse(wristRot) * (targetWorldPos - wristPos);
        wristTracker.SetWristOffset(offset);

        calibrationDone = true;
        calibrationDoneTime = Time.time;

        SetTargetColor(ColorTargetDone);
        SetInstruction("Calibrated!\nPress Next to continue.");
        SetButtonColor(confirmMaterial, ColorBtnDisabled);
        if (confirmButton != null) confirmButton.SetActive(false);

        if (nextButton != null)
        {
            nextButton.SetActive(true);
            SetButtonColor(nextMaterial, ColorBtnDisabled);
        }

        if (logEvents)
            Debug.Log($"[StylusCalibration] Calibrated. offset={offset} " +
                      $"mag={offset.magnitude:F3}m target={targetWorldPos}");

        OnCalibrationComplete?.Invoke();
    }

    // ================================================================
    // POST-CALIBRATION: wait for Next poke
    // ================================================================

    private void UpdatePostCalibration()
    {
        bool nextArmed = Time.time - calibrationDoneTime >= nextArmDelay;
        SetButtonColor(nextMaterial, nextArmed ? ColorBtnArmed : ColorBtnDisabled);
        if (!nextArmed) return;

        Handedness confirmHand = stylusHand == Handedness.Right ? Handedness.Left : Handedness.Right;
        if (!TryGetIndexTip(confirmHand, out Vector3 confirmTip)) return;

        float dist = Vector3.Distance(confirmTip, nextButtonPos);
        if (dist <= buttonPokeDistance)
        {
            if (logEvents) Debug.Log("[StylusCalibration] Next pressed.");
            isActive = false;
            OnNextButtonPressed?.Invoke();
        }
    }

    // ================================================================
    // HAND READING
    // ================================================================

    private bool TryGetIndexTip(Handedness hand, out Vector3 worldPos)
    {
        // Temporarily set handedness on wristTracker, read, restore.
        // Simpler: just use the same subsystem lookup directly.
        worldPos = Vector3.zero;
        var subsystem = WhiteboardPen.GetHandSubsystem();
        if (subsystem == null || !subsystem.running) return false;

        XRHand xrHand = hand == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
        if (!xrHand.isTracked) return false;

        XRHandJoint joint = xrHand.GetJoint(XRHandJointID.IndexTip);
        if (!joint.TryGetPose(out Pose pose)) return false;

        // Session → world via CameraFloorOffsetObject (same as StylusWristTracker).
        var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        Transform offset = (origin != null && origin.CameraFloorOffsetObject != null)
            ? origin.CameraFloorOffsetObject.transform
            : (Camera.main != null ? Camera.main.transform.parent : null);

        worldPos = offset != null
            ? offset.TransformPoint(pose.position)
            : pose.position;
        return true;
    }

    // ================================================================
    // DIAGNOSTICS
    // ================================================================

    private void UpdatePokeMarker()
    {
        if (!showPokeMarker || pokeMarker == null) return;

        Handedness confirmHand = stylusHand == Handedness.Right ? Handedness.Left : Handedness.Right;
        bool tracked = TryGetIndexTip(confirmHand, out Vector3 tip);
        pokeMarker.SetActive(tracked);
        if (tracked) pokeMarker.transform.position = tip;
    }

    // ================================================================
    // VISUALS
    // ================================================================

    private void EnsureVisuals()
    {
        if (targetSphere == null)
        {
            targetSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            targetSphere.name = "StylusCalibTarget";
            Destroy(targetSphere.GetComponent<Collider>());
            targetSphere.transform.localScale = Vector3.one * (targetRadius * 2f);
            targetMaterial = MakeMat(ColorTargetIdle);
            ApplyMat(targetSphere, targetMaterial);
            PassthroughManager.SetLayerRecursive(targetSphere, passthroughLayer);
        }

        if (confirmButton == null)
        {
            confirmButton = MakeButton("StylusCalibConfirm", "Confirm",
                                       out confirmMaterial, out confirmLabel);
        }

        if (nextButton == null)
        {
            nextButton = MakeButton("StylusCalibNext", "Next",
                                    out nextMaterial, out nextLabel);
            nextButton.SetActive(false);
        }

        if (instructionObj == null)
        {
            instructionObj = new GameObject("StylusCalibInstruction");
            instructionObj.layer = passthroughLayer;

            // Background quad
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

        if (showPokeMarker && pokeMarker == null)
        {
            pokeMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pokeMarker.name = "StylusPokeMarker";
            Destroy(pokeMarker.GetComponent<Collider>());
            pokeMarker.transform.localScale = Vector3.one * 0.012f;
            pokeMarkerMaterial = MakeMat(new Color(1f, 0.3f, 0.3f, 0.9f));
            ApplyMat(pokeMarker, pokeMarkerMaterial);
            PassthroughManager.SetLayerRecursive(pokeMarker, passthroughLayer);
            pokeMarker.SetActive(false);
        }
    }

    private void PositionVisualsInFrontOfUser()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos  = cam.transform.position;
        Vector3 forward = cam.transform.forward; forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        // Side direction: toward the non-stylus hand.
        Vector3 side = stylusHand == Handedness.Right
            ? -cam.transform.right   // left side for left hand
            : cam.transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.001f) side = Vector3.right;
        side.Normalize();

        // Target: straight ahead at comfortable arm reach, below eye level.
        targetWorldPos = camPos + forward * targetForwardDistance + Vector3.down * targetBelowEye;
        if (targetSphere != null)
        {
            targetSphere.transform.position = targetWorldPos;
            targetSphere.SetActive(true);
        }

        // Confirm & Next buttons: to the non-stylus side.
        confirmButtonPos = camPos + forward * buttonForward
                           + Vector3.down * buttonBelowEye
                           + side * buttonSideOffset;
        nextButtonPos    = confirmButtonPos; // same slot; only one is active at a time

        if (confirmButton != null)
        {
            confirmButton.transform.position = confirmButtonPos;
            confirmButton.transform.rotation = Quaternion.LookRotation(forward);
        }
        if (nextButton != null)
        {
            nextButton.transform.position = nextButtonPos;
            nextButton.transform.rotation = Quaternion.LookRotation(forward);
        }
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
        mat = MakeMat(ColorBtnIdle);
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
        // Compensate for non-uniform button scale so text isn't stretched.
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
        r.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private void OnDestroy()
    {
        if (targetSphere    != null) Destroy(targetSphere);
        if (confirmButton   != null) Destroy(confirmButton);
        if (nextButton      != null) Destroy(nextButton);
        if (instructionObj  != null) Destroy(instructionObj);
        if (pokeMarker      != null) Destroy(pokeMarker);
        if (targetMaterial      != null) Destroy(targetMaterial);
        if (confirmMaterial     != null) Destroy(confirmMaterial);
        if (nextMaterial        != null) Destroy(nextMaterial);
        if (pokeMarkerMaterial  != null) Destroy(pokeMarkerMaterial);
    }
}
