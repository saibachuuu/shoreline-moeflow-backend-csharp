# Ziteng Partner compatibility notes

Status (2026-11-09): module implemented in an isolated copy; focused tests 24/24 pass. The shared
`Program.cs` wiring (`AddZitengPartner` / `MapZitengPartner` / `InitializeZitengPartnerAsync`) is owned by
the program-wiring task. This document records the compatibility contract, not a deployment approval.

Python source of truth (read-only): `app/modules/ziteng_partner/{api,service,tasks,models,title,matching,client,config}.py`.

## 1. Files

`src/Shoreline.Api/Modules/ZitengPartner/`

| File | Role |
|------|------|
| `ZitengPartnerEndpoints.cs` | Three routes, auth, 403 mapping, public view |
| `ZitengPartnerService.cs` | Enqueue, worker body, project-created notifier |
| `ZitengPartnerStore.cs` | `ziteng_work` / `ziteng_query` / `ziteng_check` persistence and fencing |
| `ZitengPartnerClient.cs` | Partner HTTP client, paging, rate gate, error handling |
| `ZitengPartnerApiGate.cs` | Mongo CAS-based cross-process request spacing |
| `ZitengPartnerMatching.cs` | Local exact/contains scoring and suspect ordering |
| `ZitengPartnerTitle.cs` | Project-name -> search keyword extraction |
| `ZitengPartnerText.cs` / `ZitengPartnerSettings.cs` | Searchability and legacy configuration |
| `ZitengPartnerRegistration.cs` | DI, indexes, job handler |

`Jobs/ZitengCheckJobHandler.cs` is the `csharp_job` handler for kind `ziteng_check`.

## 2. Routes and authorization

| Method | Path | Python |
|--------|------|--------|
| GET | `/v1/projects/{projectId}/ziteng-check` | `api.py:27-66` |
| POST | `/v1/projects/{projectId}/ziteng-check` | `api.py:68-93` |
| GET | `/v1/ziteng-partner/config` | `api.py:96-113` |
| GET | `/v1/config/ziteng-partner` (alias) | C# migration alias, same payload |

