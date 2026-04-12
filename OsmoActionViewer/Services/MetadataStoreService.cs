using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OsmoActionViewer.Models;

namespace OsmoActionViewer.Services;

/// <summary>
/// Per-folder SQLite metadata store.
///
/// The DB lives at &lt;videoFolder&gt;/.osmo-action-viewer-metadata.sqlite, which is the
/// same layout the macOS build uses, so cloud-synced folders round-trip between
/// platforms with zero sync logic.
///
/// Data safety: the store lives with the user's videos, never inside the app
/// directory, so upgrading / uninstalling the app cannot destroy user edits.
/// Legacy data from %APPDATA%\OsmoActionViewer\metadata.sqlite (an earlier
/// Windows layout, should it ever exist) and from the per-folder JSON file are
/// copied forward once, and the originals are never deleted.
/// </summary>
public sealed class MetadataStoreService
{
    private const string FileName = ".osmo-action-viewer-metadata.sqlite";
    private const int SchemaVersion = 1;

    public Dictionary<string, RecordingMetadata> Load(string folderPath)
    {
        var dbPath = Path.Combine(folderPath, FileName);
        var dbExists = File.Exists(dbPath);

        using var connection = OpenConnection(dbPath, createIfMissing: true);
        EnsureSchema(connection);

        var current = ReadAll(connection);
        if (!dbExists || current.Count == 0)
        {
            var migrated = MigrateLegacyIfNeeded(connection, folderPath);
            if (migrated != null) return migrated;
        }
        return current;
    }

    public void Save(IDictionary<string, RecordingMetadata> entries, string folderPath)
    {
        var dbPath = Path.Combine(folderPath, FileName);
        using var connection = OpenConnection(dbPath, createIfMissing: true);
        EnsureSchema(connection);

        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM recording_metadata;";
                del.ExecuteNonQuery();
            }

            using var ins = connection.CreateCommand();
            ins.CommandText = @"
                INSERT INTO recording_metadata
                (recording_key, title, note, location_text, google_maps_url, markers_json, updated_at)
                VALUES ($k, $t, $n, $l, $g, $m, strftime('%s','now'));";
            var pKey = ins.CreateParameter(); pKey.ParameterName = "$k"; ins.Parameters.Add(pKey);
            var pTitle = ins.CreateParameter(); pTitle.ParameterName = "$t"; ins.Parameters.Add(pTitle);
            var pNote = ins.CreateParameter(); pNote.ParameterName = "$n"; ins.Parameters.Add(pNote);
            var pLoc = ins.CreateParameter(); pLoc.ParameterName = "$l"; ins.Parameters.Add(pLoc);
            var pGmap = ins.CreateParameter(); pGmap.ParameterName = "$g"; ins.Parameters.Add(pGmap);
            var pMarkers = ins.CreateParameter(); pMarkers.ParameterName = "$m"; ins.Parameters.Add(pMarkers);

