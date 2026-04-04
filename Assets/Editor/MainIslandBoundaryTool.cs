using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Bakes invisible BoxCollider walls around MainIsland in the active scene.
/// Generates a segmented coastline ring and an optional outer box guard wall.
/// Menu:
///   Tools/MR Journal/Generate MainIsland Boundaries
///   Tools/MR Journal/Clear MainIsland Boundaries
/// </summary>
public static class MainIslandBoundaryTool
{
    private const string BoundaryRootName = "__BakedMainIslandBoundary";
    private const string CoastRingRootName = "CoastRing";
    private const string OuterBoxRootName = "OuterBox";

    private const float DefaultPadding = 0.35f;
    private const float DefaultWallHeight = 2.2f;
    private const float DefaultWallThickness = 0.2f;
    private const float DefaultCoastInset = 0.75f;
    private const int DefaultRingSegments = 28;
    private const bool DefaultCreateOuterBox = true;

    [MenuItem("Tools/MR Journal/Generate MainIsland Boundaries")]
    public static void GenerateMainIslandBoundaries()
    {
        var session = Object.FindAnyObjectByType<JournalSessionManager>();
        var island = ResolveMainIsland(session);
        if (island == null)
        {
            Debug.LogError("[BoundaryTool] MainIsland not found. Assign JournalSessionManager.mainIsland or create a GameObject named 'MainIsland'.");
            return;
        }

        if (!TryComputeRendererBounds(island, out var islandBounds))
        {
            Debug.LogError("[BoundaryTool] No enabled Renderer found under MainIsland. Cannot compute boundary bounds.");
            return;
        }

        float padding = session != null ? Mathf.Max(0f, session.boundaryPadding) : DefaultPadding;
        float wallHeight = session != null ? Mathf.Clamp(session.boundaryWallHeight, 0.5f, 6f) : DefaultWallHeight;
        float wallThickness = session != null ? Mathf.Clamp(session.boundaryWallThickness, 0.02f, 1f) : DefaultWallThickness;
        float coastInset = session != null ? Mathf.Max(0f, session.boundaryCoastInset) : DefaultCoastInset;
        int ringSegments = session != null ? Mathf.Clamp(session.boundaryRingSegments, 8, 128) : DefaultRingSegments;
        bool createOuterBox = session != null ? session.boundaryCreateOuterBox : DefaultCreateOuterBox;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Generate MainIsland Boundaries");

        var existingRoot = island.Find(BoundaryRootName);
        if (existingRoot != null)
            Undo.DestroyObjectImmediate(existingRoot.gameObject);

        var root = new GameObject(BoundaryRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Boundary Root");
        root.transform.SetParent(island, true);

        var coastRoot = new GameObject(CoastRingRootName);
        Undo.RegisterCreatedObjectUndo(coastRoot, "Create Coast Ring Root");
        coastRoot.transform.SetParent(root.transform, true);

        var outerBoxRoot = new GameObject(OuterBoxRootName);
        Undo.RegisterCreatedObjectUndo(outerBoxRoot, "Create Outer Box Root");
        outerBoxRoot.transform.SetParent(root.transform, true);

        float centerY = islandBounds.min.y + (wallHeight * 0.5f);
        int wallLayer = island.gameObject.layer;

        float radiusX = Mathf.Max(0.25f, islandBounds.extents.x - coastInset);
        float radiusZ = Mathf.Max(0.25f, islandBounds.extents.z - coastInset);
        CreateCoastRingWalls(coastRoot.transform, wallLayer, islandBounds.center, centerY, radiusX, radiusZ, wallHeight, wallThickness, ringSegments);

        float minX = islandBounds.min.x - padding;
        float maxX = islandBounds.max.x + padding;
        float minZ = islandBounds.min.z - padding;
        float maxZ = islandBounds.max.z + padding;

        float spanX = Mathf.Max(0.1f, maxX - minX);
        float spanZ = Mathf.Max(0.1f, maxZ - minZ);

        if (createOuterBox)
            CreateOuterBoxWalls(outerBoxRoot.transform, wallLayer, minX, maxX, minZ, maxZ, centerY, spanX, spanZ, wallHeight, wallThickness);

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"[BoundaryTool] Generated boundaries under '{BoundaryRootName}' on '{island.name}'. " +
                  $"ringRadiusX={radiusX:F2}m, ringRadiusZ={radiusZ:F2}m, ringSegments={ringSegments}, " +
                  $"outerBox={(createOuterBox ? "on" : "off")}, padding={padding:F2}m, height={wallHeight:F2}m, thickness={wallThickness:F2}m.");
    }

