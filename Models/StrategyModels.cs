namespace TraderPhil.V4.Web.Models;

/// <summary>One active DCAGroup row + matching KrakenSymbols/quote metadata.</summary>
public sealed class StrategyRow
{
    public int      DCAGroupId            { get; init; }
    public int      UEI                   { get; init; }
    public int      LongSymbolID          { get; init; }
    public int      ShortSymbolID         { get; init; }
    public decimal? DcaLotSize            { get; init; }
    public bool?    AllowLongs            { get; init; }
    public bool?    AllowShorts           { get; init; }
    public int      OrderExpirationHours  { get; init; }
    public int      BrokerTTLMinutes      { get; init; }
    public string?  Strategy              { get; init; }
    public string   Status                { get; init; } = "";
    public DateTime StartTime             { get; init; }

    /// <summary>Set when the coin is paused because it exceeds the plan's coin limit.</summary>
    public DateTime? PlanPausedAt         { get; init; }
    public bool IsPlanPaused => PlanPausedAt.HasValue;

    // From vSymbiCorePairs
    public string?  DisplayBaseAsset      { get; init; }
    public string?  LongSymbol            { get; init; }
    public decimal? LongOrderMin          { get; init; }
    public int?     LongLotDecimals       { get; init; }
    public decimal? LongLastPrice         { get; init; }
    public DateTime? LongQuoteTime        { get; init; }
    public string?  ShortSymbol           { get; init; }
}

/// <summary>A BaseAsset the user could add a new strategy for.</summary>
public sealed class AddableSymbolRow
{
    public string   BaseAsset       { get; init; } = "";  // already display-cleaned
    public int      LongSymbolID    { get; init; }
    public string   LongSymbol      { get; init; } = "";
    public decimal  LongOrderMin    { get; init; }
    public int      LongLotDecimals { get; init; }
    public decimal? LongLastPrice   { get; init; }
    public DateTime? LongQuoteTime  { get; init; }
    public int      ShortSymbolID   { get; init; }
    public string   ShortSymbol     { get; init; } = "";
}

public sealed class StrategyEditPayload
{
    public decimal DcaLotSize           { get; set; }
    public bool    AllowLongs           { get; set; }
    public bool    AllowShorts          { get; set; }
    public int     OrderExpirationHours { get; set; }
    public int     BrokerTTLMinutes     { get; set; }
}

public sealed class StrategyCreatePayload
{
    public int     LongSymbolID         { get; set; }
    public int     ShortSymbolID        { get; set; }
    public decimal DcaLotSize           { get; set; }
    public bool    AllowLongs           { get; set; }
    public bool    AllowShorts          { get; set; }
    public int     OrderExpirationHours { get; set; }
    public int     BrokerTTLMinutes     { get; set; }
}

public sealed class UserSettingsRow
{
    public int      UEI                    { get; init; }
    public decimal? WoolChipperQtyPct      { get; init; }
    public bool     TreasuryEnabled        { get; init; }
    public decimal  TreasuryMinDeployUSDT  { get; init; }
    public int      TreasuryMaxBridges     { get; init; }
    public decimal  TreasuryTrailPct       { get; init; }
    public decimal  TreasuryTightTrailPct  { get; init; }
    public decimal  TreasuryProfitLockPct  { get; init; }
    public DateTime CreatedAt              { get; init; }
    public DateTime UpdatedAt              { get; init; }
}

public sealed class TreasurySettingsPayload
{
    public bool    TreasuryEnabled        { get; set; }
    public decimal TreasuryMinDeployUSDT  { get; set; }
    public int     TreasuryMaxBridges     { get; set; }
}

/// <summary>Helper computed by the page model: whether a given quote is stale enough to warn about.</summary>
public static class QuoteFreshness
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(2);

    public static bool IsStale(DateTime? quoteTime) =>
        !quoteTime.HasValue || DateTime.UtcNow - quoteTime.Value > StaleThreshold;
}
