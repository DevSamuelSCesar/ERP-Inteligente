using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Safety;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// Feature 6, layer 1 of references/ARQUITETURA.md section 12.8: external content is scanned by the
/// <see cref="InjectionDetector"/> before it reaches the model. A suspicious block is still delivered (it is data the
/// operator may need), but marked with <c>alerta="instrucao_suspeita"</c>, audited as <c>ai.injection</c> (source,
/// row and rule; never the text) and reported to the operator with the result.
/// </summary>
public sealed partial class Planner
{
    internal const string SuspiciousAttribute = "alerta=\"instrucao_suspeita\"";
    internal const int MaxSuspiciousPerRequest = 10;

    /// <summary>Row fields that identify where a text came from, in order of preference.</summary>
    private static readonly string[] ReferenceFields = ["sku", "code", "lot_code", "order_number", "number", "location_code", "source", "id"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/#:-]{0,79}\z")]
    private static partial Regex SafeReference();

    /// <summary>
    /// The operator's own request: an attempt is audited (the motor still decides what is allowed), but the operator is
    /// not warned about their own words.
    /// </summary>
    private bool ScanRequest(long? aiUserId, UserSession user, string request)
    {
        if (InjectionDetector.Scan(request) is not { } match) return false;
        Append(aiUserId, user, "ai.injection", "pedido", AuditOutcome.Denied, Details("pedido", null, match.Code));
        return true;
    }

    /// <summary>Scans the string values of a tool result; returns the findings (already audited and kept in the state).</summary>
    private List<SuspiciousContent> ScanToolResult(long? aiUserId, UserSession user, RunState state, string source, string json)
    {
        var found = new List<SuspiciousContent>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return found;
        }
        if (root is not null) Walk(root, null, found, source);
        return Report(aiUserId, user, state, found);
    }

    /// <summary>Scans one text block of the automatic context (a memory list or a procedure chunk).</summary>
    private List<SuspiciousContent> ScanBlock(long? aiUserId, UserSession user, RunState state, string source, string? reference, string text)
    {
        var found = new List<SuspiciousContent>();
        if (InjectionDetector.Scan(text) is { } match)
            found.Add(new SuspiciousContent(source, SafeRef(reference), match.Code));
        return Report(aiUserId, user, state, found);
    }

    private static void Walk(JsonNode node, string? reference, List<SuspiciousContent> found, string source)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                // One finding per row: the first text field that matches names the rule.
                var here = ReferenceOf(obj) ?? reference;
                var flagged = false;
                foreach (var (_, child) in obj)
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (!flagged && InjectionDetector.Scan(text) is { } hit)
                        {
                            found.Add(new SuspiciousContent(source, here, hit.Code));
                            flagged = true;
                        }
                    }
                    else if (child is not null)
                        Walk(child, here, found, source);
                }
                break;
            }
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) Walk(child, reference, found, source);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (InjectionDetector.Scan(text) is { } match)
                    found.Add(new SuspiciousContent(source, reference, match.Code));
                break;
        }
    }

    private static string? ReferenceOf(JsonObject obj)
    {
        foreach (var field in ReferenceFields)
            if (obj[field] is JsonValue v)
            {
                if (v.TryGetValue<string>(out var text)) return SafeRef(text);
                if (v.TryGetValue<long>(out var number)) return field + ":" + number;
            }
        return null;
    }

    private static string? SafeRef(string? value) => value is not null && SafeReference().IsMatch(value) ? value : null;

    /// <summary>Keeps new findings (one per source and row) and audits each; caps the work per request.</summary>
    private List<SuspiciousContent> Report(long? aiUserId, UserSession user, RunState state, List<SuspiciousContent> found)
    {
        var added = new List<SuspiciousContent>();
        foreach (var item in found)
        {
            if (state.Suspicious.Any(s => s.Source == item.Source && s.Reference == item.Reference)) { added.Add(item); continue; }
            if (state.Suspicious.Count >= MaxSuspiciousPerRequest) { added.Add(item); continue; }
            state.Suspicious.Add(item);
            added.Add(item);
            Append(aiUserId, user, "ai.injection", Shorten(item.Source), AuditOutcome.Denied, Details(item.Source, item.Reference, item.Code));
        }
        return added;
    }

    private static string Shorten(string value) => value.Length > AuditEntry.MaxTargetLength ? value[..AuditEntry.MaxTargetLength] : value;

    private static string Details(string source, string? reference, string code) =>
        JsonSerializer.Serialize(new { code, source = Shorten(source), reference }, DetailsOptions);
}

