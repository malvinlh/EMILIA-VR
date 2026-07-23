using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A tiny single-line "stroke font": turns a word into pen-like polylines in the
/// ML Kit logical canvas (300x200 px, y increasing DOWNWARD — the same space
/// <see cref="WhiteboardPen"/> projects real finger strokes into).
///
/// Purpose: let <see cref="MLKitBenchmark"/> feed the recogniser ink that actually
/// spells a known word, so recognition ACCURACY can be measured automatically —
/// the synthetic zig-zags only ever gave valid latency with accuracy = n/a.
///
/// Glyphs are authored in a normalised em box, y-down:
///     y = 0.05  ascender top   (b d f h k l t)
///     y = 0.35  x-height top
///     y = 0.75  baseline
///     y = 0.98  descender bottom (g j p q y)
/// x runs 0..advance. One glyph is a list of strokes; one stroke is a polyline.
///
/// NOTE: these are procedurally drawn letterforms, not human handwriting. Accuracy
/// measured against them is a clean-input upper bound — see MLKitBenchmark_README.md.
/// </summary>
public static class MLKitStrokeFont
{
    // ── Em-box reference lines (normalised, y-down) ──
    public const float ASCENDER = 0.05f;
    public const float XHEIGHT  = 0.35f;
    public const float BASELINE = 0.75f;
    public const float DESCENDER = 0.98f;

    /// <summary>Ascender-to-baseline span, as a fraction of the em box.</summary>
    public const float LETTER_SPAN = BASELINE - ASCENDER; // 0.70

    /// <summary>Layout knobs. Pixel values are in the 300x200 ML Kit canvas.</summary>
    public class LayoutOptions
    {
        /// <summary>Canvas width (px). Must match DigitalInkBridge.SetWritingArea.</summary>
        public float areaW = 300f;
        /// <summary>Canvas height (px).</summary>
        public float areaH = 200f;
        /// <summary>Margin kept clear on every side (px).</summary>
        public float marginPx = 18f;
        /// <summary>
        /// Ascender-to-baseline height of a tall letter (px). ~40 px == a 4 cm
        /// finger-written letter at WhiteboardPen's 1000 px/m scale.
        /// </summary>
        public float letterHeightPx = 40f;
        /// <summary>
        /// Floor for the shrink-to-one-line fit. Below this the text wraps to a second
        /// line instead of shrinking further into implausibly tiny handwriting.
        /// </summary>
        public float minLetterHeightPx = 22f;
        /// <summary>Extra gap between letters (px).</summary>
        public float letterSpacingPx = 4f;
        /// <summary>Gap between words (px).</summary>
        public float wordSpacingPx = 16f;
        /// <summary>Hand-wobble amplitude (px). 0 = mathematically perfect vectors.</summary>
        public float jitterPx = 0.8f;
        /// <summary>Seed for the deterministic wobble — same seed, same ink, every time.</summary>
        public int seed = 12345;
        /// <summary>Target spacing between emitted points (px); matches real pen sampling density.</summary>
        public float resampleSpacingPx = 2.5f;
    }

    // ==================================================================
    // PUBLIC API
    // ==================================================================

