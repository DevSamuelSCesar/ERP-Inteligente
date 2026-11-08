using System;
using System.Globalization;
using System.Text;

namespace erp_inteligente.Core.Skills;

/// <summary>
/// Normalization of skill text that comes from the UI or from the LLM: control characters, invisible
/// formatting characters (zero-width, bidi overrides) and line-ending variants are removed before validation.
/// </summary>
public static class SkillText
{
    /// <summary>
    /// Normalizes multi-line text: drops control characters except <c>\n</c> and <c>\t</c>, drops the Unicode
    /// Format category, converts <c>\r\n</c> and <c>\r</c> to <c>\n</c>, trims trailing whitespace of every line
    /// and of the whole text, and removes leading blank lines.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var clean = StripInvisible(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        var lines = clean.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines).Trim('\n').TrimEnd();
    }

    /// <summary>
    /// Normalizes a single-line value: like <see cref="Normalize"/>, but every run of whitespace (newlines
    /// included) becomes one space and the result is trimmed.
    /// </summary>
    public static string NormalizeLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var clean = StripInvisible(text);
        var builder = new StringBuilder(clean.Length);
        var pendingSpace = false;
        foreach (var c in clean)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string StripInvisible(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t' or '\r') { builder.Append(c); continue; }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
