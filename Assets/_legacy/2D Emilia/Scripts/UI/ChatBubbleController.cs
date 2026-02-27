// ChatBubbleController.cs
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
/// <summary>
/// Controls a single chat bubble's content and sizing for two modes:
/// 1) Non-agentic: standard user/AI text or typing indicator
/// 2) Agentic: reasoning (quoted) + response rendered together
///
/// The component adapts width using <see cref="LayoutElement"/> either on the
/// text (non-agentic) or on the bubble root (agentic), so layouts remain stable.
/// Markdown is transformed into TextMeshPro tags for lightweight rich text.
/// </summary>
public class ChatBubbleController : MonoBehaviour
{
    // ============================================================
    // NON-AGENTIC (user bubble & AI typing)
    // ============================================================

    [Header("TMP Text (Non-Agentic)")]
    [Tooltip("Primary TMP text used by non-agentic bubbles (user/AI typing).")]
    public TextMeshProUGUI chatText;

    [Tooltip("Maximum width (px) for non-agentic text before wrapping.")]
    public float maxTextWidth = 400f;

    [Tooltip("Extra padding added to the measured text width.")]
    public float extraWidth = 20f;

    /// <summary>
    /// LayoutElement attached to <see cref="chatText"/> used only in non-agentic mode.
    /// </summary>
    private LayoutElement _nonAgenticLE;

    // ============================================================
    // AGENTIC (reasoning + response in one bubble)
    // ============================================================

    [Header("Quote Panel (Agentic)")]
    [Tooltip("Container for vertical bar + reasoning text (optional).")]
    public GameObject quotePanel;     // reasoning container

    [Tooltip("TMP text for the reasoning inside the quote panel.")]
    public TextMeshProUGUI quoteText; // TMP in quotePanel

    [Tooltip("Optional vertical bar graphic for the quote panel.")]
    public Image quoteBar;            // vertical bar (optional)

    [Tooltip("Additional left gutter (bar + padding) reserved for the quote.")]
    public float quoteLeftGutter = 20f;

    [Header("Agentic Width")]
    [Tooltip("Maximum width (px) of the bubble in agentic mode.")]
    public float agenticMaxWidth = 400f;

    [Tooltip("Extra padding added to the wider of reasoning/response in agentic mode.")]
    public float agenticExtraWidth = 20f;

    /// <summary>
    /// LayoutElement attached to the ROOT bubble used only in agentic mode.
    /// (Intentionally not created in Awake so non-agentic is unaffected.)
    /// </summary>
    private LayoutElement _rootLEForAgentic;

    /// <summary>
    /// Unity Awake: caches references, configures TMP, and hides quote UI by default.
    /// </summary>
    private void Awake()
    {
        // ===== Non-agentic refs =====
        if (chatText == null)
        {
            chatText = GetComponentInChildren<TextMeshProUGUI>();
        }

        chatText.richText = true;
        chatText.textWrappingMode = TextWrappingModes.Normal;
        chatText.overflowMode = TextOverflowModes.Overflow;

        _nonAgenticLE = chatText.GetComponent<LayoutElement>();
        if (_nonAgenticLE == null)
        {
            _nonAgenticLE = chatText.gameObject.AddComponent<LayoutElement>();
        }

        // ===== Agentic refs (optional; does not affect non-agentic) =====
        if (quotePanel == null)
        {
            quotePanel = transform.Find("QuotePanel")?.gameObject;
        }

        if (quoteText == null && quotePanel != null)
        {
            quoteText = quotePanel.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        if (quoteBar == null && quotePanel != null)
        {
            quoteBar = quotePanel.GetComponentInChildren<Image>(true);
        }

        if (quoteText != null)
        {
            quoteText.richText = true;
            quoteText.textWrappingMode = TextWrappingModes.Normal;
            quoteText.overflowMode = TextOverflowModes.Overflow;
        }

        // Do NOT create _rootLEForAgentic here; created lazily in SetAgentic.
        if (quotePanel != null)
        {
            quotePanel.SetActive(false);
        }
    }

    // ============================================================
    // PUBLIC API — NON-AGENTIC
    // ============================================================

    /// <summary>
    /// Renders plain text (user/AI/typing) by converting Markdown to TMP and
    /// resizing the bubble width based on the text's preferred size (non-agentic path).
    /// </summary>
    /// <param name="text">Raw text (Markdown allowed; will be converted to TMP).</param>
    public void SetText(string text)
    {
        if (chatText == null || _nonAgenticLE == null)
        {
            return;
        }

        string parsed = ParseMarkdownToTMP(text ?? string.Empty);

        chatText.text = parsed;
        chatText.ForceMeshUpdate();

        Vector2 size = chatText.GetPreferredValues(parsed);
        float targetW = Mathf.Clamp(size.x + extraWidth, 0f, maxTextWidth);
        _nonAgenticLE.preferredWidth = targetW;
    }

    // ============================================================
    // PUBLIC API — AGENTIC
    // ============================================================

    /// <summary>
    /// Renders an agentic bubble with optional reasoning (quoted panel) and a response body.
    /// Uses a LayoutElement on the ROOT to size the bubble based on the widest sub-section.
    /// </summary>
    /// <param name="reasoning">Reasoning text (Markdown allowed). Optional.</param>
    /// <param name="response">Response text (Markdown allowed). Optional.</param>
    public void SetAgentic(string reasoning, string response)
    {
        // Fallback: if body text is missing, degrade to a compact composed string.
        if (chatText == null)
        {
            SetText(BuildAgenticFallback(reasoning, response));
            return;
        }

        bool hasReason = !string.IsNullOrWhiteSpace(reasoning);
        if (quotePanel != null)
        {
            quotePanel.SetActive(hasReason);
        }

        // Lazily create root LayoutElement for agentic mode
        if (_rootLEForAgentic == null)
        {
            _rootLEForAgentic = GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
        }

        // 1) Reasoning width calculation
        float quoteWidth = 0f;
        if (hasReason && quoteText != null)
        {
            string r = StyleQuote(ParseMarkdownToTMP(reasoning.Trim()));
            quoteText.text = r;
            quoteText.ForceMeshUpdate();

            Vector2 rSize = quoteText.GetPreferredValues(r);
            quoteWidth = rSize.x + quoteLeftGutter; // includes bar + left padding
        }

        // 2) Response body
        string body = ParseMarkdownToTMP((response ?? string.Empty).Trim());
        chatText.text = body;
        chatText.ForceMeshUpdate();
        Vector2 bSize = chatText.GetPreferredValues(body);

        // 3) Root width = max(reasoning, body) + agentic padding (clamped)
        float contentW = Mathf.Max(bSize.x, quoteWidth);
        float targetW = Mathf.Clamp(contentW + agenticExtraWidth, 0f, agenticMaxWidth);
        _rootLEForAgentic.preferredWidth = targetW;
    }

    // ============================================================
    // HELPERS (used by both modes)
    // ============================================================

    /// <summary>
    /// Styles reasoning as soft/italicized text. Adjust color to match your theme.
    /// </summary>
    private string StyleQuote(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }
        return $"<color=#FFFFFF><i>{s}</i></color>";
    }

