using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Attach to the Slot trigger GameObject inside PaperShredder_Root.
/// When armed by JournalReviewController, pulls the grabbed paper into the slot,
/// spawns shredded strip particles, then notifies the controller.
/// </summary>
public class PaperShredder : MonoBehaviour
{
    [Header("References")]
    public JournalReviewController reviewController;

    [Header("Detection")]
    [Tooltip("Tag used on the paper object (and searched up hierarchy).")]
    public string paperTag = "JournalBottle";

    [Header("VFX Anchors")]
    [Tooltip("Where the paper is pulled toward. Falls back to local down if null.")]
    public Transform slotTop;
    [Tooltip("Where shredded strips spawn. Falls back to this transform if null.")]
    public Transform stripsSpawnOrigin;
    [Tooltip("Visual-only shredder transform to shake during the grind SFX. Falls back to the parent transform if null.")]
    public Transform shakeTarget;

    [Header("Audio")]
    public AudioSource grindSfx;

    [Header("Pull Tuning")]
    [Range(0.3f, 2f)] public float pullDownDuration = 0.9f;
    [Range(0.05f, 0.5f)] public float pullDownDistance = 0.25f;

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

    // ── Public API ─────────────────────────────────────────────────────────

    public void Arm()
    {
        _fired = false;
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = true;
        _armed = true;
    }

    public void Disarm()
    {
        _armed = false;
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;
    }

    // ── Collision detection ────────────────────────────────────────────────

    private void OnTriggerEnter(Collider other) => TryShred(other);
    private void OnTriggerStay(Collider other)  => TryShred(other);

    private void TryShred(Collider other)
    {
        if (!_armed || _fired) return;
        if (reviewController == null || !reviewController.IsWaitingForShredder) return;

        // Walk up the hierarchy looking for the paper tag.
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(paperTag)) { StartCoroutine(ShredRoutine(t)); return; }
            t = t.parent;
        }
    }

    // ── Shred coroutine ────────────────────────────────────────────────────

    private IEnumerator ShredRoutine(Transform paper)
    {
        _armed = false;
        _fired = true;

        // Disable grab so the player can't re-grab mid-animation.
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

        Vector3 startPos   = paper.position;
        Quaternion startRot = paper.rotation;
        Vector3 startScale  = paper.localScale;

        Vector3 targetPos = slotTop != null
            ? slotTop.position
            : startPos + Vector3.down * pullDownDistance;

        float elapsed = 0f;
        float pullDuration = feedbackDuration > 0f ? feedbackDuration : pullDownDuration;
        while (elapsed < pullDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / pullDuration);
            paper.position   = Vector3.Lerp(startPos, targetPos, t);
            paper.rotation   = Quaternion.Slerp(startRot, transform.rotation, t);
            paper.localScale = Vector3.Lerp(startScale, new Vector3(startScale.x, 0f, startScale.z), t);
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
