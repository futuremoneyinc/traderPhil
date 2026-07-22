using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface IUserSettingsRepository
{
    Task<UserSettingsRow> GetOrCreateAsync(int uei);
    Task<int> UpdateTreasuryAsync(int uei, TreasurySettingsPayload payload);
}

public sealed class UserSettingsRepository : IUserSettingsRepository
{
    private readonly string _connectionString;

    public UserSettingsRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
    }

    public async Task<UserSettingsRow> GetOrCreateAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<UserSettingsRow>(@"
            SELECT UEI, WoolChipperQtyPct, TreasuryEnabled, TreasuryMinDeployUSDT,
                   TreasuryMaxBridges, TreasuryTrailPct, TreasuryTightTrailPct,
                   TreasuryProfitLockPct, CreatedAt, UpdatedAt
            FROM dbo.UserSettings
            WHERE UEI = @uei",
            new { uei });

        if (row is not null) return row;

        await conn.ExecuteAsync(@"
            INSERT INTO dbo.UserSettings
                (UEI, WoolChipperQtyPct, CreatedAt, UpdatedAt,
                 TreasuryEnabled, TreasuryMinDeployUSDT, TreasuryMaxBridges,
                 TreasuryTrailPct, TreasuryTightTrailPct, TreasuryProfitLockPct)
            VALUES
                (@uei, 0.5000, GETUTCDATE(), GETUTCDATE(),
                 0, 100.00000000, 5,
                 0.0500, 0.0200, 0.0100)",
            new { uei });

        return (await conn.QueryFirstAsync<UserSettingsRow>(@"
            SELECT UEI, WoolChipperQtyPct, TreasuryEnabled, TreasuryMinDeployUSDT,
                   TreasuryMaxBridges, TreasuryTrailPct, TreasuryTightTrailPct,
                   TreasuryProfitLockPct, CreatedAt, UpdatedAt
            FROM dbo.UserSettings
            WHERE UEI = @uei",
            new { uei }));
    }

    public async Task<int> UpdateTreasuryAsync(int uei, TreasurySettingsPayload payload)
    {
        using var conn = new SqlConnection(_connectionString);
        await GetOrCreateAsync(uei);

        return await conn.ExecuteAsync(@"
            UPDATE dbo.UserSettings
            SET TreasuryEnabled        = @enabled,
                TreasuryMinDeployUSDT  = @minDeploy,
                TreasuryMaxBridges     = @maxBridges,
                UpdatedAt              = GETUTCDATE()
            WHERE UEI = @uei",
            new
            {
                uei,
                enabled    = payload.TreasuryEnabled,
                minDeploy  = payload.TreasuryMinDeployUSDT,
                maxBridges = payload.TreasuryMaxBridges
            });
    }
}
