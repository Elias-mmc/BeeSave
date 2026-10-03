using System.Diagnostics;
using System.Formats.Tar;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace MinecraftSaves;

internal sealed class BackupService
{
    private readonly string _repositoryDirectory;
    private readonly SemaphoreSlim _repositoryGate = new(1, 1);

    public BackupService(string repositoryDirectory)
    {
        _repositoryDirectory = repositoryDirectory;
    }

    public async Task<bool> IsGitHubAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("gh", ["auth", "status", "--hostname", "github.com"],
            cancellationToken, throwOnError: false);
        return result.ExitCode == 0;
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync("gh",
            ["auth", "login", "--web", "--git-protocol", "https", "--hostname", "github.com"],
            cancellationToken, useShell: true);
        await RunAsync("gh", ["auth", "setup-git"], cancellationToken);
    }

    public async Task CreatePrivateRepositoryAsync(
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryName) ||
            repositoryName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new InvalidOperationException("El nombre del repositorio solo puede contener letras, números y guiones.");
        }

        await _repositoryGate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsGitHubAuthenticatedAsync(cancellationToken))
            {
                throw new InvalidOperationException("Primero inicia sesión en GitHub.");
            }

            Directory.CreateDirectory(_repositoryDirectory);
            if (!Directory.Exists(Path.Combine(_repositoryDirectory, ".git")))
            {
                await RunAsync("git", ["init", "--initial-branch=main"], cancellationToken,
                    workingDirectory: _repositoryDirectory);
                await RunAsync("git", ["config", "user.name", "Minecraft Saves"], cancellationToken,
                    workingDirectory: _repositoryDirectory);
                await RunAsync("git", ["config", "user.email", "minecraft-saves@users.noreply.github.com"],
                    cancellationToken, workingDirectory: _repositoryDirectory);

                var readme = "# Minecraft world backups\r\n\r\nPrivate backups managed by Minecraft Saves.\r\n";
                await File.WriteAllTextAsync(Path.Combine(_repositoryDirectory, "README.md"), readme,
                    cancellationToken);
                await RunAsync("git", ["add", "README.md"], cancellationToken,
                    workingDirectory: _repositoryDirectory);
                await RunAsync("git", ["commit", "-m", "Initialize Minecraft world backups"],
                    cancellationToken, workingDirectory: _repositoryDirectory);
            }

            var remote = await RunAsync("git", ["remote", "get-url", "origin"], cancellationToken,
                workingDirectory: _repositoryDirectory, throwOnError: false);
            if (remote.ExitCode == 0)
            {
                var privacy = await RunAsync("gh", ["repo", "view", "--json", "isPrivate", "--jq", ".isPrivate"],
                    cancellationToken, workingDirectory: _repositoryDirectory);
                if (!privacy.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "El repositorio configurado no es privado. No se subirán mundos a un repositorio público.");
                }

                return;
            }

            await RunAsync("gh",
                ["repo", "create", repositoryName, "--private", "--source", _repositoryDirectory,
                    "--remote", "origin", "--push"],
                cancellationToken);
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    internal static string ResolveExecutable(string name)
    {
        var executable = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var toolPath = name.Equals("git", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppPaths.ToolsDirectory, "git", "cmd", executable)
            : Path.Combine(AppPaths.ToolsDirectory, executable);
        if (File.Exists(toolPath))
        {
            return toolPath;
        }

        var adjacentToolPath = name.Equals("git", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "tools", "git", "cmd", executable)
            : Path.Combine(AppContext.BaseDirectory, "tools", executable);
        if (File.Exists(adjacentToolPath))
        {
            return adjacentToolPath;
        }

        var installedGhPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GitHub CLI",
            executable);
        return name.Equals("gh", StringComparison.OrdinalIgnoreCase) && File.Exists(installedGhPath)
            ? installedGhPath
            : name;
    }

    private static void AddBundledGitToPath(ProcessStartInfo startInfo)
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        var gitDirectories = new[]
        {
            AppPaths.ToolsDirectory,
            Path.Combine(AppPaths.ToolsDirectory, "git", "cmd"),
            Path.Combine(AppPaths.ToolsDirectory, "git", "bin"),
            Path.Combine(AppPaths.ToolsDirectory, "git", "ucrt64", "bin"),
            Path.Combine(AppPaths.ToolsDirectory, "git", "usr", "bin"),
            Path.Combine(toolsDirectory, "git", "cmd"),
            Path.Combine(toolsDirectory, "git", "bin"),
            Path.Combine(toolsDirectory, "git", "ucrt64", "bin"),
            Path.Combine(toolsDirectory, "git", "usr", "bin")
        };
        var existingDirectories = gitDirectories.Where(Directory.Exists).ToArray();
        if (existingDirectories.Length == 0)
        {
            return;
        }

        var path = startInfo.Environment.TryGetValue("PATH", out var currentPath)
            ? currentPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator,
            existingDirectories.Append(path));
    }

    public async Task SaveWorldAsync(
        string sourceDirectory,
        string worldName,
        DateTime savedAt,
        CancellationToken cancellationToken = default)
    {
        if (worldName is "." or ".." ||
            !Path.GetFileName(worldName).Equals(worldName, StringComparison.Ordinal) ||
            worldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("El nombre del mundo no es válido para crear el respaldo.");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"No se encuentra el mundo: {sourceDirectory}");
        }

        await _repositoryGate.WaitAsync(cancellationToken);
        try
        {
            var destinationDirectory = Path.Combine(_repositoryDirectory, "Worlds", worldName);
            var worldsRoot = Path.GetFullPath(Path.Combine(_repositoryDirectory, "Worlds")) +
                             Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(destinationDirectory).StartsWith(worldsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("La ruta de destino del mundo no es válida.");
            }

            var stagingDirectory = Path.Combine(
                _repositoryDirectory, $".backup-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                CopyWorldFiles(sourceDirectory, stagingDirectory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);
                string? previousDirectory = null;
                if (Directory.Exists(destinationDirectory))
                {
                    previousDirectory = destinationDirectory + $".previous-{Guid.NewGuid():N}";
                    Directory.Move(destinationDirectory, previousDirectory);
                }

                try
                {
                    Directory.Move(stagingDirectory, destinationDirectory);
                    if (previousDirectory is not null)
                    {
                        DeleteDirectoryTree(previousDirectory);
                    }
                }
                catch
                {
                    if (!Directory.Exists(destinationDirectory) &&
                        previousDirectory is not null &&
                        Directory.Exists(previousDirectory))
                    {
                        Directory.Move(previousDirectory, destinationDirectory);
                    }

                    throw;
                }
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    DeleteDirectoryTree(stagingDirectory);
                }
            }

            var indexEntry = await RunAsync("git", ["ls-files", "--stage", "--", "Worlds/" + worldName],
                cancellationToken, workingDirectory: _repositoryDirectory);
            if (indexEntry.Output.StartsWith("160000 ", StringComparison.Ordinal))
            {
                await RunAsync("git", ["rm", "--cached", "--force", "--", "Worlds/" + worldName],
                    cancellationToken, workingDirectory: _repositoryDirectory);
            }

            await RunAsync("git", ["add", "--all", "--force", "--", "Worlds/" + worldName], cancellationToken,
                workingDirectory: _repositoryDirectory);
            var stagedChanges = await RunAsync("git", ["diff", "--cached", "--quiet"], cancellationToken,
                workingDirectory: _repositoryDirectory, throwOnError: false);
            if (stagedChanges.ExitCode == 1)
            {
                var commitName = $"{worldName} - {savedAt:yyyy-MM-dd HH-mm}";
                await RunAsync("git", ["commit", "-m", commitName], cancellationToken,
                    workingDirectory: _repositoryDirectory);
            }
            else if (stagedChanges.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stagedChanges.Error)
                        ? $"git diff terminó con código {stagedChanges.ExitCode}."
                        : stagedChanges.Error.Trim());
            }

            await RunAsync("git", ["push", "origin", "HEAD"], cancellationToken,
                workingDirectory: _repositoryDirectory);
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    public async Task<BackupCatalog> ListBackedUpWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        await _repositoryGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRepositoryReadyAsync(cancellationToken);
            await RunAsync("git", ["fetch", "--prune", "origin"], cancellationToken,
                workingDirectory: _repositoryDirectory);
            var result = await RunAsync("git",
                ["ls-tree", "-r", "-z", "origin/main", "--", "Worlds"],
                cancellationToken, workingDirectory: _repositoryDirectory);

            var restorableWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incompleteWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = entry.IndexOf('\t');
                if (separator < 0)
                {
                    continue;
                }

                var metadata = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (metadata.Length < 2)
                {
                    continue;
                }

                var path = entry[(separator + 1)..].Replace('\\', '/');
                if (!path.StartsWith("Worlds/", StringComparison.Ordinal))
                {
                    continue;
                }

                var worldName = path["Worlds/".Length..].Split('/')[0];
                if (!IsValidWorldName(worldName))
                {
                    continue;
                }

                if (metadata[1] == "blob")
                {
                    restorableWorlds.Add(worldName);
                }
                else if (metadata[1] == "commit")
                {
                    incompleteWorlds.Add(worldName);
                }
            }

            var orderedWorlds = restorableWorlds
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var world in orderedWorlds)
            {
                var icon = await ReadRemoteIconAsync(world, cancellationToken);
                if (icon is not null)
                {
                    icons.Add(world, icon);
                }
            }

            return new BackupCatalog(
                orderedWorlds,
                incompleteWorlds.Except(restorableWorlds, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                icons);
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    public async Task DeleteCloudWorldAsync(
        string worldName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidWorldName(worldName))
        {
            throw new InvalidOperationException("El nombre del mundo guardado no es válido.");
        }

        await _repositoryGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRepositoryReadyAsync(cancellationToken);
            await RunAsync("git", ["fetch", "--prune", "origin"], cancellationToken,
                workingDirectory: _repositoryDirectory);
            await RunAsync("git", ["merge", "--ff-only", "origin/main"], cancellationToken,
                workingDirectory: _repositoryDirectory);

            var path = "Worlds/" + worldName;
            var backupEntries = await RunAsync("git", ["ls-tree", "-r", "-z", "HEAD", "--", path],
                cancellationToken, workingDirectory: _repositoryDirectory);
            if (backupEntries.Output.Length == 0)
            {
                throw new InvalidOperationException($"No se encontró la copia de «{worldName}» en la nube.");
            }

            var stagedChanges = await RunAsync("git", ["diff", "--cached", "--name-only", "-z"],
                cancellationToken, workingDirectory: _repositoryDirectory);
            if (stagedChanges.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(stagedPath => !stagedPath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                                   !stagedPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Hay otros cambios preparados en Git. Guarda o descarta esos cambios antes de eliminar una copia de la nube.");
            }

            await RunAsync("git", ["rm", "--cached", "--force", "-r", "--", path], cancellationToken,
                workingDirectory: _repositoryDirectory);
            await RunAsync("git",
                ["commit", "-m", $"Eliminar copia de la nube: {worldName}"],
                cancellationToken, workingDirectory: _repositoryDirectory);
            await RunAsync("git", ["push", "origin", "HEAD"], cancellationToken,
                workingDirectory: _repositoryDirectory);
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    public static byte[]? ReadLocalWorldIcon(string worldDirectory)
    {
        var iconPath = Path.Combine(worldDirectory, "icon.png");
        if (!File.Exists(iconPath))
        {
            return null;
        }

        if (new FileInfo(iconPath).Length > 1024 * 1024)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(iconPath);
        return IsPng(bytes) ? bytes : null;
    }

    public static byte[]? CreateWorldPlaceholder(string worldDirectory)
    {
        var levelPath = Path.Combine(worldDirectory, "level.dat");
        if (!File.Exists(levelPath))
        {
            return null;
        }

        using var file = File.OpenRead(levelPath);
        using var reader = new BinaryReader(file);
        if (reader.ReadByte() != 10 || reader.ReadUInt16() != 0)
        {
            return null;
        }

        while (file.Position < file.Length)
        {
            var type = reader.ReadByte();
            if (type == 0)
            {
                return null;
            }

            var nameLength = reader.ReadUInt16();
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength)
            {
                return null;
            }

            var name = Encoding.UTF8.GetString(nameBytes);
            switch (type)
            {
                case 1:
                    if (name == "Version")
                    {
                        ReadNamedCompound(reader);
                    }
                    else
                    {
                        file.Position += 1;
                    }
                    break;
                case 2:
                    file.Position += 2;
                    break;
                case 3:
                case 5:
                    file.Position += 4;
                    break;
                case 4:
                case 6:
                    file.Position += 8;
                    break;
                case 7:
                    file.Position += ReadCollectionLength(reader);
                    break;
                case 8:
                    file.Position += reader.ReadUInt16();
                    break;
                case 9:
                case 11:
                case 12:
                    file.Position += ReadCollectionLength(reader);
                    break;
                case 10:
                    if (name == "Data")
                    {
                        return ReadDataCompound(reader);
                    }

                    SkipCompound(reader, file);
                    break;
                default:
                    return null;
            }

            if (file.Position > file.Length)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<byte[]?> ReadRemoteIconAsync(
        string worldName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable("git"),
            WorkingDirectory = _repositoryDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        AddBundledGitToPath(startInfo);
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add($"origin/main:Worlds/{worldName}/icon.png");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "No se pudo iniciar Git para leer el icono del mundo.", exception);
        }

        using var content = new MemoryStream();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var buffer = new byte[81920];
        while (true)
        {
            var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (content.Length + bytesRead > 1024 * 1024)
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidDataException("El icono del mundo supera el tamaño permitido de 1 MB.");
            }

            await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            if (error.Contains("does not exist in", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "No se pudo leer el icono guardado en GitHub."
                    : error.Trim());
        }

        var icon = content.ToArray();
        return IsPng(icon) ? icon : null;
    }

    private static bool IsPng(byte[] image) =>
        image.Length >= 24 &&
        image[0] == 137 &&
        image[1] == 80 &&
        image[2] == 78 &&
        image[3] == 71 &&
        image[4] == 13 &&
        image[5] == 10 &&
        image[6] == 26 &&
        image[7] == 10;

    private static byte[]? ReadDataCompound(BinaryReader reader)
    {
        byte[]? iconBytes = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var type = reader.ReadByte();
            if (type == 0)
            {
                return iconBytes is null ? null : CreatePlayerHeadIcon(iconBytes);
            }

            var nameLength = reader.ReadUInt16();
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength)
            {
                return null;
            }

            var name = Encoding.UTF8.GetString(nameBytes);
            if (name is "Player" or "WorldGenSettings" or "Version")
            {
                SkipTagPayload(type, reader);
                continue;
            }

            if (name == "icon" && type == 7)
            {
                var length = reader.ReadInt32();
                if (length < 0 || length > 1024 * 1024 || reader.BaseStream.Length - reader.BaseStream.Position < length)
                {
                    return null;
                }

                iconBytes = reader.ReadBytes(length);
                continue;
            }

            SkipTagPayload(type, reader);
        }

        return null;
    }

    private static void ReadNamedCompound(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("El archivo level.dat contiene NBT no válido.");
        }

        reader.BaseStream.Position += length;
    }

    private static long ReadCollectionLength(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("El archivo level.dat contiene una lista NBT no válida.");
        }

        return length;
    }

    private static void SkipCompound(BinaryReader reader, Stream stream)
    {
        while (stream.Position < stream.Length)
        {
            var type = reader.ReadByte();
            if (type == 0)
            {
                return;
            }

            var nameLength = reader.ReadUInt16();
            stream.Position += nameLength;
            SkipTagPayload(type, reader);
        }

        throw new InvalidDataException("El archivo level.dat contiene un compound NBT incompleto.");
    }

    private static void SkipTagPayload(byte type, BinaryReader reader)
    {
        var stream = reader.BaseStream;
        switch (type)
        {
            case 1: stream.Position += 1; return;
            case 2: stream.Position += 2; return;
            case 3:
            case 5: stream.Position += 4; return;
            case 4:
            case 6: stream.Position += 8; return;
            case 7:
                stream.Position += ReadCollectionLength(reader);
                return;
            case 8:
                stream.Position += reader.ReadUInt16();
                return;
            case 9:
                var itemType = reader.ReadByte();
                var itemCount = reader.ReadInt32();
                if (itemCount < 0 || itemCount > 1_000_000)
                {
                    throw new InvalidDataException("El archivo level.dat contiene una lista NBT no válida.");
                }

                for (var index = 0; index < itemCount; index++)
                {
                    SkipListItem(itemType, reader);
                }

                return;
            case 10:
                SkipCompound(reader, stream);
                return;
            case 11:
            case 12:
                stream.Position += (long)ReadCollectionLength(reader) * 4;
                return;
            default:
                throw new InvalidDataException("El archivo level.dat contiene una etiqueta NBT no válida.");
        }
    }

    private static void SkipListItem(byte type, BinaryReader reader)
    {
        if (type == 10)
        {
            SkipCompound(reader, reader.BaseStream);
            return;
        }

        if (type == 9)
        {
            var itemType = reader.ReadByte();
            var count = reader.ReadInt32();
            if (count < 0 || count > 1_000_000)
            {
                throw new InvalidDataException("El archivo level.dat contiene una lista NBT no válida.");
            }

            for (var index = 0; index < count; index++)
            {
                SkipListItem(itemType, reader);
            }

            return;
        }

        SkipTagPayload(type, reader);
    }

    private static byte[] CreatePlayerHeadIcon(byte[] skinData)
    {
        using var source = new MemoryStream(skinData);
        using var skin = new Bitmap(source);
        var width = skin.Width;
        var height = skin.Height;
        if (width <= 0 || height <= 0 || height < 8 || width < 8)
        {
            throw new InvalidDataException("El jugador guardado contiene un skin no válido.");
        }

        using var icon = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var face = new Rectangle(width / 8, height / 8, width / 4, height / 4);
            graphics.DrawImage(skin, new Rectangle(0, 0, 64, 64), face, GraphicsUnit.Pixel);
            var overlay = new Rectangle(width / 2, height / 8, width / 4, height / 4);
            graphics.DrawImage(skin, new Rectangle(0, 0, 64, 64), overlay, GraphicsUnit.Pixel);
        }

        using var output = new MemoryStream();
        icon.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    public async Task<string> RestoreWorldAsync(
        string worldName,
        string savesDirectory,
        Action<string>? beforeRestore = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidWorldName(worldName))
        {
            throw new InvalidOperationException("El nombre del mundo guardado no es válido.");
        }

        if (!Directory.Exists(savesDirectory))
        {
            throw new DirectoryNotFoundException($"No se encuentra la carpeta de mundos: {savesDirectory}");
        }

        await _repositoryGate.WaitAsync(cancellationToken);
        var stagingDirectory = Path.Combine(savesDirectory, $".minecraft-saves-restore-{Guid.NewGuid():N}");
        try
        {
            await EnsureRepositoryReadyAsync(cancellationToken);
            await RunAsync("git", ["fetch", "--prune", "origin"], cancellationToken,
                workingDirectory: _repositoryDirectory);
            await ExtractWorldArchiveAsync(worldName, stagingDirectory, cancellationToken);

            var restoredDirectory = Path.Combine(savesDirectory, worldName);
            if (Directory.Exists(restoredDirectory))
            {
                var suffix = 1;
                do
                {
                    restoredDirectory = Path.Combine(savesDirectory, $"{worldName} (restaurado {suffix})");
                    suffix++;
                } while (Directory.Exists(restoredDirectory) || File.Exists(restoredDirectory));
            }

            cancellationToken.ThrowIfCancellationRequested();
            beforeRestore?.Invoke(Path.GetFileName(restoredDirectory));
            Directory.Move(stagingDirectory, restoredDirectory);
            return Path.GetFileName(restoredDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                DeleteDirectoryTree(stagingDirectory);
            }

            _repositoryGate.Release();
        }
    }

    private async Task EnsureRepositoryReadyAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(_repositoryDirectory, ".git")))
        {
            throw new InvalidOperationException("Primero conecta tu cuenta y crea el repositorio privado en GitHub.");
        }

        var remote = await RunAsync("git", ["remote", "get-url", "origin"], cancellationToken,
            workingDirectory: _repositoryDirectory, throwOnError: false);
        if (remote.ExitCode != 0 || string.IsNullOrWhiteSpace(remote.Output))
        {
            throw new InvalidOperationException("El repositorio de copias no tiene configurado su origen de GitHub.");
        }

        var privacy = await RunAsync("gh", ["repo", "view", "--json", "isPrivate", "--jq", ".isPrivate"],
            cancellationToken, workingDirectory: _repositoryDirectory, throwOnError: false);
        if (privacy.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(privacy.Error)
                    ? "No se pudo verificar en GitHub que el repositorio de copias sea privado."
                    : privacy.Error.Trim());
        }

        if (!privacy.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "El repositorio configurado no es privado. No se descargarán mundos desde un repositorio público.");
        }
    }

    private async Task ExtractWorldArchiveAsync(
        string worldName,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var process = StartGitArchive(worldName);
        var errorsTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var temporaryArchivePath = Path.Combine(
            AppPaths.DataDirectory, $".restore-archive-{Guid.NewGuid():N}.tar");
        try
        {
            await using (var archiveFile = new FileStream(temporaryArchivePath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await process.StandardOutput.BaseStream.CopyToAsync(archiveFile, 81920, cancellationToken);
                await archiveFile.FlushAsync(cancellationToken);
            }

            await process.WaitForExitAsync(cancellationToken);
            var errors = await errorsTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors)
                    ? $"git archive terminó con código {process.ExitCode}."
                    : errors.Trim());
            }

            var sawWorldFile = false;
            await using var archiveStream = new FileStream(temporaryArchivePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var reader = new TarReader(archiveStream);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: true, cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                {
                    continue;
                }

                var archiveEntryPath = entry.Name.Replace('\\', '/');
                var prefix = $"Worlds/{worldName}/";
                if (!archiveEntryPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("El archivo de respaldo contiene una ruta fuera del mundo solicitado.");
                }

                var relativePath = archiveEntryPath[prefix.Length..];
                var pathComponents = relativePath.Split('/');
                if (pathComponents.Length == 0 ||
                    pathComponents.Any(component => string.IsNullOrWhiteSpace(component) ||
                                                    component is "." or ".." ||
                                                    component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new InvalidDataException("El archivo de respaldo contiene una ruta de archivo no válida.");
                }

                var destination = Path.GetFullPath(Path.Combine([destinationDirectory, .. pathComponents]));
                var destinationPrefix = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("El archivo de respaldo intenta salir de la carpeta del mundo.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await entry.DataStream.CopyToAsync(output, cancellationToken);
                sawWorldFile = true;
            }

            if (!sawWorldFile)
            {
                throw new InvalidDataException(
                    $"El respaldo de «{worldName}» está vacío o no existe en el repositorio.");
            }
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporaryArchivePath))
            {
                File.Delete(temporaryArchivePath);
            }
        }
    }

    private Process StartGitArchive(string worldName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable("git"),
            WorkingDirectory = _repositoryDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        AddBundledGitToPath(startInfo);
        startInfo.ArgumentList.Add("archive");
        startInfo.ArgumentList.Add("--format=tar");
        startInfo.ArgumentList.Add("origin/main");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add($"Worlds/{worldName}");

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return process;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException(
                "No se pudo iniciar Git para leer la copia. Instala Git o coloca su carpeta «tools» junto a la aplicación.",
                exception);
        }
    }

    private static bool IsValidWorldName(string worldName) =>
        !string.IsNullOrWhiteSpace(worldName) &&
        worldName is not "." and not ".." &&
        Path.GetFileName(worldName).Equals(worldName, StringComparison.Ordinal) &&
        worldName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void CopyWorldFiles(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var entry in WorldFileTree.EnumerateEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, entry.FullName);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!entry.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(entry.FullName, destinationPath, overwrite: true);
            }
        }
    }

    private static void DeleteDirectoryTree(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            File.SetAttributes(childDirectory,
                File.GetAttributes(childDirectory) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError = true,
        string? workingDirectory = null,
        bool useShell = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(executable),
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = useShell,
            CreateNoWindow = !useShell,
            RedirectStandardOutput = !useShell,
            RedirectStandardError = !useShell
        };
        if (!useShell)
        {
            AddBundledGitToPath(startInfo);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"No se pudo iniciar '{executable}'. Instala Git y GitHub CLI (gh) y vuelve a intentarlo.",
                exception);
        }

        if (useShell)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (throwOnError && process.ExitCode != 0)
            {
                throw new InvalidOperationException($"'{executable}' terminó con código {process.ExitCode}.");
            }

            return new CommandResult(process.ExitCode, string.Empty, string.Empty);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (throwOnError && process.ExitCode != 0)
        {
            var details = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(error))
            {
                details.Append(error.Trim());
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                if (details.Length > 0) details.AppendLine();
                details.Append(output.Trim());
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details.ToString())
                    ? $"'{executable}' terminó con código {process.ExitCode}."
                    : details.ToString());
        }

        return new CommandResult(process.ExitCode, output, error);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}

internal sealed record BackupCatalog(
    IReadOnlyList<string> RestorableWorlds,
    IReadOnlyList<string> IncompleteWorlds,
    IReadOnlyDictionary<string, byte[]> Icons);
