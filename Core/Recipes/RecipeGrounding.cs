using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Memory;

namespace erp_inteligente.Core.Recipes;

/// <summary>
/// Deterministic check after the Recipe Matcher: every parameter value must come from the request ("nunca invente nem
/// assuma valores", references/ARQUITETURA.md, section 10.1). A small model fills a missing value with a plausible one
/// ("guarda 15 LIM-201 da doca 2" got a destination it was never told); this turns that into "no recipe", so the
/// Planner asks instead.
/// <para>
/// Request and values are folded (case, accents), split on anything that is not a letter or digit, and numbers lose
/// leading zeros, so "DOCA-01" is found in "doca 1" and "LIM-201" in "lim-201". Each request token backs one value:
/// in "guarda o ABC-123 da doca 1 no A-01-01" the "1" belongs to the dock and cannot also be the quantity. Text values
/// are placed first (longest first), then numbers, written in digits or in words ("quarenta", "cento e vinte"); the
/// sign is ignored (a write-off of "4" is the quantity -4). Booleans need no evidence. The reference implementation for
/// the fine-tuning metrics is grounded() in tools/finetune/evaluate.py: change both together.
/// </para>
/// </summary>
public static partial class RecipeGrounding
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separator();

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["um"] = 1, ["uma"] = 1, ["dois"] = 2, ["duas"] = 2, ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9, ["dez"] = 10, ["onze"] = 11, ["doze"] = 12, ["treze"] = 13,
        ["quatorze"] = 14, ["catorze"] = 14, ["quinze"] = 15, ["dezesseis"] = 16, ["dezessete"] = 17, ["dezoito"] = 18,
        ["dezenove"] = 19, ["vinte"] = 20, ["trinta"] = 30, ["quarenta"] = 40, ["cinquenta"] = 50, ["sessenta"] = 60,
        ["setenta"] = 70, ["oitenta"] = 80, ["noventa"] = 90, ["cem"] = 100, ["cento"] = 100, ["duzentos"] = 200,
        ["duzentas"] = 200, ["trezentos"] = 300, ["trezentas"] = 300, ["quatrocentos"] = 400, ["quatrocentas"] = 400,
        ["quinhentos"] = 500, ["quinhentas"] = 500, ["seiscentos"] = 600, ["seiscentas"] = 600, ["setecentos"] = 700,
        ["setecentas"] = 700, ["oitocentos"] = 800, ["oitocentas"] = 800, ["novecentos"] = 900, ["novecentas"] = 900,
    };

    /// <summary>The request split into comparable tokens (folded, no separators, numbers without leading zeros).</summary>
    public static IReadOnlyList<string> Tokens(string? text) =>
        Separator().Split(TextFold.Fold(text))
            .Where(t => t.Length > 0)
            .Select(t => t.All(char.IsAsciiDigit) ? t.TrimStart('0') is { Length: > 0 } s ? s : "0" : t)
            .ToList();

    /// <summary>
    /// True when every value of <paramref name="values"/> is backed by its own tokens of <paramref name="request"/>;
    /// otherwise <paramref name="ungrounded"/> names the first parameter that is not.
    /// </summary>
    public static bool IsGrounded(string request, JsonObject values, out string? ungrounded)
    {
        ArgumentNullException.ThrowIfNull(values);
        var tokens = Tokens(request);
        var used = new bool[tokens.Count];
        var words = NumberRuns(tokens);

        var texts = new List<(string Name, IReadOnlyList<string> Tokens)>();
        var numbers = new List<(string Name, decimal Value)>();
        foreach (var (name, node) in values)
            foreach (var leaf in Leaves(node))
            {
                if (leaf.TryGetValue<bool>(out _)) continue;
                if (leaf.TryGetValue<string>(out var text))
                    texts.Add((name, Tokens(text)));
                else if (leaf.TryGetValue<decimal>(out var number))
                    numbers.Add((name, Math.Abs(number)));
            }

        foreach (var (name, wanted) in texts.OrderByDescending(t => t.Tokens.Count))
            if (wanted.Count == 0 || !Claim(tokens, used, wanted))
            {
                ungrounded = name;
                return false;
            }

        foreach (var (name, value) in numbers)
        {
            var digits = value == decimal.Truncate(value)
                ? [value.ToString("0", CultureInfo.InvariantCulture)]
                : Tokens(value.ToString(CultureInfo.InvariantCulture));
            if (Claim(tokens, used, digits)) continue;
            var run = words.FirstOrDefault(w => w.Value == value && !used.Skip(w.Start).Take(w.Length).Any(u => u));
            if (run.Length > 0)
            {
                for (var i = run.Start; i < run.Start + run.Length; i++) used[i] = true;
                continue;
            }
            ungrounded = name;
            return false;
        }
        ungrounded = null;
        return true;
    }

    private static IEnumerable<JsonValue> Leaves(JsonNode? node) => node switch
    {
        JsonValue v => [v],
        JsonArray a => a.SelectMany(Leaves),
        JsonObject o => o.Select(p => p.Value).SelectMany(Leaves),
        _ => [],
    };

    /// <summary>Marks the first free contiguous occurrence of <paramref name="wanted"/> as used.</summary>
    private static bool Claim(IReadOnlyList<string> tokens, bool[] used, IReadOnlyList<string> wanted)
    {
        for (var start = 0; start + wanted.Count <= tokens.Count; start++)
        {
            var fits = true;
            for (var k = 0; k < wanted.Count && fits; k++)
                fits = !used[start + k] && tokens[start + k] == wanted[k];
            if (!fits) continue;
            for (var k = 0; k < wanted.Count; k++) used[start + k] = true;
            return true;
        }
        return false;
    }

    /// <summary>Numbers written in words: "cento e vinte e cinco" is one run worth 125 ("e" joins the parts).</summary>
    private static List<(int Start, int Length, decimal Value)> NumberRuns(IReadOnlyList<string> tokens)
    {
        var runs = new List<(int, int, decimal)>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (!NumberWords.TryGetValue(tokens[i], out var first))
            {
                i++;
                continue;
            }
            var start = i;
            decimal total = first;
            i++;
            while (i + 1 < tokens.Count && tokens[i] == "e" && NumberWords.TryGetValue(tokens[i + 1], out var next))
            {
                total += next;
                i += 2;
            }
            runs.Add((start, i - start, total));
        }
        return runs;
    }
}
