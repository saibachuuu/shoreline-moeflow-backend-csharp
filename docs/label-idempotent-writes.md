# Idempotent label writes for imports

This document describes the import-facing, stable-id helpers added in
`Features/LabelService.Idempotent.cs`. They are intentionally separate from the
normal editing endpoints: a caller (G2 import) first persists the generated
source/translation id in its own journal, then calls these helpers. A retry
after a crash or an unknown Mongo outcome must be able to converge without
incrementing label counters twice.

## Public API

```csharp
public enum SourceWriteOutcome { Created, AlreadyApplied }

public sealed record SourceWriteResult(
    SourceWriteOutcome Outcome,
    ObjectId SourceId,
    long Revision,
    bool CountersApplied);

public Task<SourceWriteResult> CreateSourceOnceAsync(
    HttpContext context,
    BsonDocument actor,
    ObjectId fileId,
    ObjectId sourceId,
    string content,
    double x,
    double y,
    int positionType,
    int sourceRank,
    long expectedRevision,
    CancellationToken ct);

public enum TranslationWriteOutcome { Created, AlreadyApplied, Empty }

public sealed record TranslationWriteResult(
    TranslationWriteOutcome Outcome,
    ObjectId TranslationId,
    long Revision,
    bool CountersApplied);

public Task<TranslationWriteResult> CreateTranslationOnceAsync(
    HttpContext context,
    BsonDocument actor,
    ObjectId sourceId,
    ObjectId targetId,
    ObjectId translationId,
    string content,
    long expectedRevision,
    CancellationToken ct);
```

`translationId == ObjectId.Empty` falls back to the legacy unique identity
`{source, target, user}`. G2 should normally pass a preallocated id and journal
it before the call.

### Source identity validation

A preallocated id is never treated as "the row exists, therefore success".
The existing row must match all of:

- `_id` and `f` (file)
- `label_epoch` (the row was written at the currently held revision)
- `c` (content, ordinal comparison)
- `x`, `y` (within `1e-9`)
- `p` (position type)

`r` (rank) is only a sorting field and is not part of identity. A conflicting
row is rejected with `ValidateError` code `2`, and the helper does not modify
it. A row belonging to another file or another import intent is never claimed.

### Translation identity validation

If the row exists, `o`, `t`, `u` and (unless content is empty) `c` must match.
`mt=true` is rejected with `TranslationNotUniqueError` code `8007`, matching the
legacy unique index. A stable id that points to another source/target/user is
never claimed.

`content == ""` is a true no-op:

- no row is inserted
- no pending marker is written
- no counters are reconciled

For an existing row this returns `AlreadyApplied` with `CountersApplied=false`.

### Common preconditions

Both helpers run inside the shared project label lease and use the existing
`LabelRequestContext`, so a middleware-held lease is borrowed rather than
acquired a second time. They verify, in legacy order:

- project/file/row/`label_epoch` consistency
- the actor's `ADD_LABEL` / `ADD_TRA` project permission
- image type for source creation
- position type in `{1, 2}` and `x`/`y` in `[0, 1]`
- file activated state (`FileNotActivatedError` code `4016`)
- target belongs to the project (`TargetNotExistError` code `4024`)
- a stale `expectedRevision` yields `LabelBusyError` code `1`, HTTP status
  `409`, with no database writes

## Why a marker and a rebuild

Incrementing `source_count`, `translated_source_count`,
`checked_source_count`, ancestor rollups, project totals, target totals and the
canonical cache in several separate writes is not exactly-once. A crash after
some increments leaves a partially applied state that a simple "source exists"
check cannot repair.

These helpers therefore:

1. Insert (or claim) the primary row with a durable pending marker:
   `idempotent_pending`, `idempotent_owner`, `idempotent_revision`.
2. Rebuild the affected label counters deterministically from primary
   `source` / `translation` / `file` / `target` rows using `$set`, not `$inc`.
   A partial write is repaired by the next run of the same helper.
3. Touch file/project edit times.
4. Clear the pending marker only with an owner-and-revision CAS, after every
   counter write and touch has succeeded.

A retry never takes the "row exists, return early" shortcut: it still runs the
reconcile, so a marker left by a failed first attempt converges.

The marker is not a replacement for the project lock. No new lock or TTL is
introduced. All normal label writes and these import writes use the same
`ProjectLabelLock`; a reconcile that observes a changed project revision fails
with the usual `LabelBusyError`.

### Test-only stage hook

`LabelIdempotentHooks.AfterStageAsync` is a deterministic fault-injection seam.
Production leaves it `null`. Stage names are stable:

- `source_inserted`
- `translation_inserted`
- `reconcile_files`
- `reconcile_cache`
- `reconcile_project`
- `reconcile_targets`
- `idempotent_touched`

## Counter rebuild rules

