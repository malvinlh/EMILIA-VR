using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

/// <summary>
/// Adds hand-based login input for 3D scenes:
/// - Right-hand pinch ray selects the active login field.
/// - Handwriting recognition output is committed to that active field.
/// - A proximity hint is shown when the user is near the login area.
/// </summary>
[DisallowMultipleComponent]
public class VRLoginHandwritingBridge : MonoBehaviour
{
    private enum LoginField
    {
        Nickname,
        FullName
    }

    [Header("Login Fields")]
    [SerializeField] private TMP_InputField fullNameInput;
    [SerializeField] private TMP_InputField nicknameInput;

    [Header("Selection")]
    [SerializeField] private LoginField defaultField = LoginField.Nickname;
    [SerializeField] private Color selectedFieldColor = new Color(0.87f, 0.96f, 1f, 1f);
    [SerializeField] [Range(0.05f, 1.0f)] private float pinchProximitySelectionDistance = 0.35f;

    [Header("Right Hand Pinch")]
    [SerializeField] [Range(0.005f, 0.05f)] private float pinchCloseThreshold = 0.020f;
    [SerializeField] [Range(0.005f, 0.06f)] private float pinchOpenThreshold = 0.030f;
    [SerializeField] private float pinchRayMaxDistance = 5.5f;

    [Header("Recognition Commit")]
    [SerializeField] private bool appendRecognizedText = true;
    [SerializeField] private bool autoClearWhiteboardAfterCommit = true;
    [SerializeField] private float duplicateSuppressWindowSeconds = 0.75f;

    [Header("Proximity Hint")]
    [SerializeField] private bool showProximityHint = true;
    [SerializeField] private float hintVisibleDistance = 3.0f;
    [SerializeField] private Transform proximityAnchor;

    private RecognitionPipeline recognitionPipeline;
    private DigitalInkBridge inkBridge;
    private Whiteboard whiteboard;
    private XRHandSubsystem handSubsystem;

    private Transform cameraOffsetTransform;
    private Transform headTransform;

    private LoginField activeField;
    private bool wasPinching;
    private bool isRightHandTracked;

    private Image nicknameImage;
    private Image fullNameImage;
    private Color nicknameBaseColor = Color.white;
    private Color fullNameBaseColor = Color.white;

    private TextMeshProUGUI hintText;

    private string lastCommitNormalized = string.Empty;
    private float lastCommitTime = -999f;

    private const string Tag = "[VRLoginHandwriting]";

    public void Configure(TMP_InputField configuredFullName, TMP_InputField configuredNickname)
    {
        fullNameInput = configuredFullName;
        nicknameInput = configuredNickname;
    }

    private void Awake()
    {
        ResolveInputReferences();
        CacheFieldImages();
        EnsureHintLabel();

        activeField = defaultField;
        UpdateFieldSelectionVisuals();
    }

    private void Start()
    {
        recognitionPipeline = RecognitionPipeline.Instance ?? FindAnyObjectByType<RecognitionPipeline>();
        inkBridge = DigitalInkBridge.Instance ?? FindAnyObjectByType<DigitalInkBridge>();
        whiteboard = FindAnyObjectByType<Whiteboard>();

        ResolveCameraOffsetTransform();
        headTransform = Camera.main != null ? Camera.main.transform : null;

        NormalizeWhiteboardPensForLogin();
        SubscribeRecognition();

        if (proximityAnchor == null)
            proximityAnchor = whiteboard != null ? whiteboard.transform : transform;

        if (recognitionPipeline == null)
            Debug.LogWarning($"{Tag} RecognitionPipeline not found. Handwriting text will not be committed.");
    }

    private void OnDisable()
    {
        UnsubscribeRecognition();
    }

    private void OnDestroy()
    {
        UnsubscribeRecognition();
    }

    private void Update()
    {
        UpdateRightHandSelection();
        UpdateHintLabel();
    }

    private void ResolveInputReferences()
    {
        if (fullNameInput != null && nicknameInput != null)
            return;

        var allInputs = FindObjectsByType<TMP_InputField>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        foreach (var input in allInputs)
        {
            if (input == null)
                continue;

            string lowered = input.name.ToLowerInvariant();

            if (nicknameInput == null && lowered.Contains("nick"))
            {
                nicknameInput = input;
                continue;
            }

            if (fullNameInput == null && lowered.Contains("name"))
            {
                fullNameInput = input;
            }
        }

        if ((nicknameInput == null || fullNameInput == null) && allInputs.Length >= 2)
        {
            if (nicknameInput == null)
                nicknameInput = allInputs[0];
            if (fullNameInput == null)
                fullNameInput = allInputs[1];
        }
    }

