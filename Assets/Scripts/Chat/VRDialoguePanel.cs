using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Core VR dialogue display with typewriter effect and page-based pagination.
///
/// Uses TMP's built-in Page overflow mode to automatically split text into pages
/// that fit the visible rect. The user pokes a continue-target (or waits for
/// auto-advance) to progress through pages. A "▼" prompt pulses while waiting.
///
/// Attach to the VRDialoguePanel root alongside <see cref="VRDialogueFader"/>
/// and <see cref="DialoguePanelPositioner"/>.
/// </summary>
public class VRDialoguePanel : MonoBehaviour
{
    #region Inspector

    [Header("Text Elements")]
    [SerializeField] private TMP_Text _bodyText;
    [SerializeField] private TMP_Text _nameLabel;

    [Header("Quote Panel (Agentic)")]
    [SerializeField] private GameObject _quotePanel;
    [SerializeField] private TMP_Text   _quoteText;

    [Header("Pagination UI")]
    [SerializeField] private TMP_Text   _pageIndicator;   // "1/3"
    [SerializeField] private TMP_Text   _continuePrompt;  // "▼"
    [SerializeField] private Button      _pokeTarget;

    [Header("Typewriter")]
    [SerializeField] private float _charsPerSecond = 30f;

    [Header("Pagination")]
    [Tooltip("Seconds before auto-advancing to next page if user doesn't poke.")]
    [SerializeField] private float _autoAdvanceDelay = 10f;

    #endregion

    #region State

    private VRDialogueFader _fader;
    private Coroutine _typewriterCo;
    private Coroutine _typingIndicatorCo;
    private Coroutine _continuePromptCo;
    private Coroutine _autoAdvanceCo;

    // Pagination
    private readonly List<PageSpan> _pages = new();
    private int  _currentPage;
    private bool _waitingForAdvance;
    private string _fullParsedText;

    /// <summary>Fired when the last page finishes typewriter reveal.</summary>
    public event Action OnContentFullyDisplayed;

    private struct PageSpan
    {
        public int FirstCharIndex;
        public int LastCharIndex;
    }

    #endregion

    #region Unity

    private void Awake()
    {
        _fader = GetComponent<VRDialogueFader>();

        if (_quotePanel != null) _quotePanel.SetActive(false);
        if (_continuePrompt != null) _continuePrompt.gameObject.SetActive(false);
        if (_pageIndicator != null) _pageIndicator.gameObject.SetActive(false);

        if (_pokeTarget != null)
            _pokeTarget.onClick.AddListener(AdvancePage);
    }

