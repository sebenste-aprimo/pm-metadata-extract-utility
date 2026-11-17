using System.Text.Json.Serialization;

namespace AprimoPmExport.Configuration;

/// <summary>
/// Root configuration, bound from appsettings.json then overlaid with environment
/// variables and command-line switches (see <see cref="ConfigLoader"/>).
/// </summary>
public sealed class ExportConfig
{
    public AprimoConfig Aprimo { get; set; } = new();
    public SourceConfig Source { get; set; } = new();
    public ThrottleConfig Throttle { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();

    /// <summary>Path to the object registry. See <see cref="ObjectCatalog"/>.</summary>
    public string ObjectsFile { get; set; } = ObjectCatalog.DefaultFileName;

    /// <summary>
    /// Which objects to export: a comma-separated list of registry names, or
    /// <c>all</c>. Overridden per run by <c>--objects</c> / <c>--all-objects</c>.
    /// </summary>
    public string Objects { get; set; } = "all";

    /// <summary>
    /// Directory holding one column list per object, named <c>{object}.txt</c>.
    /// Resolved relative to the current directory, then beside the config file, then
    /// beside the executable.
    ///
    /// <para>An object with no file of its own falls back to
    /// <see cref="DefaultFieldsFile"/>, and failing that to a generated set of every
    /// scalar property on the first item seen.</para>
    /// </summary>
    public string FieldsDirectory { get; set; } = "fields";

    /// <summary>Column list used for objects with no file of their own.</summary>
    public string? DefaultFieldsFile { get; set; } = "_default.txt";

    /// <summary>
    /// When an object has no column list at all, derive one from the first item's own
    /// properties rather than failing. Convenient for a first look at a tenant; pin the
    /// columns with <c>--discover-fields</c> before relying on the output, since a column
    /// set inferred from one item can shift between runs.
    /// </summary>
    public bool AutoDeriveColumns { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        foreach (var e in ValidateExcludingCredentials()) yield return e;

        // Credentials last, because they are the one thing that may still be supplied
        // interactively at the point this runs.
        foreach (var e in Aprimo.ValidateCredentials()) yield return e;
    }

    /// <summary>
    /// Everything that does not depend on a client ID or secret.
    ///
    /// <para>Split out so the tenant and endpoints can be checked <b>before</b> the
    /// interactive credential prompt. Validating everything at once means a missing
    /// tenant is only reported after you have typed a secret — the endpoints were never
    /// resolvable, so the credentials could not have been used whatever they were.</para>
    /// </summary>
    public IEnumerable<string> ValidateExcludingCredentials()
    {
        foreach (var e in Aprimo.ValidateEndpoints()) yield return e;
        foreach (var e in Source.Validate()) yield return e;
        foreach (var e in Throttle.Validate()) yield return e;
        foreach (var e in Output.Validate()) yield return e;
        foreach (var e in Limits.Validate()) yield return e;

        if (string.IsNullOrWhiteSpace(ObjectsFile))
            yield return "ObjectsFile is required (the object registry, normally objects.json).";

        if (string.IsNullOrWhiteSpace(Objects))
            yield return "Objects is required — name one or more objects, or 'all'.";
    }
}

/// <summary>Tenant, endpoints and OAuth 2.0 client-credentials settings.</summary>
public sealed class AprimoConfig
{
    /// <summary>Tenant identifier — the subdomain of your Aprimo URL.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>
    /// API base URL. Defaults to <c>https://{tenant}.aprimo.com/api</c>.
    ///
    /// <para>Note this differs from the DAM API host: PM lives on the plain tenant host
    /// with no <c>.dam</c> segment, on <c>/api</c> rather than <c>/api/core</c>. The
    /// token endpoint is identical for both.</para>
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>OAuth token endpoint. Defaults to <c>https://{tenant}.aprimo.com/login/connect/token</c>.</summary>
    public string? TokenUrl { get; set; }

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Space-delimited scopes. The spec lists <c>api</c>, <c>reporting-api</c> and
    /// <c>filestore-access</c>; <c>api</c> is what a metadata export needs.
    /// </summary>
    public string Scope { get; set; } = "api";

