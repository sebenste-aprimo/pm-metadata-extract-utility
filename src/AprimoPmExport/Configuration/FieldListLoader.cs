using System.Text;
using System.Text.Json;

namespace AprimoPmExport.Configuration;

/// <summary>
/// Builds one object's CSV column list from an external file, so the field set can be
/// maintained separately from the rest of the configuration.
///
/// <para>Each object gets its own file, <c>fields/{object}.txt</c>, because PM's objects
/// share almost no properties — a single shared list, as the DAM utility has, would be
/// wrong for every object at once.</para>
///
/// <para><b>Format.</b> Names separated by newlines, commas, semicolons or tabs, mixed
/// freely. <c>#</c> or <c>//</c> starts a comment, but only at line start or after
/// whitespace, since real attribute names contain <c>#</c>.</para>
///
/// <list type="table">
/// <item><term><c>title</c></term><description>column <c>title</c>, path
/// <c>title || ea:title</c> — see the note on bare names below</description></item>
/// <item><term><c>prop:title</c></term><description>the item property only, no fallback</description></item>
/// <item><term><c>ea:Business Unit</c></term><description>a single-valued extended attribute</description></item>
/// <item><term><c>eamulti:Channels</c></term><description>a multi-valued (pick-list) attribute</description></item>
/// <item><term><c>Column =&gt; path</c></term><description>explicit mapping, any path</description></item>
/// </list>
///
/// <para><b>Why a bare name means "property, then attribute".</b> DAM has one namespace
/// for tenant metadata, so a bare name there is unambiguously a metadata field. PM has
/// two — the item's own schema properties, and tenant-defined extended attributes — and
/// nothing in a name says which one it is. Resolving the property first and falling back
/// to the attribute means a pasted list of either kind works, and it degrades to an empty
/// cell rather than an error when a name matches neither. Use <c>prop:</c> or <c>ea:</c>
/// when a tenant has a name in both namespaces and the choice matters.</para>
/// </summary>
public static class FieldListLoader
{
    private const string MappingSeparator = "=>";

    /// <summary>Header of the identifier column added to an object that falls back to the shared list.</summary>
    internal const string IdColumnName = "Id";

    /// <summary>
    /// Field names may be separated by newlines, commas, semicolons or tabs, mixed
    /// freely, so a list pasted from a spreadsheet cell or typed one-per-line both work.
    ///
    /// <para>Pipe is deliberately not a separator: <c>||</c> denotes fallback branches
    /// inside a path.</para>
    /// </summary>
    private static readonly char[] Delimiters = { ',', ';', '\t' };

    /// <summary>
    /// Resolves and loads the column list for one object.
    ///
    /// <para>Order: an explicit override, then <c>{FieldsDirectory}/{object}.txt</c>,
    /// then <c>{FieldsDirectory}/{DefaultFieldsFile}</c>. Returns null when none exists,
    /// which leaves the runner to derive columns from the data if
    /// <see cref="ExportConfig.AutoDeriveColumns"/> allows it.</para>
    /// </summary>
    public static List<FieldMapping>? LoadForObject(
        PmObject pmObject,
        ExportConfig config,
        string? configPath,
        string? explicitPath,
        Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = ResolveDataFilePath(explicitPath!, configPath);
            if (!File.Exists(resolved))
                throw new FileNotFoundException(
                    $"Field list file not found: '{Path.GetFullPath(resolved)}'.", resolved);

            return Load(resolved, pmObject, log);
        }

        var directory = ResolveDirectory(config.FieldsDirectory, configPath);

