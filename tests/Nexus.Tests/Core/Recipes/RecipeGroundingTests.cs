using System.Text.Json.Nodes;
using erp_inteligente.Core.Recipes;

namespace Nexus.Tests.Core.Recipes;

/// <summary>
/// The check after the Recipe Matcher: every value comes from the request. The vectors are shared with the Python
/// reference (tools/finetune/grounding_cases.json, used by evaluate.grounded()).
/// </summary>
public sealed class RecipeGroundingTests
{
    private static JsonArray Cases()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "finetune", "grounding_cases.json");
            if (File.Exists(path)) return (JsonArray)JsonNode.Parse(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException("tools/finetune/grounding_cases.json");
    }

    [Fact]
    public void Shared_vectors()
    {
        var cases = Cases();
        Assert.True(cases.Count >= 10);
        foreach (var c in cases)
        {
            var request = c!["request"]!.GetValue<string>();
            var expected = c["ungrounded"]?.GetValue<string>();
            var grounded = RecipeGrounding.IsGrounded(request, (JsonObject)c["values"]!.DeepClone(), out var name);
            Assert.True(grounded == (expected is null), request);
            Assert.Equal(expected, name);
        }
    }

    [Fact]
    public void Tokens_fold_case_accents_separators_and_leading_zeros()
    {
        Assert.Equal(["doca", "1", "no", "a", "2", "1", "recepcao"], RecipeGrounding.Tokens("DOCA-01 no A-02-01, Recepção"));
        Assert.Equal(["0", "100"], RecipeGrounding.Tokens("000 0100"));
    }

    [Fact]
    public void Values_inside_lists_and_objects_are_checked_too()
    {
        var values = new JsonObject { ["skus"] = new JsonArray("ABC-123", "FER-010"), ["dest"] = new JsonObject { ["loc"] = "A-01-01" } };
        Assert.True(RecipeGrounding.IsGrounded("ABC-123 e FER-010 para A-01-01", values, out _));
        Assert.False(RecipeGrounding.IsGrounded("ABC-123 para A-01-01", values, out var name));
        Assert.Equal("skus", name);
    }
}
