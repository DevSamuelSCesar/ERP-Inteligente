using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace erp_inteligente.Core.Skills;

/// <summary>
/// The skills shipped with the app: <c>skills/&lt;name&gt;/SKILL.md</c> files embedded as resources named
/// <c>Nexus.Skills.*</c>. The key is the frontmatter <c>nome</c>, not the resource name.
/// </summary>
public static class BuiltInSkills
{
    private const string ResourcePrefix = "Nexus.Skills.";

    private static readonly Lazy<IReadOnlyList<SkillDocument>> Loaded = new(() => Load(typeof(BuiltInSkills).Assembly));

    /// <summary>All built-in skills sorted by name. Throws <see cref="InvalidOperationException"/> if one is invalid.</summary>
    public static IReadOnlyList<SkillDocument> All => Loaded.Value;

    /// <summary>Finds a built-in skill by exact name.</summary>
    public static SkillDocument? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Loads and validates the skills embedded in <paramref name="assembly"/>.</summary>
    internal static IReadOnlyList<SkillDocument> Load(Assembly assembly)
    {
        var result = new Dictionary<string, SkillDocument>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Built-in skill resource '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            var parsed = SkillDocument.Parse(reader.ReadToEnd());
            var issues = parsed.Issues.ToList();
            if (parsed.Document is not null) issues.AddRange(parsed.Document.Validate());
            if (issues.Count > 0)
                throw new InvalidOperationException(
                    $"Built-in skill '{resource}' is invalid: " + string.Join(" | ", issues.Select(i => $"{i.Field}: {i.Message}")));
            if (!result.TryAdd(parsed.Document!.Name, parsed.Document))
                throw new InvalidOperationException($"Built-in skill '{parsed.Document.Name}' is defined twice.");
        }
        return result.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}
