using System.Text.Json;
using System.Text.Json.Serialization;

namespace AprimoPmExport.Configuration;

/// <summary>
/// One exportable PM object: where its collection lives, how to page it, and which
/// properties identify and date-stamp an item.
///
/// <para>This is the single biggest structural difference from the DAM utility. DAM has
/// one thing to export — a record — so its endpoint, sort key and ID property could all
/// be constants. PM has roughly two dozen unrelated collections whose schemas share
/// almost nothing, so the shape has to be data.</para>
/// </summary>
public sealed class PmObject
{
    /// <summary>Slug used on the command line and in output file names.</summary>
    public string Name { get; set; } = "";

    /// <summary>Collection endpoint, relative to the API base URL. Must start with '/'.</summary>
    public string Path { get; set; } = "";

    /// <summary>OpenAPI schema name for one item. Documentation only.</summary>
    public string? Schema { get; set; }

    /// <summary>Resource name for <c>GET /metadata/{resourceName}</c>, used by field discovery.</summary>
    public string? MetadataResource { get; set; }

    /// <summary>
    /// Key under <c>_embedded</c> holding the item array, when known.
    ///
    /// <para>The spec types <c>_embedded</c> as <c>additionalProperties</c> "keyed by the
    /// resource type name" without ever giving that string, so this stays a hint: the
    /// reader falls back to the sole array under <c>_embedded</c>. Setting it turns on a
    /// strict check instead.</para>
    /// </summary>
    public string? EmbedKey { get; set; }

    public string IdProperty { get; set; } = "id";

    /// <summary>
    /// Timestamp a delta window compares against. Null means the schema carries no
    /// modified stamp, so the object is full-export only.
    /// </summary>
    public string? ModifiedProperty { get; set; }

    public string? CreatedProperty { get; set; }

    /// <summary>Human-readable label. Used in <c>--sample</c> output only.</summary>
    public string? TitleProperty { get; set; }

    /// <summary>Single-item endpoint containing <c>{id}</c>, for <c>--record</c>. Null = none.</summary>
    public string? DetailPath { get; set; }

    /// <summary>True when the schema carries tenant-defined extended attributes.</summary>
    public bool SupportsExtendedAttributes { get; set; }

    /// <summary>
    /// True when <see cref="ModifiedProperty"/> is an epoch number rather than an ISO
    /// timestamp. Budget is the only object in the spec that does this.
    /// </summary>
    public bool ModifiedIsEpoch { get; set; }

    /// <summary>Epoch unit for <see cref="ModifiedIsEpoch"/>. Seconds are auto-detected.</summary>
    public bool ModifiedIsEpochMilliseconds { get; set; } = true;

    /// <summary>
    /// Query parameter that filters server-side on the modified date, where one exists.
    /// Only <c>/review-tasks</c> declares one in the whole spec.
    /// </summary>
    public string? ModifiedAfterParam { get; set; }

    /// <summary>
    /// True when the endpoint returns only what the authenticated user can see, rather
    /// than everything in the tenant. Surfaced as a warning, since it silently changes
    /// what "a full export" means.
    /// </summary>
    public bool ScopedToCallingUser { get; set; }

    /// <summary>Fixed query parameters appended to every request for this object.</summary>
    public Dictionary<string, string> ExtraQuery { get; set; } = new();

    /// <summary>False drops the object from <c>--all-objects</c> without deleting the entry.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True when a delta window can be applied at all.</summary>
    [JsonIgnore]
    public bool SupportsDelta => !string.IsNullOrWhiteSpace(ModifiedProperty);

    /// <summary>
    /// Default sort key. The API takes <c>sortField</c> plus a separate
    /// <c>sortAscending</c> boolean, not DAM's <c>-Field</c> convention.
    /// </summary>
    [JsonIgnore]
    public string? DefaultSortField => ModifiedProperty ?? CreatedProperty ?? IdProperty;

