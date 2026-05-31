using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface ITreasuryRepository
{
    /// <summary>
    /// Live snapshot of treasury health for a UEI. Returns null if the UEI doesn't
    /// have a USDT balance row at all (brand-new account).
    /// </summary>
    Task<TreasuryHealthRow?> GetHealthAsync(int uei);
}

public sealed class TreasuryRepository : ITreasuryRepository
{
    private readonly string _connectionString;

    public TreasuryRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
    }

    public async Task<TreasuryHealthRow?> GetHealthAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<TreasuryHealthRow>(@"
            SELECT
                UEI,
                AvailableUSDT,
                SLReserveUSDT,
                TPReserveUSDT,
                ReserveUSDT,
                DeployableUSDT,
                ReservePct,
                PendingSLCount,
                PendingStaticTPCount,
                ActiveBridges
            FROM dbo.vw_TreasuryAccountHealth
            WHERE UEI = @uei",
            new { uei });
    }
}
