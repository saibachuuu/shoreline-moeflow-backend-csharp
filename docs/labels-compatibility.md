# E1 label chain compatibility notes

This document describes the C# port of the Python label chain
(`app/models/file.py`, `app/apis/source.py`, `app/apis/translation.py`,
`app/apis/tip.py`, `app/services/label_lock.py`). The E1 checkpoint wires the
registration, guard, endpoints and index initialization into `Program.cs`; the
remaining File/ProjectLifecycle and snapshot integration is listed at the end.

## Files added

- `src/Shoreline.Api/Features/LabelService.cs`
- `src/Shoreline.Api/Features/LabelService.Internal.cs`
- `src/Shoreline.Api/Features/LabelService.Models.cs`
- `src/Shoreline.Api/Features/LabelEndpoints.cs`
- `src/Shoreline.Api/Features/LabelRegistration.cs`
- `src/Shoreline.Api/Infrastructure/ProjectLabelLock.cs`
- `src/Shoreline.Api/Infrastructure/LabelRequestGuard.cs`
- `tests/Shoreline.Tests/LabelTests.cs`
- `tests/Shoreline.Tests/LabelHttpTests.cs`
- `docs/labels-compatibility.md`

The only existing production file changed is `Program.cs`, for the four
minimal integration calls described below. No project file or Python file was
modified.

## Routes

| Method | Route | Permission / behavior |
| --- | --- | --- |
| GET | `/v1/files/{fileId}/sources` | `project:ACCESS`; `target_id` required; `paging` defaults true |
| PATCH | `/v1/files/{fileId}/sources` | `project:CHECK_TRA`; body is an array of `{source_id, translation_id}` |
| POST | `/v1/files/{fileId}/sources` | image file only, `project:ADD_LABEL` |
| PUT | `/v1/sources/{sourceId}` | image file only, `project:MOVE_LABEL` |
| DELETE | `/v1/sources/{sourceId}` | image file only, `project:DELETE_LABEL` |
| PUT | `/v1/sources/{sourceId}/rank` | image file only, `project:ADD_LABEL`; `next_source_id` or `"end"` |
| POST | `/v1/sources/{sourceId}/translations` | `project:ADD_TRA` |
| PUT | `/v1/translations/{translationId}` | field-dependent: CHECK_TRA / own content / PROOFREAD_TRA |
| DELETE | `/v1/translations/{translationId}` | owner or `project:DELETE_TRA` |
| POST | `/v1/sources/{sourceId}/tips` | `project:PROOFREAD_TRA` |
| PUT | `/v1/tips/{tipId}` | owner + `project:PROOFREAD_TRA` |
| DELETE | `/v1/tips/{tipId}` | owner + `project:PROOFREAD_TRA` |

`GET /v1/files/{fileId}/sources?paging=false` returns a plain JSON array.
With paging enabled it returns the same array and sets the four
`X-Pagination-*` headers. The page is sliced before `blank` is filtered, while
the total is the count of all raw sources (including blank ones), matching
`File.to_translator`.

A malformed path ObjectId follows the legacy `fetch_model` behavior rather than
returning a generic validation error: file routes raise `FileNotExistError`
(8001), source routes `SourceNotExistError` (8005), and translation/tip routes
`NotExistError` (104). A missing or malformed non-path id (for example
`target_id`) remains a `ValidateError` (2).

## Collection / field mapping

### source (`source`)

`_id`, `f` file, `r` rank, `c` content, `x`, `y`, `v` vertices, `p`
position type (`1` in, `2` out), `lf` line feed, `b` blank, `cm` machine,
`pt` possible terms, `ct` create time, `e` edit time, `label_epoch`.

Image creation defaults: `content=""`, `x=0`, `y=0`, `position_type=1`,
`line_feed=true`, `vertices=[]`, rank `max(rank)+1`, `machine=false`.
`source_count` is incremented after insert. Text files return
`FileTypeNotSupportError` (108).

### translation (`translation`)

`_id`, `o` source (not `s`), `t` target, `c` content, `u` user, `p`
proofread content, `pr` proofreader, `s` selected, `sr` selector, `mt`
machine translation, `ct`, `e`, `label_epoch`.

The unique index remains `{o,t,u}` and deliberately does not include `mt`.
Creation first looks up the same source/target/user/mt row. If that row exists
and both content values are empty it is cleared (HTTP 204); if content exists
it is updated. If no row exists and content is empty, the result is 204.
A duplicate insert maps to `TranslationNotUniqueError` (8007).

### tip (`tip`)

`_id`, `o`, `t`, `c`, `u`, `et` edit time, `ct` create time,
`label_epoch`. The unique key is `{o,t,u}`. Creation is an upsert for the same
source/target/user and strictly increases edit time by at least 1 ms.

### target (`target`) and cache (`file_target_cache`)

