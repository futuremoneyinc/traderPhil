using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Kraken;

namespace TraderPhil.V4.Web.Pages.Account;

[Authorize]
public class IndexModel : TraderPhilPageModel
{
    private readonly IAccountRepository _account;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IWebUserRepository users,
        IAccountRepository account,
        ISubscriptionRepository subscriptions,
        ILogger<IndexModel> logger) : base(users)
    {
        _account = account;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    // -- View state --
    public AccountProfile?  Profile         { get; private set; }
    public int              CurrentUei      { get; private set; }
    public ApiKeyState?     ApiKey          { get; private set; }
    public string           ProgressFreq    { get; private set; } = "Never";
    public SubscriptionInfo Subscription    { get; private set; } = new();

    public string? FlashMessage { get; private set; }
    public string? FlashKind    { get; private set; }

    public bool AddKeyFormOpen { get; private set; }

    /// <summary>Last permission audit, populated by Test Connection.</summary>
    public KeyPermissionAudit? LastAudit { get; private set; }
    /// <summary>IP allowlist returned by the most recent test, for the advanced details expander.</summary>
    public IReadOnlyList<string> LastIpAllowlist { get; private set; } = Array.Empty<string>();

    // -- Convenience properties (model-side to avoid Razor RZ1010 from @{} blocks) --
    public string SubscriptionActionLabel =>
        Subscription.AutoRenew ? "Cancel subscription" : "Extend subscription";
    public string SubscriptionActionUrl =>
        Subscription.AutoRenew ? Subscription.CancelUrl : Subscription.ExtendUrl;
    public string SubscriptionAutoRenewLabel =>
        Subscription.AutoRenew ? "On" : "Off";
    public string ApiKeyTestBadgeClass => ApiKey?.LastApiTestResult switch
    {
        "PASS" => "tp-test-badge pass",
        "FAIL" => "tp-test-badge fail",
        _      => "tp-test-badge untested"
    };
    public string ApiKeyTestBadgeLabel => ApiKey?.LastApiTestResult switch
    {
        "PASS" => "Connected",
        "FAIL" => "Test failed",
        _      => "Never tested"
    };

    /// <summary>
    /// CSS class for the Replace-key form's collapsible container. Kept on the
    /// page model rather than in a Razor @{ } block to avoid RZ1010 from
    /// code blocks sandwiched between markup in nested if/else structures.
    /// </summary>
    public string ApiReplaceFormClass =>
        AddKeyFormOpen ? "tp-api-replace open" : "tp-api-replace";

    // ========================================================================
    // GET
    // ========================================================================

    public async Task<IActionResult> OnGetAsync()
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");

        var ueis = await GetMyUeisAsync();
        if (ueis.Count == 0)
        {
            // No UEI grants - user can still see profile + privacy + subscription stub,
            // but API key + progress-report sections will be empty-stated.
            Profile      = await _account.GetProfileAsync(webUserId);
            Subscription = await _subscriptions.GetForUserAsync(webUserId);
            return Page();
        }

        // For the foreseeable future the business model is one UEI per WebUser.
        // Per Phil's note in the design spec.
        CurrentUei = ueis[0];

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Privacy toggle
    // ========================================================================

    public async Task<IActionResult> OnPostSetPrivacyAsync(bool displayStatsPublic)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");

        try
        {
            await _account.SetDisplayStatsPublicAsync(webUserId, displayStatsPublic);
            FlashMessage = displayStatsPublic
                ? "Your stats are now visible publicly."
                : "Your stats are now private.";
            FlashKind = "success";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set privacy for WebUser {WebUserID}", webUserId);
            FlashMessage = "Couldn't update privacy. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Progress report frequency
    // ========================================================================

    public async Task<IActionResult> OnPostSetProgressFrequencyAsync(int uei, string frequency)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");
        var deny = await AssertUeiAccessAsync(uei);
        if (deny is not null) return deny;

        try
        {
            await _account.SetProgressReportFrequencyAsync(uei, frequency);
            FlashMessage = frequency == "Never"
                ? "Progress reports turned off."
                : $"Progress reports set to {frequency.ToLowerInvariant()}.";
            FlashKind = "success";
        }
        catch (ArgumentException)
        {
            FlashMessage = "Pick one of Daily, Weekly, Monthly, or Never.";
            FlashKind = "error";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set progress frequency for UEI {UEI}", uei);
            FlashMessage = "Couldn't update the schedule. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Test Connection (existing key)
    // ========================================================================

    public async Task<IActionResult> OnPostTestKeyAsync(int uei)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");
        var deny = await AssertUeiAccessAsync(uei);
        if (deny is not null) return deny;

        var creds = await _account.GetCredentialsAsync(uei);
        if (creds is null)
        {
            FlashMessage = "No API key on file to test. Add one below.";
            FlashKind = "warning";
            AddKeyFormOpen = true;
            await LoadAllAsync(webUserId);
            return Page();
        }

        try
        {
            using var client = new KrakenDirectFundingClient(creds.Value.ApiKey, creds.Value.ApiSecret, _logger);
            var r = await client.GetApiKeyInfoAsync();

            if (!r.Success || r.Data is null)
            {
                LastAudit = new KeyPermissionAudit { Passed = false };
                await _account.RecordTestResultAsync(uei, passed: false, apiKeyName: null,
                                                     expiresAt: null, modifiedAt: null);
                FlashMessage = $"Test failed: {r.Error ?? "Kraken returned no data"}.";
                FlashKind = "error";
                await LoadAllAsync(webUserId);
                return Page();
            }

            LastAudit       = KrakenKeyPermissionPolicy.Evaluate(r.Data.Permissions);
            LastIpAllowlist = r.Data.IpAllowlist;

            await _account.RecordTestResultAsync(
                uei,
                passed: LastAudit.Passed,
                apiKeyName: r.Data.ApiKeyName,
                expiresAt: r.Data.ValidUntilUtc,
                modifiedAt: r.Data.ModifiedTimeUtc);

            if (LastAudit.Passed)
            {
                FlashMessage = "Connection successful. Your key has the right permissions.";
                FlashKind = "success";
            }
            else
            {
                FlashMessage = LastAudit.ForbiddenGranted.Count > 0
                    ? "Your key has dangerous permissions enabled. Recreate it on Kraken with only the permissions listed below."
                    : "Your key is missing required permissions. See the checklist below for what to enable.";
                FlashKind = "error";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test Connection failed for UEI {UEI}", uei);
            await _account.RecordTestResultAsync(uei, passed: false, apiKeyName: null,
                                                  expiresAt: null, modifiedAt: null);
            FlashMessage = "Test failed: unexpected error. The details have been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Save new / replacement key (with inline test)
    // ========================================================================

    public async Task<IActionResult> OnPostSaveKeyAsync(int uei, string apiKey, string apiSecret)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");
        var deny = await AssertUeiAccessAsync(uei);
        if (deny is not null) return deny;

        AddKeyFormOpen = true;  // keep form expanded if anything fails

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            FlashMessage = "Both the API key and the secret are required.";
            FlashKind = "error";
            await LoadAllAsync(webUserId);
            return Page();
        }

        // Run the test BEFORE committing anything to data1/data2. If permissions
        // fail, nothing gets written.
        try
        {
            using var client = new KrakenDirectFundingClient(apiKey, apiSecret, _logger);
            var r = await client.GetApiKeyInfoAsync();
            if (!r.Success || r.Data is null)
            {
                FlashMessage = $"Could not validate that key with Kraken: {r.Error ?? "no data returned"}.";
                FlashKind = "error";
                await LoadAllAsync(webUserId);
                return Page();
            }

            LastAudit       = KrakenKeyPermissionPolicy.Evaluate(r.Data.Permissions);
            LastIpAllowlist = r.Data.IpAllowlist;

            if (!LastAudit.Passed)
            {
                FlashMessage = LastAudit.ForbiddenGranted.Count > 0
                    ? "That key has dangerous permissions enabled and was not saved. Recreate it on Kraken without Withdraw funds or Margin trading."
                    : "That key is missing required permissions and was not saved. Enable the items marked below and try again.";
                FlashKind = "error";
                await LoadAllAsync(webUserId);
                return Page();
            }

            // Permissions OK - commit.
            await _account.SaveVerifiedKeyAsync(
                uei, apiKey, apiSecret,
                apiKeyName: r.Data.ApiKeyName,
                expiresAt:  r.Data.ValidUntilUtc,
                modifiedAt: r.Data.ModifiedTimeUtc);

            FlashMessage = "Key saved and verified. Your previous key is preserved for 24 hours in case you need to roll back.";
            FlashKind = "success";
            AddKeyFormOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveKey failed for UEI {UEI}", uei);
            FlashMessage = "Couldn't save the key. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Soft-delete current key
    // ========================================================================

    public async Task<IActionResult> OnPostDeleteKeyAsync(int uei)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");
        var deny = await AssertUeiAccessAsync(uei);
        if (deny is not null) return deny;

        try
        {
            await _account.SoftDeleteKeyAsync(uei);
            FlashMessage = "Key deleted. New trades will stop on the next worker cycle. Existing Kraken orders are unaffected. You have 24 hours to undo.";
            FlashKind = "success";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteKey failed for UEI {UEI}", uei);
            FlashMessage = "Couldn't delete the key. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================
    // POST - Undo delete (restore from data3/data4)
    // ========================================================================

    public async Task<IActionResult> OnPostUndoDeleteAsync(int uei)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");
        var deny = await AssertUeiAccessAsync(uei);
        if (deny is not null) return deny;

        try
        {
            var restored = await _account.UndoDeleteAsync(uei);
            if (restored)
            {
                FlashMessage = "Key restored. Trading will resume on the next worker cycle.";
                FlashKind = "success";
            }
            else
            {
                FlashMessage = "Couldn't restore the key. The 24-hour rollback window may have expired.";
                FlashKind = "warning";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoDelete failed for UEI {UEI}", uei);
            FlashMessage = "Couldn't restore the key. The error has been logged.";
            FlashKind = "error";
        }

        await LoadAllAsync(webUserId);
        return Page();
    }

    // ========================================================================

    private async Task LoadAllAsync(int webUserId)
    {
        Profile      = await _account.GetProfileAsync(webUserId);
        Subscription = await _subscriptions.GetForUserAsync(webUserId);

        if (CurrentUei > 0)
        {
            ApiKey       = await _account.GetApiKeyStateAsync(CurrentUei);
            ProgressFreq = await _account.GetProgressReportFrequencyAsync(CurrentUei);
        }
    }
}
