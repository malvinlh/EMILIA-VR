using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch converts environment materials from URP/Lit to MiSide/Environment toon shader.
/// Detects material type (standard, emissive, cutout, water) by name pattern and applies
/// appropriate presets.
/// </summary>
public class MiSideMaterialConverter : EditorWindow
{
    private static readonly string URP_LIT_GUID = "933532a4fcc9baf4fa0491de14d08ed7";

    private enum ConvertTarget
    {
        ChatRoom,
        Beach,
        BeachDiorama,
        SelectedFolder
    }

    private ConvertTarget _target = ConvertTarget.ChatRoom;
    private string _customFolder = "";
    private bool _dryRun = true;
    private Vector2 _scrollPos;
    private string _logOutput = "";

    [MenuItem("Tools/MiSide/Convert Materials to Toon")]
    public static void ShowWindow()
    {
        var window = GetWindow<MiSideMaterialConverter>("MiSide Material Converter");
        window.minSize = new Vector2(550, 450);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("MiSide Material Converter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Converts URP/Lit materials to MiSide/Environment toon shader.\n" +
            "Detects material type by name and applies appropriate presets:\n" +
            "• Standard opaque → MiSide/Environment (default toon)\n" +
            "• Cutout (plants/flowers) → MiSide/Environment + alpha clip\n" +
            "• Emissive (lamps/monitors) → MiSide/Environment + emission\n" +
            "• Water (sea/surf) → MiSide/ToonWater",
            MessageType.Info);

        EditorGUILayout.Space();

        _target = (ConvertTarget)EditorGUILayout.EnumPopup("Target", _target);
        if (_target == ConvertTarget.SelectedFolder)
        {
            EditorGUILayout.BeginHorizontal();
            _customFolder = EditorGUILayout.TextField("Folder", _customFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFolderPanel("Select Material Folder", "Assets", "");
                if (!string.IsNullOrEmpty(path))
                {
                    int assetsIdx = path.IndexOf("Assets");
                    if (assetsIdx >= 0)
                        _customFolder = path.Substring(assetsIdx);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        _dryRun = EditorGUILayout.Toggle("Dry Run (preview only)", _dryRun);

        EditorGUILayout.Space();

        if (GUILayout.Button(_dryRun ? "Preview Conversion" : "Execute Conversion", GUILayout.Height(30)))
        {
            ConvertMaterials();
        }

        EditorGUILayout.Space();

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
        EditorGUILayout.TextArea(_logOutput, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private string GetTargetFolder()
    {
        switch (_target)
        {
            case ConvertTarget.ChatRoom:
                return "Assets/Graphics/3D/Chat_Room/materials";
            case ConvertTarget.Beach:
                return "Assets/Graphics/3D/Journal_Beach/Merged/Materials";
            case ConvertTarget.BeachDiorama:
                return "Assets/External/beach-related/lets-go-to-the-beach-beach-themed-diorama/materials";
            case ConvertTarget.SelectedFolder:
                return _customFolder;
            default:
                return "";
        }
    }

    private void ConvertMaterials()
    {
        string folder = GetTargetFolder();
        if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
        {
            _logOutput = $"Invalid folder: {folder}";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(_dryRun ? "=== DRY RUN PREVIEW ===" : "=== EXECUTING CONVERSION ===");
        sb.AppendLine($"Source folder: {folder}\n");

        // Find target shaders
        Shader toonShader = Shader.Find("MiSide/Environment");
        Shader waterShader = Shader.Find("MiSide/ToonWater");

        if (toonShader == null)
        {
            _logOutput = "ERROR: Could not find shader 'MiSide/Environment'. Make sure it's compiled.";
            return;
        }
        if (waterShader == null)
        {
            sb.AppendLine("WARNING: Could not find shader 'MiSide/ToonWater'. Water materials will use MiSide/Environment instead.\n");
        }

        // Find URP/Lit shader for comparison
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Shader urpSimpleLit = Shader.Find("Universal Render Pipeline/Simple Lit");

        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { folder });
        sb.AppendLine($"Total materials found: {matGuids.Length}\n");

        int converted = 0, skipped = 0, errors = 0;
        int emissiveCount = 0, cutoutCount = 0, waterCount = 0, standardCount = 0;

        foreach (string guid in matGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { errors++; continue; }

            // For BeachDiorama, accept any shader (Autodesk Interactive, etc.)
            // For other targets, only convert URP/Lit or URP/Simple Lit
            bool isUrp = mat.shader == urpLit || mat.shader == urpSimpleLit;
            bool isDiorama = _target == ConvertTarget.BeachDiorama;
            if (!isUrp && !isDiorama)
            {
                skipped++;
                continue;
            }

            // Skip materials already on a MiSide shader
            if (mat.shader.name.StartsWith("MiSide/"))
            {
                skipped++;
                continue;
            }

            MaterialType type = DetectMaterialType(mat.name, mat);

            string typeLabel;
            switch (type)
            {
                case MaterialType.Emissive: typeLabel = "EMISSIVE"; emissiveCount++; break;
                case MaterialType.Cutout: typeLabel = "CUTOUT"; cutoutCount++; break;
                case MaterialType.Water: typeLabel = "WATER"; waterCount++; break;
                default: typeLabel = "STANDARD"; standardCount++; break;
            }

            if (_dryRun)
            {
                sb.AppendLine($"  [{typeLabel}] {mat.name}");
            }
            else
            {
                ConvertSingleMaterial(mat, type, toonShader, waterShader);
                EditorUtility.SetDirty(mat);
            }

            converted++;

            if (converted % 50 == 0)
                EditorUtility.DisplayProgressBar("Converting", $"{converted}/{matGuids.Length}", (float)converted / matGuids.Length);
        }

        EditorUtility.ClearProgressBar();

        if (!_dryRun)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        sb.AppendLine($"\n--- Summary ---");
        sb.AppendLine($"Converted: {converted}");
        sb.AppendLine($"  Standard: {standardCount}");
        sb.AppendLine($"  Emissive: {emissiveCount}");
        sb.AppendLine($"  Cutout:   {cutoutCount}");
        sb.AppendLine($"  Water:    {waterCount}");
        sb.AppendLine($"Skipped (non-URP/Lit): {skipped}");
        sb.AppendLine($"Errors: {errors}");

        if (_dryRun)
            sb.AppendLine("\n[DRY RUN] No changes made. Uncheck 'Dry Run' to execute.");
        else
            sb.AppendLine("\n✓ Conversion complete!");

        _logOutput = sb.ToString();
        Repaint();
    }

    private static void ConvertSingleMaterial(Material mat, MaterialType type, Shader toonShader, Shader waterShader)
    {
        // Save existing properties before shader switch
        // Try URP properties first, then Autodesk Interactive properties
        Texture baseMap = null;
        if (mat.HasProperty("_BaseMap"))
            baseMap = mat.GetTexture("_BaseMap");
        if (baseMap == null && mat.HasProperty("_MainTex"))
            baseMap = mat.GetTexture("_MainTex");
        if (baseMap == null && mat.HasProperty("_BASE_COLOR_MAP"))
            baseMap = mat.GetTexture("_BASE_COLOR_MAP");

        Color baseColor = Color.white;
        if (mat.HasProperty("_BaseColor"))
            baseColor = mat.GetColor("_BaseColor");
        else if (mat.HasProperty("_BASE_COLOR"))
            baseColor = mat.GetColor("_BASE_COLOR");

        Color emissionColor = Color.black;
        if (mat.HasProperty("_EmissionColor"))
            emissionColor = mat.GetColor("_EmissionColor");
        else if (mat.HasProperty("_EMISSION_COLOR"))
            emissionColor = mat.GetColor("_EMISSION_COLOR");

        Texture emissionMap = null;
        if (mat.HasProperty("_EmissionMap"))
            emissionMap = mat.GetTexture("_EmissionMap");
        if (emissionMap == null && mat.HasProperty("_EMISSION_COLOR_MAP"))
            emissionMap = mat.GetTexture("_EMISSION_COLOR_MAP");

        // Read opacity map for cutout materials (Autodesk Interactive uses _OPACITY_MAP)
        Texture opacityMap = null;
        if (mat.HasProperty("_OPACITY_MAP"))
            opacityMap = mat.GetTexture("_OPACITY_MAP");

        // Switch shader
        if (type == MaterialType.Water && waterShader != null)
        {
            mat.shader = waterShader;
            ApplyWaterDefaults(mat);
            return;
        }

        mat.shader = toonShader;

        // Restore base texture and color
        mat.SetTexture("_BaseMap", baseMap);
        mat.SetColor("_BaseColor", baseColor);

        // Apply default toon parameters
        mat.SetColor("_ShadowColor", new Color(0.85f, 0.75f, 0.72f, 1f));
        mat.SetFloat("_ShadowStep", 0.5f);
        mat.SetFloat("_ShadowFeather", 0.05f);
        mat.SetFloat("_ShadowIntensity", 0.6f);
        mat.SetFloat("_RimLightToggle", 0f);
        mat.DisableKeyword("_RIMLIGHT");
        mat.SetColor("_RimColor", new Color(1f, 0.9f, 0.85f, 1f));
        mat.SetFloat("_RimPower", 4f);
        mat.SetFloat("_RimIntensity", 0.15f);

        // Type-specific settings
        switch (type)
        {
            case MaterialType.Emissive:
                mat.SetFloat("_EmissionToggle", 1f);
                mat.EnableKeyword("_EMISSION");
                mat.SetTexture("_EmissionMap", emissionMap);
                // Boost emission for bloom threshold pickup
                Color boostedEmission = new Color(
                    Mathf.Max(emissionColor.r, 0.5f) * 1.5f,
                    Mathf.Max(emissionColor.g, 0.5f) * 1.5f,
                    Mathf.Max(emissionColor.b, 0.5f) * 1.5f,
                    1f
                );
                mat.SetColor("_EmissionColor", boostedEmission);
                mat.SetFloat("_AlphaClip", 0f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", (float)CullMode.Back);
                mat.renderQueue = 2000;
                break;

            case MaterialType.Cutout:
                mat.SetFloat("_AlphaClip", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", (float)CullMode.Off); // Double-sided for plants
                mat.SetFloat("_EmissionToggle", 0f);
                mat.DisableKeyword("_EMISSION");
                // If an opacity map was present, bake it into the base map's alpha channel
                // For now, set the base map and let the alpha clip use the texture's alpha
                if (opacityMap != null)
                {
                    // Use opacity map as a hint: the base texture likely already has useful alpha
                    // or the opacity map is a separate mask. Store reference for manual review.
                    Debug.Log($"[MiSide Converter] Cutout material '{mat.name}' had separate opacity map: {opacityMap.name}");
                }
                mat.renderQueue = 2450;
                break;

            default: // Standard
                mat.SetFloat("_EmissionToggle", 0f);
                mat.DisableKeyword("_EMISSION");
                mat.SetFloat("_AlphaClip", 0f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", (float)CullMode.Back);
                mat.renderQueue = 2000;
                break;
        }
    }

    private static void ApplyWaterDefaults(Material mat)
    {
        mat.SetColor("_ShallowColor", new Color(0.45f, 0.78f, 0.88f, 0.85f));
        mat.SetColor("_DeepColor", new Color(0.18f, 0.38f, 0.58f, 0.85f));
        mat.SetFloat("_ColorGradientScale", 1f);
        mat.SetFloat("_ColorGradientOffset", 0f);
        mat.SetColor("_FoamColor", new Color(1f, 1f, 1f, 0.5f));
        mat.SetFloat("_FoamSpeed", 0.08f);
        mat.SetFloat("_FoamScale", 1f);
        mat.SetFloat("_FoamIntensity", 0.3f);
        mat.SetFloat("_WaveAmplitude", 0.03f);
        mat.SetFloat("_WaveFrequency", 2f);
        mat.SetFloat("_WaveSpeed", 1.5f);
        mat.renderQueue = 3000;
    }

    // --------- Material type detection ---------

    private enum MaterialType
    {
        Standard,
        Emissive,
        Cutout,
        Water
    }

    private static readonly string[] EmissivePatterns =
    {
        "Emission", "Lamp", "Monitor", "Clock", "Moon", "Street",
        "Light", "Glow", "Screen", "LED", "Neon"
    };

    private static readonly string[] CutoutPatterns =
    {
        "Cutout", "Cut", "_Cut", "Leaves", "Plants", "Decals",
        "Ropes", "Fillers", "Cable", "Hammock"
    };

    private static readonly string[] WaterPatterns =
    {
        "sea", "surf", "water", "ocean", "wave"
    };

    /// <summary>
    /// Detect material type by name patterns AND material properties.
    /// Now also inspects the material's textures and colors for smarter classification.
    /// </summary>
    private static MaterialType DetectMaterialType(string name, Material mat = null)
    {
        string lower = name.ToLowerInvariant();

        // Check water first (most specific)
        foreach (string pattern in WaterPatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                return MaterialType.Water;
        }

        // Check emissive by name
        foreach (string pattern in EmissivePatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                return MaterialType.Emissive;
        }

        // Check emissive by material properties (non-black emission color)
        if (mat != null)
        {
            Color emCol = Color.black;
            if (mat.HasProperty("_EMISSION_COLOR"))
                emCol = mat.GetColor("_EMISSION_COLOR");
            else if (mat.HasProperty("_EmissionColor"))
                emCol = mat.GetColor("_EmissionColor");

            if (emCol.r + emCol.g + emCol.b > 0.05f)
                return MaterialType.Emissive;
        }

        // Check cutout by name
        foreach (string pattern in CutoutPatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                return MaterialType.Cutout;
        }

        // Check cutout by material properties (has opacity map texture)
        if (mat != null && mat.HasProperty("_OPACITY_MAP"))
        {
            Texture opacityTex = mat.GetTexture("_OPACITY_MAP");
            if (opacityTex != null)
                return MaterialType.Cutout;
        }

        return MaterialType.Standard;
    }
}