    private void OnDestroy()
    {
        if (_pokeTarget != null)
            _pokeTarget.onClick.RemoveListener(AdvancePage);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Display a standard (non-agentic) AI response with typewriter + pagination.
    /// </summary>
    public void ShowText(string rawText)
    {
        StopAllEffects();

        if (_quotePanel != null) _quotePanel.SetActive(false);

        _fullParsedText = MarkdownToTMP.Convert(rawText ?? string.Empty);

        _fader.CancelAutoHide();
        _fader.FadeIn();

        PreparePagesAndStart(_fullParsedText);
    }

    /// <summary>
    /// Display an agentic response (reasoning quote + response body) with typewriter + pagination.
    /// </summary>
    public void ShowAgentic(string reasoning, string response)
    {
        StopAllEffects();

        bool hasReasoning = !string.IsNullOrWhiteSpace(reasoning);

        if (_quotePanel != null)
        {
            _quotePanel.SetActive(hasReasoning);
            if (hasReasoning && _quoteText != null)
            {
                string parsed = MarkdownToTMP.Convert(reasoning.Trim());
                _quoteText.text = $"<i>{parsed}</i>";
            }
        }

        _fullParsedText = MarkdownToTMP.Convert((response ?? string.Empty).Trim());

        _fader.CancelAutoHide();
        _fader.FadeIn();

        PreparePagesAndStart(_fullParsedText);
    }

    /// <summary>
    /// Shows an animated typing indicator (".", ". .", ". . .").
    /// </summary>
    public void ShowTypingIndicator()
    {
        StopAllEffects();

        if (_quotePanel != null) _quotePanel.SetActive(false);
        HidePaginationUI();

        _fader.CancelAutoHide();
        _fader.FadeIn();

        _typingIndicatorCo = StartCoroutine(CoTypingIndicator());
    }

    /// <summary>Fade out and clear all content.</summary>
    public void Hide()
    {
        StopAllEffects();
        _fader.FadeOut();
    }

    /// <summary>
    /// Advance to the next page. Called by poke interaction or auto-advance timer.
    /// </summary>
    public void AdvancePage()
    {
        if (!_waitingForAdvance) return;

        _waitingForAdvance = false;
        StopAutoAdvance();
        HideContinuePrompt();

        _currentPage++;
        if (_currentPage < _pages.Count)
        {
            UpdatePageIndicator();
            _typewriterCo = StartCoroutine(CoTypewriterPage(_currentPage));
        }
    }

    #endregion

    #region Pagination

    private void PreparePagesAndStart(string parsedText)
    {
        _bodyText.overflowMode = TextOverflowModes.Page;
        _bodyText.text = parsedText;
        _bodyText.maxVisibleCharacters = int.MaxValue;
        _bodyText.ForceMeshUpdate();

        BuildPages();

        _currentPage = 0;

        if (_pages.Count > 1)
        {
            UpdatePageIndicator();
            if (_pageIndicator != null) _pageIndicator.gameObject.SetActive(true);
        }
        else
        {
            if (_pageIndicator != null) _pageIndicator.gameObject.SetActive(false);
        }

        _typewriterCo = StartCoroutine(CoTypewriterPage(0));
    }

    /// <summary>
    /// Builds page spans using TMP's built-in page overflow data.
    /// Must be called after ForceMeshUpdate so textInfo.pageInfo is populated.
    /// </summary>
    private void BuildPages()
    {
        _pages.Clear();

        var info = _bodyText.textInfo;
        int pageCount = info.pageCount;

        if (pageCount == 0)
        {
            int lastChar = Mathf.Max(0, info.characterCount - 1);
            _pages.Add(new PageSpan { FirstCharIndex = 0, LastCharIndex = lastChar });
            return;
        }

        for (int p = 0; p < pageCount; p++)
        {
            var pi = info.pageInfo[p];
            _pages.Add(new PageSpan
            {
                FirstCharIndex = pi.firstCharacterIndex,
                LastCharIndex  = pi.lastCharacterIndex
            });
        }
    }

    private void UpdatePageIndicator()
    {
        if (_pageIndicator == null) return;
        _pageIndicator.text = $"{_currentPage + 1}/{_pages.Count}";
    }

    #endregion

    #region Typewriter

    private IEnumerator CoTypewriterPage(int pageIndex)
    {
        if (pageIndex >= _pages.Count) yield break;

        var span = _pages[pageIndex];
        float interval = _charsPerSecond > 0f ? 1f / _charsPerSecond : 0f;

        // Tell TMP to display this page (1-indexed)
        _bodyText.pageToDisplay = pageIndex + 1;

        // Reveal characters from this page's start to end
        for (int i = span.FirstCharIndex; i <= span.LastCharIndex; i++)
        {
            _bodyText.maxVisibleCharacters = i + 1;
            yield return new WaitForSeconds(interval);
        }

        _typewriterCo = null;

        // Page finished — decide what happens next
        bool isLastPage = (pageIndex >= _pages.Count - 1);

        if (isLastPage)
        {
            HidePaginationUI();
            OnContentFullyDisplayed?.Invoke();
            _fader.StartAutoHideTimer();
        }
        else
        {
            _waitingForAdvance = true;
            ShowContinuePrompt();
            StartAutoAdvance();
        }
    }

    #endregion

    #region Typing Indicator

    private IEnumerator CoTypingIndicator()
    {
        var dots = new[] { "", ".", ". .", ". . ." };
        int i = 0;
        HidePaginationUI();

        while (true)
        {
            _bodyText.overflowMode = TextOverflowModes.Overflow;
            _bodyText.text = dots[i];
            _bodyText.maxVisibleCharacters = int.MaxValue;
            i = (i + 1) % dots.Length;
            yield return new WaitForSeconds(0.5f);
        }
    }

    #endregion

    #region Continue Prompt & Auto-Advance

    private void ShowContinuePrompt()
    {
        if (_continuePrompt == null) return;
        _continuePrompt.gameObject.SetActive(true);
        _continuePromptCo = StartCoroutine(CoPulseContinuePrompt());
    }

    private void HideContinuePrompt()
    {
        if (_continuePromptCo != null)
        {
            StopCoroutine(_continuePromptCo);
            _continuePromptCo = null;
        }
        if (_continuePrompt != null) _continuePrompt.gameObject.SetActive(false);
    }

    private IEnumerator CoPulseContinuePrompt()
    {
        while (true)
        {
            float t = Mathf.PingPong(Time.time * 2f, 1f);
            float alpha = Mathf.Lerp(0.3f, 1f, t);
            if (_continuePrompt != null)
            {
                var c = _continuePrompt.color;
                c.a = alpha;
                _continuePrompt.color = c;
            }
            yield return null;
        }
    }

    private void StartAutoAdvance()
    {
        if (_autoAdvanceDelay <= 0f) return;
        _autoAdvanceCo = StartCoroutine(CoAutoAdvance());
    }

    private void StopAutoAdvance()
    {
        if (_autoAdvanceCo != null)
        {
            StopCoroutine(_autoAdvanceCo);
            _autoAdvanceCo = null;
        }
    }

    private IEnumerator CoAutoAdvance()
    {
        yield return new WaitForSeconds(_autoAdvanceDelay);
        AdvancePage();
    }

    #endregion

    #region Helpers

    private void HidePaginationUI()
    {
        HideContinuePrompt();
        if (_pageIndicator != null) _pageIndicator.gameObject.SetActive(false);
    }

    private void StopAllEffects()
    {
        if (_typewriterCo != null)
        {
            StopCoroutine(_typewriterCo);
            _typewriterCo = null;
        }
        if (_typingIndicatorCo != null)
        {
            StopCoroutine(_typingIndicatorCo);
            _typingIndicatorCo = null;
        }

        _waitingForAdvance = false;
        StopAutoAdvance();
        HideContinuePrompt();

        _pages.Clear();
        _currentPage = 0;
    }

    #endregion
}
