using System;
using System.Collections.Generic;
using System.Linq;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace Nexus.Tests.Fakes;

/// <summary>
/// In-memory, fictitious warehouse mirroring db/seed.sql. Stands in for the SQLite layer while only the
/// UI exists (ROADMAP.md, feature 1); all values are invented.
/// </summary>
public sealed class DemoData
{
    public sealed class Balance(string sku, string location, string lot, DateOnly? expiresOn, int quantity, int reserved)
    {
        public string Sku { get; } = sku;
        public string Location { get; } = location;
        public string Lot { get; } = lot;
        public DateOnly? ExpiresOn { get; } = expiresOn;
        public int Quantity { get; set; } = quantity;
        public int Reserved { get; } = reserved;
    }

    public sealed record Product(string Sku, string Name, string Unit);
    public sealed record Location(string Code, string ZoneCode, int Capacity);

    private int _nextPlanId = 3;

    public DemoData(TimeProvider time)
    {
        Time = time;

        Products =
        [
            new("ABC-123", "Caixa de parafusos 4mm", "cx"),
            new("ABC-124", "Caixa de porcas 4mm", "cx"),
            new("FER-010", "Furadeira de impacto 600W", "un"),
            new("FER-011", "Kit de brocas 15 peças", "un"),
            new("LIM-200", "Detergente neutro 5L", "gl"),
            new("LIM-201", "Álcool 70% 1L", "fr"),
            new("ALI-300", "Iogurte natural 170g (cx 24)", "cx"),
            new("ALI-301", "Queijo muçarela 1kg", "pc"),
            new("EMB-500", "Fita adesiva 48mm", "pct"),
        ];

        Zones = new Dictionary<string, string>
        {
            ["DOCA"] = "Docas de recebimento e expedição",
            ["A"] = "Porta-paletes seco",
            ["B"] = "Prateleiras de picking",
            ["C"] = "Câmara refrigerada",
        };

        Locations =
        [
            new("DOCA-01", "DOCA", 500), new("DOCA-02", "DOCA", 500),
            new("A-01-01", "A", 60), new("A-01-02", "A", 60), new("A-01-03", "A", 60), new("A-01-04", "A", 60),
            new("A-02-01", "A", 60), new("A-02-02", "A", 60), new("A-02-03", "A", 60), new("A-02-04", "A", 60),
            new("B-01-01", "B", 30), new("B-01-02", "B", 30), new("B-01-03", "B", 30), new("B-01-04", "B", 30),
            new("C-01-01", "C", 40), new("C-01-02", "C", 40),
        ];

        Balances =
        [
            new("ABC-123", "DOCA-01", "", null, 40, 0),
            new("ABC-123", "A-01-01", "", null, 40, 0),
            new("ABC-124", "A-01-02", "", null, 35, 10),
            new("FER-010", "B-01-01", "", null, 12, 2),
            new("FER-011", "B-01-02", "", null, 20, 0),
            new("LIM-200", "A-01-03", "L200-2611", new DateOnly(2026, 11, 30), 18, 0),
            new("LIM-200", "A-01-04", "L200-2703", new DateOnly(2027, 3, 31), 30, 0),
            new("LIM-201", "B-01-03", "L201-2612", new DateOnly(2026, 12, 15), 25, 0),
            new("ALI-300", "C-01-01", "L300-2611A", new DateOnly(2026, 11, 5), 16, 6),
            new("ALI-300", "C-01-01", "L300-2611B", new DateOnly(2026, 11, 20), 20, 0),
            new("ALI-301", "C-01-02", "L301-2612", new DateOnly(2026, 12, 1), 14, 0),
            new("EMB-500", "B-01-04", "", null, 28, 0),
        ];

        Orders =
        [
            new("NF-FICT-000101", "Entrada", "Fornecedora Fictícia Alfa Ltda", 2, 115, "Recebido", new DateOnly(2026, 10, 20)),
            new("NF-FICT-000102", "Entrada", "Laticínios Fictícios Gama", 2, 68, "Previsto", new DateOnly(2026, 10, 28)),
            new("PED-FICT-5001", "Saída", "Loja Fictícia Centro", 2, 12, "Reservado", new DateOnly(2026, 10, 28)),
            new("PED-FICT-5002", "Saída", "Mercado Fictício Norte", 2, 10, "Aberto", new DateOnly(2026, 10, 29)),
        ];

        Recipes =
        [
            new("armazenar_da_doca", "armazenagem", "Move a quantidade recebida de um SKU da doca para o endereço livre mais próximo", 7, "Ativa"),
        ];

        Audit =
        [
            new(Utc(2026, 10, 20, 10, 1), "Nexus por João", "Plano enviado", "Plano 1 · reversível"),
            new(Utc(2026, 10, 20, 10, 2), "Sistema por João", "Plano executado", "Plano 1 · 1 passo"),
            new(Utc(2026, 10, 26, 8, 5), "Nexus por João", "Plano enviado", "Plano 2 · crítico · aguardando aprovação"),
            new(Utc(2026, 10, 26, 8, 6), "Nexus por João", "Acesso negado", "Ajuste de estoque direto pela IA (DENY stock.adjust)"),
        ];

        var executed = new Plan(1, "armazenagem", "op.joao", "João", Utc(2026, 10, 20, 10, 1),
            [new PlanStep(PlanOperation.MoveStock, 40, "cx", "ABC-123", "DOCA-01", "A-01-01")])
        {
            Status = PlanStatus.Executed,
            DecidedByName = "Execução automática",
            DecidedAt = Utc(2026, 10, 20, 10, 2),
            Summary = new ExecutionSummary(
                [new("Quantidade movimentada", "40 cx"), new("Endereços afetados", "2"), new("SKUs", "1"), new("Risco", "Reversível")],
                [new MoveFlow("ABC-123", 40, "cx", "DOCA-01", "A-01-01")],
                []),
        };
        var pending = new Plan(2, "inventario", "op.joao", "João", Utc(2026, 10, 26, 8, 5),
            [new PlanStep(PlanOperation.AdjustStock, -1, "cx", "ALI-300", "C-01-01", null, "Avaria: embalagem violada")])
        {
            Status = PlanStatus.PendingApproval,
        };
        Plans = [executed, pending];
    }

    public TimeProvider Time { get; }
    public List<Product> Products { get; }
    public Dictionary<string, string> Zones { get; }
    public List<Location> Locations { get; }
    public List<Balance> Balances { get; }
    public List<OrderRow> Orders { get; }
    public List<RecipeRow> Recipes { get; }
    public List<AuditRow> Audit { get; }
    public List<Plan> Plans { get; }

    public int NextPlanId() => _nextPlanId++;

    public Product? FindProduct(string sku) => Products.FirstOrDefault(p => p.Sku == sku);

    public Location? FindLocation(string code) => Locations.FirstOrDefault(l => l.Code == code);

    public int QuantityAt(string sku, string location) =>
        Balances.Where(b => b.Sku == sku && b.Location == location).Sum(b => b.Quantity);

    public int OccupiedAt(string location) => Balances.Where(b => b.Location == location).Sum(b => b.Quantity);

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);
}
