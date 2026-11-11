using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace erp_inteligente.Core.Safety;

/// <summary>
/// Filter of sensitive data in logs (references/SEGURANCA_DADOS.md, section 3.4): masks API keys, bearer tokens,
/// "senha=..." pairs, e-mail, CNPJ, CPF and phone before a text is written to the audit trail. The audit already avoids
/// sensitive fields by design; this is the safety net for free text that reaches it (model-written tool arguments,
/// usernames typed at the login, error details).
/// </summary>
public static class SensitiveDataRedactor
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static Regex Rx(string pattern) => new(pattern, Options, Timeout);

    /// <summary>"senha: x", "password=x", "api_key=x": the name stays, the value is masked.</summary>
    private static readonly Regex KeyValue = Rx(@"\b((?:senha|password|passwd|pwd|api[_-]?key|token|secret)\s*[:=]\s*)[^\s,;""']+");

    // Order matters: secrets first (they can contain digits), then documents (CNPJ before CPF: 14 digits contain 11).
    private static readonly (Regex Rule, string Mask)[] Rules =
    [
        (Rx(@"\bsk-[A-Za-z0-9_-]{16,}"), "[chave]"),
        (Rx(@"\bbearer\s+[A-Za-z0-9._~+/-]{8,}=*"), "[token]"),
        (Rx(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+"), "[email]"),
        // Whole words only: a run of digits inside a hash or a code ("a1b2...", "L300-2611A") is never a document.
        (Rx(@"(?<!\w)(?:\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}|\d{14})(?!\w)"), "[cnpj]"),
        (Rx(@"(?<!\w)(?:\d{3}\.\d{3}\.\d{3}-\d{2}|\d{11})(?!\w)"), "[cpf]"),
        // DDD + number with a separator ("11 98765-4321", "(11) 3333-4444", "+55 11 98765 4321"). A bare run of digits is
        // left alone (it is caught above when it has the length of a CPF or CNPJ), so ids and years are never masked.
        (Rx(@"(?<![\w-])(?:\+?55[\s-]?)?\(?\d{2}\)?[\s-]?9?\d{4}[\s-]\d{4}(?![\w-])|(?<![\w-])\(\d{2}\)\s?9?\d{4}-?\d{4}(?![\w-])"), "[telefone]"),
    ];

    // Relaxed: accents stay as they are, so a redacted document is not longer than the original because of escaping.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Masks sensitive data in plain text. Never throws: on a regex timeout the whole text is masked.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            var result = KeyValue.Replace(text, "$1[segredo]");
            foreach (var (rule, mask) in Rules)
                result = rule.Replace(result, mask);
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[texto omitido]";
        }
    }

    /// <summary>True when <see cref="Redact"/> would change the text.</summary>
    public static bool Contains(string? text) => !string.IsNullOrEmpty(text) && Redact(text) != text;

    /// <summary>
    /// Masks sensitive data inside the string values of a JSON document (numbers, property names and structure stay
    /// untouched, so ids and quantities are never mistaken for a phone). The input is returned as is when nothing
    /// changes; invalid JSON is masked as plain text.
    /// </summary>
    public static string? RedactJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Redact(json);
        }
        switch (root)
        {
            case null:
                return json;
            case JsonValue value when value.TryGetValue<string>(out var text):
                var masked = Redact(text);
                return masked == text ? json : JsonValue.Create(masked).ToJsonString(JsonOptions);
            default:
                return MaskChildren(root) ? root.ToJsonString(JsonOptions) : json;
        }
    }

    /// <summary>Replaces, in place, every string value under <paramref name="node"/> that has something to mask.</summary>
    private static bool MaskChildren(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                    changed |= MaskSlot(obj[key], replacement => obj[key] = replacement);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var index = i;
                    changed |= MaskSlot(array[i], replacement => array[index] = replacement);
                }
                break;
        }
        return changed;
    }

    private static bool MaskSlot(JsonNode? child, Action<JsonNode> replace)
    {
        if (child is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var masked = Redact(text);
            if (masked == text) return false;
            replace(JsonValue.Create(masked));
            return true;
        }
        return child is not null && MaskChildren(child);
    }
}
