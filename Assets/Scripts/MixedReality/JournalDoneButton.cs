using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Attach to the writing bottle (BottlePreDuring) in the journaling scene.
///
/// Replaces poke-DONE with a grab-near-whiteboard suction flow:
///   1) Bottle is grab-enabled during Journaling.
///   2) While grabbed and near the whiteboard, suction progress increases.
///   3) Black particles flow toward the bottle mouth and result text fades out.
///   4) A world-space radial ring shows progress.
///   5) At 100%, bottle is auto-reset then EndSession() starts review teleport.
/// </summary>
public class JournalDoneButton : MonoBehaviour
{
    [Header("Legacy Done UI")]
    [Tooltip("Legacy DONE panel. Hidden and unused in the grab+suction flow.")]
    [SerializeField] private GameObject donePanel;

    [Tooltip("Legacy DONE button. Kept only for backward compatibility.")]
    [SerializeField] private Button doneButton;

    [Header("Grab Setup")]
    [Tooltip("Adds Rigidbody / XRGrabInteractable / ItemAutoReset at runtime when missing.")]
    [SerializeField] private bool configureGrabOnAwake = true;

    [Tooltip("Optional collider used by XRGrabInteractable. If empty, uses first collider found.")]
    [SerializeField] private Collider explicitGrabCollider;

    [Tooltip("Optional attach point for grabs.")]
    [SerializeField] private Transform attachTransform;

    [Tooltip("If true, ensures ItemAutoReset exists so bottle returns when too far away.")]
    [SerializeField] private bool ensureAutoReset = true;

    [Tooltip("Reset radius passed to ItemAutoReset.")]
    [SerializeField] private float resetRadius = 3f;

    [Tooltip("Outside delay passed to ItemAutoReset.")]
    [SerializeField] private float outsideDelay = 1.5f;

    [Header("Suction Detection")]
    [Tooltip("Optional exact bottle-hole transform. If empty, local offset is used.")]
    [SerializeField] private Transform bottleHole;

    [Tooltip("Fallback local offset to bottle-hole when bottleHole is not assigned.")]
    [SerializeField] private Vector3 bottleHoleLocalOffset = new Vector3(0f, 0.08f, 0.02f);

    [Tooltip("Progress reaches 100% after this many seconds of valid suction.")]
    [SerializeField] [Range(0.5f, 8f)] private float suctionDuration = 2.5f;

    [Tooltip("Distance from bottle hole to whiteboard surface to count as suction-valid.")]
    [SerializeField] [Range(0.03f, 0.6f)] private float nearWhiteboardDistance = 0.18f;

    [Tooltip("How quickly suction progress rolls back when bottle is moved away.")]
    [SerializeField] [Range(0f, 2f)] private float rollbackPerSecond = 0.5f;

    [Header("Progress Ring")]
    [Tooltip("Use a progress visual authored directly in the scene instead of generating one at runtime.")]
    [SerializeField] private bool useSceneProgressVisual;

    [Tooltip("Optional root GameObject for a pre-authored progress visual. If empty, falls back to sceneProgressFill parent/canvas.")]
    [SerializeField] private GameObject sceneProgressRoot;

    [Tooltip("Fill Image on the pre-authored progress visual. Its fillAmount is driven by suction progress.")]
    [SerializeField] private Image sceneProgressFill;

    [Tooltip("When true, the progress visual follows the bottle-hole anchor every frame.")]
    [SerializeField] private bool autoPositionProgressVisual = true;

    [Tooltip("When true, the progress visual billboards toward the main camera.")]
    [SerializeField] private bool billboardProgressVisual = true;

    [Header("Head-Locked Progress (Comfort)")]
    [Tooltip("When enabled for scene-authored progress visuals, the UI follows the user's head in a stable HUD-like position.")]
    [SerializeField] private bool headLockedSceneProgress = true;

    [Tooltip("Optional parent for head-locked progress root. If empty, uses JournalSessionManager.xrOrigin, then camera root.")]
    [SerializeField] private Transform headLockedProgressParent;

    [Tooltip("Head-relative position in metres (x=right, y=up, z=forward).")]
    [SerializeField] private Vector3 headLockedOffset = new Vector3(0.18f, -0.10f, 0.65f);

    [Tooltip("Smoothing time while following the user's head.")]
    [SerializeField] [Range(0.01f, 0.5f)] private float headLockedSmoothTime = 0.1f;

