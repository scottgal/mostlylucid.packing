using Mostlylucid.Packing.Calendar;

namespace Mostlylucid.Packing.Relevance;

public sealed record RelevanceReason(string Head, int Rank, double Distance, double Weight);
public sealed record CandidateRelevance(string CandidateId, double? Score, IReadOnlyList<RelevanceReason> Reasons,
    string? ExclusionReason = null);
public sealed record RankedCalendarMatch(CalendarMatch Match, double Score, IReadOnlyList<RelevanceReason> Reasons);
public sealed record RankedCalendarResult(IReadOnlyList<RankedCalendarMatch> Matches,
    IReadOnlyList<CandidateRelevance> Excluded, bool CandidatesComplete, long SourceVersion, string Ranker)
{
    public RankedCalendarMatch? Best => Matches.Count == 0 ? null : Matches[0];
}

/// <summary>Connects a feasible-pool scoring operation without requiring a particular Relevance storage provider.</summary>
public sealed class RelevanceRanker(string name,
    Func<IReadOnlyList<CalendarMatch>, CancellationToken, ValueTask<IReadOnlyList<CandidateRelevance>>> score)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A ranker needs a name.") : name;

    internal ValueTask<IReadOnlyList<CandidateRelevance>> ScoreAsync(IReadOnlyList<CalendarMatch> pool,
        CancellationToken cancellationToken) => score(pool, cancellationToken);
}

public static class RelevanceExtensions
{
    public static RelevanceQuery WithRelevance(this CalendarQuery query, RelevanceRanker ranker) => new(query, ranker);
}

public sealed class RelevanceQuery(CalendarQuery query, RelevanceRanker ranker)
{
    public ValueTask<RankedCalendarResult> FindBestAsync(CancellationToken cancellationToken = default) =>
        TopKAsync(1, cancellationToken);

    public async ValueTask<RankedCalendarResult> TopKAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = query.GetFeasibleCandidates();
        if (candidates.Matches.Count == 0)
            return new([], [], candidates.CandidatesComplete, candidates.Version, ranker.Name);
        var pool = candidates.Matches.ToDictionary(match => match.Id, StringComparer.Ordinal);
        var scored = (await ranker.ScoreAsync(candidates.Matches, cancellationToken)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ranked = new List<RankedCalendarMatch>();
        var excluded = new List<CandidateRelevance>();
        foreach (var item in scored)
        {
            if (!pool.TryGetValue(item.CandidateId, out var match) || !seen.Add(item.CandidateId))
                throw new InvalidOperationException("The ranker returned an unknown or duplicate candidate.");
            var reasons = Array.AsReadOnly(item.Reasons.ToArray());
            if (reasons.Any(reason => reason.Rank < 1 || !double.IsFinite(reason.Distance) ||
                                      !double.IsFinite(reason.Weight)))
                throw new InvalidOperationException("Ranking contributions must be finite with positive ranks.");
            if (item.Score is { } value)
            {
                if (!double.IsFinite(value) || item.ExclusionReason is not null)
                    throw new InvalidOperationException("A scored candidate needs a finite score and no exclusion.");
                ranked.Add(new(match, value, reasons));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(item.ExclusionReason))
                    throw new InvalidOperationException("An excluded candidate needs a reason.");
                excluded.Add(item with { Reasons = reasons });
            }
        }
        if (seen.Count != pool.Count)
            throw new InvalidOperationException("The ranker must score or explicitly exclude every supplied candidate.");
        return new(Array.AsReadOnly(ranked.OrderByDescending(item => item.Score).ThenBy(item => item.Match.Start)
                .ThenBy(item => item.Match.Id, StringComparer.Ordinal).Take(count).ToArray()),
            excluded.AsReadOnly(), candidates.CandidatesComplete, candidates.Version, ranker.Name);
    }
}
