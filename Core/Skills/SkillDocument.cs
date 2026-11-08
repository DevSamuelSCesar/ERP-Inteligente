using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Engine;

namespace erp_inteligente.Core.Skills;

/// <summary>
/// A skill in the SKILL.md format: a frontmatter (<c>nome</c>, <c>descricao</c>, <c>operacoes</c>) between two
/// <c>---</c> lines followed by a Markdown body. Only the first two <c>---</c> lines delimit the frontmatter; a
/// <c>---</c> line inside the body is kept as is. The parser is strict and hand-written (no YAML library).
/// </summary>
public sealed partial record SkillDocument(string Name, string Description, IReadOnlyList<string> Operations, string Body)
{
    public const int MaxInputLength = 16 * 1024;
    public const int MinDescriptionLength = 20;
    public const int MaxDescriptionLength = 300;
    public const int MaxOperations = 20;
    public const int MaxBodyLength = 12000;

    private const string Delimiter = "---";

    [GeneratedRegex(@"^[a-z][a-z_]{1,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>True when <paramref name="name"/> is a valid skill name (same rule as the plan <c>skill</c> field).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Parses SKILL.md text. All problems found are returned; the document is set only when there are none.</summary>
    public static SkillParseResult Parse(string? text)
    {
        if (text is null) return Fail("corpo", "O texto da skill está vazio.");
        if (text.Length > MaxInputLength)
            return Fail("corpo", $"O texto da skill é grande demais (máximo {MaxInputLength / 1024} KB).");

        var lines = SkillText.Normalize(text).Split('\n');
        if (lines.Length == 0 || lines[0] != Delimiter)
            return Fail("corpo", "A skill deve começar com uma linha '---' (cabeçalho).");

        var issues = new List<SkillIssue>();
        string? name = null, description = null;
        IReadOnlyList<string>? operations = null;
        var closing = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line == Delimiter) { closing = i; break; }
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                issues.Add(new SkillIssue("corpo", $"Linha {i + 1} do cabeçalho inválida: use 'chave: valor' em uma única linha."));
                continue;
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "nome" when name is null:
                    name = SkillText.NormalizeLine(StripQuotes(value));
                    break;
                case "descricao" when description is null:
                    description = SkillText.NormalizeLine(StripQuotes(value));
                    break;
                case "operacoes" when operations is null:
                    operations = ParseOperations(value, issues);
                    break;
                case "nome" or "descricao" or "operacoes":
                    issues.Add(new SkillIssue(key, $"O campo '{key}' aparece mais de uma vez."));
                    break;
                default:
                    issues.Add(new SkillIssue("corpo", $"Campo desconhecido no cabeçalho: '{Clip(key)}'."));
                    break;
            }
        }

        if (closing < 0) issues.Add(new SkillIssue("corpo", "O cabeçalho não termina com uma linha '---'."));
        if (name is null) issues.Add(new SkillIssue("nome", "O campo 'nome' é obrigatório."));
        if (description is null) issues.Add(new SkillIssue("descricao", "O campo 'descricao' é obrigatório."));
        if (issues.Count > 0) return new SkillParseResult(null, issues);