    /// <summary>
    /// Lay <paramref name="text"/> out as strokes in ML Kit pixel space.
    /// Unsupported characters are skipped; uppercase is folded to lowercase
    /// (the glyph table is lowercase-only).
    /// </summary>
    public static List<List<Vector2>> Layout(string text, LayoutOptions opt)
    {
        var result = new List<List<Vector2>>();
        if (string.IsNullOrEmpty(text)) return result;
        opt = opt ?? new LayoutOptions();

        string[] words = text.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return result;

        float usableW = opt.areaW - 2f * opt.marginPx;
        float usableH = opt.areaH - 2f * opt.marginPx;

        // ── Fit ──
        // Prefer ONE line: ML Kit recognises a single line of ink far more reliably than
        // a wrapped block, and a user writing a short phrase writes it on one line.
        // letterSpacing/wordSpacing are fixed px and do NOT scale with the em, so solve
        // for the em rather than multiplying by a naive width ratio:
        //     glyphSum * em + fixedSpacing = usableW
        float emPx = opt.letterHeightPx / LETTER_SPAN;
        float minEmPx = opt.minLetterHeightPx / LETTER_SPAN;

        MeasureParts(words, opt, out float glyphSum, out float fixedPx);
        if (glyphSum > 0f && glyphSum * emPx + fixedPx > usableW)
            emPx = Mathf.Max(minEmPx, (usableW - fixedPx) / glyphSum);

        // A single word wider than the canvas must still fit, floor or no floor.
        foreach (var w in words)
        {
            MeasureParts(new[] { w }, opt, out float gs, out float fx);
            if (gs > 0f && gs * emPx + fx > usableW)
                emPx = (usableW - fx) / gs;
        }

        // ── Wrap words into lines ──
        var lines = new List<List<string>>();
        var current = new List<string>();
        float currentW = 0f;
        foreach (var w in words)
        {
            float ww = WordWidthPx(w, emPx, opt.letterSpacingPx);
            float withGap = current.Count == 0 ? ww : currentW + opt.wordSpacingPx + ww;
            if (current.Count > 0 && withGap > usableW)
            {
                lines.Add(current);
                current = new List<string> { w };
                currentW = ww;
            }
            else
            {
                current.Add(w);
                currentW = withGap;
            }
        }
        if (current.Count > 0) lines.Add(current);

        // ── Vertical placement: centre the block of lines in the canvas ──
        float lineHeight = emPx * 1.15f;
        float blockH = lineHeight * lines.Count;
        if (blockH > usableH && lines.Count > 0)
        {
            // Too tall (very long phrase) — scale everything down to fit.
            float shrink = usableH / blockH;
            emPx *= shrink;
            lineHeight = emPx * 1.15f;
            blockH = lineHeight * lines.Count;
        }
        float blockTop = (opt.areaH - blockH) * 0.5f;

        var rng = new System.Random(opt.seed);

        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];

            float lineW = 0f;
            for (int wi = 0; wi < line.Count; wi++)
            {
                lineW += WordWidthPx(line[wi], emPx, opt.letterSpacingPx);
                if (wi > 0) lineW += opt.wordSpacingPx;
            }

            float penX = (opt.areaW - lineW) * 0.5f;
            // Baseline sits at BASELINE within the em box of this line.
            float baselineY = blockTop + li * lineHeight + BASELINE * emPx;

