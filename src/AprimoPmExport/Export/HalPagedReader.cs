using System.Globalization;
using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>One page of items. Owns the parsed document; dispose after processing.</summary>
public sealed class ItemPage : IDisposable
{
    private readonly JsonDocument _document;

    /// <summary>Items in this page, after de-duplication.</summary>
    public IReadOnlyList<JsonElement> Items { get; }

    /// <summary>Items the API returned, before de-duplication.</summary>
    public int RawCount { get; }

    /// <summary>Server-reported total from <c>_total</c>, when present. ETA only.</summary>
    public long? Total { get; }

    public int PageNumber { get; }

    /// <summary>The <c>_embedded</c> key the items were found under, for diagnostics.</summary>
    public string? EmbedKey { get; }

    internal ItemPage(
        JsonDocument document,
        IReadOnlyList<JsonElement> items,
        int rawCount,
        long? total,
        int pageNumber,
        string? embedKey)
    {
        _document = document;
        Items = items;
        RawCount = rawCount;
        Total = total;
        PageNumber = pageNumber;
        EmbedKey = embedKey;
    }

    public void Dispose() => _document.Dispose();
}

/// <summary>Serialisable paging position, persisted to the checkpoint file.</summary>
public sealed class PagingState
{
    public int PagesFetched { get; set; }
    public int NextOffset { get; set; }

    /// <summary>Next link from the HAL envelope, when paging by link rather than offset.</summary>
    public string? NextLink { get; set; }

    /// <summary>IDs already written, so a resumed or shifted collection does not duplicate them.</summary>
    public List<string> SeenIds { get; set; } = new();

    /// <summary>True once de-duplication was abandoned for memory; recorded so a resume knows.</summary>
    public bool DeduplicationDisabled { get; set; }
}

/// <summary>
/// Reads one PM collection page by page.
///
/// <para><b>The envelope.</b> Every paged endpoint returns HAL:</para>
/// <code>
///   { "_total": 1234,
///     "_embedded": { "&lt;resourceTypeName&gt;": [ … ] },
///     "_links":    { "self": {...}, "next": {...}, … } }
/// </code>
/// <para>The spec types <c>_embedded</c> as <c>additionalProperties</c> "keyed by the
/// resource type name" but never says what that string is for any object, so the reader
/// does not hard-code it: it takes the sole array under <c>_embedded</c>. Setting
/// <see cref="PmObject.EmbedKey"/> once you have seen a real response switches on a
/// strict check instead.</para>
///
/// <para><b>Paging.</b> <c>offset</c> + <c>limit</c>, or following <c>_links.next</c>.
/// There is no keyset or cursor mode here — unlike the DAM API, PM offers no query
/// language in which a watermark predicate could be expressed, so deep paging has no
/// escape hatch beyond a smaller working set. <see cref="PagingConfig.MaxOffset"/> is the
/// backstop against an endpoint that ignores <c>offset</c> and would otherwise return
/// page 1 forever.</para>
/// </summary>
public sealed class HalPagedReader
{
    private readonly ApiClient _client;
    private readonly ExportConfig _config;
    private readonly PmObject _object;
    private readonly Action<string> _log;
    private readonly PagingState _state;

    private readonly HashSet<string> _seenIds;
    private bool _deduplicating;

    /// <summary>
    /// The page size the server was seen to cap at, once it has been. Not persisted in the
    /// checkpoint: a resumed run relearns it from its first page, at the cost of one request.
    /// </summary>
    private int? _learnedCap;

    /// <summary>The server's observed page-size cap, or null if it has not shown one.</summary>
    public int? LearnedPageSizeCap => _learnedCap;

    /// <summary>Set when the caller decided the remaining pages are not wanted.</summary>
    private string? _stopReason;

    public bool HasMore { get; private set; } = true;
    public long? LastTotal { get; private set; }
    public string? StopReason => _stopReason;
    public PagingState State => Snapshot();

