namespace erp_inteligente.Models;

/// <summary>
/// Permission codes from <c>role_permissions</c> (db/seed.sql). The UI only hides what a user
/// cannot use; the policy engine and the database remain the source of truth.
/// </summary>
public static class Permissions
{
    public const string StockRead = "stock.read";
    public const string PlanSubmit = "plan.submit";
    public const string PlanApprove = "plan.approve";
    public const string RecipeManage = "recipe.manage";
    public const string AuditRead = "audit.read";
    public const string SensitiveRead = "sensitive.read";
    public const string UserManage = "user.manage";
    public const string RoleManage = "role.manage";
}
