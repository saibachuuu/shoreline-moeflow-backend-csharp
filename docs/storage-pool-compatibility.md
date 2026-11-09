# Storage pool compatibility (D2)

Status: **implemented as new, unwired code; build and integration tests pass in an
isolated snapshot, but it is not wired into the shared Program or deployed.**
The D2 files were added on top of the D0 commit
`33c93ea17beea6398df5c497f030cd498f031d6a` and intentionally do not modify
`Program.cs`, `MongoStore`, the D0 storage files, `FileService`, `OutputService`,
`ProjectService`, project lifecycle, label locking, or the API project file.
Verification used a throwaway snapshot made from repository commit `3edd683` with
only the D2 files copied in. It built with 0 warnings and 0 errors and passed all
34 D2 facts. A full-suite run on the preceding 33-fact snapshot passed 419/419;
the full rerun after adding the 34th fact was blocked by local MongoDB connection
loss and is not claimed as a clean full-suite pass. The shared working tree was not
built or committed by this work.

No real R2 or other production credentials were used. The only exercised storage
target available to this work is the local S3Mock service. S3Mock does not
validate S3 signatures, so a passing S3Mock test would not prove real R2
authorization, ACL, lifecycle, or OSS compatibility. Aliyun native OSS signing is
still not ported.

## Compatibility boundary

| Python setting | Actual backend | C# setting |
|---|---|---|
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=R2/MINIO/GARAGE/AWS/unset` | standard S3-compatible | `Storage:Type=S3` or `Storage:Type=R2` |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=S3` | Aliyun OSS native (historical naming) | unsupported; fail closed |
| `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=ALI/ALIYUN/ALIYUN_OSS/OSS` | Aliyun OSS native | unsupported; `Storage:Type=ALI` fails fast |
| `STORAGE_TYPE=LOCAL_STORAGE` | local filesystem | `Storage:Type=Local` + `Storage:Root` |

Python's old default key prefix is `storage/`; the C# D0 default is empty for
existing C# API compatibility. A deployment that still has objects below
`storage/` must set `Storage:KeyPrefix=storage/` explicitly. The per-source
`sub_path` in this module is a separate, source-local prefix.

`STORAGE_TYPE=OSS` alone is **not** enough to decide whether the old deployment
was Aliyun or S3. `OSS_BUCKET_STYLE` is the deciding setting.

## Project routing

`Shoreline.Api.Storage.ProjectStorageRouter` is the generic, scoped routing
boundary. It does not reference `Modules.StoragePool` and remains usable when
the storage-pool module is disabled.

- `ForDefaultAsync()` returns a borrowed lease over the singleton
  `ObjectStorage`; the router never disposes that shared client.
- `ForOriginalAsync`, `ForResampleAsync`, and `ForOutputAsync` select storage
  from the project's `csid` (`storage_source_id`).
- A project with no `csid` uses the default storage.
- A non-empty `csid` with no registered resolver, a disabled source, or a missing
  source fails closed with HTTP 409 and error code 6. It never silently falls
  back to the default bucket.
- Resolvers are registered through `ProjectStorageResolverRegistry`; the
  storage-pool module registers `StoragePoolSourceResolver`.
- Every operation should hold the returned `StorageBackendLease` for its
  duration. Only pooled backends created for that lease are disposed; the default
  backend is borrowed and left alive.

`StorageBackendFactory` creates a pooled backend from an immutable
`StorageBackendDescriptor` (endpoint, bucket, prefix, region, path-style,
credentials, optional CDN domain). Descriptor `ToString()` exposes endpoint,
bucket and prefix only. Credentials are never part of an API view, log, or
exception message. `verify_ssl=false` is rejected explicitly; TLS validation is
never disabled.

The factory's same-endpoint server-side copy always uses the **source bucket and
source prefix** for `CopySource`. A destination bucket containing an object with
the same relative key is not allowed to become the copy source. When descriptors
differ in endpoint, or the source is local, the transfer engine falls back to
streamed download/upload rather than buffering a business object in memory.

## HTTP surface

The module exposes exactly the Python admin routes under
`/v1/admin/storage-pool` and requires admin authentication:

- `GET/POST /sources`
- `PUT/DELETE /sources/{sourceId}`
- `POST /sources/{sourceId}/test`
- `GET /transfers`
- `POST /transfers/{transferId}/retry`
- `POST /transfers/{transferId}/cancel`
- `DELETE /transfers/{transferId}`
- `POST /projects/{projectId}/migrate`
- `POST /projects/{projectId}/restore`
- `POST /projects/{projectId}/move`
- `POST /projects/{projectId}/lock`
- `GET /stats`
- `GET /usage`
- `GET/PUT /settings`
- `POST /scan`

