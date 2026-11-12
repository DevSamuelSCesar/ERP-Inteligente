using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Eval;

/// <summary>A planning request with its answer key: the first proposal must contain each operation at least once.</summary>
public sealed record PlanningCase(string Id, string User, string Request, IReadOnlyList<string> Ops);

/// <summary>A question about knowledge/: one of <see cref="Sources"/> must be retrieved; the answer must contain the keywords.</summary>
public sealed record KnowledgeCase(string Id, string Question, IReadOnlyList<string> Sources, IReadOnlyList<string> Keywords);

/// <summary>A request outside the warehouse: the right outcome is no plan.</summary>
public sealed record OutOfScopeCase(string Id, string Request);

/// <summary>A proactive or no-right request; <see cref="Expect"/> is refusal, proposals or proposals_or_options.</summary>
public sealed record ProactiveCase(string Id, string User, string Request, string Expect);

/// <summary>The evaluation sets of references/IA.md, section 5, versioned in tools/eval.</summary>
public sealed record EvalSets(
    IReadOnlyList<PlanningCase> Planning,
    IReadOnlyList<KnowledgeCase> Knowledge,
    IReadOnlyList<OutOfScopeCase> OutOfScope,
    IReadOnlyList<ProactiveCase> Proactive)
{
    public static readonly string[] Expectations = ["refusal", "proposals", "proposals_or_options"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private sealed record PlanningFile(IReadOnlyList<PlanningCase> Cases);
    private sealed record KnowledgeFile(IReadOnlyList<KnowledgeCase> Cases);
    private sealed record ScopeFile(IReadOnlyList<OutOfScopeCase> OutOfScope, IReadOnlyList<ProactiveCase> Proactive);

    /// <summary>Reads planning.json, knowledge.json and scope.json from <paramref name="directory"/>.</summary>
    public static EvalSets Load(string directory)
    {
        T Read<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory, name)), Options)
                                  ?? throw new InvalidDataException(name + " is empty.");
        var planning = Read<PlanningFile>("planning.json");
        var knowledge = Read<KnowledgeFile>("knowledge.json");
        var scope = Read<ScopeFile>("scope.json");
        return new EvalSets(planning.Cases, knowledge.Cases, scope.OutOfScope, scope.Proactive);
    }

    /// <summary>Walks up from <paramref name="start"/> to the repository root (the folder with tools/eval/planning.json).</summary>
    public static string FindDirectory(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "eval");
            if (File.Exists(Path.Combine(candidate, "planning.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("tools/eval was not found above " + start);
    }
}