        if (directory is not null)
        {
            var perObject = Path.Combine(directory, pmObject.FieldsFileName);
            if (File.Exists(perObject)) return Load(perObject, pmObject, log);

            if (!string.IsNullOrWhiteSpace(config.DefaultFieldsFile))
            {
                var shared = Path.Combine(directory, config.DefaultFieldsFile!);
                if (File.Exists(shared))
                {
                    log($"  {pmObject.Name}: no {pmObject.FieldsFileName}, using the shared " +
                        $"{config.DefaultFieldsFile} plus the object's own ID.");

                    var columns = Load(shared, pmObject, log);

                    // The shared list is written to suit every object at once, so it cannot name
                    // an ID — each object calls its own something different (projectId, taskId,
                    // invoiceId…). Left like that, a bare export of any object without its own
                    // list would be a CSV with no way to tell one record from another or join it
                    // to anything. So the object's ID, from the registry, always leads.
                    if (!columns.Any(c => c.Column.Equals(IdColumnName, StringComparison.OrdinalIgnoreCase)))
                        columns.Insert(0, new FieldMapping
                        {
                            Column = IdColumnName,
                            Path = "prop:" + pmObject.IdProperty
                        });

                    return columns;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the fields directory: as given, then beside the config file, then beside
    /// the executable. Sitting next to the config is the intuitive place to keep it, and
    /// beside the executable is where a published layout puts it — while the working
    /// directory may be anywhere.
    /// </summary>
    public static string? ResolveDirectory(string directory, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (Directory.Exists(directory)) return directory;
        if (Path.IsPathRooted(directory)) return null;

        if (configPath is not null)
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (configDirectory is not null)
            {
                var besideConfig = Path.Combine(configDirectory, directory);
                if (Directory.Exists(besideConfig)) return besideConfig;
            }
        }

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, directory);
        return Directory.Exists(besideExecutable) ? besideExecutable : null;
    }

    private static string ResolveDataFilePath(string path, string? configPath)
    {
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
        return File.Exists(besideExecutable) ? besideExecutable : path;
    }

    public static List<FieldMapping> Load(string path, PmObject? pmObject, Action<string> log)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Field list file not found: '{Path.GetFullPath(path)}'.", path);

        // Strip a UTF-8 BOM, which would otherwise corrupt the first field name.
        var text = File.ReadAllText(path).TrimStart('﻿');

        log($"  Reading column list from {Path.GetFullPath(path)}");
        return LoadPlainList(text, path, pmObject, log);
    }

    private static List<FieldMapping> LoadPlainList(
        string text,
        string path,
        PmObject? pmObject,
        Action<string> log)
    {
        var mappings = new List<FieldMapping>();
        var lines = text.Split('\n');
        var explicitCount = 0;
        var attributeCount = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = StripComment(lines[i]).Trim();

            if (line.Length == 0) continue;

            // A line with '=>' is one explicit mapping. Never split it, since a path can
            // legitimately contain a comma inside a selector.
            var separator = line.IndexOf(MappingSeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                var column = line.Substring(0, separator).Trim();
                var mappedPath = line.Substring(separator + MappingSeparator.Length).Trim();

                if (column.Length == 0 || mappedPath.Length == 0)
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(path)} line {lineNumber}: expected " +
                        $"'Column {MappingSeparator} path', got '{line}'.");

