using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stripe;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Services;

/// <summary>
/// All Stripe API interaction lives here: creating Customers, hosted Checkout
/// sessions (subscription mode, with the free trial), Billing Portal sessions
/// for self-serve management, and handling the signed webhook that keeps
/// dbo.Subscriptions in sync. The rest of the app never touches the Stripe SDK.
/// </summary>
public interface IStripeBillingService
{
    bool IsConfigured { get; }

    /// <summary>Returns the Stripe Customer id for a web user, creating it if needed.</summary>
    Task<string> GetOrCreateCustomerAsync(int webUserId);

    /// <summary>Creates a hosted Checkout session for a paid plan and returns its URL.</summary>
    Task<string> CreateCheckoutSessionAsync(int webUserId, string planSlug, string successUrl, string cancelUrl);

    /// <summary>Live price for a tier (its product's default price), or null if unavailable.</summary>
    Task<PlanPricing?> GetPricingAsync(string planSlug);

    /// <summary>Creates a Billing Portal session and returns its URL.</summary>
    Task<string> CreatePortalSessionAsync(int webUserId, string returnUrl);

    /// <summary>Verifies and processes a Stripe webhook payload.</summary>
    Task HandleWebhookAsync(string json, string stripeSignatureHeader);
}

public sealed class StripeBillingService : IStripeBillingService
{
    private readonly StripeOptions _options;
    private readonly ISubscriptionRepository _subs;
    private readonly IWebUserRepository _users;
    private readonly ILogger<StripeBillingService> _logger;

    // slug -> resolved Price id (from a product's default price). A price change in
    // Stripe produces a new price id, so caching the resolution is safe.
    private readonly ConcurrentDictionary<string, string> _priceIdCache = new(StringComparer.OrdinalIgnoreCase);
    // slug -> (pricing, fetchedAtUtc), short TTL so dashboard price edits surface.
    private readonly ConcurrentDictionary<string, (PlanPricing Pricing, DateTime At)> _pricingCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PricingTtl = TimeSpan.FromMinutes(15);

    public StripeBillingService(
        IOptions<StripeOptions> options,
        ISubscriptionRepository subs,
        IWebUserRepository users,
        ILogger<StripeBillingService> logger)
    {
        _options = options.Value;
        _subs    = subs;
        _users   = users;
        _logger  = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("Stripe is not configured (Stripe:SecretKey missing).");
    }

    // ------------------------------------------------------------------ Customer
    public async Task<string> GetOrCreateCustomerAsync(int webUserId)
    {
        EnsureConfigured();

        var existing = await _subs.GetStripeCustomerIdAsync(webUserId);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var user = await _users.GetByIdAsync(webUserId)
            ?? throw new InvalidOperationException($"WebUser {webUserId} not found.");

        var customer = await new CustomerService().CreateAsync(new CustomerCreateOptions
        {
            Email = user.GoogleEmail,
            Name  = user.DisplayName,
            Metadata = new Dictionary<string, string> { ["WebUserID"] = webUserId.ToString() },
        });

        await _subs.SetStripeCustomerIdAsync(webUserId, customer.Id);
        _logger.LogInformation("Created Stripe customer {CustomerId} for WebUser {WebUserID}.", customer.Id, webUserId);
        return customer.Id;
    }