Target: `_id`, `t` project, `l` language, `tsc`, `csc`, `ct`, `et`, `i`,
`label_epoch`. Cache: `_id`, `f` file, `t` target, `ts` translated count,
`cs` checked count, `e`, `label_epoch`. The canonical cache collection is
`file_target_cache` (MongoEngine derives it from `FileTargetCache`); the C#
implementation reads and updates that collection, not `filetargetcache`.

Count changes update the file's ancestor chain and itself, the project, the
existing `file_target_cache` document (same `_id`, `ts`/`cs` and
`label_epoch`) and the target document. A translation is counted once per
source, not once per translation. Blank sources do not increment translated or
checked counts. `source_count` only updates files/project.

Edit-time field names are intentionally different: project and team use
`m_e` (inherited from `GroupMixin`), project_set uses `et`, file and target use
`et`, source/translation/cache use `e`, and tip uses `et`.

## Errors

Errors use the existing `ApiError` and the Program error middleware, so the
wire shape remains `{error, code, message}` (plus `identity_code` when set).

| Error | Code | Status |
| --- | --- | --- |
| `ValidateError` | 2 | 400 |
| `NoPermissionError` | 103 | 400 |
| `NotExistError` | 104 | 400 |
| `FileTypeNotSupportError` | 108 | 400 |
| `FileNotExistError` | 8001 | 400 |
| `SourceNotExistError` | 8005 | 400 |
| `SourceMovingError` | 8006 | 400 |
| `TranslationNotUniqueError` | 8007 | 400 |
| `FileNotActivatedError` | 4016 | 400 |
| `TipEmptyError` | 4021 | 400 |
| `TargetNotExistError` | 4024 | 400 |
| `ProjectNotExistError` | 4001 | 400 |
| `LabelBusyError` | 1 | 409 |

`LabelBusyError` messages:

- default: `项目标签正在操作或等待恢复，请刷新后重试`
- missing header: `批量操作后的标签写入必须携带版本号，请重新打开编辑页面`
- stale header: `标签数据已被批量修改，请重新打开编辑页面`
- stale job revision: `后台任务的标签版本已过期`

## Shared project label lock

`ProjectLabelLock` uses collection `project_label_lock` with
`{_id: projectId, owner: uuid, created: utc}`. Acquisition inserts a unique
row and polls every 25 ms for at most 2 seconds. There is no TTL and no owner
takeover; a stalled writer keeps the lock until its owner deletes it. Release
is an owner-CAS delete.

The acquire path creates its owner before any write and wraps the whole
acquisition in a cleanup path. If the post-insert project read, revision check,
recovery check, a hook, or the insert acknowledgement fails, it deletes only
`{_id, owner}` with an independent five-second budget (never the cancelled
request token). An unknown insert outcome therefore removes our own row if the
server had committed it; a duplicate wait never deletes another owner's row.
Normal lease disposal uses the same bounded owner-CAS release. If that bounded
delete fails, `ILogger<ProjectLabelLock>` receives a warning because the lock has
no TTL and needs manual recovery; the failure is never silently reported as a
successful release. `ProjectLabelLockHooks` is an optional test-only seam around
the post-insert acknowledgement and the project read; production leaves it null.

`LabelRequestContext` is a scoped object shared by the middleware and the
service. It makes the lock re-entrant within one request: the middleware owns
the lease, and service calls borrow it. This avoids a second insert against the
same non-reentrant lock. The public service methods enter exactly once;
private mutation helpers never enter again.

`LabelSession` exposes `Project`, `Revision`, `Recovery`, `AssertEpoch(row)`
and `AssertReferenceEpoch(row)`. Rows without `label_epoch` are treated as
revision 0. These are the hooks the next snapshot phase should use; they are
not wired into FileService or ProjectLifecycleService yet.

### Request guard

`LabelRequestGuard` resolves the project from `projectId`, `fileId`,
`sourceId`, `translationId`, `tipId` or `targetId`, with a path-segment
fallback. It:

- rejects ordinary requests while `project.label_recovery` is set, except
  paths containing `/label-` or `/snapshots`;
- takes the lock for every non-OPTIONS request, including GET/HEAD, until
  serialization;
- after lock acquisition requires `X-Label-Revision` for non-GET/HEAD writes
  on label paths when the current revision is greater than 0;
- rejects a supplied header that does not equal the locked revision (including
  revision 0);
- appends `X-Label-Project`, `X-Label-Revision` and the CORS expose header.

The guard must be placed after routing and inside the existing error
middleware so `ApiError` retains the old response shape.

Revision headers are written from `Response.OnStarting`, so they also appear on
error responses (including recovery/lock 409s). The project re-read used for
those headers has its own bounded five-second token; it never uses the request
token or an unbounded `CancellationToken.None`, and a header failure never
overrides the endpoint result. Header cleanup runs in a nested `try/finally`,
so a failure while writing headers cannot abandon an owned lock. The optional
`LabelRequestGuardHooks.BeforeRevisionHeadersAsync` seam exists only for
failure-injection tests.

If an owner-CAS release fails, `ProjectLabelLock` logs only the project id,
owner id and exception type. It does not log the exception object or connection
details, because Mongo exceptions can contain endpoint credentials.

