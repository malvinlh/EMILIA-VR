using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using TMPro;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Orchestrates the Mixed Reality journaling flow:
///   Idle → Passthrough → PlaneDiscovery → HandConfirmation → Preview → TransitionToVR → Journaling
///
/// UX inspired by Meta Quest 3 AR Surface Keyboard:
///   1. Press start button → fade to passthrough (real world visible)
///   2. AR planes highlight candidate tables; user places both hands flat → surface confirmed
///   3. Whiteboard spawns on real table surface (visible in passthrough)
///   4. Brief preview → fade back to VR
///   5. Virtual world is calibrated so JournalTable = real table position, chair faces user
///   6. Spatial anchor resists tracking drift
///   7. Mid-session re-calibration available via RequestReCalibration()
///
/// Attach to the JournalChairTable parent object.
/// </summary>
public class JournalSessionManager : MonoBehaviour
{
    public enum SessionState
    {
        Idle,
        RequestingPermission,
        Passthrough,
        PlaneDiscovery,
        HandConfirmation,
        Preview,
        TransitionToVR,
        Journaling,
        ReCalibrating,
        Ending
    }

    [Header("References")]
    public PassthroughManager passthroughManager;
    public ARTableDetector arTableDetector;
    public CalibrationGuide calibrationGuide;
    public AlignmentAnchor alignmentAnchor;
    public WhiteboardUtils whiteboardUtils;
    public JournalStartButton startButton;

    [Header("AR Managers")]
    [Tooltip("ARPlaneManager for table detection. Enabled only during detection.")]
    public ARPlaneManager arPlaneManager;

    [Header("Scene Objects")]
    [Tooltip("The JournalChairTable parent that will be repositioned.")]
    public Transform journalChairTable;
    [Tooltip("The JournalTable child (used for alignment reference).")]
    public Transform journalTable;
    [Tooltip("The Chair child.")]
    public Transform chair;

    [Header("UI")]
    [Tooltip("World-space TextMeshPro for instruction prompts (fallback if CalibrationGuide is null). Created at runtime if null.")]
    public TextMeshPro instructionText;

    [Header("Timing")]
    [Tooltip("How long the whiteboard preview is shown on the real table before transitioning.")]
    [Range(0.5f, 5f)]
    public float previewDuration = 2f;

    [Header("Journal Whiteboard")]
    [Tooltip("Background colour for the journal whiteboard (warm cream).")]
    public Color journalBackgroundColor = new Color(1f, 0.97f, 0.92f);

    [Header("Fallback")]
    [Tooltip("Seconds to wait in detection before offering fallback spawn.")]
    [Range(5f, 30f)]
    public float detectionTimeout = 15f;

    // ── State ───────────────────────────────────────────────────────────
    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    private Vector3 originalChairTablePosition;
    private Quaternion originalChairTableRotation;
    private float detectionTimeoutTimer;
    private bool hasTimedOut;
    private GameObject spawnedWhiteboard;
    private ARTableDetector.DetectedTable pendingTable;
    private bool scenePermissionGranted;

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Start()
    {
        if (journalChairTable != null)
        {
            originalChairTablePosition = journalChairTable.position;
            originalChairTableRotation = journalChairTable.rotation;
        }

        if (startButton != null)
            startButton.OnButtonPressed += OnStartButtonPressed;

        if (arTableDetector != null)
        {
            arTableDetector.OnTableConfirmed += OnTableConfirmed;
            arTableDetector.OnConfirmationLost += OnConfirmationLost;
            arTableDetector.enabled = false;
        }

        // Disable AR plane manager until needed (saves performance)
        if (arPlaneManager != null)
            arPlaneManager.enabled = false;

        // Setup fallback instruction text if CalibrationGuide is not assigned
        if (calibrationGuide == null && instructionText == null)
            CreateInstructionText();

        HideInstruction();
    }

    private void Update()
    {
        // Timeout guard for detection phases
        if (CurrentState == SessionState.PlaneDiscovery
            || CurrentState == SessionState.HandConfirmation)
        {
            if (hasTimedOut) return;

            detectionTimeoutTimer += Time.deltaTime;
            if (detectionTimeoutTimer >= detectionTimeout)
            {
                hasTimedOut = true;
                Debug.Log("[JournalSession] Detection timed out — using fallback spawn.");
                FallbackSpawn();
            }
        }

        // Keep fallback instruction text facing user during passthrough
        if (calibrationGuide == null && instructionText != null
            && instructionText.gameObject.activeSelf)
        {
            UpdateInstructionPosition();
        }
    }

