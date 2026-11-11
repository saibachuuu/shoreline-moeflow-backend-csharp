# Production readiness progress — 2026-11-10 evening

**Not a release approval. The C# backend must not replace production Python yet.**
This record supplements the earlier handoff; snapshot results do not imply full-suite,
load, migration, or production acceptance. No README production-status wording was changed.

## Merged on main

- `2aaaa85` integrates two reviewed `team_member` indexes and a read-only duplicate
  diagnostic. `c9775d4` clarifies the third, conflicting index's synthetic test
  states. Python migration `m0006` and the model disagree about the search-index key;
  no automatic destructive resolution or migration acceptance is claimed.
- `4fd22bd` integrates user notification mail's channel-specific enablement gate.
  It does not implement log email, fix security-code mail or prove real SMTP/TLS
  delivery. On the merged main, `NotificationPublishTests|ProofreadFeedbackTests`
  passed **75/75**, 0 skipped, with isolated Mongo/S3Mock/Mailpit. TRX:
  `C:\Users\88888888\AppData\Local\Temp\opencode\mailgate-main-20261110.trx`,
  SHA-256 `E2871C02775C90F1DBC01C5D25BA467A898C4B2355EC438D95F7213D4354E501`.
- `ba56379` integrates ordinary project creation's LabelPlus parsing and existing
  import pipeline. The HTTP tests cover valid import, invalid input, permission and
  Python error precedence. On the merged main, `ProjectCreateLabelPlusHttpTests|
  ProjectTests|ProjectImportTests|TeamDissolutionTests` passed **61/61**, 0 skipped.
  TRX: `C:\Users\88888888\AppData\Local\Temp\opencode\labelplus-main-20261110.trx`,
  SHA-256 `12DF3A8074451506265F2A78F02EFDEC3BD92656E432DA3F2B7344506971F53C`.
- `22f63cf` integrates the image branch. Both referenced projects and lock files are
  copied, and SDK `10.0.403` and ASP.NET `10.0.8` are pinned by digest. Two clean
  Linux/amd64 builds of the branch Dockerfile succeeded; the tested branch image
  `sha256:b4ecaccdc482bc4ac35da2e9208589416e8279f0093126132fc6188848068e58`
  ran as UID 1654, loaded and exercised native Imaging, wrote disposable local
  storage, and returned 200 from live and ready endpoints. A further cold build/smoke
  against main content at `bc9557f` (no Dockerfile changes since) produced the
  identical ImageID twice: `sha256:7a1936a2db68f84572a0ff41ce701f162b9f4873ef9361234faed19ebedd662e`.
  Runtime UID was 1654, Imaging processed a sample, Local storage was writable, and
  live/ready both returned 200. This proves reproducibility only for the same source,
  builder and amd64 platform. The smoke removed only its own resources and did not
  alter the running user stack. No Docker HEALTHCHECK is included. See
  `docs/image-build-readiness.md` for commands and boundaries.

The main `dotnet build Shoreline.sln --nologo` after these merges reported **0
warnings / 0 errors**. Focused tests above are separate executions, not a full suite.
Their local disposable Compose stack was stopped, not deleted. Mongo/Mailpit exited
0; S3Mock exited 143 on stop; all reported `OOMKilled=false`.

## Unmerged Bootstrap integration

`fix/readiness-bootstrap-wiring-20261110` contains `fix/readiness-bootstrap-20261110`
and an explicit `Program.cs` startup connection. On a fresh isolated Mongo, the
actual wired `WebApplicationFactory<Program>` startup, login, creator membership,
role, project set and `ajt` test together with recovery cases passed **47/47**.
TRX: `C:\Users\88888888\AppData\Local\Temp\opencode\bootstrap-wired2-20261110.trx`,
SHA-256 `5A2A4978CC7B57E47B5F4587152C19AB4F3A0F60427189F296C950DB2062B4C0`.
The branch also tested an actual dropped Mongo TCP CAS reply; this is limited to
that operation, not an exactly-once or cross-document transaction guarantee.

**Do not merge yet:** follow-up fixture branch `test/readiness-bootstrap-fixtures-20261110`
commit `d170969` supplies a disposable initial password and adapts existing-admin/site
fixtures without skipping bootstrap. Its `BootstrapTests|BootstrapStartupHttpTests|
UserTests|AdminUserTests` run passed **54/54**; `UserTests|AdminUserTests` passed **7/7**.
However, `RuntimeCompatibilityHttpTests` remains **6/12**: the differential harness
replaces the bootstrap-seeded users/team after bootstrap has persisted the admin ID,
so later startup rejects the missing journal-bound admin. This needs a real fixture
design fix and review before merge. No full suite was run on that branch. Its isolated
Compose resources are stopped, volumes retained. The initial wired HTTP test also
exceeded Mongo's 63-character DB-name limit and was corrected before the 47/47 run.

## Differential and remaining blockers

- An unmerged true Flask/C# differential branch has 16 requests including successful
  `partner_search` writes/reads and a real Python token. Its latest rerun has 17
  business differences, 3 expected-framework differences and 39 wire-format
  differences; the test correctly **fails**. It compares same raw BSON seeds and
  database before/after digests. Profile writes add BSON `csharp_profile_revision`,
  and CORS/HTTP headers differ.
  The profile revision protects C# optimistic concurrency and must not simply be
  removed to match a digest. An isolated real Flask probe confirmed Python
  MongoEngine raises `FieldDoesNotExist` when loading a user with
  `csharp_profile_revision`. CORS synthetic defaults were corrected against Python
  config and the real difference rerun. The corrected actual Flask responses include
  expose headers without Origin and wildcard CORS with Origin, while C# uses an
  explicit localhost default; policy differences require an owner decision. The
  differential also finds preflight 200 vs 204, 405 body/Allow differences, and
  three requests without Flask's no-Origin expose header. The two set-derived method
  headers are compared as token sets because Python's set iteration order varies;
  missing/extra methods still fail. No business mismatch is classified as accepted.
