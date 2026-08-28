using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2BestChoice.Core.Data;

public sealed record CardUpgradeDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cost")] string Cost,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("star_cost")] int? StarCost = null);

public sealed record CardDataDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("wiki_id")] string WikiId,
    [property: JsonPropertyName("name_zh")] string NameZh,
    [property: JsonPropertyName("character")] string Character,
    [property: JsonPropertyName("rarity")] string Rarity,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("cost")] string Cost,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("upgraded")] CardUpgradeDefinition? Upgraded,
    [property: JsonPropertyName("page")] string Page,
    [property: JsonPropertyName("multiplayer_only")] bool MultiplayerOnly,
    [property: JsonPropertyName("star_cost")] int? StarCost = null);

public sealed record CardDatabaseDocument(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("compatible_through")] string CompatibleThrough,
    [property: JsonPropertyName("source_url")] string SourceUrl,
    [property: JsonPropertyName("source_page_revision")] long SourcePageRevision,
    [property: JsonPropertyName("source_data_revision")] long SourceDataRevision,
    [property: JsonPropertyName("captured_at_utc")] string CapturedAtUtc,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("cards")] CardDataDefinition[] Cards,
    [property: JsonPropertyName("branch")] string Branch = "unknown",
    [property: JsonPropertyName("reconstructability")] string Reconstructability = "unknown",
    [property: JsonPropertyName("source_sha1")] string? SourceSha1 = null);

public sealed record ResolvedCardData(
    string Id,
    string NameZh,
    string Character,
    string Rarity,
    string Type,
    string Cost,
    string Description,
    bool IsUpgraded,
    bool MultiplayerOnly,
    string DataVersion,
    long SourceRevision,
    string Branch,
    string Reconstructability,
    int? StarCost = null);

public sealed class VersionedCardDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CardDatabaseDocument[] _documents;
    private readonly Dictionary<string, Dictionary<string, CardDataDefinition>> _cardsByDocument;

    public VersionedCardDatabase(IEnumerable<CardDatabaseDocument> documents)
    {
        _documents = documents
            .OrderBy(document => ParseVersion(document.GameVersion))
            .ToArray();
        if (_documents.Length == 0)
            throw new ArgumentException("At least one card database document is required.", nameof(documents));
        _cardsByDocument = _documents.ToDictionary(
            DocumentKey,
            document => document.Cards.ToDictionary(card => NormalizeId(card.Id), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Versions => _documents.Select(static document => document.GameVersion).ToArray();

    public static CardDatabaseDocument Read(Stream stream) =>
        JsonSerializer.Deserialize<CardDatabaseDocument>(stream, JsonOptions)
        ?? throw new InvalidDataException("The card database document is empty or invalid.");

    public static VersionedCardDatabase LoadEmbedded(Assembly assembly)
    {
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(static name =>
                name.StartsWith("STS2BestChoice.CardData.", StringComparison.Ordinal) ||
                name.Contains(".data.cards.generated.", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".cards.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var documents = new List<CardDatabaseDocument>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded card database is missing: {resourceName}");
            documents.Add(Read(stream));
        }
        return new VersionedCardDatabase(documents);
    }

    public CardDatabaseDocument SelectDocument(string? runningGameVersion, string? runningBranch = null)
    {
        if (!TryParseVersion(runningGameVersion, out var running))
            return _documents[^1];

        if (string.IsNullOrWhiteSpace(runningBranch))
        {
            var exactPublic = _documents.LastOrDefault(document =>
                ParseVersion(document.GameVersion) == running &&
                document.Branch.Equals("public", StringComparison.OrdinalIgnoreCase));
            if (exactPublic is not null) return exactPublic;
        }

        var branchDocuments = string.IsNullOrWhiteSpace(runningBranch)
            ? _documents
            : _documents.Where(document => document.Branch.Equals(runningBranch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (branchDocuments.Length == 0) branchDocuments = _documents;

        var compatible = branchDocuments
            .Where(document => ParseVersion(document.GameVersion) <= running &&
                               running <= ParseVersion(document.CompatibleThrough))
            .LastOrDefault();
        if (compatible is not null) return compatible;

        return branchDocuments.LastOrDefault(document => ParseVersion(document.GameVersion) <= running)
               ?? _documents.LastOrDefault(document => ParseVersion(document.GameVersion) <= running)
               ?? _documents[0];
    }

    public bool TryResolve(
        string internalCardId,
        bool upgraded,
        string? runningGameVersion,
        string? runningBranch,
        out ResolvedCardData? resolved)
    {
        var document = SelectDocument(runningGameVersion, runningBranch);
        if (!_cardsByDocument[DocumentKey(document)].TryGetValue(NormalizeId(internalCardId), out var card))
        {
            resolved = null;
            return false;
        }

        resolved = Resolve(document, card, upgraded);
        return true;
    }

    public bool TryResolveByName(
        string name,
        bool upgraded,
        string? runningGameVersion,
        string? runningBranch,
        out ResolvedCardData? resolved)
    {
        var document = SelectDocument(runningGameVersion, runningBranch);
        var normalizedName = name.Trim().TrimEnd('+');
        var card = document.Cards.FirstOrDefault(candidate =>
            candidate.NameZh.TrimEnd('+').Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            resolved = null;
            return false;
        }
        resolved = Resolve(document, card, upgraded || name.TrimEnd().EndsWith('+'));
        return true;
    }

    private static ResolvedCardData Resolve(
        CardDatabaseDocument document,
        CardDataDefinition card,
        bool upgraded) =>
        upgraded && card.Upgraded is not null
            ? new ResolvedCardData(
                card.Upgraded.Id,
                card.NameZh + "+",
                card.Character,
                card.Rarity,
                card.Type,
                card.Upgraded.Cost,
                card.Upgraded.Description,
                true,
                card.MultiplayerOnly,
                document.GameVersion,
                document.SourceDataRevision,
                document.Branch,
                document.Reconstructability,
                card.Upgraded.StarCost)
            : new ResolvedCardData(
                card.Id,
                card.NameZh,
                card.Character,
                card.Rarity,
                card.Type,
                card.Cost,
                card.Description,
                false,
                card.MultiplayerOnly,
                document.GameVersion,
                document.SourceDataRevision,
                document.Branch,
                document.Reconstructability,
                card.StarCost);

    public bool TryResolve(
        string internalCardId,
        bool upgraded,
        string? runningGameVersion,
        out ResolvedCardData? resolved) =>
        TryResolve(internalCardId, upgraded, runningGameVersion, null, out resolved);

    private static string NormalizeId(string value) =>
        value.EndsWith("_UPGRADE", StringComparison.OrdinalIgnoreCase)
            ? value[..^"_UPGRADE".Length]
            : value.Replace('-', '_').Trim().ToUpperInvariant();

    private static string DocumentKey(CardDatabaseDocument document) =>
        $"{document.GameVersion}|{document.Branch}|{document.SourceDataRevision}";

    private static Version ParseVersion(string value) =>
        TryParseVersion(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Invalid game version in card database: {value}");

    private static bool TryParseVersion(string? value, out Version version)
    {
        var text = value?.Trim().TrimStart('v', 'V');
        var suffix = text?.IndexOfAny(['-', '+']) ?? -1;
        if (suffix >= 0) text = text![..suffix];
        return Version.TryParse(text, out version!);
    }
}
