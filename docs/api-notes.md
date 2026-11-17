# Aprimo PM (Productivity & Spend) REST API — facts driving this utility

Extracted from `Productivity_and_Spend_OpenAPI_Specification.txt` in the repository root
(OpenAPI 3.0.1, `Aprimo Marketing Operations (PM) REST API`, version `v1`).

Everything below is either quoted from that document or was derived by parsing it. Where
the spec is silent, this file says so explicitly — those silences drove most of the
design decisions, so they are worth keeping visible.

Size: **288 paths, 377 operations, 158 component schemas, 72 tags.**

---

## Hosts

| Purpose | URL |
|---|---|
| API | `https://{tenant}.aprimo.com/api` |
| OAuth token | `https://{tenant}.aprimo.com/login/connect/token` |

The `servers` block declares the API base with a `tenant` variable defaulting to
`your-tenant`.

**This is not the DAM API host.** DAM lives at `https://{tenant}.dam.aprimo.com/api/core`.
PM is on the plain tenant host under `/api`. The token endpoint is identical for both,
which makes the API host the one thing that must change when moving between the two
tools — and the easiest thing to get wrong, because a wrong host authenticates fine and
then 404s.

## Auth

Two security schemes, and the document's top-level `security` accepts either:

- `Bearer` — `http` / `bearer`, `bearerFormat: JWT`, described as "JWT obtained from the
  Unified Login Service."
- `OAuth2` — with three flows declared:

| Flow | Token URL | Scopes |
|---|---|---|
| `clientCredentials` | `/login/connect/token` | `api`, `reporting-api`, `filestore-access` |
| `password` | `/login/connect/token` | `api`, `reporting-api` |
| `authorizationCode` | `/login/connect/authorize` + token/refresh | `api`, `reporting-api`, `offline_access`, `filestore-access` |

The utility uses `clientCredentials` with scope `api`. Scope descriptions:
`api` is "Full access to the Aprimo REST API."

## Headers

**The spec declares no header parameters on any of its 288 paths.** None. No
`API-VERSION`, no `languages`, no `select-*`.

This is a significant difference from the DAM API, where `API-VERSION: 1` is mandatory
and the `select-record*` headers are what make metadata come back at all. The utility
therefore sends only `Authorization` and `Accept: application/json` by default, with
`Aprimo.ApiVersion` and `Aprimo.AdditionalHeaders` available if a tenant turns out to
want more.

## Media types

`application/json` throughout for responses. Request bodies additionally offer
`text/json` and `application/json-patch+json` on write operations, which this read-only
utility never uses.

## Paging

Every paged endpoint takes the same four query parameters:

| Parameter | Type | Default |
|---|---|---|
| `offset` | `integer(int32)` | `0` |
| `limit` | `integer(int32)` | `50` |
| `sortField` | `string` | — |
| `sortAscending` | `boolean` | `true` |

Note `sortAscending` is a **separate boolean**, not DAM's `-Field` prefix convention.

**There is no maximum declared for `limit`, anywhere.** Of the 74 `limit` parameters, 70
declare `default: 50`, one `default: 250`, one `default: 0`, and two declare no default.
None declares a `maximum`. Contrast the DAM API, whose `RecordCollection.limit` carries
an explicit `maximum: 1000` — that ceiling is a documented fact there and simply does not
exist here. The utility defaults to 100 and applies only a local sanity ceiling to catch
typos.

**51 GET endpoints** accept `offset`+`limit`. Of those:

- **29 are top-level collections** with no parent path parameter. `objects.json` covers
  **27** of them — it leaves out `/projects/display-list` and `/review-tasks/mine`, which
  are alternate views of collections already in the registry (`mine` is explicitly
  user-scoped, and `display-list` declares no item schema at all).
- **22 are child collections** hanging off a parent — `/activities/{id}/activity-cells`,
  `/projects/{projectId}/tasks`, `/budget/{id}/…`, attachments, chatboards. Exporting
  those means iterating parents first, which is a different shape of run; see the
  README's open items.

## The response envelope

Every paged endpoint returns the same HAL shape, described in the spec as
"Paged HAL collection. Items are under '_embedded', keyed by the resource type name.":

```jsonc
{
  "_total": 1234,                       // integer(int64)
  "_embedded": {                        // additionalProperties: array of <Schema>
    "<resourceTypeName>": [ /* items */ ]
  },
  "_links": {                           // additionalProperties: { href, type }
    "self": { "href": "…" }
  }
}
```

Two things the spec does **not** provide, both of which shaped the reader:

