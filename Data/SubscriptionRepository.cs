using System.Data.SqlClient;
using System.Globalization;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

/// <summary>
/// Per-user subscription state as the UI consumes it. The shape is stable; the
/// source is now Stripe (projected into dbo.Subscriptions by webhooks) via
/// <see cref="SqlSubscriptionRepository"/>. The <see cref="StubSubscriptionRepository"/>
/// is kept for local runs without Stripe wired up.
/// </summary>
public sealed class SubscriptionInfo
{
    public string    Provider     { get; init; } = "";       // "Free plan", "Captain", "Admiral", …
    public DateTime? ExpiresOn    { get; init; }              // period end / trial end; null when unknown
    public bool      AutoRenew    { get; init; }              // renews vs. set to cancel at period end

    /// <summary>URL the user clicks to extend their subscription. # if not yet wired.</summary>
    public string    ExtendUrl    { get; init; } = "#";
    /// <summary>URL the user clicks to cancel. # if not yet wired.</summary>
    public string    CancelUrl    { get; init; } = "#";
    /// <summary>URL for the upgrade flow. # if not yet wired.</summary>
    public string    UpgradeUrl   { get; init; } = "#";
}

public interface ISubscriptionRepository
{
    /// <summary>View model for the Account/onboarding subscription card.</summary>
    Task<SubscriptionInfo> GetForUserAsync(int webUserId);

    /// <summary>Full projected Stripe record, or null if the user has none yet.</summary>
    Task<StripeSubscriptionRecord?> GetRecordAsync(int webUserId);

    Task<string?> GetStripeCustomerIdAsync(int webUserId);

    /// <summary>Stores the Stripe Customer id on the web user (and seeds the Subscriptions row).</summary>
    Task SetStripeCustomerIdAsync(int webUserId, string customerId);

    /// <summary>Resolves a web user from a Stripe Customer id (webhook path).</summary>
    Task<int?> GetWebUserIdByCustomerAsync(string customerId);

    /// <summary>Upserts the projected subscription for a web user. Called from webhooks.</summary>
    Task UpsertFromWebhookAsync(StripeSubscriptionRecord record);
}

// ============================================================================
// SQL-backed implementation (Stripe as source of truth)
// ============================================================================

public sealed class SqlSubscriptionRepository : ISubscriptionRepository
{
    private readonly string _connectionString;

