namespace TraderPhil.V4.Web.Models;

/// <summary>
/// The steps of the /onboarding wizard, in order. The integer values are
/// persisted in WebUserOnboarding.CurrentStep, so DO NOT renumber existing
/// members — only append.
///
/// Google authentication sits between Welcome and Goals but is not a stored
/// step: it's the redirect handshake with the existing /Account/SignIn flow.
/// By the time we persist any state the user is already authenticated, so the
/// first stored step is Goals.
/// </summary>
public enum OnboardingStep
{
    Welcome = 0,   // Anonymous landing — "Start My Free Trial" arrives here, then Google auth
    Goals   = 1,   // Investment goals + funding → computes the Profit Ladder
    Connect = 2,   // Kraken walkthrough video + 5-step guide + API key entry
    Verify  = 3,   // Connection verification (permission audit)
    Lounge  = 4,   // VIP Lounge welcome
    Tour    = 5,   // Walkthrough of the major sections
    Plans   = 6,   // Commander Quackers introduces the subscription options
    Done    = 7,   // Finished — user is dropped into the live platform
}

public static class OnboardingStepInfo
{
    /// <summary>The steps shown in the progress rail, in order (Welcome/auth is pre-rail).</summary>
    public static readonly (OnboardingStep Step, string Label)[] RailSteps =
    {
        (OnboardingStep.Goals,   "Goals"),
        (OnboardingStep.Connect, "Connect"),
        (OnboardingStep.Verify,  "Verify"),
        (OnboardingStep.Lounge,  "Welcome"),
        (OnboardingStep.Tour,    "Tour"),
        (OnboardingStep.Plans,   "Plans"),
    };
}

/// <summary>
/// A row from dbo.WebUserOnboarding. Mirrors the migration in
/// Data/Migrations/001_WebUserOnboarding.sql.
/// </summary>
public sealed class OnboardingState
{
    public int             WebUserID      { get; init; }
    public int             CurrentStep    { get; init; }
    public int?            UEI            { get; init; }
    public string?         InvestmentGoal { get; init; }
    public decimal?        FundingAmount  { get; init; }
    public decimal?        GrowthSpeed    { get; init; }
    public decimal?        TargetAmount   { get; init; }
    public string?         SelectedPlan   { get; init; }
    public bool            TourCompleted  { get; init; }
    public DateTime?       CompletedAt    { get; init; }
    public DateTime        CreatedAt      { get; init; }
    public DateTime        UpdatedAt      { get; init; }

    public OnboardingStep Step => (OnboardingStep)CurrentStep;
    public bool IsComplete => CompletedAt is not null;
    public bool HasGoals   => GrowthSpeed is not null && FundingAmount is not null;
}

/// <summary>The Goals-step form payload, before it's mapped onto the Profit Ladder proc.</summary>
public sealed class OnboardingGoalsPayload
{
    /// <summary>'income' | 'balanced' | 'aggressive' — maps to a growth speed.</summary>
    public string  InvestmentGoal { get; set; } = "";
    /// <summary>Dollars the user plans to fund with. Drives the first ladder rung.</summary>
    public decimal FundingAmount  { get; set; }
    /// <summary>Long-term profit goal in dollars (LongTermProfitTarget).</summary>
    public decimal TargetAmount   { get; set; }
}

/// <summary>
/// Maps the friendly Goals questions onto the three numbers that
/// usp_InitializeProfitTargets actually wants, and produces a preview of the
/// resulting ladder so we can show the user what they're signing up for
/// BEFORE we commit anything to the database.
/// </summary>
public static class OnboardingGoals
{
    // The three investment-goal answers and the growth speed each one implies.
    // Growth values match ProfitTargetsGrowthOptions (Slow / Medium / Fast).
    public static readonly (string Key, string Title, string Blurb, decimal Growth)[] GoalOptions =
    {
        ("income",     "Steady income",   "Smaller, more frequent wins. The gentlest ride.",        ProfitTargetsGrowthOptions.Slow),
        ("balanced",   "Balanced growth", "A measured mix of consistency and compounding.",         ProfitTargetsGrowthOptions.Medium),
        ("aggressive", "Aggressive growth","Bigger swings, faster compounding. Buckle up.",          ProfitTargetsGrowthOptions.Fast),
    };

