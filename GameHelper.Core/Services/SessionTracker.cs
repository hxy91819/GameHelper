using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>
/// 追踪活跃的游戏进程实例，维护引用计数和按名称/路径索引。
/// 线程安全：所有公开方法内部持有锁，调用方无需额外同步。
/// </summary>
internal sealed class SessionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dataKeyRefs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 当前活跃会话总数（按 DataKey 去重计数）。
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
    /// 当前活跃的 DataKey 列表。
    /// </summary>
    public IEnumerable<string> ActiveDataKeys
    {
        get
        {
            lock (_lock)
            {
                return _dataKeyRefs.Keys.ToList(); // 快照，避免枚举期间修改
            }
        }
    }

    /// <summary>
    /// 注册一个活跃进程实例。
    /// 返回 true 表示这是该 DataKey 的第一个活跃实例。
    /// </summary>
    public bool Register(string dataKey, string? normalizedName, string? normalizedPath)
    {
        var entry = new ActiveProcessEntry(dataKey, normalizedName, normalizedPath);

        lock (_lock)
        {
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
    /// 释放一个 DataKey 的活跃引用。
    /// 返回 true 表示这是该 DataKey 的最后一个活跃实例。
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
    /// 尝试根据进程停止事件解析对应的活跃条目。
    /// 优先按路径查找，降级到按名称查找。
    /// </summary>
    public bool TryResolve(ProcessEventInfo processInfo, out ActiveProcessEntry entry)
    {
        lock (_lock)
        {
            var normalizedPath = PathNormalizer.NormalizePath(processInfo.ExecutablePath);
            if (normalizedPath is not null &&
                _activeByPath.TryGetValue(normalizedPath, out var byPath) &&
                byPath.Count > 0)
            {
                entry = byPath[0];
                RemoveEntry(normalizedPath, entry, byPath, _activeByPath);
                return true;
            }

            var normalizedName = PathNormalizer.NormalizeName(processInfo.ExecutableName);
            if (normalizedName is not null &&
                _activeByName.TryGetValue(normalizedName, out var byName) &&
                byName.Count > 0)
            {
                entry = byName[0];
                RemoveEntry(normalizedName, entry, byName, _activeByName);
                return true;
            }

            entry = default;
            return false;
        }
    }

    /// <summary>
    /// 清空所有活跃状态。用于服务停止时的重置。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _dataKeyRefs.Clear();
            _activeByName.Clear();
            _activeByPath.Clear();
        }
    }

    private void RemoveEntry(
        string key,
        ActiveProcessEntry entry,
        List<ActiveProcessEntry> list,
        Dictionary<string, List<ActiveProcessEntry>> map)
    {
        list.Remove(entry);
        if (list.Count == 0)
        {
            map.Remove(key);
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
