using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

[RequireComponent(typeof(Button), typeof(Image))]
/// <summary>
/// Custom button styling controller that changes background, icon, and text colors
/// depending on the button state (normal, hover, pressed, disabled).
/// 
/// - Implements Unity event interfaces (<see cref="IPointerEnterHandler"/>, etc.)
///   to react to pointer interactions.
/// - Works with <see cref="TextMeshProUGUI"/> labels and optional extra icons.
/// - Colors are assigned per state via the Inspector.
/// </summary>
public class CustomButton : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IPointerDownHandler, IPointerUpHandler
{
    #region Inspector Fields

    [Header("UI References")]
    [Tooltip("Background image to tint when state changes (required).")]
    [SerializeField] private Image backgroundImage;

    [Tooltip("Optional extra image, e.g. an icon, that will also be tinted.")]
    [SerializeField] private Image extraImage;

    [Tooltip("Optional label text (TextMeshPro) to tint on state changes.")]
    [SerializeField] private TextMeshProUGUI labelText;

    [Header("Background Colors")]
    [SerializeField] private Color normalBg   = new Color(0, 0, 0, 0);
    [SerializeField] private Color hoverBg    = new Color(0.6f, 0.5f, 0.9f, 1f);
    [SerializeField] private Color pressedBg  = new Color(0.5f, 0.4f, 0.8f, 1f);
    [SerializeField] private Color disabledBg = new Color(0, 0, 0, 0.2f);

    [Header("Extra Image Colors")]
    [SerializeField] private Color normalExtra   = Color.white;
    [SerializeField] private Color hoverExtra    = Color.white;
    [SerializeField] private Color pressedExtra  = Color.white;
    [SerializeField] private Color disabledExtra = Color.gray;

    [Header("Text Colors")]
    [SerializeField] private Color normalText   = Color.black;
    [SerializeField] private Color hoverText    = Color.white;
    [SerializeField] private Color pressedText  = Color.white;
    [SerializeField] private Color disabledText = Color.gray;

    #endregion

    /// <summary>
    /// Cached reference to the attached Unity <see cref="Button"/>.
    /// </summary>
    private Button _button;

    #region Unity Lifecycle

    /// <summary>
    /// Unity Reset: auto-assigns references when first added in the editor.
    /// </summary>
    private void Reset()
    {
        backgroundImage = GetComponent<Image>();
        labelText       = GetComponentInChildren<TextMeshProUGUI>();
        // extraImage remains optional
    }

    /// <summary>
    /// Unity Awake: caches references and applies initial state.
    /// </summary>
    private void Awake()
    {
        _button = GetComponent<Button>();

        if (backgroundImage == null)
        {
            backgroundImage = GetComponent<Image>();
        }

        if (labelText == null)
        {
            labelText = GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
        }

        ApplyState(_button.interactable ? State.Normal : State.Disabled);
    }

    /// <summary>
    /// Unity OnEnable: ensures correct state is applied on re-enable.
    /// </summary>
    private void OnEnable()
    {
        ApplyState(_button.interactable ? State.Normal : State.Disabled);
    }

    #endregion

    #region Pointer Event Handlers

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!_button.interactable) return;
        ApplyState(State.Hover);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (!_button.interactable) return;
        ApplyState(State.Normal);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!_button.interactable) return;
        ApplyState(State.Pressed);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!_button.interactable) return;
        ApplyState(State.Hover);
    }

    #endregion

    #region State Handling

    /// <summary>
    /// Internal button state enum.
    /// </summary>
    private enum State { Normal, Hover, Pressed, Disabled }

    /// <summary>
    /// Applies colors to background, extra image, and label text
    /// according to the specified button state.
    /// </summary>
    private void ApplyState(State s)
    {
        // 1) Tint background
        if (backgroundImage != null)
        {
            switch (s)
            {
                case State.Normal:   backgroundImage.color = normalBg;   break;
                case State.Hover:    backgroundImage.color = hoverBg;    break;
                case State.Pressed:  backgroundImage.color = pressedBg;  break;
                case State.Disabled: backgroundImage.color = disabledBg; break;
            }
        }

        // 2) Tint extra image
        if (extraImage != null)
        {
            switch (s)
            {
                case State.Normal:   extraImage.color = normalExtra;   break;
                case State.Hover:    extraImage.color = hoverExtra;    break;
                case State.Pressed:  extraImage.color = pressedExtra;  break;
                case State.Disabled: extraImage.color = disabledExtra; break;
            }
        }

        // 3) Tint text
        if (labelText != null)
        {
            switch (s)
            {
                case State.Normal:   labelText.color = normalText;   break;
                case State.Hover:    labelText.color = hoverText;    break;
                case State.Pressed:  labelText.color = pressedText;  break;
                case State.Disabled: labelText.color = disabledText; break;
            }
        }
    }

    #endregion
}