using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch applies MiSide-style toon parameters to all AZKi character materials.
/// Uses name-based category detection (skin, hair, clothing, special) to apply
/// per-category presets. Also enables outlines with warm-brown color.
/// </summary>
public class MiSideCharacterTuner : EditorWindow
{
    private const string CharacterMaterialsPath = "Assets/Graphics/3D/Character_AZKi/materials";

    private bool _dryRun = true;
    private bool _deleteRigidMats = true;
    private Vector2 _scrollPos;
    private string _logOutput = "";

    // Tunable parameters exposed in the editor window
    private float _shadeFeather = 0.06f;
    private float _baseShadeFeather = 0.06f;
    private float _secondShadeStep = 0.15f;
    private float _secondShadeFeather = 0.1f;
    private float _outlineWidth = 0.3f;
    private Color _outlineColor = new Color(0.2f, 0.15f, 0.15f, 1f);
    private float _giIntensity = 0.3f;

    [MenuItem("Tools/MiSide/Apply Character Toon Preset")]
    public static void ShowWindow()
    {
        var window = GetWindow<MiSideCharacterTuner>("MiSide Character Tuner");
        window.minSize = new Vector2(500, 500);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("MiSide Character Tuner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Applies MiSide-style toon parameters to all AZKi character materials.\n" +
            "• Softens shade boundaries (feathering)\n" +
            "• Enables warm-brown outlines\n" +
            "• Applies per-category presets (skin, hair, clothing, special)\n" +
            "• Optionally deletes unused mmd_tools_rigid_* materials",
            MessageType.Info);

        EditorGUILayout.Space();

        _dryRun = EditorGUILayout.Toggle("Dry Run (preview only)", _dryRun);
        _deleteRigidMats = EditorGUILayout.Toggle("Delete mmd_tools_rigid_* materials", _deleteRigidMats);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Global Parameters", EditorStyles.boldLabel);
        _shadeFeather = EditorGUILayout.Slider("1st Shade Feather", _shadeFeather, 0.01f, 0.2f);
        _baseShadeFeather = EditorGUILayout.Slider("Base Shade Feather", _baseShadeFeather, 0.01f, 0.2f);
        _secondShadeStep = EditorGUILayout.Slider("2nd Shade Step", _secondShadeStep, 0f, 0.5f);
        _secondShadeFeather = EditorGUILayout.Slider("2nd Shade Feather", _secondShadeFeather, 0.01f, 0.3f);
        _outlineWidth = EditorGUILayout.Slider("Outline Width", _outlineWidth, 0f, 1f);
        _outlineColor = EditorGUILayout.ColorField("Outline Color", _outlineColor);
        _giIntensity = EditorGUILayout.Slider("GI Intensity", _giIntensity, 0f, 1f);

        EditorGUILayout.Space();

        if (GUILayout.Button(_dryRun ? "Preview Changes" : "Apply Preset", GUILayout.Height(30)))
        {
            ApplyPresets();
        }

        EditorGUILayout.Space();

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
        EditorGUILayout.TextArea(_logOutput, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void ApplyPresets()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_dryRun ? "=== DRY RUN PREVIEW ===" : "=== APPLYING PRESETS ===");
        sb.AppendLine($"Source: {CharacterMaterialsPath}\n");

        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { CharacterMaterialsPath });

        int tuned = 0, deleted = 0, skippedSpecial = 0;
        var rigidMatsToDelete = new List<string>();

        foreach (string guid in matGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            // Handle rigid body materials
            if (mat.name.StartsWith("mmd_tools_rigid"))
            {
                rigidMatsToDelete.Add(path);
                sb.AppendLine($"  [DELETE] {mat.name}");
                continue;
            }

            CharacterCategory category = DetectCategory(mat.name);
            string catLabel = category.ToString().ToUpper();

            sb.AppendLine($"  [{catLabel}] {mat.name}");

            if (!_dryRun)
            {
                ApplyGlobalSettings(mat);
                ApplyCategorySettings(mat, category);
                EditorUtility.SetDirty(mat);
            }

            if (category == CharacterCategory.Special)
                skippedSpecial++;
            else
                tuned++;
        }

        // Delete rigid body materials
        if (_deleteRigidMats && rigidMatsToDelete.Count > 0)
        {
            if (!_dryRun)
            {
                foreach (string path in rigidMatsToDelete)
                {
                    AssetDatabase.DeleteAsset(path);
                    deleted++;
                }
            }
            else
            {
                deleted = rigidMatsToDelete.Count;
            }
        }

        if (!_dryRun)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        sb.AppendLine($"\n--- Summary ---");
        sb.AppendLine($"Tuned: {tuned}");
        sb.AppendLine($"Special (minimal changes): {skippedSpecial}");
        sb.AppendLine($"Rigid materials {(_dryRun ? "to delete" : "deleted")}: {deleted}");

        if (_dryRun)
            sb.AppendLine("\n[DRY RUN] No changes made.");
        else
            sb.AppendLine("\n✓ Presets applied!");

