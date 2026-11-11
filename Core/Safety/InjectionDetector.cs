using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Memory;

namespace erp_inteligente.Core.Safety;

/// <summary>One rule that matched: a short code for the audit trail (never the text itself).</summary>
public sealed record InjectionMatch(string Code);

/// <summary>
/// Deterministic detector of text that reads like an instruction to the model (references/ARQUITETURA.md, section 12.8,
/// layer 1). It never blocks anything by itself: the Planner uses it to mark data blocks, audit the attempt
/// (<c>ai.injection</c>) and warn the operator, and the memory filter uses the core rules to refuse a memory.
/// The rules are deliberately conservative; a false positive only produces a warning.
/// </summary>
public static class InjectionDetector
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static Regex Rx(string pattern) => new(pattern, Options, Timeout);

    /// <summary>Raw text: markup that could break out of the prompt delimiters or imitate a chat role.</summary>
    private static readonly Regex Markup = Rx(@"</|<\s*(?:dados|skill|system|assistant|instru)|```|<\|");

    /// <summary>
    /// Core rules, on folded text (lower case, no accents). Shared with <see cref="MemoryFilter"/>: anything that matches
    /// here is never stored as a memory.
    /// </summary>
    private static readonly (string Code, Regex Rule)[] CoreRules =
    [
        ("override", Rx(@"\b(?:ignor\w*|desconsider\w*|esquec\w*|disregard|forget|override|sobrescrev\w*)\b[^.!?]{0,40}\b(?:instru\w*|regra\w*|anterior\w*|acima|previous|prior|above|rules?|instructions?|system|sistema|tudo|everything)")),
        ("override", Rx(@"\bdesconsidere\b")),
        ("override", Rx(@"\bvoce\s+deve\s+agora\b")),
        ("override", Rx(@"\ba\s+partir\s+de\s+agora\b[^.!?]{0,20}\b(?:voce|tu|o\s+assistente|a\s+ia|nexus)\b")),
        ("override", Rx(@"\bfrom\s+now\s+on\b[^.!?]{0,20}\byou\b")),
        ("role", Rx(@"\byou\s+(?:must|should|are)\s+now\b|\bvoce\s+agora\s+(?:e|eh)\b")),
        ("role", Rx(@"\bsystem\s+prompt\b|\bprompts?\b|\bjailbreak\w*|\bdan\s+mode\b")),
        ("role", Rx(@"\baja\s+como\b|\bact\s+as\b|\bpretend\b|\bfinja\s+(?:ser|que)\b|\bse\s+comporte\s+como\b|\broleplay\b")),
        ("role", Rx(@"\b(?:system|assistant|developer)\s*:")),
        ("approval", Rx(@"\baprov\w*\b[^.!?]{0,40}\b(?:automatic\w*|sem\s+(?:pedir\s+|exigir\s+)?(?:confirm\w*|aprova\w*|revis\w*|supervis\w*))")),
        ("approval", Rx(@"\b(?:automatic\w*|automaticamente)\b[^.!?]{0,30}\baprov\w*|\bauto[\s-]?approv\w*|\bapprov\w*\b[^.!?]{0,30}\bautomatically\b")),
        ("secrets", Rx(@"\b(?:revel\w*|mostr\w*|exib\w*|imprim\w*|envi\w*|vaz\w*|divulg\w*|reveal|show|print|send|leak|expose|dump|display)\b[^.!?]{0,40}\b(?:senhas?|passwords?|passwd|tokens?|api[\s-]?keys?|segredos?|secrets?|credenciais|credentials|chave\s+(?:d[eao]s?\s+)?(?:api|secreta|criptografica|mestra|privada|cofre)|private\s+key|secret\s+key)\b")),
    ];

    /// <summary>
    /// Extra rules for data read by the model (product descriptions, names, procedure chunks, memories, requests):
    /// orders addressed to the assistant and destructive commands. Not applied to memories, where a fact such as
    /// "o estoque da doca deve ser zerado toda sexta" is a legitimate warehouse rule.
    /// </summary>
    private static readonly (string Code, Regex Rule)[] DataRules =
    [
        ("directive", Rx(@"\b(?:a\s+ia|o\s+assistente|o\s+nexus|o\s+modelo|the\s+(?:ai|assistant|model))\s+(?:deve|precisa|tem\s+que|vai|must|should|has\s+to)\b")),
        ("directive", Rx(@"\b(?:nexus|assistente|ia)\s*[,:]\s*(?:ajuste|aprove|zere|apague|exclua|cancele|expeca|mova|mostre|revele|ignore)\b")),
        ("destructive", Rx(@"\b(?:zer\w+)\b[^.!?]{0,40}\b(?:estoque|saldos?|stock)\b|\b(?:estoque|saldos?)\b[^.!?]{0,60}\bpara\s+(?:zero|0)\b|\bset\s+(?:all\s+)?(?:the\s+)?stock\s+to\s+(?:zero|0)\b")),
        ("destructive", Rx(@"\b(?:apague|exclua|delete|remova|cancele)\b[^.!?]{0,20}\b(?:todos?|todas?|all)\b[^.!?]{0,30}\b(?:pedidos|planos|produtos|lotes|movimentos|registros|orders|plans|products)\b")),
        ("approval", Rx(@"\baprove\s+(?:o|os|todos?\s+os|este|esse)\s+planos?\b|\bapprove\s+(?:the\s+|all\s+|this\s+)?plans?\b")),
        ("sql", Rx(@"\bdrop\s+table\b|\bdelete\s+from\b|\binsert\s+into\b|\bupdate\s+\w+\s+set\b|'\s*or\s+'?1'?\s*=\s*'?1|;\s*--")),
    ];

    /// <summary>Markup or a core rule (the memory filter's test). Returns the first match, or null; a timeout is "timeout".</summary>
    public static InjectionMatch? ScanCore(string? text) => FirstMatch(text, includeDataRules: false);

    /// <summary>
    /// Scans one piece of external text with all the rules. Returns the first match, or null. Never throws: a regex
    /// timeout counts as a match ("timeout"), so a pathological text is still flagged.
    /// </summary>
    public static InjectionMatch? Scan(string? text) => FirstMatch(text, includeDataRules: true);

    private static InjectionMatch? FirstMatch(string? text, bool includeDataRules)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (Markup.IsMatch(text)) return new InjectionMatch("markup");
            var folded = TextFold.Fold(text);
            foreach (var (code, rule) in Rules(includeDataRules))
                if (rule.IsMatch(folded)) return new InjectionMatch(code);
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return new InjectionMatch("timeout");
        }
    }

    private static IEnumerable<(string Code, Regex Rule)> Rules(bool includeDataRules)
    {
        foreach (var rule in CoreRules) yield return rule;
        if (!includeDataRules) yield break;
        foreach (var rule in DataRules) yield return rule;
    }
}
