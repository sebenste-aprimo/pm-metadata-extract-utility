using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AprimoPmExport.Auth;
using AprimoPmExport.Configuration;
using AprimoPmExport.Export;
using AprimoPmExport.Http;

namespace AprimoPmExport.Tests;

/// <summary>
/// Self-contained offline test suite. No test framework, no network, no tenant.
/// Exit code 0 means everything passed.
///
/// <para>Fixtures are synthetic and tenant-agnostic. The only assertions made about the
/// shipped <c>appsettings.json</c>, <c>objects.json</c> and <c>fields/</c> are
/// structural, so editing those for a real tenant will not break the suite.</para>
/// </summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> _failures = new();
    private static string _section = "";

    public static int Main()
    {
        var stopwatch = Stopwatch.StartNew();
        var scratch = Path.Combine(Path.GetTempPath(), "aprimo-pm-export-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            PathGrammar();
            Extraction();
            FieldLists();
            ObjectRegistry(scratch);
            MetadataFieldClassification();
            LabelResolution();
            UserProbe();
            UserResolution().GetAwaiter().GetResult();
            DepthProbeAnalysis();
            PagingAgainstSimulatedServer().GetAwaiter().GetResult();
            SpecConsistency();
            PipelineForEveryObject(scratch).GetAwaiter().GetResult();
            HalEnvelopes();
            CsvOutput(scratch);
            RateLimiterTests().GetAwaiter().GetResult();
            CredentialStoreTests(scratch);
            DeltaWindows();
            DeltaFiltering();
            DeltaState(scratch);
            CommandLineGuards();
            ConfigValidation();
            LocalConfiguration(scratch);
            Manifest(scratch);
            ShippedFiles();
        }
        catch (Exception ex)
        {
            _failures.Add($"[{_section}] the suite itself threw: {ex}");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 70));

        if (_failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} checks passed in {stopwatch.ElapsedMilliseconds} ms.");
            return 0;
        }

        Console.WriteLine($"{_passed} passed, {_failures.Count} FAILED in {stopwatch.ElapsedMilliseconds} ms.");
        Console.WriteLine();
        foreach (var failure in _failures) Console.WriteLine("  FAIL  " + failure);
        return 1;
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static void Section(string title)
    {
        _section = title;
        Console.WriteLine();
        Console.WriteLine("── " + title);
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; return; }
        _failures.Add($"[{_section}] {name}" + (detail is null ? "" : $" — {detail}"));
    }

    private static void Eq(string name, string? expected, string? actual) =>
        Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
            $"expected \"{expected}\", got \"{actual}\"");

    private static void Eq(string name, long expected, long actual) =>
        Check(name, expected == actual, $"expected {expected}, got {actual}");

    private static void Eq(string name, double expected, double actual) =>
        Check(name, Math.Abs(expected - actual) < 1e-9, $"expected {expected}, got {actual}");

    private static void Eq(string name, bool expected, bool actual) =>
        Check(name, expected == actual, $"expected {expected}, got {actual}");

    private static void Throws<TException>(string name, Action action, string? messageContains = null)
        where TException : Exception
    {
        try
        {
            action();
            Check(name, false, $"expected {typeof(TException).Name}, nothing was thrown");
        }
        catch (TException ex)
        {
            if (messageContains is null)
            {
                _passed++;
                return;
            }

            Check(name, ex.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase),
                $"message did not mention \"{messageContains}\": {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(name, false, $"expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Noop(string _) { }

    /// <summary>Shortens a message so it reads as a check name.</summary>
    private static string Truncate(string value, int max = 48)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single.Substring(0, max - 1) + "…";
    }

    private static JsonDocument Json(string text) => JsonDocument.Parse(text);

    // ───────────────────────────── fixtures ─────────────────────────────

    /// <summary>
    /// A PM item shaped like the Project schema: scalar properties plus the two
    /// extended-attribute arrays. One attribute deliberately has a null
    /// <c>attributeName</c>, because the spec marks that property readOnly without
    /// committing to populating it — and an ID-only attribute is the case that breaks
    /// name-based mappings.
    /// </summary>
    private const string ProjectItem = """
    {
      "projectId": 4471,
      "activityId": 991,
      "title": "Spring Launch",
      "description": "",
      "projectStatus": 2,
      "modifiedDate": "2026-08-05T11:30:00Z",
      "modifiedUser": 77,
      "enableAlap": 0,
      "changeOrder": false,
      "projectedEndDate": null,
      "outlook": { "healthIndicator": "Green", "score": 88 },
      "changeOrderCategories": [ { "id": 1, "name": "Scope" }, { "id": 2, "name": "Budget" } ],
      "extendedAttributes": [
        { "eaId": 4102, "attributeName": "Business Unit", "eaValue": "Retail" },
        { "eaId": 4103, "attributeName": "Photo#1", "eaValue": "hero.jpg" },
        { "eaId": 4104, "attributeName": null, "eaValue": "unnamed-value" },
        { "eaId": 4105, "attributeName": "Budget Code", "eaValue": 91055 },
        { "eaId": 4106, "attributeName": "Dirty", "eaValue": "alpha|beta" }
      ],
      "multipleValueExtendedAttributes": [
        { "parentId": 4471, "extAttributeId": 4110, "encodedListItemId": 51, "attributeName": "Channels" },
        { "parentId": 4471, "extAttributeId": 4110, "encodedListItemId": 52, "attributeName": "Channels" },
        { "parentId": 4471, "extAttributeId": 4111, "encodedListItemId": 60, "attributeName": null }
      ]
    }
    """;

    private static PmObject ProjectsObject() => new()
    {
        Name = "projects",
        Path = "/projects",
        IdProperty = "projectId",
        ModifiedProperty = "modifiedDate",
        TitleProperty = "title",
        MetadataResource = "project",
        DetailPath = "/projects/{id}",
        SupportsExtendedAttributes = true
    };

    private static string Extract(JsonElement item, string path)
    {
        var extractor = new FieldExtractor(
            new[] { new FieldMapping { Column = "c", Path = path } }, "|");
        var row = new string[1];
        extractor.ExtractRow(item, row);
        return row[0];
    }

    // ───────────────────────────── A. path grammar ─────────────────────────────

    private static void PathGrammar()
    {
        Section("Path grammar");

        Eq("ea: expands to an attributeName filter",
            "extendedAttributes[attributeName=Business Unit].eaValue",
            CompiledPath.Compile("ea:Business Unit").Expanded);

        Eq("ea# expands to an eaId filter",
            "extendedAttributes[eaId=4102].eaValue",
            CompiledPath.Compile("ea#4102").Expanded);

        Eq("eamulti: expands to the multi-value array",
            "multipleValueExtendedAttributes[attributeName=Channels].encodedListItemId",
            CompiledPath.Compile("eamulti:Channels").Expanded);

        Eq("eamulti# uses extAttributeId, not eaId",
            "multipleValueExtendedAttributes[extAttributeId=4110].encodedListItemId",
            CompiledPath.Compile("eamulti#4110").Expanded);

        // 'eamulti:' does not start with 'ea:', but an implementation that matched on
        // 'ea' alone would shadow it. Worth pinning.
        Check("eamulti is not shadowed by the ea prefix",
            CompiledPath.Compile("eamulti:X").Expanded.StartsWith("multipleValueExtendedAttributes",
                StringComparison.Ordinal));

        Eq("prop: strips to the bare property", "title", CompiledPath.Compile("prop:title").Expanded);
        Eq("prop: keeps a dotted path", "outlook.score", CompiledPath.Compile("prop:outlook.score").Expanded);

        Eq("a plain path is unchanged", "outlook.healthIndicator",
            CompiledPath.Compile("outlook.healthIndicator").Expanded);

        Eq("fallback branches are counted", 3, CompiledPath.Compile("a || b || c").BranchCount);
        Eq("a single path is one branch", 1, CompiledPath.Compile("a.b.c").BranchCount);

        Check("an ea path is recognised as touching attributes",
            CompiledPath.Compile("ea:Region").TouchesExtendedAttributes);
        Check("a plain property does not touch attributes",
            !CompiledPath.Compile("title").TouchesExtendedAttributes);
        Check("a wildcard attribute walk is flagged",
            CompiledPath.Compile("extendedAttributes[*].attributeName").WalksAllExtendedAttributes);
        Check("a named attribute is not a wildcard walk",
            !CompiledPath.Compile("ea:Region").WalksAllExtendedAttributes);

        Eq("referenced attribute names are collected", "Region",
            string.Join(",", CompiledPath.Compile("ea:Region").ReferencedAttributeNames));
        Eq("referenced attribute IDs are collected", "4102",
            string.Join(",", CompiledPath.Compile("ea#4102").ReferencedAttributeIds));

        // An attribute name containing '#' has to survive: it is a real Aprimo pattern
        // and it collides with the ea#id syntax.
        Eq("a '#' inside an attribute name survives",
            "extendedAttributes[attributeName=Photo#1].eaValue",
            CompiledPath.Compile("ea:Photo#1").Expanded);

        Throws<FormatException>("an empty path is rejected", () => CompiledPath.Compile("  "));
        Throws<FormatException>("an unclosed selector is rejected",
            () => CompiledPath.Compile("a[0"), "Unclosed");
        Throws<FormatException>("a negative index is rejected",
            () => CompiledPath.Compile("a[-1]"), "Negative array index");
        Throws<FormatException>("a bare ea: is rejected",
            () => CompiledPath.Compile("ea:"), "requires a value");
        Throws<FormatException>("a non-numeric ea# is rejected",
            () => CompiledPath.Compile("ea#Region"), "numeric");
        Throws<FormatException>("a selector inside the shorthand is rejected",
            () => CompiledPath.Compile("ea:X[0]"), "plain name");
        Throws<FormatException>("a bare prop: is rejected",
            () => CompiledPath.Compile("prop:"), "requires a property");
    }

    // ───────────────────────────── B. extraction ─────────────────────────────

    private static void Extraction()
    {
        Section("Extraction");

        using var document = Json(ProjectItem);
        var item = document.RootElement;

        Eq("a scalar property", "4471", Extract(item, "projectId"));
        Eq("a string property", "Spring Launch", Extract(item, "title"));
        Eq("a nested property", "Green", Extract(item, "outlook.healthIndicator"));
        Eq("a boolean formats as lowercase", "false", Extract(item, "changeOrder"));
        Eq("a numeric zero is kept, not treated as empty", "0", Extract(item, "enableAlap"));
        Eq("a null property resolves to nothing", "", Extract(item, "projectedEndDate"));
        Eq("a missing property resolves to nothing", "", Extract(item, "noSuchProperty"));

        Eq("an extended attribute by name", "Retail", Extract(item, "ea:Business Unit"));
        Eq("an extended attribute by ID", "Retail", Extract(item, "ea#4102"));
        Eq("an attribute name containing '#'", "hero.jpg", Extract(item, "ea:Photo#1"));
        Eq("a numeric attribute value keeps its raw text", "91055", Extract(item, "ea:Budget Code"));

        // The case that decides whether a tenant needs name- or ID-based mappings.
        Eq("an attribute with a null name is unreachable by name", "", Extract(item, "ea:Unnamed"));
        Eq("an attribute with a null name is reachable by ID", "unnamed-value", Extract(item, "ea#4104"));

        Eq("multi-value attributes join on the separator", "51|52", Extract(item, "eamulti:Channels"));
        Eq("multi-value by ID joins the same way", "51|52", Extract(item, "eamulti#4110"));
        Eq("a single multi-value entry does not gain a separator", "60", Extract(item, "eamulti#4111"));

        Eq("a wildcard walk joins every element",
            "Business Unit|Photo#1|Budget Code|Dirty", Extract(item, "extendedAttributes[*].attributeName"));

        Eq("an array index selects one element", "Scope", Extract(item, "changeOrderCategories[0].name"));
        Eq("an out-of-range index resolves to nothing", "", Extract(item, "changeOrderCategories[9].name"));
        Eq("a key=value filter selects the match", "Budget",
            Extract(item, "changeOrderCategories[id=2].name"));

        Eq("a fallback takes the first branch that resolves", "Spring Launch",
            Extract(item, "noSuchProperty || title || projectId"));
        Eq("an empty-string property falls through to the next branch", "Retail",
            Extract(item, "description || ea:Business Unit"));
        Eq("a fallback with nothing resolving is empty", "",
            Extract(item, "nope || alsoNope"));

        // The bare-name expansion the field-list loader produces.
        Eq("bare-name expansion finds the property first", "Spring Launch",
            Extract(item, FieldListLoader.BareNamePath("title")));
        Eq("bare-name expansion falls back to the attribute", "Retail",
            Extract(item, FieldListLoader.BareNamePath("Business Unit")));

        Eq("an object at a leaf is emitted as raw JSON",
            "{\"healthIndicator\":\"Green\",\"score\":88}",
            Extract(item, "outlook").Replace(" ", "").Replace("\n", "").Replace("\r", ""));

        var defaulted = new FieldExtractor(
            new[] { new FieldMapping { Column = "c", Path = "nope", Default = "n/a" } }, "|");
        var row = new string[1];
        defaulted.ExtractRow(item, row);
        Eq("an unresolved column falls back to its Default", "n/a", row[0]);

        // A value that itself contains the separator makes the joined cell ambiguous.
        var collide = new FieldExtractor(
            new[]
            {
                new FieldMapping { Column = "Dirty", Path = "extendedAttributes[*].eaValue" },
                new FieldMapping { Column = "Clean", Path = "eamulti:Channels" }
            }, "|");
        var row2 = new string[2];
        collide.ExtractRow(item, row2);

        Check("a separator collision is reported",
            collide.ColumnsWithSeparatorInValue.Contains("Dirty"));
        Check("a clean multi-value column is not reported as colliding",
            !collide.ColumnsWithSeparatorInValue.Contains("Clean"));

        var extractor = new FieldExtractor(
            new[]
            {
                new FieldMapping { Column = "Title", Path = "title" },
                new FieldMapping { Column = "Unit", Path = "ea:Business Unit" }
            }, "|");

        Eq("headers follow the mapping order", "Title,Unit", string.Join(",", extractor.Headers));
        Eq("the column count matches", 2, extractor.ColumnCount);
        Check("an attribute mapping is detected", extractor.RequiresExtendedAttributes);
        Eq("referenced attribute names are exposed", "Business Unit",
            string.Join(",", extractor.ReferencedAttributeNames));

        Throws<FormatException>("a bad path names the offending column",
            () => new FieldExtractor(new[] { new FieldMapping { Column = "Bad", Path = "a[" } }, "|"),
            "Bad");

        Throws<ArgumentException>("a wrong-length destination is rejected",
            () => extractor.ExtractRow(item, new string[5]));

        // The buffers are reused between rows, so a second pass must not see the first.
        var reused = new string[2];
        extractor.ExtractRow(item, reused);
        var first = string.Join("|", reused);
        extractor.ExtractRow(item, reused);
        Eq("repeated extraction is stable", first, string.Join("|", reused));
    }

    // ───────────────────────────── C. field lists ─────────────────────────────

    private static void FieldLists()
    {
        Section("Field lists");

        Eq("whitespace becomes an underscore", "Alt_Text", FieldListLoader.ToColumnName("Alt Text"));
        Eq("a question mark is dropped", "Approved", FieldListLoader.ToColumnName("Approved?"));
        Eq("a hash is dropped from the header", "Photo4", FieldListLoader.ToColumnName("Photo#4"));
        Eq("underscores are kept", "a_b", FieldListLoader.ToColumnName("a_b"));
        Eq("a name of only symbols falls back to itself", "!!", FieldListLoader.ToColumnName("!!"));

        Eq("a leading comment is stripped", "", FieldListLoader.StripComment("# all comment").Trim());
        Eq("a trailing comment after whitespace is stripped", "Brand",
            FieldListLoader.StripComment("Brand  # a note").Trim());
        Eq("a slashes comment is stripped", "Brand",
            FieldListLoader.StripComment("Brand  // a note").Trim());

        // The rule that keeps 'Photo#1' and the 'ea#123' syntax intact.
        Eq("a '#' not preceded by whitespace is not a comment", "ea#4102",
            FieldListLoader.StripComment("ea#4102").Trim());
        Eq("an attribute name with '#' survives comment stripping", "Photo#1",
            FieldListLoader.StripComment("Photo#1").Trim());

        Eq("a bare name tries the property then the attribute",
            "title || ea:title", FieldListLoader.BareNamePath("title"));
        Eq("a bare name is camel-cased for the property branch",
            "projectId || ea:ProjectId", FieldListLoader.BareNamePath("ProjectId"));
        Eq("a name with a space cannot be a property, so only the attribute is emitted",
            "ea:Business Unit", FieldListLoader.BareNamePath("Business Unit"));
        Eq("a name with a hash emits only the attribute branch",
            "ea:Photo#1", FieldListLoader.BareNamePath("Photo#1"));

        var pmObject = ProjectsObject();
        var directory = Path.Combine(Path.GetTempPath(), "pmx-fields-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "projects.txt");

            File.WriteAllText(path, """
            # A comment line
            title, projectStatus ; modifiedDate
            prop:activityId
            ea:Business Unit
            eamulti:Channels
            Health => outlook.healthIndicator
            Owner  => createdBy.name || createdBy.userName   # fallback chain
            ea#4104
            """);

            var mappings = FieldListLoader.Load(path, pmObject, Noop);

            Eq("every entry is loaded", 9, mappings.Count);
            Eq("comma/semicolon separated names all load", "title,projectStatus,modifiedDate",
                string.Join(",", mappings.Take(3).Select(m => m.Column)));
            Eq("a prop: entry keeps its prefix in the path", "prop:activityId", mappings[3].Path);
            Eq("a prop: entry drops the prefix from the header", "activityId", mappings[3].Column);
            Eq("an ea: entry becomes a clean header", "Business_Unit", mappings[4].Column);
            Eq("an ea: entry keeps its path", "ea:Business Unit", mappings[4].Path);
            Eq("an eamulti: entry loads", "eamulti:Channels", mappings[5].Path);
            Eq("an explicit mapping keeps its column", "Health", mappings[6].Column);
            Eq("an explicit mapping keeps its path", "outlook.healthIndicator", mappings[6].Path);
            Eq("a trailing comment is stripped from an explicit mapping",
                "createdBy.name || createdBy.userName", mappings[7].Path);
            Eq("an ea#id entry loads with an ID header", "4104", mappings[8].Column);
            Eq("an ea#id entry keeps its path", "ea#4104", mappings[8].Path);

            // Every path in a real file must compile.
            foreach (var mapping in mappings)
                CompiledPath.Compile(mapping.Path);
            _passed++;

            File.WriteAllText(Path.Combine(directory, "empty.txt"), "# only a comment\n\n");
            Throws<InvalidOperationException>("a file of only comments is rejected",
                () => FieldListLoader.Load(Path.Combine(directory, "empty.txt"), pmObject, Noop),
                "no field entries");

            File.WriteAllText(Path.Combine(directory, "bad.txt"), "=> nothing\n");
            Throws<InvalidOperationException>("a mapping with no column is rejected",
                () => FieldListLoader.Load(Path.Combine(directory, "bad.txt"), pmObject, Noop),
                "line 1");

            Throws<FileNotFoundException>("a missing file is reported",
                () => FieldListLoader.Load(Path.Combine(directory, "nope.txt"), pmObject, Noop));

            // A UTF-8 BOM must not corrupt the first name.
            File.WriteAllText(Path.Combine(directory, "bom.txt"), "title\ndescription\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var bom = FieldListLoader.Load(Path.Combine(directory, "bom.txt"), pmObject, Noop);
            Eq("a BOM is stripped from the first name", "title", bom[0].Column);

            // Per-object resolution: the object's own file wins over the shared default.
            var config = new ExportConfig { FieldsDirectory = directory, DefaultFieldsFile = "_default.txt" };
            File.WriteAllText(Path.Combine(directory, "_default.txt"), "sharedColumn\n");

            var perObject = FieldListLoader.LoadForObject(pmObject, config, null, null, Noop);
            Check("the per-object file wins over the shared default",
                perObject is not null && perObject.Any(m => m.Column == "title"));

            var other = new PmObject { Name = "widgets", Path = "/widgets", IdProperty = "widgetId" };
            var shared = FieldListLoader.LoadForObject(other, config, null, null, Noop);
            Check("an object with no file of its own falls back to the shared default",
                shared is not null && shared.Any(m => m.Column == "sharedColumn"));

            // The shared list cannot name an ID, since every object calls its own something
            // different. Without this a bare export would be a CSV with no identifier.
            Eq("which is led by the object's own ID, taken from the registry", "Id", shared![0].Column);
            Eq("read from that object's own ID property", "prop:widgetId", shared[0].Path);
            Eq("and nothing else is lost", 2, shared.Count);

            File.WriteAllText(Path.Combine(directory, "_default.txt"), "Id => prop:somethingElse\nname\n");
            var hasId = FieldListLoader.LoadForObject(other, config, null, null, Noop)!;
            Eq("a shared list that already has an Id column is not given a second one", 1,
                hasId.Count(m => m.Column.Equals("Id", StringComparison.OrdinalIgnoreCase)));
            Eq("and the list's own mapping stands", "prop:somethingElse", hasId[0].Path);
            File.WriteAllText(Path.Combine(directory, "_default.txt"), "sharedColumn\n");

            config.DefaultFieldsFile = null;
            Check("with no shared default and no per-object file, nothing is returned",
                FieldListLoader.LoadForObject(other, config, null, null, Noop) is null);

            Throws<FileNotFoundException>("an explicit --fields-file that does not exist is an error",
                () => FieldListLoader.LoadForObject(pmObject, config, null,
                    Path.Combine(directory, "absent.txt"), Noop));

            // Derivation from one observed item.
            using var document = Json(ProjectItem);
            var derived = FieldListLoader.DeriveFromItem(document.RootElement, pmObject);

            Check("derivation includes scalar properties",
                derived.Any(m => m.Column == "projectId" && m.Path == "prop:projectId"));
            Check("derivation skips nested objects",
                derived.All(m => m.Column != "outlook"));
            Check("derivation skips arrays",
                derived.All(m => m.Column != "changeOrderCategories"));
            Check("derivation expands named attributes",
                derived.Any(m => m.Path == "ea:Business Unit"));
            Check("derivation falls back to an ID for an unnamed attribute",
                derived.Any(m => m.Path == "ea#4104"));
            Check("derivation expands multi-value attributes",
                derived.Any(m => m.Path == "eamulti:Channels"));
            Check("derived columns are unique",
                derived.Select(m => m.Column).Distinct(StringComparer.OrdinalIgnoreCase).Count() == derived.Count);

            foreach (var mapping in derived) CompiledPath.Compile(mapping.Path);
            _passed++;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ───────────────────────────── D. object registry ─────────────────────────────

    private static void ObjectRegistry(string scratch)
    {
        Section("Object registry");

        var path = Path.Combine(scratch, "objects.json");

        File.WriteAllText(path, """
        {
          // comments are allowed
          "Objects": [
            { "Name": "projects", "Path": "/projects", "IdProperty": "projectId",
              "ModifiedProperty": "modifiedDate", "SupportsExtendedAttributes": true,
              "DetailPath": "/projects/{id}" },
            { "Name": "teams", "Path": "/teams", "IdProperty": "teamId" },
            { "Name": "logs", "Path": "/logs", "IdProperty": "logId", "Enabled": false }
          ]
        }
        """);

        var catalog = ObjectCatalog.Load(path, Noop);

        Eq("every object loads", 3, catalog.Objects.Count);
        Eq("a disabled object is excluded from the enabled set", 2, catalog.EnabledObjects.Count);

        Check("lookup is case-insensitive", catalog.Find("PROJECTS") is not null);
        Check("an unknown name is not found", catalog.Find("nope") is null);

        Eq("'all' expands to the enabled objects", "projects,teams",
            string.Join(",", catalog.Resolve("all").Select(o => o.Name)));
        Eq("'*' also expands to the enabled objects", "projects,teams",
            string.Join(",", catalog.Resolve("*").Select(o => o.Name)));

        Eq("a named list resolves in order", "teams,projects",
            string.Join(",", catalog.Resolve("teams,projects").Select(o => o.Name)));
        Eq("names may be space separated", "teams,projects",
            string.Join(",", catalog.Resolve("teams projects").Select(o => o.Name)));
        Eq("duplicates collapse", "projects",
            string.Join(",", catalog.Resolve("projects,projects").Select(o => o.Name)));

        // A disabled object is excluded from 'all' but must still be reachable, since
        // excluding it is the reason it has a name.
        Eq("a disabled object can still be named explicitly", "logs",
            string.Join(",", catalog.Resolve("logs").Select(o => o.Name)));

        Throws<ArgumentException>("an unknown name is rejected",
            () => catalog.Resolve("nope"), "Unknown object");
        Throws<ArgumentException>("the error lists the available names",
            () => catalog.Resolve("nope"), "projects");
        Throws<ArgumentException>("an empty selection is rejected",
            () => catalog.Resolve("  "), "No objects selected");

        var projects = catalog.Find("projects")!;
        Check("SupportsDelta follows ModifiedProperty", projects.SupportsDelta);
        Eq("the default sort field is the modified property", "modifiedDate", projects.DefaultSortField);
        Eq("the fields file name follows the object name", "projects.txt", projects.FieldsFileName);
        Eq("the detail path substitutes the ID", "/projects/4471", projects.ResolveDetailPath("4471"));

        var teams = catalog.Find("teams")!;
        Check("an object with no modified property does not support delta", !teams.SupportsDelta);
        Eq("the sort field falls back to the ID property", "teamId", teams.DefaultSortField);

        // Validation.
        Check("a valid registry has no errors", !catalog.Validate().Any());

        var bad = new ObjectCatalog
        {
            Objects =
            {
                new PmObject { Name = "a", Path = "projects", IdProperty = "id" },
                new PmObject { Name = "a", Path = "/b", IdProperty = "id" },
                new PmObject { Name = "c", Path = "/c", IdProperty = "" },
                new PmObject { Name = "d", Path = "/d", IdProperty = "id", DetailPath = "/d/x" },
                new PmObject { Name = "e", Path = "/e", IdProperty = "id", ModifiedIsEpoch = true }
            }
        };

        var errors = bad.Validate().ToList();
        Check("a path without a leading slash is rejected",
            errors.Any(e => e.Contains("must start with '/'")));
        Check("a duplicate name is rejected", errors.Any(e => e.Contains("duplicate object name")));
        Check("a missing IdProperty is rejected", errors.Any(e => e.Contains("'IdProperty' is required")));
        Check("a DetailPath without {id} is rejected", errors.Any(e => e.Contains("{id}")));
        Check("ModifiedIsEpoch without a modified property is rejected",
            errors.Any(e => e.Contains("ModifiedIsEpoch")));

        File.WriteAllText(Path.Combine(scratch, "broken.json"), "{ not json");
        Throws<InvalidOperationException>("invalid JSON is reported",
            () => ObjectCatalog.Load(Path.Combine(scratch, "broken.json"), Noop), "not valid JSON");

        File.WriteAllText(Path.Combine(scratch, "invalid.json"),
            """{ "Objects": [ { "Name": "x", "Path": "no-slash", "IdProperty": "id" } ] }""");
        Throws<InvalidOperationException>("a structurally invalid registry is rejected at load",
            () => ObjectCatalog.Load(Path.Combine(scratch, "invalid.json"), Noop), "is invalid");

        Throws<FileNotFoundException>("a missing registry is reported",
            () => ObjectCatalog.Load(Path.Combine(scratch, "absent.json"), Noop));
    }

    // ──────────────────── D2. metadata field classification ────────────────────

    /// <summary>
    /// Telling a system field from an extended attribute.
    ///
    /// <para>Getting this wrong is not cosmetic: the tenant this was developed against
    /// returned all 35 fields of <c>/metadata/project</c> as system fields with qualified names like
    /// <c>Project.ProjectId</c> and negative <c>amsAttributeId</c>s, and treating them as
    /// attributes generated 26 columns reading <c>ea:Project.ProjectId</c> — every one of
    /// which resolved to an empty cell, duplicating a column that was already correct.
    /// </para>
    /// </summary>
    private static void MetadataFieldClassification()
    {
        Section("Metadata field classification");

        MetadataField Field(
            string fieldName,
            string? entity = null,
            int? attributeId = null,
            string? selectionList = null,
            int? lookupListId = null,
            string? systemType = null) =>
            new(fieldName, entity, null, null, attributeId, selectionList, lookupListId,
                systemType, false, false, false, null);

        // Observed on the live tenant: qualified name, negative pseudo-ID.
        var system = Field("Project.ProjectId", attributeId: -11011);
        Check("a qualified name marks a system field", system.IsSystemField);
        Eq("and resolves to the object's own property", "projectId", system.PropertyName);

        var negativeOnly = Field("SomethingUnqualified", attributeId: -42);
        Check("a negative attribute ID alone marks a system field", negativeOnly.IsSystemField);
        Eq("whose property is the whole name, camel-cased", "somethingUnqualified",
            negativeOnly.PropertyName);

        var dottedOnly = Field("Project.BrandName");
        Check("a qualified name alone is enough, without an ID", dottedOnly.IsSystemField);
        Eq("taking the segment after the last dot", "brandName", dottedOnly.PropertyName);

        var withEntity = Field("Project.ProjectId", entity: "ProjectId", attributeId: -11011);
        Eq("an explicit entityFieldName wins over the qualified name", "projectId",
            withEntity.PropertyName);

        // A real tenant-defined attribute: bare name, positive ID.
        var attribute = Field("reviewFlag", attributeId: 201);
        Check("a bare name with a positive ID is an extended attribute", !attribute.IsSystemField);
        Check("which has no property name", attribute.PropertyName is null);

        var noId = Field("reviewFlag");
        Check("a bare name with no ID is still an extended attribute", !noId.IsSystemField);

        // Lookup detection — what decides whether a column holds a code or a value.
        Check("a field with no list declaration has no lookup", !attribute.HasLookup);
        Check("a selection list counts as a lookup",
            Field("x", selectionList: "Regions").HasLookup);
        Check("a system type counts as a lookup",
            Field("x", systemType: "ProjectStatus").HasLookup);
        Check("a lookup list ID counts as a lookup",
            Field("x", lookupListId: 77).HasLookup);
        Check("a zero lookup list ID does not count",
            !Field("x", lookupListId: 0).HasLookup);

        Eq("DisplayName prefers the field name", "reviewFlag", attribute.DisplayName);
        Eq("falling back to the entity name", "ProjectId",
            Field("", entity: "ProjectId").DisplayName);
    }

    // ──────────────────── D3. label resolution ────────────────────

    /// <summary>
    /// The <c>label:</c> grammar and code-to-label substitution.
    ///
    /// <para>Fixtures use the shapes a live tenant actually returned, with synthetic values:
    /// a lookup list gives <c>{key, value}</c> and turns <c>projectStatus 4</c> into
    /// "Closed"; an attribute's pick list gives <c>{itemId, displayValue, systemName}</c>
    /// and turns <c>reviewFlag 201</c> into "Not Required".</para>
    /// </summary>
    private static void LabelResolution()
    {
        Section("Label resolution");

        // ---- grammar ----
        var status = CompiledPath.Compile("label:projectStatus");
        Eq("label: strips to the inner path", "projectStatus", status.Expanded);
        Eq("and keys the lookup by the property name", "projectStatus", status.LookupKey);

        var attribute = CompiledPath.Compile("label:ea:reviewFlag");
        Eq("label: composes with the ea shorthand",
            "extendedAttributes[attributeName=reviewFlag].eaValue", attribute.Expanded);
        Eq("keying the lookup by the attribute name", "reviewFlag", attribute.LookupKey);

        Eq("label: composes with eamulti", "channels",
            CompiledPath.Compile("label:eamulti:channels").LookupKey);
        Eq("label: composes with prop:", "projectStatus",
            CompiledPath.Compile("label:prop:projectStatus").LookupKey);

        // Only the first branch names the field, since a chain spanning two fields has
        // no single list to resolve against.
        Eq("a fallback chain keys off its first branch", "projectStatus",
            CompiledPath.Compile("label:projectStatus || statusId").LookupKey);
        Eq("a dotted path keys off its leading segment", "outlook",
            CompiledPath.Compile("label:outlook.healthIndicator").LookupKey);

        Check("an ordinary path has no lookup key",
            CompiledPath.Compile("projectStatus").LookupKey is null);

        Throws<FormatException>("a bare label: is rejected",
            () => CompiledPath.Compile("label:"), "requires a path");

        // ---- table and resolver ----
        var table = new LookupTable("/lookup/100", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = "Draft", ["4"] = "Closed"
        });

        Check("a table resolves a known code", table.TryGet("4", out var closed) && closed == "Closed");
        Check("and misses an unknown one", !table.TryGet("99", out _));

        Check("the empty resolver resolves nothing",
            !LookupResolver.Empty.TryResolve("projectStatus", "4", out _));
        Check("and reports itself as empty", LookupResolver.Empty.IsEmpty);

        // ---- substitution through the extractor ----
        using var document = Json("""
        {
          "projectId": 11,
          "projectStatus": 4,
          "retiredCode": 7,
          "extendedAttributes": [
            { "eaId": 28, "attributeName": "reviewFlag", "eaValue": 201 },
            { "eaId": 29, "attributeName": "multi", "eaValue": 201 }
          ],
          "multipleValueExtendedAttributes": [
            { "extAttributeId": 30, "encodedListItemId": 201, "attributeName": "picks" },
            { "extAttributeId": 30, "encodedListItemId": 202, "attributeName": "picks" }
          ]
        }
        """);

        var item = document.RootElement;

        var resolver = TestResolver(new Dictionary<string, Dictionary<string, string>>
        {
            ["projectStatus"] = new() { ["4"] = "Closed" },
            ["reviewFlag"] = new() { ["201"] = "Not Required" },
            ["picks"] = new() { ["201"] = "Alpha", ["202"] = "Beta" },
            ["retiredCode"] = new() { ["1"] = "Still Here" }
        });

        string Resolve(string path, string column = "c")
        {
            var extractor = new FieldExtractor(
                new[] { new FieldMapping { Column = column, Path = path } }, "|", resolver);
            var row = new string[1];
            extractor.ExtractRow(item, row);
            return row[0];
        }

        Eq("a system field's code becomes its label", "Closed", Resolve("label:projectStatus"));
        Eq("an attribute's code becomes its label",
            "Not Required", Resolve("label:ea:reviewFlag"));

        // Each element of a multi-value attribute needs its own label, so resolution has
        // to happen before the join rather than on the joined string.
        Eq("every value of a multi-value attribute is resolved separately",
            "Alpha|Beta", Resolve("label:eamulti:picks"));

        Eq("the raw path still yields the code", "4", Resolve("projectStatus"));

        // A code with no label keeps the code: blanking it would destroy the only
        // information the row carried, and look identical to a genuinely empty field.
        Eq("a code missing from its list keeps the code", "7", Resolve("label:retiredCode"));

        // A field the resolver never built a table for — e.g. every route it declared 404'd,
        // which a user-ID field's list did on the tenant this was developed against.
        Eq("a key with no list at all keeps the code", "11", Resolve("label:projectId"));

        var unresolvedExtractor = new FieldExtractor(
            new[] { new FieldMapping { Column = "Retired", Path = "label:retiredCode" } }, "|", resolver);
        unresolvedExtractor.ExtractRow(item, new string[1]);
        Check("and the column is reported",
            unresolvedExtractor.ColumnsWithUnresolvedCodes.Contains("Retired"));

        var cleanExtractor = new FieldExtractor(
            new[] { new FieldMapping { Column = "Status", Path = "label:projectStatus" } }, "|", resolver);
        cleanExtractor.ExtractRow(item, new string[1]);
        Check("a fully resolved column is not reported",
            cleanExtractor.ColumnsWithUnresolvedCodes.Count == 0);

        // With no resolver the label: path degrades to the code rather than to a blank,
        // so switching resolution off never changes a file's shape.
        var noResolver = new FieldExtractor(
            new[] { new FieldMapping { Column = "c", Path = "label:projectStatus" } }, "|");
        var plain = new string[1];
        noResolver.ExtractRow(item, plain);
        Eq("with lookups off, a label column holds the raw code", "4", plain[0]);

        // ---- which lists a column list needs ----
        var mappings = new[]
        {
            new FieldMapping { Column = "A", Path = "label:projectStatus" },
            new FieldMapping { Column = "B", Path = "label:ea:reviewFlag" },
            new FieldMapping { Column = "C", Path = "prop:title" },
            new FieldMapping { Column = "D", Path = "label:projectStatus" }
        };

        var keys = FieldExtractor.RequiredLookupKeys(mappings);
        Eq("only label: columns require a list", 2, keys.Count);
        Check("the property key is required", keys.Contains("projectStatus"));
        Check("the attribute key is required", keys.Contains("reviewFlag"));
        Eq("no lists are needed when nothing asks for one", 0,
            FieldExtractor.RequiredLookupKeys(
                new[] { new FieldMapping { Column = "C", Path = "prop:title" } }).Count);
    }

    // ──────────────────── D4. user probe ────────────────────

    /// <summary>
    /// The user probe's two guarantees: it only sends numeric IDs, and it never prints
    /// personal data.
    ///
    /// <para><c>GET /users/{id}</c> is typed <c>UserGroup</c> in the spec, which is a full
    /// user record — email, phone, postal address, employee number. The summary has to
    /// show a name and nothing that could be used to contact the person.</para>
    /// </summary>
    private static void UserProbe()
    {
        Section("User probe");

        var (valid, rejected) = LookupProbe.ParseIds("1001, 1002;1003 1001 abc 12x ../etc -1");
        Eq("numeric IDs are accepted", "1001,1002,1003", string.Join(",", valid));
        Check("a repeated ID is listed once", valid.Count(v => v == "1001") == 1);
        Check("a non-numeric token is rejected", rejected.Contains("abc"));
        Check("a mixed token is rejected", rejected.Contains("12x"));
        Check("a path-traversal token is rejected, not escaped and sent", rejected.Contains("../etc"));
        Check("a negative number is rejected", rejected.Contains("-1"));
        Eq("nothing valid from nothing", 0, LookupProbe.ParseIds("  ,; ").Valid.Count);

        using var document = Json("""
        {
          "userId": 1001,
          "firstName": "Pat",
          "lastName": "Example",
          "activeFlag": 1,
          "email": "pat.example@corp.example",
          "phone": "+1 555 0100",
          "mobileNumber": "+1 555 0199",
          "address1": "1 Example Street",
          "employeeNumber": "E-99",
          "loginId": "pexample",
          "costCenter": "CC-7",
          "middleName": null,
          "comments": "",
          "groups": [],
          "rights": [ { "id": 1 } ],
          "_links": { "self": { "href": "/users/1001" } }
        }
        """);

        var (safe, withheld) = LookupProbe.SummarizeUser(document.RootElement);

        Check("the name is shown", safe.Contains("Pat") && safe.Contains("Example"));
        Check("the ID is shown", safe.Contains("1001"));

        foreach (var secret in new[]
                 {
                     "pat.example@corp.example", "555 0100", "555 0199", "1 Example Street",
                     "E-99", "pexample", "CC-7"
                 })
            Check($"'{secret}' never appears in what is printed", !safe.Contains(secret));

        Check("withheld properties are listed by name", withheld.Contains("email") && withheld.Contains("phone"));
        Check("an unpopulated property is not listed", !withheld.Contains("middleName") && !withheld.Contains("comments"));
        Check("an empty array is not listed", !withheld.Contains("groups"));
        Check("a populated array is listed by name only", withheld.Contains("rights"));
        Check("HAL plumbing is ignored", !withheld.Contains("_links"));
        Check("nothing withheld repeats a printed field", !withheld.Contains("firstName"));

        var (notObject, none) = LookupProbe.SummarizeUser(Json("[1,2]").RootElement);
        Check("a non-object is described rather than thrown on", notObject.Length > 0 && none.Count == 0);

        // ---- the verdict ----
        // The first version of this probe read "not in the list, and a surname search found
        // nothing" as evidence that filterText was broken. It is not: a search of a list the
        // user is absent from finds nothing whether or not the search works. These cases pin
        // what each pattern of results may and may not be taken to mean.
        static LookupProbe.UserOutcome In(bool? active) => new(true, active);
        static LookupProbe.UserOutcome Out(bool? active) => new(false, active);
        static string Verdict(params LookupProbe.UserOutcome[] o) =>
            string.Join("\n", LookupProbe.Conclude(o));

        var activeOnly = Verdict(In(true), In(true), In(true), Out(false), Out(false));
        Check("only inactive users missing: fits an active-only list",
            activeOnly.Contains("fits a list of active users only"));
        Check("and says outright that it is not proof", activeOnly.Contains("not proof"));
        Check("and does not claim a cap", !activeOnly.Contains("cap, or"));

        var capped = Verdict(In(true), In(true), Out(true), Out(false));
        Check("an ACTIVE user missing overturns the active-only reading",
            capped.Contains("ACTIVE user(s) are missing") && capped.Contains("cap"));
        Check("and that reading is not offered alongside it", !capped.Contains("fits a list of active users only"));

        var inactiveListed = Verdict(In(true), In(false), Out(false));
        Check("an inactive user in the list means inactivity does not exclude",
            inactiveListed.Contains("DO appear in the list"));

        Check("with nobody outside the list there is nothing to conclude",
            Verdict(In(true), In(true)).Contains("nothing can be concluded"));
        Check("with no users at all there is nothing to conclude",
            Verdict().Contains("nothing can be concluded"));

        var withUnknown = Verdict(In(true), Out(false), Out(null));
        Check("a user with no activeFlag is reported, not silently counted",
            withUnknown.Contains("no activeFlag"));
        Check("and is kept out of the table's counts", withUnknown.Contains("fits a list of active users only"));

        Check("the table carries all four cells",
            Verdict(In(true), In(false), Out(true), Out(false)).Contains("not in the list"));

        // ---- sampling and reading ----
        var pool = Enumerable.Range(1, 600).Select(i => i.ToString()).ToList();
        var spread = LookupProbe.Spread(pool, 12).ToList();
        Eq("a spread returns the requested count", 12, spread.Count);
        Check("starting at the front", spread[0] == "1");
        Check("and reaching deep into the list, not just its first entries",
            int.Parse(spread[^1]) > 500);
        Check("with no repeats", spread.Distinct().Count() == spread.Count);
        Eq("a short pool is returned whole", 3, LookupProbe.Spread(new[] { "a", "b", "c" }, 12).Count());
        Eq("an empty pool yields nothing", 0, LookupProbe.Spread(Array.Empty<string>(), 12).Count());

        using var activeDoc = Json("""{ "activeFlag": 1 }""");
        using var inactiveDoc = Json("""{ "activeFlag": 0 }""");
        using var missingDoc = Json("""{ "userId": 1 }""");
        using var textDoc = Json("""{ "activeFlag": "yes" }""");
        Check("activeFlag 1 is active", LookupProbe.ReadActive(activeDoc.RootElement) == true);
        Check("activeFlag 0 is inactive", LookupProbe.ReadActive(inactiveDoc.RootElement) == false);
        Check("a missing flag is unknown, not inactive", LookupProbe.ReadActive(missingDoc.RootElement) is null);
        Check("a non-numeric flag is unknown, not inactive", LookupProbe.ReadActive(textDoc.RootElement) is null);

        using var nameDoc = Json("""{ "firstName": "Pat", "lastName": "Example", "email": "pat@corp.example" }""");
        Eq("a display name is first and last", "Pat Example", LookupProbe.DisplayName(nameDoc.RootElement));
        Check("and carries nothing else", !LookupProbe.DisplayName(nameDoc.RootElement).Contains("corp.example"));
        using var surnameOnly = Json("""{ "lastName": "Service Account" }""");
        Eq("a record with only a last name still reads", "Service Account",
            LookupProbe.DisplayName(surnameOnly.RootElement));
        using var nameless = Json("""{ "userId": 5 }""");
        Eq("a record with no name says so", "(no name)", LookupProbe.DisplayName(nameless.RootElement));

        Check("--probe-users parses",
            CommandLineArgs.Parse(new[] { "--probe-users", "1001,1002" }).ProbeUsers == "1001,1002");
        Throws<ArgumentException>("--probe-users needs a value",
            () => CommandLineArgs.Parse(new[] { "--probe-users" }), "requires a value");
        Check("the usage text mentions it and the privacy guarantee",
            CommandLineArgs.Usage.Contains("--probe-users") && CommandLineArgs.Usage.Contains("never email"));
    }

    // ──────────────────── D5. user resolution ────────────────────

    /// <summary>
    /// Resolving user IDs to names through <c>/users/{id}</c>.
    ///
    /// <para>The lookup list behind <c>projectManager</c> is unusable — capped near 600 and
    /// active users only — which is why this route exists. Fixtures use the record shape the
    /// live tenant returned: a flat <c>UserGroup</c> with <c>firstName</c>, <c>lastName</c> and
    /// a good deal of personal data that must never be kept.</para>
    /// </summary>
    private static async Task UserResolution()
    {
        Section("User resolution");

        // ---- grammar ----
        var path = CompiledPath.Compile("user:projectManager");
        Check("user: marks a path as resolving users", path.IsUser);
        Eq("and strips to the inner path", "projectManager", path.Expanded);
        Check("with no lookup key, since users are fetched by ID rather than by list",
            path.LookupKey is null);
        Check("an ordinary path is not a user path", !CompiledPath.Compile("projectManager").IsUser);
        Check("label: is not a user path", !CompiledPath.Compile("label:projectManager").IsUser);
        Check("label: has no user flag set by accident", CompiledPath.Compile("label:x").LookupKey == "x");
        Check("user: composes with prop:", CompiledPath.Compile("user:prop:projectManager").IsUser);
        Eq("and with ea:", "extendedAttributes[attributeName=Owner].eaValue",
            CompiledPath.Compile("user:ea:Owner").Expanded);
        Throws<FormatException>("a bare user: is rejected",
            () => CompiledPath.Compile("user:"), "requires a path");

        // ---- name formatting ----
        string? Format(string json)
        {
            using var doc = Json(json);
            return UserResolver.FormatName(doc.RootElement);
        }

        Eq("a name is Last, First", "Example, Pat",
            Format("""{ "firstName": "Pat", "lastName": "Example" }"""));
        Eq("matching the lookup list's own format", "Example, Pat",
            Format("""{ "lastName": "Example", "firstName": "Pat" }"""));
        Eq("a last name alone is used as it is — the service user has its whole name there",
            "Service Account", Format("""{ "lastName": "Service Account" }"""));
        Eq("a first name alone is used", "Pat", Format("""{ "firstName": "Pat" }"""));
        Check("no name at all gives null, so the cell keeps the code",
            Format("""{ "userId": 5, "email": "a@b.example" }""") is null);
        Check("blank names give null", Format("""{ "firstName": " ", "lastName": "" }""") is null);
        Eq("surrounding whitespace is trimmed", "Example, Pat",
            Format("""{ "firstName": " Pat ", "lastName": " Example " }"""));
        Check("a non-object gives null", Format("[1]") is null);

        // ---- the resolver ----
        var calls = new List<string>();
        var messages = new List<string>();

        const string record = """
        {
          "userId": 1002, "firstName": "Pat", "lastName": "Example", "activeFlag": 0,
          "email": "Pat.Example@corp.example", "phone": "+1 555 0100",
          "address1": "1 Example Street", "employeeNumber": "E-77"
        }
        """;

        Task<JsonDocument?> Fetch(string id, CancellationToken _)
        {
            calls.Add(id);

            return Task.FromResult<JsonDocument?>(id switch
            {
                "1002" => JsonDocument.Parse(record),
                "1001" => JsonDocument.Parse("""{ "firstName": "Sam", "lastName": "Sample" }"""),
                "5000" => JsonDocument.Parse("""{ "userId": 5000 }"""),   // exists, but nameless
                _ => null                                                // not found
            });
        }

        var resolver = new UserResolver(Fetch, messages.Add);

        await resolver.PrefetchAsync(new[] { "1002", "1001", "1002", "1001", "1002" }, default);
        Eq("each distinct user is fetched once, however often it repeats", 2, calls.Count);
        Eq("and counted as a request", 2, resolver.Requests);

        Check("a fetched user resolves", resolver.TryGet("1002", out var name) && name == "Example, Pat");
        Check("an inactive user resolves too — the reason this route exists",
            resolver.TryGet("1001", out var second) && second == "Sample, Sam");

        // The name is all that is kept. A resolver that held onto the record would be
        // holding an email address, phone number and postal address per user.
        resolver.TryGet("1002", out var kept);
        foreach (var secret in new[] { "Pat.Example@corp.example", "555 0100", "1 Example Street", "E-77" })
            Check($"'{secret}' is not retained", !kept.Contains(secret));

        await resolver.PrefetchAsync(new[] { "1002", "1001" }, default);
        Eq("a second prefetch of known users sends nothing", 2, calls.Count);

        await resolver.PrefetchAsync(new[] { "9999" }, default);
        Eq("an unknown user is requested once", 3, calls.Count);
        Check("and does not resolve", !resolver.TryGet("9999", out _));

        await resolver.PrefetchAsync(new[] { "9999" }, default);
        Eq("a user that was not found is not asked for again", 3, calls.Count);

        await resolver.PrefetchAsync(new[] { "5000" }, default);
        Check("a user with no name does not resolve", !resolver.TryGet("5000", out _));
        Eq("the counts add up", 2, resolver.Resolved);
        Eq("including the unresolved", 2, resolver.Unresolved);

        // IDs go into a URL path, so anything that is not all digits is dropped unsent.
        var before = calls.Count;
        await resolver.PrefetchAsync(new[] { "abc", "../x", "", "-1", "12x", "{\"a\":1}" }, default);
        Eq("non-numeric values are never sent", before, calls.Count);
        Check("and are not cached as users", !resolver.TryGet("abc", out _));

        Check("a user never fetched does not resolve", !resolver.TryGet("123456", out _));

        // A failure costs one name, not the export.
        var failing = new UserResolver((id, _) =>
            id == "2" ? throw new ApiException("500 Internal Server Error") : Task.FromResult<JsonDocument?>(
                JsonDocument.Parse("""{ "firstName": "A", "lastName": "B" }""")),
            messages.Add);

        messages.Clear();
        await failing.PrefetchAsync(new[] { "1", "2", "3" }, default);
        Check("a failing user is left unresolved", !failing.TryGet("2", out _));
        Check("and the others still resolve", failing.TryGet("1", out _) && failing.TryGet("3", out _));
        Eq("the failure is counted", 1, failing.Failures);
        Check("and reported without any personal data", messages.Count == 1 && messages[0].Contains("user 2"));

        // The log has to stay readable when a whole range of users is unreadable.
        var allFail = new UserResolver((_, _) => throw new ApiException("403 Forbidden"), messages.Add);
        messages.Clear();
        await allFail.PrefetchAsync(Enumerable.Range(1, 40).Select(i => i.ToString()), default);
        Eq("every failure is counted", 40, allFail.Failures);
        Check("but only the first few are described", messages.Count <= 6);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var threw = false;
        try { await resolver.PrefetchAsync(new[] { "777777" }, cancelled.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check("cancellation is honoured rather than swallowed", threw);

        // ---- through the extractor ----
        using var itemDoc = Json("""
        {
          "projectId": 11,
          "projectManager": 1002,
          "modifiedUser": 1001,
          "unknownUser": 9999,
          "team": [ 1002, 1001 ],
          "backup": null,
          "status": 4
        }
        """);

        var item = itemDoc.RootElement;

        string Row(string pathText, UserResolver? users)
        {
            var extractor = new FieldExtractor(
                new[] { new FieldMapping { Column = "c", Path = pathText } }, "|", null, users);
            var row = new string[1];
            extractor.ExtractRow(item, row);
            return row[0];
        }

        Eq("a user column shows the name", "Example, Pat", Row("user:projectManager", resolver));
        Eq("another user, resolved the same way", "Sample, Sam", Row("user:modifiedUser", resolver));
        Eq("a user who cannot be resolved keeps the ID", "9999", Row("user:unknownUser", resolver));
        Eq("a list of users is resolved person by person", "Example, Pat|Sample, Sam",
            Row("user:team[*]", resolver));
        Eq("an empty value stays empty", "", Row("user:backup", resolver));
        Eq("the raw column still gives the ID", "1002", Row("projectManager", resolver));
        Eq("with no resolver the ID stays — switching resolution off keeps the column set",
            "1002", Row("user:projectManager", null));

        var unresolvedColumn = new FieldExtractor(
            new[] { new FieldMapping { Column = "Mgr", Path = "user:unknownUser" } }, "|", null, resolver);
        unresolvedColumn.ExtractRow(item, new string[1]);
        Check("an unresolved user column is reported", unresolvedColumn.ColumnsWithUnresolvedCodes.Contains("Mgr"));

        var noResolver = new FieldExtractor(
            new[] { new FieldMapping { Column = "Mgr", Path = "user:projectManager" } }, "|", null, null);
        noResolver.ExtractRow(item, new string[1]);
        Check("but switching resolution off is not reported as a failure",
            noResolver.ColumnsWithUnresolvedCodes.Count == 0);

        // ---- collecting what to prefetch ----
        var mixed = new FieldExtractor(new[]
        {
            new FieldMapping { Column = "Id", Path = "projectId" },
            new FieldMapping { Column = "Mgr", Path = "user:projectManager" },
            new FieldMapping { Column = "Mod", Path = "user:modifiedUser || user:projectManager" },
            new FieldMapping { Column = "Team", Path = "user:team[*]" },
            new FieldMapping { Column = "Status", Path = "label:status" }
        }, "|", null, resolver);

        Check("a column list with user columns is detected", mixed.HasUserColumns);
        Check("a column list without them is not",
            !new FieldExtractor(new[] { new FieldMapping { Column = "Id", Path = "projectId" } }, "|").HasUserColumns);

        var codes = mixed.UserCodes(new[] { item }).Distinct().OrderBy(c => c).ToList();
        Eq("only user columns contribute IDs, across every branch", "1001,1002", string.Join(",", codes));
        Check("a label: column's value is not mistaken for a user", !codes.Contains("4"));
        Check("a plain column's value is not either", !codes.Contains("11"));
        Eq("no items give no IDs", 0, mixed.UserCodes(Array.Empty<JsonElement>()).Count());

        // ---- config ----
        var output = new OutputConfig();
        Check("user resolution is on by default", output.ResolveUsers);
        Check("projectManager is a default user field",
            output.UserFields.Contains("projectManager", StringComparer.OrdinalIgnoreCase));
        Check("modifiedUser is a default user field",
            output.UserFields.Contains("modifiedUser", StringComparer.OrdinalIgnoreCase));
        Check("a status code is not", !output.UserFields.Contains("projectStatus", StringComparer.OrdinalIgnoreCase));

        // ---- what --discover-fields emits ----
        MetadataField Field(string fieldName, int attributeId, int? lookup = null) =>
            new(fieldName, null, 12, null, attributeId, null, lookup, null, false, false, false, null);

        var definition = new ResourceMetadata("Project", "t", true, false, new[]
        {
            Field("Project.ProjectManager", -1, 11),
            Field("Project.ModifiedUser", -2, 12),
            Field("Project.EnableAlap", -3, 13),
            Field("Project.CreatedBy", -4, 77)
        });

        string Generate(string sampleJson, ExportConfig? config = null)
        {
            using var sample = Json(sampleJson);
            var generator = new FieldListGenerator(config ?? new ExportConfig(), null!, null!, Noop);
            var render = typeof(FieldListGenerator).GetMethod(
                "Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            return (string)render.Invoke(generator, new object?[]
            {
                ProjectsObject(), definition, new List<JsonElement> { sample.RootElement.Clone() }
            })!;
        }

        var generated = Generate("""
            { "projectId": 1, "projectManager": 1002, "modifiedUser": 1001, "enableAlap": 0,
              "createdBy": { "id": "x", "userName": "someone" }, "modifiedDate": "2026-01-01T00:00:00Z" }
            """);

        Check("a user field gets a user: column", generated.Contains("projectManagerLabel => user:projectManager"));
        Check("including one whose own lookup list is not served",
            generated.Contains("modifiedUserLabel => user:modifiedUser"));
        Check("a user field does NOT also get the unusable label: route",
            !generated.Contains("label:projectManager") && !generated.Contains("label:modifiedUser"));
        Check("an ordinary coded field still uses label:", generated.Contains("enableAlapLabel => label:enableAlap"));
        Check("the code column is still emitted alongside",
            generated.Contains("projectManager => prop:projectManager"));
        Check("an object-valued createdBy holds no ID, so it is not turned into a user column",
            !generated.Contains("user:createdBy"));
        Eq("and each user column appears once", 1,
            generated.Split('\n').Count(l => l.StartsWith("projectManagerLabel", StringComparison.Ordinal)));

        var noSample = new ExportConfig();
        using (var empty = Json("{}"))
        {
            var generator = new FieldListGenerator(noSample, null!, null!, Noop);
            var render = typeof(FieldListGenerator).GetMethod(
                "Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var bare = (string)render.Invoke(generator,
                new object?[] { ProjectsObject(), definition, new List<JsonElement>() })!;

            Check("with no sample to contradict it, a named user field is still emitted",
                bare.Contains("projectManagerLabel => user:projectManager"));
        }

        var off = new ExportConfig();
        off.Output.GenerateCodeAndLabelColumns = false;
        Check("with label columns switched off, no user: column is generated either",
            !Generate("""{ "projectId": 1, "projectManager": 1002 }""", off).Contains("user:"));

        var custom = new ExportConfig();
        custom.Output.UserFields = new List<string> { "enableAlap" };
        var customOutput = Generate("""{ "projectId": 1, "projectManager": 1002, "enableAlap": 0 }""", custom);
        Check("the user-field list is honoured: a listed property is a user column",
            customOutput.Contains("user:enableAlap"));
        Check("and an unlisted one is not", !customOutput.Contains("user:projectManager"));
    }

    // ──────────────────── D6. depth probe ────────────────────

    /// <summary>
    /// What the depth probe concludes from what it read.
    ///
    /// <para>Offset paging can fail three ways that need different answers — a hard error, a
    /// quiet wrong answer (offset ignored or clamped, healthy 200), and latency that grows
    /// with depth — and a server that caps <c>limit</c> silently makes the reader stop after
    /// page one. Each is built here as a synthetic read, so the verdicts do not depend on a
    /// tenant misbehaving on cue.</para>
    /// </summary>
    private static void DepthProbeAnalysis()
    {
        Section("Depth probe");

        const long total = 536_180;
        const int limit = 100;

        // A healthy read: ascending keys that continue across offsets, distinct ids.
        static DepthSample S(
            long offset, int limit = 100, int? items = null, long ms = 200,
            string? first = null, string? last = null, double? firstKey = null, double? lastKey = null,
            bool ok = true, bool ordered = true, int attempts = 1, string? error = null)
        {
            var n = items ?? limit;
            return new DepthSample(offset, limit, ok, ms, ok ? n : 0,
                ok && n > 0 ? first ?? $"id{offset}" : null,
                ok && n > 0 ? last ?? $"id{offset + n - 1}" : null,
                ok && n > 0 ? firstKey ?? offset : null,
                ok && n > 0 ? lastKey ?? offset + n - 1 : null,
                ok && n > 0 ? "2020-01-01" : null, ordered, attempts, 50_000, ok ? null : error);
        }

        static string Text(List<DepthFinding> f) => string.Join("\n", f.Select(x => $"{x.Level}: {x.Text}"));
        static bool HasProblem(List<DepthFinding> f) => f.Any(x => x.Level == DepthLevel.Problem);

        // Offsets 536,180 (== total) and 536,181 (one beyond) mirror what the live tenant did:
        // the end is inclusive, so == total is an empty 200 and beyond it is a 400.
        var healthy = new[]
        {
            S(0), S(1_000), S(10_000), S(100_000), S(500_000),
            S(536_080, items: 100), S(536_180, items: 0),
            S(536_181, ok: false, error: "400 BadRequest {\"errors\":[{\"message\":\"offset must be in range '0' ... '536180'\"}]}")
        };

        var good = DepthProbe.Analyze(healthy, total, limit, ascending: true);
        Check("a healthy read has no problems", !HasProblem(good));
        var summary = good.First(f => f.Text.StartsWith("Every position read", StringComparison.Ordinal));
        Check("and says so, naming how deep it went — the deepest page that held rows, not an empty one",
            summary.Level == DepthLevel.Ok && summary.Text.Contains("536,080") && !summary.Text.Contains("536,180"));
        Check("an empty page at exactly _total is recognised as the safe end request",
            Text(good).Contains("exactly _total — returned an empty page") &&
            Text(good).Contains("after a full final page is safe"));
        Check("a rejection beyond _total is a harmless note, since the reader never goes there",
            Text(good).Contains("Note: offset 536,181, beyond _total, is rejected") &&
            Text(good).Contains("harmless"));
        Check("and shows the server's reason, not the generic advice wrapped around it",
            Text(good).Contains("offset must be in range"));
        Check("flat latency is reported as such", Text(good).Contains("Latency is flat"));

        // ---- a hard failure at depth ----
        var broken = DepthProbe.Analyze(new[]
        {
            S(0), S(10_000), S(100_000),
            S(250_000, ok: false, error: "400: Offset must be less than 100000")
        }, total, limit, true);

        Check("a failure at depth is a problem", HasProblem(broken));
        Check("and names where it broke", Text(broken).Contains("250,000"));
        Check("and the last offset that worked", Text(broken).Contains("100,000"));
        Check("and says a full export needs another strategy", Text(broken).Contains("another strategy"));

        var immediately = DepthProbe.Analyze(new[] { S(0, ok: false, error: "500") }, total, limit, true);
        Check("a failure with nothing good before it is still reported",
            Text(immediately).Contains("breaks at or before 0"));

        // ---- the quiet wrong answers ----
        var ignored = DepthProbe.Analyze(new[]
        {
            S(0), S(100_000, first: "id0", last: "id99", firstKey: 0, lastKey: 99)
        }, total, limit, true);

        Check("the same first item at two different offsets means the offset is ignored",
            Text(ignored).Contains("ignoring the offset"));

        var near = DepthProbe.Analyze(new[]
        {
            S(0), S(50, first: "id0", last: "id99", firstKey: 0, lastKey: 99)
        }, total, limit, true);
        Check("but overlapping pages 50 apart are not mistaken for it",
            !Text(near).Contains("ignoring the offset"));

        var empty = DepthProbe.Analyze(new[] { S(0), S(300_000, items: 0) }, total, limit, true);
        Check("an empty page mid-collection is a problem — the export would stop and call it done",
            HasProblem(empty) && Text(empty).Contains("NO items") && Text(empty).Contains("report success"));

        var shortMid = DepthProbe.Analyze(new[] { S(0), S(200_000, items: 60) }, total, limit, true);
        Check("a short page mid-collection is a problem",
            HasProblem(shortMid) && Text(shortMid).Contains("60 of 100"));
        Check("and the reason is the reader's end-of-collection rule", Text(shortMid).Contains("short page as the end"));

        // ---- ordering ----
        var unordered = DepthProbe.Analyze(new[] { S(0), S(10_000, ordered: false) }, total, limit, true);
        Check("rows out of order within a page are a problem",
            HasProblem(unordered) && Text(unordered).Contains("ascending order within the page"));

        var backwards = DepthProbe.Analyze(new[]
        {
            S(0, firstKey: 5000, lastKey: 5099), S(10_000, firstKey: 100, lastKey: 199)
        }, total, limit, true);
        Check("a deeper page that starts before a shallower one ended is a problem",
            HasProblem(backwards) && Text(backwards).Contains("not walking the sort order"));

        var ties = DepthProbe.Analyze(new[]
        {
            S(0, firstKey: 100, lastKey: 100), S(10_000, firstKey: 100, lastKey: 100)
        }, total, limit, true);
        Check("equal timestamps across pages are not an ordering problem",
            !Text(ties).Contains("not walking the sort order"));

        var descending = DepthProbe.Analyze(new[]
        {
            S(0, firstKey: 5000, lastKey: 4901), S(10_000, firstKey: 4900, lastKey: 4801)
        }, total, limit, ascending: false);
        Check("a descending sort is judged by its own direction",
            !Text(descending).Contains("not walking the sort order"));

        // ---- the end ----
        // Exactly _total is the request the reader makes after a full final page, so a failure
        // there is a real problem. Beyond it is somewhere the reader never goes.
        var atEndError = DepthProbe.Analyze(new[]
        {
            S(0), S(536_180, ok: false, error: "400: offset out of range")
        }, total, limit, true);
        Check("an error at exactly _total is a problem — it is the reader's own final request",
            HasProblem(atEndError) && Text(atEndError).Contains("exactly _total") &&
            Text(atEndError).Contains("exact multiple of the page size"));

        var atEndItems = DepthProbe.Analyze(new[] { S(0), S(536_180, items: 40) }, total, limit, true);
        Check("items at exactly _total mean the total is too low",
            HasProblem(atEndItems) && Text(atEndItems).Contains("exactly _total"));

        var beyondError = DepthProbe.Analyze(new[]
        {
            S(0), S(536_180, items: 0), S(537_180, ok: false, error: "400: offset out of range")
        }, total, limit, true);
        Check("an error beyond _total is only a note", !HasProblem(beyondError));

        var beyondItems = DepthProbe.Analyze(new[] { S(0), S(537_180, items: 40) }, total, limit, true);
        Check("items beyond _total are a problem",
            HasProblem(beyondItems) && Text(beyondItems).Contains("beyond _total, returned 40"));

        // ---- page size ----
        var ladderCapped = DepthProbe.Analyze(new[]
        {
            S(0), S(0, limit: 250), S(0, limit: 500, items: 500), S(0, limit: 1000, items: 500),
            S(0, limit: 2500, ok: false, error: "400: limit too large")
        }, total, limit, true);

        // The reader reads _total and adapts to a cap, so with a total a cap is a note. The
        // probe used to call it a problem that would stop an export after page one; that
        // stopped being true when the reader was fixed, and a stale alarm is worse than none.
        Check("a limit returned short is a note when _total lets the reader adapt",
            !HasProblem(ladderCapped) && Text(ladderCapped).Contains("limit 1,000 returned only 500"));
        Check("which says the reader copes, and what to set to avoid the wasted request",
            Text(ladderCapped).Contains("will not truncate an export") &&
            Text(ladderCapped).Contains("--page-size 500"));
        Check("and no longer claims an export would stop after page one",
            !Text(ladderCapped).Contains("after its first page"));

        var capNoTotal = DepthProbe.Analyze(new[]
        {
            S(0), S(0, limit: 500, items: 250)
        }, null, limit, true);
        Check("without _total a cap IS a problem, since a capped page looks like the last one",
            HasProblem(capNoTotal) && Text(capNoTotal).Contains("cannot tell a capped page") &&
            Text(capNoTotal).Contains("after its first page"));
        Check("a rejected limit is a note", Text(ladderCapped).Contains("limit 2,500 was rejected"));
        Check("a limit returned in full is OK",
            Text(ladderCapped).Contains("limit 250 returned in full"));
        Check("the best full page size is reported with its page count",
            Text(ladderCapped).Contains("largest page size returned in full was 500") &&
            Text(ladderCapped).Contains("1,073 pages"));
        Check("with the caveat that it was only tested at offset 0",
            Text(ladderCapped).Contains("only tested it at offset 0"));

        var smallCollection = DepthProbe.Analyze(new[] { S(0, items: 30), S(0, limit: 250, items: 30) },
            30, limit, true);
        Check("a collection smaller than the limit is not mistaken for a cap",
            !Text(smallCollection).Contains("caps the page size"));

        // ---- latency ----
        var slowing = DepthProbe.Analyze(new[]
        {
            S(0, ms: 200), S(100_000, ms: 500), S(500_000, ms: 1500)
        }, total, limit, true);
        Check("latency growing several-fold is a problem",
            HasProblem(slowing) && Text(slowing).Contains("Latency grows with depth") && Text(slowing).Contains("x7.5"));

        var mild = DepthProbe.Analyze(new[] { S(0, ms: 200), S(500_000, ms: 450) }, total, limit, true);
        Check("mild growth is not a problem", !HasProblem(mild));

        // The shape of the live run: steady reads, one slow one in the middle of the depths.
        var hiccup = DepthProbe.Analyze(new[]
        {
            S(0, ms: 1059), S(1_000, ms: 824), S(10_000, ms: 889), S(100_000, ms: 1188),
            S(400_000, ms: 1393), S(500_000, ms: 4459), S(536_080, ms: 1449)
        }, total, limit, true);
        Check("one slow read among steady ones is not read as growth", !HasProblem(hiccup));
        Check("but it is called out as a hiccup",
            Text(hiccup).Contains("offset 500,000 took 4,459 ms") && Text(hiccup).Contains("hiccup, not a trend"));

        // The same slow read landing at the greatest offset must not be read as growth either.
        var hiccupDeepest = DepthProbe.Analyze(new[]
        {
            S(0, ms: 1000), S(10_000, ms: 1000), S(100_000, ms: 1100), S(400_000, ms: 1000),
            S(536_080, ms: 5000)
        }, total, limit, true);
        Check("a slow read at the very deepest offset is not read as growth",
            !HasProblem(hiccupDeepest) && Text(hiccupDeepest).Contains("Latency is flat"));

        var realGrowth = DepthProbe.Analyze(new[]
        {
            S(0, ms: 1000), S(100_000, ms: 2500), S(400_000, ms: 4200), S(500_000, ms: 4800),
            S(536_080, ms: 5100)
        }, total, limit, true);
        Check("sustained growth across the deep reads still is",
            HasProblem(realGrowth) && Text(realGrowth).Contains("Latency grows with depth"));

        var retried = DepthProbe.Analyze(new[] { S(0), S(10_000, attempts: 2) }, total, limit, true);
        Check("a request that needed a retry is noted",
            Text(retried).Contains("needed 2 attempts"));

        Check("no _total just skips the checks that need it, rather than throwing",
            !HasProblem(DepthProbe.Analyze(new[] { S(0), S(10_000) }, null, limit, true)));

        // ---- projection ----
        // Latency 1000 ms at offset 0 rising to 2800 ms at 900, over ten pages of 100:
        // 1000 + 2*offset, summed over offsets 0,100,...,900 = 10,000 + 2*4,500 = 19,000 ms.
        var projected = DepthProbe.Project(new[] { S(0, ms: 1000), S(900, ms: 2800) }, 1000, 100, 0);
        Check("a projection is produced", projected is not null);
        Check("and interpolates latency between measured depths",
            projected is { } p && Math.Abs(p.Seconds - 19.0) < 0.01, projected?.Seconds.ToString());
        Eq("counting the requests", 10, projected?.Requests ?? -1);

        // At 1 request per second every request costs at least 1000 ms, so a fast 200 ms
        // read is paced up to 1000.
        var paced = DepthProbe.Project(new[] { S(0, ms: 200) }, 1000, 100, 1.0);
        Check("the rate limit sets a floor on each request's cost",
            paced is { } q && Math.Abs(q.Seconds - 10.0) < 0.01, paced?.Seconds.ToString());

        // Five reads at offsets 0..400 of a 500-row collection, one of them ten times slower.
        // Capped at twice the median (1000 ms) it costs 2000, not 10000: 4*1000 + 2000 = 6 s.
        var withOutlier = DepthProbe.Project(new[]
        {
            S(0, ms: 1000), S(100, ms: 1000), S(200, ms: 1000), S(300, ms: 10_000), S(400, ms: 1000)
        }, 500, 100, 0);
        Check("one slow read is capped so it does not skew the estimate",
            withOutlier is { } w && Math.Abs(w.Seconds - 6.0) < 0.01, withOutlier?.Seconds.ToString());

        var flat = DepthProbe.Project(new[] { S(0, ms: 300), S(100, ms: 500) }, 1000, 100, 0);
        Check("beyond the deepest measurement latency is held flat, not extrapolated",
            flat is { } r && Math.Abs(r.Seconds - (300 + 500 + 8 * 500) / 1000.0) < 0.01, flat?.Seconds.ToString());

        Check("no projection without a total", DepthProbe.Project(new[] { S(0) }, null, 100, 5) is null);
        Check("or without any successful read", DepthProbe.Project(Array.Empty<DepthSample>(), 1000, 100, 5) is null);
        Check("a failed read does not count as a measurement",
            DepthProbe.Project(new[] { S(0, ok: false, error: "x") }, 1000, 100, 5) is null);

        Eq("seconds read as seconds", "45 seconds", DepthProbe.FormatDuration(45));
        Eq("minutes read as minutes", "2.5 minutes", DepthProbe.FormatDuration(150));
        Eq("hours read as hours", "1.5 hours", DepthProbe.FormatDuration(5400));

        // ---- choosing and parsing offsets ----
        var chosen = DepthProbe.ChooseDepths(536_180, 100, null);
        Check("the default depths start at the front", chosen[0] == 0);
        Check("include a deep one", chosen.Contains(500_000));
        Check("include the last full page, where an off-by-one would show", chosen.Contains(536_080));
        Check("include exactly _total, which is what the reader asks for after a full last page",
            chosen.Contains(536_180));
        Check("and one beyond it, to learn whether the end is inclusive", chosen.Contains(536_181));
        Check("are sorted without repeats", chosen.SequenceEqual(chosen.Distinct().OrderBy(x => x)));

        var small = DepthProbe.ChooseDepths(250, 100, null);
        Check("a small collection skips depths it cannot reach", !small.Contains(1_000) && !small.Contains(10_000));
        Check("but always reads offset 0", small.Contains(0));
        Check("and its last full page", small.Contains(150));

        var tiny = DepthProbe.ChooseDepths(30, 100, null);
        Check("a collection smaller than a page still gets a valid depth list", tiny.Contains(0));

        Eq("requested depths replace the defaults", "0,5000,400000",
            string.Join(",", DepthProbe.ChooseDepths(536_180, 100, new long[] { 400_000, 5_000 })));
        Check("offset 0 is always included", DepthProbe.ChooseDepths(null, 100, new long[] { 7 }).Contains(0));

        var (valid, rejected) = DepthProbe.ParseDepths("0, 50_000;400000 400000 abc -5 1.5 99999999999");
        Eq("offsets parse, with underscores", "0,50000,400000", string.Join(",", valid));
        Check("a repeat is listed once", valid.Count(v => v == 400_000) == 1);
        Check("text is rejected", rejected.Contains("abc"));
        Check("a negative is rejected", rejected.Contains("-5"));
        Check("a decimal is rejected", rejected.Contains("1.5"));
        Check("an offset beyond what the API can take is rejected", rejected.Contains("99999999999"));

        Check("--probe-depth parses", CommandLineArgs.Parse(new[] { "--probe-depth" }).ProbeDepth);
        Eq("--depths parses", "0,100000", CommandLineArgs.Parse(new[] { "--depths", "0,100000" }).Depths);
        Throws<ArgumentException>("--depths needs a value", () => CommandLineArgs.Parse(new[] { "--depths" }),
            "requires a value");
        Check("the usage text describes it",
            CommandLineArgs.Usage.Contains("--probe-depth") && CommandLineArgs.Usage.Contains("--depths"));
    }

    // ──────────────────── D7. the reader against a simulated server ────────────────────

    /// <summary>
    /// A fake PM API behaving as the live tenant did when the depth probe ran against it:
    /// <c>offset</c> inclusive of the total (so <c>offset == total</c> is an empty 200 and
    /// anything beyond is a 400), and a page-size cap applied <b>silently</b> — ask for 1,000,
    /// get 250, no error, a healthy status.
    /// </summary>
    private sealed class SimulatedServer : HttpMessageHandler
    {
        /// <summary>Rows that really exist.</summary>
        public int Rows;

        /// <summary>What <c>_total</c> claims, when it differs from <see cref="Rows"/>.</summary>
        public int? ReportedTotal;

        public bool OmitTotal;
        public int Cap = int.MaxValue;
        public readonly List<(int Offset, int Limit)> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal))
                return Task.FromResult(Reply(System.Net.HttpStatusCode.OK,
                    """{"access_token":"t","expires_in":600}"""));

            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

            var offset = int.Parse(query["offset"]);
            var limit = int.Parse(query["limit"]);
            Requests.Add((offset, limit));

            if (offset < 0 || offset > Rows)
                return Task.FromResult(Reply(System.Net.HttpStatusCode.BadRequest,
                    $"{{\"errors\":[{{\"message\":\"offset must be in range '0' ... '{Rows}'\"}}]}}"));

            var count = Math.Min(Math.Min(limit, Cap), Rows - offset);

            var items = string.Join(",", Enumerable.Range(offset, count).Select(i =>
                $"{{\"projectId\":{i + 1},\"modifiedDate\":\"2020-01-01T00:00:00Z\"}}"));

            var total = OmitTotal ? "" : $"\"_total\":{ReportedTotal ?? Rows},";

            return Task.FromResult(Reply(System.Net.HttpStatusCode.OK,
                $"{{{total}\"_embedded\":{{\"Project\":[{items}]}},\"_links\":{{}}}}"));
        }

        private static HttpResponseMessage Reply(System.Net.HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Reads a whole collection through the production reader, as an export does.</summary>
    private static async Task<(List<string> Ids, List<string> Log, HalPagedReader Reader)> ReadEverything(
        SimulatedServer server, int pageSize)
    {
        var config = new ExportConfig();
        config.Aprimo.Tenant = "test";
        config.Aprimo.ClientId = "id";
        config.Aprimo.ClientSecret = "secret";
        config.Source.Paging.PageSize = pageSize;

        var log = new List<string>();

        using var http = new HttpClient(server);
        using var tokens = new OAuthTokenProvider(http, config.Aprimo, config.Source.Retry, _ => { });
        using var limiter = new RateLimiter(0, 1, 1);

        var client = new ApiClient(http, tokens, limiter, config, _ => { });
        var reader = new HalPagedReader(client, config, ProjectsObject(), log.Add);

        var ids = new List<string>();
        var guard = 0;

        // The guard turns a reader that never finishes into a failed assertion, not a hung suite.
        while (reader.HasMore && guard++ < 1_000)
        {
            using var page = await reader.NextPageAsync(int.MaxValue, default);
            if (page is null) break;

            foreach (var item in page.Items)
                ids.Add(item.GetProperty("projectId").GetRawText());
        }

        return (ids, log, reader);
    }

    /// <summary>
    /// How the reader decides a collection has ended — the part the depth probe's findings
    /// bear on, and until now the part with no offline test at all.
    ///
    /// <para>The live tenant caps <c>limit</c> at 250 without saying so. A reader that treats
    /// any short page as the end would stop an export after its first page, reporting
    /// success, whenever <c>--page-size</c> exceeded that. These tests run the real reader,
    /// API client and token provider against a fake server that behaves the same way.</para>
    /// </summary>
    private static async Task PagingAgainstSimulatedServer()
    {
        Section("Paging against a simulated server");

        // ---- an ordinary read, and the end of the collection ----
        // 1,000 rows at 100 a page: ten full pages, then a request at offset 1,000 — exactly
        // _total. The live tenant treats that as valid and returns an empty page.
        var exact = new SimulatedServer { Rows = 1_000 };
        var (exactIds, exactLog, _) = await ReadEverything(exact, 100);

        Eq("an exact multiple of the page size reads every row", 1_000, exactIds.Count);
        Eq("each exactly once", 1_000, exactIds.Distinct().Count());
        Eq("with one extra request past the last full page", 11, exact.Requests.Count);
        Eq("which asks for offset == _total, the boundary the live tenant allows", 1_000,
            exact.Requests[^1].Offset);
        Check("and nothing is reported as a cap", !exactLog.Any(l => l.Contains("caps the page size")));

        var uneven = new SimulatedServer { Rows = 1_050 };
        var (unevenIds, _, _) = await ReadEverything(uneven, 100);
        Eq("a collection that is not a multiple of the page size reads every row", 1_050, unevenIds.Count);
        Eq("ending on the short page, with no further request", 11, uneven.Requests.Count);

        var empty = new SimulatedServer { Rows = 0 };
        var (emptyIds, _, _) = await ReadEverything(empty, 100);
        Eq("an empty collection reads nothing and does not loop", 0, emptyIds.Count);
        Eq("after a single request", 1, empty.Requests.Count);

        // ---- the silent cap ----
        // Ask for 1,000, get 250 with a 200. This is what truncated exports before the fix.
        var capped = new SimulatedServer { Rows = 1_000, Cap = 250 };
        var (cappedIds, cappedLog, cappedReader) = await ReadEverything(capped, 1_000);

        Eq("a server that caps the page size does not truncate the export", 1_000, cappedIds.Count);
        Eq("every row exactly once", 1_000, cappedIds.Distinct().Count());
        Check("the cap is announced, once",
            cappedLog.Count(l => l.Contains("caps the page size at 250")) == 1);
        Eq("and remembered", 250, cappedReader.LearnedPageSizeCap ?? -1);
        Eq("the first request asked for what the user configured", 1_000, capped.Requests[0].Limit);
        Check("every later request asks for what the server will give",
            capped.Requests.Skip(1).All(r => r.Limit == 250));
        Check("and the log suggests the page size that avoids it",
            cappedLog.Any(l => l.Contains("--page-size 250")));

        var cappedUneven = new SimulatedServer { Rows = 1_100, Cap = 250 };
        var (cappedUnevenIds, _, _) = await ReadEverything(cappedUneven, 500);
        Eq("a capped read still ends correctly on a short final page", 1_100, cappedUnevenIds.Count);
        Eq("without a request past it", 5, cappedUneven.Requests.Count);

        var uncapped = new SimulatedServer { Rows = 500 };
        var (_, _, uncappedReader) = await ReadEverything(uncapped, 100);
        Check("a server with no cap leaves none learned", uncappedReader.LearnedPageSizeCap is null);

        // A page size equal to the cap is the case --page-size 250 recommends: no warning.
        var matched = new SimulatedServer { Rows = 1_000, Cap = 250 };
        var (matchedIds, matchedLog, _) = await ReadEverything(matched, 250);
        Eq("a page size equal to the cap reads everything", 1_000, matchedIds.Count);
        Check("with no warning", !matchedLog.Any(l => l.Contains("caps the page size")));

        // ---- a total that is wrong ----
        var staleHigh = new SimulatedServer { Rows = 1_000, ReportedTotal = 1_100 };
        var (staleIds, _, _) = await ReadEverything(staleHigh, 100);
        Eq("a _total that overstates the collection still ends, on the empty page", 1_000, staleIds.Count);

        // The short last page is mistaken for a cap here, since _total says 70 more remain.
        // That costs one extra request and a misleading log line, but never a row.
        var staleShort = new SimulatedServer { Rows = 1_030, ReportedTotal = 1_100 };
        var (staleShortIds, _, _) = await ReadEverything(staleShort, 100);
        Eq("an overstated _total with a short last page loses no rows", 1_030, staleShortIds.Count);
        Eq("and each is read once", 1_030, staleShortIds.Distinct().Count());
        Eq("ending on an empty page rather than looping", 12, staleShort.Requests.Count);

        // ---- no total at all ----
        var noTotal = new SimulatedServer { Rows = 1_050, OmitTotal = true };
        var (noTotalIds, _, _) = await ReadEverything(noTotal, 100);
        Eq("with no _total, a short page ends the read as it always did", 1_050, noTotalIds.Count);

        // The one case the cap detection cannot see: with nothing saying more rows remain, a
        // capped short page is indistinguishable from the last one. Pinned so the limit is on
        // the record — the live tenant always reports _total, so it does not arise there.
        var noTotalCapped = new SimulatedServer { Rows = 1_000, Cap = 250, OmitTotal = true };
        var (noTotalCappedIds, _, _) = await ReadEverything(noTotalCapped, 1_000);
        Eq("with no _total AND a cap, the read ends early (a known limit)", 250, noTotalCappedIds.Count);

        // ---- the server's rules, as the probe found them ----
        var beyond = new SimulatedServer { Rows = 1_000 };
        var config = new ExportConfig();
        config.Aprimo.Tenant = "test";
        config.Aprimo.ClientId = "id";
        config.Aprimo.ClientSecret = "secret";

        using var http = new HttpClient(beyond);
        using var tokens = new OAuthTokenProvider(http, config.Aprimo, config.Source.Retry, _ => { });
        using var limiter = new RateLimiter(0, 1, 1);
        var client = new ApiClient(http, tokens, limiter, config, _ => { });

        config.Source.Paging.MaxOffset = 0;
        var atTotal = new HalPagedReader(client, config, ProjectsObject(), _ => { },
            new PagingState { NextOffset = 1_000 });
        using (var page = await atTotal.NextPageAsync(100, default))
            Check("offset == _total is an empty page, not an error", page is not null && page.Items.Count == 0);

        var pastTotal = new HalPagedReader(client, config, ProjectsObject(), _ => { },
            new PagingState { NextOffset = 1_001 });
        var rejected = false;
        try { (await pastTotal.NextPageAsync(100, default))?.Dispose(); }
        catch (ApiException ex) { rejected = ex.Message.Contains("offset must be in range"); }
        Check("offset beyond _total is rejected, with the server's reason", rejected);
    }

    // ──────────────────── D8. the registry and starter lists against the spec ────────────────────

    /// <summary>Walks up from the test binary looking for a file, so the suite works wherever the repo sits.</summary>
    private static string? FindUp(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private const string SpecFileName = "Productivity_and_Spend_OpenAPI_Specification.txt";

    private static string RefName(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$ref", out var r)
            ? r.GetString()!.Split('/')[^1]
            : "";

    private static bool TrySchemaProperties(JsonElement schemas, string schema, out JsonElement properties)
    {
        properties = default;
        return !string.IsNullOrEmpty(schema) &&
               schemas.TryGetProperty(schema, out var s) &&
               s.TryGetProperty("properties", out properties);
    }

    /// <summary>
    /// Every object in the registry, and every starter column list, checked against the
    /// OpenAPI document they were written from.
    ///
    /// <para>Only <c>projects</c> has been run against a live tenant, so for the other 26 objects
    /// the registry is inferred from the spec — which makes a typo there invisible until someone
    /// exports that object. This is what makes it visible. It found real faults in the starter
    /// lists when first written: an <c>outlook</c> column reading properties of what is an
    /// integer enum, and a predecessor column reading <c>taskId</c> (the task's own ID) where
    /// <c>predTaskId</c> was meant, which would have exported confidently wrong data.</para>
    ///
    /// <para>It checks what the spec says exists. It cannot say how a live API behaves.</para>
    /// </summary>
    private static void SpecConsistency()
    {
        Section("Registry and starter lists against the spec");

        var specPath = FindUp(SpecFileName);
        if (specPath is null)
        {
            Console.WriteLine("   skipped — the OpenAPI document is not above the test binary.");
            return;
        }

        using var spec = JsonDocument.Parse(File.ReadAllText(specPath));
        var paths = spec.RootElement.GetProperty("paths");
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");

        var catalog = ObjectCatalog.Load(Path.Combine(AppContext.BaseDirectory, "objects.json"), Noop);

        foreach (var o in catalog.Objects)
        {
            var problems = new List<string>();

            // The endpoint exists, pages, and returns the schema the registry says it does.
            if (!paths.TryGetProperty(o.Path, out var pathItem) || !pathItem.TryGetProperty("get", out var get))
            {
                problems.Add($"GET {o.Path} is not in the spec");
            }
            else
            {
                var parameters = get.TryGetProperty("parameters", out var ps)
                    ? ps.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToHashSet()
                    : new HashSet<string?>();

                if (!parameters.Contains("offset") || !parameters.Contains("limit"))
                    problems.Add("the path does not take offset and limit");

                if (o.ModifiedAfterParam is { Length: > 0 } after && !parameters.Contains(after))
                    problems.Add($"ModifiedAfterParam '{after}' is not a parameter of the path");

                var items = get.GetProperty("responses").GetProperty("200").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema")
                    .GetProperty("properties").GetProperty("_embedded").GetProperty("additionalProperties")
                    .GetProperty("items");

                if (RefName(items) != o.Schema)
                    problems.Add($"the response holds '{RefName(items)}' but the registry says '{o.Schema}'");
            }

            // The properties the registry names are real properties of that schema.
            if (!TrySchemaProperties(schemas, o.Schema ?? "", out var properties))
            {
                problems.Add($"schema '{o.Schema}' is not in the spec");
            }
            else
            {
                foreach (var (label, name) in new[]
                         {
                             ("IdProperty", o.IdProperty), ("ModifiedProperty", o.ModifiedProperty),
                             ("CreatedProperty", o.CreatedProperty), ("TitleProperty", o.TitleProperty)
                         })
                    if (!string.IsNullOrEmpty(name) && !properties.TryGetProperty(name, out _))
                        problems.Add($"{label} '{name}' is not a property of {o.Schema}");

                if (o.SupportsExtendedAttributes != properties.TryGetProperty("extendedAttributes", out _))
                    problems.Add("SupportsExtendedAttributes disagrees with whether the schema has extendedAttributes");
            }

            // A single-item endpoint, when one is named, has a GET in the spec. Two resources
            // split their operations across paths differing only in the case of the parameter.
            if (o.DetailPath is { Length: > 0 } detail)
            {
                var hasDetail = new[] { detail, detail.Replace("{id}", "{Id}") }
                    .Any(p => paths.TryGetProperty(p, out var item) && item.TryGetProperty("get", out _));

                if (!hasDetail) problems.Add($"DetailPath {detail} has no GET in the spec");
            }

            Check($"'{o.Name}' agrees with the spec", problems.Count == 0, string.Join("; ", problems));
        }

        // ---- the starter column lists ----
        var fieldsDirectory = Path.Combine(AppContext.BaseDirectory, "fields");

        foreach (var o in catalog.Objects)
        {
            var file = Path.Combine(fieldsDirectory, o.FieldsFileName);
            if (!File.Exists(file) || !TrySchemaProperties(schemas, o.Schema ?? "", out var properties)) continue;

            var problems = new List<string>();

            foreach (var mapping in FieldListLoader.Load(file, o, Noop))
            {
                var compiled = CompiledPath.Compile(mapping.Path);

                foreach (var branch in compiled.Branches)
                {
                    if (branch.Count == 0 || branch[0] is not PropStep first) continue;

                    // Tenant-defined attributes are not in any schema.
                    if (first.Name is "extendedAttributes" or "multipleValueExtendedAttributes") continue;

                    if (!properties.TryGetProperty(first.Name, out var propertySchema))
                    {
                        problems.Add($"{mapping.Column}: '{first.Name}' is not a property of {o.Schema}");
                        continue;
                    }

                    // One level down, where the property is an object or an array of them.
                    var nested = RefName(propertySchema);
                    if (nested.Length == 0 && propertySchema.TryGetProperty("items", out var itemSchema))
                        nested = RefName(itemSchema);

                    if (branch.Skip(1).FirstOrDefault(s => s is PropStep) is PropStep second && nested.Length > 0 &&
                        (!TrySchemaProperties(schemas, nested, out var nestedProperties) ||
                         !nestedProperties.TryGetProperty(second.Name, out _)))
                        problems.Add($"{mapping.Column}: '{second.Name}' is not a property of {nested}");
                }
            }

            Check($"every path in the starter list for '{o.Name}' resolves in the spec",
                problems.Count == 0, string.Join("; ", problems));
        }
    }

    // ──────────────────── D9. the whole pipeline, for every object ────────────────────

    /// <summary>
    /// A fake PM API serving schema-shaped data for every collection in the registry, plus the
    /// <c>/users/{id}</c> endpoint. Metadata and lookup endpoints answer 404, as an unlicensed or
    /// unconfigured one would.
    /// </summary>
    private sealed class SpecServer : HttpMessageHandler
    {
        public readonly Dictionary<string, (string EmbedKey, List<string> Items)> Collections = new();
        public readonly List<string> Unserved = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal))
                return Task.FromResult(Reply(System.Net.HttpStatusCode.OK,
                    """{"access_token":"t","expires_in":600}"""));

            // The API base is https://test.aprimo.com/api, so strip that to get the route.
            var route = uri.AbsolutePath.StartsWith("/api", StringComparison.Ordinal)
                ? uri.AbsolutePath.Substring(4)
                : uri.AbsolutePath;

            if (route.StartsWith("/users/", StringComparison.Ordinal))
                return Task.FromResult(Reply(System.Net.HttpStatusCode.OK,
                    """{"firstName":"Pat","lastName":"Example","email":"pat@example.invalid"}"""));

            if (!Collections.TryGetValue(route, out var collection))
            {
                Unserved.Add(route);
                return Task.FromResult(Reply(System.Net.HttpStatusCode.NotFound, "{}"));
            }

            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

            var offset = int.Parse(query["offset"]);
            var limit = int.Parse(query["limit"]);

            if (offset < 0 || offset > collection.Items.Count)
                return Task.FromResult(Reply(System.Net.HttpStatusCode.BadRequest,
                    "{\"errors\":[{\"message\":\"offset out of range\"}]}"));

            var slice = collection.Items.Skip(offset).Take(limit);

            return Task.FromResult(Reply(System.Net.HttpStatusCode.OK,
                $"{{\"_total\":{collection.Items.Count},\"_embedded\":{{\"{collection.EmbedKey}\":" +
                $"[{string.Join(",", slice)}]}},\"_links\":{{}}}}"));
        }

        private static HttpResponseMessage Reply(System.Net.HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Builds a value that conforms to a schema, populating every property, one element per array.
    /// Depth-limited and cycle-guarded, since several schemas refer back to themselves.
    /// </summary>
    private static JsonNode? Generate(JsonElement schemas, JsonElement schema, int depth, HashSet<string> active)
    {
        var reference = RefName(schema);

        if (reference.Length > 0)
        {
            if (depth > 4 || active.Contains(reference) || !schemas.TryGetProperty(reference, out var target))
                return null;

            active.Add(reference);
            try { return Generate(schemas, target, depth + 1, active); }
            finally { active.Remove(reference); }
        }

        if (schema.TryGetProperty("enum", out var members) && members.GetArrayLength() > 0)
            return JsonNode.Parse(members[0].GetRawText());

        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "object":
                var result = new JsonObject();
                if (schema.TryGetProperty("properties", out var props))
                    foreach (var property in props.EnumerateObject())
                        if (Generate(schemas, property.Value, depth, active) is { } value)
                            result[property.Name] = value;
                return result;

            case "array":
                var array = new JsonArray();
                if (schema.TryGetProperty("items", out var items) &&
                    Generate(schemas, items, depth, active) is { } element)
                    array.Add(element);
                return array;

            case "integer": return JsonValue.Create(7);
            case "number": return JsonValue.Create(1.5);
            case "boolean": return JsonValue.Create(false);

            default:
                // A string — or a property the spec leaves untyped, which PM does a lot.
                return JsonValue.Create(
                    schema.TryGetProperty("format", out var format) && format.GetString() == "date-time"
                        ? "2026-01-02T03:04:05Z"
                        : "text");
        }
    }

    /// <summary>
    /// Runs the real export — registry, column lists, reader, extractor, user resolution,
    /// CSV writer — over every object in the registry, against data generated from the spec.
    ///
    /// <para>What this establishes: the machinery works for each object's shape, and the
    /// shipped starter lists produce a value in every column on a schema-conforming item. What
    /// it cannot: that a live tenant returns what the spec says. Only <c>projects</c> has been
    /// read from one.</para>
    /// </summary>
    private static async Task PipelineForEveryObject(string scratch)
    {
        Section("Full pipeline, every object, against schema-shaped data");

        var specPath = FindUp(SpecFileName);
        if (specPath is null)
        {
            Console.WriteLine("   skipped — the OpenAPI document is not above the test binary.");
            return;
        }

        using var spec = JsonDocument.Parse(File.ReadAllText(specPath));
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");

        var catalog = ObjectCatalog.Load(Path.Combine(AppContext.BaseDirectory, "objects.json"), Noop);

        // 23 rows at 10 a page: two full pages and a short one, so the end of each collection is
        // met the way it is in a real export rather than landing on a page boundary.
        const int rows = 23;
        var server = new SpecServer();

        foreach (var o in catalog.Objects)
        {
            var items = new List<string>(rows);

            for (var i = 0; i < rows; i++)
            {
                var item = (JsonObject)Generate(schemas, schemas.GetProperty(o.Schema!), 0, new HashSet<string>())!;

                // Unique IDs, in whichever type the schema gives the ID property.
                var idIsText = schemas.GetProperty(o.Schema!).GetProperty("properties")
                    .TryGetProperty(o.IdProperty, out var idSchema) &&
                    idSchema.TryGetProperty("type", out var idType) && idType.GetString() == "string";

                item[o.IdProperty] = idIsText ? JsonValue.Create($"id-{i + 1}") : JsonValue.Create(i + 1);

                // Every integer above is 7, so a column reading the wrong one of two sibling
                // properties would still show 7 and pass. Give the pairs that are easy to
                // confuse distinct values — a task's own ID beside its predecessor's, an
                // assignment's ID beside the user's, a role link's ID beside the role's.
                if (o.Name == "tasks")
                {
                    if (item["taskPredecessors"] is JsonArray { Count: > 0 } predecessors)
                        predecessors[0] = new JsonObject { ["taskId"] = i + 1, ["predTaskId"] = 99 };
                    if (item["assignees"] is JsonArray { Count: > 0 } assignees)
                        assignees[0] = new JsonObject { ["assigneeId"] = 31, ["taskId"] = i + 1, ["userId"] = 32 };
                    if (item["taskRoles"] is JsonArray { Count: > 0 } roles)
                        roles[0] = new JsonObject { ["taskRoleId"] = 41, ["taskId"] = i + 1, ["roleId"] = 42 };
                }

                items.Add(item.ToJsonString());
            }

            // The embedded key is the schema name, as it was on the one tenant observed.
            server.Collections[o.Path] = (o.Schema!, items);
        }

        var config = new ExportConfig();
        config.Aprimo.Tenant = "test";
        config.Aprimo.ClientId = "id";
        config.Aprimo.ClientSecret = "secret";
        config.Source.Paging.PageSize = 10;
        config.Throttle.RequestsPerSecond = 0;
        config.Limits.EnableCheckpoint = false;
        config.Output.Directory = Path.Combine(scratch, "pipeline");
        config.Output.Overwrite = true;
        config.FieldsDirectory = Path.Combine(AppContext.BaseDirectory, "fields");

        using var http = new HttpClient(server);
        using var tokens = new OAuthTokenProvider(http, config.Aprimo, config.Source.Retry, _ => { });
        using var limiter = new RateLimiter(0, 1, 1);

        var client = new ApiClient(http, tokens, limiter, config, _ => { });
        var metadata = new MetadataCatalog(client, _ => { });
        var runner = new MultiObjectRunner(config, client, metadata, null, null, _ => { });

        var result = await runner.RunAsync(catalog.Objects, null, null, false, default);

        Eq("every object in the registry was attempted", catalog.Objects.Count, result.Objects.Count);

        foreach (var o in result.Objects)
        {
            Check($"'{o.Object}' exports successfully", o.Outcome == ObjectOutcome.Succeeded, o.Message);
            Eq($"'{o.Object}' writes every row, across the short last page", rows, o.RowsWritten);
        }

        // ---- the shipped starter lists: every column must resolve on a conforming item ----
        foreach (var o in catalog.Objects)
        {
            var list = Path.Combine(config.FieldsDirectory, o.FieldsFileName);
            var csvPath = Path.Combine(config.Output.Directory, $"aprimo-pm-{o.Name}_0001.csv");

            if (!File.Exists(list) || !File.Exists(csvPath)) continue;

            var csv = ReadCsv(csvPath);
            var header = csv[0];
            var firstRow = csv[1];

            var empty = header.Where((_, i) => string.IsNullOrEmpty(firstRow[i])).ToList();

            Eq($"'{o.Name}': the CSV has one column per entry in its starter list",
                FieldListLoader.Load(list, o, Noop).Count, header.Length);

            // A column left empty on an item that has every property populated is a path that
            // reads something the schema does not contain.
            Check($"'{o.Name}': every column in the shipped starter list gets a value",
                empty.Count == 0, "empty: " + string.Join(", ", empty));
        }

        // ---- objects with no list of their own use the shared one, and must still carry their ID ----
        // This was the fault the pipeline test found: the shared list names no ID, so for 24 of
        // the 27 objects a bare export produced a CSV with nothing to tell one record from another.
        foreach (var o in catalog.Objects.Where(o =>
                     !File.Exists(Path.Combine(config.FieldsDirectory, o.FieldsFileName))))
        {
            var csvPath = Path.Combine(config.Output.Directory, $"aprimo-pm-{o.Name}_0001.csv");
            if (!File.Exists(csvPath)) continue;

            var csv = ReadCsv(csvPath);

            Eq($"'{o.Name}': the first column is the ID", "Id", csv[0][0]);
            Check($"'{o.Name}': it holds a value on every row", csv.Skip(1).All(r => !string.IsNullOrEmpty(r[0])));
            Eq($"'{o.Name}': and the values are distinct", rows, csv.Skip(1).Select(r => r[0]).Distinct().Count());
            Check($"'{o.Name}': the shared columns follow it", csv[0].Contains("ModifiedDate"));
        }

        // ---- specifics worth stating for the two objects asked about ----
        var tasks = ReadCsv(Path.Combine(config.Output.Directory, "aprimo-pm-tasks_0001.csv"));
        var taskRow = tasks[1];

        Eq("a task's predecessor column reads the predecessor's ID (99), not the task's own (1)", "99",
            taskRow[Array.IndexOf(tasks[0], "PredecessorTaskIds")]);
        Eq("the assignee column reads the user's ID (32), not the assignment's (31)", "32",
            taskRow[Array.IndexOf(tasks[0], "AssigneeUserIds")]);
        Eq("and the role column reads the role's ID (42), not the link's (41)", "42",
            taskRow[Array.IndexOf(tasks[0], "RoleIds")]);

        var activities = ReadCsv(Path.Combine(config.Output.Directory, "aprimo-pm-activities_0001.csv"));
        Check("an activity's money, date and membership columns come through",
            activities[1][Array.IndexOf(activities[0], "TotalEstCost")] == "1.5" &&
            activities[1][Array.IndexOf(activities[0], "BeginDate")].StartsWith("2026-01-02", StringComparison.Ordinal) &&
            activities[1][Array.IndexOf(activities[0], "ProgramIds")] == "7");

        // ---- nothing was requested that the server did not have ----
        Check("only metadata and lookup lookups went unserved, never a collection or a page",
            server.Unserved.All(r => r.StartsWith("/metadata/", StringComparison.Ordinal) ||
                                     r.StartsWith("/lookup/", StringComparison.Ordinal) ||
                                     r.StartsWith("/extended-attributes/", StringComparison.Ordinal)),
            string.Join(", ", server.Unserved.Distinct().Take(5)));
    }

    /// <summary>Builds a resolver from literal tables, bypassing the HTTP fetch.</summary>
    private static LookupResolver TestResolver(Dictionary<string, Dictionary<string, string>> tables)
    {
        // LookupResolver has no public constructor by design — it is built from live
        // metadata. Reflection keeps that surface closed while still allowing the
        // substitution logic to be tested without a tenant.
        var byKey = tables.ToDictionary(
            kvp => kvp.Key,
            kvp => new LookupTable("test", new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        var constructor = typeof(LookupResolver).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0];

        return (LookupResolver)constructor.Invoke(new object[] { byKey, Array.Empty<string>() });
    }

    // ───────────────────────────── E. HAL envelopes ─────────────────────────────

    private static void HalEnvelopes()
    {
        Section("HAL envelopes");

        // The documented shape. The _embedded key is deliberately unknown to the reader.
        using var normal = Json("""
        {
          "_total": 1234,
          "_embedded": { "projects": [ {"projectId": 1}, {"projectId": 2} ] },
          "_links": { "self": {"href": "/projects?offset=0"},
                      "next": {"href": "/projects?offset=2&limit=2"} }
        }
        """);

        var envelope = HalEnvelopeReader.Read(normal.RootElement, null);

        Check("a documented envelope reads OK", envelope.Status == HalEnvelopeStatus.Ok);
        Eq("the sole embedded array is found without knowing its key", "projects", envelope.EmbedKey);
        Eq("the item count is right", 2, envelope.Count);
        Eq("_total is read", 1234, envelope.Total ?? -1);
        Eq("_links.next.href is read", "/projects?offset=2&limit=2", envelope.NextLink);

        // A configured EmbedKey switches on a strict lookup.
        var pinned = HalEnvelopeReader.Read(normal.RootElement, "projects");
        Check("a matching EmbedKey resolves", pinned.Status == HalEnvelopeStatus.Ok);
        var pinnedCase = HalEnvelopeReader.Read(normal.RootElement, "PROJECTS");
        Check("EmbedKey matching is case-insensitive", pinnedCase.Status == HalEnvelopeStatus.Ok);
        // A wrong EmbedKey against a single-array envelope: there is no ambiguity for the
        // key to resolve, so read the array anyway rather than letting one stale registry
        // entry kill an object the key-less path would have handled.
        var pinnedWrong = HalEnvelopeReader.Read(normal.RootElement, "widgets");
        Check("a wrong EmbedKey still reads the sole array", pinnedWrong.Status == HalEnvelopeStatus.Ok);
        Check("and flags the mismatch so it can be corrected", pinnedWrong.EmbedKeyMismatch);
        Eq("reporting the key actually used", "projects", pinnedWrong.EmbedKey);
        Check("the available keys are reported alongside", pinnedWrong.AvailableKeys.Contains("projects"));
        Check("a correct EmbedKey is not flagged as a mismatch", !pinned.EmbedKeyMismatch);
        Check("an absent EmbedKey is not flagged as a mismatch",
            !HalEnvelopeReader.Read(normal.RootElement, null).EmbedKeyMismatch);

        using var noNext = Json("""{ "_total": 2, "_embedded": { "x": [ {} ] }, "_links": { "self": {} } }""");
        Check("a missing next link is null", HalEnvelopeReader.Read(noNext.RootElement, null).NextLink is null);

        using var emptyHref = Json("""{ "_embedded": { "x": [] }, "_links": { "next": { "href": "" } } }""");
        Check("an empty next href is treated as absent",
            HalEnvelopeReader.Read(emptyHref.RootElement, null).NextLink is null);

        // Empty collections. Both shapes appear in practice and neither is an error.
        using var emptyArray = Json("""{ "_total": 0, "_embedded": { "projects": [] }, "_links": {} }""");
        var emptyResult = HalEnvelopeReader.Read(emptyArray.RootElement, null);
        Check("an empty array is OK, not an error", emptyResult.Status == HalEnvelopeStatus.Ok);
        Eq("an empty array has no items", 0, emptyResult.Count);

        using var noEmbedded = Json("""{ "_total": 0, "_links": {} }""");
        Check("_total 0 with no _embedded is Empty, not a failure",
            HalEnvelopeReader.Read(noEmbedded.RootElement, null).Status == HalEnvelopeStatus.Empty);

        using var bare = Json("{}");
        Check("an empty object is Empty",
            HalEnvelopeReader.Read(bare.RootElement, null).Status == HalEnvelopeStatus.Empty);

        using var embeddedNoArrays = Json("""{ "_total": 5, "_embedded": { "meta": {"a":1} } }""");
        Check("_embedded with no arrays is Empty",
            HalEnvelopeReader.Read(embeddedNoArrays.RootElement, null).Status == HalEnvelopeStatus.Empty);

        // Shapes that mean "this is not the envelope we were promised".
        using var wrongShape = Json("""{ "_total": 99, "items": [ {"id": 1} ] }""");
        var wrong = HalEnvelopeReader.Read(wrongShape.RootElement, null);
        Check("content with no _embedded is NotAnEnvelope", wrong.Status == HalEnvelopeStatus.NotAnEnvelope);
        Check("the received keys are reported", wrong.AvailableKeys.Contains("items"));

        using var array = Json("""[ {"id": 1} ]""");
        Check("a bare array is NotAnEnvelope",
            HalEnvelopeReader.Read(array.RootElement, null).Status == HalEnvelopeStatus.NotAnEnvelope);

        using var multi = Json("""
        { "_embedded": { "projects": [ {"projectId": 1} ], "warnings": [ {"code": 7} ] } }
        """);
        var multiResult = HalEnvelopeReader.Read(multi.RootElement, null);
        Check("with several arrays the first is used", multiResult.Status == HalEnvelopeStatus.Ok);
        Eq("and all keys are reported so the ambiguity can be surfaced", 2, multiResult.AvailableKeys.Count);
        Eq("a pinned key wins over ordering", "warnings",
            HalEnvelopeReader.Read(multi.RootElement, "warnings").EmbedKey);

        // With several arrays the key is the only thing distinguishing them, so a miss
        // is genuinely unresolvable and must stop rather than guess.
        var multiWrong = HalEnvelopeReader.Read(multi.RootElement, "widgets");
        Check("a wrong EmbedKey against several arrays is fatal",
            multiWrong.Status == HalEnvelopeStatus.EmbedKeyNotFound);
        Eq("and every candidate is named in the report", 2, multiWrong.AvailableKeys.Count);

        Eq("keys are described for an error message", "_total, items",
            HalEnvelopeReader.DescribeKeys(wrongShape.RootElement));

        using var bigTotal = Json("""{ "_total": 4294967296, "_embedded": { "x": [] } }""");
        Eq("_total is read as 64-bit", 4294967296L,
            HalEnvelopeReader.Read(bigTotal.RootElement, null).Total ?? -1);
    }

    // ───────────────────────────── F. CSV output ─────────────────────────────

    private static void CsvOutput(string scratch)
    {
        Section("CSV output");

        var directory = Path.Combine(scratch, "csv");

        OutputConfig Config() => new()
        {
            Directory = directory,
            FilePrefix = "pm",
            Delimiter = ",",
            WriteByteOrderMark = true,
            NewLine = "CRLF",
            MultiValueSeparator = "|",
            NewlineInValue = NewlineHandling.Separator,
            SanitizeFormulas = true,
            WriteBufferBytes = 4096,
            Overwrite = true
        };

        // File naming: the object is always part of the name.
        Eq("the object name is in the file prefix", "pm-projects",
            CsvRollingWriter.ResolveNamePrefix(Config(), "projects"));

        var subdir = Config();
        subdir.SubdirectoryPerObject = true;
        Eq("a per-object subdirectory is used when asked",
            Path.Combine(directory, "projects"),
            CsvRollingWriter.ResolveDirectory(subdir, "projects"));
        Eq("without it, everything shares the output directory", directory,
            CsvRollingWriter.ResolveDirectory(Config(), "projects"));

        // Rolling.
        {
            var config = Config();
            var writer = new CsvRollingWriter(config, "projects", 2, new[] { "A", "B" }, Noop);
            for (var i = 1; i <= 5; i++) writer.WriteRow(new[] { i.ToString(), "x" });
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            Eq("rows roll into parts of the configured size", 3, writer.FilesCreated);
            Eq("part files are numbered from one", "pm-projects_0001.csv",
                Path.GetFileName(writer.FilePaths[0]));
            Eq("and continue in sequence", "pm-projects_0003.csv",
                Path.GetFileName(writer.FilePaths[2]));

            foreach (var file in writer.FilePaths)
            {
                var rows = ReadCsv(file);
                Eq("every part repeats the header", "A,B", string.Join(",", rows[0]));
            }

            // 5 rows at 2 per file is 2 + 2 + 1, so the last part holds one.
            Eq("the last part holds the remainder", 1, ReadCsv(writer.FilePaths[2]).Count - 1);
            Eq("a full part holds exactly the configured count", 2,
                ReadCsv(writer.FilePaths[0]).Count - 1);

            var bytes = File.ReadAllBytes(writer.FilePaths[0]);
            Check("a UTF-8 BOM is written",
                bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            Eq("the highest existing part is found for a resume", 3,
                CsvRollingWriter.FindHighestFileIndex(config, "projects"));
            Eq("a different object has no parts", 0,
                CsvRollingWriter.FindHighestFileIndex(config, "tasks"));
        }

        // A starting index continues past existing parts rather than overwriting them.
        {
            var config = Config();
            var writer = new CsvRollingWriter(config, "projects", 0, new[] { "A" }, Noop, startingFileIndex: 3);
            writer.WriteRow(new[] { "z" });
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Eq("a resumed writer starts a new part", "pm-projects_0004.csv",
                Path.GetFileName(writer.FilePaths[0]));
        }

        // RFC 4180 quoting, verified by parsing it back.
        {
            var config = Config();
            config.NewlineInValue = NewlineHandling.Preserve;

            var writer = new CsvRollingWriter(config, "quotes", 0,
                new[] { "Plain", "Comma", "Quote", "Newline", "Space" }, Noop);

            writer.WriteRow(new[]
            {
                "plain",
                "a,b",
                "say \"hi\"",
                "line1\nline2",
                " padded "
            });

            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var rows = ReadCsv(writer.FilePaths[0]);
            Eq("a plain value round-trips", "plain", rows[1][0]);
            Eq("a value containing the delimiter round-trips", "a,b", rows[1][1]);
            Eq("embedded quotes are doubled and round-trip", "say \"hi\"", rows[1][2]);
            Eq("an embedded newline is preserved under Preserve", "line1\nline2",
                rows[1][3].Replace("\r\n", "\n"));
            Eq("edge whitespace is preserved by quoting", " padded ", rows[1][4]);

            Check("the column with newlines is reported", writer.ColumnsWithNewlines.Contains("Newline"));
            Check("a clean column is not reported", !writer.ColumnsWithNewlines.Contains("Plain"));
        }

        // Newline handling modes.
        {
            foreach (var (mode, expected, label) in new[]
            {
                (NewlineHandling.Separator, "a|b", "Separator joins on the multi-value separator"),
                (NewlineHandling.Space, "a b", "Space collapses to a single space")
            })
            {
                var config = Config();
                config.NewlineInValue = mode;

                var writer = new CsvRollingWriter(config, "nl-" + mode, 0, new[] { "V" }, Noop);
                writer.WriteRow(new[] { "a\n b\n" });
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

                Eq(label, expected, ReadCsv(writer.FilePaths[0])[1][0]);
            }
        }

        // Formula-injection guard. Numbers stay numbers, which matters for PM's
        // financial objects.
        {
            var config = Config();
            var writer = new CsvRollingWriter(config, "formula", 0,
                new[] { "Eq", "Plus", "Neg", "At", "Amount", "Decimal" }, Noop);

            writer.WriteRow(new[] { "=SUM(A1)", "+cmd", "-lookup", "@ref", "-42", "+1.5" });
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var row = ReadCsv(writer.FilePaths[0])[1];
            Eq("a formula is neutralised", "'=SUM(A1)", row[0]);
            Eq("a leading plus is neutralised", "'+cmd", row[1]);
            Eq("a leading minus is neutralised", "'-lookup", row[2]);
            Eq("a leading at is neutralised", "'@ref", row[3]);
            Eq("a negative number is left alone", "-42", row[4]);
            Eq("a signed decimal is left alone", "+1.5", row[5]);
        }

        // Alternative delimiter and LF newlines.
        {
            var config = Config();
            config.Delimiter = "\t";
            config.NewLine = "LF";
            config.WriteByteOrderMark = false;

            var writer = new CsvRollingWriter(config, "tabbed", 0, new[] { "A", "B" }, Noop);
            writer.WriteRow(new[] { "x", "y,z" });
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var text = File.ReadAllText(writer.FilePaths[0]);
            Check("LF newlines are used", !text.Contains('\r'));
            Check("a comma needs no quoting under a tab delimiter", text.Contains("x\ty,z"));
            Check("no BOM is written when disabled",
                File.ReadAllBytes(writer.FilePaths[0])[0] != 0xEF);
        }

        // Guards.
        {
            var config = Config();
            var writer = new CsvRollingWriter(config, "guard", 0, new[] { "A", "B" }, Noop);
            Throws<ArgumentException>("a row of the wrong width is rejected",
                () => writer.WriteRow(new[] { "only-one" }));
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // PrepareDirectory refuses to blend runs unless told to overwrite.
        {
            var config = Config();
            config.Overwrite = false;

            var writer = new CsvRollingWriter(config, "existing", 0, new[] { "A" }, Noop);
            writer.WriteRow(new[] { "1" });
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            Throws<InvalidOperationException>("existing parts are refused without --overwrite",
                () => CsvRollingWriter.PrepareDirectory(config, "existing", Noop), "--overwrite");

            // A different object's files must not block this one.
            CsvRollingWriter.PrepareDirectory(config, "unrelated", Noop);
            _passed++;

            config.Overwrite = true;
            CsvRollingWriter.PrepareDirectory(config, "existing", Noop);
            Eq("with --overwrite the old parts are deleted", 0,
                CsvRollingWriter.FindHighestFileIndex(config, "existing"));
        }
    }

    /// <summary>
    /// Quote-aware CSV reader, so the writer's output is verified by parsing rather than
    /// by string comparison against what the writer itself produced.
    /// </summary>
    private static List<string[]> ReadCsv(string path)
    {
        var text = File.ReadAllText(path).TrimStart('﻿');
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',' or '\t':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;

                default:
                    cell.Append(c);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    // ───────────────────────────── G. rate limiter ─────────────────────────────

    private static async Task RateLimiterTests()
    {
        Section("Rate limiter");

        // Pacing. Burst 1 means strictly even spacing, so N requests take (N-1)/rate.
        {
            using var limiter = new RateLimiter(20, 1, 1);
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) (await limiter.AcquireAsync(default)).Dispose();
            var elapsed = stopwatch.ElapsedMilliseconds;

            // Expected ~200 ms. Task.Delay granularity is coarse on Windows, so the upper
            // bound is generous; the point is that pacing happened at all.
            Check("20 rps paces five requests", elapsed >= 150 && elapsed < 900, $"took {elapsed} ms");
        }

        {
            using var limiter = new RateLimiter(5, 5, 1);
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) (await limiter.AcquireAsync(default)).Dispose();
            Check("a full burst is released immediately",
                stopwatch.ElapsedMilliseconds < 120, $"took {stopwatch.ElapsedMilliseconds} ms");
        }

        {
            using var limiter = new RateLimiter(0, 1, 1);
            Check("rps 0 disables throttling", !limiter.Enabled);

            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < 50; i++) (await limiter.AcquireAsync(default)).Dispose();
            Check("and 50 requests run without pacing",
                stopwatch.ElapsedMilliseconds < 120, $"took {stopwatch.ElapsedMilliseconds} ms");
        }

        // Backpressure holds every caller, not just the one that was throttled.
        {
            using var limiter = new RateLimiter(100, 10, 1);
            await limiter.PenalizeAsync(TimeSpan.FromMilliseconds(300), default);

            var stopwatch = Stopwatch.StartNew();
            (await limiter.AcquireAsync(default)).Dispose();
            Check("a Retry-After penalty holds the next request",
                stopwatch.ElapsedMilliseconds >= 250, $"waited only {stopwatch.ElapsedMilliseconds} ms");
        }

        {
            using var limiter = new RateLimiter(0, 1, 1);
            var stopwatch = Stopwatch.StartNew();
            await limiter.PenalizeAsync(TimeSpan.FromSeconds(5), default);
            (await limiter.AcquireAsync(default)).Dispose();
            Check("a penalty on a disabled limiter is a no-op",
                stopwatch.ElapsedMilliseconds < 120, $"took {stopwatch.ElapsedMilliseconds} ms");
        }

        // Cancellation while waiting must not hang.
        {
            using var limiter = new RateLimiter(0.5, 1, 1);
            (await limiter.AcquireAsync(default)).Dispose();

            using var cts = new CancellationTokenSource(60);
            var threw = false;
            try { (await limiter.AcquireAsync(cts.Token)).Dispose(); }
            catch (OperationCanceledException) { threw = true; }
            Check("cancellation while waiting throws", threw);
        }

        // The concurrency cap is independent of the rate.
        {
            using var limiter = new RateLimiter(0, 1, 1);
            var slot = await limiter.AcquireAsync(default);

            var second = limiter.AcquireAsync(default).AsTask();
            var completed = await Task.WhenAny(second, Task.Delay(120));
            Check("a concurrency slot blocks a second caller", completed != second);

            slot.Dispose();
            var released = await Task.WhenAny(second, Task.Delay(500));
            Check("releasing the slot lets it through", released == second);
            (await second).Dispose();
        }
    }

    // ───────────────────────────── H. credential store ─────────────────────────────

    private static void CredentialStoreTests(string scratch)
    {
        Section("Credential store");

        if (!CredentialStore.IsSupported)
        {
            Console.WriteLine("   skipped — DPAPI is Windows-only.");
            return;
        }

        var store = Path.Combine(scratch, "credentials.dat");
        const string secret = "s3cr3t-value-not-in-plaintext";

        CredentialStore.Save("acme", "client-abc", secret, Noop, store);

        Check("the store file is created", File.Exists(store));

        // The whole point of the store: no plaintext secret on disk.
        var raw = File.ReadAllBytes(store);
        Check("the secret does not appear in the file bytes",
            Encoding.UTF8.GetString(raw).IndexOf(secret, StringComparison.Ordinal) < 0);
        Check("neither does the client ID",
            Encoding.UTF8.GetString(raw).IndexOf("client-abc", StringComparison.Ordinal) < 0);

        var config = new AprimoConfig { Tenant = "acme" };
        Check("gaps are filled from the store", CredentialStore.TryFill(config, Noop, store));
        Eq("the client ID round-trips", "client-abc", config.ClientId);
        Eq("the secret round-trips", secret, config.ClientSecret);

        // The store only fills gaps: an explicit value must always win.
        var explicitConfig = new AprimoConfig { Tenant = "acme", ClientId = "mine", ClientSecret = "also-mine" };
        Check("an explicit credential is never overwritten",
            !CredentialStore.TryFill(explicitConfig, Noop, store));
        Eq("the explicit ID survives", "mine", explicitConfig.ClientId);

        // A placeholder counts as absent, or a stray env var would shadow the store.
        var placeholder = new AprimoConfig { Tenant = "acme", ClientId = "<your-client-id>" };
        Check("a placeholder is treated as a gap", CredentialStore.TryFill(placeholder, Noop, store));
        Eq("and is replaced by the saved value", "client-abc", placeholder.ClientId);

        // Tenants are isolated.
        CredentialStore.Save("other", "client-xyz", "other-secret", Noop, store);
        var tenants = CredentialStore.ListTenants(Noop, store);
        Eq("both tenants are listed", 2, tenants.Count);

        var otherConfig = new AprimoConfig { Tenant = "other" };
        CredentialStore.TryFill(otherConfig, Noop, store);
        Eq("each tenant gets its own credential", "client-xyz", otherConfig.ClientId);

        var unknown = new AprimoConfig { Tenant = "absent" };
        Check("an unknown tenant fills nothing", !CredentialStore.TryFill(unknown, Noop, store));

        // Replacing an entry keeps the others.
        CredentialStore.Save("acme", "client-new", "new-secret", Noop, store);
        var replaced = new AprimoConfig { Tenant = "acme" };
        CredentialStore.TryFill(replaced, Noop, store);
        Eq("an entry can be replaced", "client-new", replaced.ClientId);
        Eq("replacing one entry keeps the other", 2, CredentialStore.ListTenants(Noop, store).Count);

        Check("clearing one tenant succeeds", CredentialStore.Clear("acme", Noop, store));
        Eq("and leaves the other", 1, CredentialStore.ListTenants(Noop, store).Count);
        Check("clearing an absent tenant reports false", !CredentialStore.Clear("absent", Noop, store));

        Check("clearing the last entry deletes the store",
            CredentialStore.Clear("other", Noop, store) && !File.Exists(store));

        Check("clearing a store that does not exist reports false",
            !CredentialStore.Clear("acme", Noop, store));
        Check("Exists reflects the missing file", !CredentialStore.Exists(store));

        // A file that cannot be decrypted is explained, not thrown.
        var corrupt = Path.Combine(scratch, "corrupt.dat");
        File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var messages = new List<string>();
        var corruptConfig = new AprimoConfig { Tenant = "acme" };
        var filled = CredentialStore.TryFill(corruptConfig, messages.Add, corrupt);

        Check("an undecryptable store fills nothing", !filled);
        Check("and explains itself rather than crashing",
            messages.Any(m => m.Contains("could not be decrypted", StringComparison.OrdinalIgnoreCase)));
    }

    // ───────────────────────────── I. delta windows ─────────────────────────────

    private static void DeltaWindows()
    {
        Section("Delta windows");

        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var config = new DeltaConfig { OverlapMinutes = 0, WarnIfWindowExceedsDays = 0 };
        var empty = new DeltaState();

        DeltaWindow Create(string since, string? until = null, DeltaState? state = null,
            DeltaConfig? cfg = null) =>
            DeltaWindowParser.Create(since, until, cfg ?? config, state ?? empty, now, Noop);

        var yesterday = Create("yesterday");
        Eq("yesterday starts at the previous UTC midnight",
            "2026-08-04T00:00:00.000Z", DeltaWindow.Format(yesterday.Since));
        Eq("and ends at today's UTC midnight",
            "2026-08-05T00:00:00.000Z", DeltaWindow.Format(yesterday.Until));

        var today = Create("today");
        Eq("today starts at UTC midnight", "2026-08-05T00:00:00.000Z", DeltaWindow.Format(today.Since));
        Eq("and ends now", "2026-08-05T12:00:00.000Z", DeltaWindow.Format(today.Until));

        Eq("a day span counts back from now", "2026-08-04T12:00:00.000Z",
            DeltaWindow.Format(Create("1d").Since));
        Eq("an hour span counts back from now", "2026-08-05T00:00:00.000Z",
            DeltaWindow.Format(Create("12h").Since));
        Eq("a minute span counts back from now", "2026-08-05T10:30:00.000Z",
            DeltaWindow.Format(Create("90m").Since));

        var bareDate = Create("2026-07-01");
        Eq("a bare date starts at its UTC midnight",
            "2026-07-01T00:00:00.000Z", DeltaWindow.Format(bareDate.Since));
        Eq("and covers exactly that UTC day",
            "2026-07-02T00:00:00.000Z", DeltaWindow.Format(bareDate.Until));

        var instant = Create("2026-08-05T04:00:00Z", "2026-08-05T08:00:00Z");
        Eq("an explicit instant is honoured", "2026-08-05T04:00:00.000Z", DeltaWindow.Format(instant.Since));
        Eq("as is an explicit end", "2026-08-05T08:00:00.000Z", DeltaWindow.Format(instant.Until));
        Eq("the duration follows", 4, (long)instant.Duration.TotalHours);

        // --until with a bare date includes that whole day.
        Eq("an --until bare date includes that day", "2026-07-03T00:00:00.000Z",
            DeltaWindow.Format(Create("2026-07-01", "2026-07-02").Until));

        Eq("'now' is valid for --until", "2026-08-05T12:00:00.000Z",
            DeltaWindow.Format(Create("1d", "now").Until));

        // Resuming from a saved mark.
        var state = new DeltaState
        {
            HighWaterMarkUtc = new DateTimeOffset(2026, 8, 4, 6, 0, 0, TimeSpan.Zero)
        };

        var resumed = Create("last", state: state);
        Eq("--since last resumes from the saved mark",
            "2026-08-04T06:00:00.000Z", DeltaWindow.Format(resumed.Since));
        Check("and is flagged as coming from state", resumed.FromState);

        var overlapped = Create("last", state: state,
            cfg: new DeltaConfig { OverlapMinutes = 5, WarnIfWindowExceedsDays = 0 });
        Eq("the overlap re-reads a little of the previous window",
            "2026-08-04T05:55:00.000Z", DeltaWindow.Format(overlapped.Since));

        // Overlap only applies to a resumed window; an explicit one is taken literally.
        Eq("an explicit window is not shifted by the overlap", "2026-08-04T12:00:00.000Z",
            DeltaWindow.Format(Create("1d",
                cfg: new DeltaConfig { OverlapMinutes = 5, WarnIfWindowExceedsDays = 0 }).Since));

        Throws<InvalidOperationException>("--since last with no saved mark is refused",
            () => Create("last"), "no previous run");

        Throws<ArgumentException>("an unparseable --since is rejected",
            () => Create("banana"), "could not understand");
        Throws<ArgumentException>("a zero span is rejected", () => Create("0d"));
        Throws<ArgumentException>("an inverted window is rejected",
            () => Create("2026-08-05T08:00:00Z", "2026-08-05T04:00:00Z"), "inverted");
        Throws<ArgumentException>("an empty window is rejected",
            () => Create("2026-08-05T08:00:00Z", "2026-08-05T08:00:00Z"), "empty");

        // Half-open containment: adjacent windows must neither overlap nor gap.
        var window = new DeltaWindow(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            false, "test");

        Check("the lower bound is inclusive",
            window.Contains(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero)));
        Check("the upper bound is exclusive",
            !window.Contains(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));
        Check("an instant inside is contained",
            window.Contains(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)));
        Check("an instant before is not",
            !window.Contains(new DateTimeOffset(2026, 8, 3, 23, 59, 59, TimeSpan.Zero)));

        var next = new DeltaWindow(window.Until, window.Until.AddDays(1), false, "next");
        Check("adjacent windows do not double-count the boundary",
            !window.Contains(next.Since) && next.Contains(next.Since));

        Check("Describe includes both bounds",
            window.Describe().Contains("2026-08-04") && window.Describe().Contains("2026-08-05"));

        // Epoch conversion, for Budget.
        Eq("epoch milliseconds convert", "2026-08-05T12:00:00.000Z",
            DeltaWindow.Format(DeltaFilter.FromEpoch(1785931200000L, true)));
        Eq("epoch seconds convert", "2026-08-05T12:00:00.000Z",
            DeltaWindow.Format(DeltaFilter.FromEpoch(1785931200L, false)));
        Eq("an undeclared unit is inferred as seconds by magnitude", "2026-08-05T12:00:00.000Z",
            DeltaWindow.Format(DeltaFilter.FromEpoch(1785931200L, null)));
        Eq("an undeclared unit is inferred as milliseconds by magnitude", "2026-08-05T12:00:00.000Z",
            DeltaWindow.Format(DeltaFilter.FromEpoch(1785931200000L, null)));
    }

    // ───────────────────────────── J. delta filtering ─────────────────────────────

    private static void DeltaFiltering()
    {
        Section("Delta filtering");

        var window = new DeltaWindow(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            false, "test");

        var pmObject = ProjectsObject();

        DeltaFilter NewFilter(DeltaConfig? config = null) =>
            new(window, pmObject, config ?? new DeltaConfig(), Noop);

        JsonElement Item(JsonDocument doc) => doc.RootElement;

        using var inside = Json("""{ "projectId": 1, "modifiedDate": "2026-08-04T12:00:00Z" }""");
        using var before = Json("""{ "projectId": 2, "modifiedDate": "2026-08-01T12:00:00Z" }""");
        using var after = Json("""{ "projectId": 3, "modifiedDate": "2026-08-06T12:00:00Z" }""");
        using var missing = Json("""{ "projectId": 4 }""");
        using var unparseable = Json("""{ "projectId": 5, "modifiedDate": "not-a-date" }""");
        using var nullStamp = Json("""{ "projectId": 6, "modifiedDate": null }""");

        {
            var filter = NewFilter();
            Check("an item inside the window is accepted", filter.Accept(Item(inside)));
            Check("an item before the window is rejected", !filter.Accept(Item(before)));
            Check("an item after the window is rejected", !filter.Accept(Item(after)));
            Check("an item with no timestamp is rejected", !filter.Accept(Item(missing)));
            Check("an unparseable timestamp is rejected", !filter.Accept(Item(unparseable)));
            Check("a null timestamp is rejected", !filter.Accept(Item(nullStamp)));

            Eq("matches are counted", 1, filter.Matched);
            Eq("older rows are counted", 1, filter.BeforeWindow);
            Eq("newer rows are counted", 1, filter.AfterWindow);
            Eq("undated rows are counted", 3, filter.Unparseable);
            Eq("everything examined is counted", 6, filter.Examined);
            Check("the summary mentions the window", filter.Summarize().Contains("in window"));
        }

        // Boundaries, since the window is half-open.
        {
            using var atStart = Json("""{ "modifiedDate": "2026-08-04T00:00:00Z" }""");
            using var atEnd = Json("""{ "modifiedDate": "2026-08-05T00:00:00Z" }""");

            var filter = NewFilter();
            Check("an item exactly at the window start is included", filter.Accept(Item(atStart)));
            Check("an item exactly at the window end is excluded", !filter.Accept(Item(atEnd)));
        }

        // Epoch-stamped objects (Budget).
        {
            var budget = new PmObject
            {
                Name = "budgets",
                Path = "/budget",
                IdProperty = "id",
                ModifiedProperty = "updatedAt",
                ModifiedIsEpoch = true
            };

            using var epochInside = Json("""{ "id": "b1", "updatedAt": 1785801600000 }""");
            var filter = new DeltaFilter(window, budget, new DeltaConfig(), Noop);
            Check("an epoch timestamp is placed in the window", filter.Accept(Item(epochInside)));
        }

        // The early-stop rule. This is the safety-critical part: stopping early when the
        // sort was not honoured would silently truncate the export.
        {
            var filter = NewFilter();

            Check("no early stop on a page with rows still in the window",
                !filter.CanStopAfterPage(10, 3));
            Check("no early stop on an empty page", !filter.CanStopAfterPage(0, 0));

            // Nothing has proved the order yet.
            Check("no early stop before descending order has been observed",
                !filter.CanStopAfterPage(1, 1));
        }

        {
            // Two strictly decreasing values prove the order.
            var filter = NewFilter();
            using var a = Json("""{ "modifiedDate": "2026-08-03T12:00:00Z" }""");
            using var b = Json("""{ "modifiedDate": "2026-08-02T12:00:00Z" }""");
            filter.Accept(Item(a));
            filter.Accept(Item(b));

            Check("descending order plus a fully-old page allows an early stop",
                filter.CanStopAfterPage(2, 2));
            Check("and the order is not marked as disproved", !filter.SortDisproved);
        }

        {
            // An ascending pair disproves it, and no later evidence may undo that.
            var filter = NewFilter();
            using var older = Json("""{ "modifiedDate": "2026-08-01T12:00:00Z" }""");
            using var newer = Json("""{ "modifiedDate": "2026-08-02T12:00:00Z" }""");
            filter.Accept(Item(older));
            filter.Accept(Item(newer));

            Check("an out-of-order pair disproves the sort", filter.SortDisproved);
            Check("and no early stop is allowed", !filter.CanStopAfterPage(2, 2));

            using var oldest = Json("""{ "modifiedDate": "2026-07-01T12:00:00Z" }""");
            filter.Accept(Item(oldest));
            Check("a later descending pair does not undo the disproof",
                filter.SortDisproved && !filter.CanStopAfterPage(3, 3));
        }

        {
            // Ties prove nothing either way — common on bulk-updated data.
            var filter = NewFilter();
            using var t1 = Json("""{ "modifiedDate": "2026-08-01T12:00:00Z" }""");
            using var t2 = Json("""{ "modifiedDate": "2026-08-01T12:00:00Z" }""");
            filter.Accept(Item(t1));
            filter.Accept(Item(t2));

            Check("equal timestamps do not confirm descending order", !filter.CanStopAfterPage(2, 2));
            Check("nor do they disprove it", !filter.SortDisproved);
        }

        {
            // With verification off, absence of contrary evidence is enough.
            var filter = NewFilter(new DeltaConfig { SortVerification = false });
            using var t1 = Json("""{ "modifiedDate": "2026-08-01T12:00:00Z" }""");
            filter.Accept(Item(t1));
            Check("with SortVerification off, a fully-old page is enough",
                filter.CanStopAfterPage(1, 1));
        }

        {
            var filter = NewFilter(new DeltaConfig { Strategy = DeltaStrategy.FullScan });
            using var a = Json("""{ "modifiedDate": "2026-08-03T12:00:00Z" }""");
            using var b = Json("""{ "modifiedDate": "2026-08-02T12:00:00Z" }""");
            filter.Accept(Item(a));
            filter.Accept(Item(b));
            Check("FullScan never stops early", !filter.CanStopAfterPage(2, 2));
        }

        // An explicit Delta.Property overrides the object's own.
        {
            var filter = new DeltaFilter(window, pmObject,
                new DeltaConfig { Property = "createdDate" }, Noop);
            Eq("Delta.Property overrides the object's modified property", "createdDate", filter.Property);
        }

        // An object with no timestamp cannot be filtered at all.
        Throws<InvalidOperationException>("an object with no modified property is refused",
            () => new DeltaFilter(window,
                new PmObject { Name = "teams", Path = "/teams", IdProperty = "teamId" },
                new DeltaConfig(), Noop),
            "cannot be exported incrementally");
    }

    // ───────────────────────────── K. delta state ─────────────────────────────

    private static void DeltaState(string scratch)
    {
        Section("Delta state");

        var directory = Path.Combine(scratch, "delta");
        Directory.CreateDirectory(directory);

        var config = new ExportConfig();
        config.Aprimo.Tenant = "acme";
        config.Output.Directory = directory;

        var projects = ProjectsObject();
        var tasks = new PmObject
        {
            Name = "tasks", Path = "/tasks", IdProperty = "taskId", ModifiedProperty = "modifiedDate"
        };

        // Keys separate objects, so a partial run never disturbs the rest.
        var projectKey = DeltaStateStore.ComputeKey(config, projects);
        var taskKey = DeltaStateStore.ComputeKey(config, tasks);
        Check("each object gets its own lineage key", projectKey != taskKey);
        Check("the key names the object", projectKey.Contains("projects"));

        // Changing the compared property starts a fresh lineage.
        var altered = new ExportConfig { Output = { Directory = directory } };
        altered.Aprimo.Tenant = "acme";
        altered.Source.Delta.Property = "createdDate";
        Check("changing the delta property starts a fresh lineage",
            DeltaStateStore.ComputeKey(altered, projects) != projectKey);

        // Changing the tenant does too.
        var otherTenant = new ExportConfig { Output = { Directory = directory } };
        otherTenant.Aprimo.Tenant = "other";
        Check("a different tenant is a different lineage",
            DeltaStateStore.ComputeKey(otherTenant, projects) != projectKey);

        var first = DeltaStateStore.Load(config, projects, Noop);
        Check("a first run has no mark", first.HighWaterMarkUtc is null);
        Eq("and no run count", 0, first.RunCount);

        var window = new DeltaWindow(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            false, "test");

        DeltaStateStore.Save(config, projects, window, 120, Noop);

        var reloaded = DeltaStateStore.Load(config, projects, Noop);

        // The mark advances to the window end, not to the highest timestamp seen — that
        // is what makes consecutive windows gap-free.
        Eq("the mark advances to the window end", "2026-08-05T00:00:00.000Z",
            DeltaWindow.Format(reloaded.HighWaterMarkUtc!.Value));
        Eq("the row count is recorded", 120, reloaded.LastRunRows);
        Eq("the total accumulates", 120, reloaded.TotalRows);
        Eq("the run count increments", 1, reloaded.RunCount);
        Eq("the object is recorded for readability", "projects", reloaded.Object);

        var second = new DeltaWindow(window.Until, window.Until.AddDays(1), false, "next");
        DeltaStateStore.Save(config, projects, second, 30, Noop);

        var afterSecond = DeltaStateStore.Load(config, projects, Noop);
        Eq("a second run accumulates the total", 150, afterSecond.TotalRows);
        Eq("and increments the run count", 2, afterSecond.RunCount);

        // Another object's state is independent.
        Check("an object never saved has no mark",
            DeltaStateStore.Load(config, tasks, Noop).HighWaterMarkUtc is null);

        DeltaStateStore.Save(config, tasks, window, 7, Noop);
        Eq("saving one object leaves the other intact", 150,
            DeltaStateStore.Load(config, projects, Noop).TotalRows);

        // Resetting is per-object.
        Check("resetting one object succeeds",
            DeltaStateStore.Reset(config, new[] { projects }, Noop));
        Check("the reset object loses its mark",
            DeltaStateStore.Load(config, projects, Noop).HighWaterMarkUtc is null);
        Check("the other object keeps its mark",
            DeltaStateStore.Load(config, tasks, Noop).HighWaterMarkUtc is not null);

        Check("resetting everything succeeds",
            DeltaStateStore.Reset(config, Array.Empty<PmObject>(), Noop));
        Check("and removes the state file",
            !File.Exists(DeltaStateStore.ResolvePath(config)));
        Check("resetting with no state reports false",
            !DeltaStateStore.Reset(config, Array.Empty<PmObject>(), Noop));

        // A corrupt state file must not abort a run.
        File.WriteAllText(DeltaStateStore.ResolvePath(config), "{ not json");
        var recovered = DeltaStateStore.Load(config, projects, Noop);
        Check("a corrupt state file is treated as a first run", recovered.HighWaterMarkUtc is null);
    }

    // ───────────────────────────── L. CLI guards ─────────────────────────────

    private static void CommandLineGuards()
    {
        Section("Command line");

        var args = CommandLineArgs.Parse(new[]
        {
            "--tenant", "acme",
            "--objects", "projects,tasks",
            "--rps", "2.5",
            "--burst", "3",
            "--page-size", "250",
            "--paging", "LinkFollow",
            "--max-pages", "10",
            "--sort", "modifiedDate",
            "--desc",
            "--since", "1d",
            "--until", "now",
            "--delta-strategy", "FullScan",
            "--delta-property", "createdDate",
            "--max-rows", "5000",
            "--max-per-file", "1_000",
            "--out", "./out",
            "--prefix", "pm",
            "--delimiter", "tab",
            "--overwrite",
            "--subdir-per-object",
            "--resume",
            "--verbose"
        });

        Eq("--tenant parses", "acme", args.Tenant);
        Eq("--objects parses", "projects,tasks", args.Objects);
        Check("--rps parses a fractional value", Math.Abs((args.RequestsPerSecond ?? 0) - 2.5) < 1e-9);
        Eq("--burst parses", 3, args.Burst ?? -1);
        Eq("--page-size parses", 250, args.PageSize ?? -1);
        Check("--paging parses an enum", args.PagingMode == PagingMode.LinkFollow);
        Eq("--max-pages parses", 10, args.MaxPages ?? -1);
        Eq("--sort parses", "modifiedDate", args.SortField);
        Check("--desc is a flag", args.SortDescending);
        Eq("--since parses", "1d", args.Since);
        Eq("--until parses", "now", args.Until);
        Check("--delta-strategy parses an enum", args.DeltaStrategy == DeltaStrategy.FullScan);
        Eq("--delta-property parses", "createdDate", args.DeltaProperty);
        Eq("--max-rows parses", 5000, args.MaxRecordsPerObject ?? -1);
        Eq("--max-per-file accepts underscores", 1000, args.MaxRecordsPerFile ?? -1);
        Eq("--out parses", "./out", args.OutputDirectory);
        Eq("--prefix parses", "pm", args.FilePrefix);
        Eq("--delimiter maps 'tab' to a tab", "\t", args.Delimiter);
        Check("--overwrite is a flag", args.Overwrite);
        Check("--subdir-per-object is a flag", args.SubdirectoryPerObject);
        Check("--resume is a flag", args.Resume);
        Check("--verbose is a flag", args.Verbose);

        Check("--daily means --since last", CommandLineArgs.Parse(new[] { "--daily" }).Since == "last");
        Check("--all-objects is a flag", CommandLineArgs.Parse(new[] { "--all-objects" }).AllObjects);
        Check("--list-objects is a flag", CommandLineArgs.Parse(new[] { "--list-objects" }).ListObjects);
        Check("--discover-fields is a flag",
            CommandLineArgs.Parse(new[] { "--discover-fields" }).DiscoverFields);
        Check("--reset-delta is a flag", CommandLineArgs.Parse(new[] { "--reset-delta" }).ResetDelta);
        Check("--validate-only is a flag", CommandLineArgs.Parse(new[] { "--validate-only" }).ValidateOnly);
        Check("--login aliases --prompt-credentials",
            CommandLineArgs.Parse(new[] { "--login" }).PromptCredentials);
        Check("--save-credentials is a flag",
            CommandLineArgs.Parse(new[] { "--save-credentials" }).SaveCredentials);
        Check("--list-credentials is a flag",
            CommandLineArgs.Parse(new[] { "--list-credentials" }).ListCredentials);
        Check("--clear-credentials is a flag",
            CommandLineArgs.Parse(new[] { "--clear-credentials" }).ClearCredentials);
        Check("--all is a flag", CommandLineArgs.Parse(new[] { "--all" }).AllTenants);
        Check("--help is a flag", CommandLineArgs.Parse(new[] { "--help" }).ShowHelp);
        Eq("--selection-list parses", "Channels",
            CommandLineArgs.Parse(new[] { "--selection-list", "Channels" }).SelectionList);
        Eq("--fields-dir parses", "custom",
            CommandLineArgs.Parse(new[] { "--fields-dir", "custom" }).FieldsDirectory);
        Eq("--objects-file parses", "reg.json",
            CommandLineArgs.Parse(new[] { "--objects-file", "reg.json" }).ObjectsFile);
        Eq("--api-base-url parses", "https://x/api",
            CommandLineArgs.Parse(new[] { "--api-base-url", "https://x/api" }).ApiBaseUrl);
        Check("--client-auth-style parses an enum",
            CommandLineArgs.Parse(new[] { "--client-auth-style", "Body" }).ClientAuthStyle
                == ClientAuthStyle.Body);
        // Switch matching is case-insensitive, so '-O' would be indistinguishable from
        // the '-o' that means --out. There is deliberately no short alias for --objects.
        Eq("-o still means --out, not --objects", "dir",
            CommandLineArgs.Parse(new[] { "-o", "dir" }).OutputDirectory);
        Check("-o does not set the object selection",
            CommandLineArgs.Parse(new[] { "-o", "dir" }).Objects is null);
        Eq("-O is treated as --out too, not as a second meaning", "dir",
            CommandLineArgs.Parse(new[] { "-O", "dir" }).OutputDirectory);
        Check("the usage text offers no short alias for --objects",
            !CommandLineArgs.Usage.Contains("-O, --objects"));

        // --sample takes an optional count.
        Eq("--sample defaults to 5", 5, CommandLineArgs.Parse(new[] { "--sample" }).SampleSize ?? -1);
        Eq("--sample takes a count", 40, CommandLineArgs.Parse(new[] { "--sample", "40" }).SampleSize ?? -1);
        Check("--sample followed by another switch keeps the default",
            CommandLineArgs.Parse(new[] { "--sample", "--verbose" }).SampleSize == 5 &&
            CommandLineArgs.Parse(new[] { "--sample", "--verbose" }).Verbose);
        Throws<ArgumentException>("--sample 0 is rejected",
            () => CommandLineArgs.Parse(new[] { "--sample", "0" }), "1 or more");

        Eq("delimiter 'pipe' maps", "|",
            CommandLineArgs.Parse(new[] { "--delimiter", "pipe" }).Delimiter);
        Eq("delimiter 'semicolon' maps", ";",
            CommandLineArgs.Parse(new[] { "--delimiter", "semicolon" }).Delimiter);
        Eq("a literal delimiter passes through", "~",
            CommandLineArgs.Parse(new[] { "--delimiter", "~" }).Delimiter);

        Throws<ArgumentException>("an unknown option is rejected",
            () => CommandLineArgs.Parse(new[] { "--nope" }), "Unknown option");
        Throws<ArgumentException>("a switch missing its value is rejected",
            () => CommandLineArgs.Parse(new[] { "--tenant" }), "requires a value");
        Throws<ArgumentException>("a non-numeric --rps is rejected",
            () => CommandLineArgs.Parse(new[] { "--rps", "fast" }), "expects a number");
        Throws<ArgumentException>("a non-integer --page-size is rejected",
            () => CommandLineArgs.Parse(new[] { "--page-size", "many" }), "expects an integer");
        Throws<ArgumentException>("an invalid enum lists the valid values",
            () => CommandLineArgs.Parse(new[] { "--paging", "Keyset" }), "Offset");

        // --fields-file names one list, so it cannot describe a whole multi-object run.
        Throws<ArgumentException>("--fields-file with --all-objects is refused",
            () => CommandLineArgs.Parse(new[] { "--fields-file", "f.txt", "--all-objects" }),
            "cannot be combined");
        Check("--fields-file with a single object is allowed",
            CommandLineArgs.Parse(new[] { "--fields-file", "f.txt", "--objects", "projects" })
                .FieldsFile == "f.txt");

        Check("the usage text mentions the PM base path",
            CommandLineArgs.Usage.Contains("/api"));
        Check("the usage text warns that PM is not the DAM API",
            CommandLineArgs.Usage.Contains("dam.aprimo.com"));
    }

    // ───────────────────────────── M. config validation ─────────────────────────────

    private static void ConfigValidation()
    {
        Section("Config validation");

        ExportConfig Valid()
        {
            var config = new ExportConfig();
            config.Aprimo.Tenant = "acme";
            config.Aprimo.ClientId = "real-id";
            config.Aprimo.ClientSecret = "real-secret";
            return config;
        }

        Check("a complete config validates", !Valid().Validate().Any());

        var noTenant = Valid();
        noTenant.Aprimo.Tenant = "";
        Check("a missing tenant is rejected",
            noTenant.Validate().Any(e => e.Contains("Aprimo.Tenant is required")));

        var explicitUrls = Valid();
        explicitUrls.Aprimo.Tenant = "";
        explicitUrls.Aprimo.ApiBaseUrl = "https://x.example/api";
        explicitUrls.Aprimo.TokenUrl = "https://x.example/token";
        Check("explicit URLs remove the need for a tenant", !explicitUrls.Validate().Any());

        var noId = Valid();
        noId.Aprimo.ClientId = "";
        Check("a missing client ID is rejected",
            noId.Validate().Any(e => e.Contains("ClientId is required")));

        // The split that lets the tenant be checked before anything is typed. Validating
        // in one pass meant a missing tenant surfaced only after the credential prompt,
        // discarding a secret that could never have been used.
        var credentialless = Valid();
        credentialless.Aprimo.ClientId = "";
        credentialless.Aprimo.ClientSecret = "";

        Check("credential errors are excluded from the pre-prompt pass",
            !credentialless.ValidateExcludingCredentials().Any());
        Check("but are still reported by the full pass",
            credentialless.Validate().Count() == 2);
        Eq("and the credential pass reports both on its own", 2,
            credentialless.Aprimo.ValidateCredentials().Count());

        var tenantless = Valid();
        tenantless.Aprimo.Tenant = "";
        tenantless.Aprimo.ClientId = "";
        tenantless.Aprimo.ClientSecret = "";

        Check("a missing tenant is caught before credentials are asked for",
            tenantless.ValidateExcludingCredentials().Any(e => e.Contains("Aprimo.Tenant is required")));
        Check("and that message names the flag that fixes it",
            tenantless.ValidateExcludingCredentials().Any(e => e.Contains("--tenant")));
        Check("the pre-prompt pass still catches non-credential settings",
            tenantless.ValidateExcludingCredentials().Any(e => e.Contains("not a valid absolute URL")));

        // Everything non-credential must appear in both passes, or moving a check into
        // the early pass could silently drop it from the full one.
        var everything = Valid();
        everything.Aprimo.Tenant = "";
        everything.Output.NewLine = "CR";
        everything.Throttle.Burst = 0;
        everything.Objects = "";

        foreach (var error in everything.ValidateExcludingCredentials())
            Check("'" + Truncate(error) + "' also appears in the full validation",
                everything.Validate().Contains(error));

        Check("endpoint validation alone ignores credentials",
            !Valid().Aprimo.ValidateEndpoints().Any() &&
            !credentialless.Aprimo.ValidateEndpoints().Any());

        // Placeholders that were copied from documentation.
        foreach (var placeholder in new[] { "<your-client-id>", "your-client-id", "changeme", "TODO" })
        {
            var config = Valid();
            config.Aprimo.ClientId = placeholder;
            Check($"the placeholder '{placeholder}' is rejected",
                config.Validate().Any(e => e.Contains("placeholder")));
        }

        var realSecret = Valid();
        realSecret.Aprimo.ClientSecret = "a-secret-containing-the-word-example-inside";
        Check("a real secret containing a placeholder word is not rejected",
            !realSecret.Validate().Any());

        var placeholderTenant = Valid();
        placeholderTenant.Aprimo.Tenant = "your-tenant";
        Check("a placeholder tenant is rejected",
            placeholderTenant.Validate().Any(e => e.Contains("placeholder")));
        Check("the placeholder message names the environment variable first",
            placeholderTenant.Validate().Any(e => e.Contains("APRIMO_TENANT")));

        var badPageSize = Valid();
        badPageSize.Source.Paging.PageSize = 0;
        Check("a zero page size is rejected",
            badPageSize.Validate().Any(e => e.Contains("PageSize must be > 0")));

        badPageSize.Source.Paging.PageSize = PagingConfig.SafePageSizeCeiling + 1;
        var ceilingErrors = badPageSize.Validate().ToList();
        Check("an absurd page size is rejected", ceilingErrors.Any(e => e.Contains("PageSize must be <=")));
        // The spec documents no maximum, so the message must not claim one.
        Check("and the message says it is a local check, not an API limit",
            ceilingErrors.Any(e => e.Contains("documents no maximum")));

        var badNewline = Valid();
        badNewline.Output.NewLine = "CR";
        Check("an invalid newline setting is rejected",
            badNewline.Validate().Any(e => e.Contains("must be CRLF or LF")));

        var badDelimiter = Valid();
        badDelimiter.Output.Delimiter = "\"";
        Check("a double-quote delimiter is rejected",
            badDelimiter.Validate().Any(e => e.Contains("cannot contain a double quote")));

        var badBuffer = Valid();
        badBuffer.Output.WriteBufferBytes = 100;
        Check("a tiny write buffer is rejected",
            badBuffer.Validate().Any(e => e.Contains("WriteBufferBytes")));

        var badThrottle = Valid();
        badThrottle.Throttle.Burst = 0;
        Check("a burst below one is rejected",
            badThrottle.Validate().Any(e => e.Contains("Burst must be >= 1")));

        badThrottle = Valid();
        badThrottle.Throttle.RequestsPerSecond = double.NaN;
        Check("a non-finite rate is rejected",
            badThrottle.Validate().Any(e => e.Contains("finite")));

        var badRetry = Valid();
        badRetry.Source.Retry.MaxBackoffSeconds = 0.1;
        badRetry.Source.Retry.InitialBackoffSeconds = 5;
        Check("a max backoff below the initial is rejected",
            badRetry.Validate().Any(e => e.Contains("MaxBackoffSeconds")));

        var badLimits = Valid();
        badLimits.Limits.MaxRecordsPerObject = -1;
        Check("a negative per-object cap is rejected",
            badLimits.Validate().Any(e => e.Contains("MaxRecordsPerObject")));

        var badDelta = Valid();
        badDelta.Source.Delta.OverlapMinutes = -1;
        Check("a negative overlap is rejected",
            badDelta.Validate().Any(e => e.Contains("OverlapMinutes")));

        var noObjects = Valid();
        noObjects.Objects = "";
        Check("an empty object selection is rejected",
            noObjects.Validate().Any(e => e.Contains("Objects is required")));

        var noRegistry = Valid();
        noRegistry.ObjectsFile = "";
        Check("a missing registry path is rejected",
            noRegistry.Validate().Any(e => e.Contains("ObjectsFile is required")));

        var badTimeout = Valid();
        badTimeout.Source.RequestTimeoutSeconds = 0;
        Check("a zero request timeout is rejected",
            badTimeout.Validate().Any(e => e.Contains("RequestTimeoutSeconds")));

        // Derived endpoints. The PM host differs from the DAM one, which is the single
        // easiest thing to get wrong when moving between the two tools.
        var endpoints = Valid();
        Eq("the API base is derived without a .dam segment",
            "https://acme.aprimo.com/api", endpoints.Aprimo.ResolvedApiBaseUrl);
        Eq("the token endpoint is derived", "https://acme.aprimo.com/login/connect/token",
            endpoints.Aprimo.ResolvedTokenUrl);

        endpoints.Aprimo.ApiBaseUrl = "https://acme.aprimo.com/api/";
        Eq("a trailing slash on an explicit base is trimmed",
            "https://acme.aprimo.com/api", endpoints.Aprimo.ResolvedApiBaseUrl);

        Check("API-VERSION is not sent by default", endpoints.Aprimo.ApiVersion is null);
    }

    // ───────────────────────────── M2. local configuration ─────────────────────────────

    /// <summary>
    /// <c>appsettings.local.json</c>: a customer's settings, layered over the shipped file.
    ///
    /// <para>It exists so the shipped <c>appsettings.json</c> can stay generic. Without it the
    /// tenant gets edited into the shipped file, and from there into whatever is copied to the
    /// next customer — and, under <c>dotnet run</c>, into a copy in <c>bin\</c> that every
    /// rebuild overwrites.</para>
    /// </summary>
    private static void LocalConfiguration(string scratch)
    {
        Section("Local configuration");

        const string shipped = """
        {
          // comments and trailing commas are tolerated, as in the real file
          "Aprimo": { "Tenant": "", "Scope": "api", "RefreshSkewSeconds": 180 },
          "Source": { "Paging": { "PageSize": 100, "MaxPages": 0 }, "Retry": { "MaxAttempts": 5 } },
          "Throttle": { "RequestsPerSecond": 5.0 },
          "Output": { "UserFields": [ "a", "b", "c" ], "Directory": "./export" },
        }
        """;

        static JsonElement Pick(JsonDocument doc, params string[] path)
        {
            var current = doc.RootElement;
            foreach (var key in path) current = current.GetProperty(key);
            return current;
        }

        // ---- merging ----
        using (var merged = JsonDocument.Parse(ConfigLoader.MergeJson(shipped,
                   """{ "Aprimo": { "Tenant": "acme" } }""")))
        {
            Eq("an override replaces the setting it names", "acme", Pick(merged, "Aprimo", "Tenant").GetString());
            Eq("and leaves its siblings alone", "api", Pick(merged, "Aprimo", "Scope").GetString());
            Eq("and every other section", 5, Pick(merged, "Source", "Retry", "MaxAttempts").GetInt32());
        }

        using (var nested = JsonDocument.Parse(ConfigLoader.MergeJson(shipped,
                   """{ "Source": { "Paging": { "PageSize": 250 } } }""")))
        {
            Eq("a deeply nested override applies", 250, Pick(nested, "Source", "Paging", "PageSize").GetInt32());
            Eq("without disturbing a sibling at the same depth", 0,
                Pick(nested, "Source", "Paging", "MaxPages").GetInt32());
            Eq("or a neighbouring branch", 5, Pick(nested, "Source", "Retry", "MaxAttempts").GetInt32());
        }

        // The loader binds property names without regard to case, so the merge has to as
        // well — or "tenant" would sit beside "Tenant" and which one won would be arbitrary.
        using (var cased = JsonDocument.Parse(ConfigLoader.MergeJson(shipped,
                   """{ "aprimo": { "tenant": "acme" } }""")))
        {
            Eq("keys match without regard to case", "acme", Pick(cased, "Aprimo", "Tenant").GetString());
            Eq("rather than appearing a second time", 1,
                Pick(cased, "Aprimo").EnumerateObject().Count(p =>
                    p.Name.Equals("tenant", StringComparison.OrdinalIgnoreCase)));
            Check("or creating a second section",
                cased.RootElement.EnumerateObject().Count(p =>
                    p.Name.Equals("aprimo", StringComparison.OrdinalIgnoreCase)) == 1);
        }

        // A list that was unioned with the shipped one would leave no way to remove an entry.
        using (var list = JsonDocument.Parse(ConfigLoader.MergeJson(shipped,
                   """{ "Output": { "UserFields": [ "x" ] } }""")))
        {
            Eq("a list replaces the shipped list rather than adding to it", "x",
                string.Join(",", Pick(list, "Output", "UserFields").EnumerateArray().Select(e => e.GetString())));
            Eq("and the other settings in that section survive", "./export",
                Pick(list, "Output", "Directory").GetString());
        }

        using (var added = JsonDocument.Parse(ConfigLoader.MergeJson(shipped, """{ "Objects": "projects" }""")))
            Eq("a key the shipped file lacks is added", "projects", Pick(added, "Objects").GetString());

        using (var tolerant = JsonDocument.Parse(ConfigLoader.MergeJson(shipped,
                   "{ // a note\n \"Aprimo\": { \"Tenant\": \"acme\", }, }")))
            Eq("comments and trailing commas in the override are tolerated", "acme",
                Pick(tolerant, "Aprimo", "Tenant").GetString());

        Throws<JsonException>("an override that is not an object is rejected",
            () => ConfigLoader.MergeJson(shipped, "[1, 2]"), "must contain a JSON object");
        Throws<JsonException>("so is a shipped file that is not",
            () => ConfigLoader.MergeJson("[1]", "{}"), "must be a JSON object");

        // ---- finding the file ----
        var first = Directory.CreateDirectory(Path.Combine(scratch, "local-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(scratch, "local-b")).FullName;
        var third = Directory.CreateDirectory(Path.Combine(scratch, "local-c")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(scratch, "local-empty")).FullName;

        File.WriteAllText(Path.Combine(second, ConfigLoader.LocalConfigFileName), "{}");
        File.WriteAllText(Path.Combine(third, ConfigLoader.LocalConfigFileName), "{}");

        Eq("the first directory holding the file wins", Path.Combine(second, ConfigLoader.LocalConfigFileName),
            ConfigLoader.FindLocalOverride(new[] { empty, second, third }));
        Eq("and order decides it", Path.Combine(third, ConfigLoader.LocalConfigFileName),
            ConfigLoader.FindLocalOverride(new[] { third, second }));
        Check("no file anywhere finds nothing", ConfigLoader.FindLocalOverride(new[] { empty, first }) is null);
        Check("a null or blank directory is skipped, not an error",
            ConfigLoader.FindLocalOverride(new string?[] { null, "", "  ", second }) is not null);
        Check("no directories at all finds nothing", ConfigLoader.FindLocalOverride(Array.Empty<string?>()) is null);

        // The template is named so as not to match, or copying it unedited would count.
        File.WriteAllText(Path.Combine(first, "appsettings.local.example.json"), "{}");
        Check("the example template is never mistaken for the real file",
            ConfigLoader.FindLocalOverride(new[] { first }) is null);

        // ---- loading ----
        var basePath = Path.Combine(scratch, "base-config.json");
        var overlayPath = Path.Combine(scratch, "overlay-config.json");

        File.WriteAllText(basePath, shipped);
        File.WriteAllText(overlayPath, """
        {
          // a customer's file
          "Aprimo": { "Tenant": "acme" },
          "Source": { "Paging": { "PageSize": 250 } },
          "Output": { "UserFields": [ "projectManager" ] }
        }
        """);

        var log = new List<string>();
        var config = ConfigLoader.LoadFile(basePath, overlayPath, log.Add);

        Eq("a loaded override reaches the typed config", "acme", config.Aprimo.Tenant);
        Eq("including nested settings", 250, config.Source.Paging.PageSize);
        Eq("and replaced lists", "projectManager", string.Join(",", config.Output.UserFields));
        Eq("while shipped values the override did not name stay", 5.0, config.Throttle.RequestsPerSecond);
        Eq("as do code defaults the shipped file did not set either", 0, config.Source.Paging.MaxPages);
        Check("the override is announced, so it is clear which file a setting came from",
            log.Any(l => l.StartsWith("Applied local overrides from", StringComparison.Ordinal)));

        log.Clear();
        var plain = ConfigLoader.LoadFile(basePath, null, log.Add);
        Eq("with no override the shipped value stands", 100, plain.Source.Paging.PageSize);
        Check("and nothing is announced", !log.Any(l => l.Contains("local overrides")));

        var overlayOnly = ConfigLoader.LoadFile(null, overlayPath, Noop);
        Eq("an override with no shipped file still applies", "acme", overlayOnly.Aprimo.Tenant);
        Eq("over the code's own defaults", 5.0, overlayOnly.Throttle.RequestsPerSecond);

        Eq("with neither, defaults apply", "", ConfigLoader.LoadFile(null, null, Noop).Aprimo.Tenant);

        // A config named with --config is taken exactly as given, even with a local file
        // sitting beside it.
        var explicitDirectory = Directory.CreateDirectory(Path.Combine(scratch, "explicit")).FullName;
        var explicitConfig = Path.Combine(explicitDirectory, "named.json");
        File.WriteAllText(explicitConfig, shipped);
        File.WriteAllText(Path.Combine(explicitDirectory, ConfigLoader.LocalConfigFileName),
            """{ "Aprimo": { "Tenant": "should-not-apply" } }""");

        var explicitLog = new List<string>();
        var explicitLoaded = ConfigLoader.Load(
            CommandLineArgs.Parse(new[] { "--config", explicitConfig }), explicitLog.Add);

        Check("an explicit --config is not layered over",
            !explicitLog.Any(l => l.Contains("local overrides")) &&
            explicitLoaded.Aprimo.Tenant != "should-not-apply");

        // ---- errors name the file at fault ----
        var badOverlay = Path.Combine(scratch, "bad-overlay.json");
        File.WriteAllText(badOverlay, "{ not json");
        Throws<InvalidOperationException>("an override that is not valid JSON is reported by name",
            () => ConfigLoader.LoadFile(basePath, badOverlay, Noop), "bad-overlay.json");

        var badBase = Path.Combine(scratch, "bad-base.json");
        File.WriteAllText(badBase, "{ not json");
        Throws<InvalidOperationException>("so is a shipped file that is not",
            () => ConfigLoader.LoadFile(badBase, overlayPath, Noop), "bad-base.json");

        var wrongType = Path.Combine(scratch, "wrong-type.json");
        File.WriteAllText(wrongType, """{ "Source": { "Paging": { "PageSize": "many" } } }""");
        Throws<InvalidOperationException>("a value of the wrong type points at the override that holds it",
            () => ConfigLoader.LoadFile(basePath, wrongType, Noop), "wrong-type.json");

        var wrongEnum = Path.Combine(scratch, "wrong-enum.json");
        File.WriteAllText(wrongEnum, """{ "Source": { "Paging": { "Mode": "Bogus" } } }""");
        Throws<InvalidOperationException>("an unknown option name is reported rather than ignored",
            () => ConfigLoader.LoadFile(basePath, wrongEnum, Noop), "could not be read");

        // ---- the files that actually ship ----
        var shippedPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var examplePath = Path.Combine(AppContext.BaseDirectory, "appsettings.local.example.json");

        if (!File.Exists(shippedPath) || !File.Exists(examplePath))
        {
            Console.WriteLine("   skipped — the shipped config or its example is not beside the test binary.");
            return;
        }

        var fromExample = ConfigLoader.LoadFile(shippedPath, examplePath, Noop);
        Eq("the example sets a tenant, to show where it goes", "your-tenant", fromExample.Aprimo.Tenant);
        Check("but one that validation rejects, so the example cannot be used unedited",
            fromExample.Aprimo.ValidateEndpoints().Any(e => e.Contains("placeholder")));
        Eq("and changes nothing else about the shipped settings", 100, fromExample.Source.Paging.PageSize);
    }

    // ───────────────────────────── N. manifest ─────────────────────────────

    private static void Manifest(string scratch)
    {
        Section("Manifest");

        var directory = Path.Combine(scratch, "manifest");
        var config = new ExportConfig { Output = { Directory = directory, WriteManifest = true } };

        var result = new RunResult
        {
            Objects = new[]
            {
                new ObjectExportResult
                {
                    Object = "projects", Outcome = ObjectOutcome.Succeeded,
                    RowsWritten = 120, FilesCreated = 1, PagesFetched = 2,
                    ApproximateTotalAvailable = 130, WindowComplete = true,
                    FilePaths = new[] { Path.Combine(directory, "pm-projects_0001.csv") }
                },
                new ObjectExportResult
                {
                    Object = "teams", Outcome = ObjectOutcome.Skipped,
                    Message = "no modified-date property, so a window cannot be applied"
                },
                new ObjectExportResult
                {
                    Object = "invoices", Outcome = ObjectOutcome.Failed,
                    Message = "403 Forbidden, comma, and \"quotes\""
                }
            },
            Elapsed = TimeSpan.FromSeconds(12)
        };

        var path = ManifestWriter.Write(config, result, DateTimeOffset.UtcNow, Noop);

        Check("the manifest is written", path is not null && File.Exists(path));

        var rows = ReadCsv(path!);
        Eq("there is a header plus one row per object", 4, rows.Count);
        Eq("the first column is the object", "Object", rows[0][0]);
        Eq("a successful object is recorded", "projects", rows[1][0]);
        Eq("with its outcome", "Succeeded", rows[1][1]);
        Eq("and its row count", "120", rows[1][2]);
        Eq("a skipped object is recorded", "Skipped", rows[2][1]);
        Eq("a failed object is recorded", "Failed", rows[3][1]);

        // The message contains a comma and quotes, so quoting must survive a round trip.
        Eq("a message with commas and quotes round-trips",
            "403 Forbidden, comma, and \"quotes\"", rows[3][11]);

        Eq("aggregates are computed", 120, result.TotalRows);
        Eq("succeeded objects are counted", 1, result.Succeeded);
        Eq("skipped objects are counted", 1, result.Skipped);
        Eq("failed objects are counted", 1, result.Failed);

        config.Output.WriteManifest = false;
        Check("the manifest can be turned off",
            ManifestWriter.Write(config, result, DateTimeOffset.UtcNow, Noop) is null);
    }

    // ───────────────────────────── O. shipped files ─────────────────────────────

    /// <summary>
    /// Structural checks on what ships beside the executable. Deliberately structural
    /// only: these files are meant to be edited for a real tenant, and the suite must not
    /// break when they are.
    /// </summary>
    private static void ShippedFiles()
    {
        Section("Shipped files");

        var directory = AppContext.BaseDirectory;

        var registryPath = Path.Combine(directory, "objects.json");
        if (!File.Exists(registryPath))
        {
            Console.WriteLine("   skipped — objects.json is not beside the test binary.");
            return;
        }

        var catalog = ObjectCatalog.Load(registryPath, Noop);

        Check("the shipped registry validates", !catalog.Validate().Any());
        Check("it defines objects", catalog.Objects.Count > 0);
        Check("'all' resolves without error", catalog.Resolve("all").Count > 0);

        foreach (var pmObject in catalog.Objects)
        {
            Check($"'{pmObject.Name}' has a path starting with '/'", pmObject.Path.StartsWith('/'));
            Check($"'{pmObject.Name}' names an ID property",
                !string.IsNullOrWhiteSpace(pmObject.IdProperty));
            Check($"'{pmObject.Name}' has a metadata resource for field discovery",
                !string.IsNullOrWhiteSpace(pmObject.MetadataResource));
            Check($"'{pmObject.Name}' has a usable fields file name",
                pmObject.FieldsFileName.EndsWith(".txt", StringComparison.Ordinal) &&
                pmObject.FieldsFileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        // The registry is the record of two facts from the spec worth not losing.
        Check("exactly one object declares a server-side changed-since parameter",
            catalog.Objects.Count(o => o.ModifiedAfterParam is not null) == 1);
        Check("and it is review-tasks",
            catalog.Objects.Single(o => o.ModifiedAfterParam is not null).Name == "review-tasks");
        Check("budgets is flagged as epoch-stamped",
            catalog.Find("budgets") is { ModifiedIsEpoch: true });

        var configPath = Path.Combine(directory, "appsettings.json");
        if (File.Exists(configPath))
        {
            var config = JsonSerializer.Deserialize<ExportConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                });

            Check("the shipped config parses", config is not null);

            Check("it ships with no credentials in it",
                string.IsNullOrEmpty(config!.Aprimo.ClientId) &&
                string.IsNullOrEmpty(config.Aprimo.ClientSecret));

            // The shipped file is generic; a customer's tenant belongs in
            // appsettings.local.json. If this fails, someone has configured the file that
            // ships — which is how one customer's tenant ends up in a copy for the next.
            Check("it ships with no tenant — a customer's settings belong in appsettings.local.json",
                string.IsNullOrEmpty(config.Aprimo.Tenant));
            Check("the page size is within the sanity ceiling",
                config.Source.Paging.PageSize is > 0 and <= PagingConfig.SafePageSizeCeiling);
            Check("it points at the object registry",
                !string.IsNullOrWhiteSpace(config.ObjectsFile));
        }

        var fieldsDirectory = Path.Combine(directory, "fields");
        if (!Directory.Exists(fieldsDirectory)) return;

        foreach (var file in Directory.GetFiles(fieldsDirectory, "*.txt"))
        {
            var name = Path.GetFileName(file);
            List<FieldMapping> mappings;

            try
            {
                mappings = FieldListLoader.Load(file, null, Noop);
            }
            catch (Exception ex)
            {
                Check($"the shipped '{name}' loads", false, ex.Message);
                continue;
            }

            Check($"the shipped '{name}' has columns", mappings.Count > 0);

            var duplicates = mappings.Select(m => m.Column)
                                     .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                                     .Where(g => g.Count() > 1)
                                     .Select(g => g.Key)
                                     .ToList();

            Check($"'{name}' has no duplicate columns", duplicates.Count == 0,
                string.Join(", ", duplicates));

            foreach (var mapping in mappings)
            {
                try
                {
                    CompiledPath.Compile(mapping.Path);
                }
                catch (FormatException ex)
                {
                    Check($"'{name}' column '{mapping.Column}' compiles", false, ex.Message);
                }
            }

            _passed++;
        }
    }
}
