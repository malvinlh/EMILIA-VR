using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Manages VR ↔ MR passthrough transitions on Meta Quest 3 via OpenXR.
/// Uses ARCameraManager + ARCameraBackground (Meta Quest: Camera Passthrough feature)
/// to toggle passthrough, with a fade quad for smooth black-out transitions.
///
/// During passthrough the camera culling mask is stripped to a single
/// "passthrough-visible" layer so all VR scene geometry disappears and
/// the underlay passthrough feed shows through.  Only objects on that
/// layer (fade quad, instruction text, indicators) remain visible.
///
/// Setup:
///   1. Enable "Meta Quest: Camera (Passthrough)" in OpenXR Feature Groups.
///   2. Add an ARSession GameObject to the scene.
///   3. Create a layer named "PassthroughUI" (or use the default layer 31)
///      and assign it in the inspector.
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

    [Header("Passthrough Rendering")]
    [Tooltip("Layer index used for objects that must remain visible during " +
             "passthrough (fade quad, instruction text, indicators). " +
             "Everything on other layers is hidden.")]
    [Range(0, 31)]
    public int passthroughUILayer = 31;

    public bool IsPassthroughActive { get; private set; }
    public bool IsTransitioning { get; private set; }

    public event Action OnPassthroughEntered;
    public event Action OnPassthroughExited;

    private GameObject fadeQuadObj;
    private MeshRenderer fadeRenderer;
    private Material fadeMaterial;

    // Saved camera state to restore on exit
    private Color savedBackgroundColor;
    private CameraClearFlags savedClearFlags;
    private int savedCullingMask;

    private ARCameraManager arCameraManager;
    private ARCameraBackground arCameraBackground;

    private void Awake()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        // Ensure the VR culling mask includes the passthrough UI layer
        // so the fade quad renders during VR→passthrough transitions.
        mainCamera.cullingMask |= (1 << passthroughUILayer);

        CreateFadeQuad();
        EnsureARComponents();
    }

    /// <summary>
    /// Ensures ARCameraManager and ARCameraBackground exist on the main camera
    /// but ARCameraBackground is disabled until passthrough is needed.
    /// </summary>
    private void EnsureARComponents()
    {
        if (mainCamera == null) return;

        arCameraManager = mainCamera.GetComponent<ARCameraManager>();
        if (arCameraManager == null)
            arCameraManager = mainCamera.gameObject.AddComponent<ARCameraManager>();

        arCameraBackground = mainCamera.GetComponent<ARCameraBackground>();
        if (arCameraBackground == null)
            arCameraBackground = mainCamera.gameObject.AddComponent<ARCameraBackground>();

        // ARCameraManager must stay enabled so the Meta passthrough camera
        // feature can bind to it during OpenXR initialisation.  Only the
        // ARCameraBackground (which actually renders the passthrough feed)
        // is disabled until we need it.
        arCameraManager.enabled = true;
        arCameraBackground.enabled = false;
    }

    // ================================================================
    // PUBLIC API
    // ================================================================

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

    /// <summary>
    /// Returns the layer index used for passthrough-visible objects.
    /// Other scripts should assign this layer to any GameObjects that
    /// must remain visible during passthrough (e.g. instruction text).
    /// </summary>
    public int GetPassthroughUILayer() => passthroughUILayer;

    // ================================================================
    // TRANSITION
    // ================================================================

    private IEnumerator TransitionCoroutine(bool toPassthrough, Action onComplete)
    {
        IsTransitioning = true;

        // Phase 1: Fade to black (current mode — full culling mask still active)
        yield return FadeCoroutine(0f, 1f, fadeDuration);

        // Phase 2: Switch passthrough while screen is black
        if (toPassthrough)
        {
            // Save current camera state
            savedBackgroundColor = mainCamera.backgroundColor;
            savedClearFlags = mainCamera.clearFlags;
            savedCullingMask = mainCamera.cullingMask;

            // Enable passthrough underlay
            arCameraBackground.enabled = true;

            // Transparent background lets passthrough show through
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // Strip culling mask: only render the passthrough UI layer
            // (fade quad, instruction text, indicators).
            // All VR scene geometry stops rendering → eye buffer is transparent
            // → the passthrough underlay is fully visible.
            mainCamera.cullingMask = 1 << passthroughUILayer;

            IsPassthroughActive = true;
            OnPassthroughEntered?.Invoke();
            Debug.Log($"[PassthroughManager] Passthrough entered. " +
                      $"CullingMask={mainCamera.cullingMask}, " +
                      $"ClearFlags={mainCamera.clearFlags}, " +
                      $"BgColor={mainCamera.backgroundColor}, " +
                      $"ARCameraBackground.enabled={arCameraBackground.enabled}, " +
                      $"ARCameraManager.enabled={arCameraManager.enabled}");
        }
        else
        {
            // Disable passthrough underlay
            arCameraBackground.enabled = false;

            // Restore camera to VR state
            mainCamera.clearFlags = savedClearFlags;
            mainCamera.backgroundColor = savedBackgroundColor;
            mainCamera.cullingMask = savedCullingMask;

            IsPassthroughActive = false;
            OnPassthroughExited?.Invoke();
            Debug.Log("[PassthroughManager] Passthrough exited (back to VR).");
        }

        // Phase 3: Fade from black (new mode — culling mask already switched)
        yield return FadeCoroutine(1f, 0f, fadeDuration);

        IsTransitioning = false;
        onComplete?.Invoke();
    }

    // ================================================================
    // FADE
    // ================================================================

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

    // ================================================================
    // FADE QUAD SETUP
    // ================================================================

    private void CreateFadeQuad()
    {
        fadeQuadObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fadeQuadObj.name = "PassthroughFadeQuad";
        fadeQuadObj.transform.SetParent(mainCamera.transform, false);

        // Put the fade quad on the passthrough UI layer so it renders
        // in both VR mode (full culling mask includes this layer) and
        // passthrough mode (culling mask = only this layer).
        fadeQuadObj.layer = passthroughUILayer;

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

    // ================================================================
    // HELPERS
    // ================================================================

    /// <summary>
    /// Recursively sets the layer on a GameObject and all its children.
    /// Useful for callers that need to make objects visible during passthrough.
    /// </summary>
    public static void SetLayerRecursive(GameObject obj, int layer)
    {
        if (obj == null) return;
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private void OnDestroy()
    {
        if (fadeMaterial != null)
            Destroy(fadeMaterial);
    }
}
