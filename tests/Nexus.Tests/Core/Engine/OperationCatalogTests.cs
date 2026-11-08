using System.Linq;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Engine;
using erp_inteligente.Models;

namespace Nexus.Tests.Core.Engine;

public class OperationCatalogTests
{
    [Theory]
    [InlineData("receive_goods", RiskLevel.Reversible, "stock.receive", PlanOperation.ReceiveGoods)]
    [InlineData("move_stock", RiskLevel.Reversible, "stock.move", PlanOperation.MoveStock)]
    [InlineData("adjust_stock", RiskLevel.Critical, "stock.adjust", PlanOperation.AdjustStock)]
    [InlineData("create_product", RiskLevel.Reversible, "catalog.manage", PlanOperation.CreateProduct)]
    [InlineData("create_location", RiskLevel.Reversible, "location.manage", PlanOperation.CreateLocation)]
    public void Operation_metadata(string name, RiskLevel risk, string permission, PlanOperation ui)
    {
        Assert.True(OperationCatalog.TryGet(name, out var def));
        Assert.Equal(risk, def.Risk);
        Assert.Equal(permission, def.Permission);
        Assert.Equal(ui, def.UiOperation);
        Assert.False(string.IsNullOrWhiteSpace(def.Label));
    }

    [Fact]
    public void Adjust_stock_is_the_only_critical_operation() =>
        Assert.Equal(["adjust_stock"], OperationCatalog.V1.Where(o => o.Risk == RiskLevel.Critical).Select(o => o.Name));

    [Fact]
    public void Catalog_has_five_operations_and_rejects_unknown()
    {
        Assert.Equal(5, OperationCatalog.V1.Count);
        Assert.False(OperationCatalog.TryGet("drop_table", out _));
        Assert.False(OperationCatalog.TryGet("MOVE_STOCK", out _));
        Assert.Throws<ArgumentException>(() => OperationCatalog.SchemaJson("nope"));
    }

    [Fact]
    public void Reference_fields_match_the_schemas()
    {
        foreach (var op in OperationCatalog.V1)
        {
            var props = (JsonObject)JsonNode.Parse(OperationCatalog.SchemaJson(op.Name))!["properties"]!;
            var anyOf = props.Where(p => p.Value is JsonObject o && o.ContainsKey("anyOf")).Select(p => p.Key).Order().ToArray();
            Assert.Equal(op.ReferenceFields.Order().ToArray(), anyOf);
        }
        Assert.Equal(["sku", "from", "to"], OperationCatalog.V1.Single(o => o.Name == "move_stock").ReferenceFields);
    }

    [Fact]
    public void SchemaJson_returns_the_embedded_text()
    {
        foreach (var op in OperationCatalog.V1)
        {
            var text = OperationCatalog.SchemaJson(op.Name);
            var node = JsonNode.Parse(text)!;
            Assert.Equal(op.Name, (string?)node["title"]);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string?)node["$schema"]);
            Assert.False((bool)node["additionalProperties"]!.AsValue().GetValue<bool>());
        }
    }

    [Fact]
    public void Risk_helpers()
    {
        Assert.Equal("read", RiskLevel.Read.ToDbValue());
        Assert.Equal("reversible", RiskLevel.Reversible.ToDbValue());
        Assert.Equal("critical", RiskLevel.Critical.ToDbValue());
        Assert.Equal(RiskLevel.Critical, RiskLevels.FromDbValue("critical"));
        Assert.Throws<ArgumentException>(() => RiskLevels.FromDbValue("Critical"));
        Assert.Equal(RiskLevel.Read, RiskLevels.Max());
        Assert.Equal(RiskLevel.Critical, RiskLevels.Max(RiskLevel.Reversible, RiskLevel.Critical, RiskLevel.Read));
        Assert.Equal(RiskLevel.Reversible, RiskLevels.Max(new List<RiskLevel> { RiskLevel.Read, RiskLevel.Reversible }));
    }
}
