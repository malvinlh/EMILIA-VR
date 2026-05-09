using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Attach to the Slot trigger GameObject inside PaperShredder_Root.
/// When armed by JournalReviewController, the slot passively waits for paper to be
/// placed inside it. Shredding is only triggered when an external lever (or other
/// committal gesture) calls <see cref="Pull"/>. This makes the release act
/// deliberate rather than auto-firing on paper insertion.
/// </summary>
public class PaperShredder : MonoBehaviour
{
    [Header("References")]
    public JournalReviewController reviewController;
    [Tooltip("ShredderLever on the lever handle. Enabled only while armed; disabled after shredding.")]
    [SerializeField] private ShredderLever _lever;

    [Header("Detection")]
    [Tooltip("Tag used on the paper object (and searched up hierarchy).")]
    public string paperTag = "JournalBottle";

    [Header("VFX Anchors")]
    [Tooltip("Disabled anchor whose position+rotation the paper lerps to before sliding down.")]
    public Transform paperPlaceholder;
    [Tooltip("Where shredded strips spawn. Falls back to this transform if null.")]
    public Transform stripsSpawnOrigin;
    [Tooltip("Visual-only shredder transform to shake during the grind SFX. Falls back to the parent transform if null.")]
    public Transform shakeTarget;

    [Header("Audio")]
    public AudioSource grindSfx;

    [Header("Pull Tuning")]
    [Range(0.05f, 0.5f)] public float snapDuration = 0.2f;
    [Range(0.3f, 2f)] public float pullDownDuration = 0.9f;
    [Range(0.05f, 1.0f)] public float pullDownDistance = 0.25f;

    [Header("Strip VFX")]
    [Range(4, 32)] public int stripCount = 12;
    public Vector3 stripSize = new Vector3(0.02f, 0.001f, 0.08f);
    public Color stripColor = Color.white;
    [Range(1f, 8f)] public float stripLifetime = 4f;

    [Header("Shake Feedback")]
    [Range(0f, 0.03f)] public float shakePositionAmplitude = 0.008f;
    [Range(0f, 3f)] public float shakeRotationAmplitude = 1.0f;
    [Range(6f, 40f)] public float shakeFrequency = 18f;

    private bool _armed;
    private bool _fired;
    private bool _paperSnapped;
    private Transform _paperInSlot;
    private Coroutine _snapCoroutine;

