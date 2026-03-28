# EMILIA VR — Journaling Flow Design
**Perspective: Clinical Psychology × UX Design**

---

## Psychological Foundation

The journaling flow is designed around five evidence-based principles:

| Principle | Source | How It Applies |
|-----------|--------|----------------|
| **Expressive Writing Therapy** | Pennebaker & Beall (1986) | The act of writing about emotional experiences reduces stress markers and improves cognitive clarity — regardless of whether the writing is saved or discarded |
| **Symbolic Release / Ritual Completion** | Arndt et al. (2013), ritual behavior research | A physical, deliberate action (throw vs. placement) reinforces psychological closure far more effectively than a button tap |
| **Embodied Cognition** | Lakoff & Johnson (1999) | Bodily gestures shape thought — a downward throw physically enacts "letting go"; placing something carefully enacts "I value this" |
| **Witnessed Validation** | Rogers' person-centered therapy | Being seen and acknowledged after disclosure amplifies the therapeutic benefit; EMILIA's response is the "witness" |
| **Agency & Choice Architecture** | Self-determination theory | The user must actively choose what happens to their writing — this preserves their sense of control over their own narrative |

---

## Design Principles Applied

- **Friction as intention signal** — harder physical actions (hold to confirm, deliberate throw) mean more conscious choice
- **Ceremony** — every major transition uses a fade to signal "this moment matters"
- **Progressive disclosure** — complexity revealed only when needed; nothing before the user is ready
- **Single-task focus** — during writing, the world offers nothing but the whiteboard
- **Graceful closure** — every path ends with dignity; no abrupt exits

---

## Full Flow — Phase by Phase

### Overview

```
[Idle] → [Passthrough + Calibration] → [Surface Confirmation] → [Preview]
      → [VR Transition] → [Welcome] → [Writing] → [Done?]
      → [Fade + Avatar] → [AI Reflection] → [The Choice]
               ↓                                      ↓
          [Keep Path]                          [Release Path]
      place bottle in rack                  throw bottle to ocean
               ↓                                      ↓
           [Saved]                               [Released]
               ↓                                      ↓
                        [EMILIA Farewell + Fade]
```

---

### Phase 0 — Arrival (Idle)

**What the user experiences:**
- The virtual world is quiet and still
- A gentle ambient sound — soft ocean waves in the distance — orients the user spatially
- EMILIA is not visible yet (she enters only after the user has written)
- A single start button (or automatic trigger via proximity) begins the session

**Psychological intent:**
The user has not yet committed to anything. The atmosphere should feel like standing outside a room you chose to enter — no pressure, no urgency. The ocean sound is deliberate: it plants the idea of water long before the throw mechanic, so the ocean at the end does not feel arbitrary.

**What currently exists:** Start button in world. ✅
**What to consider adding:** Soft looping ocean ambience that fades in on session start.

---

### Phase 1 — Grounding (Passthrough Onset)

**What the user experiences:**
- The virtual world fades out; the real world fades in (passthrough)
- The user sees their own real hands for the first time in the session
- Calibration guide text appears: *"Place both hands flat on your table."*
- Palm indicator spheres appear at each hand (gray, waiting)

**Psychological intent:**
Grounding is a clinical technique used to anchor a person in the present moment before emotional processing. The act of placing hands flat on a physical surface — feeling real texture, real gravity — activates the parasympathetic nervous system. This is the body's "rest and process" mode. The user is being prepared for reflection without knowing it.

The real-world view also does something powerful: it tells the user *"this journaling space is connected to your real life."* The virtual desk they will write on will be in the exact position of their real desk.

**What currently exists:** Passthrough fade + calibration guide instruction. ✅

---

### Phase 2 — Surface Confirmation (HandConfirmation)

**What the user experiences:**
- Both palms flat on the table → indicators turn yellow
- Progress counter begins: *"Hold steady... 45%"*
- Indicators shift yellow → green as progress completes
- *"Table confirmed! Transitioning..."*

**Psychological intent:**
This is a small ritual of commitment. The user must hold still — deliberately, consciously — for several seconds. This is not just a technical requirement; it is the first deliberate action in the session. The 100% completion moment creates a micro-satisfaction response (dopamine, goal completion) that primes the user for the work ahead.

