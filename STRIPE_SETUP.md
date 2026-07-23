# Stripe Billing + Payments — setup

Hosted Checkout + Customer Portal + webhook-driven subscription state, wired into
the existing `ISubscriptionRepository`, the onboarding **Plans** step, and the
**Account** page. Stripe is the source of truth; `dbo.Subscriptions` is its
projection, kept in sync by `/webhooks/stripe`.

Nothing secret is committed — `appsettings*.json` is gitignored and all config
lives in user-secrets (dev) or your prod secret store.

---

## 1. Create the Products & Prices in the Stripe Dashboard

Create two **recurring** prices (Test mode first):

| Plan | Product name | Price | Billing | Copy the Price ID |
|------|--------------|-------|---------|-------------------|
| Captain | TraderPhil Captain | $49.00 | Monthly | `price_...` |
| Admiral | TraderPhil Admiral | $149.00 | Monthly | `price_...` |

The 14-day free trial is applied by the code (`Stripe:TrialDays`), so you do **not**
need a separate trial product. The "Free Trial" option in onboarding takes no card
and creates no Stripe object — it just lets the user in.

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
dotnet user-secrets set "Stripe:SecretKey"       "sk_test_xxx"
dotnet user-secrets set "Stripe:PublishableKey"  "pk_test_xxx"
dotnet user-secrets set "Stripe:WebhookSecret"   "whsec_xxx"      # from step 4
dotnet user-secrets set "Stripe:TrialDays"       "14"
dotnet user-secrets set "Stripe:Prices:captain"  "price_xxx"
dotnet user-secrets set "Stripe:Prices:admiral"  "price_xxx"
```

If `Stripe:SecretKey` is absent the app still runs — it falls back to a static
"Free plan" stub and the upgrade buttons say billing isn't set up yet.

## 4. Configure the webhook

The endpoint is `POST /webhooks/stripe` (anonymous, signature-verified — no CSRF).

**Local dev** with the Stripe CLI:
```bash
stripe listen --forward-to https://localhost:7086/webhooks/stripe
# copy the whsec_... it prints into Stripe:WebhookSecret (step 3)
```

**Production** — Dashboard → Developers → Webhooks → Add endpoint:
`https://app.traderphil.net/webhooks/stripe`, then subscribe to these events:

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
- "Start my free trial" → completes onboarding, no card.
- "Choose Captain/Admiral" → hosted Checkout (use test card `4242 4242 4242 4242`)
  → returns to the app; the webhook fills in `dbo.Subscriptions`.
- **Account** page → "Manage billing" opens the Customer Portal (cancel / update card).

---

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
