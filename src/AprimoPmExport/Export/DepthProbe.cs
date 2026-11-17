using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>One request made by the depth probe, and what came back.</summary>
internal sealed record DepthSample(
    long Offset,
    int Limit,
    bool Ok,
    long Ms,
    int Items,
    string? FirstId,
    string? LastId,
    double? FirstKey,
    double? LastKey,
    string? FirstKeyText,
    bool OrderedWithinPage,
    int Attempts,
    long Bytes,
    string? Error);

internal enum DepthLevel { Ok, Note, Problem }

internal sealed record DepthFinding(DepthLevel Level, string Text);

/// <summary>
/// Reads deep into one collection to find where offset paging stops being trustworthy,
/// before a multi-hour export finds out the hard way.
///
/// <para>Offset paging over a large collection can fail three different ways, and each needs
/// a different answer. It can <b>error</b> past some depth (a 400, a timeout). It can
/// <b>quietly return the wrong thing</b> — the offset ignored or clamped, giving the same
/// page or an empty one with a perfectly healthy 200. Or it can <b>get slower the deeper it
/// goes</b>, which breaks nothing but turns a two-hour export into a two-day one. The probe
/// reads pages at increasing offsets and checks for all three.</para>
///
/// <para>It runs the production <see cref="HalPagedReader"/>, not a copy of its URL logic, so
/// what passes here is what the export will actually do: same endpoint, same sort, same
/// envelope handling.</para>
///
/// <para>It also tries larger page sizes. A server that silently caps <c>limit</c> returns
/// fewer rows than asked for, and the reader treats a short page as the end of the
/// collection — so a page size above the cap would stop an export after its first page,
/// reporting success.</para>
/// </summary>
public sealed class DepthProbe
{
    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly Action<string> _log;

    /// <summary>Offsets to read when none are given. Filtered to fit the collection.</summary>
    internal static readonly long[] DefaultDepths =
        { 0, 1_000, 10_000, 50_000, 100_000, 250_000, 400_000, 500_000 };

    /// <summary>Page sizes tried at offset 0, beyond the configured one.</summary>
    internal static readonly int[] LadderLimits = { 250, 500, 1000, 2500 };

    /// <summary>A deepest-versus-shallowest latency ratio at or above this is worth saying so.</summary>
    private const double LatencyGrowthRatio = 3.0;

    /// <summary>A read this many times slower than the median is treated as a hiccup, not a trend.</summary>
    private const double OutlierRatio = 2.0;

    public DepthProbe(ExportConfig config, ApiClient client, Action<string> log)
    {
        _config = config;
        _client = client;
        _log = log;
    }

    // ───────────────────────────── running ─────────────────────────────

