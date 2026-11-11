using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.Planning;
using Mostlylucid.Packing.Spatial;

namespace Mostlylucid.Packing;

public static class Pack
{
    public static CalendarSpace Calendar(CalendarDefinition definition, ICalendarStore store, IPackingSolver? solver = null) =>
        new(definition, store, solver);

    public static RotaSpace Rota(CalendarDefinition definition, ICalendarStore store, IPackingSolver? solver = null) =>
        new(definition, store, solver);

    public static SpatialSpace3D Spatial3D(Box3D container, IReadOnlyList<Point3D> anchors, IPackingSolver? solver = null) =>
        new(container, anchors, solver);
}

public sealed class SpatialSpace3D(Box3D container, IReadOnlyList<Point3D> anchors, IPackingSolver? solver = null)
{
    private readonly IPackingSolver _solver = solver ?? new GreedyPackingSolver();
    private readonly Point3D[] _anchors = anchors.ToArray();

    public bool Fits(Point3D at, Box3D size, IReadOnlyList<PlacedBox>? existing = null) =>
        BoxGeometry.CanPlace(container, new PlacedBox(size, at), existing ?? []);

    public SpatialPlan Plan(IReadOnlyList<BoxItem> items, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var input = BoxPacking.Generate(container, items, _anchors);
        var result = _solver.Solve(input.Problem, budget, cancellationToken);
        BoxPacking.Verify(container, input, result);
        return new SpatialPlan(result, Array.AsReadOnly(result.Selected.Select(id => input.Placements[id]).ToArray()));
    }

    public SpatialPlan FindBest(Point3D from, Point3D to, Box3D size)
    {
        var all = BoxPacking.Generate(container, [new BoxItem("search", size)], _anchors);
        var allowed = all.Placements.Where(pair =>
        {
            var box = pair.Value;
            return box.Position.X >= from.X && box.Position.Y >= from.Y && box.Position.Z >= from.Z &&
                   (long)box.Position.X + box.Size.Width <= to.X &&
                   (long)box.Position.Y + box.Size.Depth <= to.Y &&
                   (long)box.Position.Z + box.Size.Height <= to.Z;
        }).ToDictionary();
        var problem = new PackingProblem([], all.Problem.Requests.Values,
            all.Problem.Candidates.Values.Where(candidate => allowed.ContainsKey(candidate.Id)), candidatesComplete: false);
        var result = _solver.Solve(problem, TimeSpan.FromSeconds(2));
        return new(result, Array.AsReadOnly(result.Selected.Select(id => allowed[id]).ToArray()));
    }
}

public sealed record SpatialPlan(PackingResult Summary, IReadOnlyList<PlacedBox> Placements);
