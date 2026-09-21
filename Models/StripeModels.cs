namespace TraderPhil.V4.Web.Models;

/// <summary>
/// Strongly-typed Stripe config, bound from the "Stripe" configuration section
/// (user-secrets in dev, environment/secret store in prod — appsettings*.json is
/// gitignored on purpose). Nothing here is a real secret at rest in the repo.
///
///   "Stripe": {
///     "SecretKey":      "sk_test_...",
///     "PublishableKey": "pk_test_...",
///     "WebhookSecret":  "whsec_...",
///     "TrialDays":      14,
///     "Prices": { "captain": "price_...", "admiral": "price_..." }
///   }
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string SecretKey      { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string WebhookSecret  { get; set; } = "";
    public int    TrialDays      { get; set; } = 14;

    /// <summary>Plan slug → Stripe Price id. Populated from config "Stripe:Prices".</summary>
    public Dictionary<string, string> Prices { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey) && SecretKey.StartsWith("sk_");

    /// <summary>The Price id for a plan slug, or null if the plan isn't a paid Stripe plan.</summary>
    public string? PriceIdForPlan(string? planSlug)
    {
        if (string.IsNullOrWhiteSpace(planSlug)) return null;
        return Prices.TryGetValue(planSlug, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
    }

    /// <summary>Reverse lookup: plan slug for a Stripe Price id (used when a webhook arrives).</summary>
    public string? PlanForPriceId(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        foreach (var kvp in Prices)
            if (string.Equals(kvp.Value, priceId, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        return null;
    }
}

/// <summary>
/// The app's projection of a Stripe subscription — one row per web user in
/// dbo.Subscriptions. Written by webhooks, read by the UI.
/// </summary>
public sealed class StripeSubscriptionRecord
{
    public int       WebUserID            { get; set; }
    public string?   StripeCustomerId     { get; set; }
    public string?   StripeSubscriptionId { get; set; }
    public string?   Status               { get; set; }
    public string?   PlanSlug             { get; set; }
    public string?   StripePriceId        { get; set; }
    public DateTime? CurrentPeriodEnd     { get; set; }
    public DateTime? TrialEnd             { get; set; }
    public bool      CancelAtPeriodEnd    { get; set; }
    public DateTime? LapsedAt             { get; set; }

    /// <summary>Stripe statuses that mean the subscription is in good standing.</summary>
    public static bool IsGoodStanding(string? status) =>
        string.Equals(status, "active",   StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "trialing", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stripe statuses that mean Protection Mode should be counting down.</summary>
    public static bool IsLapsed(string? status) =>
        string.Equals(status, "past_due", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "unpaid",   StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "incomplete_expired", StringComparison.OrdinalIgnoreCase);
}
