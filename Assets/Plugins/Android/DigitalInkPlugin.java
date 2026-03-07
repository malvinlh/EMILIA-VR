package com.vrhandwriting.digitalink;

import android.util.Log;
import com.google.mlkit.common.MlKitException;
import com.google.mlkit.common.model.DownloadConditions;
import com.google.mlkit.common.model.RemoteModelManager;
import com.google.mlkit.vision.digitalink.*;
import java.util.List;

/**
 * Android-side bridge for Google ML Kit Digital Ink Recognition.
 * Called from Unity via AndroidJavaObject JNI.
 *
 * Lifecycle (called from C#):
 *   1. downloadModel("en-US")       → downloads model (first launch = needs internet)
 *   2. beginStroke(x, y, timeMs)    → start a new stroke
 *   3. addPoint(x, y, timeMs)       → append points while finger is touching
 *   4. endStroke()                   → finish the current stroke
 *   5. setPreContext("previous text")→ set language model context
 *   6. setWritingArea(w, h)         → set writing surface dimensions
 *   7. recognize()                   → run recognition on accumulated strokes
 *   8. poll getLastResult() / getLastCandidatesJson() / getLastError()
 */
public class DigitalInkPlugin {
    private static final String TAG = "DigitalInkPlugin";

    private DigitalInkRecognizer recognizer;
    private Ink.Builder inkBuilder = Ink.builder();
    private Ink.Stroke.Builder strokeBuilder;
    private boolean modelReady = false;
    private String lastResult = "";
    private String lastCandidatesJson = "[]";
    private String lastError  = "";
    private boolean recognizing = false;
    private int strokeCount = 0;

    // Recognition context fields
    private String preContext = "";
    private float writingAreaWidth  = 0;
    private float writingAreaHeight = 0;

    // ────────────────────────────────────────────────────────────────
    // MODEL
    // ────────────────────────────────────────────────────────────────

    /** Download the recognition model for a BCP-47 language tag (e.g. "en-US"). */
    public void downloadModel(final String languageTag) {
        DigitalInkRecognitionModelIdentifier modelId;
        try {
            modelId = DigitalInkRecognitionModelIdentifier.fromLanguageTag(languageTag);
        } catch (MlKitException e) {
            lastError = "Invalid language tag: " + e.getMessage();
            Log.e(TAG, lastError);
            return;
        }

        if (modelId == null) {
            lastError = "No model found for tag: " + languageTag;
            Log.e(TAG, lastError);
            return;
        }

        DigitalInkRecognitionModel model =
                DigitalInkRecognitionModel.builder(modelId).build();

        RemoteModelManager manager = RemoteModelManager.getInstance();

        manager.isModelDownloaded(model)
            .addOnSuccessListener(downloaded -> {
                if (downloaded) {
                    initRecognizer(model);
                } else {
                    Log.i(TAG, "Downloading model for: " + languageTag);
                    manager.download(model, new DownloadConditions.Builder().build())
                        .addOnSuccessListener(v -> initRecognizer(model))
                        .addOnFailureListener(e -> {
                            lastError = "Model download failed: " + e.getMessage();
                            Log.e(TAG, lastError);
                        });
                }
            })
            .addOnFailureListener(e -> {
                lastError = "isModelDownloaded check failed: " + e.getMessage();
                Log.e(TAG, lastError);
            });
    }

    private void initRecognizer(DigitalInkRecognitionModel model) {
        recognizer = DigitalInkRecognition.getClient(
                DigitalInkRecognizerOptions.builder(model).build());
        modelReady = true;
        Log.i(TAG, "Model ready");
    }

    // ────────────────────────────────────────────────────────────────
    // RECOGNITION CONTEXT
    // ────────────────────────────────────────────────────────────────

    /** Set the text that precedes the current handwriting for better language model predictions. */
    public void setPreContext(String ctx) {
        preContext = (ctx != null) ? ctx : "";
    }

    /** Set the writing surface dimensions (in the same coordinate system as stroke points). */
    public void setWritingArea(float width, float height) {
        writingAreaWidth  = width;
        writingAreaHeight = height;
    }

    // ────────────────────────────────────────────────────────────────
    // STROKE COLLECTION
    // ────────────────────────────────────────────────────────────────

    /** Begin a new stroke. */
    public void beginStroke(float x, float y, long timestampMs) {
        strokeBuilder = Ink.Stroke.builder();
        strokeBuilder.addPoint(Ink.Point.create(x, y, timestampMs));
    }

