using System.Text.RegularExpressions;

/// <summary>
/// Static utility that converts a subset of Markdown into TextMeshPro rich-text tags.
/// Extracted from the legacy ChatBubbleController so both 2D and VR systems can share it.
/// </summary>
public static class MarkdownToTMP
{
    /// <summary>
    /// Converts Markdown-like input to TMP tags:
    /// fenced code blocks, inline code, headings, bold, italic, underline,
    /// strikethrough, links, blockquotes, and bullet lists.
    /// </summary>
    public static string Convert(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

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
        output = Regex.Replace(output, @"\*\*(.+?)\*\*", "<b>$1</b>");
        output = Regex.Replace(output, @"__(.+?)__",     "<u>$1</u>");
        output = Regex.Replace(output, @"~~(.+?)~~",     "<s>$1</s>");
        output = Regex.Replace(output, @"\*(.+?)\*",     "<i>$1</i>");
        output = Regex.Replace(output, @"_(.+?)_",       "<i>$1</i>");

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
