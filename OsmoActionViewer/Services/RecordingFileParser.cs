using System.Text.RegularExpressions;

namespace OsmoActionViewer.Services;

public sealed record ParsedRecordingName(string TimestampText, string ClipNumber, int SegmentNumber)
{
    public string GroupKey => $"{TimestampText}_{ClipNumber}";
}

public static partial class RecordingFileParser
{
    [GeneratedRegex(@"^DJI_(\d{14})_(\d{4})_D(?:_(\d{3}))?\.MP4$", RegexOptions.IgnoreCase)]
    private static partial Regex DjiRegex();

    public static ParsedRecordingName? Parse(string fileName)
    {
        var match = DjiRegex().Match(fileName);
        if (!match.Success) return null;

        var timestamp = match.Groups[1].Value;
        var clip = match.Groups[2].Value;
        int segment = 0;
        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var parsed))
        {
            segment = parsed;
        }
        return new ParsedRecordingName(timestamp, clip, segment);
    }
}
