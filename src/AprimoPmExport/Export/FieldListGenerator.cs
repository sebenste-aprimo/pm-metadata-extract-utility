using System.Text;
using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>
/// Writes a per-object column list from what the tenant actually exposes.
///
/// <para>This is <c>--discover-fields</c>, and it is the intended way to set the utility
/// up: the DAM utility could ship one <c>fields.txt</c> covering the single record type,
/// but two dozen unrelated PM objects cannot be pre-written usefully for an unknown
/// tenant. Two sources are combined:</para>
///
/// <list type="number">
/// <item><c>GET /metadata/{resourceName}</c> — the authoritative field list, including
/// tenant-defined extended attributes that no schema in the spec can know about.</item>
/// <item>One sampled item — which shows the properties the collection endpoint really
/// returns, as opposed to what the metadata definition says exists.</item>
/// </list>
///
/// <para>Neither alone is enough. The metadata definition names attributes that may not
/// appear on a given item; the sample shows shape but only for whatever that item
/// happens to have filled in.</para>
/// </summary>
public sealed class FieldListGenerator
{
    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly MetadataCatalog _metadata;
    private readonly Action<string> _log;

    public FieldListGenerator(
        ExportConfig config,
        ApiClient client,
        MetadataCatalog metadata,
        Action<string> log)
    {
        _config = config;
        _client = client;
        _metadata = metadata;
        _log = log;
    }

    /// <summary>Generates and writes a column list for one object. Returns the path written, or null.</summary>
    public async Task<string?> GenerateAsync(
        PmObject pmObject,
        string directory,
        bool overwrite,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, pmObject.FieldsFileName);

        if (File.Exists(path) && !overwrite)
        {
            _log($"  {pmObject.Name}: {pmObject.FieldsFileName} already exists — skipping. " +
                 "Pass --overwrite to regenerate it.");
            return null;
        }

        var definition = await _metadata.GetAsync(pmObject, cancellationToken).ConfigureAwait(false);

        using var sampleDocument = await FetchSampleAsync(pmObject, sampleSize, cancellationToken)
            .ConfigureAwait(false);

        var sample = sampleDocument is null
            ? new List<JsonElement>()
            : sampleDocument.RootElement.EnumerateArray().ToList();

        if (definition is null && sample.Count == 0)
        {
            _log($"  {pmObject.Name}: no metadata definition and no items returned — nothing to generate. " +
                 "The object may be unlicensed on this tenant, or simply empty.");
            return null;
        }

        var content = Render(pmObject, definition, sample);

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _log($"  {pmObject.Name}: wrote {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>
    /// Reads a few items, purely to see the shape the collection endpoint returns.
    ///
    /// <para>The sampled items are copied into a document of their own — as a JSON array —
    /// because the page that produced them owns the buffer their <see cref="JsonElement"/>s
    /// point into, and that buffer is released when the page is disposed. The caller
    /// disposes what is returned here.</para>
    /// </summary>
    private async Task<JsonDocument?> FetchSampleAsync(
        PmObject pmObject,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var reader = new HalPagedReader(_client, _config, pmObject, _log);
            using var page = await reader.NextPageAsync(sampleSize, cancellationToken).ConfigureAwait(false);

            if (page is null || page.Items.Count == 0) return null;

            var buffer = new StringBuilder("[");
            var first = true;

            foreach (var item in page.Items.Take(sampleSize))
            {
                if (!first) buffer.Append(',');
                buffer.Append(item.GetRawText());
                first = false;
            }

            buffer.Append(']');
            return JsonDocument.Parse(buffer.ToString());
        }
        catch (ApiException ex)
        {
            _log($"  {pmObject.Name}: could not sample items ({ex.Message.Split('\n')[0]}). " +
                 "Generating from the metadata definition alone.");
            return null;
        }
    }

