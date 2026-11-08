using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;
using static erp_inteligente.Core.Engine.StructureStore;

namespace erp_inteligente.Core.Engine;

/// <summary>create_zone: adds a zone to the session warehouse; the code is unique per warehouse.</summary>
internal sealed class CreateZoneHandler : IOperationHandler
{
    public string Op => "create_zone";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var code = Str(args, "code");
        if (FindZone(ctx, code) is not null)
            errors.Add(Error(step, "code", "business.duplicate_zone", $"a zona {code} já existe neste armazém."));
        if (string.IsNullOrWhiteSpace(Str(args, "name")))
            errors.Add(Error(step, "name", "business.invalid_name", "informe o nome da zona."));
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var code = Str(args, "code") ?? throw new InvalidOperationException("Missing code.");
        var name = (Str(args, "name") ?? throw new InvalidOperationException("Missing name.")).Trim();
        using var insert = ctx.Command("INSERT INTO zones (warehouse_id, code, name) VALUES ($wh, $code, $name)");
        insert.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        insert.Parameters.AddWithValue("$code", code);
        insert.Parameters.AddWithValue("$name", name);
        insert.ExecuteNonQuery();
        ctx.Diff.AddCreated("Zona", code, $"criada: {name}");
        return code;
    }
}
