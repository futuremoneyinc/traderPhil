namespace TraderPhil.V4.Web.Models;

public sealed class ProfitTargetsSummary
{
    public int     UEI                       { get; init; }
    public int     TotalCount                { get; init; }
    public int     AvailableCount            { get; init; }
    public int     ConsumedCount             { get; init; }
    public decimal TotalOriginalProfitTarget { get; init; }
    public decimal TotalRecoveryBoost        { get; init; }
    public decimal TotalCurrentProfitTarget  { get; init; }
    public decimal ConsumedValue             { get; init; }
    public decimal MinOriginalProfitTarget   { get; init; }
    public decimal MaxOriginalProfitTarget   { get; init; }

    public bool HasTargets => TotalCount > 0;

    /// <summary>
    /// Progress as a fraction of targets consumed (count-based, not dollar-weighted).
    /// Returns 0 if there are no targets.
    /// </summary>
    public decimal ProgressFraction =>
        TotalCount > 0 ? (decimal)ConsumedCount / TotalCount : 0m;

    /// <summary>Progress as a percentage (0-100) with two decimal places.</summary>
    public decimal ProgressPercent =>
        Math.Round(ProgressFraction * 100m, 2);
}

public sealed class ProfitTargetsInitPayload
{
    public decimal StartProfit          { get; set; }   // $1 to $10,000
    public decimal GrowthPercentage     { get; set; }   // 0.005 / 0.0125 / 0.025
    public decimal LongTermProfitTarget { get; set; }   // default 1,000,000
}

public sealed class ProfitTargetsInitResult
{
    public int     TargetsCreated       { get; init; }
    public decimal TotalProfitTargetSum { get; init; }
}

public sealed class ProfitTargetsResetPayload
{
    public string ConfirmText { get; set; } = "";
}

/// <summary>The growth-percentage radio options the user picks from.</summary>
public static class ProfitTargetsGrowthOptions
{
    public const decimal Slow   = 0.005m;
    public const decimal Medium = 0.0125m;
    public const decimal Fast   = 0.025m;

    public static bool IsValid(decimal value) =>
        value == Slow || value == Medium || value == Fast;
}
