using System;

namespace erp_inteligente.Models;

public sealed record StockRow(string Sku, string Product, string Location, string Lot, DateOnly? ExpiresOn, int Quantity, int Reserved, string Unit)
{
    public int Available => Quantity - Reserved;
    public string ExpiresOnText => ExpiresOn?.ToString("dd/MM/yyyy") ?? "";
}

/// <summary>A catalog product (global) with its balance in the session's warehouse. The unit cost is never shown here.</summary>
public sealed record ProductRow(string Sku, string Name, string Unit, double? WeightKg, bool TracksExpiry, string Description, int Balance)
{
    public string WeightText => WeightKg is { } kg ? $"{kg:0.###} kg" : "";
    public string TracksExpiryText => TracksExpiry ? "Sim" : "Não";
}

public sealed record ZoneRow(string Code, string Name, bool IsActive, int Locations, int Capacity, int Occupied)
{
    public int Free => Capacity - Occupied;
    public string StatusText => IsActive ? "Ativa" : "Desativada";
}

/// <summary>A supplier or customer. Only the masked document is ever read; contacts and full documents stay encrypted.</summary>
public sealed record PartnerRow(string Kind, string Name, string Document, bool IsActive)
{
    public string StatusText => IsActive ? "Ativo" : "Desativado";
}

public sealed record LocationRow(string Code, string Zone, int Capacity, int Occupied)
{
    public int Free => Capacity - Occupied;
    public string OccupancyText => Capacity == 0 ? "" : $"{Occupied * 100 / Capacity}%";
}

public sealed record OrderRow(string Number, string Kind, string Party, int Lines, int Units, string Status, DateOnly DueOn)
{
    public string DueOnText => DueOn.ToString("dd/MM/yyyy");
}

public sealed record RecipeRow(string Name, string Skill, string Description, int Uses, string Status);

/// <param name="IsDenied">The event was refused (outcome <c>denied</c>), e.g. a failed login or a blocked permission.</param>
/// <param name="Category">Group of the action for the audit panel: Planos, IA, Segurança, Conhecimento or Sistema.</param>
/// <param name="IsFailed">The event failed (outcome <c>failed</c>), e.g. a plan that did not execute.</param>
public sealed record AuditRow(DateTimeOffset At, string User, string Action, string Detail, bool IsDenied = false,
    string Category = "Sistema", bool IsFailed = false)
{
    public string AtText => At.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public string OutcomeText => IsDenied ? "Negado" : IsFailed ? "Falhou" : "";
}