    private void CacheFieldImages()
    {
        nicknameImage = nicknameInput != null ? nicknameInput.GetComponent<Image>() : null;
        fullNameImage = fullNameInput != null ? fullNameInput.GetComponent<Image>() : null;

        if (nicknameImage != null)
            nicknameBaseColor = nicknameImage.color;
        if (fullNameImage != null)
            fullNameBaseColor = fullNameImage.color;
    }

    private void EnsureHintLabel()
    {
        if (!showProximityHint)
            return;

        Canvas canvas = GetComponent<Canvas>();
        if (canvas == null)
            return;

        var existing = transform.Find("VRLoginHintText");
        if (existing != null)
        {
            hintText = existing.GetComponent<TextMeshProUGUI>();
            return;
        }

        var hintGo = new GameObject("VRLoginHintText", typeof(RectTransform), typeof(TextMeshProUGUI));
        hintGo.transform.SetParent(transform, false);

        var rect = (RectTransform)hintGo.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -16f);
        rect.sizeDelta = new Vector2(1280f, 200f);

        hintText = hintGo.GetComponent<TextMeshProUGUI>();
        hintText.alignment = TextAlignmentOptions.Center;
        hintText.enableWordWrapping = true;
        hintText.fontSize = 26f;
        hintText.color = new Color(0.1f, 0.24f, 0.32f, 1f);
        hintText.text = string.Empty;
        hintText.gameObject.SetActive(false);
    }

    private void UpdateHintLabel()
    {
        if (!showProximityHint || hintText == null)
            return;

        if (headTransform == null && Camera.main != null)
            headTransform = Camera.main.transform;

        if (headTransform == null || proximityAnchor == null)
        {
            hintText.gameObject.SetActive(false);
            return;
        }

        bool closeEnough = Vector3.Distance(headTransform.position, proximityAnchor.position) <= hintVisibleDistance;
        hintText.gameObject.SetActive(closeEnough);

        if (!closeEnough)
            return;

        string activeLabel = activeField == LoginField.Nickname ? "Nickname" : "Full Name";
        string trackingLine = isRightHandTracked
            ? "Right hand detected. Pinch to choose a field."
            : "Put controllers down, then raise your right hand.";

        hintText.text =
            "Handwriting Login\n" +
            "1) Pinch with RIGHT hand to choose input field\n" +
            "2) Write on whiteboard with your index finger\n" +
            "3) Press Continue when both fields are ready\n" +
            $"Active field: {activeLabel}\n" +
            trackingLine;
    }

    private void UpdateRightHandSelection()
    {
        if (handSubsystem == null || !handSubsystem.running)
            handSubsystem = WhiteboardPen.GetHandSubsystem();

        if (handSubsystem == null)
        {
            isRightHandTracked = false;
            wasPinching = false;
            return;
        }

        var rightHand = handSubsystem.rightHand;
        if (!rightHand.isTracked)
        {
            isRightHandTracked = false;
            wasPinching = false;
            return;
        }

        bool gotTip = rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tipPose);
        bool gotThumb = rightHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose);

        if (!gotTip || !gotThumb)
        {
            isRightHandTracked = false;
            wasPinching = false;
            return;
        }

        isRightHandTracked = true;

        Vector3 tipWorld = JointToWorld(tipPose.position);
        Vector3 thumbWorld = JointToWorld(thumbPose.position);

        float pinchDistance = Vector3.Distance(tipWorld, thumbWorld);
        bool isPinching = wasPinching
            ? pinchDistance < pinchOpenThreshold
            : pinchDistance < pinchCloseThreshold;

        if (isPinching && !wasPinching)
        {
            if (TryBuildSelectionRay(rightHand, tipPose, tipWorld, thumbWorld, out Ray selectionRay))
                TrySelectField(selectionRay);
        }

        wasPinching = isPinching;
    }

    private bool TryBuildSelectionRay(XRHand rightHand, Pose tipPose, Vector3 tipWorld, Vector3 thumbWorld, out Ray selectionRay)
    {
        Vector3 rayOrigin = Vector3.Lerp(tipWorld, thumbWorld, 0.5f);
        Vector3 rayDirection = Vector3.zero;

        if (TryGetJointWorld(rightHand, XRHandJointID.IndexDistal, out Vector3 distalWorld))
        {
            rayDirection = tipWorld - distalWorld;
        }
        else if (TryGetJointWorld(rightHand, XRHandJointID.IndexIntermediate, out Vector3 intermediateWorld))
        {
            rayDirection = tipWorld - intermediateWorld;
        }
        else if (TryGetJointWorld(rightHand, XRHandJointID.IndexProximal, out Vector3 proximalWorld))
        {
            rayDirection = tipWorld - proximalWorld;
        }

        if (TryGetJointWorld(rightHand, XRHandJointID.Palm, out Vector3 palmWorld))
        {
            Vector3 palmToTip = tipWorld - palmWorld;
            if (palmToTip.sqrMagnitude > 0.0001f)
            {
                Vector3 palmDirection = palmToTip.normalized;
                rayDirection = rayDirection.sqrMagnitude > 0.0001f
                    ? Vector3.Slerp(palmDirection, rayDirection.normalized, 0.7f)
                    : palmDirection;
            }
        }

        if (rayDirection.sqrMagnitude < 0.0001f)
            rayDirection = JointDirectionToWorld(tipPose.forward);

        if (rayDirection.sqrMagnitude < 0.0001f && headTransform != null)
            rayDirection = headTransform.forward;

        if (rayDirection.sqrMagnitude < 0.0001f)
        {
            selectionRay = default;
            return false;
        }

        selectionRay = new Ray(rayOrigin, rayDirection.normalized);
        return true;
    }

    private bool TryGetJointWorld(XRHand hand, XRHandJointID jointId, out Vector3 worldPosition)
    {
        if (hand.GetJoint(jointId).TryGetPose(out Pose jointPose))
        {
            worldPosition = JointToWorld(jointPose.position);
            return true;
        }

        worldPosition = Vector3.zero;
        return false;
    }

    private void TrySelectField(Ray ray)
    {
        bool hitNickname = TryHitInputField(ray, nicknameInput, out float nickDist);
        bool hitFullName = TryHitInputField(ray, fullNameInput, out float fullDist);

        LoginField nextField;
        if (hitNickname && hitFullName)
            nextField = nickDist <= fullDist ? LoginField.Nickname : LoginField.FullName;
        else if (hitNickname)
            nextField = LoginField.Nickname;
        else if (hitFullName)
            nextField = LoginField.FullName;
        else
        {
            if (!TryResolveFieldByProximity(ray.origin, out nextField))
                return;
        }

        bool changed = nextField != activeField;
        activeField = nextField;
        UpdateFieldSelectionVisuals();

        TMP_InputField target = GetActiveInputField();
        if (target != null)
            target.Select();

        if (changed)
            ResetRecognitionContextForNewField();
    }

    private bool TryResolveFieldByProximity(Vector3 rayOrigin, out LoginField resolvedField)
    {
        resolvedField = activeField;

        bool hasNicknameCenter = TryGetInputCenterWorld(nicknameInput, out Vector3 nicknameCenter);
        bool hasFullNameCenter = TryGetInputCenterWorld(fullNameInput, out Vector3 fullNameCenter);

        if (!hasNicknameCenter && !hasFullNameCenter)
            return false;

        if (hasNicknameCenter && hasFullNameCenter)
        {
            float nickDistance = Vector3.Distance(rayOrigin, nicknameCenter);
            float fullDistance = Vector3.Distance(rayOrigin, fullNameCenter);

            float bestDistance = Mathf.Min(nickDistance, fullDistance);
            if (bestDistance > pinchProximitySelectionDistance)
                return false;

            resolvedField = nickDistance <= fullDistance ? LoginField.Nickname : LoginField.FullName;
            return true;
        }

        Vector3 onlyCenter = hasNicknameCenter ? nicknameCenter : fullNameCenter;
        if (Vector3.Distance(rayOrigin, onlyCenter) > pinchProximitySelectionDistance)
            return false;

        resolvedField = hasNicknameCenter ? LoginField.Nickname : LoginField.FullName;
        return true;
    }

    private static bool TryGetInputCenterWorld(TMP_InputField input, out Vector3 centerWorld)
    {
        var rect = input != null ? input.transform as RectTransform : null;
        if (rect == null)
        {
            centerWorld = Vector3.zero;
            return false;
        }

        centerWorld = rect.TransformPoint(rect.rect.center);
        return true;
    }

    private bool TryHitInputField(Ray ray, TMP_InputField input, out float distance)
    {
        distance = float.MaxValue;
        if (input == null)
            return false;

        var rect = input.transform as RectTransform;
        if (rect == null)
            return false;

        if (!RaycastRect(ray, rect, out Vector3 _, out float hitDistance))
            return false;

        distance = hitDistance;
        return true;
    }

    private bool RaycastRect(Ray ray, RectTransform rect, out Vector3 hitPoint, out float hitDistance)
    {
        var plane = new Plane(-rect.forward, rect.position);
        if (!plane.Raycast(ray, out float distance) || distance > pinchRayMaxDistance)
        {
            hitPoint = Vector3.zero;
            hitDistance = float.MaxValue;
            return false;
        }

        hitPoint = ray.GetPoint(distance);
        Vector3 local = rect.InverseTransformPoint(hitPoint);
        Rect bounds = rect.rect;

        bool inside = local.x >= bounds.xMin && local.x <= bounds.xMax
                   && local.y >= bounds.yMin && local.y <= bounds.yMax;

        hitDistance = inside ? distance : float.MaxValue;
        return inside;
    }

    private TMP_InputField GetActiveInputField()
    {
        return activeField == LoginField.Nickname ? nicknameInput : fullNameInput;
    }

    private void UpdateFieldSelectionVisuals()
    {
        if (nicknameImage != null)
            nicknameImage.color = activeField == LoginField.Nickname ? selectedFieldColor : nicknameBaseColor;

        if (fullNameImage != null)
            fullNameImage.color = activeField == LoginField.FullName ? selectedFieldColor : fullNameBaseColor;
    }

    private void SubscribeRecognition()
    {
        if (recognitionPipeline == null)
            return;

        recognitionPipeline.OnFinalTextRecognized -= OnFinalTextRecognized;
        recognitionPipeline.OnFinalTextRecognized += OnFinalTextRecognized;
    }

    private void UnsubscribeRecognition()
    {
        if (recognitionPipeline == null)
            return;

        recognitionPipeline.OnFinalTextRecognized -= OnFinalTextRecognized;
    }

    private void OnFinalTextRecognized(string recognizedText)
    {
        string cleaned = SanitizeRecognizedText(recognizedText);
        if (string.IsNullOrEmpty(cleaned))
            return;

        string normalized = cleaned.ToLowerInvariant();
        if (normalized == lastCommitNormalized && Time.time - lastCommitTime < duplicateSuppressWindowSeconds)
            return;

        TMP_InputField target = GetActiveInputField();
        if (target == null)
            return;

        string current = target.text?.Trim() ?? string.Empty;
        string nextValue;

        if (appendRecognizedText && !string.IsNullOrEmpty(current))
            nextValue = $"{current} {cleaned}";
        else
            nextValue = cleaned;

        target.text = nextValue;
        target.ForceLabelUpdate();

        lastCommitNormalized = normalized;
        lastCommitTime = Time.time;

        if (autoClearWhiteboardAfterCommit)
            whiteboard?.ClearToBackground();
    }

    private static string SanitizeRecognizedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string collapsed = text.Replace('\n', ' ').Replace('\r', ' ');
        return collapsed.Trim();
    }

    private void ResetRecognitionContextForNewField()
    {
        inkBridge = inkBridge ?? DigitalInkBridge.Instance ?? FindAnyObjectByType<DigitalInkBridge>();

        if (inkBridge != null)
        {
            inkBridge.ClearInk();
            inkBridge.ClearPreContext();
        }

        whiteboard = whiteboard ?? FindAnyObjectByType<Whiteboard>();
        whiteboard?.ClearToBackground();
    }

    private void ResolveCameraOffsetTransform()
    {
        var xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
        {
            cameraOffsetTransform = xrOrigin.CameraFloorOffsetObject.transform;
            return;
        }

        if (Camera.main != null && Camera.main.transform.parent != null)
            cameraOffsetTransform = Camera.main.transform.parent;
    }

    private Vector3 JointToWorld(Vector3 sessionSpace)
    {
        return cameraOffsetTransform != null
            ? cameraOffsetTransform.TransformPoint(sessionSpace)
            : sessionSpace;
    }

    private Vector3 JointDirectionToWorld(Vector3 sessionSpaceDirection)
    {
        Vector3 worldDirection = cameraOffsetTransform != null
            ? cameraOffsetTransform.TransformDirection(sessionSpaceDirection)
            : sessionSpaceDirection;

        return worldDirection.normalized;
    }

    private void NormalizeWhiteboardPensForLogin()
    {
        var pens = FindObjectsByType<WhiteboardPen>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        WhiteboardPen retainedRightPen = null;

        foreach (var pen in pens)
        {
            if (pen == null)
                continue;

            if (pen.handedness != Handedness.Right)
            {
                pen.enabled = false;
                continue;
            }

            if (retainedRightPen == null)
            {
                retainedRightPen = pen;
                retainedRightPen.allowWithoutJournalSession = true;
                pen.enabled = true;
                continue;
            }

            pen.enabled = false;
        }

        if (retainedRightPen == null && pens.Length > 0)
        {
            pens[0].handedness = Handedness.Right;
            pens[0].allowWithoutJournalSession = true;
            pens[0].enabled = true;
            retainedRightPen = pens[0];
        }

        if (retainedRightPen == null)
        {
            Debug.LogWarning($"{Tag} No WhiteboardPen found. Handwriting login input will be unavailable.");
        }
    }
}