    // Suggested long-term profit goals for the radio cards.
    public static readonly (decimal Amount, string Label)[] TargetOptions =
    {
        (100_000m,   "$100K"),
        (500_000m,   "$500K"),
        (1_000_000m, "$1M"),
    };

    public static decimal GrowthForGoal(string? goalKey)
    {
        foreach (var (key, _, _, growth) in GoalOptions)
            if (string.Equals(key, goalKey, StringComparison.OrdinalIgnoreCase))
                return growth;
        return ProfitTargetsGrowthOptions.Medium;   // sensible default
    }

    public static bool IsValidGoal(string? goalKey) =>
        GoalOptions.Any(o => string.Equals(o.Key, goalKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns the funding amount into the first ladder rung (StartProfit). We use
    /// ~0.25% of the funding, clamped to the proc's accepted range of $1–$10,000.
    /// The idea: your first profit target should feel achievable relative to what
    /// you put in, not a fixed dollar amount that's tiny for a whale and huge for
    /// a beginner.
    /// </summary>
    public static decimal StartProfitFromFunding(decimal fundingAmount)
    {
        var raw = Math.Round(fundingAmount * 0.0025m, 2);
        if (raw < 1m)      return 1m;
        if (raw > 10_000m) return 10_000m;
        return raw;
    }

    /// <summary>
    /// Builds the payload the ProfitTargets repository expects. Mirrors the
    /// Strategy page's Initialize form.
    /// </summary>
    public static ProfitTargetsInitPayload ToInitPayload(OnboardingGoalsPayload goals) => new()
    {
        StartProfit          = StartProfitFromFunding(goals.FundingAmount),
        GrowthPercentage     = GrowthForGoal(goals.InvestmentGoal),
        LongTermProfitTarget = goals.TargetAmount,
    };
}

/// <summary>
/// A cheap, read-only estimate of the ladder a set of goals would generate,
/// shown live in the Goals step. It replicates the arithmetic of
/// usp_InitializeProfitTargets with the same fixed parameters the
/// ProfitTargetsRepository uses (IndividualProfitCap = 10,000; numSeq = 10) so
/// the preview matches what actually gets created.
/// </summary>
public sealed class ProfitLadderPreview
{
    public decimal StartProfit    { get; init; }
    public decimal GrowthPercent  { get; init; }   // e.g. 0.0125
    public decimal TargetAmount   { get; init; }
    public int     RungCount      { get; init; }
    public decimal FirstRung      { get; init; }
    public decimal LargestRung    { get; init; }
    public decimal TotalProfit    { get; init; }
    public bool    Estimated      { get; init; }    // true when we hit the safety cap

    private const decimal IndividualProfitCap = 10_000m;
    private const int     NumSeq              = 10;
    private const int     SafetyCap           = 20_000;  // guard against runaway loops in the preview

    public static ProfitLadderPreview Compute(decimal startProfit, decimal growthPct, decimal targetAmount)
    {
        // Guard rails so the preview never divides by zero or loops forever.
        if (startProfit < 1m) startProfit = 1m;
        if (targetAmount <= startProfit)
        {
            return new ProfitLadderPreview
            {
                StartProfit = startProfit, GrowthPercent = growthPct, TargetAmount = targetAmount,
                RungCount = 0, FirstRung = startProfit, LargestRung = startProfit, TotalProfit = 0m,
            };
        }

        var perSeriesTarget = targetAmount / NumSeq;
        var multiplier      = 1m + growthPct;
        var maxProfit       = IndividualProfitCap;

        int     rungs   = 0;
        decimal sum     = 0m;
        decimal current = startProfit;
        decimal largest = startProfit;
        bool    capped  = false;

        while (sum <= perSeriesTarget)
        {
            var clamped = current > maxProfit ? maxProfit : current;
            sum    += clamped;
            largest = clamped > largest ? clamped : largest;
            rungs++;
            current = clamped * multiplier;

            if (rungs >= SafetyCap) { capped = true; break; }
        }

        return new ProfitLadderPreview
        {
            StartProfit   = startProfit,
            GrowthPercent = growthPct,
            TargetAmount  = targetAmount,
            RungCount     = rungs * NumSeq,
            FirstRung     = startProfit,
            LargestRung   = largest,
            TotalProfit   = sum * NumSeq,
            Estimated     = capped,
        };
    }
}

/// <summary>
/// The subscription tiers Commander Quackers presents in the final step. The
/// distinguishing entitlement is how many coins a user may run in their profile.
/// Dollar prices are NOT stored here — they're read live from Stripe (each tier's
/// product default price) so the UI can never drift from the dashboard.
/// </summary>
public sealed class OnboardingPlan
{
    public string   Slug            { get; init; } = "";
    public string   Name            { get; init; } = "";
    /// <summary>Coins allowed in the profile. null = unlimited.</summary>
    public int?     CoinLimit       { get; init; }
    public string   CoinLabel       { get; init; } = "";
    public string   AccountGuidance { get; init; } = "";
    public string   Tagline         { get; init; } = "";
    public string[] Features        { get; init; } = Array.Empty<string>();
    public bool     Highlighted     { get; init; }
}

public static class OnboardingPlans
{
    public const string Starter   = "starter";
    public const string Basic     = "basic";
    public const string Unlimited = "unlimited";

    public static readonly OnboardingPlan[] All =
    {
        new()
        {
            Slug = Starter, Name = "Starter", CoinLimit = 1, CoinLabel = "1 coin",
            AccountGuidance = "Best for accounts under $5,000",
            Tagline = "One coin, full system. Learn the ropes.",
            Features = new[]
            {
                "Trade 1 coin",
                "Your full Profit Ladder, running live",
                "Live trading on your own Kraken account",
                "Starts with a 14-day free trial",
            },
        },
        new()
        {
            Slug = Basic, Name = "Basic", CoinLimit = 3, CoinLabel = "3 coins", Highlighted = true,
            AccountGuidance = "Best for accounts $5,000–$20,000",
            Tagline = "Spread across three coins.",
            Features = new[]
            {
                "Trade up to 3 coins",
                "Everything in Starter",
                "Priority Protection Mode handling",
                "Starts with a 14-day free trial",
            },
        },
        new()
        {
            Slug = Unlimited, Name = "Unlimited", CoinLimit = null, CoinLabel = "Unlimited coins",
            AccountGuidance = "Best for accounts $15,000+",
            Tagline = "Every coin, no limits.",
            Features = new[]
            {
                "Trade unlimited coins",
                "Everything in Basic",
                "Highest concurrent position limits",
                "Starts with a 14-day free trial",
            },
        },
    };

    public static bool IsValidPlan(string? slug) =>
        All.Any(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public static OnboardingPlan? Get(string? slug) =>
        All.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Coins allowed for a plan slug. null = unlimited; 0 = unknown/no plan.</summary>
    public static int? CoinLimitForPlan(string? slug) => Get(slug)?.CoinLimit;
}

/// <summary>Live price for a tier, read from Stripe. Amount is in the smallest unit.</summary>
public sealed record PlanPricing(long AmountMinor, string Currency, string Interval)
{
    public string Display
    {
        get
        {
            var major = AmountMinor / 100m;
            var sym   = string.Equals(Currency, "usd", StringComparison.OrdinalIgnoreCase) ? "$"
                      : Currency.ToUpperInvariant() + " ";
            var each  = string.IsNullOrEmpty(Interval) ? "" : $" / {Interval}";
            return $"{sym}{major:0.##}{each}";
        }
    }
}

