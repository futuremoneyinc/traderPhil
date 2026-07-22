using Microsoft.Extensions.Caching.Memory;
using TraderPhil.V4.Web.Kraken;

namespace TraderPhil.V4.Web.Services;

public interface IFundingCache
{
    Task<List<DepositMethod>?>  GetMethodsAsync  (int uei, string asset, Func<Task<List<DepositMethod>?>> loader);
    Task<List<DepositAddress>?> GetAddressesAsync(int uei, string asset, string method, Func<Task<List<DepositAddress>?>> loader);
    void InvalidateAddresses(int uei, string asset, string method);
}

/// <summary>
/// In-memory cache with the durations Phil specified:
///   - DepositMethods: 7 days (networks rarely change for an asset)
///   - DepositAddresses: 1 hour (Kraken returns the same address by default; refresh is cheap)
///
/// Keys are scoped per UEI so users don't see each other's data.
/// Process-local — fine for a single web instance. If we scale out, swap to
/// distributed cache (Redis, SQL Server, etc.) without changing the interface.
/// </summary>
public sealed class FundingCache : IFundingCache
{
    private static readonly TimeSpan MethodsTtl   = TimeSpan.FromDays(7);
    private static readonly TimeSpan AddressesTtl = TimeSpan.FromHours(1);

    private readonly IMemoryCache _cache;

    public FundingCache(IMemoryCache cache) => _cache = cache;

    public async Task<List<DepositMethod>?> GetMethodsAsync(
        int uei, string asset, Func<Task<List<DepositMethod>?>> loader)
    {
        var key = $"funding:{uei}:methods:{asset.ToUpperInvariant()}";
        if (_cache.TryGetValue<List<DepositMethod>>(key, out var hit) && hit is not null) return hit;

        var fresh = await loader();
        if (fresh is not null) _cache.Set(key, fresh, MethodsTtl);
        return fresh;
    }

    public async Task<List<DepositAddress>?> GetAddressesAsync(
        int uei, string asset, string method, Func<Task<List<DepositAddress>?>> loader)
    {
        var key = $"funding:{uei}:addr:{asset.ToUpperInvariant()}:{method}";
        if (_cache.TryGetValue<List<DepositAddress>>(key, out var hit) && hit is not null) return hit;

        var fresh = await loader();
        if (fresh is not null) _cache.Set(key, fresh, AddressesTtl);
        return fresh;
    }

    public void InvalidateAddresses(int uei, string asset, string method)
    {
        var key = $"funding:{uei}:addr:{asset.ToUpperInvariant()}:{method}";
        _cache.Remove(key);
    }
}
