using Mostlylucid.Packing;
using Mostlylucid.Packing.Calendar;
using Mostlylucid.Packing.Relevance;
using Mostlylucid.Packing.Sqlite;
using Mostlylucid.Relevance;
using SalonExample;

var definition = SalonFixture.Create();
var clock = new ManualClock(SalonFixture.Day);
using var store = new SqliteCalendarStore(":memory:", definition, clock);
var calendar = Pack.Calendar(definition, store);
var relevanceDefinition = RelevanceDefinition.ForVectors(2);
relevanceDefinition.CandidateLimit = definition.CandidateLimit;
var evidence = new MemoryRelevanceStore();
await using var relevance = new RelevanceEngine(relevanceDefinition, evidence, clock: clock);
await relevance.SaveAsync("items", new RelevanceItem("past-visit", Features(SalonFixture.At(13), "sarah")));
await relevance.SaveAsync("events", new RelevanceObservation("visit-1", "alice", "past-visit"));

RelevanceRanker Ranker(string subject) => new("appointment-fit-v1", async (pool, ct) =>
{
    var supplied = pool.Select(match => new RelevanceItem(match.Id,
        Features(match.Start, match.Appointments[0].Resources["technician"]))).ToArray();
    var ranked = await relevance.RankCandidatesAsync(subject, "next", supplied, count: supplied.Length,
        cancellationToken: ct);
    var byId = ranked.ToDictionary(item => item.Id, StringComparer.Ordinal);
    return pool.Select(match => byId.TryGetValue(match.Id, out var item)
        ? new CandidateRelevance(item.Id, item.Score,
            item.Reasons.Select(reason => new RelevanceReason(reason.Head, reason.Rank, reason.Distance, reason.Weight)).ToArray())
        : new CandidateRelevance(match.Id, null, [], "Relevance eligibility")).ToArray();
});

var query = calendar.Query().During([new Period(SalonFixture.At(9), SalonFixture.At(17))])
    .OfSize(SalonFixture.Minutes(15)).Where(new BookingRequirements("consult", "alice"));
var result = await query.WithRelevance(Ranker("alice")).TopKAsync(5);
var best = result.Best!.Match;
if (best.Start != SalonFixture.At(13) || best.Appointments[0].Resources["technician"] != "sarah")
    throw new Exception("Learned preference did not select Sarah at 13:00.");
Console.WriteLine($"Learned appointment: {best.Start:HH:mm} with {best.Appointments[0].Resources["technician"]}");

var view = calendar.GetBlockPage(SalonFixture.At(12), SalonFixture.At(14), ["sarah"]);
var held = calendar.TryHold(Guid.NewGuid(), "alice", best, TimeSpan.FromMinutes(5));
if (!held.Success) throw new Exception(held.Reason);
var delta = calendar.GetBlockChanges(view.View);
if (delta.Upserts.Count != 1) throw new Exception("The viewport did not receive its new block.");
var changed = await query.WithRelevance(Ranker("alice")).TopKAsync(5);
if (changed.Matches.Any(item => item.Match.Id == best.Id)) throw new Exception("A busy appointment reached relevance ranking.");
Console.WriteLine($"After hold: {changed.Best!.Match.Start:HH:mm}; viewport upserts={delta.Upserts.Count}");
var cold = await query.WithRelevance(Ranker("new-customer")).TopKAsync(5);
if (cold.Matches.Any(item => item.Score != 0)) throw new Exception("Cold start invented personal evidence.");
if (await evidence.TransactAsync(tx => tx.Count(relevanceDefinition.Id, "candidate")) != 1)
    throw new Exception("Transient appointment candidates were persisted.");
Console.WriteLine("Cold-start, feasible-pool ranking and transient storage checks passed.");

static float[] Features(DateTimeOffset start, string technician) =>
    [(float)start.TimeOfDay.TotalMinutes / 1440, technician == "sarah" ? 1 : 0];
