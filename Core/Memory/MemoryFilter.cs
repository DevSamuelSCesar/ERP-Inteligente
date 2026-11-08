using System;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Skills;

namespace erp_inteligente.Core.Memory;

/// <summary>Result of <see cref="MemoryFilter.Check"/>: the normalized text, or a pt-BR reason plus a short audit code.</summary>
public sealed record MemoryFilterResult(bool Accepted, string Text, string? Reason, string? Code)
{
    public static MemoryFilterResult Accept(string text) => new(true, text, null, null);
    public static MemoryFilterResult Refuse(string reason, string code) => new(false, string.Empty, reason, code);
}

/// <summary>
/// Protection filter applied before any memory is written (references/ARQUITETURA.md section 8): size limit,
/// no personal or sensitive data (CPF, full CNPJ, e-mail, phone) and no text that reads like an instruction to
/// the model. Memories come back into the prompt later, so this is a prompt-injection barrier as well as a
/// privacy one. The rules are deliberately conservative: a refused sentence can be rephrased.
/// </summary>
public static class MemoryFilter
{
    public const int MaxLength = 1000;

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static Regex Rx(string pattern) => new(pattern, Options, Timeout);

    private static readonly Regex Cpf = Rx(@"(?<!\d)(?:\d{3}\.\d{3}\.\d{3}-\d{2}|\d{11})(?!\d)");
    private static readonly Regex Cnpj = Rx(@"(?<!\d)(?:\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}|\d{14})(?!\d)");
    private static readonly Regex Email = Rx(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+");

    // DDD + number (10-11 digits, optional +55, parentheses, spaces and dashes), or a local number 9xxxx-xxxx / xxxx-xxxx.
    // Years such as 2025-2026 and codes with letters around the digits (L300-2611A) are not phones.
    private static readonly Regex Phone = Rx(
        @"(?<![\w-])(?:\+?55[\s-]?)?\(?\d{2}\)?[\s-]?9?\d{4}[\s-]?\d{4}(?![\w-])" +
        @"|(?<![\w-])(?:9\d{4}-\d{4}|(?!(?:19|20)\d{2}-)\d{4}-\d{4})(?![\w-])");

    // Raw text (markup that could break out of the prompt delimiters).
    private static readonly Regex Markup = Rx(@"</|<\s*(?:dados|skill|system|assistant|instru)|```|<\|");

    // Folded text (lower case, no accents).
    private static readonly Regex[] Instruction =
    [
        Rx(@"\b(?:ignor\w*|desconsider\w*|esquec\w*|disregard|forget|override|sobrescrev\w*)\b[^.!?]{0,40}\b(?:instru\w*|regra\w*|anterior\w*|acima|previous|prior|above|rules?|instructions?|system|sistema|tudo|everything)"),
        Rx(@"\bdesconsidere\b"),
        Rx(@"\bvoce\s+deve\s+agora\b"),
        Rx(@"\ba\s+partir\s+de\s+agora\b[^.!?]{0,20}\b(?:voce|tu|o\s+assistente|a\s+ia|nexus)\b"),
        Rx(@"\bfrom\s+now\s+on\b[^.!?]{0,20}\byou\b"),
        Rx(@"\byou\s+(?:must|should|are)\s+now\b|\bvoce\s+agora\s+(?:e|eh)\b"),
        Rx(@"\bsystem\s+prompt\b|\bprompts?\b|\bjailbreak\w*|\bdan\s+mode\b"),
        Rx(@"\baja\s+como\b|\bact\s+as\b|\bpretend\b|\bfinja\s+(?:ser|que)\b|\bse\s+comporte\s+como\b|\broleplay\b"),
        Rx(@"\b(?:system|assistant|developer)\s*:"),
        Rx(@"\baprov\w*\b[^.!?]{0,40}\b(?:automatic\w*|sem\s+(?:pedir\s+|exigir\s+)?(?:confirm\w*|aprova\w*|revis\w*|supervis\w*))"),
        Rx(@"\b(?:automatic\w*|automaticamente)\b[^.!?]{0,30}\baprov\w*|\bauto[\s-]?approv\w*|\bapprov\w*\b[^.!?]{0,30}\bautomatically\b"),
        Rx(@"\b(?:revel\w*|mostr\w*|exib\w*|imprim\w*|envi\w*|vaz\w*|divulg\w*|reveal|show|print|send|leak|expose|dump|display)\b[^.!?]{0,40}\b(?:senhas?|passwords?|passwd|tokens?|api[\s-]?keys?|segredos?|secrets?|credenciais|credentials|chave\s+(?:d[eao]s?\s+)?(?:api|secreta|criptografica|mestra|privada|cofre)|private\s+key|secret\s+key)\b"),
    ];

    /// <summary>
    /// Normalizes <paramref name="content"/> (control, format and bidi characters removed, whitespace collapsed)
    /// and checks it. Never throws; a refusal carries a pt-BR reason and a short code for the audit trail.
    /// </summary>
    public static MemoryFilterResult Check(string? content)
    {
        var text = SkillText.NormalizeLine(content);
        if (text.Length == 0)
            return MemoryFilterResult.Refuse("O conteúdo da memória está vazio.", "empty");
        if (text.Length > MaxLength)
            return MemoryFilterResult.Refuse($"A memória passa de {MaxLength} caracteres. Resuma o conteúdo.", "too_long");

        try
        {
            if (Cnpj.IsMatch(text))
                return MemoryFilterResult.Refuse("A memória contém um CNPJ completo. Use só o nome do parceiro ou o CNPJ mascarado.", "cnpj");
            if (Cpf.IsMatch(text))
                return MemoryFilterResult.Refuse("A memória contém um CPF. Dados pessoais não podem ser guardados.", "cpf");
            if (Email.IsMatch(text))
                return MemoryFilterResult.Refuse("A memória contém um e-mail. Dados pessoais não podem ser guardados.", "email");
            if (Phone.IsMatch(text))
                return MemoryFilterResult.Refuse("A memória contém um telefone. Dados pessoais não podem ser guardados.", "phone");
            if (Markup.IsMatch(text))
                return Instructionlike();

            var folded = TextFold.Fold(text);
            foreach (var rule in Instruction)
                if (rule.IsMatch(folded)) return Instructionlike();
        }
        catch (RegexMatchTimeoutException)
        {
            return MemoryFilterResult.Refuse("Não foi possível validar o conteúdo da memória.", "timeout");
        }

        return MemoryFilterResult.Accept(text);
    }

    private static MemoryFilterResult Instructionlike() => MemoryFilterResult.Refuse(
        "A memória parece uma instrução para o assistente. Registre apenas fatos, preferências e correções do armazém.",
        "instruction");
}