    /// <summary>Items dropped because their ID had already been written.</summary>
    public int DuplicatesDropped { get; private set; }

    /// <summary>
    /// Server-side filter appended to every request, when the object declares a
    /// <see cref="PmObject.ModifiedAfterParam"/>. Only <c>/review-tasks</c> has one.
    /// </summary>
    public DateTimeOffset? ModifiedAfter { get; set; }

    /// <summary>
    /// Sort field for this reader, overriding both the object's default and
    /// <see cref="SourceConfig.SortField"/>.
    ///
    /// <para>Set by a delta run so the collection is ordered by the property the window
    /// compares — sorting by anything else would make an early stop meaningless.</para>
    /// </summary>
    public string? SortFieldOverride { get; set; }

    /// <summary>Sort direction for this reader, overriding <see cref="SourceConfig.SortAscending"/>.</summary>
    public bool? SortAscendingOverride { get; set; }

    public HalPagedReader(
        ApiClient client,
        ExportConfig config,
        PmObject pmObject,
        Action<string> log,
        PagingState? resumeFrom = null)
    {
        _client = client;
        _config = config;
        _object = pmObject;
        _log = log;

        _state = resumeFrom ?? new PagingState();
        _seenIds = new HashSet<string>(_state.SeenIds, StringComparer.OrdinalIgnoreCase);
        _deduplicating = config.Limits.DeduplicateById && !_state.DeduplicationDisabled;
    }

    /// <summary>Ends paging after the current page. Used when a delta window is exhausted.</summary>
    public void Stop(string reason)
    {
        _stopReason = reason;
        HasMore = false;
    }