    /// <summary>
    /// How to present client credentials on the token request. <c>Basic</c> uses an
    /// Authorization header (RFC 6749 §2.3.1 preferred form); <c>Body</c> puts
    /// client_id/client_secret in the form body. Aprimo's IdentityServer accepts both.
    /// </summary>
    public ClientAuthStyle ClientAuthStyle { get; set; } = ClientAuthStyle.Basic;

    /// <summary>
    /// Renew the access token once it is within this many seconds of expiry, so a
    /// long-running export never presents a token that expires mid-flight. Must
    /// comfortably exceed <see cref="SourceConfig.RequestTimeoutSeconds"/>.
    /// </summary>
    public int RefreshSkewSeconds { get; set; } = 180;

    /// <summary>
    /// Value of the <c>API-VERSION</c> header.
    ///
    /// <para>Null by default, and deliberately so: the DAM API mandates this header, but
    /// the PM spec declares no header parameters on any of its 288 paths. Sending an
    /// unexpected version header is a needless way to get a 400. Set it if your tenant
    /// turns out to want one.</para>
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>Extra headers sent on every API request. Empty by default.</summary>
    public Dictionary<string, string> AdditionalHeaders { get; set; } = new();

    /// <summary>Extra form parameters for the token request, if your registration needs them.</summary>
    public Dictionary<string, string> AdditionalTokenParameters { get; set; } = new();

    public string ResolvedApiBaseUrl =>
        !string.IsNullOrWhiteSpace(ApiBaseUrl)
            ? ApiBaseUrl!.TrimEnd('/')
            : $"https://{Tenant}.aprimo.com/api";

    public string ResolvedTokenUrl =>
        !string.IsNullOrWhiteSpace(TokenUrl)
            ? TokenUrl!
            : $"https://{Tenant}.aprimo.com/login/connect/token";

    /// <summary>
    /// Explains a literally-copied placeholder and names every place it could have come
    /// from. The environment is listed first because it silently overrides the config
    /// file, which makes it the least obvious source.
    /// </summary>
    private static string PlaceholderMessage(string setting, string value, string envVar, string flag) =>
        $"Aprimo.{setting} is set to the placeholder {value} — a value from the documentation, " +
        $"not a real credential.{Environment.NewLine}" +
        $"      Check, in this order of precedence:{Environment.NewLine}" +
        $"        1. the {envVar} environment variable (this overrides the config file, " +
        $"so it wins silently){Environment.NewLine}" +
        $"           check:  [Environment]::GetEnvironmentVariable('{envVar}','User'){Environment.NewLine}" +
        $"           clear:  [Environment]::SetEnvironmentVariable('{envVar}',$null,'User')" +
        $"   (then open a new terminal){Environment.NewLine}" +
        $"        2. the {flag} command-line switch{Environment.NewLine}" +
        $"        3. Aprimo.{setting} in appsettings.local.json (or appsettings.json)";

    public IEnumerable<string> Validate() =>
        ValidateEndpoints().Concat(ValidateCredentials());

    /// <summary>
    /// Tenant, endpoints and connection settings — everything checkable without a client
    /// ID or secret, and therefore everything that can be checked before prompting for
    /// one. See <see cref="ExportConfig.ValidateExcludingCredentials"/>.
    /// </summary>
    public IEnumerable<string> ValidateEndpoints()
    {
        var haveExplicitUrls = !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
                               !string.IsNullOrWhiteSpace(TokenUrl);

        if (string.IsNullOrWhiteSpace(Tenant) && !haveExplicitUrls)
            yield return
                "Aprimo.Tenant is required — it is the subdomain of your Aprimo URL, and nothing " +
                $"can be reached without it.{Environment.NewLine}" +
                $"      Set it once in appsettings.local.json (copy appsettings.local.example.json), " +
                "or pass --tenant <name> for a single run." +
                $"{Environment.NewLine}" +
                "      (Alternatively set both Aprimo.ApiBaseUrl and Aprimo.TokenUrl explicitly.)";

        if (!Uri.TryCreate(ResolvedApiBaseUrl, UriKind.Absolute, out _))
            yield return $"Aprimo.ApiBaseUrl is not a valid absolute URL: '{ResolvedApiBaseUrl}'.";
        if (!Uri.TryCreate(ResolvedTokenUrl, UriKind.Absolute, out _))
            yield return $"Aprimo.TokenUrl is not a valid absolute URL: '{ResolvedTokenUrl}'.";

        if (!string.IsNullOrWhiteSpace(Tenant) && PlaceholderDetection.LooksLikePlaceholder(Tenant))
            yield return PlaceholderMessage("Tenant", Tenant, "APRIMO_TENANT", "--tenant");

        if (RefreshSkewSeconds < 0)
            yield return "Aprimo.RefreshSkewSeconds must be >= 0.";
    }

