#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public static class EmiliaHandInteractorSetup
{
    // Prefab provided by XR Interaction Toolkit Hands demo
    static readonly string HandsPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.3.1/Hands Interaction Demo/Prefabs/XR Origin Hands (XR Rig).prefab";
    static readonly string[] TargetScenes = new[] {
        "Assets/Scenes/use/3D_Journal_Bedroom.unity",
        "Assets/Scenes/use/3D_Journal_Beach.unity"
    };

    [MenuItem("Emilia/Apply Hand Interactors To Scenes")]
    public static void ApplyHandsToScenes()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandsPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"Emilia: Hands prefab not found at {HandsPrefabPath}");
            return;
        }

        foreach (var scenePath in TargetScenes)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Emilia: Could not open scene {scenePath}");
                continue;
            }

            var existing = FindExistingHandsRig(scene, prefab);
            if (existing != null)
            {
                Debug.Log($"Emilia: Hands rig already present in {scenePath} (root '{existing.name}').");
                EnsureVintageMicButtons(scene);
                EditorSceneManager.SaveScene(scene);
                continue;
            }

            var xrOrigin = UnityEngine.Object.FindObjectOfType<XROrigin>();
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (xrOrigin != null)
                inst.transform.SetParent(xrOrigin.transform, false);
            EditorSceneManager.MarkSceneDirty(scene);
            EnsureVintageMicButtons(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"Emilia: Instantiated hands rig in {scenePath} (xrOrigin? {xrOrigin != null}).");
        }
    }

    static GameObject FindExistingHandsRig(Scene scene, GameObject sourcePrefab)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(root) as GameObject;
            if (src == sourcePrefab) return root;
        }
        return null;
    }

    static void EnsureVintageMicButtons(Scene scene)
    {
        var buttons = Object.FindObjectsOfType<VintageMicButton>(true);
        foreach (var button in buttons)
        {
            if (!button.gameObject.scene.IsValid() || button.gameObject.scene != scene)
                continue;

            var collider = button.GetComponent<BoxCollider>() ?? button.gameObject.AddComponent<BoxCollider>();
            collider.isTrigger = false;

            var interactable = button.GetComponent<XRSimpleInteractable>() ?? button.gameObject.AddComponent<XRSimpleInteractable>();
            interactable.colliders.Clear();
            interactable.colliders.Add(collider);

            EditorUtility.SetDirty(button);
            EditorUtility.SetDirty(interactable);
            EditorUtility.SetDirty(collider);
        }

        EditorSceneManager.MarkSceneDirty(scene);
    }
}
#endif
