using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// On-device latency benchmark for ML Kit Digital Ink Recognition.
///
/// Feeds synthetic (or file-loaded) pixel-space strokes directly to
/// <see cref="DigitalInkBridge"/> — bypassing hand tracking and WhiteboardPen —
/// and times each Recognize() call over N trials. Reports mean/SD/min/max/median
/// to logcat (lines prefixed "MLKIT_BENCH|") and to a CSV under
/// Application.persistentDataPath, pullable via `adb pull`.
///
/// ML Kit only runs on an Android build (never in the Editor), so this must run
/// on the Quest. It needs NO AI server — recognition is fully on-device.
///
/// Setup: put this and a <see cref="DigitalInkBridge"/> on one GameObject in a
/// scene, build to the Quest, launch. With runOnStart it benchmarks automatically.
/// See MLKitBenchmark_README.md.
/// </summary>
public class MLKitBenchmark : MonoBehaviour
{
    [Header("Bridge")]
    [Tooltip("Digital ink bridge to drive. Auto-found via DigitalInkBridge.Instance if left empty.")]
    [SerializeField] private DigitalInkBridge bridge;

    [Header("Run")]
    [Tooltip("Start benchmarking automatically once the model is ready. Leave OFF when a BenchmarkRunner drives this.")]
    [SerializeField] private bool runOnStart = true;

    /// <summary>Exposed so a BenchmarkRunner can disable auto-run and sequence suites itself.</summary>
    public bool RunOnStart { get => runOnStart; set => runOnStart = value; }
    [Tooltip("Measured trials per sample.")]
    [SerializeField] private int trials = 30;
    [Tooltip("Discarded warmup trials per sample (absorbs first-call init cost).")]
    [SerializeField] private int warmup = 1;

    [Header("Recognizer")]
    [Tooltip("BCP-47 language tag; should match the journaling language and the DigitalInkBridge model.")]
    [SerializeField] private string languageTag = "en-US";

    [Header("Samples")]
    [Tooltip("GroundTruthWords: render real words as letter strokes (accuracy IS measured).\n" +
             "Synthetic: meaningless zig-zags, latency only (accuracy = n/a).\n" +
             "A pushed mlkit_samples.json always overrides this.")]
    [SerializeField] private SampleSource sampleSource = SampleSource.GroundTruthWords;
    [Tooltip("Words rendered for the GroundTruthWords source. Edit freely — no code change needed.")]
    [SerializeField] private WordSample[] words =
    {
        new WordSample { label = "word-short",  text = "aku" },
        new WordSample { label = "word-medium", text = "tenang" },
        new WordSample { label = "word-long",   text = "hari ini tenang" },
    };

    [Header("Stroke rendering (GroundTruthWords)")]
    [Tooltip("Ascender-to-baseline height of a tall letter, in ML Kit px. ~40 px = a 4 cm finger-written letter.")]
    [SerializeField] private float letterHeightPx = 40f;
    [Tooltip("Floor for the shrink-to-one-line fit. Below this a long phrase wraps to a second line " +
             "instead of shrinking into implausibly tiny handwriting.")]
    [SerializeField] private float minLetterHeightPx = 22f;
    [Tooltip("Extra gap between letters (px).")]
    [SerializeField] private float letterSpacingPx = 4f;
    [Tooltip("Gap between words (px).")]
    [SerializeField] private float wordSpacingPx = 16f;
    [Tooltip("Hand-wobble amplitude (px). 0 = perfect vectors, which is unrealistically clean.")]
    [SerializeField] private float jitterPx = 0.8f;
    [Tooltip("Seed for the wobble. Fixed => every trial of a sample feeds identical ink, so latency " +
             "variance measures the recognizer rather than the input.")]
    [SerializeField] private int strokeSeed = 12345;

    [Header("Timeouts (seconds)")]
    [Tooltip("How long to wait for the ML Kit model to become ready before aborting.")]
    [SerializeField] private float modelReadyTimeout = 60f;
    [Tooltip("How long to wait for a single recognition to complete before counting it as a failure.")]
    [SerializeField] private float recognizeTimeout = 15f;
    [Tooltip("Idle delay between trials, lets the frame-polled bridge settle.")]
    [SerializeField] private float interTrialDelay = 0.05f;

    /// <summary>Where the benchmark's ink comes from.</summary>
    public enum SampleSource
    {
        /// <summary>Real words drawn as letter strokes by MLKitStrokeFont — accuracy is measured.</summary>
        GroundTruthWords,
        /// <summary>Meaningless zig-zags — valid latency, accuracy n/a.</summary>
        Synthetic,
    }