    /// <summary>Default field-list file name for this object.</summary>
    [JsonIgnore]
    public string FieldsFileName => Name + ".txt";

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Objects[]: 'Name' is required.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Path))
            yield return $"Object '{Name}': 'Path' is required.";
        else if (!Path.StartsWith('/'))
            yield return $"Object '{Name}': 'Path' must start with '/' (got '{Path}').";

        if (string.IsNullOrWhiteSpace(IdProperty))
            yield return $"Object '{Name}': 'IdProperty' is required.";

        if (DetailPath is { Length: > 0 } detail && !detail.Contains("{id}", StringComparison.Ordinal))
            yield return $"Object '{Name}': 'DetailPath' must contain the '{{id}}' placeholder (got '{detail}').";

        if (ModifiedIsEpoch && !SupportsDelta)
            yield return $"Object '{Name}': 'ModifiedIsEpoch' is set but 'ModifiedProperty' is null.";
    }

    /// <summary>Resolves <see cref="DetailPath"/> for one ID.</summary>
    public string? ResolveDetailPath(string id) =>
        DetailPath?.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

    public override string ToString() => $"{Name} ({Path})";
}

/// <summary>The registry loaded from <c>objects.json</c>.</summary>
public sealed class ObjectCatalog
{
    public const string DefaultFileName = "objects.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public List<PmObject> Objects { get; set; } = new();

    /// <summary>Objects included by <c>--all-objects</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<PmObject> EnabledObjects =>
        Objects.Where(o => o.Enabled).ToList();

    public PmObject? Find(string name) =>
        Objects.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a comma/space separated selection. <c>all</c> or <c>*</c> expands to every
    /// enabled object; a disabled object named explicitly is still honoured, which is the
    /// point of naming it.
    /// </summary>
    public IReadOnlyList<PmObject> Resolve(string selection)
    {
        var tokens = selection.Split(
            new[] { ',', ' ', ';', '\t' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            throw new ArgumentException("No objects selected. Use --objects <names> or --all-objects.");

        var resolved = new List<PmObject>();
        var unknown = new List<string>();

        foreach (var token in tokens)
        {
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase) || token == "*")
            {
                foreach (var o in EnabledObjects)
                    if (!resolved.Contains(o)) resolved.Add(o);
                continue;
            }

            var match = Find(token);
            if (match is null) unknown.Add(token);
            else if (!resolved.Contains(match)) resolved.Add(match);
        }

        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Unknown object name(s): {string.Join(", ", unknown)}.{Environment.NewLine}" +
                $"      Available: {string.Join(", ", Objects.Select(o => o.Name))}");

        return resolved;
    }

    public IEnumerable<string> Validate()
    {
        if (Objects.Count == 0)
            yield return "objects.json: the registry contains no objects.";

        foreach (var o in Objects)
            foreach (var e in o.Validate())
                yield return e;

        foreach (var dupe in Objects.Select(o => o.Name)
                                    .Where(n => !string.IsNullOrWhiteSpace(n))
                                    .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .Where(g => g.Count() > 1)
                                    .Select(g => g.Key))
            yield return $"objects.json: duplicate object name '{dupe}'.";
    }

    public static ObjectCatalog Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Object registry not found: '{System.IO.Path.GetFullPath(path)}'. " +
                "It ships beside the executable as objects.json.", path);

        ObjectCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<ObjectCatalog>(File.ReadAllText(path), Json)
                      ?? new ObjectCatalog();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Object registry '{System.IO.Path.GetFullPath(path)}' is not valid JSON: {ex.Message}", ex);
        }

        var errors = catalog.Validate().ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Object registry '{System.IO.Path.GetFullPath(path)}' is invalid:{Environment.NewLine}  - " +
                string.Join(Environment.NewLine + "  - ", errors));

        log($"Loaded {catalog.Objects.Count} object definition(s) from {System.IO.Path.GetFullPath(path)} " +
            $"({catalog.EnabledObjects.Count} enabled by default).");

        return catalog;
    }
}
