using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace erp_inteligente.Core.Skills;

/// <summary>Reads and writes the <c>operations_json</c> column (a JSON array of operation names).</summary>
internal static class SkillJson
{
    public static string WriteOperations(IReadOnlyList<string> operations) => JsonSerializer.Serialize(operations);

    /// <summary>Parses the column; returns null when it is not a JSON array of strings.</summary>
    public static IReadOnlyList<string>? ReadOperations(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 4000) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                list.Add(item.GetString()!);
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string DbValue(this SkillOrigin origin) => origin switch
    {
        SkillOrigin.User => "user",
        SkillOrigin.Ai => "ai",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Only user and ai skills are stored."),
    };

    public static SkillOrigin OriginFromDb(string value) => value == "ai" ? SkillOrigin.Ai : SkillOrigin.User;
}
