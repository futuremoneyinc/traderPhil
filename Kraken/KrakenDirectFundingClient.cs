using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraderPhil.V4.Web.Kraken;

/// <summary>
/// Result wrapper for funding API calls.
/// </summary>
public class KrakenFundingResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
}

public class DepositMethod
{
    /// <summary>Name of the deposit method (e.g. "Bitcoin", "Ethereum (ERC20)", "Solana").</summary>
    public string Method { get; init; } = string.Empty;
    /// <summary>Deposit limit reported by Kraken; may be "false" or a numeric string.</summary>
    public string? Limit { get; init; }
    /// <summary>Whether Kraken can generate a fresh address for this method (new=true is allowed).</summary>
    public bool CanGenerate { get; init; }
}

public class DepositAddress
{
    public string Address    { get; init; } = string.Empty;
    /// <summary>Expiration timestamp as a string; "0" means no expiration.</summary>
    public string ExpireTime { get; init; } = "0";
    public bool IsNew        { get; init; }
}

/// <summary>
/// Response from /0/private/GetApiKeyInfo. Fields mirror Kraken's shape
/// (unix-second strings for times; "0" for no-expiration).
/// </summary>
public class ApiKeyInfo
{
    public string         ApiKeyName        { get; init; } = "";
    public string         ApiKey            { get; init; } = "";
    public List<string>   Permissions       { get; init; } = new();
    public List<string>   IpAllowlist       { get; init; } = new();
    /// <summary>Unix seconds string; "0" means no expiration. Converted at use site.</summary>
    public string         ValidUntil        { get; init; } = "0";
    /// <summary>Unix seconds string.</summary>
    public string         CreatedTime       { get; init; } = "0";
    /// <summary>Unix seconds string.</summary>
    public string         ModifiedTime      { get; init; } = "0";
    /// <summary>Unix seconds string; "0" if never used.</summary>
    public string         LastUsed          { get; init; } = "0";

    /// <summary>Helper: ValidUntil parsed as DateTime, or null if "0".</summary>
    public DateTime? ValidUntilUtc => ParseUnixOrNull(ValidUntil);
    /// <summary>Helper: ModifiedTime parsed as DateTime.</summary>
    public DateTime? ModifiedTimeUtc => ParseUnixOrNull(ModifiedTime);

    private static DateTime? ParseUnixOrNull(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "0") return null;
        if (!long.TryParse(s, out var seconds) || seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch { return null; }
    }
}

/// <summary>
/// Thin authenticated client for Kraken's funding endpoints.
/// Mirrors the auth/signing pattern used by the worker's KrakenDirectOrderClient.
///
/// Lives in the Web project because the funding flow is user-initiated UI, not
/// part of the worker's order-placement pipeline.
/// </summary>
public sealed class KrakenDirectFundingClient : IDisposable
{
    private const string KrakenApiBase        = "https://api.kraken.com";
    private const string DepositMethodsPath   = "/0/private/DepositMethods";
    private const string DepositAddressesPath = "/0/private/DepositAddresses";
    private const string GetApiKeyInfoPath    = "/0/private/GetApiKeyInfo";

