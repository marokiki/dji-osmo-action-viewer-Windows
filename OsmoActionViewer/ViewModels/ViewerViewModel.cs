using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using OsmoActionViewer.Models;
using OsmoActionViewer.Services;

namespace OsmoActionViewer.ViewModels;

public sealed partial class ViewerViewModel : ObservableObject
{
    private enum PlaybackRecoveryStage
    {
        None,
        Remuxed,
        Transcoded,
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v"
    };

    private readonly MetadataStoreService _metadataStore = new();
    private readonly FolderPersistence _folderPersistence = new();
    private readonly FFmpegRunner _ffmpeg = new();

    private Dictionary<string, RecordingMetadata> _metadataByKey = new();
    private Dictionary<string, DetectedRecordingMetadata> _detectedByKey = new();
    private readonly Dictionary<string, string> _playbackTempByRecordingId = new();
    private readonly Dictionary<string, PlaybackRecoveryStage> _playbackRecoveryStageByRecordingId = new();

    [ObservableProperty] private string? folderPath;
    [ObservableProperty] private string? selectedSectionName;
    [ObservableProperty] private string? selectedRecordingId;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string editingTitle = "";
    [ObservableProperty] private string editingLocationText = "";
    [ObservableProperty] private string editingGoogleMapsUrl = "";
    [ObservableProperty] private string markerInputSeconds = "";
    [ObservableProperty] private string markerClipDurationSecondsText = "10";
    [ObservableProperty] private double currentPlaybackSeconds;
    [ObservableProperty] private string exportStartSecondsText = "";
    [ObservableProperty] private string exportEndSecondsText = "";
    [ObservableProperty] private bool isExporting;
    [ObservableProperty] private Uri? currentMediaUri;

    public ObservableCollection<Recording> Recordings { get; } = new();
    public ObservableCollection<RecordingSection> Sections { get; } = new();
    public ObservableCollection<string> CheckedRecordingIds { get; } = new();
    public ObservableCollection<double> CurrentMarkers { get; } = new();

    public Recording? SelectedRecording =>
        SelectedRecordingId == null ? null : Recordings.FirstOrDefault(r => r.Id == SelectedRecordingId);

    public RecordingSection? SelectedSection =>
        SelectedSectionName == null ? null : Sections.FirstOrDefault(s => s.Name == SelectedSectionName);

    public IEnumerable<Recording> VisibleRecordings =>
        SelectedSection?.Recordings ?? Enumerable.Empty<Recording>();

    // ---- Folder handling ---------------------------------------------------

