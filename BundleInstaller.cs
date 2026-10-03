using System.IO.Compression;
using System.Reflection;

namespace MinecraftSaves;

internal static class BundleInstaller
{
    private const string VersionMarker = "2.102.0+2.56.0";
    private const string GitHubCliResource = "MinecraftSaves.Bundle.gh.exe";
    private const string MinGitResource = "MinecraftSaves.Bundle.mingit.zip";

    public static bool InstallBundledToolsIfPresent()
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("No se pudo leer el paquete de Minecraft Saves.");
        using var ghResource = assembly.GetManifestResourceStream(GitHubCliResource);
        using var gitResource = assembly.GetManifestResourceStream(MinGitResource);
        if (ghResource is null && gitResource is null)
        {
            return false;
        }

        if (ghResource is null || gitResource is null)
        {
            throw new InvalidDataException("La descarga está incompleta. Vuelve a descargar Minecraft Saves.");
        }

        var marker = Path.Combine(AppPaths.ToolsDirectory, ".bundle-version");
        var installedGit = Path.Combine(AppPaths.ToolsDirectory, "git", "cmd", "git.exe");
        var installedGh = Path.Combine(AppPaths.ToolsDirectory, "gh.exe");
        if (File.Exists(installedGit) && File.Exists(installedGh) &&
            File.Exists(marker) && File.ReadAllText(marker).Trim() == VersionMarker)
        {
            return true;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var stagingDirectory = Path.Combine(
            AppPaths.DataDirectory, $".tools-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ExtractGitHubCli(ghResource, stagingDirectory);
            ExtractMinGit(gitResource, stagingDirectory);
            File.WriteAllText(Path.Combine(stagingDirectory, ".bundle-version"), VersionMarker);

            var previousDirectory = Path.Combine(
                AppPaths.DataDirectory, $".tools-previous-{Guid.NewGuid():N}");
            var hadPrevious = Directory.Exists(AppPaths.ToolsDirectory);
            if (hadPrevious)
            {
                Directory.Move(AppPaths.ToolsDirectory, previousDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, AppPaths.ToolsDirectory);
            }
            catch
            {
                if (hadPrevious && !Directory.Exists(AppPaths.ToolsDirectory))
                {
                    Directory.Move(previousDirectory, AppPaths.ToolsDirectory);
                }

                throw;
            }

            if (hadPrevious && Directory.Exists(previousDirectory))
            {
                Directory.Delete(previousDirectory, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }

        return true;
    }

    private static void ExtractGitHubCli(Stream resource, string stagingDirectory)
    {
        var executablePath = Path.Combine(stagingDirectory, "gh.exe");
        using var executable = new FileStream(
            executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        resource.CopyTo(executable);
    }

    private static void ExtractMinGit(Stream resource, string stagingDirectory)
    {
        var gitDirectory = Path.Combine(stagingDirectory, "git");
        Directory.CreateDirectory(gitDirectory);
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            var relativePath = entry.FullName.Replace('\\', '/');
            var components = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Any(component => component is "." or ".." ||
                                            component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException("La descarga contiene una ruta de archivo no válida.");
            }

            if (components.Length == 0)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine([gitDirectory, .. components]));
            var destinationPrefix = Path.GetFullPath(gitDirectory) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("La descarga intenta salir de su carpeta de instalación.");
            }

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        if (!File.Exists(Path.Combine(gitDirectory, "cmd", "git.exe")))
        {
            throw new InvalidDataException("La descarga no contiene Git para Windows.");
        }
    }
}
