using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Scene transition portal for XR/VR.
///
/// Attach this to a portal trigger collider. When the player enters, the script:
/// 1) Plays portal VFX/SFX,
/// 2) Optionally fades the view,
/// 3) Loads the configured scene.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class PortalSceneTransition : MonoBehaviour
{
    [Header("Auto Wire")]
    [Tooltip("Automatically find portal references from this object's hierarchy.")]
    [SerializeField] private bool autoWireOnAwake = true;

    [Tooltip("Also auto-wire references in editor while values are missing.")]
    [SerializeField] private bool autoWireInEditor = true;

    [Tooltip("Create missing references and helper objects when none are found.")]
    [SerializeField] private bool autoCreateMissingReferences = true;

    [Tooltip("If true, auto-create a full-screen fade canvas when missing.")]
    [SerializeField] private bool autoCreateComfortFadeCanvas = true;

    [Header("Scene")]
    [Tooltip("Scene name or scene asset path in Build Settings. Example: Assets/Scenes/3D_Journal_CURRENT_IMPROVE.unity")]
    [SerializeField] private string targetScene = "Assets/Scenes/3D_Journal_CURRENT_IMPROVE.unity";

    [Tooltip("Delay before loading scene, so enter VFX can play first.")]
    [SerializeField] [Range(0f, 10f)] private float loadDelay = 2.8f;

    [Tooltip("How long the ring should spin up before transition load starts.")]
    [SerializeField] [Range(0f, 10f)] private float enterSpinDuration = 2.8f;

    [Header("Who Can Trigger")]
    [Tooltip("Primary tag used to detect the player collider or camera.")]
    [SerializeField] private string playerTag = "MainCamera";

    [Tooltip("Allow trigger from CharacterController colliders even when tag does not match.")]
    [SerializeField] private bool allowCharacterControllerTrigger = true;

    [Tooltip("Optional extra layers that can trigger the portal.")]
    [SerializeField] private LayerMask triggeringLayers;

    [Tooltip("If enabled, entering from behind the portal is ignored.")]
    [SerializeField] private bool requireForwardEntry;

    [Tooltip("Forward direction reference for front-entry checks.")]
    [SerializeField] private Transform portalForwardReference;

    [Header("Portal VFX / SFX")]
    [SerializeField] private ParticleSystem idleLoopVfx;
    [SerializeField] private ParticleSystem enterBurstVfx;
    [SerializeField] private Animator portalAnimator;
    [SerializeField] private string enterAnimatorTrigger = "Enter";
    [SerializeField] private AudioSource portalAudioSource;
    [SerializeField] private AudioClip enterSfx;
    [SerializeField] [Range(0f, 1f)] private float enterSfxVolume = 1f;
    [SerializeField] private bool stopIdleLoopOnEnter = false;
    [SerializeField] private bool playEnterBurstVfx = false;

    [Header("Synced Spin Particles")]
    [Tooltip("Keep idle particles spinning with the portal ring and accelerate on enter.")]
    [SerializeField] private bool synchronizeParticlesWithRing = true;

    [Tooltip("Idle orbital spin speed for particle loops.")]
    [SerializeField] [Range(0f, 4f)] private float idleParticleSpinSpeed = 0.16f;

    [Tooltip("Enter orbital spin speed for particle loops.")]
    [SerializeField] [Range(0f, 8f)] private float enterParticleSpinSpeed = 1.45f;

    [Tooltip("Simulation speed while portal is idle.")]
    [SerializeField] [Range(0.1f, 4f)] private float idleParticleSimulationSpeed = 0.85f;

    [Tooltip("Simulation speed while portal is spinning up.")]
    [SerializeField] [Range(0.1f, 8f)] private float enterParticleSimulationSpeed = 1.8f;

    [Tooltip("How quickly particles react to ring speed changes.")]
    [SerializeField] [Range(1f, 20f)] private float particleSpinResponse = 8f;

    [Tooltip("Automatically assign a URP-compatible particle material to avoid magenta shaders.")]
    [SerializeField] private bool autoAssignParticleMaterials = true;

    [Tooltip("Primary tint for the portal particles.")]
    [SerializeField] [ColorUsage(true, true)] private Color particleTint = new Color(0.72f, 0.66f, 0.94f, 0.78f);

    [Tooltip("Brightness multiplier for particle material color.")]
    [SerializeField] [Range(0.2f, 3f)] private float particleBrightness = 1.1f;

    [Header("Comfort Profile")]
    [Tooltip("Apply a low-stimulus visual profile suitable for calm room VR use.")]
    [SerializeField] private bool applyCalmComfortProfile = true;

    [Tooltip("Anchor used for auto-created portal particles. Defaults to PortalGlass or this object.")]
    [SerializeField] private Transform vfxAnchor;

    [Tooltip("Main calming tint used by the portal effects.")]
    [SerializeField] [ColorUsage(true, true)] private Color calmTint = new Color(0.52f, 0.86f, 0.92f, 1f);

    [Tooltip("Secondary tint to keep the effect soft instead of high-contrast.")]
    [SerializeField] [ColorUsage(true, true)] private Color calmAccent = new Color(0.74f, 0.92f, 0.96f, 1f);

    [Tooltip("How dynamic particle motion can be. Lower values are calmer.")]
    [SerializeField] [Range(0f, 1f)] private float motionIntensity = 0.35f;

    [Tooltip("Emission multiplier for portal glass tint. Keep low for comfort.")]
    [SerializeField] [Range(0f, 2f)] private float glassEmission = 0.5f;

    [Tooltip("Minimum delay before loading to avoid abrupt transition discomfort.")]
    [SerializeField] [Range(0f, 5f)] private float minimumComfortLoadDelay = 0.9f;

    [Header("Optional Screen Fade")]
    [Tooltip("Optional CanvasGroup used as full-screen fade. Leave null to skip fade.")]
    [SerializeField] private CanvasGroup fadeCanvas;

    [SerializeField] [Range(0f, 3f)] private float fadeDuration = 0.35f;
    [SerializeField] [Range(0f, 1f)] private float vignetteMaxAlpha = 0.5f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("Callbacks")]
    public UnityEvent onTransitionStarted;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
    private static readonly int BlendId = Shader.PropertyToID("_Blend");
    private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

    private Collider triggerCollider;
    private bool isTransitioning;
    private MaterialPropertyBlock propertyBlock;
    private Material particleRuntimeMaterial;
    private float currentParticleSpin01;
    private readonly HashSet<int> activePlayerTriggerIds = new HashSet<int>();
    private Coroutine _activeTransition;
    private Coroutine _vignetteCoroutine;

    private void Reset()
    {
        EnsureTriggerCollider();
        TryAutoWireReferences(includeInactive: true);
    }

    private void Awake()
    {
        EnsureTriggerCollider();

        if (autoWireOnAwake)
            TryAutoWireReferences(includeInactive: true);

        if (portalForwardReference == null)
            portalForwardReference = FindPortalRoot(transform) ?? transform;

        if (fadeCanvas != null)
        {
            fadeCanvas.alpha = 0f;
            fadeCanvas.blocksRaycasts = false;
            fadeCanvas.interactable = false;
        }

        if (idleLoopVfx != null && !idleLoopVfx.isPlaying)
            idleLoopVfx.Play(true);

        if (enterBurstVfx != null && enterBurstVfx.isPlaying)
            enterBurstVfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        EnsureParticleMaterials();
        currentParticleSpin01 = 0f;
        ApplySynchronizedParticleSpin(currentParticleSpin01);
    }

    private void OnValidate()
    {
        EnsureTriggerCollider();

        if (autoWireInEditor)
            TryAutoWireReferences(includeInactive: true);

        if (portalForwardReference == null)
            portalForwardReference = FindPortalRoot(transform) ?? transform;

        if (!Application.isPlaying)
            EnsureParticleMaterials();
    }

    private void Update()
    {
        UpdateSyncedParticleSpin();
    }

    private void UpdateSyncedParticleSpin()
    {
        if (!synchronizeParticlesWithRing || idleLoopVfx == null)
            return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f)
            return;

        float targetSpin01 = GetTargetParticleSpin01();
        float lerp = 1f - Mathf.Exp(-particleSpinResponse * dt);
        currentParticleSpin01 = Mathf.Lerp(currentParticleSpin01, targetSpin01, lerp);

        ApplySynchronizedParticleSpin(currentParticleSpin01);
    }

    [ContextMenu("Auto Wire Portal Now")]
    private void AutoWireNow()
    {
        TryAutoWireReferences(includeInactive: true);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isTransitioning) return;
        if (!IsPlayerTrigger(other)) return;
        if (requireForwardEntry && !IsEnteringFromFront(other)) return;

        activePlayerTriggerIds.Add(other.GetInstanceID());
        _activeTransition = StartCoroutine(TransitionRoutine());
    }

    private void OnTriggerExit(Collider other)
    {
        if (other == null)
            return;

        activePlayerTriggerIds.Remove(other.GetInstanceID());

        if (isTransitioning && IsPlayerTrigger(other))
            InterruptTransition();
    }

    private void InterruptTransition()
    {
        if (_activeTransition != null)
        {
            StopCoroutine(_activeTransition);
            _activeTransition = null;
        }

        isTransitioning = false;
        activePlayerTriggerIds.Clear();

        if (portalAudioSource != null && portalAudioSource.isPlaying)
            portalAudioSource.Stop();

        if (stopIdleLoopOnEnter && idleLoopVfx != null && !idleLoopVfx.isPlaying)
            idleLoopVfx.Play(true);

        currentParticleSpin01 = 0f;

        if (_vignetteCoroutine != null)
            StopCoroutine(_vignetteCoroutine);
        _vignetteCoroutine = StartCoroutine(FadeVignette(fadeCanvas != null ? fadeCanvas.alpha : 0f, 0f, 0.4f));
    }

    private bool IsPlayerTrigger(Collider other)
    {
        if (other == null) return false;

        // The NPC avatar carries a NavMeshAgent (the player's XR Origin does not). Reject it up front
        // so it can never trigger the portal — even though it has a CharacterController (for NPC gravity)
        // that would otherwise pass the allowCharacterControllerTrigger check below.
        if (other.GetComponentInParent<NavMeshAgent>() != null)
            return false;

        if (IsTagMatch(other.transform))
            return true;

        Camera cameraInParent = other.GetComponentInParent<Camera>();
        if (cameraInParent != null && (string.IsNullOrEmpty(playerTag) || cameraInParent.CompareTag(playerTag)))
            return true;

        if (allowCharacterControllerTrigger && other.GetComponentInParent<CharacterController>() != null)
            return true;

        Camera mainCamera = Camera.main;
        if (mainCamera != null && other.transform.root == mainCamera.transform.root)
            return true;

        if (triggeringLayers.value != 0)
        {
            int otherMask = 1 << other.gameObject.layer;
            if ((triggeringLayers.value & otherMask) != 0)
                return true;
        }

        return false;
    }

    private bool IsTagMatch(Transform source)
    {
        if (source == null || string.IsNullOrEmpty(playerTag))
            return false;

        if (source.CompareTag(playerTag))
            return true;

        if (source.root != null && source.root.CompareTag(playerTag))
            return true;

        return false;
    }

    private void EnsureTriggerCollider()
    {
        triggerCollider = GetComponent<Collider>();
        if (triggerCollider != null && !triggerCollider.isTrigger)
            triggerCollider.isTrigger = true;
    }

    private void TryAutoWireReferences(bool includeInactive)
    {
        Transform portalRoot = FindPortalRoot(transform) ?? transform;
        if (vfxAnchor == null)
            vfxAnchor = ResolveVfxAnchor(portalRoot, includeInactive);

        if (portalForwardReference == null)
            portalForwardReference = portalRoot;

        if (portalAnimator == null)
            portalAnimator = portalRoot.GetComponentInChildren<Animator>(includeInactive);

        if (portalAudioSource == null)
            portalAudioSource = portalRoot.GetComponentInChildren<AudioSource>(includeInactive);

        if (portalAudioSource == null && autoCreateMissingReferences)
            portalAudioSource = CreatePortalAudioSource(portalRoot);

        ParticleSystem[] systems = portalRoot.GetComponentsInChildren<ParticleSystem>(includeInactive);
        if (systems.Length > 0)
        {
            if (idleLoopVfx == null)
                idleLoopVfx = systems.FirstOrDefault(ps => ps != null && ps.main.loop);

            if (enterBurstVfx == null)
                enterBurstVfx = systems.FirstOrDefault(ps => ps != null && !ps.main.loop && ps != idleLoopVfx);

            // Fallback: if there is only one particle system, reuse it as idle loop.
            if (idleLoopVfx == null)
                idleLoopVfx = systems[0];
        }

        if (autoCreateMissingReferences)
        {
            if (idleLoopVfx == null)
                idleLoopVfx = CreateAutoParticleSystem("PortalIdleLoop_Auto", loop: true);

            if (enterBurstVfx == null)
                enterBurstVfx = CreateAutoParticleSystem("PortalEnterBurst_Auto", loop: false);
        }

        if (fadeCanvas == null)
        {
            CanvasGroup[] canvasGroups = FindObjectsOfType<CanvasGroup>(true);
            fadeCanvas = canvasGroups.FirstOrDefault(cg => NameContains(cg.transform, "fade"));
        }

        if (fadeCanvas == null && autoCreateMissingReferences && autoCreateComfortFadeCanvas)
            fadeCanvas = CreateComfortFadeCanvas();

        if (triggeringLayers.value == 0 && Camera.main != null)
            triggeringLayers = 1 << Camera.main.gameObject.layer;

        EnsureParticleMaterials();
        currentParticleSpin01 = 0f;
        ApplySynchronizedParticleSpin(currentParticleSpin01);

        if (applyCalmComfortProfile)
            ApplyCalmComfortProfile(portalRoot);
    }

    private Transform ResolveVfxAnchor(Transform portalRoot, bool includeInactive)
    {
        if (vfxAnchor != null)
            return vfxAnchor;

        if (NameContains(transform, "glass"))
            return transform;

        Transform[] hierarchy = portalRoot.GetComponentsInChildren<Transform>(includeInactive);
        Transform anchor = hierarchy.FirstOrDefault(t => NameContains(t, "portalglass"));
        if (anchor != null)
            return anchor;

        anchor = hierarchy.FirstOrDefault(t => NameContains(t, "glass"));
        if (anchor != null)
            return anchor;

        anchor = hierarchy.FirstOrDefault(t => NameContains(t, "portalcircle"));
        return anchor != null ? anchor : transform;
    }

    private AudioSource CreatePortalAudioSource(Transform portalRoot)
    {
        AudioSource source = portalRoot.GetComponent<AudioSource>();
        if (source == null)
            source = portalRoot.gameObject.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 0.3f;
        source.maxDistance = 4f;
        return source;
    }

    private ParticleSystem CreateAutoParticleSystem(string objectName, bool loop)
    {
        Transform anchor = vfxAnchor != null ? vfxAnchor : transform;
        Transform existing = anchor.Find(objectName);

        GameObject obj;
        if (existing != null)
        {
            obj = existing.gameObject;
        }
        else
        {
            obj = new GameObject(objectName);
            obj.layer = gameObject.layer;
            obj.transform.SetParent(anchor, false);
            obj.transform.localPosition = Vector3.up * 0.01f;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
        }

        ParticleSystem system = obj.GetComponent<ParticleSystem>();
        if (system == null)
            system = obj.AddComponent<ParticleSystem>();

        if (obj.GetComponent<ParticleSystemRenderer>() == null)
            obj.AddComponent<ParticleSystemRenderer>();

        var main = system.main;
        main.loop = loop;
        main.playOnAwake = loop;
        return system;
    }

    private CanvasGroup CreateComfortFadeCanvas()
    {
        CanvasGroup[] existing = FindObjectsOfType<CanvasGroup>(true);
        CanvasGroup existingPortalFade = existing.FirstOrDefault(cg => NameContains(cg.transform, "portalfade"));
        if (existingPortalFade != null)
            return existingPortalFade;

        GameObject canvasObject = new GameObject("PortalFadeCanvas_Auto");
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GraphicRaycaster raycaster = canvasObject.AddComponent<GraphicRaycaster>();
        raycaster.enabled = false;

        CanvasGroup group = canvasObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;

        GameObject fadeImageObject = new GameObject("FadeImage");
        fadeImageObject.transform.SetParent(canvasObject.transform, false);

        RectTransform rect = fadeImageObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = fadeImageObject.AddComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;

        return group;
    }

    private void ApplyCalmComfortProfile(Transform portalRoot)
    {
        EnsureComfortTiming();

        if (idleLoopVfx != null)
            ConfigureIdleLoopVfx(idleLoopVfx);

        if (enterBurstVfx != null)
            ConfigureEnterBurstVfx(enterBurstVfx);

        ApplyCalmTintToPortalGlass(portalRoot);
    }

    private void EnsureComfortTiming()
    {
        enterSpinDuration = Mathf.Max(enterSpinDuration, minimumComfortLoadDelay);
        loadDelay = Mathf.Max(loadDelay, minimumComfortLoadDelay, enterSpinDuration);
        enterSfxVolume = Mathf.Min(enterSfxVolume, 1f);

        idleParticleSpinSpeed = Mathf.Clamp(idleParticleSpinSpeed, 0.05f, 0.35f);
        enterParticleSpinSpeed = Mathf.Max(enterParticleSpinSpeed, idleParticleSpinSpeed + 0.45f);
        idleParticleSimulationSpeed = Mathf.Clamp(idleParticleSimulationSpeed, 0.7f, 1.25f);
        enterParticleSimulationSpeed = Mathf.Max(enterParticleSimulationSpeed, idleParticleSimulationSpeed + 0.4f);
        particleBrightness = Mathf.Clamp(particleBrightness, 0.6f, 1.5f);

        if (fadeCanvas != null)
            fadeDuration = Mathf.Max(0.25f, fadeDuration);
    }

    private void ConfigureIdleLoopVfx(ParticleSystem system)
    {
        bool wasPlaying = StopParticleForReconfigure(system);
        float radius = EstimatePortalRadius();

        var main = system.main;
        main.loop = true;
        main.duration = 2.4f;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.simulationSpeed = idleParticleSimulationSpeed;
        main.maxParticles = 130;
        main.gravityModifier = 0f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, Mathf.Lerp(0.05f, 0.14f, motionIntensity));
        main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.045f);

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = Mathf.Lerp(18f, 40f, motionIntensity);

        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = radius;
        shape.arc = 360f;

        var velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.x = new ParticleSystem.MinMaxCurve(0f);
        velocity.y = new ParticleSystem.MinMaxCurve(0f);
        velocity.z = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalX = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalY = new ParticleSystem.MinMaxCurve(idleParticleSpinSpeed);
        velocity.orbitalZ = new ParticleSystem.MinMaxCurve(0f);
        velocity.radial = new ParticleSystem.MinMaxCurve(Mathf.Lerp(0.01f, 0.05f, motionIntensity));

        var noise = system.noise;
        noise.enabled = true;
        noise.strength = new ParticleSystem.MinMaxCurve(Mathf.Lerp(0.01f, 0.08f, motionIntensity));
        noise.frequency = 0.35f;
        noise.scrollSpeed = Mathf.Lerp(0.02f, 0.15f, motionIntensity);
        noise.quality = ParticleSystemNoiseQuality.Low;

        var colorLifetime = system.colorOverLifetime;
        colorLifetime.enabled = true;
        colorLifetime.color = new ParticleSystem.MinMaxGradient(BuildIdleGradient());

        var sizeLifetime = system.sizeOverLifetime;
        sizeLifetime.enabled = true;
        sizeLifetime.size = new ParticleSystem.MinMaxCurve(
            1f,
            new AnimationCurve(
                new Keyframe(0f, 0.45f),
                new Keyframe(0.28f, 1f),
                new Keyframe(1f, 0.7f)));

        ConfigureRenderer(system);

        if (wasPlaying && !(isTransitioning && stopIdleLoopOnEnter))
            system.Play(true);
    }

    private void ConfigureEnterBurstVfx(ParticleSystem system)
    {
        StopParticleForReconfigure(system);
        float radius = EstimatePortalRadius();

        var main = system.main;
        main.loop = false;
        main.duration = 0.7f;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.maxParticles = 110;
        main.gravityModifier = 0f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, Mathf.Lerp(0.8f, 1.4f, motionIntensity));
        main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        short burstCount = (short)Mathf.RoundToInt(Mathf.Lerp(45f, 90f, motionIntensity));
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, burstCount) });

        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = radius * 0.98f;
        shape.arc = 360f;

        var velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.x = new ParticleSystem.MinMaxCurve(0f);
        velocity.y = new ParticleSystem.MinMaxCurve(0f);
        velocity.z = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalX = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalY = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalZ = new ParticleSystem.MinMaxCurve(0f);
        velocity.radial = new ParticleSystem.MinMaxCurve(Mathf.Lerp(0.2f, 0.9f, motionIntensity));

        var noise = system.noise;
        noise.enabled = true;
        noise.strength = new ParticleSystem.MinMaxCurve(Mathf.Lerp(0.02f, 0.1f, motionIntensity));
        noise.frequency = 0.5f;
        noise.scrollSpeed = Mathf.Lerp(0.1f, 0.25f, motionIntensity);

        var colorLifetime = system.colorOverLifetime;
        colorLifetime.enabled = true;
        colorLifetime.color = new ParticleSystem.MinMaxGradient(BuildEnterGradient());

        var trails = system.trails;
        trails.enabled = false;

        ConfigureRenderer(system);
    }

    private static bool StopParticleForReconfigure(ParticleSystem system)
    {
        if (system == null)
            return false;

        bool wasActive = system.isPlaying || system.isEmitting || system.isPaused;
        if (wasActive)
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        return wasActive;
    }

    private void ConfigureRenderer(ParticleSystem system)
    {
        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer == null)
            return;

        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.sortMode = ParticleSystemSortMode.Distance;
    }

    private float GetTargetParticleSpin01()
    {
        return isTransitioning ? 1f : 0f;
    }

    private void ApplySynchronizedParticleSpin(float normalizedSpin)
    {
        if (!synchronizeParticlesWithRing || idleLoopVfx == null)
            return;

        float spin01 = Mathf.Clamp01(normalizedSpin);

        var main = idleLoopVfx.main;
        main.simulationSpeed = Mathf.Lerp(idleParticleSimulationSpeed, enterParticleSimulationSpeed, spin01);

        var velocity = idleLoopVfx.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.orbitalX = new ParticleSystem.MinMaxCurve(0f);
        velocity.orbitalY = new ParticleSystem.MinMaxCurve(Mathf.Lerp(idleParticleSpinSpeed, enterParticleSpinSpeed, spin01));
        velocity.orbitalZ = new ParticleSystem.MinMaxCurve(0f);

        var noise = idleLoopVfx.noise;
        noise.enabled = true;
        noise.scrollSpeed = Mathf.Lerp(0.03f, 0.28f, spin01);
        noise.strength = new ParticleSystem.MinMaxCurve(Mathf.Lerp(0.01f, 0.09f, spin01));

        if (!idleLoopVfx.isPlaying && !(isTransitioning && stopIdleLoopOnEnter))
            idleLoopVfx.Play(true);
    }

    private void EnsureParticleMaterials()
    {
        if (!autoAssignParticleMaterials)
            return;

        if (idleLoopVfx == null && enterBurstVfx == null)
            return;

        Shader particleShader = ResolveParticleShader();
        if (particleShader == null)
            return;

        if (particleRuntimeMaterial == null || particleRuntimeMaterial.shader != particleShader)
        {
            DestroyParticleRuntimeMaterial();
            particleRuntimeMaterial = new Material(particleShader)
            {
                name = "PortalParticles_AutoRuntime",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3000
            };
        }

        Color tint = ClampComfortColor(Color.Lerp(calmTint, particleTint, 0.65f)) * particleBrightness;
        tint.a = Mathf.Clamp01(particleTint.a);

        if (particleRuntimeMaterial.HasProperty(BaseColorId))
            particleRuntimeMaterial.SetColor(BaseColorId, tint);

        if (particleRuntimeMaterial.HasProperty(ColorId))
            particleRuntimeMaterial.SetColor(ColorId, tint);

        if (particleRuntimeMaterial.HasProperty(SurfaceId))
            particleRuntimeMaterial.SetFloat(SurfaceId, 1f);

        if (particleRuntimeMaterial.HasProperty(BlendId))
            particleRuntimeMaterial.SetFloat(BlendId, 2f);

        if (particleRuntimeMaterial.HasProperty(ZWriteId))
            particleRuntimeMaterial.SetFloat(ZWriteId, 0f);

        AssignParticleMaterial(idleLoopVfx);
        AssignParticleMaterial(enterBurstVfx);
    }

    private static Shader ResolveParticleShader()
    {
        string[] candidates =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Universal Render Pipeline/Unlit",
            "Unlit/Color"
        };

        foreach (string candidate in candidates)
        {
            Shader shader = Shader.Find(candidate);
            if (shader != null)
                return shader;
        }

        return null;
    }

    private void AssignParticleMaterial(ParticleSystem system)
    {
        if (system == null || particleRuntimeMaterial == null)
            return;

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer == null)
            renderer = system.gameObject.AddComponent<ParticleSystemRenderer>();

        renderer.sharedMaterial = particleRuntimeMaterial;
        renderer.trailMaterial = particleRuntimeMaterial;
    }

    private void DestroyParticleRuntimeMaterial()
    {
        if (particleRuntimeMaterial == null)
            return;

        if (Application.isPlaying)
            Destroy(particleRuntimeMaterial);
        else
            DestroyImmediate(particleRuntimeMaterial);

        particleRuntimeMaterial = null;
    }

    private Gradient BuildIdleGradient()
    {
        Color start = ClampComfortColor(Color.Lerp(calmAccent, calmTint, 0.25f));
        Color mid = ClampComfortColor(calmTint);
        Color end = ClampComfortColor(Color.Lerp(calmTint, Color.white, 0.35f));

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(start, 0f),
                new GradientColorKey(mid, 0.55f),
                new GradientColorKey(end, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.22f, 0.16f),
                new GradientAlphaKey(0.18f, 0.72f),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }

    private Gradient BuildEnterGradient()
    {
        Color start = ClampComfortColor(Color.Lerp(calmTint, Color.white, 0.2f));
        Color end = ClampComfortColor(Color.Lerp(calmAccent, calmTint, 0.7f));

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(start, 0f),
                new GradientColorKey(end, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0.3f, 0f),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }

    private static Color ClampComfortColor(Color color)
    {
        return new Color(
            Mathf.Clamp01(color.r),
            Mathf.Clamp01(color.g),
            Mathf.Clamp01(color.b),
            Mathf.Clamp01(color.a));
    }

    private float EstimatePortalRadius()
    {
        Collider sourceCollider = triggerCollider != null ? triggerCollider : GetComponent<Collider>();
        if (sourceCollider == null)
            return 0.45f;

        Bounds bounds = sourceCollider.bounds;
        float worldRadius = Mathf.Max(0.2f, Mathf.Min(bounds.extents.x, bounds.extents.z));
        float lossy = Mathf.Max(0.0001f, (transform.lossyScale.x + transform.lossyScale.z) * 0.5f);
        return Mathf.Clamp(worldRadius / lossy, 0.2f, 1.2f);
    }

    private void ApplyCalmTintToPortalGlass(Transform portalRoot)
    {
        if (portalRoot == null)
            return;

        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();

        Color tint = ClampComfortColor(Color.Lerp(calmAccent, calmTint, 0.55f));
        Color emission = tint * glassEmission;

        MeshRenderer[] renderers = portalRoot.GetComponentsInChildren<MeshRenderer>(true);
        bool applied = false;
        foreach (MeshRenderer meshRenderer in renderers)
        {
            if (meshRenderer == null || !NameContains(meshRenderer.transform, "glass"))
                continue;

            meshRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, tint);
            propertyBlock.SetColor(ColorId, tint);
            propertyBlock.SetColor(EmissionColorId, emission);
            meshRenderer.SetPropertyBlock(propertyBlock);
            applied = true;
        }

        if (!applied)
        {
            MeshRenderer selfRenderer = GetComponent<MeshRenderer>();
            if (selfRenderer != null)
            {
                selfRenderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(BaseColorId, tint);
                propertyBlock.SetColor(ColorId, tint);
                propertyBlock.SetColor(EmissionColorId, emission);
                selfRenderer.SetPropertyBlock(propertyBlock);
            }
        }
    }

    private static Transform FindPortalRoot(Transform start)
    {
        if (start == null) return null;

        Transform current = start;
        Transform best = null;

        while (current != null)
        {
            if (NameContains(current, "portal"))
                best = current;

            current = current.parent;
        }

        return best;
    }

    private static bool NameContains(Transform transformRef, string keyword)
    {
        if (transformRef == null || string.IsNullOrEmpty(keyword))
            return false;

        return transformRef.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsEnteringFromFront(Collider other)
    {
        Vector3 forward = portalForwardReference != null ? portalForwardReference.forward : transform.forward;
        Vector3 toOther = other.bounds.center - transform.position;
        if (toOther.sqrMagnitude < 0.0001f) return true;

        return Vector3.Dot(forward, toOther.normalized) > 0f;
    }

    private IEnumerator TransitionRoutine()
    {
        isTransitioning = true;

        onTransitionStarted?.Invoke();

        SceneXROriginEnforcer.PrepareForSceneTransition();

        if (stopIdleLoopOnEnter && idleLoopVfx != null)
            idleLoopVfx.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        if (playEnterBurstVfx && enterBurstVfx != null)
            enterBurstVfx.Play(true);

        if (portalAnimator != null && !string.IsNullOrEmpty(enterAnimatorTrigger))
            portalAnimator.SetTrigger(enterAnimatorTrigger);

        if (portalAudioSource != null && enterSfx != null)
            portalAudioSource.PlayOneShot(enterSfx, enterSfxVolume);

        float transitionDelay = Mathf.Max(loadDelay, enterSpinDuration);

        if (fadeCanvas != null && fadeDuration > 0f)
        {
            float preFadeWait = Mathf.Max(0f, transitionDelay - fadeDuration);
            if (preFadeWait > 0f)
            {
                fadeCanvas.gameObject.SetActive(true);
                fadeCanvas.blocksRaycasts = false;
                fadeCanvas.interactable = false;
                fadeCanvas.alpha = 0f;

                float elapsed = 0f;
                while (elapsed < preFadeWait)
                {
                    float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                    elapsed += dt;
                    fadeCanvas.alpha = Mathf.Lerp(0f, vignetteMaxAlpha, Mathf.Clamp01(elapsed / preFadeWait));
                    yield return null;
                }
            }

            yield return FadeCanvas(vignetteMaxAlpha, 1f, fadeDuration);
        }
        else if (transitionDelay > 0f)
        {
            yield return WaitSeconds(transitionDelay);
        }

        _activeTransition = null;
        yield return LoadTargetScene();
    }

    private void OnDisable()
    {
        activePlayerTriggerIds.Clear();
    }

    private IEnumerator FadeCanvas(float from, float to, float duration)
    {
        fadeCanvas.gameObject.SetActive(true);
        fadeCanvas.blocksRaycasts = true;
        fadeCanvas.alpha = from;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            fadeCanvas.alpha = Mathf.Lerp(from, to, t);
            yield return null;
        }

        fadeCanvas.alpha = to;
    }

    private IEnumerator FadeVignette(float from, float to, float duration)
    {
        if (fadeCanvas == null) yield break;

        fadeCanvas.gameObject.SetActive(true);
        fadeCanvas.blocksRaycasts = false;
        fadeCanvas.interactable = false;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            fadeCanvas.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        fadeCanvas.alpha = to;
        if (to <= 0f)
            fadeCanvas.gameObject.SetActive(false);
    }

    private IEnumerator WaitSeconds(float seconds)
    {
        if (seconds <= 0f) yield break;

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator LoadTargetScene()
    {
        if (string.IsNullOrWhiteSpace(targetScene))
        {
            Debug.LogError("[PortalSceneTransition] Target scene is empty.", this);
            isTransitioning = false;
            yield break;
        }

        string sceneInput = targetScene.Trim().Replace('\\', '/');
        int buildIndex = SceneUtility.GetBuildIndexByScenePath(sceneInput);

        AsyncOperation loadOperation;
        if (buildIndex >= 0)
        {
            loadOperation = SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Single);
        }
        else
        {
            string sceneName = sceneInput.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(sceneInput)
                : sceneInput;

            loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        }

        if (loadOperation == null)
        {
            Debug.LogError($"[PortalSceneTransition] Could not load '{targetScene}'. Ensure it is in Build Settings.", this);
            isTransitioning = false;
            yield break;
        }

        while (!loadOperation.isDone)
            yield return null;
    }

    private void OnDestroy()
    {
        DestroyParticleRuntimeMaterial();
    }
}
