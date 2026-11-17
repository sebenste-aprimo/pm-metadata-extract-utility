using System.Text.Json;
using AprimoPmExport.Configuration;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>
/// Reports how an object's coded values could be turned into labels.
///
/// <para>Most numeric columns in a PM export are codes, not values — <c>projectStatus 4</c>,
/// <c>timeZoneId 43</c>, a pick-list value of <c>663</c>. The API does expose the lists behind them,
/// through four different endpoints depending on how the field is configured, and
/// <c>GET /metadata/{resourceName}</c> says which applies to each field. That is what
/// makes resolution survive fields being added later: nothing here is hard-coded per
/// tenant.</para>
///
/// <para>This exists as a separate diagnostic because <b>three of those four endpoints
/// declare no response schema at all</b> — their <c>200</c> content is empty in the spec,
/// and there are no examples anywhere in the document. Rather than guess at the shapes
/// and ship a resolver that silently produces blank columns, this probes each one and
/// prints what came back, so the resolver can be written against observed data.</para>
/// </summary>
public sealed class LookupProbe
{
    private readonly ApiClient _client;
    private readonly MetadataCatalog _metadata;
    private readonly Action<string> _log;

    public LookupProbe(ApiClient client, MetadataCatalog metadata, Action<string> log)
    {
        _client = client;
        _metadata = metadata;
        _log = log;
    }

    public async Task<bool> RunAsync(
        IReadOnlyList<PmObject> objects,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("════════ Lookup probe ════════");
        Console.WriteLine("Read-only. Reports which coded fields exist and what each resolution");
        Console.WriteLine("endpoint actually returns, so value resolution can be built on observation");
        Console.WriteLine("rather than on a spec that declares no schema for three of the four.");

        var anything = false;

        await ProbeSystemTypeIndexAsync(cancellationToken).ConfigureAwait(false);

        foreach (var pmObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine();
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"{pmObject.Name}   {pmObject.Path}");

            var definition = await _metadata.GetAsync(pmObject, cancellationToken).ConfigureAwait(false);

            if (definition is null)
            {
                Console.WriteLine("  No metadata definition on this tenant — nothing to inspect.");
                continue;
            }

            var coded = definition.Fields.Where(f => f.HasLookup).ToList();

            Console.WriteLine($"  {definition.Fields.Count} field(s) defined, " +
                              $"{coded.Count} declaring a lookup of some kind.");

            if (coded.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  None of the fields declares selectionListName, systemTypeName or");
                Console.WriteLine("  lookupListId. Either this tenant resolves codes somewhere the REST");
                Console.WriteLine("  surface does not expose, or the definition omits that detail. The");
                Console.WriteLine("  extended-attribute pick lists below are then the only route available.");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"  {"FIELD",-34} {"PROPERTY",-24} VIA");

                foreach (var field in coded)
                {
                    var via = field.SelectionListName is { Length: > 0 } list
                        ? $"selection list '{list}'"
                        : field.SystemTypeName is { Length: > 0 } type
                            ? $"system type '{type}'"
                            : $"lookup list {field.LookupListId}";

                    Console.WriteLine($"  {Trim(field.FieldName, 34),-34} " +
                                      $"{Trim(field.PropertyName ?? "(attribute)", 24),-24} {via}");
                }

                anything = true;
            }

