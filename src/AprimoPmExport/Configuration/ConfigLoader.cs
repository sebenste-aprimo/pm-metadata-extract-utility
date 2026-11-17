using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AprimoPmExport.Configuration;

/// <summary>
/// Builds an <see cref="ExportConfig"/> from appsettings.json, then
/// <c>appsettings.local.json</c>, then environment variables, then command-line
/// switches — later sources win.
///
/// <para><b>Why there is a local file.</b> <c>appsettings.json</c> ships with the tool, so
/// it has to stay generic. A customer's own settings — their tenant above all — belong in
/// <c>appsettings.local.json</c>, which is never shipped and is listed in <c>.gitignore</c>.
/// Without that split the tenant ends up edited into the shipped file, and from there into
/// whatever gets copied to the next customer. It also means a settings change survives a
/// rebuild: under <c>dotnet run</c> the shipped file is copied into <c>bin\</c> and
/// overwritten on every build, so an edit made to that copy is silently lost.</para>
/// </summary>
public static class ConfigLoader
{
    public const string DefaultConfigFileName = "appsettings.json";

    /// <summary>
    /// A customer's overrides, layered over <see cref="DefaultConfigFileName"/>. Only the
    /// settings it names are replaced; everything else keeps its shipped default.
    /// </summary>
    public const string LocalConfigFileName = "appsettings.local.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>Path the config was actually read from, for resolving relative data paths.</summary>
    public static string? LastConfigPath { get; private set; }

    /// <summary>
    /// Finds the config file: an explicit <c>--config</c> path, then the current
    /// directory, then the directory holding the executable. The last of those matters
    /// because <c>dotnet run</c> keeps the caller's working directory — without it,
    /// running from the repo root would silently ignore the shipped config. Returns null
    /// when no file exists anywhere.
    /// </summary>
    private static string? ResolveConfigPath(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            // An explicitly named file that does not exist is an error, not a fallback.
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException(
                    $"Config file not found: '{Path.GetFullPath(explicitPath)}'.", explicitPath);

            return explicitPath;
        }

