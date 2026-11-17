using System.Globalization;
using System.Text;
using System.Text.Json;
using AprimoPmExport.Configuration;

namespace AprimoPmExport.Export;

internal abstract record PathStep;
internal sealed record PropStep(string Name) : PathStep;
internal sealed record IndexStep(int Index) : PathStep;
internal sealed record AllStep : PathStep;
internal sealed record FilterStep(string Key, string Value) : PathStep;

/// <summary>
/// A parsed field mapping path.
///
/// <para>Grammar:</para>
/// <code>
///   path     := branch ('||' branch)*
///   branch   := segment ('.' segment)*
///   segment  := name selector*
///   selector := '[' ( index | '*' | key '=' value ) ']'
/// </code>
///
/// <para><c>||</c> separates fallback branches, tried left to right until one yields a
/// value. That matters more on PM than on DAM: several PM schemas expose the same idea
/// under different names across objects, and a number of properties are typed as bare
/// <c>object</c> with no properties defined at all.</para>
///
/// <para><b>Extended-attribute shorthands.</b> Tenant-defined metadata in PM lives in two
/// arrays on the item rather than in DAM's <c>fields.items[].localizedValues[]</c>:</para>
/// <code>
///   ea:Region        -> extendedAttributes[attributeName=Region].eaValue
///   ea#4102          -> extendedAttributes[eaId=4102].eaValue
///   eamulti:Channels -> multipleValueExtendedAttributes[attributeName=Channels].encodedListItemId
///   eamulti#4110     -> multipleValueExtendedAttributes[extAttributeId=4110].encodedListItemId
/// </code>
///
/// <para>The <c>#</c> forms exist because <c>attributeName</c> is declared
/// <c>readOnly</c> in the spec, which leaves open whether every tenant populates it on a
/// read. <c>eaId</c> is always present, so an ID-based mapping is the reliable fallback if
/// a tenant returns names as null — <c>--discover-fields</c> writes both forms into the
/// generated column list for exactly that reason.</para>
///
/// <para>Note that a multi-value EA resolves to <c>encodedListItemId</c>, an integer, not
/// a label. PM stores the label separately in a selection list; resolve it with
/// <c>GET /metadata/selection-lists/{name}</c> if the numeric code is not what the
/// downstream consumer wants.</para>
///
/// <para>Examples against the PM Project schema:</para>
/// <code>
///   projectId
///   projectStatus
///   outlook.healthIndicator
///   ea:Business Unit                       -- attribute names may contain spaces
///   extendedAttributes[*].attributeName    -- every attribute name, joined
/// </code>
/// </summary>
public sealed class CompiledPath
{
    private const string BranchSeparator = "||";

    /// <summary>Property holding single-valued tenant-defined metadata.</summary>
    public const string ExtendedAttributesProperty = "extendedAttributes";

    /// <summary>Property holding multi-valued (pick-list) tenant-defined metadata.</summary>
    public const string MultiValueExtendedAttributesProperty = "multipleValueExtendedAttributes";

    /// <summary>Fallback branches, in evaluation order.</summary>
    internal IReadOnlyList<IReadOnlyList<PathStep>> Branches { get; }

    /// <summary>Number of fallback branches; 1 unless the path uses <c>||</c>.</summary>
    public int BranchCount => Branches.Count;

    public string Raw { get; }
    public string Expanded { get; }

    /// <summary>
    /// Set when the path was written as <c>label:…</c>: the key under which the resolved
    /// value's list is registered. Null for an ordinary path.
    ///
    /// <para>For <c>label:projectStatus</c> this is <c>projectStatus</c> — the property
    /// name, which is how <c>GET /metadata/{resourceName}</c> identifies the field whose
    /// list the code belongs to. For <c>label:ea:Region</c> it is <c>Region</c>,
    /// the attribute name.</para>
    /// </summary>
    public string? LookupKey { get; }

    /// <summary>
    /// Set when the path was written as <c>user:…</c>: its values are user IDs, resolved to
    /// names through <c>GET /users/{id}</c> rather than through a lookup list.
    ///
    /// <para>A separate form from <c>label:</c> because the route is different and so are
    /// its costs — one request per distinct user, rather than one fetch per list — and
    /// because the lookup list behind user fields is unusable: it is capped and omits
    /// inactive users.</para>
    /// </summary>
    public bool IsUser { get; }

    /// <summary>Extended-attribute names this path reads, for the <c>--sample</c> cross-check.</summary>
    public IReadOnlyList<string> ReferencedAttributeNames { get; }

    /// <summary>Extended-attribute IDs this path reads, for the <c>--sample</c> cross-check.</summary>
    public IReadOnlyList<string> ReferencedAttributeIds { get; }

