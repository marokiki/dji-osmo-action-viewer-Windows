using System.Collections.Generic;

namespace OsmoActionViewer.Models;

public sealed class RecordingMetadata
{
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public List<double> Markers { get; set; } = new();
    public string LocationText { get; set; } = "";
    public string GoogleMapsUrl { get; set; } = "";
}
