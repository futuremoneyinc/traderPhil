using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Kraken;

namespace TraderPhil.V4.Web.Pages.Funding;

public class IndexModel : TraderPhilPageModel
{
    private readonly IFundingRepository _funding;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IWebUserRepository users,
        IFundingRepository funding,
        ILogger<IndexModel> logger) : base(users)
    {
        _funding = funding;
        _logger = logger;
    }

    public int CurrentUei { get; private set; }
    public IReadOnlyList<int> AvailableUeis { get; private set; } = Array.Empty<int>();

    public bool EmergencyStopEnabled { get; private set; }
    public IReadOnlyList<DepositableCoin> DepositCoins { get; private set; } = Array.Empty<DepositableCoin>();

    public string? FlashMessage { get; private set; }
    public string? FlashKind    { get; private set; }

    // Convenience properties so the Razor view doesn't need a @{ } block
    // sandwiched between markup sections - that pattern hits a Razor parser
    // edge case (RZ1010) in deeply nested if/else structures.
    public string EmergencyStopPillClass =>
        EmergencyStopEnabled ? "tp-ladder-pill active tp-pill-danger" : "tp-ladder-pill inactive";

    public string EmergencyStopButtonLabel =>
        EmergencyStopEnabled ? "Disengage Emergency Stop" : "Engage Emergency Stop";

    public string EmergencyStopButtonClass =>
        EmergencyStopEnabled ? "btn tp-estop-btn-resume" : "btn tp-estop-btn-engage";

    public string EmergencyStopPillLabel =>
        EmergencyStopEnabled ? "Engaged" : "Trading active";

    public async Task<IActionResult> OnGetAsync(int? uei = null)
    {
        AvailableUeis = await GetMyUeisAsync();
        if (AvailableUeis.Count == 0) return Page();

        CurrentUei = uei ?? AvailableUeis[0];
        var denied = await AssertUeiAccessAsync(CurrentUei);
        if (denied is not null) return denied;

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleEmergencyStopAsync(int uei)
    {
        AvailableUeis = await GetMyUeisAsync();
        if (AvailableUeis.Count == 0) return Forbid();

        CurrentUei = uei;
        var denied = await AssertUeiAccessAsync(CurrentUei);
        if (denied is not null) return denied;

        try
        {
            var newState = await _funding.ToggleEmergencyStopAsync(uei);
            if (newState is null)
            {
                FlashMessage = "Couldn't toggle the emergency stop - no settings row exists for this account. Open the Strategy page once to seed your settings, then try again.";
                FlashKind = "error";
            }
            else if (newState.Value)
            {
                FlashMessage = "Emergency stop ENGAGED. New trades will not be placed until you turn it off.";
                FlashKind = "success";
            }
            else
            {
                FlashMessage = "Emergency stop disengaged. Trading resumed.";
                FlashKind = "success";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle emergency stop for UEI {UEI}", uei);
            FlashMessage = "Couldn't toggle the emergency stop. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllDataAsync();
        return Page();
    }

    /// <summary>
    /// AJAX endpoint: list deposit methods (networks) for an asset.
    /// No server-side caching - the browser caches results in localStorage
    /// (7 days per Phil's spec). The server is a thin authenticated proxy.
    /// </summary>
    public async Task<IActionResult> OnGetDepositMethodsAsync(int uei, string asset)
    {
        var denied = await AssertUeiAccessAsync(uei);
        if (denied is not null) return new JsonResult(new { error = "forbidden" });
        if (string.IsNullOrWhiteSpace(asset)) return new JsonResult(new { error = "asset required" });

        var creds = await _funding.GetCredentialsAsync(uei);
        if (creds is null) return new JsonResult(new { error = "No Kraken API credentials configured for this account." });

        try
        {
            using var client = new KrakenDirectFundingClient(creds.ApiKey, creds.ApiSecret, _logger);
            var r = await client.GetDepositMethodsAsync(asset);
            if (!r.Success || r.Data is null)
            {
                return new JsonResult(new { error = r.Error ?? "Could not fetch deposit methods from Kraken." });
            }
            return new JsonResult(new { methods = r.Data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deposit methods fetch failed for UEI {UEI} asset {Asset}", uei, asset);
            return new JsonResult(new { error = "Unexpected error." });
        }
    }

    /// <summary>
    /// AJAX endpoint: deposit addresses for an asset+method.
    /// Browser handles caching (1 hour per Phil's spec). When generate=true the
    /// browser also bypasses its own cache and asks Kraken to mint a new address.
    /// </summary>
    public async Task<IActionResult> OnGetDepositAddressesAsync(int uei, string asset, string method, bool generate = false)
    {
        var denied = await AssertUeiAccessAsync(uei);
        if (denied is not null) return new JsonResult(new { error = "forbidden" });
        if (string.IsNullOrWhiteSpace(asset))  return new JsonResult(new { error = "asset required" });
        if (string.IsNullOrWhiteSpace(method)) return new JsonResult(new { error = "method required" });

        var creds = await _funding.GetCredentialsAsync(uei);
        if (creds is null) return new JsonResult(new { error = "No Kraken API credentials configured for this account." });

        try
        {
            using var client = new KrakenDirectFundingClient(creds.ApiKey, creds.ApiSecret, _logger);
            var r = await client.GetDepositAddressesAsync(asset, method, generateNew: generate);
            if (!r.Success || r.Data is null)
            {
                return new JsonResult(new { error = r.Error ?? "Could not fetch deposit address from Kraken." });
            }
            return new JsonResult(new { addresses = r.Data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deposit address fetch failed for UEI {UEI} asset {Asset} method {Method}", uei, asset, method);
            return new JsonResult(new { error = "Unexpected error." });
        }
    }

    private async Task LoadAllDataAsync()
    {
        if (AvailableUeis.Count == 0) return;
        EmergencyStopEnabled = await _funding.GetEmergencyStopAsync(CurrentUei);
        DepositCoins         = await _funding.GetDepositableCoinsAsync(CurrentUei);
    }
}