    [Tooltip("Offset of the ring from the bottle-hole anchor.")]
    [SerializeField] private Vector3 ringLocalOffset = new Vector3(0f, 0.03f, 0f);

    [Tooltip("World-space diameter of the radial progress ring in metres.")]
    [SerializeField] [Range(0.01f, 0.2f)] private float ringWorldSize = 0.05f;

    [Tooltip("Ring color shown for completed portion.")]
    [SerializeField] private Color ringFillColor = new Color(0.11f, 0.65f, 0.95f, 0.95f);

    [Tooltip("Ring color shown for remaining portion.")]
    [SerializeField] private Color ringBackgroundColor = new Color(0f, 0f, 0f, 0.35f);

    [Header("Suction Particles")]
    [Tooltip("Optional URP particle material. If null, default particle material is used.")]
    [SerializeField] private Material particleMaterial;

    [Tooltip("Approximate particles emitted per second at full suction.")]
    [SerializeField] [Range(0f, 600f)] private float particlesPerSecond = 180f;

    [Tooltip("Particle lifetime in seconds.")]
    [SerializeField] [Range(0.05f, 2f)] private float particleLifetime = 0.45f;

    [Tooltip("Particle size in world-space metres.")]
    [SerializeField] [Range(0.0005f, 0.03f)] private float particleSize = 0.0035f;

    [Tooltip("Base particle travel speed.")]
    [SerializeField] [Range(0.01f, 2f)] private float particleSpeed = 0.5f;

    [Tooltip("Random velocity spread for suction particles.")]
    [SerializeField] [Range(0f, 0.5f)] private float particleSpread = 0.06f;

    [Tooltip("How far above the result text to spawn particles.")]
    [SerializeField] [Range(0f, 0.05f)] private float textSpawnOffset = 0.006f;

    [Tooltip("Particle color. Keep this black to match ink color.")]
    [SerializeField] private Color particleColor = new Color(0f, 0f, 0f, 0.95f);

    [Header("Completion")]
    [Tooltip("Delay before EndSession after progress reaches 100%.")]
    [SerializeField] [Range(0f, 1f)] private float completionDelay = 0.05f;

    // ── Singleton ─────────────────────────────────────────────────────────
    public static JournalDoneButton Instance { get; private set; }

    // ── Runtime ───────────────────────────────────────────────────────────
    private XRGrabInteractable _grab;
    private Rigidbody _rb;
    private ItemAutoReset _autoReset;
    private float _suctionProgress;
    private float _particleAccumulator;
    private bool _completionTriggered;
    private bool _initialized;

    private Transform _originParent;
    private Vector3 _originLocalPos;
    private Quaternion _originLocalRot;

    private TMP_Text _resultText;
    private string _cachedText;
    private int _cachedCharacterCount;

    private Canvas _progressCanvas;
    private RectTransform _progressRect;
    private Image _progressFill;
    private GameObject _progressRoot;
    private bool _ownsProgressRoot;
    private bool _headLockedParentApplied;
    private Vector3 _headLockedProgressVelocity;
    private ParticleSystem _suctionParticles;
    private readonly Vector3[] _textCorners = new Vector3[4];

    private static Sprite s_discSprite;
    private static Sprite s_ringSprite;

    private const string TAG = "[JournalDoneButton]";

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _originParent = transform.parent;
        _originLocalPos = transform.localPosition;
        _originLocalRot = transform.localRotation;

        if (donePanel != null)
            donePanel.SetActive(false);

        // Legacy button path is intentionally disabled for this scene.
        if (doneButton != null)
            doneButton.onClick.RemoveListener(OnDoneClicked);

        if (configureGrabOnAwake)
            EnsureGrabRuntimeSetup();

