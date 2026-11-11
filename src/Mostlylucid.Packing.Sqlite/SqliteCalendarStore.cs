using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mostlylucid.Packing.Calendar;

namespace Mostlylucid.Packing.Sqlite;

/// <summary>One authorised calendar per SQLite file. Each write obtains an immediate transaction.</summary>
public sealed class SqliteCalendarStore : ICalendarStore, IDisposable
{
    private readonly string _connectionString;
    private readonly CalendarDefinition _definition;
    private readonly SqliteConnection _keeper;
    public TimeProvider Clock { get; }

    public SqliteCalendarStore(string path, CalendarDefinition definition, TimeProvider? clock = null)
    {
        _definition = definition;
        Clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path == ":memory:" ? $"packing-{Guid.NewGuid():N}" : Path.GetFullPath(path),
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = path == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        _keeper = Open();
        try { Initialize(); }
        catch
        {
            _keeper.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        var definition = _definition;
        using (var command = _keeper.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS state(
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                    definition TEXT NOT NULL,
                    version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS capacity(
                    resource TEXT NOT NULL, slot INTEGER NOT NULL, units INTEGER NOT NULL CHECK(units>=0),
                    PRIMARY KEY(resource,slot));
                CREATE TABLE IF NOT EXISTS reservations(
                    id TEXT PRIMARY KEY, owner TEXT NOT NULL, state INTEGER NOT NULL,
                    expires INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS demands(
                    id INTEGER PRIMARY KEY, reservation TEXT NOT NULL REFERENCES reservations(id),
                    resource TEXT NOT NULL, start INTEGER NOT NULL, finish INTEGER NOT NULL,
                    units INTEGER NOT NULL CHECK(units>0 AND finish>start));
                CREATE INDEX IF NOT EXISTS demands_resource_start ON demands(resource,start,id,finish,reservation,units);
                CREATE INDEX IF NOT EXISTS demands_reservation ON demands(reservation);
                CREATE INDEX IF NOT EXISTS reservations_expiry ON reservations(state,expires);
                CREATE TABLE IF NOT EXISTS commands(
                    id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, reply TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS changes(
                    version INTEGER PRIMARY KEY, kind TEXT NOT NULL, reservation TEXT);
                """;
            command.ExecuteNonQuery();
        }
        using var transaction = _keeper.BeginTransaction(deferred: false);
        var existing = Scalar(_keeper, transaction, "SELECT definition FROM state WHERE singleton=1;") as string;
        if (existing is not null && existing != definition.Version)
            throw new InvalidOperationException("The database is bound to a different calendar definition.");
        if (existing is null)
        {
            Execute(_keeper, transaction, "INSERT INTO state VALUES(1,$definition,0);", ("$definition", definition.Version));
            using var insert = Command(_keeper, transaction,
                "INSERT INTO capacity(resource,slot,units) VALUES($resource,$slot,$units);");
            insert.Parameters.Add("$resource", SqliteType.Text);
            insert.Parameters.Add("$slot", SqliteType.Integer);
            insert.Parameters.Add("$units", SqliteType.Integer);
            foreach (var resource in definition.Resources.Values)
            foreach (var period in resource.Availability)
                Seed(resource.Id, period, resource.Capacity);
            foreach (var client in definition.Clients.Values)
                Seed(CalendarDefinition.ClientResource(client.Id), definition.Bounds, 1);

            void Seed(string resource, Period period, int units)
            {
                insert.Parameters["$resource"].Value = resource;
                insert.Parameters["$units"].Value = units;
                for (var slot = definition.Slot(period.From); slot < definition.Slot(period.To); slot++)
                {
                    insert.Parameters["$slot"].Value = slot;
                    insert.ExecuteNonQuery();
                }
            }
        }
        transaction.Commit();
    }

    public CalendarSnapshot Capture(Period window)
    {
        ValidateWindow(window);
        using var connection = Open();
        Expire(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = Capture(connection, transaction, window);
        transaction.Commit();
        return result;
    }

    public ReservationReply TryHold(HoldCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Intent);
        if (command.Demands.Count == 0 || command.Lifetime <= TimeSpan.Zero)
            throw new ArgumentException("A hold requires a resource bundle and a positive lifetime.");
        var demands = command.Demands.OrderBy(d => d.ResourceId, StringComparer.Ordinal).ThenBy(d => d.Time.From)
            .ThenBy(d => d.Time.To).ThenBy(d => d.Units).ToArray();
        foreach (var demand in demands)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(demand.ResourceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(demand.Units);
            ValidateWindow(demand.Time);
            _ = _definition.Slot(demand.Time.From);
            _ = _definition.Slot(demand.Time.To);
        }
        var fingerprint = Fingerprint(new { Kind = "hold", command.Owner, command.Intent, command.DefinitionVersion,
            Demands = demands.Select(d => new { d.ResourceId, From = d.Time.From.UtcTicks, To = d.Time.To.UtcTicks, d.Units }),
            Lifetime = command.Lifetime.Ticks });
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Expire(connection, transaction);
        if (Previous(connection, transaction, command.Id, fingerprint) is { } previous)
        {
            transaction.Commit();
            return previous;
        }
        ReservationReply reply;
        if (command.DefinitionVersion != _definition.Version)
            reply = new(false, "DefinitionChanged", null, Version(connection, transaction));
        else if (demands.Any(demand => demand.Time.From < Clock.GetUtcNow()))
            reply = new(false, "StartInPast", null, Version(connection, transaction));
        else
        {
            var window = new Period(demands.Min(d => d.Time.From), demands.Max(d => d.Time.To));
            var snapshot = Capture(connection, transaction, window);
            var additions = Expand(demands);
            if (additions.Any(pair => checked(snapshot.Used.GetValueOrDefault(pair.Key) + pair.Value) >
                                      snapshot.Capacity.GetValueOrDefault(pair.Key)))
                reply = new(false, "NoCapacity", null, snapshot.Version);
            else
            {
                var expiry = Clock.GetUtcNow() + command.Lifetime;
                Execute(connection, transaction, "INSERT INTO reservations VALUES($id,$owner,$state,$expiry);",
                    ("$id", command.Id.ToString("N")), ("$owner", command.Owner),
                    ("$state", (int)ReservationState.Held), ("$expiry", expiry.UtcTicks));
                foreach (var demand in demands)
                    Execute(connection, transaction,
                        "INSERT INTO demands(reservation,resource,start,finish,units) VALUES($id,$resource,$start,$finish,$units);",
                        ("$id", command.Id.ToString("N")), ("$resource", demand.ResourceId),
                        ("$start", demand.Time.From.UtcTicks), ("$finish", demand.Time.To.UtcTicks), ("$units", demand.Units));
                var version = Changed(connection, transaction, "held", command.Id);
                reply = new(true, "Held", command.Id, version, expiry);
            }
        }
        Remember(connection, transaction, command.Id, fingerprint, reply);
        transaction.Commit();
        return reply;
    }

    public ReservationReply Confirm(Guid commandId, string owner, Guid holdId) => Transition(commandId, owner, holdId, true);
    public ReservationReply Cancel(Guid commandId, string owner, Guid reservationId) => Transition(commandId, owner, reservationId, false);

    public ReservationReply SetCapacity(Guid commandId, string resource, Period window, int units)
    {
        ValidateWindow(window);
        if (!_definition.Resources.ContainsKey(resource)) throw new ArgumentException("Unknown physical resource.");
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        var first = _definition.Slot(window.From);
        var end = _definition.Slot(window.To);
        var fingerprint = Fingerprint(new { Kind = "capacity", resource, first, end, units });
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Expire(connection, transaction);
        if (Previous(connection, transaction, commandId, fingerprint) is { } previous)
        {
            transaction.Commit();
            return previous;
        }
        var current = Capture(connection, transaction, window);
        var conflict = current.Used.Any(pair => pair.Key.ResourceId == resource && pair.Value > units);
        ReservationReply reply;
        if (conflict)
            reply = new(false, "CapacityInUse", null, current.Version);
        else
        {
            for (var slot = first; slot < end; slot++)
                Execute(connection, transaction,
                    "INSERT INTO capacity VALUES($resource,$slot,$units) ON CONFLICT(resource,slot) DO UPDATE SET units=excluded.units;",
                    ("$resource", resource), ("$slot", slot), ("$units", units));
            reply = new(true, "CapacityChanged", null, Changed(connection, transaction, "capacity", null));
        }
        Remember(connection, transaction, commandId, fingerprint, reply);
        transaction.Commit();
        return reply;
    }

    public BlockPage GetBlocks(Period window, IReadOnlyList<string> resourceIds, int limit = 1000)
    {
        ValidateWindow(window);
        if (limit is < 1 or > 10_000 || resourceIds.Count > 256)
            throw new ArgumentOutOfRangeException(nameof(limit), "Use at most 10,000 blocks and 256 resources per page.");
        if (resourceIds.Any(id => !_definition.Resources.ContainsKey(id) &&
                !_definition.Clients.Keys.Any(client => CalendarDefinition.ClientResource(client) == id)))
            throw new ArgumentException("Unknown resource.");
        using var connection = Open();
        Expire(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = Version(connection, transaction);
        if (resourceIds.Count == 0) return new(version, [], false);
        using var query = Command(connection, transaction, $"""
            SELECT d.id,d.reservation,d.resource,d.start,d.finish,r.state,r.expires
            FROM demands d JOIN reservations r ON r.id=d.reservation
            WHERE r.state IN (0,1) AND d.start<$to AND d.finish>$from
              AND d.resource IN ({string.Join(",", resourceIds.Select((_, index) => "$r" + index))})
            ORDER BY d.start,d.id LIMIT $limit;
            """, ("$from", window.From.UtcTicks), ("$to", window.To.UtcTicks), ("$limit", limit + 1));
        for (var index = 0; index < resourceIds.Count; index++) query.Parameters.AddWithValue("$r" + index, resourceIds[index]);
        using var reader = query.ExecuteReader();
        var blocks = new List<CalendarBlock>();
        while (reader.Read())
            blocks.Add(new CalendarBlock(reader.GetInt64(0), Guid.ParseExact(reader.GetString(1), "N"), reader.GetString(2),
                new Period(AtTicks(reader.GetInt64(3)), AtTicks(reader.GetInt64(4))),
                (ReservationState)reader.GetInt32(5), reader.GetInt32(5) == 0 ? AtTicks(reader.GetInt64(6)) : null));
        reader.Close();
        transaction.Commit();
        return new(version, Array.AsReadOnly(blocks.Take(limit).ToArray()), blocks.Count > limit);
    }

    private ReservationReply Transition(Guid commandId, string owner, Guid id, bool confirm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var fingerprint = Fingerprint(new { Kind = confirm ? "confirm" : "cancel", owner, id });
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Expire(connection, transaction);
        if (Previous(connection, transaction, commandId, fingerprint) is { } previous)
        {
            transaction.Commit();
            return previous;
        }
        ReservationReply reply;
        using var read = Command(connection, transaction, "SELECT owner,state,expires FROM reservations WHERE id=$id;",
            ("$id", id.ToString("N")));
        using var reader = read.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != owner)
            reply = new(false, "NotFound", null, Version(connection, transaction));
        else
        {
            var state = (ReservationState)reader.GetInt32(1);
            var expiry = AtTicks(reader.GetInt64(2));
            reader.Close();
            if ((confirm && state != ReservationState.Held) ||
                (!confirm && state is not (ReservationState.Held or ReservationState.Confirmed)))
                reply = new(false, "NotActive", id, Version(connection, transaction), expiry);
            else
            {
                Execute(connection, transaction, "UPDATE reservations SET state=$state WHERE id=$id;",
                    ("$state", (int)(confirm ? ReservationState.Confirmed : ReservationState.Cancelled)), ("$id", id.ToString("N")));
                reply = new(true, confirm ? "Confirmed" : "Cancelled", id,
                    Changed(connection, transaction, confirm ? "confirmed" : "cancelled", id), confirm ? null : expiry);
            }
        }
        reader.Close();
        Remember(connection, transaction, commandId, fingerprint, reply);
        transaction.Commit();
        return reply;
    }

    private CalendarSnapshot Capture(SqliteConnection connection, SqliteTransaction transaction, Period window)
    {
        var version = Version(connection, transaction);
        var capacity = new Dictionary<ResourceSlot, int>();
        using (var command = Command(connection, transaction,
                   "SELECT resource,slot,units FROM capacity WHERE slot>=$first AND slot<$end;",
                   ("$first", Floor(window.From)), ("$end", Ceiling(window.To))))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) capacity.Add(new(reader.GetString(0), reader.GetInt64(1)), reader.GetInt32(2));
        using var query = Command(connection, transaction, """
            SELECT d.resource,d.start,d.finish,d.units FROM demands d JOIN reservations r ON r.id=d.reservation
            WHERE r.state IN (0,1) AND d.start<$to AND d.finish>$from;
            """, ("$from", window.From.UtcTicks), ("$to", window.To.UtcTicks));
        using var allocations = query.ExecuteReader();
        var used = new Dictionary<ResourceSlot, long>();
        while (allocations.Read())
        {
            var first = Math.Max(_definition.Slot(AtTicks(allocations.GetInt64(1))), Floor(window.From));
            var end = Math.Min(_definition.Slot(AtTicks(allocations.GetInt64(2))), Ceiling(window.To));
            for (var slot = first; slot < end; slot++)
            {
                var key = new ResourceSlot(allocations.GetString(0), slot);
                used[key] = checked(used.GetValueOrDefault(key) + allocations.GetInt64(3));
            }
        }
        return new(version, _definition.Version, capacity.ToFrozenDictionary(), used.ToFrozenDictionary());
    }

    private Dictionary<ResourceSlot, long> Expand(IEnumerable<ResourceDemand> demands)
    {
        var result = new Dictionary<ResourceSlot, long>();
        foreach (var demand in demands)
        for (var slot = _definition.Slot(demand.Time.From); slot < _definition.Slot(demand.Time.To); slot++)
        {
            var key = new ResourceSlot(demand.ResourceId, slot);
            result[key] = checked(result.GetValueOrDefault(key) + demand.Units);
        }
        return result;
    }

    private void Expire(SqliteConnection connection)
    {
        // Check without taking the writer slot when no deadline has elapsed.
        using (var probe = Command(connection, null, "SELECT 1 FROM reservations WHERE state=0 AND expires<=$now LIMIT 1;",
                   ("$now", Clock.GetUtcNow().UtcTicks)))
            if (probe.ExecuteScalar() is null) return;
        using var transaction = connection.BeginTransaction(deferred: false);
        Expire(connection, transaction);
        transaction.Commit();
    }

    private void Expire(SqliteConnection connection, SqliteTransaction transaction)
    {
        var expired = new List<Guid>();
        using (var query = Command(connection, transaction, "SELECT id FROM reservations WHERE state=0 AND expires<=$now;",
                   ("$now", Clock.GetUtcNow().UtcTicks)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) expired.Add(Guid.ParseExact(reader.GetString(0), "N"));
        foreach (var id in expired)
        {
            Execute(connection, transaction, "UPDATE reservations SET state=2 WHERE id=$id;", ("$id", id.ToString("N")));
            Changed(connection, transaction, "expired", id);
        }
    }

    private static ReservationReply? Previous(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string fingerprint)
    {
        using var query = Command(connection, transaction, "SELECT fingerprint,reply FROM commands WHERE id=$id;",
            ("$id", id.ToString("N")));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("Command ID reused with a different payload.");
        return JsonSerializer.Deserialize<ReservationReply>(reader.GetString(1))!;
    }

    private static void Remember(SqliteConnection connection, SqliteTransaction transaction, Guid id,
        string fingerprint, ReservationReply reply) =>
        Execute(connection, transaction, "INSERT INTO commands VALUES($id,$fingerprint,$reply);",
            ("$id", id.ToString("N")), ("$fingerprint", fingerprint), ("$reply", JsonSerializer.Serialize(reply)));

    private static long Changed(SqliteConnection connection, SqliteTransaction transaction, string kind, Guid? reservation)
    {
        Execute(connection, transaction, "UPDATE state SET version=version+1 WHERE singleton=1;");
        var version = Version(connection, transaction);
        Execute(connection, transaction, "INSERT INTO changes VALUES($version,$kind,$reservation);",
            ("$version", version), ("$kind", kind), ("$reservation", reservation?.ToString("N")));
        return version;
    }

    private static long Version(SqliteConnection connection, SqliteTransaction transaction) =>
        (long)Scalar(connection, transaction, "SELECT version FROM state WHERE singleton=1;")!;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar();
    }

    private void ValidateWindow(Period window)
    {
        if (!_definition.Bounds.Contains(window)) throw new ArgumentOutOfRangeException(nameof(window));
    }

    private long Floor(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var result = Math.DivRem(ticks, _definition.Grid.Ticks, out var remainder);
        return remainder < 0 ? result - 1 : result;
    }

    private long Ceiling(DateTimeOffset time) => _definition.At(Floor(time)) == time ? Floor(time) : Floor(time) + 1;
    private static DateTimeOffset AtTicks(long ticks) => new(ticks, TimeSpan.Zero);
    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public void Dispose() => _keeper.Dispose();
}
