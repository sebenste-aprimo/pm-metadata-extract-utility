using System.Globalization;
using System.Text;
using AprimoPmExport.Configuration;

namespace AprimoPmExport.Export;

/// <summary>
/// Writes <c>manifest.csv</c>: one row per object in the run.
///
/// <para>The DAM utility has no equivalent because it exports one thing — the console
/// summary says everything there is to say. A run here touches two dozen collections,
/// each with its own row count, file set and outcome, and that does not survive being
/// read off a scrolling console. The manifest is also what makes a run machine-checkable:
/// a downstream job can assert that every expected object produced a row before it
/// starts loading the CSVs.</para>
/// </summary>
public static class ManifestWriter
{
    public const string FileName = "manifest.csv";

    private static readonly string[] Headers =
    {
        "Object", "Outcome", "Rows", "Files", "Pages", "ServerTotalApprox",
        "DuplicatesDropped", "ElapsedSeconds", "HitCap", "WindowComplete",
        "DeltaSummary", "Message", "FirstFile"
    };

    public static string? Write(ExportConfig config, RunResult result, DateTimeOffset startedAt, Action<string> log)
    {
        if (!config.Output.WriteManifest) return null;

        var path = Path.Combine(config.Output.Directory, FileName);

        try
        {
            Directory.CreateDirectory(config.Output.Directory);

            var builder = new StringBuilder(4096);
            var newLine = config.NewLineString();

            builder.Append(string.Join(config.Output.Delimiter, Headers.Select(h => Escape(h, config))))
                   .Append(newLine);

            foreach (var o in result.Objects)
            {
                var cells = new[]
                {
                    o.Object,
                    o.Outcome.ToString(),
                    o.RowsWritten.ToString(CultureInfo.InvariantCulture),
                    o.FilesCreated.ToString(CultureInfo.InvariantCulture),
                    o.PagesFetched.ToString(CultureInfo.InvariantCulture),
                    o.ApproximateTotalAvailable?.ToString(CultureInfo.InvariantCulture) ?? "",
                    o.DuplicatesDropped.ToString(CultureInfo.InvariantCulture),
                    o.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    o.HitCap ? "true" : "false",
                    o.WindowComplete ? "true" : "false",
                    o.DeltaSummary ?? "",
                    o.Message ?? "",
                    o.FilePaths.Count > 0 ? Path.GetFileName(o.FilePaths[0]) : ""
                };

                builder.Append(string.Join(config.Output.Delimiter, cells.Select(c => Escape(c, config))))
                       .Append(newLine);
            }

            // Overwritten every run on purpose: it describes this run, not a history.
            File.WriteAllText(path, builder.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: config.Output.WriteByteOrderMark));

            log($"Manifest       : {path} ({result.Objects.Count} object(s), run started {startedAt:u})");
            return path;
        }
        catch (Exception ex)
        {
            log($"Warning: could not write the manifest ({ex.Message}). The CSV output is unaffected.");
            return null;
        }
    }

    /// <summary>
    /// RFC 4180 quoting, matching <see cref="CsvRollingWriter"/>.
    ///
    /// <para>No formula guard here: every cell is a name, a number or a message this tool
    /// produced itself, so there is no untrusted value to defend against — and the guard's
    /// leading apostrophe would only make the file harder to parse.</para>
    /// </summary>
    private static string Escape(string? value, ExportConfig config)
    {
        value ??= "";

        var mustQuote =
            value.Contains('"') ||
            value.Contains('\r') ||
            value.Contains('\n') ||
            value.Contains(config.Output.Delimiter, StringComparison.Ordinal);

        if (!mustQuote) return value;

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            if (c == '"') builder.Append('"');
            builder.Append(c);
        }
        builder.Append('"');
        return builder.ToString();
    }
}

internal static class OutputConfigExtensions
{
    public static string NewLineString(this ExportConfig config) =>
        config.Output.NewLine.Equals("LF", StringComparison.OrdinalIgnoreCase) ? "\n" : "\r\n";
}
