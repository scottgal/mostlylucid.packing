using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mostlylucid.Packing.Calendar;

public sealed record Period
{
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public TimeSpan Length => To - From;

    public Period(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw new ArgumentException("A period must have positive length.");
        From = from.ToUniversalTime();
        To = to.ToUniversalTime();
    }

    public bool Contains(Period other) => From <= other.From && To >= other.To;
    public bool Overlaps(Period other) => From < other.To && other.From < To;

    public static IReadOnlyList<Period> Union(IEnumerable<Period> periods)
    {
        var result = new List<Period>();
        foreach (var period in periods.OrderBy(period => period.From))
        {
            if (result.Count == 0 || result[^1].To < period.From) result.Add(period);
            else result[^1] = new Period(result[^1].From, period.To > result[^1].To ? period.To : result[^1].To);
        }
        return result.AsReadOnly();
    }
}

public sealed record CalendarResource(string Id, int Capacity, IReadOnlySet<string> Capabilities,
    IReadOnlyList<Period> Availability);
public sealed record Client(string Id, bool MayBook, IReadOnlySet<string> ExcludedResources);
public sealed record ServiceRole(string Id, IReadOnlySet<string> Capabilities, int Units = 1);
public sealed record ServiceStage(string Id, TimeSpan Offset, TimeSpan Duration, IReadOnlyList<string> Roles);
public sealed record Service(string Id, TimeSpan Duration, IReadOnlyList<ServiceRole> Roles,
    IReadOnlyList<ServiceStage> Stages);
public sealed record BookingRequirements(string ServiceId, string ClientId,
    IReadOnlyDictionary<string, string>? RequiredResources = null,
    IReadOnlyDictionary<string, string>? PreferredResources = null);
public sealed record BookingRequest(string Id, BookingRequirements Requirements, bool Required = false);

public sealed class CalendarDefinition
{
    public string Id { get; }
    public Period Bounds { get; }
    public TimeSpan Grid { get; }
    public IReadOnlyDictionary<string, CalendarResource> Resources { get; }
    public IReadOnlyDictionary<string, Client> Clients { get; }
    public IReadOnlyDictionary<string, Service> Services { get; }
    public string Version { get; }
    public int CandidateLimit { get; }

    public CalendarDefinition(string id, Period bounds, TimeSpan grid, IEnumerable<CalendarResource> resources,
        IEnumerable<Client> clients, IEnumerable<Service> services, int candidateLimit = 10_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (grid <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateLimit);
        Id = id;
        Bounds = bounds;
        Grid = grid;
        CandidateLimit = candidateLimit;
        _ = Slot(bounds.From);
        _ = Slot(bounds.To);
        Resources = resources.Select(resource =>
        {
            ValidateId(resource.Id);
            if (resource.Id.StartsWith("client/", StringComparison.Ordinal))
                throw new ArgumentException("Resource IDs beginning with client/ are reserved for client calendars.");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resource.Capacity);
            var availability = Period.Union(resource.Availability);
            foreach (var period in availability)
            {
                if (!bounds.Contains(period)) throw new ArgumentException("Availability is outside the space bounds.");
                _ = Slot(period.From);
                _ = Slot(period.To);
            }
            return resource with { Capabilities = resource.Capabilities.ToFrozenSet(StringComparer.Ordinal),
                Availability = availability };
        }).ToFrozenDictionary(resource => resource.Id, StringComparer.Ordinal);
        Clients = clients.Select(client =>
        {
            ValidateId(client.Id);
            if (client.ExcludedResources.Any(id => !Resources.ContainsKey(id)))
                throw new ArgumentException("A client exclusion references an unknown resource.");
            return client with { ExcludedResources = client.ExcludedResources.ToFrozenSet(StringComparer.Ordinal) };
        }).ToFrozenDictionary(client => client.Id, StringComparer.Ordinal);
        Services = services.Select(service =>
        {
            ValidateId(service.Id);
            ValidateDuration(service.Duration);
            var roles = service.Roles.Select(role =>
            {
                ValidateId(role.Id);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(role.Units);
                return role with { Capabilities = role.Capabilities.ToFrozenSet(StringComparer.Ordinal) };
            }).ToDictionary(role => role.Id, StringComparer.Ordinal);
            var stages = service.Stages.Select(stage =>
            {
                ValidateId(stage.Id);
                ValidateDuration(stage.Duration);
                if (stage.Offset < TimeSpan.Zero || stage.Offset.Ticks % Grid.Ticks != 0)
                    throw new ArgumentException("Stage offsets must be nonnegative and grid-aligned.");
                var end = stage.Offset + stage.Duration;
                if (end > Bounds.Length) throw new ArgumentException("Stage footprint exceeds the horizon.");
                var used = stage.Roles.Distinct(StringComparer.Ordinal).ToArray();
                if (used.Any(role => !roles.ContainsKey(role))) throw new ArgumentException("Unknown stage role.");
                return stage with { Roles = Array.AsReadOnly(used) };
            }).ToArray();
            if (stages.Length == 0 || roles.Keys.Any(role => !stages.Any(stage => stage.Roles.Contains(role))))
                throw new ArgumentException("Every service role needs a stage.");
            if (stages.Select(stage => stage.Id).Distinct(StringComparer.Ordinal).Count() != stages.Length)
                throw new ArgumentException("Stage IDs must be unique.");
            return service with { Roles = Array.AsReadOnly(roles.Values.ToArray()), Stages = Array.AsReadOnly(stages) };
        }).ToFrozenDictionary(service => service.Id, StringComparer.Ordinal);

        // Canonical data identifies the entire policy, units and source configuration on reopen.
        var canonical = JsonSerializer.Serialize(new
        {
            Id, From = Bounds.From.UtcTicks, To = Bounds.To.UtcTicks, Grid = Grid.Ticks,
            Resources = Resources.Values.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new
            {
                r.Id, r.Capacity, Capabilities = r.Capabilities.Order(StringComparer.Ordinal),
                Availability = r.Availability.Select(p => new { From = p.From.UtcTicks, To = p.To.UtcTicks })
            }),
            Clients = Clients.Values.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new
            {
                c.Id, c.MayBook, Exclusions = c.ExcludedResources.Order(StringComparer.Ordinal)
            }),
            Services = Services.Values.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new
            {
                s.Id, Duration = s.Duration.Ticks,
                Roles = s.Roles.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new
                {
                    r.Id, r.Units, Capabilities = r.Capabilities.Order(StringComparer.Ordinal)
                }),
                Stages = s.Stages.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new
                {
                    s.Id, Offset = s.Offset.Ticks, Duration = s.Duration.Ticks, Roles = s.Roles.Order(StringComparer.Ordinal)
                })
            })
        });
        Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public long Slot(DateTimeOffset time)
    {
        var ticks = checked(time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks);
        if (ticks % Grid.Ticks != 0) throw new ArgumentException("Time must align to the space grid.");
        return ticks / Grid.Ticks;
    }

    public DateTimeOffset At(long slot) => DateTimeOffset.UnixEpoch.AddTicks(checked(slot * Grid.Ticks));
    public static string ClientResource(string client) => "client/" + client;

    private static void ValidateId(string id) => ArgumentException.ThrowIfNullOrWhiteSpace(id);

    private void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration.Ticks % Grid.Ticks != 0 || duration > Bounds.Length)
            throw new ArgumentException("Duration must be positive, grid-aligned and inside the horizon.");
    }
}
