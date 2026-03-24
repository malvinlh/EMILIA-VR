using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a grid of small dots on the detected surface around both palms,
/// mimicking Meta Quest 3's AR Surface Keyboard visual feedback.
///
/// The dots are positioned purely from hand joint data — no AR plane scanning needed.
/// Palm Y = table height, palm XZ positions = spawn area.
///
/// The grid covers the rectangular area where the whiteboard will spawn,
/// giving the user immediate visual feedback of the target zone.
/// </summary>
public class SurfaceDotGrid : MonoBehaviour
{
    [Header("Grid Layout")]
    [Tooltip("Spacing between dots in metres.")]
    public float dotSpacing = 0.035f;
    [Tooltip("Padding beyond the palm area on each side (metres).")]
    public float gridPadding = 0.08f;
    [Tooltip("Fixed depth (Z extent) of the dot grid in metres.")]
    public float gridDepth = 0.35f;
    [Tooltip("Max number of dots to spawn (performance cap).")]
    public int maxDots = 120;

    [Header("Dot Appearance")]
    [Tooltip("Diameter of each dot sphere.")]
    public float dotSize = 0.006f;
    [Tooltip("Default dot colour (white, semi-transparent like Quest keyboard).")]
    public Color idleColor = new Color(1f, 1f, 1f, 0.6f);
    [Tooltip("Colour when confirmation is progressing.")]
    public Color confirmingColor = new Color(0.3f, 0.9f, 0.4f, 0.75f);
    [Tooltip("Colour on successful confirmation.")]
    public Color confirmedColor = new Color(0.3f, 0.95f, 0.5f, 0.9f);

    [Header("Animation")]
    [Tooltip("How quickly dots scale in when appearing.")]
    public float scaleInSpeed = 8f;
    [Tooltip("Random Y-offset jitter for organic feel (metres).")]
    public float yJitter = 0.001f;

    // ── State ────────────────────────────────────────────────────────
    private List<GameObject> dotPool = new List<GameObject>();
    private List<Vector3> targetPositions = new List<Vector3>();
    private Material dotMaterial;
    private int activeDotCount;
    private int passthroughLayer = 31;
    private bool isShowing;
    private float currentProgress;

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Initialise the dot grid. Call once before Show().
    /// </summary>
    public void Initialise(int layer)
    {
        passthroughLayer = layer;
        CreateDotMaterial();
        PrewarmPool(maxDots);
    }

    /// <summary>
    /// Update the dot grid to surround the two palm positions.
    /// Call every frame while palms are tracked and flat.
    /// </summary>
    public void UpdateGrid(Vector3 leftPalm, Vector3 rightPalm, float confirmProgress)
    {
        if (dotMaterial == null) return;

        isShowing = true;
        currentProgress = confirmProgress;

        // Compute grid rectangle from palm positions
        Vector3 midpoint = (leftPalm + rightPalm) / 2f;
        float palmDistance = Vector3.Distance(
            new Vector3(leftPalm.x, 0f, leftPalm.z),
            new Vector3(rightPalm.x, 0f, rightPalm.z));

        float gridWidth = palmDistance + gridPadding * 2f;
        // Palm joint sits ~12 mm above the physical table surface.
        // Subtract that offset so dots appear on the table, not on the dorsal hand.
        float tableY = (leftPalm.y + rightPalm.y) / 2f - 0.012f;

        // Grid orientation: aligned with palm-to-palm axis (left-right)
        Vector3 palmAxis = rightPalm - leftPalm;
        palmAxis.y = 0f;
        if (palmAxis.sqrMagnitude < 0.001f)
            palmAxis = Vector3.right;
        palmAxis.Normalize();

        // Forward = perpendicular to palm axis (away from user)
        Camera cam = Camera.main;
        Vector3 userForward = Vector3.forward;
        if (cam != null)
        {
            userForward = cam.transform.forward;
            userForward.y = 0f;
            userForward.Normalize();
        }

        // Ensure forward points away from user (into the table)
        Vector3 gridForward = Vector3.Cross(Vector3.up, palmAxis).normalized;
        if (Vector3.Dot(gridForward, userForward) < 0f)
            gridForward = -gridForward;

        // Build grid positions
        ComputeGridPositions(midpoint, palmAxis, gridForward, gridWidth, gridDepth, tableY);

        // Update dot pool
        int count = Mathf.Min(targetPositions.Count, maxDots);
        ActivateDots(count);

        // Lerp color based on progress
        Color currentColor = Color.Lerp(idleColor, confirmingColor, confirmProgress);
        dotMaterial.color = currentColor;

        // Position and animate dots
        for (int i = 0; i < activeDotCount; i++)
        {
            var dot = dotPool[i];
            Vector3 target = targetPositions[i];

            // Add subtle Y jitter for organic feel
            float jitter = Mathf.PerlinNoise(target.x * 50f, target.z * 50f) * yJitter;
            target.y += jitter;

            // Smooth scale-in animation
            float targetScale = dotSize;
            Vector3 currentScale = dot.transform.localScale;
            float newScale = Mathf.MoveTowards(currentScale.x, targetScale, Time.deltaTime * scaleInSpeed * dotSize);
            dot.transform.localScale = Vector3.one * newScale;

            // Position follows palms smoothly
            dot.transform.position = Vector3.Lerp(dot.transform.position, target, Time.deltaTime * 12f);
        }
    }

