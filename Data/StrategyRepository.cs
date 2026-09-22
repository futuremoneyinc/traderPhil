using System.Data;
using System.Data.SqlClient;
using Dapper;
using TraderPhil.V4.Web.Models;

namespace TraderPhil.V4.Web.Data;

public interface IStrategyRepository
{
    /// <summary>
    /// Loads the user's active strategies AND the list of addable coins in a SINGLE round trip.
    /// Filters out rows where the pair isn't COMPLETE (no USDT short available) or where
    /// vSymbiCorePairs doesn't recognize the LongSymbolID at all (legacy/broken configs).
    /// </summary>
    Task<StrategyPageData> LoadPageDataAsync(int uei);

    Task<int> UpdateActiveStrategyAsync(int uei, int dcaGroupId, StrategyEditPayload payload);

    /// <summary>
    /// Creates a new coin strategy. When <paramref name="coinLimit"/> is non-null,
    /// the create is rejected (atomically, inside the transaction) if the UEI is
    /// already at or above that many active coins.
    /// </summary>
    Task<int> CreateStrategyAsync(int uei, StrategyCreatePayload payload, int? coinLimit);

    /// <summary>Number of active coins (distinct OPEN, non-closed, non-paused symbols) for a UEI.</summary>
    Task<int> CountActiveCoinsAsync(int uei);

    /// <summary>
    /// Brings a UEI's coins in line with its plan limit. Keeps the oldest
    /// <paramref name="coinLimit"/> coins (DCAGroupId ASC) active and pauses the
    /// rest (full stop: AllowLongs = AllowShorts = 0, prior values snapshotted).
    /// Coins that come back within the limit (e.g. after an upgrade) are unpaused
    /// and their prior flags restored. A null limit means unlimited — everything
    /// paused-by-plan is unpaused. Returns (paused, unpaused) counts.
    /// </summary>
    Task<(int Paused, int Unpaused)> ReconcileCoinLimitAsync(int uei, int? coinLimit);
}

public sealed class StrategyPageData
{
    public IReadOnlyList<StrategyRow> Strategies { get; init; } = Array.Empty<StrategyRow>();
    public IReadOnlyList<AddableSymbolRow> Addables { get; init; } = Array.Empty<AddableSymbolRow>();
}

public sealed class StrategyRepository : IStrategyRepository
{
    private readonly string _connectionString;
    private readonly ILogger<StrategyRepository> _logger;

    public StrategyRepository(IConfiguration config, ILogger<StrategyRepository> logger)
    {
        _connectionString = config.GetConnectionString("TraderPhilDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TraderPhilDB missing.");
        _logger = logger;
    }

