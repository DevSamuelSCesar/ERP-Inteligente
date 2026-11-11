using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Safety;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Safety;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("cpf 123.456.789-09 do João", "cpf [cpf] do João")]
    [InlineData("cpf 12345678909", "cpf [cpf]")]
    [InlineData("cnpj 12.345.678/0001-90", "cnpj [cnpj]")]
    [InlineData("mail joao@exemplo.com.br", "mail [email]")]
    [InlineData("ligue (11) 98765-4321", "ligue [telefone]")]
    [InlineData("ligue 11 98765-4321", "ligue [telefone]")]
    [InlineData("chave sk-abcdefghijklmnop1234", "chave [chave]")]
    [InlineData("Authorization: Bearer abc.def.ghi123", "Authorization: [token]")]
    [InlineData("senha=demo123 e password: x", "senha=[segredo] e password: [segredo]")]
    public void Masks_sensitive_data(string text, string expected)
    {
        Assert.Equal(expected, SensitiveDataRedactor.Redact(text));
    }

    [Theory]
    [InlineData("ABC-123 no A-01-01, lote L200-2611, 80 caixas")]
    [InlineData("2026-11-07T12:00:00Z")]
    [InlineData("hash 0fccc1391146c1f5ff1a34c04e878beca6d18960c532138eec326816eeb5b364")]
    [InlineData("plano 12345")]
    [InlineData("tokens: 60000")]
    public void Leaves_ordinary_text_alone(string text)
    {
        Assert.Equal(text, SensitiveDataRedactor.Redact(text));
    }

    [Fact]
    public void Json_masks_only_string_values_and_returns_unchanged_input_as_is()
    {
        const string clean = """{"code":"ok","plan_id":12345678901,"args":"{\"sku\":\"ABC-123\"}"}""";
        Assert.Same(clean, SensitiveDataRedactor.RedactJson(clean));

        var masked = SensitiveDataRedactor.RedactJson("""{"code":"ok","id":12345678901,"args":{"query":"cpf 123.456.789-09","list":["a@b.com"]}}""");
        Assert.Equal("""{"code":"ok","id":12345678901,"args":{"query":"cpf [cpf]","list":["[email]"]}}""", masked);
    }

    [Fact]
    public void Audit_rows_are_masked_before_hashing_and_the_chain_stays_valid()
    {
        using var f = new PolicyTestFixture();
        var id = f.Audit.Append(new AuditEntry(AuditActorType.System, null, null, "test.redaction", "user:joao@exemplo.com",
            AuditOutcome.Allowed, """{"args":"{\"query\":\"cpf 123.456.789-09 tel (11) 98765-4321\"}"}"""));

        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT target, details_json FROM audit_log WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("user:[email]", reader.GetString(0));
            Assert.DoesNotContain("123.456", reader.GetString(1));
            Assert.DoesNotContain("98765", reader.GetString(1));
            Assert.Contains("[cpf]", reader.GetString(1));
        }
        using var check = f.Open();
        Assert.True(AuditVerifier.Verify(check).IsValid);
    }
}
