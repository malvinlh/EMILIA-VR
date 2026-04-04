using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class MeshRendererNoColliderFinder
{
    [MenuItem("Tools/Find MeshRenderers Without Colliders")]
    static void FindObjects()
    {
        var allGOs = GameObject.FindObjectsOfType<GameObject>(true); // include inactive
        List<GameObject> results = new List<GameObject>();

        foreach (var go in allGOs)
        {
            // Must have MeshRenderer
            if (go.GetComponent<MeshRenderer>() == null)
                continue;

            // Check if ANY collider exists
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                continue;

            results.Add(go);
        }

        if (results.Count > 0)
        {
            Selection.objects = results.ToArray();
            Debug.Log($"Found {results.Count} GameObjects with MeshRenderer and NO Collider.");
        }
        else
        {
            Debug.Log("No matching GameObjects found.");
        }
    }
}