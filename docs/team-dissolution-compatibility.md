# Team dissolution compatibility (K1)

## Scope

This slice ports only the legacy core route:

| Method | Path | Handler |
| --- | --- | --- |
| DELETE | `/v1/teams/{teamId}` | `TeamDissolutionService.DeleteAsync` |

It is intentionally separate from `TeamEndpoints` because the existing C#
team routes have no DELETE. `TeamDissolutionRegistration.MapTeamDissolution`
owns the route and must not be combined with another DELETE mapping.

The Python behavior is taken from `app/apis/team.py:304-332`,
`app/models/team.py:329-333`, `app/models/project.py:1028-1033`, and the
delete rules in `app/models/team.py:621-634` / `app/models/project.py:1438-1451`.

## Files

- `src/Shoreline.Api/Infrastructure/TeamLifecycleLock.cs`
- `src/Shoreline.Api/Features/TeamDissolutionService.cs`
- `src/Shoreline.Api/Features/TeamDissolutionEndpoints.cs`
- `src/Shoreline.Api/Features/TeamDissolutionRegistration.cs`
- `tests/Shoreline.Tests/TeamDissolutionTests.cs`
- this document

No shared source, `Program.cs`, project file, or another agent's file is
changed by this slice.

## Legacy semantics

1. Require a token and fetch the team (missing or malformed id is 3001).
2. Require `team:DELETE`. The default team roles are unchanged: creator has it,
   team admin does not, ordinary member only has ACCESS. Site admin does not
   bypass team membership.
3. If any project has `st == 0` (or is missing `st`, which is not a completed
   project in the legacy state model), reject with:

   `NoPermissionError` code `103`
   `此团队含有未完结的项目，不能解散（请先完结或者转移项目）`

   Nothing is deleted before this check.
4. Clear every project in the team: physical file/output objects first, then
   content rows, targets, project roles/members/relations, and finally the
   project row. This is **not** lifecycle clear (`ProjectLifecycleService`);
   legacy `Project.clear()` physically deletes the project.
5. Delete team-owned rows, then the team row. Return `{"message":"解散成功"}`.

## Durable claim and resume

`TeamLifecycleLock` uses collection `team_lifecycle_lock`:

```text
{ _id: teamId, owner: uuid, created: UTC DateTime }
```

The unique `_id` is the mutual exclusion primitive. There is no TTL. Release is
always owner-scoped (`{_id, owner}`) with an independent 5-second
`CancellationTokenSource`; an uncertain insert acknowledgement also attempts
an owner-scoped release before rethrowing.

The team document carries a resumable marker:

```text
lc_phase = "deleting"
lc_owner = lease owner
lc_started = first claim time
lc_updated = last update
lc_error = last failure type (best effort)
```

A failure after the marker is written leaves the marker and releases the lock.
A later request may acquire the free lock and resume. The marker is not a fake
global transaction; individual deletions are not atomic across collections.

Public helper contract:

```csharp
Task<TeamLifecycleLease> AcquireAsync(ObjectId teamId, CancellationToken ct = default);
Task MarkDeletingAsync(TeamLifecycleLease lease, CancellationToken ct = default);
Task<BsonDocument> ReadOwnedTeamAsync(TeamLifecycleLease lease, CancellationToken ct = default);
Task EnsureWritableAsync(ObjectId teamId, CancellationToken ct = default);
Task<bool> IsDeletingAsync(ObjectId teamId, CancellationToken ct = default);
Task MarkFailureAsync(TeamLifecycleLease lease, string errorType);
```

`TeamLifecycleLease` also validates that no other owner replaced the claim and
releases itself on dispose.

## Cascade map

Project children removed after strict content cleanup:

- `application` / `invitation` whose generic `g` references the project;
- `project_user_relation.g`;
- `project_member.project`;
- `project_role.g`;
- residual `file.p`, `output.p`, `target.p` (target content is processed with
  `ProjectContentCleanup.DeleteTargetCascadesAsync` first);
- project row last.

Team children removed after all projects:

- `term_bank.t`, including bounded term batches (200), `source.pt` pull, and
  the global `project.tb` pull (so a foreign project cannot retain a dangling
  bank reference);
- `application` / `invitation` whose generic `g` references the team;
- `project_set.t`;
- `team_user_relation.g`;
- `team_member.team`;
- `team_role.g` only (so a system role without `g` survives);
- team row last.

The project row is intentionally deleted before the team row because the
legacy `Project.team` delete rule is `DENY`, not `CASCADE`.

