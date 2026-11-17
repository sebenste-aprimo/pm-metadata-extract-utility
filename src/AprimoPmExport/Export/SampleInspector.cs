using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>
/// One-request-per-object diagnostic. For each selected object it reports:
///
/// <list type="bullet">
/// <item>whether the endpoint answers at all, and under which <c>_embedded</c> key —
/// the spec never says what that key is, so seeing it confirmed matters;</item>
/// <item>the approximate <c>_total</c>;</item>
/// <item>which configured columns resolved and which came back empty, so a wrong
/// property or attribute name is caught before a long export;</item>
/// <item>which extended attributes the items actually carry, and whether
/// <c>attributeName</c> is populated — the answer decides whether <c>ea:Name</c> or
/// <c>ea#id</c> mappings are the ones that will work on this tenant;</item>
/// <item>whether the object's timestamp property parses, since a delta run depends
/// on it.</item>
/// </list>
///
/// <para>The report goes to stdout so it can be redirected; progress logging stays on
/// stderr.</para>
/// </summary>
public sealed class SampleInspector
{
    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly MetadataCatalog _metadata;
    private readonly string? _configPath;
    private readonly string? _fieldsFileOverride;
    private readonly Action<string> _log;

    public SampleInspector(
        ExportConfig config,
        ApiClient client,
        MetadataCatalog metadata,
        string? configPath,
        string? fieldsFileOverride,
        Action<string> log)
    {
        _config = config;
        _client = client;
        _metadata = metadata;
        _configPath = configPath;
        _fieldsFileOverride = fieldsFileOverride;
        _log = log;
    }

