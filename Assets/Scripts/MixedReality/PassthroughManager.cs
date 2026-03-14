using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Manages VR ↔ MR passthrough transitions on Meta Quest 3 via OpenXR.
/// Uses a fade quad parented to the camera for smooth black-out transitions.
/// Requires com.unity.xr.meta-openxr package with Meta Quest Environment feature enabled.
/// </summary>
public class PassthroughManager : MonoBehaviour
{
    [Header("Transition Settings")]
    [Tooltip("Duration of each fade half (fade-out + fade-in).")]
    [Range(0.1f, 1.5f)]
    public float fadeDuration = 0.5f;

    [Header("References")]
    [Tooltip("Main camera (auto-found from Camera.main if null).")]
    public Camera mainCamera;

    public bool IsPassthroughActive { get; private set; }
    public bool IsTransitioning { get; private set; }

    public event Action OnPassthroughEntered;
    public event Action OnPassthroughExited;

    private GameObject fadeQuadObj;
    private MeshRenderer fadeRenderer;
    private Material fadeMaterial;
    private Color savedBackgroundColor;
    private CameraClearFlags savedClearFlags;

    private void Awake()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        CreateFadeQuad();
    }

    /// <summary>
    /// Transition from VR to MR passthrough with a fade-through-black effect.
    /// </summary>
    public void EnterPassthrough(Action onComplete = null)
    {
        if (IsPassthroughActive || IsTransitioning) return;
        StartCoroutine(TransitionCoroutine(toPassthrough: true, onComplete));
    }

    /// <summary>
    /// Transition from MR passthrough back to VR with a fade-through-black effect.
    /// </summary>
    public void ExitPassthrough(Action onComplete = null)
    {
        if (!IsPassthroughActive || IsTransitioning) return;
        StartCoroutine(TransitionCoroutine(toPassthrough: false, onComplete));
    }

    private IEnumerator TransitionCoroutine(bool toPassthrough, Action onComplete)
    {
        IsTransitioning = true;

        // Phase 1: Fade to black
        yield return FadeCoroutine(0f, 1f, fadeDuration);

        // Phase 2: Switch blend mode while screen is black
        if (toPassthrough)
        {
            savedBackgroundColor = mainCamera.backgroundColor;
            savedClearFlags = mainCamera.clearFlags;

            // Transparent background lets passthrough show through
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);

            SetPassthroughBlendMode(true);
            IsPassthroughActive = true;
            OnPassthroughEntered?.Invoke();
        }
        else
        {
            SetPassthroughBlendMode(false);

            mainCamera.clearFlags = savedClearFlags;
            mainCamera.backgroundColor = savedBackgroundColor;

            IsPassthroughActive = false;
            OnPassthroughExited?.Invoke();
        }

        // Phase 3: Fade from black
        yield return FadeCoroutine(1f, 0f, fadeDuration);

        IsTransitioning = false;
        onComplete?.Invoke();
    }

    private IEnumerator FadeCoroutine(float fromAlpha, float toAlpha, float duration)
    {
        fadeQuadObj.SetActive(true);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            // Ease-in-out (smoothstep)
            t = t * t * (3f - 2f * t);
            float alpha = Mathf.Lerp(fromAlpha, toAlpha, t);
            fadeMaterial.color = new Color(0f, 0f, 0f, alpha);
            yield return null;
        }

        fadeMaterial.color = new Color(0f, 0f, 0f, toAlpha);

        if (toAlpha <= 0f)
            fadeQuadObj.SetActive(false);
    }

    private void SetPassthroughBlendMode(bool enablePassthrough)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            // Use reflection to call MetaOpenXREnvironment.SetEnvironmentBlendMode
            // to avoid hard compile dependency when package may not be installed yet.
            var metaEnvType = System.Type.GetType(
                "UnityEngine.XR.OpenXR.Features.Meta.MetaOpenXREnvironment, " +
                "Unity.XR.Meta.OpenXR");

            if (metaEnvType != null)
            {
                // The enum XrEnvironmentBlendMode: AlphaBlend = 2, Opaque = 1
                var method = metaEnvType.GetMethod("SetEnvironmentBlendMode",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (method != null)
                {
                    // Get the enum type
                    var enumType = method.GetParameters()[0].ParameterType;
                    object blendMode = enablePassthrough
                        ? System.Enum.ToObject(enumType, 2)  // AlphaBlend
                        : System.Enum.ToObject(enumType, 1);  // Opaque

                    method.Invoke(null, new object[] { blendMode });
                    Debug.Log($"[PassthroughManager] Blend mode set to {(enablePassthrough ? "AlphaBlend (Passthrough)" : "Opaque (VR)")}");
                }
                else
                {
                    Debug.LogWarning("[PassthroughManager] SetEnvironmentBlendMode method not found.");
                }
            }
            else
            {
                Debug.LogWarning("[PassthroughManager] MetaOpenXREnvironment type not found. " +
                    "Ensure com.unity.xr.meta-openxr package is installed and Meta Quest Environment feature is enabled.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PassthroughManager] Failed to set blend mode: {e.Message}");
        }
#else
        Debug.Log($"[PassthroughManager] (Editor) Would set passthrough to {enablePassthrough}");
#endif
    }

    private void CreateFadeQuad()
    {
        fadeQuadObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fadeQuadObj.name = "PassthroughFadeQuad";
        fadeQuadObj.transform.SetParent(mainCamera.transform, false);

        // Position just in front of the near clip plane
        float nearClip = mainCamera.nearClipPlane;
        fadeQuadObj.transform.localPosition = new Vector3(0f, 0f, nearClip + 0.01f);

        // Scale to cover the entire view
        float height = 2f * (nearClip + 0.01f) * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float width = height * mainCamera.aspect;
        fadeQuadObj.transform.localScale = new Vector3(width * 1.5f, height * 1.5f, 1f);

        // Remove collider
        Destroy(fadeQuadObj.GetComponent<Collider>());

        // Create unlit transparent material
        fadeMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        fadeMaterial.SetFloat("_Surface", 1f); // Transparent
        fadeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        fadeMaterial.SetFloat("_Blend", 0f); // Alpha
        fadeMaterial.SetFloat("_ZWrite", 0f);
        fadeMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        fadeMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        fadeMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        fadeMaterial.color = new Color(0f, 0f, 0f, 0f);

        fadeRenderer = fadeQuadObj.GetComponent<MeshRenderer>();
        fadeRenderer.material = fadeMaterial;
        fadeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        fadeRenderer.receiveShadows = false;

        fadeQuadObj.SetActive(false);
    }

    private void OnDestroy()
    {
        if (fadeMaterial != null)
            Destroy(fadeMaterial);
    }
}
