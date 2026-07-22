using System.Data;
using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface IProfitTargetsRepository
{
    /// <summary>Summary stats for a UEI's profit targets. Returns zeros if none exist.</summary>
    Task<ProfitTargetsSummary> GetSummaryAsync(int uei);

    /// <summary>
    /// Calls usp_InitializeProfitTargets with the supplied parameters.
    /// Throws on SQL error (e.g., the proc's own validation).
    /// Returns (TargetsCreated, TotalProfitTargetSum) from the proc's result set.
    /// </summary>
    Task<ProfitTargetsInitResult> InitializeAsync(int uei, ProfitTargetsInitPayload payload);

    /// <summary>
    /// Hard-deletes all of a UEI's profit targets. Per Phil's spec, this is a
    /// pure DELETE WHERE UEI=@uei. The web layer is responsible for the
    /// type-to-confirm safety check before calling.
    /// Returns the row count actually deleted.
    /// </summary>
    Task<int> DeleteAllAsync(int uei);
}

public sealed class ProfitTargetsRepository : IProfitTargetsRepository
{
    private readonly string _connectionString;
    private readonly ILogger<ProfitTargetsRepository> _logger;

    public ProfitTargetsRepository(IConfiguration config, ILogger<ProfitTargetsRepository> logger)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
        _logger = logger;
    }

    public async Task<ProfitTargetsSummary> GetSummaryAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<ProfitTargetsSummary>(@"
            SELECT
                @uei                                                                                       AS UEI,
                COUNT(*)                                                                                   AS TotalCount,
                COUNT(CASE WHEN ParentPOID IS NULL     THEN 1 END)                                         AS AvailableCount,
                COUNT(CASE WHEN ParentPOID IS NOT NULL THEN 1 END)                                         AS ConsumedCount,
                ISNULL(SUM(OriginalProfitTarget), 0)                                                       AS TotalOriginalProfitTarget,
                ISNULL(SUM(RecoveryBoost), 0)                                                              AS TotalRecoveryBoost,
                ISNULL(SUM(CurrentProfitTarget), 0)                                                        AS TotalCurrentProfitTarget,
                ISNULL(SUM(CASE WHEN ParentPOID IS NOT NULL THEN CurrentProfitTarget ELSE 0 END), 0)       AS ConsumedValue,
                ISNULL(MIN(OriginalProfitTarget), 0)                                                       AS MinOriginalProfitTarget,
                ISNULL(MAX(OriginalProfitTarget), 0)                                                       AS MaxOriginalProfitTarget
            FROM dbo.ProfitTargets
            WHERE UEI = @uei",
            new { uei });

        return row ?? new ProfitTargetsSummary { UEI = uei };
    }

    public async Task<ProfitTargetsInitResult> InitializeAsync(int uei, ProfitTargetsInitPayload payload)
    {
        using var conn = new SqlConnection(_connectionString);

        // Call the existing sproc with the fixed-hidden values per Phil's spec:
        //   IndividualProfitCap = 10000   (hidden)
        //   numSeq              = 10      (hidden, was 1 in legacy; revisit if needed)
        //   reset               = 0       (we don't use the soft-reset path here)
        var parms = new DynamicParameters();
        parms.Add("@UEI",                  uei,                                DbType.Int32);
        parms.Add("@StartProfit",          payload.StartProfit,                DbType.Decimal);
        parms.Add("@GrowthPercentage",     payload.GrowthPercentage,           DbType.Decimal);
        parms.Add("@LongTermProfitTarget", payload.LongTermProfitTarget,       DbType.Decimal);
        parms.Add("@IndividualProfitCap",  10000m,                             DbType.Decimal);
        parms.Add("@numSeq",               10,                                 DbType.Int32);
        parms.Add("@reset",                false,                              DbType.Boolean);

        var result = await conn.QueryFirstOrDefaultAsync<ProfitTargetsInitResult>(
            "dbo.usp_InitializeProfitTargets",
            parms,
            commandType: CommandType.StoredProcedure,
            commandTimeout: 120);  // the proc can run a while for big LongTermProfitTarget values

        return result ?? new ProfitTargetsInitResult { TargetsCreated = 0, TotalProfitTargetSum = 0m };
    }

    public async Task<int> DeleteAllAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // Per Phil: hard DELETE on UEI, no preservation of consumed targets.
        // The web layer's type-to-confirm box is the only safety check.
        var n = await conn.ExecuteAsync(
            "DELETE FROM dbo.ProfitTargets WHERE UEI = @uei",
            new { uei });

        _logger.LogWarning("ProfitTargets hard-delete: UEI={UEI}, rows={Rows}", uei, n);
        return n;
    }
}
