using System.Reflection;
using Microsoft.Win32;

namespace MinecraftSaves;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MinecraftSaves";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir la configuración de inicio de Windows.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo determinar la ruta de inicio de la aplicación.");
        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? throw new InvalidOperationException("No se pudo determinar el nombre de la aplicación.");
        var command = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{processPath}\" \"{Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")}\" --background"
            : $"\"{processPath}\" --background";
        key.SetValue(ValueName, command);
    }
}
