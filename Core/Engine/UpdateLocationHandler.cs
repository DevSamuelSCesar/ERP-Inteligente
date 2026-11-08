using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;
using static erp_inteligente.Core.Engine.StructureStore;

namespace erp_inteligente.Core.Engine;

/// <summary>update_location: changes the capacity or the active flag of a location of the warehouse.</summary>
internal sealed class UpdateLocationHandler : IOperationHandler
{
    private sealed record Change(LocationInfo Location, int? Capacity, bool? Active, string Description);

    public string Op => "update_location";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var change = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        using var update = ctx.Command("UPDATE locations SET capacity = COALESCE($cap, capacity), is_active = COALESCE($active, is_active) WHERE id = $id");
        update.Parameters.AddWithValue("$cap", change.Capacity is int c ? c : DBNull.Value);
        update.Parameters.AddWithValue("$active", change.Active is bool b ? (b ? 1 : 0) : DBNull.Value);
        update.Parameters.AddWithValue("$id", change.Location.Id);
        update.ExecuteNonQuery();
        ctx.Diff.AddUpdated("Endereço atualizado", change.Location.Code, change.Description);
        return change.Location.Code;
    }

    private static Change? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var code = Str(args, "code");
        var location = FindAnyLocation(ctx, code);
        if (location is null)
        {
            errors.Add(Error(step, "code", "business.unknown_location", $"o endereço {code} não existe neste armazém."));
            return null;
        }
        var capacity = Int(args, "capacity");
        var active = Bool(args, "is_active");
        if (capacity is null && active is null)
        {
            errors.Add(Error(step, null, "business.nothing_to_change", "informe a nova capacidade ou a nova situação do endereço."));
            return null;
        }

        var changes = new List<string>();
        var occupied = capacity is not null || active == false ? Occupied(ctx, location.Id) : 0;
        if (capacity is int wantedCapacity && wantedCapacity != location.Capacity)
        {
            if (wantedCapacity < occupied)
            {
                errors.Add(Error(step, "capacity", "business.capacity_below_stock",
                    $"o endereço {location.Code} guarda {N(occupied)} unidades e a capacidade não pode ficar abaixo disso."));
                return null;
            }
            changes.Add($"capacidade {N(location.Capacity)} → {N(wantedCapacity)}");
        }
        if (active is bool wanted && wanted != location.IsActive)
        {
            if (!wanted && occupied > 0)
            {
                errors.Add(Error(step, "is_active", "business.location_has_stock",
                    $"o endereço {location.Code} ainda tem {N(occupied)} unidades em estoque; mova ou baixe antes de desativar."));
                return null;
            }
            if (wanted && !location.ZoneActive)
            {
                errors.Add(Error(step, "is_active", "business.inactive_zone",
                    $"a zona {location.ZoneCode} do endereço {location.Code} está desativada."));
                return null;
            }
            changes.Add($"situação {ActiveText(location.IsActive, false)} → {ActiveText(wanted, false)}");
        }
        if (changes.Count == 0)
        {
            errors.Add(Error(step, null, "business.nothing_to_change", $"o endereço {location.Code} já está assim; não há o que alterar."));
            return null;
        }
        return new Change(location, capacity, active, string.Join("; ", changes));
    }
}
