using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only sanity check for <see cref="MLKitStrokeFont"/>.
///
/// ML Kit is Android-only, so the benchmark's ink cannot be inspected by pressing
/// Play — the first chance to see whether a word is actually legible would otherwise
/// be after a full APK build. This renders the configured words to PNGs in seconds.
///
/// Menu: Tools -> EMILIA -> Preview ML Kit Stroke Font
/// </summary>
public static class MLKitStrokeFontPreview
{
    private const int SCALE = 3;              // px per ML Kit px, for legibility
    private const string OUT_DIR = "MLKitStrokeFontPreview";

    [MenuItem("Tools/EMILIA/Preview ML Kit Stroke Font")]
    public static void Preview()
    {
        var opt = new MLKitStrokeFont.LayoutOptions();
        var words = new List<(string label, string text)>();

        // Prefer the values actually configured on the benchmark in the open scene,
        // so the preview shows what the build will really feed the recognizer.
        var bench = Object.FindFirstObjectByType<MLKitBenchmark>(FindObjectsInactive.Include);
        if (bench != null)
        {
            var so = new SerializedObject(bench);
            opt.letterHeightPx  = so.FindProperty("letterHeightPx").floatValue;
            opt.minLetterHeightPx = so.FindProperty("minLetterHeightPx").floatValue;
            opt.letterSpacingPx = so.FindProperty("letterSpacingPx").floatValue;
            opt.wordSpacingPx   = so.FindProperty("wordSpacingPx").floatValue;
            opt.jitterPx        = so.FindProperty("jitterPx").floatValue;
            opt.seed            = so.FindProperty("strokeSeed").intValue;

            var arr = so.FindProperty("words");
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                string label = el.FindPropertyRelative("label").stringValue;
                string text = el.FindPropertyRelative("text").stringValue;
                if (!string.IsNullOrEmpty(text))
                    words.Add((string.IsNullOrEmpty(label) ? text : label, text));
            }
            Debug.Log($"[StrokeFontPreview] Using settings from MLKitBenchmark on '{bench.name}'.");
        }

        if (words.Count == 0)
        {
            Debug.LogWarning("[StrokeFontPreview] No MLKitBenchmark (or no words) in the open scene — " +
                             "previewing defaults instead.");
            words.Add(("word-short", "aku"));
            words.Add(("word-medium", "tenang"));
            words.Add(("word-long", "hari ini tenang"));
        }

        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OUT_DIR);
        Directory.CreateDirectory(dir);

        for (int i = 0; i < words.Count; i++)
        {
            var (label, text) = words[i];
            var localOpt = Clone(opt);
            localOpt.seed = opt.seed + i * 977; // mirrors MLKitBenchmark.MakeWordSamples

            var strokes = MLKitStrokeFont.Layout(text, localOpt);
            int points = 0;
            foreach (var s in strokes) points += s.Count;

            var tex = Render(strokes, (int)localOpt.areaW, (int)localOpt.areaH);
            string file = Path.Combine(dir, Sanitize(label) + ".png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            Debug.Log($"[StrokeFontPreview] \"{text}\" -> {strokes.Count} strokes, {points} points -> {file}");
        }

        Debug.Log($"[StrokeFontPreview] Done. Open the PNGs in {dir} and check every word is legible.");
        EditorUtility.RevealInFinder(dir + Path.DirectorySeparatorChar);
    }

    private static MLKitStrokeFont.LayoutOptions Clone(MLKitStrokeFont.LayoutOptions o)
        => new MLKitStrokeFont.LayoutOptions
        {
            areaW = o.areaW, areaH = o.areaH, marginPx = o.marginPx,
            letterHeightPx = o.letterHeightPx, minLetterHeightPx = o.minLetterHeightPx,
            letterSpacingPx = o.letterSpacingPx,
            wordSpacingPx = o.wordSpacingPx, jitterPx = o.jitterPx,
            seed = o.seed, resampleSpacingPx = o.resampleSpacingPx,
        };

    /// <summary>Draw the strokes into a texture. Note: ML Kit space is y-DOWN, textures are y-up.</summary>
    private static Texture2D Render(List<List<Vector2>> strokes, int areaW, int areaH)
    {
        int w = areaW * SCALE, h = areaH * SCALE;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);

        // Canvas border, so it's obvious if ink is clipping the edges.
        var grey = new Color(0.85f, 0.85f, 0.85f);
        for (int x = 0; x < w; x++) { tex.SetPixel(x, 0, grey); tex.SetPixel(x, h - 1, grey); }
        for (int y = 0; y < h; y++) { tex.SetPixel(0, y, grey); tex.SetPixel(w - 1, y, grey); }

        foreach (var stroke in strokes)
            for (int i = 1; i < stroke.Count; i++)
                DrawLine(tex, ToTex(stroke[i - 1], h), ToTex(stroke[i], h), Color.black);

        tex.Apply();
        return tex;
    }

    private static Vector2 ToTex(Vector2 p, int texH) => new Vector2(p.x * SCALE, texH - 1 - p.y * SCALE);

    private static void DrawLine(Texture2D tex, Vector2 a, Vector2 b, Color c)
    {
        float dist = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(dist));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)i / steps);
            // 2 px nib so thin strokes stay visible when zoomed out.
            for (int dx = 0; dx <= 1; dx++)
                for (int dy = 0; dy <= 1; dy++)
                {
                    int x = Mathf.RoundToInt(p.x) + dx, y = Mathf.RoundToInt(p.y) + dy;
                    if (x >= 0 && x < tex.width && y >= 0 && y < tex.height) tex.SetPixel(x, y, c);
                }
        }
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