Errors use the existing `ApiError` response shape:

| Status | Code | Meaning |
|---|---|---|
| 404 | 1 | source missing |
| 409 | 2 | source is still referenced by a project |
| 409 | 3 | source location is locked because it has data |
| 400 | 4 | source connection/probe failed or `verify_ssl=false` |
| 409 | 5 | operation not allowed (locked project, invalid move, etc.) |
| 409 | 6 | source is busy / unavailable / referenced by an active transfer |
| 409 | 7 | transfer state is not valid for this operation |

Source create and credential/location updates perform a real
put/head/get/delete probe **before** persistence, using a temporary object under
the configured `Storage:FilePrefix` (default `files/`). A failed probe leaves no
source row behind. Blank access/secret keys on update preserve the stored values.
`has_data` blocks endpoint, bucket, normalized sub-path, region, and addressing
style changes. Delete rejects both a project reference and any active transfer
that uses the source as its origin or MOVE target.

## BSON schema

Collections are suffixed/typed exactly as in Python:

- `storage_pool_source`: `n`, `e`, `ep`, `b`, `sp`, `r`, `as`, `vs`, `ak`, `sk`,
  `pd`, `rm`, `iad`, `mo`, `hd`, `ct`, `ut`; index on `e`.
- `storage_transfer`: `s`, `ts`, `p`, `st`, `dir`, `set`, `to`, `co`,
  `processed`, `skipped`, `verified`, `deleted`, `tb`, `cb`, `bd`, `npc`,
  `stt`, `ftt`, `le`, `ct`; indexes on `(p,s)`, `st`, and `(-ct,-id)`, plus
  a unique partial index on `p` for active rows (`st` 0..3) so two directions or
  targets cannot run concurrently for one project.
- `storage_transfer_object`: `t`, `sk`, `dk`, `kd`, `sz`, `et`, `st`, `at`,
  `le`; indexes on `(t,st)` and `sk`.
- `storage_pool_setting`: singleton `_id=default` with `se`, `re`, `rmb`, `rmo`,
  `rmp`, `ub`, `ut`, `lst`, `lsr`, `lsq`.
- `storage_pool_lock`: `_id=projectId`, `t`, `o`, `ea`; 1800-second lease with
  owner/transfer-conditional release.
- `storage_transfer_run_lock`: unique `_id=transferId`, `o`, `ct`; no TTL.
- `storage_pool_audit`: append-only settings audit rows.

API views expose `access_key_id` masked to first/last four characters (shorter
keys are fully masked). `secret_access_key` is never returned and is not included
in JSON, logs, or exception messages.

Transfer statuses: `0 PENDING`, `1 COPYING`, `2 VERIFIED`, `3 SWITCHED`,
`4 DONE`, `5 FAILED`, `6 ABORTED`, `7 CANCELLED`; active = 0..3, retryable =
5..6. Directions: `0 IN`, `1 OUT`, `2 MOVE`. Object statuses: `0 PENDING`,
`1 COPIED`, `2 VERIFIED`, `3 SOURCE_DELETED`, `4 FAILED`, `5 SKIPPED`.

## Transfer behaviour

The manifest is bounded to image originals (`files/<save_name>`), their
`files/resample-<stem>.webp` objects, and non-empty output objects
(`outputs/<output-id>/<file_name>`). TEXT files, covers, proofread files, and
avatars are not migrated. The manifest is persisted as object rows before the
transfer starts; `total_bytes` is the project's current `fs * 1024`.

An active transfer with the same project, source, direction, and target is
reused. `storage_transfer_run_lock` prevents a second worker from running the
same transfer. Copy is resumable:

- `PENDING -> COPIED` only after destination head reports the source HEAD size
  (0-byte objects included). Stream fallback also counts bytes read and rejects
  a short read that returned success; server-side copy is checked by destination
  HEAD. `et` is stored as a diagnostic destination ETag but is not compared
  across providers because multipart ETags are not portable.
- A missing resample is `SKIPPED`; a missing original or other failure is
  `FAILED`.
- `COPIED -> VERIFIED` requires destination size to match the recorded source
  size (again including zero); cross-provider ETag equality is not required.
- The project pointer is switched only after all expected objects are verified.
- Pointer switch runs under the existing project label lock and the storage-pool
  project lock. It re-checks working status, `cip`, `csid`, edit-time snapshot,
  and that the manifest gained no new keys. `IN` accepts `null` or the target
  source; `OUT` accepts `null` or the current source; `MOVE` accepts the origin
  or target. Any other state aborts without switching.
