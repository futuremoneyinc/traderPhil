namespace TraderPhil.V4.Web.Services;

/// <summary>
/// Protection Mode is what happens when a subscription lapses. The promise to
/// the user is that we never abandon their money mid-trade: we stop *opening*
/// new risk immediately, keep *managing and closing* what's already open, and
/// only tear the account down after a two-week grace period with clear warnings
/// along the way.
///
/// This class is the single source of truth for that schedule. The web app uses
/// it to render the reassurance timeline in the onboarding Plans step and on the
/// Account page; the trading worker is expected to enforce the same day markers
/// against a lapsed subscription's start date. Keeping the schedule in one typed
/// place means the words the user reads and the behavior they get can't drift
/// apart.
/// </summary>
public static class ProtectionModePolicy
{
    public enum Phase
    {
        Active,           // subscription is current — nothing to do
        HoldNewPositions, // Day 0–4: no new parent positions, existing ones managed
        Reminder,         // Day 5–12: a nudge to resubscribe
        FinalWarning,     // Day 13: last call before cleanup
        Deactivated,      // Day 14+: keys removed, account data cleaned up
    }

    public sealed record Milestone(int Day, Phase Phase, string Title, string Detail);

    /// <summary>The timeline, in order. Days are counted from the lapse date (Day 0).</summary>
    public static readonly IReadOnlyList<Milestone> Timeline = new List<Milestone>
    {
        new(0,  Phase.HoldNewPositions,
            "New positions pause",
            "The moment a subscription lapses, TraderPhil stops opening new parent positions. Everything already open keeps being managed and closed safely — nothing is abandoned."),
        new(5,  Phase.Reminder,
            "A friendly reminder",
            "Five days in, we send a reminder that your subscription has lapsed and Protection Mode is holding the line."),
        new(13, Phase.FinalWarning,
            "Final warning",
            "On day 13 we send a final warning: reactivate now, or the account will be deactivated tomorrow."),
        new(14, Phase.Deactivated,
            "Deactivation & cleanup",
            "On day 14 the account is deactivated. API keys are removed and associated user data is cleaned up. Resubscribing after this point starts fresh."),
    };

    /// <summary>
    /// Given how many whole days ago the subscription lapsed, returns the phase
    /// currently in effect. A null lapse date means the subscription is active.
    /// </summary>
    public static Phase PhaseForLapseAgeDays(int? daysSinceLapse)
    {
        if (daysSinceLapse is not int d) return Phase.Active;
        if (d >= 14) return Phase.Deactivated;
        if (d >= 13) return Phase.FinalWarning;
        if (d >= 5)  return Phase.Reminder;
        if (d >= 0)  return Phase.HoldNewPositions;
        return Phase.Active;
    }

    /// <summary>Convenience overload that works from an absolute lapse timestamp.</summary>
    public static Phase PhaseForLapseDate(DateTime? lapsedAtUtc, DateTime nowUtc)
    {
        if (lapsedAtUtc is not DateTime lapsed) return Phase.Active;
        var days = (int)Math.Floor((nowUtc - lapsed).TotalDays);
        return PhaseForLapseAgeDays(days);
    }
}
