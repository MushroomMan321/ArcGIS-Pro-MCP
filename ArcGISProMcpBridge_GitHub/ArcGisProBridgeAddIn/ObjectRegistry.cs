using System.IO;

namespace ArcGisProBridgeAddIn;

internal sealed class SessionObjectRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _idsByStableKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistryObject> _objectsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegistryObject> _artifactsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nextOrdinalByKind = new(StringComparer.OrdinalIgnoreCase);

    public string GetOrCreateId(string kind, string stableKey)
    {
        var scopedKey = $"{kind}:{stableKey}";
        lock (_lock)
        {
            if (_idsByStableKey.TryGetValue(scopedKey, out var existing))
            {
                return existing;
            }

            var normalizedKind = NormalizeKind(kind);
            var nextOrdinal = _nextOrdinalByKind.TryGetValue(normalizedKind, out var current)
                ? current + 1
                : 1;
            _nextOrdinalByKind[normalizedKind] = nextOrdinal;

            var id = $"{normalizedKind}_{nextOrdinal:000000}";
            _idsByStableKey[scopedKey] = id;
            return id;
        }
    }

    public RegistrySnapshot ReplaceLiveObjects(IEnumerable<RegistryObject> liveObjects)
    {
        lock (_lock)
        {
            _objectsById.Clear();
            foreach (var item in liveObjects.Concat(_artifactsById.Values))
            {
                _objectsById[item.Id] = item;
            }

            return CreateSnapshotNoLock();
        }
    }

    public RegistryObject RegisterArtifact(
        string path,
        string mimeType,
        string? displayName = null,
        string? parentId = null,
        string? parentKind = null,
        string? parentName = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var fullPath = Path.GetFullPath(path);
        var id = GetOrCreateId("artifact", fullPath);
        var artifact = new RegistryObject(
            Id: id,
            Kind: "artifact",
            DisplayName: displayName ?? Path.GetFileName(fullPath),
            Type: mimeType,
            Uri: $"arcgispro://artifact/{Uri.EscapeDataString(id)}",
            Path: fullPath,
            DataSource: fullPath,
            ParentId: parentId,
            ParentKind: parentKind,
            ParentName: parentName,
            StableKey: fullPath,
            Properties: properties ?? new Dictionary<string, object?>());

        lock (_lock)
        {
            _artifactsById[id] = artifact;
            _objectsById[id] = artifact;
        }

        return artifact;
    }

    public RegistryObject? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_lock)
        {
            return _objectsById.TryGetValue(id, out var item)
                ? item
                : null;
        }
    }

    public RegistrySnapshot Snapshot()
    {
        lock (_lock)
        {
            return CreateSnapshotNoLock();
        }
    }

    private RegistrySnapshot CreateSnapshotNoLock()
    {
        var objects = _objectsById.Values
            .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RegistrySnapshot(
            Objects: objects,
            Count: objects.Length,
            RefreshedUtc: DateTimeOffset.UtcNow);
    }

    private static string NormalizeKind(string kind)
    {
        var value = string.IsNullOrWhiteSpace(kind)
            ? "object"
            : kind.Trim();
        return new string(value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray());
    }
}

internal sealed record RegistrySnapshot(
    IReadOnlyList<RegistryObject> Objects,
    int Count,
    DateTimeOffset RefreshedUtc);

internal sealed record RegistryObject(
    string Id,
    string Kind,
    string DisplayName,
    string Type,
    string? Uri,
    string? Path,
    string? DataSource,
    string? ParentId,
    string? ParentKind,
    string? ParentName,
    string StableKey,
    IReadOnlyDictionary<string, object?> Properties);
