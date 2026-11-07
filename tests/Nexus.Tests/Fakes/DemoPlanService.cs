using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace Nexus.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the deterministic engine (ROADMAP.md, feature 2): validates every step against
/// the current balances, then applies all of them or none.
/// </summary>
public sealed class DemoPlanService(DemoData data) : IPlanService
{
    public IReadOnlyList<Plan> Plans => data.Plans;

    public bool IsEngineAvailable => true;

    public Task RefreshAsync() => Task.CompletedTask;

    public event EventHandler<Plan>? PlanChanged;

    public Task SubmitAsync(Plan plan)
    {
        if (data.Plans.Any(p => p.Id == plan.Id))
            throw new InvalidOperationException($"Plan {plan.Id} was already submitted.");

        data.Plans.Add(plan);
        AppendAudit($"Nexus por {plan.RequestedByName}", "Plano enviado",
            $"Plano {plan.Id} · {(plan.Risk == PlanRisk.Critical ? "crítico" : "reversível")}");

        var error = Validate(plan);
        if (error is not null)
        {
            Fail(plan, error);
        }
        else if (plan.Risk == PlanRisk.Critical)
        {
            plan.Status = PlanStatus.PendingApproval;
            PlanChanged?.Invoke(this, plan);
        }
        else
        {
            Execute(plan, "Execução automática");
        }
        return Task.CompletedTask;
    }

    public Task ApproveAsync(Plan plan, UserSession approver)
    {
        EnsureCanDecide(plan, approver);
        AppendAudit(approver.DisplayName, "Plano aprovado", $"Plano {plan.Id}");

        var error = Validate(plan);
        if (error is not null)
            Fail(plan, error);
        else
            Execute(plan, approver.DisplayName);
        return Task.CompletedTask;
    }

    public Task RejectAsync(Plan plan, UserSession approver, string reason)
    {
        EnsureCanDecide(plan, approver);
        reason = reason.Trim();
        if (reason.Length == 0 || reason.Length > ApprovalPolicy.MaxReasonLength)
            throw new ArgumentException($"The reason must have 1 to {ApprovalPolicy.MaxReasonLength} characters.", nameof(reason));

        plan.Status = PlanStatus.Rejected;
        plan.DecidedByName = approver.DisplayName;
        plan.DecidedAt = data.Time.GetUtcNow();
        plan.RejectionReason = reason;
        AppendAudit(approver.DisplayName, "Plano rejeitado", $"Plano {plan.Id} · {reason}");
        PlanChanged?.Invoke(this, plan);
        return Task.CompletedTask;
    }

    private static void EnsureCanDecide(Plan plan, UserSession approver)
    {
        if (!ApprovalPolicy.CanDecide(plan, approver))
            throw new UnauthorizedAccessException($"{approver.Username} cannot decide plan {plan.Id}.");
    }

    /// <summary>Dry run over a copy of the balances; returns a business-language error or null.</summary>
    private string? Validate(Plan plan)
    {
        var simulated = new Dictionary<(string Sku, string Location), int>();
        int Current(string sku, string location) =>
            simulated.TryGetValue((sku, location), out var q) ? q : data.QuantityAt(sku, location);

        foreach (var step in plan.Steps)
        {
            if (data.FindProduct(step.Sku) is null)
                return $"SKU {step.Sku} não existe.";

            switch (step.Operation)
            {
                case PlanOperation.MoveStock:
                    if (step.From is null || step.To is null || data.FindLocation(step.From) is null || data.FindLocation(step.To) is null)
                        return "Endereço de origem ou destino inválido.";
                    if (step.Quantity <= 0)
                        return "A quantidade precisa ser maior que zero.";
                    if (Current(step.Sku, step.From) < step.Quantity)
                        return $"{step.From} tem só {Current(step.Sku, step.From)} {step.Unit} de {step.Sku}.";
                    simulated[(step.Sku, step.From)] = Current(step.Sku, step.From) - step.Quantity;
                    simulated[(step.Sku, step.To)] = Current(step.Sku, step.To) + step.Quantity;
                    break;

                case PlanOperation.AdjustStock:
                    if (step.From is null || data.FindLocation(step.From) is null)
                        return "Endereço do ajuste inválido.";
                    if (string.IsNullOrWhiteSpace(step.Note))
                        return "Ajuste de estoque exige motivo.";
                    if (Current(step.Sku, step.From) + step.Quantity < 0)
                        return $"O ajuste deixaria {step.Sku} negativo em {step.From}.";
                    simulated[(step.Sku, step.From)] = Current(step.Sku, step.From) + step.Quantity;
                    break;
            }
        }
        return null;
    }