                mappings.Add(new FieldMapping { Column = column, Path = mappedPath });
                explicitCount++;
                continue;
            }

            foreach (var token in line.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Trim();
                if (name.Length == 0) continue;

                var isPrefixed =
                    name.StartsWith("ea:", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("ea#", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("eamulti:", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("eamulti#", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("prop:", StringComparison.OrdinalIgnoreCase);

                if (isPrefixed)
                {
                    if (!name.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) attributeCount++;

                    mappings.Add(new FieldMapping
                    {
                        Column = ToColumnName(StripPrefix(name)),
                        Path = name
                    });
                    continue;
                }

                mappings.Add(new FieldMapping
                {
                    Column = ToColumnName(name),
                    Path = BareNamePath(name)
                });
            }
        }

        if (mappings.Count == 0)
            throw new InvalidOperationException(
                $"'{Path.GetFullPath(path)}' contained no field entries (only blanks and comments).");

        log($"  {mappings.Count} column(s): {explicitCount} explicit mapping(s), " +
            $"{attributeCount} prefixed, {mappings.Count - explicitCount - attributeCount} bare name(s).");

        if (pmObject is { SupportsExtendedAttributes: false } && attributeCount > 0)
            log($"  Note: '{pmObject.Name}' has no extendedAttributes in its schema, but the column list " +
                "references extended attributes. Those columns will be empty.");

        return mappings;
    }

    /// <summary>
    /// Expands a bare name to "the item property, then the extended attribute of that
    /// name". See the class remarks for why both are tried.
    /// </summary>
    internal static string BareNamePath(string name)
    {
        var property = ToCamelCase(name);

        // A name containing a space or other non-identifier character cannot be a JSON
        // property, so there is nothing to fall back from — emit the attribute alone
        // rather than a branch that can never match.
        if (!LooksLikeProperty(property)) return "ea:" + name;

        return $"{property} || ea:{name}";
    }

    private static bool LooksLikeProperty(string value)
    {
        if (value.Length == 0) return false;
        if (!char.IsAsciiLetter(value[0]) && value[0] != '_') return false;

        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;

        return true;
    }

    private static string StripPrefix(string name)
    {
        var colon = name.IndexOfAny(new[] { ':', '#' });
        return colon >= 0 && colon + 1 < name.Length ? name.Substring(colon + 1) : name;
    }

    /// <summary>
    /// Removes a trailing <c>#</c> or <c>//</c> comment.
    ///
    /// <para>A comment marker only counts at the start of the line or after whitespace.
    /// Attribute names really do contain '#' in practice, so treating every '#' as a
    /// comment would silently truncate them — and it would also break the <c>ea#123</c>
    /// shorthand, where the '#' is syntax.</para>
    /// </summary>
    internal static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var isHash = line[i] == '#';
            var isSlashes = line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/';

            if (!isHash && !isSlashes) continue;

            // Only a comment when it opens the line or follows whitespace.
            if (i == 0 || char.IsWhiteSpace(line[i - 1]))
                return line.Substring(0, i);
        }

        return line;
    }

    /// <summary>
    /// Derives a CSV header from a field name using Aprimo's own <c>outputName</c>
    /// convention: whitespace becomes an underscore and anything outside
    /// <c>[A-Za-z0-9_]</c> is dropped. Kept identical to the DAM utility so a downstream
    /// consumer sees the same header rules from both tools.
    /// </summary>
    internal static string ToColumnName(string fieldName)
    {
        var builder = new StringBuilder(fieldName.Length);

        foreach (var c in fieldName)
        {
            if (char.IsWhiteSpace(c)) builder.Append('_');
            else if (char.IsAsciiLetterOrDigit(c) || c == '_') builder.Append(c);
            // Everything else is dropped, matching Aprimo.
        }

        // Never return an empty header; fall back to the raw name so validation can
        // report something recognisable.
        return builder.Length > 0 ? builder.ToString() : fieldName;
    }

    internal static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>
    /// Builds a column list from the properties of one observed item.
    ///
    /// <para>The fallback when an object has no column list at all. Scalars become
    /// columns; objects and arrays are skipped, since they would land in a cell as raw
    /// JSON — except the two extended-attribute arrays, which are expanded into one
    /// column per attribute actually present.</para>
    ///
    /// <para>A column set inferred from a single item is not stable: an attribute absent
    /// from that item produces no column at all. Fine for a first look, not for a
    /// recurring job — <c>--discover-fields</c> exists to pin it down.</para>
    /// </summary>
    public static List<FieldMapping> DeriveFromItem(JsonElement item, PmObject pmObject)
    {
        var mappings = new List<FieldMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item.ValueKind != JsonValueKind.Object) return mappings;

        void Add(string column, string path)
        {
            var header = ToColumnName(column);
            if (header.Length == 0 || !seen.Add(header)) return;
            mappings.Add(new FieldMapping { Column = header, Path = path });
        }

        foreach (var property in item.EnumerateObject())
        {
            var name = property.Name;

            if (name.Equals(CompiledPathNames.ExtendedAttributes, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(CompiledPathNames.MultiValueExtendedAttributes, StringComparison.OrdinalIgnoreCase))
                continue;

            // Objects and arrays would be emitted as raw JSON in a cell. Skipping them
            // keeps a derived list readable; map one explicitly if you want it.
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;

            Add(name, "prop:" + name);
        }

        AddAttributes(item, CompiledPathNames.ExtendedAttributes, "attributeName", "eaId", "ea", Add);
        AddAttributes(item, CompiledPathNames.MultiValueExtendedAttributes,
            "attributeName", "extAttributeId", "eamulti", Add);

        return mappings;
    }

    private static void AddAttributes(
        JsonElement item,
        string arrayProperty,
        string nameKey,
        string idKey,
        string prefix,
        Action<string, string> add)
    {
        if (!item.TryGetProperty(arrayProperty, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var attribute in array.EnumerateArray())
        {
            if (attribute.ValueKind != JsonValueKind.Object) continue;

            var name = attribute.TryGetProperty(nameKey, out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            var id = attribute.TryGetProperty(idKey, out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetRawText()
                : null;

            // attributeName is readOnly in the spec, so a tenant may return it null.
            // The ID is always there, which is why it is the fallback.
            if (!string.IsNullOrWhiteSpace(name)) add(name!, $"{prefix}:{name}");
            else if (id is not null) add($"{prefix}_{id}", $"{prefix}#{id}");
        }
    }
}

/// <summary>Property names shared between the loader and the path grammar.</summary>
internal static class CompiledPathNames
{
    public const string ExtendedAttributes = "extendedAttributes";
    public const string MultiValueExtendedAttributes = "multipleValueExtendedAttributes";
}
