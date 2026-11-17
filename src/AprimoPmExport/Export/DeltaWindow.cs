using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprimoPmExport.Configuration;

namespace AprimoPmExport.Export;

/// <summary>
/// A half-open <c>[Since, Until)</c> time window.
///
/// <para>Windows are chained rather than derived from the data: the next run starts where
/// this one ended. That is gap-free by construction, and immune to items being edited
/// mid-run — those simply land in the following window. Deriving the next start from the
/// highest timestamp actually seen would instead lose anything stamped below that maximum
/// while the run was in flight.</para>
///
/// <para>Unlike the DAM utility, this window is <b>not</b> a query predicate. PM has no
/// search-expression language, so nothing can be pushed to the server (bar the single
/// <c>modifiedAfterDate</c> parameter on <c>/review-tasks</c>). The window is applied by
/// <see cref="DeltaFilter"/> to rows as they arrive.</para>
/// </summary>
public sealed class DeltaWindow
{
    public DateTimeOffset Since { get; }
    public DateTimeOffset Until { get; }

    /// <summary>True when <see cref="Since"/> came from the persisted high-water mark.</summary>
    public bool FromState { get; }

    /// <summary>How the window was requested, for logging.</summary>
    public string Origin { get; }

    public DeltaWindow(DateTimeOffset since, DateTimeOffset until, bool fromState, string origin)
    {
        if (until <= since)
            throw new ArgumentException(
                $"Delta window is empty or inverted: {Format(since)} to {Format(until)}. " +
                "Check --since / --until, or use --reset-delta if a saved mark is in the future.");

        Since = since;
        Until = until;
        FromState = fromState;
        Origin = origin;
    }

    public TimeSpan Duration => Until - Since;

    /// <summary>Half-open, so adjacent windows neither overlap nor leave a gap.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Since && instant < Until;

    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public string Describe() =>
        $"{Format(Since)} .. {Format(Until)} ({Duration.TotalHours:F1}h, {Origin})";
}

/// <summary>
/// Applies a <see cref="DeltaWindow"/> to items of one object, and decides when paging
/// may stop early.
///
/// <para>This is where PM diverges most from the DAM utility. There the window is a
/// server-side predicate, so the API returns only matching records and paging simply runs
/// to the end. Here every row has to be tested locally, and the only way to avoid reading
/// the whole collection every run is to sort by the timestamp descending and stop once
/// the rows are older than the window.</para>
///
/// <para>That shortcut is only safe if <c>sortField</c> was actually honoured. If the API
/// quietly ignores it, the rows arrive in the server's own order, an early page can hold
/// old rows, and stopping there would silently truncate the export. So the filter watches
/// the order of the values it receives: the moment they are not descending, the early stop
/// is withdrawn and the run degrades to a full scan. Being slow is recoverable; being
/// quietly incomplete is not.</para>
/// </summary>
public sealed class DeltaFilter
{
    private readonly DeltaWindow _window;
    private readonly PmObject _object;
    private readonly string _property;
    private readonly DeltaConfig _config;
    private readonly Action<string> _log;

    private DateTimeOffset? _previousValue;
    private bool _sortConfirmedDescending;
    private bool _sortDisproved;
    private bool _warnedUnparseable;

    /// <summary>Rows tested.</summary>
    public long Examined { get; private set; }

    /// <summary>Rows inside the window.</summary>
    public long Matched { get; private set; }

    /// <summary>Rows whose timestamp could not be read at all.</summary>
    public long Unparseable { get; private set; }

    /// <summary>Rows older than the window (the ones an early stop is looking for).</summary>
    public long BeforeWindow { get; private set; }

    /// <summary>Rows newer than the window — normal when a window has an explicit --until.</summary>
    public long AfterWindow { get; private set; }

    /// <summary>True once the sort order has been shown not to be descending.</summary>
    public bool SortDisproved => _sortDisproved;

    public DeltaFilter(
        DeltaWindow window,
        PmObject pmObject,
        DeltaConfig config,
        Action<string> log)
    {
        _window = window;
        _object = pmObject;
        _config = config;
        _log = log;

        _property = config.Property ?? pmObject.ModifiedProperty
            ?? throw new InvalidOperationException(
                $"Object '{pmObject.Name}' has no modified-date property, so it cannot be exported " +
                "incrementally. Set Source.Delta.Property to name one, or export it in full.");
    }

    public string Property => _property;

