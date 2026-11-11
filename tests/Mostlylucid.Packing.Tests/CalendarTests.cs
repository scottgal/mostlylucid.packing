using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.OrTools;
using Mostlylucid.Packing.Planning;
using Mostlylucid.Packing.Sqlite;
using SalonExample;

namespace Mostlylucid.Packing.Tests;

public sealed class CalendarTests
{
    private static BookingRequirements Consult(string client = "other") => new("consult", client,
        RequiredResources: new Dictionary<string, string> { ["technician"] = "sarah" });

    [Fact]
    public void StagesReleaseTheTechnicianDuringDryingAndRetainContinuity()
    {
        using var fixture = new Fixture();
        var calendar = fixture.Space;
        var match = calendar.FindBest(SalonFixture.At(10), SalonFixture.At(10, 55),
            SalonFixture.Minutes(45), SalonFixture.Gels()).Best!;
        Assert.NotNull(match);
        Assert.Equal("sarah", Assert.Single(match.Appointments).Resources["technician"]);
        Assert.True(calendar.TryHold(Guid.NewGuid(), "alice", match, SalonFixture.Minutes(5)).Success);
        Assert.True(calendar.Fits(SalonFixture.At(10, 15), SalonFixture.Minutes(15), Consult()).Fits);
        Assert.False(calendar.Fits(SalonFixture.At(10, 10), SalonFixture.Minutes(15), Consult()).Fits);
        Assert.False(calendar.Fits(SalonFixture.At(10, 35), SalonFixture.Minutes(15), Consult()).Fits);
        Assert.True(calendar.Fits(SalonFixture.At(10, 50), SalonFixture.Minutes(15), Consult()).Fits);
    }

