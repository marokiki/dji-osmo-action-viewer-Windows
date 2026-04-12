using System;
using System.IO;

namespace OsmoActionViewer.Services;

public sealed record DetectedRecordingMetadata(DateTime? CapturedAt, string? LocationText);

public static class VideoMetadataDetector
{
    public static DetectedRecordingMetadata Detect(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new DetectedRecordingMetadata(null, null);
            var info = new FileInfo(filePath);
            return new DetectedRecordingMetadata(info.CreationTime, null);
        }
        catch
        {
            return new DetectedRecordingMetadata(null, null);
        }
    }
}