1. **The `_embedded` key is never stated for any object.** It is typed as
   `additionalProperties`, and the description says only "keyed by the resource type
   name" — without giving that name anywhere. So the reader takes whichever array is
   under `_embedded` rather than looking up a name it cannot know. `objects.json` has an
   `EmbedKey` field to pin it once a real response has been seen.

2. **No example anywhere shows a populated `next` link.** The `_links` description
   mentions "self, first, last, next, prev, and action links", but the string `"next"`
   appears **zero** times as a key in the entire document. That is why `LinkFollow`
   paging exists but is not the default, and why the reader falls back to offset when a
   full page arrives without one.

More generally: **the spec contains no examples at all** — zero `example` or `examples`
blocks in 765 KB. The DAM utility could lean on documented 200 examples to confirm that
`fields.items[]` came back populated on collection endpoints; there is no equivalent
evidence available here. Anything about actual response content in this file is inference
from the schemas, and `--sample` exists to replace that inference with observation.

## Response codes

**Every one of the 377 operations declares exactly one response: `200`.**

No 400, 401, 403, 404, 409, 429, or 5xx is declared anywhere. So:

- there is no documented error shape to parse;
- **no rate limit, quota, or 429 is documented** — the strings `429`, `rate limit`,
  `throttl`, `Retry-After` and `Too Many` do not appear in the document at all. The
  client-side throttle is therefore the only rate control that exists, and the 429 /
  `Retry-After` handling is purely defensive;
- a 404 tells you nothing beyond "not here" — it could equally mean an unlicensed
  module, a wrong host, or a resource the tenant does not expose. `TryGetJsonAsync`
  treats 404 as "not available" rather than fatal when probing optional endpoints.

## Metadata — how fields are discovered

Three operations under the `Metadata` tag. **None declares a response schema** (their
`200` content is an empty object), so the shapes below are taken from the
`IResourceMetadata` / `IFieldMetadata` component schemas the operations plainly
correspond to.

| Operation | Summary |
|---|---|
| `GET /metadata/{resourceName}` | "Returns the metadata definition for the named resource (object)." |
| `GET /metadata/groups` | **Deprecated** — "use `/metadata/group` instead" |
| `GET /metadata/selection-lists/{selectionListName}` | "Returns the items of a named selection list" (`parentValue`, `filter` query params) |

`GET /metadata/{resourceName}` is this utility's counterpart to the DAM API's
`GET /fielddefinitions`, and it is what makes `--discover-fields` possible.

The deprecation note on `/metadata/groups` is the only evidence in the document of the
naming convention for `resourceName`: **singular, lower-case** (`group`, not `groups`).
`objects.json` follows that for every object, which is a guess extrapolated from one data
point — hence `MetadataResource` being editable per object.

`GET /metadata/groups` is also the **only deprecated operation in the entire document**.

### `IResourceMetadata`

```
objectName, supportsEAs, supportsSecureEAs, nameResourceId, resourceType, contentType,
fields[] -> IFieldMetadata
```

### `IFieldMetadata`

```
fieldName, titleResourceId, fieldDataType, fieldFormatting, validationRules[],
valueManipulationRules[], validValues[], lookupListId, attributeHelpTextId,
timeValueType, amsAttributeId, isSecure, isReadOnly, allowInitialValue, inactive,
selectionListName, entityFieldName, lookupValues[], systemTypeName, parentName,
parentFieldName, parentFieldType, fieldType, defaultValue, …
```

`fieldName` is the value that matters — it is what a mapping path references.
`titleResourceId` is a **resource key, not a label**: PM localises through a resource
table this REST surface does not expose, so there is no way to get a human-readable
caption from the API.

### The enums are unnamed

```
MetadataDataType : 1,2,3,4, 11,12,13,14,15, 21,22,23, 31..37, 40..44, 99
MetadataFieldType: 0,1
```

Both are bare integer enums with **no names or descriptions for any member**. So it is
not possible to say from the spec alone which `MetadataFieldType` value means "entity
property" and which means "extended attribute" — which is exactly the distinction a
generated mapping path would need.

This is why `--discover-fields` writes a **fallback chain** rather than committing to one
form:

```
Region => region || ea:Region || ea#4102
```

The entity property if the definition names one, then the attribute by name, then by ID.
Whichever is real resolves; the rest are inert. `--sample` then shows which branch
actually hit, so it can be pinned.

## Tenant-defined metadata: extended attributes

PM's answer to DAM's `fields.items[].localizedValues[]`. Two arrays hang off most object
schemas:

### `ExtendedAttributeValue` — single-valued

