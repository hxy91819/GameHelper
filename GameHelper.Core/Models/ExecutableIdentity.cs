namespace GameHelper.Core.Models;

/// <summary>
/// Immutable executable identity backed by one stored value.
/// </summary>
public sealed record ExecutableIdentity
{
    private ExecutableIdentity(string value)
    {
        Value = value;
        Path = LooksLikePath(value) ? value : null;
        Name = GetFileName(value);
    }

    /// <summary>
    /// The canonical stored value. It is either an executable file name or a path.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The executable file name derived from <see cref="Value"/>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The executable path derived from <see cref="Value"/>, or <see langword="null"/> for a name-only identity.
    /// </summary>
    public string? Path { get; }

    public bool IsPath => Path is not null;

    public static ExecutableIdentity Parse(string value)
    {
        if (!TryCreate(value, out var identity))
        {
            throw new ArgumentException("Executable identity is required.", nameof(value));
        }

        return identity;
    }

    public static bool TryCreate(string? value, out ExecutableIdentity identity)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(GetFileName(normalized)))
        {
            identity = null!;
            return false;
        }

        identity = new ExecutableIdentity(normalized);
        return true;
    }

    public override string ToString() => Value;

    private static string GetFileName(string value)
    {
        var lastSeparator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return lastSeparator >= 0 ? value[(lastSeparator + 1)..] : value;
    }

    private static bool LooksLikePath(string value) =>
        System.IO.Path.IsPathFullyQualified(value) || value.Contains('/') || value.Contains('\\');
}
