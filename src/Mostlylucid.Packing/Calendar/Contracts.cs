namespace Mostlylucid.Packing.Calendar;

public readonly record struct ResourceSlot(string ResourceId, long Slot);
public sealed record ResourceDemand(string ResourceId, Period Time, int Units = 1);
public sealed record CalendarSnapshot(long Version, string DefinitionVersion,
    IReadOnlyDictionary<ResourceSlot, int> Capacity, IReadOnlyDictionary<ResourceSlot, long> Used);
public enum ReservationState { Held, Confirmed, Expired, Cancelled }
public sealed record ReservationReply(bool Success, string Reason, Guid? ReservationId, long Version,
    DateTimeOffset? ExpiresAt = null);
public sealed record HoldCommand(Guid Id, string Owner, string Intent, string DefinitionVersion,
    IReadOnlyList<ResourceDemand> Demands, TimeSpan Lifetime);
public sealed record CalendarBlock(long Id, Guid ReservationId, string ResourceId, Period Time,
    ReservationState State, DateTimeOffset? ExpiresAt);
public sealed record BlockPage(long Version, IReadOnlyList<CalendarBlock> Blocks, bool Truncated);

public interface ICalendarStore
{
    TimeProvider Clock { get; }
    CalendarSnapshot Capture(Period window);
    ReservationReply TryHold(HoldCommand command);
    ReservationReply Confirm(Guid commandId, string owner, Guid holdId);
    ReservationReply Cancel(Guid commandId, string owner, Guid reservationId);
    BlockPage GetBlocks(Period window, IReadOnlyList<string> resourceIds, int limit = 1000);
}

public sealed record AppointmentPlacement(string RequestId, string ServiceId, string ClientId,
    DateTimeOffset Start, DateTimeOffset End, IReadOnlyDictionary<string, string> Resources);

public sealed class CalendarMatch
{
    internal IReadOnlyList<BookingRequest> Requests { get; }
    internal IReadOnlyList<Period> Windows { get; }
    public string Id { get; }
    public string SpaceId { get; }
    public string DefinitionVersion { get; }
    public long Version { get; }
    public DateTimeOffset Start { get; }
    public IReadOnlyList<AppointmentPlacement> Appointments { get; }
    public IReadOnlyList<ResourceDemand> Demands { get; }
    public long PreferenceScore { get; }

    internal CalendarMatch(string id, CalendarDefinition definition, long version, DateTimeOffset start,
        IReadOnlyList<BookingRequest> requests, IReadOnlyList<Period> windows,
        IReadOnlyList<AppointmentPlacement> appointments, IReadOnlyList<ResourceDemand> demands, long preferenceScore)
    {
        Id = id;
        SpaceId = definition.Id;
        DefinitionVersion = definition.Version;
        Version = version;
        Start = start;
        Requests = requests;
        Windows = windows;
        Appointments = appointments;
        Demands = demands;
        PreferenceScore = preferenceScore;
    }
}

public sealed record SearchCursor(string Query, long Version, string MatchId);
public enum SearchOutcome { Found, NoMatch, Incomplete }
public sealed record SearchResult(SearchOutcome Outcome, IReadOnlyList<CalendarMatch> Matches,
    bool CandidatesComplete, long Version, SearchCursor? Cursor)
{
    public CalendarMatch? Best => Matches.Count == 0 ? null : Matches[0];
}

public sealed record FitResult(bool Fits, string? Reason, bool Complete = true);
public sealed record MatchCriterion(string Name, string Version, Func<CalendarMatch, long> Value, bool Descending = false);

public static class Criteria
{
    public static MatchCriterion Start { get; } = new("start", "1", match => match.Start.UtcTicks);
    public static MatchCriterion Preference { get; } = new("preference", "1", match => match.PreferenceScore, true);
    public static MatchCriterion Nearest(DateTimeOffset anchor) =>
        new("time-distance", anchor.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            match => Math.Abs(match.Start.UtcTicks - anchor.UtcTicks));
}