```
eaId          integer(int32)
skipDCTSave   boolean
eaValue       (no type declared — nullable, untyped)
attributeName string, nullable, readOnly
```

`eaValue` is **declared with no type at all**, so a value may arrive as a string, a
number, a boolean, or an object. The extractor formats whatever turns up and emits
objects/arrays as raw JSON.

### `EaMulti` — multi-valued (pick lists)

```
parentId           integer(int32)
extAttributeId     integer(int32)     <- note: NOT eaId
encodedListItemId  integer(int32)
attributeName      string, nullable, readOnly
```

Two consequences worth stating plainly:

1. **The ID key differs between the two arrays** — `eaId` on singles, `extAttributeId`
   on multis. The `ea#` and `eamulti#` shorthands each generate the right one.

2. **A multi-value attribute exports a numeric code, not a label.** `encodedListItemId`
   is a code into a selection list; the label is only obtainable from
   `GET /metadata/selection-lists/{name}`, which is what `--selection-list` is for.

`attributeName` is marked `readOnly` on both. In OpenAPI that means "not writable by the
client", which does *not* guarantee it is populated on a read. Since a null name would
make every `ea:Name` mapping silently produce empty cells, `--sample` reports explicitly
how many attributes came back named versus unnamed, and `--discover-fields` writes the
ID form alongside the name form.

`IResourceMetadata.supportsEAs` / `supportsSecureEAs` say whether an object has them at
all, and `IFieldMetadata.isSecure` marks individual ones — whose values may or may not be
returned depending on the registration's rights.

## Resolving codes to labels — observed, not documented

Most numeric columns in a PM export are codes. The API does expose the lists behind
them, but **three of the four endpoints declare no response schema**, so the shapes below
were observed against a live tenant during development rather than read from the spec. They
describe one tenant; `--probe-lookups` shows what yours does.

| Endpoint | Observed item shape | Yields labels? |
|---|---|---|
| `GET /lookup/{id}` | `{ key, value, sequence, active }` | **Yes** — `value` |
| `GET /extended-attributes/{id}/pick-list-options` | `{ itemId, displayValue, sequence, active, systemName }` | **Yes** — `displayValue` |
| `GET /metadata/selection-lists/{name}` | `{ itemId, resourceId, displayValue?, … }` | **Sometimes** |
| `GET /system-types` | `{ systemtypes: [...], _links }` | not reached in practice |

Concrete results worth keeping:

