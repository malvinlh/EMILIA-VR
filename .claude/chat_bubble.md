# VR Chat Dialogue Panel — Implementation Plan

## Context

The EMILIA-VR app needs a way to display AI chat responses to the user inside the Meta Quest 3 headset. The existing chat system lives in `Assets/_legacy/2D Emilia/Scripts/` and is built for 2D UI (scroll views, chat bubbles, sidebar). We need a VR-native dialogue display for the `3D_Chat` scene where the user interacts with the AZKi character.

The user's initial idea — a viewport-locked text box that fades based on gaze direction toward AZKi — is a strong starting point. However, **helmet-locked UI is the #1 VR comfort anti-pattern** (causes nausea and readability issues). The plan below adapts the core idea into a VR-safe design.

---

## Recommended Approach: Hybrid World-Space + Soft-Follow

Instead of rigidly attaching the panel to the headset, we use a **two-mode system** that blends smoothly:

| Mode | When | Behavior |
|------|------|----------|
| **Character-anchored** | User is facing AZKi (within ~56deg cone) | Panel floats near AZKi's chest, billboard-faces the player |
| **Soft-follow** | User looks away from AZKi | Panel smoothly drifts to a comfortable position below the player's gaze center |

The blend between modes uses `Vector3.SmoothDamp` (not a hard switch), so the transition feels natural. The panel fades out entirely when there is no active dialogue.

**Why this is better than helmet-lock:**
- No motion sickness — the panel never rigidly tracks head movement
- Spatial association — text appears "near" AZKi, like she's speaking
- Always readable — if the user turns away mid-response, text follows gently
- Genshin/Arknights feel — dark semi-transparent panel with typewriter text reveal

---

## New Files to Create

All scripts go in `Assets/Scripts/Chat/`:

### 1. `MarkdownToTMP.cs` — Static utility (extracted from legacy)
- Copy `ParseMarkdownToTMP` logic from `ChatBubbleController.cs:241-307` as a `public static string Convert(string input)` method
- Reusable by both legacy and VR systems

### 2. `VRDialoguePanel.cs` — Core display component
- Manages the world-space Canvas with TextMeshPro children
- Public API:
  - `ShowText(string rawText)` — standard mode, starts typewriter
  - `ShowAgentic(string reasoning, string response)` — reasoning quote + response
  - `ShowTypingIndicator()` — animated "..." dots
  - `Hide()` — fade out and clear
- Typewriter effect via TMP's `maxVisibleCharacters` (no mesh rebuild per char)
- Configurable speed: default 30 chars/sec

### 3. `DialoguePanelPositioner.cs` — Spatial positioning + mode switching
- `LateUpdate` computes `Vector3.Dot(cam.forward, directionToCharacter)`
- Above threshold (0.55) → character-anchored mode (billboard near AZKi chest)
- Below threshold → soft-follow mode (1.8m forward, 17deg below gaze)
- Blend factor smoothed via `Mathf.MoveTowards` over ~0.4s
- Position via `Vector3.SmoothDamp`, rotation via `Quaternion.Slerp`

### 4. `VRDialogueFader.cs` — Alpha fade + auto-hide
- Uses `CanvasGroup.alpha` for clean fade in/out (0.3s in, 0.4s out)
- Auto-hide timer: panel fades out N seconds after typewriter finishes (default 8s)
- Timer resets when new text arrives

### 5. `VRChatBridge.cs` — Connects existing services to VR panel
- Consumes `ServiceManager.Instance` (ChatApi, AgenticApi, ChatService, etc.)
- Manages conversation state: `_currentConversationId`, `_messageCache`, `_isReasoningMode`
- On user input → persists message → calls API → `VRDialoguePanel.ShowText(response)`
- Replicates the conversation lifecycle patterns from `ChatManager.cs:676-977`
- Hooks into topic generation + summary (same patterns as legacy)

### Prefab: `Assets/Prefabs/VRDialoguePanel.prefab`
```
VRDialoguePanel (root)
  +-- [VRDialoguePanel.cs]
  +-- [DialoguePanelPositioner.cs]
  +-- [VRDialogueFader.cs]
  +-- Canvas (World Space, 0.6m x 0.25m)
      +-- [CanvasGroup]
          +-- Panel (Image, 9-slice rounded rect, dark bg)
              +-- AccentBar (Image, thin blue line at top)
              +-- NameLabel (TMP, "AZKi", Comfortaa-Bold SDF)
              +-- QuotePanel (hidden by default)
              |   +-- QuoteBar (Image, vertical blue accent)
              |   +-- QuoteText (TMP, Inter SDF, muted italic)
              +-- BodyText (TMP, Inter SDF, main text)
```

---

## Visual Design (Arknights: Endfield / Genshin-inspired)

