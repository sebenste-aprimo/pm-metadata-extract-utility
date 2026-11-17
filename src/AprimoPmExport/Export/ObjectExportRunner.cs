using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>Resume point for one object, written after every completed page.</summary>
public sealed class Checkpoint
{
    public int Version { get; set; } = 1;
    public string Object { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowsWritten { get; set; }
    public int LastFileIndex { get; set; }
    public PagingState Paging { get; set; } = new();

    /// <summary>Guards against resuming with settings that would produce an inconsistent file set.</summary>
    public string ConfigFingerprint { get; set; } = "";
}

public enum ObjectOutcome
{
    Succeeded,
    Skipped,
    Failed,
    Cancelled
}

public sealed class ObjectExportResult
{
    public required string Object { get; init; }
    public ObjectOutcome Outcome { get; init; }
    public long RowsWritten { get; init; }
    public int FilesCreated { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    public int PagesFetched { get; init; }
    public long? ApproximateTotalAvailable { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool HitCap { get; init; }
    public int DuplicatesDropped { get; init; }

    /// <summary>Why the object was skipped or failed. Null on success.</summary>
    public string? Message { get; init; }

    /// <summary>Delta filter statistics, when the run had a window.</summary>
    public string? DeltaSummary { get; init; }

    /// <summary>True when the run covered its whole window and the mark may advance.</summary>
    public bool WindowComplete { get; init; }
}

/// <summary>
/// Exports one PM object to a CSV file set.
///
/// <para>The DAM utility's runner had a simpler job: one query, one file set, and a
/// server-side window when running a delta. Here the window is applied locally, so this
/// class also owns the decision about when a delta run may stop paging — see
/// <see cref="DeltaFilter"/>.</para>
/// </summary>
public sealed class ObjectExportRunner
{
    private static readonly JsonSerializerOptions CheckpointJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly MetadataCatalog _metadata;
    private readonly Action<string> _log;

    public ObjectExportRunner(
        ExportConfig config,
        ApiClient client,
        MetadataCatalog metadata,
        Action<string> log)
    {
        _config = config;
        _client = client;
        _metadata = metadata;
        _log = log;

        // One resolver for the whole run, not one per object: the same people manage
        // projects, own activities and modify tasks, so a name fetched for one object is
        // free for the next.
        _users = config.Output.ResolveUsers ? UserResolver.FromClient(client, log) : null;
    }

    private readonly UserResolver? _users;

    private string CheckpointPath(PmObject pmObject) =>
        Path.Combine(
            CsvRollingWriter.ResolveDirectory(_config.Output, pmObject.Name),
            $".aprimo-pm-checkpoint-{pmObject.Name}.json");

    public async Task<ObjectExportResult> RunAsync(
        PmObject pmObject,
        IReadOnlyList<FieldMapping> mappings,
        DeltaWindow? window,
        bool resume,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Fetched once per object, before the first page: the lists are small and shared
        // across every row, so doing it here costs a handful of requests rather than one
        // per value.
        var lookups = LookupResolver.Empty;

        if (_config.Output.ResolveLookups)
        {
            var keys = FieldExtractor.RequiredLookupKeys(mappings);

            if (keys.Count > 0)
                lookups = await LookupResolver
                    .BuildAsync(_client, _metadata, pmObject, keys, _log, cancellationToken)
                    .ConfigureAwait(false);
        }

        var extractor = new FieldExtractor(mappings, _config.Output.MultiValueSeparator, lookups, _users);

        DeltaFilter? filter = null;
        if (window is not null)
            filter = new DeltaFilter(window, pmObject, _config.Source.Delta, _log);

        var checkpointPath = CheckpointPath(pmObject);
        var fingerprint = ComputeFingerprint(pmObject, mappings, window);

        Checkpoint? restored = null;
        if (resume)
        {
            restored = LoadCheckpoint(checkpointPath, fingerprint);
            if (restored is null)
                _log($"  {pmObject.Name}: no usable checkpoint — starting a fresh export.");
        }

        if (restored is null)
            CsvRollingWriter.PrepareDirectory(_config.Output, pmObject.Name, _log);

        var startingFileIndex = restored is not null
            ? Math.Max(restored.LastFileIndex, CsvRollingWriter.FindHighestFileIndex(_config.Output, pmObject.Name))
            : 0;

        var reader = new HalPagedReader(_client, _config, pmObject, _log, restored?.Paging);

        if (filter is not null)
        {
            // A delta run must be ordered by the property the window compares — sorting
            // by anything else would make "every row on this page is older" mean nothing.
            reader.SortFieldOverride = filter.Property;

            if (_config.Source.Delta.Strategy == DeltaStrategy.SortedDescending)
            {
                reader.SortAscendingOverride = false;
                _log($"  {pmObject.Name}: paging newest-first on '{filter.Property}' so it can stop " +
                     "once rows fall before the window.");
            }
        }

        // The one server-side changed-since filter the spec offers. Narrowing at the
        // source is strictly better than filtering locally, so use it where it exists —
        // the local filter still runs, since this only bounds one end of the window.
        if (window is not null && pmObject.ModifiedAfterParam is { Length: > 0 })
        {
            reader.ModifiedAfter = window.Since;
            _log($"  {pmObject.Name}: narrowing server-side with " +
                 $"{pmObject.ModifiedAfterParam}={DeltaWindow.Format(window.Since)}.");
        }

        await using var writer = new CsvRollingWriter(
            _config.Output, pmObject.Name, _config.Limits.MaxRecordsPerFile,
            extractor.Headers, _log, startingFileIndex);

        var rowsWritten = restored?.RowsWritten ?? 0;
        var cap = _config.Limits.MaxRecordsPerObject;
        var hitCap = false;
        var cancelled = false;
        var verifiedShape = false;
        var pagesFetched = restored?.Paging.PagesFetched ?? 0;

        var row = new string[extractor.ColumnCount];

        if (restored is not null)
            _log($"  {pmObject.Name}: resuming — {rowsWritten:N0} rows already written, " +
                 $"{restored.Paging.PagesFetched} page(s) fetched. New rows continue in part {startingFileIndex + 1}.");

        try
        {
            while (reader.HasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // With a delta window the API returns rows we will mostly discard, so the
                // remaining budget cannot shrink the request — take a full page and let
                // the filter decide. Without one, every returned row is written, so the
                // final request can be trimmed to the cap exactly.
                var takeLimit = _config.Source.Paging.PageSize;

                if (cap > 0 && filter is null)
                {
                    var remaining = cap - rowsWritten;
                    if (remaining <= 0) { hitCap = true; break; }
                    takeLimit = (int)Math.Min(_config.Source.Paging.PageSize, remaining);
                }
                else if (cap > 0 && rowsWritten >= cap)
                {
                    hitCap = true;
                    break;
                }

                using var page = await reader.NextPageAsync(takeLimit, cancellationToken).ConfigureAwait(false);
                if (page is null) break;

                pagesFetched = page.PageNumber;

                if (!verifiedShape)
                {
                    VerifyShape(pmObject, page, extractor);
                    verifiedShape = true;
                }

                var beforeWindowOnPage = 0;

                // Choose the rows first, write them second. Resolving user names needs a
                // network round trip that extraction itself cannot make, and doing it after
                // the filter means a delta run looks up users only for the rows it keeps —
                // not for the great majority it is about to discard.
                var selected = new List<JsonElement>(page.Items.Count);

                foreach (var item in page.Items)
                {
                    if (cap > 0 && rowsWritten + selected.Count >= cap) { hitCap = true; break; }

                    if (filter is not null)
                    {
                        var wasBefore = filter.BeforeWindow;
                        var accepted = filter.Accept(item);
                        if (filter.BeforeWindow > wasBefore) beforeWindowOnPage++;
                        if (!accepted) continue;
                    }

                    selected.Add(item);
                }

                if (_users is not null && extractor.HasUserColumns)
                    await _users.PrefetchAsync(extractor.UserCodes(selected), cancellationToken)
                        .ConfigureAwait(false);

                foreach (var item in selected)
                {
                    extractor.ExtractRow(item, row);
                    writer.WriteRow(row);
                    rowsWritten++;
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (_config.Limits.EnableCheckpoint)
                    SaveCheckpoint(checkpointPath, new Checkpoint
                    {
                        Object = pmObject.Name,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        RowsWritten = rowsWritten,
                        LastFileIndex = writer.CurrentFileIndex,
                        Paging = reader.State,
                        ConfigFingerprint = fingerprint
                    });

                // The whole page fell before the window, and the ordering that makes that
                // conclusive has been observed. Everything after it is older still.
                if (filter is not null && filter.CanStopAfterPage(page.Items.Count, beforeWindowOnPage))
                {
                    reader.Stop("every row on this page predates the delta window");
                    _log($"  {pmObject.Name}: stopping at page {page.PageNumber} — " +
                         "the rest of the collection is older than the window.");
                }

                if (hitCap) break;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _log($"  {pmObject.Name}: cancellation requested — flushing what has been written.");
        }

        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        ReportValueWarnings(pmObject, writer, extractor);

        return new ObjectExportResult
        {
            Object = pmObject.Name,
            Outcome = cancelled ? ObjectOutcome.Cancelled : ObjectOutcome.Succeeded,
            RowsWritten = rowsWritten,
            FilesCreated = writer.FilesCreated,
            FilePaths = writer.FilePaths,
            PagesFetched = pagesFetched,
            ApproximateTotalAvailable = reader.LastTotal,
            Elapsed = stopwatch.Elapsed,
            HitCap = hitCap,
            DuplicatesDropped = reader.DuplicatesDropped,
            DeltaSummary = filter?.Summarize(),
            // The mark may only advance when the window was genuinely exhausted. A
            // cancelled run, or one cut short by the cap, has not covered it.
            WindowComplete = !cancelled && !hitCap
        };
    }

    /// <summary>
    /// Checks the first page looks like items of this object rather than something else
    /// entirely, so a misconfigured path fails immediately instead of writing a file of
    /// blank columns.
    /// </summary>
    private void VerifyShape(PmObject pmObject, ItemPage page, FieldExtractor extractor)
    {
        if (!_config.Source.VerifyShapeOnFirstPage) return;
        if (page.Items.Count == 0) return;

        var withId = page.Items.Count(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(pmObject.IdProperty, out var id) &&
            id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

        if (withId == 0)
        {
            var first = page.Items[0];
            var keys = first.ValueKind == JsonValueKind.Object
                ? string.Join(", ", first.EnumerateObject().Select(p => p.Name).Take(15))
                : first.ValueKind.ToString();

            throw new ApiException(
                $"{pmObject.Name}: none of the {page.Items.Count} items on page 1 has the configured " +
                $"IdProperty '{pmObject.IdProperty}'.\n" +
                $"  Items were read from _embedded['{page.EmbedKey ?? "?"}'] and their properties are: {keys}.\n" +
                "  Fix IdProperty for this object in objects.json, or set " +
                "Source.VerifyShapeOnFirstPage=false to export anyway.");
        }

        if (extractor.RequiresExtendedAttributes && !pmObject.SupportsExtendedAttributes)
            _log($"  {pmObject.Name}: the column list reads extended attributes, but this object's schema " +
                 "does not declare them. Those columns will be empty.");
    }

    private void ReportValueWarnings(PmObject pmObject, CsvRollingWriter writer, FieldExtractor extractor)
    {
        if (writer.ColumnsWithNewlines.Count > 0 &&
            _config.Output.NewlineInValue == NewlineHandling.Preserve)
        {
            var affected = writer.ColumnsWithNewlines.OrderBy(c => c, StringComparer.Ordinal).ToList();
            _log($"  {pmObject.Name}: {affected.Count} column(s) held newline-delimited values and were " +
                 "kept as-is (Output.NewlineInValue = Preserve), so those rows span several physical " +
                 "lines. Readers must use a real CSV parser, not line splitting: " +
                 string.Join(", ", affected.Take(8)) + (affected.Count > 8 ? ", …" : ""));
        }

        if (_users is not null && extractor.HasUserColumns)
            _log($"  {pmObject.Name}: user names — {_users.Resolved:N0} resolved, {_users.Unresolved:N0} " +
                 $"not, {_users.Requests:N0} request(s) so far this run.");

        if (extractor.ColumnsWithUnresolvedCodes.Count > 0)
        {
            var affected = extractor.ColumnsWithUnresolvedCodes.OrderBy(c => c, StringComparer.Ordinal).ToList();
            _log($"  {pmObject.Name}: {affected.Count} label column(s) met a code that did not resolve, " +
                 "and kept the raw code for those rows. For a list column that is usually a value " +
                 "retired from the list while records still reference it; for a user column, a user " +
                 "that does not exist or could not be read: " +
                 string.Join(", ", affected.Take(8)) + (affected.Count > 8 ? ", …" : ""));
        }

        if (extractor.ColumnsWithSeparatorInValue.Count > 0)
        {
            var affected = extractor.ColumnsWithSeparatorInValue.OrderBy(c => c, StringComparer.Ordinal).ToList();
            _log($"  {pmObject.Name}: {affected.Count} multi-value column(s) contained a value that itself " +
                 $"includes '{_config.Output.MultiValueSeparator}', so those cells cannot be split " +
                 "reliably: " + string.Join(", ", affected.Take(8)) + (affected.Count > 8 ? ", …" : ""));
        }
    }

    private Checkpoint? LoadCheckpoint(string path, string fingerprint)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), CheckpointJson);
            if (checkpoint is null) return null;

            if (!string.Equals(checkpoint.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _log("  Checkpoint ignored: the paging settings, delta window or column mappings have " +
                     "changed since it was written. Resuming would mix incompatible output.");
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            _log($"  Checkpoint at '{path}' could not be read ({ex.Message}) — starting fresh.");
            return null;
        }
    }

    private void SaveCheckpoint(string path, Checkpoint checkpoint)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write-then-replace so a crash mid-write cannot corrupt the checkpoint.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, CheckpointJson));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"  Warning: could not write checkpoint ({ex.Message}). The export continues, " +
                 "but this object will not be resumable.");
        }
    }

    /// <summary>
    /// Hashes the settings that determine item order and column layout. A resume is only
    /// safe when these are unchanged.
    /// </summary>
    private string ComputeFingerprint(
        PmObject pmObject,
        IReadOnlyList<FieldMapping> mappings,
        DeltaWindow? window)
    {
        var source = _config.Source;

        var material = string.Join("", new[]
        {
            _config.Aprimo.ResolvedApiBaseUrl,
            pmObject.Name,
            pmObject.Path,
            source.SortField ?? pmObject.DefaultSortField ?? "",
            source.SortAscending.ToString(),
            source.Paging.Mode.ToString(),
            source.Paging.PageSize.ToString(),
            string.Join(",", pmObject.ExtraQuery.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")),
            // A resumed run must be finishing the same window, or the parts would cover
            // different periods. The strategy is in here too, because it flips the sort
            // direction and so changes the order the offsets refer to.
            window is null ? "(full)" : $"{window.Since:O}..{window.Until:O}",
            window is null ? "" : source.Delta.Strategy.ToString(),
            string.Join(",", mappings.Select(f => $"{f.Column}={f.Path}"))
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }
}
