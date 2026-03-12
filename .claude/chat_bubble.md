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
- **New chat**: `StartNewChat()` — clears active conversation, next voice input creates fresh one
- **Load history**: `LoadConversation(convoId)` — fetches from DB, shows last AI response
- **Delete**: `DeleteConversation(convoId)` — removes messages + conversation from DB
- Events: `OnConversationListChanged`, `OnReasoningModeChanged`, `OnActiveConversationChanged`

### 6. `VRControlPanel.cs` — World-space control buttons
- Pokeable buttons for: **New Chat**, **Toggle Reasoning**, **Show History**
- Dual input: `Button.onClick` (controller ray) + `XRSimpleInteractable.selectEntered` (hand poke)
- Reasoning label auto-updates ("Standard" / "Reasoning") via `OnReasoningModeChanged` event
- Blocks input while `VRChatBridge.IsBusy` is true

### 7. `VRHistoryPanel.cs` — Conversation history list
- Floating world-space panel, toggled via control panel or dismissed via close button
- Dynamically spawns pokeable items from `VRChatBridge.ConversationIds`
- Each item shows conversation title (topic or "New Chat" fallback)
- Poke/click an item → `VRChatBridge.LoadConversation(convoId)`, panel auto-hides
- Delete button per item → `VRChatBridge.DeleteConversation(convoId)`
- Highlights the currently active conversation
- Auto-rebuilds when conversation list changes (new chat, deletion, title update)
- CanvasGroup fade in/out with empty-state label when no conversations exist

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
              +-- Footer (horizontal layout)
              |   +-- PageIndicator (TMP, small "1/3" text, muted)
              |   +-- ContinuePrompt (TMP, "▼" pulsing, accent blue)
              +-- PokeTarget (BoxCollider + XRSimpleInteractable for poke)
```

### Prefab: `Assets/Prefabs/VRControlPanel.prefab`
```
VRControlPanel (root)
  +-- [VRControlPanel.cs]
  +-- Canvas (World Space, ~0.3m x 0.12m)
      +-- Panel (Image, dark semi-transparent bg)
          +-- NewChatButton (Button + XRSimpleInteractable + BoxCollider trigger)
          |   +-- Label (TMP "New Chat")
          +-- ReasoningToggle (Button + XRSimpleInteractable + BoxCollider trigger)
          |   +-- ToggleLabel (TMP "Standard" / "Reasoning")
          |   +-- Indicator (Image, color tint shows mode)
          +-- HistoryButton (Button + XRSimpleInteractable + BoxCollider trigger)
              +-- Label (TMP "History")
```

### Prefab: `Assets/Prefabs/VRHistoryPanel.prefab`
```
VRHistoryPanel (root)
  +-- [VRHistoryPanel.cs]
  +-- [CanvasGroup]
  +-- Canvas (World Space, ~0.4m x 0.5m)
      +-- Background (Image, dark panel matching dialogue theme)
          +-- Header
          |   +-- TitleLabel (TMP "Conversations")
          |   +-- CloseButton (Button + XRSimpleInteractable)
          +-- ScrollView (ScrollRect, vertical only)
          |   +-- Viewport (Mask)
          |       +-- Content (VerticalLayoutGroup + ContentSizeFitter)
          |           +-- [HistoryItem instances — spawned at runtime]
          +-- EmptyLabel (TMP "No conversations yet", hidden by default)
```

### Prefab: `Assets/Prefabs/VRHistoryItem.prefab`
```
HistoryItem (root)
  +-- [XRSimpleInteractable + BoxCollider trigger]
  +-- [Button] (for controller ray)
  +-- Background (Image, subtle highlight)
  +-- TitleLabel (TMP, conversation topic)
  +-- DeleteButton (Button, small "X")
```
```

---

## Long-Response Pagination (Visual Novel Style)

When the AI response is too long to fit the panel (> `maxVisibleLines`, default 6), the text is
split into pages at line boundaries. The user advances pages by **poking** a continue indicator
on the panel — a natural VR gesture using XR Interaction Toolkit 3.3.1's poke support.

### Flow

