using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Custom ShaderGUI for the MiSide/Environment toon shader.
/// Shows/hides property groups based on feature toggles and auto-manages render queue.
/// </summary>
public class MiSideShaderGUI : ShaderGUI
{
    private bool _showToon = true;
    private bool _showNormalMap = true;
    private bool _showRim = true;
    private bool _showEmission = true;
    private bool _showGlass = true;
    private bool _showCutout = true;
    private bool _showRendering = true;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        Material material = materialEditor.target as Material;

        // ----- Base -----
        EditorGUILayout.LabelField("Base", EditorStyles.boldLabel);
        MaterialProperty baseMap   = FindProperty("_BaseMap", properties);
        MaterialProperty baseColor = FindProperty("_BaseColor", properties);
        materialEditor.TexturePropertySingleLine(new GUIContent("Base Map"), baseMap, baseColor);

        EditorGUILayout.Space(6);

        // ----- Toon Shading -----
        _showToon = EditorGUILayout.Foldout(_showToon, "Toon Shading", true, EditorStyles.foldoutHeader);
        if (_showToon)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_ShadowColor", properties), "Shadow Color");
            materialEditor.ShaderProperty(FindProperty("_ShadowStep", properties), "Shadow Step");
            materialEditor.ShaderProperty(FindProperty("_ShadowFeather", properties), "Shadow Feather");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Normal Map -----
        MaterialProperty normalMapToggle = FindProperty("_NormalMapToggle", properties);
        _showNormalMap = EditorGUILayout.Foldout(_showNormalMap, "Normal Map", true, EditorStyles.foldoutHeader);
        if (_showNormalMap)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(normalMapToggle, "Enable Normal Map");
            if (normalMapToggle.floatValue > 0.5f)
            {
                MaterialProperty bumpMap = FindProperty("_BumpMap", properties);
                MaterialProperty bumpScale = FindProperty("_BumpScale", properties);
                materialEditor.TexturePropertySingleLine(new GUIContent("Normal Map"), bumpMap, bumpScale);
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Rim Light -----
        MaterialProperty rimToggle = FindProperty("_RimLightToggle", properties);
        _showRim = EditorGUILayout.Foldout(_showRim, "Rim Light", true, EditorStyles.foldoutHeader);
        if (_showRim)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(rimToggle, "Enable Rim Light");
            if (rimToggle.floatValue > 0.5f)
            {
                materialEditor.ShaderProperty(FindProperty("_RimColor", properties), "Rim Color");
                materialEditor.ShaderProperty(FindProperty("_RimPower", properties), "Rim Power");
                materialEditor.ShaderProperty(FindProperty("_RimIntensity", properties), "Rim Intensity");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Emission -----
        MaterialProperty emissionToggle = FindProperty("_EmissionToggle", properties);
        _showEmission = EditorGUILayout.Foldout(_showEmission, "Emission", true, EditorStyles.foldoutHeader);
        if (_showEmission)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(emissionToggle, "Enable Emission");
            if (emissionToggle.floatValue > 0.5f)
            {
                MaterialProperty emissionMap   = FindProperty("_EmissionMap", properties);
                MaterialProperty emissionColor = FindProperty("_EmissionColor", properties);
                materialEditor.TexturePropertySingleLine(new GUIContent("Emission Map"), emissionMap, emissionColor);
            }
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
                materialEditor.ShaderProperty(FindProperty("_Cutoff", properties), "Alpha Cutoff");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        // ----- Glass Reflection -----
        MaterialProperty glassToggle = FindProperty("_GlassToggle", properties);
        _showGlass = EditorGUILayout.Foldout(_showGlass, "Glass / Reflection", true, EditorStyles.foldoutHeader);
        if (_showGlass)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(glassToggle, "Enable Glass");
            if (glassToggle.floatValue > 0.5f)
            {
                materialEditor.ShaderProperty(FindProperty("_GlassColor", properties), "Glass Tint");
                materialEditor.ShaderProperty(FindProperty("_GlassReflectivity", properties), "Reflectivity");
                materialEditor.ShaderProperty(FindProperty("_GlassSmoothness", properties), "Smoothness");
                materialEditor.ShaderProperty(FindProperty("_GlassFresnelPower", properties), "Fresnel Power");
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

        // ----- Preset Buttons -----
        EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Set MiSide Defaults"))
        {
            foreach (Object target in materialEditor.targets)
            {
                Material mat = target as Material;
                ApplyMiSideDefaults(mat);
                EditorUtility.SetDirty(mat);
            }
        }

        if (GUILayout.Button("Set Emissive Preset"))
        {
            foreach (Object target in materialEditor.targets)
            {
                Material mat = target as Material;
                ApplyMiSideDefaults(mat);
                mat.SetFloat("_EmissionToggle", 1f);
                mat.EnableKeyword("_EMISSION");
                EditorUtility.SetDirty(mat);
            }
        }

        if (GUILayout.Button("Set Glass Preset"))
        {
            foreach (Object target in materialEditor.targets)
            {
                Material mat = target as Material;
                ApplyMiSideDefaults(mat);
                mat.SetFloat("_GlassToggle", 1f);
                mat.EnableKeyword("_GLASS");
                mat.SetColor("_GlassColor", new Color(0.85f, 0.92f, 1.0f, 0.15f));
                mat.SetFloat("_GlassReflectivity", 0.4f);
                mat.SetFloat("_GlassSmoothness", 0.85f);
                mat.SetFloat("_GlassFresnelPower", 3.0f);
                EditorUtility.SetDirty(mat);
            }
        }

        if (GUILayout.Button("Set Cutout Preset"))
        {
            foreach (Object target in materialEditor.targets)
            {
                Material mat = target as Material;
                ApplyMiSideDefaults(mat);
                mat.SetFloat("_AlphaClip", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", (float)CullMode.Off);
                mat.renderQueue = 2450;
                EditorUtility.SetDirty(mat);
            }
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Auto-set render queue based on cutout
        foreach (Object target in materialEditor.targets)
        {
            Material mat = target as Material;
            UpdateKeywordsAndQueue(mat);
        }
    }

    private static void ApplyMiSideDefaults(Material mat)
    {
        mat.SetColor("_ShadowColor", new Color(0.85f, 0.75f, 0.72f, 1f));
        mat.SetFloat("_ShadowStep", 0.5f);
        mat.SetFloat("_ShadowFeather", 0.05f);
        mat.SetFloat("_NormalMapToggle", 0f);
        mat.DisableKeyword("_NORMALMAP");
        mat.SetFloat("_BumpScale", 1f);
        mat.SetFloat("_RimLightToggle", 0f);
        mat.DisableKeyword("_RIMLIGHT");
        mat.SetColor("_RimColor", new Color(1f, 0.9f, 0.85f, 1f));
        mat.SetFloat("_RimPower", 4f);
        mat.SetFloat("_RimIntensity", 0.15f);
        mat.SetFloat("_EmissionToggle", 0f);
        mat.DisableKeyword("_EMISSION");
        mat.SetFloat("_GlassToggle", 0f);
        mat.DisableKeyword("_GLASS");
        mat.SetFloat("_AlphaClip", 0f);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.SetFloat("_Cull", (float)CullMode.Back);
        mat.renderQueue = 2000;
        // Reset blend to opaque
        mat.SetFloat("_SrcBlend", (float)BlendMode.One);
        mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
        mat.SetFloat("_ZWrite", 1f);
    }

    private static void UpdateKeywordsAndQueue(Material mat)
    {
        // Sync keywords with toggle values
        SetKeyword(mat, "_RIMLIGHT", mat.GetFloat("_RimLightToggle") > 0.5f);
        SetKeyword(mat, "_EMISSION", mat.GetFloat("_EmissionToggle") > 0.5f);
        SetKeyword(mat, "_ALPHATEST_ON", mat.GetFloat("_AlphaClip") > 0.5f);
        SetKeyword(mat, "_NORMALMAP", mat.GetFloat("_NormalMapToggle") > 0.5f);

        bool glass = mat.GetFloat("_GlassToggle") > 0.5f;
        SetKeyword(mat, "_GLASS", glass);

        // Always keep opaque blend — glass uses reflection probe + Fresnel on RGB only.
        // Transparent glass leaks the passthrough compositor on Quest 3 (alpha < 1 = see-through).
        mat.SetFloat("_SrcBlend", (float)BlendMode.One);
        mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
        mat.SetFloat("_ZWrite", 1f);
        mat.SetOverrideTag("RenderType", "");

        // Auto render queue (glass takes priority over cutout)
        if (!glass)
        {
            if (mat.GetFloat("_AlphaClip") > 0.5f)
            {
                if (mat.renderQueue < 2450)
                    mat.renderQueue = 2450;
            }
            else
            {
                if (mat.renderQueue == 2450 || mat.renderQueue >= 3000)
                    mat.renderQueue = 2000;
            }
        }
    }

    private static void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled)
            mat.EnableKeyword(keyword);
        else
            mat.DisableKeyword(keyword);
    }
}