        var body = string.Join('\n', lines[(closing + 1)..]).Trim('\n');
        return new SkillParseResult(new SkillDocument(name!, description!, operations ?? [], body), []);
    }

    /// <summary>The canonical SKILL.md text; <see cref="Parse"/> of it returns an equal document.</summary>
    public string Serialize()
    {
        var builder = new StringBuilder();
        builder.Append(Delimiter).Append('\n');
        builder.Append("nome: ").Append(Name).Append('\n');
        var description = Description.Length > 0 && Description[0] is '"' or '\'' ? "\"" + Description + "\"" : Description;
        builder.Append("descricao: ").Append(description).Append('\n');
        builder.Append("operacoes: [").Append(string.Join(", ", Operations)).Append("]\n");
        builder.Append(Delimiter).Append('\n');
        builder.Append(Body).Append('\n');
        return builder.ToString();
    }

    /// <summary>A copy with every field normalized (see <see cref="SkillText"/>).</summary>
    public SkillDocument Normalized() => new(
        SkillText.NormalizeLine(Name),
        SkillText.NormalizeLine(Description),
        Operations.Select(o => SkillText.NormalizeLine(o)).ToList(),
        SkillText.Normalize(Body));

    /// <summary>Checks the business rules. An empty list means the skill is valid.</summary>
    public IReadOnlyList<SkillIssue> Validate()
    {
        var issues = new List<SkillIssue>();
        var doc = Normalized();

        if (!IsValidName(doc.Name))
            issues.Add(new SkillIssue("nome", "O nome deve ter de 2 a 40 caracteres, só letras minúsculas e sublinhado, começando por letra."));

        if (doc.Description.Length is < MinDescriptionLength or > MaxDescriptionLength)
            issues.Add(new SkillIssue("descricao", $"A descrição deve ter de {MinDescriptionLength} a {MaxDescriptionLength} caracteres."));

        if (doc.Operations.Count > MaxOperations)
            issues.Add(new SkillIssue("operacoes", $"Informe no máximo {MaxOperations} operações."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in doc.Operations)
        {
            if (!seen.Add(op))
                issues.Add(new SkillIssue("operacoes", $"A operação '{Clip(op)}' está repetida."));
            else if (!OperationCatalog.TryGet(op, out _))
                issues.Add(new SkillIssue("operacoes", $"A operação '{Clip(op)}' não existe no catálogo."));
        }

        if (doc.Body.Length is 0 or > MaxBodyLength)
            issues.Add(new SkillIssue("corpo", $"O corpo deve ter de 1 a {MaxBodyLength} caracteres."));
        else
            ValidateExamples(doc, seen, issues);

        return issues;
    }

    private static void ValidateExamples(SkillDocument doc, HashSet<string> declared, List<SkillIssue> issues)
    {
        var number = 0;
        var blocks = ExtractJsonBlocks(doc.Body, out var unclosed);
        foreach (var json in blocks)
        {
            number++;
            var parsed = DslParser.Parse(json);
            if (parsed.Document is null)
            {
                foreach (var error in parsed.Errors.Take(5))
                    issues.Add(new SkillIssue("corpo", $"Exemplo {number}: {error.Message}"));
                continue;
            }
            foreach (var op in parsed.Document.Steps.Select(s => s.Op).Distinct(StringComparer.Ordinal))
                if (!declared.Contains(op))
                    issues.Add(new SkillIssue("corpo", $"Exemplo {number}: a operação '{Clip(op)}' não está listada em operacoes."));
        }
        if (unclosed)
            issues.Add(new SkillIssue("corpo", "Há um bloco de exemplo ```json sem fechamento."));
    }

    /// <summary>Contents of the fenced blocks whose info string is exactly <c>json</c>.</summary>
    internal static List<string> ExtractJsonBlocks(string body, out bool unclosed)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        var inJson = false;
        var inOther = false;
        unclosed = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (inJson)
            {
                if (line == "```") { blocks.Add(string.Join('\n', current)); current.Clear(); inJson = false; }
                else current.Add(raw);
            }
            else if (inOther)
            {
                if (line == "```") inOther = false;
            }
            else if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (line[3..].Trim() == "json") inJson = true; else inOther = true;
            }
        }
        unclosed = inJson;
        return blocks;
    }

    private static IReadOnlyList<string> ParseOperations(string value, List<SkillIssue> issues)
    {
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            issues.Add(new SkillIssue("operacoes", "O campo 'operacoes' deve ser uma lista entre colchetes, como [receive_goods, move_stock]."));
            return [];
        }
        var inner = value[1..^1].Trim();
        if (inner.Length == 0) return [];
        var items = inner.Split(',').Select(s => StripQuotes(s.Trim())).ToList();
        if (items.Any(i => i.Length == 0))
            issues.Add(new SkillIssue("operacoes", "A lista 'operacoes' tem um item vazio."));
        return items.Where(i => i.Length > 0).ToList();
    }

    private static string StripQuotes(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;

    private static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "...";

    private static SkillParseResult Fail(string field, string message) =>
        new(null, [new SkillIssue(field, message)]);
}
