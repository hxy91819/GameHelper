using System.Text.RegularExpressions;
using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Utilities;

/// <summary>
/// Centralized DataKey generation logic to ensure consistency across the application.
/// </summary>
public static class DataKeyGenerator
{
    private static readonly Regex NonWordOrHyphen = new(@"[^\w\-]", RegexOptions.Compiled);

    /// <summary>
    /// Generates a unique DataKey from executable path and optional product name.
    /// Ensures uniqueness by appending a suffix if the key already exists.
    /// </summary>
    /// <param name="exePath">Full path to the executable file.</param>
    /// <param name="productName">Optional product name from metadata.</param>
    /// <param name="configProvider">Config provider to check for existing keys.</param>
    /// <returns>A unique DataKey string.</returns>
    public static string GenerateUniqueDataKey(
        string exePath,
        string? productName,
        IConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);

        var baseKey = GenerateBaseDataKey(exePath, productName);
        var existingKeys = configProvider.Load().Values.Select(c => c.DataKey);
        return ConfigIdentity.EnsureUniqueDataKey(baseKey, existingKeys);
    }

    /// <summary>
    /// Generates a base DataKey without uniqueness check.
    /// Used by migration tool where uniqueness is guaranteed by source data.
    /// </summary>
    /// <param name="exePath">Full path to the executable file.</param>
    /// <param name="productName">Optional product name from metadata.</param>
    /// <returns>A normalized DataKey string.</returns>
    public static string GenerateBaseDataKey(string exePath, string? productName = null)
    {
        return !string.IsNullOrWhiteSpace(productName) && IsSuitableProductName(productName)
            ? NormalizeDataKey(productName)
            : NormalizeDataKey(GetFileNameWithoutExtensionCrossPlatform(exePath));
    }

    private static string GetFileNameWithoutExtensionCrossPlatform(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalizedPath = path.Replace('\\', '/');
        return Path.GetFileNameWithoutExtension(normalizedPath);
    }

    private static string NormalizeDataKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.ToLowerInvariant();
        normalized = NonWordOrHyphen.Replace(normalized, "");
        return normalized.Trim('-', '_');
    }

    private static bool IsSuitableProductName(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return false;
        }

        if (productName.Length < 3)
        {
            return false;
        }

        var alphanumericCount = productName.Count(char.IsLetterOrDigit);
        if (alphanumericCount < 3)
        {
            return false;
        }

        var genericNames = new[] { "game", "application", "app", "program", "launcher" };
        return !genericNames.Contains(productName.ToLowerInvariant());
    }
}