    /// <summary>True when the item falls inside the window and should be written.</summary>
    public bool Accept(JsonElement item)
    {
        Examined++;

        var value = ReadInstant(item);

        if (value is null)
        {
            Unparseable++;

            if (!_warnedUnparseable)
            {
                _warnedUnparseable = true;
                _log($"  {_object.Name}: an item has no readable '{_property}' value. Items without a " +
                     "timestamp cannot be placed in the window, so they are excluded from this delta. " +
                     "Export the object in full if they matter.");
            }

            return false;
        }

        TrackSortOrder(value.Value);

        if (value.Value < _window.Since) { BeforeWindow++; return false; }
        if (value.Value >= _window.Until) { AfterWindow++; return false; }

        Matched++;
        return true;
    }

    /// <summary>
    /// Confirms — or disproves — that the API really is returning rows newest-first.
    ///
    /// <para>One out-of-order pair is enough to disprove it. Two consecutive rows sharing
    /// a timestamp prove nothing either way, which is common on bulk-updated data, so ties
    /// are ignored rather than counted as confirmation.</para>
    /// </summary>
    private void TrackSortOrder(DateTimeOffset value)
    {
        if (_previousValue is { } previous)
        {
            if (value > previous)
            {
                if (!_sortDisproved)
                {
                    _sortDisproved = true;
                    _log($"  {_object.Name}: rows are not arriving newest-first — saw " +
                         $"{DeltaWindow.Format(previous)} then {DeltaWindow.Format(value)}. " +
                         $"'sortField={_property}' does not appear to be honoured here, so paging will " +
                         "read the whole collection rather than stopping early. The export stays correct; " +
                         "it just costs more requests.");
                }
            }
            else if (value < previous)
            {
                _sortConfirmedDescending = true;
            }
        }

        _previousValue = value;
    }

    /// <summary>
    /// True when paging may stop: every row on the page was older than the window, and the
    /// descending order that makes that conclusive has actually been observed.
    /// </summary>
    public bool CanStopAfterPage(int rowsOnPage, int rowsBeforeWindowOnPage)
    {
        if (_config.Strategy != DeltaStrategy.SortedDescending) return false;
        if (rowsOnPage == 0) return false;
        if (rowsBeforeWindowOnPage < rowsOnPage) return false;

        if (_sortDisproved) return false;

        // With verification on, an early stop needs positive evidence of descending
        // order, not merely the absence of contrary evidence. A single-row page, or a
        // page where every timestamp is identical, proves nothing.
        if (_config.SortVerification && !_sortConfirmedDescending) return false;

        return true;
    }

    /// <summary>
    /// Reads the object's timestamp property, handling both shapes PM uses: an ISO-8601
    /// string on almost everything, and an epoch number on Budget.
    /// </summary>
    public DateTimeOffset? ReadInstant(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty(_property, out var value)) return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when _object.ModifiedIsEpoch:
                if (!value.TryGetInt64(out var epoch)) return null;
                return FromEpoch(epoch, _object.ModifiedIsEpochMilliseconds);

            case JsonValueKind.Number:
                // A number where a string was expected. Treat it as an epoch anyway
                // rather than losing the row: the registry flag may just be unset.
                return value.TryGetInt64(out var guessed) ? FromEpoch(guessed, null) : null;

            case JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;

