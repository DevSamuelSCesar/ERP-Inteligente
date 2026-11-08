using System;
using System.Collections.Generic;

namespace erp_inteligente.Core.Engine;

/// <summary>Risk of an operation or a plan (references/ARQUITETURA.md, section 5). The plan risk is the highest of its steps.</summary>
public enum RiskLevel
{
    Read = 0,
    Reversible = 1,
    Critical = 2,
}

/// <summary>Conversions between <see cref="RiskLevel"/> and the values stored in <c>plans.risk</c>.</summary>
public static class RiskLevels
{
    /// <summary>The lowercase value stored in the database.</summary>
    public static string ToDbValue(this RiskLevel risk) => risk switch
    {
        RiskLevel.Read => "read",
        RiskLevel.Reversible => "reversible",
        RiskLevel.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, "Unknown risk level."),
    };

    /// <summary>Parses a database value; throws <see cref="ArgumentException"/> for anything unknown.</summary>
    public static RiskLevel FromDbValue(string value) => value switch
    {
        "read" => RiskLevel.Read,
        "reversible" => RiskLevel.Reversible,
        "critical" => RiskLevel.Critical,
        _ => throw new ArgumentException("Unknown risk value.", nameof(value)),
    };

    /// <summary>The highest of the given levels; <see cref="RiskLevel.Read"/> when there are none.</summary>
    public static RiskLevel Max(params RiskLevel[] levels) => Max((IEnumerable<RiskLevel>)levels);

    /// <inheritdoc cref="Max(RiskLevel[])"/>
    public static RiskLevel Max(IEnumerable<RiskLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var max = RiskLevel.Read;
        foreach (var level in levels)
            if (level > max)
                max = level;
        return max;
    }
}
