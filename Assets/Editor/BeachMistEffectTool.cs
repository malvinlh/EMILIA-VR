using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool to auto-create a volumetric mist effect prefab for the beach scene.
/// Generates a shader-based, localized mist volume with configurable properties.
/// 
/// Menu: Tools > Beach Mist > Create Mist Effect Prefab
/// </summary>
public static class BeachMistEffectTool
{
    private const string SHADER_PATH = "Custom/VolumetricMist";
    private const string MATERIAL_FOLDER = "Assets/Resources/Materials";
    private const string MATERIAL_NAME = "Mist_Default";
    private const string MATERIAL_PATH = MATERIAL_FOLDER + "/" + MATERIAL_NAME + ".mat";
    private const string PREFAB_FOLDER = "Assets/Prefabs";
    private const string PREFAB_NAME = "Mist_BeachVolumetric";
    private const string PREFAB_PATH = PREFAB_FOLDER + "/" + PREFAB_NAME + ".prefab";

    [MenuItem("Tools/Beach Mist/Create Mist Effect Prefab")]
    public static void CreateMistEffectPrefab()
    {
        // ── Step 1: Ensure material folder exists ──
        if (!AssetDatabase.IsValidFolder(MATERIAL_FOLDER))
        {
            AssetDatabase.CreateFolder("Assets/Resources", "Materials");
            Debug.Log("[Beach Mist] Created Materials folder");
        }

        // ── Step 2: Create or get material ──
        Material mistMaterial = AssetDatabase.LoadAssetAtPath<Material>(MATERIAL_PATH);
        if (mistMaterial == null)
        {
            Shader volumetricMistShader = Shader.Find(SHADER_PATH);
            if (volumetricMistShader == null)
            {
                Debug.LogError("[Beach Mist] Could not find shader '" + SHADER_PATH + "'. Make sure VolumetricMistURP.shader exists.");
                return;
            }

            mistMaterial = new Material(volumetricMistShader);
            mistMaterial.name = MATERIAL_NAME;

            // Set default properties for thick, prominent mist
            mistMaterial.SetColor("_MistColor", new Color(0.7f, 0.7f, 0.75f, 0.7f));
            mistMaterial.SetFloat("_Density", 1.0f);
            mistMaterial.SetFloat("_AnimationSpeed", 1.2f);
            mistMaterial.SetFloat("_NoiseScale", 2.5f);
            mistMaterial.SetFloat("_FadeDistance", 2.5f);
            mistMaterial.SetFloat("_FlowIntensity", 0.8f);
            mistMaterial.SetFloat("_Turbulence", 1.0f);
            mistMaterial.SetFloat("_SwirlyAmount", 0.4f);

            AssetDatabase.CreateAsset(mistMaterial, MATERIAL_PATH);
            Debug.Log("[Beach Mist] Created material: " + MATERIAL_PATH);
        }
        else
        {
            Debug.Log("[Beach Mist] Material already exists: " + MATERIAL_PATH);
        }

        // ── Step 3: Create root GameObject (empty) ──
        GameObject rootObject = new GameObject("Mist_Volume");
        rootObject.transform.position = Vector3.zero;
        rootObject.transform.rotation = Quaternion.identity;
        rootObject.transform.localScale = Vector3.one;

        // ── Step 4: Create sphere child with mesh ──
        GameObject sphereObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereObject.name = "MistVolume";

        // Remove default collider from primitive
        Collider primitiveCollider = sphereObject.GetComponent<Collider>();
        if (primitiveCollider != null)
        {
            Object.DestroyImmediate(primitiveCollider);
        }

        // Parent sphere to root
        sphereObject.transform.SetParent(rootObject.transform, false);
        sphereObject.transform.localPosition = Vector3.zero;
        sphereObject.transform.localRotation = Quaternion.identity;
        sphereObject.transform.localScale = Vector3.one;

        // ── Step 5: Apply material to renderer ──
        MeshRenderer renderer = sphereObject.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.material = mistMaterial;
        }

        // ── Step 6: Add SphereCollider as trigger to root ──
        SphereCollider sphereCollider = rootObject.AddComponent<SphereCollider>();
        sphereCollider.isTrigger = true;
        sphereCollider.radius = 0.5f;
        sphereCollider.center = Vector3.zero;