                return DateTimeOffset.TryParse(
                    text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Converts an epoch value. When the unit is not declared it is inferred by magnitude:
    /// a seconds value for any plausible date is far below the millisecond threshold, so
    /// the two ranges do not overlap in practice.
    /// </summary>
    internal static DateTimeOffset FromEpoch(long value, bool? milliseconds)
    {
        var isMilliseconds = milliseconds ?? Math.Abs(value) > 100_000_000_000L;

        return isMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    public string Summarize() =>
        $"{Matched:N0} in window, {BeforeWindow:N0} older, {AfterWindow:N0} newer, " +
        $"{Unparseable:N0} undated (of {Examined:N0} examined)";
}

/// <summary>
/// Parses <c>--since</c> / <c>--until</c> specifications into a <see cref="DeltaWindow"/>.
///
/// <para>Accepted forms: <c>last</c> (resume from the saved mark), <c>yesterday</c>,
/// <c>today</c>, a relative span such as <c>1d</c> / <c>36h</c> / <c>90m</c>, a date
/// (<c>2026-08-05</c>), or a full instant (<c>2026-08-05T04:00:00Z</c>).</para>
///
/// <para>All boundaries are UTC. A bare date therefore means a UTC day, not a local one;
/// pass an explicit instant if you need local-midnight boundaries.</para>
/// </summary>
public static class DeltaWindowParser
{
    public static DeltaWindow Create(
        string sinceSpec,
        string? untilSpec,
        DeltaConfig config,
        DeltaState state,
        DateTimeOffset now,
        Action<string> log)
    {
        var overlap = TimeSpan.FromMinutes(Math.Max(0, config.OverlapMinutes));

        // The upper bound defaults to "now", so the window closes at this run's start.
        var until = untilSpec is null ? now : ParseInstant(untilSpec, now, "--until", endOfDay: true);

        DateTimeOffset since;
        var fromState = false;
        var origin = sinceSpec;

        if (sinceSpec.Equals("last", StringComparison.OrdinalIgnoreCase) ||
            sinceSpec.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            if (state.HighWaterMarkUtc is not { } mark)
                throw new InvalidOperationException(
                    "--since last was requested but no previous run is recorded for this tenant and object. " +
                    "Seed the first window explicitly, e.g. --since 1d or --since 2026-08-01, " +
                    "then later runs can use --since last.");

            since = mark;
            fromState = true;
            origin = $"resumed from saved mark, {overlap.TotalMinutes:F0}m overlap";
        }
        else if (sinceSpec.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            since = startOfToday.AddDays(-1);
            // "yesterday" means the whole UTC day unless an explicit end was given.
            if (untilSpec is null) until = startOfToday;
            origin = "yesterday (UTC day)";
        }
        else if (sinceSpec.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            since = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            origin = "today (UTC, so far)";
        }
        else if (TryParseSpan(sinceSpec, out var span))
        {
            since = now - span;
            origin = $"last {sinceSpec}";
        }
        else
        {
            since = ParseInstant(sinceSpec, now, "--since", endOfDay: false);

            // A bare date with no explicit end means that single UTC day.
            if (untilSpec is null && IsBareDate(sinceSpec))
            {
                until = since.AddDays(1);
                origin = $"{sinceSpec} (UTC day)";
            }
        }

        // Overlap re-reads a little of the previous window, guarding against the API
        // stamping timestamps from a clock slightly behind ours. Costs a few duplicate
        // rows; set Source.Delta.OverlapMinutes to 0 for exact adjacency.
        if (fromState && overlap > TimeSpan.Zero)
            since -= overlap;

        var window = new DeltaWindow(since, until, fromState, origin);

        if (config.WarnIfWindowExceedsDays > 0 &&
            window.Duration > TimeSpan.FromDays(config.WarnIfWindowExceedsDays))
            log($"Warning: the delta window spans {window.Duration.TotalDays:F1} days. " +
                "If this is unintended (a long gap since the last run, or a mistyped --since), " +
                "the export may be far larger than a normal daily delta.");

        return window;
    }

    private static bool IsBareDate(string spec) =>
        DateTime.TryParseExact(spec, new[] { "yyyy-MM-dd", "yyyy/MM/dd" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static DateTimeOffset ParseInstant(string spec, DateTimeOffset now, string option, bool endOfDay)
    {
        if (spec.Equals("now", StringComparison.OrdinalIgnoreCase)) return now;

        if (IsBareDate(spec))
        {
            var date = DateTime.ParseExact(spec, new[] { "yyyy-MM-dd", "yyyy/MM/dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            var midnight = new DateTimeOffset(date, TimeSpan.Zero);
            // For --until a bare date means the end of that day, so the day is included.
            return endOfDay ? midnight.AddDays(1) : midnight;
        }

        if (DateTimeOffset.TryParse(spec, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        if (TryParseSpan(spec, out var span)) return now - span;

        throw new ArgumentException(
            $"{option}: could not understand '{spec}'. Expected 'last', 'yesterday', 'today', 'now', " +
            "a span like 1d / 36h / 90m, a date like 2026-08-05, or an instant like 2026-08-05T04:00:00Z.");
    }

    private static bool TryParseSpan(string spec, out TimeSpan span)
    {
        span = default;
        if (spec.Length < 2) return false;

        var unit = char.ToLowerInvariant(spec[^1]);
        if (unit is not ('d' or 'h' or 'm')) return false;

        if (!double.TryParse(spec.AsSpan(0, spec.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return false;

        span = unit switch
        {
            'd' => TimeSpan.FromDays(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromMinutes(amount)
        };

        return true;
    }
}

/// <summary>Persisted delta position for one tenant + object lineage.</summary>
public sealed class DeltaState
{
    public DateTimeOffset? HighWaterMarkUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public long LastRunRows { get; set; }
    public long TotalRows { get; set; }
    public int RunCount { get; set; }

    /// <summary>Object this entry belongs to. Written for readability; the key is authoritative.</summary>
    public string? Object { get; set; }

    [JsonIgnore]
    public string Key { get; set; } = "";
}

/// <summary>
/// Reads and writes delta state.
///
/// <para>Keyed by tenant <b>and object</b>, which the DAM utility had no need for: one
/// run here covers two dozen collections that each advance at their own pace, and a run
/// that exports a subset must not disturb the marks of the objects it skipped.</para>
/// </summary>
public static class DeltaStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ResolvePath(ExportConfig config) =>
        Path.IsPathRooted(config.Source.Delta.StateFile)
            ? config.Source.Delta.StateFile
            : Path.Combine(config.Output.Directory, config.Source.Delta.StateFile);

    /// <summary>
    /// Lineage key. Includes the compared property, so changing it starts a fresh
    /// lineage rather than silently reusing a mark that meant something else.
    /// </summary>
    public static string ComputeKey(ExportConfig config, PmObject pmObject)
    {
        var property = config.Source.Delta.Property ?? pmObject.ModifiedProperty ?? "(none)";
        var host = new Uri(config.Aprimo.ResolvedApiBaseUrl).Host;
        return $"{host}|{pmObject.Name}|{property}";
    }

    private static Dictionary<string, DeltaState> ReadAll(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, DeltaState>(StringComparer.Ordinal);

        return JsonSerializer.Deserialize<Dictionary<string, DeltaState>>(File.ReadAllText(path), Json)
               ?? new Dictionary<string, DeltaState>(StringComparer.Ordinal);
    }

    public static DeltaState Load(ExportConfig config, PmObject pmObject, Action<string> log)
    {
        var key = ComputeKey(config, pmObject);
        var path = ResolvePath(config);

        if (!File.Exists(path)) return new DeltaState { Key = key, Object = pmObject.Name };

        try
        {
            var all = ReadAll(path);

            if (all.TryGetValue(key, out var state))
            {
                state.Key = key;
                return state;
            }

            return new DeltaState { Key = key, Object = pmObject.Name };
        }
        catch (Exception ex)
        {
            log($"Delta state at '{path}' could not be read ({ex.Message}); treating this as a first run.");
            return new DeltaState { Key = key, Object = pmObject.Name };
        }
    }

    public static void Save(
        ExportConfig config,
        PmObject pmObject,
        DeltaWindow window,
        long rowsExported,
        Action<string> log)
    {
        var key = ComputeKey(config, pmObject);
        var path = ResolvePath(config);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var all = ReadAll(path);
            all.TryGetValue(key, out var previous);

            all[key] = new DeltaState
            {
                // The next window starts where this one ended, not at the highest
                // timestamp observed.
                HighWaterMarkUtc = window.Until,
                LastRunUtc = DateTimeOffset.UtcNow,
                LastRunRows = rowsExported,
                TotalRows = (previous?.TotalRows ?? 0) + rowsExported,
                RunCount = (previous?.RunCount ?? 0) + 1,
                Object = pmObject.Name
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, path, overwrite: true);

            log($"  Delta mark for '{pmObject.Name}' advanced to {DeltaWindow.Format(window.Until)}.");
        }
        catch (Exception ex)
        {
            log($"Warning: could not save delta state for '{pmObject.Name}' ({ex.Message}). The export " +
                "succeeded, but the next --since last would repeat this window.");
        }
    }

    /// <summary>Forgets the mark for the given objects, or for every object when the list is empty.</summary>
    public static bool Reset(ExportConfig config, IReadOnlyList<PmObject> objects, Action<string> log)
    {
        var path = ResolvePath(config);
        if (!File.Exists(path))
        {
            log($"No delta state to reset at '{path}'.");
            return false;
        }

        try
        {
            var all = ReadAll(path);
            var removed = 0;

            if (objects.Count == 0)
            {
                removed = all.Count;
                all.Clear();
            }
            else
            {
                foreach (var o in objects)
                    if (all.Remove(ComputeKey(config, o))) removed++;
            }

            if (removed == 0)
            {
                log("No matching delta state; nothing reset.");
                return false;
            }

            if (all.Count == 0)
            {
                File.Delete(path);
                log($"Removed {removed} delta entr(ies) and deleted '{path}'.");
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
                log($"Reset {removed} delta entr(ies); {all.Count} other(s) kept.");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"Could not reset delta state ({ex.Message}).");
            return false;
        }
    }
}
