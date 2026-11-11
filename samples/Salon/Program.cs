using Mostlylucid.Packing;
using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.OrTools;
using Mostlylucid.Packing.Planning;
using Mostlylucid.Packing.Spatial;
using Mostlylucid.Packing.Sqlite;
using Mostlylucid.Packing.Relevance;
using SalonExample;

var definition = SalonFixture.Create();
var clock = new ManualClock(SalonFixture.Day);
var path = args.Length > 0 ? args[0] : ":memory:";
using var store = new SqliteCalendarStore(path, definition, clock);
var calendar = Pack.Calendar(definition, store, new IncrementalPackingSolver(new CpSatPackingSolver()));
var friends = calendar.FindTogether(SalonFixture.At(10), SalonFixture.At(12),
    [SalonFixture.Gels(), SalonFixture.Pedi()]);
var group = friends.Best ?? throw new Exception("No joint appointment.");
Console.WriteLine($"Friends start together at {group.Start:HH:mm}.");
foreach (var appointment in group.Appointments)
    Console.WriteLine($"  {appointment.ClientId}: {appointment.ServiceId} with {appointment.Resources["technician"]}");
var command = Guid.NewGuid();
var hold = calendar.TryHold(command, "alice", group, TimeSpan.FromMinutes(5));
if (!hold.Success) throw new Exception(hold.Reason);
Console.WriteLine($"Group hold: {hold.Reason}");

var consult = new BookingRequirements("consult", "other",
    RequiredResources: new Dictionary<string, string> { ["technician"] = "sarah" });
Console.WriteLine($"Sarah free during drying: {calendar.Fits(SalonFixture.At(10, 15), SalonFixture.Minutes(15), consult).Fits}");
Console.WriteLine($"Sarah free during finishing: {calendar.Fits(SalonFixture.At(10, 35), SalonFixture.Minutes(15), consult).Fits}");
if (!calendar.Confirm(Guid.NewGuid(), "alice", hold.ReservationId!.Value).Success) throw new Exception("Confirmation failed.");
var blocks = calendar.GetBlocks(SalonFixture.At(10), SalonFixture.At(11), ["sarah", "anna", "manicure", "pedicure"]);
Console.WriteLine($"Display blocks: {blocks.Blocks.Count}, revision {blocks.Version}");

var query = calendar.Query().During([new Period(SalonFixture.At(13), SalonFixture.At(14))])
    .OfSize(SalonFixture.Minutes(15)).Where(consult).OrderBy(Criteria.Start);
var first = query.FindBest();
var next = query.Next(first.Cursor!);
if (next.Best is null || next.Best.Start <= first.Best!.Start) throw new Exception("Navigation failed.");
Console.WriteLine($"Next consult: {next.Best.Start:HH:mm}");

var boxes = Pack.Spatial3D(new Box3D(4, 2, 2), [new(0, 0, 0), new(2, 0, 0)], new CpSatPackingSolver())
    .Plan([new("a", new Box3D(2, 2, 2)), new("b", new Box3D(2, 2, 2))], TimeSpan.FromSeconds(2));
if (boxes.Placements.Count != 2) throw new Exception("Physical packing failed.");
Console.WriteLine($"Spatial plan: {boxes.Placements.Count} boxes.");


var projection = calendar.GetCapacityBlocks(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], TimeSpan.FromMinutes(15));
if (!projection.Blocks.Any(block => block.RemainingMin != block.RemainingMax))
    throw new Exception("Coarse blocks lost mixed availability.");
var page = calendar.GetBlockPage(SalonFixture.At(10), SalonFixture.At(11), ["sarah"], limit: 1);
if (page.Next is null) throw new Exception("The display page should have a continuation.");
Console.WriteLine($"Capacity runs: {projection.Blocks.Count}; detailed block page={page.Blocks.Count}");

var exampleRanker = new RelevanceRanker("configured-later-time", (pool, _) =>
    ValueTask.FromResult<IReadOnlyList<CandidateRelevance>>(pool.Select(match =>
        new CandidateRelevance(match.Id, match.Start.TimeOfDay.TotalMinutes, [])).ToArray()));
var ranked = await query.WithRelevance(exampleRanker).TopKAsync(2);
if (ranked.Best?.Match.Start != SalonFixture.At(13, 45)) throw new Exception("Feasible-pool ranking failed.");
Console.WriteLine($"Ranking adapter selected {ranked.Best.Match.Start:HH:mm}");
