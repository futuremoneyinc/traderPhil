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
///     "Products": { "starter": "prod_…", "basic": "prod_…", "unlimited": "prod_…" }
///   }
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string SecretKey      { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string WebhookSecret  { get; set; } = "";
    public int    TrialDays      { get; set; } = 14;

    /// <summary>
    /// Plan slug → Stripe reference. Each value may be a Product id ("prod_…", the
    /// common case — its default price is resolved at runtime) or an explicit
    /// Price id ("price_…"). Populated from config "Stripe:Products".
    /// </summary>
    public Dictionary<string, string> Products { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey) && SecretKey.StartsWith("sk_");

    /// <summary>The configured Stripe reference (prod_… or price_…) for a plan slug.</summary>
    public string? ProductRefForPlan(string? planSlug)
    {
        if (string.IsNullOrWhiteSpace(planSlug)) return null;
        return Products.TryGetValue(planSlug, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
    }

    /// <summary>Reverse lookup: plan slug for a Stripe Product id (used when a webhook arrives).</summary>
    public string? PlanForProduct(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        foreach (var kvp in Products)
            if (string.Equals(kvp.Value, productId, StringComparison.OrdinalIgnoreCase))
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
