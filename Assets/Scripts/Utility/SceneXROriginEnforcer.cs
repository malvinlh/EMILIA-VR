using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;

[DefaultExecutionOrder(-1000)]
public class SceneXROriginEnforcer : MonoBehaviour
{
    private static SceneXROriginEnforcer instance;

    [Tooltip("Enable debug logging for scene origin enforcement.")]
    public bool debugLogs = true;

    [Tooltip("Destroy XROrigin instances that belong to other scenes (e.g. DontDestroyOnLoad leftovers).")]
    public bool destroyPersistentOrigins = true;

    // Ensure one persistent enforcer exists before any scene objects begin their startup work.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitializeEnforcer()
    {
        if (instance != null) return;

        if (GameObject.Find("SceneXROriginEnforcer") != null) return;

        var go = new GameObject("SceneXROriginEnforcer");
        DontDestroyOnLoad(go);
        go.AddComponent<SceneXROriginEnforcer>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public static void PrepareForSceneTransition()
    {
        if (instance == null)
            return;

        instance.CleanupOriginsOutsideScene(SceneManager.GetActiveScene(), "pre-transition");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Scene loaded: {scene.name}, enforcing XROrigins.");

        CleanupOriginsOutsideScene(scene, "scene-loaded");

        // Ensure the scene-local origin is enabled and active after a short delay (let scene objects initialize)
        StartCoroutine(ActivateSceneOriginNextFrame(scene));
    }

    private void CleanupOriginsOutsideScene(Scene scene, string reason)
    {
        var origins = Object.FindObjectsOfType<XROrigin>(true).ToList();

        if (debugLogs)
            Debug.Log($"[SceneXROriginEnforcer] Cleanup '{reason}': found {origins.Count} XROrigins.");

        foreach (var origin in origins)
        {
            if (origin == null)
                continue;

            var originScene = origin.gameObject.scene;
            bool isSceneLocal = originScene == scene;
            if (!isSceneLocal)
            {
                if (destroyPersistentOrigins)
                {
                    if (debugLogs)
                        Debug.Log($"[SceneXROriginEnforcer] Destroying stale XROrigin '{origin.gameObject.name}' (scene '{originScene.name}', reason '{reason}').");
                    Destroy(origin.gameObject);
                }
                else if (debugLogs)
                {
                    Debug.Log($"[SceneXROriginEnforcer] Would destroy stale XROrigin '{origin.gameObject.name}' (scene '{originScene.name}', reason '{reason}').");
                }
            }
            else if (debugLogs)
            {
                Debug.Log($"[SceneXROriginEnforcer] Keeping scene-local XROrigin '{origin.gameObject.name}' (reason '{reason}').");
            }
        }
    }

    private IEnumerator ActivateSceneOriginNextFrame(Scene scene)
    {
        yield return null;

        var remainingOrigins = Object.FindObjectsOfType<XROrigin>(true)
            .Where(o => o != null && o.gameObject.scene == scene).ToList();

        if (remainingOrigins.Count == 0)
        {
            if (debugLogs) Debug.LogWarning($"[SceneXROriginEnforcer] No XROrigin found in scene '{scene.name}'.");
            yield break;
        }

        var sceneOrigin = remainingOrigins.First();

        if (!sceneOrigin.gameObject.activeInHierarchy)
        {
            sceneOrigin.gameObject.SetActive(true);
            if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Activated XROrigin '{sceneOrigin.gameObject.name}'.");
        }

        if (!sceneOrigin.enabled)
        {
            sceneOrigin.enabled = true;
            if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Enabled XROrigin component on '{sceneOrigin.gameObject.name}'.");
        }

        // Optionally, re-resolve systems that cache the XROrigin. This is intentionally generic:
        // systems should re-find XROrigin in their Start/OnEnable or expose a public Resolve/Refresh method.
        if (debugLogs)
        {
            var names = string.Join(", ", remainingOrigins.Select(o => o.gameObject.name));
            Debug.Log($"[SceneXROriginEnforcer] Scene '{scene.name}' XROrigin(s): {names}");
        }

        TryRefreshLoginSceneBindings(scene);
    }

    private void TryRefreshLoginSceneBindings(Scene scene)
    {
        if (!scene.IsValid())
            return;

        if (scene.name.IndexOf("Login", System.StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var loginSetup = Object.FindObjectOfType<LoginSceneXRSetup>(true);
        if (loginSetup != null)
            loginSetup.enabled = true;

        var bridges = Object.FindObjectsOfType<VRLoginHandwritingBridge>(true);
        foreach (var bridge in bridges)
        {
            if (bridge == null)
                continue;

            InvokePrivateNoArg(bridge, "EnforceControllerOnlyInteractionMode");
            InvokePrivateNoArg(bridge, "ResolveInputReferences");
            InvokePrivateNoArg(bridge, "ResolveCameraOffsetTransform");
        }
    }

    private static void InvokePrivateNoArg(Object target, string methodName)
    {
        if (target == null || string.IsNullOrEmpty(methodName))
            return;

        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (method == null || method.GetParameters().Length != 0)
            return;

        method.Invoke(target, null);
    }
}