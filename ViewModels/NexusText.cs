using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace erp_inteligente.ViewModels;

/// <summary>What a highlighted piece of a Nexus answer refers to (references/UI.md, section 4.2).</summary>
public enum NexusTextKind
{
    Plain,
    Sku,
    Location,
    Zone,
    Order,
    Product,
    Quantity,
    Plan,
}

/// <summary>One run of a Nexus answer: plain text or a recognized warehouse entity.</summary>
public sealed record NexusTextSegment(string Text, NexusTextKind Kind);

/// <summary>
/// Splits an answer into plain and highlighted runs. Recognition is deterministic (patterns and the known product
/// names), never markup written by the model, so the LLM cannot style text and Markdown is never interpreted.
/// </summary>
public static partial class NexusText
{
    /// <summary>Answers longer than this are shown without highlighting (they are capped upstream anyway).</summary>
    public const int MaxLength = 4000;

    private const int MaxProductNames = 2000;

    [GeneratedRegex(@"(?<![\w-])[A-Z]{3}-[0-9]{3}(?![\w-])")] private static partial Regex Sku();
    [GeneratedRegex(@"(?<![\w-])[A-Z]{1,4}-[0-9]{2}(?:-[0-9]{2})?(?![\w-])")] private static partial Regex Location();
    [GeneratedRegex(@"(?<![\w-])[A-Z]{2,4}-[A-Z]{2,6}-[0-9]{3,8}(?![\w-])")] private static partial Regex Order();
    [GeneratedRegex(@"(?<=\b[Zz]onas?\s)[A-Z]{1,4}(?![\w-])")] private static partial Regex Zone();
    [GeneratedRegex(@"(?<![\w,.])[0-9]{1,9}(?:[.,][0-9]{1,3})?\s?(?:cx|caixas?|un|unidades?|kg|pc|pct|gl|fr|l)(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex Quantity();
    [GeneratedRegex(@"\b[Pp]lanos?\s[0-9]{1,9}\b")] private static partial Regex Plan();

    /// <param name="productNames">Catalog names to highlight (longest match wins); compared ignoring case.</param>
    public static IReadOnlyList<NexusTextSegment> Parse(string text, IEnumerable<string>? productNames = null)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        if (text.Length > MaxLength)
            return [new NexusTextSegment(text, NexusTextKind.Plain)];

        var matches = new List<(int Start, int Length, NexusTextKind Kind)>();
        void Add(Regex regex, NexusTextKind kind)
        {
            foreach (Match m in regex.Matches(text))
                matches.Add((m.Index, m.Length, kind));
        }

        // Order matters only for ties: earlier kinds win when two matches start at the same place with equal length.
        Add(Order(), NexusTextKind.Order);
        Add(Sku(), NexusTextKind.Sku);
        Add(Location(), NexusTextKind.Location);
        Add(Zone(), NexusTextKind.Zone);
        Add(Plan(), NexusTextKind.Plan);
        Add(Quantity(), NexusTextKind.Quantity);

        if (productNames is not null)
        {
            foreach (var name in productNames.Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(n => n.Length).Take(MaxProductNames))
            {
                var from = 0;
                while (from < text.Length)
                {
                    var at = text.IndexOf(name, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                    var after = at + name.Length >= text.Length || !char.IsLetterOrDigit(text[at + name.Length]);
                    if (before && after)
                        matches.Add((at, name.Length, NexusTextKind.Product));
                    from = at + name.Length;
                }
            }
        }

        // Earliest first, then longest; overlapping matches are dropped.
        var chosen = new List<(int Start, int Length, NexusTextKind Kind)>();
        var end = 0;
        foreach (var m in matches.OrderBy(m => m.Start).ThenByDescending(m => m.Length))
        {
            if (m.Start < end) continue;
            chosen.Add(m);
            end = m.Start + m.Length;
        }

        var segments = new List<NexusTextSegment>(chosen.Count * 2 + 1);
        var position = 0;
        foreach (var (start, length, kind) in chosen)
        {
            if (start > position)
                segments.Add(new NexusTextSegment(text[position..start], NexusTextKind.Plain));
            segments.Add(new NexusTextSegment(text.Substring(start, length), kind));
            position = start + length;
        }
        if (position < text.Length)
            segments.Add(new NexusTextSegment(text[position..], NexusTextKind.Plain));
        return segments;
    }
}