    /// <summary>One inspector-editable word to render and score.</summary>
    [Serializable]
    public class WordSample
    {
        [Tooltip("CSV/log label for this sample.")]
        public string label;
        [Tooltip("The word(s) to render AND the ground truth to score against.")]
        public string text;
    }

    // ── One benchmark sample: ground-truth text + strokes in 300x200 px space ──
    private class Sample
    {
        public string label;
        public string groundTruth;                 // "" => accuracy is N/A (synthetic)
        public List<List<Vector2>> strokes;        // each stroke: ordered pixel points
    }

    // Serialisable shapes for optional StreamingAssets/mlkit_samples.json loading.
    [Serializable] private class JsonPoint { public float x; public float y; }
    [Serializable] private class JsonStroke { public List<JsonPoint> points; }
    [Serializable] private class JsonSample { public string label; public string groundTruth; public List<JsonStroke> strokes; }
    [Serializable] private class JsonSampleFile { public List<JsonSample> samples; }

    // ML Kit logical canvas — must match WhiteboardPen (300x200).
    private const float AREA_W = 300f;
    private const float AREA_H = 200f;

    private volatile string _lastResult;
    private bool _running;

    // ==================================================================
    private void OnEnable()
    {
        if (bridge == null) bridge = DigitalInkBridge.Instance;
        if (bridge != null) bridge.OnTextRecognized += HandleTextRecognized;
    }

    private void OnDisable()
    {
        if (bridge != null) bridge.OnTextRecognized -= HandleTextRecognized;
    }

    private void Start()
    {
        if (runOnStart) StartCoroutine(RunSuite());
    }

    /// <summary>Public entry point (e.g. to trigger from a button) if runOnStart is off.</summary>
    public void RunNow()
    {
        if (!_running) StartCoroutine(RunSuite());
    }

    private void HandleTextRecognized(string text) => _lastResult = text;

