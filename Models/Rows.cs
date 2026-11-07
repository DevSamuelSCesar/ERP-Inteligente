using System;

namespace erp_inteligente.Models;

public sealed record StockRow(string Sku, string Product, string Location, string Lot, DateOnly? ExpiresOn, int Quantity, int Reserved, string Unit)
{
    public int Available => Quantity - Reserved;
    public string ExpiresOnText => ExpiresOn?.ToString("dd/MM/yyyy") ?? "";
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

public sealed record AuditRow(DateTimeOffset At, string User, string Action, string Detail)
{
    public string AtText => At.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}
