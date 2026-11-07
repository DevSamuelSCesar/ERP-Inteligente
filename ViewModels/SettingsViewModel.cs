using System.Collections.Generic;
using erp_inteligente.Models;

namespace erp_inteligente.ViewModels;

/// <summary>Session and application information; provider settings arrive with the LLM layer.</summary>
public sealed class SettingsViewModel(UserSession user) : PageViewModel
{
    public override string Title => "Configurações";

    public IReadOnlyList<SummaryTotal> Fields { get; } =
    [
        new("Usuário", $"{user.DisplayName} ({user.Username})"),
        new("Papel", user.RoleName),
        new("Armazém", user.WarehouseCode),
        new("Provedor de LLM", "Não configurado: esta versão tem só a interface, com dados fictícios"),
        new("Tema", "Escuro"),
        new("Fonte", "Barlow (SIL OFL)"),
    ];
}
