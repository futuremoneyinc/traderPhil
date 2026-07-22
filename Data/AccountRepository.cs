using System.Data;
using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Auth;

namespace TraderPhil.V4.Web.Data;

// ============================================================================
// DTOs
// ============================================================================

public sealed class AccountProfile
{
    public int      WebUserID         { get; init; }
    public string   GoogleEmail       { get; init; } = "";
    public string?  DisplayName       { get; init; }
    public bool     IsAdmin           { get; init; }
    public DateTime CreatedAt         { get; init; }
    public DateTime? LastLoginAt      { get; init; }
    public bool     DisplayStatsPublic { get; init; }
}

public sealed class ApiKeyState
{
    public int       UEI                   { get; init; }
    public bool      HasKey                { get; init; }      // data1 is not null/empty
    public string?   KeyPreview            { get; init; }      // "AbCd...WxYz"
    public string?   ApiKeyName            { get; init; }
    public DateTime? ApiKeyModifiedAt      { get; init; }
    public DateTime? ApiKeyExpiresAt       { get; init; }
    public DateTime? LastApiTestAt         { get; init; }
    public string?   LastApiTestResult     { get; init; }      // "PASS" / "FAIL" / null
    public bool      HasUndoableDelete     { get; init; }      // data3/data4 populated and DeleteKeyAfter is future
    public DateTime? UndoExpiresAt         { get; init; }      // when the rollback window closes
    public bool      IsActive              { get; init; }
}

public interface IAccountRepository
{
    Task<AccountProfile?> GetProfileAsync(int webUserId);
    Task SetDisplayStatsPublicAsync(int webUserId, bool value);

    Task<string> GetProgressReportFrequencyAsync(int uei);
    Task SetProgressReportFrequencyAsync(int uei, string frequency);

    Task<ApiKeyState> GetApiKeyStateAsync(int uei);

    /// <summary>Plaintext key + secret for live API calls. Returns null if no key set.</summary>
    Task<(string ApiKey, string ApiSecret)?> GetCredentialsAsync(int uei);

    /// <summary>
    /// Saves a verified key. Snapshots current data1/data2 -> data3/data4 with a 24-hour
    /// rollback window, then writes the new key + metadata.
    /// </summary>
    Task SaveVerifiedKeyAsync(int uei, string apiKey, string apiSecret,
                              string apiKeyName, DateTime? expiresAt, DateTime? modifiedAt);

    /// <summary>
    /// Records the result of a Test Connection click without changing data1/data2.
    /// Also refreshes the metadata fields from the latest GetApiKeyInfo response.
    /// </summary>
    Task RecordTestResultAsync(int uei, bool passed, string? apiKeyName,
                               DateTime? expiresAt, DateTime? modifiedAt);

    /// <summary>
    /// Soft-deletes the key: snapshots to data3/data4, nulls out data1/data2,
    /// sets IsActive=0 and starts the 24-hour rollback window.
    /// </summary>
    Task SoftDeleteKeyAsync(int uei);

    /// <summary>
    /// Restores data1/data2 from data3/data4 if the rollback window hasn't expired.
    /// Returns true on success, false if window expired or nothing to restore.
    /// </summary>
    Task<bool> UndoDeleteAsync(int uei);
}

// ============================================================================
// Implementation
// ============================================================================

public sealed class AccountRepository : IAccountRepository
{
    private readonly string _connectionString;
    private readonly ILogger<AccountRepository> _logger;

