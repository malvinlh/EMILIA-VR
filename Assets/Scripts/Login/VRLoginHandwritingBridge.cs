using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
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
    [SerializeField] private bool usePinchSelectionRay = true;
    [SerializeField] [Range(0.05f, 1.0f)] private float pinchProximitySelectionDistance = 0.35f;
    [SerializeField] [Range(-30f, 30f)] private float selectionPitchOffsetDegrees = 0f;

    [Header("Right Hand Pinch")]
    [SerializeField] [Range(0.005f, 0.05f)] private float pinchCloseThreshold = 0.020f;
    [SerializeField] [Range(0.005f, 0.06f)] private float pinchOpenThreshold = 0.030f;
    [SerializeField] private float pinchRayMaxDistance = 5.5f;

    [Header("Quest-Style Hand Ray Cursor")]
    [SerializeField] private bool useQuestStyleHandRayCursor = true;
    [SerializeField] [Range(0.75f, 12f)] private float questRayMaxDistance = 6f;
    [SerializeField] [Range(0.04f, 0.45f)] private float questClickHoldSeconds = 0.14f;
    [SerializeField] [Range(0.002f, 0.02f)] private float questRayStartWidth = 0.004f;
    [SerializeField] [Range(0.001f, 0.02f)] private float questRayEndWidth = 0.0025f;
    [SerializeField] [Range(0.005f, 0.06f)] private float questCursorWorldSize = 0.018f;
    [SerializeField] [Range(0f, 0.02f)] private float questCursorSurfaceOffset = 0.0015f;
    [SerializeField] private bool showQuestRayWhenNoHit = true;
    [SerializeField] private LayerMask questPhysicsMask = -1;
    [SerializeField] private Color questRayColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private Color questCursorRingColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private Color questCursorFillColor = new Color(1f, 1f, 1f, 0.95f);

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
    private bool isLeftHandTracked;
    private bool isRightHandTracked;

    private Image nicknameImage;
    private Image fullNameImage;
    private Color nicknameBaseColor = Color.white;
    private Color fullNameBaseColor = Color.white;

    private TextMeshProUGUI hintText;

    private string lastCommitNormalized = string.Empty;
    private float lastCommitTime = -999f;
    private float nextQuestTargetRefreshTime;

    private HandRayCursorState leftQuestRay;
    private HandRayCursorState rightQuestRay;
    private readonly List<RectRayTarget> questUiTargets = new();

    private static Material questLineMaterial;
    private static Sprite questDiscSprite;
    private static Sprite questRingSprite;

    private const string Tag = "[VRLoginHandwriting]";

    private sealed class RectRayTarget
    {
        public RectTransform rect;
        public TMP_InputField input;
        public Button button;

        public bool IsValid()
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
                return false;

            if (input != null)
                return input.isActiveAndEnabled && input.IsInteractable();

            if (button != null)
                return button.isActiveAndEnabled && button.IsInteractable();

            return false;
        }

        public int TargetId()
        {
            if (input != null)
                return input.GetInstanceID();
            if (button != null)
                return button.GetInstanceID();
            return 0;
        }
    }

    private struct QuestRayHit
    {
        public bool hasHit;
        public float distance;
        public Vector3 point;
        public Vector3 normal;
        public TMP_InputField inputField;
        public Button button;
        public Collider collider;
        public int targetId;
    }

    private sealed class HandRayCursorState
    {
        public Transform root;
        public LineRenderer line;
        public RectTransform reticleRect;
        public Image reticleRing;
        public Image reticleFill;
        public bool wasPinching;
        public bool clickSent;
        public int currentTargetId;
        public float pinchHoldTime;
    }

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

        if (useQuestStyleHandRayCursor)
        {
            EnsureEventSystemExists();
            EnsureQuestRayVisuals();
            RefreshQuestUiTargets();
        }

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
        DestroyQuestRayVisuals();
    }

    private void Update()
    {
        if (useQuestStyleHandRayCursor)
        {
            UpdateQuestStyleHandRays();
            UpdateSelectionFromFocusedInput();
        }
        else if (usePinchSelectionRay)
            UpdateRightHandSelection();
        else
            UpdateSelectionFromFocusedInput();

        UpdateHintLabel();
    }

    private void UpdateSelectionFromFocusedInput()
    {
        EventSystem currentEventSystem = EventSystem.current;
        if (currentEventSystem == null)
            return;

        GameObject selectedObject = currentEventSystem.currentSelectedGameObject;
        if (selectedObject == null)
            return;

        TMP_InputField selectedInput = selectedObject.GetComponent<TMP_InputField>();
        if (selectedInput == null)
            selectedInput = selectedObject.GetComponentInParent<TMP_InputField>();

        if (selectedInput == null)
            return;

        SetActiveFieldFromInputField(selectedInput, false);
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
        string selectionInstruction = useQuestStyleHandRayCursor
            ? "1) Aim hand ray at UI and pinch-hold until cursor fills"
            : usePinchSelectionRay
            ? "1) Pinch with RIGHT hand to choose input field"
            : "1) Aim either hand ray and pinch to click input field";
        string trackingLine = useQuestStyleHandRayCursor
            ? BuildQuestTrackingLine()
            : usePinchSelectionRay
            ? (isRightHandTracked
                ? "Right hand detected. Pinch to choose a field."
                : "Put controllers down, then raise your right hand.")
            : "Cursor ray is always visible. Pinch works as click/select.";

        hintText.text =
            "Handwriting Login\n" +
            selectionInstruction + "\n" +
            "2) Write on whiteboard with your index finger\n" +
            "3) Press Continue when both fields are ready\n" +
            $"Active field: {activeLabel}\n" +
            trackingLine;
    }

    private string BuildQuestTrackingLine()
    {
        if (isLeftHandTracked && isRightHandTracked)
            return "Both hands detected. Pinch-hold to click.";
        if (isLeftHandTracked || isRightHandTracked)
            return "One hand detected. Pinch-hold to click.";
        return "Raise either hand so Quest-style cursor can track.";
    }

    private void EnsureEventSystemExists()
    {
        if (EventSystem.current != null)
            return;

        var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        eventSystemObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
    }

    private void EnsureQuestRayVisuals()
    {
        if (leftQuestRay != null && rightQuestRay != null)
            return;

        leftQuestRay ??= CreateQuestRayState("Left");
        rightQuestRay ??= CreateQuestRayState("Right");
    }

    private void DestroyQuestRayVisuals()
    {
        DestroyQuestRayState(leftQuestRay);
        DestroyQuestRayState(rightQuestRay);
        leftQuestRay = null;
        rightQuestRay = null;
    }

    private static void DestroyQuestRayState(HandRayCursorState state)
    {
        if (state?.root != null)
            Destroy(state.root.gameObject);
    }

    private HandRayCursorState CreateQuestRayState(string handLabel)
    {
        if (questLineMaterial == null)
            questLineMaterial = BuildQuestLineMaterial();

        if (questDiscSprite == null)
            questDiscSprite = CreateCircleSprite(96, ringMode: false, ringThickness: 0f);
        if (questRingSprite == null)
            questRingSprite = CreateCircleSprite(96, ringMode: true, ringThickness: 0.18f);

        var state = new HandRayCursorState();

        var root = new GameObject($"{handLabel} Quest Hand Ray").transform;
        root.SetParent(null, false);
        state.root = root;

        var lineObject = new GameObject("RayLine");
        lineObject.transform.SetParent(root, false);
        var line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.numCapVertices = 6;
        line.numCornerVertices = 2;
        line.startWidth = questRayStartWidth;
        line.endWidth = questRayEndWidth;
        line.textureMode = LineTextureMode.Stretch;
        line.alignment = LineAlignment.View;
        line.receiveShadows = false;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.material = questLineMaterial;
        line.startColor = questRayColor;
        line.endColor = questRayColor;
        line.enabled = false;
        state.line = line;

        var reticleCanvasObject = new GameObject(
            "CursorReticle",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler)
        );
        reticleCanvasObject.transform.SetParent(root, false);

        var reticleCanvas = reticleCanvasObject.GetComponent<Canvas>();
        reticleCanvas.renderMode = RenderMode.WorldSpace;
        reticleCanvas.overrideSorting = true;
        reticleCanvas.sortingOrder = 32767;

        var scaler = reticleCanvasObject.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 100;

        var reticleRect = (RectTransform)reticleCanvasObject.transform;
        reticleRect.sizeDelta = new Vector2(100f, 100f);
        reticleRect.localScale = Vector3.one * (questCursorWorldSize / 100f);
        state.reticleRect = reticleRect;

        state.reticleRing = CreateReticleImage(
            reticleRect,
            "Ring",
            questRingSprite,
            Image.Type.Simple,
            1f,
            questCursorRingColor,
            true
        );
        state.reticleFill = CreateReticleImage(
            reticleRect,
            "Fill",
            questDiscSprite,
            Image.Type.Filled,
            0f,
            questCursorFillColor,
            false
        );
        state.reticleFill.fillMethod = Image.FillMethod.Radial360;
        state.reticleFill.fillOrigin = (int)Image.Origin360.Top;
        state.reticleFill.fillClockwise = true;

        reticleCanvasObject.SetActive(false);

        return state;
    }

    private static Image CreateReticleImage(
        RectTransform parent,
        string name,
        Sprite sprite,
        Image.Type imageType,
        float fillAmount,
        Color color,
        bool showMaskable
    )
    {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        var rect = (RectTransform)imageObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = imageObject.GetComponent<Image>();
        image.sprite = sprite;
        image.type = imageType;
        image.color = color;
        image.fillAmount = fillAmount;
        image.raycastTarget = false;
        image.maskable = showMaskable;
        return image;
    }

    private static Material BuildQuestLineMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        var material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
        return material;
    }

    private static Sprite CreateCircleSprite(int size, bool ringMode, float ringThickness)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        float radius = 0.49f;
        float innerRadius = Mathf.Clamp01(radius - ringThickness);
        float edgeSmoothness = 0.02f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                float dx = u - 0.5f;
                float dy = v - 0.5f;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float outerAlpha = Mathf.InverseLerp(radius + edgeSmoothness, radius - edgeSmoothness, distance);
                float alpha;

                if (ringMode)
                {
                    float innerAlpha = Mathf.InverseLerp(innerRadius - edgeSmoothness, innerRadius + edgeSmoothness, distance);
                    alpha = Mathf.Clamp01(outerAlpha * innerAlpha);
                }
                else
                {
                    alpha = Mathf.Clamp01(outerAlpha);
                }

                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private void RefreshQuestUiTargets()
    {
        questUiTargets.Clear();

        Canvas rootCanvas = GetComponent<Canvas>();
        if (rootCanvas == null)
            return;

        var inputFields = rootCanvas.GetComponentsInChildren<TMP_InputField>(true);
        foreach (var field in inputFields)
        {
            if (field == null)
                continue;

            var rect = field.transform as RectTransform;
            if (rect == null)
                continue;

            questUiTargets.Add(new RectRayTarget
            {
                rect = rect,
                input = field
            });
        }

        var buttons = rootCanvas.GetComponentsInChildren<Button>(true);
        foreach (var button in buttons)
        {
            if (button == null)
                continue;

            var rect = button.transform as RectTransform;
            if (rect == null)
                continue;

            questUiTargets.Add(new RectRayTarget
            {
                rect = rect,
                button = button
            });
        }
    }

    private void UpdateQuestStyleHandRays()
    {
        EnsureQuestRayVisuals();

        if (Time.time >= nextQuestTargetRefreshTime)
        {
            RefreshQuestUiTargets();
            nextQuestTargetRefreshTime = Time.time + 1f;
        }

        if (handSubsystem == null || !handSubsystem.running)
            handSubsystem = WhiteboardPen.GetHandSubsystem();

        if (handSubsystem == null || !handSubsystem.running)
        {
            isLeftHandTracked = false;
            isRightHandTracked = false;
            HideQuestHandRay(leftQuestRay);
            HideQuestHandRay(rightQuestRay);
            return;
        }

        UpdateQuestHand(handSubsystem.leftHand, leftQuestRay, Handedness.Left, ref isLeftHandTracked);
        UpdateQuestHand(handSubsystem.rightHand, rightQuestRay, Handedness.Right, ref isRightHandTracked);
    }

    private void UpdateQuestHand(XRHand hand, HandRayCursorState state, Handedness handedness, ref bool tracked)
    {
        if (state == null)
            return;

        if (!hand.isTracked)
        {
            tracked = false;
            ResetQuestPinchState(state);
            HideQuestHandRay(state);
            return;
        }

        if (!TryBuildQuestRay(hand, out Ray questRay))
        {
            tracked = false;
            ResetQuestPinchState(state);
            HideQuestHandRay(state);
            return;
        }

        tracked = true;

        QuestRayHit hit = ResolveQuestRayHit(questRay);
        Vector3 endpoint = hit.hasHit
            ? hit.point
            : questRay.origin + questRay.direction * questRayMaxDistance;

        UpdateQuestLineRenderer(state, questRay.origin, endpoint, hit.hasHit);
        UpdateQuestReticle(state, hit, questRay);

        bool isPinching = EvaluatePinch(hand, state.wasPinching);

        if (!isPinching)
        {
            ResetQuestPinchState(state);
            state.wasPinching = false;
            return;
        }

        if (!hit.hasHit || hit.targetId == 0)
        {
            state.currentTargetId = 0;
            state.clickSent = false;
            state.pinchHoldTime = 0f;
            state.reticleFill.fillAmount = 0f;
            state.wasPinching = true;
            return;
        }

        if (state.currentTargetId != hit.targetId)
        {
            state.currentTargetId = hit.targetId;
            state.pinchHoldTime = 0f;
            state.clickSent = false;
        }

        state.pinchHoldTime += Time.deltaTime;
        float fillAmount = Mathf.Clamp01(state.pinchHoldTime / Mathf.Max(questClickHoldSeconds, 0.001f));
        state.reticleFill.fillAmount = fillAmount;

        if (fillAmount >= 1f && !state.clickSent)
        {
            TriggerQuestClick(hit, handedness);
            state.clickSent = true;
        }

        state.wasPinching = true;
    }

    private void HideQuestHandRay(HandRayCursorState state)
    {
        if (state == null)
            return;

        if (state.line != null)
            state.line.enabled = false;

        if (state.reticleRect != null)
            state.reticleRect.gameObject.SetActive(false);
    }

    private void ResetQuestPinchState(HandRayCursorState state)
    {
        if (state == null)
            return;

        state.currentTargetId = 0;
        state.clickSent = false;
        state.pinchHoldTime = 0f;
        state.wasPinching = false;

        if (state.reticleFill != null)
            state.reticleFill.fillAmount = 0f;
    }

    private void UpdateQuestLineRenderer(HandRayCursorState state, Vector3 start, Vector3 end, bool hasHit)
    {
        if (state?.line == null)
            return;

        if (!hasHit && !showQuestRayWhenNoHit)
        {
            state.line.enabled = false;
            return;
        }

        state.line.enabled = true;
        state.line.startWidth = questRayStartWidth;
        state.line.endWidth = questRayEndWidth;

        Color lineColor = hasHit
            ? questRayColor
            : new Color(questRayColor.r, questRayColor.g, questRayColor.b, questRayColor.a * 0.35f);

        state.line.startColor = lineColor;
        state.line.endColor = lineColor;
        state.line.SetPosition(0, start);
        state.line.SetPosition(1, end);
    }

    private void UpdateQuestReticle(HandRayCursorState state, QuestRayHit hit, Ray questRay)
    {
        if (state?.reticleRect == null)
            return;

        if (!hit.hasHit)
        {
            state.reticleRect.gameObject.SetActive(false);
            return;
        }

        Vector3 normal = hit.normal.sqrMagnitude > 0.0001f ? hit.normal : -questRay.direction;
        Vector3 facing = -questRay.direction;
        if (facing.sqrMagnitude < 0.0001f)
            facing = headTransform != null ? headTransform.forward : Vector3.forward;

        state.reticleRect.gameObject.SetActive(true);
        state.reticleRect.position = hit.point + normal.normalized * questCursorSurfaceOffset;
        state.reticleRect.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
        state.reticleRect.localScale = Vector3.one * (questCursorWorldSize / 100f);

        if (state.reticleRing != null)
            state.reticleRing.color = questCursorRingColor;
        if (state.reticleFill != null)
            state.reticleFill.color = questCursorFillColor;
    }

    private bool TryBuildQuestRay(XRHand hand, out Ray questRay)
    {
        bool gotPalmPose = hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose);
        bool gotWristPose = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose);
        bool gotIndexTip = TryGetJointWorld(hand, XRHandJointID.IndexTip, out Vector3 indexTipWorld);
        bool gotIndexKnuckle = TryGetJointWorld(hand, XRHandJointID.IndexProximal, out Vector3 indexKnuckleWorld);

        Vector3 origin;
        Vector3 direction = Vector3.zero;

        if (gotPalmPose)
        {
            origin = JointToWorld(palmPose.position);
            direction += JointRotToWorld(palmPose.rotation) * Vector3.forward * 0.65f;
        }
        else if (gotWristPose)
        {
            origin = JointToWorld(wristPose.position);
            direction += JointRotToWorld(wristPose.rotation) * Vector3.forward * 0.65f;
        }
        else if (gotIndexTip)
        {
            origin = indexTipWorld;
        }
        else
        {
            questRay = default;
            return false;
        }

        if (gotWristPose)
            direction += JointRotToWorld(wristPose.rotation) * Vector3.forward * 0.25f;

        if (gotIndexTip && gotIndexKnuckle)
        {
            Vector3 indexDirection = (indexTipWorld - indexKnuckleWorld).normalized;
            if (indexDirection.sqrMagnitude > 0.0001f)
                direction += indexDirection * 0.75f;
        }

        if (direction.sqrMagnitude < 0.0001f)
            direction = headTransform != null ? headTransform.forward : Vector3.forward;

        questRay = new Ray(origin, direction.normalized);
        return true;
    }

    private QuestRayHit ResolveQuestRayHit(Ray ray)
    {
        QuestRayHit bestHit = default;
        bestHit.distance = questRayMaxDistance + 0.001f;

        if (TryRaycastQuestUi(ray, out QuestRayHit uiHit))
            bestHit = uiHit;

        if (Physics.Raycast(ray, out RaycastHit physicsHit, questRayMaxDistance, questPhysicsMask, QueryTriggerInteraction.Collide))
        {
            if (!bestHit.hasHit || physicsHit.distance < bestHit.distance)
            {
                bestHit.hasHit = true;
                bestHit.distance = physicsHit.distance;
                bestHit.point = physicsHit.point;
                bestHit.normal = physicsHit.normal;
                bestHit.collider = physicsHit.collider;
                bestHit.inputField = null;
                bestHit.button = null;
                bestHit.targetId = physicsHit.collider != null ? physicsHit.collider.GetInstanceID() : 0;
            }
        }

        return bestHit;
    }

    private bool TryRaycastQuestUi(Ray ray, out QuestRayHit hit)
    {
        hit = default;
        bool found = false;
        float closestDistance = questRayMaxDistance + 0.001f;

        foreach (RectRayTarget target in questUiTargets)
        {
            if (target == null || !target.IsValid())
                continue;

            if (!RaycastRect(ray, target.rect, out Vector3 hitPoint, out float distance))
                continue;

            if (distance < 0f || distance > questRayMaxDistance || distance >= closestDistance)
                continue;

            found = true;
            closestDistance = distance;

            hit.hasHit = true;
            hit.distance = distance;
            hit.point = hitPoint;
            hit.normal = -target.rect.forward;
            hit.inputField = target.input;
            hit.button = target.button;
            hit.collider = null;
            hit.targetId = target.TargetId();
        }

        return found;
    }

    private bool EvaluatePinch(XRHand hand, bool wasPinchingInPreviousFrame)
    {
        bool gotTip = hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tipPose);
        bool gotThumb = hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose);

        if (!gotTip || !gotThumb)
            return false;

        Vector3 tipWorld = JointToWorld(tipPose.position);
        Vector3 thumbWorld = JointToWorld(thumbPose.position);
        float pinchDistance = Vector3.Distance(tipWorld, thumbWorld);

        return wasPinchingInPreviousFrame
            ? pinchDistance < pinchOpenThreshold
            : pinchDistance < pinchCloseThreshold;
    }

    private void TriggerQuestClick(QuestRayHit hit, Handedness handedness)
    {
        if (hit.inputField != null)
        {
            SetActiveFieldFromInputField(hit.inputField, true);
            return;
        }

        if (hit.button != null)
        {
            hit.button.Select();
            hit.button.onClick?.Invoke();
            return;
        }

        if (hit.collider != null && EventSystem.current != null)
        {
            var eventData = new PointerEventData(EventSystem.current)
            {
                pointerId = handedness == Handedness.Left ? -10 : -11,
                pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = hit.collider.gameObject,
                    worldPosition = hit.point,
                    worldNormal = hit.normal
                }
            };
            ExecuteEvents.ExecuteHierarchy(hit.collider.gameObject, eventData, ExecuteEvents.pointerClickHandler);
        }
    }

    public bool SetActiveFieldFromInputField(TMP_InputField selectedInput, bool focusInputField)
    {
        if (selectedInput == null)
            return false;

        LoginField nextField;
        if (selectedInput == nicknameInput)
            nextField = LoginField.Nickname;
        else if (selectedInput == fullNameInput)
            nextField = LoginField.FullName;
        else
            return false;

        bool changed = nextField != activeField;
        activeField = nextField;
        UpdateFieldSelectionVisuals();

        if (focusInputField)
        {
            EnsureEventSystemExists();
            selectedInput.Select();
            selectedInput.ActivateInputField();
        }

        if (changed)
            ResetRecognitionContextForNewField();

        return true;
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
            if (TryBuildSelectionRay(rightHand, tipWorld, thumbWorld, out Ray selectionRay))
                TrySelectField(selectionRay);
        }

        wasPinching = isPinching;
    }

    private bool TryBuildSelectionRay(XRHand rightHand, Vector3 tipWorld, Vector3 thumbWorld, out Ray selectionRay)
    {
        // Use wrist/palm joint rotation for controller-like ray aiming.
        // Tilting the wrist steers the ray fluidly, exactly like a controller.
        bool gotWristPose = rightHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose);
        bool gotPalmPose  = rightHand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose);

        Vector3 rayOrigin;
        Vector3 rayDirection;

        if (gotWristPose)
        {
            rayOrigin    = JointToWorld(wristPose.position);
            rayDirection = JointRotToWorld(wristPose.rotation) * Vector3.forward;

            if (gotPalmPose)
            {
                Vector3 palmForward = JointRotToWorld(palmPose.rotation) * Vector3.forward;
                rayDirection = Vector3.Slerp(rayDirection, palmForward, 0.5f);
            }
        }
        else if (gotPalmPose)
        {
            rayOrigin    = JointToWorld(palmPose.position);
            rayDirection = JointRotToWorld(palmPose.rotation) * Vector3.forward;
        }
        else
        {
            rayOrigin    = tipWorld;
            rayDirection = headTransform != null ? headTransform.forward : Vector3.forward;
        }

        if (rayDirection.sqrMagnitude < 0.0001f)
        {
            selectionRay = default;
            return false;
        }

        rayDirection.Normalize();

        if (Mathf.Abs(selectionPitchOffsetDegrees) > 0.001f)
        {
            Vector3 up = headTransform != null ? headTransform.up : Vector3.up;
            Vector3 right = Vector3.Cross(up, rayDirection).normalized;
            if (right.sqrMagnitude < 0.0001f)
                right = Vector3.right;

            rayDirection = Quaternion.AngleAxis(selectionPitchOffsetDegrees, right) * rayDirection;
            rayDirection.Normalize();
        }

        selectionRay = new Ray(rayOrigin, rayDirection);
        return true;
    }

    private Quaternion JointRotToWorld(Quaternion sessionSpaceRot)
    {
        return cameraOffsetTransform != null
            ? cameraOffsetTransform.rotation * sessionSpaceRot
            : sessionSpaceRot;
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
            if (!TryResolveFieldByRayProximity(ray, out nextField))
            {
                if (!TryResolveFieldByProximity(ray.origin, out nextField))
                    return;
            }
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

    private bool TryResolveFieldByRayProximity(Ray ray, out LoginField resolvedField)
    {
        resolvedField = activeField;

        bool hasNicknameCenter = TryGetInputCenterWorld(nicknameInput, out Vector3 nicknameCenter);
        bool hasFullNameCenter = TryGetInputCenterWorld(fullNameInput, out Vector3 fullNameCenter);

        float nicknameLateralDistance = float.MaxValue;
        float fullNameLateralDistance = float.MaxValue;

        bool canUseNickname = hasNicknameCenter &&
            TryComputeRayLateralDistance(ray, nicknameInput, nicknameCenter, out nicknameLateralDistance);
        bool canUseFullName = hasFullNameCenter &&
            TryComputeRayLateralDistance(ray, fullNameInput, fullNameCenter, out fullNameLateralDistance);

        if (!canUseNickname && !canUseFullName)
            return false;

        if (canUseNickname && canUseFullName)
        {
            resolvedField = nicknameLateralDistance <= fullNameLateralDistance
                ? LoginField.Nickname
                : LoginField.FullName;
            return true;
        }

        resolvedField = canUseNickname ? LoginField.Nickname : LoginField.FullName;
        return true;
    }

    private bool TryComputeRayLateralDistance(Ray ray, TMP_InputField input, Vector3 centerWorld, out float lateralDistance)
    {
        lateralDistance = float.MaxValue;

        var rect = input != null ? input.transform as RectTransform : null;
        if (rect == null)
            return false;

        float forwardDistance = Vector3.Dot(centerWorld - ray.origin, ray.direction);
        if (forwardDistance < 0f || forwardDistance > pinchRayMaxDistance)
            return false;

        Vector3 closestPoint = ray.origin + ray.direction * forwardDistance;
        lateralDistance = Vector3.Distance(centerWorld, closestPoint);

        float worldRadius = EstimateRectWorldRadius(rect);
        float maxAllowedDistance = worldRadius + pinchProximitySelectionDistance;
        return lateralDistance <= maxAllowedDistance;
    }

    private static float EstimateRectWorldRadius(RectTransform rect)
    {
        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);

        float width = Vector3.Distance(corners[0], corners[3]);
        float height = Vector3.Distance(corners[0], corners[1]);
        return 0.5f * Mathf.Sqrt(width * width + height * height);
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
