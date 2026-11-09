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

Writer-side integration touches only:

- `src/Shoreline.Api/Features/ProjectService.cs`
- `src/Shoreline.Api/Features/ProjectImportService.cs`
- `src/Shoreline.Api/Features/TeamMemberService.cs`
- `src/Shoreline.Api/Features/InvitationService.cs`
- `src/Shoreline.Api/Features/ProjectInvitationService.cs`

`Program.cs`, the lock implementation, the delete service semantics, project
files and other modules are not changed by this slice.

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

## Writer guard wiring (K1 integration)

`EnsureWritableAsync` is now called by every write path listed below. The call
is always inside the operation's own lock range, immediately before its first
write; there is no unlocked "check, then write" path.

| Call site | Team id source | Lock range and rationale |
| --- | --- | --- |
| `ProjectService.CreateProjectSetAsync` | `team["_id"]` | Team-scoped write. No project/label lock exists yet; the distributed guard runs after the permission check and before the insert. |
| `ProjectService.CreateProjectAsync` | `team["_id"]` | Same as above. The project row does not exist when the guard runs, so there is no project label lock to hold. A create that races a delete cannot leave an orphan: the delete pass re-counts `project` and CAS-deletes the team row last. |
| `ProjectService.UpdateProjectAsync` | `project["t"]` | Runs inside a `ProjectLabelLock` session. The HTTP label guard usually already holds that lease for `/v1/projects/{id}`; the scoped `LabelRequestContext` borrows it. The project is re-read from the session before the status/permission checks and before the update. |
| `ProjectImportService.InitializeAfterCreateAsync` | `project["t"]` | Inside the project label lock, after the `label_revision` check and before the first `is/ip/it/ij` write or enqueue. |
| `ProjectImportService.RunImportAsync` | `project["t"]` | Inside the project label lock, after the terminal replay guards and before the first flag write (`FinishAsync` or `is=Running`). |
| `TeamMemberService.AddAsync` | team `_id` | Inside `TeamMutationGate`, after re-reading the team and before the capacity/member writes. |
| `TeamMemberService.JoinTrustedAsync` | team `_id` | Same gate and position. `JoinApplicationRoleAsync` delegates here, so registration and application approval are covered. |
| `InvitationService.JoinTeamAsync` | team `_id` | Every current caller (`PostInviteAsync`, and `PatchInvitationAsync` through `AcceptInvitationCoreAsync`) holds the invitation mutation gate for the group. The guard runs before the member/base-tag write; the nested `JoinTrustedAsync` re-enters the team gate. |
| `ProjectInvitationService.CreatePendingInvitationAsync` | `project["t"]` | All callers hold the per-project invitation mutation gate. Guard runs before the invitation insert. |
| `ProjectInvitationService.JoinProjectAsync` | `project["t"]` | Same gate and position; guard runs before the membership row/count writes. |

### `ValidateBeforeCreate` has no team id

`ProjectImportService.ValidateBeforeCreate` stays a pure parser and does not
call the guard. Python's `Project.create` also parses `labelplus_txt` before
the first `save()` and performs no dissolution check; parsing writes nothing.
The first real write is `ProjectService.CreateProjectAsync`, which now has the
team context and calls the guard, and `InitializeAfterCreateAsync` calls it
again under the project label lock. Changing the parser signature would either
need a fake team id at its other call site or an unlocked check, neither of
which is useful.

### Project invitation lock order

`ProjectInvitationService` deliberately does **not** acquire `ProjectLabelLock`
itself. HTTP project routes already hold that lock outside the invitation gate,
while invitation/application routes hold the invitation gate; taking the label
lock after the invitation gate would invert the established label -> invitation
gate order used by `ProjectMemberService`/`ApplicationService` and could
deadlock. The invitation gate is the operation lock for these two methods.

### Residual race (not a claim of full serialization)

`EnsureWritableAsync` is a check, not a shared lock. Project-scoped writers
(`UpdateProjectAsync`, both import hooks) share the deletion's project label
lock, so they cannot write after the delete pass has claimed that project.
Team-scoped writers (create project/set, member joins, team invitation join)
are serialized by their own mutation gate but deletion does not take that gate;
a writer that passes the check just before `AcquireAsync`/`MarkDeletingAsync`
can still commit. The delete's project re-count and final team-row CAS prevent
project/team resurrection, but a team-member or invitation row committed in
that narrow window is not covered by a project lock. This is the documented
boundary of the no-TTL owner-CAS design; the lock itself is unchanged.

## Verification evidence

Current wiring was verified in `/tmp/opencode/k1-wire-work`, built from
`git archive 70d38ca` plus only the five writer service files, the
`TeamDissolutionTests` additions and this document. Other agents' concurrent
working-tree edits were not copied into that tree.

```text
dotnet restore --locked-mode                                   -> ok
dotnet build Shoreline.sln -c Debug -warnaserror               -> 0 warnings / 0 errors
--filter FullyQualifiedName~TeamDissolutionTests               -> 25 passed / 0 failed
  (21 pre-existing + 4 new writer/delete concurrency tests)
--filter ProjectTests|TeamTests|InvitationTests|ApplicationTests|
         ProjectMemberTests|ProjectImportTests|ProjectCleanupTests
                                                               -> 100 passed / 0 failed
dotnet test (full)                                             -> 793 passed / 0 failed / 0 skipped
```

The new tests pause a real `DeleteAsync` in
`TeamLifecycleLockHooks.AfterMarkAsync` (after the durable marker is written and
while the claim is held) and run the writer concurrently:

- `ProjectCreationRacingDeletionIsRejectedThenRetrySeesDeletedTeam`
- `MemberJoinsRacingDeletionAreRejected` (both `AddAsync` and
  `JoinTrustedAsync`)
- `ImportInitializationRacingDeletionIsRejected`
- `DurableDeletingMarkerRejectsWritesAfterLeaseRelease` (marker retained after
  lease release still rejects writes; the next DELETE resumes and completes)

They use the shared disposable MongoDB fixture and a temporary local
`ObjectStorage` root. All deletes in the test are retryable and no lock row is
left behind.

Historical snapshot evidence from the original dissolution slice (base
`c70c67b`, Program wiring added only in the snapshot) remains: 21 focused and
344 full passed.

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
