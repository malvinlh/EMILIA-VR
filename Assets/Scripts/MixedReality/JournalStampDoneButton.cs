using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Bedroom analog of JournalDoneButton. Attach to the WaxStamper GameObject.
///
/// During Journaling the player grabs the stamper and presses its tip against the
/// Whiteboard (paperSurface). Progress rises while the tip is within stampDistance.
/// At 100% a wax seal is spawned, the stamper resets, and EndSession() fires.
/// </summary>
public class JournalStampDoneButton : MonoBehaviour
{
    [Header("Grab")]
    [SerializeField] private bool configureGrabOnAwake = true;
    [SerializeField] private float resetRadius = 3f;
    [SerializeField] private float outsideDelay = 1.5f;

    [Header("Stamp Detection")]
    [Tooltip("Tip child transform. Falls back to stamperTipLocalOffset if null.")]
    public Transform stamperTip;
    [SerializeField] private Vector3 stamperTipLocalOffset = new Vector3(0f, -0.015f, 0f);
    [Tooltip("The Whiteboard surface transform to stamp against.")]
    public Transform paperSurface;
    [SerializeField, Range(0.005f, 0.3f)] private float stampDistance = 0.05f;
    [SerializeField, Range(0.2f, 8f)]     private float stampDuration  = 1.0f;
    [SerializeField, Range(0f, 2f)]       private float rollbackPerSecond = 0.5f;

    [Header("Wax Seal")]
    [SerializeField] private Color sealColor = new Color(0.75f, 0.1f, 0.1f, 1f);
    [SerializeField, Range(0.005f, 0.15f)] private float sealDiameter = 0.035f;
    [SerializeField, Range(0.05f, 1f)]     private float sealPunchDuration = 0.25f;
    [SerializeField, Range(0.0005f, 0.01f)] private float sealSurfaceOffset = 0.0015f;
    public AudioSource stampSfx;

    [Header("Completion")]
    [SerializeField, Range(0f, 1f)] private float completionDelay = 0.05f;

    public static JournalStampDoneButton Instance { get; private set; }

    private XRGrabInteractable _grab;
    private Rigidbody _rb;
    private ItemAutoReset _autoReset;
    private float _progress;
    private bool _completionTriggered;
    private bool _initialized;
    private Transform _originParent;
    private Vector3 _originLocalPos;
    private Quaternion _originLocalRot;
    private GameObject _spawnedSeal;
    private static Material s_sealMat;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _originParent   = transform.parent;
        _originLocalPos = transform.localPosition;
        _originLocalRot = transform.localRotation;

