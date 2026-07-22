using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

public interface IUnmatchedItemsStore
{
    Task<IReadOnlyList<UnmatchedItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(UnmatchedItem item, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string itemId, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A single TMDb-unmatchable item the matcher gave up on. Persisted to disk so the admin can audit it from the config page.
/// </summary>
public sealed class UnmatchedItem
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("ItemName")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("ItemType")]
    public string ItemType { get; set; } = string.Empty;

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("Reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("Message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("FirstSeenUtc")]
    public DateTime FirstSeenUtc { get; set; }

    [JsonPropertyName("LastSeenUtc")]
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Disk-backed store of <see cref="UnmatchedItem"/> entries. Persists to a single JSON file under the
/// plugin's data directory using atomic temp-file + rename, so concurrent writers from different scans
/// never leave a half-written JSON document on disk.
/// </summary>
public sealed class UnmatchedItemsStore : IUnmatchedItemsStore
{
    private readonly string _filePath;
    private readonly int _maxEntries;
    private readonly ILogger<UnmatchedItemsStore>? _logger;
    private readonly ConcurrentDictionary<string, UnmatchedItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public UnmatchedItemsStore(
        IApplicationPaths applicationPaths,
        UnmatchedItemsStoreOptions options,
        ILogger<UnmatchedItemsStore>? logger = null)
    {
        _maxEntries = options.MaxEntries > 0 ? options.MaxEntries : 1000;
        _logger = logger;

        // Convention: PluginConfigurationsPath is reserved for the auto-generated XML config file.
        // Runtime sidecar files belong under DataPath/<plugin>/.
        var dir = Path.Combine(applicationPaths.DataPath, "PhysicalReleaseGuard");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "unmatched.json");

        Load();
    }

    public async Task<IReadOnlyList<UnmatchedItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return _items.Values
            .OrderByDescending(i => i.LastSeenUtc)
            .ToList();
    }

    public async Task UpsertAsync(UnmatchedItem item, CancellationToken cancellationToken = default)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.ItemId))
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedItemId = NormalizeItemId(item.ItemId);
            var now = DateTime.UtcNow;
            if (_items.TryGetValue(normalizedItemId, out var existing))
            {
                existing.Reason = item.Reason;
                existing.Message = item.Message;
                existing.ItemName = item.ItemName;
                existing.ItemType = item.ItemType;
                existing.ProductionYear = item.ProductionYear;
                existing.LastSeenUtc = now;
            }
            else
            {
                _items[normalizedItemId] = new UnmatchedItem
                {
                    ItemId = normalizedItemId,
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    ProductionYear = item.ProductionYear,
                    Reason = item.Reason,
                    Message = item.Message,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                };
            }

            if (_items.Count > _maxEntries)
            {
                EvictOldest(_items.Count - _maxEntries);
            }

            await WriteSnapshotAsync(_items.Values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _items.TryRemove(NormalizeItemId(itemId), out _);
            if (removed)
            {
                await WriteSnapshotAsync(_items.Values, cancellationToken).ConfigureAwait(false);
            }
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _items.Clear();
            await WriteSnapshotAsync(_items.Values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EvictOldest(int toRemove)
    {
        var ordered = _items.Values.OrderBy(i => i.LastSeenUtc).Take(toRemove).ToList();
        foreach (var entry in ordered)
        {
            _items.TryRemove(entry.ItemId, out _);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<UnmatchedItem>>(json, JsonOptions);
            if (list is null)
            {
                return;
            }

            foreach (var entry in list)
            {
                if (!string.IsNullOrWhiteSpace(entry.ItemId))
                {
                    var normalizedItemId = NormalizeItemId(entry.ItemId);
                    entry.ItemId = normalizedItemId;
                    _items[normalizedItemId] = entry;
                }
            }

            _logger?.LogInformation(
                "UnmatchedItemsStore loaded {Count} entries from {Path}",
                _items.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load unmatched.json; starting empty.");
            _items.Clear();
        }
    }

    private async Task WriteSnapshotAsync(
        IEnumerable<UnmatchedItem> snapshot,
        CancellationToken cancellationToken)
    {
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static string NormalizeItemId(string itemId)
    {
        return itemId
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}

public sealed class UnmatchedItemsStoreOptions
{
    public int MaxEntries { get; init; } = 1000;
}
