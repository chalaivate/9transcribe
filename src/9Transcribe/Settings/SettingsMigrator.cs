using System.Text.Json.Nodes;

namespace NineTranscribe.Settings;

/// <summary>
/// Upgrades a settings document written by an older build, one schema version at a time,
/// before it is deserialized. Only breaking changes need a step here — adding a field is
/// handled by its C# default.
/// </summary>
public static class SettingsMigrator
{
    /// <returns>True when the document was changed and should be written back.</returns>
    public static bool Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        int version = ReadVersion(root);
        if (version >= AppSettings.CurrentSchemaVersion)
        {
            return false;
        }

        while (version < AppSettings.CurrentSchemaVersion)
        {
            switch (version)
            {
                case 0:
                    // Pre-release documents had no version stamp; nothing else differs.
                    break;

                default:
                    // Unknown intermediate version: stop rewriting and let deserialization
                    // fill the gaps with defaults rather than guess at the shape.
                    version = AppSettings.CurrentSchemaVersion;
                    continue;
            }

            version++;
        }

        root["SchemaVersion"] = AppSettings.CurrentSchemaVersion;
        return true;
    }

    private static int ReadVersion(JsonObject root)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in root)
        {
            if (!string.Equals(pair.Key, "SchemaVersion", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value is JsonValue value && value.TryGetValue(out int parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}