    /// <summary>
    /// Fallback formatter if the dedicated quote panel is unavailable.
    /// Produces a simple two-section text block.
    /// </summary>
    private string BuildAgenticFallback(string reasoning, string response)
    {
        string quote = string.IsNullOrWhiteSpace(reasoning) ? string.Empty : $"> {reasoning.Trim()}";
        string body = string.IsNullOrWhiteSpace(response) ? string.Empty : response.Trim();
        if (string.IsNullOrEmpty(quote))
        {
            return body;
        }
        return $"🧠 Reasoning\n{quote}\n\n💬 Response\n{body}";
    }

    /// <summary>
    /// Converts a subset of Markdown to TMP tags:
    /// - Fenced code blocks and inline code → monospaced (escaped) with &lt;noparse&gt;
    /// - Headings (# to ######) → larger sizes
    /// - Bold/italic/underline/strikethrough
    /// - Links [text](url) → clickable TMP links
    /// - Blockquotes (>) and simple bullets (- or *)
    /// </summary>
    /// <param name="input">Raw Markdown-like string.</param>
    /// <returns>String with TMP tags appropriate for TextMeshPro.</returns>
    private string ParseMarkdownToTMP(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string output = input;

        // 1) Fenced code block ```lang\ncode```
        output = Regex.Replace(output, @"```(?:\w+)?\n([\s\S]+?)```", m =>
        {
            string code = m.Groups[1].Value
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            return $"<noparse><font=\"Courier New\">{code}</font></noparse>";
        });

        // 2) Inline code `code`
        output = Regex.Replace(output, @"`(.+?)`", m =>
        {
            string code = m.Groups[1].Value
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            return $"<noparse><font=\"Courier New\">{code}</font></noparse>";
        });

        // 3) Headings # … ######
        output = Regex.Replace(output, @"^###### (.+)$", "<size=24>$1</size>", RegexOptions.Multiline);
        output = Regex.Replace(output, @"^##### (.+)$",  "<size=26>$1</size>", RegexOptions.Multiline);
        output = Regex.Replace(output, @"^#### (.+)$",   "<size=28>$1</size>", RegexOptions.Multiline);
        output = Regex.Replace(output, @"^### (.+)$",    "<size=30>$1</size>", RegexOptions.Multiline);
        output = Regex.Replace(output, @"^## (.+)$",     "<size=32>$1</size>", RegexOptions.Multiline);
        output = Regex.Replace(output, @"^# (.+)$",      "<size=34>$1</size>", RegexOptions.Multiline);

        // 4) Basic formatting
        output = Regex.Replace(output, @"\*\*(.+?)\*\*", "<b>$1</b>"); // bold
        output = Regex.Replace(output, @"__(.+?)__",     "<u>$1</u>"); // underline
        output = Regex.Replace(output, @"~~(.+?)~~",     "<s>$1</s>"); // strikethrough
        output = Regex.Replace(output, @"\*(.+?)\*",     "<i>$1</i>"); // italic (single *)
        output = Regex.Replace(output, @"_(.+?)_",       "<i>$1</i>"); // italic (underscore)

        // 5) Links [text](url)
        output = Regex.Replace(
            output,
            @"\[(.+?)\]\((https?:\/\/[^\s]+?)\)",
            "<link=\"$2\"><color=#0000EE><u>$1</u></color></link>"
        );

        // 6) Blockquote > text
        output = Regex.Replace(
            output,
            @"^> (.+)$",
            "<indent=20%><i>$1</i></indent>",
            RegexOptions.Multiline
        );

        // 7) Bullet list - item / * item
        output = Regex.Replace(
            output,
            @"^[-\*] (.+)$",
            "• $1",
            RegexOptions.Multiline
        );

        return output;
    }
}