    /// <summary>Returns true when at least one object returned items.</summary>
    public async Task<bool> RunAsync(
        IReadOnlyList<PmObject> objects,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        var anyItems = false;

        Console.WriteLine();
        Console.WriteLine("════════ Sample inspection ════════");
        Console.WriteLine($"Endpoint  : {_config.Aprimo.ResolvedApiBaseUrl}");
        Console.WriteLine($"Objects   : {objects.Count}");
        Console.WriteLine($"Page size : {sampleSize} (one request per object)");

        foreach (var pmObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine();
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"{pmObject.Name}   {pmObject.Path}");

            try
            {
                if (await InspectAsync(pmObject, sampleSize, cancellationToken).ConfigureAwait(false))
                    anyItems = true;
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"  FAILED: {ex.Message.Split('\n')[0]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 72));

        if (!anyItems)
        {
            Console.WriteLine("No object returned any items.");
            Console.WriteLine("  Either the tenant genuinely has none, or the registration cannot see them —");
            Console.WriteLine("  PM applies per-object access lists, so an integration user with no memberships");
            Console.WriteLine("  sees empty collections rather than an error. Check with a user who can see the");
            Console.WriteLine("  data in the UI.");
        }

        return anyItems;
    }

    private async Task<bool> InspectAsync(
        PmObject pmObject,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        var reader = new HalPagedReader(_client, _config, pmObject, _log);
        using var page = await reader.NextPageAsync(sampleSize, cancellationToken).ConfigureAwait(false);

        if (page is null || page.Items.Count == 0)
        {
            Console.WriteLine("  Items returned    : 0");
            Console.WriteLine(page?.Total is { } empty
                ? $"  Server _total     : {empty:N0}"
                : "  Server _total     : (not reported)");
            Console.WriteLine("  Nothing to inspect — the collection is empty, or not visible to this registration.");
            return false;
        }

        Console.WriteLine($"  Items returned    : {page.Items.Count}");
        Console.WriteLine($"  _embedded key     : {page.EmbedKey ?? "(none)"}" +
                          (pmObject.EmbedKey is null
                              ? "   <- add this as EmbedKey in objects.json to pin it"
                              : ""));
        Console.WriteLine(page.Total is { } total
            ? $"  Server _total     : {total:N0}"
            : "  Server _total     : (not reported)");

        ReportIdentity(pmObject, page);
        ReportTimestamps(pmObject, page);
        ReportAttributes(page);
        await ReportMetadataDefinitionAsync(pmObject, cancellationToken).ConfigureAwait(false);
        ReportColumns(pmObject, page);

        return true;
    }

    private static void ReportIdentity(PmObject pmObject, ItemPage page)
    {
        var withId = page.Items.Count(i =>
            i.ValueKind == JsonValueKind.Object &&
            i.TryGetProperty(pmObject.IdProperty, out var v) &&
            v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

        var mark = withId == page.Items.Count ? "yes" : withId == 0 ? "NO" : "partial";

        Console.WriteLine($"  IdProperty        : {pmObject.IdProperty} — present {mark} " +
                          $"({withId}/{page.Items.Count})");

        if (withId != 0) return;

        var first = page.Items[0];
        var keys = first.ValueKind == JsonValueKind.Object
            ? string.Join(", ", first.EnumerateObject().Select(p => p.Name).Take(15))
            : first.ValueKind.ToString();

        Console.WriteLine($"    Item properties : {keys}");
        Console.WriteLine("    Fix IdProperty for this object in objects.json — de-duplication and --record");
        Console.WriteLine("    both depend on it.");
    }

    /// <summary>
    /// Checks the delta property is readable. A property that is present but unparseable
    /// is the failure worth catching here: an incremental run would then silently write
    /// nothing at all, since no row could be placed in the window.
    /// </summary>
    private void ReportTimestamps(PmObject pmObject, ItemPage page)
    {
        if (!pmObject.SupportsDelta)
        {
            Console.WriteLine("  Delta property    : none on this object — full export only");
            return;
        }

        // A window is needed to build a filter; its bounds are irrelevant to parsing.
        var probe = new DeltaFilter(
            new DeltaWindow(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(1), false, "probe"),
            pmObject, _config.Source.Delta, _ => { });

        var parsed = 0;
        DateTimeOffset? example = null;

        foreach (var item in page.Items)
        {
            var value = probe.ReadInstant(item);
            if (value is null) continue;
            parsed++;
            example ??= value;
        }

        var status = parsed == page.Items.Count ? "yes" : parsed == 0 ? "NO" : "partial";

        Console.WriteLine($"  Delta property    : {probe.Property} — readable {status} " +
                          $"({parsed}/{page.Items.Count})" +
                          (example is { } e ? $", e.g. {DeltaWindow.Format(e)}" : ""));

        if (parsed == 0)
            Console.WriteLine("    A --since run would export nothing, since no item can be placed in the " +
                              "window. Check ModifiedProperty in objects.json.");
    }

    /// <summary>
    /// Reports the extended attributes present, and specifically whether
    /// <c>attributeName</c> is populated.
    ///
    /// <para>That single fact decides how a column list for this tenant should be
    /// written. The spec marks <c>attributeName</c> <c>readOnly</c> without committing to
    /// populating it on a read, so <c>ea:Name</c> mappings may resolve to nothing while
    /// <c>ea#id</c> works — and the failure is silent, producing empty columns rather
    /// than an error.</para>
    /// </summary>
    private static void ReportAttributes(ItemPage page)
    {
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        foreach (var item in page.Items)
        {
            Scan(item, CompiledPath.ExtendedAttributesProperty, "eaId");
            Scan(item, CompiledPath.MultiValueExtendedAttributesProperty, "extAttributeId");
        }

        Console.WriteLine();
        Console.WriteLine("  ── Extended attributes on these items ──");

        if (total == 0)
        {
            Console.WriteLine("    none present in this sample.");
            return;
        }

        Console.WriteLine($"    {total} value(s): {named.Count} named, {unnamed.Count} with a null " +
                          "attributeName.");

        foreach (var (name, count) in named.OrderByDescending(kvp => kvp.Value).Take(25))
            Console.WriteLine($"      ea:{name}   x{count}");

        if (named.Count > 25)
            Console.WriteLine($"      … and {named.Count - 25} more");

        if (unnamed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"    {unnamed.Count} attribute(s) came back with attributeName null, so only");
            Console.WriteLine("    an ID-based mapping reaches them:");
            foreach (var (id, count) in unnamed.OrderByDescending(kvp => kvp.Value).Take(15))
                Console.WriteLine($"      ea#{id}   x{count}");
        }

        if (named.Count == 0 && unnamed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("    WARNING: this tenant returns NO attribute names. Every 'ea:Name' mapping");
            Console.WriteLine("    would resolve to an empty cell. Use the ea#<id> forms throughout —");
            Console.WriteLine("    --discover-fields writes both, so pin the ID branch in the generated files.");
        }

        void Scan(JsonElement item, string arrayProperty, string idKey)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (!item.TryGetProperty(arrayProperty, out var array) ||
                array.ValueKind != JsonValueKind.Array) return;

            foreach (var attribute in array.EnumerateArray())
            {
                if (attribute.ValueKind != JsonValueKind.Object) continue;
                total++;

                var name = attribute.TryGetProperty("attributeName", out var n) &&
                           n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    named[name!] = named.GetValueOrDefault(name!) + 1;
                    continue;
                }

                var id = attribute.TryGetProperty(idKey, out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetRawText()
                    : "?";

                unnamed[id] = unnamed.GetValueOrDefault(id) + 1;
            }
        }
    }

