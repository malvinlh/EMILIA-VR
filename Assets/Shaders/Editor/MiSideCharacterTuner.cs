using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-converts and tunes all AZKi character materials to MiSide/Character shader.
/// Auto-detects material category (skin, hair, eyes, clothing, accessory, special)
/// using name matching (Japanese and English), texture filename heuristics, and
/// texture color analysis as fallback.
///
/// Access via: Tools > MiSide > Character Material Tuner
/// </summary>
public class MiSideCharacterTuner : EditorWindow
{
    private const string DefaultMaterialsPath = "Assets/Graphics/3D/Character/AZKi/materials";

    // User-configurable path
    private string _materialsPath = DefaultMaterialsPath;

    private bool _dryRun = true;
    private bool _deleteRigidMats = true;
    private Vector2 _scrollPos;
    private string _logOutput = "";

    // Per-category override toggles (let user override auto-detection)
    private Dictionary<string, CharacterCategory> _manualOverrides = new Dictionary<string, CharacterCategory>();

    // Preview data
    private List<MaterialPreview> _previews = new List<MaterialPreview>();
    private bool _previewGenerated = false;

    [MenuItem("Tools/MiSide/Character Material Tuner")]
    public static void ShowWindow()
    {
        var window = GetWindow<MiSideCharacterTuner>("Character Material Tuner");
        window.minSize = new Vector2(580, 600);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("MiSide Character Material Tuner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Converts all materials in the target folder to MiSide/Character shader.\n" +
            "Auto-detects category (Skin, Hair, Eyes, Clothing, Accessory, Special)\n" +
            "using material name, texture filename, and texture color analysis.\n\n" +
            "Step 1: Click 'Scan & Preview' to see detected categories.\n" +
            "Step 2: Override any incorrect detections using the dropdowns.\n" +
            "Step 3: Click 'Apply All' to convert.",
            MessageType.Info);

        EditorGUILayout.Space();

        // Materials path
        EditorGUILayout.BeginHorizontal();
        _materialsPath = EditorGUILayout.TextField("Materials Folder", _materialsPath);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            string selected = EditorUtility.OpenFolderPanel("Select Materials Folder", "Assets", "");
            if (!string.IsNullOrEmpty(selected))
            {
                // Convert absolute path to relative
                if (selected.Contains("Assets"))
                    _materialsPath = "Assets" + selected.Substring(selected.IndexOf("Assets") + 6);
            }
        }
        EditorGUILayout.EndHorizontal();

        _deleteRigidMats = EditorGUILayout.Toggle("Delete mmd_tools_rigid_* materials", _deleteRigidMats);

        EditorGUILayout.Space();

        // ---- Scan & Preview ----
        if (GUILayout.Button("Scan & Preview", GUILayout.Height(28)))
        {
            ScanMaterials();
        }

