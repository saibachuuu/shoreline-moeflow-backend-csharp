# Legacy rollback: C# database to Python

The C# backend keeps the Python BSON field names, but it stores a small amount
of bookkeeping that the Python 1.2.x MongoEngine models do not know. MongoEngine
raises `FieldDoesNotExist` when it loads a document containing an unknown field,
so a rollback is only safe after those fields are removed.

Use `tools/legacy-rollback-strip.js` while the C# backend is stopped.

## Procedure

1. Stop the C# backend and any C# worker (the worker may still write lease
   fields after the API stops).
2. Take a backup of the current database.
3. Run the strip script against the database:

   ```bash
   # Inside a MongoDB 4.4 container, or with a local mongosh/mongo client.
   mongo "mongodb://user:pass@host:27017/moeflow?authSource=admin" \
     tools/legacy-rollback-strip.js
   ```

   The script is idempotent. It reports matched/modified counts for every
   collection and drops the index names that conflict with MongoEngine's
   generated names.
4. Start the Python backend. MongoEngine recreates its own indexes on the
   first access to each collection.
5. Smoke-test `/v1/user/info`, `/v1/user/teams`, a project detail, project
   files, outputs and team members.

## Fields removed

| Collection | Fields | Why |
| --- | --- | --- |
| `project` | `label_revision`, `label_recovery` | C# label concurrency guard. |
| `file`, `source`, `target`, `translation`, `file_target_cache` | `label_epoch` | C# label revision snapshot. |
| `output` | `job_id`, `job_owner`, `job_lease_until`, `job_run_token`, `job_started_at`, `job_completed_at`, `job_error` | C# output export lease; Python `Output` has no job state. |
| `team` | `m_u`, `lc_phase`, `lc_owner`, `lc_started`, `lc_updated`, `lc_error` | C# bootstrap legacy field and team-deletion journal. |
| `ziteng_check` | `generation`, `lease_owner`, `lease_until` | C# worker lease for the optional Ziteng module. |

## Consequences

- Any output export that was running under C# is abandoned. The Python
  application can re-create the output.
- Label revisions are reset. The Python application does not use them.
- Team deletions that were interrupted remain ordinary teams; the C# deletion
  journal is discarded.
- The `csharp_*` collections (`csharp_job`, `csharp_seed`, `csharp_captcha`,
  `csharp_lifecycle_lock`, ...) are ignored by Python and can be dropped after
  the rollback. They are not removed by the script so that a rollback can be
  reversed without losing C# job history.

## Evidence

On 2026-11-10 a dump of the live C# test database (165 documents) was restored
into a separate database, stripped with this script, and read by the Python
1.2.7 reference without starting C# first. The following endpoints returned
`200`: `/v1/user/info`, `/v1/user/teams`, `/v1/projects/<id>`,
`/v1/projects/<id>/files`, `/v1/projects/<id>/targets/<id>/outputs` and
`/v1/teams/<id>/members`.

The matching index names were also corrected in the C# initializer so that a
fresh C# database no longer needs the index-dropping part of the rollback:
`n_1`, `team_1_status_1`, `team_1_aliases_1`,
`team_1_worker_qualifications_1`, `project_1_operation_id_1`,
`t_1_sn_-1`, `dn_1_t_1_sn_-1`, `f_1_t_1`,
`project_member_display_name_search_v1` (`{project,status,dns}`) and the
Ziteng default names.