    [MenuItem("Tools/MR Journal/Clear MainIsland Boundaries")]
    public static void ClearMainIslandBoundaries()
    {
        var session = Object.FindAnyObjectByType<JournalSessionManager>();
        var island = ResolveMainIsland(session);
        if (island == null)
        {
            Debug.LogWarning("[BoundaryTool] MainIsland not found. Nothing to clear.");
            return;
        }

        var root = island.Find(BoundaryRootName);
        if (root == null)
        {
            Debug.Log("[BoundaryTool] No generated boundary root found to clear.");
            return;
        }

        Undo.DestroyObjectImmediate(root.gameObject);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[BoundaryTool] Cleared generated MainIsland boundaries.");
    }

    private static Transform ResolveMainIsland(JournalSessionManager session)
    {
        if (session != null && session.mainIsland != null)
            return session.mainIsland;

        var go = GameObject.Find("MainIsland");
        return go != null ? go.transform : null;
    }

    private static bool TryComputeRendererBounds(Transform root, out Bounds bounds)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        bool found = false;
        bounds = default;

        foreach (var renderer in renderers)
        {
            if (renderer == null || !renderer.enabled)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private static void CreateOuterBoxWalls(Transform parent,
                                            int layer,
                                            float minX,
                                            float maxX,
                                            float minZ,
                                            float maxZ,
                                            float centerY,
                                            float spanX,
                                            float spanZ,
                                            float wallHeight,
                                            float wallThickness)
    {
        CreateWall(parent, "North", layer,
            new Vector3((minX + maxX) * 0.5f, centerY, maxZ + (wallThickness * 0.5f)),
            new Vector3(spanX + (wallThickness * 2f), wallHeight, wallThickness));

        CreateWall(parent, "South", layer,
            new Vector3((minX + maxX) * 0.5f, centerY, minZ - (wallThickness * 0.5f)),
            new Vector3(spanX + (wallThickness * 2f), wallHeight, wallThickness));

        CreateWall(parent, "East", layer,
            new Vector3(maxX + (wallThickness * 0.5f), centerY, (minZ + maxZ) * 0.5f),
            new Vector3(wallThickness, wallHeight, spanZ + (wallThickness * 2f)));

        CreateWall(parent, "West", layer,
            new Vector3(minX - (wallThickness * 0.5f), centerY, (minZ + maxZ) * 0.5f),
            new Vector3(wallThickness, wallHeight, spanZ + (wallThickness * 2f)));
    }

    private static void CreateCoastRingWalls(Transform parent,
                                             int layer,
                                             Vector3 boundsCenter,
                                             float centerY,
                                             float radiusX,
                                             float radiusZ,
                                             float wallHeight,
                                             float wallThickness,
                                             int segments)
    {
        float twoPi = Mathf.PI * 2f;
        for (int i = 0; i < segments; i++)
        {
            float a0 = (i / (float)segments) * twoPi;
            float a1 = ((i + 1) / (float)segments) * twoPi;

            Vector3 p0 = new Vector3(
                boundsCenter.x + (Mathf.Cos(a0) * radiusX),
                centerY,
                boundsCenter.z + (Mathf.Sin(a0) * radiusZ));

            Vector3 p1 = new Vector3(
                boundsCenter.x + (Mathf.Cos(a1) * radiusX),
                centerY,
                boundsCenter.z + (Mathf.Sin(a1) * radiusZ));

            Vector3 mid = (p0 + p1) * 0.5f;
            Vector3 tangent = (p1 - p0).normalized;
            float segmentLength = Vector3.Distance(p0, p1);

            var wall = CreateWall(parent, $"Ring_{i:00}", layer,
                mid,
                new Vector3(wallThickness, wallHeight, segmentLength + wallThickness));

            wall.transform.rotation = Quaternion.LookRotation(tangent, Vector3.up);
        }
    }

    private static GameObject CreateWall(Transform parent, string suffix, int layer, Vector3 worldCenter, Vector3 worldSize)
    {
        var wall = new GameObject($"BoundaryWall_{suffix}");
        wall.layer = layer;

        Undo.RegisterCreatedObjectUndo(wall, $"Create BoundaryWall_{suffix}");

        wall.transform.SetParent(parent, true);
        wall.transform.position = worldCenter;
        wall.transform.rotation = Quaternion.identity;
        wall.transform.localScale = Vector3.one;

        var box = wall.AddComponent<BoxCollider>();
        box.isTrigger = false;
        box.center = Vector3.zero;
        box.size = worldSize;

        return wall;
    }
}
