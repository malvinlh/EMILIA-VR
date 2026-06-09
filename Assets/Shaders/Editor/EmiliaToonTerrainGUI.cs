using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom ShaderGUI for EMILIA/ToonTerrain.
/// Shows toon-specific properties in foldout groups. Terrain layers
/// (splat textures, normals) are configured from the Terrain Inspector.
/// </summary>
public class EmiliaToonTerrainGUI : ShaderGUI
{
    private bool _showToon = true;
    private bool _showNormals = true;
    private bool _showRim = true;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        EditorGUILayout.HelpBox(
            "Terrain layers (textures, normals, tiling) are configured from the Terrain component Inspector.\n" +
            "This panel controls toon shading properties only.",
            MessageType.Info);

        EditorGUILayout.Space(4);

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

        // ----- Normal Maps -----
        MaterialProperty normalToggle = FindProperty("_NormalMapToggle", properties);
        _showNormals = EditorGUILayout.Foldout(_showNormals, "Normal Maps", true, EditorStyles.foldoutHeader);
        if (_showNormals)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(normalToggle, "Enable Layer Normals");
            if (normalToggle.floatValue > 0.5f)
            {
                EditorGUILayout.HelpBox(
                    "Normal maps are read from each Terrain Layer asset.\n" +
                    "Adjust per-layer normal scale in the Terrain Layer Inspector.",
                    MessageType.None);
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
        materialEditor.RenderQueueField();
    }
}
