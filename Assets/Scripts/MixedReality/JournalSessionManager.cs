using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Orchestrates the Mixed Reality journaling flow:
///   Idle → Passthrough → SurfaceDetection → Aligning → TransitionToVR → Journaling
///
/// The session starts when JournalStartButton is pressed (works with hand tracking AND controllers).
/// Attach to the JournalChairTable parent object.
/// Requires references to PassthroughManager, SurfaceDetector, WhiteboardUtils, and JournalStartButton.
/// </summary>
public class JournalSessionManager : MonoBehaviour
{
    public enum SessionState
    {
        Idle,
        Passthrough,
        SurfaceDetection,
        Aligning,
        TransitionToVR,
        Journaling,
        Ending
    }

    [Header("References")]
    public PassthroughManager passthroughManager;
    public SurfaceDetector surfaceDetector;
    public WhiteboardUtils whiteboardUtils;
    public JournalStartButton startButton;

    [Header("Scene Objects")]
    [Tooltip("The JournalChairTable parent that will be repositioned.")]
    public Transform journalChairTable;
    [Tooltip("The JournalTable child (used for alignment reference).")]
    public Transform journalTable;
    [Tooltip("The Chair child.")]
    public Transform chair;

    [Header("UI")]
    [Tooltip("World-space TextMeshPro for instruction prompts. Created at runtime if null.")]
    public TextMeshPro instructionText;

    [Header("Alignment Settings")]
    [Tooltip("Duration of the alignment animation (seconds).")]
    [Range(0.1f, 2f)]
    public float alignmentDuration = 0.5f;

    [Tooltip("How long the translucent preview is shown before transitioning back to VR.")]
    [Range(0.5f, 5f)]
    public float previewDuration = 1.5f;

    [Header("Journal Whiteboard")]
    [Tooltip("Background colour for the journal whiteboard (warm cream).")]
    public Color journalBackgroundColor = new Color(1f, 0.97f, 0.92f);

    [Header("Fallback")]
    [Tooltip("Seconds to wait in SurfaceDetection before offering fallback spawn.")]
    [Range(5f, 30f)]
    public float detectionTimeout = 15f;

    // ── State ───────────────────────────────────────────────────────────
    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    private Vector3 originalChairTablePosition;
    private Quaternion originalChairTableRotation;
    private float detectionTimeoutTimer;
    private GameObject spawnedWhiteboard;

    private void Start()
    {
        // Save original positions for reset
        if (journalChairTable != null)
        {
            originalChairTablePosition = journalChairTable.position;
            originalChairTableRotation = journalChairTable.rotation;
        }

        // Wire up start button (hand poke or controller select)
        if (startButton != null)
        {
            startButton.OnButtonPressed += OnStartButtonPressed;
        }

        // Wire up surface detector
        if (surfaceDetector != null)
        {
            surfaceDetector.OnTableDetected += OnTableDetected;
            surfaceDetector.OnDetectionLost += OnDetectionLost;
            surfaceDetector.enabled = false; // Only active during detection state
        }

        // Create instruction text if not assigned
        if (instructionText == null)
        {
            CreateInstructionText();
        }
        HideInstruction();
    }

    private void Update()
    {
        switch (CurrentState)
        {
            case SessionState.SurfaceDetection:
                detectionTimeoutTimer += Time.deltaTime;
                if (detectionTimeoutTimer >= detectionTimeout)
                {
                    // Fallback: spawn at default position
                    Debug.Log("[JournalSession] Detection timed out — using fallback spawn.");
                    FallbackSpawn();
                }
                break;
        }
    }

    // ================================================================
    // STATE TRANSITIONS
    // ================================================================

    private void OnStartButtonPressed()
    {
        if (CurrentState != SessionState.Idle) return;

        CurrentState = SessionState.Passthrough;

        // Hide the book/button — session has begun
        SetButtonVisible(false);

        ShowInstruction("Switching to your real surroundings...");

        if (passthroughManager != null)
        {
            passthroughManager.EnterPassthrough(() => EnterSurfaceDetection());
        }
        else
        {
            // No passthrough available — go directly to detection
            EnterSurfaceDetection();
        }
    }

    private void EnterSurfaceDetection()
    {
        CurrentState = SessionState.SurfaceDetection;
        detectionTimeoutTimer = 0f;

        ShowInstruction("Place both hands flat on your table.");

        if (surfaceDetector != null)
        {
            surfaceDetector.ResetState();
            surfaceDetector.enabled = true;
        }
    }

    private void OnTableDetected(SurfaceDetector.TablePlane table)
    {
        if (CurrentState != SessionState.SurfaceDetection) return;

        surfaceDetector.enabled = false;
        CurrentState = SessionState.Aligning;

        ShowInstruction("Table detected! Aligning your journal...");

        StartCoroutine(AlignAndTransition(table));
    }

