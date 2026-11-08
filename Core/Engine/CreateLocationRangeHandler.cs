using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;
using static erp_inteligente.Core.Engine.StructureStore;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// create_location_range: creates the locations <c>{zone}-{aisle:00}-{level:00}</c> for a range of levels of one aisle,
/// all or nothing (no code may exist). Returns the first code.
/// </summary>
internal sealed class CreateLocationRangeHandler : IOperationHandler
{
    private const int MaxLocations = 100;

    private sealed record LocationRange(ZoneRow Zone, int Aisle, int From, int To, int Capacity)
    {
        public int Count => To - From + 1;
        public string CodeAt(int level) =>
            string.Create(CultureInfo.InvariantCulture, $"{Zone.Code}-{Aisle:00}-{level:00}");
    }

    public string Op => "create_location_range";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var range = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        using var insert = ctx.Command("INSERT INTO locations (zone_id, code, capacity) VALUES ($zone, $code, $cap)");
        var zone = insert.Parameters.Add("$zone", Microsoft.Data.Sqlite.SqliteType.Integer);
        var code = insert.Parameters.Add("$code", Microsoft.Data.Sqlite.SqliteType.Text);
        var cap = insert.Parameters.Add("$cap", Microsoft.Data.Sqlite.SqliteType.Integer);
        zone.Value = range.Zone.Id;
        cap.Value = range.Capacity;
        for (var level = range.From; level <= range.To; level++)
        {
            code.Value = range.CodeAt(level);
            insert.ExecuteNonQuery();
        }
        var first = range.CodeAt(range.From);
        var description = range.Count == 1
            ? $"zona {range.Zone.Code}, capacidade {N(range.Capacity)}"
            : $"{N(range.Count)} endereços de {first} a {range.CodeAt(range.To)}, capacidade {N(range.Capacity)} cada";
        ctx.Diff.AddCreated("Endereços", first, description);
        return first;
    }

    private static LocationRange? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var zoneCode = Str(args, "zone");
        var zone = FindZone(ctx, zoneCode);
        if (zone is null)
        {
            errors.Add(Error(step, "zone", "business.unknown_zone", $"a zona {zoneCode} não existe neste armazém."));
            return null;
        }
        if (!zone.IsActive)
        {
            errors.Add(Error(step, "zone", "business.inactive_zone", $"a zona {zone.Code} está desativada."));
            return null;
        }
        var aisle = Int(args, "aisle") ?? 0;
        var from = Int(args, "level_from") ?? 0;
        var to = Int(args, "level_to") ?? 0;
        var capacity = Int(args, "capacity") ?? 0;
        if (aisle is < 1 or > 99 || from is < 1 or > 99 || to is < 1 or > 99 || capacity < 1)
        {
            errors.Add(Error(step, null, "business.invalid_range", "corredor, níveis e capacidade devem ser números positivos (corredor e níveis até 99)."));
            return null;
        }
        if (to < from)
        {
            errors.Add(Error(step, "level_to", "business.invalid_range", "o nível final deve ser igual ou maior que o nível inicial."));
            return null;
        }
        var range = new LocationRange(zone, aisle, from, to, capacity);
        if (range.Count > MaxLocations)
        {
            errors.Add(Error(step, "level_to", "business.range_too_large", $"é possível criar no máximo {MaxLocations} endereços por vez."));
            return null;
        }

        var existing = new List<string>();
        for (var level = from; level <= to; level++)
            if (LocationCodeInUse(ctx, range.CodeAt(level)))
                existing.Add(range.CodeAt(level));
        if (existing.Count > 0)
        {
            errors.Add(Error(step, "aisle", "business.duplicate_location",
                $"já existem os endereços {string.Join(", ", existing)}; nenhum endereço foi criado."));
            return null;
        }
        return range;
    }
}