## DI / startup wiring (wired in this checkpoint)

```csharp
builder.Services.AddLabelServices();   // lock + scoped context + service

// after routing and inside the existing ApiError middleware:
app.UseLabelRequestGuard();

app.MapLabels();

await app.Services.InitializeLabelIndexesAsync();
```

`InitializeAsync` creates the source, translation, tip and
`file_target_cache` indexes. The translation and tip unique keys are the legacy
`{o,t,u}` keys; an existing equivalent index under another name is accepted
(codes 85/86).

`PresenceService` is registered by the baseline Program through
`AddPresenceServices()`; the label service also resolves it optionally with
`IServiceProvider.GetService`, so tests or reduced hosts without presence still
skip heartbeat safely and never fail a label write.

## Intentional differences / review items

1. **Translation PUT pre-validation.** Python may apply `selected` before
   discovering that `content` belongs to another user, leaving a half-written
   selection. The C# service validates every requested field's permission
   first, then mutates. This is a safety correction; it should be reviewed
   before claiming byte-for-byte parity.
2. **Source delete tips.** Python `Source.clear()` deletes translations and the
   source, but does not explicitly delete tips. The C# delete cascades tips to
   keep the label snapshot consistent. This is a deliberate data-integrity
   difference for review.
3. **Selected/unselected count details.** Selection of a blank source follows
   the Python rule: selecting a blank source does not increment
   `checked_source_count`, but unselecting an existing selected row still
   decrements it.
4. **`mt` and uniqueness.** The lookup includes `mt`, but the unique index does
   not. A manual create against an existing `mt=true` row therefore returns
   8007, matching the legacy index behavior.
5. **Tip create/edit empty check.** Creation checks `content == ""`; PUT uses
   whitespace trimming (`content.Trim() == ""`) like the Python views.
6. **HTTP body parsing order.** Path ids are resolved before the body. Field
   type decoding (for example `x` as a number) happens in the endpoint before
   the service's image/type/permission checks. A request that is both
   unauthorized and carries a malformed field can therefore report the field
   error first. Valid requests and direct service calls follow the Python
   type/permission/schema order; this HTTP-layer nuance is noted for review.

## Remaining integration scope

E1 is wired in this checkpoint: `Program.cs` calls `AddLabelServices`, places
`UseLabelRequestGuard` after explicit `UseRouting` and inside the existing
ApiError middleware, maps `MapLabels`, and initializes the label indexes.

The following remain outside this checkpoint and are not claimed complete:

- Snapshot creation/restore and revision increments. E2 snapshot code exists
  in the worktree, but it is not integrated or enabled by this checkpoint.
- Wiring `LabelSession`/epoch checks into `FileService` or
  `ProjectLifecycleService`. They need a specific full-chain integration pass;
  adding only a guard call would not prove the complete path.
- G2 stable import ids and idempotent counter lower helpers. G2 will return the
  precise minimal contract separately rather than hand-writing E1 counters.

## Tests and evidence

`tests/Shoreline.Tests/LabelTests.cs` contains 26 direct-service facts covering
listing/paging, source CRUD and rank movement, translation uniqueness/selection/
counts, tip upsert/ownership, legacy lock blocking and owner CAS, recovery,
expected revisions, stale rows and no-epoch legacy rows. It also contains three
real failure-injection facts for the lock: a cancelled post-insert project read,
a lost insert acknowledgement after the server committed the row, and a
duplicate waiter that must not delete another owner's row.

`tests/Shoreline.Tests/LabelHttpTests.cs` contains six real HTTP facts. Each
fact owns a separate random database and uses `WebApplicationFactory<Program>`,
routing, the guard, the label endpoints and a real JWT. They cover token and
missing-resource codes, the source → translation → select → proofread → tip →
list flow, `my_translation` versus other translations, pagination headers,
malformed schemas and permission failures, revision/recovery guard semantics,
two concurrent requests sharing the project lock, and the single canonical
`file_target_cache` document. The last fact injects a revision-header failure
and verifies that the owned lock is still released.

The isolated validation tree was built from stable baseline commit `b528acb`
with exactly eleven overlaid paths: the seven E1 production files, the two
label test files, the label compatibility document and the minimal `Program.cs`
wiring. No other work-in-progress files were copied. The shared worktree and
the isolated tree have identical SHA-256 hashes for all eleven paths. Results:

- restore and build: 0 warnings, 0 errors;
- `LabelTests` + `LabelHttpTests`: 32 passed;
- full suite: 386 passed, 0 failed.

The earlier 309-pass figure belongs to the old `e910283` baseline and the
336-pass figure belongs to the intermediate `0eddb2e` tree; neither is the
current checkpoint figure. The isolated test HTTP host disables
`Notifications:WorkerEnabled` to avoid the new background scan worker touching
test databases. The validation phase did not commit and did not use the shared
worktree for build or test. This E1 checkpoint is now committed locally; it has
not been pushed.
