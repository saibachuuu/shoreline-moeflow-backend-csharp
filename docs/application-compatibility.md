# Project/team applications — C2 compatibility notes

Source of truth (read-only during this port): `app/apis/application.py`,
`app/models/application.py`, `app/models/user.py` (`invite`/`apply`/`join`),
`app/core/rbac.py` (application enums, `CHECK_USER`, `users_by_permission`) and
`app/apis/group.py` (public info).

The implementation lives in `src/Shoreline.Api/Features/ApplicationService.cs`,
`ApplicationEndpoints.cs`, with shared projection/join helpers in
`ProjectInvitationService.cs`, `InvitationService.cs` and `TeamMemberService.cs`.

## Routes

- `GET /v1/{project|team}/{groupId}/applications?status=...&page=...&limit=...`
- `POST /v1/{project|team}/{groupId}/applications` — `{"message": "..."}`
- `PATCH /v1/applications/{applicationId}` — `{"allow": true|false}`
- `DELETE /v1/applications/{applicationId}` — applicant only, pending only
- `GET /v1/user/related-applications?status=...&page=...&limit=...`
- `GET /v1/{project|team}/{groupId}/public-info`

The legacy URL router registered `PUT` for the application item, but the Python
API class defines no `put` method. C# therefore does not map `PUT`; it keeps the
framework 405 behavior instead of inventing semantics.

Group application lists require the live `CHECK_USER` permission (project
`CHECK_USER`, team `team:CHECK_USER`) and never grant a site-admin bypass.
`related-applications` filters against the stored `uc` snapshot and renders the
caller context; deleting or changing reviewer permissions later does not rewrite
that snapshot.

## Document shape

```text
application {
  _id: ObjectId,
  u:   ObjectId,             # applicant
  o:   ObjectId | absent,    # operator who approved/denied
  g:   GenericReference,     # Project or Team
  s:   1 pending | 2 approved | 3 denied,
  m:   string,
  uc:  ObjectId[]            # CHECK_USER snapshot captured at apply time
}
```

There is no stored role or create-time field. `create_time` is derived from
`_id.generation_time` and serialized as a whole-second UTC ISO string, for
example `2012-10-17T21:13:27+00:00` for
`507f1f77bcf86cd799439011`.

`user_role` in `to_api` always describes the **applicant**, not the viewer, and
only when the application is approved and the applicant still has an active
relation:

- project: active `project_member` tags → legacy role (`creator`, `admin`,
  `proofreader`, `translator`, `typesetter` → `picture_editor`; otherwise
  translator), then the persisted `project_role` document;
- team: active `team_member.base_tag` → the persisted `team_role` document;
- otherwise JSON `null`.

The `group` nested view keeps the viewer context (`group.to_api(user=viewer)`),
so the group application list uses a public view with `role = null` while
`PATCH` responses can show the operator's role.

## Apply flow

`User.apply` order is preserved:

1. capacity (`user_count >= max_user`) → `5012`;
2. existing relation → `5003`;
3. project applicant is an active team creator/admin
   (`AUTO_BECOME_PROJECT_ADMIN`) → join the project as `admin`, remove any pending
   application/invitation for that group (projection removal included), return
   `加入成功，管理员继承自团队` plus the group view;
4. existing pending application → `5005`;
5. existing pending invitation → accept it with its invitation role, return
   `管理员先前邀请了您，已直接加入` plus the group view;
6. `allow_apply_type`: `NONE(1)` → `103`; `ALL(2)` allowed; project
   `TEAM_USER(3)` requires an active team relation, else `103`;
7. create the pending application with the `CHECK_USER` snapshot;
8. `application_check_type == NO_NEED_CHECK(1)` → approve immediately with the
   applicant as operator, return `加入成功` plus the group view;
   otherwise return `申请成功，请等待管理员审核`.

The `uc` snapshot follows `Group.users_by_permission(CHECK_USER)`:

- roles whose `m_p` contains `101` contribute their legacy tag mapping
  (`creator`, `admin`, `coordinator` → `proofreader`, `proofreader`, `translator`,
  `picture_editor` → `typesetter`, `supporter` → `translator`; unknown codes stay
  as-is);
