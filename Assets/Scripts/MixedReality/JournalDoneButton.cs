using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach to the Bottle game object in the journaling scene.
///
/// Flow:
///   1. While in JournalSessionManager.Journaling state, the script polls the
///      finger-tip ray from JournalInlineCursor every frame.
///   2. When that ray intersects the Bottle's Collider, the Done panel becomes
///      visible (a floating world-space Canvas child of this object).
///   3. The user physically pokes the Done button (index finger tip enters the
///      12 mm fire zone) to invoke EndSession() — same poke mechanism used by
///      the whiteboard footer buttons.
///
/// Inspector setup:
///   donePanel  → child Canvas (World Space, scale ~0.001) containing the Done button.
///   doneButton → the Button component inside donePanel.
///
/// </summary>
public class JournalDoneButton : MonoBehaviour
{
    [Header("Done Panel")]
    [Tooltip("Root Canvas that shows the Done button. Starts hidden; revealed when the ray hits the bottle.")]
    [SerializeField] private GameObject donePanel;

    [Tooltip("The 'Done' Button inside donePanel.")]
    [SerializeField] private Button doneButton;

    [Header("Poke Detection")]
    [Tooltip("Outer proximity zone (metres). Fingertip must be this close in the button plane before fire-zone is checked.")]
    [SerializeField] [Range(0.01f, 0.10f)] private float pokeHoverDist = 0.04f;

    [Tooltip("Inner fire zone (metres). Entering this distance triggers the button press.")]
    [SerializeField] [Range(0.005f, 0.03f)] private float pokeFirDist = 0.012f;

    // ── Singleton ─────────────────────────────────────────────────────────
    public static JournalDoneButton Instance { get; private set; }

    // ── Runtime ───────────────────────────────────────────────────────────
    private bool     _btnInZone;
    private bool     _btnWasClose;

    private const string TAG = "[JournalDoneButton]";

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (donePanel != null)
            donePanel.SetActive(false);

        if (doneButton != null)
            doneButton.onClick.AddListener(OnDoneClicked);
    }

    private void OnEnable()
    {
        // When the parent group re-activates for a new session, immediately sync
        // the panel visibility to the current session state rather than waiting
        // for the next Update() tick. Without this, DonePanel stays hidden if it
        // was explicitly SetActive(false) at the end of the previous session.
        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;
        ShowDonePanel(journaling);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        if (doneButton != null)
            doneButton.onClick.RemoveListener(OnDoneClicked);
    }

    // ==================================================================
    // UPDATE
    // ==================================================================

    private void Update()
    {
        // Only active while the user is actively journaling.
        var session = JournalSessionManager.Instance;
        if (session == null || session.CurrentState != JournalSessionManager.SessionState.Journaling)
        {
            ShowDonePanel(false);
            _btnInZone = _btnWasClose = false;
            return;
        }

        // Done panel is always visible while journaling.
        ShowDonePanel(true);

        var cursor = JournalInlineCursor.Instance;
        if (cursor == null || !cursor.IsHandTracked)
        {
            _btnInZone = _btnWasClose = false;
            return;
        }

        // Poke detection.
        if (doneButton != null)
            CheckDoneButtonPoke(cursor.TipWorldPosition);
        else
            _btnInZone = _btnWasClose = false;
    }

    // ==================================================================
    // POKE DETECTION (mirrors JournalInlineCursor.CheckButtonPoke)
    // ==================================================================

    private void CheckDoneButtonPoke(Vector3 fingertipWorld)
    {
        var   rt    = (RectTransform)doneButton.transform;
        var   plane = new Plane(-rt.forward, rt.position);
        float sdist = plane.GetDistanceToPoint(fingertipWorld);
        float adist = Mathf.Abs(sdist);

        Vector3 onPlane  = plane.ClosestPointOnPlane(fingertipWorld);
        Vector3 local    = rt.InverseTransformPoint(onPlane);
        Rect    r        = rt.rect;
        bool    inBounds = local.x >= r.xMin && local.x <= r.xMax
                        && local.y >= r.yMin && local.y <= r.yMax;

        bool inZone  = inBounds && adist <= pokeHoverDist;
        bool isClose = inBounds && adist <= pokeFirDist;

        if (!inZone)
        {
            _btnInZone = _btnWasClose = false;
            return;
        }

        _btnInZone = true;

        if (isClose && !_btnWasClose)
        {
            Debug.Log($"{TAG} Poke detected — sdist={sdist * 1000f:F1} mm interactable={doneButton.interactable}");
            if (doneButton.interactable)
                doneButton.onClick.Invoke();
        }

        _btnWasClose = isClose;
    }

    // ==================================================================
    // BUTTON HANDLER
    // ==================================================================

    private void OnDoneClicked()
    {
        Debug.Log($"{TAG} Done clicked — ending journaling session.");
        ShowDonePanel(false);
        _btnInZone = _btnWasClose = false;
        JournalSessionManager.Instance?.EndSession();
    }

    // ==================================================================
    // HELPER
    // ==================================================================

    private void ShowDonePanel(bool show)
    {
        if (donePanel != null && donePanel.activeSelf != show)
            donePanel.SetActive(show);
    }
}
