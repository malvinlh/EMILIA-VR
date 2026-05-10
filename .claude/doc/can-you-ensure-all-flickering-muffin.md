# Plan: Make WaxStamper, BottlePreDuring, and VintageMicrophone buttons reliably pinch-grabbable / hand-pokeable

## Context

The user reports: with hand tracking on, the fingertip turns orange (system detects an interactable), **but no interaction fires**. Controller use still works. This needs to be fixed in:

- [Assets/Scenes/use/3D_Journal_Bedroom.unity](Assets/Scenes/use/3D_Journal_Bedroom.unity) → `WaxStamper` (pinch-grab) + `VintageMicrophone` button child (poke)
- [Assets/Scenes/use/3D_Journal_Beach.unity](Assets/Scenes/use/3D_Journal_Beach.unity) → `BottlePreDuring` (pinch-grab) + `VintageMicrophone` button child (poke)

### What's already in place (verified by reading the scene YAML)

The scenes are NOT in a "missing setup" state. Both already contain:

1. **The hands rig.** Both scenes reference `XR Origin Hands (XR Rig)` from XRIT 3.3.1 samples — prefab GUID `d6878e1999eb4b44a9f5a263af86c185`:
   - [3D_Journal_Bedroom.unity:3078](Assets/Scenes/use/3D_Journal_Bedroom.unity#L3078)
   - [3D_Journal_Beach.unity:22348](Assets/Scenes/use/3D_Journal_Beach.unity#L22348)

   This rig already provides `NearFarInteractor` (pinch-grab via near + far casting) and `XRPokeInteractor` for both hands.

2. **`VintageMicButton` + `XRSimpleInteractable` + non-trigger `BoxCollider` on the button child of VintageMicrophone in both scenes.**
   - [3D_Journal_Bedroom.unity:2148](Assets/Scenes/use/3D_Journal_Bedroom.unity#L2148) — colliders list **populated** (line 2173-2174).
   - [3D_Journal_Beach.unity:20201](Assets/Scenes/use/3D_Journal_Beach.unity#L20201) — colliders list **empty** (line 20226). ← scene asymmetry.

3. **Runtime grab setup on `WaxStamper` and `BottlePreDuring`.** [JournalStampDoneButton.cs:247-262](Assets/Scripts/MixedReality/JournalStampDoneButton.cs#L247-L262) and [JournalDoneButton.cs:309-351](Assets/Scripts/MixedReality/JournalDoneButton.cs#L309-L351) add `XRGrabInteractable` + `Rigidbody` + `ItemAutoReset` at `Awake`/runtime with default interaction layer (bit 0 = Default). This matches `NearFarInteractor.m_InteractionLayers.m_Bits = 1`, so layer filtering is **not** the blocker.

4. **`VintageMicButton` already supports both hands' poke** via `XRHandSubsystem.leftHand`/`rightHand` ([VintageMicButton.cs:221-225](Assets/Scripts/MixedReality/VintageMicButton.cs#L221-L225)).

### Diagnosis of "orange but cannot be interacted"

The orange tint is the `XRPokeInteractor`'s **hover** visual on its poke ray endpoint at the fingertip. Hover works → no select fires. Two independent reasons:

**Reason A — for the mic button (poke):**
`VintageMicButton.TryPokeWithHand` ([VintageMicButton.cs:238-246](Assets/Scripts/MixedReality/VintageMicButton.cs#L238-L246)) requires `_collider.bounds.Contains(tip.position)` — i.e., the index tip must be **inside the AABB**. With hand tracking the tip naturally stops at the surface; with a non-trivial-depth `BoxCollider` it rarely actually penetrates. So hover (which uses `ClosestPoint` distance ≤ `hoverDistance` = 6 cm) succeeds, but activation (which requires bounds containment) doesn't. The `XRPokeInteractor` from the rig also won't fire `selectEntered` on the `XRSimpleInteractable` because `XRSimpleInteractable` has no `XRPokeFilter`.

**Reason B — for pinch-grab on stamper/bottle (likely separate issue):**
Cannot be diagnosed from file inspection alone. Most likely candidates if it fails on-device:
- The pinch gesture isn't being recognized by the XR Hands subsystem (project settings issue, out of scope).
- The runtime-added `XRGrabInteractable.attachTransform` is `null` (not unconditionally a blocker, but can produce surprising behavior).
- `JournalSessionManager.CurrentState != Journaling` disables `_grab` — but this would block controller too, so it's ruled out by "controller works".

The user picked "Audit + fix scene asymmetries". The mic-button fix (Reason A) is a small surgical code change to `VintageMicButton`; it's in scope because it's the actual cause of the reported symptom and changing it doesn't alter design — it makes the existing intent (hand-poke activation) actually work.

## Changes

### 1. Loosen `VintageMicButton` poke detection from strict bounds to a small surface-distance threshold

**File:** [Assets/Scripts/MixedReality/VintageMicButton.cs](Assets/Scripts/MixedReality/VintageMicButton.cs)

**Why:** `bounds.Contains(tip)` requires the tip to be inside the AABB. Hand-tracked fingertips usually stop at the visible surface and don't penetrate. Switch to `Vector3.Distance(tip, ClosestPoint(tip)) <= pokeActivationDistance` (≈ 0.5 cm). This is the same shape as the existing `IsHandHovering` check, just with a tighter threshold dedicated to the activation event.

**Edits:**

- Add a new serialized field next to `hoverDistance` (around line 56):
  ```csharp
  [Tooltip("Distance (metres) at which a fingertip counts as 'pressing' the button. Should be smaller than hoverDistance.")]
  [Range(0.001f, 0.05f)]
  public float pokeActivationDistance = 0.005f;
  ```
- Replace `TryPokeWithHand` body ([VintageMicButton.cs:238-246](Assets/Scripts/MixedReality/VintageMicButton.cs#L238-L246)) so the activation test uses surface distance:
  ```csharp
  private bool TryPokeWithHand(XRHand hand)
  {
      if (!hand.isTracked) return false;
      var joint = hand.GetJoint(XRHandJointID.IndexTip);
      if (!joint.TryGetPose(out Pose tip)) return false;
      float surfaceDist = Vector3.Distance(tip.position, _collider.ClosestPoint(tip.position));
      if (surfaceDist > pokeActivationDistance) return false;
      TriggerActivation();
      return true;
  }
  ```

No change to `IsHandHovering` (still uses `hoverDistance`). No change to controller path. Both hands continue to work via the existing `||` chain in `Update()`.

### 2. Fix Beach scene asymmetry: empty `m_Colliders` list on the mic button's `XRSimpleInteractable`

**File:** [Assets/Scenes/use/3D_Journal_Beach.unity:20226](Assets/Scenes/use/3D_Journal_Beach.unity#L20226)

Currently:
```yaml
m_Colliders: []
```

Change to (matching Bedroom scene's [line 2173-2174](Assets/Scenes/use/3D_Journal_Bedroom.unity#L2173-L2174)):
```yaml
m_Colliders:
- {fileID: 1953748147}
```

(`1953748147` is the BoxCollider on the same GameObject — see [line 20287](Assets/Scenes/use/3D_Journal_Beach.unity#L20287).)

`VintageMicButton.Awake()` re-populates the list at runtime, so this is cosmetic in practice — but it removes a divergence between the two scenes and makes the asset state correct on disk. Easiest path: open the scene in Unity, click the button child's `XRSimpleInteractable` Inspector, drag the BoxCollider into the Colliders list, save scene. (Equivalently, run `Emilia → Apply Hand Interactors To Scenes` after change #3 below; `EnsureVintageMicButtons` will write the populated list.)

### 3. Make `EmiliaHandInteractorSetup.EnsureHandsRig` idempotent

**File:** [Assets/Scripts/Utility/Editor/EmiliaHandInteractorSetup.cs:50-58](Assets/Scripts/Utility/Editor/EmiliaHandInteractorSetup.cs#L50-L58)

**Why:** The current "is hands child already present?" check walks only the direct children of `XROrigin`. The hands rig's hand GameObjects sit deeper (under `Camera Offset`), so the check fails and a duplicate hands rig gets instantiated on re-run. Latent bug — easy to trip during this audit.

**Edit:** Replace the direct-child-name check with a prefab-source check that walks all root GameObjects in the scene:

```csharp
static GameObject FindExistingHandsRig(Scene scene, GameObject sourcePrefab)
{
    foreach (var root in scene.GetRootGameObjects())
    {
        var src = PrefabUtility.GetCorrespondingObjectFromSource(root) as GameObject;
        if (src == sourcePrefab) return root;
    }
    return null;
}
```

Then in `ApplyHandsToScenes`, replace lines 37-66 logic with:

```csharp
var existing = FindExistingHandsRig(scene, prefab);
if (existing != null)
{
    Debug.Log($"Emilia: Hands rig already present in {scenePath} (root '{existing.name}').");
    EnsureVintageMicButtons(scene);
    EditorSceneManager.SaveScene(scene);
    continue;
}

var xrOrigin = UnityEngine.Object.FindObjectOfType<XROrigin>();
GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
if (xrOrigin != null) inst.transform.SetParent(xrOrigin.transform, false);
EditorSceneManager.MarkSceneDirty(scene);
EnsureVintageMicButtons(scene);
EditorSceneManager.SaveScene(scene);
Debug.Log($"Emilia: Instantiated hands rig in {scenePath} (xrOrigin? {xrOrigin != null}).");
```

This collapses the two near-duplicate code paths and removes the false-negative on the existence check.

### 4. (No change) Pinch-grab on `WaxStamper` and `BottlePreDuring`

Setup is already correct. After change #1 lands, verify on-device. If pinch-grab fails, see "Open question if it still doesn't work" below.

## Critical files to modify

- [Assets/Scripts/MixedReality/VintageMicButton.cs](Assets/Scripts/MixedReality/VintageMicButton.cs) — change #1 (poke threshold).
- [Assets/Scenes/use/3D_Journal_Beach.unity](Assets/Scenes/use/3D_Journal_Beach.unity) — change #2 (one collider reference). Edit in Unity Inspector.
- [Assets/Scripts/Utility/Editor/EmiliaHandInteractorSetup.cs](Assets/Scripts/Utility/Editor/EmiliaHandInteractorSetup.cs) — change #3 (idempotent rig check).

No new files. No changes to `JournalStampDoneButton.cs`, `JournalDoneButton.cs`, `ItemAutoReset.cs`, or any prefab under `Assets/Samples/`.

## Verification

Run on-device with hand tracking enabled. For each target object, both hands should pass:

| Object | Test (controller — should still work) | Test (hand) | Pass criteria |
|---|---|---|---|
| WaxStamper (Bedroom, during Journaling) | Grip + select | Make pinch gesture within ~10 cm | Stamper attaches to hand, follows movement |
| BottlePreDuring (Beach, during Journaling) | Grip + select | Pinch within ~10 cm | Bottle attaches |
| VintageMicrophone button child (both scenes) | Ray + select | Touch index tip to button face | Button color flashes (cooldown), `OnActivated` fires (recording starts/stops in Console) |

For the mic button specifically — verify the symptom is resolved:
1. Approach with index tip → button material tints `hoverColor` (warm amber) and fingertip glows orange → expected.
2. Touch the surface → activation fires (audible click + `JournalMicController` toggles state). Before this fix, step 2 silently does nothing.
3. Repeat with the other hand to confirm dual-hand support.

After running `Emilia → Apply Hand Interactors To Scenes` post-change-#3, re-run it once more — the second run should log `Hands rig already present` and not modify scene root structure (idempotency check).

## Open question if pinch-grab still doesn't work after change #1

If the mic button starts working but pinch-grab on `WaxStamper`/`BottlePreDuring` still fails in headset, the most likely remaining cause is the XR Hands pinch-pose action not being routed to `NearFarInteractor`'s `selectInput`. Diagnosis path (out of scope for this plan, but recorded for future): inspect [Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/Interactors/Left_NearFarInteractor.prefab](Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/Interactors/Left_NearFarInteractor.prefab) to confirm `m_NearSelectInput` / `m_FarSelectInput` reference an Input Action bound to `<XRHandDevice>/pinchValue` or equivalent, and check Project Settings → XR Plug-in Management → OpenXR → Hand Tracking is enabled with the Hand Interaction Profile feature group.
