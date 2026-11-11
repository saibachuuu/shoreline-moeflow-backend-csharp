# Handoff 2026-11-10 night

## Current state

Branch `main`. The last verified full suite before the index-name change was
`4476911`: **896 passed / 0 failed** on MongoDB 4.4.1. The work after that
aligns C# index names and field cleanup with MongoEngine so a database written
by C# can be read by Python again.

A full run after the index change produced **868 passed / 27 failed / 895 total**
(`/tmp/opencode/full-rollback.log`). All 27 failures are fixture data: the new
unique `team.n` index (`n_1`) exposed tests that inserted teams without `n`.
The three affected fixtures have been fixed in this commit but the suite has
not been rebuilt/rerun yet.

## Do this first on the new machine

```bash
dotnet build Shoreline.sln --nologo
dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-build --nologo \
  --filter "FullyQualifiedName~TeamIdentityTests|FullyQualifiedName~IdentityAssignmentTests|FullyQualifiedName~RegistrationTests"
```

Expected: the 27 failures are gone. Then run the full suite with the five
isolated ports (MongoDB 4.4.1 is required):

```bash
SHORELINE_TEST_MONGO_PORT=27241 \
SHORELINE_TEST_S3_PORT=29241 \
SHORELINE_TEST_SMTP_PORT=21241 \
SHORELINE_TEST_MAILPIT_PORT=28241 \
SHORELINE_TEST_API_PORT=25241 \
dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-build --nologo
```

Test count should be 895 (one `InlineData` case was removed from
`MongoSchemaReadinessTests`).

## What changed in this commit series

- `e0a842d` align MongoEngine index names for Python rollback:
  - `team.n` → unique `n_1` (Python `Team.name` unique).
  - `team_member` → `team_1_status_1`, `team_1_aliases_1`,
    `team_1_worker_qualifications_1`; C# query index stays
    `team_member_alias_search_v1` on `{team,status,asrch}`.
  - `identity_operation` → `project_1_operation_id_1`.
  - file indexes → `t_1_sn_-1`, `dn_1_t_1_sn_-1`, etc.
  - `project_member_display_name_search_v1` now uses `{project,status,dns}`.
  - Ziteng indexes use `work_id_1`, `state_1`, `keyword_1`, `project_id_1`,
    `status_1`.
  - bootstrap no longer writes `team.m_u`.
  - `Program.cs` logs the full exception, not just its type.
  - `FileRegistration.InitializeFileIndexesAsync` creates a scope (fixes
    Development startup).
- `5240966` adds `tools/legacy-rollback-strip.js` and
  `docs/legacy-rollback.md`.
- `dc640ec` serializes `MongoStore.InitializeAsync` within one process; it
  removes the `dropIndexes` race seen with 8 concurrent initializers.
- This commit fixes test fixtures that omitted `team.n`.

## Rollback evidence

A 165-document dump from the live C# test machine was restored to a separate
database, stripped by `tools/legacy-rollback-strip.js`, and read by Python
1.2.7 **without starting C# first**. These returned 200:

- `/v1/user/info`
- `/v1/user/teams`
- `/v1/projects/<id>`
- `/v1/projects/<id>/files`
- `/v1/projects/<id>/targets/<id>/outputs`
- `/v1/teams/<id>/members`

## Remaining gates

- Full suite green after the index-name change (first priority).
- Rebuild release image and redeploy to the test machine
  (`root@103.195.188.139 -p 50600`), then run the E2E smoke again.
- Test rollback on the test machine itself (dump → strip → Python), not only
  from the copied dump.
- StoragePool restart/day-boundary scan, with `se=true` and worker enabled.
- MIT conditional module: C# used to enqueue `mit_*` jobs into its own
  `csharp_job` queue with no C# consumer, and Python delegates to an external
  `manga-image-translator` Celery worker. `c5b9218` (tests `eb13ab4`) now keeps
  `/v1/mit` unmapped unless all of `mit_detect_text`, `mit_ocr`, `mit_translate`
  have a registered `IJobHandler`; with the default empty handler set the module
  is a 404 and inserts nothing. Production has `MIT_STORAGE_ROOT` unset, so this
  is optional either way. Remaining gate unchanged: real MIT parity needs an
  in-process consumer or a supported external worker bridge; otherwise keep it
  disabled and do not advertise it.
- Write-path differential, authorization matrix, SSRF/limit/XFF, load/soak,
  and the one-page production cutover runbook.

## Environment notes

- Local test Mongo `sl-iso-mongo` is MongoDB 4.4.1 on `127.0.0.1:27241`;
  `sl-iso-s3` on 29241; `sl-iso-mailpit` on 21241/28241.
- Test machine stack: `/root/moeflow-csharp-isolated`, Mongo 4.4.1, Local
  storage, nginx at `127.0.0.1:5080`; storage bind mount must be
  `chown -R 1654:1654`. After recreating the backend container, restart the
  frontend container to clear the nginx upstream cache (otherwise 502).
- Test machine disk is tight; use `docker save | gzip | ssh | docker load`
  and prune the local builder cache.
