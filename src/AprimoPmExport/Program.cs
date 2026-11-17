using System.Net;
using AprimoPmExport.Auth;
using AprimoPmExport.Configuration;
using AprimoPmExport.Export;
using AprimoPmExport.Http;

namespace AprimoPmExport;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitInvalidConfig = 2;
    private const int ExitCancelled = 3;

    /// <summary>Some objects exported, some failed. Distinct so a scheduler can tell them apart.</summary>
    private const int ExitPartial = 4;

    public static async Task<int> Main(string[] argv)
    {
        var verbose = false;
        void Log(string message) =>
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        void Trace(string message) { if (verbose) Log(message); }

        CommandLineArgs args;
        try
        {
            args = CommandLineArgs.Parse(argv);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitInvalidConfig;
        }

        if (args.ShowHelp)
        {
            Console.WriteLine(CommandLineArgs.Usage);
            return ExitOk;
        }

        verbose = args.Verbose;

        ExportConfig config;
        string? configPath;
        try
        {
            config = ConfigLoader.Load(args, Log);
            configPath = ConfigLoader.LastConfigPath;
        }
        catch (Exception ex)
        {
            Log("Configuration error: " + ex.Message);
            return ExitInvalidConfig;
        }

        // Credential-store management runs before anything else and then exits: these
        // commands are about the store, not about exporting.
        if (args.ListCredentials)
        {
            var tenants = CredentialStore.ListTenants(Log);
            Log($"Credential store: {CredentialStore.DefaultStorePath}");
            if (tenants.Count == 0)
                Log("No saved credentials. Run --save-credentials to add some.");
            else
                foreach (var t in tenants) Console.WriteLine("  " + t);
            return ExitOk;
        }

        if (args.ClearCredentials)
        {
            var target = args.AllTenants ? null : config.Aprimo.Tenant;
            return CredentialStore.Clear(target, Log) ? ExitOk : ExitError;
        }

        // The object registry is needed by almost everything below, including
        // --list-objects, which must work without credentials.
        ObjectCatalog catalog;
        IReadOnlyList<PmObject> selected;
        try
        {
            catalog = ObjectCatalog.Load(ConfigLoader.ResolveObjectsFile(config, configPath), Log);
            selected = catalog.Resolve(config.Objects);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentException)
        {
            Log("Object registry: " + ex.Message);
            return ExitInvalidConfig;
        }

        if (args.ListObjects)
        {
            PrintObjectList(catalog);
            return ExitOk;
        }

        if (selected.Count == 0)
        {
            Log("No objects selected. Use --objects <names> or --all-objects; --list-objects shows what exists.");
            return ExitInvalidConfig;
        }

        // Check everything that does not depend on credentials FIRST.
        //
        // Order matters here. The credential prompt used to run before any validation,
        // so a missing tenant was reported only after you had typed a client ID and
        // secret — which were then discarded, because the endpoints had never resolved
        // and no credential could have worked. Failing here costs nothing and asks for
        // nothing.
        if (!ReportErrors(config.ValidateExcludingCredentials().ToList(), Log))
            return ExitInvalidConfig;

        // Resetting a delta mark is a local file operation, so it does not need
        // credentials either — same reason as above.
        if (args.ResetDelta)
        {
            DeltaStateStore.Reset(config, selected, Log);
            if (args.Since is null) return ExitOk;
        }

        // Fill gaps from the encrypted store, unless we are about to overwrite it.
        if (!args.SaveCredentials)
            CredentialStore.TryFill(config.Aprimo, Log);

        // Then offer interactive entry, so missing credentials become a prompt rather
        // than an error — but only when a real console is attached.
        CredentialPrompt.EnsureCredentials(config.Aprimo, args.PromptCredentials || args.SaveCredentials, Log);

        // Now the credentials themselves, once every source has had its chance.
        if (!ReportErrors(config.Aprimo.ValidateCredentials().ToList(), Log))
            return ExitInvalidConfig;

        // Ctrl+C flushes and checkpoints rather than killing the process mid-write.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // Second Ctrl+C: let the runtime terminate.
            e.Cancel = true;
            Log("Ctrl+C received — stopping after the current page. Press Ctrl+C again to abort immediately.");
            cts.Cancel();
        };

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(4, config.Throttle.MaxConcurrentRequests * 2)
        };

        using var apiHttp = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(config.Source.RequestTimeoutSeconds)
        };
        apiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("aprimo-pm-export/1.0");

        // Separate client for the token host: different endpoint, different timeout
        // profile, and deliberately not routed through the rate limiter so credential
        // renewal can never be starved by a long export.
        using var authHttp = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        authHttp.DefaultRequestHeaders.UserAgent.ParseAdd("aprimo-pm-export/1.0");

        using var tokenProvider = new OAuthTokenProvider(authHttp, config.Aprimo, config.Source.Retry, Log);
        using var limiter = new RateLimiter(
            config.Throttle.RequestsPerSecond,
            config.Throttle.Burst,
            config.Throttle.MaxConcurrentRequests);

        var client = new ApiClient(apiHttp, tokenProvider, limiter, config, Trace);
        var metadata = new MetadataCatalog(client, Log);

        try
        {
            if (args.SaveCredentials)
            {
                if (!CredentialStore.IsSupported)
                {
                    Log("--save-credentials needs Windows DPAPI, which is not available here. " +
                        "Use APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET instead.");
                    return ExitError;
                }

                // Verify before storing, so a mistyped secret is never persisted.
                Log("Verifying the credentials against the token endpoint before saving…");
                await tokenProvider.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                Log("Verified.");

                CredentialStore.Save(
                    config.Aprimo.Tenant, config.Aprimo.ClientId, config.Aprimo.ClientSecret, Log);

                Log("Later runs need no credentials — just 'aprimo-pm-export --sample'.");
                return ExitOk;
            }

            if (args.ValidateOnly)
            {
                Log("Validating configuration and authentication…");
                var token = await tokenProvider.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                Log($"Authentication succeeded (token length {token.Length}, " +
                    $"expires {tokenProvider.ExpiresAt:u}).");
                Log("Configuration is valid. Exiting because --validate-only was given.");
                return ExitOk;
            }

            if (args.SelectionList is { } listName)
            {
                var items = await metadata.GetSelectionListAsync(listName, cts.Token).ConfigureAwait(false);

                if (items.Count == 0)
                {
                    Log($"Selection list '{listName}' returned no items, or does not exist on this tenant.");
                    return ExitError;
                }

                Console.WriteLine($"Id,Value   ({items.Count} item(s) in '{listName}')");
                foreach (var (id, value) in items) Console.WriteLine($"{id},{value}");
                return ExitOk;
            }

            if (args.ProbeLookups)
            {
                var probe = new LookupProbe(client, metadata, Log);
                var found = await probe.RunAsync(selected, cts.Token).ConfigureAwait(false);
                return found ? ExitOk : ExitError;
            }

            if (args.ProbeUsers is { } userIdText)
            {
                var (userIds, rejected) = LookupProbe.ParseIds(userIdText);

                if (rejected.Count > 0)
                    Log($"Ignoring non-numeric user ID(s): {string.Join(", ", rejected)}. " +
                        "User IDs are whole numbers, e.g. 1234.");

                if (userIds.Count == 0)
                {
                    Log("--probe-users needs at least one numeric user ID.");
                    return ExitInvalidConfig;
                }

                var probe = new LookupProbe(client, metadata, Log);
                var found = await probe.RunUsersAsync(selected, userIds, cts.Token).ConfigureAwait(false);
                return found ? ExitOk : ExitError;
            }

            if (args.ProbeDepth)
            {
                // One object only. Probing depth is about a single collection's behaviour,
                // and the default selection is every object — silently picking the first of
                // them would test something nobody asked about.
                if (selected.Count != 1)
                {
                    Log($"--probe-depth tests one object, but {selected.Count} are selected. " +
                        "Name one, e.g. --objects projects.");
                    return ExitInvalidConfig;
                }

                List<long>? depths = null;

                if (args.Depths is { } depthText)
                {
                    var (valid, rejected) = DepthProbe.ParseDepths(depthText);

                    if (rejected.Count > 0)
                        Log($"Ignoring invalid offset(s): {string.Join(", ", rejected)}. " +
                            "Offsets are whole numbers from 0 up to 2,147,483,647.");

                    if (valid.Count == 0)
                    {
                        Log("--depths needs at least one valid offset.");
                        return ExitInvalidConfig;
                    }

                    depths = valid;
                }

                var depthProbe = new DepthProbe(config, client, Log);
                var reached = await depthProbe.RunAsync(selected[0], depths, cts.Token).ConfigureAwait(false);
                return reached ? ExitOk : ExitError;
            }

            if (args.DiscoverFields)
                return await DiscoverFieldsAsync(config, configPath, client, metadata, selected, args, Log, cts.Token)
                    .ConfigureAwait(false);

            PrintPlan(config, selected, args, Log);

            if (args.SampleSize is { } sampleSize)
            {
                Log($"Fetching up to {sampleSize} item(s) per object for inspection — no CSV will be written.");
                var inspector = new SampleInspector(
                    config, client, metadata, configPath, args.FieldsFile, Log);
                var found = await inspector.RunAsync(selected, sampleSize, cts.Token).ConfigureAwait(false);
                return found ? ExitOk : ExitError;
            }

            var startedAt = DateTimeOffset.UtcNow;

            var runner = new MultiObjectRunner(config, client, metadata, configPath, args.FieldsFile, Log);
            var result = await runner
                .RunAsync(selected, args.Since, args.Until, args.Resume, cts.Token)
                .ConfigureAwait(false);

            PrintSummary(result, config, tokenProvider, Log);
            ManifestWriter.Write(config, result, startedAt, Log);

            if (result.Cancelled) return ExitCancelled;
            return result.Failed > 0 ? ExitPartial : ExitOk;
        }
        catch (AuthenticationFailedException ex)
        {
            Log("Authentication failed.");
            Console.Error.WriteLine(ex.Message);
            return ExitError;
        }
        catch (ApiException ex)
        {
            Log("The API request failed.");
            Console.Error.WriteLine(ex.Message);
            return ExitError;
        }
        catch (FormatException ex)
        {
            Log("A field mapping path is invalid: " + ex.Message);
            return ExitInvalidConfig;
        }
        catch (InvalidOperationException ex)
        {
            Log(ex.Message);
            return ExitError;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Log("Unexpected error: " + ex);
            return ExitError;
        }
    }

    /// <summary>
    /// Prints configuration problems. Returns true when there were none, so callers read
    /// as "if this did not check out, stop".
    /// </summary>
    private static bool ReportErrors(IReadOnlyList<string> errors, Action<string> log)
    {
        if (errors.Count == 0) return true;

        log($"Configuration is invalid ({errors.Count} problem(s)):");
        foreach (var error in errors) Console.Error.WriteLine("  - " + error);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Run with --help for available options.");
        return false;
    }

    /// <summary>
    /// Picks where <c>--discover-fields</c> writes.
    ///
    /// <para>Reading a column list resolves current directory → beside the config →
    /// beside the executable, and that is right for reading. For <i>writing</i> it is a
    /// trap: under <c>dotnet run</c> the config itself lives in <c>bin\…\net8.0\</c>, so
    /// the only existing <c>fields\</c> is the one copied into the build output. Generated
    /// files landed there — invisible, outside source control, and deleted by a clean —
    /// while the <c>fields\</c> in the project looked untouched.</para>
    ///
    /// <para>So a build-output location is rejected in favour of the current directory,
    /// which is both visible and the first place the reader looks next run. An explicit
    /// <c>--fields-dir</c> is always honoured as given.</para>
    /// </summary>
    private static string ResolveDiscoveryDirectory(
        ExportConfig config,
        string? configPath,
        Action<string> log)
    {
        var resolved = FieldListLoader.ResolveDirectory(config.FieldsDirectory, configPath);

        if (resolved is not null && !IsBuildOutput(resolved))
            return resolved;

        var fallback = Path.Combine(Directory.GetCurrentDirectory(), config.FieldsDirectory);

        if (resolved is not null)
            log($"Note: '{Path.GetFullPath(resolved)}' is a build output directory, so anything written " +
                "there would be lost on a clean and would not be under source control. Writing to " +
                $"'{Path.GetFullPath(fallback)}' instead — the reader checks the current directory " +
                "first, so the next run will pick it up. Pass --fields-dir to choose somewhere else.");

        return fallback;
    }

    /// <summary>True for a path inside a <c>bin</c> or <c>obj</c> directory.</summary>
    private static bool IsBuildOutput(string path)
    {
        var full = Path.GetFullPath(path);

        return full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                   segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> DiscoverFieldsAsync(
        ExportConfig config,
        string? configPath,
        ApiClient client,
        MetadataCatalog metadata,
        IReadOnlyList<PmObject> selected,
        CommandLineArgs args,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var directory = ResolveDiscoveryDirectory(config, configPath, log);

        log($"Writing column lists to {Path.GetFullPath(directory)}");
        Directory.CreateDirectory(directory);

        var generator = new FieldListGenerator(config, client, metadata, log);
        var written = 0;

        foreach (var pmObject in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log($"{pmObject.Name}:");

            try
            {
                var path = await generator
                    .GenerateAsync(pmObject, directory, args.Overwrite,
                                   args.SampleSize ?? 25, cancellationToken)
                    .ConfigureAwait(false);

                if (path is not null) written++;
            }
            catch (Exception ex) when (ex is ApiException or IOException)
            {
                log($"  Failed: {ex.Message.Split('\n')[0]}");
            }
        }

        log("");
        log($"Wrote {written} column list(s) of {selected.Count} object(s) selected.");
        log("Review and edit them, then run --sample to confirm what resolves before exporting.");

        return written > 0 ? ExitOk : ExitError;
    }

    private static void PrintObjectList(ObjectCatalog catalog)
    {
        Console.WriteLine();
        Console.WriteLine($"{catalog.Objects.Count} object(s) in the registry " +
                          $"({catalog.EnabledObjects.Count} selected by --all-objects):");
        Console.WriteLine();
        Console.WriteLine($"  {"NAME",-24} {"PATH",-30} {"DELTA",-14} {"EA",-4} STATUS");
        Console.WriteLine("  " + new string('─', 86));

        foreach (var o in catalog.Objects)
        {
            var delta = o.SupportsDelta ? o.ModifiedProperty! : "(none)";
            var ea = o.SupportsExtendedAttributes ? "yes" : "no";

            var status = !o.Enabled ? "disabled — name it explicitly"
                       : o.ScopedToCallingUser ? "user-scoped"
                       : o.ModifiedAfterParam is not null ? "server-side delta"
                       : "";

            Console.WriteLine($"  {o.Name,-24} {o.Path,-30} {delta,-14} {ea,-4} {status}");
        }

        Console.WriteLine();
        Console.WriteLine("  DELTA  the property a --since window compares; '(none)' means full export only.");
        Console.WriteLine("  EA     whether the schema carries tenant-defined extended attributes.");
        Console.WriteLine();
        Console.WriteLine("  Next:  aprimo-pm-export --all-objects --discover-fields");
    }

    private static void PrintPlan(
        ExportConfig config,
        IReadOnlyList<PmObject> selected,
        CommandLineArgs args,
        Action<string> log)
    {
        var throttle = config.Throttle;
        var paging = config.Source.Paging;
        var limits = config.Limits;

        log($"API endpoint    : {config.Aprimo.ResolvedApiBaseUrl}");
        log($"Token endpoint  : {config.Aprimo.ResolvedTokenUrl}");
        log($"Objects         : {selected.Count} — " +
            string.Join(", ", selected.Take(8).Select(o => o.Name)) +
            (selected.Count > 8 ? $", … (+{selected.Count - 8})" : ""));
        log($"Paging          : {paging.Mode}, {paging.PageSize} per request, " +
            $"sort {(config.Source.SortField ?? "(per object)")} " +
            (config.Source.SortAscending ? "ascending" : "descending"));
        log("Rate limit      : " +
            (throttle.RequestsPerSecond > 0
                ? $"{throttle.RequestsPerSecond:0.##} req/s, burst {throttle.Burst}, " +
                  $"max {throttle.MaxConcurrentRequests} concurrent"
                : "disabled (no client-side throttling)"));
        log("Row limits      : " +
            (limits.MaxRecordsPerObject > 0
                ? $"cap {limits.MaxRecordsPerObject:N0} rows per object"
                : "no row cap") +
            ", " +
            (limits.MaxRecordsPerFile > 0 ? $"{limits.MaxRecordsPerFile:N0} rows/file" : "single file"));
        log($"Output          : {Path.GetFullPath(config.Output.Directory)}" +
            $"{Path.DirectorySeparatorChar}{config.Output.FilePrefix}-<object>_NNNN.csv");

        if (args.Since is { } since)
        {
            log($"Delta           : since '{since}'" + (args.Until is { } until ? $", until '{until}'" : "") +
                $", strategy {config.Source.Delta.Strategy}");

            var noDelta = selected.Where(o => !o.SupportsDelta).Select(o => o.Name).ToList();
            if (noDelta.Count > 0 && config.Source.Delta.SkipObjectsWithoutModifiedProperty)
                log($"                  {noDelta.Count} selected object(s) have no modified-date property " +
                    $"and will be skipped: {string.Join(", ", noDelta)}");

            if (config.Source.Delta.Strategy == DeltaStrategy.SortedDescending && config.Source.SortAscending)
                log("                  Note: SortedDescending overrides the ascending sort per object so " +
                    "paging can stop early.");
        }

        if (limits.MaxRecordsPerObject > 0)
            log("Note: a per-object row cap is in effect — this run will not export the full data set.");

        if (args.Resume)
            log("Resume requested — each object continues from its checkpoint if one matches.");
    }

    private static void PrintSummary(
        RunResult result,
        ExportConfig config,
        OAuthTokenProvider tokens,
        Action<string> log)
    {
        var seconds = Math.Max(result.Elapsed.TotalSeconds, 0.001);

        Console.Error.WriteLine();
        log("──────── Export summary ────────");
        log($"Objects         : {result.Succeeded} succeeded, {result.Skipped} skipped, " +
            $"{result.Failed} failed (of {result.Objects.Count})");
        log($"Rows written    : {result.TotalRows:N0}");
        log($"Files created   : {result.TotalFiles}");
        log($"Elapsed         : {result.Elapsed:hh\\:mm\\:ss}");
        log($"Throughput      : {result.TotalRows / seconds:F0} rows/s, " +
            $"{result.RequestCount / seconds:F2} req/s effective");
        log($"API requests    : {result.RequestCount:N0} ({result.RetryCount:N0} retried)");
        log($"Payload         : {FormatBytes(result.BytesReceived)}");
        log($"Token requests  : {tokens.TokenRequestCount}");

        if (result.Cancelled)
            log("Stopped early   : cancelled by user. Re-run with --resume to continue.");

        Console.Error.WriteLine();
        log("Per object:");

        foreach (var o in result.Objects.OrderByDescending(o => o.RowsWritten))
        {
            var detail = o.Outcome switch
            {
                ObjectOutcome.Succeeded => $"{o.RowsWritten,10:N0} rows  {o.FilesCreated} file(s)" +
                                           (o.HitCap ? "  [capped]" : ""),
                ObjectOutcome.Skipped => "   skipped  " + (o.Message ?? ""),
                ObjectOutcome.Failed => "    FAILED  " + (o.Message ?? ""),
                _ => $"{o.RowsWritten,10:N0} rows  cancelled"
            };

            Console.Error.WriteLine($"  {o.Object,-24} {detail}");
        }

        if (result.Failed > 0)
        {
            Console.Error.WriteLine();
            log($"{result.Failed} object(s) failed. The successful ones are complete and usable; " +
                "re-run just the failures with --objects <names>.");
        }

        if (result.TotalRows == 0)
            log("No rows were written. Run --sample to see what each endpoint actually returns.");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