        _logOutput = sb.ToString();
        Repaint();
    }

    private void ApplyGlobalSettings(Material mat)
    {
        CharacterCategory category = DetectCategory(mat.name);

        // Skip most settings for special materials
        if (category == CharacterCategory.Special)
            return;

        // Softened feathering (the KEY MiSide change)
        SetFloatIfExists(mat, "_1st_ShadeColor_Feather", _shadeFeather);
        SetFloatIfExists(mat, "_BaseShade_Feather", _baseShadeFeather);
        SetFloatIfExists(mat, "_2nd_ShadeColor_Step", _secondShadeStep);
        SetFloatIfExists(mat, "_2nd_ShadeColor_Feather", _secondShadeFeather);

        // Enable outlines (inverted hull)
        SetFloatIfExists(mat, "_OUTLINE", 1f);
        SetFloatIfExists(mat, "_Outline_Width", _outlineWidth);
        SetColorIfExists(mat, "_Outline_Color", _outlineColor);
        SetFloatIfExists(mat, "_Is_LightColor_Outline", 1f);
        SetFloatIfExists(mat, "_Is_BlendBaseColor", 1f);
        EnableKeyword(mat, "_OUTLINE_NML");

        // GI and shadow tweaks
        SetFloatIfExists(mat, "_GI_Intensity", _giIntensity);
        SetFloatIfExists(mat, "_Tweak_SystemShadowsLevel", 0.1f);

        // Keep matte (no specular)
        SetFloatIfExists(mat, "_HighColor_Power", 0f);
    }

    private void ApplyCategorySettings(Material mat, CharacterCategory category)
    {
        switch (category)
        {
            case CharacterCategory.Skin:
                SetColorIfExists(mat, "_1st_ShadeColor", new Color(0.90f, 0.80f, 0.77f, 1f));
                SetColorIfExists(mat, "_2nd_ShadeColor", new Color(0.80f, 0.68f, 0.65f, 1f));
                SetFloatIfExists(mat, "_RimLight", 1f);
                SetColorIfExists(mat, "_RimLightColor", new Color(1.0f, 0.85f, 0.80f, 1f));
                SetFloatIfExists(mat, "_RimLight_Power", 6f);
                SetFloatIfExists(mat, "_RimLight_InsideMask", 0.15f);
                break;

            case CharacterCategory.Hair:
                SetFloatIfExists(mat, "_1st_ShadeColor_Feather", 0.06f);
                SetFloatIfExists(mat, "_RimLight", 1f);
                SetFloatIfExists(mat, "_RimLight_Power", 8f);
                SetFloatIfExists(mat, "_RimLight_InsideMask", 0.12f);
                SetFloatIfExists(mat, "_AngelRing", 0f); // off — not MiSide style
                break;

            case CharacterCategory.Clothing:
                SetFloatIfExists(mat, "_1st_ShadeColor_Feather", 0.06f);
                SetFloatIfExists(mat, "_1st_ShadeColor_Step", 0.48f);
                SetFloatIfExists(mat, "_RimLight", 1f);
                SetFloatIfExists(mat, "_RimLight_Power", 8f);
                SetFloatIfExists(mat, "_RimLight_InsideMask", 0.1f);
                break;

            case CharacterCategory.Accessory:
                SetFloatIfExists(mat, "_RimLight", 0f);
                break;

            case CharacterCategory.Eyes:
                // Minimal shading on eyes — keep bright and expressive
                SetFloatIfExists(mat, "_1st_ShadeColor_Step", 0.8f);
                SetFloatIfExists(mat, "_1st_ShadeColor_Feather", 0.15f);
                SetFloatIfExists(mat, "_OUTLINE", 0f); // No outline on eyes
                SetFloatIfExists(mat, "_Outline_Width", 0f);
                SetFloatIfExists(mat, "_RimLight", 0f);
                break;

            case CharacterCategory.Special:
                // 頬染め (blush), 青褪め (paleness), 眉毛まつ毛 (eyebrows/lashes)
                // Leave mostly unchanged but ensure outline is off
                SetFloatIfExists(mat, "_OUTLINE", 0f);
                SetFloatIfExists(mat, "_Outline_Width", 0f);
                break;
        }
    }

    // --------- Category detection ---------

    private enum CharacterCategory
    {
        Skin,
        Hair,
        Clothing,
        Accessory,
        Eyes,
        Special
    }

    private static CharacterCategory DetectCategory(string name)
    {
        // Skin
        if (name == "体" || name == "頭")
            return CharacterCategory.Skin;

        // Hair
        if (name == "髪" || name == "髪影" || name == "髪飾り")
            return CharacterCategory.Hair;

        // Eyes
        if (name == "目")
            return CharacterCategory.Eyes;

        // Special (blush, paleness, eyebrows — leave mostly unchanged)
        if (name == "頬染め" || name == "青褪め" || name == "眉毛まつ毛")
            return CharacterCategory.Special;

        // Accessories
        if (name == "メガネ" || name == "オプション" || name == "ブローチ" || name == "腕輪")
            return CharacterCategory.Accessory;

        // Everything else is clothing
        // 服, スカート, 左/右スカート, 左/右スカート裏, ブーツ, タイ, リボン, ジッパー
        return CharacterCategory.Clothing;
    }

    // --------- Utility ---------

    private static void SetFloatIfExists(Material mat, string property, float value)
    {
        if (mat.HasProperty(property))
            mat.SetFloat(property, value);
    }

    private static void SetColorIfExists(Material mat, string property, Color value)
    {
        if (mat.HasProperty(property))
            mat.SetColor(property, value);
    }

    private static void EnableKeyword(Material mat, string keyword)
    {
        mat.EnableKeyword(keyword);
    }
}
