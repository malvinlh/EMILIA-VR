using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// VR-native control panel with pokeable buttons for chat actions:
/// - New Chat: starts a fresh conversation
/// - Toggle Reasoning: switches between standard and agentic/reasoning mode
/// - History: shows/hides the <see cref="VRHistoryPanel"/>
///
/// Build as a small world-space Canvas (e.g., 0.3m x 0.12m) placed within
/// arm's reach in the scene, or parented to a hand menu anchor.
///
/// Prefab hierarchy:
/// <code>
/// VRControlPanel (root)
///   +-- [VRControlPanel.cs]
///   +-- Canvas (World Space)
///       +-- Panel (dark background)
///           +-- NewChatButton    (Button + XRSimpleInteractable)
///           +-- ReasoningToggle  (Button + XRSimpleInteractable)
///           |   +-- ToggleLabel  (TMP "Standard" / "Reasoning")
///           +-- HistoryButton    (Button + XRSimpleInteractable)
/// </code>
/// </summary>
public class VRControlPanel : MonoBehaviour
{
    #region Inspector

    [Header("References")]
    [SerializeField] private VRChatBridge _chatBridge;
    [SerializeField] private VRHistoryPanel _historyPanel;

    [Header("Buttons")]
    [SerializeField] private Button _newChatButton;
    [SerializeField] private Button _reasoningToggleButton;
    [SerializeField] private Button _historyButton;

    [Header("Poke Targets (optional - for hand poke support)")]
    [SerializeField] private XRSimpleInteractable _newChatPoke;
    [SerializeField] private XRSimpleInteractable _reasoningPoke;
    [SerializeField] private XRSimpleInteractable _historyPoke;
    [SerializeField] private XRSimpleInteractable _micPoke;

    [Header("Mic Button")]
    [SerializeField] private Button _micButton;
    [SerializeField] private TMP_Text _micLabel;
    [SerializeField] private Color _micIdleColor = new(0.55f, 0.65f, 0.75f, 1f);
    [SerializeField] private Color _micRecordingColor = new(0.9f, 0.25f, 0.25f, 1f);

    [Header("UI Elements")]
    [Tooltip("Label that shows current mode: 'Standard' or 'Reasoning'.")]
    [SerializeField] private TMP_Text _reasoningLabel;

    [Tooltip("Optional icon/color tint on the reasoning button to indicate state.")]
    [SerializeField] private Image _reasoningIndicator;

    [Header("Colors")]
    [SerializeField] private Color _standardColor = new(0.55f, 0.65f, 0.75f, 1f);
    [SerializeField] private Color _reasoningColor = new(0.45f, 0.65f, 0.95f, 1f);

    #endregion

    #region Unity

    private void Awake()
    {
        // Button click listeners (for controller ray interaction)
        if (_newChatButton != null)
            _newChatButton.onClick.AddListener(OnNewChat);
        if (_reasoningToggleButton != null)
            _reasoningToggleButton.onClick.AddListener(OnToggleReasoning);
        if (_historyButton != null)
            _historyButton.onClick.AddListener(OnToggleHistory);

        // Poke listeners (for hand tracking)
        if (_newChatPoke != null)
            _newChatPoke.selectEntered.AddListener(_ => OnNewChat());
        if (_reasoningPoke != null)
            _reasoningPoke.selectEntered.AddListener(_ => OnToggleReasoning());
        if (_historyPoke != null)
            _historyPoke.selectEntered.AddListener(_ => OnToggleHistory());
        if (_micPoke != null)
            _micPoke.selectEntered.AddListener(_ => OnToggleMic());

        // Mic button (controller ray)
        if (_micButton != null)
            _micButton.onClick.AddListener(OnToggleMic);
    }

    private void OnEnable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnReasoningModeChanged += UpdateReasoningUI;
            _chatBridge.OnMicStateChanged += UpdateMicUI;
            _chatBridge.OnControlInputLockChanged += UpdateControlLockUI;
        }

        UpdateReasoningUI(_chatBridge != null && _chatBridge.IsReasoningMode);
        UpdateMicUI(_chatBridge != null && _chatBridge.IsRecording);
        UpdateControlLockUI(_chatBridge != null && _chatBridge.IsControlInputLocked);
    }

    private void OnDisable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnReasoningModeChanged -= UpdateReasoningUI;
            _chatBridge.OnMicStateChanged -= UpdateMicUI;
            _chatBridge.OnControlInputLockChanged -= UpdateControlLockUI;
        }
    }

    #endregion

    #region Button Handlers

    private void OnNewChat()
    {
        if (_chatBridge == null || _chatBridge.IsControlInputLocked) return;
        _chatBridge.StartNewChat();
    }

    private void OnToggleReasoning()
    {
        if (_chatBridge == null || _chatBridge.IsControlInputLocked) return;
        _chatBridge.ToggleReasoningMode();
    }

    private void OnToggleHistory()
    {
        if (_chatBridge != null && _chatBridge.IsControlInputLocked) return;
        if (_historyPanel == null) return;
        _historyPanel.Toggle();
    }

    private void OnToggleMic()
    {
        if (_chatBridge == null || _chatBridge.IsControlInputLocked) return;
        _chatBridge.ToggleMic();
    }

    #endregion

    #region UI Updates

    private void UpdateReasoningUI(bool isReasoning)
    {
        if (_reasoningLabel != null)
            _reasoningLabel.text = isReasoning ? "Reasoning" : "Standard";

        if (_reasoningIndicator != null)
            _reasoningIndicator.color = isReasoning ? _reasoningColor : _standardColor;
    }

    private void UpdateMicUI(bool isRecording)
    {
        if (_micLabel != null)
            _micLabel.text = isRecording ? "Stop" : "Mic";

        if (_micButton != null)
        {
            var img = _micButton.targetGraphic as Image;
            if (img != null)
                img.color = isRecording ? _micRecordingColor : _micIdleColor;
        }
    }

    private void UpdateControlLockUI(bool isLocked)
    {
        SetButtonLocked(_newChatButton, isLocked);
        SetButtonLocked(_reasoningToggleButton, isLocked);
        SetButtonLocked(_historyButton, isLocked);
        SetButtonLocked(_micButton, isLocked);

        SetPokeLocked(_newChatPoke, isLocked);
        SetPokeLocked(_reasoningPoke, isLocked);
        SetPokeLocked(_historyPoke, isLocked);
        SetPokeLocked(_micPoke, isLocked);
    }

    private static void SetButtonLocked(Button button, bool isLocked)
    {
        if (button != null)
            button.interactable = !isLocked;
    }

    private static void SetPokeLocked(XRSimpleInteractable poke, bool isLocked)
    {
        if (poke != null)
            poke.enabled = !isLocked;
    }

    #endregion
}
