using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.ArcfinSync.Api.Storage;

/// <summary>Small JSON files in the plugin's data folder, written whole so a reader never sees half a file.</summary>
internal static class JsonFile
{
    public static T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes beside the target, then moves over it. With <paramref name="ownerOnly"/> the file is
    /// readable by the server's account alone (on Linux and macOS), for anything secret.
    /// </summary>
    public static void Write(string path, object value, bool ownerOnly = false)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            if (ownerOnly)
            {
                RestrictToOwner(temp);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