    // ==================================================================
    /// <summary>Runs the full ML Kit latency benchmark. Yield on this to sequence suites.</summary>
    public IEnumerator RunSuite()
    {
        _running = true;

        // Defensive (BenchmarkRunner also sets these): keep the headset awake so the
        // idle-sleep timeout can't pause the app and freeze this coroutine mid-run.
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.runInBackground = true;

        if (bridge == null) bridge = DigitalInkBridge.Instance;
        if (bridge == null)
        {
            Log("ABORT: no DigitalInkBridge found in scene.");
            _running = false;
            yield break;
        }

        // Guarantee exactly one subscription even if OnEnable ran before the bridge's
        // Awake set Instance (component Awake/OnEnable order is not guaranteed).
        bridge.OnTextRecognized -= HandleTextRecognized;
        bridge.OnTextRecognized += HandleTextRecognized;

        // Keep the bridge language in sync (informational; the bridge reads this at Start()).
        if (!string.IsNullOrEmpty(languageTag)) bridge.languageTag = languageTag;

        // Wait for the on-device model. In the Editor this never becomes ready.
        Log($"Waiting for ML Kit model (timeout {modelReadyTimeout:0}s)...");
        float t0 = Time.realtimeSinceStartup;
        while (!bridge.IsModelReady)
        {
            if (Time.realtimeSinceStartup - t0 > modelReadyTimeout)
            {
                Log("ABORT: model not ready within timeout. (Editor? ML Kit is Android-only.)");
                _running = false;
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
        }
        Log($"Model ready. trials={trials} warmup={warmup} lang={languageTag} source={sampleSource}");

        List<Sample> samples = LoadSamples();
        Log($"Loaded {samples.Count} sample(s).");

        var perSampleLatencies = new Dictionary<string, List<double>>();
        var perSampleHits = new Dictionary<string, int>();
        var perSampleScored = new Dictionary<string, int>();
        var perSampleCer = new Dictionary<string, List<double>>();

        // Open the CSV up front and flush every row as it happens, so a run that is
        // interrupted (e.g. the headset sleeps) still leaves all completed trials on disk.
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(Application.persistentDataPath, $"mlkit_bench_{ts}.csv");
        StreamWriter writer = null;
        try
        {
            writer = new StreamWriter(path, false, new UTF8Encoding(false));
            WriteLine(writer, "sample,phase,trial,latency_ms,recognized,ground_truth,match,cer");
            Log($"CSV (incremental): {path}");
        }
        catch (Exception e) { Log($"CSV open failed: {e.Message}"); writer = null; }

        var overall = new List<double>();

        foreach (var s in samples)
        {
            perSampleLatencies[s.label] = new List<double>();
            perSampleHits[s.label] = 0;
            perSampleScored[s.label] = 0;
            perSampleCer[s.label] = new List<double>();

            int totalIter = warmup + trials;
            for (int i = 0; i < totalIter; i++)
            {
                bool isWarmup = i < warmup;
                int trialNo = i - warmup + 1;

                double latencyMs = -1;
                bool ok = false;
                yield return RunOneTrial(s, r => { latencyMs = r.latencyMs; ok = r.ok; });

                string recognized = _lastResult ?? "";
                bool scored = !string.IsNullOrEmpty(s.groundTruth);

                // Compare on normalised text: exact-match alone reports a harsh 0% for a
                // one-character slip, so also record the character error rate.
                string normGot = Normalize(recognized);
                string normWant = Normalize(s.groundTruth);
                bool match = scored && normGot == normWant;
                double cer = scored ? CharErrorRate(normWant, normGot) : -1;

                WriteLine(writer, string.Join(",", new[]
                {
                    Csv(s.label),
                    isWarmup ? "warmup" : "measured",
                    isWarmup ? "" : trialNo.ToString(CultureInfo.InvariantCulture),
                    latencyMs.ToString("F2", CultureInfo.InvariantCulture),
                    Csv(recognized),
                    Csv(s.groundTruth),
                    scored ? (match ? "1" : "0") : "",
                    scored ? cer.ToString("F4", CultureInfo.InvariantCulture) : ""
                }));

                string tag = isWarmup ? "warmup" : $"trial {trialNo}";
                Log($"{s.label} [{tag}] {latencyMs / 1000.0:0.000}s " +
                    $"{(ok ? "OK" : "TIMEOUT")} -> \"{recognized}\"");

                if (!isWarmup && ok)
                {
                    perSampleLatencies[s.label].Add(latencyMs);
                    overall.Add(latencyMs);
                    if (scored)
                    {
                        perSampleScored[s.label]++;
                        if (match) perSampleHits[s.label]++;
                        perSampleCer[s.label].Add(cer);
                    }
                }
            }
        }

        // ── Summary ───────────────────────────────────────────────
        WriteLine(writer, "");
        WriteLine(writer, "sample,n,mean_ms,sd_ms,min_ms,max_ms,median_ms,accuracy_pct,mean_cer_pct");
        Log("================ SUMMARY (ms) ================");
        foreach (var s in samples)
        {
            var lat = perSampleLatencies[s.label];
            var st = Stats.Of(lat);
            string acc = perSampleScored[s.label] > 0
                ? (100.0 * perSampleHits[s.label] / perSampleScored[s.label]).ToString("F1", CultureInfo.InvariantCulture)
                : "n/a";
            var cerSt = Stats.Of(perSampleCer[s.label]);
            string cer = cerSt.N > 0
                ? (100.0 * cerSt.Mean).ToString("F1", CultureInfo.InvariantCulture)
                : "n/a";
            Log($"{s.label}: n={st.N} mean={st.Mean:F1} sd={st.Sd:F1} " +
                $"min={st.Min:F1} max={st.Max:F1} median={st.Median:F1} acc={acc}% cer={cer}%");
            WriteLine(writer, string.Join(",", new[]
            {
                Csv(s.label), st.N.ToString(),
                st.Mean.ToString("F2", CultureInfo.InvariantCulture),
                st.Sd.ToString("F2", CultureInfo.InvariantCulture),
                st.Min.ToString("F2", CultureInfo.InvariantCulture),
                st.Max.ToString("F2", CultureInfo.InvariantCulture),
                st.Median.ToString("F2", CultureInfo.InvariantCulture),
                acc, cer
            }));
        }
        var o = Stats.Of(overall);
        Log($"OVERALL: n={o.N} mean={o.Mean:F1} sd={o.Sd:F1} min={o.Min:F1} max={o.Max:F1} median={o.Median:F1}");
        Log("=============================================");

        try { writer?.Dispose(); } catch { }
        if (writer != null) Log($"CSV written: {path}");

        Log("DONE.");
        _running = false;
    }

    private struct TrialResult { public double latencyMs; public bool ok; }

    private IEnumerator RunOneTrial(Sample s, Action<TrialResult> done)
    {
        // Independent trial: clear any leftover ink / language context.
        bridge.ClearInk();
        bridge.ClearPreContext();
        bridge.SetWritingArea(AREA_W, AREA_H);

        // Feed strokes with monotonically increasing timestamps.
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var stroke in s.strokes)
        {
            for (int i = 0; i < stroke.Count; i++)
            {
                float x = Mathf.Clamp(stroke[i].x, 0f, AREA_W);
                float y = Mathf.Clamp(stroke[i].y, 0f, AREA_H);
                if (i == 0) bridge.BeginStroke(x, y, ts);
                else bridge.AddPoint(x, y, ts);
                ts += 8; // ~8 ms per point, realistic pen speed
            }
            bridge.EndStroke();
            ts += 16;
        }

        _lastResult = null;

        var sw = Stopwatch.StartNew();
        bridge.Recognize();

        // Wait for the frame-polled bridge to report completion.
        float start = Time.realtimeSinceStartup;
        bool timedOut = false;
        while (bridge.IsRecognizing)
        {
            if (Time.realtimeSinceStartup - start > recognizeTimeout) { timedOut = true; break; }
            yield return null;
        }
        sw.Stop();

        if (interTrialDelay > 0f) yield return new WaitForSeconds(interTrialDelay);

        done(new TrialResult { latencyMs = sw.Elapsed.TotalMilliseconds, ok = !timedOut });
    }