    /** Append a point to the current stroke. */
    public void addPoint(float x, float y, long timestampMs) {
        if (strokeBuilder != null) {
            strokeBuilder.addPoint(Ink.Point.create(x, y, timestampMs));
        }
    }

    /** Finish the current stroke and add it to the ink. */
    public void endStroke() {
        if (strokeBuilder != null) {
            inkBuilder.addStroke(strokeBuilder.build());
            strokeBuilder = null;
            strokeCount++;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // RECOGNITION
    // ────────────────────────────────────────────────────────────────

    /** Run recognition on all accumulated strokes. Asynchronous. */
    public void recognize() {
        if (!modelReady || recognizer == null) {
            lastError = "Model not ready";
            return;
        }
        if (recognizing) return; // already in progress

        Ink ink = inkBuilder.build();
        inkBuilder = Ink.builder(); // reset for next batch
        strokeCount = 0;

        if (ink.getStrokes().isEmpty()) {
            lastResult = "";
            lastCandidatesJson = "[]";
            return;
        }

        recognizing = true;
        lastResult  = "";
        lastCandidatesJson = "[]";

        // Build RecognitionContext with pre-context and writing area.
        // preContext MUST always be set — ML Kit 18.1.0 requires it.
        RecognitionContext.Builder ctxBuilder = RecognitionContext.builder();
        ctxBuilder.setPreContext(preContext != null ? preContext : "");

        if (writingAreaWidth > 0 && writingAreaHeight > 0) {
            ctxBuilder.setWritingArea(new WritingArea(writingAreaWidth, writingAreaHeight));
        }

        RecognitionContext context = ctxBuilder.build();

        recognizer.recognize(ink, context)
            .addOnSuccessListener(result -> {
                List<RecognitionCandidate> candidates = result.getCandidates();
                if (candidates != null && !candidates.isEmpty()) {
                    lastResult = candidates.get(0).getText();
                    lastCandidatesJson = candidatesToJson(candidates);
                } else {
                    lastResult = "";
                    lastCandidatesJson = "[]";
                }
                recognizing = false;
                Log.i(TAG, "Recognized: " + lastResult
                        + " (" + (candidates != null ? candidates.size() : 0) + " candidates)");
            })
            .addOnFailureListener(e -> {
                lastError = "Recognition failed: " + e.getMessage();
                recognizing = false;
                Log.e(TAG, lastError);
            });
    }

    /** Convert ML Kit candidates to a JSON array string. */
    private String candidatesToJson(List<RecognitionCandidate> candidates) {
        StringBuilder sb = new StringBuilder("{\"items\":[");
        int limit = Math.min(candidates.size(), 10); // cap at 10 candidates
        for (int i = 0; i < limit; i++) {
            if (i > 0) sb.append(",");
            RecognitionCandidate c = candidates.get(i);
            String text = escapeJson(c.getText());
            float score;
            try {
                score = (float) c.getScore();
            } catch (Exception e) {
                score = -1f;
            }
            sb.append("{\"text\":\"").append(text).append("\",\"score\":");
            if (Float.isNaN(score)) {
                sb.append("-1");
            } else {
                sb.append(score);
            }
            sb.append("}");
        }
        sb.append("]}");
        return sb.toString();
    }

    /** Escape special characters for JSON string values. */
    private String escapeJson(String s) {
        if (s == null) return "";
        return s.replace("\\", "\\\\")
                .replace("\"", "\\\"")
                .replace("\n", "\\n")
                .replace("\r", "\\r")
                .replace("\t", "\\t");
    }

    // ────────────────────────────────────────────────────────────────
    // POLLING (called from Unity C#)
    // ────────────────────────────────────────────────────────────────

    public String getLastResult()         { return lastResult;         }
    public String getLastCandidatesJson() { return lastCandidatesJson; }
    public String getLastError()          { return lastError;          }
    public boolean isModelReady()         { return modelReady;         }
    public boolean isRecognizing()        { return recognizing;        }

    /** Returns the number of strokes currently accumulated. */
    public int getStrokeCount() {
        return strokeCount;
    }

    /** Discard all accumulated strokes without recognising. */
    public void clearInk() {
        inkBuilder    = Ink.builder();
        strokeBuilder = null;
        strokeCount   = 0;
    }
}
