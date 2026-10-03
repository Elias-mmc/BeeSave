using System.IO;

namespace MinecraftSaves;

internal static class WorldFileTree
{
    public static bool IsInternalStagingDirectory(string name) =>
        name.StartsWith(".minecraft-saves-restore-", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<FileSystemInfo> EnumerateEntries(string root)
    {
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));

        while (directories.TryPop(out var currentDirectory))
        {
            foreach (var entry in currentDirectory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    if (directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        IsInternalStagingDirectory(directory.Name))
                    {
                        continue;
                    }

                    yield return directory;
                    directories.Push(directory);
                    continue;
                }

                yield return entry;
            }
        }
    }
}
