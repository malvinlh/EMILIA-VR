using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Rich visual feedback during the journal calibration phase.
///
/// Highlights AR plane candidates with outlines, shows palm indicator spheres,
/// displays a progress ring during confirmation, and provides billboard
/// instruction text that follows the user's head.
///
/// All visual objects are set to the passthrough UI layer so they render
/// during passthrough mode.
/// </summary>
public class CalibrationGuide : MonoBehaviour
{
    [Header("References")]
    public ARTableDetector tableDetector;
    public PassthroughManager passthroughManager;

    [Header("Prefabs")]
    [Tooltip("Prefab for palm indicator spheres. Reuses MarkingSphere.")]
    public GameObject palmIndicatorPrefab;

    [Header("Instruction Text")]
    [Tooltip("Distance from user's head for instruction text.")]
    public float instructionDistance = 1.2f;
    [Tooltip("Height offset above eye level.")]
    public float instructionHeight = 0.1f;
    [Tooltip("Font size for instruction text.")]
    public float fontSize = 0.4f;

    [Header("Plane Highlight")]
    [Tooltip("Color for non-selected candidate plane outlines.")]
    public Color candidateColor = new Color(1f, 1f, 1f, 0.3f);
    [Tooltip("Color for the best-scoring candidate plane outline.")]
    public Color bestCandidateColor = new Color(0.3f, 0.9f, 0.4f, 0.5f);

