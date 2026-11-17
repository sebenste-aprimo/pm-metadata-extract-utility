# Aprimo PM (Productivity & Spend) → CSV export utility

A C# console utility that pages through the Aprimo PM REST API and writes **one CSV file
set per object**. Works against any Aprimo tenant — set `Aprimo.Tenant` and supply a
client-credentials registration.

Built to the same technical specification as the DAM
[Aprimo Metadata Extract Utility](../DAM_Metadata_Extrat_Utility): controllable request
rate, rolling output files, row caps, resumable runs, incremental (changed-since)
exports, DPAPI-encrypted credentials, and a column list you can edit or generate.

One NuGet dependency: `System.Security.Cryptography.ProtectedData`, which backs the
credential store. `System.Text.Json` ships with the framework and the CSV writer is
hand-rolled, so nothing else is needed.

## Quick start

For any Aprimo tenant. Everything specific to *your* tenant lives in two places that never
ship: `appsettings.local.json` (your settings) and the Windows credential store (your secret).
The files that ship stay generic.

1. **Create your settings file.** Copy `appsettings.local.example.json` to
   `appsettings.local.json` in the directory you run the tool from, and set your tenant — the
   subdomain of your Aprimo URL, so `acme` for `https://acme.aprimo.com`. Do not edit
   `appsettings.json`; see [Configuration](#configuration-your-settings-go-in-appsettingslocaljson).
2. **Save your credentials, once.** They are verified against your tenant before they are
   stored, and kept encrypted under your Windows account:

   ```bash
   dotnet run --project src/AprimoPmExport -- --save-credentials
   ```

3. **See what is available, then generate column lists from your own tenant's metadata:**

   ```bash
   dotnet run --project src/AprimoPmExport -- --list-objects
   ```

   ```bash
   dotnet run --project src/AprimoPmExport -- --objects projects --discover-fields
   ```

4. **Check what resolves before exporting anything:**

   ```bash
   dotnet run --project src/AprimoPmExport -- --objects projects --sample 25
   ```

5. **Export a small slice, look at it, then read [Before a full export](#before-a-full-export)**
   before running anything large.

Your tenant's configuration — its custom attributes, the lists behind its coded values, its
page-size limit — differs from every other tenant's, which is why steps 3 and 4 exist and why
no column list ships that was generated from a real one.

### Running without the SDK: the published folder

Every `dotnet run --project src/AprimoPmExport --` above can be replaced by the published
executable, which needs **no .NET installed**. Build it with:

```bash
.\publish.ps1
```

That runs the offline suite first (a failing suite publishes nothing), then writes
`dist\aprimo-pm-export-win-x64\`: one self-contained, compressed `aprimo-pm-export.exe`
(about 34 MB) beside the editable files — `appsettings.json`, `objects.json`, `fields\`,
`appsettings.local.example.json`, this README and `docs\`. Use `-Runtime win-arm64` for ARM.
The script refuses to finish if the output holds a local settings file, credential store,
checkpoint or delta state, or if the shipped `Tenant` is not blank.

To use it, copy the folder anywhere, then from the directory you want your exports and
`appsettings.local.json` to live in:

```bash
path\to\aprimo-pm-export.exe --save-credentials
```

```bash
path\to\aprimo-pm-export.exe --objects projects --sample 25
```

The settings files stay loose beside the exe on purpose: they are the part you are meant to
edit. Only Windows is supported for stored credentials (DPAPI); on another platform supply
`APRIMO_TENANT`, `APRIMO_CLIENT_ID` and `APRIMO_CLIENT_SECRET` as environment variables.
`dist\` is a build product and is listed in `.gitignore`.

## Status

Builds clean on .NET SDK 8.0.424 — Debug and Release, **0 warnings, 0 errors**.

```bash
dotnet build src/AprimoPmExport/AprimoPmExport.csproj -c Release
```

**1,068 offline checks, all passing** (876 of them without Aprimo's OpenAPI file, which is
not in the repository — see *Spec consistency* below). The suite lives in
[tests/AprimoPmExport.Tests](tests/AprimoPmExport.Tests) and needs nothing but the SDK —
no test framework to restore, no network, no tenant:

```bash
dotnet run --project tests/AprimoPmExport.Tests
```

Exit code 0 means everything passed. Fixtures are synthetic and tenant-agnostic, and the
only assertions made about the shipped `appsettings.json`, `objects.json` and `fields/`
are structural, so editing those for your own tenant will not break the suite. What it
covers:

- **Path grammar** — the `ea:` / `ea#` / `eamulti:` / `eamulti#` / `prop:` / `label:` /
  `user:` shorthands, fallback branches, and attribute names containing spaces and `#`
- **Extraction** — nulls, numeric zero, empty-string fallthrough, multi-value joins,
  wildcard walks, array indexes and `[key=value]` filters, separator collisions
- **Code-to-label resolution** — substitution per value before any join, a code with no
  label keeping the code rather than going blank, and the column set staying the same when
  resolution is off
- **User resolution** — the per-run cache (one request per distinct user, never repeated),
  negative results cached, non-numeric IDs never sent, one failing user costing one name
  rather than the export, and a check that none of the personal data in a user record
  (email, phone, address, employee number) is retained or printed
- **Field lists** — every separator, comment handling that survives `#` inside both a
  real attribute name and the `ea#123` syntax, per-object resolution and the shared
  fallback, column derivation from an observed item, and what `--discover-fields` emits
  for system fields, extended attributes, coded fields and user fields
- **Object registry** — loading, selection, `all` / `*`, disabled objects still being
  nameable, and every validation rule
- **HAL envelopes** — key inference, a pinned `EmbedKey` (a wrong one still reading a
  single array, but failing when several are present), empty collections in both shapes,
  `_links.next`, and responses that are not envelopes
- **The reader against a simulated server** — run through the real reader, API client and
  token provider, with a fake API that behaves as the live tenant did: the end of the
  collection inclusive, and a page-size cap applied silently. Covers an exact multiple of
  the page size, a short last page, an empty collection, a capped server, and a `_total`
  that is wrong. Checked by deliberately restoring the old behaviour: eight of these fail,
  the first with "expected 1000, got 250"
- **CSV output** — rolling parts, RFC 4180 quoting verified with a quote-aware reader,
  BOM, the formula-injection guard leaving negative numbers alone, all newline modes,
  and per-object file naming
- **Rate limiter** — measured pacing, bursts, disabled mode, `Retry-After` backpressure,
  the concurrency cap, and cancellation while waiting
- **Credential store** — DPAPI round-trip with a byte-level check that no secret appears
  in plaintext, multi-tenant isolation, gap-filling only, and graceful failure on an
  undecryptable file
- **Delta** — window parsing, half-open adjacency, epoch timestamps, per-object state
  lineage, and the sort-verification rules that decide whether paging may stop early
- **Probes** — the depth probe's verdicts for each failure mode (hard error, quiet wrong
  answer, slowdown, silent page-size cap), its time projection, and the user probe's
  in-list-versus-active reasoning
- **Local configuration** — the override file layered over the shipped one: nested merging
  without disturbing siblings, key matching regardless of case, lists replaced rather than
  unioned, the lookup order for finding the file, errors that name the file at fault, an
  explicit `--config` being taken as given, and the real shipped config and example merged
  to prove the example cannot be used unedited
- **Spec consistency** — read straight from Aprimo's `Productivity_and_Spend_OpenAPI_Specification.txt`.
  That file is **not included in this repository**: download it from Aprimo and place it in
  the repository root, or anywhere above the test binary, and these checks run. Without it
  this section and the next one print "skipped" and everything else still runs. Every one of
  the 27 registry entries has a `GET` with `offset`/`limit`, the right item schema, and ID,
  date and title properties that really exist on it; and every path in the three starter
  column lists resolves to a property of its schema, nested ones included
- **The whole pipeline, for every object** — a fake server built from the spec's own
  schemas serves 23 rows of schema-shaped data for each of the 27 objects, paged ten at a
  time, through the real reader, extractor and writer. Every object must export all 23 rows,
  every starter column must be populated, and sibling properties that are easy to confuse
  (a task's own ID beside its predecessor's, an assignment's ID beside the user's) are given
  different values so reading the wrong one fails. Checked by restoring the original
  mistakes: two checks fail
- **Guards** — every CLI switch, and validation that rejects placeholder credentials,
  duplicate columns and an invalid registry, with the tenant checked before any credential
  is asked for. The shipped `appsettings.json` is asserted to carry no tenant and no credentials

### Developed against one tenant

It was developed against a single production tenant, and the only collection it has read
there is `/projects`. **Your tenant may behave differently** — that is what the
[probes](#probes-read-only-commands-that-answer-one-question-each) are for. Observed there:

- Client-credentials auth, and the derived token endpoint, cross-checked against the
  tenant's OpenID discovery document
- The `_embedded` key for `/projects` is `Project` — the OpenAPI schema name, which the spec
  never states
- `GET /metadata/{resourceName}` answers with the singular lowercase name (`project`); 35
  fields
- Extended-attribute names are populated on read (not null), and `modifiedDate` parses
- Code-to-label resolution: `projectStatus` 4 → "Closed", `workflowId`, the 0/1 flags, and
  an extended attribute's pick-list value, on every row that had a code out of the first
  1,000
- User-name resolution through `GET /users/{id}`: `projectManager` and `modifiedUser`, 1,000
  of 1,000 rows each, inactive users included
- **Offset paging works to the end of a collection of about 536,000 projects.** Nine depths from offset 0 to the
  last page, at 100 and at 250 per page, all returned full pages in order with no repeats,
  and latency stayed flat: about 0.8–2 s per page, with a single 4.5 s read that was a
  hiccup rather than a trend
- **`limit` is silently capped at 250**, with a 200 and no warning
- **`sortField=projectId` is honoured** (ascending), with IDs rising monotonically at every
  depth

**Not yet verified, and the next things to do:**

- **A full export.** Nothing larger than 1,000 rows has been written. See
  [Before a full export](#before-a-full-export).
- **A `--since` / `--daily` run.** The delta machinery is tested offline only. In particular
  it pages **newest-first**, and only ascending sorts have been tried against the live API.
- **Any object other than `projects`.** Their `_embedded` keys, ID and date properties and
  metadata resource names are inferred from the spec, and `--sample` is how to confirm them.
- **`LinkFollow` paging** and **`Source.HydrateFromDetailEndpoint`**, neither of which has
  been needed.

See [What has been verified](#what-has-been-verified-and-what-has-not) for the whole list.

## What is different from the DAM utility

Both tools do the same job, and the auth, throttling, CSV writing and credential storage
are the same code. The API underneath is not the same shape at all, and the differences
are not cosmetic:

| | DAM | PM (this tool) |
|---|---|---|
| API host | `{tenant}.dam.aprimo.com/api/core` | `{tenant}.aprimo.com/api` |
| Token endpoint | `{tenant}.aprimo.com/login/connect/token` | **identical** |
| Objects | one (`record`) | **27 collections**, each a different schema |
| Collection shape | `items[]` + `totalCount` | HAL: `_embedded.<key>[]`, `_total`, `_links` |
| Paging | `skip`/`take`, `page`/`pageSize`, **keyset** | `offset`/`limit` — **no keyset available** |
| Page size ceiling | documented: 1000 | **not documented; observed: 250, applied silently** |
| Sort | `sort=Field`, `-` for descending | `sortField` + `sortAscending` boolean |
| Filtering | full search-expression language | **none** — ad-hoc query params only |
| Delta window | pushed server-side as a predicate | **applied client-side** |
| Field discovery | `GET /fielddefinitions` | `GET /metadata/{resourceName}` |
| Tenant metadata | `fields.items[].localizedValues[]` | `extendedAttributes[]` + `multipleValueExtendedAttributes[]` |
| Coded values | labels returned inline | **integer codes**; labels need separate lookups |
| Localisation | `languages` header, language IDs | not exposed by this API |
| Sub-resource embedding | `select-record*` headers | no headers declared at all |
| Column list | one `fields.txt` | one file **per object** |

[docs/api-notes.md](docs/api-notes.md) records what the OpenAPI document actually says,
what a live tenant did instead, and where the spec is silent.

## Configuration: your settings go in `appsettings.local.json`

There are two config files, and they have different jobs:

| File | Ships? | Holds |
|---|---|---|
| `appsettings.json` | yes | generic defaults, documented setting by setting |
| `appsettings.local.json` | **never** | *your* settings: your tenant, and anything you want to change |

`appsettings.local.json` is layered over `appsettings.json`. It replaces only the settings it
names, so a file containing nothing but your tenant is a complete configuration. Start from
the template:

```
copy appsettings.local.example.json appsettings.local.json
```

Precedence, lowest to highest: `appsettings.json` < `appsettings.local.json` < environment
variables < command line.

Two rules for overrides:

- **Objects merge setting by setting.** Naming one key under `Source` leaves the rest of
  `Source` alone, and keys match without regard to case.
- **A list replaces the shipped list; it does not add to it.** If you set `UserFields`, write
  all of them. A union with the default would leave no way to remove an entry.

The file is found in the current directory first, then beside `appsettings.json`, then beside
the executable. A config named with `--config` is taken exactly as given, and nothing is
layered over it. The log says which file each run applied.

**Why not just edit `appsettings.json`?** Three reasons, all of which cost time while this was
being built. It ships, so a tenant edited into it travels to whoever is given the next copy.
Under `dotnet run` the config is read from a copy in `bin\` that every build overwrites, so an
edit made there is silently lost. And `.gitignore` covers `appsettings.local.json`, the
exports and your generated column lists, so none of your tenant's data is committed by
accident. A test fails if the shipped file ever carries a tenant.

To confirm your tenant name, fetch its OpenID discovery document, which is public:

```
https://{tenant}.aprimo.com/login/.well-known/openid-configuration
```

If your environment uses different hosts — a sandbox, say, or a regional deployment — set
`Aprimo.ApiBaseUrl` and `Aprimo.TokenUrl` explicitly instead of a tenant.

## How to run it

### First time

```bash
dotnet run --project src/AprimoPmExport -- --save-credentials
```

```bash
dotnet run --project src/AprimoPmExport -- --list-objects
```

```bash
dotnet run --project src/AprimoPmExport -- --objects projects --discover-fields
```

The tenant is checked **before** you are asked for a client ID or secret, so a missing or
placeholder tenant fails immediately rather than after you have typed credentials that could
never have worked.

Then check what actually resolves before exporting anything:

```bash
dotnet run --project src/AprimoPmExport -- --objects projects --sample 25
```

A small export first:

```bash
dotnet run --project src/AprimoPmExport -- --objects projects --max-rows 1000 --rps 2 --overwrite
```

To publish a self-contained folder like the DAM utility's `dist/`:

```bash
dotnet publish src/AprimoPmExport -c Release -o dist
```

### Where files are read from

The exe treats the `appsettings.json` **next to it** as its shipped config. Your
`appsettings.local.json`, `objects.json` and `fields/` are each found by looking in the
**current directory first**, then beside the config, then beside the executable.

Under `dotnet run` the shipped config sits in `bin\Debug\net8.0\`, so the only `fields\` that
exists there is a copy of the shipped starters. That is why `--discover-fields` refuses to
write into a build-output directory and writes to `.\fields\` in the directory you ran it from
instead: that location is visible, survives a clean, and is the first place the reader looks
next time. **Run from the project root** and your local config and column lists are found.

### Probes: read-only commands that answer one question each

None of these writes a CSV. Run them before relying on what they cover.

| Command | Answers | Cost |
|---|---|---|
| `--validate-only` | Do the config and credentials work? | one token request |
| `--sample [n]` | What does each object return, and which of my columns resolve? | one request per object |
| `--probe-lookups` | Which fields hold codes, and what does each lookup endpoint return? | a request per list the object declares |
| `--probe-users <ids>` | Can user IDs be resolved, and why does the user lookup list fall short? | one per ID, plus a dozen sampled from the list |
| `--probe-depth` | Does offset paging hold to the end? What page size does the server allow? | about 15 requests |
| `--selection-list <name>` | What are the items in a named selection list? | one request |

`--sample` shows codes where a `label:` or `user:` column would resolve at export time: it
does not run the resolvers, so those columns appear as if they had worked. Use
`--probe-lookups` and `--probe-users` to see resolution, or look at a small export.

### Not retyping credentials every run

**Best option — save them encrypted, once:**

```bash
aprimo-pm-export.exe --save-credentials
```

Prompts for the ID and secret, **verifies them against the token endpoint**, and only
then stores them encrypted with Windows DPAPI under
`%LOCALAPPDATA%\AprimoPmExport\credentials.dat`. A mistyped secret is never persisted.

DPAPI protection is scoped to your Windows account on this machine — nobody else can
decrypt the file, and copying it elsewhere yields nothing. The file lives outside the
project tree so it cannot be committed. Entries are keyed by tenant, so several tenants
coexist.

```bash
aprimo-pm-export.exe --list-credentials
```

```bash
aprimo-pm-export.exe --clear-credentials
```

`--list-credentials` never prints secrets. `--clear-credentials` removes the current
tenant's entry; add `--all` to wipe the store.

**This store is separate from the DAM utility's** — different directory, different
encryption entropy. A PM registration and a DAM registration are different registrations
even on the same tenant, so sharing one entry would make the two tools fight over it.

The store only ever **fills gaps** — a value from the config file, environment or command
line always wins, so a saved credential can never silently shadow an explicit one.

**Alternative — environment variables:**

```bash
setx APRIMO_CLIENT_ID <your-client-id>
```

```bash
setx APRIMO_CLIENT_SECRET <your-client-secret>
```

> Substitute your real values. Pasting these unchanged sets the credential to the literal
> text `<your-client-id>`, which then **silently overrides the config file** — the trap
> described below. Known placeholders are rejected on sight, but the cleanest fix is not
> to create them.

`setx` does not affect the session you run it in — **open a new terminal.**

> **Precedence trap.** Order is file → environment → command line, so the environment
> **silently overrides** the config file. A stale or placeholder `APRIMO_CLIENT_ID` wins
> over `appsettings.json` and produces `invalid_client` with no obvious cause. It is
> doubly quiet because the interactive prompt only asks for values that are *missing* —
> so a wrong ID already in the environment rides along while you type the real secret.
> Check with `[Environment]::GetEnvironmentVariable('APRIMO_CLIENT_ID','User')` before
> trusting the config file.

The registration must have its **OAuth Flow Type set to Client Credential**, and grant
the **`api`** scope. A PKCE registration is a public client with no secret, so presenting
one is rejected as `invalid_client` with no more specific explanation. Registration
changes take **up to 15 minutes** to propagate.

**`invalid_client` from a credential that used to work** is a different problem, and the
error's own checklist mostly does not apply to it: the flow type, scope, propagation delay
and secret length were all fine when `--save-credentials` verified the credential. What
changes over time is the registration itself — the secret was rotated or has expired, or the
registration was disabled or deleted. Look it up under **Administration > Integration >
Registrations**, then run `--save-credentials` again, which replaces the stored entry.

Note the token host is the same for PM and DAM, so **one registration can serve both
tools** provided it has the `api` scope.

## Objects

Everything the utility can export lives in [objects.json](src/AprimoPmExport/objects.json),
one entry per top-level collection in the API. Start here:

```bash
aprimo-pm-export.exe --list-objects
```

```
  NAME                     PATH                           DELTA          EA   STATUS
  activities               /activities                    modifiedDate   yes
  projects                 /projects                      modifiedDate   yes
  tasks                    /tasks                         modifiedDate   yes  user-scoped
  review-tasks             /review-tasks                  modifiedDate   yes  server-side delta
  …
  integration-logs         /integration-logs              (none)         no   disabled — name it explicitly
```

- **DELTA** — the property a `--since` window compares. `(none)` means the schema carries
  no modified stamp, so that object is full-export only.
- **EA** — whether the schema carries tenant-defined extended attributes.

Select with `--objects activities,projects,tasks` or `--all-objects`. There is deliberately
no short alias for `--objects`: switch matching is case-insensitive, so `-O` would collide
with the `-o` that means `--out`. A disabled object is excluded from `--all-objects` but can
always be named explicitly — being able to exclude it is the reason it has a name.

Each object writes its own file set, `{prefix}-{object}_0001.csv`, plus a shared
`manifest.csv` summarising the run. Objects export sequentially and share one rate
limiter, so a 24-object run stays inside exactly the same request ceiling as a
single-object one.

### Activities and tasks

Both are in the registry, enabled by default, and have a hand-written starter column list
(`fields/activities.txt`, `fields/tasks.txt`). What is and is not established:

- **Established, offline:** the registry entries and every column path were checked against
  the spec's `Activity` and `AssignmentTask` schemas, and the full pipeline — paging, column
  extraction, CSV — runs for both against schema-shaped data. That check found and fixed
  real faults in the starter lists (a predecessor column that read the task's own ID, an
  assignee column that read the assignment's ID).
- **Not established, live:** the `_embedded` key each returns, the metadata resource names
  (`activity`, `task`), whether `sortField` is honoured on them, and — for tasks — what
  `/tasks` returns at all, since it is user-scoped (below). The reader falls back to the sole
  array under `_embedded` if the key differs, so a wrong guess there should not stop an export,
  but that is a design intention, not something observed.

An object with no list of its own gets the shared `_default.txt` plus the object's own ID as
the first column, so a bare export is always keyed.

To find out on your tenant, using the exe or `dotnet run` form as above:

```bash
aprimo-pm-export.exe --objects activities,tasks --sample 25
```

```bash
aprimo-pm-export.exe --objects activities,tasks --discover-fields
```

`--sample` prints the `_embedded` key it actually found, whether the ID property is present,
and which columns came back empty, for each object; and
`--discover-fields` writes lists from your tenant's metadata, extended attributes included.

**Only `projects` has been exercised against a live tenant.** For every other object the
registry's `EmbedKey`, ID and date properties and metadata resource name come from the spec.
The `_embedded` key for `/projects` turned out to be its OpenAPI schema name (`Project`), so
the likely pattern is that each object's key is its `Schema` value in the registry — but that
is one data point. `--sample` prints the key it actually found for each object.

**One object failing does not abandon the rest.** An unlicensed or permission-denied
collection is a normal outcome across two dozen objects; the run continues and exits 4
so a scheduler can tell "partial" from "fine". Set
`Limits.ContinueOnObjectFailure: false` to stop at the first failure instead.

### Objects scoped to the calling user

`GET /tasks` is documented as returning **"the current user's tasks"**, so what it
exports depends on the registration's own assignments rather than covering the tenant.
The utility flags this at run time, but it cannot work around it: for a tenant-wide task
export you need `/projects/{projectId}/tasks`, which means exporting projects first and
then walking them. That is a per-parent traversal this tool does not currently do — the
registry only covers top-level collections.

More generally, **PM applies per-object access lists**. An integration user with no
memberships sees empty collections rather than an error, so "0 rows" and "no permission"
look identical from here. If an object comes back empty and you expect data, check it
with a user who can see it in the UI before assuming the export is broken. `--probe-users`
prints who the registration runs as (`GET /users/me`), which is the place to start.

## Columns

Each object gets its own column list, `fields/{object}.txt`. Unlike the DAM utility's
single `fields.txt`, there has to be one per object: PM's objects share almost no
properties, so one shared list would be wrong for all of them at once.

### Generating them

```bash
aprimo-pm-export.exe --objects projects --discover-fields
```

This combines two sources, because neither alone is enough:

1. **`GET /metadata/{resourceName}`** — the authoritative field list for that object on
   that tenant, including extended attributes no schema in the spec can know about.
2. **A sampled item** — which shows the properties the collection endpoint *really*
   returns, as opposed to what the metadata definition says exists.

Existing files are skipped unless you pass `--overwrite`, so regenerating never silently
discards edits. Files are written to `.\fields\` — see
[Where files are read from](#where-files-are-read-from).

**System fields versus extended attributes.** The metadata definition lists both in one
array. On the tenant this was developed against, all 35 fields of `/metadata/project` were
*system* fields — qualified names like `Project.ProjectId` and a **negative**
`amsAttributeId` — plus that tenant's own custom attributes, which had real positive IDs. The
generator tells them apart by those two signals and maps a system field to its property
(`prop:projectId`) and an attribute to the extended-attribute array. Treating them all as
attributes produced 26 columns that could never resolve.

**Code and label columns.** For a coded field the generator writes both the code and a
`…Label` column beside it, annotated with where the label comes from:

```
projectStatus      => prop:projectStatus     # CODE -> lookup list <id>
projectStatusLabel => label:projectStatus    # resolved from lookup <id>
```

The label is what a person reads; the code is what joins to other systems and what survives a
label being renamed. Delete whichever you do not want, or set
`Output.GenerateCodeAndLabelColumns` to `false`.

### The format

Names separated by newlines, commas, semicolons or tabs, mixed freely:

| Form | Meaning |
|---|---|
| `title` | the item property, falling back to an extended attribute of that name |
| `prop:title` | the item property only, no fallback |
| `ea:Business Unit` | a single-valued extended attribute, by name |
| `ea#4102` | the same attribute by ID |
| `eamulti:Channels` | a multi-valued (pick-list) attribute |
| `eamulti#4110` | the same, by ID |
| `label:projectStatus` | a coded value, resolved to its label — see [Coded values](#coded-values-codes-versus-labels) |
| `user:projectManager` | a user ID, resolved to a name through `/users/{id}` |
| `Column => path` | an explicit mapping |
| `a \|\| b \|\| c` | fallback branches — the first that yields a value wins |
| `#` or `//` | comment — **only** at line start or after whitespace |

`label:` and `user:` wrap an ordinary path, so they compose with the rest: `label:ea:Region`
resolves an extended attribute, and `label:eamulti:Channels` resolves each value of a
multi-value one.

Path grammar for explicit mappings: `a.b` walks objects, `[0]` indexes an array, `[*]`
takes all elements joined by `MultiValueSeparator`, `[key=value]` filters.

Column headers follow Aprimo's own `outputName` rule: whitespace becomes `_`, and
anything outside `[A-Za-z0-9_]` is dropped — the same rule the DAM utility uses, so a
downstream consumer sees consistent headers from both.

**Why a bare name means "property, then attribute".** DAM has one namespace for tenant
metadata, so a bare name there is unambiguously a metadata field. PM has two — the item's
own schema properties, and extended attributes — and nothing in a name says which. Trying
the property first and falling back means a pasted list of either kind works, and an
unknown name gives an empty cell rather than an error. Use `prop:` or `ea:` when a tenant
has the same name in both namespaces and the choice matters.

The comment rule is narrow for a reason: attribute names contain `#` in practice
(`Photo#1`), and `#` is also the `ea#123` syntax. Treating every `#` as a comment would
break both.

### Extended attributes: names or IDs?

`attributeName` is marked `readOnly` in the spec, which means "not writable by the
client" — it does **not** guarantee the API populates it on a read. If a tenant returned it
null, every `ea:Name` mapping would resolve to an empty cell, silently.

**On the tenant this was developed against it was populated**, so name-based mappings worked.
`--sample` answers it for yours:

```
  ── Extended attributes on these items ──
    47 value(s): 45 named, 2 with a null attributeName.
      ea:Business Unit   x12
      …
    2 attribute(s) came back with attributeName null, so only an ID-based mapping
    reaches them:
      ea#4104   x2
```

`--discover-fields` writes both forms as a fallback chain (`region || ea:Region ||
ea#4102`) so the column works either way. Once `--sample` shows which branch hits, pin it
— a future change then shows up as an empty column rather than silently sliding to
another source.

**The names may be internal names.** On the tenant this was developed against, custom
attributes carried names of the form `extrAttr<N>` rather than readable captions. The
human-readable caption lives behind `titleResourceId`, a resource key into a table this API
does not expose, so there is no way to fetch it. If yours are like that, give a column a
meaningful header yourself:

```
Review_Required => ea:<your attribute's internal name>
```

## Coded values: codes versus labels

Most numeric columns in a PM export are **codes**, not values: `projectStatus` 4, `timeZoneId`
43, a pick-list value of 663. The API does expose the lists behind many of them, through four
endpoints, and `GET /metadata/{resourceName}` says which one each field uses — so nothing is
hard-coded per tenant, and a field added next month resolves with no code change.

A `label:` column asks the resolver for a field's label. It fetches each list **once per
object** and reuses it for every row, so resolving ten lists across half a million rows costs
ten requests, not half a million.

| Route | Returns | Labels? |
|---|---|---|
| `GET /extended-attributes/{id}/pick-list-options` | `{ itemId, displayValue, systemName, … }` | yes |
| `GET /lookup/{id}` | `{ key, value, sequence, active }` | yes |
| `GET /metadata/selection-lists/{name}` | `{ itemId, resourceId, displayValue?, … }` | **often not** |

A field commonly declares more than one route and a declared one can 404 — a field naming
both a selection list and a lookup list may find only one of them served — so the resolver
**tries them in order and takes the first that yields labels**, rather than picking one.

Two rules follow from what a live tenant did:

- **`resourceId` is never used as a label.** Selection lists often return only a key such as
  `mo-encoded-titles-NNNN`, which indexes a table the REST surface does not expose.
  Accepting it would fill cells with strings that look resolved and read no better than the
  integer. Those fields stay numeric and the run reports them.
- **A code with no label keeps the code.** A blank cell would destroy the only information
  the row had, and look identical to a field that is genuinely empty. With resolution off,
  a `label:` column holds the raw code, so a file's columns never change shape.

On the tenant this was developed against, `projectStatus`, `workflowId`, the three 0/1 flags
(`enableAlap`, `useFloat`, `uploadToCollection`) and a single-valued extended attribute all
resolved. **Five columns could not be resolved through this API:** `projectTypeId`,
`timeZoneId`, `projectScalingTypeId` and `proposedProjectScalingTypeId` returned only resource
keys, and `associatedProjectId`'s list was empty. Columns like these stay numeric, and the log
names them at the start of the run. Which ones those are depends on how your tenant is
configured; `--probe-lookups` shows it.

`--probe-lookups` lists which fields hold codes and calls each endpoint, printing what comes
back. `Output.ResolveLookups` switches resolution off.

### Users

`projectManager`, `modifiedUser` and similar fields are user IDs. They declare a lookup list,
but **that list could not be used** on the tenant this was developed against. There, the list
behind `projectManager` held 600 entries sorted alphabetically from `--` to about "Cr" — so everyone
from roughly D to Z appears to be missing, which looks like a cap on the list but was not
demonstrated directly — and it held active users only: every one of 24 inactive people tested
was absent. `modifiedUser`'s own list returns 404. `GET /users/{id}` resolved all 37 users
tried, inactive ones included.

So users get their own column form:

```
projectManagerLabel => user:projectManager
modifiedUserLabel   => user:modifiedUser
```

- **Format** is `Last, First`, matching the labels the lookup list produced, with a fallback
  to whichever part exists. A user with no name keeps the numeric ID.
- **Cost** is one request per *distinct* user, cached for the whole run and shared across
  objects, so a manager on ten thousand projects is fetched once. The first 1,000 projects
  needed about 200 lookups at about 0.8 s each, and the number of distinct people grows with
  the collection, so on a full export this is likely to be a large part of the run time.
- **Privacy.** `/users/{id}` returns a *full user record*: email, phone, postal address,
  employee number, cost centre, login ID. Only the formatted name is kept; the rest is dropped
  as soon as the name has been read, and never reaches the CSV or the log. `--probe-users`
  follows the same rule and prints only names, listing every other populated field by name
  with its value withheld.
- **A failure costs one name, not the export.** A user that cannot be read keeps its numeric
  ID, and the first few failures are logged.

`Output.ResolveUsers` switches it off (the columns then hold the ID). `Output.UserFields` is
the list of property names `--discover-fields` treats as user IDs — nothing in the API says
which integers are users, so extend it for your tenant. It affects generation only: a `user:`
path in a hand-written list works whether or not it is named there. `--probe-users` takes one
user ID that resolved and one that did not, fetches each, and reports whether the lookup list
is missing users it should have.

## Delta export, and why it works differently here

The DAM utility pushes its window into the query: `ModifiedOn >= X AND ModifiedOn < Y`
goes to the server, which returns only matching records.

**PM has no query language at all.** Of 377 operations, exactly one endpoint
(`/review-tasks`) declares a changed-since parameter. So the window has to be applied to
rows as they arrive, and the only way to avoid reading every collection in full on every
run is to sort newest-first and stop once the rows fall before the window.

**The window compares each object's last-modified date** — `modifiedDate` for most objects,
`updatedAt` for budgets, which stores it as an epoch number. `--delta-property` overrides it,
and doing so starts a fresh lineage. Projects carry no creation date at all, so
`modifiedDate` is the only date they can be windowed on. Only `modifiedDate` is reliably UTC
(it comes back with a trailing `Z`); `beginDate` and `endDate` carry no zone, and an unzoned
value is assumed to be UTC, which may be hours off from what it means.

```bash
aprimo-pm-export.exe --all-objects --daily --prefix pm-daily
```

`--daily` is shorthand for `--since last`. The first run has no mark, so seed it once:

```bash
aprimo-pm-export.exe --all-objects --since 7d --prefix pm-daily
```

| `--since` / `--until` | Meaning |
|---|---|
| `last` | Resume from the saved mark (`--daily`) |
| `yesterday` | The whole previous UTC day |
| `today` | UTC midnight to now |
| `1d`, `36h`, `90m` | Rolling span back from now |
| `2026-08-05` | That single UTC day |
| `2026-08-05T04:00:00Z` | Explicit instant — use this for local-midnight boundaries |
| `now` | Valid for `--until` |

**The delta machinery has not been run against a live tenant**, only tested offline.

### The early stop, and the check that guards it

Under the default `SortedDescending` strategy the reader asks for
`sortField=modifiedDate&sortAscending=false` and stops once a whole page falls before the
window. For a daily delta that is typically one or two requests per object instead of the
entire collection.

That shortcut is only sound if `sortField` was actually honoured — and **nothing in the
spec says which fields are sortable**. If the API quietly ignores it, rows arrive in the
server's own order, an early page can contain old rows, and stopping there would silently
truncate the export. (On `/projects`, ascending sorts on `modifiedDate` and `projectId` are
honoured. The descending sort that delta runs use has **not** been tried against the live
API, which is exactly what this check is for.)

So the filter watches the values it receives. An early stop requires **positive evidence**
that timestamps really are descending; one out-of-order pair withdraws it permanently and
the run degrades to a full scan, with a line in the log saying so. Ties prove nothing
either way and are ignored — bulk-updated data produces plenty of them.

Being slow is recoverable. Being quietly incomplete is not. If you would rather not rely
on it at all:

```bash
aprimo-pm-export.exe --all-objects --daily --delta-strategy FullScan
```

### Objects that cannot do delta

Objects with no modified stamp — `teams`, `domain-rights`, `work-request-*`,
`integration-logs` — are **skipped** on a `--since` run rather than exported in full.
Exporting them whole would mix a complete snapshot into a file set whose name says
delta. Export them separately without `--since`, or set
`Source.Delta.SkipObjectsWithoutModifiedProperty: false` to include them and accept the
snapshot.

`work-requests` has only `dateRequested`, so a window over it means "requested in the
window", not "changed in it".

### Gaps, duplicates and the mark

Windows are **half-open `[since, until)` and chained** — the next run starts exactly where
the previous ended. That is gap-free by construction and immune to items being edited
mid-run: those land in the following window.

**The mark advances only after a run that completes without cancellation and without
hitting the row cap.** A partial run leaves it alone, so re-running finishes the window
rather than skipping the remainder.

**Every `--since` run advances the mark, including a one-off.** That is right for a standing
`--daily` job and wrong for a historical pull: fetching one old day would move the mark to the
day after it, and the next `--daily` would skip everything since. The mark is keyed per
tenant + object, so only the objects in that run move. Until there is a flag to prevent it,
keep ad-hoc pulls in their own output directory, which gets its own `delta-state.json`:

```bash
aprimo-pm-export.exe --objects projects --since 2026-08-05 --out ./export-adhoc --overwrite
```

State lives in `delta-state.json` in the output directory, keyed by **tenant + object +
compared property**. A run covering a subset never disturbs the marks of the objects it
skipped, and changing the compared property starts a fresh lineage instead of reusing a
mark that meant something else.

`Source.Delta.OverlapMinutes` (default 5) re-reads a little of the previous window on
resume, covering the risk that the API stamps timestamps from a clock slightly behind
ours. It costs a few duplicate rows per run — set it to `0` for exact adjacency.
**Duplicates are possible by design; de-duplicate on the object's ID downstream if that
matters.**

All boundaries are **UTC**. So `--since yesterday` is the UTC day, which in a UTC-5 zone
runs 7pm to 7pm local. If the downstream consumer thinks in local time, pass explicit
instants — and decide before the job becomes recurring, since the two interpretations
differ by several hours of data.

## Paging

| Mode | Mechanism |
|---|---|
| `Offset` | `offset` / `limit`, advancing by the number of items returned. Default. |
| `LinkFollow` | Follows `_links.next.href`, falling back to offset when absent. |

**There is no keyset mode**, and that is not an omission. The DAM utility's keyset paging
works by writing a watermark predicate into a search expression; PM has no expression
language to write it into. Offset is what the API offers, and on `/projects` it holds up.

`LinkFollow` is preferable in principle — the server decides where the next page starts —
but **no example anywhere in the spec shows a populated `next` link**, so it is
unverified and not the default. When a full page arrives without one, the reader carries
on by offset rather than concluding the collection ended.

### Page size

The spec declares a default of 50 and **no maximum**. The tenant this was developed against
**capped `limit` at 250 and did not say so**: asking for 500, 1,000 or 2,500 each returned
exactly 250 rows, with a 200 and no warning. Yours may cap elsewhere, or not at all;
`--probe-depth` finds out.

That is dangerous for a reader that treats a short page as the end of the collection — it
would stop after the first page and report success. So the reader compares what it got against
`_total`. A short page while rows still remain is read as a cap, announced once in the log,
and the smaller size is requested from then on. An empty page always ends the read.

So a too-large `--page-size` costs one wasted first request per object rather than a
truncated export, and the log says which value avoids it. The default is 100; **250 is 2.5
times fewer requests** (about 2,145 rather than 5,362 for roughly 536,000 projects) and was
confirmed to hold at every depth. The one case the detection cannot see is a capped server that also omits
`_total`; the tested tenant always reports it, and `--probe-depth` flags it.

### Depth

`--probe-depth` reads one object at increasing offsets and at larger page sizes (see
[Before a full export](#before-a-full-export)). On `/projects`, offset paging worked to the
end with flat latency, and **the end of the collection is inclusive**: `offset == _total`
returns an empty page and anything beyond is a 400. The reader asks for exactly `_total`
after a full final page, so an export whose size is a multiple of the page size does not fail
on its last step.

`Source.Paging.MaxOffset` (default 1,000,000) fails an object that pages past that
without finishing. It is there because an endpoint that ignored `offset` would otherwise
loop forever returning page 1 — the guard turns an infinite loop into a message.

### De-duplication

On by default, unlike the DAM utility. There, keyset paging advances an immutable
watermark, so de-duplication was only needed for ties on it. Here, offset paging over a
collection someone else is editing can re-show items as rows shift between pages, and there
is no watermark mode to fall back on. Tracking IDs is the only defence available — **but it
guards against repeats only, not against skips.** See the sort caution below.

The ID set is bounded by `Limits.MaxDeduplicationIds` (default 2,000,000). Past it,
de-duplication turns itself off for that object with a warning: duplicate rows are fixable
downstream, an out-of-memory failure halfway through a multi-hour run is not.

## Before a full export

Nothing larger than 1,000 rows has been exported yet. Before the first full export of a large
object:

**1. Probe depth, and the sort you intend to use.** Pass the same `--page-size` and `--sort`
you plan to export with, so the probe tests what the export will do:

```bash
aprimo-pm-export.exe --objects projects --probe-depth --page-size 250 --sort projectId --rps 2
```

It reads about 15 pages, prints one row per request, and ends with plain findings: whether any
depth errored, returned the wrong rows, or slowed down; whether the page size was honoured;
and an estimated paging time. A failure at some depth is reported with the last offset that
worked, so an unreachable tail is known before a multi-hour run rather than during it.

**2. Sort by something that cannot change.** The default sort is the modified date, which is
what a delta window needs and a poor key for a full snapshot of a collection people are
editing. When an already-exported project is modified it jumps to the end of the order, every
project behind it shifts down one place, and the row now at the cursor's old position is
passed over and never read. De-duplication stops repeats; nothing stops skips.

Use the object's ID instead. For projects that is `projectId` — unique, immutable, and new
rows land at the end. `--sort projectId` was confirmed honoured. IDs are not contiguous (the
last page reached about 846,000 against about 536,000 rows), so an ID cannot be used to work out how many
rows remain.

**3. Export.**

```bash
aprimo-pm-export.exe --objects projects --page-size 250 --sort projectId --rps 2 --overwrite
```

Expect the paging to take about 46 minutes (2,145 requests), **plus** user lookups, which are
the unknown: one request per distinct user at roughly 0.8 s each. The output is about 180 MB,
extrapolated from 1,000 rows at 340 KB. `--rps 2` is not what limits speed — requests run one
at a time and each takes about a second — so raising it changes nothing.

Ctrl+C stops after the current page; `--resume` continues from the checkpoint, and the
immutable sort makes the resume exact. `--overwrite` replaces existing
`{prefix}-{object}_NNNN.csv` files for that prefix.

**4. Seed the delta.** Note the UTC time the export *starts*. A `--since` run from that
instant picks up everything modified while the export was running, so nothing falls in a gap
between the snapshot and the first delta:

```bash
aprimo-pm-export.exe --objects projects --since 2026-11-12T19:30:00Z --prefix pm-daily
```

(The date and time above are an example — use your own start time.)

## Rate limiting

Three settings under `Throttle`:

| Setting | Default | CLI | Meaning |
|---|---|---|---|
| `RequestsPerSecond` | `5.0` | `--rps` | Sustained rate. Fractional allowed (`0.5` = one request every two seconds). `0` disables throttling. |
| `Burst` | `1` | `--burst` | Token-bucket depth. `1` gives strictly even spacing, which is kindest to a shared tenant. |
| `MaxConcurrentRequests` | `1` | — | Cap on in-flight requests. Objects export sequentially, so `1` is correct today. |

Three behaviours that are easy to get wrong elsewhere:

- **Retries pass through the same limiter**, so backoff can never push the emitted rate
  above the ceiling — a retry storm still respects `--rps`.
- **A 429 or 503 holds back every caller**, not just the throttled request.
- **Token requests deliberately bypass the limiter** (separate `HttpClient`), so
  credential renewal cannot starve the export on a long run.

The spec documents **no rate limits and no 429 response** — in fact every one of its 377
operations declares only `200`. So there is no published ceiling to aim at: this
client-side control is the real throttle and the 429 handling is defensive. If you do not
know what the tenant tolerates, start at `--rps 2` and watch the log for retries.

Because requests are sequential and each takes about a second on the tested tenant, the rate
limit rarely binds at all; latency does.

Retry behaviour is separately tunable under `Source.Retry`. Config-file only, no CLI flags.

## CSV details

- RFC 4180 quoting; values containing the delimiter, quotes, newlines, or edge whitespace
  are quoted, and embedded quotes doubled.
- UTF-8 with BOM by default so Excel detects the encoding on double-click.
- **Formula injection guard**: values starting with `= + - @` get a leading apostrophe so
  spreadsheets do not evaluate them. Numbers are exempt, which matters here — PM's
  financial objects are full of signed amounts, and `-42` must stay numeric.
- Objects or arrays landing at a leaf are emitted as raw JSON in the cell.
- Output rolls to `{prefix}-{object}_0001.csv`, `_0002.csv`, … every
  `MaxRecordsPerFile` rows. The header repeats in every part so each file stands alone.

`Limits.MaxRecordsPerObject` caps rows **per object**, not across the run — a shared cap
would be spent entirely on whichever object happened to go first.

## Resume

With `Limits.EnableCheckpoint`, each object writes a checkpoint after every completed page
(atomically, via write-then-replace). `--resume` continues from it.

- Resume is page-granular, and checkpoints are written only after a page is fully written
  and flushed — so no duplicate and no dropped rows.
- Resumed rows start a **new** part file rather than appending to a partial one, so parts
  may be uneven after a resume.
- The checkpoint records a fingerprint of the paging settings, sort, delta window and
  column mappings. Change any of them and it is refused rather than producing a mixed
  output set.
- A page-size cap learned from the server is not saved; a resumed run relearns it from its
  first page, at the cost of one request.

Ctrl+C stops after the current page and flushes; a second Ctrl+C aborts immediately.

## What has been verified, and what has not

The spec is unusually thin — no examples at all, only `200` responses declared, several key
facts simply absent — so the honest state of each assumption matters. Everything below was
observed on a single production tenant, and only on `/projects`.

**Verified against a live tenant**

| Assumption | Result |
|---|---|
| Token endpoint, `client_credentials`, scope `api` | confirmed against the OpenID discovery document |
| `_embedded` key for `/projects` | `Project` (the schema name) |
| `resourceName` for `/metadata/{resourceName}` | singular lowercase: `project` works |
| `attributeName` populated on reads | yes |
| `modifiedDate` parses | yes, with a `Z` |
| Page size above 50 accepted | up to 250; larger is silently capped |
| Offset paging to the end of a large collection | works; latency flat |
| The end of the collection | inclusive: `offset == _total` is an empty page |
| `sortField` honoured | for `modifiedDate` and `projectId`, ascending only |
| Codes resolve through `/lookup` and pick-list endpoints | yes, on most fields |
| Users resolve through `/users/{id}` | yes, inactive users included |

**Not verified**

- **A full export**, and the time the user lookups add to it.
- **`--since` / `--daily`**, end to end, including the **descending** sort it relies on.
- **Every object except `projects`**: `_embedded` keys, ID and date properties, metadata
  resource names, and whether `sortField` is honoured on them.
- **`_total` exactness.** It moved by one within about ten minutes as projects were created,
  so it is a live count. Treat it as an ETA input, not a reconciliation target.
- **Whether collection responses carry everything the single-item endpoints do.** 14 of the
  50 columns were empty in every one of the first 1,000 rows — among them `outlook`,
  `brandName`, `projectedEndDate`, `baselineEndDate` and two custom extended attributes, plus
  five `…Label` columns whose code was itself empty. Whether those are genuinely null on those
  (the oldest) projects, or never returned by the list endpoint, has not been settled. `Source.HydrateFromDetailEndpoint` fetches each item individually, at one
  request per item, if it turns out to matter.
- **`LinkFollow` paging**, which no spec example shows working.
- **Multi-value attribute labels.** `label:eamulti:…` resolves through the same pick-list
  route, but only single-valued attributes have been seen live.

## Layout

What ships, and what is yours. The first block is the tool; the second is created by *you*
running it, is listed in `.gitignore`, and must not be copied to anyone else.

```
appsettings.local.example.json      the template for your settings (ships)
publish.ps1                         tests, then builds the self-contained dist\ folder
.gitignore                          keeps your settings, exports, column lists and dist\ out of source control
src/AprimoPmExport/
  Program.cs                        CLI, plan banner, summary
  appsettings.json                  annotated shipped defaults - do not put your settings here
  objects.json                      the object registry — what can be exported
  fields/                           shipped starter column lists
    _default.txt                      shared fallback
    activities.txt, projects.txt, tasks.txt
  Configuration/
    ExportConfig.cs                 config model + validation
    ConfigLoader.cs                 shipped < local < env < CLI, local-file merge, arg parsing, --help
    ObjectCatalog.cs                the object registry model and selection
    CredentialPrompt.cs             masked interactive credential entry
    FieldListLoader.cs              per-object column lists, column derivation
  Auth/
    OAuthTokenProvider.cs           client credentials, caching, single-flight renewal
    CredentialStore.cs              DPAPI-encrypted credential store, keyed by tenant
  Http/
    RateLimiter.cs                  token bucket + concurrency cap + 429 backpressure
    ApiClient.cs                    auth injection, retry/backoff, error messages
  Export/
    FieldExtractor.cs               path grammar, compilation, projection, label/user hooks
    HalEnvelope.cs                  the _total / _embedded / _links envelope
    HalPagedReader.cs               offset and link-following paging, page-size cap handling
    MetadataCatalog.cs              GET /metadata/{resourceName}, selection lists
    LookupResolver.cs               label: — codes to labels, routes tried in order
    UserResolver.cs                 user: — IDs to names via /users/{id}, cached per run
    FieldListGenerator.cs           --discover-fields
    CsvRollingWriter.cs             RFC 4180, rolling parts, injection guard
    DeltaWindow.cs                  windows, client-side filtering, per-object state
    ObjectExportRunner.cs           one object: paging, caps, checkpoints, user prefetch
    MultiObjectRunner.cs            orchestration across objects
    ManifestWriter.cs               manifest.csv
    SampleInspector.cs              the --sample diagnostic
    LookupProbe.cs                  --probe-lookups and --probe-users
    DepthProbe.cs                   --probe-depth
docs/api-notes.md                   what the spec says, what a live tenant did, where it is silent
tests/AprimoPmExport.Tests/         self-contained test runner, no framework needed

YOURS - created when you run the tool, never shipped:
appsettings.local.json              your settings (from the example above)
fields/                             column lists --discover-fields generates from YOUR tenant
export/                             your CSVs, manifest, checkpoints and delta state
%LOCALAPPDATA%\AprimoPmExport\      your encrypted credentials (outside this folder entirely)
```

`export/` holds your data, including people's names once user columns are resolved, and the
generated `fields/` holds the names of your tenant's custom attributes. Treat both as you would
the data they came from.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Runtime failure (auth, API, I/O) |
| 2 | Invalid configuration or arguments |
| 3 | Cancelled — re-run with `--resume` |
| 4 | Partial — some objects exported, others failed |

Code 4 is specific to this tool: with two dozen objects in a run, "some failed" is a
genuinely different outcome from both success and failure, and a scheduler should be able
to tell them apart.

## Open items

1. **Everything observed here comes from one tenant.** The page-size cap, the unusable user
   lookup list, the `_embedded` key names, the unresolvable code lists — all of it describes
   one tenant's configuration and may not hold for yours. The probes exist to find out, and
   the tool is built to adapt where it can (it detects a page-size cap, tries every lookup
   route, and falls back to the sole array under `_embedded`); but a tenant that differs in
   some way not yet seen may need something that does not exist yet.
2. **No full export yet.** The next step is [Before a full export](#before-a-full-export);
   how much the user-name lookups add is the unknown.
3. **Only `projects` has been run live.** See
   [What has been verified](#what-has-been-verified-and-what-has-not).
4. **Five columns cannot be resolved to labels:** `projectTypeId`, `timeZoneId`,
   `projectScalingTypeId`, `proposedProjectScalingTypeId` and `associatedProjectId`. The API
   returns only resource keys for the first four and an empty list for the last. They export
   as integers; map them downstream.
5. **`--sample` does not run the resolvers.** A `label:` or `user:` column shows the raw code
   and counts as having resolved. Use `--probe-lookups`, `--probe-users` or a small export to
   see real labels.
6. **User lookups are sequential.** If they turn out to dominate a full export, they could be
   fetched concurrently; the page loop itself would stay sequential.
7. **A `--no-advance-mark` flag does not exist.** One-off `--since` pulls move the delta mark;
   the workaround is a separate `--out` directory.
8. **A capped server that omits `_total`** is indistinguishable from a last page, and would end
   a read early. The tested tenant always reports `_total`.
9. **`GET /tasks` is user-scoped.** A tenant-wide task export needs
   `/projects/{projectId}/tasks`, which is a per-parent traversal this tool does not do.
10. **Child collections are out of scope.** The spec has 51 paged GET endpoints: 29 are
   top-level and the registry covers 27 of them, leaving out `/projects/display-list` and
   `/review-tasks/mine` as alternate views of collections already present. The other 22
   hang off a parent (`/activities/{id}/activity-cells`, `/budget/{id}/…`, attachments,
   chatboards). Adding those means exporting parents first and iterating, which is a
   different shape of run.
11. **No localisation.** The DAM API exposes localized field values through a `languages`
    header; PM's REST surface returns resource IDs (`titleResourceId`, `encodedTitleId`)
    with no endpoint to resolve them, so display names are not available.
12. **`Source.HydrateFromDetailEndpoint` is untested.** It exists for the case where
    collection responses turn out thinner than single-item ones.