    /// <summary>True when any branch reads from either extended-attribute array.</summary>
    public bool TouchesExtendedAttributes { get; }

    /// <summary>True when a branch enumerates attributes dynamically (e.g. <c>extendedAttributes[*]</c>).</summary>
    public bool WalksAllExtendedAttributes { get; }

    private CompiledPath(
        string raw,
        string expanded,
        IReadOnlyList<IReadOnlyList<PathStep>> branches,
        string? lookupKey,
        bool isUser)
    {
        Raw = raw;
        Expanded = expanded;
        Branches = branches;
        LookupKey = lookupKey;
        IsUser = isUser;

        var names = new List<string>();
        var ids = new List<string>();
        var touches = false;
        var walksAll = false;

        foreach (var steps in branches)
        {
            if (steps.Count == 0 || steps[0] is not PropStep root) continue;

            var isEa = root.Name.Equals(ExtendedAttributesProperty, StringComparison.OrdinalIgnoreCase) ||
                       root.Name.Equals(MultiValueExtendedAttributesProperty, StringComparison.OrdinalIgnoreCase);
            if (!isEa) continue;

            touches = true;

            var filters = steps.OfType<FilterStep>().ToList();

            var name = filters.FirstOrDefault(f =>
                f.Key.Equals("attributeName", StringComparison.OrdinalIgnoreCase))?.Value;

            var id = filters.FirstOrDefault(f =>
                f.Key.Equals("eaId", StringComparison.OrdinalIgnoreCase) ||
                f.Key.Equals("extAttributeId", StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);

            if (!string.IsNullOrEmpty(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                ids.Add(id);

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id))
                walksAll = true;
        }

        ReferencedAttributeNames = names;
        ReferencedAttributeIds = ids;
        TouchesExtendedAttributes = touches;
        WalksAllExtendedAttributes = walksAll;
    }

    public static CompiledPath Compile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("Field path cannot be empty.");

        var raw = path.Trim();

        // 'label:' wraps an ordinary path. Stripped before branch splitting so the inner
        // path keeps the full grammar — 'label:ea:Region' and
        // 'label:a || b' both work.
        string? lookupKey = null;
        var isUser = false;

        if (raw.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var inner = raw.Substring(UserPrefix.Length).Trim();
            if (inner.Length == 0)
                throw new FormatException(
                    "The 'user:' shorthand requires a path, e.g. 'user:projectManager'.");

            // Wraps an ordinary path exactly as label: does, so the inner path keeps the
            // full grammar. There is no lookup key: users are fetched by ID, not by list.
            isUser = true;
            raw = inner;
        }
        else if (raw.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var inner = raw.Substring(LabelPrefix.Length).Trim();
            if (inner.Length == 0)
                throw new FormatException(
                    "The 'label:' shorthand requires a path, e.g. 'label:projectStatus' or " +
                    "'label:ea:Region'.");

            lookupKey = DeriveLookupKey(inner);
            raw = inner;
        }

        var parts = raw.Split(
            BranchSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            throw new FormatException($"Field path '{raw}' contains no usable branch.");

        var branches = new List<IReadOnlyList<PathStep>>(parts.Length);
        var expanded = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var expandedBranch = ExpandShorthand(part);
            var steps = Parse(expandedBranch);

            if (steps.Count == 0)
                throw new FormatException($"Branch '{part}' in field path '{raw}' produced no steps.");

            branches.Add(steps);
            expanded.Add(expandedBranch);
        }

        return new CompiledPath(raw, string.Join(" || ", expanded), branches, lookupKey, isUser);
    }

    private const string LabelPrefix = "label:";
    private const string UserPrefix = "user:";

    /// <summary>
    /// Names the list a <c>label:</c> path's codes belong to.
    ///
    /// <para>The metadata definition identifies a field either by its property name or by
    /// its attribute name, so the key is taken from the first branch with any other
    /// shorthand stripped. Only the first branch is used: a fallback chain whose branches
    /// came from different fields would have no single list to resolve against.</para>
    /// </summary>
    private static string DeriveLookupKey(string inner)
    {
        var first = inner.Split(BranchSeparator, StringSplitOptions.TrimEntries)[0];

        foreach (var prefix in new[] { "eamulti:", "eamulti#", "ea:", "ea#", "prop:" })
            if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                first = first.Substring(prefix.Length).Trim();
                break;
            }

