# mostlylucid.packing

Defined dimensional spaces for calendar scheduling, rotas and finite 3D packing. The package shares resource constraints, selection and verification across typed profiles. SQLite persists calendar capacity and reservations, and OR-Tools supplies an optional optimiser.

This is an initial .NET 10 preview. The projects produce `mostlylucid.packing`, `mostlylucid.packing.sqlite`, `mostlylucid.packing.ortools` and `mostlylucid.packing.relevance`.

```csharp
using var store = new SqliteCalendarStore("salon.db", definition);
var calendar = Pack.Calendar(definition, store, new CpSatPackingSolver());

var result = calendar.FindBest(
    from, to, TimeSpan.FromMinutes(45),
    new BookingRequirements("gels", "alice"));

if (result.Best is { } match)
{
    var hold = calendar.TryHold(commandId, "alice", match, TimeSpan.FromMinutes(5));
}
```

The definition supplies identified resources, capabilities, absolute availability periods, clients and services. A service declares its duration, required roles and fixed-offset stages. Bindings retain each role's resource across stages. A technician can be occupied for preparation and finishing while remaining free during drying. Separate reset stages retain the resources they need after customer service ends.

`FindTogether` searches for linked services at the same start. The returned match contains the entire group. `TryHold` rebuilds that match from the service rules and reserves every demand in one SQLite write transaction. An unavailable part rejects the whole bundle. The host authorises its caller and any delegation to book on behalf of friends before invoking these commands.

## Query and navigation

```csharp
var query = calendar.Query()
    .During([mondayMorning, wednesdayAfternoon])
    .OfSize(TimeSpan.FromMinutes(45))
    .Where(requirements)
    .OrderBy(Criteria.Start);

var first = query.FindBest();
var next = query.Next(first.Cursor!);
var previous = query.Previous(next.Cursor!);
var nearest = query.Nearest(preferredTime);
var alternatives = query.TopK(5);
```

A period bounds the whole resource footprint, including cleaning/reset. Adjacent periods merge into one continuous window. A match cannot bridge a gap between periods. Starts use the defined UTC grid. Windows can have finer bounds and the search rounds eligible starts inward. Client restrictions and role qualifications apply before ranking.

Results contain typed appointments, resource assignments, source revision and candidate completeness. The default ordering favours declared resource preferences and then earlier time. Navigation cursors bind the query, definition and source revision. A changed source or an unavailable cursor match requires refreshing the query. A query creates no reservations.

`Fits(at, size, requirements)` checks an individual exact start. Its completeness flag distinguishes a bounded search from an exhaustive failed fit. Service duration is validated against the catalogue. `Plan` selects compatible placements for several requests, with solver status, unfulfilled requests, utility and coverage reported separately.

## Run the salon reference

```bash
dotnet restore Mostlylucid.Packing.slnx
dotnet test Mostlylucid.Packing.slnx -c Release
dotnet run --project samples/Salon -c Release
```

The example books gels and a friend's pedicure together, checks technician availability during drying and finishing, confirms the hold, reads display blocks and navigates later appointments. It also packs two cubes through the spatial profile.

Use an optional path argument to persist the example. A confirmed appointment in that database remains occupied on the next run.

The SQLite store binds one definition to one database file and rejects a different definition on reopen. `SetCapacity` changes resource capacity transactionally and rejects reductions below active commitments. Confirmation retains occupancy. Cancellation or expiry releases it once. Command retries return their original result and deadline, and conflicting reuse of a command ID fails. Readers use a consistent SQLite snapshot. Independent connections coordinate holds through immediate write transactions with bounded SQLite busy admission.

`GetBlocks` returns bounded, resource-filtered allocation blocks in a period, their revision and a truncation flag. Intervals retain original endpoints for clipping in a view. Blocks describe holds and confirmations. Use `GetBlockPage` for keyset pagination, `GetBlockChanges` for scoped upserts/tombstones and `GetCapacityBlocks` for coalesced capacity summaries. View tokens bind the database identity, definition, period and resource selection. A write outside that selection advances the stream cursor while preserving its view revision. Relevant edits during pagination return `ResnapshotRequired`. Delta overflow or pruned history also requests a bounded resnapshot.