    /// <summary>
    /// The client ID and secret. Checked only after the credential store and the
    /// interactive prompt have had their chance to supply them.
    /// </summary>
    public IEnumerable<string> ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            yield return "Aprimo.ClientId is required (set env APRIMO_CLIENT_ID to keep it out of the config file).";
        else if (PlaceholderDetection.LooksLikePlaceholder(ClientId))
            yield return PlaceholderMessage("ClientId", ClientId, "APRIMO_CLIENT_ID", "--client-id");

        if (string.IsNullOrWhiteSpace(ClientSecret))
            yield return "Aprimo.ClientSecret is required (set env APRIMO_CLIENT_SECRET to keep it out of the config file).";
        else if (PlaceholderDetection.LooksLikePlaceholder(ClientSecret))
            yield return PlaceholderMessage("ClientSecret", "(the value you supplied)",
                "APRIMO_CLIENT_SECRET", "--client-secret");
    }
}

public enum ClientAuthStyle { Basic, Body }

/// <summary>How the CSV writer treats newlines occurring inside a value.</summary>
public enum NewlineHandling
{
    /// <summary>
    /// Keep them, quoting the field per RFC 4180. Correct, but a record then spans
    /// several physical lines and needs a real CSV parser to read back.
    /// </summary>
    Preserve,

    /// <summary>Replace each run with a single space, giving one line per record.</summary>
    Space,

    /// <summary>Replace each run with <see cref="OutputConfig.MultiValueSeparator"/>.</summary>
    Separator
}

/// <summary>Detects documentation placeholders that were copied literally.</summary>
internal static class PlaceholderDetection
{
    /// <summary>
    /// Values that are unmistakably a copied example rather than a real credential.
    /// Deliberately an exact-match list, not a substring scan, so a genuine secret that
    /// happens to contain one of these words is never rejected.
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "your-client-id", "your-client-secret", "your-secret", "your-secret-here",
        "your-id", "your-tenant", "your-tenant-name", "tenant",
        "changeme", "change-me", "placeholder", "todo", "tbd",
        "xxx", "xxxx", "xxxxx", "none", "null", "example", "test-value"
    };

    public static bool LooksLikePlaceholder(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;

        // Angle-bracketed values such as <your-client-id> are always a template.
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>')) return true;

        return Known.Contains(trimmed);
    }
}

/// <summary>
/// How to walk a collection.
///
/// <para>PM offers no keyset or cursor mechanism, so unlike the DAM utility there is no
/// watermark mode to fall back on for depth. The two options here differ only in how the
/// next page is located.</para>
/// </summary>
public enum PagingMode
{
    /// <summary>
    /// <c>offset</c> + <c>limit</c>, advancing the offset by the number of items returned.
    /// The mechanism every paged endpoint in the spec declares.
    /// </summary>
    Offset,

    /// <summary>
    /// Follow <c>_links.next.href</c> from the HAL envelope, falling back to
    /// <see cref="Offset"/> when a response omits it.
    ///
    /// <para>Preferable in principle — the server decides where the next page starts, so
    /// it can stay correct where a raw offset would drift. The spec documents the link
    /// relations but no example shows a populated <c>next</c>, so it is unverified; that
    /// is why <see cref="Offset"/> remains the default.</para>
    /// </summary>
    LinkFollow
}

