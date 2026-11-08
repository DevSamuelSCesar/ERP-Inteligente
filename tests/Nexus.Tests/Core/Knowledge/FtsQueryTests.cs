using erp_inteligente.Core.Knowledge;

namespace Nexus.Tests.Core.Knowledge;

public sealed class FtsQueryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("* ( ) :")]
    [InlineData("a e o")]
    public void Build_ReturnsNullWithoutUsableTerms(string text) => Assert.Null(FtsQuery.Build(text));

    [Fact]
    public void Build_NullText_ReturnsNull() => Assert.Null(FtsQuery.Build(null));

    [Fact]
    public void Build_QuotesEveryTerm_AndJoinsWithOr() =>
        Assert.Equal("\"palete\"* OR \"zona\"", FtsQuery.Build("Palete zona"));

    [Theory]
    [InlineData("\"; DROP TABLE memories; --", "\"drop\" OR \"table\"* OR \"memories\"*")]
    [InlineData("zona*", "\"zona\"")]
    [InlineData("zona NEAR/3 doca", "\"zona\" OR \"near\" OR \"doca\"")]
    [InlineData("content:secreto", "\"content\"* OR \"secreto\"*")]
    [InlineData("(zona OR doca) AND NOT rua", "\"zona\" OR \"doca\" OR \"not\" OR \"rua\"")]
    [InlineData("^inicio -fim", "\"inicio\"* OR \"fim\"")]
    public void Build_NeverEmitsFtsSyntaxFromInput(string text, string expected) =>
        Assert.Equal(expected, FtsQuery.Build(text));

    [Fact]
    public void Build_UnicodeIsFolded() =>
        Assert.Equal("\"refrigeracao\"* OR \"acucar\"*", FtsQuery.Build("Refrigeração AÇÚCAR"));

    [Fact]
    public void Build_KeepsAtMostEightDistinctTerms()
    {
        var query = FtsQuery.Build("um1 dois2 tres3 quat4 cinc5 seis6 sete7 oito8 nove9 dez10 dois2")!;
        Assert.Equal(8, query.Split(" OR ").Length);
        Assert.DoesNotContain("nove9", query);
    }

    [Fact]
    public void Build_SplitsCodesLikeTheTokenizer() =>
        Assert.Equal("\"abc\" OR \"123\"", FtsQuery.Build("ABC-123"));

    [Fact]
    public void Terms_DropsStopWordsAndSingleChars() =>
        Assert.Equal(["zona", "frio"], FtsQuery.Terms("a zona de frio x"));
}
