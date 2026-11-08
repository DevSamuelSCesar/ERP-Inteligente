using System.Text;
using erp_inteligente.Core.Knowledge;

namespace Nexus.Tests.Core.Knowledge;

public sealed class KnowledgeChunkerTests
{
    private const string Doc =
        "# POP-99 Teste\n\n> Documento fictício\n\n## Objetivo\nFazer o teste.\n\n## Passos\n1. Um\n2. Dois\n\n## Passos\nRepetido.\n";

    [Fact]
    public void Chunk_OnePerSection_WithTitleAndHeadingPrefix()
    {
        var chunks = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/99-teste.md", Doc));
        Assert.Equal(["Objetivo", "Passos", "Passos (2)"], chunks.Select(c => c.Heading));
        Assert.All(chunks, c => Assert.Equal("knowledge/99-teste.md", c.SourcePath));
        Assert.StartsWith("POP-99 Teste - Objetivo\n\nFazer o teste.", chunks[0].Content);
        Assert.Equal(64, chunks[0].Sha256.Length);
        Assert.Equal((chunks[0].Content.Length + 3) / 4, chunks[0].TokenCount);
    }

    [Fact]
    public void Chunk_IntroOnlyWhenNonTrivial()
    {
        var trivial = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/a.md", Doc));
        Assert.DoesNotContain(trivial, c => c.Heading == "Introdução");

        var intro = "# T\n\n" + new string('x', 30) + " texto de apresentação do documento que passa do mínimo.\n\n## S\nCorpo.\n";
        var chunks = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/b.md", intro));
        Assert.Equal(["Introdução", "S"], chunks.Select(c => c.Heading));
    }

    [Fact]
    public void Chunk_HeadingInsideCodeFence_IsNotASection()
    {
        var md = "# T\n\n## A\n```\n## nao\n```\nfim\n";
        Assert.Equal(["A"], KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/c.md", md)).Select(c => c.Heading));
    }

    [Fact]
    public void Chunk_LongSection_SplitsWithOverlapAndUniqueHeadings()
    {
        var body = new StringBuilder();
        for (var i = 1; i <= 120; i++) body.Append($"Linha {i:000} com texto de enchimento suficiente para ocupar espaço.\n");
        var md = "# Longo\n\n## Grande\n" + body;
        var chunks = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/d.md", md));

        Assert.True(chunks.Count >= 3);
        Assert.Equal("Grande", chunks[0].Heading);
        Assert.Equal("Grande (2)", chunks[1].Heading);
        Assert.Equal(chunks.Count, chunks.Select(c => c.Heading).Distinct().Count());
        Assert.All(chunks, c => Assert.True(c.TokenCount <= KnowledgeChunker.MaxTokens + 5, $"{c.Heading}: {c.TokenCount}"));

        // Overlap: the last line of a piece reappears at the start of the next one; no line is lost.
        var lastLine = chunks[0].Content.TrimEnd().Split('\n')[^1];
        Assert.Contains(lastLine, chunks[1].Content);
        var all = string.Join("\n", chunks.Select(c => c.Content));
        for (var i = 1; i <= 120; i++) Assert.Contains($"Linha {i:000} ", all);
    }

    [Fact]
    public void Chunk_SingleHugeLine_TerminatesAndCuts()
    {
        var md = "# T\n\n## H\n" + string.Join(' ', Enumerable.Repeat("palavra", 1000)) + "\n";
        var chunks = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/e.md", md));
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void Chunk_IsDeterministic()
    {
        var a = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/f.md", Doc));
        var b = KnowledgeChunker.Chunk(new KnowledgeSource("knowledge/f.md", Doc));
        Assert.Equal(a.Select(c => c.Sha256), b.Select(c => c.Sha256));
    }
}