    /// <summary>
    /// Fetches the next page, or null when exhausted.
    /// </summary>
    /// <param name="takeLimit">
    /// Upper bound on this page's size. The runner passes the remaining row budget so the
    /// final request under a cap does not over-fetch.
    /// </param>
    public async Task<ItemPage?> NextPageAsync(int takeLimit, CancellationToken cancellationToken)
    {
        if (!HasMore) return null;

        var paging = _config.Source.Paging;

        if (paging.MaxPages > 0 && _state.PagesFetched >= paging.MaxPages)
        {
            Stop($"reached Source.Paging.MaxPages ({paging.MaxPages})");
            _log($"  Stopping: {_stopReason}.");
            return null;
        }

        if (paging.MaxOffset > 0 && _state.NextOffset > paging.MaxOffset)
            throw new ApiException(
                $"{_object.Name}: paging passed offset {_state.NextOffset:N0} without finishing, over the " +
                $"Source.Paging.MaxOffset guard of {paging.MaxOffset:N0}.\n" +
                "  Either the collection really is that large — raise Source.Paging.MaxOffset — or the " +
                "endpoint is ignoring 'offset' and returning the same page repeatedly, which would loop " +
                "forever without this check. Compare two pages' IDs with --sample to tell which.");

        var take = Math.Min(paging.PageSize, Math.Max(1, takeLimit));

        // Once the server has shown it caps the page size, ask for what it will give.
        if (_learnedCap is { } cap) take = Math.Min(take, cap);

        var pageNumber = _state.PagesFetched + 1;

        var url = BuildUrl(take);

        var document = await _client
            .SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url),
                           $"{_object.Name} page {pageNumber}", cancellationToken)
            .ConfigureAwait(false);

        // The page takes ownership of the document, so it is only disposed here if we
        // fail before handing it over. Items are JsonElements into its buffer.
        var keepDocument = false;
        try
        {
            var root = document.RootElement;

            var envelope = HalEnvelopeReader.Read(root, _object.EmbedKey);

            if (envelope.Total is { } total) LastTotal = total;

            ThrowIfUnusable(envelope, root, pageNumber);

            if (envelope.EmbedKeyMismatch)
                _log($"  {_object.Name} page {pageNumber}: '_embedded' has no '{_object.EmbedKey}', but it " +
                     $"holds exactly one array ('{envelope.EmbedKey}'), so that is what was read. " +
                     $"Update EmbedKey in objects.json to '{envelope.EmbedKey}'.");

            if (envelope.AvailableKeys.Count > 1 && _object.EmbedKey is null)
                _log($"  {_object.Name} page {pageNumber}: '_embedded' holds " +
                     $"{envelope.AvailableKeys.Count} arrays ({string.Join(", ", envelope.AvailableKeys)}); " +
                     $"using '{envelope.EmbedKey}'. Set EmbedKey in objects.json to pin the right one.");

            var items = envelope.Items;
            var embedKey = envelope.EmbedKey;

            // An empty collection yields an undefined element, which has no array length
            // to ask for.
            var rawCount = envelope.Count;
            var kept = new List<JsonElement>(rawCount);

            if (rawCount > 0)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null) continue;

                    if (_deduplicating)
                    {
                        var id = ReadProperty(item, _object.IdProperty);
                        if (id is not null && !_seenIds.Add(id))
                        {
                            DuplicatesDropped++;
                            continue;
                        }
                    }

                    kept.Add(item);
                }

                CheckDeduplicationBudget();
            }

            _state.PagesFetched = pageNumber;
            _state.NextLink = envelope.NextLink;
            _state.NextOffset += rawCount;

            // How many rows the server says are still unread, when it says.
            var remaining = LastTotal is { } known ? known - _state.NextOffset : (long?)null;

            if (rawCount == 0)
            {
                // An empty page is the end however the total reads.
                HasMore = false;
            }
            else if (rawCount < take)
            {
                // A short page normally means the last one. But not when _total says more
                // rows remain: then the server is capping the page size, and treating the
                // short page as the end would stop the export after one page and report
                // success. Measured on a live tenant: asking for 500, 1,000 or 2,500 items
                // all returned exactly 250, with a 200 and no warning.
                if (remaining is > 0)
                {
                    if (_learnedCap is null)
                    {
                        _learnedCap = rawCount;
                        _log($"  {_object.Name}: asked for {take:N0} items and got {rawCount:N0}, but " +
                             $"_total says {remaining:N0} more remain. The server caps the page size at " +
                             $"{rawCount:N0}, so that is what will be requested from here on. " +
                             $"(--page-size {rawCount} avoids this on the first page.)");
                    }
                }
                else
                {
                    HasMore = false;
                }
            }
            else if (paging.Mode == PagingMode.LinkFollow && _state.NextLink is null)
            {
                // A full page with no 'next' link. Under LinkFollow that would be the
                // documented end of the collection, but no example in the spec shows a
                // populated 'next' anywhere, so an endpoint that never emits one would
                // look finished after page 1. Carry on by offset rather than silently
                // truncating the export.
                _log($"  {_object.Name}: a full page arrived with no '_links.next'. Continuing by offset " +
                     "instead — set Source.Paging.Mode to Offset to silence this.");
            }

            keepDocument = true;
            return new ItemPage(document, kept, rawCount, LastTotal, pageNumber, embedKey);
        }
        finally
        {
            if (!keepDocument) document.Dispose();
        }
    }

    /// <summary>
    /// Turns an unreadable envelope into an actionable error.
    ///
    /// <para>Both failures here mean the same thing operationally — nothing can be
    /// exported from this response — but they have opposite fixes, so they are reported
    /// separately rather than as one generic "unexpected shape".</para>
    /// </summary>
    private void ThrowIfUnusable(HalEnvelope envelope, JsonElement root, int pageNumber)
    {
        switch (envelope.Status)
        {
            case HalEnvelopeStatus.NotAnEnvelope:
                throw new ApiException(
                    $"{_object.Name} page {pageNumber}: the response has no '_embedded' object, so there " +
                    "is nothing to export.\n" +
                    $"  Received keys: {HalEnvelopeReader.DescribeKeys(root)}.\n" +
                    "  Every paged PM endpoint is documented as returning { _total, _embedded, _links }. " +
                    "If this endpoint returns a bare array or a differently shaped envelope on your " +
                    "tenant, that object needs its own handling — report the keys above.");

            case HalEnvelopeStatus.EmbedKeyNotFound:
                throw new ApiException(
                    $"{_object.Name} page {pageNumber}: '_embedded' has no array under the configured " +
                    $"EmbedKey '{_object.EmbedKey}'. Present: " +
                    (envelope.AvailableKeys.Count == 0
                        ? "(no arrays)"
                        : string.Join(", ", envelope.AvailableKeys)) + ".\n" +
                    "  Clear EmbedKey in objects.json to let the reader take whichever array is there.");
        }
    }

    private string BuildUrl(int take)
    {
        // Follow the server's own link when we have one and were asked to.
        if (_config.Source.Paging.Mode == PagingMode.LinkFollow &&
            _state.NextLink is { Length: > 0 } link)
            return Uri.IsWellFormedUriString(link, UriKind.Absolute)
                ? link
                : _client.Url(link.StartsWith('/') ? link : "/" + link);

        var query = new List<string>
        {
            "offset=" + _state.NextOffset.ToString(CultureInfo.InvariantCulture),
            "limit=" + take.ToString(CultureInfo.InvariantCulture)
        };

        var sortField = SortFieldOverride ?? _config.Source.SortField ?? _object.DefaultSortField;
        if (!string.IsNullOrWhiteSpace(sortField))
        {
            // Note the API's own convention: a separate boolean, not DAM's '-Field'.
            var ascending = SortAscendingOverride ?? _config.Source.SortAscending;
            query.Add("sortField=" + Uri.EscapeDataString(sortField));
            query.Add("sortAscending=" + (ascending ? "true" : "false"));
        }

        // The one server-side changed-since filter in the spec.
        if (ModifiedAfter is { } after && _object.ModifiedAfterParam is { Length: > 0 } param)
            query.Add($"{Uri.EscapeDataString(param)}=" +
                      Uri.EscapeDataString(after.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

        foreach (var kvp in _object.ExtraQuery)
            query.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");

        return _client.Url(_object.Path) + "?" + string.Join("&", query);
    }

    /// <summary>
    /// Abandons de-duplication once the ID set grows past its budget.
    ///
    /// <para>De-duplication guards against offset paging over a collection that shifts
    /// mid-run, which is a real risk here: PM has no immutable-watermark paging mode to
    /// fall back on. But the ID set is unbounded by nature, so it gets a ceiling. Passing
    /// it degrades to possible duplicate rows, which is recoverable downstream — running
    /// out of memory partway through is not.</para>
    /// </summary>
    private void CheckDeduplicationBudget()
    {
        if (!_deduplicating) return;
        if (_seenIds.Count <= _config.Limits.MaxDeduplicationIds) return;

        _deduplicating = false;
        _state.DeduplicationDisabled = true;
        _seenIds.Clear();

        _log($"  {_object.Name}: passed {_config.Limits.MaxDeduplicationIds:N0} tracked IDs, so " +
             "de-duplication is now off for this object to bound memory. Duplicate rows are possible " +
             $"from here on — de-duplicate on {_object.IdProperty} downstream, or raise " +
             "Limits.MaxDeduplicationIds.");
    }

    private static string? ReadProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private PagingState Snapshot() => new()
    {
        PagesFetched = _state.PagesFetched,
        NextOffset = _state.NextOffset,
        NextLink = _state.NextLink,
        // Only persisted while de-duplication is still active; once abandoned the list
        // would be both huge and useless.
        SeenIds = _deduplicating ? _seenIds.ToList() : new List<string>(),
        DeduplicationDisabled = _state.DeduplicationDisabled
    };
}
