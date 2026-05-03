using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives AZKi facial blend-shapes based on the current base-layer animation state.
/// Supports Japanese blend-shape names and graceful fallback when a shape is missing.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class AzkiBlendShapeExpressionController : MonoBehaviour
{
    [Header("Renderer")]
    [Tooltip("Optional explicit face renderer. If null, auto-finds a SkinnedMeshRenderer under this avatar.")]
    [SerializeField] private SkinnedMeshRenderer faceRenderer;

    [Tooltip("Used when auto-finding the face renderer by object name.")]
    [SerializeField] private string faceMeshNameHint = "AZKi_mesh";

    [Header("State Names")]
    [SerializeField] private string idleStateName = "Base Layer.Idle";
    [SerializeField] private string idleBStateName = "Base Layer.IdleB";
    [SerializeField] private string walkStateName = "Base Layer.Walk";
    [SerializeField] private string talkStateName = "Base Layer.Talk";
    [SerializeField] private string cheeringStateName = "Base Layer.Cheering";

    [Header("Blending")]
    [Tooltip("Maximum blend-shape weight change per second.")]
    [Range(10f, 600f)]
    [SerializeField] private float weightLerpPerSecond = 120f;

    [Header("Blink")]
    [SerializeField] private string blinkShapeName = "まばたき";
    [Range(0f, 100f)]
    [SerializeField] private float blinkClosedWeight = 100f;
    [SerializeField] private Vector2 blinkIntervalRange = new Vector2(2.5f, 5.0f);
    [Range(0.04f, 0.3f)]
    [SerializeField] private float blinkDuration = 0.3f;

    [Header("Talking Mouth")]
    [Tooltip("Mouth shapes cycled while in Talk state.")]
    [SerializeField] private string[] talkMouthShapeNames = new[] { "あ", "い", "う", "え", "お", "ワ", "ワ2", "ちゅ", "ω", "▲" };
    [SerializeField] private Vector2 talkMouthIntervalRange = new Vector2(0.09f, 0.18f);
    [SerializeField] private Vector2 talkMouthWeightRange = new Vector2(50f, 100f);

    private enum FaceState
    {
        Idle,
        IdleB,
        Walk,
        Talk,
        Cheering
    }

    private Animator _animator;

    private int _idleHash;
    private int _idleBHash;
    private int _walkHash;
    private int _talkHash;
    private int _cheeringHash;

    private int _idleShortHash;
    private int _idleBShortHash;
    private int _walkShortHash;
    private int _talkShortHash;
    private int _cheeringShortHash;

    private readonly Dictionary<string, int> _indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _indexByNormalizedName = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly HashSet<int> _controlledIndices = new HashSet<int>();
    private readonly Dictionary<int, float> _targetWeights = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _currentWeights = new Dictionary<int, float>();

    private float _nextBlinkTime;
    private bool _blinkActive;
    private float _blinkStartTime;
    private float _blinkWeight;

    private float _nextTalkMouthChangeTime;
    private string _activeTalkMouthShape;
    private float _activeTalkMouthWeight;

    private void Awake()
    {
        _animator = GetComponent<Animator>();

        CacheStateHashes();
        ResolveFaceRenderer();
        RebuildBlendShapeIndex();

        ScheduleNextBlink();
        _nextTalkMouthChangeTime = Time.time;
    }

    private void LateUpdate()
    {
        if (_animator == null || faceRenderer == null || faceRenderer.sharedMesh == null)
            return;

        UpdateBlink();

        FaceState state = ResolveFaceState();
        if (state == FaceState.Talk)
            UpdateTalkingMouth();
        else
            _activeTalkMouthShape = null;

        BuildTargetWeights(state);
        ApplyWeights();

        if (faceRenderer != null && TryGetBlendShapeIndex(blinkShapeName, out int blinkIdx))
        {
            faceRenderer.SetBlendShapeWeight(blinkIdx, _blinkWeight);
            _currentWeights[blinkIdx] = _blinkWeight;
        }
    }

    private void CacheStateHashes()
    {
        _idleHash = Animator.StringToHash(idleStateName);
        _idleBHash = Animator.StringToHash(idleBStateName);
        _walkHash = Animator.StringToHash(walkStateName);
        _talkHash = Animator.StringToHash(talkStateName);
        _cheeringHash = Animator.StringToHash(cheeringStateName);

        _idleShortHash = HashShortStateName(idleStateName);
        _idleBShortHash = HashShortStateName(idleBStateName);
        _walkShortHash = HashShortStateName(walkStateName);
        _talkShortHash = HashShortStateName(talkStateName);
        _cheeringShortHash = HashShortStateName(cheeringStateName);
    }

    private void ResolveFaceRenderer()
    {
        if (faceRenderer != null)
            return;

        var renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (renderers.Length == 0)
            return;

        if (!string.IsNullOrWhiteSpace(faceMeshNameHint))
        {
            foreach (var r in renderers)
            {
                if (r != null && r.name.IndexOf(faceMeshNameHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    faceRenderer = r;
                    return;
                }
            }
        }

        faceRenderer = renderers[0];
    }

    private void RebuildBlendShapeIndex()
    {
        _indexByName.Clear();
        _indexByNormalizedName.Clear();
        _controlledIndices.Clear();
        _targetWeights.Clear();
        _currentWeights.Clear();

        if (faceRenderer == null || faceRenderer.sharedMesh == null)
            return;

        var mesh = faceRenderer.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            if (string.IsNullOrEmpty(name))
                continue;

            _indexByName[name] = i;
            string normalized = NormalizeName(name);
            if (!_indexByNormalizedName.ContainsKey(normalized))
                _indexByNormalizedName.Add(normalized, i);
        }
    }

    private FaceState ResolveFaceState()
    {
        AnimatorStateInfo stateInfo = _animator.IsInTransition(0)
            ? _animator.GetNextAnimatorStateInfo(0)
            : _animator.GetCurrentAnimatorStateInfo(0);

        if (StateMatches(stateInfo, _talkHash, _talkShortHash))
            return FaceState.Talk;
        if (StateMatches(stateInfo, _cheeringHash, _cheeringShortHash))
            return FaceState.Cheering;
        if (StateMatches(stateInfo, _walkHash, _walkShortHash))
            return FaceState.Walk;
        if (StateMatches(stateInfo, _idleBHash, _idleBShortHash))
            return FaceState.IdleB;

        return FaceState.Idle;
    }

    private void BuildTargetWeights(FaceState state)
    {
        _targetWeights.Clear();

        switch (state)
        {
            case FaceState.Idle:
                SetTarget("なごみ", 20f);
                SetTarget("笑い", 18f);
                SetTarget("口角広", 14f);
                SetTarget("眉上", 10f);
                SetTarget("頬染", 18f);
                SetTarget("キラキラ1", 22f);
                break;

            case FaceState.IdleB:
                SetTarget("困る", 18f);
                SetTarget("口角下", 8f);
                SetTarget("へ", 8f);
                SetTarget("眉下", 12f);
                SetTarget("はう", 15f);
                SetTarget("頬染", 12f);
                break;

            case FaceState.Walk:
                SetTarget("真面目", 10f);
                SetTarget("口角縮", 5f);
                SetTarget("眉前", 4f);
                SetTarget("なごみ", 12f);
                SetTarget("頬染", 8f);
                break;

            case FaceState.Talk:
                SetTarget("なごみ", 22f);
                SetTarget("笑い", 20f);
                SetTarget("口角広", 16f);
                SetTarget("頬染", 22f);
                SetTarget("キラキラ1", 18f);
                if (!string.IsNullOrEmpty(_activeTalkMouthShape))
                    SetTarget(_activeTalkMouthShape, _activeTalkMouthWeight);
                break;

            case FaceState.Cheering:
                SetTarget("笑い", 60f);
                SetTarget("口角広", 28f);
                SetTarget("眉上", 32f);
                SetTarget("びっくり", 8f);
                SetTarget("頬染", 35f);
                SetTarget("キラキラ2", 45f);
                SetTarget("ハート1", 25f);
                break;
        }

    }

    private void ApplyWeights()
    {
        if (faceRenderer == null)
            return;

        foreach (int index in _controlledIndices)
        {
            float target = _targetWeights.TryGetValue(index, out var w) ? w : 0f;
            float current = _currentWeights.TryGetValue(index, out var c) ? c : faceRenderer.GetBlendShapeWeight(index);

            float next = Mathf.MoveTowards(current, target, weightLerpPerSecond * Time.deltaTime);
            faceRenderer.SetBlendShapeWeight(index, next);
            _currentWeights[index] = next;
        }
    }

    private void UpdateBlink()
    {
        if (!_blinkActive)
        {
            if (Time.time >= _nextBlinkTime)
            {
                _blinkActive = true;
                _blinkStartTime = Time.time;
            }
            _blinkWeight = 0f;
            return;
        }

        float elapsed = Time.time - _blinkStartTime;
        float t = blinkDuration > 0f ? elapsed / blinkDuration : 1f;

        if (t >= 1f)
        {
            _blinkActive = false;
            _blinkWeight = 0f;
            ScheduleNextBlink();
            return;
        }

        float phase = t < 0.5f
            ? SmoothStep01(t / 0.5f)
            : SmoothStep01(1f - ((t - 0.5f) / 0.5f));

        _blinkWeight = phase * blinkClosedWeight;
    }

    private void ScheduleNextBlink()
    {
        float min = Mathf.Max(0.05f, blinkIntervalRange.x);
        float max = Mathf.Max(min, blinkIntervalRange.y);
        _nextBlinkTime = Time.time + UnityEngine.Random.Range(min, max);
    }

    private void UpdateTalkingMouth()
    {
        if (talkMouthShapeNames == null || talkMouthShapeNames.Length == 0)
            return;

        if (Time.time < _nextTalkMouthChangeTime && !string.IsNullOrEmpty(_activeTalkMouthShape))
            return;

        int start = UnityEngine.Random.Range(0, talkMouthShapeNames.Length);
        for (int i = 0; i < talkMouthShapeNames.Length; i++)
        {
            string candidate = talkMouthShapeNames[(start + i) % talkMouthShapeNames.Length];
            if (TryGetBlendShapeIndex(candidate, out _))
            {
                _activeTalkMouthShape = candidate;
                float min = Mathf.Clamp(talkMouthWeightRange.x, 0f, 100f);
                float max = Mathf.Clamp(talkMouthWeightRange.y, min, 100f);
                _activeTalkMouthWeight = UnityEngine.Random.Range(min, max);
                break;
            }
        }

        float intervalMin = Mathf.Max(0.02f, talkMouthIntervalRange.x);
        float intervalMax = Mathf.Max(intervalMin, talkMouthIntervalRange.y);
        _nextTalkMouthChangeTime = Time.time + UnityEngine.Random.Range(intervalMin, intervalMax);
    }

    private void SetTarget(string blendShapeName, float weight)
    {
        if (!TryGetBlendShapeIndex(blendShapeName, out int index))
            return;

        float clamped = Mathf.Clamp(weight, 0f, 100f);

        if (_targetWeights.TryGetValue(index, out float existing))
            _targetWeights[index] = Mathf.Max(existing, clamped);
        else
            _targetWeights[index] = clamped;

        _controlledIndices.Add(index);
    }

    private bool TryGetBlendShapeIndex(string blendShapeName, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(blendShapeName))
            return false;

        if (_indexByName.TryGetValue(blendShapeName, out index))
            return true;

        string normalized = NormalizeName(blendShapeName);
        return _indexByNormalizedName.TryGetValue(normalized, out index);
    }

    private static bool StateMatches(AnimatorStateInfo stateInfo, int fullHash, int shortHash)
    {
        return stateInfo.fullPathHash == fullHash || stateInfo.shortNameHash == shortHash;
    }

    private static int HashShortStateName(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            return 0;

        int lastDot = statePath.LastIndexOf('.');
        string shortName = lastDot >= 0 && lastDot < statePath.Length - 1
            ? statePath[(lastDot + 1)..]
            : statePath;

        return Animator.StringToHash(shortName);
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().Replace(" ", string.Empty);
    }

    private static float SmoothStep01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
