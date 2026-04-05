using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BatchNavMeshBakeTool
{
    private static readonly string[] TargetScenePaths =
    {
        "Assets/Scenes/use/3D_Chat.unity",
        "Assets/Scenes/use/3D_Journal.unity"
    };

    [MenuItem("Tools/NavMesh/Bake 3D_Chat + 3D_Journal")]
    public static void BakeTargetScenesNavMesh()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        string originalScenePath = SceneManager.GetActiveScene().path;
        var bakedScenes = new List<string>();

        try
        {
            foreach (string scenePath in TargetScenePaths)
            {
                if (!File.Exists(scenePath))
                {
                    Debug.LogWarning($"[BatchNavMeshBakeTool] Scene not found: {scenePath}");
                    continue;
                }

                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                if (!scene.IsValid())
                {
                    Debug.LogError($"[BatchNavMeshBakeTool] Failed to open scene: {scenePath}");
                    continue;
                }

                Debug.Log($"[BatchNavMeshBakeTool] Baking NavMesh: {scenePath}");
                NavMeshBuilder.BuildNavMesh();

                EditorSceneManager.MarkSceneDirty(scene);
                if (EditorSceneManager.SaveScene(scene))
                    bakedScenes.Add(scenePath);
                else
                    Debug.LogError($"[BatchNavMeshBakeTool] Failed to save scene after bake: {scenePath}");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[BatchNavMeshBakeTool] NavMesh bake completed. Baked {bakedScenes.Count} scene(s).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BatchNavMeshBakeTool] NavMesh bake failed: {ex.Message}\n{ex}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(originalScenePath) && File.Exists(originalScenePath))
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }
    }

    [MenuItem("Tools/NavMesh/Bake 3D_Chat + 3D_Journal", true)]
    private static bool ValidateBakeTargetScenesNavMesh()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