    // ================================================================
    // ANDROID RUNTIME PERMISSION
    // ================================================================

    /// <summary>
    /// Request com.oculus.permission.USE_SCENE at runtime.
    /// Without this, ARPlaneManager cannot detect planes on Meta Quest.
    /// </summary>
    private void RequestScenePermissionThenProceed()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string SCENE_PERMISSION = "com.oculus.permission.USE_SCENE";

        if (Permission.HasUserAuthorizedPermission(SCENE_PERMISSION))
        {
            Debug.Log("[JournalSession] USE_SCENE permission already granted.");
            scenePermissionGranted = true;
            ProceedToPassthrough();
            return;
        }

        Debug.Log("[JournalSession] Requesting USE_SCENE permission...");
        CurrentState = SessionState.RequestingPermission;

        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += (perm) =>
        {
            Debug.Log($"[JournalSession] Permission granted: {perm}");
            scenePermissionGranted = true;
            ProceedToPassthrough();
        };
        callbacks.PermissionDenied += (perm) =>
        {
            Debug.LogWarning($"[JournalSession] Permission denied: {perm}. " +
                             "Plane detection will be unavailable — using hand-only fallback.");
            scenePermissionGranted = false;
            ProceedToPassthrough();
        };

        Permission.RequestUserPermissions(new[] { SCENE_PERMISSION }, callbacks);
#else
        // In Editor or non-Android, skip permission
        scenePermissionGranted = true;
        ProceedToPassthrough();
#endif
    }

    private void ProceedToPassthrough()
    {
        CurrentState = SessionState.Passthrough;

        ShowInstruction("Switching to your real surroundings...");

        if (passthroughManager != null)
            passthroughManager.EnterPassthrough(() => EnterPlaneDiscovery());
        else
            EnterPlaneDiscovery();
    }

    // ================================================================
    // STATE TRANSITIONS
    // ================================================================

    private void OnStartButtonPressed()
    {
        Debug.Log("[JournalSession] Start button pressed. CurrentState=" + CurrentState);
        if (CurrentState != SessionState.Idle) return;

        SetButtonVisible(false);

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = true;

        // Request permission first, then proceed to passthrough
        RequestScenePermissionThenProceed();
    }

    private void EnterPlaneDiscovery()
    {
        CurrentState = SessionState.PlaneDiscovery;
        hasTimedOut = false;
        detectionTimeoutTimer = 0f;

        Debug.Log("[JournalSession] Entered PlaneDiscovery state.");

        // Enable AR plane detection (only if permission was granted)
        if (arPlaneManager != null && scenePermissionGranted)
        {
            arPlaneManager.enabled = true;
            Debug.Log("[JournalSession] ARPlaneManager enabled (permission granted).");
        }
        else if (!scenePermissionGranted)
        {
            Debug.LogWarning("[JournalSession] ARPlaneManager NOT enabled — USE_SCENE permission denied.");
        }

        if (arTableDetector != null)
        {
            arTableDetector.ResetState();
            arTableDetector.enabled = true;
        }

        if (calibrationGuide != null)
            calibrationGuide.Show();

        ShowInstruction("Place both hands flat on your table.\nHold for 2 seconds.");
    }

    private void OnTableConfirmed(ARTableDetector.DetectedTable table)
    {
        // Race condition guard: if timeout already fired, ignore detection
        if (hasTimedOut) return;
        if (CurrentState != SessionState.PlaneDiscovery
            && CurrentState != SessionState.HandConfirmation) return;

        // Disable detection systems
        if (arTableDetector != null)
            arTableDetector.enabled = false;
        if (arPlaneManager != null)
            arPlaneManager.enabled = false;

        CurrentState = SessionState.Preview;

        Debug.Log($"[JournalSession] Table confirmed at {table.position}, " +
                  $"size={table.size}, AR={table.sourcePlane != null}. " +
                  $"User at {table.userHeadPosition}.");

        StartCoroutine(PreviewAndTransition(table));
    }

    private void OnConfirmationLost()
    {
        // Reset timeout when user lifts hands (not a timeout scenario)
        detectionTimeoutTimer = 0f;
    }

    // ================================================================
    // PREVIEW & TRANSITION
    // ================================================================

    private IEnumerator PreviewAndTransition(ARTableDetector.DetectedTable table)
    {
        // Step 1: Spawn whiteboard on real table in passthrough
        SpawnWhiteboardForPreview(table);

        ShowInstruction("Journal ready! Transitioning...");

        // Step 2: Let user see the whiteboard on their real table
        yield return new WaitForSeconds(previewDuration);

        // Step 3: Transition to VR
        CurrentState = SessionState.TransitionToVR;
        HideInstruction();

        if (calibrationGuide != null)
            calibrationGuide.Hide();

        if (passthroughManager != null)
        {
            passthroughManager.OnPassthroughExited += OnceAfterPassthroughExit;
            passthroughManager.ExitPassthrough(() =>
            {
                CurrentState = SessionState.Journaling;
                Debug.Log("[JournalSession] Journaling session started.");
            });
        }
        else
        {
            AlignVRWorldToTable(table);
            MoveWhiteboardToVRLayer();
            CurrentState = SessionState.Journaling;
            Debug.Log("[JournalSession] Journaling session started (no passthrough).");
        }
    }

    private void OnceAfterPassthroughExit()
    {
        passthroughManager.OnPassthroughExited -= OnceAfterPassthroughExit;

        AlignVRWorldToTable(pendingTable);
        MoveWhiteboardToVRLayer();

        // Create spatial anchor for drift resistance
        if (alignmentAnchor != null)
        {
            Pose tablePose = new Pose(pendingTable.position, pendingTable.rotation);
            alignmentAnchor.CreateAnchorAtTable(tablePose);
        }
    }

    // ================================================================
    // WHITEBOARD SPAWNING
    // ================================================================

    private void SpawnWhiteboardForPreview(ARTableDetector.DetectedTable table)
    {
        pendingTable = table;

        if (whiteboardUtils == null) return;

        var wbComponent = whiteboardUtils.WhiteboardPrefab.GetComponent<Whiteboard>();
        if (wbComponent != null)
            wbComponent.backgroundColor = journalBackgroundColor;

        Vector3 spawnPos = table.position + Vector3.up * 0.002f;
        spawnedWhiteboard = whiteboardUtils.SpawnAligned(spawnPos, table.rotation, table.size);

        if (spawnedWhiteboard != null)
        {
            int ptLayer = passthroughManager != null
                ? passthroughManager.GetPassthroughUILayer()
                : 31;
            PassthroughManager.SetLayerRecursive(spawnedWhiteboard, ptLayer);

            Debug.Log($"[JournalSession] Whiteboard spawned at {spawnPos} " +
                      $"on layer {ptLayer} (passthrough preview).");
        }
    }

    private void MoveWhiteboardToVRLayer()
    {
        if (spawnedWhiteboard == null) return;

        const int WHITEBOARD_LAYER = 10;
        PassthroughManager.SetLayerRecursive(spawnedWhiteboard, WHITEBOARD_LAYER);
        Debug.Log("[JournalSession] Whiteboard moved to layer 10 for VR interaction.");
    }

    // ================================================================
    // VR WORLD ALIGNMENT
    // ================================================================

    private void AlignVRWorldToTable(ARTableDetector.DetectedTable table)
    {
        if (journalChairTable == null || journalTable == null) return;

        // 1. Compute "table faces user" direction
        Vector3 tableToUser = table.userHeadPosition - table.position;
        tableToUser.y = 0f;

        if (tableToUser.sqrMagnitude < 0.01f)
        {
            tableToUser = -table.userForward;
            tableToUser.y = 0f;
        }
        tableToUser.Normalize();

        Quaternion targetParentRot = Quaternion.LookRotation(tableToUser, Vector3.up);

        // 2. Position: place parent so JournalTable child ends up at detected position
        Vector3 tableChildLocalPos = journalTable.localPosition;
        Vector3 targetParentPos = table.position - targetParentRot * tableChildLocalPos;
        targetParentPos.y = table.position.y - tableChildLocalPos.y;

        journalChairTable.position = targetParentPos;
        journalChairTable.rotation = targetParentRot;

        // 3. Chair validation: if chair is too far from user, try 180° flip
        if (chair != null)
        {
            float chairToUserDist = Vector3.Distance(
                new Vector3(chair.position.x, 0f, chair.position.z),
                new Vector3(table.userHeadPosition.x, 0f, table.userHeadPosition.z));

            if (chairToUserDist > 1.5f)
            {
                Debug.LogWarning($"[JournalSession] Chair-user distance {chairToUserDist:F2}m " +
                                 "exceeds threshold. Trying 180° rotation.");

                Quaternion flippedRot = targetParentRot * Quaternion.Euler(0f, 180f, 0f);
                Vector3 flippedPos = table.position - flippedRot * tableChildLocalPos;
                flippedPos.y = table.position.y - tableChildLocalPos.y;

                journalChairTable.position = flippedPos;
                journalChairTable.rotation = flippedRot;

                float newDist = Vector3.Distance(
                    new Vector3(chair.position.x, 0f, chair.position.z),
                    new Vector3(table.userHeadPosition.x, 0f, table.userHeadPosition.z));

                if (newDist > chairToUserDist)
                {
                    journalChairTable.position = targetParentPos;
                    journalChairTable.rotation = targetParentRot;
                    Debug.Log("[JournalSession] 180° flip didn't help — reverted.");
                }
                else
                {
                    Debug.Log($"[JournalSession] 180° flip improved chair distance: " +
                              $"{chairToUserDist:F2}m → {newDist:F2}m.");
                }
            }
        }

        Debug.Log($"[JournalSession] VR world aligned. " +
                  $"JournalChairTable → pos={journalChairTable.position}, " +
                  $"rot={journalChairTable.rotation.eulerAngles}. " +
                  $"JournalTable should be at {table.position}. " +
                  $"User was at {table.userHeadPosition}.");
    }

    // ================================================================
    // RE-CALIBRATION
    // ================================================================

    public void RequestReCalibration()
    {
        if (CurrentState != SessionState.Journaling) return;

        CurrentState = SessionState.ReCalibrating;
        Debug.Log("[JournalSession] Re-calibration requested.");

        if (alignmentAnchor != null)
            alignmentAnchor.ReleaseAnchor();

        if (spawnedWhiteboard != null)
            spawnedWhiteboard.SetActive(false);

        if (passthroughManager != null)
            passthroughManager.EnterPassthrough(() => EnterPlaneDiscovery());
        else
            EnterPlaneDiscovery();
    }

    // ================================================================
    // FALLBACK
    // ================================================================

    private void FallbackSpawn()
    {
        if (arTableDetector != null)
            arTableDetector.enabled = false;
        if (arPlaneManager != null)
            arPlaneManager.enabled = false;

        HideInstruction();

        if (calibrationGuide != null)
            calibrationGuide.Hide();

        if (passthroughManager != null && passthroughManager.IsPassthroughActive)
            passthroughManager.ExitPassthrough(() => SpawnAtDefaultPosition());
        else
            SpawnAtDefaultPosition();
    }

    private void SpawnAtDefaultPosition()
    {
        CurrentState = SessionState.Journaling;

        if (whiteboardUtils != null && journalTable != null)
        {
            var wbComponent = whiteboardUtils.WhiteboardPrefab.GetComponent<Whiteboard>();
            if (wbComponent != null)
                wbComponent.backgroundColor = journalBackgroundColor;

            Vector3 pos = journalTable.position + Vector3.up * 0.002f;
            Quaternion rot = Quaternion.LookRotation(journalTable.forward, Vector3.up);
            Vector2 size = new Vector2(0.4f, 0.3f);

            spawnedWhiteboard = whiteboardUtils.SpawnAligned(pos, rot, size);
        }

        Debug.Log("[JournalSession] Fallback: spawned whiteboard at default position.");
    }

    // ================================================================
    // END SESSION
    // ================================================================

    public void EndSession()
    {
        if (CurrentState != SessionState.Journaling) return;

        CurrentState = SessionState.Ending;
        StartCoroutine(EndSessionCoroutine());
    }

    private IEnumerator EndSessionCoroutine()
    {
        ShowInstruction("Saving your reflections...");

        yield return new WaitForSeconds(1f);

        CleanupWhiteboard();
        HideInstruction();

        if (alignmentAnchor != null)
            alignmentAnchor.ReleaseAnchor();

        if (journalChairTable != null)
        {
            Vector3 startPos = journalChairTable.position;
            Quaternion startRot = journalChairTable.rotation;
            float elapsed = 0f;
            float duration = 0.5f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                t = t * t * (3f - 2f * t);

                journalChairTable.position = Vector3.Lerp(startPos, originalChairTablePosition, t);
                journalChairTable.rotation = Quaternion.Slerp(startRot, originalChairTableRotation, t);
                yield return null;
            }

            journalChairTable.position = originalChairTablePosition;
            journalChairTable.rotation = originalChairTableRotation;
        }

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = false;

        SetButtonVisible(true);
        CurrentState = SessionState.Idle;
        Debug.Log("[JournalSession] Session ended. Book re-enabled.");
    }

    // ================================================================
    // CANCEL SESSION
    // ================================================================

    public void CancelSession()
    {
        if (CurrentState == SessionState.Idle || CurrentState == SessionState.Ending) return;

        if (CurrentState == SessionState.Journaling)
        {
            EndSession();
            return;
        }

        StopAllCoroutines();

        if (arTableDetector != null)
        {
            arTableDetector.enabled = false;
            arTableDetector.ResetState();
        }
        if (arPlaneManager != null)
            arPlaneManager.enabled = false;

        HideInstruction();

        if (calibrationGuide != null)
            calibrationGuide.Hide();

        if (passthroughManager != null)
            passthroughManager.OnPassthroughExited -= OnceAfterPassthroughExit;

        if (alignmentAnchor != null)
            alignmentAnchor.ReleaseAnchor();

        CleanupWhiteboard();

        if (passthroughManager != null && passthroughManager.IsPassthroughActive)
            passthroughManager.ExitPassthrough(() => ResetToIdle());
        else
            ResetToIdle();
    }

    private void ResetToIdle()
    {
        if (journalChairTable != null)
        {
            journalChairTable.position = originalChairTablePosition;
            journalChairTable.rotation = originalChairTableRotation;
        }

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = false;

        SetButtonVisible(true);
        CurrentState = SessionState.Idle;
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private void SetButtonVisible(bool visible)
    {
        if (startButton != null)
            startButton.gameObject.SetActive(visible);
    }

    private void CleanupWhiteboard()
    {
        if (spawnedWhiteboard != null)
        {
            Destroy(spawnedWhiteboard);
            spawnedWhiteboard = null;
        }
    }

    // ================================================================
    // INSTRUCTION UI (fallback when CalibrationGuide is null)
    // ================================================================

    private void ShowInstruction(string message)
    {
        // Prefer CalibrationGuide if available
        if (calibrationGuide != null)
        {
            calibrationGuide.SetInstruction(message);
            return;
        }

        // Fallback: world-space TextMeshPro
        if (instructionText == null) return;

        instructionText.text = message;
        instructionText.gameObject.SetActive(true);
        UpdateInstructionPosition();
    }

    private void HideInstruction()
    {
        if (calibrationGuide != null)
            calibrationGuide.HideInstruction();

        if (instructionText != null)
            instructionText.gameObject.SetActive(false);
    }

    private void UpdateInstructionPosition()
    {
        if (instructionText == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = cam.transform.forward;
        forward.Normalize();

        instructionText.transform.position = cam.transform.position + forward * 1.2f + Vector3.down * 0.2f;
        instructionText.transform.rotation = Quaternion.LookRotation(forward);
    }

    private void CreateInstructionText()
    {
        GameObject textObj = new GameObject("JournalInstruction");
        instructionText = textObj.AddComponent<TextMeshPro>();

        instructionText.fontSize = 0.4f;
        instructionText.alignment = TextAlignmentOptions.Center;
        instructionText.color = new Color(0.95f, 0.92f, 0.85f);
        instructionText.rectTransform.sizeDelta = new Vector2(1.2f, 0.4f);
        instructionText.enableWordWrapping = true;

        // Put on passthrough UI layer so text is visible during passthrough
        int ptLayer = passthroughManager != null
            ? passthroughManager.GetPassthroughUILayer()
            : 31;
        textObj.layer = ptLayer;
    }

    private void OnDestroy()
    {
        if (startButton != null)
            startButton.OnButtonPressed -= OnStartButtonPressed;

        if (arTableDetector != null)
        {
            arTableDetector.OnTableConfirmed -= OnTableConfirmed;
            arTableDetector.OnConfirmationLost -= OnConfirmationLost;
        }

        if (passthroughManager != null)
            passthroughManager.OnPassthroughExited -= OnceAfterPassthroughExit;
    }
}
