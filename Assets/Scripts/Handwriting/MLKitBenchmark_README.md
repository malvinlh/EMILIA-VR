# ML Kit Handwriting Recognition — On-Device Latency & Accuracy Benchmark

Measures how long **Google ML Kit Digital Ink Recognition** takes to turn handwriting strokes into text,
on the Meta Quest 3, over N trials — reporting **mean / SD / min / max / median** to `adb logcat` and a CSV,
plus **accuracy** and **CER** against a known ground truth.

This is the on-device counterpart to the server-side HTTP stress test (`AI_MALVIN/stress_test/`). It exists
because handwriting recognition runs **100% on the headset** (bundled ML Kit model, no server call), so it
**cannot** be measured over the network.

## Key facts

- **No AI server involved.** Recognition is on-device → no GPU conflict with the backend. The AI server stays
  off the whole time, and the benchmark scene now ships with `Run Ai Server` **unchecked**.
- **ML Kit only runs on an Android build**, never in the Unity Editor (`DigitalInkBridge` guards the plugin
  with `#if UNITY_ANDROID && !UNITY_EDITOR`). So the benchmark must run on the Quest.
- **Unity Editor is needed only once** — to build the APK. Build it with the AI server off (your hardware
  constraint). After that, running the benchmark needs neither the Editor nor the server.
- You do **not** need to wear the headset — the benchmark auto-runs on launch and writes results you read on
  the PC.

## Files

- `MLKitBenchmark.cs` — the benchmark component. Feeds strokes straight into the existing
  `DigitalInkBridge`, times each `Recognize()` call, scores the result, logs + writes CSV.
- `MLKitStrokeFont.cs` — a tiny stroke font that renders a word as pen-like polylines in the 300×200 ML Kit
  canvas. This is what gives every sample a **ground truth**.
- `Editor/MLKitStrokeFontPreview.cs` — `Tools → EMILIA → Preview ML Kit Stroke Font`. Writes a PNG per word
  so you can confirm legibility **without** building an APK.
- `MLKitBenchmark_README.md` — this run-book.

## Sample sources

`MLKitBenchmark.Sample Source` picks where the ink comes from. Precedence at runtime:

| Priority | Source | Ground truth? | Notes |
|---|---|---|---|
| 1 | `mlkit_samples.json` pushed to the device | yes, if you supply it | Always wins if present. Real captured human strokes — the highest-fidelity accuracy source. |
| 2 | `GroundTruthWords` *(default)* | **yes** | Words from the `Words` list, drawn as letter strokes by `MLKitStrokeFont`. Fully automated. |
| 3 | `Synthetic` | no (`n/a`) | Meaningless zig-zags. Valid latency, unmeasurable accuracy — the original behaviour. |

Default words (all editable in the Inspector, no code change needed):

| Label | Text |
|---|---|
| `word-short` | `aku` |
| `word-medium` | `tenang` |
| `word-long` | `hari ini tenang` |

The glyph table is **lowercase `a`–`z` + space**. Uppercase is folded to lowercase; anything else is skipped
with a warning, and the whole sample is dropped if it can't be rendered.

Letters are drawn at a realistic ink scale: `Letter Height Px` defaults to 40 px, which at `WhiteboardPen`'s
1000 px/m projection is a 4 cm finger-written letter. The wobble (`Jitter Px`) is deterministic — the same
`Stroke Seed` produces byte-identical ink every trial, so latency variance measures the **recognizer**, not
the input.

## One-time setup in the Unity Editor (AI server off)

The scene `Assets/Scenes/use/benchmark.unity` is already wired: one `BenchmarkManager` GameObject carrying
`DigitalInkBridge` + `MLKitBenchmark` + `AIServerBenchmark` + `BenchmarkRunner`, and it is the only
**enabled** scene in Build Settings (so a build launches straight into it).

1. **Check the glyphs first.** `Tools → EMILIA → Preview ML Kit Stroke Font`, then open the PNGs it writes to
   `<project>/MLKitStrokeFontPreview/`. If a word isn't clearly legible, fix it *here* — a 30-second loop
   instead of a 10-minute APK build.
2. **`BenchmarkRunner`**: `Run Mlkit` checked, `Run Ai Server` **unchecked** when the backend PC isn't
   available. (This is now the scene default.)
3. **Language.** `Set Language Tag on BOTH components.` The authoritative one for the downloaded model is
   `DigitalInkBridge.languageTag` (default `en-US`), because the bridge downloads the model in its own
   `Start()`. The `en-US` model reads Indonesian handwriting acceptably, so it is kept as-is.
4. **Build** the APK (`File → Build Settings → Android`), or *Build And Run* with the Quest connected. For a
   normal (non-benchmark) build, re-enable the `3D_*` scenes and disable `benchmark.unity`.

### Config (on the `MLKitBenchmark` component)