        // A dotted or indexed path has no field in the definition to match, so the key is
        // the leading segment and resolution simply will not find it — reported as
        // unresolvable rather than silently ignored.
        var cut = first.IndexOfAny(new[] { '.', '[' });
        return (cut >= 0 ? first.Substring(0, cut) : first).Trim();
    }

    /// <summary>
    /// Escape hatch forcing a path to be read as an item property.
    ///
    /// <para>Needed because a bare name in a field list expands to "property, then
    /// extended attribute" — see <see cref="Configuration.FieldListLoader"/>. When a
    /// tenant has an extended attribute whose name collides with a real property,
    /// <c>prop:</c> pins the property and stops the fallback.</para>
    /// </summary>
    private const string PropertyPrefix = "prop:";

    private static string ExpandShorthand(string path)
    {
        if (path.StartsWith(PropertyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var property = path.Substring(PropertyPrefix.Length).Trim();
            if (property.Length == 0)
                throw new FormatException("The 'prop:' shorthand requires a property path, e.g. 'prop:title'.");
            return property;
        }

        // Longest prefix first: "eamulti:" would otherwise never be reached, since it
        // does not start with "ea:" but a careless ordering could still shadow it.
        if (TryExpand(path, "eamulti:", MultiValueExtendedAttributesProperty,
                "attributeName", "encodedListItemId", out var multiByName))
            return multiByName;

        if (TryExpand(path, "eamulti#", MultiValueExtendedAttributesProperty,
                "extAttributeId", "encodedListItemId", out var multiById))
            return multiById;

        if (TryExpand(path, "ea:", ExtendedAttributesProperty,
                "attributeName", "eaValue", out var byName))
            return byName;

        if (TryExpand(path, "ea#", ExtendedAttributesProperty,
                "eaId", "eaValue", out var byId))
            return byId;

        return path;
    }

    private static bool TryExpand(
        string path,
        string prefix,
        string arrayProperty,
        string filterKey,
        string valueProperty,
        out string expanded)
    {
        expanded = "";

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var spec = path.Substring(prefix.Length).Trim();
        if (spec.Length == 0)
            throw new FormatException(
                $"The '{prefix}' shorthand requires a value, e.g. '{prefix}" +
                (prefix.EndsWith('#') ? "4102'." : "Region'."));

        if (spec.Contains(']'))
            throw new FormatException(
                $"The '{prefix}' shorthand takes a plain name or ID, not a selector — got '{spec}'. " +
                $"Write the full path instead: {arrayProperty}[{filterKey}={spec}].{valueProperty}");

        if (prefix.EndsWith('#') &&
            !long.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new FormatException(
                $"The '{prefix}' shorthand expects a numeric attribute ID, got '{spec}'. " +
                $"For a name, use '{prefix.TrimEnd('#')}:{spec}'.");

        expanded = $"{arrayProperty}[{filterKey}={spec}].{valueProperty}";
        return true;
    }

    private static List<PathStep> Parse(string path)
    {
        var steps = new List<PathStep>();
        var i = 0;

        while (i < path.Length)
        {
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;

            if (i > start)
                steps.Add(new PropStep(path.Substring(start, i - start).Trim()));

            while (i < path.Length && path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                    throw new FormatException($"Unclosed '[' in field path '{path}'.");

                steps.Add(ParseSelector(path.Substring(i + 1, close - i - 1), path));
                i = close + 1;
            }

            if (i < path.Length && path[i] == '.') i++;
        }

        return steps;
    }

    private static PathStep ParseSelector(string inner, string path)
    {
        inner = inner.Trim();

        if (inner == "*")
            return new AllStep();

        if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0) throw new FormatException($"Negative array index in field path '{path}'.");
            return new IndexStep(index);
        }

        var eq = inner.IndexOf('=');
        if (eq <= 0)
            throw new FormatException(
                $"Invalid selector '[{inner}]' in field path '{path}'. " +
                "Expected an index, '*', or 'key=value'.");

        var key = inner.Substring(0, eq).Trim();
        var value = inner.Substring(eq + 1).Trim().Trim('\'', '"');
        return new FilterStep(key, value);
    }

    public override string ToString() => Expanded;
}

/// <summary>
/// Projects one item's JSON onto the configured CSV columns.
/// Not thread-safe: it reuses internal buffers to stay allocation-light across large
/// exports. Use one instance per writer.
/// </summary>
public sealed class FieldExtractor
{
    private readonly FieldMapping[] _mappings;
    private readonly string _multiValueSeparator;
    private readonly LookupResolver _lookups;
    private readonly UserResolver? _users;

    private readonly HashSet<string> _unresolvedCodes = new(StringComparer.Ordinal);

    private List<JsonElement> _current = new(8);
    private List<JsonElement> _scratch = new(8);
    private readonly StringBuilder _sb = new(256);

