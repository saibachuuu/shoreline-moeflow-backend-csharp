# Team insight compatibility (J1)

Legacy source of truth: `shoreline-moeflow-backend/app/apis/team.py:623-756`,
`app/services/team_member.py:471-520`, `app/models/team.py:410-481`,
`app/models/project_member.py:185-230`, and `app/models/output.py:179-204`.

## Routes

All routes require a bearer token. They do not hardcode the site admin flag;
authorization is the live `team:INSIGHT` permission from the active team
membership and tag policy.

| Method | Path | Pagination |
| --- | --- | --- |
| GET | `/v1/teams/{teamId}/insight/users` | `page`/`limit`, limit capped at 10 |
| GET | `/v1/teams/{teamId}/insight/users/{userId}/projects` | `page`/`limit`, limit capped at 100 |
| GET | `/v1/teams/{teamId}/insight/projects` | `page`/`limit`, limit capped at 10 |
| GET | `/v1/teams/{teamId}/insight/projects/{projectId}/users` | `page`/`limit`, limit capped at 100 |

Responses are the legacy JSON arrays, not `{items: ...}` wrappers. The four
`X-Pagination-Page`, `X-Pagination-Limit`, `X-Pagination-Count`, and
`X-Pagination-Page-Count` headers are emitted. `count` is the filtered total,
not the current page length.

## Behavior

- `users`: active, user-backed team members only. Dangling member rows and
  removed members are omitted. Search matches the member alias search index or
  the user's name/alias search index using the project's normalized search
  text. Sort is normalized name, then member id. Each row has
  `projects` (first five active memberships in working team projects),
  `count` (all matching memberships), and `user` (`UserView.to_api`, including
  the current viewer context).
- `users/{userId}/projects`: the subject must be an active team member,
  otherwise `UserNotExistError` (1005). Only active project memberships in the
  team's working projects are returned. External project members are not
  user projects. Rows are project mini views plus `member_summary` (the full
  project-member shape with `effective_permissions: []`).
- `projects`: team projects with status `WORKING` (0), optional literal
  `word` match on `name_search`, sorted by edit time desc then id desc. Each
  row has up to five `users` (full member shape plus `name`), the filtered
  `count`, and the project mini view.
- `projects/{projectId}/users`: active project members only, including
  external members. A member with no user falls back to `display_name`. A
  project from another team is `ProjectNotExistError` (4001).
- `outputs` is present only for callers whose live team snapshot has
  `team:AUTO_BECOME_PROJECT_ADMIN`; for everyone else the key is absent. Each
  output follows the legacy `Output.to_api` shape and is sorted by
  `create_time` descending. `status_details` contains the six legacy statuses.
- Project mini views contain only `group_type`, `id`, `name`, and the full
  `project_set` view. Full project payloads are not embedded in list rows.

## Data access

`TeamInsightService` batches users, project sets, project members, targets,
languages, avatars, and outputs. It does not call
`ProjectMemberService.MemberToApiAsync` per member, which would add a
permission snapshot per row. Project/team membership and `INSIGHT` are
resolved live for each request.

## Known compatibility gaps

- The shared `IdentityPermissions.TeamDefaults` currently omits
  `team:AUTO_BECOME_PROJECT_ADMIN` for the built-in admin/creator defaults,
  while Python includes it. J1 gates outputs on the live snapshot, so a team
  must grant the permission through tag policy until the shared defaults are
  updated. The output-gating code itself matches Python.
- Outputs whose project has a non-null `csid` belong to a storage-pool
  source. The storage-pool router is not part of J1, so the link is emitted as
  `null` rather than silently signing a default-bucket URL. Default-storage
  projects with status 4 and a filename still receive a signed link.
- Python `User.avatar` and member `avatar` use the signed user-avatar URL;
  J1 uses the same committed `AvatarService`/`ObjectStorage` path with a
  one-hour signature.
- Python uses `datetime.isoformat()` for several model timestamps. J1 uses the
  committed `BsonFields.Timestamp` format
  (`yyyy-MM-ddTHH:mm:ss.ffffff`, no trailing `Z`) wherever the existing C#
  models do, so this remains a timestamp-format compatibility gap rather than
  a silent behavior change.
- User sort uses `SearchText.Normalize` (NFC plus the project's case-fold
  table) as the closest local equivalent of Python `str.casefold()`.

## Verification snapshot

- Snapshot: `C:\Users\88888888\AppData\Local\Temp\opencode\j1-verify-0eddb2e`
  (`git archive` of `0eddb2e`, only J1 files plus snapshot-only Program
  wiring and tests).
- Baseline snapshot: `C:\Users\88888888\AppData\Local\Temp\opencode\j1-baseline-0eddb2e`
  (unmodified `0eddb2e`), **304 passed**.
- J1 focused run: **20 passed** (16 direct service facts + 4 HTTP facts).
- Full J1 snapshot run: **324 passed, 0 failed, 0 skipped**
  (304 baseline + 20 J1), 0 warnings / 0 errors.
- The HTTP facts are snapshot-only until shared `Program.cs` is wired by main.
  No shared Program, csproj, common service, or other agent file is modified.
