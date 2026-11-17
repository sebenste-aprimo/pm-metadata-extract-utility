using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>One list of codes and the labels they stand for.</summary>
public sealed class LookupTable
{
    private readonly Dictionary<string, string> _labels;

    /// <summary>Endpoint this came from, for diagnostics.</summary>
    public string Source { get; }

    public int Count => _labels.Count;

    public LookupTable(string source, Dictionary<string, string> labels)
    {
        Source = source;
        _labels = labels;
    }

    public bool TryGet(string code, out string label) => _labels.TryGetValue(code, out label!);
}

/// <summary>
/// Turns coded values into labels.
///
/// <para>Most numeric columns in a PM export are codes: a status of <c>4</c>, a pick-list
/// value of <c>663</c>. <c>GET /metadata/{resourceName}</c> declares, per field, which
/// list a code belongs to — so nothing here is hard-coded, and a field added to the
/// tenant next month resolves without a code change.</para>
///
/// <para><b>Built from observation, not from the spec.</b> Three of the four candidate
/// endpoints declare no response schema at all. What a live tenant actually returned, on
/// the one tenant this was developed against:</para>
///
/// <list type="table">
/// <item><term><c>/lookup/{id}</c></term><description><c>{ key, value, sequence, active }</c>
/// — <c>value</c> is a real label: a project-status list gave Draft / In Process / Paused /
/// Closed…, and a user list gave names.</description></item>
/// <item><term><c>/extended-attributes/{id}/pick-list-options</c></term>
/// <description><c>{ itemId, displayValue, sequence, active, systemName }</c> — real
/// labels for a single-valued extended attribute's pick list.</description></item>
/// <item><term><c>/metadata/selection-lists/{name}</c></term>
/// <description><c>{ itemId, resourceId, displayValue, … }</c> — but <b>often with no
/// <c>displayValue</c> at all</b>, leaving only a <c>resourceId</c> of the form
/// <c>mo-encoded-titles-NNNN</c>. That is a resource key into a table the REST surface
/// does not expose, so such a list yields no labels and is treated as unusable.</description></item>
/// </list>
///
/// <para><b>Why routes are tried in order rather than chosen.</b> A field commonly
/// declares more than one, and a declared one can 404: a field naming both a selection list
/// and a lookup list may find only the selection list served. Picking a single route per
/// field would silently leave such columns as bare codes.</para>
/// </summary>
public sealed class LookupResolver
{
    /// <summary>Lists that produced no usable labels, so they are not retried.</summary>
    private static readonly LookupTable Unresolvable = new("(none)", new Dictionary<string, string>());

    private readonly Dictionary<string, LookupTable> _byKey;

    /// <summary>Keys that could not be resolved to any list, for the run summary.</summary>
    public IReadOnlyList<string> UnresolvedKeys { get; }

    private LookupResolver(Dictionary<string, LookupTable> byKey, IReadOnlyList<string> unresolved)
    {
        _byKey = byKey;
        UnresolvedKeys = unresolved;
    }

    /// <summary>A resolver that resolves nothing, for when lookups are switched off.</summary>
    public static LookupResolver Empty { get; } =
        new(new Dictionary<string, LookupTable>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

    public bool IsEmpty => _byKey.Count == 0;

    /// <summary>Resolvable keys and where each one's labels came from.</summary>
    public IEnumerable<(string Key, string Source, int Count)> Describe() =>
        _byKey.Where(kvp => kvp.Value.Count > 0)
              .Select(kvp => (kvp.Key, kvp.Value.Source, kvp.Value.Count))
              .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves one code. Returns false when the key has no list, or the code is not in
    /// it — the caller then keeps the raw value rather than emitting a blank.
    /// </summary>
    public bool TryResolve(string key, string code, out string label)
    {
        label = "";
        if (string.IsNullOrEmpty(code)) return false;

        return _byKey.TryGetValue(key, out var table) && table.TryGet(code, out label);
    }

    /// <summary>
    /// Fetches the lists needed by <paramref name="keys"/>, one request per distinct
    /// list, and caches them for the run.
    /// </summary>
    /// <param name="keys">
    /// Lookup keys referenced by the column list — a property name such as
    /// <c>projectStatus</c>, or the name of an extended attribute.
    /// </param>
    public static async Task<LookupResolver> BuildAsync(
        ApiClient client,
        MetadataCatalog metadata,
        PmObject pmObject,
        IReadOnlyCollection<string> keys,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return Empty;

        var definition = await metadata.GetAsync(pmObject, cancellationToken).ConfigureAwait(false);

        if (definition is null)
        {
            log($"  {pmObject.Name}: no metadata definition, so coded values cannot be resolved. " +
                "Those columns will hold raw codes.");
            return Empty;
        }

        var byKey = new Dictionary<string, LookupTable>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        // One table per endpoint, shared by every field pointing at it — several fields
        // commonly share one list (three yes/no flags on a project all pointed at the same
        // lookup list), and fetching it once per field would triple the requests.
        var cache = new Dictionary<string, LookupTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var field = Match(definition, key);

            if (field is null)
            {
                unresolved.Add($"{key} (no such field in the metadata definition)");
                continue;
            }

            var table = await ResolveFieldAsync(client, field, cache, log, cancellationToken)
                .ConfigureAwait(false);

            if (table.Count == 0)
            {
                unresolved.Add($"{key} ({DescribeRoutes(field)})");
                continue;
            }

            byKey[key] = table;
        }

        if (byKey.Count > 0)
            log($"  {pmObject.Name}: resolved {byKey.Count} lookup list(s) " +
                $"({byKey.Values.Select(t => t.Source).Distinct().Count()} distinct endpoint(s)).");

        if (unresolved.Count > 0)
        {
            log($"  {pmObject.Name}: {unresolved.Count} column(s) have no usable label list, so they " +
                "keep their raw codes:");
            foreach (var item in unresolved.Take(10)) log("    " + item);
            if (unresolved.Count > 10) log($"    … and {unresolved.Count - 10} more");
        }

        return new LookupResolver(byKey, unresolved);
    }

