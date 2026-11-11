# Database migrations (MongoDB)

The .NET rewrite ports the legacy Python migration chain
(`shoreline-moeflow-backend/app/migrations/`: `runner.py` plus
`versions/m0000_baseline.py` … `m0009_storage_usage_backfill.py`) to C#.
The chain runs **before** the existing API initializers so documents are
normalized before any handler reads them.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `Migrations:Enabled` | `true` | Set to `false` to skip the migration gate entirely (API boots without touching the database). |
| `Migrations:Mode` | `check` | `check` (default), `apply`, or `dry-run`. |

- `check` (default): compute pending migrations and refuse to serve traffic
  (`/health/ready` returns **503** with body `migrations-pending: <ids>`)
  when anything is pending. The database is never modified.
- `apply`: acquire the migration lock, run every pending migration
  (`up` → `verify` → ledger row), then boot. A failure stops the run
  without recording the version; the next start retries it.
- `dry-run`: report what would run without changing anything — no lock
  document, no ledger index, no writes.

## Mechanics

- **Ledger**: collection `migration_record`, one document per applied version
  (`v`, `n`, `at`, `d` ms, `c` checksum, `runner`), with unique index
  `migration_version_unique` on `v`.
- **Lock**: collection `migration_lock`, document `_id: "migrate"` with a
  30-minute `holder` lease (`Acquire` → `Renew` per migration → `Release`),
  so only one instance migrates at a time.
- **Ordering**: migrations are sorted by version id; a run stops at the
  first failure; applying a subset while an earlier migration is still
  pending is rejected (out-of-order guard).
- **Tamper detection**: every status check compares the stored checksum of
  rows written by this runner against the code checksum and aborts on
  mismatch. Rows written by the legacy Python runner predate the C#
  checksum scheme and are accepted as applied (mirroring Python's own
  acceptance of pre-identity checksums).
- **Rollback**: `LegacyMigrationRunner.RollbackAsync(targetVersion)` runs
  `down` in reverse version order and deletes ledger rows. Migrations that
  are destructive by nature (0004 identity rebuild, 0005/0006/0007
  projections/backfills) are irreversible and throw, matching Python's
  `IrreversibleMigration` — restore from snapshots instead.

## Migration inventory

| Version | Name | What it does |
|---|---|---|
| 0000 | baseline | No-op marker so a fresh ledger records that the chain ran. |
| 0001 | file_list_v2 | `file` compound index `file_list_v2` (`p`, `ac`, `f`, `dn`, `t`, `sn`). |
| 0002 | thumbnail-status | `file` docs with `t: 2` and no `th` get `th: 0`, `the: ""`; `tht` cleared. |
| 0003 | file_admin_edit_time_v1 | `file` index on `et`. |
| 0004 | identity_members | Rebuilds legacy project/team roles + relations + join flows into `project_member` / `team_member` documents, repairs project owners (`ou`/`ouv`), upserts `identity_tag_policy`, denormalizes member counts (`m_uc`), drops legacy `w` maps, creates 7 indexes, writes per-collection reports + `identity-v1-summary` into `identity_migration_report`. |
| 0005 | identity_member_index_options | Upgrades identity/team indexes to `$type`-based partial filters, unsets null `external_id`. |
| 0006 | search_projections | NFC + strip + lowercase search projections (`ns`, `asrch`, `dns`) on terms/labels plus 7 indexes. |
| 0007 | project_member_tags | Missing/null `tags` on `project_member` become `[]`. |
| 0008 | file_manual_order_v1 | `file` index on manual ordering. |
| 0009 | storage_usage_backfill | Copies legacy `output.fn` to `file_name`; backfills missing/zero `project.fs` from `storage_used`. |

## Deliberate divergences from the Python runner

- **Checksum scheme**: Python hashes the migration module AST; C# hashes a
  canonical `version/name/description/level/spec` string (SHA-256 hex).
  Ledger rows are therefore namespaced by `runner: "csharp"`, and
  Python-written rows are accepted without checksum comparison.
- **No filesystem evidence bundle**: Python's 0004 additionally archives a
  JSON bundle to object storage; the C# port writes the same reports and
  summary to the `identity_migration_report` collection only.
- **Case folding**: Python 0006 uses `casefold()`; C# uses
  `ToLowerInvariant()` after NFC normalization and whitespace stripping —
  equivalent for the Latin/CJK inputs this platform stores.
- **`$rename` avoidance**: m0002/m0005/m0007 apply per-document
  unset/set updates instead of `$rename`, which is not atomic-safe across
  all supported server versions; the end state is identical.
- **m0004 index reconciliation**: like Python, the C# port drops and
  recreates the identity indexes when an incompatible definition exists,
  so startup preflight and migration converges instead of conflicting.
