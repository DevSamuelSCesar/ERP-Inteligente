using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>Deterministic highlighting of the Nexus answers (references/UI.md, section 4.2).</summary>
public class NexusTextTests
{
    private static (string Text, NexusTextKind Kind)[] Highlights(string text, params string[] products) =>
        NexusText.Parse(text, products).Where(s => s.Kind != NexusTextKind.Plain).Select(s => (s.Text, s.Kind)).ToArray();

    [Fact]
    public void Recognizes_every_entity_kind()
    {
        var found = Highlights(
            "Movi 40 cx do ABC-123 (Caixa de parafusos 4mm) de DOCA-01 para A-02-01 na zona A; pedido PED-FICT-5001, plano 12.",
            "Caixa de parafusos 4mm");

        Assert.Equal(
        [
            ("40 cx", NexusTextKind.Quantity), ("ABC-123", NexusTextKind.Sku), ("Caixa de parafusos 4mm", NexusTextKind.Product),
            ("DOCA-01", NexusTextKind.Location), ("A-02-01", NexusTextKind.Location), ("A", NexusTextKind.Zone),
            ("PED-FICT-5001", NexusTextKind.Order), ("plano 12", NexusTextKind.Plan),
        ], found);
    }

    [Fact]
    public void Segments_rebuild_the_original_text()
    {
        const string text = "Zona Q criada com Q-01-01 a Q-01-05; 10 un de FER-010 reservadas para NF-FICT-000102.";

        Assert.Equal(text, string.Concat(NexusText.Parse(text).Select(s => s.Text)));
    }

    [Theory]
    [InlineData("ABC-1234 não é um SKU")]
    [InlineData("xABC-123 colado")]
    [InlineData("a zona de conforto")]
    [InlineData("versão 1.0 e 2026-11-08")]
    public void Ignores_look_alikes(string text) => Assert.Empty(Highlights(text));

    [Fact]
    public void Longest_product_name_wins_and_case_is_ignored()
    {
        var found = Highlights("o KIT DE BROCAS 15 PEÇAS chegou", "Kit de brocas", "Kit de brocas 15 peças");

        Assert.Equal([("KIT DE BROCAS 15 PEÇAS", NexusTextKind.Product)], found);
    }

    [Fact]
    public void Markdown_and_markup_stay_plain_text()
    {
        const string text = "**ABC-123** <b>x</b> [link](http://exemplo) `A-01-01`";

        var segments = NexusText.Parse(text);

        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
        Assert.Contains(segments, s => s.Text.Contains("**") && s.Kind == NexusTextKind.Plain);
    }

    [Fact]
    public void Very_long_answers_are_not_scanned()
    {
        var text = string.Concat(Enumerable.Repeat("ABC-123 ", 600));

        Assert.Single(NexusText.Parse(text));
    }

    [Fact]
    public void Empty_text_has_no_segments() => Assert.Empty(NexusText.Parse(""));
}