    [Fact]
    public void SearchIncludesResetTimeAndCannotShortenTheService()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(10, 45),
            SalonFixture.Minutes(45), SalonFixture.Gels()).Best);
        Assert.Throws<ArgumentException>(() => fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(12),
            SalonFixture.Minutes(15), SalonFixture.Gels()));
        Assert.Throws<ArgumentException>(() => fixture.Space.Query().OfSize(SalonFixture.Minutes(45))
            .Where(SalonFixture.Gels()).OfSize(SalonFixture.Minutes(15)));
    }

    [Fact]
    public void FriendsHaveOneStartAndOneAtomicHold()
    {
        using var fixture = new Fixture();
        var match = fixture.Space.FindTogether(SalonFixture.At(10), SalonFixture.At(10, 55),
            [SalonFixture.Gels(), SalonFixture.Pedi()]).Best!;
        Assert.Equal(2, match.Appointments.Count);
        Assert.Single(match.Appointments.Select(a => a.Start).Distinct());
        Assert.True(fixture.Space.TryHold(Guid.NewGuid(), "alice", match, SalonFixture.Minutes(5)).Success);
        Assert.False(fixture.Space.Fits(SalonFixture.At(10), SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);
        Assert.False(fixture.Space.Fits(SalonFixture.At(10), SalonFixture.Minutes(30), SalonFixture.Pedi()).Fits);
    }

    [Fact]
    public void AStaleGroupCannotPartiallyOccupyResources()
    {
        using var fixture = new Fixture();
        var group = fixture.Space.FindTogether(SalonFixture.At(10), SalonFixture.At(10, 55),
            [SalonFixture.Gels(), SalonFixture.Pedi()]).Best!;
        var pedicure = fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(10, 35),
            SalonFixture.Minutes(30), SalonFixture.Pedi("other")).Best!;
        Assert.True(fixture.Space.TryHold(Guid.NewGuid(), "other", pedicure, SalonFixture.Minutes(5)).Success);
        Assert.Equal("NoCapacity", fixture.Space.TryHold(Guid.NewGuid(), "alice", group, SalonFixture.Minutes(5)).Reason);
        Assert.True(fixture.Space.Fits(SalonFixture.At(10), SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);
        Assert.Empty(fixture.Space.GetBlocks(SalonFixture.At(10), SalonFixture.At(11), ["sarah"]).Blocks);
    }

    [Fact]
    public void ExpiryEqualityRetriesAndConfirmationPreserveCapacityAccounting()
    {
        using var fixture = new Fixture();
        var match = fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(11),
            SalonFixture.Minutes(45), SalonFixture.Gels()).Best!;
        var id = Guid.NewGuid();
        var held = fixture.Space.TryHold(id, "alice", match, SalonFixture.Minutes(5));
        fixture.Clock.Advance(SalonFixture.Minutes(5));
        Assert.Equal(held, fixture.Space.TryHold(id, "alice", match, SalonFixture.Minutes(5)));
        Assert.Equal("NotActive", fixture.Space.Confirm(Guid.NewGuid(), "alice", id).Reason);
        Assert.True(fixture.Space.Fits(match.Start, SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);

        var fresh = fixture.Space.TryHold(Guid.NewGuid(), "alice", match, SalonFixture.Minutes(5));
        Assert.True(fixture.Space.Confirm(Guid.NewGuid(), "alice", fresh.ReservationId!.Value).Success);
        fixture.Clock.Advance(SalonFixture.Minutes(10));
        Assert.False(fixture.Space.Fits(match.Start, SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);
        Assert.True(fixture.Space.Cancel(Guid.NewGuid(), "alice", fresh.ReservationId.Value).Success);
        Assert.True(fixture.Space.Fits(match.Start, SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);
    }

    [Fact]
    public void PeriodUnionsNavigationAndNearestUseStableTypedResults()
    {
        using var fixture = new Fixture();
        var query = fixture.Space.Query().During(
                [new Period(SalonFixture.At(10), SalonFixture.At(11)), new Period(SalonFixture.At(10, 30), SalonFixture.At(11)),
                    new Period(SalonFixture.At(14), SalonFixture.At(15))])
            .OfSize(SalonFixture.Minutes(15)).Where(Consult()).OrderBy(Criteria.Start);
        var first = query.FindBest();
        var next = query.Next(first.Cursor!);
        Assert.Equal(SalonFixture.At(10, 5), next.Best!.Start);
        Assert.Equal(first.Best!.Id, query.Previous(next.Cursor!).Best!.Id);
        Assert.Equal(SalonFixture.At(14, 20), query.Nearest(SalonFixture.At(14, 20)).Best!.Start);
        var all = query.TopK(100);
        Assert.Equal(all.Matches.Count, all.Matches.Select(m => m.Id).Distinct().Count());
        Assert.DoesNotContain(all.Matches, m => m.Start >= SalonFixture.At(11) && m.Start < SalonFixture.At(14));
        fixture.Space.TryHold(Guid.NewGuid(), "other", first.Best!, SalonFixture.Minutes(1));
        Assert.Throws<InvalidOperationException>(() => query.Next(first.Cursor!));
    }

    [Fact]
    public void BlockedAndExcludedClientsRemainIneligible()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(11),
            SalonFixture.Minutes(45), SalonFixture.Gels("blocked")).Best);
        Assert.Null(fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(11),
            SalonFixture.Minutes(45), SalonFixture.Gels("excluded")).Best);
    }

    [Fact]
    public void BatchPlanCanSaveTheOnlyPedicureTechnicianForTheConstrainedRequest()
    {
        using var fixture = new Fixture();
        var flexible = new BookingRequirements("gels", "alice",
            PreferredResources: new Dictionary<string, string> { ["technician"] = "anna" });
        var plan = fixture.Space.Plan(SalonFixture.At(10), SalonFixture.At(10, 55),
            [new("flexible", flexible), new("pedicure", SalonFixture.Pedi())], TimeSpan.FromSeconds(30));
        Assert.Equal(SolveStatus.Optimal, plan.Summary.Status);
        Assert.Equal(2, plan.Matches.Count);
        Assert.Equal("sarah", plan.Matches.Single(m => m.Appointments[0].RequestId == "flexible").Appointments[0].Resources["technician"]);
    }

    [Fact]
    public void CapacityEditsPreserveExistingCommitmentsAndThenReflectAbsence()
    {
        using var fixture = new Fixture();
        var match = fixture.Space.FindBest(SalonFixture.At(10), SalonFixture.At(11),
            SalonFixture.Minutes(45), SalonFixture.Gels()).Best!;
        var id = Guid.NewGuid();
        fixture.Space.TryHold(id, "alice", match, SalonFixture.Minutes(5));
        var period = new Period(SalonFixture.At(10), SalonFixture.At(11));
        Assert.Equal("CapacityInUse", fixture.Store.SetCapacity(Guid.NewGuid(), "sarah", period, 0).Reason);
        fixture.Space.Cancel(Guid.NewGuid(), "alice", id);
        Assert.True(fixture.Store.SetCapacity(Guid.NewGuid(), "sarah", period, 0).Success);
        Assert.Null(fixture.Space.FindBest(period.From, period.To, SalonFixture.Minutes(45), SalonFixture.Gels()).Best);
    }

    [Fact]
    public async Task SeparateConnectionsAllowOnlyOneCompetingGroupAndPersistOnReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packing-test-{Guid.NewGuid():N}.db");
        var definition = SalonFixture.Create();
        var clock = new ManualClock(SalonFixture.Day);
        try
        {
            CalendarMatch match;
            Guid id = Guid.NewGuid();
            ReservationReply held;
            using (var first = new SqliteCalendarStore(path, definition, clock))
            using (var second = new SqliteCalendarStore(path, definition, clock))
            {
                var a = Pack.Calendar(definition, first);
                var b = Pack.Calendar(definition, second);
                match = a.FindTogether(SalonFixture.At(10), SalonFixture.At(11),
                    [SalonFixture.Gels(), SalonFixture.Pedi()]).Best!;
                var replies = await Task.WhenAll(
                    Task.Run(() => a.TryHold(id, "alice", match, SalonFixture.Minutes(5))),
                    Task.Run(() => b.TryHold(Guid.NewGuid(), "rival", match, SalonFixture.Minutes(5))));
                held = Assert.Single(replies, r => r.Success);
                Assert.Contains(replies, r => r.Reason == "NoCapacity");
                Assert.True(a.Confirm(Guid.NewGuid(), held.ReservationId == id ? "alice" : "rival", held.ReservationId!.Value).Success);
            }
            using var reopened = new SqliteCalendarStore(path, definition, clock);
            Assert.NotEmpty(reopened.GetBlocks(new Period(SalonFixture.At(10), SalonFixture.At(11)), ["sarah"]).Blocks);
            Assert.False(Pack.Calendar(definition, reopened).Fits(SalonFixture.At(10),
                SalonFixture.Minutes(45), SalonFixture.Gels()).Fits);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ManualClock Clock { get; } = new(SalonFixture.Day);
        public SqliteCalendarStore Store { get; }
        public CalendarSpace Space { get; }
        public Fixture()
        {
            var definition = SalonFixture.Create();
            Store = new SqliteCalendarStore(":memory:", definition, Clock);
            Space = Pack.Calendar(definition, Store, new CpSatPackingSolver());
        }
        public void Dispose() => Store.Dispose();
    }
}