| Field | Default | Meaning |
|---|---|---|
| `Run On Start` | true | Benchmark automatically once the model is ready (forced off when `BenchmarkRunner` drives it) |
| `Trials` | 30 | Measured trials per sample |
| `Warmup` | 1 | Discarded warmup trials per sample |
| `Language Tag` | en-US | BCP-47 tag; match the recognizer/journaling language |
| `Model Ready Timeout` | 60 s | Abort if the model never becomes ready |
| `Recognize Timeout` | 15 s | A single recognition longer than this counts as a failure |
| `Sample Source` | GroundTruthWords | See the table above |
| `Words` | aku / tenang / hari ini tenang | Label + text; the text is also the ground truth |
| `Letter Height Px` | 40 | Ascender-to-baseline height in the 300×200 canvas |
| `Letter Spacing Px` | 4 | Gap between letters |
| `Word Spacing Px` | 16 | Gap between words |
| `Jitter Px` | 0.8 | Hand-wobble amplitude; 0 = unrealistically perfect vectors |
| `Stroke Seed` | 12345 | Fixed seed → identical ink every trial |

## Device prep

1. Enable **Developer Mode** on the Quest (Meta Horizon app) and **USB debugging**.
2. Connect the Quest to the PC with a **USB-3** cable. Confirm:
   ```
   adb devices        # your Quest should be listed as "device"
   ```
3. So the headset doesn't sleep on your desk, either cover the proximity sensor or disable it:
   ```
   adb shell am broadcast -a com.oculus.vrpowermanager.prox_close
   ```

## Run & collect results

1. Install and launch:
   ```powershell
   adb install -r path\to\EMILIA-VR.apk
   adb shell monkey -p com.MiLeonStudio.EMILIAVR -c android.intent.category.LAUNCHER 1
   ```
   (Or just put the headset on briefly and open the app once — it auto-runs.)

2. **Watch live** on the PC (Windows PowerShell):
   ```powershell
   adb logcat -c                        # clear stale logs first
   adb logcat | Select-String "BENCH\|"
   ```
   Escape the pipe — `Select-String "BENCH|"` reads `|` as regex alternation and matches every line.
   With plain `adb`:
   ```
   adb logcat -s Unity | findstr BENCH
   ```
   You'll see per-trial latencies with the recognized text, then a `SUMMARY (ms)` block.

3. **Pull the CSV** to the PC:
   ```powershell
   adb pull /sdcard/Android/data/com.MiLeonStudio.EMILIAVR/files/
   ```
   This drops a `files\` folder in the current directory containing `mlkit_bench_<timestamp>.csv`
   (per-trial rows + a summary section). Rows are flushed as they happen, so an interrupted run still leaves
   every completed trial on disk.

A full run is 3 samples × 31 trials ≈ **under 2 minutes**, plus the first-launch model download.

## Reading the numbers

Per-trial rows: `sample,phase,trial,latency_ms,recognized,ground_truth,match,cer`
Summary rows: `sample,n,mean_ms,sd_ms,min_ms,max_ms,median_ms,accuracy_pct,mean_cer_pct`

- **`latency_ms`** — the primary metric: time from `Recognize()` to the result being reported by the
  frame-polled bridge, i.e. what a user waits for after finishing a word.
- **`match`** — 1 if the recognized text equals the ground truth after normalisation (lowercased,
  punctuation stripped, whitespace collapsed). `accuracy_pct` is the share of matching trials.
- **`cer`** — character error rate, Levenshtein distance ÷ ground-truth length, clamped to [0,1]. Report this
  alongside accuracy: exact-match alone shows a harsh 0% for a single-character slip, which understates how
  usable the recogniser actually is.
- `word-short/medium/long` are three ink volumes so you can see how latency scales with stroke count.
- Anything shows `n/a` only when the sample has no ground truth (i.e. `Synthetic`).

## Accuracy — what this figure does and does not mean

⚠️ **`GroundTruthWords` renders procedurally drawn letterforms, not human handwriting.**

The accuracy it produces is a legitimate, reproducible measurement of *"can ML Kit read clean, correctly
formed Indonesian words at realistic ink scale on this device"* — a **clean-input upper bound**. It is **not**
human-handwriting accuracy and must not be reported as such. State plainly in BAB 4/5 that the strokes were
procedurally generated.

For a real human-handwriting accuracy figure, feed captured strokes instead:

1. Create `mlkit_samples.json` and push it to the device — it overrides everything else:
   ```
   adb push mlkit_samples.json /sdcard/Android/data/com.MiLeonStudio.EMILIAVR/files/mlkit_samples.json
   ```
   Format:
   ```json
   {
     "samples": [
       {
         "label": "human-halo",
         "groundTruth": "halo",
         "strokes": [
           { "points": [ {"x": 40, "y": 120}, {"x": 45, "y": 90}, {"x": 50, "y": 120} ] },
           { "points": [ {"x": 70, "y": 100}, {"x": 90, "y": 100} ] }
         ]
       }
     ]
   }
   ```
   Coordinates are in the **300×200** ML Kit canvas, **y increasing downward** (the same space
   `WhiteboardPen` projects into).
2. Real stroke coordinates are easiest to obtain by capturing them from an actual handwriting session (a
   small logging hook in `WhiteboardPen.FlushAndRecognize()` can dump the projected points). Ask if you want
   that capture hook added — it's the honest way to report a human recognition-accuracy figure.

## Thesis note

Report this as **on-device ML Kit recognition latency**, separate from the network response-time table.
Latency is representative regardless of stroke source. Accuracy must always be labelled with its source:
procedurally rendered words (upper bound) or captured human strokes (real).
