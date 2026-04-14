using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace OsmoActionViewer.Services;

/// <summary>
/// Thin wrapper around bundled ffmpeg.exe. The executable is expected in
/// &lt;appdir&gt;/ffmpeg/ffmpeg.exe (see csproj item). Callers build argument lists
/// and await RunAsync.
/// </summary>
public sealed class FFmpegRunner
{
    public sealed record ProgressUpdate(double? Fraction, string? Message);

    public string? FFmpegPath { get; }

    public FFmpegRunner()
    {
        var baseDir = AppContext.BaseDirectory;
        FFmpegPath = ResolveExecutablePath(baseDir, "ffmpeg.exe");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(FFmpegPath);

    public async Task<(int ExitCode, string StdErr)> RunAsync(
        IEnumerable<string> args,
        IProgress<ProgressUpdate>? progress = null,
        TimeSpan? expectedDuration = null)
    {
        if (!IsAvailable)
        {
            return (-1, "ffmpeg.exe was not found. Place it under the app's ffmpeg folder or install ffmpeg in PATH.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderr.AppendLine(e.Data);
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            TryReportProgress(e.Data, expectedDuration, progress);
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, stderr.Length > 0 ? stderr.ToString() : stdout.ToString());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static void TryReportProgress(
        string line,
        TimeSpan? expectedDuration,
        IProgress<ProgressUpdate>? progress)
    {
        if (progress == null || string.IsNullOrWhiteSpace(line)) return;

        var separator = line.IndexOf('=');
        if (separator <= 0 || separator == line.Length - 1) return;

        var key = line[..separator];
        var value = line[(separator + 1)..];

        switch (key)
        {
            case "out_time_ms" when expectedDuration.HasValue && expectedDuration.Value.TotalMilliseconds > 0:
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                {
                    var fraction = Math.Clamp(microseconds / 1000.0 / expectedDuration.Value.TotalMilliseconds, 0, 1);
                    progress.Report(new ProgressUpdate(fraction, null));
                }
                break;
            case "progress":
                progress.Report(new ProgressUpdate(value == "end" ? 1.0 : null, value == "end" ? "Finalizing..." : "Encoding..."));
                break;
        }
    }

    private static string? ResolveExecutablePath(string baseDir, string fileName)
    {
        var localCandidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg", fileName),
            Path.Combine(baseDir, fileName),
        };

        foreach (var candidate in localCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;

        var pathExts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasExtension = Path.HasExtension(fileName);

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (hasExtension)
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                    continue;
                }

                foreach (var ext in pathExts)
                {
                    var candidate = Path.Combine(dir, fileName + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    /// <summary>
    /// Produces a concat file for ffmpeg -f concat -i &lt;file&gt;. Returns the
    /// temp path; the caller is responsible for deleting it.
    /// </summary>
    public static string WriteConcatListFile(IEnumerable<string> inputPaths)
    {
        var path = Path.Combine(Path.GetTempPath(), $"osmo_concat_{Guid.NewGuid():N}.txt");
        using var w = new StreamWriter(path);
        foreach (var p in inputPaths)
        {
            // ffmpeg concat demuxer: path must be quoted if it contains spaces;
            // single quotes must be escaped as '\''.
            var escaped = p.Replace("'", "'\\''");
            w.WriteLine($"file '{escaped}'");
        }
        return path;
    }
}