- Source deletion happens **after** the pointer switch and deletes only
  `VERIFIED` rows in repeated batches of at most 1000 until drained. A failed
  source delete leaves a checkpoint and can be retried; the pointer is never
  rolled back to data that may already be gone.
- Three stalled rounds with no movement mark the transfer `FAILED`; if a batch
  makes progress the stall counter resets. Residual source objects after a
  delete failure keep the checkpoint and retry later rather than silently
  clearing it.
- Counters are recomputed from object rows rather than incremented, so a retry
  does not double-count. Per-project verify, metrics, statistics, and recompute
  passes use cursors and do not materialize all rows.
- `FAILED` retry resets only failed rows to pending. `ABORTED` retry rebuilds the
  manifest and resets snapshots/counters; verified rows are not discarded.

`OUT` and `MOVE` metrics update the original file's `fs` (ceil KB),
`resample_bytes`, output `sb`, and project `fs/cover_bytes/resample_bytes/ob`
from actual verified objects. The engine does not call the private label
snapshot statistics helper.

## Scan and worker

The scheduled scan is disabled by default (`se=false`). A forced call ignores
`se` but still respects the candidate rules and global budget. Candidates must
be working, not clearing, unlocked, without a `csid`, older than the source's
`inactive_days`, and free of fresh presence (heartbeat within 5 minutes or
expiry in the future), active transfer, or an edit within 15 minutes. The scan
reloads each candidate before preparing it.

`rmp` limits projects per scan and `rmb` is a global byte budget shared across
all sources. The first oversized project is allowed; after at least one project
has started, adding another project over budget stops the scan. `re=false`
prepares a pending checkpoint without dispatching it. A skipped candidate does
not update the last scan timestamp.

`StoragePoolWorker` is optional and may be disabled with
`StoragePool:WorkerEnabled=false`. It is registered only by
`AddStoragePoolServices`; the core application remains runnable without the
module.

## Wiring required (not done in this change)

The shared host still needs to opt in:

1. Call `builder.Services.AddStoragePoolServices();`.
2. After `MongoStore.InitializeAsync`, call
   `await app.Services.InitializeStoragePoolAsync();` so the resolver is
   registered and indexes/settings are created.
3. Call `app.MapStoragePool();`.
4. Optionally set `StoragePool:WorkerEnabled=false` when an external worker will
   drive transfers and scans.
5. Route File/Output/cleanup reads and writes through
   `ProjectStorageRouter` in a later, separately reviewed change. The existing
   File/Output/cleanup code paths intentionally still use the default storage.

The module also exposes the generic router and factory separately through
`AddProjectStorageCore` for a future storage-pool router without requiring the
admin module.

## Tests added (isolated execution passed)

`tests/Shoreline.Tests/StoragePoolTests.cs` contains 34 direct facts covering:

- source probe-before-persist, `verify_ssl=false` rejection, blank credential
  preservation, location lock, and active MOVE target delete rejection;
- router default borrowing, fail-closed unknown source, and
  Original/Resample/Output routing;
- descriptor/API credential redaction;
- manifest preparation and active-transfer idempotency;
- IN/OUT/MOVE end-to-end with S3Mock, including the source-bucket decoy check;
- different-endpoint-authority stream-copy fallback;
- missing resample skip, missing original failure, edit-time abort;
- short-write rejection (successful destination write of 500 of 1000 bytes) with
  no pointer switch/source delete, and zero-byte object copy/verify/switch;
- already-switched pointer resume without reverse copy or current-destination
  deletion;
- concurrent different-direction/target rejection while one project transfer is
  active;
- run-lock replay prevention, failed/aborted retry, cancel/delete rules;
- project lock and expired pool-lock takeover/owner release;
- scan disabled, global budget, presence skip, run-disabled checkpoint;
- move validation, settings audit/defaults, Python-shaped stats/usage aggregation.

These tests used an isolated snapshot with the existing local MongoDB and S3Mock
services. No shared build, shared database reset, or shared tree test execution was
performed by this work. Production R2/OSS connectivity and authorization remain
outside this evidence.

## Remaining limitations

- No real R2/OSS credentials or connectivity were validated.
- S3Mock does not authenticate requests; D0's independent SigV4 verifier is the
  only signature-level evidence.
- Aliyun native OSS signing and its CDN authorization are not ported.
- The module is not wired into `Program.cs`, and File/Output/cleanup are not yet
  routed through the new project router.
- No multipart, ACL, tagging, versioning, or object-lock support.
- The local filesystem storage root is an owned deployment root, not a
  multi-tenant security sandbox.
- `IStorageBackend` and the pooled-client lifecycle are ready for a storage-pool
  router, but a bounded shared S3-client cache and D2 integration remain future
  work.
