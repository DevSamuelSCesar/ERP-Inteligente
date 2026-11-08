using System;
using System.Collections.Generic;
using System.Linq;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;

namespace erp_inteligente.Core.Skills;

/// <summary>
/// Read side of the skills for the LLM. Database skills are read through the AI read-only connection and the
/// <c>v_ai_skills</c> view only; an active database skill replaces the built-in one with the same name.
/// </summary>
public sealed class SkillCatalog
{
    public const int DefaultMaxEntries = 40;

    private readonly NexusDatabase _database;
    private readonly IReadOnlyList<SkillDocument> _builtIns;

    /// <param name="builtIns">Built-in skills; <see cref="BuiltInSkills.All"/> when null.</param>
    public SkillCatalog(NexusDatabase database, IReadOnlyList<SkillDocument>? builtIns = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _builtIns = builtIns ?? BuiltInSkills.All;
    }

    /// <summary>
    /// Active skills usable with <paramref name="rights"/>: those without operations or with at least one operation
    /// whose permission is in the rights. Sorted by name, at most <paramref name="max"/> entries, no body.
    /// </summary>
    public IReadOnlyList<SkillIndexEntry> Index(IReadOnlySet<string> rights, int max = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(rights);
        if (max < 1) return [];

        var byName = _builtIns.ToDictionary(
            b => b.Name, b => new SkillIndexEntry(b.Name, b.Description, b.Operations, 1), StringComparer.Ordinal);

        using (var connection = _database.OpenAiReadOnly())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, description, operations_json, version FROM v_ai_skills";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var operations = SkillJson.ReadOperations(reader.GetString(2));
                var name = reader.GetString(0);
                if (operations is null || !SkillDocument.IsValidName(name)) continue;
                byName[name] = new SkillIndexEntry(name, reader.GetString(1), operations, (int)reader.GetInt64(3));
            }
        }

        return byName.Values.Where(e => IsUsable(e.Operations, rights))
            .OrderBy(e => e.Name, StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>
    /// Loads one active skill with its body (database first, then built-in). Returns null for an invalid name
    /// (without querying), an unknown or archived skill, or, when <paramref name="rights"/> is given, a skill the
    /// rights do not allow.
    /// </summary>
    public SkillContent? Load(string name, IReadOnlySet<string>? rights = null)
    {
        if (!SkillDocument.IsValidName(name)) return null;

        SkillContent? found = null;
        using (var connection = _database.OpenAiReadOnly())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, description, operations_json, body, version FROM v_ai_skills WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var operations = SkillJson.ReadOperations(reader.GetString(2));
                if (operations is not null)
                    found = new SkillContent(reader.GetString(0), reader.GetString(1), operations, reader.GetString(3), (int)reader.GetInt64(4));
            }
        }

        if (found is null)
        {
            var builtIn = _builtIns.FirstOrDefault(b => b.Name == name);
            if (builtIn is not null)
                found = new SkillContent(builtIn.Name, builtIn.Description, builtIn.Operations, builtIn.Body, 1);
        }
        return found is not null && (rights is null || IsUsable(found.Operations, rights)) ? found : null;
    }

    private static bool IsUsable(IReadOnlyList<string> operations, IReadOnlySet<string> rights) =>
        operations.Count == 0
        || operations.Any(op => OperationCatalog.TryGet(op, out var definition) && rights.Contains(definition.Permission));
}