    private readonly HashSet<string> _separatorCollisions = new(StringComparer.Ordinal);
    private string _currentColumn = "";

    /// <summary>
    /// Columns where a multi-value cell contained a value that itself included
    /// <c>MultiValueSeparator</c>, making the joined cell ambiguous to split downstream.
    /// </summary>
    public IReadOnlyCollection<string> ColumnsWithSeparatorInValue => _separatorCollisions;

    public string[] Headers { get; }

    /// <summary>
    /// Columns where a <c>label:</c> path produced a code that is not in its list.
    /// Reported rather than hidden: a code outside its own list usually means the list
    /// was filtered to active items while the data still references a retired one.
    /// </summary>
    public IReadOnlyCollection<string> ColumnsWithUnresolvedCodes => _unresolvedCodes;

    /// <param name="users">
    /// Resolves <c>user:</c> columns. Null leaves them holding the numeric ID, so turning
    /// user resolution off never changes a file's column set.
    /// </param>
    public FieldExtractor(
        IEnumerable<FieldMapping> mappings,
        string multiValueSeparator,
        LookupResolver? lookups = null,
        UserResolver? users = null)
    {
        _mappings = mappings.ToArray();
        _multiValueSeparator = multiValueSeparator;
        _lookups = lookups ?? LookupResolver.Empty;
        _users = users;
        Headers = _mappings.Select(m => m.Column).ToArray();

        foreach (var m in _mappings)
        {
            try
            {
                m.Compiled ??= CompiledPath.Compile(m.Path);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Column '{m.Column}': {ex.Message}", ex);
            }
        }
    }

    public int ColumnCount => _mappings.Length;

    /// <summary>Writes one row's values into <paramref name="destination"/> (length must be <see cref="ColumnCount"/>).</summary>
    public void ExtractRow(JsonElement item, string[] destination)
    {
        if (destination.Length != _mappings.Length)
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match column count {_mappings.Length}.",
                nameof(destination));

