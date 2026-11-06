using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Kampf;

/// <summary>
/// Preferences that outlive a session and belong to this machine rather than to
/// the notes themselves: the theme, the shape of the window and the keyboard
/// shortcuts. Stored beside the vault, so a portable vault keeps the look it was
/// given.
///
/// The model server is configured separately, in <c>chat-model.json</c>: it is a
/// connection, not a preference.
/// </summary>
public sealed record AppSettings(
    bool IsDarkTheme = true,
    double Width = 1420,
    double Height = 900,
    int? X = null,
    int? Y = null,
    bool Maximized = false,
    IReadOnlyDictionary<string, string>? Shortcuts = null)
{
    public const string FileName = "app-settings.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string vaultRoot) => Path.Combine(vaultRoot, FileName);

    /// <summary>A missing or unreadable file means the defaults, never a failure to open.</summary>
    public static AppSettings Load(string vaultRoot)
    {
        try
        {
            var path = PathFor(vaultRoot);
            if (!File.Exists(path))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save(string vaultRoot)
    {
        Directory.CreateDirectory(vaultRoot);
        File.WriteAllText(PathFor(vaultRoot), JsonSerializer.Serialize(this, Json));
    }
}