1. `ShowText(rawText)` is called → full text set with alpha trick to compute TMP layout.
2. `SplitIntoPages()` examines `TMP_Text.textInfo` to find page breaks at line boundaries.
3. Typewriter reveals page 0. When complete, "▼" continue prompt appears and pulses.
4. **User pokes the panel** → `AdvancePage()` called → next page begins typewriter.
5. On last page, continue prompt disappears; auto-hide timer starts normally.
6. Subtle "1/3" page counter shown in bottom-left so user knows progress.

### Poke Interaction

- Panel has an `XRSimpleInteractable` with a `BoxCollider` trigger volume.
- On `selectEntered` (poke or pinch-select) → `AdvancePage()`.
- The collider only activates when there are remaining pages (prevents accidental dismissal).
- Works with both hand tracking poke and controller ray select as fallback.

### Why Poke (not Pinch or Scroll)

| Gesture | Pros | Cons |
|---------|------|------|
| **Poke** | Natural "tap to continue", matches UI buttons everywhere in VR, XRI 3.3.1 native | Requires hand near panel |
| Pinch | Works at distance | Feels like "grab", conflicts with other pinch gestures |
| Scroll | Familiar from 2D | Terrible on floating/billboarded world-space panels |

Poke wins for a dialogue panel — it's the exact gesture you use to press any VR button.
Controller trigger serves as fallback when hands aren't tracked.

### Auto-Advance Fallback

If the user doesn't poke within 10 seconds after typewriter completes on a non-final page,
the panel auto-advances. This prevents the user from getting stuck if they're not looking
at or near the panel.

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
Core display script with:
- Typewriter effect via `maxVisibleCharacters`
- **Pagination**: `SplitIntoPages()` using `TMP_Text.textInfo.lineInfo` + `maxVisibleLines`
- `AdvancePage()` triggered by poke interaction or auto-advance timer
- Page counter ("1/3") and pulsing "▼" continue prompt
- Show/hide API, markdown rendering via MarkdownToTMP

### Step 4: Create `Assets/Scripts/Chat/DialoguePanelPositioner.cs`
Gaze-based positioning with character-anchored <-> soft-follow blend. Depends on having a character anchor Transform reference.

### Step 5: Build the `VRDialoguePanel` prefab (Manual — Unity Editor)
World-space Canvas with the hierarchy described above. Key additions for pagination:
- Add `XRSimpleInteractable` component to a child "PokeTarget" with `BoxCollider` (trigger)
- Add Footer with PageIndicator (TMP) + ContinuePrompt (TMP "▼")
- Wire `selectEntered` event to `VRDialoguePanel.AdvancePage()`

### Step 6: Create `Assets/Scripts/Chat/VRChatBridge.cs`
The orchestrator that wires ServiceManager APIs -> VRDialoguePanel. Replicates conversation lifecycle from legacy ChatManager.
- Events: `OnConversationListChanged`, `OnReasoningModeChanged`, `OnActiveConversationChanged`
- Public API: `StartNewChat()`, `LoadConversation(convoId)`, `DeleteConversation(convoId)`, `ToggleReasoningMode()`
- Exposes `ConversationIds`, `CurrentConversationId`, `GetConversationTitle(convoId)` for panels

### Step 7: Create `Assets/Scripts/Chat/VRControlPanel.cs`
World-space control panel with pokeable buttons:
- New Chat → `VRChatBridge.StartNewChat()`
- Reasoning Toggle → `VRChatBridge.ToggleReasoningMode()`, label auto-updates
- History → `VRHistoryPanel.Toggle()`
- Dual input: Button (controller ray) + XRSimpleInteractable (hand poke)

### Step 8: Create `Assets/Scripts/Chat/VRHistoryPanel.cs`
Floating conversation list panel:
- Dynamically spawns HistoryItem prefab instances
- Poke/click item → load conversation + auto-hide panel
- Delete button per item
- CanvasGroup fade, empty-state label
- Auto-rebuilds on `OnConversationListChanged`

### Step 9: Build prefabs (Manual — Unity Editor)
- `VRDialoguePanel.prefab` — world-space Canvas per hierarchy above
- `VRControlPanel.prefab` — small button panel (~0.3m x 0.12m)
- `VRHistoryPanel.prefab` — conversation list (~0.4m x 0.5m)
- `VRHistoryItem.prefab` — single list item (XRSimpleInteractable + Button + TMP + DeleteButton)

