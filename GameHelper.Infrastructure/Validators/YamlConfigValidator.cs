using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GameHelper.Infrastructure.Validators
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public int GameCount { get; set; }
        public int DuplicateCount { get; set; }
    }

    public static class YamlConfigValidator
    {
        private enum FieldType { String, Bool, Number, Object }

        private sealed class FieldSpec
        {
            public required string Name { get; init; }
            public required FieldType Type { get; init; }
            public bool Required { get; init; }
        }

        private static IReadOnlyDictionary<string, FieldSpec> LoadTemplateSpec()
        {
            // Embedded resource path follows namespace + folder + filename
            var asm = typeof(YamlConfigValidator).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Validators.config.template.yml", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                throw new FileNotFoundException("Embedded template 'config.template.yml' not found.");

            using var stream = asm.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Unable to load template stream.") ;
            using var reader = new StreamReader(stream);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidOperationException("Invalid template YAML: root mapping missing.");

            if (!root.Children.TryGetValue(new YamlScalarNode("games"), out var gamesNode) || gamesNode is not YamlSequenceNode seq || seq.Children.Count == 0)
                throw new InvalidOperationException("Invalid template YAML: 'games' sequence with one object is required.");

            if (seq[0] is not YamlMappingNode obj)
                throw new InvalidOperationException("Invalid template YAML: first games item must be mapping.");

            var dict = new Dictionary<string, FieldSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj.Children)
            {
                var key = (kv.Key as YamlScalarNode)?.Value ?? string.Empty;
                var val = (kv.Value as YamlScalarNode)?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val)) continue;

                bool required = val.EndsWith("!", StringComparison.Ordinal);
                var core = required ? val[..^1] : val;
                var type = FieldType.String;
                if (core.Equals("bool", StringComparison.OrdinalIgnoreCase))
                {
                    type = FieldType.Bool;
                }
                else if (core.Equals("number", StringComparison.OrdinalIgnoreCase))
                {
                    type = FieldType.Number;
                }
                else if (core.Equals("object", StringComparison.OrdinalIgnoreCase)) type = FieldType.Object;

                dict[key] = new FieldSpec { Name = key, Type = type, Required = required };
            }
            return dict;
        }

        public static ValidationResult Validate(string path)
        {
            var result = new ValidationResult();
            try
            {
                if (!File.Exists(path))
                {
                    result.Errors.Add($"Config file not found: {path}");
                    result.IsValid = false;
                    return result;
                }

                var spec = LoadTemplateSpec();

                using var reader = new StreamReader(path);
                var yaml = new YamlStream();
                yaml.Load(reader);

                if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                {
                    result.Errors.Add("Invalid YAML: root mapping is missing.");
                    result.IsValid = false;
                    return result;
                }

                ValidateBreakReminder(root, result);
                if (!root.Children.TryGetValue(new YamlScalarNode("games"), out var gamesNode))
                {
                    result.Warnings.Add("Missing 'games' section. Nothing to validate.");
                    result.IsValid = result.Errors.Count == 0;
                    return result;
                }

                if (gamesNode is not YamlSequenceNode seq)
                {
                    result.Errors.Add("'games' should be a sequence (list).");
                    result.IsValid = false;
                    return result;
                }

                var dataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int dup = 0;
                int idx = 0;
                foreach (var item in seq)
                {
                    idx++;
                    if (item is not YamlMappingNode game)
                    {
                        result.Errors.Add($"games[{idx}] should be a mapping (object).");
                        continue;
                    }

                    // unknown fields
                    foreach (var kv in game.Children)
                    {
                        var key = (kv.Key as YamlScalarNode)?.Value ?? string.Empty;
                        if (!spec.ContainsKey(key))
                        {
                            result.Warnings.Add($"games[{idx}] unknown field: '{key}'");
                        }
                    }

                    // required
                    foreach (var req in spec.Values.Where(s => s.Required))
                    {
                        if (!game.Children.ContainsKey(new YamlScalarNode(req.Name)))
                        {
                            result.Errors.Add($"games[{idx}] missing required field: '{req.Name}'.");
                        }
                    }

                    // type checks
                    foreach (var field in spec.Values)
                    {
                        if (!game.Children.TryGetValue(new YamlScalarNode(field.Name), out var node)) continue;
                        if (field.Type == FieldType.Object)
                        {
                            ValidateLaunch(node, idx, result);
                            continue;
                        }
                        if (node is not YamlScalarNode scalar)
                        {
                            result.Errors.Add($"games[{idx}] '{field.Name}' must be a scalar.");
                            continue;
                        }
                        var val = scalar.Value ?? string.Empty;
                        if (field.Type == FieldType.Bool)
                        {
                            if (!IsBoolString(val))
                            {
                                result.Errors.Add($"games[{idx}] '{field.Name}' must be a boolean (true/false).");
                            }
                        }
                        else if (field.Type == FieldType.Number)
                        {
                            if (!double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                                !double.IsFinite(number) || number <= 0)
                            {
                                result.Errors.Add($"games[{idx}] '{field.Name}' must be a number greater than zero.");
                            }
                        }
                        else
                        {
                            // String: allow empty except when required
                            if (field.Required && string.IsNullOrWhiteSpace(val))
                            {
                                result.Errors.Add($"games[{idx}] '{field.Name}' cannot be empty.");
                            }
                        }
                    }

                    // duplicate dataKey detection
                    string? dataKey = null;
                    if (game.Children.TryGetValue(new YamlScalarNode("dataKey"), out var dataKeyNode) && dataKeyNode is YamlScalarNode dksc)
                    {
                        dataKey = dksc.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(dataKey))
                    {
                        if (!dataKeys.Add(dataKey))
                        {
                            dup++;
                            result.Errors.Add($"Duplicate dataKey: '{dataKey}'.");
                        }
                    }
                }

                result.GameCount = seq.Children.Count;
                result.DuplicateCount = dup;
                result.IsValid = result.Errors.Count == 0;
                return result;
            }
            catch (YamlException ex)
            {
                result.Errors.Add(FormatYamlException(ex));
                result.IsValid = false;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception: {ex.Message}");
                result.IsValid = false;
                return result;
            }
        }

        private static void ValidateBreakReminder(YamlMappingNode root, ValidationResult result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("breakReminder"), out var section)) return;
            if (section is not YamlMappingNode mapping)
            {
                result.Errors.Add("'breakReminder' must be a mapping (object).");
                return;
            }
            foreach (var entry in mapping.Children)
            {
                var name = (entry.Key as YamlScalarNode)?.Value ?? string.Empty;
                if (name is not ("enabled" or "sound" or "playMinutes" or "breakMinutes" or "repeatMinutes"))
                {
                    result.Warnings.Add($"breakReminder unknown field: '{name}'");
                    continue;
                }
                var value = (entry.Value as YamlScalarNode)?.Value;
                if (name is "enabled" or "sound")
                {
                    if (!IsBoolString(value ?? string.Empty)) result.Errors.Add($"breakReminder.{name} must be a boolean (true/false).");
                }
                else if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                {
                    result.Errors.Add($"breakReminder.{name} must be a positive integer.");
                }
            }
        }

        private static void ValidateLaunch(YamlNode node, int index, ValidationResult result)
        {
            if (node is not YamlMappingNode mapping)
            {
                result.Errors.Add($"games[{index}].launch must be a mapping (object).");
                return;
            }
            if (!mapping.Children.TryGetValue(new YamlScalarNode("target"), out var target)
                || target is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
                result.Errors.Add($"games[{index}].launch.target is required.");
            foreach (var entry in mapping.Children)
            {
                var key = (entry.Key as YamlScalarNode)?.Value;
                if (key is not ("target" or "arguments" or "workingDirectory"))
                    result.Warnings.Add($"games[{index}].launch unknown field: '{key}'");
                if (entry.Value is not YamlScalarNode) result.Errors.Add($"games[{index}].launch.{key} must be a scalar.");
            }
        }

        private static string FormatYamlException(YamlException ex)
        {
            var location = ex.Start.Line > 0
                ? $" at line {ex.Start.Line}, column {ex.Start.Column}"
                : string.Empty;

            return $"Invalid YAML{location}: {ex.Message}. Quote string values that contain ': ' when editing config.yml manually.";
        }

        private static bool IsBoolString(string s)
        {
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
