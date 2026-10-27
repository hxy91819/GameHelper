using System.Globalization;
using System.Reflection;
using GameHelper.Core.Models;
using GameHelper.Core.Validation;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameHelper.Infrastructure.Providers;

/// <summary>YAML wire names and scalar encodings. Domain properties are the schema; no shadow DTOs.</summary>
internal static class YamlConfigCodec
{
    private static string WireName(PropertyInfo property) => (property.DeclaringType, property.Name) switch
    {
        (var type, nameof(AppConfig.ProcessMonitorType)) when type == typeof(AppConfig) => "monitor",
        (var type, nameof(AppConfig.SyncSettings)) when type == typeof(AppConfig) => "sync",
        (var type, nameof(GameConfig.IsEnabled)) when type == typeof(GameConfig) => "enabled",
        (var type, nameof(GameConfig.HdrEnabled)) when type == typeof(GameConfig) => "hdr",
        (var type, nameof(GameConfig.SpeedMultiplier)) when type == typeof(GameConfig) => "speed",
        _ => CamelCaseNamingConvention.Instance.Apply(property.Name)
    };

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties()
        .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0);

    private static T Configure<T>(T builder) where T : BuilderSkeleton<T>
    {
        // Computed views never belong in YAML; discover new writable fields automatically.
        foreach (var type in new[] { typeof(AppConfig), typeof(GameConfig), typeof(SyncSettings) })
            foreach (var property in type.GetProperties())
                builder.WithAttributeOverride(type, property.Name, property.CanWrite
                    ? new YamlMemberAttribute { Alias = WireName(property), ApplyNamingConventions = false }
                    : new YamlIgnoreAttribute());
        return builder.WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new TimestampConverter());
    }

    public static AppConfig Deserialize(string text, ValidationResult? diagnostics = null)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count == 0)
        {
            if (diagnostics is null) return new AppConfig();
            throw new InvalidDataException("Invalid YAML: root mapping is missing.");
        }
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            if (diagnostics is null && IsNull(stream.Documents[0].RootNode)) return new AppConfig();
            throw new InvalidDataException("Invalid YAML: root mapping is missing.");
        }
        // startup is the only nested legacy envelope without a corresponding domain object.
        root.Children.Remove(new YamlScalarNode("launchOnSystemStartup"));
        if (root.Children.Remove(new YamlScalarNode("startup"), out var startup))
        {
            if (startup is YamlMappingNode mapping)
            {
                if (mapping.Children.TryGetValue(new YamlScalarNode("launchOnStartup"), out var launch))
                    root.Add("launchOnSystemStartup", launch);
            }
            else if (diagnostics is not null || !IsNull(startup))
                throw new InvalidDataException("startup must be a mapping (object).");
        }
        if (diagnostics is not null)
        {
            if (!root.Children.ContainsKey(new YamlScalarNode("games")))
                diagnostics.Warnings.Add("Missing 'games' section. Nothing to validate.");
            CheckShape(root, typeof(AppConfig), "", diagnostics);
        }
        if (diagnostics?.Errors.Count > 0) return new AppConfig();
        if (diagnostics is null) RestoreLegacyNullDefaults(root, typeof(AppConfig));
        using var writer = new StringWriter();
        stream.Save(writer, false);
        return Configure(new DeserializerBuilder()).IgnoreUnmatchedProperties().Build()
            .Deserialize<AppConfig>(writer.ToString()) ?? new AppConfig();
    }

    public static string Serialize(AppConfig config)
    {
        var text = Configure(new SerializerBuilder()).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithIndentedSequences().WithNewLine("\n").Build().Serialize(config);
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        root.Children.Remove(new YamlScalarNode("launchOnSystemStartup"), out var launch);
        root.Add("startup", new YamlMappingNode { { "launchOnStartup", launch! } });
        using var writer = new StringWriter { NewLine = "\n" };
        stream.Save(writer, false);
        return writer.ToString();
    }

    private static bool IsNull(YamlNode node) => node is YamlScalarNode scalar && scalar.Style == ScalarStyle.Plain &&
        (string.IsNullOrEmpty(scalar.Value) || scalar.Value == "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase));

    private static void RestoreLegacyNullDefaults(YamlNode node, Type type)
    {
        if (node is not YamlMappingNode mapping) return;
        foreach (var property in Properties(type))
        {
            var key = new YamlScalarNode(WireName(property));
            if (!mapping.Children.TryGetValue(key, out var value)) continue;
            // The former stored DTO used nullable scalars for these types, then applied model defaults.
            if ((type == typeof(AppConfig) || type == typeof(GameConfig) || type == typeof(SyncSettings)) &&
                property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null &&
                IsNull(value))
                mapping.Children.Remove(key);
            else if (value is YamlSequenceNode sequence && property.PropertyType.IsGenericType)
                foreach (var item in sequence.Children) RestoreLegacyNullDefaults(item, property.PropertyType.GetGenericArguments()[0]);
            else RestoreLegacyNullDefaults(value, property.PropertyType);
        }
    }

    // Only YAML shape/type diagnostics live here. Values and required fields are checked in Core.
    private static void CheckShape(YamlNode node, Type type, string path, ValidationResult result)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsValueType)
        {
            if (node is not YamlScalarNode scalar) { result.Errors.Add($"{path} must be a scalar."); return; }
            if (type == typeof(bool) && !bool.TryParse(scalar.Value, out _))
                result.Errors.Add($"{(path == "launchOnSystemStartup" ? "startup.launchOnStartup" : path)} must be a boolean.");
            if (type.IsEnum)
            {
                try { Enum.Parse(type, scalar.Value ?? "", true); }
                catch (ArgumentException) { result.Errors.Add($"{path} must be {string.Join(" or ", Enum.GetNames(type).Order(StringComparer.Ordinal))}."); }
            }
            else if (type.IsPrimitive && type != typeof(bool))
            {
                try { Convert.ChangeType(scalar.Value, type, CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                { result.Errors.Add($"{path} has an invalid {type.Name} value."); }
            }
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (node is not YamlSequenceNode list) { result.Errors.Add($"{path} must be a sequence."); return; }
            for (var index = 0; index < list.Children.Count; index++)
                CheckShape(list.Children[index], type.GetGenericArguments()[0], $"{path}[{index + 1}]", result);
            return;
        }
        if (node is not YamlMappingNode mapping) { result.Errors.Add($"{path} must be a mapping (object)."); return; }
        var properties = Properties(type).ToDictionary(WireName);
        foreach (var entry in mapping.Children)
        {
            var key = entry.Key.ToString();
            var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
            if (properties.TryGetValue(key, out var property)) CheckShape(entry.Value, property.PropertyType, childPath, result);
            else if (type == typeof(SpeedHotkey)) result.Errors.Add($"{childPath} is an unknown field.");
            else if (type != typeof(AppConfig)) result.Warnings.Add($"{childPath} is an unknown field.");
        }
    }

    private sealed class TimestampConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);
        public object? ReadYaml(IParser parser, Type type)
        {
            var scalar = parser.Consume<Scalar>();
            var value = scalar.Value;
            if (scalar.Style == ScalarStyle.Plain && (string.IsNullOrEmpty(value) || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
                return null;
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type) =>
            emitter.Emit(new Scalar(((DateTimeOffset)value!).ToString("O", CultureInfo.InvariantCulture)));
    }
}