Capacity blocks retain minimum and maximum remaining units for the requested resolution, including mixed buckets. They describe resource capacity rather than a service-fit guarantee. The provider reads only selected resources for this projection, and an explicit cell budget bounds its work. Hold expiry advances projection changes during quiet reads. `PruneProjectionHistory` sets the history floor for old tokens. Applications authorise resource visibility before each request; a token is a query correlation value rather than a credential.

## Rota and physical profiles

`Pack.Rota(definition, store, solver)` exposes `FindBest`, `Fits` and batch `Plan` for named employees and qualifications. Each employee has capacity one. Shift requests declare their permitted periods, duration and required skills. Existing reservations consume the same resource slots. This preview supports interval capacity and qualification rules. Daily limits, minimum rest, fairness and joint shift/appointment capacity dependencies need additional declared constraints.

`Pack.Spatial3D(container, anchors, solver)` exposes exact placement checks and bounded box planning. It generates allowed axis rotations at supplied anchors, adds collision exclusions and verifies the returned layout. A query's region checks the actual rotated extent. Candidate completeness remains false for this finite anchor search. Support, loading order and irregular shapes need a specialised profile.

The default batch strategy is a deterministic greedy baseline. Add `mostlylucid.packing.ortools` and pass `CpSatPackingSolver` for maximum request count followed by preference utility. `IncrementalPackingSolver` reuses unchanged, proven component results. Solver time limits bound search, while preprocessing and validation also take time. Cancellation is checked around each bounded native solve.

## Relevance connection

`mostlylucid.packing.relevance` adds an asynchronous, dependency-neutral adapter. The scorer receives the full bounded feasible pool before top-K selection. It must score or explicitly exclude every input ID. Unknown IDs, duplicates, incomplete responses and non-finite scores fail. Returned matches preserve the source revision and candidate completeness. Authoritative holds still check current rules and capacity.

```csharp
var result = await query.WithRelevance(ranker).TopKAsync(5);
```

The supplied-candidate scoring operation belongs in the Relevance engine. This assembly maps appointment features and results through a callback, allowing the host to use its configured engine and storage provider. The optional integration example connects to the actual local `Mostlylucid.Relevance` project and its `RankCandidatesAsync` API.

```bash
dotnet run --project integration/Relevance -p:RelevanceSourceRoot=/path/to/mostlylucidweb
```

That example learns a preference for Sarah at 13:00, excludes that appointment after it is held, checks cold-start results and verifies that transient appointment candidates were never saved to the relevance catalogue. It stays outside the main solution because the Relevance SDK is developed in a separate local repository. The ordinary salon/packed-consumer example exercises the adapter contract with a configured deterministic scorer. Ranking scores express relative preference and carry no probability interpretation.

## Initial scope

The calendar profile implements fixed stage offsets and same-start linked groups. Variable stage timing, recurring local calendars, dynamic policy migrations, automatic relevance feature encoding and materialised tile caches remain extensions. Calendar definitions use explicit absolute periods. MIT licensing applies to this repository; the optional solver retains its own dependency licences.

The provider stores capacity cells for the configured horizon and retains command/change history. Configure finite horizons and candidate limits appropriate to the application. Production retention and migration policy remain application/provider work.

## Packages and release

```bash
dotnet pack src/Mostlylucid.Packing -c Release -o artifacts
dotnet pack src/Mostlylucid.Packing.Sqlite -c Release -o artifacts
dotnet pack src/Mostlylucid.Packing.OrTools -c Release -o artifacts
dotnet pack src/Mostlylucid.Packing.Relevance -c Release -o artifacts
dotnet restore samples/PackageConsumer --source ./artifacts --source https://api.nuget.org/v3/index.json
dotnet run --project samples/PackageConsumer -c Release --no-restore
```

The consumer uses package references and runs the same reference example. The release workflow tests and packs a tagged version, exercises the package consumer, then publishes the verified artifacts through GitHub OIDC.

Configure the NuGet trusted-publishing policy for owner `scottgal`, repository `mostlylucid.packing`, workflow `release.yml` and environment `nuget`. Its NuGet identity is `mostlylucid`. Publish with a version tag such as `v0.1.0-preview.2` once repository access, package ownership and trust policy are configured. No release was performed during the initial local implementation.
