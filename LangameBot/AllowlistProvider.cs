using System.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LangameBot;

internal sealed class AllowlistProvider : IDisposable
{
    private readonly string _path;
    private readonly IDeserializer _deserializer;
    private readonly FileSystemWatcher? _watcher;
    private HashSet<long> _allowlist = new();
    private int _reloadScheduled;

    public AllowlistProvider(string path)
    {
        _path = path;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        ReloadAllowlist();
        _watcher = TryCreateWatcher();
    }

    public bool IsAllowed(long chatId)
    {
        var snapshot = Volatile.Read(ref _allowlist);
        return snapshot.Count == 0 || snapshot.Contains(chatId);
    }

    private FileSystemWatcher? TryCreateWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            var fileName = Path.GetFileName(_path);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                return null;

            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
            };
            watcher.Changed += OnAllowlistChanged;
            watcher.Created += OnAllowlistChanged;
            watcher.Deleted += OnAllowlistChanged;
            watcher.Renamed += OnAllowlistRenamed;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Allowlist] Failed to watch {_path}: {ex.Message}");
            return null;
        }
    }

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                ReloadAllowlist();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Allowlist] Reload failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _reloadScheduled, 0);
            }
        });
    }

    private void ReloadAllowlist()
    {
        HashSet<long> loaded;
        try
        {
            loaded = LoadAllowlistFromDisk();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Allowlist] Failed to parse {_path}: {ex.Message}");
            loaded = new HashSet<long>();
        }

        var previousCount = Volatile.Read(ref _allowlist).Count;
        Volatile.Write(ref _allowlist, loaded);
        if (previousCount != loaded.Count)
            Console.WriteLine($"[Allowlist] Loaded {loaded.Count} entries from {_path}");
    }

    private HashSet<long> LoadAllowlistFromDisk()
    {
        if (!File.Exists(_path))
            return new HashSet<long>();

        var yaml = File.ReadAllText(_path);

        // Plain list format
        try
        {
            var list = _deserializer.Deserialize<List<long>>(yaml);
            if (list is { Count: > 0 })
                return new HashSet<long>(list);
        }
        catch
        {
            // ignored
        }

        // Object format
        try
        {
            var cfg = _deserializer.Deserialize<AllowlistConfig>(yaml);
            var list = cfg?.Allowlist ?? cfg?.AllowedIds ?? cfg?.Ids;
            if (list is { Count: > 0 })
                return new HashSet<long>(list);
        }
        catch
        {
            // ignored
        }

        return new HashSet<long>();
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnAllowlistChanged;
            _watcher.Created -= OnAllowlistChanged;
            _watcher.Deleted -= OnAllowlistChanged;
            _watcher.Renamed -= OnAllowlistRenamed;
            _watcher.Dispose();
        }
    }

    private void OnAllowlistChanged(object? sender, FileSystemEventArgs e) => ScheduleReload();
    private void OnAllowlistRenamed(object? sender, RenamedEventArgs e) => ScheduleReload();

    private sealed class AllowlistConfig
    {
        public List<long>? Allowlist { get; set; }
        public List<long>? AllowedIds { get; set; }
        public List<long>? Ids { get; set; }
    }
}
