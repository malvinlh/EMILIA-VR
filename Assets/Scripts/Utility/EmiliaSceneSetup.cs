#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Idempotent editor utility for the Bedroom journaling scene.
/// Run Tools → EMILIA → Bedroom: Stamp PostJournal Paper + Shredder after copying
/// the journaling stack from the Beach scene.
/// </summary>
public static class EmiliaSceneSetup
{
    private const string PostGroupName    = "PostJournal_PaperGroup";
    private const string PaperName        = "Paper";
    private const string ShredderRootName = "PaperShredder_Root";
    private const string SlotName         = "Slot";
    private const string SlotTopName      = "SlotTop";
    private const string StripsOriginName = "StripsSpawnOrigin";
    private const string WaxStamperName   = "WaxStamper";
    private const string StamperTipName   = "StamperTip";
    private const string PaperTag         = "JournalBottle";

    [MenuItem("Tools/EMILIA/Bedroom: Stamp PostJournal Paper + Shredder")]
    public static void StampBedroomPostJournal()
    {
        int created = 0, wired = 0;

        // ── 1. PostJournal paper group ─────────────────────────────────────
        var postGroup = FindOrCreate(PostGroupName, ref created);
        var paper = FindOrCreateChild(postGroup.transform, PaperName, ref created, () =>
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = PaperName;
            go.transform.localScale = new Vector3(0.21f, 0.003f, 0.297f);
            EnsureTag(PaperTag);
            go.tag = PaperTag;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true; rb.useGravity = false;
            go.AddComponent<XRGrabInteractable>();
            var iarType = System.Type.GetType("ItemAutoReset, Assembly-CSharp");
            if (iarType != null) go.AddComponent(iarType);
            return go;
        });

        // ── 2. WaxStamper (DONE gesture) ───────────────────────────────────
        var waxStamperGo = GameObject.Find(WaxStamperName);
        if (waxStamperGo == null)
        {
            waxStamperGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            waxStamperGo.name = WaxStamperName;
            waxStamperGo.transform.localScale = new Vector3(0.04f, 0.03f, 0.03f);
            waxStamperGo.transform.position = paper.position + new Vector3(0.25f, 0.02f, 0f);
            var r = waxStamperGo.GetComponent<Renderer>();
            if (r != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                r.sharedMaterial = new Material(shader) { color = new Color(0.55f, 0.08f, 0.08f, 1f) };
            }
            Undo.RegisterCreatedObjectUndo(waxStamperGo, "Create WaxStamper");
            created++;
        }

        // Ensure WaxStamper is grab-ready for both controller and hand pinches.
        var waxCol = waxStamperGo.GetComponent<Collider>() ?? waxStamperGo.AddComponent<BoxCollider>();
        var waxRb  = waxStamperGo.GetComponent<Rigidbody>() ?? waxStamperGo.AddComponent<Rigidbody>();
        waxRb.useGravity = true; waxRb.isKinematic = false;

        // Add XRGrabInteractable if missing so pinch-based NearFar/Direct interactors can pick it up.
        var waxGrab = waxStamperGo.GetComponent<XRGrabInteractable>() ?? waxStamperGo.AddComponent<XRGrabInteractable>();
        waxGrab.selectMode = InteractableSelectMode.Single;
        EditorUtility.SetDirty(waxStamperGo);

        var stamperTipXf = FindOrCreateChild(waxStamperGo.transform, StamperTipName, ref created, () =>
        {
            var go = new GameObject(StamperTipName);
            go.transform.localPosition = new Vector3(0f, -0.015f, 0f);
            return go;
        });

        var stampComp = waxStamperGo.GetComponent<JournalStampDoneButton>()
                     ?? waxStamperGo.AddComponent<JournalStampDoneButton>();

        var whiteboardGo = GameObject.Find("Whiteboard");
        if (whiteboardGo == null)
            Debug.LogWarning("[EmiliaSceneSetup] 'Whiteboard' not found — paperSurface on WaxStamper not wired. Assign manually.");

        var stampSo = new SerializedObject(stampComp);
        wired += SetIfNull(stampSo, "stamperTip",   stamperTipXf);
        if (whiteboardGo != null) wired += SetIfNull(stampSo, "paperSurface", whiteboardGo.transform);
        stampSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(stampComp);