    // ==================================================================
    // Sample loading: StreamingAssets JSON if present, else synthetic.
    // ==================================================================
    private List<Sample> LoadSamples()
    {
        // Real captured strokes always win — highest-fidelity accuracy source.
        var fromFile = TryLoadJsonSamples();
        if (fromFile != null && fromFile.Count > 0)
        {
            Log("Using mlkit_samples.json (real captured strokes).");
            return fromFile;
        }

        if (sampleSource == SampleSource.GroundTruthWords)
        {
            var rendered = MakeWordSamples();
            if (rendered.Count > 0)
            {
                Log("Using procedurally rendered word strokes — accuracy IS measured, but note " +
                    "these are machine-drawn letterforms, not human handwriting.");
                return rendered;
            }
            Log("WARN: no renderable words configured; falling back to synthetic strokes.");
        }

        Log("Using built-in synthetic strokes (latency is representative; accuracy is N/A).");
        return new List<Sample>
        {
            MakeSynthetic("synthetic-short",  strokeCount: 2, pointsPerStroke: 18),
            MakeSynthetic("synthetic-medium", strokeCount: 4, pointsPerStroke: 24),
            MakeSynthetic("synthetic-long",   strokeCount: 6, pointsPerStroke: 30),
        };
    }

    /// <summary>Render each configured word into strokes via <see cref="MLKitStrokeFont"/>.</summary>
    private List<Sample> MakeWordSamples()
    {
        var result = new List<Sample>();
        if (words == null) return result;

        foreach (var w in words)
        {
            if (w == null || string.IsNullOrWhiteSpace(w.text)) continue;

            if (!MLKitStrokeFont.CanRender(w.text))
            {
                Log($"WARN: '{w.text}' contains characters with no glyph; skipping this sample.");
                continue;
            }

            var opt = new MLKitStrokeFont.LayoutOptions
            {
                areaW = AREA_W,
                areaH = AREA_H,
                letterHeightPx = letterHeightPx,
                minLetterHeightPx = minLetterHeightPx,
                letterSpacingPx = letterSpacingPx,
                wordSpacingPx = wordSpacingPx,
                jitterPx = jitterPx,
                // Vary the seed per sample so the words don't share identical wobble,
                // but keep it fixed across trials of the same sample.
                seed = strokeSeed + result.Count * 977,
            };

            var strokes = MLKitStrokeFont.Layout(w.text, opt);
            if (strokes.Count == 0)
            {
                Log($"WARN: '{w.text}' produced no strokes; skipping.");
                continue;
            }

            int points = 0;
            foreach (var s in strokes) points += s.Count;
            string label = string.IsNullOrWhiteSpace(w.label) ? w.text : w.label;
            Log($"{label}: \"{w.text}\" -> {strokes.Count} strokes, {points} points.");

            result.Add(new Sample { label = label, groundTruth = w.text, strokes = strokes });
        }
        return result;
    }

