using System.Text.RegularExpressions;
using GameHelper.Core.Models;

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
    /// <param name="executable">Executable identity used as the fallback source.</param>
    /// <param name="productName">Optional product name from metadata.</param>
    /// <param name="existingDataKeys">Existing DataKeys to check for uniqueness.</param>
    /// <returns>A unique DataKey string.</returns>
    public static string GenerateUniqueDataKey(
        ExecutableIdentity executable,
        string? productName,
        IEnumerable<string> existingDataKeys)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(existingDataKeys);

        var baseKey = GenerateBaseDataKey(executable, productName);
        return ConfigIdentity.EnsureUniqueDataKey(baseKey, existingDataKeys);
    }

    /// <summary>
    /// Generates a base DataKey without uniqueness check.
    /// Used by migration tool where uniqueness is guaranteed by source data.
    /// </summary>
    /// <param name="executable">Executable identity used as the fallback source.</param>
    /// <param name="productName">Optional product name from metadata.</param>
    /// <returns>A normalized DataKey string.</returns>
    public static string GenerateBaseDataKey(ExecutableIdentity executable, string? productName = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return !string.IsNullOrWhiteSpace(productName) && IsSuitableProductName(productName)
            ? NormalizeDataKey(productName)
            : NormalizeDataKey(Path.GetFileNameWithoutExtension(executable.Name));
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
