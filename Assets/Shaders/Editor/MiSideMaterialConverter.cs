using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch converts environment materials to MiSide toon shaders.
/// For the Beach Diorama target, every material gets individually-tuned
/// anime-style presets (shadow color, rim light, cutout, emission, etc.).
/// </summary>
public class MiSideMaterialConverter : EditorWindow
{
    // =====================================================================
    //  PRESET DATA
    // =====================================================================

    /// <summary>Holds every parameter needed to fully configure one material.</summary>
    private struct ToonPreset
    {
        public string category;        // for logging
        public bool   useWaterShader;  // true → MiSide/ToonWater

        // Shadow
        public Color  shadowColor;
        public float  shadowStep;
        public float  shadowFeather;
        public float  shadowIntensity;

        // Rim
        public bool   rimEnabled;
        public Color  rimColor;
        public float  rimPower;
        public float  rimIntensity;

        // Emission
        public bool   emissionEnabled;
        public Color  emissionColor;   // only used when emissionEnabled

        // Cutout
        public bool   alphaClip;
        public float  cutoff;
        public CullMode cullMode;

        // Render queue
        public int    renderQueue;

        // ---------- factory helpers ----------

        public static ToonPreset Sand() => new ToonPreset
        {
            category       = "SAND",
            shadowColor    = new Color(0.92f, 0.82f, 0.65f, 1f),
            shadowStep     = 0.45f,
            shadowFeather  = 0.12f,
            shadowIntensity= 0.40f,
            rimEnabled     = false,
            rimColor       = Color.white,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Vegetation() => new ToonPreset
        {
            category       = "VEGETATION",
            shadowColor    = new Color(0.70f, 0.82f, 0.60f, 1f),
            shadowStep     = 0.45f,
            shadowFeather  = 0.08f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(0.85f, 1f, 0.80f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.20f,
            alphaClip      = true,
            cutoff         = 0.5f,
            cullMode       = CullMode.Off,     // double-sided leaves
            renderQueue    = 2450
        };

        public static ToonPreset Wood() => new ToonPreset
        {
            category       = "WOOD",
            shadowColor    = new Color(0.82f, 0.72f, 0.60f, 1f),
            shadowStep     = 0.50f,
            shadowFeather  = 0.05f,
            shadowIntensity= 0.55f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.95f, 0.85f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.12f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Building() => new ToonPreset
        {
            category       = "BUILDING",
            shadowColor    = new Color(0.85f, 0.75f, 0.68f, 1f),
            shadowStep     = 0.50f,
            shadowFeather  = 0.04f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.94f, 0.88f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.10f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Fabric() => new ToonPreset
        {
            category       = "FABRIC",
            shadowColor    = new Color(0.88f, 0.78f, 0.75f, 1f),
            shadowStep     = 0.45f,
            shadowFeather  = 0.08f,
            shadowIntensity= 0.45f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.95f, 0.90f, 1f),
            rimPower       = 4f,
            rimIntensity   = 0.15f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Rock() => new ToonPreset
        {
            category       = "ROCK",
            shadowColor    = new Color(0.80f, 0.72f, 0.62f, 1f),
            shadowStep     = 0.52f,
            shadowFeather  = 0.04f,
            shadowIntensity= 0.60f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.92f, 0.82f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.10f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Prop() => new ToonPreset
        {
            category       = "PROP",
            shadowColor    = new Color(0.85f, 0.78f, 0.72f, 1f),
            shadowStep     = 0.48f,
            shadowFeather  = 0.05f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.94f, 0.88f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.12f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset PropCutout() => new ToonPreset
        {
            category       = "PROP-CUTOUT",
            shadowColor    = new Color(0.85f, 0.78f, 0.72f, 1f),
            shadowStep     = 0.48f,
            shadowFeather  = 0.05f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.94f, 0.88f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.12f,
            alphaClip      = true,
            cutoff         = 0.5f,
            cullMode       = CullMode.Off,
            renderQueue    = 2450
        };

        public static ToonPreset FabricCutout() => new ToonPreset
        {
            category       = "FABRIC-CUTOUT",
            shadowColor    = new Color(0.88f, 0.78f, 0.75f, 1f),
            shadowStep     = 0.45f,
            shadowFeather  = 0.08f,
            shadowIntensity= 0.45f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.95f, 0.90f, 1f),
            rimPower       = 4f,
            rimIntensity   = 0.15f,
            alphaClip      = true,
            cutoff         = 0.5f,
            cullMode       = CullMode.Off,
            renderQueue    = 2450
        };

        public static ToonPreset BuildingCutout() => new ToonPreset
        {
            category       = "BUILDING-CUTOUT",
            shadowColor    = new Color(0.85f, 0.75f, 0.68f, 1f),
            shadowStep     = 0.50f,
            shadowFeather  = 0.04f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.94f, 0.88f, 1f),
            rimPower       = 5f,
            rimIntensity   = 0.10f,
            alphaClip      = true,
            cutoff         = 0.5f,
            cullMode       = CullMode.Off,
            renderQueue    = 2450
        };

        public static ToonPreset Emissive(Color emColor) => new ToonPreset
        {
            category        = "EMISSIVE",
            shadowColor     = new Color(0.75f, 0.70f, 0.68f, 1f),
            shadowStep      = 0.50f,
            shadowFeather   = 0.05f,
            shadowIntensity = 0.30f,
            rimEnabled      = false,
            emissionEnabled = true,
            emissionColor   = new Color(
                Mathf.Max(emColor.r, 0.5f) * 2f,
                Mathf.Max(emColor.g, 0.5f) * 2f,
                Mathf.Max(emColor.b, 0.5f) * 2f, 1f),
            cullMode        = CullMode.Back,
            renderQueue     = 2000
        };

        public static ToonPreset Water() => new ToonPreset
        {
            category       = "WATER",
            useWaterShader = true,
            renderQueue    = 3000
        };

        public static ToonPreset Animal() => new ToonPreset
        {
            category       = "ANIMAL",
            shadowColor    = new Color(0.82f, 0.76f, 0.72f, 1f),
            shadowStep     = 0.48f,
            shadowFeather  = 0.06f,
            shadowIntensity= 0.50f,
            rimEnabled     = true,
            rimColor       = new Color(1f, 0.96f, 0.90f, 1f),
            rimPower       = 4f,
            rimIntensity   = 0.18f,
            cullMode       = CullMode.Back,
            renderQueue    = 2000
        };

        public static ToonPreset Decal() => new ToonPreset
        {
            category       = "DECAL",
            shadowColor    = new Color(0.85f, 0.78f, 0.72f, 1f),
            shadowStep     = 0.48f,
            shadowFeather  = 0.05f,
            shadowIntensity= 0.35f,
            rimEnabled     = false,
            alphaClip      = true,
            cutoff         = 0.3f,       // lower cutoff to keep soft edges
            cullMode       = CullMode.Off,
            renderQueue    = 2450
        };
    }

    // -----------------------------------------------------------------
    //  Per-material name → preset mapping for the beach diorama asset.
    //  Every material is explicitly assigned.
    // -----------------------------------------------------------------
    private static Dictionary<string, ToonPreset> BuildBeachDioramaPresets()
    {
        var p = new Dictionary<string, ToonPreset>();

        // ---- Water ----
        p["M_Water"] = ToonPreset.Water();

        // ---- Sand / Ground ----
        p["M_SandTop"]          = ToonPreset.Sand();
        p["M_SandWater"]        = ToonPreset.Sand();
        p["M_SandWaterEdge"]    = ToonPreset.Sand();
        p["M_Floor"]            = ToonPreset.Sand();
        p["M_GroundPlaneBottom"]= ToonPreset.Sand();

        // ---- Vegetation (double-sided alpha cutout) ----
        p["M_PalmTreeLeaves"]   = ToonPreset.Vegetation();
        p["M_Plants"]           = ToonPreset.Vegetation();

        // ---- Wood / Structural timber ----
        p["M_PalmTree"]         = ToonPreset.Wood();
        p["M_Pier"]             = ToonPreset.Wood();
        p["M_Pier_Trim"]        = ToonPreset.Wood();
        p["M_PlanksWall"]       = ToonPreset.Wood();
        p["M_Frames"]           = ToonPreset.Wood();

        // ---- Building surfaces ----
        p["M_Walls"]            = ToonPreset.Building();
        p["M_Roof"]             = ToonPreset.Building();
        p["M_RoofUnderside"]    = ToonPreset.Building();
        p["M_DoorsWindowsDetails"] = ToonPreset.Building();
        p["M_Chalkboard"]       = ToonPreset.Building();

        // Building with cutout (cabin has opacity map for windows/trim)
        p["M_ChangingCabin"]    = ToonPreset.BuildingCutout();

        // ---- Fabric (soft items) ----
        p["M_Towel_Variant01"]  = ToonPreset.Fabric();
        p["M_Towel_Variant02"]  = ToonPreset.Fabric();
        p["M_Pillow"]           = ToonPreset.Fabric();
        p["M_OutdoorSofa"]      = ToonPreset.Fabric();
        p["M_Sunbed"]           = ToonPreset.Fabric();
        p["M_Sponge"]           = ToonPreset.Fabric();

        // Fabric with cutout (rope mesh, hammock weave, umbrella edge)
        p["M_BeachUmbrella"]    = ToonPreset.FabricCutout();
        p["M_Hammock"]          = ToonPreset.FabricCutout();
        p["M_Ropes"]            = ToonPreset.FabricCutout();

        // ---- Rock ----
        p["M_Rocks"]            = ToonPreset.Rock();
        p["m_Rocks 1"]          = ToonPreset.Rock();

        // ---- Emissive lights ----
        p["M_Light_Red"]    = ToonPreset.Emissive(new Color(0.65f, 0.032f,  0.032f,  1f));
        p["M_Light_Green"]  = ToonPreset.Emissive(new Color(0.032f, 0.65f,  0.032f,  1f));
        p["M_Light_Blue"]   = ToonPreset.Emissive(new Color(0.032f, 0.032f, 0.65f,   1f));

        // ---- Animals ----
        p["M_Fish"]             = ToonPreset.Animal();
        p["M_Seagull"]          = ToonPreset.Animal();

        // ---- Props (opaque) ----
        p["M_Coolbox"]          = ToonPreset.Prop();
        p["M_Rims"]             = ToonPreset.Prop();
        p["M_Cable"]            = ToonPreset.Prop();

        // ---- Props with cutout (have opacity maps) ----
        p["M_Mountainbike"]         = ToonPreset.PropCutout();
        p["M_MountainBike_Variant02"] = ToonPreset.PropCutout();
        p["M_SeaScooter"]          = ToonPreset.PropCutout();
        p["M_Torch"]               = ToonPreset.PropCutout();
        p["M_Fillers"]             = ToonPreset.PropCutout();

        // ---- Decals (transparent stickers) ----
        p["M_Decals"]           = ToonPreset.Decal();

        return p;
    }

    // =====================================================================
    //  EDITOR WINDOW
    // =====================================================================

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
        window.minSize = new Vector2(580, 500);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("MiSide Material Converter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Converts materials to MiSide toon shaders.\n\n" +
            "• ChatRoom / Beach — generic URP/Lit → toon conversion\n" +
            "• Beach Diorama — per-material anime presets (44 materials,\n" +
            "  individually-tuned shadow, rim, cutout & emission settings)",
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
            if (_target == ConvertTarget.BeachDiorama)
                ConvertBeachDiorama();
            else
                ConvertMaterialsGeneric();
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

    // =====================================================================
    //  BEACH DIORAMA — Preset-driven conversion (fully automated)
    // =====================================================================

    private void ConvertBeachDiorama()
    {
        string folder = GetTargetFolder();
        if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
        {
            _logOutput = $"Invalid folder: {folder}";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(_dryRun ? "=== DRY RUN PREVIEW (Beach Diorama) ===" : "=== EXECUTING CONVERSION (Beach Diorama) ===");
        sb.AppendLine($"Source: {folder}\n");

        Shader toonShader  = Shader.Find("MiSide/Environment");
        Shader waterShader = Shader.Find("MiSide/ToonWater");

        if (toonShader == null)
        {
            _logOutput = "ERROR: Shader 'MiSide/Environment' not found. Make sure it compiles.";
            return;
        }
        if (waterShader == null)
            sb.AppendLine("WARNING: 'MiSide/ToonWater' not found — water materials will fall back to MiSide/Environment.\n");

        Dictionary<string, ToonPreset> presets = BuildBeachDioramaPresets();

        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { folder });
        sb.AppendLine($"Materials found: {matGuids.Length}");
        sb.AppendLine($"Presets defined: {presets.Count}\n");

        // Category counters
        var catCount = new Dictionary<string, int>();
        int converted = 0, skipped = 0, errors = 0, unmapped = 0;

        foreach (string guid in matGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { errors++; continue; }

            // Skip if already converted
            if (mat.shader.name.StartsWith("MiSide/"))
            {
                skipped++;
                sb.AppendLine($"  [SKIP] {mat.name}  (already MiSide)");
                continue;
            }

            // Look up preset
            ToonPreset preset;
            if (!presets.TryGetValue(mat.name, out preset))
            {
                // Fallback: auto-detect as generic prop
                preset = ToonPreset.Prop();
                preset.category = "UNMAPPED→PROP";
                unmapped++;
            }

            // Count by category
            if (!catCount.ContainsKey(preset.category))
                catCount[preset.category] = 0;
            catCount[preset.category]++;

            if (_dryRun)
            {
                sb.AppendLine($"  [{preset.category}] {mat.name}");
            }
            else
            {
                ApplyBeachDioramaPreset(mat, preset, toonShader, waterShader);
                EditorUtility.SetDirty(mat);
            }

            converted++;

            if (converted % 20 == 0)
                EditorUtility.DisplayProgressBar("Converting Beach Diorama", $"{converted}/{matGuids.Length}", (float)converted / matGuids.Length);
        }

        EditorUtility.ClearProgressBar();

        if (!_dryRun)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        sb.AppendLine($"\n--- Summary ---");
        sb.AppendLine($"Converted:   {converted}");
        foreach (var kv in catCount)
            sb.AppendLine($"  {kv.Key}: {kv.Value}");
        sb.AppendLine($"Skipped:     {skipped}");
        sb.AppendLine($"Unmapped:    {unmapped}");
        sb.AppendLine($"Errors:      {errors}");

        if (_dryRun)
            sb.AppendLine("\n[DRY RUN] No changes made. Uncheck 'Dry Run' to execute.");
        else
            sb.AppendLine("\n✓ Beach Diorama conversion complete!");

        _logOutput = sb.ToString();
        Repaint();
    }

    /// <summary>Read source properties from the old shader, switch to MiSide, and apply preset.</summary>
    private static void ApplyBeachDioramaPreset(Material mat, ToonPreset preset, Shader toonShader, Shader waterShader)
    {
        // ---- Harvest existing properties before shader swap ----
        Texture baseMap = ReadTexture(mat, "_BaseMap", "_MainTex", "_BASE_COLOR_MAP");
        Color baseColor = ReadColor(mat, "_BASE_COLOR", "_BaseColor", "_Color");
        Color emColor   = ReadColor(mat, "_EMISSION_COLOR", "_EmissionColor");
        Texture emMap   = ReadTexture(mat, "_EmissionMap", "_EMISSION_COLOR_MAP");

        // ---- Water path ----
        if (preset.useWaterShader)
        {
            if (waterShader != null)
            {
                mat.shader = waterShader;
                ApplyWaterDefaults(mat);
            }
            else
            {
                // Fallback: toon shader with blue tint
                mat.shader = toonShader;
                mat.SetTexture("_BaseMap", null);
                mat.SetColor("_BaseColor", new Color(0.35f, 0.65f, 0.85f, 1f));
                ApplyToonParams(mat, ToonPreset.Sand()); // minimal shading
            }
            return;
        }

        // ---- Switch to toon shader ----
        mat.shader = toonShader;

        // Restore base texture + color
        mat.SetTexture("_BaseMap", baseMap);
        mat.SetColor("_BaseColor", baseColor);

        // ---- Apply toon parameters from preset ----
        ApplyToonParams(mat, preset);

        // ---- Emission ----
        if (preset.emissionEnabled)
        {
            mat.SetFloat("_EmissionToggle", 1f);
            mat.EnableKeyword("_EMISSION");
            mat.SetTexture("_EmissionMap", emMap);
            mat.SetColor("_EmissionColor", preset.emissionColor);
        }
        else
        {
            mat.SetFloat("_EmissionToggle", 0f);
            mat.DisableKeyword("_EMISSION");
        }

        // ---- Alpha cutout ----
        if (preset.alphaClip)
        {
            mat.SetFloat("_AlphaClip", 1f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cutoff", preset.cutoff);
        }
        else
        {
            mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
        }

        mat.SetFloat("_Cull", (float)preset.cullMode);
        mat.renderQueue = preset.renderQueue;
    }

    private static void ApplyToonParams(Material mat, ToonPreset p)
    {
        mat.SetColor("_ShadowColor", p.shadowColor);
        mat.SetFloat("_ShadowStep", p.shadowStep);
        mat.SetFloat("_ShadowFeather", p.shadowFeather);
        mat.SetFloat("_ShadowIntensity", p.shadowIntensity);

        if (p.rimEnabled)
        {
            mat.SetFloat("_RimLightToggle", 1f);
            mat.EnableKeyword("_RIMLIGHT");
            mat.SetColor("_RimColor", p.rimColor);
            mat.SetFloat("_RimPower", p.rimPower);
            mat.SetFloat("_RimIntensity", p.rimIntensity);
        }
        else
        {
            mat.SetFloat("_RimLightToggle", 0f);
            mat.DisableKeyword("_RIMLIGHT");
        }
    }

    // =====================================================================
    //  GENERIC conversion (ChatRoom, Beach, SelectedFolder)
    // =====================================================================

    private void ConvertMaterialsGeneric()
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

        Shader toonShader  = Shader.Find("MiSide/Environment");
        Shader waterShader = Shader.Find("MiSide/ToonWater");

        if (toonShader == null) { _logOutput = "ERROR: 'MiSide/Environment' not found."; return; }
        if (waterShader == null)
            sb.AppendLine("WARNING: 'MiSide/ToonWater' not found — water materials will use MiSide/Environment.\n");

        Shader urpLit       = Shader.Find("Universal Render Pipeline/Lit");
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

            if (mat.shader != urpLit && mat.shader != urpSimpleLit) { skipped++; continue; }
            if (mat.shader.name.StartsWith("MiSide/")) { skipped++; continue; }

            MaterialType type = DetectMaterialType(mat.name, mat);

            string typeLabel;
            switch (type)
            {
                case MaterialType.Emissive: typeLabel = "EMISSIVE"; emissiveCount++; break;
                case MaterialType.Cutout:   typeLabel = "CUTOUT";   cutoutCount++;   break;
                case MaterialType.Water:    typeLabel = "WATER";    waterCount++;    break;
                default:                    typeLabel = "STANDARD"; standardCount++; break;
            }

            if (_dryRun)
            {
                sb.AppendLine($"  [{typeLabel}] {mat.name}");
            }
            else
            {
                ConvertSingleMaterialGeneric(mat, type, toonShader, waterShader);
                EditorUtility.SetDirty(mat);
            }

            converted++;
            if (converted % 50 == 0)
                EditorUtility.DisplayProgressBar("Converting", $"{converted}/{matGuids.Length}", (float)converted / matGuids.Length);
        }

        EditorUtility.ClearProgressBar();

        if (!_dryRun) { AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

        sb.AppendLine($"\n--- Summary ---");
        sb.AppendLine($"Converted: {converted}  (Standard {standardCount} / Emissive {emissiveCount} / Cutout {cutoutCount} / Water {waterCount})");
        sb.AppendLine($"Skipped: {skipped}  Errors: {errors}");
        sb.AppendLine(_dryRun ? "\n[DRY RUN] No changes." : "\n✓ Conversion complete!");

        _logOutput = sb.ToString();
        Repaint();
    }

    private static void ConvertSingleMaterialGeneric(Material mat, MaterialType type, Shader toonShader, Shader waterShader)
    {
        Texture baseMap = ReadTexture(mat, "_BaseMap", "_MainTex", "_BASE_COLOR_MAP");
        Color baseColor = ReadColor(mat, "_BaseColor", "_BASE_COLOR");
        Color emColor   = ReadColor(mat, "_EmissionColor", "_EMISSION_COLOR");
        Texture emMap   = ReadTexture(mat, "_EmissionMap", "_EMISSION_COLOR_MAP");

        if (type == MaterialType.Water && waterShader != null)
        {
            mat.shader = waterShader;
            ApplyWaterDefaults(mat);
            return;
        }

        mat.shader = toonShader;
        mat.SetTexture("_BaseMap", baseMap);
        mat.SetColor("_BaseColor", baseColor);

        // Default toon look
        ToonPreset def = ToonPreset.Prop();
        ApplyToonParams(mat, def);

        switch (type)
        {
            case MaterialType.Emissive:
                mat.SetFloat("_EmissionToggle", 1f);
                mat.EnableKeyword("_EMISSION");
                mat.SetTexture("_EmissionMap", emMap);
                mat.SetColor("_EmissionColor", new Color(
                    Mathf.Max(emColor.r, 0.5f) * 1.5f,
                    Mathf.Max(emColor.g, 0.5f) * 1.5f,
                    Mathf.Max(emColor.b, 0.5f) * 1.5f, 1f));
                mat.SetFloat("_AlphaClip", 0f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", (float)CullMode.Back);
                mat.renderQueue = 2000;
                break;

            case MaterialType.Cutout:
                mat.SetFloat("_AlphaClip", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", (float)CullMode.Off);
                mat.SetFloat("_EmissionToggle", 0f);
                mat.DisableKeyword("_EMISSION");
                mat.renderQueue = 2450;
                break;

            default:
                mat.SetFloat("_EmissionToggle", 0f);
                mat.DisableKeyword("_EMISSION");
                mat.SetFloat("_AlphaClip", 0f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", (float)CullMode.Back);
                mat.renderQueue = 2000;
                break;
        }
    }

    // =====================================================================
    //  SHARED HELPERS
    // =====================================================================

    private static void ApplyWaterDefaults(Material mat)
    {
        mat.SetColor("_ShallowColor",  new Color(0.45f, 0.78f, 0.88f, 0.85f));
        mat.SetColor("_DeepColor",     new Color(0.18f, 0.38f, 0.58f, 0.85f));
        mat.SetFloat("_ColorGradientScale",  1f);
        mat.SetFloat("_ColorGradientOffset", 0f);
        mat.SetColor("_FoamColor",     new Color(1f, 1f, 1f, 0.5f));
        mat.SetFloat("_FoamSpeed",     0.08f);
        mat.SetFloat("_FoamScale",     1f);
        mat.SetFloat("_FoamIntensity", 0.3f);
        mat.SetFloat("_WaveAmplitude", 0.03f);
        mat.SetFloat("_WaveFrequency", 2f);
        mat.SetFloat("_WaveSpeed",     1.5f);
        mat.renderQueue = 3000;
    }

    /// <summary>Read the first non-null texture from a list of property names.</summary>
    private static Texture ReadTexture(Material mat, params string[] props)
    {
        foreach (string p in props)
        {
            if (mat.HasProperty(p))
            {
                Texture t = mat.GetTexture(p);
                if (t != null) return t;
            }
        }
        return null;
    }

    /// <summary>Read the first valid color from a list of property names.</summary>
    private static Color ReadColor(Material mat, params string[] props)
    {
        foreach (string p in props)
        {
            if (mat.HasProperty(p))
                return mat.GetColor(p);
        }
        return Color.white;
    }

    // =====================================================================
    //  GENERIC TYPE DETECTION  (used only by ChatRoom / Beach / SelectedFolder)
    // =====================================================================

    private enum MaterialType { Standard, Emissive, Cutout, Water }

    private static readonly string[] EmissivePatterns =
        { "Emission", "Lamp", "Monitor", "Clock", "Moon", "Street", "Light", "Glow", "Screen", "LED", "Neon" };

    private static readonly string[] CutoutPatterns =
        { "Cutout", "Cut", "_Cut", "Leaves", "Plants", "Decals", "Ropes", "Fillers", "Cable", "Hammock" };

    private static readonly string[] WaterPatterns =
        { "sea", "surf", "water", "ocean", "wave" };

    private static MaterialType DetectMaterialType(string name, Material mat = null)
    {
        string lower = name.ToLowerInvariant();

        foreach (string p in WaterPatterns)
            if (lower.Contains(p.ToLowerInvariant())) return MaterialType.Water;

        foreach (string p in EmissivePatterns)
            if (lower.Contains(p.ToLowerInvariant())) return MaterialType.Emissive;

        if (mat != null)
        {
            Color emCol = ReadColor(mat, "_EMISSION_COLOR", "_EmissionColor");
            if (emCol.r + emCol.g + emCol.b > 0.05f) return MaterialType.Emissive;
        }

        foreach (string p in CutoutPatterns)
            if (lower.Contains(p.ToLowerInvariant())) return MaterialType.Cutout;

        if (mat != null && mat.HasProperty("_OPACITY_MAP"))
        {
            Texture opTex = mat.GetTexture("_OPACITY_MAP");
            if (opTex != null) return MaterialType.Cutout;
        }

        return MaterialType.Standard;
    }
}
