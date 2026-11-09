# Notification compatibility (N1 read surface)

This document covers the C# replacement of the Python notification read,
preference, policy, and project-card APIs. It intentionally does **not** claim
publish, dispatch, delivery, moderation, or notification-sync readiness.

## Scope

Implemented routes (legacy `/v1` paths):

| Method | Path |
| --- | --- |
| GET | `/v1/me/notification-capabilities` |
| GET | `/v1/me/notifications` |
| GET | `/v1/me/notifications/unread-counts` |
| POST | `/v1/me/notifications/mark-read` |
| GET, PATCH | `/v1/me/notifications/{notificationId}` |
| GET, PUT | `/v1/me/notification-preferences` |
| GET, PUT | `/v1/teams/{teamId}/notification-policy` |
| GET | `/v1/notification-project-cards` |
| POST | `/v1/notification-project-cards/resolve` |

`/v1/me/notification-sync`, publish, preview, candidate, manage, and revoke
routes are deliberately **not registered**. Their N1 read dependencies are
public helpers on `NotificationReadService`/`NotificationContent`, so N2 can add
the full surfaces without duplicating visibility rules.

## Configuration

- Enablement is read from `Notifications:Enabled`, falling back to
  `ENABLE_NOTIFICATIONS`, and defaults to `false` when neither is present.
- The disabled behavior matches the legacy configuration: inbox/detail/mark-read,
  card, and policy-write endpoints return `503 NotificationError`
  ("通知系统尚未启用"); preference GET/PUT and policy GET stay available because
  they did not call `enabled()` in Python. Capabilities expose
  `enabled=false`, `can_send=false`, `can_view_sent=false` and unread counts are
  all zero. `can_manage` still reflects site administrator status, as in Python.
- Project-card links must match `Notifications:SiteOrigin` (fallback
  `Site:Origin`).

## Error contract

All notification-domain failures use error name `NotificationError` and code
`4600`. Status follows the legacy response: `400` validation, `403` permission,
`404` invisible/missing object, `409` version conflict, `429` throttle, `503`
disabled. No generic `103` error is used for these paths.

## Collections and indexes

Collections read for legacy compatibility:

`notification`, `notification_receipt`, `notification_preference`,
`notification_policy`, `notification_audit`, `notification_verified_email`,
`notification_throttle` (plus `notification_audience_chunk` indexes for N2).

`NotificationRegistration.InitializeAsync` creates the legacy index names:
notification unique `key`, state/publish time, category/team/project/actor by
created time, `search_tokens`, moderation fields; receipt unique
`(notification_id,user_id)` and `(user_id,_id)`; preference unique `user_id`;
policy unique `team_id`; audit unique `key` plus notification/actor timelines;
throttle unique `key` and a TTL on `expires_at`; delivery and audience-chunk
uniques; verified-email unique `digest`.

No foreign keys or cascading deletes are used. Historical notes and receipts
must remain readable even after users, teams, or projects disappear.

## Security invariants

- A receipt is always scoped to the authenticated owner. There is no
  cross-user receipt lookup.
- Visibility is re-resolved on every request, never cached in a receipt:
  banned users are hidden; `draft`, `scheduled`, `preparing`, `cancelled`,
  revoked, and expired notes are not visible, except that the actor may still
  see their own `proofread_feedback` draft/preparing example.
- Team notes require a live active team member; project notes require a live
  active project member. `system`/`personal` notes additionally honor manual
  recipient lists, site roles, and team/project intersection filters.
- `proofread_feedback` with `cc_myself` requires a live
  `project:PROOFREAD_TRA` permission.
- Site administrators do not bypass receipt visibility. `can_publish` is
  re-evaluated from live project/team state, including finished projects and
  team policy flags; a team creator who is not an active project member is not
  a project publisher.
- Project cards are resolved through current project ACCESS on every call.
  Inaccessible or deleted projects become `{type:"unavailable"}` with no name,
  id, or derivable payload. Stored `payload.html`, `payload.text`, and storage
  destinations are never returned.
- Inbox filtering uses cursor pagination over receipt `_id` batches of 100,
  normalizes with the shared Unicode NFKC/case-fold helper, applies
  `search_tokens` plus the normalized `search_text` substring, and caps search
  work with a 2 second server max time. Pagination never skips visible rows.
- Mark-read uses a cutoff and `read_at` CAS so messages arriving after the
  cutoff stay unread. Archiving implies read; clearing read state follows the
  legacy semantics.
- Preferences and team policy writes are version-CAS. Duplicate-key races are
  mapped to `409` and never overwrite a concurrent writer.

## Public helpers for N2

`NotificationContent`:

- `ParseBody(string?) -> BsonArray`
- `Plain(BsonArray) -> string`
- `Walk(BsonArray) -> IEnumerable<BsonDocument>`
- `Normalize(string) -> string`, `SearchTokens(string) -> string[]`
- `SafeUrl(string?) -> bool`
- `PrepareAsync(string?, BsonDocument actor, CancellationToken) -> Task<BsonArray>`
- `VisibleNodesAsync(BsonArray, BsonDocument? user, CancellationToken) -> Task<BsonArray>`
- `RenderHtml(BsonArray) -> string` (escaped, for future mail rendering)

