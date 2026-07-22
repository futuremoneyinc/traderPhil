namespace TraderPhil.V4.Web.Kraken;

/// <summary>
/// What permissions TraderPhil requires (and forbids) on a Kraken API key.
///
/// Kraken returns permission strings in the GetApiKeyInfo response's
/// `permissions` array - e.g. ["query-funds", "modify-trades"]. The exact
/// strings come from Kraken; if they change them, this is the one place to
/// update.
///
/// Update history:
///   2026-06: Initial set. Full audit pending; for now we require enough to
///            run all current trading + reconciliation paths and forbid
///            anything that could enable funds movement.
/// </summary>
public static class KrakenKeyPermissionPolicy
{
    /// <summary>
    /// Permissions the key MUST have. A missing required permission means
    /// the worker will fail somewhere - either silently or with a Kraken
    /// "EAPI:Invalid permissions" error - so we reject keys without these.
    /// </summary>
    public static readonly IReadOnlySet<string> Required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "query-funds",          // Balance/TradeBalance calls
        "query-open-trades",    // OpenOrders, OpenPositions
        "query-closed-trades",  // ClosedOrders, QueryOrders, QueryTrades
        "modify-trades",        // AddOrder, EditOrder, CancelOrder, AddOrderBatch
        "query-ledger-entries", // ReconciliationService + fee math
    };

    /// <summary>
    /// Permissions the key MUST NOT have. Withdraw access means a malicious
    /// user could later argue we moved their funds; refusing to ever store a
    /// key with these permissions cleanly eliminates that attack class.
    /// Margin is in the same bucket - we don't trade margin, and a key with
    /// it grants attack surface we don't need.
    /// </summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "withdraw-funds",
        // Future audit will add more. Any permission name containing "margin"
        // is also rejected dynamically below (Kraken naming TBD - this is the
        // catch-all today).
    };

    /// <summary>Human-readable label for a Kraken permission string.</summary>
    public static string DisplayName(string permission) =>
        permission?.ToLowerInvariant() switch
        {
            "query-funds"          => "Query funds (balances)",
            "deposit-funds"        => "Deposit funds",
            "withdraw-funds"       => "Withdraw funds",
            "query-open-trades"    => "Query open orders & trades",
            "query-closed-trades"  => "Query closed orders & trades",
            "modify-trades"        => "Modify orders (place / amend / cancel)",
            "query-ledger-entries" => "Query ledger entries",
            "export-data"          => "Export data",
            "websocket-interface"  => "WebSocket interface",
            _                      => permission ?? "(unknown)"
        };

    /// <summary>
    /// Evaluates a permission list against the policy. Returns a structured
    /// breakdown the UI can render as a checklist and that we can use to
    /// decide PASS/FAIL.
    /// </summary>
    public static KeyPermissionAudit Evaluate(IEnumerable<string>? grantedPermissions)
    {
        var granted = (grantedPermissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Required
            .Where(req => !granted.Contains(req))
            .ToList();

        var forbidden = granted
            .Where(g => Forbidden.Contains(g) || g.ToLowerInvariant().Contains("margin"))
            .ToList();

        return new KeyPermissionAudit
        {
            Granted        = granted,
            MissingRequired = missing,
            ForbiddenGranted = forbidden,
            Passed = missing.Count == 0 && forbidden.Count == 0,
        };
    }
}

public sealed class KeyPermissionAudit
{
    public HashSet<string> Granted          { get; init; } = new();
    public List<string>    MissingRequired   { get; init; } = new();
    public List<string>    ForbiddenGranted  { get; init; } = new();
    public bool            Passed            { get; init; }
}
