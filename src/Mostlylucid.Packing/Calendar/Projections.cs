namespace Mostlylucid.Packing.Calendar;

public enum ProjectionStatus { Current, ResnapshotRequired }
public sealed record BlockViewToken(string StoreId, string DefinitionVersion, string QueryKey,
    Period Window, IReadOnlyList<string> Resources, long Cursor, long Revision);
public sealed record BlockPageCursor(BlockViewToken View, long StartTicks, long BlockId);
public sealed record BlockViewPage(ProjectionStatus Status, BlockViewToken View,
    IReadOnlyList<CalendarBlock> Blocks, BlockPageCursor? Next);
public sealed record ChangedRange(string ResourceId, Period Time);
public sealed record BlockDelta(ProjectionStatus Status, BlockViewToken View,
    IReadOnlyList<CalendarBlock> Upserts, IReadOnlyList<long> Removed, IReadOnlyList<ChangedRange> Invalidated);
public sealed record CapacityBlock(string ResourceId, Period Time, int CapacityMin, int CapacityMax,
    int RemainingMin, int RemainingMax);
public sealed record CapacityView(BlockViewToken View, TimeSpan Resolution, DateTimeOffset AsOf,
    IReadOnlyList<CapacityBlock> Blocks);

public interface IBlockProjectionStore
{
    BlockViewPage GetBlockPage(Period window, IReadOnlyList<string> resources, int limit = 1000,
        BlockPageCursor? after = null);
    BlockDelta GetBlockChanges(BlockViewToken since, int limit = 1000);
    CapacityView GetCapacityBlocks(Period window, IReadOnlyList<string> resources, TimeSpan resolution,
        int maxCells = 1_000_000);
}
