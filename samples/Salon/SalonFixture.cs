using Mostlylucid.Packing.Calendar;

namespace SalonExample;

public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        lock (_gate) _now += amount;
    }
}

public static class SalonFixture
{
    public static DateTimeOffset Day => new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    public static DateTimeOffset At(int hour, int minute = 0) => Day.AddHours(hour - 9).AddMinutes(minute);
    public static TimeSpan Minutes(int value) => TimeSpan.FromMinutes(value);
    public static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    public static CalendarDefinition Create()
    {
        var opening = new Period(At(9), At(17));
        CalendarResource[] resources =
        [
            new("sarah", 1, Set("technician", "gels"), [opening]),
            new("anna", 1, Set("technician", "gels", "pedicure"), [opening]),
            new("manicure", 2, Set("manicure-station"), [opening]),
            new("pedicure", 1, Set("pedicure-station"), [opening]),
            new("lamp", 2, Set("drying-lamp"), [opening])
        ];
        Service[] services =
        [
            new("gels", Minutes(45),
                [new("technician", Set("technician", "gels")), new("station", Set("manicure-station")),
                    new("lamp", Set("drying-lamp"))],
                [
                    new("prepare", Minutes(0), Minutes(15), ["technician", "station"]),
                    new("dry", Minutes(15), Minutes(20), ["station", "lamp"]),
                    new("finish", Minutes(35), Minutes(10), ["technician", "station"]),
                    new("tech-reset", Minutes(45), Minutes(5), ["technician"]),
                    new("station-reset", Minutes(45), Minutes(10), ["station"])
                ]),
            new("pedicure", Minutes(30),
                [new("technician", Set("technician", "pedicure")), new("station", Set("pedicure-station"))],
                [new("service", Minutes(0), Minutes(30), ["technician", "station"]),
                    new("reset", Minutes(30), Minutes(5), ["station"])]),
            new("consult", Minutes(15), [new("technician", Set("technician"))],
                [new("service", Minutes(0), Minutes(15), ["technician"])])
        ];
        return new CalendarDefinition("salon", opening, Minutes(5), resources,
            [new("alice", true, Set()), new("beth", true, Set()), new("other", true, Set()),
                new("blocked", false, Set()), new("excluded", true, Set("sarah"))], services);
    }

    public static BookingRequirements Gels(string client = "alice") => new("gels", client,
        RequiredResources: new Dictionary<string, string> { ["technician"] = "sarah" });
    public static BookingRequirements Pedi(string client = "beth") => new("pedicure", client);
}
