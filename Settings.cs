using System.Text.Json;

namespace MinecraftSaves;

internal sealed class AppSettings
{
    public string SavesDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ".minecraft",
        "saves");

    public string RepositoryName { get; set; } = $"minecraft-saves-{Sanitize(Environment.UserName)}";

    public bool StartWithWindows { get; set; } = true;

    public List<string> TrackedWorlds { get; set; } = [];

    private static string Sanitize(string value)
    {
        var result = new string(value.ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "player" : result;
    }
}

internal static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MinecraftSaves");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string RepositoryDirectory => Path.Combine(DataDirectory, "WorldBackups");
    public static string ToolsDirectory => Path.Combine(DataDirectory, "tools");
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (!File.Exists(AppPaths.SettingsFile))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(settings.SavesDirectory) ||
                string.IsNullOrWhiteSpace(settings.RepositoryName) ||
                settings.TrackedWorlds is null)
            {
                throw new InvalidDataException("La configuración contiene una ruta o una lista de mundos no válidas.");
            }

            if (settings.TrackedWorlds.Any(world =>
                    string.IsNullOrWhiteSpace(world) ||
                    world is "." or ".." ||
                    !Path.GetFileName(world).Equals(world, StringComparison.Ordinal) ||
                    world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException("La configuración contiene un nombre de mundo no válido.");
            }

            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"No se pudo leer la configuración {AppPaths.SettingsFile}.", exception);
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}
