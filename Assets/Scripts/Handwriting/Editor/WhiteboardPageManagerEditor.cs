using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom Inspector for WhiteboardPageManager.
///
/// Adds an "Apply Layout Preview" button that computes the canvas orientation
/// from the current Scene View camera and positions the WhiteboardUI canvas
/// directly on the board surface — exactly as it will appear at runtime.
///
/// The Scene view also shows labelled overlays for:
///   ◻ Canvas bounds (white)
///   ◻ Text area (cyan)
///   ◻ Button strip (orange)
///   → textRight direction (green arrow)
///   ↑ textForward direction (blue arrow)
/// </summary>
[CustomEditor(typeof(WhiteboardPageManager))]
public class WhiteboardPageManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var pm = (WhiteboardPageManager)target;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("─── Editor Preview Tool ───", EditorStyles.boldLabel);

        bool ready = pm.uiCanvas != null && pm.whiteboard != null;
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("▶  Apply Layout Preview", GUILayout.Height(30)))
                ApplyPreview(pm);
        }

        if (!ready)
        {
            EditorGUILayout.HelpBox(
                "Assign both 'uiCanvas' and 'whiteboard' above to enable the preview.",
                MessageType.Info);
        }
        else if (pm.previewHasData)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Last preview result:", EditorStyles.miniBoldLabel);

            float w  = pm.previewPhysW;
            float h  = pm.previewPhysH;
            float bm = pm.previewBoardMargin;
            float ba = pm.previewButtonAreaReserve;

            EditorGUILayout.LabelField($"  Board size:   {w * 100f:F1} cm × {h * 100f:F1} cm");
            EditorGUILayout.LabelField($"  Canvas pixels: {w * WhiteboardPageManager.PPU:F0} × {h * WhiteboardPageManager.PPU:F0} px");
            EditorGUILayout.LabelField($"  Text area:    {(w - 2f * bm) * 100f:F1} × {(h - ba - 2f * bm) * 100f:F1} cm");
            EditorGUILayout.LabelField($"  Button strip: {ba * 100f:F1} cm tall");
        }
    }

    private void ApplyPreview(WhiteboardPageManager pm)
    {
        // Record objects that will be modified for Undo support
        if (pm.uiCanvas != null && pm.uiCanvas.transform.parent != null)
            Undo.RegisterFullObjectHierarchyUndo(
                pm.uiCanvas.transform.parent.gameObject, "Preview Whiteboard Layout");

        Undo.RecordObject(pm, "Preview Whiteboard Layout");

        Camera cam = SceneView.lastActiveSceneView != null
            ? SceneView.lastActiveSceneView.camera
            : Camera.main;

        pm.PreviewLayout(cam);

        if (pm.uiCanvas != null)
            EditorUtility.SetDirty(pm.uiCanvas.transform);

        EditorUtility.SetDirty(pm);
        SceneView.RepaintAll();
    }

    private void OnSceneGUI()
    {
        var pm = (WhiteboardPageManager)target;
        if (!pm.previewHasData) return;

        Vector3 pos = pm.previewCanvasPos;
        Vector3 rt  = pm.previewTextRight;
        Vector3 fwd = pm.previewTextForward;
        float   w   = pm.previewPhysW;
        float   h   = pm.previewPhysH;
        float   bm  = pm.previewBoardMargin;
        float   ba  = pm.previewButtonAreaReserve;

        // ── Canvas outline (white) ────────────────────────────────────
        DrawRect(pos, rt, fwd, w, h, Color.white);

        // ── Text area (cyan) — inset by boardMargin; bottom raised by buttonAreaReserve
        float textAreaW = w - 2f * bm;
        float textAreaH = h - ba - 2f * bm;
        // Canvas centre is at pos. In fwd (canvas +Y), the canvas spans [-h/2, h/2].
        // Text area bottom (canvas-centred) = -h/2 + ba + bm, top = h/2 - bm.
        // Text area centre Y offset from canvas centre = ba/2.
        Vector3 textAreaCenter = pos + fwd * (ba * 0.5f);
        DrawRect(textAreaCenter, rt, fwd, textAreaW, textAreaH, Color.cyan);
        Handles.color = Color.cyan;
        Handles.Label(textAreaCenter + fwd * (textAreaH * 0.5f + 0.015f), "Text area");

        // ── Button strip (orange) ─────────────────────────────────────
        Vector3 btnCenter = pos + fwd * (-h * 0.5f + ba * 0.5f);
        DrawRect(btnCenter, rt, fwd, w, ba, new Color(1f, 0.55f, 0f));
        Handles.color = new Color(1f, 0.55f, 0f);
        Handles.Label(btnCenter + rt * (-w * 0.5f + 0.005f), "Buttons");

        // ── Canvas label ─────────────────────────────────────────────
        Handles.color = Color.white;
        Handles.Label(pos + fwd * (h * 0.5f + 0.02f), "Canvas");

        // ── Direction arrows ──────────────────────────────────────────
        float arrowLen = Mathf.Min(w, h) * 0.28f;

        // textRight → green
        Handles.color = new Color(0.2f, 1f, 0.3f);
        Handles.ArrowHandleCap(0, pos,
            Quaternion.LookRotation(rt, fwd), arrowLen, EventType.Repaint);
        Handles.Label(pos + rt * (arrowLen + 0.01f), "→ right");

        // textForward ↑ blue
        Handles.color = new Color(0.3f, 0.65f, 1f);
        Handles.ArrowHandleCap(0, pos,
            Quaternion.LookRotation(fwd, rt), arrowLen, EventType.Repaint);
        Handles.Label(pos + fwd * (arrowLen + 0.01f), "↑ forward");
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static void DrawRect(Vector3 center, Vector3 right, Vector3 up,
                                  float w, float h, Color color)
    {
        Handles.color = color;
        Vector3 hr = right * (w * 0.5f);
        Vector3 hu = up    * (h * 0.5f);
        Vector3 tl = center - hr + hu;
        Vector3 tr = center + hr + hu;
        Vector3 bl = center - hr - hu;
        Vector3 br = center + hr - hu;
        Handles.DrawLine(tl, tr);
        Handles.DrawLine(tr, br);
        Handles.DrawLine(br, bl);
        Handles.DrawLine(bl, tl);
    }
}