    private async Task ReportMetadataDefinitionAsync(PmObject pmObject, CancellationToken cancellationToken)
    {
        var definition = await _metadata.GetAsync(pmObject, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.Write($"  ── GET /metadata/{pmObject.MetadataResource ?? "?"} ── ");

        if (definition is null)
        {
            Console.WriteLine("not available");
            Console.WriteLine("    Field discovery falls back to the sampled items alone for this object.");
            Console.WriteLine("    If the tenant uses a different resource name, set MetadataResource in");
            Console.WriteLine("    objects.json.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"    objectName={definition.ObjectName ?? "(null)"}, " +
                          $"resourceType={definition.ResourceType ?? "(null)"}");
        Console.WriteLine($"    supportsEAs={definition.SupportsExtendedAttributes}, " +
                          $"supportsSecureEAs={definition.SupportsSecureExtendedAttributes}, " +
                          $"{definition.Fields.Count} field(s) defined");

        var inactive = definition.Fields.Count(f => f.Inactive);
        var secure = definition.Fields.Count(f => f.IsSecure);
        if (inactive > 0 || secure > 0)
            Console.WriteLine($"    {inactive} inactive, {secure} secure");
    }

    /// <summary>Per-column resolution, so a wrong name surfaces now rather than mid-export.</summary>
    private void ReportColumns(PmObject pmObject, ItemPage page)
    {
        IReadOnlyList<FieldMapping>? mappings;

        try
        {
            mappings = FieldListLoader.LoadForObject(
                pmObject, _config, _configPath, _fieldsFileOverride, _ => { });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Console.WriteLine();
            Console.WriteLine($"  ── Columns ── could not be loaded: {ex.Message}");
            return;
        }

        Console.WriteLine();

        if (mappings is null)
        {
            var derived = FieldListLoader.DeriveFromItem(page.Items[0], pmObject);
            Console.WriteLine($"  ── Columns ── no fields/{pmObject.FieldsFileName}; " +
                              $"{derived.Count} column(s) would be derived from the data.");
            Console.WriteLine("    Run --discover-fields to write a file you can edit and keep.");
            return;
        }

        FieldExtractor extractor;
        try
        {
            extractor = new FieldExtractor(mappings, _config.Output.MultiValueSeparator);
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"  ── Columns ── invalid path: {ex.Message}");
            return;
        }

        Console.WriteLine($"  ── Column resolution across {page.Items.Count} item(s) ──");

        var empty = new List<string>();
        var resolved = 0;

        foreach (var mapping in extractor.Mappings)
        {
            string? firstValue = null;
            var hits = 0;

            foreach (var item in page.Items)
            {
                var value = extractor.Evaluate(item, mapping.Compiled!);
                if (string.IsNullOrEmpty(value)) continue;
                hits++;
                firstValue ??= value;
            }

            if (hits > 0)
            {
                resolved++;
                Console.WriteLine($"    {mapping.Column,-34} {hits}/{page.Items.Count}  " +
                                  $"{Truncate(firstValue!, 44)}");
            }
            else
            {
                empty.Add(mapping.Column);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"    {resolved}/{extractor.ColumnCount} column(s) produced a value in at " +
                          "least one item.");

        if (empty.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"    {empty.Count} column(s) empty in this sample. Genuinely blank on these");
        Console.WriteLine("    items, or the name is wrong — a small sample cannot tell the two apart.");
        Console.WriteLine("    Sample more widely before assuming the former.");
        foreach (var chunk in empty.Chunk(4))
            Console.WriteLine("      " + string.Join(", ", chunk));
    }

    private static string Truncate(string value, int max)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single.Substring(0, max - 1) + "…";
    }
}
