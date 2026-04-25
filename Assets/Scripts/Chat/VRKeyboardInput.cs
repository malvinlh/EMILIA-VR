using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// Bridges the XRI Spatial Keyboard to <see cref="VRChatBridge"/>.
///
/// Contains a <see cref="TMP_InputField"/> that, when focused (poked / ray-selected),
/// activates the scene's <see cref="GlobalNonNativeKeyboard"/>. Text is sent to the
/// chat bridge either by pressing Enter on the keyboard or by poking the Send button.
///
/// The <see cref="XRKeyboardDisplay"/> on this same GameObject handles the
/// InputField and keyboard bridge automatically.
///
/// <b>Testing only</b> - disable this component (or deactivate the GameObject)
/// to remove keyboard input in production builds.
/// </summary>
public class VRKeyboardInput : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private VRChatBridge _chatBridge;

    [Header("Input")]
    [SerializeField] private TMP_InputField _inputField;
    [SerializeField] private Button _sendButton;
    [SerializeField] private XRSimpleInteractable _sendPoke;

    [Header("Keyboard Display")]
    [Tooltip("The XRKeyboardDisplay on this object. If null, auto-resolved via GetComponent.")]
    [SerializeField] private XRKeyboardDisplay _keyboardDisplay;

    private void Awake()
    {
        if (_keyboardDisplay == null)
            _keyboardDisplay = GetComponentInChildren<XRKeyboardDisplay>();

        if (_sendPoke == null && _sendButton != null)
            _sendPoke = _sendButton.GetComponent<XRSimpleInteractable>();
    }

    private void OnEnable()
    {
        if (_keyboardDisplay != null)
            _keyboardDisplay.onTextSubmitted.AddListener(OnKeyboardSubmit);

        if (_sendButton != null)
            _sendButton.onClick.AddListener(OnSendClicked);

        if (_chatBridge != null)
            _chatBridge.OnControlInputLockChanged += UpdateInputLockUI;

        UpdateInputLockUI(_chatBridge != null && _chatBridge.IsControlInputLocked);
    }

    private void OnDisable()
    {
        if (_keyboardDisplay != null)
            _keyboardDisplay.onTextSubmitted.RemoveListener(OnKeyboardSubmit);

        if (_sendButton != null)
            _sendButton.onClick.RemoveListener(OnSendClicked);

        if (_chatBridge != null)
            _chatBridge.OnControlInputLockChanged -= UpdateInputLockUI;
    }

    private void OnKeyboardSubmit(string text)
    {
        Submit(text);
    }

    private void OnSendClicked()
    {
        if (_inputField == null) return;
        Submit(_inputField.text);
    }

    private void Submit(string text)
    {
        if (_chatBridge == null || string.IsNullOrWhiteSpace(text)) return;

        if (!_chatBridge.SendTextMessage(text))
            return;

        // Clear input field after a successful send.
        if (_inputField != null)
            _inputField.text = string.Empty;
    }

    private void UpdateInputLockUI(bool isLocked)
    {
        if (_sendButton != null)
            _sendButton.interactable = !isLocked;

        if (_sendPoke != null)
            _sendPoke.enabled = !isLocked;

        if (_inputField != null)
            _inputField.interactable = !isLocked;
    }
}
