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
/// In-APK benchmark for the EMILIA AI server (FastAPI @ 192.168.31.69:8000).
/// Runs the real client services (APITranscribeService / APISentimentService /
/// APIChatService) from the Quest over WiFi and times each round-trip over N trials.
///
/// This is the most faithful version of the thesis response-time measurement — the
/// actual headset hitting the actual backend. The AI server must be running on the PC
/// (running the APK on the Quest does NOT conflict with the server, since the Unity
/// Editor is not involved).
///
/// Results: logcat lines prefixed "AISRV_BENCH|" + a CSV under persistentDataPath.
/// See BENCHMARK_README.md.
/// </summary>
public class AIServerBenchmark : MonoBehaviour
{
    [Header("Run")]
    [Tooltip("Start automatically on Start(). Leave OFF when a BenchmarkRunner drives this.")]
    [SerializeField] private bool runOnStart = false;
    [Tooltip("Measured trials per service.")]
    [SerializeField] private int trials = 30;
    [Tooltip("Discarded warmup calls per service (absorbs Ollama/Whisper cold load).")]
    [SerializeField] private int warmup = 2;
    [Tooltip("Which services to test, comma-separated. Chat is first so the slowest/most-wanted batch is captured earliest.")]
    [SerializeField] private string services = "chat,sentiment,transcribe";
    [Tooltip("Idle delay between calls (seconds).")]
    [SerializeField] private float interCallDelay = 0.1f;

    public bool RunOnStart { get => runOnStart; set => runOnStart = value; }

    // Reused client services (same defaults: baseUrl http://192.168.31.69:8000).
    private APITranscribeService _transcribe;
    private APISentimentService  _sentiment;
    private APIChatService        _chat;

    private byte[] _audioBytes;
    private bool _running;

    // ── Representative inputs (identical to the Python harness for comparability) ──
    private const string Username = "TestUser";

    private const string JournalText =
        "Hari ini rasanya cukup berat karena tugas akhir yang tidak kunjung selesai " +
        "membuat pikiranku penuh. Aku bangun pagi dengan niat menyelesaikan satu bab, " +
        "tetapi terus terganggu oleh rasa cemas dan lelah. Meski begitu, sore tadi aku " +
        "menyempatkan diri berjalan keluar sebentar dan menghirup udara segar, dan itu " +
        "sedikit menenangkan. Aku sadar bahwa aku perlu lebih sabar pada diriku sendiri " +
        "dan memberi ruang untuk beristirahat, supaya besok bisa kembali fokus dengan " +
        "kepala yang lebih jernih dan hati yang lebih tenang.";

    private const string ChatQuestion =
        "Akhir-akhir ini aku sering merasa cemas dan sulit tidur karena banyak pikiran. " +
        "Apa yang sebaiknya aku lakukan agar merasa lebih tenang?";

    // ==================================================================
    private void Awake()
    {
        // Honor an inspector-configured service (custom baseUrl) if present; else create.
        _transcribe = GetComponent<APITranscribeService>() ?? gameObject.AddComponent<APITranscribeService>();
        _sentiment  = GetComponent<APISentimentService>()  ?? gameObject.AddComponent<APISentimentService>();
        _chat       = GetComponent<APIChatService>()        ?? gameObject.AddComponent<APIChatService>();
    }

    private void Start()
    {
        if (runOnStart) StartCoroutine(RunSuite());
    }

