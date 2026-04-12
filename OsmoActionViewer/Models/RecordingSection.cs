using System.Collections.Generic;

namespace OsmoActionViewer.Models;

public sealed class RecordingSection
{
    public required string Name { get; init; }
    public required IReadOnlyList<Recording> Recordings { get; init; }
}