        // ── Step 6.5: Add ParticleSystem for black particles ──
        ParticleSystem particleSystem = rootObject.AddComponent<ParticleSystem>();
        ParticleSystemRenderer psRenderer = rootObject.GetComponent<ParticleSystemRenderer>();

        // Configure emission
        var emission = particleSystem.emission;
        emission.rateOverTime = 50f;
        emission.enabled = true;

        // Configure main module
        var main = particleSystem.main;
        main.duration = 10f;
        main.loop = true;
        main.prewarm = true;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = 4f;
        main.startSpeed = 0.02f;
        main.startSize = 0.02f;
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.05f, 0.05f, 0.05f, 0.75f));
        main.gravityModifier = 0f;

        // Configure shape (emit from sphere volume)
        var shape = particleSystem.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.3f;

        // Configure velocity over lifetime
        var velocityOverLifetime = particleSystem.velocityOverLifetime;
        velocityOverLifetime.enabled = true;
        velocityOverLifetime.space = ParticleSystemSimulationSpace.Local;
        velocityOverLifetime.x = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);
        velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(0.0f, 0.02f);
        velocityOverLifetime.z = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);

        // Keep particles drifting gently instead of escaping the volume
        var limitVelocityOverLifetime = particleSystem.limitVelocityOverLifetime;
        limitVelocityOverLifetime.enabled = true;
        limitVelocityOverLifetime.limit = 0.03f;
        limitVelocityOverLifetime.dampen = 0.85f;

        // Configure size over lifetime (fade out)
        var sizeOverLifetime = particleSystem.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        AnimationCurve sizeCurve = AnimationCurve.EaseInOut(0, 1, 1, 0);
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // Configure alpha over lifetime (fade out)
        var alphaOverLifetime = particleSystem.colorOverLifetime;
        alphaOverLifetime.enabled = true;
        Gradient fadeGradient = new Gradient();
        fadeGradient.SetKeys(
            new GradientColorKey[] { new GradientColorKey(new Color(0.1f, 0.1f, 0.1f), 0f), new GradientColorKey(new Color(0.1f, 0.1f, 0.1f), 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(0.6f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        alphaOverLifetime.color = new ParticleSystem.MinMaxGradient(fadeGradient);

        // Configure renderer - use mesh mode with sphere
        psRenderer.renderMode = ParticleSystemRenderMode.Mesh;
        Mesh sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        psRenderer.SetMeshes(new[] { sphereMesh }, 1);
        psRenderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
        psRenderer.material.SetColor("_Color", new Color(0.05f, 0.05f, 0.05f, 0.8f));

        // ── Step 7: Set default scale ──
        rootObject.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);

        // ── Step 8: Ensure prefab folder exists ──
        if (!AssetDatabase.IsValidFolder(PREFAB_FOLDER))
        {
            AssetDatabase.CreateFolder("Assets", "Prefabs");
            Debug.Log("[Beach Mist] Created Prefabs folder");
        }

        // ── Step 9: Save as prefab ──
        // Remove any existing prefab first
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PREFAB_PATH) != null)
        {
            AssetDatabase.DeleteAsset(PREFAB_PATH);
        }

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(rootObject, PREFAB_PATH);
        Debug.Log("[Beach Mist] Created prefab: " + PREFAB_PATH);

        // ── Step 10: Clean up temporary scene object ──
        Object.DestroyImmediate(rootObject);

        // ── Step 11: Refresh and notify ──
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Beach Mist Effect Created",
            "Mist effect prefab created successfully!\n\n" +
            "Location: " + PREFAB_PATH + "\n\n" +
            "You can now:\n" +
            "• Drag the prefab into your scene\n" +
            "• Adjust scale and position in the inspector\n" +
            "• Modify volumetric mist properties (Density, Animation Speed, Flow, Turbulence)\n" +
            "• Adjust particle emission and behavior via ParticleSystem component\n\n" +
            "Features:\n" +
            "✓ Spherical animated volumetric fog with swirling motion\n" +
            "✓ Small moving black sphere particles for atmosphere\n" +
            "✓ Default scale: 5×5×5 (adjust as needed)",
            "OK"
        );
    }
}
