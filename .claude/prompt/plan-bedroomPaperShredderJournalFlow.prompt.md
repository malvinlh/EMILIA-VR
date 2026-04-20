## Plan: Bedroom Paper-Shredder Journal Flow

Implement a full journaling flow in Bedroom by reusing the Beach architecture, then switch the post-journal symbolic interaction to paper handling: keep path goes to rack/bookshelf, discard path goes to shredder. Keep Beach unchanged. Log thesis IV/DV metrics to CSV for all outcomes (save and discard), while DB journal persistence behavior remains unchanged.

**Steps**
1. Establish the Bedroom journaling baseline by importing/wiring the same core runtime stack used in Beach (JournalSessionManager, JournalDoneButton, JournalReviewController, whiteboard UI stack, post-journal group, start button, seat/stand points). This is required before symbolic interaction changes. (blocks all later Bedroom-specific work)
2. Disable chat runtime path in Bedroom during the journaling experiment flow (VRChatBridge + chat mic interactions), so journaling microphone input is isolated and deterministic. (depends on 1)
3. Extend JournalReviewController to support a paper-shredder profile while preserving current bottle-ocean behavior for Beach. Add a configurable interaction mode and routing so Beach keeps current path and Bedroom uses paper mode. (depends on 1)
4. In paper mode, keep the Keep/Discard choice panel, skip cork-specific requirements, and route:
keep -> waiting for rack/bookshelf terminal;
discard -> waiting for shredder terminal.
Keep existing save behavior for keep and discard behavior for shredder. (depends on 3)
5. Add a dedicated discard detector for Bedroom shredder (new script, trigger/collision-safe, tag-based hierarchy match, one-shot guard, optional SFX), calling a paper-discard handler in JournalReviewController. (depends on 4)
6. Reuse WineRackProximity for Bedroom save path by assigning the paper item tag and attaching a trigger zone to the rack/bookshelf object. (depends on 4; can run parallel with step 5)
7. Add/prepare a code-only paper post-journal interactable in Bedroom (rigidbody + collider + XR grab + auto-reset + tag), and ensure JournalReviewController references this object in paper mode. (depends on 4; can run parallel with steps 5-6)
8. Add session metrics capture in JournalSessionManager for thesis variables:
- symbolic condition (BeachBottleOcean vs BedroomPaperShredder)
- modality flags (handwriting_used, microphone_used)
- timing checkpoints (review start, choice made, terminal complete, derived durations)
- terminal metadata (keep/rack or discard/shredder)
This metrics capture must run regardless of save/discard outcome. (depends on 3)
9. Instrument modality usage:
- mark handwriting when OnTextRecognized commits recognized text;
- mark microphone when a non-empty transcript is injected.
Use explicit calls into JournalSessionManager so metrics remain centralized. (depends on 8)
10. Add a CSV experiment logger utility (append-only, header-once, safe IO fallback) and write one row per completed session for both save and discard outcomes. Keep DB schema unchanged for these IV/DV metrics. (depends on 8-9)
11. Wire Bedroom scene references for new scripts/components and verify all serialized references are valid (no null session manager references on active writing/mic components in journaling flow). (depends on 1-10)

**Relevant files**
- Assets/Scripts/MixedReality/JournalReviewController.cs - add paper-mode flow routing, shredder completion handler, and terminal-specific completion metadata.
- Assets/Scripts/MixedReality/WineRackProximity.cs - reuse for Bedroom save path via paper tag/config.
- Assets/Scripts/MixedReality/SeaBottleDetector.cs - keep behavior unchanged for Beach (verify no regression).
- Assets/Scripts/MixedReality/JournalSessionManager.cs - capture per-session IV/DV metrics and call CSV logger after review completion.
- Assets/Scripts/Handwriting/ScribbleManager.cs - signal handwriting modality usage on recognized-text commit.
- Assets/Scripts/Handwriting/JournalMicController.cs - signal microphone modality usage when transcript is committed.
- Assets/Scripts/MixedReality/PaperShredderDetector.cs (new) - shredder terminal detector for discard path in Bedroom.
- Assets/Scripts/MixedReality/JournalExperimentCsvLogger.cs (new) - CSV append utility under Application.persistentDataPath.
- Assets/Scenes/use/3D_Journal_Bedroom.unity - full journaling stack wiring, paper object, rack trigger, shredder trigger, chat disable for experiment flow.
- Assets/Scenes/use/3D_Journal_Beach.unity - validate unchanged behavior and references.

**Verification**
1. Beach regression pass: start journal -> finish -> keep path to rack, discard path to ocean; confirm behavior unchanged.
2. Bedroom keep pass: start journal (handwriting) -> review choice keep -> place paper into rack/bookshelf trigger -> session saves journal and appends CSV row.
3. Bedroom discard pass: start journal (mic only) -> review choice discard -> place paper into shredder trigger -> session does not save journal but appends CSV row.
4. Bedroom mixed-modality pass: use handwriting + mic in one session -> CSV row contains both modality flags as true.
5. Timing validation: ensure durations are positive and terminal timestamps are populated for both keep and discard completions.
6. Null-reference sweep: run scene and verify no missing reference errors for JournalSessionManager, review controller, detectors, and mic dependencies.
7. File validation: confirm CSV exists and rows append in Application.persistentDataPath with stable column order.

**Decisions**
- Included: full journaling flow in Bedroom.
- Included: paper object as post-journal symbolic item.
- Included: keep/discard choice remains; save path uses rack (later model swap to bookshelf is art-only), discard path uses shredder.
- Included: Beach remains unchanged.
- Included: modality logging uses both flags (not dominant/first only).
- Included: IV/DV metrics stored in CSV for all sessions.
- Excluded: DB schema changes for experiment metrics.
- Excluded: 3D model/animation production (code-only interaction mechanics).

**Further Considerations**
1. If Bedroom asset hierarchy differs heavily from Beach, create a dedicated Bedroom journaling root prefab to reduce scene-level reference drift.
2. Keep CSV logger tolerant to IO failures (log warning, continue session) so experiment flow is never blocked by storage issues.
3. If later needed for analysis tooling, add a lightweight CSV export button in-editor/runtime, not as part of core interaction flow.