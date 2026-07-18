# EMILIA Benchmarks — run everything from the Quest 3

One APK that measures both AI/ML surfaces on-device, in a single launch:

1. **ML Kit handwriting recognition latency** (on-device, no server) — `MLKitBenchmark`
2. **AI-server response time** — transcribe / sentiment / chat over WiFi to the FastAPI
   backend at `192.168.31.69:8000` — `AIServerBenchmark`

`BenchmarkRunner` runs suite 1 first (needs no server), then suite 2. Results go to
`adb logcat` and to CSV files you pull to the PC.

## Why this is safe against the "Unity + AI can't run together" constraint

Running the built **APK on the Quest is not running the Unity Editor**. So the AI server
can be up on the PC at the same time as the app runs on the headset — no GPU conflict.
The Editor is only needed once, to build the APK (do that with the AI server off).

This also makes the measurement the most faithful to the thesis: it's the actual Meta
Quest 3 hitting the actual backend over WiFi.

## Files

- `AIServerBenchmark.cs` — drives the real `APITranscribeService` / `APISentimentService` /
  `APIChatService`, 30 trials + 2 warmups each; logs `AISRV_BENCH|`, writes
  `aiserver_bench_<ts>.csv`.
- `BenchmarkRunner.cs` — one entry point; sequences both suites; logs `BENCH|`.
- `MLKitBenchmark.cs` (in `Assets/Scripts/Handwriting/`) — the ML Kit suite; logs
  `MLKIT_BENCH|`, writes `mlkit_bench_<ts>.csv`.
- Bundled sample audio: `Assets/Resources/sample_audio.bytes` (Indonesian clip, same one
  the Python harness used).

## One-time Editor setup (AI server OFF)

1. Create a scene, e.g. `Assets/Scenes/use/benchmark.unity`. Add **one** GameObject
   (`BenchmarkManager`) with these components:
   - `DigitalInkBridge`  (set its `Language Tag` to your journaling language)
   - `MLKitBenchmark`     (set its `Language Tag` to match; leave `Run On Start` — the
     runner turns it off automatically)
   - `AIServerBenchmark`
   - `BenchmarkRunner`  (`Run On Start` = true)

   > **Custom server IP?** The default is `192.168.31.69:8000`. To use a different address,
   > also add `APITranscribeService`, `APISentimentService`, `APIChatService` to the same
   > GameObject and set their `baseUrl` in the Inspector — `AIServerBenchmark` will reuse
   > those instead of creating defaults.

2. `File → Build Settings → Android`. Add `use/benchmark.unity` and drag it to the **top**
   (index 0). Build the APK. (Remove it again for a normal build.)

### Config knobs

| Component | Field | Default | Meaning |
|---|---|---|---|
| BenchmarkRunner | Run Mlkit / Run Ai Server | true | Toggle either suite |
| MLKitBenchmark | Trials / Warmup | 30 / 1 | ML Kit iterations |
| AIServerBenchmark | Trials / Warmup | 30 / 2 | Server calls per service |
| AIServerBenchmark | Services | chat,sentiment,transcribe | Which endpoints (chat first on purpose) |

## Run order

1. **Build** the APK in Unity with the **AI server off**.
2. **Close the Unity Editor.**
3. **Start the AI server** on the PC (WSL):
   ```bash
   conda activate emilia
   cd /mnt/d/Malvin_TA/AI_MALVIN
   uvicorn app:app --host 0.0.0.0 --port 8000
   ```
   (Skip this only if you want the ML Kit suite alone — the AI suite will preflight and
   skip.)
4. Connect the Quest via **USB-3**; `adb devices` shows it. **Keep the headset awake for the
   entire run — see the section below; this is the #1 cause of a truncated run.**
5. **Install & launch:**
   ```
   adb install -r path\to\EMILIA-VR.apk
   adb shell monkey -p com.MiLeonStudio.EMILIAVR -c android.intent.category.LAUNCHER 1
   ```
   Both suites run automatically.

## Keep the headset awake (IMPORTANT)

An automated benchmark has no head/controller movement, so the Quest hits its **idle-sleep
timeout** and Android **pauses the app** — freezing the benchmark mid-run (this is what
truncated the earlier run: it stopped at sentiment trial 5 and the chat batch never ran).

Two layers of protection:
- **In-app (automatic):** the benchmark now sets `Screen.sleepTimeout = NeverSleep` and
  `Application.runInBackground = true` at start. This alone usually prevents the idle sleep.
- **Belt-and-suspenders (recommended):** **cover/tape the proximity sensor** — the small
  sensor inside the headset, top-center near the nose bridge. With it covered the Quest
  believes it is being worn and will not sleep. (The `prox_close` adb broadcast does **not**
  reliably persist, so don't rely on it.)

You do not need to wear it — a piece of tape or a folded sticky note over the sensor is
enough. Alternatively, just leave the headset on your head while it runs.

## Monitor on the PC

```
adb logcat | Select-String "BENCH|"
```
(PowerShell.) This catches `BENCH|`, `MLKIT_BENCH|`, and `AISRV_BENCH|`. You'll see
per-trial latencies, then `SUMMARY` blocks (mean/SD/min/max/median), then CSV paths.

With plain `adb`:
```
adb logcat -s Unity | findstr BENCH
```

## Collect results

```
adb pull /sdcard/Android/data/com.MiLeonStudio.EMILIAVR/files/
```
Grab `mlkit_bench_<ts>.csv` and `aiserver_bench_<ts>.csv`.

> **The CSV is the source of truth, not logcat.** logcat is a fixed-size ring buffer, so a
> saved logcat snippet will look truncated even on a perfectly healthy run (early lines roll
> off). The CSVs are written **incrementally** — each trial is flushed to disk as it happens —
> so even if a run is interrupted, every completed trial is already saved. Always read results
> from the pulled CSV.

## Reading the AI-server numbers

- Latency is the **full client round-trip** (send → full response received), timed with
  `Stopwatch` in the app — the same quantity the Python harness measures, now from the
  actual Quest.
- **Cold-start** (first call per service, while Ollama/Whisper load) is logged and stored
  separately in the CSV; it's excluded from the mean.
- Warm chat/sentiment calls should be well under the 30 s client timeout. A cold first
  call can exceed it — that's why warmups are discarded.

## Thesis note

Report the Part-C (Quest) numbers as the **primary** response-time table — it's literally
"dari perangkat Meta Quest 3 terhadap server backend pada jaringan lokal." Use the Python
harness results as a cross-check. Report ML Kit separately as on-device recognition latency.
