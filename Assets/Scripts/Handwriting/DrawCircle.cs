using UnityEngine;

public static class Texture2DExtension
{
    /// <summary>
    /// Draws an anti-aliased filled circle into a Color32 pixel buffer.
    /// This avoids per-stamp texture block reads/allocations.
    /// </summary>
    public static void DrawCircle(this Color32[] pixels,
                                  int width,
                                  int height,
                                  Color32 color,
                                  float cx,
                                  float cy,
                                  int radius = 3)
    {
        if (pixels == null || width <= 0 || height <= 0)
            return;

        float r = radius;

        int minU = Mathf.Max(0, Mathf.FloorToInt(cx - r - 1));
        int maxU = Mathf.Min(width  - 1, Mathf.CeilToInt(cx + r + 1));
        int minV = Mathf.Max(0, Mathf.FloorToInt(cy - r - 1));
        int maxV = Mathf.Min(height - 1, Mathf.CeilToInt(cy + r + 1));

        if (maxU < minU || maxV < minV)
            return;

        for (int v = minV; v <= maxV; v++)
        {
            int rowBase = v * width;
            for (int u = minU; u <= maxU; u++)
            {
                float dx = u - cx;
                float dy = v - cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                int idx = rowBase + u;

                if (dist <= r - 0.5f)
                {
                    pixels[idx] = color;
                }
                else if (dist <= r + 0.5f)
                {
                    float coverage = Mathf.Clamp01(r + 0.5f - dist);
                    pixels[idx] = LerpColor32(pixels[idx], color, coverage);
                }
            }
        }
    }

    private static Color32 LerpColor32(Color32 from, Color32 to, float t)
    {
        t = Mathf.Clamp01(t);
        byte r = (byte)(from.r + (to.r - from.r) * t);
        byte g = (byte)(from.g + (to.g - from.g) * t);
        byte b = (byte)(from.b + (to.b - from.b) * t);
        byte a = (byte)(from.a + (to.a - from.a) * t);
        return new Color32(r, g, b, a);
    }

    /// <summary>
    /// Draws an anti-aliased filled circle centred at sub-pixel coordinates
    /// (cx, cy).  Interior pixels are fully opaque; boundary pixels are
    /// alpha-blended with the existing texture colour for smooth edges.
    ///
    /// Uses GetPixels/SetPixels block operations instead of per-pixel
    /// SetPixel calls for significantly better performance on mobile GPUs.
    /// </summary>
    public static Texture2D DrawCircle(this Texture2D tex, Color color,
                                       float cx, float cy, int radius = 3)
    {
        float r = radius;

        // Bounding box + 1 px fringe for the AA transition zone.
        int minU = Mathf.Max(0, Mathf.FloorToInt(cx - r - 1));
        int maxU = Mathf.Min(tex.width  - 1, Mathf.CeilToInt(cx + r + 1));
        int minV = Mathf.Max(0, Mathf.FloorToInt(cy - r - 1));
        int maxV = Mathf.Min(tex.height - 1, Mathf.CeilToInt(cy + r + 1));

        int blockW = maxU - minU + 1;
        int blockH = maxV - minV + 1;
        if (blockW <= 0 || blockH <= 0) return tex;

        // Read the entire bounding-box block in one call.
        Color[] pixels = tex.GetPixels(minU, minV, blockW, blockH);

        for (int u = minU; u <= maxU; u++)
        {
            for (int v = minV; v <= maxV; v++)
            {
                float dx   = u - cx;
                float dy   = v - cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                int idx = (v - minV) * blockW + (u - minU);

                if (dist <= r - 0.5f)
                {
                    // Fully inside – solid fill.
                    pixels[idx] = color;
                }
                else if (dist <= r + 0.5f)
                {
                    // Edge pixel – smooth blend based on coverage.
                    float coverage = Mathf.Clamp01(r + 0.5f - dist);
                    pixels[idx] = Color.Lerp(pixels[idx], color, coverage);
                }
            }
        }

        // Write the entire block back in one call.
        tex.SetPixels(minU, minV, blockW, blockH, pixels);

        return tex;
    }
}