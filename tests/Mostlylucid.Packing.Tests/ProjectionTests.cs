using Microsoft.Data.Sqlite;
using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.Sqlite;
using SalonExample;

namespace Mostlylucid.Packing.Tests;

public sealed class ProjectionTests
{
    private static readonly int[] ExpectedRemainingUnits = [0, 1, 0, 1];
    [Fact]
    public void KeysetPagesReturnEveryBlockOnceAtOneRevision()
    {
        using var f = new Fixture();
        f.Hold();
        var resources = new[] { "sarah", "anna", "manicure", "pedicure" };
        var page = f.Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), resources, limit: 1);
        var collected = new List<CalendarBlock>();
        do
        {
            Assert.Equal(ProjectionStatus.Current, page.Status);
            collected.AddRange(page.Blocks);
            page = page.Next is { } next
                ? f.Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), resources, 1, next)
                : null;
        } while (page is not null);
        Assert.Equal(5, collected.Count);
        Assert.Equal(collected.Count, collected.Select(b => b.Id).Distinct().Count());
        Assert.Equal(collected.OrderBy(b => b.Time.From).ThenBy(b => b.Id), collected);
    }

    [Fact]
    public void UnrelatedWritesDoNotChangeTheViewportRevisionAndTransitionsProduceUpsertsAndTombstones()
    {
        using var f = new Fixture();
        var view = f.View();
        f.Hold(at: 14);
        var unchanged = f.Space.GetBlockChanges(view.View);
        Assert.Equal(view.View.Revision, unchanged.View.Revision);
        Assert.True(unchanged.View.Cursor > view.View.Cursor);
        Assert.Empty(unchanged.Upserts);
        var held = f.Hold();
        var created = f.Space.GetBlockChanges(unchanged.View);
        Assert.Equal(2, created.Upserts.Count);
        Assert.All(created.Upserts, block => Assert.Equal(ReservationState.Held, block.State));
        Assert.True(f.Space.Confirm(Guid.NewGuid(), "alice", held.ReservationId!.Value).Success);
        var confirmed = f.Space.GetBlockChanges(created.View);
        Assert.Equal(created.Upserts.Select(b => b.Id), confirmed.Upserts.Select(b => b.Id));
        Assert.All(confirmed.Upserts, block => Assert.Equal(ReservationState.Confirmed, block.State));
        f.Space.Cancel(Guid.NewGuid(), "alice", held.ReservationId.Value);
        var cancelled = f.Space.GetBlockChanges(confirmed.View);
        Assert.Equal(confirmed.Upserts.Select(b => b.Id).Order(), cancelled.Removed);
        Assert.Empty(cancelled.Upserts);
        Assert.Empty(f.Space.GetBlockChanges(cancelled.View).Removed);
    }

    [Fact]
    public void ExpiryInvalidatesViewsDuringQuietPeriods()
    {
        using var f = new Fixture();
        f.Hold();
        var view = f.View();
        var ids = view.Blocks.Select(b => b.Id).Order().ToArray();
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var delta = f.Space.GetBlockChanges(view.View);
        Assert.Equal(ids, delta.Removed);
        Assert.True(delta.View.Revision > view.View.Revision);
        Assert.Empty(f.View().Blocks);
    }

    [Fact]
    public void ARelevantWriteRequiresRestartingPaginationButAnUnrelatedWriteDoesNot()
    {
        using var f = new Fixture();
        var held = f.Hold();
        var view = f.Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], 1);
        Assert.NotNull(view.Next);
        f.Hold(at: 14);
        Assert.Equal(ProjectionStatus.Current,
            f.Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], 1, view.Next).Status);
        f.Space.Confirm(Guid.NewGuid(), "alice", held.ReservationId!.Value);
        Assert.Equal(ProjectionStatus.ResnapshotRequired,
            f.Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], 1, view.Next).Status);
    }

    [Fact]
    public void CapacityEditsInvalidateTheirRangeAndCoarseBlocksRetainMixedAvailability()
    {
        using var f = new Fixture();
        var view = f.View();
        f.Store.SetCapacity(Guid.NewGuid(), "sarah", new Period(SalonFixture.At(10), SalonFixture.At(10, 15)), 0);
        var delta = f.Space.GetBlockChanges(view.View);
        Assert.Empty(delta.Upserts);
        Assert.Empty(delta.Removed);
        Assert.Single(delta.Invalidated);
        var coarse = f.Space.GetCapacityBlocks(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], TimeSpan.FromMinutes(30));
        Assert.Contains(coarse.Blocks, block => block.RemainingMin == 0 && block.RemainingMax == 1);
        Assert.DoesNotContain(coarse.Blocks, block => block.ResourceId != "sarah");
    }

    [Fact]
    public void ExactCapacityRunsCoalesceAndRespectBothStagesAndViewportClipping()
    {
        using var f = new Fixture();
        f.Hold();
        var view = f.Space.GetCapacityBlocks(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], TimeSpan.FromMinutes(5));
        Assert.Equal(4, view.Blocks.Count);
        Assert.Equal(ExpectedRemainingUnits, view.Blocks.Select(block => block.RemainingMin));
        var clipped = f.Space.GetCapacityBlocks(SalonFixture.At(10, 2), SalonFixture.At(10, 58), ["sarah"], TimeSpan.FromMinutes(5));
        Assert.Equal(SalonFixture.At(10, 2), clipped.Blocks[0].Time.From);
        Assert.Equal(SalonFixture.At(10, 58), clipped.Blocks[^1].Time.To);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Space.GetCapacityBlocks(SalonFixture.At(10),
            SalonFixture.At(11), ["sarah"], TimeSpan.FromMinutes(5), maxCells: 2));
    }

    [Fact]
    public void PrunedHistoryAndDeltaOverflowRequestABoundedResnapshot()
    {
        using var f = new Fixture();
        var before = f.View();
        f.Hold();
        Assert.Equal(ProjectionStatus.ResnapshotRequired, f.Space.GetBlockChanges(before.View, limit: 1).Status);
        f.Store.PruneProjectionHistory(1);
        Assert.Equal(ProjectionStatus.ResnapshotRequired, f.Space.GetBlockChanges(before.View).Status);
        var current = f.View();
        Assert.Equal(ProjectionStatus.Current, f.Space.GetBlockChanges(current.View).Status);
    }

    [Fact]
    public void TokensCannotCrossStoresAndQueries()
    {
        using var a = new Fixture();
        using var b = new Fixture();
        var token = a.View().View;
        Assert.Throws<ArgumentException>(() => b.Space.GetBlockChanges(token));
        Assert.Throws<ArgumentException>(() => a.Space.GetBlockChanges(token with
        {
            Window = new Period(SalonFixture.At(13), SalonFixture.At(14))
        }));
        Assert.Equal(ProjectionStatus.ResnapshotRequired,
            a.Space.GetBlockChanges(token with { Cursor = token.Cursor + 100 }).Status);
    }

    [Fact]
    public void ApplyingDeltaBatchesMatchesFreshSnapshotsAcrossAStreamOfChanges()
    {
        using var f = new Fixture();
        var start = SalonFixture.At(14);
        var end = SalonFixture.At(15);
        var view = f.Space.GetBlockPage(start, end, ["sarah"]);
        var client = view.Blocks.ToDictionary(block => block.Id);
        Guid held = default;
        for (var step = 0; step < 36; step++)
        {
            switch (step % 6)
            {
                case 0:
                case 4:
                    held = f.Hold(at: 14).ReservationId!.Value;
                    break;
                case 1:
                    f.Space.Confirm(Guid.NewGuid(), "alice", held);
                    break;
                case 2:
                    f.Store.SetCapacity(Guid.NewGuid(), "sarah",
                        new Period(SalonFixture.At(10), SalonFixture.At(11)), step % 2);
                    break;
                case 3:
                    f.Space.Cancel(Guid.NewGuid(), "alice", held);
                    break;
                case 5:
                    f.Clock.Advance(TimeSpan.FromMinutes(5));
                    break;
            }
            var delta = f.Space.GetBlockChanges(view.View);
            Assert.Equal(ProjectionStatus.Current, delta.Status);
            foreach (var id in delta.Removed) client.Remove(id);
            foreach (var block in delta.Upserts) client[block.Id] = block;
            view = f.Space.GetBlockPage(start, end, ["sarah"]);
            Assert.Equal(view.Blocks.OrderBy(block => block.Id), client.Values.OrderBy(block => block.Id));
            Assert.Equal(view.View.Revision, delta.View.Revision);
        }
    }

    [Fact]
    public void ExistingDatabasesAcquireProjectionMetadataWithoutLosingAllocations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packing-projection-migration-{Guid.NewGuid():N}.db");
        var definition = SalonFixture.Create();
        var clock = new ManualClock(SalonFixture.Day);
        try
        {
            using (var store = new SqliteCalendarStore(path, definition, clock))
            {
                var space = Pack.Calendar(definition, store);
                var match = space.FindBest(SalonFixture.At(10), SalonFixture.At(11),
                    SalonFixture.Minutes(45), SalonFixture.Gels()).Best!;
                Assert.True(space.TryHold(Guid.NewGuid(), "alice", match, TimeSpan.FromMinutes(5)).Success);
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE change_ranges; DROP TABLE projection_state;";
                command.ExecuteNonQuery();
            }
            using var reopened = new SqliteCalendarStore(path, definition, clock);
            var page = reopened.GetBlockPage(new Period(SalonFixture.At(10), SalonFixture.At(11)), ["sarah"]);
            Assert.Equal(2, page.Blocks.Count);
            Assert.Equal(1, page.View.Cursor);
            Assert.Equal(ProjectionStatus.Current, reopened.GetBlockChanges(page.View).Status);
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
            Space = Pack.Calendar(definition, Store);
        }
        public BlockViewPage View() => Space.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), ["sarah"]);
        public ReservationReply Hold(int at = 10)
        {
            var match = Space.FindTogether(SalonFixture.At(at), SalonFixture.At(at + 1),
                [SalonFixture.Gels(), SalonFixture.Pedi()]).Best!;
            var reply = Space.TryHold(Guid.NewGuid(), "alice", match, TimeSpan.FromMinutes(5));
            Assert.True(reply.Success);
            return reply;
        }
        public void Dispose() => Store.Dispose();
    }
}
