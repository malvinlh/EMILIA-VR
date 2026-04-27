using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

[DisallowMultipleComponent]
public class LoginKeyboardSetup : MonoBehaviour
{
    [Header("Input Fields")]
    [SerializeField] private TMP_InputField nicknameInput;
    [SerializeField] private TMP_InputField fullNameInput;

    [Header("Scene Keyboard")]
    [Tooltip("Optional explicit keyboard reference. If empty, the first XRKeyboard found in the scene is used.")]
    [SerializeField] private XRKeyboard sceneKeyboard;

    private UnityAction<string> _nicknameSelected;
    private UnityAction<string> _fullNameSelected;

    private void Start()
    {
        if (sceneKeyboard == null)
            sceneKeyboard = FindObjectOfType<XRKeyboard>(true);

        if (sceneKeyboard == null)
        {
            Debug.LogWarning("[LoginKeyboardSetup] No scene XRKeyboard found. Assign the hierarchy XRI Spatial Keyboard to sceneKeyboard.");
            return;
        }

        EnsureKeyboardDisplay(nicknameInput, sceneKeyboard);
        EnsureKeyboardDisplay(fullNameInput, sceneKeyboard);

        HookFocusHandoff();

        sceneKeyboard.Close();
    }

    private void OnDestroy()
    {
        if (nicknameInput != null && _nicknameSelected != null)
            nicknameInput.onSelect.RemoveListener(_nicknameSelected);

        if (fullNameInput != null && _fullNameSelected != null)
            fullNameInput.onSelect.RemoveListener(_fullNameSelected);
    }

    private void HookFocusHandoff()
    {
        if (nicknameInput != null)
        {
            _nicknameSelected = _ => ActivateOnly(nicknameInput, fullNameInput);
            nicknameInput.onSelect.AddListener(_nicknameSelected);
        }

        if (fullNameInput != null)
        {
            _fullNameSelected = _ => ActivateOnly(fullNameInput, nicknameInput);
            fullNameInput.onSelect.AddListener(_fullNameSelected);
        }
    }

    private static void ActivateOnly(TMP_InputField activeField, TMP_InputField otherField)
    {
        if (otherField != null)
        {
            otherField.DeactivateInputField();
            otherField.ReleaseSelection();
        }

        if (activeField != null && EventSystem.current != null && EventSystem.current.currentSelectedGameObject != activeField.gameObject)
            EventSystem.current.SetSelectedGameObject(activeField.gameObject);
    }

    private static void EnsureKeyboardDisplay(TMP_InputField field, XRKeyboard keyboard)
    {
        if (field == null) return;

        var display = field.GetComponent<XRKeyboardDisplay>();
        if (display == null)
            display = field.gameObject.AddComponent<XRKeyboardDisplay>();

        display.keyboard = keyboard;
        display.useSceneKeyboard = true;
        display.updateOnKeyPress = true;
        display.inputField = field;
    }
}
