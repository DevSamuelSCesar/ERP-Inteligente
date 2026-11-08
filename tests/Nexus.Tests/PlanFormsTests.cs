using erp_inteligente.ViewModels;

namespace Nexus.Tests;

public class PlanFormsTests
{
    [Theory]
    [InlineData("Q", new string[0], "Q-01-01")]
    [InlineData("A", new[] { "A-01-01", "A-01-02", "A-02-01" }, "A-02-02")]
    [InlineData("A", new[] { "A-01-01", "B-01-01" }, "A-01-02")]
    public void Next_location_code_follows_the_highest_aisle(string zone, string[] codes, string expected) =>
        Assert.Equal(expected, PlanForms.NextLocationCode(zone, codes));

    [Theory]
    [InlineData("Q", new string[0], 1)]
    [InlineData("A", new[] { "A-01-01", "A-02-05" }, 3)]
    [InlineData("A", new[] { "A-02-01" }, 1)]
    public void Next_free_aisle_is_the_first_unused(string zone, string[] codes, int expected) =>
        Assert.Equal(expected, PlanForms.NextFreeAisle(zone, codes));

    [Fact]
    public void Lines_are_parsed_one_item_per_line()
    {
        var lines = PlanForms.ParseLines("abc-123 10\nFER-010: 2\n\nLIM-200;5", out var error);

        Assert.Null(error);
        Assert.Equal(["ABC-123", "FER-010", "LIM-200"], lines!.Select(l => (string)l["sku"]!));
        Assert.Equal([10, 2, 5], lines!.Select(l => (int)l["qty"]!));
    }

    [Theory]
    [InlineData("ABC-123")]
    [InlineData("ABC-123 -1")]
    [InlineData("ABC-123 dez")]
    [InlineData("ABC-123 1 2")]
    [InlineData("   ")]
    public void Malformed_lines_are_refused_with_a_message(string text)
    {
        Assert.Null(PlanForms.ParseLines(text, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Codes_are_upper_cased_but_names_are_not()
    {
        Assert.Contains("zone", PlanForms.CodeFields);
        Assert.DoesNotContain("name", PlanForms.CodeFields);
    }
}