The color shift from yellow to green is borrowed from traffic light semantics: *you are ready, you may proceed.* This is universal and requires no explanation.

**What currently exists:** Palm color feedback, progress counter, dot grid. ✅

---

### Phase 3 — Preview & Transition (Preview → TransitionToVR)

**What the user experiences:**
- The virtual whiteboard appears on their real table in passthrough — they can see both worlds at once
- *"Calibrating..."* → *"Done, returning to the game world."*
- Screen fades to black
- Silent teleport to seated position, camera snaps to face the desk
- Screen fades back in — the user is now in VR, but the desk is exactly where they expect it

**Psychological intent:**
Showing the whiteboard on the real table before the VR transition is critical for *spatial trust*. The user can verify, in the real world, that the virtual desk matches reality. This prevents the disorientation of sitting down in VR and finding the desk at the wrong height or distance.

The fade-to-black is a psychological bookmark. It says: *the previous world is pausing; something new is beginning.* This is the same reason film cuts to black before major transitions. It is not a technical necessity — it is a ceremony.

**What currently exists:** Preview, fade-to-black, teleport, fade-in. ✅

---

### Phase 4 — Welcome (EMILIA Greeting)

**What the user experiences:**
- As the screen fades in, the VRDialoguePanel displays a brief message
- Text (typewriter effect, auto-dismisses in 5–8 seconds):

  > *"This is your space. Write freely — I'll be here when you're done."*
  > — EMILIA

**Psychological intent:**
The blank whiteboard can feel intimidating. This is called "blank page anxiety" in writing therapy contexts — the empty space invites self-censorship ("what if I write the wrong thing?"). EMILIA's message before writing begins does two things:

1. It reassures the user that there is no wrong answer
2. It establishes EMILIA as a companion who is *waiting*, not watching — she is present but not intrusive

This is the difference between a therapist who sits with you and says nothing, and one who says "I'm here" before going quiet. The latter is more supportive without being directive.

**What currently exists:** Silent transition into journaling state. ⚠️ Missing.
**What to add:** In `JournalSessionManager`, after teleporting to SeatPoint and fading in, call `dialoguePanel.ShowText()` with the welcome message. The panel already auto-hides; no new system needed.

---

### Phase 5 — Writing (Journaling State)

**What the user experiences:**
- The whiteboard is in front of them, blank except for hint text: *"Write your thoughts here..."*
- The hint text disappears on first contact
- The right hand draws in real time; the left hand is passive (page navigation or idle)
- Pages can be added via navigation buttons (Previous / Next)
- A Done button sits on the bottle nearby — the bottle is the journal

**Psychological intent:**
The writing phase should feel like the safest possible space: no clock, no guidance, no AI watching. This is *free writing* — unfiltered, uncensored, self-directed. The research on expressive writing consistently shows that the benefit comes from the writing process itself, not from any particular structure or quality of what is written.

The whiteboard metaphor is appropriate for VR because it is familiar and low-stakes (whiteboards are erasable). The page navigation means there is no word limit — the user can write as much or as little as they need.

**The bottle as journal metaphor:**
The Done button is mounted on the bottle, not on the whiteboard. This is intentional: *the writing fills the bottle.* Each word is being sealed inside it. When the user presses Done, they are not closing the app — they are sealing the bottle. This metaphor will pay off at the end when they decide what to do with it.

**What currently exists:** Whiteboard, right-hand drawing, page navigation, Done button on bottle. ✅

---

### Phase 6 — The Done Decision

**What the user experiences:**
- User pokes the Done button on the bottle
- A brief confirmation prompt appears:

  > *"Are you done writing for today?"*
  > — [Confirm] [Not yet]

- If Confirm: session ends, transitions to review
- If Not yet: returns to writing immediately

**Psychological intent:**
A single accidental poke ending the session mid-thought is a significant failure mode in a therapeutic app. It would break trust and could leave the user feeling cut off at a vulnerable moment. The confirmation step is not friction for friction's sake — it is protection for the user's process.

