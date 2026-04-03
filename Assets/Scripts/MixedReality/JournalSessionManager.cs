using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using EMILIA.Data;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
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
///   5. Player is teleported to SeatPoint; virtual table offset adjusted to match real-world distance
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

    [Header("Post-Journal Review")]
    [Tooltip("Optional review flow controller. When assigned, EndSession() runs the review " +
             "before actually ending so the user can keep or release the journal.")]
    public JournalReviewController reviewController;

    [Header("AR Managers")]
    [Tooltip("ARPlaneManager for table detection. Enabled only during detection.")]
    public ARPlaneManager arPlaneManager;

    [Header("Detection Mode")]
    [Tooltip("Skip AR plane scanning entirely. Uses hand-only detection like the " +
             "Quest 3 AR Surface Keyboard — instant dot grid feedback from palm positions. " +
             "Enable this for faster, lighter calibration without Scene Model dependency.")]
    public bool skipPlaneDetection;
    [Tooltip("Editor / testing only. Skip all MR calibration and jump directly to the " +
             "Journaling state on Start. Useful for testing the journal UI and review flow " +
             "with XR Device Simulator without building to the headset.")]
    public bool skipToJournalingOnStart;

    [Header("Scene Objects")]
    [Tooltip("The JournalChairTable parent that will be repositioned.")]
    public Transform journalChairTable;
    [Tooltip("The JournalTable child (used for alignment reference).")]
    public Transform journalTable;
    [Tooltip("The Chair child.")]
    public Transform chair;

    [Header("Table Placement")]
    [Tooltip("Vertical bias applied to the detected table Y position (metres). " +
             "Negative values lower the whiteboard. Palm thickness sits ~2–3 cm above " +
             "the physical surface, so a small negative value corrects that overshoot.")]
    public float tableHeightBias = 0f;

    [Header("SeatPoint Calibration")]
    [Tooltip("The SeatPoint transform where the player will be teleported after calibration. " +
             "Its forward direction defines the player's seated facing direction. " +
             "If null, falls back to legacy AlignVRWorldToTable behaviour.")]
    public Transform seatPoint;
    [Tooltip("The XR Origin (XR Rig) root transform. Required for teleportation to SeatPoint.")]
    public Transform xrOrigin;
    [Tooltip("The root GameObject of the entire virtual island (table, chair, decorations). " +
             "Moved as a unit during height calibration so the virtual table surface sits at the " +
             "same eye-relative height as the real table detected in passthrough.")]
    public Transform mainIsland;
    [Tooltip("Optional transform to move for vertical calibration. " +
             "Recommended: JournalChairTable (or another journaling-only root). " +
             "If null, falls back to JournalChairTable, then MainIsland.")]
    public Transform heightAdjustmentRoot;

    [Header("Comfort - Height Alignment")]
    [Tooltip("If enabled, adjusts XR Origin Y so the returned VR eye height matches the real eye-above-table relationship captured in passthrough.")]
    public bool calibrateUserEyeHeight = true;

    [Tooltip("Optional: assign the WhiteboardPlaceholder transform (or any transform at the exact " +
             "virtual writing surface height). If null, falls back to journalTable.position.y — " +
             "which is only correct when the JournalTable pivot is at surface level.")]
    public Transform tableWritingSurface;

    [Tooltip("Fine-tune calibration after the session starts (metres). " +
             "Negative = lower the player (virtual table appears higher). " +
             "Positive = raise the player (virtual table appears lower). " +
             "Start at 0 and adjust in small increments if the table still feels off.")]
    [Range(-0.15f, 0.15f)]
    public float calibrationHeightBias = 0f;

    [Tooltip("Expected real eye-above-table range in metres captured from passthrough. " +
             "Used to reject outlier hand/head samples.")]
    public Vector2 realEyeAboveTableClamp = new Vector2(0.25f, 0.85f);

    [Tooltip("If enabled, also moves the scene calibration root vertically. " +
             "Recommended OFF when island/chair grounding already looks correct.")]
    public bool applySceneHeightCorrection;

    [Tooltip("Clamp for total vertical correction applied during calibration (metres). " +
             "Prevents over-correction that can cause uncomfortable seat/table mismatch.")]
    [Range(0.05f, 1.0f)]
    public float maxHeightCorrection = 0.45f;

    [Tooltip("Clamp for expected eye-above-table distance in metres. " +
             "Typical seated range is around 0.55m to 0.90m.")]
    public Vector2 eyeAboveTableClamp = new Vector2(0.50f, 1.05f);

    [Header("Calibration Diagnostics")]
    [Tooltip("Enable post-teleport calibration residual logs for on-device validation.")]
    public bool enableCalibrationDiagnostics;

    [Tooltip("Warn if final camera eye-Y differs from targetEyeY by more than this value (metres).")]
    [Range(0.001f, 0.2f)]
    public float eyeHeightResidualWarnThreshold = 0.015f;

    [Tooltip("Warn if final camera XZ differs from SeatPoint XZ by more than this value (metres).")]
    [Range(0.001f, 0.2f)]
    public float seatXZResidualWarnThreshold = 0.02f;

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
    [Range(5f, 120f)]
    public float detectionTimeout = 60f;

    [Header("Journal Data")]
    [Tooltip("PlayerPrefs key used to look up the current user ID. Must match the key used at login.")]
    public string playerPrefsUserIdKey = "Nickname";

    // ── Singleton ───────────────────────────────────────────────────────
    public static JournalSessionManager Instance { get; private set; }

    // ── State ───────────────────────────────────────────────────────────
    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    private Vector3 originalChairTablePosition;
    private Quaternion originalChairTableRotation;
    private Vector3 originalTableLocalPosition;
    private float detectionTimeoutTimer;
    private bool hasTimedOut;
    private ARTableDetector.DetectedTable pendingTable;
    private bool calibrationDataValid;
    private bool scenePermissionGranted;
    private string _sessionCreatedAtIso;    // ISO timestamp for DB (UTC+7)
    private string _sessionCreatedAtDisplay; // formatted timestamp for title-page label
    private XRHandSubsystem handSubsystem;
    private List<LocomotionProvider> disabledLocomotionProviders = new List<LocomotionProvider>();
    private float originalXROriginY;
    private float capturedRealEyeHeight;
    private float originalHeightAdjustmentY;
    private bool hasAdjustedHeightAdjustment;

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start()
    {
        if (journalChairTable != null)
        {
            originalChairTablePosition = journalChairTable.position;
            originalChairTableRotation = journalChairTable.rotation;
        }

        if (journalTable != null)
            originalTableLocalPosition = journalTable.localPosition;

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

        // Always own the instruction text so CalibrationGuide's internal event
        // handlers (OnProgress / OnConfirmed) cannot overwrite our messages.
        if (instructionText == null)
            CreateInstructionText();

        HideInstruction();

        // Whiteboard UI is only shown while journaling.
        SetWhiteboardUIActive(false);

        if (skipToJournalingOnStart)
            StartCoroutine(SkipToJournalingCoroutine());
    }

    private IEnumerator SkipToJournalingCoroutine()
    {
        // Wait one frame so all other Start() calls finish (UI layout, prefab init, etc.).
        yield return null;
        SetButtonVisible(false);
        SpawnAtDefaultPosition();
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

            // Drive CalibrationGuide palm indicators from hand tracking data
            if (calibrationGuide != null && arTableDetector != null)
                UpdateCalibrationPalmIndicators();
        }

        // Keep instruction text facing user and at arm's length during passthrough
        if (instructionText != null && instructionText.gameObject.activeSelf)
            UpdateInstructionPosition();
    }

    /// <summary>
    /// Reads hand tracking state and forwards it to CalibrationGuide so
    /// palm indicator spheres appear during detection.
    /// </summary>
    private void UpdateCalibrationPalmIndicators()
    {
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand leftHand = handSubsystem.leftHand;
        XRHand rightHand = handSubsystem.rightHand;

        bool leftTracked = leftHand.isTracked;
        bool rightTracked = rightHand.isTracked;

        bool leftFlat = false;
        bool rightFlat = false;
        Vector3 leftPos = Vector3.zero;
        Vector3 rightPos = Vector3.zero;

        if (leftTracked)
            leftFlat = arTableDetector.IsPalmFlat(leftHand, out leftPos);
        if (rightTracked)
            rightFlat = arTableDetector.IsPalmFlat(rightHand, out rightPos);

        calibrationGuide.UpdatePalmIndicators(
            leftTracked, leftPos, leftFlat,
            rightTracked, rightPos, rightFlat);
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

        ShowInstruction("Find a flat surface and place both hands\nwith palms facing downward.");

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

        // Skip permission request if plane detection is disabled (hand-only mode)
        if (skipPlaneDetection)
            ProceedToPassthrough();
        else
            RequestScenePermissionThenProceed();
    }

    private void EnterPlaneDiscovery()
    {
        CurrentState = SessionState.PlaneDiscovery;
        hasTimedOut = false;
        detectionTimeoutTimer = 0f;

        Debug.Log("[JournalSession] Entered PlaneDiscovery state.");

        // Enable AR plane detection (only if permission was granted AND not skipped)
        if (!skipPlaneDetection && arPlaneManager != null && scenePermissionGranted)
        {
            arPlaneManager.enabled = true;
            Debug.Log("[JournalSession] ARPlaneManager enabled (permission granted).");
        }
        else if (skipPlaneDetection)
        {
            Debug.Log("[JournalSession] Plane detection skipped — using hand-only mode.");
        }
        else if (!scenePermissionGranted)
        {
            Debug.LogWarning("[JournalSession] ARPlaneManager NOT enabled — USE_SCENE permission denied.");
        }

        if (arTableDetector != null)
        {
            arTableDetector.ResetState();
            arTableDetector.enabled = true;

            // Force hand-only fallback immediately when skipping planes
            if (skipPlaneDetection)
                arTableDetector.ForceHandOnlyMode();
        }

        if (calibrationGuide != null)
        {
            calibrationGuide.Show();
            // Suppress CalibrationGuide's own instruction TMP — we own the text.
            calibrationGuide.HideInstruction();
        }

        ShowInstruction("Find a flat surface and place both hands\nwith palms facing downward.");
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

        // Capture averaged eye height sampled across the entire hold period.
        // This is more stable than a single-frame snapshot and is used later
        // so the VR camera matches the passthrough perspective.
        capturedRealEyeHeight = table.avgEyeY;

        Debug.Log($"[JournalSession] Table confirmed at {table.position}, " +
                  $"size={table.size}, AR={table.sourcePlane != null}. " +
                  $"User at {table.userHeadPosition} (capturedEyeY={capturedRealEyeHeight:F2}).");

        StartCoroutine(PreviewAndTransition(table));
    }

    private void OnConfirmationLost()
    {
        // Only reset a portion of the timeout so the cumulative time still increases.
        // If a user repeatedly places/lifts hands, the fallback eventually fires rather
        // than being deferred forever.
        detectionTimeoutTimer = Mathf.Max(detectionTimeoutTimer - 2f, 0f);
    }

    // ================================================================
    // PREVIEW & TRANSITION
    // ================================================================

    private IEnumerator PreviewAndTransition(ARTableDetector.DetectedTable table)
    {
        // Step 1: Spawn whiteboard on real table in passthrough
        SpawnWhiteboardForPreview(table);

        ShowInstruction("Calibrating...");

        // Step 2: Let user see the whiteboard on their real table
        yield return new WaitForSeconds(previewDuration * 0.6f);

        ShowInstruction("Done, returning to the game world.");
        yield return new WaitForSeconds(previewDuration * 0.4f);

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
                OnJournalingEntered();
                SetWhiteboardUIActive(true);
                Debug.Log("[JournalSession] Journaling session started.");
            });
        }
        else
        {
            TeleportToSeatPoint();
            MoveWhiteboardToVRLayer();
            CurrentState = SessionState.Journaling;
            OnJournalingEntered();
            SetWhiteboardUIActive(true);
            LockLocomotion();
            Debug.Log("[JournalSession] Journaling session started (no passthrough).");
        }
    }

    private void OnceAfterPassthroughExit()
    {
        passthroughManager.OnPassthroughExited -= OnceAfterPassthroughExit;

        // Wrap in try-catch: an exception here aborts the PassthroughManager's
        // fade-from-black coroutine, leaving the screen permanently black.
        try
        {
            // Lock locomotion while the screen is still black so the player
            // cannot move during the fade-in back to the VR world.
            LockLocomotion();

            // Scene is fully static — only the player (XR Origin) is repositioned.
            TeleportToSeatPoint();
            MoveWhiteboardToVRLayer();

            if (alignmentAnchor != null)
            {
                Pose tablePose = new Pose(pendingTable.position, pendingTable.rotation);
                alignmentAnchor.CreateAnchorAtTable(tablePose);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[JournalSession] Error during post-passthrough setup: {ex}");
        }
    }

    // ================================================================
    // WHITEBOARD SPAWNING
    // ================================================================

    private void SpawnWhiteboardForPreview(ARTableDetector.DetectedTable table)
    {
        pendingTable = table;
        calibrationDataValid = true;
        Debug.Log($"[JournalSession] Table confirmed at {table.position}. " +
                  "Using static whiteboard — no preview spawn.");
    }

    private void MoveWhiteboardToVRLayer()
    {
        // Static whiteboard is already on the correct VR layer (10) — nothing to do.
        Debug.Log("[JournalSession] Static whiteboard in scene — no layer move required.");
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
        targetParentPos.y = table.position.y - tableChildLocalPos.y + tableHeightBias;

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
                flippedPos.y = table.position.y - tableChildLocalPos.y + tableHeightBias;

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
    // SEATPOINT TELEPORTATION
    // ================================================================

    /// <summary>
    /// Teleports the XR Origin so the player's camera ends up at SeatPoint's XZ
    /// position and forward direction (yaw). Camera Y is calibrated from passthrough:
    ///   targetEyeY = virtualTableSurfaceY + (realEyeY - realTableY)
    /// This makes the virtual whiteboard appear at the same eye-relative height as the
    /// real table the user placed their palms on. Scene objects are never moved.
    /// Falls back to SeatPoint.y if no calibration data is available (e.g. timeout).
    /// </summary>
    private void TeleportToSeatPoint()
    {
        if (seatPoint == null) return;

        // Auto-find XR Origin if not wired in the inspector
        if (xrOrigin == null)
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) xrOrigin = origin.transform;
        }
        if (xrOrigin == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        originalXROriginY = xrOrigin.position.y;

        // 1. Rotate XR Origin so camera faces SeatPoint's forward direction
        float yawDelta = seatPoint.eulerAngles.y - cam.transform.eulerAngles.y;
        xrOrigin.RotateAround(cam.transform.position, Vector3.up, yawDelta);

        // 2. Translate XR Origin so camera XZ is at SeatPoint XZ
        Vector3 offset = seatPoint.position - cam.transform.position;
        offset.y = 0f;
        xrOrigin.position += offset;

        // 3. Determine target eye height.
        //    If passthrough calibration ran: preserve the user's real eye-above-table
        //    distance so the virtual whiteboard feels at the same height as the real table.
        //    Otherwise: use SeatPoint's authored eye height as-is.
        float targetEyeY;
        bool hasExpectedEyeAboveTable = false;
        float expectedEyeAboveTable = 0f;
        if (calibrationDataValid)
        {
            // Use palm-based surface Y rather than AR plane Y — eliminates spatial-mesh
            // drift and uses the actual contact point the user measured from.
            float realEyeAboveTable = capturedRealEyeHeight - pendingTable.avgPalmSurfaceY;
            realEyeAboveTable = Mathf.Clamp(
                realEyeAboveTable,
                Mathf.Min(realEyeAboveTableClamp.x, realEyeAboveTableClamp.y),
                Mathf.Max(realEyeAboveTableClamp.x, realEyeAboveTableClamp.y));

            expectedEyeAboveTable = realEyeAboveTable;
            hasExpectedEyeAboveTable = true;

            float virtualTableY = GetVirtualTableSurfaceY();
            targetEyeY = virtualTableY + realEyeAboveTable + calibrationHeightBias;

            Debug.Log($"[JournalSession] Eye-height calibrated: realEyeAboveTable=" +
                      $"{realEyeAboveTable:F3}m (eye={capturedRealEyeHeight:F3}, palmSurf={pendingTable.avgPalmSurfaceY:F3}), " +
                      $"virtualTableY={virtualTableY:F3}m, bias={calibrationHeightBias:F3}m, targetEyeY={targetEyeY:F3}m");
        }
        else
        {
            targetEyeY = seatPoint.position.y;
            Debug.Log($"[JournalSession] No calibration data — using SeatPoint eye height {targetEyeY:F3}m");
        }

        float trackingHeight = cam.transform.position.y - xrOrigin.position.y;
        xrOrigin.position = new Vector3(
            xrOrigin.position.x,
            targetEyeY - trackingHeight,
            xrOrigin.position.z);

        Debug.Log($"[JournalSession] Teleported to SeatPoint. " +
                  $"XR Origin → {xrOrigin.position}, yaw delta={yawDelta:F1}°, " +
                  $"camera Y → {cam.transform.position.y:F2}");

        LogCalibrationResiduals(cam, targetEyeY, hasExpectedEyeAboveTable, expectedEyeAboveTable);
    }

    private void LogCalibrationResiduals(Camera cam,
                                         float targetEyeY,
                                         bool hasExpectedEyeAboveTable,
                                         float expectedEyeAboveTable)
    {
        if (!enableCalibrationDiagnostics || cam == null || seatPoint == null)
            return;

        float eyeResidual = cam.transform.position.y - targetEyeY;

        Vector2 camXZ = new Vector2(cam.transform.position.x, cam.transform.position.z);
        Vector2 seatXZ = new Vector2(seatPoint.position.x, seatPoint.position.z);
        float seatResidual = Vector2.Distance(camXZ, seatXZ);

        string eyeAboveMessage = string.Empty;
        float eyeAboveResidual = 0f;
        bool hasEyeAboveResidual = false;

        if (hasExpectedEyeAboveTable)
        {
            float virtualTableY = GetVirtualTableSurfaceY();
            float actualEyeAboveVirtual = cam.transform.position.y - virtualTableY;
            eyeAboveResidual = actualEyeAboveVirtual - expectedEyeAboveTable;
            hasEyeAboveResidual = true;
            eyeAboveMessage = $", eyeAboveVirtual={actualEyeAboveVirtual:F3}m " +
                              $"(target={expectedEyeAboveTable:F3}m, residual={eyeAboveResidual:+0.000;-0.000;0.000}m)";
        }

        Debug.Log($"[JournalSession][Diag] Teleport residuals: eyeY={eyeResidual:+0.000;-0.000;0.000}m, " +
                  $"seatXZ={seatResidual:F3}m{eyeAboveMessage}");

        if (Mathf.Abs(eyeResidual) > eyeHeightResidualWarnThreshold)
        {
            Debug.LogWarning($"[JournalSession][Diag] Eye-height residual {eyeResidual:+0.000;-0.000;0.000}m " +
                             $"exceeds warn threshold {eyeHeightResidualWarnThreshold:F3}m.");
        }

        if (seatResidual > seatXZResidualWarnThreshold)
        {
            Debug.LogWarning($"[JournalSession][Diag] Seat XZ residual {seatResidual:F3}m " +
                             $"exceeds warn threshold {seatXZResidualWarnThreshold:F3}m.");
        }

        if (hasEyeAboveResidual && Mathf.Abs(eyeAboveResidual) > eyeHeightResidualWarnThreshold)
        {
            Debug.LogWarning($"[JournalSession][Diag] Eye-above-table residual {eyeAboveResidual:+0.000;-0.000;0.000}m " +
                             $"exceeds warn threshold {eyeHeightResidualWarnThreshold:F3}m.");
        }
    }

    /// <summary>
    /// Moves the configured height adjustment root vertically so the virtual table surface sits
    /// at the same eye-relative height as the real table detected in passthrough.
    ///
    /// Formula: targetVirtualTableY = cameraY - (realEyeY - realTableY)
    ///          deltaY = targetVirtualTableY - currentVirtualTableY
    ///
    /// Must be called AFTER TeleportToSeatPoint (so camera Y is set) and
    /// BEFORE MoveWhiteboardToVRLayer (so the placeholder is at its final Y
    /// when the whiteboard is placed on it).
    /// </summary>
    private void AdjustIslandHeight(ARTableDetector.DetectedTable table)
    {
        Transform adjustmentRoot = ResolveHeightAdjustmentRoot();
        if (adjustmentRoot == null)
            return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // How high were the player's eyes above the real table during calibration?
        float realEyeAboveTable = capturedRealEyeHeight - table.position.y;
        realEyeAboveTable = Mathf.Clamp(
            realEyeAboveTable,
            Mathf.Min(eyeAboveTableClamp.x, eyeAboveTableClamp.y),
            Mathf.Max(eyeAboveTableClamp.x, eyeAboveTableClamp.y));

        // Find the virtual table surface Y from the whiteboard placeholder
        float virtualTableSurfaceY = GetVirtualTableSurfaceY();

        // We want: virtualTableSurfaceY == cameraY - realEyeAboveTable
        float targetVirtualTableY = cam.transform.position.y - realEyeAboveTable;
        float deltaY = targetVirtualTableY - virtualTableSurfaceY;
        deltaY = Mathf.Clamp(deltaY, -maxHeightCorrection, maxHeightCorrection);

        if (Mathf.Abs(deltaY) < 0.005f) return;  // < 5mm, skip

        originalHeightAdjustmentY = adjustmentRoot.position.y;
        hasAdjustedHeightAdjustment = true;
        adjustmentRoot.position += new Vector3(0f, deltaY, 0f);

        if (alignmentAnchor != null && alignmentAnchor.IsAnchored)
            alignmentAnchor.RefreshTargetOffset();

        Debug.Log($"[JournalSession] Height adjusted by {deltaY:+0.000;-0.000}m on '{adjustmentRoot.name}'. " +
                  $"realEyeAboveTable={realEyeAboveTable:F3}m, " +
                  $"cameraY={cam.transform.position.y:F3}, " +
                  $"virtualTableY: {virtualTableSurfaceY:F3} → {virtualTableSurfaceY + deltaY:F3}.");
    }

    private Transform ResolveHeightAdjustmentRoot()
    {
        if (heightAdjustmentRoot != null)
            return heightAdjustmentRoot;

        if (journalChairTable != null)
            return journalChairTable;

        if (mainIsland != null)
            return mainIsland;

        var go = GameObject.Find("MainIsland");
        if (go != null)
        {
            mainIsland = go.transform;
            return mainIsland;
        }

        Debug.LogWarning("[JournalSession] No height adjustment root found. " +
                         "Assign Height Adjustment Root (recommended: JournalChairTable). " +
                         "Skipping height correction.");
        return null;
    }

    private float GetVirtualTableSurfaceY()
    {
        // Prefer an explicitly assigned writing-surface reference.
        if (tableWritingSurface != null)
        {
            float y = tableWritingSurface.position.y;
            Debug.Log($"[JournalSession] VirtualTableSurfaceY={y:F3}m from tableWritingSurface '{tableWritingSurface.name}'");
            return y;
        }

        // Try WhiteboardPlaceholder first (explicit surface marker), then the
        // Whiteboard itself (the static writing surface placed in scene).
        string[] autoPaths = new[]
        {
            "JournalChairTable/JournalTable/WhiteboardPlaceholder",
            "JournalChairTable/JournalTable/PreNDuringJournal/Whiteboard",
        };

        foreach (var path in autoPaths)
        {
            var found = GameObject.Find(path);
            if (found != null)
            {
                tableWritingSurface = found.transform;
                float y = tableWritingSurface.position.y;
                Debug.Log($"[JournalSession] VirtualTableSurfaceY={y:F3}m from '{found.name}' (auto-found at '{path}'). " +
                          $"JournalTable pivot Y for comparison: {(journalTable != null ? journalTable.position.y.ToString("F3") : "N/A")}m");
                return y;
            }
        }

        // Last resort: JournalTable pivot (only correct when pivot is at surface).
        if (journalTable != null)
        {
            float y = journalTable.position.y;
            Debug.LogWarning($"[JournalSession] VirtualTableSurfaceY={y:F3}m — FALLBACK to JournalTable pivot. " +
                             "Neither WhiteboardPlaceholder nor Whiteboard found by path. " +
                             "Calibration may be inaccurate. Assign tableWritingSurface in the inspector.");
            return y;
        }

        Debug.LogError("[JournalSession] VirtualTableSurfaceY: no reference found — returning 0.");
        return 0f;
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
        TeleportToSeatPoint();
        CurrentState = SessionState.Journaling;
        OnJournalingEntered();
        SetWhiteboardUIActive(true);
        LockLocomotion();
        Debug.Log("[JournalSession] Fallback timeout — entering journaling with static whiteboard.");
    }

    // ================================================================
    // JOURNALING SESSION INIT
    // ================================================================

    /// <summary>
    /// Called once every time the session enters the Journaling state.
    /// Resets whiteboard pages and stamps the title page with the current time.
    /// </summary>
    private void OnJournalingEntered()
    {
        // Reset pages: title page (0) + first content page (1)
        ScribbleManager.Instance?.ClearAll();

        // Stamp timestamps — UTC+7 (Asia/Jakarta) to match legacy JournalManager
        var now = DateTime.UtcNow.AddHours(7);
        _sessionCreatedAtIso     = now.ToString("yyyy-MM-ddTHH:mm:ss");
        _sessionCreatedAtDisplay = now.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture);

        WhiteboardPageManager.Instance?.SetCreatedAt(_sessionCreatedAtDisplay);

        Debug.Log($"[JournalSession] Title page ready — created at {_sessionCreatedAtDisplay}");
    }

    // ================================================================
    // END SESSION
    // ================================================================

    public void EndSession()
    {
        if (CurrentState != SessionState.Journaling) return;

        CurrentState = SessionState.Ending;

        if (reviewController != null)
        {
            // Collect journal content now, while ScribbleManager data is intact.
            // Apply the same page-0 fallback used in SaveJournalCoroutine so the
            // sentiment API always receives something meaningful to analyse.
            string jTitle   = ScribbleManager.Instance?.GetTitleText().Trim()   ?? string.Empty;
            string jContent = ScribbleManager.Instance?.GetContentText().Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(jContent) && !string.IsNullOrWhiteSpace(jTitle))
                jContent = jTitle;

            reviewController.BeginReview(
                saveJournal => StartCoroutine(EndSessionCoroutine(saveJournal)),
                originalXROriginY,
                jContent);
            return;
        }

        StartCoroutine(EndSessionCoroutine(saveJournal: true));
    }

    private IEnumerator EndSessionCoroutine(bool saveJournal = true)
    {
        // Save before tearing down so ScribbleManager data is still intact.
        if (saveJournal)
            yield return SaveJournalCoroutine();

        SetWhiteboardUIActive(false);

        yield return new WaitForSeconds(0.5f);

        CleanupWhiteboard();
        HideInstruction();

        if (alignmentAnchor != null)
            alignmentAnchor.ReleaseAnchor();

        calibrationDataValid = false;
        // Scene objects are never moved during a session — only restore the XR Origin.
        RestoreXROriginHeight();
        UnlockLocomotion();

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = false;

        SetButtonVisible(true);
        CurrentState = SessionState.Idle;
        Debug.Log("[JournalSession] Session ended. Book re-enabled.");
    }

    // ================================================================
    // JOURNAL SAVE
    // ================================================================

    /// <summary>
    /// Collects title + content from ScribbleManager and persists via JournalService.
    /// Skipped silently if content is empty or ServiceManager is unavailable.
    /// </summary>
    private IEnumerator SaveJournalCoroutine()
    {
        var sm = ScribbleManager.Instance;
        if (sm == null) yield break;

        string title   = sm.GetTitleText().Trim();
        string content = sm.GetContentText().Trim();

        // Fallback: if the user wrote only on the title page (page 0) and never
        // navigated to a content page, GetContentText() returns "".
        // Treat the title-page text as the journal body and auto-derive a title.
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(title))
        {
            Debug.Log("[JournalSession] No content pages written — using title-page text as journal content.");
            content = title;
            title   = DeriveTitleFromContent(content);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            Debug.Log("[JournalSession] No content written — skipping save.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(title))
            title = DeriveTitleFromContent(content);

        var userId = PlayerPrefs.GetString(playerPrefsUserIdKey, string.Empty);
        if (string.IsNullOrEmpty(userId))
        {
            Debug.LogWarning("[JournalSession] No user ID in PlayerPrefs — journal not saved.");
            yield break;
        }

        if (ServiceManager.Instance?.JournalService == null)
        {
            Debug.LogWarning("[JournalSession] ServiceManager unavailable — journal not saved to DB.");
            yield break;
        }

        bool done = false;
        Journal savedJournal = null;
        yield return ServiceManager.Instance.JournalService.CreateJournal(
            userId, title, content, _sessionCreatedAtIso,
            onSuccess: j =>
            {
                Debug.Log($"[JournalSession] Journal saved — title: \"{title}\"");
                savedJournal = j;
                done = true;
            },
            onError: err =>
            {
                Debug.LogError($"[JournalSession] Journal save failed: {err}");
                done = true;
            }
        );

        yield return new WaitUntil(() => done);

        // Fire-and-forget sentiment analysis.
        // Runs in the background so the session-end flow is not stalled by AI inference.
        if (savedJournal != null && ServiceManager.Instance?.SentimentApi != null)
            StartCoroutine(AnalyzeAndSaveSentiment(savedJournal.Id, content));
    }

    /// <summary>
    /// Calls the /sentiment API and persists the result (tone + AI reason) to the journal row.
    /// Non-blocking relative to EndSessionCoroutine — started as a fire-and-forget coroutine.
    /// </summary>
    private IEnumerator AnalyzeAndSaveSentiment(string journalId, string content)
    {
        bool done = false;
        yield return ServiceManager.Instance.SentimentApi.AnalyzeJournal(
            content,
            onSuccess: result =>
            {
                StartCoroutine(ServiceManager.Instance.JournalService.UpdateJournalSentiment(
                    journalId,
                    result.tone,
                    result.reason,
                    onSuccess: () => Debug.Log($"[JournalSession] Sentiment saved — tone: {result.tone}"),
                    onError: err => Debug.LogError($"[JournalSession] Sentiment update failed: {err}")
                ));
                done = true;
            },
            onError: err =>
            {
                Debug.LogWarning($"[JournalSession] Sentiment API failed (non-fatal): {err}");
                done = true;
            }
        );
        yield return new WaitUntil(() => done);
    }

    private static readonly char[] s_wordSplitChars = new char[] { ' ' };

    /// <summary>Derives a short title from the first N words of content.</summary>
    private static string DeriveTitleFromContent(string content, int maxWords = 3, int maxChars = 60)
    {
        if (string.IsNullOrWhiteSpace(content)) return "Untitled";
        var words = content.Trim().Split(s_wordSplitChars, StringSplitOptions.RemoveEmptyEntries);
        int take  = Mathf.Min(maxWords, words.Length);
        var title = string.Join(" ", words, 0, take);
        return title.Length > maxChars ? title[..maxChars] : title;
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

        // Cancel any in-progress PassthroughManager transition so the screen
        // doesn't get stuck mid-fade-to-black.
        if (passthroughManager != null)
            passthroughManager.CancelTransition();

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
        calibrationDataValid = false;
        SetWhiteboardUIActive(false);
        // Scene objects are never moved during a session — only restore the XR Origin.
        RestoreXROriginHeight();
        UnlockLocomotion();

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = false;

        SetButtonVisible(true);
        CurrentState = SessionState.Idle;
    }

    // ================================================================
    // LOCOMOTION LOCK
    // ================================================================

    /// <summary>
    /// Disables all LocomotionProvider components (move, turn, teleport)
    /// so the player cannot move with controllers during journaling.
    /// Physical head movement (looking around) is unaffected.
    /// </summary>
    private void LockLocomotion()
    {
        disabledLocomotionProviders.Clear();

        var providers = FindObjectsByType<LocomotionProvider>(FindObjectsSortMode.None);
        foreach (var provider in providers)
        {
            if (provider.enabled)
            {
                provider.enabled = false;
                disabledLocomotionProviders.Add(provider);
            }
        }

        if (disabledLocomotionProviders.Count > 0)
            Debug.Log($"[JournalSession] Locomotion locked — disabled {disabledLocomotionProviders.Count} provider(s).");
    }

    /// <summary>
    /// Called by JournalReviewController mid-review to allow the player to walk
    /// to the bottle rack or ocean edge after the Keep/Discard choice is made.
    /// </summary>
    public void AllowLocomotion() => UnlockLocomotion();

    /// <summary>
    /// Re-enables all locomotion providers that were disabled by LockLocomotion().
    /// </summary>
    private void UnlockLocomotion()
    {
        foreach (var provider in disabledLocomotionProviders)
        {
            if (provider != null)
                provider.enabled = true;
        }

        if (disabledLocomotionProviders.Count > 0)
            Debug.Log($"[JournalSession] Locomotion unlocked — re-enabled {disabledLocomotionProviders.Count} provider(s).");

        disabledLocomotionProviders.Clear();
    }

    /// <summary>
    /// Restores the XR Origin's Y position to what it was before seated adjustment.
    /// </summary>
    private void RestoreXROriginHeight()
    {
        if (xrOrigin != null)
        {
            xrOrigin.position = new Vector3(
                xrOrigin.position.x,
                originalXROriginY,
                xrOrigin.position.z);
        }
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private void SetButtonVisible(bool visible)
    {
        if (startButton != null)
            startButton.gameObject.SetActive(visible);
    }

    private void SetWhiteboardUIActive(bool active)
    {
        var pm = WhiteboardPageManager.Instance;
        if (pm != null && pm.uiCanvas != null)
            pm.uiCanvas.gameObject.SetActive(active);
    }

    private void CleanupWhiteboard()
    {
        // Static whiteboard stays in scene — nothing to destroy or restore.
    }

    // ================================================================
    // INSTRUCTION UI (fallback when CalibrationGuide is null)
    // ================================================================

    private void ShowInstruction(string message)
    {
        // Always use our own world-space TMP so CalibrationGuide's internal
        // event handlers (OnConfirmationProgress / OnTableConfirmed) cannot
        // overwrite the message we set.
        if (instructionText == null)
            CreateInstructionText();
        if (instructionText == null) return;

        instructionText.text = message;
        instructionText.gameObject.SetActive(true);
        UpdateInstructionPosition();
    }

    private void HideInstruction()
    {
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
        if (Instance == this) Instance = null;

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