/// <summary>
/// How a delta run decides it has seen everything in the window.
///
/// <para>PM has no query language, so no run can push <c>modifiedDate &gt;= X</c> to the
/// server the way the DAM utility does. Rows are always filtered client-side; these
/// options change only how early paging is allowed to stop.</para>
/// </summary>
public enum DeltaStrategy
{
    /// <summary>
    /// Sort by the object's modified property descending and stop once a whole page falls
    /// before the window. Cheap for a daily delta — typically one or two pages.
    ///
    /// <para>Correctness depends on <c>sortField</c> being honoured for that property.
    /// If it is silently ignored the API returns its own order, an early page can then
    /// contain old rows, and the run would stop short. Guarded by
    /// <see cref="SortVerification"/>, which checks that returned rows really are
    /// descending before trusting an early stop.</para>
    /// </summary>
    SortedDescending,

    /// <summary>
    /// Page the entire collection and keep only rows inside the window. Always correct
    /// and independent of sort behaviour, at the cost of reading everything each run.
    /// </summary>
    FullScan
}

public sealed class SourceConfig
{
    public PagingConfig Paging { get; set; } = new();
    public RetryConfig Retry { get; set; } = new();
    public DeltaConfig Delta { get; set; } = new();

    /// <summary>
    /// Sort field applied to every object, overriding each object's own default.
    /// Normally null: the objects have unrelated schemas, so one shared sort key rarely
    /// fits them all.
    /// </summary>
    public string? SortField { get; set; }

    /// <summary>
    /// Sort direction. The API takes a separate <c>sortAscending</c> boolean rather than
    /// DAM's <c>-Field</c> prefix. Ascending is the safer default for a long export:
    /// with descending order, newly modified items jump to page 1 and shift every offset
    /// behind them.
    /// </summary>
    public bool SortAscending { get; set; } = true;

    /// <summary>
    /// Fail on the first page if the response is not the expected HAL envelope, rather
    /// than writing a file full of blanks.
    /// </summary>
    public bool VerifyShapeOnFirstPage { get; set; } = true;

    /// <summary>
    /// Fetch each item's full representation from its <c>DetailPath</c> after listing it.
    ///
    /// <para>Off by default, and expensive: it turns one request per page into one request
    /// per item. Worth it only if a collection response turns out to omit properties the
    /// single-item endpoint returns — run <c>--sample</c> against both to find out.</para>
    /// </summary>
    public bool HydrateFromDetailEndpoint { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 120;

    public IEnumerable<string> Validate()
    {
        if (RequestTimeoutSeconds <= 0)
            yield return "Source.RequestTimeoutSeconds must be > 0.";

        foreach (var e in Paging.Validate()) yield return e;
        foreach (var e in Retry.Validate()) yield return e;
        foreach (var e in Delta.Validate()) yield return e;
    }
}

public sealed class PagingConfig
{
    public PagingMode Mode { get; set; } = PagingMode.Offset;

    /// <summary>
    /// Items per request, sent as <c>limit</c>.
    ///
    /// <para>The spec's declared default is 50 and it documents <b>no maximum</b> —
    /// unlike the DAM API, whose <c>RecordCollection.limit</c> carries
    /// <c>maximum: 1000</c>. So there is no published ceiling to aim at. 100 is a
    /// deliberately conservative starting point; raise it once a tenant is shown to
    /// accept more. <see cref="SafePageSizeCeiling"/> only guards against a typo.</para>
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Sanity ceiling, not an API limit — the spec declares none. Exists so a mistyped
    /// <c>--page-size 100000</c> fails locally instead of against the tenant.
    /// </summary>
    public const int SafePageSizeCeiling = 5000;

    /// <summary>Stop after this many pages per object. 0 = unlimited.</summary>
    public int MaxPages { get; set; }

    /// <summary>
    /// Stop an object with a clear error if paging passes this offset without finishing.
    /// Catches a non-advancing collection (an endpoint that ignores <c>offset</c> would
    /// otherwise loop forever returning page 1). 0 disables the guard.
    /// </summary>
    public int MaxOffset { get; set; } = 1_000_000;