    /// <summary>Returns true when the probe could read the collection at all.</summary>
    public async Task<bool> RunAsync(
        PmObject pmObject,
        IReadOnlyList<long>? depthsOverride,
        CancellationToken cancellationToken)
    {
        var paging = _config.Source.Paging;
        var retry = _config.Source.Retry;

        var savedPageSize = paging.PageSize;
        var savedMaxOffset = paging.MaxOffset;
        var savedAttempts = retry.MaxAttempts;

        try
        {
            // The runaway-offset guard exists to stop an export looping; a probe that is
            // trying to go deep must not be stopped by it.
            paging.MaxOffset = 0;

            // Two attempts, not five. The default would retry a failing deep request five
            // times with backoff, which can mean ten minutes spent confirming one failure.
            // One retry still rides out a blip.
            retry.MaxAttempts = Math.Min(retry.MaxAttempts, 2);

            return await RunCoreAsync(pmObject, depthsOverride, savedPageSize, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            paging.PageSize = savedPageSize;
            paging.MaxOffset = savedMaxOffset;
            retry.MaxAttempts = savedAttempts;
        }
    }

    private async Task<bool> RunCoreAsync(
        PmObject pmObject,
        IReadOnlyList<long>? depthsOverride,
        int baselineLimit,
        CancellationToken cancellationToken)
    {
        var sortProperty = _config.Source.SortField ?? pmObject.DefaultSortField ?? pmObject.IdProperty;
        var ascending = _config.Source.SortAscending;

        Console.WriteLine();
        Console.WriteLine("════════ Depth probe ════════");
        Console.WriteLine($"Object    : {pmObject.Name}   {pmObject.Path}");
        Console.WriteLine($"Sort      : {sortProperty} {(ascending ? "ascending" : "descending")}  " +
                          "(the same as an export)");
        Console.WriteLine($"Page size : {baselineLimit}");
        Console.WriteLine("Read-only. Each row below is one real request; times include rate-limit pacing.");

        // Warm-up. The first request pays for the token, DNS and TLS, which would make the
        // shallowest depth look slow and every ratio against it wrong. It is also the
        // cheapest way to learn _total.
        var (warm, total) = await ReadAsync(pmObject, 0, 1, sortProperty, ascending, cancellationToken,
                print: false)
            .ConfigureAwait(false);

        if (!warm.Ok)
        {
            Console.WriteLine();
            Console.WriteLine($"Could not read the first page at all: {warm.Error}");
            Console.WriteLine("Nothing deeper can be tested. Check --sample for this object first.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine(total is { } t
            ? $"Server _total: {t:N0}   ({t / (double)baselineLimit:N0} pages at {baselineLimit} per page)"
            : "Server _total: not reported — checks that need it are skipped.");

        var depths = ChooseDepths(total, baselineLimit, depthsOverride);
        var samples = new List<DepthSample>();

        Console.WriteLine();
        Console.WriteLine("── Depth (page size " + baselineLimit + ") ──");
        PrintHeader();

        foreach (var offset in depths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (sample, observedTotal) = await ReadAsync(
                pmObject, offset, baselineLimit, sortProperty, ascending, cancellationToken).ConfigureAwait(false);

            samples.Add(sample);
            total ??= observedTotal;
        }

        var ladder = LadderLimits.Where(l => l > baselineLimit).ToList();

        if (ladder.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("── Page size (offset 0) ──");
            PrintHeader();

            foreach (var limit in ladder)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (sample, _) = await ReadAsync(
                    pmObject, 0, limit, sortProperty, ascending, cancellationToken).ConfigureAwait(false);

                samples.Add(sample);
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 72));

        foreach (var finding in Analyze(samples, total, baselineLimit, ascending))
            Console.WriteLine($"  {Marker(finding.Level)} {finding.Text}");

        var projection = Project(samples, total, baselineLimit, _config.Throttle.RequestsPerSecond);

        if (projection is { } p)
        {
            Console.WriteLine();
            Console.WriteLine($"Paging alone would take about {FormatDuration(p.Seconds)} " +
                              $"({p.Requests:N0} requests at {baselineLimit} per page, " +
                              $"{_config.Throttle.RequestsPerSecond:0.##} req/s).");
            Console.WriteLine("That is an estimate from the depths measured above, assuming latency between");
            Console.WriteLine("them is linear. It excludes user-name lookups and anything else that is not paging.");
        }

        return true;
    }

    /// <summary>
    /// Reads one page through the production reader, positioned at <paramref name="offset"/>.
    /// Prints the row as it completes, so a slow request shows progress rather than silence.
    /// </summary>
    private async Task<(DepthSample Sample, long? Total)> ReadAsync(
        PmObject pmObject,
        long offset,
        int limit,
        string sortProperty,
        bool ascending,
        CancellationToken cancellationToken,
        bool print = true)
    {
        // The reader takes its page size from config, so the probe sets it per request.
        _config.Source.Paging.PageSize = limit;

        // The reader logs as it goes — when it meets a page-size cap, say. Written straight to
        // the console that lands in the middle of the row being printed, so it is held back and
        // shown beneath the row it belongs to.
        var notes = new List<string>();

        var reader = new HalPagedReader(_client, _config, pmObject, notes.Add,
            new PagingState { NextOffset = (int)Math.Min(offset, int.MaxValue) });

        var retriesBefore = _client.RetryCount;
        var bytesBefore = _client.BytesReceived;
        var stopwatch = Stopwatch.StartNew();

        if (print) Console.Write($"  {offset,10:N0}  {limit,6:N0}  ");

        void FlushNotes()
        {
            if (!print) return;
            foreach (var note in notes) Console.WriteLine("        ↳ " + note.Trim());
        }

        try
        {
            using var page = await reader.NextPageAsync(limit, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var items = page?.Items ?? (IReadOnlyList<JsonElement>)Array.Empty<JsonElement>();
            var sample = Describe(pmObject, offset, limit, items, sortProperty, ascending,
                stopwatch.ElapsedMilliseconds,
                (int)(_client.RetryCount - retriesBefore) + 1,
                _client.BytesReceived - bytesBefore);

            if (print) PrintRow(sample);
            FlushNotes();
            return (sample, page?.Total);
        }
        catch (ApiException ex)
        {
            var result = Failed(stopwatch, ex.StatusCode is { } code ? $"{(int)code}" : "error",
                ex.Message.Split('\n')[0], offset, limit, print);
            FlushNotes();
            return result;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(stopwatch, "TIMEOUT",
                $"no response within {_config.Source.RequestTimeoutSeconds}s", offset, limit, print);
        }
        catch (HttpRequestException ex)
        {
            return Failed(stopwatch, "network", ex.Message, offset, limit, print);
        }
    }

    private (DepthSample Sample, long? Total) Failed(
        Stopwatch stopwatch, string status, string error, long offset, int limit, bool print)
    {
        stopwatch.Stop();

        var sample = new DepthSample(offset, limit, false, stopwatch.ElapsedMilliseconds, 0,
            null, null, null, null, null, true, 1, 0, $"{status}: {error}");

        if (print)
            Console.WriteLine($"FAILED  {stopwatch.ElapsedMilliseconds,6:N0} ms   {Trim(sample.Error!, 90)}");

        return (sample, null);
    }

    private static DepthSample Describe(
        PmObject pmObject,
        long offset,
        int limit,
        IReadOnlyList<JsonElement> items,
        string sortProperty,
        bool ascending,
        long ms,
        int attempts,
        long bytes)
    {
        string? firstId = null, lastId = null, firstText = null;
        double? firstKey = null, lastKey = null;
        var ordered = true;
        double? previous = null;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = ReadText(item, pmObject.IdProperty);
            var (key, text) = ReadKey(item, sortProperty);

            if (i == 0) { firstId = id; firstKey = key; firstText = text; }
            if (i == items.Count - 1) { lastId = id; lastKey = key; }

            // Ties are fine: bulk-updated data produces runs of identical timestamps.
            if (key is { } k && previous is { } p && (ascending ? k < p : k > p)) ordered = false;
            if (key is not null) previous = key;
        }

        return new DepthSample(offset, limit, true, ms, items.Count, firstId, lastId, firstKey, lastKey,
            firstText, ordered, attempts, bytes, null);
    }

    // ───────────────────────────── analysis ─────────────────────────────

    /// <summary>
    /// Reads the samples for the three failure modes, and for a server that caps
    /// <c>limit</c>.
    ///
    /// <para>Pure, and separate from the I/O, because it is the part that decides what the
    /// probe tells you — and the part most worth being sure of.</para>
    /// </summary>
    internal static List<DepthFinding> Analyze(
        IReadOnlyList<DepthSample> samples,
        long? total,
        int baselineLimit,
        bool ascending)
    {
        var findings = new List<DepthFinding>();

        // Position samples probe depth at the real page size; the rest probe page size.
        var positions = samples.Where(s => s.Limit == baselineLimit).OrderBy(s => s.Offset).ToList();
        var ladder = samples.Where(s => s.Limit != baselineLimit).OrderBy(s => s.Limit).ToList();

        bool Inside(DepthSample s) => total is null || s.Offset + s.Limit <= total;
        bool PastEnd(DepthSample s) => total is { } t && s.Offset >= t;

        // ---- hard failures ----
        var failedInside = positions.Where(s => !s.Ok && Inside(s)).ToList();

        foreach (var failure in failedInside)
            findings.Add(new(DepthLevel.Problem,
                $"offset {failure.Offset:N0} failed: {failure.Error}"));

        if (failedInside.Count > 0)
        {
            var firstFail = failedInside.Min(s => s.Offset);
            var lastGood = positions.Where(s => s.Ok && s.Items > 0 && s.Offset < firstFail)
                                    .Select(s => (long?)s.Offset).DefaultIfEmpty(null).Max();

            findings.Add(new(DepthLevel.Problem,
                (lastGood is { } g
                    ? $"Offset paging works to at least {g:N0} and breaks before {firstFail:N0}. "
                    : $"Offset paging breaks at or before {firstFail:N0}. ") +
                "Nothing beyond that is reachable this way, so a full export needs another strategy."));
        }

        // ---- quiet wrong answers ----
        foreach (var s in positions.Where(s => s.Ok && Inside(s) && s.Items == 0))
            findings.Add(new(DepthLevel.Problem,
                $"offset {s.Offset:N0} returned NO items, though _total is {total:N0}. The server is " +
                "clamping or ignoring deep offsets without saying so — an export would stop here and " +
                "report success."));

        foreach (var s in positions.Where(s => s.Ok && Inside(s) && s.Items > 0 && s.Items < s.Limit))
            findings.Add(new(DepthLevel.Problem,
                $"offset {s.Offset:N0} returned {s.Items} of {s.Limit} items, mid-collection. The reader " +
                "reads a short page as the end, so an export would stop here."));

        // Offset ignored: the same page turns up at two offsets that should differ.
        var withItems = positions.Where(s => s.Ok && s.Items > 0 && s.FirstId is not null).ToList();

        foreach (var group in withItems.GroupBy(s => s.FirstId))
        {
            var apart = group.OrderBy(s => s.Offset).ToList();
            if (apart.Count < 2) continue;

            for (var i = 1; i < apart.Count; i++)
                if (apart[i].Offset - apart[0].Offset >= apart[i].Limit)
                    findings.Add(new(DepthLevel.Problem,
                        $"offset {apart[i].Offset:N0} returned the same first item as offset " +
                        $"{apart[0].Offset:N0} (id {group.Key}). The server is ignoring the offset."));
        }

        // ---- ordering ----
        foreach (var s in positions.Where(s => s.Ok && !s.OrderedWithinPage))
            findings.Add(new(DepthLevel.Problem,
                $"rows at offset {s.Offset:N0} are not in {(ascending ? "ascending" : "descending")} " +
                "order within the page. The sort is not being honoured there."));

        var keyed = withItems.Where(s => s.FirstKey is not null && s.LastKey is not null).ToList();

        for (var i = 1; i < keyed.Count; i++)
        {
            var before = keyed[i - 1];
            var after = keyed[i];
            var inOrder = ascending ? after.FirstKey >= before.LastKey : after.FirstKey <= before.LastKey;

            if (!inOrder)
                findings.Add(new(DepthLevel.Problem,
                    $"the page at offset {after.Offset:N0} starts {(ascending ? "before" : "after")} the " +
                    $"page at {before.Offset:N0} ended. Offsets are not walking the sort order, so rows " +
                    "would be skipped or repeated."));
        }

        // ---- the end ----
        // Two different places, and only one of them is ever asked for. After a full page the
        // reader requests offset == _total, so that is the boundary that matters; an offset
        // further out is something the reader never does, so a rejection there is harmless.
        // An earlier version probed _total + 1000, saw it rejected, and called that a risk —
        // which it was not, because the reader would never have gone there.
        foreach (var s in positions.Where(PastEnd))
        {
            var atEnd = total is { } t && s.Offset == t;

            if (atEnd)
            {
                if (!s.Ok)
                    findings.Add(new(DepthLevel.Problem,
                        $"offset {s.Offset:N0} — exactly _total — returned an error ({s.Error}). That is the " +
                        "request the reader makes after a full final page, so an export whose size is an " +
                        $"exact multiple of the page size (about one in {baselineLimit}) would fail on its " +
                        "last step."));
                else if (s.Items > 0)
                    findings.Add(new(DepthLevel.Problem,
                        $"offset {s.Offset:N0} — exactly _total — returned {s.Items} item(s). _total is " +
                        "lower than the collection, or the offset is wrapping."));
                else
                    findings.Add(new(DepthLevel.Ok,
                        $"offset {s.Offset:N0} — exactly _total — returned an empty page. The request the " +
                        "reader makes after a full final page is safe, and the end is detectable."));

                continue;
            }

            if (!s.Ok)
                findings.Add(new(DepthLevel.Note,
                    $"offset {s.Offset:N0}, beyond _total, is rejected ({Short(s.Error)}). The reader never " +
                    "requests that far, so this is harmless."));
            else if (s.Items > 0)
                findings.Add(new(DepthLevel.Problem,
                    $"offset {s.Offset:N0}, beyond _total, returned {s.Items} item(s). _total is " +
                    $"{total:N0}, so the collection is larger than it says or the offset is wrapping."));
            else
                findings.Add(new(DepthLevel.Ok,
                    $"offset {s.Offset:N0}, beyond _total, returned an empty page."));
        }

        // ---- page size ----
        foreach (var s in ladder)
        {
            var reachable = total is null || total >= s.Limit;

            if (!s.Ok)
                findings.Add(new(DepthLevel.Note, $"limit {s.Limit:N0} was rejected: {s.Error}"));
            else if (reachable && s.Items < s.Limit)
            {
                // The reader copes with a cap — it reads _total, sees rows remaining after a
                // short page, and drops to the size the server will give. So a cap is a note
                // when _total is there to tell it apart from a last page, and a real problem
                // when it is not, since then a capped short page looks exactly like the end.
                if (total is not null)
                    findings.Add(new(DepthLevel.Note,
                        $"limit {s.Limit:N0} returned only {s.Items:N0} items: the server caps the page size " +
                        $"at {s.Items:N0}, silently, with a 200. The reader notices this from _total and " +
                        "adapts, so it will not truncate an export — but it costs a wasted first request " +
                        $"per object and a log line. --page-size {s.Items:N0} avoids both."));
                else
                    findings.Add(new(DepthLevel.Problem,
                        $"limit {s.Limit:N0} returned only {s.Items:N0} items, so the server caps the page " +
                        $"size at about {s.Items:N0}. With no _total the reader cannot tell a capped page " +
                        "from the last one, and would stop after its first page without an error. Set " +
                        $"--page-size to {s.Items:N0} or less."));
            }
            else
                findings.Add(new(DepthLevel.Ok,
                    $"limit {s.Limit:N0} returned in full ({s.Items:N0} items, {s.Bytes / 1024.0:N0} KB, " +
                    $"{s.Ms:N0} ms)."));
        }

        var fullLadder = ladder.Where(s => s.Ok && (total is null || total >= s.Limit) && s.Items >= s.Limit)
                               .Select(s => s.Limit).DefaultIfEmpty(0).Max();

        if (fullLadder > baselineLimit && total is { } tot)
            findings.Add(new(DepthLevel.Note,
                $"The largest page size returned in full was {fullLadder:N0}: {Math.Ceiling(tot / (double)fullLadder):N0} " +
                $"pages instead of {Math.Ceiling(tot / (double)baselineLimit):N0}. That is only worth using " +
                "if it also holds up at depth — this only tested it at offset 0."));

        // ---- latency ----
        var shallow = positions.FirstOrDefault(s => s.Ok && s.Items > 0 && s.Offset == 0);
        var deepest = positions.Where(s => s.Ok && s.Items > 0).OrderByDescending(s => s.Offset).FirstOrDefault();

        if (shallow is not null && deepest is not null && deepest.Offset > 0)
        {
            // The median of the deepest few reads, not the single deepest. One slow read that
            // happened to land at the greatest offset would otherwise read as growth with
            // depth. With fewer than three deep reads there is nothing to take a median of,
            // so the deepest one stands.
            var deep = positions.Where(s => s.Ok && s.Items > 0 && s.Offset > 0).TakeLast(3).Select(s => s.Ms)
                                .OrderBy(m => m).ToList();
            var deepMs = deep.Count >= 3 ? deep[deep.Count / 2] : deepest.Ms;

            var ratio = shallow.Ms > 0 ? deepMs / (double)shallow.Ms : 0;

            if (ratio >= LatencyGrowthRatio)
                findings.Add(new(DepthLevel.Problem,
                    $"Latency grows with depth: {shallow.Ms:N0} ms at offset 0 against {deepMs:N0} ms around " +
                    $"{deepest.Offset:N0} (x{ratio:0.#}). Nothing fails, but each page costs more than the last, " +
                    "so total time grows faster than the row count."));
            else
                findings.Add(new(DepthLevel.Ok,
                    $"Latency is flat with depth: {shallow.Ms:N0} ms at offset 0, {deepMs:N0} ms around " +
                    $"{deepest.Offset:N0}."));
        }

        // A single slow read among otherwise steady ones is noise, not depth. Said out loud so
        // it is not mistaken for a trend — and capped in the projection below.
        var steady = positions.Where(s => s.Ok && s.Items > 0).Select(s => s.Ms).OrderBy(m => m).ToList();

        if (steady.Count >= 4)
        {
            var median = steady[steady.Count / 2];

            foreach (var s in positions.Where(s => s.Ok && s.Items > 0 && median > 0 &&
                                                   s.Ms > OutlierRatio * median))
                findings.Add(new(DepthLevel.Note,
                    $"offset {s.Offset:N0} took {s.Ms:N0} ms, {s.Ms / (double)median:0.#}x the median " +
                    $"({median:N0} ms). One slow read between faster ones at similar depths reads as a " +
                    "hiccup, not a trend; the time estimate caps it so it does not skew the projection."));
        }

        foreach (var s in samples.Where(s => s.Attempts > 1))
            findings.Add(new(DepthLevel.Note,
                $"offset {s.Offset:N0}, limit {s.Limit:N0} needed {s.Attempts} attempts — a retry means a " +
                "transient error, which is itself worth knowing at depth."));

        if (findings.All(f => f.Level != DepthLevel.Problem))
        {
            var reached = deepest?.Offset ?? 0;
            findings.Insert(0, new(DepthLevel.Ok,
                $"Every position read returned the expected rows, in order, with no repeats — down to " +
                $"offset {reached:N0}."));
        }

        return findings;
    }

    /// <summary>
    /// Estimates how long paging the whole collection would take, from the latencies measured
    /// at each depth.
    ///
    /// <para>Linear interpolation between measured offsets, flat beyond the deepest one. Each
    /// request costs the larger of its latency and the rate-limit interval, since requests run
    /// one at a time and the limiter spaces their starts.</para>
    /// </summary>
    internal static (double Seconds, long Requests)? Project(
        IReadOnlyList<DepthSample> samples,
        long? total,
        int pageSize,
        double requestsPerSecond)
    {
        if (total is not { } t || t <= 0 || pageSize <= 0) return null;

        var measured = samples.Where(s => s.Limit == pageSize && s.Ok && s.Items > 0)
                              .OrderBy(s => s.Offset)
                              .ToList();

        if (measured.Count == 0) return null;

        // One slow read between steady ones would otherwise be stretched, by interpolation,
        // across everything on either side of it. Cap each at a multiple of the median so a
        // hiccup stays a hiccup. With fewer than four reads there is no median worth trusting.
        var ordered = measured.Select(s => s.Ms).OrderBy(m => m).ToList();
        var ceiling = ordered.Count >= 4 ? ordered[ordered.Count / 2] * OutlierRatio : double.MaxValue;

        var points = measured.Select(s => (Offset: (double)s.Offset, Ms: Math.Min((double)s.Ms, ceiling)))
                             .ToList();

        var minCycleMs = requestsPerSecond > 0 ? 1000.0 / requestsPerSecond : 0;
        var pages = (long)Math.Ceiling(t / (double)pageSize);
        var totalMs = 0.0;

        for (long page = 0; page < pages; page++)
        {
            var offset = (double)page * pageSize;
            totalMs += Math.Max(Interpolate(points, offset), minCycleMs);
        }

        return (totalMs / 1000.0, pages);
    }

    private static double Interpolate(List<(double Offset, double Ms)> points, double offset)
    {
        if (offset <= points[0].Offset) return points[0].Ms;
        if (offset >= points[^1].Offset) return points[^1].Ms;

        for (var i = 1; i < points.Count; i++)
        {
            if (offset > points[i].Offset) continue;

            var (x0, y0) = points[i - 1];
            var (x1, y1) = points[i];
            return x1 == x0 ? y1 : y0 + (y1 - y0) * (offset - x0) / (x1 - x0);
        }

        return points[^1].Ms;
    }

    // ───────────────────────────── choosing and parsing ─────────────────────────────

    /// <summary>
    /// The offsets to read: the defaults that fit inside the collection, plus the last full
    /// page and one past the end, which are the two places an off-by-one would hide.
    /// </summary>
    internal static List<long> ChooseDepths(long? total, int limit, IReadOnlyList<long>? requested)
    {
        var depths = new SortedSet<long> { 0 };

        if (requested is { Count: > 0 })
        {
            foreach (var depth in requested) depths.Add(depth);
            return depths.ToList();
        }

        foreach (var depth in DefaultDepths)
            if (total is null || depth + limit <= total) depths.Add(depth);

        if (total is { } t)
        {
            depths.Add(Math.Max(0, t - limit));   // The last full page.
            depths.Add(t);                        // Exactly the end: what the reader asks for after a full last page.
            depths.Add(t + 1);                    // One beyond, to learn whether the end is inclusive.
        }

        return depths.ToList();
    }

    /// <summary>Parses a comma/semicolon/space separated list of offsets. Underscores are allowed.</summary>
    internal static (List<long> Valid, List<string> Rejected) ParseDepths(string text)
    {
        var valid = new List<long>();
        var rejected = new List<string>();

        foreach (var raw in text.Split(new[] { ',', ';', ' ' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Replace("_", "");

            if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
                value <= int.MaxValue)
            {
                if (!valid.Contains(value)) valid.Add(value);
            }
            else
            {
                rejected.Add(raw);
            }
        }

        return (valid, rejected);
    }

    // ───────────────────────────── reading and printing ─────────────────────────────

    private static string? ReadText(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>
    /// A sort key as a number — a timestamp as ticks, a number as itself — plus its text for
    /// display. Null when the value is neither, which skips the ordering checks for it rather
    /// than guessing at one.
    /// </summary>
    private static (double? Key, string? Text) ReadKey(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value))
            return (null, null);

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return (value.TryGetDouble(out var number) ? number : null, value.GetRawText());

            case JsonValueKind.String:
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                    return (when.UtcTicks, text);

                return (null, text);

            default:
                return (null, null);
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine($"  {"OFFSET",10}  {"LIMIT",6}  {"RESULT",-7} {"MS",8}  {"KB",6}  {"ITEMS",6}  " +
                          $"{"FIRST ID",-10} {"LAST ID",-10} FIRST SORT KEY");
    }

    private static void PrintRow(DepthSample s)
    {
        Console.WriteLine($"{(s.Items == 0 ? "empty" : "ok"),-7} {s.Ms,8:N0}  {s.Bytes / 1024.0,6:N0}  " +
                          $"{s.Items,6:N0}  {Trim(s.FirstId ?? "-", 10),-10} {Trim(s.LastId ?? "-", 10),-10} " +
                          $"{Trim(s.FirstKeyText ?? "-", 26)}" +
                          (s.Attempts > 1 ? $"   ({s.Attempts} attempts)" : ""));
    }

    private static string Marker(DepthLevel level) => level switch
    {
        DepthLevel.Ok => "OK     ",
        DepthLevel.Note => "NOTE   ",
        _ => "PROBLEM"
    };

    /// <summary>The first sentence of an error, without the status-code boilerplate around it.</summary>
    private static string Short(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "no detail";

        // The API puts its real reason in a JSON body: {"errors":[{"message":"..."}]}. Pull it
        // out when it is there, since the text in front of it is generic advice about sortField.
        var marker = "\"message\":\"";
        var start = error.IndexOf(marker, StringComparison.Ordinal);

        if (start >= 0)
        {
            start += marker.Length;
            var end = error.IndexOf('"', start);
            if (end > start) return Trim(error.Substring(start, end - start), 160);
        }

        return Trim(error, 160);
    }

    internal static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours"
             : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} minutes"
             : $"{span.TotalSeconds:0} seconds";
    }

    private static string Trim(string value, int max)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single.Substring(0, max - 1) + "…";
    }
}
