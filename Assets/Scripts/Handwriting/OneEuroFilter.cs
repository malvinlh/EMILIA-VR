using UnityEngine;

/// <summary>
/// 1€ (One Euro) Filter – a simple, high-quality signal filter designed
/// for interactive applications.  It minimises jitter at low speed while
/// keeping latency low during fast movements.
///
/// Reference: Casiez, Roussel &amp; Vogel, CHI 2012.
/// https://cristal.univ-lille.fr/~casiez/1euro/
/// </summary>
public class OneEuroFilter
{
    private float minCutoff;
    private float beta;
    private float dCutoff;

    private LowPassFilter xFilter;
    private LowPassFilter dxFilter;

    private float lastTime;
    private bool initialised;

    /// <param name="minCutoff">Minimum cutoff frequency (Hz). Lower = more smoothing at low speed.</param>
    /// <param name="beta">Speed coefficient.  Higher = less latency during fast movements.</param>
    /// <param name="dCutoff">Cutoff frequency for the derivative filter (Hz).</param>
    public OneEuroFilter(float minCutoff = 1.5f, float beta = 0.01f, float dCutoff = 1.0f)
    {
        this.minCutoff = minCutoff;
        this.beta      = beta;
        this.dCutoff   = dCutoff;
        xFilter  = new LowPassFilter();
        dxFilter = new LowPassFilter();
    }

    /// <summary>
    /// Feed a new raw sample and return the filtered value.
    /// </summary>
    public float Filter(float value, float timestamp)
    {
        if (!initialised)
        {
            initialised = true;
            lastTime = timestamp;
            dxFilter.SetAlpha(1f);
            dxFilter.Apply(0f);
            xFilter.SetAlpha(1f);
            xFilter.Apply(value);
            return value;
        }

        float dt = timestamp - lastTime;
        if (dt <= 0f) dt = 1f / 90f; // fallback ≈ 90 Hz
        lastTime = timestamp;

        // Estimate derivative
        float dx  = (value - xFilter.Last()) / dt;
        float edx = dxFilter.ApplyWithAlpha(dx, Alpha(dt, dCutoff));

        // Adaptive cutoff based on speed
        float cutoff = minCutoff + beta * Mathf.Abs(edx);
        return xFilter.ApplyWithAlpha(value, Alpha(dt, cutoff));
    }

    /// <summary>
    /// Reset internal state so the next call to Filter starts fresh.
    /// </summary>
    public void Reset()
    {
        initialised = false;
        xFilter  = new LowPassFilter();
        dxFilter = new LowPassFilter();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static float Alpha(float dt, float cutoff)
    {
        float tau = 1f / (2f * Mathf.PI * cutoff);
        return 1f / (1f + tau / dt);
    }

    /// <summary>
    /// Simple first-order low-pass filter.
    /// </summary>
    private class LowPassFilter
    {
        private float y;
        private float a;
        private bool  init;

        public void SetAlpha(float alpha) { a = alpha; }

        public float Apply(float value)
        {
            if (!init) { y = value; init = true; return y; }
            y = a * value + (1f - a) * y;
            return y;
        }

        public float ApplyWithAlpha(float value, float alpha)
        {
            SetAlpha(alpha);
            return Apply(value);
        }

        public float Last() => y;
    }
}
