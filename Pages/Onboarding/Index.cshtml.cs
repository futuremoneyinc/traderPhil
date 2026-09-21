using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Kraken;
using TraderPhil.V4.Web.Models;
using TraderPhil.V4.Web.Services;

namespace TraderPhil.V4.Web.Pages.Onboarding;

/// <summary>
/// The /onboarding wizard. One page, many steps, driven by server-side state so
/// a user can close the tab and pick up exactly where they left off.
///
/// The whole thing is [AllowAnonymous] because the very first step (Welcome)
/// runs before the user has signed in — that's where the "Start My Free Trial"
/// CTA lands. Every handler that mutates state re-checks authentication and
/// bails to sign-in if it's missing.
///
/// Philosophy (Phil's, verbatim in the brief): don't overwhelm people with
/// "crypto automation" jargon right away. Get them connected, let them see the
/// system working, and build confidence before asking them to make decisions.
/// The step order encodes that: connect → verify → see it → tour → only THEN
/// talk money.
/// </summary>
[AllowAnonymous]
public class IndexModel : TraderPhilPageModel
{
    private readonly IOnboardingRepository    _onboarding;
    private readonly IAccountRepository       _account;
    private readonly IProfitTargetsRepository _profitTargets;
    private readonly IUserSettingsRepository  _userSettings;
    private readonly IStripeBillingService    _billing;
    private readonly ILogger<IndexModel>      _logger;

    public IndexModel(
        IWebUserRepository       users,
        IOnboardingRepository    onboarding,
        IAccountRepository       account,
        IProfitTargetsRepository profitTargets,
        IUserSettingsRepository  userSettings,
        IStripeBillingService    billing,
        ILogger<IndexModel>      logger) : base(users)
    {
        _onboarding    = onboarding;
        _account       = account;
        _profitTargets = profitTargets;
        _userSettings  = userSettings;
        _billing       = billing;
        _logger        = logger;
    }

    // ---- View state ---------------------------------------------------------
    public OnboardingStep        Step            { get; private set; } = OnboardingStep.Welcome;
    public OnboardingState?      State           { get; private set; }
    public int                   Uei             { get; private set; }
    public ApiKeyState?          ApiKey          { get; private set; }
    public KeyPermissionAudit?   LastAudit       { get; private set; }
    public IReadOnlyList<string> LastIpAllowlist { get; private set; } = Array.Empty<string>();
    public ProfitLadderPreview?  LadderPreview   { get; private set; }
    public Dictionary<string, PlanPricing?> PlanPricing { get; private set; } = new();

    public string? FlashMessage { get; private set; }
    public string? FlashKind    { get; private set; }

    public bool   IsAuthenticated => CurrentWebUserId is not null;
    public string FirstName
    {
        get
        {
            var name = CurrentDisplayName;
            if (string.IsNullOrWhiteSpace(name)) return "trader";
            var space = name.IndexOf(' ');
            return space > 0 ? name[..space] : name;
        }
    }

    // ========================================================================
    // GET
    // ========================================================================
    public async Task<IActionResult> OnGetAsync(int? step = null)
    {
        if (CurrentWebUserId is not int webUserId)
        {
            // Not signed in yet — the Welcome step invites them to authenticate.
            Step = OnboardingStep.Welcome;
            return Page();
        }

        var state = await _onboarding.GetOrCreateAsync(webUserId);
        State = state;

        // Already finished? Drop them into the live platform unless they've
        // explicitly asked to revisit a step (?step=).
        if (state.IsComplete && step is null)
            return RedirectToPage("/Performance/Index");

        Step = ResolveStep(state, step);
        await LoadForStepAsync(webUserId, state);
        return Page();
    }

    /// <summary>
    /// Picks the step to render: a requested step if it's valid and not ahead of
    /// where the user has actually reached, otherwise the furthest step reached.
    /// </summary>
    private static OnboardingStep ResolveStep(OnboardingState state, int? requested)
    {
        var furthest = state.Step;
        if (requested is int s
            && s >= (int)OnboardingStep.Goals
            && s <= (int)OnboardingStep.Plans
            && s <= (int)furthest)
        {
            return (OnboardingStep)s;
        }
        // Never render Welcome for an authenticated user, and never render Done.
        if (furthest <= OnboardingStep.Welcome) return OnboardingStep.Goals;
        if (furthest >= OnboardingStep.Done)    return OnboardingStep.Plans;
        return furthest;
    }

    private async Task LoadForStepAsync(int webUserId, OnboardingState state)
    {
        Uei = state.UEI ?? (await GetMyUeisAsync()).FirstOrDefault();

        if (Uei > 0 && Step is OnboardingStep.Connect or OnboardingStep.Verify)
            ApiKey = await _account.GetApiKeyStateAsync(Uei);

        if (Step == OnboardingStep.Goals)
            LadderPreview = BuildPreview(state);

        if (Step == OnboardingStep.Plans)
            await LoadPlanPricingAsync();
    }

    private async Task LoadPlanPricingAsync()
    {
        foreach (var p in OnboardingPlans.All)
            PlanPricing[p.Slug] = await _billing.GetPricingAsync(p.Slug);
    }

