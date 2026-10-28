using System.Text.Json;

namespace DesktopDock.Core;

// Callers retain ownership of directory creation and error/recovery policy.
public static class AtomicJson
{
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, overwrite: true);
    }
}
