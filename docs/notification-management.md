# Notification management reads (N3)

Status: implementation and tests are present, but this slice is **not wired into
the shared `Program.cs`** and has **not been committed**. N3 supplies two
integration calls:

```csharp
services.AddNotificationManagementServices();
app.MapNotificationManagementEndpoints();
```

`AddNotificationManagementServices` only registers the scoped N3 service. It
depends on the N1 read service (`NotificationReadService`) and the N2 publish
service (`NotificationPublishService`); the host must already have registered
N1, N2 and a stable `INotificationMailTransport`. N3 does not copy authorization
or publish logic from those services.

## Routes

| Method | Path | Authorization |
| --- | --- | --- |
| GET | `/v1/notifications/sent` | ordinary publisher or proofread viewer |
| GET | `/v1/notifications/{notificationId}` | ordinary management permission |
| GET | `/v1/admin/notifications` | site administrator |
| GET | `/v1/admin/notifications/{notificationId}` | site administrator |
| GET | `/v1/me/notification-sync` | authenticated user, view-dependent |

The N1 inbox, preference, policy, mark-read and cards routes and the N2
publish/edit/revoke routes are not duplicated here.

## Ordinary list semantics

`GET /v1/notifications/sent` mirrors Python
`management_page` (`app/apis/notification.py:398-496`):

1. Notifications must be enabled.
2. The caller may publish in the requested category/scope, or must have
   `PROOFREAD_TRA` for a project scope. Otherwise the request is rejected with
   `403`.
3. The requested category is always applied. A team or project scope also
   applies the exact `team_id`/`project_id`; a system/personal request applies
   the caller as `actor_id`.
4. A non-scope-owner only sees rows where `actor_id` is the caller.
5. A caller without send permission only sees `proofread_feedback` rows.

Site administrators using the ordinary route remain ordinary publishers: they
see only their own system notifications. The admin route is the only global
listing and requires `admin: true`.

## Admin list semantics

`GET /v1/admin/notifications` requires a site administrator, uses the
`admin_search` throttle (120/minute) and writes an `admin_search` audit event.
It does not implicitly scope rows to the administrator. `GET
/v1/admin/notifications/{id}` writes an `admin_read` audit event. Neither admin
route grants access through a notification receipt: receipt visibility belongs
to the inbox routes.

## Search and filters

Search is implemented in `NotificationManagementService.SearchFilter`:

- `q` is trimmed. More than 100 Unicode code points is `400` (`搜索词过长`).
- A 24-character hex value is matched as `_id`.
- Otherwise the NFKC/case-folded value must be at least two Unicode code points
  (`搜索词至少两个字符`); up to 30 search bigrams must all be present and the
  normalized value is matched as a literal (escaped) substring.
- An unknown category is rejected (`通知分类无效`).
- Every query has a two-second MongoDB max time.

Supported filters:

- `delivery_state`: one of `pending`, `leased`, `accepted`, `retry_wait`,
  `skipped`, `failed`, `unknown`, `cancelled`.
- `expired=true|false` compares `expires_at` with the current UTC time.
- `revoked=true|false` compares `revoked_at` with `null`.
- `team_id`, `project_id`, `actor_id` are parsed as ObjectIds.
- `state`, `source`, `event_type` compare the first 100 code points.
- `from`/`to` require an ISO-8601 value with an explicit timezone suffix.
- `cursor` is an ObjectId; ordering is `_id` descending.
- `limit` is 1–100 (default 20).

Delivery-state filtering first aggregates distinct `notification_id` values and
caps the result at 20,000. Exceeding the cap returns `422` (`邮件状态检索超过安全上限`).
The cap is exposed as `DeliveryStateLimit` for bounded tests; production keeps
20,000.

## Projection

Management list rows use `DetailManyAsync` with `full=false`. The Mongo query
also excludes `nodes`, `body`, `search_tokens`, `payload.html` and
`payload.text`. Management list rows may contain `body: null`, but never the
stored body, nodes, or rendered HTML/text payload. Detail routes use `full=true`.

## Sync

`GET /v1/me/notification-sync` accepts:

- `view`: `""`, `inbox`, `scope`, `admin`, `inbox_detail`, `scope_detail`,
  `admin_detail`, `compose`.
- `category` and `scope_id` for scoped views.
- `notification_id` for detail views.
- `revision` for the caller's last revision.
- The ordinary page/filter parameters for list views.

The response always starts with `enabled`, `capabilities` and `counts`.
`enabled=false` does not query a page or a detail. When enabled, a list view adds
`page`, and a detail view adds `notice`. `inbox_detail` uses the N1 receipt
visibility rule; the other detail views use N3 ordinary/admin authorization.
A `NotificationError` while resolving the requested view becomes
`view_error: {status, message}` without echoing a previously cached private
notification. Other exceptions propagate.

`revision` is the lowercase SHA-256 of Python-compatible canonical JSON:

