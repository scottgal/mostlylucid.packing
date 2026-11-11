using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.OrTools;
using Mostlylucid.Packing.Planning;
using Mostlylucid.Packing.Spatial;
using Mostlylucid.Packing.Sqlite;
using SalonExample;

namespace Mostlylucid.Packing.Tests;

public sealed class FacadeTests
{
    [Fact]
    public void RotaAssignmentsRespectSkillsAvailabilityAndExistingReservations()
    {
        var bounds = new Period(SalonFixture.At(9), SalonFixture.At(17));
        CalendarResource[] staff =
        [
            new("sarah", 1, SalonFixture.Set("gels"), [bounds]),
            new("anna", 1, SalonFixture.Set("gels", "pedicure"), [bounds])
        ];
        var definition = new CalendarDefinition("rota", bounds, SalonFixture.Minutes(5), staff, [], []);
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        var rota = Pack.Rota(definition, store, new CpSatPackingSolver());
        var requirements = new ShiftRequirements(SalonFixture.Set("pedicure"));
        var result = rota.FindBest(bounds.From, bounds.To, TimeSpan.FromHours(4), requirements);
        Assert.Equal("anna", Assert.Single(result.Assignments).EmployeeId);
        Assert.Equal(bounds.From, result.Assignments[0].Time.From);
        Assert.False(rota.Fits("sarah", new Period(bounds.From, bounds.From.AddHours(4)), requirements).Fits);
        Assert.False(rota.Fits("anna", new Period(bounds.From, bounds.From.AddHours(4)),
            requirements with { ExcludedEmployees = SalonFixture.Set("anna") }).Fits);
        Assert.True(Pack.Rota(definition, store, new UnavailableSolver())
            .Fits("anna", new Period(bounds.From, bounds.From.AddHours(4)), requirements).Fits);
        var plan = rota.Plan(
            [new("first", new Period(bounds.From, bounds.From.AddHours(4)), TimeSpan.FromHours(4), requirements),
                new("second", new Period(bounds.From, bounds.From.AddHours(4)), TimeSpan.FromHours(4), requirements)],
            TimeSpan.FromSeconds(30));
        Assert.Equal(SolveStatus.Infeasible, plan.Summary.Status);
    }

    private sealed class UnavailableSolver : IPackingSolver
    {
        public PackingResult Solve(PackingProblem problem, TimeSpan budget, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An exact fit check must not invoke an optimiser.");
    }

    [Fact]
    public void SpatialFindBestChecksTheRotatedExtentAgainstTheRequestedRegion()
    {
        var warehouse = Pack.Spatial3D(new Box3D(4, 4, 4), [new(0, 0, 0)], new CpSatPackingSolver());
        var result = warehouse.FindBest(new(0, 0, 0), new(2, 3, 1), new(3, 1, 2));
        Assert.Equal(new Box3D(2, 3, 1), Assert.Single(result.Placements).Size);
        Assert.False(result.Summary.CandidatesComplete);
    }

    [Fact]
    public void LinkedRequestsForTheSameCustomerCannotOverlap()
    {
        var definition = SalonFixture.Create();
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        var calendar = Pack.Calendar(definition, store);
        var result = calendar.FindTogether(SalonFixture.At(10), SalonFixture.At(12),
            [SalonFixture.Gels(), SalonFixture.Pedi("alice")]);
        Assert.Null(result.Best);
        Assert.True(result.CandidatesComplete);
    }

    [Fact]
    public void ExhaustedCandidateBudgetReportsIncompleteInsteadOfNoAvailability()
    {
        var original = SalonFixture.Create();
        var definition = new CalendarDefinition(original.Id, original.Bounds, original.Grid,
            original.Resources.Values, original.Clients.Values, original.Services.Values, candidateLimit: 1);
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        store.SetCapacity(Guid.NewGuid(), "anna", new Period(SalonFixture.At(10), SalonFixture.At(11)), 0);
        var result = Pack.Calendar(definition, store).FindBest(SalonFixture.At(10), SalonFixture.At(11),
            SalonFixture.Minutes(15), new BookingRequirements("consult", "other"));
        Assert.Equal(SearchOutcome.Incomplete, result.Outcome);
        Assert.False(result.CandidatesComplete);
    }
}
