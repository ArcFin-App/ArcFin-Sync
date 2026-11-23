using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// The catalogue of Arcfin app settings, shared with the app (the same settings-schema.json
/// ships in both). Only keys listed here are accepted from clients.
/// </summary>
public sealed class SettingsSchema
{
    public const string ScopeSynced = "synced";
    public const string ScopeDevice = "device";

    private static readonly Lazy<SettingsSchema> EmbeddedSchema = new(LoadEmbedded);

    private readonly Dictionary<string, SchemaSetting> _byKey;

    private SettingsSchema(string json, SchemaDocument document)
    {
        Json = json;
        Document = document;
        _byKey = new Dictionary<string, SchemaSetting>(StringComparer.Ordinal);
        foreach (var setting in document.Settings)
        {
            if (!string.IsNullOrEmpty(setting.Key))
            {
                _byKey.TryAdd(setting.Key, setting);
            }
        }
    }

    public static SettingsSchema Embedded => EmbeddedSchema.Value;

    public string Json { get; }

    public SchemaDocument Document { get; }

    public IEnumerable<SchemaSetting> Settings => Document.Settings.Where(s => _byKey.TryGetValue(s.Key, out var first) && ReferenceEquals(first, s));

    public static SettingsSchema Parse(string json)
    {
        var document = JsonSerializer.Deserialize<SchemaDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new InvalidDataException("Empty settings schema.");
        return new SettingsSchema(json, document);
    }

    public SchemaSetting? Find(string key) => _byKey.TryGetValue(key, out var setting) ? setting : null;

    public bool IsSynced(string key) => Find(key)?.Scope == ScopeSynced;

    /// <summary>Whether a wire value fits the setting's declared type.</summary>
    public static bool IsValidValue(SchemaSetting setting, string value)
    {
        return setting.Type switch
        {
            "bool" => value is "true" or "false",
            // An int with fixed choices only takes one of them.
            "int" => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                     && (setting.Options is not { Count: > 0 } || setting.Options.Any(o => o.Value == value)),
            _ => true
        };
    }

    private static SettingsSchema LoadEmbedded()
    {
        var assembly = typeof(SettingsSchema).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("settings-schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

public sealed class SchemaDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("groups")]
    public List<SchemaGroup> Groups { get; set; } = new();

    [JsonPropertyName("settings")]
    public List<SchemaSetting> Settings { get; set; } = new();
}

public sealed class SchemaGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public sealed class SchemaSetting
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = SettingsSchema.ScopeSynced;

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("options")]
    public List<SchemaOption>? Options { get; set; }

    /// <summary>Where the app shows the setting; empty means everywhere.</summary>
    [JsonPropertyName("platforms")]
    public List<string>? Platforms { get; set; }
}

public sealed class SchemaOption
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
