using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.Relevance;
using Mostlylucid.Packing.Sqlite;
using SalonExample;

namespace Mostlylucid.Packing.Tests;

public sealed class RelevanceBridgeTests
{
    [Fact]
    public async Task RankerSeesOnlyFeasibleChoicesAndRanksBeforeTopK()
    {
        var definition = SalonFixture.Create();
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        var calendar = Pack.Calendar(definition, store);
        var requirements = new BookingRequirements("consult", "alice",
            RequiredResources: new Dictionary<string, string> { ["technician"] = "sarah" });
        store.SetCapacity(Guid.NewGuid(), "sarah", new Period(SalonFixture.At(10), SalonFixture.At(11)), 0);
        var calls = 0;
        var ranker = new RelevanceRanker("later", (pool, _) =>
        {
            calls++;
            Assert.DoesNotContain(pool, match => match.Start < SalonFixture.At(11));
            return ValueTask.FromResult<IReadOnlyList<CandidateRelevance>>(pool.Select(match =>
                new CandidateRelevance(match.Id, match.Start.UtcTicks, [])).ToArray());
        });
        var result = await calendar.Query().During([new Period(SalonFixture.At(10), SalonFixture.At(12))])
            .OfSize(SalonFixture.Minutes(15)).Where(requirements).WithRelevance(ranker).TopKAsync(1);
        Assert.Equal(SalonFixture.At(11, 45), result.Best!.Match.Start);
        Assert.True(result.CandidatesComplete);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BridgeRejectsUnknownDuplicateMissingAndNonfiniteScores()
    {
        var definition = SalonFixture.Create();
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        var query = Pack.Calendar(definition, store).Query()
            .During([new Period(SalonFixture.At(10), SalonFixture.At(11))])
            .OfSize(SalonFixture.Minutes(45)).Where(SalonFixture.Gels());
        foreach (var mode in new[] { "unknown", "duplicate", "missing", "nan" })
        {
            var ranker = new RelevanceRanker(mode, (pool, _) =>
            {
                var scored = pool.Select(match => new CandidateRelevance(match.Id, 0, [])).ToList();
                if (mode == "unknown") scored[0] = scored[0] with { CandidateId = "not-in-pool" };
                if (mode == "duplicate") scored.Add(scored[0]);
                if (mode == "missing") scored.Clear();
                if (mode == "nan") scored[0] = scored[0] with { Score = double.NaN };
                return ValueTask.FromResult<IReadOnlyList<CandidateRelevance>>(scored);
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => query.WithRelevance(ranker).TopKAsync(1).AsTask());
        }
    }

    [Fact]
    public async Task ExplicitRankingExclusionsArePreserved()
    {
        var definition = SalonFixture.Create();
        using var store = new SqliteCalendarStore(":memory:", definition, new ManualClock(SalonFixture.Day));
        var ranker = new RelevanceRanker("policy", (pool, _) =>
            ValueTask.FromResult<IReadOnlyList<CandidateRelevance>>(pool.Select(match =>
                new CandidateRelevance(match.Id, null, [], "Additional recommendation policy")).ToArray()));
        var result = await Pack.Calendar(definition, store).Query()
            .OfSize(SalonFixture.Minutes(45)).Where(SalonFixture.Gels()).WithRelevance(ranker).TopKAsync(5);
        Assert.Empty(result.Matches);
        Assert.NotEmpty(result.Excluded);
    }
}
