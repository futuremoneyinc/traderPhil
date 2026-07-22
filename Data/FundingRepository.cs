using System.Data;
using System.Data.SqlClient;
using Dapper;

namespace TraderPhil.V4.Web.Data;

public sealed class DepositableCoin
{
    public int    SymbolID         { get; init; }
    public string DisplayName      { get; init; } = "";   // e.g. "BTC", "ETH"
    public string KrakenAssetCode  { get; init; } = "";   // e.g. "XBT", "ETH", "USDT" — what Kraken's funding API expects
}

public sealed class UeiCredentials
{
    public string ApiKey    { get; init; } = "";
    public string ApiSecret { get; init; } = "";
}

public interface IFundingRepository
{
    /// <summary>
    /// Returns the coins this UEI is currently trading (has an OPEN DCAGroup for, or
    /// has closed trades for). Used to populate the deposit-card coin list.
    /// </summary>
    Task<IReadOnlyList<DepositableCoin>> GetDepositableCoinsAsync(int uei);

    /// <summary>
    /// Pulls the user's Kraken API key + secret for a given UEI. Required server-side
    /// to make the deposit-address calls on the user's behalf.
    /// </summary>
    Task<UeiCredentials?> GetCredentialsAsync(int uei);

    /// <summary>
    /// Calls usp_GetEmergencyStopStatus. PARAMETER CONTRACT PENDING from Phil.
    /// </summary>
    Task<bool> GetEmergencyStopAsync(int uei);

    /// <summary>
    /// Calls usp_ToggleEmergencyStop. Returns the new state after toggling,
    /// or null if the UserSettings row doesn't exist (in which case the UPDATE
    /// affected 0 rows and no toggle happened — the caller should surface this
    /// as an error to the user rather than report a false success).
    /// </summary>
    Task<bool?> ToggleEmergencyStopAsync(int uei);
}

public sealed class FundingRepository : IFundingRepository
{
    private readonly string _connectionString;
    private readonly ILogger<FundingRepository> _logger;

    public FundingRepository(IConfiguration config, ILogger<FundingRepository> logger)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
        _logger = logger;
    }

    public async Task<IReadOnlyList<DepositableCoin>> GetDepositableCoinsAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // Coins where this UEI has an OPEN DCAGroup. Joins through KrakenSymbols to
        // get a clean display ticker. Result is sorted alphabetically.
        //
        // SQL Server 2012 compatible: no STRING_AGG, no DISTINCT inside CTE with TOP,
        // no APPLY on table-valued functions.
        var rows = await conn.QueryAsync<DepositableCoin>(@"
            SELECT DISTINCT
                ks.SymbolID                            AS SymbolID,
                ks.BaseAsset                           AS DisplayName,
                ks.BaseAsset                           AS KrakenAssetCode
            FROM dbo.DCAGroups dg
            INNER JOIN dbo.KrakenSymbols ks
                ON ks.SymbolID = dg.LongSymbolID
            WHERE dg.UEI = @uei
              AND dg.Status = 'OPEN'
              AND dg.IsClosed = 0
            ORDER BY ks.BaseAsset",
            new { uei });

        return rows.ToList();
    }

    public async Task<UeiCredentials?> GetCredentialsAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // Per UserLease.cs convention: data1 = API Key, data2 = API Secret.
        var row = await conn.QueryFirstOrDefaultAsync<UeiCredentials>(@"
            SELECT
                data1 AS ApiKey,
                data2 AS ApiSecret
            FROM dbo.UserExchangeInformation
            WHERE uei = @uei
              AND IsActive = 1",
            new { uei });

        if (row is null || string.IsNullOrWhiteSpace(row.ApiKey) || string.IsNullOrWhiteSpace(row.ApiSecret))
        {
            return null;
        }
        return row;
    }

    // ========================================================================
    // EMERGENCY STOP — TODO: wire to usp_GetEmergencyStopStatus and
    // usp_ToggleEmergencyStop once Phil provides parameter signatures.
    //
    // The procs exist in the live DB but are not in master_6_14_2026.sql, so
    // the parameter contract has to be confirmed before calling them.
    // ========================================================================

    public async Task<bool> GetEmergencyStopAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        // Proc returns a single-row, single-column result set:
        //   SELECT CAST(ISNULL(MAX(CAST(EmergencyStopActive AS INT)),0) AS BIT) AS EmergencyStopActive
        // Missing UserSettings row coalesces to 0 (stop off). ExecuteScalarAsync<bool> is
        // the natural shape for this.
        var state = await conn.ExecuteScalarAsync<bool>(
            "dbo.usp_GetEmergencyStopStatus",
            new { UEI = uei },
            commandType: CommandType.StoredProcedure);
        return state;
    }

    public async Task<bool?> ToggleEmergencyStopAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        // Proc flips EmergencyStopActive and returns the new value as a single-row
        // result set. If the UserSettings row doesn't exist, the proc returns nothing
        // — we surface that as null so the caller can warn the user instead of
        // silently reporting a state change that didn't happen.
        var rows = (await conn.QueryAsync<bool>(
            "dbo.usp_ToggleEmergencyStop",
            new { UEI = uei },
            commandType: CommandType.StoredProcedure)).ToList();

        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "ToggleEmergencyStop for UEI {UEI} returned 0 rows — UserSettings row does not exist.",
                uei);
            return null;
        }

        var newState = rows[0];
        _logger.LogWarning("Emergency stop toggled for UEI {UEI}; new state = {State}", uei, newState);
        return newState;
    }
}