    private List<Sample> TryLoadJsonSamples()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "mlkit_samples.json");
        try
        {
            // On Android, StreamingAssets lives inside the APK (jar:) and is not a
            // plain file. Also accept a copy in persistentDataPath for easy adb push.
            string persistent = Path.Combine(Application.persistentDataPath, "mlkit_samples.json");
            string json = null;
            if (File.Exists(persistent)) json = File.ReadAllText(persistent);
            else if (File.Exists(path)) json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json)) return null;

            var parsed = JsonUtility.FromJson<JsonSampleFile>(json);
            if (parsed?.samples == null) return null;

            var result = new List<Sample>();
            foreach (var js in parsed.samples)
            {
                var strokes = new List<List<Vector2>>();
                if (js.strokes != null)
                {
                    foreach (var st in js.strokes)
                    {
                        var pts = new List<Vector2>();
                        if (st.points != null)
                            foreach (var p in st.points) pts.Add(new Vector2(p.x, p.y));
                        if (pts.Count > 0) strokes.Add(pts);
                    }
                }
                if (strokes.Count > 0)
                    result.Add(new Sample { label = js.label, groundTruth = js.groundTruth ?? "", strokes = strokes });
            }
            return result;
        }
        catch (Exception e)
        {
            Log($"Sample JSON load failed ({e.Message}); falling back to synthetic.");
            return null;
        }
    }

    /// <summary>
    /// Build a synthetic sample: <paramref name="strokeCount"/> word-like strokes laid out
    /// left-to-right, each a small zig-zag polyline. Sized to fill the 300x200 canvas so the
    /// ink volume is representative of real handwriting for latency purposes.
    /// </summary>
    private Sample MakeSynthetic(string label, int strokeCount, int pointsPerStroke)
    {
        var strokes = new List<List<Vector2>>();
        float margin = 20f;
        float usableW = AREA_W - 2 * margin;
        float slotW = usableW / strokeCount;
        float midY = AREA_H * 0.5f;
        float amp = AREA_H * 0.25f;

        for (int s = 0; s < strokeCount; s++)
        {
            var pts = new List<Vector2>();
            float x0 = margin + s * slotW;
            for (int i = 0; i < pointsPerStroke; i++)
            {
                float u = (float)i / (pointsPerStroke - 1);
                float x = x0 + u * (slotW * 0.8f);
                // A couple of oscillations per stroke to mimic pen movement.
                float y = midY + amp * Mathf.Sin(u * Mathf.PI * 3f + s);
                pts.Add(new Vector2(x, y));
            }
            strokes.Add(pts);
        }
        return new Sample { label = label, groundTruth = "", strokes = strokes };
    }

    // ==================================================================
    // SCORING
    // ==================================================================

    /// <summary>
    /// Canonical form for comparison: lowercase, punctuation stripped, whitespace
    /// collapsed. ML Kit likes to add trailing periods and stray capitals; those are
    /// not recognition errors for our purposes.
    /// </summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (!char.IsLetterOrDigit(c)) continue;   // drop punctuation
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Levenshtein distance / reference length, clamped to [0,1]. 0 = perfect.</summary>
    private static double CharErrorRate(string reference, string got)
    {
        if (string.IsNullOrEmpty(reference)) return string.IsNullOrEmpty(got) ? 0.0 : 1.0;
        int dist = Levenshtein(reference, got);
        return Math.Min(1.0, (double)dist / reference.Length);
    }

    /// <summary>Edit distance, two-row DP (O(min) memory).</summary>
    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var swap = prev; prev = curr; curr = swap;
        }
        return prev[b.Length];
    }

    // ==================================================================
    private static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static void Log(string msg) => Debug.Log("MLKIT_BENCH|" + msg);

    /// <summary>Write one CSV line and flush immediately (durable against interruption).</summary>
    private static void WriteLine(StreamWriter w, string line)
    {
        if (w == null) return;
        try { w.WriteLine(line); w.Flush(); } catch { }
    }

    // ── Small stats helper (sample SD, matches the Python harness meaning) ──
    private struct Stats
    {
        public int N; public double Mean, Sd, Min, Max, Median;

        public static Stats Of(List<double> data)
        {
            var st = new Stats();
            st.N = data?.Count ?? 0;
            if (st.N == 0) return st;

            double sum = 0; st.Min = double.MaxValue; st.Max = double.MinValue;
            foreach (var d in data)
            {
                sum += d;
                if (d < st.Min) st.Min = d;
                if (d > st.Max) st.Max = d;
            }
            st.Mean = sum / st.N;

            if (st.N > 1)
            {
                double ss = 0;
                foreach (var d in data) ss += (d - st.Mean) * (d - st.Mean);
                st.Sd = Math.Sqrt(ss / (st.N - 1)); // sample standard deviation
            }
            else st.Sd = 0;

            var sorted = new List<double>(data);
            sorted.Sort();
            st.Median = (st.N % 2 == 1)
                ? sorted[st.N / 2]
                : 0.5 * (sorted[st.N / 2 - 1] + sorted[st.N / 2]);
            return st;
        }
    }
}
