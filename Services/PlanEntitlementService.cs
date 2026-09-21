using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Services;

/// <summary>What a user's plan entitles them to. Limit == null means unlimited.</summary>
public sealed record CoinEntitlement(int? Limit, string PlanLabel)
{
    public bool IsUnlimited => Limit is null;
}

public interface IPlanEntitlementService
{
    /// <summary>
    /// The coin allowance for a web user, derived from their active subscription
    /// tier (Starter 1 / Basic 3 / Unlimited ∞). Admins are unlimited. Users with
    /// no plan on file fall back to the configured Plans:FreeCoinLimit (default:
    /// unlimited, so legacy/admin-provisioned accounts are never blocked).
    /// </summary>
    Task<CoinEntitlement> GetCoinEntitlementAsync(int webUserId, bool isAdmin);
}

public sealed class PlanEntitlementService : IPlanEntitlementService
{
    private readonly ISubscriptionRepository _subs;
    private readonly int? _freeCoinLimit;

    public PlanEntitlementService(ISubscriptionRepository subs, IConfiguration config)
    {
        _subs = subs;
        // Absent => null => unlimited. This keeps existing users (who have no
        // Subscriptions row) working. Set Plans:FreeCoinLimit to 0 (or 1) once
        // you want to require a subscription before coins can be added.
        _freeCoinLimit = config.GetValue<int?>("Plans:FreeCoinLimit");
    }

    public async Task<CoinEntitlement> GetCoinEntitlementAsync(int webUserId, bool isAdmin)
    {
        if (isAdmin) return new CoinEntitlement(null, "Admin");

        var record = await _subs.GetRecordAsync(webUserId);
        var plan   = OnboardingPlans.Get(record?.PlanSlug);
        if (plan is not null)
            return new CoinEntitlement(plan.CoinLimit, plan.Name);

        return new CoinEntitlement(_freeCoinLimit, "Free");
    }
}
