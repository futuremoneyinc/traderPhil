-- =============================================================================
-- 003_CoinLimitPause.sql
--
-- Supports pausing coins that exceed a downgraded plan's limit. "Pause" = a full
-- stop: AllowLongs and AllowShorts are both set to 0 so the worker opens no new
-- buys or sells for that coin. We keep the oldest N coins (DCAGroupId ASC) active
-- and pause the rest. The pre-pause flag values are snapshotted so an upgrade can
-- restore exactly what the user had.
--
-- Idempotent. Apply against the traderPhil DB (after 001 and 002).
-- =============================================================================
USE [traderPhil];
GO

IF COL_LENGTH('dbo.DCAGroups', 'PlanPausedAt') IS NULL
    ALTER TABLE [dbo].[DCAGroups] ADD [PlanPausedAt] [datetime] NULL;
GO

IF COL_LENGTH('dbo.DCAGroups', 'PrePauseAllowLongs') IS NULL
    ALTER TABLE [dbo].[DCAGroups] ADD [PrePauseAllowLongs] [bit] NULL;
GO

IF COL_LENGTH('dbo.DCAGroups', 'PrePauseAllowShorts') IS NULL
    ALTER TABLE [dbo].[DCAGroups] ADD [PrePauseAllowShorts] [bit] NULL;
GO
