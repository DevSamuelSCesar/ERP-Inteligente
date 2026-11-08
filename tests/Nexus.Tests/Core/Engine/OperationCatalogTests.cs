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
    [InlineData("create_zone", RiskLevel.Reversible, "location.manage", PlanOperation.CreateZone)]
    [InlineData("update_zone", RiskLevel.Reversible, "location.manage", PlanOperation.UpdateZone)]
    [InlineData("create_location_range", RiskLevel.Reversible, "location.manage", PlanOperation.CreateLocationRange)]
    [InlineData("update_location", RiskLevel.Reversible, "location.manage", PlanOperation.UpdateLocation)]
    [InlineData("update_product", RiskLevel.Reversible, "catalog.manage", PlanOperation.UpdateProduct)]
    [InlineData("create_supplier", RiskLevel.Reversible, "catalog.manage", PlanOperation.CreateSupplier)]
    [InlineData("create_customer", RiskLevel.Reversible, "catalog.manage", PlanOperation.CreateCustomer)]
    [InlineData("create_inbound_order", RiskLevel.Reversible, "order.manage", PlanOperation.CreateInboundOrder)]
    [InlineData("cancel_inbound_order", RiskLevel.Critical, "order.manage", PlanOperation.CancelInboundOrder)]
    [InlineData("create_outbound_order", RiskLevel.Reversible, "order.manage", PlanOperation.CreateOutboundOrder)]
    [InlineData("reserve_stock", RiskLevel.Reversible, "stock.reserve", PlanOperation.ReserveStock)]
    [InlineData("release_reservation", RiskLevel.Reversible, "stock.reserve", PlanOperation.ReleaseReservation)]
    [InlineData("pick_items", RiskLevel.Reversible, "stock.pick", PlanOperation.PickItems)]
    [InlineData("ship_order", RiskLevel.Critical, "stock.ship", PlanOperation.ShipOrder)]
    [InlineData("cancel_outbound_order", RiskLevel.Critical, "order.manage", PlanOperation.CancelOutboundOrder)]
    public void Operation_metadata(string name, RiskLevel risk, string permission, PlanOperation ui)
    {
        Assert.True(OperationCatalog.TryGet(name, out var def));
        Assert.Equal(risk, def.Risk);
        Assert.Equal(permission, def.Permission);
        Assert.Equal(ui, def.UiOperation);
        Assert.False(string.IsNullOrWhiteSpace(def.Label));
    }

    [Fact]
    public void Only_adjustment_shipping_and_cancellations_are_critical() =>
        Assert.Equal(["adjust_stock", "cancel_inbound_order", "ship_order", "cancel_outbound_order"], OperationCatalog.V1.Where(o => o.Risk == RiskLevel.Critical).Select(o => o.Name));

    [Fact]
    public void Catalog_has_twenty_operations_and_rejects_unknown()
    {
        Assert.Equal(20, OperationCatalog.V1.Count);
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
        Assert.Equal(["zone"], OperationCatalog.V1.Single(o => o.Name == "create_location_range").ReferenceFields);
        Assert.Equal(["code"], OperationCatalog.V1.Single(o => o.Name == "update_zone").ReferenceFields);
        Assert.Equal(["code"], OperationCatalog.V1.Single(o => o.Name == "update_location").ReferenceFields);
        Assert.Equal(["sku"], OperationCatalog.V1.Single(o => o.Name == "update_product").ReferenceFields);
        Assert.Equal(["order_number", "to"], OperationCatalog.V1.Single(o => o.Name == "pick_items").ReferenceFields);
        Assert.Equal(["order_number"], OperationCatalog.V1.Single(o => o.Name == "ship_order").ReferenceFields);
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
