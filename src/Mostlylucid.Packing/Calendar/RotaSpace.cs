using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Mostlylucid.Packing.Planning;

namespace Mostlylucid.Packing.Calendar;

public sealed record ShiftRequirements(IReadOnlySet<string> Skills, string? PreferredEmployee = null,
    IReadOnlySet<string>? ExcludedEmployees = null);
public sealed record ShiftRequest(string Id, Period Window, TimeSpan Duration, ShiftRequirements Requirements, bool Required = true);
public sealed record ShiftPlacement(string RequestId, string EmployeeId, Period Time);
public sealed record RotaPlan(PackingResult Summary, IReadOnlyList<ShiftPlacement> Assignments);

public sealed class RotaSpace
{
    private readonly CalendarDefinition _definition;
    private readonly ICalendarStore _store;
    private readonly IPackingSolver _solver;

    public RotaSpace(CalendarDefinition definition, ICalendarStore store, IPackingSolver? solver = null)
    {
        if (definition.Resources.Values.Any(resource => resource.Capacity != 1))
            throw new ArgumentException("A rota employee must have capacity one.");
        _definition = definition;
        _store = store;
        _solver = solver ?? new GreedyPackingSolver();
    }

    public RotaPlan FindBest(DateTimeOffset from, DateTimeOffset to, TimeSpan size, ShiftRequirements restrictions) =>
        Plan([new ShiftRequest("search", new Period(from, to), size, restrictions)], TimeSpan.FromSeconds(2));

    public FitResult Fits(string employeeId, Period time, ShiftRequirements restrictions)
    {
        if (!_definition.Bounds.Contains(time)) return new(false, "OutsideWindow");
        if (!_definition.Resources.TryGetValue(employeeId, out var employee) ||
            !restrictions.Skills.IsSubsetOf(employee.Capabilities) ||
            restrictions.ExcludedEmployees?.Contains(employeeId) == true)
            return new(false, "EmployeeNotEligible");
        var first = _definition.Slot(time.From);
        var end = _definition.Slot(time.To);
        if (time.From < _store.Clock.GetUtcNow()) return new(false, "StartInPast");
        var snapshot = _store.Capture(time);
        if (snapshot.DefinitionVersion != _definition.Version) throw new InvalidOperationException("Definition mismatch.");
        for (var slot = first; slot < end; slot++)
        {
            var key = new ResourceSlot(employeeId, slot);
            if (snapshot.Used.GetValueOrDefault(key) + 1 > snapshot.Capacity.GetValueOrDefault(key))
                return new(false, "NoCapacity");
        }
        return new(true, null);
    }

    public RotaPlan Plan(IReadOnlyList<ShiftRequest> requests, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var input = requests.Select(request => request with
        {
            Requirements = request.Requirements with
            {
                Skills = request.Requirements.Skills.ToFrozenSet(StringComparer.Ordinal),
                ExcludedEmployees = request.Requirements.ExcludedEmployees?.ToFrozenSet(StringComparer.Ordinal)
            }
        }).ToArray();
        var snapshot = _store.Capture(_definition.Bounds);
        if (snapshot.DefinitionVersion != _definition.Version) throw new InvalidOperationException("Definition mismatch.");
        var choices = new List<Candidate>();
        var bindings = new Dictionary<string, ShiftPlacement>(StringComparer.Ordinal);
        var complete = true;
        foreach (var request in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_definition.Bounds.Contains(request.Window) || request.Duration <= TimeSpan.Zero ||
                request.Duration.Ticks % _definition.Grid.Ticks != 0)
                throw new ArgumentException("Invalid shift window or duration.");
            var first = _definition.Slot(request.Window.From);
            var last = _definition.Slot(request.Window.To) - request.Duration.Ticks / _definition.Grid.Ticks;
            foreach (var resource in _definition.Resources.Values.OrderBy(resource => resource.Id, StringComparer.Ordinal))
            {
                if (!request.Requirements.Skills.IsSubsetOf(resource.Capabilities) ||
                    request.Requirements.ExcludedEmployees?.Contains(resource.Id) == true) continue;
                for (var start = first; start <= last; start++)
                {
                    var time = new Period(_definition.At(start), _definition.At(start) + request.Duration);
                    if (time.From < _store.Clock.GetUtcNow()) continue;
                    if (choices.Count == _definition.CandidateLimit) { complete = false; break; }
                    var id = JsonSerializer.Serialize(new[] { request.Id, resource.Id, start.ToString(CultureInfo.InvariantCulture) });
                    var demands = Enumerable.Range(0, checked((int)(request.Duration.Ticks / _definition.Grid.Ticks)))
                        .Select(offset => new Consumption(Dimension(resource.Id, start + offset), 1)).ToArray();
                    choices.Add(new Candidate(id, request.Id, demands,
                        [(request.Requirements.PreferredEmployee == resource.Id ? last - first + 1 : 0) + last - start]));
                    bindings.Add(id, new ShiftPlacement(request.Id, resource.Id, time));
                }
                if (!complete) break;
            }
            if (!complete) break;
        }
        var capacities = choices.SelectMany(candidate => candidate.Demands).Select(demand => demand.Dimension).Distinct()
            .Select(dimension =>
            {
                var data = JsonSerializer.Deserialize<string[]>(dimension.Value)!;
                var key = new ResourceSlot(data[0], long.Parse(data[1], CultureInfo.InvariantCulture));
                return new Capacity(dimension, snapshot.Capacity.GetValueOrDefault(key) - snapshot.Used.GetValueOrDefault(key));
            });
        var problem = new PackingProblem(capacities, input.Select(request => new Request(request.Id, request.Required)),
            choices, [1], new PlanningSnapshot(_definition.Id, 0, snapshot.Version,
                _definition.Slot(_definition.Bounds.From), _definition.Slot(_definition.Bounds.To)), complete);
        var result = _solver.Solve(problem, budget, cancellationToken);
        return new(result, Array.AsReadOnly(result.Selected.Select(id => bindings[id]).ToArray()));
    }

    private static DimensionId Dimension(string resource, long slot) =>
        new(JsonSerializer.Serialize(new[] { resource, slot.ToString(CultureInfo.InvariantCulture) }));
}