    private static ProfitLadderPreview BuildPreview(OnboardingState state)
    {
        // Use saved answers if the user has been here before, otherwise sensible
        // starting defaults so the preview isn't empty on first paint.
        var funding = state.FundingAmount ?? 1_000m;
        var growth  = state.GrowthSpeed   ?? ProfitTargetsGrowthOptions.Medium;
        var target  = state.TargetAmount  ?? 1_000_000m;
        var start   = OnboardingGoals.StartProfitFromFunding(funding);
        return ProfitLadderPreview.Compute(start, growth, target);
    }

    // ========================================================================
    // POST — Goals: provision the account and build the Profit Ladder
    // ========================================================================
    public async Task<IActionResult> OnPostGoalsAsync([FromForm] OnboardingGoalsPayload goals)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        var state = await _onboarding.GetOrCreateAsync(webUserId);
        State = state;
        Step  = OnboardingStep.Goals;

        // -- Validate the friendly answers --------------------------------
        if (!OnboardingGoals.IsValidGoal(goals.InvestmentGoal))
            return GoalsError(state, "Pick the goal that fits you best.");
        if (goals.FundingAmount < 100m)
            return GoalsError(state, "Enter how much you plan to fund with (at least $100).");
        if (goals.TargetAmount <= 0m)
            return GoalsError(state, "Choose a long-term profit goal.");

        var payload = OnboardingGoals.ToInitPayload(goals);

        // -- Re-validate against the proc's own preconditions -------------
        if (payload.StartProfit < 1m || payload.StartProfit > 10_000m)
            return GoalsError(state, "That funding amount produces an out-of-range first target. Try a different amount.");
        if (payload.LongTermProfitTarget <= payload.StartProfit)
            return GoalsError(state, "Your long-term goal needs to be larger than your first profit target.");

