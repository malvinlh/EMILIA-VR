using System;
using UnityEngine;

[DefaultExecutionOrder(-10)]
public class Whiteboard : MonoBehaviour
{
    private int texturesSizeHorizontal;
    private int texturesSizeVertical;

    /// <summary>Width of the whiteboard texture in pixels.</summary>
    public int TextureWidth  => texturesSizeHorizontal;
    /// <summary>Height of the whiteboard texture in pixels.</summary>
    public int TextureHeight => texturesSizeVertical;

    public int penSize = 2;

    private Texture2D texture;
    private Color32[] canvasPixels;
    private Color32[] clearPixels;
    public Color color;

    [Tooltip("Background colour of the whiteboard texture. Use warm cream for journal mode.")]
    public Color backgroundColor = Color.white;

    private bool touching, touchingLast;

    private float posX, posY;
    private float lastX, lastY;  // stored as float texture-pixel coordinates

    // ── Cursor dot ────────────────────────────────────────────────────
    [Tooltip("Colour of the cursor dot shown when touching/drawing.")]
    public Color cursorColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
    [Tooltip("Radius of the cursor dot in pixels when touching.")]
    public int cursorRadius = 3;

    [Tooltip("Colour of the hover cursor (pointing but not touching).")]
    public Color hoverCursorColor = new Color(0.2f, 0.6f, 1f, 0.35f);
    [Tooltip("Radius of the hover cursor in pixels.")]
    public int hoverCursorRadius = 5;

    private float cursorLastX = -1f, cursorLastY = -1f;
    // Snapshot of pixels under the cursor so it can be erased cleanly.
    private Color32[] cursorBackup;
    private int cursorBackupMinU, cursorBackupMinV;
    private int cursorBackupW, cursorBackupH;
    private bool hasCursorBackup;

    // ── Dirty region tracking ────────────────────────────────────────
    private bool hasDirtyRegion;
    private int dirtyMinU, dirtyMinV;
    private int dirtyMaxU, dirtyMaxV;

    [Header("Rendering Performance")]
    [Tooltip("Maximum texture uploads per second while interacting. Set 0 to upload every frame.")]
    [Range(0, 120)]
    public int maxTextureUploadsPerSecond = 90;

    private float nextUploadTime;

    // ── Hover state (pointer without drawing) ─────────────────────────
    private bool hovering, hoveringLast;
    private float hoverPosX, hoverPosY;

    // ── First-touch movement gate ─────────────────────────────────────
    [Tooltip("Min pixel distance the finger must move on first contact before ink is drawn.")]
    public float moveThreshold = 1.5f;
    private bool hasStartedDrawing;

    //One meter should correspond to 1024 pixels on the whiteboard.
    private const int TEXTURE_SCALE = 1024;

    //The default plane in Unity is 10 units by 10 units. When we dynamically rescale our
    //whiteboard in the future, a localScale of 0.1 will correspond to one meter. To correct for
    //this, we will be multiplying our texture sizes by this constant.
    private const int WHITEBOARD_SCALE = 10;

    public bool isActive;

    private void Awake()
    {
        // Self-initialize so the whiteboard works whether it was placed
        // statically in the scene or instantiated at runtime via SpawnAligned.
        Initialize();
    }