### Step 10: Scene setup in `3D_Chat.unity`
- Ensure ServiceManager exists in the scene load chain
- Place VRDialoguePanel prefab in scene
- Place VRControlPanel within arm's reach (or near a table/desk in the scene)
- Place VRHistoryPanel slightly offset from control panel (starts hidden)
- Create `AZKiDialogueAnchor` (empty Transform) parented to AZKi at chest height (~1.1m Y)
- Wire all cross-references in Inspector

---

## Handling Standard vs. Agentic Mode

**Standard** (`ShowText`): Only BodyText + NameLabel visible. QuotePanel hidden. Full typewriter reveal.

**Agentic** (`ShowAgentic`): QuotePanel activates showing reasoning in muted italic. BodyText shows the response with typewriter. If reasoning is empty, falls back to standard display.

Mode toggle managed by `VRControlPanel` → poke the "Reasoning" button → calls `VRChatBridge.ToggleReasoningMode()` → fires `OnReasoningModeChanged` event → label updates to "Standard" or "Reasoning" with color tint.

---

## 2D → VR Feature Migration Map

| 2D Feature | 2D Implementation | VR Implementation | Status |
|------------|------------------|-------------------|--------|
| Send text message | InputField + SendButton | Voice-only via RecordAudio → transcription | ✅ Done |
| AI response display | Scroll view with chat bubbles | Single dialogue panel with typewriter + pagination | ✅ Done |
| Typing indicator | Bubble with animated "..." | Panel with animated "..." | ✅ Done |
| Standard AI mode | ChatApi.SendPrompt() | Same, via VRChatBridge | ✅ Done |
| Agentic/reasoning mode | AgenticApi.Send() + reasoning bubble | Same + quote panel on dialogue | ✅ Done |
| Reasoning toggle | Two buttons (ON/OFF) | Pokeable toggle on VRControlPanel | ✅ Done |
| New chat | Button click | Pokeable button on VRControlPanel | ✅ Done |
| Conversation history | Sidebar with HistoryButton list | VRHistoryPanel (pokeable floating list) | ✅ Done |
| Load past conversation | Click history button → rebuild bubbles | Poke history item → show last AI response | ✅ Done |
| Delete conversation | Delete button + confirmation modal | Delete button per history item | ✅ Done |
| Auto-title (topic) | TopicApi on first exchange | Same, via VRChatBridge | ✅ Done |
| Periodic summary | SummaryApi every 2 pairs | Same, via VRChatBridge | ✅ Done |
| Avatar animation | Animator crossfade (idle/thinking) | Not applicable (3D character in scene) | N/A |
| Auto-scroll | ScrollRect threshold detection | Not needed (pagination replaces scroll) | N/A |

---

## Performance (Quest 3)

- Single persistent world-space Canvas = 1-3 draw calls (negligible)
- `maxVisibleCharacters` typewriter avoids per-frame mesh rebuild
- Canvas only rebuilds when content changes; zero cost when idle
- `LateUpdate` positioning: one dot product + one SmoothDamp + one Slerp per frame (~0.01ms)
- No object pooling needed — single panel, not instantiated bubbles
- History panel: items instantiated/destroyed on demand; typically < 20 items
- SDF font atlases already compiled (Comfortaa, Inter)

---

## Verification

1. **Editor test**: Use XR Device Simulator to simulate head rotation -> verify mode transitions and fade
2. **Typewriter**: Send a long AI response -> verify character-by-character reveal, no TMP mesh thrashing
3. **Pagination**: Send a response longer than 6 lines -> verify page split, "▼" prompt appears, poke advances page, "1/3" counter updates
4. **Auto-advance**: Wait 10s on a non-final page without poking -> page should auto-advance
5. **Gaze fade**: Rotate camera away from AZKi -> panel should smoothly drift to follow position, not snap
6. **Agentic mode**: Poke reasoning toggle on control panel -> send message -> verify quote panel appears with reasoning, body shows response
7. **Auto-hide**: After last page fully displays, wait 8s -> panel should fade out
8. **New chat**: Poke "New Chat" on control panel -> dialogue panel hides -> speak -> new conversation created
9. **History panel**: Poke "History" -> panel fades in with conversation list -> poke an item -> loads last response -> panel auto-hides
10. **Delete conversation**: Poke delete button on history item -> conversation removed from list and DB -> if active, panel clears
11. **Quest 3 build**: Deploy to device, verify text readability at 1.8m, poke interaction responsive, no judder or nausea
