using System.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Single entry point that runs BOTH benchmark suites from one Quest launch:
///   1. ML Kit handwriting-recognition latency (on-device, no server) — runs first.
///   2. AI-server response time (transcribe / sentiment / chat over WiFi) — runs second.
///
/// Put this plus MLKitBenchmark + AIServerBenchmark (+ DigitalInkBridge) on one
/// GameObject. This component drives them, so their own auto-run is disabled here.
///
/// Order matters: ML Kit needs no server, so it completes even if you forgot to start
/// the AI server. The AI suite preflights and skips cleanly if the server is unreachable.
///
/// Filter all output on the PC with:  adb logcat | Select-String "BENCH|"
/// (catches BENCH, MLKIT_BENCH and AISRV_BENCH). See BENCHMARK_README.md.
/// </summary>
public class BenchmarkRunner : MonoBehaviour
{
    [Header("Run on launch")]
    [SerializeField] private bool runOnStart = true;

    [Header("Suites")]
    [SerializeField] private bool runMlkit = true;
    [SerializeField] private bool runAiServer = true;

    [Header("References (auto-found on this GameObject if empty)")]
    [SerializeField] private MLKitBenchmark mlkit;
    [SerializeField] private AIServerBenchmark aiServer;

    private void Awake()
    {
        if (mlkit == null) mlkit = GetComponent<MLKitBenchmark>();
        if (aiServer == null) aiServer = GetComponent<AIServerBenchmark>();

        // We sequence the suites; make sure they don't also auto-run themselves.
        if (mlkit != null) mlkit.RunOnStart = false;
        if (aiServer != null) aiServer.RunOnStart = false;
    }

    private void Start()
    {
        if (runOnStart) StartCoroutine(RunAll());
    }

    /// <summary>Public entry point (e.g. from a UI button) if runOnStart is off.</summary>
    public void RunNow() => StartCoroutine(RunAll());

    private IEnumerator RunAll()
    {
        Debug.Log("BENCH|=== Benchmark suite starting ===");

        if (runMlkit)
        {
            if (mlkit != null)
            {
                Debug.Log("BENCH|Running ML Kit handwriting benchmark...");
                yield return mlkit.RunSuite();
            }
            else Debug.Log("BENCH|WARN: MLKitBenchmark not found; skipping ML Kit suite.");
        }

        if (runAiServer)
        {
            if (aiServer != null)
            {
                Debug.Log("BENCH|Running AI-server benchmark...");
                yield return aiServer.RunSuite();
            }
            else Debug.Log("BENCH|WARN: AIServerBenchmark not found; skipping AI-server suite.");
        }

        Debug.Log("BENCH|=== All benchmarks done. CSVs in " + Application.persistentDataPath +
                  " (mlkit_bench_*.csv, aiserver_bench_*.csv). ===");
    }
}