        if (_previewGenerated && _previews.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Detected {_previews.Count} materials:", EditorStyles.boldLabel);

            // Column headers
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Material Name", EditorStyles.miniLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField("Auto-Detected", EditorStyles.miniLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("Override", EditorStyles.miniLabel, GUILayout.Width(120));
            EditorGUILayout.LabelField("Detection Method", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(300));

            foreach (var preview in _previews)
            {
                if (preview.isRigid)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    EditorGUILayout.LabelField(preview.materialName, GUILayout.Width(180));
                    EditorGUILayout.LabelField("[DELETE]", GUILayout.Width(90));
                    GUI.color = Color.white;
                    EditorGUILayout.LabelField("", GUILayout.Width(120));
                    EditorGUILayout.LabelField("rigid body material");
                    EditorGUILayout.EndHorizontal();
                    continue;
                }

                EditorGUILayout.BeginHorizontal();

                // Color-code by category
                GUI.color = GetCategoryColor(preview.finalCategory);
                EditorGUILayout.LabelField(preview.materialName, GUILayout.Width(180));
                GUI.color = Color.white;

                EditorGUILayout.LabelField(preview.autoCategory.ToString(), GUILayout.Width(90));

                // Override dropdown
                CharacterCategory overrideVal = preview.finalCategory;
                CharacterCategory newVal = (CharacterCategory)EditorGUILayout.EnumPopup(overrideVal, GUILayout.Width(120));
                if (newVal != overrideVal)
                {
                    _manualOverrides[preview.materialName] = newVal;
                    preview.finalCategory = newVal;
                }

                EditorGUILayout.LabelField(preview.detectionMethod, EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            // ---- Apply ----
            GUI.backgroundColor = new Color(0.4f, 0.85f, 0.5f);
            if (GUILayout.Button("Apply All", GUILayout.Height(32)))
            {
                _dryRun = false;
                ApplyPresets();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();
        }

        // Log output
        if (!string.IsNullOrEmpty(_logOutput))
        {
            EditorGUILayout.LabelField("Log:", EditorStyles.boldLabel);
            EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(200));
            EditorGUILayout.TextArea(_logOutput, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
    }

    // ===================================================================
    // SCAN — Build preview list with auto-detection
    // ===================================================================

    private void ScanMaterials()
    {
        _previews.Clear();
        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { _materialsPath });

        foreach (string guid in matGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            var preview = new MaterialPreview
            {
                materialName = mat.name,
                assetPath = path,
                isRigid = mat.name.StartsWith("mmd_tools_rigid")
            };

            if (!preview.isRigid)
            {
                var (category, method) = DetectCategory(mat);
                preview.autoCategory = category;
                preview.detectionMethod = method;

                // Apply manual override if exists
                if (_manualOverrides.TryGetValue(mat.name, out CharacterCategory overrideCategory))
                    preview.finalCategory = overrideCategory;
                else
                    preview.finalCategory = category;
            }

            _previews.Add(preview);
        }

        _previewGenerated = true;
        Repaint();
    }

    // ===================================================================
    // APPLY — Convert materials and apply presets
    // ===================================================================

    private void ApplyPresets()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== APPLYING CHARACTER PRESETS ===");
        sb.AppendLine($"Source: {_materialsPath}\n");

        Shader charShader = Shader.Find("MiSide/Character");
        if (charShader == null)
        {
            sb.AppendLine("ERROR: Could not find MiSide/Character shader!");
            _logOutput = sb.ToString();
            Repaint();
            return;
        }

        int tuned = 0, deleted = 0;
        var rigidToDelete = new List<string>();

        foreach (var preview in _previews)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(preview.assetPath);
            if (mat == null) continue;

            if (preview.isRigid)
            {
                if (_deleteRigidMats)
                {
                    rigidToDelete.Add(preview.assetPath);
                    sb.AppendLine($"  [DELETE] {preview.materialName}");
                }
                continue;
            }

            CharacterCategory category = preview.finalCategory;
            sb.AppendLine($"  [{category.ToString().ToUpper()}] {preview.materialName} ({preview.detectionMethod})");

            Undo.RecordObject(mat, "MiSide Character Tuner");

            // Switch shader — preserve base texture
            if (mat.shader != charShader)
            {
                Texture baseTex = TryGetBaseTexture(mat);
                Color baseCol = TryGetBaseColor(mat);

                mat.shader = charShader;

                mat.SetTexture("_MainTex", baseTex);
                mat.SetColor("_BaseColor", baseCol);
            }

            ApplyCategorySettings(mat, category);
            SyncKeywords(mat);
            EditorUtility.SetDirty(mat);
            tuned++;
        }

