using System.Collections;
using System.Collections.Generic;
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

    [Header("AR Managers")]
    [Tooltip("ARPlaneManager for table detection. Enabled only during detection.")]
    public ARPlaneManager arPlaneManager;

    [Header("Detection Mode")]
    [Tooltip("Skip AR plane scanning entirely. Uses hand-only detection like the " +
             "Quest 3 AR Surface Keyboard — instant dot grid feedback from palm positions. " +
             "Enable this for faster, lighter calibration without Scene Model dependency.")]
    public bool skipPlaneDetection;

    [Header("Scene Objects")]
    [Tooltip("The JournalChairTable parent that will be repositioned.")]
    public Transform journalChairTable;
    [Tooltip("The JournalTable child (used for alignment reference).")]
    public Transform journalTable;
    [Tooltip("The Chair child.")]
    public Transform chair;

    [Header("Whiteboard Placeholder")]
    [Tooltip("A collider on the virtual table that defines where and at what scale the " +
             "whiteboard should spawn. Place a Box Collider (set as trigger) on the table " +
             "surface to visualize the whiteboard area in the editor. " +
             "If null, the whiteboard spawns at the raw MR-detected position.")]
    public Transform whiteboardPlaceholder;

    [Header("SeatPoint Calibration")]
    [Tooltip("The SeatPoint transform where the player will be teleported after calibration. " +
             "Its forward direction defines the player's seated facing direction. " +
             "If null, falls back to legacy AlignVRWorldToTable behaviour.")]
    public Transform seatPoint;
    [Tooltip("The XR Origin (XR Rig) root transform. Required for teleportation to SeatPoint.")]
    public Transform xrOrigin;
    [Tooltip("When true, the player's real-world eye height at calibration time is used " +
             "as the target camera Y (SeatPoint becomes the XZ + yaw target only). " +
             "This produces the most natural seated perspective.")]
    public bool useRealEyeHeight = true;

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
    private Vector3 originalTableLocalPosition;
    private float detectionTimeoutTimer;
    private bool hasTimedOut;
    private GameObject spawnedWhiteboard;
    private ARTableDetector.DetectedTable pendingTable;
    private bool scenePermissionGranted;
    private XRHandSubsystem handSubsystem;
    private List<LocomotionProvider> disabledLocomotionProviders = new List<LocomotionProvider>();
    private float originalXROriginY;
    private float capturedRealEyeHeight;
    private bool placeholderWasVisible;

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

            // Drive CalibrationGuide palm indicators from hand tracking data
            if (calibrationGuide != null && arTableDetector != null)
                UpdateCalibrationPalmIndicators();
        }

        // Keep fallback instruction text facing user during passthrough
        if (calibrationGuide == null && instructionText != null
            && instructionText.gameObject.activeSelf)
        {
            UpdateInstructionPosition();
        }
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

        // Capture player's real eye height at the moment of confirmation.
        // This is used later so the VR camera matches the passthrough perspective.
        capturedRealEyeHeight = table.userHeadPosition.y;

        Debug.Log($"[JournalSession] Table confirmed at {table.position}, " +
                  $"size={table.size}, AR={table.sourcePlane != null}. " +
                  $"User at {table.userHeadPosition} (capturedEyeY={capturedRealEyeHeight:F2}).");

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
                LockLocomotion();
                Debug.Log("[JournalSession] Journaling session started.");
            });
        }
        else
        {
            if (seatPoint != null && xrOrigin != null)
            {
                TeleportToSeatPoint(table);
                AdjustTableForDistanceMismatch(table);
            }
            else
            {
                AlignVRWorldToTable(table);
            }
            MoveWhiteboardToVRLayer();
            CurrentState = SessionState.Journaling;
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
            if (seatPoint != null && xrOrigin != null)
            {
                TeleportToSeatPoint(pendingTable);
                AdjustTableForDistanceMismatch(pendingTable);
            }
            else
            {
                AlignVRWorldToTable(pendingTable);
            }

            MoveWhiteboardToVRLayer();

            // Create spatial anchor for drift resistance
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

        if (whiteboardUtils == null) return;

        var wbComponent = whiteboardUtils.WhiteboardPrefab.GetComponent<Whiteboard>();
        if (wbComponent != null)
            wbComponent.backgroundColor = journalBackgroundColor;

        // Spawn at the hand-detected position (palm midpoint on real table).
        // Use the placeholder's BoxCollider size so the passthrough preview
        // matches the game-world whiteboard exactly.
        Vector3 spawnPos = table.position + Vector3.up * 0.002f;
        Vector2 spawnSize = GetPlaceholderWorldSize(table.size);

        spawnedWhiteboard = whiteboardUtils.SpawnAligned(spawnPos, table.rotation, spawnSize);

        if (spawnedWhiteboard != null)
        {
            int ptLayer = passthroughManager != null
                ? passthroughManager.GetPassthroughUILayer()
                : 31;
            PassthroughManager.SetLayerRecursive(spawnedWhiteboard, ptLayer);

            Debug.Log($"[JournalSession] Whiteboard spawned at {spawnPos}, " +
                      $"size={spawnSize} on layer {ptLayer} (passthrough preview).");
        }
    }

    /// <summary>
    /// Returns the whiteboard size from the placeholder's BoxCollider (world-space
    /// X and Z dimensions). Falls back to the MR-detected size if no placeholder
    /// or no BoxCollider is assigned.
    /// </summary>
    private Vector2 GetPlaceholderWorldSize(Vector2 fallbackSize)
    {
        if (whiteboardPlaceholder == null) return fallbackSize;

        BoxCollider boxCol = whiteboardPlaceholder.GetComponent<BoxCollider>();
        if (boxCol == null) return fallbackSize;

        Vector3 worldSize = Vector3.Scale(boxCol.size, whiteboardPlaceholder.lossyScale);
        return new Vector2(worldSize.x, worldSize.z);
    }

    private void MoveWhiteboardToVRLayer()
    {
        if (spawnedWhiteboard == null) return;

        const int WHITEBOARD_LAYER = 10;
        PassthroughManager.SetLayerRecursive(spawnedWhiteboard, WHITEBOARD_LAYER);

        // Reposition the whiteboard onto the placeholder if one is assigned.
        // Uses the BoxCollider (if present) for precise center alignment and
        // resizes the whiteboard to match the placeholder's defined area.
        if (whiteboardPlaceholder != null)
        {
            BoxCollider boxCol = whiteboardPlaceholder.GetComponent<BoxCollider>();
            if (boxCol != null)
            {
                // Use box collider's world-space center for precise alignment
                Vector3 worldCenter = whiteboardPlaceholder.TransformPoint(boxCol.center);
                spawnedWhiteboard.transform.position = worldCenter + Vector3.up * 0.002f;

                // Match the whiteboard scale to the placeholder's box collider size
                Vector3 worldSize = Vector3.Scale(boxCol.size, whiteboardPlaceholder.lossyScale);
                float scaleX = worldSize.x / 10f;
                float scaleZ = worldSize.z / 10f;
                spawnedWhiteboard.transform.localScale = new Vector3(scaleX, 0.1f / 10f, scaleZ);

                // Reinitialize the whiteboard texture for the new size
                var wb = spawnedWhiteboard.GetComponent<Whiteboard>();
                if (wb != null) wb.Initialize();
            }
            else
            {
                spawnedWhiteboard.transform.position =
                    whiteboardPlaceholder.position + Vector3.up * 0.002f;
            }
            spawnedWhiteboard.transform.rotation = whiteboardPlaceholder.rotation;

            Debug.Log($"[JournalSession] Whiteboard repositioned to placeholder at " +
                      $"{spawnedWhiteboard.transform.position}, " +
                      $"scale={spawnedWhiteboard.transform.localScale}.");

            // Hide the placeholder during journaling
            placeholderWasVisible = whiteboardPlaceholder.gameObject.activeSelf;
            whiteboardPlaceholder.gameObject.SetActive(false);
        }

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
    // SEATPOINT TELEPORTATION
    // ================================================================

    /// <summary>
    /// Teleports the XR Origin so the player's camera ends up at SeatPoint's
    /// position and forward direction. This creates a natural seated experience
    /// where the player appears at the virtual desk.
    ///
    /// When useRealEyeHeight is true, the Y position comes from the player's
    /// actual eye height captured during passthrough calibration — not the
    /// SeatPoint's Y. This eliminates "too low / too high" mismatches.
    /// </summary>
    private void TeleportToSeatPoint(ARTableDetector.DetectedTable table)
    {
        if (seatPoint == null || xrOrigin == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Save original Y for restoration when session ends
        originalXROriginY = xrOrigin.position.y;

        // 1. Rotate XR Origin so camera faces SeatPoint's forward direction
        float currentCamY = cam.transform.eulerAngles.y;
        float targetY = seatPoint.eulerAngles.y;
        float yawDelta = targetY - currentCamY;

        xrOrigin.RotateAround(cam.transform.position, Vector3.up, yawDelta);

        // 2. Translate XR Origin so camera ends up at SeatPoint position (XZ)
        Vector3 camPos = cam.transform.position;
        Vector3 offset = seatPoint.position - camPos;
        offset.y = 0f;
        xrOrigin.position += offset;

        // 3. Height adjustment
        float targetEyeY;
        if (useRealEyeHeight && capturedRealEyeHeight > 0f)
        {
            // Use the player's actual eye height from when they confirmed the table.
            // This keeps the VR perspective identical to what they saw in passthrough.
            targetEyeY = capturedRealEyeHeight;
        }
        else
        {
            // Fallback: use SeatPoint's Y as target eye height
            targetEyeY = seatPoint.position.y;
        }

        float trackingHeight = cam.transform.position.y - xrOrigin.position.y;
        xrOrigin.position = new Vector3(
            xrOrigin.position.x,
            targetEyeY - trackingHeight,
            xrOrigin.position.z);

        Debug.Log($"[JournalSession] Teleported to SeatPoint. " +
                  $"XR Origin → {xrOrigin.position}, yaw delta={yawDelta:F1}°, " +
                  $"camera Y target={targetEyeY:F2} " +
                  $"(useRealEyeHeight={useRealEyeHeight}, captured={capturedRealEyeHeight:F2})");
    }

    /// <summary>
    /// Handles the distance mismatch between the real-world chair-to-table
    /// distance and the virtual SeatPoint-to-JournalTable distance.
    ///
    /// Approach: Offset the JournalTable along SeatPoint's forward axis
    /// so the virtual table sits at the same relative distance as the real one.
    /// This keeps the whiteboard reachable and prevents arm-length discomfort.
    /// </summary>
    private void AdjustTableForDistanceMismatch(ARTableDetector.DetectedTable table)
    {
        if (seatPoint == null || journalTable == null || journalChairTable == null) return;

        // When a placeholder defines the whiteboard position, the scene layout
        // is authoritative — moving journalTable would shift the placeholder
        // away from its designed position. TeleportToSeatPoint already places
        // the user at the correct viewing distance.
        if (whiteboardPlaceholder != null)
        {
            Debug.Log("[JournalSession] Skipping distance adjustment — " +
                      "whiteboardPlaceholder defines authoritative position.");
            return;
        }

        // Real-world horizontal distance from user's head to detected table center
        Vector3 headXZ = new Vector3(table.userHeadPosition.x, 0f, table.userHeadPosition.z);
        Vector3 tableXZ = new Vector3(table.position.x, 0f, table.position.z);
        float realDistance = Vector3.Distance(headXZ, tableXZ);

        // Virtual horizontal distance from SeatPoint to JournalTable
        Vector3 seatXZ = new Vector3(seatPoint.position.x, 0f, seatPoint.position.z);
        Vector3 virtualTableXZ = new Vector3(journalTable.position.x, 0f, journalTable.position.z);
        float virtualDistance = Vector3.Distance(seatXZ, virtualTableXZ);

        float distanceDelta = realDistance - virtualDistance;

        // Only adjust if the difference is noticeable (>5cm) to avoid unnecessary jitter
        if (Mathf.Abs(distanceDelta) < 0.05f)
        {
            Debug.Log($"[JournalSession] Distance mismatch is negligible " +
                      $"(real={realDistance:F2}m, virtual={virtualDistance:F2}m). No offset applied.");
            return;
        }

        // Push the table forward/backward along SeatPoint's forward axis
        Vector3 seatForward = seatPoint.forward;
        seatForward.y = 0f;
        seatForward.Normalize();

        journalTable.position += seatForward * distanceDelta;

        Debug.Log($"[JournalSession] Table distance offset applied: {distanceDelta:F3}m " +
                  $"(real={realDistance:F2}m, virtual={virtualDistance:F2}m). " +
                  $"JournalTable now at {journalTable.position}.");
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
        LockLocomotion();

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

        // Restore table local position if it was offset for distance mismatch
        if (journalTable != null)
            journalTable.localPosition = originalTableLocalPosition;

        RestoreXROriginHeight();
        UnlockLocomotion();

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

        // Restore table local position if it was offset for distance mismatch
        if (journalTable != null)
            journalTable.localPosition = originalTableLocalPosition;

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

    private void CleanupWhiteboard()
    {
        if (spawnedWhiteboard != null)
        {
            Destroy(spawnedWhiteboard);
            spawnedWhiteboard = null;
        }

        // Restore placeholder visibility
        if (whiteboardPlaceholder != null && placeholderWasVisible)
            whiteboardPlaceholder.gameObject.SetActive(true);
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
