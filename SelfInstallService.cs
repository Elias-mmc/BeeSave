using System.Diagnostics;
using System.Security.Cryptography;

namespace MinecraftSaves;

internal static class SelfInstallService
{
    public static bool StartInstalledCopyIfNeeded(string[] arguments)
    {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) ||
            !Path.GetFileName(currentExecutable).Equals(
                "MinecraftSaves-Windows-x64.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "MinecraftSaves");
        Directory.CreateDirectory(installDirectory);
        var installedExecutable = Path.Combine(installDirectory, "MinecraftSaves.exe");
        if (Path.GetFullPath(currentExecutable).Equals(
                Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(installedExecutable))
        {
            var sameVersion = FilesHaveSameHash(currentExecutable, installedExecutable);
            if (IsInstalledCopyRunning(installedExecutable))
            {
                System.Windows.MessageBox.Show(
                    sameVersion
                        ? "Minecraft Saves ya está abierto, quizá escondido junto al reloj. Busca su icono para volver a la ventana."
                        : "Minecraft Saves sigue cuidando tus mundos en segundo plano. Cuando termines de jugar, elige «Salir» junto al reloj y vuelve a abrir este archivo para actualizar sin interrumpir tu partida.",
                    "Minecraft Saves",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return true;
            }

            if (!sameVersion)
            {
                InstallOrUpdate(currentExecutable, installedExecutable, installDirectory);
            }
        }
        else
        {
            InstallOrUpdate(currentExecutable, installedExecutable, installDirectory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installedExecutable,
            WorkingDirectory = installDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar la aplicación instalada.");
        return true;
    }

    private static bool FilesHaveSameHash(string leftPath, string rightPath)
    {
        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(left),
            SHA256.HashData(right));
    }

    private static bool IsInstalledCopyRunning(string installedExecutable)
    {
        foreach (var process in Process.GetProcessesByName("MinecraftSaves"))
        {
            using (process)
            {
                if (process.MainModule?.FileName is { } processPath &&
                    Path.GetFullPath(processPath).Equals(
                        Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void InstallOrUpdate(
        string sourceExecutable,
        string installedExecutable,
        string installDirectory)
    {
        var stagedExecutable = Path.Combine(installDirectory, $".install-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(sourceExecutable, stagedExecutable);
            File.Move(stagedExecutable, installedExecutable, overwrite: true);
        }
        finally
        {
            if (File.Exists(stagedExecutable))
            {
                File.Delete(stagedExecutable);
            }
        }
    }
}