    public IEnumerable<string> Validate()
    {
        if (PageSize <= 0)
            yield return "Source.Paging.PageSize must be > 0.";
        else if (PageSize > SafePageSizeCeiling)
            yield return $"Source.Paging.PageSize must be <= {SafePageSizeCeiling}. " +
                         "The API documents no maximum, so this is a local sanity check rather than " +
                         "an API limit — raise Source.Paging.SafePageSizeCeiling in source if a tenant " +
                         "genuinely accepts more.";

        if (MaxPages < 0) yield return "Source.Paging.MaxPages must be >= 0 (0 = unlimited).";
        if (MaxOffset < 0) yield return "Source.Paging.MaxOffset must be >= 0 (0 = disabled).";
    }
}

/// <summary>Incremental ("changed since") export settings.</summary>
public sealed class DeltaConfig
{
    public DeltaStrategy Strategy { get; set; } = DeltaStrategy.SortedDescending;

    /// <summary>
    /// Property compared, overriding each object's own <c>ModifiedProperty</c>. Normally
    /// null, since the objects do not agree on a single name.
    /// </summary>
    public string? Property { get; set; }

    /// <summary>
    /// Under <see cref="DeltaStrategy.SortedDescending"/>, confirm the API really did sort
    /// descending before letting an early stop happen.
    ///
    /// <para>This matters more here than the equivalent would in the DAM utility. There,
    /// the window is a server-side predicate, so a bad sort costs efficiency but never
    /// correctness. Here the sort <i>is</i> the stopping rule: if <c>sortField</c> is
    /// quietly ignored, stopping early silently truncates the export. So the reader
    /// watches the values it receives and falls back to a full scan if they are not
    /// ordered as asked.</para>
    /// </summary>
    public bool SortVerification { get; set; } = true;

    /// <summary>
    /// Minutes of deliberate re-read at the start of a resumed window, absorbing clock
    /// skew between the API's timestamps and ours. Costs a few duplicate rows per run;
    /// the alternative is silently dropping records. 0 gives exact adjacency.
    /// </summary>
    public int OverlapMinutes { get; set; } = 5;

    /// <summary>High-water mark file, relative to <c>Output.Directory</c>. Keyed by tenant + object.</summary>
    public string StateFile { get; set; } = "delta-state.json";

    /// <summary>Warn when a window is longer than this many days. 0 disables the warning.</summary>
    public int WarnIfWindowExceedsDays { get; set; } = 7;

    /// <summary>
    /// What to do about an object with no modified stamp when a delta run is requested.
    /// Skipping is the honest default: exporting it in full would quietly mix a complete
    /// snapshot into what the file names describe as a delta.
    /// </summary>
    public bool SkipObjectsWithoutModifiedProperty { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (OverlapMinutes < 0)
            yield return "Source.Delta.OverlapMinutes must be >= 0.";
        if (string.IsNullOrWhiteSpace(StateFile))
            yield return "Source.Delta.StateFile is required.";
        if (WarnIfWindowExceedsDays < 0)
            yield return "Source.Delta.WarnIfWindowExceedsDays must be >= 0 (0 disables the warning).";
    }
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 5;
    public double InitialBackoffSeconds { get; set; } = 1.0;
    public double MaxBackoffSeconds { get; set; } = 60.0;
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Honour a <c>Retry-After</c> header instead of the computed backoff. The spec
    /// declares only 200 responses, so this is entirely defensive.
    /// </summary>
    public bool RespectRetryAfter { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (MaxAttempts < 1) yield return "Source.Retry.MaxAttempts must be >= 1.";
        if (InitialBackoffSeconds <= 0) yield return "Source.Retry.InitialBackoffSeconds must be > 0.";
        if (MaxBackoffSeconds < InitialBackoffSeconds)
            yield return "Source.Retry.MaxBackoffSeconds must be >= InitialBackoffSeconds.";
        if (BackoffMultiplier < 1) yield return "Source.Retry.BackoffMultiplier must be >= 1.";
    }
}

/// <summary>Client-side rate limiting. The spec documents no server limits, so this is the control.</summary>
public sealed class ThrottleConfig
{
    /// <summary>
    /// Sustained request rate. Fractional values allowed (0.5 = one request every two
    /// seconds). Zero or negative disables throttling entirely.
    /// </summary>
    public double RequestsPerSecond { get; set; } = 5.0;