    private void OnDetectionLost()
    {
        // User lifted hands — just reset timer and keep waiting
        detectionTimeoutTimer = 0f;
    }

    private IEnumerator AlignAndTransition(SurfaceDetector.TablePlane table)
    {
        // Animate JournalChairTable to align with detected table
        yield return AlignCoroutine(table);

        // Show preview of the aligned table for a moment
        ShowInstruction("You can sit down now.");
        yield return new WaitForSeconds(previewDuration);

        // Transition back to VR
        CurrentState = SessionState.TransitionToVR;
        HideInstruction();

        if (passthroughManager != null)
        {
            passthroughManager.ExitPassthrough(() =>
            {
                SpawnJournalWhiteboard(table);
            });
        }
        else
        {
            SpawnJournalWhiteboard(table);
        }
    }

    private IEnumerator AlignCoroutine(SurfaceDetector.TablePlane table)
    {
        if (journalChairTable == null) yield break;

        // Calculate offset: move JournalChairTable so that JournalTable
        // ends up at the detected table position.
        Vector3 tableChildLocalPos = journalTable != null
            ? journalTable.localPosition
            : Vector3.zero;

        // Target position for the parent, accounting for child offset
        Vector3 targetParentPos = table.position - journalChairTable.rotation * tableChildLocalPos;
        // Match the detected table's Y for the table surface
        targetParentPos.y = table.position.y - (journalTable != null ? journalTable.localPosition.y : 0f);

        Quaternion targetParentRot = table.rotation;

        Vector3 startPos = journalChairTable.position;
        Quaternion startRot = journalChairTable.rotation;

        float elapsed = 0f;
        while (elapsed < alignmentDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / alignmentDuration);
            // Smoothstep easing
            t = t * t * (3f - 2f * t);

            journalChairTable.position = Vector3.Lerp(startPos, targetParentPos, t);
            journalChairTable.rotation = Quaternion.Slerp(startRot, targetParentRot, t);
            yield return null;
        }

