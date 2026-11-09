# Presence vertical slice notes

Source of truth (read-only during this port): `app/apis/presence.py:10-56` and
`app/models/presence.py:17-145`.

## Routes

- `POST /v1/projects/{projectId}/presence/heartbeat`
- `POST /v1/projects/{projectId}/presence/leave`
- `GET /v1/projects/{projectId}/presence`
- `GET /v1/teams/{teamId}/projects/active-presence`

The C# handlers are in `src/Shoreline.Api/Features/PresenceEndpoints.cs` and are
mapped by the shared `Program.cs` (`AddPresenceServices()`, `MapPresence()` and a
startup `PresenceService.InitializeAsync` scope).

## Storage contract

Collection: `project_presence`

This schema is a data-format contract for documents written by the legacy
Python backend. It does not mean both backends may write the same collection
concurrently.

| Field | BSON type | Notes |
| --- | --- | --- |
| `project` | ObjectId | Required project reference |
| `team` | ObjectId | Required team reference, copied from `project.t` |
| `user` | ObjectId | Required user reference |
| `action` | string | Legacy default `working`; at most 64 Unicode code points |
| `last_heartbeat` | BSON datetime | Refreshed on every heartbeat |
| `expire_at` | BSON datetime | `last_heartbeat + 45s` |

Indexes are created explicitly by `PresenceService.InitializeAsync`; reads and
writes never create indexes. Names match the Python/MongoEngine indexes:

- `project_1`
- `team_1`
- `user_1`
- `expire_at_1` with `expireAfterSeconds: 0` (TTL)
- `project_1_user_1` unique
- `team_1_expire_at_1`
- `project_1_expire_at_1`

Reads filter with `expire_at > utcNow` and do not depend on the TTL monitor.
Heartbeat is an upsert on `(project, user)` and refreshes `team`, `action`,
`last_heartbeat`, and `expire_at`. Leave deletes only the caller's
`(project, user)` row and is idempotent.

Each active user's avatar uses `User.a` with the existing signing helper. When
`a` is empty the service uses `Storage:DefaultUserAvatar`; when that setting is
absent it emits `""`, never JSON null, matching Python's `u.avatar or ""`.

## Body handling

The heartbeat body is deliberately not modeled as a normal request schema.
Python uses `request.get_json(silent=True) or {}`:

- absent, empty, malformed, or non-object JSON => `action = "working"`
- non-string `action` (including arrays, numbers, and `null`) => `"working"`
- string action is truncated to 64 Unicode code points, not UTF-16 units
- empty string => `"working"`; truthy whitespace is preserved

## Legacy authorization behavior — security review required

This port keeps the observed Python behavior on purpose: a valid token is
required, and the project or team must exist, but this module has **no
membership or ACCESS permission guard**. A valid user who knows a project ID is
therefore not blocked by this slice itself.

That is the real legacy contract, not an authorization guarantee. Do not
describe these routes as membership-safe. The next security review must decide
whether to add membership/ACCESS checks; any change there is a separate,
intentional behavior change.

## Tests

`tests/Shoreline.Tests/PresenceTests.cs` contains direct service integration
tests (no HTTP route dependency): legacy BSON/avatar serialization, empty and
configured default-avatar fallback, unique upsert/field refresh, 45-second query
filtering including the exact boundary,
project/team isolation, idempotent leave, active-member/DDN/name fallback,
dangling references, Unicode action truncation, team aggregate shape, and index
compatibility.

## Isolated validation evidence

Validation was run in a task-owned snapshot outside the repository:

`C:\Users\88888888\AppData\Local\Temp\opencode\presence-validation-20261109-211015-843a2a91`

The snapshot was extracted from baseline commit
`710bbcb1d000b77cf42565263fbb3f7b840f4816` with `git archive`, then the four
presence files and this note were copied in. The snapshot's `Program.cs` was
patched with `AddPresenceServices()`, a startup scope calling
`PresenceService.InitializeAsync`, and `app.MapPresence()`. The snapshot also
contains `tests/Shoreline.Tests/PresenceHttpTests.cs`.

Evidence from the snapshot:

- `dotnet restore Shoreline.sln`: exit 0
- `dotnet build Shoreline.sln --no-restore`: 0 warnings, 0 errors
- Presence-filtered run: 17/17 passed (11 service Facts + 6 HTTP Facts)
- Full suite: 190/190 passed (baseline 173 + 17 presence Facts)
- Logs: `validation-restore.log`, `validation-build.log`,
  `validation-presence.log`, `validation-full.log`

The HTTP tests cover missing token (1003), missing project/team original error
codes (4001/3001), malformed JSON/list/non-string action falling back to
`working`, 64-code-point Unicode truncation, leave/GET/team aggregate shape and
empty objects, and the explicit legacy valid-token-without-membership behavior.
That last test characterizes the old behavior; it is not a security guarantee.

## Shared wiring status

The shared `src/Shoreline.Api/Program.cs` now contains the same minimal wiring:

- `builder.Services.AddPresenceServices()` after `ProjectService`
- a startup scope calling `PresenceService.InitializeAsync` after the
  `MongoStore` initialization
- `app.MapPresence()` after `MapProjects`

The existing `ApplicationService`, `MapApplications`, and all other current
registrations and mappings are preserved. `tests/Shoreline.Tests/PresenceHttpTests.cs`
(218 lines, 6 HTTP Facts) has been copied into the shared test tree.

Shared-worktree build/test has intentionally **not** been rerun: Label, File,
Term, and other pending modules are still being completed.

## Joint C2 + Presence evidence

A second task-owned snapshot outside the repository combined baseline `710bbcb`,
the latest C2 production/test files, these Presence production/test files and the
shared `Program.cs` (no other pending modules):

`C:\Users\88888888\AppData\Local\Temp\opencode\c2-presence-snapshot-20261109`

It built with 0 warnings/0 errors and passed: applications 20/20, Presence 17/17,
invitations 23/23, full suite 210/210. This is isolated joint-snapshot evidence,
not a claim that every uncommitted parallel worktree module passes.
