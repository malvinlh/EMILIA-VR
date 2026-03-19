# Apple Scribble-like Journaling — Design & Setup

## Overview

The Scribble system converts VR handwriting into clean, flowing text on the whiteboard surface, inspired by Apple's Scribble feature. Instead of showing raw ink alongside a small recognized-text label, the system replaces handwriting ink with properly laid-out TextMeshPro words after each recognition cycle.

**Scene:** `Assets/Scenes/3D_Journal_scribble.unity`

## Features

| Feature | Description | Gesture / Trigger |
|---------|-------------|-------------------|
| **Handwriting to text** | Strokes are recognized and rendered as 3D TMP text on the board | Automatic after idle delay |
| **Flowing layout** | Words flow left-to-right with automatic line wrapping | Automatic |
| **Scratch-to-delete** | Zigzag over a word to erase it | Draw a back-and-forth scribble over the target word |
| **Undo** | Reverts the last add or delete action | Left hand: thumb + index finger pinch |
| **Auto-clear ink** | Handwriting ink is wiped after recognition, leaving only clean text | Automatic |
| **Board clear** | Clears all text, undo history, and ink | Right hand: thumb + pinky finger pinch |

### Features intentionally omitted for VR

- **Voice input** — omitted per user request
- **Text selection / cursor** — impractical without a precise 2D pointer in VR hand tracking
- **Inline editing (insert/backspace)** — replaced by scratch-to-delete + undo, which better suits 3D hand gestures

## Architecture

```
WhiteboardPen (stroke input)
    │
    ├─ OnStrokesFlushed ──► ScribbleManager (enqueue metadata)
    ├─ CurrentTouchWorldPoint ──► ScribbleManager (scratch detection)
    ├─ ClearStrokeBuffer() ◄── ScribbleManager (after scratch)
    │
    └─ FlushAndRecognize() ──► DigitalInkBridge ──► ML Kit
                                    │
                                    ▼
                            RecognitionPipeline
                                    │
                            OnFinalTextRecognized
                                    │
                                    ▼
                            ScribbleManager.OnTextRecognized()
                                    │
                                    ├─ Whiteboard.ClearToBackground()
                                    └─ PlaceWord() × N
                                          │
                                          └─ 3D TextMeshPro objects
                                               on whiteboard surface
```

## Files Modified / Created

| File | Change |
|------|--------|
| `Assets/Scripts/Handwriting/ScribbleManager.cs` | **New** — core coordinator |
| `Assets/Scripts/Handwriting/WhiteboardPen.cs` | Added: `StrokeMetadata`, `OnStrokesFlushed`, `OnBoardCleared`, `CurrentTouchWorldPoint`, `ClearStrokeBuffer()` |
| `Assets/Scripts/Handwriting/Whiteboard.cs` | Added: `ClearToBackground()` |
| `Assets/Scenes/3D_Journal_scribble.unity` | Added ScribbleManager component on DigitalInkManager |

## Key Classes

### ScribbleManager

**Singleton.** Central coordinator. Attach to any GameObject (defaults to DigitalInkManager in the scribble scene).

**Inspector fields:**

| Field | Default | Description |
|-------|---------|-------------|
| `textHeightOffset` | 0.002 | Height above board for text objects |
| `textScale` | 0.004 | World-space scale of TMP objects |
| `fontSize` | 36 | TMP font size |
| `textColor` | (0.15, 0.15, 0.15) | Rendered text color |
| `fontAsset` | null (TMP default) | Optional custom font |
| `wordSpacing` | 0.008 | Gap between words (meters) |
| `boardMargin` | 0.015 | Inset from board edges |
| `minScratchReversals` | 4 | Direction changes needed for scratch |
| `minReversalDisplacement` | 0.005 | Min displacement per reversal segment |
| `maxScratchExtent` | 0.15 | Max bounding box diagonal for scratch |
| `maxUndoSteps` | 30 | Undo history depth |

**Public API:**

- `GetFullText()` — returns all accumulated words as a single string
- `ClearAll()` — clears everything (words, undo, ink)
- `Undo()` — reverts the last action
- `OnTextChanged` event — fired when the accumulated text changes

### WhiteboardPen (Scribble additions)

- `StrokeMetadata` struct — center, bounds, right, forward of flushed strokes
- `OnStrokesFlushed` event — fired after strokes are sent for recognition
- `OnBoardCleared` event — fired on pinky-pinch clear
- `CurrentTouchWorldPoint` — nullable Vector3, current touch point while drawing
- `ClearStrokeBuffer()` — clears buffered strokes without triggering recognition

### Whiteboard (Scribble additions)

- `ClearToBackground()` — resets the texture to `backgroundColor`

## Scratch-to-Delete Algorithm

1. While the pen is drawing, world-space touch points are accumulated
2. Points are checked periodically (every 5 points after 15 total) and on stroke end
3. **Bounding box check:** diagonal must be between 0.005m and `maxScratchExtent`
4. **Dominant axis:** determined from XZ bounding box extent
5. **Reversal counting:** direction changes along the dominant axis are counted; each reversal must have at least `minReversalDisplacement` of accumulated movement
6. If reversals >= `minScratchReversals`, the gesture is classified as a scratch
7. The scratch bounding box (expanded by 1cm) is tested against all word bounding boxes using XZ overlap
8. Overlapping words are deleted with a red-flash + fade-out animation

## Text Orientation

Text is oriented flat on the horizontal whiteboard, readable from the user's camera position at initialization time:

```csharp
textBaseRotation = Quaternion.LookRotation(textForward, Vector3.up)
                 * Quaternion.Euler(90, 0, 0);
```

- `textRight` = camera right (projected to XZ, normalized)
- `textForward` = camera forward (projected to XZ, normalized)
- Text faces upward (-Z = up), readable when looking down at the board

## Layout System

Words are placed using a cursor-based flowing layout:

1. **Start position:** top-left corner of the board (far from user, left side)
2. **Advance:** cursor moves right by `wordWidth + wordSpacing` after each word
3. **Line wrap:** when `cursorOffsetRight + wordWidth` exceeds available width, cursor moves down by `lineHeight` and resets to left edge
4. **Line height:** `fontSize * textScale * 1.8`

## Undo System

- Stack-based (LIFO), max depth = `maxUndoSteps`
- Tracks two action types: `Add` and `Delete`
- Undoing an Add hides the word and removes it from the list
- Undoing a Delete re-shows the word and re-inserts at original index
- Cursor is recomputed after undo by replaying the entire word list through the layout logic

## Setup Instructions

1. Open scene `Assets/Scenes/3D_Journal_scribble.unity`
2. The ScribbleManager component is already on the **DigitalInkManager** GameObject
3. Ensure the scene has:
   - A `WhiteboardUtils` with a valid whiteboard prefab
   - A `WhiteboardPen` (right hand)
   - A `DigitalInkBridge` and optionally `RecognitionPipeline`
4. ScribbleManager auto-disables `RecognizedTextDisplay` at runtime (it replaces that functionality)
5. Tune parameters in the Inspector as needed

## Tuning Tips

- **Recognition too slow?** Lower `autoRecognizeDelay` on WhiteboardPen (default 1.0s)
- **Scratch too sensitive?** Increase `minScratchReversals` or `minReversalDisplacement`
- **Scratch not triggering?** Decrease `minScratchReversals` or increase `maxScratchExtent`
- **Text too small/large?** Adjust `textScale` and `fontSize` together
- **Words overlapping?** Increase `wordSpacing` or `boardMargin`