- project: only active `project_member` rows with an existing `user` object, whose
  persisted tags intersect the mapped tags (any tag, not the first one; the `m_o`
  compatibility mirror is not authoritative);
- team: only active `team_member` rows with an existing `user` object; `beginner`
  and `senior` normalize to `member`;
- external rows without `user` are skipped, and user ids whose user document no
  longer exists are filtered out before the snapshot is written.

Response extra fields are ignored, matching `Meta.unknown = EXCLUDE`. The message
is any string and is not subject to the invitation 140-code-point limit.

## Approval, denial, deletion

- Only pending applications can change (`5011` otherwise).
- Approval captures an existing project projection's tags/display name and checks
  the project position lock first (the row may only be restored by the team
  creator). It then joins the applicant with `role` or the group default role,
  sets `s=2`/`o=operator`, and writes the project projection with the approving
  operator.
- Denial sets `s=3`/`o=operator`; for projects it removes a pending projection and
  never downgrades an active member.
- Deletion checks the pending state before the applicant identity check, then
  removes a pending project projection and the application document.
- `PATCH` returns `{"message":"已接受"|"已拒绝","application":...}`; `DELETE`
  returns only `{"message":"删除成功"}`.
- Approval and denial are serialized per group by `InvitationMutationGate`, with a
  version-CAS member save. Non-pending races therefore produce `5011` rather than
  two terminal transitions.

## Invitation ↔ application interaction

- Legacy project/team invitations (`InvitationService.InviteAsync`) check a
  pending application after the pending-invitation check and before the project
  team-member direct join. A project pending application is approved with the
  invited role; a team pending application joins the team and is marked approved.
  The response is only `{"message":"此用户已有申请，已直接加入"}`.
- The tags/projection invitation entry
  (`ProjectInvitationService.InviteProjectAsync`) does the same pending-application
  direct allow. If the invitation later creates/reuses a projection, the requested
  tags and display name still apply to the active projection; no second invitation
  is created.
- `InvitationMutationGate` is shared by invitation endpoints, application
  endpoints and identity `AddUser`. The lock is acquired by the entry point and the
  group is re-read after locking; helper methods do not re-enter the gate. Team
  joins still take the team gate, preserving the invitation → team lock order.

## Public info

`GET /v1/{project|team}/{groupId}/public-info` requires a valid token:

- active relation → `joined = true`;
- no relation and `allow_apply_type == NONE` → `5014`;
- project with `allow_apply_type == TEAM_USER` and no active team relation →
  `5014`;
- otherwise `joined = false`.

The response contains only `id`, `name`, `joined`, `user_count` and
`application_check_type`; it is not a full group view.

## Errors

| Code | Meaning |
| --- | --- |
| 1003 | missing/invalid token |
| 103 | no `CHECK_USER` permission / not allowed to apply |
| 2 | schema validation (message/allow/status shape) |
| 5001 | unsupported group type |
| 5003 | applicant already joined |
| 5005 | pending application already exists |
| 5007 | application not found |
| 5011 | application already finished |
| 5012 | group/team capacity reached |
| 5014 | group not open to public applications |
| 4001/3001 | project/team not found |

## Boundaries

- `PUT` on the legacy application URL is intentionally 405, not a second update
  path.
- Only invitation/application entry points and identity `AddUser` share
  `InvitationMutationGate`; `UpdateAsync`, external add/restore and other identity
  lifecycle writes are not globally serialized. There is still no multi-document
  transaction, and the gate is local to a single writer process.
- Presence and other pending modules are separate slices; their tests are not
  evidence for application behavior.

## Isolated validation

The integrated C2 + Presence evidence was produced from a task-owned snapshot
outside the repository (`C:\Users\88888888\AppData\Local\Temp\opencode\c2-presence-snapshot-20261109`),
extracted from baseline `710bbcb`, overlaid with the C2 production/test files, the
Presence production/test files and the shared `Program.cs`. It does not include
other pending modules.

- build: 0 warnings, 0 errors
- applications: 20/20
- presence: 17/17
- invitations: 23/23
- full suite: 210/210

This is isolated joint-snapshot evidence, not a claim that every uncommitted
parallel worktree module builds or passes. The service is not production-ready,
the Docker image was not rebuilt, and no real R2 authentication was exercised.