    public AccountRepository(IConfiguration config, ILogger<AccountRepository> logger)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
        _logger = logger;
    }

    public async Task<AccountProfile?> GetProfileAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<AccountProfile>(@"
            SELECT
                WebUserID,
                GoogleEmail,
                DisplayName,
                IsAdmin,
                CreatedAt,
                LastLoginAt,
                DisplayStatsPublic
            FROM dbo.WebUsers
            WHERE WebUserID = @id",
            new { id = webUserId });
    }

    public async Task SetDisplayStatsPublicAsync(int webUserId, bool value)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.WebUsers
            SET DisplayStatsPublic = @v
            WHERE WebUserID = @id",
            new { id = webUserId, v = value });
    }

    public async Task<string> GetProgressReportFrequencyAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        var freq = await conn.QueryFirstOrDefaultAsync<string>(@"
            SELECT ProgressReportFrequency
            FROM dbo.UserSettings
            WHERE UEI = @uei",
            new { uei });
        return freq ?? "Never";
    }

    public async Task SetProgressReportFrequencyAsync(int uei, string frequency)
    {
        // Validate against the allowed set so a malicious POST can't inject anything.
        var valid = new[] { "Daily", "Weekly", "Monthly", "Never" };
        if (!valid.Contains(frequency, StringComparer.Ordinal))
            throw new ArgumentException($"Invalid ProgressReportFrequency: {frequency}", nameof(frequency));

        using var conn = new SqlConnection(_connectionString);
        var rows = await conn.ExecuteAsync(@"
            UPDATE dbo.UserSettings
            SET ProgressReportFrequency = @f,
                UpdatedAt = GETUTCDATE()
            WHERE UEI = @uei",
            new { uei, f = frequency });

        if (rows == 0)
        {
            _logger.LogWarning("SetProgressReportFrequency for UEI {UEI} affected 0 rows - UserSettings row may not exist.", uei);
        }
    }

    public async Task<ApiKeyState> GetApiKeyStateAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // One round trip. Pull data1's prefix and suffix server-side so we never
        // materialize the full key in app memory just to display 8 chars.
        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT
                u.uei                                                    AS UEI,
                CASE WHEN u.data1 IS NULL OR LEN(u.data1) = 0 THEN 0 ELSE 1 END AS HasKey,
                CASE WHEN u.data1 IS NULL OR LEN(u.data1) < 8 THEN NULL
                     ELSE LEFT(u.data1, 4) + '...' + RIGHT(u.data1, 4)
                END                                                      AS KeyPreview,
                u.ApiKeyName                                             AS ApiKeyName,
                u.ApiKeyModifiedAt                                       AS ApiKeyModifiedAt,
                u.ApiKeyExpiresAt                                        AS ApiKeyExpiresAt,
                u.LastApiTestAt                                          AS LastApiTestAt,
                u.LastApiTestResult                                      AS LastApiTestResult,
                CASE WHEN u.data3 IS NOT NULL
                      AND u.data4 IS NOT NULL
                      AND u.DeleteKeyAfter IS NOT NULL
                      AND u.DeleteKeyAfter > GETDATE() THEN 1 ELSE 0 END AS HasUndoableDelete,
                u.DeleteKeyAfter                                         AS UndoExpiresAt,
                u.IsActive                                               AS IsActive
            FROM dbo.UserExchangeInformation u
            WHERE u.uei = @uei",
            new { uei });

        if (row is null)
        {
            return new ApiKeyState { UEI = uei, HasKey = false, IsActive = false };
        }

        return new ApiKeyState
        {
            UEI                = (int)row.UEI,
            HasKey             = (int)row.HasKey == 1,
            KeyPreview         = (string?)row.KeyPreview,
            ApiKeyName         = (string?)row.ApiKeyName,
            ApiKeyModifiedAt   = (DateTime?)row.ApiKeyModifiedAt,
            ApiKeyExpiresAt    = (DateTime?)row.ApiKeyExpiresAt,
            LastApiTestAt      = (DateTime?)row.LastApiTestAt,
            LastApiTestResult  = (string?)row.LastApiTestResult,
            HasUndoableDelete  = (int)row.HasUndoableDelete == 1,
            UndoExpiresAt      = (DateTime?)row.UndoExpiresAt,
            IsActive           = (bool)row.IsActive,
        };
    }

    public async Task<(string ApiKey, string ApiSecret)?> GetCredentialsAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<(string? d1, string? d2)>(@"
            SELECT data1, data2
            FROM dbo.UserExchangeInformation
            WHERE uei = @uei",
            new { uei });

        if (row.d1 is null || row.d2 is null
            || string.IsNullOrWhiteSpace(row.d1)
            || string.IsNullOrWhiteSpace(row.d2))
        {
            return null;
        }
        return (row.d1, row.d2);
    }

    public async Task SaveVerifiedKeyAsync(int uei, string apiKey, string apiSecret,
                                            string apiKeyName, DateTime? expiresAt, DateTime? modifiedAt)
    {
        using var conn = new SqlConnection(_connectionString);

        // Atomic: snapshot the old key to data3/data4 with a 24h rollback window,
        // then overwrite data1/data2 with the new key. One UPDATE so there's no
        // window where both keys are visible to a concurrent reader.
        await conn.ExecuteAsync(@"
            UPDATE dbo.UserExchangeInformation
            SET data3              = data1,
                data4              = data2,
                DeleteKeyAfter     = DATEADD(day, 1, GETDATE()),
                data1              = @apiKey,
                data2              = @apiSecret,
                ApiKeyName         = @apiKeyName,
                ApiKeyExpiresAt    = @expiresAt,
                ApiKeyModifiedAt   = @modifiedAt,
                LastApiTestAt      = GETUTCDATE(),
                LastApiTestResult  = 'PASS',
                IsActive           = 1
            WHERE uei = @uei",
            new { uei, apiKey, apiSecret, apiKeyName, expiresAt, modifiedAt });

        _logger.LogWarning("Saved verified API key for UEI {UEI}; previous key snapshotted to data3/data4 with 24h rollback.", uei);
    }

    public async Task RecordTestResultAsync(int uei, bool passed, string? apiKeyName,
                                             DateTime? expiresAt, DateTime? modifiedAt)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.UserExchangeInformation
            SET LastApiTestAt      = GETUTCDATE(),
                LastApiTestResult  = @result,
                ApiKeyName         = COALESCE(@apiKeyName, ApiKeyName),
                ApiKeyExpiresAt    = @expiresAt,
                ApiKeyModifiedAt   = COALESCE(@modifiedAt, ApiKeyModifiedAt)
            WHERE uei = @uei",
            new
            {
                uei,
                result = passed ? "PASS" : "FAIL",
                apiKeyName,
                expiresAt,
                modifiedAt
            });
    }

    public async Task SoftDeleteKeyAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE dbo.UserExchangeInformation
            SET data3              = data1,
                data4              = data2,
                DeleteKeyAfter     = DATEADD(day, 1, GETDATE()),
                data1              = NULL,
                data2              = NULL,
                ApiKeyName         = NULL,
                ApiKeyModifiedAt   = NULL,
                ApiKeyExpiresAt    = NULL,
                LastApiTestAt      = NULL,
                LastApiTestResult  = NULL,
                IsActive           = 0
            WHERE uei = @uei
              AND data1 IS NOT NULL",
            new { uei });

        _logger.LogWarning("Soft-deleted API key for UEI {UEI}; snapshotted to data3/data4 with 24h rollback window.", uei);
    }

    public async Task<bool> UndoDeleteAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // Restore only if the rollback window is still open AND we actually have
        // snapshot data. The WHERE clause enforces both conditions atomically.
        var rows = await conn.ExecuteAsync(@"
            UPDATE dbo.UserExchangeInformation
            SET data1           = data3,
                data2           = data4,
                data3           = NULL,
                data4           = NULL,
                DeleteKeyAfter  = NULL,
                IsActive        = 1
            WHERE uei = @uei
              AND data3 IS NOT NULL
              AND data4 IS NOT NULL
              AND DeleteKeyAfter IS NOT NULL
              AND DeleteKeyAfter > GETDATE()",
            new { uei });

        if (rows > 0)
        {
            _logger.LogWarning("Undid API key delete for UEI {UEI}; restored from data3/data4.", uei);
            return true;
        }
        return false;
    }
}