        EnsureProgressRing();
        EnsureSuctionParticles();
        RestoreTextVisibility();
        _initialized = true;
    }

    private void OnEnable()
    {
        ShowDonePanel(false);
        _completionTriggered = false;
        _suctionProgress = 0f;
        _particleAccumulator = 0f;
        _headLockedProgressVelocity = Vector3.zero;
        _headLockedParentApplied = false;
        SetProgressVisible(false);
        RestoreTextVisibility();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        if (doneButton != null)
            doneButton.onClick.RemoveListener(OnDoneClicked);

        RestoreTextVisibility();

        if (_ownsProgressRoot && _progressRoot != null)
            Destroy(_progressRoot);

        if (_suctionParticles != null)
            Destroy(_suctionParticles.gameObject);

        if (s_discSprite != null)
            Destroy(s_discSprite.texture);
        if (s_ringSprite != null)
            Destroy(s_ringSprite.texture);

        s_discSprite = null;
        s_ringSprite = null;
    }

    // ==================================================================
    // UPDATE
    // ==================================================================

    private void Update()
    {
        if (!_initialized)
            return;

        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;

        if (!journaling)
        {
            if (_grab != null && _grab.isSelected)
                ForceRelease();

            if (_grab != null && _grab.enabled)
                _grab.enabled = false;

            ShowDonePanel(false);
            SetProgressVisible(false);
            UpdateProgress(0f);
            StopParticles();
            RestoreTextVisibility();
            _completionTriggered = false;
            return;
        }

        ShowDonePanel(false);

        if (_grab == null)
            EnsureGrabRuntimeSetup();

        if (_grab != null && !_grab.enabled)
            _grab.enabled = true;

        bool grabbed = _grab != null && _grab.isSelected;
        bool canSuck = grabbed && IsBottleNearWhiteboard();

        float riseRate = 1f / Mathf.Max(0.01f, suctionDuration);
        float fallRate = Mathf.Max(0f, rollbackPerSecond);
        float target = canSuck ? 1f : 0f;
        float speed = canSuck ? riseRate : fallRate;

        UpdateProgress(Mathf.MoveTowards(_suctionProgress, target, speed * Time.deltaTime));

        SetProgressVisible(grabbed && !_completionTriggered);
        UpdateProgressVisualPose();
        UpdateResultTextVisibility();

        if (canSuck && !_completionTriggered)
            EmitSuctionParticles(Time.deltaTime);
        else
            StopParticles();

        if (!_completionTriggered && _suctionProgress >= 1f)
            StartCoroutine(CompleteAfterSuction());
    }

    private void OnDoneClicked()
    {
        // Legacy button path is disabled by design.
    }

    // ==================================================================
    // HELPER
    // ==================================================================

    private void ShowDonePanel(bool show)
    {
        if (donePanel != null && donePanel.activeSelf != show)
            donePanel.SetActive(show);
    }

    private void EnsureGrabRuntimeSetup()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb == null)
            _rb = gameObject.AddComponent<Rigidbody>();

        _rb.useGravity = true;
        _rb.isKinematic = false;

        _grab = GetComponent<XRGrabInteractable>();
        if (_grab == null)
            _grab = gameObject.AddComponent<XRGrabInteractable>();

        Collider chosen = explicitGrabCollider;
        if (chosen == null)
            chosen = GetComponent<Collider>();
        if (chosen == null)
            chosen = GetComponentInChildren<Collider>(includeInactive: true);

        if (chosen != null)
        {
            _grab.colliders.Clear();
            _grab.colliders.Add(chosen);
        }

        if (attachTransform != null)
            _grab.attachTransform = attachTransform;

        _grab.selectMode = InteractableSelectMode.Single;

        if (ensureAutoReset)
        {
            _autoReset = GetComponent<ItemAutoReset>();
            if (_autoReset == null)
                _autoReset = gameObject.AddComponent<ItemAutoReset>();

            _autoReset.resetRadius = resetRadius;
            _autoReset.outsideDelay = outsideDelay;
            _autoReset.enabled = true;
        }

        Debug.Log($"{TAG} Grab runtime setup complete on '{name}'.");
    }

    private bool IsBottleNearWhiteboard()
    {
        Transform writingSurface = JournalSessionManager.Instance != null
            ? JournalSessionManager.Instance.tableWritingSurface
            : null;

        if (writingSurface == null && WhiteboardPageManager.Instance != null && WhiteboardPageManager.Instance.whiteboard != null)
            writingSurface = WhiteboardPageManager.Instance.whiteboard.transform;

        if (writingSurface == null)
            return false;

        Vector3 holePos = GetBottleHoleWorldPosition();

        Collider surfaceCollider = writingSurface.GetComponent<Collider>();
        if (surfaceCollider == null)
            surfaceCollider = writingSurface.GetComponentInChildren<Collider>(includeInactive: true);

        if (surfaceCollider != null)
        {
            Vector3 closest = surfaceCollider.ClosestPoint(holePos);
            return Vector3.Distance(holePos, closest) <= nearWhiteboardDistance;
        }

        return Vector3.Distance(holePos, writingSurface.position) <= nearWhiteboardDistance;
    }

    private Vector3 GetBottleHoleWorldPosition()
    {
        if (bottleHole != null)
            return bottleHole.position;

        var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers != null && renderers.Length > 0)
        {
            Bounds combined = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                combined.Encapsulate(renderers[i].bounds);

            Vector3 topCenter = new Vector3(combined.center.x, combined.max.y, combined.center.z);
            return topCenter;
        }

        return transform.TransformPoint(bottleHoleLocalOffset);
    }

    private Vector3 GetRingWorldPosition()
    {
        if (bottleHole != null)
            return bottleHole.TransformPoint(ringLocalOffset);

        return transform.TransformPoint(bottleHoleLocalOffset + ringLocalOffset);
    }

    private IEnumerator CompleteAfterSuction()
    {
        if (_completionTriggered)
            yield break;

        _completionTriggered = true;
        SetProgressVisible(false);

        ForceRelease();
        yield return null;

        if (completionDelay > 0f)
            yield return new WaitForSeconds(completionDelay);

        if (_autoReset != null)
            _autoReset.ResetNow();
        else
            ResetToAuthoredPose();

        StopParticles();
        RestoreTextVisibility();

        Debug.Log($"{TAG} Suction complete — ending journaling session.");
        JournalSessionManager.Instance?.EndSession();
    }

    private void ForceRelease()
    {
        if (_grab == null || !_grab.isSelected)
            return;

        var manager = _grab.interactionManager;
        if (manager == null)
            return;

        for (int i = _grab.interactorsSelecting.Count - 1; i >= 0; i--)
        {
            IXRSelectInteractor interactor = _grab.interactorsSelecting[i];
            if (interactor != null)
                manager.SelectExit(interactor, _grab);
        }
    }

    private void ResetToAuthoredPose()
    {
        transform.SetParent(_originParent, worldPositionStays: false);
        transform.localPosition = _originLocalPos;
        transform.localRotation = _originLocalRot;

        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }

    private void UpdateProgress(float value)
    {
        _suctionProgress = Mathf.Clamp01(value);
        if (_progressFill != null)
            _progressFill.fillAmount = _suctionProgress;
    }

    private void EnsureProgressRing()
    {
        if (_progressFill != null)
            return;

        if (TryBindSceneProgressVisual())
            return;

        EnsureRingSprites();

        GameObject canvasGO = new GameObject("SuctionProgressRing", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasGO.transform.SetParent(transform, false);

        _progressCanvas = canvasGO.GetComponent<Canvas>();
        _progressCanvas.renderMode = RenderMode.WorldSpace;
        _progressCanvas.overrideSorting = true;
        _progressCanvas.sortingOrder = short.MaxValue;

        _progressRect = (RectTransform)canvasGO.transform;
        _progressRect.sizeDelta = new Vector2(100f, 100f);
        _progressRect.localScale = Vector3.one * (ringWorldSize / 100f);

        Image bg = CreateRingImage(_progressRect, "Background", s_ringSprite, Image.Type.Simple, 1f, ringBackgroundColor);
        bg.raycastTarget = false;

        _progressFill = CreateRingImage(_progressRect, "Fill", s_discSprite, Image.Type.Filled, 0f, ringFillColor);
        _progressFill.fillMethod = Image.FillMethod.Radial360;
        _progressFill.fillOrigin = (int)Image.Origin360.Top;
        _progressFill.fillClockwise = true;
        _progressFill.raycastTarget = false;

        _progressRoot = canvasGO;
        _ownsProgressRoot = true;
        _progressRoot.SetActive(false);
    }

    private bool TryBindSceneProgressVisual()
    {
        if (!useSceneProgressVisual && sceneProgressFill == null && sceneProgressRoot == null)
            return false;

        Image fill = sceneProgressFill;
        if (fill == null && sceneProgressRoot != null)
            fill = sceneProgressRoot.GetComponentInChildren<Image>(includeInactive: true);

        if (fill == null)
        {
            Debug.LogWarning($"{TAG} useSceneProgressVisual is enabled but no fill Image is assigned/found. Falling back to runtime-generated visual.");
            return false;
        }

        _progressFill = fill;
        _progressRect = fill.rectTransform;
        _progressCanvas = fill.canvas;
        _progressRoot = sceneProgressRoot != null
            ? sceneProgressRoot
            : (_progressCanvas != null ? _progressCanvas.gameObject : fill.gameObject);
        _ownsProgressRoot = false;
        _headLockedParentApplied = false;

        ConfigureSceneProgressFill(_progressFill);

        if (headLockedSceneProgress)
            EnsureHeadLockedProgressParent();

        if (_progressRoot != null)
            _progressRoot.SetActive(false);

        return true;
    }

    private void ConfigureSceneProgressFill(Image fill)
    {
        if (fill == null)
            return;

        // Unity hides some Image options when Source Image is None.
        // If the authored Fill has no sprite, provide a generated disc so
        // Filled mode is visible and renderable without extra setup.
        if (fill.sprite == null)
        {
            EnsureRingSprites();
            fill.sprite = s_discSprite;
        }

        // Keep authored sprites/materials from scene setup, only enforce fill behavior.
        if (fill.type != Image.Type.Filled)
            fill.type = Image.Type.Filled;

        fill.fillMethod = Image.FillMethod.Radial360;
        fill.fillOrigin = (int)Image.Origin360.Top;
        fill.fillClockwise = true;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;

        // Keep authored color unless it's default white, then apply configured ring fill color.
        if (fill.color.r > 0.99f && fill.color.g > 0.99f && fill.color.b > 0.99f)
            fill.color = ringFillColor;
    }

    private void EnsureHeadLockedProgressParent()
    {
        if (_headLockedParentApplied || !headLockedSceneProgress || _progressRoot == null || _ownsProgressRoot)
            return;

        Transform targetParent = headLockedProgressParent;

        if (targetParent == null && JournalSessionManager.Instance != null)
            targetParent = JournalSessionManager.Instance.xrOrigin;

        Camera cam = Camera.main;
        if (targetParent == null && cam != null)
            targetParent = cam.transform.root;

        if (targetParent == null)
            return;

        if (_progressRoot.transform.parent != targetParent)
            _progressRoot.transform.SetParent(targetParent, worldPositionStays: true);

        _headLockedParentApplied = true;
    }

    private void EnsureRingSprites()
    {
        if (s_discSprite == null)
            s_discSprite = CreateCircleSprite(96, ringMode: false, ringThickness: 0f);
        if (s_ringSprite == null)
            s_ringSprite = CreateCircleSprite(96, ringMode: true, ringThickness: 0.12f);
    }

    private static Image CreateRingImage(RectTransform parent, string objName, Sprite sprite, Image.Type type, float fill, Color color)
    {
        GameObject imageGO = new GameObject(objName, typeof(RectTransform), typeof(Image));
        imageGO.transform.SetParent(parent, false);

        RectTransform rt = (RectTransform)imageGO.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image image = imageGO.GetComponent<Image>();
        image.sprite = sprite;
        image.type = type;
        image.color = color;
        image.fillAmount = fill;
        image.maskable = false;
        return image;
    }

    private static Sprite CreateCircleSprite(int size, bool ringMode, float ringThickness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float radius = 0.49f;
        float innerRadius = Mathf.Clamp01(radius - ringThickness);
        const float edgeSmooth = 0.02f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                float dx = u - 0.5f;
                float dy = v - 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float outerAlpha = Mathf.InverseLerp(radius + edgeSmooth, radius - edgeSmooth, dist);
                float alpha;

                if (ringMode)
                {
                    float innerAlpha = Mathf.InverseLerp(innerRadius - edgeSmooth, innerRadius + edgeSmooth, dist);
                    alpha = Mathf.Clamp01(Mathf.Min(outerAlpha, innerAlpha));
                }
                else
                {
                    alpha = Mathf.Clamp01(outerAlpha);
                }

                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private void SetProgressVisible(bool show)
    {
        if (_progressRoot != null && _progressRoot.activeSelf != show)
            _progressRoot.SetActive(show);
    }

    private void UpdateProgressVisualPose()
    {
        if (_progressRect == null)
            return;

        if (_progressRoot != null && !_progressRoot.activeSelf)
            return;

        if (headLockedSceneProgress && !_ownsProgressRoot)
        {
            EnsureHeadLockedProgressParent();

            Camera cam = Camera.main;
            if (cam == null)
                return;

            Transform camTf = cam.transform;
            Vector3 targetPosition =
                camTf.position +
                camTf.forward * headLockedOffset.z +
                camTf.up * headLockedOffset.y +
                camTf.right * headLockedOffset.x;

            float smooth = Mathf.Max(0.01f, headLockedSmoothTime);
            _progressRect.position = Vector3.SmoothDamp(
                _progressRect.position,
                targetPosition,
                ref _headLockedProgressVelocity,
                smooth
            );

            Vector3 toCamera = camTf.position - _progressRect.position;
            if (toCamera.sqrMagnitude > 0.000001f)
                _progressRect.rotation = Quaternion.LookRotation(toCamera.normalized, camTf.up);

            return;
        }

        if (autoPositionProgressVisual)
            _progressRect.position = GetRingWorldPosition();

        if (billboardProgressVisual)
        {
            Camera cam = Camera.main;
            if (cam != null)
                _progressRect.rotation = Quaternion.LookRotation(cam.transform.forward, Vector3.up);
        }
    }

    private void EnsureSuctionParticles()
    {
        if (_suctionParticles != null)
            return;

        GameObject psGO = new GameObject("SuctionInkParticles");
        psGO.transform.SetParent(transform, false);
        _suctionParticles = psGO.AddComponent<ParticleSystem>();

        var main = _suctionParticles.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = particleLifetime;
        main.startSize = particleSize;
        main.startSpeed = 0f;
        main.startColor = particleColor;
        main.maxParticles = 800;

        var emission = _suctionParticles.emission;
        emission.enabled = false;

        var shape = _suctionParticles.shape;
        shape.enabled = false;

        var col = _suctionParticles.colorOverLifetime;
        col.enabled = true;
        Gradient grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.black, 1f) },
            new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        col.color = grad;

        var renderer = psGO.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        if (particleMaterial != null)
            renderer.material = new Material(particleMaterial);
    }

    private void EmitSuctionParticles(float dt)
    {
        if (_suctionParticles == null)
            return;

        float intensity = Mathf.Lerp(0.25f, 1f, _suctionProgress);
        _particleAccumulator += dt * particlesPerSecond * intensity;
        int emitCount = Mathf.FloorToInt(_particleAccumulator);
        if (emitCount <= 0)
            return;

        _particleAccumulator -= emitCount;

        Vector3 hole = GetBottleHoleWorldPosition();

        for (int i = 0; i < emitCount; i++)
        {
            Vector3 spawn = GetRandomTextSpawnPoint();
            Vector3 dir = hole - spawn;
            float dist = dir.magnitude;
            if (dist <= 0.0001f)
                continue;

            dir /= dist;
            Vector3 velocity = dir * (particleSpeed + dist * 0.5f) + Random.insideUnitSphere * particleSpread;

            ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
            {
                position = spawn,
                velocity = velocity,
                startLifetime = particleLifetime,
                startSize = particleSize,
                startColor = particleColor
            };

            _suctionParticles.Emit(emitParams, 1);
        }

        if (!_suctionParticles.isPlaying)
            _suctionParticles.Play();
    }

    private Vector3 GetRandomTextSpawnPoint()
    {
        TMP_Text text = GetResultText();
        if (text != null)
        {
            RectTransform rt = text.rectTransform;
            rt.GetWorldCorners(_textCorners);

            float u = Random.value;
            float v = Random.value;

            Vector3 left = Vector3.Lerp(_textCorners[0], _textCorners[1], v);
            Vector3 right = Vector3.Lerp(_textCorners[3], _textCorners[2], v);
            Vector3 p = Vector3.Lerp(left, right, u);
            return p + rt.forward * textSpawnOffset;
        }

        Transform writingSurface = JournalSessionManager.Instance != null
            ? JournalSessionManager.Instance.tableWritingSurface
            : null;

        if (writingSurface != null)
            return writingSurface.position + writingSurface.up * textSpawnOffset + Random.insideUnitSphere * 0.03f;

        return transform.position;
    }

    private void StopParticles()
    {
        if (_suctionParticles != null && _suctionParticles.isPlaying)
            _suctionParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private TMP_Text GetResultText()
    {
        if (_resultText != null)
            return _resultText;

        if (WhiteboardPageManager.Instance == null)
            return null;

        _resultText = WhiteboardPageManager.Instance.resultText;
        _cachedText = null;
        _cachedCharacterCount = 0;
        return _resultText;
    }

    private void UpdateResultTextVisibility()
    {
        TMP_Text text = GetResultText();
        if (text == null)
            return;

        if (!string.Equals(_cachedText, text.text))
        {
            _cachedText = text.text;
            text.ForceMeshUpdate();
            _cachedCharacterCount = text.textInfo.characterCount;
        }

        int visible = Mathf.RoundToInt(Mathf.Lerp(_cachedCharacterCount, 0, _suctionProgress));
        text.maxVisibleCharacters = Mathf.Clamp(visible, 0, int.MaxValue);
    }

    private void RestoreTextVisibility()
    {
        TMP_Text text = GetResultText();
        if (text != null)
            text.maxVisibleCharacters = int.MaxValue;
    }
}
