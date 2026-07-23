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
| BenchmarkRunner | Run Mlkit | true | Run the handwriting suite |
| BenchmarkRunner | Run Ai Server | **false** in `use/benchmark.unity` | Tick it (and rebuild) when the backend PC is available |
| MLKitBenchmark | Trials / Warmup | 30 / 1 | ML Kit iterations |
| AIServerBenchmark | Trials / Warmup | 30 / 2 | Server calls per service |
| AIServerBenchmark | Services | `transcribe,sentiment,chat` in the scene | Which endpoints, in order |

> **Run order caveat.** The C# default is `chat,sentiment,transcribe` — chat first on purpose, so the
> slowest and most-wanted batch lands on disk earliest. The scene overrides this to
> `transcribe,sentiment,chat`. If there's any chance of the run being interrupted, put `chat` first
> in the Inspector, and record whichever order you actually used.

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

An automated benchmark has no head/controller movement, so the Quest can sleep mid-run and
Android **pauses the app**, freezing the benchmark (this is what truncated the earlier run:
it stopped at sentiment trial 5 and the chat batch never ran).

The Quest sleeps for **two independent reasons**; know which one you're fighting:
- **Idle/inactivity timeout** (Android PowerManager). This is what the earlier log showed
  (`PowerGroup ... reason=timeout`). **The code now handles this automatically** —
  `Screen.sleepTimeout = NeverSleep` + `Application.runInBackground = true` are set at run
  start, which is exactly the flag that suppresses the idle timeout. So in most cases you
  need to do nothing.
- **Proximity sensor** (headset removed → standby). Handled by the OS/VR runtime and **cannot**
  be overridden by the app. Only relevant if the headset is off your face with the sensor
  uncovered.

Low-maintenance ladder (do the least that works — tape is the last resort):
1. **Nothing** — rely on the in-app anti-sleep above. Try a run first; it likely just works.
2. **One-time, persistent, no tape:** Quest **Settings → Power → "Auto Sleep Headset" → the
   longest option**. Raises/removes the idle timeout system-wide for every run.
3. **Simplest human option:** keep the headset on your head, or rest it so the sensor stays
   covered, for the few-minute run.
4. **Last resort:** tape / a folded sticky note over the proximity sensor (small sensor inside,
   top-center near the nose bridge). Only needed if a run still logs `APP_CMD_PAUSE`.

Whichever you pick, the **incremental CSV** means even a run that does nap keeps every trial
completed up to that point — you never lose the whole run again.

## Monitor on the PC

```powershell
adb logcat | Select-String "BENCH\|"
```
(PowerShell.) This catches `BENCH|`, `MLKIT_BENCH|`, and `AISRV_BENCH|`. You'll see
per-trial latencies, then `SUMMARY` blocks (mean/SD/min/max/median), then CSV paths.

**Escape the pipe.** `Select-String` takes a regex, so an unescaped `|` reads as alternation
("match `BENCH` or an empty string") and prints every line of logcat.

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

## Test inputs (thesis reference)

Every number in the results tables comes from these fixed inputs. They are identical to the ones the
Python harness used, so the two runs are directly comparable. **Source of truth for the two texts is
`AIServerBenchmark.cs` (`JournalText`, `ChatQuestion`, `Username`)** — if you edit them there, update
this section too.

`username` is the literal string `TestUser` for every chat call.

### Chat message → `POST /chat`

21 words, 135 characters:

> Akhir-akhir ini aku sering merasa cemas dan sulit tidur karena banyak pikiran. Apa yang sebaiknya
> aku lakukan agar merasa lebih tenang?

### Journal text → `POST /sentiment`

79 words, 516 characters, 4 sentences:

> Hari ini rasanya cukup berat karena tugas akhir yang tidak kunjung selesai membuat pikiranku penuh.
> Aku bangun pagi dengan niat menyelesaikan satu bab, tetapi terus terganggu oleh rasa cemas dan
> lelah. Meski begitu, sore tadi aku menyempatkan diri berjalan keluar sebentar dan menghirup udara
> segar, dan itu sedikit menenangkan. Aku sadar bahwa aku perlu lebih sabar pada diriku sendiri dan
> memberi ruang untuk beristirahat, supaya besok bisa kembali fokus dengan kepala yang lebih jernih
> dan hati yang lebih tenang.

### Voice message → `POST /transcribe`

`Assets/Resources/sample_audio.bytes`, uploaded as `sample_audio.wav` with MIME `audio/wav`.
Specs read from the file's WAV header:

| Property | Value |
|---|---|
| Format | WAV, PCM uncompressed (`format = 1`) |
| Duration | 5.03 s |
| Sample rate | 44 100 Hz |
| Channels | 1 (mono) |
| Bit depth | 16-bit |
| Payload size | 443 668 bytes (~433 KB) |

> **TODO — transcript pending.** What the clip actually *says* is not recorded anywhere and has not
> been verified. Play the file, write the spoken Indonesian sentence here, and only then quote it in
> the thesis. Do not paste this section into the book while this TODO is still present.

### Request shapes

| Service | Endpoint | Multipart fields | Client timeout |
|---|---|---|---|
| transcribe | `POST /transcribe` | `audio` (file) | 99999 s (effectively none) |
| sentiment | `POST /sentiment` | `journal` (string) | 30 s |
| chat | `POST /chat` | `username`, `question` — the optional `audio` field is **not** sent | 30 s |

Each service runs 30 measured trials + 2 discarded warmups.

### ML Kit inputs

The handwriting suite's inputs are the words `aku`, `tenang`, and `hari ini tenang`, rendered as
letter strokes with a known ground truth — see `Assets/Scripts/Handwriting/MLKitBenchmark_README.md`.

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

### Scope: the AI suite measures response time ONLY

`RunOneCall` sets `ok = true` on any successful HTTP response and **discards the returned text**
(`_ => ok = true`) — for all three services. Nothing is compared against a ground truth, so the AI
suite yields **waktu respons** and nothing else. Do not describe it as measuring transcription,
sentiment, or chat *accuracy*; the data does not support that claim.

This is the deliberate scope, not an oversight. The ML Kit suite is the one that reports accuracy
(exact-match) and CER against a known ground truth — keep the two clearly separated in the write-up.