| Element | Value |
|---------|-------|
| Panel background | `RGBA(20, 20, 30, 210)` — dark blue-black, ~82% opacity |
| Accent bar | 2px top line, `RGBA(115, 166, 242, 153)` — soft blue |
| Name label | Comfortaa-Bold SDF, 5pt world-space, `RGBA(140, 191, 255, 255)` |
| Body text | Inter_18pt-Regular SDF, 3.5pt world-space, `RGBA(235, 235, 242, 255)` |
| Reasoning text | Inter SDF, 3pt, `RGBA(153, 166, 179, 204)` — muted gray italic |
| Quote bar | 2px vertical, matches accent color at 40% alpha |

At 1.8m viewing distance, 3.5pt world-space TMP text ~ 32pt screen equivalent — within the comfortable 28-36pt range for Quest 3.

---

## Integration with Existing Services

**Consumed as-is (no modifications needed):**

| Service | Used by | For |
|---------|---------|-----|
| `ServiceManager.cs` | VRChatBridge | Singleton access to all services |
| `APIChatService.cs` | VRChatBridge | `SendPrompt()` for standard AI responses |
| `APIAgenticService.cs` | VRChatBridge | `Send()` for reasoning + response |
| `LocalChatService.cs` | VRChatBridge | CRUD for conversations/messages |
| `APITopicService`, `APISummaryService` | VRChatBridge | Topic generation + periodic summaries |
| `DatabaseModel.cs` | VRChatBridge | `Message`, `Conversation` data models |

**Not reused (2D-specific):**
- `ChatManager.cs` — 2D bubble instantiation, scroll, sidebar, avatar fade
- `ChatBubbleController.cs` — only `ParseMarkdownToTMP` logic is extracted
- `HistoryButton.cs`, `AutoScrollToBottom.cs` — 2D UI components

---

## Implementation Steps

### Step 1: Create `Assets/Scripts/Chat/MarkdownToTMP.cs`
Extract `ParseMarkdownToTMP` from ChatBubbleController as a static utility class.

### Step 2: Create `Assets/Scripts/Chat/VRDialogueFader.cs`
CanvasGroup-based fade controller with auto-hide timer. Simplest component, no dependencies.

### Step 3: Create `Assets/Scripts/Chat/VRDialoguePanel.cs`
Core display script: typewriter effect, show/hide API, markdown rendering via MarkdownToTMP.

### Step 4: Create `Assets/Scripts/Chat/DialoguePanelPositioner.cs`
Gaze-based positioning with character-anchored <-> soft-follow blend. Depends on having a character anchor Transform reference.

### Step 5: Build the `VRDialoguePanel` prefab
World-space Canvas with the hierarchy described above. Attach all 3 scripts to root. (Manual step in Unity Editor)

### Step 6: Create `Assets/Scripts/Chat/VRChatBridge.cs`
The orchestrator that wires ServiceManager APIs -> VRDialoguePanel. Replicates conversation lifecycle from legacy ChatManager.

### Step 7: Scene setup in `3D_Chat.unity`
- Ensure ServiceManager exists in the scene load chain
- Place VRDialoguePanel prefab in scene
- Create `AZKiDialogueAnchor` (empty Transform) parented to AZKi at chest height (~1.1m Y)
- Wire references in Inspector

---

## Handling Standard vs. Agentic Mode

**Standard** (`ShowText`): Only BodyText + NameLabel visible. QuotePanel hidden. Full typewriter reveal.

**Agentic** (`ShowAgentic`): QuotePanel activates showing reasoning in muted italic. BodyText shows the response with typewriter. If reasoning is empty, falls back to standard display.

Mode toggle managed by `VRChatBridge._isReasoningMode` — can be triggered by controller button, hand gesture, or voice command.

---

## Performance (Quest 3)

- Single persistent world-space Canvas = 1-3 draw calls (negligible)
- `maxVisibleCharacters` typewriter avoids per-frame mesh rebuild
- Canvas only rebuilds when content changes; zero cost when idle
- `LateUpdate` positioning: one dot product + one SmoothDamp + one Slerp per frame (~0.01ms)
- No object pooling needed — single panel, not instantiated bubbles
- SDF font atlases already compiled (Comfortaa, Inter)

---

## Verification

1. **Editor test**: Use XR Device Simulator to simulate head rotation -> verify mode transitions and fade
2. **Typewriter**: Send a long AI response -> verify character-by-character reveal, no TMP mesh thrashing
3. **Gaze fade**: Rotate camera away from AZKi -> panel should smoothly drift to follow position, not snap
4. **Agentic mode**: Toggle reasoning mode -> send message -> verify quote panel appears with reasoning, body shows response
5. **Auto-hide**: After text fully displays, wait 8s -> panel should fade out
6. **Quest 3 build**: Deploy to device, verify text readability at 1.8m, no judder or nausea from panel movement