Preserved deliberately:

- `audit` and notification history (there was no legacy FK cascade);
- `proofread_*` objects used by long-lived email links;
- the team avatar object (legacy `Team.clear()` does not mention avatar
  deletion), and default avatars;
- users, other teams/projects, and sources belonging to other projects;
- ungrouped system roles.

## Errors and retry behavior

| Code | Meaning |
| --- | --- |
| `3001` | Team not found / invalid team id |
| `103` | No `team:DELETE`, or a working project exists |
| `5120` | Another owner holds the team claim, or the claim was replaced |
| `5113` | Content cleanup did not complete; team/project rows remain retryable |

Storage cleanup is strict. A missing/invalid local key, a non-empty project
`csid` (storage-pool routing is not implemented yet), or any object deletion
failure aborts before the project row is removed. The service reports 5113 and
keeps the team plus the marker so a later request can retry. A `csid` project
must not be deleted against the default bucket; that is a deliberate fail-closed
D2 integration point.

Each project is processed under the existing `ProjectLabelLock` session. The
project is re-read after the label lock is acquired, and the service checks
that its status is still completed and that `t` still points at the claimed
team. Project label leases are released between projects; the team claim is
held for the whole operation. Lock order is team lifecycle claim, then project
label lock, then any operation-local lock.

## Required wiring outside this slice

`EnsureWritableAsync` is the only guard and **is not retroactively called by
existing writers**. The following methods still need to call it (or acquire
the same guard) before accepting a new write:

- `ProjectService.CreateProjectAsync`
- `ProjectService.CreateProjectSetAsync`
- `ProjectService.UpdateProjectAsync` (including project-set moves)
- `ProjectImportService.RunImportAsync`
- `TeamMemberService.AddAsync` / `JoinTrustedAsync` / `JoinApplicationRoleAsync`
- `InvitationService.JoinTeamAsync` and invitation acceptance paths
- `ProjectInvitationService.CreatePendingInvitationAsync` / `JoinProjectAsync`

Until those calls land, a concurrent writer can still create a row after the
first working-project check. The deletes themselves re-read the team marker and
reject a stale owner, but this does not make the writer path race-free. The
HTTP and direct tests therefore prove the guard itself and concurrent DELETE
behavior, not integration with unwired writers. This is a known pending item,
not a claim that team dissolution is fully race-free.

## Verification evidence

Snapshot source: `c70c67b` (base 323 tests), plus only this slice's files and
the stable E1 `ProjectLabelLock` dependency, with a snapshot-only Program
wiring:

```text
AddTeamDissolutionServices()
MapTeamDissolution()
InitializeTeamDissolutionAsync()
```

Snapshot: `C:\Users\88888888\AppData\Local\Temp\shoreline-k1-snapshot-20261109-231500`

Commands (UTF-8; 420000 ms timeout):

```powershell
dotnet build Shoreline.sln -v:minimal
dotnet test tests\Shoreline.Tests\Shoreline.Tests.csproj --no-build --filter "FullyQualifiedName~TeamDissolutionTests"
dotnet test tests\Shoreline.Tests\Shoreline.Tests.csproj --no-build
```

Actual results in that snapshot:

- build: 0 warnings, 0 errors
- focused: **21 passed** (17 direct + 4 HTTP), 0 failed, 0 skipped
- full: **344 passed**, 0 failed, 0 skipped (323 baseline + 21)

The direct tests use a temporary local `ObjectStorage` root and the shared
disposable MongoDB test fixture. HTTP tests use the snapshot-only Program
wiring; shared `Program.cs` remains unwired in this slice.

## Remaining boundaries

- No distributed/global transaction. A process crash between object deletion
  and row deletion is retryable via the marker, not atomic.
- `ProjectService.UpdateProjectAsync` does not yet integrate project
  `term_banks` selection; that remains a separate slice.
- `find_terms` background refresh is not implemented.
- Storage-pool (`csid`) projects fail closed with 5113 until the D2 router is
  integrated.
- Only local object storage was exercised in tests; S3/R2 live behavior and
  CDN/S3 signing are not verified here.
- Team avatar objects are retained by design and are not deleted by this
  service.
- The four HTTP tests require the snapshot Program wiring; they are not
  evidence that shared `Program.cs` is wired.

This document does not claim that the C# backend has replaced the Python
application. The remaining writer-guard wiring, D2 storage router integration,
and authoritative comparison must be completed by the main integration work.
