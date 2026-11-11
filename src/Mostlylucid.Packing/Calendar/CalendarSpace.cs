using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mostlylucid.Packing.Planning;

namespace Mostlylucid.Packing.Calendar;

public sealed record CalendarPlan(PackingResult Summary, IReadOnlyList<CalendarMatch> Matches);

public sealed class CalendarSpace
{
    private readonly CalendarDefinition _definition;
    private readonly ICalendarStore _store;
    private readonly IPackingSolver _solver;

    public CalendarSpace(CalendarDefinition definition, ICalendarStore store, IPackingSolver? solver = null)
    {
        _definition = definition;
        _store = store;
        _solver = solver ?? new GreedyPackingSolver();
    }

    public CalendarQuery Query() => new(this, [_definition.Bounds], null, [], [Criteria.Preference, Criteria.Start]);

    public SearchResult FindBest(DateTimeOffset from, DateTimeOffset to, TimeSpan size, BookingRequirements restrictions) =>
        Query().During([new Period(from, to)]).OfSize(size).Where(restrictions).TopK(1);

    public SearchResult Find(DateTimeOffset from, DateTimeOffset to, TimeSpan size,
        BookingRequirements restrictions, int limit = 10) =>
        Query().During([new Period(from, to)]).OfSize(size).Where(restrictions).TopK(limit);

    public SearchResult FindTogether(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<BookingRequirements> members,
        int limit = 5)
    {
        if (members.Count == 0) throw new ArgumentException("A group needs members.");
        var requests = members.Select((member, index) => Freeze(new BookingRequest($"member-{index}", member))).ToArray();
        return Search([new Period(from, to)], requests, [Criteria.Preference, Criteria.Start], limit, null, 0);
    }

    public FitResult Fits(DateTimeOffset at, TimeSpan size, BookingRequirements restrictions)
    {
        ValidateSize(size, restrictions.ServiceId);
        var request = Freeze(new BookingRequest("search", restrictions));
        var service = _definition.Services[restrictions.ServiceId];
        var footprint = TimeSpan.FromTicks(Math.Max(service.Duration.Ticks,
            service.Stages.Max(stage => (stage.Offset + stage.Duration).Ticks)));
        if (at < _definition.Bounds.From || at > _definition.Bounds.To - footprint)
            return new(false, "OutsideWindow");
        var result = Search([new Period(at, at + footprint)], [request], [Criteria.Preference], 1, null, 0);
        return new(result.Best is not null, result.Best is not null ? null :
            result.CandidatesComplete ? "No feasible resource bundle at the exact start." : "SearchLimit",
            result.CandidatesComplete);
    }

