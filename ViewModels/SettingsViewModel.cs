using System.Collections.Generic;
using erp_inteligente.Models;

namespace erp_inteligente.ViewModels;

/// <summary>Session and application information; provider settings arrive with the LLM layer.</summary>
public sealed class SettingsViewModel(UserSession user, string databaseDescription) : PageViewModel
{
    public override string Title => "Configurações";

    public IReadOnlyList<SummaryTotal> Fields { get; } =
    [
        new("Usuário", $"{user.DisplayName} ({user.Username})"),
        new("Papel", user.RoleName),
        new("Armazém", user.WarehouseCode),
        new("Banco de dados", databaseDescription),
        new("Sessão", "Expira após 15 minutos sem uso"),
        new("Provedor de LLM", "Ainda não configurado: chega com o Planner (feature 3 do roadmap)"),
        new("Tema", "Escuro"),
        new("Fonte", "Barlow (SIL OFL)"),
    ];
}
