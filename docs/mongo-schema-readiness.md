# Mongo schema readiness

`MongoStore.InitializeAsync` creates these non-unique query indexes on `team_member`:

| Name | Ordered keys | Options |
|---|---|---|
| `team_member_aliases_v1` | `team: 1, aliases: 1` | non-unique |
| `team_member_worker_qualifications_v1` | `team: 1, worker_qualifications: 1` | non-unique |

Initialization reuses existing indexes with these exact ordered keys and equivalent non-unique, non-sparse, non-partial options even when their names are MongoEngine-generated. It rejects same-name or same-key incompatible options (including collation, hidden, partial, or unknown options) without dropping or replacing anything. After a concurrent create conflict, it re-reads the catalog and accepts only an equivalent index. `background` is treated as a build hint, not a semantic option. Other indexes created earlier during initialization may already exist if a later conflict causes startup to fail.

The third index `team_member_alias_search_v1` is deliberately **not created or changed**: Python `app/migrations/versions/m0006_search_projections.py` defines `(team, status, asrch)`, whereas `app/models/team_member.py` defines `(team, status, aliases_search)` and maps the model field to BSON `asrch`. Python `app/models/_indexing.py` passes the definition's keys literally to PyMongo and drops a same-name conflicting index, so its runtime can replace the migration index with the other key, and m0006 can replace it back. Both states are possible and were synthesized in disposable tests; their presence in any deployment has not been verified. Choosing one without coordination risks `IndexKeySpecsConflict` or destructive index replacement. Resolve this upstream inconsistency before enabling the third index. C# does not drop either existing variant.

`MongoSchemaPreflight.InspectTeamMemberDuplicatesAsync` is a read-only diagnostic for duplicate `(team,user)` groups, which conflict with the existing `team_member_team_user_v1` unique index. It reports keys and counts and never deletes, merges, or modifies records. Resolve reported data only through an independently reviewed, backed-up procedure before initializing indexes.

## Migration boundary

The Python source is in the sibling backend repository. Its `app/migrations/runner.py` stores applied versions in `migration_record.v` and checks checksums (`c`) with special historical rules. This C# diagnostic does not yet inspect or validate those records or checksums and does not execute, emulate, or claim compatibility with the migrations. It covers only the duplicate team membership key; it is not wired into startup. Migration readiness remains blocked pending a verified migration-state gate and data checks, including m0004/m0005/m0007 identity changes, m0008 `file.mo`, and m0009 `project.fs`.

Python's `m0008` adds an index; it deliberately leaves old `file.mo` null or missing. Python's `m0009` backfills `project.fs` only when absent, null or zero and renames legacy `output.fn` to `file_name`. Neither is performed by this C# diagnostic.
