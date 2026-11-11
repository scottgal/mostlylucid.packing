using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mostlylucid.Packing.Calendar;

namespace Mostlylucid.Packing.Sqlite;

public sealed partial class SqliteCalendarStore : IBlockProjectionStore
{
    private sealed record ViewContext(BlockViewToken Token, long Floor);

    private static void InitializeProjections(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS projection_state(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                store_id TEXT NOT NULL, floor_version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS change_ranges(
                version INTEGER NOT NULL REFERENCES changes(version),
                resource TEXT NOT NULL, start INTEGER NOT NULL, finish INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ranges_resource_version ON change_ranges(resource,version,start,finish);
            CREATE INDEX IF NOT EXISTS ranges_version ON change_ranges(version);
            CREATE INDEX IF NOT EXISTS capacity_slot_resource ON capacity(slot,resource,units);
            """);
        // Existing databases cannot reconstruct historic capacity edits. Older tokens need a new snapshot.
        Execute(connection, transaction, """
            INSERT INTO projection_state VALUES(1,$id,$version) ON CONFLICT(singleton) DO NOTHING;
            """, ("$id", Guid.NewGuid().ToString("N")), ("$version", Version(connection, transaction)));
    }

    private static void RecordRanges(SqliteConnection connection, SqliteTransaction transaction, long version,
        Guid? reservation, (string Resource, Period Time)? capacity)
    {
        if (reservation is { } id)
            Execute(connection, transaction, """
                INSERT INTO change_ranges
                SELECT $version,resource,start,finish FROM demands WHERE reservation=$id;
                """, ("$version", version), ("$id", id.ToString("N")));
        if (capacity is { } range)
            Execute(connection, transaction, "INSERT INTO change_ranges VALUES($version,$resource,$start,$finish);",
                ("$version", version), ("$resource", range.Resource),
                ("$start", range.Time.From.UtcTicks), ("$finish", range.Time.To.UtcTicks));
    }

    public BlockViewPage GetBlockPage(Period window, IReadOnlyList<string> resources, int limit = 1000,
        BlockPageCursor? after = null)
    {
        ValidateLimit(limit);
        var ids = ValidateResources(window, resources);
        using var connection = Open();
        Expire(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var context = View(connection, transaction, window, ids);
        if (after is not null)
        {
            ValidateToken(after.View, context.Token);
            if (NeedsResnapshot(after.View, context) || context.Token.Revision > after.View.Cursor)
                return new(ProjectionStatus.ResnapshotRequired, context.Token, [], null);
        }
        if (ids.Length == 0) return new(ProjectionStatus.Current, context.Token, [], null);
        using var query = Command(connection, transaction, $"""
            SELECT d.id,d.reservation,d.resource,d.start,d.finish,r.state,r.expires
            FROM demands d JOIN reservations r ON r.id=d.reservation
            WHERE r.state IN (0,1) AND d.start<$to AND d.finish>$from
              AND d.resource IN ({Names("r", ids.Length)})
              AND ($first=1 OR d.start>$last OR (d.start=$last AND d.id>$id))
            ORDER BY d.start,d.id LIMIT $limit;
            """, ("$from", window.From.UtcTicks), ("$to", window.To.UtcTicks), ("$limit", limit + 1),
            ("$first", after is null ? 1 : 0), ("$last", after?.StartTicks ?? 0), ("$id", after?.BlockId ?? 0));
        Bind(query, "r", ids);
        var blocks = new List<CalendarBlock>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) blocks.Add(ReadBlock(reader));
        transaction.Commit();
        var page = Array.AsReadOnly(blocks.Take(limit).ToArray());
        var cursor = blocks.Count > limit
            ? new BlockPageCursor(context.Token, page[^1].Time.From.UtcTicks, page[^1].Id)
            : null;
        return new(ProjectionStatus.Current, context.Token, page, cursor);
    }

    public BlockDelta GetBlockChanges(BlockViewToken since, int limit = 1000)
    {
        ValidateLimit(limit);
        var ids = ValidateResources(since.Window, since.Resources);
        using var connection = Open();
        Expire(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var context = View(connection, transaction, since.Window, ids);
        ValidateToken(since, context.Token);
        if (NeedsResnapshot(since, context))
            return new(ProjectionStatus.ResnapshotRequired, context.Token, [], [], []);
        if (ids.Length == 0 || context.Token.Revision <= since.Cursor)
            return new(ProjectionStatus.Current, context.Token, [], [], []);

        using var changes = Command(connection, transaction, $"""
            SELECT x.resource,x.start,x.finish,c.reservation
            FROM change_ranges x JOIN changes c ON c.version=x.version
            WHERE x.version>$cursor AND x.start<$to AND x.finish>$from
              AND x.resource IN ({Names("r", ids.Length)})
            ORDER BY x.version LIMIT $limit;
            """, ("$cursor", since.Cursor), ("$from", since.Window.From.UtcTicks),
            ("$to", since.Window.To.UtcTicks), ("$limit", limit + 1));
        Bind(changes, "r", ids);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new HashSet<ChangedRange>();
        var count = 0;
        using (var reader = changes.ExecuteReader())
            while (reader.Read())
            {
                count++;
                if (!reader.IsDBNull(3)) affected.Add(reader.GetString(3));
                ranges.Add(new(reader.GetString(0), new Period(AtTicks(reader.GetInt64(1)), AtTicks(reader.GetInt64(2)))));
            }
        if (count > limit) return new(ProjectionStatus.ResnapshotRequired, context.Token, [], [], []);
        var upserts = new List<CalendarBlock>();
        var removed = new List<long>();
        foreach (var chunk in affected.Chunk(256))
        {
            using var query = Command(connection, transaction, $"""
                SELECT d.id,d.reservation,d.resource,d.start,d.finish,r.state,r.expires
                FROM demands d JOIN reservations r ON r.id=d.reservation
                WHERE d.reservation IN ({Names("a", chunk.Length)})
                  AND d.resource IN ({Names("r", ids.Length)})
                  AND d.start<$to AND d.finish>$from
                ORDER BY d.start,d.id LIMIT $limit;
                """, ("$from", since.Window.From.UtcTicks), ("$to", since.Window.To.UtcTicks),
                ("$limit", limit - upserts.Count - removed.Count + 1));
            Bind(query, "a", chunk);
            Bind(query, "r", ids);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var block = ReadBlock(reader);
                if (block.State is ReservationState.Held or ReservationState.Confirmed) upserts.Add(block);
                else removed.Add(block.Id);
            }
            if (upserts.Count + removed.Count > limit)
                return new(ProjectionStatus.ResnapshotRequired, context.Token, [], [], []);
        }
        transaction.Commit();
        return new(ProjectionStatus.Current, context.Token,
            Array.AsReadOnly(upserts.OrderBy(block => block.Time.From).ThenBy(block => block.Id).ToArray()),
            Array.AsReadOnly(removed.Order().ToArray()),
            Array.AsReadOnly(ranges.OrderBy(range => range.ResourceId, StringComparer.Ordinal)
                .ThenBy(range => range.Time.From).ToArray()));
    }

    public CapacityView GetCapacityBlocks(Period window, IReadOnlyList<string> resources, TimeSpan resolution,
        int maxCells = 1_000_000)
    {
        var ids = ValidateResources(window, resources);
        if (resolution <= TimeSpan.Zero || resolution.Ticks % _definition.Grid.Ticks != 0)
            throw new ArgumentException("Resolution must be a positive multiple of the grid.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCells);
        var first = Floor(window.From);
        var end = Ceiling(window.To);
        if (checked((end - first) * ids.Length) > maxCells)
            throw new ArgumentOutOfRangeException(nameof(maxCells), "Narrow the viewport or raise the explicit cell budget.");
        using var connection = Open();
        Expire(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var context = View(connection, transaction, window, ids);
        var snapshot = ids.Length == 0 ? null : Capture(connection, transaction, window, ids);
        var result = new List<CapacityBlock>();
        var step = resolution.Ticks / _definition.Grid.Ticks;
        foreach (var resource in ids)
        for (var start = first; start < end;)
        {
            var stop = start + Math.Min(step, end - start);
            var minimum = int.MaxValue;
            var maximum = int.MinValue;
            var capacityMin = int.MaxValue;
            var capacityMax = int.MinValue;
            for (var slot = start; slot < stop; slot++)
            {
                var key = new ResourceSlot(resource, slot);
                var capacity = snapshot!.Capacity.GetValueOrDefault(key);
                var remaining = checked((int)(capacity - snapshot.Used.GetValueOrDefault(key)));
                if (remaining < 0) throw new InvalidOperationException("Capacity projection invariant failed.");
                minimum = Math.Min(minimum, remaining);
                maximum = Math.Max(maximum, remaining);
                capacityMin = Math.Min(capacityMin, capacity);
                capacityMax = Math.Max(capacityMax, capacity);
            }
            var from = _definition.At(start) < window.From ? window.From : _definition.At(start);
            var to = _definition.At(stop) > window.To ? window.To : _definition.At(stop);
            var block = new CapacityBlock(resource, new Period(from, to), capacityMin, capacityMax, minimum, maximum);
            if (result.Count > 0 && result[^1] is var prior && prior.ResourceId == resource && prior.Time.To == from &&
                prior.CapacityMin == capacityMin && prior.CapacityMax == capacityMax &&
                prior.RemainingMin == minimum && prior.RemainingMax == maximum)
                result[^1] = prior with { Time = new Period(prior.Time.From, to) };
            else result.Add(block);
            start = stop;
        }
        transaction.Commit();
        return new(context.Token, resolution, Clock.GetUtcNow(), result.AsReadOnly());
    }

    public void PruneProjectionHistory(long retainFromVersion)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = Version(connection, transaction);
        if (retainFromVersion < 0 || retainFromVersion > current) throw new ArgumentOutOfRangeException(nameof(retainFromVersion));
        Execute(connection, transaction, "DELETE FROM change_ranges WHERE version<=$version;", ("$version", retainFromVersion));
        Execute(connection, transaction, "DELETE FROM changes WHERE version<=$version;", ("$version", retainFromVersion));
        Execute(connection, transaction, "UPDATE projection_state SET floor_version=MAX(floor_version,$version);",
            ("$version", retainFromVersion));
        transaction.Commit();
    }

    private ViewContext View(SqliteConnection connection, SqliteTransaction transaction, Period window, string[] resources)
    {
        string storeId;
        long floor;
        using (var read = Command(connection, transaction, "SELECT store_id,floor_version FROM projection_state WHERE singleton=1;"))
        using (var reader = read.ExecuteReader())
        {
            reader.Read();
            storeId = reader.GetString(0);
            floor = reader.GetInt64(1);
        }
        var version = Version(connection, transaction);
        long revision = 0;
        if (resources.Length > 0)
        {
            using var query = Command(connection, transaction, $"""
                SELECT MAX(version) FROM change_ranges
                WHERE resource IN ({Names("r", resources.Length)}) AND start<$to AND finish>$from;
                """, ("$from", window.From.UtcTicks), ("$to", window.To.UtcTicks));
            Bind(query, "r", resources);
            if (query.ExecuteScalar() is long latest) revision = latest;
        }
        // After pruning, the history floor also identifies the projection generation.
        revision = Math.Max(revision, floor);
        var data = JsonSerializer.Serialize(new { From = window.From.UtcTicks, To = window.To.UtcTicks, resources });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        return new(new BlockViewToken(storeId, _definition.Version, key, window,
            Array.AsReadOnly(resources), version, revision), floor);
    }

    private static bool NeedsResnapshot(BlockViewToken token, ViewContext current) =>
        token.Cursor < current.Floor || token.Cursor > current.Token.Cursor;

    private static void ValidateToken(BlockViewToken token, BlockViewToken current)
    {
        if (token.StoreId != current.StoreId || token.DefinitionVersion != current.DefinitionVersion ||
            token.QueryKey != current.QueryKey || token.Cursor < 0 || token.Revision < 0 ||
            token.Revision > token.Cursor)
            throw new ArgumentException("This projection token belongs to a different store, definition or query.");
    }

    private string[] ValidateResources(Period window, IReadOnlyList<string> resources)
    {
        ValidateWindow(window);
        if (resources.Count > 256) throw new ArgumentOutOfRangeException(nameof(resources));
        var known = _definition.Resources.Keys.Concat(_definition.Clients.Keys.Select(CalendarDefinition.ClientResource))
            .ToHashSet(StringComparer.Ordinal);
        if (resources.Any(resource => !known.Contains(resource))) throw new ArgumentException("Unknown resource.");
        return resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static string Names(string prefix, int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(index => "$" + prefix + index));

    private static void Bind(SqliteCommand command, string prefix, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++) command.Parameters.AddWithValue("$" + prefix + index, values[index]);
    }

    private static CalendarBlock ReadBlock(SqliteDataReader reader) =>
        new(reader.GetInt64(0), Guid.ParseExact(reader.GetString(1), "N"), reader.GetString(2),
            new Period(AtTicks(reader.GetInt64(3)), AtTicks(reader.GetInt64(4))),
            (ReservationState)reader.GetInt32(5), reader.GetInt32(5) == 0 ? AtTicks(reader.GetInt64(6)) : null);
}