The language matters: *"Are you done writing for today?"* not *"End session?"* The former is warm and personal; the latter sounds like closing a word processor.

**What currently exists:** Single poke → `EndSession()`. ⚠️ Accidental tap risk.
**What to add:** Second poke target (Confirm) within a short window, or a two-button confirmation panel on the bottle. Change is limited to `JournalDoneButton.cs`.

---

### Phase 7 — Transition to Review (Fade → Avatar)

**What the user experiences:**
- Screen fades to black
- EMILIA's avatar is activated at her authored scene position
- Player teleports to StandPoint; camera snaps to face the avatar
- Controller ray is restored (NearFarInteractor.enableFarCasting = true)
- Screen fades back in — EMILIA is standing before them

**Psychological intent:**
This is the most important transition in the session: from *private writing* to *witnessed reflection.* The fade-to-black is the ritual separator. The user is moving from "writing for myself" to "being seen by someone I trust."

The fact that EMILIA was invisible during writing is intentional: she was not watching. She is only present now, after the writing is complete, as a responder — not an observer. This mirrors the therapeutic practice of having a client write in private before sharing with a therapist.

The camera snap to face the avatar also matters: it is subtle, but it tells the user *face her; what she says is for you.*

**What currently exists:** Fade, avatar enable, teleport, camera snap, ray restore. ✅

---

### Phase 8 — AI Reflection (ShowingComment)

**What the user experiences:**
- EMILIA's words appear with a typewriter effect:

  > *"You've taken a meaningful step today by putting your thoughts into words.*
  > *Reflecting on what you've written can help you better understand your feelings*
  > *and find clarity in moments of uncertainty.*
  >
  > *Your words matter — and so do you. I'm proud of you for showing up.*
  > *— EMILIA"*

- The user cannot skip or rush this (intentional small friction)

**Psychological intent:**
This is the "witnessed validation" moment. In person-centered therapy, unconditional positive regard — being seen and accepted without judgment — is one of the most powerful healing forces. EMILIA's message does exactly this: it validates the act of writing (not what was written), acknowledges the difficulty of showing up, and affirms the user's worth.

The typewriter effect matters: the user reads at the pace the message is delivered, which is close to speaking pace. This creates a sense of being spoken to — not reading a notification.

The absence of a skip button is deliberate. In a real session, a therapist does not pause and say *"feel free to ignore this."* The message is meant to land.

**What currently exists:** Typewriter effect, EMILIA dialogue, auto-hide. ✅
**Future enhancement:** If session metadata is available (pages written, session number), the message could reference them: *"This is your third time showing up — that takes real courage."* This requires session tracking but increases personalization.

---

### Phase 9 — The Choice (ShowingChoice)