        // Delete rigid materials
        if (_deleteRigidMats)
        {
            foreach (string path in rigidToDelete)
            {
                AssetDatabase.DeleteAsset(path);
                deleted++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        sb.AppendLine($"\n--- Summary ---");
        sb.AppendLine($"Converted & tuned: {tuned}");
        sb.AppendLine($"Deleted rigid materials: {deleted}");
        sb.AppendLine("\nDone! Check materials in Inspector.");

        _logOutput = sb.ToString();

        // Refresh preview
        ScanMaterials();
    }

    // ===================================================================
    // CATEGORY DETECTION — multi-strategy
    // ===================================================================

    public enum CharacterCategory
    {
        Skin,
        Hair,
        Eyes,
        Clothing,
        Accessory,
        Special
    }

    /// <summary>
    /// Auto-detect material category using three strategies in order:
    /// 1. Japanese material name matching
    /// 2. Texture filename heuristics
    /// 3. Texture average color analysis (skin tone detection)
    /// Returns (category, detection method description).
    /// </summary>
    private static (CharacterCategory, string) DetectCategory(Material mat)
    {
        string name = mat.name.ToLowerInvariant();

        // --- Strategy 1: Japanese name matching ---
        var jpResult = DetectByJapaneseName(mat.name);
        if (jpResult.HasValue)
            return (jpResult.Value, "name (JP)");

        // --- Strategy 2: English/romanized name matching ---
        var enResult = DetectByEnglishName(name);
        if (enResult.HasValue)
            return (enResult.Value, "name (EN)");

        // --- Strategy 3: Texture filename heuristics ---
        Texture tex = TryGetBaseTexture(mat);
        if (tex != null)
        {
            string texName = tex.name.ToLowerInvariant();
            string texPath = AssetDatabase.GetAssetPath(tex).ToLowerInvariant();

            var texResult = DetectByTextureName(texName, texPath);
            if (texResult.HasValue)
                return (texResult.Value, $"texture: {tex.name}");

            // --- Strategy 4: Texture color analysis ---
            if (tex is Texture2D tex2D)
            {
                var colorResult = DetectByTextureColor(tex2D);
                if (colorResult.HasValue)
                    return (colorResult.Value, "color analysis");
            }
        }

        // Default: clothing (safest assumption for unknown materials)
        return (CharacterCategory.Clothing, "default");
    }

    private static CharacterCategory? DetectByJapaneseName(string name)
    {
        // Skin
        if (name == "\u4F53" || name == "\u982D") // 体, 頭
            return CharacterCategory.Skin;

        // Hair
        if (name == "\u9AEA" || name == "\u9AEA\u5F71" || name == "\u9AEA\u98FE\u308A") // 髪, 髪影, 髪飾り
            return CharacterCategory.Hair;

        // Eyes
        if (name == "\u76EE") // 目
            return CharacterCategory.Eyes;

        // Special: blush, paleness, eyebrows/lashes
        if (name == "\u982C\u67D3\u3081" || name == "\u9752\u8930\u3081" || name == "\u7709\u6BDB\u307E\u3064\u6BDB")
            return CharacterCategory.Special; // 頬染め, 青褪め, 眉毛まつ毛

        // Accessories
        if (name == "\u30E1\u30AC\u30CD" || name == "\u30AA\u30D7\u30B7\u30E7\u30F3" ||
            name == "\u30D6\u30ED\u30FC\u30C1" || name == "\u8155\u8F2A")
            return CharacterCategory.Accessory; // メガネ, オプション, ブローチ, 腕輪

        // Clothing keywords
        if (name == "\u670D" || name == "\u30B9\u30AB\u30FC\u30C8" ||
            name.Contains("\u30B9\u30AB\u30FC\u30C8") || // スカート
            name == "\u30D6\u30FC\u30C4" || // ブーツ
            name == "\u30BF\u30A4" || // タイ
            name == "\u30EA\u30DC\u30F3" || // リボン
            name == "\u30B8\u30C3\u30D1\u30FC") // ジッパー
            return CharacterCategory.Clothing;

        return null;
    }

    private static CharacterCategory? DetectByEnglishName(string nameLower)
    {
        // Skin
        if (nameLower.Contains("skin") || nameLower.Contains("body") || nameLower.Contains("face") ||
            nameLower.Contains("head") || nameLower.Contains("arm") || nameLower.Contains("leg") ||
            nameLower.Contains("hand") || nameLower.Contains("neck"))
            return CharacterCategory.Skin;

        // Hair
        if (nameLower.Contains("hair") || nameLower.Contains("bangs") || nameLower.Contains("ponytail") ||
            nameLower.Contains("ahoge") || nameLower.Contains("hairshadow"))
            return CharacterCategory.Hair;

        // Eyes
        if (nameLower.Contains("eye") && !nameLower.Contains("eyebrow") && !nameLower.Contains("eyelash"))
            return CharacterCategory.Eyes;

        // Special
        if (nameLower.Contains("blush") || nameLower.Contains("eyebrow") || nameLower.Contains("eyelash") ||
            nameLower.Contains("lash") || nameLower.Contains("brow") || nameLower.Contains("pale") ||
            nameLower.Contains("cheek") || nameLower.Contains("tear"))
            return CharacterCategory.Special;

        // Accessories
        if (nameLower.Contains("glass") || nameLower.Contains("ring") || nameLower.Contains("earring") ||
            nameLower.Contains("necklace") || nameLower.Contains("brooch") || nameLower.Contains("bracelet") ||
            nameLower.Contains("accessory") || nameLower.Contains("option") || nameLower.Contains("jewel"))
            return CharacterCategory.Accessory;

        // Clothing
        if (nameLower.Contains("cloth") || nameLower.Contains("shirt") || nameLower.Contains("skirt") ||
            nameLower.Contains("dress") || nameLower.Contains("pants") || nameLower.Contains("boot") ||
            nameLower.Contains("shoe") || nameLower.Contains("jacket") || nameLower.Contains("coat") ||
            nameLower.Contains("ribbon") || nameLower.Contains("tie") || nameLower.Contains("zipper") ||
            nameLower.Contains("sock") || nameLower.Contains("glove") || nameLower.Contains("belt") ||
            nameLower.Contains("hat") || nameLower.Contains("cape") || nameLower.Contains("collar"))
            return CharacterCategory.Clothing;

        return null;
    }

    private static CharacterCategory? DetectByTextureName(string texNameLower, string texPathLower)
    {
        string combined = texNameLower + " " + texPathLower;

        if (combined.Contains("eye") && !combined.Contains("eyebrow") && !combined.Contains("eyelash"))
            return CharacterCategory.Eyes;
        if (combined.Contains("skin") || combined.Contains("body") || combined.Contains("face"))
            return CharacterCategory.Skin;
        if (combined.Contains("hair"))
            return CharacterCategory.Hair;
        if (combined.Contains("blush") || combined.Contains("cheek") || combined.Contains("brow") || combined.Contains("lash"))
            return CharacterCategory.Special;

        return null;
    }

    /// <summary>
    /// Analyze the average color of the texture to detect skin tones.
    /// Samples a grid of pixels and checks for warm skin-tone hue ranges.
    /// </summary>
    private static CharacterCategory? DetectByTextureColor(Texture2D tex)
    {
        // Need readable texture
        if (!tex.isReadable)
            return null;

        int sampleCount = 0;
        float avgR = 0, avgG = 0, avgB = 0, avgA = 0;
        int stepX = Mathf.Max(1, tex.width / 8);
        int stepY = Mathf.Max(1, tex.height / 8);

        for (int x = stepX / 2; x < tex.width; x += stepX)
        {
            for (int y = stepY / 2; y < tex.height; y += stepY)
            {
                Color pixel = tex.GetPixel(x, y);
                if (pixel.a < 0.1f) continue; // Skip transparent
                avgR += pixel.r;
                avgG += pixel.g;
                avgB += pixel.b;
                avgA += pixel.a;
                sampleCount++;
            }
        }

        if (sampleCount < 4) return null;

        avgR /= sampleCount;
        avgG /= sampleCount;
        avgB /= sampleCount;
        avgA /= sampleCount;

        // Mostly transparent = likely special (blush overlay, etc.)
        if (avgA < 0.3f)
            return CharacterCategory.Special;

        // Skin detection: warm tone where R > G > B, with specific ranges
        float hue, sat, val;
        Color.RGBToHSV(new Color(avgR, avgG, avgB), out hue, out sat, out val);

        // Skin tones: hue 0-40 degrees (0.0-0.11 normalized), moderate saturation
        if (hue < 0.12f && sat > 0.15f && sat < 0.7f && val > 0.5f)
            return CharacterCategory.Skin;

        // Very saturated + specific hue ranges might be eyes
        if (sat > 0.5f && val > 0.4f)
            return CharacterCategory.Eyes;

        return null;
    }

    // ===================================================================
    // PRESET APPLICATION
    // ===================================================================

    private static void ApplyCategorySettings(Material mat, CharacterCategory category)
    {
        // --- Common — HSR-style: rich shadows, color-matched outlines ---
        mat.SetFloat("_GI_Intensity", 0.35f);
        mat.SetFloat("_Tweak_SystemShadowsLevel", 0.1f);
        mat.SetFloat("_HighColor_Power", 0f);
        mat.SetFloat("_ShadowSaturation", 1.2f);
        mat.SetFloat("_UnlitBlend", 0f);
        mat.SetFloat("_MinBrightness", 0.04f);

        switch (category)
        {
            case CharacterCategory.Skin:
                mat.SetColor("_1st_ShadeColor", new Color(0.82f, 0.68f, 0.65f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.5f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.08f);
                mat.SetColor("_2nd_ShadeColor", new Color(0.70f, 0.55f, 0.52f, 1f));
                mat.SetFloat("_2nd_ShadeColor_Step", 0.15f);
                mat.SetFloat("_2nd_ShadeColor_Feather", 0.08f);
                mat.SetFloat("_RimLight", 1f);
                mat.SetColor("_RimLightColor", new Color(0.85f, 0.75f, 0.68f, 1f));
                mat.SetFloat("_RimLight_Power", 8f);
                mat.SetFloat("_RimLight_InsideMask", 0.2f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.35f, 0.28f, 0.26f, 1f));
                mat.SetFloat("_Is_BlendBaseColor", 1f);
                mat.SetFloat("_Is_LightColor_Outline", 0f);
                mat.SetFloat("_MinBrightness", 0.05f);
                mat.SetFloat("_ShadowSaturation", 1.3f);
                break;

            case CharacterCategory.Hair:
                mat.SetColor("_1st_ShadeColor", new Color(0.75f, 0.65f, 0.62f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.5f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
                mat.SetColor("_2nd_ShadeColor", new Color(0.60f, 0.50f, 0.48f, 1f));
                mat.SetFloat("_2nd_ShadeColor_Step", 0.15f);
                mat.SetFloat("_2nd_ShadeColor_Feather", 0.08f);
                mat.SetFloat("_RimLight", 1f);
                mat.SetColor("_RimLightColor", new Color(0.85f, 0.75f, 0.68f, 1f));
                mat.SetFloat("_RimLight_Power", 8f);
                mat.SetFloat("_RimLight_InsideMask", 0.15f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.30f, 0.22f, 0.20f, 1f));
                mat.SetFloat("_Is_BlendBaseColor", 1f);
                mat.SetFloat("_Is_LightColor_Outline", 0f);
                mat.SetFloat("_MinBrightness", 0.04f);
                mat.SetFloat("_ShadowSaturation", 1.2f);
                break;

            case CharacterCategory.Eyes:
                mat.SetColor("_1st_ShadeColor", new Color(0.88f, 0.85f, 0.82f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.2f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.15f);
                mat.SetColor("_2nd_ShadeColor", new Color(0.78f, 0.75f, 0.72f, 1f));
                mat.SetFloat("_2nd_ShadeColor_Step", 0.05f);
                mat.SetFloat("_2nd_ShadeColor_Feather", 0.10f);
                mat.SetFloat("_RimLight", 0f);
                mat.SetFloat("_OUTLINE", 0f);
                mat.SetFloat("_Outline_Width", 0f);
                mat.SetFloat("_UnlitBlend", 0.7f);
                mat.SetFloat("_MinBrightness", 0.15f);
                mat.SetFloat("_GI_Intensity", 0.4f);
                mat.SetFloat("_Tweak_SystemShadowsLevel", 0.3f);
                break;

            case CharacterCategory.Clothing:
                mat.SetColor("_1st_ShadeColor", new Color(0.76f, 0.68f, 0.65f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.48f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
                mat.SetColor("_2nd_ShadeColor", new Color(0.62f, 0.54f, 0.52f, 1f));
                mat.SetFloat("_2nd_ShadeColor_Step", 0.15f);
                mat.SetFloat("_2nd_ShadeColor_Feather", 0.08f);
                mat.SetFloat("_RimLight", 1f);
                mat.SetColor("_RimLightColor", new Color(0.85f, 0.75f, 0.68f, 1f));
                mat.SetFloat("_RimLight_Power", 9f);
                mat.SetFloat("_RimLight_InsideMask", 0.15f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.32f, 0.25f, 0.23f, 1f));
                mat.SetFloat("_Is_BlendBaseColor", 1f);
                mat.SetFloat("_Is_LightColor_Outline", 0f);
                mat.SetFloat("_MinBrightness", 0.04f);
                break;

            case CharacterCategory.Accessory:
                mat.SetColor("_1st_ShadeColor", new Color(0.74f, 0.65f, 0.62f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.45f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
                mat.SetColor("_2nd_ShadeColor", new Color(0.58f, 0.50f, 0.48f, 1f));
                mat.SetFloat("_2nd_ShadeColor_Step", 0.12f);
                mat.SetFloat("_2nd_ShadeColor_Feather", 0.06f);
                mat.SetFloat("_RimLight", 0f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.2f);
                mat.SetColor("_Outline_Color", new Color(0.30f, 0.22f, 0.20f, 1f));
                mat.SetFloat("_Is_BlendBaseColor", 1f);
                mat.SetFloat("_Is_LightColor_Outline", 0f);
                mat.SetFloat("_MinBrightness", 0.04f);
                break;

            case CharacterCategory.Special:
                mat.SetColor("_1st_ShadeColor", new Color(0.88f, 0.82f, 0.80f, 1f));
                mat.SetFloat("_1st_ShadeColor_Step", 0.2f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.12f);
                mat.SetFloat("_RimLight", 0f);
                mat.SetFloat("_OUTLINE", 0f);
                mat.SetFloat("_Outline_Width", 0f);
                mat.SetFloat("_UnlitBlend", 0.6f);
                mat.SetFloat("_MinBrightness", 0.10f);
                break;
        }
    }

    // ===================================================================
    // UTILITIES
    // ===================================================================

    private static Texture TryGetBaseTexture(Material mat)
    {
        // Try common texture property names in order of likelihood
        string[] texProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Diffuse", "_Albedo" };
        foreach (string prop in texProps)
        {
            if (mat.HasProperty(prop))
            {
                Texture tex = mat.GetTexture(prop);
                if (tex != null) return tex;
            }
        }
        return null;
    }

    private static Color TryGetBaseColor(Material mat)
    {
        string[] colorProps = { "_BaseColor", "_Color", "_MainColor" };
        foreach (string prop in colorProps)
        {
            if (mat.HasProperty(prop))
                return mat.GetColor(prop);
        }
        return Color.white;
    }

    private static void SyncKeywords(Material mat)
    {
        SetKeyword(mat, "_RIMLIGHT_ON", mat.HasProperty("_RimLight") && mat.GetFloat("_RimLight") > 0.5f);
        SetKeyword(mat, "_OUTLINE_ON", mat.HasProperty("_OUTLINE") && mat.GetFloat("_OUTLINE") > 0.5f);
        SetKeyword(mat, "_ALPHATEST_ON", mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f);
    }

    private static void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled)
            mat.EnableKeyword(keyword);
        else
            mat.DisableKeyword(keyword);
    }

    private static Color GetCategoryColor(CharacterCategory cat)
    {
        switch (cat)
        {
            case CharacterCategory.Skin:      return new Color(1.0f, 0.85f, 0.8f);
            case CharacterCategory.Hair:      return new Color(0.85f, 0.75f, 1.0f);
            case CharacterCategory.Eyes:      return new Color(0.8f, 1.0f, 0.9f);
            case CharacterCategory.Clothing:  return new Color(0.85f, 0.9f, 1.0f);
            case CharacterCategory.Accessory: return new Color(1.0f, 1.0f, 0.8f);
            case CharacterCategory.Special:   return new Color(1.0f, 0.9f, 0.85f);
            default: return Color.white;
        }
    }

    // ===================================================================
    // PREVIEW DATA
    // ===================================================================

    private class MaterialPreview
    {
        public string materialName;
        public string assetPath;
        public bool isRigid;
        public CharacterCategory autoCategory;
        public CharacterCategory finalCategory;
        public string detectionMethod;
    }
}
