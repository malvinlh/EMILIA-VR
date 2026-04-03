using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds and animates a calm portal ring using a lightweight URP shader.
/// Idle mode is subtle; enter mode spins up for transition anticipation.
/// </summary>
[DisallowMultipleComponent]
public class PortalCalmRingVfx : MonoBehaviour
{
    [Header("Auto Build")]
    [SerializeField] private bool autoCreateRenderer = true;
    [SerializeField] private string ringObjectName = "PortalCalmRing_Auto";
    [SerializeField] private Transform ringAnchor;
    [SerializeField] private MeshRenderer ringRenderer;

    [Header("Appearance")]
    [SerializeField] [ColorUsage(true, true)] private Color baseColor = new Color(0.42f, 0.76f, 0.86f, 1f);
    [SerializeField] [ColorUsage(true, true)] private Color accentColor = new Color(0.82f, 0.94f, 0.98f, 1f);
    [SerializeField] [Range(0.1f, 2.5f)] private float worldRadius = 0.48f;
    [SerializeField] [Range(0.02f, 0.5f)] private float ringThickness = 0.22f;
    [SerializeField] [Range(4f, 20f)] private float arcCount = 10f;
    [SerializeField] [Range(0f, 1f)] private float sparkleStrength = 0.3f;
    [SerializeField] [Range(0f, 1f)] private float softNoise = 0.5f;

    [Header("Placement")]
    [SerializeField] private bool preserveManualRingTransform = true;
    [SerializeField] private Vector3 defaultLocalPosition = new Vector3(0f, 0.01f, 0f);
    [SerializeField] private Vector3 defaultLocalEuler = new Vector3(90f, 0f, 0f);

    [Header("Animation")]
    [SerializeField] private bool useUnscaledTime = true;
    [SerializeField] [Range(0f, 5f)] private float idleSpinSpeed = 0.9f;
    [SerializeField] [Range(0f, 8f)] private float enterSpinSpeed = 4.5f;
    [SerializeField] [Range(0f, 3f)] private float idleIntensity = 0.62f;
    [SerializeField] [Range(0f, 3f)] private float enterIntensity = 1.15f;
    [SerializeField] [Range(1f, 20f)] private float spinResponse = 7f;
    [SerializeField] [Range(1f, 20f)] private float intensityResponse = 6f;

    private const string RingShaderName = "EMILIA/PortalCalmRingURP";

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int AccentColorId = Shader.PropertyToID("_AccentColor");
    private static readonly int InnerRadiusId = Shader.PropertyToID("_InnerRadius");
    private static readonly int OuterRadiusId = Shader.PropertyToID("_OuterRadius");
    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    private static readonly int SpinSpeedId = Shader.PropertyToID("_SpinSpeed");
    private static readonly int ArcCountId = Shader.PropertyToID("_ArcCount");
    private static readonly int SparkleStrengthId = Shader.PropertyToID("_SparkleStrength");
    private static readonly int SoftNoiseId = Shader.PropertyToID("_SoftNoise");

    private Material runtimeMaterial;
    private float targetSpin;
    private float targetIntensity;
    private float currentSpin;
    private float currentIntensity;
    private float enterSpinRemaining;

    public float CurrentSpinSpeed => currentSpin <= 0f ? idleSpinSpeed : currentSpin;

    public float CurrentSpin01
    {
        get
        {
            float denominator = enterSpinSpeed - idleSpinSpeed;
            if (Mathf.Abs(denominator) < 0.0001f)
                return targetSpin > idleSpinSpeed ? 1f : 0f;

            return Mathf.Clamp01((CurrentSpinSpeed - idleSpinSpeed) / denominator);
        }
    }

    private void Awake()
    {
        EnsureRingRenderer();
        SetIdleState(immediate: true);
        PushMaterialProperties();
    }

    private void OnValidate()
    {
        EnsureRingRenderer();
        PushMaterialProperties();
    }

    private void Update()
    {
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f)
            return;

        if (enterSpinRemaining > 0f)
            enterSpinRemaining = Mathf.Max(0f, enterSpinRemaining - dt);

        if (enterSpinRemaining <= 0f)
        {
            targetSpin = idleSpinSpeed;
            targetIntensity = idleIntensity;
        }

        float spinLerp = 1f - Mathf.Exp(-spinResponse * dt);
        float intensityLerp = 1f - Mathf.Exp(-intensityResponse * dt);

        currentSpin = Mathf.Lerp(currentSpin, targetSpin, spinLerp);
        currentIntensity = Mathf.Lerp(currentIntensity, targetIntensity, intensityLerp);

