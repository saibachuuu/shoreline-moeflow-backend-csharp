# Output / export compatibility (C# port)

This document records the Python-to-C# contract for project target exports. The
C# implementation is additive: it does not modify `Program.cs`, `MongoStore`,
`JobQueue`, `JobWorker`, `ProjectService`, cleanup, file/label/snapshot/storage
code. The integrator owns final DI/route wiring.

## API surface

Routes are mounted under `/v1/projects/{projectId}`:

| Method | Path | Python source |
| --- | --- | --- |
| POST | `/outputs` | `ProjectOutputListAPI.post` (all targets) |
| GET | `/targets/{targetId}/outputs` | `ProjectTargetOutputListAPI.get` |
| POST | `/targets/{targetId}/outputs` | `ProjectTargetOutputListAPI.post` |
| POST | `/v1/teams/{teamId}/outputs` | `TeamProjectOutputListAPI.post` (all WORKING projects) |

The three project routes require `project:OUTPUT_TRA`. The team route requires
`team:AUTO_BECOME_PROJECT_ADMIN` (`app/apis/team.py:498-506`); that permission is
now part of the C# creator/admin team defaults, matching Python
`TeamRole.system_role_data` (`app/models/team.py:146,177`). A target whose `t`
is another project returns error 4024 (`项目目标语言不存在`). Missing output is
9001 (`导出导出文件不存在`); cooldown is 9002 (`导出过于频繁，请稍后再试`).

GET sorts by `ct` descending. Python's `Output.objects.order_by("-create_time")`
has no `_id` tie-break; the C# list adds `_id` descending as a deterministic
tie-break only. The API shape is the Python `to_api` shape:

```
id, project, target, user, type, status, storage_bytes, status_details,
file_ids_include, file_ids_exclude, create_time, link?
```

`project` uses `ProjectService.ViewAsync(project, actor: null, ...)`, `target`
uses `TargetViewAsync`, and `user` uses `UserView.ToApiAsync`; `null` user stays
`null`. `status_details` is the six-entry Python list (ids 0..5, names
`排队中 / 源文件整理中 / 翻译整理中 / 压缩中 / 已完成 / 导出错误，请重试`).
`link` is included only when `status == 4`, signed for 7 days. No raw BSON is
returned.

## Canonical output document

```
{ _id, p, t, u, s, ty, file_name, sb, ct,
  file_ids_include: ObjectId[], file_ids_exclude: ObjectId[] }
```

The historical Python field is **`file_name`**, not `fn`: `df_field="fn"` was
ignored by the old model. `file_name` starts empty; `sb` is a `long` and starts
at 0. Include/exclude are always arrays in the C# document (empty when absent),
and are returned as strings in the API. During publish, C# additionally stores
a temporary `job_accounting` marker (cleared with the success update); it is
internal and never returned by the API.

Types: `0 = ALL`, `1 = ONLY_TEXT`. Status: `0 queued`, `1 downloading source`,
`2 building translations`, `3 zipping`, `4 succeeded`, `5 error`.

## Create, cooldown, prune

`POST /targets/{id}/outputs` and all-target POST take the existing project
label lock. The newest row by `ct` is the cooldown anchor; rows younger than
`Outputs:WaitSeconds` (fallback `OUTPUT_WAIT_SECONDS`, default 300) are skipped
for all-target, and rejected with 9002 for a single target.

Pruning keeps the newest **2** rows and deletes everything from `skip(2)`
before inserting the new row, so at most 3 remain. Pruning is intentionally
strict and follows the required order:

1. delete all physical objects (prefix `outputs/<id>/`, `file_name`);
2. only after every object deletion succeeds, delete the rows;
3. only after deletion, release bytes: each output's `sb` is CAS-reset to 0,
   then project `ob -= size` and `fs -= round(size/1024)`.

If a physical delete fails, the rows and byte counters are left intact. This is
stricter than Python's best-effort `delete_real_files` (which could delete
metadata after a swallowed object failure) and matches the cleanup module's
strict mode. A project with a non-empty `csid` resolves its backend through the
storage-pool router; if the pool has no usable record for that id, the call
fails closed with error code 6 (`StorageSourceUnavailable`, HTTP 409) and never
silently reads a different provider.

The output row is inserted under the lock, then the job is enqueued after
release. Enqueue failure marks the row `s=5` with `job_error` and never
pretends the export succeeded. A normal request returns a queued row; the
client polls GET. No synchronous fallback is performed.

### Team-wide export