    private void Awake()
    {
        if (paperPlaceholder != null)
            paperPlaceholder.gameObject.SetActive(false);
        if (_lever?.grabInteractable != null) _lever.grabInteractable.enabled = false;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public void Arm()
    {
        _fired = false;
        _paperSnapped = false;
        _paperInSlot = null;
        if (_snapCoroutine != null) { StopCoroutine(_snapCoroutine); _snapCoroutine = null; }
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = true;
        _armed = true;
        // Lever stays disabled until paper has snapped to PaperPlaceholder (see SnapToSlot).
    }

    public void Disarm()
    {
        _armed = false;
        _paperInSlot = null;
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;
        if (_lever?.grabInteractable != null) _lever.grabInteractable.enabled = false;
    }

    /// <summary>
    /// Called by the lever (or any committal gesture) when the user decides
    /// to actually destroy the paper currently resting in the slot.
    /// No-op unless armed, paper is in the slot, and not already shredding.
    /// </summary>
    public void Pull()
    {
        if (!_armed || _fired || _paperInSlot == null) return;
        if (reviewController == null || !reviewController.IsWaitingForShredder) return;

        StartCoroutine(ShredRoutine(_paperInSlot));
    }

    // ── Collision detection ────────────────────────────────────────────────
    // Paper entering / leaving the slot only updates the cached reference.
    // Shredding is gated behind Pull() (invoked by the lever).

    private void OnTriggerEnter(Collider other) => CachePaper(other);
    private void OnTriggerStay(Collider other)  => CachePaper(other);

    private void OnTriggerExit(Collider other)
    {
        if (_paperInSlot == null || _snapCoroutine != null || _fired || _paperSnapped) return;

        Transform t = other.transform;
        while (t != null)
        {
            if (t == _paperInSlot) { _paperInSlot = null; return; }
            t = t.parent;
        }
    }

    private void CachePaper(Collider other)
    {
        if (!_armed || _fired || _paperInSlot != null) return;
        if (reviewController == null || !reviewController.IsWaitingForShredder) return;

        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(paperTag))
            {
                _paperInSlot = t;
                _snapCoroutine = StartCoroutine(SnapToSlot(t));
                return;
            }
            t = t.parent;
        }
    }

    private IEnumerator SnapToSlot(Transform paper)
    {
        var grab = paper.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = false;

        var rb = paper.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        if (paperPlaceholder != null)
        {
            Vector3 fromPos = paper.position;
            Quaternion fromRot = paper.rotation;
            float elapsed = 0f;
            while (elapsed < snapDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / snapDuration);
                paper.SetPositionAndRotation(
                    Vector3.Lerp(fromPos, paperPlaceholder.position, t),
                    Quaternion.Slerp(fromRot, paperPlaceholder.rotation, t));
                yield return null;
            }
            paper.SetPositionAndRotation(paperPlaceholder.position, paperPlaceholder.rotation);
        }

        _paperSnapped = true;
        _snapCoroutine = null;
        if (_lever?.grabInteractable != null) _lever.grabInteractable.enabled = true;
    }

    // ── Shred coroutine ────────────────────────────────────────────────────

    private IEnumerator ShredRoutine(Transform paper)
    {
        _armed = false;
        _fired = true;
        _paperInSlot = null;

        // Stop auto-snap if lever was pulled mid-snap.
        if (_snapCoroutine != null) { StopCoroutine(_snapCoroutine); _snapCoroutine = null; }

        // Ensure grab disabled and kinematic (SnapToSlot may have already done this).
        var grab = paper.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = false;

        var rb = paper.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        float feedbackDuration = ResolveGrindDuration();
        if (grindSfx != null)
        {
            grindSfx.Stop();
            grindSfx.Play();
        }

        StartCoroutine(ShakeRoutine(feedbackDuration));

        // Paper is already at PaperPlaceholder (snapped by SnapToSlot).
        // Slide only Y downward
        Vector3 slideOrigin = paper.position;
        float slideElapsed = 0f;
        float pullDuration = feedbackDuration > 0f ? feedbackDuration : pullDownDuration;
        while (slideElapsed < pullDuration)
        {
            slideElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(slideElapsed / pullDuration);
            paper.position = slideOrigin + Vector3.down * (pullDownDistance * t);
            yield return null;
        }

        SpawnStrips();
        paper.gameObject.SetActive(false);

        reviewController?.HandlePaperShredded();
    }

    // ── Strip VFX ─────────────────────────────────────────────────────────

    private void SpawnStrips()
    {
        Vector3 origin = stripsSpawnOrigin != null ? stripsSpawnOrigin.position : transform.position;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"))
            { color = stripColor };

        for (int i = 0; i < stripCount; i++)
        {
            var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strip.transform.position   = origin + Random.insideUnitSphere * 0.05f;
            strip.transform.rotation   = Random.rotation;
            strip.transform.localScale = stripSize;
            strip.GetComponent<Renderer>().material = mat;

            if (strip.TryGetComponent<Rigidbody>(out var srb))
            {
                srb.linearVelocity = Random.insideUnitSphere * 1.2f + Vector3.down * 0.5f;
            }
            else
            {
                srb = strip.AddComponent<Rigidbody>();
                srb.linearVelocity = Random.insideUnitSphere * 1.2f + Vector3.down * 0.5f;
            }

            Destroy(strip, stripLifetime);
        }
    }

    private float ResolveGrindDuration()
    {
        if (grindSfx == null || grindSfx.clip == null)
            return pullDownDuration;

        float pitch = Mathf.Abs(grindSfx.pitch);
        if (pitch < 0.01f) pitch = 0.01f;
        return grindSfx.clip.length / pitch;
    }

    private IEnumerator ShakeRoutine(float duration)
    {
        Transform target = shakeTarget != null ? shakeTarget : transform.parent;
        if (target == null || duration <= 0f)
            yield break;

        Vector3 startLocalPos = target.localPosition;
        Quaternion startLocalRot = target.localRotation;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float noiseX = Mathf.PerlinNoise(elapsed * shakeFrequency, 0f) * 2f - 1f;
            float noiseY = Mathf.PerlinNoise(0f, elapsed * shakeFrequency) * 2f - 1f;
            float noiseZ = Mathf.PerlinNoise(elapsed * shakeFrequency, elapsed * shakeFrequency) * 2f - 1f;

            target.localPosition = startLocalPos + new Vector3(noiseX, noiseY * 0.5f, noiseZ) * shakePositionAmplitude;
            target.localRotation = startLocalRot * Quaternion.Euler(noiseY * shakeRotationAmplitude, noiseX * shakeRotationAmplitude, noiseZ * shakeRotationAmplitude);
            yield return null;
        }

        target.localPosition = startLocalPos;
        target.localRotation = startLocalRot;
    }
}
