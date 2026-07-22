using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Defines which process lifecycle events a process monitor should observe.
/// </summary>
public sealed class ProcessObservationPolicy
{
    private readonly HashSet<string> _candidateProcessNames;

    /// <summary>
    /// Creates a candidate-gated observation policy. An empty candidate collection observes no processes.
    /// </summary>
    public ProcessObservationPolicy(
        IEnumerable<string> candidateProcessNames,
        bool observeStopEvents = true)
        : this(candidateProcessNames, observeStopEvents, observesAllProcessNames: false)
    {
    }

    private ProcessObservationPolicy(
        IEnumerable<string> candidateProcessNames,
        bool observeStopEvents,
        bool observesAllProcessNames)
    {
        ArgumentNullException.ThrowIfNull(candidateProcessNames);

        _candidateProcessNames = candidateProcessNames
            .Select(NormalizeProcessName)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CandidateProcessNames = Array.AsReadOnly(_candidateProcessNames
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        ObserveStopEvents = observeStopEvents;
        ObservesAllProcessNames = observesAllProcessNames;
    }

    /// <summary>
    /// Gets a normalized snapshot of candidate executable names.
    /// </summary>
    public IReadOnlyList<string> CandidateProcessNames { get; }

    /// <summary>
    /// Gets whether stop events should be emitted. Start events remain enabled.
    /// </summary>
    public bool ObserveStopEvents { get; }

    /// <summary>
    /// Gets whether the candidate-name gate is disabled.
    /// </summary>
    public bool ObservesAllProcessNames { get; }

    /// <summary>
    /// Creates a policy without a candidate-name gate.
    /// </summary>
    public static ProcessObservationPolicy ObserveAll(bool observeStopEvents = true) =>
        new(Array.Empty<string>(), observeStopEvents, observesAllProcessNames: true);

    /// <summary>
    /// Returns whether an executable name is included by this policy.
    /// </summary>
    public bool Includes(string? processName)
    {
        var normalizedName = NormalizeProcessName(processName);
        return normalizedName is not null &&
            (ObservesAllProcessNames || _candidateProcessNames.Contains(normalizedName));
    }

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var name = Path.GetFileName(processName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}.exe";
    }
}
