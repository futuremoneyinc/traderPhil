using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Pages.Performance;

[Authorize]
public class IndexModel : TraderPhilPageModel
{
    private readonly IPerformanceRepository _perf;
    private readonly ITreasuryRepository _treasury;
    private readonly IProfitTargetsRepository _profitTargets;

    public IndexModel(
        IWebUserRepository users,
        IPerformanceRepository perf,
        ITreasuryRepository treasury,
        IProfitTargetsRepository profitTargets) : base(users)
    {
        _perf = perf;
        _treasury = treasury;
        _profitTargets = profitTargets;
    }

    /// <summary>The UEI in scope for this page. Defaults to the first one granted to the user.</summary>
    public int CurrentUei { get; private set; }

    public IReadOnlyList<int> AvailableUeis { get; private set; } = Array.Empty<int>();

    // -- Card data --
    public StrategyCard SymbiCoreCard      { get; private set; } = StrategyCard.Empty("SymbiCore", "SymbiCoreDCAcquisition");
    public StrategyCard WoolChipperCard    { get; private set; } = StrategyCard.Empty("WoolChipper", "WoolChipperV2");
    public TreasuryCard TreasuryCard       { get; private set; } = new TreasuryCard();
    public ProfitTargetsSummary? ProfitTargets { get; private set; }

    public bool HasProfitTargets => ProfitTargets != null && ProfitTargets.HasTargets;

    public async Task<IActionResult> OnGetAsync(int? uei = null)
    {
        AvailableUeis = await GetMyUeisAsync();
        if (AvailableUeis.Count == 0)
        {
            // No grants — show the page but every card will display its empty state.
            return Page();
        }

        CurrentUei = uei ?? AvailableUeis[0];
        var denied = await AssertUeiAccessAsync(CurrentUei);
        if (denied is not null) return denied;

        ProfitTargets   = await _profitTargets.GetSummaryAsync(CurrentUei);
        SymbiCoreCard   = await LoadStrategyCardAsync(CurrentUei, "SymbiCore",   "SymbiCoreDCAcquisition");
        WoolChipperCard = await LoadStrategyCardAsync(CurrentUei, "WoolChipper", "WoolChipperV2");
        TreasuryCard    = await LoadTreasuryCardAsync(CurrentUei);

        return Page();
    }

    private async Task<StrategyCard> LoadStrategyCardAsync(int uei, string displayName, string strategyKey)
    {
        var days      = await _perf.GetRecentDaysAsync(uei, strategyKey, 10);
        var lifetime  = await _perf.GetLifetimePLAsync(uei, strategyKey);

        // Pre-load day details for inline expansion.
        var details = new Dictionary<string, IReadOnlyList<TradeDetailRow>>();
        foreach (var d in days.Where(d => d.HasActivity))
        {
            var key = $"{strategyKey}-{d.StatDate:yyyyMMdd}";
            details[key] = await _perf.GetDayDetailAsync(uei, strategyKey, d.StatDate);
        }

        return new StrategyCard
        {
            DisplayName  = displayName,
            StrategyKey  = strategyKey,
            Days         = days,
            LifetimePL   = lifetime,
            DayDetails   = details
        };
    }

    private async Task<TreasuryCard> LoadTreasuryCardAsync(int uei)
    {
        var health   = await _treasury.GetHealthAsync(uei);
        var lifetime = await _perf.GetLifetimePLAsync(uei, "TreasuryBridge");
        return new TreasuryCard
        {
            Health     = health,
            LifetimePL = lifetime
        };
    }
}

public sealed class StrategyCard
{
    public string DisplayName { get; init; } = "";
    public string StrategyKey { get; init; } = "";
    public IReadOnlyList<DailyStatRow> Days { get; init; } = Array.Empty<DailyStatRow>();
    public decimal LifetimePL { get; init; }
    public Dictionary<string, IReadOnlyList<TradeDetailRow>> DayDetails { get; init; }
        = new Dictionary<string, IReadOnlyList<TradeDetailRow>>();

    public static StrategyCard Empty(string display, string key) => new()
    {
        DisplayName = display,
        StrategyKey = key,
        Days = Array.Empty<DailyStatRow>(),
        LifetimePL = 0m
    };

    public bool HasAnyActivity => Days.Any(d => d.HasActivity);
}

public sealed class TreasuryCard
{
    public TreasuryHealthRow? Health { get; init; }
    public decimal LifetimePL { get; init; }
}