        // ── 3. Disable legacy JournalDoneButton (beach-specific) ───────────
        foreach (var d in Object.FindObjectsByType<JournalDoneButton>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!d.enabled) continue;
            d.enabled = false;
            EditorUtility.SetDirty(d);
            Debug.LogWarning($"[EmiliaSceneSetup] Disabled JournalDoneButton on '{d.gameObject.name}' (beach-specific).");
        }

        // ── 4. Shredder hierarchy ──────────────────────────────────────────
        var shredderRoot = FindOrCreate(ShredderRootName, ref created);

        PaperShredder shredderComp;
        var slot = FindChildByName(shredderRoot.transform, SlotName);
        if (slot == null)
        {
            var slotGo = new GameObject(SlotName);
            slotGo.transform.SetParent(shredderRoot.transform, false);
            slotGo.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            var box = slotGo.AddComponent<BoxCollider>();
            box.isTrigger = true; box.size = new Vector3(0.3f, 0.05f, 0.1f);
            shredderComp = slotGo.AddComponent<PaperShredder>();
            Undo.RegisterCreatedObjectUndo(slotGo, "Create Shredder Slot");
            slot = slotGo.transform;
            created++;
        }
        else
        {
            shredderComp = slot.GetComponent<PaperShredder>()
                        ?? slot.gameObject.AddComponent<PaperShredder>();
        }

        var slotTop = FindOrCreateChild(shredderRoot.transform, SlotTopName, ref created, () =>
        {
            var go = new GameObject(SlotTopName);
            go.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            return go;
        });

        var stripsOrigin = FindOrCreateChild(shredderRoot.transform, StripsOriginName, ref created, () =>
        {
            var go = new GameObject(StripsOriginName);
            go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            return go;
        });

        var shakeTarget = FindChildByName(shredderRoot.transform, "GLTF_SceneRootNode") ?? shredderRoot.transform;

        // ── 5. Wire JournalReviewController ───────────────────────────────
        var jrc = Object.FindAnyObjectByType<JournalReviewController>();
        if (jrc != null)
        {
            var so = new SerializedObject(jrc);

            wired += SetIfNull(so, "_shredderDetector", shredderComp);
            wired += SetIfNull(so, "bottleRoot",        paper);
            wired += SetIfNull(so, "postJournalGroup",  postGroup);

            var modeProp = so.FindProperty("_mode");
            if (modeProp != null && modeProp.enumValueIndex != (int)ReviewMode.BedroomPaper)
            { modeProp.enumValueIndex = (int)ReviewMode.BedroomPaper; wired++; }

            // avatarRoot via EMILIA_dialogue_anchor parent
            var anchor = GameObject.Find("EMILIA_dialogue_anchor");
            if (anchor != null) wired += SetIfNull(so, "avatarRoot", anchor.transform.parent);
            else Debug.LogWarning("[EmiliaSceneSetup] 'EMILIA_dialogue_anchor' not found — avatarRoot not wired.");

            // standPoint
            var sp = GameObject.Find("StandPoint1");
            if (sp == null)
            {
                sp = new GameObject("StandPoint1");
                Undo.RegisterCreatedObjectUndo(sp, "Create StandPoint1");
                created++;
                Debug.LogWarning("[EmiliaSceneSetup] Created StandPoint1 at origin — move it in front of EMILIA.");
            }
            wired += SetIfNull(so, "standPoint", sp.transform);

            // _waypointController from avatarRoot
            var avatarRootProp = so.FindProperty("avatarRoot");
            if (avatarRootProp?.objectReferenceValue is Transform avatarXf)
            {
                var wp = avatarXf.GetComponent<AvatarChatWaypointPatrolController>();
                if (wp == null)
                    wp = avatarXf.GetComponentInChildren<AvatarChatWaypointPatrolController>(true);
                if (wp == null)
                    wp = Object.FindFirstObjectByType<AvatarChatWaypointPatrolController>();
                if (wp != null) wired += SetIfNull(so, "_waypointController", wp);
            }

            // rack detector
            var rack = Object.FindAnyObjectByType<WineRackProximity>();
            if (rack != null)
            {
                wired += SetIfNull(so, "_rackDetector", rack);
                var rackSo = new SerializedObject(rack);
                wired += SetIfNull(rackSo, "reviewController", jrc);
                rackSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(rack);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(jrc);
        }
        else
        {
            Debug.LogWarning("[EmiliaSceneSetup] JournalReviewController not found. " +
                             "Copy the journaling stack from 3D_Journal_Beach.unity first, then re-run.");
        }

        // ── 6. Wire PaperShredder ──────────────────────────────────────────
        if (shredderComp != null)
        {
            var so = new SerializedObject(shredderComp);
            wired += SetIfNull(so, "reviewController",  jrc);
            wired += SetIfNull(so, "slotTop",           slotTop);
            wired += SetIfNull(so, "stripsSpawnOrigin", stripsOrigin);
            wired += SetIfNull(so, "shakeTarget",       shakeTarget);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(shredderComp);
        }

        // ── 7. Disable old beach PostJournal group ─────────────────────────
        var oldPostJournal = GameObject.Find("PostJournal");
        if (oldPostJournal != null && oldPostJournal.name == "PostJournal" && oldPostJournal.activeSelf)
        {
            oldPostJournal.SetActive(false);
            EditorUtility.SetDirty(oldPostJournal);
            Debug.LogWarning("[EmiliaSceneSetup] Disabled old 'PostJournal' (beach CorkPost/BottlePost). " +
                             "You may delete it — it is replaced by PostJournal_PaperGroup.");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[EmiliaSceneSetup] Done — created {created} object(s), wired {wired} reference(s). " +
                  $"JRC present: {(jrc != null ? "yes" : "NO — copy journaling stack first")}. " +
                  "Save the scene.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static GameObject FindOrCreate(string name, ref int created)
    {
        var go = GameObject.Find(name);
        if (go != null) return go;
        go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        created++;
        return go;
    }

    private static Transform FindOrCreateChild(Transform parent, string childName,
        ref int created, System.Func<GameObject> factory)
    {
        var existing = FindChildByName(parent, childName);
        if (existing != null) return existing;
        var go = factory();
        go.name = childName;
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {childName}");
        created++;
        return go.transform;
    }

    private static Transform FindChildByName(Transform parent, string childName)
    {
        if (parent == null) return null;
        foreach (Transform t in parent)
            if (t.name == childName) return t;
        return null;
    }

    private static int SetIfNull(SerializedObject so, string prop, Object value)
    {
        if (value == null) return 0;
        var p = so.FindProperty(prop);
        if (p == null || p.objectReferenceValue != null) return 0;
        p.objectReferenceValue = value;
        return 1;
    }

    private static void EnsureTag(string tag)
    {
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (asset == null || asset.Length == 0) return;
        var so = new SerializedObject(asset[0]);
        var tags = so.FindProperty("tags");
        if (tags == null) return;
        for (int i = 0; i < tags.arraySize; i++)
            if (tags.GetArrayElementAtIndex(i).stringValue == tag) return;
        tags.arraySize++;
        tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
#endif