    [Header("Palm Indicator Colors")]
    public Color palmIdleColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);
    public Color palmFlatColor = new Color(1f, 0.85f, 0.2f, 0.9f);
    public Color palmConfirmingColor = new Color(0.3f, 0.9f, 0.3f, 0.95f);

    [Header("Surface Dot Grid")]
    [Tooltip("SurfaceDotGrid component for Quest-style dot visualization. " +
             "If assigned, dots appear around palms when flat on the surface.")]
    public SurfaceDotGrid dotGrid;

    // ── State ────────────────────────────────────────────────────────
    private TextMeshPro instructionText;
    private GameObject leftPalmIndicator;
    private GameObject rightPalmIndicator;
    private GameObject progressIndicator;
    private List<GameObject> planeHighlights = new List<GameObject>();
    private int passthroughLayer;
    private bool isActive;
    private float confirmationProgress;

    // ================================================================
    // PUBLIC API
    // ================================================================

    public void Show()
    {
        isActive = true;
        passthroughLayer = passthroughManager != null
            ? passthroughManager.GetPassthroughUILayer() : 31;

        EnsureInstructionText();
        EnsurePalmIndicators();
        EnsureProgressIndicator();

        if (dotGrid != null)
            dotGrid.Initialise(passthroughLayer);

        SetInstruction("Place both hands flat on your table.");

        if (tableDetector != null)
        {
            tableDetector.OnCandidatesUpdated += OnCandidatesUpdated;
            tableDetector.OnConfirmationProgress += OnProgress;
            tableDetector.OnTableConfirmed += OnConfirmed;
            tableDetector.OnConfirmationLost += OnLost;
        }
    }

    public void Hide()
    {
        isActive = false;

        if (tableDetector != null)
        {
            tableDetector.OnCandidatesUpdated -= OnCandidatesUpdated;
            tableDetector.OnConfirmationProgress -= OnProgress;
            tableDetector.OnTableConfirmed -= OnConfirmed;
            tableDetector.OnConfirmationLost -= OnLost;
        }

        HideInstruction();
        HidePalmIndicators();
        HideProgressIndicator();
        HideDotGrid();
        ClearPlaneHighlights();
    }

    public void SetInstruction(string message)
    {
        if (instructionText == null) EnsureInstructionText();
        instructionText.text = message;
        instructionText.gameObject.SetActive(true);
    }

    public void HideInstruction()
    {
        if (instructionText != null)
            instructionText.gameObject.SetActive(false);
    }

    /// <summary>
    /// Update palm indicators based on current hand tracking state.
    /// Call from JournalSessionManager.Update() during detection phases.
    /// </summary>
    public void UpdatePalmIndicators(
        bool leftTracked, Vector3 leftPalmPos, bool leftFlat,
        bool rightTracked, Vector3 rightPalmPos, bool rightFlat)
    {
        if (!isActive) return;

        UpdatePalmIndicator(leftPalmIndicator, leftTracked, leftPalmPos, leftFlat);
        UpdatePalmIndicator(rightPalmIndicator, rightTracked, rightPalmPos, rightFlat);

        bool bothFlat = leftTracked && rightTracked && leftFlat && rightFlat;

        // Surface dot grid — Quest keyboard-style dots around palms
        if (dotGrid != null)
        {
            if (bothFlat)
                dotGrid.UpdateGrid(leftPalmPos, rightPalmPos, confirmationProgress);
            else
                dotGrid.Hide();
        }

        // Progress indicator between hands
        if (bothFlat && progressIndicator != null)
        {
            Vector3 mid = (leftPalmPos + rightPalmPos) / 2f + Vector3.up * 0.05f;
            progressIndicator.transform.position = mid;
            progressIndicator.SetActive(true);
        }
        else if (progressIndicator != null)
        {
            progressIndicator.SetActive(false);
        }
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void LateUpdate()
    {
        if (!isActive) return;

        // Billboard instruction text — follow user's head
        if (instructionText != null && instructionText.gameObject.activeSelf)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 forward = cam.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.001f)
                    forward.Normalize();
                else
                    forward = Vector3.forward;

                instructionText.transform.position =
                    cam.transform.position + forward * instructionDistance
                    + Vector3.up * instructionHeight;
                instructionText.transform.rotation = Quaternion.LookRotation(forward);
            }
        }
    }

    private void OnDestroy()
    {
        Hide();
        if (instructionText != null) Destroy(instructionText.gameObject);
        if (leftPalmIndicator != null) Destroy(leftPalmIndicator);
        if (rightPalmIndicator != null) Destroy(rightPalmIndicator);
        if (progressIndicator != null) Destroy(progressIndicator);
        if (dotGrid != null) dotGrid.Cleanup();
        ClearPlaneHighlights();
    }

    // ================================================================
    // EVENT HANDLERS
    // ================================================================

    private void OnCandidatesUpdated(List<ARTableDetector.CandidatePlane> candidates)
    {
        ClearPlaneHighlights();

        for (int i = 0; i < candidates.Count && i < 5; i++)
        {
            var plane = candidates[i].plane;
            var highlight = CreatePlaneHighlight(plane, i == 0);
            planeHighlights.Add(highlight);
        }
    }

    private void OnProgress(float progress)
    {
        confirmationProgress = progress;
        SetInstruction($"Hold steady... {Mathf.RoundToInt(progress * 100)}%");

        // Scale + color progress indicator
        if (progressIndicator != null)
        {
            float scale = Mathf.Lerp(0.01f, 0.05f, progress);
            progressIndicator.transform.localScale = Vector3.one * scale;

            var rend = progressIndicator.GetComponent<Renderer>();
            if (rend != null)
                rend.material.color = Color.Lerp(Color.yellow, Color.green, progress);
        }

        // Update palm indicators to confirming color
        SetPalmColor(leftPalmIndicator, Color.Lerp(palmFlatColor, palmConfirmingColor, progress));
        SetPalmColor(rightPalmIndicator, Color.Lerp(palmFlatColor, palmConfirmingColor, progress));
    }

    private void OnConfirmed(ARTableDetector.DetectedTable table)
    {
        SetInstruction("Table confirmed! Transitioning...");
        HidePalmIndicators();
        HideProgressIndicator();
        ClearPlaneHighlights();

        if (dotGrid != null)
            dotGrid.FlashConfirmed();
    }

    private void OnLost()
    {
        confirmationProgress = 0f;
        SetInstruction("Place both hands flat on your table.");

        if (progressIndicator != null)
            progressIndicator.SetActive(false);

        HideDotGrid();
    }

    // ================================================================
    // VISUAL ELEMENT CREATION
    // ================================================================

    private void EnsureInstructionText()
    {
        if (instructionText != null) return;

        var obj = new GameObject("CalibrationInstruction");
        instructionText = obj.AddComponent<TextMeshPro>();
        instructionText.fontSize = fontSize;
        instructionText.alignment = TextAlignmentOptions.Center;
        instructionText.color = new Color(0.95f, 0.92f, 0.85f);
        instructionText.rectTransform.sizeDelta = new Vector2(1.4f, 0.5f);
        instructionText.enableWordWrapping = true;
        obj.layer = passthroughLayer;
        obj.SetActive(false);
    }

    private void EnsurePalmIndicators()
    {
        if (palmIndicatorPrefab == null) return;

        if (leftPalmIndicator == null)
        {
            leftPalmIndicator = Instantiate(palmIndicatorPrefab);
            leftPalmIndicator.name = "LeftPalmIndicator";
            PassthroughManager.SetLayerRecursive(leftPalmIndicator, passthroughLayer);
            leftPalmIndicator.SetActive(false);
        }
        if (rightPalmIndicator == null)
        {
            rightPalmIndicator = Instantiate(palmIndicatorPrefab);
            rightPalmIndicator.name = "RightPalmIndicator";
            PassthroughManager.SetLayerRecursive(rightPalmIndicator, passthroughLayer);
            rightPalmIndicator.SetActive(false);
        }
    }

    private void EnsureProgressIndicator()
    {
        if (palmIndicatorPrefab == null) return;

        if (progressIndicator == null)
        {
            progressIndicator = Instantiate(palmIndicatorPrefab);
            progressIndicator.name = "ConfirmProgressIndicator";
            PassthroughManager.SetLayerRecursive(progressIndicator, passthroughLayer);
            progressIndicator.transform.localScale = Vector3.one * 0.01f;
            progressIndicator.SetActive(false);
        }
    }

    private void UpdatePalmIndicator(GameObject indicator, bool tracked, Vector3 pos, bool flat)
    {
        if (indicator == null) return;

        if (!tracked)
        {
            indicator.SetActive(false);
            return;
        }

        indicator.SetActive(true);
        indicator.transform.position = pos + Vector3.up * 0.02f;
        indicator.transform.localScale = Vector3.one * 0.02f;

        SetPalmColor(indicator, flat ? palmFlatColor : palmIdleColor);
    }

    private void SetPalmColor(GameObject indicator, Color color)
    {
        if (indicator == null) return;
        var rend = indicator.GetComponent<Renderer>();
        if (rend != null)
            rend.material.color = color;
    }

    private void HidePalmIndicators()
    {
        if (leftPalmIndicator != null) leftPalmIndicator.SetActive(false);
        if (rightPalmIndicator != null) rightPalmIndicator.SetActive(false);
    }

    private void HideProgressIndicator()
    {
        if (progressIndicator != null) progressIndicator.SetActive(false);
    }

    private void HideDotGrid()
    {
        if (dotGrid != null) dotGrid.Hide();
    }

    /// <summary>
    /// Create a flat quad outline on the passthrough layer to highlight an AR plane.
    /// </summary>
    private GameObject CreatePlaneHighlight(UnityEngine.XR.ARFoundation.ARPlane plane, bool isBest)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        obj.name = $"PlaneHighlight_{plane.trackableId}";

        // Remove collider
        var col = obj.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        // Position and scale to match AR plane
        obj.transform.position = plane.transform.position + Vector3.up * 0.001f;
        obj.transform.rotation = Quaternion.Euler(90f, plane.transform.eulerAngles.y, 0f);
        obj.transform.localScale = new Vector3(plane.size.x, plane.size.y, 1f);

        // Semi-transparent material
        var rend = obj.GetComponent<Renderer>();
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetFloat("_Surface", 1f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.color = isBest ? bestCandidateColor : candidateColor;
        rend.material = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        PassthroughManager.SetLayerRecursive(obj, passthroughLayer);

        return obj;
    }

    private void ClearPlaneHighlights()
    {
        foreach (var obj in planeHighlights)
        {
            if (obj != null) Destroy(obj);
        }
        planeHighlights.Clear();
    }
}
