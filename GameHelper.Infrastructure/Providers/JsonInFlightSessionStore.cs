using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Providers;

/// <summary>
/// JSON file-backed store for in-flight session snapshots. Writes are atomic
/// (temporary file + rename) so a crash mid-write can never corrupt the snapshot.
/// </summary>
public sealed class JsonInFlightSessionStore : IInFlightSessionStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonInFlightSessionStore>? _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public JsonInFlightSessionStore(string filePath, ILogger<JsonInFlightSessionStore>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath required", nameof(filePath));
        }

        _filePath = filePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public InFlightSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize<InFlightSnapshot>(json);
            if (snapshot is null)
            {
                return null;
            }

            snapshot.Sessions ??= new List<InFlightSession>();
            snapshot.Sessions.RemoveAll(session => string.IsNullOrWhiteSpace(session.DataKey));
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "In-flight session snapshot at {Path} is unreadable; ignoring it.", _filePath);
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(InFlightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Unique temp name per write: a leftover ".tmp" that the current token can no longer
        // touch (stale ACL, read-only attribute, antivirus quarantine) must never wedge every
        // later save of the snapshot.
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            Path.GetFileName(_filePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, SerializerOptions), Encoding.UTF8);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Cleanup is best effort; a leftover unique temp file cannot block later saves.
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to remove in-flight session snapshot: {_filePath}", ex);
        }
    }
}