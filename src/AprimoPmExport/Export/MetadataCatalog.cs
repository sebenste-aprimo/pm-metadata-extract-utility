using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>One field as described by <c>GET /metadata/{resourceName}</c>.</summary>
public sealed record MetadataField(
    string FieldName,
    string? EntityFieldName,
    int? DataType,
    int? FieldType,
    int? AttributeId,
    string? SelectionListName,
    int? LookupListId,
    string? SystemTypeName,
    bool IsSecure,
    bool IsReadOnly,
    bool Inactive,
    string? TitleResourceId)
{
    /// <summary>
    /// True when this describes one of the object's own schema properties rather than a
    /// tenant-defined extended attribute.
    ///
    /// <para>Two signals, either of which is sufficient. The <c>fieldName</c> of a system
    /// field is qualified with the object (<c>Project.ProjectId</c>), which an attribute
    /// name never is; and its <c>amsAttributeId</c> is <b>negative</b>, a sentinel for
    /// "not a real attribute row". Observed on the tenant this was developed against, where all 35 fields
    /// of <c>/metadata/project</c> came back this way.</para>
    ///
    /// <para>This distinction is what decides whether a generated mapping should read
    /// the property (<c>prop:projectId</c>) or the attribute array (<c>ea:…</c>) — and
    /// getting it wrong produces a column that silently resolves to nothing.</para>
    /// </summary>
    public bool IsSystemField =>
        AttributeId is < 0 || FieldName.Contains('.');

    /// <summary>
    /// The object's own property name, for a system field: <c>Project.ProjectId</c>
    /// becomes <c>projectId</c>. Null when this is an extended attribute.
    /// </summary>
    public string? PropertyName
    {
        get
        {
            if (!IsSystemField) return null;

            if (!string.IsNullOrWhiteSpace(EntityFieldName))
                return Configuration.FieldListLoader.ToCamelCase(EntityFieldName!);

            var dot = FieldName.LastIndexOf('.');
            var bare = dot >= 0 ? FieldName.Substring(dot + 1) : FieldName;

            return bare.Length == 0 ? null : Configuration.FieldListLoader.ToCamelCase(bare);
        }
    }

    /// <summary>True when the field's value is a code into some list rather than the value itself.</summary>
    public bool HasLookup =>
        !string.IsNullOrWhiteSpace(SelectionListName) ||
        !string.IsNullOrWhiteSpace(SystemTypeName) ||
        LookupListId is > 0;

    /// <summary>
    /// Best guess at a display name.
    ///
    /// <para><c>titleResourceId</c> is a resource key, not a label — PM localises through
    /// a resource table the REST surface does not expose — so it is only used when there
    /// is nothing better. <c>fieldName</c> is the value that actually matters, because it
    /// is what a mapping path has to reference.</para>
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FieldName) ? FieldName
        : !string.IsNullOrWhiteSpace(EntityFieldName) ? EntityFieldName!
        : TitleResourceId ?? "(unnamed)";
}

/// <summary>The metadata definition of one object.</summary>
public sealed record ResourceMetadata(
    string? ObjectName,
    string? ResourceType,
    bool SupportsExtendedAttributes,
    bool SupportsSecureExtendedAttributes,
    IReadOnlyList<MetadataField> Fields);

/// <summary>
/// Client for the <c>Metadata</c> endpoints.
///
/// <para><c>GET /metadata/{resourceName}</c> is this utility's counterpart to the DAM
/// API's <c>GET /fielddefinitions</c>: it names every field an object exposes on a given
/// tenant, which is what makes <c>--discover-fields</c> able to write a column list
/// without anyone typing one.</para>
///
/// <para>The spec declares no response schema for these operations — the 200 content is
/// literally empty — so the shape here is taken from the <c>IResourceMetadata</c> /
/// <c>IFieldMetadata</c> component schemas the operations plainly correspond to. Every
/// property is read defensively for that reason.</para>
/// </summary>
public sealed class MetadataCatalog
{
    private readonly ApiClient _client;
    private readonly Action<string> _log;
    private readonly Dictionary<string, ResourceMetadata?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MetadataCatalog(ApiClient client, Action<string> log)
    {
        _client = client;
        _log = log;
    }