    // ------------------------------------------------------------------ Checkout
    public async Task<string> CreateCheckoutSessionAsync(int webUserId, string planSlug, string successUrl, string cancelUrl)
    {
        EnsureConfigured();

        var priceId = await ResolvePriceIdAsync(planSlug);

        var customerId = await GetOrCreateCustomerAsync(webUserId);

        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode                = "subscription",
            Customer            = customerId,
            ClientReferenceId   = webUserId.ToString(),
            AllowPromotionCodes = true,
            LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 },
            },
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
            {
                TrialPeriodDays = _options.TrialDays > 0 ? (long?)_options.TrialDays : null,
                Metadata = new Dictionary<string, string> { ["WebUserID"] = webUserId.ToString() },
            },
            SuccessUrl = successUrl,
            CancelUrl  = cancelUrl,
        };

        var session = await new Stripe.Checkout.SessionService().CreateAsync(options);
        return session.Url;
    }

    /// <summary>
    /// Resolves the Stripe Price id for a plan. The configured value is either a
    /// Price id (used as-is) or a Product id (we read its default price).
    /// </summary>
    private async Task<string> ResolvePriceIdAsync(string planSlug)
    {
        var reference = _options.ProductRefForPlan(planSlug)
            ?? throw new InvalidOperationException($"No Stripe product/price configured for plan '{planSlug}'.");

        if (reference.StartsWith("price_", StringComparison.OrdinalIgnoreCase))
            return reference;

        if (_priceIdCache.TryGetValue(planSlug, out var cached))
            return cached;

        var product = await new ProductService().GetAsync(reference);
        var priceId = product.DefaultPriceId
            ?? throw new InvalidOperationException(
                $"Stripe product '{reference}' (plan '{planSlug}') has no default price. " +
                "Set a default recurring price on the product in the Stripe dashboard.");

        _priceIdCache[planSlug] = priceId;
        return priceId;
    }

    public async Task<PlanPricing?> GetPricingAsync(string planSlug)
    {
        if (!_options.IsConfigured || _options.ProductRefForPlan(planSlug) is null) return null;

        if (_pricingCache.TryGetValue(planSlug, out var hit) && DateTime.UtcNow - hit.At < PricingTtl)
            return hit.Pricing;

        try
        {
            var priceId = await ResolvePriceIdAsync(planSlug);
            var price = await new PriceService().GetAsync(priceId);
            var pricing = new PlanPricing(
                AmountMinor: price.UnitAmount ?? 0,
                Currency:    price.Currency ?? "usd",
                Interval:    price.Recurring?.Interval ?? "month");
            _pricingCache[planSlug] = (pricing, DateTime.UtcNow);
            return pricing;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load Stripe pricing for plan {Plan}.", planSlug);
            return null;
        }
    }

    // -------------------------------------------------------------- Billing Portal
    public async Task<string> CreatePortalSessionAsync(int webUserId, string returnUrl)
    {
        EnsureConfigured();

        var customerId = await GetOrCreateCustomerAsync(webUserId);
        var session = await new Stripe.BillingPortal.SessionService().CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer  = customerId,
                ReturnUrl = returnUrl,
            });
        return session.Url;
    }

    // ------------------------------------------------------------------ Webhooks
    public async Task HandleWebhookAsync(string json, string stripeSignatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");

        // Throws StripeException on a bad signature — the endpoint turns that into a 400.
        var stripeEvent = EventUtility.ConstructEvent(json, stripeSignatureHeader, _options.WebhookSecret);

        switch (stripeEvent.Type)
        {
            case Events.CheckoutSessionCompleted:
            {
                if (stripeEvent.Data.Object is Stripe.Checkout.Session session)
                {
                    var webUserId = int.TryParse(session.ClientReferenceId, out var cid)
                        ? cid
                        : await ResolveWebUserIdByCustomerAsync(session.CustomerId);
                    if (webUserId is int id)
                    {
                        if (!string.IsNullOrEmpty(session.CustomerId))
                            await _subs.SetStripeCustomerIdAsync(id, session.CustomerId);

                        if (!string.IsNullOrEmpty(session.SubscriptionId))
                        {
                            var sub = await new SubscriptionService().GetAsync(session.SubscriptionId);
                            await SyncSubscriptionAsync(id, sub);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("checkout.session.completed with no resolvable WebUserID (session {Id}).", session.Id);
                    }
                }
                break;
            }

            case Events.CustomerSubscriptionCreated:
            case Events.CustomerSubscriptionUpdated:
            case Events.CustomerSubscriptionDeleted:
            {
                if (stripeEvent.Data.Object is Subscription sub)
                {
                    var webUserId = await ResolveWebUserIdByCustomerAsync(sub.CustomerId);
                    if (webUserId is int id) await SyncSubscriptionAsync(id, sub);
                    else _logger.LogWarning("subscription event for unknown customer {CustomerId}.", sub.CustomerId);
                }
                break;
            }

            case Events.InvoicePaymentFailed:
            case Events.InvoicePaid:
            {
                // Re-sync from the subscription so status + LapsedAt stay accurate.
                if (stripeEvent.Data.Object is Invoice invoice && !string.IsNullOrEmpty(invoice.SubscriptionId))
                {
                    var webUserId = await ResolveWebUserIdByCustomerAsync(invoice.CustomerId);
                    if (webUserId is int id)
                    {
                        var sub = await new SubscriptionService().GetAsync(invoice.SubscriptionId);
                        await SyncSubscriptionAsync(id, sub);
                    }
                }
                break;
            }

            default:
                _logger.LogDebug("Unhandled Stripe event type {Type}.", stripeEvent.Type);
                break;
        }
    }

    // ------------------------------------------------------------------ Helpers
    private async Task<int?> ResolveWebUserIdByCustomerAsync(string? customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId)) return null;
        return await _subs.GetWebUserIdByCustomerAsync(customerId);
    }

    private async Task SyncSubscriptionAsync(int webUserId, Subscription sub)
    {
        var item      = sub.Items?.Data?.FirstOrDefault();
        var priceId   = item?.Price?.Id;
        var productId = item?.Price?.ProductId;

        // Preserve the earliest lapse timestamp so Protection Mode's Day-count is
        // measured from when the trouble started, not from the latest webhook.
        var existing = await _subs.GetRecordAsync(webUserId);
        DateTime? lapsedAt = StripeSubscriptionRecord.IsGoodStanding(sub.Status)
            ? null
            : (existing?.LapsedAt ?? DateTime.UtcNow);

        var record = new StripeSubscriptionRecord
        {
            WebUserID            = webUserId,
            StripeCustomerId     = sub.CustomerId,
            StripeSubscriptionId = sub.Id,
            Status               = sub.Status,
            StripePriceId        = priceId,
            PlanSlug             = _options.PlanForProduct(productId) ?? existing?.PlanSlug,
            // NOTE (Stripe.net 48+): current_period_end moved to the subscription
            // item. If you upgrade, read `sub.Items.Data[0].CurrentPeriodEnd` here.
            CurrentPeriodEnd     = sub.CurrentPeriodEnd,
            TrialEnd             = sub.TrialEnd,
            CancelAtPeriodEnd    = sub.CancelAtPeriodEnd,
            LapsedAt             = lapsedAt,
        };

        await _subs.UpsertFromWebhookAsync(record);
        _logger.LogInformation("Synced subscription {SubId} for WebUser {WebUserID}: status={Status}, lapsedAt={Lapsed}.",
            sub.Id, webUserId, sub.Status, lapsedAt);
    }
}
