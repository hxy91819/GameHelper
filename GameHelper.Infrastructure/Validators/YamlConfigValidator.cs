using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private enum FieldType { String, Bool }

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
                FieldType type = core.Equals("bool", StringComparison.OrdinalIgnoreCase) ? FieldType.Bool : FieldType.String;

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

                if (!root.Children.TryGetValue(new YamlScalarNode("games"), out var gamesNode))
                {
                    result.Warnings.Add("Missing 'games' section. Nothing to validate.");
                    result.IsValid = true;
                    return result;
                }

                if (gamesNode is not YamlSequenceNode seq)
                {
                    result.Errors.Add("'games' should be a sequence (list).");
                    result.IsValid = false;
                    return result;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        else
                        {
                            // String: allow empty except when required
                            if (field.Required && string.IsNullOrWhiteSpace(val))
                            {
                                result.Errors.Add($"games[{idx}] '{field.Name}' cannot be empty.");
                            }
                        }
                    }

                    // duplicate name detection
                    string? name = null;
                    if (game.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) && nameNode is YamlScalarNode nsc)
                    {
                        name = nsc.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        if (!names.Add(name))
                        {
                            dup++;
                            result.Errors.Add($"Duplicate game name: '{name}'.");
                        }
                    }
                }

                result.GameCount = names.Count;
                result.DuplicateCount = dup;
                result.IsValid = result.Errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception: {ex.Message}");
                result.IsValid = false;
                return result;
            }
        }

        private static bool IsBoolString(string s)
        {
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
