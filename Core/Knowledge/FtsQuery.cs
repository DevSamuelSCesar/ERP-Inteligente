using System;
using System.Collections.Generic;
using System.Text;
using erp_inteligente.Core.Memory;

namespace erp_inteligente.Core.Knowledge;

/// <summary>
/// Builds FTS5 <c>MATCH</c> expressions from free text. Nothing the user types becomes FTS syntax: the text is
/// folded (lower case, no accents), split into letter/digit terms, and every term is emitted as a quoted string
/// (internal quotes doubled), joined by <c>OR</c>. Longer terms also match as prefixes ("refrigerado" finds
/// "refrigerados"). Operators such as <c>NEAR</c>, <c>AND</c>, <c>*</c>, <c>col:x</c> and parentheses are
/// split or quoted, so they are matched as plain words.
/// </summary>
public static class FtsQuery
{
    public const int MaxTerms = 8;
    private const int MinTermLength = 2;
    internal const int PrefixFromLength = 5;

    // Very common pt-BR/en words: with OR semantics they only add noise.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "em", "no", "na", "nos", "nas", "um", "uma", "uns", "umas", "ao", "aos",
        "as", "os", "por", "para", "com", "sem", "que", "se", "ou", "e", "a", "o", "como", "qual", "quais",
        "the", "of", "to", "and", "or", "in", "on", "for", "is", "are", "at", "be", "by", "an",
    };

    /// <summary>Distinct folded search terms (stop words and one-character terms dropped), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Terms(string? text, int max = MaxTerms)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folded = TextFold.Fold(text);
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            var term = current.ToString();
            current.Clear();
            if (term.Length < MinTermLength || StopWords.Contains(term)) return;
            if (terms.Count < max && seen.Add(term)) terms.Add(term);
        }

        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) current.Append(c);
            else Flush();
        }
        Flush();
        return terms;
    }

    /// <summary>The MATCH expression, or null when the text has no usable term.</summary>
    public static string? Build(string? text, int max = MaxTerms)
    {
        var terms = Terms(text, max);
        if (terms.Count == 0) return null;

        var builder = new StringBuilder();
        foreach (var term in terms)
        {
            if (builder.Length > 0) builder.Append(" OR ");
            builder.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            if (term.Length >= PrefixFromLength) builder.Append('*');
        }
        return builder.ToString();
    }
}
