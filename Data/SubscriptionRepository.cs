namespace TraderPhil.V4.Web.Data;

/// <summary>
/// Per-user subscription state. Shape committed; source is not. Today this
/// comes from a stub returning placeholder values. When a real subscription
/// provider (Stripe, etc.) gets wired up, swap the DI registration to a real
/// SqlSubscriptionRepository (or ProviderBackedSubscriptionRepository) and the
/// Razor view doesn't change.
/// </summary>
public sealed class SubscriptionInfo
{
    public string    Provider     { get; init; } = "";       // "Demo Only", "Stripe", "Manual", etc.
    public DateTime? ExpiresOn    { get; init; }              // null when unknown
    public bool      AutoRenew    { get; init; }              // false = needs Extend; true = needs Cancel

    /// <summary>URL the user clicks to extend their subscription. # if not yet wired.</summary>
    public string    ExtendUrl    { get; init; } = "#";
    /// <summary>URL the user clicks to cancel. # if not yet wired.</summary>
    public string    CancelUrl    { get; init; } = "#";
    /// <summary>URL for the upgrade flow. # if not yet wired.</summary>
    public string    UpgradeUrl   { get; init; } = "#";
}

public interface ISubscriptionRepository
{
    Task<SubscriptionInfo> GetForUserAsync(int webUserId);
}

/// <summary>
/// Placeholder until real subscriptions land. Returns the same shape for every
/// user: Demo Only, expires 8/9/2081, auto-renew off (so the UI shows the
/// "Extend" button). Buttons go to "#" so they don't navigate anywhere.
/// </summary>
public sealed class StubSubscriptionRepository : ISubscriptionRepository
{
    public Task<SubscriptionInfo> GetForUserAsync(int webUserId)
    {
        return Task.FromResult(new SubscriptionInfo
        {
            Provider   = "Demo Only",
            ExpiresOn  = new DateTime(2081, 8, 9),
            AutoRenew  = false,
            ExtendUrl  = "#",
            CancelUrl  = "#",
            UpgradeUrl = "#",
        });
    }
}
