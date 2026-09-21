# Stripe Billing + Payments — setup

Hosted Checkout + Customer Portal + webhook-driven subscription state, wired into
the existing `ISubscriptionRepository`, the onboarding **Plans** step, and the
**Account** page. Stripe is the source of truth; `dbo.Subscriptions` is its
projection, kept in sync by `/webhooks/stripe`.

Nothing secret is committed — `appsettings*.json` is gitignored and all config
lives in user-secrets (dev) or your prod secret store.

---

## Plans

Tiers are sized by how many coins a user may run in their profile. Every tier
starts with a 14-day free trial (`Stripe:TrialDays`).

| Slug | Name | Coins | Guidance | Product ID (live) |
|------|------|-------|----------|-------------------|
| `starter`   | Starter   | 1         | under $5,000     | `prod_VIZiCY2lB0Y0mp` |
| `basic`     | Basic     | 3         | $5,000–$20,000   | `prod_VIZkQeUZeh0FR7` |
| `unlimited` | Unlimited | unlimited | $15,000+         | `prod_VIZmVxlR5dJJcj` |

**Important:** those are **Product** IDs, not Price IDs. The code resolves each
product's **default price** at runtime (for both Checkout and the displayed
price), so in the Stripe dashboard make sure **each product has a default,
recurring price set** (Product → Pricing → set as default). The displayed dollar
amounts come straight from Stripe — nothing is hardcoded.

> Test vs live: the IDs above are your live-mode products. In test mode the
> product IDs differ — set the test IDs in your dev user-secrets.

## 1. Verify the products in Stripe

For each product above: confirm it has a **recurring** price (monthly) and that
the price is marked **default**. That's all Checkout needs.

## 2. Run the database migrations

```sql
-- against the traderPhil DB, in order:
:r Data/Migrations/001_WebUserOnboarding.sql
:r Data/Migrations/002_StripeBilling.sql
```
`002` adds `WebUsers.StripeCustomerId` and the `dbo.Subscriptions` projection table.

## 3. Configure secrets

Run from the project directory (`UserSecretsId` is already set in the csproj):

```bash
dotnet user-secrets set "Stripe:SecretKey"        "sk_test_xxx"
dotnet user-secrets set "Stripe:PublishableKey"   "pk_test_xxx"
dotnet user-secrets set "Stripe:WebhookSecret"    "whsec_xxx"      # from step 4
dotnet user-secrets set "Stripe:TrialDays"        "14"

# Plan slug -> Stripe Product id (prod_...). A price id (price_...) also works.
dotnet user-secrets set "Stripe:Products:starter"   "prod_VIZiCY2lB0Y0mp"
dotnet user-secrets set "Stripe:Products:basic"     "prod_VIZkQeUZeh0FR7"
dotnet user-secrets set "Stripe:Products:unlimited" "prod_VIZmVxlR5dJJcj"
```

If `Stripe:SecretKey` is absent the app still runs — it falls back to a static
"Free plan" stub and the plan buttons say billing isn't set up yet.

## 4. Configure the webhook

The endpoint is `POST /webhooks/stripe` (anonymous, signature-verified — no CSRF).

**Local dev** with the Stripe CLI:
```bash
stripe listen --forward-to https://localhost:7086/webhooks/stripe
# copy the whsec_... it prints into Stripe:WebhookSecret (step 3)
```

**Production** — Dashboard → Developers → Webhooks → Add endpoint:
`https://app.traderphil.net/webhooks/stripe`, then subscribe to:

- `checkout.session.completed`
- `customer.subscription.created`
- `customer.subscription.updated`
- `customer.subscription.deleted`
- `invoice.paid`
- `invoice.payment_failed`

Copy that endpoint's signing secret into `Stripe:WebhookSecret`.

## 5. Build & verify

```bash
dotnet restore      # pulls Stripe.net (pinned to 47.* — see the csproj note)
dotnet build
dotnet run
```

Walk the flow: `/onboarding` → Goals → connect Kraken → … → **Plans**.
- Pick **Starter / Basic / Unlimited** → hosted Checkout (test card `4242 4242 4242 4242`)
  → returns to the app; the webhook fills in `dbo.Subscriptions`.
- "I'll choose later" finishes onboarding with no subscription.
- **Account** page → "Manage billing" opens the Customer Portal (cancel / update card).

---

## Coin limits (enforced)

The tier → coin allowance lives in `OnboardingPlans` (`CoinLimit`: Starter 1,
Basic 3, Unlimited = unlimited). It's enforced on the Strategy page's "add a coin"
path:

- `PlanEntitlementService` resolves a user's limit from their active subscription
  (Starter/Basic/Unlimited; admins are unlimited).
- The Strategy page shows "N / M coins used" and, at the cap, swaps "+ Add a coin"
  for an **Upgrade** link and blocks the add form.
- `StrategyRepository.CreateStrategyAsync` re-checks the limit **atomically inside
  the Serializable create transaction**, so the cap can't be bypassed by racing
  requests or a direct POST.

**Users with no subscription** (legacy/admin-provisioned accounts, or someone who
skipped the plan step) default to **unlimited**, so nothing breaks for existing
users. To require a subscription before any coin can be added, set a floor:

```bash
dotnet user-secrets set "Plans:FreeCoinLimit" "0"   # or "1" for a free single-coin tier
```

`Plans:FreeCoinLimit` unset = unlimited for no-plan users; `0` = must subscribe.

## How Protection Mode connects

When a subscription goes `past_due` / `canceled` / `unpaid`, the webhook stamps
`Subscriptions.LapsedAt`. `Services/ProtectionModePolicy.cs` turns the age of that
timestamp into a phase:

| Day | Phase | Behavior |
|-----|-------|----------|
| 0 | HoldNewPositions | Stop opening new parent positions; keep managing/closing existing ones |
| 5 | Reminder | Nudge to resubscribe |
| 13 | FinalWarning | Last call before cleanup |
| 14 | Deactivated | Remove API keys + clean up user data |

The **web app** surfaces this (the Account page shows a Protection Mode banner while
lapsed). **Enforcement** — halting new parents at Day 0 and removing keys at Day 14 —
belongs to the trading worker, which should read `Subscriptions.Status` / `LapsedAt`
and call the same `ProtectionModePolicy`. Day-14 key removal can reuse the existing
`IAccountRepository.SoftDeleteKeyAsync` + the `DeleteKeyAfter` / GarbageCollector path.

## Stripe.net version note

`Stripe.net` is pinned to `47.*`. On **48+**, `current_period_end` moved to the
subscription item — update the one line flagged in
`Services/StripeBillingService.cs → SyncSubscriptionAsync` to read
`sub.Items.Data[0].CurrentPeriodEnd`.