All routes require a valid token. The project GET/POST require `project:ACCESS`; denial is
`NoPermissionError` code 103 with HTTP 403 (the message is Python's `您没有此项目的访问权限`). The shared
C# `ApiError.Forbidden()` defaults to 400, so the module states 403 explicitly. A missing project remains
the legacy `ProjectNotExistError` code 4001 / HTTP 400.

Routes are always mapped, even with an empty API key. `GET /v1/ziteng-partner/config` then reports
`enabled: false`; the project routes still serve reads and explicit checks.

## 3. Configuration

`ZitengPartner` section or the legacy environment variables (legacy variables win):

`ApiUrl` / `ZITENG_PARTNER_API_URL`, `ApiKey` / `ZITENG_PARTNER_API_KEY`,
`IncludeInactive`, `CacheTtl`, `MaxPages`, `Timeout`, `MinInterval`, `MaxResults`, `TaskTimeout`.

Defaults match the Python module: API URL `https://partner-api.ziteng.org/partner-api/v1/works/search`,
cache TTL 604800 s, max pages 3, timeout 20 s, min interval 1.1 s, max results 5, task timeout 180 s.
`Enabled` is exactly "non-empty API key". The key is never included in `ToString()`/`Redact()` output.

`ApiUrl` must be an absolute `https://` URL without user info or fragment; an unset or empty value
falls back to the default above. Anything else (for example a bare `host/path`, `http://`, or
`https://user:pass@host/...`) makes startup fail with `InvalidOperationException` instead of being
silently rewritten. This is fail-closed hardening: `UriBuilder` would otherwise invent `http://` for a
scheme-less value and send `Authorization: Bearer <key>` in clear text, while Python's `requests`
raises `MissingSchema` for the same input.

## 4. Persistence and generation fencing

- `ziteng_work`: upsert by unique `work_id`; no TTL index (freshness is logical).
- `ziteng_query`: unique `keyword`, cached work order, `total`, `truncated`, `queried_at`; no TTL.
- `ziteng_check`: unique `project_id`; public result fields plus C#-private `generation`,
  `lease_owner`, `lease_until` and `created_at`.

`POST` calls `EnsureCheckAsync`, then `ResetForRetryAsync` atomically increments `generation` and resets
status/verdict/error/lease, then enqueues a job carrying that generation. `ClaimAsync`, `CompleteAsync`
and `FailAsync` all filter on `project_id + generation + status=1 + lease_owner`; a job from an older
generation can neither claim nor complete/fail a newer retry. A running row whose lease expired may be
reclaimed. This is the generation/lease fence; it is unrelated to the Archive Import label-revision TODO.

The check is a three-state result: `clear` (nothing found in the searched scope, not proof of absence),
`suspected`, `failed` (a failure is never downgraded to `clear`). HTTP exposes only
`project_id, keyword, status, verdict, suspects, suspect_count, error, updated_at`; the private fields
above are intentionally omitted.

A disabled key produces an explicit failure: the enqueued check ends as status 3 / verdict `failed`
with an error containing `API 密钥`; the routes do not disappear and do not return `clear`.

## 5. Title / matching golden vectors

`tests/Shoreline.Tests/Fixtures/ZitengPartnerGolden.json` is the immutable oracle. Its embedded
provenance records:

- generator: AST-selected pure functions from the Python source, no `app`/config/conftest import;
- Python 3.10.11;
- `app/modules/ziteng_partner/title.py` SHA-256
  `3fad076ff107e20c5cb43a1992baca0ed3a866166ac2847d9c315a9c2c9f7956`
  (`extract_title`, `is_searchable`);
- `app/modules/ziteng_partner/matching.py` SHA-256
  `91910037d880f5f3b32ec8229225c6370c09ffb97aabc7fa4052982015b53de9`
  (`_normalize`, `_core`, `score`, `find_suspects`);
- fixture SHA-256 `e633bdeb3a87c21a2c52388d1376ed1ca2345dfa60b87a1275879cfd284a2f02`.

Case counts: 15 title cases, 7 score cases, 1 find-suspects ordering case. The tests must never be
edited to fit the C# implementation; regenerate the fixture from Python if the reference changes.

Note: the current local Python checkout computes SHA-256 `9fdbfcc4…` (title.py) and `4431866f…`
(matching.py) after later ruff-format/import commits. The fixture provenance above still names the SHAs
recorded at golden-generation time, and all golden values pass. Re-verify source equivalence before
regenerating; do not silently update the recorded SHAs.

## 6. Cache, paging and rate limiting

The client preserves partner page order, stops on the configured page cap, and marks truncated results.
`ziteng_query` keeps the cached order and skips works that disappeared. The API gate stores the next
permitted slot in `ziteng_api_gate` (`_id=global`, `next_at`) with a Mongo compare-and-swap, so separate
processes cannot both take the same slot; it uses no TTL index or delete/cleanup race.

## 7. Project-created hook status

`ZitengPartnerProjectCreatedNotifier.TryNotifyAsync(projectId, ct)` exists and enqueues a check without
calling the partner API and without throwing into project creation. **It is not wired**: the shared
`ProjectService` does not invoke it, so creating a project currently does not auto-enqueue a check.
Explicit `POST .../ziteng-check` is the only trigger. When wiring it, call the notifier from the core
project-created path rather than coupling an optional module name into `ProjectService`.

## 8. Verification

In the isolated copy (`main` = `80b3893` plus this task's files):

- build: 0 warning / 0 error;
- `ZitengPartnerTests`: 24/24 pass (direct, golden, fake-client, and HTTP cases);
- combined focused with `ArchiveImportTests`: 56/56 pass.

Not covered: a real partner API request, production credentials, distributed kill/restart of a check
worker, and the unwired project-created hook.
