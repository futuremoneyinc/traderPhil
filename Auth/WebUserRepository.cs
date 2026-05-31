using System.Data.SqlClient;
using Dapper;

namespace TraderPhil.V4.Web.Auth;

public interface IWebUserRepository
{
    /// <summary>
    /// Finds (or creates) a WebUser row for a Google sub claim. Updates LastLoginAt.
    /// Returns null if the user exists but is inactive.
    /// </summary>
    Task<WebUser?> SignInAsync(string googleSubjectId, string email, string? displayName);

    /// <summary>
    /// All UEIs this WebUser has been granted access to.
    /// </summary>
    Task<IReadOnlyList<int>> GetGrantedUeisAsync(int webUserId);

    /// <summary>
    /// True if this WebUser has been granted access to the given UEI.
    /// </summary>
    Task<bool> HasUeiAccessAsync(int webUserId, int uei);

    Task<WebUser?> GetByIdAsync(int webUserId);
}

public sealed class WebUser
{
    public int     WebUserID       { get; init; }
    public string  GoogleSubjectID { get; init; } = "";
    public string  GoogleEmail     { get; init; } = "";
    public string? DisplayName     { get; init; }
    public bool    IsAdmin         { get; init; }
    public bool    IsActive        { get; init; }
    public DateTime CreatedAt      { get; init; }
    public DateTime? LastLoginAt   { get; init; }
}

public sealed class WebUserRepository : IWebUserRepository
{
    private readonly string _connectionString;

    public WebUserRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
    }

    public async Task<WebUser?> SignInAsync(string googleSubjectId, string email, string? displayName)
    {
        using var conn = new SqlConnection(_connectionString);

        // Try to find existing user
        var existing = await conn.QueryFirstOrDefaultAsync<WebUser>(@"
            SELECT WebUserID, GoogleSubjectID, GoogleEmail, DisplayName, IsAdmin, IsActive, CreatedAt, LastLoginAt
            FROM dbo.WebUsers
            WHERE GoogleSubjectID = @sub",
            new { sub = googleSubjectId });

        if (existing is not null)
        {
            if (!existing.IsActive) return null;

            await conn.ExecuteAsync(@"
                UPDATE dbo.WebUsers
                SET LastLoginAt = GETUTCDATE(),
                    GoogleEmail = @email,
                    DisplayName = COALESCE(@name, DisplayName)
                WHERE WebUserID = @id",
                new { id = existing.WebUserID, email, name = displayName });

            return existing;
        }

        // First-time login: create a new WebUser row. They will have NO UEI grants until
        // an admin adds them to WebUserUEI. Until then, the dashboard shows "no accounts linked."
        var newId = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO dbo.WebUsers (GoogleSubjectID, GoogleEmail, DisplayName, IsAdmin, IsActive, CreatedAt, LastLoginAt)
            VALUES (@sub, @email, @name, 0, 1, GETUTCDATE(), GETUTCDATE());
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { sub = googleSubjectId, email, name = displayName });

        return await GetByIdAsync(newId);
    }

    public async Task<IReadOnlyList<int>> GetGrantedUeisAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        var ueis = await conn.QueryAsync<int>(@"
            SELECT UEI
            FROM dbo.WebUserUEI
            WHERE WebUserID = @id
            ORDER BY UEI",
            new { id = webUserId });
        return ueis.ToList();
    }

    public async Task<bool> HasUeiAccessAsync(int webUserId, int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        var count = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM dbo.WebUserUEI
            WHERE WebUserID = @id AND UEI = @uei",
            new { id = webUserId, uei });
        return count > 0;
    }

    public async Task<WebUser?> GetByIdAsync(int webUserId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<WebUser>(@"
            SELECT WebUserID, GoogleSubjectID, GoogleEmail, DisplayName, IsAdmin, IsActive, CreatedAt, LastLoginAt
            FROM dbo.WebUsers
            WHERE WebUserID = @id",
            new { id = webUserId });
    }
}
