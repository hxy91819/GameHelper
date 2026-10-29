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

    // The retired stored DTOs declared these fields in this order.
    private static int LegacyOrder(PropertyInfo property)
    {
        var names = property.DeclaringType == typeof(GameConfig)
            ? new[] { "dataKey", "executable", "displayName", "enabled", "hdr", "speed", "speedAttach", "speedHotkeys", "launch", "addedAt" }
            : property.DeclaringType == typeof(SyncSettings)
                ? new[] { "enabled", "provider", "method", "repo", "branch", "token", "directory", "intervalMinutes", "includeRawCsv" }
                : Array.Empty<string>();
        var index = Array.IndexOf(names, WireName(property));
        return index < 0 ? names.Length : index;
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties()
        .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0);

    private static T Configure<T>(T builder) where T : BuilderSkeleton<T>
    {
        // Computed views never belong in YAML; discover new writable fields automatically.
        foreach (var type in new[] { typeof(AppConfig), typeof(GameConfig), typeof(SyncSettings), typeof(ReportLlmSettings) })
            foreach (var property in type.GetProperties())
                builder.WithAttributeOverride(type, property.Name, property.CanWrite
                    ? new YamlMemberAttribute { Alias = WireName(property), ApplyNamingConventions = false, Order = LegacyOrder(property) }
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
            else if (diagnostics is not null)
                diagnostics.Errors.Add("startup must be a mapping (object).");
            else if (!IsNull(startup))
                throw new InvalidDataException("startup must be a mapping (object).");
        }
        if (diagnostics is not null)
        {
            if (!root.Children.ContainsKey(new YamlScalarNode("games")))
                diagnostics.Warnings.Add("Missing 'games' section. Nothing to validate.");
            if (root.Children.TryGetValue(new YamlScalarNode("games"), out var games) && games is YamlSequenceNode sequence)
                diagnostics.GameCount = sequence.Children.Count;
            CheckShape(root, typeof(AppConfig), "", diagnostics);
        }
        if (diagnostics is null) RestoreLegacyNullDefaults(root, typeof(AppConfig));
        using var writer = new StringWriter();
        stream.Save(writer, false);
        return Configure(new DeserializerBuilder()).IgnoreUnmatchedProperties().Build()
            .Deserialize<AppConfig>(writer.ToString()) ?? new AppConfig();
    }

    public static string Serialize(AppConfig config)
    {
        // Keep the legacy envelope and top-level order without re-emitting through YamlStream,
        // whose emitter drops sequence indentation and appends the document end marker.
        var stored = new
        {
            Monitor = config.ProcessMonitorType,
            Startup = new { LaunchOnStartup = config.LaunchOnSystemStartup },
            ConsoleModeOnLibraryOpen = config.ConsoleModeOnLibraryOpen ? (bool?)true : null,
            config.CheatEngine,
            config.BreakReminder,
            config.MonitorMode,
            config.Hdr,
            Sync = config.SyncSettings,
            config.Report,
            config.Games
        };
        return Configure(new SerializerBuilder()).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithIndentedSequences().WithNewLine("\n").Build().Serialize(stored);
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
    // Remove only malformed nodes from the diagnostic copy so other sections can still be checked.
    private static bool CheckShape(YamlNode node, Type type, string path, ValidationResult result)
    {
        var nullable = Nullable.GetUnderlyingType(type) is not null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        bool Invalid(string message) { result.Errors.Add(message); return false; }
        if (type == typeof(string) || type.IsValueType)
        {
            if (node is not YamlScalarNode scalar) return Invalid($"{path} must be a scalar.");
            if (nullable && IsNull(node)) return true;
            if (type == typeof(bool) && !bool.TryParse(scalar.Value, out _))
                return Invalid($"{(path == "launchOnSystemStartup" ? "startup.launchOnStartup" : path)} must be a boolean.");
            if (type.IsEnum)
            {
                if (!Enum.TryParse(type, scalar.Value, true, out var value) || !Enum.IsDefined(type, value!))
                    return Invalid($"{path} must be {string.Join(" or ", Enum.GetNames(type).Order(StringComparer.Ordinal))}.");
            }
            else if (type == typeof(DateTimeOffset))
            {
                if (!DateTimeOffset.TryParse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    return Invalid($"{path} must be a valid timestamp.");
            }
            else if (type.IsPrimitive && type != typeof(bool))
            {
                try { Convert.ChangeType(scalar.Value, type, CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                { return Invalid($"{path} has an invalid {type.Name} value."); }
            }
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (node is not YamlSequenceNode list) return Invalid($"{path} must be a sequence.");
            var index = 0;
            foreach (var item in list.Children.ToList())
                if (!CheckShape(item, type.GetGenericArguments()[0], $"{path}[{++index}]", result))
                    list.Children.Remove(item);
            return true;
        }
        if (node is not YamlMappingNode mapping) return Invalid($"{path} must be a mapping (object).");
        var properties = Properties(type).ToDictionary(WireName);
        foreach (var entry in mapping.Children.ToList())
        {
            var key = entry.Key.ToString();
            var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
            if (properties.TryGetValue(key, out var property))
            {
                if (!CheckShape(entry.Value, property.PropertyType, childPath, result)) mapping.Children.Remove(entry.Key);
            }
            else if (type == typeof(SpeedHotkey)) result.Errors.Add($"{childPath} is an unknown field.");
            else if (type != typeof(AppConfig)) result.Warnings.Add($"{childPath} is an unknown field.");
        }
        return true;
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