`NotificationReadService` exposes the read/policy helpers used by both the
handlers and future N2 dispatch: `Enabled`, `ScopeAsync`, `CanPublishAsync`,
`CanViewProofreadAsync`, `IsLive`, `ReceiptVisibleAsync`, `GetNoteAsync`,
`GetReceiptAsync`, `DetailAsync`, `DetailManyAsync`, `InboxAsync`,
`UnreadCountsAsync`, `MarkReadAsync`, `UpdateReceiptAsync`,
`PreferencesAsync`, `SavePreferencesAsync`, `PolicyAsync`, `SavePolicyAsync`,
`CapabilitiesAsync`, `ThrottleAsync`, `AuditAsync`, `CardSearchAsync`, and
`CardResolveAsync`. `NotificationVisibilityContext` batches scope/membership
lookups so inbox pages do not issue per-row queries.

## Wiring

The shared `Program.cs` now wires only the N1 read surface:

1. `builder.Services.AddNotificationServices()`.
2. `NotificationRegistration.InitializeAsync(mongoStore, ct)` immediately after
   `MongoStore.InitializeAsync`.
3. `app.MapNotificationEndpoints()` alongside the existing feature maps.

Existing Term/Presence/C2 and other feature registrations are unchanged. No
publish worker or N2 registration is wired. Set `Notifications:Enabled=true`
only after the N2 publish/dispatch surface is complete; until then legacy
clients should observe the disabled behavior.

## Test coverage

`tests/Shoreline.Tests/NotificationTests.cs` uses the real local Mongo fixture
and direct services (no HTTP routes). It covers parser/tag bounds, unsafe URLs,
HTML escaping, Unicode search/case-fold goldens, project-card permission
revocation without leaks, receipt ownership and live visibility for
banned/expired/revoked/removed members, foreign-actor proofread draft denial,
filtered cursor pagination, unread counts/mark-read cutoff/archive semantics,
preference and policy CAS conflicts, concurrent throttle limits, unique/TTL
indexes, and DI resolution without a circular dependency.

## Isolated verification evidence

Verification used the approved temporary snapshot `n1-verify-0dfa13b`, built
from stable commit `0dfa13b` (`git archive`, parent `421208d`) plus only the
eight N1 files, the two N1 test files, shared `Program.cs`, and the test csproj
fixture link. No N2 source or `MailKit` dependency was copied.

- Baseline at `0dfa13b` before N1: **254 passed, 0 failed, 0 skipped**.
- Focused N1 filter (`FullyQualifiedName~Notification`): **29 passed**.
- Final isolated full suite: **283 passed, 0 failed, 0 skipped**.
- Composition: 254 baseline + 21 direct tests + 5 golden tests + 3 HTTP tests.
- Build: 0 warnings, 0 errors.
- The test csproj links `tools/legacy-reference/fixtures/goldens.json` into the
  output as `tools/legacy-reference/fixtures/goldens.json`, so a fresh clone
  runs without manually copying fixture files.
- Golden fixtures come from Python `notification_content` output; parse errors,
  nodes, URLs, tokens, and Unicode normalization are compared directly rather
  than hand-written.

SHA-256 of the seven source/test N1 files (all matched the isolated copies):

```
2A8FFAC0A12D10294D97A6906426F8F1CC258DB0A3E93098DBA62AD8A16EC5E9  src/Shoreline.Api/Features/NotificationContent.cs
4AC10760E1A89F66F7A890F1C59F0F2DBF6C3A2308F4FB6D13ECE21949264882  src/Shoreline.Api/Features/NotificationReadService.cs
EC818770B331851D922FCA8B54C7B56745D6E02602995658B9AE4FA28DCAF86A  src/Shoreline.Api/Features/NotificationReadService.Cards.cs
046A560D34B83CB842254BE074716655393F03CB31D1AF66243F6BE4BF643866  src/Shoreline.Api/Features/NotificationEndpoints.cs
BA2C2B488018BD7A13F93A1FFED8F7BB7E719E20C35949CFDF6397EC073049E4  src/Shoreline.Api/Infrastructure/NotificationErrors.cs
D6F842CC893681A451EA20CAF3013C25243730E6D47C7ACC0E8EEBC49017B77E  src/Shoreline.Api/Infrastructure/NotificationRegistration.cs
0BC4635BEEAFDCB8A0EEF3CD578B7118D783016EF7D70E6455A45A527E46D43E  tests/Shoreline.Tests/NotificationTests.cs
```

Shared N1 tests: `NotificationTests.cs` (21 direct), `NotificationGoldenTests.cs`
(5 Python goldens), `NotificationHttpTests.cs` (3 HTTP). Publish, dispatch,
delivery worker, moderation, manage/revoke, and notification-sync remain N2.