    /// <summary>Finds the metadata field a lookup key refers to.</summary>
    private static MetadataField? Match(ResourceMetadata definition, string key) =>
        definition.Fields.FirstOrDefault(f =>
            string.Equals(f.PropertyName, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.FieldName, key, StringComparison.OrdinalIgnoreCase));

    private static string DescribeRoutes(MetadataField field)
    {
        var routes = new List<string>();

        if (field.AttributeId is > 0) routes.Add($"pick-list for attribute {field.AttributeId}");
        if (field.LookupListId is > 0) routes.Add($"lookup list {field.LookupListId}");
        if (!string.IsNullOrWhiteSpace(field.SelectionListName))
            routes.Add($"selection list '{field.SelectionListName}'");

        return routes.Count == 0
            ? "declares no list"
            : "tried " + string.Join(", ", routes);
    }

    /// <summary>
    /// Tries each route the field declares until one yields labels.
    ///
    /// <para>Order is by observed reliability: an extended attribute's own pick list is
    /// the most specific, <c>/lookup</c> returns labels whenever it is served at all, and
    /// a selection list is last because it is the one that commonly returns resource keys
    /// instead of labels.</para>
    /// </summary>
    private static async Task<LookupTable> ResolveFieldAsync(
        ApiClient client,
        MetadataField field,
        Dictionary<string, LookupTable> cache,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var routes = new List<string>();

        if (field.AttributeId is { } attributeId and > 0)
            routes.Add($"/extended-attributes/{attributeId}/pick-list-options");

        if (field.LookupListId is { } lookupId and > 0)
            routes.Add($"/lookup/{lookupId}");

        if (!string.IsNullOrWhiteSpace(field.SelectionListName))
            routes.Add($"/metadata/selection-lists/{Uri.EscapeDataString(field.SelectionListName!)}");

        foreach (var route in routes)
        {
            if (cache.TryGetValue(route, out var cached))
            {
                if (cached.Count > 0) return cached;
                continue;
            }

            var table = await FetchAsync(client, route, cancellationToken).ConfigureAwait(false);
            cache[route] = table;

            if (table.Count > 0) return table;
        }

        return Unresolvable;
    }

    private static async Task<LookupTable> FetchAsync(
        ApiClient client,
        string route,
        CancellationToken cancellationToken)
    {
        using var document = await client
            .TryGetJsonAsync(route, $"lookup {route}", cancellationToken)
            .ConfigureAwait(false);

        if (document is null) return Unresolvable;

        var items = ExtractArray(document.RootElement);
        if (items is not { ValueKind: JsonValueKind.Array } array) return Unresolvable;

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var code = ReadFirst(item, "itemId", "key", "id", "lookupValue", "encodedListItemId");
            var label = ReadFirst(item, "displayValue", "value", "description", "name", "title");

            // systemName is a fallback label, not a first choice: it is the internal name
            // where displayValue is the one users see, and the two can read differently.
            label ??= ReadFirst(item, "systemName");

            // resourceId is deliberately NOT a label candidate. It is a key into PM's
            // resource table, which this API does not expose — accepting it would fill
            // columns with strings like 'mo-encoded-titles-NNNN' that look resolved
            // but are no more readable than the code they replaced.
            if (code is null || string.IsNullOrWhiteSpace(label)) continue;

            labels[code] = label!;
        }

        return labels.Count == 0 ? Unresolvable : new LookupTable(route, labels);
    }

    /// <summary>
    /// Finds the item array. Three shapes are in play and none is documented: a bare
    /// array, a <c>{ items: [...] }</c> envelope, and the HAL envelope used elsewhere.
    /// <c>/system-types</c> adds a fourth, putting its array under a plain root key.
    /// </summary>
    private static JsonElement? ExtractArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items;

        var envelope = HalEnvelopeReader.Read(root, null);
        if (envelope.HasItems) return envelope.Items;

        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array && !property.Name.StartsWith('_'))
                return property.Value;

        return null;
    }

    private static string? ReadFirst(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                    break;

                case JsonValueKind.Number:
                    return value.GetRawText();
            }
        }

        return null;
    }
}
