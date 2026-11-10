# Mongo schema readiness

`MongoStore.InitializeAsync` creates these non-unique query indexes on `team_member`:

| Name | Ordered keys | Options |
|---|---|---|
| `team_member_aliases_v1` | `team: 1, aliases: 1` | non-unique |
| `team_member_worker_qualifications_v1` | `team: 1, worker_qualifications: 1` | non-unique |
| `team_member_alias_search_v1` | `team: 1, status: 1, aliases_search: 1` | non-unique |

`MongoSchemaPreflight.InspectTeamMemberDuplicatesAsync` is a read-only diagnostic for duplicate `(team,user)` groups, which conflict with the existing `team_member_team_user_v1` unique index. It reports keys and counts and never deletes, merges, or modifies records. Resolve reported data only through an independently reviewed, backed-up procedure before initializing indexes.

## Migration boundary

This repository/worktree does not contain the Python `app/migrations/versions/m0000..m0009` source or a verified migration-state schema. Consequently, this change does not inspect or validate the legacy migration version and does not execute, emulate, or claim compatibility with those migrations. The schema preflight currently covers only the duplicate team membership key. Migration readiness remains blocked until the Python migration definitions and their state tracking contract are available and individually verified, including m0004/m0005/m0007 identity changes, m0008 `file.mo`, and m0009 `project.fs`.

No database-backed preflight or index-creation run is claimed by this change.