        PushMaterialProperties();
    }

    public void AutoConfigure(Transform portalRoot, Transform preferredAnchor, float estimatedRadius)
    {
        if (preferredAnchor != null)
            ringAnchor = preferredAnchor;
        else if (portalRoot != null)
            ringAnchor = portalRoot;

        if (estimatedRadius > 0f)
            worldRadius = Mathf.Clamp(estimatedRadius, 0.18f, 1.3f);

        EnsureRingRenderer();
        SetIdleState(immediate: true);
        PushMaterialProperties();
    }

    public void SetIdleState(bool immediate)
    {
        targetSpin = idleSpinSpeed;
        targetIntensity = idleIntensity;

        if (immediate)
        {
            currentSpin = targetSpin;
            currentIntensity = targetIntensity;
        }

        enterSpinRemaining = 0f;
    }

    public void TriggerEnterSpin(float seconds)
    {
        targetSpin = enterSpinSpeed;
        targetIntensity = enterIntensity;
        enterSpinRemaining = Mathf.Max(enterSpinRemaining, seconds);
    }

    private void EnsureRingRenderer()
    {
        if (ringAnchor == null)
            ringAnchor = transform;

        bool createdNow = false;

        if (ringRenderer == null)
        {
            Transform existing = ringAnchor.Find(ringObjectName);
            if (existing != null)
                ringRenderer = existing.GetComponent<MeshRenderer>();
        }

        if (ringRenderer == null && autoCreateRenderer)
        {
            ringRenderer = CreateRingRenderer();
            createdNow = ringRenderer != null;
        }

        if (ringRenderer == null)
            return;

        ringRenderer.shadowCastingMode = ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;
        ringRenderer.lightProbeUsage = LightProbeUsage.Off;
        ringRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        ringRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        MeshFilter filter = ringRenderer.GetComponent<MeshFilter>();
        if (filter != null)
        {
            float diameter = Mathf.Max(0.2f, worldRadius * 2f);
            ringRenderer.transform.localScale = new Vector3(diameter, diameter, 1f);

            if (createdNow || !preserveManualRingTransform)
            {
                ringRenderer.transform.localPosition = defaultLocalPosition;
                ringRenderer.transform.localRotation = Quaternion.Euler(defaultLocalEuler);
            }
        }

        EnsureMaterial();
    }

    private MeshRenderer CreateRingRenderer()
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = ringObjectName;
        quad.layer = gameObject.layer;
        quad.transform.SetParent(ringAnchor, false);

        Collider quadCollider = quad.GetComponent<Collider>();
        if (quadCollider != null)
        {
            if (Application.isPlaying)
                Destroy(quadCollider);
            else
                DestroyImmediate(quadCollider);
        }

        return quad.GetComponent<MeshRenderer>();
    }

    private void EnsureMaterial()
    {
        if (ringRenderer == null)
            return;

        Shader ringShader = Shader.Find(RingShaderName);
        if (ringShader == null)
        {
            ringShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (ringShader == null)
                ringShader = Shader.Find("Unlit/Color");
        }

        if (runtimeMaterial == null || runtimeMaterial.shader != ringShader)
        {
            if (runtimeMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(runtimeMaterial);
                else
                    DestroyImmediate(runtimeMaterial);
            }

            runtimeMaterial = new Material(ringShader);
            runtimeMaterial.name = "PortalCalmRing_Runtime";
        }

        ringRenderer.sharedMaterial = runtimeMaterial;
    }

    private void PushMaterialProperties()
    {
        if (ringRenderer == null)
            return;

        EnsureMaterial();
        if (runtimeMaterial == null)
            return;

        float outerRadius = 0.95f;
        float innerRadius = Mathf.Clamp01(outerRadius - ringThickness);

        runtimeMaterial.SetColor(BaseColorId, baseColor);
        runtimeMaterial.SetColor(AccentColorId, accentColor);
        runtimeMaterial.SetFloat(InnerRadiusId, innerRadius);
        runtimeMaterial.SetFloat(OuterRadiusId, outerRadius);
        runtimeMaterial.SetFloat(IntensityId, currentIntensity <= 0f ? idleIntensity : currentIntensity);
        runtimeMaterial.SetFloat(SpinSpeedId, currentSpin <= 0f ? idleSpinSpeed : currentSpin);
        runtimeMaterial.SetFloat(ArcCountId, arcCount);
        runtimeMaterial.SetFloat(SparkleStrengthId, sparkleStrength);
        runtimeMaterial.SetFloat(SoftNoiseId, softNoise);
    }

    private void OnDestroy()
    {
        if (runtimeMaterial == null)
            return;

        if (Application.isPlaying)
            Destroy(runtimeMaterial);
        else
            DestroyImmediate(runtimeMaterial);
    }
}
