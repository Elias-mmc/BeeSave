using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftSaves;

internal sealed class GameMonitor : IDisposable
{
    private readonly string _savesDirectory;
    private readonly object _stateLock = new();
    private readonly HashSet<string> _knownWorlds;
    private readonly HashSet<string> _ignoredWorlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openWorlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _awaitingFirstSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedFirstSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baselineFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<string>? NewWorldDetected;
    public event EventHandler<IReadOnlyList<string>>? GameSessionEnded;

    public GameMonitor(string savesDirectory, IEnumerable<string> trackedWorlds)
    {
        _savesDirectory = savesDirectory;
        _knownWorlds = new HashSet<string>(
            trackedWorlds.Where(world => !WorldFileTree.IsInternalStagingDirectory(world)),
            StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_savesDirectory))
        {
            foreach (var world in _knownWorlds)
            {
                var path = Path.Combine(_savesDirectory, world);
                if (Directory.Exists(path))
                {
                    _baselineFingerprints[world] = GetFingerprint(path);
                    if (IsWorldOpen(path))
                    {
                        _openWorlds.Add(world);
                    }
                }
            }

            _watcher = new FileSystemWatcher(_savesDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            _watcher.Created += OnCreated;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Poll()
    {
        var changedWorlds = new List<string>();
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var world in _knownWorlds)
            {
                var path = Path.Combine(_savesDirectory, world);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                var isOpen = IsWorldOpen(path);
                var wasOpen = _openWorlds.Contains(world);
                if (isOpen)
                {
                    _openWorlds.Add(world);
                    if (_awaitingFirstSession.Contains(world))
                    {
                        _observedFirstSession.Add(world);
                    }

                    continue;
                }

                _openWorlds.Remove(world);
                var firstSessionJustClosed = _awaitingFirstSession.Contains(world) &&
                                             _observedFirstSession.Remove(world);
                if (!wasOpen && !firstSessionJustClosed)
                {
                    continue;
                }

                var fingerprint = GetFingerprint(path);
                if (!_baselineFingerprints.TryGetValue(world, out var baseline) ||
                    !string.Equals(fingerprint, baseline, StringComparison.Ordinal))
                {
                    changedWorlds.Add(world);
                    _baselineFingerprints[world] = fingerprint;
                }

                _awaitingFirstSession.Remove(world);
                _observedFirstSession.Remove(world);
            }
        }

        if (changedWorlds.Count > 0)
        {
            GameSessionEnded?.Invoke(this, changedWorlds);
        }
    }

    public bool TrackWorld(
        string worldName,
        bool needsInitialBackup = false,
        bool waitForFirstSessionToClose = false)
    {
        lock (_stateLock)
        {
            _knownWorlds.Add(worldName);
            var path = Path.Combine(_savesDirectory, worldName);
            if (Directory.Exists(path))
            {
                var isOpen = IsWorldOpen(path);
                _baselineFingerprints[worldName] = needsInitialBackup ? string.Empty : GetFingerprint(path);
                if (isOpen)
                {
                    _openWorlds.Add(worldName);
                }

                if (needsInitialBackup && waitForFirstSessionToClose)
                {
                    _awaitingFirstSession.Add(worldName);
                    if (isOpen)
                    {
                        _observedFirstSession.Add(worldName);
                    }
                }

                return isOpen;
            }

            return false;
        }
    }

    public void UntrackWorld(string worldName)
    {
        lock (_stateLock)
        {
            _knownWorlds.Remove(worldName);
            _openWorlds.Remove(worldName);
            _awaitingFirstSession.Remove(worldName);
            _observedFirstSession.Remove(worldName);
            _baselineFingerprints.Remove(worldName);
        }
    }

    public void IgnoreWorld(string worldName)
    {
        lock (_stateLock)
        {
            _ignoredWorlds.Add(worldName);
        }
    }

    public void MarkWorldBackedUp(string worldName)
    {
        lock (_stateLock)
        {
            var path = Path.Combine(_savesDirectory, worldName);
            if (Directory.Exists(path))
            {
                _baselineFingerprints[worldName] = GetFingerprint(path);
            }

            _awaitingFirstSession.Remove(worldName);
            _observedFirstSession.Remove(worldName);
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs eventArgs)
    {
        if (!Directory.Exists(eventArgs.FullPath))
        {
            return;
        }

        var worldName = Path.GetFileName(eventArgs.FullPath);
        lock (_stateLock)
        {
            if (WorldFileTree.IsInternalStagingDirectory(worldName) ||
                _knownWorlds.Contains(worldName) || _ignoredWorlds.Contains(worldName))
            {
                return;
            }
        }

        NewWorldDetected?.Invoke(this, worldName);
    }

    private static bool IsWorldOpen(string worldDirectory)
    {
        var lockFile = Path.Combine(worldDirectory, "session.lock");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(0, 1);
            stream.Unlock(0, 1);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string GetFingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in WorldFileTree.EnumerateEntries(directory)
                     .OfType<FileInfo>()
                     .Where(file => !file.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(directory, file.FullName);
            var fingerprintEntry =
                Encoding.UTF8.GetBytes($"{relativePath}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\n");
            hash.AppendData(fingerprintEntry);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.Created -= OnCreated;
            _watcher.Dispose();
        }
    }
}
