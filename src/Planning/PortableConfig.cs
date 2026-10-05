using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public static class PortableConfig
{
    public static string SettingsPath(string appDirectory) => Path.Combine(appDirectory, "config.json");
    public static string HistoryPath(string appDirectory) => Path.Combine(appDirectory, "trade-history.json");

    public static T Load<T>(string path, Func<T> createDefaults) where T : class =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"Configuration file is empty: {path}")
            : createDefaults();

    public static void Save<T>(string path, T settings)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
