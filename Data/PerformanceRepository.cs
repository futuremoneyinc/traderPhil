using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface IPerformanceRepository
{
    /// <summary>
    /// Most recent N days of daily stats for a strategy, newest first.
    /// Returns rows from DailyStrategyStats which is populated nightly + by backfill.
    /// Days with no activity exist as zero-rows (see usp_BuildDailyStrategyStats).
    /// </summary>
    Task<IReadOnlyList<DailyStatRow>> GetRecentDaysAsync(int uei, string strategyName, int days = 10);

    /// <summary>
    /// Trade-level detail for one strategy, one day. Default sort is CloseExecutedDate DESC.
    /// </summary>
    Task<IReadOnlyList<TradeDetailRow>> GetDayDetailAsync(int uei, string strategyName, DateTime statDate);

    /// <summary>Lifetime sum of RealizedPL for one strategy.</summary>
    Task<decimal> GetLifetimePLAsync(int uei, string strategyName);
}

public sealed class PerformanceRepository : IPerformanceRepository
{
    private readonly string _connectionString;

    public PerformanceRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
    }

    public async Task<IReadOnlyList<DailyStatRow>> GetRecentDaysAsync(int uei, string strategyName, int days = 10)
    {
        using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<DailyStatRow>(@"
            SELECT TOP (@days)
                StatDate, UEI, StrategyName,
                ClosedTrades, Wins, Losses, BreakEven,
                RealizedPL, DailyVolume, AccountSizeUSD
            FROM dbo.DailyStrategyStats
            WHERE UEI = @uei AND StrategyName = @strategy
            ORDER BY StatDate DESC",
            new { uei, strategy = strategyName, days });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TradeDetailRow>> GetDayDetailAsync(int uei, string strategyName, DateTime statDate)
    {
        // Pull from TradeStatus view (now poID-joined, post-Migration B).
        // We capture BaseAsset by stripping the standard quote suffix from the symbol.
        using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<TradeDetailRow>(@"
            SELECT
                ts.symbol                      AS Symbol,
                ts.EntryPOID,
                ts.ClosePOID,
                ts.E_Qty                       AS EntryQty,
                ts.C_Qty                       AS CloseQty,
                ts.E_Price                     AS EntryPrice,
                ts.C_Price                     AS ClosePrice,
                ts.RealizedPL,
                ts.TradeResult,
                ts.EntryExecutedDate,
                ts.CloseExecutedDate,
                CASE
                    WHEN ts.symbol LIKE '%usdt' THEN UPPER(LEFT(ts.symbol, LEN(ts.symbol) - 4))
                    WHEN ts.symbol LIKE '%usd'  THEN UPPER(LEFT(ts.symbol, LEN(ts.symbol) - 3))
                    WHEN ts.symbol LIKE '%btc'  THEN UPPER(LEFT(ts.symbol, LEN(ts.symbol) - 3))
                    WHEN ts.symbol LIKE '%eth'  THEN UPPER(LEFT(ts.symbol, LEN(ts.symbol) - 3))
                    ELSE UPPER(ts.symbol)
                END AS BaseAsset
            FROM dbo.TradeStatus ts
            WHERE ts.uei = @uei
              AND ts.StrategyName = @strategy
              AND CAST(ts.CloseExecutedDate AS DATE) = @statDate
            ORDER BY ts.CloseExecutedDate DESC",
            new { uei, strategy = strategyName, statDate });
        return rows.ToList();
    }

    public async Task<decimal> GetLifetimePLAsync(int uei, string strategyName)
    {
        using var conn = new SqlConnection(_connectionString);
        var pl = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT SUM(RealizedPL)
            FROM dbo.DailyStrategyStats
            WHERE UEI = @uei AND StrategyName = @strategy",
            new { uei, strategy = strategyName });
        return pl ?? 0m;
    }
}