            await ProbeFieldListsAsync(coded, cancellationToken).ConfigureAwait(false);
            await ProbePickListsAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 72));
        Console.WriteLine("If the samples above show real labels, value resolution can be wired in.");
        Console.WriteLine("Paste this output and the resolver will be built to the shapes it reports.");

        return anything;
    }

    /// <summary>Properties of a user record that are safe to print: who it is, not how to reach them.</summary>
    private static readonly string[] SafeUserFields = { "userId", "firstName", "lastName", "activeFlag" };

    /// <summary>
    /// Candidate properties whose lookup list is the user list, best first.
    /// <c>modifiedUser</c> is deliberately absent: on the observed tenant it names lookup
    /// list 58, which is not served, so it cannot be used to learn anything.
    /// </summary>
    private static readonly string[] UserListProperties = { "projectManager", "ownerId", "administratorId" };

    /// <summary>
    /// Splits a comma/semicolon/space separated list of user IDs. IDs go into a URL path,
    /// so anything that is not all digits is rejected rather than escaped and sent.
    /// </summary>
    internal static (List<string> Valid, List<string> Rejected) ParseIds(string text)
    {
        var valid = new List<string>();
        var rejected = new List<string>();

        foreach (var token in text.Split(new[] { ',', ';', ' ' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.All(char.IsAsciiDigit))
            {
                if (!valid.Contains(token)) valid.Add(token);
            }
            else
            {
                rejected.Add(token);
            }
        }

        return (valid, rejected);
    }

    /// <summary>
    /// Describes a user record without disclosing personal data.
    ///
    /// <para><c>GET /users/{id}</c> is typed in the spec as <c>UserGroup</c>, which is a
    /// full user record: email, phone, mobile, fax, postal address, employee number, cost
    /// centre, login ID, last login. Printing it the way the other probes print items
    /// would put all of that in a terminal, and from there in whatever the output gets
    /// pasted into. So only a name and an ID are shown; every other populated property is
    /// listed by name alone, which is all that is needed to see the record's shape.</para>
    /// </summary>
    internal static (string Safe, List<string> Withheld) SummarizeUser(JsonElement user)
    {
        var withheld = new List<string>();

        if (user.ValueKind != JsonValueKind.Object) return ("(not an object)", withheld);

        var safe = new List<string>();

        foreach (var property in user.EnumerateObject())
        {
            if (property.Name.StartsWith('_')) continue;

            var populated = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(property.Value.GetString()),
                JsonValueKind.Array => property.Value.GetArrayLength() > 0,
                _ => true
            };

            if (!populated) continue;

            if (SafeUserFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                safe.Add($"{property.Name}={Trim(Text(property.Value), 40)}");
            else
                withheld.Add(property.Name);
        }

        return (string.Join("  ", safe), withheld);
    }

    /// <summary>One user's outcome, for the tally.</summary>
    internal readonly record struct UserOutcome(bool InList, bool? Active);

    /// <summary>
    /// Cross-tabulates users by <i>in the lookup list</i> against <i>active</i>, and says
    /// what that does and does not establish.
    ///
    /// <para>Two explanations fit a user missing from the list: the list is capped
    /// (600 is a round number), or it holds active users only. They predict different
    /// things. A cap leaves <b>active</b> users out; an active-only list leaves out
    /// <b>only</b> inactive ones. So the question is whether any active user is missing —
    /// which two hand-picked IDs cannot answer, and which an earlier version of this
    /// probe got wrong by treating "not in the list" plus "a surname search found
    /// nothing" as evidence about <c>filterText</c>. A search of a list the user is not
    /// in finds nothing whether or not the search works.</para>
    /// </summary>
    internal static List<string> Conclude(IReadOnlyCollection<UserOutcome> outcomes)
    {
        var lines = new List<string>();

        var inActive = outcomes.Count(o => o is { InList: true, Active: true });
        var inInactive = outcomes.Count(o => o is { InList: true, Active: false });
        var outActive = outcomes.Count(o => o is { InList: false, Active: true });
        var outInactive = outcomes.Count(o => o is { InList: false, Active: false });
        var unknown = outcomes.Count(o => o.Active is null);

        lines.Add($"{"",-20}{"active",10}{"inactive",10}");
        lines.Add($"{"in the list",-20}{inActive,10}{inInactive,10}");
        lines.Add($"{"not in the list",-20}{outActive,10}{outInactive,10}");

        if (unknown > 0)
            lines.Add($"({unknown} user(s) had no activeFlag and are left out of the table.)");

        lines.Add("");

        var notInList = outActive + outInactive;

        if (notInList == 0)
        {
            lines.Add("=> No user outside the list was tested, so nothing can be concluded about why " +
                      "managers fail to resolve. Pass IDs that did not resolve.");
            return lines;
        }

        if (outActive > 0)
        {
            lines.Add($"=> {outActive} ACTIVE user(s) are missing from the list. Being inactive does not " +
                      "explain that, so the list is limited by something else: a cap, or what the calling " +
                      "user is allowed to see.");
            return lines;
        }

        if (inInactive > 0)
        {
            lines.Add($"=> {inInactive} inactive user(s) DO appear in the list, so being inactive is not " +
                      "what keeps the others out.");
            return lines;
        }

        lines.Add($"=> Every one of the {outInactive} user(s) missing from the list is inactive, and none of " +
                  $"the {inActive} listed users is. That fits a list of active users only, and needs no cap " +
                  "to explain it.");
        lines.Add("   This is a sample. It is consistent with active-only, not proof of it: one active " +
                  "user missing from the list would overturn it.");

        return lines;
    }

    /// <summary>
    /// Works out why the user lookup leaves most managers as bare codes, and whether
    /// <c>GET /users/{id}</c> can resolve them.
    ///
    /// <para>The lookup behind <c>projectManager</c> returned exactly 600 users. Two
    /// explanations fit — a server-side cap, or a list of active users only — and this
    /// separates them by testing users in <i>both</i> positions: the IDs you pass (meant
    /// to be ones that failed to resolve) and a spread sampled from the list itself. Each
    /// is fetched with <c>/users/{id}</c> to read its <c>activeFlag</c>, then tallied
    /// against list membership.</para>
    ///
    /// <para>The list sample doubles as a positive control for <c>filterText</c>: a
    /// surname search can only be judged against users who are known to be in the list.</para>
    /// </summary>
    public async Task<bool> RunUsersAsync(
        IReadOnlyList<PmObject> objects,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("════════ User probe ════════");
        Console.WriteLine("Read-only. Tests why the user lookup list leaves managers as numbers, and");
        Console.WriteLine("whether GET /users/{id} can resolve them.");
        Console.WriteLine("Personal data (email, phone, address, ...) is never printed - only names.");

        var (listId, listSource) = await FindUserListAsync(objects, cancellationToken).ConfigureAwait(false);

        // Who this registration runs as. Worth seeing because PM applies per-user access:
        // the lookup a caller gets may depend on who the caller is, which would make the
        // list differ between registrations as well as between days.
        Console.WriteLine();
        Console.WriteLine("── GET /users/me ──");
        await PrintUserAsync("/users/me", "the authenticated user", cancellationToken).ConfigureAwait(false);

        ListEntries? listed = null;

        if (listId is { } id0)
        {
            Console.WriteLine();
            Console.WriteLine($"── GET /lookup/{id0} (the user list behind {listSource}) ──");
            listed = await ReadListAsync($"/lookup/{id0}", cancellationToken).ConfigureAwait(false);

            if (listed is null)
            {
                Console.WriteLine("  not available - the list cross-check below is skipped.");
            }
            else
            {
                Console.WriteLine($"  {listed.Labels.Count} user(s) in the unfiltered list.");
                Console.WriteLine(listed.Labels.Count % 100 == 0
                    ? "  A round count: a hint of a limit, not proof of one."
                    : "  Not a round count, so a hard cap looks less likely.");
                Console.WriteLine($"  {listed.Inactive} of them are flagged active=false by the list itself" +
                                  (listed.Inactive == 0 ? " - it may hold active users only." : "."));
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("No selected object has a projectManager / ownerId / administratorId field with");
            Console.WriteLine("a lookup list, so the list cross-check is skipped. Try --objects projects.");
        }

        // The IDs given, then a spread taken from the list itself. The spread is what
        // makes the tally meaningful: without users known to be in the list there is
        // nothing to compare the missing ones against.
        var targets = ids.Select(id => (Id: id, FromList: false)).ToList();

        if (listed is { Labels.Count: > 0 })
        {
            var supplied = new HashSet<string>(ids);
            var pool = listed.Labels.Keys.Where(k => !supplied.Contains(k) && k.All(char.IsAsciiDigit)).ToList();

            foreach (var id in Spread(pool, ListSampleSize))
                targets.Add((id, true));
        }

        // Compact lines once there are many; the full block per user is too long to scan.
        var compact = targets.Count > 6;

        if (compact)
        {
            Console.WriteLine();
            Console.WriteLine($"── GET /users/{{id}} for {targets.Count} user(s) " +
                              $"({ids.Count} given, {targets.Count - ids.Count} sampled from the list) ──");
            Console.WriteLine($"  {"ID",-8} {"NAME",-28} {"STATE",-9} IN LIST   SOURCE");
        }

        var outcomes = new List<UserOutcome>();
        var fetched = 0;
        var filterTested = 0;
        var filterWorked = 0;
        var rescued = new List<string>();

        foreach (var (userId, fromList) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!compact)
            {
                Console.WriteLine();
                Console.WriteLine($"── GET /users/{userId} ──");
            }

            using var document = await _client
                .TryGetJsonAsync($"/users/{userId}", $"user {userId}", cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {
                Console.WriteLine(compact ? $"  {userId,-8} not available (404/403)" : "  not available (404/403).");
                continue;
            }

            fetched++;
            var root = document.RootElement;

            var active = ReadActive(root);
            var inList = listed?.Labels.ContainsKey(userId) ?? false;
            var name = DisplayName(root);

            if (listed is not null) outcomes.Add(new UserOutcome(inList, active));

            var state = active switch { true => "active", false => "inactive", _ => "?" };

            if (compact)
            {
                Console.WriteLine($"  {userId,-8} {Trim(name, 28),-28} {state,-9} " +
                                  $"{(listed is null ? "n/a" : inList ? "yes" : "NO"),-8}  " +
                                  (fromList ? "from list" : "given"));
            }
            else
            {
                PrintUser(root);
                if (listed is not null)
                    Console.WriteLine($"  in the unfiltered /lookup/{listId}: " +
                                      (inList ? "yes -> " + listed.Labels[userId] : "NO"));
            }

            if (listed is null || listId is null) continue;

            var surname = root.ValueKind == JsonValueKind.Object &&
                          root.TryGetProperty("lastName", out var last) &&
                          last.ValueKind == JsonValueKind.String
                ? last.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(surname)) continue;

            // Positive control: a user known to be in the list, searched for by surname.
            // This is the only way to learn whether filterText works as a name filter.
            if (inList && filterTested < 3)
            {
                var found = await ReadListAsync(
                    $"/lookup/{listId}?filterText={Uri.EscapeDataString(surname!)}", cancellationToken)
                    .ConfigureAwait(false);

                filterTested++;
                var hit = found is not null && found.Labels.ContainsKey(userId);
                if (hit) filterWorked++;

                Console.WriteLine($"    filterText='{surname}' -> {(found?.Labels.Count.ToString() ?? "n/a")} " +
                                  $"item(s); {(hit ? "includes this user" : "does NOT include this user")}");
            }

            // An ACTIVE user missing from the list is the interesting case: if a search
            // finds them, filterText reaches past whatever limits the unfiltered list.
            // An inactive one is skipped - its absence is uninformative either way.
            if (!inList && active == true)
            {
                var found = await ReadListAsync(
                    $"/lookup/{listId}?filterText={Uri.EscapeDataString(surname!)}", cancellationToken)
                    .ConfigureAwait(false);

                var hit = found is not null && found.Labels.ContainsKey(userId);
                if (hit) rescued.Add(userId);

                Console.WriteLine($"    ACTIVE and not in the list; filterText='{surname}' -> " +
                                  (hit ? "FOUND by search" : "still not found"));
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"{fetched} of {targets.Count} user(s) fetched via /users/{{id}}.");

        if (listed is null) return fetched > 0;

        Console.WriteLine();
        foreach (var line in Conclude(outcomes)) Console.WriteLine(line);

        Console.WriteLine();
        if (filterTested == 0)
            Console.WriteLine("filterText: not tested - no listed user had a surname to search for.");
        else
            Console.WriteLine($"filterText as a surname filter: found the user in {filterWorked} of " +
                              $"{filterTested} control search(es)" +
                              (filterWorked == filterTested ? " - it works." :
                               filterWorked == 0 ? " - it does not behave as a surname filter." : " - mixed."));

        if (rescued.Count > 0)
            Console.WriteLine($"filterText surfaced {rescued.Count} active user(s) the unfiltered list left out: " +
                              string.Join(", ", rescued));

        return fetched > 0;
    }

    /// <summary>How many users to sample from the list as the "in the list" side of the tally.</summary>
    private const int ListSampleSize = 12;

    /// <summary>
    /// Takes <paramref name="count"/> items spread evenly across a list, so the sample is
    /// not just the first few entries (which here are alphabetically the earliest names).
    /// </summary>
    internal static IEnumerable<string> Spread(IReadOnlyList<string> pool, int count)
    {
        if (pool.Count == 0 || count <= 0) yield break;
        if (pool.Count <= count)
        {
            foreach (var item in pool) yield return item;
            yield break;
        }

        for (var i = 0; i < count; i++)
            yield return pool[(int)((long)i * pool.Count / count)];
    }

    /// <summary><c>activeFlag</c> as a bool, or null when absent or not a number.</summary>
    internal static bool? ReadActive(JsonElement user) =>
        user.ValueKind == JsonValueKind.Object &&
        user.TryGetProperty("activeFlag", out var flag) &&
        flag.ValueKind == JsonValueKind.Number &&
        flag.TryGetInt32(out var value)
            ? value != 0
            : null;

    /// <summary>First and last name only. Nothing that could be used to contact the person.</summary>
    internal static string DisplayName(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object) return "(unknown)";

        string Read(string name) =>
            user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        var full = $"{Read("firstName")} {Read("lastName")}".Trim();
        return full.Length == 0 ? "(no name)" : full;
    }

    /// <summary>Finds the lookup list that the object's user fields point at.</summary>
    private async Task<(int? ListId, string Source)> FindUserListAsync(
        IReadOnlyList<PmObject> objects,
        CancellationToken cancellationToken)
    {
        foreach (var pmObject in objects)
        {
            var definition = await _metadata.GetAsync(pmObject, cancellationToken).ConfigureAwait(false);
            if (definition is null) continue;

            foreach (var property in UserListProperties)
            {
                var field = definition.Fields.FirstOrDefault(f =>
                    string.Equals(f.PropertyName, property, StringComparison.OrdinalIgnoreCase) &&
                    f.LookupListId is > 0);

                if (field is not null)
                    return (field.LookupListId, $"{pmObject.Name}.{property}");
            }
        }

        return (null, "");
    }

    private async Task PrintUserAsync(string path, string description, CancellationToken cancellationToken)
    {
        using var document = await _client
            .TryGetJsonAsync(path, description, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            Console.WriteLine("  not available (404/403).");
            return;
        }

        PrintUser(document.RootElement);
    }

    private static void PrintUser(JsonElement root)
    {
        var (safe, withheld) = SummarizeUser(root);

        Console.WriteLine("  " + (safe.Length == 0 ? "(none of the safe fields are populated)" : safe));

        if (withheld.Count == 0) return;

        Console.WriteLine($"  {withheld.Count} other populated propert{(withheld.Count == 1 ? "y" : "ies")}, " +
                          "values withheld (personal data):");
        Console.WriteLine("    " + string.Join(", ", withheld.Take(25)) +
                          (withheld.Count > 25 ? $", ... and {withheld.Count - 25} more" : ""));
    }

    /// <summary>A lookup list as read: code to label, plus how many entries the list itself flags inactive.</summary>
    private sealed record ListEntries(Dictionary<string, string> Labels, int Inactive);

    /// <summary>Reads a lookup list, or null when it is not served.</summary>
    private async Task<ListEntries?> ReadListAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await _client
            .TryGetJsonAsync(path, $"lookup {path}", cancellationToken)
            .ConfigureAwait(false);

        if (document is null) return null;

        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : TryItemsProperty(root) ?? HalItems(root);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var inactive = 0;

        if (items is not { ValueKind: JsonValueKind.Array } array) return new ListEntries(map, 0);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var key = ReadScalar(item, "key") ?? ReadScalar(item, "itemId") ?? ReadScalar(item, "id");
            var label = ReadScalar(item, "value") ?? ReadScalar(item, "displayValue") ?? "";

            if (key is null) continue;
            map[key] = label;

            if (item.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
                inactive++;
        }

        return new ListEntries(map, inactive);
    }

    private static string? ReadScalar(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>
    /// Lists the tenant's system types. This one endpoint <i>does</i> declare a schema
    /// (<c>SystemType</c>), so it is the cheapest confirmation that the whole family
    /// behaves as expected before trusting the undocumented ones.
    /// </summary>
    private async Task ProbeSystemTypeIndexAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("── GET /system-types ──");

        using var document = await _client
            .TryGetJsonAsync("/system-types", "system types", cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            Console.WriteLine("  not available on this tenant.");
            return;
        }

        var envelope = HalEnvelopeReader.Read(document.RootElement, null);

        if (!envelope.HasItems)
        {
            Console.WriteLine($"  returned no items (status {envelope.Status}, " +
                              $"keys: {HalEnvelopeReader.DescribeKeys(document.RootElement)}).");
            return;
        }

        Console.WriteLine($"  {envelope.Count} system type(s), _embedded key '{envelope.EmbedKey}'" +
                          (envelope.Total is { } total ? $", _total {total:N0}" : "") + ".");

        foreach (var item in envelope.Items.EnumerateArray().Take(15))
            Console.WriteLine("    " + Summarize(item));

        if (envelope.Count > 15)
            Console.WriteLine($"    … and {envelope.Count - 15} more");
    }

    /// <summary>Tries each distinct list the object's fields point at.</summary>
    private async Task ProbeFieldListsAsync(
        IReadOnlyList<MetadataField> coded,
        CancellationToken cancellationToken)
    {
        // Every distinct list, not a sample of them. An earlier Take(5) here hid the
        // fact that several fields share one lookup list and it was never tried at all.
        // The cap is a runaway guard, not a sampling decision.
        const int cap = 40;

        foreach (var name in coded.Select(f => f.SelectionListName)
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .Take(cap))
        {
            await ProbeAsync($"/metadata/selection-lists/{Uri.EscapeDataString(name!)}",
                $"selection list '{name}'", cancellationToken).ConfigureAwait(false);
        }

        foreach (var type in coded.Select(f => f.SystemTypeName)
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .Take(cap))
        {
            await ProbeAsync($"/system-types/{Uri.EscapeDataString(type!)}?offset=0&limit=10",
                $"system type '{type}'", cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in coded.Select(f => f.LookupListId)
                                .Where(i => i is > 0)
                                .Distinct()
                                .Take(cap))
        {
            await ProbeAsync($"/lookup/{id}", $"lookup list {id}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tries the pick-list endpoint for the object's extended attributes — the route for
    /// the <c>extrAttr*</c> codes, which the field definitions may not describe.
    /// </summary>
    private async Task ProbePickListsAsync(
        ResourceMetadata definition,
        CancellationToken cancellationToken)
    {
        var attributes = definition.Fields
            .Where(f => !f.IsSystemField && f.AttributeId is > 0)
            .Select(f => f.AttributeId!.Value)
            .Distinct()
            .Take(40)
            .ToList();

        foreach (var id in attributes)
            await ProbeAsync($"/extended-attributes/{id}/pick-list-options",
                $"pick-list options for attribute {id}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GETs one endpoint and prints enough of the result to design against.</summary>
    private async Task ProbeAsync(string path, string description, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"── GET {path} ──");

        using var document = await _client
            .TryGetJsonAsync(path, description, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            Console.WriteLine("  not available (404/403) — this route does not serve that list here.");
            return;
        }

        var root = document.RootElement;

        // Three plausible shapes, none of them documented: a bare array, the HAL
        // envelope used everywhere else, or a SelectionList-style { items: [...] }.
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : TryItemsProperty(root) ?? HalItems(root);

        if (items is not { ValueKind: JsonValueKind.Array } array)
        {
            Console.WriteLine($"  no array found. Root is {root.ValueKind}, " +
                              $"keys: {HalEnvelopeReader.DescribeKeys(root)}.");
            Console.WriteLine("  Raw (first 300 chars): " + Trim(root.GetRawText(), 300));
            return;
        }

        var count = array.GetArrayLength();
        Console.WriteLine($"  {count} item(s).");

        foreach (var item in array.EnumerateArray().Take(10))
            Console.WriteLine("    " + Summarize(item));

        if (count > 10) Console.WriteLine($"    … and {count - 10} more");
    }

    private static JsonElement? TryItemsProperty(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("items", out var items) &&
        items.ValueKind == JsonValueKind.Array
            ? items
            : null;

    private static JsonElement? HalItems(JsonElement root)
    {
        var envelope = HalEnvelopeReader.Read(root, null);
        return envelope.HasItems ? envelope.Items : null;
    }

    /// <summary>
    /// Prints an item's scalar properties. Deliberately prints the whole set rather than
    /// a guessed id/label pair — which key holds the label is exactly what is unknown.
    /// </summary>
    private static string Summarize(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return Trim(item.GetRawText(), 120);

        var parts = item.EnumerateObject()
            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                          or JsonValueKind.True or JsonValueKind.False)
            .Take(8)
            .Select(p => $"{p.Name}={Trim(Text(p.Value), 40)}");

        return string.Join("  ", parts);
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    private static string Trim(string value, int max)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single.Substring(0, max - 1) + "…";
    }
}