        if (File.Exists(DefaultConfigFileName))
            return DefaultConfigFileName;

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        return File.Exists(besideExecutable) ? besideExecutable : null;
    }

    public static ExportConfig Load(CommandLineArgs args, Action<string> log)
    {
        var configPath = ResolveConfigPath(args.ConfigPath);
        LastConfigPath = configPath;

        // A config named with --config is taken exactly as given. Quietly layering another
        // file over a file the user pointed at explicitly would make "what is this run
        // using?" harder to answer, not easier.
        var overlayPath = args.ConfigPath is null
            ? FindLocalOverride(new[]
              {
                  Directory.GetCurrentDirectory(),
                  configPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(configPath)),
                  AppContext.BaseDirectory
              })
            : null;

        var config = LoadFile(configPath, overlayPath, log);
        ApplyEnvironment(config, log);
        ApplyCommandLine(config, args, log);

        return config;
    }

    /// <summary>
    /// Finds <see cref="LocalConfigFileName"/>: the current directory first, then beside
    /// the config file, then beside the executable — the same order every other data file
    /// uses. The current directory leads so that running from the project root finds the
    /// file you actually edit, rather than a copy in <c>bin\</c>.
    /// </summary>
    internal static string? FindLocalOverride(IEnumerable<string?> directories)
    {
        foreach (var directory in directories
                     .Where(d => !string.IsNullOrWhiteSpace(d))
                     .Select(d => Path.GetFullPath(d!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory, LocalConfigFileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Layers <paramref name="overlayJson"/> over <paramref name="baseJson"/>.
    ///
    /// <para>Objects merge key by key, recursively, so an override that sets only
    /// <c>Aprimo.Tenant</c> leaves every other <c>Aprimo</c> setting alone. Anything that is
    /// not an object on both sides — a number, a string, an array — is <b>replaced</b>, not
    /// combined: an overridden list (<c>UserFields</c>, say) means exactly the list written,
    /// since a silent union with the shipped default would leave no way to remove an
    /// entry.</para>
    ///
    /// <para>Keys match without regard to case, as the loader's own binding does, so
    /// <c>"tenant"</c> overrides <c>"Tenant"</c> rather than sitting beside it.</para>
    /// </summary>
    internal static string MergeJson(string baseJson, string overlayJson)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        if (JsonNode.Parse(baseJson, documentOptions: options) is not JsonObject target)
            throw new JsonException("the shipped config must be a JSON object.");

        if (JsonNode.Parse(overlayJson, documentOptions: options) is not JsonObject source)
            throw new JsonException("the file must contain a JSON object.");

        MergeInto(target, source);
        return target.ToJsonString();
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            var existingKey = target
                .Select(p => p.Key)
                .FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));

            if (existingKey is not null &&
                target[existingKey] is JsonObject existingObject &&
                value is JsonObject incomingObject)
            {
                MergeInto(existingObject, incomingObject);
                continue;
            }

            // A node can have only one parent, so the overlay's value is cloned rather than
            // moved across.
            target[existingKey ?? key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Resolves the object registry: as given, then beside the config file, then beside
    /// the executable.
    /// </summary>
    public static string ResolveObjectsFile(ExportConfig config, string? configPath)
    {
        var path = config.ObjectsFile;

        if (File.Exists(path)) return path;
        if (Path.IsPathRooted(path)) return path;

        if (configPath is not null)
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (configDirectory is not null)
            {
                var besideConfig = Path.Combine(configDirectory, path);
                if (File.Exists(besideConfig)) return besideConfig;
            }
        }

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(besideExecutable)) return besideExecutable;

        // Let the loader raise a consistent, path-qualified error.
        return path;
    }

    internal static ExportConfig LoadFile(string? path, string? overlayPath, Action<string> log)
    {
        if (path is null)
        {
            log($"No {DefaultConfigFileName} found in {Directory.GetCurrentDirectory()} " +
                $"or next to the executable ({AppContext.BaseDirectory}) — " +
                "using defaults plus environment variables and command-line switches.");
        }

        // Parsed as JSON first, so a file that is not valid JSON is reported as that — with
        // the file's own name — rather than as a failure somewhere inside the merge.
        string baseJson = "{}";

        if (path is not null)
        {
            baseJson = File.ReadAllText(path);
            Validate(baseJson, path);
            log($"Loaded config from {Path.GetFullPath(path)}");
        }

        var json = baseJson;

        if (overlayPath is not null)
        {
            var overlayJson = File.ReadAllText(overlayPath);
            Validate(overlayJson, overlayPath);

            try
            {
                json = MergeJson(baseJson, overlayJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Local config '{Path.GetFullPath(overlayPath)}' could not be applied: {ex.Message}", ex);
            }

            log($"Applied local overrides from {Path.GetFullPath(overlayPath)}");
        }

        try
        {
            return JsonSerializer.Deserialize<ExportConfig>(json, JsonOptions) ?? new ExportConfig();
        }
        catch (JsonException ex)
        {
            // The merge succeeded, so a failure here is a value of the wrong type or an
            // unknown enum name. Name the file that most plausibly holds it.
            var culprit = overlayPath ?? path;
            throw new InvalidOperationException(
                "The configuration could not be read" +
                (culprit is null ? "" : $" (check '{Path.GetFullPath(culprit)}')") +
                $": {ex.Message}", ex);
        }

        static void Validate(string text, string file)
        {
            try
            {
                using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Config file '{Path.GetFullPath(file)}' is not valid JSON: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Credentials belong in the environment, not in a file that might be committed.</summary>
    private static void ApplyEnvironment(ExportConfig config, Action<string> log)
    {
        var applied = new List<string>();

        Set("APRIMO_TENANT", v => { config.Aprimo.Tenant = v; applied.Add("Tenant"); });
        Set("APRIMO_CLIENT_ID", v => { config.Aprimo.ClientId = v; applied.Add("ClientId"); });
        Set("APRIMO_CLIENT_SECRET", v => { config.Aprimo.ClientSecret = v; applied.Add("ClientSecret"); });
        Set("APRIMO_PM_API_BASE_URL", v => { config.Aprimo.ApiBaseUrl = v; applied.Add("ApiBaseUrl"); });
        Set("APRIMO_TOKEN_URL", v => { config.Aprimo.TokenUrl = v; applied.Add("TokenUrl"); });
        Set("APRIMO_SCOPE", v => { config.Aprimo.Scope = v; applied.Add("Scope"); });

        if (applied.Count > 0)
            log($"Applied from environment: {string.Join(", ", applied)}");

        // Flag placeholders at the point they enter, rather than leaving the puzzle to a
        // later invalid_client. The environment is the least visible source because it
        // silently outranks the config file.
        WarnIfPlaceholder("APRIMO_CLIENT_ID", config.Aprimo.ClientId);
        WarnIfPlaceholder("APRIMO_TENANT", config.Aprimo.Tenant);

        if (!string.IsNullOrWhiteSpace(config.Aprimo.ClientSecret) &&
            PlaceholderDetection.LooksLikePlaceholder(config.Aprimo.ClientSecret))
            log("Warning: the client secret is a documentation placeholder, not a real value.");

        void WarnIfPlaceholder(string envVar, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!PlaceholderDetection.LooksLikePlaceholder(value)) return;

            log($"Warning: '{value}' is a documentation placeholder, not a real value. " +
                $"If it came from {envVar}, that overrides your config files — clear it with " +
                $"[Environment]::SetEnvironmentVariable('{envVar}',$null,'User') and open a new terminal.");
        }

        static void Set(string name, Action<string> assign)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
        }
    }

    private static void ApplyCommandLine(ExportConfig config, CommandLineArgs args, Action<string> log)
    {
        var applied = new List<string>();

        if (args.Tenant is { } tenant) { config.Aprimo.Tenant = tenant; applied.Add("Tenant"); }
        if (args.ClientId is { } id) { config.Aprimo.ClientId = id; applied.Add("ClientId"); }
        if (args.ClientSecret is { } secret) { config.Aprimo.ClientSecret = secret; applied.Add("ClientSecret"); }
        if (args.ClientAuthStyle is { } authStyle) { config.Aprimo.ClientAuthStyle = authStyle; applied.Add("ClientAuthStyle"); }
        if (args.ApiBaseUrl is { } baseUrl) { config.Aprimo.ApiBaseUrl = baseUrl; applied.Add("ApiBaseUrl"); }

        if (args.RequestsPerSecond is { } rps) { config.Throttle.RequestsPerSecond = rps; applied.Add("RequestsPerSecond"); }
        if (args.Burst is { } burst) { config.Throttle.Burst = burst; applied.Add("Burst"); }

        if (args.PageSize is { } pageSize) { config.Source.Paging.PageSize = pageSize; applied.Add("PageSize"); }
        if (args.PagingMode is { } pagingMode) { config.Source.Paging.Mode = pagingMode; applied.Add("PagingMode"); }
        if (args.MaxPages is { } maxPages) { config.Source.Paging.MaxPages = maxPages; applied.Add("MaxPages"); }

        if (args.SortField is { } sortField) { config.Source.SortField = sortField; applied.Add("SortField"); }
        if (args.SortDescending) { config.Source.SortAscending = false; applied.Add("SortAscending=false"); }

        if (args.DeltaStrategy is { } strategy) { config.Source.Delta.Strategy = strategy; applied.Add("DeltaStrategy"); }
        if (args.DeltaProperty is { } property) { config.Source.Delta.Property = property; applied.Add("DeltaProperty"); }

        if (args.Objects is { } objects) { config.Objects = objects; applied.Add("Objects"); }
        if (args.AllObjects) { config.Objects = "all"; applied.Add("Objects=all"); }
        if (args.ObjectsFile is { } objectsFile) { config.ObjectsFile = objectsFile; applied.Add("ObjectsFile"); }
        if (args.FieldsDirectory is { } fieldsDirectory) { config.FieldsDirectory = fieldsDirectory; applied.Add("FieldsDirectory"); }

        if (args.MaxRecordsPerObject is { } maxRows) { config.Limits.MaxRecordsPerObject = maxRows; applied.Add("MaxRecordsPerObject"); }
        if (args.MaxRecordsPerFile is { } perFile) { config.Limits.MaxRecordsPerFile = perFile; applied.Add("MaxRecordsPerFile"); }

        if (args.OutputDirectory is { } dir) { config.Output.Directory = dir; applied.Add("OutputDirectory"); }
        if (args.FilePrefix is { } prefix) { config.Output.FilePrefix = prefix; applied.Add("FilePrefix"); }
        if (args.Delimiter is { } delimiter) { config.Output.Delimiter = delimiter; applied.Add("Delimiter"); }
        if (args.Overwrite) { config.Output.Overwrite = true; applied.Add("Overwrite"); }
        if (args.SubdirectoryPerObject) { config.Output.SubdirectoryPerObject = true; applied.Add("SubdirectoryPerObject"); }

        if (applied.Count > 0)
            log($"Applied from command line: {string.Join(", ", applied)}");
    }
}

/// <summary>Parsed command-line switches. Null means "not specified — keep the configured value".</summary>
public sealed class CommandLineArgs
{
    public string? ConfigPath { get; private set; }
    public string? Tenant { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }
    public string? ApiBaseUrl { get; private set; }
    public ClientAuthStyle? ClientAuthStyle { get; private set; }

    public double? RequestsPerSecond { get; private set; }
    public int? Burst { get; private set; }

    public int? PageSize { get; private set; }
    public PagingMode? PagingMode { get; private set; }
    public int? MaxPages { get; private set; }

    /// <summary>Comma-separated object names, or 'all'.</summary>
    public string? Objects { get; private set; }
    public bool AllObjects { get; private set; }
    public string? ObjectsFile { get; private set; }

    /// <summary>List the object registry and exit.</summary>
    public bool ListObjects { get; private set; }

    public string? SortField { get; private set; }
    public bool SortDescending { get; private set; }

    /// <summary>Start of the delta window: last | yesterday | today | 1d | 2026-08-05 | instant.</summary>
    public string? Since { get; private set; }

    /// <summary>Optional end of the delta window. Defaults to the run's start time.</summary>
    public string? Until { get; private set; }

    public bool ResetDelta { get; private set; }
    public DeltaStrategy? DeltaStrategy { get; private set; }
    public string? DeltaProperty { get; private set; }

    public long? MaxRecordsPerObject { get; private set; }
    public long? MaxRecordsPerFile { get; private set; }

    public string? FieldsFile { get; private set; }
    public string? FieldsDirectory { get; private set; }

    /// <summary>Generate per-object column lists from the tenant's own metadata, then exit.</summary>
    public bool DiscoverFields { get; private set; }

    /// <summary>Print a named selection list and exit — decodes multi-value attribute codes.</summary>
    public string? SelectionList { get; private set; }

    /// <summary>Report how each object's coded values could be resolved to labels, then exit.</summary>
    public bool ProbeLookups { get; private set; }

    /// <summary>Comma-separated user IDs for the read-only <c>/users/{id}</c> probe.</summary>
    public string? ProbeUsers { get; private set; }

    /// <summary>Read one object at increasing offsets to find where offset paging breaks, then exit.</summary>
    public bool ProbeDepth { get; private set; }

    /// <summary>Comma-separated offsets for <c>--probe-depth</c>, replacing the defaults.</summary>
    public string? Depths { get; private set; }

    public string? OutputDirectory { get; private set; }
    public string? FilePrefix { get; private set; }
    public string? Delimiter { get; private set; }

    public bool Overwrite { get; private set; }
    public bool SubdirectoryPerObject { get; private set; }
    public bool Resume { get; private set; }

    public bool PromptCredentials { get; private set; }
    public bool SaveCredentials { get; private set; }
    public bool ClearCredentials { get; private set; }
    public bool AllTenants { get; private set; }
    public bool ListCredentials { get; private set; }

    public bool ValidateOnly { get; private set; }

    /// <summary>Items per object for the --sample diagnostic; null when not requested.</summary>
    public int? SampleSize { get; private set; }

    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }

    public static CommandLineArgs Parse(string[] argv)
    {
        var args = new CommandLineArgs();

        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?":
                    args.ShowHelp = true;
                    break;

                case "--overwrite": args.Overwrite = true; break;
                case "--resume": args.Resume = true; break;
                case "--subdir-per-object": args.SubdirectoryPerObject = true; break;
                case "--prompt-credentials" or "--login": args.PromptCredentials = true; break;
                case "--save-credentials": args.SaveCredentials = true; break;
                case "--clear-credentials": args.ClearCredentials = true; break;
                case "--list-credentials": args.ListCredentials = true; break;
                case "--all": args.AllTenants = true; break;
                case "--validate-only": args.ValidateOnly = true; break;
                case "--list-objects": args.ListObjects = true; break;
                case "--all-objects": args.AllObjects = true; break;
                case "--discover-fields": args.DiscoverFields = true; break;
                case "--probe-lookups": args.ProbeLookups = true; break;
                case "--probe-users": args.ProbeUsers = Next(argv, ref i, arg); break;
                case "--probe-depth": args.ProbeDepth = true; break;
                case "--depths": args.Depths = Next(argv, ref i, arg); break;
                case "--reset-delta": args.ResetDelta = true; break;
                case "--desc" or "--sort-descending": args.SortDescending = true; break;
                case "--verbose" or "-v": args.Verbose = true; break;

                case "--daily":
                    // Shorthand for the standing daily job: resume where the last run ended.
                    args.Since = "last";
                    break;

                case "--sample":
                    // The count is optional: "--sample" alone means 5.
                    if (i + 1 < argv.Length &&
                        int.TryParse(argv[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                    {
                        i++;
                        if (size < 1)
                            throw new ArgumentException("Option '--sample' expects a count of 1 or more.");
                        args.SampleSize = size;
                    }
                    else
                    {
                        args.SampleSize = 5;
                    }
                    break;

                case "--config" or "-c": args.ConfigPath = Next(argv, ref i, arg); break;
                case "--tenant": args.Tenant = Next(argv, ref i, arg); break;
                case "--client-id": args.ClientId = Next(argv, ref i, arg); break;
                case "--client-secret": args.ClientSecret = Next(argv, ref i, arg); break;
                case "--api-base-url": args.ApiBaseUrl = Next(argv, ref i, arg); break;
                case "--client-auth-style":
                    args.ClientAuthStyle = ParseEnum<ClientAuthStyle>(Next(argv, ref i, arg), arg);
                    break;

                case "--rps": args.RequestsPerSecond = ParseDouble(Next(argv, ref i, arg), arg); break;
                case "--burst": args.Burst = ParseInt(Next(argv, ref i, arg), arg); break;

                case "--page-size": args.PageSize = ParseInt(Next(argv, ref i, arg), arg); break;
                case "--paging": args.PagingMode = ParseEnum<PagingMode>(Next(argv, ref i, arg), arg); break;
                case "--max-pages": args.MaxPages = ParseInt(Next(argv, ref i, arg), arg); break;

                // No short alias here on purpose. '-O' would be the obvious one, but
                // switch matching is case-insensitive, so it would collide with the '-o'
                // that means --out — two unrelated options separated only by the shift
                // key is a trap, not a convenience.
                case "--objects": args.Objects = Next(argv, ref i, arg); break;
                case "--objects-file": args.ObjectsFile = Next(argv, ref i, arg); break;

                case "--sort": args.SortField = Next(argv, ref i, arg); break;
                case "--since": args.Since = Next(argv, ref i, arg); break;
                case "--until": args.Until = Next(argv, ref i, arg); break;
                case "--delta-strategy":
                    args.DeltaStrategy = ParseEnum<DeltaStrategy>(Next(argv, ref i, arg), arg);
                    break;
                case "--delta-property": args.DeltaProperty = Next(argv, ref i, arg); break;

                case "--max-rows": args.MaxRecordsPerObject = ParseLong(Next(argv, ref i, arg), arg); break;
                case "--max-per-file": args.MaxRecordsPerFile = ParseLong(Next(argv, ref i, arg), arg); break;

                case "--fields-file" or "-f": args.FieldsFile = Next(argv, ref i, arg); break;
                case "--fields-dir": args.FieldsDirectory = Next(argv, ref i, arg); break;
                case "--selection-list": args.SelectionList = Next(argv, ref i, arg); break;

                case "--out" or "-o": args.OutputDirectory = Next(argv, ref i, arg); break;
                case "--prefix": args.FilePrefix = Next(argv, ref i, arg); break;
                case "--delimiter": args.Delimiter = ParseDelimiter(Next(argv, ref i, arg)); break;

                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Run with --help for usage.");
            }
        }

        // --fields-file names one file, which can only mean one object's column list.
        // Applying it to a whole multi-object run would give every object the same
        // columns, which is never what was meant.
        if (args.FieldsFile is not null && args.AllObjects)
            throw new ArgumentException(
                "--fields-file names a single column list, so it cannot be combined with --all-objects. " +
                "Use --fields-dir for a directory of per-object lists, or name one object with --objects.");

        return args;
    }

    private static string Next(string[] argv, ref int i, string option)
    {
        if (i + 1 >= argv.Length)
            throw new ArgumentException($"Option '{option}' requires a value.");
        return argv[++i];
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects an integer, got '{value}'.");

    private static long ParseLong(string value, string option) =>
        long.TryParse(value.Replace("_", "").Replace(",", ""),
                      NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects an integer, got '{value}'.");

    private static double ParseDouble(string value, string option) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects a number, got '{value}'.");

    private static TEnum ParseEnum<TEnum>(string value, string option) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Option '{option}' expects one of: {string.Join(", ", Enum.GetNames<TEnum>())}. Got '{value}'.");

    private static string ParseDelimiter(string value) => value.ToLowerInvariant() switch
    {
        "tab" or "\\t" => "\t",
        "pipe" => "|",
        "semicolon" => ";",
        "comma" => ",",
        _ => value
    };

    public static string Usage => """
        aprimo-pm-export — export Aprimo PM (Productivity & Spend) object metadata to CSV

        USAGE
          aprimo-pm-export [options]

        One CSV file set per object, named {prefix}-{object}_0001.csv.

        CONFIG
          -c, --config <path>        Config file (default: appsettings.json, with your
                                     appsettings.local.json layered over it). A config named
                                     here is used exactly as given, with nothing layered on.
              --tenant <name>        Aprimo tenant (subdomain of your Aprimo URL)
              --api-base-url <url>   Override the derived API base. PM is
                                     https://{tenant}.aprimo.com/api — note this differs
                                     from the DAM API's {tenant}.dam.aprimo.com/api/core.
              --client-id <id>       OAuth client ID      (prefer env APRIMO_CLIENT_ID)
              --client-secret <s>    OAuth client secret  (prefer env APRIMO_CLIENT_SECRET)
              --prompt-credentials   Type the ID and secret at a prompt (secret masked,
                                     never saved). Also aliased as --login.
              --save-credentials     Prompt, verify against the token endpoint, then store
                                     encrypted with Windows DPAPI under %LOCALAPPDATA%.
              --list-credentials     List tenants with saved credentials (never secrets)
              --clear-credentials    Remove saved credentials for this tenant (--all for
                                     every tenant)
              --client-auth-style <s>  Basic (Authorization header, default) or Body.
                                     Try Body on invalid_client with credentials you trust.

        WHICH OBJECTS
              --objects <names>      Comma-separated object names, e.g.
                                     "projects,tasks,invoices". 'all' selects every
                                     enabled object.
              --all-objects          Every enabled object in the registry
              --list-objects         Print the registry and exit — start here
              --objects-file <path>  Object registry (default: objects.json)

        COLUMNS
              --discover-fields      Write fields/{object}.txt for each selected object,
                                     from GET /metadata/{resourceName} plus a sampled item,
                                     then exit. The intended setup step. Use --overwrite to
                                     regenerate files that already exist.
              --fields-dir <path>    Directory of per-object column lists (default: fields)
          -f, --fields-file <path>   One column list, applied to the selected object.
                                     Cannot be combined with --all-objects.
              --selection-list <n>   Print a named selection list and exit. Decodes the
                                     encodedListItemId codes that multi-value attributes
                                     export as.
              --probe-lookups        Report which of an object's fields hold codes rather
                                     than values, and what each resolution endpoint
                                     actually returns. Read-only, writes no CSV. Run this
                                     before relying on any numeric column.
              --probe-users <ids>    Comma-separated user IDs. Fetches each via
                                     GET /users/{id} and checks whether the user lookup
                                     list is truncated. Read-only. Prints names only -
                                     never email, phone or address. Pass one ID that
                                     resolved in your export and one that did not.
              --probe-depth          Read ONE object (--objects <name>) at increasing
                                     offsets, and at larger page sizes, to find where offset
                                     paging errors, quietly returns the wrong rows, or slows
                                     down. Read-only; about 15 requests. Run it before a
                                     full export of anything large.
              --depths <offsets>     Comma-separated offsets for --probe-depth, replacing
                                     the defaults, e.g. 0,50000,400000. Underscores allowed.

        INCREMENTAL (DELTA) EXPORT
              --daily                Shorthand for --since last
              --since <spec>         last | yesterday | today | a span like 1d / 36h / 90m
                                     | a UTC date like 2026-08-05 | an instant like
                                     2026-08-05T04:00:00Z
              --until <spec>         End of the window. Defaults to this run's start time.
              --reset-delta          Forget the saved mark for the selected objects
              --delta-strategy <s>   SortedDescending (default) stops paging once rows fall
                                     before the window; FullScan reads everything and
                                     filters locally.
              --delta-property <p>   Compare this property instead of each object's own

              PM has no query language, so the window is applied to rows as they arrive
              rather than being pushed to the server. Only /review-tasks can narrow
              server-side. Objects with no modified-date property are skipped by default.
              All boundaries are UTC. Windows are half-open [since, until) and chained, so
              consecutive runs neither overlap nor leave gaps. A mark advances only after a
              run that completes without cancellation and without hitting the row cap.

        RATE LIMITING
              --rps <n>              Requests per second; fractional allowed, 0 = unlimited
              --burst <n>            Token-bucket depth (1 = strictly even spacing)

        PAGING
              --page-size <n>        Items per request. The spec documents no maximum and
                                     defaults to 50; this tool defaults to 100.
              --paging <m>           Offset (default) | LinkFollow (follows _links.next)
              --max-pages <n>        Stop after n pages per object (0 = unlimited)
              --sort <field>         Sort field, applied to every object
              --desc                 Sort descending (the API takes sortAscending=false)

        LIMITS
              --max-rows <n>         Row cap per object (0 = unlimited)
              --max-per-file <n>     Rows per CSV before rolling to a new file (0 = never)

        OUTPUT
          -o, --out <dir>            Output directory
              --prefix <name>        File name prefix (files land as prefix-object_0001.csv)
              --subdir-per-object    Give each object its own subdirectory
              --delimiter <d>        comma | tab | semicolon | pipe, or a literal string
              --overwrite            Replace existing export files

        RUN CONTROL
              --resume               Continue from each object's checkpoint
              --validate-only        Check config and authentication, then exit
              --sample [n]           Fetch n items per object (default 5) and report what
                                     resolves. One request per object; writes no CSV.
                                     Run this before any real export.
          -v, --verbose              Verbose logging
          -h, --help                 Show this help

        EXAMPLES
          # See what is available
          aprimo-pm-export --list-objects

          # Set up column lists for everything
          aprimo-pm-export --all-objects --discover-fields

          # Check what resolves before exporting
          aprimo-pm-export --objects projects,tasks --sample 25

          # Full export of three objects
          aprimo-pm-export --objects activities,projects,tasks --rps 4 --overwrite

          # Standing daily delta across everything
          aprimo-pm-export --all-objects --daily --prefix pm-daily
        """;
}