    private static long _lastNonce;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly byte[] _apiSecretBytes;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public KrakenDirectFundingClient(string apiKey, string apiSecret, ILogger logger, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))    throw new ArgumentException("apiKey required",    nameof(apiKey));
        if (string.IsNullOrWhiteSpace(apiSecret)) throw new ArgumentException("apiSecret required", nameof(apiSecret));

        _apiKey         = apiKey;
        _apiSecretBytes = Convert.FromBase64String(apiSecret);
        _logger         = logger;

        if (http is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = http;
            _ownsHttpClient = false;
        }
    }

    /// <summary>
    /// Strictly monotonic nonce. Matches the worker's GetNonce() pattern so any nonces
    /// produced here are coherent with the order-placement nonces on the same key.
    /// </summary>
    private static long GetNonce()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _lastNonce);
            var candidate = Math.Max(DateTime.UtcNow.Ticks, current + 1);
            if (Interlocked.CompareExchange(ref _lastNonce, candidate, current) == current)
                return candidate;
        }
    }

    /// <summary>
    /// /0/private/DepositMethods — list deposit methods (networks) available for an asset.
    /// </summary>
    /// <param name="asset">Kraken asset code, e.g. "XBT", "ETH", "USDT".</param>
    public async Task<KrakenFundingResult<List<DepositMethod>>> GetDepositMethodsAsync(
        string asset, CancellationToken ct = default)
    {
        var nonce = GetNonce().ToString();
        var formParams = new List<KeyValuePair<string, string>>
        {
            new("nonce", nonce),
            new("asset", asset),
        };

        var json = await PostAsync(DepositMethodsPath, formParams, ct);
        if (json.Error != null)
        {
            return new KrakenFundingResult<List<DepositMethod>>
            {
                Success = false,
                Error = json.Error
            };
        }

        try
        {
            var methods = new List<DepositMethod>();
            using var doc = JsonDocument.Parse(json.Json!);
            if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string method = item.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    string? limit = item.TryGetProperty("limit", out var l)
                        ? l.ValueKind switch
                        {
                            JsonValueKind.String => l.GetString(),
                            JsonValueKind.False  => "false",
                            JsonValueKind.True   => "true",
                            _                    => l.ToString()
                        }
                        : null;
                    bool canGenerate = item.TryGetProperty("gen-address", out var g) && g.ValueKind == JsonValueKind.True;

                    methods.Add(new DepositMethod
                    {
                        Method      = method,
                        Limit       = limit,
                        CanGenerate = canGenerate,
                    });
                }
            }

            return new KrakenFundingResult<List<DepositMethod>> { Success = true, Data = methods };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse DepositMethods response");
            return new KrakenFundingResult<List<DepositMethod>> { Success = false, Error = "parse-error" };
        }
    }

    /// <summary>
    /// /0/private/DepositAddresses — retrieve existing addresses (or generate a new one).
    /// </summary>
    /// <param name="asset">Kraken asset code.</param>
    /// <param name="method">Method name from DepositMethods.</param>
    /// <param name="generateNew">When true, Kraken creates a fresh address (uses new=true).</param>
    public async Task<KrakenFundingResult<List<DepositAddress>>> GetDepositAddressesAsync(
        string asset, string method, bool generateNew = false, CancellationToken ct = default)
    {
        var nonce = GetNonce().ToString();
        var formParams = new List<KeyValuePair<string, string>>
        {
            new("nonce",  nonce),
            new("asset",  asset),
            new("method", method),
        };
        if (generateNew) formParams.Add(new("new", "true"));

        var json = await PostAsync(DepositAddressesPath, formParams, ct);
        if (json.Error != null)
        {
            return new KrakenFundingResult<List<DepositAddress>>
            {
                Success = false,
                Error = json.Error
            };
        }

        try
        {
            var addresses = new List<DepositAddress>();
            using var doc = JsonDocument.Parse(json.Json!);
            if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string address = item.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
                    string expire  = item.TryGetProperty("expiretm", out var e)
                        ? e.ValueKind switch
                        {
                            JsonValueKind.Number => e.GetRawText(),
                            JsonValueKind.String => e.GetString() ?? "0",
                            _                    => "0"
                        }
                        : "0";
                    bool isNew = item.TryGetProperty("new", out var n) && n.ValueKind == JsonValueKind.True;

                    if (!string.IsNullOrEmpty(address))
                    {
                        addresses.Add(new DepositAddress
                        {
                            Address    = address,
                            ExpireTime = expire,
                            IsNew      = isNew,
                        });
                    }
                }
            }

            return new KrakenFundingResult<List<DepositAddress>> { Success = true, Data = addresses };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse DepositAddresses response");
            return new KrakenFundingResult<List<DepositAddress>> { Success = false, Error = "parse-error" };
        }
    }

    /// <summary>
    /// /0/private/GetApiKeyInfo - retrieve metadata about the API key used to
    /// make the request (name, permissions, IP allowlist, timestamps).
    ///
    /// Requires no permissions on the key itself. Used by the Account page's
    /// Test Connection feature.
    /// </summary>
    public async Task<KrakenFundingResult<ApiKeyInfo>> GetApiKeyInfoAsync(CancellationToken ct = default)
    {
        var nonce = GetNonce().ToString();
        var formParams = new List<KeyValuePair<string, string>>
        {
            new("nonce", nonce),
        };

        var json = await PostAsync(GetApiKeyInfoPath, formParams, ct);
        if (json.Error != null)
        {
            return new KrakenFundingResult<ApiKeyInfo> { Success = false, Error = json.Error };
        }

        try
        {
            using var doc = JsonDocument.Parse(json.Json!);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return new KrakenFundingResult<ApiKeyInfo> { Success = false, Error = "missing result" };
            }

            var info = new ApiKeyInfo
            {
                ApiKeyName   = GetStringOrEmpty(result, "apiKeyName"),
                ApiKey       = GetStringOrEmpty(result, "apiKey"),
                ValidUntil   = GetStringOrEmpty(result, "validUntil",  "0"),
                CreatedTime  = GetStringOrEmpty(result, "createdTime", "0"),
                ModifiedTime = GetStringOrEmpty(result, "modifiedTime","0"),
                LastUsed     = GetStringOrEmpty(result, "lastUsed",    "0"),
                Permissions  = ReadStringArray(result, "permissions"),
                IpAllowlist  = ReadStringArray(result, "ipAllowlist"),
            };

            return new KrakenFundingResult<ApiKeyInfo> { Success = true, Data = info };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse GetApiKeyInfo response");
            return new KrakenFundingResult<ApiKeyInfo> { Success = false, Error = "parse-error" };
        }
    }

    private static string GetStringOrEmpty(JsonElement el, string name, string fallback = "")
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? fallback,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _                    => fallback
        };
    }

    private static List<string> ReadStringArray(JsonElement el, string name)
    {
        var list = new List<string>();
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    // -------------------------------------------------------------------------

    private record HttpJsonResult(string? Json, string? Error);    private async Task<HttpJsonResult> PostAsync(
        string path, List<KeyValuePair<string, string>> formParams, CancellationToken ct)
    {
        var postBody = string.Join("&", formParams.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        // The nonce is the first key in formParams by convention.
        var nonce = formParams[0].Value;
        var signature = ComputeSignature(nonce, postBody, path);

        using var request = new HttpRequestMessage(HttpMethod.Post, KrakenApiBase + path)
        {
            Content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("API-Key",  _apiKey);
        request.Headers.Add("API-Sign", signature);

        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kraken {Path} HTTP {Status}: {Body}", path, (int)response.StatusCode, body);
                return new HttpJsonResult(null, $"HTTP {(int)response.StatusCode}");
            }

            // Kraken returns "error" as an array; empty == success
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.Array
                    && err.GetArrayLength() > 0)
                {
                    var msg = string.Join("; ", err.EnumerateArray().Select(e => e.GetString() ?? "?"));
                    _logger.LogWarning("Kraken {Path} business error: {Msg}", path, msg);
                    return new HttpJsonResult(null, msg);
                }
            }
            catch (JsonException)
            {
                /* parse failure falls through to caller */
            }

            return new HttpJsonResult(body, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "HTTP error calling Kraken {Path}", path);
            return new HttpJsonResult(null, ex.Message);
        }
    }

    private string ComputeSignature(string nonce, string postBody, string uriPath)
    {
        // Step 1: SHA256( nonce + post_body )
        var sha256Input = Encoding.UTF8.GetBytes(nonce + postBody);
        byte[] sha256Hash;
        using (var sha256 = SHA256.Create())
            sha256Hash = sha256.ComputeHash(sha256Input);

        // Step 2: HMAC-SHA512( decoded_secret, path_bytes + sha256_bytes )
        var pathBytes = Encoding.UTF8.GetBytes(uriPath);
        var hmacInput = pathBytes.Concat(sha256Hash).ToArray();

        using var hmac = new HMACSHA512(_apiSecretBytes);
        return Convert.ToBase64String(hmac.ComputeHash(hmacInput));
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