    /// <summary>Public entry point (BenchmarkRunner, or a UI button).</summary>
    public IEnumerator RunSuite()
    {
        if (_running) yield break;
        _running = true;

        // Defensive (BenchmarkRunner also sets these): keep the headset awake so the
        // idle-sleep timeout can't pause the app and freeze this coroutine mid-run.
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.runInBackground = true;

        var svc = new List<string>();
        foreach (var s in services.Split(','))
        {
            var t = s.Trim().ToLowerInvariant();
            if (t == "transcribe" || t == "sentiment" || t == "chat") svc.Add(t);
        }

        // Load bundled sample audio only if transcribe is requested.
        if (svc.Contains("transcribe"))
        {
            var ta = Resources.Load<TextAsset>("sample_audio");
            if (ta == null || ta.bytes == null || ta.bytes.Length == 0)
            {
                Log("WARN: Resources/sample_audio(.bytes) missing or empty; skipping transcribe.");
                svc.Remove("transcribe");
            }
            else
            {
                _audioBytes = ta.bytes;
                Log($"Loaded sample audio: {_audioBytes.Length} bytes.");
            }
        }

        Log($"Start. services=[{string.Join(",", svc)}] trials={trials} warmup={warmup}");

        // ── Preflight (also primes qwen3): one chat call, timed. ──
        // Connection-refused returns fast (<3s); a cold-but-reachable server takes longer
        // (LLM load) or succeeds — so use call duration to tell "down" from "slow".
        double preMs = 0; bool preOk = false; string preErr = null;
        yield return RunOneCall("chat", r => { preMs = r.latencyMs; preOk = r.ok; preErr = r.err; });
        if (!preOk && preMs < 3000)
        {
            Log($"PREFLIGHT FAIL: server unreachable ({preErr}); skipping AI suite. " +
                "Is `uvicorn app:app --host 0.0.0.0 --port 8000` running and reachable from the Quest's WiFi?");
            _running = false;
            yield break;
        }
        Log(preOk ? $"Preflight OK ({preMs / 1000.0:0.000}s)."
                  : $"Preflight: server reachable but first call failed/slow ({preMs / 1000.0:0.000}s, {preErr}) — proceeding.");

        // Open the CSV up front and flush every row as it happens, so a run that is
        // interrupted (e.g. the headset sleeps) still leaves all completed trials on disk.
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(Application.persistentDataPath, $"aiserver_bench_{ts}.csv");
        StreamWriter writer = null;
        try
        {
            writer = new StreamWriter(path, false, new UTF8Encoding(false));
            WriteLine(writer, "service,phase,trial,latency_ms,ok,error");
            Log($"CSV (incremental): {path}");
        }
        catch (Exception e) { Log($"CSV open failed: {e.Message}"); writer = null; }

        var summaries = new List<(string name, Stats st, int fails, double coldMs)>();

        foreach (var name in svc)
        {
            Log($"=== {name} ===");
            var lat = new List<double>();
            int fails = 0;
            double coldMs = -1;
            int total = warmup + trials;

            for (int i = 0; i < total; i++)
            {
                bool isWarmup = i < warmup;
                int trialNo = i - warmup + 1;

                CallResult res = default;
                yield return RunOneCall(name, r => res = r);

                if (i == 0) coldMs = res.latencyMs;

                string tag = isWarmup ? "warmup" : $"trial {trialNo}";
                Log($"{name} [{tag}] {res.latencyMs / 1000.0:0.000}s {(res.ok ? "OK" : "FAIL: " + res.err)}");

                WriteLine(writer, string.Join(",", new[]
                {
                    Csv(name),
                    isWarmup ? "warmup" : "measured",
                    isWarmup ? "" : trialNo.ToString(CultureInfo.InvariantCulture),
                    res.latencyMs.ToString("F2", CultureInfo.InvariantCulture),
                    res.ok ? "1" : "0",
                    Csv(res.err)
                }));

                if (!isWarmup)
                {
                    if (res.ok) lat.Add(res.latencyMs);
                    else fails++;
                }

                if (interCallDelay > 0f) yield return new WaitForSeconds(interCallDelay);
            }

            summaries.Add((name, Stats.Of(lat), fails, coldMs));
        }

        // ── Summary ───────────────────────────────────────────────
        WriteLine(writer, "");
        WriteLine(writer, "service,n,mean_ms,sd_ms,min_ms,max_ms,median_ms,cold_start_ms,failures");
        Log("================ SUMMARY (seconds) ================");
        foreach (var (name, st, fails, coldMs) in summaries)
        {
            Log($"{name}: n={st.N} mean={st.Mean / 1000.0:0.000} sd={st.Sd / 1000.0:0.000} " +
                $"min={st.Min / 1000.0:0.000} max={st.Max / 1000.0:0.000} median={st.Median / 1000.0:0.000} " +
                $"cold={(coldMs >= 0 ? (coldMs / 1000.0).ToString("0.000") : "-")} fails={fails}");
            WriteLine(writer, string.Join(",", new[]
            {
                Csv(name), st.N.ToString(),
                st.Mean.ToString("F2", CultureInfo.InvariantCulture),
                st.Sd.ToString("F2", CultureInfo.InvariantCulture),
                st.Min.ToString("F2", CultureInfo.InvariantCulture),
                st.Max.ToString("F2", CultureInfo.InvariantCulture),
                st.Median.ToString("F2", CultureInfo.InvariantCulture),
                coldMs >= 0 ? coldMs.ToString("F2", CultureInfo.InvariantCulture) : "",
                fails.ToString()
            }));
        }
        Log("==================================================");

        try { writer?.Dispose(); } catch { }
        if (writer != null) Log($"CSV written: {path}");

        Log("DONE.");
        _running = false;
    }

    /// <summary>Write one CSV line and flush immediately (durable against interruption).</summary>
    private static void WriteLine(StreamWriter w, string line)
    {
        if (w == null) return;
        try { w.WriteLine(line); w.Flush(); } catch { }
    }

    private struct CallResult { public double latencyMs; public bool ok; public string err; }

    /// <summary>Invoke one service call and time the full client round-trip.</summary>
    private IEnumerator RunOneCall(string service, Action<CallResult> done)
    {
        bool ok = false;
        string err = null;
        var sw = Stopwatch.StartNew();

        switch (service)
        {
            case "transcribe":
                yield return _transcribe.Transcribe(
                    _audioBytes, "sample_audio.wav", "audio/wav",
                    _ => ok = true,
                    e => err = e);
                break;

            case "sentiment":
                yield return _sentiment.AnalyzeJournal(
                    JournalText,
                    _ => ok = true,
                    e => err = e);
                break;

            case "chat":
                yield return _chat.SendPrompt(
                    Username, ChatQuestion,
                    _ => ok = true,
                    e => err = e);
                break;
        }

        sw.Stop();
        done(new CallResult { latencyMs = sw.Elapsed.TotalMilliseconds, ok = ok, err = err });
    }

    // ==================================================================
    private static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static void Log(string msg) => Debug.Log("AISRV_BENCH|" + msg);

    // ── Stats helper (sample SD; latencies in ms) ──
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
                st.Sd = Math.Sqrt(ss / (st.N - 1));
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
