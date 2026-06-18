using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>
/// Tracks active game process instances by normalized name, normalized path, and DataKey reference count.
/// All public methods synchronize internally; callers do not need additional locking.
/// </summary>
internal sealed class SessionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ActiveProcessEntry> _activeByProcessId = new();
    private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dataKeyRefs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of currently active DataKeys.
    /// </summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _dataKeyRefs.Count;
            }
        }
    }

    /// <summary>
    /// Returns a stable snapshot of the currently active DataKeys.
    /// </summary>
    public IReadOnlyList<string> GetActiveDataKeysSnapshot()
    {
        lock (_lock)
        {
            return _dataKeyRefs.Keys.ToList();
        }
    }

    /// <summary>
    /// Returns executable names that must remain observable until their stop events arrive.
    /// </summary>
    public IReadOnlyList<string> GetActiveNamesSnapshot()
    {
        lock (_lock)
        {
            return _activeByName.Keys.ToList();
        }
    }

    /// <summary>
    /// Registers an active process instance.
    /// Returns true when this is the first active instance for the DataKey.
    /// </summary>
    public bool Register(string dataKey, string? normalizedName, string? normalizedPath, int? processId)
    {
        var entry = new ActiveProcessEntry(dataKey, normalizedName, normalizedPath, processId);

        lock (_lock)
        {
            if (processId.HasValue)
            {
                _activeByProcessId[processId.Value] = entry;
            }

            if (normalizedName is not null)
            {
                if (!_activeByName.TryGetValue(normalizedName, out var nameList))
                {
                    nameList = new List<ActiveProcessEntry>();
                    _activeByName[normalizedName] = nameList;
                }

                nameList.Add(entry);
            }

            if (normalizedPath is not null)
            {
                if (!_activeByPath.TryGetValue(normalizedPath, out var pathList))
                {
                    pathList = new List<ActiveProcessEntry>();
                    _activeByPath[normalizedPath] = pathList;
                }

                pathList.Add(entry);
            }

            if (!_dataKeyRefs.TryGetValue(dataKey, out var count))
            {
                _dataKeyRefs[dataKey] = 1;
                return true;
            }

            _dataKeyRefs[dataKey] = count + 1;
            return false;
        }
    }

    /// <summary>
    /// Releases one active reference for a DataKey.
    /// Returns true when this was the last active instance for the DataKey.
    /// </summary>
    public bool Release(string dataKey)
    {
        lock (_lock)
        {
            if (!_dataKeyRefs.TryGetValue(dataKey, out var count))
            {
                return true;
            }

            if (count <= 1)
            {
                _dataKeyRefs.Remove(dataKey);
                return true;
            }

            _dataKeyRefs[dataKey] = count - 1;
            return false;
        }
    }

    /// <summary>
    /// Resolves an active entry for a process stop event, preferring path over name.
    /// </summary>
    public bool TryResolve(ProcessEventInfo processInfo, out ActiveProcessEntry entry)
    {
        lock (_lock)
        {
            if (processInfo.ProcessId.HasValue &&
                _activeByProcessId.TryGetValue(processInfo.ProcessId.Value, out entry))
            {
                RemoveEntry(entry);
                return true;
            }

            var normalizedPath = PathNormalizer.NormalizePath(processInfo.ExecutablePath);
            if (normalizedPath is not null &&
                _activeByPath.TryGetValue(normalizedPath, out var byPath) &&
                byPath.Count > 0)
            {
                entry = byPath[0];
                RemoveEntry(entry);
                return true;
            }

            var normalizedName = PathNormalizer.NormalizeName(processInfo.ExecutableName);
            if (normalizedName is not null &&
                _activeByName.TryGetValue(normalizedName, out var byName) &&
                byName.Count > 0)
            {
                entry = byName[0];
                RemoveEntry(entry);
                return true;
            }

            entry = default;
            return false;
        }
    }

    /// <summary>
    /// Clears all active tracking state, used when the automation service stops.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _dataKeyRefs.Clear();
            _activeByProcessId.Clear();
            _activeByName.Clear();
            _activeByPath.Clear();
        }
    }

    private void RemoveEntry(ActiveProcessEntry entry)
    {
        if (entry.ProcessId.HasValue)
        {
            _activeByProcessId.Remove(entry.ProcessId.Value);
        }

        if (entry.NormalizedName is not null &&
            _activeByName.TryGetValue(entry.NormalizedName, out var nameList))
        {
            nameList.Remove(entry);
            if (nameList.Count == 0)
            {
                _activeByName.Remove(entry.NormalizedName);
            }
        }

        if (entry.NormalizedPath is not null &&
            _activeByPath.TryGetValue(entry.NormalizedPath, out var pathList))
        {
            pathList.Remove(entry);
            if (pathList.Count == 0)
            {
                _activeByPath.Remove(entry.NormalizedPath);
            }
        }
    }
}
