using erp_inteligente.Core.Skills;

namespace Nexus.Tests.Core.Skills;

public class SkillDocumentTests
{
    internal const string ValidExample =
        "{\"dsl_version\":\"1.0\",\"intent\":\"receive\",\"steps\":[{\"op\":\"create_zone\",\"args\":{\"code\":\"Z9\",\"name\":\"Zona nove\"}}]}";

    internal static string Text(string name = "recebimento", string? description = null, string ops = "[receive_goods, create_inbound_order]", string body = "# Recebimento\n\nPassos.") =>
        $"---\nnome: {name}\ndescricao: {description ?? "Use ao receber mercadoria de fornecedor: confere NF e guarda."}\noperacoes: {ops}\n---\n{body}\n";

    [Fact]
    public void Parse_ValidText_ReturnsDocument()
    {
        var result = SkillDocument.Parse(Text());
        Assert.True(result.Succeeded);
        var doc = result.Document!;
        Assert.Equal("recebimento", doc.Name);
        Assert.Equal("Use ao receber mercadoria de fornecedor: confere NF e guarda.", doc.Description);
        Assert.Equal(["receive_goods", "create_inbound_order"], doc.Operations);
        Assert.Equal("# Recebimento\n\nPassos.", doc.Body);
        Assert.Empty(doc.Validate());
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var doc = SkillDocument.Parse(Text(body: "# T\n\n---\n\ntexto")).Document!;
        var again = SkillDocument.Parse(doc.Serialize()).Document!;
        Assert.Equal(doc.Name, again.Name);
        Assert.Equal(doc.Description, again.Description);
        Assert.Equal(doc.Operations, again.Operations);
        Assert.Equal(doc.Body, again.Body);
        Assert.Equal(doc.Serialize(), again.Serialize());
    }

    [Fact]
    public void Serialize_RoundTripsDescriptionStartingWithQuote()
    {
        var doc = new SkillDocument("abc", "\"Citado\" no começo da descrição, ok", [], "corpo");
        Assert.Equal(doc.Description, SkillDocument.Parse(doc.Serialize()).Document!.Description);
    }

    [Fact]
    public void Parse_EmptyOperationsAndMissingOperations()
    {
        Assert.Empty(SkillDocument.Parse(Text(ops: "[]")).Document!.Operations);
        var noOps = "---\nnome: abc\ndescricao: Uma descrição com mais de vinte caracteres\n---\ncorpo\n";
        Assert.Empty(SkillDocument.Parse(noOps).Document!.Operations);
    }

    [Fact]
    public void Parse_StripsQuotes_AndKeepsColonsInDescription()
    {
        var text = "---\nnome: \"abc\"\ndescricao: 'Use quando: algo acontecer, sempre'\n---\ncorpo";
        var doc = SkillDocument.Parse(text).Document!;
        Assert.Equal("abc", doc.Name);
        Assert.Equal("Use quando: algo acontecer, sempre", doc.Description);
    }

