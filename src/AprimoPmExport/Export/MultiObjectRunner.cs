using System.Diagnostics;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

public sealed class RunResult
{
    public IReadOnlyList<ObjectExportResult> Objects { get; init; } = Array.Empty<ObjectExportResult>();
    public long RequestCount { get; init; }
    public long RetryCount { get; init; }
    public long BytesReceived { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }

    public long TotalRows => Objects.Sum(o => o.RowsWritten);
    public int TotalFiles => Objects.Sum(o => o.FilesCreated);
    public int Succeeded => Objects.Count(o => o.Outcome == ObjectOutcome.Succeeded);
    public int Skipped => Objects.Count(o => o.Outcome == ObjectOutcome.Skipped);
    public int Failed => Objects.Count(o => o.Outcome == ObjectOutcome.Failed);
}

/// <summary>
/// Runs an export across several objects, one after another.
///
/// <para>Objects run sequentially and share a single rate limiter, so a run over two
/// dozen collections stays inside exactly the same request ceiling as a run over one.
/// Parallelism would be easy to add and is deliberately absent: the throttle is the
/// binding constraint on a shared tenant, so concurrency would buy nothing except a
/// harder-to-read log.</para>
///
/// <para>By default one object failing does not abandon the rest — an unlicensed or
/// permission-denied collection is a normal outcome across a two-dozen-object registry,
/// and it should not throw away the exports that did work.</para>
/// </summary>
public sealed class MultiObjectRunner
{
    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly MetadataCatalog _metadata;
    private readonly string? _configPath;
    private readonly string? _fieldsFileOverride;
    private readonly Action<string> _log;