        for (var i = 0; i < _mappings.Length; i++)
        {
            var mapping = _mappings[i];
            _currentColumn = mapping.Column;
            destination[i] = Evaluate(item, mapping.Compiled!) ?? mapping.Default;
        }
    }

    /// <summary>Returns the joined value, or null when every branch resolved to nothing.</summary>
    public string? Evaluate(JsonElement root, CompiledPath path)
    {
        foreach (var branch in path.Branches)
        {
            var value = EvaluateBranch(root, branch, path.LookupKey, path.IsUser);
            if (!string.IsNullOrEmpty(value)) return value;
        }

        return null;
    }

    /// <summary>
    /// Maps one code to its label, keeping the code when no label exists.
    ///
    /// <para>Keeping it matters: a blank cell would lose the only information the row
    /// had, and would be indistinguishable from a genuinely empty field.</para>
    /// </summary>
    private string Label(string? lookupKey, bool isUser, string value)
    {
        if (value.Length == 0) return value;

        if (isUser)
        {
            // No resolver means user resolution is switched off, not that it failed, so the
            // code stays and nothing is reported as unresolved.
            if (_users is null) return value;
            if (_users.TryGet(value, out var name)) return name;

            _unresolvedCodes.Add(_currentColumn);
            return value;
        }

        if (lookupKey is null) return value;
        if (_lookups.TryResolve(lookupKey, value, out var label)) return label;

        _unresolvedCodes.Add(_currentColumn);
        return value;
    }

    /// <summary>True when any column resolves user IDs, so pages need prefetching.</summary>
    public bool HasUserColumns => _mappings.Any(m => m.Compiled!.IsUser);

    /// <summary>
    /// The raw user IDs a page's items hold in <c>user:</c> columns, ready to prefetch.
    ///
    /// <para>Walks every fallback branch rather than only the first that resolves: that can
    /// fetch a user a column never ends up showing, but it cannot miss one, and a miss would
    /// leave a cell as a bare code for no reason anyone could see.</para>
    /// </summary>
    public IEnumerable<string> UserCodes(IEnumerable<JsonElement> items)
    {
        var columns = _mappings.Select(m => m.Compiled!).Where(c => c.IsUser).ToList();
        if (columns.Count == 0) yield break;

        foreach (var item in items)
            foreach (var column in columns)
                foreach (var branch in column.Branches)
                    foreach (var value in RawValues(item, branch))
                        yield return value;
    }

    /// <summary>Walks a branch and returns each value it reaches, as text, without joining or labelling.</summary>
    private static IEnumerable<string> RawValues(JsonElement root, IReadOnlyList<PathStep> steps)
    {
        // Fresh lists, not the shared buffers: this is a lazy enumeration, and the shared
        // ones belong to whichever ExtractRow call is running.
        var current = new List<JsonElement> { root };

        foreach (var step in steps)
        {
            var next = new List<JsonElement>();
            foreach (var element in current) Apply(step, element, next);

            current = next;
            if (current.Count == 0) yield break;
        }

        foreach (var element in current)
        {
            var text = Format(element);
            if (text.Length > 0) yield return text;
        }
    }

    private string? EvaluateBranch(
        JsonElement root,
        IReadOnlyList<PathStep> steps,
        string? lookupKey,
        bool isUser)
    {
        var current = _current;
        var scratch = _scratch;

        current.Clear();
        current.Add(root);

        foreach (var step in steps)
        {
            scratch.Clear();
            foreach (var element in current)
                Apply(step, element, scratch);

            (current, scratch) = (scratch, current);

            if (current.Count == 0) break;
        }

        // Keep the swapped buffers for the next call.
        _current = current;
        _scratch = scratch;

        if (current.Count == 0) return null;

        if (current.Count == 1)
            return Label(lookupKey, isUser, Format(current[0]));

        // Several values under one mapping: a multi-value extended attribute, or a
        // wildcard walk. Every value is preserved, joined by MultiValueSeparator.
        _sb.Clear();
        var wrote = false;
        var collision = false;

        foreach (var element in current)
        {
            // Resolved per element, before joining: a multi-value pick list holds several
            // codes, and each needs its own label.
            var text = Label(lookupKey, isUser, Format(element));
            if (string.IsNullOrEmpty(text)) continue;

            // A value containing the separator makes the joined cell ambiguous to split
            // downstream. Only checked when actually joining, so single values cost nothing.
            if (text.Contains(_multiValueSeparator, StringComparison.Ordinal)) collision = true;

            if (wrote) _sb.Append(_multiValueSeparator);
            _sb.Append(text);
            wrote = true;
        }

        if (collision) _separatorCollisions.Add(_currentColumn);

        return wrote ? _sb.ToString() : null;
    }

    private static void Apply(PathStep step, JsonElement element, List<JsonElement> output)
    {
        switch (step)
        {
            case PropStep prop:
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty(prop.Name, out var child) &&
                    child.ValueKind != JsonValueKind.Null &&
                    child.ValueKind != JsonValueKind.Undefined)
                    output.Add(child);
                break;

            case IndexStep idx:
                if (element.ValueKind == JsonValueKind.Array &&
                    idx.Index < element.GetArrayLength())
                {
                    var item = element[idx.Index];
                    if (item.ValueKind != JsonValueKind.Null) output.Add(item);
                }
                break;

            case AllStep:
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.Null) output.Add(item);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var member in element.EnumerateObject())
                        if (member.Value.ValueKind != JsonValueKind.Null) output.Add(member.Value);
                }
                break;

            case FilterStep filter:
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                        if (Matches(item, filter)) output.Add(item);
                }
                else if (element.ValueKind == JsonValueKind.Object && Matches(element, filter))
                {
                    output.Add(element);
                }
                break;
        }
    }

    private static bool Matches(JsonElement candidate, FilterStep filter)
    {
        if (candidate.ValueKind != JsonValueKind.Object) return false;
        if (!candidate.TryGetProperty(filter.Key, out var value)) return false;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

        return text is not null && text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        // Objects/arrays at a leaf are emitted as raw JSON; the CSV writer quotes them.
        _ => element.GetRawText()
    };

    /// <summary>True when at least one column reads tenant-defined metadata.</summary>
    public bool RequiresExtendedAttributes => _mappings.Any(m => m.Compiled!.TouchesExtendedAttributes);

    /// <summary>Distinct extended-attribute names referenced by the column list.</summary>
    public IReadOnlyList<string> ReferencedAttributeNames =>
        _mappings.SelectMany(m => m.Compiled!.ReferencedAttributeNames)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    /// <summary>Mappings in column order. Used by the --sample diagnostic.</summary>
    public IReadOnlyList<FieldMapping> Mappings => _mappings;

    /// <summary>
    /// Lookup keys this column list needs resolved. Computed from the mappings alone, so
    /// the resolver fetches only the lists actually referenced rather than every list the
    /// object declares.
    /// </summary>
    public static IReadOnlyCollection<string> RequiredLookupKeys(IEnumerable<FieldMapping> mappings)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in mappings)
        {
            var compiled = mapping.Compiled ?? CompiledPath.Compile(mapping.Path);
            mapping.Compiled = compiled;

            if (compiled.LookupKey is { Length: > 0 } key) keys.Add(key);
        }

        return keys;
    }
}