    public CalendarPlan Plan(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<BookingRequest> requests,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var input = requests.Select(Freeze).ToArray();
        if (input.Select(request => request.Id).Distinct(StringComparer.Ordinal).Count() != input.Length)
            throw new ArgumentException("Request IDs must be unique.");
        var window = Window(new Period(from, to));
        var snapshot = Capture(window);
        var bindings = new Dictionary<string, CalendarMatch>(StringComparer.Ordinal);
        var choices = new List<Candidate>();
        var complete = true;
        foreach (var request in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var match in Generate([window], [request], snapshot))
            {
                if (choices.Count == _definition.CandidateLimit) { complete = false; break; }
                choices.Add(new Candidate(match.Id, request.Id, Consumption(match), [match.PreferenceScore]));
                bindings.Add(match.Id, match);
            }
            if (!complete) break;
        }
        var capacities = choices.SelectMany(choice => choice.Demands).Select(demand => demand.Dimension).Distinct()
            .Select(dimension =>
            {
                var key = ParseDimension(dimension);
                return new Capacity(dimension, snapshot.Capacity.GetValueOrDefault(key) - snapshot.Used.GetValueOrDefault(key));
            });
        var problem = new PackingProblem(capacities, input.Select(request => new Request(request.Id, request.Required)),
            choices, [1], new PlanningSnapshot(_definition.Id, 0, snapshot.Version,
                Floor(window.From), Ceiling(window.To)), complete);
        var result = _solver.Solve(problem, budget, cancellationToken);
        return new(result, Array.AsReadOnly(result.Selected.Select(id => bindings[id]).ToArray()));
    }

    public ReservationReply TryHold(Guid commandId, string owner, CalendarMatch match, TimeSpan lifetime)
    {
        if (match.SpaceId != _definition.Id || match.DefinitionVersion != _definition.Version)
            return new(false, "DefinitionChanged", null, match.Version);
        if (match.Demands.Any(demand => !match.Windows.Any(window => window.Contains(demand.Time))))
            return new(false, "OutsideWindow", null, match.Version);
        var assigned = match.Appointments.SelectMany(appointment => appointment.Resources.Select(pair =>
            new KeyValuePair<(string Request, string Role), string>((appointment.RequestId, pair.Key), pair.Value)))
            .ToDictionary();
        var rebuilt = Build(match.Start, match.Requests, match.Windows, assigned, match.Version);
        return _store.TryHold(new HoldCommand(commandId, owner, rebuilt.Id, _definition.Version, rebuilt.Demands, lifetime));
    }

    public ReservationReply Confirm(Guid commandId, string owner, Guid holdId) => _store.Confirm(commandId, owner, holdId);
    public ReservationReply Cancel(Guid commandId, string owner, Guid reservationId) => _store.Cancel(commandId, owner, reservationId);
    public BlockPage GetBlocks(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> resources, int limit = 1000) =>
        _store.GetBlocks(Window(new Period(from, to)), resources, limit);

    public BlockViewPage GetBlockPage(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> resources,
        int limit = 1000, BlockPageCursor? after = null) =>
        Projections().GetBlockPage(Window(new Period(from, to)), resources, limit, after);

    public BlockDelta GetBlockChanges(BlockViewToken since, int limit = 1000) =>
        Projections().GetBlockChanges(since, limit);

    public CapacityView GetCapacityBlocks(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> resources,
        TimeSpan resolution, int maxCells = 1_000_000) =>
        Projections().GetCapacityBlocks(Window(new Period(from, to)), resources, resolution, maxCells);

    private IBlockProjectionStore Projections() => _store as IBlockProjectionStore ??
        throw new NotSupportedException("This store does not implement block projections.");

    internal SearchResult Search(IReadOnlyList<Period> periods, IReadOnlyList<BookingRequest> requests,
        IReadOnlyList<MatchCriterion> criteria, int limit, SearchCursor? cursor, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (requests.Count == 0) throw new ArgumentException("Select a service and client before searching.");
        var windows = Period.Union(periods.Select(Window));
        if (windows.Count == 0) throw new ArgumentException("Select at least one search period.");
        var snapshot = Capture(new Period(windows[0].From, windows[^1].To));
        var queryKey = Hash(JsonSerializer.Serialize(new
        {
            Space = _definition.Id, Definition = _definition.Version,
            Windows = windows.Select(p => new[] { p.From.UtcTicks, p.To.UtcTicks }),
            Requests = requests.Select(r => new { r.Id, r.Requirements.ServiceId, r.Requirements.ClientId,
                Required = r.Requirements.RequiredResources!.OrderBy(p => p.Key, StringComparer.Ordinal),
                Preferred = r.Requirements.PreferredResources!.OrderBy(p => p.Key, StringComparer.Ordinal) }),
            Criteria = criteria.Select(c => new { c.Name, c.Version, c.Descending })
        }));
        if (cursor is not null && (cursor.Query != queryKey || cursor.Version != snapshot.Version))
            throw new InvalidOperationException("The navigation cursor is stale. Refresh this query.");

        var pool = Generate(windows, requests, snapshot).Take(_definition.CandidateLimit + 1).ToArray();
        var complete = pool.Length <= _definition.CandidateLimit;
        var feasible = pool.Take(_definition.CandidateLimit).Where(match => CanFit(match, snapshot));
        IOrderedEnumerable<CalendarMatch>? ordered = null;
        foreach (var criterion in criteria)
            ordered = ordered is null
                ? criterion.Descending ? feasible.OrderByDescending(criterion.Value) : feasible.OrderBy(criterion.Value)
                : criterion.Descending ? ordered.ThenByDescending(criterion.Value) : ordered.ThenBy(criterion.Value);
        var ranked = (ordered ?? feasible.OrderBy(match => match.Start)).ThenBy(match => match.Id, StringComparer.Ordinal).ToList();
        IEnumerable<CalendarMatch> selected = ranked;
        if (cursor is not null)
        {
            var index = ranked.FindIndex(match => match.Id == cursor.MatchId);
            if (index < 0) throw new InvalidOperationException("The cursor match is no longer available. Refresh this query.");
            selected = direction < 0 ? ranked.Take(index).Reverse() : ranked.Skip(index + 1);
        }
        var matches = selected.Take(limit).ToArray();
        var next = matches.Length == 0 ? null : new SearchCursor(queryKey, snapshot.Version, matches[^1].Id);
        return new(matches.Length > 0 ? SearchOutcome.Found : complete ? SearchOutcome.NoMatch : SearchOutcome.Incomplete,
            Array.AsReadOnly(matches), complete, snapshot.Version, next);
    }

    internal SearchResult FeasiblePool(IReadOnlyList<Period> periods, IReadOnlyList<BookingRequest> requests,
        IReadOnlyList<MatchCriterion> criteria) =>
        Search(periods, requests, criteria, _definition.CandidateLimit, null, 0);

    internal BookingRequest Single(TimeSpan? size, BookingRequirements requirement)
    {
        if (size is null) throw new ArgumentException("Specify the requested duration.");
        ValidateSize(size.Value, requirement.ServiceId);
        return Freeze(new BookingRequest("search", requirement));
    }

    private void ValidateSize(TimeSpan size, string serviceId)
    {
        if (!_definition.Services.TryGetValue(serviceId, out var service)) throw new ArgumentException("Unknown service.");
        if (size != service.Duration) throw new ArgumentException("Requested size must match the service duration.");
    }

    private BookingRequest Freeze(BookingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        var required = request.Requirements.RequiredResources?.ToFrozenDictionary(StringComparer.Ordinal)
                       ?? FrozenDictionary<string, string>.Empty;
        var preferred = request.Requirements.PreferredResources?.ToFrozenDictionary(StringComparer.Ordinal)
                        ?? FrozenDictionary<string, string>.Empty;
        if (!_definition.Services.TryGetValue(request.Requirements.ServiceId, out var service) ||
            !_definition.Clients.ContainsKey(request.Requirements.ClientId)) throw new ArgumentException("Unknown service or client.");
        var roles = service.Roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);
        if (required.Keys.Concat(preferred.Keys).Any(role => !roles.Contains(role)))
            throw new ArgumentException("A restriction references an unknown service role.");
        return request with { Requirements = request.Requirements with { RequiredResources = required, PreferredResources = preferred } };
    }

    private IEnumerable<CalendarMatch> Generate(IReadOnlyList<Period> windows, IReadOnlyList<BookingRequest> requests,
        CalendarSnapshot snapshot)
    {
        var options = new List<((string Request, string Role) Key, string[] Values)>();
        foreach (var request in requests)
        {
            var client = _definition.Clients[request.Requirements.ClientId];
            if (!client.MayBook) yield break;
            foreach (var role in _definition.Services[request.Requirements.ServiceId].Roles)
            {
                var required = request.Requirements.RequiredResources!.GetValueOrDefault(role.Id);
                var eligible = _definition.Resources.Values.Where(resource => role.Capabilities.IsSubsetOf(resource.Capabilities) &&
                        !client.ExcludedResources.Contains(resource.Id) && (required is null || required == resource.Id))
                    .OrderBy(resource => resource.Id, StringComparer.Ordinal).Select(resource => resource.Id).ToArray();
                if (eligible.Length == 0) yield break;
                options.Add(((request.Id, role.Id), eligible));
            }
        }
        var footprint = requests.Max(request =>
        {
            var service = _definition.Services[request.Requirements.ServiceId];
            return TimeSpan.FromTicks(Math.Max(service.Duration.Ticks,
                service.Stages.Max(stage => (stage.Offset + stage.Duration).Ticks)));
        });
        foreach (var window in windows)
        {
            if (window.Length < footprint) continue;
            var first = Math.Max(Ceiling(window.From), Ceiling(_store.Clock.GetUtcNow()));
            for (var slot = first; slot <= Floor(window.To - footprint); slot++)
            foreach (var assigned in Assign(options, 0, new()))
            {
                var match = Build(At(slot), requests, windows, assigned, snapshot.Version);
                if (match.Demands.All(demand => windows.Any(window => window.Contains(demand.Time))))
                    yield return match;
            }
        }
    }

    private CalendarMatch Build(DateTimeOffset start, IReadOnlyList<BookingRequest> requests,
        IReadOnlyList<Period> windows, IReadOnlyDictionary<(string Request, string Role), string> assigned, long version)
    {
        var demands = new List<ResourceDemand>();
        var appointments = new List<AppointmentPlacement>();
        long preference = 0;
        foreach (var request in requests)
        {
            var service = _definition.Services[request.Requirements.ServiceId];
            var client = _definition.Clients[request.Requirements.ClientId];
            if (!client.MayBook) throw new InvalidOperationException("Client is not eligible.");
            var bindings = service.Roles.ToDictionary(role => role.Id, role => assigned[(request.Id, role.Id)], StringComparer.Ordinal);
            foreach (var role in service.Roles)
            {
                var resource = _definition.Resources[bindings[role.Id]];
                if (!role.Capabilities.IsSubsetOf(resource.Capabilities) || client.ExcludedResources.Contains(resource.Id) ||
                    (request.Requirements.RequiredResources!.TryGetValue(role.Id, out var required) && required != resource.Id))
                    throw new InvalidOperationException("Resource assignment violates eligibility.");
                if (request.Requirements.PreferredResources!.GetValueOrDefault(role.Id) == resource.Id)
                    preference = checked(preference + 1000);
                var periods = service.Stages.Where(stage => stage.Roles.Contains(role.Id)).Select(stage =>
                    new Period(start + stage.Offset, start + stage.Offset + stage.Duration));
                demands.AddRange(Period.Union(periods).Select(period => new ResourceDemand(resource.Id, period, role.Units)));
            }
            var clientTime = new Period(start, start + service.Duration);
            demands.Add(new ResourceDemand(CalendarDefinition.ClientResource(client.Id), clientTime));
            appointments.Add(new AppointmentPlacement(request.Id, service.Id, client.Id, start, clientTime.To,
                bindings.ToFrozenDictionary(StringComparer.Ordinal)));
        }
        var id = Hash(JsonSerializer.Serialize(new
        {
            Start = start.UtcTicks,
            Members = appointments.Select(a => new { a.RequestId, a.ServiceId, a.ClientId,
                Bindings = a.Resources.OrderBy(p => p.Key, StringComparer.Ordinal) })
        }));
        return new CalendarMatch(id, _definition, version, start, requests, windows,
            appointments.AsReadOnly(), demands.AsReadOnly(), preference);
    }

    private static IEnumerable<Dictionary<(string Request, string Role), string>> Assign(
        List<((string Request, string Role) Key, string[] Values)> options, int index,
        Dictionary<(string Request, string Role), string> assigned)
    {
        if (index == options.Count) { yield return new(assigned); yield break; }
        foreach (var resource in options[index].Values)
        {
            assigned[options[index].Key] = resource;
            foreach (var result in Assign(options, index + 1, assigned)) yield return result;
        }
    }

    private bool CanFit(CalendarMatch match, CalendarSnapshot snapshot)
    {
        var used = new Dictionary<ResourceSlot, long>();
        foreach (var demand in match.Demands)
        for (var slot = _definition.Slot(demand.Time.From); slot < _definition.Slot(demand.Time.To); slot++)
        {
            var key = new ResourceSlot(demand.ResourceId, slot);
            used[key] = checked(used.GetValueOrDefault(key) + demand.Units);
            if (used[key] + snapshot.Used.GetValueOrDefault(key) > snapshot.Capacity.GetValueOrDefault(key)) return false;
        }
        return true;
    }

    private IReadOnlyList<Consumption> Consumption(CalendarMatch match) => match.Demands.SelectMany(demand =>
        Enumerable.Range(0, checked((int)(_definition.Slot(demand.Time.To) - _definition.Slot(demand.Time.From))))
            .Select(offset => new Consumption(new DimensionId(JsonSerializer.Serialize(new[]
            {
                demand.ResourceId, (_definition.Slot(demand.Time.From) + offset).ToString(CultureInfo.InvariantCulture)
            })), demand.Units))).ToArray();

    private static ResourceSlot ParseDimension(DimensionId dimension)
    {
        var values = JsonSerializer.Deserialize<string[]>(dimension.Value)!;
        return new(values[0], long.Parse(values[1], CultureInfo.InvariantCulture));
    }

    private CalendarSnapshot Capture(Period window)
    {
        var snapshot = _store.Capture(window);
        if (snapshot.DefinitionVersion != _definition.Version)
            throw new InvalidOperationException("The store belongs to a different space definition.");
        return snapshot;
    }

    private Period Window(Period period)
    {
        if (!_definition.Bounds.Contains(period)) throw new ArgumentOutOfRangeException(nameof(period), "Window is outside the space.");
        return period;
    }

    private long Floor(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var slot = Math.DivRem(ticks, _definition.Grid.Ticks, out var remainder);
        return remainder < 0 ? slot - 1 : slot;
    }

    private long Ceiling(DateTimeOffset time) => At(Floor(time)) == time ? Floor(time) : Floor(time) + 1;
    private DateTimeOffset At(long slot) => _definition.At(slot);
    private static string Hash(string data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
}

