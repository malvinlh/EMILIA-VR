using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR;
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
    [SerializeField] private Button backspaceButton;
    [SerializeField] [Range(0f, 1f)] private float backspaceActivationCooldownSeconds = 0.25f;

    [Header("Selection")]
    [SerializeField] private LoginField defaultField = LoginField.Nickname;
    [SerializeField] private Color selectedFieldColor = new Color(0.87f, 0.96f, 1f, 1f);
    [SerializeField] private bool usePinchSelectionRay = false;
    [SerializeField] [Range(0.05f, 1.0f)] private float pinchProximitySelectionDistance = 0.35f;
    [SerializeField] [Range(-30f, 30f)] private float selectionPitchOffsetDegrees = 0f;

    [Header("Right Hand Pinch")]
    [SerializeField] [Range(0.005f, 0.05f)] private float pinchCloseThreshold = 0.020f;
    [SerializeField] [Range(0.005f, 0.06f)] private float pinchOpenThreshold = 0.030f;
    [SerializeField] private float pinchRayMaxDistance = 5.5f;

    [Header("Quest-Style Hand Ray Cursor")]
    [SerializeField] private bool useQuestStyleHandRayCursor = false;
    [SerializeField] [Range(0.75f, 12f)] private float questRayMaxDistance = 6f;
    [SerializeField] [Range(0.04f, 0.45f)] private float questClickHoldSeconds = 0.14f;
    [SerializeField] [Range(0.002f, 0.02f)] private float questRayStartWidth = 0.004f;
    [SerializeField] [Range(0.001f, 0.02f)] private float questRayEndWidth = 0.0025f;
    [SerializeField] [Range(0.005f, 0.06f)] private float questCursorWorldSize = 0.018f;
    [SerializeField] [Range(0f, 0.02f)] private float questCursorSurfaceOffset = 0.0015f;
    [SerializeField] [Range(0f, 0.5f)] private float handRayLineStartOffset = 0.15f;
    [SerializeField] private bool showQuestRayWhenNoHit = true;
    [SerializeField] private LayerMask questPhysicsMask = -1;
    [SerializeField] private Color questRayColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private Color questCursorRingColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private Color questCursorFillColor = new Color(1f, 1f, 1f, 0.95f);

    [Header("Quest-Style Controller Ray Cursor")]
    [SerializeField] private bool useQuestStyleControllerRayCursor = true;
    [SerializeField] [Range(0.75f, 12f)] private float controllerRayMaxDistance = 8f;
    [SerializeField] [Range(0.04f, 0.45f)] private float controllerClickHoldSeconds = 0.12f;
    [SerializeField] [Range(0f, 1f)] private float controllerPressThreshold = 0.65f;
    [SerializeField] [Range(0f, 1f)] private float controllerReleaseThreshold = 0.35f;
    [SerializeField] [Range(-20f, 20f)] private float controllerRayPitchOffsetDegrees = 0f;
    [SerializeField] [Range(0f, 40f)] private float controllerRaySmoothing = 16f;
    [SerializeField] [Range(0f, 1.5f)] private float controllerRayAngularDeadzoneDegrees = 0.22f;
    [SerializeField] [Range(0f, 0.02f)] private float controllerRayPositionDeadzone = 0.0015f;
    [SerializeField] private bool disableBuiltInXriControllerRayWhenCustomRayIsActive = true;

    [Header("Ray Stabilization")]
    // System aim pose (XR_EXT_hand_interaction) is already pre-filtered by Meta's runtime,
    // so only light additional smoothing is needed — lower value = more lag, higher = more responsive.
    [SerializeField] [Range(0f, 80f)] private float questSystemPoseSmoothing = 40f;
    // Joint-derived fallback (when system pose is unavailable) needs heavier smoothing.
    [SerializeField] [Range(0f, 80f)] private float questFreeAimSmoothing = 18f;
    [SerializeField] private bool preferOpenXRSystemHandPointerPose = true;
    [SerializeField] [Range(0f, 1f)] private float handRayTipMidpointWeight = 0.0f;
    [SerializeField] [Range(0f, 0.12f)] private float handRayDetachDistance = 0.028f;
    [SerializeField] [Range(0f, 1f)] private float handRayPalmOriginBlend = 0.82f;
    [SerializeField] [Range(0f, 1f)] private float handRayPalmDirectionBlend = 0.72f;

    [Header("Recognition Commit")]
    [SerializeField] private bool appendRecognizedText = true;
    [SerializeField] private bool autoClearWhiteboardAfterCommit = true;
    [SerializeField] private float duplicateSuppressWindowSeconds = 0.75f;

    // Proximity hint is temporarily disabled.
    // [Header("Proximity Hint")]
    // [SerializeField] private bool showProximityHint = true;
    // [SerializeField] private float hintVisibleDistance = 3.0f;
    // [SerializeField] private Transform proximityAnchor;

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
    private bool isLeftControllerTracked;
    private bool isRightControllerTracked;

    private Image nicknameImage;
    private Image fullNameImage;
    private Color nicknameBaseColor = Color.white;
    private Color fullNameBaseColor = Color.white;

    // private TextMeshProUGUI hintText;

    private string lastCommitNormalized = string.Empty;
    private float lastCommitTime = -999f;
    private float nextQuestTargetRefreshTime;
    private bool builtInXriControllerRayDisabled;

    private HandRayCursorState leftQuestRay;
    private HandRayCursorState rightQuestRay;
    private HandRayCursorState leftControllerQuestRay;
    private HandRayCursorState rightControllerQuestRay;
    private readonly List<RectRayTarget> questUiTargets = new();
    private readonly List<InputDevice> leftControllerDevices = new();
    private readonly List<InputDevice> rightControllerDevices = new();
    private readonly List<InputDevice> leftHandTrackingDevices = new();
    private readonly List<InputDevice> rightHandTrackingDevices = new();

    private static Material questLineMaterial;
    private static Sprite questDiscSprite;
    private static Sprite questRingSprite;

    private Vector3 _prevCameraOffsetPos;
    private bool _hasPrevCameraOffsetPos;

    private const float BackspacePokeHoverDist = 0.04f;  // 4 cm outer zone
    private const float BackspacePokeFireDist  = 0.012f; // 12 mm fire zone
    private readonly bool[] _backspacePokeInZone   = new bool[2]; // [0]=left [1]=right
    private readonly bool[] _backspacePokeWasClose = new bool[2];
    private float _nextBackspaceActivationTime;

    private const string Tag = "[VRLoginHandwriting]";
    private const int LeftHandPointerId = -10;
    private const int RightHandPointerId = -11;
    private const int LeftControllerPointerId = -20;
    private const int RightControllerPointerId = -21;
    private static readonly InputFeatureUsage<Vector3> PointerPositionUsage = new InputFeatureUsage<Vector3>("PointerPosition");
    private static readonly InputFeatureUsage<Quaternion> PointerRotationUsage = new InputFeatureUsage<Quaternion>("PointerRotation");

    private bool IsQuestStylePointerEnabled => useQuestStyleControllerRayCursor;

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
        public float clickHoldDuration;
        public bool hasSmoothedPose;
        public Vector3 smoothedOrigin;
        public Vector3 smoothedDirection;
        public bool pinchDirectionLocked;
        public Vector3 pinchLockedOrigin;
        public Vector3 pinchLockedDirection;
    }

    public void Configure(TMP_InputField configuredFullName, TMP_InputField configuredNickname)
    {
        fullNameInput = configuredFullName;
        nicknameInput = configuredNickname;
    }

    private void Awake()
    {
        EnforceControllerOnlyInteractionMode();
        ResolveInputReferences();
        CacheFieldImages();
        // EnsureHintLabel();

        activeField = defaultField;
        UpdateFieldSelectionVisuals();

        if (fullNameInput != null)  fullNameInput.shouldHideMobileInput = true;
        if (nicknameInput != null)  nicknameInput.shouldHideMobileInput = true;
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

        if (backspaceButton)
            backspaceButton.onClick.AddListener(OnBackspaceClicked);

        if (IsQuestStylePointerEnabled)
        {
            EnsureEventSystemExists();
            EnsureQuestRayVisuals();
            RefreshQuestUiTargets();
            DisableBuiltInXriControllerRayIfNeeded();
        }

        // if (proximityAnchor == null)
        //     proximityAnchor = whiteboard != null ? whiteboard.transform : transform;

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
        if (IsQuestStylePointerEnabled)
        {
            UpdateSelectionFromFocusedInput();
        }
        else
            UpdateSelectionFromFocusedInput();

        // UpdateHintLabel();
    }

    private void LateUpdate()
    {
        // Update rays in LateUpdate so visuals follow latest tracked poses in the frame.
        if (IsQuestStylePointerEnabled)
            UpdateQuestStyleHandRays();

        if (backspaceButton)
            UpdateBackspacePoke();
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

#if false

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

        bool controllerActive = isLeftControllerTracked || isRightControllerTracked;

        string activeLabel = activeField == LoginField.Nickname ? "Nickname" : "Full Name";
        string selectionInstruction = IsQuestStylePointerEnabled
            ? "1) Aim controller ray and hold trigger until cursor fills"
            : usePinchSelectionRay
            ? "1) Pinch with RIGHT hand to choose input field"
            : "1) Aim either hand ray and pinch to click input field";
        string trackingLine = IsQuestStylePointerEnabled
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
        if (isLeftControllerTracked && isRightControllerTracked)
            return "Both controllers detected. Hold trigger to click.";
        if (isLeftControllerTracked || isRightControllerTracked)
            return "Controller detected. Hold trigger to click.";
        return "Controller ray ready. Hands are handwriting-only in this mode.";
    }

