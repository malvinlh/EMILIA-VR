using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Custom ShaderGUI for the MiSide/Character toon shader.
/// Provides foldout sections for each feature group and category-based presets.
/// </summary>
public class MiSideCharacterShaderGUI : ShaderGUI
{
    private bool _showBase = true;
    private bool _show1stShade = true;
    private bool _show2ndShade = true;
    private bool _showRim = true;
    private bool _showOutline = true;
    private bool _showLighting = true;
    private bool _showCutout = true;
    private bool _showRendering = true;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        Material material = materialEditor.target as Material;

        // ----- Base -----
        _showBase = EditorGUILayout.Foldout(_showBase, "Base", true, EditorStyles.foldoutHeader);
        if (_showBase)
        {
            EditorGUI.indentLevel++;
            MaterialProperty mainTex   = FindProperty("_MainTex", properties);
            MaterialProperty baseColor = FindProperty("_BaseColor", properties);
            materialEditor.TexturePropertySingleLine(new GUIContent("Base Map"), mainTex, baseColor);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- 1st Shade -----
        _show1stShade = EditorGUILayout.Foldout(_show1stShade, "1st Shade", true, EditorStyles.foldoutHeader);
        if (_show1stShade)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_1st_ShadeColor", properties), "Color");
            materialEditor.ShaderProperty(FindProperty("_1st_ShadeColor_Step", properties), "Step");
            materialEditor.ShaderProperty(FindProperty("_1st_ShadeColor_Feather", properties), "Feather");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- 2nd Shade -----
        _show2ndShade = EditorGUILayout.Foldout(_show2ndShade, "2nd Shade", true, EditorStyles.foldoutHeader);
        if (_show2ndShade)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_2nd_ShadeColor", properties), "Color");
            materialEditor.ShaderProperty(FindProperty("_2nd_ShadeColor_Step", properties), "Step");
            materialEditor.ShaderProperty(FindProperty("_2nd_ShadeColor_Feather", properties), "Feather");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Rim Light -----
        MaterialProperty rimToggle = FindProperty("_RimLight", properties);
        _showRim = EditorGUILayout.Foldout(_showRim, "Rim Light", true, EditorStyles.foldoutHeader);
        if (_showRim)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(rimToggle, "Enable Rim Light");
            if (rimToggle.floatValue > 0.5f)
            {
                materialEditor.ShaderProperty(FindProperty("_RimLightColor", properties), "Color");
                materialEditor.ShaderProperty(FindProperty("_RimLight_Power", properties), "Power");
                materialEditor.ShaderProperty(FindProperty("_RimLight_InsideMask", properties), "Inside Mask");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Outline -----
        MaterialProperty outlineToggle = FindProperty("_OUTLINE", properties);
        _showOutline = EditorGUILayout.Foldout(_showOutline, "Outline", true, EditorStyles.foldoutHeader);
        if (_showOutline)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(outlineToggle, "Enable Outline");
            if (outlineToggle.floatValue > 0.5f)
            {
                materialEditor.ShaderProperty(FindProperty("_Outline_Width", properties), "Width");
                materialEditor.ShaderProperty(FindProperty("_Outline_Color", properties), "Color");
                materialEditor.ShaderProperty(FindProperty("_Is_BlendBaseColor", properties), "Blend Base Color");
                materialEditor.ShaderProperty(FindProperty("_Is_LightColor_Outline", properties), "Light Color Outline");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Lighting -----
        _showLighting = EditorGUILayout.Foldout(_showLighting, "Lighting", true, EditorStyles.foldoutHeader);
        if (_showLighting)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_GI_Intensity", properties), "GI Intensity");
            materialEditor.ShaderProperty(FindProperty("_Tweak_SystemShadowsLevel", properties), "Shadow Level Tweak");
            materialEditor.ShaderProperty(FindProperty("_HighColor_Power", properties), "Specular Power (0=off)");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Alpha Cutout -----
        MaterialProperty alphaClip = FindProperty("_AlphaClip", properties);
        _showCutout = EditorGUILayout.Foldout(_showCutout, "Alpha Cutout", true, EditorStyles.foldoutHeader);
        if (_showCutout)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(alphaClip, "Alpha Clip");
            if (alphaClip.floatValue > 0.5f)
            {
                materialEditor.ShaderProperty(FindProperty("_Cutoff", properties), "Cutoff");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Rendering -----
        _showRendering = EditorGUILayout.Foldout(_showRendering, "Rendering", true, EditorStyles.foldoutHeader);
        if (_showRendering)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_Cull", properties), "Cull Mode");
            materialEditor.RenderQueueField();
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);

        // ----- Category Presets -----
        EditorGUILayout.LabelField("Category Presets", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Skin"))
        {
            foreach (Object target in materialEditor.targets)
                ApplyPreset(target as Material, Category.Skin);
        }
        if (GUILayout.Button("Hair"))
        {
            foreach (Object target in materialEditor.targets)
                ApplyPreset(target as Material, Category.Hair);
        }
        if (GUILayout.Button("Eyes"))
        {
            foreach (Object target in materialEditor.targets)
                ApplyPreset(target as Material, Category.Eyes);
        }
        if (GUILayout.Button("Clothing"))
        {
            foreach (Object target in materialEditor.targets)
                ApplyPreset(target as Material, Category.Clothing);
        }
        if (GUILayout.Button("Special"))
        {
            foreach (Object target in materialEditor.targets)
                ApplyPreset(target as Material, Category.Special);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Auto-sync keywords
        foreach (Object target in materialEditor.targets)
        {
            Material mat = target as Material;
            UpdateKeywords(mat);
        }
    }

    private enum Category { Skin, Hair, Eyes, Clothing, Special }

    private static void ApplyPreset(Material mat, Category category)
    {
        // Common base settings
        mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
        mat.SetFloat("_2nd_ShadeColor_Step", 0.15f);
        mat.SetFloat("_2nd_ShadeColor_Feather", 0.1f);
        mat.SetFloat("_GI_Intensity", 0.3f);
        mat.SetFloat("_Tweak_SystemShadowsLevel", 0.1f);
        mat.SetFloat("_HighColor_Power", 0f);

        switch (category)
        {
            case Category.Skin:
                mat.SetColor("_1st_ShadeColor", new Color(0.90f, 0.80f, 0.77f, 1f));
                mat.SetColor("_2nd_ShadeColor", new Color(0.80f, 0.68f, 0.65f, 1f));
                mat.SetFloat("_RimLight", 1f);
                mat.SetColor("_RimLightColor", new Color(1f, 0.85f, 0.80f, 1f));
                mat.SetFloat("_RimLight_Power", 6f);
                mat.SetFloat("_RimLight_InsideMask", 0.15f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.2f, 0.15f, 0.15f, 1f));
                break;

            case Category.Hair:
                mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
                mat.SetFloat("_RimLight", 1f);
                mat.SetFloat("_RimLight_Power", 8f);
                mat.SetFloat("_RimLight_InsideMask", 0.12f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.2f, 0.15f, 0.15f, 1f));
                break;

            case Category.Eyes:
                mat.SetFloat("_1st_ShadeColor_Step", 0.8f);
                mat.SetFloat("_1st_ShadeColor_Feather", 0.15f);
                mat.SetFloat("_RimLight", 0f);
                mat.SetFloat("_OUTLINE", 0f);
                mat.SetFloat("_Outline_Width", 0f);
                break;

            case Category.Clothing:
                mat.SetFloat("_1st_ShadeColor_Feather", 0.06f);
                mat.SetFloat("_1st_ShadeColor_Step", 0.48f);
                mat.SetFloat("_RimLight", 1f);
                mat.SetFloat("_RimLight_Power", 8f);
                mat.SetFloat("_RimLight_InsideMask", 0.1f);
                mat.SetFloat("_OUTLINE", 1f);
                mat.SetFloat("_Outline_Width", 0.3f);
                mat.SetColor("_Outline_Color", new Color(0.2f, 0.15f, 0.15f, 1f));
                break;

            case Category.Special:
                mat.SetFloat("_RimLight", 0f);
                mat.SetFloat("_OUTLINE", 0f);
                mat.SetFloat("_Outline_Width", 0f);
                break;
        }

        UpdateKeywords(mat);
        EditorUtility.SetDirty(mat);
    }

    private static void UpdateKeywords(Material mat)
    {
        SetKeyword(mat, "_RIMLIGHT_ON", mat.GetFloat("_RimLight") > 0.5f);
        SetKeyword(mat, "_OUTLINE_ON", mat.GetFloat("_OUTLINE") > 0.5f);
        SetKeyword(mat, "_ALPHATEST_ON", mat.GetFloat("_AlphaClip") > 0.5f);
    }

    private static void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled)
            mat.EnableKeyword(keyword);
        else
            mat.DisableKeyword(keyword);
    }
}