    private void Execute(Plan plan, string decidedBy)
    {
        var touched = plan.Steps
            .SelectMany(s => new[] { (s.Sku, s.From), (s.Sku, s.To) })
            .Where(k => k.Item2 is not null)
            .Select(k => (k.Sku, Location: k.Item2!))
            .Distinct()
            .ToList();
        var before = touched.ToDictionary(k => k, k => data.QuantityAt(k.Sku, k.Location));

        foreach (var step in plan.Steps)
        {
            switch (step.Operation)
            {
                case PlanOperation.MoveStock:
                    Take(step.Sku, step.From!, step.Quantity);
                    Put(step.Sku, step.To!, step.Quantity);
                    break;
                case PlanOperation.AdjustStock when step.Quantity < 0:
                    Take(step.Sku, step.From!, -step.Quantity);
                    break;
                case PlanOperation.AdjustStock:
                    Put(step.Sku, step.From!, step.Quantity);
                    break;
            }
        }

        plan.Status = PlanStatus.Executed;
        plan.DecidedByName = decidedBy;
        plan.DecidedAt = data.Time.GetUtcNow();
        plan.Summary = BuildSummary(plan, before);
        AppendAudit($"Sistema por {plan.RequestedByName}", "Plano executado", $"Plano {plan.Id} · {plan.Steps.Count} passo(s)");
        PlanChanged?.Invoke(this, plan);
    }

    /// <summary>Removes quantity from the balances of a location, earliest expiry first (FEFO).</summary>
    private void Take(string sku, string location, int quantity)
    {
        foreach (var balance in data.Balances
                     .Where(b => b.Sku == sku && b.Location == location && b.Quantity > 0)
                     .OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue))
        {
            var taken = Math.Min(balance.Quantity, quantity);
            balance.Quantity -= taken;
            quantity -= taken;
            if (quantity == 0)
                return;
        }
    }

    private void Put(string sku, string location, int quantity)
    {
        var balance = data.Balances.FirstOrDefault(b => b.Sku == sku && b.Location == location && b.Lot.Length == 0);
        if (balance is null)
            data.Balances.Add(new DemoData.Balance(sku, location, "", null, quantity, 0));
        else
            balance.Quantity += quantity;
    }

    private ExecutionSummary BuildSummary(Plan plan, Dictionary<(string Sku, string Location), int> before)
    {
        var moves = plan.Steps.Where(s => s.Operation == PlanOperation.MoveStock).ToList();
        var adjusts = plan.Steps.Where(s => s.Operation == PlanOperation.AdjustStock).ToList();
        var unit = plan.Steps[0].Unit;

        var totals = new List<SummaryTotal>();
        if (moves.Count > 0)
            totals.Add(new("Quantidade movimentada", $"{moves.Sum(s => s.Quantity)} {unit}"));
        if (adjusts.Count > 0)
            totals.Add(new("Ajuste líquido", $"{adjusts.Sum(s => s.Quantity):+0;-0;0} {unit}"));
        totals.Add(new("Endereços afetados", before.Keys.Select(k => k.Location).Distinct().Count().ToString()));
        totals.Add(new("SKUs", plan.Steps.Select(s => s.Sku).Distinct().Count().ToString()));
        totals.Add(new("Risco", plan.Risk == PlanRisk.Critical ? "Crítico" : "Reversível"));

        var flows = moves.Select(s => new MoveFlow(s.Sku, s.Quantity, s.Unit, s.From!, s.To!)).ToList();
        var balances = adjusts.Count == 0
            ? new List<BalanceChange>()
            : before.Select(kv => new BalanceChange(kv.Key.Sku, kv.Key.Location, kv.Value, data.QuantityAt(kv.Key.Sku, kv.Key.Location), unit)).ToList();

        return new ExecutionSummary(totals, flows, balances);
    }

    private void Fail(Plan plan, string message)
    {
        plan.Status = PlanStatus.Failed;
        plan.FailureMessage = message;
        plan.DecidedAt = data.Time.GetUtcNow();
        AppendAudit("Sistema", "Plano falhou", $"Plano {plan.Id} · {message}");
        PlanChanged?.Invoke(this, plan);
    }

    private void AppendAudit(string user, string action, string detail) =>
        data.Audit.Add(new AuditRow(data.Time.GetUtcNow(), user, action, detail));
}