    public void ChooseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Recording Folder",
            InitialDirectory = FolderPath ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        SetFolder(dialog.FolderName);
    }

    public void RestoreLastOpenedFolderIfAvailable()
    {
        var last = _folderPersistence.Load();
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
        {
            SetFolder(last);
        }
    }

    private void SetFolder(string path)
    {
        FolderPath = path;
        _folderPersistence.Save(path);
        LoadRecordings(path, null);
    }

    // ---- Recording loading -------------------------------------------------

    public void LoadRecordings(string folder, string? preferredSectionName)
    {
        try
        {
            _metadataByKey = _metadataStore.Load(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load metadata: {ex.Message}";
            _metadataByKey = new Dictionary<string, RecordingMetadata>();
        }
        _detectedByKey.Clear();

        var grouped = new Dictionary<string, (string section, List<(ParsedRecordingName parsed, string path)> values, string? fallbackName)>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.*", System.IO.SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!SupportedExtensions.Contains(ext)) continue;

            var name = Path.GetFileName(file);
            var sectionName = SectionName(file, folder);
            var parsed = RecordingFileParser.Parse(name);
            if (parsed != null)
            {
                var key = $"{sectionName}|{parsed.GroupKey}";
                if (!grouped.TryGetValue(key, out var bucket))
                {
                    bucket = (sectionName, new(), null);
                }
                bucket.values.Add((parsed, file));
                grouped[key] = bucket;
            }
            else
            {
                var relative = file.Substring(folder.Length).TrimStart('\\', '/');
                var key = $"{sectionName}|RAW|{relative.Replace('\\', '/')}";
                var synth = new ParsedRecordingName("00000000000000", "0000", 0);
                grouped[key] = (sectionName, new() { (synth, file) },
                    Path.GetFileNameWithoutExtension(name));
            }
        }

        var built = grouped
            .Select(kv =>
            {
                var sorted = kv.Value.values.OrderBy(v => v.parsed.SegmentNumber).ToList();
                var first = sorted[0].parsed;
                return new Recording
                {
                    Key = kv.Key,
                    SectionName = kv.Value.section,
                    TimestampText = first.TimestampText,
                    ClipNumber = first.ClipNumber,
                    SegmentPaths = sorted.Select(v => v.path).ToList(),
                    FallbackDisplayName = kv.Value.fallbackName,
                };
            })
            .OrderBy(r => r.SectionName, StringComparer.Ordinal)
            .ThenBy(r => r.TimestampText, StringComparer.Ordinal)
            .ThenBy(r => r.ClipNumber, StringComparer.Ordinal)
            .ToList();

        if (ReconcileMetadataKeys(built))
        {
            SaveMetadataIfPossible();
        }

        var sections = built
            .GroupBy(r => r.SectionName)
            .Select(g => new RecordingSection { Name = g.Key, Recordings = g.ToList() })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        Recordings.Clear();
        foreach (var r in built) Recordings.Add(r);
        Sections.Clear();
        foreach (var s in sections) Sections.Add(s);

        var validIds = new HashSet<string>(built.Select(r => r.Id));
        for (int i = CheckedRecordingIds.Count - 1; i >= 0; i--)
        {
            if (!validIds.Contains(CheckedRecordingIds[i])) CheckedRecordingIds.RemoveAt(i);
        }

        string? targetSection = preferredSectionName ?? SelectedSectionName;
        if (targetSection == null || sections.All(s => s.Name != targetSection))
        {
            targetSection = sections.FirstOrDefault()?.Name;
        }
        SelectedSectionName = targetSection;

        var section = SelectedSection;
        if (section == null || section.Recordings.Count == 0)
        {
            ClearSelection();
            ErrorMessage = "No video files were found.";
            return;
        }

        var first2 = section.Recordings.First();
        Play(first2.Id);
    }

    private static string SectionName(string filePath, string rootFolder)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        if (!dir.StartsWith(rootFolder, StringComparison.Ordinal)) return "Uncategorized";
        var suffix = dir.Substring(rootFolder.Length).TrimStart('\\', '/');
        if (string.IsNullOrEmpty(suffix)) return "Root";
        var parts = suffix.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? NormalizeKeyPart(parts[0]) : "Root";
    }

    private bool ReconcileMetadataKeys(IReadOnlyList<Recording> recordings)
    {
        if (_metadataByKey.Count == 0 || recordings.Count == 0) return false;

        var exactKeys = recordings
            .Select(r => NormalizeMetadataKey(r.Key))
            .ToHashSet(StringComparer.Ordinal);
        var uniqueByGroupKey = recordings
            .Select(r => new { RecordingKey = NormalizeMetadataKey(r.Key), GroupKey = MetadataGroupKey(r.Key) })
            .Where(x => x.GroupKey != null)
            .GroupBy(x => x.GroupKey!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().RecordingKey, StringComparer.Ordinal);

        var reconciled = new Dictionary<string, RecordingMetadata>(StringComparer.Ordinal);
        var changed = false;

        foreach (var entry in _metadataByKey)
        {
            var normalizedKey = NormalizeMetadataKey(entry.Key);
            var targetKey = normalizedKey;

            if (!exactKeys.Contains(targetKey))
            {
                var groupKey = MetadataGroupKey(targetKey);
                if (groupKey != null && uniqueByGroupKey.TryGetValue(groupKey, out var matchedKey))
                {
                    targetKey = matchedKey;
                }
            }

            if (!ReferenceEquals(targetKey, entry.Key) && targetKey != entry.Key)
            {
                changed = true;
            }

            if (reconciled.TryGetValue(targetKey, out var existing))
            {
                MergeMetadata(existing, entry.Value);
                changed = true;
                continue;
            }

            reconciled[targetKey] = CloneMetadata(entry.Value);
        }

        if (!changed) return false;
        _metadataByKey = reconciled;
        return true;
    }

    private static RecordingMetadata CloneMetadata(RecordingMetadata source)
        => new()
        {
            Title = source.Title ?? "",
            Note = source.Note ?? "",
            LocationText = source.LocationText ?? "",
            GoogleMapsUrl = source.GoogleMapsUrl ?? "",
            Markers = source.Markers.ToList(),
        };

    private static void MergeMetadata(RecordingMetadata target, RecordingMetadata source)
    {
        if (string.IsNullOrWhiteSpace(target.Title) && !string.IsNullOrWhiteSpace(source.Title))
            target.Title = source.Title;
        if (string.IsNullOrWhiteSpace(target.Note) && !string.IsNullOrWhiteSpace(source.Note))
            target.Note = source.Note;
        if (string.IsNullOrWhiteSpace(target.LocationText) && !string.IsNullOrWhiteSpace(source.LocationText))
            target.LocationText = source.LocationText;
        if (string.IsNullOrWhiteSpace(target.GoogleMapsUrl) && !string.IsNullOrWhiteSpace(source.GoogleMapsUrl))
            target.GoogleMapsUrl = source.GoogleMapsUrl;

        foreach (var marker in source.Markers)
        {
            if (!target.Markers.Any(existing => Math.Abs(existing - marker) < 0.05))
            {
                target.Markers.Add(marker);
            }
        }
        target.Markers.Sort();
    }

    private static string NormalizeMetadataKey(string key)
    {
        var separator = key.IndexOf('|');
        if (separator < 0) return NormalizeKeyPart(key);

        var section = NormalizeKeyPart(key[..separator]);
        var suffix = NormalizeKeyPart(key[(separator + 1)..]);
        return $"{section}|{suffix}";
    }

    private static string NormalizeKeyPart(string value)
        => value.Normalize(NormalizationForm.FormC).Trim();

    private static string? MetadataGroupKey(string key)
    {
        var separator = key.IndexOf('|');
        if (separator < 0 || separator == key.Length - 1) return null;

        var suffix = key[(separator + 1)..];
        if (suffix.StartsWith("RAW|", StringComparison.Ordinal)) return null;
        return NormalizeKeyPart(suffix);
    }

    // ---- Selection / playback ---------------------------------------------

    public void Play(string recordingId)
    {
        var recording = Recordings.FirstOrDefault(r => r.Id == recordingId);
        if (recording == null) return;

        SelectedRecordingId = recordingId;
        var metadata = _metadataByKey.TryGetValue(recording.Key, out var m) ? m : new RecordingMetadata();
        var detected = GetDetectedMetadata(recording);

        EditingTitle = metadata.Title;
        EditingLocationText = string.IsNullOrEmpty(metadata.LocationText)
            ? (detected.LocationText ?? "")
            : metadata.LocationText;
        EditingGoogleMapsUrl = metadata.GoogleMapsUrl;

        MarkerInputSeconds = "";
        CurrentPlaybackSeconds = 0;
        ExportStartSecondsText = "";
        ExportEndSecondsText = "";

        // For multi-segment recordings we concat via ffmpeg to a temp file the
        // first time playback is requested. For single-segment recordings the
        // MediaElement can open the file directly.
        if (_playbackTempByRecordingId.TryGetValue(recording.Id, out var preparedPath) && File.Exists(preparedPath))
        {
            CurrentMediaUri = new Uri(preparedPath);
        }
        else if (recording.SegmentPaths.Count == 1)
        {
            CurrentMediaUri = new Uri(recording.SegmentPaths[0]);
        }
        else
        {
            _ = PrepareConcatPlaybackAsync(recording);
        }

        RefreshCurrentMarkers();
    }

    public async Task RecoverPlaybackForSelectedRecordingAsync(string? failureMessage)
    {
        var recording = SelectedRecording;
        if (recording == null) return;
        _playbackRecoveryStageByRecordingId.TryGetValue(recording.Id, out var stage);

        if (!_ffmpeg.IsAvailable)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(failureMessage)
                ? "Playback failed and ffmpeg.exe is not available for compatibility fallback."
                : $"Playback failed and ffmpeg.exe is not available for compatibility fallback: {failureMessage}";
            return;
        }

        if (recording.SegmentPaths.Count != 1)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(failureMessage)
                ? "Playback failed for the concatenated file."
                : $"Playback failed for the concatenated file: {failureMessage}";
            return;
        }

        switch (stage)
        {
            case PlaybackRecoveryStage.None:
                _playbackRecoveryStageByRecordingId[recording.Id] = PlaybackRecoveryStage.Remuxed;
                ErrorMessage = "Windows compatibility fallback: remuxing playback file...";
                await PrepareSinglePlaybackRemuxAsync(recording);
                break;
            case PlaybackRecoveryStage.Remuxed:
                _playbackRecoveryStageByRecordingId[recording.Id] = PlaybackRecoveryStage.Transcoded;
                ErrorMessage = "Windows compatibility fallback: transcoding playback file...";
                await PrepareSinglePlaybackTranscodeAsync(recording);
                break;
            default:
                ErrorMessage = string.IsNullOrWhiteSpace(failureMessage)
                    ? "Playback failed even after compatibility transcode."
                    : $"Playback failed even after compatibility transcode: {failureMessage}";
                break;
        }
    }

    private async Task PrepareConcatPlaybackAsync(Recording recording)
    {
        if (!_ffmpeg.IsAvailable)
        {
            ErrorMessage = "ffmpeg.exe not found — cannot play multi-segment recording.";
            return;
        }
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"osmo_play_{recording.Key.GetHashCode():X8}.mp4");
            if (!File.Exists(temp))
            {
                var listPath = FFmpegRunner.WriteConcatListFile(recording.SegmentPaths);
                try
                {
                    var (exit, err) = await _ffmpeg.RunAsync(new[]
                    {
                        "-y", "-f", "concat", "-safe", "0",
                        "-i", listPath,
                        "-c", "copy",
                        temp
                    });
                    if (exit != 0)
                    {
                        ErrorMessage = $"ffmpeg concat failed: {err}";
                        return;
                    }
                }
                finally { try { File.Delete(listPath); } catch { } }
            }
            if (SelectedRecordingId == recording.Id)
            {
                CurrentMediaUri = new Uri(temp);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Playback preparation failed: {ex.Message}";
        }
    }

    private async Task PrepareSinglePlaybackRemuxAsync(Recording recording)
    {
        try
        {
            var input = recording.SegmentPaths[0];
            var temp = Path.Combine(Path.GetTempPath(), $"osmo_play_fix_{recording.Key.GetHashCode():X8}_remux.mp4");
            if (!File.Exists(temp))
            {
                var (exit, err) = await _ffmpeg.RunAsync(new[]
                {
                    "-y",
                    "-i", input,
                    "-map", "0:v:0",
                    "-map", "0:a?",
                    "-dn",
                    "-c", "copy",
                    "-movflags", "+faststart",
                    temp
                });
                if (exit != 0)
                {
                    ErrorMessage = $"Playback remux failed: {err}";
                    return;
                }
            }

            _playbackTempByRecordingId[recording.Id] = temp;
            if (SelectedRecordingId == recording.Id)
            {
                CurrentMediaUri = new Uri(temp);
                ErrorMessage = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Playback remux failed: {ex.Message}";
        }
    }

    private async Task PrepareSinglePlaybackTranscodeAsync(Recording recording)
    {
        try
        {
            var input = recording.SegmentPaths[0];
            var temp = Path.Combine(Path.GetTempPath(), $"osmo_play_fix_{recording.Key.GetHashCode():X8}_h264.mp4");
            if (!File.Exists(temp))
            {
                var (exit, err) = await _ffmpeg.RunAsync(new[]
                {
                    "-y",
                    "-i", input,
                    "-map", "0:v:0",
                    "-map", "0:a?",
                    "-dn",
                    "-c:v", "libx264",
                    "-preset", "veryfast",
                    "-crf", "20",
                    "-pix_fmt", "yuv420p",
                    "-profile:v", "high",
                    "-level", "4.1",
                    "-c:a", "aac",
                    "-b:a", "192k",
                    "-movflags", "+faststart",
                    temp
                });
                if (exit != 0)
                {
                    ErrorMessage = $"Playback compatibility transcode failed: {err}";
                    return;
                }
            }

            _playbackTempByRecordingId[recording.Id] = temp;
            if (SelectedRecordingId == recording.Id)
            {
                CurrentMediaUri = new Uri(temp);
                ErrorMessage = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Playback compatibility transcode failed: {ex.Message}";
        }
    }

    // ---- Display -----------------------------------------------------------

    public string RecordingDisplayName(Recording recording)
    {
        if (_metadataByKey.TryGetValue(recording.Key, out var meta))
        {
            var t = (meta.Title ?? "").Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        if (!string.IsNullOrEmpty(recording.FallbackDisplayName)) return recording.FallbackDisplayName!;
        if (DateTime.TryParseExact(recording.TimestampText, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return $"{date:yyyy-MM-dd HH:mm:ss} clip{recording.ClipNumber}";
        }
        return $"DJI_{recording.TimestampText}_{recording.ClipNumber}";
    }

    public DateTime? EffectiveCapturedAt(Recording recording)
    {
        var detected = GetDetectedMetadata(recording).CapturedAt;
        if (detected != null) return detected;
        if (DateTime.TryParseExact(recording.TimestampText, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        return null;
    }

    private DetectedRecordingMetadata GetDetectedMetadata(Recording recording)
    {
        if (_detectedByKey.TryGetValue(recording.Key, out var cached)) return cached;
        var d = VideoMetadataDetector.Detect(recording.SegmentPaths[0]);
        _detectedByKey[recording.Key] = d;
        return d;
    }

    // ---- Metadata editing --------------------------------------------------

    public void PersistEditingMetadata()
    {
        var recording = SelectedRecording;
        if (recording == null) return;

        if (!_metadataByKey.TryGetValue(recording.Key, out var meta))
        {
            meta = new RecordingMetadata();
        }
        meta.Title = EditingTitle;
        meta.LocationText = EditingLocationText;
        meta.GoogleMapsUrl = (EditingGoogleMapsUrl ?? "").Trim();
        _metadataByKey[recording.Key] = meta;
        SaveMetadataIfPossible();
    }

    // ---- Markers -----------------------------------------------------------

    public IReadOnlyList<double> Markers(Recording recording)
    {
        if (!_metadataByKey.TryGetValue(recording.Key, out var meta)) return Array.Empty<double>();
        var list = meta.Markers.ToList();
        list.Sort();
        return list;
    }

    private void RefreshCurrentMarkers()
    {
        CurrentMarkers.Clear();
        var r = SelectedRecording;
        if (r == null) return;
        foreach (var m in Markers(r)) CurrentMarkers.Add(m);
    }

    public void AddMarkerAtCurrentTime() => AddMarker(CurrentPlaybackSeconds);

    public void AddMarkerFromInput()
    {
        if (!double.TryParse((MarkerInputSeconds ?? "").Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            ErrorMessage = "Marker seconds must be a valid number.";
            return;
        }
        AddMarker(seconds);
        MarkerInputSeconds = "";
    }

    private void AddMarker(double seconds)
    {
        var recording = SelectedRecording;
        if (recording == null) return;
        var rounded = Math.Round(Math.Max(0, seconds) * 10) / 10.0;
        if (!_metadataByKey.TryGetValue(recording.Key, out var meta))
        {
            meta = new RecordingMetadata();
        }
        if (meta.Markers.Any(m => Math.Abs(m - rounded) < 0.05)) return;
        meta.Markers.Add(rounded);
        meta.Markers.Sort();
        _metadataByKey[recording.Key] = meta;
        ErrorMessage = null;
        SaveMetadataIfPossible();
        RefreshCurrentMarkers();
    }

    public void RemoveMarker(double seconds)
    {
        var recording = SelectedRecording;
        if (recording == null) return;
        if (!_metadataByKey.TryGetValue(recording.Key, out var meta)) return;
        meta.Markers.RemoveAll(m => Math.Abs(m - seconds) < 0.05);
        _metadataByKey[recording.Key] = meta;
        SaveMetadataIfPossible();
        RefreshCurrentMarkers();
    }

    // ---- Checkboxes --------------------------------------------------------

    public bool IsChecked(string recordingId) => CheckedRecordingIds.Contains(recordingId);

    public void ToggleChecked(string recordingId)
    {
        if (CheckedRecordingIds.Contains(recordingId)) CheckedRecordingIds.Remove(recordingId);
        else CheckedRecordingIds.Add(recordingId);
    }

    public void SelectAllInCurrentSection()
    {
        var section = SelectedSection;
        if (section == null) return;
        foreach (var r in section.Recordings)
        {
            if (!CheckedRecordingIds.Contains(r.Id)) CheckedRecordingIds.Add(r.Id);
        }
    }

    public void ClearCheckedInCurrentSection()
    {
        var section = SelectedSection;
        if (section == null) return;
        var ids = section.Recordings.Select(r => r.Id).ToHashSet();
        for (int i = CheckedRecordingIds.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(CheckedRecordingIds[i])) CheckedRecordingIds.RemoveAt(i);
        }
    }

    public void DeleteCheckedRecordings()
    {
        var folder = FolderPath;
        if (folder == null) return;
        if (CheckedRecordingIds.Count == 0)
        {
            ErrorMessage = "No videos selected.";
            return;
        }
        var targets = Recordings.Where(r => CheckedRecordingIds.Contains(r.Id)).ToList();
        foreach (var recording in targets)
        {
            foreach (var path in recording.SegmentPaths)
            {
                try
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to delete video: {ex.Message}";
                    return;
                }
            }
        }
        CheckedRecordingIds.Clear();
        LoadRecordings(folder, SelectedSectionName);
    }

    // ---- Export helpers ---------------------------------------------------

    public string DefaultHighlightsFileName(Recording recording)
        => $"{SafeName(RecordingDisplayName(recording))}_highlights.mp4";

    public string DefaultRangeFileName(Recording recording, double start, double end)
        => $"{SafeName(RecordingDisplayName(recording))}_{(int)start}-{(int)end}.mp4";

    private static string SafeName(string input)
        => input.Replace('/', '-').Replace(':', '-').Replace('\\', '-');

    public string? ValidatedGoogleMapsUrl()
    {
        var raw = (EditingGoogleMapsUrl ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith("http://") || raw.StartsWith("https://"))
        {
            return Uri.TryCreate(raw, UriKind.Absolute, out _) ? raw : null;
        }
        var prefixed = "https://" + raw;
        return Uri.TryCreate(prefixed, UriKind.Absolute, out _) ? prefixed : null;
    }

    // ---- Misc -------------------------------------------------------------

    public void Seek(double deltaSeconds) { /* Wired by View via MediaElement. */ }
    public void TogglePlayPause() { /* Wired by View. */ }

    public void SetExportStartFromCurrentTime() =>
        ExportStartSecondsText = Math.Max(0, CurrentPlaybackSeconds)
            .ToString("F1", CultureInfo.InvariantCulture);

    public void SetExportEndFromCurrentTime() =>
        ExportEndSecondsText = Math.Max(0, CurrentPlaybackSeconds)
            .ToString("F1", CultureInfo.InvariantCulture);

    private void ClearSelection()
    {
        SelectedRecordingId = null;
        EditingTitle = "";
        EditingLocationText = "";
        EditingGoogleMapsUrl = "";
        ExportStartSecondsText = "";
        ExportEndSecondsText = "";
        CurrentPlaybackSeconds = 0;
        CurrentMediaUri = null;
        CurrentMarkers.Clear();
    }

    private void SaveMetadataIfPossible()
    {
        var folder = FolderPath;
        if (folder == null) return;
        try
        {
            _metadataStore.Save(_metadataByKey, folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save metadata: {ex.Message}";
        }
    }

    // ---- Export (ffmpeg) ---------------------------------------------------

    public async Task ExportHighlightsFromMarkersAsync(string outputPath)
    {
        var recording = SelectedRecording;
        if (recording == null || IsExporting) return;

        var markers = Markers(recording);
        if (markers.Count == 0) { ErrorMessage = "No markers found for this video."; return; }

        if (!double.TryParse((MarkerClipDurationSecondsText ?? "").Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var clipDuration) || clipDuration <= 0)
        {
            ErrorMessage = "Highlight clip duration must be a number greater than 0."; return;
        }

        IsExporting = true;
        ErrorMessage = null;
        try
        {
            var title = RecordingDisplayName(recording);
            var ok = await ExportMarkerHighlightsAsync(recording, markers, clipDuration, title, outputPath);
            if (!ok) ErrorMessage = "Highlight export failed.";
        }
        finally { IsExporting = false; }
    }

    public async Task ExportHighlightsFromCheckedAsync(string outputPath)
    {
        if (IsExporting) return;
        if (CheckedRecordingIds.Count == 0) { ErrorMessage = "Select videos first (checkbox mode)."; return; }

        if (!double.TryParse((MarkerClipDurationSecondsText ?? "").Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var clipDuration) || clipDuration <= 0)
        {
            ErrorMessage = "Highlight clip duration must be a number greater than 0."; return;
        }

        var targets = Recordings.Where(r => CheckedRecordingIds.Contains(r.Id)).ToList();
        var requests = new List<(Recording rec, IReadOnlyList<double> markers)>();
        foreach (var r in targets)
        {
            var m = Markers(r);
            if (m.Count > 0) requests.Add((r, m));
        }
        if (requests.Count == 0) { ErrorMessage = "No markers found on selected videos."; return; }

        IsExporting = true;
        ErrorMessage = null;
        try
        {
            var ok = await ExportMultiHighlightsAsync(requests, clipDuration, outputPath);
            if (!ok) ErrorMessage = "Highlight export failed.";
        }
        finally { IsExporting = false; }
    }

    public async Task ExportSelectedRangeAsync(string outputPath)
    {
        var recording = SelectedRecording;
        if (recording == null || IsExporting) return;

        if (!double.TryParse(ExportStartSecondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(ExportEndSecondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
        {
            ErrorMessage = "Please enter numeric start/end seconds."; return;
        }
        if (start < 0 || end <= start)
        {
            ErrorMessage = "End seconds must be greater than start seconds."; return;
        }

        IsExporting = true;
        ErrorMessage = null;
        try
        {
            var input = await EnsurePlayableInputAsync(recording);
            if (input == null) { ErrorMessage = "Failed to prepare input video."; return; }

            var args = new List<string>
            {
                "-y",
                "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
                "-to", end.ToString("F3", CultureInfo.InvariantCulture),
                "-i", input,
                "-c:v", "libx264", "-c:a", "aac",
                "-movflags", "+faststart",
            };
            var capturedAt = EffectiveCapturedAt(recording);
            if (capturedAt.HasValue)
            {
                var offset = capturedAt.Value.AddSeconds(start);
                args.Add("-metadata");
                args.Add($"creation_time={offset.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
            }
            args.Add(outputPath);

            var (exit, err) = await _ffmpeg.RunAsync(args);
            if (exit != 0) ErrorMessage = $"Export failed: {err}";
        }
        finally { IsExporting = false; }
    }

    private async Task<string?> EnsurePlayableInputAsync(Recording recording)
    {
        if (recording.SegmentPaths.Count == 1) return recording.SegmentPaths[0];
        var temp = Path.Combine(Path.GetTempPath(), $"osmo_merged_{recording.Key.GetHashCode():X8}.mp4");
        if (File.Exists(temp)) return temp;
        var listPath = FFmpegRunner.WriteConcatListFile(recording.SegmentPaths);
        try
        {
            var (exit, _) = await _ffmpeg.RunAsync(new[]
            {
                "-y", "-f", "concat", "-safe", "0",
                "-i", listPath, "-c", "copy", temp
            });
            return exit == 0 ? temp : null;
        }
        finally { try { File.Delete(listPath); } catch { } }
    }

    private async Task<bool> ExportMarkerHighlightsAsync(
        Recording recording, IReadOnlyList<double> markers, double clipDuration,
        string title, string outputPath)
    {
        var input = await EnsurePlayableInputAsync(recording);
        if (input == null) return false;

        var clipPaths = new List<string>();
        try
        {
            int idx = 0;
            foreach (var marker in markers.OrderBy(m => m))
            {
                var start = Math.Max(0, marker);
                var clipPath = Path.Combine(Path.GetTempPath(),
                    $"osmo_hl_{recording.Key.GetHashCode():X8}_{idx++}.mp4");
                var drawtext = BuildDrawTextFilter(title);
                var args = new List<string>
                {
                    "-y",
                    "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
                    "-i", input,
                    "-t", clipDuration.ToString("F3", CultureInfo.InvariantCulture),
                    "-vf", drawtext,
                    "-c:v", "libx264", "-c:a", "aac",
                    clipPath
                };
                var (exit, err) = await _ffmpeg.RunAsync(args);
                if (exit != 0) { ErrorMessage = err; return false; }
                clipPaths.Add(clipPath);
            }

            return await ConcatClipsAsync(clipPaths, outputPath);
        }
        finally
        {
            foreach (var p in clipPaths) { try { File.Delete(p); } catch { } }
        }
    }

    private async Task<bool> ExportMultiHighlightsAsync(
        List<(Recording rec, IReadOnlyList<double> markers)> requests,
        double clipDuration, string outputPath)
    {
        var clipPaths = new List<string>();
        try
        {
            int idx = 0;
            foreach (var (rec, markers) in requests)
            {
                var input = await EnsurePlayableInputAsync(rec);
                if (input == null) return false;
                var title = RecordingDisplayName(rec);

                foreach (var marker in markers.OrderBy(m => m))
                {
                    var start = Math.Max(0, marker);
                    var clipPath = Path.Combine(Path.GetTempPath(),
                        $"osmo_hlm_{idx++}_{rec.Key.GetHashCode():X8}.mp4");
                    var drawtext = BuildDrawTextFilter(title);
                    var args = new List<string>
                    {
                        "-y",
                        "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
                        "-i", input,
                        "-t", clipDuration.ToString("F3", CultureInfo.InvariantCulture),
                        "-vf", drawtext,
                        "-c:v", "libx264", "-c:a", "aac",
                        clipPath
                    };
                    var (exit, err) = await _ffmpeg.RunAsync(args);
                    if (exit != 0) { ErrorMessage = err; return false; }
                    clipPaths.Add(clipPath);
                }
            }
            return await ConcatClipsAsync(clipPaths, outputPath);
        }
        finally
        {
            foreach (var p in clipPaths) { try { File.Delete(p); } catch { } }
        }
    }

    private async Task<bool> ConcatClipsAsync(List<string> clipPaths, string outputPath)
    {
        if (clipPaths.Count == 0) return false;
        var listPath = FFmpegRunner.WriteConcatListFile(clipPaths);
        try
        {
            var (exit, err) = await _ffmpeg.RunAsync(new[]
            {
                "-y", "-f", "concat", "-safe", "0",
                "-i", listPath, "-c", "copy", outputPath
            });
            if (exit != 0) { ErrorMessage = err; return false; }
            return true;
        }
        finally { try { File.Delete(listPath); } catch { } }
    }

    private static string BuildDrawTextFilter(string title)
    {
        // Escape characters that have meaning in drawtext.
        var escaped = (title ?? "")
            .Replace("\\", "\\\\")
            .Replace(":", "\\:")
            .Replace("'", "\\'");
        return $"drawtext=text='{escaped}':fontcolor=white:fontsize=32:" +
               $"box=1:boxcolor=black@0.55:boxborderw=8:x=w-tw-24:y=h-th-32";
    }
}