    /// <summary>
    /// Token-bucket depth: how many requests may fire back-to-back after an idle period.
    /// 1 gives strictly even spacing.
    /// </summary>
    public int Burst { get; set; } = 1;

    /// <summary>Cap on in-flight requests. Objects export sequentially, so 1 is correct today.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    public IEnumerable<string> Validate()
    {
        if (Burst < 1) yield return "Throttle.Burst must be >= 1.";
        if (MaxConcurrentRequests < 1) yield return "Throttle.MaxConcurrentRequests must be >= 1.";
        if (double.IsNaN(RequestsPerSecond) || double.IsInfinity(RequestsPerSecond))
            yield return "Throttle.RequestsPerSecond must be a finite number.";
    }
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "./export";

    /// <summary>
    /// Base file name. Output lands as <c>{prefix}-{object}_0001.csv</c>.
    ///
    /// <para>The object name is always part of the file name, because one run writes a
    /// separate file set per object and they must not collide.</para>
    /// </summary>
    public string FilePrefix { get; set; } = "aprimo-pm";

    /// <summary>Write each object's files into its own subdirectory under <see cref="Directory"/>.</summary>
    public bool SubdirectoryPerObject { get; set; }

    public string Delimiter { get; set; } = ",";

    /// <summary>Write a UTF-8 BOM. Excel needs it to detect UTF-8 on double-click.</summary>
    public bool WriteByteOrderMark { get; set; } = true;

    /// <summary>CRLF (RFC 4180) or LF.</summary>
    public string NewLine { get; set; } = "CRLF";

    /// <summary>Joins multiple values when one mapping resolves to several.</summary>
    public string MultiValueSeparator { get; set; } = "|";

    /// <summary>What to do about newlines inside a value.</summary>
    public NewlineHandling NewlineInValue { get; set; } = NewlineHandling.Separator;

    /// <summary>
    /// Prefix values starting with <c>= + - @</c> (or tab/CR) with a single quote so
    /// spreadsheets do not evaluate them as formulas. CSV injection defence.
    /// </summary>
    public bool SanitizeFormulas { get; set; } = true;

    /// <summary>Write file buffer size in bytes.</summary>
    public int WriteBufferBytes { get; set; } = 1 << 20;

    /// <summary>Delete any pre-existing export files matching the prefix instead of refusing to start.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Also write a <c>manifest.csv</c> summarising every object in the run: rows, files,
    /// requests, elapsed time and outcome. With two dozen objects per run, the summary is
    /// too big to read off the console reliably.
    /// </summary>
    public bool WriteManifest { get; set; } = true;

    /// <summary>
    /// Resolve <c>label:</c> columns from the tenant's lookup lists.
    ///
    /// <para>Costs a few extra requests per object — one per distinct list the column
    /// list references, fetched once and reused for every row. Turning it off leaves
    /// those columns holding the raw codes rather than removing them, so a file's shape
    /// does not change.</para>
    /// </summary>
    public bool ResolveLookups { get; set; } = true;

    /// <summary>
    /// Resolve <c>user:</c> columns to names through <c>GET /users/{id}</c>.
    ///
    /// <para>Separate from <see cref="ResolveLookups"/> because it costs and exposes
    /// something different: one request per distinct user rather than one per list, and each
    /// response is a full user record (email, phone, address) that is held in memory just to
    /// read two name fields. Only the name is kept, and nothing else is written or logged.
    /// Off leaves those columns holding the numeric ID, so the column set does not change.</para>
    /// </summary>
    public bool ResolveUsers { get; set; } = true;

    /// <summary>
    /// Property names whose values are user IDs, for <c>--discover-fields</c> to emit
    /// <c>user:</c> columns for.
    ///
    /// <para>A list rather than a rule because nothing in the API says which integers are
    /// users — a field's lookup list can be unusable (<c>modifiedUser</c> names list 58, which
    /// is not served), so the type cannot be read from the metadata. Extend it for your
    /// tenant. It only affects generation: a <c>user:</c> path in a hand-written column list
    /// works whether or not its property is named here.</para>
    /// </summary>
    public List<string> UserFields { get; set; } = new()
    {
        "projectManager", "modifiedUser", "modifiedUserId", "ownerId", "administratorId",
        "creatorId", "completedByUser", "reviewedBy", "purgedBy", "modifiedBy", "createdBy"
    };

