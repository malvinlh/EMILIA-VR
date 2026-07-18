# ML Kit Handwriting Recognition — On-Device Latency Benchmark

Measures how long **Google ML Kit Digital Ink Recognition** takes to turn handwriting strokes into text,
on the Meta Quest 3, over N trials — reporting **mean / SD / min / max / median** to `adb logcat` and a CSV.

This is the on-device counterpart to the server-side HTTP stress test (`AI_MALVIN/stress_test/`). It exists
because handwriting recognition runs **100% on the headset** (bundled ML Kit model, no server call), so it
**cannot** be measured over the network.

## Key facts

- **No AI server involved.** Recognition is on-device → no GPU conflict with the backend. The AI server stays
  off the whole time.
- **ML Kit only runs on an Android build**, never in the Unity Editor (`DigitalInkBridge` guards the plugin
  with `#if UNITY_ANDROID && !UNITY_EDITOR`). So the benchmark must run on the Quest.
- **Unity Editor is needed only once** — to build the APK. Build it with the AI server off (your hardware
  constraint). After that, running the benchmark needs neither the Editor nor the server.
- You do **not** need to wear the headset — the benchmark auto-runs on launch and writes results you read on
  the PC.

## Files

- `MLKitBenchmark.cs` — the benchmark component. Feeds synthetic strokes straight into the existing
  `DigitalInkBridge`, times each `Recognize()` call, logs + writes CSV.
- `MLKitBenchmark_README.md` — this run-book.

## One-time setup in the Unity Editor (AI server off)

1. **Scene.** Create a new scene (e.g. `Assets/Scenes/MLKitBenchmark.unity`). In it, create one empty
   GameObject (name it `DigitalInkManager`) and add **both** components:
   - `DigitalInkBridge`
   - `MLKitBenchmark`
   In `MLKitBenchmark`, leave `Bridge` empty (it auto-finds `DigitalInkBridge.Instance`) or drag the same
   GameObject in. **Set `Language Tag` to the language your users actually write in — on BOTH components.**
   The authoritative one for the downloaded model is `DigitalInkBridge.languageTag` (default `en-US`), because
   the bridge downloads the model in its own `Start()`; set the matching value on `MLKitBenchmark` too.
2. **Build Settings.** `File → Build Settings → Android`. Add the benchmark scene and drag it to the **top**
   (index 0) so a benchmark build launches straight into it. (For a normal build, remove it again.)
3. **Build** the APK (or use *Build And Run* with the Quest connected).

### Config (on the `MLKitBenchmark` component)

| Field | Default | Meaning |
|---|---|---|
| `Run On Start` | true | Benchmark automatically once the model is ready |
| `Trials` | 30 | Measured trials per sample |
| `Warmup` | 1 | Discarded warmup trials per sample |
| `Language Tag` | en-US | BCP-47 tag; match the recognizer/journaling language |
| `Model Ready Timeout` | 60 s | Abort if the model never becomes ready |
| `Recognize Timeout` | 15 s | A single recognition longer than this counts as a failure |

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
   ```
   adb install -r path\to\EMILIA-VR.apk
   adb shell monkey -p com.MiLeonStudio.EMILIAVR -c android.intent.category.LAUNCHER 1
   ```
   (Or just put the headset on briefly and open the app once — it auto-runs.)

2. **Watch live** on the PC (Windows PowerShell):
   ```
   adb logcat | Select-String MLKIT_BENCH
   ```
   or with plain `adb`:
   ```
   adb logcat -s Unity | findstr MLKIT_BENCH
   ```
   You'll see per-trial latencies, then a `SUMMARY (ms)` block with mean/SD/min/max/median.

3. **Pull the CSV** to the PC:
   ```
   adb pull /sdcard/Android/data/com.MiLeonStudio.EMILIAVR/files/
   ```
   The file is `mlkit_bench_<timestamp>.csv` (per-trial rows + a summary section).

## Reading the numbers

- The primary metric is **recognition latency** (ms): time from `Recognize()` to the result being reported
  by the frame-polled bridge — i.e. what a user waits for after finishing a word.
- `synthetic-short/medium/long` are three ink sizes so you can see how latency scales with stroke count.
- `accuracy_pct` shows `n/a` for synthetic strokes — see below.

## Accuracy (optional)

Synthetic strokes give **valid latency** (latency tracks ink volume, not legibility) but **not** trustworthy
accuracy. To measure real recognition accuracy, feed real strokes with known text:

1. Create `mlkit_samples.json` and push it to the device:
   ```
   adb push mlkit_samples.json /sdcard/Android/data/com.MiLeonStudio.EMILIAVR/files/mlkit_samples.json
   ```
   The benchmark loads this instead of the synthetic set. Format:
   ```json
   {
     "samples": [
       {
         "label": "kata-halo",
         "groundTruth": "halo",
         "strokes": [
           { "points": [ {"x": 40, "y": 120}, {"x": 45, "y": 90}, {"x": 50, "y": 120} ] },
           { "points": [ {"x": 70, "y": 100}, {"x": 90, "y": 100} ] }
         ]
       }
     ]
   }
   ```
   Coordinates are in the **300×200** ML Kit canvas (same space `WhiteboardPen` projects into).
2. Real stroke coordinates are easiest to obtain by capturing them from an actual handwriting session (a
   small logging hook in `WhiteboardPen.FlushAndRecognize()` can dump the projected points). Ask if you want
   that capture hook added — it's the honest way to report a real recognition-accuracy figure.

## Thesis note

Report this as **on-device ML Kit recognition latency**, separate from the network response-time table. If
you used synthetic strokes, state that latency is representative while accuracy requires real captured
strokes — don't present synthetic-stroke accuracy as a validated result.
