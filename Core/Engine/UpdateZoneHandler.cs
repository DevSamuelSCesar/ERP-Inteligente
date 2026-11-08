using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;
using static erp_inteligente.Core.Engine.StructureStore;

namespace erp_inteligente.Core.Engine;

/// <summary>update_zone: renames a zone or (de)activates it; a zone holding stock cannot be deactivated.</summary>
internal sealed class UpdateZoneHandler : IOperationHandler
{
    private sealed record Change(ZoneRow Zone, string? Name, bool? Active, string Description);

    public string Op => "update_zone";

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
        using var update = ctx.Command("UPDATE zones SET name = COALESCE($name, name), is_active = COALESCE($active, is_active) WHERE id = $id");
        update.Parameters.AddWithValue("$name", (object?)change.Name ?? DBNull.Value);
        update.Parameters.AddWithValue("$active", change.Active is bool b ? (b ? 1 : 0) : DBNull.Value);
        update.Parameters.AddWithValue("$id", change.Zone.Id);
        update.ExecuteNonQuery();
        ctx.Diff.AddUpdated("Zona atualizada", change.Zone.Code, change.Description);
        return change.Zone.Code;
    }

    private static Change? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var code = Str(args, "code");
        var zone = FindZone(ctx, code);
        if (zone is null)
        {
            errors.Add(Error(step, "code", "business.unknown_zone", $"a zona {code} não existe neste armazém."));
            return null;
        }
        var name = Str(args, "name")?.Trim();
        var active = Bool(args, "is_active");
        if (name is null && active is null)
        {
            errors.Add(Error(step, null, "business.nothing_to_change", "informe o novo nome ou a nova situação da zona."));
            return null;
        }
        if (name is { Length: 0 })
        {
            errors.Add(Error(step, "name", "business.invalid_name", "informe o nome da zona."));
            return null;
        }

        var changes = new List<string>();
        if (name is not null && !string.Equals(name, zone.Name, StringComparison.Ordinal))
            changes.Add($"nome {zone.Name} → {name}");
        if (active is bool wanted && wanted != zone.IsActive)
        {
            if (!wanted)
            {
                var stock = ZoneStock(ctx, zone.Id);
                if (stock > 0)
                {
                    errors.Add(Error(step, "is_active", "business.zone_has_stock",
                        $"a zona {zone.Code} ainda tem {N(stock)} unidades em estoque; mova ou baixe antes de desativar."));
                    return null;
                }
            }
            changes.Add($"situação {ActiveText(zone.IsActive, true)} → {ActiveText(wanted, true)}");
        }
        if (changes.Count == 0)
        {
            errors.Add(Error(step, null, "business.nothing_to_change", $"a zona {zone.Code} já está assim; não há o que alterar."));
            return null;
        }
        return new Change(zone, name, active, string.Join("; ", changes));
    }
}
