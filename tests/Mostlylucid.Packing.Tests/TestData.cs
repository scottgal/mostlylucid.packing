using Mostlylucid.Packing.Planning;

namespace Mostlylucid.Packing.Tests;

public static class TestData
{
    public static PackingProblem ServerProblem() => new(
        [new(new DimensionId("small:cpu"), 4), new(new DimensionId("large:cpu"), 8)],
        [new("api"), new("jobs")],
        [new("api@small", "api", [new(new DimensionId("small:cpu"), 4)], []),
         new("api@large", "api", [new(new DimensionId("large:cpu"), 4)], []),
         new("jobs@small", "jobs", [new(new DimensionId("small:cpu"), 6)], []),
         new("jobs@large", "jobs", [new(new DimensionId("large:cpu"), 6)], [])]);
}