            foreach (var kv in entries)
            {
                pKey.Value = kv.Key;
                pTitle.Value = kv.Value.Title ?? "";
                pNote.Value = kv.Value.Note ?? "";
                pLoc.Value = kv.Value.LocationText ?? "";
                pGmap.Value = kv.Value.GoogleMapsUrl ?? "";
                pMarkers.Value = JsonSerializer.Serialize(kv.Value.Markers ?? new List<double>());
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ---- Migration ---------------------------------------------------------

    private Dictionary<string, RecordingMetadata>? MigrateLegacyIfNeeded(
        SqliteConnection conn, string folderPath)
    {
        if (ReadMetaFlag(conn, "migrated_from_appsupport") == "1") return null;

        var merged = new Dictionary<string, RecordingMetadata>();

        foreach (var kv in LoadLegacyJson(folderPath)) merged[kv.Key] = kv.Value;
        foreach (var kv in LoadLegacyAppData(folderPath)) merged[kv.Key] = kv.Value;

        // Always set the flag (even if nothing was imported) to skip scanning on every open.
        WriteMetaFlag(conn, "migrated_from_appsupport", "1");

        if (merged.Count == 0) return null;

        try
        {
            // Recursive call is safe: the flag is now set so we won't loop.
            Save(merged, folderPath);
        }
        catch
        {
            // Return in-memory data even if persisting failed, so the user still sees it.
        }
        return merged;
    }

    private static Dictionary<string, RecordingMetadata> LoadLegacyJson(string folderPath)
    {
        var result = new Dictionary<string, RecordingMetadata>();
        var jsonPath = Path.Combine(folderPath, ".osmo-action-viewer-metadata.json");
        if (!File.Exists(jsonPath)) return result;

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("entries", out var entriesElement)) return result;

            foreach (var entry in entriesElement.EnumerateObject())
            {
                var meta = new RecordingMetadata();
                var v = entry.Value;
                if (v.TryGetProperty("title", out var t)) meta.Title = t.GetString() ?? "";
                if (v.TryGetProperty("note", out var n)) meta.Note = n.GetString() ?? "";
                if (v.TryGetProperty("locationText", out var l)) meta.LocationText = l.GetString() ?? "";
                if (v.TryGetProperty("googleMapsURL", out var g)) meta.GoogleMapsUrl = g.GetString() ?? "";
                if (v.TryGetProperty("markers", out var m) && m.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mk in m.EnumerateArray())
                    {
                        if (mk.TryGetDouble(out var d)) meta.Markers.Add(d);
                    }
                }
                result[entry.Name] = meta;
            }
        }
        catch
        {
            // Ignore malformed legacy JSON.
        }
        return result;
    }

    private static Dictionary<string, RecordingMetadata> LoadLegacyAppData(string folderPath)
    {
        var result = new Dictionary<string, RecordingMetadata>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return result;

        var legacyDb = Path.Combine(appData, "OsmoActionViewer", "metadata.sqlite");
        if (!File.Exists(legacyDb)) return result;

        try
        {
            using var conn = new SqliteConnection($"Data Source={legacyDb};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT recording_key, title, note, location_text, google_maps_url, markers_json
                FROM recording_metadata
                WHERE folder_path = $p;";
            cmd.Parameters.AddWithValue("$p", folderPath);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var meta = new RecordingMetadata
                {
                    Title = reader.GetString(1),
                    Note = reader.GetString(2),
                    LocationText = reader.GetString(3),
                    GoogleMapsUrl = reader.GetString(4),
                    Markers = ParseMarkers(reader.GetString(5)),
                };
                result[reader.GetString(0)] = meta;
            }
        }
        catch
        {
            // Legacy DB may have a different schema — best-effort.
        }
        return result;
    }

    // ---- Low-level helpers -------------------------------------------------

    private static SqliteConnection OpenConnection(string dbPath, bool createIfMissing)
    {
        var mode = createIfMissing ? "ReadWriteCreate" : "ReadWrite";
        var conn = new SqliteConnection($"Data Source={dbPath};Mode={mode}");
        conn.Open();
        return conn;
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS recording_metadata (
                recording_key TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                location_text TEXT NOT NULL DEFAULT '',
                google_maps_url TEXT NOT NULL DEFAULT '',
                markers_json TEXT NOT NULL DEFAULT '[]',
                updated_at INTEGER NOT NULL DEFAULT (strftime('%s','now'))
            );
            CREATE TABLE IF NOT EXISTS schema_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('schema_version', '" + SchemaVersion + @"');
        ";
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, RecordingMetadata> ReadAll(SqliteConnection conn)
    {
        var result = new Dictionary<string, RecordingMetadata>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT recording_key, title, note, location_text, google_maps_url, markers_json
            FROM recording_metadata;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var meta = new RecordingMetadata
            {
                Title = reader.GetString(1),
                Note = reader.GetString(2),
                LocationText = reader.GetString(3),
                GoogleMapsUrl = reader.GetString(4),
                Markers = ParseMarkers(reader.GetString(5)),
            };
            result[reader.GetString(0)] = meta;
        }
        return result;
    }

    private static List<double> ParseMarkers(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<double>();
        try
        {
            return JsonSerializer.Deserialize<List<double>>(json) ?? new List<double>();
        }
        catch
        {
            return new List<double>();
        }
    }

    private static string? ReadMetaFlag(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static void WriteMetaFlag(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO schema_meta (key, value) VALUES ($k, $v);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
