using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Pages.Strategy;

[Authorize]
public class IndexModel : TraderPhilPageModel
{
    private readonly IStrategyRepository _strategies;
    private readonly IUserSettingsRepository _userSettings;
    private readonly IProfitTargetsRepository _profitTargets;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IWebUserRepository users,
        IStrategyRepository strategies,
        IUserSettingsRepository userSettings,
        IProfitTargetsRepository profitTargets,
        ILogger<IndexModel> logger) : base(users)
    {
        _strategies = strategies;
        _userSettings = userSettings;
        _profitTargets = profitTargets;
        _logger = logger;
    }

    public int CurrentUei { get; private set; }
    public IReadOnlyList<int> AvailableUeis { get; private set; } = Array.Empty<int>();

    public IReadOnlyList<StrategyRow> Strategies { get; private set; } = Array.Empty<StrategyRow>();
    public IReadOnlyList<AddableSymbolRow> AddableSymbols { get; private set; } = Array.Empty<AddableSymbolRow>();
    public UserSettingsRow? TreasurySettings { get; private set; }
    public ProfitTargetsSummary? ProfitTargets { get; private set; }

    public string? FlashMessage { get; private set; }
    public string? FlashKind    { get; private set; }

    public int? EditOpenForDCAGroupId { get; private set; }
    public bool AddFormOpen { get; private set; }
    public bool TreasuryEditOpen { get; private set; }
    public bool ProfitTargetsResetOpen { get; private set; }

    // Convenience for the Razor view
    public bool HasProfitTargets => ProfitTargets != null && ProfitTargets.HasTargets;
    public string ProfitTargetsResetClass =>
        ProfitTargetsResetOpen ? "tp-pt-reset open" : "tp-pt-reset";

    public static readonly (int Hours, string Label)[] OrderShelfLifeOptions = new[]
    {
        (12, "12 hours"),
        (24, "24 hours"),
        (48, "48 hours")
    };

    public static readonly (int Minutes, string Label)[] KrakenShelfLifeOptions = new[]
    {
        (60,    "1 hour"),
        (360,   "6 hours"),
        (720,   "12 hours"),
        (1440,  "24 hours"),
        (4320,  "3 days"),
        (10080, "7 days")
    };

    public async Task<IActionResult> OnGetAsync(int? uei = null)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;
        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(int uei, int dcaGroupId, [FromForm] StrategyEditPayload payload)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;

        if (payload.DcaLotSize <= 0)
            return Reject("DCA lot size must be greater than zero.", dcaGroupId);
        if (!IsValidOrderHours(payload.OrderExpirationHours))
            return Reject("Invalid Order Shelf Life.", dcaGroupId);
        if (!IsValidBrokerTtl(payload.BrokerTTLMinutes))
            return Reject("Invalid Kraken Shelf Life.", dcaGroupId);

        var n = await _strategies.UpdateActiveStrategyAsync(uei, dcaGroupId, payload);
        if (n == 0)
        {
            FlashMessage = "Couldn't find that strategy to update. It may have been closed.";
            FlashKind = "error";
        }
        else
        {
            FlashMessage = "Strategy updated.";
            FlashKind = "success";
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(int uei, [FromForm] StrategyCreatePayload payload)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;

        AddFormOpen = true;

        if (payload.DcaLotSize <= 0)
            return RejectAdd("DCA lot size must be greater than zero.");
        if (!payload.AllowLongs && !payload.AllowShorts)
            return RejectAdd("Pick at least one of 'Acquire' or 'Protect' - otherwise nothing will happen.");
        if (!IsValidOrderHours(payload.OrderExpirationHours) || !IsValidBrokerTtl(payload.BrokerTTLMinutes))
            return RejectAdd("Invalid shelf-life selection.");

        try
        {
            await _strategies.CreateStrategyAsync(uei, payload);
            FlashMessage = "Strategy added.";
            FlashKind = "success";
            AddFormOpen = false;
        }
        catch (InvalidOperationException ex)
        {
            FlashMessage = ex.Message;
            FlashKind = "error";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create strategy for UEI {UEI}", uei);
            FlashMessage = "Something went wrong creating that strategy. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTreasuryAsync(int uei, [FromForm] TreasurySettingsPayload payload)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;

        TreasuryEditOpen = true;

        if (payload.TreasuryMinDeployUSDT < 0)
        {
            FlashMessage = "Minimum deployable USDT can't be negative.";
            FlashKind = "error";
        }
        else if (payload.TreasuryMaxBridges < 1 || payload.TreasuryMaxBridges > 999)
        {
            FlashMessage = "Max bridges must be between 1 and 999.";
            FlashKind = "error";
        }
        else
        {
            await _userSettings.UpdateTreasuryAsync(uei, payload);
            FlashMessage = "Treasury settings saved.";
            FlashKind = "success";
            TreasuryEditOpen = false;
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostInitializeProfitTargetsAsync(
        int uei,
        [FromForm] ProfitTargetsInitPayload payload)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;

        // Validate against the proc's own preconditions before we waste a round trip.
        if (payload.StartProfit < 1m || payload.StartProfit > 10_000m)
        {
            FlashMessage = "Starting profit must be between $1 and $10,000.";
            FlashKind = "error";
            await LoadAllDataAsync();
            return Page();
        }

        if (!ProfitTargetsGrowthOptions.IsValid(payload.GrowthPercentage))
        {
            FlashMessage = "Pick one of the growth speed options (Slow, Medium, Fast).";
            FlashKind = "error";
            await LoadAllDataAsync();
            return Page();
        }

        if (payload.LongTermProfitTarget <= payload.StartProfit)
        {
            FlashMessage = "Long-term profit target must be greater than starting profit.";
            FlashKind = "error";
            await LoadAllDataAsync();
            return Page();
        }

        // Refuse to initialize if targets already exist. Forces the user through
        // the Reset path first, which is intentionally scary.
        var existing = await _profitTargets.GetSummaryAsync(uei);
        if (existing.HasTargets)
        {
            FlashMessage = "Profit targets already exist. Use the Reset button first if you want to start over.";
            FlashKind = "error";
            await LoadAllDataAsync();
            return Page();
        }

        try
        {
            var result = await _profitTargets.InitializeAsync(uei, payload);
            FlashMessage = $"Created {result.TargetsCreated:N0} profit targets totaling ${result.TotalProfitTargetSum:N2}.";
            FlashKind = "success";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profit-target initialize failed for UEI {UEI}", uei);
            FlashMessage = "Couldn't initialize profit targets. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResetProfitTargetsAsync(
        int uei,
        [FromForm] ProfitTargetsResetPayload payload)
    {
        var prep = await PrepareUeiAsync(uei);
        if (prep is not null) return prep;

        ProfitTargetsResetOpen = true;

        // Case-sensitive, exact-match confirmation. Anything else fails closed.
        if (payload.ConfirmText != "Destroy Profits")
        {
            FlashMessage = "Type \"Destroy Profits\" exactly (case-sensitive) to confirm the reset.";
            FlashKind = "error";
            await LoadAllDataAsync();
            return Page();
        }

        try
        {
            var deleted = await _profitTargets.DeleteAllAsync(uei);
            FlashMessage = $"Deleted {deleted:N0} profit targets. You can now initialize a fresh ladder.";
            FlashKind = "success";
            ProfitTargetsResetOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profit-target delete failed for UEI {UEI}", uei);
            FlashMessage = "Couldn't delete profit targets. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllDataAsync();
        return Page();
    }

    private async Task<IActionResult?> PrepareUeiAsync(int? requestedUei)
    {
        AvailableUeis = await GetMyUeisAsync();
        if (AvailableUeis.Count == 0) return null;

        CurrentUei = requestedUei ?? AvailableUeis[0];
        return await AssertUeiAccessAsync(CurrentUei);
    }

    private async Task LoadAllDataAsync()
    {
        if (AvailableUeis.Count == 0) return;
        var data = await _strategies.LoadPageDataAsync(CurrentUei);
        Strategies        = data.Strategies;
        AddableSymbols    = data.Addables;
        TreasurySettings  = await _userSettings.GetOrCreateAsync(CurrentUei);
        ProfitTargets     = await _profitTargets.GetSummaryAsync(CurrentUei);
    }

    private IActionResult Reject(string msg, int dcaGroupId)
    {
        FlashMessage = msg;
        FlashKind = "error";
        EditOpenForDCAGroupId = dcaGroupId;
        return LoadAndReturn();
    }

    private IActionResult RejectAdd(string msg)
    {
        FlashMessage = msg;
        FlashKind = "error";
        return LoadAndReturn();
    }

    private IActionResult LoadAndReturn()
    {
        // Synchronous wrapper for the simple validation-failure path.
        LoadAllDataAsync().GetAwaiter().GetResult();
        return Page();
    }

    private static bool IsValidOrderHours(int v) => OrderShelfLifeOptions.Any(o => o.Hours == v);
    private static bool IsValidBrokerTtl(int v)  => KrakenShelfLifeOptions.Any(o => o.Minutes == v);
}
