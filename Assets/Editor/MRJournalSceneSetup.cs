using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Editor utility to auto-wire the MR Journaling system in the current scene.
/// Finds existing GameObjects by name and connects all inspector references.
///
/// Menu: Tools > MR Journal > Setup Scene References
/// </summary>
public static class MRJournalSceneSetup
{
    [MenuItem("Tools/MR Journal/Setup Scene References")]
    public static void SetupSceneReferences()
    {
        // Find JournalSessionManager
        var sessionMgr = Object.FindAnyObjectByType<JournalSessionManager>();
        if (sessionMgr == null)
        {
            Debug.LogError("[MR Setup] No JournalSessionManager found in scene.");
            return;
        }

        var so = new SerializedObject(sessionMgr);
        int wired = 0;

        // PassthroughManager
        if (sessionMgr.passthroughManager == null)
        {
            var pt = Object.FindAnyObjectByType<PassthroughManager>();
            if (pt != null) { sessionMgr.passthroughManager = pt; wired++; }
        }

        // ARTableDetector
        if (sessionMgr.arTableDetector == null)
        {
            var det = Object.FindAnyObjectByType<ARTableDetector>();
            if (det != null) { sessionMgr.arTableDetector = det; wired++; }
        }

        // CalibrationGuide
        if (sessionMgr.calibrationGuide == null)
        {
            var cg = Object.FindAnyObjectByType<CalibrationGuide>();
            if (cg != null) { sessionMgr.calibrationGuide = cg; wired++; }
        }

        // AlignmentAnchor
        if (sessionMgr.alignmentAnchor == null)
        {
            var aa = Object.FindAnyObjectByType<AlignmentAnchor>();
            if (aa != null) { sessionMgr.alignmentAnchor = aa; wired++; }
        }

        // WhiteboardUtils
        if (sessionMgr.whiteboardUtils == null)
        {
            var wb = Object.FindAnyObjectByType<WhiteboardUtils>();
            if (wb != null) { sessionMgr.whiteboardUtils = wb; wired++; }
        }

        // JournalStartButton
        if (sessionMgr.startButton == null)
        {
            var btn = Object.FindAnyObjectByType<JournalStartButton>();
            if (btn != null) { sessionMgr.startButton = btn; wired++; }
        }

        // ARPlaneManager
        if (sessionMgr.arPlaneManager == null)
        {
            var apm = Object.FindAnyObjectByType<ARPlaneManager>();
            if (apm != null) { sessionMgr.arPlaneManager = apm; wired++; }
        }

        // Scene Objects by name
        if (sessionMgr.journalChairTable == null)
        {
            var go = GameObject.Find("JournalChairTable");
            if (go != null) { sessionMgr.journalChairTable = go.transform; wired++; }
        }

        if (sessionMgr.journalTable == null)
        {
            var go = GameObject.Find("JournalTable");
            if (go != null) { sessionMgr.journalTable = go.transform; wired++; }
        }

        if (sessionMgr.chair == null)
        {
            var go = GameObject.Find("Chair");
            if (go != null) { sessionMgr.chair = go.transform; wired++; }
        }

        if (sessionMgr.seatPoint == null)
        {
            var go = GameObject.Find("SeatPoint");
            if (go != null) { sessionMgr.seatPoint = go.transform; wired++; }
        }

        // XR Origin
        if (sessionMgr.xrOrigin == null)
        {
            var go = GameObject.Find("XR Origin (XR Rig)");
            if (go != null) { sessionMgr.xrOrigin = go.transform; wired++; }
        }

        // ── Wire ARTableDetector.xrOrigin ────────────────────────────
        if (sessionMgr.arTableDetector != null)
        {
            var det = sessionMgr.arTableDetector;
            if (det.xrOrigin == null && sessionMgr.xrOrigin != null)
            {
                det.xrOrigin = sessionMgr.xrOrigin;
                EditorUtility.SetDirty(det);
                wired++;
            }
        }

        // ── Wire CalibrationGuide references ────────────────────────
        if (sessionMgr.calibrationGuide != null)
        {
            var cg = sessionMgr.calibrationGuide;
            if (cg.tableDetector == null && sessionMgr.arTableDetector != null)
            {
                cg.tableDetector = sessionMgr.arTableDetector;
                EditorUtility.SetDirty(cg);
                wired++;
            }
            if (cg.passthroughManager == null && sessionMgr.passthroughManager != null)
            {
                cg.passthroughManager = sessionMgr.passthroughManager;
                EditorUtility.SetDirty(cg);
                wired++;
            }

            // Add SurfaceDotGrid if not present
            if (cg.dotGrid == null)
            {
                var dotGrid = cg.GetComponent<SurfaceDotGrid>();
                if (dotGrid == null)
                    dotGrid = cg.gameObject.AddComponent<SurfaceDotGrid>();
                cg.dotGrid = dotGrid;
                EditorUtility.SetDirty(cg);
                wired++;
            }
        }

        // ── Wire AlignmentAnchor.targetToAlign ──────────────────────
        if (sessionMgr.alignmentAnchor != null)
        {
            var aa = sessionMgr.alignmentAnchor;
            if (aa.targetToAlign == null && sessionMgr.journalChairTable != null)
            {
                aa.targetToAlign = sessionMgr.journalChairTable;
                EditorUtility.SetDirty(aa);
                wired++;
            }
        }

        // Mark dirty and save
        EditorUtility.SetDirty(sessionMgr);

        Debug.Log($"[MR Setup] Wired {wired} reference(s) on JournalSessionManager. " +
                  "Check inspector to verify. Manual setup still needed:\n" +
                  "  - Assign whiteboardPlaceholder (Box Collider on virtual table surface)\n" +
                  "  - useRealEyeHeight is ON by default (captures player's real eye Y at calibration)\n" +
                  "  - Optionally assign palmIndicatorPrefab on CalibrationGuide");
    }
}