    public void Initialize()
    {
        //Scale the texture on the whiteboard based on the size of the whiteboard.
        // texturesSizeHorizontal = (int)(transform.localScale.x * WHITEBOARD_SCALE * TEXTURE_SCALE);
        // texturesSizeVertical = (int)(transform.localScale.z * WHITEBOARD_SCALE * TEXTURE_SCALE);

        // Use lossyScale (world-space scale) so that a whiteboard nested under
        // a parent with non-uniform scale (e.g. JournalTable at 160×16×280)
        // computes the correct texture resolution. For root-level whiteboards
        // (spawned by SpawnAligned) lossyScale == localScale, so no difference.
        Vector3 ws = transform.lossyScale;
        texturesSizeHorizontal = Mathf.Max(
            16,
            (int)(ws.x * WHITEBOARD_SCALE * TEXTURE_SCALE)
        );

        texturesSizeVertical = Mathf.Max(
            16,
            (int)(ws.z * WHITEBOARD_SCALE * TEXTURE_SCALE)
        );

        //Create a new texture and set it as the default texture of this whiteboard
        Renderer renderer = GetComponent<Renderer>();
        texture = new Texture2D(texturesSizeHorizontal, texturesSizeVertical, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;   // smooth scaling / angled viewing

        // Keep a CPU-side pixel buffer to avoid per-stamp GetPixels/SetPixels allocations.
        int pixelCount = texturesSizeHorizontal * texturesSizeVertical;
        canvasPixels = new Color32[pixelCount];
        clearPixels = new Color32[pixelCount];

        Color32 bg = backgroundColor;
        for (int i = 0; i < pixelCount; i++)
            clearPixels[i] = bg;

        Array.Copy(clearPixels, canvasPixels, pixelCount);
        texture.SetPixels32(canvasPixels);
        texture.Apply(false, false);

        renderer.material.mainTexture = texture;

        //Set the color of our pen to black
        color = Color.black;

        isActive = true;
        hasDirtyRegion = false;
        hasCursorBackup = false;
        nextUploadTime = 0f;
    }

    // Update is called once per frame
    void Update()
    {
        if (!isActive) return;

        // Float coordinates – DrawCircle already centres, no offset needed.
        float x = posX * texturesSizeHorizontal;
        float y = posY * texturesSizeVertical;
        Color32 drawColor = color;

        // ── Erase previous cursor before any drawing ──────────────
        EraseCursor();

        if (touching && !touchingLast)
        {
            // ── First frame of contact ────────────────────────────
            // Record position but do NOT draw yet.  Drawing begins
            // only once the user moves beyond moveThreshold.
            this.lastX = x;
            this.lastY = y;
            hasStartedDrawing = false;
        }
        else if (touching && touchingLast)
        {
            // ── Continuous stroke ─────────────────────────────────
            float dx   = x - lastX;
            float dy   = y - lastY;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);

            // On the very first movement we require a larger threshold
            // so the initial "landing jitter" doesn't produce a dot.
            float threshold = hasStartedDrawing ? 0.5f : moveThreshold;

            if (dist >= threshold)
            {
                hasStartedDrawing = true;

                // Step at most 1 pixel apart so circles overlap and
                // produce a perfectly continuous line.
                int steps = Mathf.Max(1, Mathf.CeilToInt(dist));

                for (int i = 0; i <= steps; i++)
                {
                    float t     = (float)i / steps;
                    float lerpX = Mathf.Lerp(lastX, x, t);
                    float lerpY = Mathf.Lerp(lastY, y, t);
                    canvasPixels.DrawCircle(texturesSizeHorizontal, texturesSizeVertical,
                                            drawColor, lerpX, lerpY, penSize);
                    MarkCircleDirty(lerpX, lerpY, penSize);
                }

                this.lastX = x;
                this.lastY = y;
            }
        }
        else
        {
            // ── Not touching ─────────────────────────────────────
            this.lastX = x;
            this.lastY = y;
            hasStartedDrawing = false;
        }

        // ── Draw cursor dot ───────────────────────────────────────
        if (touching)
        {
            DrawCursor(x, y, cursorColor, cursorRadius);
        }
        else if (hovering)
        {
            float hx = hoverPosX * texturesSizeHorizontal;
            float hy = hoverPosY * texturesSizeVertical;
            DrawCursor(hx, hy, hoverCursorColor, hoverCursorRadius);
        }

        // Upload changed pixels to the GPU at a controlled cadence.
        bool stateChanged = (touching != touchingLast) || (hovering != hoveringLast);
        UploadIfNeeded(stateChanged);

        this.touchingLast  = this.touching;
        this.hoveringLast  = this.hovering;
    }

    // ==================================================================
    // CURSOR HELPERS
    // ==================================================================

    /// <summary>
    /// Save the pixels under the cursor region and draw the cursor dot.
    /// </summary>
    private void DrawCursor(float cx, float cy, Color col, int rad)
    {
        int r = rad + 2;  // include AA fringe
        cursorBackupMinU = Mathf.Max(0, Mathf.FloorToInt(cx - r));
        cursorBackupMinV = Mathf.Max(0, Mathf.FloorToInt(cy - r));
        int maxU = Mathf.Min(texturesSizeHorizontal - 1, Mathf.CeilToInt(cx + r));
        int maxV = Mathf.Min(texturesSizeVertical   - 1, Mathf.CeilToInt(cy + r));
        cursorBackupW = maxU - cursorBackupMinU + 1;
        cursorBackupH = maxV - cursorBackupMinV + 1;

        if (cursorBackupW <= 0 || cursorBackupH <= 0) return;

        int backupLength = cursorBackupW * cursorBackupH;
        if (cursorBackup == null || cursorBackup.Length < backupLength)
            cursorBackup = new Color32[backupLength];

        for (int row = 0; row < cursorBackupH; row++)
        {
            int srcIndex = (cursorBackupMinV + row) * texturesSizeHorizontal + cursorBackupMinU;
            int dstIndex = row * cursorBackupW;
            Array.Copy(canvasPixels, srcIndex, cursorBackup, dstIndex, cursorBackupW);
        }

        hasCursorBackup = true;

        cursorLastX = cx;
        cursorLastY = cy;

        // Draw cursor as a soft circle.
        canvasPixels.DrawCircle(texturesSizeHorizontal, texturesSizeVertical,
                                (Color32)col, cx, cy, rad);
        MarkCircleDirty(cx, cy, rad);
    }