**What the user experiences:**
- Choice panel appears after EMILIA's message concludes (brief delay ~0.5s)
- Title: *"Would you like to preserve this journal entry?"*
- Two buttons:
  - **"Yes, keep it"** — sage green (#7CB98E)
  - **"Let it go"** — soft coral (#C97873)

**Psychological intent:**
This is the therapeutic core of the entire experience. The user is not being asked to "save file" or "delete file." They are being asked: *do these words belong in your story, or are you ready to release them?*

The color choice is deliberate: green signals growth, safety, continuation; coral is warm, not alarming — "letting go" is not coded as failure or deletion. Both choices are valid. Both are presented with equal visual weight.

The brief delay after EMILIA speaks before the buttons appear is important: it gives the user a moment to absorb what she said before being asked to decide. Presenting the choice simultaneously with or before the message would undermine the reflection.

**What currently exists:** Choice panel, delay mechanism, button colors. ✅

---

### Phase 10a — Keep Path (Bottle → Rack)

**What the user experiences:**
- EMILIA says:
  > *"I'm glad you want to hold onto this memory. Please place the journal bottle into the rack on the board nearby to safely store your entry."*

- The bottle becomes grabbable
- User reaches out, pinches or grips with controller, lifts the bottle
- Haptic feedback: medium pulse on grab
- User carries it deliberately to the wine rack
- Bottle settles into rack — satisfying placement
- Haptic: gentle confirmation pulse
- Session saved to database

**Psychological intent:**
The physical act of *carefully placing* something into a rack is a gesture of curation. The user is saying: *I choose to keep this; I value what I wrote.* The deliberateness required — lifting, carrying, placing — makes the decision feel real and lasting. This is not a button click; it is a physical commitment.

The wine rack metaphor is apt: wine improves with time. Stored journals can be revisited. The rack is a vault, not a filing cabinet.

**What currently exists:** Wine rack XRSimpleInteractable, save trigger on selectEntered. ✅
**What to add:** XRGrabInteractable + Rigidbody on bottle. Haptic feedback on grab/place.

---

### Phase 10b — Release Path (Bottle → Ocean Throw)

**What the user experiences:**
- EMILIA says:
  > *"It's okay to release what no longer serves you. When you're ready, gently toss the journal bottle into the ocean below. Let your words drift away with the tide — you've already done the hard work."*

- The bottle becomes grabbable
- User reaches out, grips the bottle (pinch or controller trigger)
- Haptic: medium pulse on grab
- User winds up and throws — the bottle arcs through the air
- Haptic: short strong burst on release
- Bottle splashes into the ocean below
- Sound: satisfying ocean splash SFX
- Haptic: (optional) single strong pulse on impact
- Session is NOT saved; entry is discarded

**Psychological intent:**
This is the most powerful moment in the therapeutic flow. The research on symbolic release consistently shows that physical enactment of a metaphor — not just imagining it — produces measurable emotional relief. Throwing an object downward, away from the body, triggers motor circuits associated with expulsion, release, and completion.

Critically: the user must *choose* to throw. This is not automatic. The bottle sits in their hand, and they decide the moment, the force, the angle. This agency is what makes it feel real. A scripted animation would not produce the same effect.

The ocean splash — sound, visual, haptic — closes the loop. The user hears, sees, and feels the release. Multisensory feedback anchors the emotional closure in the body, not just the mind.

**"You've already done the hard work"** — this line in EMILIA's message is intentional. The release is not failure or avoidance. Writing the entry was the therapeutic act. The throw is the ceremony of completion.

**What currently exists:** Y-threshold monitoring in `JournalReviewController.Update()`. ✅ Bottle physics: ⚠️ Missing.
**What to add:** XRGrabInteractable + Rigidbody (throwOnDetach = true) on bottle. Splash particle + SFX. Haptic events on grab/release/splash.

---

### Phase 11 — Closure

**What the user experiences:**
- Screen fades to black briefly
- Optional EMILIA farewell text (single line, no typewriter, just appears):

  > *"Thank you for showing up today."*

- Returns to starting state or main menu

**Psychological intent:**
The last thing a user hears should not be a system message. It should be human. *"Thank you for showing up today"* is one of the most affirming things a therapist can say — it honors the courage it took to engage, regardless of outcome. It is also a reminder that the user will be welcomed back.

The brevity of the farewell is deliberate. A long closing message would dilute the emotional impact of everything that just happened. One sentence is enough.

**What currently exists:** Fade out, session cleanup. ✅
**What to add:** Brief farewell text before final fade. Single call to `dialoguePanel.ShowText()` before `EndSession()` cleanup.

---

## Bottle Throw Mechanic — Technical Design

### Why Hand Throw (Primary) + Controller (Secondary)

From a psychological standpoint, hand throwing is strongly preferred:

- The user physically extends their arm, winds up, and releases
- The throwing motion recruits the whole upper body
- The momentum of the throw is felt in the muscles and joints (proprioception)
- This creates a stronger embodied experience of "releasing"

A controller throw (holding trigger, releasing to throw) is a valid fallback and should be supported, but it is mechanically thinner — the user presses a button rather than making a throwing gesture.

XRI's XRGrabInteractable supports both simultaneously: hand pinch to grab, open hand to release; controller trigger to grab, release trigger to throw. Both paths share the same throw physics.

### Component Requirements

| Component | Setting | Reason |
|-----------|---------|--------|
| `Rigidbody` | `useGravity = true`, `isKinematic = false` while grabbed | Realistic arc after throw |
| `XRGrabInteractable` | `throwOnDetach = true` | Preserves throw velocity on release |
| `XRGrabInteractable` | `throwSmoothingDuration = 0.05s` | Smooths jittery hand data without killing throw speed |
| `XRGrabInteractable` | Interaction Layer: Default | Accessible by both hand and controller interactor |
| Collider | Convex MeshCollider or CapsuleCollider | Required for physics simulation |

### Haptic Design

| Moment | Haptic | Both Controllers? |
|--------|--------|------------------|
| Bottle grabbed | Medium rumble, 0.2s | Only the grabbing hand/controller |
| Bottle released (throw) | Short sharp burst, 0.1s | Only the throwing hand/controller |
| Bottle hits ocean (splash) | Single strong pulse, 0.15s | Both (the event affects both hands) |
| Bottle placed in rack | Soft double-tap, 0.15s | Only the placing hand |

### Visual / Audio Design

| Event | Visual | Audio |
|-------|--------|-------|
| Session start (Idle → Passthrough) | — | Ocean waves fade in (ambient loop) |
| Bottle in air (release) | Subtle trail particle | Whoosh SFX |
| Bottle hits ocean | Splash particle system at ocean Y | Splash SFX |
| Bottle placed in rack | Small glow pulse on rack | Soft thud + glass clink SFX |

---

## Summary: What to Change vs. What to Keep

### Keep Unchanged
- All 7-state calibration flow (Passthrough → HandConfirmation → Preview)
- Whiteboard writing system (WhiteboardPen, WhiteboardPageManager)
- VR transition sequence (fade → teleport → height restoration → fade-in)
- JournalReviewController review flow and state machine
- Choice panel design, EMILIA dialogue text, button colors
- Ocean Y-threshold monitoring in `JournalReviewController.Update()`
- Wine rack XRSimpleInteractable placement detection

### Add (Minimal Code Changes)
| Addition | Where | Scope |
|----------|-------|-------|
| Pre-writing EMILIA welcome message | `JournalSessionManager.cs` in TransitionToVR exit | ~5 lines |
| XRGrabInteractable + Rigidbody on bottle | Inspector on bottle prefab + `JournalReviewController.cs` | ~20 lines |
| Splash particle + SFX on throw | New particle prefab + hook in bottle's Y-threshold detection | ~10 lines |
| Haptics on bottle grab/release | Hook into XRGrabInteractable events in `JournalReviewController.cs` | ~15 lines |

### Modify (Small Changes)
| Modification | Where | Scope |
|--------------|-------|-------|
| Done button confirmation (2-tap or hold) | `JournalDoneButton.cs` | ~30 lines |
| EMILIA farewell text before final fade | `JournalReviewController.cs` in Complete transition | ~5 lines |

---

## What the User Feels at Each Stage

| Phase | Emotion Being Targeted | Mechanism |
|-------|------------------------|-----------|
| Passthrough grounding | Calm, present | Proprioceptive touch, familiar real-world view |
| Surface confirmation | Small satisfaction, readiness | Color feedback, progress completion |
| Preview + VR transition | Spatial trust, safety | Matching real and virtual space, ceremonial fade |
| EMILIA welcome | Welcomed, not alone | Warm text, companionship without intrusion |
| Writing | Freedom, release | No constraints, no audience, blank page permission |
| Done confirmation | Intentional, ready | Active confirmation = conscious choice |
| Fade to EMILIA | Threshold crossing | Ceremony of transition |
| AI reflection | Seen, validated | Unconditional positive regard from EMILIA |
| The choice | Agency, ownership | Equal-weight options, no "wrong" answer |
| Keep → rack | Curation, pride | Deliberate physical placement |
| Release → throw | Release, lightness | Embodied expulsion gesture + multisensory closure |
| Farewell | Dignity, completion | Warm acknowledgment of showing up |

---

*Last updated: 2026-03-28*
*Frameworks: Pennebaker expressive writing therapy, Rogers person-centered therapy, embodied cognition (Lakoff & Johnson), ritual completion theory (Arndt et al.)*