```python
json.dumps(result, sort_keys=True, ensure_ascii=False)
```

This means sorted keys, literal Unicode, `", "`/`": "` separators, and lowercase
`\u00xx` control escapes. Key ordering follows Unicode code points rather than
UTF-16 code units, so an astral character such as U+1F600 sorts after BMP
U+E000. Float and double values use Python's `repr` shape (including `0.0`,
`-0.0`, `1e-05` and `1e+20`); BSON DateTime fractions are emitted from ticks, so
`.123000` is retained. If the caller's `revision` matches, the response is only
`{"unchanged": true, "revision": "..."}`. All sync responses, including disabled
and unchanged responses, must carry `Cache-Control: no-store`.

Five vectors were generated independently with Python's standard `json` and
`hashlib` modules and are pinned in `NotificationManagementTests`. The last
three specifically cover floats, codepoint key ordering, control characters and
nested numbers (U+E000/emoji are shown escaped here; the canonical text contains
the actual code points):

```text
{"counts": {"system": 1, "total": 2}, "enabled": true, "note": null, "title": "汉字😀 <b>猫</b>"}
c5c889bd7912ca52cdfb60fa86e38e4320856e634496bc980191fa82f7a827a7

{"a": [true, false, null, "line\nbreak\t\"quote\"", "emoji😀汉字"], "z": {"html": "<script>&", "nested": 0}}
9ba076ac582c921054e24a9f7a0ab09b542c35e097f25f46ca3c3e8923fb1baf

{"a": 0.0, "b": -0.0, "c": 1e-05, "d": 1e+20}
f7d24e8618173b43a11d33a3a8df510e03bd360f673a0970a978200c8efe655c

{"a": "汉字", "b": true, "n": null, "\uE000": "bmp", "\U0001F600": "emoji"}
1a0369a6c30b068463edc296e586f3d6f8f28b759aea5988860cc66859ddfe66

{"a": [true, false, null, "line\nbreak\t\"quote\"\u0001", "emoji😀汉字"], "f": 1.2345678901234568e+16, "z": {"html": "<script>&", "nested": 1.5}}
f294629e5b31b8e8cff1d86c476821e204584369295656578fffee6974691c96
```

## Tests

`tests/Shoreline.Tests/NotificationManagementTests.cs` contains 20 direct and
minimal-host Facts, and `tests/Shoreline.Tests/NotificationManagementHttpTests.cs`
contains 4 real `WebApplicationFactory<Program>` Facts:

- ordinary site-admin, team creator/admin, project owner, proofread-only and
  removed-member authorization;
- team/project scope isolation;
- literal search, invalid search/cursor/category/delivery state;
- pagination, delivery-state filtering, expiry/revocation/date filters and the
  422 delivery cap;
- list projection excluding bodies/nodes/payloads;
- ordinary detail ownership and audited admin detail;
- admin list forbidden for normal users and `admin_search` audit;
- disabled sync, permission-loss `view_error`, scoped sync and unchanged
  revision;
- canonical JSON vectors, including Python floats (`0.0`, `-0.0`, `1e-05`,
  `1e+20`), Unicode codepoint key ordering, control characters and DateTime
  fractions;
- three minimal-host HTTP Facts: no-store disabled sync, 401/403 behavior, and
  a `NotificationError` view validation response;
- four real `WebApplicationFactory<Program>` Facts: missing-token sync 401 and
  no-store; audited admin detail plus list body/nodes projection; forwarded
  scope/category sync with stable revision and no-store; unknown-view and
  disabled-host no-store behavior.

The direct and minimal-host tests do not require SMTP, a real browser, or an
external storage service. The real-host file exercises the actual ASP.NET
pipeline (routing, authentication, `CurrentUser` and error responses); it is
validated against an isolated snapshot whose private `Program.cs` copy wires N3.
The shared `Program.cs` remains untouched.

## Verification status

The main worktree was not built because other parallel slices are still in
progress. An isolated snapshot at
`%TEMP%\shoreline-n3-isolated-d76f587-http` was created from the committed `HEAD`
`d76f587` (N1 plus the committed N2 publish slice) with only the four N3
production files, the two N3 test files and this document added. The snapshot's
private `Program.cs` copy calls `AddNotificationManagementServices()` and
`MapNotificationManagementEndpoints()` solely for the real-host tests; the
shared `Program.cs` is not changed.

`dotnet restore Shoreline.sln --locked-mode` succeeded, and a normal
`dotnet build Shoreline.sln --nologo` completed with **0 warnings and 0 errors**
without disabling NuGet audit. The focused N3 run passed **24/24** (20 in
`NotificationManagementTests` plus 4 in `NotificationManagementHttpTests`), and
the full snapshot run passed **378/378**. The snapshot and main worktree copies
of the four N3 production files, both test files and this document have matching
SHA-256 values.

The host wiring is intentionally left to the integration owner. No commit, push,
server access, SMTP configuration or shared `Program.cs` change was made.