    /// <summary>
    /// Restore the pixels that were overwritten by the cursor dot.
    /// </summary>
    private void EraseCursor()
    {
        if (!hasCursorBackup) return;

        for (int row = 0; row < cursorBackupH; row++)
        {
            int dstIndex = (cursorBackupMinV + row) * texturesSizeHorizontal + cursorBackupMinU;
            int srcIndex = row * cursorBackupW;
            Array.Copy(cursorBackup, srcIndex, canvasPixels, dstIndex, cursorBackupW);
        }

        MarkDirtyRect(cursorBackupMinU,
                      cursorBackupMinV,
                      cursorBackupMinU + cursorBackupW - 1,
                      cursorBackupMinV + cursorBackupH - 1);
        hasCursorBackup = false;
    }

    private void MarkCircleDirty(float cx, float cy, int radius)
    {
        int r = radius + 2; // include anti-alias fringe
        MarkDirtyRect(Mathf.FloorToInt(cx - r),
                      Mathf.FloorToInt(cy - r),
                      Mathf.CeilToInt(cx + r),
                      Mathf.CeilToInt(cy + r));
    }

    private void MarkDirtyRect(int minU, int minV, int maxU, int maxV)
    {
        if (texturesSizeHorizontal <= 0 || texturesSizeVertical <= 0)
            return;

        minU = Mathf.Clamp(minU, 0, texturesSizeHorizontal - 1);
        minV = Mathf.Clamp(minV, 0, texturesSizeVertical - 1);
        maxU = Mathf.Clamp(maxU, 0, texturesSizeHorizontal - 1);
        maxV = Mathf.Clamp(maxV, 0, texturesSizeVertical - 1);

        if (maxU < minU || maxV < minV)
            return;

        if (!hasDirtyRegion)
        {
            dirtyMinU = minU;
            dirtyMinV = minV;
            dirtyMaxU = maxU;
            dirtyMaxV = maxV;
            hasDirtyRegion = true;
            return;
        }

        dirtyMinU = Mathf.Min(dirtyMinU, minU);
        dirtyMinV = Mathf.Min(dirtyMinV, minV);
        dirtyMaxU = Mathf.Max(dirtyMaxU, maxU);
        dirtyMaxV = Mathf.Max(dirtyMaxV, maxV);
    }

    private void UploadIfNeeded(bool force)
    {
        if (!hasDirtyRegion || texture == null || canvasPixels == null)
            return;

        if (!force && maxTextureUploadsPerSecond > 0)
        {
            if (Time.unscaledTime < nextUploadTime)
                return;

            nextUploadTime = Time.unscaledTime + (1f / maxTextureUploadsPerSecond);
        }

        texture.SetPixels32(canvasPixels);
        texture.Apply(false, false);
        hasDirtyRegion = false;
    }

    //ToggleTouch allows the WhiteboardPen.cs script to tell
    //the whiteboard if the user is touching the whiteboard.
    public void ToggleTouch(bool touching)
    {
        this.touching = touching;
    }

    //SetTouchPosition takes in the coordinates at which our whiteboard
    //pen intersects the board.
    public void SetTouchPosition(float x, float y)
    {
        this.posX = x;
        this.posY = y;
    }

    /// <summary>
    /// Set the hover position (pointing at the board without touching).
    /// </summary>
    public void SetHoverPosition(float x, float y)
    {
        this.hoverPosX = x;
        this.hoverPosY = y;
    }

    /// <summary>
    /// Toggle the hover state on or off.
    /// </summary>
    public void ToggleHover(bool hovering)
    {
        this.hovering = hovering;
    }

    /// <summary>
    /// Clear the whiteboard back to its background colour (preserving the texture).
    /// Used by ScribbleManager after recognition to wipe handwriting ink.
    /// </summary>
    public void ClearToBackground()
    {
        if (texture == null || canvasPixels == null || clearPixels == null) return;

        Array.Copy(clearPixels, canvasPixels, clearPixels.Length);
        hasCursorBackup = false;
        MarkDirtyRect(0, 0, texturesSizeHorizontal - 1, texturesSizeVertical - 1);
        UploadIfNeeded(force: true);
    }

    /// <summary>
    /// Returns the current whiteboard texture.
    /// </summary>
    public Texture2D GetTexture() => texture;
}