        journalChairTable.position = targetParentPos;
        journalChairTable.rotation = targetParentRot;
    }

    private void SpawnJournalWhiteboard(SurfaceDetector.TablePlane table)
    {
        CurrentState = SessionState.Journaling;

        if (whiteboardUtils != null)
        {
            // Set the journal background colour on the prefab before spawning
            var wbComponent = whiteboardUtils.WhiteboardPrefab.GetComponent<Whiteboard>();
            if (wbComponent != null)
            {
                wbComponent.backgroundColor = journalBackgroundColor;
            }

            // Spawn the whiteboard flat on the table
            Vector3 spawnPos = table.position + Vector3.up * 0.002f;
            spawnedWhiteboard = whiteboardUtils.SpawnAligned(spawnPos, table.rotation, table.size);
        }

        Debug.Log("[JournalSession] Journaling session started.");
    }

    // ================================================================
    // FALLBACK
    // ================================================================

    private void FallbackSpawn()
    {
        if (surfaceDetector != null)
            surfaceDetector.enabled = false;

        HideInstruction();

        // Exit passthrough if active
        if (passthroughManager != null && passthroughManager.IsPassthroughActive)
        {
            passthroughManager.ExitPassthrough(() =>
            {
                SpawnAtDefaultPosition();
            });
        }
        else
        {
            SpawnAtDefaultPosition();
        }
    }

    private void SpawnAtDefaultPosition()
    {
        CurrentState = SessionState.Journaling;

        if (whiteboardUtils != null && journalTable != null)
        {
            var wbComponent = whiteboardUtils.WhiteboardPrefab.GetComponent<Whiteboard>();
            if (wbComponent != null)
                wbComponent.backgroundColor = journalBackgroundColor;

            // Use the JournalTable's current position at default height
            Vector3 pos = journalTable.position + Vector3.up * 0.002f;
            Quaternion rot = Quaternion.LookRotation(journalTable.forward, Vector3.up);
            Vector2 size = new Vector2(0.4f, 0.3f); // reasonable default

            spawnedWhiteboard = whiteboardUtils.SpawnAligned(pos, rot, size);
        }

        Debug.Log("[JournalSession] Fallback: spawned whiteboard at default position.");
    }

    // ================================================================
    // END SESSION (graceful — user finished journaling)
    // ================================================================

    /// <summary>
    /// Gracefully end the journaling session. Destroys the whiteboard,
    /// restores JournalChairTable to its original position, and re-shows
    /// the start button so the user can begin a new session later.
    ///
    /// Call from a finish gesture (e.g., left-hand pinky-thumb pinch)
    /// or from a UI "Done" button.
    /// </summary>
    public void EndSession()
    {
        if (CurrentState != SessionState.Journaling) return;

        CurrentState = SessionState.Ending;
        StartCoroutine(EndSessionCoroutine());
    }

    private IEnumerator EndSessionCoroutine()
    {
        // Brief "closing" moment — let the user see their last stroke
        ShowInstruction("Saving your reflections...");
        yield return new WaitForSeconds(1f);

        // Destroy the whiteboard
        CleanupWhiteboard();

        HideInstruction();

        // Smoothly return JournalChairTable to original position
        if (journalChairTable != null)
        {
            Vector3 startPos = journalChairTable.position;
            Quaternion startRot = journalChairTable.rotation;
            float elapsed = 0f;

            while (elapsed < alignmentDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / alignmentDuration);
                t = t * t * (3f - 2f * t); // smoothstep

                journalChairTable.position = Vector3.Lerp(startPos, originalChairTablePosition, t);
                journalChairTable.rotation = Quaternion.Slerp(startRot, originalChairTableRotation, t);
                yield return null;
            }

            journalChairTable.position = originalChairTablePosition;
            journalChairTable.rotation = originalChairTableRotation;
        }

        // Re-show the book/button
        SetButtonVisible(true);

        CurrentState = SessionState.Idle;
        Debug.Log("[JournalSession] Session ended. Book re-enabled.");
    }

    // ================================================================
    // CANCEL SESSION (abort — user wants out mid-setup)
    // ================================================================

    /// <summary>
    /// Abort the session at any point during setup (Passthrough, SurfaceDetection,
    /// Aligning, TransitionToVR). If already in Journaling state, use EndSession() instead.
    /// </summary>
    public void CancelSession()
    {
        if (CurrentState == SessionState.Idle || CurrentState == SessionState.Ending) return;

        // If user cancels during journaling, treat it as ending the session
        if (CurrentState == SessionState.Journaling)
        {
            EndSession();
            return;
        }

        StopAllCoroutines();

        if (surfaceDetector != null)
        {
            surfaceDetector.enabled = false;
            surfaceDetector.ResetState();
        }

        CleanupWhiteboard();
        HideInstruction();

        // Return to VR if in passthrough
        if (passthroughManager != null && passthroughManager.IsPassthroughActive)
        {
            passthroughManager.ExitPassthrough(() => ResetToIdle());
        }
        else
        {
            ResetToIdle();
        }
    }

    private void ResetToIdle()
    {
        // Restore original position
        if (journalChairTable != null)
        {
            journalChairTable.position = originalChairTablePosition;
            journalChairTable.rotation = originalChairTableRotation;
        }

        // Re-show the book/button
        SetButtonVisible(true);

        CurrentState = SessionState.Idle;
    }

    // ================================================================
    // BUTTON VISIBILITY
    // ================================================================

    private void SetButtonVisible(bool visible)
    {
        if (startButton != null)
            startButton.gameObject.SetActive(visible);
    }

    // ================================================================
    // WHITEBOARD CLEANUP
    // ================================================================

    private void CleanupWhiteboard()
    {
        if (spawnedWhiteboard != null)
        {
            Destroy(spawnedWhiteboard);
            spawnedWhiteboard = null;
        }
    }

    // ================================================================
    // INSTRUCTION UI
    // ================================================================

    private void CreateInstructionText()
    {
        GameObject textObj = new GameObject("JournalInstruction");
        instructionText = textObj.AddComponent<TextMeshPro>();

        instructionText.fontSize = 0.4f;
        instructionText.alignment = TextAlignmentOptions.Center;
        instructionText.color = new Color(0.95f, 0.92f, 0.85f); // warm cream text
        instructionText.rectTransform.sizeDelta = new Vector2(1.2f, 0.4f);
        instructionText.enableWordWrapping = true;
    }

    private void ShowInstruction(string message)
    {
        if (instructionText == null) return;

        instructionText.text = message;
        instructionText.gameObject.SetActive(true);

        // Position in front of the user's gaze
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            forward.Normalize();

            instructionText.transform.position = cam.transform.position + forward * 1.5f + Vector3.up * 0.2f;
            instructionText.transform.rotation = Quaternion.LookRotation(forward);
        }
    }

    private void HideInstruction()
    {
        if (instructionText != null)
            instructionText.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (startButton != null)
        {
            startButton.OnButtonPressed -= OnStartButtonPressed;
        }

        if (surfaceDetector != null)
        {
            surfaceDetector.OnTableDetected -= OnTableDetected;
            surfaceDetector.OnDetectionLost -= OnDetectionLost;
        }
    }
}
