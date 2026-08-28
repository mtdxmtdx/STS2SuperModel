using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Data;

public sealed class VersionedRelicDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly ImmutableDictionary<string, RelicCatalogEntry> _relicsById;
    private readonly ImmutableDictionary<string, RelicCatalogEntry> _relicsByName;

    public RelicCatalogDocument Document { get; }
    public IReadOnlyCollection<RelicCatalogEntry> AllRelics => Document.Relics;

    public VersionedRelicDatabase(RelicCatalogDocument document)
    {
        Document = document;
        var byId = ImmutableDictionary.CreateBuilder<string, RelicCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var byName = ImmutableDictionary.CreateBuilder<string, RelicCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var relic in document.Relics)
        {
            byId[relic.RelicId] = relic;
            if (!string.IsNullOrWhiteSpace(relic.CanonicalName))
                byName[relic.CanonicalName] = relic;
            if (!string.IsNullOrWhiteSpace(relic.LocalizedNameZh))
                byName[relic.LocalizedNameZh] = relic;
            foreach (var alias in relic.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    byName[alias] = relic;
            }
        }

        _relicsById = byId.ToImmutable();
        _relicsByName = byName.ToImmutable();
    }

    public static VersionedRelicDatabase LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(VersionedRelicDatabase).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        var targetResource = resourceNames.FirstOrDefault(name =>
            name.EndsWith("relics.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("relic-catalog.json", StringComparison.OrdinalIgnoreCase));

        if (targetResource is null)
        {
            throw new InvalidOperationException(
                $"Embedded relic catalog resource not found in assembly {assembly.FullName}. Available resources: {string.Join(", ", resourceNames)}");
        }

        using var stream = assembly.GetManifestResourceStream(targetResource)!;
        return Read(stream);
    }

    public static VersionedRelicDatabase Read(Stream stream)
    {
        var doc = JsonSerializer.Deserialize<RelicCatalogDocument>(stream, JsonOptions);
        if (doc is null)
            throw new InvalidOperationException("Failed to deserialize RelicCatalogDocument.");
        return new VersionedRelicDatabase(doc);
    }

    public static VersionedRelicDatabase Read(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Read(stream);
    }

    public bool TryResolve(string relicId, out RelicCatalogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(relicId)) return false;
        return _relicsById.TryGetValue(NormalizeId(relicId), out entry);
    }

    public bool TryResolveByName(string name, out RelicCatalogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (_relicsByName.TryGetValue(name.Trim(), out entry)) return true;
        return _relicsById.TryGetValue(NormalizeId(name), out entry);
    }

    private static string NormalizeId(string id) =>
        id.Trim().ToUpperInvariant();
}