- The lookup list behind a project's status returned `1=Draft, 2=In Process, 3=Paused,
  4=Closed, 5=Canceled, …`.
- The lookup list behind a project's manager returned about 600 user names.
- An extended attribute's pick-list endpoint returned the label for each of its values.

Three findings that shaped the resolver:

1. **A field commonly declares more than one route, and a declared one can 404.** A field
   that names both a selection list and a lookup list may find only the selection list
   served, and vice versa. So routes have to be tried in order, not chosen.

2. **Selection lists frequently carry no `displayValue`.** Several returned only
   `resourceId` values of the form `mo-encoded-titles-NNNN` — resource keys into a table this
   API does not expose. Those fields are genuinely unresolvable, and `resourceId` is
   deliberately not accepted as a label: it would fill cells with strings that look resolved
   but read no better than the code.

3. **`/system-types` does not use the HAL envelope** the spec gives it. It returns
   `{ systemtypes: [...], _links }` — the array under a plain root key, with no `_total`
   and no `_embedded`.

Not every code resolves through these routes. A user-ID field can declare a lookup list that
is not served at all, and the user list that *is* served was not usable (next section).

### User IDs: the lookup list is unusable, `/users/{id}` is not

On the tenant this was developed against, the lookup list behind a project's manager:

- held **600 entries**, sorted alphabetically by label. Sampling every 50th entry ran from
  `--` to a name beginning "Cr" at entry 550, so the 600 end at about "Cr" and everyone from
  roughly D to Z is missing. A tenant with half a million projects does not have active users
  only up to C, so this looks like a cap. It was not demonstrated directly: no active user
  with a D–Z surname was in the test set;
- **excluded inactive users.** All 24 inactive people tested were absent and all 12 sampled
  from the list were active. This held inside the list's range too: inactive users whose
  surnames fall alphabetically within it were all absent;
- omitted at least one **active** user for a reason neither of those explains: a
  placeholder-style entry that sorts at the very top of the list was nonetheless absent.

`GET /lookup/{id}?filterText=<surname>` does work as a name search: it found the listed users
in 3 of 3 control searches and surfaced the placeholder user that the unfiltered list omitted.
But it cannot reach inactive users, and it needs the surname first, which means calling
`/users/{id}` anyway.

**`GET /users/{id}` resolved 37 of 37 users, inactive ones included.** It is declared in the
spec as returning `UserGroup`, which despite the name is a full user record: `firstName`,
`lastName`, `loginId`, `email`, `phone`, `mobileNumber`, `fax`, postal address,
`employeeNumber`, `costCenter`, `lastLoginDate`, rights and group memberships. The utility
reads two name fields and retains only a formatted `Last, First`; nothing else is written,
logged or printed. `GET /users/me` names the user the registration runs as, and it came back
with the whole name in `lastName` and no `firstName`, so the formatter falls back to
whichever part exists.

One more thing the probe turned up: a user who resolved through the lookup on one date was
absent from it on a later date, by which time that user had become inactive. Either the list
drops people when they are deactivated, or the list differs by caller (the registration had
been replaced between the two runs). Not determined. Either way a list that changes under a
caller is another reason not to depend on it.

## Paging depth and page size — observed

`--probe-depth` was run against `/projects` on one tenant (about 536,000 projects), sorted by
`modifiedDate` ascending at 100 per page:

- **Offset paging works to the very end.** Pages read at offsets 0, 1,000, 10,000, 50,000,
  100,000, 250,000, 400,000, 500,000 and the last full page all returned 100 rows, with no
  repeats and the sort order carried across every depth. There is no depth ceiling and no
  quiet clamping, at least to half a million rows.
- **Latency is flat with depth.** 0.8–1.5 s per page from offset 0 to the end, with one slow
  read (4.5 s at offset 500,000) between faster ones either side. A single hiccup, not a trend.
- **The end is inclusive.** The server's own error gives the rule: `The value of argument
  'offset' must be in range '0' ... 'N'` where N was equal to `_total`. So `offset == _total`
  is valid and returns an empty page, and anything beyond is a 400. That matters because the
  reader, after a full final page, requests exactly `offset == _total`; it is safe, so an
  export whose size is a multiple of the page size does not fail on its last step.
- **`limit` is silently capped at 250.** Asking for 500, 1,000 or 2,500 items each returned
  exactly 250, with a 200 and no warning or error. Rows, byte size and first and last IDs were
  identical across all four. The spec documents no maximum for `limit`; this is it, on this
  tenant. A full `/projects` export is therefore about 2,145 requests at the cap, not the 5,362
  it takes at 100 per page.

Neither the cap nor anything above was tested for the other objects, or on any other tenant;
each may differ. `--probe-depth` tests it for yours.

### Why the cap needed a reader change

The reader used to treat any short page as the end of the collection. Against a server that caps
`limit` without saying so, that stops the export after its first page and reports success: ask
for 1,000, get 250, conclude there is nothing more. The reader now compares what it got against
`_total`. A short page with rows still remaining is read as a cap, announced once, and the
smaller page size is used from then on. An empty page always ends the read, and with no `_total`
a short page ends it as before. The one case that cannot be told apart is a capped server that
also omits `_total`; the tenant tested always reported it.

## A caution on sorting a full export

The default sort is the object's modified date, ascending. That is what an export needs for a
delta window, but it is a poor key for a full snapshot of a collection that is being edited:
**the key moves.** When an already-exported project is modified it jumps to the end of the sort
order, every project behind it shifts down one place, and the row now sitting at the cursor's
old position is passed over and never read. The reader's de-duplication stops rows being read
twice; nothing stops them being skipped. How often this bites depends on how often old projects
are edited during a run, which has not been measured.

The fix is a sort key that cannot change. For `/projects` that is `projectId`: unique, immutable,
and new rows land at the end.

**`sortField=projectId` was honoured.** Re-running the depth probe with `--sort projectId
--page-size 250` read nine pages from offset 0 to the last, with IDs rising monotonically across
every depth and each page starting on exactly the key it was sorted by. That run also
confirmed, at depth, what the first one showed at offset 0:

- **250 per page held at every depth.** About 0.8–2 s per page from offset 0 to the end, flat.
- **The end was the same.** Offset `== _total` returned an empty page; one beyond was a 400.
- **The cap was still 250.** Asking for 500, 1,000 and 2,500 each returned 250 again.
- **Projected paging time: about 46 minutes** for the 2,145 requests, from the measured
  latencies. That is paging only. It excludes user-name lookups, one request per distinct user.

IDs are not contiguous: the last page's IDs reached about 846,000 against about 536,000 rows, so
roughly a third of the ID range was absent (deleted, or not visible to the registration).
Sorting by ID is therefore stable but not dense, and an ID cannot be used to infer how many rows
remain.

## No query language

**There is no search-expression language in this API.** This is the single biggest
functional difference from the DAM API, which has a full expression grammar
(`Status = 'Released' AND CreatedOn >= 2025-01-01`, `FieldName("Brand")`, positional and
named parameters, wildcards) on `POST /search/records`.

PM has nothing equivalent. Filtering is limited to whatever ad-hoc query parameters an
individual endpoint happens to declare:

| Endpoint | Filters available |
|---|---|
| `/projects` | `projectStatuses`, `titleFilter` |
| `/review-tasks` | `taskStatus`, `taskTitle`, **`modifiedAfterDate`** |
| `/work-requests` | `activityTitle`, `requestId`, `status`, `workRequestForm` |
| `/teams` | `titleFilter` |
| `/work-request-forms` | `damOnly`, `multipleAssets` |
| `/budget` | `option` |

**`modifiedAfterDate` on `/review-tasks` is the only server-side changed-since filter in
the entire document.** Every other object's delta window has to be applied client-side —
see the README section "Delta export, and why it works differently here".

There is also `POST /groups/search`, but it is a group lookup, not a general query
facility.

## Timestamps

Almost every object stamps modifications with `modifiedDate` (ISO-8601 string) plus a
`modifiedUser` integer. Several also carry `createdDate` / `createdBy`.

**`Budget` is the exception**: `createdAt` and `updatedAt` are `integer(int64)` epoch
values, not strings. `BudgetTemplate` uses the same names but declares them as strings.
`objects.json` flags this with `ModifiedIsEpoch`, and the delta comparer handles both
shapes (inferring seconds vs milliseconds by magnitude when the unit is not declared).

Objects with **no** modified stamp at all — `teams`, `domain-rights`,
`work-request-*`, `integration-logs` — cannot be exported incrementally. `work-requests`
has only `dateRequested`, which means "requested in the window", not "changed in it".

## Spec quirks worth knowing

**Case-colliding path templates.** Two resources split their operations across path
templates that differ only in the case of the parameter name:

```
GET    /groups/{id}          PUT, DELETE  /groups/{Id}
GET    /user-roles/{id}      PUT, DELETE  /user-roles/{Id}
```

These are distinct keys in the JSON object. They parse fine under a case-sensitive JSON
reader, but a **case-insensitive one rejects the whole document as having duplicate
keys** — Windows PowerShell's `ConvertFrom-Json` does exactly this. Worth knowing before
concluding the file is corrupt. It makes no difference to this utility, which only reads
the `GET` forms.

**Untyped properties.** Several schema properties are declared with no type: `Budget.name`,
`Project.projectCategoryId`, `AssignmentTask.outlook`, `AssignmentTask.reviewComments`,
`ExtendedAttributeValue.eaValue`. The extractor is defensive about all of them.

**Codes, not labels.** Status, type and state properties throughout are integer codes
(`projectStatus`, `activityTypeId`, `workFlowTaskStatus`, `statusId`, …). PM resolves
them from configuration, and the REST surface exposes no general lookup for them —
`GET /system-types` and `GET /system-types/{systemtype}` cover some. Exported CSVs
therefore carry codes unless the downstream consumer maps them.

## What the utility relies on that is *not* in the spec

Listed here so a future reader knows exactly which assumptions need testing against a
live tenant:

1. **The `_embedded` key name** for every object. Unstated; the reader infers it. Confirm
   with `--sample`, which prints the key it found.
2. **That `sortField` is honoured** for each object's modified property. Nothing
   documents which fields are sortable. The delta filter verifies this at runtime rather
   than assuming it, and degrades to a full scan if the order is wrong.
3. **That `limit` above 50 is accepted.** No maximum is declared, and no minimum
   guarantee either.
4. **That `offset` advances.** The `MaxOffset` guard exists because an endpoint that
   ignored `offset` would otherwise loop forever returning page 1.
5. **`resourceName` values** for `GET /metadata/{resourceName}`. Extrapolated from the
   single `/metadata/groups` → `/metadata/group` deprecation note.
6. **Whether `attributeName` is populated on reads.** `readOnly` does not settle it.
7. **Whether collection responses carry the same properties as the single-item
   endpoints.** No examples exist to compare, which is why
   `Source.HydrateFromDetailEndpoint` is available but off.
8. **Whether `GET /tasks` can be made tenant-wide.** It is documented as returning "the
   current user's tasks".
