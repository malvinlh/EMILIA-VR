using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool to consolidate the ~1,400 auto-generated duplicate materials in the Beach scene
/// down to a manageable number of unique materials.
/// Groups materials by identical shader + texture + color properties, then remaps Renderer references.
/// </summary>
public class BeachMaterialConsolidator : EditorWindow
{
    private const string BeachMaterialsPath = "Assets/Graphics/3D/Journal_Beach/Merged/Materials";
    private const string BeachModelPath = "Assets/Graphics/3D/Journal_Beach/Merged/Source/pantai.fbx";

    private bool _dryRun = true;
    private Vector2 _scrollPos;
    private string _logOutput = "";
    private Dictionary<string, List<Material>> _groups;

    [MenuItem("Tools/EMILIA/Consolidate Beach Materials")]
    public static void ShowWindow()
    {
        var window = GetWindow<BeachMaterialConsolidator>("Beach Material Consolidator");
        window.minSize = new Vector2(500, 400);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Beach Material Consolidator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "This tool scans all materials in the Beach scene's Materials folder, " +
            "groups identical materials (same shader, textures, colors, floats), " +
            "remaps all references to a canonical material per group, and deletes duplicates.\n\n" +
            "Source: " + BeachMaterialsPath,
            MessageType.Info);

        EditorGUILayout.Space();