public sealed class CalendarQuery
{
    private readonly CalendarSpace _space;
    private readonly IReadOnlyList<Period> _periods;
    private readonly TimeSpan? _size;
    private readonly IReadOnlyList<BookingRequest> _requests;
    private readonly IReadOnlyList<MatchCriterion> _criteria;

    internal CalendarQuery(CalendarSpace space, IReadOnlyList<Period> periods, TimeSpan? size,
        IReadOnlyList<BookingRequest> requests, IReadOnlyList<MatchCriterion> criteria)
    {
        _space = space;
        _periods = periods;
        _size = size;
        _requests = requests;
        _criteria = criteria;
    }

    public CalendarQuery During(IEnumerable<Period> periods) => new(_space, Period.Union(periods), _size, _requests, _criteria);
    public CalendarQuery OfSize(TimeSpan size)
    {
        var requests = _requests.Select(request => _space.Single(size, request.Requirements)).ToArray();
        return new(_space, _periods, size, requests, _criteria);
    }
    public CalendarQuery Where(BookingRequirements restrictions) => new(_space, _periods, _size,
        [_space.Single(_size, restrictions)], _criteria);
    public CalendarQuery OrderBy(MatchCriterion criterion) => new(_space, _periods, _size, _requests, [criterion]);
    public CalendarQuery ThenBy(MatchCriterion criterion) => new(_space, _periods, _size, _requests, [.. _criteria, criterion]);
    public SearchResult GetFeasibleCandidates() => _space.FeasiblePool(_periods, _requests, _criteria);
    public SearchResult TopK(int count) => _space.Search(_periods, _requests, _criteria, count, null, 0);
    public SearchResult FindBest() => TopK(1);
    public SearchResult Nearest(DateTimeOffset to) => OrderBy(Criteria.Nearest(to)).FindBest();
    public SearchResult Next(SearchCursor after) => _space.Search(_periods, _requests, _criteria, 1, after, 1);
    public SearchResult Previous(SearchCursor before) => _space.Search(_periods, _requests, _criteria, 1, before, -1);
}
