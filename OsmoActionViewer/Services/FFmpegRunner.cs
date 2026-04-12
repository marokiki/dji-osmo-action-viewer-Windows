using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public string FFmpegPath { get; }

    public FFmpegRunner()
    {
        var baseDir = AppContext.BaseDirectory;
        FFmpegPath = Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
    }

    public bool IsAvailable => File.Exists(FFmpegPath);

    public async Task<(int ExitCode, string StdErr)> RunAsync(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stderr.ToString());
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
