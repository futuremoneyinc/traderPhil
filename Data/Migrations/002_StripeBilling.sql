-- =============================================================================
-- 002_StripeBilling.sql
--
-- Stripe Billing state. Two changes:
--   1. WebUsers.StripeCustomerId — the Customer we create per web user.
--   2. Subscriptions — a projection of the Stripe subscription, kept in sync by
--      the /webhooks/stripe endpoint. This table is the source of truth the app
--      reads from (ISubscriptionRepository); Stripe is the source of truth that
--      writes to it via webhooks. One active subscription per web user.
--
-- Idempotent: safe to run more than once. Apply against the traderPhil DB.
-- =============================================================================
USE [traderPhil];
GO

IF COL_LENGTH('dbo.WebUsers', 'StripeCustomerId') IS NULL
BEGIN
    ALTER TABLE [dbo].[WebUsers] ADD [StripeCustomerId] [varchar](64) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Subscriptions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[Subscriptions](
        [WebUserID]             [int]          NOT NULL,
        [StripeCustomerId]      [varchar](64)  NULL,
        [StripeSubscriptionId]  [varchar](64)  NULL,

        -- Stripe status verbatim: trialing | active | past_due | canceled |
        -- unpaid | incomplete | incomplete_expired | paused.
        [Status]                [varchar](32)  NULL,

        -- Our plan slug (starter | basic | unlimited) resolved from the product id.
        [PlanSlug]              [varchar](40)  NULL,
        [StripePriceId]         [varchar](64)  NULL,

        [CurrentPeriodEnd]      [datetime]     NULL,
        [TrialEnd]              [datetime]     NULL,
        [CancelAtPeriodEnd]     [bit]          NOT NULL CONSTRAINT [DF_Subscriptions_CancelAtPeriodEnd] DEFAULT (0),

        -- When the subscription stopped being good-standing (past_due/canceled/
        -- unpaid). NULL while healthy. ProtectionModePolicy counts days from here
        -- to decide Day 0 / 5 / 13 / 14 behavior. Set and cleared by webhooks.
        [LapsedAt]              [datetime]     NULL,

        [CreatedAt]             [datetime]     NOT NULL CONSTRAINT [DF_Subscriptions_CreatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedAt]             [datetime]     NOT NULL CONSTRAINT [DF_Subscriptions_UpdatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_Subscriptions] PRIMARY KEY CLUSTERED ([WebUserID] ASC),
        CONSTRAINT [FK_Subscriptions_WebUsers]
            FOREIGN KEY([WebUserID]) REFERENCES [dbo].[WebUsers] ([WebUserID])
    ) ON [PRIMARY];

    -- Webhooks arrive keyed by Stripe subscription id, so index it for the lookup.
    CREATE UNIQUE NONCLUSTERED INDEX [UX_Subscriptions_StripeSubscriptionId]
        ON [dbo].[Subscriptions] ([StripeSubscriptionId] ASC)
        WHERE [StripeSubscriptionId] IS NOT NULL;
END
GO