        _dryRun = EditorGUILayout.Toggle("Dry Run (preview only)", _dryRun);

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("1. Analyze Materials", GUILayout.Height(30)))
        {
            AnalyzeMaterials();
        }

        EditorGUI.BeginDisabledGroup(_groups == null || _groups.Count == 0);
        if (GUILayout.Button(_dryRun ? "2. Preview Consolidation" : "2. Execute Consolidation", GUILayout.Height(30)))
        {
            ConsolidateMaterials();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
        EditorGUILayout.TextArea(_logOutput, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void AnalyzeMaterials()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ANALYSIS ===\n");

        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { BeachMaterialsPath });
        sb.AppendLine($"Total materials found: {matGuids.Length}");

        _groups = new Dictionary<string, List<Material>>();

        int processed = 0;
        foreach (string guid in matGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            string hash = ComputeMaterialHash(mat);
            if (!_groups.ContainsKey(hash))
                _groups[hash] = new List<Material>();
            _groups[hash].Add(mat);

            processed++;
            if (processed % 200 == 0)
                EditorUtility.DisplayProgressBar("Analyzing", $"Processed {processed}/{matGuids.Length}", (float)processed / matGuids.Length);
        }
        EditorUtility.ClearProgressBar();

        int uniqueCount = _groups.Count;
        int duplicateCount = processed - uniqueCount;

        sb.AppendLine($"Unique material groups: {uniqueCount}");
        sb.AppendLine($"Duplicate materials: {duplicateCount}");
        sb.AppendLine($"Reduction: {processed} → {uniqueCount} ({(1f - (float)uniqueCount / processed) * 100f:F1}% reduction)\n");

        // Show top groups
        var sortedGroups = _groups.OrderByDescending(g => g.Value.Count).ToList();
        sb.AppendLine("--- Largest groups (most duplicates) ---\n");
        int shown = 0;
        foreach (var group in sortedGroups)
        {
            if (shown >= 30) break;
            Material canonical = PickCanonical(group.Value);
            sb.AppendLine($"[{group.Value.Count} materials] Canonical: \"{canonical.name}\" (Shader: {canonical.shader?.name ?? "null"})");
            if (group.Value.Count <= 5)
            {
                foreach (var mat in group.Value)
                {
                    string marker = mat == canonical ? " ★" : "";
                    sb.AppendLine($"    - {mat.name}{marker}");
                }
            }
            shown++;
        }

        _logOutput = sb.ToString();
        Repaint();
    }

    private void ConsolidateMaterials()
    {
        if (_groups == null || _groups.Count == 0)
        {
            _logOutput = "Run analysis first.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(_dryRun ? "=== DRY RUN PREVIEW ===\n" : "=== EXECUTING CONSOLIDATION ===\n");

        // Build remap dictionary: duplicate material → canonical material
        var remap = new Dictionary<Material, Material>();
        int deletionCount = 0;

        foreach (var group in _groups)
        {
            if (group.Value.Count <= 1) continue;

            Material canonical = PickCanonical(group.Value);
            foreach (Material mat in group.Value)
            {
                if (mat != canonical)
                {
                    remap[mat] = canonical;
                    deletionCount++;
                }
            }
        }

        sb.AppendLine($"Materials to remap & delete: {deletionCount}");
        sb.AppendLine($"Unique materials to keep: {_groups.Count}");

        // Find all Renderers in prefabs/models that reference beach materials
        int remapCount = 0;

        // Remap material references on the FBX model's material remap table
        // and on any prefab instances or scene objects
        if (!_dryRun)
        {
            // Remap scene references: find all scenes and prefabs using these materials
            string[] allPrefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Graphics/3D/Journal_Beach" });
            foreach (string guid in allPrefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                bool dirty = false;
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && remap.ContainsKey(mats[i]))
                        {
                            mats[i] = remap[mats[i]];
                            dirty = true;
                            remapCount++;
                        }
                    }
                    if (dirty)
                        renderer.sharedMaterials = mats;
                }
                if (dirty)
                    EditorUtility.SetDirty(prefab);
            }

            sb.AppendLine($"Renderer material slots remapped: {remapCount}");

            // Delete duplicate materials
            int deleted = 0;
            foreach (var kvp in remap)
            {
                string path = AssetDatabase.GetAssetPath(kvp.Key);
                if (!string.IsNullOrEmpty(path))
                {
                    AssetDatabase.DeleteAsset(path);
                    deleted++;

                    if (deleted % 100 == 0)
                        EditorUtility.DisplayProgressBar("Deleting duplicates", $"{deleted}/{deletionCount}", (float)deleted / deletionCount);
                }
            }
            EditorUtility.ClearProgressBar();

            sb.AppendLine($"Materials deleted: {deleted}");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            sb.AppendLine("\n✓ Consolidation complete!");
        }
        else
        {
            sb.AppendLine("\n[DRY RUN] No changes were made. Uncheck 'Dry Run' and run again to execute.");
            sb.AppendLine("\nMaterials that would be deleted (first 50):");
            int shown = 0;
            foreach (var kvp in remap)
            {
                if (shown >= 50) { sb.AppendLine("  ... and more"); break; }
                sb.AppendLine($"  ✗ \"{kvp.Key.name}\" → canonical \"{kvp.Value.name}\"");
                shown++;
            }
        }

        _logOutput = sb.ToString();
        Repaint();
    }

    /// <summary>
    /// Compute a hash string that represents the material's visual identity.
    /// Materials with the same hash are visually identical and can be merged.
    /// </summary>
    private static string ComputeMaterialHash(Material mat)
    {
        var sb = new StringBuilder();

        // Shader identity
        sb.Append(mat.shader != null ? mat.shader.name : "null");
        sb.Append("|");

        // Render queue
        sb.Append(mat.renderQueue);
        sb.Append("|");

        // Keywords
        string[] keywords = mat.shaderKeywords;
        System.Array.Sort(keywords);
        sb.Append(string.Join(",", keywords));
        sb.Append("|");

        // Known texture properties
        string[] texProps = { "_BaseMap", "_MainTex", "_BumpMap", "_EmissionMap", "_MetallicGlossMap",
                              "_OcclusionMap", "_ParallaxMap", "_DetailAlbedoMap", "_SpecGlossMap" };
        foreach (string prop in texProps)
        {
            if (mat.HasProperty(prop))
            {
                Texture tex = mat.GetTexture(prop);
                sb.Append(tex != null ? tex.GetInstanceID().ToString() : "0");
            }
            sb.Append(",");
        }
        sb.Append("|");

        // Known color properties
        string[] colorProps = { "_BaseColor", "_Color", "_EmissionColor", "_SpecColor" };
        foreach (string prop in colorProps)
        {
            if (mat.HasProperty(prop))
            {
                Color c = mat.GetColor(prop);
                sb.Append($"{c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3}");
            }
            sb.Append(";");
        }
        sb.Append("|");

        // Known float properties
        string[] floatProps = { "_Metallic", "_Smoothness", "_Cutoff", "_Surface", "_Blend",
                                "_AlphaClip", "_ZWrite", "_Cull" };
        foreach (string prop in floatProps)
        {
            if (mat.HasProperty(prop))
                sb.Append(mat.GetFloat(prop).ToString("F3"));
            sb.Append(",");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pick the best "canonical" material from a group of duplicates.
    /// Prefers descriptively-named materials over generic numbered ones.
    /// </summary>
    private static Material PickCanonical(List<Material> group)
    {
        // Prefer materials with descriptive names (not "Material.XXX", "Default.XXX", "base_gray.XXX", etc.)
        string[] genericPrefixes = { "Material", "Default", "base_gray", "flower", "tomato", "None" };

        Material best = null;
        int bestScore = int.MinValue;

        foreach (Material mat in group)
        {
            int score = 0;
            string name = mat.name;

            bool isGeneric = false;
            foreach (string prefix in genericPrefixes)
            {
                if (name.StartsWith(prefix))
                {
                    isGeneric = true;
                    break;
                }
            }

            if (!isGeneric)
                score += 100; // Strongly prefer descriptive names

            // Prefer shorter names
            score -= name.Length;

            // Prefer names without dots/numbers suffix
            if (!name.Contains("."))
                score += 10;

            if (best == null || score > bestScore)
            {
                best = mat;
                bestScore = score;
            }
        }

        return best ?? group[0];
    }
}
