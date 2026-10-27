using GameHelper.Core.Validation;
using GameHelper.Infrastructure.Providers;
using YamlDotNet.Core;

namespace GameHelper.Infrastructure.Validators;

/// <summary>File and YAML diagnostics; all configuration value rules belong to Core.</summary>
public static class YamlConfigValidator
{
    public static ValidationResult Validate(string path)
    {
        var result = new ValidationResult();
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"Config file not found: {path}");
            var config = YamlConfigCodec.Deserialize(File.ReadAllText(path), result);
            var validation = AppConfigValidator.Validate(config);
            result.Errors.AddRange(validation.Errors);
            result.DuplicateCount = validation.DuplicateCount;
        }
        catch (YamlException ex)
        {
            result.Errors.Add($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}. Quote string values that contain ': ' when editing config.yml manually.");
        }
        catch (Exception ex) { result.Errors.Add(ex.Message); }
        result.IsValid = result.Errors.Count == 0;
        return result;
    }
}