    [Fact]
    public void Parse_AcceptsCrLf()
    {
        var doc = SkillDocument.Parse(Text().Replace("\n", "\r\n")).Document!;
        Assert.Equal("recebimento", doc.Name);
        Assert.DoesNotContain('\r', doc.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nome: abc\n")]
    [InlineData("\n---\nnome: abc\n---\nx")]
    public void Parse_RejectsMissingFrontmatter(string text) =>
        Assert.False(SkillDocument.Parse(text).Succeeded);

    [Fact]
    public void Parse_RejectsNull() => Assert.False(SkillDocument.Parse(null).Succeeded);

    [Fact]
    public void Parse_RejectsMissingClosingDelimiter() =>
        Assert.False(SkillDocument.Parse("---\nnome: abc\ndescricao: Uma descrição com mais de vinte caracteres\ncorpo").Succeeded);

    [Fact]
    public void Parse_RejectsUnknownKey()
    {
        var result = SkillDocument.Parse(Text().Replace("operacoes:", "extra: 1\noperacoes:"));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Message.Contains("desconhecido"));
    }

    [Fact]
    public void Parse_RejectsDuplicateKey() =>
        Assert.False(SkillDocument.Parse(Text().Replace("operacoes:", "nome: outro\noperacoes:")).Succeeded);

    [Fact]
    public void Parse_RejectsMultilineValue() =>
        Assert.False(SkillDocument.Parse("---\nnome: abc\ndescricao: primeira linha\n continuação sem chave\n---\nx").Succeeded);

    [Fact]
    public void Parse_RejectsMissingRequiredKeys()
    {
        var result = SkillDocument.Parse("---\noperacoes: []\n---\nx");
        Assert.Contains(result.Issues, i => i.Field == "nome");
        Assert.Contains(result.Issues, i => i.Field == "descricao");
    }

    [Theory]
    [InlineData("receive_goods")]
    [InlineData("[receive_goods")]
    [InlineData("[receive_goods,,move_stock]")]
    public void Parse_RejectsBadOperationsList(string ops) =>
        Assert.False(SkillDocument.Parse(Text(ops: ops)).Succeeded);

    [Fact]
    public void Parse_RejectsTooLargeInput() =>
        Assert.False(SkillDocument.Parse(Text(body: new string('a', 17 * 1024))).Succeeded);

    [Fact]
    public void Parse_KeepsDelimiterInsideBody()
    {
        var doc = SkillDocument.Parse(Text(body: "Antes\n---\nDepois\n---\nFim")).Document!;
        Assert.Equal("Antes\n---\nDepois\n---\nFim", doc.Body);
    }

    [Fact]
    public void Parse_StripsZeroWidthBidiAndControlChars()
    {
        var body = "Oi​ mundo‮\u0007 fim\ttab";
        var doc = SkillDocument.Parse(Text(body: body)).Document!;
        Assert.Equal("Oi mundo fim\ttab", doc.Body);
        var name = SkillDocument.Parse(Text(name: "rece​bimento")).Document!;
        Assert.Equal("recebimento", name.Name);
    }

    [Fact]
    public void Normalize_CollapsesWhitespaceInLineValues()
    {
        Assert.Equal("a b c", SkillText.NormalizeLine("  a \t b\n c  "));
        Assert.Equal("a\nb", SkillText.Normalize("a  \r\nb \r\n\r\n"));
    }

    [Theory]
    [InlineData("Recebimento", false)]
    [InlineData("a", false)]
    [InlineData("rec-1", false)]
    [InlineData("rec1", false)]
    [InlineData("1ab", false)]
    [InlineData("ab", true)]
    [InlineData("pick_up", true)]
    public void Validate_Name(string name, bool ok)
    {
        var doc = new SkillDocument(name, "Descrição com mais de vinte caracteres", [], "corpo");
        Assert.Equal(ok, !doc.Validate().Any(i => i.Field == "nome"));
    }

    [Fact]
    public void Validate_NameLength()
    {
        var d = (string n) => new SkillDocument(n, "Descrição com mais de vinte caracteres", [], "c").Validate().Any(i => i.Field == "nome");
        Assert.False(d(new string('a', 40)));
        Assert.True(d(new string('a', 41)));
    }

    [Fact]
    public void Validate_DescriptionLength()
    {
        var d = (int n) => new SkillDocument("abc", new string('x', n), [], "c").Validate().Any(i => i.Field == "descricao");
        Assert.True(d(19));
        Assert.False(d(20));
        Assert.False(d(300));
        Assert.True(d(301));
    }

    [Fact]
    public void Validate_BodyLength()
    {
        var d = (int n) => new SkillDocument("abc", "Descrição com mais de vinte caracteres", [], new string('x', n)).Validate().Any(i => i.Field == "corpo");
        Assert.True(d(0));
        Assert.False(d(12000));
        Assert.True(d(12001));
    }

    [Fact]
    public void Validate_Operations()
    {
        SkillDocument Make(params string[] ops) => new("abc", "Descrição com mais de vinte caracteres", ops, "corpo");
        Assert.Contains(Make("nao_existe").Validate(), i => i.Field == "operacoes");
        Assert.Contains(Make("move_stock", "move_stock").Validate(), i => i.Field == "operacoes");
        Assert.Empty(Make("move_stock", "receive_goods").Validate());
        var twentyOne = erp_inteligente.Core.Engine.OperationCatalog.V1.Select(o => o.Name).Take(20).Append("x_y").ToArray();
        Assert.Contains(Make(twentyOne).Validate(), i => i.Field == "operacoes" && i.Message.Contains("20"));
        var twenty = erp_inteligente.Core.Engine.OperationCatalog.V1.Select(o => o.Name).Take(20).ToArray();
        Assert.Empty(Make(twenty).Validate());
    }

    [Fact]
    public void Validate_ExampleValid_Passes()
    {
        var body = $"# T\n\n## Exemplo de plano\n```json\n{ValidExample}\n```\n";
        var doc = new SkillDocument("abc", "Descrição com mais de vinte caracteres", ["create_zone"], body);
        Assert.Empty(doc.Validate());
    }

    [Fact]
    public void Validate_ExampleInvalidJson_Reported()
    {
        var doc = new SkillDocument("abc", "Descrição com mais de vinte caracteres", [], "```json\n{nao e json\n```");
        var issue = Assert.Single(doc.Validate());
        Assert.Equal("corpo", issue.Field);
        Assert.StartsWith("Exemplo 1:", issue.Message);
    }

    [Fact]
    public void Validate_ExampleOpNotDeclared_Reported()
    {
        var doc = new SkillDocument("abc", "Descrição com mais de vinte caracteres", ["move_stock"], $"```json\n{ValidExample}\n```");
        Assert.Contains(doc.Validate(), i => i.Message.Contains("create_zone") && i.Message.Contains("operacoes"));
    }

    [Fact]
    public void Validate_NonJsonFencesAreIgnored_AndUnclosedIsReported()
    {
        var ok = new SkillDocument("abc", "Descrição com mais de vinte caracteres", [], "```sql\nSELECT 1\n```\n```text\n{x\n```");
        Assert.Empty(ok.Validate());
        var bad = new SkillDocument("abc", "Descrição com mais de vinte caracteres", [], "```json\n{}");
        Assert.NotEmpty(bad.Validate());
    }
}