    public MultiObjectRunner(
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

    public async Task<RunResult> RunAsync(
        IReadOnlyList<PmObject> objects,
        string? sinceSpec,
        string? untilSpec,
        bool resume,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<ObjectExportResult>(objects.Count);
        var runner = new ObjectExportRunner(_config, _client, _metadata, _log);
        var cancelled = false;

        for (var i = 0; i < objects.Count; i++)
        {
            var pmObject = objects[i];

            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(Skipped(pmObject, "the run was cancelled before this object started"));
                continue;
            }

            _log("");
            _log($"[{i + 1}/{objects.Count}] {pmObject.Name}  ({pmObject.Path})");

            if (pmObject.ScopedToCallingUser)
                _log($"  Note: {pmObject.Path} returns only what the authenticated registration can see, " +
                     "not everything in the tenant. See the README on user-scoped objects.");

            DeltaWindow? window = null;

            if (sinceSpec is not null)
            {
                if (!pmObject.SupportsDelta && _config.Source.Delta.Property is null)
                {
                    var message =
                        $"'{pmObject.Name}' has no modified-date property, so a changed-since window " +
                        "cannot be applied to it";

                    if (_config.Source.Delta.SkipObjectsWithoutModifiedProperty)
                    {
                        _log($"  Skipped: {message}. Export it separately without --since, or set " +
                             "Source.Delta.SkipObjectsWithoutModifiedProperty=false to include it in full.");
                        results.Add(Skipped(pmObject, message));
                        continue;
                    }

                    _log($"  {message} — exporting it in full, because " +
                         "Source.Delta.SkipObjectsWithoutModifiedProperty is false. Note the output is a " +
                         "complete snapshot, not a delta.");
                }
                else
                {
                    try
                    {
                        var state = DeltaStateStore.Load(_config, pmObject, _log);

                        window = DeltaWindowParser.Create(
                            sinceSpec, untilSpec, _config.Source.Delta, state, DateTimeOffset.UtcNow, _log);

                        _log($"  Window: {window.Describe()} on '{_config.Source.Delta.Property ?? pmObject.ModifiedProperty}'");

                        if (state.RunCount > 0)
                            _log($"  Previous runs: {state.RunCount}, {state.TotalRows:N0} rows total, " +
                                 $"last {state.LastRunUtc:u} ({state.LastRunRows:N0} rows).");
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        _log($"  Skipped: {ex.Message}");
                        results.Add(Skipped(pmObject, ex.Message));
                        continue;
                    }
                }
            }

            IReadOnlyList<FieldMapping>? mappings;
            try
            {
                mappings = ResolveColumns(pmObject);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or FormatException)
            {
                _log($"  Failed: {ex.Message}");
                results.Add(Failed(pmObject, ex.Message));
                if (!_config.Limits.ContinueOnObjectFailure) break;
                continue;
            }

            if (mappings is null)
            {
                mappings = await DeriveColumnsAsync(pmObject, cancellationToken).ConfigureAwait(false);

                if (mappings is null)
                {
                    var message =
                        $"no column list for '{pmObject.Name}' and none could be derived " +
                        "(the collection returned no items to learn from)";

                    _log($"  Skipped: {message}.");
                    _log($"  Create fields/{pmObject.FieldsFileName}, or run --discover-fields.");
                    results.Add(Skipped(pmObject, message));
                    continue;
                }
            }

            try
            {
                var result = await runner
                    .RunAsync(pmObject, mappings, window, resume, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(result);

                if (result.Outcome == ObjectOutcome.Cancelled) cancelled = true;

                LogObjectSummary(result);

                if (window is not null)
                {
                    if (result.WindowComplete)
                        DeltaStateStore.Save(_config, pmObject, window, result.RowsWritten, _log);
                    else
                        _log($"  Delta mark for '{pmObject.Name}' NOT advanced: the run " +
                             (result.Outcome == ObjectOutcome.Cancelled
                                 ? "was cancelled"
                                 : $"stopped at the {_config.Limits.MaxRecordsPerObject:N0}-row cap") +
                             ", so the window is incomplete. Re-run with the same --since to finish it.");
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                results.Add(new ObjectExportResult
                {
                    Object = pmObject.Name,
                    Outcome = ObjectOutcome.Cancelled,
                    Message = "cancelled"
                });
            }
            catch (Exception ex) when (ex is ApiException or InvalidOperationException or FormatException or IOException)
            {
                _log($"  Failed: {ex.Message}");
                results.Add(Failed(pmObject, ex.Message));

                if (!_config.Limits.ContinueOnObjectFailure)
                {
                    _log("  Stopping the run (Limits.ContinueOnObjectFailure is false).");
                    break;
                }
            }
        }

        return new RunResult
        {
            Objects = results,
            RequestCount = _client.RequestCount,
            RetryCount = _client.RetryCount,
            BytesReceived = _client.BytesReceived,
            Elapsed = stopwatch.Elapsed,
            Cancelled = cancelled
        };
    }

    private IReadOnlyList<FieldMapping>? ResolveColumns(PmObject pmObject)
    {
        var mappings = FieldListLoader.LoadForObject(
            pmObject, _config, _configPath, _fieldsFileOverride, _log);

        if (mappings is null) return null;

        var duplicates = mappings
            .Select(m => m.Column)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"the column list for '{pmObject.Name}' has duplicate column name(s): " +
                string.Join(", ", duplicates));

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Column))
                throw new InvalidOperationException(
                    $"the column list for '{pmObject.Name}' has an entry with no column name");
            if (string.IsNullOrWhiteSpace(mapping.Path))
                throw new InvalidOperationException(
                    $"column '{mapping.Column}' in '{pmObject.Name}' has no path");
        }

        return mappings;
    }

    /// <summary>
    /// Last resort when an object has no column list: read one page and take the columns
    /// from what came back.
    ///
    /// <para>Costs one extra request per object, and the resulting column set depends on
    /// what those particular items happened to have — an attribute nobody filled in on
    /// page 1 produces no column at all. Usable for a first look, not for a recurring
    /// job.</para>
    /// </summary>
    private async Task<IReadOnlyList<FieldMapping>?> DeriveColumnsAsync(
        PmObject pmObject,
        CancellationToken cancellationToken)
    {
        if (!_config.AutoDeriveColumns)
        {
            _log($"  No fields/{pmObject.FieldsFileName}, and AutoDeriveColumns is off.");
            return null;
        }

        _log($"  No fields/{pmObject.FieldsFileName} — deriving columns from the first page. " +
             "Run --discover-fields to pin them down before relying on this output.");

        var reader = new HalPagedReader(_client, _config, pmObject, _log);
        using var page = await reader.NextPageAsync(1, cancellationToken).ConfigureAwait(false);

        if (page is null || page.Items.Count == 0) return null;

        var mappings = FieldListLoader.DeriveFromItem(page.Items[0], pmObject);
        if (mappings.Count == 0) return null;

        _log($"  Derived {mappings.Count} column(s) from one item.");
        return mappings;
    }

    private void LogObjectSummary(ObjectExportResult result)
    {
        var seconds = Math.Max(result.Elapsed.TotalSeconds, 0.001);

        _log($"  Done: {result.RowsWritten:N0} row(s) in {result.FilesCreated} file(s), " +
             $"{result.PagesFetched} page(s), {result.Elapsed:hh\\:mm\\:ss} " +
             $"({result.RowsWritten / seconds:F0} rows/s)" +
             (result.ApproximateTotalAvailable is { } total ? $", server _total ~{total:N0}" : ""));

        if (result.DeltaSummary is { } delta)
            _log($"  Window: {delta}");

        if (result.DuplicatesDropped > 0)
            _log($"  Dropped {result.DuplicatesDropped:N0} duplicate item(s) already seen in this run.");

        if (result.HitCap)
            _log($"  Stopped at the {_config.Limits.MaxRecordsPerObject:N0}-row per-object cap.");
    }

    private static ObjectExportResult Skipped(PmObject pmObject, string message) => new()
    {
        Object = pmObject.Name,
        Outcome = ObjectOutcome.Skipped,
        Message = message
    };

    private static ObjectExportResult Failed(PmObject pmObject, string message) => new()
    {
        Object = pmObject.Name,
        Outcome = ObjectOutcome.Failed,
        Message = message.Split('\n')[0]
    };
}