    /// <summary>
    /// Flash the grid to confirmed colour, then hide.
    /// Call once when confirmation completes.
    /// </summary>
    public void FlashConfirmed()
    {
        if (dotMaterial != null)
            dotMaterial.color = confirmedColor;
    }

    /// <summary>
    /// Hide all dots (e.g. when palms lift or session ends).
    /// </summary>
    public void Hide()
    {
        isShowing = false;
        for (int i = 0; i < dotPool.Count; i++)
        {
            dotPool[i].SetActive(false);
            dotPool[i].transform.localScale = Vector3.zero;
        }
        activeDotCount = 0;
    }

    /// <summary>
    /// Clean up all pooled dots.
    /// </summary>
    public void Cleanup()
    {
        for (int i = 0; i < dotPool.Count; i++)
        {
            if (dotPool[i] != null)
                Destroy(dotPool[i]);
        }
        dotPool.Clear();

        if (dotMaterial != null)
            Destroy(dotMaterial);
    }

    // ================================================================
    // GRID COMPUTATION
    // ================================================================

    private void ComputeGridPositions(Vector3 center, Vector3 right, Vector3 forward,
        float width, float depth, float y)
    {
        targetPositions.Clear();

        int colCount = Mathf.FloorToInt(width / dotSpacing) + 1;
        int rowCount = Mathf.FloorToInt(depth / dotSpacing) + 1;

        float halfWidth = (colCount - 1) * dotSpacing / 2f;
        float halfDepth = (rowCount - 1) * dotSpacing / 2f;

        for (int row = 0; row < rowCount; row++)
        {
            for (int col = 0; col < colCount; col++)
            {
                if (targetPositions.Count >= maxDots) return;

                float xOffset = col * dotSpacing - halfWidth;
                float zOffset = row * dotSpacing - halfDepth;

                Vector3 pos = center + right * xOffset + forward * zOffset;
                pos.y = y;

                targetPositions.Add(pos);
            }
        }
    }

    // ================================================================
    // OBJECT POOL
    // ================================================================

    private void PrewarmPool(int count)
    {
        for (int i = dotPool.Count; i < count; i++)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = $"SurfaceDot_{i}";

            // Remove collider — dots are visual only
            var col = dot.GetComponent<Collider>();
            if (col != null) Destroy(col);

            dot.transform.localScale = Vector3.zero;
            dot.layer = passthroughLayer;

            var rend = dot.GetComponent<MeshRenderer>();
            rend.sharedMaterial = dotMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;

            dot.SetActive(false);
            dotPool.Add(dot);
        }
    }

    private void ActivateDots(int count)
    {
        // Activate needed dots
        for (int i = 0; i < count; i++)
        {
            if (!dotPool[i].activeSelf)
            {
                dotPool[i].SetActive(true);
                dotPool[i].transform.localScale = Vector3.zero; // Will animate in
            }
        }

        // Deactivate excess dots
        for (int i = count; i < activeDotCount; i++)
        {
            dotPool[i].SetActive(false);
            dotPool[i].transform.localScale = Vector3.zero;
        }

        activeDotCount = count;
    }

    // ================================================================
    // MATERIAL
    // ================================================================

    private void CreateDotMaterial()
    {
        dotMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

        // Transparent rendering
        dotMaterial.SetFloat("_Surface", 1f);
        dotMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        dotMaterial.SetFloat("_Blend", 0f);
        dotMaterial.SetFloat("_ZWrite", 0f);
        dotMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        dotMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        dotMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        dotMaterial.color = idleColor;
    }

    private void OnDestroy()
    {
        Cleanup();
    }
}