`POST /v1/teams/{teamId}/outputs` mirrors
`app/tasks/output_team_projects.py:21-66`: it selects projects by
`{t: team, st: 0}` (Python `team.projects(status=ProjectStatus.WORKING)`), then
runs the same per-target all-target export as `POST /v1/projects/{id}/outputs`
— cooldown skip, newest-two pruning, type `0` (`ALL`) and one queued job per
created row. The response is `{"message": "创建导出任务成功"}`.

Differences / unverified:

- Python may execute the Celery task synchronously when no worker answers
  (`output_team_projects.py:69-80`); C# always enqueues and returns immediately,
  like the existing project output routes.
- Content is produced per project, so the team call creates N independent
  archives, never a combined team archive.
- A project whose `csid` has no usable pool record fails the whole call closed
  with 409/code 6 before its row is inserted; a valid pooled project resolves
  that project's backend through the router. Real R2/storage-pool export remains
  unverified in C#.
- The Python task has no per-project exception handling; the first failure
  aborts the remaining loop. C# propagates the same way, leaving earlier
  queued outputs intact.

## Job, leasing, fencing

Job kind: `output_project`. Payload:
`{ output_id, project_id, label_revision }`.

`OutputJobHandler` is a singleton with `IServiceScopeFactory`; every execution
creates a fresh scope and resolves the scoped `OutputExportService`, so the
scoped label context is never captured. The existing worker supplies the
5-attempt / 2-minute lease behavior.

Each output additionally has an execution owner/lease (`job_owner`,
`job_run_token`, `job_started_at`, `job_lease_until`). Claim is a CAS on
`_id + s != 4 + lease absent/expired`. The worker renews every 30 seconds and
cancels all remaining IO if renewal loses ownership. A renewal exception is
treated as lost ownership too. Release, mark-error, stale-revision marking and
the final staging cleanup are each bounded to 5 seconds and cannot mask the
original error; the `finally` block always attempts staging/temp cleanup
(bounded to 5 seconds because object storage may be slow). The artifact record and the success update are guarded by
`job_owner + job_run_token + lease > now + s != 4`; the final copy is performed
under the project lock after an owner check. Because both claim and publish
take the project lock, a newer owner cannot be claimed or marked succeeded
while a stale executor is copying. A stale executor that loses its lease while
copying cannot record the artifact or mark success.

The long phase is fenced as follows:

1. acquire the existing `LabelRequestContext` under the project lock, verify
   `label_revision`, claim the output, set status 2;
2. freeze LabelPlus text, `project.json`, file metadata and source storage
   pointers to an owned temp directory/manifest, then release the project lock.
   File metadata is bounded `O(files)` and kept in memory because the global
   `dn/t/mo/sn` ordering is a project-wide order; the source cursors use
   `BatchSize(200)`. The internal `manifest.json` is also proportional to the
   exported file count and is not stored in MongoDB. It is serialized and kept
   in process memory for the publish check, so a project large enough to produce
   a manifest over the 16 MiB BSON limit can use that much managed memory even
   though it is never written as a MongoDB document.
3. status 1, download images and build the ZIP; status 3;
4. upload to a unique staging key
   `outputs/<output>/.work/<run-token>.<ext>` (never the final key);
5. reacquire the project lock with the frozen revision; verify revision,
   no recovery, project not clearing, owner still current, target/output still
   present, and each frozen file row still exists with the same (`p`, `sa`);
   then server-side copy staging -> `outputs/<output>/<file_name>`;
6. record the artifact (`sb`, `file_name`, durable `job_accounting`) while the
   publish CAS still holds, apply the project ledger, then mark status 4 with a
   fresh publish CAS; finally delete staging and the run directory.

If the target was deleted, the output was replaced, the project started
clearing, the source pointer changed, or the lease/epoch was lost, the job
fails without publishing. Stale revisions mark the row error (recoverable);
only the current owner may set status 5. Cancellation and fence loss abort the
ZIP; a missing source image is different: it is recorded in `errors.txt` and
the ZIP still succeeds.

## LabelPlus output

`translations.txt` is written UTF-8 **without BOM**, with the exact header:

```
1,0\r\n-\r\n框内\r\n框外\r\n-\r\n可使用 LabelPlus Photoshop 脚本导入 psd 中\r\n
```

Files are all active `IMAGE` files of the project (not only roots), applying
`file_ids_include` then `file_ids_exclude`. Default order is
`dn, t, sn`; if **any** selected file has non-null `mo`, the order becomes
`dn, t, mo (nulls last), sn, _id`.

With manual order, every exported file gets `0001_<name>` (width
`max(4, digits(fileCount))`); the same mapping is used both for the LabelPlus
file header and for the ZIP image entry. Without manual order the header uses
`ancestor names joined by "/" + "/"` and the raw name; ZIP entries use the raw
name.

Per file:

```
>>>>>>>>[name]<<<<<<<<\r\n
----------------[1]----------------[x,y,p]\r\n
<translation>\r\n
...
```

Sources sort by `r` only, matching Python `File.sources()`; equal-rank rows
keep their natural order and numbering starts at 1. `x`,
`y` and `p` use Python `str(float)`/int-compatible formatting: `0.0` is not
`0`, and exponent formatting follows Python (`1e-07` fixture). Translation is
the first row ordered by `-s, -p, -e, -ct, -id`; the whole record is used, then
`proofread_content` if truthy else `content`, else empty. Newlines are
`content.Replace("\n", "\r\n")`, so pre-existing CRLF intentionally becomes
`\r\r\n`; this is not normalized. Best translations are fetched in one
aggregation per batch of up to 200 source ids (`$match` -> Python sort ->
`$group` first per source), never one query per source and never by loading
every translation row into RAM.

Staff block: active project members (external included) are grouped in
`raw_provider, scanner, cropper, cleaner, translator, proofreader, typesetter`
order, labelled `图源, 扫图, 裁切, 修图, 翻译, 校对, 嵌字`; display names replace
CR and LF with spaces and join with `、`, one line per non-empty role. Block:

```
----------------[0]----------------[0.5,0.5,2]\r\n
翻译：xxx\r\n
```

Page resolution: project `slp`, else team `slp`, else `1`. Positive `page`
places the block before the sources of file `page-1` (clamped to last).
A negative `page` places it after file `count + page` (clamped to first);
`page == 0` resolves to index `count` and therefore places no block, matching
the legacy Python helper exactly.

## Project JSON and archive

`project.json` uses Python's key order:

```
name, intro, default_role (system_code string), allow_apply_type,
application_check_type, is_need_check_application, create_time, edit_time,
source_language.code, target_languages[].code, staff_list_page,
position_lock_enabled, position_lock_tags
```

then appends `output_id` and `output_language`. It uses the Python-compatible
JSON serializer (ensure_ascii, `", "` / `": "` separators, Python float/iso
formatting).

`ALL` produces `<download_name>.zip` containing `translations.txt`,
`project.json`, optional `errors.txt`, and `images/**` with nested paths
preserved. `ONLY_TEXT` produces `<download_name>.txt` containing only
`translations.txt` content. `download_name` is
`<set name> - <project name> - <language i18n name>` unless the project set is
default, in which case the set prefix is omitted. Unsafe filename characters
`[/\\?%*:|"<>\x7F\x00-\x1F]` become `□` for download/header names only; the
DB `file.name` is never mutated. ZIP entries are checked against zip-slip.

Images are read from the original object key
`files/<save_name>` (FilePrefix default `files/`). Missing objects write
`File <name><<id>> is not found in server.\r\n`; other download failures write
`File <name><<id>> download error.\r\n`. `errors.txt` uses the exact Python
header:

```
Project Name: ...
Project ID:   ...
Target ID:    ...
Output ID:    ...
------------------------
```

The ZIP is streamed from disk to storage; it is never assembled as a
`byte[]`. Entries are stored (no compression), matching Python's default
`zipfile.ZipFile(path, "w")`. Upload records the actual object size
(`HeadAsync`), not the local file length.

## Accounting

Accounting is two-phase and crash-consistent without a permanent operation
ledger:

1. after the final object copy, `RecordArtifactAsync` CAS-sets the output
   `sb`, `file_name` and a durable `job_accounting` marker under the publish
   guard, but does **not** mark success;
2. `ApplyProjectAccountingAsync` recomputes `sum(output.sb)` for the project
   and CAS-updates that single project document (`ob = total`,
   `fs += round((total - oldOb)/1024)`), retrying bounded concurrency races.
   The same project document is both the application and the dedup point, so a
   retry sees `total == ob` and applies zero;
3. only after the project ledger is applied does `MarkSucceededAsync` set
   `s=4` and clear `job_accounting`.

If the process dies between 1 and 2, the output is still non-success and the
job can run again; the recomputation completes the missing project delta. If
it dies between 2 and 3, the retry recomputes zero and only marks success.
Replaying a succeeded export returns immediately and adds zero. `fs` uses
`Math.Round(delta / 1024.0, MidpointRounding.ToEven)` to match Python's
banker's rounding. The staging object is deleted best-effort in `finally`
(bounded to 5 seconds); the final object is only written after all fences pass.
A crash after the final copy but before the artifact record can leave an orphan
final object keyed by the current download name; a retry overwrites that key,
while an abandoned job leaves it to later cleanup. The label lock is held while the
ledger and success marker are committed, but cleanup/target-delete counter
races remain a later cross-module integration item.

## C# files