    private string Render(PmObject pmObject, ResourceMetadata? definition, List<JsonElement> sample)
    {
        var builder = new StringBuilder(4096);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Paths, not just columns. The same property is described twice — once by the
        // data as 'projectId', once by the metadata definition as 'Project.ProjectId' —
        // and only a path-level check collapses those into one column.
        var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Line(string text) => builder.Append(text).Append('\n');

        Line($"# Column list for '{pmObject.Name}' ({pmObject.Path})");
        Line($"# Generated by aprimo-pm-export --discover-fields on {DateTimeOffset.UtcNow:u}.");
        Line("#");
        Line("# Edit freely: delete a line to drop the column, reorder to reorder the CSV.");
        Line("# Regenerating overwrites this file, so keep local edits somewhere or use a copy.");
        Line("#");
        Line("# Syntax:");
        Line("#   name              the item property, falling back to an extended attribute of that name");
        Line("#   prop:name         the item property only");
        Line("#   ea:Name           a single-valued extended attribute, by name");
        Line("#   ea#123            the same attribute by ID, for tenants that return attributeName null");
        Line("#   eamulti:Name      a multi-valued (pick-list) attribute; exports encodedListItemId codes");
        Line("#   Column => path    an explicit mapping");
        Line("");

        // ---- Identity first, so every file starts with something usable ----
        Line("# ---- Identity ----");
        EmitMapping(Line, emitted, "Id", "prop:" + pmObject.IdProperty, "", emittedPaths);

        if (pmObject.TitleProperty is { Length: > 0 } title)
            EmitMapping(Line, emitted, "Title", "prop:" + title, "", emittedPaths);

        if (pmObject.ModifiedProperty is { Length: > 0 } modified)
            EmitMapping(Line, emitted, "ModifiedDate", "prop:" + modified, "", emittedPaths);

        if (pmObject.CreatedProperty is { Length: > 0 } created)
            EmitMapping(Line, emitted, "CreatedDate", "prop:" + created, "", emittedPaths);

        Line("");

        // ---- Scalar properties actually observed on the wire ----
        var observed = CollectObservedProperties(sample);

        if (observed.Count > 0)
        {
            Line($"# ---- Properties returned by {pmObject.Path} ----");
            Line($"# Observed across {sample.Count} sampled item(s). Nested objects and arrays are");
            Line("# omitted: they would land in a cell as raw JSON. Map one explicitly if you want it,");
            Line("# e.g.  ManagerName => projectManager.name");
            Line("");

            foreach (var name in observed)
            {
                EmitMapping(Line, emitted, FieldListLoader.ToColumnName(name), "prop:" + name,
                    "", emittedPaths);

                // Also covers an object with no metadata definition, where the loop below
                // never runs. Where it does run, the identical path is skipped as a repeat.
                if (IsUserField(name))
                    EmitUserLabel(Line, emitted, emittedPaths, name);
            }

            Line("");
        }
        else if (sample.Count == 0)
        {
            Line("# ---- Properties ----");
            Line("# No items were returned, so nothing could be confirmed on the wire. The entries below");
            Line("# come from the metadata definition only; run --sample once the object has data.");
            Line("");
        }

        // ---- Extended attributes from the metadata definition ----
        if (definition is not null)
        {
            var attributes = definition.Fields
                .Where(f => !f.Inactive)
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .ToList();

            var secure = attributes.Count(f => f.IsSecure);

            Line("# ---- Fields from GET /metadata/" + (pmObject.MetadataResource ?? "?") + " ----");
            Line($"# {definition.Fields.Count} field(s) defined" +
                 (definition.Fields.Count != attributes.Count
                     ? $", {definition.Fields.Count - attributes.Count} inactive and omitted"
                     : "") + ".");
            Line($"# supportsEAs={definition.SupportsExtendedAttributes.ToString().ToLowerInvariant()}" +
                 $", supportsSecureEAs={definition.SupportsSecureExtendedAttributes.ToString().ToLowerInvariant()}.");

            if (secure > 0)
                Line($"# {secure} field(s) are marked secure; whether their values come back at all " +
                     "depends on the registration's rights.");

            var systemFields = attributes.Where(f => f.IsSystemField).ToList();
            var realAttributes = attributes.Where(f => !f.IsSystemField).ToList();

            Line("#");
            Line($"# {systemFields.Count} of these describe the object's own schema properties and");
            Line($"# {realAttributes.Count} are tenant-defined extended attributes. The two are mapped");
            Line("# differently — a property is read directly, an attribute out of the extendedAttributes");
            Line("# array — so they are written as separate sections below.");
            Line("");

            if (systemFields.Count > 0)
            {
                Line("# -- System fields (the object's own properties) --");
                Line("# Identified by a qualified fieldName like 'Project.ProjectId' and a negative");
                Line("# amsAttributeId. Anything already listed above is skipped rather than repeated.");
                Line("");

                foreach (var field in systemFields)
                {
                    var property = field.PropertyName;
                    if (string.IsNullOrWhiteSpace(property)) continue;

                    // Deduplicate on the path, not the column: the same property arrives
                    // here under a qualified name ('Project.ProjectId') having already
                    // been emitted from the observed data as 'projectId'. Keying on the
                    // column alone would let both through as separate columns, one of
                    // which can never resolve.
                    EmitMapping(Line, emitted, FieldListLoader.ToColumnName(property),
                        "prop:" + property, Annotate(field), emittedPaths);

                    // A user field gets its names from /users/{id}, not from the lookup it
                    // declares: that list is capped and omits inactive users. When there is a
                    // sample, require the property to have been seen as a plain number — an
                    // object-valued 'createdBy' (Budget has one) holds no ID to resolve.
                    if (IsUserField(property!) && (sample.Count == 0 || SeenAsScalar(observed, property!)))
                        EmitUserLabel(Line, emitted, emittedPaths, property!);
                    else
                        EmitLabel(Line, emitted, emittedPaths, field, property!, property!);
                }

                Line("");
            }

            if (realAttributes.Count > 0)
            {
                Line("# -- Extended attributes --");
                Line("# Written as a fallback chain: by name, then by ID. The spec marks attributeName");
                Line("# readOnly without saying whether a read populates it, so the ID branch is what");
                Line("# keeps the column working on a tenant that returns names as null. Once --sample");
                Line("# shows which branch resolves, pin it — a future change then shows up as an empty");
                Line("# column instead of silently sliding to another source.");
                Line("");

                foreach (var field in realAttributes)
                {
                    var column = FieldListLoader.ToColumnName(field.DisplayName);
                    if (column.Length == 0) continue;

                    var branches = new List<string>();

                    if (!string.IsNullOrWhiteSpace(field.FieldName))
                        branches.Add("ea:" + field.FieldName);

                    if (field.AttributeId is { } id and > 0)
                        branches.Add($"ea#{id}");

                    if (branches.Count == 0) continue;

                    EmitMapping(Line, emitted, column, string.Join(" || ", branches),
                        Annotate(field), emittedPaths);

                    EmitLabel(Line, emitted, emittedPaths, field, field.FieldName, "ea:" + field.FieldName);
                }

                Line("");
            }
        }
        else
        {
            Line("# ---- Fields from GET /metadata/{resourceName} ----");
            Line($"# Not available for '{pmObject.MetadataResource ?? pmObject.Name}' on this tenant, so");
            Line("# tenant-defined extended attributes could not be listed. Any attributes seen on the");
            Line("# sampled items are below; anything else has to be added by hand as 'ea:Name'.");
            Line("");
        }

        // ---- Extended attributes seen on the wire but not in the definition ----
        var fromData = CollectObservedAttributes(sample);
        var missing = fromData.Where(a => !emitted.Contains(FieldListLoader.ToColumnName(a.Column))).ToList();

        if (missing.Count > 0)
        {
            Line("# ---- Extended attributes seen on sampled items ----");
            if (definition is not null)
                Line("# Present in the data but not in the metadata definition above.");
            Line("");

            foreach (var (column, path) in missing)
                EmitMapping(Line, emitted, FieldListLoader.ToColumnName(column), path, "", emittedPaths);

            Line("");
        }

        if (pmObject.SupportsExtendedAttributes && fromData.Count == 0 && sample.Count > 0)
        {
            Line("# Note: no extended attributes appeared on the sampled items, though the schema has");
            Line("# them. Either this tenant defines none for this object, or the sampled items simply");
            Line("# have none set. Sample more widely before concluding the former.");
            Line("");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds a <c>…Label</c> column beside a coded one.
    ///
    /// <para>Both are emitted rather than just the label, because they answer different
    /// questions: the label is what a person reads, the code is what joins to other
    /// systems and what survives the label being renamed. Drop whichever you do not want
    /// — it is a generated file meant to be edited.</para>
    /// </summary>
    private void EmitLabel(
        Action<string> line,
        HashSet<string> emitted,
        HashSet<string> emittedPaths,
        MetadataField field,
        string displayName,
        string innerPath)
    {
        if (!_config.Output.GenerateCodeAndLabelColumns) return;
        if (!field.HasLookup) return;

        var column = FieldListLoader.ToColumnName(displayName) + "Label";

        EmitMapping(line, emitted, column, "label:" + innerPath,
            "   # resolved from " + DescribeRoute(field), emittedPaths);
    }

    /// <summary>True when <c>Output.UserFields</c> names this property as holding user IDs.</summary>
    private bool IsUserField(string property) =>
        _config.Output.UserFields.Contains(property, StringComparer.OrdinalIgnoreCase);

    private static bool SeenAsScalar(IReadOnlyList<string> observed, string property) =>
        observed.Contains(property, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a <c>…Label</c> column that resolves a user ID to a name.
    ///
    /// <para>The column keeps the <c>Label</c> suffix the other resolved columns use, so a
    /// file switching from <c>label:projectManager</c> to <c>user:projectManager</c> changes
    /// only the values in that column — not its name, and so not anything downstream that
    /// reads it.</para>
    /// </summary>
    private void EmitUserLabel(
        Action<string> line,
        HashSet<string> emitted,
        HashSet<string> emittedPaths,
        string property)
    {
        if (!_config.Output.GenerateCodeAndLabelColumns) return;

        EmitMapping(line, emitted, FieldListLoader.ToColumnName(property) + "Label",
            "user:" + property, "   # a user ID, resolved through GET /users/{id}", emittedPaths);
    }

    private static string DescribeRoute(MetadataField field)
    {
        var routes = new List<string>();

        if (field.AttributeId is > 0) routes.Add($"attribute {field.AttributeId} pick list");
        if (field.LookupListId is > 0) routes.Add($"lookup {field.LookupListId}");
        if (!string.IsNullOrWhiteSpace(field.SelectionListName))
            routes.Add($"selection list '{field.SelectionListName}'");

        // Several routes are commonly declared and the first can 404, so the comment
        // lists them in the order the resolver will try them.
        return string.Join(" then ", routes);
    }

    /// <summary>Describes a field's properties as a trailing comment.</summary>
    private static string Annotate(MetadataField field)
    {
        var note = new List<string>();

        if (field.IsSecure) note.Add("secure");
        if (field.IsReadOnly) note.Add("read-only");
        if (field.DataType is { } dataType) note.Add($"dataType {dataType}");

        // The three ways a value can turn out to be a code rather than the thing itself.
        // Called out here because a column annotated this way is one whose CSV cell will
        // hold an integer, and the comment is the only place that says so.
        if (!string.IsNullOrWhiteSpace(field.SelectionListName))
            note.Add($"CODE -> selection list '{field.SelectionListName}'");
        if (!string.IsNullOrWhiteSpace(field.SystemTypeName))
            note.Add($"CODE -> system type '{field.SystemTypeName}'");
        if (field.LookupListId is { } lookup and > 0)
            note.Add($"CODE -> lookup list {lookup}");

        return note.Count > 0 ? "   # " + string.Join(", ", note) : "";
    }

    /// <param name="emittedPaths">
    /// Paths already written. Deduplicating on the path as well as the column matters
    /// because the same property reaches this method twice under different names — once
    /// as observed data (<c>projectId</c>) and once from the metadata definition
    /// (<c>Project.ProjectId</c>). Keying on the column alone lets both through.
    /// </param>
    private static void EmitMapping(
        Action<string> line,
        HashSet<string> emitted,
        string column,
        string path,
        string suffix = "",
        HashSet<string>? emittedPaths = null)
    {
        if (column.Length == 0) return;
        if (emittedPaths is not null && !emittedPaths.Add(path)) return;
        if (!emitted.Add(column)) return;

        line($"{column} => {path}{suffix}");
    }

    /// <summary>Scalar property names seen across the sample, in first-seen order.</summary>
    private static List<string> CollectObservedProperties(List<JsonElement> sample)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in sample)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            foreach (var property in item.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;

                if (property.Name.StartsWith('_')) continue;   // HAL plumbing.
                if (seen.Add(property.Name)) names.Add(property.Name);
            }
        }

        return names;
    }

    /// <summary>Extended attributes seen across the sample, as (column, path) pairs.</summary>
    private static List<(string Column, string Path)> CollectObservedAttributes(List<JsonElement> sample)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in sample)
        {
            Collect(item, CompiledPath.ExtendedAttributesProperty, "attributeName", "eaId", "ea");
            Collect(item, CompiledPath.MultiValueExtendedAttributesProperty,
                "attributeName", "extAttributeId", "eamulti");
        }

        return found;

        void Collect(JsonElement item, string arrayProperty, string nameKey, string idKey, string prefix)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (!item.TryGetProperty(arrayProperty, out var array) ||
                array.ValueKind != JsonValueKind.Array) return;

            foreach (var attribute in array.EnumerateArray())
            {
                if (attribute.ValueKind != JsonValueKind.Object) continue;

                var name = attribute.TryGetProperty(nameKey, out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;

                var id = attribute.TryGetProperty(idKey, out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetRawText()
                    : null;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (seen.Add(name!)) found.Add((name!, $"{prefix}:{name}"));
                }
                else if (id is not null)
                {
                    var column = $"{prefix}_{id}";
                    if (seen.Add(column)) found.Add((column, $"{prefix}#{id}"));
                }
            }
        }
    }
}
