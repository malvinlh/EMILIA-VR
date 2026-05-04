using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using EMILIA.Data;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using TMPro;

/// <summary>
/// Orchestrates the Mixed Reality journaling flow:
///   Idle → Passthrough → StylusCalibration → TablePlacement → Preview → TransitionToVR → Journaling
///
/// Flow (4-tap, stylus-driven):
///   1. Press start button → fade to passthrough (real world visible)
///   2. Stylus calibration: touch-the-fingertip multi-sample solve
///   3. Table placement: user taps 4 corners of writing area with calibrated pen
///   4. Brief preview → fade back to VR
///   5. Player is teleported to SeatPoint; virtual table offset adjusted to match real-world eye-above-table
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
        Passthrough,
        StylusCalibration,
        TablePlacement,
        Preview,
        TransitionToVR,
        Journaling,
        ReCalibrating,
        Ending
    }

    [Header("References")]
    public PassthroughManager passthroughManager;
    public TableTapCalibrator tableTapCalibrator;
    public AlignmentAnchor alignmentAnchor;
    public WhiteboardUtils whiteboardUtils;
    public JournalStartButton startButton;

    [Header("Post-Journal Review")]
    [Tooltip("Optional review flow controller. When assigned, EndSession() runs the review " +
             "before actually ending so the user can keep or release the journal.")]
    public JournalReviewController reviewController;

    [Header("Portal During Journaling")]
    [Tooltip("GameObjects to hide while journaling (e.g. PortalVFX, PortalGlass).")]
    public GameObject[] portalVisuals;
    [Tooltip("Behaviours to disable while journaling to block teleport (e.g. PortalSceneTransition).")]
    public Behaviour[] portalTriggers;

    [Header("Pen Toggle")]
    [Tooltip("Left-hand ThumbTip-to-MiddleTip distance (metres) required to fire the pen on/off toggle. " +
             "Lower = requires a tighter, more deliberate pinch. Tune on device if the toggle fires accidentally.")]
    [Range(0.005f, 0.04f)]
    public float penTogglePinchThreshold = 0.015f;

    [Header("Detection Mode")]
    [Tooltip("Editor / testing only. Skip all MR calibration and jump directly to the " +
             "Journaling state on Start. Useful for testing the journal UI and review flow " +
             "with XR Device Simulator without building to the headset.")]
    public bool skipToJournalingOnStart;

    [Header("Stylus Calibration")]
    [Tooltip("Optional stylus calibration controller. When assigned, a stylus calibration " +
             "step runs after passthrough entry and before table detection. The user holds " +
             "their physical pen and touches the tip to their opposite index fingertip to " +
             "compute the wrist-to-tip offset used by the StylusTipProvider at runtime.")]
    public StylusCalibrationController stylusCalibrationController;

    [Tooltip("Optional StylusTipProvider. When assigned, the detected writing plane is passed " +
             "to it after table confirmation, so the tip snaps to the real surface at runtime.")]
    public StylusTipProvider stylusTipProvider;

    [Tooltip("Optional StylusVisualProp. When assigned, the virtual pen is shown only during " +
             "the Journaling state and hidden during review/intro/other non-writing states.")]
    public StylusVisualProp stylusVisualProp;

    [Tooltip("Skip stylus calibration and use legacy finger-tip tracking. Useful for testing " +
             "without a physical pen or for users who prefer finger drawing. When true, the " +
             "4-tap flow uses the index fingertip as the tap source instead of the pen tip.")]
    public bool skipStylusCalibration;

    [Header("Scene Objects")]
    [Tooltip("The JournalChairTable parent that will be repositioned.")]
    public Transform journalChairTable;
    [Tooltip("The JournalTable child (used for alignment reference).")]
    public Transform journalTable;
    [Tooltip("The Chair child.")]
    public Transform chair;

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

    [Header("MainIsland Boundary (Editor Tool)")]
    [Tooltip("Extra padding for the optional outer box wall generator (metres).")]
    [Range(0f, 5f)]
    public float boundaryPadding = 0.35f;

    [Tooltip("Generated wall height (metres).")]
    [Range(0.5f, 6f)]
    public float boundaryWallHeight = 2.2f;

    [Tooltip("Generated wall thickness (metres).")]
    [Range(0.02f, 1f)]
    public float boundaryWallThickness = 0.2f;

    [Tooltip("Inset from MainIsland renderer bounds for the coastline ring (metres).")]
    [Range(0f, 5f)]
    public float boundaryCoastInset = 0.75f;

    [Tooltip("Number of wall segments used to approximate the coastline ring.")]
    [Range(8, 64)]
    public int boundaryRingSegments = 28;

    [Tooltip("Also generate an outer rectangular guard wall around MainIsland bounds.")]
    public bool boundaryCreateOuterBox = true;

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
    [Tooltip("World-space TextMeshPro for instruction prompts. Created at runtime if null.")]
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
    private TableTapCalibrator.DetectedTable pendingTable;
    private bool calibrationDataValid;
    private string _sessionCreatedAtIso;    // ISO timestamp for DB (UTC+7)
    private string _sessionCreatedAtDisplay; // formatted timestamp for title-page label
    private List<LocomotionProvider> disabledLocomotionProviders = new List<LocomotionProvider>();
    private float originalXROriginY;
    private float capturedRealEyeHeight;
    private float originalHeightAdjustmentY;
    private bool hasAdjustedHeightAdjustment;

    // Per-scene-visit calibration cache: once a session in this scene has
    // completed table placement, subsequent sessions skip passthrough/stylus/
    // table-tap calibration and reuse the saved DetectedTable. The cache is an
    // instance field, so it is naturally cleared when the scene unloads and
    // a fresh manager is constructed on re-entry.
    private bool hasCalibratedThisSceneVisit;

    // ── Pen Toggle (Journaling) ──────────────────────────────────────
    private bool _penEnabled = true;
    private bool _togglePrevPinched;
    private XRHandSubsystem _cachedHandSubsystem;
    private Transform _cachedCameraOffset;

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
        if (CurrentState == SessionState.TablePlacement)
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

        // Keep instruction text facing user and at arm's length during passthrough
        if (instructionText != null && instructionText.gameObject.activeSelf)
            UpdateInstructionPosition();

        if (CurrentState == SessionState.Journaling)
            UpdatePenToggle();
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

        if (hasCalibratedThisSceneVisit && calibrationDataValid)
            StartSubsequentSession();
        else
            ProceedToPassthrough();
    }

    /// <summary>
    /// Fast path for the 2nd, 3rd, … journaling session in the same scene visit.
    /// Reuses the DetectedTable / SeatPoint / eye-height calibration captured by
    /// the first session in this scene, so the user can resume journaling
    /// without redoing passthrough, stylus calibration, or the 4-tap flow.
    /// </summary>
    private void StartSubsequentSession()
    {
        Debug.Log("[JournalSession] Subsequent same-scene session — skipping calibration.");

        LockLocomotion();
        TeleportToSeatPoint();

        if (alignmentAnchor != null && calibrationDataValid)
        {
            Pose tablePose = new Pose(pendingTable.position, pendingTable.rotation);
            alignmentAnchor.CreateAnchorAtTable(tablePose);
        }

        CurrentState = SessionState.Journaling;
        OnJournalingEntered();
        SetWhiteboardUIActive(true);
    }

    private void ProceedToPassthrough()
    {
        CurrentState = SessionState.Passthrough;

        bool willCalibrateStylus = !skipStylusCalibration && stylusCalibrationController != null;

        ShowInstruction(willCalibrateStylus
            ? "Hold your pen and prepare to calibrate\nwhen the target appears."
            : "Tap the four corners of your writing\narea with your index fingertip.");

        if (passthroughManager != null)
            passthroughManager.EnterPassthrough(() =>
            {
                if (willCalibrateStylus) EnterStylusCalibration();
                else EnterTablePlacement();
            });
        else if (willCalibrateStylus) EnterStylusCalibration();
        else EnterTablePlacement();
    }

    // ================================================================
    // STYLUS CALIBRATION
    // ================================================================

    private void EnterStylusCalibration()
    {
        CurrentState = SessionState.StylusCalibration;

        Debug.Log("[JournalSession] Entered StylusCalibration state.");

        // StylusCalibrationController owns its own instruction text during this phase.
        HideInstruction();

        stylusCalibrationController.OnCalibrationComplete += OnStylusCalibrationComplete;
        stylusCalibrationController.OnNextButtonPressed += OnStylusCalibrationNext;
        stylusCalibrationController.BeginCalibration();
    }

    private void OnStylusCalibrationComplete()
    {
        Debug.Log("[JournalSession] Stylus calibration complete — awaiting Next button.");
    }

    private void OnStylusCalibrationNext()
    {
        if (stylusCalibrationController != null)
        {
            stylusCalibrationController.OnCalibrationComplete -= OnStylusCalibrationComplete;
            stylusCalibrationController.OnNextButtonPressed -= OnStylusCalibrationNext;
            stylusCalibrationController.Cleanup();
        }

        var stylusRecord = StylusCalibrationStore.Load();
        Debug.Log($"[JournalSession] Transitioning to TablePlacement. " +
                  $"StylusRMS={(stylusRecord != null ? $"{stylusRecord.rmsResidualMeters * 1000f:F1} mm" : "n/a")}");
        EnterTablePlacement();
    }

    // ================================================================
    // TABLE PLACEMENT (4-tap)
    // ================================================================

    private void EnterTablePlacement()
    {
        CurrentState = SessionState.TablePlacement;
        hasTimedOut = false;
        detectionTimeoutTimer = 0f;

        Debug.Log("[JournalSession] Entered TablePlacement state.");

        if (tableTapCalibrator == null)
        {
            Debug.LogError("[JournalSession] No TableTapCalibrator assigned — cannot place table.");
            FallbackSpawn();
            return;
        }

        tableTapCalibrator.OnTableConfirmed += OnTableConfirmed;
        tableTapCalibrator.BeginCalibration();
    }

    private void OnTableConfirmed(TableTapCalibrator.DetectedTable table)
    {
        if (hasTimedOut) return;
        if (CurrentState != SessionState.TablePlacement) return;

        if (tableTapCalibrator != null)
        {
            tableTapCalibrator.OnTableConfirmed -= OnTableConfirmed;
            tableTapCalibrator.Cleanup();
        }

        CurrentState = SessionState.Preview;

        // Capture averaged eye height sampled across the tap sequence.
        capturedRealEyeHeight = table.avgEyeY;

        Debug.Log($"[JournalSession] Table confirmed at {table.position}, " +
                  $"size={table.size}, tapSurfaceY={table.avgTapSurfaceY:F3}. " +
                  $"User at {table.userHeadPosition} (capturedEyeY={capturedRealEyeHeight:F2}).");

        // Mark the per-scene-visit cache warm so subsequent sessions can skip
        // the calibration flow (see StartSubsequentSession). pendingTable and
        // calibrationDataValid have just been populated above.
        hasCalibratedThisSceneVisit = true;

        StartCoroutine(PreviewAndTransition(table));
    }

    // ================================================================
    // PREVIEW & TRANSITION
    // ================================================================

    private IEnumerator PreviewAndTransition(TableTapCalibrator.DetectedTable table)
    {
        SpawnWhiteboardForPreview(table);

        ShowInstruction("Calibrating...");

        yield return new WaitForSeconds(previewDuration * 0.6f);

        ShowInstruction("Done, returning to the game world.");
        yield return new WaitForSeconds(previewDuration * 0.4f);

        CurrentState = SessionState.TransitionToVR;
        HideInstruction();

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

    private void SpawnWhiteboardForPreview(TableTapCalibrator.DetectedTable table)
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

    private void AlignVRWorldToTable(TableTapCalibrator.DetectedTable table)
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
    /// Teleports the XR Origin so the player's camera ends up at SeatPoint's XZ
    /// position and forward direction (yaw). Camera Y is calibrated from passthrough:
    ///   targetEyeY = virtualTableSurfaceY + (realEyeY - realTableY)
    /// This makes the virtual whiteboard appear at the same eye-relative height as the
    /// real table the user tapped. Scene objects are never moved.
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
        float targetEyeY;
        bool hasExpectedEyeAboveTable = false;
        float expectedEyeAboveTable = 0f;
        if (calibrationDataValid)
        {
            // Surface Y comes from tap contact points — no palm-thickness bias.
            float realEyeAboveTable = capturedRealEyeHeight - pendingTable.avgTapSurfaceY;
            realEyeAboveTable = Mathf.Clamp(
                realEyeAboveTable,
                Mathf.Min(realEyeAboveTableClamp.x, realEyeAboveTableClamp.y),
                Mathf.Max(realEyeAboveTableClamp.x, realEyeAboveTableClamp.y));

            expectedEyeAboveTable = realEyeAboveTable;
            hasExpectedEyeAboveTable = true;

            float virtualTableY = GetVirtualTableSurfaceY();
            targetEyeY = virtualTableY + realEyeAboveTable + calibrationHeightBias;

            Debug.Log($"[JournalSession] Eye-height calibrated: realEyeAboveTable=" +
                      $"{realEyeAboveTable:F3}m (eye={capturedRealEyeHeight:F3}, tapSurf={pendingTable.avgTapSurfaceY:F3}), " +
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
    /// </summary>
    private void AdjustIslandHeight(TableTapCalibrator.DetectedTable table)
    {
        Transform adjustmentRoot = ResolveHeightAdjustmentRoot();
        if (adjustmentRoot == null)
            return;

        Camera cam = Camera.main;
        if (cam == null) return;

        float realEyeAboveTable = capturedRealEyeHeight - table.position.y;
        realEyeAboveTable = Mathf.Clamp(
            realEyeAboveTable,
            Mathf.Min(eyeAboveTableClamp.x, eyeAboveTableClamp.y),
            Mathf.Max(eyeAboveTableClamp.x, eyeAboveTableClamp.y));

        float virtualTableSurfaceY = GetVirtualTableSurfaceY();

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
        if (tableWritingSurface != null)
        {
            float y = tableWritingSurface.position.y;
            Debug.Log($"[JournalSession] VirtualTableSurfaceY={y:F3}m from tableWritingSurface '{tableWritingSurface.name}'");
            return y;
        }

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
            passthroughManager.EnterPassthrough(() => EnterTablePlacement());
        else
            EnterTablePlacement();
    }

    // ================================================================
    // FALLBACK
    // ================================================================

    private void FallbackSpawn()
    {
        if (tableTapCalibrator != null)
        {
            tableTapCalibrator.OnTableConfirmed -= OnTableConfirmed;
            tableTapCalibrator.Cleanup();
        }

        HideInstruction();

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
        _penEnabled = true;
        ApplyPenEnabled(true);

        // During writing, keep AZKi hidden and non-locomoting until review begins.
        reviewController?.EnterJournalingMode();

        // Re-lock canvas orientation now that the player is at SeatPoint facing the
        // whiteboard. LockTextOrientation() at Start() uses the camera's initial
        // position which may differ from the seated position (e.g. bedroom).
        ScribbleManager.Instance?.RelockOrientation();

        // Hand the virtual whiteboard surface plane to the stylus tip provider so
        // it can snap the tip to the writing plane during drawing.
        if (stylusTipProvider != null)
        {
            float surfaceY = GetVirtualTableSurfaceY();
            stylusTipProvider.SetWritingPlane(new Plane(Vector3.up, new Vector3(0f, surfaceY, 0f)));
            Debug.Log($"[JournalSession] StylusTipProvider writing plane set at Y={surfaceY:F3}m");
        }

        // Show the virtual pen prop only while actually writing.
        stylusVisualProp?.SetPropEnabled(true);

        // Reset pages: title page (0) + first content page (1)
        ScribbleManager.Instance?.ClearAll();

        // Stamp timestamps — UTC+7 (Asia/Jakarta) to match legacy JournalManager
        var now = DateTime.UtcNow.AddHours(7);
        _sessionCreatedAtIso     = now.ToString("yyyy-MM-ddTHH:mm:ss");
        _sessionCreatedAtDisplay = now.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture);

        WhiteboardPageManager.Instance?.SetCreatedAt(_sessionCreatedAtDisplay);

        Debug.Log($"[JournalSession] Title page ready — created at {_sessionCreatedAtDisplay}");

        SetPortalActive(false);
    }

    // ================================================================
    // PEN TOGGLE (left-hand thumb + middle finger pinch)
    // ================================================================

    private void UpdatePenToggle()
    {
        bool currentlyPinching = IsPenTogglePinching();

        if (currentlyPinching && !_togglePrevPinched)
        {
            _penEnabled = !_penEnabled;
            ApplyPenEnabled(_penEnabled);
            Debug.Log($"[JournalSession] Pen toggle: pen is now {(_penEnabled ? "ENABLED" : "DISABLED")}.");
        }

        _togglePrevPinched = currentlyPinching;
    }

    private bool IsPenTogglePinching()
    {
        if (_cachedHandSubsystem == null || !_cachedHandSubsystem.running)
            _cachedHandSubsystem = WhiteboardPen.GetHandSubsystem();

        if (_cachedHandSubsystem == null) return false;

        XRHand leftHand = _cachedHandSubsystem.leftHand;
        if (!leftHand.isTracked) return false;

        if (!leftHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose)) return false;
        if (!leftHand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middlePose)) return false;

        if (_cachedCameraOffset == null)
        {
            var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null && origin.CameraFloorOffsetObject != null)
                _cachedCameraOffset = origin.CameraFloorOffsetObject.transform;
        }

        Vector3 thumbW  = _cachedCameraOffset != null
            ? _cachedCameraOffset.TransformPoint(thumbPose.position) : thumbPose.position;
        Vector3 middleW = _cachedCameraOffset != null
            ? _cachedCameraOffset.TransformPoint(middlePose.position) : middlePose.position;

        return Vector3.Distance(thumbW, middleW) < penTogglePinchThreshold;
    }

    private void ApplyPenEnabled(bool enabled)
    {
        stylusTipProvider?.SetPenEnabled(enabled);
        stylusVisualProp?.SetPropEnabled(enabled);
    }

    // ================================================================
    // END SESSION
    // ================================================================

    public void EndSession()
    {
        if (CurrentState != SessionState.Journaling) return;

        CurrentState = SessionState.Ending;

        // Hide the virtual pen as soon as we leave Journaling.
        stylusVisualProp?.SetPropEnabled(false);

        if (reviewController != null)
        {
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
        if (saveJournal)
            yield return SaveJournalCoroutine();

        SetWhiteboardUIActive(false);

        yield return new WaitForSeconds(0.5f);

        CleanupWhiteboard();
        HideInstruction();

        if (alignmentAnchor != null)
            alignmentAnchor.ReleaseAnchor();

        // Keep calibrationDataValid + pendingTable populated so the next
        // session in the same scene visit can fast-path via
        // StartSubsequentSession. The cache is invalidated only on scene
        // unload (instance destroyed) or explicit cancellation (ResetToIdle).
        RestoreXROriginHeight();
        UnlockLocomotion();

        if (whiteboardUtils != null)
            whiteboardUtils.suppressManualGestures = false;

        reviewController?.OnSessionEnded();

        SetButtonVisible(true);
        SetPortalActive(true);
        CurrentState = SessionState.Idle;
        Debug.Log("[JournalSession] Session ended. Book re-enabled.");
    }

    // ================================================================
    // JOURNAL SAVE
    // ================================================================

    private IEnumerator SaveJournalCoroutine()
    {
        var sm = ScribbleManager.Instance;
        if (sm == null) yield break;

        string title   = sm.GetTitleText().Trim();
        string content = sm.GetContentText().Trim();

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

        if (savedJournal != null && ServiceManager.Instance?.SentimentApi != null)
            StartCoroutine(AnalyzeAndSaveSentiment(savedJournal.Id, content));
    }

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

        if (passthroughManager != null)
            passthroughManager.CancelTransition();

        if (tableTapCalibrator != null)
        {
            tableTapCalibrator.OnTableConfirmed -= OnTableConfirmed;
            tableTapCalibrator.Cleanup();
        }

        HideInstruction();

        if (stylusCalibrationController != null)
        {
            stylusCalibrationController.OnCalibrationComplete -= OnStylusCalibrationComplete;
            stylusCalibrationController.OnNextButtonPressed -= OnStylusCalibrationNext;
            stylusCalibrationController.Cleanup();
        }

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
        hasCalibratedThisSceneVisit = false;
        SetWhiteboardUIActive(false);
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

    public void AllowLocomotion() => UnlockLocomotion();

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

    private void SetPortalActive(bool active)
    {
        if (portalVisuals != null)
            foreach (var go in portalVisuals)
                if (go != null) go.SetActive(active);
        if (portalTriggers != null)
            foreach (var b in portalTriggers)
                if (b != null) b.enabled = active;
    }

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
    // INSTRUCTION UI
    // ================================================================

    private void ShowInstruction(string message)
    {
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

        instructionText.fontSize = 0.3f;
        instructionText.alignment = TextAlignmentOptions.Center;
        instructionText.color = new Color(0.95f, 0.92f, 0.85f);
        instructionText.rectTransform.sizeDelta = new Vector2(1.35f, 0.48f);
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

        if (tableTapCalibrator != null)
            tableTapCalibrator.OnTableConfirmed -= OnTableConfirmed;

        if (passthroughManager != null)
            passthroughManager.OnPassthroughExited -= OnceAfterPassthroughExit;
    }
}
