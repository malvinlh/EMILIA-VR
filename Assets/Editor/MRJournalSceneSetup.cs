using UnityEditor;
using UnityEngine;

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

        int wired = 0;

        // PassthroughManager
        if (sessionMgr.passthroughManager == null)
        {
            var pt = Object.FindAnyObjectByType<PassthroughManager>();
            if (pt != null) { sessionMgr.passthroughManager = pt; wired++; }
        }

        // TableTapCalibrator
        if (sessionMgr.tableTapCalibrator == null)
        {
            var tc = Object.FindAnyObjectByType<TableTapCalibrator>();
            if (tc != null) { sessionMgr.tableTapCalibrator = tc; wired++; }
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

        // StylusCalibrationController
        if (sessionMgr.stylusCalibrationController == null)
        {
            var scc = Object.FindAnyObjectByType<StylusCalibrationController>();
            if (scc != null) { sessionMgr.stylusCalibrationController = scc; wired++; }
        }

        // StylusTipProvider
        if (sessionMgr.stylusTipProvider == null)
        {
            var stp = Object.FindAnyObjectByType<StylusTipProvider>();
            if (stp != null) { sessionMgr.stylusTipProvider = stp; wired++; }
        }

        // StylusVisualProp
        if (sessionMgr.stylusVisualProp == null)
        {
            var svp = Object.FindAnyObjectByType<StylusVisualProp>();
            if (svp != null) { sessionMgr.stylusVisualProp = svp; wired++; }
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
                  "  - Assign tableWritingSurface (WhiteboardPlaceholder transform)\n" +
                  "  - Configure TableTapCalibrator visuals (tapMarkerPrefab, previewRectangle, Confirm/Redo buttons)\n" +
                  "  - Configure StylusCalibrationController (wristTracker, passthroughManager)");
    }
}