    /// <summary>
    /// Fetches an object's metadata definition, or null when the tenant does not expose
    /// one. A missing definition is not an error: field discovery is a convenience, and
    /// several registry objects have no obvious <c>resourceName</c> counterpart.
    /// </summary>
    public async Task<ResourceMetadata?> GetAsync(PmObject pmObject, CancellationToken cancellationToken)
    {
        var resource = pmObject.MetadataResource;
        if (string.IsNullOrWhiteSpace(resource))
        {
            _log($"  {pmObject.Name}: no MetadataResource in objects.json, so there is nothing to look up.");
            return null;
        }

        if (_cache.TryGetValue(resource, out var cached)) return cached;

        using var document = await _client
            .TryGetJsonAsync($"/metadata/{Uri.EscapeDataString(resource)}",
                             $"metadata for '{resource}'", cancellationToken)
            .ConfigureAwait(false);

        var parsed = document is null ? null : Parse(document.RootElement, resource);
        _cache[resource] = parsed;
        return parsed;
    }

    private ResourceMetadata? Parse(JsonElement root, string resource)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            _log($"  metadata for '{resource}': expected an object, got {root.ValueKind}. Ignoring it.");
            return null;
        }

        var fields = new List<MetadataField>();

        if (root.TryGetProperty("fields", out var fieldArray) &&
            fieldArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldArray.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(field, "fieldName");
                var entity = ReadString(field, "entityFieldName");

                // A field with neither name is unusable in a mapping path.
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(entity)) continue;

                fields.Add(new MetadataField(
                    FieldName: name ?? "",
                    EntityFieldName: entity,
                    DataType: ReadInt(field, "fieldDataType"),
                    FieldType: ReadInt(field, "fieldType"),
                    AttributeId: ReadInt(field, "amsAttributeId"),
                    SelectionListName: ReadString(field, "selectionListName"),
                    LookupListId: ReadInt(field, "lookupListId"),
                    SystemTypeName: ReadString(field, "systemTypeName"),
                    IsSecure: ReadBool(field, "isSecure"),
                    IsReadOnly: ReadBool(field, "isReadOnly"),
                    Inactive: ReadBool(field, "inactive"),
                    TitleResourceId: ReadString(field, "titleResourceId")));
            }
        }

        return new ResourceMetadata(
            ObjectName: ReadString(root, "objectName"),
            ResourceType: ReadString(root, "resourceType"),
            SupportsExtendedAttributes: ReadBool(root, "supportsEAs"),
            SupportsSecureExtendedAttributes: ReadBool(root, "supportsSecureEAs"),
            Fields: fields);
    }

    /// <summary>
    /// Fetches a named selection list.
    ///
    /// <para>Worth knowing about because a multi-value extended attribute exports as
    /// <c>encodedListItemId</c> — an integer code, not a label. The label lives in the
    /// selection list, and this is the only endpoint that returns one.</para>
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Value)>> GetSelectionListAsync(
        string listName,
        CancellationToken cancellationToken)
    {
        using var document = await _client
            .TryGetJsonAsync($"/metadata/selection-lists/{Uri.EscapeDataString(listName)}",
                             $"selection list '{listName}'", cancellationToken)
            .ConfigureAwait(false);

        if (document is null) return Array.Empty<(string, string)>();

        var items = new List<(string, string)>();
        var root = document.RootElement;

        // The spec declares no schema, so accept a bare array or any single array
        // property on an envelope.
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject()
                      .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                      .Select(p => p.Value)
                      .FirstOrDefault()
                : default;

        if (array.ValueKind != JsonValueKind.Array) return items;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = ReadString(item, "id")
                     ?? ReadString(item, "itemId")
                     ?? ReadString(item, "encodedListItemId")
                     ?? ReadNumberAsString(item, "id")
                     ?? ReadNumberAsString(item, "encodedListItemId")
                     ?? "";

            var value = ReadString(item, "value")
                        ?? ReadString(item, "name")
                        ?? ReadString(item, "title")
                        ?? ReadString(item, "displayValue")
                        ?? "";

            if (id.Length > 0 || value.Length > 0) items.Add((id, value));
        }

        return items;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNumberAsString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