- `src/Shoreline.Api/Features/OutputService.cs`
- `src/Shoreline.Api/Features/OutputExportService.cs`
- `src/Shoreline.Api/Features/OutputEndpoints.cs`
- `src/Shoreline.Api/Features/OutputRegistration.cs`
- `src/Shoreline.Api/Features/OutputPolicies.cs`
- `src/Shoreline.Api/Features/OutputLease.cs`
- `src/Shoreline.Api/Features/OutputAccounting.cs`
- `src/Shoreline.Api/Features/OutputObjectCleanup.cs`
- `src/Shoreline.Api/Jobs/OutputJobHandler.cs`
- `src/Shoreline.Api/Formats/LabelPlusWriter.cs`
- `src/Shoreline.Api/Formats/ProjectOutputJson.cs`
- `src/Shoreline.Api/Formats/PythonJson.cs`
- `src/Shoreline.Api/Formats/OutputArchive.cs`
- `tests/Shoreline.Tests/OutputTests.cs`
- `tests/Shoreline.Tests/OutputHttpTests.cs`
- `tests/Shoreline.Tests/OutputGoldenTests.cs`

`AddOutputServices()` registers the scoped services and the singleton job
handler; `MapOutputs()` maps the routes. The integrator must call both.

## Direct-service facts (31)

`tests/Shoreline.Tests/OutputTests.cs` contains 31 `[Fact]` tests covering:
canonical BSON/job payload; unknown storage source fail-closed 409/code 6 and
pooled export reading/writing through the resolved backend; cooldown 9002 and
all-target skip; prune keeps two and deletes objects before rows;
physical-delete failure preserves rows/bytes; foreign target 4024; status
details; exact LabelPlus header/CRLF behavior;
Python float rendering; manual padded names; staff ordering/external members;
staff page fallback/clamp; project JSON key order; safe names/traversal; ZIP
UTF-8/stored entries; two-phase banker's-rounding accounting; retry after a
crash before and after the project ledger; full export (LabelPlus, project
JSON, image, final name, bytes); missing-image `errors.txt`; replay without
double accounting; stale revision; deleted target; deleted frozen file metadata
(409); changed frozen save name (409); concurrent duplicate runs publishing once;
stale owner after lease expiry; and staging cleanup after success.

These are direct-service facts. `OutputHttpTests.cs` adds 5 real
`WebApplicationFactory<Program>` facts for token/permission/foreign-target,
canonical queued creation followed by an explicit real job-handler run,
schema/cooldown/all-target skipping, epoch-header/restore-stale failure, and the
team-wide route (team permission, legacy message, one job per target).
`OutputGoldenTests.cs` adds 3 facts reading the committed Python AST goldens for
file LabelPlus bytes, staff-block placement, and manual export names.

The team-output additions were verified from a clean `git archive 5bb9404` with
only the touched feature/endpoint/registration/test files. `dotnet build
-warnaserror` completed with 0 warnings/0 errors, the focused
`Output|AdminUser|OcrHttp` run passed 51/51, and the full isolated suite passed
783/783 with 6 skipped (789 total). The new direct facts are
`CreateTeam_UnknownStorageSourceFailsClosedWithoutPartialOutput` and
`Export_PooledProjectReadsAndWritesThroughSourceBackend`.

## Open boundaries / integration notes

- `Program.cs` is untouched; `AddOutputServices()` and `MapOutputs()` still need
  to be wired by the integrator, after the label service registration is in
  place.
- Output storage resolves a non-empty `csid` through
  `ProjectStorageRouter.ForProjectOrNullAsync`; a missing/disabled pool record
  fails closed with 409/code 6. Default storage keeps the legacy path.
  Real pooled-provider export still lacks production credentials.
- `storage.CopyAsync` (server-side copy) must be supported by the configured S3
  provider; a provider without copy will fail publish rather than fall back to
  a non-atomic byte copy.
- Snapshot/recovery integration is not enabled in this pass. The worker uses
  the existing project label revision and rejects a changed revision; it does
  not originate recovery locks.
- The Python Photoshop script copy is commented out in the legacy task and is
  not ported.
- Legacy no-worker synchronous fallback is not ported; C# always returns a
  queued row and relies on the in-process job worker.
- Only Chinese/English/`lo_name` fallback is used for the download language
  name; the full Python gettext catalog is outside this change.
- The succeeded-output `link` uses the existing `ObjectStorage.SignGetAsync`
  (7-day validity) with the exact `OutputPrefix + outputId + "/" + file_name`
  key. That shared storage API has no `response-content-disposition=attachment`
  override, unlike Python's `sign_url(..., download=True)`; the file name is
  still the final key segment.
