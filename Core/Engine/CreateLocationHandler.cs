using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>create_location: adds a storage location to a zone of the session warehouse.</summary>
internal sealed class CreateLocationHandler : IOperationHandler
{
    public string Op => "create_location";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var zoneCode = Str(args, "zone");
        var zone = StructureStore.FindZone(ctx, zoneCode);
        if (zone is null)
            errors.Add(Error(step, "zone", "business.unknown_zone", $"a zona {zoneCode} não existe neste armazém."));
        else if (!zone.IsActive)
            errors.Add(Error(step, "zone", "business.inactive_zone", $"a zona {zone.Code} está desativada."));
        var code = Str(args, "code");
        using var cmd = ctx.Command("SELECT 1 FROM locations WHERE code = $code");
        cmd.Parameters.AddWithValue("$code", code ?? string.Empty);
        if (cmd.ExecuteScalar() is not null)
            errors.Add(Error(step, "code", "business.duplicate_location", $"o código {code} já está em uso."));
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var code = Str(args, "code") ?? throw new InvalidOperationException("Missing code.");
        var zone = Str(args, "zone");
        var zoneId = StructureStore.FindZone(ctx, zone)?.Id ?? throw new InvalidOperationException("Step applied without validation.");
        var capacity = Int(args, "capacity") ?? throw new InvalidOperationException("Missing capacity.");
        using var insert = ctx.Command("INSERT INTO locations (zone_id, code, capacity) VALUES ($zone, $code, $cap)");
        insert.Parameters.AddWithValue("$zone", zoneId);
        insert.Parameters.AddWithValue("$code", code);
        insert.Parameters.AddWithValue("$cap", capacity);
        insert.ExecuteNonQuery();
        ctx.Diff.AddCreated("Endereço", code, $"zona {zone}, capacidade {capacity.ToString(CultureInfo.InvariantCulture)}");
        return code;
    }
}