            bool lastGlyphAdvanced = false;
            for (int wi = 0; wi < line.Count; wi++)
            {
                if (wi > 0) penX += opt.wordSpacingPx;
                foreach (char c in line[wi])
                {
                    var glyph = GetGlyph(c);
                    if (glyph == null)
                    {
                        Debug.LogWarning($"[MLKitStrokeFont] No glyph for '{c}' — skipped.");
                        continue;
                    }

                    // Per-glyph wobble: a real hand never repeats a letter identically.
                    float rot = (float)(rng.NextDouble() * 2.0 - 1.0) * 2f * Mathf.Deg2Rad; // +/-2 deg
                    float scl = 1f + (float)(rng.NextDouble() * 2.0 - 1.0) * 0.03f;          // +/-3%
                    float dx  = (float)(rng.NextDouble() * 2.0 - 1.0) * emPx * 0.01f;
                    float dy  = (float)(rng.NextDouble() * 2.0 - 1.0) * emPx * 0.015f;
                    float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);

                    foreach (var stroke in glyph.strokes)
                    {
                        var pts = new List<Vector2>(stroke.Count);
                        foreach (var g in stroke)
                        {
                            // Normalised -> pixels, relative to the glyph's own centre so
                            // the rotation wobble pivots sensibly.
                            float gx = (g.x - glyph.advance * 0.5f) * emPx * scl;
                            float gy = (g.y - BASELINE) * emPx * scl;
                            float rx = gx * cosR - gy * sinR;
                            float ry = gx * sinR + gy * cosR;
                            pts.Add(new Vector2(
                                penX + glyph.advance * 0.5f * emPx * scl + rx + dx,
                                baselineY + ry + dy));
                        }

                        var dense = Resample(pts, opt.resampleSpacingPx);
                        Jitter(dense, opt.jitterPx, rng);
                        Clamp(dense, opt.areaW, opt.areaH);
                        if (dense.Count > 0) result.Add(dense);
                    }

                    penX += glyph.advance * emPx * scl + opt.letterSpacingPx;
                    lastGlyphAdvanced = true;
                }
                // WordWidthPx counts letter gaps between glyphs only, so drop the
                // trailing one to keep placement consistent with the measurement.
                if (lastGlyphAdvanced) { penX -= opt.letterSpacingPx; lastGlyphAdvanced = false; }
            }
        }

        return result;
    }

    /// <summary>True if every non-space character in <paramref name="text"/> has a glyph.</summary>
    public static bool CanRender(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (c == ' ') continue;
            if (GetGlyph(c) == null) return false;
        }
        return true;
    }

    // ==================================================================
    // LAYOUT HELPERS
    // ==================================================================

    /// <summary>
    /// Split a line's width into the part that scales with the em (<paramref name="glyphSum"/>,
    /// in em units) and the part that does not (<paramref name="fixedPx"/> — letter and word gaps).
    /// </summary>
    private static void MeasureParts(IList<string> words, LayoutOptions opt,
                                     out float glyphSum, out float fixedPx)
    {
        glyphSum = 0f;
        int glyphCount = 0;
        foreach (var w in words)
            foreach (char c in w)
            {
                var g = GetGlyph(c);
                if (g == null) continue;
                glyphSum += g.advance;
                glyphCount++;
            }
        int gaps = Mathf.Max(0, glyphCount - words.Count);
        fixedPx = opt.letterSpacingPx * gaps + opt.wordSpacingPx * Mathf.Max(0, words.Count - 1);
    }

    private static float WordWidthPx(string word, float emPx, float letterSpacingPx)
    {
        float w = 0f;
        int n = 0;
        foreach (char c in word)
        {
            var g = GetGlyph(c);
            if (g == null) continue;
            w += g.advance * emPx;
            n++;
        }
        if (n > 1) w += letterSpacingPx * (n - 1);
        return w;
    }

    /// <summary>Re-space a polyline to roughly uniform <paramref name="spacing"/> px steps.</summary>
    private static List<Vector2> Resample(List<Vector2> pts, float spacing)
    {
        var outPts = new List<Vector2>();
        if (pts == null || pts.Count == 0) return outPts;
        if (pts.Count == 1 || spacing <= 0f) { outPts.AddRange(pts); return outPts; }

        outPts.Add(pts[0]);
        float carry = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            float seg = Vector2.Distance(a, b);
            if (seg <= 1e-5f) continue;

            float t = spacing - carry;
            while (t <= seg)
            {
                outPts.Add(Vector2.Lerp(a, b, t / seg));
                t += spacing;
            }
            carry = seg - (t - spacing);
        }
        // Always keep the true end point so the letterform closes properly.
        if (Vector2.Distance(outPts[outPts.Count - 1], pts[pts.Count - 1]) > 0.5f)
            outPts.Add(pts[pts.Count - 1]);
        return outPts;
    }

    /// <summary>
    /// Add low-frequency wobble. Deliberately NOT white noise: per-point random
    /// offsets at 2.5 px spacing make the ink furry and hurt recognition, whereas a
    /// slow drift reads as a human hand.
    /// </summary>
    private static void Jitter(List<Vector2> pts, float amplitude, System.Random rng)
    {
        if (amplitude <= 0f || pts.Count < 2) return;

        float phaseX = (float)(rng.NextDouble() * Mathf.PI * 2f);
        float phaseY = (float)(rng.NextDouble() * Mathf.PI * 2f);
        float freqX  = 0.6f + (float)rng.NextDouble() * 0.8f;
        float freqY  = 0.6f + (float)rng.NextDouble() * 0.8f;

        for (int i = 0; i < pts.Count; i++)
        {
            float u = (float)i / (pts.Count - 1) * Mathf.PI * 2f;
            pts[i] = new Vector2(
                pts[i].x + Mathf.Sin(u * freqX + phaseX) * amplitude,
                pts[i].y + Mathf.Sin(u * freqY + phaseY) * amplitude);
        }
    }

    private static void Clamp(List<Vector2> pts, float w, float h)
    {
        for (int i = 0; i < pts.Count; i++)
            pts[i] = new Vector2(Mathf.Clamp(pts[i].x, 0f, w), Mathf.Clamp(pts[i].y, 0f, h));
    }

    // ==================================================================
    // GLYPH TABLE
    // ==================================================================

    private class Glyph
    {
        public float advance;
        public List<List<Vector2>> strokes;
    }

    private static Dictionary<char, Glyph> _glyphs;

    private static Glyph GetGlyph(char c)
    {
        if (_glyphs == null) _glyphs = BuildGlyphs();
        return _glyphs.TryGetValue(c, out var g) ? g : null;
    }

    private static Vector2 V(float x, float y) => new Vector2(x, y);

    /// <summary>Straight segment as a polyline.</summary>
    private static List<Vector2> Line(Vector2 a, Vector2 b, int segments = 6)
    {
        var pts = new List<Vector2>(segments + 1);
        for (int i = 0; i <= segments; i++) pts.Add(Vector2.Lerp(a, b, (float)i / segments));
        return pts;
    }

    /// <summary>
    /// Elliptical arc. Angles in degrees, y-DOWN convention:
    /// 0 = right, 90 = bottom, 180 = left, 270 = top. Interpolates linearly from
    /// <paramref name="startDeg"/> to <paramref name="endDeg"/>, so a decreasing
    /// range sweeps the other way round.
    /// </summary>
    private static List<Vector2> Arc(float cx, float cy, float rx, float ry,
                                     float startDeg, float endDeg, int segments = 0)
    {
        if (segments <= 0) segments = Mathf.Max(4, Mathf.RoundToInt(Mathf.Abs(endDeg - startDeg) / 12f));
        var pts = new List<Vector2>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Lerp(startDeg, endDeg, (float)i / segments) * Mathf.Deg2Rad;
            pts.Add(new Vector2(cx + rx * Mathf.Cos(a), cy + ry * Mathf.Sin(a)));
        }
        return pts;
    }

    /// <summary>Concatenate segments into one continuous stroke (drops duplicated joins).</summary>
    private static List<Vector2> Join(params List<Vector2>[] parts)
    {
        var pts = new List<Vector2>();
        foreach (var p in parts)
        {
            if (p == null || p.Count == 0) continue;
            int start = (pts.Count > 0 && Vector2.Distance(pts[pts.Count - 1], p[0]) < 1e-4f) ? 1 : 0;
            for (int i = start; i < p.Count; i++) pts.Add(p[i]);
        }
        return pts;
    }

    private static Glyph G(float advance, params List<Vector2>[] strokes)
        => new Glyph { advance = advance, strokes = new List<List<Vector2>>(strokes) };

    private static Dictionary<char, Glyph> BuildGlyphs()
    {
        const float A = ASCENDER;   // 0.05
        const float X = XHEIGHT;    // 0.35
        const float B = BASELINE;   // 0.75
        const float D = DESCENDER;  // 0.98
        const float M = 0.55f;      // x-height middle

        var g = new Dictionary<char, Glyph>();

        // a — bowl + right stem (single-storey)
        g['a'] = G(0.48f,
            Arc(0.20f, M, 0.17f, 0.20f, 0f, 360f),
            Line(V(0.37f, X), V(0.37f, B)));

        // b — ascender stem + bowl
        g['b'] = G(0.48f,
            Line(V(0.06f, A), V(0.06f, B)),
            Arc(0.23f, M, 0.17f, 0.20f, 0f, 360f));

        // c — open arc, gap on the right
        g['c'] = G(0.42f,
            Arc(0.22f, M, 0.16f, 0.20f, 315f, 45f));

        // d — bowl + ascender stem
        g['d'] = G(0.48f,
            Arc(0.20f, M, 0.17f, 0.20f, 0f, 360f),
            Line(V(0.37f, A), V(0.37f, B)));

        // e — crossbar then round, one stroke
        g['e'] = G(0.42f,
            Join(Line(V(0.06f, M), V(0.38f, M)),
                 Arc(0.22f, M, 0.16f, 0.20f, 0f, -315f)));

        // f — hook over the top + stem, plus crossbar
        g['f'] = G(0.32f,
            Join(Arc(0.19f, 0.16f, 0.11f, 0.11f, 0f, -180f),
                 Line(V(0.08f, 0.16f), V(0.08f, B))),
            Line(V(0.00f, X), V(0.24f, X)));

        // g — bowl + descender with a left hook
        g['g'] = G(0.48f,
            Arc(0.20f, M, 0.17f, 0.20f, 0f, 360f),
            Join(Line(V(0.37f, X), V(0.37f, 0.88f)),
                 Arc(0.20f, 0.88f, 0.17f, 0.10f, 0f, 150f)));

        // h — ascender stem + shoulder
        g['h'] = G(0.48f,
            Line(V(0.06f, A), V(0.06f, B)),
            Join(Arc(0.22f, M, 0.16f, 0.20f, 180f, 360f),
                 Line(V(0.38f, M), V(0.38f, B))));

        // i — stem + dot
        g['i'] = G(0.24f,
            Line(V(0.12f, X), V(0.12f, B)),
            Arc(0.12f, 0.20f, 0.022f, 0.022f, 0f, 360f, 8));

        // j — descender stem with hook + dot
        g['j'] = G(0.26f,
            Join(Line(V(0.15f, X), V(0.15f, 0.88f)),
                 Arc(0.02f, 0.88f, 0.13f, 0.10f, 0f, 150f)),
            Arc(0.15f, 0.20f, 0.022f, 0.022f, 0f, 360f, 8));

        // k — ascender stem + arm/leg
        g['k'] = G(0.46f,
            Line(V(0.06f, A), V(0.06f, B)),
            Join(Line(V(0.38f, X), V(0.08f, 0.58f)),
                 Line(V(0.08f, 0.58f), V(0.40f, B))));

        // l — plain ascender
        g['l'] = G(0.22f,
            Line(V(0.11f, A), V(0.11f, B)));

        // m — stem + two arches
        g['m'] = G(0.72f,
            Line(V(0.06f, X), V(0.06f, B)),
            Join(Arc(0.20f, 0.50f, 0.14f, 0.15f, 180f, 360f),
                 Line(V(0.34f, 0.50f), V(0.34f, B))),
            Join(Arc(0.48f, 0.50f, 0.14f, 0.15f, 180f, 360f),
                 Line(V(0.62f, 0.50f), V(0.62f, B))));

        // n — stem + one arch
        g['n'] = G(0.48f,
            Line(V(0.06f, X), V(0.06f, B)),
            Join(Arc(0.22f, 0.52f, 0.16f, 0.17f, 180f, 360f),
                 Line(V(0.38f, 0.52f), V(0.38f, B))));

        // o — closed bowl
        g['o'] = G(0.46f,
            Arc(0.22f, M, 0.17f, 0.20f, 0f, 360f));

        // p — descender stem + bowl
        g['p'] = G(0.48f,
            Line(V(0.06f, X), V(0.06f, D)),
            Arc(0.23f, M, 0.17f, 0.20f, 0f, 360f));

        // q — bowl + descender stem
        g['q'] = G(0.48f,
            Arc(0.20f, M, 0.17f, 0.20f, 0f, 360f),
            Line(V(0.37f, X), V(0.37f, D)));

        // r — stem + small shoulder
        g['r'] = G(0.34f,
            Line(V(0.06f, X), V(0.06f, B)),
            Arc(0.18f, 0.52f, 0.12f, 0.17f, 180f, 295f));

        // s — two half-arcs meeting at the waist
        g['s'] = G(0.38f,
            Join(Arc(0.19f, 0.45f, 0.13f, 0.10f, 0f, -270f),
                 Arc(0.19f, 0.65f, 0.13f, 0.10f, -90f, 180f)));

        // t — tall stem + crossbar
        g['t'] = G(0.32f,
            Line(V(0.16f, 0.15f), V(0.16f, B)),
            Line(V(0.02f, X), V(0.30f, X)));

        // u — down, round the bottom, up, then the tail
        g['u'] = G(0.48f,
            Join(Line(V(0.06f, X), V(0.06f, 0.58f)),
                 Arc(0.22f, 0.58f, 0.16f, 0.17f, 180f, 0f),
                 Line(V(0.38f, 0.58f), V(0.38f, B))));

        // v
        g['v'] = G(0.42f,
            Join(Line(V(0.04f, X), V(0.21f, B)),
                 Line(V(0.21f, B), V(0.38f, X))));

        // w
        g['w'] = G(0.64f,
            Join(Line(V(0.03f, X), V(0.17f, B)),
                 Line(V(0.17f, B), V(0.30f, 0.45f)),
                 Line(V(0.30f, 0.45f), V(0.43f, B)),
                 Line(V(0.43f, B), V(0.57f, X))));

        // x
        g['x'] = G(0.42f,
            Line(V(0.04f, X), V(0.36f, B)),
            Line(V(0.36f, X), V(0.04f, B)));

        // y — short arm + long descending arm with a tail
        g['y'] = G(0.44f,
            Line(V(0.04f, X), V(0.23f, 0.70f)),
            Join(Line(V(0.40f, X), V(0.16f, 0.90f)),
                 Line(V(0.16f, 0.90f), V(0.05f, 0.96f))));

        // z
        g['z'] = G(0.42f,
            Join(Line(V(0.04f, X), V(0.38f, X)),
                 Line(V(0.38f, X), V(0.04f, B)),
                 Line(V(0.04f, B), V(0.38f, B))));

        return g;
    }
}
