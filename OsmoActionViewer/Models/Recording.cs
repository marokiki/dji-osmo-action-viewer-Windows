using System.Collections.Generic;

namespace OsmoActionViewer.Models;

public sealed class Recording
{
    public required string Key { get; init; }
    public required string SectionName { get; init; }
    public required string TimestampText { get; init; }
    public required string ClipNumber { get; init; }
    public required IReadOnlyList<string> SegmentPaths { get; init; }
    public string? FallbackDisplayName { get; init; }

    public string Id => Key;
}
