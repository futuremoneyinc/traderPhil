namespace TraderPhil.V4.Web.Models;

public sealed class DailyStatRow
{
    public DateTime StatDate       { get; init; }
    public int      UEI            { get; init; }
    public string   StrategyName   { get; init; } = "";
    public int      ClosedTrades   { get; init; }
    public int      Wins           { get; init; }
    public int      Losses         { get; init; }
    public int      BreakEven      { get; init; }
    public decimal  RealizedPL     { get; init; }
    public decimal  DailyVolume    { get; init; }
    public decimal? AccountSizeUSD { get; init; }

    // Derived metrics computed at read time so the formulas can change without re-backfilling.

    /// <summary>Volume efficiency: profit as % of capital actively traded that day.</summary>
    public decimal? VolumeEfficiencyPct =>
        DailyVolume > 0 ? Math.Round(RealizedPL / DailyVolume * 100m, 4) : (decimal?)null;

    /// <summary>Account impact: profit as % of account size snapshotted that day. NULL on backfilled days.</summary>
    public decimal? AccountImpactPct =>
        AccountSizeUSD is decimal acc && acc > 0
            ? Math.Round(RealizedPL / acc * 100m, 4)
            : (decimal?)null;

    public bool HasActivity => ClosedTrades > 0 || DailyVolume > 0;
}

public sealed class TradeDetailRow
{
    public string   Symbol            { get; init; } = "";
    public int      EntryPOID         { get; init; }
    public int      ClosePOID         { get; init; }
    public decimal  EntryQty          { get; init; }
    public decimal  CloseQty          { get; init; }
    public decimal  EntryPrice        { get; init; }
    public decimal  ClosePrice        { get; init; }
    public decimal  RealizedPL        { get; init; }
    public string   TradeResult       { get; init; } = "";
    public DateTime EntryExecutedDate { get; init; }
    public DateTime CloseExecutedDate { get; init; }
    public string   BaseAsset         { get; init; } = "";
}

public sealed class TreasuryHealthRow
{
    public int     UEI                   { get; init; }
    public decimal AvailableUSDT         { get; init; }
    public decimal SLReserveUSDT         { get; init; }
    public decimal TPReserveUSDT         { get; init; }
    public decimal ReserveUSDT           { get; init; }
    public decimal DeployableUSDT        { get; init; }
    public decimal ReservePct            { get; init; }
    public int     PendingSLCount        { get; init; }
    public int     PendingStaticTPCount  { get; init; }
    public int     ActiveBridges         { get; init; }
}
