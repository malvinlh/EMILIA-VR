using UnityEngine;

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
    private Color[] cursorBackup;
    private int cursorBackupMinU, cursorBackupMinV;
    private int cursorBackupW, cursorBackupH;
    private bool hasCursorBackup;

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

    public void Initialize()
    {
        //Scale the texture on the whiteboard based on the size of the whiteboard.
        // texturesSizeHorizontal = (int)(transform.localScale.x * WHITEBOARD_SCALE * TEXTURE_SCALE);
        // texturesSizeVertical = (int)(transform.localScale.z * WHITEBOARD_SCALE * TEXTURE_SCALE);

        texturesSizeHorizontal = Mathf.Max(
            16,
            (int)(transform.localScale.x * WHITEBOARD_SCALE * TEXTURE_SCALE)
        );

        texturesSizeVertical = Mathf.Max(
            16,
            (int)(transform.localScale.z * WHITEBOARD_SCALE * TEXTURE_SCALE)
        );

        //Create a new texture and set it as the default texture of this whiteboard
        Renderer renderer = GetComponent<Renderer>();
        texture = new Texture2D(texturesSizeHorizontal, texturesSizeVertical);
        texture.filterMode = FilterMode.Bilinear;   // smooth scaling / angled viewing

        // Fill with background colour
        Color[] fill = new Color[texturesSizeHorizontal * texturesSizeVertical];
        for (int i = 0; i < fill.Length; i++)
            fill[i] = backgroundColor;
        texture.SetPixels(fill);
        texture.Apply();

        renderer.material.mainTexture = texture;

        //Set the color of our pen to black
        color = Color.black;

        isActive = true;
    }

    // Update is called once per frame
    void Update()
    {
        if (!isActive) return;

        // Float coordinates – DrawCircle already centres, no offset needed.
        float x = posX * texturesSizeHorizontal;
        float y = posY * texturesSizeVertical;

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
                    texture.DrawCircle(color, lerpX, lerpY, penSize);
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

        // Batch-apply all texture changes once per frame.
        if (touching || touchingLast || hovering || hoveringLast)
        {
            texture.Apply();
        }

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

        cursorBackup = texture.GetPixels(cursorBackupMinU, cursorBackupMinV,
                                         cursorBackupW, cursorBackupH);
        hasCursorBackup = true;

        cursorLastX = cx;
        cursorLastY = cy;

        // Draw cursor as a soft circle.
        texture.DrawCircle(col, cx, cy, rad);
    }

    /// <summary>
    /// Restore the pixels that were overwritten by the cursor dot.
    /// </summary>
    private void EraseCursor()
    {
        if (!hasCursorBackup) return;

        texture.SetPixels(cursorBackupMinU, cursorBackupMinV,
                          cursorBackupW, cursorBackupH, cursorBackup);
        hasCursorBackup = false;
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
        if (texture == null) return;
        Color[] fill = new Color[texturesSizeHorizontal * texturesSizeVertical];
        for (int i = 0; i < fill.Length; i++)
            fill[i] = backgroundColor;
        texture.SetPixels(fill);
        texture.Apply();
    }

    /// <summary>
    /// Returns the current whiteboard texture (for VLM image capture).
    /// </summary>
    public Texture2D GetTexture() => texture;
}