using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;

[DefaultExecutionOrder(-1000)]
public class SceneXROriginEnforcer : MonoBehaviour
{
    [Tooltip("Enable debug logging for scene origin enforcement.")]
    public bool debugLogs = true;

    [Tooltip("Destroy XROrigin instances that belong to other scenes (e.g. DontDestroyOnLoad leftovers).")]
    public bool destroyPersistentOrigins = true;

    // Ensure one persistent enforcer exists at runtime startup
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InitializeEnforcer()
    {
        if (GameObject.Find("SceneXROriginEnforcer") != null) return;
        var go = new GameObject("SceneXROriginEnforcer");
        DontDestroyOnLoad(go);
        go.AddComponent<SceneXROriginEnforcer>();
    }

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Scene loaded: {scene.name}, enforcing XROrigins.");

        var origins = FindObjectsOfType<XROrigin>(true).ToList();
        if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Found {origins.Count} XROrigins.");

        // Destroy any origins that do NOT belong to the newly loaded scene (likely DontDestroyOnLoad leftovers)
        foreach (var origin in origins)
        {
            if (origin == null) continue;
            var originScene = origin.gameObject.scene;
            bool isSceneLocal = originScene == scene;
            if (!isSceneLocal)
            {
                if (destroyPersistentOrigins)
                {
                    if (debugLogs) Debug.Log($"[SceneXROriginEnforcer] Destroying persistent XROrigin '{origin.gameObject.name}' (scene '{originScene.name}').");
                    Destroy(origin.gameObject);
                }
                else if (debugLogs)
                {
                    Debug.Log($"[SceneXROriginEnforcer] Would destroy persistent XROrigin '{origin.gameObject.name}' (scene '{originScene.name}').");
                }
            }
            else if (debugLogs)
            {
                Debug.Log($"[SceneXROriginEnforcer] Keeping scene-local XROrigin '{origin.gameObject.name}'.");
            }
        }

        // Ensure the scene-local origin is enabled and active after a short delay (let scene objects initialize)
        StartCoroutine(ActivateSceneOriginNextFrame(scene));
    }

    IEnumerator ActivateSceneOriginNextFrame(Scene scene)
    {
        yield return null;

        var remainingOrigins = FindObjectsOfType<XROrigin>(true)
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
    }
}