    public async Task<StrategyPageData> LoadPageDataAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);

        // One round trip, two result sets.
        const string sql = @"
            -- Set 1: Active SymbiCore strategies for this UEI.
            -- Filters:
            --   1) Status = OPEN AND IsClosed = 0
            --   2) DisplayBaseAsset IS NOT NULL  (long symbol is in vSymbiCorePairs)
            --   3) PairStatus = 'COMPLETE'       (USDT short exists for the base)
            -- If a DCAGroup has more than one OPEN row per (UEI, LongSymbolID), keep most recent.
            ;WITH ranked AS (
                SELECT
                    d.DCAGroupId, d.UEI, d.LongSymbolID, d.ShortSymbolID,
                    d.DcaLotSize, d.AllowLongs, d.AllowShorts,
                    d.OrderExpirationHours, d.BrokerTTLMinutes,
                    d.Strategy, d.Status, d.StartTime, d.PlanPausedAt,
                    ROW_NUMBER() OVER (PARTITION BY d.UEI, d.LongSymbolID
                                       ORDER BY d.DCAGroupId DESC) AS rn
                FROM dbo.DCAGroups d
                WHERE d.UEI = @uei
                  AND d.Status = 'OPEN'
                  AND d.IsClosed = 0
            )
            SELECT
                r.DCAGroupId, r.UEI, r.LongSymbolID, r.ShortSymbolID,
                r.DcaLotSize, r.AllowLongs, r.AllowShorts,
                r.OrderExpirationHours, r.BrokerTTLMinutes,
                r.Strategy, r.Status, r.StartTime, r.PlanPausedAt,
                p.DisplayBaseAsset,
                p.LongSymbol, p.LongOrderMin, p.LongLotDecimals,
                p.LongLastPrice, p.LongQuoteTime,
                p.ShortSymbol
            FROM ranked r
            INNER JOIN dbo.vSymbiCorePairs p ON p.LongSymbolID = r.LongSymbolID
            WHERE r.rn = 1
              AND p.PairStatus = 'COMPLETE'
              AND p.DisplayBaseAsset IS NOT NULL
            ORDER BY p.DisplayBaseAsset;

            -- Set 2: Coins available to ADD a new strategy for.
            SELECT
                p.DisplayBaseAsset AS BaseAsset,
                p.LongSymbolID,
                p.LongSymbol,
                p.LongOrderMin,
                p.LongLotDecimals,
                p.LongLastPrice,
                p.LongQuoteTime,
                p.ShortSymbolID,
                p.ShortSymbol
            FROM dbo.vSymbiCorePairs p
            WHERE p.PairStatus = 'COMPLETE'
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.DCAGroups d
                  WHERE d.UEI = @uei
                    AND d.LongSymbolID = p.LongSymbolID
                    AND d.Status = 'OPEN'
                    AND d.IsClosed = 0
              )
            ORDER BY p.DisplayBaseAsset;
        ";

        using var multi = await conn.QueryMultipleAsync(sql, new { uei });
        var strategies = (await multi.ReadAsync<StrategyRow>()).AsList();
        var addables   = (await multi.ReadAsync<AddableSymbolRow>()).AsList();

        return new StrategyPageData
        {
            Strategies = strategies,
            Addables   = addables
        };
    }

    public async Task<int> UpdateActiveStrategyAsync(int uei, int dcaGroupId, StrategyEditPayload payload)
    {
        using var conn = new SqlConnection(_connectionString);

        return await conn.ExecuteAsync(@"
            UPDATE dbo.DCAGroups
            SET DcaLotSize           = @lot,
                AllowLongs           = @allowLongs,
                AllowShorts          = @allowShorts,
                OrderExpirationHours = @orderHours,
                BrokerTTLMinutes     = @brokerTtl
            WHERE DCAGroupId = @id
              AND UEI = @uei
              AND Status = 'OPEN'
              AND IsClosed = 0
              AND PlanPausedAt IS NULL",
            new
            {
                id = dcaGroupId,
                uei,
                lot         = payload.DcaLotSize,
                allowLongs  = payload.AllowLongs,
                allowShorts = payload.AllowShorts,
                orderHours  = payload.OrderExpirationHours,
                brokerTtl   = payload.BrokerTTLMinutes
            });
    }

    public async Task<int> CountActiveCoinsAsync(int uei)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(DISTINCT LongSymbolID)
            FROM dbo.DCAGroups
            WHERE UEI = @uei AND Status = 'OPEN' AND IsClosed = 0 AND PlanPausedAt IS NULL",
            new { uei });
    }

    public async Task<(int Paused, int Unpaused)> ReconcileCoinLimitAsync(int uei, int? coinLimit)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        try
        {
            // Unlimited plan (or no limit): restore everything paused-by-plan.
            if (coinLimit is not int limit)
            {
                var restored = await conn.ExecuteAsync(@"
                    UPDATE dbo.DCAGroups
                    SET AllowLongs          = ISNULL(PrePauseAllowLongs, 1),
                        AllowShorts         = ISNULL(PrePauseAllowShorts, 1),
                        PrePauseAllowLongs  = NULL,
                        PrePauseAllowShorts = NULL,
                        PlanPausedAt        = NULL
                    WHERE UEI = @uei AND PlanPausedAt IS NOT NULL",
                    new { uei }, tx);

                tx.Commit();
                return (0, restored);
            }

            // Rank live coins oldest-first. The load query keeps a single OPEN row
            // per (UEI, LongSymbolID) and CreateStrategy forbids duplicates, so
            // ranking DCAGroups rows directly matches "one coin per row". rn is by
            // DCAGroupId, which never changes, so the ranking is stable across both
            // updates in this transaction.
            const string rankCte = @"
                ;WITH ranked AS (
                    SELECT DCAGroupId,
                           ROW_NUMBER() OVER (ORDER BY DCAGroupId ASC) AS rn
                    FROM dbo.DCAGroups WITH (UPDLOCK, HOLDLOCK)
                    WHERE UEI = @uei AND Status = 'OPEN' AND IsClosed = 0
                )";

            // Pause the excess (rank beyond the limit) that isn't already paused.
            var paused = await conn.ExecuteAsync(rankCte + @"
                UPDATE d
                SET d.PrePauseAllowLongs  = d.AllowLongs,
                    d.PrePauseAllowShorts = d.AllowShorts,
                    d.AllowLongs          = 0,
                    d.AllowShorts         = 0,
                    d.PlanPausedAt        = GETUTCDATE()
                FROM dbo.DCAGroups d
                INNER JOIN ranked r ON r.DCAGroupId = d.DCAGroupId
                WHERE r.rn > @limit AND d.PlanPausedAt IS NULL",
                new { uei, limit }, tx);

            // Unpause any now within the limit that were previously paused (upgrade).
            var unpaused = await conn.ExecuteAsync(rankCte + @"
                UPDATE d
                SET d.AllowLongs          = ISNULL(d.PrePauseAllowLongs, 1),
                    d.AllowShorts         = ISNULL(d.PrePauseAllowShorts, 1),
                    d.PrePauseAllowLongs  = NULL,
                    d.PrePauseAllowShorts = NULL,
                    d.PlanPausedAt        = NULL
                FROM dbo.DCAGroups d
                INNER JOIN ranked r ON r.DCAGroupId = d.DCAGroupId
                WHERE r.rn <= @limit AND d.PlanPausedAt IS NOT NULL",
                new { uei, limit }, tx);

            tx.Commit();
            return (paused, unpaused);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<int> CreateStrategyAsync(int uei, StrategyCreatePayload payload, int? coinLimit)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        try
        {
            var pair = await conn.QueryFirstOrDefaultAsync<PairLookupDto>(@"
                SELECT DisplayBaseAsset, ShortSymbolID, PairStatus
                FROM dbo.vSymbiCorePairs
                WHERE LongSymbolID = @id",
                new { id = payload.LongSymbolID }, tx);

            if (pair is null || pair.DisplayBaseAsset is null)
                throw new InvalidOperationException("That long symbol isn't recognized.");

            if (!string.Equals(pair.PairStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{pair.DisplayBaseAsset} is missing its USDT pair on Kraken - can't run SymbiCore on it.");

            if (pair.ShortSymbolID != payload.ShortSymbolID)
                throw new InvalidOperationException(
                    "Short symbol mismatch. Refresh the page and try again.");

            var alreadyActive = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM dbo.DCAGroups WITH (UPDLOCK, HOLDLOCK)
                WHERE UEI = @uei
                  AND LongSymbolID = @longId
                  AND Status = 'OPEN'
                  AND IsClosed = 0",
                new { uei, longId = payload.LongSymbolID }, tx);

            if (alreadyActive > 0)
                throw new InvalidOperationException(
                    $"An active strategy already exists for {pair.DisplayBaseAsset}. Edit it instead.");

            // Plan coin-limit gate. This is a NEW symbol (the check above proves it),
            // so adding it takes the user from `activeCoins` to `activeCoins + 1`.
            // Enforce inside the Serializable transaction so two concurrent adds
            // can't both slip past the limit.
            if (coinLimit is int limit)
            {
                var activeCoins = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(DISTINCT LongSymbolID) FROM dbo.DCAGroups WITH (UPDLOCK, HOLDLOCK)
                    WHERE UEI = @uei AND Status = 'OPEN' AND IsClosed = 0 AND PlanPausedAt IS NULL",
                    new { uei }, tx);

                if (activeCoins >= limit)
                    throw new InvalidOperationException(
                        $"Your plan allows {limit} coin{(limit == 1 ? "" : "s")}. Upgrade your plan to add more.");
            }

            var strategyLabel = $"SymbiCore_{pair.DisplayBaseAsset.ToUpperInvariant()}_250";

            var newId = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO dbo.DCAGroups
                    (SymbolID, WalletId, Strategy, TargetProfitPct, StartTime, Status,
                     TargetProfitAmount, SellOrderSent, DcaLotSize, IsClosed, UEI,
                     GrowthFactorProfit, GrowthFactorLot, MaxOpenShorts, JackpotID,
                     StandardBuyDiscount, BonusBuyDiscount, TakeProfitMargin,
                     OrderExpirationHours, BrokerTTLMinutes,
                     LongSymbolID, ShortSymbolID, AllowLongs, AllowShorts)
                VALUES
                    (@longId, -1, @strategy, 0, GETUTCDATE(), 'OPEN',
                     250.00000000, 0, @lot, 0, @uei,
                     0.001000, 0.001000, 50, 0,
                     0.00500000, 0.02500000, 0.02500000,
                     @orderHours, @brokerTtl,
                     @longId, @shortId, @allowLongs, @allowShorts);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    longId      = payload.LongSymbolID,
                    shortId     = payload.ShortSymbolID,
                    strategy    = strategyLabel,
                    lot         = payload.DcaLotSize,
                    orderHours  = payload.OrderExpirationHours,
                    brokerTtl   = payload.BrokerTTLMinutes,
                    allowLongs  = payload.AllowLongs,
                    allowShorts = payload.AllowShorts,
                    uei
                }, tx);

            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

internal sealed class PairLookupDto
{
    public string?  DisplayBaseAsset { get; init; }
    public int      ShortSymbolID    { get; init; }
    public string?  PairStatus       { get; init; }
}