- Security-code delivery and rollback interoperability remain a hard blocker:
  C# stores the verification record and durable mail job atomically in one
  `csharp_job` document, while Python `VCode` reads only `v_code`. Naive dual-write
  would create a cross-document crash/unknown-commit window; standalone Mongo cannot
  make those writes transactional. Do not claim exactly-once or enable rollback
  without a reviewed stable-ID recovery protocol (or coordinated Python support).
  The separate email branch also has unresolved template/site URL, SMTP acceptance,
  TLS and retry classification differences.
- The third legacy index,
  historical migrations, real R2, retry/lease/crash coverage for all jobs, optional
  routes, MIT consumers, SSRF/trusted proxies and resource/security controls remain
  unaccepted. No product exception is assumed.
- MIT is now fail closed rather than silently queueing dead work: `eb13ab4` (tests)
  and `c5b9218` (guard) make `src/Shoreline.Api/Program.cs:181-200` map `/v1/mit`
  only when `mit_detect_text`, `mit_ocr` and `mit_translate` all have a registered
  `IJobHandler`; otherwise the routes stay unmapped (404) and one warning names the
  missing kinds. `dotnet test ... --filter "FullyQualifiedName~MitHttpTests"` passed
  **9/9**, 0 skipped. This removes the silent-acceptance defect only: MIT
  functionality is still unimplemented, and real parity needs an in-process handler
  or a supported external `manga-image-translator` Celery (`mit` queue) bridge,
  neither of which exists.
- C# full, safely isolated Python full, complete real differential, test-server
  deployment, migration/backup/rollback, load and soak remain unverified. An earlier
  full-test attempt suffered Mongo OOM; it is not a pass. Do not read production
  credentials or treat S3Mock as evidence of real R2 authorization.

## Python suite isolation preparation

Unmerged branch `test/readiness-python-isolation-20261110` commit `868b5c1` adds a
safe runner, tests and documentation; it did not alter the Python source repository
or read `.env*`/credentials. Runner self-tests passed **68/68**, ruff passed, and its
Release `dotnet build` passed 0/0. Pytest itself produced no valid result: the first
attempt stopped during collection because a read-only snapshot blocked `.mo` output;
the second produced **160 errors / 0 passed / 0 failed / 0 skipped** due to a harness
`load_dotenv` stub rejecting its own key. That stub was fixed but pytest was not
rerun. Its owned Mongo was removed and port 27321 is free; snapshot/logs remain in
WSL. Python full-suite status is still **unverified**.

## C# full-suite attempt (incomplete)

At 2026-11-10 22:51 local, `dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj
--no-build --nologo` was started with isolated Compose ports 27341/29341/21341/28341/
25341. Mongo had a 1 GiB cgroup limit and 0.35 GiB WiredTiger cache. The owned Mongo
container exited after 14 seconds with code 137 and `OOMKilled=true`; tests then
received connection-refused/server-selection failures. The run reached the 1800 s
command timeout and exited 1. Console capture contains 306 `[FAIL]` markers, but no
complete summary or TRX was produced, so there is no authoritative total/pass/fail/
skip count. This is an infrastructure-invalid full run, not a result to call green
or a reliable count of product failures. Compose stopped only this run's S3/Mailpit
and confirmed the test Mongo exited; the running user stack remained up. Do not
retry before power-off. A later run needs a larger dedicated Mongo memory budget and
must preserve full TRX output.

## Ziteng SSRF guard (2026-11-11, this worktree)

- Phase 1 `ac71020` extracted the archive-import connection-time DNS/IP guard
  into `src/Shoreline.Api/Infrastructure/PublicNetworkGuard.cs` and migrated
  `ArchiveImportTransport` to it with identical semantics (archive host label
  preserved); added `PublicNetworkGuardTests` (29 cases, Mongo-free).
- Phase 2 wired the Ziteng typed-client handler to the same guard
  (`ZitengPartnerRegistration`, redirects stay disabled, HTTPS config
  validation unchanged) and preserved the transport `HttpRequestException` as
  `InnerException` of `ZitengPartnerApiException("请求失败: 网络错误")`
  (user-visible message unchanged; service persists/logs only `Message`).
  Added `ZitengPartnerSsrfTests` (5 cases, Mongo-free): the real
  DI-registered handler rejects `127.0.0.1`/`10/8`/`192.168/16`/`::1` HTTPS
  targets with the guard message before any socket opens, so the Bearer key
  can never be sent there; no test contacts a real external host.
- `dotnet build Shoreline.sln --nologo`: 0 warnings / 0 errors.
  Focused `dotnet test --filter "FullyQualifiedName~ZitengPartnerSsrfTests|
  FullyQualifiedName~PublicNetworkGuardTests"`: **34/34 passed**.
  Checklist SSRF row moved to 部分验证 with residual limits (other outbound
  paths still need source + HTTP/connection review). Full suite was not run
  here; no full-suite claim is made. `git diff --check` clean.
