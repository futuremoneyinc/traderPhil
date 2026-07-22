using System.Data;
using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface IOnboardingRepository
{
    /// <summary>Returns the user's onboarding row, creating it (at the Goals step) on first touch.</summary>
    Task<OnboardingState> GetOrCreateAsync(int webUserId);

    /// <summary>Reads the current onboarding row, or null if the user has never started.</summary>
    Task<OnboardingState?> GetAsync(int webUserId);

    /// <summary>
    /// Ensures the user has a trading account (UEI). Idempotent: if they already
    /// have a UEI grant, that UEI is returned and reused. Otherwise a fresh
    /// UserExchangeInformation row is minted (exchange = Kraken, inactive until a
    /// key is saved), a WebUserUEI grant is created, and the UEI is recorded on
    /// the onboarding row. Returns the UEI.
    /// </summary>
    Task<int> ProvisionUeiAsync(int webUserId);

    /// <summary>Persists the Goals-step answers.</summary>
    Task SaveGoalsAsync(int webUserId, OnboardingGoalsPayload goals);

    /// <summary>Moves the user to a specific step (the step they should resume at).</summary>
    Task SetStepAsync(int webUserId, OnboardingStep step);

    /// <summary>Records the chosen plan and marks onboarding complete.</summary>
    Task CompleteAsync(int webUserId, string selectedPlan);
}

public sealed class OnboardingRepository : IOnboardingRepository
{
    private readonly string _connectionString;
    private readonly ILogger<OnboardingRepository> _logger;

    private const string SelectColumns = @"
        WebUserID, CurrentStep, UEI, InvestmentGoal, FundingAmount, GrowthSpeed,
        TargetAmount, SelectedPlan, TourCompleted, CompletedAt, CreatedAt, UpdatedAt";

    public OnboardingRepository(IConfiguration config, ILogger<OnboardingRepository> logger)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
        _logger = logger;
    }

    public async Task<OnboardingState?> GetAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<OnboardingState>(
            $"SELECT {SelectColumns} FROM dbo.WebUserOnboarding WHERE WebUserID = @id",
            new { id = webUserId });
    }

    public async Task<OnboardingState> GetOrCreateAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);

        var existing = await conn.QueryFirstOrDefaultAsync<OnboardingState>(
            $"SELECT {SelectColumns} FROM dbo.WebUserOnboarding WHERE WebUserID = @id",
            new { id = webUserId });
        if (existing is not null) return existing;

        // First touch: create the row at the Goals step. The CurrentStep default
        // in the table is already Goals(1); we insert explicitly so behavior is
        // independent of the DB default.
        await conn.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM dbo.WebUserOnboarding WHERE WebUserID = @id)
            INSERT INTO dbo.WebUserOnboarding (WebUserID, CurrentStep, CreatedAt, UpdatedAt)
            VALUES (@id, @step, GETUTCDATE(), GETUTCDATE())",
            new { id = webUserId, step = (int)OnboardingStep.Goals });

        return await conn.QueryFirstAsync<OnboardingState>(
            $"SELECT {SelectColumns} FROM dbo.WebUserOnboarding WHERE WebUserID = @id",
            new { id = webUserId });
    }

    public async Task<int> ProvisionUeiAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Fast path: already provisioned (either recorded on the onboarding row
        // or granted directly). Reuse it — we never mint a second trading account.
        var existingUei = await conn.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 UEI FROM dbo.WebUserUEI WHERE WebUserID = @id ORDER BY UEI",
            new { id = webUserId });

        if (existingUei is int reused)
        {
            await conn.ExecuteAsync(@"
                UPDATE dbo.WebUserOnboarding
                SET UEI = @uei, UpdatedAt = GETUTCDATE()
                WHERE WebUserID = @id AND (UEI IS NULL OR UEI <> @uei)",
                new { id = webUserId, uei = reused });
            return reused;
        }

        using var tx = conn.BeginTransaction();
        try
        {
            // Mint a UEI by inserting an exchange-info row. IsActive = 0 until a
            // verified key is saved; the worker leaves inactive accounts alone.
            var newUei = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO dbo.UserExchangeInformation (userID, exchangeID, IsActive, LastStatusCheck)
                VALUES (@userID, 'Kraken', 0, GETUTCDATE());
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new { userID = $"web-{webUserId}" }, tx);

            // Grant the WebUser access to it (CreatedAt has a DB default).
            await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.WebUserUEI WHERE WebUserID = @id AND UEI = @uei)
                INSERT INTO dbo.WebUserUEI (WebUserID, UEI) VALUES (@id, @uei)",
                new { id = webUserId, uei = newUei }, tx);

            await conn.ExecuteAsync(@"
                UPDATE dbo.WebUserOnboarding
                SET UEI = @uei, UpdatedAt = GETUTCDATE()
                WHERE WebUserID = @id",
                new { id = webUserId, uei = newUei }, tx);

            tx.Commit();
            _logger.LogWarning("Provisioned UEI {UEI} for WebUser {WebUserID} during onboarding.", newUei, webUserId);
            return newUei;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SaveGoalsAsync(int webUserId, OnboardingGoalsPayload goals)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.WebUserOnboarding
            SET InvestmentGoal = @goal,
                FundingAmount  = @funding,
                GrowthSpeed    = @growth,
                TargetAmount   = @target,
                UpdatedAt      = GETUTCDATE()
            WHERE WebUserID = @id",
            new
            {
                id      = webUserId,
                goal    = goals.InvestmentGoal,
                funding = goals.FundingAmount,
                growth  = OnboardingGoals.GrowthForGoal(goals.InvestmentGoal),
                target  = goals.TargetAmount,
            });
    }

    public async Task SetStepAsync(int webUserId, OnboardingStep step)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.WebUserOnboarding
            SET CurrentStep = @step, UpdatedAt = GETUTCDATE()
            WHERE WebUserID = @id",
            new { id = webUserId, step = (int)step });
    }

    public async Task CompleteAsync(int webUserId, string selectedPlan)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.WebUserOnboarding
            SET SelectedPlan  = @plan,
                TourCompleted = 1,
                CurrentStep   = @done,
                CompletedAt   = COALESCE(CompletedAt, GETUTCDATE()),
                UpdatedAt     = GETUTCDATE()
            WHERE WebUserID = @id",
            new { id = webUserId, plan = selectedPlan, done = (int)OnboardingStep.Done });
    }
}
