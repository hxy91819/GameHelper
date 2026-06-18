using System.Collections.Generic;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Allows a process monitor to update its cheap process-name gate when automation config changes.
/// </summary>
public interface IProcessNameFilterControl
{
    /// <summary>
    /// Replaces the allowed executable-name set. An empty set means no process names are allowed.
    /// </summary>
    void SetAllowedProcessNames(IEnumerable<string> processNames);
}
