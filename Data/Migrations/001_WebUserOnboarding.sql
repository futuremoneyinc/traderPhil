-- =============================================================================
-- 001_WebUserOnboarding.sql
--
-- Backing store for the /onboarding wizard. One row per WebUser, created the
-- first time an authenticated user lands on the wizard. Tracks how far they
-- got (so we can resume), the answers they gave in the Goals step (which drive
-- their Profit Ladder), and a couple of completion flags.
--
-- Idempotent: safe to run more than once. Apply against the traderPhil DB.
-- =============================================================================
USE [traderPhil];
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WebUserOnboarding' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[WebUserOnboarding](
        [WebUserID]        [int]            NOT NULL,
        -- Furthest step the user has reached. Mirrors the OnboardingStep enum in
        -- the web app (0=Welcome, 1=Goals, 2=Connect, 3=Verify, 4=Lounge,
        -- 5=Tour, 6=Plans, 7=Done). Stored as int so a new step can be inserted
        -- without a schema change to a check constraint.
        [CurrentStep]      [int]            NOT NULL CONSTRAINT [DF_WebUserOnboarding_CurrentStep] DEFAULT (1),

        -- The UEI (trading account) we provisioned for this user during the
        -- Goals step. NULL until provisioning happens. FK to the identity that
        -- mints UEIs.
        [UEI]              [int]            NULL,

        -- Goals-step answers, retained for support/audit and to re-render the
        -- form if the user steps back.
        [InvestmentGoal]   [varchar](40)    NULL,   -- 'income' | 'balanced' | 'aggressive'
        [FundingAmount]    [decimal](18, 2) NULL,   -- what the user says they'll fund with (USD)
        [GrowthSpeed]      [decimal](9, 4)  NULL,   -- 0.005 / 0.0125 / 0.025 (fed to usp_InitializeProfitTargets)
        [TargetAmount]     [decimal](18, 2) NULL,   -- long-term profit goal (USD)

        -- Chosen plan slug from the final step. NULL until the user picks one.
        [SelectedPlan]     [varchar](40)    NULL,

        [TourCompleted]    [bit]            NOT NULL CONSTRAINT [DF_WebUserOnboarding_TourCompleted] DEFAULT (0),
        [CompletedAt]      [datetime]       NULL,
        [CreatedAt]        [datetime]       NOT NULL CONSTRAINT [DF_WebUserOnboarding_CreatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedAt]        [datetime]       NOT NULL CONSTRAINT [DF_WebUserOnboarding_UpdatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_WebUserOnboarding] PRIMARY KEY CLUSTERED ([WebUserID] ASC)
    ) ON [PRIMARY];

    ALTER TABLE [dbo].[WebUserOnboarding] WITH CHECK
        ADD CONSTRAINT [FK_WebUserOnboarding_WebUsers]
        FOREIGN KEY([WebUserID]) REFERENCES [dbo].[WebUsers] ([WebUserID]);

    -- UEI can be NULL, but when set it must reference a real exchange-info row,
    -- exactly like WebUserUEI does.
    ALTER TABLE [dbo].[WebUserOnboarding] WITH CHECK
        ADD CONSTRAINT [FK_WebUserOnboarding_UEI]
        FOREIGN KEY([UEI]) REFERENCES [dbo].[UserExchangeInformation] ([uei]);
END
GO