    public SqlSubscriptionRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
    }

    private const string RecordColumns = @"
        WebUserID, StripeCustomerId, StripeSubscriptionId, Status, PlanSlug,
        StripePriceId, CurrentPeriodEnd, TrialEnd, CancelAtPeriodEnd, LapsedAt";

    public async Task<StripeSubscriptionRecord?> GetRecordAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<StripeSubscriptionRecord>(
            $"SELECT {RecordColumns} FROM dbo.Subscriptions WHERE WebUserID = @id",
            new { id = webUserId });
    }

    public async Task<SubscriptionInfo> GetForUserAsync(int webUserId)
    {
        var r = await GetRecordAsync(webUserId);
        if (r is null || string.IsNullOrEmpty(r.StripeSubscriptionId))
        {
            // No paid subscription on file — the user is on the free trial path.
            return new SubscriptionInfo
            {
                Provider  = "Free plan",
                ExpiresOn = r?.TrialEnd,
                AutoRenew = false,
            };
        }

        var name = string.IsNullOrEmpty(r.PlanSlug)
            ? "Subscription"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.PlanSlug);

        return new SubscriptionInfo
        {
            Provider  = name,
            ExpiresOn = r.CurrentPeriodEnd ?? r.TrialEnd,
            AutoRenew = StripeSubscriptionRecord.IsGoodStanding(r.Status) && !r.CancelAtPeriodEnd,
        };
    }

    public async Task<string?> GetStripeCustomerIdAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT StripeCustomerId FROM dbo.WebUsers WHERE WebUserID = @id",
            new { id = webUserId });
    }

    public async Task SetStripeCustomerIdAsync(int webUserId, string customerId)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.WebUsers SET StripeCustomerId = @cust WHERE WebUserID = @id;

            MERGE dbo.Subscriptions AS tgt
            USING (SELECT @id AS WebUserID) AS src ON tgt.WebUserID = src.WebUserID
            WHEN MATCHED THEN
                UPDATE SET StripeCustomerId = @cust, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (WebUserID, StripeCustomerId, CreatedAt, UpdatedAt)
                VALUES (@id, @cust, GETUTCDATE(), GETUTCDATE());",
            new { id = webUserId, cust = customerId });
    }

    public async Task<int?> GetWebUserIdByCustomerAsync(string customerId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 WebUserID FROM dbo.WebUsers WHERE StripeCustomerId = @cust
            UNION ALL
            SELECT TOP 1 WebUserID FROM dbo.Subscriptions WHERE StripeCustomerId = @cust",
            new { cust = customerId });
    }

    public async Task UpsertFromWebhookAsync(StripeSubscriptionRecord r)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            MERGE dbo.Subscriptions AS tgt
            USING (SELECT @WebUserID AS WebUserID) AS src ON tgt.WebUserID = src.WebUserID
            WHEN MATCHED THEN UPDATE SET
                StripeCustomerId     = COALESCE(@StripeCustomerId, tgt.StripeCustomerId),
                StripeSubscriptionId = COALESCE(@StripeSubscriptionId, tgt.StripeSubscriptionId),
                Status               = @Status,
                PlanSlug             = COALESCE(@PlanSlug, tgt.PlanSlug),
                StripePriceId        = COALESCE(@StripePriceId, tgt.StripePriceId),
                CurrentPeriodEnd     = @CurrentPeriodEnd,
                TrialEnd             = @TrialEnd,
                CancelAtPeriodEnd    = @CancelAtPeriodEnd,
                LapsedAt             = @LapsedAt,
                UpdatedAt            = GETUTCDATE()
            WHEN NOT MATCHED THEN INSERT
                (WebUserID, StripeCustomerId, StripeSubscriptionId, Status, PlanSlug,
                 StripePriceId, CurrentPeriodEnd, TrialEnd, CancelAtPeriodEnd, LapsedAt,
                 CreatedAt, UpdatedAt)
            VALUES
                (@WebUserID, @StripeCustomerId, @StripeSubscriptionId, @Status, @PlanSlug,
                 @StripePriceId, @CurrentPeriodEnd, @TrialEnd, @CancelAtPeriodEnd, @LapsedAt,
                 GETUTCDATE(), GETUTCDATE());",
            new
            {
                r.WebUserID,
                r.StripeCustomerId,
                r.StripeSubscriptionId,
                r.Status,
                r.PlanSlug,
                r.StripePriceId,
                r.CurrentPeriodEnd,
                r.TrialEnd,
                r.CancelAtPeriodEnd,
                r.LapsedAt,
            });
    }
}

// ============================================================================
// Stub — used only when Stripe isn't configured (local dev). Returns a static
// "free plan" and no-ops the write paths.
// ============================================================================

public sealed class StubSubscriptionRepository : ISubscriptionRepository
{
    public Task<SubscriptionInfo> GetForUserAsync(int webUserId) =>
        Task.FromResult(new SubscriptionInfo { Provider = "Free plan", AutoRenew = false });

    public Task<StripeSubscriptionRecord?> GetRecordAsync(int webUserId) =>
        Task.FromResult<StripeSubscriptionRecord?>(null);

    public Task<string?> GetStripeCustomerIdAsync(int webUserId) => Task.FromResult<string?>(null);
    public Task SetStripeCustomerIdAsync(int webUserId, string customerId) => Task.CompletedTask;
    public Task<int?> GetWebUserIdByCustomerAsync(string customerId) => Task.FromResult<int?>(null);
    public Task UpsertFromWebhookAsync(StripeSubscriptionRecord record) => Task.CompletedTask;
}
