namespace erp_inteligente.Core.Engine;

/// <summary>The built-in skill (business area) each catalog operation belongs to.</summary>
public static class SkillMapping
{
    /// <summary>The skill name for <paramref name="op"/>; unknown and master-data operations map to <c>cadastros</c>.</summary>
    public static string ForOperation(string op) => op switch
    {
        "receive_goods" or "create_inbound_order" or "cancel_inbound_order" => "recebimento",
        "move_stock" => "armazenagem",
        "adjust_stock" => "inventario",
        "pick_items" => "separacao",
        "create_outbound_order" or "cancel_outbound_order" or "reserve_stock" or "release_reservation" or "ship_order" => "expedicao",
        _ => "cadastros",
    };
}