`LabelCounterReconciler` is public so a future import service can reuse it; E1
does not reference the E2 statistics implementation. It only writes label
counters and never touches storage, file counts, derivatives, output or
accounting.

The rules mirror `app/services/project_statistics.py`:

- **Sources**: `sc` is the number of non-blank sources (`b != true`) whose
  `f` is the file. Every source, image or text, counts if it is not blank.
- **Translations**: a translation counts only when its source is non-blank and
  its target belongs to the project. `ts` is the count of distinct source ids
  for `{file, target}`; `cs` is the count of distinct source ids having a
  selected translation. It is not the number of translation rows.
- **Files**: folders have their own label counts equal to `0`; non-folder files
  get `sc`, and `tsc`/`csc` are the sums of their canonical per-target cache
  values.
- **Active files**: contribute to project totals (`sc`/`tsc`/`csc`), target
  totals and ancestor rollups. Every ancestor must exist; a missing ancestor is
  a `ValidateError`.
- **Inactive files**: keep their own `sc`/`tsc`/`csc` and canonical cache, but
  do not contribute to project/target totals and do not roll up to ancestors.
- **Canonical cache**: the collection is `file_target_cache` (underscored), not
  `filetargetcache`. Reconcile updates the existing `{f, t}` document in place
  with `ts`/`cs` and `label_epoch`; it never creates a parallel cache
  collection. A missing cache row is upserted with `e` on insert.
- **Batching**: source lookups use an `_id` projection and cursor chunks of
  `200`; writes use bulk batches of `500`. The full project `source` set is
  never materialized in memory. Translation counting is a Mongo aggregation
  over primary rows. Memory is O(files × targets) for the in-memory file
  state, not O(all sources).

Label edit-time fields are intentionally distinct:

| Document | Edit-time field |
| --- | --- |
| project / team | `m_e` |
| project_set | `et` |
| file / target | `et` |
| source / translation / file_target_cache | `e` |
| tip | `et` |

## Journal / recovery contract for G2

The intended durable import order is:

```text
journal source id -> source upsert -> journal translation id
-> translation upsert -> reconcile (helpers) -> G2 checkpoint
```

A retry at any point is safe for these helpers:

- source row committed but acknowledgement lost: lookup by stable id, validate
  intent, reconcile
- source insert never committed: retry the same id
- translation row committed but acknowledgement lost: lookup by stable id,
  validate identity, reconcile
- a counter/pending write failed: retry the same helper; the rebuild converges

This is a convergence guarantee **only for these import helpers**. It does not
retroactively make arbitrary legacy label writes crash-safe, and it does not
introduce a transaction. Standalone MongoDB is sufficient.

## Tests and evidence

`tests/Shoreline.Tests/LabelIdempotencyTests.cs` contains 17 direct-service
facts. They use real MongoDB (the normal local test fixture) and cover:

- source insert committed but acknowledgement lost; retry converges
- failure after file, project, canonical cache and target counter writes;
  retry twice and compare the rebuilt counts with primary rows
- translation insert committed but acknowledgement lost; retry converges
- translation failure after a reconcile stage; retry converges
- empty translation content is a no-op (no row, no counter write)
- a stable source id on another file is rejected without changes
- a stable source id with different content/position intent is rejected
- a stable source id with a stale `label_epoch` is rejected with `409`
- a stale expected project revision is rejected with no writes
- a rank collision with another source does not claim that source
- a stable translation id on another source is rejected
- folder rollup, inactive files, multiple targets, blank sources and selected
  distinct-source counting
- a service call borrowing an already-held request lease does not deadlock
  and does not leave the owned lock row behind

The implementation was verified only in an isolated `git archive 3edd683` tree
with the three new files copied in:

```text
dotnet build tests/Shoreline.Tests/Shoreline.Tests.csproj -c Debug --artifacts-path .artifacts
  -> 0 warnings, 0 errors
dotnet test ... --filter FullyQualifiedName~LabelIdempotencyTests
  -> 17 passed, 0 failed
dotnet test ...
  -> 403 passed, 0 failed
```

The previous E1 checkpoint baseline was `386` full tests; `403 = 386 + 17`.
No shared working tree was used as evidence. No commit was created.

## Known boundaries

- No TTL and no takeover of a non-expired project label lock; recovery follows
  the existing `ProjectLabelLock` owner-CAS rules.
- The reconciler rebuilds label counters for all files in one project and all
  project targets. It does not repair non-label fields (`fs`, `fc`, `fo`,
  output/accounting counters).
- A normal HTTP `LabelService` edit and an import helper may interleave only
  through the shared project lock; no separate import lock exists.
- These helpers do not change `Program.cs`, `LabelEndpoints`, or the normal
  editor handlers. G2 must wire its journal and call them explicitly.