        if (configureGrabOnAwake) EnsureGrabSetup();
        _initialized = true;
    }

    private void OnEnable()
    {
        _completionTriggered = false;
        _progress = 0f;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Update ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (!_initialized) return;

        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;

        if (!journaling)
        {
            if (_grab != null && _grab.enabled) _grab.enabled = false;
            _progress = 0f;
            _completionTriggered = false;
            return;
        }

        if (_grab != null && !_grab.enabled) _grab.enabled = true;

        bool grabbed  = _grab != null && _grab.isSelected;
        bool nearPaper = grabbed && IsNearPaper();

        float riseRate = 1f / Mathf.Max(0.01f, stampDuration);
        float fallRate = Mathf.Max(0f, rollbackPerSecond);
        float target   = nearPaper ? 1f : 0f;
        float speed    = nearPaper ? riseRate : fallRate;

        _progress = Mathf.MoveTowards(_progress, target, speed * Time.deltaTime);

        if (!_completionTriggered && _progress >= 1f)
            StartCoroutine(CompleteAfterStamp());
    }

    // ── Stamp detection ────────────────────────────────────────────────────

    private bool IsNearPaper()
    {
        if (paperSurface == null) return false;
        Vector3 tip = TipWorld();
        var col = paperSurface.GetComponent<Collider>()
               ?? paperSurface.GetComponentInChildren<Collider>(true);
        return col != null
            ? Vector3.Distance(tip, col.ClosestPoint(tip)) <= stampDistance
            : Vector3.Distance(tip, paperSurface.position) <= stampDistance;
    }

    private Vector3 TipWorld() =>
        stamperTip != null ? stamperTip.position : transform.TransformPoint(stamperTipLocalOffset);

    // ── Completion ─────────────────────────────────────────────────────────

    private IEnumerator CompleteAfterStamp()
    {
        if (_completionTriggered) yield break;
        _completionTriggered = true;

        ForceRelease();
        yield return null;

        SpawnSeal();
        stampSfx?.Play();

        if (completionDelay > 0f) yield return new WaitForSeconds(completionDelay);

        if (_autoReset != null) _autoReset.ResetNow();
        else ResetToOrigin();

        JournalSessionManager.Instance?.EndSession();
    }

    // ── Wax seal ───────────────────────────────────────────────────────────

    private void SpawnSeal()
    {
        if (paperSurface == null) return;
        if (_spawnedSeal != null) Destroy(_spawnedSeal);

        var seal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        seal.name = "WaxSeal";
        Destroy(seal.GetComponent<Collider>());

        Vector3 tip     = TipWorld();
        Vector3 paperUp = paperSurface.up;

        var col = paperSurface.GetComponent<Collider>()
               ?? paperSurface.GetComponentInChildren<Collider>(true);
        Vector3 sealPos = col != null
            ? new Vector3(tip.x, col.bounds.max.y, tip.z) + paperUp * sealSurfaceOffset
            : tip - paperUp * new Plane(paperUp, paperSurface.position).GetDistanceToPoint(tip)
              + paperUp * sealSurfaceOffset;

        Quaternion sealRot = Quaternion.LookRotation(paperSurface.forward, paperUp)
                           * Quaternion.Euler(90f, 0f, 0f);

        seal.transform.SetPositionAndRotation(sealPos, sealRot);
        seal.transform.SetParent(paperSurface, worldPositionStays: true);

        if (s_sealMat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            s_sealMat = new Material(shader) { color = sealColor };
        }
        seal.GetComponent<Renderer>().sharedMaterial = s_sealMat;
        _spawnedSeal = seal;
        StartCoroutine(PunchScale(seal.transform));
    }

    private IEnumerator PunchScale(Transform t)
    {
        if (t == null) yield break;
        Vector3 target = Vector3.one * sealDiameter;
        float half = Mathf.Max(0.02f, sealPunchDuration * 0.5f);

        for (float e = 0f; e < half; e += Time.deltaTime)
        {
            if (t == null) yield break;
            t.localScale = Vector3.Lerp(Vector3.zero, target * 1.2f, e / half);
            yield return null;
        }
        for (float e = 0f; e < half; e += Time.deltaTime)
        {
            if (t == null) yield break;
            t.localScale = Vector3.Lerp(target * 1.2f, target, e / half);
            yield return null;
        }
        if (t != null) t.localScale = target;
    }

    // ── Grab helpers ───────────────────────────────────────────────────────

    private void EnsureGrabSetup()
    {
        _rb = GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();
        _rb.useGravity  = true;
        _rb.isKinematic = false;

        _grab = GetComponent<XRGrabInteractable>() ?? gameObject.AddComponent<XRGrabInteractable>();
        _grab.selectMode = InteractableSelectMode.Single;

        var col = GetComponent<Collider>() ?? GetComponentInChildren<Collider>(true);
        if (col != null) { _grab.colliders.Clear(); _grab.colliders.Add(col); }

        _autoReset = GetComponent<ItemAutoReset>() ?? gameObject.AddComponent<ItemAutoReset>();
        _autoReset.resetRadius  = resetRadius;
        _autoReset.outsideDelay = outsideDelay;
    }

    private void ForceRelease()
    {
        if (_grab == null || !_grab.isSelected) return;
        var mgr = _grab.interactionManager;
        if (mgr == null) return;
        for (int i = _grab.interactorsSelecting.Count - 1; i >= 0; i--)
        {
            IXRSelectInteractor interactor = _grab.interactorsSelecting[i];
            if (interactor != null) mgr.SelectExit(interactor, _grab);
        }
    }

    private void ResetToOrigin()
    {
        transform.SetParent(_originParent, worldPositionStays: false);
        transform.localPosition = _originLocalPos;
        transform.localRotation = _originLocalRot;
        if (_rb != null) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
    }
}
