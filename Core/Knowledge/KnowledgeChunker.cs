using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using erp_inteligente.Core.Skills;

namespace erp_inteligente.Core.Knowledge;

/// <summary>A Markdown document to index; <see cref="Path"/> is the stable <c>source_path</c> ("knowledge/01-x.md").</summary>
public sealed record KnowledgeSource(string Path, string Markdown);

/// <summary>One indexed chunk. <see cref="Content"/> starts with the document title and the section heading.</summary>
public sealed record KnowledgeChunk(string SourcePath, string Heading, string Content, int TokenCount, string Sha256);

/// <summary>
/// Splits Markdown into chunks (references/ARQUITETURA.md section 8): one chunk per <c>##</c> section, prefixed
/// with the document title and the section heading; sections above ~500 tokens are split on line boundaries with
/// ~50 tokens of overlap (tokens are estimated as characters / 4). Text before the first <c>##</c> becomes the
/// "Introdução" chunk when it is not trivial. Headings are unique per document.
/// </summary>
public static class KnowledgeChunker
{
    public const int MaxTokens = 500;
    public const int OverlapTokens = 50;
    public const string IntroHeading = "Introdução";
    private const int CharsPerToken = 4;
    private const int MinIntroChars = 60;

    public static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    public static IReadOnlyList<KnowledgeChunk> Chunk(KnowledgeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = SkillText.Normalize(source.Markdown).Split('\n');

        string? title = null;
        var sections = new List<(string Heading, StringBuilder Body)>();
        var intro = new StringBuilder();
        StringBuilder current = intro;
        var inFence = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence)
            {
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
                {
                    title = line[2..].Trim();
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    current = new StringBuilder();
                    sections.Add((line[3..].Trim(), current));
                    continue;
                }
            }
            current.AppendLine(line);
        }

        title ??= System.IO.Path.GetFileNameWithoutExtension(source.Path);
        var chunks = new List<KnowledgeChunk>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        var introText = TrimBlock(intro.ToString());
        if (NonQuoteLength(introText) >= MinIntroChars) AddSection(IntroHeading, introText);
        foreach (var (heading, body) in sections)
        {
            var text = TrimBlock(body.ToString());
            if (text.Length == 0) continue;
            AddSection(heading.Length == 0 ? "Seção" : heading, text);
        }
        return chunks;

        void AddSection(string heading, string body)
        {
            var baseHeading = UniqueHeading(heading);
            var pieces = Split(body, title, baseHeading);
            for (var i = 0; i < pieces.Count; i++)
            {
                var pieceHeading = i == 0 ? baseHeading : UniqueHeading($"{baseHeading} ({i + 1})");
                var content = $"{title} - {pieceHeading}\n\n{pieces[i]}";
                chunks.Add(new KnowledgeChunk(source.Path, pieceHeading, content, EstimateTokens(content), Hash(content)));
            }
        }

        string UniqueHeading(string heading)
        {
            var candidate = heading;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{heading} ({n})";
            return candidate;
        }
    }

    private static List<string> Split(string body, string title, string heading)
    {
        var prefix = title.Length + heading.Length + 8;
        var maxChars = MaxTokens * CharsPerToken;
        if (prefix + body.Length <= maxChars) return [body];

        var budget = Math.Max(200, maxChars - prefix);
        var overlap = Math.Min(OverlapTokens * CharsPerToken, budget / 4);

        // Units are lines; a line longer than the budget is cut at whitespace.
        var units = new List<string>();
        foreach (var line in body.Split('\n'))
            foreach (var part in CutLong(line, budget)) units.Add(part);

        var pieces = new List<string>();
        var window = new List<string>();
        var size = 0;
        foreach (var unit in units)
        {
            var add = unit.Length + 1;
            if (size + add > budget && window.Count > 0)
            {
                pieces.Add(TrimBlock(string.Join('\n', window)));
                // Carry the last lines (up to the overlap) into the next piece.
                var carry = new List<string>();
                var carried = 0;
                for (var i = window.Count - 1; i >= 0 && carried + window[i].Length + 1 <= overlap; i--)
                {
                    carry.Insert(0, window[i]);
                    carried += window[i].Length + 1;
                }
                while (carry.Count > 0 && carried + add > budget)
                {
                    carried -= carry[0].Length + 1;
                    carry.RemoveAt(0);
                }
                window = carry;
                size = carried;
            }
            window.Add(unit);
            size += add;
        }
        var last = TrimBlock(string.Join('\n', window));
        if (last.Length > 0) pieces.Add(last);
        return pieces.FindAll(p => p.Length > 0);
    }

    private static IEnumerable<string> CutLong(string line, int budget)
    {
        while (line.Length > budget)
        {
            var cut = line.LastIndexOf(' ', budget - 1, budget);
            if (cut <= 0) cut = budget;
            yield return line[..cut].TrimEnd();
            line = line[cut..].TrimStart();
        }
        yield return line;
    }

    private static string TrimBlock(string text) => text.Trim('\n', '\r', ' ', '\t');

    private static int NonQuoteLength(string text)
    {
        var total = 0;
        foreach (var line in text.Split('\n'))
            if (!line.TrimStart().StartsWith('>')) total += line.Trim().Length;
        return total;
    }

    private static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