#endif

    private void EnforceControllerOnlyInteractionMode()
    {
        useQuestStyleHandRayCursor = false;
        usePinchSelectionRay = false;
    }

    private void EnsureEventSystemExists()
    {
        if (EventSystem.current != null)
            return;

        var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        eventSystemObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
    }

    private static readonly FieldInfo TmpKeyboardField = typeof(TMP_InputField)
        .GetField("m_Keyboard", BindingFlags.NonPublic | BindingFlags.Instance);

    private static void SuppressKeyboardAfterActivation(TMP_InputField field)
    {
        if (TmpKeyboardField?.GetValue(field) is not TouchScreenKeyboard keyboard)
            return;
        keyboard.active = false;
        TmpKeyboardField.SetValue(field, null);
    }

    private void DisableBuiltInXriControllerRayIfNeeded()
    {
        if (!useQuestStyleControllerRayCursor || !disableBuiltInXriControllerRayWhenCustomRayIsActive || builtInXriControllerRayDisabled)
            return;

        int disabledCount = 0;
        int matchedBuiltInRayObjects = 0;
        int deactivatedBuiltInRayObjects = 0;
        var behaviours = FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null || behaviour == this)
                continue;

            string fullName = behaviour.GetType().FullName;
            if (string.IsNullOrEmpty(fullName))
                continue;

            if (!IsBuiltInXriControllerRayType(fullName) || !behaviour.enabled)
                continue;

            behaviour.enabled = false;
            disabledCount++;
        }

        var allTransforms = FindObjectsByType<Transform>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        foreach (Transform transformRef in allTransforms)
        {
            if (transformRef == null)
                continue;

            string objectName = transformRef.name;
            if (string.IsNullOrEmpty(objectName))
                continue;

            bool isLikelyBuiltInControllerRayObject =
                objectName.Equals("Left Hand Cursor Ray", StringComparison.OrdinalIgnoreCase) ||
                objectName.Equals("Right Hand Cursor Ray", StringComparison.OrdinalIgnoreCase) ||
                objectName.IndexOf("Ray Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                objectName.IndexOf("Near Far", StringComparison.OrdinalIgnoreCase) >= 0;

            bool isCustomQuestRayObject =
                objectName.IndexOf("Quest Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                objectName.Equals("RayLine", StringComparison.OrdinalIgnoreCase) ||
                objectName.Equals("CursorReticle", StringComparison.OrdinalIgnoreCase);

            if (!isLikelyBuiltInControllerRayObject || isCustomQuestRayObject)
                continue;

            matchedBuiltInRayObjects++;

            if (!transformRef.gameObject.activeSelf)
                continue;

            transformRef.gameObject.SetActive(false);
            deactivatedBuiltInRayObjects++;
        }

        if (disabledCount > 0 || deactivatedBuiltInRayObjects > 0)
        {
            Debug.Log($"{Tag} Disabled built-in XRI controller ray artifacts (components: {disabledCount}, objects: {deactivatedBuiltInRayObjects}) so custom controller ray takes over.");
        }

        // Stop rescanning once built-in artifacts are confirmed (disabled now or already inactive).
        if (disabledCount > 0 || matchedBuiltInRayObjects > 0)
            builtInXriControllerRayDisabled = true;
    }

    private static bool IsBuiltInXriControllerRayType(string fullTypeName)
    {
        string lowered = fullTypeName.ToLowerInvariant();
        if (!lowered.Contains("unityengine.xr.interaction.toolkit"))
            return false;

        return lowered.Contains("nearfarinteractor") ||
               lowered.Contains("xrrayinteractor") ||
               lowered.Contains("interactorlinevisual") ||
               lowered.Contains("interactorreticlevisual");
    }

    private void EnsureQuestRayVisuals()
    {
        DestroyQuestRayState(leftQuestRay);
        DestroyQuestRayState(rightQuestRay);
        leftQuestRay = null;
        rightQuestRay = null;

        if (useQuestStyleControllerRayCursor)
        {
            leftControllerQuestRay ??= CreateQuestRayState("Left Controller");
            rightControllerQuestRay ??= CreateQuestRayState("Right Controller");
        }
    }

    private void DestroyQuestRayVisuals()
    {
        DestroyQuestRayState(leftQuestRay);
        DestroyQuestRayState(rightQuestRay);
        DestroyQuestRayState(leftControllerQuestRay);
        DestroyQuestRayState(rightControllerQuestRay);
        leftQuestRay = null;
        rightQuestRay = null;
        leftControllerQuestRay = null;
        rightControllerQuestRay = null;
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

        var root = new GameObject($"{handLabel} Quest Ray").transform;
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
        CompensateSmoothedStatesForRigMovement();
        EnsureQuestRayVisuals();
        DisableBuiltInXriControllerRayIfNeeded();

        if (Time.time >= nextQuestTargetRefreshTime)
        {
            RefreshQuestUiTargets();
            nextQuestTargetRefreshTime = Time.time + 1f;
        }

        if (useQuestStyleControllerRayCursor)
        {
            UpdateQuestController(isLeftController: true, leftControllerQuestRay, LeftControllerPointerId, ref isLeftControllerTracked);
            UpdateQuestController(isLeftController: false, rightControllerQuestRay, RightControllerPointerId, ref isRightControllerTracked);
        }
        else
        {
            isLeftControllerTracked = false;
            isRightControllerTracked = false;
            HideQuestHandRay(leftControllerQuestRay);
            HideQuestHandRay(rightControllerQuestRay);
        }

        // Hand rays are disabled in controller-only mode.
        isLeftHandTracked = false;
        isRightHandTracked = false;
        wasPinching = false;
        ResetQuestPinchState(leftQuestRay);
        ResetQuestPinchState(rightQuestRay);
        HideQuestHandRay(leftQuestRay);
        HideQuestHandRay(rightQuestRay);
    }

    private void UpdateQuestController(bool isLeftController, HandRayCursorState state, int pointerId, ref bool tracked)
    {
        if (state == null)
            return;

        if (!TryGetControllerRay(isLeftController, out Ray controllerRay, out InputDevice controllerDevice))
        {
            tracked = false;
            ResetQuestPinchState(state);
            state.hasSmoothedPose = false;
            HideQuestHandRay(state);
            return;
        }

        tracked = true;

        controllerRay = StabilizeControllerRay(state, controllerRay);

        QuestRayHit hit = ResolveQuestRayHit(controllerRay, controllerRayMaxDistance);
        Vector3 endpoint = hit.hasHit
            ? hit.point
            : controllerRay.origin + controllerRay.direction * controllerRayMaxDistance;

        UpdateQuestLineRenderer(state, controllerRay.origin, endpoint, hit.hasHit);
        UpdateQuestReticle(state, hit, controllerRay);

        state.clickHoldDuration = controllerClickHoldSeconds;
        bool isPressing = EvaluateControllerPress(controllerDevice, state.wasPinching);
        HandleQuestPressAndClick(state, isPressing, hit, pointerId);
    }

    private bool TryGetControllerRay(bool isLeftController, out Ray controllerRay, out InputDevice controllerDevice)
    {
        controllerRay = default;
        controllerDevice = default;

        if (!TryGetControllerDevice(isLeftController, out controllerDevice))
            return false;

        bool tracked = true;
        if (controllerDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isTrackedValue))
            tracked = isTrackedValue;

        if (!tracked)
            return false;

        bool gotPointerPosition = controllerDevice.TryGetFeatureValue(PointerPositionUsage, out Vector3 pointerPosition);
        bool gotPointerRotation = controllerDevice.TryGetFeatureValue(PointerRotationUsage, out Quaternion pointerRotation);

        bool gotGripPosition = controllerDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 gripPosition);
        bool gotGripRotation = controllerDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion gripRotation);

        Vector3 localPosition;
        Quaternion localRotation;

        if (gotPointerPosition && gotPointerRotation)
        {
            localPosition = pointerPosition;
            localRotation = pointerRotation;
        }
        else if (gotGripPosition && gotGripRotation)
        {
            localPosition = gripPosition;
            localRotation = gripRotation;
        }
        else
        {
            return false;
        }

        Vector3 worldPosition = JointToWorld(localPosition);
        Vector3 worldDirection = JointRotToWorld(localRotation) * Vector3.forward;
        controllerRay = ApplyControllerPitchOffset(new Ray(worldPosition, worldDirection.normalized));
        return true;
    }

    private bool TryGetControllerDevice(bool isLeftController, out InputDevice device)
    {
        var characteristics = InputDeviceCharacteristics.HeldInHand |
                              InputDeviceCharacteristics.Controller |
                              InputDeviceCharacteristics.TrackedDevice |
                              (isLeftController ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right);

        List<InputDevice> devices = isLeftController ? leftControllerDevices : rightControllerDevices;
        devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(characteristics, devices);

        if (devices.Count == 0)
        {
            device = default;
            return false;
        }

        device = devices[0];
        return device.isValid;
    }

    private bool TryGetHandTrackingDevice(bool isLeftHand, out InputDevice device)
    {
        var characteristics = InputDeviceCharacteristics.HandTracking |
                              InputDeviceCharacteristics.TrackedDevice |
                              (isLeftHand ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right);

        List<InputDevice> devices = isLeftHand ? leftHandTrackingDevices : rightHandTrackingDevices;
        devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(characteristics, devices);

        if (devices.Count == 0)
        {
            device = default;
            return false;
        }

        device = devices[0];
        return device.isValid;
    }

    private bool TryGetSystemHandPointerRay(bool isLeftHand, out Ray ray)
    {
        ray = default;
        if (!preferOpenXRSystemHandPointerPose)
            return false;

        if (!TryGetHandTrackingDevice(isLeftHand, out InputDevice handDevice))
            return false;

        if (handDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked)
            return false;

        bool gotPointerPosition = handDevice.TryGetFeatureValue(PointerPositionUsage, out Vector3 pointerPosition);
        bool gotPointerRotation = handDevice.TryGetFeatureValue(PointerRotationUsage, out Quaternion pointerRotation);
        if (!gotPointerPosition || !gotPointerRotation)
            return false;

        Vector3 worldOrigin = JointToWorld(pointerPosition);
        Vector3 worldDirection = JointRotToWorld(pointerRotation) * Vector3.forward;
        if (worldDirection.sqrMagnitude < 0.0001f)
            return false;

        worldDirection.Normalize();
        worldOrigin += worldDirection * handRayDetachDistance;
        ray = new Ray(worldOrigin, worldDirection);
        return true;
    }

    private bool EvaluateControllerPress(InputDevice device, bool wasPressedPreviously)
    {
        float analogValue = 0f;
        bool hasAnalog = device.TryGetFeatureValue(CommonUsages.trigger, out analogValue);

        if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButton) && triggerButton)
            analogValue = Mathf.Max(analogValue, 1f);

        if (!hasAnalog && device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton) && primaryButton)
            analogValue = 1f;

        float pressThreshold = Mathf.Clamp01(Mathf.Max(controllerPressThreshold, controllerReleaseThreshold + 0.02f));
        float releaseThreshold = Mathf.Clamp01(Mathf.Min(controllerReleaseThreshold, pressThreshold - 0.01f));

        return wasPressedPreviously
            ? analogValue >= releaseThreshold
            : analogValue >= pressThreshold;
    }

    private Ray ApplyControllerPitchOffset(Ray ray)
    {
        if (Mathf.Abs(controllerRayPitchOffsetDegrees) <= 0.001f)
            return ray;

        Vector3 up = headTransform != null ? headTransform.up : Vector3.up;
        Vector3 rightAxis = Vector3.Cross(up, ray.direction).normalized;
        if (rightAxis.sqrMagnitude < 0.0001f)
            rightAxis = Vector3.right;

        Vector3 adjustedDirection = Quaternion.AngleAxis(controllerRayPitchOffsetDegrees, rightAxis) * ray.direction;
        return new Ray(ray.origin, adjustedDirection.normalized);
    }

    private void UpdateQuestHand(XRHand hand, HandRayCursorState state, int pointerId, ref bool tracked)
    {
        if (state == null)
            return;

        if (!hand.isTracked)
        {
            tracked = false;
            ResetQuestPinchState(state);
            state.hasSmoothedPose = false;
            HideQuestHandRay(state);
            return;
        }

        bool isPinching = EvaluatePinch(hand, state.wasPinching);
        bool isLeftHand = pointerId == LeftHandPointerId;

        // Prefer the OpenXR hand-interaction aim pose (XR_EXT_hand_interaction).
        // Meta's runtime pre-filters this pose, so only light additional smoothing is needed.
        // Fall back to joint-derived pose when the system pose is unavailable.
        bool gotSystemPose = TryGetSystemHandPointerRay(isLeftHand, out Ray rawRay);
        if (!gotSystemPose && !TryBuildHandRayFromJoints(hand, out rawRay))
        {
            tracked = false;
            ResetQuestPinchState(state);
            state.hasSmoothedPose = false;
            HideQuestHandRay(state);
            return;
        }

        tracked = true;

        float smoothing = gotSystemPose ? questSystemPoseSmoothing : questFreeAimSmoothing;
        Ray questRay = StabilizeQuestRay(state, rawRay, smoothing);

        QuestRayHit hit = ResolveQuestRayHit(questRay, questRayMaxDistance);
        Vector3 endpoint = hit.hasHit
            ? hit.point
            : questRay.origin + questRay.direction * questRayMaxDistance;

        // Start the line ahead of the aim origin to visually separate it from the hand (Quest 3 style).
        Vector3 lineStart = questRay.origin + questRay.direction * handRayLineStartOffset;
        UpdateQuestLineRenderer(state, lineStart, endpoint, hit.hasHit);
        UpdateQuestReticle(state, hit, questRay);

        state.clickHoldDuration = questClickHoldSeconds;
        HandleQuestPressAndClick(state, isPinching, hit, pointerId);
    }

    private void HandleQuestPressAndClick(HandRayCursorState state, bool isPressing, QuestRayHit hit, int pointerId)
    {
        if (state == null)
            return;

        if (!isPressing)
        {
            ResetQuestPinchState(state);
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
        float holdSeconds = Mathf.Max(state.clickHoldDuration, 0.001f);
        float fillAmount = Mathf.Clamp01(state.pinchHoldTime / holdSeconds);
        state.reticleFill.fillAmount = fillAmount;

        if (fillAmount >= 1f && !state.clickSent)
        {
            TriggerQuestClick(hit, pointerId);
            state.clickSent = true;
        }

        state.wasPinching = true;
    }

    private void CompensateSmoothedStatesForRigMovement()
    {
        if (cameraOffsetTransform == null)
            return;

        Vector3 currentPos = cameraOffsetTransform.position;
        if (_hasPrevCameraOffsetPos)
        {
            Vector3 delta = currentPos - _prevCameraOffsetPos;
            if (delta.sqrMagnitude > 1e-8f)
            {
                ShiftSmoothedOrigin(leftQuestRay, delta);
                ShiftSmoothedOrigin(rightQuestRay, delta);
                ShiftSmoothedOrigin(leftControllerQuestRay, delta);
                ShiftSmoothedOrigin(rightControllerQuestRay, delta);
            }
        }
        _prevCameraOffsetPos = currentPos;
        _hasPrevCameraOffsetPos = true;
    }

    private static void ShiftSmoothedOrigin(HandRayCursorState state, Vector3 delta)
    {
        if (state != null && state.hasSmoothedPose)
            state.smoothedOrigin += delta;
    }

    private Ray StabilizeQuestRay(HandRayCursorState state, Ray rawRay, float smoothing)
    {
        Vector3 rawDirection = rawRay.direction.sqrMagnitude > 0.0001f
            ? rawRay.direction.normalized
            : (headTransform != null ? headTransform.forward : Vector3.forward);

        if (!state.hasSmoothedPose)
        {
            state.smoothedOrigin = rawRay.origin;
            state.smoothedDirection = rawDirection;
            state.hasSmoothedPose = true;
            return new Ray(state.smoothedOrigin, state.smoothedDirection);
        }

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float blend = smoothing <= 0.001f ? 1f : 1f - Mathf.Exp(-smoothing * dt);

        state.smoothedOrigin = Vector3.Lerp(state.smoothedOrigin, rawRay.origin, blend);
        state.smoothedDirection = Vector3.Slerp(state.smoothedDirection, rawDirection, blend).normalized;
        return new Ray(state.smoothedOrigin, state.smoothedDirection);
    }

    private Ray StabilizeControllerRay(HandRayCursorState state, Ray rawRay)
    {
        Vector3 rawDirection = rawRay.direction.sqrMagnitude > 0.0001f
            ? rawRay.direction.normalized
            : (headTransform != null ? headTransform.forward : Vector3.forward);

        if (!state.hasSmoothedPose)
        {
            state.smoothedOrigin = rawRay.origin;
            state.smoothedDirection = rawDirection;
            state.hasSmoothedPose = true;
            return new Ray(state.smoothedOrigin, state.smoothedDirection);
        }

        float angularDelta = Vector3.Angle(state.smoothedDirection, rawDirection);
        float positionDelta = Vector3.Distance(state.smoothedOrigin, rawRay.origin);

        if (angularDelta <= controllerRayAngularDeadzoneDegrees && positionDelta <= controllerRayPositionDeadzone)
            return new Ray(state.smoothedOrigin, state.smoothedDirection);

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float baseBlend = controllerRaySmoothing <= 0.001f
            ? 1f
            : 1f - Mathf.Exp(-controllerRaySmoothing * dt);

        float motionBoost = Mathf.InverseLerp(controllerRayAngularDeadzoneDegrees, 7f, angularDelta);
        float blend = Mathf.Clamp01(Mathf.Max(baseBlend, motionBoost));

        state.smoothedOrigin = Vector3.Lerp(state.smoothedOrigin, rawRay.origin, blend);
        state.smoothedDirection = Vector3.Slerp(state.smoothedDirection, rawDirection, blend).normalized;
        return new Ray(state.smoothedOrigin, state.smoothedDirection);
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
        state.pinchDirectionLocked = false;

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

        Color endColor = new Color(lineColor.r, lineColor.g, lineColor.b, lineColor.a * 0.15f);
        state.line.startColor = lineColor;
        state.line.endColor = endColor;
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

    // Fallback joint-derived ray used when the OpenXR system aim pose is unavailable.
    private bool TryBuildHandRayFromJoints(XRHand hand, out Ray questRay)
    {
        bool gotPalmPose = hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose);
        bool gotWristPose = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose);
        bool gotIndexTip = TryGetJointWorld(hand, XRHandJointID.IndexTip, out Vector3 indexTipWorld);
        bool gotThumbTip = TryGetJointWorld(hand, XRHandJointID.ThumbTip, out Vector3 thumbTipWorld);
        bool gotThumbProximal = TryGetJointWorld(hand, XRHandJointID.ThumbProximal, out Vector3 thumbProximalWorld);
        bool gotIndexKnuckle = TryGetJointWorld(hand, XRHandJointID.IndexProximal, out Vector3 indexKnuckleWorld);

        Vector3 origin;
        Vector3 direction = Vector3.zero;

        if (gotPalmPose && gotWristPose)
        {
            Vector3 palmWorld = JointToWorld(palmPose.position);
            Vector3 wristWorld = JointToWorld(wristPose.position);
            origin = Vector3.Lerp(wristWorld, palmWorld, handRayPalmOriginBlend);

            Vector3 palmForward = JointRotToWorld(palmPose.rotation) * Vector3.forward;
            Vector3 wristForward = JointRotToWorld(wristPose.rotation) * Vector3.forward;
            direction = Vector3.Slerp(wristForward, palmForward, handRayPalmDirectionBlend);
        }
        else if (gotPalmPose)
        {
            origin = JointToWorld(palmPose.position);
            direction = JointRotToWorld(palmPose.rotation) * Vector3.forward;
        }
        else if (gotWristPose)
        {
            origin = JointToWorld(wristPose.position);
            direction = JointRotToWorld(wristPose.rotation) * Vector3.forward;
        }
        else
        {
            bool hasStableFingerBaseOrigin = gotIndexKnuckle && gotThumbProximal;
            if (hasStableFingerBaseOrigin)
            {
                Vector3 fingerBaseMidpoint = (indexKnuckleWorld + thumbProximalWorld) * 0.5f;

                if (gotIndexTip && gotThumbTip)
                {
                    Vector3 tipMidpoint = (indexTipWorld + thumbTipWorld) * 0.5f;
                    origin = Vector3.Lerp(fingerBaseMidpoint, tipMidpoint, handRayTipMidpointWeight);
                }
                else
                {
                    origin = fingerBaseMidpoint;
                }
            }
            else if (gotIndexTip)
            {
                origin = indexTipWorld;
            }
            else if (gotThumbTip)
            {
                origin = thumbTipWorld;
            }
            else
            {
                questRay = default;
                return false;
            }

            if (gotIndexTip && gotIndexKnuckle)
            {
                Vector3 indexDirection = (indexTipWorld - indexKnuckleWorld).normalized;
                if (indexDirection.sqrMagnitude > 0.0001f)
                    direction = indexDirection;
            }
        }

        if (direction.sqrMagnitude < 0.0001f)
            direction = headTransform != null ? headTransform.forward : Vector3.forward;

        Vector3 normalizedDirection = direction.normalized;
        origin += normalizedDirection * handRayDetachDistance;

        questRay = new Ray(origin, normalizedDirection);
        return true;
    }

    private QuestRayHit ResolveQuestRayHit(Ray ray, float maxDistance)
    {
        QuestRayHit bestHit = default;
        bestHit.distance = maxDistance + 0.001f;

        if (TryRaycastQuestUi(ray, maxDistance, out QuestRayHit uiHit))
            bestHit = uiHit;

        if (Physics.Raycast(ray, out RaycastHit physicsHit, maxDistance, questPhysicsMask, QueryTriggerInteraction.Collide))
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

    private bool TryRaycastQuestUi(Ray ray, float maxDistance, out QuestRayHit hit)
    {
        hit = default;
        bool found = false;
        float closestDistance = maxDistance + 0.001f;

        foreach (RectRayTarget target in questUiTargets)
        {
            if (target == null || !target.IsValid())
                continue;

            if (!RaycastRect(ray, target.rect, out Vector3 hitPoint, out float distance))
                continue;

            if (distance < 0f || distance > maxDistance || distance >= closestDistance)
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

    private void TriggerQuestClick(QuestRayHit hit, int pointerId)
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
                pointerId = pointerId,
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
            SuppressKeyboardAfterActivation(selectedInput);
        }

        if (changed)
            ResetRecognitionContextForNewField();

        return true;
    }

    private void UpdateBackspacePoke()
    {
        if (handSubsystem == null || !handSubsystem.running)
            handSubsystem = WhiteboardPen.GetHandSubsystem();
        if (handSubsystem == null)
            return;

        PollHandBackspacePoke(handSubsystem.leftHand,  0);
        PollHandBackspacePoke(handSubsystem.rightHand, 1);
    }

    private void PollHandBackspacePoke(XRHand hand, int idx)
    {
        if (!hand.isTracked || !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tip))
        {
            _backspacePokeInZone[idx] = _backspacePokeWasClose[idx] = false;
            return;
        }
        CheckBackspacePoke(JointToWorld(tip.position), idx);
    }

    private void CheckBackspacePoke(Vector3 fingertipWorld, int idx)
    {
        var rt    = (RectTransform)backspaceButton.transform;
        var plane = new Plane(-rt.forward, rt.position);
        float adist   = Mathf.Abs(plane.GetDistanceToPoint(fingertipWorld));
        Vector3 local = rt.InverseTransformPoint(plane.ClosestPointOnPlane(fingertipWorld));
        Rect r        = rt.rect;
        bool inBounds  = local.x >= r.xMin && local.x <= r.xMax
                      && local.y >= r.yMin && local.y <= r.yMax;

        bool inZone  = inBounds && adist <= BackspacePokeHoverDist;
        bool isClose = inBounds && adist <= BackspacePokeFireDist;

        if (!inZone)
        {
            _backspacePokeInZone[idx] = _backspacePokeWasClose[idx] = false;
            return;
        }

        _backspacePokeInZone[idx] = true;

        if (isClose && !_backspacePokeWasClose[idx] && backspaceButton.interactable && CanActivateBackspace())
            backspaceButton.onClick.Invoke();

        _backspacePokeWasClose[idx] = isClose;
    }

    private bool CanActivateBackspace()
    {
        return Time.unscaledTime >= _nextBackspaceActivationTime;
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

    private void OnBackspaceClicked()
    {
        if (!CanActivateBackspace())
            return;

        _nextBackspaceActivationTime = Time.unscaledTime + Mathf.Max(0f, backspaceActivationCooldownSeconds);

        TMP_InputField target = GetActiveInputField();
        if (target == null)
            return;

        string current = target.text;
        if (string.IsNullOrEmpty(current))
            return;

        string trimmed = current.TrimEnd();
        int lastSpace = trimmed.LastIndexOf(' ');
        target.text = lastSpace >= 0 ? trimmed[..lastSpace] : string.Empty;
        target.ForceLabelUpdate();
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
                if (backspaceButton && backspaceButton.transform.parent is RectTransform footerRect)
                    retainedRightPen.loginHandwritingExclusionArea = footerRect;
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