    /// <summary>
    /// When <c>--discover-fields</c> meets a coded field, emit the raw code column
    /// alongside the label.
    ///
    /// <para>On by default. The label is what a reader wants, but the code is what joins
    /// to other systems and what survives a label being renamed — and a renamed label
    /// with no code beside it silently breaks a downstream match.</para>
    /// </summary>
    public bool GenerateCodeAndLabelColumns { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            yield return "Output.Directory is required.";
        if (string.IsNullOrWhiteSpace(FilePrefix))
            yield return "Output.FilePrefix is required.";
        if (string.IsNullOrEmpty(Delimiter))
            yield return "Output.Delimiter is required.";
        if (Delimiter.Contains('"'))
            yield return "Output.Delimiter cannot contain a double quote.";
        if (!string.Equals(NewLine, "CRLF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(NewLine, "LF", StringComparison.OrdinalIgnoreCase))
            yield return $"Output.NewLine must be CRLF or LF (got '{NewLine}').";
        if (WriteBufferBytes < 4096)
            yield return "Output.WriteBufferBytes must be >= 4096.";
    }
}

public sealed class LimitsConfig
{
    /// <summary>Rows per CSV file before rolling to the next. 0 = never roll.</summary>
    public long MaxRecordsPerFile { get; set; } = 1_000_000;

    /// <summary>
    /// Row cap <b>per object</b>, not across the run. 0 = unlimited.
    ///
    /// <para>Per-object is the useful meaning when one run covers two dozen collections:
    /// a shared cap would be spent entirely on whichever object happened to go first.</para>
    /// </summary>
    public long MaxRecordsPerObject { get; set; }

    /// <summary>Write a checkpoint after each completed page so an interrupted run can resume.</summary>
    public bool EnableCheckpoint { get; set; } = true;

    /// <summary>
    /// Drop an item whose ID has already been written in this run.
    ///
    /// <para>On by default, which is a departure from the DAM utility — there, keyset
    /// paging advances an immutable watermark, so de-duplication was only needed for ties
    /// on that watermark. PM offers no watermark mode at all: <c>offset</c> paging over a
    /// collection that someone else is editing will re-show or skip items as rows shift
    /// between pages. Tracking IDs is the only defence available.</para>
    /// </summary>
    public bool DeduplicateById { get; set; } = true;

    /// <summary>
    /// Ceiling on tracked IDs before <see cref="DeduplicateById"/> turns itself off for
    /// that object. Passing it means duplicate rows become possible; not having it means
    /// an unbounded set. Duplicates are fixable downstream, an OOM halfway through a
    /// multi-hour run is not.
    /// </summary>
    public int MaxDeduplicationIds { get; set; } = 2_000_000;

    /// <summary>
    /// Keep exporting the remaining objects when one fails, and report the failures at the
    /// end. With two dozen objects in a run, one unlicensed or permission-denied
    /// collection should not throw away twenty-three good exports.
    /// </summary>
    public bool ContinueOnObjectFailure { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (MaxRecordsPerFile < 0) yield return "Limits.MaxRecordsPerFile must be >= 0 (0 = never roll).";
        if (MaxRecordsPerObject < 0) yield return "Limits.MaxRecordsPerObject must be >= 0 (0 = unlimited).";
        if (MaxDeduplicationIds < 0) yield return "Limits.MaxDeduplicationIds must be >= 0.";
    }
}

public sealed class FieldMapping
{
    /// <summary>CSV column header.</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Extraction path into the item JSON. Supports the <c>ea:</c> shorthand.
    /// See <see cref="Export.FieldExtractor"/> for the full grammar.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Emitted when the path resolves to nothing.</summary>
    public string Default { get; set; } = "";

    [JsonIgnore]
    public Export.CompiledPath? Compiled { get; set; }
}