        try
        {
            // 1) Mint (or reuse) the trading account.
            Uei = await _onboarding.ProvisionUeiAsync(webUserId);

            // 2) Make sure a settings row exists for the worker + Strategy page.
            await _userSettings.GetOrCreateAsync(Uei);

            // 3) Remember what they told us.
            await _onboarding.SaveGoalsAsync(webUserId, goals);

            // 4) Build the ladder — but only if we haven't already (resume-safe).
            var existing = await _profitTargets.GetSummaryAsync(Uei);
            if (!existing.HasTargets)
                await _profitTargets.InitializeAsync(Uei, payload);

            await _onboarding.SetStepAsync(webUserId, OnboardingStep.Connect);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding Goals step failed for WebUser {WebUserID}", webUserId);
            return GoalsError(state, "Something went wrong setting up your ladder. The error has been logged — please try again.");
        }
    }

    private IActionResult GoalsError(OnboardingState state, string message)
    {
        FlashMessage  = message;
        FlashKind     = "error";
        Step          = OnboardingStep.Goals;
        LadderPreview = BuildPreview(state);
        return Page();
    }

    // ========================================================================
    // POST — Connect: verify the Kraken key, then save it
    // ========================================================================
    public async Task<IActionResult> OnPostSaveKeyAsync(string apiKey, string apiSecret)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        var state = await _onboarding.GetOrCreateAsync(webUserId);
        State = state;
        Step  = OnboardingStep.Connect;
        Uei   = state.UEI ?? await _onboarding.ProvisionUeiAsync(webUserId);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            return await ConnectError(webUserId, "Paste both your API key and your private key.");

        try
        {
            using var client = new KrakenDirectFundingClient(apiKey.Trim(), apiSecret.Trim(), _logger);
            var r = await client.GetApiKeyInfoAsync();

            if (!r.Success || r.Data is null)
                return await ConnectError(webUserId, $"Kraken wouldn't accept that key: {r.Error ?? "no response"}. Double-check you copied both values exactly.");

            LastAudit       = KrakenKeyPermissionPolicy.Evaluate(r.Data.Permissions);
            LastIpAllowlist = r.Data.IpAllowlist;

            if (!LastAudit.Passed)
            {
                var msg = LastAudit.ForbiddenGranted.Count > 0
                    ? "That key has permissions we won't accept (like Withdraw or Margin). Recreate it on Kraken with only the permissions in the checklist below."
                    : "That key is missing some required permissions. Enable the items marked below and paste it again.";
                return await ConnectError(webUserId, msg);
            }

            await _account.SaveVerifiedKeyAsync(
                Uei, apiKey.Trim(), apiSecret.Trim(),
                apiKeyName: r.Data.ApiKeyName,
                expiresAt:  r.Data.ValidUntilUtc,
                modifiedAt: r.Data.ModifiedTimeUtc);

            await _onboarding.SetStepAsync(webUserId, OnboardingStep.Verify);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding SaveKey failed for WebUser {WebUserID}", webUserId);
            return await ConnectError(webUserId, "We couldn't reach Kraken to check that key just now. Please try again in a moment.");
        }
    }

    private async Task<IActionResult> ConnectError(int webUserId, string message)
    {
        FlashMessage = message;
        FlashKind    = "error";
        Step         = OnboardingStep.Connect;
        if (Uei > 0) ApiKey = await _account.GetApiKeyStateAsync(Uei);
        return Page();
    }

    // ========================================================================
    // POST — Verify: re-test the saved key so the user watches it pass
    // ========================================================================
    public async Task<IActionResult> OnPostVerifyAsync()
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        var state = await _onboarding.GetOrCreateAsync(webUserId);
        State = state;
        Step  = OnboardingStep.Verify;
        Uei   = state.UEI ?? (await GetMyUeisAsync()).FirstOrDefault();

        var creds = Uei > 0 ? await _account.GetCredentialsAsync(Uei) : null;
        if (creds is null)
        {
            // No key on file — send them back a step to add one.
            await _onboarding.SetStepAsync(webUserId, OnboardingStep.Connect);
            return RedirectToPage(new { step = (int)OnboardingStep.Connect });
        }

        try
        {
            using var client = new KrakenDirectFundingClient(creds.Value.ApiKey, creds.Value.ApiSecret, _logger);
            var r = await client.GetApiKeyInfoAsync();

            if (r.Success && r.Data is not null)
            {
                LastAudit       = KrakenKeyPermissionPolicy.Evaluate(r.Data.Permissions);
                LastIpAllowlist = r.Data.IpAllowlist;
                await _account.RecordTestResultAsync(Uei, LastAudit.Passed, r.Data.ApiKeyName,
                                                     r.Data.ValidUntilUtc, r.Data.ModifiedTimeUtc);

                if (LastAudit.Passed)
                {
                    await _onboarding.SetStepAsync(webUserId, OnboardingStep.Lounge);
                    return RedirectToPage();
                }
            }
            else
            {
                await _account.RecordTestResultAsync(Uei, false, null, null, null);
            }

            FlashMessage = "The connection test didn't pass. Head back a step and re-check your key's permissions.";
            FlashKind    = "warning";
            ApiKey       = await _account.GetApiKeyStateAsync(Uei);
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding Verify failed for WebUser {WebUserID}", webUserId);
            FlashMessage = "We couldn't reach Kraken to verify just now. Try again in a moment.";
            FlashKind    = "error";
            ApiKey       = await _account.GetApiKeyStateAsync(Uei);
            return Page();
        }
    }

    // ========================================================================
    // POST — Continue: advance through the informational steps
    // ========================================================================
    public async Task<IActionResult> OnPostContinueAsync(int fromStep)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        var from = (OnboardingStep)fromStep;
        var next = from switch
        {
            OnboardingStep.Lounge => OnboardingStep.Tour,
            OnboardingStep.Tour   => OnboardingStep.Plans,
            _                     => OnboardingStep.Plans,
        };
        await _onboarding.SetStepAsync(webUserId, next);
        return RedirectToPage();
    }

    // ========================================================================
    // POST — Plans: choose a plan (or skip) and finish
    // ========================================================================
    public async Task<IActionResult> OnPostChoosePlanAsync(string plan)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        // "Skip for now" (or any non-tier value) finishes onboarding with no
        // subscription — they can subscribe later from the Account page.
        if (!OnboardingPlans.IsValidPlan(plan))
        {
            await _onboarding.CompleteAsync(webUserId, "none");
            return RedirectToPage("/Performance/Index");
        }

        // Stripe not wired up yet — don't block the flow.
        if (!_billing.IsConfigured)
        {
            await _onboarding.CompleteAsync(webUserId, plan);
            return RedirectToPage("/Performance/Index");
        }

        // Paid tier → hosted Stripe Checkout (subscription mode, with the trial).
        try
        {
            var baseUrl    = $"{Request.Scheme}://{Request.Host}";
            var successUrl = $"{baseUrl}/onboarding?handler=CheckoutComplete&plan={plan}&session_id={{CHECKOUT_SESSION_ID}}";
            var cancelUrl  = $"{baseUrl}/onboarding?step={(int)OnboardingStep.Plans}";
            var url = await _billing.CreateCheckoutSessionAsync(webUserId, plan, successUrl, cancelUrl);
            return Redirect(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checkout session creation failed for WebUser {WebUserID}, plan {Plan}", webUserId, plan);
            State        = await _onboarding.GetOrCreateAsync(webUserId);
            Step         = OnboardingStep.Plans;
            await LoadPlanPricingAsync();
            FlashMessage = "We couldn't open checkout just now. Please try again, or skip and subscribe later from your account.";
            FlashKind    = "warning";
            return Page();
        }
    }

    // Stripe redirects here after a successful Checkout. The subscription itself is
    // recorded by the webhook; we just mark onboarding finished and move on.
    public async Task<IActionResult> OnGetCheckoutCompleteAsync(string? plan = null)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToSignIn();

        var slug = OnboardingPlans.IsValidPlan(plan) ? plan! : OnboardingPlans.Basic;
        await _onboarding.CompleteAsync(webUserId, slug);
        return RedirectToPage("/Performance/Index");
    }

    private IActionResult RedirectToSignIn() =>
        Redirect("/Account/SignIn?returnUrl=%2FOnboarding");
}
